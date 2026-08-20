using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One row of the assembly body list: a live view onto a project <see cref="Body"/>.
/// Edits (heat source, visibility) write straight through to the model — the list IS the
/// editor, so there is no apply step that could leave the two disagreeing.
/// </summary>
public partial class BodyRowViewModel : ObservableObject
{
    private readonly Action _onVisibilityChanged;

    private readonly Action? _onRoleChanged;

    public BodyRowViewModel(Body model, int index, Action onVisibilityChanged,
        Action? onRoleChanged = null)
    {
        Model = model;
        Index = index;
        _onVisibilityChanged = onVisibilityChanged;
        _onRoleChanged = onRoleChanged;
        _isFluidRegion = model.Role == BodyRole.FluidRegion;
    }

    public Body Model { get; }

    /// <summary>Position in the project's body list — also this body's REGION ID in the
    /// merged assembly mesh, which is why it is shown.</summary>
    public int Index { get; }

    public string Name => Model.Name;

    public string MaterialName => Model.Material?.Name ?? "no material";

    public string MeshStatus => Model.Mesh is null
        ? "not meshed"
        : $"{Model.Mesh.ElementCount:N0} elements";

    /// <summary>
    /// Marks this body as a FLUID VOLUME rather than material: a CAD assembly of an
    /// internal-flow part carries the passage as its own solid, and treating it as metal
    /// would fill the channel and leave nothing to flow. A fluid body is skipped by
    /// meshing, by the FE assembly and by the voxelizer solid set; its geometry defines
    /// the flow domain and its end caps are the openings.
    /// </summary>
    [ObservableProperty] private bool _isFluidRegion;

    partial void OnIsFluidRegionChanged(bool value)
    {
        Model.Role = value ? BodyRole.FluidRegion : BodyRole.Solid;
        OnPropertyChanged(nameof(Detail));
        _onRoleChanged?.Invoke();
    }

    /// <summary>Secondary line: role, material and mesh state at a glance.</summary>
    public string Detail => Model.Role == BodyRole.FluidRegion
        ? "fluid volume · defines the flow domain"
        : $"{MaterialName} · {MeshStatus}";

    /// <summary>Internal dissipation [W]; empty text clears it.</summary>
    public string HeatSourceText
    {
        get => Model.HeatSourcePower is { } w ? w.ToString("g6") : string.Empty;
        set
        {
            double? parsed = double.TryParse(value, out double w) ? w : null;
            if (string.IsNullOrWhiteSpace(value)) parsed = null;
            Model.HeatSourcePower = parsed;
            OnPropertyChanged();
        }
    }

    /// <summary>Shown in the 3D view. Hiding a body hides it from the SCENE only — it is
    /// still meshed, still solved and still exchanges heat with its neighbours, so a
    /// hidden part can never silently change an answer.</summary>
    [ObservableProperty] private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value) => _onVisibilityChanged();

    /// <summary>Re-reads the model after a mesh or material change.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MaterialName));
        OnPropertyChanged(nameof(MeshStatus));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HeatSourceText));
        OnPropertyChanged(nameof(IsFluidRegion));
    }
}
