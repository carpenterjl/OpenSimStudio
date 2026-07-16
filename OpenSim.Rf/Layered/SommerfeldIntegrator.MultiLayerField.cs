using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage S9b — the per-observation-height remainder integrator for the MULTI-LAYER / covered
/// field kernels: the six field kernels (A, W, Φ, ∂zΦ, ∂zA, ∂zW) at one (ρ, z), minus their
/// two per-z quasi-static images (<see cref="MultiLayerFieldKernels.FieldImages"/>) and per-z
/// pole terms. It is the multi-layer sibling of <see cref="SommerfeldIntegrator.FieldRemainder"/>
/// — the SAME contour (k_ρ = k₀ sin t head, √(k₀²+s²) pole-broken panels, geometric + partition
/// tail), only the kernel comes from the TLGF per-z evaluator instead of the single-slab closed
/// form, and the image subtraction uses the two-image G̃_A/K̃_Φ sets. W̃/∂zW̃ carry no image, as
/// in the single-slab field path.
/// </summary>
internal static partial class SommerfeldIntegrator
{
    public static (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        FieldRemainderMultiLayer(LayeredStackup stackup, double k0,
        IReadOnlyList<SurfaceWavePole> poles, int m, double rho, double z, int refinement = 1)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho));
        double zs = stackup.InterfaceHeights()[m];
        if (z < zs) throw new ArgumentOutOfRangeException(nameof(z),
            "Multi-layer field kernels are tabulated for observation at or above the source "
            + "height z ≥ z_s (the map region above the metal).");
        if (refinement < 1) throw new ArgumentOutOfRangeException(nameof(refinement));

        double d = stackup.TotalThicknessMeters;
        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        double k1Real = k0 * Math.Sqrt(epsMax);
        var (gaImages, phiImages) = MultiLayerFieldKernels.FieldImages(stackup, m, z);
        var residues = new (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)[poles.Count];
        for (int p = 0; p < poles.Count; p++)
            residues[p] = MultiLayerFieldKernels.PoleResidues(stackup, k0, poles[p].KRho, poles[p].IsTm, m, z);

        Complex sumA = Complex.Zero, sumW = Complex.Zero;
        Complex sumPhi = Complex.Zero, sumDz = Complex.Zero;
        Complex sumDzA = Complex.Zero, sumDzW = Complex.Zero;

        void Accumulate(double kRho, Complex kz0, double weight)
        {
            var (fA, fW, fPhi, fDz, fDzA, fDzW) = FieldIntegrandML(stackup, k0, kRho, kz0, m, z,
                gaImages, phiImages, poles, residues, rho);
            sumA += weight * fA;
            sumW += weight * fW;
            sumPhi += weight * fPhi;
            sumDz += weight * fDz;
            sumDzA += weight * fDzA;
            sumDzW += weight * fDzW;
        }

        // ---- Segment 1: k_ρ = k₀ sin t (k_z0 = k₀ cos t). ----
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
        double headScale = sumA.Magnitude + sumW.Magnitude + sumPhi.Magnitude + sumDz.Magnitude;
        for (int doubling = 0; doubling < 60 && b * rho < 3; doubling++)
        {
            var (vA, vW, vPhi, vDz, vDzA, vDzW) = FieldTailPanelML(stackup, k0, b, 2 * b, m, z,
                gaImages, phiImages, poles, residues, rho, refinement);
            sumA += vA;
            sumW += vW;
            sumPhi += vPhi;
            sumDz += vDz;
            sumDzA += vDzA;
            sumDzW += vDzW;
            b *= 2;
            if (vA.Magnitude + vW.Magnitude + vPhi.Magnitude + vDz.Magnitude
                < 1e-16 * (headScale + 1e-300))
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
            var partial = new (Complex A, Complex W, Complex Phi, Complex Dz, Complex DzA, Complex DzW)[partitions];
            Complex accA = Complex.Zero, accW = Complex.Zero, accPhi = Complex.Zero, accDz = Complex.Zero;
            Complex accDzA = Complex.Zero, accDzW = Complex.Zero;
            for (int n = 0; n < partitions; n++)
            {
                var (vA, vW, vPhi, vDz, vDzA, vDzW) = FieldTailPanelML(stackup, k0,
                    b + n * delta, b + (n + 1) * delta, m, z, gaImages, phiImages, poles, residues, rho, refinement);
                accA += vA;
                accW += vW;
                accPhi += vPhi;
                accDz += vDz;
                accDzA += vDzA;
                accDzW += vDzW;
                partial[n] = (accA, accW, accPhi, accDz, accDzA, accDzW);
            }
            for (int mm = 1; mm < partitions; mm++)
                for (int i = 0; i < partitions - mm; i++)
                    partial[i] = (0.5 * (partial[i].A + partial[i + 1].A),
                                  0.5 * (partial[i].W + partial[i + 1].W),
                                  0.5 * (partial[i].Phi + partial[i + 1].Phi),
                                  0.5 * (partial[i].Dz + partial[i + 1].Dz),
                                  0.5 * (partial[i].DzA + partial[i + 1].DzA),
                                  0.5 * (partial[i].DzW + partial[i + 1].DzW));
            sumA += partial[0].A;
            sumW += partial[0].W;
            sumPhi += partial[0].Phi;
            sumDz += partial[0].Dz;
            sumDzA += partial[0].DzA;
            sumDzW += partial[0].DzW;
        }

        double norm = 1 / (4 * Math.PI);
        return (norm * sumA, norm * sumW, norm * sumPhi, norm * sumDz, norm * sumDzA, norm * sumDzW);
    }

    private static (Complex A, Complex W, Complex Phi, Complex Dz, Complex DzA, Complex DzW) FieldTailPanelML(
        LayeredStackup stackup, double k0, double lo, double hi, int m, double z,
        MultiLayerImages.Image[] gaImages, MultiLayerImages.Image[] phiImages,
        IReadOnlyList<SurfaceWavePole> poles,
        (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)[] residues,
        double rho, int refinement)
    {
        Complex vA = Complex.Zero, vW = Complex.Zero, vPhi = Complex.Zero, vDz = Complex.Zero;
        Complex vDzA = Complex.Zero, vDzW = Complex.Zero;
        for (int p = 0; p < refinement; p++)
        {
            double x0 = lo + (hi - lo) * p / refinement, x1 = lo + (hi - lo) * (p + 1) / refinement;
            double mid = 0.5 * (x0 + x1), half = 0.5 * (x1 - x0);
            for (int i = 0; i < Gauss.Nodes.Length; i++)
            {
                double kRho = mid + half * Gauss.Nodes[i];
                var kz0 = new Complex(0, -Math.Sqrt(kRho * kRho - k0 * k0));
                var (fA, fW, fPhi, fDz, fDzA, fDzW) = FieldIntegrandML(stackup, k0, kRho, kz0, m, z,
                    gaImages, phiImages, poles, residues, rho);
                double w = Gauss.Weights[i] * half;
                vA += w * fA;
                vW += w * fW;
                vPhi += w * fPhi;
                vDz += w * fDz;
                vDzA += w * fDzA;
                vDzW += w * fDzW;
            }
        }
        return (vA, vW, vPhi, vDz, vDzA, vDzW);
    }

    private static (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW) FieldIntegrandML(
        LayeredStackup stackup, double k0, double kRho, Complex kz0, int m, double z,
        MultiLayerImages.Image[] gaImages, MultiLayerImages.Image[] phiImages,
        IReadOnlyList<SurfaceWavePole> poles,
        (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)[] residues, double rho)
    {
        var (fA, fW, fPhi, fDz, fDzA, fDzW) = MultiLayerFieldKernels.EvaluateAll(stackup, k0, kRho, kz0, m, z);
        var jKz0 = Complex.ImaginaryOne * kz0;

        // Quasi-static image subtraction (dh/dz = +1 throughout — observation above the source).
        Complex imgA = Complex.Zero, imgDzA = Complex.Zero;
        foreach (var img in gaImages)
        {
            var e = Complex.Exp(-jKz0 * img.Depth);
            imgA += img.Coeff * e;
            imgDzA += -img.Coeff * e;       // −c·(dh/dz)·e, dh/dz = +1
        }
        Complex imgPhi = Complex.Zero, imgDz = Complex.Zero;
        foreach (var img in phiImages)
        {
            var e = Complex.Exp(-jKz0 * img.Depth);
            imgPhi += img.Coeff * e;
            imgDz += -img.Coeff * e;
        }
        fA -= RfConstants.Mu0 * imgA / jKz0;
        fPhi -= imgPhi / (jKz0 * RfConstants.Eps0);
        fDz -= imgDz / RfConstants.Eps0;
        fDzA -= RfConstants.Mu0 * imgDzA;

        for (int p = 0; p < poles.Count; p++)
        {
            var factor = 2 * poles[p].KRho / (kRho * kRho - poles[p].KRho * poles[p].KRho);
            fA -= residues[p].A * factor;
            fW -= residues[p].W * factor;
            fPhi -= residues[p].Phi * factor;
            fDz -= residues[p].DzPhi * factor;
            fDzA -= residues[p].DzA * factor;
            fDzW -= residues[p].DzW * factor;
        }

        double measure = Bessel.J0(kRho * rho) * kRho;
        return (measure * fA, measure * fW, measure * fPhi, measure * fDz, measure * fDzA, measure * fDzW);
    }
}
