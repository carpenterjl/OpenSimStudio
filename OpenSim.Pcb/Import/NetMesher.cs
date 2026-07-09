using OpenSim.Core.Model;
using OpenSim.Pcb.Extrude;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Import;

/// <summary>Options for meshing a single selected net.</summary>
public sealed record NetMeshOptions
{
    public double TargetEdgeLength { get; init; }                  // 0 = auto
    public double CopperThickness { get; init; } = PcbStackup.DefaultCopperThickness;
    public string CopperMaterialName { get; init; } = "Copper (annealed)";

    /// <summary>Per-layer copper thickness (layer order → metres); falls back to <see cref="CopperThickness"/>.</summary>
    public IReadOnlyDictionary<int, double>? LayerThickness { get; init; }

    /// <summary>
    /// Per-gap dielectric thickness (upper copper layer order → metres of dielectric below it);
    /// falls back to <see cref="DefaultDielectricThickness"/>. This is the true z-separation a
    /// via barrel is extruded through, so a trace on L1 and one on L2 sit at their real heights.
    /// </summary>
    public IReadOnlyDictionary<int, double>? DielectricGapThickness { get; init; }

    /// <summary>Dielectric thickness for gaps not listed in <see cref="DielectricGapThickness"/>.</summary>
    public double DefaultDielectricThickness { get; init; } = PcbStackup.DefaultBoardThickness;

    /// <summary>
    /// Copper wall thickness of a plated via barrel [m]. A real via is a hollow annulus —
    /// the drilled bore stays open and only this plated wall conducts — so barrels are
    /// meshed as annuli, not solid cylinders. IPC Class 2 nominal wall is ≈ 20–25 µm.
    /// </summary>
    public double ViaPlatingThickness { get; init; } = 25e-6;

}

/// <summary>
/// Meshes one selected copper net into a solvable <see cref="Body"/>. A net is a small,
/// connected conductor (unlike a whole layer), so it meshes quickly. A single-layer net is
/// extruded copper-only. A via-stitched multi-layer net keeps every layer at its true z and
/// connects them with solid copper via barrels (one shared 2D triangulation extruded through
/// the stackup, so the copper on adjacent layers and the barrels between them share nodes and
/// current flows across layers) — layers are never collapsed onto one plane.
/// </summary>
public sealed class NetMesher
{
    /// <summary>A selectable pad electrode on the meshed net: its tagged face id, centre, and layer.</summary>
    public sealed record PadElectrode(int FaceId, Point2 Center, int LayerOrder)
    {
        /// <summary>Human label for the electrode picker, e.g. "L1 pad (12.4, 3.20) mm".</summary>
        public string Label => $"L{LayerOrder} pad ({Center.X * 1e3:g3}, {Center.Y * 1e3:g3}) mm";
    }

    public sealed record Result(Body Body, IReadOnlyList<PadElectrode> Pads, IReadOnlyList<string> Warnings);

    public Result MeshNet(CopperNet net, IReadOnlyList<CopperPad>? boardPads = null, NetMeshOptions? options = null)
    {
        options ??= new NetMeshOptions();
        boardPads ??= Array.Empty<CopperPad>();
        var warnings = new List<string>();
        if (net.IsSingleLayer)
            return MeshSingleLayer(net, net.Layers[0], boardPads, options, warnings);

        // A degenerate vertex ordering can still defeat constraint recovery even after
        // normalization/retries; never leave the user stuck — fall back to the largest layer.
        try
        {
            return MeshMultiLayer(net, boardPads, options, warnings);
        }
        catch (ConstraintRecoveryException ex)
        {
            int largest = net.Layers
                .OrderByDescending(l => net.Islands.Where(i => i.LayerOrder == l).Sum(i => i.Area)).First();
            warnings.Add($"Net {net.Id}: multi-layer mesh could not be recovered ({ex.Message}); " +
                         $"meshing the largest layer L{largest} only.");
            return MeshSingleLayer(net, largest, boardPads, options, warnings);
        }
    }

