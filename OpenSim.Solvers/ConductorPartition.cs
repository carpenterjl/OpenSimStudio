using OpenSim.Core.Model;

namespace OpenSim.Solvers;

/// <summary>
/// Which elements of an electrical mesh are conductors and which are insulators, which
/// conductor pieces reach a voltage, and whether every injected current has somewhere to
/// go — shared by the DC and the AC solver, which stage conductors and insulators the
/// same way (see <see cref="ElectricalConductionSolver"/>).
/// <para>
/// A conductor piece is what conductor elements connect, following an electrical contact
/// between bodies as well: two copper bars pressed together are one piece, so a voltage on
/// one anchors the other. A contact stamp is followed only when every node it couples
/// belongs to a conductor element — a joint against an insulator body carries nothing
/// worth connecting through.
/// </para>
/// </summary>
internal sealed class ConductorPartition
{
    /// <summary>The largest conductivity ratio solved as ONE system. An element further
    /// than this below the best conductor is an insulator and is solved in the second
    /// stage.</summary>
    public const double SpreadLimit = 1e8;

    /// <summary>σ per element as given.</summary>
    public required double[] Sigma { get; init; }

    /// <summary>Element is a conductor (within <see cref="SpreadLimit"/> of the best one).</summary>
    public required bool[] Conductor { get; init; }

    /// <summary>Element belongs to a conductor that reaches a voltage (stage one).</summary>
    public required bool[] Anchored { get; init; }

    /// <summary>Node belongs to at least one stage-one element.</summary>
    public required bool[] NodeAnchored { get; init; }

    /// <summary>The conductor piece of each node (a representative node index);
    /// meaningful where <see cref="NodeAnchored"/> is set.</summary>
    public required int[] Piece { get; init; }

    /// <summary>True when some element is an insulator, so the solve is staged.</summary>
    public required bool Staged { get; init; }

    public required int InsulatorElements { get; init; }
    public required double InsulatorMaxSigma { get; init; }

    /// <summary>Mesh regions holding conductor that reaches no voltage.</summary>
    public required IReadOnlyList<int> FloatingRegions { get; init; }

    /// <summary>Electrical contacts whose stamps couple stage-one nodes only.</summary>
    public required IReadOnlyList<ContactInterface>? StageOneContacts { get; init; }

    /// <summary>The remaining electrical contacts (an insulator body, a floating piece).</summary>
    public required IReadOnlyList<ContactInterface>? StageTwoContacts { get; init; }

