using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>One half of a triangular current basis: the rooftop leg that lives on a single
/// element, together with the role it plays there and the coefficient it carries.
///
/// <para><b>Rising</b> means the current amplitude runs 0 → 1 along the ELEMENT's own
/// direction (so the basis peaks at the element's B node); <b>falling</b> is 1 → 0 (the basis
/// peaks at its A node). The shape is fixed by which end the basis peaks at — the only freedom
/// left is <see cref="Sign"/>, and that is what makes a multi-wire junction expressible: at a
/// node where several elements meet, the incident elements do not all point the same way, so
/// current conservation needs the ±1 that the two-element rooftop never did (its two halves are
/// always +1, which is why every pre-junction path is bitwise unchanged).</para></summary>
public readonly record struct WireBasisHalf(int Element, bool Rising, double Sign);

/// <summary>
/// A discretized wire structure ready for the moment-method solve: nodes, an explicit element
/// table (element e runs <c>Elements[e].A → Elements[e].B</c>), and one triangular (rooftop)
/// current basis per interior node — per every node on a loop.
///
/// <para>The basis peaking at a node carries value 1 there and falls linearly to 0 at both
/// neighbours, so current continuity holds by construction and open wire ends carry exactly zero
/// current (no basis peaks there) — UNLESS that end is grounded or attached to a sheet: such an
/// end gets a basis peaking at the end node with only one real supporting element (the half
/// rooftop). A grounded end's other half lives on the image current and enters through the
/// solver's image pass; an attached end's other half is carried across the contact by the
/// junction's 1/ρ disc. That is the monopole base, and the wire↔surface attachment.</para>
///
/// <para><b>Topology.</b> A chain or a loop is the ordered case: element e runs node e → node
/// e+1 (wrapping on a loop). A BRANCHED structure — three or more wires meeting at a node — is
/// the general case, and it is expressed through the same two tables, so nothing downstream
/// distinguishes them. The rule that generates the bases is uniform over both:</para>
/// <code>
///   a node carries (degree − 1) bases when it is free, and (degree) when it is
///   grounded or attached — because there the current does not have to close.
/// </code>
/// <para>Degree 1 free ⇒ 0 bases (an open end carries no current); degree 1 grounded ⇒ 1 (the
/// monopole base); degree 2 free ⇒ 1 (the ordinary rooftop); degree N free ⇒ N−1, each pairing
/// one incident element against a common reference element so that Kirchhoff's current law holds
/// identically, for any coefficients, with no constraint equation to enforce.</para>
/// </summary>
public sealed class WireStructure
{
    private readonly (int A, int B)[] _elements;
    private readonly WireBasisHalf[][] _basisHalves;
    private readonly int[] _basisNodes;

    /// <summary>The ordered (chain or loop) construction: element e runs node e → node e+1,
    /// wrapping on a loop, and one rooftop basis peaks at every interior node (plus a half
    /// rooftop at a grounded/attached end). Element and basis tables are DERIVED here, so this
    /// path produces exactly the enumeration order it always did.</summary>
    internal WireStructure(IReadOnlyList<Vector3D> nodes, IReadOnlyList<double> elementRadii, bool isLoop,
        GroundPlane? ground = null, bool startGrounded = false, bool endGrounded = false)
    {
        Nodes = nodes;
        ElementRadii = elementRadii;
        IsLoop = isLoop;
        Ground = ground;
        StartGrounded = startGrounded;
        EndGrounded = endGrounded;

        int elementCount = isLoop ? nodes.Count : nodes.Count - 1;
        _elements = new (int, int)[elementCount];
        for (int e = 0; e < elementCount; e++) _elements[e] = (e, (e + 1) % nodes.Count);

        int basisCount = isLoop
            ? nodes.Count
            : nodes.Count - 2 + (startGrounded ? 1 : 0) + (endGrounded ? 1 : 0);
        _basisNodes = new int[basisCount];
        _basisHalves = new WireBasisHalf[basisCount][];
        for (int b = 0; b < basisCount; b++)
        {
            int node = isLoop ? b : b + (startGrounded ? 0 : 1);
            _basisNodes[b] = node;
            int rising = isLoop ? (b - 1 + nodes.Count) % nodes.Count : node == 0 ? -1 : node - 1;
            int falling = isLoop ? b : node == nodes.Count - 1 ? -1 : node;
            var halves = new List<WireBasisHalf>(2);
            if (rising >= 0) halves.Add(new WireBasisHalf(rising, true, 1.0));
            if (falling >= 0) halves.Add(new WireBasisHalf(falling, false, 1.0));
            _basisHalves[b] = halves.ToArray();
        }
    }

