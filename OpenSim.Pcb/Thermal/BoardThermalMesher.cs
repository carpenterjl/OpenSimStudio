using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Thermal;

/// <summary>How the whole board is meshed for a thermal solve.</summary>
public sealed record BoardThermalOptions
{
    /// <summary>Element edge length in the plane [m]; 0 = the board's diagonal / 60, at
    /// least 0.4 mm. Small footprints are refined locally whatever this is.</summary>
    public double TargetEdgeLength { get; init; }

    public required PcbStackupSettings Stackup { get; init; }
    public required Material Copper { get; init; }
    public required Material Laminate { get; init; }

    /// <summary>Element layers through each dielectric gap.</summary>
    public int GapElementLayers { get; init; } = 2;

    /// <summary>Plated vias carry heat through the board (their barrel copper is added to
    /// the dielectric they cross).</summary>
    public bool IncludeVias { get; init; } = true;

    public double ViaPlatingThickness { get; init; } = 25e-6;

    /// <summary>Emissivity of the board's surface (solder mask ≈ 0.9); null keeps the
    /// laminate material's own.</summary>
    public double? SurfaceEmissivity { get; init; } = 0.9;

    /// <summary>
    /// Give a copper-layer element whose copper is mostly trace an orthotropic conductivity:
    /// along the trace copper and laminate side by side, across it the two in series, and
    /// through the layer side by side again (<see cref="BoardThermalMesh.Conductivity"/>).
    /// Off, every element conducts as copper and laminate side by side in every direction —
    /// right for planes and pours, too high across a narrow trace.
    /// </summary>
    public bool TraceDirections { get; init; }
}

/// <summary>The whole board as a thermal mesh, with the materials that go with its
/// region ids and the face each component sits on.</summary>
public sealed record BoardThermalMesh
{
    public required FeMesh Mesh { get; init; }

    /// <summary>Region id → material. Region q is laminate with q / 2000 of its volume copper.</summary>
    public required IReadOnlyDictionary<int, Material> RegionMaterials { get; init; }

    /// <summary>The plain laminate (region 0).</summary>
    public required Material Laminate { get; init; }

    /// <summary>Face id of each component's contact patch; −1 when it is off the board.</summary>
    public required IReadOnlyList<int> ComponentFaceIds { get; init; }

    /// <summary>Meshed contact area per component [m²].</summary>
    public required IReadOnlyList<double> ComponentContactArea { get; init; }

    /// <summary>Copper in the layers [m³], as the model holds it.</summary>
    public double CopperVolume { get; init; }

    /// <summary>Copper in via barrels [m³], as the model holds it.</summary>
    public double ViaCopperVolume { get; init; }

    /// <summary>Copper coverage per layer (index 0 = layer 1), 0…1.</summary>
    public IReadOnlyList<double> LayerCoverage { get; init; } = Array.Empty<double>();

    public double EdgeLength { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Per-element conductivity tensor with <see cref="BoardThermalOptions.TraceDirections"/>;
    /// null otherwise (the region materials' own conductivity).</summary>
    public IReadOnlyList<ConductivityTensor>? Conductivity { get; init; }

    /// <summary>z extent of every copper layer (layer order → metres), the board's bottom at 0.</summary>
    public IReadOnlyDictionary<int, (double zLo, double zHi)> LayerZ { get; init; } =
        new Dictionary<int, (double zLo, double zHi)>();
}

/// <summary>
/// Meshes the whole board for heat: one planar triangulation of the outline, extruded
/// through every copper layer and dielectric gap. The copper is not cut out shape by shape
/// — a full board's copper is far beyond what the tetrahedral mesher can follow — but
/// each element of a copper layer holds the fraction of copper found under it (seven sample
/// points per triangle) and conducts as laminate and copper side by side. A plane conducts
/// as a plane; traces narrower than an element conduct as their share of copper, without
/// their direction. Via barrels add their copper to the dielectric they cross. Component
/// footprints are imprinted so each has its own face.
/// </summary>
public static class BoardThermalMesher
{
    /// <summary>Copper fractions are held in steps of 1 / this.</summary>
    public const int FractionSteps = 2000;

