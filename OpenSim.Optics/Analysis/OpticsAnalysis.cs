namespace OpenSim.Optics.Analysis;

/// <summary>The typed base every analysis derives from: it names its settings record once and
/// receives it typed.</summary>
public abstract class OpticsAnalysis<TSettings> : IOpticsAnalysis where TSettings : class, new()
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Category { get; }

    public Type SettingsType => typeof(TSettings);

    public object CreateDefaultSettings() => new TSettings();

    public OpticsAnalysisResult Compute(OpticsAnalysisContext context, object settings, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (settings is not TSettings typed)
            throw new ArgumentException(
                $"Analysis '{Id}' takes settings of type {typeof(TSettings).Name}, not {settings?.GetType().Name ?? "null"}.",
                nameof(settings));
        cancellation.ThrowIfCancellationRequested();
        return Compute(context, typed, cancellation);
    }

    /// <summary>The analysis itself, on a background thread; see <see cref="IOpticsAnalysis.Compute"/>.</summary>
    protected abstract OpticsAnalysisResult Compute(OpticsAnalysisContext context, TSettings settings,
        CancellationToken cancellation);
}
