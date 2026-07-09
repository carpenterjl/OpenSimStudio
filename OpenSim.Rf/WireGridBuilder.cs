using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// Turns raw wire segments into a solvable <see cref="WireStructure"/>: clusters
/// endpoints into nodes (snapping to the cluster mean), validates the topology (one
/// open path or one closed loop — multi-wire junctions are a typed failure in v1),
/// merges near-collinear slivers (arc tessellation feeds many sub-millimetre chords
/// that would otherwise become elements shorter than the wire radius), and splits every
/// run to the requested maximum element length (λ/10 is the customary MoM ceiling).
/// Splitting is capped so elements never go below ~2 radii, where the reduced thin-wire
/// kernel loses validity — a cap that bites produces a WARNING, never silence.
/// </summary>
public static class WireGridBuilder
{
    /// <summary>Elements shorter than this multiple of the wire radius degrade the
    /// reduced-kernel accuracy; splitting stops there and a warning is raised.</summary>
    private const double MinElementRadiusRatio = 2.0;

    /// <summary>Consecutive collinear slivers merge until the accumulated turn exceeds
    /// this angle [rad] (~2°) — small enough that geometry stays faithful at λ/10 scale.</summary>
    private const double MaxMergeTurn = 0.035;

    public static WireGridResult Build(IReadOnlyList<WireSegment> wires, double maxElementLength,
        int maxUnknowns = 2000)
    {
        if (wires.Count == 0)
            return WireGridResult.Failure("no wire segments to discretize");
        if (maxElementLength <= 0)
            return WireGridResult.Failure("the maximum element length must be positive");
        foreach (var wire in wires)
            if (wire.Radius <= 0 || wire.Length <= 0)
                return WireGridResult.Failure("every wire segment needs a positive length and radius");

        // ---- Cluster endpoints into nodes (tolerance from the geometry scale). ----
        double scale = wires.Max(w => Math.Max(w.Length, Math.Max(w.A.Length, w.B.Length)));
        double tolerance = Math.Max(scale * 1e-9, 1e-12);

        var nodePositions = new List<Vector3D>();
        var nodeCounts = new List<int>();
        int NodeOf(Vector3D p)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < nodePositions.Count; i++)
            {
                double distance = (nodePositions[i] - p).Length;
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            if (best >= 0)
            {
                // Snap to the running mean so the node is one exact shared point.
                nodePositions[best] = (nodePositions[best] * nodeCounts[best] + p) / (nodeCounts[best] + 1);
                nodeCounts[best]++;
                return best;
            }
            nodePositions.Add(p);
            nodeCounts.Add(1);
            return nodePositions.Count - 1;
        }

        var edges = new (int A, int B, double Radius)[wires.Count];
        var degree = new Dictionary<int, List<int>>();
        for (int i = 0; i < wires.Count; i++)
        {
            int a = NodeOf(wires[i].A);
            int b = NodeOf(wires[i].B);
            if (a == b)
                return WireGridResult.Failure(
                    $"wire segment {i} is degenerate (both endpoints coincide within tolerance)");
            edges[i] = (a, b, wires[i].Radius);
            (degree.TryGetValue(a, out var la) ? la : degree[a] = new List<int>()).Add(i);
            (degree.TryGetValue(b, out var lb) ? lb : degree[b] = new List<int>()).Add(i);
        }

        // ---- Topology: exactly one open path or one closed loop. ----
        foreach (var (node, incident) in degree)
            if (incident.Count > 2)
                return WireGridResult.Failure(
                    $"{incident.Count} wires meet at ({nodePositions[node].X:g4}, " +
                    $"{nodePositions[node].Y:g4}, {nodePositions[node].Z:g4}) — multi-wire " +
                    "junctions are not supported yet (chains, bends, and loops only)");

        var endpoints = degree.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        bool isLoop = endpoints.Count == 0;
        if (endpoints.Count > 2)
            return WireGridResult.Failure(
                $"the wires form {endpoints.Count / 2} disconnected pieces — one connected run is required");

        // Deterministic walk start: the lexicographically smallest endpoint (or node 0
        // for a loop — input order is already deterministic).
        int start = isLoop
            ? 0
            : endpoints.OrderBy(n => nodePositions[n].X)
                .ThenBy(n => nodePositions[n].Y)
                .ThenBy(n => nodePositions[n].Z).First();

