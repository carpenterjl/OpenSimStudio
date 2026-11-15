using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential.Composite;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-32: the Lens Data Editor's composite add-on list on the current surface: add,
/// edit, ignore all, activate all and remove, each one undo step, reaching the traced lens.
/// Values are arbitrary inputs.</summary>
public sealed class OpticsLensEditorCompositeTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    [Fact]
    public void AnAddOn_IsAddedEditedIgnoredAndRemoved_AndReachesTheTracedLens()
    {
        var editor = Optics.LensEditor;
        var notes = new List<string>();
        editor.LoadSettings(new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 90 },
            new(),
        }, notes);
        Assert.Empty(notes);
        editor.CurrentRow = 1;
        Assert.Empty(editor.AddOnList);

        editor.AddCompositeAddOnCommand.Execute(null);
        Assert.Single(editor.AddOnList);
        Assert.Equal(0, editor.SelectedAddOn);

        editor.AddOnType = "Even Asphere";
        editor.AddOnParameters = "2=1e-6";
        editor.ApplyCompositeAddOnCommand.Execute(null);
        Assert.Empty(editor.Problems);                  // centred: the paraxial model takes it

        // Decentered, the paraxial model refuses the surface (it is not symmetric about its
        // vertex), but the add-on is saved and traced.
        editor.AddOnDecenterY = 0.5;
        editor.ApplyCompositeAddOnCommand.Execute(null);
        var saved = editor.ToSettings()[1].AddOns!.Single();
        Assert.Equal("Even Asphere", saved.Type);
        Assert.Equal(0.5, saved.DecenterY);
        Assert.Equal(1e-6, saved.Parameters!.Single(p => p.Number == 2).Value);

        // In SI: 0.5 mm is 5e-4 m.
        var context = new LensEditContext(Optics.ToSettings().SystemData, Array.Empty<OpticalCatalog>());
        var addOn = context.ToLens(editor.ToSettings()).Surfaces[1].AddOns!.Single();
        Assert.True(Math.Abs(addOn.Placement.DecenterY - 5e-4) <= 1e-19);

        editor.IgnoreCompositeAddOnsCommand.Execute(null);
        Assert.True(editor.ToSettings()[1].AddOns!.Single().IsIgnored);
        Assert.False(CompositeSurfaces.HasActiveAddOns(context.ToLens(editor.ToSettings()).Surfaces[1]));
        editor.ActivateCompositeAddOnsCommand.Execute(null);
        Assert.False(editor.ToSettings()[1].AddOns!.Single().IsIgnored);

        editor.RemoveCompositeAddOnCommand.Execute(null);
        Assert.Null(editor.ToSettings()[1].AddOns);
        Assert.Empty(editor.AddOnList);

        // Each step was one undo step: undoing the removal brings the add-on back.
        editor.UndoCommand.Execute(null);
        Assert.Single(editor.ToSettings()[1].AddOns!);
    }
}
