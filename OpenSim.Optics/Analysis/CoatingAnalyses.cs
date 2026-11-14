using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Coatings;

namespace OpenSim.Optics.Analysis;

/// <summary>What a coating plot shows.</summary>
public enum CoatingQuantity
{
    Reflection,
    Transmission,
    Absorption,
    Diattenuation,
    Phase,
    Retardance,
}

/// <summary>Settings of a coating plot against angle of incidence.</summary>
public sealed record CoatingVsAngleSettings
{
    [DisplayName("Coating"), Description("A coating of the catalog by name; blank for an uncoated surface.")]
    public string Coating { get; init; } = "";

    [DisplayName("Incident medium"), Description("Blank for air (index 1), an index such as 1.5, or a coating material or glass by name.")]
    public string IncidentMedium { get; init; } = "";

    [DisplayName("Substrate"), Description("Blank for air (index 1), an index such as 1.5, or a coating material or glass by name. 1.5 is only a starting value.")]
    public string Substrate { get; init; } = "1.5";

    [DisplayName("Mirror"), Description("The surface is a mirror: the coating's last layer is the substrate (uncoated: aluminium, 0.7 − 7.0i).")]
    public bool Mirror { get; init; }

    [DisplayName("Wavelength (µm)"), Description("0 for the system's primary wavelength.")]
    public double WavelengthMicrometers { get; init; }

    [DisplayName("Minimum angle (°)")]
    public double MinimumAngleDegrees { get; init; }

    [DisplayName("Maximum angle (°)"), Description("Below 90°.")]
    public double MaximumAngleDegrees { get; init; } = 89;

    [DisplayName("Points")]
    public int Points { get; init; } = 90;

    [DisplayName("Coating multiplier")]
    public double ThicknessMultiplier { get; init; } = 1;

    [DisplayName("Index offset")]
    public double IndexOffset { get; init; }

    [DisplayName("Extinction offset"), Description("Added to k; positive absorbs more.")]
    public double ExtinctionOffset { get; init; }

    [DisplayName("Taper x (lens units)")]
    public double TaperX { get; init; }

    [DisplayName("Taper y (lens units)")]
    public double TaperY { get; init; }

    [DisplayName("Surface radius (lens units)"), Description("For cosine tapers; 0 for a plane.")]
    public double SurfaceRadius { get; init; }
}

/// <summary>Settings of a coating plot against wavelength.</summary>
public sealed record CoatingVsWavelengthSettings
{
    [DisplayName("Coating"), Description("A coating of the catalog by name; blank for an uncoated surface.")]
    public string Coating { get; init; } = "";

    [DisplayName("Incident medium"), Description("Blank for air (index 1), an index such as 1.5, or a coating material or glass by name.")]
    public string IncidentMedium { get; init; } = "";

    [DisplayName("Substrate"), Description("Blank for air (index 1), an index such as 1.5, or a coating material or glass by name. 1.5 is only a starting value.")]
    public string Substrate { get; init; } = "1.5";

    [DisplayName("Mirror"), Description("The surface is a mirror: the coating's last layer is the substrate (uncoated: aluminium, 0.7 − 7.0i).")]
    public bool Mirror { get; init; }

    [DisplayName("Angle (°)")]
    public double AngleDegrees { get; init; }

    [DisplayName("Minimum wavelength (µm)"), Description("0 for the system's shortest wavelength.")]
    public double MinimumMicrometers { get; init; }

    [DisplayName("Maximum wavelength (µm)"), Description("0 for the system's longest wavelength.")]
    public double MaximumMicrometers { get; init; }

    [DisplayName("Points")]
    public int Points { get; init; } = 100;

    [DisplayName("Coating multiplier")]
    public double ThicknessMultiplier { get; init; } = 1;

    [DisplayName("Index offset")]
    public double IndexOffset { get; init; }

    [DisplayName("Extinction offset"), Description("Added to k; positive absorbs more.")]
    public double ExtinctionOffset { get; init; }

    [DisplayName("Taper x (lens units)")]
    public double TaperX { get; init; }

    [DisplayName("Taper y (lens units)")]
    public double TaperY { get; init; }

    [DisplayName("Surface radius (lens units)"), Description("For cosine tapers; 0 for a plane.")]
    public double SurfaceRadius { get; init; }
}

/// <summary>
/// The twelve coating plots (OP-15): reflection, transmission, absorption, diattenuation, phase
/// and retardance of one coating of the project's coating catalog, against angle of incidence or
/// against wavelength. The definitions are those of <see cref="CoatingResponse"/>.
/// </summary>
public static class CoatingAnalyses
{
    public static IEnumerable<IOpticsAnalysis> All()
    {
        foreach (var q in Enum.GetValues<CoatingQuantity>()) yield return new CoatingVsAngleAnalysis(q);
        foreach (var q in Enum.GetValues<CoatingQuantity>()) yield return new CoatingVsWavelengthAnalysis(q);
    }

