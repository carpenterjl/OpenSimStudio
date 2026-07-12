using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The per-(z, z′) remainder integrals behind the probe-feed (vertical current)
/// kernels: (G_A^zz, G_A^xz, K_Φ) at one lateral ρ with source and observation both
/// inside the slab, minus the per-(z, z′) quasi-static images
/// (<see cref="VerticalSpatialKernels.Images"/>) and pole terms. Same branch-safe
/// path, same panel rules, same partition–extrapolation tail as the z = d and Stage D
/// field machinery — kept as a SEPARATE method so the pinned boundary and field paths
/// are never re-touched. G_A^xz gets no image extraction (1/k_ρ² decay, the W̃
/// precedent). The full spatial kernels are
///
///   remainder + Σ_images coeff·e^{−jk₀R_h}/(4πR_h) + Σ_poles Res·(−j/4π·¼·4π)…
///
/// composed by <see cref="VerticalKernelSet"/>, which owns the closed-form add-backs.
/// </summary>
internal static partial class SommerfeldIntegrator
{
    public static (Complex GAzz, Complex GAxz, Complex KPhi) VerticalRemainder(
        SubstrateStackup substrate, double k0, IReadOnlyList<SurfaceWavePole> poles,
        double rho, double z, double zPrime, int refinement = 1)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho),
            "The vertical remainder needs a positive lateral distance — probe self terms use the reduced ρ_eff = √(ρ² + a²) ≥ a.");
        double d = substrate.ThicknessMeters;
        if (z < 0 || z > d || zPrime < 0 || zPrime > d)
            throw new ArgumentOutOfRangeException(nameof(z),
                "Vertical-current kernels live inside the slab — both heights must be in [0, d].");
        if (refinement < 1) throw new ArgumentOutOfRangeException(nameof(refinement));

        var images = VerticalSpatialKernels.Images(substrate, z, zPrime);
        var residues = new (Complex GAzz, Complex GAxz, Complex KPhi)[poles.Count];
        for (int p = 0; p < poles.Count; p++)
            residues[p] = VerticalSpatialKernels.PoleResidues(substrate, k0, poles[p].KRho, z, zPrime);
        double k1Real = k0 * Math.Sqrt(substrate.RelativePermittivity);

        Complex sumZz = Complex.Zero, sumXz = Complex.Zero, sumPhi = Complex.Zero;

        void Accumulate(double kRho, Complex kz0, double weight)
        {
            var (fZz, fXz, fPhi) = VerticalIntegrand(substrate, k0, kRho, kz0, z, zPrime,
                images, poles, residues, rho);
            sumZz += weight * fZz;
            sumXz += weight * fXz;
            sumPhi += weight * fPhi;
        }

        // ---- Segment 1: k_ρ = k₀ sin t (k_z0 = k₀ cos t in closed form). ----
        int n1 = refinement * (4 + (int)Math.Ceiling(k0 * rho / Math.PI));
        for (int p = 0; p < n1; p++)
        {
            double t0 = Math.PI / 2 * p / n1, t1 = Math.PI / 2 * (p + 1) / n1;
            double mid = 0.5 * (t0 + t1), half = 0.5 * (t1 - t0);
            for (int i = 0; i < Gauss.Nodes.Length; i++)
            {
                double t = mid + half * Gauss.Nodes[i];
                var (sin, cos) = Math.SinCos(t);
                Accumulate(k0 * sin, k0 * cos, Gauss.Weights[i] * half * k0 * cos);
            }
        }

        // ---- Segment 2: k_ρ = √(k₀² + s²), panels broken at poles. ----
        // Exponential reach: the extraction leaves e^{−k_ρ·2d}-scale content at worst
        // (the truncated 2d family), plus the k_z1-vs-k_z0 flavor residual that decays
        // algebraically — keep the 6/d head reach and let the tail extrapolation carry
        // the algebraic part, exactly like the Stage D field path.
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
                    Accumulate(kRho, new Complex(0, -s), Gauss.Weights[i] * half * s / kRho);
                }
            }
        }

        // ---- Tail: geometric doubling, then partition–extrapolation. ----
        double b = a;
        double headScale = sumZz.Magnitude + sumXz.Magnitude + sumPhi.Magnitude;
        for (int doubling = 0; doubling < 60 && b * rho < 3; doubling++)
        {
            var (vZz, vXz, vPhi) = VerticalTailPanel(substrate, k0, b, 2 * b, z, zPrime,
                images, poles, residues, rho, refinement);
            sumZz += vZz;
            sumXz += vXz;
            sumPhi += vPhi;
            b *= 2;
            if (vZz.Magnitude + vXz.Magnitude + vPhi.Magnitude < 1e-16 * (headScale + 1e-300))
            {
                b = double.PositiveInfinity;
                break;
            }
        }
        if (!double.IsPositiveInfinity(b))
        {
            double delta = Math.PI / rho;
            // Floor 24 (vs the field path's 12): junction-adjacent pairs put remainder
            // content out to k_ρ ~ 1/(2d−z−z′) ≫ the 6/d head reach, and at tube-scale
            // ρ the whole burden lands on this extrapolation — 12 partitions measured
            // 4.7e-8 on G_zz at z = z′ = 0.95d, 24 lands ~1e-12 (probe-measured).
            int partitions = Math.Min(400 * refinement,
                24 * refinement + (int)Math.Ceiling(6.0 / d / delta));
            var partial = new (Complex Zz, Complex Xz, Complex Phi)[partitions];
            Complex accZz = Complex.Zero, accXz = Complex.Zero, accPhi = Complex.Zero;
            for (int n = 0; n < partitions; n++)
            {
                var (vZz, vXz, vPhi) = VerticalTailPanel(substrate, k0,
                    b + n * delta, b + (n + 1) * delta, z, zPrime, images, poles, residues,
                    rho, refinement);
                accZz += vZz;
                accXz += vXz;
                accPhi += vPhi;
                partial[n] = (accZz, accXz, accPhi);
            }
            for (int m = 1; m < partitions; m++)
                for (int i = 0; i < partitions - m; i++)
                    partial[i] = (0.5 * (partial[i].Zz + partial[i + 1].Zz),
                                  0.5 * (partial[i].Xz + partial[i + 1].Xz),
                                  0.5 * (partial[i].Phi + partial[i + 1].Phi));
            sumZz += partial[0].Zz;
            sumXz += partial[0].Xz;
            sumPhi += partial[0].Phi;
        }

        double norm = 1 / (4 * Math.PI);
        return (norm * sumZz, norm * sumXz, norm * sumPhi);
    }

    private static (Complex Zz, Complex Xz, Complex Phi) VerticalTailPanel(
        SubstrateStackup substrate, double k0, double lo, double hi, double z, double zPrime,
        VerticalSpatialKernels.KernelImage[] images, IReadOnlyList<SurfaceWavePole> poles,
        (Complex GAzz, Complex GAxz, Complex KPhi)[] residues, double rho, int refinement)
    {
        Complex vZz = Complex.Zero, vXz = Complex.Zero, vPhi = Complex.Zero;
        for (int p = 0; p < refinement; p++)
        {
            double x0 = lo + (hi - lo) * p / refinement, x1 = lo + (hi - lo) * (p + 1) / refinement;
            double mid = 0.5 * (x0 + x1), half = 0.5 * (x1 - x0);
            for (int i = 0; i < Gauss.Nodes.Length; i++)
            {
                double kRho = mid + half * Gauss.Nodes[i];
                var kz0 = new Complex(0, -Math.Sqrt(kRho * kRho - k0 * k0));
                var (fZz, fXz, fPhi) = VerticalIntegrand(substrate, k0, kRho, kz0, z, zPrime,
                    images, poles, residues, rho);
                double w = Gauss.Weights[i] * half;
                vZz += w * fZz;
                vXz += w * fXz;
                vPhi += w * fPhi;
            }
        }
        return (vZz, vXz, vPhi);
    }

    private static (Complex Zz, Complex Xz, Complex Phi) VerticalIntegrand(
        SubstrateStackup substrate, double k0, double kRho, Complex kz0, double z, double zPrime,
        VerticalSpatialKernels.KernelImage[] images, IReadOnlyList<SurfaceWavePole> poles,
        (Complex GAzz, Complex GAxz, Complex KPhi)[] residues, double rho)
    {
        // Dielectric-side branch: the extraction (images, pole residues) is in-slab
        // flavored, and G̃_zz is two-sided at z = d — mixing sides leaves a
        // non-decaying remainder (found live by the self-convergence gate at z = d).
        var (fZz, fXz, fPhi) = VerticalSpectralKernels.EvaluateDielectricSide(
            substrate, k0, kRho, kz0, z, zPrime);
        var jKz0 = Complex.ImaginaryOne * kz0;

        Complex imgZz = Complex.Zero, imgPhi = Complex.Zero;
        for (int m = 0; m < images.Length; m++)
        {
            var e = Complex.Exp(-jKz0 * images[m].Height);
            imgZz += images[m].CoefficientGAzz * e;
            imgPhi += images[m].CoefficientKPhi * e;
        }
        fZz -= RfConstants.Mu0 * imgZz / jKz0;
        fPhi -= imgPhi / (jKz0 * RfConstants.Eps0);

        for (int p = 0; p < poles.Count; p++)
        {
            var factor = 2 * poles[p].KRho / (kRho * kRho - poles[p].KRho * poles[p].KRho);
            fZz -= residues[p].GAzz * factor;
            fXz -= residues[p].GAxz * factor;
            fPhi -= residues[p].KPhi * factor;
        }

        double measure = Bessel.J0(kRho * rho) * kRho;
        return (measure * fZz, measure * fXz, measure * fPhi);
    }
}
