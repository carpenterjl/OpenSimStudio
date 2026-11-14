using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Analysis;

// The material analyses of plan item OP-14: glass maps, dispersion and transmittance curves,
// glass comparison, model glass and glass fitting. They read the active catalogs from the
// context (OpticsAnalysisContext.Catalogs), so any catalog the library loads, built in or
// imported, appears in them.

/// <summary>What the glass map's vertical axis shows.</summary>
public enum GlassMapAxis
{
    /// <summary>nd against Vd: the glass map.</summary>
    RefractiveIndex,
    /// <summary>P_g,F against Vd: the partial dispersion diagram.</summary>
    PartialDispersion
}

public sealed record GlassMapSettings
{
    [DisplayName("Vertical axis")]
    public GlassMapAxis Axis { get; init; } = GlassMapAxis.RefractiveIndex;

    /// <summary>Only glasses whose name contains this text; empty for all.</summary>
    [DisplayName("Name contains")]
    public string NameFilter { get; init; } = "";

    [DisplayName("List every glass")]
    public bool ListGlasses { get; init; } = true;
}

/// <summary>The nd–Vd glass map (or the P_g,F–Vd diagram) of the active catalogs.</summary>
public sealed class GlassMapAnalysis : OpticsAnalysis<GlassMapSettings>
{
    public const string SavedId = "GlassMap";
    public override string Id => SavedId;
    public override string Name => "Glass map";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GlassMapSettings settings, CancellationToken cancellation)
    {
        var catalogs = MaterialAnalysis.RequireCatalogs(context);
        var (all, leftOut) = GlassMaps.GlassMap(catalogs, settings.NameFilter, cancellation);
        bool pgf = settings.Axis == GlassMapAxis.PartialDispersion;
        var left = leftOut.ToList();
        var points = all.ToList();
        if (pgf)
        {
            foreach (var p in all.Where(p => p.Properties.PgF is null))
                left.Add(new GlassLeftOut(p.Catalog, p.Material, $"{p.Material.Who}: its valid range does not reach the g line, so P_g,F is unknown."));
            points = all.Where(p => p.Properties.PgF is not null).ToList();
        }
        var series = points.GroupBy(p => p.Catalog, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AnalysisScatterSeries(g.Key, g.Select(p => new AnalysisPoint(p.Properties.Vd,
                pgf ? p.Properties.PgF!.Value : p.Properties.Nd, p.Material.Name)).ToArray()))
            .ToArray();
        var plot = new AnalysisScatterPlot(pgf ? "Partial dispersion P_g,F against Vd" : "Glass map: nd against Vd",
            "Vd", pgf ? "P_g,F" : "nd", series);

        var sections = new List<ReportSection>
        {
            new("Glass map")
            {
                Pairs = new[]
                {
                    ("Catalogs", string.Join(", ", catalogs.Select(c => c.Name))),
                    ("Glasses placed", points.Count.ToString(CultureInfo.InvariantCulture)),
                    ("Glasses left out", left.Count.ToString(CultureInfo.InvariantCulture)),
                }
            }
        };
        if (settings.ListGlasses && points.Count > 0)
            sections.Add(new ReportSection("Glasses")
            {
                TableHeaders = new[] { "Catalog", "Glass", "nd", "Vd", "PgF", "ΔPgF" },
                TableRows = points.Select(p => (IReadOnlyList<string>)new[]
                {
                    p.Catalog, p.Material.Name, MaterialAnalysis.F(p.Properties.Nd, "F6"), MaterialAnalysis.F(p.Properties.Vd, "F3"),
                    MaterialAnalysis.F(p.Properties.PgF, "F4"), MaterialAnalysis.F(p.Properties.DeltaPgF, "F4")
                }).ToArray()
            });
        MaterialAnalysis.AddLeftOut(sections, left);
        var assumptions = new[]
        {
            "Each glass is placed at the nd, Vd and P_g,F computed from its own dispersion coefficients at the catalog's reference conditions, not at the values its NM line prints.",
            "ΔP_g,F is measured from the normal line P_g,F = 0.6438 − 0.001682·Vd (ZemaxGlass user manual, after SCHOTT TIE-29).",
            "Vd increases to the right; the axes are not reversed as printed glass maps usually are.",
        };
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>What the athermal map's vertical axis shows.</summary>
public enum AthermalMapAxis
{
    /// <summary>G = α·(n − 1) + dn/dT, SCHOTT TIE-19 equation (10).</summary>
    ThermoOpticConstant,
    /// <summary>dn_rel/dT alone.</summary>
    RelativeDnDT
}

public sealed record AthermalGlassMapSettings
{
    /// <summary>The wavelength in air, µm; TIE-19's Table 2 uses the e line.</summary>
    [DisplayName("Wavelength (µm)")]
    public double WavelengthMicrometers { get; init; } = SpectralLines.E.Micrometers;

    /// <summary>The temperature interval of the mean dn/dT; TIE-19's Table 2 uses +20/+40 °C.</summary>
    [DisplayName("From (°C)")]
    public double LowerCelsius { get; init; } = 20;

    [DisplayName("To (°C)")]
    public double UpperCelsius { get; init; } = 40;

    /// <summary>The air the relative index refers to; TIE-19's own air formula by default,
    /// since TIE-19 defines the quantities plotted.</summary>
    [DisplayName("Air index model")]
    public AirIndexModel AirModel { get; init; } = AirIndexModel.Schott;

    [DisplayName("Pressure (atm)")]
    public double PressureAtmospheres { get; init; } = 1;

    [DisplayName("Vertical axis")]
    public AthermalMapAxis Axis { get; init; } = AthermalMapAxis.ThermoOpticConstant;

    [DisplayName("Name contains")]
    public string NameFilter { get; init; } = "";

    [DisplayName("List every glass")]
    public bool ListGlasses { get; init; } = true;
}

/// <summary>The athermal glass map: thermal expansion α against G (or dn_rel/dT).</summary>
public sealed class AthermalGlassMapAnalysis : OpticsAnalysis<AthermalGlassMapSettings>
{
    public const string SavedId = "AthermalGlassMap";
    public override string Id => SavedId;
    public override string Name => "Athermal glass map";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, AthermalGlassMapSettings settings, CancellationToken cancellation)
    {
        var catalogs = MaterialAnalysis.RequireCatalogs(context);
        if (!(settings.PressureAtmospheres >= 0)) throw new ArgumentException("The pressure must be zero or more atmospheres.");
        var (points, left) = GlassMaps.AthermalMap(catalogs, settings.WavelengthMicrometers, settings.LowerCelsius, settings.UpperCelsius,
            settings.AirModel, settings.PressureAtmospheres * AirIndex.StandardPressurePascals, settings.NameFilter, cancellation);
        bool g = settings.Axis == AthermalMapAxis.ThermoOpticConstant;
        var series = points.GroupBy(p => p.Catalog, StringComparer.OrdinalIgnoreCase)
            .Select(s => new AnalysisScatterSeries(s.Key, s.Select(p => new AnalysisPoint(p.ExpansionPerKelvin * 1e6,
                (g ? p.ThermoOpticConstant : p.RelativeDnDT) * 1e6, p.Material.Name)).ToArray()))
            .ToArray();
        string yLabel = g ? "G (10⁻⁶/K)" : "dn_rel/dT (10⁻⁶/K)";
        var plot = new AnalysisScatterPlot($"Athermal glass map at {MaterialAnalysis.F(settings.WavelengthMicrometers, "G7")} µm",
            "α, −30…+70 °C (10⁻⁶/K)", yLabel, series);
        var sections = new List<ReportSection>
        {
            new("Athermal glass map")
            {
                Pairs = new[]
                {
                    ("Catalogs", string.Join(", ", catalogs.Select(c => c.Name))),
                    ("Wavelength (in air)", $"{MaterialAnalysis.F(settings.WavelengthMicrometers, "G7")} µm"),
                    ("Temperature interval", $"{MaterialAnalysis.F(settings.LowerCelsius)} to {MaterialAnalysis.F(settings.UpperCelsius)} °C"),
                    ("Glasses placed", points.Count.ToString(CultureInfo.InvariantCulture)),
                    ("Glasses left out", left.Count.ToString(CultureInfo.InvariantCulture)),
                }
            }
        };
        if (settings.ListGlasses && points.Count > 0)
            sections.Add(new ReportSection("Glasses")
            {
                TableHeaders = new[] { "Catalog", "Glass", "n_rel", "α (10⁻⁶/K)", "dn_rel/dT (10⁻⁶/K)", "G (10⁻⁶/K)" },
                TableRows = points.Select(p => (IReadOnlyList<string>)new[]
                {
                    p.Catalog, p.Material.Name, MaterialAnalysis.F(p.RelativeIndex, "F6"), MaterialAnalysis.F(p.ExpansionPerKelvin * 1e6, "F2"),
                    MaterialAnalysis.F(p.RelativeDnDT * 1e6, "F3"), MaterialAnalysis.F(p.ThermoOpticConstant * 1e6, "F3")
                }).ToArray()
            });
        MaterialAnalysis.AddLeftOut(sections, left);
        var air = new AirConditions(settings.LowerCelsius, settings.PressureAtmospheres * AirIndex.StandardPressurePascals);
        var assumptions = new[]
        {
            "G = α·(n_rel − 1) + dn_rel/dT is SCHOTT TIE-19 equation (10); a glass with G near zero is athermal in TIE-19's sense.",
            "dn_rel/dT is the mean over the interval, (n_rel(T₂) − n_rel(T₁))/(T₂ − T₁), from the TIE-19 dn/dT model (equations (2)–(5)) and the glass's TD constants, as TIE-19's Table 2 quotes it; n_rel is taken at the lower temperature.",
            "α is the catalog's ED-line expansion coefficient for −30…+70 °C, used as printed (10⁻⁶/K); glasses without TD constants or an expansion coefficient are left out.",
            AirIndexModels.Describe(settings.AirModel, air),
        };
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}

public sealed record DispersionCurveSettings
{
    /// <summary>Glass names separated by commas; "catalog:glass" picks a catalog.</summary>
    [DisplayName("Glasses")]
    public string Glasses { get; init; } = "";

    [DisplayName("Shortest wavelength (µm)")]
    public double MinMicrometers { get; init; } = 0.35;

    [DisplayName("Longest wavelength (µm)")]
    public double MaxMicrometers { get; init; } = 1.0;

    [DisplayName("Points")]
    public int Points { get; init; } = 200;
}

/// <summary>Index against wavelength for the named glasses (OpticStudio's Dispersion vs Wavelength).</summary>
public sealed class DispersionCurveAnalysis : OpticsAnalysis<DispersionCurveSettings>
{
    public const string SavedId = "DispersionVsWavelength";
    public override string Id => SavedId;
    public override string Name => "Dispersion vs wavelength";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, DispersionCurveSettings settings, CancellationToken cancellation)
    {
        var glasses = MaterialAnalysis.RequireGlasses(context, settings.Glasses, out var notes);
        if (!(settings.MinMicrometers > 0) || !(settings.MaxMicrometers > settings.MinMicrometers))
            throw new ArgumentException("The wavelength range needs a positive shortest wavelength below the longest.");
        if (settings.Points < 2) throw new ArgumentException("A curve needs at least two points.");
        var curves = new List<AnalysisCurve>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var glass in glasses)
        {
            cancellation.ThrowIfCancellationRequested();
            if (glass.ValidRange is not { } range || range.Max < settings.MinMicrometers || range.Min > settings.MaxMicrometers)
            {
                notes.Add($"{glass.Who} has no valid range inside {MaterialAnalysis.F(settings.MinMicrometers)}–{MaterialAnalysis.F(settings.MaxMicrometers)} µm; not drawn.");
                continue;
            }
            double lo = Math.Max(range.Min, settings.MinMicrometers), hi = Math.Min(range.Max, settings.MaxMicrometers);
            var x = new double[settings.Points];
            var y = new double[settings.Points];
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = i == x.Length - 1 ? hi : lo + (hi - lo) * i / (x.Length - 1);
                y[i] = glass.RelativeIndex(x[i]);
            }
            curves.Add(new AnalysisCurve(glass.Name, x, y));
            rows.Add(new[] { glass.Name, glass.Catalog, $"{MaterialAnalysis.F(lo)}–{MaterialAnalysis.F(hi)}", MaterialAnalysis.F(y[0], "F6"), MaterialAnalysis.F(y[^1], "F6") });
            if (lo > settings.MinMicrometers || hi < settings.MaxMicrometers)
                notes.Add($"{glass.Who} is drawn over its valid range {MaterialAnalysis.F(lo)}–{MaterialAnalysis.F(hi)} µm only; indices are not extrapolated.");
        }
        var plot = new LinePlot("Index against wavelength", "Wavelength (µm)", "n", curves);
        var sections = new List<ReportSection>
        {
            new("Curves") { TableHeaders = new[] { "Glass", "Catalog", "Drawn over (µm)", "n at start", "n at end" }, TableRows = rows },
        };
        if (notes.Count > 0) sections.Add(new ReportSection("Notes") { Lines = notes });
        return OpticsAnalysisResult.FromSections(Name, sections,
            new[] { "Indices are catalog values relative to air at each catalog's reference temperature and pressure, at wavelengths in that air; the environment is not applied." },
            new AnalysisPlot[] { plot });
    }
}