    internal const string Category = "Coatings";

    internal static string QuantityName(CoatingQuantity q) => q.ToString();

    /// <summary>The common inputs of both plots, read from either settings record.</summary>
    internal sealed record Inputs(string Coating, string IncidentMedium, string Substrate, bool Mirror, int Points,
        double Multiplier, double IndexOffset, double ExtinctionOffset, double TaperX, double TaperY, double SurfaceRadius);

    /// <summary>Resolves the inputs against the snapshot and computes the curves.</summary>
    internal static OpticsAnalysisResult Compute(string title, CoatingQuantity quantity, OpticsAnalysisContext context, Inputs inputs,
        string xLabel, double xMin, double xMax, Func<double, (double Micrometers, double AngleDegrees)> conditionsAt,
        IEnumerable<(string, string)> axisPairs, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var catalog = CoatingCatalogSettings.FromSettings(context.System.Coatings, notes);
        var system = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>());
        double primary = system.Wavelengths.Primary.Micrometers;

        Coating? coating = null;
        if (inputs.Coating.Trim().Length > 0)
            coating = catalog.FindCoating(inputs.Coating.Trim())
                ?? throw new InvalidOperationException(
                    $"There is no coating '{inputs.Coating.Trim()}' in the coating catalog. " +
                    (catalog.Coatings.Count == 0 ? "The catalog is empty: add a coating in the Coatings panel or import a catalog." :
                        $"The catalog has: {string.Join(", ", catalog.Coatings.Select(c => c.Name))}."));
        if (inputs.Points < 2 || inputs.Points > 100_000)
            throw new InvalidOperationException($"Points must lie in 2–100000 (it is {inputs.Points}).");
        if (!(xMax > xMin))
            throw new InvalidOperationException($"The {xLabel.ToLowerInvariant()} range is empty: the maximum ({Num(xMax)}) must exceed the minimum ({Num(xMin)}).");

        bool incidentIsAir = inputs.IncidentMedium.Trim().Length == 0;
        bool substrateIsAir = inputs.Substrate.Trim().Length == 0;
        var adjust = new CoatingLayerAdjustment(inputs.Multiplier, inputs.IndexOffset, inputs.ExtinctionOffset);
        var x = new double[inputs.Points];
        var responses = new CoatingResponse[inputs.Points];
        for (int i = 0; i < inputs.Points; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            x[i] = i == inputs.Points - 1 ? xMax : xMin + (xMax - xMin) * i / (inputs.Points - 1);
            var (micrometers, angle) = conditionsAt(x[i]);
            var conditions = new CoatingConditions
            {
                Incident = Medium(inputs.IncidentMedium, micrometers, catalog, context, "incident medium"),
                Substrate = inputs.Mirror ? Complex.One : Medium(inputs.Substrate, micrometers, catalog, context, "substrate"),
                IsMirror = inputs.Mirror,
                // OpticStudio reverses the layers on a surface going from glass to air (its help,
                // "Specifying Coatings on Surfaces").
                ReverseLayers = !inputs.Mirror && !incidentIsAir && substrateIsAir,
                Micrometers = micrometers, PrimaryMicrometers = primary, AngleDegrees = angle,
                X = inputs.TaperX, Y = inputs.TaperY,
                SurfaceRadius = inputs.SurfaceRadius == 0 ? double.PositiveInfinity : inputs.SurfaceRadius,
                All = adjust
            };
            responses[i] = CoatingEvaluator.Evaluate(catalog, coating, conditions);
        }

        const double deg = 180 / Math.PI;
        IReadOnlyList<AnalysisCurve> curves = quantity switch
        {
            CoatingQuantity.Reflection => Three(x, responses, r => r.S.R, r => r.P.R),
            CoatingQuantity.Transmission => Three(x, responses, r => r.S.T, r => r.P.T),
            CoatingQuantity.Absorption => Three(x, responses, r => r.S.A, r => r.P.A),
            CoatingQuantity.Diattenuation => new[]
            {
                Curve("Reflection", x, responses, r => r.ReflectionDiattenuation),
                Curve("Transmission", x, responses, r => r.TransmissionDiattenuation, dashed: true),
            },
            CoatingQuantity.Phase => new[]
            {
                Curve("s reflection", x, responses, r => r.S.ReflectionPhase * deg),
                Curve("p reflection", x, responses, r => r.P.ReflectionPhase * deg),
                Curve("s transmission", x, responses, r => r.S.TransmissionPhase * deg, dashed: true),
                Curve("p transmission", x, responses, r => r.P.TransmissionPhase * deg, dashed: true),
            },
            CoatingQuantity.Retardance => new[]
            {
                Curve("Reflection", x, responses, r => r.ReflectionRetardance * deg),
                Curve("Transmission", x, responses, r => r.TransmissionRetardance * deg, dashed: true),
            },
            _ => throw new NotSupportedException(quantity.ToString())
        };
        string yLabel = quantity switch
        {
            CoatingQuantity.Phase or CoatingQuantity.Retardance => $"{quantity} (°)",
            _ => quantity.ToString()
        };
        var plot = new LinePlot(title, xLabel, yLabel, curves);

