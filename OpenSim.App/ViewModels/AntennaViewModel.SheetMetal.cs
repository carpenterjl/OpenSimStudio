using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Rf;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>One flat rectangle of a sheet-metal assembly [mm]: its two corners; one coordinate must
/// be equal (the plane it lies in).</summary>
public partial class SheetPartRow : ObservableObject
{
    [ObservableProperty] private string _name = "plate";
    [ObservableProperty] private double _x0Mm;
    [ObservableProperty] private double _y0Mm;
    [ObservableProperty] private double _z0Mm;
    [ObservableProperty] private double _x1Mm;
    [ObservableProperty] private double _y1Mm;
    [ObservableProperty] private double _z1Mm;
}

/// <summary>A port of a sheet-metal assembly: a straight cut along mesh lines [mm], the direction the
/// positive current crosses it, and what closes it.</summary>
public partial class SheetPortRow : ObservableObject
{
    public static IReadOnlyList<string> Directions { get; } = new[] { "+x", "−x", "+y", "−y", "+z", "−z" };
    public static IReadOnlyList<string> Loads => PortLoads.All;

    [ObservableProperty] private string _name = "feed";
    [ObservableProperty] private double _fromXMm;
    [ObservableProperty] private double _fromYMm;
    [ObservableProperty] private double _fromZMm;
    [ObservableProperty] private double _toXMm;
    [ObservableProperty] private double _toYMm;
    [ObservableProperty] private double _toZMm;
    [ObservableProperty] private string? _direction = "+z";
    [ObservableProperty] private string? _load = PortLoads.Port;
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _q;

    public Vector3D DirectionVector => Direction switch
    {
        "+x" => new Vector3D(1, 0, 0), "−x" => new Vector3D(-1, 0, 0),
        "+y" => new Vector3D(0, 1, 0), "−y" => new Vector3D(0, -1, 0),
        "−z" => new Vector3D(0, 0, -1), _ => new Vector3D(0, 0, 1)
    };
}

/// <summary>
/// Sheet metal in air (Feature 12's air model; FU-47): axis-aligned plates on one shared graded grid —
/// a PIFA's top plate, its feed and shorting tabs, a finite ground plate of its real size — the edges
/// where three or more plates meet carrying one basis per extra plate, the ports along mesh lines.
/// Free space, or over an infinite image ground at z = 0 in place of a meshed ground plate. No
/// dielectric: the model is for metal in air.
/// </summary>
public partial class AntennaViewModel
{
    public ObservableCollection<SheetPartRow> SheetParts { get; } = new();
    public ObservableCollection<SheetPortRow> SheetPorts { get; } = new();
    /// <summary>An infinite PEC ground at z = 0 (image theory) instead of a meshed ground plate.</summary>
    [ObservableProperty] private bool _sheetImageGround;
    /// <summary>The finest element, near part edges and ports [mm]; 0 = a uniform grid.</summary>
    [ObservableProperty] private double _sheetMinEdgeMm;

    // The PIFA filler [mm]: a ground plate, a top plate over its far end, and two tabs down to the
    // ground at the top plate's inner edge — the shorting tab at its corner, the feed tab beside it.
    [ObservableProperty] private double _pifaGroundWidthMm = 40;
    [ObservableProperty] private double _pifaGroundLengthMm = 80;
    [ObservableProperty] private double _pifaTopWidthMm = 20;
    [ObservableProperty] private double _pifaTopLengthMm = 30;
    [ObservableProperty] private double _pifaHeightMm = 6;
    [ObservableProperty] private double _pifaTabWidthMm = 2;
    [ObservableProperty] private double _pifaFeedSpacingMm = 5;

    private bool _sheetRowsTracked;

    private void TrackSheetRows()
    {
        if (_sheetRowsTracked) return;
        _sheetRowsTracked = true;
        foreach (var row in SheetPorts) row.PropertyChanged += OnPortRowChanged;
        SheetPorts.CollectionChanged += (_, e) =>
        {
            foreach (SheetPortRow row in e.NewItems ?? Array.Empty<SheetPortRow>()) row.PropertyChanged += OnPortRowChanged;
            OnPortRowChanged(null, new System.ComponentModel.PropertyChangedEventArgs(nameof(SheetPorts)));
        };
    }

