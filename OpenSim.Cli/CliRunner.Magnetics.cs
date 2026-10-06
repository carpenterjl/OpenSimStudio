using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.PostProcessing;
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

        var board = Ipc2581Reader.Matches(boardPath) ? new Ipc2581Reader().Read(boardPath) : new PcbBoardReader().Read(boardPath);
        var stackup = BoardStackup.FromBoard(board);
        var turns = PlanarWindingSection.Cut(board, nets, new Point2(xy[0], xy[1]), angle, radius, stackup);
        AxisymmetricCore? core = null;
        if (o.TryGetValue("core", out var corePath))
        {
            var spec = JsonSerializer.Deserialize<CoreSpec>(File.ReadAllText(corePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The core file is empty.");
            core = new AxisymmetricCore(spec.PostRadius, spec.WindowRadius, spec.OuterRadius, spec.PlateThickness, spec.WindowHeight,
                spec.CentreGap, new MagneticMaterial("core", spec.RelativePermeability));
        }
        var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { [drive] = 1 }, core, solidTurns: frequency > 0);
        var solver = new MagneticSolver2D();
        var solution = frequency > 0 ? solver.SolveTimeHarmonic(built.Model, frequency) : solver.SolveStatic(built.Model);

        var results = new List<(string, string)>();
        var l = built.Inductance(solution, drive);
        results.Add(($"L({drive})", $"{l.Real * 1e9:g5} nH"));
        if (frequency > 0)
            results.Add(($"R_ac({drive}) at {frequency:g4} Hz", $"{built.Resistance(solution, drive) * 1e3:g5} mΩ"));
        // Mutual inductance to the undriven windings (static only: their stranded turns carry no
        // current, so their flux linkage per ampere of the driven winding is M).
        if (frequency == 0)
            foreach (var other in nets.Where(n => n != drive && built.Windings.ContainsKey(n)))
            {
                var m = built.Windings[other].Aggregate(Complex.Zero, (s, t) => s + solution.FluxLinkage(t.Name));
                results.Add(($"M({drive},{other})", $"{m.Real * 1e9:g5} nH"));
            }
        foreach (var (k, v) in results) stdout.WriteLine($"{k}: {v}");

        string dir = OutDir(boardPath, o, "-magnetics");
        var report = new StudyReport
        {
            Title = $"{Path.GetFileNameWithoutExtension(boardPath)} — planar magnetics",
            Inputs = new List<(string, string)>
            {
                ("Windings", string.Join(", ", nets)), ("Driven", drive),
                ("Axis", $"({xy[0] * 1e3:g5}, {xy[1] * 1e3:g5}) mm, cut at {angle * 180 / Math.PI:g4}°"),
                ("Frequency", frequency > 0 ? $"{frequency:g5} Hz" : "static"),
                ("Core", core is null ? "none" : $"post {core.PostRadius * 1e3:g4} mm, gap {core.CentreGap * 1e3:g4} mm, µr {core.Material.RelativePermeability:g4}")
            },
            Results = results,
            Sections = new[]
            {
                new ReportSection("Turns")
                {
                    TableHeaders = new[] { "Winding", "Layer", "r0 (mm)", "r1 (mm)", "z0 (µm)", "z1 (µm)" },
                    TableRows = turns.Select(t => (IReadOnlyList<string>)new[]
                    {
                        t.Net, t.LayerOrder.ToString(CultureInfo.InvariantCulture), (t.R0 * 1e3).ToString("0.###", CultureInfo.InvariantCulture),
                        (t.R1 * 1e3).ToString("0.###", CultureInfo.InvariantCulture), (t.Z0 * 1e6).ToString("0.#", CultureInfo.InvariantCulture),
                        (t.Z1 * 1e6).ToString("0.#", CultureInfo.InvariantCulture)
                    }).ToList()
                },
                new ReportSection("Solve") { Lines = solution.Log.Concat(built.Notes).ToList() }
            },
            Assumptions = PlanarWindingSection.Assumptions.Concat(MagneticSolver2D.Assumptions).ToList()
        };
        File.WriteAllText(Path.Combine(dir, "magnetics-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "magnetics-report.html"), report.ToHtml());
        stdout.WriteLine($"Results written to {dir}");
        return 0;
    }
}
