using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// A solid made of the occupied cells of a rectilinear grid, as an exact surface AND as an
/// exact conforming tetrahedral mesh of the same cells.
/// <para>
/// The mesh audit has to be shown meshes that are wrong in a known way — a bore filled, a
/// tab hollowed, two bodies welded — and the meshers cannot be asked to produce those on
/// demand. Here the "mesh" of one occupancy can be audited against the "geometry" of
/// another, so every fixture states exactly what was done to it.
/// </para>
/// </summary>
internal sealed class GridSolid
{
    private readonly double[] _x, _y, _z;
    private readonly bool[,,] _solid;

    /// <summary>The six tetrahedra of a cell (corner bit 0 = x, 1 = y, 2 = z): the
    /// Freudenthal subdivision, which is conforming across cells of any size.</summary>
    private static readonly int[][] CellTets =
    {
        new[] { 0, 1, 3, 7 }, new[] { 0, 1, 7, 5 }, new[] { 0, 5, 7, 4 },
        new[] { 0, 3, 2, 7 }, new[] { 0, 6, 4, 7 }, new[] { 0, 2, 6, 7 }
    };

    /// <param name="x">Grid planes along x; any order, duplicates ignored.</param>
    /// <param name="inside">Occupancy, asked at each cell centre.</param>
    /// <param name="maxCell">Cells are subdivided until none is longer than this.</param>
    public GridSolid(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> z,
        Func<Vector3D, bool> inside, double maxCell = double.PositiveInfinity)
    {
        _x = Planes(x, maxCell); _y = Planes(y, maxCell); _z = Planes(z, maxCell);
        _solid = new bool[_x.Length - 1, _y.Length - 1, _z.Length - 1];
        for (int i = 0; i < _x.Length - 1; i++)
            for (int j = 0; j < _y.Length - 1; j++)
                for (int k = 0; k < _z.Length - 1; k++)
                    _solid[i, j, k] = inside(new Vector3D(
                        (_x[i] + _x[i + 1]) / 2, (_y[j] + _y[j + 1]) / 2, (_z[k] + _z[k + 1]) / 2));
    }