    /// <summary>Region ids of copper layers start here, so a copper-layer element and a
    /// dielectric element with the same copper share stay distinguishable.</summary>
    public const int CopperLayerRegionBase = 10000;

    private static readonly (double W0, double W1, double W2)[] Samples =
    {
        (1 / 3.0, 1 / 3.0, 1 / 3.0),
        (0.6 + 0.4 / 3, 0.4 / 3, 0.4 / 3), (0.4 / 3, 0.6 + 0.4 / 3, 0.4 / 3), (0.4 / 3, 0.4 / 3, 0.6 + 0.4 / 3),
        (0.4 / 3, 0.3 + 0.4 / 3, 0.3 + 0.4 / 3), (0.3 + 0.4 / 3, 0.4 / 3, 0.3 + 0.4 / 3), (0.3 + 0.4 / 3, 0.3 + 0.4 / 3, 0.4 / 3)
    };

    public static BoardThermalMesh Mesh(PcbBoard board, IReadOnlyList<ThermalComponent> components,
        BoardThermalOptions options)
    {
        if (board.Outline.Count == 0)
            throw new InvalidOperationException("The board has no outline; a thermal mesh needs one.");
        options.Copper.ValidateThermal();
        options.Laminate.ValidateThermal();
        var notes = new List<string>();
        var ops = new ClipperPolygonOps();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var polygon in board.Outline)
            foreach (var p in polygon.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double h = options.TargetEdgeLength > 0 ? options.TargetEdgeLength : Math.Max(diagonal / 60, 0.4e-3);

        // Footprints first (priority), clipped to the board; the outline last.
        var regions = new List<PlanarRegion>();
        var refinements = new List<MeshRefinement>();
        var laid = new List<(Point2 Point, double Spacing)>();
        var holes = board.Outline.SelectMany(o => o.Holes).ToList();
        for (int k = 0; k < components.Count; k++)
        {
            components[k].Validate();
            var clipped = ops.Intersect(new[] { components[k].Footprint }, board.Outline.Select(o => o.Outer));
            if (holes.Count > 0 && clipped.Count > 0) clipped = ops.Difference(clipped, holes);
            if (clipped.Sum(p => p.Area()) <= 0)
            {
                notes.Add($"{components[k].RefDes}: its footprint is off the board.");
                continue;
            }
            regions.Add(new PlanarRegion(1 + k, clipped));
            if (Grading(components[k].Footprint, h, laid) is { } grading) refinements.Add(grading);
        }
        regions.Add(new PlanarRegion(0, board.Outline));
        var planar = new PlanarMesher().Mesh(regions, h, refinements: refinements.Count > 0 ? refinements : null);
        int triangleCount = planar.Triangles.Count;
        var area = new double[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            var tri = planar.Triangles[t];
            Point2 a = planar.Points[tri.A], b = planar.Points[tri.B], c = planar.Points[tri.C];
            area[t] = 0.5 * Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));
        }

        // The stack, bottom (z = 0) to top.
        // The layers the copper shows, or the stackup's when it lists more (a layer may carry no copper).
        int layerCount = Math.Max(Math.Max(1, board.Islands.Count == 0 ? 1 : board.Islands.Max(i => i.LayerOrder)),
            options.Stackup.DielectricGapThicknesses.Count + 1);
        var stackup = options.Stackup;
        double CopperThickness(int order) =>
            order - 1 < stackup.CopperLayerThicknesses.Count ? stackup.CopperLayerThicknesses[order - 1] : stackup.CopperThickness;
        double GapThickness(int upperOrder) =>
            upperOrder - 1 < stackup.DielectricGapThicknesses.Count
                ? stackup.DielectricGapThicknesses[upperOrder - 1]
                : stackup.BoardThickness / Math.Max(1, layerCount - 1);
        int gapLayers = Math.Max(1, options.GapElementLayers);

