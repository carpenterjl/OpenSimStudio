using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-94: the optics panel's Import .zmx and Export .zmx commands, without their dialogs.</summary>
public sealed class OpticsZemaxTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>A three-surface lens file written from the keyword rules (docs/Optics-Conventions.md section 52).</summary>
    private const string Lens =
        "VERS 1\nMODE SEQ\nNAME Imported\nUNIT MM X W X CM MR CPMM\nENPD 12\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
        "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n" +
        "SURF 1\n  STOP\n  TYPE STANDARD\n  CURV 0.01 0 0 0 0 \"\"\n  DISZ 5\n" +
        "SURF 2\n  TYPE STANDARD\n  DISZ 0\n";

    [Fact]
    public void AZemaxFile_ReplacesTheWorkspacesSystem_AndExportsAgain()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        string path = Path.Combine(_app.UserDirectory, "imported.zmx");
        File.WriteAllBytes(path, ZemaxText.Encode(Lens));

        optics.ImportZemaxFile(path);
        Assert.Equal("Imported", optics.SystemTitle);
        Assert.Equal(12, optics.ApertureValue);
        Assert.Equal(3, optics.LensEditor.SurfaceCount);
        Assert.Contains(path, optics.LensFileStatus);
        Assert.Contains(_app.Log.Entries, e => e.Contains("Zemax import") && e.Contains("VERS: not read"));

        // Whatever the workspace adds that a lens file cannot hold is listed, not fatal, here.
        optics.ZemaxUnsupportedAsWarnings = true;
        string written = Path.Combine(_app.UserDirectory, "written.zmx");
        optics.ExportZemaxFile(written);
        var back = ZemaxLensFiles.Import(written, catalogs: null);
        Assert.Equal(3, back.Optics.Surfaces!.Count);
        Assert.Equal(100, back.Optics.Surfaces[1].Radius!.Value, 10);
        Assert.True(back.Optics.Surfaces[1].IsStop);
        Assert.Equal("Imported", back.Optics.SystemData!.Title);
    }

    [Fact]
    public void AnUnsupportedKeyword_StopsTheImport_AndLeavesTheWorkspace()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        optics.SystemTitle = "Kept";
        string path = Path.Combine(_app.UserDirectory, "bad.zmx");
        File.WriteAllText(path, Lens.Replace("  DISZ 5\n", "  DISZ 5\n  WXYZ 3\n"));
        var ex = Assert.Throws<ZemaxFileException>(() => optics.ImportZemaxFile(path));
        Assert.Contains(ex.Problems, p => p.Contains("WXYZ"));
        Assert.Equal("Kept", optics.SystemTitle);
    }
}
