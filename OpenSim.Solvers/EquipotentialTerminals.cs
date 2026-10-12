using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// The equipotential current terminals of an electrical solve (<see cref="CurrentFlow"/>
/// with <see cref="CurrentFlow.Equipotential"/>), as a tying of degrees of freedom: every
/// node of a terminal face shares ONE unknown potential, and the total current is the load
/// on that unknown. Tying K to Tᵀ·K·T keeps it symmetric positive definite — the free
/// potential of a terminal is an ordinary unknown of the reduced system, not a Lagrange
/// multiplier — so the shared conjugate-gradient solver applies unchanged, and the full
/// potential is read back by expansion. A face's nodal reactions K·φ then sum to the
/// injected current, distributed over the face as the field wants, which is what makes the
/// terminal an equipotential and not a uniform injection.
/// </summary>
internal sealed class EquipotentialTerminals
{
    private EquipotentialTerminals(IReadOnlyList<CurrentFlow> terminals, int[] map, int[] masterOf, int tiedCount)
    {
        Terminals = terminals;
        _map = map;
        _masterOf = masterOf;
        TiedCount = tiedCount;
    }

    private readonly int[] _map;        // node → tied DOF
    private readonly int[] _masterOf;   // terminal → tied DOF

    public IReadOnlyList<CurrentFlow> Terminals { get; }

    /// <summary>Number of unknowns after tying: untied nodes plus one per terminal.</summary>
    public int TiedCount { get; }

    /// <summary>The tied DOF of a node.</summary>
    public int DofOf(int node) => _map[node];

    /// <summary>
    /// The terminals of a condition set, or null when there is none. A terminal that shares
    /// a node with a voltage would be two statements about one potential, and two terminals
    /// sharing a node would be one equipotential carrying two currents; both are refused by
    /// name rather than resolved silently.
    /// </summary>
    public static EquipotentialTerminals? Of(FeMesh mesh, IReadOnlyList<BoundaryCondition> conditions)
    {
        var terminals = conditions.OfType<CurrentFlow>().Where(c => c.Equipotential).ToList();
        if (terminals.Count == 0) return null;

        int nodes = mesh.NodeCount;
        var terminalOf = new int[nodes];
        Array.Fill(terminalOf, -1);
        for (int t = 0; t < terminals.Count; t++)
            foreach (int n in mesh.GetScopeNodes(terminals[t]))
            {
                if (terminalOf[n] >= 0 && terminalOf[n] != t)
                    throw new InvalidOperationException(
                        $"Equipotential terminals '{terminals[terminalOf[n]].Name}' and '{terminals[t].Name}' " +
                        "share mesh nodes: their faces meet, so they would be one equipotential. Merge " +
                        "them into one terminal or separate the faces.");
                terminalOf[n] = t;
            }
        foreach (var voltage in conditions.OfType<VoltagePotential>())
            foreach (int n in mesh.GetScopeNodes(voltage))
                if (terminalOf[n] >= 0)
                    throw new InvalidOperationException(
                        $"Equipotential terminal '{terminals[terminalOf[n]].Name}' shares mesh nodes with " +
                        $"voltage '{voltage.Name}': a terminal's potential is found by the solve and cannot " +
                        "also be prescribed. Separate the faces, or make the terminal a voltage.");

        var map = new int[nodes];
        int next = 0;
        for (int n = 0; n < nodes; n++)
            if (terminalOf[n] < 0) map[n] = next++;
        var masterOf = new int[terminals.Count];
        for (int t = 0; t < terminals.Count; t++) masterOf[t] = next++;
        for (int n = 0; n < nodes; n++)
            if (terminalOf[n] >= 0) map[n] = masterOf[terminalOf[n]];

        return new EquipotentialTerminals(terminals, map, masterOf, next);
    }

    /// <summary>Tᵀ·K·T: the rows and columns of a terminal's nodes summed into its DOF. Two
    /// matrices with the same sparsity tie to the same sparsity (one walk, sorted columns).</summary>
    public CsrMatrix Tie(CsrMatrix k)
    {
        var builder = new SparseMatrixBuilder(TiedCount, TiedCount);
        for (int row = 0; row < k.RowCount; row++)
        {
            int r = _map[row];
            for (int i = k.RowPointers[row]; i < k.RowPointers[row + 1]; i++)
                builder.Add(r, _map[k.ColumnIndices[i]], k.Values[i]);
        }
        return builder.Build();
    }

    /// <summary>Tᵀ·f: a terminal's nodal loads summed into its DOF (the total current).</summary>
    public double[] TieLoads(IReadOnlyList<double> loads)
    {
        var tied = new double[TiedCount];
        for (int n = 0; n < loads.Count; n++) tied[_map[n]] += loads[n];
        return tied;
    }

    public Complex[] TieLoads(IReadOnlyList<Complex> loads)
    {
        var tied = new Complex[TiedCount];
        for (int n = 0; n < loads.Count; n++) tied[_map[n]] += loads[n];
        return tied;
    }

    /// <summary>Prescribed values on tied DOFs (no prescribed node is a terminal node, by
    /// construction).</summary>
    public Dictionary<int, T> TiePrescribed<T>(IReadOnlyDictionary<int, T> prescribed)
    {
        var tied = new Dictionary<int, T>(prescribed.Count);
        foreach (var (node, value) in prescribed) tied[_map[node]] = value;
        return tied;
    }

    /// <summary>T·u: the nodal potential, every terminal node at its terminal's value.</summary>
    public double[] Expand(IReadOnlyList<double> tied)
    {
        var full = new double[_map.Length];
        for (int n = 0; n < full.Length; n++) full[n] = tied[_map[n]];
        return full;
    }

    public Complex[] Expand(IReadOnlyList<Complex> tied)
    {
        var full = new Complex[_map.Length];
        for (int n = 0; n < full.Length; n++) full[n] = tied[_map[n]];
        return full;
    }

    /// <summary>A full-length vector on the tied DOFs (a warm start); a terminal takes any
    /// of its nodes' values, which agree.</summary>
    public Complex[] Restrict(IReadOnlyList<Complex> full)
    {
        var tied = new Complex[TiedCount];
        for (int n = 0; n < full.Count; n++) tied[_map[n]] = full[n];
        return tied;
    }

    /// <summary>The potential of a terminal in a full nodal solution.</summary>
    public T PotentialOf<T>(int terminal, IReadOnlyList<T> full, FeMesh mesh)
    {
        int node = mesh.GetScopeNodes(Terminals[terminal]).First();
        return full[node];
    }

    /// <summary>Whether a condition is one of these terminals.</summary>
    public bool Contains(CurrentFlow current) => Terminals.Contains(current);
}