public sealed record TransmittanceCurveSettings
{
    [DisplayName("Glasses")]
    public string Glasses { get; init; } = "";

    [DisplayName("Thickness (mm)")]
    public double ThicknessMillimeters { get; init; } = 10;
}

/// <summary>Internal transmittance against wavelength at a chosen thickness, from the IT data.</summary>
public sealed class TransmittanceCurveAnalysis : OpticsAnalysis<TransmittanceCurveSettings>
{
    public const string SavedId = "InternalTransmittanceVsWavelength";
    public override string Id => SavedId;
    public override string Name => "Internal transmittance vs wavelength";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, TransmittanceCurveSettings settings, CancellationToken cancellation)
    {
        var glasses = MaterialAnalysis.RequireGlasses(context, settings.Glasses, out var notes);
        var curves = new List<AnalysisCurve>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var glass in glasses)
        {
            cancellation.ThrowIfCancellationRequested();
            var (points, skipped) = InternalTransmittance.Curve(glass, settings.ThicknessMillimeters);
            if (skipped > 0) notes.Add($"{glass.Who}: {skipped} IT point(s) without a sample thickness or with a transmittance outside 0–1 were left out.");
            if (points.Count == 0)
            {
                notes.Add($"{glass.Who} has no usable internal transmittance data (IT lines); not drawn.");
                continue;
            }
            curves.Add(new AnalysisCurve(glass.Name, points.Select(p => p.Micrometers).ToArray(), points.Select(p => p.Transmittance).ToArray()));
            rows.Add(new[] { glass.Name, glass.Catalog, points.Count.ToString(CultureInfo.InvariantCulture),
                $"{MaterialAnalysis.F(points[0].Micrometers)}–{MaterialAnalysis.F(points[^1].Micrometers)}" });
        }
        var plot = new LinePlot($"Internal transmittance, {MaterialAnalysis.F(settings.ThicknessMillimeters)} mm", "Wavelength (µm)", "Internal transmittance", curves);
        var sections = new List<ReportSection>
        {
            new("Curves") { TableHeaders = new[] { "Glass", "Catalog", "Points", "Wavelengths (µm)" }, TableRows = rows },
        };
        if (notes.Count > 0) sections.Add(new ReportSection("Notes") { Lines = notes });
        return OpticsAnalysisResult.FromSections(Name, sections,
            new[]
            {
                "Each IT point (transmittance t of a sample τ mm thick) is scaled to the chosen thickness d as t^(d/τ): absorption −ln(t)/τ per mm, as AGFFileReader.jl defines it.",
                "Points are drawn at the catalog's own wavelengths, joined by straight lines; nothing is extrapolated. Fresnel reflection is not included (internal transmittance).",
            },
            new AnalysisPlot[] { plot });
    }
}

