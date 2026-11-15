using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Diffraction;

/// <summary>The incident plane wave of an <see cref="Rcwa1D"/> solve: wavelength (meters, in the
/// medium the indices are relative to), angle in the cover (degrees, in the plane containing the
/// grating vector: the planar mounting), polarization, and the harmonics kept, orders −N … N.</summary>
public sealed record RcwaIncidence(double Wavelength, double AngleDegrees, GratingPolarization Polarization, int Harmonics);

/// <summary>One diffraction order of an <see cref="Rcwa1D"/> solve: reflected and transmitted
/// efficiencies (fractions of the incident power through the plane z = const), whether the order
/// propagates in the cover and in the substrate, and its angle there (degrees, NaN when it does
/// not propagate).</summary>
public sealed record GratingOrderEfficiency(int Order, double Reflected, double Transmitted,
    bool ReflectedPropagates, bool TransmittedPropagates, double ReflectedAngleDegrees, double TransmittedAngleDegrees);

/// <summary>The efficiencies of every retained order, their sums, and the absorbed fraction
/// 1 − ΣR − ΣT. With an absorbing substrate the transmitted orders carry no defined power: they
/// are reported as zero and <see cref="TransmissionDefined"/> is false, so the absorbed fraction
/// includes what enters the substrate.</summary>
public sealed record GratingEfficiencyResult(IReadOnlyList<GratingOrderEfficiency> Orders, double TotalReflected,
    double TotalTransmitted, double Absorbed, bool TransmissionDefined)
{
    /// <summary>The entry of order m, or null when it was not retained.</summary>
    public GratingOrderEfficiency? Order(int m) => Orders.FirstOrDefault(o => o.Order == m);
}

