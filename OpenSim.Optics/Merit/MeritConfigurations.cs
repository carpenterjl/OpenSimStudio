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
        return new SingleMeritConfiguration(lens, system);
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

    public ProjectMeritConfigurations(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        int count = MultiConfig.MultiConfiguration.ConfigurationCount(optics);
        _built = new Lazy<(SequentialLens, OpticalSystemData)>[count];
        for (int k = 1; k <= count; k++)
        {
            int configuration = k;
            _built[k - 1] = new Lazy<(SequentialLens, OpticalSystemData)>(() =>
            {
                var settings = MultiConfig.MultiConfiguration.SettingsFor(optics, configuration, catalogs);   // OP-91: thermal pickups need the glasses
                var lens = ProjectLens.From(settings, catalogs)
                    ?? throw new ArgumentException($"Configuration {configuration} has no sequential surfaces: add them in the Lens Data Editor, so there is something to evaluate.");
                return (lens, SystemDataSettings.FromSettings(settings.SystemData, new List<string>()));
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public int Count => _built.Length;

    public SequentialLens LensFor(int configuration) => At(configuration).Lens;

    public OpticalSystemData SystemFor(int configuration) => At(configuration).System;

    private (SequentialLens Lens, OpticalSystemData System) At(int configuration)
    {
        if (configuration < 1 || configuration > _built.Length)
            throw new ArgumentOutOfRangeException(nameof(configuration), configuration, $"There are {_built.Length} configurations, numbered from 1.");
        return _built[configuration - 1].Value;
    }
}
