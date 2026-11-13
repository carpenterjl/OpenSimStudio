using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics;

namespace OpenSim.Tests.Optics;

/// <summary>OP-2: every conversion in docs/Optics-Conventions.md, and the ray type.</summary>
public sealed class OpticsConventionsTests
{
    private static void AssertClose(Vector3D expected, Vector3D actual, double tolerance)
    {
        Assert.True(Vector3D.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual} (off by {Vector3D.Distance(expected, actual):g3})");
    }

    private static void AssertSame(RigidTransform expected, RigidTransform actual, double tolerance)
    {
        AssertClose(expected.Col0, actual.Col0, tolerance);
        AssertClose(expected.Col1, actual.Col1, tolerance);
        AssertClose(expected.Col2, actual.Col2, tolerance);
        AssertClose(expected.Translation, actual.Translation, tolerance);
    }

    // ---- Tilts and decenters ----

    /// <summary>
    /// Decenter then tilt against the written-out matrix R = Rx·Ry·Rz that OpticStudio reports
    /// as a surface's global rotation (docs/Optics-Conventions.md §4 lists the sources):
    /// R11 = cy·cz, R12 = −cy·sz, R13 = sy, R21 = cx·sz + sx·sy·cz, R22 = cx·cz − sx·sy·sz,
    /// R23 = −sx·cy, R31 = sx·sz − cx·sy·cz, R32 = sx·cz + cx·sy·sz, R33 = cx·cy.
    /// </summary>
    [Fact]
    public void DecenterThenTilt_IsTheWrittenOutMatrix()
    {
        const double ax = 17.0, ay = -34.0, az = 61.0, rad = Math.PI / 180.0;
        double sx = Math.Sin(ax * rad), cx = Math.Cos(ax * rad);
        double sy = Math.Sin(ay * rad), cy = Math.Cos(ay * rad);
        double sz = Math.Sin(az * rad), cz = Math.Cos(az * rad);

        var t = new TiltDecenter(0.002, -0.005, ax, ay, az).ToTransform();

        // Columns of R are the new axes in old coordinates.
        AssertClose(new Vector3D(cy * cz, cx * sz + sx * sy * cz, sx * sz - cx * sy * cz), t.Col0, 1e-15);
        AssertClose(new Vector3D(-cy * sz, cx * cz - sx * sy * sz, sx * cz + cx * sy * sz), t.Col1, 1e-15);
        AssertClose(new Vector3D(sy, -sx * cy, cx * cy), t.Col2, 1e-15);
        Assert.Equal(new Vector3D(0.002, -0.005, 0), t.Translation);    // the decenter is not rotated
    }

    [Fact]
    public void EachTilt_IsAboutTheAxisTheEarlierTiltsLeft()
    {
        // Quarter turns are exact. Decenter then tilt: x first, then the new y.
        // After +90° about x the new y is the old +z; +90° about that carries the new z
        // (which the first tilt left at old −y) to the old +x.
        var xy = new TiltDecenter(0, 0, 90, 90, 0).ToTransform();
        Assert.Equal(Vector3D.UnitZ, xy.Col1);
        Assert.Equal(Vector3D.UnitX, xy.Col2);

        // Tilt then decenter runs the other way: z first, then the new y, then the new x.
        // After +90° about z the new y is the old −x; +90° about it carries the new z to old +y.
        var zy = new TiltDecenter(0, 0, 0, 90, 90, TiltDecenterOrder.TiltThenDecenter).ToTransform();
        Assert.Equal(-Vector3D.UnitX, zy.Col1);
        Assert.Equal(Vector3D.UnitY, zy.Col2);
    }

