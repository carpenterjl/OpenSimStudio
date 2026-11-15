using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.DesignTools;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization;

namespace OpenSim.Cli;

// OP-61: "optics convert" runs a design conversion tool on a project's lens (docs/Optics-Conventions.md §59).

public static partial class CliRunner
{
    /// <summary>
    /// <c>opensim optics convert &lt;project&gt; --tool asphere|best-asphere|stock [options]</c>:
    /// Convert Asphere Types (<c>--surface N --to even|extended|qbfs|qcon [--terms N] [--keep-radius]</c>),
    /// Find Best Asphere (<c>[--first N] [--last N] [--kind conic|even] [--order 4..16]</c>) or Stock
    /// Lens Matching (<c>--catalogs DIR [--vendor NAME] [--variables] [--matches N] [--efl-tol P]
    /// [--epd-tol P] [--air] [--any-shape] [--both-ways]</c>), with <c>[--cycles auto|N]</c> for the
    /// trials. Writes report.txt, log.txt and the project with the result (the input is unchanged).
    /// Ctrl+C stops a search and writes what it found (exit 2).
    /// </summary>
    private static int OpticsConvert(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(line); stdout.WriteLine(line); }
        var (_, optics, catalogs) = LoadOptics(projectPath, o, Log, needSurfaces: true);
        var active = GlassLookup.ActiveCatalogs(catalogs, optics.Materials?.ActiveCatalogs);
        string tool = o.TryGetValue("tool", out var t) && t != "true" ? t.ToLowerInvariant()
            : throw new UsageException("'optics convert' needs --tool asphere, best-asphere or stock.");
        int Whole(string key, int fallback)
        {
            double v = Number(o, key, fallback);
            return v == Math.Floor(v) && v >= int.MinValue && v <= int.MaxValue ? (int)v : throw new UsageException($"--{key} takes a whole number, not '{o[key]}'.");
        }
        var local = new LocalOptimizationOptions();
        if (o.TryGetValue("cycles", out var c) && !c.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            int n = Whole("cycles", 5);
            if (n < 1) throw new UsageException($"--cycles takes auto or a whole number of at least 1, not '{c}'.");
            local = new LocalOptimizationOptions { Cycles = LocalOptimizationCycles.Fixed, CycleCount = n };
        }
        void Progress(DesignToolProgress p) => stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Done}/{p.Total} {p.Step}: best merit {p.BestMerit:g8}"));

        OpticsSettings? result;
        IReadOnlyList<string> report;
        bool cancelled = false;
        try
        {
            switch (tool)
            {
                case "asphere":
                {
                    var target = Choice(o, "to", AsphereTarget.QTypeQcon, new()
                    {
                        ["even"] = AsphereTarget.EvenAsphere, ["extended"] = AsphereTarget.ExtendedAsphere,
                        ["qbfs"] = AsphereTarget.QTypeQbfs, ["qcon"] = AsphereTarget.QTypeQcon,
                    });
                    if (!o.ContainsKey("surface")) throw new UsageException("--tool asphere needs --surface N.");
                    var r = AsphereTypeConversion.Run(optics, active, new AsphereTypeConversionOptions
                    {
                        Surface = Whole("surface", 0), Target = target, Terms = o.ContainsKey("terms") ? Whole("terms", 0) : null,
                        VaryRadiusAndConic = !o.ContainsKey("keep-radius"), CancellationToken = cancellation,
                    });
                    result = r.Settings;
                    report = r.ReportLines();
                    break;
                }
                case "best-asphere":
                {
                    var kind = Choice(o, "kind", BestAsphereKind.Conic, new() { ["conic"] = BestAsphereKind.Conic, ["even"] = BestAsphereKind.EvenAsphere });
                    var r = FindBestAsphere.Run(optics, active, new FindBestAsphereOptions
                    {
                        FirstSurface = o.ContainsKey("first") ? Whole("first", 1) : null, LastSurface = o.ContainsKey("last") ? Whole("last", 1) : null,
                        Kind = kind, MaxOrder = Whole("order", 8), Optimization = local, CancellationToken = cancellation, Progress = Progress,
                    });
                    result = r.BestSurface is null ? null : r.Best;
                    report = r.ReportLines();
                    cancelled = r.Cancelled;
                    break;
                }
                case "stock":
                {
                    string folder = o.TryGetValue("catalogs", out var f) && f != "true" ? f : LensCatalogLibrary.ChosenFolder();
                    var library = LensCatalogLibrary.Open(folder, catalogs);
                    foreach (var p in library.Problems) Log("Stock lens catalogs: " + p);
                    IReadOnlyList<LensCatalog> vendors = o.TryGetValue("vendor", out var v) && v != "true"
                        ? new[] { library.Find(v) ?? throw new UsageException($"--vendor '{v}' is not a catalog in {library.Folder}; it has {string.Join(", ", library.Catalogs.Select(x => x.Name))}.") }
                        : library.Catalogs;
                    var r = StockLensMatching.Run(optics, vendors, active, new StockLensMatchingOptions
                    {
                        Surfaces = o.ContainsKey("variables") ? StockLensSurfaces.Variables : StockLensSurfaces.All,
                        ShowMatches = Whole("matches", 5), EflTolerancePercent = Number(o, "efl-tol", 25), EpdTolerancePercent = Number(o, "epd-tol", 25),
                        AirThicknessCompensation = o.ContainsKey("air"), Optimization = local,
                        MatchShape = !o.ContainsKey("any-shape"), MatchOrientation = !o.ContainsKey("both-ways"),
                        CancellationToken = cancellation, Progress = Progress,
                    });
                    result = r.Best;
                    report = r.ReportLines();
                    cancelled = r.Cancelled;
                    break;
                }
                default:
                    throw new UsageException($"--tool takes asphere, best-asphere or stock, not '{tool}'.");
            }
        }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        string dir = OutDir(projectPath, o, "-optics-convert");
        foreach (var line in report) stdout.WriteLine(line);
        File.WriteAllText(Path.Combine(dir, "report.txt"), Join(report));
        if (result is not null)
        {
            var saved = new ProjectSerializer().Load(projectPath);
            saved.Optics = result;
            string path = Path.Combine(dir, Path.GetFileNameWithoutExtension(projectPath) + "-converted" + ProjectSerializer.FileExtension);
            new ProjectSerializer().Save(saved, path);
            Log($"Project with the result written to {path} (the input project is unchanged).");
        }
        else Log("No result to write.");
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        return cancelled ? 2 : 0;
    }
}
