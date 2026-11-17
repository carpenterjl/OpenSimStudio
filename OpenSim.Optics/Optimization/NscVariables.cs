// OP-88
using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.Editing;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization;

/// <summary>
/// Variables on non-sequential object cells (plan item OP-88; docs/Optics-Conventions.md §76),
/// OpticStudio's Variable flag on the Non-Sequential Component Editor's cells: an object's X, Y,
/// Z position (lens units), its tilts about X, Y, Z (degrees) and its parameters (their columns'
/// units), saved as <see cref="OpticsNscObjectSettings.Flags"/>. <see cref="LensVariables"/> lists
/// them after the surface cells, object by object and within an object in the order of its flags,
/// and reads and writes them through this class; everything else in the row is kept.
/// Parameters that take a whole number or a flag (pixel counts, "Is Volume?") are refused, as are
/// solves and pickups on object cells (not built).
/// </summary>
public static class NscVariables
{
    public const string X = "X", Y = "Y", Z = "Z", TiltX = "TiltX", TiltY = "TiltY", TiltZ = "TiltZ";

    /// <summary>The saved column of parameter <paramref name="number"/> (1-based): "Par3".</summary>
    public static string ParameterColumn(int number) => SavedSurfaces.ParameterColumn(number);

    /// <summary>The parameter number of a saved column ("Par3" → 3), or null.</summary>
    public static int? ParameterNumber(string? column) =>
        column is not null && column.StartsWith("Par", StringComparison.Ordinal)
        && int.TryParse(column.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 ? n : null;

    /// <summary>Appends the object cells flagged Variable to <paramref name="variables"/>, and
    /// what cannot be varied to <paramref name="problems"/> naming the object and cell.</summary>
    internal static void Find(OpticsSettings optics, List<LensVariable> variables, List<string> problems)
    {
        var rows = optics.NonSequential?.Objects;
        if (rows is null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row?.Flags is not { Count: > 0 } flags) continue;
            int number = i + 1;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in flags)
            {
                if (f is null) continue;
                string column = f.Column ?? "";
                string where = $"Object {number} ({row.Type ?? "no type"}), {column}";
                if (!string.Equals(f.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal))
                {
                    problems.Add($"{where}: the flag '{f.Kind}' is not built on object cells; only Variable is (solves and pickups on object cells are not built).");
                    continue;
                }
                if (!seen.Add(column)) { problems.Add($"{where}: the cell is flagged Variable twice."); continue; }
                if (Classify(row, column, out string? why, out string label) is not { } kind) { problems.Add($"{where}: {why}"); continue; }
                variables.Add(new LensVariable { Kind = kind, NscObject = number, Column = column, Label = $"Object {number} {label}" });
            }
        }
    }

    private static LensVariableKind? Classify(OpticsNscObjectSettings row, string column, out string? why, out string label)
    {
        why = null;
        label = column;
        switch (column)
        {
            case X: label = "X position"; return LensVariableKind.NscPosition;
            case Y: label = "Y position"; return LensVariableKind.NscPosition;
            case Z: label = "Z position"; return LensVariableKind.NscPosition;
            case TiltX: label = "tilt about X"; return LensVariableKind.NscTilt;
            case TiltY: label = "tilt about Y"; return LensVariableKind.NscTilt;
            case TiltZ: label = "tilt about Z"; return LensVariableKind.NscTilt;
        }
        if (ParameterNumber(column) is not int n)
        {
            why = $"the column '{column}' is not one the optimizer varies (X, Y, Z, TiltX, TiltY, TiltZ or a parameter Par n).";
            return null;
        }
        var d = NscObjectCatalogue.Default.Find(row.Type ?? "");
        if (d is null || !d.IsBuilt)
        {
            why = $"the object type '{row.Type}' is not built, so its parameters cannot be varied.";
            return null;
        }
        if (n > d.Parameters.Count && n > (row.Parameters?.Count ?? 0))
        {
            why = $"a {d.TypeName} has parameters 1 to {d.Parameters.Count}.";
            return null;
        }
        if (n <= d.Parameters.Count)
        {
            var p = d.Parameters[n - 1];
            label = $"{p.Name} (parameter {n})";
            if (p.Kind is NscParameterKind.Integer or NscParameterKind.Flag)
            {
                why = $"{p.Name} takes {(p.Kind == NscParameterKind.Flag ? "a flag" : "a whole number")} and cannot be varied continuously.";
                return null;
            }
        }
        else label = $"parameter {n}";
        return LensVariableKind.NscParameter;
    }

    private static OpticsNscObjectSettings RowOf(OpticsSettings optics, LensVariable v)
    {
        var rows = optics.NonSequential?.Objects;
        if (rows is null || v.NscObject < 1 || v.NscObject > rows.Count)
            throw new ArgumentException($"{v.Label}: the system has no non-sequential object {v.NscObject}.");
        return rows[v.NscObject - 1] ?? new OpticsNscObjectSettings();
    }

    /// <summary>The cell's value in the saved unit.</summary>
    internal static double Value(OpticsSettings optics, LensVariable v)
    {
        var row = RowOf(optics, v);
        return v.Column switch
        {
            X => row.X ?? 0, Y => row.Y ?? 0, Z => row.Z ?? 0,
            TiltX => row.TiltX ?? 0, TiltY => row.TiltY ?? 0, TiltZ => row.TiltZ ?? 0,
            _ => Parameter(row, ParameterNumber(v.Column) ?? throw new ArgumentException($"{v.Label}: not an object cell.")),
        };
    }

    private static double Parameter(OpticsNscObjectSettings row, int n)
    {
        if (row.Parameters is { } p && n <= p.Count) return p[n - 1];
        var d = NscObjectCatalogue.Default.Find(row.Type ?? "");
        return d is not null && n <= d.Parameters.Count ? d.Parameters[n - 1].Default : 0;
    }

    /// <summary>The record with the cell set to <paramref name="x"/>; a parameter list shorter
    /// than the parameter is padded with the type's defaults.</summary>
    internal static OpticsSettings Apply(OpticsSettings optics, LensVariable v, double x)
    {
        var row = RowOf(optics, v);
        OpticsNscObjectSettings changed = v.Column switch
        {
            X => row with { X = x }, Y => row with { Y = x }, Z => row with { Z = x },
            TiltX => row with { TiltX = x }, TiltY => row with { TiltY = x }, TiltZ => row with { TiltZ = x },
            _ => WithParameter(row, ParameterNumber(v.Column) ?? throw new ArgumentException($"{v.Label}: not an object cell."), x),
        };
        return With(optics, v.NscObject, changed);
    }

    /// <summary><paramref name="to"/> with the cell copied exactly from <paramref name="from"/>
    /// (a missing value stays missing).</summary>
    internal static OpticsSettings Copy(OpticsSettings from, OpticsSettings to, LensVariable v)
    {
        var source = RowOf(from, v);
        var row = RowOf(to, v);
        OpticsNscObjectSettings changed = v.Column switch
        {
            X => row with { X = source.X }, Y => row with { Y = source.Y }, Z => row with { Z = source.Z },
            TiltX => row with { TiltX = source.TiltX }, TiltY => row with { TiltY = source.TiltY }, TiltZ => row with { TiltZ = source.TiltZ },
            _ => WithParameter(row, ParameterNumber(v.Column)!.Value, Parameter(source, ParameterNumber(v.Column)!.Value)),
        };
        return With(to, v.NscObject, changed);
    }

    private static OpticsNscObjectSettings WithParameter(OpticsNscObjectSettings row, int n, double x)
    {
        var list = row.Parameters?.ToList() ?? new List<double>();
        while (list.Count < n) list.Add(Parameter(row, list.Count + 1));
        list[n - 1] = x;
        return row with { Parameters = list };
    }

    private static OpticsSettings With(OpticsSettings optics, int number, OpticsNscObjectSettings row)
    {
        var saved = optics.NonSequential!;
        var rows = saved.Objects!.ToList();
        rows[number - 1] = row;
        return optics with { NonSequential = saved with { Objects = rows } };
    }
}
