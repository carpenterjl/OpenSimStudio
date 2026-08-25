using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// HEX20 through the full structural solver: the patch test, the consistent surface loads,
/// support pinning, and the bending behaviour the element exists for.
///
/// The QUAD8 load identity here is the one that had to be got right rather than approximated.
/// A hexahedral face carries MINUS one twelfth of its load on each corner and a third on each
/// mid-side node; treating the triangulated skin as two T6 triangles would put zero on the
/// corners and a third on three mid-nodes of a diagonal that is not an element edge. Both are
/// "a distribution summing to the resultant", and only one is the right one.
/// </summary>
public class Hex20SolverTests
{
    private readonly ITestOutputHelper _output;

    public Hex20SolverTests(ITestOutputHelper output) => _output = output;

    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh Lattice(double lx, double ly, double lz, int nx, int ny, int nz) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(lx, ly, lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    // ------------------------------------------------------------------ the patch test

    /// <summary>
    /// The patch test: prescribe a linear displacement field on every boundary node — corners
    /// AND mid-edge nodes — and the interior must reproduce the constant strain exactly. This
    /// validates the shape functions, the Jacobian mapping, the quadrature and the assembly
    /// together; a serendipity hex is complete to first order and the 27-point rule is exact
    /// here, so there is no discretization error to hide behind.
    /// </summary>
    [Fact]
    public void PatchTest_ConstantStrainReproducedExactly()
    {
        var mesh = Lattice(1, 1, 1, 3, 3, 3);
        var assembler = new Hex20Assembler(mesh, Steel);
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

        // Every boundary node of the mesh gets u = A x, whatever kind of node it is.
        var boundary = new HashSet<int>();
        foreach (var t in mesh.BoundaryTriangles) { boundary.Add(t.A); boundary.Add(t.B); boundary.Add(t.C); }
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        foreach (var q in mesh.BoundaryQuads!)
            foreach (var (p, r) in new[] { (q.A, q.B), (q.B, q.C), (q.C, q.D), (q.D, q.A) })
                boundary.Add(edgeMid[p < r ? (p, r) : (r, p)]);

        var prescribed = new Dictionary<int, double>();
        foreach (int node in boundary)
        {
            var p = mesh.Nodes[node];
            for (int i = 0; i < 3; i++)
                prescribed[node * 3 + i] = a[i, 0] * p.X + a[i, 1] * p.Y + a[i, 2] * p.Z;
        }

        var result = ConstrainedSystemSolver.Solve(stiffness, new double[stiffness.RowCount], prescribed);

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

    // ------------------------------------------------------------------ QUAD8 loads

    /// <summary>
    /// The consistent-load identity, on a single flat face: each mid-side node takes A/3 and
    /// each corner MINUS A/12, exactly. Anything that quietly distributed the load some other
    /// way would still sum to the resultant, which is why the per-node values are asserted and
    /// not just the total.
    /// </summary>
    [Fact]
    public void UniformPressureOnAFace_GivesTheClassicQuad8NodalLoads()
    {
        const double lx = 0.20, ly = 0.06, lz = 0.02, pressure = 1.5e6;
        var mesh = Lattice(lx, ly, lz, 4, 3, 2);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);

        var quads = mesh.GetFaceQuads(new[] { 5 });        // z-max
        double cellArea = (lx / 4) * (ly / 3);

        // One interior corner of the face is shared by four quads, a mid-side node by one.
        var perNode = new Dictionary<int, double>();
        foreach (var q in quads)
        {
            var (nodes, areas, areaVectors) = StructuralSurfaceLoads.QuadLoadWeights(mesh, q, edgeMid);
            for (int k = 0; k < 8; k++)
            {
                // The classic weights, on this rectangle, to the last few bits.
                double expected = k < 4 ? -cellArea / 12 : cellArea / 3;
                Assert.Equal(expected, areas[k], 15);

                // And the area VECTOR points along the outward normal with that magnitude.
                Assert.Equal(0.0, areaVectors[k].X, 15);
                Assert.Equal(0.0, areaVectors[k].Y, 15);
                Assert.Equal(expected, areaVectors[k].Z, 15);

                perNode[nodes[k]] = perNode.GetValueOrDefault(nodes[k]) + areas[k];
            }
        }

        // The whole face still integrates to its own area — the shares are a partition of it.
        Assert.Equal(lx * ly, perNode.Values.Sum(), 12);

        // And the resultant the SOLVER reports — computed by its own load builder, not by
        // this test — is the pressure times the face area.
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new PressureLoad { Name = "p", FaceIds = new List<int> { 5 }, Magnitude = pressure }
            }
        });
        string reported = Assert.Single(output.Log, l => l.StartsWith("Pressure 'p'"));
        Assert.Contains($"resultant {pressure * lx * ly:g4} N", reported);
    }

    /// <summary>
    /// A force spread over a face keeps its resultant exactly, and the CORNERS carry load in
    /// the opposite direction — the tell that this is the QUAD8 rule rather than a triangulated
    /// approximation of it, and the reason a hexahedral face cannot be loaded as two triangles.
    /// </summary>
    [Fact]
    public void AForceLoad_KeepsItsResultantAndPutsNegativeLoadOnTheCorners()
    {
        const double lx = 0.20, ly = 0.06, lz = 0.02;
        var mesh = Lattice(lx, ly, lz, 5, 3, 2);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);

        // The distribution the solver uses, read from the same production routine it calls.
        var quads = mesh.GetFaceQuads(new[] { 5 });
        double totalArea = quads.Sum(
            q => StructuralSurfaceLoads.QuadLoadWeights(mesh, q, edgeMid).Areas.Sum());
        Assert.Equal(lx * ly, totalArea, 12);

        var perNode = new Dictionary<int, double>();
        foreach (var q in quads)
        {
            var (nodes, areas, _) = StructuralSurfaceLoads.QuadLoadWeights(mesh, q, edgeMid);
            for (int k = 0; k < 8; k++)
                perNode[nodes[k]] = perNode.GetValueOrDefault(nodes[k]) + areas[k] / totalArea;
        }

        // The shares are a partition of one, so any force keeps its magnitude exactly...
        Assert.Equal(1.0, perNode.Values.Sum(), 12);
        // ...and some of them are NEGATIVE, which a per-triangle T6 rule never produces.
        Assert.Contains(perNode.Values, share => share < 0);

        // End to end: the solver reports the force it actually distributed, over quads.
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new ForceLoad
                {
                    Name = "F", FaceIds = new List<int> { 5 },
                    TotalForce = new Vector3D(0, 0, -5e5)
                }
            }
        });
        string reported = Assert.Single(output.Log, l => l.StartsWith("Force 'F'"));
        Assert.Contains($"over {quads.Count} face quads", reported);
    }

    // ------------------------------------------------------------------ supports

    /// <summary>
    /// A support scoped to a face must pin every node of that face, mid-side nodes included,
    /// and the triangulation diagonal must cost nothing: the two triangles of a quad cover
    /// all four of its perimeter edges between them.
    /// </summary>
    [Fact]
    public void AFaceSupport_PinsEveryNodeOfTheFaceIncludingMidSideNodes()
    {
        var mesh = Lattice(0.20, 0.06, 0.02, 4, 3, 2);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        var support = new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } };

        var prescribed = StructuralSurfaceLoads.BuildPrescribedDofs(
            mesh, new List<BoundaryCondition> { support }, edgeMid, null);

        // Everything the face touches: its corner nodes plus the mid-node of every quad edge.
        var expected = new HashSet<int>(mesh.GetFaceNodes(new[] { 0 }));
        foreach (var q in mesh.GetFaceQuads(new[] { 0 }))
            foreach (var (a, b) in new[] { (q.A, q.B), (q.B, q.C), (q.C, q.D), (q.D, q.A) })
                expected.Add(edgeMid[a < b ? (a, b) : (b, a)]);

        var pinned = new HashSet<int>(prescribed.Keys.Select(dof => dof / 3));
        Assert.Equal(expected.OrderBy(n => n), pinned.OrderBy(n => n));
        Assert.Equal(expected.Count * 3, prescribed.Count);

        // And every pinned node genuinely lies on the plane x = 0.
        foreach (int n in pinned) Assert.Equal(0.0, mesh.Nodes[n].X);
    }

    [Fact]
    public void AnEdgeSupport_PinsTheWholeLineIncludingMidEdgeNodes()
    {
        var mesh = Lattice(0.20, 0.06, 0.02, 6, 3, 2);
        int edgeId = Assert.Single(mesh.Edges.EdgesBetween(new[] { 4, 2 }));

        var support = new FixedSupport
        {
            Name = "line", FaceIds = new List<int>(), EdgeIds = new List<int> { edgeId }
        };
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        var prescribed = StructuralSurfaceLoads.BuildPrescribedDofs(
            mesh, new List<BoundaryCondition> { support }, edgeMid, null);

        var pinned = new HashSet<int>(prescribed.Keys.Select(dof => dof / 3));
        Assert.Equal(2 * 6 + 1, pinned.Count);              // corners and mids along the line
        foreach (int n in pinned)
        {
            Assert.Equal(0.0, mesh.Nodes[n].Y);
            Assert.Equal(0.0, mesh.Nodes[n].Z);
        }
    }

    // ------------------------------------------------------------------ physics

    /// <summary>
    /// Uniaxial tension: a bar pulled on one end elongates by PL/AE. Every ingredient — the
    /// QUAD8 loads, the mid-node pinning, the assembly — has to be right for this to land,
    /// and a HEX20 mesh represents the linear solution exactly, so the band is tight.
    /// </summary>
    [Fact]
    public void UniaxialTension_MatchesTheExactSolution()
    {
        const double lx = 0.20, ly = 0.04, lz = 0.03, pull = 4e5;
        var mesh = Lattice(lx, ly, lz, 8, 3, 2);

        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new ForceLoad
                {
                    Name = "pull",
                    FaceIds = new List<int> { 1 },
                    TotalForce = new Vector3D(pull, 0, 0)
                }
            }
        };

        var output = new LinearStaticSolver().Solve(input);
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");

        double tip = mesh.GetFaceNodes(new[] { 1 }).Max(n => displacement.Values[n].X);
        double exact = pull * lx / (ly * lz * Steel.YoungsModulus);
        _output.WriteLine($"tip {tip:g6} m against PL/AE {exact:g6} m -> {tip / exact:P2}");

        // The fixed end suppresses the Poisson contraction over roughly one thickness, which
        // stiffens the bar slightly — the deviation is that end effect, not element error.
        Assert.InRange(tip / exact, 0.97, 1.01);
    }

    /// <summary>
    /// The cantilever benchmark: the bending case that made TET4 unusable and TET10 usable.
    /// HEX20 is the element a commercial code would put on this, and it should sit at least
    /// as close to Timoshenko as TET10 does.
    /// </summary>
    [Fact]
    public void CantileverBeam_TipDeflectionMatchesTimoshenko()
    {
        const double length = 0.30, width = 0.03, height = 0.02, load = 2e3;
        var mesh = Lattice(length, width, height, 24, 3, 2);

        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new ForceLoad
                {
                    Name = "tip",
                    FaceIds = new List<int> { 1 },
                    TotalForce = new Vector3D(0, 0, -load)
                }
            }
        };

        var output = new LinearStaticSolver().Solve(input);
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");
        double tip = mesh.GetFaceNodes(new[] { 1 }).Min(n => displacement.Values[n].Z);

        double inertia = width * height * height * height / 12.0;
        double shear = Steel.YoungsModulus / (2 * (1 + Steel.PoissonRatio));
        double analytic = load * length * length * length / (3 * Steel.YoungsModulus * inertia)
                        + load * length / ((5.0 / 6.0) * shear * width * height);

        double ratio = Math.Abs(tip) / analytic;
        _output.WriteLine($"tip {Math.Abs(tip):g6} m against Timoshenko {analytic:g6} m -> {ratio:P2}");

        // TET10 holds [0.92, 1.03] on this problem; a mapped HEX20 mesh should be inside that.
        Assert.InRange(ratio, 0.95, 1.03);
    }

    [Fact]
    public void ASolveLogsTheElementType()
    {
        var mesh = Lattice(0.20, 0.06, 0.02, 4, 3, 2);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new PressureLoad { Name = "p", FaceIds = new List<int> { 5 }, Magnitude = 1e5 }
            }
        };

        var output = new LinearStaticSolver().Solve(input);
        Assert.Contains(output.Log, line => line.Contains("HEX20"));
        // The pressure resultant is reported, and it is exactly p times the face area.
        Assert.Contains(output.Log, line => line.Contains("resultant 1200 N"));

        // A force load names the quad skin it was distributed over, not a triangle count.
        var forced = new LinearStaticSolver().Solve(input with
        {
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new ForceLoad
                {
                    Name = "F",
                    FaceIds = new List<int> { 5 },
                    TotalForce = new Vector3D(0, 0, -1e4)
                }
            }
        });
        Assert.Contains(forced.Log, line => line.Contains("face quads"));
    }
}
