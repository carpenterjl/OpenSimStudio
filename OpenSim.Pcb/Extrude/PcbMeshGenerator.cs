using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Meshing2D;

namespace OpenSim.Pcb.Extrude;

/// <summary>
/// Builds a tetrahedral <see cref="FeMesh"/> by extruding a planar triangulation
/// through a board stackup. Because every layer shares the same 2D triangulation,
/// copper/dielectric interfaces are conformal by construction (shared nodes → exact
/// current and heat continuity), and per-element region ids fall out of the 2D
/// classification crossed with the layer's material.
/// </summary>
public sealed class PcbMeshGenerator
{
    /// <summary>Side-wall grouping bin width for face ids [rad]. 16 bins of 22.5°.</summary>
    private const double SideBinWidth = Math.PI / 8;
    private const int SideBinCount = 16;

    /// <summary>
    /// Base face id for pads: a top face over pad <c>k</c> gets id <c>PadFaceBase + k</c>,
    /// so the app can map a clicked face back to a specific pad electrode. Chosen well above
    /// the top(0)/bottom(1)/side(2..17) ids.
    /// </summary>
    public const int PadFaceBase = 1000;

    /// <summary>How close a boundary face's z must be to a pad's exposed z to be tagged [m].
    /// Well below one copper thickness so a pad on one layer never claims another's face.</summary>
    private const double PadZTolerance = 1e-7;

    /// <summary>
    /// One extruded slab: its z extent, the planar triangle indices to extrude through it,
    /// and the 3D region id assigned to the resulting tets. Generalizes a stackup layer so a
    /// caller can extrude an arbitrary per-slab triangle subset — e.g. only the copper on
    /// layer L, or only a via barrel's disc across a dielectric gap.
    /// </summary>
    public readonly record struct ExtrudeSlab(double Z0, double Z1, IReadOnlyCollection<int> TriangleIndices, int Region);

    /// <summary>An exposed pad footprint used for electrode tagging: its 2D shape, the z of
    /// its exposed surface, and whether that surface faces up (+z) or down (−z).</summary>
    public readonly record struct PadFace(Polygon2 Shape, double Z, bool TopFacing);

    /// <summary>
    /// Copper-only mesh (single region): extrudes just the copper triangles one layer
    /// thick. This is all the electrical solve needs and sidesteps the 21-decade
    /// copper/FR4 conductivity spread entirely. When <paramref name="pads"/> is given, each
    /// pad's top face is tagged <c>PadFaceBase + index</c> so it can be picked as an electrode.
    /// </summary>
    public FeMesh GenerateCopperOnly(PlanarMesh planar, double copperThickness = PcbStackup.DefaultCopperThickness,
        IReadOnlyList<Polygon2>? pads = null)
    {
        var slabs = new[]
        {
            new ExtrudeSlab(0, copperThickness, TriangleIndices(planar, PcbStackup.CopperRegion), PcbStackup.CopperRegion)
        };
        var padFaces = pads?.Select(p => new PadFace(p, copperThickness, true)).ToList();
        return Build(planar, slabs, padFaces);
    }

    /// <summary>
    /// Full multi-region mesh: the dielectric layer spans the whole board outline, the
    /// copper layer only the copper footprint on top of it. Used for coupled thermal.
    /// </summary>
    public FeMesh Generate(PlanarMesh planar, PcbStackup stackup)
    {
        double z = 0;
        var slabs = new List<ExtrudeSlab>();
        foreach (var layer in stackup.Layers)
        {
            // A dielectric (board) layer underlies everything; a copper layer covers
            // only the copper footprint.
            var wanted = layer.RegionId == PcbStackup.CopperRegion
                ? new HashSet<int> { PcbStackup.CopperRegion }
                : new HashSet<int> { PcbStackup.CopperRegion, PcbStackup.DielectricRegion };
            var indices = Enumerable.Range(0, planar.Triangles.Count)
                .Where(i => wanted.Contains(planar.Triangles[i].RegionId)).ToList();
            slabs.Add(new ExtrudeSlab(z, z + layer.Thickness, indices, layer.RegionId));
            z += layer.Thickness;
        }
        return Build(planar, slabs, null);
    }

