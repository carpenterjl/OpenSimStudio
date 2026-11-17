using System.Globalization;
using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// A Stokes vector (I, Q, U, V) of light in a transverse basis (û, v̂) at right angles to the ray
/// (OP-52, docs/Optics-Conventions.md §30). The definitions are those of "Stokes parameters"
/// (Wikipedia, section "Representations in fixed bases", saved under
/// plans/sources/optics/op-52-help/wiki_Stokes_parameters.html): for a field (Eu, Ev),
/// I = |Eu|² + |Ev|², Q = |Eu|² − |Ev|², U = 2 Re(Eu Ev*), V = −2 Im(Eu Ev*), applied to this
/// program's complex amplitudes (a wave e^(i(k·r − ωt)), §21). For partially polarized light
/// the products are averages, i.e. entries of the 2 × 2 coherency matrix C = ⟨E E^H⟩:
/// I = C00 + C11, Q = C00 − C11, U = 2 Re C01, V = −2 Im C01. This is the same map as
/// S = A·(E ⊗ E*) with the matrix A of "Mueller calculus" (Wikipedia, section "Mueller vs.
/// Jones calculi", saved beside it), checked row by row in StokesMuellerTests. Which sign of V
/// is called right-handed is not asserted here (the Stokes page notes that the sign of V depends
/// on the convention); the degree of circular polarization is reported signed, V/I.
/// </summary>
public readonly record struct StokesVector(double I, double Q, double U, double V)
{
    /// <summary>√(Q² + U² + V²)/I, between 0 (unpolarized) and 1 (fully polarized); NaN when
    /// I ≤ 0 (no light, where the degree is undefined).</summary>
    public double DegreeOfPolarization => I > 0 ? Math.Sqrt(Q * Q + U * U + V * V) / I : double.NaN;

    /// <summary>√(Q² + U²)/I; NaN when I ≤ 0.</summary>
    public double DegreeOfLinearPolarization => I > 0 ? Math.Sqrt(Q * Q + U * U) / I : double.NaN;

    /// <summary>V/I, signed; NaN when I ≤ 0.</summary>
    public double DegreeOfCircularPolarization => I > 0 ? V / I : double.NaN;

    /// <summary>The Stokes vector of a fully polarized field (Eu, Ev).</summary>
    public static StokesVector FromField(Complex eu, Complex ev)
    {
        Complex c01 = eu * Complex.Conjugate(ev);
        double iu = eu.Real * eu.Real + eu.Imaginary * eu.Imaginary, iv = ev.Real * ev.Real + ev.Imaginary * ev.Imaginary;
        return new StokesVector(iu + iv, iu - iv, 2 * c01.Real, -2 * c01.Imaginary);
    }

    /// <summary>The vector from a 2 × 2 coherency matrix (c00, c01, c11; c10 = c01*).</summary>
    public static StokesVector FromCoherency(double c00, Complex c01, double c11) =>
        new(c00 + c11, c00 - c11, 2 * c01.Real, -2 * c01.Imaginary);

    /// <summary>The 2 × 2 coherency matrix of this vector, the inverse of
    /// <see cref="FromCoherency"/> (derived): c00 = (I + Q)/2, c11 = (I − Q)/2,
    /// c01 = (U − iV)/2.</summary>
    public (double C00, Complex C01, double C11) ToCoherency() => ((I + Q) / 2, new Complex(U / 2, -V / 2), (I - Q) / 2);

    /// <summary>M·S for a 4 × 4 Mueller matrix given row-major (16 entries).</summary>
    public StokesVector Apply(IReadOnlyList<double> m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Count != 16) throw new ArgumentException("A Mueller matrix has 16 entries.", nameof(m));
        double i = I, q = Q, u = U, v = V;
        double Row(int r) => m[4 * r] * i + m[4 * r + 1] * q + m[4 * r + 2] * u + m[4 * r + 3] * v;
        return new StokesVector(Row(0), Row(1), Row(2), Row(3));
    }

    /// <summary>
    /// The Mueller matrix (row-major) of a Jones matrix [[a, b], [c, d]] in this program's
    /// convention: M = A·(J ⊗ J*)·A⁻¹ ("Mueller calculus", Wikipedia, section "Mueller vs. Jones
    /// calculi"), with A = [[1, 0, 0, 1], [1, 0, 0, −1], [0, 1, 1, 0], [0, i, −i, 0]] copied from
    /// there and A⁻¹ = ½[[1, 1, 0, 0], [0, 0, 1, −i], [0, 0, 1, i], [1, −1, 0, 0]] derived (it is
    /// the map S → (c00, c01, c10, c11) of <see cref="ToCoherency"/>). The imaginary parts
    /// vanish in exact arithmetic and are dropped.
    /// </summary>
    public static double[] MuellerFromJones(Complex a, Complex b, Complex c, Complex d)
    {
        var j = new[,] { { a, b }, { c, d } };
        var kron = new Complex[4, 4];
        for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                for (int r = 0; r < 2; r++)
                    for (int s = 0; s < 2; s++)
                        kron[2 * p + q, 2 * r + s] = j[p, r] * Complex.Conjugate(j[q, s]);
        var i = Complex.ImaginaryOne;
        var A = new Complex[,] { { 1, 0, 0, 1 }, { 1, 0, 0, -1 }, { 0, 1, 1, 0 }, { 0, i, -i, 0 } };
        var Ainv = new Complex[,] { { 0.5, 0.5, 0, 0 }, { 0, 0, 0.5, -0.5 * i }, { 0, 0, 0.5, 0.5 * i }, { 0.5, -0.5, 0, 0 } };
        var m = new double[16];
        for (int r = 0; r < 4; r++)
            for (int col = 0; col < 4; col++)
            {
                Complex sum = Complex.Zero;
                for (int x = 0; x < 4; x++)
                    for (int y = 0; y < 4; y++)
                        sum += A[r, x] * kron[x, y] * Ainv[y, col];
                m[4 * r + col] = sum.Real;
            }
        return m;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({I:g8}, {Q:g8}, {U:g8}, {V:g8})");
}

