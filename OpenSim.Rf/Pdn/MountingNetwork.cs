using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// The copper above a board's outer plate on one side — pads, escape traces and via barrels
/// down to the plate — as one network of partial inductances, so that every mounting couples to
/// every other: neighbouring capacitors, a pad with several vias, the load's field of vias.
///
/// <para>z runs up from the plate's surface (z = 0) away from the board. The plate is taken as
/// a perfect conductor, by images: a conductor's image carries the opposite horizontal and the
/// same vertical current, so the partial inductance of branch i with branch j is
/// M(i, j) − M(i, mirror(j)). A barrel standing on the plate continues into its own image, and
/// the two are taken as one tube of twice the height (half its self-inductance), not as two stubs
/// coupled end to end at the plate. Kernels: <see cref="LoopComposer.PartialMutual"/> — round
/// barrels as filaments at their centre lines, parallel traces by the finite-section bar kernel;
/// a barrel and a trace at right angles do not couple.</para>
///
/// <para>The network is reduced once to the nodes the rest of the circuit sees (pads, and the
/// feet of vias that go on through the plate to another one): the currents into them are
/// Γ·V/(jω), with Γ frequency independent.</para>
/// </summary>
public sealed class MountingNetwork
{
    /// <summary>The plate's surface: the network's reference node.</summary>
    public const int Plate = -1;

    private readonly List<string> _nodes = new();
    private readonly List<(TraceSegment3D Segment, int From, int To)> _branches = new();

    public IReadOnlyList<string> Nodes => _nodes;
    public int BranchCount => _branches.Count;

    public int AddNode(string name)
    {
        _nodes.Add(name);
        return _nodes.Count - 1;
    }

    /// <summary>A via barrel from a node at <paramref name="height"/> down to its foot on the plate
    /// (<paramref name="foot"/>: <see cref="Plate"/> where the via joins the plate, or a node that
    /// stands for the via's continuation through the plate).</summary>
    public void AddBarrel(Point2 at, double diameter, double height, int top, int foot)
    {
        if (!(height > 0)) throw new ArgumentException("A barrel above the plate needs a height.");
        if (!(diameter > 0)) throw new ArgumentException("A barrel needs a diameter.");
        _branches.Add((new TraceSegment3D(new Vector3D(at.X, at.Y, height), new Vector3D(at.X, at.Y, 0), diameter, 0,
            SegmentProfile.RoundTube), top, foot));
    }

    /// <summary>A trace at <paramref name="height"/> (its copper's middle) from node a to node b.</summary>
    public void AddTrace(Point2 from, Point2 to, double height, double width, double thickness, int a, int b)
    {
        if (!((to - from).Length > 0)) throw new ArgumentException("A trace needs a length.");
        _branches.Add((new TraceSegment3D(new Vector3D(from.X, from.Y, height), new Vector3D(to.X, to.Y, height), width, thickness), a, b));
    }

    private static bool StandsOnPlate(TraceSegment3D s) =>
        s.Profile != SegmentProfile.Bar && s.Start.X == s.End.X && s.Start.Y == s.End.Y && (s.Start.Z == 0 || s.End.Z == 0);

    private static TraceSegment3D Mirror(TraceSegment3D s) =>
        s with { Start = new Vector3D(s.Start.X, s.Start.Y, -s.Start.Z), End = new Vector3D(s.End.X, s.End.Y, -s.End.Z) };

