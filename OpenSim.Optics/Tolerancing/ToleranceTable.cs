using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.Tolerancing;

/// <summary>One row of the tolerance table (see <see cref="OpticsToleranceOperandSettings"/>).</summary>
public sealed record ToleranceOperandRow
{
    public string Code { get; init; } = ToleranceOperandRegistry.Off;
    public string Comment { get; init; } = "";
    public int Int1 { get; init; }
    public int Int2 { get; init; }
    public int Int3 { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public bool Ignore { get; init; }
    public bool DoNotAdjust { get; init; }
}

/// <summary>How a TIRR row's fringes become sag (docs/Optics-Conventions.md §36, OQ-10).</summary>
public enum IrregularityModel
{
    /// <summary>The default: the half-spherical, half-astigmatic shape is scaled so its
    /// peak-to-valley after removing piston, tilt and power (least squares over the aperture) is
    /// W·λ/2, the irregularity an ISO 10110 drawing and a test-plate fringe count mean.</summary>
    PowerRemoved = 0,

    /// <summary>OpticStudio's formula as its help page writes it: ΔZ = (λ/2)(W/2·ρ⁴ + W/2·ρ′y²).</summary>
    OpticStudio = 1,
}

/// <summary>The Monte Carlo distributions of STAT (OpticStudio 2025 R2 help, "STAT: Define Statistics").</summary>
public enum ToleranceStatistics
{
    Normal = 0,
    Uniform = 1,
    Parabolic = 2,
    UserDefined = 3,
}

/// <summary>The element pivot of MPVT (OpticStudio 2025 R2 help, "MPVT: Define Mechanical Pivot Point").</summary>
public enum TolerancePivot
{
    FrontVertex = 0,
    RearVertex = 1,
    FrontMechanicalEdge = 2,
    RearMechanicalEdge = 3,
    GeometricCenter = 4,
}

/// <summary>
/// What the control rows above a row set for it: the test wavelength (TWAV: "each operand defines
/// the test wavelength for the operands that follow"; none means the primary wavelength), the
/// pivot (MPVT: "each one acts on the operands following until the next MPVT"; none means the
/// front vertex), and the statistics and seed (STAT, SEED) the Monte Carlo item will read.
/// </summary>
public sealed record ToleranceRowContext
{
    /// <summary>The TWAV wavelength in micrometers, or null for the system's primary wavelength.</summary>
    public double? TestWavelengthMicrometers { get; init; }

    public TolerancePivot Pivot { get; init; } = TolerancePivot.FrontVertex;

    public ToleranceStatistics Statistics { get; init; } = ToleranceStatistics.Normal;

    /// <summary>STAT's Int2 for the normal distribution; null when no STAT row precedes.</summary>
    public int? StandardDeviations { get; init; }

    /// <summary>The SEED value in effect, or null when no SEED row precedes.</summary>
    public int? Seed { get; init; }
}

/// <summary>A compensator a COMP, CPAR or CMCO row defines (the optimization of compensators is
/// a later item). Min and Max bound its change ("General Comments About Min &amp; Max Values on
/// Compensators").</summary>
public sealed record ToleranceCompensator(int Row, string Code, int Int1, int Int2, double Min, double Max);

/// <summary>
/// The tolerance table: the rows in order and the irregularity model. Immutable.
/// </summary>
public sealed class ToleranceTable
{
    public ToleranceTable(IEnumerable<ToleranceOperandRow>? rows = null, IrregularityModel model = IrregularityModel.PowerRemoved)
    {
        Rows = (rows ?? Array.Empty<ToleranceOperandRow>()).ToArray();
        IrregularityModel = model;
    }

    public IReadOnlyList<ToleranceOperandRow> Rows { get; }

    public IrregularityModel IrregularityModel { get; }

