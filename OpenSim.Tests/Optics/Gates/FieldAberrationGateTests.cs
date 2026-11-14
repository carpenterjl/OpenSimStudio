using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;
using F = OpenSim.Tests.Optics.FieldAberrationFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-37's gate: "Petzval curvature of a thin lens and of a mirror from closed form; a pinhole
/// (paraxial) system's relative illumination is cos⁴; a symmetric unit-magnification relay has
/// zero distortion and lateral colour." Every expected value is derived in the comments from
/// the test's own inputs and the paraxial engine's focus; the lenses are arbitrary inputs.
/// </summary>
public sealed class FieldAberrationGateTests
{
    private const double Mm = F.Mm;
    private static readonly double Inf = F.Inf;

    private static IReadOnlyList<FieldCurvaturePoint> Scan(SequentialLens lens, OpticalSystemData system, double[] h)
    {
        var setup = F.Setup(lens, system);
        var curve = FieldCurvatureDistortion.Compute(setup, new[] { system.Wavelengths.PrimaryIndex }, FieldScan.PlusY, h,
            DistortionReference.FTanTheta, default).Single();
        Assert.Null(curve.FieldCurvatureFailure);
        return curve.Points;
    }

    /// <summary>The lens with its image surface at the paraxial focus of the paraxial engine
    /// (an independent code path), and that image distance.</summary>
    private static (SequentialLens Lens, double ImageDistance) AtParaxialFocus(SequentialLens lens, OpticalSystemData system)
    {
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        double t = p.ParaxialImageDistance!.Value;
        return (F.WithImageDistance(lens, t), t);
    }

    // ---- Petzval: concentric surfaces ----

    /// <summary>
    /// A concave mirror (radius R0, centre C) with the stop at C. Every chief ray passes through C,
    /// so it meets the mirror along a radius, at normal incidence, and goes back along itself:
    /// its image-space direction makes the field angle θ with the axis, |d_z| = cos θ. The pencil
    /// about a radius is the axial pencil turned about C, so its focus lies on that radius at the
    /// same distance from C as the axial (paraxial) focus: tangential and sagittal foci coincide
    /// (no astigmatism) on a sphere about C, the Petzval surface. With ρ the signed distance from C
    /// to the axial focus along the direction of travel, the focus at θ is C + ρ·d and the image
    /// plane (through the axial focus, normal to the axis) is at C + ρ·ẑ_travel, so the focus lies
    /// ρ·(cos θ − 1) beyond the plane along the travel (derived).
    /// </summary>
    [Fact]
    public void AMirrorWithItsStopAtTheCentre_FocusesOnItsPetzvalSphere()
    {
        double r0 = 100 * Mm;
        var system = F.Sys(10, FieldType.Angle, new[] { 0.0, 10.0 });
        // Stop plane at z = 0, mirror vertex at z = R0 with radius −R0: centre at z = 0, the stop.
        var (lens, t) = AtParaxialFocus(F.Lens(Inf, F.S(Inf, r0 / Mm, stop: true), F.S(-r0 / Mm, -50, mirror: true)), system);
        double zImage = r0 + t, zCentre = 0, travel = -1;   // light travels −z after the mirror
        double rho = (zImage - zCentre) * travel;
        var h = FieldScanCommon.Even(5);
        var points = Scan(lens, system, h);
        for (int i = 0; i < h.Length; i++)
        {
            double theta = h[i] * 10 * Math.PI / 180;
            double expected = rho * (Math.Cos(theta) - 1);
            F.Close(expected, points[i].Tangential, 1e-13, $"tangential at {h[i] * 10}°");
            F.Close(expected, points[i].Sagittal, 1e-13, $"sagittal at {h[i] * 10}°");
        }
        Assert.True(points[^1].Tangential > 0, "the mirror's Petzval sphere curves away from it, beyond the image plane");
    }

