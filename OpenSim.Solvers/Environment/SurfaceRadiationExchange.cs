using System.Diagnostics;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// Grey diffuse radiation among the faces of each <see cref="SurfaceRadiation"/> set.
/// <para>
/// <b>View factors.</b> F_ij = (1/A_i)∫_{A_i} F_{dA→j} dA. The inner factor, from a point
/// to a polygon, is exact: (1/2π)·Σ_edges γ_k·n·(R_k × R_{k+1})/|R_k × R_{k+1}|, with R the
/// edge's ends seen from the point and γ the angle between them — the projected solid
/// angle. The polygon is triangle j clipped to the half-space in front of triangle i, and
/// a point behind j's plane sees j's back and nothing. The outer integral is the centroid
/// rule on m² equal sub-triangles of i, one rule for i's whole row, m from 1 to 8 as the
/// nearest triangle facing i comes within six of their sizes: inside a closed surface the
/// point factors add to 1 at every point of the rule, so an unobstructed closed row adds to
/// 1 to rounding. With obstruction on, each pair's factor is scaled by the fraction of
/// rays — from i's centroid, or four points of i for a near pair, to samples of the clipped
/// polygon — that no boundary triangle of the mesh interrupts: a partial shadow is sampled,
/// not integrated. Reciprocity is imposed by averaging A_i·F_ij and A_j·F_ji; what a
/// triangle's row leaves of 1 goes to the surroundings, a black body at the set's ambient.
/// </para>
/// <para>
/// <b>Exchange.</b> With E = σT⁴ the radiosities J = εE + (1 − ε)(F·J + F₀E₀) give each
/// triangle's net loss Q = A·ε·(E − F·J − F₀E₀), linear in E: Q = 𝒦·E − k₀E₀ with
/// 𝒦 = diag(Aε)·(I − F·(I − diag(1 − ε)F)⁻¹·diag(ε)). Equal temperatures exchange nothing, so
/// k₀ is 𝒦's row sum and Q_i = Σ_j S_ij·σ(T_i⁴ − T_j⁴) + S_i0·σ(T_i⁴ − T₀⁴) with
/// S_ij = −𝒦_ij ≥ 0 (symmetric by reciprocity) and S_i0 = k₀_i: a network of radiative
/// conductances, computed once — the temperatures only enter through T⁴.
/// </para>
/// <para>
/// <b>In the solve.</b> A triangle's temperature is its nodes' mean and its loss leaves
/// its nodes in thirds. Each pair's σ(T_i⁴ − T_j⁴) is linearized with the slope
/// 2σ(T_i³ + T_j³) on the difference (the mean of the two partial derivatives, so the
/// matrix stays symmetric and the conjugate gradient applies), the exchange with the
/// surroundings with its exact tangent 4σT_i³; both are exact at the point they are taken
/// at, so a converged iteration satisfies the T⁴ law itself.
/// </para>
/// </summary>
internal sealed class SurfaceRadiationExchange
{
    /// <summary>Most triangles one set may have: the exchange matrix is dense (n² memory,
    /// n³ to factor) and its node block enters the sparse system whole.</summary>
    public const int MaxTriangles = 2500;

    private const double Sigma = ConvectionCorrelations.StefanBoltzmann;

    internal sealed class Enclosure
    {
        public required string Name { get; init; }
        public required int[] Triangles { get; init; }      // boundary-triangle indices
        public required int[] Nodes { get; init; }          // mesh nodes, distinct
        public required int[][] Local { get; init; }        // per triangle, its 3 nodes as indices into Nodes
        public required double[] Areas { get; init; }
        public required double[] Emissivity { get; init; }
        public required double[] ViewFactors { get; init; } // n×n row-major, after reciprocity
        public required double[] Exchange { get; init; }    // S_ij, n×n row-major, zero diagonal
        public required double[] ToAmbient { get; init; }   // S_i0
        public required double Ambient { get; init; }
        public required int PatternStart { get; init; }

        /// <summary>Scratch of <see cref="Linearize"/>, kept between calls (dense, n² and
        /// n × nodes): the nonlinear loops call it every pass.</summary>
        internal double[]? TriangleBlock, HalfBlock;
    }