    public static ConductorPartition Of(FeMesh mesh, Func<int, double> conductivity,
        IReadOnlyList<BoundaryCondition> conditions, IReadOnlyList<ContactInterface>? contacts)
    {
        int elements = mesh.ElementCount;
        var sigma = new double[elements];
        double max = 0;
        for (int e = 0; e < elements; e++)
        {
            sigma[e] = conductivity(e);
            max = Math.Max(max, sigma[e]);
        }

        var conductor = new bool[elements];
        var conductorNode = new bool[mesh.NodeCount];
        int insulators = 0;
        double insulatorMax = 0;
        for (int e = 0; e < elements; e++)
        {
            conductor[e] = sigma[e] * SpreadLimit >= max;
            if (!conductor[e])
            {
                insulators++;
                insulatorMax = Math.Max(insulatorMax, sigma[e]);
                continue;
            }
            var el = mesh.Elements[e];
            conductorNode[el.N0] = conductorNode[el.N1] = conductorNode[el.N2] = conductorNode[el.N3] = true;
        }

        // Conductor pieces: connected through conductor elements, and through a contact
        // whose stamps couple conductor nodes only.
        var parent = new int[mesh.NodeCount];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        for (int e = 0; e < elements; e++)
        {
            if (!conductor[e]) continue;
            var el = mesh.Elements[e];
            Union(parent, el.N0, el.N1);
            Union(parent, el.N0, el.N2);
            Union(parent, el.N0, el.N3);
        }
        if (contacts is not null)
            foreach (var contact in contacts)
                foreach (var s in contact.Stamps)
                {
                    if (!(conductorNode[s.Node0] && conductorNode[s.Node1]
                          && conductorNode[s.Node2] && conductorNode[s.Node3])) continue;
                    Union(parent, s.Node0, s.Node1);
                    Union(parent, s.Node0, s.Node2);
                    Union(parent, s.Node0, s.Node3);
                }

        var anchoredRoots = new HashSet<int>();
        foreach (var voltage in conditions.OfType<VoltagePotential>())
            foreach (int node in mesh.GetScopeNodes(voltage))
                anchoredRoots.Add(Find(parent, node));

        var anchored = new bool[elements];
        var nodeAnchored = new bool[mesh.NodeCount];
        var nodeFloating = new bool[mesh.NodeCount];
        var floatingRegions = new SortedSet<int>();
        for (int e = 0; e < elements; e++)
        {
            if (!conductor[e]) continue;
            var el = mesh.Elements[e];
            anchored[e] = anchoredRoots.Contains(Find(parent, el.N0));
            var flags = anchored[e] ? nodeAnchored : nodeFloating;
            flags[el.N0] = flags[el.N1] = flags[el.N2] = flags[el.N3] = true;
            if (!anchored[e]) floatingRegions.Add(mesh.RegionOf(e));
        }

        // An injected current must land on a conductor that reaches a voltage. Anywhere
        // else it has no path: on an isolated conductor the system is singular with a
        // non-zero load (the iteration used to run to its cap and fail without saying
        // why), and on an insulator it asks 10⁻¹⁴ S/m to carry amperes.
        foreach (var current in conditions.OfType<CurrentFlow>())
        {
            if (current.TotalCurrent == 0) continue;
            foreach (var t in mesh.GetFaceTriangles(current.FaceIds))
            {
                if (nodeAnchored[t.A] && nodeAnchored[t.B] && nodeAnchored[t.C]) continue;
                bool isolated = nodeFloating[t.A] || nodeFloating[t.B] || nodeFloating[t.C];
                throw new InvalidOperationException(isolated
                    ? $"Current '{current.Name}' is injected into a conductor that reaches no " +
                      "voltage potential, so the current has nowhere to go and the potential " +
                      "there has no level. Put a voltage potential on that conductor, or move " +
                      "the current to one that has it."
                    : $"Current '{current.Name}' is injected into an insulator (its conductivity " +
                      $"is below 1/{SpreadLimit:G1} of the best conductor in the " +
                      "mesh). Inject it on a conductor face.");
            }
        }

        List<ContactInterface>? stageOne = null, stageTwo = null;
        if (contacts is not null && insulators > 0)
        {
            stageOne = new List<ContactInterface>();
            stageTwo = new List<ContactInterface>();
            foreach (var contact in contacts)
            {
                var first = new List<ContactStamp>();
                var second = new List<ContactStamp>();
                foreach (var s in contact.Stamps)
                    (nodeAnchored[s.Node0] && nodeAnchored[s.Node1] && nodeAnchored[s.Node2] && nodeAnchored[s.Node3]
                        ? first : second).Add(s);
                if (first.Count > 0) stageOne.Add(contact with { Stamps = first });
                if (second.Count > 0) stageTwo.Add(contact with { Stamps = second });
            }
        }

        return new ConductorPartition
        {
            Sigma = sigma,
            Conductor = conductor,
            Anchored = anchored,
            NodeAnchored = nodeAnchored,
            Piece = Enumerable.Range(0, parent.Length).Select(n => Find(parent, n)).ToArray(),
            Staged = insulators > 0,
            InsulatorElements = insulators,
            InsulatorMaxSigma = insulatorMax,
            FloatingRegions = floatingRegions.ToList(),
            StageOneContacts = insulators > 0 ? stageOne : contacts,
            StageTwoContacts = stageTwo
        };
    }

    /// <summary>
    /// The conductor pieces that are DRIVEN — held at two different potentials, or carrying an
    /// injected current — against those held at one potential with nothing injected. The
    /// latter carry no conduction current at all: their potential is that voltage exactly
    /// and their reactions are exactly zero, which matters when two such pieces face each
    /// other across an insulator and the insulator's leakage is the whole answer.
    /// </summary>
    public (Dictionary<int, double> PieceVolts, HashSet<int> Driven) SnapPieces(
        IReadOnlyDictionary<int, double> prescribed, Func<int, bool> loaded)
    {
        var pieceVolts = new Dictionary<int, double>();
        var driven = new HashSet<int>();
        foreach (var (node, value) in prescribed)
        {
            if (!NodeAnchored[node]) continue;
            int piece = Piece[node];
            if (pieceVolts.TryGetValue(piece, out double seen) && seen != value) driven.Add(piece);
            pieceVolts[piece] = value;
        }
        for (int n = 0; n < NodeAnchored.Length; n++)
            if (NodeAnchored[n] && loaded(n)) driven.Add(Piece[n]);
        return (pieceVolts, driven);
    }

    /// <summary>
    /// Which electrodes own each anchored piece: the current an insulator draws from a
    /// piece's surface is supplied through the piece by its electrode(s), shared equally
    /// when one piece carries several.
    /// </summary>
    public Dictionary<int, List<int>> ElectrodesOfPieces(FeMesh mesh, IReadOnlyList<VoltagePotential> electrodes)
    {
        var owners = new Dictionary<int, List<int>>();
        for (int i = 0; i < electrodes.Count; i++)
        {
            var pieces = mesh.GetScopeNodes(electrodes[i])
                .Where(n => NodeAnchored[n]).Select(n => Piece[n]).Distinct();
            foreach (int piece in pieces)
            {
                if (!owners.TryGetValue(piece, out var list))
                    owners[piece] = list = new List<int>();
                list.Add(i);
            }
        }
        return owners;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }
}
