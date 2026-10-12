using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Extraction;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Layout;

namespace OpenSim.Rf.Emi;

/// <summary>The parts that bound a converter's hot loop, by reference designator: the input
/// capacitor, the high-side switch and the low-side switch or diode. One integrated part may be
/// both switches (a converter IC: its input pin and its power return pin bound the loop, and the
/// switch node's copper is outside it).</summary>
public sealed record ConverterParts(string InputCapacitor, string HighSideSwitch, string LowSideSwitch)
{
    public bool Integrated => string.Equals(HighSideSwitch, LowSideSwitch, StringComparison.OrdinalIgnoreCase);
}

public sealed record ConverterBoardOptions
{
    /// <summary>The stackup; null takes the board file's.</summary>
    public BoardStackup? Stackup { get; init; }

    public LoopExtractionOptions? Loop { get; init; }

    /// <summary>Frequencies the loop is solved at; the highest gives the inductance and resistance
    /// the ringing model takes (the ring sits at tens of MHz, where L has settled).</summary>
    public IReadOnlyList<double> FrequenciesHz { get; init; } = new[] { 1e6, 1e7, 1e8 };

    /// <summary>Smallest island of the return net that counts as a plane [m²]; a return net with
    /// none is followed as traces.</summary>
    public double MinPlaneAreaSquareMeters { get; init; } = 100e-6;

    /// <summary>How far from a pad its via down to the plane is looked for [m].</summary>
    public double ViaSearchRadiusMeters { get; init; } = 3e-3;
}

/// <summary>What the board says about a converter's EMI model.</summary>
/// <param name="SwitchNodeToReturnCapacitance">The switch-node net's capacitance to its reference
/// planes [F] (0 with <paramref name="SwitchNodeCapacitanceProblem"/> set when it could not be read).</param>
/// <param name="HotLoopInductance">The hot loop's inductance at the highest solved frequency [H],
/// the copper's: the parts' package inductance is not in it.</param>
/// <param name="HotLoopResistance">The copper's resistance at that frequency [Ω].</param>
/// <param name="HotLoopAreaM2">The loop's area [m²]: over a plane, each run's length times its height
/// above the plane; between traces, what the two paths enclose in plan view.</param>
public sealed record ConverterBoardReading(
    double SwitchNodeToReturnCapacitance, string? SwitchNodeCapacitanceProblem,
    double HotLoopInductance, double HotLoopResistance, double HotLoopAreaM2, double HotLoopLengthMeters,
    bool ReturnIsPlane, LoopExtractionResult Loop, IReadOnlyList<string> Notes)
{
    /// <summary>The input with the board's numbers in it: the board's switch-node capacitance and loop
    /// resistance add to what is given (the switches' output capacitance and on-resistance), the
    /// loop inductance adds to the given package inductance, and the loop area is the board's.</summary>
    public ConverterInput Apply(ConverterInput input) => input with
    {
        SwitchNodeToReturnCapacitance = input.SwitchNodeToReturnCapacitance + SwitchNodeToReturnCapacitance,
        HotLoopInductance = input.HotLoopInductance + HotLoopInductance,
        HotLoopResistance = input.HotLoopResistance + HotLoopResistance,
        HotLoopAreaM2 = HotLoopAreaM2
    };

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"Hot loop: {HotLoopLengthMeters * 1e3:g4} mm of copper, {HotLoopInductance * 1e9:g4} nH and {HotLoopResistance * 1e3:g4} mΩ at "
            + $"{Loop.Points[^1].FrequencyHz / 1e6:g4} MHz, area {HotLoopAreaM2 * 1e6:g4} mm², returning through "
            + (ReturnIsPlane ? "the plane." : "traces.")
        };
        lines.AddRange(Loop.Describe().Select(l => "  " + l));
        lines.Add(SwitchNodeCapacitanceProblem is null
            ? $"Switch node: {SwitchNodeToReturnCapacitance * 1e12:g4} pF to its reference planes (add the switches' output capacitance)."
            : $"Switch node capacitance not read: {SwitchNodeCapacitanceProblem}");
        return lines;
    }
}