    /// <summary>What the control rows before row <paramref name="index"/> (0-based) set for it.</summary>
    public ToleranceRowContext ContextAt(int index)
    {
        if (index < 0 || index > Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The table has rows 1 to {Rows.Count}.");
        var c = new ToleranceRowContext();
        for (int i = 0; i < index; i++)
        {
            var r = Rows[i];
            if (r.Ignore) continue;
            switch (r.Code.ToUpperInvariant())
            {
                case "TWAV": c = c with { TestWavelengthMicrometers = r.Min }; break;
                case "MPVT": c = c with { Pivot = (TolerancePivot)Math.Clamp(r.Int1, 0, 4) }; break;
                case "STAT": c = c with { Statistics = (ToleranceStatistics)Math.Clamp(r.Int1, 0, 3), StandardDeviations = r.Int2 }; break;
                case "SEED": c = c with { Seed = r.Int1 }; break;
            }
        }
        return c;
    }

    /// <summary>The compensators the COMP, CPAR, CEDV and CMCO rows define, in row order (1-based rows).</summary>
    public IReadOnlyList<ToleranceCompensator> Compensators() => Rows
        .Select((r, i) => (r, i))
        .Where(x => !x.r.Ignore && x.r.Code.ToUpperInvariant() is "COMP" or "CPAR" or "CEDV" or "CMCO")
        .Select(x => new ToleranceCompensator(x.i + 1, x.r.Code.ToUpperInvariant(), x.r.Int1, x.r.Int2, x.r.Min, x.r.Max))
        .ToArray();
}

/// <summary>The tolerance table to and from the files' <see cref="OpticsToleranceSettings"/>.</summary>
public static class ToleranceDataSettings
{
    public const string PowerRemovedName = "PowerRemoved";
    public const string OpticStudioName = "OpticStudio";

    /// <summary>The saved name of a model.</summary>
    public static string ModelName(IrregularityModel model) => model == IrregularityModel.OpticStudio ? OpticStudioName : PowerRemovedName;

    /// <summary>The settings of a table; null for an empty table with the default model.</summary>
    public static OpticsToleranceSettings? ToSettings(ToleranceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Rows.Count == 0 && table.IrregularityModel == IrregularityModel.PowerRemoved) return null;
        return new OpticsToleranceSettings
        {
            IrregularityModel = table.IrregularityModel == IrregularityModel.PowerRemoved ? null : ModelName(table.IrregularityModel),
            Operands = table.Rows.Count == 0 ? null : table.Rows.Select(r => new OpticsToleranceOperandSettings
            {
                Type = r.Code, Comment = r.Comment.Length == 0 ? null : r.Comment,
                Int1 = r.Int1, Int2 = r.Int2, Int3 = r.Int3, Min = r.Min, Max = r.Max, Ignore = r.Ignore, DoNotAdjust = r.DoNotAdjust,
            }).ToList(),
        };
    }

    /// <summary>The table of saved settings; null is an empty table. A code this version does not
    /// know becomes TOFF with the code kept in the comment, and is named in <paramref name="notes"/>;
    /// an unknown model name keeps the default and is named.</summary>
    public static ToleranceTable FromSettings(OpticsToleranceSettings? saved, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (saved is null) return new ToleranceTable();
        var model = IrregularityModel.PowerRemoved;
        if (saved.IrregularityModel is { } name)
        {
            if (string.Equals(name, OpticStudioName, StringComparison.OrdinalIgnoreCase)) model = IrregularityModel.OpticStudio;
            else if (!string.Equals(name, PowerRemovedName, StringComparison.OrdinalIgnoreCase))
                notes.Add($"Tolerance data: the irregularity model '{name}' is not one this version knows; '{PowerRemovedName}' is used.");
        }
        var rows = new List<ToleranceOperandRow>();
        int n = 0;
        foreach (var s in saved.Operands ?? new List<OpticsToleranceOperandSettings>())
        {
            n++;
            if (s is null) { rows.Add(new ToleranceOperandRow()); continue; }
            string code = string.IsNullOrWhiteSpace(s.Type) ? ToleranceOperandRegistry.Off : s.Type.Trim().ToUpperInvariant();
            string comment = s.Comment ?? "";
            if (ToleranceOperandRegistry.Find(code) is null)
            {
                notes.Add($"Tolerance data row {n}: the operand '{s.Type}' is not one this version knows; it is kept as TOFF with its code in the comment.");
                comment = $"{s.Type}: {comment}".TrimEnd(' ', ':');
                code = ToleranceOperandRegistry.Off;
            }
            rows.Add(new ToleranceOperandRow
            {
                Code = code, Comment = comment, Int1 = s.Int1, Int2 = s.Int2, Int3 = s.Int3, Min = s.Min, Max = s.Max,
                Ignore = s.Ignore, DoNotAdjust = s.DoNotAdjust,
            });
        }
        return new ToleranceTable(rows, model);
    }

    internal static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