        var table = new List<IReadOnlyList<string>>();
        int step = Math.Max(1, (inputs.Points - 1) / 20);
        for (int i = 0; i < inputs.Points; i += step)
            table.Add(new[] { Num(x[i]) }.Concat(curves.Select(c => Num(c.Y[i]))).ToArray());
        if ((inputs.Points - 1) % step != 0)
            table.Add(new[] { Num(x[^1]) }.Concat(curves.Select(c => Num(c.Y[^1]))).ToArray());

        var pairs = new List<(string, string)>
        {
            ("Coating", coating?.Name ?? "none (uncoated)"),
            ("Incident medium", incidentIsAir ? "air (index 1)" : inputs.IncidentMedium.Trim()),
            ("Substrate", inputs.Mirror ? "mirror" : substrateIsAir ? "air (index 1)" : inputs.Substrate.Trim()),
            ("Primary wavelength", $"{Num(primary)} µm"),
        };
        pairs.AddRange(axisPairs);
        if (inputs.Multiplier != 1 || inputs.IndexOffset != 0 || inputs.ExtinctionOffset != 0)
            pairs.Add(("Adjustments", $"multiplier {Num(inputs.Multiplier)}, index offset {Num(inputs.IndexOffset)}, extinction offset {Num(inputs.ExtinctionOffset)}"));
        var sections = new List<ReportSection>
        {
            new("Coating") { Pairs = pairs },
            new(title) { TableHeaders = new[] { xLabel }.Concat(curves.Select(c => c.Name)).ToArray(), TableRows = table },
        };
        if (notes.Count > 0) sections.Add(new ReportSection("Catalog notes") { Lines = notes });
        var assumptions = new List<string>
        {
            "Each layer is a homogeneous, isotropic planar film; the light is coherent in the stack and the substrate is semi-infinite (no back-surface reflection).",
            "Indices are the coating materials' and the glass catalogs' own values at the wavelength (in the catalogs' air); they are not adjusted to the system's temperature and pressure.",
            "Phases follow OpticStudio's convention (index n − ik); reflection retardance is φp − φs + 180°, transmission retardance φp − φs; diattenuation |s − p| / (s + p). docs/Optics-Conventions.md §12.",
            "Relative layer thicknesses are waves of the primary wavelength in the layer: d = λ0 T / n0.",
            "Coatings are not yet applied to traced rays; this plot evaluates the coating alone.",
        };
        if (inputs.Mirror) assumptions.Add("On a mirror everything not reflected counts as absorbed (T = 0).");
        if (!inputs.Mirror && !incidentIsAir && substrateIsAir) assumptions.Add("Glass to air: the layers are applied in reverse order, as OpticStudio does.");
        return OpticsAnalysisResult.FromSections(title, sections, assumptions, new[] { plot });
    }

    private static AnalysisCurve[] Three(double[] x, CoatingResponse[] r, Func<CoatingResponse, double> s, Func<CoatingResponse, double> p) => new[]
    {
        Curve("s", x, r, s),
        Curve("p", x, r, p),
        Curve("Average", x, r, v => (s(v) + p(v)) / 2, dashed: true),
    };

    private static AnalysisCurve Curve(string name, double[] x, CoatingResponse[] r, Func<CoatingResponse, double> f, bool dashed = false) =>
        new(name, x, r.Select(f).ToArray(), dashed);

    /// <summary>A medium by its text: blank is air (1); a number is a real index; otherwise a
    /// coating material, then a glass of the active catalogs, by name.</summary>
    internal static Complex Medium(string text, double micrometers, CoatingCatalog catalog, OpticsAnalysisContext context, string role)
    {
        string t = text.Trim();
        if (t.Length == 0) return Complex.One;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
        {
            if (!(n > 0) || !double.IsFinite(n)) throw new InvalidOperationException($"The {role}'s index must be positive (it is {t}).");
            return n;
        }
        if (catalog.FindMaterial(t) is { } material) return material.Index(micrometers);
        if (context.FindGlass(t) is { } glass)
            return new Complex(glass.RelativeIndex(micrometers), glass.ExtinctionCoefficient(micrometers) ?? 0);
        throw new InvalidOperationException(
            $"The {role} '{t}' is neither a number, a coating material nor a glass in the active catalogs.");
    }

    internal static string Num(double v) => StudyReport.Number(v);
}

