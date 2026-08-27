using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The probe-feed (vertical current) assembly blocks for the layered solver. The tube
/// current on [0, z_metal] uses triangular rooftop bases exactly like the thin-wire solver:
/// a half basis at the ground node (current flows INTO the ground — well-posed because
/// K_Φ(0, ·) = 0, the kernel's built-in PEC), full hats at interior nodes, and a top
/// half basis that exists only as part of the junction unknown.
///
/// Tube–tube entries split per the spatial-kernel composition: each quasi-static image
/// track is a thin-wire PAIR MOMENT against a height-TRANSFORMED source segment — reusing
/// the wire machinery's oracle-tested SELF/NEAR/FAR quadrature with the reduced radius
/// bump; currents are NOT transformed (the kernel argument shifts, the scalar coefficients
/// carry the physics — the layered-image doctrine). The smooth track (surface-wave poles +
/// Sommerfeld remainder) is a plain Gauss product per element pair at the reduced
/// ρ_eff = a. One contribution is computed per (m, n) and scattered to both entries,
/// so the block is bitwise complex-symmetric (the shift-pair tracks are symmetric
/// only as a SUM — analytically exact, numerically reassociated — the wire image-pass
/// precedent).
///
/// <para>Which tracks exist is the MEDIUM's business, not this file's: a single grounded slab
/// offers the primary plus four (reflect at 0, reflect at d, shift by ∓2d); an N-layer stackup
/// offers the primary plus its PEC-ground reflection, and lets the Sommerfeld remainder carry
/// the rest of the ladder. Both arrive through <see cref="VerticalKernels.MomentTracks"/>, so
/// this assembly is written once and the N = 1 agreement between the two is a statement about
/// the kernels rather than about two hand-aligned assemblies.</para>
/// </summary>
internal static class ProbeAssembly
{
    /// <summary>Uniform tube node heights 0 = z₀ &lt; … &lt; z_N = d for a single slab. The
    /// element floor h ≥ 2a is the reduced-kernel validity line — a typed failure, never
    /// silent.</summary>
    public static double[] TubeNodes(SubstrateStackup substrate, ProbeFeed probe) =>
        TubeNodes(LayeredStackup.FromSubstrate(substrate), null, probe);

    /// <summary>Tube node heights 0 = z₀ &lt; … &lt; z_N = z_metal for an N-layer grounded
    /// stackup, with the probe ending on the metal at <paramref name="metalInterface"/>
    /// (null ⇒ the top of the stack).
    ///
    /// <para><b>Every internal interface below the metal is forced to be a NODE.</b> The kernels
    /// are only piecewise-smooth in z — a rooftop straddling an ε jump would integrate a kink
    /// with a smooth rule — and, more sharply, a vertical source exactly ON a discontinuity has
    /// source strength −2µ₀/ε with two different values, which the multi-layer kernels refuse by
    /// name. Nodding at every interface makes that case structurally unreachable: quadrature
    /// points are strictly inside an element, hence strictly inside one material.</para>
    ///
    /// <para><paramref name="probe"/>'s segment count is a TARGET for the whole tube, split
    /// between the layers in proportion to their thickness with a floor of one element each. A
    /// stack of many thin layers therefore yields MORE elements than asked for; that is stated
    /// rather than silently rounded away, because the alternative is an element spanning a
    /// material it has no kernel for.</para></summary>
    public static double[] TubeNodes(LayeredStackup stackup, int? metalInterface, ProbeFeed probe)
    {
        int top = metalInterface ?? stackup.Layers.Count - 1;
        if (top < 0 || top >= stackup.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(metalInterface),
                $"The metal interface must index one of the stackup's {stackup.Layers.Count} layer "
                + $"tops — got {top}.");
        var heights = stackup.InterfaceHeights();
        double zMetal = heights[top];

        var nodes = new List<double>(probe.Segments + top + 1) { 0.0 };
        for (int i = 0; i <= top; i++)
        {
            double thickness = stackup.Layers[i].ThicknessMeters;
            double below = i == 0 ? 0 : heights[i - 1];
            int count = Math.Max(1, (int)Math.Round(probe.Segments * thickness / zMetal));
            double h = thickness / count;
            if (h < 2 * probe.RadiusMeters)
                throw new InvalidOperationException(
                    $"Layer {i} (thickness {thickness:g4} m of the {zMetal:g4} m below the metal) is "
                    + $"too thin for the probe bore: {count} element(s) of {h:g4} m against radius "
                    + $"{probe.RadiusMeters:g4} m violate the reduced thin-wire kernel's element ≳ "
                    + "2·radius floor. Use a thinner probe or fewer segments — this is a model "
                    + "validity line, not a tolerance.");
            for (int k = 1; k < count; k++) nodes.Add(below + thickness * k / count);
            nodes.Add(heights[i]); // the interface itself, exactly
        }
        var result = nodes.ToArray();
        result[^1] = zMetal;
        return result;
    }

