using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The full-dialect acceptance gates: every real IPC-2581 export in the repo root —
/// Cadence Allegro (Large_Board_Example.xml), Altium (.cvg ×3), KiCad
/// (Breakout_Board.xml) — imports with ZERO warnings (nothing the file declares gets
/// skipped or approximated; informational notes for genuinely-absent data are allowed)
/// plus structural floors that pin what the import actually recovered. All soft-skip:
/// a checkout without the example boards stays green.
/// </summary>
public class Ipc2581ZeroWarningTests
{
    private static string? FindRepoFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static PcbBoard? ReadIfPresent(string fileName)
    {
        string? path = FindRepoFile(fileName);
        return path is null ? null : new Ipc2581Reader().Read(path);
    }

    private static void AssertZeroWarnings(PcbBoard board, string name) =>
        Assert.True(board.Warnings.Count == 0,
            $"{name} must import with zero warnings, got {board.Warnings.Count}:\n  "
            + string.Join("\n  ", board.Warnings));

    [Fact]
    public void CadenceBoard_ImportsWithZeroWarnings_AndFullStructure()
    {
        var board = ReadIfPresent("Large_Board_Example.xml");
        if (board is null) return;

        AssertZeroWarnings(board, "Large_Board_Example.xml");

        // 12 copper layers in DECLARED order (the file has no Stackup section).
        Assert.Equal(new[] { "TOP", "P02", "S03", "P04", "S05", "P06",
                             "P07", "S08", "P09", "S10", "P11", "BOTTOM" },
            board.Layers.OrderBy(l => l.CopperOrder).Select(l => l.FileName));

        // The routed copper actually landed (0 centerlines before the Polyline stage).
        Assert.True(board.TraceCenterlines.Count > 50_000,
            $"only {board.TraceCenterlines.Count} centerlines");
        Assert.True(board.Nets.Count(n => n.Name is not null) > 200,
            $"only {board.Nets.Count(n => n.Name is not null)} named nets");
        Assert.True(board.Pads.Count > 20_000, $"only {board.Pads.Count} pads");
        Assert.True(board.Pads.Count(p => p.ComponentRef is not null) > 7_000,
            $"only {board.Pads.Count(p => p.ComponentRef is not null)} component-pin pads");

        // Backdrills sever, and never masquerade as drills (32 backdrill holes measured).
        Assert.Contains(board.Notes, n => n.Contains("severed"));
        Assert.Equal(2300, board.Vias.Count);

        // The coincident-copper bridge rule restores inner-layer connections: via H1 at
        // (54.700, 107.227) carries pads on exactly TOP/S05/BOTTOM — the old
        // span-endpoint fallback reported {TOP, BOTTOM} and silently missed S05.
        var h1 = board.Nets
            .SelectMany(n => n.StitchingVias)
            .Single(b => Math.Abs(b.Via.Position.X - 54.700e-3) < 1e-9
                      && Math.Abs(b.Via.Position.Y - 107.227e-3) < 1e-9);
        Assert.Equal(new[] { 1, 5, 12 }, h1.Layers);

        // Through vias genuinely bridge more than the two span endpoints somewhere.
        Assert.Contains(board.Nets.SelectMany(n => n.StitchingVias), b => b.Layers.Count > 2);

        // Both nonplated slots became outline cutouts.
        Assert.Contains(board.Notes, n => n.Contains("2 routed slot(s)"));
    }

    [Fact]
    public void AltiumBoards_ImportWithZeroWarnings_AndTheirStructure()
    {
        foreach (var (file, copper, minNets, minPads) in new[]
        {
            ("Example_IPC-2581.cvg", 4, 100, 1500),
            ("Ohmmeter_PCB_Final.cvg", 4, 150, 2500),
            ("spider_connector_v1_1_pcb.cvg", 2, 20, 200),
        })
        {
            var board = ReadIfPresent(file);
            if (board is null) continue;

            AssertZeroWarnings(board, file);
            Assert.Equal(copper, board.Layers.Count);
            Assert.True(board.Nets.Count(n => n.Name is not null) >= minNets,
                $"{file}: only {board.Nets.Count(n => n.Name is not null)} named nets");
            Assert.True(board.Pads.Count >= minPads, $"{file}: only {board.Pads.Count} pads");
            Assert.True(board.TraceCenterlines.Count > 0, $"{file}: no centerlines");
        }
    }

    [Fact]
    public void OhmmeterBoard_PlatedSlots_AggregateAndBridge()
    {
        var board = ReadIfPresent("Ohmmeter_PCB_Final.cvg");
        if (board is null) return;

        // 224 per-layer occurrences aggregate to exactly 8 plated shield slots …
        Assert.Contains(board.Notes, n => n.Contains("8 routed slot(s)"));
        // … each subtracted from the outline and bridged by synthesized barrels
        // (1134 real plated holes measured before the slot stage; the rest are slots).
        Assert.True(board.Vias.Count(v => v.Plated) > 1134,
            $"only {board.Vias.Count(v => v.Plated)} plated holes — no slot barrels?");
        Assert.True(board.Outline[0].Holes.Count >= 8,
            $"only {board.Outline[0].Holes.Count} outline cutouts");
    }

    [Fact]
    public void KiCadBoard_ImportsWithZeroWarnings()
    {
        var board = ReadIfPresent("Breakout_Board.xml");
        if (board is null) return;
        AssertZeroWarnings(board, "Breakout_Board.xml");
        // The structural pins live in Ipc2581IntegrationTests (484 pads / 149 holes /
        // exact stackup) — this gate adds only the zero-warning contract.
    }

    /// <summary>The whole-board DC-nets report works on the Cadence import end to end:
    /// component-pin scoped rows with finite physical values (the report is an
    /// IPC-2581-specific feature — the pin identity comes from the PinRefs).</summary>
    [Fact]
    public void CadenceBoard_DcNetsReport_ProducesComponentPinRows()
    {
        var board = ReadIfPresent("Large_Board_Example.xml");
        if (board is null) return;

        var report = OpenSim.Rf.Si.DcNetEvaluator.Evaluate(board,
            new NetMeshOptions(),
            new OpenSim.Rf.Si.BoardCoupledOptions(), "Large_Board_Example.xml");

        Assert.True(report.Rows.Count > 0, "the Cadence board should produce DC-net rows");
        Assert.True(report.NetsEvaluated > 0);
        foreach (var row in report.Rows.Take(200))
        {
            Assert.DoesNotContain("mm)", row.PadA);              // component pins, never coordinates
            Assert.True(double.IsFinite(row.ResistanceOhms) && row.ResistanceOhms >= 0);
            Assert.True(double.IsFinite(row.CapacitanceFarads) && row.CapacitanceFarads > 0);
        }
    }
}