    private readonly FeMesh _mesh;
    private readonly List<Enclosure> _enclosures;
    private readonly List<(int Row, int Column)> _pattern;

    private SurfaceRadiationExchange(FeMesh mesh, List<Enclosure> enclosures, List<(int, int)> pattern)
    {
        _mesh = mesh;
        _enclosures = enclosures;
        _pattern = pattern;
    }

    public IReadOnlyList<Enclosure> Enclosures => _enclosures;

    /// <summary>Every (row, column) the exchange writes to: each set's node block, whole.</summary>
    public IReadOnlyList<(int Row, int Column)> Pattern => _pattern;

    /// <summary>The boundary triangles some set radiates from.</summary>
    public IReadOnlySet<int> RadiatingTriangles(FeMesh mesh) =>
        _enclosures.SelectMany(e => e.Triangles).ToHashSet();

    /// <summary>The boundary triangles of the input's sets, without computing anything —
    /// for the environment, whose own radiation these replace.</summary>
    public static IReadOnlySet<int> TrianglesOf(SolveInput input)
    {
        var set = new HashSet<int>();
        if (input.SurfaceRadiation is not { Count: > 0 } sets) return set;
        var faces = sets.SelectMany(s => s.FaceIds).ToHashSet();
        for (int t = 0; t < input.Mesh.BoundaryTriangles.Count; t++)
            if (faces.Contains(input.Mesh.BoundaryTriangles[t].FaceId)) set.Add(t);
        return set;
    }

    public static void Validate(SolveInput input)
    {
        if (input.SurfaceRadiation is not { Count: > 0 } sets) return;
        if (input.PrescribedFilm is not null || input.PrescribedFilmSchedule is not null)
            throw new InvalidOperationException(
                "Surface-to-surface radiation is not combined with a prescribed film (the conjugate CFD path); " +
                "put the radiation in the film's owner or leave it out.");
        var known = input.Mesh.BoundaryTriangles.Select(t => t.FaceId).ToHashSet();
        BoundaryAdjacency? adjacency = null;
        foreach (var set in sets)
        {
            if (set.FaceIds.Count == 0)
                throw new InvalidOperationException($"Surface radiation '{set.Name}': it names no faces.");
            foreach (int face in set.FaceIds)
                if (!known.Contains(face))
                    throw new InvalidOperationException($"Surface radiation '{set.Name}': face {face} is not on the mesh's boundary.");
            if (!(set.AmbientTemperature > 0))
                throw new InvalidOperationException($"Surface radiation '{set.Name}': the ambient temperature must be positive (kelvin).");
            if (set.Emissivity is { } e && !(e > 0 && e <= 1))
                throw new InvalidOperationException($"Surface radiation '{set.Name}': the emissivity must lie in (0, 1].");
            int count = input.Mesh.GetFaceTriangles(set.FaceIds).Count;
            if (count > MaxTriangles)
                throw new InvalidOperationException(
                    $"Surface radiation '{set.Name}' has {count} triangles; the dense exchange takes at most {MaxTriangles}. " +
                    "Mesh those faces more coarsely or split the set where the faces cannot see each other.");
            if (set.Emissivity is null)
            {
                // The materials of the bodies the set's faces belong to.
                adjacency ??= BoundaryAdjacency.Build(input.Mesh);
                var faces = set.FaceIds.ToHashSet();
                for (int t = 0; t < input.Mesh.BoundaryTriangles.Count; t++)
                {
                    if (!faces.Contains(input.Mesh.BoundaryTriangles[t].FaceId)) continue;
                    var material = MaterialOfRegion(input, adjacency.Regions[t]);
                    if (material.Emissivity is not { } own || !(own >= 0 && own <= 1))
                        throw new InvalidOperationException(
                            $"Surface radiation '{set.Name}' takes the materials' emissivity, and '{material.Name}' has none in [0, 1]. " +
                            "Give the material an emissivity or the set one of its own.");
                }
            }
        }
        var seen = new HashSet<int>();
        foreach (var set in sets)
            foreach (int face in set.FaceIds.Distinct())
                if (!seen.Add(face))
                    throw new InvalidOperationException($"Face {face} is in two surface-radiation sets; a face radiates in one.");
    }