    /// <summary>The tube–tube impedance block over the given node heights. Bases 0..N−1 are the
    /// ground half basis and interior hats; with <paramref name="includeTopBasis"/> a final basis
    /// N is the top half hat (the tube leg of the junction unknown). Order matches node order;
    /// the delta-gap port drives basis 0 (f(0) = 1).</summary>
    public static ComplexDenseMatrix ProbeSelfBlock(
        VerticalKernels set, double[] nodes, ProbeFeed probe, double omega, bool includeTopBasis,
        int? maxDegreeOfParallelism = null)
    {
        int segments = nodes.Length - 1;
        double a = probe.RadiusMeters;
        double k0 = set.K0;
        int basisCount = includeTopBasis ? segments + 1 : segments;
        var z = new ComplexDenseMatrix(basisCount, basisCount);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);

        // The wire structure carries the primary track's geometry (SELF/NEAR dispatch
        // by node adjacency); kernels are radial, so the tube lives at the origin.
        var axis = new Vector3D[segments + 1];
        for (int i = 0; i <= segments; i++) axis[i] = new Vector3D(0, 0, nodes[i]);
        var wire = new WireStructure(axis, Enumerable.Repeat(a, segments).ToArray(), isLoop: false);

        // Element → supported bases: basis b peaks at node b (falls on element b,
        // rises on element b−1); the ground half has no rising element.
        var supports = new List<(int Basis, bool Rising)>[segments];
        for (int e = 0; e < segments; e++)
        {
            supports[e] = new List<(int, bool)>(2);
            if (e < basisCount) supports[e].Add((e, false));
            if (e + 1 < basisCount || (includeTopBasis && e + 1 == segments))
                supports[e].Add((e + 1, true));
        }

        var (gaussNodes, gaussWeights) = GaussLegendre.Rule(4, 0, 1);

