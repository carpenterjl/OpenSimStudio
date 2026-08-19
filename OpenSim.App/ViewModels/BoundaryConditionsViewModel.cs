using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Face selection + boundary conditions for every analysis type. The list mirrors the
/// active body's conditions and re-syncs whenever the body is replaced.
/// </summary>
public partial class BoundaryConditionsViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;

    public BoundaryConditionsViewModel(ProjectSession session, ILogService log)
    {
        _session = session;
        _log = log;
        session.GeometryReplaced += (_, _) => ResyncFromBody();
    }

    public ObservableCollection<BoundaryCondition> BoundaryConditions { get; } = new();

    // Load parameters
    [ObservableProperty] private double _forceX;
    [ObservableProperty] private double _forceY;
    [ObservableProperty] private double _forceZ = -100;
    [ObservableProperty] private double _pressureMagnitude = 1e5;

    // Electrical boundary condition parameters
    [ObservableProperty] private double _voltageValue = 1.0;
    [ObservableProperty] private double _currentValue = 1.0;

    // Thermal boundary condition parameters
    [ObservableProperty] private double _temperatureValue = 300.0;
    [ObservableProperty] private double _heatPowerValue = 1.0;
    [ObservableProperty] private double _convectionCoefficient = 10.0;
    [ObservableProperty] private double _ambientTemperature = 300.0;

    /// <summary>Toggles a face in the selection (viewport left-click on a non-pad face).</summary>
    public void ToggleFaceSelection(int faceId)
    {
        if (_session.SelectedFaces.Contains(faceId))
            _session.SelectedFaces.Remove(faceId);
        else
            _session.SelectedFaces.Add(faceId);
        _session.RaiseHighlightsInvalidated();
        _session.StatusText = _session.SelectedFaces.Count == 0
            ? "Ready"
            : $"Selected faces: {string.Join(", ", _session.SelectedFaces)}";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _session.ClearScopeSelection();
        _session.RaiseHighlightsInvalidated();
    }

    [RelayCommand]
    private void AddFixedSupport()
    {
        if (!ValidateScopeSelection(allowZeroArea: true)) return;
        AddCondition(new FixedSupport
        {
            Name = $"Fixed support {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            EdgeIds = SelectedEdgesOrNull(),
            VertexIds = SelectedVerticesOrNull()
        });
    }

    [RelayCommand]
    private void AddForce()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new ForceLoad
        {
            Name = $"Force {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            TotalForce = new Vector3D(ForceX, ForceY, ForceZ)
        });
    }

    [RelayCommand]
    private void AddPressure()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new PressureLoad
        {
            Name = $"Pressure {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            Magnitude = PressureMagnitude
        });
    }

    [RelayCommand]
    private void AddVoltage()
    {
        if (!ValidateScopeSelection(allowZeroArea: true)) return;
        AddCondition(new VoltagePotential
        {
            Name = $"Voltage {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            EdgeIds = SelectedEdgesOrNull(),
            VertexIds = SelectedVerticesOrNull(),
            Volts = VoltageValue
        });
    }

    [RelayCommand]
    private void AddCurrent()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new CurrentFlow
        {
            Name = $"Current {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            TotalCurrent = CurrentValue
        });
    }

    [RelayCommand]
    private void AddTemperature()
    {
        if (!ValidateScopeSelection(allowZeroArea: true)) return;
        AddCondition(new FixedTemperature
        {
            Name = $"Temperature {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            EdgeIds = SelectedEdgesOrNull(),
            VertexIds = SelectedVerticesOrNull(),
            Kelvin = TemperatureValue
        });
    }

    [RelayCommand]
    private void AddHeatFlux()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new HeatFlux
        {
            Name = $"Heat flow {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            TotalPower = HeatPowerValue
        });
    }

    [RelayCommand]
    private void AddConvection()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new Convection
        {
            Name = $"Convection {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            Coefficient = ConvectionCoefficient,
            AmbientTemperature = AmbientTemperature
        });
    }

    [RelayCommand]
    private void RemoveCondition(BoundaryCondition? condition)
    {
        if (condition is null) return;
        BoundaryConditions.Remove(condition);
        _session.Body.BoundaryConditions.Remove(condition);
        _log.Append($"Removed '{condition.Name}'.");
    }

    /// <summary>
    /// A scope must name something, and a DISTRIBUTED quantity must name something with
    /// area. A total force or heat flow is spread area-weighted over its scope, and the
    /// area of a curve or a point is zero — so an edge selection is refused here rather
    /// than reaching the solver, which refuses it again with the same rule.
    /// </summary>
    private bool ValidateScopeSelection(bool allowZeroArea)
    {
        bool hasZeroArea = _session.SelectedEdges.Count > 0 || _session.SelectedVertices.Count > 0;
        if (_session.SelectedFaces.Count == 0 && !hasZeroArea)
        {
            _log.Append("Select one or more faces in the 3D view (left-click), " +
                        "or tick edges or vertices in the Scope panel.");
            return false;
        }
        if (hasZeroArea && !allowZeroArea)
        {
            if (_session.SelectedFaces.Count == 0)
            {
                _log.Append("This condition distributes a total quantity over its scope, so it needs a " +
                            "face — the area of an edge or a vertex is zero. Select a face instead.");
                return false;
            }
            _log.Append("Note: edges and vertices carry no area, so this condition uses the selected faces only.");
        }
        return true;
    }

    private IReadOnlyList<int>? SelectedEdgesOrNull() =>
        _session.SelectedEdges.Count > 0 ? _session.SelectedEdges.ToList() : null;

    private IReadOnlyList<int>? SelectedVerticesOrNull() =>
        _session.SelectedVertices.Count > 0 ? _session.SelectedVertices.ToList() : null;

    private void AddCondition(BoundaryCondition condition)
    {
        BoundaryConditions.Add(condition);
        _session.Body.BoundaryConditions.Add(condition);
        _log.Append($"Added {condition.GetType().Name} '{condition.Name}' on {condition.ScopeSummary}.");
        _session.ClearScopeSelection();
        _session.RaiseHighlightsInvalidated();
    }

    /// <summary>Mirrors the (possibly replaced) body's conditions and drops the face selection.
    /// Also called directly when the ACTIVE body of an assembly changes, which re-points the
    /// list without the rest of a geometry replacement.</summary>
    public void ResyncFromBody()
    {
        BoundaryConditions.Clear();
        foreach (var bc in _session.Body.BoundaryConditions)
            BoundaryConditions.Add(bc);
        _session.ClearScopeSelection();
    }
}
