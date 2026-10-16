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

    /// <summary>Grid pitch of a meshed plane [m], uniform over the plane; 0 (the default)
    /// meshes GRADED instead — the trace's height above the plane under and near the chain,
    /// growing by 1.3 per step toward the margin — refined until the plane has at most
    /// <see cref="MaxPlaneNodes"/> nodes.</summary>
    public double PlanePitchMeters { get; init; }

    public int MaxPlaneNodes { get; init; } = 6000;

    /// <summary>How far beyond the trace's extent the return plane is meshed [m]; 0 takes
    /// ten times the height above it, at least 3 mm. Current farther out is left out.</summary>
    public double PlaneMarginMeters { get; init; }

    /// <summary>Strips a plane's bars are cut into through the copper's thickness; 0 (the
    /// default) takes what the highest frequency needs, at most 3. With one strip the skin
    /// effect through the plane is not solved and its resistance stays the DC sheet's.</summary>
    public int PlaneStripsAcrossThickness { get; init; }

    /// <summary>Join the loop's ends to the plane ideally, as before FU-33, instead of through
    /// a connection with its own resistance and inductance.</summary>
    public bool IdealEndConnections { get; init; }
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
        "Via barrels are rings of filaments round their shell: the current can crowd round the barrel "
        + "toward a neighbour, though not through the thin wall; bends couple at the filament level.",
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
            else if (s.Profile == SegmentProfile.RoundTube && s.Thickness > 0 && s.Thickness <= s.Width / 2)
                model.AddTube(previous, next, s.Width, s.Thickness, options.ConductivitySiemensPerMeter);
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
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default) =>
        LoopOverPlane(chain, Array.Empty<TraceSegment3D>(), planeShape, planeZ, frequenciesHz, options, cancellationToken);

    /// <summary>
    /// A loop closed through a plane or pour in two legs: <paramref name="outbound"/> runs from
    /// the port's positive terminal to the plane (its last segment ends on the plane — a via
    /// barrel, or a trace whose end is tied to the copper under it), the plane carries the
    /// current to where <paramref name="inbound"/> leaves it and runs back to the port's
    /// negative terminal. An empty inbound leg is <see cref="ChainOverPlane"/>: the port sits
    /// between the outbound start and the plane under it. The plane is meshed around both legs.
    /// </summary>
    public static LoopExtractionResult LoopOverPlane(IReadOnlyList<TraceSegment3D> outbound, IReadOnlyList<TraceSegment3D> inbound,
        IReadOnlyList<Polygon2> planeShape, (double zLo, double zHi) planeZ, IReadOnlyList<double> frequenciesHz,
        LoopExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoopExtractionOptions();
        if (outbound.Count == 0) throw new InvalidOperationException("The trace chain is empty.");
        var chain = outbound.Concat(inbound).ToList();
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
        var model = new PeecModel();
        double delta = FinestSkinDepth(frequenciesHz, options);
        int acrossThickness = options.PlaneStripsAcrossThickness > 0 ? options.PlaneStripsAcrossThickness
            : PeecModel.StripsFor(planeThickness, delta, max: 3);
        var (start, end) = Add(model, outbound, delta, options);
        PlaneGrid grid;
        bool graded = !(options.PlanePitchMeters > 0);
        if (!graded)
        {
            double pitch = Math.Max(options.PlanePitchMeters, Math.Sqrt(area / options.MaxPlaneNodes));
            grid = model.AddPlane(copper, planeMid, planeThickness, pitch, options.ConductivitySiemensPerMeter,
                acrossThickness);
        }
        else
        {
            // Fine at the trace's height under and near the chain, growing 1.3 per step to the
            // margin; the fine pitch is coarsened only when even the graded grid would blow the
            // node budget.
            double chainMinX = chain.Min(s => Math.Min(s.Start.X, s.End.X)), chainMaxX = chain.Max(s => Math.Max(s.Start.X, s.End.X));
            double chainMinY = chain.Min(s => Math.Min(s.Start.Y, s.End.Y)), chainMaxY = chain.Max(s => Math.Max(s.Start.Y, s.End.Y));
            double fine = height;
            double[] xs, ys;
            while (true)
            {
                xs = GradedAxis(minX, maxX, chainMinX - 2 * height, chainMaxX + 2 * height, fine);
                ys = GradedAxis(minY, maxY, chainMinY - 2 * height, chainMaxY + 2 * height, fine);
                if ((long)xs.Length * ys.Length <= options.MaxPlaneNodes
                    || fine > Math.Max(maxX - minX, maxY - minY)) break;
                fine *= 1.25;
            }
            grid = model.AddPlane(copper, planeMid, planeThickness, xs, ys, options.ConductivitySiemensPerMeter,
                acrossThickness);
        }
        Point2 endXy = new(outbound[^1].End.X, outbound[^1].End.Y);
        Point2 returnXy = inbound.Count > 0 ? new(inbound[0].Start.X, inbound[0].Start.Y) : new(outbound[0].Start.X, outbound[0].Start.Y);
        if (grid.DistanceTo(returnXy) > 2 * Math.Max(grid.PitchMeters, height)
            || grid.DistanceTo(endXy) > 2 * Math.Max(grid.PitchMeters, height))
            throw new InvalidOperationException(
                "The plane has no copper under an end of the chain, so the current has nowhere to return from there.");

        // The joins at the loop's ends: a connection with its own resistance and inductance —
        // the adjoining segment's barrel (or a wire of the trace's width) down to the plane,
        // and a strip of plane copper over to the grid node that stands in for the spot.
        void Join(int chainNode, Vector3D at, TraceSegment3D adjoining, Point2 target)
        {
            var (gridPoint, gridNode) = grid.Nodes.MinBy(n => (n.Position - target).Length);
            if (options.IdealEndConnections)
            {
                model.AddShort(chainNode, gridNode);
                return;
            }
            int node = chainNode;
            if (Math.Abs(at.Z - planeMid) > planeThickness / 2)
            {
                int under = model.AddNode(new Vector3D(at.X, at.Y, planeMid));
                if (adjoining.Profile == SegmentProfile.RoundTube && adjoining.Thickness > 0
                    && adjoining.Thickness <= adjoining.Width / 2)
                    model.AddTube(node, under, adjoining.Width, adjoining.Thickness, options.ConductivitySiemensPerMeter);
                else
                    model.AddRoundWire(node, under, adjoining.Width / 2, options.ConductivitySiemensPerMeter);
                node = under;
            }
            if ((gridPoint - new Point2(at.X, at.Y)).Length > 1e-9)
                model.AddBar(node, gridNode, Math.Max(grid.PitchMeters, adjoining.Width), planeThickness,
                    options.ConductivitySiemensPerMeter);
            else
                model.AddShort(node, gridNode);
        }
        Join(end, outbound[^1].End, outbound[^1], endXy);
        if (inbound.Count > 0)
        {
            var (back, negative) = Add(model, inbound, delta, options);
            Join(back, inbound[0].Start, inbound[0], returnXy);
            model.AddPort("loop", start, negative);
        }
        else
            model.AddPort("loop", start, grid.NodeNear(returnXy));

        var notes = Common.ToList();
        notes.Add($"Return through the plane's copper within {margin * 1e3:g3} mm of the chain, meshed at "
            + $"{grid.PitchMeters * 1e3:g3} mm" + (graded ? " under and near the chain, grading 1.3 per step to the margin" : "")
            + $" ({grid.Nodes.Count} nodes), " + (acrossThickness > 1
                ? $"{acrossThickness} graded strips through its thickness, so the skin effect through the plane is solved too."
                : "one filament through its thickness: where the return flows in the plane is solved, the skin "
                  + "effect through the plane's thickness is not (its resistance is the DC sheet's)."));
        if (grid.PitchMeters > 1.5 * height)
            notes.Add($"The plane grid ({grid.PitchMeters * 1e3:g3} mm) is coarser than the trace's height above it "
                + $"({height * 1e3:g3} mm): the return cannot gather under the trace as tightly as it would, and the "
                + "high-frequency inductance is over-stated.");
        notes.Add(options.IdealEndConnections
            ? (inbound.Count > 0
                ? "The outbound leg's end and the inbound leg's start are joined to the plane ideally under them; the "
                  + "port is between the outbound start and the inbound end."
                : "The chain is joined to the plane ideally under its far end and seen between its near end and the "
                  + "plane under it: the vias that make those connections are not in the loop.")
            : (inbound.Count > 0
                ? "The legs join the plane through real connections (each leg's own barrel or a wire of its width down "
                  + "to the plane, and plane copper over to the mesh); the port is between the outbound start and the "
                  + "inbound end."
                : "The chain joins the plane through a real connection under its far end (its own barrel or a wire of "
                  + "its width, and plane copper over to the mesh); at the near end the loop is seen between the chain "
                  + "and the plane under it."));
        var points = model.Solve(frequenciesHz, false, cancellationToken);
        if (model.SolvedIteratively)
            notes.Add("Solved by the compressed iterative solve (far couplings in low-rank blocks): it matches the "
                + "dense factorization to its tolerances, about six digits.");
        return new LoopExtractionResult(model.PortNames, points, model.FilamentCount, notes);
    }

    /// <summary>Ascending coordinates over [<paramref name="lo"/>, <paramref name="hi"/>]:
    /// steps of <paramref name="fine"/> across the fine zone, growing 1.3 per step outside it.</summary>
    private static double[] GradedAxis(double lo, double hi, double fineLo, double fineHi, double fine)
    {
        const double growth = 1.3;
        fineLo = Math.Max(lo, fineLo);
        fineHi = Math.Min(hi, fineHi);
        if (fineHi - fineLo < fine)
        {
            double centre = 0.5 * (fineLo + fineHi);
            fineLo = Math.Max(lo, centre - fine / 2);
            fineHi = Math.Min(hi, fineLo + fine);
        }
        int steps = Math.Max(1, (int)Math.Ceiling((fineHi - fineLo) / fine - 1e-9));
        double step = (fineHi - fineLo) / steps;
        var axis = new List<double>();
        for (int k = 0; k <= steps; k++) axis.Add(fineLo + k * step);
        double x = fineHi, s = step;
        while (x < hi - 1e-12)
        {
            s *= growth;
            x = Math.Min(hi, x + s);
            axis.Add(x);
        }
        x = fineLo;
        s = step;
        while (x > lo + 1e-12)
        {
            s *= growth;
            x = Math.Max(lo, x - s);
            axis.Insert(0, x);
        }
        return axis.ToArray();
    }

    /// <summary>The area of a loop made of the chain and its return in the plane directly beneath:
    /// each horizontal segment's length times its height above the plane's facing surface
    /// (barrels, vertical, enclose nothing by themselves).</summary>
    public static double AreaOverPlane(IReadOnlyList<TraceSegment3D> chain, (double zLo, double zHi) planeZ)
    {
        double planeMid = 0.5 * (planeZ.zLo + planeZ.zHi);
        double area = 0;
        foreach (var s in chain.Where(s => Math.Abs(s.Direction.Z) < 1e-6))
        {
            double surface = s.Start.Z >= planeMid ? planeZ.zHi : planeZ.zLo;
            area += s.Length * Math.Abs(s.Start.Z - surface);
        }
        return area;
    }

    /// <summary>The area the polygon of a chain and its return enclose in plan view (the
    /// shoelace formula over the chain's points followed by the return's, taken far end
    /// first).</summary>
    public static double AreaBetween(IReadOnlyList<TraceSegment3D> chain, IReadOnlyList<TraceSegment3D> returnChain)
    {
        var points = new List<Point2>();
        foreach (var s in chain) points.Add(new Point2(s.Start.X, s.Start.Y));
        points.Add(new Point2(chain[^1].End.X, chain[^1].End.Y));
        bool forward = (returnChain[0].Start - chain[^1].End).Length <= (returnChain[^1].End - chain[^1].End).Length;
        var back = forward ? returnChain : returnChain.Reverse().Select(s => s with { Start = s.End, End = s.Start }).ToList();
        foreach (var s in back) points.Add(new Point2(s.Start.X, s.Start.Y));
        points.Add(new Point2(back[^1].End.X, back[^1].End.Y));
        double twice = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return 0.5 * Math.Abs(twice);
    }
}

