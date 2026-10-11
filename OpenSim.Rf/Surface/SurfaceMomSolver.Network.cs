using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>A sheet solved with several ports at one frequency.</summary>
/// <param name="Admittance">Y[i, j]: current through port i per volt across port j with
/// every other port's gap closed.</param>
/// <param name="EdgeCurrents">For each port, the RWG coefficients with one volt across it
/// and the other gaps closed.</param>
public sealed record SurfaceMultiPortSolution(double FrequencyHz, Complex[,] Admittance, Complex[][] EdgeCurrents);

/// <summary>
/// Ohmic loss of sheet metal as an impedance boundary: the tangential field on the sheet is
/// Z_s·J instead of zero, which adds Z_s·∫f_m·f_n dS (the RWG Gram matrix) to the moment
/// matrix, and the power turned to heat is ½·Re Z_s·∫|J|² dS.
/// </summary>
public static class SheetLoss
{
    /// <summary>
    /// Impedance per square of a copper sheet of finite thickness carrying its current on
    /// both faces (a strip or a plate in free space), or on one (a patch or a trace close
    /// over its ground plane, where the current is on the face toward the plane):
    /// (k/σ)·coth(k·t) for one face, half of (k/σ)·coth(k·t/2) for two, k = (1 + j)/δ. Both
    /// are 1/(σt) at DC.
    /// </summary>
    public static Complex CopperSheet(double frequencyHz, double conductivity, double thickness, bool bothFaces)
    {
        if (!(conductivity > 0 && thickness > 0 && frequencyHz > 0))
            throw new ArgumentException("Conductivity, thickness and frequency must be positive.");
        Complex k = Complex.Sqrt(new Complex(0, 2 * Math.PI * frequencyHz * RfConstants.Mu0 * conductivity));
        Complex Coth(Complex x) => x.Magnitude < 1e-3 ? 1 / x + x / 3 : 1 / Complex.Tanh(x);
        return bothFaces
            ? 0.5 * k / conductivity * Coth(k * thickness / 2)
            : k / conductivity * Coth(k * thickness);
    }

    /// <summary>
    /// The stopping distance of a free edge of sheet metal t thick (FU-37): the zero-thickness
    /// edge current, k/√x on each face, integrated from Δ to a distance X far from the edge, carries
    /// the loss of the thick edge, Δ = t/(4π·e^π) ≈ t/291.
    ///
    /// <para>Derivation. Outside a semi-infinite plate {x &gt; 0, −t &lt; y &lt; 0} the map
    /// z = (t/π)[ζ√(ζ² − 1) − arccosh ζ] takes the upper ζ half-plane onto the exterior (the end face is
    /// ζ ∈ [−1, 1], the faces ζ &gt; 1 and ζ &lt; −1), with dz/dζ = (2t/π)√(ζ² − 1). The edge's field is
    /// w = ζ (real on the metal, ≈ √(πz/t) far away, the thin edge's), and the surface current is
    /// |dw/dz|, so ∫|J|² ds = ∫ dξ/|dz/dξ| = (π/2t)[π + 2 arccosh Ξ] over the end face and both faces
    /// out to ξ = ±Ξ, where x = (t/π)(Ξ² + O(ln Ξ)). The thin edge with the same far field carries
    /// (1/2)√(π/t)/√x on each face: 2·(π/4t)·ln(X/Δ). Equal as X → ∞:
    /// π + ln(4πX/t) = ln(X/Δ), so Δ = t·e^{−π}/(4π). It holds where the skin depth is small
    /// against t, so the current lies on the surface; the 2D volume solve of a thick strip agrees
    /// (EdgeLossTests).</para>
    /// </summary>
    public static double StoppingDistance(double thickness) => thickness * Math.Exp(-Math.PI) / (4 * Math.PI);

