namespace OpenSim.Rf;

/// <summary>
/// Gauss–Legendre quadrature rules on [−1, 1], computed by Newton iteration on the
/// Legendre recurrence (machine precision, deterministic). Shared by the MoM matrix
/// assembly and the far-field sphere integration.
/// </summary>
public static class GaussLegendre
{
    public static (double[] Nodes, double[] Weights) Rule(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        var nodes = new double[n];
        var weights = new double[n];
        int half = (n + 1) / 2;
        for (int i = 0; i < half; i++)
        {
            // Chebyshev-like initial guess, then Newton on P_n(x) = 0.
            double x = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
            double derivative = 0;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double p0 = 1, p1 = x;
                for (int k = 2; k <= n; k++)
                {
                    double p2 = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
                    p0 = p1;
                    p1 = p2;
                }
                derivative = n * (x * p1 - p0) / (x * x - 1);
                double step = p1 / derivative;
                x -= step;
                if (Math.Abs(step) < 1e-15) break;
            }
            nodes[i] = -x;
            nodes[n - 1 - i] = x;
            double w = 2.0 / ((1 - x * x) * derivative * derivative);
            weights[i] = w;
            weights[n - 1 - i] = w;
        }
        return (nodes, weights);
    }

    /// <summary>The rule mapped to [a, b]: node t → (a+b)/2 + (b−a)/2·t, weight scaled.</summary>
    public static (double[] Nodes, double[] Weights) Rule(int n, double a, double b)
    {
        var (nodes, weights) = Rule(n);
        double mid = 0.5 * (a + b), halfSpan = 0.5 * (b - a);
        for (int i = 0; i < n; i++)
        {
            nodes[i] = mid + halfSpan * nodes[i];
            weights[i] *= halfSpan;
        }
        return (nodes, weights);
    }
}