/// <summary>A Maxwell capacitance matrix of several nets over their reference planes.</summary>
/// <param name="Farads">C[i, i]: net i's total capacitance (to the planes and to the other
/// nets); C[i, j], i ≠ j: minus the capacitance between nets i and j.</param>
public sealed record NetCapacitanceMatrix(IReadOnlyList<string> Nets, double[,] Farads,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>The crossing, pad and via terms that went in beyond the side-by-side and
    /// broadside runs (FU-33), each with its place and kind.</summary>
    public IReadOnlyList<CouplingTerm> Terms { get; init; } = Array.Empty<CouplingTerm>();

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
            $"Between nets: stretches running side by side on one layer within {maxEdgeGapMeters * 1e3:g3} mm "
            + "(a third trace between two is solved as a guard there), and broadside stretches on two layers with "
            + "no plane between, each solved as a 2D cross-section, mutual capacitance per length times length.",
            "Also between nets (FU-33): crossings on two layers (the overlap's section at the crossing angle), "
            + "stacked and side-by-side pads, and parallel via barrels over their shared depth (the two-cylinder "
            + "form; a plane's clearance holes around a via pair are not modelled, so that term is conservative). "
            + "Copper of a third net between two shields a crossing or pad pair, and the term is dropped.",
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

        // The terms the run scan cannot see: crossings, pads, via barrels (FU-33).
        var terms = NetCouplingTerms.Find(new BoardLayoutIndex(board), stackup ?? BoardStackup.FromBoard(board),
            nets, maxEdgeGapMeters);
        foreach (var term in terms)
        {
            int i = wanted.IndexOf(term.NetA), j = wanted.IndexOf(term.NetB);
            if (i < 0 || j < 0 || i == j) continue;
            c[i, j] -= term.Farads;
            c[j, i] -= term.Farads;
            c[i, i] += term.Farads;
            c[j, j] += term.Farads;
        }
        return new NetCapacitanceMatrix(wanted, c, notes) { Terms = terms };
    }
}
