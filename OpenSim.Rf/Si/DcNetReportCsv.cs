using System.Globalization;
using System.Text;

namespace OpenSim.Rf.Si;

/// <summary>
/// Renders a <see cref="DcNetReport"/> as CSV: a '#'-prefixed preamble carrying the
/// board name (once — not repeated per row), the sweep counts, per-net failure reasons,
/// and the stated assumptions (no timestamp — the output is deterministic, so the
/// composition gates can compare byte-for-byte), then an RFC-4180 table of the COMPLETE
/// rows only (both R and C computable; incomplete pairs are counted in the preamble).
/// Numeric cells use invariant round-trip formatting in consistent SI units (Ω, F, s —
/// friendly pF/ps belong in the UI summary line).
/// </summary>
public static class DcNetReportCsv
{
    public static string Write(DcNetReport report)
    {
        var sb = new StringBuilder();
        sb.Append("# OpenSim Studio — DC net evaluation\r\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"# board: {report.BoardName}\r\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"# nets: {report.NetsEvaluated} evaluated, {report.NetsSkipped} skipped (<2 pads), "
            + $"{report.NetsFailed} not computable\r\n");
        if (report.PairsOmitted > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"# omitted {report.PairsOmitted} pad pair(s) without a computable resistance (pad not on drawn copper, or disconnected pieces)\r\n");
        foreach (var note in report.FailureNotes)
            sb.Append(CultureInfo.InvariantCulture, $"# not computable: {note}\r\n");
        foreach (var assumption in report.Assumptions)
            sb.Append(CultureInfo.InvariantCulture, $"# {assumption}\r\n");

        sb.Append("Net,Pad A,Part A,Pad B,Part B,R (ohm),C_total (F),Tau (s),Note\r\n");
        foreach (var row in report.Rows)
        {
            sb.Append(Escape(row.Net)).Append(',');
            sb.Append(Escape(row.PadA)).Append(',');
            sb.Append(Escape(row.PartA)).Append(',');
            sb.Append(Escape(row.PadB)).Append(',');
            sb.Append(Escape(row.PartB)).Append(',');
            sb.Append(row.ResistanceOhms.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.CapacitanceFarads.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.TimeConstantSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Escape(row.Note)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC-4180 minimal quoting: only fields carrying a comma, quote, or line
    /// break are quoted (with embedded quotes doubled) — everything else stays verbatim.</summary>
    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
