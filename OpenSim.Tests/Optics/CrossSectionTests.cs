using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Layout;
using static OpenSim.Tests.Optics.LayoutFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>OP-8: the 2-D YZ cross-section, its edges and flats, rays, fletches and the pixel
/// mapping.</summary>
public sealed class CrossSectionTests
{
    /// <summary>A sphere's sag by elementary geometry, R − sign(R)·√(R² − r²): an oracle
    /// independent of the rationalised form the code uses.</summary>
    private static double SphereSag(double radius, double r) =>
        radius - Math.Sign(radius) * Math.Sqrt(radius * radius - r * r);

    /// <summary>
    /// The singlet's outline: one closed line, the front face on the sphere sag out to the
    /// clear 10 mm, a flat at the clear-edge sag from 10 to 12 mm, the edge at 12 mm, and the
    /// back face (clear 10 mm, flat to the body's 12 mm edge) on its own sphere.
    /// </summary>
    [Fact]
    public void Singlet_Outline_FollowsTheSag_WithFlatsAndEdge()
    {
        const int n = 16;
        var drawing = CrossSection.Build(Singlet(), new CrossSectionOptions { SamplesPerSemiDiameter = n });
        var outline = Assert.Single(drawing.Lines, l => l.Kind == LayoutLineKind.LensOutline);
        Assert.True(outline.Closed);
        int perFace = 2 * (n + 1) + 1;                     // ±(n clear samples + flat rim) and the vertex
        Assert.Equal(2 * perFace, outline.Points.Count);

        double clear = 10 * Mm, edge = 12 * Mm, t = 5 * Mm;
        var front = outline.Points.Take(perFace).ToList();
        var back = outline.Points.Skip(perFace).ToList();
        foreach (var p in front)
        {
            double r = Math.Abs(p.Y);
            double expected = r <= clear * (1 + 1e-12) ? SphereSag(50 * Mm, r) : SphereSag(50 * Mm, clear);
            Assert.Equal(expected, p.X, 1e-15);
        }
        foreach (var p in back)
        {
            double r = Math.Abs(p.Y);
            double expected = t + (r <= clear * (1 + 1e-12) ? SphereSag(-50 * Mm, r) : SphereSag(-50 * Mm, clear));
            Assert.Equal(expected, p.X, 1e-15);
        }

        // Front runs −edge → +edge, back +edge → −edge; closing the line draws the two edges.
        Assert.Equal(-edge, front[0].Y, 1e-15);
        Assert.Equal(edge, front[^1].Y, 1e-15);
        Assert.Equal(edge, back[0].Y, 1e-15);
        Assert.Equal(-edge, back[^1].Y, 1e-15);
        // The flats: the last two front points share z (10 mm and 12 mm at the clear-edge sag).
        Assert.Equal(front[^2].X, front[^1].X);
        Assert.Equal(clear, front[^2].Y, 1e-15);

        var layout = Singlet();
        double edgeThickness = t + SphereSag(-50 * Mm, clear) - SphereSag(50 * Mm, clear);
        Assert.Equal(edgeThickness, layout.EdgeThickness(layout.Elements.Single()), 1e-15);
        Assert.Equal(edgeThickness, back[0].X - front[^1].X, 1e-15);
        Assert.Empty(layout.Warnings);
    }

    /// <summary>
    /// The plan's gate: the drawn edge of a known prescription matches the sag function to a
    /// pixel. The front face's clear-edge point, mapped to pixels in an 800 × 400 view, lies
    /// within half a pixel of the exact sag point mapped the same way, and the view keeps the
    /// aspect ratio (one scale on both axes).
    /// </summary>
    [Fact]
    public void Singlet_DrawnEdge_MatchesTheSag_ToAPixel()
    {
        var drawing = CrossSection.Build(Singlet());
        var view = LayoutViewport2D.Fit(drawing.Bounds, 800, 400);
        var outline = drawing.Lines.Single(l => l.Kind == LayoutLineKind.LensOutline);
        double clear = 10 * Mm;
        var drawn = outline.Points.First(p => Math.Abs(p.Y - clear) < 1e-15);
        var exact = new Point2(SphereSag(50 * Mm, clear), clear);
        var a = view.ToPixel(drawn);
        var b = view.ToPixel(exact);
        Assert.True((a - b).Length < 0.5, $"drawn edge is {(a - b).Length:g3} px from the sag");

        // Equal scale: a 1 mm step in y and in z is the same number of pixels.
        var o = view.ToPixel(new Point2(0, 0));
        Assert.Equal((view.ToPixel(new Point2(Mm, 0)) - o).Length, (view.ToPixel(new Point2(0, Mm)) - o).Length, 1e-9);
        // Everything fits inside the 8 px margin.
        foreach (var p in outline.Points)
        {
            var px = view.ToPixel(p);
            Assert.InRange(px.X, 8 - 1e-9, 792 + 1e-9);
            Assert.InRange(px.Y, 8 - 1e-9, 392 + 1e-9);
        }
        Assert.Equal(drawn, view.FromPixel(view.ToPixel(drawn)), new PointComparer(1e-14));
    }