    [Fact]
    public void TheTwoOrders_PlaceTheSameTiltDifferently()
    {
        // One tilt, so both orders turn the frame the same way; only the decenter differs.
        var first = new TiltDecenter(0, 0.001, 30, 0, 0, TiltDecenterOrder.DecenterThenTilt).ToTransform();
        var last = new TiltDecenter(0, 0.001, 30, 0, 0, TiltDecenterOrder.TiltThenDecenter).ToTransform();

        AssertSame(first with { Translation = Vector3D.Zero }, last with { Translation = Vector3D.Zero }, 0);
        // Decenter first: along the old y. Tilt first: along the tilted y, (0, cos 30°, sin 30°).
        Assert.Equal(new Vector3D(0, 0.001, 0), first.Translation);
        AssertClose(new Vector3D(0, 0.001 * Math.Sqrt(3) / 2, 0.0005), last.Translation, 1e-18);

        // With three tilts the rotations differ too: Rx·Ry·Rz against Rz·Ry·Rx.
        var a = new TiltDecenter(0, 0, 20, 30, 40, TiltDecenterOrder.DecenterThenTilt).ToTransform();
        var b = new TiltDecenter(0, 0, 20, 30, 40, TiltDecenterOrder.TiltThenDecenter).ToTransform();
        Assert.True(Vector3D.Distance(a.Col0, b.Col0) > 0.1);
    }

    [Theory]
    [InlineData(TiltDecenterOrder.DecenterThenTilt)]
    [InlineData(TiltDecenterOrder.TiltThenDecenter)]
    public void TheReversedBreak_UndoesIt(TiltDecenterOrder order)
    {
        var breakIn = new TiltDecenter(0.003, -0.011, 12.5, -48.0, 107.0, order);
        var both = RigidTransform.Compose(breakIn.ToTransform(), breakIn.Reversed().ToTransform());
        AssertSame(RigidTransform.Identity, both, 1e-15);
        Assert.Equal(breakIn, breakIn.Reversed().Reversed());
    }

    [Theory]
    [InlineData(TiltDecenterOrder.DecenterThenTilt, 12.5, -48.0, 107.0)]
    [InlineData(TiltDecenterOrder.TiltThenDecenter, 12.5, -48.0, 107.0)]
    [InlineData(TiltDecenterOrder.DecenterThenTilt, -170.0, 89.0, -3.0)]
    [InlineData(TiltDecenterOrder.TiltThenDecenter, 45.0, 0.0, 0.0)]
    public void TiltsReadBackFromATransform_AreTheOnesPutIn(TiltDecenterOrder order, double ax, double ay, double az)
    {
        var original = new TiltDecenter(0.003, -0.011, ax, ay, az, order);
        var back = TiltDecenter.FromTransform(original.ToTransform(), order, out double axial);

        Assert.Equal(original.DecenterX, back.DecenterX, 15);
        Assert.Equal(original.DecenterY, back.DecenterY, 15);
        Assert.Equal(ax, back.TiltXDegrees, 11);
        Assert.Equal(ay, back.TiltYDegrees, 11);
        Assert.Equal(az, back.TiltZDegrees, 11);
        Assert.Equal(0.0, axial, 15);
        Assert.Equal(order, back.Order);
    }

    [Theory]
    [InlineData(TiltDecenterOrder.DecenterThenTilt)]
    [InlineData(TiltDecenterOrder.TiltThenDecenter)]
    public void ATransformWithAnAxialShift_ReadsBackInEitherOrder(TiltDecenterOrder order)
    {
        // A transform no single break holds: the z part comes back as an axial shift, and the
        // break plus that shift rebuild the transform.
        var target = RigidTransform.Compose(
            RigidTransform.FromTranslation(new Vector3D(0.01, -0.02, 0.3)),
            new TiltDecenter(0, 0, 25, -40, 70).ToTransform());

        var found = TiltDecenter.FromTransform(target, order, out double axial);
        var shift = RigidTransform.FromTranslation(new Vector3D(0, 0, axial));
        var rebuilt = order == TiltDecenterOrder.DecenterThenTilt
            ? RigidTransform.Compose(shift, found.ToTransform())      // shift along the old z
            : RigidTransform.Compose(found.ToTransform(), shift);     // shift along the new z
        AssertSame(target, rebuilt, 1e-14);
    }

    [Fact]
    public void AYTiltOfNinetyDegrees_StillReadsBackToTheSameTransform()
    {
        var target = new TiltDecenter(0, 0, 35, 90, 20).ToTransform();
        var found = TiltDecenter.FromTransform(target, TiltDecenterOrder.DecenterThenTilt, out _);
        Assert.Equal(0.0, found.TiltZDegrees);
        AssertSame(target, found.ToTransform(), 1e-14);
    }

