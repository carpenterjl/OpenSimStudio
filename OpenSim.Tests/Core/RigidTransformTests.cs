using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>OP-2: the general rigid transform in Core (moved out of the STEP reader).</summary>
public sealed class RigidTransformTests
{
    private static void AssertClose(Vector3D expected, Vector3D actual, double tolerance)
    {
        Assert.True(Vector3D.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual} (off by {Vector3D.Distance(expected, actual):g3})");
    }

    /// <summary>A general rotation and translation, from three unequal angles.</summary>
    private static RigidTransform General() =>
        RigidTransform.Compose(
            RigidTransform.FromTranslation(new Vector3D(0.3, -1.7, 2.9)),
            RigidTransform.Compose(RigidTransform.RotationAboutX(0.71),
                RigidTransform.Compose(RigidTransform.RotationAboutY(-1.13), RigidTransform.RotationAboutZ(2.57))));

    [Fact]
    public void AxisRotations_AreRightHanded()
    {
        // A quarter turn, from exact sine and cosine: x → y → z → x in cyclic order.
        Assert.Equal(Vector3D.UnitZ, RigidTransform.RotationAboutX(1, 0).ApplyDirection(Vector3D.UnitY));
        Assert.Equal(Vector3D.UnitX, RigidTransform.RotationAboutY(1, 0).ApplyDirection(Vector3D.UnitZ));
        Assert.Equal(Vector3D.UnitY, RigidTransform.RotationAboutZ(1, 0).ApplyDirection(Vector3D.UnitX));

        // And each leaves its own axis alone, at any angle.
        Assert.Equal(Vector3D.UnitX, RigidTransform.RotationAboutX(0.4).ApplyDirection(Vector3D.UnitX));
        Assert.Equal(Vector3D.UnitY, RigidTransform.RotationAboutY(0.4).ApplyDirection(Vector3D.UnitY));
        Assert.Equal(Vector3D.UnitZ, RigidTransform.RotationAboutZ(0.4).ApplyDirection(Vector3D.UnitZ));
    }

    [Fact]
    public void ASmallRotation_MovesAPointByAxisCrossPoint()
    {
        // To first order R·p − p = θ·(axis × p); the remainder is second order in θ.
        const double theta = 1e-6;
        var p = new Vector3D(0.3, -0.8, 0.5);
        (RigidTransform R, Vector3D Axis)[] cases =
        [
            (RigidTransform.RotationAboutX(theta), Vector3D.UnitX),
            (RigidTransform.RotationAboutY(theta), Vector3D.UnitY),
            (RigidTransform.RotationAboutZ(theta), Vector3D.UnitZ),
        ];
        foreach (var (r, axis) in cases)
            AssertClose(Vector3D.Cross(axis, p) * theta, r.ApplyDirection(p) - p, theta * theta);
    }

    [Fact]
    public void AGeneralTransform_IsARotation()
    {
        var t = General();
        Assert.True(t.OrthonormalityError <= 4e-16, $"RᵀR − I = {t.OrthonormalityError:g3}");
        Assert.Equal(1.0, t.Determinant, 15);
        Assert.False(t.IsIdentity);
        Assert.True(RigidTransform.Identity.IsIdentity);
    }

    [Fact]
    public void LocalToParentAndBack_ReturnsThePoint()
    {
        var t = General();
        var inverse = t.Inverse();
        var p = new Vector3D(-0.42, 0.057, 1.3);
        var d = new Vector3D(0.6, 0.0, 0.8);

        AssertClose(p, t.InverseApply(t.Apply(p)), 1e-14);
        AssertClose(p, inverse.Apply(t.Apply(p)), 1e-14);
        AssertClose(p, t.Apply(inverse.Apply(p)), 1e-14);
        AssertClose(d, t.InverseApplyDirection(t.ApplyDirection(d)), 1e-14);
        Assert.Equal(1.0, t.ApplyDirection(d).Length, 15);

        var roundTrip = RigidTransform.Compose(inverse, t);
        Assert.True(roundTrip.OrthonormalityError <= 1e-15);
        AssertClose(Vector3D.UnitX, roundTrip.Col0, 1e-15);
        AssertClose(Vector3D.UnitY, roundTrip.Col1, 1e-15);
        AssertClose(Vector3D.UnitZ, roundTrip.Col2, 1e-15);
        AssertClose(Vector3D.Zero, roundTrip.Translation, 1e-15);
    }

    [Fact]
    public void Compose_AppliesTheInnerTransformFirst()
    {
        var inner = RigidTransform.FromTranslation(new Vector3D(1, 0, 0));
        var outer = RigidTransform.RotationAboutZ(1, 0);
        var p = new Vector3D(2, 3, 4);

        // Exact: a quarter turn and whole-number offsets.
        Assert.Equal(outer.Apply(inner.Apply(p)), RigidTransform.Compose(outer, inner).Apply(p));
        Assert.Equal(new Vector3D(-3, 3, 4), RigidTransform.Compose(outer, inner).Apply(p));
        Assert.Equal(new Vector3D(-2, 2, 4), RigidTransform.Compose(inner, outer).Apply(p));

        Assert.Same(inner, RigidTransform.Compose(RigidTransform.Identity, inner));
        Assert.Same(inner, RigidTransform.Compose(inner, RigidTransform.Identity));
        Assert.Same(RigidTransform.Identity, RigidTransform.Identity.Inverse());
    }

    [Fact]
    public void FromColumns_RefusesAMatrixThatIsNotARotation()
    {
        var t = General();
        Assert.Equal(t, RigidTransform.FromColumns(t.Col0, t.Col1, t.Col2, t.Translation));

        // Mirrored (det −1), scaled, and sheared.
        Assert.Throws<ArgumentException>(() =>
            RigidTransform.FromColumns(Vector3D.UnitX, Vector3D.UnitY, -Vector3D.UnitZ, Vector3D.Zero));
        Assert.Throws<ArgumentException>(() =>
            RigidTransform.FromColumns(Vector3D.UnitX * 2, Vector3D.UnitY, Vector3D.UnitZ * 0.5, Vector3D.Zero));
        Assert.Throws<ArgumentException>(() =>
            RigidTransform.FromColumns(Vector3D.UnitX, new Vector3D(0.1, 1, 0), Vector3D.UnitZ, Vector3D.Zero));
        Assert.Throws<ArgumentException>(() =>
            RigidTransform.FromColumns(new Vector3D(double.NaN, 0, 0), Vector3D.UnitY, Vector3D.UnitZ, Vector3D.Zero));
    }
}
