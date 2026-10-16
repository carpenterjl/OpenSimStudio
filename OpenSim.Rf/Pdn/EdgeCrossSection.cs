namespace OpenSim.Rf.Pdn;

/// <summary>A copper layer of a board cross-section and the dielectric below it, top down.</summary>
/// <param name="GapBelowMeters">Dielectric to the next layer down; ignored on the last layer.</param>
public sealed record SectionLayer(double CopperThicknessMeters, double GapBelowMeters, double GapPermittivity);

/// <summary>Copper of one layer from <paramref name="FromX"/> to <paramref name="ToX"/>, part of
/// conductor <paramref name="Conductor"/>.</summary>
public sealed record SectionCopper(int Layer, double FromX, double ToX, int Conductor);

/// <summary>
/// A cross-section through a board at an open edge of its planes: x runs outward across the
/// edge, the board's dielectric stops at <see cref="BoardEdgeX"/> and air lies beyond it, above
/// and below. At <see cref="InteriorX"/> the section is cut by a mirror wall (no field crosses
/// it), so the section is half of a board twice as wide.
/// </summary>
public sealed record SectionGeometry
{
    public required IReadOnlyList<SectionLayer> Layers { get; init; }
    public required IReadOnlyList<SectionCopper> Copper { get; init; }
    public required int ConductorCount { get; init; }
    public required double BoardEdgeX { get; init; }
    public required double InteriorX { get; init; }

    /// <summary>Air around the board out to this distance from it [m]; 0 chooses ten board
    /// thicknesses or the interior width, whichever is more.</summary>
    public double AirMarginMeters { get; init; }

    /// <summary>Finest grid spacing as a fraction of the thinnest copper.</summary>
    public double FinestFraction { get; init; } = 0.25;

    /// <summary>Each grid step at most this much larger than its neighbour.</summary>
    public double Growth { get; init; } = 1.3;
}

/// <summary>
/// The electrostatics of a board cross-section: the Maxwell capacitance matrix per metre of
/// its conductors, with the board's dielectrics and with every dielectric replaced by air.
/// Biquadratic elements on a rectangular grid that follows every copper face, the board's faces
/// and its edge, graded geometrically toward each of them (the field is singular at copper
/// corners); conductors are held at fixed potentials, one solve per conductor, and the charge
/// on each is read off as the reaction of its fixed nodes.
/// </summary>
public static class EdgeCrossSection
{
    private const double Epsilon0 = 8.8541878128e-12;

    public sealed record Result(double[,] Capacitance, double[,] AirCapacitance, int Unknowns);

    /// <summary>A plate of the stack at an open edge: on section layer <paramref name="SectionLayer"/>,
    /// stack layer <paramref name="StackLayer"/>, reaching <paramref name="ReachMeters"/> past the
    /// edge (0: it ends there).</summary>
    public sealed record EdgePlate(int SectionLayer, int StackLayer, double ReachMeters);

    /// <summary>Other plane copper in the section, held at AC ground.</summary>
    public sealed record EdgeGround(int SectionLayer, double FromX, double ToX);

