using System.Collections.Concurrent;
using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>A node of the plane grid nearest a point, with how far off it is.</summary>
public sealed record PlaneGrid(IReadOnlyList<(Point2 Position, int Node)> Nodes, double PitchMeters, double Z)
{
    public int NodeNear(Point2 p) => Nodes.MinBy(n => (n.Position - p).Length).Node;

    /// <summary>Distance from <paramref name="p"/> to the nearest grid node.</summary>
    public double DistanceTo(Point2 p) => Nodes.Min(n => (n.Position - p).Length);
}

/// <summary>Port impedance of a <see cref="PeecModel"/> at one frequency.</summary>
/// <param name="Impedance">Z[i, j]: volts across port i per ampere through port j.</param>
/// <param name="FilamentCurrents">For each port, the current in every filament with one
/// ampere through that port (model order); null unless asked for.</param>
public sealed record PeecPoint(double FrequencyHz, Complex[,] Impedance, Complex[][]? FilamentCurrents)
{
    public double Resistance(int i, int j) => Impedance[i, j].Real;
    public double Inductance(int i, int j) => Impedance[i, j].Imaginary / (2 * Math.PI * FrequencyHz);
}

/// <summary>
/// Conductors as bundles of straight filaments, each carrying a uniform current: the
/// partial-element equivalent circuit for resistance and inductance (Ruehli 1972; the
/// formulation of FastHenry without its multipole acceleration). A bar's cross-section is
/// cut into filaments, finer toward its faces; every filament has its DC resistance and a
/// partial inductance with every other, and the filaments of one bar share its two end
/// nodes. How the current divides among them — crowding to the surface with frequency, and
/// toward a neighbouring return — then comes out of the circuit solve
/// <code>
///   [ R + jωL   −Aᵀ ] [ I ]   [ 0 ]
///   [   A        0  ] [ V ] = [ J ]
/// </code>
/// with A the node–filament incidence and J the port currents. A plane or pour is a grid of
/// such bars in both directions.
///
/// <para>Parallel filaments couple through the exact finite-section kernel
/// (<see cref="PartialInductance.BarBarMutual"/>); filaments at an angle through the
/// straight-filament formula between their centre lines, and perpendicular ones not at all.
/// No displacement current: valid while the structure is short against a wavelength.</para>
/// </summary>
public sealed class PeecModel
{
    private sealed record Filament(Vector3D Start, Vector3D End, Vector3D Along, Vector3D Across, Vector3D Normal,
        double Width, double Thickness, int From, int To, double Resistance)
    {
        public double Length => (End - Start).Length;
        public Vector3D Centre => 0.5 * (Start + End);
    }

    private readonly List<Vector3D> _nodes = new();
    private readonly List<int> _parent = new();
    private readonly List<Filament> _filaments = new();
    private readonly List<(string Name, int Positive, int Negative)> _ports = new();

    public int NodeCount => _nodes.Count;
    public int FilamentCount => _filaments.Count;
    public int PortCount => _ports.Count;
    public IReadOnlyList<string> PortNames => _ports.Select(p => p.Name).ToList();

    public int AddNode(Vector3D position)
    {
        _nodes.Add(position);
        _parent.Add(_parent.Count);
        return _nodes.Count - 1;
    }

    public Vector3D PositionOf(int node) => _nodes[node];

    private int Root(int node)
    {
        while (_parent[node] != node) node = _parent[node] = _parent[_parent[node]];
        return node;
    }

    /// <summary>Joins two nodes with an ideal connection.</summary>
    public void AddShort(int a, int b)
    {
        int ra = Root(a), rb = Root(b);
        if (ra != rb) _parent[rb] = ra;
    }

    /// <summary>A port: one ampere in at <paramref name="positive"/> and out at
    /// <paramref name="negative"/>, the voltage read between them.</summary>
    public int AddPort(string name, int positive, int negative)
    {
        _ports.Add((name, positive, negative));
        return _ports.Count - 1;
    }

