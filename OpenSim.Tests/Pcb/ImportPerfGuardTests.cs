using System.Diagnostics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Wall-clock guards in the SpiderBoardTests style: bounds are set at roughly an order
/// of magnitude above the measured post-optimization times (example board ≈ 0.4 s,
/// 12-layer Large_Board ≈ 0.8 s in Release) so slow CI can never flake, while a return
/// of the O(polarity-flips × image-vertices) union pathology or a serialization of the
/// per-layer/per-net parallel import — both formerly tens-of-seconds-class — still trips
/// them. If one fires, look at LayerImageBuilder's suffix composition and the
/// Parallel.For assembly in PcbBoardReader / Ipc2581BoardBuilder first.
/// </summary>
public class ImportPerfGuardTests
{
    [Fact]
    public void ExampleBoard_FullImport_StaysFast()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var sw = Stopwatch.StartNew();
        var board = new PcbBoardReader().Read(path);
        sw.Stop();
        Assert.True(board.Islands.Count > 500, "sanity: the real board should import fully");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"example_board.zip import took {sw.Elapsed.TotalSeconds:F1} s (guard 15 s) — " +
            "the copper-image composition or the parallel layer fan-out has regressed");
    }

    [Fact]
    public void LargeIpcBoard_FullImport_StaysFast()
    {
        // Soft-skip: the 12-layer, ~21k-pad real export lives at the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null && path is null)
        {
            string candidate = Path.Combine(dir.FullName, "Large_Board_Example.xml");
            if (File.Exists(candidate)) path = candidate;
            dir = dir.Parent;
        }
        if (path is null) return;

        var sw = Stopwatch.StartNew();
        var board = new Ipc2581Reader().Read(path);
        sw.Stop();
        Assert.True(board.Islands.Count > 5000, "sanity: the large board should import fully");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Large_Board_Example.xml import took {sw.Elapsed.TotalSeconds:F1} s (guard 30 s) — " +
            "the per-net stroke+union stage or its parallel fan-out has regressed");
    }
}
