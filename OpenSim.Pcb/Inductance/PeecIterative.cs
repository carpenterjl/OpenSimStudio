using System.Collections.Concurrent;
using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>
/// The compressed iterative solve of a <see cref="PeecModel"/> past the dense limit — the
/// FastHenry formulation: unknowns are MESH currents (every Kirchhoff current law holds by
/// construction, so the saddle structure of the nodal system is gone and what remains,
/// M·(R + jωL)·Mᵀ, is complex symmetric with positive-definite real and imaginary parts),
/// the inductance matrix is never formed — far cluster pairs are low-rank factors found by
/// adaptive cross approximation on the same kernel the dense solve uses, near pairs stay
/// exact — and the mesh system is solved by preconditioned conjugate iterations (COCG, the
/// unconjugated form that pairs with transpose symmetry).
///
/// <para>Meshes: within a bundle of filaments sharing two nodes, each filament against the
/// first (a two-branch loop); between bundles, every co-tree bundle of a breadth-first
/// spanning forest closes a loop through the tree. A port's unit current flows along the
/// tree path between its terminals, and its voltage is read along the same path.</para>
///
/// <para>The preconditioner is the mesh system built from the resistances and the NEAR part
/// of the inductance (the pairs the compression keeps exact), factored by
/// <see cref="SparseComplexLdl"/>; where that would fill in too much it falls back to the
/// mesh diagonal. Accuracy is set by the compression tolerance and the iteration tolerance,
/// about six to eight digits against the dense solve.</para>
/// </summary>
internal static class PeecIterativeSolver
{
    private const double CocgTolerance = 1e-10;

    public static IReadOnlyList<PeecPoint> Solve(PeecModel model, PeecModel.Connectivity net,
        IReadOnlyList<double> frequenciesHz, bool keepCurrents, CancellationToken cancellationToken)
    {
        var filaments = model.Filaments;
        int nb = filaments.Count;
        var ends = net.Ends;

        // ---- Bundles: filaments sharing one pair of circuit nodes.
        var groupOf = new Dictionary<(int, int), int>();
        var groupFilaments = new List<List<int>>();
        var groupEnds = new List<(int A, int B)>();
        var selfLoops = new List<int>();
        for (int f = 0; f < nb; f++)
        {
            var (from, to) = ends[f];
            if (from == to) { selfLoops.Add(f); continue; }
            var key = from < to ? (from, to) : (to, from);
            if (!groupOf.TryGetValue(key, out int g))
            {
                groupOf[key] = g = groupFilaments.Count;
                groupFilaments.Add(new List<int>());
                groupEnds.Add(key);
            }
            groupFilaments[g].Add(f);
        }
        int groups = groupFilaments.Count;

        // ---- Breadth-first spanning forest of the bundle graph, rooted near each piece's middle.
        int nn = net.NodeCount;
        var adjacency = new List<(int Group, int Other)>[nn];
        for (int k = 0; k < nn; k++) adjacency[k] = new List<(int, int)>();
        for (int g = 0; g < groups; g++)
        {
            adjacency[groupEnds[g].A].Add((g, groupEnds[g].B));
            adjacency[groupEnds[g].B].Add((g, groupEnds[g].A));
        }
        var position = new Vector3D[nn];
        var hasPosition = new bool[nn];
        for (int f = 0; f < nb; f++)
        {
            if (!hasPosition[ends[f].From]) { position[ends[f].From] = filaments[f].Start; hasPosition[ends[f].From] = true; }
            if (!hasPosition[ends[f].To]) { position[ends[f].To] = filaments[f].End; hasPosition[ends[f].To] = true; }
        }
        var parentNode = new int[nn];
        var parentGroup = new int[nn];
        var depth = new int[nn];
        var visited = new bool[nn];
        var treeGroup = new bool[groups];
        var pieces = PieceLists(nn, adjacency);
        foreach (var piece in pieces)
        {
            // The root nearest the piece's centroid keeps the tree paths short.
            var centroid = Vector3D.Zero;
            foreach (int v in piece) centroid += position[v];
            centroid = (1.0 / piece.Count) * centroid;
            int root = piece.MinBy(v => (position[v] - centroid).Length);
            var queue = new Queue<int>();
            queue.Enqueue(root);
            visited[root] = true;
            parentNode[root] = -1;
            parentGroup[root] = -1;
            depth[root] = 0;
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (var (g, other) in adjacency[v])
                {
                    if (visited[other]) continue;
                    visited[other] = true;
                    parentNode[other] = v;
                    parentGroup[other] = g;
                    depth[other] = depth[v] + 1;
                    treeGroup[g] = true;
                    queue.Enqueue(other);
                }
            }
        }