    /// <summary>
    /// The refracting analogue: a convex sphere (radius R0) into glass with the stop inside the
    /// glass at its centre C. C images onto itself through the sphere (a ray aimed at C meets the
    /// surface along a radius, at normal incidence, and is not bent), so the entrance pupil is at C
    /// and every chief ray goes undeviated through C at the field angle θ; as for the mirror the
    /// foci lie on a sphere about C through the axial focus, ρ·(cos θ − 1) beyond the image plane
    /// (derived), now towards the lens since ρ > 0.
    /// </summary>
    [Fact]
    public void ARefractingSphereWithItsStopAtTheCentre_FocusesOnItsPetzvalSphere()
    {
        double r0 = 50 * Mm, n = 1.5;
        var system = F.Sys(10, FieldType.Angle, new[] { 0.0, 15.0 });
        var (lens, t) = AtParaxialFocus(F.Lens(Inf, F.S(r0 / Mm, r0 / Mm, n), F.S(Inf, 100, n, stop: true)), system);
        double rho = (r0 + t) - r0;   // image vertex at z = R0 + t, centre at z = R0, light travels +z
        var h = FieldScanCommon.Even(4);
        var points = Scan(lens, system, h);
        for (int i = 0; i < h.Length; i++)
        {
            double theta = h[i] * 15 * Math.PI / 180;
            double expected = rho * (Math.Cos(theta) - 1);
            F.Close(expected, points[i].Tangential, 1e-13, $"tangential at {h[i] * 15}°");
            F.Close(expected, points[i].Sagittal, 1e-13, $"sagittal at {h[i] * 15}°");
        }
    }

    // ---- Petzval: thin lens ----

    /// <summary>
    /// A lens of zero thickness (curvatures c1, c2, index n in air) with the stop on it, object at
    /// infinity. The chief ray of field angle θ goes through the common vertex of both surfaces,
    /// where both normals are ẑ, so Coddington's equations (cited in <see cref="CoddingtonTrace"/>)
    /// close: with K = n·cos I′ − cos θ = √(n² − sin²θ) − cos θ, surface 1 gives n/s₁ = c1·K and
    /// n·cos²I′/t₁ = c1·K, and surface 2 (oblique factor cos θ − n cos I′ = −K, no transfer) gives
    /// 1/s′ = (c1 − c2)·K and cos²θ/t′ = (c1 − c2)·K. The image plane is at the back focus b
    /// (= f for a thin lens), reached along the ray after b/cos θ, so the foci lie
    /// Δs = s′cos θ − b and Δt = t′cos θ − b = s′cos³θ − b beyond it (derived, exact).
    /// <para>
    /// For small θ, K = (n − 1)(1 + θ²/(2n)) + O(θ⁴), s′ = f(1 − θ²/(2n)), so
    /// Δs = −fθ²(1/2 + 1/(2n)) and Δt = −fθ²(3/2 + 1/(2n)): the astigmatism Δt − Δs = −fθ² is
    /// twice what (3Δs − Δt)/2 = −fθ²/(2n) leaves out of Δs, and that astigmatism-free part is the
    /// Petzval surface z = −(φ/n)·h²/2 with h = fθ, curvature −φ/n (derived from the expansion).
    /// </para>
    /// </summary>
    [Fact]
    public void AThinLensWithItsStopOnIt_HasTheClosedFormFoci_AndPetzvalCurvatureMinusPhiOverN()
    {
        double r1 = 60 * Mm, r2 = -90 * Mm, n = 1.5;
        var system = F.Sys(10, FieldType.Angle, new[] { 0.0, 20.0 });
        var lens0 = F.Lens(Inf, F.S(r1 / Mm, 0, n, stop: true), F.S(r2 / Mm, 100));
        var p = ParaxialSystem.Create(lens0.ToParaxialLens(), system);
        double b = p.BackFocalLength!.Value, f = p.EffectiveFocalLength!.Value;
        var lens = F.WithImageDistance(lens0, b);
        double c1 = 1 / r1, c2 = 1 / r2;

        var h = new[] { 0.0, 0.25, 0.5, 1.0 };
        var points = Scan(lens, system, h);
        for (int i = 0; i < h.Length; i++)
        {
            double theta = h[i] * 20 * Math.PI / 180;
            double k = Math.Sqrt(n * n - Math.Sin(theta) * Math.Sin(theta)) - Math.Cos(theta);
            double s = 1 / ((c1 - c2) * k);
            F.Close(s * Math.Cos(theta) - b, points[i].Sagittal, 1e-13, $"sagittal at {h[i] * 20}°");
            F.Close(s * Math.Pow(Math.Cos(theta), 3) - b, points[i].Tangential, 1e-13, $"tangential at {h[i] * 20}°");
        }

        // Petzval at a small field: 0.2°, where the O(θ²) remainder of the expansion is ~10⁻⁵.
        var small = Scan(lens, system, new[] { 0.01 }).Single();
        double th = 0.2 * Math.PI / 180;
        double petzval = (3 * small.Sagittal - small.Tangential) / 2;
        double expectedPetzval = -(1 / f) / n * (f * th) * (f * th) / 2;
        F.Close(1, petzval / expectedPetzval, 1e-3, "Petzval sag over −(φ/n)·h²/2");
    }

