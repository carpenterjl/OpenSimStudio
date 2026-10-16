using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Pdn;

/// <summary>One copper island of a stack layer: a plane, or a piece of a split plane.</summary>
public sealed record StackPlate(string Name, IReadOnlyList<Polygon2> Shape)
{
    /// <summary>The net it belongs to, for the summary; the solve does not use it.</summary>
    public string? Net { get; init; }
}

/// <summary>A copper layer of the stack and the dielectric below it.</summary>
public sealed record StackLayer
{
    public required string Name { get; init; }
    public double CopperThicknessMeters { get; init; } = 35e-6;

    /// <summary>The layer's plates. Plates of one layer must not overlap.</summary>
    public required IReadOnlyList<StackPlate> Plates { get; init; }

    /// <summary>Dielectric between this layer and the next one down [m]; ignored on the last layer.</summary>
    public double GapBelowMeters { get; init; }

    public double GapPermittivity { get; init; } = 4.4;
    public double GapLossTangent { get; init; } = 0.02;
}

/// <summary>A port of the stack: current enters the plate of <paramref name="UpperLayer"/> at
/// the site and leaves the plate of <paramref name="LowerLayer"/> there (layer indices into
/// <see cref="PlaneStackSpec.Layers"/>; "upper" only names the sign, either may be the lower).</summary>
public sealed record StackPort(PlanePort Site, int UpperLayer, int LowerLayer);

/// <summary>A via that joins the plates of two layers at a point (a stitching via).</summary>
public sealed record StackTie(Point2 Position, double RadiusMeters, int UpperLayer, int LowerLayer)
{
    public string Name { get; init; } = "";
}

/// <summary>The fringe of an open edge between two plates, per metre of edge: their mutual
/// capacitance beyond what the plates' overlap holds, with the board's dielectric and with
/// every dielectric replaced by air.</summary>
public sealed record FringePair(int UpperLayer, int LowerLayer, double FaradsPerMeter, double AirFaradsPerMeter);

/// <summary>The fringe of one kind of open edge: one entry per pair of plates that end there.</summary>
public sealed record EdgeFringe(IReadOnlyList<FringePair> Pairs);

/// <summary>An open edge of the stack the fringe is asked for: where it is, which way is out,
/// and the layers with copper on its inner side.</summary>
public readonly record struct FringeEdge(Point2 Midpoint, Point2 Outward, double LengthMeters, IReadOnlyList<int> Layers);

/// <summary>A cavity of the stack: the region between two plates with no copper between them.</summary>
public sealed record StackCavity(int UpperLayer, int LowerLayer, double SeparationMeters,
    double RelativePermittivity, double LossTangent, double AreaSquareMeters);

public sealed record PlaneStackSpec
{
    /// <summary>The copper layers, top down.</summary>
    public required IReadOnlyList<StackLayer> Layers { get; init; }

    public required IReadOnlyList<StackPort> Ports { get; init; }
    public IReadOnlyList<StackTie> Ties { get; init; } = Array.Empty<StackTie>();

    /// <summary>Djordjevic–Sarkar dielectrics, as <see cref="PlanePairSpec.WidebandDielectric"/>.</summary>
    public bool WidebandDielectric { get; init; } = true;

    public double DielectricReferenceHz { get; init; } = 1e9;
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Target edge length away from the ports [m]; 0 chooses it from
    /// <see cref="MaxFrequencyHz"/> and the stack's size.</summary>
    public double MeshEdgeMeters { get; init; }

    public double MaxFrequencyHz { get; init; } = 1e9;

    /// <summary>The fringe of each open edge of the stack; null leaves the edges bare (the
    /// field stops at the copper's edge).</summary>
    public Func<FringeEdge, EdgeFringe?>? Fringe { get; init; }
}

