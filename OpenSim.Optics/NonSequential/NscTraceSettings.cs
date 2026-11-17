using System.Globalization;
using OpenSim.Optics.Coatings;

namespace OpenSim.Optics.NonSequential;

/// <summary>What happens at a refractive interface (OpticStudio help, "Ray Splitting" and
/// "Simple Ray Splitting").</summary>
public enum NscSplitting
{
    /// <summary>"The transmitted path is always taken at a refractive interface unless the ray
    /// TIRs." With energy accounted, the reflected part is not traced and the ledger lists it as
    /// "reflected, not traced".</summary>
    None = 0,

    /// <summary>"Both the reflected and refracted rays are traced. Each ray gets the fraction of
    /// energy corresponding to the reflection and transmission coefficients."</summary>
    Split = 1,

    /// <summary>Simple ray splitting: "either the reflected or the refracted ray is traced, but
    /// not both", chosen with the coefficients as relative probabilities, and the chosen ray
    /// gets "all the energy that would have propagated down both paths"; the absorbed part is
    /// still lost.</summary>
    Simple = 2,
}

/// <summary>
/// The controls of one non-sequential trace: OpticStudio's non-sequential System Explorer
/// settings (help, "Non-sequential (system explorer)") and the Ray Trace control's checkboxes
/// (help, "Ray Trace"). Lengths are meters inside the program.
/// </summary>
public sealed record NscTraceSettings
{
    /// <summary>"How many times a single ray may intersect objects along any single path from
    /// the original source parent ray to the final object intersection"; at most 4000 (help,
    /// "Maximum Intersections Per Ray"). This program's default is 4000.</summary>
    public int MaxIntersectionsPerRay { get; init; } = 4000;

    /// <summary>"The maximum number of segments per ray launched" (the whole tree of one launched
    /// ray); at most 2,000,000 (help, "Maximum Segments Per Ray"). This program's default 1,000,000.</summary>
    public int MaxSegmentsPerRay { get; init; } = 1_000_000;

    /// <summary>"How many objects can be inside, straddled, or in direct contact with each
    /// other"; the help's own minimum is 4 (help, "Object Placement", "Nesting object limits").
    /// This program's default 16.</summary>
    public int MaxNestedObjects { get; init; } = 16;

    /// <summary>A child ray below this fraction of its launched ray's starting intensity is
    /// terminated (help, "Minimum Relative Ray Intensity"). 0 turns it off. This program's
    /// default is 1e-6.</summary>
    public double MinimumRelativeIntensity { get; init; } = 1e-6;

    /// <summary>The same in source units (watts per ray here); 0 ignores it (help, "Minimum
    /// Absolute Ray Intensity").</summary>
    public double MinimumAbsoluteIntensity { get; init; }

    /// <summary>
    /// Meters. Intercepts nearer than this to the previous one are ignored, and intercepts on
    /// different objects within this distance of each other are one contact between touching
    /// objects (help, "Glue Distance In Lens Units"; OpticStudio limits it to 1e-10–1e-3 lens
    /// units). This program's default, 1e-9 m, is its own choice: far above the rounding of
    /// points in a scene a few meters across (about 1e-15 m) and far below any gap meant to be
    /// physical. OpticStudio's own default was not found in its help.
    /// </summary>
    public double GlueDistance { get; init; } = 1e-9;

    /// <summary>Account for energy with the polarization ray trace: Fresnel and coating
    /// reflection, transmission and absorption at every face, the uncoated mirror's losses and
    /// bulk absorption (help, "Polarization and Thin Film Coatings"). Off, interfaces neither gain
    /// nor lose energy and mirrors reflect everything. On by default here (the more accurate
    /// model, docs/Optics-Conventions.md, the opening rules); splitting turns it on regardless,
    /// as OpticStudio's Ray Trace control does ("it is required for accurate ray splitting").</summary>
    public bool UsePolarization { get; init; } = true;

    public NscSplitting Splitting { get; init; } = NscSplitting.None;

    /// <summary>Monte Carlo run only (<see cref="NscTracer.Run"/>): when false, the first ray trace
    /// error stops the run with an exception naming the ray, object and cause; when true the ray
    /// is terminated, its energy is counted as lost to errors, and the run goes on (help, "Ray
    /// Trace", "Ignore Errors"). The single-ray trace never throws for a trace error; it marks the
    /// segment.</summary>
    public bool IgnoreErrors { get; init; }

