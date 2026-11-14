namespace OpenSim.Core.Numerics.Optimization;

// OP-57: the derivative-free local optimizer the optics toolbox offers beside damped least squares.

/// <summary>Why <see cref="OrthogonalDescent"/> stopped.</summary>
public enum OrthogonalDescentStatus
{
    /// <summary>Every trial step fell below the step tolerance.</summary>
    StepConverged,
    /// <summary>The value fell to zero (or below ε² of its start).</summary>
    ZeroValue,
    /// <summary>The cycle limit was reached.</summary>
    CycleLimit,
    /// <summary>The evaluation limit was reached.</summary>
    EvaluationLimit
}

/// <summary>Settings for <see cref="OrthogonalDescent"/>. Every member has a usable default.</summary>
public sealed class OrthogonalDescentOptions
{
    /// <summary>The first trial step per variable. Null for 5 % of |xⱼ|, or 0.05 where xⱼ = 0.</summary>
    public double[]? InitialSteps { get; init; }

    /// <summary>Stop when every trial step is below this fraction of its initial size.</summary>
    public double StepTolerance { get; init; } = 1e-9;

    /// <summary>One cycle is one pass through every direction; null for no limit.</summary>
    public int? MaxCycles { get; init; }

    public int MaxEvaluations { get; init; } = 100_000;

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called at the end of every cycle that improved the value, on the minimiser's
    /// thread, with a copy of the best point; null for none. The value only falls, so the last
    /// call holds the best point so far.</summary>
    public Action<OrthogonalDescentIterate>? OnImprovedCycle { get; init; }
}

/// <summary>A cycle of <see cref="OrthogonalDescent"/> that improved the value.</summary>
public sealed record OrthogonalDescentIterate(int Cycle, double[] X, double Value, int Evaluations);

/// <summary>The outcome of <see cref="OrthogonalDescent.Minimize"/>.</summary>
/// <param name="X">The best point found.</param>
/// <param name="Value">The function there.</param>
/// <param name="FailedEvaluations">Evaluations that threw or gave NaN (counted as +∞).</param>
public sealed record OrthogonalDescentResult(double[] X, double Value, OrthogonalDescentStatus Status, int Cycles,
    int Evaluations, int FailedEvaluations);

/// <summary>
/// A derivative-free local minimiser by orthonormal search directions and discrete trial steps,
/// for merit functions whose derivatives are noisy or unreliable. OpticStudio's help describes
/// its Orthogonal Descent only as "an orthonormalization of the variables and discrete sampling
/// of solution space" that "does not compute numerical derivatives" (OpticStudio 2025 R2 help,
/// Optimize!); its algorithm is not published. This is the rotating-coordinates scheme of
/// H. H. Rosenbrock (Computer Journal 3, 175, 1960), which fits that description:
/// <list type="bullet">
/// <item>Each cycle tries one step along each direction in turn. A step that lowers the value
/// is kept and that direction's step grows by <see cref="Expansion"/>; one that does not is
/// undone and the step reverses and shrinks by <see cref="Contraction"/>.</item>
/// <item>When every direction has had a success and a failure since the last rotation, the
/// directions are rotated: the progress made along them, summed from each direction on, is
/// orthonormalised by Gram–Schmidt, so the first new direction is the overall displacement.</item>
/// </list>
/// The two step factors are this program's choice (stated here, not taken from a paper). Every
/// evaluation is sequential, so the result does not depend on threads. A failed evaluation
/// counts as +∞, so the search moves away from where the function cannot be evaluated.
/// </summary>
public static class OrthogonalDescent
{
    /// <summary>The growth of a direction's step after a success (this program's choice).</summary>
    public const double Expansion = 3;

    /// <summary>The factor a direction's step takes after a failure, reversing it (this program's choice).</summary>
    public const double Contraction = -0.5;

    private const double Epsilon = 2.220446049250313e-16;