    /// <summary>Surface impedance (1 + j)·√(πfµ₀/σ) of a round conductor whose skin depth is
    /// well inside it, per square of its surface.</summary>
    public static Complex RoundWire(double frequencyHz, double conductivity)
    {
        if (!(conductivity > 0 && frequencyHz > 0))
            throw new ArgumentException("Conductivity and frequency must be positive.");
        double rs = Math.Sqrt(Math.PI * frequencyHz * RfConstants.Mu0 / conductivity);
        return new Complex(rs, rs);
    }

    /// <summary>The value of every basis on a triangle at its three edge midpoints (a rule
    /// exact for the quadratic f_m·f_n), as (basis, vector) lists per midpoint.</summary>
    private static IEnumerable<(double Weight, (int Basis, Vector3D Value)[] Bases)> Midpoints(SurfaceStructure s, int t)
    {
        var (a, b, c) = s.Triangles[t];
        Vector3D va = s.Vertices[a], vb = s.Vertices[b], vc = s.Vertices[c];
        double area = s.TriangleAreas[t];
        foreach (var mid in new[] { 0.5 * (va + vb), 0.5 * (vb + vc), 0.5 * (vc + va) })
        {
            var supports = s.TriangleSupports[t];
            var values = new (int, Vector3D)[supports.Count];
            for (int i = 0; i < supports.Count; i++)
            {
                var (basis, sign, opposite) = supports[i];
                values[i] = (basis, sign * s.Edges[basis].Length / (2 * area) * (mid - s.Vertices[opposite]));
            }
            yield return (area / 3, values);
        }
    }