    /// <summary>
    /// The fringe of an open edge: the section through it solved, and for each pair of plates
    /// their mutual capacitance per metre less what their overlap holds inside (the parallel-plate
    /// part of the interior, for plates with nothing between them there). The interior is cut at
    /// −<paramref name="halfWidth"/> by a mirror wall, so the section is half of a pair of plates
    /// 2·halfWidth wide — which matters where the field wraps round the plates' outer faces.
    /// </summary>
    public static EdgeFringe FringeOf(IReadOnlyList<SectionLayer> layers, IReadOnlyList<EdgePlate> plates,
        IReadOnlyList<EdgeGround> grounds, double boardEdgeX, double halfWidth,
        double airMargin = 0, double finestFraction = 0.25, double growth = 1.3)
    {
        if (plates.Count < 2) throw new ArgumentException("An edge fringe needs at least two plates.");
        int ground = plates.Count;
        var copper = plates.Select((p, k) => new SectionCopper(p.SectionLayer, -halfWidth, p.ReachMeters, k))
            .Concat(grounds.Select(g => new SectionCopper(g.SectionLayer, Math.Max(g.FromX, -halfWidth), g.ToX, ground)))
            .ToList();
        var result = Solve(new SectionGeometry
        {
            Layers = layers, Copper = copper, ConductorCount = grounds.Count > 0 ? ground + 1 : ground,
            BoardEdgeX = boardEdgeX, InteriorX = -halfWidth, AirMarginMeters = airMargin,
            FinestFraction = finestFraction, Growth = growth
        });
        var pairs = new List<FringePair>();
        for (int a = 0; a < plates.Count; a++)
            for (int b = a + 1; b < plates.Count; b++)
            {
                int upper = Math.Min(plates[a].SectionLayer, plates[b].SectionLayer);
                int lower = Math.Max(plates[a].SectionLayer, plates[b].SectionLayer);
                // Neighbours inside: no other conductor on a layer between them there.
                bool adjacent = !copper.Any(c => c.Conductor != a && c.Conductor != b
                                                 && c.Layer > upper && c.Layer < lower && c.FromX < 0);
                double inside = adjacent ? InteriorPerWidth(layers, upper, lower, air: false) * halfWidth : 0;
                double insideAir = adjacent ? InteriorPerWidth(layers, upper, lower, air: true) * halfWidth : 0;
                pairs.Add(new FringePair(plates[a].StackLayer, plates[b].StackLayer,
                    -result.Capacitance[a, b] - inside, -result.AirCapacitance[a, b] - insideAir));
            }
        return new EdgeFringe(pairs);
    }

    /// <summary>The parallel-plate capacitance per unit area between two copper layers of the
    /// section, with the section's own filling of the copper layers between them: resin of the
    /// mean of the neighbouring dielectrics (or air throughout).</summary>
    public static double InteriorPerWidth(IReadOnlyList<SectionLayer> layers, int upper, int lower, bool air)
    {
        double overEps = 0;
        for (int j = upper; j < lower; j++)
        {
            overEps += layers[j].GapBelowMeters / (air ? 1 : layers[j].GapPermittivity);
            if (j > upper)
                overEps += layers[j].CopperThicknessMeters / (air ? 1 : 0.5 * (layers[j - 1].GapPermittivity + layers[j].GapPermittivity));
        }
        return Epsilon0 / overEps;
    }

