using System.Globalization;
using System.Text;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Rf.Emi;

/// <summary>
/// The numbers an EMI pre-compliance study of a hard-switched converter starts from — what the
/// command line's job file holds and the app's panel edits. SI units throughout. With a board,
/// <see cref="ConverterBoardReading.Apply"/> adds the copper's loop inductance, resistance and
/// switch-node capacitance to the hot-loop values given here.
/// </summary>
public sealed record EmiStudyInput
{
    public double Vin { get; init; }
    public double Iout { get; init; }
    public double FrequencyHz { get; init; }
    public double Duty { get; init; } = 0.5;
    public double RiseSeconds { get; init; } = 10e-9;
    public double FallSeconds { get; init; } = 10e-9;
    /// <summary>Switch node to earth (the common-mode path) [F].</summary>
    public double SwitchNodeCapacitance { get; init; }
    public double InputCapacitance { get; init; }
    public double InputEsr { get; init; }
    public double InputEsl { get; init; }
    public double FilterInductance { get; init; }
    public double FilterResistance { get; init; }
    public double FilterCapacitance { get; init; }
    public double FilterCapacitorEsr { get; init; }
    public double FilterCapacitorEsl { get; init; }
    public double YCapacitance { get; init; }
    /// <summary>The hot loop and its ringing (FU-41): package inductance and on-resistance of
    /// the switches, their output capacitance on the switch node, the diode's recovery charge.
    /// With a board, its copper adds to these.</summary>
    public double HotLoopInductance { get; init; }
    public double HotLoopResistance { get; init; }
    public double SwitchNodeToReturnCapacitance { get; init; }
    public double ReverseRecoveryCharge { get; init; }
    public double RecoverySoftness { get; init; } = 1;
    /// <summary>An isolated converter's interwinding path.</summary>
    public double InterwindingCapacitance { get; init; }
    public double SecondaryToEarthCapacitance { get; init; }
    public double SecondaryYCapacitance { get; init; }
    /// <summary>The radiating hot loop's area [m²]; 0 takes the board's when one was read.</summary>
    public double LoopArea { get; init; }
    /// <summary>The input cable's length for the common-mode radiated estimate [m].</summary>
    public double CableLength { get; init; }
    /// <summary>Measuring distance of the radiated estimate [m].</summary>
    public double Distance { get; init; } = 3;
}

/// <summary>One radiated line: the hot loop's field and the cable's at the measuring distance, and
/// the limit there.</summary>
public sealed record RadiatedLine(double FrequencyHz, double LoopDbuVm, double CableDbuVm, double? LimitDbuVm)
{
    public double StrongestDbuVm => Math.Max(LoopDbuVm, CableDbuVm);
    public bool LoopDominates => LoopDbuVm >= CableDbuVm;
}