    /// <summary>
    /// General layered extrusion: each <paramref name="slabs"/> entry extrudes its own
    /// triangle subset through its own z extent. Because all slabs share one <see cref="Node"/>
    /// map, copper on adjacent layers and the via barrels between them share nodes at the
    /// z-boundaries — the multi-layer conformity guarantee. Used to mesh a via-stitched net
    /// with every trace at its true z, connected by solid copper barrels.
    /// </summary>
    public FeMesh GenerateLayered(PlanarMesh planar, IReadOnlyList<ExtrudeSlab> slabs,
        IReadOnlyList<PadFace>? pads = null) => Build(planar, slabs, pads);

    private static List<int> TriangleIndices(PlanarMesh planar, int regionId) =>
        Enumerable.Range(0, planar.Triangles.Count).Where(i => planar.Triangles[i].RegionId == regionId).ToList();

    private FeMesh Build(PlanarMesh planar, IReadOnlyList<ExtrudeSlab> slabs, IReadOnlyList<PadFace>? pads)
    {
        var nodes = new List<Vector3D>();
        var nodePlanarVertex = new List<int>();
        var nodeAt = new Dictionary<(int Vertex, double Z), int>();

        int Node(int planarVertex, double zLevel)
        {
            var key = (planarVertex, zLevel);
            if (nodeAt.TryGetValue(key, out int id)) return id;
            id = nodes.Count;
            nodeAt[key] = id;
            var p = planar.Points[planarVertex];
            nodes.Add(new Vector3D(p.X, p.Y, zLevel));
            nodePlanarVertex.Add(planarVertex);
            return id;
        }

        var elements = new List<Tet4>();
        var regionIds = new List<int>();

        foreach (var slab in slabs)
            foreach (int ti in slab.TriangleIndices)
                EmitPrism(planar.Triangles[ti], slab.Z0, slab.Z1, slab.Region, Node, elements, regionIds, nodes);

        if (elements.Count == 0)
            throw new InvalidOperationException("The stackup produced no elements for the given planar mesh.");

        var boundary = ExtractBoundary(nodes, elements, nodePlanarVertex, pads);
        return new FeMesh(nodes, elements, boundary, regionIds);
    }

    /// <summary>
    /// Splits the triangular prism into three tets with diagonals chosen by sorted base
    /// vertex index, so neighbouring prisms share compatible quad-face diagonals
    /// (the conformity guarantee). Each tet is oriented to positive volume.
    /// </summary>
    private static void EmitPrism(in Tri2 t, double z0, double z1, int region,
        Func<int, double, int> node, List<Tet4> elements, List<int> regionIds, List<Vector3D> nodes)
    {
        // Sort the three planar vertices ascending for a consistent diagonal choice.
        Span<int> v = stackalloc int[] { t.A, t.B, t.C };
        if (v[0] > v[1]) (v[0], v[1]) = (v[1], v[0]);
        if (v[1] > v[2]) (v[1], v[2]) = (v[2], v[1]);
        if (v[0] > v[1]) (v[0], v[1]) = (v[1], v[0]);

        int b0 = node(v[0], z0), b1 = node(v[1], z0), b2 = node(v[2], z0);
        int t0 = node(v[0], z1), t1 = node(v[1], z1), t2 = node(v[2], z1);

        AddTet(b0, b1, b2, t2, elements, regionIds, region, nodes);
        AddTet(b0, b1, t2, t1, elements, regionIds, region, nodes);
        AddTet(b0, t1, t2, t0, elements, regionIds, region, nodes);
    }

    private static void AddTet(int n0, int n1, int n2, int n3,
        List<Tet4> elements, List<int> regionIds, int region, List<Vector3D> nodes)
    {
        // Ensure positive volume so element weights and assembled coefficients stay positive.
        double vol = Vector3D.Dot(nodes[n1] - nodes[n0],
            Vector3D.Cross(nodes[n2] - nodes[n0], nodes[n3] - nodes[n0]));
        if (vol < 0) (n2, n3) = (n3, n2);
        elements.Add(new Tet4(n0, n1, n2, n3));
        regionIds.Add(region);
    }

