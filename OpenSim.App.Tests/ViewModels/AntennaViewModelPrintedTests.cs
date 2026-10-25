using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// FU-36's app side (wired under FU-47, pinned here): the antenna panel's printed-antenna mode
/// solves the antenna net and its ground pour on the board's dielectric as an ungrounded slab
/// (<c>PrintedAntennaBuilder.SolveOnBoard</c>), and the slab is the stackup panel's — the gaps'
/// thickness-weighted εr and tanδ and their summed thickness — not a box of its own. The slab
/// solve itself is gated in <c>OpenSim.Tests/Rf/SlabKernelTests.cs</c>.
/// </summary>
public sealed class AntennaViewModelPrintedTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void ThePrintedAntenna_SolvesOnTheStackupPanelsSlab_AndFollowsItsRows()
    {
        var pcb = _app.Resolve<PcbViewModel>();
        var antenna = _app.Resolve<AntennaViewModel>();
        pcb.InstallBoard(Boards.PrintedMonopole(), "monopole");

        antenna.SourceMode = AntennaViewModel.PrintedMode;
        antenna.AntennaNet = antenna.Nets.Single(n => n.Name == "ANT");
        antenna.GroundPourNet = antenna.Nets.Single(n => n.Name == "GND");
        Assert.Equal(1, antenna.PrintedLayer);                     // the layer the antenna net has copper on
        antenna.FeedXMm = 0;
        antenna.FeedYMm = 0.5;                                     // between the pour's edge (y = 0) and the trace (y = 1 mm)
        antenna.BridgeWidthMm = 1;
        antenna.FrequencyMHz = 2000;
        antenna.SweepFMinMHz = 2000;
        antenna.SweepFMaxMHz = 2000;
        antenna.SweepPoints = 1;
        antenna.AdaptiveSweep = false;
        antenna.CheckMeshConvergence = false;
        antenna.MeshCellsPerWavelength = 4;                        // coarse: this pins the plumbing, not the number
        Assert.True(antenna.PrintedOnLaminate);                    // the default

        // The two-layer Gerber board seeded one gap of the 1.6 mm board box at FR-4.
        var gap = Assert.Single(pcb.DielectricGaps);
        Assert.Equal(1600.0, gap.ThicknessMicrons, 9);

        DispatcherPump.Run(() => antenna.SolveAntennaCommand.ExecuteAsync(null));
        Assert.StartsWith("Zin = ", antenna.AntennaResult);
        Assert.Contains("on the board's laminate (εr 4.4, 1.6 mm)", antenna.AntennaResult);
        Assert.Contains("The board is one slab of 1.6 mm, εr 4.4, tanδ 0.02", antenna.AntennaAssumptions);
        var onFr4 = Zin(antenna);

        // A gap-row edit in the stackup panel is the slab the next solve runs on.
        gap.ThicknessMicrons = 1000;
        gap.RelativePermittivity = 3.0;
        gap.LossTangent = 0.001;
        DispatcherPump.Run(() => antenna.SolveAntennaCommand.ExecuteAsync(null));
        Assert.Contains("on the board's laminate (εr 3, 1 mm)", antenna.AntennaResult);
        Assert.Contains("The board is one slab of 1 mm, εr 3, tanδ 0.001", antenna.AntennaAssumptions);
        var onThinner = Zin(antenna);

        // The same copper in air: a different medium, a different impedance.
        antenna.PrintedOnLaminate = false;
        DispatcherPump.Run(() => antenna.SolveAntennaCommand.ExecuteAsync(null));
        Assert.Contains("in air", antenna.AntennaResult);
        Assert.DoesNotContain("laminate", antenna.AntennaResult);
        var inAir = Zin(antenna);

        // The dielectric loads the antenna: at a fixed frequency the impedance moves with the
        // slab, so the three solves ran on three different media (how far it moves is the
        // library's gate, SlabKernelTests.APrintedMonopoleOnFR4_ResonatesBelowItsAirResonance).
        Assert.NotEqual(inAir.X, onFr4.X, 3);
        Assert.NotEqual(onThinner.X, onFr4.X, 3);
        Assert.NotEqual(onThinner.X, inAir.X, 3);
    }

    /// <summary>The Zin the panel displays, read back from its result line.</summary>
    private static (double R, double X) Zin(AntennaViewModel antenna)
    {
        var point = Assert.Single(antenna.ZinSweep);
        return (point.Resistance, point.Reactance);
    }
}