/// <summary>
/// The impedance between ports of a stack of copper planes, by finite elements on the planes'
/// real outlines — the plane pair of <see cref="PlanePair"/> generalized to any number of
/// layers, each with any number of plates, holes and splits.
///
/// <para>Where several plates lie over one another, each two neighbours with no copper between
/// them bound a cavity k with a voltage v_k across it and a sheet current i_k on its faces;
/// where a plate stops (a split, a cut-out) the cavities above and below it merge into one.
/// Per cavity, as for a single pair,</para>
/// <code>
///   ∇v_k = −Σ_m Z_km·i_m      Z_kk = jωµ₀d_k + Z_s(upper plate) + Z_s(lower plate)
///                             Z_k,k+1 = −Z_t(the plate the two share)
///   ∇·i_k = −Y_k·v_k + J_k    Y_k = jωε_k/d_k
/// </code>
/// <para>with Z_s = (k/σ)·coth(k·t) the impedance of a plate face and Z_t = (k/σ)/sinh(k·t) the
/// transfer through a plate carrying current on both faces (at DC both are the sheet resistance:
/// a shared plate couples the cavities on its two sides, and at high frequency it shields them).</para>
///
/// <para>The unknowns are the plates' potentials φ, v_k = φ_upper − φ_lower. Only differences
/// between plates at the same place enter, so at every node one plate — the lowest there — is
/// taken as the reference and carries no unknown; with two plates this is exactly the plane
/// pair's single voltage. Where the cavities meet at a plate's edge, the potentials of the plates
/// that go on are continuous, and current is conserved on each face; both come with the
/// potential unknowns by themselves.</para>
///
/// <para>Stitching vias join two plates: at a port's site through the port's own patch (shorted
/// in the port matrix), elsewhere at a mesh point, with the near-field inductance a point cannot
/// carry — between the mesh's effective radius at that point and the barrel's — added in series.
/// Open edges can carry a fringe (<see cref="PlaneStackSpec.Fringe"/>): the fringe capacitance
/// between the plates that end there, and for each cavity a strip of the width that holds its
/// air fringe, along which current flows, so that a long pair has the capacitance and the
/// inductance of the cross-section it came from.</para>
/// </summary>
public sealed class PlaneStack
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Epsilon0 = 8.8541878128e-12;
    private const double SpeedOfLight = 299792458.0;

    /// <summary>Nodes per ring of the grading around a port, and of a round port's outline.</summary>
    private const int RingNodes = 12;

    private static readonly IPolygonOps Ops = new ClipperPolygonOps();

    /// <summary>Dunavant's six-point rule, exact to degree 4 (the mass matrix of a quadratic
    /// element): barycentric coordinates and weight as a fraction of the area.</summary>
    private static readonly (double, double, double, double)[] Dunavant6 = BuildDunavant6();

    private static (double, double, double, double)[] BuildDunavant6()
    {
        const double a1 = 0.445948490915965, b1 = 0.108103018168070, w1 = 0.223381589678011;
        const double a2 = 0.091576213509771, b2 = 0.816847572980459, w2 = 0.109951743655322;
        return new[]
        {
            (b1, a1, a1, w1), (a1, b1, a1, w1), (a1, a1, b1, w1),
            (b2, a2, a2, w2), (a2, b2, a2, w2), (a2, a2, b2, w2)
        };
    }

    /// <summary>The cavities of one set of plates lying over one another.</summary>
    private sealed class CavityClass
    {
        public required ulong Mask;
        public required int[] Layers;                 // top down
        public required double[] Separation;          // per cavity [m]
        public required double[] Permittivity;        // εr of the cavity's dielectric
        public required double[] LossTangent;
        public double Area;
        public CsrMatrix Stiffness = null!, Mass = null!;
        public int Cavities => Layers.Length - 1;
    }

    private sealed class FringeGroup
    {
        public required int Class;
        public required EdgeFringe Fringe;
        public CsrMatrix Mass = null!, Stiffness = null!;   // along the edge, node level
        public double Length;
    }

    private readonly record struct PointTie(int Node, int UpperLayer, int LowerLayer, double SeriesHenries);

    private readonly List<CavityClass> _classes = new();
    private readonly List<FringeGroup> _fringe = new();
    private readonly ulong[] _nodeMask;
    private readonly int[] _dofStart;
    private readonly int _unknowns;
    private readonly int _halfBandwidth;
    private readonly int[] _rowPointers = Array.Empty<int>(), _columns = Array.Empty<int>();   // unknowns' pattern
    private readonly SparseComplexLdl? _ldl;                    // null: the band solve is cheaper
    private readonly double[][] _site;                          // ∫ N_i over the patch / patch area
    private readonly int[][] _siteNodes;                        // the nodes with a weight, ascending
    private readonly Point2[] _point;                           // every node, mid-edge ones included
    private readonly (int Site, int Upper, int Lower)[] _ports;  // external, then the patch ties
    private readonly int _externalPorts;
    private readonly List<PointTie> _ties = new();
    private readonly List<string> _notes = new();

    public PlaneStackSpec Spec { get; }
    public PlanarMesh Mesh { get; }
    public double MeshEdgeMeters { get; }

    /// <summary>Area with at least two plates over one another [m²].</summary>
    public double AreaSquareMeters { get; }

    /// <summary>Mesh nodes: the corners and one node per edge.</summary>
    public int NodeCount { get; }

    /// <summary>Unknowns of the solve: per node, one per plate there less the reference.</summary>
    public int UnknownCount => _unknowns;

    /// <summary>Half-bandwidth of the system in its solve order (the band solve costs n·b²).</summary>
    public int HalfBandwidth => _halfBandwidth;

    /// <summary>Whether the sparse LDLᵀ solves it (the band solve otherwise).</summary>
    public bool SparseSolve => _ldl is not null;

    /// <summary>Entries of the sparse factor, when it is used.</summary>
    public long FactorNonZeros => _ldl?.FactorNonZeros ?? 0;

    /// <summary>The cavities, one entry per pair of plates and dielectric.</summary>
    public IReadOnlyList<StackCavity> Cavities { get; }

    /// <summary>What the model made of the input (dropped slivers, ties it could not place, …).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Total length of open edge that carries a fringe [m].</summary>
    public double FringedEdgeMeters => _fringe.Sum(g => g.Length);

    /// <summary>Each kind of fringe on the open edges and the length of edge that carries it.</summary>
    public IReadOnlyList<(EdgeFringe Fringe, double LengthMeters)> FringedEdges =>
        _fringe.GroupBy(g => g.Fringe).Select(g => (g.Key, g.Sum(x => x.Length))).ToList();

    /// <summary>ε₀εr·A/d of every cavity, without the fringe: what the stack holds between
    /// neighbouring plates at low frequency.</summary>
    public double StaticCapacitanceFarads =>
        _classes.Sum(c => Enumerable.Range(0, c.Cavities).Sum(k => Epsilon0 * c.Permittivity[k] / c.Separation[k]) * c.Area);

    public PlaneStack(PlaneStackSpec spec) : this(spec, null) { }

    /// <param name="sparse">True: the sparse solve whatever the size; false: the band solve; null:
    /// by the cost of each (for tests to check one against the other).</param>
    internal PlaneStack(PlaneStackSpec spec, bool? sparse)
    {
        Spec = spec;
        int layerCount = spec.Layers.Count;
        if (layerCount < 2) throw new ArgumentException("A plane stack needs at least two layers.");
        if (layerCount > 63) throw new ArgumentException("A plane stack can have at most 63 layers.");
        for (int j = 0; j + 1 < layerCount; j++)
        {
            if (!(spec.Layers[j].GapBelowMeters > 0))
                throw new ArgumentException($"The dielectric below layer {spec.Layers[j].Name} must be thicker than zero.");
            if (!(spec.Layers[j].GapPermittivity >= 1))
                throw new ArgumentException($"The relative permittivity below layer {spec.Layers[j].Name} must be at least 1.");
        }
        if (spec.Ports.Count == 0) throw new ArgumentException("The plane stack needs at least one port.");
        foreach (var p in spec.Ports.Select(p => (p.Site.Name, p.UpperLayer, p.LowerLayer))
                     .Concat(spec.Ties.Select(t => (t.Name, t.UpperLayer, t.LowerLayer))))
            if (p.UpperLayer < 0 || p.UpperLayer >= layerCount || p.LowerLayer < 0 || p.LowerLayer >= layerCount
                || p.UpperLayer == p.LowerLayer)
                throw new ArgumentException($"Port or tie '{p.Name}' must join two different layers of the stack.");

        // ---------------------------------------------------------- the arrangement
        // One shape shared by every layer (the plane pair): the mesher sees it as the pair did.
        var allPlates = spec.Layers.SelectMany((l, j) => l.Plates.Select(p => (Layer: j, Plate: p))).ToList();
        bool single = spec.Layers.All(l => l.Plates.Count == 1)
                      && allPlates.All(p => ReferenceEquals(p.Plate.Shape, allPlates[0].Plate.Shape));
        var faces = new List<(IReadOnlyList<Polygon2> Shape, ulong Mask)>();
        if (single)
            faces.Add((allPlates[0].Plate.Shape, (1UL << layerCount) - 1));
        else
            faces = Arrangement(spec, _notes);
        if (faces.Count == 0)
            throw new InvalidOperationException("No two plates of the stack lie over one another: there is no cavity to solve.");

        double minX = faces.Min(f => f.Shape.Min(p => p.Outer.Min(q => q.X))), maxX = faces.Max(f => f.Shape.Max(p => p.Outer.Max(q => q.X)));
        double minY = faces.Min(f => f.Shape.Min(p => p.Outer.Min(q => q.Y))), maxY = faces.Max(f => f.Shape.Max(p => p.Outer.Max(q => q.Y)));
        double size = Math.Max(maxX - minX, maxY - minY);

        double h = spec.MeshEdgeMeters;
        if (!(h > 0))
        {
            // Ten quadratic elements per wavelength in the dielectric at the top frequency,
            // and at least twenty-five across the plane; never so fine that the banded solve
            // stops being interactive.
            double er = spec.Layers.Take(layerCount - 1).Max(l => l.GapPermittivity);
            double wavelength = SpeedOfLight / (Math.Sqrt(er) * Math.Max(spec.MaxFrequencyHz, 1));
            h = Math.Min(wavelength / 10, size / 25);
            double area = faces.Sum(f => f.Shape.Sum(p => p.Area()));
            h = Math.Max(h, Math.Sqrt(area / 4000));
        }
        MeshEdgeMeters = h;

        // ---------------------------------------------------------- port sites
        var faceIndex = faces.Select(f => new PolygonSetIndex(f.Shape)).ToList();
        int FaceOf(Point2 p)
        {
            for (int f = 0; f < faceIndex.Count; f++)
                if (faceIndex[f].Contains(p)) return f;
            return -1;
        }
        var siteOf = new Dictionary<PlanePort, int>();
        var sites = new List<PlanePort>();
        var siteFace = new List<int>();
        var regions = new List<PlanarRegion>();
        var refinements = new List<MeshRefinement>();
        var portList = new List<(int Site, int Upper, int Lower)>();
        foreach (var port in spec.Ports)
        {
            if (!siteOf.TryGetValue(port.Site, out int s))
            {
                var outline = OutlineOf(port.Site, h);
                int face = FaceOf(outline[0]);
                foreach (var vertex in outline)
                    if (face < 0 || !faceIndex[face].Contains(vertex))
                        throw new InvalidOperationException(single
                            ? $"Port '{port.Site.Name}' at ({port.Site.Center.X * 1e3:g4}, {port.Site.Center.Y * 1e3:g4}) mm is not " +
                              "wholly on the plane pair (it lies on or over an edge or a cut-out)."
                            : $"Port '{port.Site.Name}' at ({port.Site.Center.X * 1e3:g4}, {port.Site.Center.Y * 1e3:g4}) mm does not " +
                              "lie wholly within one region of the stack (it lies on or over a plate's edge, a split or a cut-out).");
                s = sites.Count;
                siteOf[port.Site] = s;
                sites.Add(port.Site);
                siteFace.Add(face);
                regions.Add(new PlanarRegion(s + 1, new[] { new Polygon2(outline) }));
                refinements.Add(Grading(port.Site.Center, outline, h));
            }
            ulong mask = faces[siteFace[s]].Mask;
            foreach (int layer in new[] { port.UpperLayer, port.LowerLayer })
                if ((mask & (1UL << layer)) == 0)
                    throw new InvalidOperationException(
                        $"Port '{port.Site.Name}' joins layer {spec.Layers[layer].Name}, which has no plate at " +
                        $"({port.Site.Center.X * 1e3:g4}, {port.Site.Center.Y * 1e3:g4}) mm.");
            portList.Add((s, port.UpperLayer, port.LowerLayer));
        }
        _externalPorts = portList.Count;
        for (int a = 0; a < sites.Count; a++)
            for (int b = a + 1; b < sites.Count; b++)
            {
                double apart = (sites[a].Center - sites[b].Center).Length;
                if (apart < 1.05 * (Reach(regions[a]) + Reach(regions[b])))
                    throw new InvalidOperationException(
                        $"Ports '{sites[a].Name}' and '{sites[b].Name}' overlap ({apart * 1e3:g3} mm apart).");
            }

        // Ties at a port's site use its patch; the rest become mesh points.
        var pointTies = new List<StackTie>();
        foreach (var tie in spec.Ties)
        {
            int at = Enumerable.Range(0, sites.Count)
                .FirstOrDefault(s => (sites[s].Center - tie.Position).Length < Math.Max(sites[s].RadiusMeters, tie.RadiusMeters), -1);
            if (at >= 0)
            {
                ulong mask = faces[siteFace[at]].Mask;
                if ((mask & (1UL << tie.UpperLayer)) == 0 || (mask & (1UL << tie.LowerLayer)) == 0)
                {
                    _notes.Add($"Tie '{tie.Name}' joins a layer with no plate at its site; it was left out.");
                    continue;
                }
                portList.Add((at, tie.UpperLayer, tie.LowerLayer));
                continue;
            }
            int face = FaceOf(tie.Position);
            if (face < 0 || (faces[face].Mask & (1UL << tie.UpperLayer)) == 0 || (faces[face].Mask & (1UL << tie.LowerLayer)) == 0)
            {
                _notes.Add($"Tie '{tie.Name}' at ({tie.Position.X * 1e3:g4}, {tie.Position.Y * 1e3:g4}) mm does not land on " +
                           "both of its plates inside the stack; it was left out.");
                continue;
            }
            pointTies.Add(tie);
            refinements.Add(new MeshRefinement(tie.Position, 0.5 * h, new[] { (tie.Position, h) }));
        }
        _ports = portList.ToArray();

        if (single)
            regions.Add(new PlanarRegion(0, faces[0].Shape));
        else
            for (int f = 0; f < faces.Count; f++)
                regions.Add(new PlanarRegion(sites.Count + 1 + f, faces[f].Shape));

        Mesh = new PlanarMesher().Mesh(regions, h, cleanPolygons: single, refinements);

        // ---------------------------------------------------------- elements and classes
        var edgeNode = new Dictionary<(int, int), int>();
        int n = Mesh.Points.Count;
        int Mid(int u, int v)
        {
            var key = u < v ? (u, v) : (v, u);
            if (!edgeNode.TryGetValue(key, out int id)) edgeNode[key] = id = n++;
            return id;
        }
        var elements = new int[Mesh.Triangles.Count][];
        var elementClass = new int[Mesh.Triangles.Count];
        var classOfMask = new Dictionary<ulong, int>();
        for (int e = 0; e < elements.Length; e++)
        {
            var t = Mesh.Triangles[e];
            elements[e] = new[] { t.A, t.B, t.C, Mid(t.A, t.B), Mid(t.B, t.C), Mid(t.C, t.A) };
            int face = t.RegionId >= 1 && t.RegionId <= sites.Count ? siteFace[t.RegionId - 1]
                : single ? 0 : t.RegionId - sites.Count - 1;
            ulong mask = faces[face].Mask;
            if (!classOfMask.TryGetValue(mask, out int c))
            {
                classOfMask[mask] = c = _classes.Count;
                _classes.Add(NewClass(spec, mask));
            }
            elementClass[e] = c;
        }
        NodeCount = n;
        _point = new Point2[n];
        for (int i = 0; i < Mesh.Points.Count; i++) _point[i] = Mesh.Points[i];
        foreach (var ((u, v), id) in edgeNode) _point[id] = (Mesh.Points[u] + Mesh.Points[v]) * 0.5;

        var stiffness = _classes.Select(_ => new SparseMatrixBuilder(n, n)).ToArray();
        var mass = _classes.Select(_ => new SparseMatrixBuilder(n, n)).ToArray();
        var pattern = new SparseMatrixBuilder(n, n);
        _site = new double[sites.Count][];
        var siteArea = new double[sites.Count];
        for (int k = 0; k < _site.Length; k++) _site[k] = new double[n];
        double total = 0;
        Span<double> ke = stackalloc double[36];
        Span<double> me = stackalloc double[36];
        Span<double> fe = stackalloc double[6];
        for (int e = 0; e < elements.Length; e++)
        {
            var t = Mesh.Triangles[e];
            double area = ElementMatrices(Mesh.Points[t.A], Mesh.Points[t.B], Mesh.Points[t.C], ke, me, fe);
            if (area <= 0) continue;
            total += area;
            int c = elementClass[e];
            _classes[c].Area += area;
            var nodes = elements[e];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                {
                    stiffness[c].Add(nodes[i], nodes[j], ke[6 * i + j]);
                    mass[c].Add(nodes[i], nodes[j], me[6 * i + j]);
                    if (_classes.Count > 1) pattern.Add(nodes[i], nodes[j], ke[6 * i + j]);
                }
            if (t.RegionId > 0 && t.RegionId <= sites.Count)
            {
                siteArea[t.RegionId - 1] += area;
                for (int i = 0; i < 6; i++) _site[t.RegionId - 1][nodes[i]] += fe[i];
            }
        }
        for (int k = 0; k < _site.Length; k++)
        {
            if (!(siteArea[k] > 0))
                throw new InvalidOperationException($"Port '{sites[k].Name}' received no mesh.");
            for (int i = 0; i < n; i++) _site[k][i] /= siteArea[k];
        }
        _siteNodes = _site.Select(w => Enumerable.Range(0, n).Where(i => w[i] != 0).ToArray()).ToArray();
        AreaSquareMeters = total;
        for (int c = 0; c < _classes.Count; c++)
        {
            _classes[c].Stiffness = stiffness[c].Build();
            _classes[c].Mass = mass[c].Build();
        }
        var graph = _classes.Count > 1 ? pattern.Build() : _classes[0].Stiffness;

        // ---------------------------------------------------------- unknowns
        _nodeMask = new ulong[n];
        for (int e = 0; e < elements.Length; e++)
            foreach (int node in elements[e])
                _nodeMask[node] |= _classes[elementClass[e]].Mask;
        var position = BandedComplexLu.ReverseCuthillMcKee(graph.RowPointers, graph.ColumnIndices);
        var byPosition = new int[n];
        for (int i = 0; i < n; i++) byPosition[position[i]] = i;
        _dofStart = new int[n];
        int next = 0;
        foreach (int node in byPosition)
        {
            _dofStart[node] = next;
            next += Unknowns(node);
        }
        _unknowns = next;

        // ---------------------------------------------------------- point ties
        if (pointTies.Count > 0)
        {
            var nodeElements = new List<int>[n];
            for (int e = 0; e < elements.Length; e++)
                foreach (int node in elements[e])
                    (nodeElements[node] ??= new List<int>()).Add(e);
            var boundaryNode = BoundaryNodes(elements, n);
            int floored = 0, edges = 0;
            foreach (var tie in pointTies)
            {
                int node = Enumerable.Range(0, Mesh.Points.Count).MinBy(i => (Mesh.Points[i] - tie.Position).Length);
                int e0 = nodeElements[node]!.MinBy(e => (Mesh.Centroid(Mesh.Triangles[e]) - tie.Position).Length);
                var cls = _classes[elementClass[e0]];
                int top = Math.Min(tie.UpperLayer, tie.LowerLayer), bottom = Math.Max(tie.UpperLayer, tie.LowerLayer);
                double crossed = 0;
                for (int k = 0; k < cls.Cavities; k++)
                    if (cls.Layers[k] >= top && cls.Layers[k + 1] <= bottom) crossed += cls.Separation[k];
                double floor = 1e-3 * Mu0 * crossed, series = floor;
                if (boundaryNode[node]) edges++;            // on a plate's open edge: no free-space near field
                else
                {
                    double effective = EffectiveRadius(node, elements, nodeElements, boundaryNode, Mesh.Points[node]);
                    series = Mu0 * crossed / (2 * Math.PI) * Math.Log(effective / tie.RadiusMeters);
                    if (series < floor) { series = floor; floored++; }
                }
                _ties.Add(new PointTie(node, tie.UpperLayer, tie.LowerLayer, series));
            }
            if (floored > 0)
                _notes.Add($"{floored} stitching via(s) sit where the mesh is finer than the barrel; they are taken as shorts.");
            if (edges > 0)
                _notes.Add($"{edges} stitching via(s) sit on a plate's open edge; their own inductance there is not added.");
        }

        // ---------------------------------------------------------- open edges
        if (spec.Fringe is { } fringeOf)
        {
            var count = new Dictionary<(int, int), (int Element, int Count)>();
            for (int e = 0; e < elements.Length; e++)
            {
                var el = elements[e];
                foreach (var (u, v) in new[] { (el[0], el[1]), (el[1], el[2]), (el[2], el[0]) })
                {
                    var key = u < v ? (u, v) : (v, u);
                    count[key] = count.TryGetValue(key, out var seen) ? (seen.Element, seen.Count + 1) : (e, 1);
                }
            }
            var groups = new Dictionary<(int, EdgeFringe), (FringeGroup Group, SparseMatrixBuilder M, SparseMatrixBuilder S)>();
            foreach (var ((u, v), (e, hits)) in count)
            {
                if (hits != 1) continue;
                var el = elements[e];
                int third = el.Take(3).First(x => x != u && x != v);
                Point2 a = Mesh.Points[u], b = Mesh.Points[v], c = Mesh.Points[third];
                double length = (b - a).Length;
                if (!(length > 0)) continue;
                var mid = (a + b) * 0.5;
                var normal = new Point2(-(b - a).Y / length, (b - a).X / length);
                if ((c.X - mid.X) * normal.X + (c.Y - mid.Y) * normal.Y > 0) normal = new Point2(-normal.X, -normal.Y);
                var cls = _classes[elementClass[e]];
                if (fringeOf(new FringeEdge(mid, normal, length, cls.Layers)) is not { } fringe) continue;
                var key = (elementClass[e], fringe);
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = (new FringeGroup { Class = elementClass[e], Fringe = fringe },
                        new SparseMatrixBuilder(n, n), new SparseMatrixBuilder(n, n));
                g.Group.Length += length;
                int m = edgeNode[u < v ? (u, v) : (v, u)];
                int[] line = { u, m, v };
                // Quadratic line element: mass L/30·[4 2 −1; 2 16 2; −1 2 4], stiffness 1/(3L)·[7 −8 1; −8 16 −8; 1 −8 7].
                double[,] lm = { { 4, 2, -1 }, { 2, 16, 2 }, { -1, 2, 4 } };
                double[,] ls = { { 7, -8, 1 }, { -8, 16, -8 }, { 1, -8, 7 } };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        g.M.Add(line[i], line[j], lm[i, j] * length / 30);
                        g.S.Add(line[i], line[j], ls[i, j] / (3 * length));
                    }
            }
            foreach (var (group, m, s) in groups.Values)
            {
                group.Mass = m.Build();
                group.Stiffness = s.Build();
                _fringe.Add(group);
            }
        }

        // ---------------------------------------------------------- bandwidth
        int band = 0;
        void Couple(int i, int j)
        {
            int di0 = _dofStart[i], dj0 = _dofStart[j];
            int ni = Unknowns(i), nj = Unknowns(j);
            if (ni <= 0 || nj <= 0) return;
            band = Math.Max(band, Math.Max(Math.Abs(di0 + ni - 1 - dj0), Math.Abs(dj0 + nj - 1 - di0)));
        }
        foreach (var c in _classes)
            for (int i = 0; i < n; i++)
                for (int k = c.Stiffness.RowPointers[i]; k < c.Stiffness.RowPointers[i + 1]; k++)
                    Couple(i, c.Stiffness.ColumnIndices[k]);
        _halfBandwidth = band;

        // ---------------------------------------------------------- the solver
        // Wide bands (several unknowns per node, rings of nodes round many ports) make n·b² minutes
        // per frequency; a nested-dissection LDLᵀ is then used, when its count of operations is
        // far below the band's. Problems of a few seconds stay on the band solve.
        double bandCost = (double)_unknowns * band * band;
        if (sparse ?? bandCost > 2e9)
        {
            var count = new int[_unknowns + 1];
            for (int i = 0; i < n; i++)
            {
                int width = 0;
                for (int k = graph.RowPointers[i]; k < graph.RowPointers[i + 1]; k++) width += Unknowns(graph.ColumnIndices[k]);
                for (int d = 0; d < Unknowns(i); d++) count[_dofStart[i] + d + 1] = width;
            }
            var rows = new int[_unknowns + 1];
            for (int r = 0; r < _unknowns; r++) rows[r + 1] = rows[r] + count[r + 1];
            var columns = new int[rows[_unknowns]];
            for (int i = 0; i < n; i++)
            {
                var neighbours = new List<int>();
                for (int k = graph.RowPointers[i]; k < graph.RowPointers[i + 1]; k++)
                {
                    int j = graph.ColumnIndices[k];
                    for (int d = 0; d < Unknowns(j); d++) neighbours.Add(_dofStart[j] + d);
                }
                neighbours.Sort();
                for (int d = 0; d < Unknowns(i); d++) neighbours.CopyTo(columns, rows[_dofStart[i] + d]);
            }
            var nodeOrder = SparseComplexLdl.NestedDissection(_point, graph.RowPointers, graph.ColumnIndices);
            var permutation = new int[_unknowns];
            int at = 0;
            foreach (int node in nodeOrder)
                for (int d = 0; d < Unknowns(node); d++) permutation[at++] = _dofStart[node] + d;
            var ldl = new SparseComplexLdl(rows, columns, permutation);
            if (sparse == true || 4.0 * ldl.Flops < bandCost)
            {
                _rowPointers = rows;
                _columns = columns;
                _ldl = ldl;
            }
        }

        var cavities = new List<StackCavity>();
        foreach (var c in _classes)
            for (int k = 0; k < c.Cavities; k++)
            {
                int existing = cavities.FindIndex(x => x.UpperLayer == c.Layers[k] && x.LowerLayer == c.Layers[k + 1]
                                                       && x.SeparationMeters == c.Separation[k]);
                if (existing >= 0)
                    cavities[existing] = cavities[existing] with { AreaSquareMeters = cavities[existing].AreaSquareMeters + c.Area };
                else
                    cavities.Add(new StackCavity(c.Layers[k], c.Layers[k + 1], c.Separation[k], c.Permittivity[k], c.LossTangent[k], c.Area));
            }
        Cavities = cavities;
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------

    /// <summary>
    /// The plates cut into regions over which the same layers have copper; those with two or
    /// more are the stack's domain. Each plate is cleaned first: the regions share their
    /// boundaries vertex for vertex and must not be cleaned again one by one.
    /// </summary>
    private static List<(IReadOnlyList<Polygon2> Shape, ulong Mask)> Arrangement(PlaneStackSpec spec, List<string> notes)
    {
        var faces = new List<(IReadOnlyList<Polygon2> Shape, ulong Mask)>();
        var covered = new List<Polygon2>();
        for (int layer = 0; layer < spec.Layers.Count; layer++)
            foreach (var plate in spec.Layers[layer].Plates)
            {
                var shape = PolygonCleaner.Clean(plate.Shape);
                if (shape.Count == 0) continue;
                var rings = shape.SelectMany(Polygon2.OrientedRings).ToList();
                var next = new List<(IReadOnlyList<Polygon2>, ulong)>();
                foreach (var (face, mask) in faces)
                {
                    var inside = Ops.Intersect(face, rings);
                    var outside = Ops.Difference(face, rings);
                    if (inside.Count > 0) next.Add((inside, mask | (1UL << layer)));
                    if (outside.Count > 0) next.Add((outside, mask));
                }
                var fresh = covered.Count > 0 ? Ops.Difference(shape, covered.SelectMany(Polygon2.OrientedRings)) : shape;
                if (fresh.Count > 0) next.Add((fresh, 1UL << layer));
                faces = next;
                covered = Ops.Union(covered.SelectMany(Polygon2.OrientedRings).Concat(rings)).ToList();
            }
        var domain = new List<(IReadOnlyList<Polygon2>, ulong)>();
        int slivers = 0;
        foreach (var (face, mask) in faces)
        {
            if (System.Numerics.BitOperations.PopCount(mask) < 2) continue;
            var kept = face.Where(p => p.Area() >= 1e-10).ToList();
            slivers += face.Count - kept.Count;
            if (kept.Count > 0) domain.Add((kept, mask));
        }
        if (slivers > 0)
            notes.Add($"{slivers} sliver(s) under 100 µm² where plates' edges nearly meet were left out of the stack.");
        return domain;
    }

    private static CavityClass NewClass(PlaneStackSpec spec, ulong mask)
    {
        var layers = Enumerable.Range(0, spec.Layers.Count).Where(j => (mask & (1UL << j)) != 0).ToArray();
        int m = layers.Length - 1;
        var d = new double[m];
        var er = new double[m];
        var tan = new double[m];
        for (int k = 0; k < m; k++)
        {
            int upper = layers[k], lower = layers[k + 1];
            if (lower == upper + 1)
            {
                // One dielectric: its own values, untouched by arithmetic.
                d[k] = spec.Layers[upper].GapBelowMeters;
                er[k] = spec.Layers[upper].GapPermittivity;
                tan[k] = spec.Layers[upper].GapLossTangent;
                continue;
            }
            // Several dielectrics in series; a layer without copper here is filled with resin, the
            // mean of the dielectrics either side of it (as EdgeCrossSection fills it).
            double thickness = 0, overEps = 0, lossOverEps = 0;
            for (int j = upper; j < lower; j++)
            {
                var l = spec.Layers[j];
                thickness += l.GapBelowMeters;
                overEps += l.GapBelowMeters / l.GapPermittivity;
                lossOverEps += l.GapBelowMeters * l.GapLossTangent / l.GapPermittivity;
                if (j > upper)
                {
                    var above = spec.Layers[j - 1];
                    double fill = 0.5 * (above.GapPermittivity + l.GapPermittivity);
                    double fillLoss = 0.5 * (above.GapLossTangent + l.GapLossTangent);
                    thickness += l.CopperThicknessMeters;
                    overEps += l.CopperThicknessMeters / fill;
                    lossOverEps += l.CopperThicknessMeters * fillLoss / fill;
                }
            }
            d[k] = thickness;
            er[k] = thickness / overEps;
            tan[k] = lossOverEps / overEps;
        }
        return new CavityClass { Mask = mask, Layers = layers, Separation = d, Permittivity = er, LossTangent = tan };
    }

    /// <summary>Stiffness, mass and load vector of a quadratic triangle; returns its area.</summary>
    private static double ElementMatrices(Point2 a, Point2 b, Point2 c, Span<double> ke, Span<double> me, Span<double> fe)
    {
        double twice = Point2.Cross(b - a, c - a);
        double area = Math.Abs(twice) / 2;
        if (area <= 0) return 0;
        Span<double> gx = stackalloc double[3];
        Span<double> gy = stackalloc double[3];
        Span<double> shape = stackalloc double[6];
        Span<double> dx = stackalloc double[6];
        Span<double> dy = stackalloc double[6];
        Span<double> l = stackalloc double[3];
        // Gradients of the barycentric coordinates.
        gx[0] = (b.Y - c.Y) / twice; gx[1] = (c.Y - a.Y) / twice; gx[2] = (a.Y - b.Y) / twice;
        gy[0] = (c.X - b.X) / twice; gy[1] = (a.X - c.X) / twice; gy[2] = (b.X - a.X) / twice;
        ke.Clear(); me.Clear(); fe.Clear();
        foreach (var (l0, l1, l2, weight) in Dunavant6)
        {
            l[0] = l0; l[1] = l1; l[2] = l2;
            for (int i = 0; i < 3; i++)
            {
                shape[i] = l[i] * (2 * l[i] - 1);
                dx[i] = (4 * l[i] - 1) * gx[i];
                dy[i] = (4 * l[i] - 1) * gy[i];
                int j = (i + 1) % 3;
                shape[3 + i] = 4 * l[i] * l[j];
                dx[3 + i] = 4 * (l[i] * gx[j] + l[j] * gx[i]);
                dy[3 + i] = 4 * (l[i] * gy[j] + l[j] * gy[i]);
            }
            double w = weight * area;
            for (int i = 0; i < 6; i++)
            {
                fe[i] += w * shape[i];
                for (int j = 0; j < 6; j++)
                {
                    ke[6 * i + j] += w * (dx[i] * dx[j] + dy[i] * dy[j]);
                    me[6 * i + j] += w * shape[i] * shape[j];
                }
            }
        }
        return area;
    }

    private static bool[] BoundaryNodes(int[][] elements, int n)
    {
        var count = new Dictionary<(int, int), int>();
        foreach (var el in elements)
            foreach (var (u, v) in new[] { (el[0], el[1]), (el[1], el[2]), (el[2], el[0]) })
            {
                var key = u < v ? (u, v) : (v, u);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var boundary = new bool[n];
        foreach (var el in elements)
        {
            (int u, int v, int m)[] edges = { (el[0], el[1], el[3]), (el[1], el[2], el[4]), (el[2], el[0], el[5]) };
            foreach (var (u, v, m) in edges)
                if (count[u < v ? (u, v) : (v, u)] == 1)
                    boundary[u] = boundary[v] = boundary[m] = true;
        }
        return boundary;
    }

    /// <summary>Rings of elements around a point tie in its effective-radius solve.</summary>
    private const int Rings = 3;

    /// <summary>
    /// The radius of the barrel a unit point source at <paramref name="node"/> stands for on
    /// this mesh: the discrete solution of −∇²u = δ on three rings of elements around the node,
    /// with the free-space solution −ln(r)/2π held on the rings' outer nodes, reads
    /// −ln(r_eff)/2π at the node (the well index of reservoir simulation).
    /// </summary>
    private double EffectiveRadius(int node, int[][] elements, List<int>?[] nodeElements, bool[] boundaryNode, Point2 at)
    {
        var patch = new HashSet<int>(nodeElements[node]!);
        for (int ring = 1; ring < Rings; ring++)
            foreach (int e in patch.ToList())
                for (int v = 0; v < 3; v++)
                    foreach (int e2 in nodeElements[elements[e][v]]!) patch.Add(e2);
        var local = new Dictionary<int, int>();
        var interior = new List<int>();
        foreach (int e in patch)
            foreach (int v in elements[e])
            {
                if (local.ContainsKey(v)) continue;
                bool inside = !boundaryNode[v] && nodeElements[v]!.All(patch.Contains);
                local[v] = inside ? interior.Count : -1;
                if (inside) interior.Add(v);
            }
        int m = interior.Count;
        var k = new double[m * m];
        var rhs = new double[m];
        rhs[local[node]] = 1;
        Span<double> ke = stackalloc double[36];
        Span<double> me = stackalloc double[36];
        Span<double> fe = stackalloc double[6];
        foreach (int e in patch)
        {
            var el = elements[e];
            if (ElementMatrices(Mesh.Points[el[0]], Mesh.Points[el[1]], Mesh.Points[el[2]], ke, me, fe) <= 0) continue;
            for (int i = 0; i < 6; i++)
            {
                int row = local[el[i]];
                if (row < 0) continue;
                for (int j = 0; j < 6; j++)
                {
                    int column = local[el[j]];
                    if (column >= 0) k[row * m + column] += ke[6 * i + j];
                    else rhs[row] += ke[6 * i + j] * Math.Log((_point[el[j]] - at).Length) / (2 * Math.PI);
                }
            }
        }
        var pivot = new int[m];
        var lu = DenseLu.FactorInPlace(k, m, pivot);
        var u = new double[m];
        lu.Solve(rhs, u);
        return Math.Exp(-2 * Math.PI * u[local[node]]);
    }

    private static double Reach(PlanarRegion region) =>
        region.Polygons[0].Outer.Max(p => (p - Centroid(region.Polygons[0].Outer)).Length);

    private static Point2 Centroid(IReadOnlyList<Point2> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring) { x += p.X; y += p.Y; }
        return new Point2(x / ring.Count, y / ring.Count);
    }

    /// <summary>The port patch as a polygon: the given outline with its edges cut to the
    /// local element size, or a regular polygon of the disc's area.</summary>
    internal static List<Point2> OutlineOf(PlanePort port, double h)
    {
        var outline = new List<Point2>();
        if (port.Outline is { Count: >= 3 } given)
        {
            double perimeter = 0;
            for (int i = 0; i < given.Count; i++) perimeter += (given[(i + 1) % given.Count] - given[i]).Length;
            double step = Math.Min(h, perimeter / RingNodes);
            for (int i = 0; i < given.Count; i++)
            {
                var a = given[i]; var b = given[(i + 1) % given.Count];
                int pieces = Math.Max(1, (int)Math.Ceiling((b - a).Length / step - 1e-9));
                for (int s = 0; s < pieces; s++) outline.Add(a + (b - a) * ((double)s / pieces));
            }
            if (Polygon2.RingArea(outline) < 0) outline.Reverse();
            return outline;
        }
        if (!(port.RadiusMeters > 0))
            throw new ArgumentException($"Port '{port.Name}' needs a radius or an outline.");
        // Same area as the disc: π r² = ½ n R² sin(2π/n).
        double vertexRadius = port.RadiusMeters * Math.Sqrt(2 * Math.PI / (RingNodes * Math.Sin(2 * Math.PI / RingNodes)));
        for (int i = 0; i < RingNodes; i++)
        {
            double angle = 2 * Math.PI * i / RingNodes;
            outline.Add(new Point2(port.Center.X + vertexRadius * Math.Cos(angle), port.Center.Y + vertexRadius * Math.Sin(angle)));
        }
        return outline;
    }

    /// <summary>
    /// Rings of nodes around a port: inside the patch (which carries a uniform current
    /// density, so the voltage is curved there), then outward with each ring a constant
    /// factor larger than the last, until the spacing along a ring reaches the target edge
    /// length. The voltage near a port goes as ln r, and a geometric progression of rings
    /// resolves every decade of it alike.
    /// </summary>
    private static MeshRefinement Grading(Point2 center, IReadOnlyList<Point2> outline, double h)
    {
        double reach = outline.Max(p => (p - center).Length);
        double inner = outline.Min(p => (p - center).Length);
        var points = new List<(Point2, double)> { (center, 0.5 * inner) };
        void Ring(double radius, int count, double phase)
        {
            double spacing = 2 * Math.PI * radius / count;
            for (int i = 0; i < count; i++)
            {
                double angle = phase + 2 * Math.PI * i / count;
                points.Add((new Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), spacing));
            }
        }
        Ring(0.33 * inner, 6, 0);
        Ring(0.66 * inner, 12, Math.PI / 12);

        double growth = 1 + 2 * Math.PI / RingNodes;
        double radius = reach * growth;
        double last = reach;
        int ring = 0;
        while (2 * Math.PI * radius / RingNodes < 0.9 * h)
        {
            Ring(radius, RingNodes, (ring++ % 2) * Math.PI / RingNodes + Math.PI / RingNodes);
            last = radius;
            radius *= growth;
        }
        // With rings laid, the lattice starts most of an edge length beyond the last one;
        // with none (a patch as large as the elements) only the patch itself is kept clear.
        return new MeshRefinement(center, ring > 0 ? last + 0.7 * h : reach, points);
    }

    // ------------------------------------------------------------------
    // The unknowns
    // ------------------------------------------------------------------

    /// <summary>The node's unknowns: one per cavity there.</summary>
    private int Unknowns(int node) => System.Numerics.BitOperations.PopCount(_nodeMask[node]) - 1;

    /// <summary>The direct solve of one frequency: the band LU, or the sparse LDLᵀ with every
    /// solve's residual checked and refined, and the band LU taken instead when a pivot comes out
    /// small or the refinement does not converge.</summary>
    private sealed class Workspace
    {
        private readonly PlaneStack _stack;
        private readonly BandedComplexLu? _band;
        private readonly SparseComplexLdl? _ldl;
        private readonly double[] _re = Array.Empty<double>(), _im = Array.Empty<double>();
        private BandedComplexLu? _fallback;
        private bool _useFallback;
        private double _norm;                                   // ‖A‖∞ of the last factorization

        public Workspace(PlaneStack stack)
        {
            _stack = stack;
            if (stack._ldl is null) _band = new BandedComplexLu(stack._unknowns, stack._halfBandwidth);
            else
            {
                _ldl = stack._ldl.Clone();
                _re = new double[stack._columns.Length];
                _im = new double[stack._columns.Length];
            }
        }

        /// <summary>Bytes one workspace holds.</summary>
        public static long StorageBytes(PlaneStack stack) => stack._ldl is null
            ? BandedComplexLu.StorageBytes(stack._unknowns, stack._halfBandwidth)
            : stack._ldl.StorageBytes + 16L * stack._columns.Length;

        public void Clear()
        {
            _useFallback = false;
            if (_band is not null) { _band.Clear(); return; }
            Array.Clear(_re);
            Array.Clear(_im);
        }

        public void Add(int row, int column, Complex value)
        {
            if (_band is not null) { _band.Add(row, column, value); return; }
            int p = Array.BinarySearch(_stack._columns, _stack._rowPointers[row], _stack._rowPointers[row + 1] - _stack._rowPointers[row], column);
            _re[p] += value.Real;
            _im[p] += value.Imaginary;
        }

        public void Factor()
        {
            if (_band is not null) { _band.Factor(); return; }
            _norm = 0;
            var rows = _stack._rowPointers;
            for (int r = 0; r < _stack._unknowns; r++)
            {
                double sum = 0;
                for (int p = rows[r]; p < rows[r + 1]; p++) sum += Math.Sqrt(_re[p] * _re[p] + _im[p] * _im[p]);
                _norm = Math.Max(_norm, sum);
            }
            if (!_ldl!.Factor(_re, _im)) UseBand();
        }

        private void UseBand()
        {
            _fallback ??= new BandedComplexLu(_stack._unknowns, _stack._halfBandwidth);
            _fallback.Clear();
            var rows = _stack._rowPointers;
            for (int r = 0; r < _stack._unknowns; r++)
                for (int p = rows[r]; p < rows[r + 1]; p++)
                    if (_re[p] != 0 || _im[p] != 0) _fallback.Add(r, _stack._columns[p], new Complex(_re[p], _im[p]));
            _fallback.Factor();
            _useFallback = true;
        }

        public void Solve(Complex[] x)
        {
            if (_band is not null) { _band.Solve(x); return; }
            if (_useFallback) { _fallback!.Solve(x); return; }
            int n = x.Length;
            var b = (Complex[])x.Clone();
            var xr = new double[n];
            var xi = new double[n];
            for (int i = 0; i < n; i++) { xr[i] = x[i].Real; xi[i] = x[i].Imaginary; }
            _ldl!.Solve(xr, xi);
            double scale = 0;
            foreach (var v in b) scale = Math.Max(scale, v.Magnitude);
            var rr = new double[n];
            var ri = new double[n];
            var rows = _stack._rowPointers;
            var columns = _stack._columns;
            for (int pass = 0; ; pass++)
            {
                // r = b − A·x, judged as a backward error: against ‖A‖·‖x‖ + ‖b‖, which is what any
                // stable solve leaves when A is ill-conditioned (its static modes at low frequency).
                double worst = 0, size = 0;
                for (int i = 0; i < n; i++) size = Math.Max(size, Math.Sqrt(xr[i] * xr[i] + xi[i] * xi[i]));
                double allowed = _norm * size + scale;
                for (int r = 0; r < n; r++)
                {
                    double sr = b[r].Real, si = b[r].Imaginary;
                    for (int p = rows[r]; p < rows[r + 1]; p++)
                    {
                        int c = columns[p];
                        sr -= _re[p] * xr[c] - _im[p] * xi[c];
                        si -= _re[p] * xi[c] + _im[p] * xr[c];
                    }
                    rr[r] = sr; ri[r] = si;
                    worst = Math.Max(worst, Math.Sqrt(sr * sr + si * si));
                }
                if (worst <= 1e-14 * allowed) break;
                if (pass == 3)
                {
                    if (worst > 1e-11 * allowed)
                    {
                        UseBand();
                        Array.Copy(b, x, n);
                        _fallback!.Solve(x);
                        return;
                    }
                    break;
                }
                _ldl.Solve(rr, ri);
                for (int i = 0; i < n; i++) { xr[i] += rr[i]; xi[i] += ri[i]; }
            }
            for (int i = 0; i < n; i++) x[i] = new Complex(xr[i], xi[i]);
        }
    }

    /// <summary>The number of plates at a node above <paramref name="layer"/> — the index of
    /// the node's cavity just below that plate.</summary>
    private int Rank(int node, int layer) =>
        System.Numerics.BitOperations.PopCount(_nodeMask[node] & ((1UL << layer) - 1));

    /// <summary>The unknowns whose sum is φ_a − φ_b at a node (the cavities between the two
    /// plates there), and the sign of that sum.</summary>
    private (int From, int To, int Sign) Between(int node, int a, int b)
    {
        int ra = Rank(node, a), rb = Rank(node, b);
        return a < b ? (_dofStart[node] + ra, _dofStart[node] + rb, 1) : (_dofStart[node] + rb, _dofStart[node] + ra, -1);
    }

    // ------------------------------------------------------------------
    // Material constants
    // ------------------------------------------------------------------

    /// <summary>(k/σ)·coth(k·t): the impedance per square of a plate face, with the field
    /// decaying through the thickness.</summary>
    private Complex SurfaceImpedance(double omega, double thickness)
    {
        double sigma = Spec.ConductivitySiemensPerMeter;
        if (!(sigma > 0) || double.IsInfinity(sigma)) return Complex.Zero;
        if (!(thickness > 0)) return Complex.Zero;
        Complex k = Complex.Sqrt(new Complex(0, omega * Mu0 * sigma));
        Complex kt = k * thickness;
        // coth(x) = 1/x + x/3 − … for small x; the direct form for the rest.
        Complex coth = kt.Magnitude < 1e-3 ? 1 / kt + kt / 3 : 1 / Complex.Tanh(kt);
        return k / sigma * coth;
    }

    /// <summary>(k/σ)/sinh(k·t): the field on one face of a plate per unit current on the other.</summary>
    private Complex TransferImpedance(double omega, double thickness)
    {
        double sigma = Spec.ConductivitySiemensPerMeter;
        if (!(sigma > 0) || double.IsInfinity(sigma)) return Complex.Zero;
        if (!(thickness > 0)) return Complex.Zero;
        Complex k = Complex.Sqrt(new Complex(0, omega * Mu0 * sigma));
        Complex kt = k * thickness;
        if (kt.Real > 300) return Complex.Zero;
        Complex csch = kt.Magnitude < 1e-3 ? 1 / kt - kt / 6 : 1 / Complex.Sinh(kt);
        return k / sigma * csch;
    }

    /// <summary>The cavity's relative permittivity at a frequency (complex, ε′ − jε″).</summary>
    private Complex Permittivity(double er, double tan, double frequencyHz) =>
        Spec.WidebandDielectric && tan > 0
            ? er * (1 + tan * new WidebandDebye(Spec.DielectricReferenceHz).Shape(frequencyHz))
            : er * new Complex(1, -tan);

    /// <summary>The cavity series impedance matrix Z of a class (tridiagonal).</summary>
    private Complex[,] SeriesMatrix(CavityClass c, double omega)
    {
        int m = c.Cavities;
        var z = new Complex[m, m];
        for (int k = 0; k < m; k++)
        {
            z[k, k] = new Complex(0, omega * Mu0 * c.Separation[k])
                + SurfaceImpedance(omega, Spec.Layers[c.Layers[k]].CopperThicknessMeters)
                + SurfaceImpedance(omega, Spec.Layers[c.Layers[k + 1]].CopperThicknessMeters);
            if (k + 1 < m)
                z[k, k + 1] = z[k + 1, k] = -TransferImpedance(omega, Spec.Layers[c.Layers[k + 1]].CopperThicknessMeters);
        }
        return z;
    }

    /// <summary>The class's cavity coefficients at a frequency: Z⁻¹ on the stiffness and the
    /// shunt admittance Y (diagonal) on the mass.</summary>
    private (Complex[,] Gradient, Complex[,] Shunt) CavityCoefficients(CavityClass c, double frequencyHz)
    {
        double omega = 2 * Math.PI * frequencyHz;
        int m = c.Cavities;
        var series = SeriesMatrix(c, omega);
        var inverse = m == 1 ? new[,] { { 1 / series[0, 0] } } : Invert(series);
        var shunt = new Complex[m, m];
        for (int k = 0; k < m; k++)
            shunt[k, k] = new Complex(0, omega) * Epsilon0 * Permittivity(c.Permittivity[k], c.LossTangent[k], frequencyHz) / c.Separation[k];
        return (inverse, shunt);
    }

    private static Complex[,] Invert(Complex[,] a)
    {
        int n = a.GetLength(0);
        var m = (Complex[,])a.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
                if (m[r, col].Magnitude > m[pivot, col].Magnitude) pivot = r;
            if (pivot != col)
                for (int j = 0; j < n; j++)
                {
                    (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
                    (inv[col, j], inv[pivot, j]) = (inv[pivot, j], inv[col, j]);
                }
            Complex d = m[col, col];
            for (int j = 0; j < n; j++) { m[col, j] /= d; inv[col, j] /= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                Complex f = m[r, col];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < n; j++) { m[r, j] -= f * m[col, j]; inv[r, j] -= f * inv[col, j]; }
            }
        }
        return inv;
    }

    /// <summary>
    /// The fringe's cavity coefficients for a class at a frequency: the edge capacitance between
    /// every pair of plates ending there (on the line mass; a pair's voltage is the sum of the
    /// cavities between them), and the strips' sheet admittance (on the line stiffness). The
    /// dielectric part of a capacitance follows the permittivity of the cavities between the
    /// two plates.
    /// </summary>
    private (Complex[,] Strip, Complex[,] Capacitance) FringeCoefficients(CavityClass c, EdgeFringe fringe, double frequencyHz,
        bool lossless = false)
    {
        double omega = 2 * Math.PI * frequencyHz;
        int m = c.Cavities;
        var capacitance = new Complex[m, m];
        var strip = new Complex[m, m];
        var series = lossless ? null : SeriesMatrix(c, omega);
        foreach (var pair in fringe.Pairs)
        {
            int a = Array.IndexOf(c.Layers, Math.Min(pair.UpperLayer, pair.LowerLayer));
            int b = Array.IndexOf(c.Layers, Math.Max(pair.UpperLayer, pair.LowerLayer));
            if (a < 0 || b < 0) continue;
            Complex cab = pair.FaradsPerMeter;
            if (!lossless)
            {
                // Series permittivity of the cavities between a and b, relative to its static value.
                Complex overEps = Complex.Zero;
                double overEpsStatic = 0;
                for (int k = a; k < b; k++)
                {
                    overEps += c.Separation[k] / Permittivity(c.Permittivity[k], c.LossTangent[k], frequencyHz);
                    overEpsStatic += c.Separation[k] / c.Permittivity[k];
                }
                cab = pair.AirFaradsPerMeter + (pair.FaradsPerMeter - pair.AirFaradsPerMeter) * (overEpsStatic / overEps);
            }
            Complex y = lossless ? cab : new Complex(0, omega) * cab;
            for (int k = a; k < b; k++)
                for (int l = a; l < b; l++) capacitance[k, l] += y;
            if (b == a + 1 && pair.AirFaradsPerMeter > 0)
            {
                // The strip that carries the air fringe as plate: width C_air·d/ε₀.
                double width = pair.AirFaradsPerMeter * c.Separation[a] / Epsilon0;
                strip[a, a] += lossless ? width / (Mu0 * c.Separation[a]) : width / series![a, a];
            }
        }
        return (strip, capacitance);
    }

    // ------------------------------------------------------------------
    // Frequency response
    // ------------------------------------------------------------------

    private void Assemble(Workspace lu, double frequencyHz)
    {
        lu.Clear();
        foreach (var c in _classes)
        {
            var (gradient, shunt) = CavityCoefficients(c, frequencyHz);
            AddBlock((r, s, v) => lu.Add(r, s, v), c, c.Stiffness, c.Mass, gradient, shunt);
        }
        foreach (var g in _fringe)
        {
            var c = _classes[g.Class];
            var (strip, capacitance) = FringeCoefficients(c, g.Fringe, frequencyHz);
            AddBlock((r, s, v) => lu.Add(r, s, v), c, g.Stiffness, g.Mass, strip, capacitance);
        }
        double omega = 2 * Math.PI * frequencyHz;
        foreach (var tie in _ties)
            AddTie((r, s, v) => lu.Add(r, s, v), tie, 1 / new Complex(0, omega * tie.SeriesHenries));
    }

    /// <summary>A branch of admittance y between the two plates of a tie: on every pair of the
    /// unknowns whose sum is their voltage.</summary>
    private void AddTie(Action<int, int, Complex> add, PointTie tie, Complex y)
    {
        var (from, to, _) = Between(tie.Node, tie.UpperLayer, tie.LowerLayer);
        for (int r = from; r < to; r++)
            for (int s = from; s < to; s++) add(r, s, y);
    }

    /// <summary>
    /// Adds cavity coefficients times two node-level matrices of one pattern. A class cavity at
    /// a node is the sum of the node's own cavities between the class's two plates (more than
    /// one where the node also touches a region with a plate between them).
    /// </summary>
    private void AddBlock(Action<int, int, Complex> add, CavityClass c, CsrMatrix k, CsrMatrix m, Complex[,] onK, Complex[,] onM)
    {
        int cavities = c.Cavities;
        var rowFrom = new int[cavities];
        var rowTo = new int[cavities];
        for (int i = 0; i < NodeCount; i++)
        {
            if (k.RowPointers[i] == k.RowPointers[i + 1]) continue;
            for (int a = 0; a < cavities; a++)
            {
                rowFrom[a] = _dofStart[i] + Rank(i, c.Layers[a]);
                rowTo[a] = _dofStart[i] + Rank(i, c.Layers[a + 1]);
            }
            for (int p = k.RowPointers[i]; p < k.RowPointers[i + 1]; p++)
            {
                int j = k.ColumnIndices[p];
                double kv = k.Values[p], mv = m.Values[p];
                for (int a = 0; a < cavities; a++)
                    for (int b = 0; b < cavities; b++)
                    {
                        Complex g = onK[a, b], y = onM[a, b];
                        if (g == Complex.Zero && y == Complex.Zero) continue;
                        Complex value = g * kv + y * mv;
                        int columnFrom = _dofStart[j] + Rank(j, c.Layers[b]), columnTo = _dofStart[j] + Rank(j, c.Layers[b + 1]);
                        for (int row = rowFrom[a]; row < rowTo[a]; row++)
                            for (int column = columnFrom; column < columnTo; column++)
                                add(row, column, value);
                    }
            }
        }
    }

    /// <summary>The port impedance matrix [Ω] at one frequency.</summary>
    public Complex[,] Impedance(double frequencyHz) => Impedance(frequencyHz, null);

    private Complex[,] Impedance(double frequencyHz, Workspace? workspace)
    {
        if (!(frequencyHz > 0))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz),
                "The plane impedance is defined for frequencies above zero (at DC a plane pair is an open circuit).");
        int ports = _ports.Length;
        var lu = workspace ?? new Workspace(this);
        Assemble(lu, frequencyHz);
        lu.Factor();

        var z = new Complex[ports, ports];
        var x = new Complex[_unknowns];
        for (int q = 0; q < ports; q++)
        {
            Array.Clear(x);
            var (site, upper, lower) = _ports[q];
            var weights = _site[site];
            foreach (int i in _siteNodes[site])
            {
                var (from, to, sign) = Between(i, upper, lower);
                for (int d = from; d < to; d++) x[d] += sign * weights[i];
            }
            lu.Solve(x);
            for (int p = 0; p < ports; p++)
            {
                var (sp, up, lp) = _ports[p];
                Complex sum = Complex.Zero;
                var w = _site[sp];
                foreach (int i in _siteNodes[sp])
                {
                    var (from, to, sign) = Between(i, up, lp);
                    Complex voltage = x[from];
                    for (int d = from + 1; d < to; d++) voltage += x[d];
                    sum += w[i] * (sign > 0 ? voltage : -voltage);
                }
                z[p, q] = sum;
            }
        }
        // Symmetric by construction; remove the rounding asymmetry.
        for (int p = 0; p < ports; p++)
            for (int q = p + 1; q < ports; q++)
                z[p, q] = z[q, p] = 0.5 * (z[p, q] + z[q, p]);
        if (ports == _externalPorts) return z;

        // The ties at port sites are shorted: Z_ee − Z_et·Z_tt⁻¹·Z_te.
        int e = _externalPorts, t = ports - e;
        var ztt = new Complex[t, t];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < t; j++) ztt[i, j] = z[e + i, e + j];
        var inverse = Invert(ztt);
        var reduced = new Complex[e, e];
        for (int i = 0; i < e; i++)
            for (int j = 0; j < e; j++)
            {
                Complex sum = z[i, j];
                for (int a = 0; a < t; a++)
                {
                    if (z[i, e + a] == Complex.Zero) continue;
                    for (int b = 0; b < t; b++) sum -= z[i, e + a] * inverse[a, b] * z[e + b, j];
                }
                reduced[i, j] = sum;
            }
        for (int p = 0; p < e; p++)
            for (int q = p + 1; q < e; q++)
                reduced[p, q] = reduced[q, p] = 0.5 * (reduced[p, q] + reduced[q, p]);
        return reduced;
    }

    /// <summary>The port impedance matrix at each frequency, solved in parallel.</summary>
    public IReadOnlyList<Complex[,]> Sweep(IReadOnlyList<double> frequenciesHz,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new Complex[frequenciesHz.Count][,];
        long each = Workspace.StorageBytes(this);
        int workers = (int)Math.Clamp((1L << 31) / Math.Max(each, 1), 1, Environment.ProcessorCount);
        int done = 0;
        Parallel.For(0, frequenciesHz.Count,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            () => new Workspace(this),
            (i, _, lu) =>
            {
                result[i] = Impedance(frequenciesHz[i], lu);
                progress?.Report((double)Interlocked.Increment(ref done) / frequenciesHz.Count);
                return lu;
            },
            _ => { });
        return result;
    }

    // ------------------------------------------------------------------
    // Resonances
    // ------------------------------------------------------------------

    /// <summary>
    /// The lowest resonances of the stack with no loss: K·v = ω²·M·v, K the cavities'
    /// inductive stiffness (with the ties and the fringe strips) and M their capacitance (with
    /// the fringe). The static modes (each plate's uniform charge) are left out. The ties at
    /// port sites are taken at the patch's centre.
    /// </summary>
    public IReadOnlyList<PlaneMode> Modes(int count, CancellationToken cancellationToken = default)
    {
        if (count < 1) return Array.Empty<PlaneMode>();
        int n = _unknowns;
        int wanted = Math.Min(count + 1, n);

        // Real K and M at the unknowns' level.
        var kb = new SparseMatrixBuilder(n, n);
        var mb = new SparseMatrixBuilder(n, n);
        Complex[,] Diagonal(IEnumerable<double> values)
        {
            var v = values.ToArray();
            var a = new Complex[v.Length, v.Length];
            for (int k = 0; k < v.Length; k++) a[k, k] = v[k];
            return a;
        }
        var zero = new Func<int, Complex[,]>(m => new Complex[m, m]);
        double maxEr = 1;
        foreach (var c in _classes)
        {
            maxEr = Math.Max(maxEr, c.Permittivity.Max());
            AddBlock((r, s, v) => kb.Add(r, s, v.Real), c, c.Stiffness, c.Mass,
                Diagonal(c.Separation.Select(d => 1 / (Mu0 * d))), zero(c.Cavities));
            AddBlock((r, s, v) => mb.Add(r, s, v.Real), c, c.Stiffness, c.Mass,
                zero(c.Cavities), Diagonal(Enumerable.Range(0, c.Cavities).Select(k => Epsilon0 * c.Permittivity[k] / c.Separation[k])));
        }
        foreach (var g in _fringe)
        {
            var c = _classes[g.Class];
            var (strip, capacitance) = FringeCoefficients(c, g.Fringe, 1, lossless: true);
            AddBlock((r, s, v) => kb.Add(r, s, v.Real), c, g.Stiffness, g.Mass, strip, zero(c.Cavities));
            AddBlock((r, s, v) => mb.Add(r, s, v.Real), c, g.Stiffness, g.Mass, zero(c.Cavities), capacitance);
        }
        var ties = _ties.ToList();
        for (int q = _externalPorts; q < _ports.Length; q++)
        {
            var (site, upper, lower) = _ports[q];
            int centre = Enumerable.Range(0, Mesh.Points.Count).MinBy(i => (Mesh.Points[i] - SiteCentre(site)).Length);
            ties.Add(new PointTie(centre, upper, lower, 1e-3 * Mu0 * _classes.Min(c => c.Separation.Min())));
        }
        foreach (var tie in ties)
            AddTie((r, s, v) => kb.Add(r, s, v.Real), tie, 1 / tie.SeriesHenries);
        var stiffness = kb.Build();
        var mass = mb.Build();

        // Subspace iteration on (K + s·M)⁻¹·M with a band factorization. K alone is singular
        // (the static modes), hence the shift s, of the size of the first eigenvalue; it is
        // taken off again below.
        double shift = Math.Pow(Math.PI / Math.Sqrt(AreaSquareMeters), 2) * SpeedOfLight * SpeedOfLight / maxEr;
        var lu = new Workspace(this);
        for (int i = 0; i < n; i++)
        {
            for (int k = stiffness.RowPointers[i]; k < stiffness.RowPointers[i + 1]; k++)
                lu.Add(i, stiffness.ColumnIndices[k], stiffness.Values[k]);
            for (int k = mass.RowPointers[i]; k < mass.RowPointers[i + 1]; k++)
                lu.Add(i, mass.ColumnIndices[k], shift * mass.Values[k]);
        }
        lu.Factor();

        int block = Math.Min(n, 2 * wanted + 4);
        var x = new double[block][];
        uint seed = 12345;
        for (int j = 0; j < block; j++)
        {
            x[j] = new double[n];
            for (int i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;             // deterministic start vectors
                x[j][i] = j == 0 ? 1 : seed / (double)uint.MaxValue - 0.5;
            }
        }
        var work = new double[n];
        var rhs = new Complex[n];
        var values = new double[block];
        double[]? previous = null;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < block; j++)
            {
                mass.Multiply(x[j], work);
                for (int i = 0; i < n; i++) rhs[i] = work[i];
                lu.Solve(rhs);
                for (int i = 0; i < n; i++) x[j][i] = rhs[i].Real;
            }
            // M-orthonormalise (twice, for the digits the first pass loses).
            for (int pass = 0; pass < 2; pass++)
                for (int j = 0; j < block; j++)
                {
                    for (int p = 0; p < j; p++)
                    {
                        mass.Multiply(x[p], work);
                        double dot = 0;
                        for (int i = 0; i < n; i++) dot += x[j][i] * work[i];
                        for (int i = 0; i < n; i++) x[j][i] -= dot * x[p][i];
                    }
                    mass.Multiply(x[j], work);
                    double norm = 0;
                    for (int i = 0; i < n; i++) norm += x[j][i] * work[i];
                    norm = Math.Sqrt(Math.Max(norm, double.Epsilon));
                    for (int i = 0; i < n; i++) x[j][i] /= norm;
                }
            // Rayleigh–Ritz: the projected stiffness, diagonalised.
            var projected = new double[block][];
            var kx = new double[block][];
            for (int j = 0; j < block; j++)
            {
                kx[j] = new double[n];
                stiffness.Multiply(x[j], kx[j]);
            }
            for (int i = 0; i < block; i++)
            {
                projected[i] = new double[block];
                for (int j = 0; j < block; j++)
                {
                    double dot = 0;
                    for (int r = 0; r < n; r++) dot += x[i][r] * kx[j][r];
                    projected[i][j] = dot;
                }
            }
            for (int i = 0; i < block; i++)
                for (int j = i + 1; j < block; j++)
                    projected[i][j] = projected[j][i] = 0.5 * (projected[i][j] + projected[j][i]);
            var (ritz, vectors) = JacobiEigenSolver.Solve(projected);
            var rotated = new double[block][];
            for (int m = 0; m < block; m++)
            {
                rotated[m] = new double[n];
                for (int j = 0; j < block; j++)
                {
                    double c = vectors[m][j];
                    if (c == 0) continue;
                    for (int i = 0; i < n; i++) rotated[m][i] += c * x[j][i];
                }
            }
            x = rotated;
            values = ritz;
            if (previous is not null)
            {
                double change = 0;
                for (int m = 0; m < wanted; m++)
                    change = Math.Max(change, Math.Abs(values[m] - previous[m]) / (Math.Abs(values[m]) + shift));
                if (change < 1e-9) break;
            }
            previous = values;
        }

        var modes = new List<PlaneMode>();
        for (int m = 0; m < block && modes.Count < count; m++)
        {
            double omega2 = values[m];
            if (omega2 < 0.01 * shift) continue;                    // a static mode
            var shape = x[m];
            double peak = shape.Max(Math.Abs);                      // the largest cavity voltage
            var coupling = new double[_externalPorts];
            for (int p = 0; p < _externalPorts; p++)
            {
                var (site, upper, lower) = _ports[p];
                double at = 0;
                foreach (int i in _siteNodes[site])
                {
                    var (from, to, sign) = Between(i, upper, lower);
                    double voltage = 0;
                    for (int d = from; d < to; d++) voltage += shape[d];
                    at += _site[site][i] * sign * voltage;
                }
                coupling[p] = peak > 0 ? Math.Abs(at) / peak : 0;
            }
            modes.Add(new PlaneMode(Math.Sqrt(omega2) / (2 * Math.PI), coupling));
        }
        return modes;
    }

    private Point2 SiteCentre(int site)
    {
        double x = 0, y = 0, w = 0;
        foreach (int i in _siteNodes[site])
            if (i < Mesh.Points.Count) { x += Mesh.Points[i].X; y += Mesh.Points[i].Y; w++; }
        return new Point2(x / w, y / w);
    }
}
