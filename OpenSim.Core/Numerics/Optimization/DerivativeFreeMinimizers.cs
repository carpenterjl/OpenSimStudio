namespace OpenSim.Core.Numerics.Optimization;

/// <summary>Why a derivative-free minimiser stopped.</summary>
public enum MinimizerStatus
{
    Converged,
    EvaluationLimit
}

/// <summary>Settings for <see cref="NelderMead"/> and <see cref="PowellMinimizer"/>.</summary>
public sealed class MinimizerOptions
{
    /// <summary>Lower bound per variable; null for none.</summary>
    public double[]? LowerBounds { get; init; }

    /// <summary>Upper bound per variable; null for none.</summary>
    public double[]? UpperBounds { get; init; }

    /// <summary>The initial step per variable: the simplex edge, or the length of Powell's initial
    /// direction. Null for 5 % of |xⱼ| (0.00025 where xⱼ = 0).</summary>
    public double[]? InitialSteps { get; init; }

    /// <summary>Relative tolerance on the function value.</summary>
    public double ValueTolerance { get; init; } = 1e-14;

    /// <summary>Tolerance on the position, relative to |xⱼ| + the initial step (Nelder–Mead only;
    /// Powell's line searches resolve the position to about √ε).</summary>
    public double StepTolerance { get; init; } = 1e-10;

    public int MaxEvaluations { get; init; } = 100_000;

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>The outcome of a derivative-free minimisation.</summary>
/// <param name="FailedEvaluations">Evaluations that threw or returned NaN.</param>
public sealed record MinimizerResult(double[] X, double Value, MinimizerStatus Status, int Iterations,
    int Evaluations, int FailedEvaluations);

/// <summary>Counts evaluations and turns a failure (an exception or NaN) into +∞, so a minimiser
/// simply moves away from where the merit function cannot be evaluated.</summary>
internal sealed class PenalisedFunction
{
    private readonly Func<double[], double> _function;
    private readonly double[] _lower, _upper;
    private readonly CancellationToken _token;

    public PenalisedFunction(Func<double[], double> function, double[] lower, double[] upper, CancellationToken token)
    {
        _function = function;
        _lower = lower;
        _upper = upper;
        _token = token;
    }

    public int Evaluations { get; private set; }
    public int Failures { get; private set; }

    /// <summary>The value at the point clamped into the bounds; the clamped point replaces the
    /// argument's contents.</summary>
    public double Evaluate(double[] x)
    {
        _token.ThrowIfCancellationRequested();
        for (int j = 0; j < x.Length; j++) x[j] = Math.Clamp(x[j], _lower[j], _upper[j]);
        Evaluations++;
        double value;
        try
        {
            value = _function((double[])x.Clone());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failures++;
            return double.PositiveInfinity;
        }
        if (double.IsNaN(value))
        {
            Failures++;
            return double.PositiveInfinity;
        }
        return value;
    }

    public static double[] DefaultSteps(double[] x, double[]? steps)
    {
        if (steps is not null)
        {
            if (steps.Length != x.Length) throw new ArgumentException("One initial step per variable.");
            foreach (double s in steps)
                if (!(s > 0) || double.IsInfinity(s)) throw new ArgumentException("Initial steps must be positive and finite.");
            return (double[])steps.Clone();
        }
        var result = new double[x.Length];
        for (int j = 0; j < x.Length; j++) result[j] = x[j] != 0 ? 0.05 * Math.Abs(x[j]) : 0.00025;
        return result;
    }
}

/// <summary>
/// The Nelder–Mead simplex method with the dimension-dependent coefficients of Gao and Han
/// (reflection 1, expansion 1 + 2/n, contraction 3/4 − 1/(2n), shrink 1 − 1/n), which keep the
/// method effective beyond a handful of variables. It needs only function values and tolerates
/// noise, so it suits merit functions that are not smooth. Points outside the bounds are clamped
/// onto them. On convergence the simplex is rebuilt around the best point and the search resumed,
/// which guards against the method's known false convergence; it stops when a restart gains
/// nothing. Sequential and deterministic.
/// <para>
/// References: J. A. Nelder and R. Mead, Computer Journal 7, 308 (1965); F. Gao and L. Han,
/// "Implementing the Nelder–Mead simplex algorithm with adaptive parameters", Computational
/// Optimization and Applications 51, 259 (2012).
/// </para>
/// </summary>
public static class NelderMead
{
    private const int MaxRestarts = 5;

