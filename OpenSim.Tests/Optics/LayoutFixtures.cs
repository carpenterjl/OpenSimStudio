using OpenSim.Core.Numerics;
using OpenSim.Optics.Layout;

namespace OpenSim.Tests.Optics;

/// <summary>Lens prescriptions for the OP-8 layout tests. Lengths in meters.</summary>
internal static class LayoutFixtures
{
    public const double Mm = 1e-3;

    public static RigidTransform At(double z) => RigidTransform.FromTranslation(new Vector3D(0, 0, z));

    /// <summary>
    /// An equiconvex spherical singlet: R1 = +50 mm, R2 = −50 mm, 5 mm thick on axis, clear
    /// semi-diameter 10 mm on both faces, mechanical 12 mm on the front (so both faces get a
    /// flat from 10 to 12 mm). <paramref name="thicknessSign"/> −1 places the back face before
    /// the front, as after one mirror.
    /// </summary>
    public static OpticalLayout Singlet(int thicknessSign = 1) => new(new[]
    {
        new LayoutSurface(At(0), ConicSag.FromRadius(50 * Mm), 10 * Mm)
            { MechanicalSemiDiameter = 12 * Mm, MaterialAfter = "N-BK7" },
        new LayoutSurface(At(thicknessSign * 5 * Mm), ConicSag.FromRadius(-50 * Mm), 10 * Mm),
    });

    /// <summary>A cemented doublet (two bodies sharing surface 1) followed by a plane image
    /// surface, with the stop in front.</summary>
    public static OpticalLayout Doublet(IReadOnlyList<LayoutRayPath>? rays = null) => new(new[]
    {
        new LayoutSurface(At(-5 * Mm), ConicSag.Plane, 8 * Mm) { IsStop = true },
        new LayoutSurface(At(0), ConicSag.FromRadius(60 * Mm), 10 * Mm) { MaterialAfter = "N-BK7" },
        new LayoutSurface(At(6 * Mm), ConicSag.FromRadius(-45 * Mm), 10 * Mm) { MaterialAfter = "N-SF5" },
        new LayoutSurface(At(8 * Mm), ConicSag.FromRadius(-200 * Mm), 10 * Mm),
        new LayoutSurface(At(100 * Mm), ConicSag.Plane, 5 * Mm),
    }, rays);
}
