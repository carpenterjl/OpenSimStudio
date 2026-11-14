using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// Builds the YZ cross-section (OpticStudio's Cross-Section layout). Every surface is drawn
/// along its local y through the vertex, placed in the global frame, and seen from the side:
/// the global z is horizontal and the global y vertical (docs/Optics-Conventions.md §3, x
/// into the screen).
/// <list type="bullet">
/// <item>A lens body is one closed outline: the front face from −edge to +edge, the edge, the
/// back face from +edge to −edge, the edge. A face whose outer radius is smaller than the
/// body's edge gets a flat out to it.</item>
/// <item>A mirror outside a lens body is its profile, closed through the back of its substrate
/// when it has one.</item>
/// <item>The stop gets two aperture blades, from the clear edge outward.</item>
/// <item>Rays are their polylines seen from the side, coloured by field or wavelength.</item>
/// </list>
/// </summary>
public static class CrossSection
{
    public static CrossSectionDrawing Build(OpticalLayout layout, CrossSectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= CrossSectionOptions.Default;
        int n = options.SamplesPerSemiDiameter;
        var lines = new List<LayoutPolyline>();

        foreach (var e in layout.Elements)
        {
            double edge = layout.EdgeSemiDiameter(e);
            var front = layout.Surfaces[e.Front];
            var back = layout.Surfaces[e.Back];
            var pts = new List<Point2>();
            foreach (var p in SagSampler.Meridian(front, edge, n)) pts.Add(Project(front.Placement.Apply(p)));
            var backPts = SagSampler.Meridian(back, edge, n);
            for (int i = backPts.Count - 1; i >= 0; i--) pts.Add(Project(back.Placement.Apply(backPts[i])));
            lines.Add(new LayoutPolyline(pts, LayoutLineKind.LensOutline, e.Front, true, e.Front));
        }

        for (int i = 0; i < layout.Surfaces.Count; i++)
        {
            var s = layout.Surfaces[i];
            if (s.IsHidden) continue;
            bool inElement = LayoutGeometry.InElement(layout, i);
            double radius = LayoutGeometry.DrawRadius(layout, i);

            if (s.IsMirror && !inElement)
            {
                var pts = new List<Point2>();
                foreach (var p in SagSampler.Meridian(s, radius, n)) pts.Add(Project(s.Placement.Apply(p)));
                var back = LayoutGeometry.MirrorBack(s, radius, n);
                if (back is not null)
                {
                    pts.Add(Project(back.GlobalPoint(0, radius)));
                    pts.Add(Project(back.GlobalPoint(0, -radius)));
                }
                lines.Add(new LayoutPolyline(pts, LayoutLineKind.Mirror, i, back is not null, i));
            }
            else if (!inElement && !s.IsStop)
            {
                var pts = new List<Point2>();
                foreach (var p in SagSampler.Meridian(s, radius, n)) pts.Add(Project(s.Placement.Apply(p)));
                lines.Add(new LayoutPolyline(pts, LayoutLineKind.Surface, i, false, i));
            }

            if (s.IsStop)
            {
                double outer = Math.Max(LayoutGeometry.ApertureOuterRadius(s), radius);
                foreach (double sign in new[] { 1.0, -1.0 })
                {
                    var blade = new[]
                    {
                        Project(s.GlobalPoint(0, sign * s.ClearSemiDiameter)),
                        Project(s.GlobalPoint(0, sign * outer)),
                    };
                    lines.Add(new LayoutPolyline(blade, LayoutLineKind.Aperture, i, false, i));
                }
            }
        }

        foreach (var ray in layout.Rays)
        {
            if (ray.Points.Count < 2) continue;
            if (options.DeleteVignetted && ray.Status != RayStatus.Ok) continue;
            var pts = new Point2[ray.Points.Count];
            for (int k = 0; k < pts.Length; k++) pts[k] = Project(ray.Points[k]);
            lines.Add(new LayoutPolyline(pts, LayoutLineKind.Ray, ColorKey(ray, options.Coloring), false, -1));
        }

        var bounds = LayoutBounds2D.Empty;
        foreach (var line in lines)
            foreach (var p in line.Points) bounds = bounds.Including(p);

        if (options.Fletch != FletchMode.None)
        {
            double size = options.FletchSize * Math.Max(bounds.Width, bounds.Height);
            int count = lines.Count;
            for (int l = 0; l < count; l++)
            {
                var line = lines[l];
                if (line.Kind != LayoutLineKind.Ray) continue;
                int segments = options.Fletch == FletchMode.FirstSegment ? 1 : line.Points.Count - 1;
                for (int k = 0; k < segments; k++)
                {
                    var arrow = Fletch(line.Points[k], line.Points[k + 1], size);
                    if (arrow is not null)
                        lines.Add(new LayoutPolyline(arrow, LayoutLineKind.Fletch, line.ColorKey, false, -1));
                }
            }
        }

        return new CrossSectionDrawing(lines, bounds);
    }

    /// <summary>A global point seen from the side: (z, y).</summary>
    public static Point2 Project(Vector3D global) => new(global.Z, global.Y);

    /// <summary>The colour index of a ray: its field or its wavelength index.</summary>
    public static int ColorKey(LayoutRayPath ray, RayColoring coloring) => coloring switch
    {
        RayColoring.ByField => ray.FieldIndex,
        RayColoring.ByWavelength => ray.WavelengthIndex,
        _ => throw new ArgumentOutOfRangeException(nameof(coloring), coloring, "Unknown ray colouring.")
    };

    /// <summary>
    /// An open arrowhead pointing along a → b at the segment's middle: two arms of the given
    /// length, 25° either side of the backward direction. Null for a segment of zero length
    /// in the drawing plane (a ray running along x).
    /// </summary>
    public static Point2[]? Fletch(Point2 a, Point2 b, double size)
    {
        var d = b - a;
        double len = d.Length;
        if (!(len > 0) || !(size > 0)) return null;
        var u = d * (1.0 / len);
        var tip = a + d * 0.5;
        var (s, c) = Math.SinCos(25.0 * Math.PI / 180.0);
        var left = new Point2(u.X * c - u.Y * s, u.X * s + u.Y * c);
        var right = new Point2(u.X * c + u.Y * s, -u.X * s + u.Y * c);
        return new[] { tip - left * size, tip, tip - right * size };
    }
}
