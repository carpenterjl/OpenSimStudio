using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The mixed wire↔sheet blocks of a hybrid MoM matrix: the mutual impedance between a thin-wire
/// triangular basis and a surface current living on one or two triangles, in FREE SPACE.
///
/// <para>Wire–wire coupling is the thin-wire solver's, sheet–sheet is the RWG solver's; only this
/// cross block is new, and it is the whole of what a hybrid structure needs beyond the two
/// solvers that already exist. The form is the usual mixed-potential one,</para>
/// <code>
///   Z = jωµ/4π ∬ f_w(s)·(t̂·J_s) g(R) ds dS′  −  j/(4πωε₀) ∬ f_w′(s)·(∇·J_s) g(R) ds dS′
/// </code>
/// <para>with the wire's REDUCED kernel R = √(|r − r′|² + a²) — the same regularization the
/// thin-wire solver uses, and the reason a wire may pass near a sheet without the integrand
/// blowing up. The wire's own radius is what keeps this finite.</para>
///
/// <para><b>Halves, not just RWGs.</b> An ordinary RWG basis is two halves (its plus and minus
/// triangles); the attachment junction's continuation is a SINGLE half on each fan neighbour.
/// Both go through <see cref="MutualHalves"/>, so there is one implementation of the mixed
/// integral and the RWG case is literally the two-element list. Note the charge legs of the
/// halves are summed BEFORE the ε₀ prefactor multiplies them, so
/// <c>Mutual(rwg) ≡ MutualHalf(plus) + MutualHalf(minus)</c> holds mathematically but not
/// bitwise — IEEE addition does not distribute. Keeping the RWG case one call preserves the
/// arithmetic its oracle gates were measured against.</para>
///
/// <para><b>Quadrature.</b> Distance-adaptive, like every other regime split in this solver: the
/// outer wire segment and the inner triangle are each subdivided until the sampling is fine
/// against the separation, so a wire running just above a sheet is integrated as accurately as
/// one a wavelength away. There is no analytic inner integral here because there is no
/// coincident-support case to make one for — the wire is a curve and the basis a surface, and
/// they never overlap.</para>
/// </summary>
internal static class WireSurfaceCoupling
{
    /// <summary>One triangle's worth of surface current: J = Sign·(EdgeLength/2A)(r′ − p_opp)
    /// with ∇·J = Sign·(EdgeLength/A). An RWG basis is the plus half (+1) and the minus half
    /// (−1); an attachment continuation is a lone half with Sign = −1 (the (p_opp − r) form
    /// that flows INTO the neighbour).</summary>
    internal readonly record struct SurfaceHalf(
        int Triangle, double Sign, int Opposite, double EdgeLength);

    /// <summary>Mutual impedance between wire basis <paramref name="wireBasis"/> and RWG basis
    /// <paramref name="rwgBasis"/>.</summary>
    public static Complex Mutual(WireStructure wire, int wireBasis,
        SurfaceStructure surface, int rwgBasis, double k, double omega)
    {
        var edge = surface.Edges[rwgBasis];
        return MutualHalves(wire, wireBasis, surface, new[]
        {
            new SurfaceHalf(edge.PlusTriangle, +1.0, edge.PlusOpposite, edge.Length),
            new SurfaceHalf(edge.MinusTriangle, -1.0, edge.MinusOpposite, edge.Length),
        }, k, omega);
    }

    /// <summary>Mutual impedance between a wire basis and the surface current made of the given
    /// halves, summed as ONE current before the potential prefactors are applied.</summary>
    public static Complex MutualHalves(WireStructure wire, int wireBasis,
        SurfaceStructure surface, IReadOnlyList<SurfaceHalf> halves, double k, double omega)
    {
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);

        Complex vector = Complex.Zero, charge = Complex.Zero;

