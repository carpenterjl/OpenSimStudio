using System.Globalization;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>The "Model" of an object's Volume Physics section (help, "Volume Physics").</summary>
public enum NscVolumeModel
{
    /// <summary>"rays will propagate through the solid without scattering".</summary>
    None = 0,
    /// <summary>Mean path and cone angle (help, "Angle Scattering", "Bulk scattering").</summary>
    AngleScattering = 1,
    /// <summary>A bulk-scatter DLL. This program builds the help's sample DLLs
    /// Henyey-Greenstein-bulk, Rayleigh and Mie first-party; any other DLL is refused.</summary>
    DllDefined = 2,
    /// <summary>Photoluminescence with optional Mie scattering (help, "Phosphors &amp; Fluorescence").</summary>
    PhosphorsAndFluorescence = 3,
}

/// <summary>"Basic" or "Standard" photoluminescence (help, "Phosphors &amp; Fluorescence").</summary>
public enum NscPhotoluminescenceModel
{
    Standard = 0,
    Basic = 1,
}

/// <summary>The first-party stand-ins for the help's sample bulk-scatter DLLs.</summary>
public enum NscBulkDll
{
    /// <summary>Henyey-Greenstein-bulk.DLL; parameter 1 is g.</summary>
    HenyeyGreenstein = 0,
    /// <summary>Rayleigh.DLL; parameter 1 the Reference Wavelength (µm, the wavelength of the Mean
    /// Path), parameter 2 the Transmission (the fraction kept at each event).</summary>
    Rayleigh = 1,
    /// <summary>Mie.DLL; parameter 1 the particle index, 2 the particle radius (µm), 3 the particle
    /// density (cm⁻³; 0 uses the Mean Path).</summary>
    Mie = 2,
}

/// <summary>
/// The wavelength shift string (help, "Wavelength Shift"): "in, out, prob" triples separated by
/// semicolons, in and out system wavelength numbers, prob "the relative probability that this shift
/// will occur when tracing the in wavelength"; "The probability values are only relative to other
/// wavelength shifts defined for the same input wavelength". A wavelength with no triple does not
/// shift.
/// </summary>
public sealed record NscWavelengthShift
{
    public required IReadOnlyList<(int In, int Out, double Weight)> Transitions { get; init; }

    public string Text { get; init; } = "";

    /// <summary>Parses the string; throws <see cref="FormatException"/> naming the triple.</summary>
    public static NscWavelengthShift Parse(string text)
    {
        var list = new List<(int, int, double)>();
        int k = 0;
        foreach (var part in (text ?? "").Split(';'))
        {
            k++;
            var t = part.Trim();
            if (t.Length == 0) continue;
            var f = t.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length != 3
                || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)
                || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)
                || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                throw new FormatException($"Wavelength shift \"{text}\", entry {k} (\"{t}\"): expected \"in, out, prob\" (help, \"Wavelength Shift\").");
            if (a < 1 || b < 1) throw new FormatException($"Wavelength shift entry {k} (\"{t}\"): wavelength numbers start at 1.");
            if (!(p >= 0) || !double.IsFinite(p)) throw new FormatException($"Wavelength shift entry {k} (\"{t}\"): the probability must be zero or positive.");
            list.Add((a, b, p));
        }
        foreach (var g in list.GroupBy(x => x.Item1))
            if (!(g.Sum(x => x.Item3) > 0))
                throw new FormatException($"Wavelength shift \"{text}\": the probabilities from wavelength {g.Key} sum to zero.");
        return new NscWavelengthShift { Transitions = list, Text = text ?? "" };
    }

    /// <summary>The output wavelength number for input <paramref name="number"/> drawn with
    /// <paramref name="u"/>; the input itself when it has no transition.</summary>
    public int Choose(int number, double u)
    {
        double total = 0;
        foreach (var t in Transitions) if (t.In == number) total += t.Weight;
        if (!(total > 0)) return number;
        double target = u * total, acc = 0;
        int last = number;
        foreach (var t in Transitions)
        {
            if (t.In != number) continue;
            last = t.Out;
            acc += t.Weight;
            if (target < acc) return t.Out;
        }
        return last;
    }
}