    /// <summary>Fills the parts and the port with a PIFA over a finite ground (or over the image
    /// ground when that is ticked), and centres the sweep on its quarter-wave estimate.</summary>
    [RelayCommand]
    private void FillPifa()
    {
        TrackSheetRows();
        double gw = PifaGroundWidthMm, gl = PifaGroundLengthMm, tw = PifaTopWidthMm, tl = PifaTopLengthMm,
            h = PifaHeightMm, tab = PifaTabWidthMm, spacing = PifaFeedSpacingMm;
        if (!(gw > 0 && gl > 0 && tw > 0 && tl > 0 && h > 0 && tab > 0 && spacing >= tab && tl < gl && tw <= gw
              && tab + spacing + tab / 2 < tw))
        {
            AntennaResult = "The PIFA does not fit: the top plate must sit on the ground, and both tabs (the feed "
                + "one tab-and-a-spacing from the short) on the top plate's inner edge.";
            return;
        }
        double ys = gl / 2 - tl, x0 = -tw / 2, xf = x0 + tab / 2 + spacing;
        SheetParts.Clear();
        if (!SheetImageGround)
            SheetParts.Add(new SheetPartRow { Name = "ground", X0Mm = -gw / 2, Y0Mm = -gl / 2, Z0Mm = 0, X1Mm = gw / 2, Y1Mm = gl / 2, Z1Mm = 0 });
        SheetParts.Add(new SheetPartRow { Name = "top", X0Mm = -tw / 2, Y0Mm = ys, Z0Mm = h, X1Mm = tw / 2, Y1Mm = gl / 2, Z1Mm = h });
        SheetParts.Add(new SheetPartRow { Name = "short", X0Mm = x0, Y0Mm = ys, Z0Mm = 0, X1Mm = x0 + tab, Y1Mm = ys, Z1Mm = h });
        SheetParts.Add(new SheetPartRow { Name = "feed tab", X0Mm = xf - tab / 2, Y0Mm = ys, Z0Mm = 0, X1Mm = xf + tab / 2, Y1Mm = ys, Z1Mm = h });
        SheetPorts.Clear();
        SheetPorts.Add(new SheetPortRow
        {
            Name = "feed", FromXMm = xf - tab / 2, FromYMm = ys, FromZMm = h / 2, ToXMm = xf + tab / 2, ToYMm = ys, ToZMm = h / 2,
            Direction = "+z", Load = PortLoads.Port
        });
        // The quarter-wave estimate: the top plate's two sides and the height make a quarter wavelength.
        double quarter = (tw + tl + h) * 1e-3;
        double f0 = 299_792_458.0 / (4 * quarter);
        FrequencyMHz = Math.Round(f0 / 1e6);
        SweepFMinMHz = Math.Round(0.6 * f0 / 1e6);
        SweepFMaxMHz = Math.Round(1.5 * f0 / 1e6);
        if (SheetMinEdgeMm <= 0) SheetMinEdgeMm = Math.Max(0.25 * tab, 0.25);
        AntennaResult = $"PIFA filled: {SheetParts.Count} plates, the feed port across the feed tab; the sweep is set round the "
            + $"quarter-wave estimate {f0 / 1e6:g4} MHz.";
    }

    [RelayCommand]
    private void AddSheetPart() => SheetParts.Add(new SheetPartRow { Name = $"plate{SheetParts.Count + 1}", X1Mm = 10, Y1Mm = 10 });

    [RelayCommand]
    private void RemoveSheetPart(SheetPartRow? row) { if (row is not null) SheetParts.Remove(row); }

    [RelayCommand]
    private void AddSheetPort()
    {
        TrackSheetRows();
        SheetPorts.Add(new SheetPortRow { Name = $"port{SheetPorts.Count + 1}" });
    }

    [RelayCommand]
    private void RemoveSheetPort(SheetPortRow? row)
    {
        TrackSheetRows();
        if (row is not null) SheetPorts.Remove(row);
    }

    private IReadOnlyList<PortClosure> SheetClosures()
    {
        TrackSheetRows();
        return SheetPorts.Select(p => new PortClosure(p.Name.Trim(), p.Load ?? PortLoads.Port, p.Value, p.Q)).ToList();
    }

