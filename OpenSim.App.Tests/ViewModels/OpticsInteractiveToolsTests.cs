using System.Collections.Concurrent;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-60: the interactive adjustment tools as the Optics workspace runs them. The gate's app half
/// (docs/Optics-Gates.md): dragging the Slider updates an open spot diagram within the update
/// budget, the analysis windows' debounce (<see cref="OpticsAnalysesViewModel.DefaultDebounce"/>,
/// this program's own 250 ms): each slider move issues the window's request at once through the
/// existing AutoUpdate/Request path, every request waits that debounce, the moves inside it
/// coalesce into one compute, and that compute shows the last value. No wall clock is read: the
/// debounce's wait is replaced by a gate the test opens. The library results are checked in
/// OpenSim.Tests (QuickFocusGateTests, InteractiveAdjustmentTests); here the tools must reach the
/// lens through the editors and their undo. The singlet's radii, thicknesses and index are
/// arbitrary inputs.
/// </summary>
public sealed class OpticsInteractiveToolsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet, EPD 20 mm, near its focus; surface 1's radius flagged Variable when asked.</summary>
    private static OpticsSettings Singlet(bool variable = false) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 20 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Radius = 50, Thickness = 6, Index = 1.6, IsStop = true,
                Flags = variable ? new() { new() { Column = "Radius", Kind = "Variable" } } : null,
            },
            new() { Radius = -200, Thickness = 64 },
            new(),
        },
    };

    [Fact]
    public async Task DraggingTheSlider_UpdatesAnOpenSpotDiagram_WithinTheUpdateBudget()
    {
        var host = _app.Resolve<OpticsAnalysesViewModel>();
        var session = _app.Resolve<ProjectSession>();
        Assert.Equal(TimeSpan.FromMilliseconds(250), OpticsAnalysesViewModel.DefaultDebounce);   // the budget, as docs/Optics-Gates.md states it
        Assert.Equal(OpticsAnalysesViewModel.DefaultDebounce, host.Debounce);
        Assert.Empty(Optics.LoadSettings(Singlet()));

        var delays = new ConcurrentQueue<TimeSpan>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Delay = (span, token) => { delays.Enqueue(span); return gate.Task.WaitAsync(token); };
        var spot = host.Available.Single(a => a.Id == StandardSpotAnalysis.SavedId);
        var window = host.Open(spot);                          // opening computes at once, without the debounce
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(1, window.ComputeCount);
        Assert.True(window.AutoUpdate);
        string before = window.ResultText;
        int requests = window.RequestCount;
        Assert.Empty(delays);

        var slider = Optics.Slider;
        slider.Surface = 2;                                    // loads the thickness (the default parameter)
        Assert.True(slider.HasCell, slider.Status);
        Assert.Equal(64, slider.Value);
        Assert.Equal(requests, window.RequestCount);           // reading the cell publishes nothing

        foreach (double v in new[] { 63.0, 63.2, 63.4 }) slider.Value = v;

        Assert.Equal(requests + 3, window.RequestCount);       // each move issues the window's request at once
        Assert.Equal(3, delays.Count);
        Assert.All(delays, d => Assert.Equal(OpticsAnalysesViewModel.DefaultDebounce, d));
        Assert.Equal(1, window.ComputeCount);                  // nothing computes inside the budget

        gate.SetResult();                                      // the budget runs out
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(2, window.ComputeCount);                  // three moves, one compute
        Assert.Equal("Up to date", window.Status);
        Assert.NotEqual(before, window.ResultText);
        Assert.Equal(63.4, session.OpticsSystem!.Surfaces![2].Thickness);
        Assert.Equal(spot.Compute(host.Snapshot(), window.Settings, default).Text, window.ResultText);   // the last value's spot
        Assert.Equal(64, Optics.ToSettings().Surfaces![2].Thickness);   // the editors keep the original until Keep

        slider.Exit();
        Assert.Equal(64, session.OpticsSystem!.Surfaces![2].Thickness);
        Assert.Equal(requests + 4, window.RequestCount);       // restoring the original updates the window too
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(before, window.ResultText);
    }

    [Fact]
    public void TheSlider_RestoresOnExitAndOnAnotherParameter_AndKeepIsUndoable()
    {
        var session = _app.Resolve<ProjectSession>();
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var slider = Optics.Slider;
        slider.Surface = 2;
        slider.Value = 60;
        Assert.True(slider.IsModified);
        Assert.Equal(60, session.OpticsSystem!.Surfaces![2].Thickness);
        Assert.Equal(64, Optics.ToSettings().Surfaces![2].Thickness);

        slider.Exit();
        Assert.False(slider.IsModified);
        Assert.Equal(64, session.OpticsSystem!.Surfaces![2].Thickness);

        // The help: choosing other data restores the old data unless it was saved.
        slider.Value = 61;
        slider.SelectedParameter = slider.Parameters.Single(p => p.Label == "Radius");
        Assert.Equal(64, session.OpticsSystem!.Surfaces![2].Thickness);
        Assert.Equal(-200, slider.Value);

        slider.Value = -150;
        slider.Keep();
        Assert.False(slider.IsModified);
        Assert.Equal(-150, Optics.ToSettings().Surfaces![2].Radius);
        Assert.Equal(-150, session.OpticsSystem!.Surfaces![2].Radius);
        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal(-200, Optics.ToSettings().Surfaces![2].Radius);

        // A plane has no radius to drag; the slider says so.
        slider.Surface = 3;
        Assert.False(slider.HasCell);
        Assert.Contains("plane", slider.Status);
    }

    [Fact]
    public void TheVisualOptimizer_DragsTheVariables_AsCurvatures_AndKeepIsUndoable()
    {
        var session = _app.Resolve<ProjectSession>();
        Assert.Empty(Optics.LoadSettings(Singlet(variable: true)));
        var vo = Optics.VisualOptimizer;
        vo.Load();
        var row = Assert.Single(vo.Rows);
        Assert.Equal("Surface 1 curvature", row.Variable.Label);
        Assert.Equal(1.0 / 50, row.Value);

        row.Value = 1.0 / 40;
        Assert.True(vo.IsModified);
        Assert.Equal(40, session.OpticsSystem!.Surfaces![1].Radius!.Value, 9);
        Assert.Equal(50, Optics.ToSettings().Surfaces![1].Radius);

        vo.Exit();
        Assert.Equal(50, session.OpticsSystem!.Surfaces![1].Radius);

        row.Value = 1.0 / 45;
        vo.Keep();
        Assert.Equal(45, Optics.ToSettings().Surfaces![1].Radius!.Value, 9);
        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal(50, Optics.ToSettings().Surfaces![1].Radius);
    }

    [Fact]
    public void TheVisualOptimizer_SaysSoWhenThereAreNoVariables()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        Optics.VisualOptimizer.Load();
        Assert.Empty(Optics.VisualOptimizer.Rows);
        Assert.Contains("no variables", Optics.VisualOptimizer.Status);
    }

    [Fact]
    public async Task QuickFocus_WritesTheThicknessThroughTheEditor_AndUndoPutsItBack()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var qf = Optics.QuickFocus;
        await qf.FocusCommand.ExecuteAsync(null);
        var r = qf.LastResult;
        Assert.NotNull(r);
        Assert.Equal(2, r!.Surface);
        Assert.Equal(64 + OpticsConventions.FromMeters(r.Shift, LensUnit.Millimeter), Optics.ToSettings().Surfaces![2].Thickness, 9);
        Assert.True(r.Value < r.StartValue);
        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal(64, Optics.ToSettings().Surfaces![2].Thickness);
    }

    [Fact]
    public async Task QuickAdjust_WritesTheCellThroughTheEditor_AndSaysWhyWhenRefused()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var qa = Optics.QuickAdjust;
        qa.Surface = 2;
        qa.SelectedParameter = qa.Parameters.Single(p => p.Label == "Thickness");
        await qa.AdjustCommand.ExecuteAsync(null);
        Assert.NotNull(qa.LastResult);
        Assert.Equal(64 + OpticsConventions.FromMeters(qa.LastResult!.Value - qa.LastResult.StartValue, LensUnit.Millimeter),
            Optics.ToSettings().Surfaces![2].Thickness, 9);

        qa.EvaluateSurface = 2;                                 // not after the adjusted surface
        await qa.AdjustCommand.ExecuteAsync(null);
        Assert.Contains("evaluation surface", qa.Status);
    }
}