    [Fact]
    public void Doublet_DrawsTwoBodies_SharingTheCementedFace_AndApertureBlades()
    {
        var layout = Doublet();
        Assert.Equal(new[] { new LensElement(1, 2, "N-BK7"), new LensElement(2, 3, "N-SF5") }, layout.Elements);
        var drawing = CrossSection.Build(layout);
        var outlines = drawing.Lines.Where(l => l.Kind == LayoutLineKind.LensOutline).ToList();
        Assert.Equal(2, outlines.Count);

        // The first body's back profile is the second body's front profile, point for point.
        int perFace = outlines[0].Points.Count / 2;
        var firstBack = outlines[0].Points.Skip(perFace).Reverse().ToList();
        var secondFront = outlines[1].Points.Take(perFace).ToList();
        Assert.Equal(firstBack, secondFront);

        var blades = drawing.Lines.Where(l => l.Kind == LayoutLineKind.Aperture).ToList();
        Assert.Equal(2, blades.Count);
        Assert.All(blades, b => Assert.Equal(8 * Mm, Math.Abs(b.Points[0].Y), 1e-15));
        Assert.All(blades, b => Assert.Equal(LayoutGeometry.DefaultApertureRatio * 8 * Mm, Math.Abs(b.Points[1].Y), 1e-15));
        // The image plane is a lone surface.
        Assert.Single(drawing.Lines, l => l.Kind == LayoutLineKind.Surface && l.Surface == 4);
    }

    [Fact]
    public void ThinBiconvex_WhoseFacesCross_IsDrawnWithAWarning()
    {
        var layout = new OpticalLayout(new[]
        {
            new LayoutSurface(At(0), ConicSag.FromRadius(20 * Mm), 10 * Mm) { MaterialAfter = "N-BK7" },
            new LayoutSurface(At(2 * Mm), ConicSag.FromRadius(-20 * Mm), 10 * Mm),
        });
        var warning = Assert.Single(layout.Warnings);
        Assert.Contains("Surfaces 0 and 1", warning);
        Assert.Single(CrossSection.Build(layout).Lines, l => l.Kind == LayoutLineKind.LensOutline);
    }

    [Fact]
    public void Rays_AreColouredByFieldOrWavelength_AndVignettedRaysCanBeDeleted()
    {
        var rays = new[]
        {
            Ray(field: 0, wavelength: 2, RayStatus.Ok),
            Ray(field: 1, wavelength: 0, RayStatus.Ok),
            Ray(field: 2, wavelength: 1, RayStatus.Vignetted),
        };
        var layout = Doublet(rays);

        var byField = CrossSection.Build(layout).Lines.Where(l => l.Kind == LayoutLineKind.Ray).Select(l => l.ColorKey);
        Assert.Equal(new[] { 0, 1, 2 }, byField);
        var byWave = CrossSection.Build(layout, new CrossSectionOptions { Coloring = RayColoring.ByWavelength })
            .Lines.Where(l => l.Kind == LayoutLineKind.Ray).Select(l => l.ColorKey);
        Assert.Equal(new[] { 2, 0, 1 }, byWave);
        var kept = CrossSection.Build(layout, new CrossSectionOptions { DeleteVignetted = true })
            .Lines.Where(l => l.Kind == LayoutLineKind.Ray).Select(l => l.ColorKey);
        Assert.Equal(new[] { 0, 1 }, kept);

        // A ray is seen from the side: global (x, y, z) is drawn at (z, y).
        var line = CrossSection.Build(layout).Lines.First(l => l.Kind == LayoutLineKind.Ray);
        Assert.Equal(new Point2(rays[0].Points[1].Z, rays[0].Points[1].Y), line.Points[1]);

        static LayoutRayPath Ray(int field, int wavelength, RayStatus status) => new(new[]
        {
            new Vector3D(0.001, 1 * Mm * field, -20 * Mm),
            new Vector3D(0.001, 1 * Mm * field, 0),
            new Vector3D(0.001, 0, 100 * Mm),
        }, field, wavelength, status);
    }

