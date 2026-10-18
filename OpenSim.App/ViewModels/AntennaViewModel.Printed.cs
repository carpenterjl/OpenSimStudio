using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>
/// A printed antenna with its finite ground (Feature 12's air model, FU-36's board slab; FU-47): the
/// antenna net's copper and the ground net's pour on the same layer, joined at the feed by a short
/// bridge with a delta gap across it — the coaxial or microstrip feed's two terminals. Solved in air,
/// or on the board as an infinite dielectric slab with no ground plane (the pour is the ground), which
/// brings the resonance down where FR-4 puts it. The pour can be clipped to bound the unknowns.
/// </summary>
public partial class AntennaViewModel
{
    /// <summary>The coplanar ground pour's net.</summary>
    [ObservableProperty] private CopperNet? _groundPourNet;
    /// <summary>Copper layers the antenna net has copper on.</summary>
    public ObservableCollection<int> PrintedLayers { get; } = new();
    [ObservableProperty] private int _printedLayer;
    [ObservableProperty] private double _bridgeWidthMm = 1;
    /// <summary>The feed point [mm, board coordinates] when no source pad is picked: the bridge leaves
    /// the antenna copper at its point nearest this one.</summary>
    [ObservableProperty] private double _feedXMm;
    [ObservableProperty] private double _feedYMm;
    /// <summary>Keep only the pour within this margin of the antenna copper's bounds [mm]; 0 = the whole pour.</summary>
    [ObservableProperty] private double _groundClipMm;
    /// <summary>On the board's dielectric (an infinite ungrounded slab) instead of in air.</summary>
    [ObservableProperty] private bool _printedOnLaminate = true;

    private void RefreshPrintedLayers(CopperNet? net)
    {
        PrintedLayers.Clear();
        if (net is null) return;
        foreach (int layer in net.Layers) PrintedLayers.Add(layer);
        PrintedLayer = PrintedLayers.FirstOrDefault();
    }