    /// <summary>The loss term Z_s·∫f_m·f_n added to the moment matrix; with <paramref name="edge"/>
    /// (and a frequency where it applies) the current along each free edge takes the edge's loss
    /// instead (<see cref="SheetEdge"/>), the same quadratic form <see cref="OhmicPower(SurfaceStructure, IReadOnlyList{Complex}, Complex, SheetEdge?, double)"/>
    /// evaluates, so the port's power and the heat stay one ledger.</summary>
    internal static void AddTo(ComplexDenseMatrix z, SurfaceStructure surface, Complex sheetImpedance,
        SheetEdge? edge = null, double frequencyHz = 0)
    {
        if (sheetImpedance == Complex.Zero) return;
        var bands = EdgeBands(surface, edge, frequencyHz);
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            // In a band only the current across the edge keeps the sheet's own loss.
            Vector3D? across = bands.BandOf[t] >= 0 ? bands.Bands[bands.BandOf[t]].Across : null;
            foreach (var (weight, bases) in Midpoints(surface, t))
                foreach (var (m, fm) in bases)
                    foreach (var (n, fn) in bases)
                        z[m, n] += sheetImpedance * weight * (across is { } d
                            ? Vector3D.Dot(fm, d) * Vector3D.Dot(fn, d)
                            : Vector3D.Dot(fm, fn));
        }
        if (bands.Bands.Count == 0) return;
        Complex edgeImpedance = edge!.EdgeImpedance(frequencyHz);
        foreach (var band in bands.Bands)
            foreach (var (m, am) in band.Moments)
                foreach (var (n, an) in band.Moments)
                    z[m, n] += edgeImpedance * band.Kappa * am * an;
    }

    /// <summary>∫|J|² dS over the sheet [A²] for the given RWG coefficients.</summary>
    public static double CurrentSquared(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents)
    {
        double total = 0;
        for (int t = 0; t < surface.Triangles.Count; t++)
            total += TriangleCurrentSquared(surface, edgeCurrents, t, null);
        return total;
    }

    private static double TriangleCurrentSquared(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents, int t,
        Vector3D? along)
    {
        double total = 0;
        foreach (var (weight, bases) in Midpoints(surface, t))
        {
            Complex jx = Complex.Zero, jy = Complex.Zero, jz = Complex.Zero;
            foreach (var (n, fn) in bases)
            {
                jx += edgeCurrents[n] * fn.X;
                jy += edgeCurrents[n] * fn.Y;
                jz += edgeCurrents[n] * fn.Z;
            }
            if (along is { } d)
            {
                Complex j = jx * d.X + jy * d.Y + jz * d.Z;
                total += weight * (j.Real * j.Real + j.Imaginary * j.Imaginary);
            }
            else
                total += weight * (jx.Magnitude * jx.Magnitude + jy.Magnitude * jy.Magnitude + jz.Magnitude * jz.Magnitude);
        }
        return total;
    }

    /// <summary>Power turned to heat in the sheet [W] for peak-amplitude currents.</summary>
    public static double OhmicPower(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents, Complex sheetImpedance) =>
        0.5 * sheetImpedance.Real * CurrentSquared(surface, edgeCurrents);

    /// <summary>The same, with the free edges of a sheet of finite thickness taking the edge's loss
    /// (<see cref="SheetEdge"/>) where it applies at <paramref name="frequencyHz"/>.</summary>
    public static double OhmicPower(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents, Complex sheetImpedance,
        SheetEdge? edge, double frequencyHz)
    {
        var bands = EdgeBands(surface, edge, frequencyHz);
        if (bands.Bands.Count == 0) return OhmicPower(surface, edgeCurrents, sheetImpedance);
        double power = 0;
        for (int t = 0; t < surface.Triangles.Count; t++)
            power += 0.5 * sheetImpedance.Real * TriangleCurrentSquared(surface, edgeCurrents, t,
                bands.BandOf[t] >= 0 ? bands.Bands[bands.BandOf[t]].Across : null);
        double edgeResistance = edge!.EdgeImpedance(frequencyHz).Real;
        foreach (var band in bands.Bands)
        {
            Complex moment = Complex.Zero;
            foreach (var (n, an) in band.Moments) moment += edgeCurrents[n] * an;
            power += 0.5 * edgeResistance * band.Kappa * (moment.Real * moment.Real + moment.Imaginary * moment.Imaginary);
        }
        return power;
    }

    /// <summary>Rows of triangles beside a free edge whose current along it fixes the edge's
    /// amplitude: the solve's current in the edge triangle itself is several per cent off the edge
    /// profile (measured on a strip 8 cells across: 7 % low in the triangle on the edge, 34 % high in
    /// the one beside it), but what two rows carry together is within a few per cent.</summary>
    private const int BandRows = 2;

    /// <summary>A free-edge segment's band: the edge's unit tangent τ, the in-plane unit vector
    /// across it, M = ∫_band J·τ dA as coefficients per basis, and κ such that the edge current k·g(s)
    /// (s the distance from the edge's line, g the profile of <see cref="ProfileIntegrals"/>), its
    /// amplitude fitted to M = k·∫_band g dA, has ∫_band |J·τ|² dA over s &gt; Δ = k²∫_band g² dA = κM²:
    /// κ = ∫g²/(∫g)².</summary>
    internal sealed record EdgeBand(Vector3D Tangent, Vector3D Across, double Kappa, (int Basis, double Value)[] Moments);

    internal sealed record EdgeBandSet(IReadOnlyList<EdgeBand> Bands, int[] BandOf);

    /// <summary>The free-edge bands of a sheet, when the edge model applies at this frequency. A free
    /// edge is one no other triangle shares and no basis crosses (a rim on an image ground is
    /// joined to it). Every triangle within <see cref="BandRows"/> edge-triangle heights of a free
    /// edge segment, coplanar with it, joins the band of the nearest such segment.</summary>
    internal static EdgeBandSet EdgeBands(SurfaceStructure s, SheetEdge? edge, double frequencyHz)
    {
        int nt = s.Triangles.Count;
        var bandOf = Enumerable.Repeat(-1, nt).ToArray();
        if (edge is null || !edge.AppliesAt(frequencyHz)) return new EdgeBandSet(Array.Empty<EdgeBand>(), bandOf);
        var count = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in s.Triangles)
            foreach (var key in new[] { Key(a, b), Key(b, c), Key(c, a) })
                count[key] = count.TryGetValue(key, out int k) ? k + 1 : 1;
        foreach (var rwg in s.Edges) count[Key(rwg.V1, rwg.V2)] = 2;

        var normals = new Vector3D[nt];
        for (int t = 0; t < nt; t++)
        {
            var (a, b, c) = s.Triangles[t];
            var n = Vector3D.Cross(s.Vertices[b] - s.Vertices[a], s.Vertices[c] - s.Vertices[a]);
            normals[t] = n * (1 / n.Length);
        }
        var segments = new List<(Vector3D P0, Vector3D P1, Vector3D Normal, double Reach, int Triangle)>();
        for (int t = 0; t < nt; t++)
        {
            var (a, b, c) = s.Triangles[t];
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                if (count[Key(u, v)] != 1) continue;
                double length = (s.Vertices[v] - s.Vertices[u]).Length;
                segments.Add((s.Vertices[u], s.Vertices[v], normals[t], BandRows * 2 * s.TriangleAreas[t] / length, t));
            }
        }
        if (segments.Count == 0) return new EdgeBandSet(Array.Empty<EdgeBand>(), bandOf);

        var owner = Enumerable.Repeat(-1, nt).ToArray();
        var nearest = Enumerable.Repeat(double.PositiveInfinity, nt).ToArray();
        for (int i = 0; i < segments.Count; i++)
        {
            var (p0, p1, normal, reach, _) = segments[i];
            var axis = p1 - p0;
            double length = axis.Length;
            axis = axis * (1 / length);
            for (int t = 0; t < nt; t++)
            {
                var centroid = s.TriangleCentroids[t];
                var r = centroid - p0;
                double along = Vector3D.Dot(r, axis);
                var closest = p0 + axis * Math.Clamp(along, 0, length);
                double distance = (centroid - closest).Length;
                if (distance >= reach || distance >= nearest[t]) continue;
                if (Vector3D.Cross(normals[t], normal).Length > 1e-6 || Math.Abs(Vector3D.Dot(r, normal)) > 1e-6 * length) continue;
                nearest[t] = distance;
                owner[t] = i;
            }
        }

        double delta = edge.StoppingDistance;
        var bands = new List<EdgeBand>();
        for (int i = 0; i < segments.Count; i++)
        {
            var members = Enumerable.Range(0, nt).Where(t => owner[t] == i).ToList();
            if (members.Count == 0) continue;
            var (p0, p1, normal, _, home) = segments[i];
            var tangent = (p1 - p0) * (1 / (p1 - p0).Length);
            var across = Vector3D.Cross(normal, tangent);
            across = across * (1 / across.Length);
            if (Vector3D.Dot(s.TriangleCentroids[home] - p0, across) < 0) across = across * -1;
            double width = WidthAcross(segments.Select(g => (g.P0, g.P1)).ToList(), i, normal, tangent, across);
            double half = 0, inverse = 0;
            var moments = new Dictionary<int, double>();
            foreach (int t in members)
            {
                var (a, b, c) = s.Triangles[t];
                var (h, inv) = ProfileIntegrals(
                    Math.Abs(Vector3D.Dot(s.Vertices[a] - p0, across)),
                    Math.Abs(Vector3D.Dot(s.Vertices[b] - p0, across)),
                    Math.Abs(Vector3D.Dot(s.Vertices[c] - p0, across)),
                    s.TriangleAreas[t], delta, width);
                half += h;
                inverse += inv;
                var centroid = s.TriangleCentroids[t];
                double area = s.TriangleAreas[t];
                foreach (var (basis, sign, opposite) in s.TriangleSupports[t])
                {
                    var f = sign * s.Edges[basis].Length / (2 * area) * (centroid - s.Vertices[opposite]);
                    moments[basis] = (moments.TryGetValue(basis, out double m) ? m : 0) + area * Vector3D.Dot(f, tangent);
                }
                bandOf[t] = bands.Count;
            }
            bands.Add(new EdgeBand(tangent, across, inverse / (half * half),
                moments.Select(kv => (kv.Key, kv.Value)).ToArray()));
        }
        return new EdgeBandSet(bands, bandOf);

        static (int, int) Key(int p, int q) => p < q ? (p, q) : (q, p);
    }

    /// <summary>How far the sheet reaches across from free-edge segment <paramref name="i"/>: from
    /// its midpoint along the inward normal to the first other free edge in its plane; +∞ when the
    /// ray meets none.</summary>
    private static double WidthAcross(List<(Vector3D P0, Vector3D P1)> segments, int i,
        Vector3D normal, Vector3D tangent, Vector3D inward)
    {
        var (a0, a1) = segments[i];
        var middle = (a0 + a1) * 0.5;
        double scale = (a1 - a0).Length, width = double.PositiveInfinity;
        for (int j = 0; j < segments.Count; j++)
        {
            if (j == i) continue;
            var (b0, b1) = segments[j];
            if (Math.Abs(Vector3D.Dot(b0 - middle, normal)) > 1e-6 * scale
                || Math.Abs(Vector3D.Dot(b1 - middle, normal)) > 1e-6 * scale) continue;
            double x0 = Vector3D.Dot(b0 - middle, tangent), y0 = Vector3D.Dot(b0 - middle, inward);
            double x1 = Vector3D.Dot(b1 - middle, tangent), y1 = Vector3D.Dot(b1 - middle, inward);
            if (x0 * x1 > 0 || x0 == x1) continue;
            double y = y0 + (y1 - y0) * (-x0) / (x1 - x0);
            if (y > 1e-9 * scale) width = Math.Min(width, y);
        }
        return width;
    }

    /// <summary>∫_T g dA and ∫_T g² dA over Δ &lt; s &lt; D − Δ for the edge current's profile
    /// g(s) = 1/√(s(1 − s/D)) — a strip's two-edge current, D its width across, which is 1/√s on a
    /// sheet wide against the band — over a triangle whose vertices lie s₁, s₂, s₃ from the edge's
    /// line: its width at distance s rises linearly from the nearest vertex to the middle one, where
    /// it is 2A/(s₃ − s₁), and falls to the farthest, so each integral is a closed form on two linear
    /// pieces (a triangle with an edge on the line starts at full width). With s = D sin²θ,
    /// ∫(p + qs)/√(s(D − s)) ds = 2pθ + qD(θ − sinθ cosθ); and (p + qs)/(s(1 − s/D)) has the
    /// antiderivative p·ln s − (p + qD)·ln(D − s).</summary>
    internal static (double Half, double Inverse) ProfileIntegrals(double s1, double s2, double s3, double area,
        double delta, double width = double.PositiveInfinity)
    {
        Span<double> v = stackalloc double[] { s1, s2, s3 };
        v.Sort();
        double lo = v[0], mid = v[1], hi = v[2];
        bool strip = double.IsFinite(width);
        if (!(hi - lo > 0))
        {
            double s = Math.Max(lo, delta), g2 = strip ? 1 / (s * (1 - s / width)) : 1 / s;
            return (area * Math.Sqrt(g2), area * g2);
        }
        double peak = 2 * area / (hi - lo);
        double half = 0, inverse = 0;
        double Theta(double s) => Math.Asin(Math.Sqrt(Math.Clamp(s / width, 0, 1)));
        void Piece(double a, double b, double p, double q)   // width p + q·s on [a, b]
        {
            if (!(b > a)) return;
            if (!strip)
            {
                half += 2 * p * (Math.Sqrt(b) - Math.Sqrt(a)) + 2.0 / 3 * q * (b * Math.Sqrt(b) - a * Math.Sqrt(a));
                double from = Math.Max(a, delta);
                if (b > from) inverse += p * Math.Log(b / from) + q * (b - from);
                return;
            }
            double ta = Theta(a), tb = Theta(b);
            half += Math.Sqrt(width) * (2 * p * (tb - ta)
                + q * width * ((tb - Math.Sin(tb) * Math.Cos(tb)) - (ta - Math.Sin(ta) * Math.Cos(ta))));
            double lower = Math.Max(a, delta), upper = Math.Min(b, width - delta);
            if (upper > lower)
                inverse += p * Math.Log(upper / lower) - (p + q * width) * Math.Log((width - upper) / (width - lower));
        }
        if (mid > lo) Piece(lo, mid, -peak * lo / (mid - lo), peak / (mid - lo));
        if (hi > mid) Piece(mid, hi, peak * hi / (hi - mid), -peak / (hi - mid));
        return (half, inverse);
    }
}

