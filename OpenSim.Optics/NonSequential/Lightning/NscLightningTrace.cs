// OP-85
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.NonSequential.Volume;   // wave 17
using OpenSim.Optics.Photometry;

namespace OpenSim.Optics.NonSequential.Lightning;

/// <summary>The LightningTrace control's inputs (help, "Lightning Trace"): Ray Sampling and Edge
/// Sampling, each 0 = "Low (1X)" to 5 = "1024X".</summary>
public sealed record NscLightningSettings
{
    /// <summary>Chart cells per side at Ray Sampling 0 ("Low (1X)"). The help gives no size; 32 is
    /// this program's own choice (1,089 mesh rays per source).</summary>
    public const int BaseCells = 32;

    /// <summary>The highest sampling level (1024X).</summary>
    public const int MaxLevel = 5;

    /// <summary>"Sampling used to define the initial mesh for ray launch": level k has
    /// <see cref="BaseCells"/>·2^k cells per side ("as the mesh size doubles, the number of mesh
    /// rays quadruples").</summary>
    public int RaySampling { get; init; }

    /// <summary>"Sampling used to define the accuracy with which the mesh resolves the edges of
    /// objects": level k lets a cell whose corner rays disagree be halved up to k times.</summary>
    public int EdgeSampling { get; init; }

    /// <summary>The thread limit; null for none. The result is the same to the bit at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    public int CellsPerSide => BaseCells << RaySampling;

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (RaySampling < 0 || RaySampling > MaxLevel) p.Add($"Ray Sampling must be 0 (Low, 1X) to {MaxLevel} (1024X); it is {RaySampling}.");
        if (EdgeSampling < 0 || EdgeSampling > MaxLevel) p.Add($"Edge Sampling must be 0 (Low, 1X) to {MaxLevel} (1024X); it is {EdgeSampling}.");
        if (MaxDegreeOfParallelism is int t && t < 1) p.Add($"The thread limit must be at least 1; it is {t}.");
        return p;
    }
}

/// <summary>One source's part of a LightningTrace: its power in the traced unit (every array
/// element), the one wavelength its mesh rays were traced at and how its far field was charted.</summary>
public sealed record NscLightningSourceSummary(int ObjectNumber, string TypeName, int Elements, double Power, double WavelengthMicrometers, string Chart);

/// <summary>
/// The outcome of a LightningTrace (docs/Optics-Conventions.md §79): every Detector Rectangle and
/// Detector Color's data in object order (spatial only; the noise sums NaN, as the result has no
/// Monte Carlo noise), and where the emitted power went.
/// </summary>
public sealed record NscLightningResult
{
    public required IReadOnlyList<NscDetectorData> Detectors { get; init; }
    public required IReadOnlyList<NscLightningSourceSummary> Sources { get; init; }

    /// <summary>"W" or "J" (lumens are traced as watts, as OP-77's emitter does).</summary>
    public required string Unit { get; init; }

    /// <summary>Mesh rays traced (corner rays of the base mesh and of every refinement).</summary>
    public long Rays { get; init; }
    public int CellsPerSide { get; init; }
    public int EdgeDepth { get; init; }
    /// <summary>Cells halved because their corner rays disagreed or their image folded.</summary>
    public long RefinedCells { get; init; }
    /// <summary>Cells at the finest level whose image still folds (a caustic inside the cell); their
    /// power is spread over the folded image all the same.</summary>
    public long FoldedCells { get; init; }

