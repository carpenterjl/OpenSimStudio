namespace OpenSim.Core.Numerics;

/// <summary>
/// A rigid motion: a rotation (stored as the three columns of R) followed by a translation.
/// A point p in the local frame sits at R·p + t in the parent frame, so the columns of R are
/// the local axes written in parent coordinates and t is the local origin.
/// <para>
/// The record does not check itself: build it with <see cref="FromColumns"/> when the columns
/// come from data, or from the factories here, which are rotations by construction.
/// Axis-aligned rotations have exact 0/±1 entries, so they map points with exact arithmetic.
/// </para>
/// </summary>
public sealed record RigidTransform(Vector3D Col0, Vector3D Col1, Vector3D Col2, Vector3D Translation)
{
    /// <summary>The transform that changes nothing.</summary>
    public static RigidTransform Identity { get; } =
        new(Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ, Vector3D.Zero);

    /// <summary>True when this is exactly the identity — callers skip the work entirely.</summary>
    public bool IsIdentity =>
        Col0 == Vector3D.UnitX && Col1 == Vector3D.UnitY && Col2 == Vector3D.UnitZ
        && Translation == Vector3D.Zero;

    /// <summary>det R; +1 for every rotation.</summary>
    public double Determinant => Vector3D.Dot(Col0, Vector3D.Cross(Col1, Col2));

    /// <summary>The largest entry of RᵀR − I in magnitude; zero for an exact rotation.</summary>
    public double OrthonormalityError
    {
        get
        {
            double worst = 0;
            Vector3D[] c = [Col0, Col1, Col2];
            for (int i = 0; i < 3; i++)
                for (int j = i; j < 3; j++)
                    worst = Math.Max(worst, Math.Abs(Vector3D.Dot(c[i], c[j]) - (i == j ? 1.0 : 0.0)));
            return worst;
        }
    }

    /// <summary>A pure translation.</summary>
    public static RigidTransform FromTranslation(Vector3D translation) =>
        new(Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ, translation);

    /// <summary>A transform from rotation columns that came from data. Throws when they are
    /// not orthonormal and right-handed to <paramref name="tolerance"/>, so a scaled or
    /// mirrored matrix is a named failure and never a quietly distorted body.</summary>
    public static RigidTransform FromColumns(Vector3D col0, Vector3D col1, Vector3D col2,
        Vector3D translation, double tolerance = 1e-9)
    {
        var t = new RigidTransform(col0, col1, col2, translation);
        double error = t.OrthonormalityError, det = t.Determinant;
        if (!(error <= tolerance) || !(Math.Abs(det - 1.0) <= tolerance))
            throw new ArgumentException(
                $"The matrix is not a rotation (det R = {det:g6}, RᵀR differs from I by {error:g3}).");
        return t;
    }

    /// <summary>Right-handed rotation about the x axis: +y turns toward +z.</summary>
    public static RigidTransform RotationAboutX(double radians) =>
        RotationAboutX(Math.Sin(radians), Math.Cos(radians));

    /// <summary>Right-handed rotation about the y axis: +z turns toward +x.</summary>
    public static RigidTransform RotationAboutY(double radians) =>
        RotationAboutY(Math.Sin(radians), Math.Cos(radians));

    /// <summary>Right-handed rotation about the z axis: +x turns toward +y.</summary>
    public static RigidTransform RotationAboutZ(double radians) =>
        RotationAboutZ(Math.Sin(radians), Math.Cos(radians));

    /// <summary>Rotation about x from the angle's sine and cosine, for callers that hold exact
    /// values (a quarter turn is exactly 0 and ±1, which <see cref="Math.Cos"/> does not return).</summary>
    public static RigidTransform RotationAboutX(double sin, double cos) =>
        new(Vector3D.UnitX, new Vector3D(0, cos, sin), new Vector3D(0, -sin, cos), Vector3D.Zero);

    /// <summary>Rotation about y from the angle's sine and cosine.</summary>
    public static RigidTransform RotationAboutY(double sin, double cos) =>
        new(new Vector3D(cos, 0, -sin), Vector3D.UnitY, new Vector3D(sin, 0, cos), Vector3D.Zero);

    /// <summary>Rotation about z from the angle's sine and cosine.</summary>
    public static RigidTransform RotationAboutZ(double sin, double cos) =>
        new(new Vector3D(cos, sin, 0), new Vector3D(-sin, cos, 0), Vector3D.UnitZ, Vector3D.Zero);

    /// <summary>Rotates a direction (no translation).</summary>
    public Vector3D ApplyDirection(Vector3D v) => Col0 * v.X + Col1 * v.Y + Col2 * v.Z;

    /// <summary>Maps a point.</summary>
    public Vector3D Apply(Vector3D p) => ApplyDirection(p) + Translation;

    /// <summary>Rotates a direction back: Rᵀ·v.</summary>
    public Vector3D InverseApplyDirection(Vector3D v) =>
        new(Vector3D.Dot(Col0, v), Vector3D.Dot(Col1, v), Vector3D.Dot(Col2, v));

    /// <summary>Maps a point back: Rᵀ·(p − t).</summary>
    public Vector3D InverseApply(Vector3D p) => InverseApplyDirection(p - Translation);

    /// <summary>The inverse motion: rotation Rᵀ, translation −Rᵀ·t.</summary>
    public RigidTransform Inverse()
    {
        if (IsIdentity) return this;
        return new RigidTransform(
            new Vector3D(Col0.X, Col1.X, Col2.X),
            new Vector3D(Col0.Y, Col1.Y, Col2.Y),
            new Vector3D(Col0.Z, Col1.Z, Col2.Z),
            -InverseApplyDirection(Translation));
    }

    /// <summary>The transform equivalent to applying <paramref name="inner"/> and then
    /// <paramref name="outer"/> — how a nested frame reaches the root frame.</summary>
    public static RigidTransform Compose(RigidTransform outer, RigidTransform inner)
    {
        if (outer.IsIdentity) return inner;
        if (inner.IsIdentity) return outer;
        return new RigidTransform(
            outer.ApplyDirection(inner.Col0),
            outer.ApplyDirection(inner.Col1),
            outer.ApplyDirection(inner.Col2),
            outer.Apply(inner.Translation));
    }
}
