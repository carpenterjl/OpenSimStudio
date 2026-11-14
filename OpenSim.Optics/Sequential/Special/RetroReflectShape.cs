using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// OpticStudio's Retro Reflect surface (OpticStudio 2025 R2 help, "Retro Reflect"): "a planar
/// shape surface. If the surface is made of glass or air, rays refract according to Snell's law
/// in the usual way. If the surface is made of "MIRROR", rays do not reflect in the usual way;
/// instead the rays are undeviated. This means under subsequent propagation in the opposite
/// direction the rays will follow the incident path". On a mirror the direction leaving is
/// therefore −d at the same point, and the trace adds no path. No parameters.
/// <para>
/// First order: on a mirror the slope u = L/N is unchanged (both cosines change sign), so the
/// paraxial engine gets the identity on (y, u), which its signed indices turn into
/// diag(1, −1) on (y, n·u) (docs/Optics-Conventions.md §18); off a mirror it is a plane.
/// The help warns that OpticStudio's OPD after this surface is inaccurate for fast beams with
/// large aberrations; here the path is the geometric one, which is exact.
/// </para>
/// </summary>
public sealed class RetroReflectShape : SurfaceShape
{
    public override string TypeName => "Retro Reflect";

    public override double Sag(double x, double y, SequentialSurface surface) => 0;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        point = new Vector3D(x, y, 0);
        distance = 0;
        return true;
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) => Vector3D.UnitZ;

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        surface.IsMirror ? new() { SlopeMatrix = ParaxialMatrix.Identity } : new();

    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        extraOpticalPath = 0;
        failure = RayStatus.Ok;
        if (!input.Surface.IsMirror)
        {
            direction = input.Incoming;
            return false;   // glass or air: Snell's law at the plane, done by the trace
        }
        direction = -input.Incoming;
        return true;
    }
}
