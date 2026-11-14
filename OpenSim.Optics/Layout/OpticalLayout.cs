using System.Globalization;

namespace OpenSim.Optics.Layout;

/// <summary>
/// What the layout views draw: the surfaces in sequential order and the traced ray paths.
/// <para>
/// The constructor refuses a description that cannot be drawn, naming the surface: a clear
/// semi-diameter that is not positive and finite, a mechanical one smaller than the clear one,
/// or a sag that is not defined out to the clear edge. Drawable oddities (the two faces of a
/// lens crossing before its edge, a material after the last surface) are kept and listed in
/// <see cref="Warnings"/> for the log.
/// </para>
/// </summary>
public sealed class OpticalLayout
{
    public IReadOnlyList<LayoutSurface> Surfaces { get; }
    public IReadOnlyList<LayoutRayPath> Rays { get; }

    /// <summary>The lens bodies, in surface order.</summary>
    public IReadOnlyList<LensElement> Elements { get; }

    /// <summary>Drawable but suspicious points, one sentence each, naming the surfaces.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public OpticalLayout(IReadOnlyList<LayoutSurface> surfaces, IReadOnlyList<LayoutRayPath>? rays = null)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        for (int i = 0; i < surfaces.Count; i++) Check(surfaces[i], i);
        Surfaces = surfaces;
        Rays = rays ?? Array.Empty<LayoutRayPath>();
        for (int i = 0; i < Rays.Count; i++)
        {
            if (Rays[i].FieldIndex < 0 || Rays[i].WavelengthIndex < 0)
                throw new ArgumentException($"Ray path {i}: field and wavelength indices must not be negative.", nameof(rays));
        }

        var warnings = new List<string>();
        Elements = FindElements(surfaces, warnings);
        foreach (var e in Elements) CheckEdge(e, warnings);
        Warnings = warnings;
    }

    /// <summary>The radius out to which a lens body is drawn: both faces get a flat out to
    /// the larger of their outer semi-diameters, and the edge is a cylinder there.</summary>
    public double EdgeSemiDiameter(LensElement element) =>
        Math.Max(Surfaces[element.Front].OuterSemiDiameter, Surfaces[element.Back].OuterSemiDiameter);

    /// <summary>
    /// The edge thickness of a lens body: from the front face to the back face at the edge
    /// radius, along the front surface's local z, positive the way the axial thickness runs.
    /// </summary>
    public double EdgeThickness(LensElement element)
    {
        var front = Surfaces[element.Front];
        var back = Surfaces[element.Back];
        double e = EdgeSemiDiameter(element);
        double zFront = front.LocalPoint(0, e).Z;
        double zBack = front.Placement.InverseApply(back.GlobalPoint(0, e)).Z;
        double axial = front.Placement.InverseApply(back.Placement.Translation).Z;
        double edge = zBack - zFront;
        return axial < 0 ? -edge : edge;
    }

    private static void Check(LayoutSurface s, int i)
    {
        if (s is null) throw new ArgumentException($"Surface {i} is null.");
        if (!(s.ClearSemiDiameter > 0) || double.IsInfinity(s.ClearSemiDiameter))
            throw new ArgumentException(
                $"Surface {i}: the clear semi-diameter must be positive and finite (got {Format(s.ClearSemiDiameter)} m).");
        if (s.MechanicalSemiDiameter is double m && (!(m >= s.ClearSemiDiameter) || double.IsInfinity(m)))
            throw new ArgumentException(
                $"Surface {i}: the mechanical semi-diameter ({Format(m)} m) must be finite and at least the clear semi-diameter ({Format(s.ClearSemiDiameter)} m).");
        if (s.ClearSemiDiameter > s.Sag.MaxDefinedRadius)
            throw new ArgumentException(
                $"Surface {i}: the surface shape is defined only out to a radius of {Format(s.Sag.MaxDefinedRadius)} m, inside the clear semi-diameter of {Format(s.ClearSemiDiameter)} m.");
        if (double.IsNaN(s.Sag.Sag(0, s.ClearSemiDiameter)) || double.IsNaN(s.Sag.Sag(s.ClearSemiDiameter, 0)))
            throw new ArgumentException(
                $"Surface {i}: the sag is not defined at the clear semi-diameter of {Format(s.ClearSemiDiameter)} m.");
    }

    /// <summary>
    /// Surface i and i + 1 bound a body when a material follows surface i. A mirror does not
    /// start a body: the space after a mirror is the medium before it travelled back through
    /// (docs/Optics-Conventions.md §3), so a Mangin mirror's glass is drawn once, from the body
    /// that ends on the mirror.
    /// </summary>
    private static List<LensElement> FindElements(IReadOnlyList<LayoutSurface> surfaces, List<string> warnings)
    {
        var elements = new List<LensElement>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (s.IsMirror || !s.HasMaterialAfter) continue;
            if (i + 1 == surfaces.Count)
            {
                warnings.Add($"Surface {i}: material {s.MaterialAfter} follows the last surface; no lens body is drawn for it.");
                continue;
            }
            elements.Add(new LensElement(i, i + 1, s.MaterialAfter!.Trim()));
        }
        return elements;
    }

    private void CheckEdge(LensElement e, List<string> warnings)
    {
        double edge = EdgeThickness(e);
        if (edge <= 0)
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Surfaces {e.Front} and {e.Back}: the lens faces cross before the edge (edge thickness {edge * 1e3:g4} mm); the body is drawn as given."));
    }

    private static string Format(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
