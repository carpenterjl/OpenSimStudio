using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.Composite;

/// <summary>
/// Composite surfaces (OP-32, docs/Optics-Conventions.md §58): which types may be bases and
/// add-ons, the effective shape the trace and the analyses use, the lens checks, and OpticStudio's
/// Activate / Ignore Composite Add-ons on the saved surfaces.
/// <para>
/// OpticStudio keeps each add-on as its own Lens Data Editor row, above the base, with "Composite
/// Surface: Add sag to next surface" checked; "rays do not interact with the Composite Add-on
/// surfaces" (help, "Composite Surface"). This program keeps the add-ons as a list on the base
/// surface (<see cref="SequentialSurface.AddOns"/>), so surface numbers, thicknesses and every
/// analysis's surface count are those of the traced surfaces; the list's order is the rows'
/// order from the top.
/// </para>
/// </summary>
public static class CompositeSurfaces
{
    /// <summary>
    /// The types that may be composite add-ons or bases, in the help's table ("Composite
    /// Surface", Supported Surfaces: "Surface types that can be used as Composite Add-On surfaces
    /// can also be used as Composite Base … the composite checkbox is unavailable for all other
    /// surface types"), restricted to the types this program builds. The help's Cubic Spline,
    /// Extended Cubic Spline, Q-Type Freeform, Tilted, Toroidal NURBS ("Torodial" in the
    /// table is read as Toroidal) and TrueFreeForm are not built.
    /// </summary>
    public static IReadOnlyList<string> SupportedTypes { get; } = new[]
    {
        "Biconic", "Biconic Zernike", "Chebyshev Polynomial", "Even Asphere", "Extended Asphere",
        "Extended Odd Asphere", "Extended Polynomial", "Grid Sag", "Irregular", "Odd Asphere", "Odd Cosine",
        "Off-Axis Conic Freeform", "Periodic", "Polynomial", "Q-Type Asphere", "Radial NURBS",
        SavedSurfaces.StandardType, "Superconic", "Toroidal", "Zernike Fringe Sag", "Zernike Standard Sag",
        "Zernike Annular Standard Sag",
    };

    private static readonly HashSet<string> Supported = new(SupportedTypes, StringComparer.Ordinal);

    /// <summary>True when <paramref name="typeName"/> may be a composite add-on or base.</summary>
    public static bool IsSupportedType(string? typeName) => Supported.Contains(typeName ?? SavedSurfaces.StandardType);

    /// <summary>True when the surface has an add-on whose sag is added (not ignored).</summary>
    public static bool HasActiveAddOns(SequentialSurface surface) =>
        surface.AddOns?.Any(a => a is { IsIgnored: false }) == true;

