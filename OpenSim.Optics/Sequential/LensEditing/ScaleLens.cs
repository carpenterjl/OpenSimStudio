using OpenSim.Core.Model;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// Scale Lens: multiplies every length of the surfaces <c>first</c>…<c>last</c> by a factor
/// (OpticStudio 2025 R2 help, Setup tab › System › Scale Lens, "Scale by factor" with First
/// and Last Surface; plans/sources/optics/ansyshelp-op31/Scale_Lens.html). The help: "data
/// measured in lens units of length will be scaled by the factor X. Data measured in units of
/// lens units squared … will be scaled by X squared. Some polynomial coefficients, such as those
/// on the Even Aspheric surface, have units that change from term to term", dimensionless data
/// such as the conic constant is not, and wavelengths are not.
/// <para>
/// Scaled here: radius, thickness (a finite one), clear and mechanical semi-diameters, chip
/// zone, the aperture's lengths and decenters, the decenters of the surface tilts before and
/// after, each parameter of a registered surface type by factor^power of its column
/// (<see cref="SurfaceParameterInfo.LengthPower"/>, TC-6: a coordinate break's decenters,
/// not its tilts; an Even Asphere's α2 by factor⁻³), and the offset of a pickup on such a cell.
/// Not scaled: the conic, TCE, tilts, integer parameters, a pickup's scale factor, and solve
/// parameters (a note names how many were left). The system data (aperture value, field
/// heights) is not scaled: this tool edits the surfaces only. A surface of a type that is not
/// registered is refused by name, since the units of its parameters are not known.
/// "Scale by units" is the lens-unit change of the system data.
/// </para>
/// </summary>
public static class ScaleLens
{
    public static LensEditResult Apply(IReadOnlyList<OpticsSurfaceSettings> surfaces, double factor, int? first = null, int? last = null)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        int a = first ?? 0, b = last ?? surfaces.Count - 1;
        SurfaceFlags.CheckRange("Scale Lens", surfaces, a, b, 0, surfaces.Count - 1);
        if (!double.IsFinite(factor) || !(factor > 0))
            throw new ArgumentException($"Scale Lens: the factor must be a positive number (it is {factor}).");
        for (int j = a; j <= b; j++)
            if (!SavedSurfaces.IsStandard(surfaces[j]) && SurfaceTypes.Find(surfaces[j].Type) is null)
                throw new ArgumentException($"Scale Lens: surface {j} is a '{surfaces[j].Type}' surface, a type whose parameters' units this build does not know; scale it by hand.");
        return Scaled(surfaces, factor, a, b, out _);
    }

    private static LensEditResult Scaled(IReadOnlyList<OpticsSurfaceSettings> surfaces, double factor, int a, int b, out int[] unregistered)
    {
        var result = surfaces.ToList();
        var skipped = new List<int>();
        int solves = 0;
        for (int j = a; j <= b; j++)
        {
            var s = surfaces[j];
            var layout = SurfaceTypes.Find(s.Type);
            if (layout is null && !SavedSurfaces.IsStandard(s) && (s.Parameters is { Count: > 0 } || s.ExtraData is { Count: > 0 })) skipped.Add(j);
            double ParameterFactor(int number) => layout?.Parameter(number) is { } info && !info.IsInteger ? Power(factor, info.LengthPower) : 1;
            List<OpticsSurfaceParameterSettings>? Each(List<OpticsSurfaceParameterSettings>? list) =>
                layout is null ? list : list?.Select(p => p with { Value = p.Value * ParameterFactor(p.Number) }).ToList();
            double OffsetFactor(string? column) => SurfaceFlags.IsLengthColumn(column) ? factor
                : SavedSurfaces.ParameterNumber(column) is int n ? ParameterFactor(n) : 1;
            result[j] = s with
            {
                Radius = s.Radius * factor,
                Thickness = s.ThicknessIsInfinite ? s.Thickness : s.Thickness * factor,
                SemiDiameter = s.SemiDiameter * factor,
                MechanicalSemiDiameter = s.MechanicalSemiDiameter * factor,
                ChipZone = s.ChipZone * factor,
                Aperture = s.Aperture is { } ap ? Scale(ap, factor) : null,
                TiltBefore = s.TiltBefore is { } tb ? tb with { DecenterX = tb.DecenterX * factor, DecenterY = tb.DecenterY * factor } : null,
                TiltAfter = s.TiltAfter is { } ta ? ta with { DecenterX = ta.DecenterX * factor, DecenterY = ta.DecenterY * factor } : null,
                Parameters = Each(s.Parameters),
                ExtraData = Each(s.ExtraData),
                Flags = s.Flags?.Select(f => SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) && OffsetFactor(f.Column) != 1
                    ? f with { PickupOffset = f.PickupOffset * OffsetFactor(f.Column) } : f).ToList(),
            };
            solves += s.Flags?.Count(f => SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag) && f.SolveParameters is { Count: > 0 }) ?? 0;
        }
        unregistered = skipped.ToArray();
        var notes = new List<string> { FormattableString.Invariant($"Surfaces {a} to {b} scaled by {factor:G10}.") };
        if (solves > 0) notes.Add($"{solves} solve{(solves == 1 ? "" : "s")} kept with unscaled parameters; check them.");
        return new LensEditResult(result, notes);
    }

    /// <summary>factor^power by repeated multiplication (factor^0 = 1 exactly).</summary>
    private static double Power(double factor, int power) => SurfaceTypes.Factor(power, factor);

    /// <summary>
    /// The lens-unit change of the system data (OpticStudio's "Scale by units"): every surface's
    /// lengths divided by the new unit and multiplied by the old, so the lens keeps its size in
    /// meters, with the parameters of registered types scaled by their units. Unlike
    /// <see cref="Apply"/> it does not refuse a type that is not registered: its shared columns
    /// are converted and its numbered parameters are left, with a note naming the surfaces.
    /// </summary>
    public static LensEditResult ConvertUnits(IReadOnlyList<OpticsSurfaceSettings> surfaces, double factor)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (!double.IsFinite(factor) || !(factor > 0))
            throw new ArgumentException($"Lens unit change: the factor must be a positive number (it is {factor}).");
        if (surfaces.Count < 3) throw new ArgumentException("Lens unit change: the lens needs the object, at least one surface and the image.");
        var scaled = Scaled(surfaces, factor, 0, surfaces.Count - 1, out int[] others);
        var notes = new List<string>();
        if (others.Length > 0)
            notes.Add($"The parameters of surface{(others.Length == 1 ? "" : "s")} {string.Join(", ", others)} were not converted to the new lens unit (the type is not registered); check them.");
        return new LensEditResult(scaled.Surfaces, notes);
    }

    private static OpticsSurfaceApertureSettings Scale(OpticsSurfaceApertureSettings a, double k) => a with
    {
        DecenterX = a.DecenterX * k, DecenterY = a.DecenterY * k,
        MinRadius = a.MinRadius * k, MaxRadius = a.MaxRadius * k,
        HalfWidthX = a.HalfWidthX * k, HalfWidthY = a.HalfWidthY * k,
        ArmWidth = a.ArmWidth * k,
        Vertices = a.Vertices?.Select(v => new OpticsPointSettings { X = v.X * k, Y = v.Y * k }).ToList(),
    };
}

