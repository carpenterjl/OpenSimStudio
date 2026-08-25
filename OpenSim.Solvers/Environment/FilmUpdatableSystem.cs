using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// A reduced thermal system whose SURFACE FILM terms can be rewritten without rebuilding it.
///
/// The lagged-coefficient loop re-evaluates the film every iterate, and every iterate used to
/// pay for that twice over at the size of the whole matrix: once to copy every non-zero into a
/// fresh builder so the Robin terms could be stamped on, and again to walk the result into a
/// reduced system. Neither cost is about the film. The film touches only the node pairs of the
/// boundary triangles — a surface, not a volume — so the positions it writes to are the same
/// on every iterate and can be resolved once.
///
/// What is resolved once: the reduced sparsity (film positions included, at zero), the base
/// values, and for each boundary triangle the nine places its stamp lands. What happens per
/// iterate is a copy of the base values plus O(boundary triangles) additions.
///
/// <para>
/// The arithmetic is the rebuild's, entry for entry and in the same order — which is a
/// stronger requirement than it sounds, and one the obvious implementation misses. A stamp
/// whose COLUMN is a prescribed DOF does not enter the matrix; it enters the right-hand-side
/// correction as value * prescribed. Accumulating those film terms separately and adding them
/// to a stored base correction computes base*p + film*p, while the rebuild computes
/// (base + film)*p — equal in exact arithmetic, and not equal in floating point. So the film
/// is accumulated ONTO the base value here, and the multiply happens afterwards, exactly once,
/// exactly where the rebuild does it.
/// </para>
/// </summary>
internal sealed class FilmUpdatableSystem
{
    /// <summary>Where one entry of a triangle stamp goes: into the reduced matrix values, or
    /// into the working slot of a prescribed column (which later feeds the correction).</summary>
    private readonly record struct StampSlot(int ValueIndex, int PrescribedSlot);

    /// <summary>One prescribed-column contribution to a free row's correction, in the order
    /// the reduction walks them.</summary>
    private readonly record struct CorrectionTerm(int Row, int PrescribedSlot, double Value);

    private readonly FeMesh _mesh;
    private readonly ConstrainedSystemSolver.ReducedSystem _system;
    private readonly double[] _baseValues;
    private readonly double[] _basePrescribedValues;   // base matrix value at each prescribed slot
    private readonly double[] _prescribedWork;         // that value plus this iterate's film
    private readonly CorrectionTerm[] _correctionTerms;
    private readonly double[] _triangleArea;
    private readonly StampSlot[] _slots;               // 9 per boundary triangle, row-major (i, j)

    private FilmUpdatableSystem(FeMesh mesh, ConstrainedSystemSolver.ReducedSystem system,
        double[] baseValues, double[] basePrescribedValues, CorrectionTerm[] correctionTerms,
        double[] triangleArea, StampSlot[] slots)
    {
        _mesh = mesh;
        _system = system;
        _baseValues = baseValues;
        _basePrescribedValues = basePrescribedValues;
        _prescribedWork = new double[basePrescribedValues.Length];
        _correctionTerms = correctionTerms;
        _triangleArea = triangleArea;
        _slots = slots;
    }

    /// <summary>The reduced system, holding whatever film was last applied.</summary>
    public ConstrainedSystemSolver.ReducedSystem System => _system;

