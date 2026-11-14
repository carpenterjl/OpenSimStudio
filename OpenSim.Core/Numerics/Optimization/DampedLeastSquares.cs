namespace OpenSim.Core.Numerics.Optimization;

/// <summary>Fills <paramref name="residuals"/> with the residuals at <paramref name="x"/>. It may throw, or
/// return a non-finite value, where the model cannot be evaluated; the solver treats that point as
/// infinitely bad. With more than one thread it is called concurrently and must be thread-safe.</summary>
public delegate void ResidualFunction(double[] x, double[] residuals);

/// <summary>Fills <paramref name="jacobian"/>[i, j] = ∂rᵢ/∂xⱼ at <paramref name="x"/>.</summary>
public delegate void JacobianFunction(double[] x, double[,] jacobian);

/// <summary>Why <see cref="DampedLeastSquares"/> stopped.</summary>
public enum LeastSquaresStatus
{
    /// <summary>The residuals are zero to rounding level: Σ r² has fallen below ε² times its
    /// starting value.</summary>
    ZeroResidual,
    /// <summary>The scaled gradient fell below its tolerance: no free variable can reduce the cost.</summary>
    GradientConverged,
    /// <summary>An accepted step reduced the cost by less than the cost tolerance.</summary>
    CostConverged,
    /// <summary>The step fell below the step tolerance.</summary>
    StepConverged,
    IterationLimit,
    EvaluationLimit
}

/// <summary>Finite differencing of the Jacobian.</summary>
public enum FiniteDifferenceMode
{
    /// <summary>Forward differences (one evaluation per variable) until the solver stops on a small
    /// step or a small cost reduction, then central differences from that point until it stops
    /// again. A forward-difference Jacobian is accurate only to about √ε relative, which on an
    /// ill-conditioned problem can stall the method short of the minimum; central differences
    /// (about ε^(2/3)) finish the job. This follows the advice in J. E. Dennis and R. B.
    /// Schnabel, Numerical Methods for Unconstrained Optimization and Nonlinear Equations (1983),
    /// section 5.6.</summary>
    ForwardThenCentral,
    /// <summary>Forward differences throughout.</summary>
    Forward,
    /// <summary>Central differences throughout: twice the evaluations.</summary>
    Central
}

/// <summary>Settings for <see cref="DampedLeastSquares"/>. Every member has a usable default.</summary>
public sealed class LeastSquaresOptions
{
    /// <summary>Lower bound per variable; null for none. Use −∞ for an unbounded variable.</summary>
    public double[]? LowerBounds { get; init; }

    /// <summary>Upper bound per variable; null for none. Use +∞ for an unbounded variable.</summary>
    public double[]? UpperBounds { get; init; }

    /// <summary>The analytic Jacobian; null to use finite differences.</summary>
    public JacobianFunction? Jacobian { get; init; }

    /// <summary>The finite-difference step per variable (absolute). Null for √ε (∛ε for central
    /// differences) times max(|xⱼ|, sⱼ), where the typical size sⱼ is |x₀ⱼ| at the start, or 1 if
    /// the start is zero. The floor keeps the step sensible when a variable passes through zero:
    /// a purely relative step there shrinks below rounding and freezes the variable.</summary>
    public double[]? FiniteDifferenceSteps { get; init; }

    /// <summary>How the Jacobian is differenced when no analytic one is given.</summary>
    public FiniteDifferenceMode Differences { get; init; } = FiniteDifferenceMode.ForwardThenCentral;

    /// <summary>Stop when an accepted step reduces the cost by less than this fraction of it.</summary>
    public double CostTolerance { get; init; } = 1e-14;

    /// <summary>Stop when the scaled step is less than this fraction of the scaled position.</summary>
    public double StepTolerance { get; init; } = 1e-14;

    /// <summary>Stop when the largest cosine between the residual and a scaled Jacobian column is
    /// below this.</summary>
    public double GradientTolerance { get; init; } = 1e-14;

    public int MaxIterations { get; init; } = 1000;

    /// <summary>Residual evaluations, counting finite-difference ones.</summary>
    public int MaxEvaluations { get; init; } = 100_000;

    /// <summary>The thread limit for the finite-difference columns; null for no limit. The result
    /// is bitwise identical at every setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; } = 1;

    public CancellationToken CancellationToken { get; init; }

    // OP-57
    /// <summary>Called after every accepted step, on the solver's thread, with a copy of the new
    /// point; null for none. The cost only falls from one call to the next, so the last call holds
    /// the best point so far: a caller that cancels keeps it. An exception it throws ends the solve.</summary>
    public Action<LeastSquaresIterate>? OnAcceptedStep { get; init; }
}