/// <summary>
/// Rigorous coupled-wave analysis of a one-dimensional grating stack in the planar mounting, TE
/// and TM (OP-47, docs/Optics-Conventions.md §61). The method is Moharam, Grann, Pommet and
/// Gaylord's (JOSA A 12, 1068 and 1077, 1995: the formulation and the enhanced transmittance
/// matrix) with Li's Fourier factorisation for TM (JOSA A 13, 1870, 1996; Lalanne and Morris,
/// JOSA A 13, 779, 1996). Those papers are not on this machine, so the equations below are derived
/// here from Maxwell's equations; EMpy's IsotropicRCWA (MIT licence, read as documentation and run
/// as the test oracle) follows the same papers.
/// <para>
/// <b>Conventions.</b> Time dependence e^{+iωt}: a forward wave is e^{−ik·r}, and an absorbing
/// medium of index n + ik (as given, k ≥ 0) has relative permittivity ε = (n − ik)². z runs from the
/// cover (z &lt; 0) through the layers into the substrate; x is along the grating vector. Order m
/// has k_x,m = k₀(n_c sin θ + mλ/Λ) — OpticStudio's grating equation n₂sin θ₂ − n₁sin θ₁ = MλT with
/// M = m. Lengths are normalised by k₀ (z′ = k₀z) and K_x = diag(k_x,m/k₀).
/// </para>
/// <para>
/// <b>Layer equations.</b> ε(x) = Σ_h ε̂_h e^{−ihKx}, ε̂_h = ∫₀¹ ε(u)e^{+i2πhu} du, and [[ε]] the
/// Toeplitz matrix (ε̂_{m−p}). TE (E = E_y = Σ S_m e^{−ik_x,m x}): ∂²S/∂z′² = (K_x² − [[ε]])S. TM
/// (H = H_y = Σ U_m e^{−ik_x,m x}, second field s = ε⁻¹∂U/∂z′, proportional to E_x): Ampère and
/// Faraday give ∂U/∂z′ = εs and ∂s/∂z′ = (K_x ε⁻¹ K_x − I)U; Li's rules (a product of two
/// functions discontinuous at the same points whose product is continuous is factored with the
/// inverse rule) turn εs (= D_x, continuous across the vertical walls) into [[1/ε]]⁻¹s and ε⁻¹·∂ₓU
/// (= E_z, continuous) into [[ε]]⁻¹, so ∂²U/∂z′² = [[1/ε]]⁻¹(K_x[[ε]]⁻¹K_x − I)U. In either case
/// the matrix A has eigenvectors W and eigenvalues q² (<see cref="ComplexEigenSolver"/>), q taken
/// with Re q ≥ 0 (and Im q ≥ 0 when q is imaginary to rounding), and in the layer
/// S = W(e^{−Q(z′−z_l)}c⁺ + e^{Q(z′−z_l−d)}c⁻), the second field V(−e^{−Q(z′−z_l)}c⁺ + e^{Q(z′−z_l−d)}c⁻)
/// with V = WQ (TE) or [[1/ε]]WQ (TM). A homogeneous layer skips the eigen-solve: W = I.
/// </para>
/// <para>
/// <b>Media.</b> In the cover and substrate k_z,m = k₀√(n² − K_x,m²), the root with Re ≥ 0 and
/// Im ≤ 0 (forward or decaying), Y = diag(k_z/k₀). Cover: S = δ_m0 e^{−iY₀z′} + R e^{+iYz′};
/// substrate: S = T e^{−iY(z′−D′)}. The second field is ∂S/∂z′ (TE) or ε⁻¹∂U/∂z′ (TM).
/// </para>
/// <para>
/// <b>Enhanced transmittance matrix.</b> From the substrate, [f; g] = [I; −iY_s] (TE) or
/// [I; −iY_s/ε_s] (TM). Through each layer from the bottom, with X = e^{−Qd′} (never growing):
/// a = ½(W⁻¹f − V⁻¹g), b = ½(W⁻¹f + V⁻¹g), then f ← W(I + XbA), g ← V(−I + XbA) with A = a⁻¹X
/// kept for the way back. At the cover, (iY_c f − g)t = 2iY_c δ (TE; Y_c/n_c² in place of Y_c for
/// TM), R = ft − δ, and T = A_L ⋯ A₁ t. Derived from the continuity of both tangential fields at
/// every interface; only a and V are inverted (LU, <see cref="ComplexLu"/>), never e^{+Qd}.
/// </para>
/// <para>
/// <b>Efficiencies.</b> R_m: |R_m|²·Re(Y_c,m)/(n_c cos θ). T_m: |T_m|²·Re(Y_s,m)/(n_c cos θ) for TE
/// and |T_m|²·Re(Y_s,m/ε_s)·n_c/cos θ for TM — the z-flux of each order over the incident one. A
/// lossless stack conserves Σ(R + T) = 1 to the rounding of the linear algebra.
/// </para>
/// <para>
/// <b>Refused</b>, by name: the conical mounting (an incidence plane not containing the grating
/// vector, not built), an order exactly grazing in a layer (q = 0, where W·Q is singular), and a
/// singular boundary system. The solve is sequential and deterministic.
/// </para>
/// </summary>
public static class Rcwa1D
{
    /// <summary>The largest number of harmonics N (orders −N … N) a solve takes.</summary>
    public const int MaxHarmonics = 200;

