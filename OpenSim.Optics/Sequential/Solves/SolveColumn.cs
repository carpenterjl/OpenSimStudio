namespace OpenSim.Optics.Sequential.Solves;

/// <summary>
/// A column of the lens data that a solve can drive, in the order OpticStudio evaluates them on
/// each surface: "curvature, thickness, glass, clear semi-diameter or semi-diameter, conic,
/// parameters (in order left to right), TCE" (OpticStudio 2025 R2 help, "Restrictions" under
/// "Solve Types (lens data editor)", ansyshelp.ansys.com, read 2026-11-14; saved in
/// plans/sources/optics/solves/Restrictions.html). The enum values are that order.
/// Extra data values are not built.
/// </summary>
public enum SolveColumn
{
    /// <summary>The radius column; a solve and a pickup act on the curvature 1/R.</summary>
    Curvature = 0,
    Thickness = 1,
    /// <summary>The material (glass) column: the medium after the surface and the MIRROR flag.</summary>
    Material = 2,
    /// <summary>The clear semi-diameter.</summary>
    SemiDiameter = 3,
    Conic = 4,
    /// <summary>A parameter of the surface's shape (<see cref="IParameterizedShape"/>).</summary>
    Parameter = 5,
    /// <summary>The TCE column (<see cref="SequentialSurface.ThermalExpansion"/>).</summary>
    Tce = 6,
}

/// <summary>One cell of the lens data: a surface, a column and, for
/// <see cref="SolveColumn.Parameter"/>, the parameter's number (0-based); otherwise 0.</summary>
public readonly record struct SurfaceCell(int Surface, SolveColumn Column, int Parameter = 0) : IComparable<SurfaceCell>
{
    /// <summary>Evaluation order: surface first, then column, then parameter number.</summary>
    public int CompareTo(SurfaceCell other)
    {
        int c = Surface.CompareTo(other.Surface);
        if (c != 0) return c;
        c = Column.CompareTo(other.Column);
        return c != 0 ? c : Parameter.CompareTo(other.Parameter);
    }

    public override string ToString() => $"surface {Surface}, {SolveColumns.Label(Column, Parameter)}";
}

/// <summary>Labels of the columns.</summary>
public static class SolveColumns
{
    public static string Label(SolveColumn column, int parameter = 0) => column switch
    {
        SolveColumn.Curvature => "curvature",
        SolveColumn.Thickness => "thickness",
        SolveColumn.Material => "material",
        SolveColumn.SemiDiameter => "clear semi-diameter",
        SolveColumn.Conic => "conic",
        SolveColumn.Parameter => $"parameter {parameter}",
        SolveColumn.Tce => "TCE",
        _ => column.ToString(),
    };

    /// <summary>True for the columns that hold one number (all but the material).</summary>
    public static bool IsNumeric(SolveColumn column) => column != SolveColumn.Material;
}