    private static double[] Planes(IEnumerable<double> breaks, double maxCell)
    {
        var sorted = breaks.Distinct().OrderBy(v => v).ToList();
        var planes = new List<double> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            double a = sorted[i - 1], b = sorted[i];
            int pieces = double.IsFinite(maxCell) ? Math.Max(1, (int)Math.Ceiling((b - a) / maxCell - 1e-9)) : 1;
            for (int p = 1; p < pieces; p++) planes.Add(a + (b - a) * p / pieces);
            planes.Add(b);
        }
        return planes.ToArray();
    }

    private bool Solid(int i, int j, int k) =>
        i >= 0 && j >= 0 && k >= 0 && i < _x.Length - 1 && j < _y.Length - 1 && k < _z.Length - 1 && _solid[i, j, k];

    private int NodeIndex(int i, int j, int k) => (k * _y.Length + j) * _x.Length + i;

    private Vector3D Node(int index)
    {
        int i = index % _x.Length, j = index / _x.Length % _y.Length, k = index / (_x.Length * _y.Length);
        return new Vector3D(_x[i], _y[j], _z[k]);
    }

    /// <summary>The exact boundary surface: one quad (two triangles) per exposed cell face,
    /// wound outward, face ids from crease detection.</summary>
    public TriangleMesh ToGeometry()
    {
        var map = new Dictionary<int, int>();
        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        int V(int i, int j, int k)
        {
            int id = NodeIndex(i, j, k);
            if (!map.TryGetValue(id, out int v)) { map[id] = v = vertices.Count; vertices.Add(Node(id)); }
            return v;
        }
        void Quad(int a, int b, int c, int d)
        {
            triangles.Add(new Triangle(a, b, c));
            triangles.Add(new Triangle(a, c, d));
        }

        for (int i = 0; i < _x.Length - 1; i++)
            for (int j = 0; j < _y.Length - 1; j++)
                for (int k = 0; k < _z.Length - 1; k++)
                {
                    if (!_solid[i, j, k]) continue;
                    if (!Solid(i - 1, j, k)) Quad(V(i, j, k), V(i, j, k + 1), V(i, j + 1, k + 1), V(i, j + 1, k));
                    if (!Solid(i + 1, j, k)) Quad(V(i + 1, j, k), V(i + 1, j + 1, k), V(i + 1, j + 1, k + 1), V(i + 1, j, k + 1));
                    if (!Solid(i, j - 1, k)) Quad(V(i, j, k), V(i + 1, j, k), V(i + 1, j, k + 1), V(i, j, k + 1));
                    if (!Solid(i, j + 1, k)) Quad(V(i, j + 1, k), V(i, j + 1, k + 1), V(i + 1, j + 1, k + 1), V(i + 1, j + 1, k));
                    if (!Solid(i, j, k - 1)) Quad(V(i, j, k), V(i, j + 1, k), V(i + 1, j + 1, k), V(i + 1, j, k));
                    if (!Solid(i, j, k + 1)) Quad(V(i, j, k + 1), V(i + 1, j, k + 1), V(i + 1, j + 1, k + 1), V(i, j + 1, k + 1));
                }
        return new TriangleMesh(vertices, triangles, FaceDetector.DetectFaces(vertices, triangles));
    }

    /// <summary>
    /// A conforming tetrahedral mesh of exactly the occupied cells, its skin wound outward.
    /// Skin face ids are those of <paramref name="tagFrom"/>'s nearest triangle when given
    /// (what a mesher does), otherwise 0.
    /// </summary>
    public FeMesh ToMesh(TriangleMesh? tagFrom = null)
    {
        var map = new Dictionary<int, int>();
        var nodes = new List<Vector3D>();
        int N(int id)
        {
            if (!map.TryGetValue(id, out int n)) { map[id] = n = nodes.Count; nodes.Add(Node(id)); }
            return n;
        }

        var elements = new List<Tet4>();
        var corner = new int[8];
        for (int i = 0; i < _x.Length - 1; i++)
            for (int j = 0; j < _y.Length - 1; j++)
                for (int k = 0; k < _z.Length - 1; k++)
                {
                    if (!_solid[i, j, k]) continue;
                    for (int c = 0; c < 8; c++)
                        corner[c] = N(NodeIndex(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1)));
                    foreach (var tet in CellTets)
                    {
                        int a = corner[tet[0]], b = corner[tet[1]], cc = corner[tet[2]], d = corner[tet[3]];
                        if (Vector3D.Dot(nodes[b] - nodes[a], Vector3D.Cross(nodes[cc] - nodes[a], nodes[d] - nodes[a])) < 0)
                            (cc, d) = (d, cc);
                        elements.Add(new Tet4(a, b, cc, d));
                    }
                }
        return new FeMesh(nodes, elements, Skin(nodes, elements, tagFrom));
    }

    /// <summary>Element faces used once, wound outward.</summary>
    internal static List<BoundaryTriangle> Skin(IReadOnlyList<Vector3D> nodes, IReadOnlyList<Tet4> elements,
        TriangleMesh? tagFrom = null)
    {
        var use = new Dictionary<(int, int, int), (int Count, int A, int B, int C, int Opposite)>();
        void Touch(int a, int b, int c, int opposite)
        {
            var key = Sorted(a, b, c);
            use[key] = use.TryGetValue(key, out var seen)
                ? (seen.Count + 1, seen.A, seen.B, seen.C, seen.Opposite)
                : (1, a, b, c, opposite);
        }
        foreach (var e in elements)
        {
            Touch(e.N1, e.N2, e.N3, e.N0); Touch(e.N0, e.N2, e.N3, e.N1);
            Touch(e.N0, e.N1, e.N3, e.N2); Touch(e.N0, e.N1, e.N2, e.N3);
        }
        var skin = new List<BoundaryTriangle>();
        foreach (var face in use.Values)
        {
            if (face.Count != 1) continue;
            int a = face.A, b = face.B, c = face.C;
            if (Vector3D.Dot(Vector3D.Cross(nodes[b] - nodes[a], nodes[c] - nodes[a]), nodes[face.Opposite] - nodes[a]) > 0)
                (b, c) = (c, b);
            int faceId = tagFrom is null ? 0 : NearestFaceId(tagFrom, (nodes[a] + nodes[b] + nodes[c]) / 3.0);
            skin.Add(new BoundaryTriangle(a, b, c, faceId));
        }
        return skin;
    }

    private static int NearestFaceId(TriangleMesh geometry, Vector3D p)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int t = 0; t < geometry.Triangles.Count; t++)
        {
            var tri = geometry.Triangles[t];
            double d = OpenSim.Meshing.SurfaceDistanceField.PointTriangleDistance(p,
                geometry.Vertices[tri.A], geometry.Vertices[tri.B], geometry.Vertices[tri.C]);
            if (d < bestDistance) (best, bestDistance) = (t, d);
        }
        return geometry.TriangleFaceIds[best];
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>True inside the axis-aligned box [x0, x1] × [y0, y1] × [z0, z1].</summary>
    public static bool In(Vector3D p, double x0, double x1, double y0, double y1, double z0, double z1) =>
        p.X > x0 && p.X < x1 && p.Y > y0 && p.Y < y1 && p.Z > z0 && p.Z < z1;
}

