using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// The 3-D wireframe (OpticStudio's 3D Viewer): polylines in the global frame, meters. Each
/// drawn surface gives its clear rim, the outer rim of its flat when it has one, and its
/// profiles along the local x and y; each lens body adds four edge lines joining its faces'
/// rims at the quarter turns, and a mirror substrate its back rim and edge lines. The stop
/// gives the inner and outer circles of its aperture. The rays are not included: the renderer
/// draws them from <see cref="OpticalLayout.Rays"/> so it can colour them.
/// </summary>
public static class LayoutWireframe
{
    public static IReadOnlyList<IReadOnlyList<Vector3D>> Build(OpticalLayout layout, LatheResolution? resolution = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var res = resolution ?? LatheResolution.Default;
        var lines = new List<IReadOnlyList<Vector3D>>();

        for (int i = 0; i < layout.Surfaces.Count; i++)
        {
            var s = layout.Surfaces[i];
            if (s.IsHidden) continue;
            double radius = LayoutGeometry.DrawRadius(layout, i);
            bool stopOnly = s.IsStop && !s.IsMirror && !LayoutGeometry.InElement(layout, i);
            if (!stopOnly)
            {
                AddSurface(lines, s, radius, res);
                var back = s.IsMirror && !LayoutGeometry.InElement(layout, i)
                    ? LayoutGeometry.MirrorBack(s, radius, res.RadialSamples)
                    : null;
                if (back is not null)
                {
                    lines.Add(Circle(back, radius, res.Segments));
                    AddEdgeLines(lines, s, back, radius);
                }
            }
            if (s.IsStop)
            {
                lines.Add(Circle(s, s.ClearSemiDiameter, res.Segments));
                lines.Add(Circle(s, Math.Max(LayoutGeometry.ApertureOuterRadius(s), radius), res.Segments));
            }
        }

        foreach (var e in layout.Elements)
            AddEdgeLines(lines, layout.Surfaces[e.Front], layout.Surfaces[e.Back], layout.EdgeSemiDiameter(e));
        return lines;
    }

    private static void AddSurface(List<IReadOnlyList<Vector3D>> lines, LayoutSurface s, double radius, LatheResolution res)
    {
        lines.Add(Circle(s, s.ClearSemiDiameter, res.Segments));
        if (radius > s.ClearSemiDiameter) lines.Add(Circle(s, radius, res.Segments));
        foreach (double azimuth in new[] { 0.0, Math.PI / 2 })
        {
            var local = SagSampler.Meridian(s, radius, res.RadialSamples, azimuth);
            var global = new Vector3D[local.Count];
            for (int k = 0; k < global.Length; k++) global[k] = s.Placement.Apply(local[k]);
            lines.Add(global);
        }
    }

    private static void AddEdgeLines(List<IReadOnlyList<Vector3D>> lines, LayoutSurface front, LayoutSurface back, double radius)
    {
        for (int q = 0; q < 4; q++)
        {
            var (sin, cos) = OpticsConventions.SinCosDegrees(90.0 * q);
            lines.Add(new[]
            {
                front.GlobalPoint(radius * cos, radius * sin),
                back.GlobalPoint(radius * cos, radius * sin),
            });
        }
    }

    /// <summary>A closed circle on the drawn surface at radius r (first point repeated last).</summary>
    private static Vector3D[] Circle(LayoutSurface s, double r, int segments)
    {
        var pts = new Vector3D[segments + 1];
        for (int j = 0; j < segments; j++)
        {
            var (sin, cos) = LatheMesher.Azimuth(j, segments);
            pts[j] = s.GlobalPoint(r * cos, r * sin);
        }
        pts[segments] = pts[0];
        return pts;
    }
}
