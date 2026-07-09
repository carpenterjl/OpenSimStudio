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
        _session.SelectedFaces.Clear();
        _session.RaiseHighlightsInvalidated();
    }

    [RelayCommand]
    private void AddFixedSupport()
    {
        if (!ValidateFaceSelection()) return;
        AddCondition(new FixedSupport
        {
            Name = $"Fixed support {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList()
        });
    }

    [RelayCommand]
    private void AddForce()
    {
        if (!ValidateFaceSelection()) return;
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
        if (!ValidateFaceSelection()) return;
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
        if (!ValidateFaceSelection()) return;
        AddCondition(new VoltagePotential
        {
            Name = $"Voltage {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            Volts = VoltageValue
        });
    }

    [RelayCommand]
    private void AddCurrent()
    {
        if (!ValidateFaceSelection()) return;
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
        if (!ValidateFaceSelection()) return;
        AddCondition(new FixedTemperature
        {
            Name = $"Temperature {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            Kelvin = TemperatureValue
        });
    }

    [RelayCommand]
    private void AddHeatFlux()
    {
        if (!ValidateFaceSelection()) return;
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
        if (!ValidateFaceSelection()) return;
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

    private bool ValidateFaceSelection()
    {
        if (_session.SelectedFaces.Count == 0)
        {
            _log.Append("Select one or more faces in the 3D view first (left-click).");
            return false;
        }
        return true;
    }

    private void AddCondition(BoundaryCondition condition)
    {
        BoundaryConditions.Add(condition);
        _session.Body.BoundaryConditions.Add(condition);
        _log.Append($"Added {condition.GetType().Name} '{condition.Name}' on faces [{string.Join(", ", condition.FaceIds)}].");
        _session.SelectedFaces.Clear();
        _session.RaiseHighlightsInvalidated();
    }

    /// <summary>Mirrors the (possibly replaced) body's conditions and drops the face selection.</summary>
    private void ResyncFromBody()
    {
        BoundaryConditions.Clear();
        foreach (var bc in _session.Body.BoundaryConditions)
            BoundaryConditions.Add(bc);
        _session.SelectedFaces.Clear();
    }
}
