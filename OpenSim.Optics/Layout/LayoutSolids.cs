namespace OpenSim.Optics.Layout;

/// <summary>
/// The bodies of the shaded model (OpticStudio's Shaded Model): one closed solid per lens
/// body, each mirror outside a lens body as a solid with its substrate or as a bare surface,
/// and an aperture annulus at the stop. Hidden surfaces add no mirror or aperture, but still
/// bound their lens body.
/// </summary>
public static class LayoutSolids
{
    public static IReadOnlyList<LayoutBody> Build(OpticalLayout layout, LatheResolution? resolution = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var res = resolution ?? LatheResolution.Default;
        var bodies = new List<LayoutBody>();

        foreach (var e in layout.Elements)
        {
            var mesh = LatheMesher.Solid(layout.Surfaces[e.Front], layout.Surfaces[e.Back],
                layout.EdgeSemiDiameter(e), res);
            bodies.Add(new LayoutBody(LayoutBodyKind.Lens, mesh, e.Front, true));
        }

        for (int i = 0; i < layout.Surfaces.Count; i++)
        {
            var s = layout.Surfaces[i];
            if (s.IsHidden) continue;
            double radius = LayoutGeometry.DrawRadius(layout, i);
            if (s.IsMirror && !LayoutGeometry.InElement(layout, i))
            {
                var back = LayoutGeometry.MirrorBack(s, radius, res.RadialSamples);
                bodies.Add(back is null
                    ? new LayoutBody(LayoutBodyKind.Mirror, LatheMesher.Cap(s, radius, res), i, false)
                    : new LayoutBody(LayoutBodyKind.Mirror, LatheMesher.Solid(s, back, radius, res), i, true));
            }
            if (s.IsStop)
            {
                double outer = Math.Max(LayoutGeometry.ApertureOuterRadius(s), radius);
                if (outer > s.ClearSemiDiameter)
                    bodies.Add(new LayoutBody(LayoutBodyKind.Aperture,
                        LatheMesher.Annulus(s, s.ClearSemiDiameter, outer, res), i, false));
            }
        }
        return bodies;
    }
}
