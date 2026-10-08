using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Power;
using OpenSim.Pcb.Thermal;

namespace OpenSim.App.ViewModels;

/// <summary>One pad of the meshed net in the rail table: what it is on the rail and the
/// number that goes with it.</summary>
public partial class RailPadRow : ObservableObject
{
    public const string NoRole = "—", SourceRole = "Source", LoadRole = "Load";

    public RailPadRow(NetMesher.PadElectrode pad) => Pad = pad;

    public NetMesher.PadElectrode Pad { get; }
    public string Label => Pad.Label;
    public IReadOnlyList<string> Roles { get; } = new[] { NoRole, SourceRole, LoadRole };

    [ObservableProperty] private string _role = NoRole;

    /// <summary>Volts for a source, amperes for a load.</summary>
    [ObservableProperty] private double _value;

    /// <summary>Output resistance of a source [mΩ]; ignored on a load.</summary>
    [ObservableProperty] private double _outputMilliohms;
}

/// <summary>
/// Rail-level DC power integrity on the meshed net: several sources and loads at once,
/// each load's voltage against a limit, via currents and neck-downs
/// (<see cref="RailAnalysis"/>). The PCB view model hands over the mesh after meshing a
/// net, exactly as it hands the pads to <see cref="ElectrodesViewModel"/>.
/// </summary>
public partial class PowerRailViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly MaterialsViewModel _materials;
    private readonly EnvironmentViewModel _environment;
    private readonly CfdSetupViewModel _cfd;
    private NetMesher.Result? _mesh;
    private CopperNet? _net;

    public PowerRailViewModel(ProjectSession session, ILogService log, MaterialsViewModel materials,
        EnvironmentViewModel environment, CfdSetupViewModel cfd)
    {
        _session = session;
        _log = log;
        _materials = materials;
        _environment = environment;
        _cfd = cfd;
        // Pads of a body that is gone must not be solved against the new one.
        session.GeometryReplaced += (_, _) => Clear();
    }

    public ObservableCollection<RailPadRow> Pads { get; } = new();
    public ObservableCollection<string> ResultLines { get; } = new();

    /// <summary>Voltage the drops are measured from [V]; 0 = the highest source.</summary>
    [ObservableProperty] private double _nominalVolts;
    [ObservableProperty] private double _dropLimitPercent = 3.0;

    /// <summary>Neck-down limit [A/mm²]; 0 = report the densest spot only.</summary>
    [ObservableProperty] private double _currentDensityLimit = 50.0;

    /// <summary>Per-via limit [A]; 0 = not checked.</summary>
    [ObservableProperty] private double _viaCurrentLimit;
    [ObservableProperty] private double _copperConductivity = 5.96e7;
    [ObservableProperty] private string _railResult = "";
    [ObservableProperty] private string _railAssumptions = "";

    /// <summary>The board, its thermal options and parts for the whole-board thermal body.</summary>
    public sealed record BoardThermalContext(PcbBoard Board, BoardThermalOptions Options, IReadOnlyList<ThermalComponent> Parts);

    /// <summary>Set by the PCB panel: (trace directions, with the parts) → the board's thermal
    /// context, or the reason there is none.</summary>
    public Func<bool, bool, (BoardThermalContext? Context, string? Problem)>? BoardThermal { get; set; }

    /// <summary>Solve the heat on the whole board (planes, other nets, parts) instead of the
    /// net's own mesh. The net must be meshed with the board.</summary>
    [ObservableProperty] private bool _wholeBoardThermal;

    /// <summary>On the whole board, trace copper conducts along its traces (orthotropic).</summary>
    [ObservableProperty] private bool _traceDirections = true;

    /// <summary>On the whole board, the parts of the component table with their losses.</summary>
    [ObservableProperty] private bool _includeParts;

    /// <summary>Cool by a conjugate CFD solve (the CFD setup of the Thermal workspace) instead of
    /// the environment's film.</summary>
    [ObservableProperty] private bool _coolWithCfd;

    /// <summary>CFD cell size [mm]; 0 = automatic.</summary>
    [ObservableProperty] private double _cfdCellSizeMm;

    [ObservableProperty] private double _transientDurationSeconds = 600;
    [ObservableProperty] private double _transientStepSeconds = 10;

    /// <summary>The mesh and net the last hand-over installed; null before any.</summary>
    public (NetMesher.Result Mesh, CopperNet Net)? Current =>
        _mesh is null || _net is null ? null : (_mesh, _net);

    /// <summary>Installs a freshly meshed net: every pad starts with no role.</summary>
    public void Load(NetMesher.Result mesh, CopperNet net)
    {
        Clear();
        _mesh = mesh;
        _net = net;
        foreach (var pad in mesh.Pads) Pads.Add(new RailPadRow(pad));
    }

    private void Clear()
    {
        _mesh = null;
        _net = null;
        Pads.Clear();
        ResultLines.Clear();
        RailResult = "";
        RailAssumptions = "";
    }

    /// <summary>
    /// The table as a rail setup, or the reason it is not one. Pads of ONE component with
    /// the same role are one terminal (the part ties them together): a source at their
    /// common voltage, a load drawing the sum of their currents. A pad with no component
    /// is a terminal of its own.
    /// </summary>
    public RailSetup? BuildSetup(out string? problem)
    {
        problem = null;
        var sources = new List<RailSource>();
        var sinks = new List<RailSink>();
        foreach (var group in Pads.Where(r => r.Role != RailPadRow.NoRole)
                     .GroupBy(r => (r.Role, Key: r.Pad.ComponentRef ?? r.Pad.Label)))
        {
            var rows = group.ToList();
            var pads = rows.Select(r => r.Pad).ToList();
            if (group.Key.Role == RailPadRow.SourceRole)
            {
                var volts = rows.Select(r => r.Value).Where(v => v != 0).Distinct().ToList();
                if (volts.Count != 1)
                {
                    problem = volts.Count == 0
                        ? $"Source {group.Key.Key}: enter its voltage."
                        : $"Source {group.Key.Key}: its pads carry different voltages " +
                          $"({string.Join(", ", volts.Select(v => v.ToString("g4")))} V); one part has one output.";
                    return null;
                }
                sources.Add(new RailSource(group.Key.Key, pads, volts[0])
                {
                    OutputResistance = rows.Max(r => r.OutputMilliohms) * 1e-3
                });
            }
            else
            {
                sinks.Add(new RailSink(group.Key.Key, pads, rows.Sum(r => r.Value)));
            }
        }
        if (sources.Count == 0) { problem = "Give at least one pad the Source role and a voltage."; return null; }
        if (sinks.Count == 0) { problem = "Give at least one pad the Load role and a current."; return null; }
        return new RailSetup
        {
            Sources = sources,
            Sinks = sinks,
            NominalVolts = NominalVolts,
            DropLimitPercent = DropLimitPercent,
            CurrentDensityLimit = CurrentDensityLimit * 1e6,
            ViaCurrentLimit = ViaCurrentLimit,
            CopperConductivity = CopperConductivity
        };
    }

    [RelayCommand]
    private async Task SolveRailAsync()
    {
        if (_mesh is null || _net is null)
        {
            RailResult = "Mesh a net first.";
            return;
        }
        var setup = BuildSetup(out string? problem);
        if (setup is null)
        {
            RailResult = problem ?? "";
            return;
        }
        var mesh = _mesh;
        var net = _net;
        _session.IsBusy = true;
        _session.StatusText = "Solving the rail…";
        try
        {
            var report = await Task.Run(() => RailAnalysis.Solve(mesh, net, setup));
            Show(report);
            _session.RaiseResultsProduced(report.Fields, preferFieldName: "Electric potential");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }

    /// <summary>
    /// The rail with the heat it makes: the copper loss heats the copper and the laminate,
    /// the environment cools them, and the copper's resistivity follows its temperature
    /// (<see cref="RailThermalAnalysis"/>). Materials are the meshed body's region
    /// materials; the environment is the one the Thermal workspace edits.
    /// </summary>
    [RelayCommand]
    private async Task SolveRailThermalAsync()
    {
        if (_mesh is null || _net is null)
        {
            RailResult = "Mesh a net first (with the laminate, for a board temperature).";
            return;
        }
        var setup = BuildSetup(out string? problem);
        if (setup is null)
        {
            RailResult = problem ?? "";
            return;
        }
        OpenSim.Core.Model.Material copper, laminate;
        try
        {
            var regions = _materials.ResolveRegionMaterials(_mesh.Body);
            copper = regions?.GetValueOrDefault(OpenSim.Pcb.Extrude.PcbStackup.CopperRegion)
                     ?? _materials.DefaultConductor();
            laminate = regions?.GetValueOrDefault(OpenSim.Pcb.Extrude.PcbStackup.DielectricRegion)
                       ?? _materials.FindByName("FR4 (PCB laminate)") ?? copper;
        }
        catch (InvalidOperationException ex)
        {
            RailResult = ex.Message;
            return;
        }
        var environment = _environment.Build();
        var (context, contextProblem) = WholeBoardThermal
            ? BoardThermal?.Invoke(TraceDirections, IncludeParts) ?? (null, "Import a board first (PCB panel).")
            : (null, null);
        if (WholeBoardThermal && context is null)
        {
            RailResult = contextProblem ?? "";
            return;
        }
        Func<OpenSim.Core.Interfaces.SolveInput, CancellationToken, OpenSim.Core.Interfaces.SolveOutput>? cooling = null;
        if (CoolWithCfd)
        {
            var cfd = _cfd.Build(Math.Max(0, CfdCellSizeMm) * 1e-3, environment.FlowVelocity);
            if (cfd is null)
            {
                RailResult = "Set up the CFD domain in the Thermal workspace first.";
                return;
            }
            cooling = OpenSim.Cfd.ConjugateCooling.Steady(cfd);
        }
        var mesh = _mesh;
        var net = _net;
        _session.IsBusy = true;
        _session.StatusText = "Solving the rail with self-heating…";
        try
        {
            var report = await Task.Run(() =>
            {
                var board = context is null ? null : BoardThermalMesher.Mesh(context.Board, context.Parts, context.Options);
                return RailThermalAnalysis.Solve(mesh, net, new RailThermalSetup
                {
                    Rail = setup, Copper = copper, Laminate = laminate, Environment = environment,
                    Board = board, Components = context?.Parts ?? Array.Empty<ThermalComponent>(), Cooling = cooling
                });
            });
            Show(report.Rail, report.Describe());
            RailAssumptions = "Assumptions: " + string.Join("; ", report.Assumptions) + ".";
            foreach (string line in report.Log) _log.Append($"Rail: {line}");
            foreach (string line in report.Describe()) _log.Append($"Rail: {line}");
            foreach (var part in report.Components)
                ResultLines.Add($"{part.RefDes}: junction {part.JunctionKelvin - 273.15:f1} °C, {part.ToBoardWatts:g3} W into the board.");
            _session.RaiseResultsProduced(report.Fields, preferFieldName: "Temperature");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }

    /// <summary>
    /// The rail's coupled transient from the ambient: how fast the copper heats and what the
    /// drop is after the duration given (<see cref="RailThermalAnalysis.SolveTransient"/>).
    /// Cooled by the environment; the whole-board option applies, the CFD one does not.
    /// </summary>
    [RelayCommand]
    private async Task SolveRailTransientAsync()
    {
        if (_mesh is null || _net is null)
        {
            RailResult = "Mesh a net first (with the laminate, for a board temperature).";
            return;
        }
        var setup = BuildSetup(out string? problem);
        if (setup is null)
        {
            RailResult = problem ?? "";
            return;
        }
        if (!(TransientStepSeconds > 0 && TransientDurationSeconds >= TransientStepSeconds))
        {
            RailResult = "Give a positive time step and a duration of at least one step.";
            return;
        }
        OpenSim.Core.Model.Material copper, laminate;
        try
        {
            var regions = _materials.ResolveRegionMaterials(_mesh.Body);
            copper = regions?.GetValueOrDefault(OpenSim.Pcb.Extrude.PcbStackup.CopperRegion) ?? _materials.DefaultConductor();
            laminate = regions?.GetValueOrDefault(OpenSim.Pcb.Extrude.PcbStackup.DielectricRegion)
                       ?? _materials.FindByName("FR4 (PCB laminate)") ?? copper;
        }
        catch (InvalidOperationException ex)
        {
            RailResult = ex.Message;
            return;
        }
        var environment = _environment.Build();
        var (context, contextProblem) = WholeBoardThermal
            ? BoardThermal?.Invoke(TraceDirections, IncludeParts) ?? (null, "Import a board first (PCB panel).")
            : (null, null);
        if (WholeBoardThermal && context is null)
        {
            RailResult = contextProblem ?? "";
            return;
        }
        var settings = new OpenSim.Core.Interfaces.TransientThermalSettings
        {
            InitialTemperature = environment.AmbientTemperature,
            Duration = TransientDurationSeconds,
            TimeStep = TransientStepSeconds
        };
        var mesh = _mesh;
        var net = _net;
        _session.IsBusy = true;
        _session.StatusText = "Solving the rail's self-heating transient…";
        try
        {
            var report = await Task.Run(() =>
            {
                var board = context is null ? null : BoardThermalMesher.Mesh(context.Board, context.Parts, context.Options);
                return RailThermalAnalysis.SolveTransient(mesh, net, new RailThermalSetup
                {
                    Rail = setup, Copper = copper, Laminate = laminate, Environment = environment,
                    Board = board, Components = context?.Parts ?? Array.Empty<ThermalComponent>()
                }, settings);
            });
            Show(report.Rail, report.Describe());
            RailAssumptions = "Assumptions: " + string.Join("; ", report.Assumptions) + ".";
            foreach (string line in report.Solution.Log) _log.Append($"Rail transient: {line}");
            foreach (var (time, loss, peak) in report.Solution.History)
                _log.Append($"Rail transient: t = {time:g4} s, loss {loss * 1e3:g4} mW, hottest copper {peak - 273.15:f2} °C");
            // The frames are on the thermal body: shown when that is the net's own mesh.
            if (context is null)
                _session.RaiseResultsProduced(report.Solution.Thermal.Fields, preferFieldName: "Temperature",
                    frames: report.Solution.Thermal.Frames, frameAxis: report.Solution.Thermal.FrameAxis);
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }

    /// <summary>Puts a report on the panel and in the log.</summary>
    public void Show(RailReport report, IEnumerable<string>? extraLines = null)
    {
        ResultLines.Clear();
        var lines = report.Describe();
        RailResult = lines[0];
        foreach (string line in lines.Skip(1)) ResultLines.Add(line.Trim());
        if (extraLines is not null)
            foreach (string line in extraLines) ResultLines.Add(line);
        RailAssumptions = "Assumptions: " + string.Join("; ", report.Assumptions) + ".";
        foreach (string line in report.Log) _log.Append($"Rail: {line}");
        foreach (string line in lines) _log.Append($"Rail: {line}");
    }
}
