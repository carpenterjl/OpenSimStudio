using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;

namespace OpenSim.Optics.Tolerancing;

// OP-66: the Tolerance Summary (docs/Optics-Conventions.md §51). Source: OpticStudio 2025 R2 help,
// "Tolerance Summary" (plans/sources/optics/op-64-help/Tolerance_Summary.html): "a text listing of the
// tolerances which can be saved or printed ... it uses no mnemonics which makes the tolerances easier
// to understand for fabricators and others not familiar with OpticStudio terminology".

/// <summary>
/// The Tolerance Summary (plan item OP-66): the tolerance table as a <see cref="StudyReport"/> in plain
/// words, one line per tolerance (what, where, nominal, limits, unit), the compensators and the control
/// settings (test wavelength, pivot, statistics, seed), with no operand codes in the text. The layout
/// is this program's; the help shows none.
/// </summary>
public static class ToleranceSummary
{
    /// <summary>The summary of <paramref name="table"/> against the saved lens <paramref name="lens"/>.</summary>
    public static StudyReport Build(OpticsSettings lens, ToleranceTable table, string title = "Tolerance summary")
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(table);
        string unit = ProjectLens.LensUnitOf(lens.SystemData) switch
        {
            LensUnit.Millimeter => "mm",
            LensUnit.Centimeter => "cm",
            LensUnit.Inch => "in",
            LensUnit.Meter => "m",
            var u => u.ToString(),
        };
        var tolerances = new List<IReadOnlyList<string>>();
        var compensators = new List<string>();
        var controls = new List<string>();
        var ignored = new List<string>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i];
            var d = ToleranceOperandRegistry.Find(r.Code);
            if (d is null || d.Code == ToleranceOperandRegistry.Off)
            {
                if (r.Comment.Length > 0) controls.Add($"Note (row {i + 1}): {r.Comment}");
                continue;
            }
            if (r.Ignore) { ignored.Add($"Row {i + 1}: {Describe(r, d.Code, unit).What} of {Describe(r, d.Code, unit).Where} (ignored)"); continue; }
            switch (d.Code)
            {
                case "TWAV": controls.Add($"Fringes below row {i + 1} are counted at the test wavelength {N(r.Min)} µm."); continue;
                case "MPVT": controls.Add($"Element tilts below row {i + 1} pivot about the {Pivot(r.Int1)}."); continue;
                case "STAT": controls.Add($"Monte Carlo statistics below row {i + 1}: {Stat(r)}."); continue;
                case "SEED": controls.Add($"Random seed below row {i + 1}: {(r.Int1 == 0 ? "drawn from the clock" : r.Int1.ToString(CultureInfo.InvariantCulture))}."); continue;
                case "COMM": controls.Add(r.Comment.Length > 0 ? r.Comment : ""); continue;
                case "SAVE": continue;
                case "COMP" or "CPAR" or "CMCO" or "CEDV":
                    compensators.Add($"{Compensator(r, d.Code, unit)}, allowed change {N(r.Min)} to {N(r.Max)}{(r.Comment.Length > 0 ? $" ({r.Comment})" : "")}.");
                    continue;
            }
            var (what, where, u) = Describe(r, d.Code, unit);
            double? nominal = ToleranceNominal.ValueOf(lens, r);
            tolerances.Add(new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture), where, what,
                nominal is double v ? N(v) : "—",
                Limits(r.Min, r.Max), u, r.Comment,
            });
        }

        var sections = new List<ReportSection>
        {
            new("Tolerances")
            {
                TableHeaders = new[] { "Row", "Where", "What", "Nominal", "Tolerance", "Unit", "Comment" },
                TableRows = tolerances,
                Lines = tolerances.Count == 0 ? new[] { "There are no tolerances." } : Array.Empty<string>(),
            },
            new("Compensators") { Lines = compensators.Count == 0 ? new[] { "None." } : compensators },
        };
        if (controls.Count > 0) sections.Add(new ReportSection("Settings and notes") { Lines = controls });
        if (ignored.Count > 0) sections.Add(new ReportSection("Rows marked ignored") { Lines = ignored });
        return new StudyReport
        {
            Title = title,
            Inputs = new[] { ("Tolerance rows", table.Rows.Count.ToString(CultureInfo.InvariantCulture)), ("Lens units", unit) },
            Sections = sections,
            Assumptions = new[]
            {
                "Each tolerance is the change from nominal: the lowest and highest change allowed.",
                "Fringes are double-pass (Newton's rings) fringes over the clear semi-diameter at the test wavelength; irregularity is half spherical, half astigmatic (TIRR's model).",
                "Element tolerances act on the group of surfaces named as one rigid body.",
                "The layout is this program's (OpticStudio's help, \"Tolerance Summary\", describes the listing but shows none).",
            },
        };
    }

    /// <summary>The plain-text summary, one line each.</summary>
    public static IReadOnlyList<string> Lines(OpticsSettings lens, ToleranceTable table) =>
        Build(lens, table).ToText().Replace("\r", "").TrimEnd('\n').Split('\n');

    private static (string What, string Where, string Unit) Describe(ToleranceOperandRow r, string code, string lu)
    {
        string S(int j) => $"surface {j}";
        string G() => r.Int1 == r.Int2 ? $"element at surface {r.Int1}" : $"element of surfaces {r.Int1} to {r.Int2}";
        return code switch
        {
            "TRAD" => ("radius of curvature", S(r.Int1), r.Int2 == 1 ? "% of the radius" : lu),
            "TCUR" => ("curvature", S(r.Int1), $"1/{lu}"),
            "TFRN" => ("power (radius) as test-plate fringes", S(r.Int1), "fringes"),
            "TTHI" => ("thickness after the surface", S(r.Int1) + (r.Int2 != r.Int1 ? $" (taken up by the air space after surface {r.Int2})" : ""), lu),
            "TCON" => ("conic constant", S(r.Int1), ""),
            "TSDI" => ("semi-diameter", S(r.Int1), lu),
            "TSDX" => ("decenter in x", S(r.Int1), lu),
            "TSDY" => ("decenter in y", S(r.Int1), lu),
            "TSDR" => ("decenter in any direction", S(r.Int1), lu),
            "TSTX" => ("tilt about x", S(r.Int1), "degrees"),
            "TSTY" => ("tilt about y", S(r.Int1), "degrees"),
            "TIRX" => ("wedge (total indicator runout) in x", S(r.Int1), lu),
            "TIRY" => ("wedge (total indicator runout) in y", S(r.Int1), lu),
            "TIRR" => ("irregularity (half spherical, half astigmatic)", S(r.Int1), "fringes"),
            "TEXI" or "TEZI" => ($"Zernike irregularity, terms {r.Int3} to {r.Int2}", S(r.Int1), code == "TEXI" ? "fringes" : $"{lu} RMS"),
            "TPAR" => ($"parameter {r.Int2}", S(r.Int1), "the parameter's unit"),
            "TPAI" => ($"inverse of parameter {r.Int2}", S(r.Int1), "the parameter's inverse unit"),
            "TIND" => ("index of refraction of the glass after the surface", S(r.Int1), ""),
            "TABB" => ("Abbe number of the glass after the surface", S(r.Int1), ""),
            "TEDX" => ("decenter in x", G(), lu),
            "TEDY" => ("decenter in y", G(), lu),
            "TEDR" => ("decenter in any direction", G(), lu),
            "TETX" => ("tilt about x", G(), "degrees"),
            "TETY" => ("tilt about y", G(), "degrees"),
            "TETZ" => ("rotation about the axis", G(), "degrees"),
            "TARX" or "TARY" or "TARR" => ($"roll about the centre of curvature of surface {r.Int3}{(code == "TARX" ? " in x" : code == "TARY" ? " in y" : "")}", G(), "degrees"),
            "TRLX" or "TRLY" or "TRLR" => ($"roll about the centre of curvature of surface {r.Int3}, as runout{(code == "TRLX" ? " in x" : code == "TRLY" ? " in y" : "")}", G(), lu),
            "TUDX" => ("decenter in x of the coordinate break", S(r.Int1), lu),
            "TUDY" => ("decenter in y of the coordinate break", S(r.Int1), lu),
            "TUTX" => ("tilt about x of the coordinate break", S(r.Int1), "degrees"),
            "TUTY" => ("tilt about y of the coordinate break", S(r.Int1), "degrees"),
            "TUTZ" => ("tilt about z of the coordinate break", S(r.Int1), "degrees"),
            "TMCO" => ($"multi-configuration value, row {r.Int1}", $"configuration {r.Int2}", "the row's unit"),
            _ => (ToleranceOperandRegistry.Get(code).Name.Replace("Tolerance on ", "", StringComparison.Ordinal), S(r.Int1), ToleranceOperandRegistry.Get(code).Unit ?? ""),
        };
    }

    private static string Compensator(ToleranceOperandRow r, string code, string lu) => code switch
    {
        "COMP" => r.Int2 switch
        {
            0 => $"Thickness after surface {r.Int1} ({lu})",
            1 => $"Curvature of surface {r.Int1} (1/{lu})",
            2 => $"Conic constant of surface {r.Int1}",
            _ => $"Surface {r.Int1}, code {r.Int2}",
        },
        "CPAR" => $"Parameter {r.Int2} of surface {r.Int1}",
        "CMCO" => $"Multi-configuration row {r.Int1}, configuration {r.Int2}",
        _ => $"Extra data {r.Int2} of surface {r.Int1} (deprecated)",
    };

    private static string Pivot(int code) => code switch
    {
        1 => "rear vertex",
        2 => "front mechanical edge",
        3 => "rear mechanical edge",
        4 => "geometric centre",
        _ => "front vertex",
    };

    private static string Stat(ToleranceOperandRow r) => r.Int1 switch
    {
        1 => "uniform",
        2 => "parabolic",
        3 => $"user defined ({(r.Comment.Length > 0 ? r.Comment : "no file named")})",
        _ => $"normal, {r.Int2} standard deviations to the extremes",
    };

    private static string Limits(double min, double max) => min == -max && max >= 0 ? $"± {N(max)}" : $"{Signed(min)} / {Signed(max)}";

    private static string Signed(double v) => v > 0 ? "+" + N(v) : N(v);

    private static string N(double v) => StudyReport.Number(v, "g6");
}
