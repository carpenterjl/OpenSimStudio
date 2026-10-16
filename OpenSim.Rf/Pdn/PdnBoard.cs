using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Pcb.Polygons;
using OpenSim.Rf.Si.Layout;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// The pads, escape traces and vias that carry a two-terminal part's current down to the
/// planes, as far as its inductance goes.
/// </summary>
/// <param name="HeightMeters">From the middle of the pad copper down to the facing surface
/// of the nearer plane.</param>
public sealed record MountingGeometry(
    Point2 PadA, Point2 ViaA, double ViaDiameterA,
    Point2 PadB, Point2 ViaB, double ViaDiameterB,
    double HeightMeters, double TraceWidthMeters, double CopperThicknessMeters);

/// <summary>
/// Inductance of a capacitor's mounting above the planes: down via A from the pad layer to
/// the nearer plane, along that plane, and up via B, with the escape traces from each pad to
/// its via. Composed from partial inductances (via barrels as round tubes, traces as bars)
/// with the plane as a perfect conductor, by image: half the inductance of the structure
/// taken together with its mirror image.
/// The gap between the two pads — the part's own body — is not in the chain; that is what
/// the part's ESL stands for.
/// </summary>
public static class DecapMounting
{
    public static double LoopInductance(MountingGeometry g)
    {
        if (!(g.HeightMeters > 0)) throw new ArgumentException("The mounting height must be positive.");
        double h = g.HeightMeters;
        Vector3D At(Point2 p, double z) => new(p.X, p.Y, z);
        bool escapeA = (g.PadA - g.ViaA).Length > 0.25 * g.ViaDiameterA;
        bool escapeB = (g.PadB - g.ViaB).Length > 0.25 * g.ViaDiameterB;

        // The structure together with its image in the plane: each barrel continues to
        // twice its height (a vertical current's image runs the same way), each trace has
        // a reversed twin below. The plane loop links half the flux of that pair structure.
        // A barrel is taken whole, so its inductance is the tube's at its doubled length
        // and not two stubby halves coupled at their centre line.
        var doubled = new List<TraceSegment3D>
        {
            new(At(g.ViaA, -h), At(g.ViaA, h), g.ViaDiameterA, 0, SegmentProfile.RoundTube)
        };
        if (escapeA) doubled.Add(new(At(g.ViaA, h), At(g.PadA, h), g.TraceWidthMeters, g.CopperThicknessMeters));
        if (escapeB) doubled.Add(new(At(g.PadB, h), At(g.ViaB, h), g.TraceWidthMeters, g.CopperThicknessMeters));
        doubled.Add(new(At(g.ViaB, h), At(g.ViaB, -h), g.ViaDiameterB, 0, SegmentProfile.RoundTube));
        if (escapeB) doubled.Add(new(At(g.ViaB, -h), At(g.PadB, -h), g.TraceWidthMeters, g.CopperThicknessMeters));
        if (escapeA) doubled.Add(new(At(g.PadA, -h), At(g.ViaA, -h), g.TraceWidthMeters, g.CopperThicknessMeters));
        return 0.5 * new LoopComposer().Compose(doubled).LoopInductance;
    }
}

public sealed record PdnBoardOptions
{
    /// <summary>The stackup to read gaps and copper from; null takes the board file's.</summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Smallest island that counts as a plane [m²].</summary>
    public double MinPlaneAreaSquareMeters { get; init; } = 100e-6;

    /// <summary>Holes in a plane smaller than this are filled [m²]: via clearance holes, which
    /// the mesh cannot afford one by one. A setting of this program.</summary>
    public double HoleFillAreaSquareMeters { get; init; } = 4e-6;

    /// <summary>How far from a pad its via to the plane is looked for [m].</summary>
    public double ViaSearchRadiusMeters { get; init; } = 3e-3;

    /// <summary>Outline detail below this is smoothed away before meshing [m].</summary>
    public double OutlineToleranceMeters { get; init; } = 50e-6;

    /// <summary>Give each open edge of the planes the fringe of the board's cross-section
    /// through it (<see cref="EdgeCrossSection"/>); off leaves the edges bare.</summary>
    public bool EdgeFringe { get; init; } = true;

    /// <summary>The load's package between its die and its pads, as one series branch.</summary>
    public double LoadPackageOhms { get; init; }

    public double LoadPackageHenries { get; init; }
}

/// <summary>Where a pad's current enters the plane pair.</summary>
public sealed record PlaneSite(Point2 Position, double RadiusMeters);

/// <summary>One via of a pad: where it is, how wide, and the stack layers whose plates of its
/// net it joins (the first, from the pad's side, is where its current enters the stack).</summary>
public sealed record PadVia(Point2 Position, double Diameter, IReadOnlyList<int> PlateLayers);

/// <summary>A part's pad with the vias that take it to the planes.</summary>
public sealed record MountPad(string Pin, Point2 Center, int Layer, string Net, double Size, IReadOnlyList<PadVia> Vias);

/// <summary>A capacitor found between the power and the ground net.</summary>
/// <param name="Site">Where it connects to the plane pair; null when it does not
/// (<paramref name="Problem"/> says why).</param>
/// <param name="MountingInductanceHenries">The loop through its pads, escape traces and vias
/// down to the outer plane, every via of each pad included.</param>
public sealed record BoardCapacitor(string RefDes, string? PartName, Point2 Position,
    PlaneSite? Site, double MountingInductanceHenries, string? Problem)
{
    /// <summary>The side it is mounted on.</summary>
    public BoardSide Side { get; init; }

    /// <summary>Its power pad and its ground pad with their vias (empty when not connected).</summary>
    public IReadOnlyList<MountPad> Pads { get; init; } = Array.Empty<MountPad>();
}

