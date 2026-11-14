using System.Collections;
using System.Globalization;
using System.Reflection;
using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>One line of a <see cref="FileComparator"/> listing: what, its value in each file,
/// and whether they differ.</summary>
public sealed record LensComparisonLine(string Item, string Left, string Right)
{
    public bool Differs => !string.Equals(Left, Right, StringComparison.Ordinal);
}

/// <summary>
/// File Comparator: a side-by-side listing of two lenses' data, surface by surface, by default
/// only where they differ (OpticStudio 2025 R2 help, File tab › Lens File › File Comparator,
/// plans/sources/optics/ansyshelp-op31/File_Comparator.html: "compares the systems surface by
/// surface … By default the checkbox 'Show Difference Only' is on").
/// <para>
/// Compared here: every field of the system data and every field of every saved surface, read
/// from the records themselves, so a field added later is compared without a change here.
/// Numbers are written in their shortest round-trip form, so two values that print alike are
/// equal to the bit. Configurations are not built (OP-30), so there is no configuration choice,
/// and the data categories of OpticStudio's Prescription Data report are not reproduced.
/// </para>
/// </summary>
public static class FileComparator
{
    public static IReadOnlyList<LensComparisonLine> Compare(OpticsSettings left, OpticsSettings right, bool differencesOnly = true)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var lines = new List<LensComparisonLine>();
        AddRecord(lines, "System", left.SystemData ?? new OpticsSystemSettings(), right.SystemData ?? new OpticsSystemSettings());
        var a = left.Surfaces ?? new List<OpticsSurfaceSettings>();
        var b = right.Surfaces ?? new List<OpticsSurfaceSettings>();
        lines.Add(new("Number of surfaces", Text(a.Count), Text(b.Count)));
        for (int j = 0; j < Math.Max(a.Count, b.Count); j++)
        {
            string name = $"Surface {j}";
            if (j >= a.Count) { lines.Add(new(name, "(none)", Describe(b[j]))); continue; }
            if (j >= b.Count) { lines.Add(new(name, Describe(a[j]), "(none)")); continue; }
            AddRecord(lines, name, a[j], b[j]);
        }
        return differencesOnly ? lines.Where(l => l.Differs).ToArray() : lines;
    }

    /// <summary>The listing as text, one line each: item, left | right.</summary>
    public static string Format(IReadOnlyList<LensComparisonLine> lines, string leftName, string rightName)
    {
        if (lines.Count == 0) return $"{leftName} and {rightName} hold the same lens data.";
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{"Item",-40} {leftName} | {rightName}");
        foreach (var l in lines) text.AppendLine($"{(l.Differs ? "* " : "  ")}{l.Item,-38} {l.Left} | {l.Right}");
        return text.ToString();
    }

    private static string Describe(OpticsSurfaceSettings s) =>
        $"{s.Type ?? SavedSurfaces.StandardType}, R {Text(s.Radius)}, t {(s.ThicknessIsInfinite ? "Infinity" : Text(s.Thickness))}, {SavedSurfaces.FormatMaterial(s)}";

    private static void AddRecord(List<LensComparisonLine> lines, string prefix, object left, object right)
    {
        foreach (var p in left.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            lines.Add(new($"{prefix}: {p.Name}", Text(p.GetValue(left)), Text(p.GetValue(right))));
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        string s => s,
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable list => "[" + string.Join("; ", list.Cast<object?>().Select(Text)) + "]",
        _ => "{" + string.Join(", ", value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .Select(p => $"{p.Name} {Text(p.GetValue(value))}")) + "}",
    };
}
