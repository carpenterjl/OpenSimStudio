using System.Text.Json;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// The hexahedral element layer on <see cref="FeMesh"/>: its invariants, its volume rule,
/// its node ordering, and the back-compatibility of the persisted form.
///
/// The invariant tests matter more than they look. A hex mesh carries NO tetrahedra, and
/// dozens of loops in this codebase walk <c>ElementCount</c> indexing <c>Elements[e]</c> —
/// a mesh that carried both families would hand every one of them the wrong list. The
/// constructor refuses that combination outright rather than leaving it to be discovered
/// as an index crash somewhere downstream.
/// </summary>
public class HexMeshTests
{
    /// <summary>One HEX20 element on the box [0,lx]x[0,ly]x[0,lz], nodes in canonical order.</summary>
    private static FeMesh SingleHex(double lx, double ly, double lz)
    {
        var corners = new List<Vector3D>
        {
            new(0, 0, 0), new(lx, 0, 0), new(lx, ly, 0), new(0, ly, 0),
            new(0, 0, lz), new(lx, 0, lz), new(lx, ly, lz), new(0, ly, lz)
        };
        var hexes = new List<Hex8> { new(0, 1, 2, 3, 4, 5, 6, 7) };
        var quads = new List<BoundaryQuad>
        {
            new(0, 3, 2, 1, 4), new(4, 5, 6, 7, 5),
            new(0, 1, 5, 4, 2), new(1, 2, 6, 5, 1),
            new(2, 3, 7, 6, 3), new(3, 0, 4, 7, 0)
        };
        var triangles = new List<BoundaryTriangle>();
        foreach (var q in quads)
        {
            triangles.Add(new BoundaryTriangle(q.A, q.B, q.C, q.FaceId));
            triangles.Add(new BoundaryTriangle(q.A, q.C, q.D, q.FaceId));
        }
        return QuadraticMeshBuilder.UpgradeHex(corners, hexes, triangles, quads);
    }

    [Fact]
    public void AHexMesh_IsQuadraticAndHexahedralAndCarriesNoTetrahedra()
    {
        var mesh = SingleHex(2, 3, 5);

        Assert.True(mesh.IsHex);
        Assert.True(mesh.IsQuadratic);          // the guard every TET4-only solver reads
        Assert.Empty(mesh.Elements);
        Assert.Equal(1, mesh.ElementCount);
        Assert.Equal(20, mesh.GetElementNodes(0).Length);
        // 8 corners + 12 edge midpoints, all distinct.
        Assert.Equal(20, mesh.NodeCount);
        Assert.Equal(20, mesh.GetElementNodes(0).Distinct().Count());
    }

    [Fact]
    public void HexVolume_OfABrick_IsExactlyTheProductOfItsSides()
    {
        var mesh = SingleHex(2, 3, 5);
        Assert.Equal(30.0, mesh.ElementVolume(0), 12);
        Assert.Equal(30.0, mesh.TotalVolume(), 12);
    }

    /// <summary>
    /// The volume rule is a closed form for ANY trilinear hexahedron, not just a brick: the
    /// Jacobian determinant is degree 2 at most per reference direction and the 2x2x2 Gauss
    /// rule is exact to degree 3.
    /// <para>
    /// The oracle here is a SUBDIVISION of the same trilinear map — the reference cube cut
    /// into n^3 sub-cells, each priced as six tetrahedra on its own eight mapped corners.
    /// That converges to the trilinear volume from a completely different construction, and
    /// the gate is the convergence itself: refining must move the estimate TOWARD the closed
    /// form, and the finest level must sit within its own discretization error of it.
    /// </para>
    /// <para>
    /// Note what this rules out, because it is the trap: a warped hexahedron is NOT the union
    /// of six tetrahedra on its corners. Its faces are bilinear patches, and a tetrahedral
    /// decomposition replaces each with two flat triangles — a different solid, off by a
    /// couple of percent on the cell below. Only the subdivision recovers the true one.
    /// </para>
    /// </summary>
    [Fact]
    public void HexVolume_OfADistortedCell_MatchesASubdividedTrilinearHull()
    {
        var corners = new[]
        {
            new Vector3D(0.0, 0.0, 0.0), new Vector3D(2.1, -0.1, 0.2),
            new Vector3D(1.9, 3.2, -0.3), new Vector3D(-0.2, 2.8, 0.1),
            new Vector3D(0.3, 0.2, 4.7), new Vector3D(2.4, 0.1, 5.1),
            new Vector3D(2.0, 3.0, 4.9), new Vector3D(-0.1, 3.1, 5.2)
        };
        var mesh = SingleHexFrom(corners);
        double closedForm = mesh.ElementVolume(0);

        Assert.True(closedForm > 0, "the canonical node order must give a positive volume");

        // The subdivision is second-order in cell size, so its error must fall by ~4x each
        // time n doubles. THAT is the gate: an oracle converging at its own predicted rate
        // onto the closed form cannot be converging onto a different number.
        var errors = new[] { 6, 12, 24, 48 }
            .Select(n => Math.Abs(SubdividedVolume(corners, n) - closedForm))
            .ToArray();
        for (int i = 1; i < errors.Length; i++)
            Assert.True(errors[i] < 0.35 * errors[i - 1],
                $"doubling the subdivision must cut the error at least ~3x (second order): " +
                $"{string.Join(" -> ", errors.Select(e => e.ToString("g4")))}");

        // And the finest level agrees to well inside its own remaining discretization error.
        Assert.True(errors[^1] / closedForm < 1e-4,
            $"finest subdivision differs by {errors[^1] / closedForm:g3} relative");
    }

