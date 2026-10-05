using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Pcb.Polygons;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Layout;

namespace OpenSim.Rf.Extraction;

public sealed record LoopExtractionOptions
{
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Most strips a trace section is cut into across its width and its thickness.</summary>
    public int MaxStripsAcrossWidth { get; init; } = 7;
    public int MaxStripsAcrossThickness { get; init; } = 5;

    /// <summary>Grid pitch of a meshed plane [m]; 0 takes the trace's height above it,
    /// coarsened until the plane has at most <see cref="MaxPlaneNodes"/> nodes.</summary>
    public double PlanePitchMeters { get; init; }

    public int MaxPlaneNodes { get; init; } = 1400;

    /// <summary>How far beyond the trace's extent the return plane is meshed [m]; 0 takes
    /// ten times the height above it, at least 3 mm. Current farther out is left out.</summary>
    public double PlaneMarginMeters { get; init; }
}

/// <summary>R and L of a current path against frequency.</summary>
public sealed record LoopExtractionResult(
    IReadOnlyList<string> PortNames,
    IReadOnlyList<PeecPoint> Points,
    int Filaments,
    IReadOnlyList<string> Assumptions)
{
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        foreach (var p in Points)
        {
            string f = p.FrequencyHz >= 1e6 ? $"{p.FrequencyHz / 1e6:g4} MHz" : p.FrequencyHz >= 1e3
                ? $"{p.FrequencyHz / 1e3:g4} kHz" : $"{p.FrequencyHz:g4} Hz";
            var parts = new List<string>();
            for (int i = 0; i < PortNames.Count; i++)
                parts.Add($"{(PortNames.Count > 1 ? PortNames[i] + ": " : "")}R {p.Resistance(i, i) * 1e3:g4} mΩ, L {p.Inductance(i, i) * 1e9:g4} nH");
            for (int i = 0; i < PortNames.Count; i++)
                for (int j = i + 1; j < PortNames.Count; j++)
                    parts.Add($"M {p.Inductance(i, j) * 1e9:g4} nH (k {p.Inductance(i, j) / Math.Sqrt(p.Inductance(i, i) * p.Inductance(j, j)):g3})");
            lines.Add($"{f}: {string.Join("; ", parts)}");
        }
        return lines;
    }
}

