using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization;

// OP-57: the lens variables the local optimizer drives.

/// <summary>The type of a lens variable, which sets its scale and derivative increment
/// (<see cref="LocalOptimizationOptions.Kinds"/>).</summary>
public enum LensVariableKind
{
    /// <summary>A Radius cell, varied as its curvature 1/R (1/lens unit; 0 is a plane), so a
    /// surface may pass through flat.</summary>
    Curvature,
    /// <summary>A Thickness cell (lens units).</summary>
    Thickness,
    /// <summary>A Conic cell.</summary>
    Conic,
    /// <summary>A Semi-Diameter cell (lens units).</summary>
    SemiDiameter,
    /// <summary>A TCE cell (1e-6 per °C).</summary>
    Tce,
    /// <summary>A surface type's parameter or extra-data cell ("Par n"), in its column's unit.</summary>
    Parameter,
    /// <summary>A numeric cell of the multi-configuration table, in its operand's unit.</summary>
    Configuration,
}

/// <summary>One variable cell: a cell of the Lens Data Editor flagged Variable
/// (<see cref="Surface"/> and <see cref="Column"/>) or one of the multi-configuration table
/// (<see cref="Cell"/>).</summary>
public sealed record LensVariable
{
    public required LensVariableKind Kind { get; init; }

    /// <summary>The surface (0-based), or −1 for a multi-configuration cell.</summary>
    public int Surface { get; init; } = -1;

    /// <summary>The saved column key ("Radius", "Thickness", "Par3", …), or null for a
    /// multi-configuration cell.</summary>
    public string? Column { get; init; }

    /// <summary>The multi-configuration cell, or null for a surface cell.</summary>
    public MultiConfigCell? Cell { get; init; }

    /// <summary>"Surface 2 curvature", "Configuration 2, operand 1 (THIC)".</summary>
    public required string Label { get; init; }
}

