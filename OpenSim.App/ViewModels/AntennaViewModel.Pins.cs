using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Geometry2D;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>One vertical pin of a pin-array antenna: where it stands, its radius, whether it is a feed
/// (a port at its base) or a shorting pin welded to the ground, and what closes a feed's port.</summary>
public partial class AntennaPinRow : ObservableObject
{
    public const string Feed = "Feed";
    public const string Short = "Short";
    public static IReadOnlyList<string> Roles { get; } = new[] { Feed, Short };
    public static IReadOnlyList<string> Loads => PortLoads.All;

    [ObservableProperty] private string _name = "feed";
    [ObservableProperty] private double _xMm;
    [ObservableProperty] private double _yMm;
    [ObservableProperty] private double _radiusMm = 0.2;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFeed))]
    private string? _role = Feed;
    /// <summary>A feed's closure: left a port, shorted, open, or a lumped part.</summary>
    [ObservableProperty] private string? _load = PortLoads.Port;
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _q;

    public bool IsFeed => Role != Short;
}

/// <summary>
/// Pins (Feature 12; FU-47): vertical currents from the ground plane up to the sheet — a probe feed,
/// a shorting pin, the arms of an inverted-F or a PIFA, a patch with two feeds — solved together by
/// <see cref="SurfaceMomSolver.SolvePins(SurfaceStructure, LayeredKernelTable, IReadOnlyList{VerticalPin}, IReadOnlyList{NamedSurfacePort})"/>.
/// On the wizard patch the pins stand on a grounded slab and their (x, y) are from the patch centre;
/// on a board island they stand on the plane under it, through the board's stackup, at board
/// coordinates, and the island's own vias can be taken as pins. The mesh puts a vertex exactly on
/// every pin.
/// </summary>
public partial class AntennaViewModel
{
    public ObservableCollection<AntennaPinRow> Pins { get; } = CreateDefaultPins();

    private static ObservableCollection<AntennaPinRow> CreateDefaultPins() => new()
    {
        // An inverted-F's two arms on the default patch: a feed and a shorting pin near one edge.
        new AntennaPinRow { Name = "feed", XMm = -60, YMm = -200, Role = AntennaPinRow.Feed },
        new AntennaPinRow { Name = "short", XMm = 60, YMm = -200, Role = AntennaPinRow.Short }
    };

    private bool _pinRowsTracked;

    /// <summary>Hooks the rows so the far-field port list follows their names, roles and loads.</summary>
    private void TrackPinRows()
    {
        if (_pinRowsTracked) return;
        _pinRowsTracked = true;
        foreach (var row in Pins) row.PropertyChanged += OnPortRowChanged;
        Pins.CollectionChanged += (_, e) =>
        {
            foreach (AntennaPinRow row in e.NewItems ?? Array.Empty<AntennaPinRow>()) row.PropertyChanged += OnPortRowChanged;
            OnPortRowChanged(null, new System.ComponentModel.PropertyChangedEventArgs(nameof(Pins)));
        };
    }

    [RelayCommand]
    private void AddPin()
    {
        TrackPinRows();
        var last = Pins.LastOrDefault();
        int n = Pins.Count + 1;
        Pins.Add(new AntennaPinRow
        {
            Name = $"pin{n}", XMm = (last?.XMm ?? 0) + 5, YMm = last?.YMm ?? 0, RadiusMm = last?.RadiusMm ?? ProbeRadiusMm,
            Role = AntennaPinRow.Feed
        });
    }

    [RelayCommand]
    private void RemovePin(AntennaPinRow? row)
    {
        TrackPinRows();
        if (row is not null) Pins.Remove(row);
    }

