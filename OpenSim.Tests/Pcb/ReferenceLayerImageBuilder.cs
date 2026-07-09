using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Excellon;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The ORIGINAL sequential layer-image fold, kept verbatim as the polarity-semantics
/// oracle: image = fold over ops in file order (dark → union of the whole accumulated
/// image plus the batch, clear → subtract the batch from the image). The production
/// <see cref="LayerImageBuilder"/> restructured this into the distributive suffix
/// composition ⋃ᵢ(Dᵢ − clears-after-i) for speed; the equivalence tests pin the two
/// against each other on synthetic polarity gauntlets and real boards. Never shipped —
/// test-only. Do not "modernize" it: its value is being the old algorithm.
/// </summary>
public sealed class ReferenceLayerImageBuilder
{
    private readonly IPolygonOps _ops;
    private readonly double _chordTolerance;

    private readonly Dictionary<MacroAperture, IReadOnlyList<Polygon2>> _flattened =
        new(ReferenceEqualityComparer.Instance);

    public ReferenceLayerImageBuilder(IPolygonOps ops, double chordTolerance = 5e-6)
    {
        _ops = ops;
        _chordTolerance = chordTolerance;
    }

    public LayerImage Build(GerberDocument document, DrillFile? drills = null)
    {
        var warnings = new List<string>(document.Warnings);
        var image = new List<Polygon2>();
        var batch = new List<IReadOnlyList<Point2>>();
        GerberPolarity batchPolarity = GerberPolarity.Dark;

        void Flush()
        {
            if (batch.Count == 0) return;
            image = batchPolarity == GerberPolarity.Dark
                ? _ops.Union(image.SelectMany(AllRings).Concat(batch)).ToList()
                : _ops.Difference(image, batch).ToList();
            batch.Clear();
        }

        foreach (var op in document.Ops)
        {
            if (op.Polarity != batchPolarity)
            {
                Flush();
                batchPolarity = op.Polarity;
            }
            batch.AddRange(Polygonize(op, warnings));
        }
        Flush();

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
                .Reverse().ToList();
    }

    private IEnumerable<IReadOnlyList<Point2>> StrokeDraw(DrawOp draw, List<string> warnings)
    {
        switch (draw.Aperture)
        {
            case CircleAperture c:
                return _ops.StrokeOpenPath(draw.Path, c.Diameter / 2);

            case RectangleAperture r:
                return EnumerateSegments(draw.Path)
                    .Select(s => RectangleSweep(s.From, s.To, r.Width, r.Height));

            default:
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

    private static IEnumerable<(Point2 From, Point2 To)> EnumerateSegments(IReadOnlyList<Point2> path)
    {
        for (int i = 1; i < path.Count; i++)
            yield return (path[i - 1], path[i]);
    }

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
        return OpenSim.Pcb.Geometry2D.ConvexHull.Compute(corners);
    }

    private static IEnumerable<IReadOnlyList<Point2>> AllRings(Polygon2 polygon)
    {
        yield return polygon.Outer;
        foreach (var hole in polygon.Holes)
            yield return hole;
    }
}
