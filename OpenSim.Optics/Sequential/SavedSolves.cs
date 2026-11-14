using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Sequential.Solves;

namespace OpenSim.Optics.Sequential;

/// <summary>One saved cell a solve or pickup drives, as <see cref="SavedSolves.Translate"/> made it.</summary>
/// <param name="Surface">The surface (0-based).</param>
/// <param name="Column">The saved column key ("Radius", "Thickness", "Par3", …).</param>
/// <param name="Solve">The solve record handed to <see cref="SolveEngine"/>.</param>
public sealed record SavedSolveCell(int Surface, string Column, SurfaceSolve Solve);

/// <summary>The solves of a saved lens and what could not be translated.</summary>
public sealed record SavedSolveSet
{
    /// <summary>One entry per translated solve or pickup.</summary>
    public required IReadOnlyList<SavedSolveCell> Cells { get; init; }

    /// <summary>Flags that cannot be evaluated, one line each naming the surface and the cell;
    /// a lens with any must not be traced as solved.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>Flags kept but not evaluated by design (variables are for the optimizer, which
    /// is not built; the semi-diameters' "Automatic" is the sizing; chip zone and mechanical
    /// semi-diameter pickups have no solve column), one line each.</summary>
    public required IReadOnlyList<string> NotEvaluated { get; init; }

    public IEnumerable<SurfaceSolve> Solves => Cells.Select(c => c.Solve);
}

/// <summary>
/// The saved cell flags (<see cref="OpticsCellFlagSettings"/>) as <see cref="SurfaceSolve"/>
/// records for <see cref="SolveEngine.Apply"/> (TC-6, docs/Optics-Conventions.md section 20).
/// Solve names are the lens data editor's, which are OpticStudio's "Summary of Solves" names
/// (OpticStudio 2025 R2 help, plans/sources/optics/solves/Summary_of_Solves.html), and the
/// parameters are in that table's order, in lens units; they are converted to SI here.
/// <list type="bullet">
/// <item>Column keys map to <see cref="SolveColumn"/>: Radius → curvature, Thickness, Material,
/// SemiDiameter, Conic, Tce, and "Par n" → parameter n for n ≤ 12 (OpticStudio's pickup column
/// codes reach "Parameter 0 through 12", "Integer Codes for Column Numbers"). Flags on extra
/// data (n ≥ 13) are not built and are problems.</item>
/// <item>A pickup T = O + S·V is written in lens units; with f_t and f_s the SI factors of the
/// target and source columns it becomes O·f_t + (S·f_t/f_s)·V in SI. A curvature picks up a
/// curvature with the scale unchanged (the help: the scale applies to the curvature).</item>
/// <item>Surface numbers in solve parameters (the surface to be concentric to, the reference
/// surface of a position or compensator solve) must be whole numbers.</item>
/// </list>
/// </summary>
public static class SavedSolves
{
    // The solve names, as the editor stores them (OpticsLensEditorSchema) and SurfaceSolve.Name gives them.
    public const string Automatic = "Automatic";
    public const string Maximum = "Maximum";