    /// <summary>The board island's own vias as pins: a via the net uses to join the island to the
    /// ground layer is a shorting pin, one rising through the plane's antipad a feed.</summary>
    [RelayCommand]
    private void PinsFromVias()
    {
        TrackPinRows();
        if (_board is null || AntennaNet is not { } net || AntennaIsland is not { } choice)
        {
            AntennaResult = "Import a board and pick a net and its island first.";
            return;
        }
        var stackup = _options?.Invoke().Stackup ?? OpenSim.Pcb.Import.BoardStackup.FromBoard(_board);
        if (!BoardAntennaStackup.TryResolve(_board, choice.Island.LayerOrder, stackup, choice.Island.Shape,
                out var resolved, out var failure))
        {
            AntennaResult = $"No pins: {failure}";
            return;
        }
        var candidates = BoardPins.OnIsland(_board, net, choice.Island, resolved!.GroundLayer);
        if (candidates.Count == 0)
        {
            AntennaResult = $"No plated via on this island reaches the ground on L{resolved.GroundLayer}; enter the pins by hand.";
            return;
        }
        Pins.Clear();
        int feeds = 0, shorts = 0;
        foreach (var c in candidates)
            Pins.Add(new AntennaPinRow
            {
                Name = c.ShortsToGround ? $"short{++shorts}" : $"feed{++feeds}",
                XMm = c.Position.X * 1e3, YMm = c.Position.Y * 1e3, RadiusMm = c.RadiusMeters * 1e3,
                Role = c.ShortsToGround ? AntennaPinRow.Short : AntennaPinRow.Feed
            });
        AntennaResult = $"{candidates.Count} via(s) on the island reach the ground on L{resolved.GroundLayer}: "
            + $"{shorts} shorting, {feeds} feed(s) — edit the roles and loads as the design has them.";
        foreach (var c in candidates) _log.Append("Antenna pins: " + c.Description);
    }

    /// <summary>The feed pins' closures, in pin order (shorting pins are welded, not ports).</summary>
    private IReadOnlyList<PortClosure> PinClosures()
    {
        TrackPinRows();
        return Pins.Where(p => p.IsFeed)
            .Select(p => new PortClosure(p.Name.Trim(), p.Load ?? PortLoads.Port, p.Value, p.Q)).ToList();
    }