    /// <summary>An arrow sits at the middle of the segment, its two arms of the asked length
    /// pointing back from the tip at 25° either side.</summary>
    [Fact]
    public void Fletch_IsAnArrowheadAtTheSegmentMiddle()
    {
        var arrow = CrossSection.Fletch(new Point2(0, 0), new Point2(2, 2), 0.1)!;
        Assert.Equal(3, arrow.Length);
        Assert.Equal(new Point2(1, 1), arrow[1]);
        var u = new Point2(1, 1) * (1 / Math.Sqrt(2));
        foreach (var arm in new[] { arrow[0], arrow[2] })
        {
            var d = arrow[1] - arm;
            Assert.Equal(0.1, d.Length, 1e-15);
            Assert.Equal(Math.Cos(25 * Math.PI / 180), Point2.Dot(d, u) / d.Length, 1e-14);
        }
        Assert.True(Point2.Cross(u, arrow[1] - arrow[0]) * Point2.Cross(u, arrow[1] - arrow[2]) < 0);
        Assert.Null(CrossSection.Fletch(new Point2(1, 1), new Point2(1, 1), 0.1));

        var rays = new[] { new LayoutRayPath(new[] { new Vector3D(0, 0, -10 * Mm), new Vector3D(0, 0, 0), new Vector3D(0, 0, 50 * Mm) }, 0, 0) };
        int first = CrossSection.Build(Doublet(rays), new CrossSectionOptions { Fletch = FletchMode.FirstSegment })
            .Lines.Count(l => l.Kind == LayoutLineKind.Fletch);
        int every = CrossSection.Build(Doublet(rays), new CrossSectionOptions { Fletch = FletchMode.EverySegment })
            .Lines.Count(l => l.Kind == LayoutLineKind.Fletch);
        Assert.Equal(1, first);
        Assert.Equal(2, every);
    }

    /// <summary>The cross-section's profile and the lathe's meridian are the same points: a
    /// lens drawn in 2-D lies exactly on the 3-D body seen from the side.</summary>
    [Fact]
    public void CrossSection_LiesOnTheLatheMeridian()
    {
        const int n = 12;
        var layout = Singlet();
        var outline = CrossSection.Build(layout, new CrossSectionOptions { SamplesPerSemiDiameter = n })
            .Lines.Single(l => l.Kind == LayoutLineKind.LensOutline);
        var mesh = LatheMesher.Solid(layout.Surfaces[0], layout.Surfaces[1], 12 * Mm, new LatheResolution(n, 32));
        var meridian = mesh.Vertices.Where(v => v.X == 0).Select(v => new Point2(v.Z, v.Y)).ToHashSet();
        Assert.All(outline.Points, p => Assert.Contains(p, meridian));
    }

    [Fact]
    public void MirrorWithSubstrate_IsAClosedOutline_StopOnly_HasNoSurfaceLine()
    {
        var layout = new OpticalLayout(new[]
        {
            new LayoutSurface(At(-50 * Mm), ConicSag.Plane, 10 * Mm) { IsStop = true },
            new LayoutSurface(At(0), ConicSag.FromRadius(-200 * Mm), 15 * Mm) { IsMirror = true, MirrorSubstrateThickness = 3 * Mm },
        });
        var lines = CrossSection.Build(layout).Lines;
        var mirror = Assert.Single(lines, l => l.Kind == LayoutLineKind.Mirror);
        Assert.True(mirror.Closed);
        Assert.Equal(3 * Mm, mirror.Points[^1].X, 1e-15);
        Assert.DoesNotContain(lines, l => l.Kind == LayoutLineKind.Surface);
        Assert.Equal(2, lines.Count(l => l.Kind == LayoutLineKind.Aperture));
    }

    private sealed class PointComparer(double tolerance) : IEqualityComparer<Point2>
    {
        public bool Equals(Point2 a, Point2 b) => (a - b).Length <= tolerance;
        public int GetHashCode(Point2 p) => 0;
    }
}
