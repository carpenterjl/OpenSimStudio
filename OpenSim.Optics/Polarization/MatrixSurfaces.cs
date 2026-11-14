using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// The geometry shared by the Jones and Mueller matrix surfaces (OP-52): a plane, traced exactly
/// as a Standard surface of zero curvature and conic (the same <see cref="ConicIntersection"/>
/// calls), so that the geometric trace through a matrix surface is the trace through a plane
/// bit for bit. Rays refract or reflect at it by the media on its two sides as at any plane;
/// the matrix acts only in the polarization ray trace (OpticStudio 2025 R2 help, "Jones Matrix
/// (sequential surfaces, lens data editor)": "The surface shape is always plane" and "Only
/// polarization analysis features consider the effects of this surface type").
/// </summary>
public abstract class PlaneMatrixShape : SurfaceShape
{
    public override double Sag(double x, double y, SequentialSurface surface) => 0;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance) =>
        ConicIntersection.Intersect(x, y, direction, 0, 0, out point, out distance);

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) => ConicIntersection.Normal(point, 0, 0);

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) => new();

    /// <summary>
    /// The transverse basis (û, v̂) in which a matrix surface acts on a ray of unit direction
    /// <paramref name="k"/>, from the surface's local axes x̂_L, ŷ_L (both unit, in the same
    /// frame as k): û is the part of x̂_L at right angles to k̂, normalised, and v̂ = k̂ × û;
    /// where k̂ lies along x̂_L (|x̂_L − k̂(k̂·x̂_L)| below 1e-9) v̂ is the part of ŷ_L at right angles
    /// to k̂ and û = v̂ × k̂. (û, v̂, k̂) is right-handed, and at normal incidence (k̂ = ẑ_L) û = x̂_L
    /// and v̂ = ŷ_L exactly in exact arithmetic (docs/Optics-Conventions.md §30).
    /// </summary>
    public static (Vector3D U, Vector3D V) TransverseBasis(Vector3D k, Vector3D xLocal, Vector3D yLocal)
    {
        var u = xLocal - k * Vector3D.Dot(k, xLocal);
        if (u.Length >= 1e-9)
        {
            u /= u.Length;
            var v = Vector3D.Cross(k, u);
            return (u, v / v.Length);
        }
        var vv = yLocal - k * Vector3D.Dot(k, yLocal);
        vv /= vv.Length;
        var uu = Vector3D.Cross(vv, k);
        return (uu / uu.Length, vv);
    }
}

/// <summary>
/// OpticStudio's Jones Matrix surface (OpticStudio 2025 R2 help, "Jones Matrix (sequential
/// surfaces, lens data editor)" and "Defining Polarizing Components", saved under
/// plans/sources/optics/op-52-help/): a plane whose matrix [[A, B], [C, D]] takes (Ex, Ey) to
/// (A Ex + B Ey, C Ex + D Ey); "This surface uses eight parameter values": 1 Ar, 2 Ai, 3 Br,
/// 4 Bi, 5 Cr, 6 Ci, 7 Dr, 8 Di.
/// <para>
/// <b>Convention (a reading, docs §10 item 26).</b> The entries are OpticStudio's complex
/// numbers, in its convention (the one in which a Jones vector's phase φ is e^(−iφ) here,
/// <see cref="JonesVector.FromAmplitudes"/>). A field in one convention is the complex conjugate
/// of the other, and conj(J E) = conj(J) conj(E), so the matrix applied here is the conjugate,
/// A = Ar − i·Ai and so on (<see cref="A"/> … <see cref="D"/>). The help's sample "Quarter-Wave
/// plate in X direction" ((0, 1) on the diagonal for x) thus delays x by a quarter wave relative
/// to y in both programs.
/// </para>
/// <para>
/// <b>Frame (a reading).</b> Ex and Ey are taken along the surface's local x and y axes. The
/// help says the matrix is "designed for normal incidence" and that OpticStudio adjusts Ez or
/// Ex and Ey after it so that k·E = 0 and |E| does not grow, without the formula. Here the
/// matrix acts in the transverse basis (û, v̂) of <see cref="PlaneMatrixShape.TransverseBasis"/>
/// for the ray leaving the surface, which is the local (x, y) at normal incidence, keeps the
/// field transverse, and never increases |E| for a passive matrix (singular values at most 1);
/// a unitary matrix (a retarder or rotator) keeps |E| at every angle. Its 3 × 3 embedding is
/// <see cref="Embedded"/>.
/// </para>
/// </summary>
public sealed class JonesMatrixShape : PlaneMatrixShape
{
    public const string Name = "Jones Matrix";

