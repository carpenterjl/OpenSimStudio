using System.Text.Json;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-81: the editor's Volume Physics fields (<c>OpticsNonSequentialEditorViewModel.Volume.cs</c>):
/// loading shows the saved values, Apply writes them into the current object and nothing else, and
/// bad input is refused with a reason. Values are arbitrary test inputs.</summary>
public sealed class OpticsNscVolumeEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsNonSequentialSettings TwoSpheres() => new()
    {
        Objects = new()
        {
            new()
            {
                Type = "Sphere", Parameters = new() { 5, 1 },
                VolumePhysics = new OpticsNscVolumePhysicsSettings { Model = "Angle Scattering", MeanPath = 0.5, Angle = 10, WavelengthShift = "1,2,1" },
            },
            new() { Type = "Sphere", Parameters = new() { 2, 1 }, Z = 20 },
        },
    };

    private OpticsNonSequentialEditorViewModel Loaded(OpticsNonSequentialSettings settings)
    {
        var notes = Optics.LoadSettings(Optics.ToSettings() with { NonSequential = settings });
        Assert.Empty(notes);
        return Optics.NonSequentialEditor;
    }

    [Fact]
    public void Loading_ShowsTheSavedVolumeFields_AndLeavesTheFileUnchanged()
    {
        var saved = TwoSpheres();
        var editor = Loaded(saved);
        Assert.Equal("Angle Scattering", editor.VolumeModel);
        Assert.Equal(0.5, editor.VolumeMeanPath);
        Assert.Equal(10, editor.VolumeAngle);
        Assert.Equal("1,2,1", editor.WavelengthShift);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(editor.ToSettings()));

        editor.CurrentRow = 1;
        Assert.Equal("None", editor.VolumeModel);
        Assert.Equal("", editor.WavelengthShift);
    }

    [Fact]
    public void Apply_WritesTheCurrentObjectOnly()
    {
        var editor = Loaded(TwoSpheres());
        editor.CurrentRow = 1;
        editor.VolumeModel = "DLL Defined Scattering";
        editor.VolumeMeanPath = 2;
        editor.VolumeDll = "Henyey-Greenstein-bulk.dll";
        editor.VolumeDllParameters = "0.7";
        Assert.Null(editor.ApplyVolumeFields());
        var rows = editor.ToSettings()!.Objects!;
        var v = rows[1].VolumePhysics!;
        Assert.Equal(("DLL Defined Scattering", 2.0, "Henyey-Greenstein-bulk.dll"), (v.Model, v.MeanPath!.Value, v.Dll));
        Assert.Equal(new List<double> { 0.7 }, v.DllParameters);
        Assert.Equal(TwoSpheres().Objects![0].VolumePhysics, rows[0].VolumePhysics);

        editor.VolumeModel = "None";
        Assert.Null(editor.ApplyVolumeFields());
        Assert.Null(editor.ToSettings()!.Objects![1].VolumePhysics);
    }

    [Fact]
    public void BadInput_IsRefusedWithAReason()
    {
        var editor = Loaded(TwoSpheres());
        editor.VolumeModel = "Fog";
        Assert.Contains("is not one of", editor.ApplyVolumeFields());
        editor.VolumeModel = "Angle Scattering";
        editor.WavelengthShift = "1, 2";
        Assert.Contains("in, out, prob", editor.ApplyVolumeFields());
        editor.WavelengthShift = "";
        editor.VolumeDllParameters = "0.5 x";
        Assert.Contains("not a number", editor.ApplyVolumeFields());
    }
}
