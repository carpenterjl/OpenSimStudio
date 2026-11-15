using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-99: the ZPL macro window as the Optics workspace runs it. The language and its bindings are
/// pinned in OpenSim.Tests (ZplLanguageTests, ZplConformanceTests, ZplMacroGateTests); here a
/// macro's change must reach the lens (as one undoable Lens Data Editor edit when only surfaces
/// changed), a failing macro must change nothing and name its line, and Check must list what is
/// not bound. The singlet's values are arbitrary inputs.
/// </summary>
public sealed class OpticsMacroEditorTests : IDisposable
{
    private readonly AppComposition _app = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opensim-op99-" + Guid.NewGuid().ToString("N"));

    public OpticsMacroEditorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsSettings Singlet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 10, Title = "Before" },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Index = 1.5, IsStop = true },
            new() { Thickness = 95 },
            new(),
        },
    };

    [Fact]
    public async Task ASurfaceChange_IsOneUndoableEdit()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var macros = Optics.Macros;
        macros.MacroFolder = _folder;
        macros.Text = "SURP 2, THIC, 50\nPRINT THIC(2)";
        await macros.RunCommand.ExecuteAsync(null);
        Assert.Equal("50.0000" + Environment.NewLine, macros.Output);
        Assert.Contains("written to the lens", macros.Status);
        Assert.Equal(50, Optics.ToSettings().Surfaces![2].Thickness);
        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal(95, Optics.ToSettings().Surfaces![2].Thickness);
    }

    [Fact]
    public async Task ASystemDataChange_LoadsTheWholeRecord()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var macros = Optics.Macros;
        macros.MacroFolder = _folder;
        macros.Text = "SYSP 16, \"After\"";
        await macros.RunCommand.ExecuteAsync(null);
        Assert.Equal("After", Optics.ToSettings().SystemData!.Title);
    }

    [Fact]
    public async Task AFailingMacro_NamesItsLine_AndChangesNothing()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var macros = Optics.Macros;
        macros.MacroFolder = _folder;
        macros.Text = "SURP 2, THIC, 50\nx = y";
        await macros.RunCommand.ExecuteAsync(null);
        Assert.Contains("line 2", macros.Status);
        Assert.Equal(95, Optics.ToSettings().Surfaces![2].Thickness);

        macros.Text = "BEEP\nz = GNUM(\"BK7\")";
        macros.CheckCommand.Execute(null);
        Assert.Contains("BEEP (line 1)", macros.Status);
        Assert.Contains("GNUM (line 2)", macros.Status);
    }

    [Fact]
    public void TheMacroList_IsTheFolder_AndAMacroOpensFromIt()
    {
        File.WriteAllText(Path.Combine(_folder, "b.zpl"), "PRINT 2");
        File.WriteAllText(Path.Combine(_folder, "a.zpl"), "PRINT 1");
        var macros = Optics.Macros;
        macros.MacroFolder = _folder;
        Assert.Equal(new[] { "a.zpl", "b.zpl" }, macros.Macros);
        macros.SelectedMacro = "b.zpl";
        macros.LoadSelectedCommand.Execute(null);
        Assert.Equal("PRINT 2", macros.Text);
        macros.Save(Path.Combine(_folder, "c.zpl"));
        Assert.Contains("c.zpl", macros.Macros);
    }
}