        // ---- Meshes. The sign of an entry maps the mesh current onto the filament's own
        // From → To direction.
        var meshFilament = new List<int[]>();
        var meshSign = new List<sbyte[]>();
        var meshAnchor = new List<int>();           // the defining filament, for ordering
        void AddMesh(List<(int Filament, sbyte Sign)> entries, int anchor)
        {
            meshFilament.Add(entries.Select(e => e.Filament).ToArray());
            meshSign.Add(entries.Select(e => e.Sign).ToArray());
            meshAnchor.Add(anchor);
        }
        sbyte Direction(int filament, int fromCircuit) => (sbyte)(ends[filament].From == fromCircuit ? 1 : -1);
        // Walks x → y over the tree edge between them (y is x's parent or the reverse).
        void AddEdge(int x, int y, int group, List<(int, sbyte)> into)
        {
            int representative = groupFilaments[group][0];
            into.Add((representative, Direction(representative, x)));
        }
        void AddPath(int u, int v, List<(int, sbyte)> into)
        {
            while (u != v)
            {
                if (depth[u] >= depth[v])
                {
                    AddEdge(u, parentNode[u], parentGroup[u], into);
                    u = parentNode[u];
                }
                else
                {
                    AddEdge(parentNode[v], v, parentGroup[v], into);
                    v = parentNode[v];
                }
            }
        }
        foreach (int f in selfLoops)
            AddMesh(new List<(int, sbyte)> { (f, 1) }, f);
        for (int g = 0; g < groups; g++)
        {
            var (a, _) = groupEnds[g];
            var fils = groupFilaments[g];
            int first = fils[0];
            for (int k = 1; k < fils.Count; k++)
                AddMesh(new List<(int, sbyte)>
                {
                    (fils[k], Direction(fils[k], a)), (first, (sbyte)(-Direction(first, a)))
                }, fils[k]);
            if (treeGroup[g]) continue;
            var entries = new List<(int, sbyte)> { (first, Direction(first, a)) };
            AddPath(groupEnds[g].B, a, entries);
            AddMesh(entries, first);
        }
        int meshes = meshFilament.Count;

        // ---- Port paths: a unit current along the tree between the terminals.
        var ports = net.Ports;
        var portPath = new List<(int Filament, sbyte Sign)>[ports.Length];
        for (int q = 0; q < ports.Length; q++)
        {
            portPath[q] = new List<(int, sbyte)>();
            AddPath(ports[q].Positive, ports[q].Negative, portPath[q]);
        }

        // ---- The compressed inductance and the filament → mesh map.
        var compressed = new CompressedInductance(model, cancellationToken);
        var filamentMeshes = BuildTranspose(nb, meshFilament, meshSign);
        var resistance = new double[nb];
        for (int f = 0; f < nb; f++) resistance[f] = filaments[f].Resistance;

        // ---- Preconditioner pattern and real parts (frequency independent).
        var preconditioner = MeshPreconditioner.Build(meshes, meshFilament, meshSign, meshAnchor,
            filamentMeshes, resistance, compressed, filaments);

