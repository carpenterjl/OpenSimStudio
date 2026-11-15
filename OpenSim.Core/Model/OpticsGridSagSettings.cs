namespace OpenSim.Core.Model;

/// <summary>
/// The grid of a Grid Sag surface as project and lens files store it (OP-25): the values of the
/// imported data file, in the file's own unit, so the numbers read back digit for digit.
/// OpenSim.Optics' <c>GridSagData</c> holds the same and documents the layout (OpticStudio 2025
/// R2 help, "Grid Sag", "Importing Grid Data"). Points are in file order: row 0 is the +y row,
/// each row runs from −x to +x.
/// </summary>
public sealed record OpticsGridSagSettings
{
    /// <summary>Points in x (at least 5).</summary>
    public int Nx { get; init; }

    /// <summary>Points in y (at least 5).</summary>
    public int Ny { get; init; }

    /// <summary>The x spacing in the file's unit.</summary>
    public double DeltaX { get; init; }

    /// <summary>The y spacing in the file's unit.</summary>
    public double DeltaY { get; init; }

    /// <summary>The file's unit flag: 0 mm, 1 cm, 2 in, 3 m.</summary>
    public int UnitFlag { get; init; }

    /// <summary>The grid's decenter in x, the file's unit.</summary>
    public double DecenterX { get; init; }

    /// <summary>The grid's decenter in y, the file's unit.</summary>
    public double DecenterY { get; init; }

    /// <summary>z per point, the file's unit.</summary>
    public List<double>? Sag { get; init; }

    /// <summary>dz/dx per point, dimensionless.</summary>
    public List<double>? SlopeX { get; init; }

    /// <summary>dz/dy per point, dimensionless.</summary>
    public List<double>? SlopeY { get; init; }

    /// <summary>d²z/dxdy per point, inverse file units.</summary>
    public List<double>? Cross { get; init; }

    /// <summary>The 0-based indices of the points flagged nodata (their values are zero), or
    /// null for none.</summary>
    public List<int>? NoData { get; init; }
}
