using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>One surface-wave pole of the layered kernels: its wavenumber, which
/// dispersion family it solves, and the residues of BOTH spectral kernels there
/// (G̃_A carries only TE poles — its ResidueA is zero at a TM pole; K̃_Φ carries
/// both families through its D_TE·D_TM denominator).</summary>
internal readonly record struct SurfaceWavePole(
    Complex KRho, bool IsTm, Complex ResidueA, Complex ResiduePhi);

/// <summary>
/// Finds every surface-wave pole of the grounded-slab kernels on the proper sheet.
/// Lossless slabs put all poles on the real segment (k₀, k₁): parametrized by
/// u = k_z1·d, the TM dispersion is u·sin u = εr·γ₀d·cos u and the TE dispersion
/// u·cos u = −γ₀d·sin u with γ₀d = √(u_max² − u²), u_max = d√(k₁² − k₀²). TM0 (the
/// u &lt; π/2 root) exists for ANY thickness; higher branches appear as u_max crosses
/// their cutoffs. The search brackets sign changes on a fixed fine sampling of each
/// quarter-period branch and bisects — deterministic, no Newton stall modes. Lossy
/// slabs take the lossless root as a seed for a complex Newton iteration on the
/// reduced dispersion function (analytic derivative), landing on Im(k_ρ) ≤ 0.
/// Residues come from the same analytic derivative:
///   TM:  Res K̃_Φ = (2/ε₀)·S·N / (D̂_TE · D̂_TM′),      Res G̃_A = 0
///   TE:  Res G̃_A = 2µ₀·S / D̂_TE′,   Res K̃_Φ = (2/ε₀)·S·N / (D̂_TE′ · D̂_TM).
/// </summary>
internal static class SurfaceWavePoles
{
    /// <summary>
    /// The multi-layer (Stage F) pole finder: zeros of the TLGF TE/TM dispersion
    /// (<see cref="TransmissionLineGreens.TeDispersion"/> / <see cref="TransmissionLineGreens.TmDispersion"/>)
    /// on the bound-mode segment (k₀, k₀√εr_max), where a lossless stack makes each
    /// dispersion REAL — so the same bracket-and-bisect logic as the single-slab finder
    /// applies, now sampling k_ρ directly instead of the u = k_z1·d branches. Lossy stacks
    /// take the lossless root as a complex-Newton seed. Residues come from the analytic
    /// null-vector matrix method (<see cref="TransmissionLineGreens.PoleResidues"/>), general
    /// for any N. The single-slab list is byte-identical to <see cref="Find(SubstrateStackup,double)"/>
    /// only up to the finder route; the F-pole gates pin locations + residues to it.
    /// </summary>
    public static IReadOnlyList<SurfaceWavePole> Find(LayeredStackup stackup, double k0)
    {
        if (k0 <= 0) throw new ArgumentOutOfRangeException(nameof(k0));
        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        double kMax = k0 * Math.Sqrt(epsMax);
        var poles = new List<SurfaceWavePole>();
        if (kMax <= k0) return poles;   // all air: no surface waves, image theory is exact.
        bool lossy = stackup.Layers.Any(l => l.LossTangent > 0);

        foreach (bool isTm in new[] { true, false })
        {
            Func<Complex, Complex> disp = isTm
                ? kr => TransmissionLineGreens.TmDispersion(stackup, k0, kr)
                : kr => TransmissionLineGreens.TeDispersion(stackup, k0, kr);
            foreach (double kRhoReal in RealDispersionRoots(kr => disp(kr).Real, k0, kMax))
            {
                Complex kp = kRhoReal;
                if (lossy)
                {
                    for (int iteration = 0; iteration < 50; iteration++)
                    {
                        Complex delta = 1e-7 * Math.Max(kp.Magnitude, k0);
                        Complex slope = (disp(kp + delta) - disp(kp - delta)) / (2 * delta);
                        Complex step = disp(kp) / slope;
                        kp -= step;
                        if (step.Magnitude <= 1e-15 * kp.Magnitude) break;
                    }
                    if (kp.Imaginary > 0)
                        throw new InvalidOperationException(
                            $"A {(isTm ? "TM" : "TE")} pole converged to the non-physical half-plane (k_ρ = {kp}).");
                }
                var (resA, resPhi) = TransmissionLineGreens.PoleResidues(stackup, k0, kp, isTm);
                poles.Add(new SurfaceWavePole(kp, isTm, resA, resPhi));
            }
        }
        return poles;
    }