    [Fact]
    public void QuarterTurns_AreExact()
    {
        Assert.Equal((0.0, 1.0), OpticsConventions.SinCosDegrees(0));
        Assert.Equal((1.0, 0.0), OpticsConventions.SinCosDegrees(90));
        Assert.Equal((0.0, -1.0), OpticsConventions.SinCosDegrees(180));
        Assert.Equal((-1.0, 0.0), OpticsConventions.SinCosDegrees(-90));
        Assert.Equal((-1.0, 0.0), OpticsConventions.SinCosDegrees(270));
        Assert.Equal((0.0, 1.0), OpticsConventions.SinCosDegrees(-720));

        var (s, c) = OpticsConventions.SinCosDegrees(30);
        Assert.Equal(0.5, s, 15);
        Assert.Equal(Math.Sqrt(3) / 2, c, 15);

        Assert.True(new TiltDecenter(0, 0, 0, 0, 0).ToTransform().IsIdentity);
        Assert.Equal(0.0, new TiltDecenter(0, 0, 90, 180, 270).ToTransform().OrthonormalityError);
    }

    // ---- Mirrors ----

    [Fact]
    public void AMirror_FlipsTheRayAndTheThicknessSign()
    {
        Assert.Equal(1, OpticsConventions.ThicknessSignAfter(0));
        Assert.Equal(-1, OpticsConventions.ThicknessSignAfter(1));
        Assert.Equal(1, OpticsConventions.ThicknessSignAfter(2));
        Assert.Equal(-1, OpticsConventions.ThicknessSignAfter(3));

        // A ray along +z off a flat mirror normal to z travels along −z; the sense of the
        // normal does not matter.
        Assert.Equal(-Vector3D.UnitZ, OpticsConventions.Reflect(Vector3D.UnitZ, Vector3D.UnitZ));
        Assert.Equal(-Vector3D.UnitZ, OpticsConventions.Reflect(Vector3D.UnitZ, -Vector3D.UnitZ));

        // A 45° fold turns +z into −y (normal in the y-z plane), and keeps unit length.
        var normal = new Vector3D(0, 1, 1).Normalized();
        AssertClose(-Vector3D.UnitY, OpticsConventions.Reflect(Vector3D.UnitZ, normal), 1e-15);

        // The transverse part is kept, the normal part is reversed.
        var d = new Vector3D(0.36, 0.48, 0.8);
        Assert.Equal(new Vector3D(0.36, 0.48, -0.8), OpticsConventions.Reflect(d, Vector3D.UnitZ));

        // After the mirror the next vertex lies at a negative thickness, in the direction the
        // ray now travels, and the optical path still grows.
        var ray = new Ray(Vector3D.Zero, Vector3D.UnitZ, 550e-9).Advanced(0.1, 1.5);
        var reflected = ray with { Direction = OpticsConventions.Reflect(ray.Direction, Vector3D.UnitZ) };
        double thickness = OpticsConventions.ThicknessSignAfter(1) * 0.04;
        var atNext = reflected.Advanced((thickness - 0.0) / reflected.Direction.Z, 1.5);
        Assert.Equal(0.1 + thickness, atNext.Position.Z, 15);
        Assert.Equal(1.5 * 0.14, atNext.OpticalPath, 15);
    }

    // ---- Radius, units, wavelength, OPD ----

    [Fact]
    public void RadiusAndCurvature_ConvertBothWays_AndAPlaneIsZeroCurvature()
    {
        Assert.Equal(20.0, OpticsConventions.CurvatureFromRadius(0.05));
        Assert.Equal(-20.0, OpticsConventions.CurvatureFromRadius(-0.05));
        Assert.Equal(0.0, OpticsConventions.CurvatureFromRadius(double.PositiveInfinity));
        Assert.Equal(0.0, OpticsConventions.CurvatureFromRadius(0.0));
        Assert.Equal(double.PositiveInfinity, OpticsConventions.RadiusFromCurvature(0.0));
        Assert.Equal(-0.05, OpticsConventions.RadiusFromCurvature(OpticsConventions.CurvatureFromRadius(-0.05)));
    }

