using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using OpenSim.Core.Geometry2D;
using TriangleMesh = OpenSim.Core.Model.TriangleMesh;
using OpenSim.Optics.Layout;
using CoreVector = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.Rendering;

/// <summary>
/// The optical layout views (plan item OP-8): the YZ cross-section as a 2-D drawing, and the
/// 3-D wireframe and shaded model. The geometry (sag sampling, lathe, outlines, depth order)
/// is built in OpenSim.Optics.Layout; this part only turns it into WPF objects.
/// </summary>
public static partial class SceneBuilder
{
    /// <summary>
    /// The 3-D layout: lens bodies (transparent unless <see cref="OpticalSceneStyle.LensOpacity"/>
    /// is 1, and visible from both sides), mirrors, the aperture at the stop, the wireframe and
    /// the ray bundles as ribbon meshes (the frozen-mesh approach of
    /// <see cref="BuildStreamlinesModel"/>), one model per ray colour. Call
    /// <see cref="OpticalLayoutModel.SortForCamera"/> once the camera is known and on every
    /// camera move.
    /// </summary>
    public static OpticalLayoutModel BuildOpticalLayoutModel(OpticalLayout layout, OpticalSceneStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        style ??= OpticalSceneStyle.Default;
        var bodies = style.ShowShaded ? LayoutSolids.Build(layout, style.Resolution) : Array.Empty<LayoutBody>();
        var wires = style.ShowWireframe ? LayoutWireframe.Build(layout, style.Resolution) : Array.Empty<IReadOnlyList<CoreVector>>();
        double diagonal = LayoutDiagonal(layout, bodies, wires);

        var opaque = new Model3DGroup();
        var transparent = new List<GeometryModel3D>();
        var centers = new List<CoreVector>();
        bool lensesTransparent = style.LensOpacity < 1;

        if (style.ShowRays)
        {
            double width = Math.Max(style.RayWidthFraction * diagonal, 1e-12);
            foreach (var group in layout.Rays
                         .Where(r => r.Points.Count >= 2 && !(style.DeleteVignetted && r.Status != OpenSim.Optics.RayStatus.Ok))
                         .GroupBy(r => CrossSection.ColorKey(r, style.Coloring))
                         .OrderBy(g => g.Key))
            {
                var polylines = group.Select(r => r.Points).ToList();
                opaque.Children.Add(BuildStreamlinesModel(polylines, width, style.RayColor(group.Key)));
            }
        }
        if (wires.Count > 0)
            opaque.Children.Add(BuildStreamlinesModel(wires, Math.Max(style.WireWidthFraction * diagonal, 1e-12), style.WireColor));

        var lensMaterial = BodyMaterial(style.LensColor, lensesTransparent ? style.LensOpacity : 1, specular: true);
        var mirrorMaterial = BodyMaterial(style.MirrorColor, 1, specular: true);
        var apertureMaterial = BodyMaterial(style.ApertureColor, 1, specular: false);
        foreach (var body in bodies)
        {
            var material = body.Kind switch
            {
                LayoutBodyKind.Lens => lensMaterial,
                LayoutBodyKind.Mirror => mirrorMaterial,
                _ => apertureMaterial,
            };
            var model = new GeometryModel3D(ToMeshGeometry(body.Mesh), material) { BackMaterial = material };
            model.Freeze();
            if (body.Kind == LayoutBodyKind.Lens && lensesTransparent)
            {
                transparent.Add(model);
                centers.Add(body.Center);
            }
            else
            {
                opaque.Children.Add(model);
            }
        }
        return new OpticalLayoutModel(opaque, transparent, centers);
    }

    /// <summary>
    /// The YZ cross-section fitted into a width × height pixel area, as a frozen drawing (show
    /// it through a <see cref="DrawingImage"/>). Lens outlines are filled with the lens colour
    /// at the lens opacity; rays are coloured by field or wavelength.
    /// </summary>
    public static DrawingGroup BuildCrossSection(OpticalLayout layout, double widthPx, double heightPx,
        OpticalSceneStyle? style = null, CrossSectionOptions? options = null)
    {
        style ??= OpticalSceneStyle.Default;
        options ??= new CrossSectionOptions { Coloring = style.Coloring, DeleteVignetted = style.DeleteVignetted };
        var drawing = CrossSection.Build(layout, options);
        return BuildCrossSectionDrawing(drawing, LayoutViewport2D.Fit(drawing.Bounds, widthPx, heightPx), style);
    }

