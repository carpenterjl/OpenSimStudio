using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The attachment mode at a probe junction vertex v. On each fan wedge (triangle v, u, w with
/// angle θᵢ at v and outer edge u–w of length lᵢ) the current is the curl of a stream function,
/// J = ∇ψ × ẑ, in the ray parametrization r′ = v + t·e(s), e(s) = (1−s)(u−v) + s(w−v):
///
///   ψ(s, t) = [(1 − t)·Δφ(s) + t·θᵢ·s] / 2π,
///
/// Δφ(s) the angle from u−v to e(s). Being a curl it has no divergence on the wedge, so it carries
/// no charge there; ψ is constant along both radial edges, so no current crosses them; at the outer
/// edge (t = 1) ψ is linear in s, so the current crosses it with the UNIFORM density γᵢ = θᵢ/(2πlᵢ)
/// — the density of the half-RWG on the outward neighbour that takes it up — so it is continuous
/// there too; and as t → 0 it is the classical isotropic disc ρ̂/(2πρ). Around v, ψ steps by
/// Σθᵢ/2π = 1: the divergence is exactly δ²(v), cancelling the tube current's endpoint delta, so the
/// junction basis carries no point charge. The half-RWG's ordinary −l/A divergence is where the
/// junction current's charge accumulates.
///
/// <para>In the ray variables (dS′ = t·|e′ × e| ds dt, e′ = w − u):
///   J dS′ = (1/2π)·[((1 − t)φ′(s) + tθᵢ)·e(s) − t(θᵢs − Δφ(s))·e′] ds dt,   φ′ = |e′ × e|/|e|²,
/// bounded and linear in t (<see cref="RayCurrent"/>), so every junction-current integral is an
/// ordinary 2-D quadrature; only the kernel's 1/R needs panelling when the test point is close.
/// Test points use the reduced ρ_eff = √(ρ² + a²) (the junction current physically rides the
/// tube-top region of radius a).</para>
///
/// <para>FU-4 (audit WH-C). The classical disc alone (ψ = Δφ/2π) leaves the outer edge with density
/// h/(2π(h² + x²)), not the half-RWG's uniform θ/(2πl): a line charge of zero net along each outer
/// edge, which was omitted — +10 % / −17 % on an equilateral wedge, ±80 % with a vertex close to a
/// neighbour, and a monopole's reactance 11 % low on such a fan. A purely radial current with the
/// uniform edge density (ψ = θs/2π at every t) has no line charge either, but its 1/ρ part is no
/// longer isotropic at v, which adds ln(b/a) inductance: 4 % on a regular mesh, 5 % on the skewed
/// fan. The blend keeps both properties. (The vertex-anchored affine fan ρ_v = (r−v)/2Aᵢ was tried
/// before any of these and MEASURED unsound: its +δ(v) point charge has self-energy ~K_Φ(a)/(jω), a
/// ~0.04 pF series capacitor that choked the quasi-static feed to 0.20 pF against the physical
/// 1.32 pF plate value.)</para>
/// </summary>
internal sealed class AttachmentFan
{
    /// <summary>One fan wedge: the incident triangle, its outer-edge RWG basis, the
    /// wedge angle at v, the half-RWG weight γ = θ/(2πl) on the outward neighbor
    /// (POSITIVE — the half is always the (p_opp − r) form pointing INTO the neighbor,
    /// depositing positive charge; found live: carrying the RWG orientation sign here
    /// flips the halves on fan-minus-side wedges, the charge deposits partially cancel,
    /// and the junction sees a near-free charge path — C_quasi-static read 50 pF
    /// against the 1.32 pF plate), the RWG orientation sign (used ONLY when mapping
    /// the transported current onto the full edge basis for far-field consumers), and
    /// the neighbor's identity/opposite vertex for the half composition.</summary>
    public readonly record struct Wedge(
        int Triangle, int EdgeBasis, double Angle, double Gamma, double OrientationSign,
        int NeighborTriangle, int NeighborOpposite);

    private readonly double _radiusFloor;

    /// <summary>Gauss order per direction on each cell of the disc potential's inner rule,
    /// and the cell-size-to-distance ratio above which a cell is split. Measured against a
    /// rule of order 8 at ratio 0.5: the potential agrees to better than 0.1 % anywhere in
    /// the fan for a/b from 0.3 down to 0.001.</summary>
    internal const int PotentialOrder = 4;
    internal const double PotentialResolve = 1.0;
    private const int MaxSplitDepth = 60;

    public int Vertex { get; }
    public Vector3D VertexPosition { get; }
    public IReadOnlyList<Wedge> Wedges { get; }

    /// <summary>Total wedge angle — 2π at an interior vertex; Σθᵢ/2π = 1 is the
    /// discrete junction-continuity identity the tests assert.</summary>
    public double TotalAngle { get; }

    public AttachmentFan(SurfaceStructure surface, int vertex, double radiusFloor)
    {
        Vertex = vertex;
        VertexPosition = surface.Vertices[vertex];
        _radiusFloor = radiusFloor;
        var edgeLookup = new Dictionary<(int, int), int>();
        for (int e = 0; e < surface.Edges.Count; e++)
            edgeLookup[(surface.Edges[e].V1, surface.Edges[e].V2)] = e;

        var wedges = new List<Wedge>();
        double total = 0;
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var (a, b, c) = surface.Triangles[t];
            if (a != vertex && b != vertex && c != vertex) continue;
            var (u, w) = a == vertex ? (b, c) : b == vertex ? (a, c) : (a, b);
            var du = surface.Vertices[u] - VertexPosition;
            var dw = surface.Vertices[w] - VertexPosition;
            double angle = Math.Acos(Math.Clamp(
                Vector3D.Dot(du, dw) / (du.Length * dw.Length), -1, 1));
            total += angle;

            var key = (Math.Min(u, w), Math.Max(u, w));
            if (!edgeLookup.TryGetValue(key, out int edge)
                || surface.Edges[edge].MinusTriangle < 0)
                throw new InvalidOperationException(
                    "A fan triangle's outer edge at the probe vertex is a boundary edge — the "
                    + "junction current cannot continue into the patch there. Move the probe off the rim.");

            var rwg = surface.Edges[edge];
            // The half-RWG lives on the triangle OPPOSITE the fan triangle; the
            // (p_opp − r) form always flows INTO the neighbor, continuing D's outward
            // crossing regardless of the RWG's own plus/minus orientation.
            int neighbor = rwg.PlusTriangle == t ? rwg.MinusTriangle : rwg.PlusTriangle;
            int neighborOpposite = rwg.PlusTriangle == t ? rwg.MinusOpposite : rwg.PlusOpposite;
            double sign = rwg.PlusTriangle == t ? 1.0 : -1.0;
            double gamma = angle / (2 * Math.PI * rwg.Length);

            wedges.Add(new Wedge(t, edge, angle, gamma, sign, neighbor, neighborOpposite));
        }
        if (wedges.Count == 0)
            throw new InvalidOperationException("No triangles are incident at the probe vertex.");
        // The fan must CLOSE. Its wedges carry the junction current away in shares
        // γ = θ/(2πl), so Σθ = 2π is exactly the statement that all of it leaves; at a rim
        // vertex the incident angles sum to less and the missing share is silently lost.
        // Checking the outer edges alone does not catch this: at a CORNER every wedge's
        // opposite edge is interior, so the fan builds happily with a quarter turn of angle.
        if (Math.Abs(total - 2 * Math.PI) > 1e-9 * 2 * Math.PI)
            throw new InvalidOperationException(
                $"The attachment vertex is on the sheet's rim: its incident angles sum to "
                + $"{total:g6} rad, not 2π, so the fan cannot carry the whole junction current "
                + "away (the γ = θ/2π shares would sum to "
                + $"{total / (2 * Math.PI):g4}). Move the attachment inside the sheet.");
        Wedges = wedges;
        TotalAngle = total;
    }

    /// <summary>J dS′ per ds dt at ray coordinates (s, t) of a wedge with edge vectors eu, ew from
    /// v and angle θ (see the class remarks).</summary>
    internal static Vector3D RayCurrent(Vector3D eu, Vector3D ew, double angle, double s, double t)
    {
        var e = eu * (1 - s) + ew * s;
        var edge = ew - eu;
        double cross = Vector3D.Cross(edge, e).Length;
        double slope = cross / e.LengthSquared;
        double turned = Math.Atan2(Vector3D.Cross(eu, e).Length, Vector3D.Dot(eu, e));
        return (e * ((1 - t) * slope + t * angle) - edge * (t * (angle * s - turned))) * (1 / (2 * Math.PI));
    }

    /// <summary>The junction's fan current at a point of the fan, per unit junction coefficient
    /// (<see cref="RayCurrent"/> over the measure t·|e′ × e|). Zero off the fan and at v.</summary>
    public Vector3D DiscCurrent(SurfaceStructure surface, Vector3D r)
    {
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            var p = r - VertexPosition;
            // Barycentric (t·(1−s), t·s) of p in the wedge's (eu, ew) frame.
            var normal = Vector3D.Cross(eu, ew);
            double area2 = normal.LengthSquared;
            double alpha = Vector3D.Dot(Vector3D.Cross(p, ew), normal) / area2;
            double beta = Vector3D.Dot(Vector3D.Cross(eu, p), normal) / area2;
            double t = alpha + beta;
            const double slack = 1e-12;
            if (alpha < -slack || beta < -slack || t > 1 + slack || t <= 0) continue;
            double s = beta / t;
            var e = eu * (1 - s) + ew * s;
            double measure = t * Vector3D.Cross(ew - eu, e).Length;
            return RayCurrent(eu, ew, wedge.Angle, s, t) * (1 / measure);
        }
        return Vector3D.Zero;
    }

    /// <summary>The junction surface current's in-plane spectral transform per unit
    /// junction coefficient, J̃(k⃗) = ∫ (D + Σᵢγᵢ Hᵢ) e^{+jk⃗·r′} dS — the disc in the
    /// ray form (its t-free measure) plus each half-RWG's triangle moment. This is the
    /// EXACT far field of the junction current, replacing the mesh-scale fold onto the
    /// fan edges (which over-radiates and omits the disc entirely).</summary>
    public (Complex Jx, Complex Jy) CurrentTransform(SurfaceStructure surface, double kx, double ky)
    {
        Complex jx = Complex.Zero, jy = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(6, 0, 1);
        // Fan current: ray r′ = v + t·e(s), J dS′ = RayCurrent ds dt (see the class remarks).
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            for (int si = 0; si < nodes.Length; si++)
            {
                var e = eu * (1 - nodes[si]) + ew * nodes[si];
                for (int ti = 0; ti < nodes.Length; ti++)
                {
                    var rPrime = VertexPosition + e * nodes[ti];
                    var (sinP, cosP) = Math.SinCos(kx * rPrime.X + ky * rPrime.Y);
                    var phase = new Complex(cosP, sinP);
                    var j = RayCurrent(eu, ew, wedge.Angle, nodes[si], nodes[ti]) * (weights[si] * weights[ti]);
                    jx += j.X * phase;
                    jy += j.Y * phase;
                }
            }
        }
        // Half-RWGs on the outward neighbors: current γᵢ·(lᵢ/2A)(p_opp − r).
        var (t1, t2, t3, wq) = TriangleQuadrature.Rule(5);
        foreach (var wedge in Wedges)
        {
            int t = wedge.NeighborTriangle;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];
            var pOpp = surface.Vertices[wedge.NeighborOpposite];
            double coeff = wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length / (2 * area);
            for (int i = 0; i < wq.Length; i++)
            {
                var r = va * t1[i] + vb * t2[i] + vc * t3[i];
                var fDir = pOpp - r;
                var (sinP, cosP) = Math.SinCos(kx * r.X + ky * r.Y);
                var phase = new Complex(cosP, sinP);
                double weight = wq[i] * area * coeff;
                jx += weight * fDir.X * phase;
                jy += weight * fDir.Y * phase;
            }
        }
        return (jx, jy);
    }

    /// <summary>The junction surface current's FULL 3-D radiation vector per unit junction
    /// coefficient, N(r̂) = ∫ (D + Σᵢγᵢ Hᵢ) e^{+jk r̂·r′} dS — the free-space analogue of
    /// <see cref="CurrentTransform"/>, which carries only the in-plane phase because a layered
    /// stackup fixes z at the metal plane. Here the sheet may sit at any height and in any
    /// orientation, so the phase takes the true 3-D dot product and all three current components
    /// are returned. Same quadrature, same ray form; the two are algebraically identical for a
    /// horizontal sheet at z = 0.</summary>
    public (Complex Jx, Complex Jy, Complex Jz) CurrentTransform3D(SurfaceStructure surface,
        double k, Vector3D direction)
    {
        Complex jx = Complex.Zero, jy = Complex.Zero, jz = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(6, 0, 1);
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            for (int si = 0; si < nodes.Length; si++)
            {
                var e = eu * (1 - nodes[si]) + ew * nodes[si];
                for (int ti = 0; ti < nodes.Length; ti++)
                {
                    var rPrime = VertexPosition + e * nodes[ti];
                    var (sinP, cosP) = Math.SinCos(k * Vector3D.Dot(direction, rPrime));
                    var phase = new Complex(cosP, sinP);
                    var j = RayCurrent(eu, ew, wedge.Angle, nodes[si], nodes[ti]) * (weights[si] * weights[ti]);
                    jx += j.X * phase;
                    jy += j.Y * phase;
                    jz += j.Z * phase;
                }
            }
        }
        var (t1, t2, t3, wq) = TriangleQuadrature.Rule(5);
        foreach (var wedge in Wedges)
        {
            int t = wedge.NeighborTriangle;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];
            var pOpp = surface.Vertices[wedge.NeighborOpposite];
            double coeff = wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length / (2 * area);
            for (int i = 0; i < wq.Length; i++)
            {
                var r = va * t1[i] + vb * t2[i] + vc * t3[i];
                var fDir = pOpp - r;
                var (sinP, cosP) = Math.SinCos(k * Vector3D.Dot(direction, r));
                var phase = new Complex(cosP, sinP);
                double weight = wq[i] * area * coeff;
                jx += weight * fDir.X * phase;
                jy += weight * fDir.Y * phase;
                jz += weight * fDir.Z * phase;
            }
        }
        return (jx, jy, jz);
    }

    /// <summary>The disc current's vector potential at one test point, per unit
    /// junction current: A(r) = ∫ D(r′) G_A(ρ_eff) dS′ over the fan (in-plane
    /// components; G_A is the boundary table's FULL layered kernel).</summary>
    public (Complex Ax, Complex Ay) DiscPotential(LayeredKernelTable kernel,
        SurfaceStructure surface, Vector3D r) =>
        DiscPotential(new LayeredRadialGaKernel(kernel), surface, r);

    /// <summary>As above, against any radial G_A source (<see cref="IRadialGaKernel"/>) — the
    /// only kernel fact the disc integral uses. The single-slab overload delegates here, so the
    /// shipped probe path runs this exact code.
    ///
    /// <para>The source is smooth in the ray variables but the kernel is not: at a test point
    /// inside or beside the fan it peaks to 1/a over a region of size a. Each wedge's (s, t)
    /// square is therefore split, longer physical side first, until every cell is no larger
    /// than its reduced distance to the test point; a far test point takes one cell per
    /// wedge. (The earlier fixed 6 × 6 rule per wedge was 9 % high to 19 % low inside the fan
    /// once a/b fell below 0.03.)</para></summary>
    public (Complex Ax, Complex Ay) DiscPotential(IRadialGaKernel kernel,
        SurfaceStructure surface, Vector3D r) =>
        DiscPotential(kernel, surface, r, PotentialOrder, PotentialResolve);

    internal (Complex Ax, Complex Ay) DiscPotential(IRadialGaKernel kernel,
        SurfaceStructure surface, Vector3D r, int order, double resolve)
    {
        Complex ax = Complex.Zero, ay = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(order, 0, 1);
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            WedgePotential(kernel, eu, ew, wedge.Angle, r, 0, 1, 0, 1, 0, nodes, weights, resolve,
                ref ax, ref ay);
        }
        return (ax, ay);
    }

    /// <summary>One cell [s0, s1] × [t0, t1] of one wedge (angle θ at v) in the ray form
    /// r′ = v + t·e(s), e(s) = (1−s)eu + s·ew, where J dS′ = RayCurrent ds dt.</summary>
    private void WedgePotential(IRadialGaKernel kernel, Vector3D eu, Vector3D ew, double angle, Vector3D r,
        double s0, double s1, double t0, double t1, int depth,
        double[] nodes, double[] weights, double resolve, ref Complex ax, ref Complex ay)
    {
        double sm = 0.5 * (s0 + s1), tm = 0.5 * (t0 + t1);
        var em = eu * (1 - sm) + ew * sm;
        double radial = em.Length * (t1 - t0);
        double arc = (ew - eu).Length * (s1 - s0) * t1;
        var centre = VertexPosition + em * tm;
        double cx = r.X - centre.X, cy = r.Y - centre.Y;
        double distance = Math.Sqrt(cx * cx + cy * cy + _radiusFloor * _radiusFloor);
        if (Math.Max(radial, arc) > resolve * distance && depth < MaxSplitDepth)
        {
            if (radial >= arc)
            {
                WedgePotential(kernel, eu, ew, angle, r, s0, s1, t0, tm, depth + 1, nodes, weights, resolve, ref ax, ref ay);
                WedgePotential(kernel, eu, ew, angle, r, s0, s1, tm, t1, depth + 1, nodes, weights, resolve, ref ax, ref ay);
            }
            else
            {
                WedgePotential(kernel, eu, ew, angle, r, s0, sm, t0, t1, depth + 1, nodes, weights, resolve, ref ax, ref ay);
                WedgePotential(kernel, eu, ew, angle, r, sm, s1, t0, t1, depth + 1, nodes, weights, resolve, ref ax, ref ay);
            }
            return;
        }

        double cell = (s1 - s0) * (t1 - t0);
        for (int si = 0; si < nodes.Length; si++)
        {
            double sv = s0 + (s1 - s0) * nodes[si];
            var e = eu * (1 - sv) + ew * sv;
            for (int ti = 0; ti < nodes.Length; ti++)
            {
                double tv = t0 + (t1 - t0) * nodes[ti];
                var rPrime = VertexPosition + e * tv;
                double dx = r.X - rPrime.X, dy = r.Y - rPrime.Y;
                double rhoEff = Math.Sqrt(dx * dx + dy * dy + _radiusFloor * _radiusFloor);
                var weight = cell * weights[si] * weights[ti] * kernel.EvaluateGa(rhoEff);
                var j = RayCurrent(eu, ew, angle, sv, tv);
                ax += j.X * weight;
                ay += j.Y * weight;
            }
        }
    }

    /// <summary>∬ D·D′ G_A — the disc's vector self term.
    ///
    /// <para>The outer rule must NOT share its nodes with the inner one. It once did (the same
    /// 6 × 6 Gauss points on the same wedge for both integrals), so every node met itself at
    /// zero separation and contributed G_A(a) = µ₀/(4πa): a term growing as 1/a that the true
    /// integral, which grows only as ln(1/a), does not contain. It read 1.17× at a/b = 0.03
    /// and 3.5× at 0.003, and since it is purely reactive the power ledger could not see it —
    /// it appeared as a series inductance at the junction that grew with the mesh size.</para>
    ///
    /// <para>Here the inner integral is <see cref="DiscPotential(IRadialGaKernel, SurfaceStructure, Vector3D)"/>,
    /// which resolves the kernel's peak around whatever point it is handed, and the outer one
    /// samples it on panels graded toward the vertex (the potential varies on the scale a
    /// there, and on the scale of the fan elsewhere). Order 3 on both sides is within 2e-4 of
    /// order 8 at half the cell ratio for a/b from 0.03 to 0.003, and within the 0.5 % bracket
    /// of the closed-form circular disc down to a/b = 0.001.</para></summary>
    public Complex DiscSelf(LayeredKernelTable kernel, SurfaceStructure surface) =>
        DiscSelf(new LayeredRadialGaKernel(kernel), surface);

    /// <summary>As above, against any radial G_A source.</summary>
    public Complex DiscSelf(IRadialGaKernel kernel, SurfaceStructure surface) =>
        DiscSelf(kernel, surface, outerOrder: 3, sPanels: 1, innerOrder: 3, PotentialResolve);

    internal Complex DiscSelf(IRadialGaKernel kernel, SurfaceStructure surface,
        int outerOrder, int sPanels, int innerOrder, double innerResolve) =>
        DiscAgainst(kernel, surface, this, outerOrder, sPanels, innerOrder, innerResolve);

    /// <summary>∬ D·A(D_source) — this fan's disc tested against another junction's disc (two
    /// pins on one sheet). The same outer rule as <see cref="DiscSelf(IRadialGaKernel, SurfaceStructure)"/>;
    /// the inner potential is the source fan's, which resolves its own peak.</summary>
    internal Complex DiscAgainst(IRadialGaKernel kernel, SurfaceStructure surface, AttachmentFan source) =>
        DiscAgainst(kernel, surface, source, outerOrder: 3, sPanels: 1, innerOrder: 3, PotentialResolve);

    private Complex DiscAgainst(IRadialGaKernel kernel, SurfaceStructure surface, AttachmentFan source,
        int outerOrder, int sPanels, int innerOrder, double innerResolve)
    {
        Complex sum = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(outerOrder, 0, 1);
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;

            // Radial panels: halving toward the vertex until a panel is inside the radius.
            double reach = Math.Min(eu.Length, ew.Length);
            var breaks = new List<double> { 1.0 };
            while (breaks[^1] * reach > _radiusFloor && breaks.Count < MaxSplitDepth)
                breaks.Add(breaks[^1] / 2);
            breaks.Add(0.0);

            for (int sp = 0; sp < sPanels; sp++)
                for (int si = 0; si < nodes.Length; si++)
                {
                    double sv = (sp + nodes[si]) / sPanels;
                    var e = eu * (1 - sv) + ew * sv;
                    double scale = weights[si] / sPanels;
                    for (int tp = 0; tp + 1 < breaks.Count; tp++)
                    {
                        double tHigh = breaks[tp], tLow = breaks[tp + 1];
                        for (int ti = 0; ti < nodes.Length; ti++)
                        {
                            double tv = tLow + (tHigh - tLow) * nodes[ti];
                            var rPrime = VertexPosition + e * tv;
                            var (ax, ay) = source.DiscPotential(kernel, surface, rPrime,
                                innerOrder, innerResolve);
                            var j = RayCurrent(eu, ew, wedge.Angle, sv, tv);
                            sum += weights[ti] * (tHigh - tLow) * scale * (j.X * ax + j.Y * ay);
                        }
                    }
                }
        }
        return sum;
    }
}
