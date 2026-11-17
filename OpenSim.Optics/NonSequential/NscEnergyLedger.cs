using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// Where the emitted energy of a non-sequential trace went (docs/Optics-Conventions.md §69):
/// <c>Detected + Absorbed + Escaped + Thresholded + NotTraced + LimitTerminated + Errors =
/// Emitted</c>, to rounding (<see cref="Residual"/>). Energies are in the launched rays' units
/// (watts for the source stand-in). OpticStudio's Ray Trace control reports "lost energy"
/// due to threshold settings and due to errors, and counts rays that reach the intersection,
/// segment or nesting limits among the errors (help, "Ray Trace", "Lost energy"); here those
/// limits have their own line, <see cref="LimitTerminated"/>, and the nesting limit, being a
/// geometry problem, is an error.
/// </summary>
public sealed record NscEnergyLedger
{
    /// <summary>The sum of the launched rays' intensities.</summary>
    public double Emitted { get; init; }

    /// <summary>Absorbed by detecting faces (the OP-70 stand-in).</summary>
    public double Detected { get; init; }

    /// <summary>Absorbed by ABSORB objects and absorbing faces, by coatings and uncoated
    /// mirrors, and in the bulk of absorbing media.</summary>
    public double Absorbed { get; init; }

    /// <summary>Carried away by rays that met no object.</summary>
    public double Escaped { get; init; }

    /// <summary>Carried by child rays below the minimum relative or absolute intensity.</summary>
    public double Thresholded { get; init; }

    /// <summary>Reflected at refractive interfaces and not traced because splitting is off.</summary>
    public double NotTraced { get; init; }

    /// <summary>Carried by rays stopped at the maximum intersections or segments per ray.</summary>
    public double LimitTerminated { get; init; }

    /// <summary>Carried by rays stopped by a trace error (geometry, nesting, coating).</summary>
    public double Errors { get; init; }

    // OP-80
    /// <summary>Scattered energy not traced: scattered rays that miss every object of a Scatter
    /// To list, and importance sampling's correction (the scattered energy minus what the aimed
    /// and BSDF rays carry; one event's share may be negative, its expectation is the scattered
    /// energy outside the target cones). Zero without scattering.</summary>
    public double ScatterNotTraced { get; init; }

    public long Rays { get; init; }
    public long Segments { get; init; }
    public long ThresholdedRays { get; init; }
    public long LimitTerminatedRays { get; init; }
    public long ErrorRays { get; init; }

    /// <summary>Detected energy by 1-based object number.</summary>
    public IReadOnlyDictionary<int, double> DetectedByObject { get; init; } = new Dictionary<int, double>();

    /// <summary>The sum of every destination.</summary>
    public double Accounted => Detected + Absorbed + Escaped + Thresholded + NotTraced + LimitTerminated + Errors
        + ScatterNotTraced;   // OP-80

    /// <summary>Emitted − accounted: zero to rounding.</summary>
    public double Residual => Emitted - Accounted;

    /// <summary>Label and value rows for reports.</summary>
    public IReadOnlyList<(string Label, string Value)> Rows(string unit)
    {
        string E(double v) => v.ToString("g10", CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : "");
        string P(double v) => Emitted > 0 ? $" ({(v / Emitted * 100).ToString("g6", CultureInfo.InvariantCulture)} %)" : "";
        var rows = new List<(string, string)>
        {
            ("Rays launched", Rays.ToString(CultureInfo.InvariantCulture)),
            ("Segments traced", Segments.ToString(CultureInfo.InvariantCulture)),
            ("Emitted", E(Emitted)),
            ("Detected", E(Detected) + P(Detected)),
            ("Absorbed", E(Absorbed) + P(Absorbed)),
            ("Escaped (missed every object)", E(Escaped) + P(Escaped)),
            ($"Below the intensity thresholds ({ThresholdedRays} rays)", E(Thresholded) + P(Thresholded)),
            ("Reflected at interfaces, not traced (splitting off)", E(NotTraced) + P(NotTraced)),
            ($"Stopped at the intersection or segment limit ({LimitTerminatedRays} rays)", E(LimitTerminated) + P(LimitTerminated)),
            ($"Lost to errors ({ErrorRays} rays)", E(Errors) + P(Errors)),
            ("Emitted − accounted", E(Residual)),
        };
        // OP-80: only when something scattered and was not traced.
        if (ScatterNotTraced != 0) rows.Insert(rows.Count - 1, ("Scattered, not traced (Scatter To, importance sampling)", E(ScatterNotTraced) + P(ScatterNotTraced)));
        foreach (var (obj, energy) in DetectedByObject.OrderBy(p => p.Key))
            rows.Add(($"Detected on object {obj}", E(energy) + P(energy)));
        return rows;
    }
}

