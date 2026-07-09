using OpenSim.Pcb.Excellon;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Polygons;

/// <summary>The final 2D copper image of one layer: polygons with holes, in meters.</summary>
public sealed class LayerImage
{
    public required IReadOnlyList<Polygon2> Polygons { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public double TotalArea() => Polygons.Sum(p => p.Area());
}

/// <summary>
/// Composes a parsed Gerber layer into its copper image. The Gerber semantics are the
/// sequential fold over ops in file order (dark → union, clear → subtract), but that
/// fold re-processes the whole accumulated image on every polarity flip — O(flips ×
/// image-vertices) on pour layers with thermal reliefs. Difference distributes over
/// union, so the fold is IDENTICALLY  image = ⋃ᵢ (Dᵢ − Sᵢ)  where Sᵢ is every clear
/// ring occurring AFTER dark batch i: each dark batch is cut once by its clear suffix
/// and the results are unioned once. A later dark re-fills an earlier clear because
/// that clear is not in its suffix — ordering semantics are preserved exactly. (The
/// original fold survives as the tests' ReferenceLayerImageBuilder oracle.)
/// </summary>
public sealed class LayerImageBuilder
{
    private readonly IPolygonOps _ops;
    private readonly double _chordTolerance;

    // One aperture flashes hundreds of times: flatten its exact footprint once at the
    // origin and translate per flash. Keyed by instance — codes may be redefined.
    private readonly Dictionary<MacroAperture, IReadOnlyList<Polygon2>> _flattened =
        new(ReferenceEqualityComparer.Instance);

    public LayerImageBuilder(IPolygonOps ops, double chordTolerance = 5e-6)
    {
        _ops = ops;
        _chordTolerance = chordTolerance;
    }

    public LayerImage Build(GerberDocument document, DrillFile? drills = null)
    {
        var warnings = new List<string>(document.Warnings);

        // 1. Polygonize into ordered same-polarity batches (the same batch boundaries
        //    the sequential fold used). Draws stay stroked ONE PER TRACE on purpose:
        //    batching same-radius paths into one InflatePaths call was tried and
        //    reverted — the offset engine pre-unions overlapping capsules and emits
        //    boundary vertex lists that differ from the boolean engine's (same region,
        //    different vertices/counts), which breaks the bitwise-geometry equivalence
        //    gate against the reference fold. A ~2× boolean-stage win is not worth
        //    giving up vertex-exact reproducibility.
        var batches = new List<(GerberPolarity Polarity, List<IReadOnlyList<Point2>> Rings)>();
        var batch = new List<IReadOnlyList<Point2>>();
        GerberPolarity batchPolarity = GerberPolarity.Dark;
        foreach (var op in document.Ops)
        {
            if (op.Polarity != batchPolarity)
            {
                if (batch.Count > 0)
                {
                    batches.Add((batchPolarity, batch));
                    batch = new List<IReadOnlyList<Point2>>();
                }
                batchPolarity = op.Polarity;
            }
            batch.AddRange(Polygonize(op, warnings));
        }
        if (batch.Count > 0) batches.Add((batchPolarity, batch));

        // 2. Each dark batch's clear suffix = every clear ring appended AFTER its
        //    position: one flat clear list plus a start index per dark batch. A hole
        //    ring always travels inside its outer, so NonZero winding over the raw
        //    suffix rings IS the union of the clear regions — no pre-union needed.
        var flatClears = new List<IReadOnlyList<Point2>>();
        var darkBatches = new List<(List<IReadOnlyList<Point2>> Rings, int ClearsFrom)>();
        foreach (var (polarity, rings) in batches)
        {
            if (polarity == GerberPolarity.Dark) darkBatches.Add((rings, flatClears.Count));
            else flatClears.AddRange(rings);
        }

        // 3. Cut each dark batch by its suffix (one boolean over just that batch, never
        //    the accumulated image), then union everything once. A batch with no later
        //    clears passes straight through — an all-dark signal layer costs exactly
        //    the single final union.
        var composed = new List<IReadOnlyList<Point2>>();
        foreach (var (rings, clearsFrom) in darkBatches)
        {
            if (clearsFrom == flatClears.Count)
                composed.AddRange(rings);
            else
                composed.AddRange(_ops.DifferenceRings(
                    rings, flatClears.Skip(clearsFrom)));
        }
        var image = composed.Count == 0
            ? new List<Polygon2>()
            : _ops.Union(composed).ToList();

        if (drills is not null && (drills.Hits.Count > 0 || drills.Slots.Count > 0))
        {
            warnings.AddRange(drills.Warnings);
            var cuts = drills.Hits
                .Select(h => ApertureShapes.Circle(h.Position, h.Diameter / 2, _chordTolerance))
                .Concat(drills.Slots
                    .Select(s => ApertureShapes.Capsule(s.Start, s.End, s.Diameter / 2, _chordTolerance)));
            image = _ops.Difference(image, cuts).ToList();
        }

        return new LayerImage { Polygons = CanonicalOrder(image), Warnings = warnings };
    }

