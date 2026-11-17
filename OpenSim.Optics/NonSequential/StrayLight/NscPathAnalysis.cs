using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>How Path Analysis orders its paths (help, "Path Analysis", "Sort Paths By": "total
/// ending flux, number of objects struck, number of branches, last object struck, number of unique
/// objects struck, average optical path length, number of diffraction events").</summary>
public enum NscPathSort
{
    TotalFlux = 0,
    ObjectsStruck = 1,
    Branches = 2,
    LastObject = 3,
    UniqueObjects = 4,
    AverageOpticalPath = 5,
    DiffractionEvents = 6,
}

/// <summary>What Path Analysis reads and how (help, "Path Analysis"). <see cref="Detector"/> 0
/// is the help's analysis (each branch's flux at its end); a detector object number groups that
/// detector's recorded hits instead (docs/Optics-Conventions.md §78).</summary>
public sealed record NscPathAnalysisOptions
{
    /// <summary>"First/Last Ray": 1-based; Last −1 for the last ray.</summary>
    public long FirstRay { get; init; } = 1;
    public long LastRay { get; init; } = -1;
    public NscRayFilter? Filter { get; init; }
    public NscRayFilterFrame? Frame { get; init; }

    /// <summary>"Relative Minimum Flux": a branch whose end flux over its launch flux is below
    /// this goes to the uncategorized sum.</summary>
    public double RelativeMinimumFlux { get; init; }

    public NscPathSort Sort { get; init; } = NscPathSort.TotalFlux;

    /// <summary>0: every branch by its end flux; n: the hits on detector object n.</summary>
    public int Detector { get; init; }

    /// <summary>"the maximum number of unique paths (currently 10,000)" (help, "Path Analysis").</summary>
    public int MaxPaths { get; init; } = 10_000;
}

/// <summary>One path of a Path Analysis: its number after sorting (1 first), its sequence, the
/// flux it delivers (in the database's flux unit), that flux as a percentage of the flux launched
/// by the rays read, the branches (or detector hits) grouped in it, and the sort keys.</summary>
public sealed record NscPathRecord
{
    public required int Number { get; init; }
    public required NscPathSequence Sequence { get; init; }
    public required double Flux { get; init; }
    public required double RelativePercent { get; init; }
    public required long Branches { get; init; }
    public required int ObjectsStruck { get; init; }
    public required int UniqueObjects { get; init; }
    public required int LastObject { get; init; }
    /// <summary>The mean optical path of the grouped branch ends (or hits), meters.</summary>
    public required double AverageOpticalPath { get; init; }
    public required int DiffractionEvents { get; init; }
}

/// <summary>The paths of a Path Analysis, the uncategorized sum and the totals.</summary>
public sealed record NscPathAnalysisResult
{
    public required IReadOnlyList<NscPathRecord> Paths { get; init; }
    public required double Uncategorized { get; init; }
    public required long UncategorizedBranches { get; init; }
    /// <summary>The flux launched by every ray in the first/last range (the percentages' base).</summary>
    public required double FluxIn { get; init; }
    /// <summary>The sum of every path's flux and the uncategorized sum (compensated).</summary>
    public required double Total { get; init; }
    /// <summary>Branches (or hits) read after the filter.</summary>
    public required long Branches { get; init; }
    public required long RaysRead { get; init; }
    public required int Detector { get; init; }
}

/// <summary>
/// Path Analysis (plan item OP-84; help, "Path Analysis"; docs/Optics-Conventions.md §78): the
/// branches of a ray database grouped by path. "Rays that have the same sequence of object faces
/// are grouped in the same path and their flux is added together to determine the path's total
/// flux"; branches below the Relative Minimum Flux, or past the 10 000th distinct path, go to the
/// "uncategorized" sum; branches with more than 100 entries share the overflow path.
/// <para>
/// With a detector object, each hit the database recorded on that detector (on a segment that
/// lies on a branch passing the filter) is grouped by the path from the source to the segment that
/// made it, and its power summed: the paths and the uncategorized sum then add up to the
/// detector's total flux from the same hits (the gate). Such paths are prefixes: their filters
/// select the branches that start with them (a transparent detector lets a branch go on).
/// </para>
/// Sums are compensated (Neumaier), in database order, so the result does not depend on threads.
/// </summary>
public static class NscPathAnalysis
{
    public static NscPathAnalysisResult Analyse(NscRayDatabase database, NscPathAnalysisOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        if (options.FirstRay < 1) throw new ArgumentException($"Path Analysis: the first ray is counted from 1 (it is {options.FirstRay}).");
        if (options.LastRay != -1 && options.LastRay < options.FirstRay)
            throw new ArgumentException($"Path Analysis: the last ray ({options.LastRay}) must be −1 or not before the first ({options.FirstRay}).");
        if (!(options.RelativeMinimumFlux >= 0) || !double.IsFinite(options.RelativeMinimumFlux))
            throw new ArgumentException($"Path Analysis: the relative minimum flux must be 0 or more (it is {options.RelativeMinimumFlux}).");
        if (options.MaxPaths < 1) throw new ArgumentException($"Path Analysis: the maximum number of paths must be 1 or more (it is {options.MaxPaths}).");
        if (options.Detector < 0) throw new ArgumentException($"Path Analysis: the detector must be 0 (every branch) or a detector object number (it is {options.Detector}).");
        if (options.Detector > 0 && !database.HasDetectorHits)
            throw new NotSupportedException("Path Analysis by detector: the database was read from a UFD file, which does not keep detector hits; save the trace in the OpenSim ray database format or use detector 0.");
        if (options.Filter?.NeedsFrame == true && options.Frame is null)
            throw new ArgumentException($"Path Analysis: the filter \"{options.Filter.Text}\" needs the scene's object placements.");

        long first = options.FirstRay, last = options.LastRay < 0 ? long.MaxValue : options.LastRay;
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var order = new List<Group>();
        NscLedgerBuilder.Sum fluxIn = default, uncategorized = default, total = default;
        long uncategorizedBranches = 0, branchesRead = 0, raysRead = 0, passedTrees = 0;
        bool detectorSeen = options.Detector == 0;

        for (long k = first - 1; k < database.Rays.Count && k < last; k++)
        {
            if ((k & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var ray = database.Rays[(int)k];
            raysRead++;
            double launched = ray.Segments[0].Intensity;
            fluxIn.Add(launched);
            var run = database.Runs[ray.Run];
            var branches = NscRayFilter.Branches(ray.Segments);
            var passing = new List<int[]>();
            foreach (var b in branches)
                if (options.Filter is null || options.Filter.BranchPasses(ray, b, run, options.Frame)) passing.Add(b);
            if (passing.Count == 0) continue;
            passedTrees++;
            if (options.Filter?.TreeRange is { } range)
            {
                if (passedTrees < range.From) continue;
                if (passedTrees > range.To) break;
            }

            if (options.Detector == 0)
            {
                foreach (var b in passing)
                {
                    var end = ray.Segments[b[^1]];
                    Add(NscPathSequence.Of(ray, b), end.Intensity, end.OpticalPath, Diffractions(ray, b), launched);
                }
            }
            else
            {
                var keep = new bool[ray.Segments.Count];
                foreach (var b in passing) foreach (int s in b) keep[s] = true;
                for (int s = 0; s < ray.Segments.Count; s++)
                {
                    if (!keep[s] || ray.Segments[s].DetectorHits is not { } hits) continue;
                    NscPathSequence? path = null;
                    int[]? chain = null;
                    foreach (var hit in hits)
                    {
                        if (hit.ObjectNumber != options.Detector) continue;
                        detectorSeen = true;
                        chain ??= NscPathSequence.ChainTo(ray.Segments, s);
                        path ??= NscPathSequence.Of(ray, chain, prefix: true);
                        Add(path, hit.Power, hit.OpticalPath, Diffractions(ray, chain), launched);
                    }
                }
            }
        }

        void Add(NscPathSequence path, double flux, double optical, int diffractions, double launched)
        {
            branchesRead++;
            total.Add(flux);
            bool tooFaint = launched > 0 ? flux / launched < options.RelativeMinimumFlux : options.RelativeMinimumFlux > 0;
            string key = path.Text;
            if (tooFaint || (!groups.ContainsKey(key) && groups.Count >= options.MaxPaths))
            {
                uncategorized.Add(flux);
                uncategorizedBranches++;
                return;
            }
            if (!groups.TryGetValue(key, out var g))
            {
                g = new Group(path, order.Count);
                groups[key] = g;
                order.Add(g);
            }
            g.Flux.Add(flux);
            g.Optical.Add(optical);
            g.Count++;
            g.Diffractions = Math.Max(g.Diffractions, diffractions);
        }

        double fin = fluxIn.Total;
        var sorted = Sort(order, options.Sort);
        var records = sorted.Select((g, i) =>
        {
            var hits = g.Path.Steps.Skip(1).ToArray();
            double flux = g.Flux.Total;
            return new NscPathRecord
            {
                Number = i + 1, Sequence = g.Path, Flux = flux,
                RelativePercent = fin > 0 ? flux / fin * 100 : 0,
                Branches = g.Count,
                ObjectsStruck = g.Path.Overflow ? int.MaxValue : hits.Length,
                UniqueObjects = g.Path.Overflow ? int.MaxValue : hits.Select(h => h.Object).Distinct().Count(),
                LastObject = hits.Length == 0 ? 0 : hits[^1].Object,
                AverageOpticalPath = g.Optical.Total / g.Count,
                DiffractionEvents = g.Diffractions,
            };
        }).ToArray();
        if (!detectorSeen && raysRead > 0)
            throw new ArgumentException($"Path Analysis: no ray read recorded a hit on object {options.Detector}; check that it is a detector of the scene the database comes from.");
        return new NscPathAnalysisResult
        {
            Paths = records, Uncategorized = uncategorized.Total, UncategorizedBranches = uncategorizedBranches,
            FluxIn = fin, Total = total.Total, Branches = branchesRead, RaysRead = raysRead, Detector = options.Detector,
        };
    }

    private static int Diffractions(NscRecordedRay ray, IReadOnlyList<int> branch)
    {
        int n = 0;
        foreach (int s in branch) if (ray.Segments[s].Events.HasFlag(NscEvents.Diffracted)) n++;
        return n;
    }

    /// <summary>The help names the sort keys but not their direction. Total ending flux and number
    /// of branches sort largest first; the other keys smallest first; ties by flux (largest first),
    /// then by first appearance in the database (§10 item 74).</summary>
    private static List<Group> Sort(List<Group> groups, NscPathSort sort)
    {
        double Key(Group g) => sort switch
        {
            NscPathSort.TotalFlux => -g.Flux.Total,
            NscPathSort.Branches => -g.Count,
            NscPathSort.ObjectsStruck => g.Path.Overflow ? double.PositiveInfinity : g.Path.ObjectsStruck,
            NscPathSort.LastObject => g.Path.Steps.Count <= 1 ? 0 : g.Path.Steps[^1].Object,
            NscPathSort.UniqueObjects => g.Path.Overflow ? double.PositiveInfinity : g.Path.Steps.Skip(1).Select(s => s.Object).Distinct().Count(),
            NscPathSort.AverageOpticalPath => g.Optical.Total / g.Count,
            NscPathSort.DiffractionEvents => g.Diffractions,
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Path Analysis: unknown sort key."),
        };
        return groups.OrderBy(Key).ThenByDescending(g => g.Flux.Total).ThenBy(g => g.First).ToList();
    }

    private sealed class Group(NscPathSequence path, int first)
    {
        public readonly NscPathSequence Path = path;
        public readonly int First = first;
        public NscLedgerBuilder.Sum Flux, Optical;
        public long Count;
        public int Diffractions;
    }

    /// <summary>The report of a result (lengths in <paramref name="unit"/>, flux in <paramref name="fluxUnit"/>).</summary>
    public static IReadOnlyList<ReportSection> Report(NscPathAnalysisResult r, LensUnit unit, string fluxUnit, bool pathFilters)
    {
        string F(double v) => StudyReport.Number(v, "g10") + (fluxUnit.Length > 0 ? " " + fluxUnit : "");
        var summary = new List<(string, string)>
        {
            ("Grouped by", r.Detector == 0 ? "each branch's flux at its end" : $"the hits on detector object {r.Detector}"),
            ("Rays read", r.RaysRead.ToString(CultureInfo.InvariantCulture)),
            ("Flux in (rays read)", F(r.FluxIn)),
            (r.Detector == 0 ? "Branches read" : "Hits read", r.Branches.ToString(CultureInfo.InvariantCulture)),
            ("Paths", r.Paths.Count.ToString(CultureInfo.InvariantCulture)),
            ("Uncategorized", $"{F(r.Uncategorized)} ({r.UncategorizedBranches} {(r.Detector == 0 ? "branches" : "hits")})"),
            (r.Detector == 0 ? "Flux out, all paths and uncategorized" : $"Flux on detector {r.Detector}, all paths and uncategorized", F(r.Total)),
        };
        var rows = r.Paths.Select(p => (IReadOnlyList<string>)new[]
        {
            p.Number.ToString(CultureInfo.InvariantCulture), F(p.Flux), StudyReport.Number(p.RelativePercent, "g6"),
            p.Branches.ToString(CultureInfo.InvariantCulture), p.Sequence.Text,
        }).ToList();
        var sections = new List<ReportSection>
        {
            new("Path analysis") { Pairs = summary },
            new("Paths")
            {
                TableHeaders = new[] { "Path", "Flux out", "Relative flux out (%)", r.Detector == 0 ? "Branches" : "Hits", "Sequence (object.face)" },
                TableRows = rows,
                Lines = new[]
                {
                    "Relative flux out is the path's flux as a percentage of the flux launched by the rays read (help, \"Path Analysis\").",
                    r.Detector == 0
                        ? "A branch's flux is its last segment's intensity, at its end before the interaction there."
                        : "Each detector hit is grouped by the path from the source to the segment that made it (\" #\": the path selects the branches that start with it).",
                },
            },
        };
        if (pathFilters)
            sections.Add(new ReportSection("Path filters")
            {
                Lines = r.Paths.Select(p => $"_{p.Number}: {p.Sequence.Text}").Prepend(
                    "Generate Path Filters: each path becomes the filter _n. An analysis does not change the project; save them with the Non-Sequential Component Editor's \"Generate path filters\" (or paste the sequences into its Path filters box).").ToArray(),
            });
        return sections;
    }
}

/// <summary>The ray database a stray-light tool reads: a ZRD file, or the project's sources
/// traced now with the Ray Trace control's options.</summary>
public static class NscStrayLightInput
{
    /// <summary>The project's scene and trace settings as <see cref="Sources.ProjectNscSources.Trace"/>
    /// builds them (the saved non-sequential controls over the system environment, coatings and
    /// primary wavelength), for tools that launch their own rays.</summary>
    public static (NscScene Scene, NscTraceSettings Settings, LensUnit Unit) Trace(OpticsAnalysisContext context, bool split,
        bool polarization, bool ignoreErrors, bool scatter, List<string> notes, string who)
    {
        ArgumentNullException.ThrowIfNull(context);
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var unit = system.Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException($"{who}: the system has no non-sequential objects.");
        var saved = context.System.NonSequential;
        var baseline = new NscTraceSettings
        {
            UsePolarization = polarization,
            Splitting = !split ? NscSplitting.None : saved?.SimpleRaySplitting == true ? NscSplitting.Simple : NscSplitting.Split,
            IgnoreErrors = ignoreErrors,
            ScatterRays = scatter,
            Environment = system.Environment,
            Coatings = OpenSim.Optics.Coatings.CoatingCatalogSettings.FromSettings(context.System.Coatings, notes),
            PrimaryMicrometers = system.Wavelengths.Primary.Micrometers,
        };
        return (scene, ProjectNonSequential.Settings(saved, unit, baseline), unit);
    }

    public static NscRayDatabase Database(OpticsAnalysisContext context, string? file, bool split, bool polarization,
        bool ignoreErrors, bool scatter, LensUnit unit, List<string> notes, out string source, out string fluxUnit,
        CancellationToken cancellation, string who)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            var db = NscZrdFile.Read(file, unit);
            source = $"{Path.GetFileName(file)} ({(db.Origin == NscRayDatabaseOrigin.UfdFile ? "Uncompressed Full Data" : "OpenSim full data")})";
            if (db.Origin == NscRayDatabaseOrigin.UfdFile)
                notes.Add($"UFD lengths are read in the system's lens unit ({ParaxialReport.UnitName(unit)}); the file does not say which unit it was written in.");
            fluxUnit = "";
            notes.Add("Fluxes are in the source units the file was traced in (watts for W and lumen sources).");
            return db;
        }
        if (ProjectNonSequential.Scene(context, unit) is null)
            throw new ArgumentException($"{who}: the system has no non-sequential objects to trace; give a ZRD file instead.");
        var recorder = new NscRayRecorder();
        var run = Sources.ProjectNscSources.Trace(context, split: split, usePolarization: polarization, ignoreErrors: ignoreErrors,
            cancellation: cancellation, recorder: recorder, scatter: scatter);
        source = $"the project's sources traced now ({string.Join(", ", run.Sources.Select(s => $"object {s.ObjectNumber} {s.TypeName}, {s.Rays} rays"))})";
        fluxUnit = run.Unit;
        notes.AddRange(run.Notes);
        return recorder.Database;
    }
}

/// <summary>Settings of the <see cref="NscPathAnalysisAnalysis"/> (help, "Path Analysis"; the
/// trace options from "Ray Trace").</summary>
public sealed record NscPathAnalysisSettings
{
    [DisplayName("Ray database"), Description("The full path of a ZRD file (UFD or OpenSim format); blank to trace the project's sources now. PAF path files are not built.")]
    public string File { get; init; } = "";

    [DisplayName("Split NSC rays"), Description("When tracing: split rays at refractive interfaces.")]
    public bool SplitRays { get; init; } = true;

    [DisplayName("Scatter NSC rays"), Description("When tracing: scatter at faces with a scatter model.")]
    public bool ScatterRays { get; init; } = true;

    [DisplayName("Use polarization"), Description("When tracing: the polarization trace (splitting turns it on).")]
    public bool UsePolarization { get; init; }

    [DisplayName("Ignore errors"), Description("When tracing: count rays with trace errors as lost energy instead of stopping.")]
    public bool IgnoreErrors { get; init; }

    [DisplayName("First ray"), Description("The first ray read, 1 for the first.")]
    public long FirstRay { get; init; } = 1;

    [DisplayName("Last ray"), Description("The last ray read; −1 for the last.")]
    public long LastRay { get; init; } = -1;

    [DisplayName("Filter"), Description("A filter string or named filter; only passing branches are grouped.")]
    public string Filter { get; init; } = "";

    [DisplayName("Generate path filters"), Description("List the filter _n of each path (the editor's \"Generate path filters\" saves them with the project).")]
    public bool GeneratePathFilters { get; init; }

    [DisplayName("Relative minimum flux"), Description("A branch whose end flux over its launch flux is below this goes to the uncategorized sum.")]
    public double RelativeMinimumFlux { get; init; }

    [DisplayName("Sort paths by"), Description("Total ending flux and branches: largest first; the others smallest first.")]
    public NscPathSort SortPathsBy { get; init; } = NscPathSort.TotalFlux;

    [DisplayName("Detector"), Description("0: group every branch by its end flux (the help's analysis); a detector object number: group that detector's hits by the path that reached it.")]
    public int Detector { get; init; }
}

/// <summary>Path Analysis as a registered analysis (id <c>NscPathAnalysis</c>).</summary>
public sealed class NscPathAnalysisAnalysis : OpticsAnalysis<NscPathAnalysisSettings>
{
    public const string SavedId = "NscPathAnalysis";
    public override string Id => SavedId;
    public override string Name => "Path analysis";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscPathAnalysisSettings settings, CancellationToken cancellation)
    {
        var (result, unit, fluxUnit, notes) = Run(context, settings, cancellation);
        return OpticsAnalysisResult.FromSections(Name, NscPathAnalysis.Report(result, unit, fluxUnit, settings.GeneratePathFilters), notes);
    }

    /// <summary>The analysis without the report (the editor's Generate path filters calls it).</summary>
    public static (NscPathAnalysisResult Result, LensUnit Unit, string FluxUnit, IReadOnlyList<string> Notes) Run(
        OpticsAnalysisContext context, NscPathAnalysisSettings settings, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        var named = NscRayDatabaseProject.NamedFilters(context);
        var filter = NscRayFilter.Parse(settings.Filter, named);
        var scene = filter?.NeedsFrame == true || settings.Detector > 0 ? ProjectNonSequential.Scene(context, unit) : null;
        if (settings.Detector > 0)
        {
            if (scene is null || settings.Detector > scene.Objects.Count || scene.Objects[settings.Detector - 1].Geometry is not Detectors.INscDetector)
                throw new ArgumentException($"Path analysis: object {settings.Detector} is not a detector object of the system.");
        }
        var db = NscStrayLightInput.Database(context, settings.File, settings.SplitRays, settings.UsePolarization, settings.IgnoreErrors,
            settings.ScatterRays, unit, notes, out string source, out string fluxUnit, cancellation, "Path analysis");
        var result = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions
        {
            FirstRay = settings.FirstRay, LastRay = settings.LastRay, Filter = filter,
            Frame = filter?.NeedsFrame == true && scene is not null ? NscRayFilterFrame.For(scene, unit) : null,
            RelativeMinimumFlux = settings.RelativeMinimumFlux, Sort = settings.SortPathsBy, Detector = settings.Detector,
        }, cancellation);
        notes.Insert(0, $"Rays from {source}{(filter is null ? "" : $"; filter \"{filter.Expanded}\"")}.");
        notes.Add("Diffraction events are always 0: non-sequential diffraction is plan item OP-82.");
        return (result, unit, fluxUnit, notes);
    }
}