    /// <summary>Real roots of a real dispersion on (k₀, k_max), bracketed on a fixed dense
    /// sampling and bisected. Endpoints are nudged inward: k_ρ → k₀ makes k_z0 → 0 and
    /// k_ρ → k_max makes the top-layer k_z → 0, both of which the dispersion form divides
    /// through. The grid is fine enough to separate the surface-wave branches of a thick
    /// high-εr stack (they crowd toward k_max).</summary>
    private static IEnumerable<double> RealDispersionRoots(Func<double, double> f, double k0, double kMax)
    {
        const int Samples = 4000;
        double lo = k0 * (1 + 1e-9), hi = kMax * (1 - 1e-9);
        double previous = lo, previousF = f(lo);
        for (int i = 1; i <= Samples; i++)
        {
            double kr = lo + (hi - lo) * i / Samples;
            double value = f(kr);
            if (previousF * value < 0)
            {
                double a = previous, b = kr, fa = previousF;
                for (int iteration = 0; iteration < 200; iteration++)
                {
                    double mid = 0.5 * (a + b);
                    if (mid == a || mid == b) break;
                    double fm = f(mid);
                    if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; }
                    else b = mid;
                }
                yield return 0.5 * (a + b);
            }
            previous = kr;
            previousF = value;
        }
    }

    public static IReadOnlyList<SurfaceWavePole> Find(SubstrateStackup substrate, double k0)
    {
        if (k0 <= 0) throw new ArgumentOutOfRangeException(nameof(k0));
        double epsR = substrate.RelativePermittivity;
        double d = substrate.ThicknessMeters;
        double k1 = k0 * Math.Sqrt(epsR);
        double uMax = d * Math.Sqrt(Math.Max(k1 * k1 - k0 * k0, 0));
        var poles = new List<SurfaceWavePole>();
        if (uMax <= 0) return poles; // εr = 1: no surface waves, image theory is exact.

        // TM: R(u) = u·sin u − εr·√(u_max²−u²)·cos u; TE: R(u) = u·cos u + √(u_max²−u²)·sin u.
        foreach (double u in RealRoots(u => u * Math.Sin(u)
                     - epsR * Math.Sqrt(Math.Max(uMax * uMax - u * u, 0)) * Math.Cos(u), uMax))
            AddPole(poles, substrate, k0, KRhoFromU(u, k1, d), isTm: true);
        foreach (double u in RealRoots(u => u * Math.Cos(u)
                     + Math.Sqrt(Math.Max(uMax * uMax - u * u, 0)) * Math.Sin(u), uMax))
            AddPole(poles, substrate, k0, KRhoFromU(u, k1, d), isTm: false);
        return poles;
    }

    private static double KRhoFromU(double u, double k1, double d) =>
        Math.Sqrt(Math.Max(k1 * k1 - (u / d) * (u / d), 0));

    /// <summary>Roots of a real dispersion function on (0, u_max), bracketed on a fixed
    /// 64-sample grid per quarter-period branch (the function changes sign at most once
    /// between its trig extrema at that resolution) and bisected to machine precision.
    /// Roots within 1e-9·u_max of an endpoint are skipped: a mode exactly at cutoff
    /// sits ON the k₀ branch point and carries no residue worth extracting.</summary>
    private static IEnumerable<double> RealRoots(Func<double, double> f, double uMax)
    {
        var breaks = new List<double> { 0 };
        for (double b = Math.PI / 2; b < uMax; b += Math.PI / 2) breaks.Add(b);
        breaks.Add(uMax);
        for (int seg = 0; seg + 1 < breaks.Count; seg++)
        {
            double lo = breaks[seg], hi = breaks[seg + 1];
            const int Samples = 64;
            double previousU = lo + 1e-12 * uMax;
            double previousF = f(previousU);
            for (int i = 1; i <= Samples; i++)
            {
                double u = lo + (hi - lo) * i / Samples;
                if (i == Samples) u = hi - 1e-12 * uMax;
                double value = f(u);
                if (previousF * value < 0)
                {
                    double a = previousU, b = u, fa = previousF;
                    for (int iteration = 0; iteration < 200; iteration++)
                    {
                        double mid = 0.5 * (a + b);
                        if (mid == a || mid == b) break;
                        double fm = f(mid);
                        if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; }
                        else b = mid;
                    }
                    double root = 0.5 * (a + b);
                    if (root > 1e-9 * uMax && root < uMax * (1 - 1e-9))
                        yield return root;
                }
                previousU = u;
                previousF = value;
            }
        }
    }

    private static void AddPole(List<SurfaceWavePole> poles, SubstrateStackup substrate,
        double k0, double losslessKRho, bool isTm)
    {
        Complex kRho = losslessKRho;
        if (substrate.LossTangent > 0)
        {
            // Complex Newton on the reduced dispersion from the lossless seed. The
            // loss perturbation is O(tanδ), well inside Newton's basin here.
            for (int iteration = 0; iteration < 50; iteration++)
            {
                var (dTe, dTm, dTePrime, dTmPrime, _, _) =
                    SpectralKernels.Dispersion(substrate, k0, kRho);
                var value = isTm ? dTm : dTe;
                var slope = isTm ? dTmPrime : dTePrime;
                var step = value / slope;
                kRho -= step;
                if (step.Magnitude <= 1e-15 * kRho.Magnitude) break;
            }
            if (kRho.Imaginary > 0)
                throw new InvalidOperationException(
                    $"A {(isTm ? "TM" : "TE")} pole converged to the non-physical half-plane (k_ρ = {kRho}).");
        }

        var (te, tm, tePrime, tmPrime, s, n) = SpectralKernels.Dispersion(substrate, k0, kRho);
        Complex residueA, residuePhi;
        if (isTm)
        {
            residueA = Complex.Zero;
            residuePhi = 2 / RfConstants.Eps0 * s * n / (te * tmPrime);
        }
        else
        {
            residueA = 2 * RfConstants.Mu0 * s / tePrime;
            residuePhi = 2 / RfConstants.Eps0 * s * n / (tePrime * tm);
        }
        poles.Add(new SurfaceWavePole(kRho, isTm, residueA, residuePhi));
    }
}
