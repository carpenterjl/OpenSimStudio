using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Hands element values from one tetrahedral mesh to another that fills the same space, and
/// nodal values back: the heat of an electrical mesh (explicit copper) into a thermal mesh
/// (the whole board), and the thermal mesh's temperature back to the electrical elements.
/// Each source element is placed by its centroid in the target element that contains it;
/// its power goes into that element whole, so the total heat is kept exactly, and the
/// temperature it is given is the target's linear field at the centroid.
/// </summary>
public sealed class MeshTransfer
{
    private readonly FeMesh _source, _target;
    private readonly int[] _host;
    private readonly double[][] _weights;

    private MeshTransfer(FeMesh source, FeMesh target, int[] host, double[][] weights, int outside)
    {
        _source = source;
        _target = target;
        _host = host;
        _weights = weights;
        Outside = outside;
    }

    /// <summary>Source elements whose centroid lies in no target element; each was given the
    /// nearest one (by centroid) and its clamped linear field.</summary>
    public int Outside { get; }

    public static MeshTransfer Build(FeMesh source, FeMesh target)
    {
        var locator = new TetLocator(target);
        var host = new int[source.ElementCount];
        var weights = new double[source.ElementCount][];
        int outside = 0;
        for (int e = 0; e < source.ElementCount; e++)
        {
            var el = source.Elements[e];
            var centroid = (source.Nodes[el.N0] + source.Nodes[el.N1] + source.Nodes[el.N2] + source.Nodes[el.N3]) * 0.25;
            var (t, w, inside) = locator.Locate(centroid);
            host[e] = t;
            weights[e] = w;
            if (!inside) outside++;
        }
        return new MeshTransfer(source, target, host, weights, outside);
    }

    /// <summary>Power density per target element [W/m³] for a power density per source element.</summary>
    public double[] Heat(IReadOnlyList<double> sourceDensity)
    {
        var power = new double[_target.ElementCount];
        for (int e = 0; e < _source.ElementCount; e++)
            power[_host[e]] += sourceDensity[e] * _source.ElementVolume(e);
        for (int t = 0; t < power.Length; t++)
            if (power[t] != 0) power[t] /= _target.ElementVolume(t);
        return power;
    }

    /// <summary>The target's nodal field at every node of <paramref name="mesh"/> (a point outside
    /// the target takes the nearest element's clamped field).</summary>
    public static double[] AtNodes(FeMesh mesh, FeMesh target, IReadOnlyList<double> targetNodal)
    {
        var locator = new TetLocator(target);
        var values = new double[mesh.NodeCount];
        for (int n = 0; n < values.Length; n++)
        {
            var (e, w, _) = locator.Locate(mesh.Nodes[n]);
            var el = target.Elements[e];
            values[n] = w[0] * targetNodal[el.N0] + w[1] * targetNodal[el.N1] + w[2] * targetNodal[el.N2] + w[3] * targetNodal[el.N3];
        }
        return values;
    }

    /// <summary>The target's nodal field at every source element's centroid.</summary>
    public double[] ElementValues(IReadOnlyList<double> targetNodal)
    {
        var values = new double[_source.ElementCount];
        for (int e = 0; e < values.Length; e++)
        {
            var el = _target.Elements[_host[e]];
            var w = _weights[e];
            values[e] = w[0] * targetNodal[el.N0] + w[1] * targetNodal[el.N1]
                      + w[2] * targetNodal[el.N2] + w[3] * targetNodal[el.N3];
        }
        return values;
    }
}

/// <summary>
/// Point location in a tetrahedral mesh: a uniform grid of the elements' bounding boxes, then
/// barycentric coordinates.
/// </summary>
public sealed class TetLocator
{
    private readonly FeMesh _mesh;
    private readonly List<int>[] _cells;
    private readonly double _x0, _y0, _z0, _size;
    private readonly int _nx, _ny, _nz;
    private readonly Vector3D[] _centroids;

