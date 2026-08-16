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

        if (ShowSlice)
            group.Children.Add(BuildSlice(flow));

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

    private GeometryModel3D BuildSlice(FlowSolution flow)
    {
        var grid = flow.Grid;
        int axis = Math.Clamp(SliceAxis, 0, 2);
        int nAxis = axis == 0 ? grid.Nx : axis == 1 ? grid.Ny : grid.Nz;
        int slice = Math.Clamp((int)(SliceFraction * nAxis), 0, nAxis - 1);

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

        double max = speeds.Count > 0 ? speeds.Max() : 1;
        return SceneBuilder.BuildFlowSliceModel(points, speeds, n1, n2,
            ColormapKind.Rainbow, 0, Math.Max(max, 1e-30), opacity: 0.55);

        void AddSample(int i, int j, int k)
        {
            points.Add(grid.CellCenter(i, j, k));
            // Solid cells show zero speed — they read as the dark silhouette of the body.
            speeds.Add(grid.IsFluid(i, j, k) ? flow.CellVelocity(i, j, k).Length : 0);
        }
    }
}
