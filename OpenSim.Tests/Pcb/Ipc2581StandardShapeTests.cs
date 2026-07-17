using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Ipc2581;
using Xunit;
using Dict = OpenSim.Pcb.Ipc2581.Ipc2581PrimitiveDictionary;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The full IPC-2581B EntryStandard shape family, gated analytically in the house
/// benchmark style: polygonal shapes match their closed-form areas at 1e-12; curved
/// shapes are INSCRIBED tessellations, so their gates are one-sided bands against the
/// ideal area (polygon ≤ ideal, within the chord-tolerance deficit) — sharp against the
/// discretization model, never a loosened two-sided tolerance where one side is exact.
/// </summary>
public class Ipc2581StandardShapeTests
{
    private const double Mm = 1e-3;

    private static double RingArea(IReadOnlyList<Point2> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    // ---------------- exact polygonal shapes (1e-12) ----------------

    [Fact]
    public void RectCham_AllCorners_IsExact()
    {
        double w = 3 * Mm, h = 2 * Mm, c = 0.4 * Mm;
        var p = Ipc2581StandardShapes.RectCham(w, h, c, true, true, true, true);
        Assert.Equal(w * h - 2 * c * c, p.Area(), w * h * 1e-12);
        Assert.Equal(8, p.Outer.Count);
    }

    [Fact]
    public void RectCham_SingleCorner_CutsExactlyHalfCSquared()
    {
        double w = 3 * Mm, h = 2 * Mm, c = 0.4 * Mm;
        var p = Ipc2581StandardShapes.RectCham(w, h, c, upperLeft: true,
            upperRight: false, lowerLeft: false, lowerRight: false);
        Assert.Equal(w * h - c * c / 2, p.Area(), w * h * 1e-12);
    }

    [Fact]
    public void RectRound_AllCornersFalse_IsThePlainRectangle()
    {
        // KiCad writes all-false rounded rects — exactly a rectangle (measured).
        double w = 16.686710 * Mm, h = 11.124472 * Mm;
        var p = Ipc2581StandardShapes.RectRound(w, h, 0.108345 * Mm, false, false, false, false);
        Assert.Equal(4, p.Outer.Count);
        Assert.Equal(w * h, p.Area(), w * h * 1e-12);
    }

    [Fact]
    public void RectCorner_IsExact_AndStaysAsymmetric()
    {
        // Corner-defined: offsets from the flash location, NOT centered.
        var p = Ipc2581StandardShapes.RectCorner(0.5 * Mm, -0.25 * Mm, 2.5 * Mm, 0.75 * Mm);
        Assert.Equal(2 * Mm * 1 * Mm, p.Area(), 1e-18);
        Assert.All(p.Outer, v => Assert.True(v.X >= 0.5 * Mm - 1e-15));
    }

    [Fact]
    public void Diamond_Triangle_AreExact()
    {
        Assert.Equal(3 * Mm * 2 * Mm / 2,
            Ipc2581StandardShapes.Diamond(3 * Mm, 2 * Mm).Area(), 1e-18);
        Assert.Equal(3 * Mm * 2 * Mm / 2,
            Ipc2581StandardShapes.Triangle(3 * Mm, 2 * Mm).Area(), 1e-18);
    }

    [Fact]
    public void Hexagon_Octagon_MatchTheirClosedForms()
    {
        // Hexagon: length = vertex-to-vertex ⇒ A = (3√3/8)L². Octagon: length =
        // across-flats ⇒ A = 2(√2−1)L². Both exact (no curved boundary).
        double L = 2 * Mm;
        Assert.Equal(3 * Math.Sqrt(3) / 8 * L * L,
            Ipc2581StandardShapes.Hexagon(L).Area(), L * L * 1e-12);
        Assert.Equal(2 * (Math.Sqrt(2) - 1) * L * L,
            Ipc2581StandardShapes.Octagon(L).Area(), L * L * 1e-12);
        // Octagon flats face the axes: the bounding box IS the across-flats size.
        var oct = Ipc2581StandardShapes.Octagon(L).Outer;
        Assert.Equal(L / 2, oct.Max(p => p.X), L * 1e-12);
        Assert.Equal(L / 2, oct.Max(p => p.Y), L * 1e-12);
    }

    [Fact]
    public void ButterflySquare_IsExactlyHalfTheSquare()
    {
        var pieces = Ipc2581StandardShapes.Butterfly(round: false, 2 * Mm);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(2 * Mm * 2 * Mm / 2, pieces.Sum(p => p.Area()), 1e-18);
        // Opposite quadrants: bounding boxes on opposite sides of the origin.
        Assert.All(pieces[0].Outer, p => Assert.True(p.X >= -1e-15 && p.Y >= -1e-15));
        Assert.All(pieces[1].Outer, p => Assert.True(p.X <= 1e-15 && p.Y <= 1e-15));
    }

    [Fact]
    public void DonutSquare_HasTheExactRingArea_AndARealHole()
    {
        var p = Ipc2581StandardShapes.Donut("SQUARE", 3 * Mm, 1.5 * Mm);
        Assert.Single(p.Holes);
        Assert.Equal(3 * Mm * 3 * Mm - 1.5 * Mm * 1.5 * Mm, p.Area(), 1e-18);
    }

    // ---------------- curved shapes: SHARP against the discretization model ----------------
    // Curved boundaries are inscribed chord tessellations, so the expected areas below
    // are the exact polygon areas of that model (the STEP-importer benchmark style):
    // a full circle of n segments has area ½nR²sin(2π/n); an arc of sweep θ split into
    // s chords keeps (R²/2)·s·sin(θ/s) of its (R²/2)θ sector. n and s reproduce the
    // generators' own step rules from the public tolerance constants.

    /// <summary>The chord count AppendArc uses for an arc of the given radius and sweep.</summary>
    private static int ArcSteps(double radius, double sweep)
    {
        double maxStep = 2 * Math.Acos(Math.Max(0.0, 1 - OpenSim.Pcb.Ipc2581.PolyShapeReader.ChordTolerance / radius));
        return Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(maxStep, 1e-4)));
    }

    /// <summary>Exact area of the inscribed n-gon of a circle of radius R.</summary>
    private static double InscribedCircleArea(double radius) =>
        0.5 * OpenSim.Pcb.Gerber.ApertureShapes.SegmentCount(radius, OpenSim.Pcb.Ipc2581.PolyShapeReader.ChordTolerance)
            * radius * radius
            * Math.Sin(2 * Math.PI / OpenSim.Pcb.Gerber.ApertureShapes.SegmentCount(radius, OpenSim.Pcb.Ipc2581.PolyShapeReader.ChordTolerance));

    [Fact]
    public void RectRound_AllCorners_MatchesTheChordModelExactly()
    {
        double w = 4 * Mm, h = 3 * Mm, r = 1 * Mm;
        int s = ArcSteps(r, Math.PI / 2);
        // Sharp corner square r² per corner is replaced by the inscribed quarter-arc fan.
        double cornerKept = r * r / 2 * s * Math.Sin(Math.PI / 2 / s);
        double expected = w * h - 4 * (r * r - cornerKept);
        double area = Ipc2581StandardShapes.RectRound(w, h, r, true, true, true, true).Area();
        Assert.Equal(expected, area, expected * 1e-12);
        Assert.True(area < w * h - (4 - Math.PI) * r * r, "chords must inscribe the ideal fillet");
    }

    [Fact]
    public void Ellipse_MatchesTheUniformParameterPolygonExactly()
    {
        double w = 3 * Mm, h = 1.5 * Mm;
        int n = OpenSim.Pcb.Gerber.ApertureShapes.SegmentCount(w / 2,
            OpenSim.Pcb.Ipc2581.PolyShapeReader.ChordTolerance);
        // Uniform-parameter sampling of an ellipse: every consecutive cross product is
        // ab·sin(2π/n), so the polygon area is (ab/2)·n·sin(2π/n) — exact.
        double expected = w / 2 * (h / 2) / 2 * n * Math.Sin(2 * Math.PI / n);
        double area = Ipc2581StandardShapes.Ellipse(w, h).Area();
        Assert.Equal(expected, area, expected * 1e-12);
        Assert.True(area < Math.PI * w * h / 4);
    }

    [Fact]
    public void ButterflyRound_IsTwoExactQuarterDiskFans()
    {
        double d = 2 * Mm, r = d / 2;
        var pieces = Ipc2581StandardShapes.Butterfly(round: true, d);
        Assert.Equal(2, pieces.Count);
        int s = ArcSteps(r, Math.PI / 2);
        double expected = 2 * (r * r / 2 * s * Math.Sin(Math.PI / 2 / s));
        Assert.Equal(expected, pieces.Sum(p => p.Area()), expected * 1e-12);
    }

    [Fact]
    public void DonutRound_NetsTheExactInscribedAnnulus()
    {
        double R = 1.5 * Mm, r = 0.75 * Mm;
        var p = Ipc2581StandardShapes.Donut("ROUND", 2 * R, 2 * r);
        Assert.Single(p.Holes);
        double expected = InscribedCircleArea(R) - InscribedCircleArea(r);
        Assert.Equal(expected, p.Area(), expected * 1e-12);
    }

    [Fact]
    public void Thermal_BreaksIntoSpokeCountSegments_WithTheAnalyticCutArea()
    {
        double R = 1 * Mm, r = 0.5 * Mm, gap = 0.2 * Mm;
        const int spokes = 4;
        var pieces = Ipc2581StandardShapes.Thermal("ROUND", 2 * R, 2 * r, spokes, gap, 45);
        Assert.Equal(spokes, pieces.Count);

        // Expected = the inscribed annulus minus the analytic strip∩annulus cut per gap:
        // F(ρ) = a√(ρ²−a²) + ρ²·asin(a/ρ), a = g/2 (the exact strip∩disk area). The
        // only residual is chord-vs-arc inside the gap width (≲ gap·sagitta, ~1e-6 rel)
        // and the boolean engine's 1 nm grid.
        double F(double rho) { double a = gap / 2; return a * Math.Sqrt(rho * rho - a * a) + rho * rho * Math.Asin(a / rho); }
        double expected = InscribedCircleArea(R) - InscribedCircleArea(r) - spokes * (F(R) - F(r));
        Assert.Equal(expected, pieces.Sum(p => p.Area()), expected * 1e-4);
    }

    [Fact]
    public void Moire_RingsOnly_AreDisjointAnnuli()
    {
        // 3 rings stepping inward from d=4mm, width 0.3, gap 0.3; no crosshair.
        var pieces = Ipc2581StandardShapes.Moire(4 * Mm, 0.3 * Mm, 0.3 * Mm, 3, 0, 0, 0);
        Assert.Equal(3, pieces.Count);
        double expected = 0;
        double outer = 2 * Mm;
        for (int i = 0; i < 3; i++)
        {
            double inner = outer - 0.3 * Mm;
            expected += InscribedCircleArea(outer) - InscribedCircleArea(inner);
            outer = inner - 0.3 * Mm;
        }
        // Union passes through the boolean engine's 1 nm grid — vertex-snap noise only.
        Assert.Equal(expected, pieces.Sum(p => p.Area()), expected * 1e-5);

        // The crosshair overlaps the rings — union strictly grows the copper.
        var withCross = Ipc2581StandardShapes.Moire(4 * Mm, 0.3 * Mm, 0.3 * Mm, 3, 0.2 * Mm, 5 * Mm, 30);
        Assert.True(withCross.Sum(p => p.Area()) > pieces.Sum(p => p.Area()));
    }

    // ---------------- placement: mirror before rotation ----------------

    [Fact]
    public void Flash_RotateThenMirror_MapsVerticesExactly()
    {
        // The IPC-2581 Xform order is ROTATE first, then mirror (x → −x) — pinned by the
        // real-board oracle (rotate-then-mirror matches 1689/1689 instantiated Cadence
        // pads; mirror-then-rotate only 523/1417 on the mirrored side). Asymmetric
        // L-contour: local (1,3) → rotate 90° CCW (−3,1) → mirror (3,1) → translate
        // (10,5) ⇒ (13,6); the wrong order would give mirror(−1,3) → rotate (−3,−1)
        // ⇒ (7,4) — the orders disagree, so the gate is decisive.
        var contour = new Dict.ContourPrimitive(new Polygon2(new[]
        {
            new Point2(0, 0), new Point2(2 * Mm, 0), new Point2(2 * Mm, 1 * Mm),
            new Point2(1 * Mm, 1 * Mm), new Point2(1 * Mm, 3 * Mm), new Point2(0, 3 * Mm)
        }));
        var loc = new Point2(10 * Mm, 5 * Mm);
        var placed = Assert.Single(Dict.Flash(contour, loc, 90, mirror: true));

        Assert.Contains(placed.Outer, p =>
            Math.Abs(p.X - 13 * Mm) < 1e-15 && Math.Abs(p.Y - 6 * Mm) < 1e-15);
        // Local (2,0) → rotate (0,2) → mirror (0,2) → (10,7).
        Assert.Contains(placed.Outer, p =>
            Math.Abs(p.X - 10 * Mm) < 1e-15 && Math.Abs(p.Y - 7 * Mm) < 1e-15);
        // Mirroring flips the winding; the placement restores the CCW convention.
        Assert.True(RingArea(placed.Outer) > 0);
        // Area is transform-invariant.
        var unmirrored = Assert.Single(Dict.Flash(contour, loc, 90));
        Assert.Equal(unmirrored.Area(), placed.Area(), 1e-18);
    }

    [Fact]
    public void Flash_MultiPiecePrimitive_PlacesEveryPiece()
    {
        var thermal = new Dict.PiecesPrimitive(
            Ipc2581StandardShapes.Thermal("ROUND", 2 * Mm, 1 * Mm, 4, 0.2 * Mm, 0));
        var loc = new Point2(3 * Mm, -2 * Mm);
        var placed = Dict.Flash(thermal, loc, 30);
        Assert.Equal(4, placed.Count);
        // Every piece lands centered around the flash location (within the outer
        // radius + the boolean engine's 1 nm vertex grid the Thermal pieces carry).
        Assert.All(placed, piece => Assert.All(piece.Outer,
            p => Assert.True((p - loc).Length <= 1 * Mm + 2e-9)));
    }
}
