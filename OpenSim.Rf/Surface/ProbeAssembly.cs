using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The probe-feed (vertical current) assembly blocks for the layered solver. The tube
/// current on [0, d] uses triangular rooftop bases exactly like the thin-wire solver:
/// a half basis at the ground node (current flows INTO the ground — well-posed because
/// K_Φ(0, ·) = 0, the kernel's built-in PEC), full hats at interior nodes, and a top
/// half basis that exists only as part of the junction unknown.
///
/// Tube–tube entries split per the spatial-kernel composition: the five quasi-static
/// image tracks are each a thin-wire PAIR MOMENT against a height-TRANSFORMED source
/// segment — reflect at z = 0 (the z+z′ image), reflect at z = d (the critical
/// 2d−z−z′ image, nearly singular near the junction and regime-dispatched for free),
/// and shift by ∓2d (the 2d±|z−z′| pair) — reusing the wire machinery's oracle-tested
/// SELF/NEAR/FAR quadrature with the reduced radius bump; currents are NOT
/// transformed (the kernel argument shifts, the scalar coefficients carry the
/// physics — the layered-image doctrine). The smooth track (surface-wave poles +
/// Sommerfeld remainder) is a plain Gauss product per element pair at the reduced
/// ρ_eff = a. One contribution is computed per (m, n) and scattered to both entries,
/// so the block is bitwise complex-symmetric (the shift-pair tracks are symmetric
/// only as a SUM — analytically exact, numerically reassociated — the wire image-pass
/// precedent).
/// </summary>
internal static class ProbeAssembly
{
    /// <summary>Uniform tube node heights 0 = z₀ &lt; … &lt; z_N = d. The element floor
    /// h ≥ 2a is the reduced-kernel validity line — a typed failure, never silent.</summary>
    public static double[] TubeNodes(SubstrateStackup substrate, ProbeFeed probe)
    {
        double d = substrate.ThicknessMeters;
        double h = d / probe.Segments;
        if (h < 2 * probe.RadiusMeters)
            throw new InvalidOperationException(
                $"The slab (d = {d:g4} m) is too thin for the probe bore: {probe.Segments} segments of "
                + $"{h:g4} m against radius {probe.RadiusMeters:g4} m violate the reduced thin-wire kernel's "
                + "element ≳ 2·radius floor. Use a thinner probe or fewer segments — this is a model "
                + "validity line, not a tolerance.");
        var nodes = new double[probe.Segments + 1];
        for (int i = 0; i <= probe.Segments; i++)
            nodes[i] = d * i / probe.Segments;
        nodes[^1] = d;
        return nodes;
    }

    /// <summary>The tube–tube impedance block. Bases 0..N−1 are the ground half basis
    /// and interior hats; with <paramref name="includeTopBasis"/> a final basis N is
    /// the top half hat (the tube leg of the junction unknown). Order matches node
    /// order; the delta-gap port drives basis 0 (f(0) = 1).</summary>
    public static ComplexDenseMatrix ProbeSelfBlock(
        VerticalKernelSet set, ProbeFeed probe, double omega, bool includeTopBasis)
    {
        var substrate = set.Substrate;
        double[] nodes = TubeNodes(substrate, probe);
        int segments = probe.Segments;
        double a = probe.RadiusMeters;
        double d = substrate.ThicknessMeters;
        double k0 = set.K0;
        int basisCount = includeTopBasis ? segments + 1 : segments;
        var z = new ComplexDenseMatrix(basisCount, basisCount);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);

        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var eta = (epsC - 1) / (epsC + 1);
        // (source-height transform, vector coefficient, charge coefficient) per track.
        var tracks = new (Func<double, double> Map, Complex CoeffA, Complex CoeffPhi)[]
        {
            (zp => -zp, 1, -1 / epsC),
            (zp => 2 * d - zp, -eta, eta / epsC),
            (zp => zp - 2 * d, -eta, -eta / epsC),
            (zp => zp + 2 * d, -eta, -eta / epsC)
        };

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
        for (int p = 0; p < segments; p++)
        {
            double lengthP = nodes[p + 1] - nodes[p];
            for (int q = p; q < segments; q++)
            {
                double lengthQ = nodes[q + 1] - nodes[q];

                // Primary + the four geometric image tracks.
                var primary = ThinWireMomSolver.PairMoments(wire, p, q, k0);
                var trackMoments = new ThinWireMomSolver.Moments[tracks.Length];
                for (int t = 0; t < tracks.Length; t++)
                {
                    var map = tracks[t].Map;
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
                    double zi = nodes[p] + lengthP * gaussNodes[i];
                    for (int j = 0; j < gaussNodes.Length; j++)
                    {
                        double zj = nodes[q] + lengthQ * gaussNodes[j];
                        var (gzzS, _, phiS) = set.EvaluateSmoothG(a, zi, zj);
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

                foreach (var (basisP, risingP) in supports[p])
                    foreach (var (basisQ, risingQ) in supports[q])
                    {
                        if (p == q && basisQ < basisP) continue; // ordered pairs, one value both ways
                        Complex vectorMoment =
                            ThinWireMomSolver.Combine(primary, risingP, risingQ)
                            + ThinWireMomSolver.Combine(smoothVector, risingP, risingQ);
                        Complex chargeMoment = (1 / epsC) * primary.M00 + sc00;
                        for (int t = 0; t < tracks.Length; t++)
                        {
                            vectorMoment += tracks[t].CoeffA
                                * ThinWireMomSolver.Combine(trackMoments[t], risingP, risingQ);
                            chargeMoment += tracks[t].CoeffPhi * trackMoments[t].M00;
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
        VerticalKernelSet set, ProbeFeed probe, double gapVolts = 1.0)
    {
        double omega = 2 * Math.PI * set.FrequencyHz;
        var z = ProbeSelfBlock(set, probe, omega, includeTopBasis: false);
        var rhs = new Complex[z.Rows];
        rhs[0] = gapVolts; // delta gap at the base: only the ground half basis has f(0) = 1
        var currents = ComplexLu.Factor(z).Solve(rhs);
        if (currents[0] == Complex.Zero)
            throw new InvalidOperationException("The probe base carries zero current.");
        return (gapVolts / currents[0], currents);
    }
}
