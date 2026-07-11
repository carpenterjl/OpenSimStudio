using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Far field and surface-wave power for a layered (microstrip) solve — the two legs of
/// the Stage C power ledger P_in = P_rad + P_sw (+ dielectric loss when tanδ &gt; 0).
///
/// FAR FIELD by stationary phase on the EXACT spectral solution: for horizontal
/// currents at the slab surface the region-0 amplitudes are Ãx = G̃_A·J̃x (same for y)
/// and Ãz = −j·(k⃗_ρ·J̃)·W̃·G̃_A, so at the stationary point k_ρ = k₀sinθ
///
///   E_θ ∝ k₀cosθ·G̃_A(k₀sinθ)·(cosθ + j k₀ sin²θ·W̃)·J̃_∥,   E_φ ∝ k₀cosθ·G̃_A·J̃_⊥
///
/// (J̃_∥/J̃_⊥ = the current transform along/across the azimuth). At εr = 1 (W̃ = 0,
/// G̃_A = the image pair) this reduces EXACTLY to Stage B's PEC-image pattern —
/// a hard cross-solver test gate, not a hope.
///
/// SURFACE-WAVE POWER from the spectral power integral P = −½Re⟨E, J*⟩: on the real
/// k_ρ axis of a lossless slab the integrand is real outside k_ρ &lt; k₀ except at the
/// surface-wave poles, whose Sokhotski half-residues contribute the REAL power each
/// mode carries off laterally:
///
///   P_sw = Σ_p [ (ω k_p/16π)·Re(Res_A)·∮|J̃|²dα − (k_p³/16πω)·Re(Res_Φ)·∮|k̂·J̃|²dα ]
///
/// — closed form in the residues the pole finder already computed; the azimuth
/// integral is a trapezoid on a periodic integrand (spectrally accurate). The 16π
/// carries a convention trap worth naming: the table's G̃ is SOMMERFELD-normalized
/// ((1/4π)∫G̃J₀k dk), which is TWICE the plain 2D-Fourier kernel that Parseval's
/// theorem wants — found live as an EXACT factor-2.00000 excess against the circuit
/// power and pinned by the spectral-power identity test.
/// </summary>
public static class LayeredFarField
{
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredKernelTable kernel,
        SurfaceMomSolution solution, int thetaCount = 32, int phiCount = 64)
    {
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        double k0 = kernel.K0;
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);

        var (uNodes, uWeights) = GaussLegendre.Rule(thetaCount, 0, 1); // hemisphere
        var theta = uNodes.Select(Math.Acos).ToArray();
        var phi = Enumerable.Range(0, phiCount).Select(i => 2 * Math.PI * i / phiCount).ToArray();
        double phiWeight = 2 * Math.PI / phiCount;

        var intensity = new double[thetaCount, phiCount];
        double totalPower = 0;
        for (int ti = 0; ti < thetaCount; ti++)
        {
            double cosTheta = uNodes[ti];
            double sinTheta = Math.Sin(theta[ti]);
            double kRho = k0 * sinTheta;
            var kz0 = new Complex(k0 * cosTheta, 0);
            var (gA, _) = SpectralKernels.Evaluate(kernel.Substrate, k0, kRho, kz0);
            var w = SpectralKernels.AzRatio(kernel.Substrate, k0, kRho, kz0);
            var thetaFactor = cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w;

            for (int pi = 0; pi < phiCount; pi++)
            {
                var (cosPhi, sinPhi) = (Math.Cos(phi[pi]), Math.Sin(phi[pi]));
                var (jx, jy) = SpectralCurrent(surface, solution,
                    kRho * cosPhi, kRho * sinPhi);
                var jPar = cosPhi * jx + sinPhi * jy;
                var jPerp = -sinPhi * jx + cosPhi * jy;

                // |E·r| per polarization: ω·(k₀cosθ/4π)·|G̃_A|·|…|.
                double amplitude = omega * k0 * cosTheta / (4 * Math.PI);
                Complex eTheta = amplitude * gA * thetaFactor * jPar;
                Complex ePhi = amplitude * gA * jPerp;
                double u = (eTheta.Magnitude * eTheta.Magnitude
                            + ePhi.Magnitude * ePhi.Magnitude) / (2 * eta);
                intensity[ti, pi] = u;
                totalPower += uWeights[ti] * phiWeight * u;
            }
        }

        double maxDirectivity = 0;
        foreach (double u in intensity)
            maxDirectivity = Math.Max(maxDirectivity, 4 * Math.PI * u / totalPower);
        return new FarFieldPattern(theta, phi, intensity, totalPower, maxDirectivity);
    }

    /// <summary>The lateral power carried off by the extracted surface-wave modes
    /// (lossless: exact; lossy slabs damp the mode, and the number reported is the
    /// launched power at the antenna, stated by the assumptions).</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        LayeredKernelTable kernel, SurfaceMomSolution solution, int alphaCount = 64)
    {
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        double power = 0;
        foreach (var pole in kernel.Poles)
        {
            double kp = pole.KRho.Real;
            double integralAll = 0, integralRadial = 0;
            for (int i = 0; i < alphaCount; i++)
            {
                double alpha = 2 * Math.PI * i / alphaCount;
                var (sin, cos) = Math.SinCos(alpha);
                var (jx, jy) = SpectralCurrent(surface, solution, kp * cos, kp * sin);
                var radial = cos * jx + sin * jy;
                integralAll += jx.Magnitude * jx.Magnitude + jy.Magnitude * jy.Magnitude;
                integralRadial += radial.Magnitude * radial.Magnitude;
            }
            double dAlpha = 2 * Math.PI / alphaCount;
            integralAll *= dAlpha;
            integralRadial *= dAlpha;

            power += omega * kp / (16 * Math.PI) * pole.ResidueA.Real * integralAll
                     - kp * kp * kp / (16 * Math.PI * omega) * pole.ResiduePhi.Real * integralRadial;
        }
        return power;
    }

    /// <summary>J̃(k⃗) = Σ_T ∫ J(r′) e^{+j k⃗·ρ⃗′} dS — the in-plane current transform
    /// (5-point Dunavant per triangle; the phase is slow at λ/10 edges).</summary>
    internal static (Complex Jx, Complex Jy) SpectralCurrent(SurfaceStructure surface,
        SurfaceMomSolution solution, double kx, double ky)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        Complex jxTotal = Complex.Zero, jyTotal = Complex.Zero;
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var supports = surface.TriangleSupports[t];
            if (supports.Count == 0) continue;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];

            for (int i = 0; i < w.Length; i++)
            {
                var point = va * l1[i] + vb * l2[i] + vc * l3[i];
                Complex jx = Complex.Zero, jy = Complex.Zero;
                foreach (var (basis, sign, opposite) in supports)
                {
                    Complex coefficient = solution.EdgeCurrents[basis]
                        * (sign * surface.Edges[basis].Length / (2 * area));
                    var rho = point - surface.Vertices[opposite];
                    jx += coefficient * rho.X;
                    jy += coefficient * rho.Y;
                }
                var (sinP, cosP) = Math.SinCos(kx * point.X + ky * point.Y);
                var phase = new Complex(cosP, sinP);
                double weight = w[i] * area;
                jxTotal += weight * phase * jx;
                jyTotal += weight * phase * jy;
            }
        }
        return (jxTotal, jyTotal);
    }
}