/// <summary>A finished study: the converter it built, the conducted estimate, the comparative
/// radiated estimate (null when there was no loop area and no cable), and what the board added.</summary>
public sealed record EmiStudyResult(EmiStudyInput Spec, ConverterInput Input, ConductedEmissionResult Conducted,
    IReadOnlyList<RadiatedLine>? Radiated, EmiLimitLine? RadiatedLimit, ConverterBoardReading? Board)
{
    /// <summary>Any conducted line over its limit.</summary>
    public bool OverLimit => Conducted.Lines.Any(l => l.MarginDb < 0);

    /// <summary>The strongest radiated line, or null without a radiated estimate.</summary>
    public RadiatedLine? StrongestRadiated => Radiated is { Count: > 0 } lines ? lines.MaxBy(l => l.StrongestDbuVm) : null;

    /// <summary>"X dBµV/m at F MHz (hot loop|input cable)" for the strongest radiated line.</summary>
    public string? StrongestRadiatedText => StrongestRadiated is { } top
        ? $"{top.StrongestDbuVm:F1} dBµV/m at {top.FrequencyHz / 1e6:g4} MHz ({(top.LoopDominates ? "hot loop" : "input cable")})"
        : null;

    /// <summary>The radiated lines as CSV (frequency, loop, cable, limit; dBµV/m).</summary>
    public string RadiatedCsv()
    {
        var sb = new StringBuilder("frequency_Hz,loop_dBuV_m,cable_dBuV_m,limit_dBuV_m\n");
        foreach (var r in Radiated ?? Array.Empty<RadiatedLine>())
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{r.FrequencyHz},{r.LoopDbuVm:F2},{r.CableDbuVm:F2},{r.LimitDbuVm?.ToString("F2", CultureInfo.InvariantCulture) ?? ""}\n"));
        return sb.ToString();
    }

    /// <summary>The study as a report: the inputs, the margins, the strongest conducted lines, what
    /// was read from the board, and every assumption.</summary>
    public StudyReport Report(string title)
    {
        var spec = Spec;
        var conducted = Conducted;
        var sections = new List<ReportSection>
        {
            new("Conducted, strongest lines")
            {
                TableHeaders = new[] { "MHz", "Line (dBµV)", "Neutral (dBµV)", "DM (dBµV)", "CM (dBµV)", "Limit", "Margin (dB)" },
                TableRows = conducted.Lines.OrderByDescending(l => l.WorstDbuV).Take(15).OrderBy(l => l.FrequencyHz)
                    .Select(l => (IReadOnlyList<string>)new[]
                    {
                        (l.FrequencyHz / 1e6).ToString("0.###", CultureInfo.InvariantCulture), l.LineDbuV.ToString("F1", CultureInfo.InvariantCulture),
                        l.NeutralDbuV.ToString("F1", CultureInfo.InvariantCulture), l.DifferentialDbuV.ToString("F1", CultureInfo.InvariantCulture),
                        l.CommonDbuV.ToString("F1", CultureInfo.InvariantCulture), l.LimitDbuV?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
                        l.MarginDb?.ToString("F1", CultureInfo.InvariantCulture) ?? ""
                    }).ToList()
            }
        };
        var results = new List<(string, string)>();
        if (conducted.Worst is { } worst)
            results.Add(("Conducted margin", $"{worst.MarginDb:F1} dB at {worst.FrequencyHz / 1e6:g4} MHz"));
        if (Board is not null)
            sections.Add(new("Read from the board") { Lines = Board.Describe().Concat(Board.Notes).ToList() });
        if (StrongestRadiatedText is { } radiated)
            results.Add(("Radiated, strongest (comparative)", radiated));

        var input = Input;
        var limit = conducted.Limit;
        return new StudyReport
        {
            Title = title,
            Inputs = new List<(string, string)>
            {
                ("Converter", $"{spec.Vin:g4} V in, {spec.Iout:g4} A, {spec.FrequencyHz / 1e3:g4} kHz, duty {spec.Duty:g3}, edges {spec.RiseSeconds * 1e9:g3}/{spec.FallSeconds * 1e9:g3} ns"),
                ("Switch node to earth", $"{spec.SwitchNodeCapacitance * 1e12:g4} pF"),
                ("Input capacitor", $"{spec.InputCapacitance * 1e6:g4} µF, ESR {spec.InputEsr * 1e3:g4} mΩ, ESL {spec.InputEsl * 1e9:g4} nH"),
                ("Filter", spec.FilterInductance > 0 ? $"{spec.FilterInductance * 1e6:g4} µH, {spec.FilterCapacitance * 1e6:g4} µF" : "none"),
                ("Hot loop", input.HotLoopInductance > 0
                    ? $"{input.HotLoopInductance * 1e9:g4} nH, {input.HotLoopResistance * 1e3:g4} mΩ; switch node {input.SwitchNodeToReturnCapacitance * 1e12:g4} pF to the return"
                      + (Board is not null ? " (board copper included)" : "")
                    : "not given (no ringing)"),
                ("Diode recovery", spec.ReverseRecoveryCharge > 0 ? $"{spec.ReverseRecoveryCharge * 1e9:g4} nC, softness {spec.RecoverySoftness:g3}" : "none"),
                ("Interwinding path", spec.InterwindingCapacitance > 0
                    ? $"{spec.InterwindingCapacitance * 1e12:g4} pF; secondary {spec.SecondaryToEarthCapacitance * 1e12:g4} pF to earth, Y {spec.SecondaryYCapacitance * 1e12:g4} pF"
                    : "none"),
                ("Limit", limit is null ? "none" : $"{limit.Name} ({limit.Source})")
            },
            Results = results,
            Sections = sections,
            Assumptions = conducted.Assumptions.Concat(new[] { EmiStudy.RadiatedAssumption }).ToList()
        };
    }
}

/// <summary>
/// The EMI pre-compliance study the command line's <c>emi</c> job and the app's panel both run:
/// the converter from <see cref="EmiStudyInput"/> (a buck's trapezoidal switch current and node
/// voltage, the filter, the hot loop's ringing and recovery, the interwinding path), the conducted
/// estimate against a limit line, and — when a loop area, a cable or a board loop is known — the
/// comparative radiated estimate.
/// </summary>
public static class EmiStudy
{
    /// <summary>The one line the radiated estimate adds to the conducted assumptions.</summary>
    public const string RadiatedAssumption =
        "Radiated: small-loop and short-cable formulas for comparing layouts, not a chamber prediction; hot-loop current is the input capacitor's, cable current the switch-node capacitance's.";