    public static Result Solve(SectionGeometry g)
    {
        int layers = g.Layers.Count;
        if (layers < 1) throw new ArgumentException("The section has no layers.");
        if (!(g.BoardEdgeX > g.InteriorX)) throw new ArgumentException("The board edge must lie beyond the interior wall.");

        // z of each copper layer's top and bottom face, top down from z = 0.
        var top = new double[layers];
        var bottom = new double[layers];
        double z = 0;
        for (int j = 0; j < layers; j++)
        {
            top[j] = z;
            z -= g.Layers[j].CopperThicknessMeters;
            bottom[j] = z;
            if (j + 1 < layers) z -= g.Layers[j].GapBelowMeters;
        }
        double boardTop = top[0], boardBottom = bottom[layers - 1];
        double thickness = boardTop - boardBottom;
        double width = g.BoardEdgeX - g.InteriorX;
        double margin = g.AirMarginMeters > 0 ? g.AirMarginMeters : Math.Max(10 * thickness, width);
        double thinnest = g.Layers.Min(l => l.CopperThicknessMeters);
        double finest = g.FinestFraction * thinnest;

        // Grid lines: every face, graded toward the faces and corners that matter.
        var xMarks = new SortedSet<double> { g.InteriorX, g.BoardEdgeX, g.BoardEdgeX + margin };
        foreach (var c in g.Copper)
        {
            xMarks.Add(Math.Max(c.FromX, g.InteriorX));
            xMarks.Add(c.ToX);
        }
        var zMarks = new SortedSet<double> { boardBottom - margin, boardTop + margin };
        for (int j = 0; j < layers; j++) { zMarks.Add(top[j]); zMarks.Add(bottom[j]); }
        double coarse = Math.Max(thickness, width / 8);
        var x = Graded(xMarks.ToList(), finest, coarse, g.Growth, fineAt: xMarks.Where(v => v > g.InteriorX && v < g.BoardEdgeX + margin).ToList());
        var zs = Graded(zMarks.ToList(), finest, coarse, g.Growth, fineAt: zMarks.Where(v => v >= boardBottom && v <= boardTop).ToList());

        int nx = x.Count - 1, nz = zs.Count - 1;
        int nI = 2 * nx + 1, nK = 2 * nz + 1;
        double NodeX(int i) => i % 2 == 0 ? x[i / 2] : 0.5 * (x[i / 2] + x[i / 2 + 1]);
        double NodeZ(int k) => k % 2 == 0 ? zs[k / 2] : 0.5 * (zs[k / 2] + zs[k / 2 + 1]);

        // Which conductor (if any) holds each node.
        var conductor = new int[nI * nK];
        Array.Fill(conductor, -1);
        foreach (var c in g.Copper)
        {
            double x0 = Math.Max(c.FromX, g.InteriorX), x1 = c.ToX;
            double z0 = bottom[c.Layer], z1 = top[c.Layer];
            for (int i = 0; i < nI; i++)
            {
                double px = NodeX(i);
                if (px < x0 - 1e-15 || px > x1 + 1e-15) continue;
                for (int k = 0; k < nK; k++)
                {
                    double pz = NodeZ(k);
                    if (pz < z0 - 1e-15 || pz > z1 + 1e-15) continue;
                    conductor[i * nK + k] = c.Conductor;
                }
            }
        }

        // Permittivity of each cell (its centre's material); NaN inside copper.
        double Material(double cx, double cz, bool air)
        {
            foreach (var c in g.Copper)
                if (cx > Math.Max(c.FromX, g.InteriorX) && cx < c.ToX && cz > bottom[c.Layer] && cz < top[c.Layer])
                    return double.NaN;
            if (air || cx > g.BoardEdgeX || cz > boardTop || cz < boardBottom) return 1;
            for (int j = 0; j < layers; j++)
            {
                if (cz <= top[j] && cz >= bottom[j])
                {
                    // A copper layer's thickness without copper: air on an outer layer, resin
                    // (the mean of the dielectrics either side) inside the board.
                    if (j == 0 || j == layers - 1) return 1;
                    return 0.5 * (g.Layers[j - 1].GapPermittivity + g.Layers[j].GapPermittivity);
                }
                if (j + 1 < layers && cz < bottom[j] && cz > top[j + 1]) return g.Layers[j].GapPermittivity;
            }
            return 1;
        }

        // Unknowns: the free nodes, numbered across the shorter direction of the grid first (the
        // band is then that direction's node count).
        var index = new int[nI * nK];
        int unknowns = 0;
        bool byColumns = nK <= nI;
        if (byColumns)
            for (int n = 0; n < index.Length; n++) index[n] = conductor[n] < 0 ? unknowns++ : -1;
        else
            for (int k = 0; k < nK; k++)
                for (int i = 0; i < nI; i++)
                    index[i * nK + k] = conductor[i * nK + k] < 0 ? unknowns++ : -1;
        int band = 0;
        {
            // The widest reach of one element in the chosen numbering.
            for (int a = 0; a < nx; a++)
                for (int b = 0; b < nz; b++)
                {
                    int lo = int.MaxValue, hi = -1;
                    for (int p = 0; p < 9; p++)
                    {
                        int u = index[(2 * a + p / 3) * nK + 2 * b + p % 3];
                        if (u < 0) continue;
                        lo = Math.Min(lo, u); hi = Math.Max(hi, u);
                    }
                    if (hi >= 0) band = Math.Max(band, hi - lo);
                }
        }

        var capacitance = new double[g.ConductorCount, g.ConductorCount];
        var air = new double[g.ConductorCount, g.ConductorCount];
        foreach (bool inAir in new[] { false, true })
        {
            var target = inAir ? air : capacitance;
            var cells = new double[nx, nz];
            for (int a = 0; a < nx; a++)
                for (int b = 0; b < nz; b++)
                    cells[a, b] = Material(0.5 * (x[a] + x[a + 1]), 0.5 * (zs[b] + zs[b + 1]), inAir);

            var matrix = new BandedCholesky(unknowns, band);
            var element = new double[81];
            void Stiffness(int a, int b)
            {
                double hx = x[a + 1] - x[a], hz = zs[b + 1] - zs[b];
                double eps = cells[a, b];
                for (int p = 0; p < 9; p++)
                    for (int q = 0; q < 9; q++)
                        element[9 * p + q] = eps * Epsilon0 *
                            (K1[p / 3, q / 3] / hx * M1[p % 3, q % 3] * hz + M1[p / 3, q / 3] * hx * K1[p % 3, q % 3] / hz);
            }
            int Node(int a, int b, int p) => (2 * a + p / 3) * nK + 2 * b + p % 3;

            for (int a = 0; a < nx; a++)
                for (int b = 0; b < nz; b++)
                {
                    if (double.IsNaN(cells[a, b])) continue;
                    Stiffness(a, b);
                    for (int p = 0; p < 9; p++)
                    {
                        int row = index[Node(a, b, p)];
                        if (row < 0) continue;
                        for (int q = 0; q < 9; q++)
                        {
                            int column = index[Node(a, b, q)];
                            if (column >= 0 && column <= row) matrix.Add(row, column, element[9 * p + q]);
                        }
                    }
                }
            matrix.Factor();

            for (int driven = 0; driven < g.ConductorCount; driven++)
            {
                // Right-hand side from the driven conductor's fixed nodes.
                var rhs = new double[unknowns];
                for (int a = 0; a < nx; a++)
                    for (int b = 0; b < nz; b++)
                    {
                        if (double.IsNaN(cells[a, b])) continue;
                        bool touches = false;
                        for (int p = 0; p < 9 && !touches; p++) touches = conductor[Node(a, b, p)] == driven;
                        if (!touches) continue;
                        Stiffness(a, b);
                        for (int p = 0; p < 9; p++)
                        {
                            int row = index[Node(a, b, p)];
                            if (row < 0) continue;
                            for (int q = 0; q < 9; q++)
                                if (conductor[Node(a, b, q)] == driven) rhs[row] -= element[9 * p + q];
                        }
                    }
                matrix.Solve(rhs);

                // Charge on each conductor: the reaction at its fixed nodes.
                double Value(int node) => conductor[node] >= 0 ? (conductor[node] == driven ? 1 : 0) : rhs[index[node]];
                for (int a = 0; a < nx; a++)
                    for (int b = 0; b < nz; b++)
                    {
                        if (double.IsNaN(cells[a, b])) continue;
                        bool fixedNode = false;
                        for (int p = 0; p < 9 && !fixedNode; p++) fixedNode = conductor[Node(a, b, p)] >= 0;
                        if (!fixedNode) continue;
                        Stiffness(a, b);
                        for (int p = 0; p < 9; p++)
                        {
                            int owner = conductor[Node(a, b, p)];
                            if (owner < 0) continue;
                            double reaction = 0;
                            for (int q = 0; q < 9; q++) reaction += element[9 * p + q] * Value(Node(a, b, q));
                            target[driven, owner] += reaction;
                        }
                    }
            }
            for (int i = 0; i < g.ConductorCount; i++)
                for (int j = i + 1; j < g.ConductorCount; j++)
                    target[i, j] = target[j, i] = 0.5 * (target[i, j] + target[j, i]);
        }
        return new Result(capacitance, air, unknowns);
    }

