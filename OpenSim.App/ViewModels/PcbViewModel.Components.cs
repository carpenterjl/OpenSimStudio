using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.Core.Model;
using OpenSim.Pcb.Thermal;

namespace OpenSim.App.ViewModels;

/// <summary>One part in the junction-temperature table.</summary>
public partial class ThermalPartRow : ObservableObject
{
    public ThermalPartRow(PlacedPart part)
    {
        Part = part;
        if (part.PadExtent is { } extent)
        {
            var xs = extent.Outer.Select(p => p.X).ToList();
            var ys = extent.Outer.Select(p => p.Y).ToList();
            _widthMm = Math.Round((xs.Max() - xs.Min()) * 1e3, 3);
            _heightMm = Math.Round((ys.Max() - ys.Min()) * 1e3, 3);
        }
    }

    public PlacedPart Part { get; }
    public string Label => $"{Part.RefDes}{(Part.Part is null ? "" : $" ({Part.Part})")} — {(Part.OnTop ? "top" : "bottom")}";

    /// <summary>Dissipation [W]; a part at 0 W is left out of the solve.</summary>
    [ObservableProperty] private double _powerWatts;
    [ObservableProperty] private double _thetaJc = 10;
    [ObservableProperty] private double _thetaJb = 10;

    /// <summary>Contact size [mm]: the pad extent from the layout, or the body size typed in.</summary>
    [ObservableProperty] private double _widthMm;
    [ObservableProperty] private double _heightMm;

    /// <summary>Case top to ambient through a heatsink [K/W]; 0 = bare case top in air.</summary>
    [ObservableProperty] private double _heatsinkKelvinPerWatt;
}

/// <summary>
/// Junction temperatures of the board's parts (<see cref="ComponentThermalAnalysis"/>):
/// the whole board is meshed with its copper, each powered part is a two-resistor model
/// on its footprint, and the Thermal workspace's environment cools the board.
/// </summary>
public partial class PcbViewModel
{
    public ObservableCollection<ThermalPartRow> ThermalParts { get; } = new();
    public ObservableCollection<string> ComponentResultLines { get; } = new();

    [ObservableProperty] private string _componentResult = "";
    [ObservableProperty] private string _componentAssumptions = "";

    /// <summary>Film coefficient on bare case tops [W/(m²·K)].</summary>
    [ObservableProperty] private double _caseFilmCoefficient = 10;

    /// <summary>Board element size for the thermal mesh [mm]; 0 = automatic.</summary>
    [ObservableProperty] private double _thermalEdgeMm;

    private void ClearThermalParts()
    {
        ThermalParts.Clear();
        ComponentResultLines.Clear();
        ComponentResult = "";
        ComponentAssumptions = "";
    }

    /// <summary>Lists the parts the board file names on its pads.</summary>
    [RelayCommand]
    private void ListThermalParts()
    {
        if (_board is null)
        {
            ComponentResult = "Import a board first.";
            return;
        }
        ClearThermalParts();
        foreach (var part in ComponentPlacement.FromPads(_board)) ThermalParts.Add(new ThermalPartRow(part));
        ComponentResult = ThermalParts.Count == 0
            ? "The board file names no components on its pads (IPC-2581, or Gerber with X2 attributes). Load a placement file instead."
            : $"{ThermalParts.Count} parts. Enter power and the datasheet's θJC and θJB for the ones that dissipate.";
    }

