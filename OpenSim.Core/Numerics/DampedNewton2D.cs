namespace OpenSim.Core.Numerics;

/// <summary>
/// A residual of two unknowns: returns false where it is not defined (a ray that cannot be
/// traced, say), and otherwise the two components of F(x, y).
/// </summary>
public delegate bool Residual2D(double x, double y, out double fx, out double fy);

/// <summary>The outcome of <see cref="DampedNewton2D.Solve"/>: the best point found, its residual
/// norm, the iterations taken, and, when it did not converge, why.</summary>
public readonly record struct Newton2DResult(bool Converged, double X, double Y, double ResidualNorm, int Iterations, string? Failure);

/// <summary>
/// Damped Newton iteration for two equations in two unknowns, F(x, y) = 0, with a forward
/// finite-difference Jacobian and a backtracking line search on the residual norm. Derived
/// here, not copied: the Newton step δ solves J·δ = −F (2 × 2, by Cramer's rule); the step taken
/// is α·δ with α = 1, ½, ¼, … the first that keeps F defined and lowers ‖F‖ by the Armijo
/// fraction 10⁻⁴·α (for the norm, the directional derivative along δ is −‖F‖, so this is the
/// usual sufficient-decrease test). Damping keeps a start far from the root, or a residual that
/// is undefined beyond some boundary (a ray that misses a surface), from jumping out of the
/// region where the root lives.
/// <para>
/// Convergence is ‖F‖ ≤ <c>tolerance</c>. When no step lowers ‖F‖ any further (the rounding
/// floor), the point is accepted if ‖F‖ ≤ <c>acceptTolerance</c> and refused otherwise. Every
/// evaluation is a pure function of its inputs, so the result is bitwise reproducible.
/// </para>
/// </summary>
public static class DampedNewton2D
{
    /// <param name="f">The residual.</param>
    /// <param name="x0">Start, x.</param>
    /// <param name="y0">Start, y.</param>
    /// <param name="step">The finite-difference step, in the unknowns' units (positive). When
    /// the residual is undefined a step forward, the step backward is used.</param>
    /// <param name="tolerance">Target residual norm.</param>
    /// <param name="acceptTolerance">Residual norm still accepted when the iteration stalls at
    /// rounding (at least <paramref name="tolerance"/>).</param>
    /// <param name="maxIterations">Newton steps at most.</param>
    /// <param name="maxHalvings">Line-search halvings at most per step.</param>
    public static Newton2DResult Solve(Residual2D f, double x0, double y0, double step, double tolerance,
        double acceptTolerance, int maxIterations = 60, int maxHalvings = 40)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!(step > 0) || !double.IsFinite(step)) throw new ArgumentOutOfRangeException(nameof(step), step, "The difference step must be positive and finite.");
        if (!(tolerance >= 0)) throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be zero or positive.");
        acceptTolerance = Math.Max(acceptTolerance, tolerance);

        double x = x0, y = y0;
        if (!Evaluate(f, x, y, out double fx, out double fy))
            return new Newton2DResult(false, x, y, double.NaN, 0, "the residual is not defined at the start");
        double norm = Norm(fx, fy);
        for (int iteration = 0; ; iteration++)
        {
            if (norm <= tolerance) return new Newton2DResult(true, x, y, norm, iteration, null);
            if (iteration >= maxIterations)
                return Stalled(x, y, norm, iteration, acceptTolerance, $"no convergence in {maxIterations} Newton steps");

            // Jacobian by forward (or, where undefined, backward) differences.
            if (!Column(f, x, y, step, 0, fx, fy, out double a11, out double a21)
                || !Column(f, x, y, step, 1, fx, fy, out double a12, out double a22))
                return Stalled(x, y, norm, iteration, acceptTolerance, "the residual is not defined on either side of the point, so it has no Jacobian there");
            double det = a11 * a22 - a12 * a21;
            if (!(Math.Abs(det) > 0) || !double.IsFinite(det))
                return Stalled(x, y, norm, iteration, acceptTolerance, "the Jacobian is singular");
            double dx = -(a22 * fx - a12 * fy) / det;
            double dy = -(-a21 * fx + a11 * fy) / det;

            double alpha = 1;
            bool moved = false;
            for (int h = 0; h <= maxHalvings; h++, alpha *= 0.5)
            {
                double xt = x + alpha * dx, yt = y + alpha * dy;
                if (xt == x && yt == y) break;                       // the step is below one ulp
                if (!Evaluate(f, xt, yt, out double gx, out double gy)) continue;
                double trial = Norm(gx, gy);
                if (trial <= (1 - 1e-4 * alpha) * norm)
                {
                    x = xt; y = yt; fx = gx; fy = gy; norm = trial;
                    moved = true;
                    break;
                }
            }
            if (!moved) return Stalled(x, y, norm, iteration, acceptTolerance, "no step along the Newton direction lowers the residual");
        }
    }

    private static Newton2DResult Stalled(double x, double y, double norm, int iterations, double accept, string why) =>
        norm <= accept
            ? new Newton2DResult(true, x, y, norm, iterations, null)
            : new Newton2DResult(false, x, y, norm, iterations, why);

    private static bool Column(Residual2D f, double x, double y, double step, int which, double fx, double fy,
        out double c1, out double c2)
    {
        for (int sign = 1; sign >= -1; sign -= 2)
        {
            double h = sign * step;
            double xs = which == 0 ? x + h : x, ys = which == 1 ? y + h : y;
            double actual = which == 0 ? xs - x : ys - y;            // the step as represented
            if (actual != 0 && Evaluate(f, xs, ys, out double gx, out double gy))
            {
                c1 = (gx - fx) / actual;
                c2 = (gy - fy) / actual;
                return true;
            }
        }
        c1 = c2 = double.NaN;
        return false;
    }

    private static bool Evaluate(Residual2D f, double x, double y, out double fx, out double fy) =>
        f(x, y, out fx, out fy) && double.IsFinite(fx) && double.IsFinite(fy);

    private static double Norm(double a, double b) => Math.Sqrt(a * a + b * b);
}
