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

    /// <summary>Whether this mesh carries quadratic (TET10) elements.</summary>
    [JsonIgnore]
    public bool IsQuadratic => MidEdgeNodes is not null;

    public FeMesh(IReadOnlyList<Vector3D> nodes, IReadOnlyList<Tet4> elements,
        IReadOnlyList<BoundaryTriangle> boundaryTriangles, IReadOnlyList<int>? elementRegionIds = null,
        IReadOnlyList<Tet10Mid>? midEdgeNodes = null)
    {
        if (elementRegionIds is not null && elementRegionIds.Count != elements.Count)
            throw new ArgumentException(
                $"elementRegionIds has {elementRegionIds.Count} entries but the mesh has {elements.Count} elements.");
        if (midEdgeNodes is not null && midEdgeNodes.Count != elements.Count)
            throw new ArgumentException(
                $"midEdgeNodes has {midEdgeNodes.Count} entries but the mesh has {elements.Count} elements.");
        Nodes = nodes;
        Elements = elements;
        BoundaryTriangles = boundaryTriangles;
        ElementRegionIds = elementRegionIds;
        MidEdgeNodes = midEdgeNodes;
    }

    /// <summary>Region id of one element; 0 when the mesh carries no region information.</summary>
    public int RegionOf(int elementIndex) => ElementRegionIds?[elementIndex] ?? 0;

    public int NodeCount => Nodes.Count;
    public int ElementCount => Elements.Count;

    /// <summary>Volume of one element. Positive when the node ordering follows the right-hand convention.</summary>
    public double ElementVolume(int elementIndex)
    {
        var e = Elements[elementIndex];
        var p0 = Nodes[e.N0];
        return Vector3D.Dot(Nodes[e.N1] - p0, Vector3D.Cross(Nodes[e.N2] - p0, Nodes[e.N3] - p0)) / 6.0;
    }

    /// <summary>Total mesh volume.</summary>
    public double TotalVolume()
    {
        double v = 0;
        for (int i = 0; i < Elements.Count; i++) v += ElementVolume(i);
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
}
