using OpenSim.Core.Numerics.Optimization;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The merit function as the optimizer kernel's residual interface
/// (<see cref="ResidualFunction"/>, OP-5): one residual per row, √(W/ΣW)·(V − T), so the sum
/// of squares is MF². Adapter only: how the variables x change the lens is the caller's
/// (<paramref name="model"/> in the constructor), and no optimization loop is here (OP-57).
/// A failed row gives its penalised residual, not a non-finite one, so the kernel sees a finite,
/// large merit where a ray fails.
/// </summary>
public sealed class MeritResiduals
{
    private readonly MeritFunction _function;
    private readonly Func<double[], IMeritConfigurations> _model;
    private readonly MeritOperandRegistry _registry;
    private readonly MeritEvaluationOptions _options;

    /// <param name="function">The merit function; checked here (<see cref="MeritEvaluator.Validate"/>).</param>
    /// <param name="model">The configurations at the variables x.</param>
    /// <param name="configurationCount">How many configurations <paramref name="model"/> gives.</param>
    public MeritResiduals(MeritFunction function, Func<double[], IMeritConfigurations> model, int configurationCount = 1,
        MeritOperandRegistry? registry = null, MeritEvaluationOptions? options = null)
    {
        _function = function ?? throw new ArgumentNullException(nameof(function));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _registry = registry ?? MeritOperandRegistry.CreateDefault();
        _options = options ?? new MeritEvaluationOptions();
        var problems = MeritEvaluator.Validate(function, _registry, configurationCount);
        if (problems.Count > 0)
            throw new ArgumentException("Merit function:" + Environment.NewLine + string.Join(Environment.NewLine, problems), nameof(function));
        if (function.Rows.Count == 0) throw new ArgumentException("The merit function has no rows.", nameof(function));
    }

    /// <summary>The number of residuals: one per row.</summary>
    public int Count => _function.Rows.Count;

    /// <summary>Fills <paramref name="residuals"/> at <paramref name="x"/>. Thread-safe as long as
    /// <c>model</c> is.</summary>
    public void Evaluate(double[] x, double[] residuals)
    {
        ArgumentNullException.ThrowIfNull(residuals);
        if (residuals.Length != Count)
            throw new ArgumentException($"The merit function has {Count} residuals; the array holds {residuals.Length}.", nameof(residuals));
        var evaluation = MeritEvaluator.Evaluate(_function, _model(x), _registry, _options);
        for (int i = 0; i < Count; i++) residuals[i] = evaluation.Residuals[i];
    }

    /// <summary>The adapter as the kernel's delegate.</summary>
    public ResidualFunction AsResidualFunction() => Evaluate;
}
