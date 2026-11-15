using OpenSim.Core.Model;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The Optimization Wizard's options to and from the project's saved form
/// (<see cref="OpticsMeritWizardSettings"/>, OP-56). Choices are saved by their enum names; a
/// name this build does not know loads as the default and is named in the notes. Every value is
/// saved, so a later build with other defaults reopens what was saved.
/// </summary>
public static class DefaultMeritWizardSettings
{
    public static OpticsMeritWizardSettings ToSettings(DefaultMeritOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new OpticsMeritWizardSettings
        {
            Criterion = o.Criterion.ToString(),
            Reference = o.Reference.ToString(),
            Integration = o.Integration.ToString(),
            Rings = o.Rings,
            Arms = o.Arms,
            Grid = o.Grid,
            DeleteVignetted = o.DeleteVignetted,
            AssumeAxialSymmetry = o.AssumeAxialSymmetry,
            IgnoreLateralColor = o.IgnoreLateralColor,
            StartAt = o.StartAt,
            OverallWeight = o.OverallWeight,
            RelativeXWeight = o.RelativeXWeight,
            RelativeYWeight = o.RelativeYWeight,
            Configurations = o.Configurations.ToString(),
            GlassBoundaries = o.GlassBoundaries,
            GlassMinimum = o.GlassMinimum,
            GlassMaximum = o.GlassMaximum,
            GlassEdge = o.GlassEdge,
            AirBoundaries = o.AirBoundaries,
            AirMinimum = o.AirMinimum,
            AirMaximum = o.AirMaximum,
            AirEdge = o.AirEdge,
        };
    }

    /// <summary>The saved options; null (or a null property) is the default.</summary>
    public static DefaultMeritOptions FromSettings(OpticsMeritWizardSettings? saved, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var d = new DefaultMeritOptions();
        if (saved is null) return d;
        return new DefaultMeritOptions
        {
            Criterion = Choice(saved.Criterion, d.Criterion, "criterion", notes),
            Reference = Choice(saved.Reference, d.Reference, "reference", notes),
            Integration = Choice(saved.Integration, d.Integration, "pupil integration", notes),
            Rings = saved.Rings ?? d.Rings,
            Arms = saved.Arms ?? d.Arms,
            Grid = saved.Grid ?? d.Grid,
            DeleteVignetted = saved.DeleteVignetted ?? d.DeleteVignetted,
            AssumeAxialSymmetry = saved.AssumeAxialSymmetry,
            IgnoreLateralColor = saved.IgnoreLateralColor ?? d.IgnoreLateralColor,
            StartAt = saved.StartAt,
            OverallWeight = saved.OverallWeight ?? d.OverallWeight,
            RelativeXWeight = saved.RelativeXWeight ?? d.RelativeXWeight,
            RelativeYWeight = saved.RelativeYWeight ?? d.RelativeYWeight,
            Configurations = Choice(saved.Configurations, d.Configurations, "configurations", notes),
            GlassBoundaries = saved.GlassBoundaries ?? d.GlassBoundaries,
            GlassMinimum = saved.GlassMinimum ?? d.GlassMinimum,
            GlassMaximum = saved.GlassMaximum ?? d.GlassMaximum,
            GlassEdge = saved.GlassEdge ?? d.GlassEdge,
            AirBoundaries = saved.AirBoundaries ?? d.AirBoundaries,
            AirMinimum = saved.AirMinimum ?? d.AirMinimum,
            AirMaximum = saved.AirMaximum ?? d.AirMaximum,
            AirEdge = saved.AirEdge ?? d.AirEdge,
        };
    }

    private static T Choice<T>(string? name, T fallback, string what, ICollection<string> notes) where T : struct, Enum
    {
        if (name is null) return fallback;
        if (Enum.TryParse<T>(name, ignoreCase: false, out var value) && Enum.IsDefined(value) && !int.TryParse(name, out _)) return value;
        notes.Add($"Optimization Wizard: the saved {what} '{name}' is not one this version knows; kept {fallback}.");
        return fallback;
    }
}