    /// <summary>The sources' power (every element).</summary>
    public double Emitted { get; init; }
    /// <summary>Power of cells whose four corner rays agree and reach no detector.</summary>
    public double Missed { get; init; }
    /// <summary>Power of cells at the finest level whose corner rays still disagree: each corner
    /// ray carries a quarter of it to where it lands (or nowhere).</summary>
    public double Unresolved { get; init; }
    /// <summary>The part of <see cref="Unresolved"/> carried by corner rays with a trace error.</summary>
    public double Errors { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// LightningTrace (plan item OP-85; docs/Optics-Conventions.md §79; help, "Lightning Trace"): a
/// noise-free irradiance estimate by deterministic ray mapping. OpticStudio's method ("advanced ray
/// interpolation technology (patent pending)") is unpublished; this is the published flux-tube
/// method: the source's far field is cut into chart cells of known power
/// (<see cref="NscLightningChart"/>), the corner rays of every cell are traced unsplit from the
/// source's centre with <see cref="NscTracer.TraceSingle"/>, and when the four corners follow the
/// same path the cell's power is spread uniformly over the image of the cell on each detector it
/// reaches, the two triangles of the cell taking power in proportion to their image areas (a
/// piecewise-linear map, so the irradiance is the cell power over its mapped area: the Jacobian).
/// <list type="bullet">
/// <item><b>Edges.</b> A cell whose corner rays follow different paths (an object's or a
/// detector's edge runs through it), or whose image folds (the two triangles' images have opposite
/// orientation: a caustic), is halved in both directions up to Edge Sampling times. At the finest
/// level a still-disagreeing cell gives each corner ray a quarter of its power, deposited at the
/// pixel where that ray lands: <see cref="NscLightningResult.Unresolved"/> reports that power, so
/// the error it can carry is stated, never hidden.</item>
/// <item><b>Physics</b> (help): mesh rays "may only follow the specular ray path ... splitting and
/// scattering of these rays is not supported. Polarization effects are also not accounted for";
/// "an average wavelength for the source is used to trace the mesh rays" while "the overall color
/// (i.e. Tristimulus values) of each source will be retained". So ray splitting and Scatter NSC
/// Rays are refused by name, the trace runs with polarization (and energy accounting) off, each
/// source is traced at its weight-averaged wavelength, and its lumens and tristimulus values per
/// watt are the weight averages over its spectrum.</item>
/// <item><b>Detectors</b>: Detector Rectangle and Detector Color, spatial data only ("only the
/// spatial data will be available when viewing the results on a Detector Rectangle or Detector
/// Color object"). Mesh rays reaching a Detector Polar are refused (its angular mapping is not
/// built).</item>
/// <item><b>Determinism.</b> The mesh is cut into fixed bands of <see cref="RowsPerChunk"/> rows,
/// traced in parallel and deposited in band order, so the result is the same to the bit at any
/// thread count.</item>
/// </list>
/// Sources: the help page "Lightning Trace" (plans/sources/optics/op-85-help/). The flux-tube idea
/// is the textbook ray-mapping irradiance estimate; see §79 for the literature this build could and
/// could not consult.
/// </summary>
public static class NscLightningTrace
{
    /// <summary>Mesh rows per parallel work item.</summary>
    public const int RowsPerChunk = 4;

    private const int ChunksPerWave = 64;
    private const NscEvents PathEvents = NscEvents.Reflected | NscEvents.Transmitted | NscEvents.TotalInternalReflection | NscEvents.Terminated;

    /// <summary>
    /// Traces the mesh rays of <paramref name="sources"/> (1-based; null for every source in the
    /// scene) into the scene's flat detectors. Throws <see cref="NotSupportedException"/> for ray
    /// splitting, Scatter NSC Rays, a source LightningTrace refuses or a Detector Polar reached,
    /// and <see cref="ArgumentException"/> for bad settings or a bad source, each naming it.
    /// </summary>
    public static NscLightningResult Run(NscScene scene, NscTraceSettings trace, NscSourceEmitOptions options,
        NscLightningSettings settings, IReadOnlyList<int>? sources = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Validate() is { Count: > 0 } bad) throw new ArgumentException("LightningTrace refused: " + string.Join(" ", bad));
        if (trace.Splitting != NscSplitting.None)
            throw new NotSupportedException("LightningTrace refuses ray splitting: \"Mesh rays may only follow the specular ray path, however; splitting and scattering of these rays is not supported\" (help, \"Lightning Trace\"). Trace with splitting off, or use the Monte Carlo ray trace.");
        if (trace.ScatterRays)
            throw new NotSupportedException("LightningTrace refuses Scatter NSC Rays: \"splitting and scattering of these rays is not supported\" (help, \"Lightning Trace\"). Use the Monte Carlo ray trace for scattering systems.");

        var notes = new List<string>();
        if (trace.UsePolarization)
            notes.Add("Polarization (and with it Fresnel and coating losses and bulk absorption) is off for the mesh rays: \"Polarization effects are also not accounted for in tracing the mesh rays\" (help).");
        var traceSettings = trace with { UsePolarization = false, Splitting = NscSplitting.None, ScatterRays = false };
        // Wave 17 join (OP-81): the tracer checks wavelength shifts against the system wavelengths
        // even though mesh rays never undergo volume events (launch rays carry Bulk Scatter Never).
        traceSettings = traceSettings with { SystemWavelengths = traceSettings.SystemWavelengths ?? options.SystemWavelengths.Select(l => l.Micrometers).ToArray() };
        if (settings.MaxDegreeOfParallelism is int dop) traceSettings = traceSettings with { MaxDegreeOfParallelism = dop };

        var placements = scene.GlobalPlacements();
        var numbers = sources?.ToList() ?? Enumerable.Range(1, scene.Objects.Count).Where(i => scene.Objects[i - 1].Geometry is NscSource).ToList();
        if (numbers.Count == 0) throw new ArgumentException("LightningTrace refused: the scene has no source objects.");
        numbers.Sort();
        if (numbers.Distinct().Count() != numbers.Count) throw new ArgumentException("LightningTrace refused: a source object is listed twice.");
        foreach (int number in numbers)
            if (number < 1 || number > scene.Objects.Count || scene.Objects[number - 1].Geometry is not NscSource)
                throw new ArgumentException($"LightningTrace refused: object {number} is not a source object.");

        // Detector grids, by 0-based object index.
        var grids = new Dictionary<int, Grid>();
        for (int i = 0; i < scene.Objects.Count; i++)
            if (scene.Objects[i].Geometry is NscFlatDetector f) grids[i] = new Grid(f);
        if (scene.Objects.Any(o => o.FaceProperties?.Values.Any(face => face.Scatter is not null) == true))
            notes.Add("Some faces scatter; mesh rays follow the specular path only, so the result leaves the scattered light out (\"LightningTrace will not be accurate for systems containing diffusers\"; help).");
        // Wave 17 join (OP-81): volume scattering and photoluminescence are scattering too; the help's
        // "Mesh rays may only follow the specular ray path" means mesh rays pass volumes unscattered.
        if (scene.Objects.Select((o, i) => (o, i)).Where(x => x.o.VolumePhysics is { Model: not NscVolumeModel.None }).Select(x => $"{x.i + 1} ({x.o.Geometry.TypeName})").ToList() is { Count: > 0 } volumes)
            notes.Add($"Object{(volumes.Count > 1 ? "s" : "")} {string.Join(", ", volumes)} {(volumes.Count > 1 ? "have" : "has")} volume physics; mesh rays are launched with Bulk Scatter Never and follow the specular path only (\"splitting and scattering of these rays is not supported\"; help), so the result leaves volume scattering, absorption by volume events and wavelength conversion out.");

        var tracers = new Dictionary<double, NscTracer>();
        var summaries = new List<NscLightningSourceSummary>();
        var totals = new Totals();
        int n = settings.CellsPerSide;
        double referenceMicrometers = double.NaN;
        foreach (int number in numbers)
        {
            var o = scene.Objects[number - 1];
            var source = (NscSource)o.Geometry;
            string where = $"Object {number} ({source.TypeName})";
            var problems = new List<string>();
            source.Common.Validate(where, problems);
            source.Properties.Validate(where, problems);
            if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));
            var chart = NscLightningChart.For(source, where);
            var lines = source.Properties.Spectrum.Lines(options.SystemWavelengths, source.Common.Wavenumber, traceSettings.Environment.VacuumMicrometers, where);
            var spectral = Spectral(lines, options, where, notes, source.Common.Power, out double elementPower, out double meanMicrometers);
            if (double.IsNaN(referenceMicrometers)) referenceMicrometers = meanMicrometers;
            if (!tracers.TryGetValue(meanMicrometers, out var tracer))
            {
                try { tracer = new NscTracer(scene, meanMicrometers, traceSettings); }
                catch (InvalidOperationException e) { throw new InvalidOperationException($"LightningTrace, {where} at its average wavelength {F(meanMicrometers)} µm: {e.Message}", e); }
                tracers[meanMicrometers] = tracer;
            }
            var offsets = source.Properties.Array.Offsets(where);
            CheckSolidAngle(chart, where);
            var element = new Element
            {
                Chart = chart, Tracer = tracer, Transform = placements[number - 1], InsideOf = NscScene.Resolve(o.InsideOf, number),
                PrePropagation = source.Properties.PrePropagation, Reverse = source.Properties.ReverseRays, Power = elementPower,
                Factors = spectral, Grids = grids, Scene = scene, Where = where, Cells = n, Depth = settings.EdgeSampling,
            };
            foreach (var (ox, oy) in offsets)
            {
                element.Offset = new Vector3D(ox, oy, 0);
                RunElement(element, totals, settings.MaxDegreeOfParallelism, cancellation);
            }
            summaries.Add(new NscLightningSourceSummary(number, source.TypeName, offsets.Count, elementPower * offsets.Count, meanMicrometers, chart.Description));
            if (source.Properties.Polarization is { Random: false })
                notes.Add($"{where}: its Jones vector is not used (polarization is not traced for mesh rays).");
        }

