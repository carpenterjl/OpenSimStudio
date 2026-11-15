using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.CadExport;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-96: the Export CAD Files dialog as the Optics workspace runs it. The library results are
/// checked in OpenSim.Tests (CadExportStepTests, CadExportFormatTests); here the dialog must read
/// the workspace's lens, write the file, report a refusal in its status, and leave the lens alone.
/// The singlet's radii, thickness, index and semi-diameters are arbitrary inputs.
/// </summary>
public sealed class OpticsCadExportTests : IDisposable
{
    private readonly AppComposition _app = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-cad-vm-tests", Guid.NewGuid().ToString("N"));

    public OpticsCadExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsSettings Singlet(bool mirror = false) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 20, Title = "cad singlet" },
        Surfaces = mirror
            ? new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = -100, Thickness = -50, Material = "MIRROR", IsStop = true, SemiDiameter = 10 },
                new(),
            }
            : new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 50, Thickness = 6, Index = 1.6, IsStop = true, SemiDiameter = 12.5 },
                new() { Radius = -200, Thickness = 90, SemiDiameter = 12.5 },
                new(),
            },
    };

    [Fact]
    public async Task Export_WritesTheWorkspaceLens_AndLeavesItUnchanged()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var before = Optics.ToSettings();
        var dialog = Optics.CadExport;
        Assert.Equal(CadExportFormat.Step, dialog.SelectedFormat.Value);
        Assert.Equal("cad singlet.step", dialog.DefaultFileName);

        string path = Path.Combine(_dir, dialog.DefaultFileName);
        var result = await dialog.ExportToAsync(path);
        Assert.NotNull(result);
        Assert.StartsWith("ISO-10303-21;", File.ReadAllText(path));
        Assert.StartsWith("Wrote ", dialog.Status);
        Assert.Contains("STEP AP214", dialog.Details);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(Optics.ToSettings()));

        dialog.SelectedFormat = dialog.Formats.Single(f => f.Value == CadExportFormat.Iges);
        Assert.EndsWith(".igs", dialog.DefaultFileName);
        Assert.NotNull(await dialog.ExportToAsync(Path.Combine(_dir, dialog.DefaultFileName)));
    }

    [Fact]
    public async Task ABareMirrorAsStep_IsRefusedInTheStatus_AndWithASubstrateItIsWritten()
    {
        Assert.Empty(Optics.LoadSettings(Singlet(mirror: true)));
        var dialog = Optics.CadExport;
        string path = Path.Combine(_dir, "mirror.step");
        Assert.Null(await dialog.ExportToAsync(path));
        Assert.StartsWith("Not exported:", dialog.Status);
        Assert.Contains("substrate", dialog.Status);
        Assert.False(File.Exists(path));

        dialog.MirrorSubstrateThickness = 2;
        Assert.NotNull(await dialog.ExportToAsync(path));
        Assert.True(File.Exists(path));
    }
}