/// <summary>
/// Frequency-dependent resistance and inductance of routed copper by the filament solve
/// (<see cref="PeecModel"/>): a trace chain alone, a chain with a return chain, a chain
/// returning through the copper of a plane or pour, or two chains as two coupled ports.
/// </summary>
public static class LoopExtraction
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();

    private static readonly string[] Common =
    {
        "Filament solve: each trace section is cut into strips carrying uniform current, finer toward the faces; "
        + "skin and proximity effect come from how the current divides among them.",
        "Magneto-quasi-static: no displacement current, so valid while the path is short against a wavelength; "
        + "capacitance is a separate extraction.",
        "Via barrels are one filament each (no skin effect in a barrel); bends couple at the filament level.",
        "Non-magnetic materials."
    };

    /// <summary>Adds a chain to the model; returns its first and last node.</summary>
    private static (int Start, int End) Add(PeecModel model, IReadOnlyList<TraceSegment3D> chain, double skinDepth,
        LoopExtractionOptions options)
    {
        if (chain.Count == 0) throw new InvalidOperationException("The trace chain is empty.");
        int first = model.AddNode(chain[0].Start), previous = first;
        for (int i = 0; i < chain.Count; i++)
        {
            var s = chain[i];
            // Segments of a chain are head to tail; a gap between two is closed ideally.
            if (i > 0 && (s.Start - chain[i - 1].End).Length > 1e-9)
            {
                int restart = model.AddNode(s.Start);
                model.AddShort(previous, restart);
                previous = restart;
            }
            int next = model.AddNode(s.End);
            if (s.Profile == SegmentProfile.Bar && Math.Abs(s.Direction.Z) < 1e-6)
                model.AddBar(previous, next, s.Width, s.Thickness, options.ConductivitySiemensPerMeter,
                    PeecModel.StripsFor(s.Width, skinDepth, max: options.MaxStripsAcrossWidth),
                    PeecModel.StripsFor(s.Thickness, skinDepth, max: options.MaxStripsAcrossThickness));
            else
                model.AddRoundWire(previous, next, s.Profile == SegmentProfile.Bar
                    ? Math.Sqrt(s.Width * s.Thickness / Math.PI) : s.Width / 2, options.ConductivitySiemensPerMeter);
            previous = next;
        }
        return (first, previous);
    }

    private static double FinestSkinDepth(IReadOnlyList<double> frequenciesHz, LoopExtractionOptions options) =>
        PeecModel.SkinDepth(frequenciesHz.Max(), options.ConductivitySiemensPerMeter);

    /// <summary>The chain by itself, end to end: its partial resistance and inductance.</summary>
    public static LoopExtractionResult Chain(IReadOnlyList<TraceSegment3D> chain, IReadOnlyList<double> frequenciesHz,
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoopExtractionOptions();
        var model = new PeecModel();
        var (start, end) = Add(model, chain, FinestSkinDepth(frequenciesHz, options), options);
        model.AddPort("end to end", start, end);
        return new LoopExtractionResult(model.PortNames, model.Solve(frequenciesHz, false, cancellationToken),
            model.FilamentCount, Common.Append(
                "Partial values: the chain end to end with no return conductor. A loop needs its return.").ToList());
    }

    /// <summary>Out on one chain and back on the other: joined at their far ends, seen
    /// between their near ends. Which end of the return is the far one is decided by
    /// distance.</summary>
    public static LoopExtractionResult ChainAndReturn(IReadOnlyList<TraceSegment3D> chain,
        IReadOnlyList<TraceSegment3D> returnChain, IReadOnlyList<double> frequenciesHz,
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoopExtractionOptions();
        var model = new PeecModel();
        double delta = FinestSkinDepth(frequenciesHz, options);
        var (aStart, aEnd) = Add(model, chain, delta, options);
        var (bStart, bEnd) = Add(model, returnChain, delta, options);
        bool forward = (returnChain[0].Start - chain[^1].End).Length <= (returnChain[^1].End - chain[^1].End).Length;
        int far = forward ? bStart : bEnd, near = forward ? bEnd : bStart;
        model.AddShort(aEnd, far);
        model.AddPort("loop", aStart, near);
        return new LoopExtractionResult(model.PortNames, model.Solve(frequenciesHz, false, cancellationToken),
            model.FilamentCount, Common.Append(
                "Loop: the two chains joined ideally at their far ends and seen between their near ends; the "
                + "connections that close it there are not modelled.").ToList());
    }

    /// <summary>Two chains as two ports: each chain's own R and L and what they share.</summary>
    public static LoopExtractionResult TwoChains(IReadOnlyList<TraceSegment3D> chainA, string nameA,
        IReadOnlyList<TraceSegment3D> chainB, string nameB, IReadOnlyList<double> frequenciesHz,
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoopExtractionOptions();
        var model = new PeecModel();
        double delta = FinestSkinDepth(frequenciesHz, options);
        var (aStart, aEnd) = Add(model, chainA, delta, options);
        var (bStart, bEnd) = Add(model, chainB, delta, options);
        model.AddPort(nameA, aStart, aEnd);
        model.AddPort(nameB, bStart, bEnd);
        return new LoopExtractionResult(model.PortNames, model.Solve(frequenciesHz, false, cancellationToken),
            model.FilamentCount, Common.Append(
                "Partial values of each chain end to end, and their mutual; the sign of the mutual follows the "
                + "direction each chain is traversed in.").ToList());
    }

    /// <summary>
    /// The chain returning through a plane or pour: joined to the copper under its far end,
    /// seen between its near end and the copper under that. The copper is meshed as a grid
    /// around the chain, so the return finds its own way — under the trace at high
    /// frequency, spread out at low, and round a cut-out when there is one.
    /// </summary>
    /// <param name="planeShape">The plane's copper.</param>
    /// <param name="planeZ">(zLo, zHi) of the plane's copper in the chain's z frame.</param>
    public static LoopExtractionResult ChainOverPlane(IReadOnlyList<TraceSegment3D> chain,
        IReadOnlyList<Polygon2> planeShape, (double zLo, double zHi) planeZ, IReadOnlyList<double> frequenciesHz,
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoopExtractionOptions();
        if (chain.Count == 0) throw new InvalidOperationException("The trace chain is empty.");
        double planeMid = 0.5 * (planeZ.zLo + planeZ.zHi), planeThickness = planeZ.zHi - planeZ.zLo;
        double height = chain.Where(s => Math.Abs(s.Direction.Z) < 1e-6)
            .Select(s => Math.Abs(s.Start.Z - planeMid) - planeThickness / 2 - s.Thickness / 2)
            .DefaultIfEmpty(0).Min();
        if (!(height > 0))
            throw new InvalidOperationException("The chain does not run above or below the plane (it touches or crosses it).");

        double margin = options.PlaneMarginMeters > 0 ? options.PlaneMarginMeters : Math.Max(3e-3, 10 * height);
        double minX = chain.Min(s => Math.Min(s.Start.X, s.End.X)) - margin, maxX = chain.Max(s => Math.Max(s.Start.X, s.End.X)) + margin;
        double minY = chain.Min(s => Math.Min(s.Start.Y, s.End.Y)) - margin, maxY = chain.Max(s => Math.Max(s.Start.Y, s.End.Y)) + margin;
        var window = new[] { new Point2(minX, minY), new Point2(maxX, minY), new Point2(maxX, maxY), new Point2(minX, maxY) };
        var copper = Ops.Intersect(planeShape, new[] { (IReadOnlyList<Point2>)window });
        double area = copper.Sum(p => p.Area());
        if (!(area > 0))
            throw new InvalidOperationException("The plane has no copper near the chain.");
        double pitch = options.PlanePitchMeters > 0 ? options.PlanePitchMeters : height;
        pitch = Math.Max(pitch, Math.Sqrt(area / options.MaxPlaneNodes));

        var model = new PeecModel();
        var (start, end) = Add(model, chain, FinestSkinDepth(frequenciesHz, options), options);
        var grid = model.AddPlane(copper, planeMid, planeThickness, pitch, options.ConductivitySiemensPerMeter);
        Point2 startXy = new(chain[0].Start.X, chain[0].Start.Y), endXy = new(chain[^1].End.X, chain[^1].End.Y);
        if (grid.DistanceTo(startXy) > 2 * grid.PitchMeters || grid.DistanceTo(endXy) > 2 * grid.PitchMeters)
            throw new InvalidOperationException(
                "The plane has no copper under an end of the chain, so the current has nowhere to return from there.");
        model.AddShort(end, grid.NodeNear(endXy));
        model.AddPort("loop", start, grid.NodeNear(startXy));

        var notes = Common.ToList();
        notes.Add($"Return through the plane's copper within {margin * 1e3:g3} mm of the chain, meshed at "
            + $"{grid.PitchMeters * 1e3:g3} mm ({grid.Nodes.Count} nodes), one filament through its thickness: "
            + "where the return flows in the plane is solved, the skin effect through the plane's thickness is not "
            + "(its resistance is the DC sheet's).");
        if (grid.PitchMeters > 1.5 * height)
            notes.Add($"The plane grid ({grid.PitchMeters * 1e3:g3} mm) is coarser than the trace's height above it "
                + $"({height * 1e3:g3} mm): the return cannot gather under the trace as tightly as it would, and the "
                + "high-frequency inductance is over-stated.");
        notes.Add("The chain is joined to the plane ideally under its far end and seen between its near end and the "
            + "plane under it: the vias that make those connections are not in the loop.");
        return new LoopExtractionResult(model.PortNames, model.Solve(frequenciesHz, false, cancellationToken),
            model.FilamentCount, notes);
    }
}

