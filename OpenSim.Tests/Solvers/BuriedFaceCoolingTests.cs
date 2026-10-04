using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Fix 7 gates (TH-01): skin inside a body-to-body contact is not cooled by the
/// environment. The oracle is independent of the film code: a block cut into two
/// contacting halves must behave like the uncut block.
/// </summary>
public class BuriedFaceCoolingTests
{
    private const double Ambient = 300.0;

    private static Material Metal => new()
    {
        Name = "Metal",
        Density = 1000,
        YoungsModulus = 1e9,
        PoissonRatio = 0.3,
        ThermalConductivity = 400,
        SpecificHeat = 500,
        Emissivity = 0.9
    };

    private static double MeanRise(List<Body> bodies, EnvironmentSettings environment,
        out IReadOnlyList<ContactInterface> contacts)
    {
        var assembled = FeMeshAssembler.Assemble(bodies);
        contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
            new ContactDetectionSettings { DefaultConductance = 1e6 });
        var input = new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = bodies[0].Material!,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ElementHeatSource = FeMeshAssembler.BuildElementHeatSource(assembled, bodies),
            ThermalContacts = contacts,
            Environment = environment
        };
        var output = new HeatConductionSolver().Solve(input);
        var field = (OpenSim.Core.Results.NodalScalarField)output.Fields.First(f => f.Name == "Temperature");
        return field.Values.Average() - Ambient;
    }

    /// <summary>Insulates the four edge faces (a zero-power flux claims them), so only the
    /// broad faces meet the environment and cutting the plate changes nothing but the joint.</summary>
    private static List<Body> EdgesInsulated(List<Body> bodies)
    {
        foreach (var body in bodies)
            body.BoundaryConditions.Add(new HeatFlux
            {
                Name = "Insulated edges",
                FaceIds = new[]
                {
                    StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceXMax,
                    StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceYMax
                },
                TotalPower = 0
            });
        return bodies;
    }

    private static List<Body> Stacked(double power)
    {
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Lower", 0, 0.1, 0, 0.1, 0, 0.002, 8, 8, 1, Metal),
            StructuredBoxMesh.Box("Upper", 0, 0.1, 0, 0.1, 0.002, 0.004, 8, 8, 1, Metal)
        };
        bodies[0].HeatSourcePower = power;
        return bodies;
    }

    private static List<Body> Single(double power)
    {
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Whole", 0, 0.1, 0, 0.1, 0, 0.004, 8, 8, 2, Metal)
        };
        bodies[0].HeatSourcePower = power;
        return bodies;
    }

    [Fact]
    public void StackedPlates_RadiatingInVacuum_MatchTheUncutPlate()
    {
        // Radiation only: the film has no length scale, so cutting the plate changes
        // nothing but the buried joint. Before the fix the joint's two faces radiated too
        // (about twice the area) and the rise was roughly 45 % low.
        var vacuum = new EnvironmentSettings
        {
            Medium = MediumKind.Vacuum, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        double stacked = MeanRise(Stacked(2.0), vacuum, out var contacts);
        double single = MeanRise(Single(2.0), vacuum, out _);
        Assert.NotEmpty(contacts);
        Assert.InRange(stacked / single, 0.99, 1.01);
    }

    [Fact]
    public void StackedPlates_InStillAir_MatchTheUncutPlate()
    {
        // With convection the cut would also split each 4 mm edge face into two 2 mm
        // panels with their own length scale (a different edge coefficient that has nothing
        // to do with the joint), so the edges are insulated in both models: the only
        // difference left is the buried pair of faces.
        var air = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        double stacked = MeanRise(EdgesInsulated(Stacked(2.0)), air, out _);
        double single = MeanRise(EdgesInsulated(Single(2.0)), air, out _);
        Assert.InRange(stacked / single, 0.99, 1.01);
    }

    [Fact]
    public void BlockOnAPlate_OnlyTheFootprintIsExcluded()
    {
        // A 20 × 20 mm block on a 60 × 60 mm plate; the footprint lies on the plate's grid
        // lines, the block's own mesh does not match the plate's. The plate's top face
        // keeps its area outside the footprint; the block's bottom face keeps none.
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Plate", 0, 0.06, 0, 0.06, 0, 0.002, 6, 6, 1, Metal),
            StructuredBoxMesh.Box("Block", 0.02, 0.04, 0.02, 0.04, 0.002, 0.012, 3, 3, 2, Metal)
        };
        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        var mesh = assembled.Mesh;
        var exposed = ContactInterface.ExposedFractions(mesh.BoundaryTriangles.Count, contacts);

        double plateTop = 0, blockBottom = 0, everything = 0, all = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            double area = ScalarDiffusionAssembler.SurfaceArea(mesh, tri);
            all += area;
            everything += exposed[t] * area;
            bool lowerBody = tri.A < assembled.NodeBases[1];
            double z = (mesh.Nodes[tri.A].Z + mesh.Nodes[tri.B].Z + mesh.Nodes[tri.C].Z) / 3;
            if (Math.Abs(z - 0.002) > 1e-9) continue;
            if (lowerBody) plateTop += exposed[t] * area; else blockBottom += exposed[t] * area;
        }
        Assert.Equal(0.06 * 0.06 - 0.02 * 0.02, plateTop, 9);
        Assert.Equal(0.0, blockBottom, 12);
        Assert.Equal(all - 2 * 0.02 * 0.02, everything, 9);

        // And the environment model reports the same exposed area as its panel total.
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Metal,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts,
            Environment = new EnvironmentSettings { AmbientTemperature = Ambient }
        };
        var log = new List<string>();
        var model = EnvironmentBoundaryModel.Build(input, log)!;
        Assert.Equal(everything, model.Panels.Sum(p => p.Area), 9);
        Assert.Contains(log, line => line.Contains("inside body-to-body contacts"));
    }

    [Fact]
    public void NoContacts_NothingIsExcluded()
    {
        var exposed = ContactInterface.ExposedFractions(5, null);
        Assert.All(exposed, f => Assert.Equal(1.0, f));
    }
}