/// <summary>
/// The 3 × 3 coherency matrix Φ = ⟨E E^H⟩ of a ray's field, three Cartesian components in
/// surface 1's frame (OP-52, docs/Optics-Conventions.md §30). It is the state a ray carries from
/// the first Mueller matrix surface on: a polarization matrix P acts as Φ → P Φ P^H, a power
/// factor f as Φ → f Φ, and tr Φ is the power. For a fully polarized ray Φ = E E^H, so the
/// field calculus of §21 and this one agree wherever both apply. Immutable; row-major.
/// </summary>
public sealed class CoherencyMatrix
{
    private readonly Complex[] _m;

    private CoherencyMatrix(Complex[] m) => _m = m;

    public Complex this[int row, int column] => _m[3 * row + column];

    /// <summary>tr Φ, the power.</summary>
    public double Power => _m[0].Real + _m[4].Real + _m[8].Real;

    // OP-83
    /// <summary>The matrix of nine elements in row-major order, as read back from a ray database
    /// file (the elements are taken as they are, to the bit).</summary>
    public static CoherencyMatrix FromElements(IReadOnlyList<Complex> rowMajor)
    {
        ArgumentNullException.ThrowIfNull(rowMajor);
        if (rowMajor.Count != 9) throw new ArgumentException($"A coherency matrix has 9 elements; {rowMajor.Count} were given.");
        return new CoherencyMatrix(rowMajor.ToArray());
    }

    /// <summary>E E^H.</summary>
    public static CoherencyMatrix FromField(RayField e)
    {
        Complex[] v = { e.X, e.Y, e.Z };
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = v[r] * Complex.Conjugate(v[c]);
        return new CoherencyMatrix(m);
    }

    /// <summary>The mean of two coherency matrices (an incoherent sum of half powers, as
    /// unpolarized light is the mean of two orthogonal states).</summary>
    public static CoherencyMatrix Average(CoherencyMatrix a, CoherencyMatrix b)
    {
        var m = new Complex[9];
        for (int i = 0; i < 9; i++) m[i] = (a._m[i] + b._m[i]) / 2;
        return new CoherencyMatrix(m);
    }

    /// <summary>P Φ P^H.</summary>
    public CoherencyMatrix Transformed(PolarizationMatrix p)
    {
        var t = new Complex[9];   // P Φ
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                t[3 * r + c] = p[r, 0] * _m[c] + p[r, 1] * _m[3 + c] + p[r, 2] * _m[6 + c];
        var m = new Complex[9];   // (P Φ) P^H
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = t[3 * r] * Complex.Conjugate(p[c, 0]) + t[3 * r + 1] * Complex.Conjugate(p[c, 1]) + t[3 * r + 2] * Complex.Conjugate(p[c, 2]);
        return new CoherencyMatrix(m);
    }

    /// <summary>f Φ for a real power factor f.</summary>
    public CoherencyMatrix Scaled(double factor)
    {
        var m = new Complex[9];
        for (int i = 0; i < 9; i++) m[i] = factor * _m[i];
        return new CoherencyMatrix(m);
    }

    /// <summary>The Stokes vector in the transverse basis (û, v̂): the 2 × 2 coherency matrix
    /// C_ab = âᵀ Φ b̂ (â, b̂ ∈ {û, v̂}) read by <see cref="StokesVector.FromCoherency"/>.</summary>
    public StokesVector Stokes(Vector3D u, Vector3D v) =>
        StokesVector.FromCoherency(Form(u, u).Real, Form(u, v), Form(v, v).Real);

    /// <summary>The coherency matrix û c00 ûᵀ + û c01 v̂ᵀ + v̂ c10 ûᵀ + v̂ c11 v̂ᵀ of a Stokes
    /// vector in the basis (û, v̂): light wholly transverse to û × v̂.</summary>
    public static CoherencyMatrix FromStokes(StokesVector s, Vector3D u, Vector3D v)
    {
        var (c00, c01, c11) = s.ToCoherency();
        var c10 = Complex.Conjugate(c01);
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = c00 * (u[r] * u[c]) + c01 * (u[r] * v[c]) + c10 * (v[r] * u[c]) + c11 * (v[r] * v[c]);
        return new CoherencyMatrix(m);
    }

    private Complex Form(Vector3D a, Vector3D b)
    {
        Complex sum = Complex.Zero;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                sum += a[r] * _m[3 * r + c] * b[c];
        return sum;
    }
}