/// <summary>
/// Reads a converter's EMI model off its board: the hot loop — from the input capacitor's input
/// pad along the input net to the high-side switch, through it (ideally) along the switch node
/// to the low-side switch, through it to its return pad, then back to the capacitor's return pad
/// through the return plane (down one via, along the plane, up the other) or along return traces
/// — solved by the filament extraction for its inductance and resistance; its area for the
/// radiated estimate; and the switch-node net's capacitance to the planes. The parts' bodies are
/// ideal joins: the FET packages' inductance and the capacitor's ESL are the user's numbers.
/// </summary>
public static class ConverterBoard
{
    public static ConverterBoardReading Read(PcbBoard board, CopperNet input, CopperNet @return, CopperNet switchNode,
        ConverterParts parts, ConverterBoardOptions? options = null)
    {
        options ??= new ConverterBoardOptions();
        if (ReferenceEquals(input, @return) || ReferenceEquals(input, switchNode) || ReferenceEquals(@return, switchNode))
            throw new InvalidOperationException("The input, return and switch-node nets must be three different nets.");
        var stackup = options.Stackup ?? BoardStackup.FromBoard(board);
        var mesh = new NetMeshOptions
        {
            CopperThickness = stackup.DefaultCopperThickness, LayerThickness = stackup.LayerThickness,
            DefaultDielectricThickness = stackup.DefaultGapThickness, DielectricGapThickness = stackup.GapThickness,
            DielectricGapPermittivity = stackup.GapPermittivity, DielectricGapLossTangent = stackup.GapLossTangent
        };
        var index = new BoardLayoutIndex(board);
        var layers = input.Layers.Concat(@return.Layers).Concat(switchNode.Layers).Distinct().ToList();
        var notes = new List<string>();

        CopperNet? NetAt(CopperPad pad) => index.IslandAt(pad.LayerOrder, pad.Center) is { } island ? index.NetOf(island) : null;
        IReadOnlyList<CopperPad> PadsOf(string refDes, CopperNet net)
        {
            var all = board.Pads.Where(p => string.Equals(p.ComponentRef, refDes, StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count == 0)
                throw new InvalidOperationException($"The board has no pads of a part '{refDes}' (the file must name its components: IPC-2581, or Gerber with X2 attributes).");
            var on = all.Where(p => ReferenceEquals(NetAt(p), net)).ToList();
            if (on.Count == 0)
                throw new InvalidOperationException($"'{refDes}' has no pad on {BoardLayoutIndex.NameOf(net)} (its pads sit on "
                    + string.Join(", ", all.Select(p => NetAt(p) is { } n ? BoardLayoutIndex.NameOf(n) : "no copper").Distinct()) + ").");
            return on;
        }
        static (CopperPad A, CopperPad B) Closest(IReadOnlyList<CopperPad> a, IReadOnlyList<CopperPad> b) =>
            a.SelectMany(x => b.Select(y => (x, y))).MinBy(pair => (pair.x.Center - pair.y.Center).Length);
        static CopperPad Nearest(IReadOnlyList<CopperPad> pads, Point2 to) => pads.MinBy(p => (p.Center - to).Length)!;

        IReadOnlyList<TraceSegment3D> Chain(CopperNet net, CopperPad from, CopperPad to, string what, out IReadOnlyDictionary<int, (double zLo, double zHi)> layerZ)
        {
            var traces = NetTraceExtractor.ForNet(board, net);
            var terminals = (new ChainTerminal(from.Center, from.LayerOrder) { Reach = from.Size / 2 },
                new ChainTerminal(to.Center, to.LayerOrder) { Reach = to.Size / 2 });
            var built = TraceChainBuilder.Build(traces, net.StitchingVias, mesh, net.Islands, layers, terminals);
            if (built.Chain is null)
                throw new InvalidOperationException($"{what} along {BoardLayoutIndex.NameOf(net)}: {built.FailureReason}.");
            if (built.CopperBridges > 0)
                notes.Add($"{what}: {built.CopperBridges} pad/fill crossing(s) closed by straight bars.");
            layerZ = built.LayerZ ?? throw new InvalidOperationException("The chain builder gave no stackup frame.");
            return built.Chain;
        }

        // The pads that bound the loop: the closest pair where two parts meet.
        var (cinIn, hsIn) = Closest(PadsOf(parts.InputCapacitor, input), PadsOf(parts.HighSideSwitch, input));
        var cinRet = Nearest(PadsOf(parts.InputCapacitor, @return), cinIn.Center);
        var forward = new List<TraceSegment3D>();
        string path;
        var inputChain = Chain(input, cinIn, hsIn, $"{parts.InputCapacitor} → {parts.HighSideSwitch}", out var layerZ);
        forward.AddRange(inputChain);
        CopperPad lsRet;
        if (parts.Integrated)
        {
            lsRet = Nearest(PadsOf(parts.LowSideSwitch, @return), hsIn.Center);
            path = $"{parts.InputCapacitor} → {BoardLayoutIndex.NameOf(input)} → {parts.HighSideSwitch} (both switches) → {BoardLayoutIndex.NameOf(@return)} → {parts.InputCapacitor}";
        }
        else
        {
            var (hsSw, lsSw) = Closest(PadsOf(parts.HighSideSwitch, switchNode), PadsOf(parts.LowSideSwitch, switchNode));
            forward.AddRange(Chain(switchNode, hsSw, lsSw, $"{parts.HighSideSwitch} → {parts.LowSideSwitch}", out _));
            lsRet = Nearest(PadsOf(parts.LowSideSwitch, @return), lsSw.Center);
            path = $"{parts.InputCapacitor} → {BoardLayoutIndex.NameOf(input)} → {parts.HighSideSwitch} → {BoardLayoutIndex.NameOf(switchNode)} → "
                + $"{parts.LowSideSwitch} → {BoardLayoutIndex.NameOf(@return)} → {parts.InputCapacitor}";
        }

        // The return: a plane on another layer than the parts, or traces.
        var plane = @return.Islands.Where(i => i.Area >= options.MinPlaneAreaSquareMeters && i.LayerOrder != cinIn.LayerOrder).MaxBy(i => i.Area);
        var loopOptions = options.Loop ?? new LoopExtractionOptions();
        LoopExtractionResult loop;
        double area, length;
        if (plane is not null)
        {
            if (!layerZ.TryGetValue(plane.LayerOrder, out var planeZ))
                throw new InvalidOperationException($"The return plane's layer L{plane.LayerOrder} is not in the chain's stackup frame.");
            var down = Leg(lsRet, @return, plane.LayerOrder, layerZ, planeZ, mesh, options, parts.LowSideSwitch, up: false);
            var up = Leg(cinRet, @return, plane.LayerOrder, layerZ, planeZ, mesh, options, parts.InputCapacitor, up: true);
            forward.AddRange(down);
            loop = LoopExtraction.LoopOverPlane(forward, up, new[] { plane.Shape }, planeZ, options.FrequenciesHz, loopOptions);
            var whole = forward.Concat(up).ToList();
            area = LoopExtraction.AreaOverPlane(whole, planeZ);
            length = whole.Sum(s => s.Length);
            notes.Add($"Hot loop {path}: {length * 1e3:g4} mm of copper in {whole.Count} segments, returning through the "
                + $"{BoardLayoutIndex.NameOf(@return)} plane on L{plane.LayerOrder} — down {parts.LowSideSwitch}'s via, up {parts.InputCapacitor}'s.");
        }
        else
        {
            var returnChain = Chain(@return, lsRet, cinRet, $"{parts.LowSideSwitch} → {parts.InputCapacitor}", out _);
            loop = LoopExtraction.ChainAndReturn(forward, returnChain, options.FrequenciesHz, loopOptions);
            area = LoopExtraction.AreaBetween(forward, returnChain);
            length = forward.Sum(s => s.Length) + returnChain.Sum(s => s.Length);
            notes.Add($"Hot loop {path}: {length * 1e3:g4} mm of copper in {forward.Count + returnChain.Count} segments, returning along "
                + $"{BoardLayoutIndex.NameOf(@return)}'s traces (no island of it of {options.MinPlaneAreaSquareMeters * 1e6:g3} mm² or more on another layer).");
        }
        notes.Add("The parts' bodies are ideal joins: the switches' package inductance and the input capacitor's ESL are not in the loop — "
            + "give them with the input. The loop's resistance is the copper's; add the switches' on-resistance.");
        notes.AddRange(loop.Assumptions);

        var capacitance = TraceCapacitanceExtractor.Extract(board, switchNode, new BoardCoupledOptions { Stackup = stackup });
        string? problem = capacitance.FailureReason;
        if (problem is null)
            notes.Add($"Switch node {BoardLayoutIndex.NameOf(switchNode)}: {capacitance.TotalFarads * 1e12:g4} pF to its reference planes "
                + $"({capacitance.TraceFarads * 1e12:g3} pF of traces, {capacitance.PadFarads * 1e12:g3} pF of {capacitance.PadCount} pads, no fringing at the pads); "
                + "it resonates with the hot loop and carries no common-mode current. The switches' output capacitance adds to it.");
        else
            notes.Add($"The switch node's capacitance could not be read: {problem}");

        var last = loop.Points[^1];
        return new ConverterBoardReading(problem is null ? capacitance.TotalFarads : 0, problem,
            last.Inductance(0, 0), last.Resistance(0, 0), area, length, plane is not null, loop, notes);
    }

    /// <summary>A pad's way to the plane: the escape bar from the pad to its via (when the via is
    /// not in the pad) and the barrel down to the plane's facing surface — or the same in reverse
    /// for the way up.</summary>
    private static IReadOnlyList<TraceSegment3D> Leg(CopperPad pad, CopperNet net, int planeLayer,
        IReadOnlyDictionary<int, (double zLo, double zHi)> layerZ, (double zLo, double zHi) planeZ, NetMeshOptions mesh,
        ConverterBoardOptions options, string refDes, bool up)
    {
        if (pad.LayerOrder == planeLayer)
            throw new InvalidOperationException($"{refDes}'s {BoardLayoutIndex.NameOf(net)} pad sits on the plane's own layer L{planeLayer}.");
        ViaBridge? best = null;
        double nearest = options.ViaSearchRadiusMeters;
        foreach (var bridge in net.StitchingVias)
        {
            if (!bridge.Layers.Contains(pad.LayerOrder) || !bridge.Layers.Contains(planeLayer)) continue;
            double distance = (bridge.Via.Position - pad.Center).Length;
            if (distance <= nearest) { nearest = distance; best = bridge; }
        }
        if (best is null)
            throw new InvalidOperationException($"No via joins {refDes}'s {BoardLayoutIndex.NameOf(net)} pad on L{pad.LayerOrder} to the plane on "
                + $"L{planeLayer} within {options.ViaSearchRadiusMeters * 1e3:g3} mm.");
        if (!layerZ.TryGetValue(pad.LayerOrder, out var padZ))
            throw new InvalidOperationException($"L{pad.LayerOrder} is not in the chain's stackup frame.");
        double z = 0.5 * (padZ.zLo + padZ.zHi), planeMid = 0.5 * (planeZ.zLo + planeZ.zHi);
        double surface = z >= planeMid ? planeZ.zHi : planeZ.zLo;
        var via = best.Via.Position;
        var segments = new List<TraceSegment3D>();
        if ((via - pad.Center).Length > best.Via.Diameter / 2)
        {
            double width = Math.Min(pad.Size, Math.Max(2 * best.Via.Diameter, 0.25e-3));
            segments.Add(new TraceSegment3D(new Vector3D(pad.Center.X, pad.Center.Y, z), new Vector3D(via.X, via.Y, z), width, padZ.zHi - padZ.zLo));
        }
        segments.Add(new TraceSegment3D(new Vector3D(via.X, via.Y, z), new Vector3D(via.X, via.Y, surface),
            best.Via.Diameter + mesh.ViaPlatingThickness, mesh.ViaPlatingThickness, SegmentProfile.RoundTube));
        if (!up) return segments;
        segments.Reverse();
        return segments.Select(s => s with { Start = s.End, End = s.Start }).ToList();
    }
}