    [Fact]
    public void LensUnits_AndWavelengths_ConvertToMeters()
    {
        Assert.Equal(4, Enum.GetValues<LensUnit>().Length);
        Assert.Equal(1e-3, OpticsConventions.MetersPer(LensUnit.Millimeter));
        Assert.Equal(1e-2, OpticsConventions.MetersPer(LensUnit.Centimeter));
        Assert.Equal(0.0254, OpticsConventions.MetersPer(LensUnit.Inch));       // exact by definition
        Assert.Equal(1.0, OpticsConventions.MetersPer(LensUnit.Meter));

        foreach (var unit in Enum.GetValues<LensUnit>())
            Assert.Equal(12.5, OpticsConventions.FromMeters(OpticsConventions.ToMeters(12.5, unit), unit), 13);
        Assert.Equal(0.0508, OpticsConventions.ToMeters(2, LensUnit.Inch));

        Assert.Equal(5.5e-7, OpticsConventions.MicrometersToMeters(0.55), 1e-21);
        Assert.Equal(0.55, OpticsConventions.MetersToMicrometers(OpticsConventions.MicrometersToMeters(0.55)), 15);
    }

    [Fact]
    public void Opd_IsChiefMinusRay_AndPositiveWhenTheRayIsAhead()
    {
        // The ray reaches the reference sphere over a shorter path: it is ahead.
        double opd = OpticsConventions.OpticalPathDifference(chiefPath: 0.25, rayPath: 0.25 - 275e-9);
        Assert.True(opd > 0);
        Assert.Equal(0.5, OpticsConventions.InWaves(opd, 550e-9), 6);
        Assert.Equal(0.0, OpticsConventions.OpticalPathDifference(0.25, 0.25));
    }

    // ---- Field normalisation ----

    [Fact]
    public void RadialNormalisation_UsesTheFarthestFieldPoint()
    {
        // The manual's own example: field points (10, 0) and (−10, −3) give Fr = √(10² + 3²).
        (double, double)[] fields = [(10, 0), (-10, -3)];
        var scale = OpticsConventions.FieldScale(fields, FieldNormalization.Radial);
        Assert.Equal(Math.Sqrt(109), scale.X, 14);
        Assert.Equal(scale.X, scale.Y);

        var h = OpticsConventions.NormalizeField((-10, -3), scale);
        Assert.Equal(1.0, h.Hx * h.Hx + h.Hy * h.Hy, 14);
        var back = OpticsConventions.DenormalizeField(h, scale);
        Assert.Equal(-10.0, back.X, 13);
        Assert.Equal(-3.0, back.Y, 13);
    }

    [Fact]
    public void RectangularNormalisation_UsesTheLargestXAndY()
    {
        (double, double)[] fields = [(10, 0), (-10, -3)];
        var scale = OpticsConventions.FieldScale(fields, FieldNormalization.Rectangular);
        Assert.Equal((10.0, 3.0), scale);
        Assert.Equal((-1.0, -1.0), OpticsConventions.NormalizeField((-10, -3), scale));
        Assert.Equal((5.0, 1.5), OpticsConventions.DenormalizeField((0.5, 0.5), scale));

        // Fields along one axis only: the other axis has no scale and normalises to zero.
        var onAxis = OpticsConventions.FieldScale([(0, 0), (0, 7)], FieldNormalization.Rectangular);
        Assert.Equal((0.0, 1.0), OpticsConventions.NormalizeField((0, 7), onAxis));
    }

    // ---- The ray ----

