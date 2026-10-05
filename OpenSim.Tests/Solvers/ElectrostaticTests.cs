using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Feature 11: the electrostatic solve. The references are closed forms the structured mesh
/// reproduces exactly (uniform fields), so the gates are tight.
/// </summary>
public class ElectrostaticTests
{
    private readonly ITestOutputHelper _out;
    public ElectrostaticTests(ITestOutputHelper output) => _out = output;

    private const double Eps0 = 8.8541878128e-12;

    private static Material Dielectric(string name, double er, double? strength = null) => new()
    {
        Name = name, YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1000,
        ElectricalConductivity = 1e-14, RelativePermittivity = er, DielectricStrength = strength
    };

    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960, ElectricalConductivity = 5.96e7
    };

    private static VoltagePotential Volts(string name, int face, double volts) =>
        new() { Name = name, FaceIds = new[] { face }, Volts = volts };

    /// <summary>A box mesh with the region of each element set by a rule on its centroid.</summary>
    private static FeMesh Regions(FeMesh mesh, Func<Vector3D, int> region)
    {
        var ids = new int[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            var c = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
            ids[e] = region(c);
        }
        return new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, ids);
    }

    [Fact]
    public void ParallelPlate_CapacitanceFieldAndEnergy_AreExact()
    {
        const double a = 20e-3, b = 10e-3, d = 1e-3, er = 4.4, v = 100;
        var mesh = StructuredBoxMesh.Build(0, a, 0, b, 0, d, 8, 4, 4);
        var input = new SolveInput
        {
            Mesh = mesh, Material = Dielectric("FR4", er, strength: 20e6),
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("bottom", StructuredBoxMesh.FaceZMin, 0), Volts("top", StructuredBoxMesh.FaceZMax, v)
            }
        };
        var output = new ElectrostaticSolver().Solve(input);
        _out.WriteLine(string.Join("\n", output.Log));

        double exact = Eps0 * er * a * b / d;
        Assert.Equal(exact, output.Summary!["Capacitance (F)"], exact * 1e-9);
        Assert.Equal(0.5 * exact * v * v, output.Summary["Energy (J)"], 0.5 * exact * v * v * 1e-9);
        Assert.Equal(v / d, output.Summary["Max |E| (V/m)"], v / d * 1e-9);
        Assert.Equal(v / d / 20e6, output.Summary["Max dielectric stress (fraction of breakdown)"], 1e-9);
        Assert.Equal(exact * v, output.Summary["Charge 'top' (C)"], exact * v * 1e-9);
        Assert.Equal(-exact * v, output.Summary["Charge 'bottom' (C)"], exact * v * 1e-9);

        var e = (NodalScalarField)output.Fields.Single(f => f.Name == ElectrostaticSolver.FieldMagnitudeName);
        for (int i = 0; i < e.Count; i++) Assert.Equal(v / d, e.GetScalar(i), v / d * 1e-9);
        var stress = output.Fields.Single(f => f.Name == ElectrostaticSolver.StressFieldName);
        Assert.Equal(mesh.ElementCount, stress.Count);
    }

    [Fact]
    public void TwoDielectrics_SeriesCapacitanceAndFieldRatio_AreExact()
    {
        // ε1 = 2 over 0 < z < d1, ε2 = 8 over d1 < z < d: E1/E2 = ε2/ε1 and C = A/(d1/ε1 + d2/ε2).
        const double a = 10e-3, d1 = 1e-3, d2 = 2e-3, er1 = 2, er2 = 8, v = 10;
        var mesh = Regions(StructuredBoxMesh.Build(0, a, 0, a, 0, d1 + d2, 4, 4, 6), c => c.Z < d1 ? 0 : 1);
        var low = Dielectric("low", er1); var high = Dielectric("high", er2);
        var input = new SolveInput
        {
            Mesh = mesh, Material = low,
            RegionMaterials = new Dictionary<int, Material> { [0] = low, [1] = high },
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("bottom", StructuredBoxMesh.FaceZMin, 0), Volts("top", StructuredBoxMesh.FaceZMax, v)
            }
        };
        var output = new ElectrostaticSolver().Solve(input);
        _out.WriteLine(string.Join("\n", output.Log));

        double exact = Eps0 * a * a / (d1 / er1 + d2 / er2);
        Assert.Equal(exact, output.Summary!["Capacitance (F)"], exact * 1e-9);
        double e1 = v / (d1 + d2 * er1 / er2), e2 = e1 * er1 / er2;
        var field = (ElementScalarField)output.Fields.Single(f => f.Name == "Field magnitude (element)");
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double expected = mesh.RegionOf(e) == 0 ? e1 : e2;
            Assert.Equal(expected, field.GetScalar(e), expected * 1e-8);
        }
        Assert.Equal(e1, output.Summary["Max |E| (V/m)"], e1 * 1e-9);
        // The field is discontinuous at the interface; the nodal value there is the larger side.
        var nodal = output.Fields.Single(f => f.Name == ElectrostaticSolver.FieldMagnitudeName);
        for (int n = 0; n < mesh.NodeCount; n++)
            if (Math.Abs(mesh.Nodes[n].Z - d1) < 1e-12) Assert.Equal(e1, nodal.GetScalar(n), e1 * 1e-8);
    }

    [Fact]
    public void ConductorSlabBetweenPlates_DrivenAndFloating()
    {
        // Plates at z = 0 and z = d; a copper slab spanning the cross-section over d1 < z < d1 + t.
        const double a = 10e-3, d1 = 1e-3, t = 0.5e-3, d2 = 2e-3, er = 3;
        double d = d1 + t + d2;
        var mesh = Regions(StructuredBoxMesh.Build(0, a, 0, a, 0, d, 3, 3, 7),
            c => c.Z > d1 && c.Z < d1 + t ? 1 : 0);
        var fr4 = Dielectric("FR4", er);
        var regions = new Dictionary<int, Material> { [0] = fr4, [1] = Copper };
        double c1 = Eps0 * er * a * a / d1, c2 = Eps0 * er * a * a / d2;

        // No condition reaches the slab, so it is an equipotential of zero net charge
        // and the plates see c1 and c2 in series.
        var floating = new SolveInput
        {
            Mesh = mesh, Material = fr4, RegionMaterials = regions,
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("bottom", StructuredBoxMesh.FaceZMin, 0), Volts("top", StructuredBoxMesh.FaceZMax, 1)
            }
        };
        var output = new ElectrostaticSolver().Solve(floating);
        _out.WriteLine(string.Join("\n", output.Log));
        double series = c1 * c2 / (c1 + c2);
        double c = output.Summary!["Capacitance (F)"];
        _out.WriteLine($"floating slab: C = {c:g6} F against {series:g6} F (ratio {c / series:f6})");
        Assert.InRange(c / series, 1 - 3e-4, 1 + 1e-9);          // the slab is uniform to 1e-4

        // The slab's potential is the divider's: V·c1/(c1 + c2) above the bottom plate.
        var phi = output.Fields.Single(f => f.Name == ElectrostaticSolver.PotentialFieldName);
        double expected = c1 / (c1 + c2);
        for (int n = 0; n < mesh.NodeCount; n++)
            if (mesh.Nodes[n].Z > d1 + 1e-9 && mesh.Nodes[n].Z < d1 + t - 1e-9)
                Assert.Equal(expected, phi.GetScalar(n), 3e-4);
        Assert.Contains(output.Log, l => l.Contains("floating piece"));
    }

    [Fact]
    public void ThreeElectrodes_MatrixIsSymmetric_RowsSumToZero_AndShieldingIsExact()
    {
        // A slab electrode spanning the cross-section between the plates screens them from each other.
        const double a = 10e-3, d1 = 1e-3, t = 0.5e-3, d2 = 2e-3, er = 3;
        double d = d1 + t + d2;
        var mesh = Regions(StructuredBoxMesh.Build(0, a, 0, a, 0, d, 3, 3, 7),
            c => c.Z > d1 && c.Z < d1 + t ? 1 : 0);
        // The structured mesh tags only the six box faces, so the slab's side-wall triangles
        // are retagged as face 10 for the condition that drives it; the electrode then takes
        // the whole conductor piece those faces touch.
        var skin = mesh.BoundaryTriangles.Select(tr =>
        {
            bool inSlab = new[] { tr.A, tr.B, tr.C }.All(n => mesh.Nodes[n].Z > d1 - 1e-9 && mesh.Nodes[n].Z < d1 + t + 1e-9);
            bool side = tr.FaceId is StructuredBoxMesh.FaceXMin or StructuredBoxMesh.FaceXMax
                or StructuredBoxMesh.FaceYMin or StructuredBoxMesh.FaceYMax;
            return inSlab && side ? new BoundaryTriangle(tr.A, tr.B, tr.C, 10) : tr;
        }).ToList();
        mesh = new FeMesh(mesh.Nodes, mesh.Elements, skin, mesh.ElementRegionIds);

        var fr4 = Dielectric("FR4", er);
        var input = new SolveInput
        {
            Mesh = mesh, Material = fr4,
            RegionMaterials = new Dictionary<int, Material> { [0] = fr4, [1] = Copper },
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("bottom", StructuredBoxMesh.FaceZMin, 0), Volts("top", StructuredBoxMesh.FaceZMax, 5),
                Volts("slab", 10, 2)
            }
        };
        var matrix = ElectrostaticSolver.CapacitanceMatrixOf(input);
        _out.WriteLine(string.Join("\n", matrix.Describe()));
        double c1 = Eps0 * er * a * a / d1, c2 = Eps0 * er * a * a / d2;
        Assert.Equal(3, matrix.Count);
        Assert.True(matrix.Asymmetry() < 1e-10, $"asymmetry {matrix.Asymmetry():g3}");
        for (int i = 0; i < 3; i++) Assert.Equal(0, matrix.ToReference(i), c1 * 1e-9);
        int bottom = matrix.Electrodes.ToList().IndexOf("bottom"), top = matrix.Electrodes.ToList().IndexOf("top"),
            slab = matrix.Electrodes.ToList().IndexOf("slab");
        Assert.Equal(c1, matrix.Mutual(bottom, slab), c1 * 1e-9);
        Assert.Equal(c2, matrix.Mutual(top, slab), c2 * 1e-9);
        Assert.Equal(0, matrix.Mutual(bottom, top), c1 * 1e-9);                       // fully screened
        Assert.Equal(c1 * c2 / (c1 + c2), matrix.Between(bottom, top), c1 * 1e-9);    // slab floating

        var output = new ElectrostaticSolver().Solve(input);
        _out.WriteLine(string.Join("\n", output.Log));
        // Q_slab = c1·(2 − 0) + c2·(2 − 5)
        Assert.Equal(c1 * 2 - c2 * 3, output.Summary!["Charge 'slab' (C)"], c1 * 1e-9);
        var volts = matrix.Electrodes.Select(name => name == "bottom" ? 0.0 : name == "top" ? 5.0 : 2.0).ToList();
        Assert.Equal(matrix.Energy(volts), output.Summary["Energy (J)"], c1 * 25 * 1e-9);
        var e = (ElementScalarField)output.Fields.Single(f => f.Name == "Field magnitude (element)");
        for (int el = 0; el < mesh.ElementCount; el++)
        {
            double z = mesh.Elements[el] is var q ? (mesh.Nodes[q.N0].Z + mesh.Nodes[q.N1].Z + mesh.Nodes[q.N2].Z + mesh.Nodes[q.N3].Z) / 4 : 0;
            double expected = z < d1 ? 2 / d1 : z > d1 + t ? 3 / d2 : 0;
            Assert.Equal(expected, e.GetScalar(el), 1e-6 * Math.Max(expected, 1));
        }
    }

    [Fact]
    public void Validation_RefusesWhatDoesNotApply()
    {
        var mesh = StructuredBoxMesh.Build(0, 1e-3, 0, 1e-3, 0, 1e-3, 2, 2, 2);
        var solver = new ElectrostaticSolver();
        var noPermittivity = new Material { Name = "glass?", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1 };
        var plates = new BoundaryCondition[]
        {
            Volts("a", StructuredBoxMesh.FaceZMin, 0), Volts("b", StructuredBoxMesh.FaceZMax, 1)
        };
        var ex = Assert.Throws<InvalidOperationException>(() => solver.Validate(new SolveInput
            { Mesh = mesh, Material = noPermittivity, BoundaryConditions = plates }));
        Assert.Contains("permittivity", ex.Message);

        ex = Assert.Throws<InvalidOperationException>(() => solver.Validate(new SolveInput
        {
            Mesh = mesh, Material = Dielectric("d", 2),
            BoundaryConditions = new BoundaryCondition[]
            {
                plates[0], new CurrentFlow { Name = "i", FaceIds = new[] { StructuredBoxMesh.FaceZMax }, TotalCurrent = 1 }
            }
        }));
        Assert.Contains("current", ex.Message, StringComparison.OrdinalIgnoreCase);

        // All copper: nothing holds a field (both faces at one potential, so they are one electrode).
        ex = Assert.Throws<InvalidOperationException>(() => solver.Solve(new SolveInput
        {
            Mesh = mesh, Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("a", StructuredBoxMesh.FaceZMin, 0), Volts("b", StructuredBoxMesh.FaceZMax, 0)
            }
        }));
        Assert.Contains("no dielectric", ex.Message);

        // Two voltages on one conductor piece disagree.
        var half = Regions(StructuredBoxMesh.Build(0, 1e-3, 0, 1e-3, 0, 2e-3, 2, 2, 4), c => c.Z < 1e-3 ? 1 : 0);
        ex = Assert.Throws<InvalidOperationException>(() => solver.Validate(new SolveInput
        {
            Mesh = half, Material = Dielectric("d", 2),
            RegionMaterials = new Dictionary<int, Material> { [0] = Dielectric("d", 2), [1] = Copper },
            BoundaryConditions = new BoundaryCondition[]
            {
                Volts("a", StructuredBoxMesh.FaceZMin, 0), Volts("a2", StructuredBoxMesh.FaceXMin, 1),
                Volts("b", StructuredBoxMesh.FaceZMax, 1)
            }
        }));
        Assert.Contains("same conductor piece", ex.Message);
    }

    [Fact]
    public void CapacitanceMatrix_FormattingAndNetworkValues()
    {
        var m = new CapacitanceMatrix(new[] { "A", "B", "C" }, new[,]
        {
            { 3e-12, -1e-12, -2e-12 },
            { -1e-12, 4e-12, -3e-12 },
            { -2e-12, -3e-12, 5e-12 }
        });
        Assert.Equal(1e-12, m.Mutual(0, 1), 1e-24);
        Assert.Equal(0, m.ToReference(0), 1e-24);
        // A–C with B floating: C_ac + series(C_ab, C_bc) = 2 + 1·3/4 = 2.75 pF.
        Assert.Equal(2.75e-12, m.Between(0, 2), 1e-22);
        Assert.Equal(0, m.Asymmetry());
        var q = m.Charges(new[] { 1.0, 0, 0 });
        Assert.Equal(3e-12, q[0], 1e-24);
        Assert.Equal("2.75 pF", CapacitanceMatrix.FormatCapacitance(2.75e-12));
        Assert.Equal("1.5 nF", CapacitanceMatrix.FormatCapacitance(1.5e-9));
        Assert.Contains("C(A, C)", string.Join("\n", m.Describe()));
    }
}
