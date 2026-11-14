using System.Globalization;
using OpenSim.Optics.Polarization;

namespace OpenSim.Optics;

/// <summary>How the launch vectors S and P are chosen from a ray's direction (OpticStudio's
/// "Method (polarization)"; its help, "Defining the Initial Polarization").</summary>
public enum PolarizationReference
{
    /// <summary>P = K × X̂, S = P × K (OpticStudio's default).</summary>
    XAxis = 0,

    /// <summary>S = Ŷ × K, P = K × S.</summary>
    YAxis = 1,

    /// <summary>S = K × Ẑ, P = K × S.</summary>
    ZAxis = 2,
}

/// <summary>Saved names and labels for <see cref="PolarizationReference"/>.</summary>
public static class PolarizationReferences
{
    public static SavedNameTable<PolarizationReference> Names { get; } = new(
        (PolarizationReference.XAxis, "XAxis", "X axis reference"),
        (PolarizationReference.YAxis, "YAxis", "Y axis reference"),
        (PolarizationReference.ZAxis, "ZAxis", "Z axis reference"));
}

/// <summary>
/// The system polarization (OP-51; OpticStudio's System Explorer, "Polarization"): the launch
/// Jones vector or unpolarized light, the rule that turns a Jones vector into a field, and
/// whether coating phases are converted from the thin-film to the ray convention. The
/// analyses that take a polarization of their own (the five polarization analyses, as in
/// OpticStudio) read only <see cref="Reference"/> and <see cref="ConvertThinFilmPhase"/> from
/// here; everything else that applies real transmission reads all of it
/// (<see cref="PolarizationTracer.Transmission(Sequential.RayPath, SystemPolarization)"/>).
/// <para>
/// Defaults: X axis reference and conversion on are OpticStudio's stated defaults (help,
/// "Defining the Initial Polarization": "This method is the default"; "Convert Thin Film
/// Phase to Ray Equivalent": "The recommended and default setting"). Polarized along S,
/// Jx = 1, Jy = 0, is this program's choice; OpticStudio's default Jones vector was not read.
/// </para>
/// </summary>
public sealed record SystemPolarization
{
    /// <summary>Trace each ray twice, with (Jx, Jy) = (1, 0) and (0, 1), and average the
    /// intensities (help, "Unpolarized").</summary>
    public bool Unpolarized { get; init; }

    /// <summary>Field magnitudes along S and P (normalised before use).</summary>
    public double Jx { get; init; } = 1;
    public double Jy { get; init; }

    /// <summary>Phases in degrees, OpticStudio's convention.</summary>
    public double XPhaseDegrees { get; init; }
    public double YPhaseDegrees { get; init; }

    public PolarizationReference Reference { get; init; } = PolarizationReference.XAxis;

    /// <summary>Measure coating phases along the ray from where the coating begins instead of
    /// along the normal across the stack (help, "Convert Thin Film Phase to Ray Equivalent").</summary>
    public bool ConvertThinFilmPhase { get; init; } = true;

    public static SystemPolarization Default { get; } = new();

    /// <summary>The normalised Jones vector (ignored when <see cref="Unpolarized"/>).</summary>
    public JonesVector ToJones() => JonesVector.FromAmplitudes(Jx, Jy, XPhaseDegrees, YPhaseDegrees);

    /// <summary>Adds one line per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        foreach (var (name, v) in new[] { ("Jx", Jx), ("Jy", Jy), ("X-phase", XPhaseDegrees), ("Y-phase", YPhaseDegrees) })
            if (!double.IsFinite(v))
                problems.Add($"Polarization: {name} must be a number (it is {v.ToString(CultureInfo.InvariantCulture)}).");
        if (!Unpolarized && double.IsFinite(Jx) && double.IsFinite(Jy) && Jx == 0 && Jy == 0)
            problems.Add("Polarization: Jx and Jy are both zero; give a non-zero Jones vector or choose unpolarized.");
    }
}