        var levels = new List<double> { 0 };
        var slabKinds = new List<(bool Copper, int Order)>();   // Order: the copper layer, or the gap's upper layer
        void Add(double thickness, bool copper, int order, int pieces)
        {
            for (int i = 0; i < pieces; i++)
            {
                levels.Add(levels[^1] + thickness / pieces);
                slabKinds.Add((copper, order));
            }
        }
        if (layerCount == 1)
        {
            Add(stackup.BoardThickness, false, 1, gapLayers);
            Add(CopperThickness(1), true, 1, 1);
        }
        else
        {
            for (int order = layerCount; order >= 1; order--)
            {
                Add(CopperThickness(order), true, order, 1);
                if (order > 1) Add(GapThickness(order - 1), false, order - 1, gapLayers);
            }
        }

        // Copper share of each triangle on each layer.
        var coverage = new int[layerCount + 1][];
        var layerCoverage = new double[layerCount];
        double copperVolume = 0, boardArea = area.Sum();
        for (int order = 1; order <= layerCount; order++)
        {
            coverage[order] = CoverageOf(planar, board.Islands.Where(i => i.LayerOrder == order).ToList(), h);
            double covered = 0;
            for (int t = 0; t < triangleCount; t++) covered += area[t] * coverage[order][t] / (Samples.Length * 1.0);
            layerCoverage[order - 1] = boardArea > 0 ? covered / boardArea : 0;
            copperVolume += covered * CopperThickness(order);
        }

        // Via barrels: copper area per triangle, per gap.
        var viaArea = new Dictionary<int, double>[layerCount + 1];
        double viaVolume = 0;
        int viasUsed = 0;
        if (options.IncludeVias && board.Vias.Count > 0)
        {
            var locator = new TriangleLocator(planar, h);
            foreach (var via in board.Vias)
            {
                if (!via.Plated) continue;
                int t = locator.Find(via.Position);
                if (t < 0) continue;
                double barrel = Math.PI * via.Diameter * options.ViaPlatingThickness;
                bool used = false;
                for (int upper = 1; upper < Math.Max(2, layerCount); upper++)
                {
                    if (layerCount > 1 && !(via.Reaches(upper) && via.Reaches(upper + 1))) continue;
                    (viaArea[upper] ??= new Dictionary<int, double>())[t] =
                        viaArea[upper].GetValueOrDefault(t) + barrel;
                    viaVolume += barrel * (layerCount == 1 ? stackup.BoardThickness : GapThickness(upper));
                    used = true;
                }
                if (used) viasUsed++;
            }
        }

        // Slabs: one per element layer and copper share.
        var slabs = new List<PcbMeshGenerator.ExtrudeSlab>();
        var usedRegions = new SortedSet<int>();
        for (int s = 0; s < slabKinds.Count; s++)
        {
            var (isCopper, order) = slabKinds[s];
            var byRegion = new Dictionary<int, List<int>>();
            for (int t = 0; t < triangleCount; t++)
            {
                int region;
                if (isCopper)
                    region = CopperLayerRegionBase + (int)Math.Round(FractionSteps * coverage[order][t] / (double)Samples.Length);
                else
                {
                    double fraction = viaArea[order] is { } map && map.TryGetValue(t, out double barrel)
                        ? Math.Min(1.0, barrel / area[t]) : 0;
                    region = (int)Math.Round(FractionSteps * fraction);
                }
                if (!byRegion.TryGetValue(region, out var list)) byRegion[region] = list = new List<int>();
                list.Add(t);
            }
            foreach (var (region, list) in byRegion)
            {
                slabs.Add(new PcbMeshGenerator.ExtrudeSlab(levels[s], levels[s + 1], list, region));
                usedRegions.Add(region);
            }
        }

        double top = levels[^1];
        var faces = components.Select(c => new PcbMeshGenerator.PadFace(c.Footprint, c.OnTop ? top : 0, c.OnTop)).ToList();
        var mesh = new PcbMeshGenerator().GenerateLayered(planar, slabs, faces);