        foreach (var leg in wire.BasisHalves(wireBasis))
        {
            int element = leg.Element;
            bool rising = leg.Rising;
            var a = wire.ElementStart(element);
            var b = wire.ElementEnd(element);
            double length = wire.ElementLength(element);
            if (length <= 0) continue;
            var tHat = wire.ElementDirection(element);
            double radius = wire.ElementRadii[element];
            double slope = leg.Sign * ((rising ? 1.0 : -1.0) / length);

            foreach (var half in halves)
            {
                var (ia, ib, ic) = surface.Triangles[half.Triangle];
                var va = surface.Vertices[ia];
                var vb = surface.Vertices[ib];
                var vc = surface.Vertices[ic];
                double area = surface.TriangleAreas[half.Triangle];
                var pOpp = surface.Vertices[half.Opposite];

                // J = sign·(l/2A)(r′ − p_opp); ∇·J = sign·(l/A).
                double jScale = half.Sign * half.EdgeLength / (2 * area);
                double divergence = half.Sign * half.EdgeLength / area;

                var (v, c) = PairIntegral(a, b, length, tHat, radius, rising,
                    va, vb, vc, pOpp, jScale, k);
                vector += leg.Sign * v;
                charge += slope * divergence * c;
            }
        }
        return vectorFactor * vector + chargeFactor * charge;
    }

    /// <summary>Mutual impedance between a wire basis and the attachment DISC
    /// D(r′) = ρ̂/(2πρ) on the fan wedges, per unit junction coefficient.
    ///
    /// <para><b>Vector term only, and that is bookkeeping, not an approximation.</b> D's
    /// divergence is exactly δ²(v), and that delta is spent cancelling the attached wire
    /// half-hat's own endpoint charge — the two live in the SAME junction basis, so once the
    /// wire solver is asked for a half hat with no continuation (which is precisely what it
    /// builds for a grounded end), the pair is already charge-neutral. Adding a charge leg here
    /// would count that delta a second time.</para>
    ///
    /// <para>The disc integrates in the ray form r′ = v + t·e(s), where D dS′ = e(s)·|e ×
    /// (e_w − e_u)|/(2π|e|²) ds dt — the 1/ρ is cancelled by the polar measure and t drops out
    /// entirely, so the source is bounded. What is NOT bounded away is the kernel: a wire ending
    /// ON the sheet sits a distance of order its own radius from the disc's inner region, so
    /// both the disc's t and the touching wire element's s are panelled GEOMETRICALLY toward the
    /// contact. Away from the contact the ordinary distance-adaptive depth applies.</para>
    ///
    /// <para>For a PERPENDICULAR wire this term vanishes identically (t̂·D ≡ 0, the disc being
    /// purely in-plane) — which is the cheapest possible check that it is wired up correctly.</para>
    /// </summary>
    public static Complex MutualDisc(WireStructure wire, int wireBasis,
        AttachmentFan fan, SurfaceStructure surface, double k, double omega, int gradedPanels = 0)
    {
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        var v0 = fan.VertexPosition;

        // The fan's own length scale, for choosing the grading depth and the near/far dispatch.
        double meanRay = 0;
        int rayCount = 0;
        foreach (var wedge in fan.Wedges)
        {
            var (ta, tb, tc) = surface.Triangles[wedge.Triangle];
            int u = ta == fan.Vertex ? tb : ta;
            int w = ta == fan.Vertex ? tc : tb == fan.Vertex ? tc : tb;
            meanRay += (surface.Vertices[u] - v0).Length + (surface.Vertices[w] - v0).Length;
            rayCount += 2;
        }
        meanRay = rayCount > 0 ? meanRay / rayCount : 1;

        Complex vector = Complex.Zero;
        foreach (var leg in wire.BasisHalves(wireBasis))
        {
            int element = leg.Element;
            bool rising = leg.Rising;
            var a = wire.ElementStart(element);
            var b = wire.ElementEnd(element);
            double length = wire.ElementLength(element);
            if (length <= 0) continue;
            var tHat = wire.ElementDirection(element);
            double radius = wire.ElementRadii[element];

            int panels = gradedPanels > 0 ? gradedPanels : PanelCount(meanRay, radius);
            double dStart = (a - v0).Length, dEnd = (b - v0).Length;
            double touchTolerance = 1e-9 * Math.Max(length, 1e-12);
            (double A, double B)[] sPanels =
                dStart <= touchTolerance ? Graded(panels, towardZero: true)
                : dEnd <= touchTolerance ? Graded(panels, towardZero: false)
                : Uniform(FarDepth(Math.Min(dStart, dEnd), Math.Max(length, meanRay)));
            // The disc source always clusters at v; grade it every time.
            var tPanels = Graded(panels, towardZero: true);

            var (sNodes, sWeights) = GaussLegendre.Rule(6, 0, 1);
            var (rayNodes, rayWeights) = GaussLegendre.Rule(6, 0, 1);

            foreach (var (s0, s1) in sPanels)
            {
                double sSpan = s1 - s0;
                if (sSpan <= 0) continue;
                for (int si = 0; si < sNodes.Length; si++)
                {
                    double s = s0 + sSpan * sNodes[si];
                    var r = a + (b - a) * s;
                    double f = rising ? s : 1 - s;
                    double wS = leg.Sign * (sWeights[si] * sSpan * length * f);

                    foreach (var wedge in fan.Wedges)
                    {
                        var (ta, tb, tc) = surface.Triangles[wedge.Triangle];
                        var (iu, iw) = ta == fan.Vertex ? (tb, tc)
                            : tb == fan.Vertex ? (ta, tc) : (ta, tb);
                        var eu = surface.Vertices[iu] - v0;
                        var ew = surface.Vertices[iw] - v0;
                        double cross = Vector3D.Cross(eu, ew - eu).Length;

                        for (int ri = 0; ri < rayNodes.Length; ri++)
                        {
                            var e = eu * (1 - rayNodes[ri]) + ew * rayNodes[ri];
                            double scale = cross / (2 * Math.PI * e.LengthSquared);
                            double tangential = Vector3D.Dot(tHat, e) * scale;
                            if (tangential == 0) continue;
                            double wRay = rayWeights[ri] * tangential;

                            foreach (var (t0, t1) in tPanels)
                            {
                                double tSpan = t1 - t0;
                                if (tSpan <= 0) continue;
                                for (int ti = 0; ti < rayNodes.Length; ti++)
                                {
                                    var rp = v0 + e * (t0 + tSpan * rayNodes[ti]);
                                    var d = r - rp;
                                    double rEff = Math.Sqrt(d.LengthSquared + radius * radius);
                                    var (sin, cos) = Math.SinCos(k * rEff);
                                    vector += wS * wRay * rayWeights[ti] * tSpan
                                        * new Complex(cos, -sin) / rEff;
                                }
                            }
                        }
                    }
                }
            }
        }
        return vectorFactor * vector;
    }

    /// <summary>Geometric panels spanning [0, 1] and clustering toward one end — the standard
    /// treatment of an integrand whose scale collapses at a contact point. The count is chosen
    /// so the innermost panel is of order the wire radius, which is where the kernel's 1/R
    /// finally stops growing.</summary>
    private static (double A, double B)[] Graded(int count, bool towardZero)
    {
        var panels = new (double, double)[count];
        double edge = 1.0;
        for (int i = 0; i < count - 1; i++)
        {
            double inner = edge / 2;
            panels[i] = towardZero ? (inner, edge) : (1 - edge, 1 - inner);
            edge = inner;
        }
        panels[count - 1] = towardZero ? (0.0, edge) : (1 - edge, 1.0);
        return panels;
    }

    private static (double A, double B)[] Uniform(int count)
    {
        var panels = new (double, double)[count];
        for (int i = 0; i < count; i++) panels[i] = ((double)i / count, (double)(i + 1) / count);
        return panels;
    }

    private static int PanelCount(double scale, double radius) =>
        Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(scale / Math.Max(radius, 1e-30), 2))) + 1, 3, 14);

    private static int FarDepth(double separation, double scale) =>
        separation > 3 * scale ? 1 : separation > 1.2 * scale ? 2 : 4;

    /// <summary>∬ over one (wire element, triangle) pair: the vector moment ∬ f(s)(t̂·J) g and the
    /// scalar moment ∬ g, both with the wire's reduced kernel. Subdivision is chosen from the
    /// separation measured in units of the larger support, so near pairs cost more and far pairs
    /// stay cheap — the same policy the RWG self/near/far dispatch uses.</summary>
    private static (Complex Vector, Complex Scalar) PairIntegral(
        Vector3D a, Vector3D b, double length, Vector3D tHat, double radius, bool rising,
        Vector3D va, Vector3D vb, Vector3D vc, Vector3D pOpp, double jScale, double k)
    {
        var centroid = (va + vb + vc) * (1.0 / 3.0);
        var wireMid = (a + b) * 0.5;
        double separation = (centroid - wireMid).Length;
        double scale = Math.Max(length, (vb - va).Length);
        int depth = separation > 3 * scale ? 1 : separation > 1.2 * scale ? 2 : 4;

        var (sNodes, sWeights) = GaussLegendre.Rule(6, 0, 1);
        var (l1, l2, l3, tw) = TriangleQuadrature.Rule(5);

        Complex vector = Complex.Zero, scalar = Complex.Zero;
        double dsOuter = 1.0 / depth;
        for (int sub = 0; sub < depth; sub++)
        {
            for (int si = 0; si < sNodes.Length; si++)
            {
                double s = (sub + sNodes[si]) * dsOuter;
                var r = a + (b - a) * s;
                // The triangular basis: rises 0→1 across a rising element, falls 1→0 across a
                // falling one — matching the thin-wire solver's own convention.
                double f = rising ? s : 1 - s;
                double wS = sWeights[si] * dsOuter * length;

                for (int sd = 0; sd < depth; sd++)
                {
                    var (sa, sb, sc) = Subtriangle(va, vb, vc, sd, depth);
                    double subArea = 0.5 * Vector3D.Cross(sb - sa, sc - sa).Length;
                    for (int ti = 0; ti < tw.Length; ti++)
                    {
                        var rp = sa * l1[ti] + sb * l2[ti] + sc * l3[ti];
                        var d = r - rp;
                        double rEff = Math.Sqrt(d.LengthSquared + radius * radius);
                        var (sin, cos) = Math.SinCos(k * rEff);
                        var g = new Complex(cos, -sin) / rEff;
                        double wT = tw[ti] * subArea;

                        var jVec = (rp - pOpp) * jScale;
                        vector += wS * wT * f * Vector3D.Dot(tHat, jVec) * g;
                        scalar += wS * wT * g;
                    }
                }
            }
        }
        return (vector, scalar);
    }

    /// <summary>One of <paramref name="depth"/> sub-triangles used to refine a near pair. Depth 1
    /// returns the triangle itself; higher depths walk a uniform barycentric strip decomposition,
    /// which is enough because the integrand here is smooth (the wire radius keeps the kernel
    /// bounded) and only needs resolving, not desingularizing.</summary>
    private static (Vector3D A, Vector3D B, Vector3D C) Subtriangle(
        Vector3D va, Vector3D vb, Vector3D vc, int index, int depth)
    {
        if (depth <= 1) return (va, vb, vc);
        // Split along the a→b edge into `depth` slivers sharing vertex c: exact cover, and the
        // union of their areas is the parent's by construction.
        double t0 = (double)index / depth, t1 = (double)(index + 1) / depth;
        var p0 = va + (vb - va) * t0;
        var p1 = va + (vb - va) * t1;
        return (p0, p1, vc);
    }
}