/// <summary>The planes of a power and a ground net as the board has them.</summary>
public sealed record PdnBoardModel
{
    public required string PowerNet { get; init; }
    public required string GroundNet { get; init; }

    /// <summary>The closest pair of a power and a ground plane, for the summary.</summary>
    public required int PowerLayer { get; init; }
    public required int GroundLayer { get; init; }

    /// <summary>Where those two planes overlap.</summary>
    public required IReadOnlyList<Polygon2> Shape { get; init; }
    public required double SeparationMeters { get; init; }
    public required double RelativePermittivity { get; init; }
    public required double LossTangent { get; init; }
    public required double UpperCopperThicknessMeters { get; init; }
    public required double LowerCopperThicknessMeters { get; init; }
    public required IReadOnlyList<BoardCapacitor> Capacitors { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The stack the analysis solves: every plane-sized island on the layers from the
    /// outermost power or ground plane to the other, of whatever net, with the dielectric below
    /// each layer.</summary>
    public IReadOnlyList<StackLayer> StackLayers { get; init; } = Array.Empty<StackLayer>();

    /// <summary>The board layer of each stack layer.</summary>
    public IReadOnlyList<int> StackBoardLayers { get; init; } = Array.Empty<int>();

    /// <summary>The stitching vias that join two plates of one net.</summary>
    public IReadOnlyList<StackTie> Ties { get; init; } = Array.Empty<StackTie>();

    /// <summary>The fringe of each open edge of the stack; null with the fringe off.</summary>
    public Func<FringeEdge, EdgeFringe?>? Fringe { get; init; }

    public double AreaSquareMeters => Shape.Sum(p => p.Area());
}

/// <summary>
/// Reads a PDN problem off a board: the stack of planes between a power and a ground net (every
/// plane-sized island on the layers from the outermost of their planes to the other, of
/// whatever net — another rail's plane, a ground pour — with the stitching vias that join a
/// net's plates), the capacitors between the two nets with every via of each pad, and the
/// pads of the load and the regulator. The fringe of each open edge comes from the board's
/// own cross-section through it.
/// </summary>
public static class PdnBoard
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();
    private static readonly Regex CapacitorRefDes = new(@"^C\d", RegexOptions.Compiled);

    /// <summary>The plates of one stack layer, for point queries.</summary>
    private sealed record Plate(string Net, PolygonSetIndex Index);

    /// <summary>What both <see cref="Extract"/> and <see cref="Setup"/> need of the board.</summary>
    private sealed class Context
    {
        public required PcbBoard Board { get; init; }
        public required BoardLayoutIndex Index { get; init; }
        public required BoardStackup Stackup { get; init; }
        public required PdnBoardOptions Options { get; init; }
        public required int[] BoardLayers { get; init; }               // per stack layer
        public required List<Plate>[] Plates { get; init; }            // per stack layer
        public required Dictionary<CopperPad, List<ViaBridge>> ViasOfPad { get; init; }

        public int StackLayerOf(int boardLayer) => Array.IndexOf(BoardLayers, boardLayer);

        public Plate? PlateAt(int stackLayer, Point2 p)
        {
            foreach (var plate in Plates[stackLayer])
                if (plate.Index.Contains(p)) return plate;
            return null;
        }

        /// <summary>The stack layers whose plates of the via's net the via joins.</summary>
        public List<int> PlatesJoined(ViaBridge via, string net)
        {
            var joined = new List<int>();
            for (int s = 0; s < BoardLayers.Length; s++)
                if (via.Layers.Contains(BoardLayers[s]) && PlateAt(s, via.Via.Position) is { } plate && plate.Net == net)
                    joined.Add(s);
            return joined;
        }

        /// <summary>Height of a pad layer's copper middle above the facing surface of the side's
        /// outer plate.</summary>
        public double HeightAbove(int padLayer, BoardSide side)
        {
            int outer = side == BoardSide.Top ? BoardLayers[0] : BoardLayers[^1];
            double height = 0.5 * Stackup.CopperThicknessOf(padLayer);
            int step = outer > padLayer ? 1 : -1;
            for (int l = padLayer; l != outer; l += step)
            {
                height += Stackup.GapThicknessOf(step > 0 ? l : l - 1);
                if (l != padLayer) height += Stackup.CopperThicknessOf(l);
            }
            return height;
        }
    }