    /// <summary>
    /// Relative widths of <paramref name="count"/> strips across a section, each
    /// <paramref name="ratio"/> times its neighbour toward the middle, so the outermost are
    /// the thinnest. They sum to 1.
    /// </summary>
    public static double[] Graded(int count, double ratio = 2.0)
    {
        var w = new double[Math.Max(count, 1)];
        double sum = 0;
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = Math.Pow(ratio, Math.Min(i, w.Length - 1 - i));
            sum += w[i];
        }
        for (int i = 0; i < w.Length; i++) w[i] /= sum;
        return w;
    }

    /// <summary>
    /// How many strips a dimension needs for the outermost to be at most
    /// <paramref name="fractionOfSkinDepth"/> skin depths thick with a grading ratio of 2 —
    /// one where the dimension is itself that thin.
    /// </summary>
    public static int StripsFor(double dimension, double skinDepth, double fractionOfSkinDepth = 0.7, int max = 11)
    {
        for (int n = 1; n <= max; n += 2)
            if (Graded(n)[0] * dimension <= fractionOfSkinDepth * skinDepth) return n;
        return max;
    }

    /// <summary>
    /// A rectangular bar between two nodes: width in the board plane, thickness along z for
    /// a horizontal bar (for a vertical one, width along x and thickness along y). Its
    /// section is cut into <paramref name="acrossWidth"/> × <paramref name="acrossThickness"/>
    /// filaments, graded toward the faces.
    /// </summary>
    public void AddBar(int from, int to, double width, double thickness, double conductivity,
        int acrossWidth = 1, int acrossThickness = 1)
    {
        var (along, across, normal, length) = Frame(from, to);
        if (!(width > 0 && thickness > 0 && conductivity > 0))
            throw new ArgumentException("A bar needs positive width, thickness and conductivity.");
        var ws = Graded(acrossWidth);
        var ts = Graded(acrossThickness);
        double offsetW = -width / 2;
        foreach (double fw in ws)
        {
            double w = fw * width;
            double offsetT = -thickness / 2;
            foreach (double ft in ts)
            {
                double t = ft * thickness;
                var shift = (offsetW + w / 2) * across + (offsetT + t / 2) * normal;
                _filaments.Add(new Filament(_nodes[from] + shift, _nodes[to] + shift, along, across, normal,
                    w, t, from, to, length / (conductivity * w * t)));
                offsetT += t;
            }
            offsetW += w;
        }
    }

    /// <summary>
    /// A solid round wire between two nodes, its section filled with square filaments on a
    /// <paramref name="cellsAcross"/> grid (those whose centre is inside the circle). The
    /// filaments' resistance is scaled so that together they have the wire's DC resistance.
    /// One cell is the square of the wire's area.
    /// </summary>
    public void AddRoundWire(int from, int to, double radius, double conductivity, int cellsAcross = 1)
    {
        var (along, across, normal, length) = Frame(from, to);
        if (!(radius > 0 && conductivity > 0))
            throw new ArgumentException("A wire needs a positive radius and conductivity.");
        if (cellsAcross <= 1)
        {
            double side = radius * Math.Sqrt(Math.PI);
            _filaments.Add(new Filament(_nodes[from], _nodes[to], along, across, normal, side, side, from, to,
                length / (conductivity * Math.PI * radius * radius)));
            return;
        }
        double cell = 2 * radius / cellsAcross;
        var centres = new List<(double W, double T)>();
        for (int i = 0; i < cellsAcross; i++)
            for (int j = 0; j < cellsAcross; j++)
            {
                double w = -radius + (i + 0.5) * cell, t = -radius + (j + 0.5) * cell;
                if (w * w + t * t <= radius * radius) centres.Add((w, t));
            }
        // All filaments alike and in parallel: each has N times the wire's resistance.
        double each = centres.Count * length / (conductivity * Math.PI * radius * radius);
        foreach (var (w, t) in centres)
        {
            var shift = w * across + t * normal;
            _filaments.Add(new Filament(_nodes[from] + shift, _nodes[to] + shift, along, across, normal,
                cell, cell, from, to, each));
        }
    }

    /// <summary>
    /// A plane or pour at height <paramref name="z"/>: a square grid of nodes at
    /// <paramref name="pitch"/> inside the shape, joined to their neighbours by bars one pitch
    /// wide carrying the x and the y current. One filament through the thickness: the
    /// distribution of the current over the plane is solved, its skin effect through the
    /// thickness is not.
    /// </summary>
    public PlaneGrid AddPlane(IReadOnlyList<Polygon2> shape, double z, double thickness, double pitch,
        double conductivity)
    {
        if (!(pitch > 0 && thickness > 0 && conductivity > 0))
            throw new ArgumentException("A plane needs a positive pitch, thickness and conductivity.");
        var index = new PolygonSetIndex(shape);
        double minX = shape.Min(p => p.Outer.Min(q => q.X)), maxX = shape.Max(p => p.Outer.Max(q => q.X));
        double minY = shape.Min(p => p.Outer.Min(q => q.Y)), maxY = shape.Max(p => p.Outer.Max(q => q.Y));
        int nx = Math.Max(1, (int)Math.Round((maxX - minX) / pitch)), ny = Math.Max(1, (int)Math.Round((maxY - minY) / pitch));
        double px = (maxX - minX) / nx, py = (maxY - minY) / ny;
        var id = new int[nx, ny];
        var nodes = new List<(Point2, int)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var p = new Point2(minX + (i + 0.5) * px, minY + (j + 0.5) * py);
                id[i, j] = index.Contains(p) ? AddNode(new Vector3D(p.X, p.Y, z)) : -1;
                if (id[i, j] >= 0) nodes.Add((p, id[i, j]));
            }
        if (nodes.Count == 0) throw new InvalidOperationException("The plane shape holds no grid node at this pitch.");
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (id[i, j] < 0) continue;
                if (i + 1 < nx && id[i + 1, j] >= 0) AddBar(id[i, j], id[i + 1, j], py, thickness, conductivity);
                if (j + 1 < ny && id[i, j + 1] >= 0) AddBar(id[i, j], id[i, j + 1], px, thickness, conductivity);
            }
        return new PlaneGrid(nodes, Math.Max(px, py), z);
    }

    private (Vector3D Along, Vector3D Across, Vector3D Normal, double Length) Frame(int from, int to)
    {
        var d = _nodes[to] - _nodes[from];
        double length = d.Length;
        if (!(length > 0)) throw new ArgumentException("A conductor needs two distinct end points.");
        var along = d / length;
        var across = Vector3D.Cross(Vector3D.UnitZ, along);
        if (across.Length < 1e-9) across = Vector3D.UnitX;      // vertical: width along x
        else across /= across.Length;
        return (along, across, Vector3D.Cross(along, across), length);
    }

    // ------------------------------------------------------------------
    // Inductance matrix
    // ------------------------------------------------------------------

    private static double Mutual(Filament a, Filament b, ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double> cache)
    {
        double cos = Vector3D.Dot(a.Along, b.Along);
        if (Math.Abs(cos) < 1e-9) return 0;
        if (1 - Math.Abs(cos) < 1e-9)
        {
            // Section axes of b in a's frame: the same, or turned a quarter.
            double bw, bt;
            if (Math.Abs(Math.Abs(Vector3D.Dot(a.Across, b.Across)) - 1) < 1e-6) { bw = b.Width; bt = b.Thickness; }
            else if (Math.Abs(Math.Abs(Vector3D.Dot(a.Across, b.Normal)) - 1) < 1e-6) { bw = b.Thickness; bt = b.Width; }
            else return FilamentMutual.Between(a.Start, a.End, b.Start, b.End);

            var offset = b.Centre - a.Centre;
            double la = a.Length, lb = b.Length;
            double e = Vector3D.Dot(offset, a.Across) + 0.5 * (a.Width - bw);
            double p = Vector3D.Dot(offset, a.Normal) + 0.5 * (a.Thickness - bt);
            double l3 = Vector3D.Dot(offset, a.Along) + 0.5 * (la - lb);
            static long Q(double v) => (long)Math.Round(v * 1e10);        // 0.1 nm
            var key = (Q(a.Width), Q(a.Thickness), Q(la), Q(bw), Q(bt), Q(lb), Q(e), Q(p), Q(l3));
            double m = cache.GetOrAdd(key, _ => PartialInductance.BarBarMutual(a.Width, a.Thickness, la, bw, bt, lb, e, p, l3));
            return Math.Sign(cos) * m;
        }
        return FilamentMutual.Between(a.Start, a.End, b.Start, b.End);
    }

    private double[,]? _inductance;

    /// <summary>The filaments' partial inductance matrix [H], computed once.</summary>
    private double[,] Inductance()
    {
        if (_inductance is not null) return _inductance;
        int n = _filaments.Count;
        var l = new double[n, n];
        var cache = new ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double>();
        Parallel.For(0, n, i =>
        {
            for (int j = i; j < n; j++) l[i, j] = Mutual(_filaments[i], _filaments[j], cache);
        });
        for (int i = 0; i < n; i++)
            for (int j = 0; j < i; j++) l[i, j] = l[j, i];
        return _inductance = l;
    }

    // ------------------------------------------------------------------
    // Solve
    // ------------------------------------------------------------------

    /// <summary>The port impedance matrix at each frequency (all above zero).</summary>
    public IReadOnlyList<PeecPoint> Solve(IReadOnlyList<double> frequenciesHz, bool keepCurrents = false,
        CancellationToken cancellationToken = default)
    {
        if (_ports.Count == 0) throw new InvalidOperationException("The model has no port.");
        if (_filaments.Count == 0) throw new InvalidOperationException("The model has no conductor.");
        int nb = _filaments.Count;

        // Circuit nodes after the shorts; one reference node per connected piece of copper.
        var circuit = new Dictionary<int, int>();
        int Circuit(int node)
        {
            int root = Root(node);
            if (!circuit.TryGetValue(root, out int id)) circuit[root] = id = circuit.Count;
            return id;
        }
        var ends = _filaments.Select(f => (From: Circuit(f.From), To: Circuit(f.To))).ToArray();
        var ports = _ports.Select(p => (p.Name, Positive: Circuit(p.Positive), Negative: Circuit(p.Negative))).ToArray();
        int nn = circuit.Count;

        var piece = Enumerable.Range(0, nn).ToArray();
        int Piece(int x) { while (piece[x] != x) x = piece[x] = piece[piece[x]]; return x; }
        foreach (var (from, to) in ends) { int a = Piece(from), b = Piece(to); if (a != b) piece[b] = a; }
        foreach (var port in ports)
        {
            if (port.Positive == port.Negative)
                throw new InvalidOperationException($"Port '{port.Name}' has both terminals on one node.");
            if (Piece(port.Positive) != Piece(port.Negative))
                throw new InvalidOperationException(
                    $"Port '{port.Name}' is between two conductors that are not connected anywhere: no current can flow.");
        }
        var reference = new HashSet<int>(Enumerable.Range(0, nn).Where(x => Piece(x) == x));

        var l = Inductance();
        var result = new List<PeecPoint>();
        int size = nb + nn;
        foreach (double f in frequenciesHz)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(f > 0)) throw new ArgumentOutOfRangeException(nameof(frequenciesHz), "Frequencies must be above zero.");
            double omega = 2 * Math.PI * f;
            // Row-major, real and imaginary parts apart: the factorization below is the
            // whole cost of a frequency point.
            var re = new double[(long)size * size];
            var im = new double[(long)size * size];
            for (int i = 0; i < nb; i++)
            {
                long row = (long)i * size;
                for (int j = 0; j < nb; j++) im[row + j] = omega * l[i, j];
                re[row + i] = _filaments[i].Resistance;
                // Filament equation: Z·I − (V_from − V_to) = 0.
                re[row + nb + ends[i].From] -= 1;
                re[row + nb + ends[i].To] += 1;
            }
            for (int k = 0; k < nn; k++)
                if (reference.Contains(k)) re[(long)(nb + k) * size + nb + k] = 1;      // V = 0 at the reference
            for (int i = 0; i < nb; i++)
            {
                // Node equation: current leaving the node through filaments = current injected.
                if (!reference.Contains(ends[i].From)) re[(long)(nb + ends[i].From) * size + i] += 1;
                if (!reference.Contains(ends[i].To)) re[(long)(nb + ends[i].To) * size + i] -= 1;
            }
            var pivots = DenseFactor(re, im, size, cancellationToken);

            var z = new Complex[ports.Length, ports.Length];
            var currents = keepCurrents ? new Complex[ports.Length][] : null;
            for (int q = 0; q < ports.Length; q++)
            {
                var x = new Complex[size];
                if (!reference.Contains(ports[q].Positive)) x[nb + ports[q].Positive] = 1;
                if (!reference.Contains(ports[q].Negative)) x[nb + ports[q].Negative] = -1;
                DenseSolve(re, im, size, pivots, x);
                for (int p = 0; p < ports.Length; p++)
                    z[p, q] = x[nb + ports[p].Positive] - x[nb + ports[p].Negative];
                if (currents is not null) currents[q] = x.Take(nb).ToArray();
            }
            for (int p = 0; p < ports.Length; p++)
                for (int q = p + 1; q < ports.Length; q++)
                    z[p, q] = z[q, p] = 0.5 * (z[p, q] + z[q, p]);
            result.Add(new PeecPoint(f, z, currents));
        }
        return result;
    }

    /// <summary>In-place LU with partial pivoting of a dense complex matrix held row-major
    /// in two real arrays; the trailing update runs in parallel over rows (each row's
    /// arithmetic is the serial one, so the result does not depend on the thread count).</summary>
    private static int[] DenseFactor(double[] re, double[] im, int n, CancellationToken cancellationToken)
    {
        var pivots = new int[n];
        for (int k = 0; k < n; k++)
        {
            if ((k & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            int pivot = k;
            double best = -1;
            for (int i = k; i < n; i++)
            {
                long at = (long)i * n + k;
                double magnitude = re[at] * re[at] + im[at] * im[at];
                if (magnitude > best) { best = magnitude; pivot = i; }
            }
            if (!(best > 0))
                throw new InvalidOperationException(
                    "The conductor network is singular: a piece of copper is left without a current path.");
            pivots[k] = pivot;
            if (pivot != k)
            {
                long a = (long)k * n, b = (long)pivot * n;
                for (int j = 0; j < n; j++)
                {
                    (re[a + j], re[b + j]) = (re[b + j], re[a + j]);
                    (im[a + j], im[b + j]) = (im[b + j], im[a + j]);
                }
            }
            long rowK = (long)k * n;
            double pr = re[rowK + k], pi = im[rowK + k];
            double scale = 1 / (pr * pr + pi * pi);
            int kk = k;
            void Eliminate(int i)
            {
                long row = (long)i * n;
                double ar = re[row + kk], ai = im[row + kk];
                if (ar == 0 && ai == 0) return;
                double fr = (ar * pr + ai * pi) * scale, fi = (ai * pr - ar * pi) * scale;
                re[row + kk] = fr;
                im[row + kk] = fi;
                long source = (long)kk * n;
                for (int j = kk + 1; j < n; j++)
                {
                    double sr = re[source + j], si = im[source + j];
                    re[row + j] -= fr * sr - fi * si;
                    im[row + j] -= fr * si + fi * sr;
                }
            }
            if (n - k > 128) Parallel.For(k + 1, n, Eliminate);
            else for (int i = k + 1; i < n; i++) Eliminate(i);
        }
        return pivots;
    }

    private static void DenseSolve(double[] re, double[] im, int n, int[] pivots, Complex[] x)
    {
        var xr = new double[n];
        var xi = new double[n];
        for (int i = 0; i < n; i++) { xr[i] = x[i].Real; xi[i] = x[i].Imaginary; }
        for (int k = 0; k < n; k++)
        {
            int p = pivots[k];
            if (p != k) { (xr[k], xr[p]) = (xr[p], xr[k]); (xi[k], xi[p]) = (xi[p], xi[k]); }
        }
        for (int i = 1; i < n; i++)
        {
            long row = (long)i * n;
            double sr = xr[i], si = xi[i];
            for (int j = 0; j < i; j++)
            {
                double ar = re[row + j], ai = im[row + j];
                sr -= ar * xr[j] - ai * xi[j];
                si -= ar * xi[j] + ai * xr[j];
            }
            xr[i] = sr; xi[i] = si;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            long row = (long)i * n;
            double sr = xr[i], si = xi[i];
            for (int j = i + 1; j < n; j++)
            {
                double ar = re[row + j], ai = im[row + j];
                sr -= ar * xr[j] - ai * xi[j];
                si -= ar * xi[j] + ai * xr[j];
            }
            double dr = re[row + i], di = im[row + i];
            double scale = 1 / (dr * dr + di * di);
            xr[i] = (sr * dr + si * di) * scale;
            xi[i] = (si * dr - sr * di) * scale;
        }
        for (int i = 0; i < n; i++) x[i] = new Complex(xr[i], xi[i]);
    }

    /// <summary>Copper skin depth [m] at a frequency.</summary>
    public static double SkinDepth(double frequencyHz, double conductivity) =>
        1 / Math.Sqrt(Math.PI * frequencyHz * 4e-7 * Math.PI * conductivity);
}
