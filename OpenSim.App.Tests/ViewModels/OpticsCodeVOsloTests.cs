using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-95: the optics panel's CODE V / OSLO import command, without its dialog.</summary>
public sealed class OpticsCodeVOsloTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    // Written for the test from the command rules (docs/Optics-Conventions.md section 54).
    private const string Seq = "RDM\nTIT 'Imported seq'\nEPD 12\nWL 550\nYAN 0\nSO 0 1e20\nS 100 5 1.5:60\nSTO\nS 0 90\nSI 0 0\nGO\n";
    private const string Len = "LEN NEW \"Imported len\" 1 3\nEBR 7; ANG 0; WV 0.55\nTH 1e20\nNXT; RD 100; TH 5; GLA 1.5; AST\nNXT; TH 90\nNXT\nEND 3\n";

    [Fact]
    public void ASeqAndALenFile_ReplaceTheWorkspacesSystem()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        string seq = Path.Combine(_app.UserDirectory, "imported.seq");
        File.WriteAllText(seq, Seq);
        optics.ImportCodeVOsloFile(seq);
        Assert.Equal("Imported seq", optics.SystemTitle);
        Assert.Equal(12, optics.ApertureValue);
        Assert.Equal(4, optics.LensEditor.SurfaceCount);
        Assert.Contains(seq, optics.LensFileStatus);
        Assert.Contains(_app.Log.Entries, e => e.Contains("CODE V import") && e.Contains("GO: not read"));

        string len = Path.Combine(_app.UserDirectory, "imported.len");
        File.WriteAllText(len, Len);
        optics.ImportCodeVOsloFile(len);
        Assert.Equal("Imported len", optics.SystemTitle);
        Assert.Equal(14, optics.ApertureValue);   // 2 × EBR
        Assert.Equal(4, optics.LensEditor.SurfaceCount);
        Assert.Contains(_app.Log.Entries, e => e.Contains("OSLO import"));
    }

    [Fact]
    public void AnUnsupportedCommand_StopsTheImport_AndLeavesTheWorkspace()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        optics.SystemTitle = "Kept";
        string path = Path.Combine(_app.UserDirectory, "bad.seq");
        File.WriteAllText(path, Seq.Replace("STO\n", "STO\nWXYZ 3\n"));
        var ex = Assert.Throws<ZemaxFileException>(() => optics.ImportCodeVOsloFile(path));
        Assert.Contains(ex.Problems, p => p.Contains("WXYZ"));
        Assert.Equal("Kept", optics.SystemTitle);
    }
}
