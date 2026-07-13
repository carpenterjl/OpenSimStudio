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
        => ComputeCore(surface, kernel, solution.EdgeCurrents, null, null, thetaCount, phiCount);

    /// <summary>Probe-fed far field: the COMPLETE mixed current, each component once —
    /// the raw horizontal RWG patch currents, the junction's true horizontal current
    /// (the 1/ρ disc + the half-RWG continuations, via <see cref="AttachmentFan.CurrentTransform"/>,
    /// NOT the mesh-scale fold), and the vertical tube current (E_θ only). All three add
    /// COHERENTLY, which is what makes the probe power ledger close — the fold both
    /// over-radiated the half-RWGs and omitted the disc.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredKernelTable kernel,
        ProbeFedSolution probeSolution, ProbeFeed probe, int thetaCount = 32, int phiCount = 64)
    {
        double[] tubeNodes = ProbeAssembly.TubeNodes(kernel.Substrate, probe);
        var leg = new VerticalLeg(probe.X, probe.Y, tubeNodes, probeSolution.TubeCurrents);
        var junction = new JunctionLeg(
            ProbeVertexFan(surface, probe), probeSolution.TubeCurrents[^1]);
        return ComputeCore(surface, kernel, probeSolution.RawEdgeCurrents, junction, leg,
            thetaCount, phiCount);
    }

    /// <summary>Far field of a MULTI-LAYER stackup solve (Stage F): the horizontal RWG patch
    /// currents radiating through the stack, with the region-0 amplitude G̃_A = C and the
    /// W̃ = S/C coupling taken from the transmission-line Green's function
    /// (<see cref="TransmissionLineGreens.RadiationAmplitude"/>) instead of the single-slab
    /// closed form. A covered patch (buried source) just changes C and S — the source depth
    /// is encoded in the table's <see cref="MultiLayerKernelTable.SourceInterface"/>. No probe /
    /// vertical leg here (covered patches are pure horizontal metal); at N = 1 the pattern
    /// equals the single-slab <see cref="Compute(SurfaceStructure, LayeredKernelTable, SurfaceMomSolution, int, int)"/>
    /// to the table-accuracy floor — a cross-check gate.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfaceMomSolution solution, int thetaCount = 32, int phiCount = 64)
    {
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        double k0 = kernel.K0;
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);
        int m = kernel.SourceInterface ?? kernel.Stackup.Layers.Count - 1;
        var edgeCurrents = solution.EdgeCurrents;

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
            var (gA, w) = TransmissionLineGreens.RadiationAmplitude(kernel.Stackup, k0, kRho, kz0, m);
            var thetaFactor = cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w;

            for (int pi = 0; pi < phiCount; pi++)
            {
                var (cosPhi, sinPhi) = (Math.Cos(phi[pi]), Math.Sin(phi[pi]));
                double kx = kRho * cosPhi, ky = kRho * sinPhi;
                var (jx, jy) = SpectralCurrent(surface, edgeCurrents, kx, ky);
                var jPar = cosPhi * jx + sinPhi * jy;
                var jPerp = -sinPhi * jx + cosPhi * jy;

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

    /// <summary>The attachment fan at the probe vertex — rebuilt from the mesh + probe
    /// so the far field carries the junction's exact current geometry.</summary>
    internal static AttachmentFan ProbeVertexFan(SurfaceStructure surface, ProbeFeed probe)
    {
        int vertex = 0;
        double best = double.MaxValue;
        for (int v = 0; v < surface.Vertices.Count; v++)
        {
            double dx = surface.Vertices[v].X - probe.X, dy = surface.Vertices[v].Y - probe.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; vertex = v; }
        }
        return new AttachmentFan(surface, vertex, probe.RadiusMeters);
    }

    /// <summary>The probe tube's contribution to E_θ: its lateral point (X, Y), its node
    /// heights, and the per-node tube currents (index 0 = ground, last = junction).</summary>
    private readonly record struct VerticalLeg(double X, double Y, double[] Nodes, Complex[] Currents);

    /// <summary>The junction's horizontal current: its attachment fan and the solved
    /// junction coefficient it scales.</summary>
    private readonly record struct JunctionLeg(AttachmentFan Fan, Complex Coeff);

    private static FarFieldPattern ComputeCore(SurfaceStructure surface, LayeredKernelTable kernel,
        IReadOnlyList<Complex> edgeCurrents, JunctionLeg? junction, VerticalLeg? vertical,
        int thetaCount, int phiCount)
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

            // Ĝ(θ) is φ-independent — the probe is one lateral point, so its transverse
            // phase factors out per φ. Compute the z′-integral once per θ.
            Complex gHat = vertical is { } vl
                ? VerticalAmplitude(kernel.Substrate, k0, theta[ti], vl.Nodes, vl.Currents)
                : Complex.Zero;

            for (int pi = 0; pi < phiCount; pi++)
            {
                var (cosPhi, sinPhi) = (Math.Cos(phi[pi]), Math.Sin(phi[pi]));
                double kx = kRho * cosPhi, ky = kRho * sinPhi;
                var (jx, jy) = SpectralCurrent(surface, edgeCurrents, kx, ky);
                if (junction is { } jl)
                {
                    var (djx, djy) = jl.Fan.CurrentTransform(surface, kx, ky);
                    jx += jl.Coeff * djx;
                    jy += jl.Coeff * djy;
                }
                var jPar = cosPhi * jx + sinPhi * jy;
                var jPerp = -sinPhi * jx + cosPhi * jy;

                // |E·r| per polarization: ω·(k₀cosθ/4π)·|G̃_A|·|…|.
                double amplitude = omega * k0 * cosTheta / (4 * Math.PI);
                Complex eTheta = amplitude * gA * thetaFactor * jPar;
                Complex ePhi = amplitude * gA * jPerp;
                if (vertical is { } v2)
                {
                    // A_θ from the vertical current: −sinθ·e^{+j k⃗_ρ·ρ_probe}·Ĝ(θ),
                    // same amp/normalization and 1/4π convention as the horizontal leg.
                    var (sinPr, cosPr) = Math.SinCos(kRho * (cosPhi * v2.X + sinPhi * v2.Y));
                    var probePhase = new Complex(cosPr, sinPr);
                    eTheta += amplitude * (-sinTheta) * probePhase * gHat;
                }
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

    /// <summary>The φ-independent vertical spectral amplitude of the probe tube,
    /// Ĝ(θ) = ∫₀^d J_z(z′)·(G̃_A^zz(d,z′) + j·k_z0·G̃_A^xz(d,z′)) dz′, evaluated on the
    /// z = d boundary (the region-0 propagation e^{−jk_z0(z−d)} is the common far-field
    /// factor, dropped here as in the horizontal leg). J_z is the rooftop interpolation
    /// of the node currents; 2-point Gauss per element matches the solver's tube
    /// quadrature. A vertical dipole radiates E_θ = −jω·A_θ with A_θ = −sinθ·Ĝ; at
    /// εr = 1 this reduces to the monopole-over-PEC array factor (a cross-solver gate).</summary>
    internal static Complex VerticalAmplitude(SubstrateStackup substrate, double k0, double theta,
        double[] nodes, Complex[] currents)
    {
        double kRho = k0 * Math.Sin(theta);
        var kz0 = new Complex(k0 * Math.Cos(theta), 0);
        double d = substrate.ThicknessMeters;
        var (gn, gw) = GaussLegendre.Rule(2, 0, 1);
        Complex gHat = Complex.Zero;
        for (int e = 0; e + 1 < nodes.Length; e++)
        {
            double h = nodes[e + 1] - nodes[e];
            for (int q = 0; q < gn.Length; q++)
            {
                double zp = nodes[e] + h * gn[q];
                Complex jz = currents[e] * (1 - gn[q]) + currents[e + 1] * gn[q];
                var (gzz, gxz, _) = VerticalSpectralKernels.Evaluate(
                    substrate, k0, kRho, kz0, d, zp);
                gHat += gw[q] * h * jz * (gzz + Complex.ImaginaryOne * kz0 * gxz);
            }
        }
        return gHat;
    }

    /// <summary>The lateral power carried off by the extracted surface-wave modes
    /// (lossless: exact; lossy slabs damp the mode, and the number reported is the
    /// launched power at the antenna, stated by the assumptions).</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        LayeredKernelTable kernel, SurfaceMomSolution solution, int alphaCount = 64)
        => SurfaceWavePowerWatts(surface, kernel, solution.EdgeCurrents, null, alphaCount);

    /// <summary>Probe-fed surface-wave power from the COMPLETE horizontal current (raw
    /// RWG + junction disc/half-RWGs). The vertical tube's own TM0 launch is a validated
    /// capability (<see cref="VerticalSurfaceWavePowerWatts"/>, gated against the
    /// probe-only oracle to 5e-4), but mixing it into the patch ledger needs the
    /// junction charge-continuity partition (the tube's top-node ∂_zJ_z and the junction
    /// half-RWG divergence are the SAME charge) — a documented open item. At εr = 1 there
    /// is no surface wave and the full far field is exact, so this residual is isolated.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        LayeredKernelTable kernel, ProbeFedSolution probeSolution, ProbeFeed probe,
        int alphaCount = 64)
    {
        var junction = new JunctionLeg(
            ProbeVertexFan(surface, probe), probeSolution.TubeCurrents[^1]);
        return SurfaceWavePowerWatts(surface, kernel, probeSolution.RawEdgeCurrents,
            junction, alphaCount);
    }

    /// <summary>The surface-wave power a PURE vertical tube current launches (no patch):
    /// P_sw = (ωk_p/16π)·2π·Re[∫∫J_z*·Res G̃_A^zz·J_z − ∫∫q_v*·Res K̃_Φ·q_v], q_v =
    /// (j/ω)∂_zJ_z, summed over TM poles (a z-current excites only the axially-symmetric
    /// TM mode). Gated against the probe-only oracle P_in − P_rad (both exact).</summary>
    public static double VerticalSurfaceWavePowerWatts(SubstrateStackup substrate,
        double frequencyHz, double[] tubeNodes, Complex[] tubeCurrents)
    {
        double omega = 2 * Math.PI * frequencyHz;
        var set = new VerticalKernelSet(substrate, frequencyHz);
        double k0 = set.K0;
        var (gn, gw) = GaussLegendre.Rule(4, 0, 1);
        int n = (tubeNodes.Length - 1) * gn.Length;
        var z = new double[n]; var jz = new Complex[n]; var qv = new Complex[n]; var w = new double[n];
        int idx = 0;
        for (int e = 0; e + 1 < tubeNodes.Length; e++)
        {
            double h = tubeNodes[e + 1] - tubeNodes[e];
            Complex slope = (tubeCurrents[e + 1] - tubeCurrents[e]) / h;
            for (int q = 0; q < gn.Length; q++)
            {
                z[idx] = tubeNodes[e] + h * gn[q];
                jz[idx] = tubeCurrents[e] * (1 - gn[q]) + tubeCurrents[e + 1] * gn[q];
                qv[idx] = Complex.ImaginaryOne / omega * slope;
                w[idx] = gw[q] * h;
                idx++;
            }
        }
        double power = 0;
        foreach (var pole in set.Poles)
        {
            if (!pole.IsTm) continue;
            double kp = pole.KRho.Real;
            var pk = new Complex(kp, 0);
            Complex vA = Complex.Zero, vPhi = Complex.Zero;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var res = VerticalSpatialKernels.PoleResidues(substrate, k0, pk, z[i], z[j]);
                    Complex ww = w[i] * w[j];
                    vA += ww * Complex.Conjugate(jz[i]) * res.GAzz * jz[j];
                    vPhi += ww * Complex.Conjugate(qv[i]) * res.KPhi * qv[j];
                }
            power += omega * kp / (16 * Math.PI) * 2 * Math.PI * (vA - vPhi).Real;
        }
        return power;
    }

    private static double SurfaceWavePowerWatts(SurfaceStructure surface,
        LayeredKernelTable kernel, IReadOnlyList<Complex> edgeCurrents,
        JunctionLeg? junction, int alphaCount)
        => SurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles, edgeCurrents,
            junction, alphaCount);

    /// <summary>The lateral surface-wave power carried by the extracted modes of a MULTI-LAYER
    /// stackup (Stage F). Identical spectral-power residue formula as the single slab — the
    /// only thing that changes is the pole set / residues, which the table already carries
    /// (interior-plane residues when it is a covered patch). <see cref="SpectralCurrent"/> is
    /// geometry-only, so it is reused verbatim.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        MultiLayerKernelTable kernel, SurfaceMomSolution solution, int alphaCount = 64)
        => SurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles,
            solution.EdgeCurrents, null, alphaCount);

    private static double SurfaceWavePowerWatts(SurfaceStructure surface,
        double frequencyHz, IReadOnlyList<SurfaceWavePole> poles,
        IReadOnlyList<Complex> edgeCurrents, JunctionLeg? junction, int alphaCount)
    {
        double omega = 2 * Math.PI * frequencyHz;
        double power = 0;
        foreach (var pole in poles)
        {
            double kp = pole.KRho.Real;
            double integralAll = 0, integralRadial = 0;
            for (int i = 0; i < alphaCount; i++)
            {
                double alpha = 2 * Math.PI * i / alphaCount;
                var (sin, cos) = Math.SinCos(alpha);
                var (jx, jy) = SpectralCurrent(surface, edgeCurrents, kp * cos, kp * sin);
                if (junction is { } jl)
                {
                    var (djx, djy) = jl.Fan.CurrentTransform(surface, kp * cos, kp * sin);
                    jx += jl.Coeff * djx;
                    jy += jl.Coeff * djy;
                }
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
        => SpectralCurrent(surface, solution.EdgeCurrents, kx, ky);

    /// <summary>The in-plane transform for an arbitrary edge-current vector (the probe
    /// path passes the RAW edge currents so the junction can be added exactly rather
    /// than through the mesh-scale fan-edge fold).</summary>
    internal static (Complex Jx, Complex Jy) SpectralCurrent(SurfaceStructure surface,
        IReadOnlyList<Complex> edgeCurrents, double kx, double ky)
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
                    Complex coefficient = edgeCurrents[basis]
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
