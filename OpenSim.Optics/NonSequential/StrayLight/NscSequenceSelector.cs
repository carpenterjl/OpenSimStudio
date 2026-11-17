using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.RayDatabase;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>The Sequence Generator's methods (help, "Sequence Selector": "Use Straight Through
/// Paths For All Sources", "Generate using path filters from Path Analysis", "Use ZRD or PAF File").</summary>
public enum NscSequenceMethod
{
    StraightThroughPaths = 0,
    PathFilters = 1,
    RayDatabase = 2,
}

/// <summary>Which of Path Analysis's path filters become sequences ("All Paths", "First 3
/// Paths", "First 5 Paths", "First 10 Paths", or an individual path).</summary>
public enum NscSequencePaths
{
    AllPaths = 0,
    First3Paths = 1,
    First5Paths = 2,
    First10Paths = 3,
    OnePath = 4,
}

/// <summary>
/// The Sequence Selector's Sequence Generator (plan item OP-84; help, "Sequence Selector";
/// docs/Optics-Conventions.md §78). Each method returns paths; <see cref="NscSequenceNames.Append"/>
/// turns them into SEQ# sequences after the project's own, skipping duplicates.
/// </summary>
public static class NscSequenceGenerator
{
    /// <summary>
    /// "Use straight-through paths for all sources": "a unique SEQ# filter is generated per
    /// source which contains the straight-through sequence of the ray. No scattering, polarization,
    /// or splitting will be active". The help does not say which ray's path is taken when a
    /// source's rays take several; here every source's analysis rays are traced unsplit, without
    /// scattering or polarization, their branch paths grouped, and the path carrying the most flux
    /// at its end is the source's sequence (§10 item 74). With <paramref name="mustEndInDetector"/>
    /// ("Paths must end in a detector") only paths whose last entry is a detector object count; a
    /// source with no such path gets none, and a note says so.
    /// </summary>
    public static IReadOnlyList<NscPathSequence> StraightThrough(OpticsAnalysisContext context, bool mustEndInDetector, List<string> notes,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException("Sequence selector: the system has no non-sequential objects.");
        var recorder = new NscRayRecorder();
        var run = Sources.ProjectNscSources.Trace(context, split: false, usePolarization: false, ignoreErrors: true,
            cancellation: cancellation, recorder: recorder, scatter: false);
        bool IsDetector(int o) => o >= 1 && o <= scene.Objects.Count && scene.Objects[o - 1].Geometry is Detectors.INscDetector;
        var best = new SortedDictionary<int, Dictionary<string, (NscPathSequence Path, NscLedgerBuilder.Sum Flux, int First)>>();
        int seen = 0;
        foreach (var ray in recorder.Database.Rays)
        {
            foreach (var b in NscRayFilter.Branches(ray.Segments))
            {
                var path = NscPathSequence.Of(ray, b);
                if (path.Overflow) continue;
                if (mustEndInDetector && (path.Steps.Count < 2 || !IsDetector(path.Steps[^1].Object))) continue;
                if (!best.TryGetValue(ray.SourceObject, out var bySource)) best[ray.SourceObject] = bySource = new();
                if (!bySource.TryGetValue(path.Text, out var g)) g = (path, default, seen++);
                g.Flux.Add(ray.Segments[b[^1]].Intensity);
                bySource[path.Text] = g;
            }
        }
        var result = new List<NscPathSequence>();
        foreach (var source in run.Sources.Select(s => s.ObjectNumber))
        {
            if (!best.TryGetValue(source, out var bySource) || bySource.Count == 0)
            {
                notes.Add($"Source object {source}: no straight-through path{(mustEndInDetector ? " ends in a detector" : "")}, so it has no sequence.");
                continue;
            }
            var top = bySource.Values.OrderByDescending(v => v.Flux.Total).ThenBy(v => v.First).First();
            result.Add(top.Path);
            notes.Add($"Source object {source}: {bySource.Count} distinct straight-through path(s); the one carrying the most flux ({StudyReport.Number(top.Flux.Total, "g6")} {run.Unit}) is taken.");
        }
        return result;
    }

