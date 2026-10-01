using System.Text.Json.Serialization;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>A 4-node tetrahedral element with node indices into the owning mesh.</summary>
public readonly record struct Tet4(int N0, int N1, int N2, int N3);

/// <summary>
/// The six mid-edge node indices completing one TET10 element; Mxy is the midpoint of
/// corner edge (Nx, Ny). Kept separate from <see cref="Tet4"/> so linear meshes, old
/// project files, and every corner-index consumer stay untouched.
/// </summary>
public readonly record struct Tet10Mid(int M01, int M02, int M03, int M12, int M13, int M23);

/// <summary>A boundary surface triangle of the FE mesh, tagged with the geometric face it lies on.</summary>
public readonly record struct BoundaryTriangle(int A, int B, int C, int FaceId);

/// <summary>
/// The eight corners of one hexahedral element, in the standard SOLID186/C3D20 order:
/// N0..N3 walk the base face counter-clockwise, N4..N7 the top face directly above them.
/// </summary>
public readonly record struct Hex8(int N0, int N1, int N2, int N3, int N4, int N5, int N6, int N7);

/// <summary>
/// The twelve mid-edge node indices completing one HEX20 element: the four base-face edges,
/// the four top-face edges, then the four verticals. Kept separate from <see cref="Hex8"/>
/// for the same reason <see cref="Tet10Mid"/> is separate from <see cref="Tet4"/> - corner
/// indices and everything derived from them stay untouched.
/// </summary>
public readonly record struct Hex20Mid(
    int M01, int M12, int M23, int M30,
    int M45, int M56, int M67, int M74,
    int M04, int M15, int M26, int M37);

/// <summary>
/// A boundary surface QUAD of a hexahedral mesh, wound outward and tagged with its geometric
/// face id.
/// <para>
/// Carried alongside the triangular skin rather than instead of it. The skin stays triangular
/// so rendering, contact, section cutting and the whole feature-edge machinery are unaffected;
/// but a triangulated quad face has a DIAGONAL that is not an element edge, and consistent
/// surface loads on a quadratic element are a property of the real face - spreading them over
/// two triangles as if they were T6 triangles gives the wrong nodal loads (the QUAD8 result
/// even puts NEGATIVE load on the corner nodes). Loads and supports therefore read the quads.
/// </para>
/// </summary>
public readonly record struct BoundaryQuad(int A, int B, int C, int D, int FaceId);

/// <summary>
/// The finite element mesh every solver consumes: nodes, tetrahedral elements, and the
/// boundary skin tagged with geometric face ids so boundary conditions can be resolved
/// to nodes and surface triangles.
/// </summary>
public sealed class FeMesh
{
    public IReadOnlyList<Vector3D> Nodes { get; }
    public IReadOnlyList<Tet4> Elements { get; }
    public IReadOnlyList<BoundaryTriangle> BoundaryTriangles { get; }

    /// <summary>
    /// Optional per-element region ids for multi-material meshes (e.g. PCB copper vs.
    /// dielectric). Null means the whole mesh is region 0, so single-material meshes
    /// and old project files are unaffected.
    /// </summary>
    public IReadOnlyList<int>? ElementRegionIds { get; }

    /// <summary>
    /// Optional quadratic (TET10) layer: per-element mid-edge node indices, appended to
    /// <see cref="Nodes"/> after all corner nodes so every corner index — and with it
    /// the boundary triangles, rendering, and load distribution — stays valid. Null
    /// means a plain linear (TET4) mesh; old project files deserialize to null.
    /// </summary>
    public IReadOnlyList<Tet10Mid>? MidEdgeNodes { get; }

    /// <summary>
    /// Optional hexahedral element layer. A mesh is either tetrahedral or hexahedral, never
    /// both: <see cref="Elements"/> is empty when this is present. Null on every tetrahedral
    /// mesh and on every project file written before hexes existed.
    /// </summary>
    public IReadOnlyList<Hex8>? HexElements { get; }

    /// <summary>
    /// Per-hex mid-edge node indices, appended to <see cref="Nodes"/> after all corner nodes
    /// exactly as <see cref="MidEdgeNodes"/> is. Hexahedral meshes are HEX20 only - a linear
    /// hex shear-locks in bending without the incompatible-mode machinery this code does not
    /// carry, so it is never produced and this is never null beside <see cref="HexElements"/>.
    /// </summary>
    public IReadOnlyList<Hex20Mid>? HexMidEdgeNodes { get; }

    /// <summary>
    /// The boundary skin as QUADS, present exactly when the mesh is hexahedral. See
    /// <see cref="BoundaryQuad"/> for why both this and the triangular skin exist.
    /// </summary>
    public IReadOnlyList<BoundaryQuad>? BoundaryQuads { get; }

