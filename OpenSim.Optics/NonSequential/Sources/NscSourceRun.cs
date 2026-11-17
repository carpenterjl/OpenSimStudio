using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential.Detectors;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>One wavelength's part of a source run: the rays launched and the tracer's result
/// (ledger, errors, notes, and that wavelength's detector data).</summary>
public sealed record NscWavelengthRun(double WavelengthMicrometers, int Rays, NscRunResult Result);

/// <summary>What one source launched: its stated power (source units, every array element
/// counted) and the power its rays carry in the traced unit.</summary>
public sealed record NscSourceSummary(int ObjectNumber, string TypeName, long Rays, double StatedPower, SourceUnit StatedUnit, double TracedPower);

/// <summary>The outcome of tracing the scene's sources: one run per wavelength, the ledger
/// merged over them, each source's summary, and notes.</summary>
public sealed record NscSourceRunResult
{
    public required IReadOnlyList<NscWavelengthRun> Runs { get; init; }

    /// <summary>Every wavelength's ledger summed (compensated, in ascending wavelength order).</summary>
    public required NscEnergyLedger Ledger { get; init; }

    public required IReadOnlyList<NscSourceSummary> Sources { get; init; }

    /// <summary>The unit the ledger is in: "W" (watts, and lumens converted to watts) or "J".</summary>
    public required string Unit { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Each detector's data summed over the wavelengths in ascending order
    /// (<see cref="NscDetectorResults.Combine"/>, OP-79), in the runs' flux unit
    /// (<see cref="Unit"/>); null when the scene has no detector objects.</summary>
    public IReadOnlyList<NscDetectorData>? Detectors { get; init; }
}

/// <summary>
/// Traces the sources of a scene (plan item OP-77; docs/Optics-Conventions.md §72), OpticStudio's
/// "Trace Begins tracing rays from all defined sources" (help, "Ray Trace"). Each source's rays
/// come from <see cref="NscSourceEmitter"/>; rays of equal wavelength from every source are
/// joined (sources in object order) and traced by one <see cref="NscTracer"/> per wavelength, as
/// the tracer works at one wavelength. Wavelength g (ascending, 0-based) runs with the seed
/// SplitMix64.ForStream(seed, −1 − g), so the random choices of simple ray splitting are
/// independent between wavelengths; the emission itself uses the settings' own seed. Each run is
/// bitwise reproducible at any thread count, and so is the merged ledger.
/// </summary>
public static class NscSourceRun
{
    /// <param name="sources">The source objects to trace (1-based); null for every source in the
    /// scene (ZPL NSTR's "If source is zero, all sources will be traced").</param>
    /// <param name="recorder">OP-83: when given, every ray tree is saved into it (the Ray Trace
    /// control's Save Rays), one database run per wavelength with each ray's source object and
    /// wavelength number (docs/Optics-Conventions.md §75).</param>
    public static NscSourceRunResult Run(NscScene scene, NscTraceSettings settings, NscSourceEmitOptions options,
        IReadOnlyList<int>? sources = null, CancellationToken cancellation = default,
        RayDatabase.NscRayRecorder? recorder = null)   // OP-83
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var placements = scene.GlobalPlacements();
        var numbers = sources?.ToList() ?? Enumerable.Range(1, scene.Objects.Count).Where(i => scene.Objects[i - 1].Geometry is NscSource).ToList();
        if (numbers.Count == 0)
            throw new ArgumentException("Source run refused: the scene has no source objects.");
        numbers.Sort();
        if (numbers.Distinct().Count() != numbers.Count)
            throw new ArgumentException("Source run refused: a source object is listed twice.");

        var notes = new List<string>();
        var summaries = new List<NscSourceSummary>();
        var byWavelength = new SortedDictionary<double, (List<NscLaunchRay> Rays, List<int> Objects, List<long> Numbers)>();
        foreach (int number in numbers)
        {
            var set = NscSourceEmitter.Emit(scene, placements, number, settings, options, cancellation);
            summaries.Add(new NscSourceSummary(set.ObjectNumber, set.TypeName, set.Rays, set.StatedPower, set.StatedUnit, set.TracedPower));
            notes.AddRange(set.Notes);
            foreach (var e in set.Emissions)
            {
                if (!byWavelength.TryGetValue(e.WavelengthMicrometers, out var group))
                    byWavelength[e.WavelengthMicrometers] = group = (new List<NscLaunchRay>(), new List<int>(), new List<long>());
                group.Rays.AddRange(e.Rays);
                group.Objects.AddRange(e.SourceObjects ?? Enumerable.Repeat(number, e.Rays.Count));
                group.Numbers.AddRange(e.SourceRayNumbers ?? Enumerable.Range(0, e.Rays.Count).Select(i => (long)i));
            }
        }

