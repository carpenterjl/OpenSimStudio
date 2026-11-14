namespace OpenSim.Optics.Analysis;

/// <summary>
/// Every analysis the program offers, in menu order, found by saved id. The analyses register
/// in <see cref="CreateDefault"/>: a new analysis item adds one line there and nothing else in
/// the windows, the project file or the report.
/// </summary>
public sealed class OpticsAnalysisRegistry
{
    private readonly List<IOpticsAnalysis> _all = new();
    private readonly Dictionary<string, IOpticsAnalysis> _byId = new(StringComparer.Ordinal);

    /// <summary>The registered analyses in registration order.</summary>
    public IReadOnlyList<IOpticsAnalysis> All => _all;

    /// <summary>Adds an analysis. Its id must be non-blank and not taken, and its settings type
    /// must be constructible, so that a mistake fails at start-up rather than when a saved
    /// project names the analysis.</summary>
    public void Register(IOpticsAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (string.IsNullOrWhiteSpace(analysis.Id))
            throw new ArgumentException($"Analysis '{analysis.Name}' has no id.", nameof(analysis));
        if (string.IsNullOrWhiteSpace(analysis.Name))
            throw new ArgumentException($"Analysis '{analysis.Id}' has no name.", nameof(analysis));
        if (_byId.ContainsKey(analysis.Id))
            throw new ArgumentException($"An analysis with id '{analysis.Id}' is already registered.", nameof(analysis));
        var defaults = analysis.CreateDefaultSettings();
        if (defaults is null || defaults.GetType() != analysis.SettingsType)
            throw new ArgumentException(
                $"Analysis '{analysis.Id}' must create default settings of its own settings type {analysis.SettingsType.Name}.",
                nameof(analysis));
        _all.Add(analysis);
        _byId.Add(analysis.Id, analysis);
    }

    public bool TryGet(string? id, out IOpticsAnalysis analysis)
    {
        if (id is not null && _byId.TryGetValue(id, out var found))
        {
            analysis = found;
            return true;
        }
        analysis = null!;
        return false;
    }

    /// <summary>The program's analyses. The analysis items (OP-34 to OP-45) register here.</summary>
    public static OpticsAnalysisRegistry CreateDefault()
    {
        var registry = new OpticsAnalysisRegistry();
        registry.Register(new SystemSummaryAnalysis());
        registry.Register(new ParaxialDataAnalysis());
        // OP-20: the real ray trace (no saved surfaces until OP-31).
        registry.Register(new SingleRayTraceAnalysis());
        // Materials (OP-14).
        registry.Register(new GlassMapAnalysis());
        registry.Register(new AthermalGlassMapAnalysis());
        registry.Register(new DispersionCurveAnalysis());
        registry.Register(new TransmittanceCurveAnalysis());
        registry.Register(new GlassCompareAnalysis());
        registry.Register(new ModelGlassAnalysis());
        registry.Register(new GlassFitAnalysis());
        // OP-15: the twelve coating plots.
        foreach (var coating in CoatingAnalyses.All()) registry.Register(coating);
        // OP-48: Gaussian beams.
        registry.Register(new GaussianBeamAnalysis());
        return registry;
    }
}
