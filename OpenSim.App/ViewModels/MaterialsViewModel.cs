using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using Material = OpenSim.Core.Model.Material;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The session's material list (library + project-adopted extras) and the editor
/// dialog. The active choice itself lives on <see cref="ProjectSession.SelectedMaterial"/>
/// so solvers and the scene read it without referencing this view model.
/// </summary>
public partial class MaterialsViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly MaterialLibrary _materialLibrary;

    /// <summary>The project's materials the list holds in place of the library's, by name:
    /// ones the library lacks, and ones whose values differ from the library's of the same
    /// name. Session-only — never written to the library.</summary>
    private readonly Dictionary<string, Material> _projectCopies = new();

    /// <summary>The divergences already logged, so switching bodies does not repeat them.</summary>
    private readonly HashSet<Material> _reported = new();

    public MaterialsViewModel(ProjectSession session, ILogService log, MaterialLibrary materialLibrary)
    {
        _session = session;
        _log = log;
        _materialLibrary = materialLibrary;
        Materials = new ObservableCollection<Material>(materialLibrary.Materials);
        session.SelectedMaterial = Materials.FirstOrDefault();
        foreach (var warning in materialLibrary.LoadWarnings)
            log.Append($"Materials: {warning}");
    }

    public ObservableCollection<Material> Materials { get; }

    /// <summary>Opens the material editor dialog and resyncs the panel's list afterwards.</summary>
    [RelayCommand]
    private void EditMaterials()
    {
        var before = _materialLibrary.Materials.ToDictionary(m => m.Name);
        var editorViewModel = new MaterialEditorViewModel(_materialLibrary);
        editorViewModel.LibraryChanged += (_, _) => SyncMaterialsFromLibrary(before);
        var window = new MaterialEditorWindow(editorViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>Rebuilds the list from the edited library. A project copy stays in place of
    /// the library's material unless the user edited that very material, which then
    /// replaces it (the edit is what the user asked for, and it is logged).</summary>
    private void SyncMaterialsFromLibrary(IReadOnlyDictionary<string, Material> before)
    {
        string? selected = _session.SelectedMaterial?.Name;
        Materials.Clear();
        foreach (var material in _materialLibrary.Materials)
            Materials.Add(material);
        foreach (var (name, copy) in _projectCopies.ToList())
        {
            int index = IndexOf(name);
            if (index < 0)
                Materials.Add(copy);
            else if (before.TryGetValue(name, out var old) && old == Materials[index])
                Materials[index] = copy;
            else
            {
                _projectCopies.Remove(name);
                _log.Append($"Materials: the library's edited '{name}' replaces the project's copy.");
            }
        }
        _session.SelectedMaterial = FindByName(selected ?? "") ?? Materials.FirstOrDefault();
    }

    /// <summary>
    /// Selects a loaded project's material — the project's own values. When the library has
    /// no material of that name it joins the session list; when the library's of that name
    /// differs, the project's takes its place in the list and the difference is logged
    /// (D24 — the library's used to replace it silently). Never written to the library: a
    /// project must not mutate the machine-wide library.
    /// </summary>
    public void AdoptProjectMaterial(Material? material)
    {
        if (material is null) return;
        int index = IndexOf(material.Name);
        if (index < 0)
        {
            Materials.Add(material);
            _projectCopies[material.Name] = material;
        }
        else if (!MaterialDivergence.SameValues(material, Materials[index]))
        {
            if (_materialLibrary.Materials.FirstOrDefault(m => m.Name == material.Name) is { } library)
            {
                if (MaterialDivergence.SameValues(material, library))
                    _projectCopies.Remove(material.Name);
                else
                {
                    _projectCopies[material.Name] = material;
                    if (MaterialDivergence.Describe(material, library) is { } note && _reported.Add(material))
                        _log.Append("Materials: " + note);
                }
            }
            else
                _projectCopies[material.Name] = material;
            Materials[index] = material;
        }
        _session.SelectedMaterial = Materials[index < 0 ? Materials.Count - 1 : index];
    }

    /// <summary>A new or loaded project starts from the library: the previous project's
    /// copies leave the list (a selected one is swapped for the library's of its name).</summary>
    public void ForgetProjectCopies()
    {
        string? selected = _session.SelectedMaterial?.Name;
        bool selectionLeaves = selected is not null && _projectCopies.ContainsKey(selected);
        foreach (var name in _projectCopies.Keys)
        {
            int index = IndexOf(name);
            if (index < 0) continue;
            if (_materialLibrary.Materials.FirstOrDefault(m => m.Name == name) is { } library)
                Materials[index] = library;
            else
                Materials.RemoveAt(index);
        }
        _projectCopies.Clear();
        _reported.Clear();
        if (selectionLeaves)
            _session.SelectedMaterial = FindByName(selected!) ?? Materials.FirstOrDefault();
    }

    /// <summary>Finds a material by name in the session list (library + adopted).</summary>
    public Material? FindByName(string name) => Materials.FirstOrDefault(m => m.Name == name);

    private int IndexOf(string name)
    {
        for (int i = 0; i < Materials.Count; i++)
            if (Materials[i].Name == name) return i;
        return -1;
    }

    /// <summary>Fallback conductor for the pad-to-pad electrical test.</summary>
    public Material DefaultConductor() =>
        _materialLibrary.Materials.First(m => m.Name.Contains("Copper"));

    /// <summary>Resolves a body's per-region material names for a multi-material solve —
    /// the session list first, so a project's own copy of a name wins over the library's as
    /// the body's material does (D24). A name that resolves to nothing is a failure naming
    /// the region and the material — the region used to fall back, silently, to whatever
    /// material was selected, so a board whose laminate had been renamed solved as copper
    /// throughout.</summary>
    public IReadOnlyDictionary<int, Material>? ResolveRegionMaterials(OpenSim.Core.Model.Body body) =>
        RegionMaterialResolver.Resolve(body.Name, body.RegionMaterialNames,
            name => Materials.FirstOrDefault(m => m.Name == name)
                    ?? _materialLibrary.Materials.FirstOrDefault(m => m.Name == name));
}