    /// <summary>"Generate using path filters from Path Analysis": the chosen path filters
    /// (<paramref name="number"/> is the individual path for <see cref="NscSequencePaths.OnePath"/>).</summary>
    public static IReadOnlyList<NscPathSequence> FromPathFilters(IReadOnlyList<NscPathSequence> pathFilters, NscSequencePaths which, int number = 1)
    {
        ArgumentNullException.ThrowIfNull(pathFilters);
        if (pathFilters.Count == 0)
            throw new ArgumentException("Sequence selector: the project has no path filters; run Path Analysis with Generate Path Filters first (help: \"available only if the Path Analysis is run with the Generate Path Filters setting enabled\").");
        return which switch
        {
            NscSequencePaths.AllPaths => pathFilters.ToArray(),
            NscSequencePaths.First3Paths => pathFilters.Take(3).ToArray(),
            NscSequencePaths.First5Paths => pathFilters.Take(5).ToArray(),
            NscSequencePaths.First10Paths => pathFilters.Take(10).ToArray(),
            NscSequencePaths.OnePath => number >= 1 && number <= pathFilters.Count
                ? new[] { pathFilters[number - 1] }
                : throw new ArgumentException($"Sequence selector: path {number} is not defined; the project has _1 to _{pathFilters.Count}."),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, "Sequence selector: unknown path choice."),
        };
    }

    /// <summary>"Use ZRD or PAF file": the database's paths as Path Analysis groups them (filter,
    /// Relative Minimum Flux, Sort Paths By), in that order, without the overflow path.</summary>
    public static IReadOnlyList<NscPathSequence> FromDatabase(NscRayDatabase database, NscRayFilter? filter, NscRayFilterFrame? frame,
        double relativeMinimumFlux, NscPathSort sort, CancellationToken cancellation = default)
    {
        var r = NscPathAnalysis.Analyse(database, new NscPathAnalysisOptions
        {
            Filter = filter, Frame = frame, RelativeMinimumFlux = relativeMinimumFlux, Sort = sort,
        }, cancellation);
        return r.Paths.Where(p => !p.Sequence.Overflow).Select(p => p.Sequence).ToArray();
    }
}

/// <summary>Settings of the <see cref="NscSequenceSelectorAnalysis"/> (help, "Sequence Selector",
/// Sequence Generator tab).</summary>
public sealed record NscSequenceSelectorSettings
{
    [DisplayName("Method"), Description("Straight-through paths for all sources, path filters from Path Analysis, or a ZRD file.")]
    public NscSequenceMethod Method { get; init; } = NscSequenceMethod.StraightThroughPaths;

    [DisplayName("Paths must end in a detector"), Description("Straight-through paths only: keep paths whose last object is a detector.")]
    public bool PathsMustEndInDetector { get; init; } = true;

    [DisplayName("Path"), Description("Path filters: all paths, the first 3, 5 or 10, or one path.")]
    public NscSequencePaths Paths { get; init; } = NscSequencePaths.AllPaths;

    [DisplayName("Path number"), Description("Path filters with \"One path\": the path filter _n to use.")]
    public int PathNumber { get; init; } = 1;

    [DisplayName("Ray database"), Description("ZRD file: the full path of the file (UFD or OpenSim format). PAF files are not built.")]
    public string File { get; init; } = "";

    [DisplayName("Filter"), Description("ZRD file: a filter string applied to the rays first.")]
    public string Filter { get; init; } = "";

    [DisplayName("Relative minimum flux"), Description("ZRD file: branches below this relative end flux make no sequence.")]
    public double RelativeMinimumFlux { get; init; }

    [DisplayName("Sort paths by"), Description("ZRD file: the order of the generated sequences.")]
    public NscPathSort SortPathsBy { get; init; } = NscPathSort.TotalFlux;
}

/// <summary>
/// The Sequence Selector as a registered analysis (id <c>NscSequenceSelector</c>; plan item
/// OP-84): lists the project's sequences and the sequences the chosen method generates after
/// them ("Generate sequence"). An analysis does not change the project: the Non-Sequential
/// Component Editor's "Generate sequences" saves them, and its Sequences box edits names, paths
/// and check boxes (the Sequence Builder's role).
/// </summary>
public sealed class NscSequenceSelectorAnalysis : OpticsAnalysis<NscSequenceSelectorSettings>
{
    public const string SavedId = "NscSequenceSelector";
    public override string Id => SavedId;
    public override string Name => "Sequence selector";
    public override string Category => "Non-sequential";

