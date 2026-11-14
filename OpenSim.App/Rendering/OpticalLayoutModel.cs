using System.Windows.Media.Media3D;
using OpenSim.Optics.Layout;

namespace OpenSim.App.Rendering;

/// <summary>
/// The 3-D optical layout as WPF models (built by <see cref="SceneBuilder.BuildOpticalLayoutModel"/>).
/// <see cref="Root"/> holds the opaque models first (rays, wireframe, mirrors, apertures, opaque
/// lenses) and the transparent lens bodies last; WPF draws children in order and does not
/// sort translucent triangles, so <see cref="SortForCamera"/> must be called on every camera
/// move to keep the transparent bodies back to front. Every child model is frozen; the groups
/// are not, so this object belongs to the thread that built it.
/// </summary>
public sealed class OpticalLayoutModel
{
    private readonly List<GeometryModel3D> _transparent;
    private readonly List<OpenSim.Core.Numerics.Vector3D> _centers;
    private int[] _order;

    internal OpticalLayoutModel(Model3DGroup opaque, List<GeometryModel3D> transparent,
        List<OpenSim.Core.Numerics.Vector3D> centers)
    {
        Opaque = opaque;
        _transparent = transparent;
        _centers = centers;
        Transparent = new Model3DGroup();
        _order = new int[transparent.Count];
        for (int i = 0; i < _order.Length; i++)
        {
            _order[i] = i;
            Transparent.Children.Add(transparent[i]);
        }
        Root = new Model3DGroup { Children = { Opaque, Transparent } };
    }

    /// <summary>The whole layout: add this to the viewport.</summary>
    public Model3DGroup Root { get; }

    public Model3DGroup Opaque { get; }

    /// <summary>The transparent bodies, in the current back-to-front order.</summary>
    public Model3DGroup Transparent { get; }

    /// <summary>The body index (in build order) of each child of <see cref="Transparent"/>.</summary>
    public IReadOnlyList<int> TransparentOrder => _order;

    /// <summary>
    /// Puts the transparent bodies back to front for a camera at <paramref name="position"/>
    /// looking along <paramref name="lookDirection"/>. Children are only touched when the order
    /// changes, so calling this on every camera move is cheap.
    /// </summary>
    public void SortForCamera(Point3D position, Vector3D lookDirection)
    {
        if (_transparent.Count < 2) return;
        var order = DepthOrder.BackToFront(_centers,
            new OpenSim.Core.Numerics.Vector3D(position.X, position.Y, position.Z),
            new OpenSim.Core.Numerics.Vector3D(lookDirection.X, lookDirection.Y, lookDirection.Z));
        if (order.AsSpan().SequenceEqual(_order)) return;
        _order = order;
        Transparent.Children.Clear();
        foreach (int i in order) Transparent.Children.Add(_transparent[i]);
    }
}
