using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// The post-processing geometry (contour extraction, marching-tetrahedra section
/// cutting) verified against analytic reference cases and a linear field on a real
/// mesh — linear interpolation must reproduce a linear field exactly, jitter or not.
/// </summary>
public class PostProcessingTests
{
    /// <summary>The reference unit tet (corners at origin and unit axes) as an FeMesh.</summary>
    private static FeMesh UnitTet() => new(
        new[]
        {
            new Vector3D(0, 0, 0), new Vector3D(1, 0, 0),
            new Vector3D(0, 1, 0), new Vector3D(0, 0, 1)
        },
        new[] { new Tet4(0, 1, 2, 3) },
        new[]
        {
            new BoundaryTriangle(0, 2, 1, 0), new BoundaryTriangle(0, 1, 3, 0),
            new BoundaryTriangle(0, 3, 2, 0), new BoundaryTriangle(1, 2, 3, 0)
        });

    private static double Area(CutTriangle t) =>
        0.5 * Vector3D.Cross(t.P1 - t.P0, t.P2 - t.P0).Length;

    [Fact]
    public void Cut_31Split_OneTriangleWithAnalyticArea()
    {
        // Plane x = 0.5: only corner (1,0,0) is above → the lone-vertex case.
        // The cut is the triangle with vertices (0.5,0,0), (0.5,0.5,0), (0.5,0,0.5):
        // a right triangle with legs 0.5 → area 1/8.
        var mesh = UnitTet();
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        var cut = SectionCutter.Cut(mesh, new SectionPlane(SectionAxis.X, 0.5), scalars, null, 1);

        var triangle = Assert.Single(cut);
        Assert.Equal(0.125, Area(triangle), 12);
        // Every cut vertex lies exactly on the plane and its scalar (= x) equals 0.5.
        foreach (var (p, s) in new[] { (triangle.P0, triangle.S0), (triangle.P1, triangle.S1), (triangle.P2, triangle.S2) })
        {
            Assert.Equal(0.5, p.X, 12);
            Assert.Equal(0.5, s, 12);
        }
    }

    [Fact]
    public void Cut_22Split_TwoTrianglesNoBowTie()
    {
        // Plane x + ... no: use axis plane through the middle of an edge pair. For the
        // unit tet and plane z = 0.25 shifted... simplest 2-2: plane separating
        // {(0,0,0),(1,0,0)} from {(0,1,0),(0,0,1)} does not exist axis-aligned; use a
        // stretched tet instead: corners at y = 0 and y = 1 pairs.
        var mesh = new FeMesh(
            new[]
            {
                new Vector3D(0, 0, 0), new Vector3D(1, 0, 0),
                new Vector3D(0, 1, 0), new Vector3D(1, 1, 1)
            },
            new[] { new Tet4(0, 1, 2, 3) },
            Array.Empty<BoundaryTriangle>());
        var scalars = mesh.Nodes.Select(n => n.Y).ToArray();
        var cut = SectionCutter.Cut(mesh, new SectionPlane(SectionAxis.Y, 0.5), scalars, null, 1);

        // Two corners below (y=0), two above (y=1) → quad emitted as two triangles.
        Assert.Equal(2, cut.Count);
        foreach (var t in cut)
        {
            Assert.True(Area(t) > 0);
            foreach (var (p, s) in new[] { (t.P0, t.S0), (t.P1, t.S1), (t.P2, t.S2) })
            {
                Assert.Equal(0.5, p.Y, 12);
                Assert.Equal(0.5, s, 12);
            }
        }
        // No bow-tie: both triangle normals point the same way (a bow-tie flips one).
        var n0 = Vector3D.Cross(cut[0].P1 - cut[0].P0, cut[0].P2 - cut[0].P0);
        var n1 = Vector3D.Cross(cut[1].P1 - cut[1].P0, cut[1].P2 - cut[1].P0);
        Assert.True(Vector3D.Dot(n0, n1) > 0, "Quad triangulation produced a bow-tie.");
    }