/// <summary>
/// Closed surfaces built from extruded polygons — the faceted cylinders, wedges and
/// chamfered plates the audit's ray comparison is pinned against. Bare surfaces: they stand
/// in for a geometry or for a mesh skin, whichever the fixture needs.
/// </summary>
internal sealed class SurfaceBuilder
{
    public List<Vector3D> Vertices { get; } = new();
    public List<(int A, int B, int C)> Triangles { get; } = new();

    /// <summary>
    /// The prism over a polygon (counter-clockwise seen from +z, star-shaped about
    /// <paramref name="centre"/>) from <paramref name="z0"/> to <paramref name="z1"/>. Caps
    /// are fans about the centre; each lateral face is cut into <paramref name="zSteps"/>
    /// bands.
    /// </summary>
    public SurfaceBuilder AddPrism(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) centre,
        double z0, double z1, int zSteps = 1)
    {
        int n = polygon.Count;
        int first = Vertices.Count;
        for (int s = 0; s <= zSteps; s++)
        {
            double z = z0 + (z1 - z0) * s / zSteps;
            foreach (var (x, y) in polygon) Vertices.Add(new Vector3D(x, y, z));
        }
        int bottomCentre = Vertices.Count; Vertices.Add(new Vector3D(centre.X, centre.Y, z0));
        int topCentre = Vertices.Count; Vertices.Add(new Vector3D(centre.X, centre.Y, z1));

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            Triangles.Add((bottomCentre, first + next, first + i));
            Triangles.Add((topCentre, first + zSteps * n + i, first + zSteps * n + next));
            for (int s = 0; s < zSteps; s++)
            {
                int a = first + s * n + i, b = first + s * n + next;
                int c = first + (s + 1) * n + next, d = first + (s + 1) * n + i;
                Triangles.Add((a, b, c));
                Triangles.Add((a, c, d));
            }
        }
        return this;
    }

    public SurfaceBuilder AddBox(double x0, double x1, double y0, double y1, double z0, double z1) =>
        AddPrism(new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }, ((x0 + x1) / 2, (y0 + y1) / 2), z0, z1);

    /// <summary>A regular polygon inscribed in the circle of the given radius.</summary>
    public static List<(double X, double Y)> Polygon(int sides, double radius, double centreX, double centreY,
        double rotation = 0)
    {
        var points = new List<(double, double)>(sides);
        for (int i = 0; i < sides; i++)
        {
            double angle = rotation + 2 * Math.PI * i / sides;
            points.Add((centreX + radius * Math.Cos(angle), centreY + radius * Math.Sin(angle)));
        }
        return points;
    }

    public TriangleMesh ToGeometry()
    {
        var triangles = Triangles.Select(t => new Triangle(t.A, t.B, t.C)).ToList();
        return new TriangleMesh(Vertices, triangles, FaceDetector.DetectFaces(Vertices, triangles));
    }
}