public sealed record GlassCompareSettings
{
    [DisplayName("Glasses")]
    public string Glasses { get; init; } = "";

    /// <summary>Wavelengths (µm) separated by commas; empty for the system's wavelengths.</summary>
    [DisplayName("Wavelengths (µm)")]
    public string Wavelengths { get; init; } = "";
}

/// <summary>Named glasses side by side (OpticStudio's Glass Compare).</summary>
public sealed class GlassCompareAnalysis : OpticsAnalysis<GlassCompareSettings>
{
    public const string SavedId = "GlassCompare";
    public override string Id => SavedId;
    public override string Name => "Glass compare";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GlassCompareSettings settings, CancellationToken cancellation)
    {
        var glasses = MaterialAnalysis.RequireGlasses(context, settings.Glasses, out var notes);
        IReadOnlyList<double> wavelengths;
        if (string.IsNullOrWhiteSpace(settings.Wavelengths))
            wavelengths = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Wavelengths.Wavelengths.Select(w => w.Micrometers).ToArray();
        else
            wavelengths = settings.Wavelengths.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0
                    ? v : throw new FormatException($"'{s}' in the wavelength list is not a positive number of micrometers."))
                .ToArray();
        var table = GlassComparison.Build(glasses, wavelengths);
        var sections = new List<ReportSection> { new("Comparison") { TableHeaders = table.Headers, TableRows = table.Rows } };
        if (notes.Count > 0) sections.Add(new ReportSection("Notes") { Lines = notes });
        return OpticsAnalysisResult.FromSections(Name, sections, new[]
        {
            "Computed values come from each glass's dispersion coefficients at its catalog's reference conditions; printed values are as the catalog prints them.",
            "ΔP_g,F (computed) is measured from the normal line P_g,F = 0.6438 − 0.001682·Vd (ZemaxGlass user manual, after SCHOTT TIE-29).",
        });
    }
}

