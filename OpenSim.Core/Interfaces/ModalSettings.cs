namespace OpenSim.Core.Interfaces;

/// <summary>Settings for a modal (natural frequency) analysis. Attached to
/// <see cref="SolveInput.Modal"/>; ignored by every other solver.</summary>
public sealed record ModalSettings
{
    /// <summary>Number of natural-frequency modes to extract (lowest first).</summary>
    public int ModeCount { get; init; } = 6;
}
