using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

// OP-99: ZPL macros in the Optics workspace.
public partial class OpticsViewModel
{
    private OpticsMacroEditorViewModel? _macros;

    /// <summary>The macro window (OP-99): runs ZPL macros on a copy of the record and writes the
    /// result back with <see cref="ApplyMacro"/>.</summary>
    public OpticsMacroEditorViewModel Macros => _macros ??=
        new OpticsMacroEditorViewModel(ToSettings, () => BuildLensEditContext().Catalogs, ApplyMacro);

    /// <summary>A macro's result: when only the surfaces changed, one undoable edit of the Lens
    /// Data Editor (as the Optimize tool's result); otherwise the whole record is loaded, which
    /// the editors cannot undo. Null on success.</summary>
    private string? ApplyMacro(OpticsSettings before, OpticsSettings after)
    {
        if (after with { Surfaces = before.Surfaces } == before)
            return ApplyOptimization(after, false, "ZPL macro");
        var notes = LoadSettings(after);
        return notes.Count > 0 ? string.Join(" ", notes) : null;
    }
}