    /// <summary>The converter the study solves, with the board's copper when a reading is given.
    /// Throws when a quantity the estimate cannot do without is missing.</summary>
    public static ConverterInput Converter(EmiStudyInput spec, ConverterBoardReading? board = null)
    {
        if (!(spec.Vin > 0 && spec.Iout > 0 && spec.FrequencyHz > 0 && spec.SwitchNodeCapacitance > 0 && spec.InputCapacitance > 0))
            throw new ArgumentException("The job needs vin, iout, frequencyHz, switchNodeCapacitance and inputCapacitance, all positive.", nameof(spec));
        var input = ConverterInput.Buck(spec.Vin, spec.Iout, spec.FrequencyHz, spec.Duty, spec.RiseSeconds, spec.FallSeconds,
            spec.SwitchNodeCapacitance, spec.InputCapacitance, spec.InputEsr, spec.InputEsl) with
        {
            FilterInductance = spec.FilterInductance, FilterResistance = spec.FilterResistance,
            FilterCapacitance = spec.FilterCapacitance, FilterCapacitorEsr = spec.FilterCapacitorEsr,
            FilterCapacitorEsl = spec.FilterCapacitorEsl, YCapacitance = spec.YCapacitance,
            HotLoopInductance = spec.HotLoopInductance, HotLoopResistance = spec.HotLoopResistance,
            SwitchNodeToReturnCapacitance = spec.SwitchNodeToReturnCapacitance,
            ReverseRecoveryCharge = spec.ReverseRecoveryCharge, RecoverySoftness = spec.RecoverySoftness,
            InterwindingCapacitance = spec.InterwindingCapacitance, SecondaryToEarthCapacitance = spec.SecondaryToEarthCapacitance,
            SecondaryYCapacitance = spec.SecondaryYCapacitance
        };
        return board is null ? input : board.Apply(input);
    }

    /// <summary>Whether the study has anything to radiate from: a loop area, a cable, or a board loop.</summary>
    public static bool HasRadiatedEstimate(EmiStudyInput spec, ConverterInput input) =>
        spec.LoopArea > 0 || spec.CableLength > 0 || input.HotLoopAreaM2 > 0;

    /// <summary>Runs the study: the conducted estimate against <paramref name="conductedLimit"/> (150 kHz
    /// to 30 MHz) and, when <see cref="HasRadiatedEstimate"/>, the radiated one against
    /// <paramref name="radiatedLimit"/> (30 MHz to 1 GHz).</summary>
    public static EmiStudyResult Run(EmiStudyInput spec, EmiLimitLine? conductedLimit, EmiLimitLine? radiatedLimit = null,
        ConverterBoardReading? board = null)
    {
        var input = Converter(spec, board);
        var conducted = ConductedEmission.Estimate(input, conductedLimit);
        IReadOnlyList<RadiatedLine>? radiated = null;
        if (HasRadiatedEstimate(spec, input))
            radiated = RadiatedEstimate.Converter(input, spec.LoopArea > 0 ? spec.LoopArea : Math.Max(input.HotLoopAreaM2, 1e-12),
                    Math.Max(spec.CableLength, 1e-9), spec.Distance, radiatedLimit)
                .Select(r => new RadiatedLine(r.FrequencyHz, r.LoopDbuVm, r.CableDbuVm, r.LimitDbuVm)).ToList();
        return new EmiStudyResult(spec, input, conducted, radiated, radiated is null ? null : radiatedLimit, board);
    }

    /// <summary>A built-in limit line by its <see cref="EmiLimits"/> property name, e.g.
    /// "ConductedClassBQuasiPeak"; throws naming the known ones.</summary>
    public static EmiLimitLine NamedLimit(string name) =>
        typeof(EmiLimits).GetProperty(name)?.GetValue(null) as EmiLimitLine
        ?? throw new ArgumentException($"Unknown limit line '{name}'. Known: {string.Join(", ", LimitNames)}.", nameof(name));

    /// <summary>The <see cref="EmiLimits"/> property names of the built-in lines.</summary>
    public static IReadOnlyList<string> LimitNames { get; } = typeof(EmiLimits).GetProperties()
        .Where(p => p.PropertyType == typeof(EmiLimitLine)).Select(p => p.Name).ToList();
}