        // The smooth track is ONE Sommerfeld evaluation per (element pair, quadrature point)
        // and they are independent, so they run in parallel into ORDERED SLOTS and the fill
        // below consumes them in its historical order — the Stage G recipe, bitwise identical
        // at any thread count. This is the measured bottleneck of a multi-layer probe: the
        // single-slab kernels are closed forms, but a stackup integrates a contour per call
        // (~80 µs spectral × the contour), and a 4-element tube asks for 160 of them.
        var pairs = new List<(int P, int Q)>();
        for (int p = 0; p < segments; p++)
            for (int q = p; q < segments; q++) pairs.Add((p, q));
        int rule = gaussNodes.Length;
        var smooth = new (Complex Gzz, Complex Phi)[pairs.Count * rule * rule];
        LayeredKernelTable.ForKnots(pairs.Count, maxDegreeOfParallelism, pair =>
        {
            var (p, q) = pairs[pair];
            double lp = nodes[p + 1] - nodes[p], lq = nodes[q + 1] - nodes[q];
            for (int i = 0; i < rule; i++)
                for (int j = 0; j < rule; j++)
                {
                    var (gzzS, _, phiS) = set.EvaluateSmoothG(
                        a, nodes[p] + lp * gaussNodes[i], nodes[q] + lq * gaussNodes[j]);
                    smooth[(pair * rule + i) * rule + j] = (gzzS, phiS);
                }
        });
        int pairIndex = 0;
        for (int p = 0; p < segments; p++)
        {
            double lengthP = nodes[p + 1] - nodes[p];
            double midP = 0.5 * (nodes[p] + nodes[p + 1]);
            for (int q = p; q < segments; q++)
            {
                double lengthQ = nodes[q + 1] - nodes[q];
                double midQ = 0.5 * (nodes[q] + nodes[q + 1]);

                // The medium's image tracks for THIS pair. Midpoints identify the materials
                // unambiguously — no element straddles an interface, by construction of the
                // node list — so a coefficient that depends on ε(z), ε(z′) is a constant of
                // the pair rather than a function of the quadrature point.
                var tracks = set.MomentTracks(midP, midQ);
                var images = tracks.Images;

                // Primary + the medium's geometric image tracks.
                var primary = ThinWireMomSolver.PairMoments(wire, p, q, k0);
                var trackMoments = new ThinWireMomSolver.Moments[images.Count];
                for (int t = 0; t < images.Count; t++)
                {
                    var map = images[t].SourceHeightMap;
                    trackMoments[t] = ThinWireMomSolver.GeometricPairMoments(
                        axis[p], axis[p + 1],
                        new Vector3D(0, 0, map(nodes[q])), new Vector3D(0, 0, map(nodes[q + 1])),
                        a, k0);
                }

                // Smooth track (poles + remainder), dual kernels, plain Gauss product.
                Complex sv00 = default, sv01 = default, sv10 = default, sv11 = default;
                Complex sc00 = default;
                for (int i = 0; i < gaussNodes.Length; i++)
                {
                    for (int j = 0; j < gaussNodes.Length; j++)
                    {
                        var (gzzS, phiS) = smooth[(pairIndex * rule + i) * rule + j];
                        double w = gaussWeights[i] * gaussWeights[j] * lengthP * lengthQ;
                        Complex wv = w * gzzS;
                        sv00 += wv;
                        sv01 += wv * gaussNodes[j];
                        sv10 += wv * gaussNodes[i];
                        sv11 += wv * gaussNodes[i] * gaussNodes[j];
                        sc00 += w * phiS;
                    }
                }
                var smoothVector = new ThinWireMomSolver.Moments(sv00, sv01, sv10, sv11);
                pairIndex++;

                foreach (var (basisP, risingP) in supports[p])
                    foreach (var (basisQ, risingQ) in supports[q])
                    {
                        if (p == q && basisQ < basisP) continue; // ordered pairs, one value both ways
                        Complex vectorMoment =
                            ThinWireMomSolver.Combine(primary, risingP, risingQ)
                            + ThinWireMomSolver.Combine(smoothVector, risingP, risingQ);
                        Complex chargeMoment = tracks.PrimaryCoefficientPhi * primary.M00 + sc00;
                        for (int t = 0; t < images.Count; t++)
                        {
                            vectorMoment += images[t].CoefficientA
                                * ThinWireMomSolver.Combine(trackMoments[t], risingP, risingQ);
                            chargeMoment += images[t].CoefficientPhi * trackMoments[t].M00;
                        }
                        double slopeP = (risingP ? 1.0 : -1.0) / lengthP;
                        double slopeQ = (risingQ ? 1.0 : -1.0) / lengthQ;
                        Complex contribution = vectorFactor * vectorMoment
                            + chargeFactor * slopeP * slopeQ * chargeMoment;
                        z[basisP, basisQ] += contribution;
                        // Distinct element pairs scatter the transpose UNCONDITIONALLY —
                        // an interior hat supported by both elements takes the (p,q) and
                        // (q,p) contributions on its DIAGONAL entry (the wire solver's
                        // `p != q` guard; a basis-index guard silently halves it).
                        if (p != q)
                            z[basisQ, basisP] += contribution;
                        else if (basisP != basisQ)
                            z[basisQ, basisP] += contribution;
                    }
            }
        }
        return z;
    }

    /// <summary>A standalone probe solve (no patch, open top — the current vanishes at
    /// z = d): the E2 cross-solver identity fixture. At εr = 1 with slab thickness = L
    /// this IS a monopole of length L over a PEC ground, and must reproduce the
    /// thin-wire solver's monopole at the same discretization.</summary>
    public static (Complex InputImpedance, Complex[] Currents) SolveProbeOnly(
        VerticalKernelSet set, ProbeFeed probe, double gapVolts = 1.0) =>
        SolveProbeOnly(set, TubeNodes(set.Substrate, probe), probe, gapVolts);

    /// <summary>The same standalone probe over any medium, given its node heights.</summary>
    public static (Complex InputImpedance, Complex[] Currents) SolveProbeOnly(
        VerticalKernels set, double[] nodes, ProbeFeed probe, double gapVolts = 1.0)
    {
        double omega = 2 * Math.PI * set.FrequencyHz;
        var z = ProbeSelfBlock(set, nodes, probe, omega, includeTopBasis: false);
        var rhs = new Complex[z.Rows];
        rhs[0] = gapVolts; // delta gap at the base: only the ground half basis has f(0) = 1
        var currents = ComplexLu.Factor(z).Solve(rhs);
        if (currents[0] == Complex.Zero)
            throw new InvalidOperationException("The probe base carries zero current.");
        return (gapVolts / currents[0], currents);
    }
}
