using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>A set of faces exchanging radiation (FU-44, <see cref="SurfaceRadiation"/>).</summary>
public partial class RadiationSetRow : ObservableObject
{
    public RadiationSetRow(string name, IReadOnlyList<int> faceIds)
    {
        _name = name;
        FaceIds = faceIds;
    }

    public IReadOnlyList<int> FaceIds { get; }
    public string FaceSummary => $"{FaceIds.Count} face(s): {string.Join(", ", FaceIds.Take(8))}{(FaceIds.Count > 8 ? ", …" : "")}";
    [ObservableProperty] private string _name;
    [ObservableProperty] private double _ambientKelvin = 293.15;
    /// <summary>One emissivity for the set; 0 takes each face's material emissivity.</summary>
    [ObservableProperty] private double _emissivity;
    [ObservableProperty] private bool _obstruction = true;

    public SurfaceRadiation ToModel() => new()
    {
        Name = Name, FaceIds = FaceIds, AmbientTemperature = AmbientKelvin,
        Emissivity = Emissivity > 0 ? Emissivity : null, Obstruction = Obstruction
    };
}

/// <summary>A surface the recovered flux is integrated over (FU-45, <see cref="FluxProbe"/>): a
/// plane through a point with a normal, or a set of boundary faces.</summary>
public partial class FluxProbeRow : ObservableObject
{
    public FluxProbeRow(string name, IReadOnlyList<int>? faceIds = null)
    {
        _name = name;
        FaceIds = faceIds;
    }

    public IReadOnlyList<int>? FaceIds { get; }
    public bool IsPlane => FaceIds is null;
    public string Kind => FaceIds is null ? "plane" : $"{FaceIds.Count} face(s)";
    [ObservableProperty] private string _name;
    [ObservableProperty] private double _originXMm;
    [ObservableProperty] private double _originYMm;
    [ObservableProperty] private double _originZMm;
    [ObservableProperty] private double _normalX = 1;
    [ObservableProperty] private double _normalY;
    [ObservableProperty] private double _normalZ;

    public FluxProbe ToModel() => FaceIds is { } faces
        ? new FluxProbe { Name = Name, FaceIds = faces }
        : new FluxProbe
        {
            Name = Name,
            Origin = new Vector3D(OriginXMm, OriginYMm, OriginZMm) * 1e-3,
            Normal = new Vector3D(NormalX, NormalY, NormalZ)
        };
}

/// <summary>
/// Solve options that ride on the solve input (FU-42, FU-44, FU-45; FU-47): the electrostatic
/// solve's open outer boundary, flux recovery with its error estimate and flux probes, radiation
/// exchange among faces that see each other, and a transient that starts from the last steady
/// thermal result instead of a uniform temperature. Radiation sets and probes live with the
/// session (they are not saved in the project); the open-boundary flag is saved with the solver
/// settings.
/// </summary>
public partial class SolveViewModel
{
    /// <summary>The electrostatic solve's outer boundary as open space (the asymptotic 1/r
    /// condition) instead of a zero-flux wall.</summary>
    [ObservableProperty] private bool _electrostaticOpenBoundary;

    /// <summary>Recover the flux (current density, heat flux) and estimate its error, with the
    /// flux through every probe (DC conduction and steady heat conduction).</summary>
    [ObservableProperty] private bool _recoverFlux;
    public ObservableCollection<FluxProbeRow> FluxProbes { get; } = new();

    /// <summary>Radiation exchange sets (steady and transient heat conduction, and Z_th).</summary>
    public ObservableCollection<RadiationSetRow> RadiationSets { get; } = new();

    /// <summary>Start a transient from the last steady thermal result on this body's mesh.</summary>
    [ObservableProperty] private bool _startFromLastSteady;
    [ObservableProperty] private string _lastSteadyStatus = "No steady thermal result yet.";
    private (string Body, int Nodes, SolveOutput Output)? _lastSteady;

