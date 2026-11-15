namespace OpenSim.App.ViewModels;

// OP-17: test plate fitting in the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsTestPlateFittingViewModel? _testPlates;

    /// <summary>Test Plate Fitting (OP-17): the variable radii fitted to a fabricator's plate list,
    /// written through the Lens Data Editor's undo stack as the Optimize tool's result is.</summary>
    public OpticsTestPlateFittingViewModel TestPlates => _testPlates ??=
        new OpticsTestPlateFittingViewModel(ToSettings, () => BuildLensEditContext().Catalogs, ApplyOptimization);
}
