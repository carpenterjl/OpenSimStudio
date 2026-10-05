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

    /// <summary>Holes in the plane overlap smaller than this are filled [m²]: via clearance
    /// holes, which the mesh cannot afford one by one. A setting of this program.</summary>
    public double HoleFillAreaSquareMeters { get; init; } = 4e-6;

    /// <summary>How far from a pad its via to the plane is looked for [m].</summary>
    public double ViaSearchRadiusMeters { get; init; } = 3e-3;

    /// <summary>Outline detail below this is smoothed away before meshing [m].</summary>
    public double OutlineToleranceMeters { get; init; } = 50e-6;
}

/// <summary>Where a pad's current enters the plane pair.</summary>
public sealed record PlaneSite(Point2 Position, double RadiusMeters);

/// <summary>A capacitor found between the power and the ground net.</summary>
/// <param name="Site">Where it connects to the plane pair; null when it does not
/// (<paramref name="Problem"/> says why).</param>
public sealed record BoardCapacitor(string RefDes, string? PartName, Point2 Position,
    PlaneSite? Site, double MountingInductanceHenries, string? Problem);

/// <summary>The plane pair of a power and a ground net as the board has it.</summary>
public sealed record PdnBoardModel
{
    public required string PowerNet { get; init; }
    public required string GroundNet { get; init; }
    public required int PowerLayer { get; init; }
    public required int GroundLayer { get; init; }
    public required IReadOnlyList<Polygon2> Shape { get; init; }
    public required double SeparationMeters { get; init; }
    public required double RelativePermittivity { get; init; }
    public required double LossTangent { get; init; }
    public required double UpperCopperThicknessMeters { get; init; }
    public required double LowerCopperThicknessMeters { get; init; }
    public required IReadOnlyList<BoardCapacitor> Capacitors { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public double AreaSquareMeters => Shape.Sum(p => p.Area());
}

/// <summary>
/// Reads a PDN problem off a board: the plane pair of a power and a ground net, the
/// capacitors between the two nets with where and through how much inductance each reaches
/// the planes, and the plane ports of a named component.
/// </summary>
public static class PdnBoard
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();
    private static readonly Regex CapacitorRefDes = new(@"^C\d", RegexOptions.Compiled);

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
        int filled = 0, dropped = 0;
        var shape = new List<Polygon2>();
        foreach (var polygon in PolygonCleaner.Clean(best.Shape, options.OutlineToleranceMeters))
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
        if (shape.Count == 0)
            throw new InvalidOperationException(
                $"The overlap of the {powerName} and {groundName} planes is below {options.MinPlaneAreaSquareMeters * 1e6:g3} mm².");
        if (filled > 0)
            notes.Add($"{filled} hole(s) under {options.HoleFillAreaSquareMeters * 1e6:g3} mm² in the plane overlap "
                + "(via clearances) were filled: the perforation's small rise in plane inductance is not modelled.");
        if (dropped > 0)
            notes.Add($"{dropped} separate overlap area(s) under {options.MinPlaneAreaSquareMeters * 1e6:g3} mm² were left out.");

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
        if (lower - upper > 1)
            notes.Add($"The planes are on L{upper} and L{lower} with {lower - upper - 1} copper layer(s) between them; "
                + "that copper is not in the model, and the dielectrics are combined in series.");
        notes.Add($"Stackup {stackup.Source}: planes L{best.P} ({powerName}) and L{best.G} ({groundName}), "
            + $"{separation * 1e6:g4} µm apart.");

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
            Notes = notes
        };

        var capacitors = FindCapacitors(board, index, stackup, power, ground, model, options);
        if (!board.Pads.Any(p => p.ComponentRef is not null))
            notes.Add("The board file names no components (no IPC-2581 component data, no Gerber X2 attributes), "
                + "so no capacitors could be found on it.");
        return model with { Capacitors = capacitors };
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
                if (SiteFor(pad, net!, own, options) is { } found && OnPlanes(planes, found.Site)
                    && !sites.Any(s => (s.Position - found.Site.Position).Length < 1.2 * (s.RadiusMeters + found.Site.RadiusMeters)))
                    sites.Add(found.Site);
            }
            if (sites.Count > 0) break;
        }
        if (sites.Count == 0)
            throw new InvalidOperationException(
                $"No pad of '{refDes}' on {model.PowerNet} or {model.GroundNet} reaches the plane pair through a via "
                + $"within {options.ViaSearchRadiusMeters * 1e3:g3} mm.");
        return sites;
    }

    private static CopperNet? NetAt(BoardLayoutIndex index, CopperPad pad) =>
        index.IslandAt(pad.LayerOrder, pad.Center) is { } island ? index.NetOf(island) : null;

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
    private static (PlaneSite Site, Point2 Via, double Diameter)? SiteFor(CopperPad pad, CopperNet net,
        int planeLayer, PdnBoardOptions options)
    {
        if (pad.LayerOrder == planeLayer)
            return (new PlaneSite(pad.Center, Math.Max(pad.Size / 2, 50e-6)), pad.Center, pad.Size);
        ViaBridge? best = null;
        double nearest = options.ViaSearchRadiusMeters;
        foreach (var bridge in net.StitchingVias)
        {
            if (!bridge.Layers.Contains(pad.LayerOrder) || !bridge.Layers.Contains(planeLayer)) continue;
            double distance = (bridge.Via.Position - pad.Center).Length;
            if (distance <= nearest) { nearest = distance; best = bridge; }
        }
        if (best is null) return null;
        return (new PlaneSite(best.Via.Position, best.Via.Diameter / 2), best.Via.Position, best.Via.Diameter);
    }

    private static IReadOnlyList<BoardCapacitor> FindCapacitors(PcbBoard board, BoardLayoutIndex index,
        BoardStackup stackup, CopperNet power, CopperNet ground, PdnBoardModel model, PdnBoardOptions options)
    {
        var planes = new PolygonSetIndex(model.Shape);
        var result = new List<BoardCapacitor>();
        foreach (var part in board.Pads.Where(p => p.ComponentRef is not null && CapacitorRefDes.IsMatch(p.ComponentRef))
                     .GroupBy(p => p.ComponentRef!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var onPower = part.FirstOrDefault(p => ReferenceEquals(NetAt(index, p), power));
            var onGround = part.FirstOrDefault(p => ReferenceEquals(NetAt(index, p), ground));
            if (onPower is null || onGround is null) continue;
            var centre = (onPower.Center + onGround.Center) * 0.5;
            string? partName = onPower.PartName ?? onGround.PartName;

            var a = SiteFor(onPower, power, model.PowerLayer, options);
            var b = SiteFor(onGround, ground, model.GroundLayer, options);
            if (a is null || b is null)
            {
                result.Add(new BoardCapacitor(part.Key, partName, centre, null, 0,
                    $"no via from its {(a is null ? model.PowerNet : model.GroundNet)} pad to the plane within "
                    + $"{options.ViaSearchRadiusMeters * 1e3:g3} mm"));
                continue;
            }
            int layer = onPower.LayerOrder;
            if (onGround.LayerOrder != layer)
            {
                result.Add(new BoardCapacitor(part.Key, partName, centre, null, 0, "its pads are on two layers"));
                continue;
            }
            int upper = Math.Min(model.PowerLayer, model.GroundLayer), lower = Math.Max(model.PowerLayer, model.GroundLayer);
            if (layer > upper && layer < lower)
            {
                result.Add(new BoardCapacitor(part.Key, partName, centre, null, 0, "it sits between the two planes"));
                continue;
            }
            // The nearer plane carries the return under the mounting; the via of the other
            // net goes on through it and is where the current enters the pair.
            int near = layer <= upper ? upper : lower;
            bool powerCrosses = model.PowerLayer != near;
            var site = powerCrosses ? a.Value.Site : b.Value.Site;
            if (!OnPlanes(planes, site))
            {
                result.Add(new BoardCapacitor(part.Key, partName, centre, null, 0,
                    "its via does not come down over the plane pair"));
                continue;
            }

            double height = 0.5 * stackup.CopperThicknessOf(layer);
            int step = near > layer ? 1 : -1;
            for (int l = layer; l != near; l += step)
            {
                height += stackup.GapThicknessOf(step > 0 ? l : l - 1);
                if (l != layer) height += stackup.CopperThicknessOf(l);
            }
            double inductance = 0;
            if (layer != near)
            {
                double width = Math.Min(Math.Min(onPower.Size, onGround.Size),
                    Math.Max(2 * Math.Max(a.Value.Diameter, b.Value.Diameter), 0.25e-3));
                inductance = DecapMounting.LoopInductance(new MountingGeometry(
                    onPower.Center, a.Value.Via, a.Value.Diameter, onGround.Center, b.Value.Via, b.Value.Diameter,
                    height, width, stackup.CopperThicknessOf(layer)));
            }
            result.Add(new BoardCapacitor(part.Key, partName, centre, site, inductance, null));
        }
        return result;
    }

    /// <summary>
    /// The PDN problem for a load on this board: the load's plane ports, every found
    /// capacitor with the model <paramref name="modelFor"/> gives it (null leaves it off the
    /// board), and optionally a regulator at a component's pads.
    /// </summary>
    public static PdnSetup Setup(PcbBoard board, CopperNet power, CopperNet ground, PdnBoardModel model,
        string loadRefDes, Func<BoardCapacitor, CapacitorModel?> modelFor,
        (string RefDes, double ResistanceOhms, double InductanceHenries)? regulator = null,
        PdnBoardOptions? options = null)
    {
        var ports = new List<PlanePort>();
        int PortAt(PlaneSite site, string name)
        {
            for (int k = 0; k < ports.Count; k++)
                if ((ports[k].Center - site.Position).Length < 1.2 * (ports[k].RadiusMeters + site.RadiusMeters))
                    return k;
            ports.Add(new PlanePort(name, site.Position, site.RadiusMeters));
            return ports.Count - 1;
        }

        var loadSites = SitesOf(board, loadRefDes, power, ground, model, options);
        var loadPorts = loadSites.Select((s, k) => PortAt(s, loadSites.Count == 1 ? loadRefDes : $"{loadRefDes}.{k + 1}"))
            .Distinct().ToList();

        var regulators = new List<Regulator>();
        if (regulator is { } r)
        {
            // The regulator's pads share its output: its impedance is split over them.
            var sites = SitesOf(board, r.RefDes, power, ground, model, options);
            var regulatorPorts = sites.Select((s, k) => PortAt(s, sites.Count == 1 ? r.RefDes : $"{r.RefDes}.{k + 1}"))
                .Distinct().ToList();
            foreach (int port in regulatorPorts)
                regulators.Add(new Regulator(r.RefDes, port, r.ResistanceOhms * regulatorPorts.Count,
                    r.InductanceHenries * regulatorPorts.Count));
        }

        var placed = new List<PlacedCapacitor>();
        foreach (var capacitor in model.Capacitors)
        {
            if (capacitor.Site is null) continue;
            if (modelFor(capacitor) is not { } part) continue;
            placed.Add(new PlacedCapacitor(capacitor.RefDes, PortAt(capacitor.Site, capacitor.RefDes), part,
                capacitor.MountingInductanceHenries));
        }

        return new PdnSetup
        {
            Planes = new PlanePairSpec
            {
                Shape = model.Shape,
                SeparationMeters = model.SeparationMeters,
                RelativePermittivity = model.RelativePermittivity,
                LossTangent = model.LossTangent,
                UpperCopperThicknessMeters = model.UpperCopperThicknessMeters,
                LowerCopperThicknessMeters = model.LowerCopperThicknessMeters,
                Ports = ports
            },
            LoadPorts = loadPorts,
            Capacitors = placed,
            Regulators = regulators
        };
    }
}