    [RelayCommand]
    private void AddPlaneProbe() => FluxProbes.Add(new FluxProbeRow($"plane {FluxProbes.Count + 1}"));

    [RelayCommand]
    private void AddFaceProbe()
    {
        if (_session.SelectedFaces.Count == 0) { _log.Append("Select the faces the probe integrates over first."); return; }
        FluxProbes.Add(new FluxProbeRow($"faces {FluxProbes.Count + 1}", _session.SelectedFaces.ToList()));
        _session.ClearScopeSelection();
        _session.RaiseHighlightsInvalidated();
    }

    [RelayCommand]
    private void RemoveProbe(FluxProbeRow? row) { if (row is not null) FluxProbes.Remove(row); }

    [RelayCommand]
    private void AddRadiationSet()
    {
        if (_session.SelectedFaces.Count < 1) { _log.Append("Select the faces that radiate to each other first."); return; }
        var row = new RadiationSetRow($"Radiation {RadiationSets.Count + 1}", _session.SelectedFaces.ToList());
        RadiationSets.Add(row);
        _log.Append($"Added radiation set '{row.Name}' on {row.FaceSummary}.");
        _session.ClearScopeSelection();
        _session.RaiseHighlightsInvalidated();
    }

    [RelayCommand]
    private void RemoveRadiationSet(RadiationSetRow? row) { if (row is not null) RadiationSets.Remove(row); }

    /// <summary>The electrostatic settings the panel asks for; null = the zero-flux wall.</summary>
    private ElectrostaticSettings? BuildElectrostaticSettings(AnalysisType kind) =>
        kind == AnalysisType.Electrostatic && ElectrostaticOpenBoundary ? new ElectrostaticSettings { OpenBoundary = true } : null;

    /// <summary>Flux recovery for the scalar steady solves; null = off.</summary>
    private FluxRecoverySettings? BuildFluxRecovery(AnalysisType kind) =>
        RecoverFlux && kind is AnalysisType.Electrical or AnalysisType.Thermal
            ? new FluxRecoverySettings { Probes = FluxProbes.Select(p => p.ToModel()).ToList() }
            : null;

    /// <summary>The radiation sets for the single-body thermal solves; null = none.</summary>
    private IReadOnlyList<SurfaceRadiation>? BuildRadiationSets(AnalysisType kind) =>
        RadiationSets.Count > 0 && kind is AnalysisType.Thermal or AnalysisType.TransientThermal
            ? RadiationSets.Select(r => r.ToModel()).ToList()
            : null;

    /// <summary>The transient's starting state: the last steady thermal result when asked for and
    /// it was solved on this body's present mesh; otherwise the uniform initial temperature.</summary>
    private TransientThermalSettings WithInitialState(TransientThermalSettings settings, Body body)
    {
        if (!StartFromLastSteady) return settings;
        if (_lastSteady is { } last && last.Body == body.Name && body.Mesh is { } mesh && last.Nodes == mesh.NodeCount)
        {
            _log.Append($"Transient starts from the steady result solved on '{last.Body}' ({last.Nodes:N0} nodes).");
            return settings with { InitialState = ThermalInitialState.FromSteady(last.Output) };
        }
        _log.Append("No steady thermal result on this body's present mesh to start from; the transient starts at the uniform initial temperature.");
        return settings;
    }

    /// <summary>Keeps a steady thermal solve's result for a later transient to start from.</summary>
    private void RememberSteady(AnalysisType kind, Body body, SolveOutput output)
    {
        if (kind != AnalysisType.Thermal || body.Mesh is null) return;
        if (!output.Fields.OfType<OpenSim.Core.Results.NodalScalarField>().Any(f => f.Name == "Temperature")) return;
        _lastSteady = (body.Name, body.Mesh.NodeCount, output);
        LastSteadyStatus = $"Steady result on '{body.Name}' ({body.Mesh.NodeCount:N0} nodes) kept to start a transient from.";
    }
}
