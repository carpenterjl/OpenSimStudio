using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// The flux through a <see cref="FluxProbe"/>.
/// <para>
/// <b>Section</b>: the recovered flux q* integrated over the surface — the plane's cut
/// through every element it crosses (clipped to the probe's box), or the probe's boundary
/// triangles — by a triangle rule exact for it.
/// </para>
/// <para>
/// <b>Conservative</b> (a whole plane): with w the linear function that is 1 on the corner
/// nodes on the plane's positive side and 0 on the others, and H the step across the plane
/// itself, the divergence theorem gives the flux through the plane exactly as
/// Q = ∫q·∇w dV + ∫(w − H)·s dV − ∫(w − H)·q·n dS over the boundary, s being the source.
/// Every term lives in the band of elements the plane crosses. The first is evaluated with
/// the computed flux, the second exactly (the source is constant per element and the band's
/// volume beyond the plane is clipped exactly), the third — a boundary strip one element
/// wide — with the outflow the boundary conditions give wherever they give it (zero on an
/// insulated face, the uniform flow of a heat flow or a current, h·(T − T∞) of a
/// convection) and the recovered flux elsewhere. Nothing of it needs the plane to fall on
/// element faces.
/// </para>
/// <para>
/// Where the strip meets no fixed potential or temperature, the conservative value is the
/// solve's own discrete balance of the positive side — w lies in the element space — and
/// it converges at twice the order of the flux, like an electrode's current. A strip across
/// a fixed-value face weights that face's reactions with a step, which is not
/// superconvergent; the value is then about as accurate as the section's.
/// </para>
/// </summary>
internal static class FluxProbeEvaluator
{
    /// <summary>What a probe measured: the section value, the conservative one (whole planes
    /// only) and the area the section covered.</summary>
    public sealed record Result(FluxProbe Probe, double Section, double? Conservative, double Area)
    {
        /// <summary>The value to report: the conservative one where there is one.</summary>
        public double Value => Conservative ?? Section;
    }

    /// <summary>The outward flux q·n the boundary conditions give at a point of a boundary
    /// triangle (by index), or null where they do not (a fixed potential or temperature).</summary>
    public delegate double? KnownOutflow(int triangle, Vector3D point);

    public static Result Evaluate(FluxProbe probe, FluxRecovery recovery, ScalarDiffusionAssembler assembler,
        IReadOnlyList<double>? elementSource, KnownOutflow? known = null)
    {
        known ??= (_, _) => null;
        var mesh = assembler.Mesh;
        if (probe.FaceIds is { } faces)
        {
            var owners = Owners(mesh);
            var wanted = new HashSet<int>(faces);
            double flux = 0, area = 0;
            for (int ti = 0; ti < mesh.BoundaryTriangles.Count; ti++)
            {
                var t = mesh.BoundaryTriangles[ti];
                if (!wanted.Contains(t.FaceId)) continue;
                var p = new[] { mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C] };
                flux += IntegrateTriangle(p, Outflow(recovery, known, ti, owners[Key(t)]));
                area += ScalarSolverHelpers.TriangleArea(mesh, t);
            }
            return new Result(probe, flux, null, area);
        }

        var normal = probe.Normal.Normalized();
        var distance = new double[mesh.NodeCount];
        for (int n = 0; n < mesh.NodeCount; n++) distance[n] = Vector3D.Dot(mesh.Nodes[n] - probe.Origin, normal);