    /// <summary>Whether this mesh carries hexahedral (HEX20) elements.</summary>
    [JsonIgnore]
    public bool IsHex => HexElements is not null;

    /// <summary>
    /// Whether this mesh carries quadratic elements - TET10 or HEX20. Every solver that
    /// handles only linear tetrahedra tests this, so a hexahedral mesh is refused by the
    /// same guards, by name, before it can reach a loop that assumes four-node elements.
    /// </summary>
    [JsonIgnore]
    public bool IsQuadratic => MidEdgeNodes is not null || HexMidEdgeNodes is not null;

    public FeMesh(IReadOnlyList<Vector3D> nodes, IReadOnlyList<Tet4> elements,
        IReadOnlyList<BoundaryTriangle> boundaryTriangles, IReadOnlyList<int>? elementRegionIds = null,
        IReadOnlyList<Tet10Mid>? midEdgeNodes = null, IReadOnlyList<Hex8>? hexElements = null,
        IReadOnlyList<Hex20Mid>? hexMidEdgeNodes = null, IReadOnlyList<BoundaryQuad>? boundaryQuads = null)
    {
        if (midEdgeNodes is not null && midEdgeNodes.Count != elements.Count)
            throw new ArgumentException(
                $"midEdgeNodes has {midEdgeNodes.Count} entries but the mesh has {elements.Count} elements.");

        if (hexElements is not null)
        {
            // A mesh is one element family or the other. Allowing both would put two element
            // kinds in one index space, and every consumer that walks ElementCount indexing
            // Elements[e] would read the wrong list for half the mesh.
            if (elements.Count != 0)
                throw new ArgumentException(
                    $"A hexahedral mesh carries no tetrahedra, but {elements.Count} were supplied. " +
                    "Mixed element families are not supported.");
            if (hexMidEdgeNodes is null)
                throw new ArgumentException(
                    "Hexahedral meshes are HEX20: hexMidEdgeNodes is required alongside hexElements.");
            if (hexMidEdgeNodes.Count != hexElements.Count)
                throw new ArgumentException(
                    $"hexMidEdgeNodes has {hexMidEdgeNodes.Count} entries but the mesh has " +
                    $"{hexElements.Count} hexahedra.");
            if (boundaryQuads is null)
                throw new ArgumentException(
                    "A hexahedral mesh must carry its quad skin: consistent surface loads and " +
                    "support pinning are defined on the quad faces, not on the triangulated skin.");
        }
        else
        {
            if (hexMidEdgeNodes is not null)
                throw new ArgumentException("hexMidEdgeNodes was supplied without hexElements.");
            if (boundaryQuads is not null)
                throw new ArgumentException("boundaryQuads was supplied without hexElements.");
        }

        int elementTotal = hexElements?.Count ?? elements.Count;
        if (elementRegionIds is not null && elementRegionIds.Count != elementTotal)
            throw new ArgumentException(
                $"elementRegionIds has {elementRegionIds.Count} entries but the mesh has {elementTotal} elements.");

        Nodes = nodes;
        Elements = elements;
        BoundaryTriangles = boundaryTriangles;
        ElementRegionIds = elementRegionIds;
        MidEdgeNodes = midEdgeNodes;
        HexElements = hexElements;
        HexMidEdgeNodes = hexMidEdgeNodes;
        BoundaryQuads = boundaryQuads;
        _edges = new Lazy<BoundaryEdgeSet>(() => BoundaryEdgeSet.Extract(this));
    }

    private readonly Lazy<BoundaryEdgeSet> _edges;

    /// <summary>
    /// What the post-mesh audit found when this mesh was generated: a record for the
    /// meshing view to show, nothing a solver reads. Null on a mesh that did not come out of
    /// an audited mesher - one loaded from a project file, built by the PCB mesher, or
    /// assembled by hand - and never persisted, since a stored verdict could only go stale.
    /// </summary>
    [JsonIgnore]
    public MeshAuditReport? Audit { get; private init; }

    /// <summary>The same mesh, carrying its audit report.</summary>
    public FeMesh WithAudit(MeshAuditReport report) =>
        new(Nodes, Elements, BoundaryTriangles, ElementRegionIds, MidEdgeNodes, HexElements,
            HexMidEdgeNodes, BoundaryQuads) { Audit = report };

    /// <summary>
    /// The geometric edges and vertices of the boundary skin, derived on first use and
    /// cached. Derived rather than stored: the ids are a function of the skin and its face
    /// tags, so a second copy could only ever disagree with them.
    /// </summary>
    [JsonIgnore]
    public BoundaryEdgeSet Edges => _edges.Value;