        IReadOnlyList<ConductivityTensor>? conductivity = null;
        if (options.TraceDirections)
        {
            var directions = new (double Share, double Angle)[layerCount + 1][];
            for (int order = 1; order <= layerCount; order++)
                directions[order] = TraceDirections(planar, board, order);
            conductivity = Tensors(mesh, planar, h, levels, slabKinds, coverage, directions, options.Copper,
                options.Laminate, notes);
        }

        var contact = new double[components.Count];
        foreach (var triangle in mesh.BoundaryTriangles)
        {
            int k = triangle.FaceId - PcbMeshGenerator.PadFaceBase;
            if (k < 0 || k >= contact.Length) continue;
            contact[k] += 0.5 * Vector3D.Cross(mesh.Nodes[triangle.B] - mesh.Nodes[triangle.A],
                mesh.Nodes[triangle.C] - mesh.Nodes[triangle.A]).Length;
        }

        var laminate = options.SurfaceEmissivity is { } eps ? options.Laminate with { Emissivity = eps } : options.Laminate;
        var materials = new Dictionary<int, Material>();
        foreach (int region in usedRegions)
        {
            double fraction = (region >= CopperLayerRegionBase ? region - CopperLayerRegionBase : region) / (double)FractionSteps;
            materials[region] = Mixture(options.Copper, laminate, fraction);
        }
        if (!materials.ContainsKey(0)) materials[0] = laminate;

        notes.Add($"Board mesh: {mesh.ElementCount} elements, {planar.Triangles.Count} triangles at {h * 1e3:g3} mm, " +
                  $"{slabKinds.Count} element layers through {top * 1e3:g4} mm.");
        notes.Add("Copper per layer: " + string.Join(", ",
            layerCoverage.Select((c, i) => $"L{i + 1} {c * 100:f0} %")) + ".");
        if (viasUsed > 0) notes.Add($"{viasUsed} plated via(s) carry heat through the board.");

