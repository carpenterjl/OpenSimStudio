using OpenSim.Core.Model;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// Scale Lens: multiplies every length of the surfaces <c>first</c>…<c>last</c> by a factor
/// (OpticStudio 2025 R2 help, Setup tab › System › Scale Lens, "Scale by factor" with First
/// and Last Surface; plans/sources/optics/ansyshelp-op31/Scale_Lens.html). The help: "data
/// measured in lens units of length will be scaled by the factor X", dimensionless data such as
/// the conic constant is not, and wavelengths are not.
/// <para>
/// Scaled here: radius, thickness (a finite one), clear and mechanical semi-diameters, chip
/// zone, the aperture's lengths and decenters, and the offset of a pickup on a length cell.
/// Not scaled: the conic, TCE, a pickup's scale factor, and solve parameters (their units are
/// the solve's, which OP-29 defines; a note names how many were left). The system data
/// (aperture value, field heights) is not scaled: this tool edits the surfaces only. A surface
/// of any type but Standard in the range is refused, since the units of its parameters are not
/// known to this build. "Scale by units" is the lens-unit change of the system data.
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
            if (!SavedSurfaces.IsStandard(surfaces[j]))
                throw new ArgumentException($"Scale Lens: surface {j} is a '{surfaces[j].Type}' surface, whose parameters' units this build does not know; scale it by hand.");

        var result = surfaces.ToList();
        int solves = 0;
        for (int j = a; j <= b; j++)
        {
            var s = surfaces[j];
            result[j] = s with
            {
                Radius = s.Radius * factor,
                Thickness = s.ThicknessIsInfinite ? s.Thickness : s.Thickness * factor,
                SemiDiameter = s.SemiDiameter * factor,
                MechanicalSemiDiameter = s.MechanicalSemiDiameter * factor,
                ChipZone = s.ChipZone * factor,
                Aperture = s.Aperture is { } ap ? Scale(ap, factor) : null,
                Flags = s.Flags?.Select(f => SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) && SurfaceFlags.IsLengthColumn(f.Column)
                    ? f with { PickupOffset = f.PickupOffset * factor } : f).ToList(),
            };
            solves += s.Flags?.Count(f => SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag) && f.SolveParameters is { Count: > 0 }) ?? 0;
        }
        var notes = new List<string> { FormattableString.Invariant($"Surfaces {a} to {b} scaled by {factor:G10}.") };
        if (solves > 0) notes.Add($"{solves} solve{(solves == 1 ? "" : "s")} kept with unscaled parameters; check them.");
        return new LensEditResult(result, notes);
    }

    /// <summary>
    /// The lens-unit change of the system data (OpticStudio's "Scale by units"): every surface's
    /// lengths divided by the new unit and multiplied by the old, so the lens keeps its size in
    /// meters. Unlike <see cref="Apply"/> it does not refuse other surface types: their shared
    /// columns are converted and their numbered parameters are left, with a note naming them.
    /// </summary>
    public static LensEditResult ConvertUnits(IReadOnlyList<OpticsSurfaceSettings> surfaces, double factor)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (!double.IsFinite(factor) || !(factor > 0))
            throw new ArgumentException($"Lens unit change: the factor must be a positive number (it is {factor}).");
        var standard = surfaces.Select(s => s with { Type = null }).ToList();
        var scaled = Apply(standard, factor).Surfaces.Select((s, j) => s with { Type = surfaces[j].Type }).ToList();
        var others = Enumerable.Range(0, surfaces.Count).Where(j => !SavedSurfaces.IsStandard(surfaces[j]) && surfaces[j].Parameters is { Count: > 0 }).ToArray();
        var notes = new List<string>();
        if (others.Length > 0)
            notes.Add($"The parameters of surface{(others.Length == 1 ? "" : "s")} {string.Join(", ", others)} were not converted to the new lens unit; check them.");
        return new LensEditResult(scaled, notes);
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
