using OpenSim.Optics.NonSequential.Detectors;

namespace OpenSim.Optics.NonSequential.RayDatabase;

/// <summary>
/// Playback ZRD on Detector (plan item OP-83; help, "Playback ZRD on Detector", ZPL
/// "ZRDPLAYBACK"; docs/Optics-Conventions.md §75): the detector hits a saved trace recorded are
/// handed again to the scene's detector objects, optionally only those of segments that pass a
/// filter. The replay repeats the trace's own reduction: per run (wavelength) in database order,
/// rays grouped into the tracer's 64-ray chunks by ray number, each chunk summed in ray order with
/// every ray closed (<see cref="NscDetectorAccumulator.EndRay"/>), the chunks added in chunk order,
/// each run built with its launched-ray count and the runs combined in order. An unfiltered replay
/// of a whole database therefore gives the traced detector data to the bit. As the help says of
/// ZRDPLAYBACK, "no attempt is made to modify object numbers or coordinates": the database must
/// come from the scene it is played onto.
/// </summary>
public static class NscRayPlayback
{
    /// <param name="detector">The detector object to load (1-based), or 0 for all of them.</param>
    /// <returns>Each loaded detector's data, in object order.</returns>
    public static IReadOnlyList<NscDetectorData> Play(NscRayDatabase database, NscScene scene, NscRayFilter? filter = null,
        int detector = 0, NscRayFilterFrame? frame = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(scene);
        if (!database.HasDetectorHits)
            throw new NotSupportedException("Playback ZRD on detector: the database was read from a UFD file, which does not keep the hits in each detector's frame that an exact replay needs; replaying UFD files is not built. Save the trace in the OpenSim ray database format.");
        var objects = scene.Objects;
        var detectors = Enumerable.Range(1, objects.Count).Where(i => objects[i - 1].Geometry is INscDetector).ToArray();
        if (detectors.Length == 0)
            throw new ArgumentException("Playback ZRD on detector: the scene has no detector objects.");
        if (detector != 0 && !detectors.Contains(detector))
            throw new ArgumentException($"Playback ZRD on detector: object {detector} is not a detector; the detectors are object{(detectors.Length == 1 ? "" : "s")} {string.Join(", ", detectors)}.");

        var selected = NscRayFilter.Select(database, filter, frame).ToList();
        var byRun = new List<(NscRecordedRay Ray, bool[] Keep)>[database.Runs.Count];
        for (int r = 0; r < byRun.Length; r++) byRun[r] = new();
        foreach (var s in selected) byRun[s.Ray.Run].Add(s);

        Dictionary<int, NscDetectorData>? total = null;
        var order = new List<int>();
        for (int r = 0; r < database.Runs.Count; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            var run = database.Runs[r];
            var all = NscDetectorSet.For(objects, run.AirMicrometers)!;
            var rays = byRun[r];
            int k = 0;
            while (k < rays.Count)
            {
                long chunk = rays[k].Ray.RayNumber / NscTracer.RaysPerChunk;
                var part = NscDetectorSet.For(objects, run.AirMicrometers)!;
                for (; k < rays.Count && rays[k].Ray.RayNumber / NscTracer.RaysPerChunk == chunk; k++)
                {
                    var (ray, keep) = rays[k];
                    for (int s = 0; s < ray.Segments.Count; s++)
                    {
                        if (!keep[s] || ray.Segments[s].DetectorHits is not { } hits) continue;
                        foreach (var hit in hits)
                        {
                            int o = hit.ObjectNumber;
                            if (o < 1 || o > objects.Count || objects[o - 1].Geometry is not INscDetector)
                                throw new InvalidDataException($"Playback ZRD on detector: run {r + 1}, ray {ray.RayNumber}, segment {s} recorded a hit on object {o}, which is not a detector in this scene; play a database back onto the system that traced it.");
                            if (detector == 0 || o == detector) part.Record(o - 1, hit);
                        }
                    }
                    part.EndRay();
                }
                all.Add(part);
            }
            var built = all.Build(run.LaunchedRays);
            total ??= new Dictionary<int, NscDetectorData>();
            foreach (var d in built)
            {
                if (detector != 0 && d.ObjectNumber != detector) continue;
                if (total.TryGetValue(d.ObjectNumber, out var t)) total[d.ObjectNumber] = t.Combine(d);
                else { total[d.ObjectNumber] = d; order.Add(d.ObjectNumber); }
            }
        }
        if (total is null) return Array.Empty<NscDetectorData>();
        return order.Select(o => total[o]).ToArray();
    }
}

/// <summary>The Detector Viewer's playback path (its "Playback ZRD" setting, OP-83): the saved
/// rays replayed onto the chosen detector of the project's scene and shown by
/// <see cref="NscDetectorViewer.Render"/>.</summary>
public static class NscRayPlaybackViewer
{
    public static Analysis.OpticsAnalysisResult Show(Analysis.OpticsAnalysisContext context, NscDetectorViewerSettings settings,
        string name, LensUnit unit, IReadOnlyList<string> notes)
    {
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException($"{name}: playback needs the system's detector objects, and the system has no non-sequential objects.");
        var detectors = Enumerable.Range(1, scene.Objects.Count).Where(i => scene.Objects[i - 1].Geometry is INscDetector).ToArray();
        if (detectors.Length == 0)
            throw new ArgumentException($"{name}: the scene has no detector objects to play the rays back onto.");
        int number = settings.Detector == 0 ? detectors[0] : settings.Detector;
        if (!detectors.Contains(number))
            throw new ArgumentException($"{name}: object {settings.Detector} is not a detector; the detectors are object{(detectors.Length == 1 ? "" : "s")} {string.Join(", ", detectors)}.");
        var db = NscZrdFile.Read(settings.PlaybackZrd, unit);
        var filter = NscRayDatabaseProject.Filter(context, settings.PlaybackFilter);
        var frame = filter?.NeedsFrame == true ? NscRayFilterFrame.For(scene, unit) : null;
        var data = Assert(NscRayPlayback.Play(db, scene, filter, number, frame), number, name);
        var shown = NscDetectorViewer.Render(data, settings, unit, name);
        var extra = new List<string>
        {
            $"Detector object {number}: the rays of {Path.GetFileName(settings.PlaybackZrd)} played back ({db.Rays.Count} ray trees, {db.Runs.Count} run{(db.Runs.Count == 1 ? "" : "s")}){(filter is null ? "" : $", filter \"{filter.Expanded}\"")}; nothing is traced.",
        };
        return Analysis.OpticsAnalysisResult.FromSections(name, shown.Sections,
            shown.Assumptions.Concat(notes).Concat(extra).ToArray(), shown.Plots);
    }

    private static NscDetectorData Assert(IReadOnlyList<NscDetectorData> data, int number, string name) =>
        data.FirstOrDefault(d => d.ObjectNumber == number)
        ?? throw new InvalidOperationException($"{name}: the playback returned no data for detector object {number}.");
}
