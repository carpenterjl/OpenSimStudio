namespace OpenSim.Core.Numerics.Optimization;

/// <summary>
/// One-dimensional minimisation by Brent's method: golden-section steps, with parabolic
/// interpolation whenever it is safe, so it converges superlinearly on smooth functions and never
/// slower than golden section. Used by <see cref="PowellMinimizer"/> for its line searches, and
/// directly for one-variable problems (best focus, say).
/// <para>
/// Reference: R. P. Brent, Algorithms for Minimization without Derivatives (1973), chapter 5,
/// procedure localmin.
/// </para>
/// </summary>
public static class BrentMinimizer
{
    private const double GoldenSection = 0.3819660112501051; // (3 − √5)/2
    private const double Growth = 1.618033988749895; // (1 + √5)/2

    /// <summary>A local minimum of <paramref name="f"/> in [<paramref name="a"/>, <paramref name="b"/>],
    /// located to |x|·<paramref name="relativeTolerance"/> + <paramref name="absoluteTolerance"/>.
    /// The relative tolerance should not be below about √ε, since the function's value cannot
    /// resolve the position more finely.</summary>
    public static (double X, double Value) Minimize(Func<double, double> f, double a, double b,
        double relativeTolerance = 1.4901161193847656e-8, double absoluteTolerance = 1e-12, int maxIterations = 500)
    {
        if (!(a < b)) throw new ArgumentException("The interval must have a < b.");
        double x = a + GoldenSection * (b - a);
        return Minimize(f, a, b, x, f(x), relativeTolerance, absoluteTolerance, maxIterations);
    }

    /// <summary>Brent's localmin on [a, b] from the point x with value fx.</summary>
    internal static (double X, double Value) Minimize(Func<double, double> f, double a, double b, double x, double fx,
        double relativeTolerance, double absoluteTolerance, int maxIterations)
    {
        double v = x, w = x, fv = fx, fw = fx, d = 0, e = 0;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            double middle = 0.5 * (a + b);
            double tolerance = relativeTolerance * Math.Abs(x) + absoluteTolerance, twice = 2 * tolerance;
            if (Math.Abs(x - middle) <= twice - 0.5 * (b - a)) break;
            bool golden = true;
            if (Math.Abs(e) > tolerance)
            {
                // A parabola through x, w and v.
                double r = (x - w) * (fx - fv);
                double q = (x - v) * (fx - fw);
                double p = (x - v) * q - (x - w) * r;
                q = 2 * (q - r);
                if (q > 0) p = -p;
                else q = -q;
                double previous = e;
                e = d;
                if (Math.Abs(p) < Math.Abs(0.5 * q * previous) && p > q * (a - x) && p < q * (b - x))
                {
                    d = p / q;
                    double u0 = x + d;
                    if (u0 - a < twice || b - u0 < twice) d = x < middle ? tolerance : -tolerance;
                    golden = false;
                }
            }
            if (golden)
            {
                e = (x < middle ? b : a) - x;
                d = GoldenSection * e;
            }
            double u = Math.Abs(d) >= tolerance ? x + d : x + (d > 0 ? tolerance : -tolerance);
            double fu = f(u);
            if (fu <= fx)
            {
                if (u < x) b = x;
                else a = x;
                (v, fv, w, fw, x, fx) = (w, fw, x, fx, u, fu);
            }
            else
            {
                if (u < x) a = u;
                else b = u;
                if (fu <= fw || w == x) (v, fv, w, fw) = (w, fw, u, fu);
                else if (fu <= fv || v == x || v == w) (v, fv) = (u, fu);
            }
        }
        return (x, fx);
    }

    /// <summary>
    /// The line search: from t = 0 (value <paramref name="f0"/>), steps of 1 in either direction,
    /// widened by the golden ratio while the function keeps falling, then Brent on the bracket.
    /// The search stays in [<paramref name="lo"/>, <paramref name="hi"/>], which must contain 0.
    /// </summary>
    internal static (double T, double Value) Search(Func<double, double> f, double lo, double hi, double f0)
    {
        const double relative = 1.4901161193847656e-8, absolute = 1e-11;
        double up = Math.Min(1, hi), down = Math.Max(-1, lo);
        double fUp = up != 0 ? f(up) : double.PositiveInfinity;
        if (fUp < f0) return Expand(f, 0, up, fUp, lo, hi, relative, absolute);
        double fDown = down != 0 ? f(down) : double.PositiveInfinity;
        if (fDown < f0) return Expand(f, 0, down, fDown, lo, hi, relative, absolute);
        if (up == down) return (0, f0);
        return Minimize(f, down, up, 0, f0, relative, absolute, 500);
    }

    // OP-60: the line search for one-variable callers outside this assembly (Quick Focus, Quick Adjust).
    /// <summary>
    /// <see cref="Search"/> for callers outside this assembly: the minimum of <paramref name="f"/>
    /// along t from t = 0 (value <paramref name="f0"/>), with unit first steps either way, the
    /// bracket widened by the golden ratio while the function keeps falling, then Brent's
    /// localmin on the bracket to |t|·1.49e-8 + 1e-11 (<see cref="LineSearchRelativeTolerance"/>,
    /// <see cref="LineSearchAbsoluteTolerance"/>). The caller scales its variable so that one unit
    /// of t is a sensible first step. A value of +∞ counts as higher than any finite value, so a
    /// caller can mark a point it cannot evaluate. t stays in [<paramref name="lo"/>,
    /// <paramref name="hi"/>], which must contain 0.
    /// </summary>
    public static (double T, double Value) LineSearch(Func<double, double> f, double lo, double hi, double f0)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!(lo <= 0 && hi >= 0)) throw new ArgumentException($"The line search range [{lo}, {hi}] must contain 0.");
        return Search(f, lo, hi, f0);
    }

    /// <summary>The relative stopping tolerance of <see cref="LineSearch"/> in t (√ε of a double).</summary>
    public const double LineSearchRelativeTolerance = 1.4901161193847656e-8;

    /// <summary>The absolute stopping tolerance of <see cref="LineSearch"/> in t.</summary>
    public const double LineSearchAbsoluteTolerance = 1e-11;

    private static (double, double) Expand(Func<double, double> f, double previous, double current, double fCurrent,
        double lo, double hi, double relative, double absolute)
    {
        for (int k = 0; k < 200; k++)
        {
            double next = Math.Clamp(current + Growth * (current - previous), lo, hi);
            if (next == current) return (current, fCurrent);
            double fNext = f(next);
            if (fNext >= fCurrent)
                return Minimize(f, Math.Min(previous, next), Math.Max(previous, next), current, fCurrent, relative, absolute, 500);
            (previous, current, fCurrent) = (current, next, fNext);
        }
        return (current, fCurrent);
    }
}
