using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>What a body of the shaded model is.</summary>
public enum LayoutBodyKind
{
    Lens = 0,
    Mirror = 1,
    Aperture = 2,
}

/// <summary>
/// One body of the shaded model, in the global frame (meters). <see cref="IsClosed"/> is true
/// for a solid (a lens, a mirror with its substrate) and false for a sheet (a bare mirror
/// surface, an aperture annulus), which the renderer shows from both sides.
/// <see cref="Surface"/> is the first surface the body belongs to.
/// </summary>
public sealed record LayoutBody(LayoutBodyKind Kind, TriangleMesh Mesh, int Surface, bool IsClosed)
{
    /// <summary>The centre of the body's bounding box: the point back-to-front ordering sorts
    /// on (<see cref="DepthOrder"/>).</summary>
    public Vector3D Center => Mesh.Bounds.Center;
}
