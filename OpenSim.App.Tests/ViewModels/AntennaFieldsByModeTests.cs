using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>The antenna form shows the fields the selected geometry reads, and no others.</summary>
public sealed class AntennaFieldsByModeTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private AntennaViewModel At(string mode)
    {
        var antenna = _app.Resolve<AntennaViewModel>();
        antenna.SourceMode = mode;
        return antenna;
    }

    [Fact]
    public void ADipole_AsksForItsLengthWireAndGround_NotForASheet()
    {
        var a = At(AntennaViewModel.DipoleMode);
        Assert.True(a.ShowsDipoleLength && a.ShowsWireRadius && a.ShowsGroundPlane && a.ShowsHeightAboveGround && a.ShowsJunctionFeed);
        Assert.False(a.ShowsLoopRadius || a.ShowsMonopoleHeight || a.ShowsPlateSize || a.ShowsSubstrate
                     || a.ShowsProbePosition || a.ShowsCover || a.ShowsAttachedWire || a.ShowsIsland || a.ShowsNet
                     || a.ShowsMeshDensity || a.ShowsPatchGap);
    }

    [Fact]
    public void AProbeFedCoveredPatch_AsksForTheSlabProbeAndCover_NotForAPortGap()
    {
        var a = At(AntennaViewModel.ProbeFedCoveredPatchMode);
        Assert.True(a.ShowsPlateSize && a.ShowsPatchHeight && a.ShowsSubstrate && a.ShowsProbePosition
                    && a.ShowsProbeBore && a.ShowsCover && a.ShowsMeshDensity);
        Assert.False(a.ShowsPatchGap || a.ShowsAttachedWire || a.ShowsDipoleLength || a.ShowsWireRadius);
    }

    [Fact]
    public void AWireFedPlate_KeepsTheGroundBox_ItsSolveRefusesItByNameSoItMustBeUntickable()
    {
        var a = At(AntennaViewModel.WireFedPlateMode);
        Assert.True(a.ShowsPlateSize && a.ShowsAttachedWire && a.ShowsWireRadius && a.ShowsGroundPlane);
        Assert.False(a.ShowsProbePosition || a.ShowsPatchHeight);
    }

    [Fact]
    public void BoardModes_AskForTheirNet_AndAnIslandWhereTheySolveOne()
    {
        Assert.True(At(AntennaViewModel.NetMode).ShowsNet);
        Assert.False(At(AntennaViewModel.NetMode).ShowsIsland);
        Assert.True(At(AntennaViewModel.IslandMode).ShowsIsland);
        Assert.True(At(AntennaViewModel.IslandPinsMode).ShowsIsland);
        Assert.True(At(AntennaViewModel.PrintedMode).ShowsNet);
        Assert.False(At(AntennaViewModel.PrintedMode).ShowsIsland);
    }

    [Fact]
    public void ChangingTheMode_NotifiesTheFlags()
    {
        var a = At(AntennaViewModel.DipoleMode);
        var raised = new List<string?>();
        a.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        a.SourceMode = AntennaViewModel.PatchMode;

        Assert.Contains(nameof(AntennaViewModel.ShowsPatchGap), raised);
        Assert.Contains(nameof(AntennaViewModel.ShowsDipoleLength), raised);
    }
}
