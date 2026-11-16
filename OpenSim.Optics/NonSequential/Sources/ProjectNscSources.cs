using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;


namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// The saved source data of a project (plan item OP-77): a source row's Sources tab
/// (<see cref="OpticsNscSourceSettings"/>) as <see cref="NscSourceProperties"/>, the system's
/// source unit (the System Explorer's, already in the system data), and the emission options from the system wavelengths. Lengths are converted
/// from the lens unit; unknown names are reported, never guessed.
/// </summary>
public static class ProjectNscSources
{
    /// <summary>The source properties of a saved row; null when <paramref name="saved"/> has
    /// problems (each appended to <paramref name="problems"/> naming <paramref name="where"/>).</summary>
    public static NscSourceProperties? Properties(OpticsNscSourceSettings? saved, LensUnit unit, string where, List<string> problems)
    {
        if (saved is null) return NscSourceProperties.Default;
        int before = problems.Count;
        double L(double? v) => v is double d ? OpticsConventions.ToMeters(d, unit) : 0;
        var sampling = Parse(saved.Sampling, "sampling method", where, problems, NscSourceSampling.Sobol,
            ("Sobol", NscSourceSampling.Sobol), ("Random", NscSourceSampling.Random));
        var bulk = Parse(saved.BulkScatter, "bulk scatter", where, problems, NscSourceBulkScatter.Many,
            ("Many", NscSourceBulkScatter.Many), ("Once", NscSourceBulkScatter.Once), ("Never", NscSourceBulkScatter.Never));
        var array = Parse(saved.ArrayType, "array type", where, problems, NscSourceArrayType.None,
            ("None", NscSourceArrayType.None), ("Rectangular", NscSourceArrayType.Rectangular), ("Circular", NscSourceArrayType.Circular),
            ("Hexapolar", NscSourceArrayType.Hexapolar), ("Hexagonal", NscSourceArrayType.Hexagonal));
        var models = Enum.GetValues<NscColorModel>().Select(m => (NscSpectrum.Name(m), m)).ToArray();
        var model = Parse(saved.ColorModel, "source colour model", where, problems, NscColorModel.SystemWavelengths, models);

        List<NscSpectralLine>? table = null;
        if (saved.SpectrumWavelengths is not null || saved.SpectrumWeights is not null)
        {
            var w = saved.SpectrumWavelengths ?? new List<double>();
            var g = saved.SpectrumWeights ?? new List<double>();
            if (w.Count != g.Count) problems.Add($"{where}: the spectrum table has {w.Count} wavelengths and {g.Count} weights.");
            else table = w.Zip(g, (a, b) => new NscSpectralLine(a, b)).ToList();
        }
        var spectrumDefault = new NscSpectrum();
        var props = new NscSourceProperties
        {
            Polarization = new NscSourcePolarization
            {
                Random = saved.RandomPolarization ?? true,
                Jx = saved.Jx ?? 1, Jy = saved.Jy ?? 0,
                XPhaseDegrees = saved.XPhase ?? 0, YPhaseDegrees = saved.YPhase ?? 0,
            },
            InitialPhaseDegrees = saved.InitialPhase ?? 0,
            CoherenceLength = L(saved.CoherenceLength),
            ReverseRays = saved.ReverseRays ?? false,
            PrePropagation = L(saved.PrePropagation),
            Sampling = sampling,
            BulkScatter = bulk,
            Array = new NscSourceArray
            {
                Type = array, Count1 = saved.ArrayCount1 ?? 1, Count2 = saved.ArrayCount2 ?? 1,
                Size1 = L(saved.ArraySize1), Size2 = L(saved.ArraySize2),
            },
            Spectrum = new NscSpectrum
            {
                Model = model,
                TemperatureKelvin = saved.Temperature ?? spectrumDefault.TemperatureKelvin,
                Count = saved.SpectrumCount ?? spectrumDefault.Count,
                FromMicrometers = saved.WavelengthFrom ?? spectrumDefault.FromMicrometers,
                ToMicrometers = saved.WavelengthTo ?? spectrumDefault.ToMicrometers,
                Table = table,
            },
        };
        // The values themselves (a CIE colour model, a zero Jones vector) are checked when the
        // source emits, so a scene with such a source still loads for the other analyses.
        return problems.Count > before ? null : props;
    }

    /// <summary>The emission options of a project: its system wavelengths with their weights,
    /// its source units, and the CIE photopic luminous efficiency V(λ)
    /// (<see cref="Photometry.Photometric.PhotopicEfficiency"/>, OP-79) for lumens, unless
    /// <paramref name="luminousEfficiency"/> gives another.</summary>
    public static NscSourceEmitOptions EmitOptions(OpticsAnalysisContext context, NscEmissionPurpose purpose,
        Func<double, double>? luminousEfficiency = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        return new NscSourceEmitOptions
        {
            Purpose = purpose,
            SystemWavelengths = system.Wavelengths.Wavelengths.Select(w => new NscSpectralLine(w.Micrometers, w.Weight)).ToArray(),
            Units = system.Units.SourceUnit,
            LuminousEfficiency = luminousEfficiency ?? Photometry.Photometric.PhotopicEfficiency,
        };
    }

    /// <summary>
    /// Traces a project's own sources (wave 15 wiring): the saved scene
    /// (<see cref="ProjectNonSequential.Scene"/>), the saved non-sequential controls on top of the
    /// system environment, coatings and primary wavelength, the analysis ray counts and the
    /// project's emission options (V(λ) included), through <see cref="NscSourceRun.Run"/>. The
    /// Ray Trace control's choices are the arguments: which sources (null for all), splitting
    /// (<paramref name="split"/>: OpticStudio's "Split NSC Rays", simple when the system says
    /// so), polarization, Ignore Errors and the seed (null: the settings' default). Throws
    /// <see cref="ArgumentException"/> naming the problem when the system has no objects or no
    /// sources.
    /// </summary>
    public static NscSourceRunResult Trace(OpticsAnalysisContext context, IReadOnlyList<int>? sources = null, bool split = false,
        bool usePolarization = false, bool ignoreErrors = false, long? seed = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var unit = system.Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException("The system has no non-sequential objects, so there are no sources to trace.");
        var saved = context.System.NonSequential;
        var baseline = new NscTraceSettings
        {
            UsePolarization = usePolarization,
            Splitting = !split ? NscSplitting.None : saved?.SimpleRaySplitting == true ? NscSplitting.Simple : NscSplitting.Split,
            IgnoreErrors = ignoreErrors,
            Environment = system.Environment,
            Coatings = OpenSim.Optics.Coatings.CoatingCatalogSettings.FromSettings(context.System.Coatings, notes),
            PrimaryMicrometers = system.Wavelengths.Primary.Micrometers,
        };
        if (seed is long s) baseline = baseline with { Seed = s };
        var settings = ProjectNonSequential.Settings(saved, unit, baseline);
        var run = NscSourceRun.Run(scene, settings, EmitOptions(context, NscEmissionPurpose.Analysis), sources, cancellation);
        return notes.Count == 0 ? run : run with { Notes = notes.Concat(run.Notes).ToArray() };
    }

    private static T Parse<T>(string? text, string what, string where, List<string> problems, T fallback, params (string Name, T Value)[] names)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return fallback;
        foreach (var (name, value) in names)
            if (string.Equals(name, t, StringComparison.Ordinal)) return value;
        problems.Add($"{where}: the {what} '{t}' is not one of {string.Join(", ", names.Select(n => n.Name))}.");
        return fallback;
    }
}
