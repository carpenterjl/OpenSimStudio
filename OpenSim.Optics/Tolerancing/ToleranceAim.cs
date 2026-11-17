using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Anamorphic;

namespace OpenSim.Optics.Tolerancing;

// TC-15: tolerance trials on lenses their decenters and tilts make non-axial
// (docs/Optics-Conventions.md §42, "Trials made non-axial").

/// <summary>One row applied in a trial: the row (0-based), its value and the radial direction
/// and TIRR angle it is applied with.</summary>
internal readonly record struct ToleranceStep(int RowIndex, double Value, double DirectionDegrees = 90, double AstigmatismAngleDegrees = 0);

/// <summary>A trial's perturbed record, its surface map (table numbers to the record's) and its
/// aim twin: the same trial with every decenter, tilt and roll drawn as zero (null when the trial
/// has none that is not zero).</summary>
internal sealed record ToleranceTrialSettings(OpticsSettings Perturbed, Func<int, int> Map, OpticsSettings? Twin);

/// <summary>
/// The aim of a trial lens that its decenters and tilts make non-axial. The ray launcher needs a
/// paraxial model for the entrance pupil, the fields and the paraxial aim (§7, §13), and the
/// paraxial focus compensator needs one for the defocus; a tilted or decentered lens has none in
/// this program's rotationally symmetric engine (§15). OpticStudio's ray aiming help ("Ray
/// Aiming", plans/sources/optics/ansys-op22/Ray_Aiming.html) says that with ray aiming off rays
/// are launched through "the paraxial entrance pupil size and location determined by the aperture
/// settings and calculated at the primary wavelength on axis", and with it on are aimed "to the
/// correct location on the stop" of the lens traced. Such a trial is therefore traced as it is, with
/// its pupil, fields and paraxial data taken from its aim twin: the same trial with its decenters,
/// tilts and rolls drawn as zero, so the radius, thickness, index and irregularity changes it
/// carries stay in its paraxial data. The decenters and tilts then show in the trace as aberration
/// and boresight. The twin has the trial's surfaces one for one (a zero row inserts the same
/// breaks), so the launcher's surface indices hold. A TIRR astigmatism at an angle that is not a
/// multiple of 90° also leaves the lens without a paraxial model (§23); the twin takes it at 0°.
/// <para>
/// A trial lens that has a paraxial model of its own keeps using it, so every result that did not
/// fail before is unchanged. The paraxial focus (and the pupil's axial position and size) of a lens
/// with a decenter or tilt δ differs from the twin's by O(δ²): the lens with −δ is the mirror image
/// of the lens with +δ, so these on-axis quantities are even in δ (this program's derivation; the
/// help does not say how OpticStudio's paraxial engine treats a tilted lens, §10 item 38).
/// </para>
/// </summary>
internal static class ToleranceAim
{
    private static readonly HashSet<string> OffAxisCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TSDX", "TSDY", "TSDR", "TSTX", "TSTY", "TIRX", "TIRY",
        "TUDX", "TUDY", "TUTX", "TUTY", "TUTZ",
        "TEDX", "TEDY", "TEDR", "TETX", "TETY", "TETZ",
        "TARX", "TARY", "TARR", "TRLX", "TRLY", "TRLR",
    };

    /// <summary>True for the operands that decenter, tilt or roll a surface or an element.</summary>
    public static bool MovesOffAxis(string code) => OffAxisCodes.Contains(code);

    /// <summary>Applies the steps one after another to <paramref name="start"/>, each change's map
    /// composed so later rows find their surfaces, and the same steps with the decenters, tilts and
    /// rolls at zero to build the aim twin when any of them is not zero.</summary>
    public static ToleranceTrialSettings Apply(TolerancePerturber perturber, OpticsSettings start, ToleranceTable table, IReadOnlyList<ToleranceStep> steps)
    {
        bool needTwin = steps.Any(s => s.Value != 0 && (MovesOffAxis(table.Rows[s.RowIndex].Code) || SkewIrregularity(table.Rows[s.RowIndex].Code, s)));
        var current = start;
        var twin = needTwin ? start : null;
        Func<int, int> map = n => n;
        foreach (var s in steps)
        {
            var before = map;
            var options = new ToleranceApplyOptions
            {
                SurfaceMap = before, DirectionDegrees = s.DirectionDegrees, AstigmatismAngleDegrees = s.AstigmatismAngleDegrees,
            };
            var change = perturber.Apply(current, table, s.RowIndex, s.Value, options);
            current = change.Perturbed;
            if (twin is not null)
            {
                string code = table.Rows[s.RowIndex].Code;
                var twinOptions = SkewIrregularity(code, s) ? options with { AstigmatismAngleDegrees = 0 } : options;
                twin = perturber.Apply(twin, table, s.RowIndex, MovesOffAxis(code) ? 0 : s.Value, twinOptions).Perturbed;
            }
            map = n => change.MapSurface(before(n));
        }
        return new ToleranceTrialSettings(current, map, twin);
    }

    /// <summary>A TIRR step whose astigmatism angle is not a multiple of 90°: the paraxial engine
    /// has no model of astigmatism at such an angle (§23), so the twin takes the angle 0, the one the
    /// sensitivity analysis applies TIRR at, whose y-section the engine models.</summary>
    private static bool SkewIrregularity(string code, ToleranceStep s) =>
        string.Equals(code, "TIRR", StringComparison.OrdinalIgnoreCase) && s.AstigmatismAngleDegrees % 90 != 0;

    /// <summary>True when the paraxial engine has a model of <paramref name="lens"/>; otherwise
    /// <paramref name="why"/> says why not.</summary>
    public static bool HasParaxialModel(SequentialLens lens, out string? why)
    {
        try
        {
            lens.ToParaxialLens();
            why = null;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            why = e.Message;
            return false;
        }
    }

    /// <summary>
    /// <paramref name="lens"/> with its aim reference when it has no paraxial model of its own and a
    /// twin is given: the lens with the coordinate breaks, tilts and Irregular placements of the
    /// twin (its thicknesses, curvatures and the rest stay the lens's own, so a compensator's change
    /// is in it). Otherwise the lens as it is. Throws, naming <paramref name="where"/>, when the twin
    /// does not match the lens or has no paraxial model either.
    /// </summary>
    public static SequentialLens Attach(SequentialLens lens, SequentialLens? twin, string where)
    {
        if (twin is null || HasParaxialModel(lens, out _)) return lens;
        if (twin.Surfaces.Count != lens.Surfaces.Count)
            throw new ToleranceEvaluationException(
                $"{where}: the trial lens has {lens.Surfaces.Count} surfaces but its twin with the decenters and tilts at zero has {twin.Surfaces.Count}, so the twin cannot aim it.");
        var surfaces = new SequentialSurface[lens.Surfaces.Count];
        for (int j = 0; j < surfaces.Length; j++)
        {
            var s = lens.Surfaces[j];
            var t = twin.Surfaces[j];
            surfaces[j] = s with
            {
                CoordinateBreak = t.CoordinateBreak,
                TiltBefore = t.TiltBefore,
                TiltAfter = t.TiltAfter,
                Shape = s.Shape is IrregularSurface && t.Shape is IrregularSurface ? t.Shape : s.Shape,
            };
        }
        var aim = new SequentialLens { Surfaces = surfaces };
        if (!HasParaxialModel(aim, out string? why))
            throw new ToleranceEvaluationException(
                $"{where}: the trial lens is not axial, and with its decenters and tilts at zero it still has no paraxial model to take the pupil and fields from: {why}");
        return lens with { AimReference = aim };
    }
}

/// <summary>Configurations whose lenses carry the aim reference of the twin's configurations
/// (<see cref="ToleranceAim.Attach"/>).</summary>
internal sealed class AimedConfigurations : IMeritConfigurations
{
    private readonly IMeritConfigurations _inner;
    private readonly IMeritConfigurations _twin;

    public AimedConfigurations(IMeritConfigurations inner, IMeritConfigurations twin)
    {
        _inner = inner;
        _twin = twin;
    }

    public int Count => _inner.Count;

    public SequentialLens LensFor(int configuration) =>
        ToleranceAim.Attach(_inner.LensFor(configuration), _twin.LensFor(configuration), $"configuration {configuration}");

    public OpticalSystemData SystemFor(int configuration) => _inner.SystemFor(configuration);

    // wire-16: the non-sequential rows (OP-88) build their scene from the trial project.
    public OpticsSettings? SettingsFor(int configuration) => _inner.SettingsFor(configuration);
}
