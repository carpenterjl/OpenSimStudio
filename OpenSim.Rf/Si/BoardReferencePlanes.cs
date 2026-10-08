using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>
/// The cross-section a trace layer actually sits in: the dielectric layers ground-up, the
/// interface carrying the traces, whether a second plane closes the top, the parallel-plate
/// capacitance per area a pad on that layer sees, and the sentence that says all of it.
/// </summary>
internal sealed record BoardSubstrate(
    LayeredStackup Stackup,
    int MetalInterface,
    bool TopGround,
    double PlateCapacitancePerSquareMeter,
    string Note)
{
    /// <summary>The copper layer(s) the line returns through: one for a microstrip, the upper
    /// then the lower for a stripline.</summary>
    public IReadOnlyList<int> PlaneLayers { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Finds the reference planes of a trace layer from the board's copper, and builds the
/// transmission-line cross-section between them.
///
/// <para>Before this existed every trace was priced as surface microstrip over "the gap
/// below, else the gap above", air on the other side, whether or not there was copper to
/// return through. An inner-layer trace between two planes came out with Z0 about 75 %
/// high and capacitance about 2× low. Here the planes are looked for: walking away from
/// the trace layer in each direction, the first copper layer with copper under at least
/// <see cref="CoverageFraction"/> of the trace is that side's reference plane.</para>
///
/// <list type="bullet">
/// <item>A plane on both sides — stripline: the gaps between the trace and each plane,
/// closed by a PEC at both ends.</item>
/// <item>A plane on one side — microstrip (trace on an outer layer) or embedded
/// microstrip (the board's remaining dielectric lies over the trace, then air).</item>
/// <item>No plane — refused with the coverage found on each layer. A transmission line
/// without a return conductor has no characteristic impedance to report.</item>
/// </list>
///
/// <para>The net's own copper is never its reference. Copper layers between the trace and
/// its plane that do not cover it (routing layers) are passed through as dielectric. The
/// plane is then idealised as infinite and solid — slots and the plane edge are not in
/// the model.</para>
/// </summary>
internal static class BoardReferencePlanes
{
    /// <summary>Fraction of the sampled trace points a layer's copper must lie under to be
    /// the reference plane: a plane with antipads and a few slots still qualifies, a layer
    /// of routed traces does not.</summary>
    public const double CoverageFraction = 0.9;

    private const double Epsilon0 = 8.8541878128e-12;

    /// <summary>Point-in-copper index per copper layer, built once per board (a DC sweep
    /// asks for every net of the board).</summary>
    private static readonly ConditionalWeakTable<PcbBoard, ConcurrentDictionary<int, PolygonSetIndex>>
        Indexes = new();

    /// <summary>Resolve the cross-section of <paramref name="traces"/> (all on
    /// <paramref name="layer"/>). Null with <paramref name="failure"/> set when no layer
    /// has copper under them.</summary>
    public static BoardSubstrate? Resolve(PcbBoard board, int layer,
        IReadOnlyList<TraceCenterline> traces, IReadOnlyCollection<CopperIsland> ownIslands,
        BoardCoupledOptions options, out string failure) =>
        Resolve(board, layer, SamplePoints(traces), ownIslands, options, out failure);

    public static BoardSubstrate? Resolve(PcbBoard board, int layer,
        IReadOnlyList<Point2> samples, IReadOnlyCollection<CopperIsland> ownIslands,
        BoardCoupledOptions options, out string failure)
    {
        failure = "";
        int layerCount = CopperLayerCount(board);
        if (layerCount < 2)
        {
            failure = $"the board has a single copper layer, so there is no reference plane above "
                + $"or below the traces on L{layer} to form a transmission line with.";
            return null;
        }

        var checkedLayers = new List<string>();
        double CoverageOf(int candidate)
        {
            var copper = Indexes.GetOrCreateValue(board).GetOrAdd(candidate, c =>
                new PolygonSetIndex(board.Islands.Where(i => i.LayerOrder == c).Select(i => i.Shape).ToList()));
            var own = ownIslands.Where(i => i.LayerOrder == candidate).Select(i => i.Shape).ToList();
            var ownIndex = own.Count > 0 ? new PolygonSetIndex(own) : null;
            int covered = 0;
            foreach (var p in samples)
                if (copper.Contains(p) && ownIndex?.Contains(p) != true) covered++;
            double fraction = samples.Count == 0 ? 0 : (double)covered / samples.Count;
            checkedLayers.Add($"L{candidate}: {fraction:P0}");
            return fraction;
        }

        int planeBelow = 0, planeAbove = 0;
        double coverBelow = 0, coverAbove = 0;
        for (int c = layer + 1; c <= layerCount && planeBelow == 0; c++)
        {
            double f = CoverageOf(c);
            if (f >= CoverageFraction) { planeBelow = c; coverBelow = f; }
        }
        for (int c = layer - 1; c >= 1 && planeAbove == 0; c--)
        {
            double f = CoverageOf(c);
            if (f >= CoverageFraction) { planeAbove = c; coverAbove = f; }
        }
        if (planeBelow == 0 && planeAbove == 0)
        {
            failure = $"no copper layer has copper under the traces on L{layer} "
                + $"({string.Join(", ", checkedLayers)}; {CoverageFraction:P0} is needed), so there is "
                + "no reference plane to form a transmission line with — a cross-section solved "
                + "over an assumed plane would be a made-up number.";
            return null;
        }

        var stackup = BoardCoupledExtractor.StackupOf(board, options);
        LayeredStackup.Layer Gap(int upper) => new(
            stackup.PermittivityOf(upper), Math.Max(stackup.LossTangentOf(upper), 0),
            stackup.GapThicknessOf(upper));
        string Describe(IEnumerable<int> gaps) => string.Join("; ", gaps.Select(g =>
            $"L{g}–L{g + 1} εr {stackup.PermittivityOf(g):g3}, tanδ {stackup.LossTangentOf(g):g3}, "
            + $"h {stackup.GapThicknessOf(g) * 1e3:g3} mm"));
        // Series parallel-plate capacitance per area through a run of gaps to a plane.
        double PlatePerArea(IEnumerable<int> gaps) =>
            Epsilon0 / gaps.Sum(g => stackup.GapThicknessOf(g) / stackup.PermittivityOf(g));

        if (planeBelow > 0 && planeAbove > 0)
        {
            // Stripline. Ground-up: the lower plane's gaps up to the trace, then on up to
            // the upper plane.
            var below = Enumerable.Range(layer, planeBelow - layer).Reverse().ToList();   // planeBelow−1 … layer
            var above = Enumerable.Range(planeAbove, layer - planeAbove).Reverse().ToList(); // layer−1 … planeAbove
            return new BoardSubstrate(
                new LayeredStackup(below.Concat(above).Select(Gap).ToList()),
                below.Count - 1, TopGround: true,
                PlatePerArea(below) + PlatePerArea(above),
                $"Stripline: trace layer L{layer} lies between reference planes L{planeAbove} above "
                + $"(copper under {coverAbove:P0} of the trace) and L{planeBelow} below ({coverBelow:P0}). "
                + $"Dielectric ({stackup.Source}) below the trace: {Describe(below.AsEnumerable().Reverse())}; "
                + $"above: {Describe(above)}. Both planes are taken as infinite and solid, and "
                + "coupling to any other layer is out of scope by construction.")
            { PlaneLayers = new[] { planeAbove, planeBelow } };
        }

        // One plane. Build ground-up from it: the gaps between plane and trace, then the
        // board's remaining dielectric on the far side of the trace, then air.
        bool isBelow = planeBelow > 0;
        int plane = isBelow ? planeBelow : planeAbove;
        double cover = isBelow ? coverBelow : coverAbove;
        var between = isBelow
            ? Enumerable.Range(layer, plane - layer).Reverse().ToList()            // plane−1 … layer
            : Enumerable.Range(plane, layer - plane).ToList();                     // plane … layer−1
        var beyond = isBelow
            ? Enumerable.Range(1, layer - 1).Reverse().ToList()                    // layer−1 … 1
            : Enumerable.Range(layer, layerCount - layer).ToList();                // layer … N−1
        var layers = between.Concat(beyond).Select(Gap).ToList();
        string side = isBelow ? "below" : "above";

        string note;
        if (between.Count == 1 && beyond.Count == 0)
            note = $"Substrate = the dielectric gap {side} trace layer L{layer} "
                + $"({stackup.Source}: εr {stackup.PermittivityOf(between[0]):g3}, "
                + $"tanδ {stackup.LossTangentOf(between[0]):g3}, "
                + $"h {stackup.GapThicknessOf(between[0]) * 1e3:g3} mm); the reference plane is "
                + $"L{plane} (copper under {cover:P0} of the trace), taken as infinite and solid, "
                + "with air on the other side, and coupling to any other layer is out of scope "
                + "by construction.";
        else
            note = $"Reference plane L{plane} {side} trace layer L{layer} (copper under {cover:P0} of "
                + $"the trace), taken as infinite and solid; no copper layer on the other side "
                + $"covers the trace. Dielectric ({stackup.Source}) between trace and plane: "
                + $"{Describe(isBelow ? between.AsEnumerable().Reverse() : between)}"
                + (beyond.Count > 0
                    ? $"; on the other side: {Describe(isBelow ? beyond : beyond)}, then air "
                      + "(an embedded microstrip)."
                    : "; air on the other side.")
                + " Coupling to any other layer is out of scope by construction.";

        return new BoardSubstrate(new LayeredStackup(layers), between.Count - 1, TopGround: false,
            PlatePerArea(between), note) { PlaneLayers = new[] { plane } };
    }

    /// <summary>Copper layers of the board: what the islands show, or what the file's
    /// stackup lists when that is more (a layer may carry no copper).</summary>
    private static int CopperLayerCount(PcbBoard board)
    {
        int fromCopper = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        int fromFile = board.Stackup is { DielectricGapThicknesses.Count: > 0 } s
            ? s.DielectricGapThicknesses.Count + 1 : 1;
        return Math.Max(fromCopper, fromFile);
    }

    /// <summary>Points along the centerlines, about two widths apart (at least the two ends,
    /// at most 64 per segment): what "copper under the trace" is measured on.</summary>
    internal static List<Point2> SamplePoints(IReadOnlyList<TraceCenterline> traces)
    {
        var points = new List<Point2>();
        foreach (var t in traces)
        {
            int steps = (int)Math.Clamp(Math.Ceiling(t.Length / (2 * Math.Max(t.Width, 1e-6))), 1, 64);
            for (int k = 0; k <= steps; k++)
                points.Add(t.Start + (t.End - t.Start) * ((double)k / steps));
        }
        return points;
    }
}