    [Fact]
    public void Cut_LinearField_OnRealMesh_IsExactAndAreaMatches()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(1, 1, 1), new MeshSettings { TargetEdgeLength = 0.25 });
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        var cut = SectionCutter.Cut(mesh, new SectionPlane(SectionAxis.X, 0.5), scalars, null, 1);

        Assert.NotEmpty(cut);
        double area = 0;
        foreach (var t in cut)
        {
            area += Area(t);
            // Linear interpolation of the linear field s = x is exact.
            foreach (var (p, s) in new[] { (t.P0, t.S0), (t.P1, t.S1), (t.P2, t.S2) })
            {
                Assert.Equal(0.5, p.X, 9);
                Assert.Equal(0.5, s, 9);
            }
        }
        // Mid-cube cross-section ≈ 1.0, within the mesher's geometry tolerance class.
        Assert.InRange(area, 0.96, 1.04);
    }

    [Fact]
    public void Contours_LinearField_LieExactlyOnTheirLevels()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(1, 1, 1), new MeshSettings { TargetEdgeLength = 0.25 });
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        double min = scalars.Min(), max = scalars.Max();

        const int levels = 7;
        var segments = IsoLineExtractor.Extract(mesh, scalars, null, 1, levels, min, max);

        Assert.NotEmpty(segments);
        double range = max - min;
        foreach (var (a, b) in segments)
        {
            // Every endpoint's x must sit exactly on one of the interior levels.
            foreach (var p in new[] { a, b })
            {
                double k = (p.X - min) / range * (levels + 1);
                Assert.Equal(Math.Round(k), k, 6);
                Assert.InRange(Math.Round(k), 1, levels);
            }
        }
    }

    [Fact]
    public void Deformation_ShiftsCutAndContoursConsistently()
    {
        var mesh = UnitTet();
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        var shift = new Vector3D(0, 0, 0.25);
        var displacement = Enumerable.Repeat(shift, mesh.NodeCount).ToArray();

        var cut = SectionCutter.Cut(mesh, new SectionPlane(SectionAxis.X, 0.5), scalars, displacement, 2.0);
        var triangle = Assert.Single(cut);
        // A rigid shift of 2 × 0.25 in z moves every cut vertex by exactly that.
        Assert.Equal(0.5, triangle.P0.X, 12);
        Assert.True(triangle.P0.Z >= 0.5 - 1e-12, "Cut vertices must follow the deformation.");
    }

    [Fact]
    public void SkinFilter_HidesWhollyClippedTriangles_KeepsStraddlers()
    {
        var mesh = UnitTet();
        var plane = new SectionPlane(SectionAxis.X, 0.25);

        // Face (0,3,2) lies in the x = 0 plane → fully kept side → visible.
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2], plane, null, 1));
        // Face (1,2,3) has vertex (1,0,0) beyond the plane but two vertices on the
        // kept side → straddler → still visible.
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[3], plane, null, 1));
        // With the plane at x = -0.1 everything is beyond → hidden.
        var behind = new SectionPlane(SectionAxis.X, -0.1);
        Assert.False(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[3], behind, null, 1));
    }

    [Fact]
    public void SkinFilter_FlippedPlane_KeepsOppositeSide()
    {
        var mesh = UnitTet();

        // Plane at x = -0.1: everything is on the positive side, so flipping keeps it all.
        var behindFlipped = new SectionPlane(SectionAxis.X, -0.1) { FlipKeptSide = true };
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2], behindFlipped, null, 1));
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[3], behindFlipped, null, 1));

        // Plane at x = 1.1: everything is below → default keeps all, flipped hides all.
        var beyond = new SectionPlane(SectionAxis.X, 1.1);
        var beyondFlipped = beyond with { FlipKeptSide = true };
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2], beyond, null, 1));
        Assert.False(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2], beyondFlipped, null, 1));

        // A straddling plane keeps the straddler face from BOTH sides (it crosses the cut).
        var mid = new SectionPlane(SectionAxis.X, 0.25);
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[3], mid, null, 1));
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[3],
            mid with { FlipKeptSide = true }, null, 1));
        // ... but the x = 0 face (0,3,2) is wholly on the negative side: kept by
        // default, hidden when flipped.
        Assert.True(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2], mid, null, 1));
        Assert.False(SectionCutter.IsTriangleVisible(mesh, mesh.BoundaryTriangles[2],
            mid with { FlipKeptSide = true }, null, 1));
    }

    [Fact]
    public void Cut_FlippedPlane_SameCutCrossSection()
    {
        // The cross-section itself is side-independent: flipping which half is kept
        // must produce the same cut area on the same plane with the same scalars.
        var mesh = UnitTet();
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        var plane = new SectionPlane(SectionAxis.X, 0.5);

        var cut = Assert.Single(SectionCutter.Cut(mesh, plane, scalars, null, 1));
        var flipped = Assert.Single(SectionCutter.Cut(
            mesh, plane with { FlipKeptSide = true }, scalars, null, 1));

        Assert.Equal(0.125, Area(cut), 12);
        Assert.Equal(0.125, Area(flipped), 12);
        foreach (var (p, s) in new[] { (flipped.P0, flipped.S0), (flipped.P1, flipped.S1), (flipped.P2, flipped.S2) })
        {
            Assert.Equal(0.5, p.X, 12);
            Assert.Equal(0.5, s, 12);
        }
    }

    [Fact]
    public void Contours_FlippedClip_KeepsOppositeSide()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(1, 1, 1), new MeshSettings { TargetEdgeLength = 0.25 });
        var scalars = mesh.Nodes.Select(n => n.X).ToArray();
        double min = scalars.Min(), max = scalars.Max();

        int unclipped = IsoLineExtractor.Extract(mesh, scalars, null, 1, 7, min, max).Count;
        Assert.True(unclipped > 0);

        // A plane below the box (y = -0.1): every vertex is on the positive side, so
        // the default clip drops everything and the flipped clip keeps everything.
        var below = new SectionPlane(SectionAxis.Y, -0.1);
        Assert.Empty(IsoLineExtractor.Extract(mesh, scalars, null, 1, 7, min, max, below));
        Assert.Equal(unclipped, IsoLineExtractor.Extract(mesh, scalars, null, 1, 7, min, max,
            below with { FlipKeptSide = true }).Count);

        // And symmetrically above the box (y = 1.1).
        var above = new SectionPlane(SectionAxis.Y, 1.1);
        Assert.Equal(unclipped, IsoLineExtractor.Extract(mesh, scalars, null, 1, 7, min, max, above).Count);
        Assert.Empty(IsoLineExtractor.Extract(mesh, scalars, null, 1, 7, min, max,
            above with { FlipKeptSide = true }));
    }
}
