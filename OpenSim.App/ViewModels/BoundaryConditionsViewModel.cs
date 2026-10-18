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

    /// <summary>A current flow added as an equipotential terminal (one potential over the
    /// face, found by the solve) rather than a uniform-density injection.</summary>
    [ObservableProperty] private bool _currentEquipotential;

    /// <summary>A prescribed total charge [C] for the electrostatic solve (FU-42): on a conductor
    /// its whole piece floats at the potential that charge gives it; on a dielectric face it is a
    /// uniform surface charge.</summary>
    [ObservableProperty] private double _chargeValue = 1e-12;

    // Thermal boundary condition parameters
    [ObservableProperty] private double _temperatureValue = 300.0;
    [ObservableProperty] private double _heatPowerValue = 1.0;
    [ObservableProperty] private double _convectionCoefficient = 10.0;
    [ObservableProperty] private double _ambientTemperature = 300.0;

    // A convection coefficient varying along a flow (FU-44, ConvectionProfile).
    [ObservableProperty] private bool _convectionVaries;
    [ObservableProperty] private double _leadingEdgeXMm;
    [ObservableProperty] private double _leadingEdgeYMm;
    [ObservableProperty] private double _leadingEdgeZMm;
    [ObservableProperty] private double _flowDirectionX = 1;
    [ObservableProperty] private double _flowDirectionY;
    [ObservableProperty] private double _flowDirectionZ;
    /// <summary>h ∝ x^exponent with the faces' mean held at h: −0.5 laminar, about −0.2 turbulent, 0 none.</summary>
    [ObservableProperty] private double _boundaryLayerExponent = -0.5;
    /// <summary>A factor table against the downstream distance: "mm:factor" pairs, e.g. "0:1, 20:0.8".</summary>
    [ObservableProperty] private string _convectionTable = "";
    /// <summary>Wakes behind parts: "x,y,z,half-width,length,factor" in mm (factor plain), separated by ';'.</summary>
    [ObservableProperty] private string _convectionWakes = "";

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

    /// <summary>
    /// Toggles a geometric edge in the selection (viewport left-click on an edge). The ids
    /// live in whichever space <see cref="ProjectSession.ScopeIsGeometric"/> names, exactly
    /// as the scope list uses — and because the list rows follow the session collection, a
    /// viewport pick ticks the matching row with no extra plumbing.
    /// </summary>
    public void ToggleEdgeSelection(int edgeId)
    {
        if (_session.SelectedEdges.Contains(edgeId))
            _session.SelectedEdges.Remove(edgeId);
        else
            _session.SelectedEdges.Add(edgeId);
        _session.RaiseHighlightsInvalidated();
        _session.StatusText = _session.SelectedEdges.Count == 0
            ? "Ready"
            : $"Selected edges: {string.Join(", ", _session.SelectedEdges)}";
    }

    /// <summary>Toggles a geometric vertex in the selection (viewport left-click on a corner).</summary>
    public void ToggleVertexSelection(int vertexId)
    {
        if (_session.SelectedVertices.Contains(vertexId))
            _session.SelectedVertices.Remove(vertexId);
        else
            _session.SelectedVertices.Add(vertexId);
        _session.RaiseHighlightsInvalidated();
        _session.StatusText = _session.SelectedVertices.Count == 0
            ? "Ready"
            : $"Selected vertices: {string.Join(", ", _session.SelectedVertices)}";
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
            VertexIds = SelectedVerticesOrNull(),
            GeometryEdgeIds = SelectedGeometryEdgesOrNull(),
            GeometryVertexIds = SelectedGeometryVerticesOrNull()
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
            GeometryEdgeIds = SelectedGeometryEdgesOrNull(),
            GeometryVertexIds = SelectedGeometryVerticesOrNull(),
            Volts = VoltageValue
        });
    }

    [RelayCommand]
    private void AddCurrent()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new CurrentFlow
        {
            Name = $"{(CurrentEquipotential ? "Terminal" : "Current")} {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            TotalCurrent = CurrentValue,
            Equipotential = CurrentEquipotential
        });
    }

    [RelayCommand]
    private void AddCharge()
    {
        if (!ValidateScopeSelection(allowZeroArea: false)) return;
        AddCondition(new ElectricCharge
        {
            Name = $"Charge {BoundaryConditions.Count + 1}",
            FaceIds = _session.SelectedFaces.ToList(),
            TotalCharge = ChargeValue
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
            GeometryEdgeIds = SelectedGeometryEdgesOrNull(),
            GeometryVertexIds = SelectedGeometryVerticesOrNull(),
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
        ConvectionProfile? profile = null;
        if (ConvectionVaries)
        {
            if (!TryBuildProfile(out profile, out string? failure))
            {
                _log.Append($"Convection not added: {failure}");
                return;
            }
        }
        AddCondition(new Convection
        {
            Name = $"Convection {BoundaryConditions.Count + 1}" + (profile is null ? "" : " (varying)"),
            FaceIds = _session.SelectedFaces.ToList(),
            Coefficient = ConvectionCoefficient,
            AmbientTemperature = AmbientTemperature,
            Profile = profile
        });
    }

    /// <summary>The profile the panel describes: the leading edge and flow direction, the
    /// boundary layer's exponent, the factor table and the wakes. Every refusal names the field.</summary>
    private bool TryBuildProfile(out ConvectionProfile? profile, out string? failure)
    {
        profile = null;
        failure = null;
        var direction = new Vector3D(FlowDirectionX, FlowDirectionY, FlowDirectionZ);
        if (!(direction.Length > 0)) { failure = "the flow direction must not be zero"; return false; }
        double? exponent = BoundaryLayerExponent == 0 ? null : BoundaryLayerExponent;
        if (exponent is { } e && !(e > -1 && e <= 0)) { failure = "the boundary-layer exponent must lie in (−1, 0]"; return false; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        List<double>? distances = null, factors = null;
        if (!string.IsNullOrWhiteSpace(ConvectionTable))
        {
            distances = new List<double>();
            factors = new List<double>();
            foreach (var pair in ConvectionTable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out double mm)
                    || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double factor))
                { failure = $"the factor table entry '{pair}' is not 'mm:factor'"; return false; }
                if (distances.Count > 0 && !(mm * 1e-3 > distances[^1])) { failure = "the table's distances must rise"; return false; }
                distances.Add(mm * 1e-3);
                factors.Add(factor);
            }
        }
        var wakes = new List<ConvectionWake>();
        if (!string.IsNullOrWhiteSpace(ConvectionWakes))
            foreach (var entry in ConvectionWakes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var v = entry.Split(',', StringSplitOptions.TrimEntries);
                var numbers = new double[6];
                if (v.Length != 6 || v.Select((s, k) => double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out numbers[k])).Any(ok => !ok))
                { failure = $"the wake '{entry}' is not 'x,y,z,half-width,length,factor' (mm; factor plain)"; return false; }
                if (!(numbers[3] > 0 && numbers[4] > 0 && numbers[5] >= 0))
                { failure = $"the wake '{entry}' needs a positive half-width and length and a factor of 0 or more"; return false; }
                wakes.Add(new ConvectionWake(new Vector3D(numbers[0], numbers[1], numbers[2]) * 1e-3, numbers[3] * 1e-3, numbers[4] * 1e-3, numbers[5]));
            }
        if (exponent is null && distances is null && wakes.Count == 0)
        { failure = "a varying coefficient needs an exponent, a factor table or a wake"; return false; }
        profile = new ConvectionProfile
        {
            LeadingEdge = new Vector3D(LeadingEdgeXMm, LeadingEdgeYMm, LeadingEdgeZMm) * 1e-3,
            FlowDirection = direction,
            Exponent = exponent,
            Distances = distances,
            Factors = factors,
            Wakes = wakes
        };
        return true;
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

    // The selection is in geometry ids wherever the body has geometry, so it is stored in
    // the matching property: geometry ids survive remeshing and are resolved to mesh ids at
    // solve time, while mesh ids go stale the moment the element size changes.
    private IReadOnlyList<int>? SelectedEdgesOrNull() =>
        !_session.ScopeIsGeometric && _session.SelectedEdges.Count > 0
            ? _session.SelectedEdges.ToList() : null;

    private IReadOnlyList<int>? SelectedVerticesOrNull() =>
        !_session.ScopeIsGeometric && _session.SelectedVertices.Count > 0
            ? _session.SelectedVertices.ToList() : null;

    private IReadOnlyList<int>? SelectedGeometryEdgesOrNull() =>
        _session.ScopeIsGeometric && _session.SelectedEdges.Count > 0
            ? _session.SelectedEdges.ToList() : null;

    private IReadOnlyList<int>? SelectedGeometryVerticesOrNull() =>
        _session.ScopeIsGeometric && _session.SelectedVertices.Count > 0
            ? _session.SelectedVertices.ToList() : null;

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