    /// <summary>The eight parameters as OpticStudio's editor holds them (OpticStudio's convention).</summary>
    public double Ar { get; }
    public double Ai { get; }
    public double Br { get; }
    public double Bi { get; }
    public double Cr { get; }
    public double Ci { get; }
    public double Dr { get; }
    public double Di { get; }

    public JonesMatrixShape(double ar, double ai, double br, double bi, double cr, double ci, double dr, double di)
    {
        foreach (var (v, n) in new[] { (ar, "Ar"), (ai, "Ai"), (br, "Br"), (bi, "Bi"), (cr, "Cr"), (ci, "Ci"), (dr, "Dr"), (di, "Di") })
            if (!double.IsFinite(v)) throw new ArgumentException($"{Name}: {n} is {v}; it must be a finite number.");
        (Ar, Ai, Br, Bi, Cr, Ci, Dr, Di) = (ar, ai, br, bi, cr, ci, dr, di);
    }

    public override string TypeName => Name;

    /// <summary>The matrix entries in this program's convention (conjugates of OpticStudio's).</summary>
    public Complex A => new(Ar, -Ai);
    public Complex B => new(Br, -Bi);
    public Complex C => new(Cr, -Ci);
    public Complex D => new(Dr, -Di);

    /// <summary>True for exactly [[1, 0], [0, 1]], which the polarization trace skips so that an
    /// identity surface leaves every number of the trace unchanged.</summary>
    public bool IsIdentity => Ar == 1 && Ai == 0 && Br == 0 && Bi == 0 && Cr == 0 && Ci == 0 && Dr == 1 && Di == 0;

    /// <summary>
    /// The 3 × 3 polarization matrix of the surface for a ray leaving it along k̂ with transverse
    /// basis (û, v̂): P_J = [û v̂]·J·[û v̂]ᵀ + k̂k̂ᵀ, i.e.
    /// P_J = û(A ûᵀ + B v̂ᵀ) + v̂(C ûᵀ + D v̂ᵀ) + k̂k̂ᵀ. It maps a transverse field
    /// Eu û + Ev v̂ to (A Eu + B Ev) û + (C Eu + D Ev) v̂ and k̂ to k̂, the convention of the
    /// interface matrices (§21), so it composes with them by matrix product.
    /// </summary>
    public PolarizationMatrix Embedded(Vector3D u, Vector3D v, Vector3D k)
    {
        Complex a = A, b = B, c = C, d = D;
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int col = 0; col < 3; col++)
                m[3 * r + col] = u[r] * (a * u[col] + b * v[col]) + v[r] * (c * u[col] + d * v[col]) + k[r] * k[col];
        return PolarizationMatrix.FromRowMajor(m);
    }
}

/// <summary>
/// A Mueller matrix surface (OP-52): a plane whose 4 × 4 real matrix M acts on the ray's Stokes
/// vector in the transverse basis (û, v̂) of <see cref="PlaneMatrixShape.TransverseBasis"/>,
/// S_out = M·S_in, so that it can depolarize. OpticStudio 2025 R2 has no sequential surface of
/// this kind (its "Summary Table of Sequential Surface Types" lists a Jones Matrix surface and no
/// Mueller one; saved under plans/sources/optics/op-52-help/), so the type and its sixteen
/// parameters are this program's: parameter 4r + c + 1 is M_rc, row r and column c from 0 to 3
/// (M00 … M33, row-major), dimensionless. A ray that meets one carries its 3 × 3 coherency
/// matrix from there on (<see cref="CoherencyMatrix"/>, docs/Optics-Conventions.md §30).
/// </summary>
public sealed class MuellerMatrixShape : PlaneMatrixShape
{
    public const string Name = "Mueller Matrix";

    private readonly double[] _m;

    public MuellerMatrixShape(IReadOnlyList<double> rowMajor)
    {
        ArgumentNullException.ThrowIfNull(rowMajor);
        if (rowMajor.Count != 16) throw new ArgumentException($"{Name}: the matrix has 16 entries (it was given {rowMajor.Count}).");
        for (int i = 0; i < 16; i++)
            if (!double.IsFinite(rowMajor[i])) throw new ArgumentException($"{Name}: M{i / 4}{i % 4} is {rowMajor[i]}; it must be a finite number.");
        _m = rowMajor.ToArray();
    }

    public override string TypeName => Name;

    /// <summary>The sixteen entries, row-major.</summary>
    public IReadOnlyList<double> Matrix => _m;

    public double this[int row, int column] => _m[4 * row + column];

    /// <summary>The ideal depolarizer diag(1, d, d, d): it keeps the power and multiplies the
    /// degree of polarization by d (derived: Q, U and V all scale by d).</summary>
    public static MuellerMatrixShape Depolarizer(double factor) => new(new[]
    {
        1, 0, 0, 0,
        0, factor, 0, 0,
        0, 0, factor, 0,
        0, 0, 0, factor,
    });
}
