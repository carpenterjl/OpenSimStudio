using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// A 3 × 3 complex polarization ray-tracing matrix P: it maps the electric field arriving at a
/// surface (or a whole system) to the field leaving it, E_out = P·E_in, all three components in
/// one Cartesian frame. Immutable; row-major.
/// <para>
/// <b>One interface</b> (<see cref="Interface"/>). With k̂ the unit ray directions before and
/// after, η̂ the surface normal, ŝ = k̂_in × η̂ / |k̂_in × η̂| (common to both sides, since
/// k̂_in, k̂_out and η̂ lie in the plane of incidence), p̂_in = k̂_in × ŝ and
/// p̂_out = k̂_out × ŝ, the frames O_in = [ŝ p̂_in k̂_in] and O_out = [ŝ p̂_out k̂_out] are
/// orthogonal, and the matrix that multiplies the s part by a_s, the p part by a_p and carries
/// k̂_in to k̂_out is P = O_out·diag(a_s, a_p, 1)·O_inᵀ
/// = a_s ŝŝᵀ + a_p p̂_out p̂_inᵀ + k̂_out k̂_inᵀ.
/// This is the construction of Yun, Crabtree and Chipman, "Three-dimensional polarization
/// ray-tracing calculus I", Appl. Opt. 50 (2011) 2855, as its abstract and the Polaris-M
/// literature describe it; the paper itself was not read (no free copy was found), so the
/// form above is derived here, not copied: an orthogonal change of basis into the local s, p, k
/// frame, a diagonal Jones action, and the change back into the exit frame. A system's matrix
/// is the product of its surfaces' matrices in the order the light meets them, times the
/// scalar bulk amplitude factors.
/// </para>
/// </summary>
public sealed class PolarizationMatrix
{
    private readonly Complex[] _m;

    private PolarizationMatrix(Complex[] m) => _m = m;

    public Complex this[int row, int column] => _m[3 * row + column];

    public static PolarizationMatrix Identity { get; } = new(new[]
    {
        Complex.One, Complex.Zero, Complex.Zero,
        Complex.Zero, Complex.One, Complex.Zero,
        Complex.Zero, Complex.Zero, Complex.One,
    });

    /// <summary>The matrix of one interface, a_s ŝŝᵀ + a_p p̂_out p̂_inᵀ + k̂_out k̂_inᵀ.</summary>
    public static PolarizationMatrix Interface(Vector3D s, Vector3D pIn, Vector3D kIn, Vector3D sOut, Vector3D pOut, Vector3D kOut,
        Complex aS, Complex aP)
    {
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = aS * (sOut[r] * s[c]) + aP * (pOut[r] * pIn[c]) + kOut[r] * kIn[c];
        return new PolarizationMatrix(m);
    }

    public static PolarizationMatrix operator *(PolarizationMatrix a, PolarizationMatrix b)
    {
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = a._m[3 * r] * b._m[c] + a._m[3 * r + 1] * b._m[3 + c] + a._m[3 * r + 2] * b._m[6 + c];
        return new PolarizationMatrix(m);
    }

    /// <summary>The matrix times a complex scalar (a bulk amplitude factor).</summary>
    public PolarizationMatrix Scaled(Complex factor)
    {
        var m = new Complex[9];
        for (int i = 0; i < 9; i++) m[i] = factor * _m[i];
        return new PolarizationMatrix(m);
    }

    public RayField Apply(RayField e) => new(
        _m[0] * e.X + _m[1] * e.Y + _m[2] * e.Z,
        _m[3] * e.X + _m[4] * e.Y + _m[5] * e.Z,
        _m[6] * e.X + _m[7] * e.Y + _m[8] * e.Z);

    /// <summary>
    /// The intensity transmittances of the two eigen-polarizations of the transverse part, for
    /// light arriving along <paramref name="kIn"/>: with u, v any orthonormal pair at right
    /// angles to k̂_in, a = P·u and b = P·v, the Hermitian Gram matrix
    /// G = [[a*·a, a*·b], [b*·a, b*·b]] has eigenvalues
    /// λ± = (tr G ± √((G11 − G22)² + 4|G12|²)) / 2, the largest and smallest |P·E|² over unit
    /// transverse E (derived: |P(αu + βv)|² = [α β]* G [α β]ᵀ, a Rayleigh quotient).
    /// </summary>
    public (double Max, double Min) TransverseTransmittances(Vector3D kIn)
    {
        var (u, v) = JonesVector.LaunchBasis(kIn, PolarizationReference.XAxis);
        var a = Apply(new RayField(u.X, u.Y, u.Z));
        var b = Apply(new RayField(v.X, v.Y, v.Z));
        double g11 = a.NormSquared, g22 = b.NormSquared;
        Complex g12 = Complex.Conjugate(a.X) * b.X + Complex.Conjugate(a.Y) * b.Y + Complex.Conjugate(a.Z) * b.Z;
        double tr = g11 + g22;
        double root = Math.Sqrt((g11 - g22) * (g11 - g22) + 4 * (g12.Real * g12.Real + g12.Imaginary * g12.Imaginary));
        return ((tr + root) / 2, Math.Max(0, (tr - root) / 2));
    }

    /// <summary>The diattenuation (T_max − T_min)/(T_max + T_min) of the transverse part; zero
    /// when nothing is transmitted.</summary>
    public double Diattenuation(Vector3D kIn)
    {
        var (max, min) = TransverseTransmittances(kIn);
        return max + min > 0 ? (max - min) / (max + min) : 0;
    }
}