    // ---------------- Boundary extraction and face tagging ----------------

    private static IReadOnlyList<BoundaryTriangle> ExtractBoundary(
        List<Vector3D> nodes, List<Tet4> elements, List<int> nodePlanarVertex, IReadOnlyList<PadFace>? pads)
    {
        // Faces used by exactly one tet are on the boundary; remember the opposite
        // (4th) vertex so the outward normal can be oriented.
        var faceUse = new Dictionary<(int, int, int), (int Count, int A, int B, int C, int Opp)>();
        void Face(int a, int b, int c, int opp)
        {
            var key = SortedKey(a, b, c);
            if (faceUse.TryGetValue(key, out var v))
                faceUse[key] = (v.Count + 1, v.A, v.B, v.C, v.Opp);
            else
                faceUse[key] = (1, a, b, c, opp);
        }
        foreach (var e in elements)
        {
            Face(e.N1, e.N2, e.N3, e.N0);
            Face(e.N0, e.N2, e.N3, e.N1);
            Face(e.N0, e.N1, e.N3, e.N2);
            Face(e.N0, e.N1, e.N2, e.N3);
        }

        var boundary = new List<BoundaryTriangle>();
        foreach (var (_, f) in faceUse)
        {
            if (f.Count != 1) continue;
            var (a, b, c) = OrientOutward(nodes, f.A, f.B, f.C, f.Opp);
            int faceId = ClassifyFace(nodes, a, b, c, pads);
            boundary.Add(new BoundaryTriangle(a, b, c, faceId));
        }
        return boundary;
    }

    private static (int, int, int) OrientOutward(List<Vector3D> nodes, int a, int b, int c, int opp)
    {
        var normal = Vector3D.Cross(nodes[b] - nodes[a], nodes[c] - nodes[a]);
        // Outward points away from the opposite vertex.
        if (Vector3D.Dot(normal, nodes[opp] - nodes[a]) > 0)
            (b, c) = (c, b);
        return (a, b, c);
    }

    /// <summary>
    /// Face ids: a pad face <c>k</c> = <c>PadFaceBase + k</c> (electrode); otherwise
    /// 0 = top (+z), 1 = bottom (−z), 2+bin = side wall grouped by outward normal direction
    /// (so the ends of a straight trace get distinct, selectable ids). A pad is tagged only
    /// on the exposed side and z it lives on, so on a multi-layer net a top-layer pad and a
    /// bottom-layer pad don't claim each other's faces.
    /// </summary>
    private static int ClassifyFace(List<Vector3D> nodes, int a, int b, int c, IReadOnlyList<PadFace>? pads)
    {
        var normal = Vector3D.Cross(nodes[b] - nodes[a], nodes[c] - nodes[a]).Normalized();
        bool up = normal.Z > 0.7, down = normal.Z < -0.7;
        if (up || down)
        {
            if (pads is not null)
            {
                // The face's XYZ centroid is its location; tag it to a covering pad on the
                // matching exposed side (up/down) at the matching z.
                var mid = (nodes[a] + nodes[b] + nodes[c]) * (1.0 / 3.0);
                var p = new Point2(mid.X, mid.Y);
                for (int k = 0; k < pads.Count; k++)
                {
                    var pad = pads[k];
                    if (pad.TopFacing != up) continue;
                    if (Math.Abs(mid.Z - pad.Z) > PadZTolerance) continue;
                    if (PlanarMesher.ContainsPoint(new[] { pad.Shape }, p))
                        return PadFaceBase + k;
                }
            }
            return up ? 0 : 1;
        }

        double angle = Math.Atan2(normal.Y, normal.X);
        int bin = (int)Math.Floor((angle + Math.PI) / SideBinWidth) % SideBinCount;
        if (bin < 0) bin += SideBinCount;
        return 2 + bin;
    }

    private static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
