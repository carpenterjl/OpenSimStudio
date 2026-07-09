using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Schema;

namespace OpenSim.Geometry.Step.Tessellate;

/// <summary>One edge's shared 3D sampling, ordered from the edge's Start to its End.</summary>
internal sealed record EdgePolyline(IReadOnlyList<Vector3D> Points, IReadOnlyList<double> Params);

/// <summary>
/// Samples every EDGE_CURVE exactly once — the watertightness cornerstone: both adjacent
/// faces receive the SAME Vector3D instances, so welding stitches the shared boundary
/// bitwise-exactly. Endpoints are snapped to the VERTEX_POINT coordinates verbatim, and
/// a minimum-spacing floor of 20× the weld tolerance guarantees no two distinct edge
/// samples can ever weld together (the weld/sampling tolerance firewall).
/// </summary>
internal sealed class EdgeTessellator
{
    private readonly StepImportOptions _options;
    private readonly double _tol;
    private readonly double _minSpacing;
    private readonly Dictionary<int, EdgePolyline> _memo = new();

    /// <summary>Interior samples merged by the spacing floor (reported once per import).</summary>
    public int FloorMerges { get; private set; }

    public EdgeTessellator(StepImportOptions options, double chordTolerance, double minSpacing)
    {
        _options = options;
        _tol = chordTolerance;
        _minSpacing = minSpacing;
    }

    public EdgePolyline Tessellate(StepEdge edge)
    {
        if (_memo.TryGetValue(edge.Id, out var cached)) return cached;
        var result = Build(edge);
        _memo[edge.Id] = result;
        return result;
    }

    private EdgePolyline Build(StepEdge edge)
    {
        var curve = edge.Curve;

        // A ring shorter than the spacing floor is a point-like feature (degenerate pole
        // circle): contribute the endpoints only.
        if (curve is StepCircle tiny && tiny.Radius < _minSpacing)
            return new EdgePolyline(new[] { edge.Start.Point, edge.End.Point }, new[] { 0.0, 0.0 });

        double t0 = curve.ParameterOf(edge.Start.Point);
        double t1;
        if (edge.IsRing)
        {
            double period = curve.Period ?? throw new StepGeometryException(
                $"edge #{edge.Id}: start and end vertices coincide but the curve is not periodic");
            t1 = t0 + (edge.CurveSameSense ? period : -period);
        }
        else
        {
            t1 = curve.ParameterOf(edge.End.Point);
            if (curve.Period is double p)
            {
                // The edge runs with (same_sense) or against the curve parameterization;
                // unwrap the end parameter onto the matching side of t0.
                if (edge.CurveSameSense) while (t1 <= t0 + 1e-12 * p) t1 += p;
                else while (t1 >= t0 - 1e-12 * p) t1 -= p;
            }
        }

        var (points, parameters) = Sample(edge, curve, t0, t1);

        // Endpoints verbatim: every edge meeting a vertex shares that exact point.
        points[0] = edge.Start.Point;
        points[^1] = edge.IsRing ? edge.Start.Point : edge.End.Point;

        ApplySpacingFloor(points, parameters, out var keptPoints, out var keptParams);
        return new EdgePolyline(keptPoints, keptParams);
    }