    public static PdnBoardModel Extract(PcbBoard board, CopperNet power, CopperNet ground,
        PdnBoardOptions? options = null)
    {
        options ??= new PdnBoardOptions();
        if (ReferenceEquals(power, ground))
            throw new InvalidOperationException("The power and the ground net are the same net.");
        var stackup = options.Stackup ?? BoardStackup.FromBoard(board);
        var index = new BoardLayoutIndex(board);
        string powerName = BoardLayoutIndex.NameOf(power), groundName = BoardLayoutIndex.NameOf(ground);

        CopperIsland? PlaneOn(CopperNet net, int layer) => net.Islands
            .Where(i => i.LayerOrder == layer && i.Area >= options.MinPlaneAreaSquareMeters)
            .MaxBy(i => i.Area);
        var powerLayers = power.Layers.Where(l => PlaneOn(power, l) is not null).ToList();
        var groundLayers = ground.Layers.Where(l => PlaneOn(ground, l) is not null).ToList();
        if (powerLayers.Count == 0 || groundLayers.Count == 0)
            throw new InvalidOperationException(
                $"{(powerLayers.Count == 0 ? powerName : groundName)} has no copper area of at least "
                + $"{options.MinPlaneAreaSquareMeters * 1e6:g3} mm² on any layer, so there is no plane pair to solve. "
                + "A rail routed as traces has no plane impedance; its inductance is a loop extraction.");

        // The closest two layers; among equally close ones, the pair that overlaps most.
        (int P, int G, IReadOnlyList<Polygon2> Shape, double Area) best = (0, 0, Array.Empty<Polygon2>(), 0);
        int closest = int.MaxValue;
        foreach (int p in powerLayers)
            foreach (int g in groundLayers)
            {
                if (p == g) continue;
                int apart = Math.Abs(p - g);
                if (apart > closest) continue;
                var overlap = Ops.Intersect(new[] { PlaneOn(power, p)!.Shape },
                    Polygon2.OrientedRings(PlaneOn(ground, g)!.Shape));
                double area = overlap.Sum(o => o.Area());
                if (apart < closest ? area > 0 : area > best.Area)
                {
                    closest = apart;
                    best = (p, g, overlap, area);
                }
            }
        if (best.Area <= 0)
            throw new InvalidOperationException(
                $"The planes of {powerName} and {groundName} do not lie over one another on any two layers.");

        var notes = new List<string>();
        var shape = FilledShape(best.Shape, options, notes);
        if (shape.Count == 0)
            throw new InvalidOperationException(
                $"The overlap of the {powerName} and {groundName} planes is below {options.MinPlaneAreaSquareMeters * 1e6:g3} mm².");

        int upper = Math.Min(best.P, best.G), lower = Math.Max(best.P, best.G);
        double separation = 0, overEps = 0, lossOverEps = 0;
        for (int gap = upper; gap < lower; gap++)
        {
            double d = stackup.GapThicknessOf(gap);
            separation += d;
            overEps += d / stackup.PermittivityOf(gap);
            lossOverEps += d * stackup.LossTangentOf(gap) / stackup.PermittivityOf(gap);
        }
        for (int layer = upper + 1; layer < lower; layer++) separation += stackup.CopperThicknessOf(layer);

        // ------------------------------------------------------------ the stack
        int lo = powerLayers.Concat(groundLayers).Min(), hi = powerLayers.Concat(groundLayers).Max();
        var stackLayers = new List<StackLayer>();
        var boardLayers = Enumerable.Range(lo, hi - lo + 1).ToArray();
        var plates = new List<Plate>[boardLayers.Length];
        int others = 0;
        foreach (int layer in boardLayers)
        {
            var layerPlates = new List<StackPlate>();
            plates[layer - lo] = new List<Plate>();
            foreach (var island in board.Islands.Where(i => i.LayerOrder == layer && i.Area >= options.MinPlaneAreaSquareMeters)
                         .OrderByDescending(i => i.Area))
            {
                var filled = FilledShape(new[] { island.Shape }, options, null);
                if (filled.Count == 0) continue;
                string net = index.NameOf(island);
                if (net != powerName && net != groundName) others++;
                layerPlates.Add(new StackPlate($"{net} L{layer}", filled) { Net = net });
                plates[layer - lo].Add(new Plate(net, new PolygonSetIndex(filled)));
            }
            stackLayers.Add(new StackLayer
            {
                Name = $"L{layer}",
                CopperThicknessMeters = stackup.CopperThicknessOf(layer),
                Plates = layerPlates,
                GapBelowMeters = layer < hi ? stackup.GapThicknessOf(layer) : 0,
                GapPermittivity = layer < hi ? stackup.PermittivityOf(layer) : 1,
                GapLossTangent = layer < hi ? stackup.LossTangentOf(layer) : 0
            });
        }
        var context = new Context
        {
            Board = board, Index = index, Stackup = stackup, Options = options,
            BoardLayers = boardLayers, Plates = plates, ViasOfPad = AssignVias(board, index, options)
        };

        var ties = new List<StackTie>();
        foreach (var net in board.Nets)
        {
            string name = BoardLayoutIndex.NameOf(net);
            foreach (var via in net.StitchingVias)
            {
                var joined = context.PlatesJoined(via, name);
                for (int k = 0; k + 1 < joined.Count; k++)
                    ties.Add(new StackTie(via.Via.Position, via.Via.Diameter / 2, joined[k], joined[k + 1])
                        { Name = $"{name} via at ({via.Via.Position.X * 1e3:f2}, {via.Via.Position.Y * 1e3:f2}) mm" });
            }
        }

        notes.Add($"Stackup {stackup.Source}: planes L{best.P} ({powerName}) and L{best.G} ({groundName}) are the closest pair, "
            + $"{separation * 1e6:g4} µm apart.");
        notes.Add($"The stack runs from L{lo} to L{hi}: {stackLayers.Sum(l => l.Plates.Count)} plate(s)"
            + (others > 0 ? $", {others} of them of other nets (floating unless stitched to the rail)" : "")
            + $", {ties.Count} stitching via join(s) between plates of one net.");

        var model = new PdnBoardModel
        {
            PowerNet = powerName,
            GroundNet = groundName,
            PowerLayer = best.P,
            GroundLayer = best.G,
            Shape = shape,
            SeparationMeters = separation,
            RelativePermittivity = (separation - Enumerable.Range(upper + 1, Math.Max(0, lower - upper - 1))
                .Sum(stackup.CopperThicknessOf)) / overEps,
            LossTangent = overEps > 0 ? lossOverEps / overEps : 0,
            UpperCopperThicknessMeters = stackup.CopperThicknessOf(upper),
            LowerCopperThicknessMeters = stackup.CopperThicknessOf(lower),
            Capacitors = Array.Empty<BoardCapacitor>(),
            Notes = notes,
            StackLayers = stackLayers,
            StackBoardLayers = boardLayers,
            Ties = ties,
            Fringe = options.EdgeFringe ? new FringeProbe(board, index, stackup, options, boardLayers, plates).At : null
        };

        var capacitors = FindCapacitors(context, power, ground, model);
        if (!board.Pads.Any(p => p.ComponentRef is not null))
            notes.Add("The board file names no components (no IPC-2581 component data, no Gerber X2 attributes), "
                + "so no capacitors could be found on it.");
        return model with { Capacitors = capacitors };
    }

