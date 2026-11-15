namespace OpenSim.Optics.Sequential.Solves;

// OP-99: the ZPL macro solve (help, "Using ZPL Macro Solves"; docs/Optics-Conventions.md §56).

/// <summary>
/// Curvature, thickness, clear semi-diameter or conic: ZPL Macro. "ZPL Macro solves call a user
/// defined ZPL Macro to determine the solve value … the macro uses the keyword SOLVERETURN to
/// pass the data back to the editor" (help, Using ZPL Macro Solves); the macro is
/// <see cref="Macro"/> in the macro folder (<see cref="Scripting.Zpl.ZplMacroFolder"/>), run on a
/// copy of the lens as solved so far, with SOSO(0) the solve's surface. The value is in lens
/// units ("curvature solves must return data in units of curvature", Curvature Solves). A macro
/// that calls no SOLVERETURN fails the solve ("The absence of the SOLVERETURN call will indicate
/// … that the solve cannot be computed", SOLVERETURN). The parameter, chip zone and
/// mechanical semi-diameter macro solves are not built.
/// </summary>
public sealed record ZplMacroSolve : SurfaceSolve
{
    /// <summary>The column: <see cref="SolveColumn.Curvature"/>, <see cref="SolveColumn.Thickness"/>,
    /// <see cref="SolveColumn.SemiDiameter"/> or <see cref="SolveColumn.Conic"/>.</summary>
    public required SolveColumn Target { get; init; }

    /// <summary>The macro's file name in the macro folder ("MySolve.ZPL").</summary>
    public required string Macro { get; init; }

    public override SolveColumn Column => Target;
    public override string Name => SavedName;
    public override string Code => "Z";

    /// <summary>The solve's name as saved and as OpticStudio's solve list gives it.</summary>
    public const string SavedName = "ZPL Macro";

    /// <summary>The columns this solve is built for.</summary>
    public static bool Takes(SolveColumn column) =>
        column is SolveColumn.Curvature or SolveColumn.Thickness or SolveColumn.SemiDiameter or SolveColumn.Conic;
}
