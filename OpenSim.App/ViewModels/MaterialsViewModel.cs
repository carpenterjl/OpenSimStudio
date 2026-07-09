using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
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
    private readonly MaterialLibrary _materialLibrary;

    public MaterialsViewModel(ProjectSession session, ILogService log, MaterialLibrary materialLibrary)
    {
        _session = session;
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
        var editorViewModel = new MaterialEditorViewModel(_materialLibrary);
        editorViewModel.LibraryChanged += (_, _) => SyncMaterialsFromLibrary();
        var window = new MaterialEditorWindow(editorViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void SyncMaterialsFromLibrary()
    {
        string? selected = _session.SelectedMaterial?.Name;
        Materials.Clear();
        foreach (var material in _materialLibrary.Materials)
            Materials.Add(material);
        _session.SelectedMaterial =
            Materials.FirstOrDefault(m => m.Name == selected) ?? Materials.FirstOrDefault();
    }

    /// <summary>Selects a loaded project's material, adopting it into the session list
    /// when the library has no material of that name (never auto-persisted — a project
    /// must not silently mutate the machine-wide library).</summary>
    public void AdoptProjectMaterial(Material? material)
    {
        if (material is null) return;
        var match = Materials.FirstOrDefault(m => m.Name == material.Name);
        if (match is null)
            Materials.Add(material);
        _session.SelectedMaterial = match ?? material;
    }

    /// <summary>Finds a material by name in the session list (library + adopted).</summary>
    public Material? FindByName(string name) => Materials.FirstOrDefault(m => m.Name == name);

    /// <summary>Fallback conductor for the pad-to-pad electrical test.</summary>
    public Material DefaultConductor() =>
        _materialLibrary.Materials.First(m => m.Name.Contains("Copper"));

    /// <summary>Resolves a body's per-region material names against the library for a multi-material solve.</summary>
    public IReadOnlyDictionary<int, Material>? ResolveRegionMaterials(OpenSim.Core.Model.Body body)
    {
        if (body.RegionMaterialNames is not { Count: > 0 }) return null;
        var map = new Dictionary<int, Material>();
        foreach (var (region, name) in body.RegionMaterialNames)
        {
            var material = _materialLibrary.Materials.FirstOrDefault(m => m.Name == name)
                           ?? Materials.FirstOrDefault(m => m.Name == name);
            if (material is not null) map[region] = material;
        }
        return map.Count > 0 ? map : null;
    }
}