    /// <exception cref="InvalidOperationException">The function cannot be evaluated at the start.</exception>
    public static MinimizerResult Minimize(Func<double[], double> function, double[] start, MinimizerOptions? options = null)
    {
        options ??= new MinimizerOptions();
        int n = start.Length;
        if (n == 0) throw new ArgumentException("There must be at least one variable.", nameof(start));
        var (lower, upper) = Bounds.Resolve(options.LowerBounds, options.UpperBounds, n);
        var steps = PenalisedFunction.DefaultSteps(start, options.InitialSteps);
        var f = new PenalisedFunction(function, lower, upper, options.CancellationToken);

        var best = (double[])start.Clone();
        double bestValue = f.Evaluate(best);
        if (double.IsPositiveInfinity(bestValue))
            throw new InvalidOperationException("The function cannot be evaluated at the starting point.");

        double alpha = 1, beta = 1 + 2.0 / n, gamma = 0.75 - 0.5 / n, delta = 1 - 1.0 / n;
        if (n == 1) { beta = 2; gamma = 0.5; delta = 0.5; }
        int iterations = 0;
        var status = MinimizerStatus.Converged;
        for (int restart = 0; restart <= MaxRestarts; restart++)
        {
            // The simplex: the best point and one step along each axis (inward at an upper bound).
            var points = new double[n + 1][];
            var values = new double[n + 1];
            points[0] = (double[])best.Clone();
            values[0] = bestValue;
            for (int j = 0; j < n; j++)
            {
                var p = (double[])best.Clone();
                p[j] = best[j] + steps[j] <= upper[j] ? best[j] + steps[j] : best[j] - steps[j];
                points[j + 1] = p;
                values[j + 1] = f.Evaluate(p);
            }
            double startValue = bestValue;

            while (true)
            {
                Order(points, values);
                if (Converged(points, values, steps, options)) break;
                if (f.Evaluations >= options.MaxEvaluations) { status = MinimizerStatus.EvaluationLimit; break; }
                iterations++;

                var centroid = new double[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) centroid[j] += points[i][j] / n;
                var worst = points[n];
                double worstValue = values[n];

                var reflected = Along(centroid, worst, -alpha);
                double reflectedValue = f.Evaluate(reflected);
                if (reflectedValue < values[0])
                {
                    var expanded = Along(centroid, worst, -alpha * beta);
                    double expandedValue = f.Evaluate(expanded);
                    if (expandedValue < reflectedValue) Replace(points, values, expanded, expandedValue);
                    else Replace(points, values, reflected, reflectedValue);
                    continue;
                }
                if (reflectedValue < values[n - 1])
                {
                    Replace(points, values, reflected, reflectedValue);
                    continue;
                }
                if (reflectedValue < worstValue)
                {
                    var outside = Along(centroid, worst, -alpha * gamma);
                    double outsideValue = f.Evaluate(outside);
                    if (outsideValue <= reflectedValue)
                    {
                        Replace(points, values, outside, outsideValue);
                        continue;
                    }
                }
                else
                {
                    var inside = Along(centroid, worst, gamma);
                    double insideValue = f.Evaluate(inside);
                    if (insideValue < worstValue)
                    {
                        Replace(points, values, inside, insideValue);
                        continue;
                    }
                }
                // Shrink towards the best point.
                for (int i = 1; i <= n; i++)
                {
                    for (int j = 0; j < n; j++) points[i][j] = points[0][j] + delta * (points[i][j] - points[0][j]);
                    values[i] = f.Evaluate(points[i]);
                }
            }
            Order(points, values);
            best = points[0];
            bestValue = values[0];
            if (status == MinimizerStatus.EvaluationLimit) break;
            // A restart that gains nothing (to the value tolerance) confirms the minimum.
            if (restart > 0 && startValue - bestValue <= options.ValueTolerance * Math.Abs(bestValue)) break;
        }
        return new MinimizerResult(best, bestValue, status, iterations, f.Evaluations, f.Failures);
    }

    /// <summary>centroid + t·(point − centroid).</summary>
    private static double[] Along(double[] centroid, double[] point, double t)
    {
        var result = new double[centroid.Length];
        for (int j = 0; j < result.Length; j++) result[j] = centroid[j] + t * (point[j] - centroid[j]);
        return result;
    }

    private static void Replace(double[][] points, double[] values, double[] point, double value)
    {
        points[^1] = point;
        values[^1] = value;
    }

    /// <summary>Stable sort by value, so ties keep their order and runs are reproducible.</summary>
    private static void Order(double[][] points, double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var p = order.Select(i => points[i]).ToArray();
        var v = order.Select(i => values[i]).ToArray();
        Array.Copy(p, points, p.Length);
        Array.Copy(v, values, v.Length);
    }