    /// <summary>
    /// Prepares the system for repeated film updates.
    /// <para>
    /// The pattern is built with a ZERO film stamped on every boundary triangle, purely so the
    /// sparsity contains every position a film can ever write to. Robin terms couple the nodes
    /// of one boundary triangle, which the element behind it already couples, so in practice
    /// this adds no position — but relying on that would make the sparsity depend on a property
    /// of the mesh that nothing checks.
    /// </para>
    /// </summary>
    public static FilmUpdatableSystem Prepare(CsrMatrix baseMatrix, FeMesh mesh,
        IReadOnlyDictionary<int, double> prescribed)
    {
        var pattern = new SparseMatrixBuilder(baseMatrix.RowCount, baseMatrix.ColumnCount);
        for (int row = 0; row < baseMatrix.RowCount; row++)
            for (int k = baseMatrix.RowPointers[row]; k < baseMatrix.RowPointers[row + 1]; k++)
                pattern.Add(row, baseMatrix.ColumnIndices[k], baseMatrix.Values[k]);
        foreach (var triangle in mesh.BoundaryTriangles)
            ScalarDiffusionAssembler.AddRobinSurface(pattern, mesh, triangle, 0.0);

        var full = pattern.Build();
        var system = ConstrainedSystemSolver.Reduce(full, prescribed, allowUnconstrained: true);
        var baseValues = (double[])system.Reduced.Values.Clone();

        // One working slot per (free row, prescribed column) entry of the full pattern, and
        // the correction terms that read them — enumerated in exactly the order the reduction
        // accumulated the base correction, so the sums are built the same way.
        var slotOfEntry = new Dictionary<(int Row, int Column), int>();
        var basePrescribedValues = new List<double>();
        var correctionTerms = new List<CorrectionTerm>();

        for (int row = 0; row < full.RowCount; row++)
        {
            int r = system.FreeIndexOf(row);
            if (r < 0) continue;
            for (int k = full.RowPointers[row]; k < full.RowPointers[row + 1]; k++)
            {
                int column = full.ColumnIndices[k];
                if (system.FreeIndexOf(column) >= 0) continue;

                int slot = basePrescribedValues.Count;
                slotOfEntry[(row, column)] = slot;
                basePrescribedValues.Add(full.Values[k]);
                correctionTerms.Add(new CorrectionTerm(r, slot, prescribed[column]));
            }
        }

        // Resolve every stamp position once.
        int count = mesh.BoundaryTriangles.Count;
        var areas = new double[count];
        var slots = new StampSlot[count * 9];
        Span<int> nodes = stackalloc int[3];

        for (int t = 0; t < count; t++)
        {
            var triangle = mesh.BoundaryTriangles[t];
            areas[t] = ScalarDiffusionAssembler.SurfaceArea(mesh, triangle);
            nodes[0] = triangle.A; nodes[1] = triangle.B; nodes[2] = triangle.C;

            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    int rowDof = nodes[i], columnDof = nodes[j];
                    int r = system.FreeIndexOf(rowDof);
                    if (r < 0)
                    {
                        // A prescribed ROW has no equation in the reduced system, so nothing
                        // this entry contributes is ever read.
                        slots[t * 9 + i * 3 + j] = new StampSlot(-1, -1);
                        continue;
                    }

                    int c = system.FreeIndexOf(columnDof);
                    slots[t * 9 + i * 3 + j] = c >= 0
                        ? new StampSlot(system.Reduced.IndexOf(r, c), -1)
                        : new StampSlot(-1, slotOfEntry[(rowDof, columnDof)]);
                }
        }

        var updatable = new FilmUpdatableSystem(mesh, system, baseValues,
            basePrescribedValues.ToArray(), correctionTerms.ToArray(), areas, slots);
        updatable.RebuildCorrection();
        return updatable;
    }

    /// <summary>
    /// Rewrites the system for a new film. Values start from the film-free base and take the
    /// same stamps, in the same order, that a rebuild would have applied.
    /// </summary>
    public void Apply(SurfaceFilmModel film)
    {
        Array.Copy(_baseValues, _system.Reduced.Values, _baseValues.Length);
        Array.Copy(_basePrescribedValues, _prescribedWork, _prescribedWork.Length);

        for (int t = 0; t < _mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;

            double a12 = h * _triangleArea[t] / 12.0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double value = i == j ? 2 * a12 : a12;
                    var slot = _slots[t * 9 + i * 3 + j];
                    if (slot.ValueIndex >= 0)
                        _system.Reduced.Values[slot.ValueIndex] += value;
                    else if (slot.PrescribedSlot >= 0)
                        _prescribedWork[slot.PrescribedSlot] += value;
                }
        }

        RebuildCorrection();
    }

    /// <summary>
    /// The K_fc * u_c correction, summed from the working values in the order the reduction
    /// walks them — the multiply happens here, once per entry, on the value the rebuild would
    /// have stored.
    /// </summary>
    private void RebuildCorrection()
    {
        _system.ClearCorrection();
        foreach (var term in _correctionTerms)
            _system.AddCorrection(term.Row, _prescribedWork[term.PrescribedSlot] * term.Value);
    }
}
