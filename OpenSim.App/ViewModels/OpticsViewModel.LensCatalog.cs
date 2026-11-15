using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

// OP-16: stock lens catalogs, Insert Lens and design templates in the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsLensCatalogViewModel? _lensCatalog;

    /// <summary>Lens Catalogs (OP-16): Insert writes through the Lens Data Editor's undo stack as
    /// the Optimize tool's result is (the renumbered multi-configuration table with it, and the
    /// renumbered merit rows loaded into the Merit Function Editor); Load and the design templates
    /// replace the workspace's optical model as a Zemax import does.</summary>
    public OpticsLensCatalogViewModel LensCatalog => _lensCatalog ??=
        new OpticsLensCatalogViewModel(ToSettings, () => _glassLibrary.Catalogs, ApplyInsertedLens, LoadSettings, line => _log.Append(line));

    private string? ApplyInsertedLens(OpticsSettings settings, bool table, bool merit, string description)
    {
        if (ApplyOptimization(settings, table, description) is { } refusal) return refusal;
        if (!merit) return null;
        var notes = new List<string>();
        MeritEditor.LoadSettings(settings.MeritFunction, notes);
        foreach (var n in notes) _log.Append($"{description}: {n}");
        return null;
    }
}
