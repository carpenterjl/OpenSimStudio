using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential.RayDatabase;

/// <summary>Settings of the <see cref="NscRayDatabaseViewerAnalysis"/> (help, "Ray Database
/// Viewer"; the trace options from "Ray Trace").</summary>
public sealed record NscRayDatabaseViewerSettings
{
    [DisplayName("File"), Description("The full path of a ZRD file (UFD or OpenSim format); blank to trace the project's sources now and show that trace.")]
    public string File { get; init; } = "";

    [DisplayName("Split NSC rays"), Description("When tracing: split rays at refractive interfaces (simple splitting when the system says so).")]
    public bool SplitRays { get; init; }

    [DisplayName("Use polarization"), Description("When tracing: the polarization trace (Fresnel and coating energy); splitting turns it on.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Ignore errors"), Description("When tracing: count rays with trace errors as lost energy instead of stopping.")]
    public bool IgnoreErrors { get; init; }

    [DisplayName("Save rays as"), Description("When tracing: the full path of a ZRD file to save the trace to (Ray Trace, \"Save Rays\"); blank to save nothing.")]
    public string SaveRaysAs { get; init; } = "";

    [DisplayName("Save filter"), Description("When tracing and saving: only ray trees that pass this filter are saved (Ray Trace, \"Filter\").")]
    public string SaveFilter { get; init; } = "";

    [DisplayName("ZRD format"), Description("The format of saved files: OpenSim full (every field and detector hit, replays exactly) or Uncompressed Full Data (UFD, for programs that read OpticStudio's ZRD).")]
    public NscZrdFormat Format { get; init; } = NscZrdFormat.OpenSimFull;

    [DisplayName("Show unprocessed data"), Description("List every segment of each ray; off lists only the segments that pass the filter.")]
    public bool ShowUnprocessedData { get; init; } = true;

    [DisplayName("Expand into branches"), Description("List each branch (one complete path from the source to a ray termination) on its own.")]
    public bool ExpandIntoBranches { get; init; }

    [DisplayName("XYZ"), Description("Show the global X, Y, Z of the segment's end.")]
    public bool Xyz { get; init; } = true;

    [DisplayName("LMN"), Description("Show the global direction cosines of the segment.")]
    public bool Lmn { get; init; }

    [DisplayName("Normal"), Description("Show the global normal of the object at the segment's end.")]
    public bool Normal { get; init; }

    [DisplayName("Path"), Description("Show the optical path in waves and lens units and the physical length (Path To).")]
    public bool Path { get; init; }

    [DisplayName("First ray"), Description("The first ray to list, 1 for the first.")]
    public long FirstRay { get; init; } = 1;

    [DisplayName("Last ray"), Description("The last ray to list; −1 for the last ray in the file, which suppresses the listing.")]
    public long LastRay { get; init; } = 10;

    [DisplayName("Apply filter"), Description("Use the filter string below.")]
    public bool ApplyFilter { get; init; }

    [DisplayName("Filter"), Description("A filter string (help, \"The Filter String\") or a named filter.")]
    public string Filter { get; init; } = "";

    [DisplayName("Save subset data as"), Description("The full path of a ZRD file to save the rays in the first/last range that pass the filter to; blank to save nothing.")]
    public string SaveSubsetAs { get; init; } = "";

    [DisplayName("Save rays on object"), Description("Save the rays that hit this object as a source file (DAT, SDF, TM25RAY): not built; leave 0.")]
    public int SaveRaysOnObject { get; init; }
}

/// <summary>The project side of the ray database: the saved named filters and filters parsed
/// with them.</summary>
public static class NscRayDatabaseProject
{
    /// <summary>The project's named filters (<see cref="OpenSim.Core.Model.OpticsNonSequentialSettings.NamedFilters"/>).</summary>
    public static NscNamedFilters NamedFilters(OpticsAnalysisContext context) =>
        NscNamedFilters.Parse(context.System.NonSequential?.NamedFilters)
            .WithStrayLight(StrayLight.NscSequenceNames.PathFilters(context.System.NonSequential),   // OP-84: _n and the sequences
                StrayLight.NscSequenceNames.Sequences(context.System.NonSequential));