    /// <exception cref="InvalidOperationException">The function cannot be evaluated at the start.</exception>
    public static OrthogonalDescentResult Minimize(Func<double[], double> function, double[] start, OrthogonalDescentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(start);
        options ??= new OrthogonalDescentOptions();
        int n = start.Length;
        if (n == 0) throw new ArgumentException("There must be at least one variable.", nameof(start));
        foreach (double v in start)
            if (!double.IsFinite(v)) throw new ArgumentException("The start must be finite.", nameof(start));
        var token = options.CancellationToken;

        var initial = new double[n];
        if (options.InitialSteps is { } given)
        {
            if (given.Length != n) throw new ArgumentException("One initial step per variable.", nameof(options));
            for (int j = 0; j < n; j++)
            {
                if (!(given[j] > 0) || !double.IsFinite(given[j])) throw new ArgumentException("Initial steps must be positive and finite.", nameof(options));
                initial[j] = given[j];
            }
        }
        else
            for (int j = 0; j < n; j++) initial[j] = start[j] != 0 ? 0.05 * Math.Abs(start[j]) : 0.05;

        int evaluations = 0, failures = 0;
        double Evaluate(double[] point)
        {
            token.ThrowIfCancellationRequested();
            evaluations++;
            double value;
            try { value = function((double[])point.Clone()); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                return double.PositiveInfinity;
            }
            if (double.IsNaN(value)) { failures++; return double.PositiveInfinity; }
            return value;
        }

        var x = (double[])start.Clone();
        double f = Evaluate(x);
        if (!double.IsFinite(f)) throw new InvalidOperationException("The function cannot be evaluated at the starting point.");
        double zero = Epsilon * Epsilon * Math.Abs(f);

        // Directions in the units of the initial steps: direction k moves variable j by
        // basis[k][j]·initial[j] per unit step, so the variables' scales do not matter.
        var basis = new double[n][];
        for (int k = 0; k < n; k++) { basis[k] = new double[n]; basis[k][k] = 1; }
        var step = Enumerable.Repeat(1.0, n).ToArray();
        var progress = new double[n];
        var succeeded = new bool[n];
        var failed = new bool[n];

        int cycle = 0;
        OrthogonalDescentStatus status;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (f <= zero) { status = OrthogonalDescentStatus.ZeroValue; break; }
            if (step.All(s => Math.Abs(s) <= options.StepTolerance)) { status = OrthogonalDescentStatus.StepConverged; break; }
            if (options.MaxCycles is int max && cycle >= max) { status = OrthogonalDescentStatus.CycleLimit; break; }
            if (evaluations >= options.MaxEvaluations) { status = OrthogonalDescentStatus.EvaluationLimit; break; }
            cycle++;
            bool improved = false;
            for (int k = 0; k < n; k++)
            {
                if (evaluations >= options.MaxEvaluations) break;
                var trial = new double[n];
                for (int j = 0; j < n; j++) trial[j] = x[j] + step[k] * basis[k][j] * initial[j];
                double value = Evaluate(trial);
                if (value < f)
                {
                    x = trial;
                    f = value;
                    progress[k] += step[k];
                    step[k] *= Expansion;
                    succeeded[k] = true;
                    improved = true;
                }
                else
                {
                    step[k] *= Contraction;
                    failed[k] = true;
                }
            }
            if (improved) options.OnImprovedCycle?.Invoke(new OrthogonalDescentIterate(cycle, (double[])x.Clone(), f, evaluations));
            if (succeeded.All(b => b) && failed.All(b => b))
            {
                Rotate(basis, progress);
                Array.Clear(progress);
                Array.Clear(succeeded);
                Array.Clear(failed);
            }
        }
        return new OrthogonalDescentResult(x, f, status, cycle, evaluations, failures);
    }

    /// <summary>Rosenbrock's rotation: aₖ = Σ_{i ≥ k} progressᵢ·dᵢ, orthonormalised in order. A
    /// set that has become dependent to rounding is left as it was.</summary>
    private static void Rotate(double[][] basis, double[] progress)
    {
        int n = basis.Length;
        var a = new double[n][];
        for (int k = 0; k < n; k++)
        {
            a[k] = new double[n];
            for (int i = k; i < n; i++)
                for (int j = 0; j < n; j++) a[k][j] += progress[i] * basis[i][j];
        }
        var next = new double[n][];
        for (int k = 0; k < n; k++)
        {
            var v = (double[])a[k].Clone();
            double size = Norm(v);
            for (int p = 0; p < k; p++)
            {
                double dot = 0;
                for (int j = 0; j < n; j++) dot += v[j] * next[p][j];
                for (int j = 0; j < n; j++) v[j] -= dot * next[p][j];
            }
            double length = Norm(v);
            if (!(length > 1e-10 * size)) return;   // dependent: keep the old directions
            for (int j = 0; j < n; j++) v[j] /= length;
            next[k] = v;
        }
        for (int k = 0; k < n; k++) basis[k] = next[k];
    }

    private static double Norm(double[] v)
    {
        double sum = 0;
        foreach (double x in v) sum += x * x;
        return Math.Sqrt(sum);
    }
}
