using OpenSim.Optics.NonSequential.Detectors;

namespace OpenSim.Optics.NonSequential.RayDatabase;

/// <summary>
/// One wavelength's run in a ray database (plan item OP-83; docs/Optics-Conventions.md §75): the
/// trace wavelength (µm in the system's air), its wavelength number (OpticStudio's "W" filter
/// flag and the UFD file's segment-0 "storage") and how many rays the run launched (the N of the
/// detectors' noise estimate, which counts rays a save filter left out of the file).
/// </summary>
public sealed record NscRayDatabaseRun(double AirMicrometers, int WavelengthNumber, long LaunchedRays);

/// <summary>
/// One launched ray's whole tree as saved: the run it belongs to (an index into
/// <see cref="NscRayDatabase.Runs"/>), its number within that run (the tracer's ray number,
/// which fixes its 64-ray chunk), the source object that launched it (0 when unknown) and its
/// segments in the order traced. Each segment's <see cref="NscSegment.DetectorHits"/> holds what
/// it left on detector objects, exactly as the tracer handed it to them.
/// </summary>
public sealed record NscRecordedRay
{
    public required int Run { get; init; }
    public required long RayNumber { get; init; }
    public int SourceObject { get; init; }
    public required IReadOnlyList<NscSegment> Segments { get; init; }
}

/// <summary>Where a database came from: the lossless OpenSim format keeps every detector hit, so
/// it replays onto detectors to the bit; a UFD file does not.</summary>
public enum NscRayDatabaseOrigin
{
    /// <summary>Recorded in memory by a trace.</summary>
    Trace = 0,
    /// <summary>Read from an OpenSim full-data file.</summary>
    OpenSimFile = 1,
    /// <summary>Read from an OpticStudio uncompressed full data (UFD) file.</summary>
    UfdFile = 2,
}

/// <summary>
/// Every saved ray tree of one or more source runs (OpticStudio's ray database, help "Ray
/// Database (ZRD) Files"), in run order and, within a run, in ray-number order.
/// </summary>
public sealed record NscRayDatabase
{
    public required IReadOnlyList<NscRayDatabaseRun> Runs { get; init; }
    public required IReadOnlyList<NscRecordedRay> Rays { get; init; }
    public NscRayDatabaseOrigin Origin { get; init; } = NscRayDatabaseOrigin.Trace;

    /// <summary>True when every segment carries its detector hits, so the database can be played
    /// back onto detectors (<see cref="NscRayPlayback"/>).</summary>
    public bool HasDetectorHits => Origin != NscRayDatabaseOrigin.UfdFile;

    /// <summary>The largest number of segments of any ray.</summary>
    public int MaxSegments => Rays.Count == 0 ? 0 : Rays.Max(r => r.Segments.Count);

    /// <summary>The wavelength (µm in air) of ray <paramref name="ray"/>.</summary>
    public double Wavelength(NscRecordedRay ray) => Runs[ray.Run].AirMicrometers;

    /// <summary>Checks the database is well formed: runs exist, ray numbers ascend within a run,
    /// segments are numbered from 0 with parents before children. Throws
    /// <see cref="InvalidDataException"/> naming the ray and segment.</summary>
    public void Validate(string what = "Ray database")
    {
        var last = new Dictionary<int, long>();
        for (int k = 0; k < Rays.Count; k++)
        {
            var r = Rays[k];
            if (r.Run < 0 || r.Run >= Runs.Count)
                throw new InvalidDataException($"{what}: ray {k} names run {r.Run}; the database has {Runs.Count} run(s).");
            if (last.TryGetValue(r.Run, out long prev) && r.RayNumber <= prev)
                throw new InvalidDataException($"{what}: ray {k} (run {r.Run}, ray number {r.RayNumber}) does not follow ray number {prev}; rays must ascend within a run.");
            last[r.Run] = r.RayNumber;
            if (r.Segments.Count == 0)
                throw new InvalidDataException($"{what}: ray {k} has no segments.");
            for (int s = 0; s < r.Segments.Count; s++)
            {
                var seg = r.Segments[s];
                if (seg.Number != s)
                    throw new InvalidDataException($"{what}: ray {k}, segment {s} is numbered {seg.Number}.");
                if (s == 0 ? seg.Parent != -1 : seg.Parent < 0 || seg.Parent >= s)
                    throw new InvalidDataException($"{what}: ray {k}, segment {s} has parent {seg.Parent}; a parent must come before its child (segment 0 has none).");
            }
        }
    }
}

