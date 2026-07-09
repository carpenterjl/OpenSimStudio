namespace OpenSim.Pcb.Extrude;

/// <summary>
/// One conductor/dielectric layer in the board stackup, ordered bottom to top. All
/// dimensions in meters. <see cref="RegionId"/> ties the layer's elements to a material.
/// </summary>
public sealed record StackupLayer(string Name, double Thickness, int RegionId);

/// <summary>
/// The board stackup: an ordered list of layers with their z extents derived from
/// thicknesses. Defaults model a single-copper-on-FR4 board (35 µm Cu / 1.6 mm FR4).
/// </summary>
public sealed class PcbStackup
{
    public required IReadOnlyList<StackupLayer> Layers { get; init; }

    /// <summary>Region id used for copper (conductor) layers.</summary>
    public const int CopperRegion = 0;

    /// <summary>Region id used for dielectric (board) layers.</summary>
    public const int DielectricRegion = 1;

    public const double DefaultCopperThickness = 35e-6;
    public const double DefaultBoardThickness = 1.6e-3;

    /// <summary>A single copper layer only (the electrical-solve staging path).</summary>
    public static PcbStackup CopperOnly(double copperThickness = DefaultCopperThickness) => new()
    {
        Layers = new[] { new StackupLayer("Copper", copperThickness, CopperRegion) }
    };

    /// <summary>Copper on top of an FR4 core (the coupled thermal path).</summary>
    public static PcbStackup CopperOnBoard(
        double copperThickness = DefaultCopperThickness, double boardThickness = DefaultBoardThickness) => new()
    {
        Layers = new[]
        {
            new StackupLayer("FR4 core", boardThickness, DielectricRegion),
            new StackupLayer("Copper", copperThickness, CopperRegion)
        }
    };

    public double TotalThickness => Layers.Sum(l => l.Thickness);
}
