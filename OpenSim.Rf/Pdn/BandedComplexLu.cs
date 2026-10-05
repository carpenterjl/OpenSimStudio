using System.Numerics;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// LU factorization of a complex band matrix with partial pivoting — the direct solve of
/// the plane-pair Helmholtz system. The system is complex-symmetric and, above the first
/// plane resonance, indefinite: an iterative solve stalls exactly where the answer matters
/// (at a resonance), and an unpivoted LDLᵀ can meet a small pivot in a leading block. With
/// the nodes in reverse Cuthill–McKee order the half-bandwidth of a planar mesh is about the
/// square root of the node count, so this costs n·b² per frequency.
///
/// <para>Storage is by rows: row i holds columns i − b … i + 2b (the upper band widens by b
/// under row interchanges). Real and imaginary parts are kept in separate arrays.</para>
/// </summary>
internal sealed class BandedComplexLu
{
    private readonly int _n, _b, _width;
    private readonly double[] _re, _im;
    private readonly int[] _pivot;

    public int Order => _n;
    public int HalfBandwidth => _b;

    /// <summary>Bytes of band storage a factorization of this size holds.</summary>
    public static long StorageBytes(int order, int halfBandwidth) =>
        16L * order * (3L * halfBandwidth + 1);

    public BandedComplexLu(int order, int halfBandwidth)
    {
        _n = order;
        _b = halfBandwidth;
        _width = 3 * halfBandwidth + 1;
        _re = new double[(long)order * _width];
        _im = new double[(long)order * _width];
        _pivot = new int[order];
    }

    private int At(int row, int column) => row * _width + (column - row + _b);

    /// <summary>Adds to entry (row, column); |row − column| must not exceed the half-bandwidth.</summary>
    public void Add(int row, int column, Complex value)
    {
        int at = At(row, column);
        _re[at] += value.Real;
        _im[at] += value.Imaginary;
    }

    public void Clear()
    {
        Array.Clear(_re);
        Array.Clear(_im);
    }

    public void Factor()
    {
        for (int k = 0; k < _n; k++)
        {
            int lastRow = Math.Min(_n - 1, k + _b);
            int lastColumn = Math.Min(_n - 1, k + 2 * _b);

            int pivotRow = k;
            double best = -1;
            for (int i = k; i <= lastRow; i++)
            {
                int at = At(i, k);
                double magnitude = _re[at] * _re[at] + _im[at] * _im[at];
                if (magnitude > best) { best = magnitude; pivotRow = i; }
            }
            if (!(best > 0))
                throw new InvalidOperationException(
                    $"The plane-pair system is singular (zero pivot at unknown {k}).");
            _pivot[k] = pivotRow;
            if (pivotRow != k)
                for (int j = k; j <= lastColumn; j++)
                {
                    int a = At(k, j), c = At(pivotRow, j);
                    (_re[a], _re[c]) = (_re[c], _re[a]);
                    (_im[a], _im[c]) = (_im[c], _im[a]);
                }

            int diagonal = At(k, k);
            double pr = _re[diagonal], pi = _im[diagonal];
            double scale = 1 / (pr * pr + pi * pi);
            for (int i = k + 1; i <= lastRow; i++)
            {
                int at = At(i, k);
                double ar = _re[at], ai = _im[at];
                if (ar == 0 && ai == 0) continue;
                double fr = (ar * pr + ai * pi) * scale, fi = (ai * pr - ar * pi) * scale;
                _re[at] = fr;
                _im[at] = fi;
                int target = At(i, k + 1), source = At(k, k + 1);
                for (int j = k + 1; j <= lastColumn; j++, target++, source++)
                {
                    double sr = _re[source], si = _im[source];
                    _re[target] -= fr * sr - fi * si;
                    _im[target] -= fr * si + fi * sr;
                }
            }
        }
    }

