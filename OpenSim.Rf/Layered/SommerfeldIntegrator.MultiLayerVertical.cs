using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage C1 (spatial) — the per-(z, z′) remainder integrator for the MULTI-LAYER vertical-current
/// kernels: (G_A^zz, G_A^xz, K_Φ) at one lateral ρ, minus their two quasi-static images
/// (<see cref="MultiLayerVerticalImages.Images"/>) and their per-(z, z′) pole terms
/// (<see cref="TransmissionLineGreens.PoleVerticalResidues"/>).
///
/// <para>It is the N-layer sibling of <see cref="SommerfeldIntegrator.VerticalRemainder"/> and
/// shares its contour — the k_ρ = k₀ sin t head, the √(k₀²+s²) pole-broken panels, the geometric
/// doubling and the Michalski partition–extrapolation tail — because the integrand has the same
/// character: an exponentially decaying remainder riding a J₀ oscillation. Two things differ. The
/// kernel comes from the TLGF assembly rather than the single-slab closed form; and the HEAD REACH
/// is scaled by the geometry rather than by the stack thickness, because with a minimal image set
/// the smallest un-extracted image is the nearest-interface reflection, whose height collapses as
/// a pair approaches an interface (see the reach comment below — it is measured). The pinned
/// single-slab path is never re-touched, exactly as the S9b field remainder kept its own.</para>
///
/// <para>The tail budget follows the single-slab vertical path rather than the field path (24
/// partitions rather than 12): junction-adjacent pairs put remainder content out to k_ρ ~
/// 1/(z + z′), and at tube-scale ρ the whole burden lands on the extrapolation.</para>
/// </summary>
internal static partial class SommerfeldIntegrator
{
    public static (Complex GAzz, Complex GAxz, Complex KPhi) VerticalRemainderMultiLayer(
        LayeredStackup stackup, double k0, IReadOnlyList<SurfaceWavePole> poles,
        double rho, double z, double zPrime, int refinement = 1)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho),
            "The vertical remainder needs a positive lateral distance — probe self terms use the "
            + "reduced ρ_eff = √(ρ² + a²) ≥ a.");
        double d = stackup.TotalThicknessMeters;
        if (z < 0 || z > d || zPrime < 0 || zPrime > d)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"Vertical-current kernels live inside the stack — both heights must be in [0, {d}].");
        if (refinement < 1) throw new ArgumentOutOfRangeException(nameof(refinement));

        // The stack splits depend only on (z, z′), so they are derived once here rather than a
        // thousand times inside the integrand — same objects, same arithmetic, gated bitwise.
        var geometry = TransmissionLineGreens.PrepareVertical(stackup, z, zPrime);
        var images = MultiLayerVerticalImages.Images(stackup, z, zPrime);
        var residues = new (Complex GAzz, Complex GAxz, Complex KPhi)[poles.Count];
        for (int p = 0; p < poles.Count; p++)
            residues[p] = TransmissionLineGreens.PoleVerticalResidues(
                stackup, k0, poles[p].KRho, poles[p].IsTm, z, zPrime);
        double k1Real = k0 * Math.Sqrt(stackup.Layers.Max(l => l.RelativePermittivity));

        Complex sumZz = Complex.Zero, sumXz = Complex.Zero, sumPhi = Complex.Zero;

        void Accumulate(double kRho, Complex kz0, double weight)
        {
            var (fZz, fXz, fPhi) = IntegrandML(geometry, k0, kRho, kz0, z, zPrime,
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
        // The head must reach past the SMALLEST UN-EXTRACTED image, and with a minimal image set
        // that height is a property of the geometry rather than of the stack: it is the nearest
        // interface reflection, min over interfaces of |h − z| + |h − z′|, which collapses toward
        // zero as a pair approaches an interface (the tube’s top element against the patch plane
        // — the case the single-slab set names as critical). Scaling the reach by that height
        // rather than by the stack thickness leaves the SAME residual amplitude at the head
        // boundary as the shipped single-slab path, whose 6/d reach against its own smallest
        // un-extracted height of 2d is an exponent of 12. Measured: a fixed 6/d floors a near-top
        // pair at 1e-9 while mid-stack pairs reach 1e-12; this holds 1e-12 across both. When the
        // pair sits ON an interface the height vanishes, the reach is capped at 3π/ρ as always,
        // and the un-extracted term becomes a pure J₀ oscillation — exactly what the
        // partition–extrapolation tail exists to sum.
        double hMin = double.PositiveInfinity;
        foreach (double h in stackup.InterfaceHeights())
            hMin = Math.Min(hMin, Math.Abs(h - z) + Math.Abs(h - zPrime));
        double a = Math.Max(2 * k1Real,
            k1Real + Math.Min(12.0 * refinement / hMin, 3 * Math.PI / rho));
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
            var (vZz, vXz, vPhi) = TailPanelML(geometry, k0, b, 2 * b, z, zPrime,
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
            int partitions = Math.Min(400 * refinement,
                24 * refinement + (int)Math.Ceiling(6.0 / d / delta));
            var partial = new (Complex Zz, Complex Xz, Complex Phi)[partitions];
            Complex accZz = Complex.Zero, accXz = Complex.Zero, accPhi = Complex.Zero;
            for (int nn = 0; nn < partitions; nn++)
            {
                var (vZz, vXz, vPhi) = TailPanelML(geometry, k0,
                    b + nn * delta, b + (nn + 1) * delta, z, zPrime, images, poles, residues,
                    rho, refinement);
                accZz += vZz;
                accXz += vXz;
                accPhi += vPhi;
                partial[nn] = (accZz, accXz, accPhi);
            }
            for (int mm = 1; mm < partitions; mm++)
                for (int i = 0; i < partitions - mm; i++)
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

    private static (Complex Zz, Complex Xz, Complex Phi) TailPanelML(
        TransmissionLineGreens.VerticalGeometry geometry, double k0, double lo, double hi,
        double z, double zPrime,
        MultiLayerVerticalImages.KernelImage[] images, IReadOnlyList<SurfaceWavePole> poles,
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
                var (fZz, fXz, fPhi) = IntegrandML(geometry, k0, kRho, kz0, z, zPrime,
                    images, poles, residues, rho);
                double w = Gauss.Weights[i] * half;
                vZz += w * fZz;
                vXz += w * fXz;
                vPhi += w * fPhi;
            }
        }
        return (vZz, vXz, vPhi);
    }

    private static (Complex Zz, Complex Xz, Complex Phi) IntegrandML(
        TransmissionLineGreens.VerticalGeometry geometry, double k0, double kRho, Complex kz0,
        double z, double zPrime,
        MultiLayerVerticalImages.KernelImage[] images, IReadOnlyList<SurfaceWavePole> poles,
        (Complex GAzz, Complex GAxz, Complex KPhi)[] residues, double rho)
    {
        var (fZz, fXz, fPhi) = TransmissionLineGreens.EvaluateVertical(
            geometry, k0, new Complex(kRho, 0), kz0, z, zPrime);
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
