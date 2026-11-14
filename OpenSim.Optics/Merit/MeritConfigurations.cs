using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The configurations a merit function is evaluated over (the CONF operand switches between
/// them). Numbered from 1. Multi-configuration (OP-30) implements this over its configurations;
/// until then <see cref="SingleMeritConfiguration"/> is the one configuration of the project.
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