    private bool TryBuildPrintedModel(out PortedModel model, out IReadOnlyList<PortClosure> closures, out string? failure,
        double refine)
    {
        model = null!;
        closures = new[] { new PortClosure("feed", PortLoads.Port, 0, 0) };
        failure = null;
        if (_board is not { } board || AntennaNet is not { } antenna)
        { failure = "import a board and pick the antenna's net first"; return false; }
        if (GroundPourNet is not { } ground) { failure = "pick the ground pour's net"; return false; }
        if (!PrintedLayers.Contains(PrintedLayer)) { failure = "pick the layer the antenna is printed on"; return false; }
        if (!(BridgeWidthMm > 0)) { failure = "the feed bridge needs a positive width"; return false; }

        Point2 feed = _electrodes.SelectedSource is { } pad ? pad.Center : new Point2(FeedXMm * 1e-3, FeedYMm * 1e-3);
        int layerCount = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        var stackup = _options?.Invoke().Stackup ?? BoardStackup.FromBoard(board);
        BoardSlab? slab = null;
        string medium = "in air";
        var assumptions = PrintedAntennaBuilder.Assumptions.ToList();
        if (PrintedOnLaminate)
        {
            if (PrintedLayer != 1 && PrintedLayer != layerCount)
            {
                failure = $"an antenna on the inner layer L{PrintedLayer} is buried in the board — the slab model has its copper on a "
                    + "face; solve it in air, or use an outer layer";
                return false;
            }
            // The board's dielectric as one slab: thickness the sum of the gaps, εr and tanδ their
            // thickness-weighted means.
            double h = 0, epsH = 0, tanH = 0;
            for (int gap = 1; gap < Math.Max(layerCount, 2); gap++)
            {
                double t = stackup.GapThicknessOf(gap);
                h += t;
                epsH += t * stackup.PermittivityOf(gap);
                tanH += t * Math.Max(stackup.LossTangentOf(gap), 0);
            }
            slab = new BoardSlab(epsH / h, tanH / h, h, CopperOnTop: PrintedLayer == 1);
            medium = $"on the board's laminate (εr {slab.RelativePermittivity:g3}, {h * 1e3:g3} mm)";
            assumptions.Add($"The board is one slab of {h * 1e3:g4} mm, εr {slab.RelativePermittivity:g3}, tanδ {slab.LossTangent:g3} "
                + "(the gaps' thickness-weighted means), copper on its " + (slab.CopperOnTop ? "top" : "bottom") + " face.");
        }
        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        double effective = slab is null ? 1 : 0.5 * (slab.RelativePermittivity + 1);
        double element = 299_792_458.0 / maxFrequency / Math.Sqrt(effective) / (Math.Max(MeshCellsPerWavelength, 4) * refine);
        Polygon2? clip = null;
        if (GroundClipMm > 0)
        {
            var copper = antenna.Islands.Where(i => i.LayerOrder == PrintedLayer).ToList();
            double minX = copper.Min(i => i.Bounds().MinX), minY = copper.Min(i => i.Bounds().MinY);
            double maxX = copper.Max(i => i.Bounds().MaxX), maxY = copper.Max(i => i.Bounds().MaxY);
            double m = GroundClipMm * 1e-3;
            clip = new Polygon2(new[]
            {
                new Point2(minX - m, minY - m), new Point2(maxX + m, minY - m), new Point2(maxX + m, maxY + m), new Point2(minX - m, maxY + m)
            });
        }
        PrintedAntennaModel printed;
        try
        {
            printed = PrintedAntennaBuilder.FromNets(PrintedLayer, antenna, ground, feed, BridgeWidthMm * 1e-3, element, clip, maxUnknowns: 4000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            failure = ex.Message;
            return false;
        }
        var solver = new SurfaceMomSolver { SheetImpedance = SheetModel(overGround: false), SheetEdge = SheetEdgeModel() };
        model = new PrintedModel
        {
            Surface = printed.Structure,
            PortNames = new[] { "feed" },
            Description = $"{printed.Structure.BasisCount} RWG unknowns, antenna and pour on L{PrintedLayer} {medium}",
            Assumptions = assumptions,
            Notes = printed.Notes,
            Solver = solver,
            Printed = printed,
            Slab = slab
        };
        return true;
    }

    private sealed class PrintedModel : PortedModel
    {
        public required SurfaceMomSolver Solver { get; init; }
        public required PrintedAntennaModel Printed { get; init; }
        public BoardSlab? Slab { get; init; }

        private SurfaceMomSolution SolveAt(double f, out MultiLayerKernelTable? table)
        {
            if (Slab is { } slab)
            {
                var (solution, t) = PrintedAntennaBuilder.SolveOnBoard(Printed, slab, f, Solver);
                table = t;
                return solution;
            }
            table = null;
            return Solver.Solve(Surface, f, Printed.Port);
        }

        public override Complex[,] Impedance(double frequencyHz) => new[,] { { SolveAt(frequencyHz, out _).InputImpedance } };

        private static SurfaceMomSolution Scaled(SurfaceMomSolution solution, Complex volts) =>
            solution with { EdgeCurrents = solution.EdgeCurrents.Select(e => e * volts).ToArray() };

        public override (FarFieldPattern Pattern, SurfaceMomSolution Sheet, double InputWatts, string Ledger) Radiate(
            double frequencyHz, Complex[] volts, Complex[] currents)
        {
            var sheet = Scaled(SolveAt(frequencyHz, out var table), volts[0]);
            var pattern = table is null ? SurfaceFarFieldEvaluator.Compute(Surface, sheet) : SlabFarField.Compute(Surface, table, sheet);
            double input = 0.5 * (volts[0] * Complex.Conjugate(currents[0])).Real;
            double heat = Solver.SheetImpedance is { } zs
                ? SheetLoss.OhmicPower(Surface, sheet.EdgeCurrents, zs(frequencyHz), Solver.SheetEdge, frequencyHz) : 0;
            string ledger;
            if (!(input > 0)) ledger = "";
            else if (table is not null)
                ledger = PowerLedger.Describe(input, pattern.TotalRadiatedPowerWatts,
                    SlabFarField.SurfaceWavePowerWatts(Surface, table, sheet), Slab!.LossTangent, pattern.MaxDirectivity, heat);
            else
            {
                double efficiency = pattern.TotalRadiatedPowerWatts / input;
                ledger = $"radiation efficiency {efficiency:P1}, gain {10 * Math.Log10(Math.Max(efficiency * pattern.MaxDirectivity, 1e-300)):g3} dBi"
                    + (heat > 0 ? $"; conductor loss {heat / input:P1} of the input (radiated + heat = {(pattern.TotalRadiatedPowerWatts + heat) / input:P1})" : "");
            }
            return (pattern, sheet, input, ledger);
        }

        public override (FieldMap? Map, SurfaceMomSolution? Sheet) NearField(double frequencyHz, Complex[] volts,
            IReadOnlyList<Vector3D> points, out string? refusal)
        {
            if (Slab is not null)
            {
                refusal = "the near field over the board's slab is not available for a printed antenna (its slab field kernels "
                    + "with no ground are not written); untick 'On the board's laminate' for the field in air, or use the far field.";
                return (null, null);
            }
            refusal = null;
            var sheet = Scaled(SolveAt(frequencyHz, out _), volts[0]);
            return (SurfaceFieldProbe.Evaluate(Surface, sheet, points), sheet);
        }
    }
}
