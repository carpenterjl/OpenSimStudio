namespace OpenSim.App.ViewModels;

// OP-71
public partial class OpticsViewModel
{
    private OpticsNonSequentialEditorViewModel? _nonSequentialEditor;

    /// <summary>The Non-Sequential Component Editor (OP-71). It edits the saved non-sequential
    /// system: built on first use from what was loaded, and from then on every edit replaces the
    /// saved record and publishes the system through the session.</summary>
    public OpticsNonSequentialEditorViewModel NonSequentialEditor
    {
        get
        {
            if (_nonSequentialEditor is { } editor) return editor;
            editor = new OpticsNonSequentialEditorViewModel(PublishFromNonSequentialEditor, () => BuildLensEditContext().Unit,
                () => Coatings?.ToSettings()?.Coatings?.Select(c => c.Name ?? "").ToArray());
            _nonSequentialEditor = editor;
            // OP-84: the project as analyses see it, for the stray-light Generate commands.
            editor.AnalysisContext = () => new OpenSim.Optics.Analysis.OpticsAnalysisContext(_session?.Project.Name ?? "", ToSettings())
            {
                Catalogs = BuildLensEditContext().Catalogs,
            };
            var notes = new List<string>();
            editor.LoadSettings(_nonSequential, notes);
            foreach (var note in notes) _log?.Append($"Non-sequential objects: {note}");
            return editor;
        }
    }

    /// <summary>The editor's own edits: the saved record becomes the editor's, then the system is published.</summary>
    private void PublishFromNonSequentialEditor()
    {
        if (_nonSequentialEditor is null) return;
        _nonSequential = _nonSequentialEditor.ToSettings();
        if (_session is null) return;
        _session.PublishOptics(ToSettings());
    }

    /// <summary>After a file or a new project replaced <see cref="_nonSequential"/>: the editor,
    /// if built, shows it.</summary>
    private void LoadNonSequentialEditor(ICollection<string> notes) =>
        _nonSequentialEditor?.LoadSettings(_nonSequential, notes);
}