    /// <summary>
    /// The shape the trace and the analyses use for <paramref name="surface"/>: its own
    /// <see cref="SequentialSurface.Shape"/> (the same instance) when it has no active add-on
    /// whose sag is not zero everywhere, or is ignored or a coordinate break; otherwise a
    /// <see cref="CompositeSurfaceShape"/> of the base and the active add-ons. Throws
    /// <see cref="ArgumentException"/> naming the add-on when the base or an add-on is of a type
    /// that cannot be composite (<see cref="Check"/> reports the same for the lens).
    /// </summary>
    public static SurfaceShape? EffectiveShape(SequentialSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.AddOns is not { Count: > 0 } list || surface.IsIgnored || surface.IsCoordinateBreak) return surface.Shape;
        var active = new List<(int, CompositeAddOn)>();
        for (int k = 0; k < list.Count; k++)
            if (list[k] is { IsIgnored: false, IsZeroSag: false } a) active.Add((k + 1, a));
        if (active.Count == 0) return surface.Shape;
        var problems = new List<string>();
        CheckSurface(surface, "The surface", problems);
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        return new CompositeSurfaceShape(surface, active);
    }

    /// <summary>The surface with its effective shape in <see cref="SequentialSurface.Shape"/> and
    /// no add-on list, for code that reads only the shape (the sag analyses, the Coddington trace,
    /// element volumes); the same instance when nothing is added.</summary>
    public static SequentialSurface WithEffectiveShape(SequentialSurface surface)
    {
        var shape = EffectiveShape(surface);
        return ReferenceEquals(shape, surface.Shape) ? surface : surface with { Shape = shape, AddOns = null };
    }

    /// <summary>Adds a line to <paramref name="problems"/> for each composite rule a surface of
    /// the lens breaks (<see cref="SequentialLens.Validate"/> calls it).</summary>
    public static void Check(IReadOnlyList<SequentialSurface> surfaces, Func<int, string> where, ICollection<string> problems)
    {
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            if (s?.AddOns is not { Count: > 0 }) continue;
            string w = where(j);
            if (j == 0) problems.Add($"{w}: the object surface cannot have composite add-ons.");
            if (s.IsCoordinateBreak) problems.Add($"{w}: a coordinate break cannot have composite add-ons.");
            CheckSurface(s, w, problems);
        }
    }

    private static void CheckSurface(SequentialSurface s, string w, ICollection<string> problems)
    {
        string baseType = s.Shape?.TypeName ?? SavedSurfaces.StandardType;
        if (!s.IsCoordinateBreak && !IsSupportedType(baseType))
            problems.Add($"{w}: a {baseType} surface cannot be a composite base (the types that can are {string.Join(", ", SupportedTypes)}).");
        for (int k = 0; k < s.AddOns!.Count; k++)
        {
            string a = $"{w}, composite add-on {k + 1}";
            if (s.AddOns[k] is not { } addOn)
            {
                problems.Add($"{a}: missing.");
                continue;
            }
            if (!IsSupportedType(addOn.TypeName))
                problems.Add($"{a}: a {addOn.TypeName} surface cannot be a composite add-on (the types that can are {string.Join(", ", SupportedTypes)}).");
            if (!double.IsFinite(addOn.Curvature) || !double.IsFinite(addOn.Conic))
                problems.Add($"{a}: the curvature and conic must be finite (they are {addOn.Curvature} and {addOn.Conic}).");
            var p = addOn.Placement;
            if (!double.IsFinite(p.DecenterX) || !double.IsFinite(p.DecenterY) || !double.IsFinite(p.TiltXDegrees)
                || !double.IsFinite(p.TiltYDegrees) || !double.IsFinite(p.TiltZDegrees))
                problems.Add($"{a}: the decenters and tilts must be finite numbers.");
            if (p.Order != TiltDecenterOrder.DecenterThenTilt)
                problems.Add($"{a}: an add-on's placement is decenter then tilt (the help's Composite page has no order).");
            if (addOn.SemiDiameter is double sd && !(sd > 0 && double.IsFinite(sd)))
                problems.Add($"{a}: its own semi-diameter must be positive (it is {sd}); leave it empty to use the base's.");
        }
    }

    /// <summary>
    /// OpticStudio's Activate Composite Add-ons (<paramref name="ignore"/> false: "Activate all
    /// Composite Add-on surfaces defined in the Lens Data Editor by automatically unchecking their
    /// 'Ignore This Surface' flag") and Ignore Composite Add-ons (true: "… by automatically
    /// checking their 'Ignore This Surface' flag"), OpticStudio 2025 R2 help pages of those
    /// names. Returns the surfaces with every add-on's flag set and the number of add-ons whose
    /// flag changed.
    /// </summary>
    public static List<OpticsSurfaceSettings> SetAllIgnored(IReadOnlyList<OpticsSurfaceSettings> surfaces, bool ignore, out int changed)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        changed = 0;
        var result = new List<OpticsSurfaceSettings>(surfaces.Count);
        foreach (var s in surfaces)
        {
            if (s?.AddOns is not { Count: > 0 } list)
            {
                result.Add(s!);
                continue;
            }
            var addOns = new List<OpticsCompositeAddOnSettings>(list.Count);
            foreach (var a in list)
            {
                var item = a ?? new OpticsCompositeAddOnSettings();
                if (item.IsIgnored != ignore) changed++;
                addOns.Add(item with { IsIgnored = ignore });
            }
            result.Add(s with { AddOns = addOns });
        }
        return result;
    }
}
