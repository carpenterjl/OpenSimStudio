using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>
/// The shared 3D viewport. Code-behind is visual behaviour only: hit testing,
/// zoom-to-fit, camera clip planes, context-menu suppression during right-drag rotate,
/// and pushing viewmodel point collections into the named LinesVisual3D children
/// (whose Points property is not cleanly bindable).
/// </summary>
public partial class Viewport3DView : UserControl
{
    private MainViewModel? _viewModel;

    public Viewport3DView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => ConfigureCamera();
    }

    /// <summary>One-time hookup once the inherited DataContext arrives. The view and
    /// the viewmodels are app-lifetime singletons, so no unsubscription is needed.</summary>
    private void Attach()
    {
        if (_viewModel is not null || DataContext is not MainViewModel viewModel) return;
        _viewModel = viewModel;

        viewModel.Session.GeometryReplaced += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Viewport.ZoomExtents(400);
            ConfigureCamera();
        });
        viewModel.Scene.PropertyChanged += OnScenePropertyChanged;
        viewModel.Results.PropertyChanged += OnResultsPropertyChanged;
        viewModel.Pcb.PropertyChanged += OnPcbPropertyChanged;
        viewModel.Antenna.PropertyChanged += OnAntennaPropertyChanged;
        UpdateBodyVisual();
    }

    /// <summary>The body content is code-behind-managed (see the XAML comment on
    /// BodyVisual): shown by reference, hidden by nulling the content.</summary>
    private void UpdateBodyVisual() =>
        BodyVisual.Content = _viewModel!.Scene.ShowBody ? _viewModel.Scene.SceneRoot : null;

    private void OnAntennaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AntennaViewModel.WirePoints):
                AntennaWireLines.Points = _viewModel!.Antenna.WirePoints;
                break;
            case nameof(AntennaViewModel.FieldOverlayModel):
                FieldOverlayLegendPanel.Visibility = _viewModel!.Antenna.FieldOverlayModel is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                break;
        }
    }

    private void OnResultsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.SelectedField))
            LegendPanel.Visibility = _viewModel!.Results.SelectedField is null
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void OnScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SceneViewModel.MeshEdges):
            case nameof(SceneViewModel.ShowMeshEdges):
                MeshEdgeLines.Points = _viewModel!.Scene.ShowMeshEdges
                    ? _viewModel.Scene.MeshEdges
                    : new Point3DCollection();
                break;
            case nameof(SceneViewModel.SceneRoot):
            case nameof(SceneViewModel.ShowBody):
                UpdateBodyVisual();
                break;
            case nameof(SceneViewModel.ContourPoints):
                ContourLineVisual.Points = _viewModel!.Scene.ContourPoints;
                break;
        }
    }

    private void OnPcbPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PcbViewModel.BoardOutlinePoints):
            case nameof(PcbViewModel.ShowBoardOutline):
                BoardOutlineLines.Points = _viewModel!.Pcb.ShowBoardOutline
                    ? _viewModel.Pcb.BoardOutlinePoints
                    : new Point3DCollection();
                break;
            case nameof(PcbViewModel.SelectedNetOutlines):
                SelectedNetLines.Points = _viewModel!.Pcb.SelectedNetOutlines;
                break;
        }
    }

    /// <summary>
    /// Small near-plane / large far-plane so zooming into the millimetre-scale, origin-
    /// offset PCB doesn't clip through the board. Re-applied after every view change
    /// because ZoomExtents can reset the camera.
    /// </summary>
    private void ConfigureCamera()
    {
        if (Viewport.Camera is ProjectionCamera cam)
        {
            cam.NearPlaneDistance = 1e-5;
            cam.FarPlaneDistance = 1e3;
        }
    }

    // ---------------- View context menu ----------------

    private void FitToView_Click(object sender, RoutedEventArgs e)
    {
        Viewport.ZoomExtents(400);
        ConfigureCamera();
    }

    private void ViewTop_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, 0, -1), new Vector3D(0, 1, 0));
    private void ViewBottom_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, 0, 1), new Vector3D(0, 1, 0));
    private void ViewFront_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));
    private void ViewBack_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(0, -1, 0), new Vector3D(0, 0, 1));
    private void ViewLeft_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(1, 0, 0), new Vector3D(0, 0, 1));
    private void ViewRight_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1));
    private void ViewIso_Click(object sender, RoutedEventArgs e) => SetView(new Vector3D(-1, -1, -1), new Vector3D(0, 0, 1));

    /// <summary>Points the camera along a standard axis and refits.</summary>
    private void SetView(Vector3D look, Vector3D up)
    {
        if (Viewport.Camera is ProjectionCamera cam)
        {
            look.Normalize();
            cam.LookDirection = look;
            cam.UpDirection = up;
            Viewport.ZoomExtents(400);
            ConfigureCamera();
        }
    }

    /// <summary>
    /// Left-click either picks a copper net (while a board preview is showing) or a
    /// geometric face (once a net/body is meshed). Net picking projects the click ray to
    /// the board plane and hit-tests the island polygons, so the thin outline lines don't
    /// need to be individually clickable.
    /// </summary>
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || e.ChangedButton != MouseButton.Left) return;

        if (_viewModel.Pcb.NetPickingActive && TryPickNet(e.GetPosition(Viewport)))
        {
            e.Handled = true;
            return;
        }

        var hits = Viewport.Viewport.FindHits(e.GetPosition(Viewport));
        foreach (var hit in hits)
        {
            if (hit.Model is GeometryModel3D model)
            {
                int? faceId = _viewModel.Scene.GetFaceIdForModel(model);
                if (faceId is not null)
                {
                    _viewModel.OnFaceClicked(faceId.Value);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    /// <summary>Hands the click ray to the view model, which intersects it with every
    /// layer's own z-plane — projecting to one fixed plane picks the wrong trace when
    /// the camera is angled (layer-height parallax).</summary>
    private bool TryPickNet(Point clickPoint)
    {
        var ray = Viewport3DHelper.Point2DtoRay3D(Viewport.Viewport, clickPoint);
        if (ray is null) return false;
        return _viewModel!.Pcb.PickNetAt(ray.Origin, ray.Direction);
    }

    // ---------------- Context-menu vs right-drag rotate ----------------

    private Point? _viewportRightDownPosition;

    private void Viewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Tunneling event: fires before Helix's CameraController captures the mouse.
        _viewportRightDownPosition = e.GetPosition(Viewport);
    }

    /// <summary>
    /// Helix rotates the camera on right-drag, and WPF opens the ContextMenu on right
    /// mouse-up regardless — so every rotate would end with the menu popping. Suppress
    /// the menu when the button travelled beyond the system drag threshold. A null
    /// down-position means keyboard invocation (menu key / Shift+F10): always allow it.
    /// </summary>
    private void Viewport_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_viewportRightDownPosition is not { } down) return;
        _viewportRightDownPosition = null;
        var moved = Mouse.GetPosition(Viewport) - down;
        double threshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance,
                                    SystemParameters.MinimumVerticalDragDistance);
        if (moved.Length > threshold)
            e.Handled = true;
    }
}