    /// <summary>The seed of the random choices (simple ray splitting, random source sampling).
    /// Each launched ray draws from its own stream SplitMix64.ForStream(seed, ray number).</summary>
    public long Seed { get; init; } = 1;

    /// <summary>The thread limit of a Monte Carlo run ("# CPU's"); null for no limit. The result
    /// is the same to the bit at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>The system environment (air index model, temperature, pressure) the glass
    /// indices and the vacuum wavelength follow.</summary>
    public OpticsEnvironment Environment { get; init; } = new();

    /// <summary>The coating catalog face coatings are looked up in; null is an empty catalog.</summary>
    public CoatingCatalog? Coatings { get; init; }

    /// <summary>The primary wavelength (µm in air) relative coating thicknesses refer to; 0 uses
    /// the trace's own wavelength.</summary>
    public double PrimaryMicrometers { get; init; }

    /// <summary>The lens unit coating tapers read the face point in.</summary>
    public LensUnit LensUnit { get; init; } = LensUnit.Millimeter;

    // OP-80
    /// <summary>"Scatter NSC Rays" on the Ray Trace control ("If checked, rays will scatter
    /// according to the scatter properties of the interface", help "Ray Trace") and ZPL NSTR's
    /// scatter flag. The help gives no default; this program's is off, so a trace without it is
    /// the trace of OP-70 to the bit.</summary>
    public bool ScatterRays { get; init; }

    // OP-81
    /// <summary>The system wavelengths (µm in air, row 1 first) that wavelength shift strings
    /// number (help, "Wavelength Shift"); null when unknown, and then a scene with a wavelength
    /// shift is refused. <see cref="Sources.NscSourceRun"/> fills it from the emit options.</summary>
    public IReadOnlyList<double>? SystemWavelengths { get; init; }

    /// <summary>True when energy is accounted: <see cref="UsePolarization"/> or any splitting.</summary>
    public bool AccountsEnergy => UsePolarization || Splitting != NscSplitting.None;

    /// <summary>Every problem with the settings, one line each.</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (MaxIntersectionsPerRay < 1 || MaxIntersectionsPerRay > 4000)
            p.Add($"Maximum intersections per ray must lie in 1–4000 (help, \"Maximum Intersections Per Ray\"); it is {MaxIntersectionsPerRay}.");
        if (MaxSegmentsPerRay < 1 || MaxSegmentsPerRay > 2_000_000)
            p.Add($"Maximum segments per ray must lie in 1–2,000,000 (help, \"Maximum Segments Per Ray\"); it is {MaxSegmentsPerRay}.");
        if (MaxNestedObjects < 4)
            p.Add($"Maximum nested/touching objects must be at least 4 (help, \"Object Placement\"); it is {MaxNestedObjects}.");
        if (!(MinimumRelativeIntensity >= 0 && MinimumRelativeIntensity < 1))
            p.Add($"Minimum relative ray intensity must lie in [0, 1); it is {F(MinimumRelativeIntensity)}.");
        if (!(MinimumAbsoluteIntensity >= 0) || !double.IsFinite(MinimumAbsoluteIntensity))
            p.Add($"Minimum absolute ray intensity must be zero or positive; it is {F(MinimumAbsoluteIntensity)}.");
        if (!(GlueDistance > 0) || !double.IsFinite(GlueDistance))
            p.Add($"The glue distance must be positive (meters); it is {F(GlueDistance)}.");
        if (!Enum.IsDefined(Splitting)) p.Add($"Unknown splitting mode {(int)Splitting}.");
        if (MaxDegreeOfParallelism is int t && t < 1) p.Add($"The thread limit must be at least 1; it is {t}.");
        if (PrimaryMicrometers < 0 || !double.IsFinite(PrimaryMicrometers)) p.Add($"The primary wavelength must be zero or positive (µm); it is {F(PrimaryMicrometers)}.");
        if (SystemWavelengths is { } system && system.Any(w => !(w > 0) || !double.IsFinite(w)))   // OP-81
            p.Add("Every system wavelength must be positive (µm).");
        return p;
    }

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
