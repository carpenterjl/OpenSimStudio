using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The assembly body list: every part of the project, which one is active (the panels
/// edit it), what it is made of, whether it is meshed, and what it dissipates.
/// <para>
/// The list order is the project's body order, never re-sorted: that order is the region
/// numbering of the merged assembly mesh, so a display sort would make the row labels
/// disagree with the solved regions.
/// </para>
/// </summary>
public partial class BodiesViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private bool _syncing;

    public BodiesViewModel(ProjectSession session, ILogService log)
    {
        _session = session;
        _log = log;
        session.BodiesChanged += (_, _) => Rebuild();
        session.GeometryReplaced += (_, _) =>
        {
            LoadAssemblySettings();
            Rebuild();
        };
        session.ActiveBodyChanged += (_, _) => SyncSelectionFromSession();
        session.MeshChanged += (_, _) => RefreshRows();
        Rebuild();
    }

    public ObservableCollection<BodyRowViewModel> Bodies { get; } = new();

    [ObservableProperty] private BodyRowViewModel? _selectedBody;

    /// <summary>Raised when a body's scene visibility is toggled; the scene recomposes.</summary>
    public event EventHandler? VisibilityChanged;

    /// <summary>The bodies currently hidden in the 3D view, by project index.</summary>
    public IReadOnlySet<int> HiddenBodyIndices =>
        Bodies.Where(b => !b.IsVisible).Select(b => b.Index).ToHashSet();

    /// <summary>Header line: "3 bodies (2 meshed)".</summary>
    public string Summary
    {
        get
        {
            int meshed = Bodies.Count(b => b.Model.Mesh is not null);
            string plural = Bodies.Count == 1 ? "body" : "bodies";
            return $"{Bodies.Count} {plural} ({meshed} meshed)";
        }
    }

    /// <summary>True once the project holds more than one body — the list is pointless
    /// for a single part and takes panel space the single-part workflows need.</summary>
    public bool HasAssembly => Bodies.Count > 1;

    // ---------------- Joint quality (how the parts are thermally connected) ----------------

    /// <summary>
    /// Interfacial conductance of every detected joint [W/(m²·K)]. Two parts merely touching
    /// do NOT conduct perfectly: microscopic roughness leaves a real thermal resistance, and
    /// this is the number that models it. The default is dry machined metal at moderate
    /// pressure; greased or bolted joints run an order higher, loose dry ones an order lower.
    /// </summary>
    [ObservableProperty] private double _contactConductance = 5e3;

    /// <summary>How far apart two surfaces may be and still be treated as touching [m];
    /// 0 = auto from the mesh size.</summary>
    [ObservableProperty] private double _contactGapTolerance;

    partial void OnContactConductanceChanged(double value) => PushAssemblySettings();
    partial void OnContactGapToleranceChanged(double value) => PushAssemblySettings();

    private void PushAssemblySettings()
    {
        if (_syncing) return;
        _session.Project.Assembly = new AssemblySettings
        {
            ContactConductance = ContactConductance,
            ContactGapTolerance = ContactGapTolerance
        };
    }

    private void LoadAssemblySettings()
    {
        var settings = _session.Project.Assembly;
        if (settings is null) return;
        _syncing = true;
        try
        {
            ContactConductance = settings.ContactConductance;
            ContactGapTolerance = settings.ContactGapTolerance;
        }
        finally { _syncing = false; }
    }

    partial void OnSelectedBodyChanged(BodyRowViewModel? value)
    {
        // WPF pushes a transient null through the two-way binding whenever ItemsSource is
        // rebuilt; ignore it rather than clearing the session's active body.
        if (_syncing || value is null) return;
        try
        {
            _session.SelectActiveBody(value.Model);
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    private void Rebuild()
    {
        _syncing = true;
        try
        {
            Bodies.Clear();
            var bodies = _session.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                Bodies.Add(new BodyRowViewModel(bodies[i], i, RaiseVisibilityChanged));
            SelectedBody = Bodies.FirstOrDefault(b => ReferenceEquals(b.Model, _session.Body))
                           ?? Bodies.FirstOrDefault();
        }
        finally { _syncing = false; }
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasAssembly));
    }

    private void SyncSelectionFromSession()
    {
        _syncing = true;
        try
        {
            SelectedBody = Bodies.FirstOrDefault(b => ReferenceEquals(b.Model, _session.Body));
        }
        finally { _syncing = false; }
    }

    private void RefreshRows()
    {
        foreach (var row in Bodies) row.Refresh();
        OnPropertyChanged(nameof(Summary));
    }

    private void RaiseVisibilityChanged() => VisibilityChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Gives every part the material currently selected in the Material panel. An assembly
    /// solve needs a material on each body, and an imported STEP file carries none — this is
    /// the one-click answer for the common "it is all the same alloy" case, after which
    /// individual parts can be re-assigned by selecting them.
    /// </summary>
    [RelayCommand]
    private void ApplyMaterialToAll()
    {
        if (_session.SelectedMaterial is not { } material)
        {
            _log.Append("Select a material first.");
            return;
        }
        foreach (var body in _session.Bodies)
            body.Material = material;
        RefreshRows();
        _log.Append($"Material '{material.Name}' applied to {_session.Bodies.Count} bod" +
                    $"{(_session.Bodies.Count == 1 ? "y" : "ies")}.");
    }

    /// <summary>Selects the body a picked face belongs to (viewport click on any part).</summary>
    public void SelectByIndex(int index)
    {
        var row = Bodies.FirstOrDefault(b => b.Index == index);
        if (row is null) return;
        SelectedBody = row;
        _log.Append($"Selected body {index + 1}: {row.Name}");
    }
}
