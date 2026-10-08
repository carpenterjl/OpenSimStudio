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
        => ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            solution.EdgeCurrents, Array.Empty<JunctionLeg>(), Array.Empty<VerticalLeg>(), thetaCount, phiCount);

    /// <summary>Probe-fed far field: the COMPLETE mixed current, each component once —
    /// the raw horizontal RWG patch currents, the junction's true horizontal current
    /// (the 1/ρ disc + the half-RWG continuations, via <see cref="AttachmentFan.CurrentTransform"/>,
    /// NOT the mesh-scale fold), and the vertical tube current (E_θ only). All three add
    /// COHERENTLY, which is what makes the probe power ledger close — the fold both
    /// over-radiated the half-RWGs and omitted the disc.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredKernelTable kernel,
        ProbeFedSolution probeSolution, ProbeFeed probe, int thetaCount = 32, int phiCount = 64)
    {
        double[] tubeNodes = probeSolution.TubeNodes;
        var leg = new VerticalLeg(probe.X, probe.Y, tubeNodes, probeSolution.TubeCurrents);
        var junction = new JunctionLeg(
            ProbeVertexFan(surface, probe), probeSolution.TubeCurrents[^1]);
        return ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            probeSolution.RawEdgeCurrents, new[] { junction }, new[] { leg }, thetaCount, phiCount);
    }

    /// <summary>Far field of a pin array (FU-35): the sheet currents, every pin's junction and every
    /// pin's tube, all coherently, for the excitation the currents belong to.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredKernelTable kernel,
        PinArrayCurrents currents, IReadOnlyList<VerticalPin> pins, int thetaCount = 32, int phiCount = 64)
    {
        var (junctions, legs) = PinLegs(surface, currents, pins);
        return ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            currents.RawEdgeCurrents, junctions, legs, thetaCount, phiCount);
    }

    /// <summary>The pin array's far field over a multi-layer stackup.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, MultiLayerKernelTable kernel,
        PinArrayCurrents currents, IReadOnlyList<VerticalPin> pins, int thetaCount = 32, int phiCount = 64)
    {
        var (junctions, legs) = PinLegs(surface, currents, pins);
        return ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            currents.RawEdgeCurrents, junctions, legs, thetaCount, phiCount);
    }

    private static (JunctionLeg[] Junctions, VerticalLeg[] Legs) PinLegs(SurfaceStructure surface,
        PinArrayCurrents currents, IReadOnlyList<VerticalPin> pins)
    {
        if (pins.Count != currents.TubeCurrents.Length)
            throw new ArgumentException("The pins are not the ones the currents belong to.", nameof(pins));
        var junctions = new JunctionLeg[pins.Count];
        var legs = new VerticalLeg[pins.Count];
        for (int p = 0; p < pins.Count; p++)
        {
            junctions[p] = new JunctionLeg(ProbeVertexFan(surface, pins[p].Geometry), currents.TubeCurrents[p][^1]);
            legs[p] = new VerticalLeg(pins[p].Geometry.X, pins[p].Geometry.Y, currents.TubeNodes[p], currents.TubeCurrents[p]);
        }
        return (junctions, legs);
    }

    /// <summary>Far field of a MULTI-LAYER stackup solve (Stage F): the horizontal RWG patch
    /// currents radiating through the stack, with the region-0 amplitude G̃_A = C and the
    /// W̃ = S/C coupling taken from the transmission-line Green's function
    /// (<see cref="TransmissionLineGreens.RadiationAmplitude"/>) instead of the single-slab
    /// closed form. A covered patch (buried source) just changes C and S — the source depth
    /// is encoded in the table's <see cref="MultiLayerKernelTable.SourceInterface"/>. This is the
    /// horizontal-metal-only overload; a probe adds its junction and tube legs through the
    /// ProbeFedSolution overload below. At N = 1 the pattern equals the single-slab
    /// <see cref="Compute(SurfaceStructure, LayeredKernelTable, SurfaceMomSolution, int, int)"/>
    /// to the table-accuracy floor — a cross-check gate.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfaceMomSolution solution, int thetaCount = 32, int phiCount = 64)
        => ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            solution.EdgeCurrents, Array.Empty<JunctionLeg>(), Array.Empty<VerticalLeg>(), thetaCount, phiCount);

    /// <summary>Stage C2 — the probe-fed far field of a MULTI-LAYER stackup: the same complete
    /// mixed current as the single-slab probe overload (raw RWG + the junction's exact transform +
    /// the vertical tube), radiated through the TLGF's region-0 amplitudes. A covered patch's tube
    /// ends on the buried metal, but the wave still leaves from the TOP of the stack, which is
    /// where the vertical amplitude is read.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, MultiLayerKernelTable kernel,
        ProbeFedSolution probeSolution, ProbeFeed probe, int thetaCount = 32, int phiCount = 64)
    {
        var leg = new VerticalLeg(probe.X, probe.Y, probeSolution.TubeNodes,
            probeSolution.TubeCurrents);
        var junction = new JunctionLeg(
            ProbeVertexFan(surface, probe), probeSolution.TubeCurrents[^1]);
        return ComputeCore(surface, kernel.FrequencyHz, kernel.K0, Medium(kernel),
            probeSolution.RawEdgeCurrents, new[] { junction }, new[] { leg }, thetaCount, phiCount);
    }

    /// <summary>How a medium turns a source-plane current into a region-0 radiation amplitude:
    /// (G̃_A, W̃) at one spectral point, and the vertical tube's φ-independent amplitude Ĝ(θ).
    /// Naming that dependency is what lets ONE <see cref="ComputeCore"/> serve the single slab's
    /// closed forms and the multi-layer TLGF — and it is why the N = 1 far-field identity is a
    /// statement about the media rather than about two hand-aligned quadratures.</summary>
    private readonly record struct RadiationMedium(
        Func<double, Complex, (Complex GA, Complex W)> Amplitude,
        Func<double, double[], Complex[], Complex> VerticalAmplitude);

    private static RadiationMedium Medium(LayeredKernelTable kernel) => new(
        (kRho, kz0) =>
        {
            var (gA, _) = SpectralKernels.Evaluate(kernel.Substrate, kernel.K0, kRho, kz0);
            return (gA, SpectralKernels.AzRatio(kernel.Substrate, kernel.K0, kRho, kz0));
        },
        (theta, nodes, currents) =>
            VerticalAmplitude(kernel.Substrate, kernel.K0, theta, nodes, currents));

    private static RadiationMedium Medium(MultiLayerKernelTable kernel)
    {
        int m = kernel.SourceInterface ?? kernel.Stackup.Layers.Count - 1;
        return new RadiationMedium(
            (kRho, kz0) =>
                TransmissionLineGreens.RadiationAmplitude(kernel.Stackup, kernel.K0, kRho, kz0, m),
            (theta, nodes, currents) =>
                VerticalAmplitude(kernel.Stackup, kernel.K0, theta, nodes, currents));
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

    private static FarFieldPattern ComputeCore(SurfaceStructure surface, double frequencyHz,
        double k0, in RadiationMedium medium, IReadOnlyList<Complex> edgeCurrents,
        IReadOnlyList<JunctionLeg> junctions, IReadOnlyList<VerticalLeg> verticals, int thetaCount, int phiCount)
    {
        double omega = 2 * Math.PI * frequencyHz;
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
            var (gA, w) = medium.Amplitude(kRho, kz0);
            var thetaFactor = cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w;

            // Ĝ(θ) is φ-independent — each tube is one lateral point, so its transverse
            // phase factors out per φ. Compute its z′-integral once per θ.
            var gHats = new Complex[verticals.Count];
            for (int v = 0; v < verticals.Count; v++)
                gHats[v] = medium.VerticalAmplitude(theta[ti], verticals[v].Nodes, verticals[v].Currents);

            for (int pi = 0; pi < phiCount; pi++)
            {
                var (cosPhi, sinPhi) = (Math.Cos(phi[pi]), Math.Sin(phi[pi]));
                double kx = kRho * cosPhi, ky = kRho * sinPhi;
                var (jx, jy) = SpectralCurrent(surface, edgeCurrents, kx, ky);
                foreach (var jl in junctions)
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
                for (int v = 0; v < verticals.Count; v++)
                {
                    // A_θ from the vertical current: −sinθ·e^{+j k⃗_ρ·ρ_probe}·Ĝ(θ),
                    // same amp/normalization and 1/4π convention as the horizontal leg.
                    var v2 = verticals[v];
                    var (sinPr, cosPr) = Math.SinCos(kRho * (cosPhi * v2.X + sinPhi * v2.Y));
                    var probePhase = new Complex(cosPr, sinPr);
                    eTheta += amplitude * (-sinTheta) * probePhase * gHats[v];
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

    /// <summary>Stage C2 — the same vertical amplitude over an N-layer stackup, read at the TOP
    /// of the stack (region 0 propagates from there, exactly as the horizontal leg's amplitudes
    /// do). For a covered patch the tube ends on the BURIED metal, which changes the integration
    /// range but not the read-out plane. At N = 1 this reproduces the single-slab form, which is
    /// how it is gated.</summary>
    internal static Complex VerticalAmplitude(LayeredStackup stackup, double k0, double theta,
        double[] nodes, Complex[] currents)
    {
        double kRho = k0 * Math.Sin(theta);
        var kz0 = new Complex(k0 * Math.Cos(theta), 0);
        double zTop = stackup.TotalThicknessMeters;
        var (gn, gw) = GaussLegendre.Rule(2, 0, 1);
        Complex gHat = Complex.Zero;
        for (int e = 0; e + 1 < nodes.Length; e++)
        {
            double h = nodes[e + 1] - nodes[e];
            for (int q = 0; q < gn.Length; q++)
            {
                double zp = nodes[e] + h * gn[q];
                Complex jz = currents[e] * (1 - gn[q]) + currents[e + 1] * gn[q];
                var (gzz, gxz, _) = TransmissionLineGreens.EvaluateVertical(
                    stackup, k0, kRho, kz0, zTop, zp);
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

    /// <summary>
    /// Probe-fed surface-wave power from the COMPLETE current - the horizontal patch current
    /// (raw RWG + junction disc/half-RWGs) AND the vertical tube current, launched COHERENTLY.
    ///
    /// <para><b>Why they cannot simply be added.</b> Both currents launch the same TM mode, so
    /// their launches INTERFERE: the power goes as |a_h + a_v|^2, not |a_h|^2 + |a_v|^2. Summing
    /// two independently-computed powers drops the cross term entirely, which is why the ledger
    /// read about 1.05 at resonance with the vertical leg omitted.</para>
    ///
    /// <para><b>The unified form.</b> Both shipped formulas are already the same expression -
    /// per pole, P = (omega k_p/16pi) * closed-integral Re[Q_A - Q_Phi] dalpha, where Q_A is the
    /// current-current quadratic form and Q_Phi the charge-charge one with q = (j/omega) div J.
    /// (The horizontal formula's -k_p^3/(16 pi omega) Res_Phi |J_rho|^2 is exactly
    /// -(omega k_p/16pi) Res_Phi |q_h|^2 once q_h = (k_p/omega) J_rho is substituted; the
    /// vertical formula's explicit 2pi is the azimuthal integral of an axially-symmetric
    /// current.) Written that way, the mixed problem simply adds the off-diagonal blocks:</para>
    /// <code>
    ///   Q_A   = Res_A |J|^2 + sum_ij w_i w_j conj(J_z_i) Res_zz(z_i,z_j) J_z_j
    ///                       + 2Re[ sum_j w_j conj(J_rho) (-j k_p) Res_xz(d, z_j) J_z_j ]
    ///   Q_Phi = Res_Phi |q_h|^2 + sum_ij w_i w_j conj(q_v_i) Res_Phi(z_i,z_j) q_v_j
    ///                       + 2Re[ sum_j w_j conj(q_h) Res_Phi(d, z_j) q_v_j ]
    /// </code>
    /// <para>The (-j k_p) on the cross term is DERIVED, not fitted:
    /// <see cref="VerticalSpectralKernels"/> defines G_A^xz "per -jk_x*J_z", so the radial vector
    /// potential a tube current produces is G_A^xz * (-j k_rho) * J_z. Only TM poles carry the
    /// vertical and cross blocks - a z-directed current has no TE coupling - which is also what
    /// keeps the vertical-only limit identical to
    /// <see cref="VerticalSurfaceWavePowerWatts"/>.</para>
    ///
    /// <para><b>The charge partition.</b> The junction disc's div D = delta^2(v) and the tube's
    /// endpoint delta at z = d are the SAME charge with opposite signs - the disc is what carries
    /// the arriving tube current away radially. The horizontal transform INCLUDES the disc, so its
    /// divergence carries (j/omega) I_top e^{jk.v}; the tube's distributed q_v does NOT carry the
    /// cancelling endpoint delta. Counting one without the other would leave a spurious point
    /// charge at the junction, so it is subtracted explicitly below.</para>
    /// </summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        LayeredKernelTable kernel, ProbeFedSolution probeSolution, ProbeFeed probe,
        int alphaCount = 64)
    {
        var fan = ProbeVertexFan(surface, probe);
        var junction = new JunctionLeg(fan, probeSolution.TubeCurrents[^1]);
        // The tube stands at the junction vertex (the mesh snaps one to the probe).
        var tube = new VerticalLeg(fan.VertexPosition.X, fan.VertexPosition.Y, probeSolution.TubeNodes, probeSolution.TubeCurrents);
        return MixedSurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles,
            new VerticalKernelSet(kernel.Substrate, kernel.FrequencyHz),
            kernel.Substrate.ThicknessMeters, probeSolution.RawEdgeCurrents, new[] { junction },
            new[] { tube }, alphaCount);
    }

    /// <summary>Stage C2 — the same coherent horizontal + vertical surface-wave ledger over a
    /// MULTI-LAYER stackup. Only the medium changes: the pole set and its horizontal residues
    /// come from the table, the per-(z, z′) vertical residues from the TLGF, and the metal plane
    /// is wherever the tube ends (the top of the stack, or a buried interface).</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface,
        MultiLayerKernelTable kernel, ProbeFedSolution probeSolution, ProbeFeed probe,
        int alphaCount = 64)
    {
        var fan = ProbeVertexFan(surface, probe);
        var junction = new JunctionLeg(fan, probeSolution.TubeCurrents[^1]);
        var tube = new VerticalLeg(fan.VertexPosition.X, fan.VertexPosition.Y, probeSolution.TubeNodes, probeSolution.TubeCurrents);
        return MixedSurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles,
            new MultiLayerVerticalKernelSet(kernel.Stackup, kernel.FrequencyHz),
            probeSolution.TubeNodes[^1], probeSolution.RawEdgeCurrents, new[] { junction },
            new[] { tube }, alphaCount);
    }

    /// <summary>The surface-wave power of a pin array (FU-35): the sheet, every junction and every
    /// tube launching the surface waves together.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface, LayeredKernelTable kernel,
        PinArrayCurrents currents, IReadOnlyList<VerticalPin> pins, int alphaCount = 64)
    {
        var (junctions, legs) = PinLegs(surface, currents, pins);
        return MixedSurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles,
            new VerticalKernelSet(kernel.Substrate, kernel.FrequencyHz), kernel.Substrate.ThicknessMeters,
            currents.RawEdgeCurrents, junctions, legs, alphaCount);
    }

    /// <summary>The pin array's surface-wave power over a multi-layer stackup.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface, MultiLayerKernelTable kernel,
        PinArrayCurrents currents, IReadOnlyList<VerticalPin> pins, int alphaCount = 64)
    {
        var (junctions, legs) = PinLegs(surface, currents, pins);
        return MixedSurfaceWavePowerWatts(surface, kernel.FrequencyHz, kernel.Poles,
            new MultiLayerVerticalKernelSet(kernel.Stackup, kernel.FrequencyHz), currents.TubeNodes[0][^1],
            currents.RawEdgeCurrents, junctions, legs, alphaCount);
    }

    /// <summary>The coherent horizontal + vertical surface-wave power; see the overload above
    /// for the derivation and the charge-partition argument. Several tubes (a pin array) add
    /// their vertical currents into one spectrum, each at its own lateral phase e^{jk·ρ_pin}, and
    /// each junction takes its own tube''s endpoint charge out of the horizontal charge.</summary>
    private static double MixedSurfaceWavePowerWatts(SurfaceStructure surface,
        double frequencyHz, IReadOnlyList<SurfaceWavePole> poles, VerticalKernels set,
        double metalHeight, IReadOnlyList<Complex> edgeCurrents, IReadOnlyList<JunctionLeg> junctions,
        IReadOnlyList<VerticalLeg> tubes, int alphaCount)
    {
        double omega = 2 * Math.PI * frequencyHz;
        double d = metalHeight;
        var j = Complex.ImaginaryOne;

        // Every tube's current and distributed line charge on the SAME Gauss grid
        // VerticalSurfaceWavePowerWatts uses, so the vertical-only block reproduces that
        // already-oracle-gated formula rather than approximating it.
        var (gn, gw) = GaussLegendre.Rule(4, 0, 1);
        var z = new List<double>();
        var jz = new List<Complex>();
        var qv = new List<Complex>();
        var w = new List<double>();
        var owner = new List<int>();
        for (int p = 0; p < tubes.Count; p++)
        {
            var tubeNodes = tubes[p].Nodes;
            var tubeCurrents = tubes[p].Currents;
            for (int e = 0; e + 1 < tubeNodes.Length; e++)
            {
                double h = tubeNodes[e + 1] - tubeNodes[e];
                Complex slope = (tubeCurrents[e + 1] - tubeCurrents[e]) / h;
                for (int q = 0; q < gn.Length; q++)
                {
                    z.Add(tubeNodes[e] + h * gn[q]);
                    jz.Add(tubeCurrents[e] * (1 - gn[q]) + tubeCurrents[e + 1] * gn[q]);
                    qv.Add(j / omega * slope);
                    w.Add(gw[q] * h);
                    owner.Add(p);
                }
            }
        }
        int n = z.Count;

        double power = 0;
        foreach (var pole in poles)
        {
            double kp = pole.KRho.Real;
            var pk = new Complex(kp, 0);
            bool vertical = pole.IsTm && n > 0;      // a z-current launches only the TM mode

            // Residues independent of the azimuth: computed once per pole, not per alpha.
            Complex[,] resZz = null!, resPhiVv = null!;
            Complex[] resXzTop = null!, resPhiTop = null!;
            if (vertical)
            {
                resZz = new Complex[n, n];
                resPhiVv = new Complex[n, n];
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                    {
                        var r = set.PoleResidues(pk, pole.IsTm, z[a], z[b]);
                        resZz[a, b] = r.GAzz;
                        resPhiVv[a, b] = r.KPhi;
                    }
                resXzTop = new Complex[n];
                resPhiTop = new Complex[n];
                for (int b = 0; b < n; b++)
                {
                    var r = set.PoleResidues(pk, pole.IsTm, d, z[b]);
                    resXzTop[b] = r.GAxz;
                    resPhiTop[b] = r.KPhi;
                }
            }

            double dAlpha = 2 * Math.PI / alphaCount;
            double accum = 0;                 // the azimuthal integral of Re[Q_A - Q_Phi]
            var phase = new Complex[tubes.Count];
            for (int i = 0; i < alphaCount; i++)
            {
                double alpha = 2 * Math.PI * i / alphaCount;
                var (sin, cos) = Math.SinCos(alpha);
                double kx = kp * cos, ky = kp * sin;

                var (jx, jy) = SpectralCurrent(surface, edgeCurrents, kx, ky);
                foreach (var junction in junctions)
                {
                    var (djx, djy) = junction.Fan.CurrentTransform(surface, kx, ky);
                    jx += junction.Coeff * djx;
                    jy += junction.Coeff * djy;
                }
                var jRho = cos * jx + sin * jy;

                // Horizontal charge, MINUS each junction disc's point charge: the tube's endpoint
                // delta that cancels it is not carried in q_v, so counting one alone would leave
                // a spurious point charge sitting at the junction.
                var qh = kp / omega * jRho;
                foreach (var junction in junctions)
                {
                    var vertexPos = junction.Fan.VertexPosition;
                    qh -= j / omega * junction.Coeff * Complex.Exp(j * (kx * vertexPos.X + ky * vertexPos.Y));
                }

                double qA = pole.ResidueA.Real
                            * (jx.Magnitude * jx.Magnitude + jy.Magnitude * jy.Magnitude);
                double qPhi = pole.ResiduePhi.Real * (qh.Magnitude * qh.Magnitude);

                if (vertical)
                {
                    // Each tube stands at its own lateral point, so its transform carries the
                    // same lateral phase e^{jk·ρ} the horizontal transform gives every patch point
                    // (and the far field gives the tube). Without it the cross terms paired the
                    // patch current with a tube moved to the origin — the RF-6 residual.
                    for (int p = 0; p < tubes.Count; p++)
                        phase[p] = Complex.Exp(j * (kx * tubes[p].X + ky * tubes[p].Y));
                    Complex vv = Complex.Zero, vvPhi = Complex.Zero;
                    for (int a = 0; a < n; a++)
                    {
                        var ja = Complex.Conjugate(jz[a] * phase[owner[a]]);
                        var qa = Complex.Conjugate(qv[a] * phase[owner[a]]);
                        for (int b = 0; b < n; b++)
                        {
                            double ww = w[a] * w[b];
                            var pb = phase[owner[b]];
                            vv += ww * ja * resZz[a, b] * jz[b] * pb;
                            vvPhi += ww * qa * resPhiVv[a, b] * qv[b] * pb;
                        }
                    }
                    Complex cross = Complex.Zero, crossPhi = Complex.Zero;
                    for (int b = 0; b < n; b++)
                    {
                        var pb = phase[owner[b]];
                        cross += w[b] * Complex.Conjugate(jRho) * (-j * pk) * resXzTop[b] * jz[b] * pb;
                        crossPhi += w[b] * Complex.Conjugate(qh) * resPhiTop[b] * qv[b] * pb;
                    }
                    qA += vv.Real + 2 * cross.Real;
                    qPhi += vvPhi.Real + 2 * crossPhi.Real;
                }
                accum += (qA - qPhi) * dAlpha;
            }
            power += omega * kp / (16 * Math.PI) * accum;
        }
        return power;
    }

    /// <summary>The surface-wave power a PURE vertical tube current launches (no patch):
    /// P_sw = (ωk_p/16π)·2π·Re[∫∫J_z*·Res G̃_A^zz·J_z − ∫∫q_v*·Res K̃_Φ·q_v], q_v =
    /// (j/ω)∂_zJ_z, summed over TM poles (a z-current excites only the axially-symmetric
    /// TM mode). Gated against the probe-only oracle P_in − P_rad (both exact).</summary>
    public static double VerticalSurfaceWavePowerWatts(SubstrateStackup substrate,
        double frequencyHz, double[] tubeNodes, Complex[] tubeCurrents)
        => VerticalSurfaceWavePowerWatts(
            new VerticalKernelSet(substrate, frequencyHz), tubeNodes, tubeCurrents);

    /// <summary>The same pure-vertical launch over any medium — the multi-layer sibling shares
    /// every line, since only the residues differ.</summary>
    internal static double VerticalSurfaceWavePowerWatts(VerticalKernels set,
        double[] tubeNodes, Complex[] tubeCurrents)
    {
        double omega = 2 * Math.PI * set.FrequencyHz;
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
                    var res = set.PoleResidues(pk, pole.IsTm, z[i], z[j]);
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