        var runs = new List<NscWavelengthRun>();
        int g = 0;
        foreach (var (wavelength, group) in byWavelength)
        {
            var traceSettings = settings with
            {
                Seed = unchecked((long)SplitMix64.ForStream(settings.Seed, -1L - g).Next()),
                SystemWavelengths = settings.SystemWavelengths ?? options.SystemWavelengths.Select(l => l.Micrometers).ToArray(),   // OP-81
            };
            string at = $"at {wavelength.ToString("g9", CultureInfo.InvariantCulture)} µm";
            NscRunResult result;
            try
            {
                var tracer = new NscTracer(scene, wavelength, traceSettings);
                recorder?.PrepareRun(group.Objects, WavelengthNumber(options, byWavelength.Keys, wavelength));   // OP-83
                result = tracer.Run(group.Rays, recorder, cancellation);   // OP-83: recorder null is the plain run
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"Source run {at}: {Locate(e.Message, group.Objects, group.Numbers)}", e);
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Source run {at}: {Locate(e.Message, group.Objects, group.Numbers)}", e);
            }
            runs.Add(new NscWavelengthRun(wavelength, group.Rays.Count, result));
            if (g == 0) notes.AddRange(result.Notes.Where(n => !n.StartsWith("Wavelength", StringComparison.Ordinal)));
            g++;
        }
        return new NscSourceRunResult
        {
            Runs = runs,
            Ledger = Merge(runs.Select(r => r.Result.Ledger)),
            Sources = summaries,
            Unit = options.Units == SourceUnit.Joules ? "J" : "W",
            Notes = notes,
            Detectors = NscDetectorResults.Combine(runs.Select(r => r.Result)),
        };
    }

    // OP-83
    /// <summary>A run's wavelength number for the ray database (the W filter flag): the system
    /// wavelength row (1-based) whose wavelength equals the run's exactly; otherwise the number
    /// after the system rows, counting the run wavelengths that are not system wavelengths in
    /// ascending order.</summary>
    internal static int WavelengthNumber(NscSourceEmitOptions options, IEnumerable<double> runWavelengths, double wavelength)
    {
        var system = options.SystemWavelengths;
        for (int i = 0; i < system.Count; i++)
            if (system[i].Micrometers == wavelength) return i + 1;
        int k = system.Count;
        foreach (double w in runWavelengths)
        {
            if (system.Any(l => l.Micrometers == w)) continue;
            k++;
            if (w == wavelength) return k;
        }
        return k;
    }

    /// <summary>Appends which source and source ray the tracer's "Ray i" is: the tracer numbers
    /// rays within one wavelength's list.</summary>
    private static string Locate(string message, List<int> objects, List<long> numbers)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"\bRay (\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < objects.Count)
            return $"{message} (ray {i} of this wavelength is source object {objects[i]}'s ray {numbers[i]})";
        return message;
    }

    /// <summary>The sum of several ledgers, each destination with compensated summation in the
    /// order given, per-object detected energy merged by object.</summary>
    public static NscEnergyLedger Merge(IEnumerable<NscEnergyLedger> ledgers)
    {
        NscLedgerBuilder.Sum emitted = default, detected = default, absorbed = default, escaped = default,
            thresholded = default, notTraced = default, limit = default, errors = default;
        NscLedgerBuilder.Sum scatterNotTraced = default;   // OP-80
        NscLedgerBuilder.Sum conversionLoss = default, converted = default;   // OP-81
        long volumeEvents = 0, conversionEvents = 0;                         // OP-81
        long rays = 0, segments = 0, thresholdedRays = 0, limitRays = 0, errorRays = 0;
        var byObject = new SortedDictionary<int, NscLedgerBuilder.Sum>();
        foreach (var l in ledgers)
        {
            emitted.Add(l.Emitted); detected.Add(l.Detected); absorbed.Add(l.Absorbed); escaped.Add(l.Escaped);
            thresholded.Add(l.Thresholded); notTraced.Add(l.NotTraced); limit.Add(l.LimitTerminated); errors.Add(l.Errors);
            scatterNotTraced.Add(l.ScatterNotTraced);   // OP-80
            conversionLoss.Add(l.ConversionLoss); converted.Add(l.Converted); volumeEvents += l.VolumeEvents; conversionEvents += l.ConversionEvents;   // OP-81
            rays += l.Rays; segments += l.Segments; thresholdedRays += l.ThresholdedRays; limitRays += l.LimitTerminatedRays; errorRays += l.ErrorRays;
            foreach (var (k, v) in l.DetectedByObject)
            {
                byObject.TryGetValue(k, out var s);
                s.Add(v);
                byObject[k] = s;
            }
        }
        return new NscEnergyLedger
        {
            Emitted = emitted.Total, Detected = detected.Total, Absorbed = absorbed.Total, Escaped = escaped.Total,
            Thresholded = thresholded.Total, NotTraced = notTraced.Total, LimitTerminated = limit.Total, Errors = errors.Total,
            ScatterNotTraced = scatterNotTraced.Total,   // OP-80
            ConversionLoss = conversionLoss.Total, Converted = converted.Total, VolumeEvents = volumeEvents, ConversionEvents = conversionEvents,   // OP-81
            Rays = rays, Segments = segments, ThresholdedRays = thresholdedRays, LimitTerminatedRays = limitRays, ErrorRays = errorRays,
            DetectedByObject = byObject.ToDictionary(p => p.Key, p => p.Value.Total),
        };
    }
}