        var data = new List<NscDetectorData>();
        foreach (var (index, grid) in grids.OrderBy(g => g.Key))
            data.Add(grid.Build(index + 1, referenceMicrometers, totals.Rays));
        notes.Add($"LightningTrace: {n} × {n} chart cells per source element (Ray Sampling {settings.RaySampling}), up to {settings.EdgeSampling} halvings at edges and folds (Edge Sampling {settings.EdgeSampling}); {totals.Rays} mesh rays traced from each source's centre; {totals.Refined} cells refined, {totals.Folded} folded cells left at the finest level.");
        notes.Add("Each source is represented by its far field from its centre: its size and spatial distribution are neglected (help, \"Lightning Trace\"). Detector data are spatial only and carry no noise estimate.");
        return new NscLightningResult
        {
            Detectors = data, Sources = summaries, Unit = options.Units == SourceUnit.Joules ? "J" : "W",
            Rays = totals.Rays, CellsPerSide = n, EdgeDepth = settings.EdgeSampling, RefinedCells = totals.Refined, FoldedCells = totals.Folded,
            Emitted = totals.Emitted.Total, Missed = totals.Missed.Total, Unresolved = totals.Unresolved.Total, Errors = totals.Errors.Total,
            Notes = notes,
        };
    }

    // ================================================================ spectrum and power

    /// <summary>The source's per-watt factors (lumens, X, Y, Z: weight averages over its lines),
    /// its element power in the traced unit (lumens to watts as OP-77's emitter converts them) and
    /// its weight-averaged wavelength.</summary>
    private static Factors Spectral(IReadOnlyList<NscSpectralLine> lines, NscSourceEmitOptions options, string where, List<string> notes,
        double statedPower, out double elementPower, out double meanMicrometers)
    {
        NscLedgerBuilder.Sum w = default, wl = default, lm = default, x = default, y = default, z = default;
        foreach (var l in lines)
        {
            w.Add(l.Weight);
            wl.Add(l.Weight * l.Micrometers);
            lm.Add(l.Weight * Photometric.LumensPerWatt(l.Micrometers));
            var t = Photometric.TristimulusPerWatt(l.Micrometers);
            x.Add(l.Weight * t.X); y.Add(l.Weight * t.Y); z.Add(l.Weight * t.Z);
        }
        double total = w.Total;
        if (!(total > 0)) throw new ArgumentException($"{where}: its spectrum has no weight.");
        meanMicrometers = wl.Total / total;
        elementPower = statedPower;
        if (options.Units == SourceUnit.Lumens)
        {
            var v = options.LuminousEfficiency
                ?? throw new NotSupportedException($"{where}: the source units are lumens, which need the CIE photopic luminous efficiency V(λ), and these emission options carry none.");
            NscLedgerBuilder.Sum weighted = default;
            foreach (var l in lines) weighted.Add(l.Weight * v(l.Micrometers));
            double efficacy = NscSourceEmitter.LuminousEfficacy * weighted.Total / total;
            if (!(efficacy > 0)) throw new ArgumentException($"{where}: the source is stated in lumens but its spectrum has no visible light.");
            elementPower = statedPower / efficacy;
            notes.Add($"{where}: {F(statedPower)} lm per element traced as {F(elementPower)} W (spectral luminous efficacy {F(efficacy)} lm/W).");
        }
        if (lines.Count > 1)
            notes.Add($"{where}: {lines.Count} wavelengths; mesh rays traced at their weighted average {F(meanMicrometers)} µm, colour kept as the spectrum's average tristimulus values per watt.");
        return new Factors(lm.Total / total, x.Total / total, y.Total / total, z.Total / total);
    }

    /// <summary>Refuses a chart whose directions span no solid angle (a beam collimated in one or
    /// both directions): the mapped areas would be zero.</summary>
    private static void CheckSolidAngle(NscLightningChart chart, string where)
    {
        const int k = 16;
        var d = new Vector3D[k + 1, k + 1];
        for (int i = 0; i <= k; i++)
            for (int j = 0; j <= k; j++) d[i, j] = chart.Direction((double)i / k, (double)j / k);
        double omega = 0;
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
                omega += 0.5 * (Vector3D.Cross(d[i + 1, j] - d[i, j], d[i + 1, j + 1] - d[i, j]).Length
                    + Vector3D.Cross(d[i + 1, j + 1] - d[i, j], d[i, j + 1] - d[i, j]).Length);
        if (!(omega > 1e-9))
            throw new NotSupportedException($"{where}: LightningTrace refuses this source: its far field spans no solid angle (collimated in at least one direction), and \"LightningTrace cannot be used to model perfectly collimated beams\" (help, \"Lightning Trace\").");
    }

    // ================================================================ the mesh

    private readonly record struct Factors(double Lumens, double X, double Y, double Z);

    private readonly record struct Landing(int Object, double X, double Y, double Power);

    private sealed class Node
    {
        public bool Error;
        public int[] Path = Array.Empty<int>();
        public Landing[] Landings = Array.Empty<Landing>();
    }

    private sealed class Element
    {
        public required NscLightningChart Chart;
        public required NscTracer Tracer;
        public required RigidTransform Transform;
        public required int InsideOf;
        public required double PrePropagation;
        public required bool Reverse;
        public required double Power;
        public required Factors Factors;
        public required Dictionary<int, Grid> Grids;
        public required NscScene Scene;
        public required string Where;
        public required int Cells;
        public required int Depth;
        public Vector3D Offset;
    }

    /// <summary>A triangle (or, with <see cref="IsPoint"/>, a point at X0, Y0) of power on a detector.</summary>
    private readonly record struct Deposit(int Object, double X0, double Y0, double X1, double Y1, double X2, double Y2, double Power, bool IsPoint);

    private sealed class Chunk
    {
        public readonly List<Deposit> Deposits = new();
        public readonly Dictionary<int, long> Hits = new();
        public long Rays, Refined, Folded;
        public NscLedgerBuilder.Sum Emitted, Missed, Unresolved, Errors;
    }

    private sealed class Totals
    {
        public long Rays, Refined, Folded;
        public NscLedgerBuilder.Sum Emitted, Missed, Unresolved, Errors;
    }

    private static void RunElement(Element e, Totals totals, int? dop, CancellationToken cancellation)
    {
        int n = e.Cells;
        int chunks = (n + RowsPerChunk - 1) / RowsPerChunk;
        for (int wave = 0; wave < chunks; wave += ChunksPerWave)
        {
            int count = Math.Min(ChunksPerWave, chunks - wave);
            int first = wave;
            var parts = ParallelSlots.Compute(count, dop, c => Band(e, first + c, cancellation), cancellation);
            foreach (var part in parts)   // in band order: the same sums at any thread count
            {
                foreach (var d in part.Deposits) e.Grids[d.Object].Add(d, e.Factors);
                foreach (var (obj, hits) in part.Hits.OrderBy(h => h.Key)) e.Grids[obj].Hits += hits;
                totals.Rays += part.Rays; totals.Refined += part.Refined; totals.Folded += part.Folded;
                totals.Emitted.Add(part.Emitted); totals.Missed.Add(part.Missed); totals.Unresolved.Add(part.Unresolved); totals.Errors.Add(part.Errors);
            }
        }
    }

    private static Chunk Band(Element e, int band, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int n = e.Cells;
        int i0 = band * RowsPerChunk, i1 = Math.Min(n, i0 + RowsPerChunk);
        var chunk = new Chunk();
        var nodes = new Node[i1 - i0 + 1, n + 1];
        for (int i = i0; i <= i1; i++)
            for (int j = 0; j <= n; j++)
                nodes[i - i0, j] = Trace(e, (double)i / n, (double)j / n, chunk);
        for (int i = i0; i < i1; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int j = 0; j < n; j++)
                Cell(e, chunk, (double)i / n, (double)(i + 1) / n, (double)j / n, (double)(j + 1) / n,
                    nodes[i - i0, j], nodes[i + 1 - i0, j], nodes[i - i0, j + 1], nodes[i + 1 - i0, j + 1], 0, top: true);
        }
        return chunk;
    }

    private static Node Trace(Element e, double s, double b, Chunk chunk)
    {
        var d = e.Chart.Direction(s, b);
        var p = e.Chart.Center + e.Offset;
        if (e.PrePropagation != 0) p += d * e.PrePropagation;
        if (e.Reverse) d = -d;
        var launch = new NscLaunchRay { Position = e.Transform.Apply(p), Direction = e.Transform.ApplyDirection(d), Intensity = 1, InsideOf = e.InsideOf, BulkScatter = NscSourceBulkScatter.Never, FixedWavelength = true };   // wave 17: deterministic, no volume events
        var tree = e.Tracer.TraceSingle(launch);
        chunk.Rays++;
        var node = new Node { Error = tree.Errors.Count > 0 };
        var path = new List<int>();
        var landings = new List<Landing>();
        for (int k = 1; k < tree.Segments.Count; k++)
        {
            var seg = tree.Segments[k];
            path.Add(seg.Hit); path.Add(seg.Face); path.Add((int)(seg.Events & PathEvents));
            if (seg.DetectorHits is null) continue;
            foreach (var h in seg.DetectorHits)
            {
                int index = h.ObjectNumber - 1;
                if (!e.Grids.ContainsKey(index))
                    throw new NotSupportedException($"LightningTrace, {e.Where}: mesh rays reach object {h.ObjectNumber} ({e.Scene.Objects[index].Geometry.TypeName}); LightningTrace onto a Detector Polar is not built (its angular mapping of the mesh), only onto Detector Rectangle and Detector Color.");
                landings.Add(new Landing(index, h.LocalPoint.X, h.LocalPoint.Y, h.Power));
            }
        }
        node.Path = path.ToArray();
        node.Landings = landings.ToArray();
        foreach (var l in node.Landings)
            chunk.Hits[l.Object] = chunk.Hits.GetValueOrDefault(l.Object) + 1;
        return node;
    }

    private static bool Same(Node a, Node b)
    {
        if (a.Error || b.Error) return false;
        if (!a.Path.AsSpan().SequenceEqual(b.Path)) return false;
        if (a.Landings.Length != b.Landings.Length) return false;
        for (int k = 0; k < a.Landings.Length; k++)
            if (a.Landings[k].Object != b.Landings[k].Object) return false;
        return true;
    }

    private static double Signed(Landing a, Landing b, Landing c) =>
        0.5 * ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));

    private static void Cell(Element e, Chunk chunk, double s0, double s1, double b0, double b1,
        Node n00, Node n10, Node n01, Node n11, int depth, bool top)
    {
        double power = e.Power * e.Chart.Weight(s0, s1) * (b1 - b0);
        if (top) chunk.Emitted.Add(power);
        bool agree = Same(n00, n10) && Same(n00, n01) && Same(n00, n11);
        bool folded = false;
        if (agree)
            for (int k = 0; k < n00.Landings.Length && !folded; k++)
                folded = Signed(n00.Landings[k], n10.Landings[k], n11.Landings[k]) * Signed(n00.Landings[k], n11.Landings[k], n01.Landings[k]) < 0;
        if ((!agree || folded) && depth < e.Depth)
        {
            chunk.Refined++;
            double sm = 0.5 * (s0 + s1), bm = 0.5 * (b0 + b1);
            var m0 = Trace(e, sm, b0, chunk);   // (sm, b0)
            var c0 = Trace(e, s0, bm, chunk);   // (s0, bm)
            var mm = Trace(e, sm, bm, chunk);
            var c1 = Trace(e, s1, bm, chunk);   // (s1, bm)
            var m1 = Trace(e, sm, b1, chunk);   // (sm, b1)
            Cell(e, chunk, s0, sm, b0, bm, n00, m0, c0, mm, depth + 1, false);
            Cell(e, chunk, sm, s1, b0, bm, m0, n10, mm, c1, depth + 1, false);
            Cell(e, chunk, s0, sm, bm, b1, c0, mm, n01, m1, depth + 1, false);
            Cell(e, chunk, sm, s1, bm, b1, mm, c1, m1, n11, depth + 1, false);
            return;
        }
        if (!agree)
        {
            // The finest level and the corners still disagree: each corner ray carries a quarter.
            chunk.Unresolved.Add(power);
            foreach (var node in new[] { n00, n10, n01, n11 })
            {
                if (node.Error) { chunk.Errors.Add(0.25 * power); continue; }
                foreach (var l in node.Landings)
                    chunk.Deposits.Add(new Deposit(l.Object, l.X, l.Y, 0, 0, 0, 0, 0.25 * power * l.Power, true));
            }
            return;
        }
        if (n00.Landings.Length == 0) { chunk.Missed.Add(power); return; }
        if (folded) chunk.Folded++;
        for (int k = 0; k < n00.Landings.Length; k++)
        {
            Landing a = n00.Landings[k], b = n10.Landings[k], c = n01.Landings[k], d = n11.Landings[k];
            double t1 = Math.Abs(Signed(a, b, d)), t2 = Math.Abs(Signed(a, d, c)), area = t1 + t2;
            if (!(area > 0))
            {
                double x = 0.25 * (a.X + b.X + c.X + d.X), y = 0.25 * (a.Y + b.Y + c.Y + d.Y);
                chunk.Deposits.Add(new Deposit(a.Object, x, y, 0, 0, 0, 0, power * 0.25 * (a.Power + b.Power + c.Power + d.Power), true));
                continue;
            }
            chunk.Deposits.Add(new Deposit(a.Object, a.X, a.Y, b.X, b.Y, d.X, d.Y, power * t1 / area * (a.Power + b.Power + d.Power) / 3, false));
            chunk.Deposits.Add(new Deposit(a.Object, a.X, a.Y, d.X, d.Y, c.X, c.Y, power * t2 / area * (a.Power + d.Power + c.Power) / 3, false));
        }
    }

    // ================================================================ detector grids

    /// <summary>A flat detector's pixel sums: each deposited triangle shares its power among the
    /// pixels by the exact area of the triangle inside each (Sutherland–Hodgman clipping to the
    /// pixel, shoelace area); the mirroring flag's copies as the detector's own accumulator makes them.</summary>
    private sealed class Grid
    {
        private readonly NscFlatDetector _d;
        private readonly double _dx, _dy;
        private readonly (int Sx, int Sy, double W)[] _copies;
        public readonly double[] Flux, Luminous;
        public readonly double[]? X, Y, Z;
        public long Hits;

        public Grid(NscFlatDetector d)
        {
            _d = d;
            int n = d.PixelsX * d.PixelsY;
            Flux = new double[n];
            Luminous = new double[n];
            if (d is NscDetectorColor) { X = new double[n]; Y = new double[n]; Z = new double[n]; }
            _dx = 2 * d.HalfWidthX / d.PixelsX;
            _dy = 2 * d.HalfWidthY / d.PixelsY;
            _copies = NscFlatDetector.MirrorCopies(d.Mirroring);
        }

        private void Pixel(int k, double w, Factors f)
        {
            Flux[k] += w;
            Luminous[k] += w * f.Lumens;
            if (X is not null) { X[k] += w * f.X; Y![k] += w * f.Y; Z![k] += w * f.Z; }
        }

        private int Ix(double x) => Math.Clamp((int)Math.Floor((x + _d.HalfWidthX) / _dx), 0, _d.PixelsX - 1);
        private int Iy(double y) => Math.Clamp((int)Math.Floor((y + _d.HalfWidthY) / _dy), 0, _d.PixelsY - 1);

        public void Add(in Deposit dep, Factors f)
        {
            foreach (var (sx, sy, wf) in _copies)
            {
                double w = dep.Power * wf;
                if (w == 0) continue;
                if (dep.IsPoint) { Pixel(Iy(sy * dep.Y0) * _d.PixelsX + Ix(sx * dep.X0), w, f); continue; }
                Triangle(sx * dep.X0, sy * dep.Y0, sx * dep.X1, sy * dep.Y1, sx * dep.X2, sy * dep.Y2, w, f);
            }
        }

        private void Triangle(double x0, double y0, double x1, double y1, double x2, double y2, double w, Factors f)
        {
            int ix0 = Ix(Math.Min(x0, Math.Min(x1, x2))), ix1 = Ix(Math.Max(x0, Math.Max(x1, x2)));
            int iy0 = Iy(Math.Min(y0, Math.Min(y1, y2))), iy1 = Iy(Math.Max(y0, Math.Max(y1, y2)));
            if (ix0 == ix1 && iy0 == iy1) { Pixel(iy0 * _d.PixelsX + ix0, w, f); return; }
            double area = 0.5 * Math.Abs((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0));
            if (!(area > 1e-12 * _dx * _dy))
            {
                Pixel(Iy((y0 + y1 + y2) / 3) * _d.PixelsX + Ix((x0 + x1 + x2) / 3), w, f);
                return;
            }
            Span<double> px = stackalloc double[16], py = stackalloc double[16];
            for (int iy = iy0; iy <= iy1; iy++)
            {
                double yLo = -_d.HalfWidthY + iy * _dy, yHi = yLo + _dy;
                if (iy == 0) yLo = double.NegativeInfinity;                 // the rounding margin of a landing on the edge
                if (iy == _d.PixelsY - 1) yHi = double.PositiveInfinity;
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    double xLo = -_d.HalfWidthX + ix * _dx, xHi = xLo + _dx;
                    if (ix == 0) xLo = double.NegativeInfinity;
                    if (ix == _d.PixelsX - 1) xHi = double.PositiveInfinity;
                    px[0] = x0; py[0] = y0; px[1] = x1; py[1] = y1; px[2] = x2; py[2] = y2;
                    int m = 3;
                    m = Clip(px, py, m, 0, xLo); m = Clip(px, py, m, 1, xHi);
                    m = Clip(px, py, m, 2, yLo); m = Clip(px, py, m, 3, yHi);
                    if (m < 3) continue;
                    double a = 0;
                    for (int k = 0; k < m; k++)
                    {
                        int l = k + 1 == m ? 0 : k + 1;
                        a += px[k] * py[l] - px[l] * py[k];
                    }
                    a = 0.5 * Math.Abs(a);
                    if (a > 0) Pixel(iy * _d.PixelsX + ix, w * a / area, f);
                }
            }
        }

        /// <summary>Clips the polygon (px, py)[0..m) to one half-plane (Sutherland–Hodgman):
        /// side 0 x ≥ v, 1 x ≤ v, 2 y ≥ v, 3 y ≤ v. Returns the new vertex count.</summary>
        private static int Clip(Span<double> px, Span<double> py, int m, int side, double v)
        {
            if (m == 0 || double.IsInfinity(v)) return m;
            Span<double> qx = stackalloc double[16], qy = stackalloc double[16];
            int r = 0;
            for (int k = 0; k < m; k++)
            {
                int l = k + 1 == m ? 0 : k + 1;
                double ak = side < 2 ? px[k] : py[k], al = side < 2 ? px[l] : py[l];
                bool ink = side % 2 == 0 ? ak >= v : ak <= v;
                bool inl = side % 2 == 0 ? al >= v : al <= v;
                if (ink) { qx[r] = px[k]; qy[r] = py[k]; r++; }
                if (ink != inl)
                {
                    double t = (v - ak) / (al - ak);
                    qx[r] = px[k] + t * (px[l] - px[k]);
                    qy[r] = py[k] + t * (py[l] - py[k]);
                    if (side < 2) qx[r] = v; else qy[r] = v;
                    r++;
                }
            }
            for (int k = 0; k < r; k++) { px[k] = qx[k]; py[k] = qy[k]; }
            return r;
        }

        public NscDetectorData Build(int objectNumber, double airMicrometers, long rays)
        {
            var empty = (NscFlatDetectorData)_d.CreateAccumulator(objectNumber, airMicrometers).Build(0);
            int n = Flux.Length;
            double[] Nan() { var a = new double[n]; Array.Fill(a, double.NaN); return a; }
            return empty with
            {
                LaunchedRays = rays,
                Flux = (double[])Flux.Clone(), FluxSquares = Nan(),
                AngleFlux = new double[n], AngleFluxSquares = Nan(),
                Luminous = (double[])Luminous.Clone(), AngleLuminous = new double[n],
                CoherentReal = null, CoherentImaginary = null, CoherentAmplitude = null,
                X = (double[]?)X?.Clone(), Y = (double[]?)Y?.Clone(), Z = (double[]?)Z?.Clone(),
                AngleX = X is null ? null : new double[n], AngleY = X is null ? null : new double[n], AngleZ = X is null ? null : new double[n],
                SpatialHits = Hits, AngularHits = 0,
                Spectrum = null,
            };
        }
    }

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
