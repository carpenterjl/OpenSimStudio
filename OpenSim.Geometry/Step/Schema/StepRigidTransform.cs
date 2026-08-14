using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A rigid placement: a rotation (stored as the three columns of R) followed by a
/// translation, in meters.
/// <para>
/// Assemblies place a part by giving two frames — one in the part's own representation and
/// one in the parent's. The map between them is R = [X₂ Y₂ Z₂]·[X₁ Y₁ Z₁]ᵀ, t = O₂ − R·O₁.
/// Both frames are orthonormal and right-handed by the schema's build_axes rule, so R is a
/// pure rotation: det R = +1 and a STEP assembly can never mirror a part. The determinant
/// is nonetheless checked at construction, which turns corrupt DIRECTION data into a named
/// failure instead of a quietly reflected body.
/// </para>
/// Because axis-aligned exporter frames have exact 0/±1 direction cosines, an axis-aligned
/// placement (the overwhelmingly common case) maps vertices with EXACT arithmetic.
/// </summary>
public sealed record StepRigidTransform(Vector3D Col0, Vector3D Col1, Vector3D Col2, Vector3D Translation)
{
    /// <summary>The placement that changes nothing.</summary>
    public static StepRigidTransform Identity { get; } =
        new(Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ, Vector3D.Zero);

    /// <summary>True when this is exactly the identity — the fast path skips copying a mesh.</summary>
    public bool IsIdentity =>
        Col0 == Vector3D.UnitX && Col1 == Vector3D.UnitY && Col2 == Vector3D.UnitZ
        && Translation == Vector3D.Zero;

    /// <summary>det R; +1 for every rotation.</summary>
    public double Determinant => Vector3D.Dot(Col0, Vector3D.Cross(Col1, Col2));

    /// <summary>The placement carrying geometry expressed in <paramref name="from"/>'s
    /// representation into <paramref name="to"/>'s.</summary>
    /// <param name="contextId">#id of the entity that supplied the frames, for error text.</param>
    public static StepRigidTransform FromFrames(Axis2Placement3D from, Axis2Placement3D to, int contextId)
    {
        Vector3D MapDirection(Vector3D v) =>
            to.XAxis * Vector3D.Dot(from.XAxis, v)
            + to.YAxis * Vector3D.Dot(from.YAxis, v)
            + to.ZAxis * Vector3D.Dot(from.ZAxis, v);

        var c0 = MapDirection(Vector3D.UnitX);
        var c1 = MapDirection(Vector3D.UnitY);
        var c2 = MapDirection(Vector3D.UnitZ);
        var o = from.Origin;
        var translation = to.Origin - (c0 * o.X + c1 * o.Y + c2 * o.Z);
        return Validate(new StepRigidTransform(c0, c1, c2, translation), contextId);
    }

    /// <summary>Rotates a direction (no translation).</summary>
    public Vector3D ApplyDirection(Vector3D v) => Col0 * v.X + Col1 * v.Y + Col2 * v.Z;

    /// <summary>Places a point.</summary>
    public Vector3D Apply(Vector3D p) => ApplyDirection(p) + Translation;

    /// <summary>The placement equivalent to applying <paramref name="inner"/> and then
    /// <paramref name="outer"/> — how a nested assembly's part reaches the root frame.</summary>
    public static StepRigidTransform Compose(StepRigidTransform outer, StepRigidTransform inner)
    {
        if (outer.IsIdentity) return inner;
        if (inner.IsIdentity) return outer;
        return new StepRigidTransform(
            outer.ApplyDirection(inner.Col0),
            outer.ApplyDirection(inner.Col1),
            outer.ApplyDirection(inner.Col2),
            outer.Apply(inner.Translation));
    }

    private static StepRigidTransform Validate(StepRigidTransform t, int contextId)
    {
        double det = t.Determinant;
        if (Math.Abs(det - 1.0) > 1e-9)
            throw new StepImportException(
                $"#{contextId}: assembly placement is not a rigid motion (det R = {det:g6}); " +
                "scaled or mirrored placements are not supported");
        return t;
    }
}