public sealed record ModelGlassSettings
{
    [DisplayName("Name")]
    public string GlassName { get; init; } = "MODEL";

    [DisplayName("nd")]
    public double Nd { get; init; } = 1.5;

    [DisplayName("Vd")]
    public double Vd { get; init; } = 60;

    [DisplayName("ΔPgF")]
    public double DeltaPgF { get; init; }
}

/// <summary>A model glass from nd, Vd and ΔP_g,F, trained on the active catalogs (<see cref="ModelGlassFit"/>).</summary>
public sealed class ModelGlassAnalysis : OpticsAnalysis<ModelGlassSettings>
{
    public const string SavedId = "ModelGlass";
    public override string Id => SavedId;
    public override string Name => "Model glass";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ModelGlassSettings settings, CancellationToken cancellation)
    {
        var catalogs = MaterialAnalysis.RequireCatalogs(context);
        var fit = ModelGlassFit.Train(catalogs.SelectMany(c => c.Materials));
        cancellation.ThrowIfCancellationRequested();
        var glass = fit.Create(settings.Nd, settings.Vd, settings.DeltaPgF, settings.GlassName);
        var p = GlassProperties.Compute(glass);
        var lines = SpectralLines.All.Where(l => glass.Covers(l.Micrometers)).OrderBy(l => l.Micrometers).ToArray();
        var x = Enumerable.Range(0, 200).Select(i => fit.MinMicrometers + (fit.MaxMicrometers - fit.MinMicrometers) * i / 199).ToArray();
        x[^1] = fit.MaxMicrometers;
        var plot = new LinePlot($"Model glass {glass.Name}", "Wavelength (µm)", "n",
            new[] { new AnalysisCurve(glass.Name, x, x.Select(glass.RelativeIndex).ToArray()) });
        var sections = new List<ReportSection>
        {
            new("Model glass")
            {
                Pairs = new[]
                {
                    ("Requested nd, Vd, ΔPgF", $"{MaterialAnalysis.F(settings.Nd, "G9")}, {MaterialAnalysis.F(settings.Vd, "G9")}, {MaterialAnalysis.F(settings.DeltaPgF, "G9")}"),
                    ("Computed nd", MaterialAnalysis.F(p.Nd, "F8")),
                    ("Computed Vd", MaterialAnalysis.F(p.Vd, "F6")),
                    ("Computed PgF", MaterialAnalysis.F(p.PgF, "F6")),
                    ("Computed ΔPgF", MaterialAnalysis.F(p.DeltaPgF, "F6")),
                    ("Formula", $"{(int)DispersionFormula.Herzberger} (Herzberger)"),
                    ("Coefficients C1…C6", string.Join(" ", glass.Coefficients.Select(c => MaterialAnalysis.F(c, "E10")))),
                    ("Valid range", $"{MaterialAnalysis.F(fit.MinMicrometers, "G7")}–{MaterialAnalysis.F(fit.MaxMicrometers, "G7")} µm"),
                }
            },
            new("Training")
            {
                Pairs = new[]
                {
                    ("Glasses used", fit.TrainingCount.ToString(CultureInfo.InvariantCulture)),
                    ("Catalogs", string.Join(", ", fit.TrainingCatalogs)),
                    ("Glasses left out", fit.LeftOut.Count.ToString(CultureInfo.InvariantCulture)),
                    ("RMS index residual over the training glasses", MaterialAnalysis.F(fit.RmsIndexResidual, "E2")),
                    ("Largest index residual", $"{MaterialAnalysis.F(fit.MaxIndexResidual, "E2")} ({fit.WorstGlass})"),
                }
            },
            new("Index at the spectral lines")
            {
                TableHeaders = new[] { "Line", "λ (µm)", "n" },
                TableRows = lines.Select(l => (IReadOnlyList<string>)new[] { l.Designation.Length > 0 ? l.Designation : l.Source, MaterialAnalysis.F(l.Micrometers, "G7"), MaterialAnalysis.F(glass.RelativeIndex(l.Micrometers), "F6") }).ToArray()
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, new[]
        {
            "This model glass is OpenSim's stand-in, not OpticStudio's model (whose formula is not published): a fit of the relative partial dispersion against P_g,F over the active catalogs' glasses, constrained so the requested nd, Vd and P_g,F come out exactly. Away from the d line its indices differ from OpticStudio's.",
            "The residuals say how well a model glass with a training glass's own nd, Vd and P_g,F reproduces that glass over the valid range; a different set of active catalogs gives a different model.",
        }, new AnalysisPlot[] { plot });
    }
}

