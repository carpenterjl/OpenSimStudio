using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Schema;

namespace OpenSim.Geometry.Step.Tessellate;

/// <summary>
/// Triangulates one face: boundary rings (metric-scaled UV) + an interior Steiner lattice
/// sized by curvature, one constrained Delaunay run, even-odd containment, then 3D lift.
/// Two invariants carry watertightness and correctness through this stage:
/// boundary nodes' 3D output is the stored edge sample looked up by index — Cdt2D's
/// internal jitter never touches shared boundary geometry; and each triangle's winding is
/// decided against the surface normal × same_sense × shell orientation BEFORE anything
/// global, because the final signed-volume flip can only rescue an all-inward soup,
/// never a mixed one.
/// </summary>
internal static class FaceTessellator
{
    /// <summary>Interior lattice budget per face — pathological curvature (a cone apex)
    /// densifies locally; the cap keeps the lattice bounded with a note, never a hang.</summary>
    private const int MaxLatticePoints = 20_000;

    public static void Tessellate(
        StepFace face, bool flipWinding, Func<StepEdge, EdgePolyline> edges,
        StepImportOptions options, double chordTol, double minSpacing, double inversionAcceptTol,
        List<Vector3D> soup, List<int> soupFaceIds, int faceIndex, List<string> notes)
    {
        var surface = face.Surface;
        var (rings, scaleU, scaleV) = SurfaceUvMapper.Map(face, edges, options, chordTol, inversionAcceptTol);

        // Boundary points and constraints (each ring closes on itself).
        var points = new List<Point2>();
        var boundary3D = new List<Vector3D>();
        var constraints = new List<(int U, int V)>();
        var segments = new List<(Point2 A, Point2 B)>();
        foreach (var ring in rings)
        {
            int start = points.Count;
            points.AddRange(ring.Uv);
            boundary3D.AddRange(ring.Points3D);
            for (int i = 0; i < ring.Uv.Count; i++)
            {
                int a = start + i, b = start + (i + 1) % ring.Uv.Count;
                constraints.Add((a, b));
                segments.Add((points[a], points[b]));
            }
        }
        int boundaryCount = points.Count;

        // PolygonSetIndex is a UNION over polygons, so rings must arrive as one polygon
        // with holes — passing each ring separately would fill every hole. The outer ring
        // is the one with the largest |area| (a face has exactly one outer bound and its
        // holes lie inside it).
        int outerRing = 0;
        double bestArea = -1;
        for (int i = 0; i < rings.Count; i++)
        {
            double area = Math.Abs(Polygon2.RingArea(rings[i].Uv));
            if (area > bestArea)
            {
                bestArea = area;
                outerRing = i;
            }
        }

        // Containment for LATTICE candidates — safely interior, unjittered coordinates fine.
        var latticeInside = new PolygonSetIndex(new[] { AsPolygon(rings.Select(r => r.Uv).ToList(), outerRing) });

        // Interior Steiner lattice, row-major and jitter-free here (Cdt2D adds its own).
        if (options.InteriorRefinement && MinCurvatureRadius(face, rings, scaleU, scaleV) is double rMin)
        {
            double spacing = Math.Sqrt(8 * chordTol * rMin);
            spacing = Math.Max(spacing, 4 * minSpacing);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
            double area = (maxX - minX) * (maxY - minY);
            if (area / (spacing * spacing) > MaxLatticePoints)
            {
                spacing = Math.Sqrt(area / MaxLatticePoints);
                notes.Add($"face {faceIndex} (#{face.Id}): interior sampling coarsened to stay " +
                          $"within {MaxLatticePoints} points");
            }

            var grid = new SegmentGrid(segments, spacing);
            for (double y = minY + spacing / 2; y < maxY; y += spacing)
                for (double x = minX + spacing / 2; x < maxX; x += spacing)
                {
                    var p = new Point2(x, y);
                    if (!latticeInside.Contains(p)) continue;
                    if (grid.AnyWithin(p, 0.45 * spacing)) continue;
                    points.Add(p);
                }
        }

        var cdt = new Cdt2D();
        try
        {
            cdt.Triangulate(points, constraints);
        }
        catch (ConstraintRecoveryException ex)
        {
            throw new StepGeometryException(
                $"face {faceIndex} (#{face.Id}): boundary constraint recovery failed — {ex.Message}");
        }

        // Triangle classification must use the SAME coordinates the triangulation lives
        // in: Cdt2D jitters every point, and a boundary sliver caused by the jittered
        // ring zigzag lies OUTSIDE the jittered polygon but inside the ideal one — the
        // ideal-ring test would keep it, double-covering the boundary strip and breaking
        // the closed-surface invariant.
        var jitteredRings = new List<List<Point2>>(rings.Count);
        int cursor = 0;
        foreach (var ring in rings)
        {
            var jittered = new List<Point2>(ring.Uv.Count);
            for (int i = 0; i < ring.Uv.Count; i++) jittered.Add(cdt.Points[cursor + i]);
            jitteredRings.Add(jittered);
            cursor += ring.Uv.Count;
        }
        var inside = new PolygonSetIndex(new[] { AsPolygon(jitteredRings, outerRing) });

        double signFlip = (face.SameSense ? 1 : -1) * (flipWinding ? -1 : 1);
        foreach (var (a, b, c) in cdt.Triangles())
        {
            var pa = cdt.Points[a];
            var pb = cdt.Points[b];
            var pc = cdt.Points[c];
            var centroid = new Point2((pa.X + pb.X + pc.X) / 3, (pa.Y + pb.Y + pc.Y) / 3);
            if (!inside.Contains(centroid)) continue;

            var va = Lift(a);
            var vb = Lift(b);
            var vc = Lift(c);

            // Winding against the composed face normal, evaluated at the centroid UV.
            var normal = Vector3D.Cross(
                surface.PartialU(centroid.X / scaleU, centroid.Y / scaleV),
                surface.PartialV(centroid.X / scaleU, centroid.Y / scaleV)) * signFlip;
            if (Vector3D.Dot(Vector3D.Cross(vb - va, vc - va), normal) < 0)
                (vb, vc) = (vc, vb);

            soup.Add(va);
            soup.Add(vb);
            soup.Add(vc);
            soupFaceIds.Add(faceIndex);
        }
        return;

        Vector3D Lift(int index)
        {
            if (index < boundaryCount) return boundary3D[index]; // verbatim shared edge sample
            if (index < points.Count)
            {
                // Interior lattice node: evaluate the surface at the jittered UV the
                // triangulation actually used — any UV point is on the surface.
                var p = cdt.Points[index];
                return surface.Point(p.X / scaleU, p.Y / scaleV);
            }
            // A recovery-inserted point on a boundary constraint would exist on this face
            // only — the neighbouring face would not share it, cracking the shared edge.
            throw new StepGeometryException(
                $"face {faceIndex} (#{face.Id}): constraint recovery split a boundary edge — " +
                "the shared-edge watertightness invariant cannot be kept for this face");
        }
    }