/// <summary>
/// The free edges of sheet metal of finite thickness (FU-37). A zero-thickness sheet's current
/// along a free edge goes as 1/√x, whose ∫|J|² diverges — the sheet's heat rose with every
/// refinement of the mesh. Along each free edge the loss is that of the thick edge instead: the
/// 1/√x current integrated from the stopping distance Δ = t/(4π·e^π) (<see cref="SheetLoss.StoppingDistance"/>),
/// its amplitude fitted to the current the rows of triangles along the edge carry along it, on both faces
/// (the edge's own singular current is shared by the two faces, whichever face the sheet's
/// current otherwise rides — so with the both-face impedance ½(1 + j)/(σδ)). It holds while the
/// skin depth is under a third of the thickness; at lower frequencies the sheet's own loss stands.
/// </summary>
public sealed record SheetEdge(double ThicknessMeters, double ConductivitySiemensPerMeter)
{
    public double StoppingDistance => SheetLoss.StoppingDistance(ThicknessMeters);

    public bool AppliesAt(double frequencyHz) =>
        frequencyHz > 0 && 1 / Math.Sqrt(Math.PI * frequencyHz * RfConstants.Mu0 * ConductivitySiemensPerMeter) <= ThicknessMeters / 3;

    /// <summary>Both faces of a thick conductor: half of (1 + j)·√(πfµ₀/σ).</summary>
    public Complex EdgeImpedance(double frequencyHz) => 0.5 * SheetLoss.RoundWire(frequencyHz, ConductivitySiemensPerMeter);
}

