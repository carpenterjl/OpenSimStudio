using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The View Current Object window (OP-71). View glue only: it turns the view model's
/// mesh into one WPF model per face, each face its own colour, and draws the local axes.</summary>
public partial class NscObjectViewerWindow : Window
{
    private static readonly Color[] FaceColors =
    {
        Color.FromRgb(0x8C, 0xB4, 0xD8), Color.FromRgb(0xE0, 0xA4, 0x58), Color.FromRgb(0x7C, 0xC0, 0x8C),
        Color.FromRgb(0xC8, 0x8C, 0xC8), Color.FromRgb(0xD8, 0xD0, 0x70), Color.FromRgb(0x90, 0x90, 0x90),
    };

    private readonly OpticsNscObjectViewModel _view;

    public NscObjectViewerWindow(OpticsNscObjectViewModel view)
    {
        InitializeComponent();
        _view = view;
        DataContext = view;
        view.PropertyChanged += OnViewChanged;
        Closed += (_, _) => view.PropertyChanged -= OnViewChanged;
        Rebuild();
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var mesh = _view.Mesh;
        double opacity = Math.Clamp(_view.OpacityPercent / 100.0, 0.05, 1);
        var group = new Model3DGroup();
        var byFace = new Dictionary<int, MeshGeometry3D>();
        var maps = new Dictionary<int, Dictionary<int, int>>();
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            int face = mesh.TriangleFaceIds[t];
            if (!byFace.TryGetValue(face, out var geometry))
            {
                byFace[face] = geometry = new MeshGeometry3D();
                maps[face] = new Dictionary<int, int>();
            }
            var map = maps[face];
            var tri = mesh.Triangles[t];
            foreach (int v in new[] { tri.A, tri.B, tri.C })
            {
                if (!map.TryGetValue(v, out int i))
                {
                    var p = mesh.Vertices[v];
                    map[v] = i = geometry.Positions.Count;
                    geometry.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                }
                geometry.TriangleIndices.Add(i);
            }
        }
        foreach (var (face, geometry) in byFace.OrderBy(f => f.Key))
        {
            var color = FaceColors[((face % FaceColors.Length) + FaceColors.Length) % FaceColors.Length];
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            geometry.Freeze();
            group.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
        }
        ObjectVisual.Content = group;

        var b = mesh.Bounds;
        double size = Math.Max(Math.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y), b.Max.Z - b.Min.Z);
        if (!(size > 0)) size = 1;
        double half = 0.5 * size;
        AxisX.Points = _view.ShowLocalAxes ? new Point3DCollection { new(0, 0, 0), new(half, 0, 0) } : new Point3DCollection();
        AxisY.Points = _view.ShowLocalAxes ? new Point3DCollection { new(0, 0, 0), new(0, half, 0) } : new Point3DCollection();
        AxisZ.Points = _view.ShowLocalAxes ? new Point3DCollection { new(0, 0, 0), new(0, 0, size) } : new Point3DCollection();
    }
}