    /// <summary>The simplex has collapsed to the step tolerance, or its values agree to the value
    /// tolerance. Either can stop a run early on a hard problem; the restarts catch that.</summary>
    private static bool Converged(double[][] points, double[] values, double[] steps, MinimizerOptions options)
    {
        if (double.IsPositiveInfinity(values[^1])) return false;
        double spread = values[^1] - values[0];
        if (values[0] != 0 && spread <= options.ValueTolerance * Math.Abs(values[0])) return true;
        var best = points[0];
        for (int i = 1; i < points.Length; i++)
            for (int j = 0; j < best.Length; j++)
                if (Math.Abs(points[i][j] - best[j]) > options.StepTolerance * (Math.Abs(best[j]) + steps[j]))
                    return false;
        return true;
    }
}

/// <summary>
/// Powell's conjugate-direction method: line minimisations along a set of directions, with the
/// overall displacement of each sweep replacing the direction of largest decrease when Powell's
/// test says this keeps the set from becoming linearly dependent. It needs only function values
/// and converges quadratically on smooth problems. Line searches stay inside the bounds.
/// Sequential and deterministic.
/// <para>
/// References: M. J. D. Powell, Computer Journal 7, 155 (1964); W. H. Press et al., Numerical
/// Recipes, 3rd ed., section 10.7, for the direction-replacement test.
/// </para>
/// </summary>
public static class PowellMinimizer
{
    /// <exception cref="InvalidOperationException">The function cannot be evaluated at the start.</exception>
    public static MinimizerResult Minimize(Func<double[], double> function, double[] start, MinimizerOptions? options = null)
    {
        options ??= new MinimizerOptions();
        int n = start.Length;
        if (n == 0) throw new ArgumentException("There must be at least one variable.", nameof(start));
        var (lower, upper) = Bounds.Resolve(options.LowerBounds, options.UpperBounds, n);
        var steps = PenalisedFunction.DefaultSteps(start, options.InitialSteps);
        var f = new PenalisedFunction(function, lower, upper, options.CancellationToken);

        var x = (double[])start.Clone();
        double value = f.Evaluate(x);
        if (double.IsPositiveInfinity(value))
            throw new InvalidOperationException("The function cannot be evaluated at the starting point.");

        var directions = new double[n][];
        for (int j = 0; j < n; j++)
        {
            directions[j] = new double[n];
            directions[j][j] = steps[j];
        }

        int iterations = 0;
        var status = MinimizerStatus.Converged;
        while (true)
        {
            if (f.Evaluations >= options.MaxEvaluations) { status = MinimizerStatus.EvaluationLimit; break; }
            iterations++;
            var sweepStart = (double[])x.Clone();
            double startValue = value;
            int largest = 0;
            double largestDecrease = 0;
            for (int i = 0; i < n; i++)
            {
                double before = value;
                (x, value) = LineMinimize(f, x, value, directions[i], lower, upper);
                if (before - value > largestDecrease)
                {
                    largestDecrease = before - value;
                    largest = i;
                }
            }
            if (2 * (startValue - value) <= options.ValueTolerance * (Math.Abs(startValue) + Math.Abs(value)) + 1e-300)
                break;

            var displacement = new double[n];
            var extrapolated = new double[n];
            for (int j = 0; j < n; j++)
            {
                displacement[j] = x[j] - sweepStart[j];
                extrapolated[j] = 2 * x[j] - sweepStart[j];
            }
            double extrapolatedValue = f.Evaluate(extrapolated);
            if (extrapolatedValue < startValue)
            {
                double a = startValue - value - largestDecrease, b = startValue - extrapolatedValue;
                double test = 2 * (startValue - 2 * value + extrapolatedValue) * a * a - largestDecrease * b * b;
                if (test < 0)
                {
                    (x, value) = LineMinimize(f, x, value, displacement, lower, upper);
                    directions[largest] = directions[n - 1];
                    directions[n - 1] = displacement;
                }
            }
        }
        return new MinimizerResult(x, value, status, iterations, f.Evaluations, f.Failures);
    }

    /// <summary>The minimum of f(x + t·d) over the t that keep the point within the bounds.</summary>
    private static (double[] X, double Value) LineMinimize(PenalisedFunction f, double[] x, double value,
        double[] direction, double[] lower, double[] upper)
    {
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        bool moves = false;
        for (int j = 0; j < x.Length; j++)
        {
            if (direction[j] == 0) continue;
            moves = true;
            double a = (lower[j] - x[j]) / direction[j], b = (upper[j] - x[j]) / direction[j];
            lo = Math.Max(lo, Math.Min(a, b));
            hi = Math.Min(hi, Math.Max(a, b));
        }
        if (!moves || hi <= lo) return (x, value);
        lo = Math.Min(lo, 0);
        hi = Math.Max(hi, 0);

        double Along(double t)
        {
            var point = new double[x.Length];
            for (int j = 0; j < x.Length; j++) point[j] = x[j] + t * direction[j];
            return f.Evaluate(point);
        }
        var (best, bestValue) = BrentMinimizer.Search(Along, lo, hi, value);
        if (!(bestValue < value)) return (x, value);
        var result = new double[x.Length];
        for (int j = 0; j < x.Length; j++) result[j] = Math.Clamp(x[j] + best * direction[j], lower[j], upper[j]);
        return (result, bestValue);
    }
}