    private static Polygon2 AsPolygon<TRing>(IReadOnlyList<TRing> rings, int outerIndex)
        where TRing : IReadOnlyList<Point2>
    {
        var holes = new List<IReadOnlyList<Point2>>();
        for (int i = 0; i < rings.Count; i++)
            if (i != outerIndex)
                holes.Add(rings[i]);
        return new Polygon2(rings[outerIndex], holes);
    }

    /// <summary>
    /// Smallest curvature radius governing interior sampling; null for flat faces (which
    /// need no interior points — boundary triangulation is exact there).
    /// </summary>
    private static double? MinCurvatureRadius(StepFace face, List<UvRing> rings, double scaleU, double scaleV)
    {
        switch (face.Surface)
        {
            case StepPlane:
                return null;
            case StepCylinder cyl:
                return cyl.Radius;
            case StepSphere sph:
                return sph.Radius;
            case StepTorus tor:
                return tor.MinorRadius;
            case StepCone cone:
            {
                double min = double.MaxValue;
                foreach (var ring in rings)
                    foreach (var uv in ring.Uv)
                    {
                        double v = uv.Y / scaleV;
                        double radius = Math.Abs(cone.Radius + v * Math.Tan(cone.SemiAngle));
                        min = Math.Min(min, radius / Math.Cos(cone.SemiAngle));
                    }
                return min == double.MaxValue ? null : Math.Max(min, 1e-12);
            }
            default:
                return NumericMinRadius(face.Surface, rings, scaleU, scaleV);
        }
    }

    /// <summary>Finite-difference normal-curvature bound over a 5×5 UV grid of the rings' bbox.</summary>
    private static double? NumericMinRadius(StepSurface surface, List<UvRing> rings, double scaleU, double scaleV)
    {
        double minU = double.MaxValue, minV = double.MaxValue;
        double maxU = double.MinValue, maxV = double.MinValue;
        foreach (var ring in rings)
            foreach (var uv in ring.Uv)
            {
                minU = Math.Min(minU, uv.X / scaleU);
                maxU = Math.Max(maxU, uv.X / scaleU);
                minV = Math.Min(minV, uv.Y / scaleV);
                maxV = Math.Max(maxV, uv.Y / scaleV);
            }
        double du = (maxU - minU) / 4, dv = (maxV - minV) / 4;
        if (du <= 0 || dv <= 0) return null;
        double hu = du * 1e-3, hv = dv * 1e-3;

        double maxKappa = 0;
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                double u = minU + du * i, v = minV + dv * j;
                var su = surface.PartialU(u, v);
                var sv = surface.PartialV(u, v);
                var n = Vector3D.Cross(su, sv);
                if (n.Length < 1e-30) continue;
                n = n.Normalized();
                var suu = (surface.PartialU(u + hu, v) - surface.PartialU(u - hu, v)) / (2 * hu);
                var svv = (surface.PartialV(u, v + hv) - surface.PartialV(u, v - hv)) / (2 * hv);
                var suv = (surface.PartialU(u, v + hv) - surface.PartialU(u, v - hv)) / (2 * hv);
                double ku = su.LengthSquared > 0 ? Math.Abs(Vector3D.Dot(suu, n)) / su.LengthSquared : 0;
                double kv = sv.LengthSquared > 0 ? Math.Abs(Vector3D.Dot(svv, n)) / sv.LengthSquared : 0;
                double kuv = su.Length * sv.Length > 0
                    ? Math.Abs(Vector3D.Dot(suv, n)) / (su.Length * sv.Length)
                    : 0;
                maxKappa = Math.Max(maxKappa, Math.Max(ku, Math.Max(kv, kuv)));
            }
        return maxKappa > 1e-30 ? 1 / maxKappa : null;
    }
}
