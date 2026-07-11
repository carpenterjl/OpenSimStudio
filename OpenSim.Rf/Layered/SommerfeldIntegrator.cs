using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Direct Sommerfeld integration of the layered-kernel REMAINDER — the spectral
/// kernels minus (a) the quasi-static dynamic images and (b) the surface-wave pole
/// terms, both of which have exact closed-form spatial counterparts:
///
///   images:  G̃_A − µ₀(1 − e^{−2jk_z0 d})/(jk_z0)            ↔ µ₀[g(ρ) − g(R_{2d})]
///            K̃_Φ − (c₀ + c₁e^{−2jk_z0 d})/(jk_z0 ε₀)        ↔ [c₀g(ρ) + c₁g(R_{2d})]/ε₀
///            with c₀ = 2/(ε_c+1), c₁ = −4ε_c/(ε_c+1)², g(R) = e^{−jk₀R}/4πR —
///            the EXACT k_ρ → ∞ asymptote (and the exact whole kernel at εr = 1),
///   poles:   P̃ = Res·2k_p/(k_ρ² − k_p²)  ↔  −(j/4)·Res·k_p·H₀⁽²⁾(k_p ρ),
///
/// leaving a remainder that is regular ON the whole path and decays algebraically.
/// This is direct integration with extraction, NOT DCIM: no fitted exponentials, no
/// silent approximation — every subtracted term is an exact identity, and the
/// integration error is controlled by deterministic panel counts that the C1
/// self-convergence gate (double everything ⇒ &lt; 1e-8 movement) polices.
///
/// Path: [0, k₀] via k_ρ = k₀ sin t and [k₀, a] via k_ρ = √(k₀² + s²) — both
/// substitutions carry Jacobians that cancel the 1/k_z0 branch behavior EXACTLY
/// (k_z0 = k₀cos t and −js in closed form); panels break at each extracted pole.
/// Tail beyond a = max(2k₁, k₁ + 6/d): geometric doubling panels while J₀ has not
/// begun oscillating (k_ρρ &lt; 3), then Michalski partition–extrapolation — half-period
/// partitions of J₀ with iterated averaging of the partial sums, the deterministic
/// ~15-line form of the classic tail accelerator.
/// </summary>
internal static class SommerfeldIntegrator
{
    private static readonly (double[] Nodes, double[] Weights) Gauss = GaussLegendre.Rule(15);

    /// <summary>The quasi-static image coefficients of K̃_Φ: c₀ (primary, the classic
    /// 2/(εr+1) interface factor) and c₁ (first image at 2d). Complex ε_c keeps the
    /// same algebra exact for lossy slabs.</summary>
    public static (Complex C0, Complex C1) PhiImageCoefficients(Complex epsC) =>
        (2 / (epsC + 1), -4 * epsC / ((epsC + 1) * (epsC + 1)));

