namespace OpenSim.Rf.Layered;

/// <summary>First-party natural cubic spline (zero second derivative at the ends) on a
/// strictly increasing abscissa — the interpolant behind the layered kernel table.
/// Construction is the classical symmetric tridiagonal solve for the knot second
/// derivatives; evaluation is a binary search plus the two-sided Hermite form. Both
/// are deterministic to the bit for identical inputs.</summary>
internal sealed class NaturalCubicSpline
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _y2;

    public NaturalCubicSpline(double[] x, double[] y)
    {
        if (x.Length != y.Length) throw new ArgumentException("x/y length mismatch.");
        if (x.Length < 3) throw new ArgumentException("A cubic spline needs at least 3 knots.");
        for (int i = 1; i < x.Length; i++)
            if (x[i] <= x[i - 1])
                throw new ArgumentException("Spline abscissa must be strictly increasing.");
        _x = x;
        _y = y;

        int n = x.Length;
        _y2 = new double[n];
        var u = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            double sig = (x[i] - x[i - 1]) / (x[i + 1] - x[i - 1]);
            double pInv = 1 / (sig * _y2[i - 1] + 2);
            _y2[i] = (sig - 1) * pInv;
            double slopeDelta = (y[i + 1] - y[i]) / (x[i + 1] - x[i])
                                - (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
            u[i] = (6 * slopeDelta / (x[i + 1] - x[i - 1]) - sig * u[i - 1]) * pInv;
        }
        for (int i = n - 2; i >= 1; i--)
            _y2[i] = _y2[i] * _y2[i + 1] + u[i];
    }

    public double Evaluate(double x)
    {
        if (x < _x[0] || x > _x[^1])
            throw new ArgumentOutOfRangeException(nameof(x),
                $"Spline evaluated outside its knots [{_x[0]:g6}, {_x[^1]:g6}] at {x:g6}.");
        int lo = 0, hi = _x.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_x[mid] > x) hi = mid;
            else lo = mid;
        }
        double h = _x[hi] - _x[lo];
        double a = (_x[hi] - x) / h, b = (x - _x[lo]) / h;
        return a * _y[lo] + b * _y[hi]
               + ((a * a * a - a) * _y2[lo] + (b * b * b - b) * _y2[hi]) * (h * h) / 6;
    }
}
