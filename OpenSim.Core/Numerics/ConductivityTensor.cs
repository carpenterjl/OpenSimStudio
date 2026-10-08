namespace OpenSim.Core.Numerics;

/// <summary>
/// A symmetric conductivity tensor [W/(m·K) for heat]: the flux is q = −K·∇T. An isotropic
/// material is k·I; a board layer whose copper runs as traces conducts along them as copper and
/// laminate side by side and across them as the two in series, which is a tensor with its
/// principal axes along and across the traces.
/// </summary>
public readonly record struct ConductivityTensor(double Xx, double Yy, double Zz,
    double Xy = 0, double Xz = 0, double Yz = 0)
{
    public static ConductivityTensor Isotropic(double k) => new(k, k, k);

    /// <summary>Principal values along a direction at <paramref name="angleRadians"/> from x in
    /// the board plane, across it in the plane, and through the plane (z).</summary>
    public static ConductivityTensor InPlane(double along, double across, double through, double angleRadians)
    {
        double c = Math.Cos(angleRadians), s = Math.Sin(angleRadians);
        return new ConductivityTensor(
            along * c * c + across * s * s,
            along * s * s + across * c * c,
            through,
            (along - across) * c * s);
    }

    /// <summary>K·v.</summary>
    public Vector3D Apply(Vector3D v) => new(
        Xx * v.X + Xy * v.Y + Xz * v.Z,
        Xy * v.X + Yy * v.Y + Yz * v.Z,
        Xz * v.X + Yz * v.Y + Zz * v.Z);

    /// <summary>Sylvester's criterion: every leading minor positive.</summary>
    public bool IsPositiveDefinite =>
        Xx > 0
        && Xx * Yy - Xy * Xy > 0
        && Xx * (Yy * Zz - Yz * Yz) - Xy * (Xy * Zz - Yz * Xz) + Xz * (Xy * Yz - Yy * Xz) > 0;

    /// <summary>A bound on the largest principal value (Gershgorin), for step-size estimates.</summary>
    public double LargestBound => Math.Max(Xx + Math.Abs(Xy) + Math.Abs(Xz),
        Math.Max(Yy + Math.Abs(Xy) + Math.Abs(Yz), Zz + Math.Abs(Xz) + Math.Abs(Yz)));
}
