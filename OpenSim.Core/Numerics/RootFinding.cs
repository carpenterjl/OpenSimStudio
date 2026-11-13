namespace OpenSim.Core.Numerics;

/// <summary>
/// Bracketing root finders. Brent's method (R. P. Brent, "Algorithms for Minimization without
/// Derivatives", Prentice-Hall 1973, chapter 4) combines bisection, the secant step and inverse
/// quadratic interpolation: it never leaves the bracket, so it always converges, and it converges
/// superlinearly on a smooth function.
/// </summary>
public static class RootFinding
{
    /// <summary>
    /// Finds a root of <paramref name="f"/> between <paramref name="a"/> and <paramref name="b"/>,
    /// where f(a) and f(b) must have opposite signs (or one of them is zero).
    /// </summary>
    /// <param name="tolerance">Absolute width of the final bracket. Zero asks for the root to
    /// the last bit the function allows; the limit is then 4·ε·|root|.</param>
    /// <exception cref="ArgumentException">The interval does not bracket a root, or f is not a
    /// number at an end.</exception>
    /// <exception cref="InvalidOperationException">No convergence in
    /// <paramref name="maxIterations"/> steps.</exception>
    public static double Brent(Func<double, double> f, double a, double b,
        double tolerance = 0, int maxIterations = 200)
    {
        if (!TryBrent(f, a, b, out double root, tolerance, maxIterations))
            throw new InvalidOperationException(
                $"Brent's method did not converge in {maxIterations} iterations on [{a:g6}, {b:g6}].");
        return root;
    }

    /// <summary>As <see cref="Brent"/>, but reports failure to converge by returning false;
    /// <paramref name="root"/> is then the best estimate so far.</summary>
    public static bool TryBrent(Func<double, double> f, double a, double b, out double root,
        double tolerance = 0, int maxIterations = 200)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (tolerance < 0 || double.IsNaN(tolerance)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        double fa = f(a), fb = f(b);
        if (double.IsNaN(fa) || double.IsNaN(fb))
            throw new ArgumentException("The function is not a number at an end of the interval.");
        if (fa == 0) { root = a; return true; }
        if (fb == 0) { root = b; return true; }
        if ((fa > 0) == (fb > 0))
            throw new ArgumentException(
                $"The interval [{a:g6}, {b:g6}] does not bracket a root: f = {fa:g6} and {fb:g6}.");

        // b is the best estimate, a the previous one, and c the end that keeps the bracket with b.
        double c = b, fc = fb, d = 0, e = 0;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            if ((fb > 0) == (fc > 0))
            {
                c = a; fc = fa;
                d = b - a; e = d;
            }
            if (Math.Abs(fc) < Math.Abs(fb))
            {
                a = b; b = c; c = a;
                fa = fb; fb = fc; fc = fa;
            }
            double tol = 2 * double.Epsilon + 2 * MachineEpsilon * Math.Abs(b) + 0.5 * tolerance;
            double m = 0.5 * (c - b);
            if (Math.Abs(m) <= tol || fb == 0) { root = b; return true; }

            if (Math.Abs(e) >= tol && Math.Abs(fa) > Math.Abs(fb))
            {
                double s = fb / fa, p, q;
                if (a == c)
                {
                    p = 2 * m * s;                              // secant
                    q = 1 - s;
                }
                else
                {
                    double r1 = fa / fc, r2 = fb / fc;          // inverse quadratic
                    p = s * (2 * m * r1 * (r1 - r2) - (b - a) * (r2 - 1));
                    q = (r1 - 1) * (r2 - 1) * (s - 1);
                }
                if (p > 0) q = -q; else p = -p;
                if (2 * p < Math.Min(3 * m * q - Math.Abs(tol * q), Math.Abs(e * q)))
                {
                    e = d; d = p / q;                           // the interpolated step is accepted
                }
                else
                {
                    d = m; e = m;                               // bisection
                }
            }
            else
            {
                d = m; e = m;
            }
            a = b; fa = fb;
            b += Math.Abs(d) > tol ? d : (m > 0 ? tol : -tol);
            fb = f(b);
            if (double.IsNaN(fb))
                throw new ArgumentException($"The function is not a number at {b:R}.");
        }
        root = b;
        return false;
    }

    private const double MachineEpsilon = 2.220446049250313e-16;
}