    // ---- Relative illumination of a paraxial lens: cos⁴ ----

    /// <summary>
    /// An ideal lens (focal length f) with the stop on it (radius a), object at infinity, image at
    /// f. The lens images the field angle θ perfectly at P = (0, f tan θ, f), so the rays arriving
    /// at P are exactly those from the stop disk to P. A disk element dA at q seen from P at
    /// distance R subtends dΩ = cos α·dA/R² with cos α = f/R (the disk and the image plane are both
    /// normal to z), and its projected solid angle on the image plane is cos α·dΩ = f²·dA/R⁴,
    /// R² = f² + |q − P|². So A(θ) = ∫∫_disk f²/(f² + r² − 2ry sin φ + y²)² r dr dφ, y = f tan θ,
    /// computed here by its own quadrature (40 Gauss–Legendre radii × 256 angles), and
    /// RI = A(θ)/A(0). For a small stop, f² + y² = f²/cos²θ gives A ≈ π a² cos⁴θ/f²: the cos⁴ law.
    /// Expanding (1 + ε)⁻² with ε = (r² − 2ry sin φ)/(f² + y²) over the disk, the correction is
    /// RI/cos⁴θ − 1 = (a/f)²·(1 − cos²θ(1 − 3 sin²θ)) + O((a/f)⁴), at most (a/f)² over 0–30°
    /// (derived), 10⁻⁴ here.
    /// </summary>
    [Fact]
    public void AParaxialLens_HasCosineFourthRelativeIllumination()
    {
        double f = 0.1, a = 1 * Mm;
        var lens = F.Lens(Inf, F.S(Inf, f / Mm, stop: true, shape: new ParaxialShape(f)));
        var system = F.Sys(2 * a / Mm, FieldType.Angle, new[] { 0.0, 30.0 });
        var setup = F.Setup(lens, system, false);
        var h = FieldScanCommon.Even(4);
        var ri = RelativeIllumination.Compute(setup, 0, FieldScan.PlusY, 20, h, null, default);

        var (rn, rw) = GaussLegendre.Rule(40, 0, a);
        double Area(double theta)
        {
            double y = f * Math.Tan(theta), sum = 0;
            const int m = 256;
            for (int i = 0; i < rn.Length; i++)
                for (int k = 0; k < m; k++)
                {
                    double phi = 2 * Math.PI * k / m, r = rn[i];
                    double d = f * f + r * r - 2 * r * y * Math.Sin(phi) + y * y;
                    sum += rw[i] * r * (2 * Math.PI / m) * f * f / (d * d);
                }
            return sum;
        }
        double a0 = Area(0);
        for (int i = 0; i < h.Length; i++)
        {
            double theta = h[i] * 30 * Math.PI / 180;
            // The method's error: spectral quadrature of a smooth integrand plus central
            // differences at 10⁻⁵ of the pupil (rounding ~10⁻⁹ relative).
            F.Close(Area(theta) / a0, ri.Relative[i], 1e-7, $"RI against the exact disk integral at {h[i] * 30}°");
            F.Close(Math.Pow(Math.Cos(theta), 4), ri.Relative[i], 2e-4, $"RI against cos⁴ at {h[i] * 30}°");
        }
    }