    /// <summary>Translates every flag of <paramref name="surfaces"/>.</summary>
    public static SavedSolveSet Translate(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        double perUnit = OpticsConventions.MetersPer(unit);
        var cells = new List<SavedSolveCell>();
        var problems = new List<string>();
        var kept = new List<string>();
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            if (s?.Flags is not { Count: > 0 } flags) continue;
            foreach (var f in flags)
            {
                string where = $"{SavedSurfaces.Where(j, surfaces.Count)}, {f.Column}";
                if (string.Equals(f.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal)) continue;
                bool solve = string.Equals(f.Kind, SavedSurfaces.SolveFlag, StringComparison.Ordinal);
                bool pickup = string.Equals(f.Kind, SavedSurfaces.PickupFlag, StringComparison.Ordinal);
                if (!solve && !pickup)
                {
                    problems.Add($"{where}: the flag '{f.Kind}' is not one this version knows (Variable, Solve, Pickup).");
                    continue;
                }
                if (solve && f.Solve == Automatic && f.Column is SavedSurfaces.SemiDiameterColumn or SavedSurfaces.MechanicalSemiDiameterColumn)
                    continue;   // automatic: the saved value is null and the trace sizes it
                if (f.Column is SavedSurfaces.ChipZoneColumn or SavedSurfaces.MechanicalSemiDiameterColumn)
                {
                    kept.Add($"{where}: a {(pickup ? "pickup" : $"'{f.Solve}' solve")} is stored but not evaluated (OpticStudio's Summary of Solves has no solve for this column); the cell's value is traced.");
                    continue;
                }
                if (!TryColumn(f.Column, out var column, out int parameter, out string? why))
                {
                    problems.Add($"{where}: {why}");
                    continue;
                }
                try
                {
                    var record = pickup
                        ? Pickup(surfaces, j, f, column, parameter, perUnit)
                        : Solve(surfaces, j, f, column, parameter, perUnit);
                    cells.Add(new SavedSolveCell(j, f.Column!, record));
                }
                catch (ArgumentException ex) { problems.Add($"{where}: {ex.Message}"); }
            }
        }
        return new SavedSolveSet { Cells = cells, Problems = problems, NotEvaluated = kept };
    }

    /// <summary>The solve column of a saved column key.</summary>
    public static bool TryColumn(string? key, out SolveColumn column, out int parameter, out string? why)
    {
        parameter = 0;
        why = null;
        switch (key)
        {
            case SavedSurfaces.RadiusColumn: column = SolveColumn.Curvature; return true;
            case SavedSurfaces.ThicknessColumn: column = SolveColumn.Thickness; return true;
            case SavedSurfaces.MaterialColumn: column = SolveColumn.Material; return true;
            case SavedSurfaces.SemiDiameterColumn: column = SolveColumn.SemiDiameter; return true;
            case SavedSurfaces.ConicColumn: column = SolveColumn.Conic; return true;
            case SavedSurfaces.TceColumn: column = SolveColumn.Tce; return true;
        }
        column = SolveColumn.Parameter;
        if (SavedSurfaces.ParameterNumber(key) is int n)
        {
            if (n >= SurfaceTypes.FirstExtraData)
            {
                why = $"solves and pickups on parameter {n} (extra data) are not built; OpticStudio's pickup columns reach parameter 12.";
                return false;
            }
            parameter = n;
            return true;
        }
        why = $"the column '{key}' takes no solve or pickup in this version.";
        return false;
    }

    /// <summary>The SI factor of a saved numeric column of surface <paramref name="s"/>: lengths
    /// u, the conic and TCE 1, a parameter u^power of its column (1 when the type is not
    /// registered), the radius 1 (pickups act on the curvature with the scale unchanged).</summary>
    public static double Factor(OpticsSurfaceSettings s, SolveColumn column, int parameter, double perUnit) => column switch
    {
        SolveColumn.Thickness or SolveColumn.SemiDiameter => perUnit,
        SolveColumn.Parameter => SurfaceTypes.Find(s.Type)?.Parameter(parameter) is { } info ? SurfaceTypes.Factor(info.LengthPower, perUnit) : 1,
        _ => 1,
    };

    private static SurfaceSolve Pickup(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j, OpticsCellFlagSettings f,
        SolveColumn target, int parameter, double perUnit)
    {
        if (f.PickupSurface < 0 || f.PickupSurface >= surfaces.Count)
            throw new ArgumentException($"the pickup's source surface {f.PickupSurface} does not exist (the lens has surfaces 0 to {surfaces.Count - 1}).");
        SolveColumn source = target;
        int sourceParameter = parameter;
        if (f.PickupColumn is { Length: > 0 } key && key != f.Column)
        {
            if (key is SavedSurfaces.ChipZoneColumn or SavedSurfaces.MechanicalSemiDiameterColumn || !TryColumn(key, out source, out sourceParameter, out string? why))
                throw new ArgumentException($"the pickup reads the column '{key}', which a pickup cannot read in this version.");
        }
        double scale = f.PickupScale, offset = f.PickupOffset;
        if (target != SolveColumn.Curvature && target != SolveColumn.Material && target != SolveColumn.Tce)
        {
            double ft = Factor(surfaces[j], target, parameter, perUnit);
            double fs = Factor(surfaces[f.PickupSurface], source, sourceParameter, perUnit);
            double ratio = ft / fs;
            scale = ratio == 1 ? scale : scale * ratio;
            offset *= ft;
        }
        return new PickupSolve
        {
            Surface = j,
            Target = target,
            TargetParameter = target == SolveColumn.Parameter ? parameter : 0,
            FromSurface = f.PickupSurface,
            FromColumn = source == target ? null : source,
            FromParameter = source == SolveColumn.Parameter ? sourceParameter : null,
            Scale = scale,
            Offset = offset,
        };
    }

    private static SurfaceSolve Solve(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j, OpticsCellFlagSettings f,
        SolveColumn column, int parameter, double perUnit)
    {
        var p = f.SolveParameters ?? new List<double>();
        double P(int i) => i < p.Count ? p[i] : 0;
        int Surface(int i)
        {
            double v = P(i);
            if (Math.Round(v) != v || !double.IsFinite(v))
                throw new ArgumentException($"the '{f.Solve}' solve's surface number is {v.ToString("R", CultureInfo.InvariantCulture)}; it must be a whole number.");
            return (int)v;
        }
        SurfaceSolve? solve = (column, f.Solve) switch
        {
            (SolveColumn.Curvature, "Marginal ray angle") => new MarginalRayAngleSolve { Surface = j, Angle = P(0) },
            (SolveColumn.Curvature, "Chief ray angle") => new ChiefRayAngleSolve { Surface = j, Angle = P(0) },
            (SolveColumn.Curvature, "Marginal ray normal") => new MarginalRayNormalSolve { Surface = j },
            (SolveColumn.Curvature, "Chief ray normal") => new ChiefRayNormalSolve { Surface = j },
            (SolveColumn.Curvature, "Aplanatic") => new AplanaticSolve { Surface = j },
            (SolveColumn.Curvature, "Element power") => new ElementPowerSolve { Surface = j, Power = P(0) / perUnit },
            (SolveColumn.Curvature, "Concentric with surface") => new ConcentricWithSurfaceSolve { Surface = j, Of = Surface(0) },
            (SolveColumn.Curvature, "Concentric with radius") => new ConcentricWithRadiusSolve { Surface = j, Of = Surface(0) },
            (SolveColumn.Curvature, "F/#") => new FNumberSolve { Surface = j, FNumber = P(0) },
            (SolveColumn.Thickness, "Marginal ray height") => new MarginalRayHeightSolve { Surface = j, Height = P(0) * perUnit, PupilZone = P(1) },
            (SolveColumn.Thickness, "Chief ray height") => new ChiefRayHeightSolve { Surface = j, Height = P(0) * perUnit },
            (SolveColumn.Thickness, "Edge thickness") => new EdgeThicknessSolve { Surface = j, EdgeThickness = P(0) * perUnit, RadialHeight = P(1) * perUnit },
            (SolveColumn.Thickness, "Optical path difference") => new OpticalPathDifferenceSolve { Surface = j, Waves = P(0), PupilZone = P(1) },
            (SolveColumn.Thickness, "Position") => new PositionSolve { Surface = j, ReferenceSurface = Surface(0), Length = P(1) * perUnit },
            (SolveColumn.Thickness, "Compensator") => new CompensatorSolve { Surface = j, ReferenceSurface = Surface(0), Sum = P(1) * perUnit },
            (SolveColumn.Thickness, "Center of curvature") => new CenterOfCurvatureSolve { Surface = j, Of = Surface(0) },
            (SolveColumn.Thickness, "Pupil position") => new PupilPositionSolve { Surface = j },
            (SolveColumn.SemiDiameter, Maximum) => new MaximumSemiDiameterSolve { Surface = j },
            (SolveColumn.Parameter, "Chief ray") => new ParameterChiefRaySolve { Surface = j, Parameter = parameter, Field = Surface(0), Wavelength = Surface(1) },
            _ => null,
        };
        return solve ?? throw new ArgumentException($"the solve '{f.Solve}' is not one this column takes in this version.");
    }

    /// <summary>
    /// The value a solved cell holds in the saved lens's units, read from the solved lens: the
    /// radius (infinity for a plane), the thickness, the clear semi-diameter (null when
    /// automatic), the conic, the TCE, a parameter; null for the material.
    /// </summary>
    public static double? SavedValue(SequentialLens lens, OpticsSurfaceSettings saved, SavedSolveCell cell, double perUnit)
    {
        ArgumentNullException.ThrowIfNull(lens);
        var s = lens.Surfaces[cell.Surface];
        var solve = cell.Solve;
        switch (solve.Column)
        {
            case SolveColumn.Curvature: return s.Curvature == 0 ? double.PositiveInfinity : 1 / s.Curvature / perUnit;
            case SolveColumn.Thickness: return s.Thickness / perUnit;
            case SolveColumn.SemiDiameter: return s.SemiDiameter is double sd ? sd / perUnit : null;
            case SolveColumn.Conic: return s.Conic;
            case SolveColumn.Tce: return s.ThermalExpansion;
            case SolveColumn.Parameter:
                return SurfaceTypes.TryGetParameter(s, solve.ParameterIndex, out double v)
                    ? v / Factor(saved, SolveColumn.Parameter, solve.ParameterIndex, perUnit)
                    : null;
            default: return null;
        }
    }
}
