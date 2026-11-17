using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The configurations a merit function is evaluated over (the CONF operand switches between
/// them). Numbered from 1. <see cref="ProjectMeritConfigurations"/> takes them from the project's
/// multi-configuration table (OP-30); <see cref="SingleMeritConfiguration"/> is one given lens.
/// </summary>
public interface IMeritConfigurations
{
    /// <summary>The number of configurations, at least 1.</summary>
    int Count { get; }

    /// <summary>The lens of configuration <paramref name="configuration"/> (1 to <see cref="Count"/>),
    /// solves evaluated. May throw <see cref="ArgumentException"/> naming why it cannot be built;
    /// the rows evaluated in that configuration then fail with that message.</summary>
    SequentialLens LensFor(int configuration);

    /// <summary>The system data of configuration <paramref name="configuration"/>.</summary>
    OpticalSystemData SystemFor(int configuration);

    // OP-59
    /// <summary>The active glass catalogs the glass operands (RGLA) compare against; null when
    /// not known.</summary>
    IReadOnlyList<OpticalCatalog>? Catalogs => null;

    // OP-55B
    /// <summary>The value of row <paramref name="operand"/> (1-based) of the multi-configuration
    /// table in configuration <paramref name="configuration"/>, pickups evaluated, in the saved
    /// record's units as the editor shows it (MCOG, MCOL, MCOV). Throws
    /// <see cref="ArgumentException"/> naming why there is none; by default there is no table.</summary>
    double MultiConfigValue(int operand, int configuration) =>
        throw new ArgumentException("these configurations have no multi-configuration table to read (the evaluation was given a lens, not a project).");

    // OP-49
    /// <summary>The settings the analysis with saved id <paramref name="analysisId"/> was last
    /// used with (the project's saved analysis settings), for the operands that read an analysis's
    /// saved settings (POPD, POPI); null when there are none (the analysis's defaults are used).</summary>
    System.Text.Json.JsonElement? AnalysisSettings(string analysisId) => null;

    // OP-88
    /// <summary>The optics record of configuration <paramref name="configuration"/> (the project's
    /// settings with that configuration's multi-configuration values applied), which the
    /// non-sequential rows (NSTR, NSDD, the object operands) build their scene from; null when
    /// these configurations carry no project (a lens was given), and those rows then fail naming
    /// why. May throw <see cref="ArgumentException"/> naming why the configuration cannot be built.</summary>
    OpticsSettings? SettingsFor(int configuration) => null;
}

/// <summary>One configuration: a lens and its system data.</summary>
public sealed class SingleMeritConfiguration : IMeritConfigurations
{
    private readonly SequentialLens _lens;
    private readonly OpticalSystemData _system;

    public SingleMeritConfiguration(SequentialLens lens, OpticalSystemData system)
    {
        _lens = lens ?? throw new ArgumentNullException(nameof(lens));
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    public int Count => 1;

    // OP-59
    /// <summary>The active glass catalogs, for the glass operands; null when not given.</summary>
    public IReadOnlyList<OpticalCatalog>? Catalogs { get; init; }

    public SequentialLens LensFor(int configuration) { Check(configuration); return _lens; }

    public OpticalSystemData SystemFor(int configuration) { Check(configuration); return _system; }

    private static void Check(int configuration)
    {
        if (configuration != 1)
            throw new ArgumentOutOfRangeException(nameof(configuration), configuration, "There is one configuration, number 1.");
    }

    /// <summary>The project's one configuration: its saved lens with solves evaluated
    /// (<see cref="ProjectLens.From"/>) and its system data. Throws
    /// <see cref="ArgumentException"/> when the project has no surfaces or the lens cannot be
    /// built, naming every problem.</summary>
    public static SingleMeritConfiguration FromProject(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var lens = ProjectLens.From(optics, catalogs)
            ?? throw new ArgumentException("The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to evaluate.");
        var system = SystemDataSettings.FromSettings(optics.SystemData, new List<string>());
        return new SingleMeritConfiguration(lens, system) { Catalogs = catalogs };   // OP-59: the catalogs for RGLA
    }
}

/// <summary>
/// The project's configurations from its multi-configuration table (OP-30): configuration k is
/// <see cref="MultiConfig.MultiConfiguration.SettingsFor"/> of the saved settings, turned into a
/// lens with solves evaluated (<see cref="ProjectLens.From"/>) and its system data. A project
/// without a table has one configuration, the system as entered. Each configuration is built
/// once, on first use, and safely from parallel row evaluation.
/// </summary>
public sealed class ProjectMeritConfigurations : IMeritConfigurations
{
    private readonly Lazy<(SequentialLens Lens, OpticalSystemData System)>[] _built;
    private readonly OpticsSettings _optics;   // OP-55B: MCOG, MCOL, MCOV read the table
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;   // OP-55B

    // OP-59
    /// <summary>The active glass catalogs the configurations are built with.</summary>
    public IReadOnlyList<OpticalCatalog>? Catalogs { get; }

    public ProjectMeritConfigurations(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        Catalogs = catalogs;   // OP-59
        _optics = optics;   // OP-55B
        _catalogs = catalogs;   // OP-55B
        int count = MultiConfig.MultiConfiguration.ConfigurationCount(optics);
        _built = new Lazy<(SequentialLens, OpticalSystemData)>[count];
        _settings = new Lazy<OpticsSettings>[count];   // OP-88
        for (int k = 1; k <= count; k++)
        {
            int configuration = k;
            _settings[k - 1] = new Lazy<OpticsSettings>(() => MultiConfig.MultiConfiguration.SettingsFor(optics, configuration, catalogs),   // OP-88
                LazyThreadSafetyMode.ExecutionAndPublication);
            _built[k - 1] = new Lazy<(SequentialLens, OpticalSystemData)>(() =>
            {
                var settings = _settings[configuration - 1].Value;   // OP-91: thermal pickups need the glasses (OP-88: built once, shared with SettingsFor)
                var lens = ProjectLens.From(settings, catalogs)
                    ?? throw new ArgumentException($"Configuration {configuration} has no sequential surfaces: add them in the Lens Data Editor, so there is something to evaluate.");
                return (lens, SystemDataSettings.FromSettings(settings.SystemData, new List<string>()));
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public int Count => _built.Length;

    // OP-88
    private readonly Lazy<OpticsSettings>[] _settings;

    /// <summary>Configuration <paramref name="configuration"/>'s optics record
    /// (<see cref="MultiConfig.MultiConfiguration.SettingsFor"/>), built once.</summary>
    public OpticsSettings SettingsFor(int configuration)
    {
        if (configuration < 1 || configuration > _settings.Length)
            throw new ArgumentOutOfRangeException(nameof(configuration), configuration, $"There are {_settings.Length} configurations, numbered from 1.");
        return _settings[configuration - 1].Value;
    }

    // OP-49
    /// <summary>The project's last-used settings of an analysis (<see cref="OpticsAnalysesSettings.LastSettings"/>).</summary>
    public System.Text.Json.JsonElement? AnalysisSettings(string analysisId) =>
        _optics.Analyses?.LastSettings is { } last && last.TryGetValue(analysisId, out var saved) ? saved : null;

    // OP-55B
    /// <summary>The table's row <paramref name="operand"/> in configuration
    /// <paramref name="configuration"/> (<see cref="MultiConfig.MultiConfiguration.ValuesIn(OpticsSettings, int, IReadOnlyList{OpticalCatalog}?)"/>).</summary>
    public double MultiConfigValue(int operand, int configuration)
    {
        if (_optics.MultiConfiguration is null)
            throw new ArgumentException("the system has no multi-configuration table; add one in the Multi-Configuration Editor.");
        if (configuration < 1 || configuration > _built.Length)
            throw new ArgumentException($"Cfg# {configuration} is not a configuration; there are {_built.Length}.");
        var values = MultiConfig.MultiConfiguration.ValuesIn(_optics, configuration, _catalogs);
        if (operand < 1 || operand > values.Count)
            throw new ArgumentException($"Op# {operand} is not a row of the multi-configuration table; it has rows 1 to {values.Count}.");
        var (value, error) = values[operand - 1];
        if (error is not null) throw new ArgumentException($"multi-configuration row {operand}, configuration {configuration}: {error}");
        double v = value.Number;
        return double.IsFinite(v) ? v
            : throw new ArgumentException($"multi-configuration row {operand}, configuration {configuration} holds {value}, not a finite number.");
    }

    public SequentialLens LensFor(int configuration) => At(configuration).Lens;

    public OpticalSystemData SystemFor(int configuration) => At(configuration).System;

    private (SequentialLens Lens, OpticalSystemData System) At(int configuration)
    {
        if (configuration < 1 || configuration > _built.Length)
            throw new ArgumentOutOfRangeException(nameof(configuration), configuration, $"There are {_built.Length} configurations, numbered from 1.");
        return _built[configuration - 1].Value;
    }
}