    /// <summary>The remainder integrals (1/4π)∫(F̃ − images − poles) J₀(k_ρρ) k_ρ dk_ρ
    /// for both kernels at one lateral distance. <paramref name="refinement"/> scales
    /// every panel count / partition count / tail reach — the self-convergence knob.</summary>
    public static (Complex A, Complex Phi) Remainder(SubstrateStackup substrate, double k0,
        IReadOnlyList<SurfaceWavePole> poles, double rho, int refinement = 1)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho),
            "The remainder is tabulated for ρ > 0 (the ρ → 0 limit is flat on the scale of d).");
        if (refinement < 1) throw new ArgumentOutOfRangeException(nameof(refinement));

        double d = substrate.ThicknessMeters;
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var (c0Phi, c1Phi) = PhiImageCoefficients(epsC);
        double k1Real = k0 * Math.Sqrt(substrate.RelativePermittivity);

        Complex sumA = Complex.Zero, sumPhi = Complex.Zero;

        // ---- Segment 1: k_ρ = k₀ sin t, t ∈ [0, π/2], k_z0 = k₀ cos t. ----
        int n1 = refinement * (4 + (int)Math.Ceiling(k0 * rho / Math.PI));
        for (int p = 0; p < n1; p++)
        {
            double t0 = Math.PI / 2 * p / n1, t1 = Math.PI / 2 * (p + 1) / n1;
            double mid = 0.5 * (t0 + t1), half = 0.5 * (t1 - t0);
            for (int i = 0; i < Gauss.Nodes.Length; i++)
            {
                double t = mid + half * Gauss.Nodes[i];
                var (sin, cos) = Math.SinCos(t);
                double kRho = k0 * sin;
                var (fA, fPhi) = Integrand(substrate, k0, kRho, k0 * cos,
                    poles, c0Phi, c1Phi, rho, d);
                double w = Gauss.Weights[i] * half * k0 * cos;
                sumA += w * fA;
                sumPhi += w * fPhi;
            }
        }

        // ---- Segment 2: k_ρ = √(k₀² + s²), k_z0 = −js, panels broken at poles. ----
        // The head only needs to clear the pole/branch-point region; how far it also
        // covers the exponential e^{−2k_ρd} reach is a COST trade against the tail:
        // for large ρ, resolving J₀ across all of 6/d would take ~(6/d)/(π/ρ) panels,
        // while the partition tail resolves the same stretch one half-period per
        // partition — so the head is capped at 3π/ρ past k₁ and the tail's partition
        // count grows to cover the rest of the exponential reach (below).
        double a = Math.Max(2 * k1Real,
            k1Real + Math.Min(6.0 * refinement / d, 3 * Math.PI / rho));
        double sMax = Math.Sqrt(a * a - k0 * k0);
        var breaks = new List<double> { 0 };
        foreach (var pole in poles)
        {
            double re = pole.KRho.Real;
            if (re > k0 && re * re - k0 * k0 < sMax * sMax)
                breaks.Add(Math.Sqrt(re * re - k0 * k0));
        }
        breaks.Add(sMax);
        breaks.Sort();
        double widthTarget = Math.Min(Math.Min(0.5 / d, sMax / 8), Math.PI / rho);
        for (int seg = 0; seg + 1 < breaks.Count; seg++)
        {
            double lo = breaks[seg], hi = breaks[seg + 1];
            if (hi - lo <= 0) continue;
            int panels = refinement * Math.Max(1, (int)Math.Ceiling((hi - lo) / widthTarget));
            for (int p = 0; p < panels; p++)
            {
                double s0 = lo + (hi - lo) * p / panels, s1 = lo + (hi - lo) * (p + 1) / panels;
                double mid = 0.5 * (s0 + s1), half = 0.5 * (s1 - s0);
                for (int i = 0; i < Gauss.Nodes.Length; i++)
                {
                    double s = mid + half * Gauss.Nodes[i];
                    double kRho = Math.Sqrt(k0 * k0 + s * s);
                    var (fA, fPhi) = Integrand(substrate, k0, kRho, new Complex(0, -s),
                        poles, c0Phi, c1Phi, rho, d);
                    // dk_ρ = (s/k_ρ) ds — the Jacobi factor that cancels 1/k_z0.
                    double w = Gauss.Weights[i] * half * s / kRho;
                    sumA += w * fA;
                    sumPhi += w * fPhi;
                }
            }
        }

        // ---- Tail: geometric panels until J₀ oscillates, then partition–extrapolation. ----
        double b = a;
        double l1A = sumA.Magnitude, l1Phi = sumPhi.Magnitude;
        for (int doubling = 0; doubling < 60 && b * rho < 3; doubling++)
        {
            var (vA, vPhi) = TailPanel(substrate, k0, b, 2 * b, poles, c0Phi, c1Phi, rho, refinement);
            sumA += vA;
            sumPhi += vPhi;
            b *= 2;
            if (vA.Magnitude < 1e-16 * (l1A + 1e-300) && vPhi.Magnitude < 1e-16 * (l1Phi + 1e-300))
            {
                b = double.PositiveInfinity; // the integrand died before oscillating
                break;
            }
        }
        if (!double.IsPositiveInfinity(b))
        {
            // Enough half-period partitions to march through the remaining exponential
            // content (reach ~6/d past b) before the extrapolation takes over the
            // algebraic far tail; 12 is the floor for the pure-algebraic case.
            double delta = Math.PI / rho;
            int partitions = Math.Min(400 * refinement,
                12 * refinement + (int)Math.Ceiling(6.0 / d / delta));
            var partialA = new Complex[partitions];
            var partialPhi = new Complex[partitions];
            Complex accA = Complex.Zero, accPhi = Complex.Zero;
            for (int n = 0; n < partitions; n++)
            {
                var (vA, vPhi) = TailPanel(substrate, k0, b + n * delta, b + (n + 1) * delta,
                    poles, c0Phi, c1Phi, rho, refinement);
                accA += vA;
                accPhi += vPhi;
                partialA[n] = accA;
                partialPhi[n] = accPhi;
            }
            // Iterated averaging of partial sums — the alternating tail's Euler limit.
            for (int m = 1; m < partitions; m++)
                for (int i = 0; i < partitions - m; i++)
                {
                    partialA[i] = 0.5 * (partialA[i] + partialA[i + 1]);
                    partialPhi[i] = 0.5 * (partialPhi[i] + partialPhi[i + 1]);
                }
            sumA += partialA[0];
            sumPhi += partialPhi[0];
        }

        double norm = 1 / (4 * Math.PI);
        return (norm * sumA, norm * sumPhi);
    }

    private static (Complex A, Complex Phi) TailPanel(SubstrateStackup substrate, double k0,
        double lo, double hi, IReadOnlyList<SurfaceWavePole> poles,
        Complex c0Phi, Complex c1Phi, double rho, int refinement)
    {
        Complex vA = Complex.Zero, vPhi = Complex.Zero;
        int sub = refinement;
        for (int p = 0; p < sub; p++)
        {
            double x0 = lo + (hi - lo) * p / sub, x1 = lo + (hi - lo) * (p + 1) / sub;
            double mid = 0.5 * (x0 + x1), half = 0.5 * (x1 - x0);
            for (int i = 0; i < Gauss.Nodes.Length; i++)
            {
                double kRho = mid + half * Gauss.Nodes[i];
                // Far past the branch point — the direct k_z0 loses nothing out here.
                var kz0 = new Complex(0, -Math.Sqrt(kRho * kRho - k0 * k0));
                var (fA, fPhi) = Integrand(substrate, k0, kRho, kz0,
                    poles, c0Phi, c1Phi, rho, substrate.ThicknessMeters);
                double w = Gauss.Weights[i] * half;
                vA += w * fA;
                vPhi += w * fPhi;
            }
        }
        return (vA, vPhi);
    }

    /// <summary>The full remainder integrand at one real k_ρ, including the J₀·k_ρ
    /// transform measure: (F̃ − T̃_images − Σ P̃_poles)·J₀(k_ρρ)·k_ρ.</summary>
    private static (Complex A, Complex Phi) Integrand(SubstrateStackup substrate, double k0,
        double kRho, Complex kz0, IReadOnlyList<SurfaceWavePole> poles,
        Complex c0Phi, Complex c1Phi, double rho, double d)
    {
        var (gA, kPhi) = SpectralKernels.Evaluate(substrate, k0, kRho, kz0);
        var jKz0 = Complex.ImaginaryOne * kz0;
        var x = 2 * jKz0 * d;
        var oneMinusE = OneMinusExpNeg(x);
        var e2 = 1 - oneMinusE; // e^{−2jk_z0 d} without a second Exp call

        var remA = gA - RfConstants.Mu0 * oneMinusE / jKz0;
        var remPhi = kPhi - (c0Phi + c1Phi * e2) / (jKz0 * RfConstants.Eps0);

        for (int p = 0; p < poles.Count; p++)
        {
            var pole = poles[p];
            var denom = kRho * kRho - pole.KRho * pole.KRho;
            var factor = 2 * pole.KRho / denom;
            if (pole.ResidueA != Complex.Zero) remA -= pole.ResidueA * factor;
            remPhi -= pole.ResiduePhi * factor;
        }

        double measure = Bessel.J0(kRho * rho) * kRho;
        return (measure * remA, measure * remPhi);
    }

    /// <summary>1 − e^{−x} without cancellation for small |x| (the k_z0 → 0 branch
    /// point, where the image combination must go to 2jk_z0d·(1+…) smoothly).</summary>
    internal static Complex OneMinusExpNeg(Complex x)
    {
        if (x.Magnitude >= 0.5) return 1 - Complex.Exp(-x);
        // x·(1 − x/2·(1 − x/3·(1 − …))) — the alternating exponential series, nested.
        Complex inner = Complex.One;
        for (int k = 12; k >= 2; k--)
            inner = 1 - x * inner / k;
        return x * inner;
    }
}