public sealed partial class SurfaceMomSolver
{
    /// <summary>
    /// Impedance per square of the sheet metal against frequency (see <see cref="SheetLoss"/>);
    /// null is a perfect conductor. Used by the delta-gap solves, the multi-port solve and the
    /// probe-fed solve.
    /// </summary>
    public Func<double, Complex>? SheetImpedance { get; init; }

    /// <summary>The sheet metal's thickness and conductivity, for the loss of the current along its
    /// free edges (<see cref="Surface.SheetEdge"/>); null keeps the zero-thickness sheet's own
    /// loss there, which grows with every refinement of the mesh. Only with <see cref="SheetImpedance"/>.</summary>
    public SheetEdge? SheetEdge { get; init; }

    /// <summary>
    /// Surface impedance of round conductors (probe tubes, wires) against frequency [Ω per
    /// square]; null is a perfect conductor. A round conductor of radius a carries it over its
    /// circumference: Z_s/(2πa) per metre of its current (<see cref="SheetLoss.RoundWire"/>).
    /// </summary>
    public Func<double, Complex>? WireSurfaceImpedance { get; init; }

    /// <summary>Several delta-gap ports on a sheet in free space (or over its image plane).</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, double frequencyHz,
        IReadOnlyList<SurfacePort> ports)
    {
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
        double omega = 2 * Math.PI * frequencyHz;
        var z = AssembleImpedanceMatrix(surface, omega / RfConstants.SpeedOfLight, omega, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, frequencyHz, z);
    }

    /// <summary>Several delta-gap ports on a sheet on a grounded slab.</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, LayeredKernelTable kernel,
        IReadOnlyList<SurfacePort> ports)
    {
        foreach (var port in ports) ValidateLayeredSurface(surface, port);
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, 2 * Math.PI * kernel.FrequencyHz, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, kernel.FrequencyHz, z);
    }

    /// <summary>Several delta-gap ports on a sheet in a multi-layer stackup.</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, MultiLayerKernelTable kernel,
        IReadOnlyList<SurfacePort> ports)
    {
        foreach (var port in ports) ValidateLayeredSurface(surface, port);
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, 2 * Math.PI * kernel.FrequencyHz, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, kernel.FrequencyHz, z);
    }

    /// <summary>The coefficient with which each edge of a port is driven (rhs = c·V·l, port
    /// current Σ c·l·I): a finite gap's own coefficients, or for a delta gap +1 where the edge's
    /// T⁺→T⁻ crossing runs along the port direction (a grounded rim edge crosses into the
    /// plane) and −1 otherwise.</summary>
    private static double[] PortSigns(SurfaceStructure surface, SurfacePort port)
    {
        if (port.Coefficients is { } given)
        {
            if (given.Count != port.EdgeBases.Count)
                throw new ArgumentException(
                    $"The port has {port.EdgeBases.Count} edges but {given.Count} coefficients.", nameof(port));
            foreach (int e in port.EdgeBases)
                if (e < 0 || e >= surface.BasisCount)
                    throw new ArgumentOutOfRangeException(nameof(port), $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");
            return given.ToArray();
        }
        var signs = new double[port.EdgeBases.Count];
        for (int i = 0; i < signs.Length; i++)
        {
            int e = port.EdgeBases[i];
            if (e < 0 || e >= surface.BasisCount)
                throw new ArgumentOutOfRangeException(nameof(port), $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");
            var plusCentroid = surface.TriangleCentroids[surface.Edges[e].PlusTriangle];
            var crossing = surface.Edges[e].MinusTriangle >= 0
                ? surface.TriangleCentroids[surface.Edges[e].MinusTriangle] - plusCentroid
                : ThinWireMomSolver.Mirror(plusCentroid, surface.Ground!.SurfaceZ) - plusCentroid;
            signs[i] = Vector3D.Dot(crossing, port.Direction) >= 0 ? 1.0 : -1.0;
        }
        return signs;
    }

    private SurfaceMultiPortSolution SolveMultiPortAssembled(SurfaceStructure surface,
        IReadOnlyList<SurfacePort> ports, double frequencyHz, ComplexDenseMatrix z)
    {
        if (ports.Count == 0) throw new ArgumentException("At least one port is needed.", nameof(ports));
        var signs = ports.Select(p => PortSigns(surface, p)).ToArray();
        var used = new HashSet<int>();
        foreach (var port in ports)
            foreach (int e in port.EdgeBases)
                if (!used.Add(e))
                    throw new ArgumentException($"Mesh edge {e} belongs to two ports.", nameof(ports));

        if (SheetImpedance is { } sheet) SheetLoss.AddTo(z, surface, sheet(frequencyHz), SheetEdge, frequencyHz);
        var lu = ComplexLu.Factor(z, MaxDegreeOfParallelism);
        var y = new Complex[ports.Count, ports.Count];
        var currents = new Complex[ports.Count][];
        for (int q = 0; q < ports.Count; q++)
        {
            var rhs = new Complex[surface.BasisCount];
            for (int i = 0; i < ports[q].EdgeBases.Count; i++)
                rhs[ports[q].EdgeBases[i]] = signs[q][i] * surface.Edges[ports[q].EdgeBases[i]].Length;
            currents[q] = lu.Solve(rhs);
            for (int p = 0; p < ports.Count; p++)
            {
                Complex current = Complex.Zero;
                for (int i = 0; i < ports[p].EdgeBases.Count; i++)
                    current += signs[p][i] * surface.Edges[ports[p].EdgeBases[i]].Length * currents[q][ports[p].EdgeBases[i]];
                y[p, q] = current;
            }
        }
        return new SurfaceMultiPortSolution(frequencyHz, y, currents);
    }
}