public sealed record GlassFitSettings
{
    /// <summary>A text file of "wavelength(µm) index" pairs, one per line.</summary>
    [DisplayName("Measured data file")]
    public string DataFile { get; init; } = "";

    [DisplayName("Formula")]
    public DispersionFormula Formula { get; init; } = DispersionFormula.Sellmeier1;

    [DisplayName("Name")]
    public string GlassName { get; init; } = "FITTED";
}

/// <summary>Fits a dispersion formula to measured indices (OpticStudio's Glass Fitting).</summary>
public sealed class GlassFitAnalysis : OpticsAnalysis<GlassFitSettings>
{
    public const string SavedId = "GlassFit";
    public override string Id => SavedId;
    public override string Name => "Glass fitting";
    public override string Category => MaterialAnalysis.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GlassFitSettings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.DataFile))
            throw new InvalidOperationException("Name a measured data file in the settings: one 'wavelength(µm) index' pair per line.");
        if (!File.Exists(settings.DataFile))
            throw new FileNotFoundException($"The measured data file '{settings.DataFile}' does not exist.", settings.DataFile);
        var samples = DispersionFit.ReadSamples(File.ReadAllText(settings.DataFile));
        var fit = DispersionFit.Fit(samples, settings.Formula, cancellation);
        var coefficients = fit.Coefficients.Select((c, j) => (IReadOnlyList<string>)new[]
        {
            $"C{j + 1}", MaterialAnalysis.F(c, "E10"), MaterialAnalysis.F(fit.Sensitivity[j] * fit.RmsResidual, "E2")
        }).ToArray();
        var plot = new LinePlot("Fit residual", "Wavelength (µm)", "Fitted − measured index",
            new[] { new AnalysisCurve("Residual", fit.Samples.Select(s => s.Micrometers).ToArray(), fit.Residuals.ToArray()) });
        var sections = new List<ReportSection>
        {
            new("Fit")
            {
                Pairs = new[]
                {
                    ("Data", $"{fit.Samples.Count} indices, {MaterialAnalysis.F(fit.MinMicrometers, "G6")}–{MaterialAnalysis.F(fit.MaxMicrometers, "G6")} µm"),
                    ("Formula", DispersionFormulas.NameOf((int)fit.Formula)),
                    ("RMS residual", MaterialAnalysis.F(fit.RmsResidual, "E2")),
                    ("Largest residual", MaterialAnalysis.F(fit.MaxResidual, "E2")),
                    ("Condition number (scaled)", MaterialAnalysis.F(fit.ConditionNumber, "E2")),
                    ("Stopped", fit.Status?.ToString() ?? "linear solve"),
                }
            },
            new("Coefficients") { TableHeaders = new[] { "Coefficient", "Value", "Standard error (RMS × sensitivity)" }, TableRows = coefficients },
            new("AGF lines") { Lines = fit.ToAgf(settings.GlassName).TrimEnd().Split("\r\n") },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, new[]
        {
            "The fit minimises the sum of squared index residuals; every sample has equal weight.",
            "Standard errors assume independent residuals of equal size; a condition number above about 1e10 means some coefficients are not determined by the data.",
            "The fitted glass is valid only over the measured wavelength range.",
        }, new AnalysisPlot[] { plot });
    }
}

