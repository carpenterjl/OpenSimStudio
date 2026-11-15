using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

// OP-60: the interactive adjustment tools of the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsQuickFocusViewModel? _quickFocus;
    private OpticsQuickAdjustViewModel? _quickAdjust;
    private OpticsSliderViewModel? _slider;
    private OpticsVisualOptimizerViewModel? _visualOptimizer;

    /// <summary>Quick Focus (OP-60): the thickness before the image for the best RMS, written
    /// through the Lens Data Editor's undo stack as the Optimize tool's result is.</summary>
    public OpticsQuickFocusViewModel QuickFocus => _quickFocus ??=
        new OpticsQuickFocusViewModel(ToSettings, () => BuildLensEditContext().Catalogs, ApplyOptimization);

    /// <summary>Quick Adjust (OP-60): one radius or thickness for the best RMS at a later surface.</summary>
    public OpticsQuickAdjustViewModel QuickAdjust => _quickAdjust ??=
        new OpticsQuickAdjustViewModel(ToSettings, () => BuildLensEditContext().Catalogs, ApplyOptimization);

    /// <summary>The Slider (OP-60): trial values published to the session, kept through the editors.</summary>
    public OpticsSliderViewModel Slider => _slider ??= new OpticsSliderViewModel(ToSettings, PreviewSystem, ApplyOptimization);

    /// <summary>The Visual Optimizer (OP-60): up to eight sliders on the lens's variables.</summary>
    public OpticsVisualOptimizerViewModel VisualOptimizer => _visualOptimizer ??=
        new OpticsVisualOptimizerViewModel(ToSettings, PreviewSystem, ApplyOptimization);

    /// <summary>Publishes a trial record to the session, so the analysis windows recompute on it,
    /// without changing the editors (the Slider's and Visual Optimizer's "copy of the data"); the
    /// next edit, or the tool's Exit, publishes the editors' record again.</summary>
    private void PreviewSystem(OpticsSettings settings)
    {
        if (_session is null) return;
        _session.PublishOptics(settings);
    }
}