    /// <summary>
    /// Deterministic output ordering by geometric key rather than PolyTree walk order.
    /// Island order assigns CopperIsland ids downstream, so it is a CONTRACT: this sort
    /// makes it independent of how the booleans were staged (batching, suffix
    /// composition, parallelism) — only the geometry itself determines the order.
    /// </summary>
    private static List<Polygon2> CanonicalOrder(List<Polygon2> polygons)
    {
        static (double MinX, double MinY, int Count, double Area) KeyOf(IReadOnlyList<Point2> ring)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
            }
            return (minX, minY, ring.Count, Math.Abs(Polygon2.RingArea(ring)));
        }

        return polygons
            .Select(p => p.Holes.Count <= 1
                ? p
                : new Polygon2(p.Outer, p.Holes.OrderBy(h => KeyOf(h)).ToList()))
            .OrderBy(p => KeyOf(p.Outer))
            .ToList();
    }

    private IEnumerable<IReadOnlyList<Point2>> Polygonize(GerberOp op, List<string> warnings)
    {
        switch (op)
        {
            case FlashOp flash:
                foreach (var ring in FlashRings(flash))
                    yield return ring;
                break;

            case RegionOp region:
                foreach (var contour in region.Contours)
                    yield return contour;
                break;

            case DrawOp draw:
                foreach (var ring in StrokeDraw(draw, warnings))
                    yield return ring;
                break;
        }
    }

    private IEnumerable<IReadOnlyList<Point2>> StrokeDraw(DrawOp draw, List<string> warnings)
    {
        switch (draw.Aperture)
        {
            case CircleAperture c:
                return _ops.StrokeOpenPath(draw.Path, c.Diameter / 2);

            case RectangleAperture r:
                // A rectangle aperture sweeps the convex hull of its footprint at both
                // segment ends (KiCad only draws with circles; this covers the spec).
                return EnumerateSegments(draw.Path)
                    .Select(s => RectangleSweep(s.From, s.To, r.Width, r.Height));

            default:
                // Drawing with special apertures is deprecated in the spec — stroke as a
                // circle of the footprint size, with a warning (never silently exact).
                double diameter = draw.Aperture switch
                {
                    ObroundAperture o => Math.Max(o.Width, o.Height),
                    PolygonAperture p => p.OuterDiameter,
                    MacroAperture m => m.BoundingSize,
                    UnsupportedAperture u => u.ApproximateDiameter,
                    _ => throw new NotSupportedException($"Draw with {draw.Aperture.GetType().Name}.")
                };
                warnings.Add($"Draw with aperture D{draw.Aperture.Code} " +
                             $"({draw.Aperture.GetType().Name}) stroked as a {diameter * 1e3:g3} mm circle.");
                return _ops.StrokeOpenPath(draw.Path, diameter / 2);
        }
    }

    /// <summary>
    /// The rings one flash contributes to the boolean batch. A macro flash contributes
    /// its exact flattened footprint (outer rings CCW, hole rings CW — NonZero fill
    /// honors the reverse winding in both dark unions and clear subtractions); standard
    /// apertures with a hole parameter contribute outline + reversed hole circle.
    /// </summary>
    private IEnumerable<IReadOnlyList<Point2>> FlashRings(FlashOp flash)
    {
        if (flash.Aperture is MacroAperture macro)
        {
            if (!_flattened.TryGetValue(macro, out var polygons))
            {
                polygons = MacroFlattener.Flatten(macro, _ops, _chordTolerance);
                _flattened[macro] = polygons;
            }
            foreach (var polygon in polygons)
                foreach (var ring in Polygon2.OrientedRings(polygon))
                    yield return ring.Select(p => p + flash.Position).ToList();
            yield break;
        }

        yield return ApertureShapes.Outline(flash.Aperture, flash.Position, _chordTolerance);

        double? hole = flash.Aperture switch
        {
            CircleAperture c => c.HoleDiameter,
            RectangleAperture r => r.HoleDiameter,
            ObroundAperture o => o.HoleDiameter,
            PolygonAperture p => p.HoleDiameter,
            _ => null
        };
        if (hole is > 0)
            yield return ApertureShapes.Circle(flash.Position, hole.Value / 2, _chordTolerance)
                .Reverse().ToList();                              // CW ⇒ a hole under NonZero fill
    }

    private static IEnumerable<(Point2 From, Point2 To)> EnumerateSegments(IReadOnlyList<Point2> path)
    {
        for (int i = 1; i < path.Count; i++)
            yield return (path[i - 1], path[i]);
    }

    /// <summary>Convex hull of an axis-aligned w×h rectangle placed at both segment endpoints.</summary>
    private static IReadOnlyList<Point2> RectangleSweep(Point2 a, Point2 b, double w, double h)
    {
        var corners = new List<Point2>(8);
        foreach (var c in new[] { a, b })
        {
            corners.Add(new Point2(c.X - w / 2, c.Y - h / 2));
            corners.Add(new Point2(c.X + w / 2, c.Y - h / 2));
            corners.Add(new Point2(c.X + w / 2, c.Y + h / 2));
            corners.Add(new Point2(c.X - w / 2, c.Y + h / 2));
        }
        return ConvexHull.Compute(corners);
    }

}
