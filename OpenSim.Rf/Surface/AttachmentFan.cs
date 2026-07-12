using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The attachment mode at a probe junction vertex v: the classical 1/ρ disc current
///
///   D(r) = ρ̂/(2πρ) on the fan triangles around v,
///
/// whose divergence is EXACTLY δ²(v) — it cancels the tube current's endpoint delta
/// identically, so the junction basis carries no point charge at any scale. (The
/// vertex-anchored affine fan ρ_v = (r−v)/2Aᵢ was tried first per the plan and
/// MEASURED unsound: with honest charge bookkeeping its unavoidable +δ(v) point
/// charge has self-energy ~K_Φ(a)/(jω) — a ~0.04 pF series capacitor that choked the
/// quasi-static feed to 0.20 pF against the physical 1.32 pF plate value; with the
/// point term dropped, the power ledger read 1.8× at resonance. The 1/ρ fan is
/// classical for a reason.)
///
/// D is chargeless on the wedges, and its 1/ρ is cancelled by the polar measure
/// (dS = ρ dρ dφ), so every D integral is an ordinary 2-D quadrature — in the ray
/// parametrization r′ = v + t·e(s) the radial variable drops out of D dS′ entirely.
/// Only the kernel's 1/R needs panelling when the test point is close; test points
/// use the reduced ρ_eff = √(ρ² + a²) (the junction current physically rides the
/// tube-top region of radius a). Across each wedge's OUTER edge D exits with flux
/// θᵢ/2π (the wedge angle at v — closed form); a HALF-RWG on the outward neighbor
/// absorbs it (βᵢ = ±θᵢ/(2π·lᵢ)), its normal flux vanishes on the neighbor's other
/// edges (the (p⁻ − r) form is edge-parallel there), and its ordinary −l/A divergence
/// is where the junction current's charge finally accumulates — standard pair-moment
/// machinery, no line charges, no new singular families.
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
        Wedges = wedges;
        TotalAngle = total;
    }

    /// <summary>The disc current's vector potential at one test point, per unit
    /// junction current: A(r) = ∫ D(r′) G_A(ρ_eff) dS′ over the fan (in-plane
    /// components; G_A is the boundary table's FULL layered kernel).</summary>
    public (Complex Ax, Complex Ay) DiscPotential(LayeredKernelTable kernel,
        SurfaceStructure surface, Vector3D r)
    {
        Complex ax = Complex.Zero, ay = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(6, 0, 1);
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            // Ray form r′ = v + t·e(s), e(s) = (1−s)eu + s·ew, t ∈ (0,1]:
            // dS′ = t·|e × (ew−eu)| ds dt and D = e/(t·|e|²)/2π ⇒ the t cancels in
            // D dS′ = e(s)·|e×(ew−eu)|/(2π|e(s)|²) ds dt.
            double cross = Vector3D.Cross(eu, ew - eu).Length;
            for (int si = 0; si < nodes.Length; si++)
            {
                var e = eu * (1 - nodes[si]) + ew * nodes[si];
                double scale = cross / (2 * Math.PI * e.LengthSquared);
                double wx = e.X * scale, wy = e.Y * scale;
                for (int ti = 0; ti < nodes.Length; ti++)
                {
                    var rPrime = VertexPosition + e * nodes[ti];
                    double dx = r.X - rPrime.X, dy = r.Y - rPrime.Y;
                    double rhoEff = Math.Sqrt(dx * dx + dy * dy + _radiusFloor * _radiusFloor);
                    var (gA, _) = kernel.EvaluateKernels(rhoEff);
                    var weight = weights[si] * weights[ti] * gA;
                    ax += wx * weight;
                    ay += wy * weight;
                }
            }
        }
        return (ax, ay);
    }

    /// <summary>∬ D·D′ G_A — the disc's vector self term (outer in the same ray form,
    /// panelled by refinement of the radial direction being unnecessary: the measure
    /// cancellation makes the integrand bounded except G_A's own peak, softened by
    /// the ρ_eff floor).</summary>
    public Complex DiscSelf(LayeredKernelTable kernel, SurfaceStructure surface)
    {
        Complex sum = Complex.Zero;
        var (nodes, weights) = GaussLegendre.Rule(6, 0, 1);
        foreach (var wedge in Wedges)
        {
            var (a, b, c) = surface.Triangles[wedge.Triangle];
            var (u, w) = a == Vertex ? (b, c) : b == Vertex ? (a, c) : (a, b);
            var eu = surface.Vertices[u] - VertexPosition;
            var ew = surface.Vertices[w] - VertexPosition;
            double cross = Vector3D.Cross(eu, ew - eu).Length;
            for (int si = 0; si < nodes.Length; si++)
            {
                var e = eu * (1 - nodes[si]) + ew * nodes[si];
                double scale = cross / (2 * Math.PI * e.LengthSquared);
                for (int ti = 0; ti < nodes.Length; ti++)
                {
                    var rPrime = VertexPosition + e * nodes[ti];
                    var (ax, ay) = DiscPotential(kernel, surface, rPrime);
                    sum += weights[si] * weights[ti] * scale * (e.X * ax + e.Y * ay);
                }
            }
        }
        return sum;
    }
}