/// <summary>
/// The running sums of a ledger. Each destination is summed with Neumaier's compensated
/// summation, so the ledger closes to a few rounding units of the emitted energy however many
/// segments a run has; builders are combined in a fixed order, so a run's ledger is the same to
/// the bit at any thread count.
/// </summary>
internal sealed class NscLedgerBuilder
{
    internal struct Sum
    {
        public double Value, Compensation;

        public void Add(double x)
        {
            double t = Value + x;
            Compensation += Math.Abs(Value) >= Math.Abs(x) ? (Value - t) + x : (x - t) + Value;
            Value = t;
        }

        public void Add(Sum other)
        {
            Add(other.Value);
            Add(other.Compensation);
        }

        public readonly double Total => Value + Compensation;
    }

    public Sum Emitted, Detected, Absorbed, Escaped, Thresholded, NotTraced, LimitTerminated, Errors;
    public Sum ScatterNotTraced;   // OP-80
    public long Rays, Segments, ThresholdedRays, LimitTerminatedRays, ErrorRays;
    public readonly SortedDictionary<int, Sum> DetectedByObject = new();

    public void Detect(int objectNumber, double energy)
    {
        Detected.Add(energy);
        DetectedByObject.TryGetValue(objectNumber, out var s);
        s.Add(energy);
        DetectedByObject[objectNumber] = s;
    }

    public void Add(NscLedgerBuilder o)
    {
        Emitted.Add(o.Emitted); Detected.Add(o.Detected); Absorbed.Add(o.Absorbed); Escaped.Add(o.Escaped);
        Thresholded.Add(o.Thresholded); NotTraced.Add(o.NotTraced); LimitTerminated.Add(o.LimitTerminated); Errors.Add(o.Errors);
        ScatterNotTraced.Add(o.ScatterNotTraced);   // OP-80
        Rays += o.Rays; Segments += o.Segments; ThresholdedRays += o.ThresholdedRays;
        LimitTerminatedRays += o.LimitTerminatedRays; ErrorRays += o.ErrorRays;
        foreach (var (k, v) in o.DetectedByObject)
        {
            DetectedByObject.TryGetValue(k, out var s);
            s.Add(v);
            DetectedByObject[k] = s;
        }
    }

    public NscEnergyLedger Build() => new()
    {
        Emitted = Emitted.Total, Detected = Detected.Total, Absorbed = Absorbed.Total, Escaped = Escaped.Total,
        Thresholded = Thresholded.Total, NotTraced = NotTraced.Total, LimitTerminated = LimitTerminated.Total, Errors = Errors.Total,
        ScatterNotTraced = ScatterNotTraced.Total,   // OP-80
        Rays = Rays, Segments = Segments, ThresholdedRays = ThresholdedRays, LimitTerminatedRays = LimitTerminatedRays, ErrorRays = ErrorRays,
        DetectedByObject = DetectedByObject.ToDictionary(p => p.Key, p => p.Value.Total),
    };
}

/// <summary>What happened at the end of a segment, as OpticStudio's ray database letters it
/// (help, "Ray Database Viewer"): X terminated, R reflected, T transmitted, I total internal
/// reflection, Z error; G marks a ghost (reflected from a refractive interface at its start).
/// S scattered, D diffracted and B bulk scattered happen at the end of the segment; F and E mark a
/// segment scattered or diffracted from its parent's hit object, at its start. The tracer sets
/// S and F once scattering (OP-80) is built; D, E (OP-82) and B (OP-81) are not set yet.</summary>
[Flags]
public enum NscEvents
{
    None = 0,
    Terminated = 1,
    Reflected = 2,
    Transmitted = 4,
    TotalInternalReflection = 8,
    Error = 16,
    Ghost = 32,
    /// <summary>S: scattered at the end of the segment.</summary>
    Scattered = 64,
    /// <summary>F: scattered from the parent segment's hit object (at the start).</summary>
    ScatteredFromParent = 128,
    /// <summary>D: diffracted at the end of the segment.</summary>
    Diffracted = 256,
    /// <summary>E: diffracted from the parent segment's hit object (at the start).</summary>
    DiffractedFromParent = 512,
    /// <summary>B: bulk scattered.</summary>
    BulkScattered = 1024,
}

