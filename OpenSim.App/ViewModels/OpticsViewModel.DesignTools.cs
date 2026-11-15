namespace OpenSim.App.ViewModels;

// OP-61: the design conversion tools in the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsDesignToolsViewModel? _designTools;

    /// <summary>Find Best Asphere, Convert Asphere Types and Stock Lens Matching (OP-61): Keep writes
    /// the result through the Lens Data Editor's undo stack as the Optimize tool's result is (every
    /// tool changes surfaces in place, so nothing is renumbered).</summary>
    public OpticsDesignToolsViewModel DesignTools => _designTools ??=
        new OpticsDesignToolsViewModel(ToSettings, () => BuildLensEditContext().Catalogs, () => _glassLibrary.Catalogs, ApplyOptimization);
}