/// <summary>
/// Make Focal: scales the whole lens so its effective focal length is the one asked for
/// ("Make focal length is similar to scale lens, except the desired focal length is typed in
/// directly. The entire lens is then scaled"; OpticStudio 2025 R2 help, Lens Data Editor
/// Toolbar › Make Focal, plans/sources/optics/ansyshelp-op31/Make_Focal.html). The focal length
/// is the paraxial effective focal length at the primary wavelength (section 11); every
/// surface 0 to the image is scaled by target / current with <see cref="ScaleLens"/>, which
/// makes the new focal length the target to rounding because a uniform scale of every length
/// at fixed indices scales the focal length by the same factor. A focal length of the other
/// sign cannot be reached by scaling and is refused, as is an afocal lens.
/// </summary>
public static class MakeFocal
{
    public static LensEditResult Apply(IReadOnlyList<OpticsSurfaceSettings> surfaces, double focalLength, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(context);
        if (!double.IsFinite(focalLength) || focalLength == 0)
            throw new ArgumentException($"Make Focal: the focal length must be a non-zero number (it is {focalLength}).");
        double current = EffectiveFocalLength(surfaces, context);
        double factor = focalLength / current;
        if (!(factor > 0))
            throw new ArgumentException(FormattableString.Invariant(
                $"Make Focal: the lens's focal length is {current:G10} lens units; scaling cannot change its sign to reach {focalLength:G10}."));
        var scaled = ScaleLens.Apply(surfaces, factor);
        return scaled with { Notes = new[] { FormattableString.Invariant($"Scaled by {factor:G10} from a focal length of {current:G10} to {focalLength:G10} lens units.") }.Concat(scaled.Notes.Skip(1)).ToArray() };
    }

    /// <summary>The paraxial effective focal length at the primary wavelength, in lens units.</summary>
    public static double EffectiveFocalLength(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        var lens = context.ToLens(surfaces);
        var system = ParaxialSystem.Create(lens.ToParaxialLens(), context.System());
        if (system.EffectiveFocalLength is not double efl)
            throw new ArgumentException("Make Focal: the lens is afocal (it has no power), so it has no focal length to scale.");
        return efl / OpticsConventions.MetersPer(context.Unit);
    }
}