        double section = 0, sectionArea = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var polygon = Section(mesh, e, distance, normal);
            if (polygon is null) continue;
            if (probe.BoxMin is { } lo && probe.BoxMax is { } hi) polygon = ClipToBox(polygon, lo, hi);
            if (polygon.Count < 3) continue;
            int element = e;
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                var triangle = new[] { polygon[0], polygon[i], polygon[i + 1] };
                section += IntegrateTriangle(triangle,
                    x => Vector3D.Dot(recovery.RecoveredAt(element, recovery.Barycentric(element, x)), normal));
                sectionArea += Area(triangle);
            }
        }
        if (probe.BoxMin is not null) return new Result(probe, section, null, sectionArea);

        return new Result(probe, section,
            Conservative(mesh, assembler, recovery, distance, elementSource, known), sectionArea);
    }

    /// <summary>The outward flux over one boundary triangle: the conditions' where known, the
    /// recovered flux of the element behind it otherwise.</summary>
    private static Func<Vector3D, double> Outflow(FluxRecovery recovery, KnownOutflow known, int triangle,
        (int Element, Vector3D Outward) owner) =>
        x => known(triangle, x)
             ?? Vector3D.Dot(recovery.RecoveredAt(owner.Element, recovery.Barycentric(owner.Element, x)), owner.Outward);

    private static double Conservative(FeMesh mesh, ScalarDiffusionAssembler assembler, FluxRecovery recovery,
        double[] distance, IReadOnlyList<double>? elementSource, KnownOutflow known)
    {
        var centroid = new[] { 0.25, 0.25, 0.25, 0.25 };
        double flux = 0;
        Span<double> w = stackalloc double[4];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var t = mesh.Elements[e];
            int[] corners = { t.N0, t.N1, t.N2, t.N3 };
            for (int i = 0; i < 4; i++) w[i] = distance[corners[i]] > 0 ? 1 : 0;
            double sum = w[0] + w[1] + w[2] + w[3];
            if (sum is 0 or 4) continue;

            var g = assembler.BarycentricGradients(e);
            var gradW = g[0] * w[0] + g[1] * w[1] + g[2] * w[2] + g[3] * w[3];
            double volume = mesh.ElementVolume(e);
            // The computed flux is linear on a TET10, so its mean is its centroid value.
            flux += volume * Vector3D.Dot(recovery.ComputedAt(e, centroid), gradW);

            if (elementSource is not null && elementSource[e] != 0)
            {
                var p = corners.Select(n => mesh.Nodes[n]).ToArray();
                var d = corners.Select(n => distance[n]).ToArray();
                flux += elementSource[e] * (volume * sum / 4 - PositiveVolume(p, d));
            }
        }

        var owners = Owners(mesh);
        for (int ti = 0; ti < mesh.BoundaryTriangles.Count; ti++)
        {
            var t = mesh.BoundaryTriangles[ti];
            double wa = distance[t.A] > 0 ? 1 : 0, wb = distance[t.B] > 0 ? 1 : 0, wc = distance[t.C] > 0 ? 1 : 0;
            if (wa == wb && wb == wc) continue;
            var p = new[] { mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C] };
            var Normal = Outflow(recovery, known, ti, owners[Key(t)]);
            // ∫w·f over the triangle, w linear between its corners' values.
            double weighted = 0;
            double area = Area(p);
            foreach (var (l, weight) in ScalarElements.TriangleRule)
            {
                var x = p[0] * l[0] + p[1] * l[1] + p[2] * l[2];
                weighted += weight * area * (wa * l[0] + wb * l[1] + wc * l[2]) * Normal(x);
            }
            // ∫H·f: f over the part beyond the plane.
            double beyond = 0;
            foreach (var piece in PositivePart(p, new[] { distance[t.A], distance[t.B], distance[t.C] }))
                beyond += IntegrateTriangle(piece, Normal);
            flux -= weighted - beyond;
        }
        return flux;
    }

    /// <summary>∫f dA over one triangle by the degree-4 rule.</summary>
    private static double IntegrateTriangle(Vector3D[] p, Func<Vector3D, double> f)
    {
        double area = Area(p);
        if (!(area > 0)) return 0;
        double sum = 0;
        foreach (var (l, w) in ScalarElements.TriangleRule)
            sum += w * f(p[0] * l[0] + p[1] * l[1] + p[2] * l[2]);
        return sum * area;
    }

    private static double Area(Vector3D[] p) => 0.5 * Vector3D.Cross(p[1] - p[0], p[2] - p[0]).Length;

    /// <summary>Where the edge from a point beyond the plane (d &gt; 0) to one not beyond it
    /// (d ≤ 0) crosses the plane.</summary>
    private static Vector3D Crossing(Vector3D inside, double dInside, Vector3D outside, double dOutside) =>
        inside + (outside - inside) * (dInside / (dInside - dOutside));

    /// <summary>
    /// The plane's cut through one element, as a convex polygon in order; null when the
    /// element has no corner beyond the plane or none on or before it. A corner exactly on
    /// the plane counts as before it, so a face lying in the plane is counted once, by the
    /// element beyond it.
    /// </summary>
    private static List<Vector3D>? Section(FeMesh mesh, int element, double[] distance, Vector3D normal)
    {
        var t = mesh.Elements[element];
        int[] c = { t.N0, t.N1, t.N2, t.N3 };
        bool beyond = false, before = false;
        foreach (int n in c)
        {
            if (distance[n] > 0) beyond = true;
            else before = true;
        }
        if (!beyond || !before) return null;

        var points = new List<Vector3D>(4);
        foreach (int n in c)
            if (distance[n] == 0) points.Add(mesh.Nodes[n]);
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
            {
                double di = distance[c[i]], dj = distance[c[j]];
                if (di > 0 && dj < 0) points.Add(Crossing(mesh.Nodes[c[i]], di, mesh.Nodes[c[j]], dj));
                else if (dj > 0 && di < 0) points.Add(Crossing(mesh.Nodes[c[j]], dj, mesh.Nodes[c[i]], di));
            }
        if (points.Count < 3) return null;
        return Ordered(points, normal);
    }

    /// <summary>
    /// The points of a convex polygon in the plane of <paramref name="normal"/>, in order
    /// around their centroid. The plane's own normal, not one taken from the points: a corner
    /// a rounding error off the plane gives two coincident crossings, and a normal built from
    /// them can vanish.
    /// </summary>
    private static List<Vector3D> Ordered(List<Vector3D> points, Vector3D normal)
    {
        var centre = points.Aggregate(Vector3D.Zero, (a, b) => a + b) / points.Count;
        var far = points.MaxBy(p => (p - centre).LengthSquared);
        if (!((far - centre).LengthSquared > 0)) return points;
        var u = (far - centre).Normalized();
        var v = Vector3D.Cross(normal, u);
        return points.OrderBy(p => Math.Atan2(Vector3D.Dot(p - centre, v), Vector3D.Dot(p - centre, u))).ToList();
    }

    /// <summary>Sutherland–Hodgman against the six faces of an axis-aligned box.</summary>
    private static List<Vector3D> ClipToBox(List<Vector3D> polygon, Vector3D lo, Vector3D hi)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            polygon = Clip(polygon, p => p[axis] - lo[axis]);
            polygon = Clip(polygon, p => hi[axis] - p[axis]);
            if (polygon.Count < 3) break;
        }
        return polygon;
    }

    private static List<Vector3D> Clip(List<Vector3D> polygon, Func<Vector3D, double> inside)
    {
        var result = new List<Vector3D>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            double da = inside(a), db = inside(b);
            if (da >= 0) result.Add(a);
            if ((da >= 0) != (db >= 0)) result.Add(a + (b - a) * (da / (da - db)));
        }
        return result;
    }

    /// <summary>The triangles of the part of a triangle beyond the plane (d &gt; 0).</summary>
    private static IEnumerable<Vector3D[]> PositivePart(Vector3D[] p, double[] d)
    {
        var beyond = Enumerable.Range(0, 3).Where(i => d[i] > 0).ToList();
        switch (beyond.Count)
        {
            case 3:
                yield return p;
                break;
            case 1:
            {
                int i = beyond[0], j = (i + 1) % 3, k = (i + 2) % 3;
                yield return new[] { p[i], Crossing(p[i], d[i], p[j], d[j]), Crossing(p[i], d[i], p[k], d[k]) };
                break;
            }
            case 2:
            {
                int k = Enumerable.Range(0, 3).First(n => d[n] <= 0), i = (k + 1) % 3, j = (k + 2) % 3;
                var xi = Crossing(p[i], d[i], p[k], d[k]);
                var xj = Crossing(p[j], d[j], p[k], d[k]);
                yield return new[] { p[i], p[j], xj };
                yield return new[] { p[i], xj, xi };
                break;
            }
        }
    }

    /// <summary>
    /// The volume of the part of a tetrahedron beyond the plane (d &gt; 0), exactly: a
    /// tetrahedron cut off at one corner, the whole less one cut off at the other side, or a
    /// prism between two corners on each side, split into three tetrahedra.
    /// </summary>
    internal static double PositiveVolume(Vector3D[] p, double[] d)
    {
        var beyond = Enumerable.Range(0, 4).Where(i => d[i] > 0).ToList();
        var before = Enumerable.Range(0, 4).Where(i => d[i] <= 0).ToList();
        double whole = Tet(p[0], p[1], p[2], p[3]);
        switch (beyond.Count)
        {
            case 0: return 0;
            case 4: return whole;
            case 1:
            {
                int i = beyond[0];
                var x = before.Select(j => Crossing(p[i], d[i], p[j], d[j])).ToArray();
                return Tet(p[i], x[0], x[1], x[2]);
            }
            case 3:
            {
                int m = before[0];
                var x = beyond.Select(j => Crossing(p[j], d[j], p[m], d[m])).ToArray();
                return whole - Tet(p[m], x[0], x[1], x[2]);
            }
            default:
            {
                int i1 = beyond[0], i2 = beyond[1], m1 = before[0], m2 = before[1];
                var a = Crossing(p[i1], d[i1], p[m1], d[m1]);
                var b = Crossing(p[i1], d[i1], p[m2], d[m2]);
                var c = Crossing(p[i2], d[i2], p[m1], d[m1]);
                var e = Crossing(p[i2], d[i2], p[m2], d[m2]);
                // The prism with ends (i1, a, b) and (i2, c, e) and side edges i1–i2, a–c, b–e.
                return Tet(p[i1], a, b, p[i2]) + Tet(a, b, p[i2], c) + Tet(b, p[i2], c, e);
            }
        }
    }

    private static double Tet(Vector3D a, Vector3D b, Vector3D c, Vector3D d) =>
        Math.Abs(Vector3D.Dot(b - a, Vector3D.Cross(c - a, d - a))) / 6;

    private static (int, int, int) Key(BoundaryTriangle t) => Sorted(t.A, t.B, t.C);

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }

    /// <summary>The element behind every boundary triangle and the triangle's outward unit
    /// normal (pointing away from that element's fourth corner).</summary>
    private static Dictionary<(int, int, int), (int Element, Vector3D Outward)> Owners(FeMesh mesh)
    {
        var wanted = new HashSet<(int, int, int)>(mesh.BoundaryTriangles.Select(Key));
        var owners = new Dictionary<(int, int, int), (int, Vector3D)>(wanted.Count);
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var t = mesh.Elements[e];
            int[] c = { t.N0, t.N1, t.N2, t.N3 };
            for (int skip = 0; skip < 4; skip++)
            {
                int a = c[(skip + 1) % 4], b = c[(skip + 2) % 4], d = c[(skip + 3) % 4];
                var key = Sorted(a, b, d);
                if (!wanted.Contains(key)) continue;
                var normal = Vector3D.Cross(mesh.Nodes[b] - mesh.Nodes[a], mesh.Nodes[d] - mesh.Nodes[a]).Normalized();
                if (Vector3D.Dot(normal, mesh.Nodes[a] - mesh.Nodes[c[skip]]) < 0) normal = -normal;
                owners[key] = (e, normal);
            }
        }
        return owners;
    }
}
