using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Cfd;
using OpenSim.Core.PostProcessing;
using Vector3D = OpenSim.Core.Numerics.Vector3D;   // not the WPF Media3D one

namespace OpenSim.App.ViewModels;

/// <summary>
/// The flow visualizations of a conjugate (CFD) solve: velocity arrows, streamlines
/// seeded upstream, and a translucent speed slice — composed into ONE
/// <see cref="OverlayModel"/> group the viewport shows beside the solid results.
/// The flow field itself lives on <see cref="ProjectSession.FlowSolution"/>
/// (session-transient, like every result); this viewmodel only draws it.
/// </summary>
public partial class FlowVisualizationViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;

    public FlowVisualizationViewModel(ProjectSession session, ILogService log)
    {
        _session = session;
        _log = log;
        _session.FlowResultChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFlowResult));
            OnPropertyChanged(nameof(FlowSummary));
            Rebuild();
        };
    }

    /// <summary>Whether a conjugate solve has produced a flow field to draw.</summary>
    public bool HasFlowResult => _session.FlowSolution is not null;

    /// <summary>One line about the flow the panel shows above the toggles.</summary>
    public string FlowSummary
    {
        get
        {
            var flow = _session.FlowSolution;
            if (flow is null) return "Run the CFD analysis to see the airflow.";
            var g = flow.Grid;
            return $"Flow: {g.Nx}×{g.Ny}×{g.Nz} cells, peak {flow.MaxSpeed():G3} m/s, " +
                   $"{flow.Steps} steps.";
        }
    }

    /// <summary>What the slice paints. Speed is the flow-field view; TEMPERATURE is the
    /// heat-exchanger view — the fluid contour a conjugate study is actually about, and
    /// the one every reference report of this kind publishes.</summary>
    [ObservableProperty] private int _sliceQuantity;

    /// <summary>Fixed colour range for the slice. Off = the slice auto-scales to what it
    /// contains; on, the two bounds below are used verbatim, which is what makes two
    /// runs (or a run and a published figure) comparable at a glance.</summary>
    [ObservableProperty] private bool _sliceFixedRange;

    [ObservableProperty] private double _sliceMin;
    [ObservableProperty] private double _sliceMax = 1;

    /// <summary>The range and unit the slice is currently painting — the legend text.</summary>
    [ObservableProperty] private string _sliceLegend = "";

    [ObservableProperty] private bool _showArrows = true;
    [ObservableProperty] private bool _showStreamlines = true;
    [ObservableProperty] private bool _showSlice;

    /// <summary>Slice normal: 0 = X, 1 = Y, 2 = Z. Default through the flow (Z-normal
    /// shows a horizontal cut; Y is usually the informative side view).</summary>
    [ObservableProperty] private int _sliceAxis = 1;

    /// <summary>Slice position as a fraction of the domain along the slice axis.</summary>
    [ObservableProperty] private double _sliceFraction = 0.5;

    /// <summary>The composed flow overlay for the viewport; null when hidden/no result.</summary>
    [ObservableProperty] private Model3DGroup? _overlayModel;

    partial void OnSliceQuantityChanged(int value) => Rebuild();
    partial void OnSliceFixedRangeChanged(bool value) => Rebuild();
    partial void OnSliceMinChanged(double value) { if (SliceFixedRange) Rebuild(); }
    partial void OnSliceMaxChanged(double value) { if (SliceFixedRange) Rebuild(); }
    partial void OnShowArrowsChanged(bool value) => Rebuild();
    partial void OnShowStreamlinesChanged(bool value) => Rebuild();
    partial void OnShowSliceChanged(bool value) => Rebuild();
    partial void OnSliceAxisChanged(int value) => Rebuild();
    partial void OnSliceFractionChanged(double value) => Rebuild();

    [RelayCommand]
    private void Refresh() => Rebuild();

    private void Rebuild()
    {
        var flow = _session.FlowSolution;
        var domain = _session.FlowDomain;
        if (flow is null || domain is null)
        {
            OverlayModel = null;
            return;
        }

        var grid = flow.Grid;
        var group = new Model3DGroup();

        if (ShowArrows)
        {
            // A decimated lattice of cell-center velocities: ~16 samples per axis keeps
            // the arrows legible at any grid size.
            int strideX = Math.Max(1, grid.Nx / 16);
            int strideY = Math.Max(1, grid.Ny / 16);
            int strideZ = Math.Max(1, grid.Nz / 16);
            var points = new List<Vector3D>();
            var vectors = new List<Vector3D>();
            for (int k = 0; k < grid.Nz; k += strideZ)
                for (int j = 0; j < grid.Ny; j += strideY)
                    for (int i = 0; i < grid.Nx; i += strideX)
                    {
                        if (!grid.IsFluid(i, j, k)) continue;
                        points.Add(grid.CellCenter(i, j, k));
                        vectors.Add(flow.CellVelocity(i, j, k));
                    }
            group.Children.Add(SceneBuilder.BuildFlowArrowsModel(points, vectors,
                ColormapKind.Rainbow, arrowLength: 2.2 * grid.H * Math.Max(strideX, 1)));
        }

        if (ShowStreamlines)
            group.Children.Add(BuildStreamlines(flow, domain));

        if (ShowSlice && BuildSlice(flow) is { } sliceModel)
            group.Children.Add(sliceModel);

        OverlayModel = group.Children.Count > 0 ? group : null;
    }

    private static GeometryModel3D BuildStreamlines(FlowSolution flow, VoxelizedDomain domain)
    {
        var grid = flow.Grid;
        // Trilinear-free, honest v1 sampler: nearest-cell velocity; solids and the
        // outside world return null so traces stop at bodies and domain walls.
        Vector3D? Sample(Vector3D p)
        {
            int i = (int)Math.Floor((p.X - grid.Origin.X) / grid.H);
            int j = (int)Math.Floor((p.Y - grid.Origin.Y) / grid.H);
            int k = (int)Math.Floor((p.Z - grid.Origin.Z) / grid.H);
            if (i < 0 || i >= grid.Nx || j < 0 || j >= grid.Ny || k < 0 || k >= grid.Nz)
                return null;
            if (!grid.IsFluid(i, j, k)) return null;
            return flow.CellVelocity(i, j, k);
        }

        // Seeds on an upstream-biased lattice over the whole domain: streams that start
        // everywhere show recirculation, not just the inlet jet.
        var lines = new List<IReadOnlyList<Vector3D>>();
        int seedsPerAxis = 6;
        for (int a = 0; a < seedsPerAxis; a++)
            for (int b = 0; b < seedsPerAxis; b++)
            {
                var seed = new Vector3D(
                    grid.Origin.X + 0.05 * grid.Nx * grid.H,
                    grid.Origin.Y + (a + 0.5) / seedsPerAxis * grid.Ny * grid.H,
                    grid.Origin.Z + (b + 0.5) / seedsPerAxis * grid.Nz * grid.H);
                var line = StreamlineTracer.Trace(seed, Sample,
                    stepLength: grid.H * 0.5,
                    maxSteps: 4 * (grid.Nx + grid.Ny + grid.Nz));
                if (line.Count > 2) lines.Add(line);
            }
        return SceneBuilder.BuildStreamlinesModel(lines, thickness: 0.15 * grid.H,
            Color.FromRgb(230, 240, 255));
    }

    /// <summary>Quantity names and units, indexed by <see cref="SliceQuantity"/>.</summary>
    private static readonly (string Name, string Unit)[] SliceQuantities =
    {
        ("Speed", "m/s"), ("Temperature", "K"), ("Pressure", "Pa")
    };

    private GeometryModel3D? BuildSlice(FlowSolution flow)
    {
        var grid = flow.Grid;
        int axis = Math.Clamp(SliceAxis, 0, 2);
        int nAxis = axis == 0 ? grid.Nx : axis == 1 ? grid.Ny : grid.Nz;
        int slice = Math.Clamp((int)(SliceFraction * nAxis), 0, nAxis - 1);

        int quantity = Math.Clamp(SliceQuantity, 0, SliceQuantities.Length - 1);
        if (quantity == 1 && flow.Temperature is null)
        {
            _log.Append("The flow carries no temperature field (the solve ran without an " +
                        "energy equation), so there is no temperature slice to draw.");
            SliceLegend = "";
            return null;
        }

        var points = new List<Vector3D>();
        var speeds = new List<double>();
        int n1, n2;
        if (axis == 0)
        {
            n1 = grid.Ny; n2 = grid.Nz;
            for (int k = 0; k < grid.Nz; k++)
                for (int j = 0; j < grid.Ny; j++)
                    AddSample(slice, j, k);
        }
        else if (axis == 1)
        {
            n1 = grid.Nx; n2 = grid.Nz;
            for (int k = 0; k < grid.Nz; k++)
                for (int i = 0; i < grid.Nx; i++)
                    AddSample(i, slice, k);
        }
        else
        {
            n1 = grid.Nx; n2 = grid.Ny;
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                    AddSample(i, j, slice);
        }

        double lo, hi;
        if (SliceFixedRange && SliceMax > SliceMin)
        {
            lo = SliceMin; hi = SliceMax;
        }
        else
        {
            // Speed always starts at zero (that is a physical floor and keeps the still
            // fluid black); the others take the data range they actually span.
            lo = quantity == 0 ? 0 : speeds.Count > 0 ? speeds.Min() : 0;
            hi = speeds.Count > 0 ? speeds.Max() : 1;
            if (hi - lo < 1e-30) hi = lo + 1e-30;
        }
        var (name, unit) = SliceQuantities[quantity];
        SliceLegend = $"{name}: {lo:G5} – {hi:G5} {unit}" +
                      (quantity == 1
                          ? "  (solid cells carry the ambient reference — the metal’s own " +
                            "field is the FE result)"
                          : "");
        return SceneBuilder.BuildFlowSliceModel(points, speeds, n1, n2,
            ColormapKind.Rainbow, lo, hi, opacity: quantity == 1 ? 0.95 : 0.55);

        void AddSample(int i, int j, int k)
        {
            points.Add(grid.CellCenter(i, j, k));
            bool fluid = grid.IsFluid(i, j, k);
            speeds.Add(quantity switch
            {
                // Solid cells show zero speed — they read as the dark silhouette of the body.
                0 => fluid ? flow.CellVelocity(i, j, k).Length : 0,
                // The temperature array already carries the ambient in solid cells; that
                // is stated on FlowSolution and repeated in the legend rather than faked.
                1 => flow.Temperature![grid.CellIndex(i, j, k)],
                _ => fluid ? flow.Pressure[grid.CellIndex(i, j, k)] : 0
            });
        }
    }
}