/// <summary>The Mie particles of a phosphor host or of the Mie DLL: index, radius (µm) and either
/// the number density (cm⁻³) or a mean free path (meters).</summary>
public sealed record NscMieParticles
{
    public double Index { get; init; } = 1.5;
    public double RadiusMicrometers { get; init; } = 1;
    public double DensityPerCm3 { get; init; }
    public double MeanFreePath { get; init; }
    public bool UseMeanFreePath { get; init; }

    public void Validate(string where, List<string> problems)
    {
        if (!(Index > 0) || !double.IsFinite(Index)) problems.Add($"{where}: the particle index must be positive; it is {Index}.");
        if (!(RadiusMicrometers > 0) || !double.IsFinite(RadiusMicrometers)) problems.Add($"{where}: the particle radius must be positive (µm); it is {RadiusMicrometers}.");
        if (UseMeanFreePath)
        {
            if (!(MeanFreePath > 0) || !double.IsFinite(MeanFreePath)) problems.Add($"{where}: the Mie mean free path must be positive; it is {MeanFreePath}.");
        }
        else if (!(DensityPerCm3 > 0) || !double.IsFinite(DensityPerCm3))
            problems.Add($"{where}: the particle density must be positive (cm⁻³); it is {DensityPerCm3}.");
    }
}

/// <summary>
/// Phosphors and fluorescence (help, "Phosphors &amp; Fluorescence"). Standard model: the
/// photoluminescence mean free path l(λ) = [ε(λ)·c·ln 10]⁻¹ with ε(λ) = ε(λ_x)/A_meas(λ_x)·A_meas(λ)
/// and c the molar concentration "converted from the user-supplied density"
/// (c = density·1000/N_A mol/L, N_A = 6.022 140 76 × 10²³ mol⁻¹ exactly, Wikipedia "Avogadro
/// constant" citing the 2019 SI); outside the absorption spectrum the ray "will not be absorbed".
/// </summary>
public sealed record NscPhosphor
{
    /// <summary>Avogadro constant, mol⁻¹: "the exact value 6.022 140 76 × 10²³ mol⁻¹" (Wikipedia,
    /// "Avogadro constant", the 2019 SI definition).</summary>
    public const double Avogadro = 6.02214076e23;

    public NscPhotoluminescenceModel Model { get; init; }
    public required NscSpectrumTable Absorption { get; init; }
    public required NscEmissionTable Emission { get; init; }
    public required NscSpectrumTable QuantumYield { get; init; }

    /// <summary>ε at <see cref="ExtinctionMicrometers"/>, M⁻¹ cm⁻¹ (L mol⁻¹ cm⁻¹).</summary>
    public double ExtinctionCoefficient { get; init; }
    public double ExtinctionMicrometers { get; init; }

    /// <summary>Phosphor density, cm⁻³.</summary>
    public double DensityPerCm3 { get; init; }

    public bool ConsiderMie { get; init; }
    public NscMieParticles? Mie { get; init; }

    /// <summary>c in mol/L.</summary>
    public double MolarConcentration => DensityPerCm3 * 1000 / Avogadro;

    /// <summary>The photoluminescence mean free path at <paramref name="airMicrometers"/>, meters;
    /// +∞ outside the absorption spectrum or where it is zero (Standard model).</summary>
    public double PhotoluminescenceMeanFreePath(double airMicrometers)
    {
        if (!Absorption.Covers(airMicrometers)) return double.PositiveInfinity;
        double ax = Absorption.At(ExtinctionMicrometers), a = Absorption.At(airMicrometers);
        double epsilon = ExtinctionCoefficient / ax * a;        // L mol⁻¹ cm⁻¹
        double perCm = epsilon * MolarConcentration * Math.Log(10);
        return perCm > 0 ? 0.01 / perCm : double.PositiveInfinity;   // cm → m
    }