    public static GratingEfficiencyResult Solve(GratingStructure structure, RcwaIncidence incidence, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(incidence);
        double lambda = incidence.Wavelength;
        if (!(lambda > 0) || !double.IsFinite(lambda))
            throw new ArgumentException($"RCWA: the wavelength is {lambda} m; it must be positive.");
        if (!(Math.Abs(incidence.AngleDegrees) < 90))
            throw new ArgumentException($"RCWA: the angle of incidence is {incidence.AngleDegrees}°; it must lie strictly between −90° and 90°.");
        int nh = incidence.Harmonics;
        if (nh < 0 || nh > MaxHarmonics)
            throw new ArgumentException($"RCWA: {nh} harmonics requested; take 0 to {MaxHarmonics} (orders −N … N).");
        bool tm = incidence.Polarization == GratingPolarization.TM;
        int size = 2 * nh + 1;
        double k0 = 2 * Math.PI / lambda;
        double nc = structure.CoverIndex;
        double theta = incidence.AngleDegrees * Math.PI / 180;
        double sinT = nc * Math.Sin(theta);
        double cosIn = nc * Math.Cos(theta);   // Y_c,0
        double ratio = lambda / structure.Period;
        var kx = new double[size];
        for (int i = 0; i < size; i++) kx[i] = sinT + (i - nh) * ratio;

        var epsCover = new Complex(nc * nc, 0);
        var epsSub = Permittivity(structure.SubstrateIndex);
        var yc = new Complex[size];
        var ys = new Complex[size];
        for (int i = 0; i < size; i++)
        {
            yc[i] = Kz(epsCover - kx[i] * kx[i]);
            ys[i] = Kz(epsSub - kx[i] * kx[i]);
        }

        // Substrate: f = I, g = −iY_s (TE) or −iY_s/ε_s (TM).
        var f = Identity(size);
        var g = new Complex[size, size];
        for (int i = 0; i < size; i++) g[i, i] = -Complex.ImaginaryOne * (tm ? ys[i] / epsSub : ys[i]);

        var layers = structure.Layers;
        var back = new Complex[layers.Count][,];
        for (int l = layers.Count - 1; l >= 0; l--)
        {
            cancellation.ThrowIfCancellationRequested();
            var (w, v, q) = LayerModes(layers[l], kx, nh, tm, l);
            var x = new Complex[size];
            double dPrime = k0 * layers[l].Thickness;
            for (int i = 0; i < size; i++) x[i] = Complex.Exp(-q[i] * dPrime);
            var wf = Solve(w, f, $"layer {l + 1}: the mode matrix W");
            var vg = Solve(v, g, $"layer {l + 1}: the mode matrix V");
            var a = new Complex[size, size];
            var b = new Complex[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    a[i, j] = 0.5 * (wf[i, j] - vg[i, j]);
                    b[i, j] = 0.5 * (wf[i, j] + vg[i, j]);
                }
            var xDiag = new Complex[size, size];
            for (int i = 0; i < size; i++) xDiag[i, i] = x[i];
            var aInvX = Solve(a, xDiag, $"layer {l + 1}: the transfer matrix a");
            back[l] = aInvX;
            var xbA = Multiply(b, aInvX);
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++) xbA[i, j] *= x[i];
            var plus = (Complex[,])xbA.Clone();
            var minus = (Complex[,])xbA.Clone();
            for (int i = 0; i < size; i++)
            {
                plus[i, i] += 1;
                minus[i, i] -= 1;
            }
            f = Multiply(w, plus);
            g = Multiply(v, minus);
        }

        // Cover: (iY f − g) t = 2iY δ, Y = Y_c (TE) or Y_c/n_c² (TM).
        var system = new Complex[size, size];
        var rhs = new Complex[size, 1];
        for (int i = 0; i < size; i++)
        {
            var yi = tm ? yc[i] / epsCover : yc[i];
            for (int j = 0; j < size; j++) system[i, j] = Complex.ImaginaryOne * yi * f[i, j] - g[i, j];
            if (i == nh) rhs[i, 0] = 2 * Complex.ImaginaryOne * yi;
        }
        var t = Solve(system, rhs, "the boundary system at the cover");
        var r = Multiply(f, t);
        r[nh, 0] -= 1;
        for (int l = 0; l < layers.Count; l++) t = Multiply(back[l], t);