// OP-57
/// <summary>An accepted step of <see cref="DampedLeastSquares"/>.</summary>
/// <param name="Iteration">The iteration that took it (1 = the first).</param>
/// <param name="X">The new point (a copy).</param>
/// <param name="SumOfSquares">Σ rᵢ² there.</param>
/// <param name="Evaluations">Residual evaluations so far, finite-difference ones included.</param>
public sealed record LeastSquaresIterate(int Iteration, double[] X, double SumOfSquares, int Evaluations);

/// <summary>The outcome of <see cref="DampedLeastSquares.Solve"/>.</summary>
/// <param name="X">The best point found.</param>
/// <param name="Residuals">The residuals at <paramref name="X"/>.</param>
/// <param name="SumOfSquares">Σ rᵢ² at <paramref name="X"/>.</param>
/// <param name="FailedEvaluations">Evaluations that threw or gave a non-finite residual.</param>
public sealed record LeastSquaresResult(double[] X, double[] Residuals, double SumOfSquares,
    LeastSquaresStatus Status, int Iterations, int Evaluations, int FailedEvaluations);

/// <summary>
/// Nonlinear least squares, min Σ rᵢ(x)², by damped least squares (Levenberg–Marquardt). Each
/// iteration solves the damped linear problem min ‖J·p + r‖² + μ‖D·p‖² through the singular value
/// decomposition of J·D⁻¹, never through the normal equations, so a nearly singular Jacobian costs
/// no accuracy. One decomposition serves every trial damping of the iteration.
/// <list type="bullet">
/// <item>D holds the largest column lengths of J seen so far (Moré's scaling), so the method does
/// not depend on the units of the variables.</item>
/// <item>The damping follows Nielsen's rule: after an accepted step μ shrinks by
/// max(1/3, 1 − (2ρ − 1)³), where ρ is the ratio of actual to predicted reduction; after a rejected
/// one it grows by a factor that doubles each time.</item>
/// <item>Bounds are kept by projection, with variables held at a bound while the gradient pushes
/// them outward.</item>
/// <item>A point whose residuals throw or are not finite counts as infinitely bad, so a step into
/// it is rejected and the damping grows; a finite-difference step into it is taken on the other
/// side instead.</item>
/// </list>
/// References: J. J. Moré, "The Levenberg–Marquardt algorithm: implementation and theory",
/// Lecture Notes in Mathematics 630 (1978); K. Madsen, H. B. Nielsen and O. Tingleff, "Methods for
/// non-linear least squares problems", DTU (2004), section 3.2.
/// </summary>
public static class DampedLeastSquares
{
    private const double Epsilon = 2.220446049250313e-16;