    /// <summary>Solves A·x = b in place.</summary>
    public void Solve(Complex[] x)
    {
        // In plain doubles: this loop is the whole cost of a many-port solve.
        var xr = new double[_n];
        var xi = new double[_n];
        for (int i = 0; i < _n; i++) { xr[i] = x[i].Real; xi[i] = x[i].Imaginary; }

        for (int k = 0; k < _n; k++)
        {
            int p = _pivot[k];
            if (p != k)
            {
                (xr[k], xr[p]) = (xr[p], xr[k]);
                (xi[k], xi[p]) = (xi[p], xi[k]);
            }
            double kr = xr[k], ki = xi[k];
            if (kr == 0 && ki == 0) continue;
            int lastRow = Math.Min(_n - 1, k + _b);
            // Entry (i, k) sits at i·width + (k − i + b): one row down is width − 1 further.
            int at = At(k + 1, k);
            for (int i = k + 1; i <= lastRow; i++, at += _width - 1)
            {
                double ar = _re[at], ai = _im[at];
                xr[i] -= ar * kr - ai * ki;
                xi[i] -= ar * ki + ai * kr;
            }
        }
        for (int i = _n - 1; i >= 0; i--)
        {
            int lastColumn = Math.Min(_n - 1, i + 2 * _b);
            double sr = xr[i], si = xi[i];
            int at = At(i, i + 1);
            for (int j = i + 1; j <= lastColumn; j++, at++)
            {
                double ar = _re[at], ai = _im[at];
                sr -= ar * xr[j] - ai * xi[j];
                si -= ar * xi[j] + ai * xr[j];
            }
            int diagonal = At(i, i);
            double dr = _re[diagonal], di = _im[diagonal];
            double scale = 1 / (dr * dr + di * di);
            xr[i] = (sr * dr + si * di) * scale;
            xi[i] = (si * dr - sr * di) * scale;
        }
        for (int i = 0; i < _n; i++) x[i] = new Complex(xr[i], xi[i]);
    }

    /// <summary>
    /// Reverse Cuthill–McKee ordering of a symmetric sparsity pattern: for each node, its
    /// position in the new order. Every connected component is started from a node of least
    /// degree found by repeated breadth-first sweeps (a pseudo-peripheral node).
    /// </summary>
    public static int[] ReverseCuthillMcKee(int[] rowPointers, int[] columnIndices)
    {
        int n = rowPointers.Length - 1;
        int Degree(int i) => rowPointers[i + 1] - rowPointers[i];
        var order = new List<int>(n);
        var visited = new bool[n];
        var level = new int[n];

        int Sweep(int start, List<int>? collect)
        {
            // Breadth-first from start over unvisited-by-order nodes; returns the last node
            // of the deepest level with the smallest degree.
            var queue = new Queue<int>();
            var seen = new HashSet<int> { start };
            queue.Enqueue(start);
            level[start] = 0;
            int deepest = start;
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                collect?.Add(u);
                if (level[u] > level[deepest] || (level[u] == level[deepest] && Degree(u) < Degree(deepest)))
                    deepest = u;
                var neighbours = new List<int>();
                for (int k = rowPointers[u]; k < rowPointers[u + 1]; k++)
                {
                    int v = columnIndices[k];
                    if (v != u && !visited[v] && seen.Add(v)) neighbours.Add(v);
                }
                neighbours.Sort((a, c) => Degree(a) != Degree(c) ? Degree(a).CompareTo(Degree(c)) : a.CompareTo(c));
                foreach (int v in neighbours)
                {
                    level[v] = level[u] + 1;
                    queue.Enqueue(v);
                }
            }
            return deepest;
        }

        for (int seed = 0; seed < n; seed++)
        {
            if (visited[seed]) continue;
            int start = seed;
            for (int pass = 0; pass < 4; pass++)
            {
                int far = Sweep(start, null);
                if (far == start) break;
                start = far;
            }
            var component = new List<int>();
            Sweep(start, component);
            foreach (int u in component) visited[u] = true;
            order.AddRange(component);
        }

        var position = new int[n];
        for (int k = 0; k < n; k++) position[order[n - 1 - k]] = k;
        return position;
    }
}
