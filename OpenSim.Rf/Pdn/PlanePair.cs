using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// A place where current enters the plane pair: a via, or a patch of given outline. The
/// current is spread uniformly over the patch and the port voltage is the mean over it, so
/// a port's own (spreading) inductance depends on its size, as it does on the board.
/// </summary>
/// <param name="RadiusMeters">Radius of a round port (a via barrel). Ignored when
/// <see cref="Outline"/> is given.</param>
public sealed record PlanePort(string Name, Point2 Center, double RadiusMeters)
{
    /// <summary>The patch outline, when it is not a disc.</summary>
    public IReadOnlyList<Point2>? Outline { get; init; }

    /// <summary>A rectangular patch, the port of the analytic cavity model.</summary>
    public static PlanePort Rectangle(string name, Point2 center, double sizeX, double sizeY) =>
        new(name, center, 0.5 * Math.Sqrt(sizeX * sizeX + sizeY * sizeY))
        {
            Outline = new[]
            {
                new Point2(center.X - sizeX / 2, center.Y - sizeY / 2),
                new Point2(center.X + sizeX / 2, center.Y - sizeY / 2),
                new Point2(center.X + sizeX / 2, center.Y + sizeY / 2),
                new Point2(center.X - sizeX / 2, center.Y + sizeY / 2)
            }
        };
}

/// <summary>Two parallel copper planes and the dielectric between them.</summary>
public sealed record PlanePairSpec
{
    /// <summary>Where both planes have copper (the cavity), as polygons with holes.</summary>
    public required IReadOnlyList<Polygon2> Shape { get; init; }

    /// <summary>Dielectric thickness between the planes [m].</summary>
    public required double SeparationMeters { get; init; }

    public double RelativePermittivity { get; init; } = 4.4;
    public double LossTangent { get; init; } = 0.02;

    /// <summary>Djordjevic–Sarkar dielectric: εr and tan δ are the values at
    /// <see cref="DielectricReferenceHz"/> and ε′ rises toward low frequency as a flat loss
    /// tangent requires. Off: one complex ε at every frequency.</summary>
    public bool WidebandDielectric { get; init; } = true;

    public double DielectricReferenceHz { get; init; } = 1e9;

    public double UpperCopperThicknessMeters { get; init; } = 35e-6;
    public double LowerCopperThicknessMeters { get; init; } = 35e-6;
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    public required IReadOnlyList<PlanePort> Ports { get; init; }

    /// <summary>Target edge length of the mesh away from the ports [m]; 0 chooses it from
    /// <see cref="MaxFrequencyHz"/> and the plane size.</summary>
    public double MeshEdgeMeters { get; init; }

    /// <summary>The highest frequency the mesh has to carry (used when
    /// <see cref="MeshEdgeMeters"/> is 0).</summary>
    public double MaxFrequencyHz { get; init; } = 1e9;
}

/// <summary>A resonance of the bare plane pair with open edges.</summary>
/// <param name="PortCoupling">For each port, the mode's voltage at the port relative to the
/// mode's largest: near 0 the port sits on a node of the mode and neither excites nor sees it.</param>
public sealed record PlaneMode(double FrequencyHz, IReadOnlyList<double> PortCoupling);

