using System.Windows.Media;
using System.Windows.Media.Media3D;
using OpenSim.App.Rendering;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Layout;
using CoreVector = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.Tests.Rendering;

/// <summary>OP-8: the optical layout as WPF models. The plan's gate: a doublet renders with
/// both elements visible from either side.</summary>
public sealed class OpticalLayoutSceneTests
{
    private const double Mm = 1e-3;

    private static RigidTransform At(double z) => RigidTransform.FromTranslation(new CoreVector(0, 0, z));

    private static OpticalLayout Doublet() => new(new[]
    {
        new LayoutSurface(At(-5 * Mm), ConicSag.Plane, 8 * Mm) { IsStop = true },
        new LayoutSurface(At(0), ConicSag.FromRadius(60 * Mm), 10 * Mm) { MaterialAfter = "N-BK7" },
        new LayoutSurface(At(6 * Mm), ConicSag.FromRadius(-45 * Mm), 10 * Mm) { MaterialAfter = "N-SF5" },
        new LayoutSurface(At(8 * Mm), ConicSag.FromRadius(-200 * Mm), 10 * Mm),
        new LayoutSurface(At(100 * Mm), ConicSag.Plane, 5 * Mm),
    }, new[]
    {
        new LayoutRayPath(new[] { new CoreVector(0, 4 * Mm, -20 * Mm), new CoreVector(0, 4 * Mm, 0), new CoreVector(0, 0, 100 * Mm) }, 0, 0),
        new LayoutRayPath(new[] { new CoreVector(0, -4 * Mm, -20 * Mm), new CoreVector(0, -4 * Mm, 0), new CoreVector(0, 0, 100 * Mm) }, 1, 0),
    });

    /// <summary>
    /// Both lens bodies are transparent, drawn on both faces (BackMaterial), and after a camera
    /// move they are drawn back to front: from in front of the lens (camera at −z) the second
    /// body first, from behind (camera at +z) the first body first.
    /// </summary>
    [Fact]
    public void Doublet_BothBodiesVisible_FromEitherSide()
    {
        var model = SceneBuilder.BuildOpticalLayoutModel(Doublet());
        Assert.Equal(2, model.Transparent.Children.Count);
        foreach (GeometryModel3D body in model.Transparent.Children)
        {
            Assert.NotNull(body.BackMaterial);
            Assert.True(((MeshGeometry3D)body.Geometry).TriangleIndices.Count > 0);
        }

        model.SortForCamera(new Point3D(0, 0, -1), new System.Windows.Media.Media3D.Vector3D(0, 0, 1));
        Assert.Equal(new[] { 1, 0 }, model.TransparentOrder);
        var farFromFront = model.Transparent.Children[0];

        model.SortForCamera(new Point3D(0, 0, 1), new System.Windows.Media.Media3D.Vector3D(0, 0, -1));
        Assert.Equal(new[] { 0, 1 }, model.TransparentOrder);
        Assert.Same(farFromFront, model.Transparent.Children[1]);
        Assert.Same(model.Transparent, model.Root.Children[^1]);   // transparent bodies drawn last
    }

    [Fact]
    public void Rays_AreOneRibbonModelPerColour_AndApertureIsOpaque()
    {
        var model = SceneBuilder.BuildOpticalLayoutModel(Doublet(), OpticalSceneStyle.Default with { ShowWireframe = true });
        // Two ray colours, the wireframe, the aperture.
        Assert.Equal(4, model.Opaque.Children.Count);
        var opaqueLenses = SceneBuilder.BuildOpticalLayoutModel(Doublet(), OpticalSceneStyle.Default with { LensOpacity = 1 });
        Assert.Empty(opaqueLenses.Transparent.Children);
    }

    [Fact]
    public void Mesh_IsSplitPerFace_SoRimsStaySharp()
    {
        var layout = Doublet();
        var body = LayoutSolids.Build(layout, new LatheResolution(4, 16)).First(b => b.Kind == LayoutBodyKind.Lens);
        var geometry = SceneBuilder.ToMeshGeometry(body.Mesh);
        Assert.Equal(body.Mesh.Triangles.Count * 3, geometry.TriangleIndices.Count);
        // Front face, back face and edge: each rim vertex appears once per face that meets it.
        Assert.Equal(body.Mesh.Vertices.Count + 2 * 16, geometry.Positions.Count);
    }

    [Fact]
    public void CrossSection_IsOneFrozenDrawingPerLine()
    {
        var layout = Doublet();
        var lines = CrossSection.Build(layout).Lines.Count;
        var drawing = SceneBuilder.BuildCrossSection(layout, 800, 400);
        Assert.True(drawing.IsFrozen);
        Assert.Equal(lines, drawing.Children.Count);
        Assert.All(drawing.Children, d => Assert.IsType<GeometryDrawing>(d));
        var bounds = drawing.Bounds;
        Assert.InRange(bounds.Left, 0, 800);
        Assert.InRange(bounds.Right, 0, 800);
    }
}