    /// <summary>Minimises the sum of squares of <paramref name="residualCount"/> residuals.</summary>
    /// <exception cref="InvalidOperationException">The residuals cannot be evaluated at the start.</exception>
    public static LeastSquaresResult Solve(ResidualFunction residuals, int residualCount, double[] start,
        LeastSquaresOptions? options = null)
    {
        options ??= new LeastSquaresOptions();
        int n = start.Length, m = residualCount;
        if (n == 0) throw new ArgumentException("There must be at least one variable.", nameof(start));
        if (m < 1) throw new ArgumentOutOfRangeException(nameof(residualCount));
        var (lower, upper) = Bounds.Resolve(options.LowerBounds, options.UpperBounds, n);
        if (options.FiniteDifferenceSteps is { } steps && steps.Length != n)
            throw new ArgumentException("One finite-difference step per variable.", nameof(options));
        var token = options.CancellationToken;

        int evaluations = 0, failures = 0;
        double[]? Evaluate(double[] point)
        {
            Interlocked.Increment(ref evaluations);
            var r = new double[m];
            try
            {
                residuals((double[])point.Clone(), r);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                return null;
            }
            foreach (double value in r)
                if (!double.IsFinite(value))
                {
                    Interlocked.Increment(ref failures);
                    return null;
                }
            return r;
        }

        var x = Bounds.Clamp(start, lower, upper);
        foreach (double value in x)
            if (!double.IsFinite(value)) throw new ArgumentException("The start must be finite.", nameof(start));
        var r = Evaluate(x) ?? throw new InvalidOperationException("The residuals cannot be evaluated at the starting point.");
        double cost = SumOfSquares(r);
        // Residuals at rounding level of their starting size count as zero: a problem whose minimum
        // is zero but singular (Powell's) otherwise converges linearly for ever.
        double zeroCost = Epsilon * Epsilon * cost;

        var scale = new double[n];
        double mu = -1, nu = 2;
        bool central = options.Differences == FiniteDifferenceMode.Central;
        int iteration = 0;
        LeastSquaresStatus status;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (cost <= zeroCost) { status = LeastSquaresStatus.ZeroResidual; break; }
            if (iteration >= options.MaxIterations) { status = LeastSquaresStatus.IterationLimit; break; }
            if (evaluations >= options.MaxEvaluations) { status = LeastSquaresStatus.EvaluationLimit; break; }
            iteration++;

            var jacobian = options.Jacobian is { } analytic
                ? AnalyticJacobian(analytic, x, m)
                : FiniteDifferenceJacobian(Evaluate, x, r, start, lower, upper, central, options);
            var gradient = new double[n];
            for (int j = 0; j < n; j++)
            {
                double length = 0, dot = 0;
                for (int i = 0; i < m; i++)
                {
                    length += jacobian[i, j] * jacobian[i, j];
                    dot += jacobian[i, j] * r[i];
                }
                scale[j] = Math.Max(scale[j], Math.Sqrt(length));
                gradient[j] = dot;
            }
            for (int j = 0; j < n; j++)
                if (scale[j] == 0) scale[j] = 1;

            // Free variables: those not held at a bound by a gradient pointing outward. The
            // descent direction is −g, so g > 0 at a lower bound (or g < 0 at an upper) holds it.
            var free = new List<int>();
            double residualNorm = Math.Sqrt(cost), cosine = 0;
            for (int j = 0; j < n; j++)
            {
                if ((x[j] <= lower[j] && gradient[j] > 0) || (x[j] >= upper[j] && gradient[j] < 0)) continue;
                free.Add(j);
                cosine = Math.Max(cosine, Math.Abs(gradient[j]) / (scale[j] * residualNorm));
            }
            if (free.Count == 0 || cosine <= options.GradientTolerance)
            {
                status = LeastSquaresStatus.GradientConverged;
                break;
            }

            var scaled = new double[m, free.Count];
            for (int i = 0; i < m; i++)
                for (int k = 0; k < free.Count; k++) scaled[i, k] = jacobian[i, free[k]] / scale[free[k]];
            var svd = RealSvd.Decompose(scaled);
            var sigma = svd.SingularValues;
            var projected = new double[sigma.Length];
            for (int k = 0; k < sigma.Length; k++)
            {
                double dot = 0;
                for (int i = 0; i < m; i++) dot += svd.U[i, k] * r[i];
                projected[k] = dot;
            }
            if (mu < 0) mu = 1e-3 * sigma[0] * sigma[0];

            LeastSquaresStatus? stop = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // q = −Σ σₖ bₖ / (σₖ² + μ) · vₖ minimises ‖Ĵq + r‖² + μ‖q‖²; p = D⁻¹q.
                var q = new double[free.Count];
                for (int k = 0; k < sigma.Length; k++)
                {
                    double factor = -sigma[k] * projected[k] / (sigma[k] * sigma[k] + mu);
                    if (factor == 0) continue;
                    for (int f = 0; f < free.Count; f++) q[f] += factor * svd.V[f, k];
                }
                var trial = (double[])x.Clone();
                for (int f = 0; f < free.Count; f++)
                {
                    int j = free[f];
                    trial[j] = Math.Clamp(x[j] + q[f] / scale[j], lower[j], upper[j]);
                }
                double stepNorm = 0, positionNorm = 0;
                for (int j = 0; j < n; j++)
                {
                    double s = scale[j] * (trial[j] - x[j]), p = scale[j] * x[j];
                    stepNorm += s * s;
                    positionNorm += p * p;
                }
                stepNorm = Math.Sqrt(stepNorm);
                positionNorm = Math.Sqrt(positionNorm);
                if (stepNorm <= options.StepTolerance * (positionNorm + options.StepTolerance))
                {
                    stop = LeastSquaresStatus.StepConverged;
                    break;
                }
                if (evaluations >= options.MaxEvaluations)
                {
                    stop = LeastSquaresStatus.EvaluationLimit;
                    break;
                }

                // Predicted reduction of the linear model: ‖r‖² − ‖r + J·s‖² = −Σ (2rᵢ + (Js)ᵢ)(Js)ᵢ.
                double predicted = 0;
                for (int i = 0; i < m; i++)
                {
                    double js = 0;
                    for (int j = 0; j < n; j++) js += jacobian[i, j] * (trial[j] - x[j]);
                    predicted -= (2 * r[i] + js) * js;
                }
                var trialResiduals = Evaluate(trial);
                double trialCost = trialResiduals is null ? double.PositiveInfinity : SumOfSquares(trialResiduals);
                if (trialCost < cost)
                {
                    double reduction = cost - trialCost;
                    double rho = predicted > 0 ? reduction / predicted : 0.5;
                    double previous = cost;
                    x = trial;
                    r = trialResiduals!;
                    cost = trialCost;
                    double shrink = 2 * rho - 1;
                    mu *= Math.Max(1.0 / 3, 1 - shrink * shrink * shrink);
                    nu = 2;
                    options.OnAcceptedStep?.Invoke(new LeastSquaresIterate(iteration, (double[])x.Clone(), cost, evaluations));   // OP-57
                    if (reduction <= options.CostTolerance * previous) stop = LeastSquaresStatus.CostConverged;
                    break;
                }
                mu *= nu;
                nu *= 2;
                if (!double.IsFinite(mu))
                {
                    stop = LeastSquaresStatus.StepConverged;
                    break;
                }
            }
            if (stop is { } reason)
            {
                bool stalled = reason is LeastSquaresStatus.StepConverged or LeastSquaresStatus.CostConverged;
                if (stalled && !central && options.Jacobian is null
                    && options.Differences == FiniteDifferenceMode.ForwardThenCentral)
                {
                    // Carry on from here with central differences and a fresh damping.
                    central = true;
                    mu = -1;
                    nu = 2;
                    continue;
                }
                status = reason;
                break;
            }
        }
        return new LeastSquaresResult(x, r, cost, status, iteration, evaluations, failures);
    }

    private static double SumOfSquares(double[] r)
    {
        double sum = 0;
        foreach (double value in r) sum += value * value;
        return sum;
    }

    private static double[,] AnalyticJacobian(JacobianFunction jacobian, double[] x, int m)
    {
        var result = new double[m, x.Length];
        jacobian((double[])x.Clone(), result);
        foreach (double value in result)
            if (!double.IsFinite(value)) throw new InvalidOperationException("The analytic Jacobian is not finite.");
        return result;
    }

    /// <summary>One column per variable, each computed on its own (in parallel if allowed). A step
    /// that would leave the bounds, or lands where the residuals fail, is taken the other way; a
    /// column that fails both ways is zero, which holds that variable still for this iteration.</summary>
    private static double[,] FiniteDifferenceJacobian(Func<double[], double[]?> evaluate, double[] x, double[] r,
        double[] start, double[] lower, double[] upper, bool central, LeastSquaresOptions options)
    {
        int n = x.Length, m = r.Length;
        double relative = central ? Math.Cbrt(Epsilon) : Math.Sqrt(Epsilon);
        var columns = ParallelSlots.Compute(n, options.MaxDegreeOfParallelism, j =>
        {
            double typical = start[j] != 0 ? Math.Abs(start[j]) : 1;
            double h = options.FiniteDifferenceSteps?[j] ?? relative * Math.Max(Math.Abs(x[j]), typical);
            if (central)
            {
                var (plus, rPlus) = Probe(evaluate, x, j, h);
                var (minus, rMinus) = Probe(evaluate, x, j, -h);
                if (plus <= upper[j] && minus >= lower[j] && rPlus is not null && rMinus is not null)
                {
                    var column = new double[m];
                    for (int i = 0; i < m; i++) column[i] = (rPlus[i] - rMinus[i]) / (plus - minus);
                    return column;
                }
            }
            double first = x[j] + h > upper[j] ? -h : h;
            foreach (double step in new[] { first, -first })
            {
                var (moved, rMoved) = Probe(evaluate, x, j, step);
                if (moved == x[j] || moved < lower[j] || moved > upper[j] || rMoved is null) continue;
                var column = new double[m];
                for (int i = 0; i < m; i++) column[i] = (rMoved[i] - r[i]) / (moved - x[j]);
                return column;
            }
            return new double[m];
        }, options.CancellationToken);
        var jacobian = new double[m, n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < m; i++) jacobian[i, j] = columns[j][i];
        return jacobian;
    }

    private static (double Moved, double[]? Residuals) Probe(Func<double[], double[]?> evaluate, double[] x, int j, double step)
    {
        var point = (double[])x.Clone();
        point[j] = x[j] + step;
        return (point[j], evaluate(point));
    }
}

/// <summary>Bound handling shared by the optimisers.</summary>
internal static class Bounds
{
    public static (double[] Lower, double[] Upper) Resolve(double[]? lower, double[]? upper, int n)
    {
        if (lower is not null && lower.Length != n) throw new ArgumentException("One lower bound per variable.");
        if (upper is not null && upper.Length != n) throw new ArgumentException("One upper bound per variable.");
        var lo = new double[n];
        var hi = new double[n];
        for (int j = 0; j < n; j++)
        {
            lo[j] = lower?[j] ?? double.NegativeInfinity;
            hi[j] = upper?[j] ?? double.PositiveInfinity;
            if (double.IsNaN(lo[j]) || double.IsNaN(hi[j]) || lo[j] > hi[j])
                throw new ArgumentException($"The bounds of variable {j} are not an interval.");
        }
        return (lo, hi);
    }

    public static double[] Clamp(double[] x, double[] lower, double[] upper)
    {
        var result = new double[x.Length];
        for (int j = 0; j < x.Length; j++) result[j] = Math.Clamp(x[j], lower[j], upper[j]);
        return result;
    }
}
