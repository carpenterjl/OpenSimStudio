using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Magnetics;

/// <summary>The linear-system interface the magnetic meshes assemble into: banded LU on the
/// rectilinear grid, sparse LDLᵀ on an unstructured one.</summary>
internal interface ILinearSolver
{
    void Add(int i, int j, double re, double im = 0);
    void Pin(int i);
    void Factor();
    Complex[] Solve(IReadOnlyList<Complex> rhs);
}

/// <summary>
/// Complex-symmetric sparse LDLᵀ for an unstructured mesh, where the node numbering has no useful
/// band: entries are accumulated by (row, column), pinned rows and columns are replaced by the
/// identity at <see cref="Factor"/>, and the elimination order is a nested dissection of the
/// node positions.
/// </summary>
internal sealed class SparseLdlSolver : ILinearSolver
{
    private readonly int _n;
    private readonly IReadOnlyList<Point2> _positions;
    private readonly Dictionary<long, int> _slot = new();
    private readonly List<int> _ri = new(), _ci = new();
    private readonly List<double> _re = new(), _im = new();
    private readonly bool[] _pinned;
    private SparseComplexLdl? _ldl;

    public SparseLdlSolver(IReadOnlyList<Point2> positions)
    {
        _n = positions.Count;
        _positions = positions;
        _pinned = new bool[_n];
    }

    public void Add(int i, int j, double re, double im = 0)
    {
        long key = (long)i * _n + j;
        if (_slot.TryGetValue(key, out int k))
        {
            _re[k] += re;
            _im[k] += im;
        }
        else
        {
            _slot[key] = _re.Count;
            _ri.Add(i);
            _ci.Add(j);
            _re.Add(re);
            _im.Add(im);
        }
    }

    public void Pin(int i) => _pinned[i] = true;

    public void Factor()
    {
        int m = _re.Count;
        // CSR pattern (row-major, columns ascending), entries mapped from the accumulator.
        var rowCount = new int[_n + 1];
        for (int k = 0; k < m; k++) rowCount[_ri[k] + 1]++;
        var rowPtr = new int[_n + 1];
        for (int i = 0; i < _n; i++) rowPtr[i + 1] = rowPtr[i] + rowCount[i + 1];
        var order = Enumerable.Range(0, m).ToArray();
        Array.Sort(order, (a, b) => _ri[a] != _ri[b] ? _ri[a] - _ri[b] : _ci[a] - _ci[b]);
        var colIdx = new int[m];
        for (int k = 0; k < m; k++) colIdx[k] = _ci[order[k]];

        var vr = new double[m];
        var vi = new double[m];
        for (int k = 0; k < m; k++)
        {
            int src = order[k];
            int i = _ri[src], j = _ci[src];
            if (_pinned[i] || _pinned[j])
            {
                vr[k] = i == j ? 1 : 0;
                vi[k] = 0;
            }
            else
            {
                vr[k] = _re[src];
                vi[k] = _im[src];
            }
        }
        var perm = SparseComplexLdl.NestedDissection(_positions, rowPtr, colIdx);
        _ldl = new SparseComplexLdl(rowPtr, colIdx, perm);
        if (!_ldl.Factor(vr, vi))
            throw new InvalidOperationException("Zero pivot in the sparse factorization: a node is not connected to anything.");
    }

    public Complex[] Solve(IReadOnlyList<Complex> rhs)
    {
        if (_ldl is null) throw new InvalidOperationException("Factor first.");
        var xr = new double[_n];
        var xi = new double[_n];
        for (int i = 0; i < _n; i++) { xr[i] = rhs[i].Real; xi[i] = rhs[i].Imaginary; }
        _ldl.Solve(xr, xi);
        var x = new Complex[_n];
        for (int i = 0; i < _n; i++) x[i] = new Complex(xr[i], xi[i]);
        return x;
    }
}
