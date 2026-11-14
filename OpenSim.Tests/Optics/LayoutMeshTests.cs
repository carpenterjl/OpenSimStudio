using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Layout;
using static OpenSim.Tests.Optics.LayoutFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>OP-8: the conic sag, the lathe that turns surfaces into lens solids, and the
/// back-to-front order of transparent bodies.</summary>
public sealed class LayoutMeshTests
{
    // ---- Conic sag ----

    /// <summary>
    /// The sag against the surface-profile formula of the Wikipedia article "Aspheric lens"
    /// (section "Surface profile", read 2026-11-13) evaluated in its own radius form,
    /// z = r² / (R·(1 + √(1 − (1 + k)·r²/R²))), and against the quadric it is a root of,
    /// (1 + k)·z² − 2R·z + r² = 0, for every conic family the article names.
    /// </summary>
    [Theory]
    [InlineData(-2.5)]   // hyperboloid
    [InlineData(-1.0)]   // paraboloid
    [InlineData(-0.4)]   // prolate ellipsoid
    [InlineData(0.0)]    // sphere
    [InlineData(0.8)]    // oblate ellipsoid
    public void ConicSag_IsTheSurfaceProfileFormula_AndLiesOnItsQuadric(double k)
    {
        foreach (double radius in new[] { 0.08, -0.08 })
        {
            var sag = ConicSag.FromRadius(radius, k);
            for (int i = 0; i <= 20; i++)
            {
                double r = 0.03 * i / 20;
                double reference = r * r / (radius * (1 + Math.Sqrt(1 - (1 + k) * r * r / (radius * radius))));
                double z = sag.Sag(r * 0.6, r * 0.8);
                Assert.Equal(reference, z, 1e-17);
                double residual = (1 + k) * z * z - 2 * radius * z + r * r;
                Assert.True(Math.Abs(residual) <= 1e-17, $"k = {k}, R = {radius}, r = {r}: quadric residual {residual:g3}");
            }
        }
    }

    /// <summary>The two closed forms of elementary geometry: a sphere's sag R − √(R² − r²) and
    /// a paraboloid's r²/(2R).</summary>
    [Fact]
    public void ConicSag_MatchesSphereAndParaboloidClosedForms()
    {
        const double radius = 0.05;
        var sphere = ConicSag.FromRadius(radius);
        var parabola = ConicSag.FromRadius(radius, -1);
        for (int i = 0; i <= 10; i++)
        {
            double r = 0.04 * i / 10;
            Assert.Equal(radius - Math.Sqrt(radius * radius - r * r), sphere.SagAtRadius(r), 1e-15);
            Assert.Equal(r * r / (2 * radius), parabola.SagAtRadius(r), 1e-17);
        }
        Assert.Equal(radius, sphere.MaxDefinedRadius, 1e-17);
        Assert.True(double.IsNaN(sphere.SagAtRadius(radius * 1.001)));
        Assert.Equal(double.PositiveInfinity, parabola.MaxDefinedRadius);
        Assert.Equal(0.0, ConicSag.Plane.Sag(1, 2));
    }

    [Fact]
    public void Layout_RefusesAClearApertureBeyondTheSagDomain_AndMechanicalBelowClear()
    {
        var tooWide = new LayoutSurface(At(0), ConicSag.FromRadius(5 * Mm), 6 * Mm);
        var ex = Assert.Throws<ArgumentException>(() => new OpticalLayout(new[] { tooWide }));
        Assert.Contains("Surface 0", ex.Message);

        var badMech = new LayoutSurface(At(0), ConicSag.Plane, 6 * Mm) { MechanicalSemiDiameter = 5 * Mm };
        ex = Assert.Throws<ArgumentException>(() => new OpticalLayout(new[] { ConicSurface(), badMech }));
        Assert.Contains("Surface 1", ex.Message);
        Assert.Contains("mechanical", ex.Message);

        static LayoutSurface ConicSurface() => new(At(-1 * Mm), ConicSag.Plane, 1 * Mm);
    }

    // ---- Lathe ----

