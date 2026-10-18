using System.Text.Json;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using OpenSim.Rf.Emi;

namespace OpenSim.Cli;

public static partial class CliRunner
{
    /// <summary>The job's keys beyond the study's own numbers (<see cref="EmiStudyInput"/>, read from
    /// the same JSON): the board to read and the limit lines.</summary>
    private sealed record EmiSpec
    {
        /// <summary>The board to read the hot loop and the switch node from.</summary>
        public EmiBoardSpec? Board { get; init; }
        public string Limit { get; init; } = "ConductedClassBQuasiPeak";
        public string? LimitFile { get; init; }
        public string RadiatedLimit { get; init; } = "RadiatedClassB3m";
    }

    private sealed record EmiBoardSpec
    {
        /// <summary>The board file (IPC-2581 or a Gerber set), relative to the job.</summary>
        public string Path { get; init; } = "";
        public string InputNet { get; init; } = "";
        public string ReturnNet { get; init; } = "";
        public string SwitchNodeNet { get; init; } = "";
        public string InputCapacitor { get; init; } = "";
        public string HighSideSwitch { get; init; } = "";
        /// <summary>The low-side switch or diode; the same part as the high side for an integrated converter.</summary>
        public string LowSideSwitch { get; init; } = "";
    }

    private static ConverterBoardReading ReadConverterBoard(EmiBoardSpec spec, string jobPath, TextWriter stdout)
    {
        string boardPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(jobPath)) ?? ".", spec.Path);
        var board = Ipc2581Reader.Matches(boardPath) ? new Ipc2581Reader().Read(boardPath) : new PcbBoardReader().Read(boardPath);
        stdout.WriteLine($"Board: {board.Nets.Count} nets, {board.Islands.Count} copper islands" +
                         (board.Warnings.Count > 0 ? $", {board.Warnings.Count} import warning(s)." : "."));
        CopperNet Net(string name, string role)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"The board section needs the {role} net's name.");
            return board.Nets.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"The board has no net '{name}'. Named nets: "
                    + string.Join(", ", board.Nets.Where(n => n.Name is not null).Select(n => n.Name)) + ".");
        }
        var parts = new ConverterParts(spec.InputCapacitor, spec.HighSideSwitch,
            string.IsNullOrWhiteSpace(spec.LowSideSwitch) ? spec.HighSideSwitch : spec.LowSideSwitch);
        if (string.IsNullOrWhiteSpace(parts.InputCapacitor) || string.IsNullOrWhiteSpace(parts.HighSideSwitch))
            throw new InvalidDataException("The board section needs inputCapacitor and highSideSwitch (and lowSideSwitch unless the part is both).");
        var reading = ConverterBoard.Read(board, Net(spec.InputNet, "input"), Net(spec.ReturnNet, "return"), Net(spec.SwitchNodeNet, "switch-node"), parts);
        foreach (string line in reading.Describe()) stdout.WriteLine(line);
        return reading;
    }

    private static int Emi(string jobPath, Dictionary<string, string> o, TextWriter stdout)
    {
        string text = File.ReadAllText(jobPath);
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var study = JsonSerializer.Deserialize<EmiStudyInput>(text, json) ?? throw new InvalidDataException("The EMI job is empty.");
        var spec = JsonSerializer.Deserialize<EmiSpec>(text, json) ?? throw new InvalidDataException("The EMI job is empty.");
        // Fail on the numbers before reading a board: a missing one is the cheaper mistake to report.
        EmiStudy.Converter(study);
        ConverterBoardReading? reading = spec.Board is { } boardSpec ? ReadConverterBoard(boardSpec, jobPath, stdout) : null;
        var input = EmiStudy.Converter(study, reading);
        foreach (string note in input.SourceNotes().Take(2)) stdout.WriteLine(note);
        var limit = spec.LimitFile is { } file
            ? EmiLimitLine.ParseCsv(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath)) ?? ".", file)),
                Path.GetFileNameWithoutExtension(file), "dBµV", $"loaded from {file}")
            : EmiStudy.NamedLimit(spec.Limit);
        // The radiated limit is looked up only when there is a radiated estimate to hold against it.
        var radiatedLimit = EmiStudy.HasRadiatedEstimate(study, input) ? EmiStudy.NamedLimit(spec.RadiatedLimit) : null;
        var result = EmiStudy.Run(study, limit, radiatedLimit, reading);
        foreach (var line in result.Conducted.Describe().Split(Environment.NewLine).Take(3)) stdout.WriteLine(line);

        string dir = OutDir(jobPath, o, "-emi");
        File.WriteAllText(Path.Combine(dir, "conducted.csv"), result.Conducted.ToCsv());
        if (result.Radiated is not null)
        {
            File.WriteAllText(Path.Combine(dir, "radiated.csv"), result.RadiatedCsv());
            if (result.StrongestRadiatedText is { } strongest)
                stdout.WriteLine("Radiated (comparative only): strongest " + strongest);
        }
        var report = result.Report($"{Path.GetFileNameWithoutExtension(jobPath)} — EMI pre-compliance");
        File.WriteAllText(Path.Combine(dir, "emi-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "emi-report.html"), report.ToHtml());
        stdout.WriteLine($"Results written to {dir}");
        return result.OverLimit ? 3 : 0;
    }
}