    [Fact]
    public void ANewRay_IsUnitIntensity_OnAUnitDirection()
    {
        var ray = new Ray(new Vector3D(1, 2, 3), new Vector3D(0, 3, 4), 632.8e-9);
        Assert.Equal(new Vector3D(0, 0.6, 0.8), ray.Direction);
        Assert.Equal(1.0, ray.Intensity);
        Assert.Equal(0.0, ray.OpticalPath);
        Assert.Equal(RayStatus.Ok, ray.Status);
        Assert.True(ray.IsOk);
        Assert.Equal(Ray.NoSurface, ray.FailedAtSurface);
        Assert.Null(ray.Field);

        // An exact axis direction is kept bit for bit.
        Assert.Equal(-Vector3D.UnitZ, new Ray(Vector3D.Zero, -Vector3D.UnitZ, 1e-6).Direction);

        Assert.Throws<ArgumentException>(() => new Ray(Vector3D.Zero, Vector3D.Zero, 1e-6));
        Assert.Throws<ArgumentException>(() => new Ray(Vector3D.Zero, new Vector3D(double.NaN, 0, 1), 1e-6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ray(Vector3D.Zero, Vector3D.UnitZ, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ray(Vector3D.Zero, Vector3D.UnitZ, double.NaN));
    }

    [Fact]
    public void AdvancingARay_AddsIndexTimesDistance()
    {
        var ray = new Ray(Vector3D.Zero, new Vector3D(0, 0.6, 0.8), 550e-9)
            .Advanced(0.5, 1.0)
            .Advanced(0.25, 1.5);
        AssertClose(new Vector3D(0, 0.45, 0.6), ray.Position, 1e-15);
        Assert.Equal(0.5 + 0.375, ray.OpticalPath, 15);

        // A virtual step back takes the path back with it.
        Assert.Equal(0.5, ray.Advanced(-0.25, 1.5).OpticalPath, 15);
        Assert.Throws<ArgumentOutOfRangeException>(() => ray.Advanced(0.1, 0));
    }

    [Fact]
    public void AFailedRay_KeepsWhereAndWhy()
    {
        var ray = new Ray(Vector3D.Zero, Vector3D.UnitZ, 550e-9).Advanced(0.1, 1.0);
        var failed = ray.Failed(RayStatus.TotalInternalReflection, 4);
        Assert.False(failed.IsOk);
        Assert.Equal(RayStatus.TotalInternalReflection, failed.Status);
        Assert.Equal(4, failed.FailedAtSurface);
        Assert.Equal(ray.Position, failed.Position);
        Assert.Equal(ray.OpticalPath, failed.OpticalPath);
        Assert.Throws<ArgumentException>(() => ray.Failed(RayStatus.Ok, 4));
    }

    [Fact]
    public void EveryRayStatus_HasALabel()
    {
        Assert.Equal(0, (int)RayStatus.Ok);     // default(RayStatus) must mean "not failed"
        Assert.Equal(Enum.GetValues<RayStatus>(), RayStatuses.All);
        foreach (var status in RayStatuses.All)
            Assert.False(string.IsNullOrWhiteSpace(RayStatuses.Label(status)));
        Assert.Equal(RayStatuses.All.Count, RayStatuses.All.Select(RayStatuses.Label).Distinct().Count());
        foreach (var needed in new[] { RayStatus.MissedSurface, RayStatus.TotalInternalReflection,
                     RayStatus.Vignetted, RayStatus.NoConvergence, RayStatus.EvanescentOrder })
            Assert.Contains(needed, RayStatuses.All);
    }

    [Fact]
    public void ARay_GoesToTheGlobalFrameAndBack()
    {
        var frame = RigidTransform.Compose(
            RigidTransform.FromTranslation(new Vector3D(0.01, -0.02, 0.3)),
            new TiltDecenter(0.004, 0.001, 25, -40, 70).ToTransform());
        var field = new RayField(new Complex(0.8, 0.1), new Complex(-0.6, 0.2), Complex.Zero);
        var local = new Ray(new Vector3D(0.002, -0.003, 0), Vector3D.UnitZ, 550e-9) with { Field = field };

        var global = local.ToParent(frame);
        Assert.Equal(1.0, global.Direction.Length, 15);
        Assert.Equal(local.OpticalPath, global.OpticalPath);
        Assert.Equal(field.NormSquared, global.Field!.Value.NormSquared, 15);
        // The field was transverse to the ray, and stays so in the other frame.
        Assert.True(global.Field.Value.Along(global.Direction).Magnitude <= 1e-15);

        var back = global.ToLocal(frame);
        AssertClose(local.Position, back.Position, 1e-14);
        AssertClose(local.Direction, back.Direction, 1e-14);
        Assert.True((back.Field!.Value.X - field.X).Magnitude <= 1e-14);
        Assert.True((back.Field.Value.Y - field.Y).Magnitude <= 1e-14);
        Assert.True(back.Field.Value.Z.Magnitude <= 1e-14);

        Assert.Equal(local, local.ToParent(RigidTransform.Identity));
    }
}
