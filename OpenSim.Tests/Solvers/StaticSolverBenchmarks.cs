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
/// Regression benchmarks against analytical reference solutions — the project's
/// "simulation correctness first" gate.
/// </summary>
public class StaticSolverBenchmarks
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
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    // ------------------------------------------------------------------
    // Patch test: with displacements u = A·x prescribed on every boundary
    // node, a consistent TET4 implementation reproduces the constant strain
    // state exactly (to solver tolerance) at arbitrary interior nodes.
    // ------------------------------------------------------------------
    [Fact]
    public void PatchTest_ConstantStrainReproducedExactly()
    {
        var mesh = MeshBox(1, 1, 1, 0.35);
        var assembler = new Tet4Assembler(mesh, Steel);
        var stiffness = assembler.AssembleStiffness();

        // Arbitrary small displacement gradient A (symmetric part = expected strain).
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

        var boundaryNodes = mesh.GetFaceNodes(mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct());
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

    // ------------------------------------------------------------------
    // Uniaxial tension with ν = 0: the exact solution is a linear
    // displacement field, which TET4 must reproduce to solver tolerance.
    // σ_xx = F/A everywhere, tip extension = σL/E.
    // ------------------------------------------------------------------
    [Fact]
    public void UniaxialTension_MatchesExactSolution()
    {
        const double length = 0.1, width = 0.05, thick = 0.02;
        const double force = 1000.0;
        var material = Steel with { PoissonRatio = 0.0 };

        var mesh = MeshBox(length, width, thick, 0.012);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Fixed end", FaceIds = new[] { 0 } },          // x-min
                new ForceLoad
                {
                    Name = "Axial force",
                    FaceIds = new[] { 1 },                                               // x-max
                    TotalForce = new Vector3D(force, 0, 0)
                }
            }
        };

        var output = new LinearStaticSolver().Solve(input);

        double area = width * thick;
        double sigma = force / area;                    // 1.0 MPa
        double tipExtension = sigma * length / material.YoungsModulus;

        // Tip displacement: average u_x over the loaded face nodes.
        var tipNodes = mesh.GetFaceNodes(new[] { 1 });
        var displacement = (NodalVectorField)output.Fields.Single(f => f.Name == "Displacement");
        // 2% tolerance. Before quality-driven refinement the mesh under-filled the box
        // by ~2% (faceting at low-stress edges/corners), which happened to cancel the
        // intrinsic conforming-P1 over-stiffness; the refined mesh is volumetrically
        // near-exact (~0.1%), exposing the genuine ~1% discretization bias. TET10
        // pins this back down — see the quadratic benchmarks.
        double avgTip = tipNodes.Average(n => displacement.GetVector(n).X);
        Assert.Equal(tipExtension, avgTip, tipExtension * 2e-2);

        // Von Mises must equal σ_xx everywhere for uniaxial stress.
        var stress = (ElementTensorField)output.Fields.Single(f => f.Name == "Stress tensor");
        double avgVm = Enumerable.Range(0, stress.Count).Average(i => stress.GetTensor(i).VonMises());
        Assert.Equal(sigma, avgVm, sigma * 1e-2);
    }

    // ------------------------------------------------------------------
    // Cantilever beam, tip force: compare against Euler–Bernoulli with shear
    // correction (Timoshenko). TET4 locks in bending, so the tolerance is wide
    // and one-sided; this stays as the TET4 regression gate. The quadratic
    // companion (Tet10SolverTests.CantileverBeam_Tet10_...) asserts [0.92, 1.03].
    // ------------------------------------------------------------------
    [Fact]
    public void CantileverBeam_TipDeflectionWithinTet4Bounds()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double force = 100.0;

        var mesh = MeshBox(length, width, thick, 0.004);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } },               // x-min
                new ForceLoad
                {
                    Name = "Tip load",
                    FaceIds = new[] { 1 },                                               // x-max
                    TotalForce = new Vector3D(0, 0, -force)                              // bend about y
                }
            }
        };

        var output = new LinearStaticSolver().Solve(input);

        // Timoshenko: δ = FL³/3EI + FL/(κGA), κ = 5/6 for rectangle.
        double inertia = width * Math.Pow(thick, 3) / 12.0;
        double g = Steel.YoungsModulus / (2 * (1 + Steel.PoissonRatio));
        double bending = force * Math.Pow(length, 3) / (3 * Steel.YoungsModulus * inertia);
        double shear = force * length / (5.0 / 6.0 * g * width * thick);
        double analytic = bending + shear;

        var tipNodes = mesh.GetFaceNodes(new[] { 1 });
        var displacement = (NodalVectorField)output.Fields.Single(f => f.Name == "Displacement");
        double tip = -tipNodes.Average(n => displacement.GetVector(n).Z);

        double ratio = tip / analytic;
        Assert.InRange(ratio, 0.40, 1.05);

        // Peak stress must occur at the wall (moment maximum).
        var vonMises = (NodalScalarField)output.Fields.Single(f => f.Name == "Stress (von Mises)");
        int peakNode = Enumerable.Range(0, mesh.NodeCount).OrderByDescending(vonMises.GetScalar).First();
        Assert.True(mesh.Nodes[peakNode].X < 0.25 * length,
            $"Peak von Mises should be near the wall; found at x = {mesh.Nodes[peakNode].X:g3} m.");
    }

    // ------------------------------------------------------------------
    // Linearity: doubling the load must exactly double the response.
    // ------------------------------------------------------------------
    [Fact]
    public void Solution_IsLinearInLoad()
    {
        var mesh = MeshBox(0.05, 0.02, 0.02, 0.008);
        SolveInput MakeInput(double load) => new()
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Fix", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "F", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, 0, -load) }
            }
        };

        var solver = new LinearStaticSolver();
        var u1 = (NodalVectorField)solver.Solve(MakeInput(100)).Fields.Single(f => f.Name == "Displacement");
        var u2 = (NodalVectorField)solver.Solve(MakeInput(200)).Fields.Single(f => f.Name == "Displacement");

        double max1 = Enumerable.Range(0, u1.Count).Max(u1.GetScalar);
        double max2 = Enumerable.Range(0, u2.Count).Max(u2.GetScalar);
        Assert.Equal(2.0, max2 / max1, 6);
    }

    // ------------------------------------------------------------------
    // Validation errors must be actionable.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_MissingSupportOrLoad_Throws()
    {
        var mesh = MeshBox(0.05, 0.02, 0.02, 0.01);
        var solver = new LinearStaticSolver();

        var noSupport = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new ForceLoad { Name = "F", FaceIds = new[] { 1 }, TotalForce = new Vector3D(1, 0, 0) }
            }
        };
        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noSupport));
        Assert.Contains("fixed support", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var noLoad = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Fix", FaceIds = new[] { 0 } }
            }
        };
        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noLoad));
        Assert.Contains("load", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }
}