    /// <summary>A plane's outline cleaned, small holes (via clearances) filled.</summary>
    private static List<Polygon2> FilledShape(IEnumerable<Polygon2> polygons, PdnBoardOptions options, List<string>? notes)
    {
        int filled = 0, dropped = 0;
        var shape = new List<Polygon2>();
        foreach (var polygon in PolygonCleaner.Clean(polygons, options.OutlineToleranceMeters))
        {
            if (polygon.Area() < options.MinPlaneAreaSquareMeters) { dropped++; continue; }
            var holes = new List<IReadOnlyList<Point2>>();
            foreach (var hole in polygon.Holes)
            {
                if (Math.Abs(Polygon2.RingArea(hole)) < options.HoleFillAreaSquareMeters) filled++;
                else holes.Add(hole);
            }
            shape.Add(new Polygon2(polygon.Outer, holes));
        }
        if (notes is not null && filled > 0)
            notes.Add($"{filled} hole(s) under {options.HoleFillAreaSquareMeters * 1e6:g3} mm² in the planes "
                + "(via clearances) were filled: the perforation's small rise in plane inductance is not modelled.");
        if (notes is not null && dropped > 0)
            notes.Add($"{dropped} separate overlap area(s) under {options.MinPlaneAreaSquareMeters * 1e6:g3} mm² were left out.");
        return shape;
    }

    /// <summary>Each component pad's vias: the stitching vias of its net that reach its layer
    /// within the search radius, each given to the nearest such pad (so neighbouring parts do not
    /// share one).</summary>
    private static Dictionary<CopperPad, List<ViaBridge>> AssignVias(PcbBoard board, BoardLayoutIndex index, PdnBoardOptions options)
    {
        var result = new Dictionary<CopperPad, List<ViaBridge>>(ReferenceEqualityComparer.Instance);
        var padsByNet = new Dictionary<CopperNet, List<CopperPad>>(ReferenceEqualityComparer.Instance);
        foreach (var pad in board.Pads.Where(p => p.ComponentRef is not null))
            if (NetAt(index, pad) is { } net)
                (padsByNet.TryGetValue(net, out var list) ? list : padsByNet[net] = new List<CopperPad>()).Add(pad);
        foreach (var (net, pads) in padsByNet)
            foreach (var via in net.StitchingVias)
            {
                CopperPad? nearest = null;
                double distance = options.ViaSearchRadiusMeters;
                foreach (var pad in pads)
                {
                    if (!via.Layers.Contains(pad.LayerOrder)) continue;
                    double d = (via.Via.Position - pad.Center).Length;
                    if (d <= distance) { distance = d; nearest = pad; }
                }
                if (nearest is not null)
                    (result.TryGetValue(nearest, out var list) ? list : result[nearest] = new List<ViaBridge>()).Add(via);
            }
        return result;
    }

    private static CopperNet? NetAt(BoardLayoutIndex index, CopperPad pad) =>
        index.IslandAt(pad.LayerOrder, pad.Center) is { } island ? index.NetOf(island) : null;

    /// <summary>The pads of a part on the two nets, with the vias that reach a plate.</summary>
    private static List<MountPad> PadsOf(Context context, IEnumerable<CopperPad> pads, CopperNet power, CopperNet ground)
    {
        var result = new List<MountPad>();
        foreach (var pad in pads)
        {
            var net = NetAt(context.Index, pad);
            if (!ReferenceEquals(net, power) && !ReferenceEquals(net, ground)) continue;
            string name = BoardLayoutIndex.NameOf(net!);
            var vias = new List<PadVia>();
            if (context.ViasOfPad.TryGetValue(pad, out var bridges))
                foreach (var bridge in bridges.OrderBy(b => (b.Via.Position - pad.Center).Length))
                {
                    var joined = context.PlatesJoined(bridge, name);
                    if (joined.Count > 0) vias.Add(new PadVia(bridge.Via.Position, bridge.Via.Diameter, joined));
                }
            result.Add(new MountPad(pad.Pin ?? "", pad.Center, pad.LayerOrder, name, pad.Size, vias));
        }
        return result;
    }