    public void Validate(string where, List<string> problems)
    {
        if (!Enum.IsDefined(Model)) problems.Add($"{where}: unknown photoluminescence model {(int)Model}.");
        if (Model == NscPhotoluminescenceModel.Standard)
        {
            if (!(ExtinctionCoefficient > 0) || !double.IsFinite(ExtinctionCoefficient)) problems.Add($"{where}: the extinction coefficient must be positive (M⁻¹ cm⁻¹); it is {ExtinctionCoefficient}.");
            if (!(DensityPerCm3 > 0) || !double.IsFinite(DensityPerCm3)) problems.Add($"{where}: the phosphor density must be positive (cm⁻³); it is {DensityPerCm3}.");
            if (!(ExtinctionMicrometers >= Absorption.Min && ExtinctionMicrometers <= Absorption.Max))
                problems.Add($"{where}: the extinction wavelength {ExtinctionMicrometers} µm \"must be between the minimum and maximum wavelengths defined in the absorption spectrum\" ({Absorption.Min}–{Absorption.Max} µm).");
            else if (!(Absorption.At(ExtinctionMicrometers) > 0))
                problems.Add($"{where}: the absorption spectrum is zero at the extinction wavelength {ExtinctionMicrometers} µm, so ε(λ) = ε(λx)/A(λx)·A(λ) is not defined.");
            if (ConsiderMie)
            {
                if (Mie is null) problems.Add($"{where}: Consider Mie Scattering is on but no Mie particles are given.");
                else Mie.Validate(where + ", Mie scattering", problems);
            }
        }
        else
        {
            if (Mie is null) problems.Add($"{where}: the Basic model draws its events from the Mie parameters; none are given.");
            else Mie.Validate(where + ", Mie scattering", problems);
            if (Absorption.Values.Any(v => v > 1)) problems.Add($"{where}: the Basic model reads the absorption spectrum as the probability that a photon is absorbed; {Absorption.Name} has values above 1.");
        }
        if (QuantumYield.Values.Any(v => v > 1)) problems.Add($"{where}: {QuantumYield.Name} has quantum yields above 1; this program refuses them (they would create energy).");
    }
}

/// <summary>
/// An object's Volume Physics section (plan item OP-81; docs/Optics-Conventions.md §77): the bulk
/// scattering model with its mean path (meters) and cone angle, the DLL and its parameters, the
/// wavelength shift and the phosphor. Allowed on refractive volumes only.
/// </summary>
public sealed record NscVolumePhysics
{
    public NscVolumeModel Model { get; init; }

    /// <summary>Meters.</summary>
    public double MeanPath { get; init; }

    public double AngleDegrees { get; init; }

    /// <summary>The DLL name as given (DLL Defined Scattering).</summary>
    public string? Dll { get; init; }

    public IReadOnlyList<double> DllParameters { get; init; } = Array.Empty<double>();

    public NscWavelengthShift? WavelengthShift { get; init; }

    public NscPhosphor? Phosphor { get; init; }

    /// <summary>The DLL names built here (with or without ".dll", any case).</summary>
    public static IReadOnlyList<string> BuiltDllNames { get; } = new[] { "Henyey-Greenstein-bulk.dll", "Rayleigh.dll", "Mie.dll" };

