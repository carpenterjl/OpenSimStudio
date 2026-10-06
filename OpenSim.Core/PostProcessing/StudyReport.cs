using System.Globalization;
using System.Net;
using System.Text;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Core.PostProcessing;

/// <summary>A block of a report: a table, a list of lines, or key–value pairs.</summary>
public sealed record ReportSection(string Heading)
{
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Key, string Value)> Pairs { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<string> TableHeaders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string>> TableRows { get; init; } = Array.Empty<IReadOnlyList<string>>();
}

/// <summary>
/// A study's report: what was asked, what came out, and what was assumed, in one document
/// a reader can file. The assumptions list is not decoration — every analysis in this
/// program prints the limits of what it did, and a result without them is a number without
/// its meaning. Renders as Markdown, HTML or plain text; no timestamp, so two reports of the
/// same study compare byte for byte.
/// </summary>
public sealed record StudyReport
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public IReadOnlyList<(string Key, string Value)> Inputs { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<(string Key, string Value)> Results { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<ReportSection> Sections { get; init; } = Array.Empty<ReportSection>();
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Formats a value for a report: general precision, invariant culture.</summary>
    public static string Number(double v, string format = "g6") => v.ToString(format, Ci);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Title);
        if (Subtitle is { Length: > 0 }) sb.AppendLine().AppendLine(Subtitle);
        if (Inputs.Count > 0) Pairs(sb, "Inputs", Inputs);
        if (Results.Count > 0) Pairs(sb, "Results", Results);
        foreach (var section in Sections)
        {
            sb.AppendLine().Append("## ").AppendLine(section.Heading).AppendLine();
            if (section.Pairs.Count > 0)
            {
                sb.AppendLine("| | |").AppendLine("|---|---|");
                foreach (var (k, v) in section.Pairs) sb.Append("| ").Append(Cell(k)).Append(" | ").Append(Cell(v)).AppendLine(" |");
            }
            if (section.TableHeaders.Count > 0)
            {
                sb.Append("| ").Append(string.Join(" | ", section.TableHeaders.Select(Cell))).AppendLine(" |");
                sb.Append('|').Append(string.Concat(section.TableHeaders.Select(_ => "---|"))).AppendLine();
                foreach (var row in section.TableRows)
                    sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
            }
            foreach (var line in section.Lines) sb.AppendLine(line.Length == 0 ? "" : line);
        }
        if (Assumptions.Count > 0)
        {
            sb.AppendLine().AppendLine("## Assumptions and limits").AppendLine();
            foreach (var a in Assumptions) sb.Append("- ").AppendLine(a);
        }
        if (Log.Count > 0)
        {
            sb.AppendLine().AppendLine("## Solver log").AppendLine().AppendLine("```");
            foreach (var l in Log) sb.AppendLine(l);
            sb.AppendLine("```");
        }
        return sb.ToString();

        static void Pairs(StringBuilder sb, string heading, IReadOnlyList<(string Key, string Value)> pairs)
        {
            sb.AppendLine().Append("## ").AppendLine(heading).AppendLine().AppendLine("| | |").AppendLine("|---|---|");
            foreach (var (k, v) in pairs) sb.Append("| ").Append(Cell(k)).Append(" | ").Append(Cell(v)).AppendLine(" |");
        }
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
    }

    public string ToHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(E(Title)).AppendLine("</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:60em;margin:2em auto;line-height:1.4}" +
                      "table{border-collapse:collapse;margin:.5em 0}td,th{border:1px solid #bbb;padding:.2em .6em;text-align:left}" +
                      "th{background:#eee}pre{background:#f5f5f5;padding:.6em;overflow-x:auto}h2{margin-top:1.5em}</style></head><body>");
        sb.Append("<h1>").Append(E(Title)).AppendLine("</h1>");
        if (Subtitle is { Length: > 0 }) sb.Append("<p>").Append(E(Subtitle)).AppendLine("</p>");
        if (Inputs.Count > 0) Table(sb, "Inputs", Inputs);
        if (Results.Count > 0) Table(sb, "Results", Results);
        foreach (var section in Sections)
        {
            sb.Append("<h2>").Append(E(section.Heading)).AppendLine("</h2>");
            if (section.Pairs.Count > 0)
            {
                sb.AppendLine("<table>");
                foreach (var (k, v) in section.Pairs) sb.Append("<tr><td>").Append(E(k)).Append("</td><td>").Append(E(v)).AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }
            if (section.TableHeaders.Count > 0)
            {
                sb.AppendLine("<table><tr>");
                foreach (var h in section.TableHeaders) sb.Append("<th>").Append(E(h)).Append("</th>");
                sb.AppendLine("</tr>");
                foreach (var row in section.TableRows)
                {
                    sb.Append("<tr>");
                    foreach (var c in row) sb.Append("<td>").Append(E(c)).Append("</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
            }
            foreach (var line in section.Lines) sb.Append("<p>").Append(E(line)).AppendLine("</p>");
        }
        if (Assumptions.Count > 0)
        {
            sb.AppendLine("<h2>Assumptions and limits</h2><ul>");
            foreach (var a in Assumptions) sb.Append("<li>").Append(E(a)).AppendLine("</li>");
            sb.AppendLine("</ul>");
        }
        if (Log.Count > 0)
        {
            sb.AppendLine("<h2>Solver log</h2><pre>");
            foreach (var l in Log) sb.AppendLine(E(l));
            sb.AppendLine("</pre>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();

        static void Table(StringBuilder sb, string heading, IReadOnlyList<(string Key, string Value)> pairs)
        {
            sb.Append("<h2>").Append(E(heading)).AppendLine("</h2><table>");
            foreach (var (k, v) in pairs) sb.Append("<tr><td>").Append(E(k)).Append("</td><td>").Append(E(v)).AppendLine("</td></tr>");
            sb.AppendLine("</table>");
        }
        static string E(string s) => WebUtility.HtmlEncode(s);
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title).AppendLine(new string('=', Title.Length));
        if (Subtitle is { Length: > 0 }) sb.AppendLine(Subtitle);
        if (Inputs.Count > 0) { sb.AppendLine().AppendLine("Inputs"); foreach (var (k, v) in Inputs) sb.Append("  ").Append(k).Append(": ").AppendLine(v); }
        if (Results.Count > 0) { sb.AppendLine().AppendLine("Results"); foreach (var (k, v) in Results) sb.Append("  ").Append(k).Append(": ").AppendLine(v); }
        foreach (var section in Sections)
        {
            sb.AppendLine().AppendLine(section.Heading);
            foreach (var (k, v) in section.Pairs) sb.Append("  ").Append(k).Append(": ").AppendLine(v);
            if (section.TableHeaders.Count > 0)
            {
                sb.Append("  ").AppendLine(string.Join(" | ", section.TableHeaders));
                foreach (var row in section.TableRows) sb.Append("  ").AppendLine(string.Join(" | ", row));
            }
            foreach (var line in section.Lines) sb.Append("  ").AppendLine(line);
        }
        if (Assumptions.Count > 0) { sb.AppendLine().AppendLine("Assumptions and limits"); foreach (var a in Assumptions) sb.Append("  - ").AppendLine(a); }
        if (Log.Count > 0) { sb.AppendLine().AppendLine("Solver log"); foreach (var l in Log) sb.Append("  ").AppendLine(l); }
        return sb.ToString();
    }

    /// <summary>
    /// The report of one solve: the project and body, the material, the conditions, the
    /// summary values, the min/max of every field, and the solver's log — whose WARNING
    /// and "not" lines are lifted into the assumptions.
    /// </summary>
    public static StudyReport FromSolve(string projectName, string bodyName, string analysis,
        SolveInput input, SolveOutput output, IEnumerable<string>? assumptions = null)
    {
        var inputs = new List<(string, string)>
        {
            ("Project", projectName), ("Body", bodyName), ("Analysis", analysis),
            ("Mesh", $"{input.Mesh.NodeCount} nodes, {input.Mesh.ElementCount} elements" +
                     (input.Mesh.IsQuadratic ? " (quadratic)" : "")),
            ("Material", input.Material.Name + (input.RegionMaterials is { Count: > 0 } r
                ? $" + {r.Count} region material(s): {string.Join(", ", r.Values.Select(m => m.Name).Distinct())}" : ""))
        };
        foreach (var bc in input.BoundaryConditions)
            inputs.Add(($"Condition '{bc.Name}'", $"{bc.GetType().Name} on {bc.ScopeSummary}: {Describe(bc)}"));
        if (input.TransientThermal is { } t)
            inputs.Add(("Transient", $"{t.Duration:g4} s in steps of {t.TimeStep:g4} s from {t.InitialTemperature:g4} K" +
                                     (t.PowerProfile is { } pp ? $", power {pp.Describe()}" : "")));
        if (input.HarmonicElectric is { } h)
            inputs.Add(("AC sweep", $"{h.MinFrequency:g4} Hz to {h.MaxFrequency:g4} Hz, {h.PointCount} points"));
        if (input.Modal is { } m) inputs.Add(("Modes", m.ModeCount.ToString(Ci)));
        if (input.Environment is { } env) inputs.Add(("Environment", env.Describe()));

        var results = new List<(string, string)>();
        if (output.Summary is { } summary)
            foreach (var (k, v) in summary.OrderBy(kv => kv.Key, StringComparer.Ordinal)) results.Add((k, Number(v)));

        var rows = new List<IReadOnlyList<string>>();
        foreach (var field in output.Fields)
        {
            if (field.Count == 0) continue;
            var s = FieldStatistics.Compute(field, input.Mesh);
            rows.Add(new[] { field.Name, field.Unit, Number(s.Min), Number(s.Max), Number(s.Mean) });
        }
        var sections = new List<ReportSection>
        {
            new("Fields") { TableHeaders = new[] { "Field", "Unit", "Min", "Max", "Mean" }, TableRows = rows }
        };
        if (output.Frames is { Count: > 1 } frames)
            sections.Add(new($"Frames ({output.FrameAxis})")
            {
                Lines = new[] { $"{frames.Count} frames from {frames[0].Label} to {frames[^1].Label}; the fields above are the default frame." }
            });

        var assumed = new List<string>(assumptions ?? Array.Empty<string>());
        assumed.AddRange(output.Log.Where(l =>
            l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) || l.Contains(" not ") || l.Contains("assum", StringComparison.OrdinalIgnoreCase)
            || l.Contains("neglect", StringComparison.OrdinalIgnoreCase) || l.Contains("zero-flux")));

        return new StudyReport
        {
            Title = $"{projectName} — {analysis}",
            Subtitle = $"Body '{bodyName}'",
            Inputs = inputs, Results = results, Sections = sections,
            Assumptions = assumed.Distinct().ToList(),
            Log = output.Log
        };
    }

    private static string Describe(BoundaryCondition bc) => bc switch
    {
        VoltagePotential v => $"{Number(v.Volts)} V",
        CurrentFlow c => $"{Number(c.TotalCurrent)} A",
        FixedTemperature t => $"{Number(t.Kelvin)} K",
        HeatFlux q => $"{Number(q.TotalPower)} W",
        Convection h => $"h = {Number(h.Coefficient)} W/(m²·K), ambient {Number(h.AmbientTemperature)} K",
        ForceLoad f => $"{f.TotalForce} N",
        PressureLoad p => $"{Number(p.Magnitude)} Pa",
        FixedSupport => "fixed",
        _ => ""
    };
}
