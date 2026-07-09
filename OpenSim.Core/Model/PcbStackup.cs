namespace OpenSim.Core.Model;

/// <summary>
/// The board stackup persisted with a project: copper and board thicknesses in meters.
/// Kept in Core (not OpenSim.Pcb) so it round-trips through the .ossproj serializer with
/// the rest of the model. Optional on a project — null for non-PCB work.
/// </summary>
public sealed record PcbStackupSettings
{
    /// <summary>Copper (conductor) layer thickness [m]. Default 35 µm (1 oz). Fallback when a
    /// per-layer thickness is not listed in <see cref="CopperLayerThicknesses"/>.</summary>
    public double CopperThickness { get; init; } = 35e-6;

    /// <summary>FR4 (dielectric) board thickness [m]. Default 1.6 mm. When
    /// <see cref="DielectricGapThicknesses"/> is empty this is split evenly across the gaps.</summary>
    public double BoardThickness { get; init; } = 1.6e-3;

    /// <summary>
    /// Per-copper-layer thickness [m]; index <c>i</c> is copper layer order <c>i+1</c> (1 = top).
    /// Empty ⇒ every copper layer uses <see cref="CopperThickness"/>. Kept as a list (not a
    /// dictionary) so it round-trips cleanly through the .ossproj serializer.
    /// </summary>
    public IReadOnlyList<double> CopperLayerThicknesses { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Per-gap dielectric thickness [m]; index <c>i</c> is the dielectric between copper layer
    /// order <c>i+1</c> and <c>i+2</c>. Empty ⇒ <see cref="BoardThickness"/> is split evenly
    /// across the gaps. Sets the true z-separation the mesher extrudes via barrels through.
    /// </summary>
    public IReadOnlyList<double> DielectricGapThicknesses { get; init; } = Array.Empty<double>();
}