    /// <summary>A filter string parsed with the project's named filters; null for blank.</summary>
    public static NscRayFilter? Filter(OpticsAnalysisContext context, string? text) =>
        NscRayFilter.Parse(text, NamedFilters(context));
}

/// <summary>
/// The Ray Database Viewer (help, "Ray Database Viewer"; plan item OP-83;
/// docs/Optics-Conventions.md §75): a saved ray database (or the project's sources traced now)
/// listed segment by segment with the parent, level, inside, hit and face numbers and the event
/// letters XRTS DGEF BZI, optionally the position, direction, normal and path data, for a range
/// of rays, filtered; the passing subset can be saved as a new ZRD file.
/// </summary>
public sealed class NscRayDatabaseViewerAnalysis : OpticsAnalysis<NscRayDatabaseViewerSettings>
{
    public const string SavedId = "NscRayDatabaseViewer";
    public override string Id => SavedId;
    public override string Name => "Ray database viewer";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscRayDatabaseViewerSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        if (settings.SaveRaysOnObject != 0)
            throw new NotSupportedException($"{Name}: \"Save Rays on Object\" writes a source file (DAT, SDF or TM25RAY), and writing source files is not built.");
        if (settings.FirstRay < 1)
            throw new ArgumentException($"{Name}: the first ray is counted from 1 (it is {settings.FirstRay}).");
        if (settings.LastRay != -1 && settings.LastRay < settings.FirstRay)
            throw new ArgumentException($"{Name}: the last ray ({settings.LastRay}) must be −1 or not before the first ({settings.FirstRay}).");
        var named = NscRayDatabaseProject.NamedFilters(context);
        var scene = ProjectNonSequential.Scene(context, unit);
        var frame = scene is null ? null : NscRayFilterFrame.For(scene, unit);
        var filter = settings.ApplyFilter ? NscRayFilter.Parse(settings.Filter, named) : null;

