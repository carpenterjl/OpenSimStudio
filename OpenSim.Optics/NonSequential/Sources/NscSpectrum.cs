using System.Globalization;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// The "Source Color" models of the Sources tab, in the order the help lists them (help,
/// "Sources", "Color/Spectrum"; ZPL SETNSCPROPERTY code 113 numbers them "starting with 0 for
/// the System Wavelengths, and then from 1 through the last model listed in the dialog box
/// control"; that the dialog lists them in the help's order is a reading, Conventions §10 item 68).
/// </summary>
public enum NscColorModel
{
    SystemWavelengths = 0,
    Cie1931Xyz = 1,
    Cie1931Xy = 2,
    Cie1931Rgb = 3,
    UniformPowerSpectrum = 4,
    D65White = 5,
    ColorTemperature = 6,
    BlackBodySpectrum = 7,
    SpectrumFile = 8,
    Cie1976UV = 9,
}

/// <summary>One wavelength of a source spectrum: µm in the system's air and a relative power
/// weight (dimensionless).</summary>
public readonly record struct NscSpectralLine(double Micrometers, double Weight);

/// <summary>
/// A source's spectral model (help, "Sources", "Color/Spectrum"). Built:
/// <list type="bullet">
/// <item><b>System Wavelength</b>: the system wavelengths and their weights; the source's
/// wavenumber picks one row, or 0 uses all of them by weight (help, "Parameters Common to All
/// Source Objects").</item>
/// <item><b>Uniform Power Spectrum</b>: equal weights at <see cref="Count"/> wavelengths from
/// <see cref="FromMicrometers"/> to <see cref="ToMicrometers"/>, ends included and equally
/// spaced (the spacing is a reading: the help says only "at the number of discrete wavelengths
/// defined").</item>
/// <item><b>Black Body Spectrum</b>: the same wavelengths weighted by Planck's spectral radiance
/// per unit wavelength, B_λ(λ, T) = (2hc²/λ⁵)/(exp(hc/(λk_BT)) − 1) (Wikipedia, "Planck's law",
/// the B_λ form in the introduction), λ the vacuum wavelength, with h, c and k_B the exact SI
/// values (NIST, CODATA 2022: h = 6.626 070 15 × 10⁻³⁴ J Hz⁻¹, c = 299 792 458 m s⁻¹,
/// k = 1.380 649 × 10⁻²³ J K⁻¹; plans/sources/optics/op-77-help/nist/Value_h.html, Value_c.html,
/// Value_k.html). With equal spacing the interval width is the same for every line, so B_λ is the
/// weight.</item>
/// <item><b>Spectrum File</b>: the pairs of <see cref="Table"/> ("wavelength values ... in
/// micrometers and must be listed in ascending order ... At least 3 but no more than 200 pairs";
/// help, "Defining a spectrum file"). Reading the .SPCD file itself is plan item OP-78; the table
/// is given here.</item>
/// </list>
/// Refused by name: the CIE 1931 XYZ, xy and RGB models, D65 White, Color Temperature and CIE
/// 1976 u′v′ all fit a spectrum to a colour with OpticStudio's spectrum-fitting algorithm and the
/// CIE colour-matching functions (OP-79 bundles the functions; the fitting algorithm is not built).
/// <para>
/// Rays pick their wavelength at random by weight ("A wavelength with twice the relative weight
/// as another wavelength will trace twice as often"; help, "How rays are selected from the
/// spectrum"), each ray carrying the same power.
/// </para>
/// </summary>
public sealed record NscSpectrum
{
    public NscColorModel Model { get; init; } = NscColorModel.SystemWavelengths;
    public double TemperatureKelvin { get; init; } = 5400;
    public int Count { get; init; } = 50;
    public double FromMicrometers { get; init; } = 0.4;
    public double ToMicrometers { get; init; } = 0.8;
    public IReadOnlyList<NscSpectralLine>? Table { get; init; }

    /// <summary>Planck constant, J Hz⁻¹ (NIST CODATA 2022, exact).</summary>
    public const double Planck = 6.62607015e-34;

    /// <summary>Speed of light in vacuum, m s⁻¹ (NIST CODATA 2022, exact).</summary>
    public const double SpeedOfLight = 299792458;

    /// <summary>Boltzmann constant, J K⁻¹ (NIST CODATA 2022, exact).</summary>
    public const double Boltzmann = 1.380649e-23;