        var result = new List<PeecPoint>();
        var xb = new Complex[nb];
        foreach (double f in frequenciesHz)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(f > 0)) throw new ArgumentOutOfRangeException(nameof(frequenciesHz), "Frequencies must be above zero.");
            double omega = 2 * Math.PI * f;
            preconditioner.Prepare(omega);

            // Zm·x: scatter to branches, apply R + jωL, gather.
            var branchRe = new double[nb];
            var branchIm = new double[nb];
            var productRe = new double[nb];
            var productIm = new double[nb];
            void ApplyBranch(Complex[] branch, Complex[] into)
            {
                for (int i = 0; i < nb; i++) { branchRe[i] = branch[i].Real; branchIm[i] = branch[i].Imaginary; }
                Array.Clear(productRe);
                Array.Clear(productIm);
                compressed.MultiplyAdd(branchRe, productRe);
                compressed.MultiplyAdd(branchIm, productIm);
                for (int i = 0; i < nb; i++)
                    into[i] = new Complex(resistance[i] * branchRe[i] - omega * productIm[i],
                        resistance[i] * branchIm[i] + omega * productRe[i]);
            }
            void Multiply(Complex[] xm, Complex[] ym)
            {
                Array.Clear(xb);
                for (int m = 0; m < meshes; m++)
                {
                    var fils = meshFilament[m];
                    var signs = meshSign[m];
                    for (int k = 0; k < fils.Length; k++) xb[fils[k]] += signs[k] * xm[m];
                }
                var w = new Complex[nb];
                ApplyBranch(xb, w);
                for (int m = 0; m < meshes; m++)
                {
                    var fils = meshFilament[m];
                    var signs = meshSign[m];
                    Complex sum = Complex.Zero;
                    for (int k = 0; k < fils.Length; k++) sum += signs[k] * w[fils[k]];
                    ym[m] = sum;
                }
            }

            var z = new Complex[ports.Length, ports.Length];
            var currents = keepCurrents ? new Complex[ports.Length][] : null;
            var solution = new Complex[meshes];
            for (int q = 0; q < ports.Length; q++)
            {
                // Right-hand side: −M·Zb·Ib0 with Ib0 the port's unit tree-path current.
                var ib0 = new Complex[nb];
                foreach (var (fil, sign) in portPath[q]) ib0[fil] += sign;
                var w0 = new Complex[nb];
                ApplyBranch(ib0, w0);
                var rhs = new Complex[meshes];
                for (int m = 0; m < meshes; m++)
                {
                    var fils = meshFilament[m];
                    var signs = meshSign[m];
                    Complex sum = Complex.Zero;
                    for (int k = 0; k < fils.Length; k++) sum += signs[k] * w0[fils[k]];
                    rhs[m] = -sum;
                }
                Array.Clear(solution);
                Cocg(Multiply, preconditioner, rhs, solution, f, cancellationToken);

                // Branch currents and voltages, and every port's voltage along its path.
                var ib = ib0;
                for (int m = 0; m < meshes; m++)
                {
                    var fils = meshFilament[m];
                    var signs = meshSign[m];
                    for (int k = 0; k < fils.Length; k++) ib[fils[k]] += signs[k] * solution[m];
                }
                var voltage = new Complex[nb];
                ApplyBranch(ib, voltage);
                for (int p = 0; p < ports.Length; p++)
                {
                    Complex sum = Complex.Zero;
                    foreach (var (fil, sign) in portPath[p]) sum += sign * voltage[fil];
                    z[p, q] = sum;
                }
                if (currents is not null) currents[q] = (Complex[])ib.Clone();
            }
            for (int p = 0; p < ports.Length; p++)
                for (int q = p + 1; q < ports.Length; q++)
                    z[p, q] = z[q, p] = 0.5 * (z[p, q] + z[q, p]);
            result.Add(new PeecPoint(f, z, currents));
        }
        return result;
    }

    private static List<List<int>> PieceLists(int nn, List<(int Group, int Other)>[] adjacency)
    {
        var pieces = new List<List<int>>();
        var seen = new bool[nn];
        for (int start = 0; start < nn; start++)
        {
            if (seen[start]) continue;
            var piece = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                piece.Add(v);
                foreach (var (_, other) in adjacency[v])
                    if (!seen[other]) { seen[other] = true; queue.Enqueue(other); }
            }
            pieces.Add(piece);
        }
        return pieces;
    }

    private static List<(int Mesh, sbyte Sign)>[] BuildTranspose(int nb, List<int[]> meshFilament, List<sbyte[]> meshSign)
    {
        var map = new List<(int, sbyte)>[nb];
        for (int f = 0; f < nb; f++) map[f] = new List<(int, sbyte)>();
        for (int m = 0; m < meshFilament.Count; m++)
            for (int k = 0; k < meshFilament[m].Length; k++)
                map[meshFilament[m][k]].Add((m, meshSign[m][k]));
        return map;
    }

    /// <summary>Preconditioned COCG on the complex-symmetric mesh system.</summary>
    private static void Cocg(Action<Complex[], Complex[]> multiply, MeshPreconditioner preconditioner,
        Complex[] b, Complex[] x, double frequencyHz, CancellationToken cancellationToken)
    {
        int n = b.Length;
        double bNorm = Norm(b);
        if (bNorm == 0) return;
        var r = (Complex[])b.Clone();           // x starts at zero
        var z = new Complex[n];
        var p = new Complex[n];
        var ap = new Complex[n];
        preconditioner.Apply(r, z);
        Array.Copy(z, p, n);
        Complex rz = Bilinear(r, z);
        int maxIterations = Math.Max(400, 2 * n);
        for (int iteration = 1; iteration <= maxIterations; iteration++)
        {
            if ((iteration & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
            multiply(p, ap);
            Complex pap = Bilinear(p, ap);
            if (pap == Complex.Zero) break;
            Complex alpha = rz / pap;
            for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            if (Norm(r) <= CocgTolerance * bNorm) return;
            preconditioner.Apply(r, z);
            Complex rzNew = Bilinear(r, z);
            if (rz == Complex.Zero) break;
            Complex beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
        }
        if (Norm(r) > 1e-6 * bNorm)
            throw new InvalidOperationException(
                $"The iterative filament solve did not converge at {frequencyHz:g4} Hz "
                + $"(residual {Norm(r) / bNorm:g2} of the drive). Force the dense solve (PeecSolver.Dense) or coarsen the model.");
    }

    private static double Norm(Complex[] v)
    {
        double sum = 0;
        foreach (var c in v) sum += c.Real * c.Real + c.Imaginary * c.Imaginary;
        return Math.Sqrt(sum);
    }

    private static Complex Bilinear(Complex[] a, Complex[] b)
    {
        Complex sum = Complex.Zero;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>
/// The mesh preconditioner: M·(R + jω·L_near)·Mᵀ assembled sparse — L_near the exact pairs the
/// compression keeps — ordered by nested dissection and factored by <see cref="SparseComplexLdl"/>
/// once per frequency. Where the meshes share too many filaments for a sparse factor (long tree
/// paths through one root), it falls back to the mesh diagonal.
/// </summary>
internal sealed class MeshPreconditioner
{
    private readonly int _n;
    private readonly int[]? _rows, _columns;
    private readonly double[]? _valuesR, _valuesL;      // the pattern's M·R·Mᵀ and M·L_near·Mᵀ parts
    private readonly SparseComplexLdl? _ldl;
    private readonly Complex[] _diagonal;               // the same two parts, diagonal only
    private readonly double[] _diagonalR, _diagonalL;
    private Complex[] _inverseDiagonal;
    private bool _useFactor;

    public bool UsesFactor => _useFactor;

    private MeshPreconditioner(int n, int[]? rows, int[]? columns, double[]? valuesR, double[]? valuesL,
        SparseComplexLdl? ldl, double[] diagonalR, double[] diagonalL)
    {
        _n = n;
        _rows = rows;
        _columns = columns;
        _valuesR = valuesR;
        _valuesL = valuesL;
        _ldl = ldl;
        _diagonalR = diagonalR;
        _diagonalL = diagonalL;
        _diagonal = new Complex[n];
        _inverseDiagonal = new Complex[n];
    }

    /// <summary>Sharing beyond this many accumulations per mesh on average means the sparse
    /// factor would fill in too much; the diagonal serves instead.</summary>
    private const long MaxWork = 400_000_000;

    public static MeshPreconditioner Build(int meshes, List<int[]> meshFilament, List<sbyte[]> meshSign,
        List<int> meshAnchor, List<(int Mesh, sbyte Sign)>[] filamentMeshes, double[] resistance,
        CompressedInductance compressed, IReadOnlyList<PeecModel.Filament> filaments)
    {
        var near = compressed.NearPairs();          // CSR over filaments, symmetric, with L values
        // Diagonal always.
        var diagonalR = new double[meshes];
        var diagonalL = new double[meshes];
        var signOf = new Dictionary<int, sbyte>();
        for (int m = 0; m < meshes; m++)
        {
            var fils = meshFilament[m];
            var signs = meshSign[m];
            signOf.Clear();
            for (int a = 0; a < fils.Length; a++) signOf[fils[a]] = signs[a];
            double dr = 0, dl = 0;
            for (int a = 0; a < fils.Length; a++)
            {
                dr += resistance[fils[a]];
                for (int p = near.RowPointers[fils[a]]; p < near.RowPointers[fils[a] + 1]; p++)
                    if (signOf.TryGetValue(near.ColumnIndices[p], out sbyte s))
                        dl += signs[a] * s * near.Values[p];
            }
            diagonalR[m] = dr;
            diagonalL[m] = dl;
        }

        // Estimate the assembly work; past the cap only the diagonal is kept.
        long work = 0;
        for (int m = 0; m < meshes && work <= MaxWork; m++)
            foreach (int f in meshFilament[m])
                for (int p = near.RowPointers[f]; p < near.RowPointers[f + 1]; p++)
                    work += filamentMeshes[near.ColumnIndices[p]].Count;
        if (work > MaxWork)
            return new MeshPreconditioner(meshes, null, null, null, null, null, diagonalR, diagonalL);

        // Assemble M·R·Mᵀ + M·L_near·Mᵀ, full symmetric CSR.
        var accumulatorR = new Dictionary<int, double>();
        var accumulatorL = new Dictionary<int, double>();
        var rowLists = new List<(int Column, double R, double L)>[meshes];
        for (int m = 0; m < meshes; m++)
        {
            accumulatorR.Clear();
            accumulatorL.Clear();
            var fils = meshFilament[m];
            var signs = meshSign[m];
            for (int a = 0; a < fils.Length; a++)
            {
                int f = fils[a];
                foreach (var (m2, s2) in filamentMeshes[f])
                {
                    accumulatorR.TryGetValue(m2, out double v);
                    accumulatorR[m2] = v + signs[a] * s2 * resistance[f];
                }
                for (int p = near.RowPointers[f]; p < near.RowPointers[f + 1]; p++)
                {
                    int g = near.ColumnIndices[p];
                    double value = near.Values[p];
                    foreach (var (m2, s2) in filamentMeshes[g])
                    {
                        accumulatorL.TryGetValue(m2, out double v);
                        accumulatorL[m2] = v + signs[a] * s2 * value;
                    }
                }
            }
            var row = new List<(int, double, double)>();
            foreach (int m2 in accumulatorR.Keys.Union(accumulatorL.Keys))
            {
                accumulatorR.TryGetValue(m2, out double r);
                accumulatorL.TryGetValue(m2, out double l);
                row.Add((m2, r, l));
            }
            row.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            rowLists[m] = row;
        }
        var rows = new int[meshes + 1];
        for (int m = 0; m < meshes; m++) rows[m + 1] = rows[m] + rowLists[m].Count;
        var columns = new int[rows[meshes]];
        var valuesR = new double[rows[meshes]];
        var valuesL = new double[rows[meshes]];
        for (int m = 0; m < meshes; m++)
        {
            int at = rows[m];
            foreach (var (column, r, l) in rowLists[m])
            {
                columns[at] = column;
                valuesR[at] = r;
                valuesL[at] = l;
                at++;
            }
        }
        var point = new Point2[meshes];
        for (int m = 0; m < meshes; m++)
        {
            var centre = filaments[meshAnchor[m]].Centre;
            point[m] = new Point2(centre.X, centre.Y);
        }
        var order = SparseComplexLdl.NestedDissection(point, rows, columns);
        var ldl = new SparseComplexLdl(rows, columns, order);
        return new MeshPreconditioner(meshes, rows, columns, valuesR, valuesL, ldl, diagonalR, diagonalL);
    }

    /// <summary>Factors (or re-diagonalizes) for one frequency.</summary>
    public void Prepare(double omega)
    {
        for (int m = 0; m < _n; m++)
        {
            _diagonal[m] = new Complex(_diagonalR[m], omega * _diagonalL[m]);
            _inverseDiagonal[m] = _diagonal[m] == Complex.Zero ? Complex.One : 1 / _diagonal[m];
        }
        _useFactor = false;
        if (_ldl is null) return;
        int nnz = _valuesR!.Length;
        var re = _valuesR;
        var im = new double[nnz];
        for (int p = 0; p < nnz; p++) im[p] = omega * _valuesL![p];
        _useFactor = _ldl.Factor(re, im);
    }

    public void Apply(Complex[] r, Complex[] z)
    {
        if (_useFactor)
        {
            var xr = new double[_n];
            var xi = new double[_n];
            for (int i = 0; i < _n; i++) { xr[i] = r[i].Real; xi[i] = r[i].Imaginary; }
            _ldl!.Solve(xr, xi);
            bool finite = true;
            for (int i = 0; i < _n && finite; i++) finite = double.IsFinite(xr[i]) && double.IsFinite(xi[i]);
            if (finite)
            {
                for (int i = 0; i < _n; i++) z[i] = new Complex(xr[i], xi[i]);
                return;
            }
            _useFactor = false;
        }
        for (int i = 0; i < _n; i++) z[i] = _inverseDiagonal[i] * r[i];
    }
}

/// <summary>A symmetric sparse real matrix in CSR, as the preconditioner wants the near field.</summary>
internal sealed record NearField(int[] RowPointers, int[] ColumnIndices, double[] Values);

/// <summary>
/// The filament partial-inductance matrix in hierarchical low-rank form: a cluster tree over
/// the filament centres, far cluster pairs as U·Vᵀ factors found by adaptive cross
/// approximation of the same kernel the dense solve evaluates, near pairs (and the diagonal)
/// exact. Built once; <see cref="MultiplyAdd"/> is the only operation a frequency needs.
/// </summary>
internal sealed class CompressedInductance
{
    private sealed class Cluster
    {
        public int Start, Count;
        public Vector3D Min, Max;
        public Cluster? Left, Right;
        public double Diameter => (Max - Min).Length;
        public bool Leaf => Left is null;
    }

    private sealed class Block
    {
        public required Cluster Rows, Columns;
        public double[]? Dense;                      // Rows.Count × Columns.Count, row-major
        public List<(double[] U, double[] V)>? Factors;
    }

    private readonly IReadOnlyList<PeecModel.Filament> _filaments;
    private readonly int[] _order;                   // cluster position → filament
    private readonly int[] _place;                   // filament → cluster position
    private readonly List<Block> _blocks = new();
    private readonly int _n;
    private long _denseEntries, _factorEntries;

    /// <summary>Entries the blocks hold, against n² for the dense matrix.</summary>
    public (long Dense, long LowRank) StoredEntries => (_denseEntries, _factorEntries);

    public CompressedInductance(PeecModel model, CancellationToken cancellationToken)
    {
        _filaments = model.Filaments;
        _n = _filaments.Count;

        // One cluster tree per ORIENTATION: between two directions the kernel is smooth, so the
        // cross approximation is safe, where a block mixing parallel and perpendicular filaments
        // is block diagonal (perpendicular pairs couple not at all) and the approximation would
        // stop after seeing only one of the diagonal's parts. Perpendicular pairs of trees are
        // skipped outright.
        var bins = new Dictionary<(long, long, long), List<int>>();
        var directions = new List<Vector3D>();
        var binKeys = new List<(long, long, long)>();
        for (int f = 0; f < _n; f++)
        {
            var d = _filaments[f].Along;
            if (d.X < 0 || (d.X == 0 && (d.Y < 0 || (d.Y == 0 && d.Z < 0)))) d = -d;
            var key = ((long)Math.Round(d.X * 1e6), (long)Math.Round(d.Y * 1e6), (long)Math.Round(d.Z * 1e6));
            if (!bins.TryGetValue(key, out var list))
            {
                bins[key] = list = new List<int>();
                directions.Add(d);
                binKeys.Add(key);
            }
            list.Add(f);
        }
        _order = new int[_n];
        var roots = new List<Cluster>();
        int at = 0;
        foreach (var key in binKeys)
        {
            var list = bins[key];
            list.CopyTo(0, _order, at, list.Count);
            roots.Add(BuildCluster(at, list.Count));
            at += list.Count;
        }
        _place = new int[_n];
        for (int k = 0; k < _n; k++) _place[_order[k]] = k;
        for (int i = 0; i < roots.Count; i++)
            for (int j = i; j < roots.Count; j++)
            {
                if (Math.Abs(Vector3D.Dot(directions[i], directions[j])) < 1e-9) continue;
                Partition(roots[i], roots[j]);
            }

        var cache = new ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double>();
        Parallel.ForEach(_blocks, block =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Factors is not null) CrossApproximate(block, cache);
            if (block.Factors is null) Densify(block, cache);
        });
        foreach (var block in _blocks)
        {
            if (block.Dense is not null) _denseEntries += block.Dense.Length;
            else _factorEntries += block.Factors!.Count * (block.Rows.Count + block.Columns.Count);
        }
    }

    private Cluster BuildCluster(int start, int count)
    {
        var cluster = new Cluster { Start = start, Count = count };
        var min = new Vector3D(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vector3D(double.MinValue, double.MinValue, double.MinValue);
        for (int k = start; k < start + count; k++)
        {
            var c = _filaments[_order[k]].Centre;
            min = new Vector3D(Math.Min(min.X, c.X), Math.Min(min.Y, c.Y), Math.Min(min.Z, c.Z));
            max = new Vector3D(Math.Max(max.X, c.X), Math.Max(max.Y, c.Y), Math.Max(max.Z, c.Z));
        }
        cluster.Min = min;
        cluster.Max = max;
        if (count <= LeafSize) return cluster;
        var side = max - min;
        Comparison<int> by = side.X >= side.Y && side.X >= side.Z
            ? (a, b) => _filaments[a].Centre.X.CompareTo(_filaments[b].Centre.X)
            : side.Y >= side.Z
                ? (a, b) => _filaments[a].Centre.Y.CompareTo(_filaments[b].Centre.Y)
                : (a, b) => _filaments[a].Centre.Z.CompareTo(_filaments[b].Centre.Z);
        Array.Sort(_order, start, count, Comparer<int>.Create(by));
        int half = count / 2;
        cluster.Left = BuildCluster(start, half);
        cluster.Right = BuildCluster(start + half, count - half);
        return cluster;
    }

    private const int LeafSize = 48;
    private const int MaxRank = 64;
    private const double AcaTolerance = 1e-7;

    private static double Distance(Cluster a, Cluster b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        double dz = Math.Max(0, Math.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void Partition(Cluster a, Cluster b)
    {
        // Only the upper half: blocks with a.Start ≤ b.Start; the matvec applies transposes.
        if (!ReferenceEquals(a, b) && Distance(a, b) >= 0.5 * Math.Max(a.Diameter, b.Diameter))
        {
            _blocks.Add(new Block { Rows = a, Columns = b, Factors = new List<(double[], double[])>() });
            return;
        }
        if (a.Leaf && b.Leaf)
        {
            _blocks.Add(new Block { Rows = a, Columns = b });
            return;
        }
        if (ReferenceEquals(a, b))
        {
            Partition(a.Left!, b.Left!);
            Partition(a.Left!, b.Right!);
            Partition(a.Right!, b.Right!);
            return;
        }
        // Split the larger side (both are kept ordered by start).
        if (!a.Leaf && (b.Leaf || a.Count >= b.Count))
        {
            Order(a.Left!, b);
            Order(a.Right!, b);
        }
        else
        {
            Order(a, b.Left!);
            Order(a, b.Right!);
        }
        void Order(Cluster x, Cluster y)
        {
            if (x.Start <= y.Start) Partition(x, y);
            else Partition(y, x);
        }
    }

    private double Kernel(int row, int column,
        ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double> cache) =>
        PeecModel.MutualOf(_filaments[_order[row]], _filaments[_order[column]], cache);

    private void Densify(Block block,
        ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double> cache)
    {
        block.Factors = null;
        int rows = block.Rows.Count, columns = block.Columns.Count;
        var dense = new double[rows * columns];
        bool self = ReferenceEquals(block.Rows, block.Columns);
        for (int i = 0; i < rows; i++)
            for (int j = self ? i : 0; j < columns; j++)
            {
                double value = Kernel(block.Rows.Start + i, block.Columns.Start + j, cache);
                dense[i * columns + j] = value;
                if (self && j > i) dense[j * columns + i] = value;
            }
        block.Dense = dense;
    }

    /// <summary>Partially pivoted ACA; null factors on failure (the caller densifies).</summary>
    private void CrossApproximate(Block block,
        ConcurrentDictionary<(long, long, long, long, long, long, long, long, long), double> cache)
    {
        int rows = block.Rows.Count, columns = block.Columns.Count;
        var factors = block.Factors!;
        var usedRows = new bool[rows];
        double frobenius = 0;
        int nextRow = 0;
        int maxRank = Math.Min(MaxRank, Math.Min(rows, columns));
        for (int rank = 0; rank < maxRank; rank++)
        {
            // The residual's row: kernel row minus what the factors already hold.
            int i = -1;
            double[]? v = null;
            for (int attempt = 0; attempt < rows; attempt++)
            {
                int candidate = nextRow;
                nextRow = (nextRow + 1) % rows;
                if (usedRows[candidate]) continue;
                var row = new double[columns];
                for (int j = 0; j < columns; j++)
                    row[j] = Kernel(block.Rows.Start + candidate, block.Columns.Start + j, cache);
                foreach (var (u, w) in factors)
                {
                    double scale = u[candidate];
                    if (scale == 0) continue;
                    for (int j = 0; j < columns; j++) row[j] -= scale * w[j];
                }
                usedRows[candidate] = true;
                if (Largest(row, out _) > 0) { i = candidate; v = row; break; }
            }
            if (i < 0) return;                       // residual exhausted: converged (or zero block)
            Largest(v!, out int pivot);
            double pivotValue = v![pivot];
            for (int j = 0; j < columns; j++) v[j] /= pivotValue;
            var u2 = new double[rows];
            for (int r2 = 0; r2 < rows; r2++)
                u2[r2] = Kernel(block.Rows.Start + r2, block.Columns.Start + pivot, cache);
            foreach (var (u, w) in factors)
            {
                double scale = w[pivot];
                if (scale == 0) continue;
                for (int r2 = 0; r2 < rows; r2++) u2[r2] -= scale * u[r2];
            }
            factors.Add((u2, v));
            double uNorm = NormOf(u2), vNorm = NormOf(v);
            frobenius += uNorm * uNorm * vNorm * vNorm;
            foreach (var (u, w) in factors)
                if (!ReferenceEquals(u, u2))
                    frobenius += 2 * Math.Abs(Dot(u, u2) * Dot(w, v));
            if (uNorm * vNorm <= AcaTolerance * Math.Sqrt(Math.Max(frobenius, 0))) return;
            // The next row: where the new column term is largest, unused.
            double best = -1;
            int bestRow = -1;
            for (int r2 = 0; r2 < rows; r2++)
                if (!usedRows[r2] && Math.Abs(u2[r2]) > best) { best = Math.Abs(u2[r2]); bestRow = r2; }
            if (bestRow >= 0) nextRow = bestRow;
        }
        block.Factors = null;                        // rank cap hit: keep it exact instead
    }

    private static double Largest(double[] v, out int at)
    {
        double best = -1;
        at = 0;
        for (int j = 0; j < v.Length; j++)
        {
            double a = Math.Abs(v[j]);
            if (a > best) { best = a; at = j; }
        }
        return best;
    }

    private static double NormOf(double[] v)
    {
        double sum = 0;
        foreach (double x in v) sum += x * x;
        return Math.Sqrt(sum);
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>y += L·x over the filaments (the caller's numbering).</summary>
    public void MultiplyAdd(double[] x, double[] y)
    {
        var xp = new double[_n];
        for (int k = 0; k < _n; k++) xp[k] = x[_order[k]];
        var total = new double[_n];
        var guard = new object();
        Parallel.ForEach(_blocks, () => new double[_n], (block, _, local) =>
        {
            int rowStart = block.Rows.Start, columnStart = block.Columns.Start;
            int rows = block.Rows.Count, columns = block.Columns.Count;
            bool self = ReferenceEquals(block.Rows, block.Columns);
            if (block.Dense is { } dense)
            {
                for (int i = 0; i < rows; i++)
                {
                    double sum = 0;
                    int at = i * columns;
                    for (int j = 0; j < columns; j++) sum += dense[at + j] * xp[columnStart + j];
                    local[rowStart + i] += sum;
                }
                if (!self)
                    for (int j = 0; j < columns; j++)
                    {
                        double sum = 0;
                        for (int i = 0; i < rows; i++) sum += dense[i * columns + j] * xp[rowStart + i];
                        local[columnStart + j] += sum;
                    }
            }
            else
                foreach (var (u, v) in block.Factors!)
                {
                    double forward = 0;
                    for (int j = 0; j < columns; j++) forward += v[j] * xp[columnStart + j];
                    for (int i = 0; i < rows; i++) local[rowStart + i] += u[i] * forward;
                    double backward = 0;
                    for (int i = 0; i < rows; i++) backward += u[i] * xp[rowStart + i];
                    for (int j = 0; j < columns; j++) local[columnStart + j] += v[j] * backward;
                }
            return local;
        }, local =>
        {
            lock (guard)
                for (int k = 0; k < _n; k++) total[k] += local[k];
        });
        for (int k = 0; k < _n; k++) y[_order[k]] += total[k];
    }

    /// <summary>The exact (dense-block) pairs as a symmetric CSR over the filaments, for the
    /// preconditioner's near field.</summary>
    public NearField NearPairs()
    {
        var lists = new List<(int Column, double Value)>[_n];
        for (int f = 0; f < _n; f++) lists[f] = new List<(int, double)>();
        foreach (var block in _blocks)
        {
            if (block.Dense is not { } dense) continue;
            int rows = block.Rows.Count, columns = block.Columns.Count;
            bool self = ReferenceEquals(block.Rows, block.Columns);
            for (int i = 0; i < rows; i++)
            {
                int row = _order[block.Rows.Start + i];
                for (int j = 0; j < columns; j++)
                {
                    int column = _order[block.Columns.Start + j];
                    double value = dense[i * columns + j];
                    lists[row].Add((column, value));
                    if (!self) lists[column].Add((row, value));
                }
            }
        }
        var pointers = new int[_n + 1];
        for (int f = 0; f < _n; f++)
        {
            lists[f].Sort((a, b) => a.Column.CompareTo(b.Column));
            pointers[f + 1] = pointers[f] + lists[f].Count;
        }
        var columnsOut = new int[pointers[_n]];
        var values = new double[pointers[_n]];
        for (int f = 0; f < _n; f++)
        {
            int at = pointers[f];
            foreach (var (column, value) in lists[f])
            {
                columnsOut[at] = column;
                values[at] = value;
                at++;
            }
        }
        return new NearField(pointers, columnsOut, values);
    }
}