    /// <summary>A built cross-section drawn through a given viewport, frozen.</summary>
    public static DrawingGroup BuildCrossSectionDrawing(CrossSectionDrawing drawing, LayoutViewport2D viewport,
        OpticalSceneStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        style ??= OpticalSceneStyle.Default;
        var group = new DrawingGroup();
        var lensFill = Frozen(new SolidColorBrush(style.LensColor) { Opacity = Math.Clamp(style.LensOpacity, 0, 1) });
        var outlinePen = FrozenPen(style.WireColor, 1.0);
        var mirrorPen = FrozenPen(Darker(style.MirrorColor), 1.5);
        var aperturePen = FrozenPen(style.ApertureColor, 2.0);
        var rayPens = new Dictionary<int, Pen>();

        foreach (var line in drawing.Lines)
        {
            if (line.Points.Count < 2) continue;
            Pen pen;
            Brush? fill = null;
            switch (line.Kind)
            {
                case LayoutLineKind.LensOutline: pen = outlinePen; fill = lensFill; break;
                case LayoutLineKind.Mirror: pen = mirrorPen; break;
                case LayoutLineKind.Aperture: pen = aperturePen; break;
                case LayoutLineKind.Ray:
                case LayoutLineKind.Fletch:
                    if (!rayPens.TryGetValue(line.ColorKey, out pen!))
                        rayPens[line.ColorKey] = pen = FrozenPen(style.RayColor(line.ColorKey), 0.75);
                    break;
                default: pen = outlinePen; break;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(ToPoint(viewport.ToPixel(line.Points[0])), fill is not null, line.Closed);
                for (int k = 1; k < line.Points.Count; k++)
                    context.LineTo(ToPoint(viewport.ToPixel(line.Points[k])), true, false);
            }
            geometry.Freeze();
            group.Children.Add(Frozen(new GeometryDrawing(fill, pen, geometry)));
        }
        group.Freeze();
        return group;
    }

    /// <summary>
    /// A triangle mesh as WPF geometry. Vertices are split per face id, so WPF's smoothed
    /// normals stay smooth across one optical face but the rim between a face, its flat and
    /// the edge stays sharp.
    /// </summary>
    internal static MeshGeometry3D ToMeshGeometry(TriangleMesh mesh)
    {
        var geometry = new MeshGeometry3D();
        var map = new Dictionary<(int Vertex, int Face), int>();
        int Index(int v, int face)
        {
            if (map.TryGetValue((v, face), out int i)) return i;
            var p = mesh.Vertices[v];
            i = geometry.Positions.Count;
            geometry.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            map[(v, face)] = i;
            return i;
        }
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            var tri = mesh.Triangles[t];
            int face = mesh.TriangleFaceIds[t];
            geometry.TriangleIndices.Add(Index(tri.A, face));
            geometry.TriangleIndices.Add(Index(tri.B, face));
            geometry.TriangleIndices.Add(Index(tri.C, face));
        }
        geometry.Freeze();
        return geometry;
    }

    private static double LayoutDiagonal(OpticalLayout layout, IReadOnlyList<LayoutBody> bodies,
        IReadOnlyList<IReadOnlyList<CoreVector>> wires)
    {
        var points = new List<CoreVector>();
        foreach (var b in bodies) { points.Add(b.Mesh.Bounds.Min); points.Add(b.Mesh.Bounds.Max); }
        foreach (var w in wires) points.AddRange(w);
        foreach (var r in layout.Rays) points.AddRange(r.Points);
        for (int i = 0; i < layout.Surfaces.Count; i++)
        {
            var s = layout.Surfaces[i];
            double radius = LayoutGeometry.DrawRadius(layout, i);
            points.Add(s.GlobalPoint(0, radius));
            points.Add(s.GlobalPoint(0, -radius));
        }
        if (points.Count == 0) return 1;
        double d = OpenSim.Core.Numerics.Aabb.FromPoints(points).Diagonal;
        return d > 0 ? d : 1;
    }

    private static Material BodyMaterial(Color color, double opacity, bool specular)
    {
        var brush = Frozen(new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) });
        Material material = new DiffuseMaterial(brush);
        if (specular)
        {
            var highlight = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), 255, 255, 255)));
            material = new MaterialGroup { Children = { material, new SpecularMaterial(highlight, 40) } };
        }
        material.Freeze();
        return material;
    }

    private static Color Darker(Color c) => Color.FromRgb((byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55));

    private static Point ToPoint(Point2 p) => new(p.X, p.Y);

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(color)), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
