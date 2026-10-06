using System.Numerics;

namespace OpenSim.Solvers.Magnetics;

/// <summary>
/// A complex banded matrix with LU factorization without pivoting — sufficient for the
/// complex-symmetric FEM systems K + jωM (K positive definite once the Dirichlet rows are
/// replaced by identity). Real and imaginary parts are kept in separate arrays, which matters in
/// a debug build where complex arithmetic in the inner loop is several times slower.
/// </summary>
internal sealed class BandedSolver
{
    private readonly int _n, _q, _w;
    private readonly double[] _re, _im;
    private bool _factored;

    public BandedSolver(int n, int halfBandwidth)
    {
        _n = n;
        _q = halfBandwidth;
        _w = 2 * halfBandwidth + 1;
        _re = new double[(long)n * _w is var size && size <= int.MaxValue ? (int)size
            : throw new InvalidOperationException("The banded system is too large; use a coarser grid.")];
        _im = new double[_re.Length];
    }

    public int Size => _n;

    public void Add(int i, int j, double re, double im = 0)
    {
        int d = j - i + _q;
        if (d < 0 || d >= _w) throw new InvalidOperationException($"Entry ({i}, {j}) is outside the band.");
        int k = i * _w + d;
        _re[k] += re;
        _im[k] += im;
    }

    /// <summary>Replace row and column <paramref name="i"/> by the identity (a Dirichlet node with
    /// value zero).</summary>
    public void Pin(int i)
    {
        for (int j = Math.Max(0, i - _q); j <= Math.Min(_n - 1, i + _q); j++)
        {
            int a = i * _w + (j - i + _q), b = j * _w + (i - j + _q);
            _re[a] = _im[a] = 0;
            _re[b] = _im[b] = 0;
        }
        _re[i * _w + _q] = 1;
    }

    public void Factor()
    {
        for (int k = 0; k < _n; k++)
        {
            int pk = k * _w + _q;
            double pr = _re[pk], pi = _im[pk];
            double den = pr * pr + pi * pi;
            if (den == 0) throw new InvalidOperationException($"Zero pivot at row {k}: a node is not connected to anything.");
            double ir = pr / den, ii = -pi / den;
            int last = Math.Min(_n - 1, k + _q);
            for (int i = k + 1; i <= last; i++)
            {
                int ik = i * _w + (k - i + _q);
                double lr = _re[ik] * ir - _im[ik] * ii;
                double li = _re[ik] * ii + _im[ik] * ir;
                _re[ik] = lr;
                _im[ik] = li;
                if (lr == 0 && li == 0) continue;
                int rowI = i * _w - i + _q, rowK = k * _w - k + _q;
                for (int j = k + 1; j <= last; j++)
                {
                    double ur = _re[rowK + j], ui = _im[rowK + j];
                    _re[rowI + j] -= lr * ur - li * ui;
                    _im[rowI + j] -= lr * ui + li * ur;
                }
            }
        }
        _factored = true;
    }

    public Complex[] Solve(IReadOnlyList<Complex> rhs)
    {
        if (!_factored) throw new InvalidOperationException("Factor first.");
        var xr = new double[_n];
        var xi = new double[_n];
        for (int i = 0; i < _n; i++) { xr[i] = rhs[i].Real; xi[i] = rhs[i].Imaginary; }
        for (int i = 0; i < _n; i++)
        {
            int row = i * _w - i + _q;
            for (int j = Math.Max(0, i - _q); j < i; j++)
            {
                double lr = _re[row + j], li = _im[row + j];
                xr[i] -= lr * xr[j] - li * xi[j];
                xi[i] -= lr * xi[j] + li * xr[j];
            }
        }
        for (int i = _n - 1; i >= 0; i--)
        {
            int row = i * _w - i + _q;
            for (int j = i + 1; j <= Math.Min(_n - 1, i + _q); j++)
            {
                double ur = _re[row + j], ui = _im[row + j];
                xr[i] -= ur * xr[j] - ui * xi[j];
                xi[i] -= ur * xi[j] + ui * xr[j];
            }
            double pr = _re[row + i], pi = _im[row + i];
            double den = pr * pr + pi * pi;
            double vr = (xr[i] * pr + xi[i] * pi) / den;
            double vi = (xi[i] * pr - xr[i] * pi) / den;
            xr[i] = vr;
            xi[i] = vi;
        }
        var x = new Complex[_n];
        for (int i = 0; i < _n; i++) x[i] = new Complex(xr[i], xi[i]);
        return x;
    }
}
