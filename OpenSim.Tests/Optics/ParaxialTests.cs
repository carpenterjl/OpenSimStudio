using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-19: the paraxial engine against closed forms derived in the comments (every expected value
/// is computed in the test from the textbook first-order relations, not typed in), the sign
/// conventions of docs/Optics-Conventions.md §11, and OP-18's deferred gate: every aperture type
/// gives the same entrance pupil, and every paraxially defined field type the same chief ray,
/// where they are equivalent by construction. The lens values (radii, thicknesses, indices) are
/// arbitrary test inputs, not reference data.
/// </summary>
public sealed class ParaxialTests
{
    // ---- Builders ----

    private static ParaxialSurface S(double radius, double thickness, double n = 1, bool stop = false,
        bool mirror = false, double? semiDiameter = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, SemiDiameter = semiDiameter,
    };

    /// <summary>Object surface (distance <paramref name="objectDistance"/>), the surfaces, image surface.</summary>
    private static ParaxialLens Lens(double objectDistance, params ParaxialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new ParaxialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new ParaxialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(Aperture? aperture = null, FieldType type = FieldType.Angle,
        params FieldPoint[] fields) => new()
    {
        Aperture = aperture ?? new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Fields = new FieldSet { Type = type, Points = fields.Length == 0 ? new[] { new FieldPoint() } : fields },
    };

    private static void Close(double expected, double actual, double relative)
    {
        double scale = Math.Max(Math.Abs(expected), double.Epsilon);
        Assert.True(Math.Abs(actual - expected) <= relative * scale,
            $"Expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {relative * scale:R}).");
    }

    private static void SameRay(ParaxialRay expected, ParaxialRay actual, double relative)
    {
        double scale = expected.Heights.Where(double.IsFinite).Select(Math.Abs).Concat(expected.Slopes.Select(Math.Abs)).Max();
        for (int j = 0; j < expected.Heights.Count; j++)
        {
            if (double.IsFinite(expected.Heights[j]))
                Assert.True(Math.Abs(expected.Heights[j] - actual.Heights[j]) <= relative * scale, $"height at surface {j}: {expected.Heights[j]:R} vs {actual.Heights[j]:R}");
            Assert.True(Math.Abs(expected.Slopes[j] - actual.Slopes[j]) <= relative * scale, $"slope after surface {j}: {expected.Slopes[j]:R} vs {actual.Slopes[j]:R}");
        }
    }

    // ---- Thin and thick lens ----

    [Fact]
    public void ThickLens_FollowsTheLensmakersEquation_AndThePrincipalPlanePositions()
    {
        double r1 = 0.05, r2 = -0.08, t = 0.006, n = 1.6;
        var lens = Lens(double.PositiveInfinity, S(r1, t, n, stop: true), S(r2, 0.05));
        var p = ParaxialSystem.Create(lens, Sys());

        // Surface powers φ1 = (n − 1)c1, φ2 = (1 − n)c2. A ray parallel to the axis at height 1
        // leaves surface 1 with ν = −φ1, reaches surface 2 at y2 = 1 − (t/n)φ1 and leaves it with
        // ν = −φ1 − y2·φ2 = −φ, so φ = φ1 + φ2 − (t/n)φ1φ2, which is the lensmaker's equation
        // (n − 1)[c1 − c2 + (n − 1)·t·c1·c2/n]. In air the ray crosses the axis y2/φ behind
        // surface 2 (BFL); the rear principal plane is BFL − 1/φ = −(t/n)φ1/φ from surface 2.
        // Reversing the lens gives FFL = −(1 − (t/n)φ2)/φ and the front principal plane at
        // +(t/n)φ2/φ from surface 1. In air the nodal points are the principal points.
        double c1 = 1 / r1, c2 = 1 / r2;
        double lensmaker = (n - 1) * (c1 - c2 + (n - 1) * t * c1 * c2 / n);
        double phi1 = (n - 1) * c1, phi2 = (1 - n) * c2;
        Close(1 / lensmaker, p.EffectiveFocalLength!.Value, 1e-14);
        Close(lensmaker, p.Power, 1e-14);
        Close((1 - t / n * phi1) / lensmaker, p.BackFocalLength!.Value, 1e-14);
        Close(-(1 - t / n * phi2) / lensmaker, p.FrontFocalLength!.Value, 1e-14);
        Close(-(t / n) * phi1 / lensmaker, p.RearPrincipalPlane!.Value, 1e-13);
        Close(t / n * phi2 / lensmaker, p.FrontPrincipalPlane!.Value, 1e-13);
        Close(p.FrontPrincipalPlane!.Value, p.FrontNodalPoint!.Value, 1e-13);
        Close(p.RearPrincipalPlane!.Value, p.RearNodalPoint!.Value, 1e-13);
        // In air both focal lengths are 1/φ in size: f′ = n′/φ, f = −n/φ.
        Close(1 / lensmaker, p.ImageSpaceFocalLength!.Value, 1e-14);
        Close(-1 / lensmaker, p.ObjectSpaceFocalLength!.Value, 1e-14);
        // Object at infinity: the paraxial image is the rear focal point.
        Close(p.BackFocalLength!.Value, p.ParaxialImageDistance!.Value, 1e-14);
        Assert.Equal(0, p.LateralMagnification);
        Assert.False(p.IsAfocal);
    }

    [Fact]
    public void ThinLens_HasPrincipalPlanesAtItsVertex_AndPowerFromTheLensmaker()
    {
        double r1 = 0.1, r2 = -0.2, n = 1.5;
        var p = ParaxialSystem.Create(Lens(double.PositiveInfinity, S(r1, 0, n, stop: true), S(r2, 0.1)), Sys());
        // t = 0 in the lensmaker's equation: φ = (n − 1)(c1 − c2); BFL = EFL; planes at the vertex.
        double phi = (n - 1) * (1 / r1 - 1 / r2);
        Close(1 / phi, p.EffectiveFocalLength!.Value, 1e-15);
        Close(1 / phi, p.BackFocalLength!.Value, 1e-15);
        Assert.Equal(0, p.FrontPrincipalPlane!.Value, 15);
        Assert.Equal(0, p.RearPrincipalPlane!.Value, 15);
        // Stop at the lens, object at infinity: the entrance pupil is the stop, at surface 1;
        // the image space F/# is f/D.
        Assert.Equal(0, p.EntrancePupilPosition!.Value);
        Close(0.01, p.EntrancePupilDiameter!.Value, 1e-15);
        Close(1 / phi / 0.01, p.ImageSpaceFNumber!.Value, 1e-14);
    }

    /// <summary>An equiconvex thin lens of focal length f in glass of index 1.5: c = ±1/(2f(n − 1)).</summary>
    private static ParaxialSurface[] ThinLens(double f, double distanceAfter, bool stop = false)
    {
        const double n = 1.5;
        double r = 2 * f * (n - 1);
        return new[] { S(r, 0, n, stop), S(-r, distanceAfter) };
    }

    [Fact]
    public void TwoThinLenses_FollowTheCombinationFormula()
    {
        double f1 = 0.1, f2 = 0.05, d = 0.03;
        var p = ParaxialSystem.Create(Lens(double.PositiveInfinity, ThinLens(f1, d, stop: true).Concat(ThinLens(f2, 0.05)).ToArray()), Sys());
        // Two thin lenses d apart in air: φ = φ1 + φ2 − d·φ1·φ2 (the y-nu trace of a ray at
        // height 1: after lens 1 ν = −φ1, at lens 2 y = 1 − dφ1, after it ν = −φ1 − (1 − dφ1)φ2).
        // BFL = (1 − dφ1)/φ from lens 2, FFL = −(1 − dφ2)/φ from lens 1.
        double phi1 = 1 / f1, phi2 = 1 / f2, phi = phi1 + phi2 - d * phi1 * phi2;
        Close(1 / phi, p.EffectiveFocalLength!.Value, 1e-14);
        Close((1 - d * phi1) / phi, p.BackFocalLength!.Value, 1e-14);
        Close(-(1 - d * phi2) / phi, p.FrontFocalLength!.Value, 1e-14);
    }

    // ---- Telescopes and afocal systems ----

    [Theory]
    [InlineData(0.2, 0.05)]     // Keplerian: two positive lenses, inverted image
    [InlineData(0.2, -0.05)]    // Galilean: positive objective, negative eyepiece, erect image
    public void Telescope_IsAfocal_WithAngularMagnificationMinusF1OverF2(double f1, double f2)
    {
        double d = f1 + f2;
        var system = Sys(new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02, AfocalImageSpace = true },
            FieldType.Angle, new FieldPoint(), new FieldPoint { Y = 1 });
        var p = ParaxialSystem.Create(Lens(double.PositiveInfinity, ThinLens(f1, d, stop: true).Concat(ThinLens(f2, 0.05)).ToArray()), system);
        // With d = f1 + f2 the power φ1 + φ2 − dφ1φ2 = (f1 + f2 − d)/(f1 f2) is zero; the
        // system matrix's D = 1 − dφ2 = −f1/f2, and with C = 0 a ray leaves with ν′ = D·ν, so
        // in air every slope is multiplied by −f1/f2.
        Assert.True(p.IsAfocal);
        Assert.Null(p.EffectiveFocalLength);
        Assert.Null(p.BackFocalLength);
        Assert.True(p.ImageAtInfinity);
        Assert.Null(p.WorkingFNumber);
        Assert.Null(p.ImageSpaceFNumber);
        Close(-f1 / f2, p.AngularMagnification!.Value, 1e-13);
        Close(-f1 / f2, p.ChiefRay.Slopes[^1] / p.ChiefRay.Slopes[0], 1e-13);
        // The beam leaves |f2/f1| times as wide: the exit pupil is the stop's image.
        Close(0.02 * Math.Abs(f2 / f1), p.ExitPupilDiameter!.Value, 1e-13);
        Assert.Empty(p.Notes);   // afocal and the afocal setting agree

        // Moving the eyepiece by 1 µm gives the system power, which is found.
        var defocused = ParaxialSystem.Create(Lens(double.PositiveInfinity, ThinLens(f1, d + 1e-6, stop: true).Concat(ThinLens(f2, 0.05)).ToArray()), system with
        {
            Aperture = system.Aperture with { AfocalImageSpace = false }
        });
        Assert.False(defocused.IsAfocal);
        Close(-1e-6 / (f1 * f2), defocused.Power, 1e-6);
    }

    [Fact]
    public void ImageSpaceFNumber_IsRefusedForAnAfocalSystem()
    {
        var system = Sys(new Aperture { Type = ApertureType.ImageSpaceFNumber, Value = 4 });
        var lens = Lens(double.PositiveInfinity, ThinLens(0.2, 0.25, stop: true).Concat(ThinLens(0.05, 0.05)).ToArray());
        var e = Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens, system));
        Assert.Contains("afocal", e.Message);
    }

    // ---- Lagrange invariant ----

    public static IEnumerable<object[]> InvariantSystems()
    {
        // A finite-conjugate four-element lens with the stop inside it and object-height fields.
        yield return new object[]
        {
            Lens(0.2, S(0.04, 0.005, 1.62), S(-0.1, 0.01), S(0, 0.01, stop: true), S(-0.03, 0.003, 1.7), S(0.2, 0.05)),
            Sys(null, FieldType.ObjectHeight, new FieldPoint(), new FieldPoint { Y = 0.01 }),
        };
        // A two-mirror system from infinity with an angle field (signed indices −1 then +1).
        yield return new object[]
        {
            Lens(double.PositiveInfinity, S(-0.4, -0.15, mirror: true, stop: true), S(-0.15, 0.2, mirror: true)),
            Sys(null, FieldType.Angle, new FieldPoint(), new FieldPoint { Y = 0.5 }),
        };
    }

    [Theory]
    [MemberData(nameof(InvariantSystems))]
    public void LagrangeInvariant_IsTheSameAtEverySurface(ParaxialLens lens, OpticalSystemData system)
    {
        var p = ParaxialSystem.Create(lens, system);
        // H = n(ū·y − u·ȳ): unchanged by a transfer (y′ = y + (t/n)ν adds (t/n)(ν̄ν − νν̄) = 0)
        // and by a surface (ν′ = ν − yφ adds −φ(ȳy − yȳ) = 0), so constant to rounding.
        double h = p.LagrangeInvariant;
        Assert.NotEqual(0, h);
        var all = p.LagrangeInvariants();
        for (int j = 0; j < all.Count; j++)
        {
            if (j == 0 && lens.ObjectAtInfinity) { Assert.True(double.IsNaN(all[0])); continue; }
            Assert.True(Math.Abs(all[j] - h) <= 1e-13 * Math.Abs(h), $"surface {j}: H = {all[j]:R}, against {h:R}");
        }
        // The matrices have determinant 1 in (y, n·u).
        Assert.True(Math.Abs(p.SystemMatrix.Determinant - 1) <= 1e-12, $"det = {p.SystemMatrix.Determinant:R}");
        Assert.Equal(p.SystemMatrix, p.Matrix(1, lens.LastSurfaceIndex));
    }

    // ---- Mirrors ----

    [Fact]
    public void ConcaveMirror_FocusesAtHalfItsRadius_WithNodalPointsAtTheCentre()
    {
        // Concave toward incoming light: centre of curvature toward −z, so R < 0 (§3).
        double r = -0.2;
        var p = ParaxialSystem.Create(Lens(double.PositiveInfinity, S(r, r / 2, mirror: true, stop: true)), Sys());
        // φ = c(n′ − n) with n′ = −1 (signed, after one mirror): φ = −2/R > 0, a converging
        // mirror, EFL = −R/2. The focus is |R|/2 in front of the mirror, toward −z: BFL = R/2,
        // negative like the thickness after a mirror. Both nodal points sit at the centre of
        // curvature, z = R, and both principal points at the vertex.
        Close(-2 / r, p.Power, 1e-15);
        Close(-r / 2, p.EffectiveFocalLength!.Value, 1e-15);
        Close(r / 2, p.BackFocalLength!.Value, 1e-15);
        Close(r, p.FrontNodalPoint!.Value, 1e-15);
        Close(r, p.RearNodalPoint!.Value, 1e-15);
        Assert.Equal(0, p.FrontPrincipalPlane!.Value, 15);
        Assert.Equal(0, p.RearPrincipalPlane!.Value, 15);
        Assert.Equal(-1, p.SignedIndices[1]);
        Assert.Equal(1, p.Indices[1]);
        Close(r / 2, p.ParaxialImageDistance!.Value, 1e-15);

        // An object at the centre of curvature images onto itself, inverted: the marginal ray
        // from the axis at z = R returns along the same line with ν′ = −ν, so m = ν/ν′ = −1.
        var centre = ParaxialSystem.Create(Lens(-r, S(r, r, mirror: true, stop: true)), Sys());
        Close(-1, centre.LateralMagnification!.Value, 1e-15);
        Close(r, centre.ParaxialImageDistance!.Value, 1e-15);
    }

    // ---- Finite conjugates ----

    [Fact]
    public void FiniteConjugates_FollowTheGaussianLensEquation()
    {
        double f = 0.1, objectDistance = 0.3, h = 0.01;
        // Gauss: 1/s′ − 1/s = 1/f with s = −0.3 (object in front, measured from the lens), so
        // s′ = 1/(1/f + 1/s) and m = s′/s.
        double s = -objectDistance, sPrime = 1 / (1 / f + 1 / s), m = sPrime / s;
        var lens = Lens(objectDistance, ThinLens(f, sPrime, stop: true));
        var p = ParaxialSystem.Create(lens, Sys(null, FieldType.ObjectHeight, new FieldPoint(), new FieldPoint { Y = h }));
        Close(sPrime, p.ParaxialImageDistance!.Value, 1e-14);
        Close(m, p.LateralMagnification!.Value, 1e-14);
        Close(m * h, p.ParaxialImageHeight!.Value, 1e-13);
        Close(m * h, p.ImageSurfaceChiefHeight, 1e-13);   // the image surface is at the paraxial image
        // NA in object space: the marginal ray goes from the axis to the pupil rim, slope 0.005/0.3.
        Close(Math.Sin(Math.Atan(0.005 / objectDistance)), p.ObjectNumericalAperture, 1e-14);
        // Working F/# is 1/(2|u′|), u′ = 0.005/s′ to the image.
        Close(1 / (2 * (0.005 / sPrime)), p.WorkingFNumber!.Value, 1e-13);
    }

    // ---- OP-18's deferred gate: aperture types ----

    /// <summary>A finite-conjugate thick lens whose stop sits behind it, so the entrance pupil
    /// is a magnified, displaced image of the stop.</summary>
    private static ParaxialLens StopBehind(double objectDistance, double? stopSemiDiameter = null) =>
        Lens(objectDistance, S(0.05, 0.006, 1.6), S(-0.08, 0.01), S(0, 0.06, stop: true, semiDiameter: stopSemiDiameter));

    [Fact]
    public void EveryApertureType_GivesTheSameEntrancePupil_WhereEquivalent_FiniteObject()
    {
        double epd = 0.008;
        var lens = StopBehind(0.3);
        var reference = ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd }));
        Assert.NotEqual(0, reference.EntrancePupilPosition!.Value);
        Close(epd, reference.EntrancePupilDiameter!.Value, 1e-14);

        double coneDegrees = Math.Atan(Math.Abs(reference.MarginalRay.Slopes[0])) * (180 / Math.PI);
        var cases = new (Aperture Aperture, ParaxialLens Lens)[]
        {
            (new Aperture { Type = ApertureType.ImageSpaceFNumber, Value = reference.ImageSpaceFNumber!.Value }, lens),
            (new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = reference.ObjectNumericalAperture }, lens),
            (new Aperture { Type = ApertureType.ObjectConeAngle, Value = coneDegrees }, lens),
            (new Aperture { Type = ApertureType.FloatByStopSize }, StopBehind(0.3, Math.Abs(reference.MarginalRay.Heights[3]))),
            (new Aperture { Type = ApertureType.ParaxialWorkingFNumber, Value = reference.WorkingFNumber!.Value }, lens),
        };
        foreach (var (aperture, l) in cases)
        {
            var p = ParaxialSystem.Create(l, Sys(aperture));
            Close(epd, p.EntrancePupilDiameter!.Value, 1e-13);
            SameRay(reference.MarginalRay, p.MarginalRay, 1e-13);
        }
    }

    [Fact]
    public void EveryApertureType_GivesTheSameEntrancePupil_WhereEquivalent_ObjectAtInfinity()
    {
        double epd = 0.008;
        var lens = StopBehind(double.PositiveInfinity);
        var reference = ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd }));
        var cases = new (Aperture Aperture, ParaxialLens Lens)[]
        {
            (new Aperture { Type = ApertureType.ImageSpaceFNumber, Value = reference.ImageSpaceFNumber!.Value }, lens),
            (new Aperture { Type = ApertureType.FloatByStopSize }, StopBehind(double.PositiveInfinity, Math.Abs(reference.MarginalRay.Heights[3]))),
            (new Aperture { Type = ApertureType.ParaxialWorkingFNumber, Value = reference.WorkingFNumber!.Value }, lens),
        };
        foreach (var (aperture, l) in cases)
        {
            var p = ParaxialSystem.Create(l, Sys(aperture));
            Close(epd, p.EntrancePupilDiameter!.Value, 1e-13);
            SameRay(reference.MarginalRay, p.MarginalRay, 1e-13);
        }
        // An object at infinity sends no cone: NA and cone angle are refused, not guessed.
        Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.1 })));
        Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.ObjectConeAngle, Value = 5 })));
    }

    [Fact]
    public void TelecentricObjectSpace_ApertureTypesAgree_AndChiefRaysEnterParallel()
    {
        var lens = StopBehind(0.3);
        var fields = new[] { new FieldPoint(), new FieldPoint { Y = 0.005 } };
        var reference = ParaxialSystem.Create(lens, Sys(
            new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.05, TelecentricObjectSpace = true },
            FieldType.ObjectHeight, fields));
        Assert.True(reference.EntrancePupilAtInfinity);
        Assert.Null(reference.EntrancePupilDiameter);
        Assert.Equal(0, reference.ChiefRay.Slopes[0]);
        Close(0.005, reference.ChiefRay.Heights[0], 1e-15);
        Close(0.05, reference.ObjectNumericalAperture, 1e-14);

        double coneDegrees = Math.Atan(Math.Abs(reference.MarginalRay.Slopes[0])) * (180 / Math.PI);
        var cases = new (Aperture Aperture, ParaxialLens Lens)[]
        {
            (new Aperture { Type = ApertureType.ObjectConeAngle, Value = coneDegrees, TelecentricObjectSpace = true }, lens),
            (new Aperture { Type = ApertureType.FloatByStopSize, TelecentricObjectSpace = true }, StopBehind(0.3, Math.Abs(reference.MarginalRay.Heights[3]))),
            (new Aperture { Type = ApertureType.ParaxialWorkingFNumber, Value = reference.WorkingFNumber!.Value, TelecentricObjectSpace = true }, lens),
        };
        foreach (var (aperture, l) in cases)
            SameRay(reference.MarginalRay, ParaxialSystem.Create(l, Sys(aperture, FieldType.ObjectHeight, fields)).MarginalRay, 1e-13);

        // Angle fields mean nothing when every chief ray enters parallel.
        Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens, Sys(
            new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.05, TelecentricObjectSpace = true },
            FieldType.Angle, new FieldPoint(), new FieldPoint { Y = 2 })));
    }

    [Fact]
    public void AStopAtTheRearFocalPoint_PutsTheEntrancePupilAtInfinity()
    {
        // A thin lens of focal length f with the stop f behind it: A_s = 1 − f·φ = 0, so the
        // stop's image is at infinity and the entrance pupil diameter is undefined.
        double f = 0.1;
        var lens = Lens(0.3, ThinLens(f, f).Append(S(0, 0.2, stop: true)).ToArray());
        var e = Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens, Sys()));
        Assert.Contains("infinity", e.Message);
        var p = ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.02 },
            FieldType.ObjectHeight, new FieldPoint(), new FieldPoint { Y = 0.003 }));
        Assert.True(p.EntrancePupilAtInfinity);
        Assert.Contains(p.Notes, n => n.Contains("telecentric"));
        // The chief ray launched parallel crosses the axis at the stop.
        Assert.True(Math.Abs(p.ChiefRay.Heights[3]) <= 1e-15 * 0.003);
    }

    // ---- OP-18's deferred gate: field types ----

    [Fact]
    public void EveryParaxialFieldType_LaunchesTheSameChiefRay_WhereEquivalent()
    {
        double objectDistance = 0.3, angle = 5;
        var lens = StopBehind(objectDistance);
        var byAngle = ParaxialSystem.Create(lens, Sys(null, FieldType.Angle, new FieldPoint(), new FieldPoint { Y = angle }));
        var reference = byAngle.ChiefRay;

        // An object height h is the angle field whose chief ray, aimed at the entrance-pupil
        // centre z_E, starts at height h on the object plane: h = −(z_E + t0)·tan θ.
        double zE = byAngle.EntrancePupilPosition!.Value;
        double h = -(zE + objectDistance) * Math.Tan(angle * Math.PI / 180);
        // h is negative for a positive angle, and the reports' ChiefRay is that of Hy = +1, the
        // field scale (largest |y|, §7), so compare the field point's own chief ray.
        var heightField = new FieldPoint { Y = h };
        var byHeight = ParaxialSystem.Create(lens, Sys(null, FieldType.ObjectHeight, new FieldPoint(), heightField));
        var heightRay = byHeight.TraceChiefRay(heightField).Y;
        SameRay(reference, heightRay, 1e-14);
        Close(h, heightRay.Heights[0], 1e-14);

        // A paraxial image height is the height that chief ray reaches on the image surface.
        double hImage = byAngle.ImageSurfaceChiefHeight;
        var byImage = ParaxialSystem.Create(lens, Sys(null, FieldType.ParaxialImageHeight, new FieldPoint(), new FieldPoint { Y = hImage }));
        SameRay(reference, byImage.ChiefRay, 1e-14);

        // A theodolite field with zero azimuth is the angle field of the same elevation.
        var byTheodolite = ParaxialSystem.Create(lens, Sys(null, FieldType.TheodoliteAngle, new FieldPoint(), new FieldPoint { Y = angle }));
        SameRay(reference, byTheodolite.ChiefRay, 1e-15);

        // Off the meridian: theodolite (az, el) has direction (cos el sin az, sin el, cos el cos az),
        // slopes tan az and tan el / cos az, which is the angle field (az, atan(tan el / cos az)).
        double az = 3, el = 4;
        double thetaY = Math.Atan(Math.Tan(el * Math.PI / 180) / Math.Cos(az * Math.PI / 180)) * (180 / Math.PI);
        var theodolite = new FieldPoint { X = az, Y = el };
        var asAngle = new FieldPoint { X = az, Y = thetaY };
        var (tx, ty) = ParaxialSystem.Create(lens, Sys(null, FieldType.TheodoliteAngle, theodolite)).TraceChiefRay(theodolite);
        var (ax, ay) = ParaxialSystem.Create(lens, Sys(null, FieldType.Angle, asAngle)).TraceChiefRay(asAngle);
        SameRay(ax, tx, 1e-14);
        SameRay(ay, ty, 1e-14);

        // A real image height needs the real trace and ray aiming (OP-20, OP-22).
        Assert.Throws<NotSupportedException>(() => ParaxialSystem.Create(lens,
            Sys(null, FieldType.RealImageHeight, new FieldPoint(), new FieldPoint { Y = 0.001 })));
    }

    [Fact]
    public void ObjectHeightFields_AreRefusedForAnObjectAtInfinity()
    {
        var lens = StopBehind(double.PositiveInfinity);
        var e = Assert.Throws<InvalidOperationException>(() => ParaxialSystem.Create(lens,
            Sys(null, FieldType.ObjectHeight, new FieldPoint(), new FieldPoint { Y = 0.01 })));
        Assert.Contains("finite distance", e.Message);
    }

    // ---- Pupils ----

    [Fact]
    public void ExitPupil_IsTheStopImagedThroughTheSurfacesAfterIt()
    {
        // Stop d in front of a thin lens of focal length f: the lens images it to
        // s′ = 1/(1/f − 1/d) behind the lens, magnified by s′/(−d) (Gauss, as above).
        double f = 0.1, d = 0.04, stopRadius = 0.004;
        var lens = Lens(double.PositiveInfinity, new[] { S(0, d, stop: true, semiDiameter: stopRadius) }.Concat(ThinLens(f, 0.08)).ToArray());
        var p = ParaxialSystem.Create(lens, Sys(new Aperture { Type = ApertureType.FloatByStopSize }));
        double sPrime = 1 / (1 / f + 1 / -d);
        Close(sPrime, p.ExitPupilPosition!.Value, 1e-14);
        Close(2 * stopRadius * Math.Abs(sPrime / -d), p.ExitPupilDiameter!.Value, 1e-14);
        Close(2 * stopRadius, p.EntrancePupilDiameter!.Value, 1e-15);   // nothing before the stop
    }

    // ---- Wavelengths and materials ----

    [Fact]
    public void EachWavelength_UsesTheMaterialsIndexAtThatWavelength()
    {
        // A synthetic tabulated material (test data, not a real glass).
        var glass = new OpticalMaterial
        {
            Name = "TEST", Tabulated = new TabulatedIndex
            {
                Points = new[] { new TabulatedIndexPoint(0.4, 1.53, 0), new TabulatedIndexPoint(0.7, 1.51, 0) }
            }
        };
        double r1 = 0.1, r2 = -0.1;
        var lens = Lens(double.PositiveInfinity,
            new ParaxialSurface { Curvature = 1 / r1, Medium = ParaxialMedium.Of(glass), IsStop = true },
            S(r2, 0.1));
        var system = Sys() with
        {
            Wavelengths = new WavelengthSet
            {
                Wavelengths = new[] { new Wavelength { Micrometers = 0.45 }, new Wavelength { Micrometers = 0.55 }, new Wavelength { Micrometers = 0.65 } },
                PrimaryIndex = 1,
            }
        };
        var all = ParaxialSystem.ForEachWavelength(lens, system);
        Assert.Equal(3, all.Count);
        foreach (var p in all)
        {
            // Thin lens: φ = (n(λ) − 1)(c1 − c2), n(λ) from the material itself.
            double n = glass.RelativeIndex(p.WavelengthMicrometers);
            Close((n - 1) * (1 / r1 - 1 / r2), p.Power, 1e-15);
        }
        Assert.Equal(0.55, ParaxialSystem.Create(lens, system).WavelengthMicrometers);   // primary by default
        Assert.True(all[0].EffectiveFocalLength < all[2].EffectiveFocalLength);         // higher index in the blue
    }

    // ---- Validation ----

    [Fact]
    public void ALensWithoutExactlyOneStop_IsRefusedWithTheReason()
    {
        var none = Lens(double.PositiveInfinity, S(0.1, 0.005, 1.5), S(-0.1, 0.1));
        Assert.Contains("no surface is the stop", Assert.Throws<ArgumentException>(() => ParaxialSystem.Create(none, Sys())).Message);
        var two = Lens(double.PositiveInfinity, S(0.1, 0.005, 1.5, stop: true), S(-0.1, 0.1, stop: true));
        Assert.Contains("2 surfaces", Assert.Throws<ArgumentException>(() => ParaxialSystem.Create(two, Sys())).Message);
        var badIndex = Lens(double.PositiveInfinity, S(0.1, 0.005, -1.5, stop: true), S(-0.1, 0.1));
        Assert.Contains("Surface 1", Assert.Throws<ArgumentException>(() => ParaxialSystem.Create(badIndex, Sys())).Message);
    }

    // ---- The report in the analysis framework ----

    [Fact]
    public void TheParaxialAnalysis_IsRegistered_AndSaysSoWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(ParaxialDataAnalysis.SavedId, out var analysis));
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        var result = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
        Assert.Equal(new[] { "No surfaces" }, result.Sections.Select(s => s.Heading));
    }

    [Fact]
    public void TheParaxialAnalysis_ListsSystemDataCardinalPointsAndRayData()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.08, 0.05));
        var analysis = new ParaxialDataAnalysis(_ => lens);
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        var result = analysis.Compute(context, new ParaxialDataSettings(), default);
        Assert.Equal(new[] { "System data", "ABCD matrix", "Cardinal points", "Paraxial ray data" }, result.Sections.Select(s => s.Heading));
        Assert.Contains("Effective focal length", result.Text);
        Assert.Contains(result.Assumptions, a => a.Contains("Ciddor", StringComparison.OrdinalIgnoreCase) || a.Contains("air", StringComparison.OrdinalIgnoreCase));
        var rays = result.Sections.Single(s => s.Heading == "Paraxial ray data");
        Assert.Equal(lens.Surfaces.Count, rays.TableRows.Count);

        var brief = analysis.Compute(context, new ParaxialDataSettings { ShowCardinalPoints = false, ShowRayData = false }, default);
        Assert.Equal(new[] { "System data", "ABCD matrix" }, brief.Sections.Select(s => s.Heading));
        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new ParaxialDataSettings { Wavelength = 2 }, default));
    }
}