    /// <summary>1D quadratic element: stiffness ×h and mass ÷h, nodes (start, middle, end).</summary>
    private static readonly double[,] K1 = { { 7.0 / 3, -8.0 / 3, 1.0 / 3 }, { -8.0 / 3, 16.0 / 3, -8.0 / 3 }, { 1.0 / 3, -8.0 / 3, 7.0 / 3 } };
    private static readonly double[,] M1 = { { 4.0 / 30, 2.0 / 30, -1.0 / 30 }, { 2.0 / 30, 16.0 / 30, 2.0 / 30 }, { -1.0 / 30, 2.0 / 30, 4.0 / 30 } };

    /// <summary>
    /// Grid lines through every mark. Between two marks the step follows h(t) = min(coarsest,
    /// s_a + (g − 1)·(t − a), s_b + (g − 1)·(b − t)) — each step g times the last moving away
    /// from a mark — with s = <paramref name="finest"/> at the marks in <paramref name="fineAt"/>
    /// and <paramref name="coarsest"/> at the others; the steps are then stretched to fit.
    /// </summary>
    private static List<double> Graded(List<double> marks, double finest, double coarsest, double growth, List<double> fineAt)
    {
        bool Fine(double v) => fineAt.Any(f => Math.Abs(f - v) <= 1e-12 * Math.Max(1e-3, Math.Abs(v)));
        var lines = new List<double> { marks[0] };
        for (int s = 0; s + 1 < marks.Count; s++)
        {
            double a = marks[s], b = marks[s + 1], length = b - a;
            if (!(length > 0)) continue;
            double sa = Fine(a) ? finest : coarsest, sb = Fine(b) ? finest : coarsest;
            double Step(double t) => Math.Min(coarsest, Math.Min(sa + (growth - 1) * (t - a), sb + (growth - 1) * (b - t)));
            var steps = new List<double>();
            double position = a;
            while (position < b)
            {
                double h = Math.Max(Step(position), 1e-3 * finest);
                steps.Add(h);
                position += h;
            }
            // The last step overshoots b: drop it if it is mostly past b, then stretch.
            double total = steps.Sum();
            if (steps.Count > 1 && total - b + a > 0.5 * steps[^1]) { total -= steps[^1]; steps.RemoveAt(steps.Count - 1); }
            double scale = length / total, at = a;
            for (int i = 0; i + 1 < steps.Count; i++) { at += steps[i] * scale; lines.Add(at); }
            lines.Add(b);
        }
        return lines;
    }

