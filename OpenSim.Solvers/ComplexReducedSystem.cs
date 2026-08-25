using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// A complex system reduced ONCE and re-valued per frequency — the AC sweep's counterpart to
/// <see cref="ConstrainedSystemSolver.ReducedSystem"/>.
///
/// A sweep solves the same mesh with the same electrodes at a dozen or more frequencies, and
/// only omega changes between them. Reducing per point rebuilt the free-DOF map, walked every
/// non-zero into two fresh builders, and re-validated that the two shared a sparsity pattern —
/// none of which depends on the frequency at all.
///
/// What is frequency-dependent is a single multiply per entry, and that is all this does per
/// point. The arithmetic is the old path's, entry for entry: the sigma and epsilon parts are
/// selected by the same walk in the same order, combined into <c>re + j*omega*im</c> exactly as
/// <see cref="ComplexCsrMatrix.Combine"/> did, and the prescribed-column terms are subtracted
/// from the right-hand side in the same CSR order they were before.
/// </summary>
internal sealed class ComplexReducedSystem
{
    /// <summary>One prescribed-column contribution to a free row's right-hand side, in the
    /// order the reduction walks them.</summary>
    private readonly record struct CorrectionTerm(int Row, double Real, double Imag, Complex Prescribed);

    private readonly int[] _freeIndex;              // full DOF -> free index, -1 when prescribed
    private readonly IReadOnlyDictionary<int, Complex> _prescribed;
    private readonly CsrMatrix _real;               // reduced sigma assembly
    private readonly CsrMatrix _imag;               // reduced epsilon assembly, before omega
    private readonly CorrectionTerm[] _correction;

    public int FreeCount { get; }

    private ComplexReducedSystem(int[] freeIndex, IReadOnlyDictionary<int, Complex> prescribed,
        int freeCount, CsrMatrix real, CsrMatrix imag, CorrectionTerm[] correction)
    {
        _freeIndex = freeIndex;
        _prescribed = prescribed;
        FreeCount = freeCount;
        _real = real;
        _imag = imag;
        _correction = correction;
    }

    /// <summary>
    /// Reduces the two real assemblies once. They must already share a sparsity pattern, which
    /// they do by construction — both are assembled over the same mesh connectivity — and which
    /// <see cref="ComplexCsrMatrix.Combine"/> checks on the way in.
    /// </summary>
    public static ComplexReducedSystem Reduce(CsrMatrix real, CsrMatrix imag,
        IReadOnlyDictionary<int, Complex> prescribed)
    {
        if (prescribed.Count == 0)
            throw new InvalidOperationException(
                "The system has no prescribed potentials; without a reference the solution is not unique.");
        if (!real.RowPointers.AsSpan().SequenceEqual(imag.RowPointers)
            || !real.ColumnIndices.AsSpan().SequenceEqual(imag.ColumnIndices))
            throw new ArgumentException(
                "The real and imaginary matrices must share an identical sparsity pattern " +
                "(assemble both over the same mesh connectivity).");

        int n = real.RowCount;
        var freeIndex = new int[n];
        int freeCount = 0;
        for (int i = 0; i < n; i++)
            freeIndex[i] = prescribed.ContainsKey(i) ? -1 : freeCount++;

        // Both parts are selected by ONE walk, so the reduced pair is guaranteed the same
        // pattern for the same reason the full pair was.
        var reBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var imBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var correction = new List<CorrectionTerm>();

        for (int row = 0; row < n; row++)
        {
            int r = freeIndex[row];
            if (r < 0) continue;
            for (int k = real.RowPointers[row]; k < real.RowPointers[row + 1]; k++)
            {
                int col = real.ColumnIndices[k];
                int c = freeIndex[col];
                if (c >= 0)
                {
                    reBuilder.Add(r, c, real.Values[k]);
                    imBuilder.Add(r, c, imag.Values[k]);
                }
                else
                {
                    correction.Add(new CorrectionTerm(r, real.Values[k], imag.Values[k],
                        prescribed[col]));
                }
            }
        }

        return new ComplexReducedSystem(freeIndex, prescribed, freeCount,
            reBuilder.Build(), imBuilder.Build(), correction.ToArray());
    }

    /// <summary>
    /// The reduced matrix at one angular frequency: sigma + j*omega*epsilon, zipped by the
    /// same routine — and therefore the same per-entry arithmetic — the unreduced path used.
    /// </summary>
    public ComplexCsrMatrix MatrixAt(double omega) =>
        ComplexCsrMatrix.Combine(_real, _imag, omega);

    /// <summary>The reduced right-hand side at one angular frequency.</summary>
    public Complex[] LoadsAt(double omega, IReadOnlyList<Complex> fullLoads)
    {
        var rhs = new Complex[FreeCount];
        for (int i = 0; i < _freeIndex.Length; i++)
            if (_freeIndex[i] >= 0)
                rhs[_freeIndex[i]] = fullLoads[i];

        // Combined then multiplied, in the CSR order the walk found them — the same
        // expression, evaluated the same way, as when the full matrix was reduced per point.
        foreach (var term in _correction)
            rhs[term.Row] -= new Complex(term.Real, omega * term.Imag) * term.Prescribed;

        return rhs;
    }

    /// <summary>The free entries of a full-length vector — a warm start from the previous
    /// frequency point.</summary>
    public Complex[] Restrict(IReadOnlyList<Complex>? fullVector)
    {
        var free = new Complex[FreeCount];
        if (fullVector is null) return free;
        for (int i = 0; i < _freeIndex.Length; i++)
            if (_freeIndex[i] >= 0)
                free[_freeIndex[i]] = fullVector[i];
        return free;
    }

    /// <summary>Re-inserts the prescribed phasors around a free-DOF solution.</summary>
    public Complex[] Expand(IReadOnlyList<Complex> freeSolution)
    {
        var full = new Complex[_freeIndex.Length];
        for (int i = 0; i < _freeIndex.Length; i++)
            full[i] = _freeIndex[i] >= 0 ? freeSolution[_freeIndex[i]] : _prescribed[i];
        return full;
    }
}