/// <summary>
/// The impedance of a plane pair between its ports, by finite elements on the plane's real
/// outline.
///
/// <para>Between two planes a distance d apart, with d far below a wavelength, the field is
/// a voltage v(x, y) across the gap and a sheet current in the planes:</para>
/// <code>
///   ∇v = −Z_sq·K            Z_sq = jωµ₀d + Z_s,upper + Z_s,lower      [Ω per square]
///   ∇·K = −Y·v + J          Y = jωε/d                                 [S per area]
/// </code>
/// <para>so ∇·(∇v/Z_sq) − Y·v = −J, with no current leaving through the open edge (∂v/∂n = 0;
/// the fringing field there is not modelled). On quadratic triangles this is
/// (K/Z_sq + Y·M)·v = p·I with K the stiffness and M the mass matrix of the mesh, both real
/// and assembled once; a frequency only changes the two scalars. Z_s is the surface impedance
/// of a plane of finite thickness, (k/σ)·coth(k·t) with k = (1 + j)/δ, which is the DC sheet
/// resistance at low frequency and (1 + j)/(σδ) at high.</para>
///
/// <para>A port injects its current uniformly over its patch and reads the mean voltage over
/// it (the patch is meshed conformally, with rings of nodes grading out to the target edge
/// length), which makes Z reciprocal by construction and is the same port the analytic
/// cavity model uses.</para>
/// </summary>
public sealed class PlanePair
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Epsilon0 = 8.8541878128e-12;
    private const double SpeedOfLight = 299792458.0;

    /// <summary>Nodes per ring of the grading around a port, and of a round port's outline.</summary>
    private const int RingNodes = 12;

    private readonly CsrMatrix _stiffness, _mass;
    private readonly double[][] _port;          // ∫ N_i over the patch / patch area
    private readonly int[] _position;           // node → band order
    private readonly int _halfBandwidth;

    public PlanePairSpec Spec { get; }
    public PlanarMesh Mesh { get; }
    public double AreaSquareMeters { get; }
    public double MeshEdgeMeters { get; }
    /// <summary>Unknowns of the solve: the mesh's corners and one node per edge.</summary>
    public int NodeCount { get; }

    /// <summary>Dunavant's six-point rule, exact to degree 4 (the mass matrix of a quadratic
    /// element): barycentric coordinates and weight as a fraction of the area.</summary>
    private static readonly (double, double, double, double)[] Dunavant6 = BuildDunavant6();

    private static (double, double, double, double)[] BuildDunavant6()
    {
        const double a1 = 0.445948490915965, b1 = 0.108103018168070, w1 = 0.223381589678011;
        const double a2 = 0.091576213509771, b2 = 0.816847572980459, w2 = 0.109951743655322;
        return new[]
        {
            (b1, a1, a1, w1), (a1, b1, a1, w1), (a1, a1, b1, w1),
            (b2, a2, a2, w2), (a2, b2, a2, w2), (a2, a2, b2, w2)
        };
    }

    /// <summary>Half-bandwidth of the system in its solve order (the solve costs n·b²).</summary>
    public int HalfBandwidth => _halfBandwidth;

    /// <summary>ε₀εr·A/d at the reference frequency: what the pair is at low frequency.</summary>
    public double StaticCapacitanceFarads =>
        Epsilon0 * Spec.RelativePermittivity * AreaSquareMeters / Spec.SeparationMeters;

    public PlanePair(PlanePairSpec spec)
    {
        Spec = spec;
        if (!(spec.SeparationMeters > 0))
            throw new ArgumentException("The plane separation must be positive.");
        if (!(spec.RelativePermittivity >= 1))
            throw new ArgumentException("The relative permittivity must be at least 1.");
        if (spec.Shape.Count == 0)
            throw new ArgumentException("The plane pair has no area.");
        if (spec.Ports.Count == 0)
            throw new ArgumentException("The plane pair needs at least one port.");

        double minX = spec.Shape.Min(p => p.Outer.Min(q => q.X)), maxX = spec.Shape.Max(p => p.Outer.Max(q => q.X));
        double minY = spec.Shape.Min(p => p.Outer.Min(q => q.Y)), maxY = spec.Shape.Max(p => p.Outer.Max(q => q.Y));
        double size = Math.Max(maxX - minX, maxY - minY);

        double h = spec.MeshEdgeMeters;
        if (!(h > 0))
        {
            // Ten quadratic elements per wavelength in the dielectric at the top frequency,
            // and at least twenty-five across the plane; never so fine that the banded solve
            // stops being interactive.
            double wavelength = SpeedOfLight / (Math.Sqrt(spec.RelativePermittivity) * Math.Max(spec.MaxFrequencyHz, 1));
            h = Math.Min(wavelength / 10, size / 25);
            double area = spec.Shape.Sum(p => p.Area());
            h = Math.Max(h, Math.Sqrt(area / 4000));
        }
        MeshEdgeMeters = h;

        var planeIndex = new PolygonSetIndex(spec.Shape);
        var regions = new List<PlanarRegion>();
        var refinements = new List<MeshRefinement>();
        for (int k = 0; k < spec.Ports.Count; k++)
        {
            var port = spec.Ports[k];
            var outline = OutlineOf(port, h);
            foreach (var vertex in outline)
                if (!planeIndex.Contains(vertex))
                    throw new InvalidOperationException(
                        $"Port '{port.Name}' at ({port.Center.X * 1e3:g4}, {port.Center.Y * 1e3:g4}) mm is not " +
                        "wholly on the plane pair (it lies on or over an edge or a cut-out).");
            regions.Add(new PlanarRegion(k + 1, new[] { new Polygon2(outline) }));
            refinements.Add(Grading(port.Center, outline, h));
        }
        for (int a = 0; a < spec.Ports.Count; a++)
            for (int b = a + 1; b < spec.Ports.Count; b++)
            {
                double apart = (spec.Ports[a].Center - spec.Ports[b].Center).Length;
                if (apart < 1.05 * (Reach(regions[a]) + Reach(regions[b])))
                    throw new InvalidOperationException(
                        $"Ports '{spec.Ports[a].Name}' and '{spec.Ports[b].Name}' overlap " +
                        $"({apart * 1e3:g3} mm apart).");
            }
        regions.Add(new PlanarRegion(0, spec.Shape));

        Mesh = new PlanarMesher().Mesh(regions, h, cleanPolygons: true, refinements);

        // Quadratic elements: a node on every edge besides the corners.
        var edgeNode = new Dictionary<(int, int), int>();
        int n = Mesh.Points.Count;
        int Mid(int u, int v)
        {
            var key = u < v ? (u, v) : (v, u);
            if (!edgeNode.TryGetValue(key, out int id)) edgeNode[key] = id = n++;
            return id;
        }
        var elements = new int[Mesh.Triangles.Count][];
        for (int e = 0; e < elements.Length; e++)
        {
            var t = Mesh.Triangles[e];
            elements[e] = new[] { t.A, t.B, t.C, Mid(t.A, t.B), Mid(t.B, t.C), Mid(t.C, t.A) };
        }
        NodeCount = n;

        var stiffness = new SparseMatrixBuilder(n, n);
        var mass = new SparseMatrixBuilder(n, n);
        _port = new double[spec.Ports.Count][];
        var portArea = new double[spec.Ports.Count];
        for (int k = 0; k < _port.Length; k++) _port[k] = new double[n];
        double total = 0;
        Span<double> gx = stackalloc double[3];
        Span<double> gy = stackalloc double[3];
        Span<double> shape = stackalloc double[6];
        Span<double> dx = stackalloc double[6];
        Span<double> dy = stackalloc double[6];
        Span<double> ke = stackalloc double[36];
        Span<double> me = stackalloc double[36];
        Span<double> fe = stackalloc double[6];
        Span<double> l = stackalloc double[3];
        for (int e = 0; e < elements.Length; e++)
        {
            var t = Mesh.Triangles[e];
            var a = Mesh.Points[t.A]; var b = Mesh.Points[t.B]; var c = Mesh.Points[t.C];
            double twice = Point2.Cross(b - a, c - a);
            double area = Math.Abs(twice) / 2;
            if (area <= 0) continue;
            total += area;
            // Gradients of the barycentric coordinates.
            gx[0] = (b.Y - c.Y) / twice; gx[1] = (c.Y - a.Y) / twice; gx[2] = (a.Y - b.Y) / twice;
            gy[0] = (c.X - b.X) / twice; gy[1] = (a.X - c.X) / twice; gy[2] = (b.X - a.X) / twice;
            ke.Clear(); me.Clear(); fe.Clear();
            foreach (var (l0, l1, l2, weight) in Dunavant6)
            {
                l[0] = l0; l[1] = l1; l[2] = l2;
                for (int i = 0; i < 3; i++)
                {
                    shape[i] = l[i] * (2 * l[i] - 1);
                    dx[i] = (4 * l[i] - 1) * gx[i];
                    dy[i] = (4 * l[i] - 1) * gy[i];
                    int j = (i + 1) % 3;
                    shape[3 + i] = 4 * l[i] * l[j];
                    dx[3 + i] = 4 * (l[i] * gx[j] + l[j] * gx[i]);
                    dy[3 + i] = 4 * (l[i] * gy[j] + l[j] * gy[i]);
                }
                double w = weight * area;
                for (int i = 0; i < 6; i++)
                {
                    fe[i] += w * shape[i];
                    for (int j = 0; j < 6; j++)
                    {
                        ke[6 * i + j] += w * (dx[i] * dx[j] + dy[i] * dy[j]);
                        me[6 * i + j] += w * shape[i] * shape[j];
                    }
                }
            }
            var nodes = elements[e];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                {
                    stiffness.Add(nodes[i], nodes[j], ke[6 * i + j]);
                    mass.Add(nodes[i], nodes[j], me[6 * i + j]);
                }
            if (t.RegionId > 0)
            {
                portArea[t.RegionId - 1] += area;
                for (int i = 0; i < 6; i++) _port[t.RegionId - 1][nodes[i]] += fe[i];
            }
        }
        for (int k = 0; k < _port.Length; k++)
        {
            if (!(portArea[k] > 0))
                throw new InvalidOperationException($"Port '{spec.Ports[k].Name}' received no mesh.");
            for (int i = 0; i < n; i++) _port[k][i] /= portArea[k];
        }
        AreaSquareMeters = total;
        _stiffness = stiffness.Build();
        _mass = mass.Build();

        _position = BandedComplexLu.ReverseCuthillMcKee(_stiffness.RowPointers, _stiffness.ColumnIndices);
        int band = 0;
        for (int i = 0; i < n; i++)
            for (int k = _stiffness.RowPointers[i]; k < _stiffness.RowPointers[i + 1]; k++)
                band = Math.Max(band, Math.Abs(_position[i] - _position[_stiffness.ColumnIndices[k]]));
        _halfBandwidth = band;
    }

    private static double Reach(PlanarRegion region) =>
        region.Polygons[0].Outer.Max(p => (p - Centroid(region.Polygons[0].Outer)).Length);

    private static Point2 Centroid(IReadOnlyList<Point2> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring) { x += p.X; y += p.Y; }
        return new Point2(x / ring.Count, y / ring.Count);
    }

    /// <summary>The port patch as a polygon: the given outline with its edges cut to the
    /// local element size, or a regular polygon of the disc's area.</summary>
    private static List<Point2> OutlineOf(PlanePort port, double h)
    {
        var outline = new List<Point2>();
        if (port.Outline is { Count: >= 3 } given)
        {
            double perimeter = 0;
            for (int i = 0; i < given.Count; i++) perimeter += (given[(i + 1) % given.Count] - given[i]).Length;
            double step = Math.Min(h, perimeter / RingNodes);
            for (int i = 0; i < given.Count; i++)
            {
                var a = given[i]; var b = given[(i + 1) % given.Count];
                int pieces = Math.Max(1, (int)Math.Ceiling((b - a).Length / step - 1e-9));
                for (int s = 0; s < pieces; s++) outline.Add(a + (b - a) * ((double)s / pieces));
            }
            if (Polygon2.RingArea(outline) < 0) outline.Reverse();
            return outline;
        }
        if (!(port.RadiusMeters > 0))
            throw new ArgumentException($"Port '{port.Name}' needs a radius or an outline.");
        // Same area as the disc: π r² = ½ n R² sin(2π/n).
        double vertexRadius = port.RadiusMeters * Math.Sqrt(2 * Math.PI / (RingNodes * Math.Sin(2 * Math.PI / RingNodes)));
        for (int i = 0; i < RingNodes; i++)
        {
            double angle = 2 * Math.PI * i / RingNodes;
            outline.Add(new Point2(port.Center.X + vertexRadius * Math.Cos(angle), port.Center.Y + vertexRadius * Math.Sin(angle)));
        }
        return outline;
    }

    /// <summary>
    /// Rings of nodes around a port: inside the patch (which carries a uniform current
    /// density, so the voltage is curved there), then outward with each ring a constant
    /// factor larger than the last, until the spacing along a ring reaches the target edge
    /// length. The voltage near a port goes as ln r, and a geometric progression of rings
    /// resolves every decade of it alike.
    /// </summary>
    private static MeshRefinement Grading(Point2 center, IReadOnlyList<Point2> outline, double h)
    {
        double reach = outline.Max(p => (p - center).Length);
        double inner = outline.Min(p => (p - center).Length);
        var points = new List<(Point2, double)> { (center, 0.5 * inner) };
        void Ring(double radius, int count, double phase)
        {
            double spacing = 2 * Math.PI * radius / count;
            for (int i = 0; i < count; i++)
            {
                double angle = phase + 2 * Math.PI * i / count;
                points.Add((new Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), spacing));
            }
        }
        Ring(0.33 * inner, 6, 0);
        Ring(0.66 * inner, 12, Math.PI / 12);

        double growth = 1 + 2 * Math.PI / RingNodes;
        double radius = reach * growth;
        double last = reach;
        int ring = 0;
        while (2 * Math.PI * radius / RingNodes < 0.9 * h)
        {
            Ring(radius, RingNodes, (ring++ % 2) * Math.PI / RingNodes + Math.PI / RingNodes);
            last = radius;
            radius *= growth;
        }
        // With rings laid, the lattice starts most of an edge length beyond the last one;
        // with none (a patch as large as the elements) only the patch itself is kept clear.
        return new MeshRefinement(center, ring > 0 ? last + 0.7 * h : reach, points);
    }

    // ------------------------------------------------------------------
    // Frequency response
    // ------------------------------------------------------------------

    /// <summary>Series impedance of the pair per square [Ω] and shunt admittance per area [S/m²].</summary>
    public (Complex SeriesPerSquare, Complex ShuntPerArea) Constants(double frequencyHz)
    {
        if (!(frequencyHz > 0))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz),
                "The plane-pair impedance is defined for frequencies above zero (at DC the pair is an open circuit).");
        double omega = 2 * Math.PI * frequencyHz;
        Complex series = new Complex(0, omega * Mu0 * Spec.SeparationMeters)
            + SurfaceImpedance(omega, Spec.UpperCopperThicknessMeters)
            + SurfaceImpedance(omega, Spec.LowerCopperThicknessMeters);
        Complex permittivity = Spec.WidebandDielectric && Spec.LossTangent > 0
            ? Spec.RelativePermittivity * (1 + Spec.LossTangent * new WidebandDebye(Spec.DielectricReferenceHz).Shape(frequencyHz))
            : Spec.RelativePermittivity * new Complex(1, -Spec.LossTangent);
        Complex shunt = new Complex(0, omega) * Epsilon0 * permittivity / Spec.SeparationMeters;
        return (series, shunt);
    }

    /// <summary>(k/σ)·coth(k·t): the impedance per square of a plane carrying its current on
    /// one face, with the field decaying through the thickness.</summary>
    private Complex SurfaceImpedance(double omega, double thickness)
    {
        double sigma = Spec.ConductivitySiemensPerMeter;
        if (!(sigma > 0) || double.IsInfinity(sigma)) return Complex.Zero;
        if (!(thickness > 0)) return Complex.Zero;
        Complex k = Complex.Sqrt(new Complex(0, omega * Mu0 * sigma));
        Complex kt = k * thickness;
        // coth(x) = 1/x + x/3 − … for small x; the direct form for the rest.
        Complex coth = kt.Magnitude < 1e-3 ? 1 / kt + kt / 3 : 1 / Complex.Tanh(kt);
        return k / sigma * coth;
    }

    /// <summary>The port impedance matrix [Ω] at one frequency.</summary>
    public Complex[,] Impedance(double frequencyHz) => Impedance(frequencyHz, null);

    private Complex[,] Impedance(double frequencyHz, BandedComplexLu? workspace)
    {
        var (series, shunt) = Constants(frequencyHz);
        Complex a = 1 / series;
        int n = NodeCount, ports = _port.Length;
        var lu = workspace ?? new BandedComplexLu(n, _halfBandwidth);
        lu.Clear();
        for (int i = 0; i < n; i++)
            for (int k = _stiffness.RowPointers[i]; k < _stiffness.RowPointers[i + 1]; k++)
            {
                int j = _stiffness.ColumnIndices[k];
                // K and M share the mesh's sparsity pattern (built together, entry for entry).
                lu.Add(_position[i], _position[j], a * _stiffness.Values[k] + shunt * _mass.Values[k]);
            }
        lu.Factor();

        var z = new Complex[ports, ports];
        var x = new Complex[n];
        for (int q = 0; q < ports; q++)
        {
            for (int i = 0; i < n; i++) x[_position[i]] = _port[q][i];
            lu.Solve(x);
            for (int p = 0; p < ports; p++)
            {
                Complex sum = Complex.Zero;
                var weights = _port[p];
                for (int i = 0; i < n; i++)
                    if (weights[i] != 0) sum += weights[i] * x[_position[i]];
                z[p, q] = sum;
            }
        }
        // Symmetric by construction; remove the rounding asymmetry.
        for (int p = 0; p < ports; p++)
            for (int q = p + 1; q < ports; q++)
                z[p, q] = z[q, p] = 0.5 * (z[p, q] + z[q, p]);
        return z;
    }

    /// <summary>The port impedance matrix at each frequency, solved in parallel.</summary>
    public IReadOnlyList<Complex[,]> Sweep(IReadOnlyList<double> frequenciesHz,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new Complex[frequenciesHz.Count][,];
        long each = BandedComplexLu.StorageBytes(NodeCount, _halfBandwidth);
        int workers = (int)Math.Clamp((1L << 31) / Math.Max(each, 1), 1, Environment.ProcessorCount);
        int done = 0;
        Parallel.For(0, frequenciesHz.Count,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            () => new BandedComplexLu(NodeCount, _halfBandwidth),
            (i, _, lu) =>
            {
                result[i] = Impedance(frequenciesHz[i], lu);
                progress?.Report((double)Interlocked.Increment(ref done) / frequenciesHz.Count);
                return lu;
            },
            _ => { });
        return result;
    }

    // ------------------------------------------------------------------
    // Resonances
    // ------------------------------------------------------------------

    /// <summary>
    /// The lowest resonances of the bare pair with open edges and no loss: K·φ = k²·M·φ,
    /// f = k·c/(2π√εr). The uniform mode (k = 0, the pair as a capacitor) is left out.
    /// </summary>
    public IReadOnlyList<PlaneMode> Modes(int count, CancellationToken cancellationToken = default)
    {
        if (count < 1) return Array.Empty<PlaneMode>();
        int n = NodeCount;
        int wanted = Math.Min(count + 1, n);
        // Subspace iteration on (K + s·M)⁻¹·M with the band factorization the frequency
        // solve uses. K alone is singular (the uniform mode), hence the shift s, of the
        // size of the first eigenvalue; it is taken off again below.
        double shift = Math.Pow(Math.PI / Math.Sqrt(AreaSquareMeters), 2);
        var lu = new BandedComplexLu(n, _halfBandwidth);
        for (int i = 0; i < n; i++)
            for (int k = _stiffness.RowPointers[i]; k < _stiffness.RowPointers[i + 1]; k++)
                lu.Add(_position[i], _position[_stiffness.ColumnIndices[k]],
                    _stiffness.Values[k] + shift * _mass.Values[k]);
        lu.Factor();

        int block = Math.Min(n, 2 * wanted + 4);
        var x = new double[block][];
        uint seed = 12345;
        for (int j = 0; j < block; j++)
        {
            x[j] = new double[n];
            for (int i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;             // deterministic start vectors
                x[j][i] = j == 0 ? 1 : seed / (double)uint.MaxValue - 0.5;
            }
        }
        var work = new double[n];
        var rhs = new Complex[n];
        var values = new double[block];
        double[]? previous = null;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < block; j++)
            {
                _mass.Multiply(x[j], work);
                for (int i = 0; i < n; i++) rhs[_position[i]] = work[i];
                lu.Solve(rhs);
                for (int i = 0; i < n; i++) x[j][i] = rhs[_position[i]].Real;
            }
            // M-orthonormalise (twice, for the digits the first pass loses).
            for (int pass = 0; pass < 2; pass++)
                for (int j = 0; j < block; j++)
                {
                    for (int p = 0; p < j; p++)
                    {
                        _mass.Multiply(x[p], work);
                        double dot = 0;
                        for (int i = 0; i < n; i++) dot += x[j][i] * work[i];
                        for (int i = 0; i < n; i++) x[j][i] -= dot * x[p][i];
                    }
                    _mass.Multiply(x[j], work);
                    double norm = 0;
                    for (int i = 0; i < n; i++) norm += x[j][i] * work[i];
                    norm = Math.Sqrt(Math.Max(norm, double.Epsilon));
                    for (int i = 0; i < n; i++) x[j][i] /= norm;
                }
            // Rayleigh–Ritz: the projected stiffness, diagonalised.
            var projected = new double[block][];
            var kx = new double[block][];
            for (int j = 0; j < block; j++)
            {
                kx[j] = new double[n];
                _stiffness.Multiply(x[j], kx[j]);
            }
            for (int i = 0; i < block; i++)
            {
                projected[i] = new double[block];
                for (int j = 0; j < block; j++)
                {
                    double dot = 0;
                    for (int r = 0; r < n; r++) dot += x[i][r] * kx[j][r];
                    projected[i][j] = dot;
                }
            }
            for (int i = 0; i < block; i++)
                for (int j = i + 1; j < block; j++)
                    projected[i][j] = projected[j][i] = 0.5 * (projected[i][j] + projected[j][i]);
            var (ritz, vectors) = JacobiEigenSolver.Solve(projected);
            var rotated = new double[block][];
            for (int m = 0; m < block; m++)
            {
                rotated[m] = new double[n];
                for (int j = 0; j < block; j++)
                {
                    double c = vectors[m][j];
                    if (c == 0) continue;
                    for (int i = 0; i < n; i++) rotated[m][i] += c * x[j][i];
                }
            }
            x = rotated;
            values = ritz;
            if (previous is not null)
            {
                double change = 0;
                for (int m = 0; m < wanted; m++)
                    change = Math.Max(change, Math.Abs(values[m] - previous[m]) / (Math.Abs(values[m]) + shift));
                if (change < 1e-9) break;
            }
            previous = values;
        }

        var modes = new List<PlaneMode>();
        for (int m = 0; m < wanted; m++)
        {
            double k2 = values[m];
            if (k2 < 0.01 * shift) continue;                    // the uniform mode
            var shape = x[m];
            double peak = shape.Max(Math.Abs);
            var coupling = new double[_port.Length];
            for (int p = 0; p < _port.Length; p++)
            {
                double at = 0;
                for (int i = 0; i < n; i++) at += _port[p][i] * shape[i];
                coupling[p] = peak > 0 ? Math.Abs(at) / peak : 0;
            }
            modes.Add(new PlaneMode(
                Math.Sqrt(k2) * SpeedOfLight / (2 * Math.PI * Math.Sqrt(Spec.RelativePermittivity)), coupling));
        }
        return modes.Take(count).ToList();
    }
}