    // ---------------- Single layer ----------------

    private static Result MeshSingleLayer(CopperNet net, int layer, IReadOnlyList<CopperPad> boardPads,
        NetMeshOptions options, List<string> warnings)
    {
        var polygons = net.Islands.Where(i => i.LayerOrder == layer).Select(i => i.Shape).ToList();
        WarnIfLarge(polygons, net, warnings);

        var netPads = boardPads
            .Where(p => p.LayerOrder == layer && polygons.Any(poly => PlanarMesher.ContainsPoint(new[] { poly }, p.Center)))
            .ToList();
        var padShapes = netPads.Select(p => p.Shape).ToList();

        double edge = options.TargetEdgeLength > 0 ? options.TargetEdgeLength : AutoEdge(polygons);
        double thickness = CopperThickness(options, layer);

        var planar = MeshRobust(polygons, edge);
        var mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, thickness, padShapes);

        var electrodes = CollectElectrodes(mesh, netPads);
        var body = BuildBody(net, mesh, options);
        warnings.Add($"Net {net.Id}: {mesh.ElementCount} elements, {electrodes.Count} pad electrodes, " +
                     $"{edge * 1e3:g3} mm edge (single layer L{layer}).");
        return new Result(body, electrodes, warnings);
    }

    // ---------------- Multi layer + via barrels ----------------

    private static Result MeshMultiLayer(CopperNet net, IReadOnlyList<CopperPad> boardPads,
        NetMeshOptions options, List<string> warnings)
    {
        var layers = net.Layers;                 // sorted ascending, ≥ 2 entries
        int minL = layers[0], maxL = layers[^1];

        // 1. Copper footprint per layer, CLEANED here — before the arrangement booleans —
        //    at the tolerance the planar mesher would use. The mesher's own per-polygon
        //    cleaning must stay off for the atomic faces built below (cleaning the two
        //    copies of a shared face boundary independently makes them cross), so the
        //    sub-micron arc-tessellation spurs have to die on the standalone inputs.
        var layerPolys = layers.ToDictionary(
            L => L, L => net.Islands.Where(i => i.LayerOrder == L).Select(i => i.Shape).ToList());
        double edge = options.TargetEdgeLength > 0
            ? options.TargetEdgeLength
            : AutoEdge(layers.SelectMany(L => layerPolys[L]).ToList());
        double cleanTol = Math.Min(PolygonCleaner.DefaultTolerance, edge / 20);
        foreach (var L in layers)
            layerPolys[L] = PolygonCleaner.Clean(layerPolys[L], cleanTol).ToList();

        // Pre-drill footprints are kept for pad-to-net matching (a via-in-pad's center
        // lies inside the bore that step 2b opens).
        var padLookupPolys = layers.ToDictionary(L => L, L => layerPolys[L].ToList());

        // 2. Hollow annular via barrels: the drilled bore (the finished hole, Via.Diameter)
        //    stays open and only a plated copper wall around it conducts — the geometry and
        //    conductance of a real plated via, not a solid cylinder.
        var bridges = net.StitchingVias;
        double plating = options.ViaPlatingThickness;
        if (plating < MinViaPlating)
        {
            warnings.Add($"Net {net.Id}: via plating {plating * 1e6:g3} µm is below the meshable " +
                         $"minimum; clamped to {MinViaPlating * 1e6:g3} µm.");
            plating = MinViaPlating;
        }
        var barrels = bridges.Select(b => Barrel(b.Via.Position, b.Via.Diameter / 2, plating)).ToList();
        var bridgeSpan = bridges.Select(b => (Min: b.Layers.Min(), Max: b.Layers.Max())).ToList();

        // 2b. The drill passes through every layer in the via's span — subtract the bore so
        //     a pad or plane over the hole correctly becomes an annulus around it. The clip
        //     ring MUST be the barrel's own bore tessellation: a second, differently
        //     tessellated circle at the same radius would cross the first and feed the CDT
        //     unrecoverable near-coincident constraints.
        for (int v = 0; v < bridges.Count; v++)
        {
            var bore = new[] { barrels[v].Holes[0] };
            for (int L = bridgeSpan[v].Min; L <= bridgeSpan[v].Max; L++)
                if (layerPolys.TryGetValue(L, out var polys) && polys.Count > 0)
                    layerPolys[L] = Ops.Difference(polys, bore).ToList();
        }

        // 3. One shared triangulation over the BOOLEAN UNION of every layer footprint + every
        //    barrel, split into an ARRANGEMENT of atomic faces along each footprint/barrel
        //    boundary. Raw per-layer outlines routinely cross each other (degenerate as CDT
        //    constraints), but boolean-resolved faces never do — and with every copper
        //    boundary imprinted as constraint edges, no triangle straddles one, so the
        //    per-layer centroid classification below is exact. (Straddling triangles were
        //    the "extra geometry" at bends, loops, and vias.) Shared nodes keep it conformal.
        var allRings = layers.SelectMany(L => layerPolys[L]).Concat(barrels)
            .SelectMany(Polygon2.OrientedRings).ToList();
        var domain = Ops.Union(allRings);
        WarnIfLarge(domain, net, warnings);

        IReadOnlyList<Polygon2> faces = domain;
        foreach (var group in layers.Select(L => (IReadOnlyList<Polygon2>)layerPolys[L])
                     .Concat(barrels.Select(b => (IReadOnlyList<Polygon2>)new[] { b })))
            faces = SplitFaces(faces, group);
        // Boolean outputs sharing a boundary can disagree by a snap-rounding unit at
        // grazing tangencies (crossing chains a nanometre deep, unrecoverable as CDT
        // constraints) — weld + imprint restores a conformal face set.
        faces = ArrangementWeld.Apply(faces, WeldTolerance);
        var planar = MeshRobust(faces, edge, cleanPolygons: false);

        // 4. True z of every copper layer and dielectric gap in the spanned range.
        var (layerZ, gapZ) = BuildStackupZ(minL, maxL, options);

        // 5. Per-triangle membership by centroid: which layer footprint / which barrel wall.
        int nt = planar.Triangles.Count;
        var centroid = new Point2[nt];
        for (int t = 0; t < nt; t++) centroid[t] = planar.Centroid(planar.Triangles[t]);

        var trisOnLayer = layers.ToDictionary(L => L, L => TrianglesIn(layerPolys[L], centroid));
        var trisInBridge = barrels.Select(b => TrianglesIn(new[] { b }, centroid)).ToList();

        // 6. Slabs: one per copper layer (its copper + any barrel passing through it) and one
        //    per dielectric gap (only the barrels bridging it). All copper.
        var slabs = new List<PcbMeshGenerator.ExtrudeSlab>();
        for (int L = minL; L <= maxL; L++)
        {
            var set = new HashSet<int>();
            if (trisOnLayer.TryGetValue(L, out var onL)) set.UnionWith(onL);
            for (int v = 0; v < bridges.Count; v++)
                if (bridgeSpan[v].Min <= L && L <= bridgeSpan[v].Max)
                    set.UnionWith(trisInBridge[v]);
            if (set.Count == 0) continue;
            var (zLo, zHi) = layerZ[L];
            slabs.Add(new PcbMeshGenerator.ExtrudeSlab(zLo, zHi, set.ToList(), PcbStackup.CopperRegion));
        }
        for (int g = minL; g < maxL; g++)
        {
            var set = new HashSet<int>();
            for (int v = 0; v < bridges.Count; v++)
                if (bridgeSpan[v].Min <= g && bridgeSpan[v].Max >= g + 1)
                    set.UnionWith(trisInBridge[v]);
            if (set.Count == 0) continue;
            var (zLo, zHi) = gapZ[g];
            slabs.Add(new PcbMeshGenerator.ExtrudeSlab(zLo, zHi, set.ToList(), PcbStackup.CopperRegion));
        }

        // 7. Pads on any spanned layer, tagged on that layer's top surface (each copper slab is
        //    surrounded by air except at the barrels, so both faces are boundary — top is enough).
        var netPads = new List<CopperPad>();
        var padFaces = new List<PcbMeshGenerator.PadFace>();
        foreach (var pad in boardPads)
        {
            // Match against the pre-drill footprints: a via-in-pad's center is inside the bore.
            if (!padLookupPolys.TryGetValue(pad.LayerOrder, out var polys)) continue;
            if (!polys.Any(poly => PlanarMesher.ContainsPoint(new[] { poly }, pad.Center))) continue;
            netPads.Add(pad);
            padFaces.Add(new PcbMeshGenerator.PadFace(pad.Shape, layerZ[pad.LayerOrder].zHi, TopFacing: true));
        }

        var mesh = new PcbMeshGenerator().GenerateLayered(planar, slabs, padFaces);
        var electrodes = CollectElectrodes(mesh, netPads);
        var body = BuildBody(net, mesh, options);

        warnings.Add($"Net {net.Id}: {mesh.ElementCount} elements across layers L{string.Join("+", layers)}, " +
                     $"{bridges.Count} annular via barrel(s) ({plating * 1e6:g3} µm wall), " +
                     $"{electrodes.Count} pad electrodes, {edge * 1e3:g3} mm edge.");
        if (bridges.Count == 0)
            warnings.Add($"Net {net.Id} spans layers L{string.Join("+", layers)} but has no annular-ring via bridges; " +
                         "layers are meshed at their true z but stay electrically separate (no barrel).");
        return new Result(body, electrodes, warnings);
    }

    // ---------------- Shared helpers ----------------

    /// <summary>
    /// z of each copper layer and dielectric gap over [<paramref name="minL"/>, <paramref name="maxL"/>].
    /// Depth accumulates downward from the top of the top-most layer; z = total − depth so the
    /// top layer (order minL) sits highest and every z ≥ 0. Public because the board preview
    /// places layers with this exact function, so what you see is where the mesh will sit.
    /// </summary>
    public static (Dictionary<int, (double zLo, double zHi)> LayerZ, Dictionary<int, (double zLo, double zHi)> GapZ)
        BuildStackupZ(int minL, int maxL, NetMeshOptions options)
    {
        var layerDepth = new Dictionary<int, (double Top, double Bot)>();
        var gapDepth = new Dictionary<int, (double Top, double Bot)>();
        double d = 0;
        for (int L = minL; L <= maxL; L++)
        {
            double cu = CopperThickness(options, L);
            layerDepth[L] = (d, d + cu);
            d += cu;
            if (L < maxL)
            {
                double gap = DielectricThickness(options, L);
                gapDepth[L] = (d, d + gap);
                d += gap;
            }
        }
        double total = d;
        var layerZ = layerDepth.ToDictionary(kv => kv.Key, kv => (zLo: total - kv.Value.Bot, zHi: total - kv.Value.Top));
        var gapZ = gapDepth.ToDictionary(kv => kv.Key, kv => (zLo: total - kv.Value.Bot, zHi: total - kv.Value.Top));
        return (layerZ, gapZ);
    }

    /// <summary>Shared polygon engine for the arrangement booleans.</summary>
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();

    /// <summary>Arc tessellation chord tolerance for barrel/bore rings [m] (Gerber default).</summary>
    private const double ChordTolerance = 5e-6;

    /// <summary>Thinnest via wall the planar mesher can carry without collapsing to slivers.</summary>
    private const double MinViaPlating = 5e-6;

    /// <summary>Atomic faces below this area [m²] are tangency slivers, dropped from the arrangement.</summary>
    private const double FaceAreaEpsilon = 1e-11;

    /// <summary>Conformal weld/imprint tolerance for the arrangement [m]: generously above the
    /// polygon engine's 1 nm snap grid (the source of the mismatches), 100× below the smallest
    /// meshable copper feature (<see cref="MinViaPlating"/>).</summary>
    private const double WeldTolerance = 50e-9;

    /// <summary>
    /// Splits every face along the boundary of <paramref name="group"/>: each face becomes
    /// its parts inside the group plus its parts outside. Faces are processed one at a
    /// time — a whole-list boolean would let the union fill rule merge adjacent faces back
    /// together and erase boundaries imprinted by earlier groups.
    /// </summary>
    private static IReadOnlyList<Polygon2> SplitFaces(IReadOnlyList<Polygon2> faces,
        IReadOnlyList<Polygon2> group)
    {
        if (group.Count == 0) return faces;
        var clips = group.SelectMany(Polygon2.OrientedRings).ToList();
        var result = new List<Polygon2>();
        foreach (var face in faces)
        {
            var subject = new[] { face };
            result.AddRange(Ops.Intersect(subject, clips).Where(f => f.Area() > FaceAreaEpsilon));
            result.AddRange(Ops.Difference(subject, clips).Where(f => f.Area() > FaceAreaEpsilon));
        }
        return result;
    }

    /// <summary>
    /// Meshes a domain robustly against the CDT's rare vertex-order degeneracies. The point
    /// insertion order (hence the deterministic jitter that breaks exact collinear/cocircular
    /// cases) is fixed by the ring vertex order, so two mitigations dodge a stuck recovery
    /// without touching the solver-critical mesher: normalise every ring to a lexicographic
    /// start, then, if recovery still fails, retry at slightly perturbed edge lengths (which
    /// re-index the points). Throws <see cref="ConstraintRecoveryException"/> if all attempts
    /// fail, so the caller can fall back.
    /// </summary>
    private static PlanarMesh MeshRobust(IReadOnlyList<Polygon2> polygons, double edge,
        bool cleanPolygons = true)
    {
        var normalized = polygons.Select(Normalize).ToList();
        var region = new[] { new PlanarRegion(PcbStackup.CopperRegion, normalized) };
        ConstraintRecoveryException? last = null;
        foreach (double m in new[] { 1.0, 1.09, 0.91, 1.19, 0.83 })
        {
            try { return new PlanarMesher().Mesh(region, edge * m, cleanPolygons); }
            catch (ConstraintRecoveryException ex) { last = ex; }
        }
        throw last!;
    }

    private static Polygon2 Normalize(Polygon2 p) =>
        new(RotateToLexMin(p.Outer), p.Holes.Select(RotateToLexMin).ToList());

    /// <summary>Rotates a ring to start at its lexicographically smallest (x, then y) vertex —
    /// a stable order that avoids the constraint-recovery degeneracy some orderings trigger.</summary>
    private static IReadOnlyList<Point2> RotateToLexMin(IReadOnlyList<Point2> ring)
    {
        int k = 0;
        for (int i = 1; i < ring.Count; i++)
            if (ring[i].X < ring[k].X || (ring[i].X == ring[k].X && ring[i].Y < ring[k].Y)) k = i;
        if (k == 0) return ring;
        var rotated = new List<Point2>(ring.Count);
        for (int i = 0; i < ring.Count; i++) rotated.Add(ring[(k + i) % ring.Count]);
        return rotated;
    }

    private static List<int> TrianglesIn(IReadOnlyList<Polygon2> polygons, Point2[] centroid)
    {
        var index = new PolygonSetIndex(polygons);
        var list = new List<int>();
        for (int t = 0; t < centroid.Length; t++)
            if (index.Contains(centroid[t])) list.Add(t);
        return list;
    }

    private static List<PadElectrode> CollectElectrodes(FeMesh mesh, IReadOnlyList<CopperPad> netPads)
    {
        var tagged = mesh.BoundaryTriangles.Select(t => t.FaceId).ToHashSet();
        var electrodes = new List<PadElectrode>();
        for (int k = 0; k < netPads.Count; k++)
        {
            int faceId = PcbMeshGenerator.PadFaceBase + k;
            if (tagged.Contains(faceId))
                electrodes.Add(new PadElectrode(faceId, netPads[k].Center, netPads[k].LayerOrder));
        }
        return electrodes;
    }

    private static Body BuildBody(CopperNet net, FeMesh mesh, NetMeshOptions options) => new()
    {
        Name = net.Label,
        Geometry = BoundarySurface(mesh),
        GeometrySource = $"PCB net {net.Id}",
        Mesh = mesh,
        RegionMaterialNames = new Dictionary<int, string> { [PcbStackup.CopperRegion] = options.CopperMaterialName }
    };

    /// <summary>
    /// Display surface = the FE boundary skin, so its face ids are exactly the mesh's face ids
    /// (including pad electrode ids ≥ <see cref="PcbMeshGenerator.PadFaceBase"/>). This is what
    /// makes a clicked pad map to an electrode — the clickable geometry and the electrode faces
    /// are the same set. It also renders the true multi-layer shape (traces at their z + barrels).
    /// </summary>
    private static TriangleMesh BoundarySurface(FeMesh mesh)
    {
        var tris = mesh.BoundaryTriangles.Select(b => new Triangle(b.A, b.B, b.C)).ToList();
        var faceIds = mesh.BoundaryTriangles.Select(b => b.FaceId).ToList();
        return new TriangleMesh(mesh.Nodes, tris, faceIds);
    }

    /// <summary>
    /// Advisory only — the old hard vertex cap is gone (the pipeline handles plane/pour
    /// nets now); a heads-up still reaches the log so a long mesh/solve is never a
    /// silent surprise.
    /// </summary>
    private static void WarnIfLarge(IReadOnlyList<Polygon2> polygons, CopperNet net, List<string> warnings)
    {
        const int advisory = 20_000;
        int verts = polygons.Sum(p => p.Outer.Count + p.Holes.Sum(h => h.Count));
        if (verts > advisory)
            warnings.Add($"Net {net.Id}: large copper outline ({verts} vertices, likely a plane/pour) — " +
                         "meshing and solving may take a while.");
    }

    private static double CopperThickness(NetMeshOptions o, int layer) =>
        o.LayerThickness?.GetValueOrDefault(layer) is > 0 and var t ? t : o.CopperThickness;

    private static double DielectricThickness(NetMeshOptions o, int upperLayer) =>
        o.DielectricGapThickness?.GetValueOrDefault(upperLayer) is > 0 and var t ? t : o.DefaultDielectricThickness;

    /// <summary>
    /// A plated via barrel's copper cross-section: an annulus whose inner ring is the open
    /// bore (radius = finished hole radius) and whose wall is the plating thickness. Both
    /// rings share the same vertex count and angles so the wall triangulates into clean,
    /// radially aligned strips.
    /// </summary>
    private static Polygon2 Barrel(Point2 c, double boreRadius, double plating)
    {
        double rOut = boreRadius + plating;
        int n = ApertureShapes.SegmentCount(rOut, ChordTolerance);
        var outer = new Point2[n];
        var bore = new Point2[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            outer[i] = new Point2(c.X + rOut * cos, c.Y + rOut * sin);
            bore[n - 1 - i] = new Point2(c.X + boreRadius * cos, c.Y + boreRadius * sin);   // CW hole
        }
        return new Polygon2(outer, new[] { (IReadOnlyList<Point2>)bore });
    }

    private static double AutoEdge(IReadOnlyList<Polygon2> polygons)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var poly in polygons)
            foreach (var p in poly.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        double diag = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
        // Finer than the whole board: a net is small and we want a few elements across a trace.
        return Math.Max(diag / 60, 5e-5);
    }
}
