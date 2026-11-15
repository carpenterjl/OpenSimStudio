using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// OpticStudio's Validate ("Checks all tolerance operands for proper surface ranges and other
/// common errors", help page "Tolerance Data Editor Toolbar"): each row's integers and limits
/// against its operand's page, and the element rows against the nesting rules ("Nesting Rules for
/// Monte Carlo Analysis": "1) All element tilt and decenter tolerances must be nested. 2) The
/// outermost pair of surfaces in each nested group must be first"; a range that shares one or both
/// limits with an earlier one counts as nested). Rows marked Ignore are skipped. Each problem
/// names its row.
/// </summary>
public static class ToleranceValidation
{
    public static IReadOnlyList<string> Validate(ToleranceTable table, OpticsSettings lens)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(lens);
        var problems = new List<string>();
        int count = lens.Surfaces?.Count ?? 0;
        var elements = new List<(int Row, int First, int Last)>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i];
            if (r.Ignore) continue;
            var d = ToleranceOperandRegistry.Find(r.Code);
            string who = $"Tolerance row {i + 1} ({r.Code})";
            void P(string what) => problems.Add($"{who}: {what}");
            if (d is null) { P("not an operand this version knows."); continue; }
            void Surf(int n, string label, bool image = false)
            {
                int top = image ? count - 1 : count - 2;
                if (n < 1 || n > top) P($"{label} is {n}; the lens has surfaces 1 to {top}{(image ? "" : " between the object and the image")}.");
            }
            switch (d.Code)
            {
                case ToleranceOperandRegistry.Off: case "COMM": break;
                case "TWAV":
                    if (!(r.Min > 0) || !double.IsFinite(r.Min)) P($"the test wavelength (Min) is {N(r.Min)}; it must be a positive number of micrometers.");
                    break;
                case "MPVT": if (r.Int1 < 0 || r.Int1 > 4) P($"Pivot at (Int1) is {r.Int1}; it must be 0 to 4."); break;
                case "STAT":
                    if (r.Int1 < 0 || r.Int1 > 3) P($"the type (Int1) is {r.Int1}; it must be 0 (normal) to 3 (user defined).");
                    if (r.Int1 == 0 && (r.Int2 < 0 || r.Int2 > 10)) P($"the standard deviations (Int2) are {r.Int2}; the help caps them at 10.");
                    break;
                case "SAVE": if (r.Int1 < 0 || r.Int1 > 9999) P($"the file number (Int1) is {r.Int1}; it must be 0 to 9999."); break;
                case "SEED": break;
                case "COMP":
                    Surf(r.Int1, "Surf (Int1)", image: true);
                    if (r.Int2 < 0 || r.Int2 > 2) P($"the code (Int2) is {r.Int2}; it must be 0 (thickness), 1 (curvature) or 2 (conic).");
                    break;
                case "CPAR": Surf(r.Int1, "Surf (Int1)", image: true); break;
                case "CEDV": P("CEDV is deprecated and replaced by CPAR (help, Tolerance Control Operands Summary Table)."); break;
                case "CMCO": case "TMCO":
                {
                    var rows = lens.MultiConfiguration?.Operands?.Count ?? 0;
                    int configs = Math.Max(1, lens.MultiConfiguration?.ConfigurationCount ?? 1);
                    if (r.Int1 < 1 || r.Int1 > rows) P($"the operand row (Int1) is {r.Int1}; the multi-configuration table has {rows} row{(rows == 1 ? "" : "s")}.");
                    if (r.Int2 < 1 || r.Int2 > configs) P($"the configuration (Int2) is {r.Int2}; there {(configs == 1 ? "is 1 configuration" : $"are {configs} configurations")}.");
                    break;
                }
                default:
                    if (d.NotAppliedReason is { } why) P($"defined but not applied in this version: {why}");
                    if (d.IsElement)
                    {
                        Surf(r.Int1, "First surf (Int1)");
                        Surf(r.Int2, "Last surf (Int2)");
                        if (r.Int2 < r.Int1) P($"the last surface {r.Int2} comes before the first {r.Int1}.");
                        if (d.Int3 is not null && r.Int3 != r.Int1 && r.Int3 != r.Int2) P($"the roll surface (Int3) is {r.Int3}; it must be the first or the last surface.");
                        if (d.Code.StartsWith("TRL", StringComparison.Ordinal) && r.Int1 == r.Int2) P("Surf1 must not equal Surf2.");
                        elements.Add((i, r.Int1, r.Int2));
                    }
                    else if (d.Category == ToleranceCategory.Surface || d.Category == ToleranceCategory.UserDefined)
                    {
                        bool imageAllowed = d.Code is "TRAD" or "TCUR" or "TFRN" or "TCON" or "TSDI";
                        Surf(r.Int1, "Surf (Int1)", imageAllowed);
                        if (d.Code == "TTHI" && r.Int2 != r.Int1) Surf(r.Int2, "Adjust (Int2)");
                        if (d.Code == "TRAD" && r.Int2 is not (0 or 1)) P($"the code (Int2) is {r.Int2}; it must be 0 (lens units) or 1 (percent).");
                        if (d.Code == "TEXI" && (r.Int2 < 3 || r.Int2 > 37 || r.Int3 < 2 || r.Int3 > r.Int2))
                            P($"the terms are {r.Int3} to {r.Int2}; the maximum must be 3 to 37 and the minimum 2 to the maximum.");
                        if (d.Code == "TEZI" && (r.Int2 < 3 || r.Int2 > 231 || r.Int3 < 2 || r.Int3 > r.Int2))
                            P($"the terms are {r.Int3} to {r.Int2}; the maximum must be 3 to 231 and the minimum 2 to the maximum.");
                    }
                    if (r.Min > r.Max) P($"Min {N(r.Min)} is greater than Max {N(r.Max)}.");
                    if (d.NonNegative && (r.Min < 0 || r.Max < 0)) P("Min and Max must both be zero or more.");
                    if (d.MinIsNegativeMax && r.Min != -r.Max) P($"Min must be −Max ({N(-r.Max)}); the help sets it so.");
                    break;
            }
        }

        // The nesting rules.
        for (int a = 0; a < elements.Count; a++)
            for (int b = 0; b < a; b++)
            {
                var (row, f, l) = elements[a];
                var (prior, pf, pl) = elements[b];
                bool disjoint = l < pf || f > pl;
                bool inside = f >= pf && l <= pl;
                if (disjoint || inside) continue;
                bool contains = f <= pf && l >= pl;
                problems.Add(contains
                    ? $"Tolerance row {row + 1}: surfaces {f}–{l} contain those of row {prior + 1} ({pf}–{pl}); the outermost range must come first (nesting rule 2)."
                    : $"Tolerance row {row + 1}: surfaces {f}–{l} partly overlap those of row {prior + 1} ({pf}–{pl}); element ranges must be nested (nesting rule 1).");
            }
        return problems;
    }

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