        var orderedNodes = new List<Vector3D> { nodePositions[start] };
        var orderedRadii = new List<double>();
        var used = new bool[wires.Count];
        int current = start;
        for (int step = 0; step < wires.Count; step++)
        {
            int next = degree[current].FirstOrDefault(e => !used[e], -1);
            if (next < 0)
                return WireGridResult.Failure("the wires form disconnected pieces — one connected run is required");
            used[next] = true;
            current = edges[next].A == current ? edges[next].B : edges[next].A;
            orderedRadii.Add(edges[next].Radius);
            if (!(isLoop && step == wires.Count - 1))     // a loop's last edge returns to the start
                orderedNodes.Add(nodePositions[current]);
        }
        if (isLoop && current != start)
            return WireGridResult.Failure("the wires form disconnected pieces — one connected run is required");

        // ---- Merge near-collinear slivers (equal radius only). ----
        var warnings = new List<string>();
        MergeSlivers(orderedNodes, orderedRadii, isLoop, maxElementLength);

        // ---- Split to the element-length ceiling, floored at ~2 radii. ----
        var nodes = new List<Vector3D>();
        var radii = new List<double>();
        bool tooShortForKernel = false;
        int runCount = orderedRadii.Count;
        for (int e = 0; e < runCount; e++)
        {
            var a = orderedNodes[e];
            var b = orderedNodes[(e + 1) % orderedNodes.Count];
            double length = (b - a).Length;
            double radius = orderedRadii[e];

            int pieces = Math.Max(1, (int)Math.Ceiling(length / maxElementLength));
            int kernelCap = Math.Max(1, (int)Math.Floor(length / (MinElementRadiusRatio * radius)));
            pieces = Math.Min(pieces, kernelCap);
            if (length / pieces < MinElementRadiusRatio * radius) tooShortForKernel = true;

            nodes.Add(a);
            for (int p = 1; p < pieces; p++)
            {
                nodes.Add(a + (b - a) * ((double)p / pieces));
                radii.Add(radius);
            }
            radii.Add(radius);
        }
        if (!isLoop) nodes.Add(orderedNodes[^1]);

        if (tooShortForKernel)
            warnings.Add("Some elements are shorter than twice their wire radius; the thin-wire " +
                         "kernel loses accuracy there (typical for very short, wide traces).");

        int basisCount = isLoop ? nodes.Count : nodes.Count - 2;
        if (basisCount < 1)
            return WireGridResult.Failure(
                "the discretized wire has no interior nodes — it is too short relative to " +
                "its radius to carry a thin-wire current basis");
        if (basisCount > maxUnknowns)
            return WireGridResult.Failure(
                $"{basisCount} current unknowns exceed the {maxUnknowns} cap (dense LU is O(N³)) — " +
                "raise the maximum element length or lower the frequency");

        return WireGridResult.Success(new WireStructure(nodes, radii, isLoop)) with
        {
            Warnings = warnings
        };
    }

    /// <summary>Merges consecutive runs whose accumulated turn stays under
    /// <see cref="MaxMergeTurn"/> and whose combined length stays under half the element
    /// ceiling — arc-tessellation chords become one straight element instead of dozens of
    /// sub-radius slivers. Merging never crosses a radius change.</summary>
    private static void MergeSlivers(List<Vector3D> nodes, List<double> radii, bool isLoop,
        double maxElementLength)
    {
        // Interior node i sits between run (i−1) and run (i); it may be removed when
        // directions agree, radii match, and the merged run stays short. Loops keep
        // node 0 as an anchor so indexing stays simple (one uncollapsed node is free).
        for (int i = nodes.Count - 2; i >= 1; i--)
        {
            if (i >= nodes.Count - (isLoop ? 0 : 1)) continue;
            var previous = nodes[i] - nodes[i - 1];
            var next = nodes[(i + 1) % nodes.Count] - nodes[i];
            if (radii[i - 1] != radii[i]) continue;
            double turn = Math.Acos(Math.Clamp(
                Vector3D.Dot(previous.Normalized(), next.Normalized()), -1, 1));
            if (turn > MaxMergeTurn) continue;
            if (previous.Length + next.Length > 0.5 * maxElementLength) continue;
            nodes.RemoveAt(i);
            radii.RemoveAt(i);
        }
    }
}
