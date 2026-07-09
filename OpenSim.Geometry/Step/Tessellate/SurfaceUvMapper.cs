using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Schema;

namespace OpenSim.Geometry.Step.Tessellate;

/// <summary>One boundary ring in metric-scaled UV with its verbatim 3D points in parallel.</summary>
internal sealed record UvRing(List<Point2> Uv, List<Vector3D> Points3D);

/// <summary>
/// Maps a face's boundary loops into the surface's UV space. The three structural rules
/// that make seams and poles safe: (1) periodic branches are selected by CONTINUITY along
/// the ordered loop — a seam edge visited twice lands on the 0 and 2π branches
/// automatically; (2) parameterization poles (sphere pole, cone apex), where u is
/// undefined, take u from their loop neighbours and keep their single 3D point verbatim;
/// (3) UV is metrically scaled per surface (radians × radius) so triangulation quality
/// and Steiner sizing work in consistent length units.
/// </summary>
internal static class SurfaceUvMapper
{
    public static (List<UvRing> Rings, double ScaleU, double ScaleV) Map(
        StepFace face, Func<StepEdge, EdgePolyline> edges, StepImportOptions options, double chordTol,
        double inversionAcceptTol)
    {
        var surface = face.Surface;
        var edgeBounds = face.Bounds.Where(b => !b.Loop.IsVertexLoop).ToList();
        if (edgeBounds.Count == 0)
            return SynthesizeFullDomain(face, options, chordTol);

        // Pass 1: raw UV per ring (u may be NaN at poles), unwrapped by continuity.
        var raw = new List<(List<double> U, List<double> V, List<Vector3D> P)>();
        foreach (var bound in edgeBounds)
        {
            var points = AssembleLoopPoints(bound.Loop, edges, face.Id);
            var u = new List<double>(points.Count);
            var v = new List<double>(points.Count);
            if (surface is StepBSplineSurface spline)
            {
                InvertBSplineLoop(spline, points, face.Id, inversionAcceptTol, u, v);
            }
            else
            {
                foreach (var p in points)
                {
                    var (pu, pv) = surface.InvertRaw(p);
                    u.Add(pu);
                    v.Add(pv);
                }
            }
            Unwrap(u, surface.PeriodU, face.Id);
            Unwrap(v, surface.PeriodV, face.Id);
            FillPoles(u, face.Id);
            CheckClosure(u, surface.PeriodU, face.Id);
            CheckClosure(v, surface.PeriodV, face.Id);
            raw.Add((u, v, points));
        }

        var (scaleU, scaleV) = MetricScales(surface, raw);

        var rings = new List<UvRing>();
        foreach (var (u, v, p) in raw)
        {
            var ring = new UvRing(new List<Point2>(u.Count), new List<Vector3D>(u.Count));
            for (int i = 0; i < u.Count; i++)
            {
                ring.Uv.Add(new Point2(u[i] * scaleU, v[i] * scaleV));
                ring.Points3D.Add(p[i]);
            }
            SubdividePoleSegments(ring);
            rings.Add(ring);
        }
        return (rings, scaleU, scaleV);
    }

    /// <summary>
    /// Ordered 3D points around a loop: each oriented edge contributes its shared polyline
    /// in traversal direction, excluding its final point (the next edge's first).
    /// </summary>
    private static List<Vector3D> AssembleLoopPoints(StepLoop loop, Func<StepEdge, EdgePolyline> edges, int faceId)
    {
        var points = new List<Vector3D>();
        foreach (var use in loop.Edges)
        {
            var poly = edges(use.Edge).Points;
            if (use.Forward)
                for (int i = 0; i < poly.Count - 1; i++) points.Add(poly[i]);
            else
                for (int i = poly.Count - 1; i > 0; i--) points.Add(poly[i]);
        }
        if (points.Count < 3)
            throw new StepGeometryException($"face #{faceId}: boundary loop #{loop.Id} has fewer than 3 samples");
        return points;
    }