    /// <summary>Lists the parts of a pick-and-place file; each needs a body size.</summary>
    [RelayCommand]
    private void LoadPlacementFile()
    {
        if (_board is null)
        {
            ComponentResult = "Import a board first.";
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Placement (pick-and-place) file",
            Filter = "Placement files (*.pos;*.csv;*.txt)|*.pos;*.csv;*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var parts = ComponentPlacement.ReadPlacementFile(File.ReadAllText(dialog.FileName), out var notes);
            ClearThermalParts();
            foreach (var part in parts) ThermalParts.Add(new ThermalPartRow(part));
            foreach (string note in notes) _log.Append($"Placement: {note}");
            ComponentResult = $"{parts.Count} parts from {Path.GetFileName(dialog.FileName)}. " +
                              "A placement file gives centres only: enter each powered part's body size.";
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    [RelayCommand]
    private async Task SolveComponentTemperaturesAsync()
    {
        var board = _board;
        if (board is null)
        {
            ComponentResult = "Import a board first.";
            return;
        }
        var environment = _session.Project.Environment;
        if (environment is null)
        {
            ComponentResult = "Set the environment in the Thermal workspace first.";
            return;
        }
        var components = new List<ThermalComponent>();
        foreach (var row in ThermalParts.Where(r => r.PowerWatts > 0))
        {
            if (!(row.WidthMm > 0 && row.HeightMm > 0))
            {
                ComponentResult = $"{row.Part.RefDes}: enter its body size.";
                return;
            }
            components.Add(new ThermalComponent
            {
                RefDes = row.Part.RefDes, Part = row.Part.Part, OnTop = row.Part.OnTop,
                Footprint = ComponentPlacement.Rectangle(row.Part.Center, row.WidthMm * 1e-3, row.HeightMm * 1e-3,
                    row.Part.PadExtent is null ? row.Part.RotationDegrees : 0),
                PowerWatts = row.PowerWatts, ThetaJc = row.ThetaJc, ThetaJb = row.ThetaJb,
                CaseToAmbient = row.HeatsinkKelvinPerWatt > 0 ? row.HeatsinkKelvinPerWatt : null,
                CaseFilmCoefficient = CaseFilmCoefficient
            });
        }
        if (components.Count == 0)
        {
            ComponentResult = "Give at least one part a power.";
            return;
        }
        Material copper = _materials.DefaultConductor();
        Material? laminate = _materials.FindByName("FR4 (PCB laminate)");
        if (laminate is null)
        {
            ComponentResult = "The material library has no 'FR4 (PCB laminate)'.";
            return;
        }
        var options = new BoardThermalOptions
        {
            Stackup = BuildStackupSettings(), Copper = copper, Laminate = laminate,
            TargetEdgeLength = Math.Max(0, ThermalEdgeMm) * 1e-3,
            ViaPlatingThickness = ViaPlatingMicrons * 1e-6
        };

        _session.IsBusy = true;
        _session.StatusText = "Solving component temperatures…";
        try
        {
            var (mesh, report) = await Task.Run(() =>
            {
                var m = BoardThermalMesher.Mesh(board, components, options);
                return (m, ComponentThermalAnalysis.Solve(m, new ComponentThermalSetup
                {
                    Components = components, Environment = environment
                }));
            });

            // The board mesh becomes the body on screen so the temperature map has
            // something to be drawn on. Its regions are copper shares of the laminate,
            // which the material library has no names for: they all carry the laminate's.
            var skin = new TriangleMesh(mesh.Mesh.Nodes,
                mesh.Mesh.BoundaryTriangles.Select(b => new Triangle(b.A, b.B, b.C)).ToList(),
                mesh.Mesh.BoundaryTriangles.Select(b => b.FaceId).ToList());
            LoadImportedBody(new Body
            {
                Name = "Board (thermal)", Geometry = skin, GeometrySource = "PCB board, thermal mesh", Mesh = mesh.Mesh,
                RegionMaterialNames = mesh.RegionMaterials.Keys.ToDictionary(k => k, _ => laminate.Name)
            }, options.Stackup);
            ShowCopperPreview = false;

            var lines = report.Describe();
            ComponentResultLines.Clear();
            ComponentResult = lines[0];
            foreach (string line in lines.Skip(1)) ComponentResultLines.Add(line);
            ComponentAssumptions = "Assumptions: " + string.Join("; ", report.Assumptions) + ".";
            foreach (string line in report.Log) _log.Append($"Components: {line}");
            foreach (string line in lines) _log.Append($"Components: {line}");
            _session.RaiseResultsProduced(report.Fields, preferFieldName: "Temperature");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }
}
