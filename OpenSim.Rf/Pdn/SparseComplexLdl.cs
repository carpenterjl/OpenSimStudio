using System.Numerics;
using OpenSim.Core.Geometry2D;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// LDLᵀ of a sparse complex-symmetric matrix (A = Aᵀ, no conjugation) — the plane solve when
/// the band is too wide for <see cref="BandedComplexLu"/>: a stack's several unknowns per node
/// and the rings of nodes around every port widen the band to the point where n·b² is minutes
/// per frequency, while a nested-dissection order keeps the factor's fill near n·log n.
///
/// <para>The structure (elimination tree, the factor's pattern) is found once; each frequency
/// refactors the values (Davis's up-looking LDL). There is no pivoting: a pivot that comes out
/// zero is reported, and the caller checks every solve's residual, refines it, and falls back to the
/// pivoting band solve when that does not converge.</para>
/// </summary>
internal sealed class SparseComplexLdl
{
    private readonly int _n;
    private readonly int[] _perm, _inverse;          // new → old, old → new
    private readonly int[] _ap, _ai;                 // A's pattern in the new order, upper part by columns (i ≤ k)
    private readonly int[] _sourceOfEntry;           // for each stored entry, its index in the caller's CSR
    private readonly int[] _parent, _lp;
    private readonly long _flops;

    // Numeric state (one instance per worker).
    private readonly int[] _li, _lnz, _flag, _pattern;
    private readonly double[] _lxr, _lxi, _dr, _di, _yr, _yi;

    public int Order => _n;
    public long FactorNonZeros => _lp[_n];

    /// <summary>Multiply-adds of one factorization (Σ over columns of their count squared).</summary>
    public long Flops => _flops;

    /// <param name="rowPointers">The matrix's full symmetric pattern in CSR.</param>
    /// <param name="permutation">The elimination order: new index → old.</param>
    public SparseComplexLdl(int[] rowPointers, int[] columnIndices, int[] permutation)
    {
        _n = rowPointers.Length - 1;
        _perm = permutation;
        _inverse = new int[_n];
        for (int k = 0; k < _n; k++) _inverse[_perm[k]] = k;

        // A by columns of the new order, the entries on and above the diagonal only.
        var count = new int[_n + 1];
        for (int old = 0; old < _n; old++)
            for (int p = rowPointers[old]; p < rowPointers[old + 1]; p++)
            {
                int i = _inverse[old], k = _inverse[columnIndices[p]];
                if (i <= k) count[k + 1]++;
            }
        _ap = new int[_n + 1];
        for (int k = 0; k < _n; k++) _ap[k + 1] = _ap[k] + count[k + 1];
        _ai = new int[_ap[_n]];
        _sourceOfEntry = new int[_ap[_n]];
        var next = (int[])_ap.Clone();
        for (int old = 0; old < _n; old++)
            for (int p = rowPointers[old]; p < rowPointers[old + 1]; p++)
            {
                int i = _inverse[old], k = _inverse[columnIndices[p]];
                if (i > k) continue;
                _ai[next[k]] = i;
                _sourceOfEntry[next[k]++] = p;
            }

        // Symbolic: elimination tree and column counts (ldl_symbolic).
        _parent = new int[_n];
        _lnz = new int[_n];
        _flag = new int[_n];
        for (int k = 0; k < _n; k++)
        {
            _parent[k] = -1;
            _flag[k] = k;
            _lnz[k] = 0;
            for (int p = _ap[k]; p < _ap[k + 1]; p++)
            {
                int i = _ai[p];
                if (i >= k) continue;
                for (; _flag[i] != k; i = _parent[i])
                {
                    if (_parent[i] == -1) _parent[i] = k;
                    _lnz[i]++;
                    _flag[i] = k;
                }
            }
        }
        _lp = new int[_n + 1];
        long flops = 0;
        for (int k = 0; k < _n; k++)
        {
            _lp[k + 1] = _lp[k] + _lnz[k];
            flops += (long)_lnz[k] * _lnz[k];
        }
        _flops = flops;

        _li = new int[_lp[_n]];
        _lxr = new double[_lp[_n]];
        _lxi = new double[_lp[_n]];
        _dr = new double[_n];
        _di = new double[_n];
        _yr = new double[_n];
        _yi = new double[_n];
        _pattern = new int[_n];
    }