/// <summary>A coating quantity against angle of incidence (OP-15).</summary>
public sealed class CoatingVsAngleAnalysis : OpticsAnalysis<CoatingVsAngleSettings>
{
    public CoatingVsAngleAnalysis(CoatingQuantity quantity) => Quantity = quantity;

    public CoatingQuantity Quantity { get; }
    public override string Id => $"Coating{CoatingAnalyses.QuantityName(Quantity)}VsAngle";
    public override string Name => $"{Quantity} vs angle";
    public override string Category => CoatingAnalyses.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, CoatingVsAngleSettings s, CancellationToken cancellation)
    {
        if (!(s.MinimumAngleDegrees >= 0) || !(s.MaximumAngleDegrees < 90))
            throw new InvalidOperationException($"The angles must lie in [0°, 90°) (they are {CoatingAnalyses.Num(s.MinimumAngleDegrees)}° to {CoatingAnalyses.Num(s.MaximumAngleDegrees)}°).");
        double wavelength = s.WavelengthMicrometers;
        if (wavelength == 0)
            wavelength = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Wavelengths.Primary.Micrometers;
        if (!(wavelength > 0)) throw new InvalidOperationException($"The wavelength must be positive (it is {CoatingAnalyses.Num(wavelength)} µm).");
        var inputs = new CoatingAnalyses.Inputs(s.Coating, s.IncidentMedium, s.Substrate, s.Mirror, s.Points,
            s.ThicknessMultiplier, s.IndexOffset, s.ExtinctionOffset, s.TaperX, s.TaperY, s.SurfaceRadius);
        return CoatingAnalyses.Compute(Name, Quantity, context, inputs, "Angle of incidence (°)",
            s.MinimumAngleDegrees, s.MaximumAngleDegrees, a => (wavelength, a),
            new[] { ("Wavelength", $"{CoatingAnalyses.Num(wavelength)} µm") }, cancellation);
    }
}

/// <summary>A coating quantity against wavelength (OP-15).</summary>
public sealed class CoatingVsWavelengthAnalysis : OpticsAnalysis<CoatingVsWavelengthSettings>
{
    public CoatingVsWavelengthAnalysis(CoatingQuantity quantity) => Quantity = quantity;

    public CoatingQuantity Quantity { get; }
    public override string Id => $"Coating{CoatingAnalyses.QuantityName(Quantity)}VsWavelength";
    public override string Name => $"{Quantity} vs wavelength";
    public override string Category => CoatingAnalyses.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, CoatingVsWavelengthSettings s, CancellationToken cancellation)
    {
        if (!(s.AngleDegrees >= 0 && s.AngleDegrees < 90))
            throw new InvalidOperationException($"The angle must lie in [0°, 90°) (it is {CoatingAnalyses.Num(s.AngleDegrees)}°).");
        var wavelengths = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Wavelengths.Wavelengths;
        double min = s.MinimumMicrometers == 0 ? wavelengths.Min(w => w.Micrometers) : s.MinimumMicrometers;
        double max = s.MaximumMicrometers == 0 ? wavelengths.Max(w => w.Micrometers) : s.MaximumMicrometers;
        if (!(min > 0)) throw new InvalidOperationException($"The wavelengths must be positive (the minimum is {CoatingAnalyses.Num(min)} µm).");
        if (!(max > min))
            throw new InvalidOperationException(
                $"The wavelength range {CoatingAnalyses.Num(min)}–{CoatingAnalyses.Num(max)} µm is empty. With 0 for both ends the range is the system's wavelengths; a system with one wavelength needs the ends set.");
        var inputs = new CoatingAnalyses.Inputs(s.Coating, s.IncidentMedium, s.Substrate, s.Mirror, s.Points,
            s.ThicknessMultiplier, s.IndexOffset, s.ExtinctionOffset, s.TaperX, s.TaperY, s.SurfaceRadius);
        return CoatingAnalyses.Compute(Name, Quantity, context, inputs, "Wavelength (µm)", min, max, w => (w, s.AngleDegrees),
            new[] { ("Angle of incidence", $"{CoatingAnalyses.Num(s.AngleDegrees)}°") }, cancellation);
    }
}
