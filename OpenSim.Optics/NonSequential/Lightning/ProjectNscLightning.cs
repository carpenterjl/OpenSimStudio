// OP-85
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.NonSequential.Lightning;

/// <summary>
/// LightningTrace on a project's own scene (plan item OP-85; docs/Optics-Conventions.md §79): the
/// saved objects (<see cref="ProjectNonSequential.Scene"/>), the saved non-sequential controls on
/// top of the system environment, coatings and primary wavelength, and the project's emission
/// options (system wavelengths, source units, V(λ)), as <see cref="ProjectNscSources.Trace"/>
/// reads them for the Monte Carlo trace, then <see cref="NscLightningTrace.Run"/>.
/// </summary>
public static class ProjectNscLightning
{
    /// <param name="sources">The source objects (1-based); null for every source ("Trace Begins
    /// tracing mesh rays from all defined sources"; help, "Lightning Trace").</param>
    public static NscLightningResult Trace(OpticsAnalysisContext context, int raySampling, int edgeSampling,
        IReadOnlyList<int>? sources = null, int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var unit = system.Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException("The system has no non-sequential objects, so there are no sources for LightningTrace.");
        return Trace(context, scene, raySampling, edgeSampling, sources, maxDegreeOfParallelism, cancellation, notes);
    }

    /// <summary>As <see cref="Trace(OpticsAnalysisContext, int, int, IReadOnlyList{int}?, int?, CancellationToken)"/>
    /// on a given scene (the Source Illumination Map's screen scene).</summary>
    internal static NscLightningResult Trace(OpticsAnalysisContext context, NscScene scene, int raySampling, int edgeSampling,
        IReadOnlyList<int>? sources, int? maxDegreeOfParallelism, CancellationToken cancellation, List<string>? notes = null)
    {
        notes ??= new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var unit = system.Units.LensUnit;
        var baseline = new NscTraceSettings
        {
            UsePolarization = false,
            Environment = system.Environment,
            Coatings = OpenSim.Optics.Coatings.CoatingCatalogSettings.FromSettings(context.System.Coatings, notes),
            PrimaryMicrometers = system.Wavelengths.Primary.Micrometers,
        };
        var trace = ProjectNonSequential.Settings(context.System.NonSequential, unit, baseline);
        var settings = new NscLightningSettings { RaySampling = raySampling, EdgeSampling = edgeSampling, MaxDegreeOfParallelism = maxDegreeOfParallelism };
        var result = NscLightningTrace.Run(scene, trace, ProjectNscSources.EmitOptions(context, NscEmissionPurpose.Analysis), settings, sources, cancellation);
        return notes.Count == 0 ? result : result with { Notes = notes.Concat(result.Notes).ToArray() };
    }
}
