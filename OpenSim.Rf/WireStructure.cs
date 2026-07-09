using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// A discretized single-wire structure ready for the moment-method solve: nodes ordered
/// along the wire, one element between consecutive nodes (wrapping for a loop), and one
/// triangular (rooftop) current basis per interior node — per every node on a loop. The
/// basis peaking at a node carries value 1 there and falls linearly to 0 at both
/// neighbours, so current continuity holds by construction and open wire ends carry
/// exactly zero current (no basis peaks there).
/// </summary>
public sealed class WireStructure
{
    internal WireStructure(IReadOnlyList<Vector3D> nodes, IReadOnlyList<double> elementRadii, bool isLoop)
    {
        Nodes = nodes;
        ElementRadii = elementRadii;
        IsLoop = isLoop;
    }

    /// <summary>Node positions in wire order; element e runs Nodes[e] → Nodes[e+1]
    /// (the last element wraps back to Nodes[0] on a loop).</summary>
    public IReadOnlyList<Vector3D> Nodes { get; }

    /// <summary>Per-element wire radius [m], same indexing as elements.</summary>
    public IReadOnlyList<double> ElementRadii { get; }

    public bool IsLoop { get; }

    public int ElementCount => IsLoop ? Nodes.Count : Nodes.Count - 1;

    /// <summary>Number of current unknowns.</summary>
    public int BasisCount => IsLoop ? Nodes.Count : Nodes.Count - 2;

    public Vector3D ElementStart(int element) => Nodes[element];

    public Vector3D ElementEnd(int element) => Nodes[(element + 1) % Nodes.Count];

    public double ElementLength(int element) => (ElementEnd(element) - ElementStart(element)).Length;

    public Vector3D ElementDirection(int element) =>
        (ElementEnd(element) - ElementStart(element)).Normalized();

    /// <summary>The node index a basis peaks at.</summary>
    public int BasisNode(int basis) => IsLoop ? basis : basis + 1;

    /// <summary>The element on which the basis RISES 0 → 1 (ends at the basis node).</summary>
    public int RisingElement(int basis) =>
        IsLoop ? (basis - 1 + Nodes.Count) % Nodes.Count : basis;

    /// <summary>The element on which the basis FALLS 1 → 0 (starts at the basis node).</summary>
    public int FallingElement(int basis) => IsLoop ? basis : basis + 1;

    /// <summary>The basis whose peak node lies nearest <paramref name="point"/> — how a
    /// feed location request (a pad position, a wire midpoint) resolves to an unknown.</summary>
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