    public TetLocator(FeMesh mesh)
    {
        _mesh = mesh;
        var nodes = mesh.Nodes;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in nodes)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }
        // About one element per cell on average, from the mean element volume.
        double volume = Math.Max((maxX - minX) * (maxY - minY) * Math.Max(maxZ - minZ, 1e-12), 1e-30);
        _size = Math.Max(Math.Cbrt(volume / Math.Max(1, mesh.ElementCount)) * 2, 1e-9);
        _x0 = minX; _y0 = minY; _z0 = minZ;
        _nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / _size) + 1);
        _ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / _size) + 1);
        _nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / _size) + 1);
        _cells = new List<int>[_nx * _ny * _nz];
        _centroids = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            Vector3D a = nodes[el.N0], b = nodes[el.N1], c = nodes[el.N2], d = nodes[el.N3];
            _centroids[e] = (a + b + c + d) * 0.25;
            var (i0, j0, k0) = Cell(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
                Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)), Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)));
            var (i1, j1, k1) = Cell(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
                Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)), Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z)));
            for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        (_cells[(k * _ny + j) * _nx + i] ??= new List<int>()).Add(e);
        }
    }

    private (int, int, int) Cell(double x, double y, double z) => (
        Math.Clamp((int)((x - _x0) / _size), 0, _nx - 1),
        Math.Clamp((int)((y - _y0) / _size), 0, _ny - 1),
        Math.Clamp((int)((z - _z0) / _size), 0, _nz - 1));

    /// <summary>The element holding <paramref name="p"/> and the point's barycentric weights;
    /// a point in no element gets the nearest element (by centroid, widening the search) and
    /// its clamped, renormalised weights, with Inside false.</summary>
    public (int Element, double[] Weights, bool Inside) Locate(Vector3D p)
    {
        var (i, j, k) = Cell(p.X, p.Y, p.Z);
        if (_cells[(k * _ny + j) * _nx + i] is { } candidates)
            foreach (int e in candidates)
            {
                var w = Barycentric(e, p);
                if (w.All(v => v >= -1e-9)) return (e, w, true);
            }

        // Nearest element by centroid, in growing shells of cells.
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int radius = 0; radius <= Math.Max(_nx, Math.Max(_ny, _nz)); radius++)
        {
            for (int kk = Math.Max(0, k - radius); kk <= Math.Min(_nz - 1, k + radius); kk++)
                for (int jj = Math.Max(0, j - radius); jj <= Math.Min(_ny - 1, j + radius); jj++)
                    for (int ii = Math.Max(0, i - radius); ii <= Math.Min(_nx - 1, i + radius); ii++)
                        if (_cells[(kk * _ny + jj) * _nx + ii] is { } list)
                            foreach (int e in list)
                            {
                                double d = (_centroids[e] - p).Length;
                                if (d < bestDistance) (best, bestDistance) = (e, d);
                            }
            if (best >= 0 && bestDistance < radius * _size) break;
        }
        var clamped = Barycentric(best, p).Select(v => Math.Max(0, v)).ToArray();
        double sum = clamped.Sum();
        return (best, sum > 0 ? clamped.Select(v => v / sum).ToArray() : new[] { 0.25, 0.25, 0.25, 0.25 }, false);
    }

    private double[] Barycentric(int e, Vector3D p)
    {
        var el = _mesh.Elements[e];
        Vector3D a = _mesh.Nodes[el.N0], b = _mesh.Nodes[el.N1], c = _mesh.Nodes[el.N2], d = _mesh.Nodes[el.N3];
        double v = Vector3D.Dot(b - a, Vector3D.Cross(c - a, d - a));
        double wb = Vector3D.Dot(p - a, Vector3D.Cross(c - a, d - a)) / v;
        double wc = Vector3D.Dot(b - a, Vector3D.Cross(p - a, d - a)) / v;
        double wd = Vector3D.Dot(b - a, Vector3D.Cross(c - a, p - a)) / v;
        return new[] { 1 - wb - wc - wd, wb, wc, wd };
    }
}