    /// <summary>
    /// Gauss-Newton inversion for a B-spline face's loop: the first sample seeds from a
    /// deterministic evaluation grid over the knot domain, each subsequent sample from
    /// its predecessor (continuity makes 2-3 iterations typical). The fallback ladder is
    /// grid seed → 4× finer grid → loud failure; a silently wrong UV is never returned.
    /// </summary>
    private static void InvertBSplineLoop(StepBSplineSurface spline, List<Vector3D> points,
        int faceId, double acceptTol, List<double> u, List<double> v)
    {
        (double U, double V)? previous = null;
        foreach (var p in points)
        {
            var result = previous is { } prev ? spline.InvertNear(p, prev.U, prev.V, acceptTol) : null;
            if (result is null)
            {
                var seed = GridSeed(spline, p, refine: 1);
                result = spline.InvertNear(p, seed.U, seed.V, acceptTol);
            }
            if (result is null)
            {
                var seed = GridSeed(spline, p, refine: 4);
                result = spline.InvertNear(p, seed.U, seed.V, acceptTol);
            }
            if (result is not { } uv)
                throw new StepGeometryException(FormattableString.Invariant(
                    $"face #{faceId}: B-spline surface #{spline.Id} inversion did not converge for boundary point ({p.X}, {p.Y}, {p.Z}) — the edge geometry disagrees with the surface"));
            u.Add(uv.U);
            v.Add(uv.V);
            previous = uv;
        }
    }