    public void Validate(string where, List<string> problems)
    {
        switch (Model)
        {
            case NscColorModel.SystemWavelengths:
                return;
            case NscColorModel.UniformPowerSpectrum:
            case NscColorModel.BlackBodySpectrum:
                if (Count < 2) problems.Add($"{where}: the spectrum needs at least 2 wavelengths; it has {Count}.");
                if (!(FromMicrometers > 0) || !(ToMicrometers > FromMicrometers) || !double.IsFinite(ToMicrometers))
                    problems.Add($"{where}: the spectrum's wavelengths must satisfy 0 < from < to (µm); they are {F(FromMicrometers)} and {F(ToMicrometers)}.");
                if (Model == NscColorModel.BlackBodySpectrum && (!(TemperatureKelvin > 0) || !double.IsFinite(TemperatureKelvin)))
                    problems.Add($"{where}: the black body temperature must be positive (K); it is {F(TemperatureKelvin)}.");
                return;
            case NscColorModel.SpectrumFile:
                if (Table is not { } t) { problems.Add($"{where}: the spectrum table is empty (reading .SPCD files is plan item OP-78; give the pairs)."); return; }
                if (t.Count < 3 || t.Count > 200) problems.Add($"{where}: a spectrum has 3 to 200 pairs (help, \"Defining a spectrum file\"); it has {t.Count}.");
                for (int i = 0; i < t.Count; i++)
                {
                    if (!(t[i].Micrometers > 0) || !double.IsFinite(t[i].Micrometers)) problems.Add($"{where}: spectrum pair {i + 1} has wavelength {F(t[i].Micrometers)} µm; it must be positive.");
                    if (!(t[i].Weight >= 0) || !double.IsFinite(t[i].Weight)) problems.Add($"{where}: spectrum pair {i + 1} has weight {F(t[i].Weight)}; it must be zero or positive.");
                    if (i > 0 && !(t[i].Micrometers > t[i - 1].Micrometers)) problems.Add($"{where}: spectrum pair {i + 1}: the wavelengths must be in ascending order.");
                }
                if (t.All(l => l.Weight == 0)) problems.Add($"{where}: every spectrum weight is zero.");
                return;
            case NscColorModel.Cie1931Xyz:
            case NscColorModel.Cie1931Xy:
            case NscColorModel.Cie1931Rgb:
            case NscColorModel.D65White:
            case NscColorModel.ColorTemperature:
            case NscColorModel.Cie1976UV:
                problems.Add($"{where}: the source colour model '{Name(Model)}' is not built: it fits a spectrum to a colour with OpticStudio's spectrum-fitting algorithm and the CIE colour-matching functions (plan item OP-79 bundles the functions; the fitting is not built). Use System Wavelength, Uniform Power Spectrum, Black Body Spectrum or a spectrum table.");
                return;
            default:
                problems.Add($"{where}: unknown source colour model {(int)Model}.");
                return;
        }
    }

    /// <summary>OpticStudio's name of a colour model.</summary>
    public static string Name(NscColorModel model) => model switch
    {
        NscColorModel.SystemWavelengths => "System Wavelength",
        NscColorModel.Cie1931Xyz => "CIE 1931 Tristimulus XYZ",
        NscColorModel.Cie1931Xy => "CIE 1931 Chromaticity xy",
        NscColorModel.Cie1931Rgb => "CIE 1931 RGB (Saturated)",
        NscColorModel.UniformPowerSpectrum => "Uniform Power Spectrum",
        NscColorModel.D65White => "D65 White",
        NscColorModel.ColorTemperature => "Color Temperature",
        NscColorModel.BlackBodySpectrum => "Black Body Spectrum",
        NscColorModel.SpectrumFile => "Spectrum File",
        NscColorModel.Cie1976UV => "CIE 1976 Chromaticity u' v'",
        _ => $"model {(int)model}",
    };

    /// <summary>
    /// The wavelengths and weights rays are drawn from, zero weights dropped.
    /// </summary>
    /// <param name="systemWavelengths">The system wavelengths (µm in air) and weights, row 1 first.</param>
    /// <param name="wavenumber">The source's parameter 4: 0 for every system wavelength, else a row.</param>
    /// <param name="vacuumMicrometers">Air µm to vacuum µm (the system environment), for Planck's law.</param>
    /// <param name="where">Names the source in messages.</param>
    public IReadOnlyList<NscSpectralLine> Lines(IReadOnlyList<NscSpectralLine> systemWavelengths, int wavenumber,
        Func<double, double> vacuumMicrometers, string where)
    {
        var problems = new List<string>();
        Validate(where, problems);
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        List<NscSpectralLine> lines;
        switch (Model)
        {
            case NscColorModel.SystemWavelengths:
                ArgumentNullException.ThrowIfNull(systemWavelengths);
                if (systemWavelengths.Count == 0) throw new ArgumentException($"{where}: the system has no wavelengths.");
                if (wavenumber < 0 || wavenumber > systemWavelengths.Count)
                    throw new ArgumentException($"{where}: the wavenumber {wavenumber} is not 0 or a wavelength row (1 to {systemWavelengths.Count}).");
                lines = wavenumber == 0
                    ? systemWavelengths.ToList()
                    : new List<NscSpectralLine> { systemWavelengths[wavenumber - 1] with { Weight = 1 } };
                break;
            case NscColorModel.UniformPowerSpectrum:
            case NscColorModel.BlackBodySpectrum:
                lines = new List<NscSpectralLine>(Count);
                for (int i = 0; i < Count; i++)
                {
                    double air = FromMicrometers + (ToMicrometers - FromMicrometers) * i / (Count - 1);
                    double w = Model == NscColorModel.UniformPowerSpectrum ? 1 : PlanckRelative(vacuumMicrometers(air), TemperatureKelvin);
                    lines.Add(new NscSpectralLine(air, w));
                }
                break;
            default:
                lines = Table!.ToList();
                break;
        }
        foreach (var l in lines)
            if (!(l.Micrometers > 0) || !double.IsFinite(l.Micrometers) || !(l.Weight >= 0) || !double.IsFinite(l.Weight))
                throw new ArgumentException($"{where}: wavelength {F(l.Micrometers)} µm with weight {F(l.Weight)} is not a usable spectral line.");
        lines.RemoveAll(l => l.Weight == 0);
        if (lines.Count == 0) throw new ArgumentException($"{where}: every wavelength of the source's spectrum has zero weight.");
        return lines;
    }

    /// <summary>Planck's B_λ(λ, T) in W sr⁻¹ m⁻³ for the vacuum wavelength in µm (formula and
    /// constants as in the class summary). expm1 keeps the long-wavelength limit accurate.</summary>
    public static double PlanckRelative(double vacuumMicrometers, double kelvin)
    {
        double lambda = vacuumMicrometers * 1e-6;
        double x = Planck * SpeedOfLight / (lambda * Boltzmann * kelvin);
        double l5 = lambda * lambda * lambda * lambda * lambda;
        return 2 * Planck * SpeedOfLight * SpeedOfLight / l5 / double.ExpM1(x);
    }

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