        return new BoardThermalMesh
        {
            Mesh = mesh,
            RegionMaterials = materials,
            Laminate = laminate,
            ComponentFaceIds = contact.Select((a, k) => a > 0 ? PcbMeshGenerator.PadFaceBase + k : -1).ToList(),
            ComponentContactArea = contact,
            CopperVolume = copperVolume,
            ViaCopperVolume = viaVolume,
            LayerCoverage = layerCoverage,
            EdgeLength = h,
            Notes = notes,
            Conductivity = conductivity,
            LayerZ = Enumerable.Range(0, slabKinds.Count).Where(s => slabKinds[s].Copper)
                .ToDictionary(s => slabKinds[s].Order, s => (levels[s], levels[s + 1]))
        };
    }

    /// <summary>Per triangle of a copper layer: the share of its copper samples that lie on a
    /// trace (within half a width of a centerline), and the traces' mean direction there
    /// (doubled-angle mean, so a trace and its reverse agree).</summary>
    private static (double Share, double Angle)[] TraceDirections(PlanarMesh planar, PcbBoard board, int order)
    {
        var result = new (double, double)[planar.Triangles.Count];
        var lines = board.TraceCenterlines.Where(c => c.LayerOrder == order && c.Length > 0).ToList();
        var shapes = board.Islands.Where(i => i.LayerOrder == order).Select(i => i.Shape).ToList();
        if (lines.Count == 0 || shapes.Count == 0) return result;
        var copper = new PolygonSetIndex(shapes);
        Parallel.For(0, planar.Triangles.Count, t =>
        {
            var tri = planar.Triangles[t];
            Point2 a = planar.Points[tri.A], b = planar.Points[tri.B], c = planar.Points[tri.C];
            int onCopper = 0, onTrace = 0;
            double cos2 = 0, sin2 = 0;
            foreach (var (w0, w1, w2) in Samples)
            {
                var p = new Point2(w0 * a.X + w1 * b.X + w2 * c.X, w0 * a.Y + w1 * b.Y + w2 * c.Y);
                if (!copper.Contains(p)) continue;
                onCopper++;
                foreach (var line in lines)
                {
                    var d = line.End - line.Start;
                    double length = d.Length;
                    double s = Math.Clamp(Point2.Dot(p - line.Start, d) / (length * length), 0, 1);
                    if ((p - (line.Start + d * s)).Length > 0.5 * line.Width * 1.05) continue;
                    double angle = Math.Atan2(d.Y, d.X);
                    cos2 += Math.Cos(2 * angle);
                    sin2 += Math.Sin(2 * angle);
                    onTrace++;
                    break;
                }
            }
            result[t] = onCopper == 0 ? (0, 0) : ((double)onTrace / onCopper, 0.5 * Math.Atan2(sin2, cos2));
        });
        return result;
    }

    /// <summary>The conductivity tensor of every element: orthotropic where a copper layer's
    /// copper is mostly trace, isotropic (copper and laminate side by side) elsewhere.</summary>
    private static ConductivityTensor[] Tensors(FeMesh mesh, PlanarMesh planar, double h, List<double> levels,
        List<(bool Copper, int Order)> slabKinds, int[][] coverage, (double Share, double Angle)[][] directions,
        Material copper, Material laminate, List<string> notes)
    {
        var locator = new TriangleLocator(planar, h);
        double kc = copper.ThermalConductivity!.Value, kl = laminate.ThermalConductivity!.Value;
        var tensors = new ConductivityTensor[mesh.ElementCount];
        int orthotropic = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            var centroid = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
            int slab = 0;
            while (slab < slabKinds.Count - 1 && centroid.Z > levels[slab + 1]) slab++;
            int t = locator.Find(new Point2(centroid.X, centroid.Y));
            var (isCopper, order) = slabKinds[slab];
            if (isCopper && t >= 0 && directions[order][t].Share >= 0.5)
            {
                double f = coverage[order][t] / (double)Samples.Length;
                double parallel = kl + f * (kc - kl);
                double series = 1 / (f / kc + (1 - f) / kl);
                tensors[e] = ConductivityTensor.InPlane(parallel, series, parallel, directions[order][t].Angle);
                orthotropic++;
                continue;
            }
            double fraction = (mesh.RegionOf(e) >= CopperLayerRegionBase ? mesh.RegionOf(e) - CopperLayerRegionBase : mesh.RegionOf(e))
                              / (double)FractionSteps;
            tensors[e] = ConductivityTensor.Isotropic(kl + Math.Max(0, fraction) * (kc - kl));
        }
        notes.Add($"Trace directions: {orthotropic} copper-layer elements conduct along their traces as copper and " +
                  "laminate side by side and across them in series.");
        return tensors;
    }

    /// <summary>Laminate with a share of its volume copper, the two conducting side by side.</summary>
    internal static Material Mixture(Material copper, Material laminate, double fraction)
    {
        if (fraction <= 0) return laminate;
        double k = laminate.ThermalConductivity!.Value
                   + fraction * (copper.ThermalConductivity!.Value - laminate.ThermalConductivity.Value);
        double density = laminate.Density + fraction * (copper.Density - laminate.Density);
        double? heat = copper.SpecificHeat is { } cc && laminate.SpecificHeat is { } cl && density > 0
            ? ((1 - fraction) * laminate.Density * cl + fraction * copper.Density * cc) / density
            : null;
        return laminate with
        {
            Name = fraction >= 1 ? copper.Name + " (board layer)" : $"{laminate.Name} with {fraction * 100:g3} % copper",
            ThermalConductivity = k,
            Density = density,
            SpecificHeat = heat
        };
    }

    /// <summary>How many of the seven sample points of each triangle lie on copper.</summary>
    private static int[] CoverageOf(PlanarMesh planar, IReadOnlyList<CopperIsland> islands, double h)
    {
        var hits = new int[planar.Triangles.Count];
        if (islands.Count == 0) return hits;
        var bounds = islands.Select(i => i.Bounds()).ToArray();
        var shapes = islands.Select(i => new[] { i.Shape }).ToArray();
        double minX = bounds.Min(b => b.MinX), minY = bounds.Min(b => b.MinY);
        double maxX = bounds.Max(b => b.MaxX), maxY = bounds.Max(b => b.MaxY);
        double cell = Math.Max(h, Math.Max(maxX - minX, maxY - minY) / 96);
        int nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cell)), ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cell));
        var grid = new List<int>?[nx * ny];
        for (int i = 0; i < islands.Count; i++)
        {
            int x0 = Math.Clamp((int)((bounds[i].MinX - minX) / cell), 0, nx - 1), x1 = Math.Clamp((int)((bounds[i].MaxX - minX) / cell), 0, nx - 1);
            int y0 = Math.Clamp((int)((bounds[i].MinY - minY) / cell), 0, ny - 1), y1 = Math.Clamp((int)((bounds[i].MaxY - minY) / cell), 0, ny - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    (grid[y * nx + x] ??= new List<int>()).Add(i);
        }
        Parallel.For(0, planar.Triangles.Count, t =>
        {
            var tri = planar.Triangles[t];
            Point2 a = planar.Points[tri.A], b = planar.Points[tri.B], c = planar.Points[tri.C];
            int count = 0;
            foreach (var (w0, w1, w2) in Samples)
            {
                var p = new Point2(w0 * a.X + w1 * b.X + w2 * c.X, w0 * a.Y + w1 * b.Y + w2 * c.Y);
                if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;
                int gx = Math.Clamp((int)((p.X - minX) / cell), 0, nx - 1), gy = Math.Clamp((int)((p.Y - minY) / cell), 0, ny - 1);
                var candidates = grid[gy * nx + gx];
                if (candidates is null) continue;
                foreach (int i in candidates)
                {
                    var bb = bounds[i];
                    if (p.X < bb.MinX || p.X > bb.MaxX || p.Y < bb.MinY || p.Y > bb.MaxY) continue;
                    if (!PlanarMesher.ContainsPoint(shapes[i], p)) continue;
                    count++;
                    break;
                }
            }
            hits[t] = count;
        });
        return hits;
    }

    /// <summary>
    /// Points for a footprint smaller than a few elements: a lattice at a quarter of its
    /// short side over the disc around it, then rings growing out to the board's element
    /// size. Null when the footprint is already resolved.
    /// </summary>
    private static MeshRefinement? Grading(Polygon2 footprint, double h, List<(Point2 Point, double Spacing)> laid)
    {
        var ring0 = footprint.Outer;
        var centre = new Point2(ring0.Average(p => p.X), ring0.Average(p => p.Y));
        double reach = ring0.Max(p => (p - centre).Length);
        double shortSide = double.MaxValue;
        for (int i = 0; i < ring0.Count; i++)
        {
            Point2 a = ring0[i], b = ring0[(i + 1) % ring0.Count];
            double length = (b - a).Length;
            if (length <= 0) continue;
            // Width across this edge: the farthest vertex from its line.
            double width = ring0.Max(p => Math.Abs((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / length);
            shortSide = Math.Min(shortSide, width);
        }
        double s = Math.Max(shortSide / 4, h / 10);
        if (s >= 0.8 * h) return null;

        var points = new List<(Point2, double)>();
        void Lay(Point2 p, double spacing)
        {
            foreach (var (q, other) in laid)
                if ((q - p).Length < 0.6 * Math.Min(spacing, other)) return;
            laid.Add((p, spacing));
            points.Add((p, spacing));
        }
        double rowStep = s * Math.Sqrt(3) / 2;
        int rows = (int)Math.Ceiling(reach / rowStep);
        for (int j = -rows; j <= rows; j++)
        {
            double y = j * rowStep, shift = (j & 1) == 0 ? 0 : 0.5 * s;
            int columns = (int)Math.Ceiling(reach / s) + 1;
            for (int i = -columns; i <= columns; i++)
            {
                double x = i * s + shift;
                if (x * x + y * y > reach * reach) continue;
                // A fixed small offset per point keeps the lattice off exact cocircularity.
                double wobble = Math.Sin(i * 12.9898 + j * 78.233) * 43758.5453;
                wobble -= Math.Floor(wobble);
                Lay(new Point2(centre.X + x + 0.04 * s * (wobble - 0.5), centre.Y + y + 0.04 * s * (0.5 - wobble)), s);
            }
        }
        int ringNodes = Math.Clamp((int)Math.Ceiling(2 * Math.PI * reach / s), 8, 96);
        double growth = 1 + 2 * Math.PI / ringNodes;
        double radius = reach * growth, last = reach;
        int ring = 0;
        while (2 * Math.PI * radius / ringNodes < 0.9 * h)
        {
            double spacing = 2 * Math.PI * radius / ringNodes;
            double phase = (ring++ % 2) * Math.PI / ringNodes;
            for (int i = 0; i < ringNodes; i++)
            {
                double angle = phase + 2 * Math.PI * i / ringNodes;
                Lay(new Point2(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)), spacing);
            }
            last = radius;
            radius *= growth;
        }
        return new MeshRefinement(centre, ring > 0 ? last + 0.7 * h : reach, points);
    }

    /// <summary>Finds the planar triangle under a point through a uniform grid.</summary>
    private sealed class TriangleLocator
    {
        private readonly PlanarMesh _mesh;
        private readonly List<int>?[] _grid;
        private readonly double _minX, _minY, _cell;
        private readonly int _nx, _ny;

        public TriangleLocator(PlanarMesh mesh, double h)
        {
            _mesh = mesh;
            _minX = mesh.Points.Min(p => p.X);
            _minY = mesh.Points.Min(p => p.Y);
            double maxX = mesh.Points.Max(p => p.X), maxY = mesh.Points.Max(p => p.Y);
            _cell = Math.Max(h, Math.Max(maxX - _minX, maxY - _minY) / 256);
            _nx = Math.Max(1, (int)Math.Ceiling((maxX - _minX) / _cell));
            _ny = Math.Max(1, (int)Math.Ceiling((maxY - _minY) / _cell));
            _grid = new List<int>?[_nx * _ny];
            for (int t = 0; t < mesh.Triangles.Count; t++)
            {
                var tri = mesh.Triangles[t];
                Point2 a = mesh.Points[tri.A], b = mesh.Points[tri.B], c = mesh.Points[tri.C];
                int x0 = Cell(Math.Min(a.X, Math.Min(b.X, c.X)) - _minX, _nx), x1 = Cell(Math.Max(a.X, Math.Max(b.X, c.X)) - _minX, _nx);
                int y0 = Cell(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - _minY, _ny), y1 = Cell(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - _minY, _ny);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (_grid[y * _nx + x] ??= new List<int>()).Add(t);
            }
        }

        private int Cell(double offset, int count) => Math.Clamp((int)(offset / _cell), 0, count - 1);

        public int Find(Point2 p)
        {
            if (p.X < _minX || p.Y < _minY) return -1;
            var candidates = _grid[Cell(p.Y - _minY, _ny) * _nx + Cell(p.X - _minX, _nx)];
            if (candidates is null) return -1;
            foreach (int t in candidates)
            {
                var tri = _mesh.Triangles[t];
                Point2 a = _mesh.Points[tri.A], b = _mesh.Points[tri.B], c = _mesh.Points[tri.C];
                double d1 = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                double d2 = (c.X - b.X) * (p.Y - b.Y) - (c.Y - b.Y) * (p.X - b.X);
                double d3 = (a.X - c.X) * (p.Y - c.Y) - (a.Y - c.Y) * (p.X - c.X);
                if ((d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0)) return t;
            }
            return -1;
        }
    }
}