/// <summary>
/// Collects the ray trees of a trace (OpticStudio's Ray Trace control "Save Rays"; help "Ray
/// Trace": "If the filter is not left blank, then the defined filter will be applied to each ray
/// tree traced before it is saved to a file. Only if the ray tree passes the filter will the ray
/// tree be saved"). Hand it to <see cref="NscTracer.Run(IReadOnlyList{NscLaunchRay}, NscRayRecorder?, CancellationToken)"/>
/// or <see cref="Sources.NscSourceRun.Run"/>: each run's trees arrive in chunk order, so the
/// database is the same at any thread count. Not thread-safe; one recorder per trace.
/// </summary>
public sealed class NscRayRecorder
{
    private readonly List<NscRayDatabaseRun> _runs = new();
    private readonly List<NscRecordedRay> _rays = new();
    private readonly NscRayFilter? _filter;
    private readonly NscRayFilterFrame? _frame;
    private IReadOnlyList<int>? _nextSources;
    private int? _nextWavelengthNumber;
    private IReadOnlyList<int>? _sources;
    private long _passed;

    /// <param name="filter">The save filter (null: every tree is kept).</param>
    /// <param name="frame">The scene frame the filter's local-coordinate flags need (X_XGT and
    /// the like); null when the filter has none.</param>
    public NscRayRecorder(NscRayFilter? filter = null, NscRayFilterFrame? frame = null)
    {
        _filter = filter;
        _frame = frame;
        if (filter is not null && filter.NeedsFrame && frame is null)
            throw new ArgumentException($"Save filter \"{filter.Text}\": its local-coordinate flags need the scene's object placements; give the recorder a frame.");
    }

    /// <summary>The trees recorded so far.</summary>
    public NscRayDatabase Database => new() { Runs = _runs.ToArray(), Rays = _rays.ToArray() };

    /// <summary>Trees the save filter rejected.</summary>
    public long Rejected { get; private set; }

    /// <summary>Sets the source object of each ray (by ray number) and the wavelength number of
    /// the next run (<see cref="Sources.NscSourceRun"/> calls this before each wavelength).</summary>
    public void PrepareRun(IReadOnlyList<int>? sourceObjects, int? wavelengthNumber)
    {
        _nextSources = sourceObjects;
        _nextWavelengthNumber = wavelengthNumber;
    }

    internal void BeginRun(double airMicrometers, long launchedRays)
    {
        _runs.Add(new NscRayDatabaseRun(airMicrometers, _nextWavelengthNumber ?? _runs.Count + 1, launchedRays));
        _sources = _nextSources;
        _nextSources = null;
        _nextWavelengthNumber = null;
    }

    internal void Add(long rayNumber, IReadOnlyList<NscSegment> segments)
    {
        int source = _sources is not null && rayNumber < _sources.Count ? _sources[(int)rayNumber] : 0;
        var ray = new NscRecordedRay { Run = _runs.Count - 1, RayNumber = rayNumber, SourceObject = source, Segments = segments };
        if (_filter is not null)
        {
            bool keep = _filter.TreePasses(ray, _runs[^1], _frame);
            if (keep && _filter.TreeRange is { } range)
            {
                _passed++;   // {#n1-n2}: the n1-th to n2-th passing trees, counted over the whole trace
                keep = _passed >= range.From && _passed <= range.To;
            }
            if (!keep)
            {
                Rejected++;
                return;
            }
        }
        _rays.Add(ray);
    }

    /// <summary>A detector hit appended to a segment's list (the tracer's call; OP-83).</summary>
    internal static IReadOnlyList<NscDetectorHit> Append(IReadOnlyList<NscDetectorHit>? hits, in NscDetectorHit hit)
    {
        var list = hits as List<NscDetectorHit> ?? new List<NscDetectorHit>(hits ?? Array.Empty<NscDetectorHit>());
        list.Add(hit);
        return list;
    }
}
