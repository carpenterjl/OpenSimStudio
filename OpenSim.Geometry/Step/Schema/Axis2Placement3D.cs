using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A resolved AXIS2_PLACEMENT_3D: an orthonormal right-handed frame in meters. The ISO
/// 10303-42 defaults are applied at construction (missing axis → +Z, missing
/// ref_direction → a global axis not parallel to Z), and the X axis is re-orthogonalized
/// against Z exactly as the schema's build_axes function prescribes.
/// </summary>
public sealed record Axis2Placement3D(Vector3D Origin, Vector3D XAxis, Vector3D YAxis, Vector3D ZAxis)
{
    /// <summary>Builds the frame from the raw STEP attributes, applying the schema defaults.</summary>
    public static Axis2Placement3D FromAxes(Vector3D origin, Vector3D? axis, Vector3D? refDirection, int id)
    {
        var z = (axis ?? Vector3D.UnitZ).Normalized();
        if (z.Length == 0) throw new StepImportException($"#{id}: placement axis is a zero vector");

        var seed = refDirection ?? (Math.Abs(Vector3D.Dot(z, Vector3D.UnitX)) < 0.9
            ? Vector3D.UnitX
            : Vector3D.UnitY);
        var x = (seed - z * Vector3D.Dot(seed, z));
        if (x.Length < 1e-12)
            throw new StepImportException($"#{id}: placement ref_direction is parallel to the axis");
        x = x.Normalized();
        var y = Vector3D.Cross(z, x); // right-handed by construction
        return new Axis2Placement3D(origin, x, y, z);
    }

    /// <summary>Components of a world point in this frame (translation removed).</summary>
    public (double X, double Y, double Z) Local(Vector3D p)
    {
        var d = p - Origin;
        return (Vector3D.Dot(d, XAxis), Vector3D.Dot(d, YAxis), Vector3D.Dot(d, ZAxis));
    }
}