    private bool TryBuildSheetMetalModel(out PortedModel model, out IReadOnlyList<PortClosure> closures, out string? failure,
        double refine)
    {
        model = null!;
        closures = SheetClosures();
        failure = null;
        if (SheetParts.Count == 0) { failure = "add the plates (or fill a PIFA)"; return false; }
        if (SheetPorts.Count == 0) { failure = "add a port line"; return false; }
        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        double maxEdge = 299_792_458.0 / maxFrequency / (Math.Max(MeshCellsPerWavelength, 4) * refine);
        double? minEdge = SheetMinEdgeMm > 0 ? Math.Min(SheetMinEdgeMm * 1e-3 / refine, maxEdge) : null;
        var parts = SheetParts.Select(p => new SheetRectangle(p.Name,
            new Vector3D(Math.Min(p.X0Mm, p.X1Mm), Math.Min(p.Y0Mm, p.Y1Mm), Math.Min(p.Z0Mm, p.Z1Mm)) * 1e-3,
            new Vector3D(Math.Max(p.X0Mm, p.X1Mm), Math.Max(p.Y0Mm, p.Y1Mm), Math.Max(p.Z0Mm, p.Z1Mm)) * 1e-3)).ToList();
        var lines = SheetPorts.Select(p => new SheetPortLine(p.Name.Trim(),
            new Vector3D(p.FromXMm, p.FromYMm, p.FromZMm) * 1e-3, new Vector3D(p.ToXMm, p.ToYMm, p.ToZMm) * 1e-3,
            p.DirectionVector)).ToList();
        SheetAssemblyResult assembly;
        try
        {
            assembly = SheetMetalAssembly.Build(parts, lines, maxEdge, maxUnknowns: 4000, minEdgeLength: minEdge,
                imageGround: SheetImageGround ? new GroundPlane(0) : null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            failure = ex.Message;
            return false;
        }
        var solver = new SurfaceMomSolver { SheetImpedance = SheetModel(overGround: SheetImageGround), SheetEdge = SheetEdgeModel() };
        var assumptions = SheetMetalAssembly.Assumptions.ToList();
        if (SheetImageGround)
            assumptions.Insert(0, "Over an infinite perfect ground at z = 0 (image theory): plates touching it are grounded there.");
        model = new SheetMetalModel
        {
            Surface = assembly.Structure,
            PortNames = assembly.Ports.Select(p => p.Name).ToList(),
            Description = $"{assembly.Structure.BasisCount} RWG unknowns ({assembly.Structure.JunctionEdgeCount} junction edge(s)), "
                + $"{parts.Count} plate(s), {(SheetImageGround ? "over the image ground" : "in free space")}",
            Assumptions = assumptions,
            Notes = assembly.Notes,
            GroundZ = SheetImageGround ? 0 : null,
            Solver = solver,
            Ports = assembly.Ports.Select(p => p.Port).ToList()
        };
        return true;
    }

    private sealed class SheetMetalModel : PortedModel
    {
        public required SurfaceMomSolver Solver { get; init; }
        public required IReadOnlyList<SurfacePort> Ports { get; init; }

        public override Complex[,] Impedance(double frequencyHz) =>
            NetworkParameters.Invert(Solver.SolveMultiPort(Surface, frequencyHz, Ports).Admittance);

        /// <summary>The sheet currents of a port excitation — the unit solutions superposed — and the
        /// driven port's impedance with the others closed as the excitation has them.</summary>
        private SurfaceMomSolution Sheet(double f, Complex[] volts)
        {
            var solved = Solver.SolveMultiPort(Surface, f, Ports);
            var edges = new Complex[Surface.BasisCount];
            for (int j = 0; j < volts.Length; j++)
                for (int e = 0; e < edges.Length; e++) edges[e] += volts[j] * solved.EdgeCurrents[j][e];
            // The excitation drives exactly one port with 1 V.
            int driven = Array.FindIndex(volts, v => v == Complex.One);
            Complex current = Complex.Zero;
            if (driven >= 0)
                for (int j = 0; j < volts.Length; j++) current += solved.Admittance[driven, j] * volts[j];
            return new SurfaceMomSolution(f, current != Complex.Zero ? 1 / current : Complex.Zero, edges);
        }

        public override (FarFieldPattern Pattern, SurfaceMomSolution Sheet, double InputWatts, string Ledger) Radiate(
            double frequencyHz, Complex[] volts, Complex[] currents)
        {
            var sheet = Sheet(frequencyHz, volts);
            var pattern = SurfaceFarFieldEvaluator.Compute(Surface, sheet);
            double input = 0;
            for (int j = 0; j < volts.Length; j++) input += 0.5 * (volts[j] * Complex.Conjugate(currents[j])).Real;
            double heat = Solver.SheetImpedance is { } zs
                ? SheetLoss.OhmicPower(Surface, sheet.EdgeCurrents, zs(frequencyHz), Solver.SheetEdge, frequencyHz) : 0;
            string ledger = "";
            if (input > 0)
            {
                double efficiency = pattern.TotalRadiatedPowerWatts / input;
                ledger = $"P_rad = {efficiency:P1} of the power the ports deliver, gain "
                    + $"{10 * Math.Log10(Math.Max(efficiency * pattern.MaxDirectivity, 1e-300)):g3} dBi"
                    + (heat > 0 ? $"; conductor loss {heat / input:P1} (radiated + heat = {(pattern.TotalRadiatedPowerWatts + heat) / input:P1})" : "");
            }
            return (pattern, sheet, input, ledger);
        }

        public override (FieldMap? Map, SurfaceMomSolution? Sheet) NearField(double frequencyHz, Complex[] volts,
            IReadOnlyList<Vector3D> points, out string? refusal)
        {
            refusal = null;
            var sheet = Sheet(frequencyHz, volts);
            return (SurfaceFieldProbe.Evaluate(Surface, sheet, points), sheet);
        }
    }
}