/// <summary>
/// The variables of an optics record and their values (docs/Optics-Conventions.md §35):
/// surface cells flagged Variable (<see cref="OpticsCellFlagSettings.Kind"/>) in surface order
/// and, within a surface, in the order of its flags, then multi-configuration cells flagged Variable
/// (<see cref="MultiConfiguration.Variables"/>) row by row. Values are in the saved units (lens
/// units; a radius as its curvature). Writing values back changes only those cells: flags,
/// solves and pickups are kept, and are evaluated again by the settings-to-lens path
/// (<see cref="Analysis.ProjectLens"/>, <see cref="MultiConfiguration.SettingsFor"/>) whenever
/// the lens is built.
/// </summary>
public static class LensVariables
{
    /// <summary>The variables of <paramref name="optics"/>. Throws <see cref="ArgumentException"/>
    /// naming every variable cell this version cannot vary (a material, an automatic
    /// semi-diameter, an infinite thickness, an integer parameter, a text or infinite
    /// configuration cell …).</summary>
    public static IReadOnlyList<LensVariable> Find(OpticsSettings optics)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var variables = new List<LensVariable>();
        var problems = new List<string>();
        var surfaces = optics.Surfaces ?? new List<OpticsSurfaceSettings>();
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            if (s?.Flags is not { Count: > 0 } flags) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in flags)
            {
                if (f is null || !string.Equals(f.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal)) continue;
                string column = f.Column ?? "";
                string where = $"{SavedSurfaces.Where(j, surfaces.Count)}, {column}";
                if (!seen.Add(column)) { problems.Add($"{where}: the cell is flagged Variable twice."); continue; }
                if (Classify(s, column, out string? why) is not { } kind) { problems.Add($"{where}: {why}"); continue; }
                variables.Add(new LensVariable
                {
                    Kind = kind, Surface = j, Column = column,
                    Label = $"Surface {j} {(kind == LensVariableKind.Curvature ? "curvature" : column)}",
                });
            }
        }
        var table = optics.MultiConfiguration;
        foreach (var cell in MultiConfiguration.Variables(optics))
        {
            var row = table!.Operands![cell.Operand - 1]!;
            var operand = MultiConfigOperands.Find(row.Type);
            string label = $"Configuration {cell.Configuration}, operand {cell.Operand} ({row.Type})";
            if (operand is null || !operand.IsSupported) { problems.Add($"{label}: the operand '{row.Type}' is not one this version applies, so it cannot be varied."); continue; }
            if (operand.Kind != MultiConfigValueKind.Number) { problems.Add($"{label}: only real-number cells can be variables; this operand holds {(operand.Kind == MultiConfigValueKind.Integer ? "a whole number" : "a text")}."); continue; }
            if (double.IsInfinity(MultiConfiguration.ValueOf(optics, cell))) { problems.Add($"{label}: an infinite value cannot be varied."); continue; }
            variables.Add(new LensVariable { Kind = LensVariableKind.Configuration, Cell = cell, Label = label });
        }
        if (problems.Count > 0)
            throw new ArgumentException("These variable cells cannot be optimized:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return variables;
    }

    private static LensVariableKind? Classify(OpticsSurfaceSettings s, string column, out string? why)
    {
        why = null;
        switch (column)
        {
            case SavedSurfaces.RadiusColumn: return LensVariableKind.Curvature;
            case SavedSurfaces.ThicknessColumn:
                if (s.ThicknessIsInfinite) { why = "an infinite thickness cannot be varied; give it a finite value first."; return null; }
                return LensVariableKind.Thickness;
            case SavedSurfaces.ConicColumn: return LensVariableKind.Conic;
            case SavedSurfaces.TceColumn: return LensVariableKind.Tce;
            case SavedSurfaces.SemiDiameterColumn:
                if (s.SemiDiameter is null) { why = "the semi-diameter is automatic; enter a value to vary it."; return null; }
                return LensVariableKind.SemiDiameter;
            case SavedSurfaces.MaterialColumn:
                why = "glass variables (model glasses and substitution) are plan item OP-59, not built.";
                return null;
        }
        if (SavedSurfaces.ParameterNumber(column) is int n)
        {
            if (SurfaceTypes.Find(s.Type)?.Parameter(n) is { IsInteger: true } info)
            {
                why = $"{SurfaceTypeLayout.Label(info)} takes a whole number and cannot be varied continuously.";
                return null;
            }
            return LensVariableKind.Parameter;
        }
        why = $"the column '{column}' is not one the optimizer varies (radius, thickness, conic, semi-diameter, TCE, parameters).";
        return null;
    }

    /// <summary>The variables' values in <paramref name="optics"/>, in the saved units.</summary>
    public static double[] Values(OpticsSettings optics, IReadOnlyList<LensVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(variables);
        var values = new double[variables.Count];
        for (int i = 0; i < variables.Count; i++)
        {
            var v = variables[i];
            if (v.Cell is { } cell) { values[i] = MultiConfiguration.ValueOf(optics, cell); continue; }
            var s = SurfaceAt(optics, v);
            values[i] = v.Kind switch
            {
                LensVariableKind.Curvature => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value,
                LensVariableKind.Thickness => s.Thickness,
                LensVariableKind.Conic => s.Conic,
                LensVariableKind.Tce => s.Tce,
                LensVariableKind.SemiDiameter => s.SemiDiameter ?? 0,
                LensVariableKind.Parameter => SavedSurfaces.Value(s, SavedSurfaces.ParameterNumber(v.Column)!.Value),
                _ => throw new ArgumentException($"{v.Label}: not a surface variable."),
            };
        }
        return values;
    }

    /// <summary>The record with the variables set to <paramref name="values"/> (saved units);
    /// everything else, flags included, unchanged. A curvature of 0 writes a plane.</summary>
    public static OpticsSettings Apply(OpticsSettings optics, IReadOnlyList<LensVariable> variables, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != variables.Count) throw new ArgumentException($"There are {variables.Count} variables; {values.Count} values were given.", nameof(values));
        var result = optics;
        for (int i = 0; i < variables.Count; i++)
        {
            var v = variables[i];
            double x = values[i];
            if (!double.IsFinite(x)) throw new ArgumentException($"{v.Label}: the value {x.ToString(CultureInfo.InvariantCulture)} is not finite.", nameof(values));
            if (v.Cell is { } cell) { result = MultiConfiguration.WithValue(result, cell, x); continue; }
            // Read the surface from the record as edited so far, so two variables on one surface both apply.
            var s = SurfaceAt(result, v);
            var surfaces = result.Surfaces!.ToList();
            surfaces[v.Surface] = v.Kind switch
            {
                LensVariableKind.Curvature => s with { Radius = x == 0 ? null : 1 / x },
                LensVariableKind.Thickness => s with { Thickness = x },
                LensVariableKind.Conic => s with { Conic = x },
                LensVariableKind.Tce => s with { Tce = x },
                LensVariableKind.SemiDiameter => s with { SemiDiameter = x },
                LensVariableKind.Parameter => WithParameter(s, SavedSurfaces.ParameterNumber(v.Column)!.Value, x),
                _ => throw new ArgumentException($"{v.Label}: not a surface variable."),
            };
            result = result with { Surfaces = surfaces };
        }
        return result;
    }

    /// <summary>The record <paramref name="to"/> with the variables' cells copied exactly from
    /// <paramref name="from"/> (the saved radius, not 1/curvature, so undo restores the digits).</summary>
    public static OpticsSettings CopyCells(OpticsSettings from, OpticsSettings to, IReadOnlyList<LensVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(variables);
        var result = to;
        foreach (var v in variables)
        {
            if (v.Cell is { } cell) { result = MultiConfiguration.WithValue(result, cell, MultiConfiguration.ValueOf(from, cell)); continue; }
            var source = SurfaceAt(from, v);
            var s = SurfaceAt(result, v);
            var surfaces = result.Surfaces!.ToList();
            surfaces[v.Surface] = v.Kind switch
            {
                LensVariableKind.Curvature => s with { Radius = source.Radius },
                LensVariableKind.Thickness => s with { Thickness = source.Thickness },
                LensVariableKind.Conic => s with { Conic = source.Conic },
                LensVariableKind.Tce => s with { Tce = source.Tce },
                LensVariableKind.SemiDiameter => s with { SemiDiameter = source.SemiDiameter },
                LensVariableKind.Parameter => WithParameter(s, SavedSurfaces.ParameterNumber(v.Column)!.Value,
                    SavedSurfaces.Value(source, SavedSurfaces.ParameterNumber(v.Column)!.Value)),
                _ => throw new ArgumentException($"{v.Label}: not a surface variable."),
            };
            result = result with { Surfaces = surfaces };
        }
        return result;
    }

    private static OpticsSurfaceSettings SurfaceAt(OpticsSettings optics, LensVariable v)
    {
        var surfaces = optics.Surfaces;
        if (surfaces is null || v.Surface < 0 || v.Surface >= surfaces.Count)
            throw new ArgumentException($"{v.Label}: the system has no surface {v.Surface}.");
        return surfaces[v.Surface] ?? new OpticsSurfaceSettings();
    }

    private static OpticsSurfaceSettings WithParameter(OpticsSurfaceSettings s, int number, double value)
    {
        var entry = new OpticsSurfaceParameterSettings { Number = number, Value = value };
        if (SavedSurfaces.IsExtraData(s, number))
        {
            var list = (s.ExtraData ?? new List<OpticsSurfaceParameterSettings>()).Where(p => p.Number != number).ToList();
            list.Add(entry);
            return s with { ExtraData = list.OrderBy(p => p.Number).ToList() };
        }
        var parameters = (s.Parameters ?? new List<OpticsSurfaceParameterSettings>()).Where(p => p.Number != number).ToList();
        parameters.Add(entry);
        return s with { Parameters = parameters.OrderBy(p => p.Number).ToList() };
    }
}
