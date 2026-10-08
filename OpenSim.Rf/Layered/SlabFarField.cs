using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-36 — the far field and surface-wave power of sheet currents on a dielectric stack with no
/// ground (<see cref="MultiLayerKernelTable.IsUngrounded"/>): radiation leaves into BOTH half-spaces.
/// Above, the region-0 amplitudes C and S/C enter exactly as on a grounded stack
/// (<see cref="LayeredFarField"/>); below, the downward amplitudes D and E/D enter the same
/// formula with the stationary-phase factor k₀|cos θ| — the plane-wave expansion of a wave
/// leaving downward is the upward one mirrored — while cos θ itself keeps its sign in the
/// polarization factor (cos θ + jk₀ sin²θ·W), where it is the direction cosine, not a magnitude.
/// The pattern covers the whole sphere: Gauss–Legendre in cos θ on (−1, 1).
/// </summary>
public static class SlabFarField
{
    public static FarFieldPattern Compute(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfaceMomSolution solution, int thetaCount = 48, int phiCount = 64)
    {
        if (!kernel.IsUngrounded) throw new ArgumentException("This far field is for a stack with no ground.", nameof(kernel));
        int m = kernel.SourceInterface ?? kernel.Stackup.Layers.Count - 1;
        double k0 = kernel.K0, omega = 2 * Math.PI * kernel.FrequencyHz;
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);
        var (uNodes, uWeights) = GaussLegendre.Rule(thetaCount, -1, 1);
        var theta = uNodes.Select(Math.Acos).ToArray();
        var phi = Enumerable.Range(0, phiCount).Select(i => 2 * Math.PI * i / phiCount).ToArray();
        double phiWeight = 2 * Math.PI / phiCount;
        var intensity = new double[thetaCount, phiCount];
        double total = 0;
        for (int ti = 0; ti < thetaCount; ti++)
        {
            double cosTheta = uNodes[ti], sinTheta = Math.Sin(theta[ti]);
            double kRho = k0 * sinTheta;
            var kz0 = new Complex(k0 * Math.Abs(cosTheta), 0);
            var (gaUp, wUp, gaDown, wDown) = TransmissionLineGreens.RadiationAmplitudesUngrounded(
                kernel.Stackup, k0, kRho, kz0, m);
            bool up = cosTheta >= 0;
            Complex gA = up ? gaUp : gaDown, w = up ? wUp : wDown;
            var thetaFactor = cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w;
            double amplitude = omega * k0 * Math.Abs(cosTheta) / (4 * Math.PI);
            for (int pi = 0; pi < phiCount; pi++)
            {
                var (sinPhi, cosPhi) = Math.SinCos(phi[pi]);
                var (jx, jy) = LayeredFarField.SpectralCurrent(surface, solution.EdgeCurrents, kRho * cosPhi, kRho * sinPhi);
                var jPar = cosPhi * jx + sinPhi * jy;
                var jPerp = -sinPhi * jx + cosPhi * jy;
                Complex eTheta = amplitude * gA * thetaFactor * jPar;
                Complex ePhi = amplitude * gA * jPerp;
                double u = (eTheta.Magnitude * eTheta.Magnitude + ePhi.Magnitude * ePhi.Magnitude) / (2 * eta);
                intensity[ti, pi] = u;
                total += uWeights[ti] * phiWeight * u;
            }
        }
        double maxDirectivity = 0;
        foreach (double u in intensity) maxDirectivity = Math.Max(maxDirectivity, 4 * Math.PI * u / total);
        return new FarFieldPattern(theta, phi, intensity, total, maxDirectivity);
    }

    /// <summary>The power the slab's bound modes carry away: the grounded stack's residue formula
    /// (ωk_p/16π·Re Res_A·∮|J̃|² − k_p³/16πω·Re Res_Φ·∮|J̃_ρ|²) with the slab's own modes and
    /// residues — the formula reads the mode's power off its residue at the source plane, which
    /// does not care what bounds the stack.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfaceMomSolution solution, int alphaCount = 64)
    {
        if (!kernel.IsUngrounded) throw new ArgumentException("This ledger is for a stack with no ground.", nameof(kernel));
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        double power = 0;
        foreach (var pole in kernel.Poles)
        {
            double kp = pole.KRho.Real;
            double all = 0, radial = 0;
            for (int i = 0; i < alphaCount; i++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * i / alphaCount);
                var (jx, jy) = LayeredFarField.SpectralCurrent(surface, solution.EdgeCurrents, kp * cos, kp * sin);
                var r = cos * jx + sin * jy;
                all += jx.Magnitude * jx.Magnitude + jy.Magnitude * jy.Magnitude;
                radial += r.Magnitude * r.Magnitude;
            }
            double dAlpha = 2 * Math.PI / alphaCount;
            power += omega * kp / (16 * Math.PI) * pole.ResidueA.Real * all * dAlpha
                     - kp * kp * kp / (16 * Math.PI * omega) * pole.ResiduePhi.Real * radial * dAlpha;
        }
        return power;
    }
}