    /// <summary>A second worker on the same structure.</summary>
    public SparseComplexLdl Clone() => new(this);

    private SparseComplexLdl(SparseComplexLdl other)
    {
        _n = other._n; _perm = other._perm; _inverse = other._inverse; _ap = other._ap; _ai = other._ai;
        _sourceOfEntry = other._sourceOfEntry; _parent = other._parent; _lp = other._lp; _flops = other._flops;
        _li = new int[_lp[_n]];
        _lxr = new double[_lp[_n]];
        _lxi = new double[_lp[_n]];
        _lnz = new int[_n];
        _flag = new int[_n];
        _dr = new double[_n];
        _di = new double[_n];
        _yr = new double[_n];
        _yi = new double[_n];
        _pattern = new int[_n];
    }

    /// <summary>Bytes the numeric factor of one worker holds.</summary>
    public long StorageBytes => 20L * _lp[_n] + 64L * _n;

    /// <summary>
    /// Factors the matrix whose values are given in the caller's CSR layout (real and imaginary
    /// parts). False when a pivot came out zero or not finite.
    /// </summary>
    public bool Factor(double[] valuesRe, double[] valuesIm)
    {
        int n = _n;
        bool ok = true;
        for (int k = 0; k < n; k++)
        {
            _yr[k] = 0; _yi[k] = 0;
            int top = n;
            _flag[k] = k;
            _lnz[k] = 0;
            for (int p = _ap[k]; p < _ap[k + 1]; p++)
            {
                int i = _ai[p], source = _sourceOfEntry[p];
                _yr[i] += valuesRe[source];
                _yi[i] += valuesIm[source];
                int length = 0;
                for (; _flag[i] != k; i = _parent[i])
                {
                    _pattern[length++] = i;
                    _flag[i] = k;
                }
                while (length > 0) _pattern[--top] = _pattern[--length];
            }
            double dr = _yr[k], di = _yi[k];
            _yr[k] = 0; _yi[k] = 0;
            for (; top < n; top++)
            {
                int i = _pattern[top];
                double yr = _yr[i], yi = _yi[i];
                _yr[i] = 0; _yi[i] = 0;
                int end = _lp[i] + _lnz[i];
                for (int p = _lp[i]; p < end; p++)
                {
                    int r = _li[p];
                    double lr = _lxr[p], lim = _lxi[p];
                    _yr[r] -= lr * yr - lim * yi;
                    _yi[r] -= lr * yi + lim * yr;
                }
                // l_ki = y_i / d_i; d_k −= l_ki·y_i.
                double pr = _dr[i], pim = _di[i], inv = 1 / (pr * pr + pim * pim);
                double lkr = (yr * pr + yi * pim) * inv, lki = (yi * pr - yr * pim) * inv;
                dr -= lkr * yr - lki * yi;
                di -= lkr * yi + lki * yr;
                _li[end] = k;
                _lxr[end] = lkr;
                _lxi[end] = lki;
                _lnz[i]++;
            }
            _dr[k] = dr;
            _di[k] = di;
            // A small last pivot is the matrix's own near-singularity (its static modes at low
            // frequency), not a failure of the order; only a zero or non-finite one is.
            if (!double.IsFinite(dr) || !double.IsFinite(di) || (dr == 0 && di == 0)) { _dr[k] = 1e-300; ok = false; }
        }
        return ok;
    }