    /// <summary>Cholesky factorization of a symmetric positive definite band matrix (lower band
    /// stored by rows).</summary>
    private sealed class BandedCholesky
    {
        private readonly int _n, _b;
        private readonly double[] _l;

        public BandedCholesky(int n, int halfBandwidth)
        {
            _n = n;
            _b = halfBandwidth;
            _l = new double[(long)n * (halfBandwidth + 1)];
        }

        private int At(int row, int column) => row * (_b + 1) + (column - row + _b);

        /// <summary>Adds to entry (row, column), column ≤ row.</summary>
        public void Add(int row, int column, double value)
        {
            if (row - column > _b) throw new InvalidOperationException("Entry outside the band.");
            _l[At(row, column)] += value;
        }

        public void Factor()
        {
            for (int i = 0; i < _n; i++)
            {
                int first = Math.Max(0, i - _b);
                for (int j = first; j <= i; j++)
                {
                    double sum = _l[At(i, j)];
                    int start = Math.Max(first, Math.Max(0, j - _b));
                    int ai = At(i, start), aj = At(j, start);
                    for (int k = start; k < j; k++, ai++, aj++) sum -= _l[ai] * _l[aj];
                    if (j == i)
                    {
                        if (!(sum > 0))
                            throw new InvalidOperationException("The cross-section system is not positive definite.");
                        _l[At(i, i)] = Math.Sqrt(sum);
                    }
                    else _l[At(i, j)] = sum / _l[At(j, j)];
                }
            }
        }

        public void Solve(double[] x)
        {
            for (int i = 0; i < _n; i++)
            {
                double sum = x[i];
                int first = Math.Max(0, i - _b);
                int at = At(i, first);
                for (int k = first; k < i; k++, at++) sum -= _l[at] * x[k];
                x[i] = sum / _l[At(i, i)];
            }
            for (int i = _n - 1; i >= 0; i--)
            {
                double sum = x[i] / _l[At(i, i)];
                x[i] = sum;
                // Subtract column i of Lᵀ from the rows above: x[k] for k < i gets −L[i,k]·x[i].
                int first = Math.Max(0, i - _b);
                int at = At(i, first);
                for (int k = first; k < i; k++, at++) x[k] -= _l[at] * sum;
            }
        }
    }
}