    /// <summary>The project's sequences with the generated ones appended (duplicates skipped),
    /// and notes.</summary>
    public static (IReadOnlyList<NscSequence> Sequences, int Added, int Skipped, IReadOnlyList<string> Notes) Generate(
        OpticsAnalysisContext context, NscSequenceSelectorSettings settings, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        var saved = context.System.NonSequential;
        var existing = NscSequenceNames.Sequences(saved);
        IReadOnlyList<NscPathSequence> paths;
        switch (settings.Method)
        {
            case NscSequenceMethod.StraightThroughPaths:
                paths = NscSequenceGenerator.StraightThrough(context, settings.PathsMustEndInDetector, notes, cancellation);
                break;
            case NscSequenceMethod.PathFilters:
                paths = NscSequenceGenerator.FromPathFilters(NscSequenceNames.PathFilters(saved), settings.Paths, settings.PathNumber);
                break;
            case NscSequenceMethod.RayDatabase:
            {
                if (string.IsNullOrWhiteSpace(settings.File))
                    throw new ArgumentException("Sequence selector: \"Use ZRD or PAF file\" needs the full path of a ZRD file.");
                if (settings.File.EndsWith(".PAF", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Sequence selector: PAF path files are not built (OpticStudio writes them from its Ray Trace control; their format is not documented in the help). Use a ZRD file.");
                var filter = NscRayDatabaseProject.Filter(context, settings.Filter);
                NscRayFilterFrame? frame = null;
                if (filter?.NeedsFrame == true)
                    frame = NscRayFilterFrame.For(ProjectNonSequential.Scene(context, unit)
                        ?? throw new ArgumentException($"Sequence selector: the filter \"{filter.Text}\" needs the scene's objects."), unit);
                var db = NscZrdFile.Read(settings.File, unit);
                paths = NscSequenceGenerator.FromDatabase(db, filter, frame, settings.RelativeMinimumFlux, settings.SortPathsBy, cancellation);
                notes.Add($"Paths of {Path.GetFileName(settings.File)}{(filter is null ? "" : $" passing \"{filter.Expanded}\"")}.");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Method, "Sequence selector: unknown method.");
        }
        var list = NscSequenceNames.Append(existing, paths, out int skipped);
        return (list, list.Count - existing.Count, skipped, notes);
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscSequenceSelectorSettings settings, CancellationToken cancellation)
    {
        var (list, added, skipped, notes) = Generate(context, settings, cancellation);
        int saved = list.Count - added;
        var rows = list.Select((s, i) => (IReadOnlyList<string>)new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), s.Name, s.Enabled ? "on" : "off", i < saved ? "saved" : "generated",
            s.Path.Text, s.Path.XSeq ?? "—",
        }).ToList();
        var sections = new List<ReportSection>
        {
            new("Sequences")
            {
                Pairs = new (string, string)[]
                {
                    ("Method", settings.Method switch
                    {
                        NscSequenceMethod.StraightThroughPaths => $"straight-through paths for all sources{(settings.PathsMustEndInDetector ? ", ending in a detector" : "")}",
                        NscSequenceMethod.PathFilters => $"path filters from Path Analysis ({settings.Paths}{(settings.Paths == NscSequencePaths.OnePath ? $" _{settings.PathNumber}" : "")})",
                        _ => "a ZRD file",
                    }),
                    ("Saved sequences", saved.ToString(CultureInfo.InvariantCulture)),
                    ("Generated", $"{added} (duplicates skipped: {skipped})"),
                },
                TableHeaders = new[] { "#", "Filter name", "Check box", "Origin", "Sequence (object.face)", "Equivalent filter" },
                TableRows = rows,
                Lines = new[]
                {
                    "Each name may be used wherever a filter string is (help: \"The SEQ# filter is available to use anywhere the Filter String field is enabled\"); SEQ[a:b] selects sequences a to b of this list.",
                    "Generated sequences are not saved by this analysis; the Non-Sequential Component Editor's \"Generate sequences\" saves them, or copy the lines \"name; sequence\" into its Sequences box.",
                },
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, notes.Append(
            "Not built: PAF files, the embedded layout plot (there is no non-sequential layout drawing) and diffraction orders in sequences (OP-82).").ToArray());
    }
}