/// <summary>
/// One segment of a ray tree, as OpticStudio's NSC Single Ray Trace and ray database list them
/// (help, "NSC Single Ray Trace", "Ray Database Viewer"): segment 0 is the source point;
/// every other segment's position, normal and intensity are at its end, on the object it hit,
/// "prior to reflection, refraction, splitting, absorbing, or scattering at the end point".
/// Positions and directions are in the scene frame, meters.
/// </summary>
public sealed record NscSegment
{
    public required int Number { get; init; }
    /// <summary>The segment this one was spawned from; −1 for segment 0.</summary>
    public required int Parent { get; init; }
    /// <summary>"How many segments away from the source ray the segment is."</summary>
    public required int Level { get; init; }
    /// <summary>The object the segment travels through (1-based), 0 for the background.</summary>
    public required int Inside { get; init; }
    /// <summary>The object hit at the end (1-based); 0 for the source segment and for a miss.</summary>
    public required int Hit { get; init; }
    public required int Face { get; init; }
    public required Vector3D Position { get; init; }
    public required Vector3D Direction { get; init; }
    /// <summary>The hit object's unit normal at the end (outward for a volume); zero when nothing was hit.</summary>
    public Vector3D Normal { get; init; }
    public required double Intensity { get; init; }
    /// <summary>Geometric length from the parent segment's end, meters.</summary>
    public double PathLength { get; init; }
    /// <summary>Optical path from the source point, meters (relative to the system's air).</summary>
    public double OpticalPath { get; init; }
    /// <summary>The index of the medium the segment runs through.</summary>
    public required double Index { get; init; }
    public NscEvents Events { get; set; }
    /// <summary>A plain sentence of what happened at the end ("refracted into object 2", "escaped").</summary>
    public string Fate { get; set; } = "";

    // OP-83
    /// <summary>What the segment left on detector objects at its end, each hit exactly as the
    /// tracer handed it to the detector (several when detectors touch other objects there); null
    /// for none. The ray database keeps these so a saved trace replays onto the detectors to the
    /// bit (docs/Optics-Conventions.md §75).</summary>
    public IReadOnlyList<global::OpenSim.Optics.NonSequential.Detectors.NscDetectorHit>? DetectorHits { get; set; }
}

/// <summary>One launched ray's whole tree: its segments in the order traced, its ledger and its
/// trace errors (each naming the segment, object and cause).</summary>
public sealed record NscRayTree
{
    public required IReadOnlyList<NscSegment> Segments { get; init; }
    public required NscEnergyLedger Ledger { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>The outcome of a Monte Carlo run: the ledger, the first trace errors (when errors
/// were ignored), and notes on the models used.</summary>
public sealed record NscRunResult
{
    public required NscEnergyLedger Ledger { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    // OP-79
    /// <summary>What each detector object collected, in object order; null when the scene has no
    /// detector objects (docs/Optics-Conventions.md §73). Combine the per-wavelength runs with
    /// <see cref="global::OpenSim.Optics.NonSequential.Detectors.NscDetectorResults.Combine"/>.</summary>
    public IReadOnlyList<global::OpenSim.Optics.NonSequential.Detectors.NscDetectorData>? Detectors { get; init; }
}

internal static class NscFormat
{
    public static string Letters(NscEvents e)
    {
        var s = new System.Text.StringBuilder();
        if (e.HasFlag(NscEvents.Terminated)) s.Append('X');
        if (e.HasFlag(NscEvents.Reflected)) s.Append('R');
        if (e.HasFlag(NscEvents.Transmitted)) s.Append('T');
        if (e.HasFlag(NscEvents.Ghost)) s.Append('G');
        if (e.HasFlag(NscEvents.Error)) s.Append('Z');
        if (e.HasFlag(NscEvents.TotalInternalReflection)) s.Append('I');
        if (e.HasFlag(NscEvents.Scattered)) s.Append('S');              // OP-80
        if (e.HasFlag(NscEvents.ScatteredFromParent)) s.Append('F');    // OP-80
        return s.ToString();
    }
}
