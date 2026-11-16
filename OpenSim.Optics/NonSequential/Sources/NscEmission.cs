namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// The rays one or more sources launch at one wavelength (plan item OP-77;
/// docs/Optics-Conventions.md §72): the non-sequential tracer works at one wavelength, so a
/// source's rays come grouped by wavelength. Positions and directions are in the scene frame,
/// meters; each ray's <see cref="NscLaunchRay.Intensity"/> is its power in the traced unit
/// (watts, or joules for an energy source; a lumen source is traced in watts, see
/// <see cref="NscSourceEmitter"/>), and its <see cref="NscLaunchRay.InsideOf"/> is the source's
/// resolved, absolute "Inside Of".
/// </summary>
/// <param name="WavelengthMicrometers">µm in the system's air, as the tracer takes it.</param>
/// <param name="Rays">In source order, then in ray-number order within each source.</param>
public sealed record NscEmission(double WavelengthMicrometers, IReadOnlyList<NscLaunchRay> Rays)
{
    /// <summary>For each ray, the 1-based number of the source object that launched it.</summary>
    public IReadOnlyList<int>? SourceObjects { get; init; }

    /// <summary>For each ray, its number within its source (0-based, counting every array
    /// element's rays: element e's ray j is e·N + j). Ray k's position, direction and wavelength
    /// depend only on k, the source and the seed.</summary>
    public IReadOnlyList<long>? SourceRayNumbers { get; init; }

    /// <summary>The sum of the rays' powers (compensated summation).</summary>
    public double Power
    {
        get
        {
            var sum = new NscLedgerBuilder.Sum();
            foreach (var r in Rays) sum.Add(r.Intensity);
            return sum.Total;
        }
    }
}

/// <summary>Which of a source's two ray counts to use (help, "Parameters Common to All Source
/// Objects": parameter 1, "# Layout Rays", for layout plots; parameter 2, "# Analysis Rays",
/// for analysis).</summary>
public enum NscEmissionPurpose
{
    Analysis = 0,
    Layout = 1,
}