    /// <summary>Nearest node of a deterministic evaluation grid over the knot domain.</summary>
    private static (double U, double V) GridSeed(StepBSplineSurface s, Vector3D p, int refine)
    {
        int nu = Math.Max(8, 2 * (s.CountU - s.DegreeU)) * refine;
        int nv = Math.Max(8, 2 * (s.CountV - s.DegreeV)) * refine;
        double bestU = s.DomainStartU, bestV = s.DomainStartV, bestD = double.MaxValue;
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                double gu = s.DomainStartU + (s.DomainEndU - s.DomainStartU) * i / nu;
                double gv = s.DomainStartV + (s.DomainEndV - s.DomainStartV) * j / nv;
                double d = Vector3D.DistanceSquared(s.Point(gu, gv), p);
                if (d < bestD)
                {
                    bestD = d;
                    bestU = gu;
                    bestV = gv;
                }
            }
        return (bestU, bestV);
    }

    /// <summary>First non-NaN sample keeps the canonical branch; each later sample moves to the branch nearest its predecessor.</summary>
    private static void Unwrap(List<double> values, double? period, int faceId)
    {
        if (period is not double p) return;
        double? prev = null;
        for (int i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            if (prev is double q) values[i] += p * Math.Round((q - values[i]) / p);
            prev = values[i];
        }
    }

    /// <summary>Pole samples (NaN u) interpolate u linearly between their non-NaN loop neighbours (cyclic).</summary>
    private static void FillPoles(List<double> u, int faceId)
    {
        int n = u.Count;
        if (!u.Any(double.IsNaN)) return;
        if (u.All(double.IsNaN))
            throw new StepGeometryException($"face #{faceId}: every boundary sample sits at a parameterization pole");

        for (int i = 0; i < n; i++)
        {
            if (!double.IsNaN(u[i])) continue;
            // Find the enclosing non-NaN neighbours of the maximal NaN run containing i.
            int before = i;
            while (double.IsNaN(u[(before - 1 + n) % n])) before--;
            int after = i;
            while (double.IsNaN(u[(after + 1) % n])) after++;
            int lo = (before - 1 + n) % n, hi = (after + 1) % n;
            int runLength = after - before + 1;
            int offset = i - before + 1;
            double t = (double)offset / (runLength + 1);
            u[i] = u[lo] + (u[hi] - u[lo]) * t;
        }
    }

    /// <summary>
    /// After unwrapping, a loop must close in UV: a net winding of ±period means the face
    /// is bounded around the periodic direction with no seam edge — a topology v1 does not
    /// mesh (OCC-lineage exporters always write seams).
    /// </summary>
    private static void CheckClosure(List<double> values, double? period, int faceId)
    {
        if (period is not double p || values.Count == 0) return;
        double gap = Math.Abs(values[0] - values[^1]);
        // Consecutive unwrapped steps are small; the closing step must be too.
        if (gap > 0.5 * p)
            throw new StepGeometryException(
                $"face #{faceId}: boundary winds around the periodic direction without a seam " +
                "edge — this exporter topology is not supported in v1");
    }

    private static (double U, double V) MetricScales(
        StepSurface surface,
        List<(List<double> U, List<double> V, List<Vector3D> P)> rings)
    {
        switch (surface)
        {
            case StepPlane:
                return (1, 1);
            case StepCylinder cyl:
                return (cyl.Radius, 1);
            case StepSphere sph:
                return (sph.Radius, sph.Radius);
            case StepTorus tor:
                return (tor.MajorRadius + tor.MinorRadius, tor.MinorRadius);
            case StepCone cone:
            {
                // Radius varies with v: scale u by the mean boundary radius so the seam
                // rectangle is roughly square in metric terms; v by the slant factor.
                double sum = 0;
                int count = 0;
                foreach (var (_, v, _) in rings)
                    foreach (double vi in v)
                    {
                        sum += Math.Abs(cone.Radius + vi * Math.Tan(cone.SemiAngle));
                        count++;
                    }
                double mean = count > 0 ? Math.Max(sum / count, 1e-12) : 1;
                return (mean, 1.0 / Math.Cos(cone.SemiAngle));
            }
            default:
            {
                // Generic (extrusion, B-spline): mean first-partial magnitudes over the
                // boundary samples make each scaled axis approximately arc length.
                double su = 0, sv = 0;
                int n = 0;
                foreach (var (u, v, _) in rings)
                    for (int i = 0; i < u.Count; i++)
                    {
                        su += surface.PartialU(u[i], v[i]).Length;
                        sv += surface.PartialV(u[i], v[i]).Length;
                        n++;
                    }
                return (Math.Max(su / Math.Max(n, 1), 1e-12), Math.Max(sv / Math.Max(n, 1), 1e-12));
            }
        }
    }

    /// <summary>
    /// A boundary segment whose two endpoints carry the SAME 3D point (a pole crossing)
    /// can span a wide UV gap; subdividing it (each inserted node carrying the pole point
    /// verbatim) gives the triangulator fan constraints of normal size.
    /// </summary>
    private static void SubdividePoleSegments(UvRing ring)
    {
        int n = ring.Uv.Count;
        if (n < 3) return;
        double meanSpacing = 0;
        for (int i = 0; i < n; i++)
            meanSpacing += (ring.Uv[(i + 1) % n] - ring.Uv[i]).Length;
        meanSpacing /= n;
        if (meanSpacing <= 0) return;

        var uv = new List<Point2>(n);
        var p3 = new List<Vector3D>(n);
        for (int i = 0; i < n; i++)
        {
            uv.Add(ring.Uv[i]);
            p3.Add(ring.Points3D[i]);
            int j = (i + 1) % n;
            if (!ring.Points3D[i].Equals(ring.Points3D[j])) continue;
            double gap = (ring.Uv[j] - ring.Uv[i]).Length;
            int extra = (int)Math.Floor(gap / (2 * meanSpacing));
            for (int k = 1; k <= extra; k++)
            {
                double t = (double)k / (extra + 1);
                uv.Add(ring.Uv[i] + (ring.Uv[j] - ring.Uv[i]) * t);
                p3.Add(ring.Points3D[i]); // the pole's 3D point, verbatim
            }
        }
        ring.Uv.Clear();
        ring.Uv.AddRange(uv);
        ring.Points3D.Clear();
        ring.Points3D.AddRange(p3);
    }

    /// <summary>
    /// A face whose bounds are all vertex loops covers its surface's full domain (full
    /// sphere, full torus). The synthetic rectangle boundary constructs the identified
    /// sides by COPYING the partner side's 3D points verbatim — never re-evaluating
    /// cos(2π) and hoping it equals cos(0) — so welding closes the seam exactly.
    /// </summary>
    private static (List<UvRing>, double, double) SynthesizeFullDomain(
        StepFace face, StepImportOptions options, double chordTol)
    {
        switch (face.Surface)
        {
            case StepSphere sphere:
            {
                double r = sphere.Radius;
                int nu = SegmentsForSweep(2 * Math.PI, r, options, chordTol, ring: true);
                int nv = Math.Max(2, SegmentsForSweep(Math.PI, r, options, chordTol, ring: false));
                var southPole = sphere.Point(0, -Math.PI / 2);
                var northPole = sphere.Point(0, Math.PI / 2);
                var seam = new Vector3D[nv + 1]; // the u = 0 meridian, evaluated once
                for (int j = 0; j <= nv; j++)
                    seam[j] = sphere.Point(0, -Math.PI / 2 + Math.PI * j / nv);
                seam[0] = southPole;
                seam[^1] = northPole;

                var ring = new UvRing(new List<Point2>(), new List<Vector3D>());
                void Add(double u, double v, Vector3D p)
                {
                    ring.Uv.Add(new Point2(u * r, v * r));
                    ring.Points3D.Add(p);
                }

                for (int i = 0; i < nu; i++) // bottom row: all the south pole, u ascending
                    Add(2 * Math.PI * i / nu, -Math.PI / 2, southPole);
                for (int j = 0; j <= nv; j++) // right column u = 2π: copies of the seam
                    Add(2 * Math.PI, -Math.PI / 2 + Math.PI * j / nv, seam[j]);
                for (int i = nu - 1; i >= 0; i--) // top row: all the north pole, u descending
                    Add(2 * Math.PI * i / nu, Math.PI / 2, northPole);
                for (int j = nv - 1; j >= 1; j--) // left column u = 0: the same seam points
                    Add(0, -Math.PI / 2 + Math.PI * j / nv, seam[j]);
                return (new List<UvRing> { ring }, r, r);
            }
            case StepTorus torus:
            {
                double su = torus.MajorRadius + torus.MinorRadius, sv = torus.MinorRadius;
                int nu = SegmentsForSweep(2 * Math.PI, su, options, chordTol, ring: true);
                int nv = SegmentsForSweep(2 * Math.PI, sv, options, chordTol, ring: true);
                var bottom = new Vector3D[nu + 1]; // the v = 0 ring, evaluated once
                for (int i = 0; i <= nu; i++) bottom[i] = torus.Point(2 * Math.PI * i / nu, 0);
                bottom[^1] = bottom[0];
                var side = new Vector3D[nv + 1]; // the u = 0 ring, evaluated once
                for (int j = 0; j <= nv; j++) side[j] = torus.Point(0, 2 * Math.PI * j / nv);
                side[^1] = side[0];

                var ring = new UvRing(new List<Point2>(), new List<Vector3D>());
                void Add(double u, double v, Vector3D p)
                {
                    ring.Uv.Add(new Point2(u * su, v * sv));
                    ring.Points3D.Add(p);
                }

                for (int i = 0; i < nu; i++) Add(2 * Math.PI * i / nu, 0, bottom[i]);
                for (int j = 0; j < nv; j++) Add(2 * Math.PI, 2 * Math.PI * j / nv, side[j]);
                for (int i = nu; i > 0; i--) Add(2 * Math.PI * i / nu, 2 * Math.PI, bottom[i]);
                for (int j = nv; j > 0; j--) Add(0, 2 * Math.PI * j / nv, side[j]);
                return (new List<UvRing> { ring }, su, sv);
            }
            default:
                throw new StepGeometryException(
                    $"face #{face.Id}: vertex-loop-only bounds on a {face.Surface.GetType().Name} — " +
                    "only full spheres and tori have a bounded full domain");
        }
    }

    private static int SegmentsForSweep(double sweep, double radius, StepImportOptions options,
        double chordTol, bool ring)
    {
        // The same sagitta + angle rule the edge tessellator uses for circles.
        double maxAngle = Math.Min(
            2 * Math.Acos(Math.Clamp(1 - chordTol / radius, -1, 1)),
            options.MaxAnglePerSegment);
        if (maxAngle <= 0) maxAngle = options.MaxAnglePerSegment;
        int n = Math.Max(2, (int)Math.Ceiling(sweep / maxAngle));
        return ring ? Math.Max(options.MinSegmentsPerCircle, n) : n;
    }
}