        bool transmissionDefined = !structure.SubstrateAbsorbs;
        var orders = new GratingOrderEfficiency[size];
        double sumR = 0, sumT = 0;
        for (int i = 0; i < size; i++)
        {
            int m = i - nh;
            bool reflectedPropagates = yc[i].Real > 0;
            double re = reflectedPropagates ? Sq(r[i, 0]) * yc[i].Real / cosIn : 0;   // an evanescent order carries no flux
            double te = 0;
            bool transmittedPropagates = transmissionDefined && ys[i].Real > 0;
            if (transmittedPropagates)
            {
                double ns = structure.SubstrateIndex.Real;
                te = tm ? Sq(t[i, 0]) * (ys[i].Real / (ns * ns)) * nc * nc / cosIn
                        : Sq(t[i, 0]) * ys[i].Real / cosIn;
            }
            if (!double.IsFinite(re) || !double.IsFinite(te))
                throw new InvalidOperationException($"RCWA: order {m} came out non-finite (R {re}, T {te}); the linear systems are too ill-conditioned at {nh} harmonics. Change the number of harmonics or the geometry slightly.");
            double ra = reflectedPropagates ? Math.Asin(Math.Clamp(kx[i] / nc, -1, 1)) * 180 / Math.PI : double.NaN;
            double ta = transmittedPropagates ? Math.Asin(Math.Clamp(kx[i] / structure.SubstrateIndex.Real, -1, 1)) * 180 / Math.PI : double.NaN;
            orders[i] = new GratingOrderEfficiency(m, re, te, reflectedPropagates, transmittedPropagates, ra, ta);
            sumR += re;
            sumT += te;
        }
        return new GratingEfficiencyResult(orders, sumR, sumT, 1 - sumR - sumT, transmissionDefined);
    }

    /// <summary>ε = (n − ik)² for an index given as n + ik (the e^{+iωt} convention).</summary>
    internal static Complex Permittivity(Complex index)
    {
        var n = Complex.Conjugate(index);
        return n * n;
    }

    /// <summary>√z with Re ≥ 0 and Im ≤ 0: a forward or decaying k_z/k₀ under e^{+iωt}.</summary>
    private static Complex Kz(Complex z)
    {
        var s = Complex.Sqrt(z);
        if (s.Imaginary > 0) s = -s;
        return s;
    }

    /// <summary>The layer's W, V and q (Re q ≥ 0; Im q ≥ 0 when q is imaginary to rounding).</summary>
    private static (Complex[,] W, Complex[,] V, Complex[] Q) LayerModes(GratingLayer layer, double[] kx, int nh, bool tm, int index)
    {
        int size = kx.Length;
        var q = new Complex[size];
        if (layer.IsHomogeneous)
        {
            var eps = Permittivity(layer.Segments[0].Index);
            var w = Identity(size);
            var v = new Complex[size, size];
            for (int i = 0; i < size; i++)
            {
                q[i] = ModeRoot(kx[i] * kx[i] - eps, index, i - nh);
                v[i, i] = tm ? q[i] / eps : q[i];
            }
            return (w, v, q);
        }

        var e = Toeplitz(layer, nh, inverse: false);
        ComplexDenseMatrix a;
        Complex[,]? eInv = null;
        if (!tm)
        {
            a = new ComplexDenseMatrix(size, size);
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++) a[i, j] = (i == j ? kx[i] * kx[i] : 0) - e[i, j];
        }
        else
        {
            eInv = Toeplitz(layer, nh, inverse: true);   // [[1/ε]]
            // B = K_x [[ε]]⁻¹ K_x − I; A = [[1/ε]]⁻¹ B.
            var kxDiag = new Complex[size, size];
            for (int i = 0; i < size; i++) kxDiag[i, i] = kx[i];
            var eInvKx = Solve(e, kxDiag, $"layer {index + 1}: the permittivity matrix [[ε]]");
            var bm = new Complex[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++) bm[i, j] = kx[i] * eInvKx[i, j] - (i == j ? 1 : 0);
            var am = Solve(eInv, bm, $"layer {index + 1}: the inverse-permittivity matrix [[1/ε]]");
            a = ToDense(am);
        }
        var eig = ComplexEigenSolver.Compute(a);
        var wv = FromDense(eig.Vectors!);
        for (int i = 0; i < size; i++) q[i] = ModeRoot(eig.Values[i], index, null);
        var wq = new Complex[size, size];
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++) wq[i, j] = wv[i, j] * q[j];
        var vMatrix = tm ? Multiply(eInv!, wq) : wq;
        return (wv, vMatrix, q);
    }

    private static Complex ModeRoot(Complex eigenvalue, int layer, int? order)
    {
        var s = Complex.Sqrt(eigenvalue);
        double magnitude = s.Magnitude;
        if (Math.Abs(s.Real) <= 1e-14 * magnitude && s.Imaginary < 0) s = -s;
        if (magnitude == 0 || magnitude < 1e-13)
            throw new InvalidOperationException(order is int m
                ? $"RCWA: order {m} grazes along layer {layer + 1} (its normal wavenumber is zero), where the mode expansion is singular; change the angle or wavelength slightly."
                : $"RCWA: a mode of layer {layer + 1} has a zero normal wavenumber (an order grazing in the layer), where the mode expansion is singular; change the angle or wavelength slightly.");
        return s;
    }

    /// <summary>The Toeplitz matrix of ε (or of 1/ε): entry (m, p) = ĉ_{m−p}, ĉ_h = Σ_k c_k ∫ e^{i2πhu} du over segment k.</summary>
    private static Complex[,] Toeplitz(GratingLayer layer, int nh, bool inverse)
    {
        int size = 2 * nh + 1;
        var coefficients = new Complex[4 * nh + 1];   // h = −2N … 2N
        double u0 = 0;
        var segments = layer.Segments;
        for (int k = 0; k < segments.Count; k++)
        {
            double u1 = k == segments.Count - 1 ? 1 : u0 + segments[k].Width;
            var eps = Permittivity(segments[k].Index);
            var c = inverse ? 1 / eps : eps;
            for (int h = -2 * nh; h <= 2 * nh; h++)
            {
                Complex integral;
                if (h == 0) integral = u1 - u0;
                else
                {
                    double w = 2 * Math.PI * h;
                    integral = (Complex.FromPolarCoordinates(1, w * u1) - Complex.FromPolarCoordinates(1, w * u0)) / new Complex(0, w);
                }
                coefficients[h + 2 * nh] += c * integral;
            }
            u0 = u1;
        }
        var t = new Complex[size, size];
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++) t[i, j] = coefficients[i - j + 2 * nh];
        return t;
    }

    // ---- small dense helpers ----

    private static Complex[,] Identity(int n)
    {
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++) m[i, i] = Complex.One;
        return m;
    }

    private static Complex[,] Multiply(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), k = a.GetLength(1), p = b.GetLength(1);
        var c = new Complex[n, p];
        for (int i = 0; i < n; i++)
            for (int l = 0; l < k; l++)
            {
                var ail = a[i, l];
                if (ail == Complex.Zero) continue;
                for (int j = 0; j < p; j++) c[i, j] += ail * b[l, j];
            }
        return c;
    }

    /// <summary>X with A·X = B, by one LU of A (serial).</summary>
    private static Complex[,] Solve(Complex[,] a, Complex[,] b, string what)
    {
        int n = a.GetLength(0), p = b.GetLength(1);
        ComplexLu lu;
        try
        {
            lu = ComplexLu.Factor(ToDense(a), maxDegreeOfParallelism: 1);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"RCWA: {what} is singular ({ex.Message}); the geometry sits on an anomaly. Change the angle, wavelength or number of harmonics slightly.", ex);
        }
        var x = new Complex[n, p];
        var column = new Complex[n];
        for (int j = 0; j < p; j++)
        {
            for (int i = 0; i < n; i++) column[i] = b[i, j];
            var solution = lu.Solve(column);
            for (int i = 0; i < n; i++) x[i, j] = solution[i];
        }
        return x;
    }

    private static ComplexDenseMatrix ToDense(Complex[,] a)
    {
        int n = a.GetLength(0), m = a.GetLength(1);
        var d = new ComplexDenseMatrix(n, m);
        for (int j = 0; j < m; j++)
            for (int i = 0; i < n; i++) d[i, j] = a[i, j];
        return d;
    }

    private static Complex[,] FromDense(ComplexDenseMatrix d)
    {
        var a = new Complex[d.Rows, d.Columns];
        for (int j = 0; j < d.Columns; j++)
            for (int i = 0; i < d.Rows; i++) a[i, j] = d[i, j];
        return a;
    }

    private static double Sq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
