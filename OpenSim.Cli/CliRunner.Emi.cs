using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Core.PostProcessing;
using OpenSim.Rf.Emi;

namespace OpenSim.Cli;

public static partial class CliRunner
{
    private sealed record EmiSpec
    {
        public double Vin { get; init; }
        public double Iout { get; init; }
        public double FrequencyHz { get; init; }
        public double Duty { get; init; } = 0.5;
        public double RiseSeconds { get; init; } = 10e-9;
        public double FallSeconds { get; init; } = 10e-9;
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
        public string Limit { get; init; } = "ConductedClassBQuasiPeak";
        public string? LimitFile { get; init; }
        public double LoopArea { get; init; }
        public double CableLength { get; init; }
        public double Distance { get; init; } = 3;
        public string RadiatedLimit { get; init; } = "RadiatedClassB3m";
    }

    private static EmiLimitLine NamedLimit(string name) =>
        typeof(EmiLimits).GetProperty(name)?.GetValue(null) as EmiLimitLine
        ?? throw new InvalidDataException($"Unknown limit line '{name}'. Known: {string.Join(", ", typeof(EmiLimits).GetProperties().Where(p => p.PropertyType == typeof(EmiLimitLine)).Select(p => p.Name))}.");

    private static int Emi(string jobPath, Dictionary<string, string> o, TextWriter stdout)
    {
        var spec = JsonSerializer.Deserialize<EmiSpec>(File.ReadAllText(jobPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The EMI job is empty.");
        if (!(spec.Vin > 0 && spec.Iout > 0 && spec.FrequencyHz > 0 && spec.SwitchNodeCapacitance > 0 && spec.InputCapacitance > 0))
            throw new InvalidDataException("The job needs vin, iout, frequencyHz, switchNodeCapacitance and inputCapacitance, all positive.");
        var input = ConverterInput.Buck(spec.Vin, spec.Iout, spec.FrequencyHz, spec.Duty, spec.RiseSeconds, spec.FallSeconds,
            spec.SwitchNodeCapacitance, spec.InputCapacitance, spec.InputEsr, spec.InputEsl) with
        {
            FilterInductance = spec.FilterInductance, FilterResistance = spec.FilterResistance,
            FilterCapacitance = spec.FilterCapacitance, FilterCapacitorEsr = spec.FilterCapacitorEsr,
            FilterCapacitorEsl = spec.FilterCapacitorEsl, YCapacitance = spec.YCapacitance
        };
        var limit = spec.LimitFile is { } file
            ? EmiLimitLine.ParseCsv(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath)) ?? ".", file)),
                Path.GetFileNameWithoutExtension(file), "dBµV", $"loaded from {file}")
            : NamedLimit(spec.Limit);
        var conducted = ConductedEmission.Estimate(input, limit);
        foreach (var line in conducted.Describe().Split(Environment.NewLine).Take(3)) stdout.WriteLine(line);

        string dir = OutDir(jobPath, o, "-emi");
        File.WriteAllText(Path.Combine(dir, "conducted.csv"), conducted.ToCsv());
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

        if (spec.LoopArea > 0 || spec.CableLength > 0)
        {
            var radiatedLimit = NamedLimit(spec.RadiatedLimit);
            var radiated = RadiatedEstimate.Converter(input, Math.Max(spec.LoopArea, 1e-12), Math.Max(spec.CableLength, 1e-9),
                spec.Distance, radiatedLimit);
            var sb = new StringBuilder("frequency_Hz,loop_dBuV_m,cable_dBuV_m,limit_dBuV_m\n");
            foreach (var r in radiated)
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{r.FrequencyHz},{r.LoopDbuVm:F2},{r.CableDbuVm:F2},{r.LimitDbuVm?.ToString("F2", CultureInfo.InvariantCulture) ?? ""}\n"));
            File.WriteAllText(Path.Combine(dir, "radiated.csv"), sb.ToString());
            var top = radiated.MaxBy(r => Math.Max(r.LoopDbuVm, r.CableDbuVm));
            if (top != default)
            {
                string text = $"{Math.Max(top.LoopDbuVm, top.CableDbuVm):F1} dBµV/m at {top.FrequencyHz / 1e6:g4} MHz ({(top.LoopDbuVm >= top.CableDbuVm ? "hot loop" : "input cable")})";
                results.Add(("Radiated, strongest (comparative)", text));
                stdout.WriteLine("Radiated (comparative only): strongest " + text);
            }
        }

        var report = new StudyReport
        {
            Title = $"{Path.GetFileNameWithoutExtension(jobPath)} — EMI pre-compliance",
            Inputs = new List<(string, string)>
            {
                ("Converter", $"{spec.Vin:g4} V in, {spec.Iout:g4} A, {spec.FrequencyHz / 1e3:g4} kHz, duty {spec.Duty:g3}, edges {spec.RiseSeconds * 1e9:g3}/{spec.FallSeconds * 1e9:g3} ns"),
                ("Switch node to earth", $"{spec.SwitchNodeCapacitance * 1e12:g4} pF"),
                ("Input capacitor", $"{spec.InputCapacitance * 1e6:g4} µF, ESR {spec.InputEsr * 1e3:g4} mΩ, ESL {spec.InputEsl * 1e9:g4} nH"),
                ("Filter", spec.FilterInductance > 0 ? $"{spec.FilterInductance * 1e6:g4} µH, {spec.FilterCapacitance * 1e6:g4} µF" : "none"),
                ("Limit", $"{limit.Name} ({limit.Source})")
            },
            Results = results,
            Sections = sections,
            Assumptions = conducted.Assumptions.Concat(new[]
            {
                "Radiated: small-loop and short-cable formulas for comparing layouts, not a chamber prediction; hot-loop current is the input capacitor's, cable current the switch-node capacitance's."
            }).ToList()
        };
        File.WriteAllText(Path.Combine(dir, "emi-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "emi-report.html"), report.ToHtml());
        stdout.WriteLine($"Results written to {dir}");
        return conducted.Lines.Any(l => l.MarginDb < 0) ? 3 : 0;
    }
}