    private bool TryBuildPinModel(out PortedModel model, out IReadOnlyList<PortClosure> closures, out string? failure,
        double refine)
    {
        model = null!;
        closures = PinClosures();
        failure = null;
        if (Pins.Count == 0) { failure = "add at least one pin"; return false; }
        if (closures.Count == 0) { failure = "at least one pin must be a feed"; return false; }
        if (ProbeSegments < 2) { failure = "each pin needs at least 2 tube segments"; return false; }
        foreach (var p in Pins)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) { failure = "every pin needs a name"; return false; }
            if (!(p.RadiusMm > 0)) { failure = $"pin '{p.Name}' needs a positive radius"; return false; }
        }
        var allNames = Pins.Select(p => p.Name.Trim()).ToList();
        if (allNames.Distinct(StringComparer.Ordinal).Count() != allNames.Count) { failure = "pin names must be distinct"; return false; }
        if (GroundLoss)
        {
            failure = "ground-plane loss is not available with pins (their vertical kernels are written for a perfect ground)";
            return false;
        }

        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        double cells = Math.Max(MeshCellsPerWavelength, 4) * refine;
        var points = Pins.Select(p => (X: p.XMm * 1e-3, Y: p.YMm * 1e-3)).ToList();
        var pins = Pins.Select(p => new VerticalPin(p.Name.Trim(),
            new ProbeFeed(p.XMm * 1e-3, p.YMm * 1e-3, p.RadiusMm * 1e-3, ProbeSegments), IsPort: p.IsFeed)).ToList();

        SurfaceStructure surface;
        SubstrateStackup? slab = null;
        LayeredSpec? layered = null;
        var notes = new List<string>();
        var assumptions = SurfaceMomSolver.PinArrayAssumptions.ToList();
        string medium;
        if (SourceMode == PinnedPatchMode)
        {
            if (PlateWidthMm <= 0 || PlateLengthMm <= 0 || PatchHeightMm <= 0)
            { failure = "the patch needs positive width, length and slab thickness (patch height)"; return false; }
            if (SubstrateEpsR < 1) { failure = "the substrate εr must be ≥ 1 (εr = 1 is an air gap)"; return false; }
            double lambdaMin = 299_792_458.0 / maxFrequency / Math.Sqrt(SubstrateEpsR);
            double element = Math.Min(lambdaMin / cells, Math.Max(PlateWidthMm, PlateLengthMm) * 1e-3 / (10 * refine));
            double h = PatchHeightMm * 1e-3;
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, element,
                z: GroundZMm * 1e-3 + h, portFraction: 0.5, maxUnknowns: 4000, snapVertices: points);
            if (grid.Structure is null) { failure = grid.FailureReason; return false; }
            surface = grid.Structure;
            notes.AddRange(grid.Warnings);
            slab = new SubstrateStackup(SubstrateEpsR, Math.Max(SubstrateTanD, 0), h);
            medium = $"εr {SubstrateEpsR:g3} slab {PatchHeightMm:g3} mm";
            assumptions.Insert(1, $"Pins on a {PlateWidthMm:g4} × {PlateLengthMm:g4} mm patch over a grounded slab "
                + $"(εr = {SubstrateEpsR:g3}, tanδ = {Math.Max(SubstrateTanD, 0):g3}, {PatchHeightMm:g4} mm); pin positions are "
                + "from the patch centre.");
        }
        else
        {
            if (_board is not { } board || AntennaIsland is not { } choice)
            { failure = "import a board and pick a net and its island first"; return false; }
            var shape = choice.Island.Shape;
            var stackup = _options?.Invoke().Stackup ?? OpenSim.Pcb.Import.BoardStackup.FromBoard(board);
            if (!BoardAntennaStackup.TryResolve(board, choice.Island.LayerOrder, stackup, shape, out var resolved, out var why))
            { failure = why; return false; }
            double epsMax = resolved!.Stackup.Layers.Max(l => l.RelativePermittivity);
            double lambdaMin = 299_792_458.0 / maxFrequency / Math.Sqrt(epsMax);
            double minX = shape.Outer.Min(p => p.X), maxX = shape.Outer.Max(p => p.X);
            double minY = shape.Outer.Min(p => p.Y), maxY = shape.Outer.Max(p => p.Y);
            double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            double element = Math.Min(lambdaMin / cells, diagonal / (8 * refine));
            var grid = SurfaceMeshBuilder.BuildFromPolygon(shape, element, z: 0, maxUnknowns: 4000,
                fixedPoints: points.Select(p => new Point2(p.X, p.Y)).ToList());
            if (grid.Structure is null) { failure = grid.FailureReason; return false; }
            surface = grid.Structure;
            // The polygon mesher's own port cut is not used: the pins are the ports.
            notes.AddRange(grid.Warnings.Where(w => !w.StartsWith("Port:", StringComparison.Ordinal)));
            if (resolved.IsSingleSlabTop)
            {
                var layer = resolved.Stackup.Layers[0];
                slab = new SubstrateStackup(layer.RelativePermittivity, layer.LossTangent, layer.ThicknessMeters);
            }
            else layered = new LayeredSpec(resolved.Stackup, resolved.SourceInterface);
            medium = resolved.IsSingleSlabTop ? $"board slab to L{resolved.GroundLayer}" : $"{resolved.Stackup.Layers.Count}-gap board stackup to L{resolved.GroundLayer}";
            assumptions.InsertRange(1, resolved.Assumptions);
            assumptions.Insert(1, $"Pins stand on the ground layer L{resolved.GroundLayer} under the island; their positions are board coordinates.");
        }

        var (_, diag) = SurfaceBounds(surface);
        var solver = new SurfaceMomSolver
        {
            SheetImpedance = SheetModel(overGround: true), SheetEdge = SheetEdgeModel(), WireSurfaceImpedance = WireLossModel()
        };
        if (solver.SheetImpedance is not null)
            assumptions.Add("Metal loss: the sheet and every pin's wall carry their surface impedance in the solve; the power "
                + "ledger's heat counts the sheet only, so the pins' wall loss sits in its remainder.");
        model = new PinModel
        {
            Surface = surface,
            PortNames = closures.Select(c => c.Name).ToList(),
            Description = $"{surface.BasisCount} RWG unknowns, {pins.Count} pin(s) ({pins.Count(p => p.IsPort)} port(s)), {medium}",
            Assumptions = assumptions,
            Notes = notes,
            GroundZ = surface.Vertices[0].Z - (slab?.ThicknessMeters ?? layered?.Stackup.Layers.Take((layered?.SourceInterface ?? 0) + 1).Sum(l => l.ThicknessMeters) ?? 0),
            Solver = solver,
            Pins = pins,
            Slab = slab,
            Layered = layered,
            RhoMax = 1.2 * diag
        };
        return true;
    }

    /// <summary>A pin array over a grounded slab or a board stackup.</summary>
    private sealed class PinModel : PortedModel
    {
        public required SurfaceMomSolver Solver { get; init; }
        public required IReadOnlyList<VerticalPin> Pins { get; init; }
        public SubstrateStackup? Slab { get; init; }
        public LayeredSpec? Layered { get; init; }
        public required double RhoMax { get; init; }

        private (PinArraySolution Solution, LayeredKernelTable? Slab, MultiLayerKernelTable? Multi) Solve(double f)
        {
            if (Layered is { } spec)
            {
                var multi = new MultiLayerKernelTable(spec.Stackup, f, RhoMax, sourceInterface: spec.SourceInterface);
                return (Solver.SolvePins(Surface, multi, Pins), null, multi);
            }
            var table = new LayeredKernelTable(Slab!, f, RhoMax);
            return (Solver.SolvePins(Surface, table, Pins), table, null);
        }

        public override Complex[,] Impedance(double frequencyHz) => Solve(frequencyHz).Solution.Impedance();

        public override (FarFieldPattern Pattern, SurfaceMomSolution Sheet, double InputWatts, string Ledger) Radiate(
            double frequencyHz, Complex[] volts, Complex[] currents)
        {
            var (solution, slab, multi) = Solve(frequencyHz);
            var c = solution.Currents(volts);
            var pattern = multi is not null ? LayeredFarField.Compute(Surface, multi, c, Pins) : LayeredFarField.Compute(Surface, slab!, c, Pins);
            double sw = multi is not null
                ? LayeredFarField.SurfaceWavePowerWatts(Surface, multi, c, Pins)
                : LayeredFarField.SurfaceWavePowerWatts(Surface, slab!, c, Pins);
            double heat = Solver.SheetImpedance is { } zs
                ? SheetLoss.OhmicPower(Surface, c.RawEdgeCurrents, zs(frequencyHz), Solver.SheetEdge, frequencyHz) : 0;
            double tanD = Layered is { } spec ? spec.Stackup.Layers.Max(l => l.LossTangent) : Slab!.LossTangent;
            var sheet = new SurfaceMomSolution(frequencyHz, volts[0] / (currents[0] == Complex.Zero ? Complex.One : currents[0]), c.RawEdgeCurrents);
            string ledger = c.InputPowerWatts > 0
                ? PowerLedger.Describe(c.InputPowerWatts, pattern.TotalRadiatedPowerWatts, sw, tanD, pattern.MaxDirectivity, heat)
                : "";
            return (pattern, sheet, c.InputPowerWatts, ledger);
        }

        public override (FieldMap? Map, SurfaceMomSolution? Sheet) NearField(double frequencyHz, Complex[] volts,
            IReadOnlyList<Vector3D> points, out string? refusal)
        {
            refusal = "the near field of a pin array is not available — each tube and junction would need its own layered "
                + "near-field transform, and the sheet's alone would leave out the pins' currents. The far field (with every pin) is.";
            return (null, null);
        }
    }
}
