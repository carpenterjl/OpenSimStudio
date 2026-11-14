namespace OpenSim.Core.Model;

/// <summary>
/// The system polarization (OP-51) as project and lens files store it: the launch Jones vector
/// (magnitudes and phases in degrees, OpticStudio's convention) or unpolarized light, the
/// reference rule by saved name (<c>PolarizationReferences</c> in OpenSim.Optics; null is the
/// X axis reference) and the thin-film phase conversion. A null record on
/// <see cref="OpticsSystemSettings.Polarization"/> — every file written before OP-51 — is the
/// default polarization.
/// </summary>
public sealed record OpticsPolarizationSettings
{
    public bool Unpolarized { get; init; }
    public double Jx { get; init; } = 1;
    public double Jy { get; init; }
    public double XPhaseDegrees { get; init; }
    public double YPhaseDegrees { get; init; }
    public string? Reference { get; init; }
    public bool ConvertThinFilmPhase { get; init; } = true;
}
