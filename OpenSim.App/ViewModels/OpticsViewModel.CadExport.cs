namespace OpenSim.App.ViewModels;

// OP-96: CAD export in the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsCadExportViewModel? _cadExport;

    /// <summary>Export CAD Files (OP-96): STEP, IGES, STL or the 2-D DXF of the project's lens. It
    /// reads the lens and its active catalogs at each export and changes nothing.</summary>
    public OpticsCadExportViewModel CadExport => _cadExport ??=
        new OpticsCadExportViewModel(ToSettings, () => BuildLensEditContext().Catalogs);
}
