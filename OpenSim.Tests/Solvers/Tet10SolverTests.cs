using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Correctness gate for the quadratic (TET10) elements: patch test validates shape
/// functions + quadrature + assembly at once; uniaxial tension validates the T6
/// consistent surface loads and mid-node support pinning through the full solver; and
/// the cantilever benchmark demonstrates the point of TET10 — the bending lock that
/// forced TET4's [0.40, 1.05] tolerance is gone.
/// </summary>
public class Tet10SolverTests
{
    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z),
            new MeshSettings { TargetEdgeLength = h, ElementOrder = ElementOrder.Quadratic });

    [Fact]
    public void PatchTest_ConstantStrainReproducedExactly()
    {
        var mesh = MeshBox(1, 1, 1, 0.35);
        Assert.True(mesh.IsQuadratic);
        var assembler = new Tet10Assembler(mesh, Steel);
        var stiffness = assembler.AssembleStiffness();

        double[,] a =
        {
            { 1.0e-4, 0.3e-4, 0.2e-4 },
            { 0.1e-4, -0.6e-4, 0.4e-4 },
            { 0.25e-4, 0.15e-4, 0.8e-4 }
        };
        var expected = new SymmetricTensor(
            a[0, 0], a[1, 1], a[2, 2],
            0.5 * (a[0, 1] + a[1, 0]),
            0.5 * (a[1, 2] + a[2, 1]),
            0.5 * (a[2, 0] + a[0, 2]));

        // Prescribe u = A·x on every boundary node INCLUDING the boundary mid-edge
        // nodes (mid positions are exact for a linear field, so the prescription is
        // exact too). Missing the mids here is exactly the bug class this test guards.
        var boundaryNodes = new HashSet<int>(
            mesh.GetFaceNodes(mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct()));
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        foreach (var t in mesh.BoundaryTriangles)
        {
            boundaryNodes.Add(edgeMid[Key(t.A, t.B)]);
            boundaryNodes.Add(edgeMid[Key(t.B, t.C)]);
            boundaryNodes.Add(edgeMid[Key(t.C, t.A)]);
        }
        Assert.True(mesh.NodeCount > boundaryNodes.Count, "Patch test needs interior nodes.");

        var prescribed = new Dictionary<int, double>();
        foreach (int n in boundaryNodes)
        {
            var p = mesh.Nodes[n];
            for (int axis = 0; axis < 3; axis++)
                prescribed[n * 3 + axis] = a[axis, 0] * p.X + a[axis, 1] * p.Y + a[axis, 2] * p.Z;
        }

        var result = ConstrainedSystemSolver.Solve(stiffness, new double[assembler.DofCount], prescribed,
            tolerance: 1e-13);

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = assembler.ElementStrain(e, result.Displacements);
            Assert.Equal(expected.XX, strain.XX, 9);
            Assert.Equal(expected.YY, strain.YY, 9);
            Assert.Equal(expected.ZZ, strain.ZZ, 9);
            Assert.Equal(expected.XY, strain.XY, 9);
            Assert.Equal(expected.YZ, strain.YZ, 9);
            Assert.Equal(expected.ZX, strain.ZX, 9);
        }
    }

    [Fact]
    public void UniaxialTension_MatchesExactSolution()
    {
        const double length = 0.1, width = 0.05, thick = 0.02;
        const double force = 1000.0;
        var material = Steel with { PoissonRatio = 0.0 };

        var mesh = MeshBox(length, width, thick, 0.012);
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Fixed end", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "Axial force", FaceIds = new[] { 1 }, TotalForce = new Vector3D(force, 0, 0) }
            }
        });

        double sigma = force / (width * thick);
        double tipExtension = sigma * length / material.YoungsModulus;

        // 2% tolerance — geometry (jitter/faceting) only, same class as the TET4 case;
        // a wrong corner/mid consistent-load split would miss by far more.
        var tipNodes = mesh.GetFaceNodes(new[] { 1 });
        var displacement = (NodalVectorField)output.Fields.Single(f => f.Name == "Displacement");
        double avgTip = tipNodes.Average(n => displacement.GetVector(n).X);
        Assert.Equal(tipExtension, avgTip, tipExtension * 2e-2);

        var stress = (ElementTensorField)output.Fields.Single(f => f.Name == "Stress tensor");
        double avgVm = Enumerable.Range(0, stress.Count).Average(i => stress.GetTensor(i).VonMises());
        Assert.Equal(sigma, avgVm, sigma * 2e-2);
    }

    [Fact]
    public void CantileverBeam_Tet10_TipDeflectionMatchesTimoshenko()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double force = 100.0;

        var mesh = MeshBox(length, width, thick, 0.004);
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "Tip load", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, 0, -force) }
            }
        });

        // Timoshenko: δ = FL³/3EI + FL/(κGA), κ = 5/6 for a rectangle.
        double inertia = width * Math.Pow(thick, 3) / 12.0;
        double g = Steel.YoungsModulus / (2 * (1 + Steel.PoissonRatio));
        double analytic = force * Math.Pow(length, 3) / (3 * Steel.YoungsModulus * inertia)
                        + force * length / (5.0 / 6.0 * g * width * thick);

        var tipNodes = mesh.GetFaceNodes(new[] { 1 });
        var displacement = (NodalVectorField)output.Fields.Single(f => f.Name == "Displacement");
        double tip = -tipNodes.Average(n => displacement.GetVector(n).Z);

        // Quadratic tets represent the linear bending-strain field exactly — the
        // shear locking behind TET4's [0.40, 1.05] band is gone. The residual band
        // covers Timoshenko-vs-3D model error and end effects (~1%) plus mesh/geometry
        // discretization at 2-3 elements through the thickness (~1-2%).
        Assert.InRange(tip / analytic, 0.92, 1.03);
    }

    [Fact]
    public void ScalarSolvers_RejectQuadraticMeshes_Loudly()
    {
        var mesh = MeshBox(0.05, 0.02, 0.01, 0.01);
        var copper = new Material
        {
            Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
            ElectricalConductivity = 5.96e7, ThermalConductivity = 400
        };
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "V", FaceIds = new[] { 0 }, Volts = 1 }
            }
        };

        var e1 = Assert.Throws<InvalidOperationException>(() => new ElectricalConductionSolver().Validate(input));
        Assert.Contains("linear (TET4)", e1.Message);
        var e2 = Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Validate(input));
        Assert.Contains("linear (TET4)", e2.Message);
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
