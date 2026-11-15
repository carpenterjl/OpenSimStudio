using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.HighYield;
using OpenSim.Optics.Tolerancing;
using OpenSim.Tests.Optics;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Cli;

/// <summary>
/// Wave 9 wiring: optics optimise --high-yield (OP-62) and optics tolerance --wizard and
/// --quick-yield (OP-66). Each command's output is compared with a direct call of the engine on
/// the same saved lens, settings and catalogs; the lenses are test fixtures (arbitrary inputs).
/// </summary>
public sealed class OpticsYieldCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-optics-yield-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsYieldCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int Run(params string[] args) => CliRunner.Run(args, new StringWriter(), new StringWriter(), CancellationToken.None);

    private static IReadOnlyList<OpticalCatalog> BuiltIn =>
        GlassCatalogLibrary.BuiltInCatalogs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private string Save(OpticsSettings optics, string name = "lens")
    {
        string path = Path.Combine(_dir, name + ".ossproj");
        new ProjectSerializer().Save(new SimProject { Name = "Lens " + name, Optics = optics }, path);
        return path;
    }

    private static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    /// <summary>--high-yield W: the written project's merit function is the saved one with OP-62's
    /// rows at weight W (as the Optimize window writes them), and its variable is the local
    /// optimizer's result against that merit function with the same options.</summary>
    [Fact]
    public void HighYield_AddsTheRows_AndOptimisesAgainstThem()
    {
        var shell = HighYieldFixtures.Shell(15, variable: true);
        var rows = DefaultMeritWizard.Generate(HighYieldFixtures.Configurations(shell), 1, new DefaultMeritOptions()).Rows;
        var optics = shell with { MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(rows)) };
        string path = Save(optics);
        string outDir = Path.Combine(_dir, "hy");
        Assert.Equal(0, Run("optics", "optimise", path, "--high-yield", "1", "--cycles", "2", "--builtin-catalogs", "--out", outDir));

        var loaded = new ProjectSerializer().Load(path).Optics!;
        var active = GlassLookup.ActiveCatalogs(BuiltIn, loaded.Materials?.ActiveCatalogs);
        var registry = MeritOperandRegistry.CreateDefault();
        var notes = new List<string>();
        var hy = HighYieldMerit.Generate(new ProjectMeritConfigurations(loaded, active), 1, DefaultMeritWizardSettings.FromSettings(loaded.MeritWizard, notes), 1);
        var function = HighYieldMerit.WithRows(MeritFunctionSettings.FromSettings(loaded.MeritFunction, registry, notes), hy.Rows, registry);
        var withRows = loaded with { MeritFunction = MeritFunctionSettings.ToSettings(function) };
        var direct = LocalOptimizer.Run(withRows, active, new LocalOptimizationOptions { Cycles = LocalOptimizationCycles.Fixed, CycleCount = 2 });

        var written = new ProjectSerializer().Load(Path.Combine(outDir, "lens-optimised.ossproj")).Optics!;
        Assert.Equal(direct.Optimized.Surfaces![1].Radius, written.Surfaces![1].Radius);
        Assert.Equal(function.Rows.Count, MeritFunctionSettings.FromSettings(written.MeritFunction, registry, new List<string>()).Rows.Count);
        Assert.Equal(rows.Count, MeritFunctionSettings.FromSettings(new ProjectSerializer().Load(path).Optics!.MeritFunction, registry, new List<string>()).Rows.Count);   // input unchanged

        Assert.Equal(1, Run("optics", "optimise", path, "--high-yield"));                         // no weight: usage
        Assert.Equal(2, Run("optics", "optimise", path, "--high-yield", "-1", "--builtin-catalogs", "--out", Path.Combine(_dir, "bad")));   // the engine's refusal
    }

    private static OpticsSettings ToleranceLens(ToleranceTable table) => X.Singlet() with
    {
        Tolerances = ToleranceDataSettings.ToSettings(table)! with
        {
            Run = ToleranceRunSettings.ToSettings(X.Wavefront),
            MonteCarlo = MonteCarloSettings.ToSettings(new MonteCarloOptions { Seed = 5, YieldLimit = 1 }),
            QuickYield = QuickYieldSettings.ToSettings(new QuickYieldOptions { Samples = 50 }),
        },
    };

    /// <summary>--quick-yield is QuickYield.Run with the saved run, Monte Carlo and Quick Yield
    /// settings; --samples overrides the saved sample count; it refuses --montecarlo.</summary>
    [Fact]
    public void QuickYield_FromTheCommandLine_IsTheEnginesRun()
    {
        var table = X.Table(X.Row("TRAD", 1, 0, -0.5, 0.5), X.Row("TSDX", 1, 0, -0.05, 0.05));
        string path = Save(ToleranceLens(table));
        string outDir = Path.Combine(_dir, "qy");
        Assert.Equal(0, Run("optics", "tolerance", path, "--quick-yield", "--samples", "30", "--builtin-catalogs", "--out", outDir));

        var loaded = new ProjectSerializer().Load(path).Optics!;
        var active = GlassLookup.ActiveCatalogs(BuiltIn, loaded.Materials?.ActiveCatalogs);
        var run = ToleranceRunSettings.FromSettings(loaded.Tolerances!.Run, new List<string>());
        var mc = MonteCarloSettings.FromSettings(loaded.Tolerances.MonteCarlo, new List<string>(), out _, out _);
        var direct = QuickYield.Run(loaded, ToleranceDataSettings.FromSettings(loaded.Tolerances, new List<string>()), active, run, mc,
            QuickYieldSettings.FromSettings(loaded.Tolerances.QuickYield) with { Samples = 30 });
        Assert.True(direct.Succeeded, string.Join("; ", direct.Failures));
        Assert.Equal(Lines(direct.ReportLines()), File.ReadAllText(Path.Combine(outDir, "quickyield.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "histogram.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "yield.csv")));

        Assert.Equal(1, Run("optics", "tolerance", path, "--quick-yield", "--montecarlo"));
    }

    /// <summary>--wizard runs the Tolerance Wizard's rows (from its saved settings, here the defaults)
    /// in place of the saved ones, and leaves the project unchanged.</summary>
    [Fact]
    public void Wizard_RunsTheWizardsRows()
    {
        var saved = X.Table(X.Row("TRAD", 1, 0, -0.1, 0.1));
        string path = Save(ToleranceLens(saved));
        string outDir = Path.Combine(_dir, "wiz");
        Assert.Equal(0, Run("optics", "tolerance", path, "--wizard", "--builtin-catalogs", "--out", outDir));

        var loaded = new ProjectSerializer().Load(path).Optics!;
        var active = GlassLookup.ActiveCatalogs(BuiltIn, loaded.Materials?.ActiveCatalogs);
        var table = ToleranceDataSettings.FromSettings(loaded.Tolerances, new List<string>());
        var wizard = ToleranceWizard.Generate(loaded, table, active, ToleranceWizardSettings.FromSettings(loaded.Tolerances!.Wizard, new List<string>()));
        Assert.True(wizard.Table.Rows.Count > table.Rows.Count);
        var direct = ToleranceAnalysis.Run(loaded, wizard.Table, active, ToleranceRunSettings.FromSettings(loaded.Tolerances.Run, new List<string>()));
        Assert.Equal(Lines(direct.ReportLines()), File.ReadAllText(Path.Combine(outDir, "tolerance.txt")));
        Assert.Single(ToleranceDataSettings.FromSettings(new ProjectSerializer().Load(path).Optics!.Tolerances, new List<string>()).Rows);
    }
}
