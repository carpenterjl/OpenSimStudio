using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// Builds the rigid placement (<see cref="RigidTransform"/>, in meters) that a STEP assembly
/// gives a part.
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
public static class StepRigidTransform
{
    /// <summary>The placement carrying geometry expressed in <paramref name="from"/>'s
    /// representation into <paramref name="to"/>'s.</summary>
    /// <param name="contextId">#id of the entity that supplied the frames, for error text.</param>
    public static RigidTransform FromFrames(Axis2Placement3D from, Axis2Placement3D to, int contextId)
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
        return Validate(new RigidTransform(c0, c1, c2, translation), contextId);
    }

    private static RigidTransform Validate(RigidTransform t, int contextId)
    {
        double det = t.Determinant;
        if (Math.Abs(det - 1.0) > 1e-9)
            throw new StepImportException(
                $"#{contextId}: assembly placement is not a rigid motion (det R = {det:g6}); " +
                "scaled or mirrored placements are not supported");
        return t;
    }
}