    /// <summary>Region id of one element; 0 when the mesh carries no region information.</summary>
    public int RegionOf(int elementIndex) => ElementRegionIds?[elementIndex] ?? 0;

    public int NodeCount => Nodes.Count;
    public int ElementCount => HexElements?.Count ?? Elements.Count;

    /// <summary>Volume of one element. Positive when the node ordering follows the right-hand convention.</summary>
    public double ElementVolume(int elementIndex)
    {
        if (HexElements is not null) return HexVolume(elementIndex);
        var e = Elements[elementIndex];
        var p0 = Nodes[e.N0];
        return Vector3D.Dot(Nodes[e.N1] - p0, Vector3D.Cross(Nodes[e.N2] - p0, Nodes[e.N3] - p0)) / 6.0;
    }

    /// <summary>
    /// Volume of one hexahedron: the integral of |J| over the reference cube of the TRILINEAR
    /// corner map. That determinant is degree 2 at most in each reference coordinate, so the
    /// 2x2x2 Gauss rule (degree-3 exact per direction) integrates it EXACTLY - a closed form,
    /// not an approximation, reducing to dx*dy*dz on a lattice cell.
    /// <para>
    /// The mid-edge nodes deliberately take no part: they are the straight-edge midpoints, so
    /// the element occupies exactly the trilinear hull of its corners.
    /// </para>
    /// </summary>
    private double HexVolume(int elementIndex)
    {
        var h = HexElements![elementIndex];
        Span<Vector3D> c = stackalloc Vector3D[8];
        c[0] = Nodes[h.N0]; c[1] = Nodes[h.N1]; c[2] = Nodes[h.N2]; c[3] = Nodes[h.N3];
        c[4] = Nodes[h.N4]; c[5] = Nodes[h.N5]; c[6] = Nodes[h.N6]; c[7] = Nodes[h.N7];

        // Reference corner signs, in the same order as the node numbering above.
        ReadOnlySpan<int> sx = stackalloc int[] { -1, 1, 1, -1, -1, 1, 1, -1 };
        ReadOnlySpan<int> sy = stackalloc int[] { -1, -1, 1, 1, -1, -1, 1, 1 };
        ReadOnlySpan<int> sz = stackalloc int[] { -1, -1, -1, -1, 1, 1, 1, 1 };

        const double g = 0.5773502691896257;   // the 2-point Gauss node, 1/sqrt(3)
        double volume = 0;
        for (int gz = 0; gz < 2; gz++)
            for (int gy = 0; gy < 2; gy++)
                for (int gx = 0; gx < 2; gx++)
                {
                    double xi = gx == 0 ? -g : g;
                    double eta = gy == 0 ? -g : g;
                    double zeta = gz == 0 ? -g : g;

                    Vector3D dXi = default, dEta = default, dZeta = default;
                    for (int i = 0; i < 8; i++)
                    {
                        double a = 1 + sx[i] * xi, b = 1 + sy[i] * eta, d = 1 + sz[i] * zeta;
                        dXi += c[i] * (0.125 * sx[i] * b * d);
                        dEta += c[i] * (0.125 * sy[i] * a * d);
                        dZeta += c[i] * (0.125 * sz[i] * a * b);
                    }
                    volume += Vector3D.Dot(dXi, Vector3D.Cross(dEta, dZeta));   // unit weights
                }
        return volume;
    }

    /// <summary>Total mesh volume.</summary>
    public double TotalVolume()
    {
        double v = 0;
        for (int i = 0; i < ElementCount; i++) v += ElementVolume(i);
        return v;
    }

    /// <summary>
    /// All node indices of one element: the 4 corners, followed by the 6 mid-edge nodes
    /// when the mesh is quadratic. The single accessor every nodal-averaging loop must
    /// use — averaging over corners only leaves quadratic mid-nodes at zero and
    /// corrupts field ranges.
    /// </summary>
    public int[] GetElementNodes(int elementIndex)
    {
        if (HexElements is not null)
        {
            var h = HexElements[elementIndex];
            var hm = HexMidEdgeNodes![elementIndex];
            return new[]
            {
                h.N0, h.N1, h.N2, h.N3, h.N4, h.N5, h.N6, h.N7,
                hm.M01, hm.M12, hm.M23, hm.M30,
                hm.M45, hm.M56, hm.M67, hm.M74,
                hm.M04, hm.M15, hm.M26, hm.M37
            };
        }
        var e = Elements[elementIndex];
        if (MidEdgeNodes is null)
            return new[] { e.N0, e.N1, e.N2, e.N3 };
        var m = MidEdgeNodes[elementIndex];
        return new[] { e.N0, e.N1, e.N2, e.N3, m.M01, m.M02, m.M03, m.M12, m.M13, m.M23 };
    }

