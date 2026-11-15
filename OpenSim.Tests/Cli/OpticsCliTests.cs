using System.Globalization;
using System.Text.Json;
using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Plotting;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Tolerancing;
using OpenSim.Tests.Optics;
using OpenSim.Tests.Optics.Gates;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cli;

/// <summary>
/// OP-97: the optics command family. The gate ("the CLI and the app produce byte-identical reports
/// for the same lens") is two legs over one library function: here the command's report file
/// equals <see cref="OpticsBatch.Report"/> of the same lens, settings and catalogs byte for byte;
/// OpenSim.App.Tests' OpticsReportParityTests shows the app's Save report is that same function's
/// output. The lenses are the OP-57 and OP-64 test fixtures (constant indices, no glass catalog),
/// arbitrary inputs, not designs from a source; every expected number is computed here through
/// the library the command calls, never typed.
/// </summary>
public sealed class OpticsCliTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public OpticsCliTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "opensim-optics-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (int Code, string Out, string Err) Run(params string[] args) => RunWith(CancellationToken.None, args);

    private (int Code, string Out, string Err) RunWith(CancellationToken token, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr, token);
        _out.WriteLine($"$ opensim {string.Join(' ', args)}  → {code}");
        _out.WriteLine(stdout.ToString());
        if (stderr.ToString().Length > 0) _out.WriteLine("stderr: " + stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The built-in catalogs in the order --builtin-catalogs loads them (the library's: by name).</summary>
    private static IReadOnlyList<OpticalCatalog> BuiltIn =>
        GlassCatalogLibrary.BuiltInCatalogs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private string Save(OpticsSettings optics, string name = "lens")
    {
        var project = new SimProject { Name = "Lens " + name, Optics = optics };
        string path = Path.Combine(_dir, name + ".ossproj");
        new ProjectSerializer().Save(project, path);
        return path;
    }

    /// <summary>The OP-64 singlet with a saved Standard spot setting (ray density 4) and the
    /// OP-57 marginal-ray merit function.</summary>
    private static OpticsSettings AnalysedSinglet() => ToleranceRunFixtures.Singlet() with
    {
        Analyses = new OpticsAnalysesSettings
        {
            LastSettings = new()
            {
                [StandardSpotAnalysis.SavedId] = OpticsAnalysisSettingsCodec.Save(new StandardSpotSettings { RayDensity = 4 }),
            },
        },
        MeritFunction = MeritFunctionSettings.ToSettings(LocalOptimizationFixtures.MarginalRayMerit(imageSurface: 3, 0.5, 1.0)),
    };

    /// <summary>The library's report for the lens as saved and reloaded: what the command must write.</summary>
    private static (string Markdown, string Html, IReadOnlyList<OpticsAnalysisReportEntry> Entries) LibraryReport(string projectPath,
        IReadOnlyList<IOpticsAnalysis> analyses, Func<IOpticsAnalysis, object, object>? adjust = null)
    {
        var project = new ProjectSerializer().Load(projectPath);
        var optics = project.Optics!;
        var runs = analyses.Select(a =>
        {
            var s = OpticsBatch.SavedSettings(a, optics, out _);
            return (a, adjust is null ? s : adjust(a, s));
        }).ToList();
        var context = OpticsBatch.Context(project.Name, optics, BuiltIn);
        var entries = OpticsBatch.Run(context, runs);
        var report = OpticsBatch.Report(context, entries);
        return (report.ToMarkdown(), report.ToHtml(), entries);
    }

    // ------------------------------------------------------------------ usage

    [Fact]
    public void Usage_ListsTheOpticsCommands_AndBadArgumentsAreUsageErrors()
    {
        var help = Run("help").Out;
        foreach (var sub in new[] { "optics list", "optics analyse", "optics trace", "optics optimise", "optics tolerance", "optics:surface<n>" })
            Assert.Contains(sub, help);
        Assert.Equal(1, Run("optics").Code);
        var (code, _, err) = Run("optics", "bogus", "x.ossproj");
        Assert.Equal(1, code);
        Assert.Contains("bogus", err);
        Assert.Equal(1, Run("optics", "analyse").Code);                                     // no project
        string path = Save(AnalysedSinglet());
        (code, _, err) = Run("optics", "analyse", path);                                    // no --analysis
        Assert.Equal(1, code);
        Assert.Contains("--analysis", err);
        (code, _, err) = Run("optics", "analyse", path, "--analysis", "NoSuchAnalysis");
        Assert.Equal(1, code);
        Assert.Contains("NoSuchAnalysis", err);
        (code, _, err) = Run("optics", "analyse", path, "--analysis", "StandardSpot", "--set", "Bogus=3");
        Assert.Equal(1, code);
        Assert.Contains("no setting 'Bogus'", err);
        Assert.Contains("RayDensity", err);                                                 // the settings it does have
        (code, _, err) = Run("optics", "analyse", path, "--analysis", "StandardSpot", "--set", "RayDensity=many");
        Assert.Equal(1, code);
        Assert.Contains("RayDensity", err);
        Assert.Contains("whole number", err);
    }

    [Fact]
    public void List_NamesEveryRegisteredAnalysis_AndOnesSettings()
    {
        var (code, output, _) = Run("optics", "list");
        Assert.Equal(0, code);
        foreach (var a in OpticsAnalysisRegistry.CreateDefault().All) Assert.Contains(a.Id, output);
        (code, output, _) = Run("optics", "list", "--analysis", "SingleRayTrace");
        Assert.Equal(0, code);
        foreach (var name in new[] { "Hx", "Hy", "Px", "Py", "Wavelength", "GlobalCoordinates" }) Assert.Contains(name, output);
    }

    // ------------------------------------------------------------------ the gate (CLI leg)

    /// <summary>OP-97's gate, the command's half: the report the command writes is, byte for byte,
    /// the library function the app's Save report calls, for the same saved lens and the settings
    /// the project saved; each analysis's text file is its Text tab.</summary>
    [Fact]
    public void Analyse_WritesTheLibrarysReport_ByteForByte()
    {
        string path = Save(AnalysedSinglet());
        string outDir = Path.Combine(_dir, "out");
        var (code, _, _) = Run("optics", "analyse", path, "--analysis", "SystemData,StandardSpot,SingleRayTrace", "--builtin-catalogs", "--out", outDir);
        Assert.Equal(0, code);
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var analyses = new[] { "SystemData", "StandardSpot", "SingleRayTrace" }.Select(id => { registry.TryGet(id, out var a); return a; }).ToList();
        var expected = LibraryReport(path, analyses);
        Assert.Equal(expected.Markdown, File.ReadAllText(Path.Combine(outDir, "report.md")));
        Assert.Equal(expected.Html, File.ReadAllText(Path.Combine(outDir, "report.html")));
        Assert.All(expected.Entries, e => Assert.NotNull(e.Result));
        Assert.Equal(expected.Entries[1].Result!.Text, File.ReadAllText(Path.Combine(outDir, "StandardSpot.txt")));
        // The saved ray density was used (the library's settings), and the plots' data are written.
        Assert.Contains("\"rayDensity\":4", File.ReadAllText(Path.Combine(outDir, "log.txt")));
        for (int k = 0; k < expected.Entries[1].Result!.Plots.Count; k++)
            Assert.Equal(AnalysisPlotCsv.Write(expected.Entries[1].Result!.Plots[k]), File.ReadAllText(Path.Combine(outDir, $"StandardSpot-plot{k + 1}.csv")));
    }

    /// <summary>--set gives the same bytes as saving those settings in the project.</summary>
    [Fact]
    public void Set_IsTheSameAsSavingTheSettings()
    {
        string plain = Save(AnalysedSinglet(), "plain");
        var savedOptics = AnalysedSinglet() with
        {
            Analyses = new OpticsAnalysesSettings
            {
                LastSettings = new() { [SingleRayTraceAnalysis.SavedId] = OpticsAnalysisSettingsCodec.Save(new SingleRayTraceSettings { Py = 0.7, Wavelength = 1 }) },
            },
        };
        string saved = Save(savedOptics, "saved");
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Assert.Equal(0, Run("optics", "analyse", plain, "--analysis", "SingleRayTrace", "--set", "py=0.7;Wavelength=1", "--builtin-catalogs", "--out", a).Code);
        Assert.Equal(0, Run("optics", "analyse", saved, "--analysis", "SingleRayTrace", "--builtin-catalogs", "--out", b).Code);
        // The project names differ ("Lens plain", "Lens saved"); everything after the inputs is the same.
        string Body(string dir) => File.ReadAllText(Path.Combine(dir, "SingleRayTrace.txt"));
        Assert.Equal(Body(b), Body(a));
        // trace is the same analysis with its own options.
        string c = Path.Combine(_dir, "c");
        var (code, output, _) = Run("optics", "trace", plain, "--py", "0.7", "--wavelength", "1", "--builtin-catalogs", "--out", c);
        Assert.Equal(0, code);
        Assert.Equal(Body(b), Body(c));
        Assert.Contains(Body(b), output);
    }

    [Fact]
    public void ACancelledRun_ExitsWithTwo()
    {
        string path = Save(AnalysedSinglet());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (code, _, err) = RunWith(cts.Token, "optics", "analyse", path, "--analysis", "StandardSpot", "--builtin-catalogs", "--out", Path.Combine(_dir, "x"));
        Assert.Equal(2, code);
        Assert.Contains("Cancelled", err);
    }

    // ------------------------------------------------------------------ optimise

    [Fact]
    public void Optimise_WritesTheOptimizersResult_AndLeavesTheInputAlone()
    {
        var optics = LocalOptimizationFixtures.Singlet(r1: 80);
        string path = Save(optics);
        string outDir = Path.Combine(_dir, "opt");
        var (code, _, _) = Run("optics", "optimise", path, "--builtin-catalogs", "--out", outDir);
        Assert.Equal(0, code);

        var loaded = new ProjectSerializer().Load(path).Optics!;
        var direct = LocalOptimizer.Run(loaded, GlassLookup.ActiveCatalogs(BuiltIn, loaded.Materials?.ActiveCatalogs));
        var written = new ProjectSerializer().Load(Path.Combine(outDir, "lens-optimised.ossproj")).Optics!;
        Assert.Equal(direct.Optimized.Surfaces![1].Radius, written.Surfaces![1].Radius);
        Assert.Equal(80, new ProjectSerializer().Load(path).Optics!.Surfaces![1].Radius);     // the input is unchanged
        Assert.Equal(string.Join(Environment.NewLine, direct.Log), File.ReadAllText(Path.Combine(outDir, "log.txt"))
            .Split(Environment.NewLine).SkipWhile(l => l.StartsWith("Glass catalogs:")).TakeWhile(l => !l.StartsWith("Optimised project")).Aggregate((x, y) => x + Environment.NewLine + y));
        Assert.Equal(CliRunner.OptimiseReport("Lens lens", direct, new LocalOptimizationOptions()).ToMarkdown(), File.ReadAllText(Path.Combine(outDir, "report.md")));
        var variables = PlotCsv.ReadTable(File.ReadAllText(Path.Combine(outDir, "variables.csv")));
        Assert.Equal(direct.Values[0], double.Parse(variables.Rows[0][2], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void GlobalOptimisationAndMonteCarlo_AreNotBuiltYet_AndSaySo()
    {
        string path = Save(LocalOptimizationFixtures.Singlet());
        var (code, _, err) = Run("optics", "optimise", path, "--global");
        Assert.Equal(1, code);
        Assert.Contains("not built yet", err);
        Assert.Contains("OP-58", err);
        (code, _, err) = Run("optics", "tolerance", path, "--montecarlo");
        Assert.Equal(1, code);
        Assert.Contains("not built yet", err);
        Assert.Contains("OP-65", err);
    }

    // ------------------------------------------------------------------ tolerance

    /// <summary>The tolerance text the command writes is the Tolerance Data Editor's report text
    /// (ReportLines joined by newlines) of the same run.</summary>
    [Fact]
    public void Tolerance_WritesTheEditorsReportText()
    {
        var table = ToleranceRunFixtures.Table(ToleranceRunFixtures.Row("TRAD", 1, 0, -0.5, 0.5), ToleranceRunFixtures.Row("TTHI", 1, 1, -0.2, 0.2));
        var optics = ToleranceRunFixtures.Singlet() with
        {
            Tolerances = ToleranceDataSettings.ToSettings(table)! with { Run = ToleranceRunSettings.ToSettings(ToleranceRunFixtures.Wavefront) },
        };
        string path = Save(optics);
        string outDir = Path.Combine(_dir, "tol");
        var (code, _, _) = Run("optics", "tolerance", path, "--builtin-catalogs", "--out", outDir);
        Assert.Equal(0, code);
        var loaded = new ProjectSerializer().Load(path).Optics!;
        var direct = ToleranceAnalysis.Run(loaded, ToleranceDataSettings.FromSettings(loaded.Tolerances, new List<string>()),
            GlassLookup.ActiveCatalogs(BuiltIn, null), ToleranceRunSettings.FromSettings(loaded.Tolerances!.Run, new List<string>()));
        Assert.Equal(string.Join(Environment.NewLine, direct.ReportLines()), File.ReadAllText(Path.Combine(outDir, "tolerance.txt")));
        Assert.Equal(CliRunner.ToleranceReport("Lens lens", direct).ToMarkdown(), File.ReadAllText(Path.Combine(outDir, "report.md")));
        var rows = PlotCsv.ReadTable(File.ReadAllText(Path.Combine(outDir, "rows.csv")));
        Assert.Equal(2, rows.Rows.Count);

        // An inverse mode from the command line: the increment the help defines (here a tenth of the nominal).
        string inv = Path.Combine(_dir, "inv");
        string increment = (direct.Nominal / 10).ToString("R", CultureInfo.InvariantCulture);
        Assert.Equal(0, Run("optics", "tolerance", path, "--mode", "inverse-increment", "--increment", increment, "--builtin-catalogs", "--out", inv).Code);
        Assert.Contains("Mode: InverseIncrement", File.ReadAllText(Path.Combine(inv, "tolerance.txt")));
        Assert.Equal(1, Run("optics", "tolerance", path, "--mode", "sideways").Code);
        Assert.Equal(1, Run("optics", "tolerance", path, "--criterion", "Happiness").Code);
    }

    [Fact]
    public void Tolerance_WithoutRows_FailsNamingTheEditor()
    {
        var (code, _, err) = Run("optics", "tolerance", Save(ToleranceRunFixtures.Singlet()));
        Assert.Equal(2, code);
        Assert.Contains("Tolerance Data Editor", err);
    }

    // ------------------------------------------------------------------ sweep targets

    /// <summary>An optics sweep tabulates the saved merit function at each value, equal to the
    /// merit function evaluated on the lens with that value set.</summary>
    [Fact]
    public void AnOpticsSweep_TabulatesTheMeritFunction()
    {
        var optics = LocalOptimizationFixtures.Singlet();
        string path = Save(optics);
        string job = Path.Combine(_dir, "sweep.json");
        File.WriteAllText(job, JsonSerializer.Serialize(new
        {
            project = path, mode = "OneAtATime",
            parameters = new[] { new { name = "r1", target = "optics:surface1.radius", values = new[] { 90.0, 100, 110 } } },
        }));
        string outDir = Path.Combine(_dir, "sw");
        var (code, _, _) = Run("sweep", job, "--out", outDir);
        Assert.Equal(0, code);
        var (header, rows) = PlotCsv.ReadTable(File.ReadAllText(Path.Combine(outDir, "sweep.csv")));
        int r1 = header.ToList().IndexOf("r1"), merit = header.ToList().IndexOf("Merit function");
        Assert.True(r1 >= 0 && merit >= 0, string.Join(",", header));
        Assert.Contains(header, h => h.StartsWith("Row 1 REAY"));
        var loaded = new ProjectSerializer().Load(path).Optics!;
        var function = MeritFunctionSettings.FromSettings(loaded.MeritFunction, MeritOperandRegistry.CreateDefault(), new List<string>());
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            double value = double.Parse(row[r1], CultureInfo.InvariantCulture);
            var set = OpticsTargets.Apply(loaded, "optics:surface1.radius", value);
            var expected = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(set, LocalOptimizationFixtures.NoCatalogs));
            Assert.Equal(expected.Value, double.Parse(row[merit], CultureInfo.InvariantCulture));
        }
        Assert.Contains("optics sweep", File.ReadAllText(Path.Combine(outDir, "sweep-report.md")));
    }

    [Fact]
    public void BadOpticsTargets_AreRefused_NamingTheTarget()
    {
        string path = Save(LocalOptimizationFixtures.Singlet());
        string Job(params (string Name, string Target)[] p)
        {
            string file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                project = path,
                parameters = p.Select(x => new { name = x.Name, target = x.Target, values = new[] { 1.0, 2 } }).ToArray(),
            }));
            return file;
        }
        var (code, _, err) = Run("sweep", Job(("t", "optics:surface2.thickness")));      // a marginal-ray-height solve drives it
        Assert.Equal(2, code);
        Assert.Contains("optics:surface2.thickness", err);
        Assert.Contains("solve", err);
        (code, _, err) = Run("sweep", Job(("k", "optics:surface9.conic")));
        Assert.Equal(2, code);
        Assert.Contains("optics:surface9.conic", err);
        Assert.Contains("surfaces 0 to 3", err);
        (code, _, err) = Run("sweep", Job(("k", "optics:surface1.colour")));
        Assert.Equal(2, code);
        Assert.Contains("colour", err);
        (code, _, err) = Run("sweep", Job(("k", "optics:surface1.conic"), ("h", "condition:hot.value")));
        Assert.Equal(2, code);
        Assert.Contains("condition:hot.value", err);
    }

    [Fact]
    public void OpticsTargets_SetTheCellsTheyName()
    {
        var optics = ToleranceRunFixtures.Singlet() with
        {
            SystemData = ToleranceRunFixtures.Singlet().SystemData! with { Fields = new() { new OpticsFieldSettings(), new OpticsFieldSettings { Y = 5 } } },
        };
        Assert.Equal(90, OpticsTargets.Apply(optics, "optics:surface1.radius", 90).Surfaces![1].Radius);
        Assert.Null(OpticsTargets.Apply(optics, "optics:surface1.radius", 0).Surfaces![1].Radius);           // 0 is a plane
        Assert.Equal(1 / 0.02, OpticsTargets.Apply(optics, "OPTICS:Surface1.Curvature", 0.02).Surfaces![1].Radius);
        Assert.Null(OpticsTargets.Apply(optics, "optics:surface1.curvature", 0).Surfaces![1].Radius);
        Assert.Equal(7, OpticsTargets.Apply(optics, "optics:surface1.thickness", 7).Surfaces![1].Thickness);
        Assert.Equal(-1, OpticsTargets.Apply(optics, "optics:surface2.conic", -1).Surfaces![2].Conic);
        Assert.Equal(1.6, OpticsTargets.Apply(optics, "optics:surface1.index", 1.6).Surfaces![1].Index);
        Assert.Equal(3e-6, SavedParameter(OpticsTargets.Apply(optics, "optics:surface1.par2", 3e-6).Surfaces![1], 2));
        Assert.Equal(8, OpticsTargets.Apply(optics, "optics:system.apertureValue", 8).SystemData!.ApertureValue);
        Assert.Equal(0.6, OpticsTargets.Apply(optics, "optics:wavelength1", 0.6).SystemData!.Wavelengths![0].Micrometers);
        Assert.Equal(7, OpticsTargets.Apply(optics, "optics:field2.y", 7).SystemData!.Fields![1].Y);
        // Unchanged cells stay as they were.
        Assert.Equal(optics.Surfaces![2], OpticsTargets.Apply(optics, "optics:surface1.radius", 90).Surfaces![2]);

        string Refusal(string target, double value = 1) => Assert.Throws<InvalidOperationException>(() => OpticsTargets.Apply(optics, target, value)).Message;
        Assert.Contains("infinite", Refusal("optics:surface0.thickness"));
        Assert.Contains("constant-index", Refusal("optics:surface2.index"));
        Assert.Contains("1 saved wavelength", Refusal("optics:wavelength2"));
        Assert.Contains("2 saved field", Refusal("optics:field3.x"));
        Assert.Contains("must be positive", Refusal("optics:system.apertureValue", 0));
        Assert.Contains("no model glass", Refusal("optics:surface1.modelNd"));
        Assert.Contains("not understood", Refusal("optics:lens.radius"));
        Assert.Contains("optics:surfaceX.radius", Refusal("optics:surfaceX.radius"));

        static double SavedParameter(OpticsSurfaceSettings s, int n) =>
            (s.Parameters ?? new()).Concat(s.ExtraData ?? new()).Last(p => p.Number == n).Value;
    }

    // ------------------------------------------------------------------ library pieces

    [Fact]
    public void PlotCsv_OfALinePlotAndAMap_ReadsBackBitForBit()
    {
        var line = new LinePlot("t", "x (mm)", "y, waves", new[]
        {
            new AnalysisCurve("a", new[] { 0.1, 1.0 / 3 }, new[] { double.NaN, -2e-300 }),
            new AnalysisCurve("b \"q\"", new[] { 5.0 }, new[] { Math.PI }),
        });
        var points = PlotCsv.ReadPoints(AnalysisPlotCsv.Write(line));
        Assert.Equal(new[] { ("a", 0.1, double.NaN), ("a", 1.0 / 3, -2e-300), ("b \"q\"", 5.0, Math.PI) },
            points.Select(p => (p.Series, p.X, p.Y)).ToArray());
        Assert.Equal(AnalysisPlotCsv.Write(line), AnalysisPlotCsv.Write(line));

        var field = new GridField(new double[,] { { 1, double.NaN }, { 3, 1.0 / 7 } }, -1, 1, -2, 2);
        var (header, rows) = PlotCsv.ReadTable(AnalysisPlotCsv.Write(new AnalysisMapPlot("m", "x", "y", field, "waves")));
        Assert.Equal(new[] { "y\\x", "-1", "1" }, header);
        Assert.Equal(1.0 / 7, double.Parse(rows[1][2], CultureInfo.InvariantCulture));
        Assert.Equal("NaN", rows[0][2]);
    }

    [Fact]
    public void SettingsOverrides_AreStrict()
    {
        var spot = new StandardSpotAnalysis();
        var changed = (StandardSpotSettings)OpticsBatch.WithOverrides(spot, new StandardSpotSettings(),
            new Dictionary<string, string> { ["raydensity"] = "9", ["Pattern"] = "square", ["ShowAiryDisk"] = "false" });
        Assert.Equal(9, changed.RayDensity);
        Assert.Equal(PupilPattern.Square, changed.Pattern);
        Assert.False(changed.ShowAiryDisk);
        Assert.Contains("Hexapolar", Assert.Throws<ArgumentException>(() => OpticsBatch.WithOverrides(spot, new StandardSpotSettings(),
            new Dictionary<string, string> { ["Pattern"] = "spiral" })).Message);
        Assert.Equal(5, ((StandardSpotSettings)OpticsBatch.SettingsFromJson(spot, "{\"rayDensity\":5}")).RayDensity);
        Assert.Contains("Standard spot diagram", Assert.Throws<ArgumentException>(() => OpticsBatch.SettingsFromJson(spot, "{\"rays\":5}")).Message);
    }
}
