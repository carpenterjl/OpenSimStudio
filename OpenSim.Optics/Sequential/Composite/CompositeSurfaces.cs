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

    // ------------------------------------------------------------ Wave 11: COMPOSITEON / COMPOSITEOFF

    /// <summary>
    /// The ZPL keyword COMPOSITEON surf (OpticStudio 2025 R2 help, "COMPOSITEON (keywords)":
    /// "Checks the Composite property option 'Composite Surface: Add sag to the next surface' to
    /// make the surface an Add-on composite"). In this program's model (add-ons are a list on the
    /// base, not rows) surface <paramref name="j"/> leaves the surface list and becomes the top
    /// add-on of surface j + 1, above that surface's existing add-ons and below its own (its own add-ons were
    /// rows above it, which "the sag profile of the Add-on surfaces will be added to the following
    /// Add-on surface" carries down to the same base; help, "Composite Surface"). Every surface
    /// number held elsewhere follows (<see cref="Tolerancing.SavedLensSurgery"/>).
    /// <para>
    /// Refused, naming the reason, for anything the add-on cannot hold: the object, the image or
    /// the surface before the image; a type the composite table does not list (base or add-on);
    /// the stop ("The Add-on composite surface can't be set as stop"); a thickness other than 0 or
    /// a medium after the surface other than the one before it (removing the row would move the
    /// surfaces behind it or change the medium the base is met in; the help does not say what an
    /// add-on row's thickness or glass does); and the cell flags, tilts, coordinate return,
    /// aperture, coating and other row settings an add-on has no place for.
    /// </para>
    /// </summary>
    public static OpticsSettings MakeAddOn(OpticsSettings optics, int j)
    {
        ArgumentNullException.ThrowIfNull(optics);
        const string who = "COMPOSITEON";
        var surfaces = optics.Surfaces ?? throw new ArgumentException($"{who}: the lens has no surfaces.");
        int image = surfaces.Count - 1;
        if (j < 1 || j > image - 2)
            throw new ArgumentException($"{who}: surface {j} cannot become an add-on; the surfaces that can are 1 to {image - 2} (an add-on needs a lens surface after it, before the image).");
        var s = surfaces[j] ?? new OpticsSurfaceSettings();
        var b = surfaces[j + 1] ?? new OpticsSurfaceSettings();
        var why = new List<string>();
        if (!IsSupportedType(s.Type)) why.Add($"a {s.Type} surface cannot be a composite add-on (the types that can are {string.Join(", ", SupportedTypes)})");
        if (!IsSupportedType(b.Type)) why.Add($"surface {j + 1}, a {b.Type} surface, cannot be a composite base");
        if (s.IsStop) why.Add("it is the stop, and an add-on cannot be the stop");
        if (s.ThicknessIsInfinite || s.Thickness != 0) why.Add("its thickness is not 0, and removing the row would move the surfaces after it");
        if (!SameMedium(s, surfaces[j - 1] ?? new OpticsSurfaceSettings())) why.Add($"the medium after it is not the medium before it (surface {j - 1}'s), and removing the row would change the medium surface {j + 1} is met in");
        if (s.Flags is { Count: > 0 }) why.Add("it has variables, solves or pickups, which an add-on cannot hold");
        if (s.TiltBefore is not null || s.TiltAfter is not null) why.Add("it has its own tilt or decenter (an add-on's placement is set relative to the base)");
        if (s.CoordinateReturn is { Mode: not (null or "None") }) why.Add("it has a coordinate return");
        if (s.Aperture is not null) why.Add("it has an aperture (the base's aperture is the composite surface's)");
        if (!string.IsNullOrEmpty(s.Coating)) why.Add("it has a coating");
        if (s.MechanicalSemiDiameter is not null || s.ChipZone != 0 || s.Tce != 0 || s.SkipRaysTo || s.HideRaysTo || s.DoNotDraw || s.RowColor is not null)
            why.Add("it has a mechanical semi-diameter, chip zone, TCE, ray or drawing setting or row color, which an add-on cannot hold");
        if (why.Count > 0) throw new ArgumentException($"{who} {j}: " + string.Join("; ", why) + ".");

        var addOn = new OpticsCompositeAddOnSettings
        {
            Type = s.Type, Radius = s.Radius, Conic = s.Conic, Parameters = s.Parameters, ExtraData = s.ExtraData, GridSag = s.GridSag,
            SemiDiameter = s.SemiDiameter, IsIgnored = s.IsIgnored, Comment = s.Comment,
        };
        var list = new List<OpticsCompositeAddOnSettings>();
        list.AddRange(s.AddOns ?? new List<OpticsCompositeAddOnSettings>());
        list.Add(addOn);
        list.AddRange(b.AddOns ?? new List<OpticsCompositeAddOnSettings>());
        var edited = surfaces.ToList();
        edited[j + 1] = b with { AddOns = list };
        try { return Tolerancing.SavedLensSurgery.Remove(optics with { Surfaces = edited }, new[] { j }); }
        catch (InvalidOperationException)
        {
            throw new ArgumentException($"{who} {j}: something still names surface {j} (a pickup, a solve, a coordinate return, a merit or multi-configuration row); remove that first.");
        }
    }

    /// <summary>
    /// The ZPL keyword COMPOSITEOFF surf (OpticStudio 2025 R2 help, "COMPOSITEOFF (keywords)":
    /// "Unchecks the Composite property option 'Composite Surface: Add sag to the next surface'").
    /// OpticStudio numbers the add-on rows as surfaces; this program does not, so here
    /// <paramref name="j"/> is the base and the top add-on of surface j (OpticStudio's first
    /// add-on row of the stack) becomes surface j again, with thickness 0 and the medium of
    /// surface j − 1; the base and the rest of its add-ons move to j + 1. It undoes
    /// <see cref="MakeAddOn"/> of a surface that had no add-ons of its own. Refused when surface j
    /// has no add-on or the top add-on is decentered or tilted (a surface's own tilt and decenter
    /// is not an add-on's placement relative to the base).
    /// </summary>
    public static OpticsSettings MakeSurface(OpticsSettings optics, int j)
    {
        ArgumentNullException.ThrowIfNull(optics);
        const string who = "COMPOSITEOFF";
        var surfaces = optics.Surfaces ?? throw new ArgumentException($"{who}: the lens has no surfaces.");
        if (j < 1 || j >= surfaces.Count) throw new ArgumentException($"{who}: surface {j} is not a surface that can have add-ons (1 to {surfaces.Count - 1}).");
        var b = surfaces[j] ?? new OpticsSurfaceSettings();
        if (b.AddOns is not { Count: > 0 } list) throw new ArgumentException($"{who} {j}: surface {j} has no composite add-ons.");
        var a = list[0] ?? new OpticsCompositeAddOnSettings();
        if (a.DecenterX != 0 || a.DecenterY != 0 || a.TiltX != 0 || a.TiltY != 0 || a.TiltZ != 0)
            throw new ArgumentException($"{who} {j}: surface {j}'s add-on 1 is decentered or tilted relative to the base, which a surface of its own does not hold; set its decenters and tilts to 0 first.");
        var before = surfaces[j - 1] ?? new OpticsSurfaceSettings();
        var row = new OpticsSurfaceSettings
        {
            Type = a.Type, Radius = a.Radius, Conic = a.Conic, Parameters = a.Parameters, ExtraData = a.ExtraData, GridSag = a.GridSag,
            SemiDiameter = a.SemiDiameter, IsIgnored = a.IsIgnored, Comment = a.Comment,
            Thickness = 0, Material = before.Material, Index = before.Index, ModelGlass = before.ModelGlass,
        };
        var edited = surfaces.ToList();
        edited[j] = b with { AddOns = list.Count > 1 ? list.Skip(1).ToList() : null };
        var builder = new Tolerancing.SavedLensSurgery.Builder(edited);
        builder.CopyRange(0, j - 1);
        builder.Add(row);
        builder.CopyRange(j, edited.Count - 1);
        return builder.Build(optics with { Surfaces = edited }).Settings;
    }

    private static bool SameMedium(OpticsSurfaceSettings a, OpticsSurfaceSettings b) =>
        string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase) && a.Index == b.Index && Equals(a.ModelGlass, b.ModelGlass);
}