    /// <summary>The singlet's solid is closed and every triangle faces out: the front face and
    /// its flat toward −z, the back face and its flat toward +z, the edge radially out.</summary>
    [Fact]
    public void LatheSolid_OfASinglet_IsWatertight_WithOutwardNormals()
    {
        var layout = Singlet();
        var e = layout.Elements.Single();
        var mesh = LatheMesher.Solid(layout.Surfaces[0], layout.Surfaces[1], layout.EdgeSemiDiameter(e), new LatheResolution(12, 48));

        Assert.True(mesh.IsWatertight());
        Assert.True(mesh.ComputeSignedVolume() > 0);
        AssertOutward(mesh, frontNormalZSign: -1);
        var faces = mesh.TriangleFaceIds.Distinct().OrderBy(f => f).ToArray();
        Assert.Equal(new[] { LatheMesher.FrontFace, LatheMesher.FrontFlat, LatheMesher.BackFace, LatheMesher.BackFlat, LatheMesher.EdgeFace }, faces);
    }

    /// <summary>After a mirror the back face lies before the front (negative thickness): the
    /// solid is still closed and outward, now with the front face toward +z.</summary>
    [Fact]
    public void LatheSolid_WithNegativeThickness_TurnsItselfOutward()
    {
        var layout = Singlet(thicknessSign: -1);
        var e = layout.Elements.Single();
        var mesh = LatheMesher.Solid(layout.Surfaces[0], layout.Surfaces[1], layout.EdgeSemiDiameter(e), new LatheResolution(8, 32));
        Assert.True(mesh.IsWatertight());
        Assert.True(mesh.ComputeSignedVolume() > 0);
    }

    /// <summary>
    /// A plane-parallel window, with flats from 10 to 12 mm, against the volume of the prism
    /// it discretises: ½·n·E²·sin(2π/n)·t, the inscribed n-gon of the edge radius times the
    /// thickness (CONTRIBUTING.md's sharp form, not πE²t at a loose tolerance).
    /// </summary>
    [Fact]
    public void LatheSolid_OfAWindow_HasTheInscribedPrismVolume()
    {
        const int n = 40;
        const double t = 3 * Mm, edge = 12 * Mm;
        var front = new LayoutSurface(At(1 * Mm), ConicSag.Plane, 10 * Mm) { MechanicalSemiDiameter = edge };
        var back = new LayoutSurface(At(1 * Mm + t), ConicSag.Plane, 10 * Mm);
        var mesh = LatheMesher.Solid(front, back, edge, new LatheResolution(5, n));
        double expected = 0.5 * n * edge * edge * Math.Sin(2 * Math.PI / n) * t;
        Assert.True(mesh.IsWatertight());
        Assert.Equal(1.0, mesh.ComputeSignedVolume() / expected, 1e-12);
    }