    /// <summary>
    /// Volume of the trilinear hull by subdividing the REFERENCE cube into n^3 cells and
    /// summing six tetrahedra per mapped cell. Independent of the Gauss rule under test.
    /// </summary>
    private static double SubdividedVolume(IReadOnlyList<Vector3D> corners, int n)
    {
        int[] sx = { -1, 1, 1, -1, -1, 1, 1, -1 };
        int[] sy = { -1, -1, 1, 1, -1, -1, 1, 1 };
        int[] sz = { -1, -1, -1, -1, 1, 1, 1, 1 };

        Vector3D Map(double xi, double eta, double zeta)
        {
            var p = new Vector3D(0, 0, 0);
            for (int i = 0; i < 8; i++)
                p += corners[i] * (0.125 * (1 + sx[i] * xi) * (1 + sy[i] * eta) * (1 + sz[i] * zeta));
            return p;
        }

        // The six-tetrahedron Freudenthal split of a cell, in bit ordering (0=x, 1=y, 2=z).
        int[][] cellTets =
        {
            new[] { 0, 1, 3, 7 }, new[] { 0, 1, 7, 5 }, new[] { 0, 5, 7, 4 },
            new[] { 0, 3, 2, 7 }, new[] { 0, 6, 4, 7 }, new[] { 0, 2, 6, 7 }
        };

        double total = 0;
        var cell = new Vector3D[8];
        for (int k = 0; k < n; k++)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < 8; c++)
                        cell[c] = Map(
                            -1 + 2.0 * (i + (c & 1)) / n,
                            -1 + 2.0 * (j + ((c >> 1) & 1)) / n,
                            -1 + 2.0 * (k + ((c >> 2) & 1)) / n);
                    foreach (var t in cellTets)
                    {
                        var p0 = cell[t[0]];
                        total += Math.Abs(Vector3D.Dot(cell[t[1]] - p0,
                            Vector3D.Cross(cell[t[2]] - p0, cell[t[3]] - p0)) / 6.0);
                    }
                }
        return total;
    }

    /// <summary>One HEX20 element over eight given corners, in canonical order.</summary>
    private static FeMesh SingleHexFrom(IReadOnlyList<Vector3D> corners)
    {
        var hexes = new List<Hex8> { new(0, 1, 2, 3, 4, 5, 6, 7) };
        var quads = new List<BoundaryQuad>
        {
            new(0, 3, 2, 1, 0), new(4, 5, 6, 7, 1),
            new(0, 1, 5, 4, 2), new(1, 2, 6, 5, 3),
            new(2, 3, 7, 6, 4), new(3, 0, 4, 7, 5)
        };
        var triangles = new List<BoundaryTriangle>();
        foreach (var q in quads)
        {
            triangles.Add(new BoundaryTriangle(q.A, q.B, q.C, q.FaceId));
            triangles.Add(new BoundaryTriangle(q.A, q.C, q.D, q.FaceId));
        }
        return QuadraticMeshBuilder.UpgradeHex(corners.ToList(), hexes, triangles, quads);
    }

    [Fact]
    public void MixedElementFamilies_AreRefusedByName()
    {
        var nodes = new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };
        var ex = Assert.Throws<ArgumentException>(() => new FeMesh(
            nodes,
            new List<Tet4> { new(0, 1, 2, 3) },
            Array.Empty<BoundaryTriangle>(),
            hexElements: new List<Hex8> { new(0, 1, 2, 3, 0, 1, 2, 3) },
            hexMidEdgeNodes: new List<Hex20Mid> { default },
            boundaryQuads: Array.Empty<BoundaryQuad>()));
        Assert.Contains("carries no tetrahedra", ex.Message);
    }

    [Fact]
    public void AHexMeshWithoutItsMidEdgeNodes_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FeMesh(
            new List<Vector3D> { new(0, 0, 0) },
            Array.Empty<Tet4>(),
            Array.Empty<BoundaryTriangle>(),
            hexElements: new List<Hex8> { new(0, 0, 0, 0, 0, 0, 0, 0) },
            boundaryQuads: Array.Empty<BoundaryQuad>()));
        Assert.Contains("HEX20", ex.Message);
    }

    [Fact]
    public void AHexMeshWithoutItsQuadSkin_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FeMesh(
            new List<Vector3D> { new(0, 0, 0) },
            Array.Empty<Tet4>(),
            Array.Empty<BoundaryTriangle>(),
            hexElements: new List<Hex8> { new(0, 0, 0, 0, 0, 0, 0, 0) },
            hexMidEdgeNodes: new List<Hex20Mid> { default }));
        Assert.Contains("quad skin", ex.Message);
    }

    [Fact]
    public void HexMembersWithoutHexElements_AreRefused()
    {
        var nodes = new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };
        var tets = new List<Tet4> { new(0, 1, 2, 3) };

        Assert.Throws<ArgumentException>(() => new FeMesh(nodes, tets,
            Array.Empty<BoundaryTriangle>(), hexMidEdgeNodes: new List<Hex20Mid> { default }));
        Assert.Throws<ArgumentException>(() => new FeMesh(nodes, tets,
            Array.Empty<BoundaryTriangle>(), boundaryQuads: new List<BoundaryQuad> { default }));
    }

    /// <summary>Region ids are validated against the element count that actually applies —
    /// the hexahedra, on a hexahedral mesh.</summary>
    [Fact]
    public void RegionIdsAreCountedAgainstTheHexahedra()
    {
        var mesh = SingleHex(1, 1, 1);
        var ex = Assert.Throws<ArgumentException>(() => new FeMesh(
            mesh.Nodes, Array.Empty<Tet4>(), mesh.BoundaryTriangles,
            elementRegionIds: new[] { 0, 0 },
            hexElements: mesh.HexElements, hexMidEdgeNodes: mesh.HexMidEdgeNodes,
            boundaryQuads: mesh.BoundaryQuads));
        Assert.Contains("2 entries", ex.Message);
        Assert.Contains("1 elements", ex.Message);
    }

    [Fact]
    public void EveryQuadEdge_ResolvesInTheEdgeMidMap()
    {
        var mesh = SingleHex(2, 3, 5);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        Assert.Equal(12, edgeMid.Count);

        foreach (var q in mesh.BoundaryQuads!)
        {
            foreach (var (a, b) in new[] { (q.A, q.B), (q.B, q.C), (q.C, q.D), (q.D, q.A) })
                Assert.True(edgeMid.ContainsKey(a < b ? (a, b) : (b, a)),
                    "every edge of a boundary quad is an element edge");
            // The quad DIAGONAL is not an element edge — which is exactly why loads and
            // supports read the quads instead of the triangulated skin.
            int lo = Math.Min(q.A, q.C), hi = Math.Max(q.A, q.C);
            Assert.False(edgeMid.ContainsKey((lo, hi)));
        }
    }

    [Fact]
    public void MidEdgeNodes_AreTheMidpointsOfTheirEdges()
    {
        var mesh = SingleHex(2, 3, 5);
        var nodes = mesh.GetElementNodes(0);
        int[][] edges =
        {
            new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 3 }, new[] { 3, 0 },
            new[] { 4, 5 }, new[] { 5, 6 }, new[] { 6, 7 }, new[] { 7, 4 },
            new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 }
        };
        for (int e = 0; e < 12; e++)
        {
            var expected = (mesh.Nodes[nodes[edges[e][0]]] + mesh.Nodes[nodes[edges[e][1]]]) / 2.0;
            var actual = mesh.Nodes[nodes[8 + e]];
            Assert.Equal(expected.X, actual.X, 12);
            Assert.Equal(expected.Y, actual.Y, 12);
            Assert.Equal(expected.Z, actual.Z, 12);
        }
    }

    [Fact]
    public void GetFaceQuads_SelectsByFaceIdAndIsEmptyOnATetrahedralMesh()
    {
        var mesh = SingleHex(2, 3, 5);
        var top = mesh.GetFaceQuads(new[] { 5 });
        Assert.Single(top);
        Assert.Equal(5, top[0].FaceId);

        var tetMesh = new FeMesh(
            new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) },
            new List<Tet4> { new(0, 1, 2, 3) },
            new List<BoundaryTriangle> { new(0, 2, 1, 0) });
        Assert.Empty(tetMesh.GetFaceQuads(new[] { 0 }));
    }

    /// <summary>
    /// The persisted form round-trips, and — the back-compatibility claim — a project file
    /// written before hexes existed still loads, with every hex member null.
    /// </summary>
    [Fact]
    public void HexMesh_RoundTripsThroughJson_AndOldFilesLoadAsTetrahedral()
    {
        var mesh = SingleHex(2, 3, 5);
        var project = new SimProject { Name = "hex" };
        project.Bodies.Add(new Body { Name = "block", Mesh = mesh });

        var serializer = new ProjectSerializer();
        var path = Path.Combine(Path.GetTempPath(), $"hex-{Guid.NewGuid():N}.ossproj");
        try
        {
            serializer.Save(project, path);
            var loaded = serializer.Load(path);
            var reloaded = loaded.Bodies[0].Mesh!;

            Assert.True(reloaded.IsHex);
            Assert.True(reloaded.IsQuadratic);
            Assert.Equal(mesh.ElementCount, reloaded.ElementCount);
            Assert.Equal(mesh.NodeCount, reloaded.NodeCount);
            Assert.Equal(mesh.BoundaryQuads!.Count, reloaded.BoundaryQuads!.Count);
            Assert.Equal(mesh.GetElementNodes(0), reloaded.GetElementNodes(0));
            Assert.Equal(mesh.ElementVolume(0), reloaded.ElementVolume(0), 12);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AProjectFromBeforeHexesExisted_LoadsWithEveryHexMemberNull()
    {
        // Exactly the shape a pre-batch .ossproj has: a mesh with no hex members at all.
        const string json = """
        {
          "name": "old",
          "bodies": [
            {
              "name": "part",
              "mesh": {
                "nodes": [ {"x":0,"y":0,"z":0}, {"x":1,"y":0,"z":0},
                           {"x":0,"y":1,"z":0}, {"x":0,"y":0,"z":1} ],
                "elements": [ {"n0":0,"n1":1,"n2":2,"n3":3} ],
                "boundaryTriangles": [ {"a":0,"b":2,"c":1,"faceId":0} ]
              }
            }
          ]
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.ossproj");
        try
        {
            File.WriteAllText(path, json);
            var mesh = new ProjectSerializer().Load(path).Bodies[0].Mesh!;

            Assert.False(mesh.IsHex);
            Assert.False(mesh.IsQuadratic);
            Assert.Null(mesh.HexElements);
            Assert.Null(mesh.HexMidEdgeNodes);
            Assert.Null(mesh.BoundaryQuads);
            Assert.Equal(1, mesh.ElementCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A hexahedral mesh is quadratic, so every solver that handles linear tetrahedra only
    /// refuses it through the guard that was already there — by name, before any loop can
    /// index the empty tetrahedron list.
    /// </summary>
    [Fact]
    public void ScalarSolvers_RefuseAHexMesh_ByName()
    {
        var mesh = SingleHex(0.02, 0.02, 0.02);
        var material = new Material
        {
            Name = "copper",
            YoungsModulus = 110e9,
            PoissonRatio = 0.34,
            ThermalConductivity = 400,
            ElectricalConductivity = 5.8e7,
            Density = 8960,
            SpecificHeat = 385
        };
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new List<BoundaryCondition>()
        };

        var thermal = Assert.Throws<InvalidOperationException>(
            () => new HeatConductionSolver().Validate(input));
        Assert.Contains("linear tetrahedral (TET4)", thermal.Message);

        var electrical = Assert.Throws<InvalidOperationException>(
            () => new ElectricalConductionSolver().Validate(input));
        Assert.Contains("linear tetrahedral (TET4)", electrical.Message);
    }
}