    /// <summary>Solves A·x = b in place (b in the caller's order), with the last factorization.</summary>
    public void Solve(double[] xr, double[] xi)
    {
        int n = _n;
        var r = _yr; var im = _yi;
        for (int k = 0; k < n; k++) { r[k] = xr[_perm[k]]; im[k] = xi[_perm[k]]; }
        // L·y = b (unit lower; column j holds L[i, j] for its rows i > j).
        for (int j = 0; j < n; j++)
        {
            double br = r[j], bi = im[j];
            if (br == 0 && bi == 0) continue;
            for (int p = _lp[j]; p < _lp[j + 1]; p++)
            {
                int i = _li[p];
                double lr = _lxr[p], li = _lxi[p];
                r[i] -= lr * br - li * bi;
                im[i] -= lr * bi + li * br;
            }
        }
        // D·z = y.
        for (int k = 0; k < n; k++)
        {
            double dr = _dr[k], di = _di[k], inv = 1 / (dr * dr + di * di);
            double ar = r[k], ai = im[k];
            r[k] = (ar * dr + ai * di) * inv;
            im[k] = (ai * dr - ar * di) * inv;
        }
        // Lᵀ·x = z.
        for (int j = n - 1; j >= 0; j--)
        {
            double sr = r[j], si = im[j];
            for (int p = _lp[j]; p < _lp[j + 1]; p++)
            {
                int i = _li[p];
                double lr = _lxr[p], li = _lxi[p];
                sr -= lr * r[i] - li * im[i];
                si -= lr * im[i] + li * r[i];
            }
            r[j] = sr; im[j] = si;
        }
        for (int k = 0; k < n; k++) { xr[_perm[k]] = r[k]; xi[_perm[k]] = im[k]; }
        Array.Clear(r);
        Array.Clear(im);
    }

    /// <summary>
    /// A nested-dissection order of a planar mesh's nodes by coordinate bisection: split the
    /// nodes at the median of the longer side, take the nodes of one half that touch the other as
    /// the separator, order each half the same way, and the separator last. Returns new → old.
    /// </summary>
    public static int[] NestedDissection(IReadOnlyList<Point2> position, int[] rowPointers, int[] columnIndices, int leaf = 48)
    {
        int n = position.Count;
        var order = new List<int>(n);
        var side = new int[n];
        var stack = new Stack<(List<int> Nodes, int Depth)>();
        // Iterative post-order: emit (left, right, separator) with the separator after both halves.
        var output = new List<int>[1];
        void Recurse(List<int> nodes, List<int> into)
        {
            if (nodes.Count <= leaf)
            {
                into.AddRange(nodes);
                return;
            }
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (int v in nodes)
            {
                var p = position[v];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            bool alongX = maxX - minX >= maxY - minY;
            var sorted = nodes.OrderBy(v => alongX ? position[v].X : position[v].Y).ThenBy(v => v).ToList();
            int half = sorted.Count / 2;
            var mark = new Dictionary<int, int>(sorted.Count);
            for (int k = 0; k < sorted.Count; k++) mark[sorted[k]] = k < half ? 1 : 2;
            var left = new List<int>();
            var separator = new List<int>();
            var right = new List<int>();
            foreach (int v in sorted)
            {
                if (mark[v] == 2) { right.Add(v); continue; }
                bool touches = false;
                for (int p = rowPointers[v]; p < rowPointers[v + 1] && !touches; p++)
                    touches = mark.TryGetValue(columnIndices[p], out int m) && m == 2;
                (touches ? separator : left).Add(v);
            }
            if (left.Count == 0 || right.Count == 0)
            {
                into.AddRange(nodes);                         // no useful split (a degenerate cluster)
                return;
            }
            Recurse(left, into);
            Recurse(right, into);
            into.AddRange(separator);
        }
        var all = Enumerable.Range(0, n).ToList();
        // Components of the graph separately (each is its own subtree).
        var seen = new bool[n];
        foreach (int start in all)
        {
            if (seen[start]) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                component.Add(v);
                for (int p = rowPointers[v]; p < rowPointers[v + 1]; p++)
                {
                    int w = columnIndices[p];
                    if (!seen[w]) { seen[w] = true; queue.Enqueue(w); }
                }
            }
            Recurse(component, order);
        }
        return order.ToArray();
    }
}
