namespace OpenSim.Cfd;

/// <summary>
/// Anderson acceleration of a fixed-point exchange x ← F(x), for the wall-temperature
/// handoff of the conjugate study. Given the iterate x and its residual f = F(x) − x it
/// proposes the next iterate from the last few pairs: the combination of past residuals
/// that a LINEAR map would cancel (least squares over the stored differences), which for
/// a linear exchange is the secant/quasi-Newton step. A plain or under-relaxed handoff
/// contracts at 1 − ½·h_eff/g per pass — a few percent when the grid resolves the
/// boundary layer — so it either stops early or runs out of iterations; this reaches the
/// same fixed point in a handful of passes however slow the plain map is.
/// <para>
/// The size of the proposed step is also the honest convergence measure: for a slowly
/// contracting map the residual is far smaller than the remaining error, while the
/// quasi-Newton step estimates the error itself.
/// </para>
/// </summary>
internal sealed class AndersonMixer
{
    private readonly int _depth;
    private readonly double _beta;
    private readonly List<double[]> _dx = new();
    private readonly List<double[]> _df = new();
    private double[]? _lastX;
    private double[]? _lastF;

    /// <param name="depth">How many past differences are kept (≥ 1).</param>
    /// <param name="beta">Mixing of the plain step, in (0, 1]: the very first pass, with
    /// no history, moves by β·f.</param>
    public AndersonMixer(int depth, double beta)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
        if (!(beta > 0 && beta <= 1)) throw new ArgumentOutOfRangeException(nameof(beta));
        _depth = depth;
        _beta = beta;
    }

    /// <summary>Forgets the history (a new fixed-point problem starts).</summary>
    public void Reset()
    {
        _dx.Clear();
        _df.Clear();
        _lastX = null;
        _lastF = null;
    }

    /// <summary>The next iterate, from the current one and its residual F(x) − x.
    /// Neither argument is modified or kept.</summary>
    public double[] Next(IReadOnlyList<double> x, IReadOnlyList<double> f)
    {
        int n = x.Count;
        if (_lastX is not null)
        {
            var dx = new double[n];
            var df = new double[n];
            for (int i = 0; i < n; i++)
            {
                dx[i] = x[i] - _lastX[i];
                df[i] = f[i] - _lastF![i];
            }
            _dx.Add(dx);
            _df.Add(df);
            if (_dx.Count > _depth)
            {
                _dx.RemoveAt(0);
                _df.RemoveAt(0);
            }
        }
        _lastX = x.ToArray();
        _lastF = f.ToArray();

        var next = new double[n];
        for (int i = 0; i < n; i++) next[i] = x[i] + _beta * f[i];

        int m = _df.Count;
        if (m == 0) return next;

        // Normal equations of min ‖f − ΔF·γ‖₂ (m ≤ depth, a handful), with a relative
        // ridge so two nearly parallel differences cannot produce a wild γ.
        var a = new double[m, m];
        var b = new double[m];
        double trace = 0;
        for (int p = 0; p < m; p++)
        {
            for (int q = p; q < m; q++)
            {
                double dot = 0;
                var dp = _df[p];
                var dq = _df[q];
                for (int i = 0; i < n; i++) dot += dp[i] * dq[i];
                a[p, q] = a[q, p] = dot;
            }
            double rhs = 0;
            for (int i = 0; i < n; i++) rhs += _df[p][i] * f[i];
            b[p] = rhs;
            trace += a[p, p];
        }
        if (!(trace > 0)) return next;
        for (int p = 0; p < m; p++) a[p, p] += 1e-10 * trace;

        var gamma = SolveSymmetric(a, b);
        if (gamma is null) return next;

        var accelerated = (double[])next.Clone();
        for (int p = 0; p < m; p++)
        {
            double g = gamma[p];
            var dx = _dx[p];
            var df = _df[p];
            for (int i = 0; i < n; i++) accelerated[i] -= g * (dx[i] + _beta * df[i]);
        }
        foreach (double v in accelerated)
            if (!double.IsFinite(v)) return next;
        return accelerated;
    }

    /// <summary>Gaussian elimination with partial pivoting on a tiny dense system; null
    /// when it is singular to working precision.</summary>
    private static double[]? SolveSymmetric(double[,] a, double[] b)
    {
        int m = b.Length;
        var x = (double[])b.Clone();
        var w = (double[,])a.Clone();
        for (int col = 0; col < m; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < m; r++)
                if (Math.Abs(w[r, col]) > Math.Abs(w[pivot, col])) pivot = r;
            if (!(Math.Abs(w[pivot, col]) > 0)) return null;
            if (pivot != col)
            {
                for (int c = 0; c < m; c++)
                    (w[col, c], w[pivot, c]) = (w[pivot, c], w[col, c]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (int r = col + 1; r < m; r++)
            {
                double factor = w[r, col] / w[col, col];
                if (factor == 0) continue;
                for (int c = col; c < m; c++) w[r, c] -= factor * w[col, c];
                x[r] -= factor * x[col];
            }
        }
        for (int r = m - 1; r >= 0; r--)
        {
            double sum = x[r];
            for (int c = r + 1; c < m; c++) sum -= w[r, c] * x[c];
            x[r] = sum / w[r, r];
        }
        return x;
    }
}