/// <summary>A Maxwell capacitance matrix of several nets over their reference planes.</summary>
/// <param name="Farads">C[i, i]: net i's total capacitance (to the planes and to the other
/// nets); C[i, j], i ≠ j: minus the capacitance between nets i and j.</param>
public sealed record NetCapacitanceMatrix(IReadOnlyList<string> Nets, double[,] Farads,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>Capacitance between two nets [F] (positive).</summary>
    public double Between(int i, int j) => -Farads[i, j];

    /// <summary>Net i's capacitance to the reference planes alone [F].</summary>
    public double ToPlanes(int i)
    {
        double c = Farads[i, i];
        for (int j = 0; j < Nets.Count; j++) if (j != i) c += Farads[i, j];
        return c;
    }
}

/// <summary>
/// Net-to-net capacitance from the two cross-section extractions the board already has: each
/// net's capacitance to its reference planes (<see cref="TraceCapacitanceExtractor"/>, every
/// centerline and pad of the net), and between two nets the mutual capacitance per length of
/// each stretch they run side by side on a layer, times its length (<see cref="CrosstalkScan"/>).
/// </summary>
public static class NetCapacitance
{
    public static NetCapacitanceMatrix Extract(PcbBoard board, IReadOnlyList<CopperNet> nets,
        BoardStackup? stackup = null, double maxEdgeGapMeters = 2e-3)
    {
        if (nets.Count == 0) throw new InvalidOperationException("Choose at least one net.");
        int n = nets.Count;
        var c = new double[n, n];
        var notes = new List<string>
        {
            "To the planes: per-length capacitance of each (layer, width) of the net's traces over the reference "
            + "planes found for it, times routed length, plus parallel-plate pad terms; the net is solved alone.",
            $"Between nets: stretches running side by side on one layer within {maxEdgeGapMeters * 1e3:g3} mm, each "
            + "solved as two traces over their planes, mutual capacitance per length times length. Coupling "
            + "between layers, at crossings, between pads and through vias is not in the matrix, and a third "
            + "trace between two is not seen as a shield.",
            "A net's total is its capacitance to the planes when alone plus what it has to each neighbour; a "
            + "neighbour also takes some of the field that went to the plane, so the total is slightly over-stated."
        };
        var options = new BoardCoupledOptions { Stackup = stackup };
        for (int i = 0; i < n; i++)
        {
            var own = TraceCapacitanceExtractor.Extract(board, nets[i], options);
            if (own.FailureReason is not null)
                throw new InvalidOperationException(own.FailureReason);
            c[i, i] = own.TotalFarads;
        }
        var wanted = nets.Select(BoardLayoutIndex.NameOf).ToList();
        var scan = CrosstalkScan.Run(board, new CrosstalkScanOptions
        {
            Stackup = stackup, MaxEdgeGapMeters = maxEdgeGapMeters, MinParallelLengthMeters = 0, MaxSolvedPairs = int.MaxValue
        });
        foreach (var pair in scan.Pairs)
        {
            int i = wanted.IndexOf(pair.NetA), j = wanted.IndexOf(pair.NetB);
            if (i < 0 || j < 0 || i == j || pair.NotPriced is not null) continue;
            double mutual = pair.Runs.Sum(r => r.MutualCapacitanceFaradsPerMeter * r.LengthMeters);
            c[i, j] -= mutual;
            c[j, i] -= mutual;
            // A net's total includes what it has to its neighbours.
            c[i, i] += mutual;
            c[j, j] += mutual;
        }
        return new NetCapacitanceMatrix(wanted, c, notes);
    }
}