    // ---- Symmetric unit-magnification relay ----

    /// <summary>
    /// Two lenses mirror-symmetric about a plane stop, object and image at equal distances from
    /// the outer vertices, a dispersive glass, ray aiming on (so each wavelength's chief ray goes
    /// through the real stop centre). Turning the first half by 180° about the x axis through the
    /// stop centre maps it onto the second half (rotationally symmetric surfaces: the turn is the
    /// z mirror), and a chief ray from (0, y) through the stop centre continues along the turned,
    /// reversed copy of its first half, to (0, −y) on the image plane: magnification −1 exactly at
    /// every field and wavelength (derived). So the real image height is |y| = 1·|y|, the
    /// small-field reference is the same line, and distortion and lateral colour are zero; the
    /// paraxial chief ray of each wavelength (through that wavelength's stop centre) likewise.
    /// Tolerances: the aimed chief ray meets the stop within 10⁻¹¹ of its radius (the launcher's
    /// accept tolerance), about 4·10⁻¹⁴ m here, which moves the small-field slope by ~10⁻⁷ and a
    /// percentage distortion by ~10⁻⁵ %; an asymmetric lens gives tenths of a percent.
    /// </summary>
    [Fact]
    public void ASymmetricUnitMagnificationRelay_HasNoDistortionAndNoLateralColour()
    {
        var glass = F.TestGlass();
        var lens = F.Lens(100,
            F.S(80, 6, medium: glass), F.S(-40, 20), F.S(Inf, 20, stop: true), F.S(40, 6, medium: glass), F.S(-80, 100));
        var system = F.Sys(8, FieldType.ObjectHeight, new[] { 0.0, 5.0 }, RayAimingMode.Real, new[] { 0.45, 0.55, 0.65 }, primary: 1);
        var setup = F.Setup(lens, system);
        var h = FieldScanCommon.Even(5);
        var rows = new[] { 0, 1, 2 };

        foreach (var curve in FieldCurvatureDistortion.Compute(setup, rows, FieldScan.PlusY, h, DistortionReference.FTanTheta, default))
        {
            F.Close(1, curve.SmallFieldSlope, 1e-6, $"magnification, wavelength {curve.Row + 1}");
            for (int i = 1; i < h.Length; i++)
                F.Close(0, curve.Points[i].DistortionPercent, 1e-3, $"distortion at H = {h[i]}, wavelength {curve.Row + 1}");
        }

        foreach (bool real in new[] { true, false })
        {
            var y = LateralColorAnalysis.ChiefHeights(setup, rows, h, real, default);
            for (int i = 0; i < h.Length; i++)
            {
                double objectHeight = h[i] * 5 * Mm;
                F.Close(-objectHeight, y[1][i], 1e-12, $"{(real ? "real" : "paraxial")} image height at H = {h[i]}");
                F.Close(0, y[0][i] - y[2][i], 1e-12, $"{(real ? "real" : "paraxial")} lateral colour at H = {h[i]}");
            }
        }

        var grid = GridDistortion.Compute(setup, 1, new GridDistortionSettings { GridSize = 5 }, default);
        F.Close(-1, grid.A, 1e-6, "A");
        F.Close(-1, grid.D, 1e-6, "D");
        F.Close(0, grid.B, 1e-6, "B");
        F.Close(0, grid.C, 1e-6, "C");
        foreach (var point in grid.Points)
        {
            Assert.Null(point.Failure);
            if (double.IsFinite(point.Percent)) F.Close(0, point.Percent, 1e-3, $"grid distortion at ({point.Fx}, {point.Fy})");
        }
        Assert.NotNull(grid.SmiaTv);
        F.Close(0, grid.SmiaTv!.Value, 1e-3, "SMIA-TV distortion");
    }
}