    private (Vector3D[] Points, double[] Params) Sample(StepEdge edge, StepCurve curve, double t0, double t1)
    {
        switch (curve)
        {
            case StepLine:
                return (new[] { curve.Point(t0), curve.Point(t1) }, new[] { t0, t1 });

            case StepCircle or StepEllipse:
            {
                double r = curve is StepCircle c ? c.Radius
                    : Math.Max(((StepEllipse)curve).SemiAxis1, ((StepEllipse)curve).SemiAxis2);
                double maxAngle = Math.Min(
                    2 * Math.Acos(Math.Clamp(1 - _tol / r, -1, 1)),
                    _options.MaxAnglePerSegment);
                if (maxAngle <= 0) maxAngle = _options.MaxAnglePerSegment;
                double sweep = Math.Abs(t1 - t0);
                int n = Math.Max(2, (int)Math.Ceiling(sweep / maxAngle));
                if (edge.IsRing) n = Math.Max(_options.MinSegmentsPerCircle, n);
                Guard(edge, n);
                var pts = new Vector3D[n + 1];
                var prm = new double[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    prm[i] = t0 + (t1 - t0) * i / n; // uniform in angle — rings align exactly
                    pts[i] = curve.Point(prm[i]);
                }
                return (pts, prm);
            }

            default:
            {
                // Adaptive deterministic bisection on sagitta + tangent turn angle.
                var pts = new List<Vector3D> { curve.Point(t0) };
                var prm = new List<double> { t0 };
                Refine(edge, curve, t0, t1, pts[0], curve.Point(t1), depth: 0, pts, prm);
                pts.Add(curve.Point(t1));
                prm.Add(t1);
                return (pts.ToArray(), prm.ToArray());
            }
        }
    }

    private void Refine(StepEdge edge, StepCurve curve, double t0, double t1,
        Vector3D p0, Vector3D p1, int depth, List<Vector3D> pts, List<double> prm)
    {
        Guard(edge, pts.Count);
        double tm = 0.5 * (t0 + t1);
        var pm = curve.Point(tm);

        bool split;
        if (depth >= 24)
        {
            split = false; // depth bound: 2^24 ≫ MaxSegmentsPerEdge, Guard fires first
        }
        else
        {
            double sagitta = DistanceToSegment(pm, p0, p1);
            var a = pm - p0;
            var b = p1 - pm;
            double turn = a.Length > 0 && b.Length > 0
                ? Math.Acos(Math.Clamp(Vector3D.Dot(a, b) / (a.Length * b.Length), -1, 1))
                : 0;
            split = sagitta > _tol || turn > _options.MaxAnglePerSegment;
            // Always split at least once so a symmetric curve cannot hide behind a chord
            // whose midpoint happens to lie on it.
            if (depth == 0) split = true;
        }

        if (split)
        {
            Refine(edge, curve, t0, tm, p0, pm, depth + 1, pts, prm);
            pts.Add(pm);
            prm.Add(tm);
            Refine(edge, curve, tm, t1, pm, p1, depth + 1, pts, prm);
        }
    }

    private void Guard(StepEdge edge, int count)
    {
        if (count > _options.MaxSegmentsPerEdge)
            throw new StepGeometryException(
                $"edge #{edge.Id}: refinement exceeded {_options.MaxSegmentsPerEdge} segments — " +
                "the curve is pathologically tight relative to the model size");
    }

    private static double DistanceToSegment(Vector3D p, Vector3D a, Vector3D b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 > 0 ? Math.Clamp(Vector3D.Dot(p - a, ab) / len2, 0, 1) : 0;
        return Vector3D.Distance(p, a + ab * t);
    }

    private void ApplySpacingFloor(IReadOnlyList<Vector3D> points, IReadOnlyList<double> parameters,
        out IReadOnlyList<Vector3D> keptPoints, out IReadOnlyList<double> keptParams)
    {
        var pts = new List<Vector3D> { points[0] };
        var prm = new List<double> { parameters[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            if (Vector3D.Distance(points[i], pts[^1]) < _minSpacing)
            {
                FloorMerges++;
                continue;
            }
            pts.Add(points[i]);
            prm.Add(parameters[i]);
        }
        // The end vertex is inviolable — if the last kept interior sample crowds it, drop
        // that sample instead.
        if (points.Count > 1)
        {
            if (pts.Count > 1 && Vector3D.Distance(points[^1], pts[^1]) < _minSpacing
                              && !points[^1].Equals(pts[^1]))
            {
                pts.RemoveAt(pts.Count - 1);
                prm.RemoveAt(prm.Count - 1);
                FloorMerges++;
            }
            pts.Add(points[^1]);
            prm.Add(parameters[^1]);
        }
        keptPoints = pts;
        keptParams = prm;
    }
}