    private static BoardSide? SideOf(Context context, int padLayer) =>
        padLayer < context.BoardLayers[0] ? BoardSide.Top : padLayer > context.BoardLayers[^1] ? BoardSide.Bottom : null;

    /// <summary>The escape trace's width: the narrower pad, but no narrower than twice the via
    /// (or a quarter millimetre) — an assumption, the trace is not read from the layout.</summary>
    private static double EscapeWidth(IReadOnlyList<MountPad> pads) =>
        Math.Min(pads.Min(p => p.Size), Math.Max(2 * pads.SelectMany(p => p.Vias).Select(v => v.Diameter).DefaultIfEmpty(0).Max(), 0.25e-3));

    /// <summary>Adds a pad, its escape traces and its barrels to a mounting network; returns the
    /// pad's node and lists the feet that go on into the stack.</summary>
    private static int AddPad(MountingNetwork network, Context context, MountPad pad, BoardSide side, double width,
        string name, Func<PadVia, int?> footPort, List<(int Node, int Port)> feet, int? shared = null)
    {
        int padNode = shared ?? network.AddNode(name);
        double height = context.HeightAbove(pad.Layer, side);
        double copper = context.Stackup.CopperThicknessOf(pad.Layer);
        int outer = side == BoardSide.Top ? 0 : context.BoardLayers.Length - 1;
        foreach (var via in pad.Vias)
        {
            int top = padNode;
            if ((via.Position - pad.Center).Length > 0.25 * via.Diameter)
            {
                top = network.AddNode($"{name} via top");
                network.AddTrace(pad.Center, via.Position, height, width, copper, padNode, top);
            }
            int foot = MountingNetwork.Plate;
            int first = side == BoardSide.Top ? via.PlateLayers[0] : via.PlateLayers[^1];
            if (first != outer && footPort(via) is { } port)
            {
                foot = network.AddNode($"{name} via foot");
                feet.Add((foot, port));
            }
            network.AddBarrel(via.Position, via.Diameter, height, top, foot);
        }
        return padNode;
    }

