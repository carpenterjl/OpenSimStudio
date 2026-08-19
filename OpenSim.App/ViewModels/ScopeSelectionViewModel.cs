using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Picks the geometric EDGES and VERTICES a boundary condition is scoped to — the scope an
/// "Ansys fixed support on 2 edges" needs and a face selection cannot express.
///
/// Selection is a LIST, not a viewport pick. Hit testing here is object-granular (one
/// GeometryModel3D per face, reverse-mapped by reference), so picking a line would mean
/// building and hit-testing new per-edge geometry — a much larger change for a scope the
/// user selects a handful of times per model. The list is also the only place the ids the
/// project file stores are ever visible.
///
/// Edges and vertices exist only once a body is MESHED: they are derived from the boundary
/// skin and its face tags, so remeshing can renumber them. That is why the panel refreshes
/// on every mesh change and why a stale id is a typed failure at solve time rather than a
/// silent drop.
/// </summary>
public partial class ScopeSelectionViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private bool _suppressSync;

    public ScopeSelectionViewModel(ProjectSession session, ILogService log)
    {
        _session = session;
        _log = log;
        session.MeshChanged += (_, _) => Refresh();
        session.GeometryReplaced += (_, _) => Refresh();
        session.ActiveBodyChanged += (_, _) => Refresh();

        // The session collections are the source of truth, so a clear from elsewhere (a new
        // condition was just created) unticks the rows without any viewmodel calling another.
        session.SelectedEdges.CollectionChanged += (_, _) => PullTicks(Edges, session.SelectedEdges);
        session.SelectedVertices.CollectionChanged += (_, _) => PullTicks(Vertices, session.SelectedVertices);
    }

    public ObservableCollection<ScopeItem> Edges { get; } = new();
    public ObservableCollection<ScopeItem> Vertices { get; } = new();

    /// <summary>True once the active body carries a mesh, so edges and vertices exist.</summary>
    [ObservableProperty] private bool _hasScopeItems;

    [ObservableProperty] private string _scopeSummary = "Mesh the body to select edges or vertices.";

    /// <summary>Rebuilds the rows from the active body's mesh, dropping any stale selection.</summary>
    public void Refresh()
    {
        _suppressSync = true;
        foreach (var item in Edges) item.PropertyChanged -= OnItemChanged;
        foreach (var item in Vertices) item.PropertyChanged -= OnItemChanged;
        Edges.Clear();
        Vertices.Clear();
        _session.SelectedEdges.Clear();
        _session.SelectedVertices.Clear();

        var mesh = _session.Body.Mesh;
        if (mesh is null)
        {
            HasScopeItems = false;
            ScopeSummary = "Mesh the body to select edges or vertices.";
            _suppressSync = false;
            return;
        }

        var set = mesh.Edges;
        foreach (var edge in set.Edges)
            Edges.Add(Track(new ScopeItem(edge.Id,
                $"Edge {edge.Id} — faces {edge.FaceA} and {edge.FaceB}, {FormatLength(edge.Length)}")));
        foreach (var vertex in set.Vertices)
            Vertices.Add(Track(new ScopeItem(vertex.Id,
                $"Vertex {vertex.Id} — faces {string.Join(", ", vertex.FaceIds)}")));

        HasScopeItems = Edges.Count > 0 || Vertices.Count > 0;
        ScopeSummary = $"{Edges.Count} edge(s), {Vertices.Count} vertex/vertices.";
        if (set.NonManifoldEdgeCount > 0)
            _log.Append($"Note: {set.NonManifoldEdgeCount} mesh edge(s) touch more than two geometric " +
                        "faces; those edges are assigned to their two lowest face ids.");
        _suppressSync = false;
    }

    /// <summary>
    /// Ticks every edge lying between two of the currently selected faces. The two-click
    /// path to the reference beam supports: select the bottom and one end face, take their
    /// shared edge.
    /// </summary>
    [RelayCommand]
    private void SelectSharedEdges()
    {
        var mesh = _session.Body.Mesh;
        if (mesh is null)
        {
            _log.Append("Mesh the body before selecting edges.");
            return;
        }
        if (_session.SelectedFaces.Count < 2)
        {
            _log.Append("Select two or more faces in the 3D view first — a geometric edge is where two of them meet.");
            return;
        }

        var shared = mesh.Edges.EdgesBetween(_session.SelectedFaces).ToHashSet();
        if (shared.Count == 0)
        {
            _log.Append("The selected faces do not meet along any edge.");
            return;
        }

        foreach (var item in Edges)
            if (shared.Contains(item.Id)) item.IsSelected = true;
        _log.Append($"Selected {shared.Count} edge(s) shared by the face selection.");
    }

    [RelayCommand]
    private void ClearScopeSelection()
    {
        foreach (var item in Edges) item.IsSelected = false;
        foreach (var item in Vertices) item.IsSelected = false;
        _session.SelectedFaces.Clear();
        _session.RaiseHighlightsInvalidated();
    }

    private ScopeItem Track(ScopeItem item)
    {
        item.PropertyChanged += OnItemChanged;
        return item;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressSync || e.PropertyName != nameof(ScopeItem.IsSelected)) return;
        _suppressSync = true;
        PushTicks(Edges, _session.SelectedEdges);
        PushTicks(Vertices, _session.SelectedVertices);
        _suppressSync = false;
        _session.RaiseHighlightsInvalidated();
    }

    private static void PushTicks(IEnumerable<ScopeItem> items, ObservableCollection<int> target)
    {
        target.Clear();
        foreach (var item in items)
            if (item.IsSelected) target.Add(item.Id);
    }

    private void PullTicks(IEnumerable<ScopeItem> items, ObservableCollection<int> source)
    {
        if (_suppressSync) return;
        _suppressSync = true;
        var selected = source.ToHashSet();
        foreach (var item in items) item.IsSelected = selected.Contains(item.Id);
        _suppressSync = false;
    }

    private static string FormatLength(double metres) => metres switch
    {
        >= 1 => $"{metres:g4} m",
        >= 1e-3 => $"{metres * 1e3:g4} mm",
        _ => $"{metres * 1e6:g4} µm"
    };
}