    /// <summary>The partial-inductance matrix of the branches with the plate's images [H].</summary>
    public double[,] InductanceMatrix()
    {
        int n = _branches.Count;
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            var si = _branches[i].Segment;
            if (StandsOnPlate(si))
            {
                // The barrel and its image are one tube twice as tall.
                var top = si.Start.Z > 0 ? si.Start : si.End;
                var doubled = new TraceSegment3D(top, new Vector3D(top.X, top.Y, -top.Z), si.Width, si.Thickness, si.Profile);
                l[i, i] = 0.5 * LoopComposer.PartialSelf(doubled);
            }
            else
                l[i, i] = LoopComposer.PartialSelf(si) - LoopComposer.PartialMutual(si, Mirror(si));
            for (int j = i + 1; j < n; j++)
            {
                var sj = _branches[j].Segment;
                double m = LoopComposer.PartialMutual(si, sj) - LoopComposer.PartialMutual(si, Mirror(sj));
                double back = LoopComposer.PartialMutual(sj, si) - LoopComposer.PartialMutual(sj, Mirror(si));
                l[i, j] = l[j, i] = 0.5 * (m + back);
            }
        }
        return l;
    }

    /// <summary>
    /// Γ reduced to <paramref name="terminals"/> (node indices): the currents into the
    /// terminals are Γ·V/(jω) with every other node free. Γ = A·L⁻¹·Aᵀ over the nodes, with the
    /// plate held at zero, then the free nodes eliminated.
    /// </summary>
    public double[,] Reduce(IReadOnlyList<int> terminals)
    {
        int s = _branches.Count, n = _nodes.Count;
        if (s == 0) return new double[terminals.Count, terminals.Count];
        var inverse = InvertPositiveDefinite(InductanceMatrix());

        // Γ over all nodes: branch k runs From → To; Γ = A·L⁻¹·Aᵀ with A[From,k] = 1, A[To,k] = −1.
        var gamma = new double[n, n];
        for (int a = 0; a < s; a++)
        {
            var (_, fa, ta) = _branches[a];
            for (int b = 0; b < s; b++)
            {
                double v = inverse[a, b];
                if (v == 0) continue;
                var (_, fb, tb) = _branches[b];
                if (fa >= 0 && fb >= 0) gamma[fa, fb] += v;
                if (fa >= 0 && tb >= 0) gamma[fa, tb] -= v;
                if (ta >= 0 && fb >= 0) gamma[ta, fb] -= v;
                if (ta >= 0 && tb >= 0) gamma[ta, tb] += v;
            }
        }

        var isTerminal = new bool[n];
        foreach (int t in terminals) isTerminal[t] = true;
        var free = Enumerable.Range(0, n).Where(i => !isTerminal[i]).ToList();
        int t0 = terminals.Count, f0 = free.Count;
        var result = new double[t0, t0];
        for (int i = 0; i < t0; i++)
            for (int j = 0; j < t0; j++) result[i, j] = gamma[terminals[i], terminals[j]];
        if (f0 == 0) return result;

        // Schur complement: Γ_TT − Γ_TF·Γ_FF⁻¹·Γ_FT.
        var ff = new double[f0 * f0];
        for (int i = 0; i < f0; i++)
            for (int j = 0; j < f0; j++) ff[i * f0 + j] = gamma[free[i], free[j]];
        var pivot = new int[f0];
        var lu = DenseLu.FactorInPlace(ff, f0, pivot);
        var column = new double[f0];
        var solved = new double[f0];
        for (int j = 0; j < t0; j++)
        {
            for (int i = 0; i < f0; i++) column[i] = gamma[free[i], terminals[j]];
            lu.Solve(column, solved);
            for (int i = 0; i < t0; i++)
            {
                double sum = 0;
                for (int k = 0; k < f0; k++) sum += gamma[terminals[i], free[k]] * solved[k];
                result[i, j] -= sum;
            }
        }
        for (int i = 0; i < t0; i++)
            for (int j = i + 1; j < t0; j++)
                result[i, j] = result[j, i] = 0.5 * (result[i, j] + result[j, i]);
        return result;
    }

    /// <summary>The inductance between two nodes [H] — or between a node and the plate, with
    /// <paramref name="b"/> = <see cref="Plate"/> — with every other node left free.</summary>
    public double LoopInductance(int a, int b)
    {
        var terminals = b == Plate ? new[] { a } : new[] { a, b };
        var gamma = Reduce(terminals);
        if (b == Plate) return 1 / gamma[0, 0];
        // Inductance matrix of the two terminals against the plate, then the loop between them.
        double det = gamma[0, 0] * gamma[1, 1] - gamma[0, 1] * gamma[1, 0];
        return (gamma[0, 0] + gamma[1, 1] + gamma[0, 1] + gamma[1, 0]) / det;
    }

    private static double[,] InvertPositiveDefinite(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i, j];
                for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (!(sum > 0))
                        throw new InvalidOperationException(
                            "The partial-inductance matrix of the mounting is not positive definite: the conductors " +
                            "overlap or touch in a way the partial-inductance kernels cannot represent.");
                    l[i, i] = Math.Sqrt(sum);
                }
                else l[i, j] = sum / l[j, j];
            }
        // L⁻¹ = L⁻ᵀ·L⁻¹, column by column.
        var inverse = new double[n, n];
        var e = new double[n];
        for (int c = 0; c < n; c++)
        {
            Array.Clear(e);
            e[c] = 1;
            for (int i = 0; i < n; i++)
            {
                double sum = e[i];
                for (int k = 0; k < i; k++) sum -= l[i, k] * e[k];
                e[i] = sum / l[i, i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = e[i];
                for (int k = i + 1; k < n; k++) sum -= l[k, i] * e[k];
                e[i] = sum / l[i, i];
            }
            for (int i = 0; i < n; i++) inverse[i, c] = e[i];
        }
        return inverse;
    }
}
