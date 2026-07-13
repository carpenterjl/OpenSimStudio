using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The Stage F (multi-layer) counterpart of the single-slab remainder integrator: direct
/// Sommerfeld integration of G̃_A and K̃_Φ minus (a) the multi-layer quasi-static image
/// series (<see cref="MultiLayerImages"/>) and (b) the surface-wave pole terms, leaving a
/// remainder regular at ρ = 0 that decays algebraically. The kernel comes from the general
/// transmission-line Green's function (<see cref="TransmissionLineGreens"/>); the contour,
/// pole-broken panels, and partition–extrapolation tail are exactly the single-slab
/// integrator's (this shares its <c>Gauss</c> rule), so the two agree at N = 1.
///
/// Only the two POTENTIAL kernels are carried here (the voltage kernel K_V and the vertical
/// kernels are single-slab features whose multi-layer versions are separate follow-ups).
/// The image asymptote is subtracted RAW — Σ c_m e^{−jk_z0 D_m}/(jk_z0) — matched term for
/// term to the spatial images Σ c_m g(√(ρ²+D_m²)) the table adds back; the image
/// coefficients sum to zero (the static kernel vanishes as k_ρ → 0), so the 1/(jk_z0)
/// branch-point behaviour cancels and no cancellation-safe rewrite is needed at the
/// quadrature nodes (k_z0 never reaches the exact branch point on the open rule).
/// </summary>
internal static partial class SommerfeldIntegrator
{
    /// <summary>The remainder integrals for both potential kernels of a multi-layer stackup
    /// at one lateral distance, source & observation at the TOP metal plane. <paramref
    /// name="gaImages"/> / <paramref name="phiImages"/> are the pre-generated static image
    /// series (frequency-independent, built once per stackup); <paramref name="poles"/> are the
    /// TLGF surface-wave poles.</summary>
    public static (Complex A, Complex Phi) RemainderMultiLayer(
        LayeredStackup stackup, double k0, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<MultiLayerImages.Image> gaImages,
        IReadOnlyList<MultiLayerImages.Image> phiImages, double rho, int refinement = 1) =>
        // Top source: the kernel is the pinned TransmissionLineGreens.Evaluate — the delegate
        // wraps that EXACT call, so this path stays bitwise the pre-F2b remainder.
        RemainderMultiLayerCore(stackup, k0, poles, gaImages, phiImages, rho, refinement,
            (kRho, kz0) => TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0));

    /// <summary>The INTERIOR-source remainder (covered patch): source & observation at
    /// interface <paramref name="m"/>. Same contour / pole-broken panels / partition tail —
    /// only the spectral kernel switches to <see cref="TransmissionLineGreens.EvaluateInterior"/>,
    /// and the caller supplies the interior image series (<see cref="MultiLayerImages.PhiImagesInterior"/>
    /// / <see cref="MultiLayerImages.GaImagesInterior"/>) and interior-plane pole residues.</summary>
    public static (Complex A, Complex Phi) RemainderMultiLayerInterior(
        LayeredStackup stackup, double k0, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<MultiLayerImages.Image> gaImages,
        IReadOnlyList<MultiLayerImages.Image> phiImages, double rho, int m, int refinement = 1) =>
        RemainderMultiLayerCore(stackup, k0, poles, gaImages, phiImages, rho, refinement,
            (kRho, kz0) => TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m));

    private static (Complex A, Complex Phi) RemainderMultiLayerCore(
        LayeredStackup stackup, double k0, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<MultiLayerImages.Image> gaImages,
        IReadOnlyList<MultiLayerImages.Image> phiImages, double rho, int refinement,
        Func<double, Complex, (Complex GA, Complex KPhi)> kernel)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho),
            "The remainder is tabulated for ρ > 0 (the ρ → 0 limit is flat on the scale of d).");
        if (refinement < 1) throw new ArgumentOutOfRangeException(nameof(refinement));

        double d = stackup.TotalThicknessMeters;
        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        double k1Real = k0 * Math.Sqrt(epsMax);

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
                var (fA, fPhi) = IntegrandML(k0, k0 * sin, k0 * cos,
                    poles, gaImages, phiImages, rho, kernel);
                double w = Gauss.Weights[i] * half * k0 * cos;
                sumA += w * fA;
                sumPhi += w * fPhi;
            }
        }

        // ---- Segment 2: k_ρ = √(k₀² + s²), k_z0 = −js, panels broken at poles. ----
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
                    var (fA, fPhi) = IntegrandML(k0, kRho, new Complex(0, -s),
                        poles, gaImages, phiImages, rho, kernel);
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
            var (vA, vPhi) = TailPanelML(k0, b, 2 * b, poles, gaImages, phiImages, rho, refinement, kernel);
            sumA += vA;
            sumPhi += vPhi;
            b *= 2;
            if (vA.Magnitude < 1e-16 * (l1A + 1e-300) && vPhi.Magnitude < 1e-16 * (l1Phi + 1e-300))
            {
                b = double.PositiveInfinity;
                break;
            }
        }
        if (!double.IsPositiveInfinity(b))
        {
            double delta = Math.PI / rho;
            int partitions = Math.Min(400 * refinement,
                12 * refinement + (int)Math.Ceiling(6.0 / d / delta));
            var partialA = new Complex[partitions];
            var partialPhi = new Complex[partitions];
            Complex accA = Complex.Zero, accPhi = Complex.Zero;
            for (int n = 0; n < partitions; n++)
            {
                var (vA, vPhi) = TailPanelML(k0, b + n * delta, b + (n + 1) * delta,
                    poles, gaImages, phiImages, rho, refinement, kernel);
                accA += vA;
                accPhi += vPhi;
                partialA[n] = accA;
                partialPhi[n] = accPhi;
            }
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

    private static (Complex A, Complex Phi) TailPanelML(double k0,
        double lo, double hi, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<MultiLayerImages.Image> gaImages,
        IReadOnlyList<MultiLayerImages.Image> phiImages, double rho, int refinement,
        Func<double, Complex, (Complex GA, Complex KPhi)> kernel)
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
                var kz0 = new Complex(0, -Math.Sqrt(kRho * kRho - k0 * k0));
                var (fA, fPhi) = IntegrandML(k0, kRho, kz0, poles, gaImages, phiImages, rho, kernel);
                double w = Gauss.Weights[i] * half;
                vA += w * fA;
                vPhi += w * fPhi;
            }
        }
        return (vA, vPhi);
    }

    /// <summary>The multi-layer remainder integrand at one real k_ρ, including the J₀·k_ρ
    /// measure: (F̃ − Σ image asymptotes − Σ pole terms)·J₀(k_ρρ)·k_ρ. The image asymptote
    /// is Σ Coeff·e^{−jk_z0 D}/(jk_z0), scaled by µ₀ for G̃_A and 1/ε₀ for K̃_Φ.</summary>
    private static (Complex A, Complex Phi) IntegrandML(double k0,
        double kRho, Complex kz0, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<MultiLayerImages.Image> gaImages,
        IReadOnlyList<MultiLayerImages.Image> phiImages, double rho,
        Func<double, Complex, (Complex GA, Complex KPhi)> kernel)
    {
        var (gA, kPhi) = kernel(kRho, kz0);
        var jKz0 = Complex.ImaginaryOne * kz0;

        Complex asymA = Complex.Zero;
        foreach (var img in gaImages)
            asymA += img.Coeff * Complex.Exp(-jKz0 * img.Depth) / jKz0;
        Complex asymPhi = Complex.Zero;
        foreach (var img in phiImages)
            asymPhi += img.Coeff * Complex.Exp(-jKz0 * img.Depth) / jKz0;

        var remA = gA - RfConstants.Mu0 * asymA;
        var remPhi = kPhi - asymPhi / RfConstants.Eps0;

        foreach (var pole in poles)
        {
            var factor = 2 * pole.KRho / (kRho * kRho - pole.KRho * pole.KRho);
            if (pole.ResidueA != Complex.Zero) remA -= pole.ResidueA * factor;
            remPhi -= pole.ResiduePhi * factor;
        }

        double measure = Bessel.J0(kRho * rho) * kRho;
        return (measure * remA, measure * remPhi);
    }
}
