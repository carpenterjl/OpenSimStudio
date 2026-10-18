using System.Globalization;
using System.Text.Json;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;

namespace OpenSim.Cli;

public static partial class CliRunner
{
    private sealed record CoreSpec(double PostRadius, double WindowRadius, double OuterRadius, double PlateThickness,
        double WindowHeight, double CentreGap, double RelativePermeability = 2000);

    private static int Magnetics(string boardPath, Dictionary<string, string> o, TextWriter stdout)
    {
        if (!o.TryGetValue("nets", out var netList)) throw new UsageException("'magnetics' needs --nets P[,S...].");
        if (!o.TryGetValue("center", out var centerText)) throw new UsageException("'magnetics' needs --center X,Y (mm).");
        var nets = netList.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        var xy = centerText.Split(',').Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) * 1e-3).ToArray();
        if (xy.Length != 2) throw new UsageException("--center needs X,Y.");
        double angle = Number(o, "angle", 0) * Math.PI / 180;
        double radius = Number(o, "radius", 30) * 1e-3;
        double frequency = Number(o, "frequency", 0);
        string drive = o.GetValueOrDefault("drive") ?? nets[0];
        double strandDiameter = 0;
        int strandsPerTurn = 1;
        if (o.TryGetValue("strand", out var strandText))
        {
            var parts = strandText.Split(',');
            strandDiameter = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture) * 1e-3;
            if (parts.Length > 1) strandsPerTurn = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        int cutAngles = (int)Number(o, "cut-angles", 1);

        var board = Ipc2581Reader.Matches(boardPath) ? new Ipc2581Reader().Read(boardPath) : new PcbBoardReader().Read(boardPath);
        var stackup = BoardStackup.FromBoard(board);
        IReadOnlyList<WindingTurn> turns;
        IReadOnlyList<string> cutNotes = Array.Empty<string>();
        if (cutAngles > 1)
        {
            var averaged = PlanarWindingSection.CutAveraged(board, nets, new Point2(xy[0], xy[1]), radius, stackup, cutAngles);
            turns = averaged.Turns;
            cutNotes = averaged.Notes;
        }
        else turns = PlanarWindingSection.Cut(board, nets, new Point2(xy[0], xy[1]), angle, radius, stackup);
        AxisymmetricCore? core = null;
        if (o.TryGetValue("core", out var corePath))
        {
            var spec = JsonSerializer.Deserialize<CoreSpec>(File.ReadAllText(corePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The core file is empty.");
            core = new AxisymmetricCore(spec.PostRadius, spec.WindowRadius, spec.OuterRadius, spec.PlateThickness, spec.WindowHeight,
                spec.CentreGap, new MagneticMaterial("core", spec.RelativePermeability));
        }
        var result = PlanarMagneticsStudy.Run(new PlanarMagneticsInput
        {
            Turns = turns, Drive = drive, Windings = nets, FrequencyHz = frequency, Core = core,
            StrandDiameter = strandDiameter, StrandsPerTurn = strandsPerTurn
        });
        foreach (var (k, v) in result.Lines()) stdout.WriteLine($"{k}: {v}");

        string dir = OutDir(boardPath, o, "-magnetics");
        var report = result.Report($"{Path.GetFileNameWithoutExtension(boardPath)} — planar magnetics",
            $"({xy[0] * 1e3:g5}, {xy[1] * 1e3:g5}) mm, "
            + (cutAngles > 1 ? $"cut averaged over {cutAngles} angles" : $"cut at {angle * 180 / Math.PI:g4}°"),
            cutNotes);
        File.WriteAllText(Path.Combine(dir, "magnetics-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "magnetics-report.html"), report.ToHtml());
        stdout.WriteLine($"Results written to {dir}");
        return 0;
    }
}