    /// <summary>All distinct node indices lying on the given geometric faces.</summary>
    public IReadOnlySet<int> GetFaceNodes(IEnumerable<int> faceIds)
    {
        var faces = faceIds as ISet<int> ?? new HashSet<int>(faceIds);
        var nodes = new HashSet<int>();
        foreach (var bt in BoundaryTriangles)
        {
            if (!faces.Contains(bt.FaceId)) continue;
            nodes.Add(bt.A);
            nodes.Add(bt.B);
            nodes.Add(bt.C);
        }
        return nodes;
    }

    /// <summary>All boundary triangles lying on the given geometric faces.</summary>
    public IReadOnlyList<BoundaryTriangle> GetFaceTriangles(IEnumerable<int> faceIds)
    {
        var faces = faceIds as ISet<int> ?? new HashSet<int>(faceIds);
        return BoundaryTriangles.Where(bt => faces.Contains(bt.FaceId)).ToList();
    }

    /// <summary>
    /// All boundary QUADS lying on the given geometric faces; empty on a tetrahedral mesh.
    /// The surface a load or support acts on, for meshes that have one - see
    /// <see cref="BoundaryQuad"/>.
    /// </summary>
    public IReadOnlyList<BoundaryQuad> GetFaceQuads(IEnumerable<int> faceIds)
    {
        if (BoundaryQuads is null) return Array.Empty<BoundaryQuad>();
        var faces = faceIds as ISet<int> ?? new HashSet<int>(faceIds);
        return BoundaryQuads.Where(q => faces.Contains(q.FaceId)).ToList();
    }

    /// <summary>All distinct node indices lying on the given geometric edges.</summary>
    public IReadOnlySet<int> GetEdgeNodes(IEnumerable<int> edgeIds)
    {
        var nodes = new HashSet<int>();
        foreach (int id in edgeIds)
            if (Edges.EdgeById(id) is { } edge)
                foreach (int n in edge.NodeIds)
                    nodes.Add(n);
        return nodes;
    }

    /// <summary>The node indices of the given geometric vertices.</summary>
    public IReadOnlySet<int> GetVertexNodes(IEnumerable<int> vertexIds)
    {
        var nodes = new HashSet<int>();
        foreach (int id in vertexIds)
            if (Edges.VertexById(id) is { } vertex)
                nodes.Add(vertex.NodeId);
        return nodes;
    }

    /// <summary>
    /// Every CORNER node a boundary condition scope resolves to, across faces, edges and
    /// vertices. The single seam every solver goes through, so a new scope kind is picked
    /// up everywhere at once.
    /// </summary>
    public IReadOnlySet<int> GetScopeNodes(BoundaryCondition condition)
    {
        var nodes = new HashSet<int>(GetFaceNodes(condition.FaceIds));
        if (condition.EdgeIds is { Count: > 0 } edgeIds) nodes.UnionWith(GetEdgeNodes(edgeIds));
        if (condition.VertexIds is { Count: > 0 } vertexIds) nodes.UnionWith(GetVertexNodes(vertexIds));
        return nodes;
    }

    /// <summary>
    /// The mesh edges a boundary condition scope covers: the three edges of every face
    /// triangle, plus the segments of every named geometric edge.
    /// <para>
    /// A quadratic (TET10) solve must also constrain the MID-EDGE node of each of these.
    /// Pinning only corners leaves the mid-nodes free, which is spurious compliance at a
    /// support and a leak at a Dirichlet boundary. A vertex contributes no segment — it is
    /// a single node and has no mid-node to speak of.
    /// </para>
    /// </summary>
    public IReadOnlyList<BoundaryEdgeSegment> GetScopeSegments(BoundaryCondition condition)
    {
        var segments = new List<BoundaryEdgeSegment>();
        foreach (var t in GetFaceTriangles(condition.FaceIds))
        {
            segments.Add(BoundaryEdgeSegment.Sorted(t.A, t.B));
            segments.Add(BoundaryEdgeSegment.Sorted(t.B, t.C));
            segments.Add(BoundaryEdgeSegment.Sorted(t.C, t.A));
        }
        if (condition.EdgeIds is { Count: > 0 } edgeIds)
            foreach (int id in edgeIds)
                if (Edges.EdgeById(id) is { } edge)
                    segments.AddRange(edge.Segments);
        return segments;
    }
}