    private static IReadOnlyList<BoardCapacitor> FindCapacitors(Context context, CopperNet power, CopperNet ground, PdnBoardModel model)
    {
        var result = new List<BoardCapacitor>();
        foreach (var part in context.Board.Pads.Where(p => p.ComponentRef is not null && CapacitorRefDes.IsMatch(p.ComponentRef))
                     .GroupBy(p => p.ComponentRef!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var onPower = part.FirstOrDefault(p => ReferenceEquals(NetAt(context.Index, p), power));
            var onGround = part.FirstOrDefault(p => ReferenceEquals(NetAt(context.Index, p), ground));
            if (onPower is null || onGround is null) continue;
            var centre = (onPower.Center + onGround.Center) * 0.5;
            string? partName = onPower.PartName ?? onGround.PartName;
            BoardCapacitor Refused(string why) => new(part.Key, partName, centre, null, 0, why);

            if (onGround.LayerOrder != onPower.LayerOrder) { result.Add(Refused("its pads are on two layers")); continue; }
            if (SideOf(context, onPower.LayerOrder) is not { } side) { result.Add(Refused("it sits between the planes")); continue; }
            var pads = PadsOf(context, new[] { onPower, onGround }, power, ground);
            var missing = pads.FirstOrDefault(p => p.Vias.Count == 0);
            if (missing is not null)
            {
                result.Add(Refused($"no via from its {missing.Net} pad to the plane within {context.Options.ViaSearchRadiusMeters * 1e3:g3} mm"));
                continue;
            }
            int outer = side == BoardSide.Top ? 0 : context.BoardLayers.Length - 1;
            if (context.PlateAt(outer, centre) is null)
            {
                result.Add(Refused($"L{context.BoardLayers[outer]} has no plane under it"));
                continue;
            }

            // Its own loop down to the outer plane, every via grounded there.
            var network = new MountingNetwork();
            var feet = new List<(int, int)>();
            double width = EscapeWidth(pads);
            int a = AddPad(network, context, pads[0], side, width, "A", _ => null, feet);
            int b = AddPad(network, context, pads[1], side, width, "B", _ => null, feet);
            double loop = network.LoopInductance(a, b);
            // The via whose current goes on into the stack: on a pad of the other net than the outer plane's.
            var crossing = pads.SelectMany(p => p.Vias).FirstOrDefault(v => (side == BoardSide.Top ? v.PlateLayers[0] : v.PlateLayers[^1]) != outer)
                           ?? pads[0].Vias[0];
            result.Add(new BoardCapacitor(part.Key, partName, centre, new PlaneSite(crossing.Position, crossing.Diameter / 2), loop, null)
                { Side = side, Pads = pads });
        }
        return result;
    }

    /// <summary>
    /// Where the pads of a component reach the plane pair: one site per pad of the net whose
    /// via has to cross the gap between the planes (the net of the farther plane, seen from
    /// the component's side), or of the other net when the part has no such pad.
    /// </summary>
    public static IReadOnlyList<PlaneSite> SitesOf(PcbBoard board, string refDes, CopperNet power,
        CopperNet ground, PdnBoardModel model, PdnBoardOptions? options = null)
    {
        options ??= new PdnBoardOptions();
        var index = new BoardLayoutIndex(board);
        var pads = board.Pads.Where(p => string.Equals(p.ComponentRef, refDes, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pads.Count == 0)
            throw new InvalidOperationException($"The board has no pads of a component '{refDes}'.");
        var planes = new PolygonSetIndex(model.Shape);
        var sites = new List<PlaneSite>();
        foreach (bool crossing in new[] { true, false })
        {
            foreach (var pad in pads)
            {
                var net = NetAt(index, pad);
                bool isPower = ReferenceEquals(net, power), isGround = ReferenceEquals(net, ground);
                if (!isPower && !isGround) continue;
                int own = isPower ? model.PowerLayer : model.GroundLayer;
                int other = isPower ? model.GroundLayer : model.PowerLayer;
                bool crosses = Math.Abs(own - pad.LayerOrder) > Math.Abs(other - pad.LayerOrder);
                if (crosses != crossing) continue;
                if (SiteFor(pad, net!, own, options) is { } found && OnPlanes(planes, found)
                    && !sites.Any(s => (s.Position - found.Position).Length < 1.2 * (s.RadiusMeters + found.RadiusMeters)))
                    sites.Add(found);
            }
            if (sites.Count > 0) break;
        }
        if (sites.Count == 0)
            throw new InvalidOperationException(
                $"No pad of '{refDes}' on {model.PowerNet} or {model.GroundNet} reaches the plane pair through a via "
                + $"within {options.ViaSearchRadiusMeters * 1e3:g3} mm.");
        return sites;
    }

    private static bool OnPlanes(PolygonSetIndex planes, PlaneSite site)
    {
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI * i / 4;
            if (!planes.Contains(new Point2(site.Position.X + 1.3 * site.RadiusMeters * Math.Cos(angle),
                    site.Position.Y + 1.3 * site.RadiusMeters * Math.Sin(angle))))
                return false;
        }
        return true;
    }

    /// <summary>The via that takes a pad's net to its plane layer, nearest the pad; or the
    /// pad itself when it is on the plane layer.</summary>
    private static PlaneSite? SiteFor(CopperPad pad, CopperNet net, int planeLayer, PdnBoardOptions options)
    {
        if (pad.LayerOrder == planeLayer)
            return new PlaneSite(pad.Center, Math.Max(pad.Size / 2, 50e-6));
        ViaBridge? best = null;
        double nearest = options.ViaSearchRadiusMeters;
        foreach (var bridge in net.StitchingVias)
        {
            if (!bridge.Layers.Contains(pad.LayerOrder) || !bridge.Layers.Contains(planeLayer)) continue;
            double distance = (bridge.Via.Position - pad.Center).Length;
            if (distance <= nearest) { nearest = distance; best = bridge; }
        }
        return best is null ? null : new PlaneSite(best.Via.Position, best.Via.Diameter / 2);
    }

    /// <summary>
    /// The PDN problem for a load on this board: the stack with a port at every via that goes
    /// on into it past the outer plane, every found capacitor on its pads with the model
    /// <paramref name="modelFor"/> gives it (null leaves it off the board), the load on its pads
    /// behind its package, and optionally a regulator on its pads — with a loop, sensing at its
    /// own pads or at the load's.
    /// </summary>
    public static PdnSetup Setup(PcbBoard board, CopperNet power, CopperNet ground, PdnBoardModel model,
        string loadRefDes, Func<BoardCapacitor, CapacitorModel?> modelFor,
        (string RefDes, double ResistanceOhms, double InductanceHenries)? regulator = null,
        PdnBoardOptions? options = null, RegulatorLoop? regulatorLoop = null, bool senseAtLoad = false)
    {
        options ??= new PdnBoardOptions();
        if (model.StackLayers.Count == 0)
            throw new InvalidOperationException("The board model carries no plane stack; extract it again.");
        var index = new BoardLayoutIndex(board);
        var context = new Context
        {
            Board = board, Index = index, Stackup = options.Stackup ?? BoardStackup.FromBoard(board), Options = options,
            BoardLayers = model.StackBoardLayers.ToArray(),
            Plates = model.StackLayers.Select(l => l.Plates.Select(p => new Plate(p.Net ?? "", new PolygonSetIndex(p.Shape))).ToList()).ToArray(),
            ViasOfPad = AssignVias(board, index, options)
        };

        var ports = new List<StackPort>();
        var sites = new List<PlanePort>();
        int PortAt(PadVia via, string name, BoardSide side)
        {
            int outer = side == BoardSide.Top ? 0 : context.BoardLayers.Length - 1;
            int plate = side == BoardSide.Top ? via.PlateLayers[0] : via.PlateLayers[^1];
            var site = sites.FirstOrDefault(s => (s.Center - via.Position).Length < 1.2 * (s.RadiusMeters + via.Diameter / 2));
            if (site is null)
            {
                site = new PlanePort(name, via.Position, via.Diameter / 2);
                sites.Add(site);
            }
            int existing = ports.FindIndex(p => ReferenceEquals(p.Site, site) && p.UpperLayer == plate && p.LowerLayer == outer);
            if (existing >= 0) return existing;
            ports.Add(new StackPort(site, plate, outer));
            return ports.Count - 1;
        }

        var networks = new Dictionary<BoardSide, MountingNetwork>();
        var feet = new List<PortFoot>();
        MountNode Pad(MountPad pad, BoardSide side, double width, string name)
        {
            if (!networks.TryGetValue(side, out var network)) networks[side] = network = new MountingNetwork();
            var local = new List<(int Node, int Port)>();
            int node = AddPad(network, context, pad, side, width, name, v => PortAt(v, name, side), local);
            foreach (var (n, p) in local) feet.Add(new PortFoot(new MountNode(side, n), p));
            return new MountNode(side, node);
        }

        // A part's pads on one net tied to one node (the part ties them).
        (MountNode Plus, MountNode Minus, List<int> Ports) Part(string refDes, string role)
        {
            var padsOfPart = board.Pads.Where(p => string.Equals(p.ComponentRef, refDes, StringComparison.OrdinalIgnoreCase)).ToList();
            if (padsOfPart.Count == 0) throw new InvalidOperationException($"The board has no pads of a component '{refDes}'.");
            var pads = PadsOf(context, padsOfPart, power, ground).Where(p => p.Vias.Count > 0).ToList();
            var plusPads = pads.Where(p => p.Net == model.PowerNet).ToList();
            var minusPads = pads.Where(p => p.Net == model.GroundNet).ToList();
            if (plusPads.Count == 0 || minusPads.Count == 0)
                throw new InvalidOperationException(
                    $"The {role} {refDes} has no {(plusPads.Count == 0 ? model.PowerNet : model.GroundNet)} pad with a via to the planes "
                    + $"within {options.ViaSearchRadiusMeters * 1e3:g3} mm.");
            var side = SideOf(context, plusPads[0].Layer)
                       ?? throw new InvalidOperationException($"The {role} {refDes} sits between the planes.");
            int outer = side == BoardSide.Top ? 0 : context.BoardLayers.Length - 1;
            var centre = new Point2(pads.Average(p => p.Center.X), pads.Average(p => p.Center.Y));
            if (context.PlateAt(outer, centre) is null)
                throw new InvalidOperationException(
                    $"The {role} {refDes} is not over a plane on L{context.BoardLayers[outer]}, the plane its mounting is measured against.");
            double width = EscapeWidth(pads);
            int before = feet.Count;
            // The pads of one net share a node: the first pad's, the others joined to it by their own traces.
            var plus = Pad(plusPads[0], side, width, $"{refDes} {model.PowerNet}");
            var network = networks[side];
            foreach (var pad in plusPads.Skip(1))
                JoinPad(network, context, pad, side, width, $"{refDes} {model.PowerNet}", plus.Node, v => PortAt(v, refDes, side), feet);
            var minus = Pad(minusPads[0], side, width, $"{refDes} {model.GroundNet}");
            foreach (var pad in minusPads.Skip(1))
                JoinPad(network, context, pad, side, width, $"{refDes} {model.GroundNet}", minus.Node, v => PortAt(v, refDes, side), feet);
            return (plus, minus, feet.Skip(before).Select(f => f.Port).Distinct().ToList());
        }

        var (loadPlus, loadMinus, loadPorts) = Part(loadRefDes, "load");
        var capacitors = new List<MountedCapacitor>();
        foreach (var capacitor in model.Capacitors)
        {
            if (capacitor.Site is null || capacitor.Pads.Count < 2) continue;
            if (modelFor(capacitor) is not { } part) continue;
            double width = EscapeWidth(capacitor.Pads);
            var a = Pad(capacitor.Pads[0], capacitor.Side, width, $"{capacitor.RefDes}.{capacitor.Pads[0].Pin}");
            var b = Pad(capacitor.Pads[1], capacitor.Side, width, $"{capacitor.RefDes}.{capacitor.Pads[1].Pin}");
            capacitors.Add(new MountedCapacitor(capacitor.RefDes, a, b, part));
        }
        var regulators = new List<MountedRegulator>();
        if (regulator is { } r)
        {
            var (plus, minus, _) = Part(r.RefDes, "regulator");
            regulators.Add(new MountedRegulator(r.RefDes, plus, minus, r.ResistanceOhms, r.InductanceHenries)
            {
                Loop = regulatorLoop,
                SensePlus = senseAtLoad ? loadPlus : null,
                SenseMinus = senseAtLoad ? loadMinus : null
            });
        }

        return new PdnSetup
        {
            Stack = new PlaneStackSpec
            {
                Layers = model.StackLayers,
                Ports = ports,
                Ties = model.Ties,
                Fringe = options.EdgeFringe ? model.Fringe : null
            },
            Mounting = new PdnMounting
            {
                Top = networks.GetValueOrDefault(BoardSide.Top),
                Bottom = networks.GetValueOrDefault(BoardSide.Bottom),
                Feet = feet,
                Capacitors = capacitors,
                Load = new MountedLoad(loadRefDes, loadPlus, loadMinus)
                {
                    PackageOhms = options.LoadPackageOhms, PackageHenries = options.LoadPackageHenries, Ports = loadPorts
                },
                Regulators = regulators
            }
        };
    }

    /// <summary>Another pad of a part on the same net: its own escape traces and vias, from the
    /// part's node (the part ties its pads of one net together).</summary>
    private static void JoinPad(MountingNetwork network, Context context, MountPad pad, BoardSide side, double width,
        string name, int partNode, Func<PadVia, int?> footPort, List<PortFoot> feet)
    {
        var local = new List<(int Node, int Port)>();
        AddPad(network, context, pad, side, width, name, footPort, local, partNode);
        foreach (var (n, p) in local) feet.Add(new PortFoot(new MountNode(side, n), p));
    }

    /// <summary>
    /// The fringe of the stack's open edges on a board: at each edge, the board's cross-section
    /// along the outward normal — the plates that end there and how far any goes on past it, the
    /// plane copper of the layers outside the stack (held at AC ground), the board's own edge, and
    /// the stack's width there (half the chord inward, the section's mirror wall) — solved once
    /// per kind of edge, lengths rounded to a quarter of the board's thickness and the width to
    /// steps of √2.
    /// </summary>
    private sealed class FringeProbe
    {
        private readonly PolygonSetIndex _outline;
        private readonly int[] _stackBoardLayers;
        private readonly List<Plate>[] _plates;
        private readonly Dictionary<int, List<PolygonSetIndex>> _grounds = new();     // board layer → plane islands
        private readonly SectionLayer[] _section;
        private readonly double _thickness;
        private readonly Dictionary<string, EdgeFringe> _cache = new();
        private readonly object _lock = new();

        public FringeProbe(PcbBoard board, BoardLayoutIndex index, BoardStackup stackup, PdnBoardOptions options,
            int[] stackBoardLayers, List<Plate>[] plates)
        {
            _outline = new PolygonSetIndex(board.Outline);
            _stackBoardLayers = stackBoardLayers;
            _plates = plates;
            // The board's layers: what its copper shows, or what the stackup describes when that is more.
            int layers = Math.Max(index.LayerCount, stackup.GapThickness is { Count: > 0 } gaps ? gaps.Keys.Max() + 1 : 0);
            foreach (var island in board.Islands.Where(i => i.Area >= options.MinPlaneAreaSquareMeters
                                                            && Array.IndexOf(stackBoardLayers, i.LayerOrder) < 0))
                (_grounds.TryGetValue(island.LayerOrder, out var list) ? list : _grounds[island.LayerOrder] = new List<PolygonSetIndex>())
                    .Add(new PolygonSetIndex(new[] { island.Shape }));
            _section = Enumerable.Range(1, layers)
                .Select(l => new SectionLayer(stackup.CopperThicknessOf(l), l < layers ? stackup.GapThicknessOf(l) : 0,
                    l < layers ? stackup.PermittivityOf(l) : 1))
                .ToArray();
            _thickness = stackup.TotalThickness(layers);
        }

        public EdgeFringe? At(FringeEdge edge)
        {
            double h = _thickness, quarter = h / 4, step = h / 8, cap = 8 * h;
            Point2 Along(double s) => new(edge.Midpoint.X + edge.Outward.X * s, edge.Midpoint.Y + edge.Outward.Y * s);
            double Rounded(double v) => Math.Round(v / quarter) * quarter;
            // How far copper (or the board) goes on past the edge: 0 when it stops there.
            double Reach(Func<Point2, bool> has)
            {
                if (!has(Along(1e-6))) return 0;
                for (double s = step; s < cap; s += step)
                    if (!has(Along(s))) return Rounded(s - step / 2);
                return cap;
            }

            var inside = Along(-1e-6);
            var plates = new List<EdgeCrossSection.EdgePlate>();
            var key = new StringBuilder();
            foreach (int layer in edge.Layers)
            {
                var plate = _plates[layer].FirstOrDefault(p => p.Index.Contains(inside));
                if (plate is null) return null;
                double reach = Reach(q => plate.Index.Contains(q));
                plates.Add(new EdgeCrossSection.EdgePlate(_stackBoardLayers[layer] - 1, layer, reach));
                key.Append(CultureInfo.InvariantCulture, $"P{layer}:{reach / quarter:f0};");
            }
            var grounds = new List<(int Layer, double Reach)>();
            foreach (var (layer, islands) in _grounds.OrderBy(g => g.Key))
            {
                var island = islands.FirstOrDefault(i => i.Contains(inside));
                if (island is null) continue;
                double reach = Reach(q => island.Contains(q));
                grounds.Add((layer - 1, reach));
                key.Append(CultureInfo.InvariantCulture, $"G{layer}:{reach / quarter:f0};");
            }
            double boardEdge = Reach(q => _outline.Contains(q));

            // Half the stack's width inward along the normal (where the section's mirror wall goes).
            double chord = 0;
            for (double s = step; s < 200 * h; s += Math.Max(step, 0.02 * s))
            {
                var q = Along(-s);
                if (edge.Layers.Any(l => !_plates[l].Any(p => p.Index.Contains(q)))) break;
                chord = s;
            }
            double halfWidth = Math.Clamp(chord / 2, 2 * h, 40 * h);
            halfWidth = h * Math.Pow(2, Math.Round(2 * Math.Log2(halfWidth / h)) / 2);
            key.Append(CultureInfo.InvariantCulture, $"E{boardEdge / quarter:f0};W{halfWidth / h:f3}");

            string k = key.ToString();
            lock (_lock)
            {
                if (_cache.TryGetValue(k, out var cached)) return cached;
                var fringe = EdgeCrossSection.FringeOf(_section, plates,
                    grounds.Select(g => new EdgeCrossSection.EdgeGround(g.Layer, -halfWidth, g.Reach)).ToList(), boardEdge, halfWidth);
                _cache[k] = fringe;
                return fringe;
            }
        }
    }
}