    /// <summary>The general (branched) construction: an explicit element table and an explicit
    /// per-basis half list. Used by <see cref="WireGridBuilder"/> when some node has three or
    /// more incident elements — the case the ordered constructor cannot express.</summary>
    internal WireStructure(IReadOnlyList<Vector3D> nodes, IReadOnlyList<(int A, int B)> elements,
        IReadOnlyList<double> elementRadii, IReadOnlyList<WireBasisHalf[]> basisHalves,
        IReadOnlyList<int> basisNodes, GroundPlane? ground = null)
    {
        Nodes = nodes;
        ElementRadii = elementRadii;
        IsLoop = false;
        Ground = ground;
        StartGrounded = false;
        EndGrounded = false;
        _elements = elements.ToArray();
        _basisHalves = basisHalves.ToArray();
        _basisNodes = basisNodes.ToArray();
        IsBranched = true;
    }

    /// <summary>The infinite PEC plane the solve images against, or null for free space.</summary>
    public GroundPlane? Ground { get; }

    /// <summary>Whether the first node of an open run sits exactly on the ground plane (or is
    /// the sheet attachment). Only meaningful for the ordered chain construction.</summary>
    public bool StartGrounded { get; }

    /// <summary>Whether the last node of an open run sits exactly on the ground plane (or is
    /// the sheet attachment). Only meaningful for the ordered chain construction.</summary>
    public bool EndGrounded { get; }

    /// <summary>True when the structure carries a node where three or more elements meet — the
    /// case whose bases need <see cref="BasisHalves"/> rather than the ordered accessors.</summary>
    public bool IsBranched { get; }

    /// <summary>Node positions. For a chain or loop these are in wire order; for a branched
    /// structure the order is the builder's deterministic branch walk.</summary>
    public IReadOnlyList<Vector3D> Nodes { get; }

    /// <summary>Per-element wire radius [m], same indexing as elements.</summary>
    public IReadOnlyList<double> ElementRadii { get; }

    public bool IsLoop { get; }

    /// <summary>The element table: element e runs <c>Nodes[Elements[e].A]</c> →
    /// <c>Nodes[Elements[e].B]</c>.</summary>
    public IReadOnlyList<(int A, int B)> Elements => _elements;

    public int ElementCount => _elements.Length;

    /// <summary>Number of current unknowns.</summary>
    public int BasisCount => _basisHalves.Length;

    /// <summary>The (at most two) element legs a basis is built from. This is the general view
    /// every assembler should use: it carries the ± that a junction basis needs and that an
    /// ordinary rooftop never does.</summary>
    public IReadOnlyList<WireBasisHalf> BasisHalves(int basis) => _basisHalves[basis];

    public Vector3D ElementStart(int element) => Nodes[_elements[element].A];

    public Vector3D ElementEnd(int element) => Nodes[_elements[element].B];

    public double ElementLength(int element) => (ElementEnd(element) - ElementStart(element)).Length;

    public Vector3D ElementDirection(int element) =>
        (ElementEnd(element) - ElementStart(element)).Normalized();

    /// <summary>The node index a basis peaks at.</summary>
    public int BasisNode(int basis) => _basisNodes[basis];

    /// <summary>The element on which the basis RISES 0 → 1 (ends at the basis node), or −1 when
    /// it has no rising leg (a start-grounded basis, whose rising half lives on the image
    /// current). The UNSIGNED view: on a branched structure a junction basis's legs may carry a
    /// −1, so assemblers must read <see cref="BasisHalves"/> instead.</summary>
    public int RisingElement(int basis)
    {
        foreach (var half in _basisHalves[basis]) if (half.Rising) return half.Element;
        return -1;
    }

    /// <summary>The element on which the basis FALLS 1 → 0 (starts at the basis node), or −1
    /// when it has no falling leg. The UNSIGNED view — see <see cref="RisingElement"/>.</summary>
    public int FallingElement(int basis)
    {
        foreach (var half in _basisHalves[basis]) if (!half.Rising) return half.Element;
        return -1;
    }

    /// <summary>The basis whose peak node lies nearest <paramref name="point"/> — how a feed
    /// location request (a pad position, a wire midpoint) resolves to an unknown.</summary>
    public int NearestBasis(Vector3D point)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int b = 0; b < BasisCount; b++)
        {
            double distance = (Nodes[BasisNode(b)] - point).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = b;
            }
        }
        return best;
    }

    /// <summary>Total wire length [m].</summary>
    public double TotalLength()
    {
        double sum = 0;
        for (int e = 0; e < ElementCount; e++) sum += ElementLength(e);
        return sum;
    }
}

/// <summary>Either a discretized structure or the specific reason none could be built,
/// plus accuracy warnings that must reach the user (never silently degraded).</summary>
public sealed record WireGridResult(WireStructure? Structure, string? FailureReason)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static WireGridResult Success(WireStructure structure) => new(structure, null);
    public static WireGridResult Failure(string reason) => new(null, reason);
}