    /// <summary>The built DLL a name names, or null.</summary>
    public static NscBulkDll? DllOf(string? name)
    {
        string t = (name ?? "").Trim();
        if (t.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        return t.ToLowerInvariant() switch
        {
            "henyey-greenstein-bulk" => NscBulkDll.HenyeyGreenstein,
            "rayleigh" => NscBulkDll.Rayleigh,
            "mie" => NscBulkDll.Mie,
            _ => null,
        };
    }

    /// <summary>Parameter <paramref name="i"/> (1-based), 0 when not given.</summary>
    public double Parameter(int i) => i >= 1 && i <= DllParameters.Count ? DllParameters[i - 1] : 0;

    /// <summary>The Mie particles of the Mie DLL (density 0 uses the Mean Path).</summary>
    public NscMieParticles DllMie => new()
    {
        Index = Parameter(1), RadiusMicrometers = Parameter(2), DensityPerCm3 = Parameter(3),
        MeanFreePath = MeanPath, UseMeanFreePath = Parameter(3) == 0,
    };

    public void Validate(string where, List<string> problems)
    {
        where += ", Volume Physics";
        switch (Model)
        {
            case NscVolumeModel.None:
                if (WavelengthShift is { Transitions.Count: > 0 }) problems.Add($"{where}: a wavelength shift is set but the model is None; shifts happen at bulk scatter events.");
                return;
            case NscVolumeModel.AngleScattering:
                MeanPathCheck();
                if (!(AngleDegrees >= 0 && AngleDegrees <= 180)) problems.Add($"{where}: the angle must lie in 0–180° (help, \"Bulk scattering\"); it is {AngleDegrees}.");
                break;
            case NscVolumeModel.DllDefined:
                switch (DllOf(Dll))
                {
                    case null:
                        problems.Add($"{where}: the bulk-scatter DLL '{Dll}' is not built; no DLLs are loaded. This program builds {string.Join(", ", BuiltDllNames)} (the help's sample DLLs) first-party.");
                        break;
                    case NscBulkDll.HenyeyGreenstein:
                        MeanPathCheck();
                        if (!double.IsFinite(Parameter(1))) problems.Add($"{where}: Henyey-Greenstein g must be a finite number.");
                        break;
                    case NscBulkDll.Rayleigh:
                        MeanPathCheck();
                        if (!(Parameter(1) > 0) || !double.IsFinite(Parameter(1))) problems.Add($"{where}: the Rayleigh reference wavelength (parameter 1, µm) must be positive; it is {Parameter(1)}.");
                        if (!(Parameter(2) >= 0 && Parameter(2) <= 1)) problems.Add($"{where}: the Rayleigh transmission (parameter 2) must lie in 0–1; it is {Parameter(2)}.");
                        break;
                    case NscBulkDll.Mie:
                        DllMie.Validate(where + ", Mie.dll", problems);
                        break;
                }
                break;
            case NscVolumeModel.PhosphorsAndFluorescence:
                if (Phosphor is null) problems.Add($"{where}: Phosphors and Fluorescence needs the absorption, emission and quantum yield spectra.");
                else Phosphor.Validate(where, problems);
                if (WavelengthShift is { Transitions.Count: > 0 })
                    problems.Add($"{where}: a wavelength shift string applies to Angle and DLL Defined Scattering; with Phosphors and Fluorescence the emission spectrum sets the new wavelength. Clear one.");
                break;
            default:
                problems.Add($"{where}: unknown model {(int)Model}.");
                break;
        }

        void MeanPathCheck()
        {
            if (!(MeanPath > 0) || !double.IsFinite(MeanPath)) problems.Add($"{where}: the mean path must be positive; it is {MeanPath} m.");
        }
    }

    /// <summary>A one-line description for notes.</summary>
    public string Describe() => Model switch
    {
        NscVolumeModel.AngleScattering => $"Angle Scattering, mean path {MeanPath:g6} m, cone {AngleDegrees:g6}°" + Shift(),
        NscVolumeModel.DllDefined => $"{Dll} (first-party), mean path {MeanPath:g6} m, parameters {string.Join(", ", DllParameters.Select(p => p.ToString("g6", CultureInfo.InvariantCulture)))}" + Shift(),
        NscVolumeModel.PhosphorsAndFluorescence => $"Phosphors and Fluorescence ({Phosphor?.Model}{(Phosphor?.ConsiderMie == true ? ", Mie scattering" : "")})",
        _ => "none",
    };

    private string Shift() => WavelengthShift is { Transitions.Count: > 0 } w ? $", wavelength shift \"{w.Text}\"" : "";
}