    /// <summary>Every vertex of the lathe's front face sits on the conic sag inside the clear
    /// aperture, and on the flat (the clear-edge sag) beyond it.</summary>
    [Fact]
    public void LatheSolid_SamplesTheSag_AndFlatsSitAtTheClearEdgeSag()
    {
        var layout = Singlet();
        var front = layout.Surfaces[0];
        var sag = (ConicSag)front.Sag;
        var mesh = LatheMesher.Solid(front, layout.Surfaces[1], 12 * Mm, new LatheResolution(10, 36));
        var frontVertices = new HashSet<int>();
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            if (mesh.TriangleFaceIds[t] is not (LatheMesher.FrontFace or LatheMesher.FrontFlat)) continue;
            var tri = mesh.Triangles[t];
            frontVertices.Add(tri.A); frontVertices.Add(tri.B); frontVertices.Add(tri.C);
        }
        double edgeSag = sag.SagAtRadius(10 * Mm);
        int flatCount = 0;
        foreach (int v in frontVertices)
        {
            var p = mesh.Vertices[v];
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            if (r <= 10 * Mm * (1 + 1e-12))
                Assert.Equal(sag.SagAtRadius(r), p.Z, 1e-15);
            else
            {
                Assert.Equal(12 * Mm, r, 1e-15);
                Assert.Equal(edgeSag, p.Z, 1e-15);
                flatCount++;
            }
        }
        Assert.Equal(36, flatCount);
    }

    [Fact]
    public void Solids_OfADoublet_AreTwoClosedLenses_AndAnApertureAtTheStop()
    {
        var bodies = LayoutSolids.Build(Doublet(), new LatheResolution(6, 24));
        var lenses = bodies.Where(b => b.Kind == LayoutBodyKind.Lens).ToList();
        Assert.Equal(2, lenses.Count);
        Assert.All(lenses, b => Assert.True(b.IsClosed && b.Mesh.IsWatertight() && b.Mesh.ComputeSignedVolume() > 0));
        var aperture = Assert.Single(bodies, b => b.Kind == LayoutBodyKind.Aperture);
        Assert.Equal(0, aperture.Surface);
        Assert.True(lenses[0].Center.Z < lenses[1].Center.Z);
    }

    [Fact]
    public void Mirror_WithSubstrate_IsAClosedSolidBehindItsFace()
    {
        var mirror = new LayoutSurface(At(0), ConicSag.FromRadius(-100 * Mm, -1), 20 * Mm)
            { IsMirror = true, MirrorSubstrateThickness = 4 * Mm };
        var layout = new OpticalLayout(new[] { mirror });
        var body = Assert.Single(LayoutSolids.Build(layout, new LatheResolution(8, 32)));
        Assert.Equal(LayoutBodyKind.Mirror, body.Kind);
        Assert.True(body.IsClosed && body.Mesh.IsWatertight() && body.Mesh.ComputeSignedVolume() > 0);
        // The face is concave toward −z (it bends to −z at the rim); the back is 4 mm beyond
        // its farthest +z point, the vertex.
        Assert.Equal(4 * Mm, body.Mesh.Bounds.Max.Z, 1e-15);
        Assert.Equal(new ConicSag(-10, -1).SagAtRadius(20 * Mm), body.Mesh.Bounds.Min.Z, 1e-15);
    }

    // ---- Depth order ----

    [Fact]
    public void DepthOrder_IsFarthestFirst_FromEitherSide_AndStableOnTies()
    {
        var centers = new[] { new Vector3D(0, 0, 0), new Vector3D(0, 0, 0.01), new Vector3D(0, 0, 0.02) };
        Assert.Equal(new[] { 2, 1, 0 }, DepthOrder.BackToFront(centers, new Vector3D(0, 0, -1), Vector3D.UnitZ));
        Assert.Equal(new[] { 0, 1, 2 }, DepthOrder.BackToFront(centers, new Vector3D(0, 0, 1), -Vector3D.UnitZ));
        // Seen from the side all three are equally deep: input order is kept.
        Assert.Equal(new[] { 0, 1, 2 }, DepthOrder.BackToFront(centers, new Vector3D(1, 0, 0), -Vector3D.UnitX));
        // An oblique view: depth is the projection on the look direction, not the distance.
        var look = new Vector3D(1, 0, 1);
        Assert.Equal(new[] { 2, 1, 0 }, DepthOrder.BackToFront(centers, new Vector3D(-1, 0, -1), look));
    }

    private static void AssertOutward(TriangleMesh mesh, int frontNormalZSign)
    {
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            var n = mesh.TriangleNormal(t);
            var tri = mesh.Triangles[t];
            var c = (mesh.Vertices[tri.A] + mesh.Vertices[tri.B] + mesh.Vertices[tri.C]) / 3.0;
            switch (mesh.TriangleFaceIds[t])
            {
                case LatheMesher.FrontFace:
                case LatheMesher.FrontFlat:
                    Assert.True(n.Z * frontNormalZSign > 0, $"front triangle {t} faces {n}");
                    break;
                case LatheMesher.BackFace:
                case LatheMesher.BackFlat:
                    Assert.True(n.Z * frontNormalZSign < 0, $"back triangle {t} faces {n}");
                    break;
                default:
                    Assert.True(n.X * c.X + n.Y * c.Y > 0, $"edge triangle {t} faces {n}");
                    break;
            }
        }
    }
}