        NscRayDatabase db;
        string source;
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            db = NscZrdFile.Read(settings.File, unit);
            source = $"{System.IO.Path.GetFileName(settings.File)} ({(db.Origin == NscRayDatabaseOrigin.UfdFile ? "Uncompressed Full Data" : "OpenSim full data")})";
            if (db.Origin == NscRayDatabaseOrigin.UfdFile)
                notes.Add($"UFD lengths are read in the system's lens unit ({ParaxialReport.UnitName(unit)}); the file does not say which unit it was written in.");
        }
        else
        {
            if (scene is null)
                throw new ArgumentException($"{Name}: the system has no non-sequential objects to trace; give a ZRD file instead.");
            var saveFilter = NscRayFilter.Parse(settings.SaveFilter, named);
            var recorder = new NscRayRecorder(saveFilter, saveFilter?.NeedsFrame == true ? frame : null);
            var run = Sources.ProjectNscSources.Trace(context, split: settings.SplitRays, usePolarization: settings.UsePolarization,
                ignoreErrors: settings.IgnoreErrors, cancellation: cancellation, recorder: recorder);
            db = recorder.Database;
            source = $"the project's sources traced now ({string.Join(", ", run.Sources.Select(s => $"object {s.ObjectNumber} {s.TypeName}, {s.Rays} rays"))})";
            if (saveFilter is not null) notes.Add($"Save filter \"{saveFilter.Expanded}\": {recorder.Rejected} ray tree(s) left out.");
            if (!string.IsNullOrWhiteSpace(settings.SaveRaysAs))
            {
                NscZrdFile.Write(settings.SaveRaysAs, db, settings.Format, unit);
                notes.Add($"The trace was saved to {settings.SaveRaysAs} ({FormatName(settings.Format)}).");
            }
            notes.AddRange(run.Notes);
        }
        cancellation.ThrowIfCancellationRequested();

        var result = Render(db, settings, unit, filter, frame, source);
        if (!string.IsNullOrWhiteSpace(settings.SaveSubsetAs))
        {
            var subset = NscZrdFile.Subset(db, filter, frame, settings.FirstRay, settings.LastRay);
            NscZrdFile.Write(settings.SaveSubsetAs, subset, settings.Format, unit);
            notes.Add($"{subset.Rays.Count} ray(s) saved to {settings.SaveSubsetAs} ({FormatName(settings.Format)}).");
        }
        return OpticsAnalysisResult.FromSections(Name, result.Sections, result.Assumptions.Concat(notes).ToArray());
    }

    private static string FormatName(NscZrdFormat f) => f == NscZrdFormat.OpenSimFull ? "OpenSim full data" : "Uncompressed Full Data";

    /// <summary>The listing of <paramref name="db"/> (lengths in <paramref name="unit"/>).</summary>
    public static OpticsAnalysisResult Render(NscRayDatabase db, NscRayDatabaseViewerSettings s, LensUnit unit,
        NscRayFilter? filter = null, NscRayFilterFrame? frame = null, string source = "the database")
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(s);
        string u = ParaxialReport.UnitName(unit);
        string Len(double m) => StudyReport.Number(OpticsConventions.FromMeters(m, unit), "g10");
        string Num(double v) => StudyReport.Number(v, "g10");

        var window = db with
        {
            Rays = db.Rays.Skip((int)Math.Min(int.MaxValue, s.FirstRay - 1))
                .Take(s.LastRay < 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, s.LastRay - s.FirstRay + 1)).ToArray(),
        };
        var passing = NscRayFilter.Select(window, filter, frame).ToList();
        long segments = window.Rays.Sum(r => (long)r.Segments.Count);
        long passingSegments = passing.Sum(p => (long)p.Segments.Count(k => k));

        var summary = new List<(string, string)>
        {
            ("Source", source),
            ("Runs", string.Join("; ", db.Runs.Select((r, i) => $"{i + 1}: {StudyReport.Number(r.AirMicrometers, "g8")} µm (wavelength {r.WavelengthNumber}), {r.LaunchedRays} rays launched"))),
            ("Rays in the database", db.Rays.Count.ToString(CultureInfo.InvariantCulture)),
            ("Rays in the first/last range", $"{window.Rays.Count} (rays {s.FirstRay} to {(s.LastRay < 0 ? "the last" : s.LastRay.ToString(CultureInfo.InvariantCulture))})"),
            ("Segments in the range", segments.ToString(CultureInfo.InvariantCulture)),
            ("Filter", filter is null ? "none" : filter.Expanded),
            ("Rays passing", passing.Count.ToString(CultureInfo.InvariantCulture)),
            ("Segments passing", passingSegments.ToString(CultureInfo.InvariantCulture)),
            ("Largest number of segments per ray", db.MaxSegments.ToString(CultureInfo.InvariantCulture)),
        };
        var sections = new List<ReportSection> { new("Ray database") { Pairs = summary } };

        if (s.LastRay != -1)
        {
            var headers = new List<string> { "Ray" };
            if (s.ExpandIntoBranches) headers.Add("Branch");
            headers.AddRange(new[] { "Seg#", "Prnt", "Levl", "In", "Hit", "Face", "XRTS DGEF BZI" });
            if (s.Xyz) headers.AddRange(new[] { $"X ({u})", $"Y ({u})", $"Z ({u})" });
            if (s.Lmn) headers.AddRange(new[] { "L", "M", "N" });
            if (s.Normal) headers.AddRange(new[] { "Nx", "Ny", "Nz" });
            headers.Add("Intensity");
            if (s.Path) headers.AddRange(new[] { "OPL (waves)", $"OPL ({u})", $"Path to ({u})" });
            headers.Add("Comment");
            var rows = new List<IReadOnlyList<string>>();
            var position = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < window.Rays.Count; i++) position[window.Rays[i]] = i;
            foreach (var (ray, keep) in passing)
            {
                long index = s.FirstRay + position[ray];
                double waveMeters = OpticsConventions.MicrometersToMeters(db.Runs[ray.Run].AirMicrometers);
                IEnumerable<(int Branch, int[] Segs)> groups = s.ExpandIntoBranches
                    ? NscRayFilter.Branches(ray.Segments).Select((b, i) => (i + 1, b))
                    : new[] { (0, Enumerable.Range(0, ray.Segments.Count).ToArray()) };
                foreach (var (branch, list) in groups)
                {
                    if (s.ExpandIntoBranches && !s.ShowUnprocessedData && !list.All(k => keep[k])) continue;
                    foreach (int k in list)
                    {
                        if (!s.ShowUnprocessedData && !keep[k]) continue;
                        var sg = ray.Segments[k];
                        var row = new List<string> { index.ToString(CultureInfo.InvariantCulture) };
                        if (s.ExpandIntoBranches) row.Add(branch.ToString(CultureInfo.InvariantCulture));
                        row.AddRange(new[]
                        {
                            sg.Number.ToString(CultureInfo.InvariantCulture), sg.Parent < 0 ? "0" : sg.Parent.ToString(CultureInfo.InvariantCulture),
                            sg.Level.ToString(CultureInfo.InvariantCulture), sg.Inside.ToString(CultureInfo.InvariantCulture),
                            (sg.Number == 0 ? ray.SourceObject : sg.Hit).ToString(CultureInfo.InvariantCulture), sg.Face.ToString(CultureInfo.InvariantCulture),
                            Letters(sg.Events),
                        });
                        if (s.Xyz) row.AddRange(new[] { Len(sg.Position.X), Len(sg.Position.Y), Len(sg.Position.Z) });
                        if (s.Lmn) row.AddRange(new[] { Num(sg.Direction.X), Num(sg.Direction.Y), Num(sg.Direction.Z) });
                        if (s.Normal) row.AddRange(sg.Number == 0 || sg.Hit == 0 ? new[] { "—", "—", "—" } : new[] { Num(sg.Normal.X), Num(sg.Normal.Y), Num(sg.Normal.Z) });
                        row.Add(Num(sg.Intensity));
                        if (s.Path) row.AddRange(new[] { Num(sg.OpticalPath / waveMeters), Len(sg.OpticalPath), Len(sg.PathLength) });
                        row.Add(sg.Number == 0 ? $"source object {ray.SourceObject}, wavelength {db.Runs[ray.Run].WavelengthNumber}" : sg.Fate);
                        rows.Add(row);
                    }
                }
            }
            sections.Add(new ReportSection("Segments")
            {
                TableHeaders = headers,
                TableRows = rows,
                Lines = new[]
                {
                    "Segment 0 is the source point; its Hit column is the source object. Every other segment's position, normal and intensity are at its end, on the object hit, before the interaction there; Hit 0 is a miss (the position is then the segment's start).",
                    "Letters (help, \"Ray Database Viewer\"): X terminated, R reflected, T transmitted, S scattered, D diffracted and B bulk scattered at the segment's end; G ghost, E diffracted and F scattered from the parent's hit object, at its start; Z error; I total internal reflection.",
                    "The Comment column is the tracer's sentence for a trace shown now; a file does not keep it.",
                }
            });
        }
        var assumptions = new List<string>
        {
            "Filters are evaluated on each branch (a complete path from the source to one ray termination); a ray passes when one of its branches does, and a segment passes when it lies on a passing branch (docs/Optics-Conventions.md §75).",
            "Not shown: the electric field (Exyz: the trace carries coherency matrices, not fields) and the phase (not computed).",
        };
        return OpticsAnalysisResult.FromSections("Ray database viewer", sections, assumptions);
    }

    /// <summary>The event letters in the help's order "XRTS DGEF BZI".</summary>
    public static string Letters(NscEvents e)
    {
        var b = new System.Text.StringBuilder();
        void A(NscEvents f, char c) { if (e.HasFlag(f)) b.Append(c); }
        A(NscEvents.Terminated, 'X'); A(NscEvents.Reflected, 'R'); A(NscEvents.Transmitted, 'T'); A(NscEvents.Scattered, 'S');
        A(NscEvents.Diffracted, 'D'); A(NscEvents.Ghost, 'G'); A(NscEvents.DiffractedFromParent, 'E'); A(NscEvents.ScatteredFromParent, 'F');
        A(NscEvents.BulkScattered, 'B'); A(NscEvents.Error, 'Z'); A(NscEvents.TotalInternalReflection, 'I');
        return b.ToString();
    }
}