/// <summary>Shared pieces of the material analyses.</summary>
internal static class MaterialAnalysis
{
    public const string Category = "Materials";

    public static IReadOnlyList<OpticalCatalog> RequireCatalogs(OpticsAnalysisContext context) =>
        context.Catalogs.Count > 0 ? context.Catalogs
            : throw new InvalidOperationException("No glass catalog is active: import an AGF catalog or activate one in the Materials panel.");

    public static IReadOnlyList<OpticalMaterial> RequireGlasses(OpticsAnalysisContext context, string names, out List<string> notes)
    {
        var catalogs = RequireCatalogs(context);
        if (string.IsNullOrWhiteSpace(names))
            throw new InvalidOperationException("Name one or more glasses in the settings, separated by commas (\"catalog:glass\" picks a catalog).");
        var (found, missing) = GlassLookup.Find(names, catalogs);
        notes = missing.Select(m => $"Glass '{m}' is not in the active catalogs ({string.Join(", ", catalogs.Select(c => c.Name))}).").ToList();
        if (found.Count == 0) throw new InvalidOperationException(string.Join(" ", notes));
        return found;
    }

    public static void AddLeftOut(List<ReportSection> sections, IReadOnlyList<GlassLeftOut> left)
    {
        if (left.Count == 0) return;
        sections.Add(new ReportSection("Left out")
        {
            TableHeaders = new[] { "Catalog", "Glass", "Reason" },
            TableRows = left.Select(l => (IReadOnlyList<string>)new[] { l.Catalog, l.Material.Name, l.Reason }).ToArray()
        });
    }

    public static string F(double? v, string format = "G6") =>
        v is double x && double.IsFinite(x) ? x.ToString(format, CultureInfo.InvariantCulture) : "—";
}
