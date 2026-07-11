using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>One frequency point of a thin-wire moment-method solve: the input impedance
/// seen by the delta-gap feed and the complex current coefficient at every basis node
/// (peak-value phasors, like the EQS solver).</summary>
public sealed record MomSolution(double FrequencyHz, Complex InputImpedance, Complex[] BasisCurrents);

/// <summary>
/// Frequency-domain thin-wire method of moments: the electric-field integral equation
/// with the reduced kernel R = √(d² + a²), triangular (rooftop) current bases, and
/// Galerkin testing — so the impedance matrix is complex-symmetric by construction and
/// current continuity holds at every bend. Excitation is a delta-gap voltage source at
/// a basis node; the dense complex-symmetric system is solved by the first-party LU.
///
/// Quadrature strategy (each regime oracle-tested):
///  • SELF element pairs — the double integral collapses to 1D in t = s−s′; the static
///    1/R part is fully ANALYTIC (asinh forms), the remainder (e^{−jkR}−1)/R is smooth
///    and takes a 16-point Gauss rule. No numerical singularity handling anywhere.
///  • NEAR pairs (sharing a node, or closer than two element lengths) — the static part
///    uses the exact line-charge inner integral (analytic) under an outer Gauss rule,
///    panelled geometrically toward a shared corner where the integrand is log-like;
///    the smooth remainder takes a 12×12 product rule.
///  • FAR pairs — the full kernel is smooth; a direct 8×8 product rule.
/// Assumptions: perfect conductor, free space (no dielectric — PCB antennas detune
/// accordingly), thin wires (radius ≪ λ, element length ≳ 2·radius).
/// </summary>
public sealed class ThinWireMomSolver
{
    /// <summary>Kernel facts every consumer must surface next to results.</summary>
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Perfect conductor (no ohmic loss).",
        "Free space — board dielectric and nearby copper are not modeled; physical PCB antennas detune accordingly.",
        "Thin-wire kernel: wire radius ≪ λ; strips as equivalent-radius wires (r = w/4).",
        "Delta-gap voltage feed at a basis node."
    };

    public MomSolution Solve(WireStructure wire, double frequencyHz, int feedBasis, double gapVolts = 1.0)
    {
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
        if (feedBasis < 0 || feedBasis >= wire.BasisCount)
            throw new ArgumentOutOfRangeException(nameof(feedBasis),
                $"Feed basis {feedBasis} is outside 0..{wire.BasisCount - 1}.");

        double omega = 2 * Math.PI * frequencyHz;
        double k = omega / RfConstants.SpeedOfLight;

        var z = AssembleImpedanceMatrix(wire, k, omega);
        var rhs = new Complex[wire.BasisCount];
        rhs[feedBasis] = gapVolts;
        var currents = ComplexLu.Factor(z).Solve(rhs);

        Complex feedCurrent = currents[feedBasis];
        if (feedCurrent == Complex.Zero)
            throw new InvalidOperationException(
                "The feed carries zero current — the feed sits at a current null of a degenerate structure.");
        return new MomSolution(frequencyHz, gapVolts / feedCurrent, currents);
    }

    // ------------------------------------------------------------------
    // Assembly
    // ------------------------------------------------------------------

    internal static ComplexDenseMatrix AssembleImpedanceMatrix(WireStructure wire, double k, double omega)
    {
        int n = wire.BasisCount;
        int elementCount = wire.ElementCount;
        var z = new ComplexDenseMatrix(n, n);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);

        // Element → the (≤2) bases it supports, with the rising/falling role. A grounded
        // end's basis has only ONE real supporting element (RisingElement/FallingElement
        // return −1 for the half that lives on the image current).
        var supports = new List<(int Basis, bool Rising)>[elementCount];
        for (int e = 0; e < elementCount; e++) supports[e] = new List<(int, bool)>(2);
        for (int b = 0; b < n; b++)
        {
            int rising = wire.RisingElement(b);
            if (rising >= 0) supports[rising].Add((b, true));
            int falling = wire.FallingElement(b);
            if (falling >= 0) supports[falling].Add((b, false));
        }

        for (int p = 0; p < elementCount; p++)
        {
            if (supports[p].Count == 0) continue;
            double lengthP = wire.ElementLength(p);
            var directionP = wire.ElementDirection(p);
            for (int q = p; q < elementCount; q++)
            {
                if (supports[q].Count == 0) continue;
                var moments = PairMoments(wire, p, q, k);
                double dot = Vector3D.Dot(directionP, wire.ElementDirection(q));
                double lengthQ = wire.ElementLength(q);

                foreach (var (basisP, risingP) in supports[p])
                    foreach (var (basisQ, risingQ) in supports[q])
                    {
                        Complex vector = vectorFactor * dot * Combine(moments, risingP, risingQ);
                        double slopeP = (risingP ? 1.0 : -1.0) / lengthP;
                        double slopeQ = (risingQ ? 1.0 : -1.0) / lengthQ;
                        Complex charge = chargeFactor * slopeP * slopeQ * moments.M00;
                        Complex contribution = vector + charge;

                        z[basisP, basisQ] += contribution;
                        // ∬ f_m f_n g is symmetric in (m, n): the transposed entry gets
                        // the numerically identical addend, so Z stays bitwise symmetric.
                        if (p != q)
                            z[basisQ, basisP] += contribution;
                    }
            }
        }

        if (wire.Ground is { } ground)
            AddImagePass(z, wire, supports, ground.SurfaceZ, k, vectorFactor, chargeFactor);
        return z;
    }

    /// <summary>
    /// The image-theory pass for a PEC ground plane: Z += Z_image, testing every real
    /// basis against the IMAGE of every source basis. The image of element q is mirrored
    /// across the plane AND endpoint-swapped (the PlaneReturnComposer convention) — the
    /// swap reverses horizontal current and preserves vertical current, so ALL sign
    /// physics rides on the geometry: a basis that RISES on q plays the FALLING role on
    /// q's image, and the tangent dot / charge slope come out of the swapped-mirrored
    /// endpoints with no hand-tuned signs. The unknown count is unchanged.
    ///
    /// Symmetry: mirroring both integration variables shows ∬f_m·(image f_n)·g equals
    /// ∬f_n·(image f_m)·g exactly, so each real/image element pair is evaluated ONCE and
    /// scattered to both entries — Z stays bitwise complex-symmetric. (For p == q the
    /// same identity gives M00 = M01 + M10 for the exact image moments; the ordered
    /// basis-pair loop below reuses one Combine value for both entries because the
    /// NUMERICAL moments only satisfy that identity approximately.)
    ///
    /// Image pairs are never singular: a real element and any image element are separated
    /// by at least twice its clearance above the plane — except at a grounded node, which
    /// IS shared (bitwise, thanks to the builder's exact snap), where the geometric
    /// shared-corner detection routes the pair into the panelled NEAR quadrature exactly
    /// like adjacent real elements.
    /// </summary>
    private static void AddImagePass(ComplexDenseMatrix z, WireStructure wire,
        List<(int Basis, bool Rising)>[] supports, double surfaceZ, double k,
        Complex vectorFactor, Complex chargeFactor)
    {
        int elementCount = wire.ElementCount;
        for (int p = 0; p < elementCount; p++)
        {
            if (supports[p].Count == 0) continue;
            double lengthP = wire.ElementLength(p);
            var directionP = wire.ElementDirection(p);
            for (int q = p; q < elementCount; q++)
            {
                if (supports[q].Count == 0) continue;
                var imageStart = Mirror(wire.ElementEnd(q), surfaceZ);
                var imageEnd = Mirror(wire.ElementStart(q), surfaceZ);
                double c = Math.Sqrt(wire.ElementRadii[p] * wire.ElementRadii[q]);
                var moments = GeometricPairMoments(
                    wire.ElementStart(p), wire.ElementEnd(p), imageStart, imageEnd, c, k);
                double lengthQ = wire.ElementLength(q);
                var imageDirection = (imageEnd - imageStart) / lengthQ;
                double dot = Vector3D.Dot(directionP, imageDirection);

                Complex Contribution(bool risingP, bool risingQ)
                {
                    bool imageRising = !risingQ;   // the endpoint swap flips the role
                    Complex vector = vectorFactor * dot * Combine(moments, risingP, imageRising);
                    double slopeP = (risingP ? 1.0 : -1.0) / lengthP;
                    double slopeQ = (imageRising ? 1.0 : -1.0) / lengthQ;
                    return vector + chargeFactor * slopeP * slopeQ * moments.M00;
                }

                if (p != q)
                {
                    foreach (var (basisP, risingP) in supports[p])
                        foreach (var (basisQ, risingQ) in supports[q])
                        {
                            Complex contribution = Contribution(risingP, risingQ);
                            z[basisP, basisQ] += contribution;
                            z[basisQ, basisP] += contribution;
                        }
                }
                else
                {
                    // Same element: iterate ordered basis pairs and reuse one value for
                    // both entries (the exact values coincide; see the symmetry note).
                    var list = supports[p];
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i; j < list.Count; j++)
                        {
                            Complex contribution = Contribution(list[i].Rising, list[j].Rising);
                            z[list[i].Basis, list[j].Basis] += contribution;
                            if (list[i].Basis != list[j].Basis)
                                z[list[j].Basis, list[i].Basis] += contribution;
                        }
                }
            }
        }
    }

    internal static Vector3D Mirror(Vector3D p, double surfaceZ) =>
        new(p.X, p.Y, 2 * surfaceZ - p.Z);

    /// <summary>The bilinear kernel moments over one element pair:
    /// M_ab = ∬ (s/L_p)^a (s′/L_q)^b · e^{−jkR}/R ds ds′, a,b ∈ {0,1}. Any rooftop
    /// weight combination is a linear combination of the four.</summary>
    internal readonly record struct Moments(Complex M00, Complex M01, Complex M10, Complex M11);

    private static Complex Combine(Moments m, bool risingP, bool risingQ) =>
        (risingP, risingQ) switch
        {
            (true, true) => m.M11,
            (true, false) => m.M10 - m.M11,
            (false, true) => m.M01 - m.M11,
            (false, false) => m.M00 - m.M01 - m.M10 + m.M11
        };

    internal static Moments PairMoments(WireStructure wire, int p, int q, double k)
    {
        double c = Math.Sqrt(wire.ElementRadii[p] * wire.ElementRadii[q]);
        if (p == q)
            return SelfMoments(wire.ElementLength(p), c, k);

        var p0 = wire.ElementStart(p);
        var p1 = wire.ElementEnd(p);
        var q0 = wire.ElementStart(q);
        var q1 = wire.ElementEnd(q);
        double lengthP = (p1 - p0).Length;
        double lengthQ = (q1 - q0).Length;

        int nodeCount = wire.Nodes.Count;
        int pStart = p, pEnd = (p + 1) % nodeCount, qStart = q, qEnd = (q + 1) % nodeCount;
        bool shareNode = pStart == qStart || pStart == qEnd || pEnd == qStart || pEnd == qEnd;

        double minEndpointDistance = Math.Min(
            Math.Min((p0 - q0).Length, (p0 - q1).Length),
            Math.Min((p1 - q0).Length, (p1 - q1).Length));
        bool near = shareNode || minEndpointDistance < 2 * Math.Max(lengthP, lengthQ);

        return near
            ? NearMoments(p0, p1, q0, q1, c, k, shareNode)
            : FarMoments(p0, p1, q0, q1, c, k);
    }

    /// <summary>Regime dispatch for element pairs given by raw endpoints (the image pass,
    /// where node indices don't apply): a shared corner is detected GEOMETRICALLY —
    /// a grounded node's image coincides with it bitwise, so a real element and its
    /// neighbour's image share that corner exactly like adjacent real elements do.
    /// A real/image pair is never coincident (the builder rejects in-plane segments),
    /// so the SELF regime can't occur here.</summary>
    internal static Moments GeometricPairMoments(Vector3D p0, Vector3D p1, Vector3D q0, Vector3D q1,
        double c, double k)
    {
        double lengthP = (p1 - p0).Length;
        double lengthQ = (q1 - q0).Length;
        double minEndpointDistance = Math.Min(
            Math.Min((p0 - q0).Length, (p0 - q1).Length),
            Math.Min((p1 - q0).Length, (p1 - q1).Length));
        double maxLength = Math.Max(lengthP, lengthQ);
        bool shareNode = minEndpointDistance <= 1e-9 * maxLength;
        bool near = shareNode || minEndpointDistance < 2 * maxLength;
        return near
            ? NearMoments(p0, p1, q0, q1, c, k, shareNode)
            : FarMoments(p0, p1, q0, q1, c, k);
    }

    // ---- SELF: exact static + smooth 1D remainder --------------------------------

    private static Moments SelfMoments(double length, double c, double k)
    {
        double l = length;
        double hyp = Math.Sqrt(l * l + c * c);
        double asinhL = Math.Asinh(l / c);

        // ∫₀ᴸ tᵐ/√(t²+c²) dt for m = 0..3 — everything the S_ab weights need.
        double i0 = asinhL;
        double i1 = hyp - c;
        double i3 = (hyp * hyp * hyp - c * c * c) / 3 - c * c * (hyp - c);

        // M⁰_ab = ∫₀ᴸ S_ab(t)/√(t²+c²) dt with S₀₀ = 2(L−t), S₀₁ = S₁₀ = L−t,
        // S₁₁ = (L−t)²(2L+t)/(3L²) = (2L³ − 3L²t + t³)/(3L²).
        double static01 = l * i0 - i1;
        double static00 = 2 * static01;
        double static11 = (2 * l * l * l * i0 - 3 * l * l * i1 + i3) / (3 * l * l);

        // Smooth remainder (e^{−jkR} − 1)/R depends on t alone — 1D Gauss.
        var (nodes, weights) = GaussLegendre.Rule(16, 0, l);
        Complex smooth00 = Complex.Zero, smooth01 = Complex.Zero, smooth11 = Complex.Zero;
        for (int i = 0; i < nodes.Length; i++)
        {
            double t = nodes[i];
            double r = Math.Sqrt(t * t + c * c);
            Complex gs = (Complex.Exp(new Complex(0, -k * r)) - 1) / r;
            Complex w = weights[i] * gs;
            smooth00 += w * (2 * (l - t));
            smooth01 += w * (l - t);
            smooth11 += w * ((l - t) * (l - t) * (2 * l + t) / (3 * l * l));
        }

        var m00 = static00 + smooth00;
        var m01 = static01 + smooth01;
        var m11 = static11 + smooth11;
        return new Moments(m00, m01, m01, m11);
    }

    // ---- NEAR: analytic inner (line charge), panelled outer + smooth 2D ----------

    private static Moments NearMoments(Vector3D p0, Vector3D p1, Vector3D q0, Vector3D q1,
        double c, double k, bool shareNode)
    {
        double lengthP = (p1 - p0).Length;
        double lengthQ = (q1 - q0).Length;
        var directionP = (p1 - p0) / lengthP;
        var directionQ = (q1 - q0) / lengthQ;

        // Outer quadrature on P for the STATIC part. Toward a shared corner the
        // (analytically inner-integrated) integrand is log-like, so panels shrink
        // geometrically to that corner; otherwise four equal panels resolve a close
        // approach anywhere along the element.
        bool corners = shareNode;
        bool cornerAtStart = false;
        if (corners)
        {
            double startGap = Math.Min((p0 - q0).Length, (p0 - q1).Length);
            double endGap = Math.Min((p1 - q0).Length, (p1 - q1).Length);
            cornerAtStart = startGap <= endGap;
        }
        var panels = new List<(double A, double B)>();
        if (corners)
        {
            double delta = Math.Max(c, 1e-3 * lengthP);
            double edge = 0;
            while (edge < lengthP)
            {
                double nextEdge = edge == 0 ? delta : Math.Min(4 * edge, lengthP);
                if (nextEdge > lengthP) nextEdge = lengthP;
                panels.Add((edge, nextEdge));
                edge = nextEdge;
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
                panels.Add((lengthP * i / 4.0, lengthP * (i + 1) / 4.0));
        }

        Complex m00 = Complex.Zero, m01 = Complex.Zero, m10 = Complex.Zero, m11 = Complex.Zero;
        foreach (var (a, b) in panels)
        {
            var (nodes, weights) = GaussLegendre.Rule(6, a, b);
            for (int i = 0; i < nodes.Length; i++)
            {
                // ξ measures from the shared corner when there is one, so the panel
                // refinement lands where the integrand actually peaks.
                double s = corners && !cornerAtStart ? lengthP - nodes[i] : nodes[i];
                var x = p0 + directionP * s;
                var (j0, j1) = LineChargeIntegrals(x, q0, directionQ, lengthQ, c);
                double w = weights[i];
                double rise = s / lengthP;
                m00 += w * j0;
                m01 += w * j1;
                m10 += w * rise * j0;
                m11 += w * rise * j1;
            }
        }

        // Smooth remainder (e^{−jkR} − 1)/R — bounded with O(k)-scale variation.
        var (outerNodes, outerWeights) = GaussLegendre.Rule(12, 0, lengthP);
        var (innerNodes, innerWeights) = GaussLegendre.Rule(12, 0, lengthQ);
        for (int i = 0; i < outerNodes.Length; i++)
        {
            var x = p0 + directionP * outerNodes[i];
            double riseP = outerNodes[i] / lengthP;
            for (int j = 0; j < innerNodes.Length; j++)
            {
                var y = q0 + directionQ * innerNodes[j];
                double r = Math.Sqrt((x - y).LengthSquared + c * c);
                Complex gs = (Complex.Exp(new Complex(0, -k * r)) - 1) / r;
                Complex w = outerWeights[i] * innerWeights[j] * gs;
                double riseQ = innerNodes[j] / lengthQ;
                m00 += w;
                m01 += w * riseQ;
                m10 += w * riseP;
                m11 += w * riseP * riseQ;
            }
        }
        return new Moments(m00, m01, m10, m11);
    }

    /// <summary>The exact static inner integrals over a source element (line charge with
    /// the reduced-kernel radius bump): J₀ = ∫ du/R, J₁ = ∫ (u/L) du/R with
    /// R = √((u−u₀)² + ρ² + c²) — the closed forms that make the near-singular static
    /// part exact instead of quadrature-limited.</summary>
    private static (double J0, double J1) LineChargeIntegrals(
        Vector3D x, Vector3D q0, Vector3D directionQ, double lengthQ, double c)
    {
        var relative = x - q0;
        double u0 = Vector3D.Dot(relative, directionQ);
        double rhoSquared = Math.Max(0, relative.LengthSquared - u0 * u0);
        double rc = Math.Sqrt(rhoSquared + c * c);

        double j0 = Math.Asinh((lengthQ - u0) / rc) + Math.Asinh(u0 / rc);
        double far = Math.Sqrt((lengthQ - u0) * (lengthQ - u0) + rc * rc);
        double nearEnd = Math.Sqrt(u0 * u0 + rc * rc);
        double j1 = (far - nearEnd + u0 * j0) / lengthQ;
        return (j0, j1);
    }

    // ---- FAR: the whole kernel is smooth — one 8×8 product rule -------------------

    private static Moments FarMoments(Vector3D p0, Vector3D p1, Vector3D q0, Vector3D q1,
        double c, double k)
    {
        double lengthP = (p1 - p0).Length;
        double lengthQ = (q1 - q0).Length;
        var directionP = (p1 - p0) / lengthP;
        var directionQ = (q1 - q0) / lengthQ;

        var (outerNodes, outerWeights) = GaussLegendre.Rule(8, 0, lengthP);
        var (innerNodes, innerWeights) = GaussLegendre.Rule(8, 0, lengthQ);
        Complex m00 = Complex.Zero, m01 = Complex.Zero, m10 = Complex.Zero, m11 = Complex.Zero;
        for (int i = 0; i < outerNodes.Length; i++)
        {
            var x = p0 + directionP * outerNodes[i];
            double riseP = outerNodes[i] / lengthP;
            for (int j = 0; j < innerNodes.Length; j++)
            {
                var y = q0 + directionQ * innerNodes[j];
                double r = Math.Sqrt((x - y).LengthSquared + c * c);
                Complex g = Complex.Exp(new Complex(0, -k * r)) / r;
                Complex w = outerWeights[i] * innerWeights[j] * g;
                double riseQ = innerNodes[j] / lengthQ;
                m00 += w;
                m01 += w * riseQ;
                m10 += w * riseP;
                m11 += w * riseP * riseQ;
            }
        }
        return new Moments(m00, m01, m10, m11);
    }
}