    /// <summary>Computes the view factors and the exchange of every set; null without sets.</summary>
    public static SurfaceRadiationExchange? Build(SolveInput input, List<string> log, CancellationToken cancellationToken = default)
    {
        if (input.SurfaceRadiation is not { Count: > 0 } sets) return null;
        var mesh = input.Mesh;
        var adjacency = BoundaryAdjacency.Build(mesh);
        var enclosures = new List<Enclosure>();
        var pattern = new List<(int, int)>();
        foreach (var set in sets)
        {
            var watch = Stopwatch.StartNew();
            var faces = set.FaceIds.ToHashSet();
            var triangles = Enumerable.Range(0, mesh.BoundaryTriangles.Count)
                .Where(t => faces.Contains(mesh.BoundaryTriangles[t].FaceId)).ToArray();
            int n = triangles.Length;
            var areas = triangles.Select(t => adjacency.Areas[t]).ToArray();
            var emissivity = triangles.Select(t => set.Emissivity ?? MaterialOfRegion(input, adjacency.Regions[t]).Emissivity!.Value).ToArray();

            var (raw, rays) = ViewFactors(mesh, adjacency, triangles, set.Obstruction, cancellationToken);
            // Reciprocity: A_i·F_ij and A_j·F_ji come from separate quadratures; their mean is used both ways.
            var f = new double[n * n];
            double mismatch = 0, largest = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double ij = areas[i] * raw[i * n + j], ji = areas[j] * raw[j * n + i];
                    double mean = 0.5 * (ij + ji);
                    f[i * n + j] = mean / areas[i];
                    f[j * n + i] = mean / areas[j];
                    mismatch = Math.Max(mismatch, Math.Abs(ij - ji));
                    largest = Math.Max(largest, mean);
                }
            var toSurroundings = new double[n];
            double lowSum = double.PositiveInfinity, highSum = 0;
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++) sum += f[i * n + j];
                lowSum = Math.Min(lowSum, sum);
                highSum = Math.Max(highSum, sum);
                toSurroundings[i] = Math.Max(0, 1 - sum);
            }

            var (exchange, ambient) = ExchangeConductances(f, toSurroundings, areas, emissivity);

            var nodes = new List<int>();
            var index = new Dictionary<int, int>();
            var local = new int[n][];
            for (int i = 0; i < n; i++)
            {
                var bt = mesh.BoundaryTriangles[triangles[i]];
                local[i] = new[] { bt.A, bt.B, bt.C }.Select(node =>
                {
                    if (!index.TryGetValue(node, out int k)) { k = nodes.Count; index[node] = k; nodes.Add(node); }
                    return k;
                }).ToArray();
            }
            int start = pattern.Count;
            foreach (int a in nodes)
                foreach (int b in nodes)
                    pattern.Add((a, b));
            enclosures.Add(new Enclosure
            {
                Name = set.Name, Triangles = triangles, Nodes = nodes.ToArray(), Local = local, Areas = areas,
                Emissivity = emissivity, ViewFactors = f, Exchange = exchange, ToAmbient = ambient,
                Ambient = set.AmbientTemperature, PatternStart = start
            });
            log.Add($"Surface radiation '{set.Name}': {n} triangles on {faces.Count} face(s), ε {emissivity.Min():g3} … {emissivity.Max():g3}; " +
                    $"view factors in {watch.Elapsed.TotalSeconds:F1} s" + (set.Obstruction ? $" ({rays} rays for obstruction)" : " (no obstruction)") +
                    $"; each triangle sees {lowSum:F4} … {highSum:F4} of its view in the set, the rest the surroundings at " +
                    $"{set.AmbientTemperature:g4} K; reciprocity A·F matched to {(largest > 0 ? mismatch / largest : 0):g2} of the largest pair.");
            if (highSum > 1 + 1e-3)
                log.Add($"  WARNING: some triangle's view factors add to {highSum:F4}, above 1 — the faces are too coarse for " +
                        "how close they sit; refine them.");
        }
        return new SurfaceRadiationExchange(mesh, enclosures, pattern);
    }

    private static Material MaterialOfRegion(SolveInput input, int region) =>
        input.RegionMaterials?.GetValueOrDefault(region) ?? input.Material;

    /// <summary>S_ij and S_i0 from the view factors and emissivities (see the class notes).</summary>
    internal static (double[] Exchange, double[] ToAmbient) ExchangeConductances(double[] f, double[] toSurroundings,
        double[] areas, double[] emissivity)
    {
        int n = areas.Length;
        var k = new double[n * n];
        if (emissivity.All(e => e == 1))
        {
            // Black: 𝒦 = diag(A)(I − F).
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    k[i * n + j] = areas[i] * ((i == j ? 1 : 0) - f[i * n + j]);
        }
        else
        {
            // W = (I − diag(1 − ε)F)⁻¹·diag(ε), column by column; 𝒦 = diag(Aε)(I − F·W).
            var p = new double[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    p[i * n + j] = (i == j ? 1 : 0) - (1 - emissivity[i]) * f[i * n + j];
            var lu = DenseLu.FactorInPlace(p, n, new int[n]);
            var w = new double[n * n];
            var column = new double[n];
            var solution = new double[n];
            for (int c = 0; c < n; c++)
            {
                Array.Clear(column);
                column[c] = emissivity[c];
                lu.Solve(column, solution);
                for (int r = 0; r < n; r++) w[r * n + c] = solution[r];
            }
            var row = new double[n];
            for (int i = 0; i < n; i++)
            {
                Array.Clear(row);
                for (int m = 0; m < n; m++)
                {
                    double fim = f[i * n + m];
                    if (fim == 0) continue;
                    for (int j = 0; j < n; j++) row[j] += fim * w[m * n + j];
                }
                for (int j = 0; j < n; j++)
                    k[i * n + j] = areas[i] * emissivity[i] * ((i == j ? 1 : 0) - row[j]);
            }
        }
        var exchange = new double[n * n];
        var ambient = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++) sum += k[i * n + j];
            ambient[i] = Math.Max(0, sum);
            for (int j = 0; j < n; j++)
                if (j != i) exchange[i * n + j] = Math.Max(0, -0.5 * (k[i * n + j] + k[j * n + i]));
        }
        return (exchange, ambient);
    }

    /// <summary>
    /// The linearized exchange at a temperature: the node block's values (parallel to
    /// <see cref="Pattern"/>) and the loads it adds (into <paramref name="loads"/>, a
    /// full-length vector). With <paramref name="secant"/> the conductances are the secant
    /// σ(T_i² + T_j²)(T_i + T_j) toward the other surface's (and the surroundings') actual
    /// temperature and no offsets: the exchange as a linear network AT this state — the
    /// form a rise above zero takes (the loads then carry the surroundings at their temperature).
    /// </summary>
    public void Linearize(IReadOnlyList<double> temperature, double[] values, double[] loads, bool secant = false)
    {
        foreach (var e in _enclosures)
        {
            int n = e.Triangles.Length, nodes = e.Nodes.Length;
            var t = new double[n];
            for (int i = 0; i < n; i++)
                t[i] = (temperature[e.Nodes[e.Local[i][0]]] + temperature[e.Nodes[e.Local[i][1]]] + temperature[e.Nodes[e.Local[i][2]]]) / 3;
            var l = e.TriangleBlock ??= new double[n * n];
            Array.Clear(l);
            var offset = new double[n];
            for (int i = 0; i < n; i++)
            {
                double ti = t[i];
                for (int j = i + 1; j < n; j++)
                {
                    double s = e.Exchange[i * n + j];
                    if (s == 0) continue;
                    double tj = t[j];
                    double slope = secant
                        ? Sigma * (ti * ti + tj * tj) * (ti + tj)
                        : 2 * Sigma * (ti * ti * ti + tj * tj * tj);
                    double c = s * slope;
                    l[i * n + i] += c; l[j * n + j] += c;
                    l[i * n + j] -= c; l[j * n + i] -= c;
                    if (secant) continue;
                    double flow = s * Sigma * (ti * ti * ti * ti - tj * tj * tj * tj) - c * (ti - tj);
                    offset[i] += flow;
                    offset[j] -= flow;
                }
                double s0 = e.ToAmbient[i];
                if (s0 == 0) continue;
                double t0 = e.Ambient;
                if (secant)
                {
                    double c0 = s0 * Sigma * (ti * ti + t0 * t0) * (ti + t0);
                    l[i * n + i] += c0;
                    offset[i] -= c0 * t0;
                }
                else
                {
                    double c0 = 4 * s0 * Sigma * ti * ti * ti;
                    l[i * n + i] += c0;
                    offset[i] += s0 * Sigma * (ti * ti * ti * ti - t0 * t0 * t0 * t0) - c0 * ti;
                }
            }

            // Node block W·L·Wᵀ, W the thirds: first L·Wᵀ (triangle × node), then W·(that).
            var half = e.HalfBlock ??= new double[n * nodes];
            Array.Clear(half);
            for (int j = 0; j < n; j++)
                foreach (int b in e.Local[j])
                    for (int i = 0; i < n; i++)
                    {
                        double v = l[i * n + j];
                        if (v != 0) half[i * nodes + b] += v / 3;
                    }
            int start = e.PatternStart;
            Array.Clear(values, start, nodes * nodes);
            for (int i = 0; i < n; i++)
                foreach (int a in e.Local[i])
                    for (int b = 0; b < nodes; b++)
                        values[start + a * nodes + b] += half[i * nodes + b] / 3;
            for (int i = 0; i < n; i++)
                foreach (int a in e.Local[i])
                    loads[e.Nodes[a]] -= offset[i] / 3;
        }
    }

    /// <summary>Each set's net loss to the surroundings [W] and each face's net radiative
    /// loss [W] at a temperature, by the T⁴ law itself.</summary>
    public IReadOnlyList<(string Name, double ToSurroundings, IReadOnlyDictionary<int, double> ByFace)> Balance(IReadOnlyList<double> temperature)
    {
        var result = new List<(string, double, IReadOnlyDictionary<int, double>)>();
        foreach (var e in _enclosures)
        {
            int n = e.Triangles.Length;
            var e4 = new double[n];
            for (int i = 0; i < n; i++)
            {
                double ti = (temperature[e.Nodes[e.Local[i][0]]] + temperature[e.Nodes[e.Local[i][1]]] + temperature[e.Nodes[e.Local[i][2]]]) / 3;
                e4[i] = Sigma * ti * ti * ti * ti;
            }
            double ambient4 = Sigma * Math.Pow(e.Ambient, 4), surroundings = 0;
            var byFace = new SortedDictionary<int, double>();
            for (int i = 0; i < n; i++)
            {
                double q = e.ToAmbient[i] * (e4[i] - ambient4);
                surroundings += q;
                for (int j = 0; j < n; j++) q += e.Exchange[i * n + j] * (e4[i] - e4[j]);
                int face = _mesh.BoundaryTriangles[e.Triangles[i]].FaceId;
                byFace[face] = byFace.GetValueOrDefault(face) + q;
            }
            result.Add((e.Name, surroundings, byFace));
        }
        return result;
    }

    /// <summary>Log lines for a converged state.</summary>
    public IEnumerable<string> Describe(IReadOnlyList<double> temperature) =>
        Balance(temperature).Select(b =>
            $"Surface radiation '{b.Name}': {b.ToSurroundings:g4} W to the surroundings; net loss by face: " +
            string.Join(", ", b.ByFace.Select(kv => $"{kv.Key}: {kv.Value:g4} W")) + ".");

    // ---------------- View factors ----------------

    /// <summary>Raw F_ij (row-major, each row from quadrature over triangle i) and the rays cast.</summary>
    internal static (double[] F, long Rays) ViewFactors(FeMesh mesh, BoundaryAdjacency adjacency, int[] triangles,
        bool obstruction, CancellationToken cancellationToken)
    {
        int n = triangles.Length;
        var corners = triangles.Select(t =>
        {
            var bt = mesh.BoundaryTriangles[t];
            return (A: mesh.Nodes[bt.A], B: mesh.Nodes[bt.B], C: mesh.Nodes[bt.C]);
        }).ToArray();
        var normals = triangles.Select(t => adjacency.OutwardNormals[t]).ToArray();
        var centroids = corners.Select(c => (c.A + c.B + c.C) * (1.0 / 3)).ToArray();
        var sizes = corners.Select(c => Math.Max((c.B - c.A).Length, Math.Max((c.C - c.B).Length, (c.A - c.C).Length))).ToArray();

        var all = mesh.BoundaryTriangles.Select(bt => (bt.A, bt.B, bt.C)).ToList();
        var f = new double[n * n];
        long rays = 0;
        Parallel.For(0, n, new ParallelOptions { CancellationToken = cancellationToken },
            () => (Tree: obstruction ? new TriangleAabbTree(mesh.Nodes, all) : null, Candidates: new List<int>(), Rays: 0L),
            (i, _, state) =>
            {
                var (a, b, c) = corners[i];
                var ni = normals[i];
                Span<Vector3D> polygon = stackalloc Vector3D[4];
                var facing = new List<int>();
                double nearest = double.PositiveInfinity;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    // j must lie (partly) in front of i, and i in front of j.
                    var (ja, jb, jc) = corners[j];
                    var nj = normals[j];
                    if (Math.Max(Vector3D.Dot(ja - a, ni), Math.Max(Vector3D.Dot(jb - a, ni), Vector3D.Dot(jc - a, ni))) <= 1e-12 * sizes[i]) continue;
                    if (Math.Max(Vector3D.Dot(a - ja, nj), Math.Max(Vector3D.Dot(b - ja, nj), Vector3D.Dot(c - ja, nj))) <= 1e-12 * sizes[j]) continue;
                    facing.Add(j);
                    nearest = Math.Min(nearest, (centroids[j] - centroids[i]).Length / Math.Max(sizes[i], sizes[j]));
                }
                // One rule for the whole row, fine enough for the nearest facing triangle: the
                // point factors of a closed surface then add to 1 at every point of the rule.
                int m = Math.Clamp((int)Math.Ceiling(6 / Math.Max(nearest, 1e-30)), 1, 8);
                var points = SubCentroids(m).Select(uv => a + (b - a) * uv.U + (c - a) * uv.V).ToArray();
                double weight = 1.0 / points.Length;
                foreach (int j in facing)
                {
                    var (ja, jb, jc) = corners[j];
                    var nj = normals[j];
                    int count = Clip(ja, jb, jc, a, ni, polygon);
                    if (count < 3) continue;
                    double sum = 0;
                    foreach (var p in points)
                    {
                        if (Vector3D.Dot(p - ja, nj) <= 0) continue;   // behind j: its back
                        sum += weight * PointToPolygon(p, ni, polygon[..count]);
                    }
                    if (sum > 0 && state.Tree is not null)
                    {
                        // The share of the view no other surface interrupts: rays from i's
                        // centroid (or, for a near pair, from four points of i) to samples of j.
                        bool near = (centroids[j] - centroids[i]).Length < 3 * Math.Max(sizes[i], sizes[j]);
                        var origins = near ? SubCentroids(2).Select(uv => a + (b - a) * uv.U + (c - a) * uv.V) : new[] { centroids[i] };
                        double visible = 0;
                        int from = 0;
                        foreach (var origin in origins)
                        {
                            if (Vector3D.Dot(origin - ja, nj) <= 0) continue;
                            visible += Visible(state.Tree, state.Candidates, mesh, all, triangles[i], triangles[j],
                                origin + ni * (1e-7 * sizes[i]), polygon[..count], ref state.Rays);
                            from++;
                        }
                        sum *= from > 0 ? visible / from : 1;
                    }
                    f[i * n + j] = sum;
                }
                return state;
            },
            state => Interlocked.Add(ref rays, state.Rays));
        return (f, rays);
    }

    /// <summary>The projected solid angle of a polygon seen from a point with normal n, / π.</summary>
    internal static double PointToPolygon(Vector3D p, Vector3D n, ReadOnlySpan<Vector3D> polygon)
    {
        double sum = 0;
        for (int k = 0; k < polygon.Length; k++)
        {
            var r0 = polygon[k] - p;
            var r1 = polygon[(k + 1) % polygon.Length] - p;
            var cross = Vector3D.Cross(r0, r1);
            double length = cross.Length;
            if (!(length > 0)) continue;
            double angle = Math.Atan2(length, Vector3D.Dot(r0, r1));
            sum += angle * Vector3D.Dot(n, cross) / length;
        }
        return Math.Abs(sum) / (2 * Math.PI);
    }

    /// <summary>Triangle (a, b, c) clipped to the closed half-space in front of the plane
    /// through <paramref name="origin"/> with normal <paramref name="normal"/>; returns the
    /// polygon's vertex count (at most 4).</summary>
    private static int Clip(Vector3D a, Vector3D b, Vector3D c, Vector3D origin, Vector3D normal, Span<Vector3D> output)
    {
        Span<Vector3D> input = stackalloc Vector3D[3] { a, b, c };
        int count = 0;
        for (int k = 0; k < 3; k++)
        {
            var current = input[k];
            var next = input[(k + 1) % 3];
            double dc = Vector3D.Dot(current - origin, normal), dn = Vector3D.Dot(next - origin, normal);
            if (dc >= 0) output[count++] = current;
            if ((dc > 0 && dn < 0) || (dc < 0 && dn > 0))
                output[count++] = current + (next - current) * (dc / (dc - dn));
        }
        return count;
    }

    /// <summary>The fraction of rays from a point to samples of a polygon (its centroid and
    /// the midpoints from the centroid to each corner) that no other boundary triangle cuts.</summary>
    private static double Visible(TriangleAabbTree tree, List<int> candidates, FeMesh mesh,
        List<(int A, int B, int C)> all, int from, int to, Vector3D p, ReadOnlySpan<Vector3D> polygon, ref long rays)
    {
        var centroid = new Vector3D(0, 0, 0);
        foreach (var v in polygon) centroid += v;
        centroid *= 1.0 / polygon.Length;
        int clear = 0, total = 0;
        for (int s = -1; s < polygon.Length; s++)
        {
            var target = s < 0 ? centroid : (centroid + polygon[s]) * 0.5;
            var direction = target - p;
            total++;
            rays++;
            candidates.Clear();
            tree.CollectRayCandidates(p, direction, candidates);
            bool blocked = false;
            foreach (int t in candidates)
            {
                if (t == from || t == to) continue;
                var (ta, tb, tc) = all[t];
                if (Cuts(p, direction, mesh.Nodes[ta], mesh.Nodes[tb], mesh.Nodes[tc])) { blocked = true; break; }
            }
            if (!blocked) clear++;
        }
        return (double)clear / total;
    }

    /// <summary>Whether the segment p + s·d, s in (0, 1), crosses the triangle's interior
    /// (Möller–Trumbore; a segment grazing it in its plane does not count).</summary>
    private static bool Cuts(Vector3D p, Vector3D d, Vector3D a, Vector3D b, Vector3D c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var h = Vector3D.Cross(d, e2);
        double det = Vector3D.Dot(e1, h);
        if (Math.Abs(det) < 1e-300) return false;
        double inv = 1 / det;
        var s = p - a;
        double u = inv * Vector3D.Dot(s, h);
        if (u <= 0 || u >= 1) return false;
        var q = Vector3D.Cross(s, e1);
        double v = inv * Vector3D.Dot(d, q);
        if (v <= 0 || u + v >= 1) return false;
        double t = inv * Vector3D.Dot(e2, q);
        return t > 1e-9 && t < 1 - 1e-9;
    }

    /// <summary>The centroids of the m² equal sub-triangles, as (u, v) on the edges AB, AC.</summary>
    private static IEnumerable<(double U, double V)> SubCentroids(int m)
    {
        for (int i = 0; i < m; i++)
            for (int j = 0; i + j < m; j++)
            {
                yield return ((i + 1.0 / 3) / m, (j + 1.0 / 3) / m);
                if (i + j < m - 1) yield return ((i + 2.0 / 3) / m, (j + 2.0 / 3) / m);
            }
    }
}
