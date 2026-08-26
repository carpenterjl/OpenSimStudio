using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// Turns raw wire segments into a solvable <see cref="WireStructure"/>: clusters
/// endpoints into nodes (snapping to the cluster mean), resolves the topology, merges
/// near-collinear slivers (arc tessellation feeds many sub-millimetre chords that would
/// otherwise become elements shorter than the wire radius), and splits every run to the
/// requested maximum element length (λ/10 is the customary MoM ceiling).
///
/// <para>An ordered chain or closed loop takes the walk below; a BRANCHED structure — three
/// or more wires meeting at a node — takes <c>BuildGeneral</c>. Keeping the ordered walk
/// untouched rather than folding both into one general routine is deliberate: it is what
/// makes every structure that could be built before come out bitwise unchanged, and that
/// identity is gated rather than assumed.</para>
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

    /// <param name="attachmentPoint">Where this wire ENDS on a metal sheet, for the hybrid
    /// wire↔surface solve. The named end is given the half rooftop that the attachment junction
    /// carries across the contact — the same basis a grounded end gets, but with no ground plane
    /// and therefore no image pass: the flag says only "the current does not vanish here", which
    /// is exactly the attachment condition.</param>
    public static WireGridResult Build(IReadOnlyList<WireSegment> wires, double maxElementLength,
        int maxUnknowns = 2000, GroundPlane? ground = null, Vector3D? attachmentPoint = null) =>
        Build(wires, maxElementLength, maxUnknowns, ground, attachmentPoint,
            forceGeneralTopology: false);

    /// <summary>A test seam: take the general (branched) construction even for a chain, so the
    /// two paths can be compared on geometry where BOTH apply. Nothing in the app calls it — its
    /// whole purpose is to let a gate assert that the general machinery reproduces the ordered
    /// one bitwise, which is the only way to know the junction bases really are the rooftop in
    /// disguise.</summary>
    internal static WireGridResult BuildForcingGeneralTopology(IReadOnlyList<WireSegment> wires,
        double maxElementLength, int maxUnknowns = 2000, GroundPlane? ground = null,
        Vector3D? attachmentPoint = null) =>
        Build(wires, maxElementLength, maxUnknowns, ground, attachmentPoint,
            forceGeneralTopology: true);

    private static WireGridResult Build(IReadOnlyList<WireSegment> wires, double maxElementLength,
        int maxUnknowns, GroundPlane? ground, Vector3D? attachmentPoint, bool forceGeneralTopology)
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

        // ---- Topology: an ordered chain or loop is handled here; anything with a node of
        // degree three or more goes to the general construction below. Leaving this walk exactly
        // as it was is what pins every pre-existing structure bitwise — a junction is new
        // physics, not a reason to re-derive the case that already worked.
        bool branched = forceGeneralTopology;
        foreach (var (_, incident) in degree)
            if (incident.Count > 2) branched = true;
        if (branched)
            return BuildGeneral(edges, degree, nodePositions, tolerance,
                maxElementLength, maxUnknowns, ground, attachmentPoint);
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

        // ---- Ground-plane validation: strictly above, except open ENDS on the plane. ----
        // A grounded end is snapped to exactly z0 so its image shares the node bitwise
        // (2·z0 − z0 = z0 exactly), which is what routes the real/image element pair into
        // the shared-corner NEAR quadrature.
        bool startGrounded = false, endGrounded = false;
        if (ground is not null)
        {
            double z0 = ground.SurfaceZ;
            for (int i = 0; i < orderedNodes.Count; i++)
            {
                var node = orderedNodes[i];
                bool onPlane = Math.Abs(node.Z - z0) <= tolerance;
                if (!onPlane && node.Z < z0)
                    return WireGridResult.Failure(
                        $"the wire goes below the ground plane at ({node.X:g4}, {node.Y:g4}, {node.Z:g4}) — " +
                        "image theory needs the structure strictly above it");
                if (!onPlane) continue;
                if (isLoop)
                    return WireGridResult.Failure(
                        $"the loop touches the ground plane at ({node.X:g4}, {node.Y:g4}) — " +
                        "image theory needs a loop strictly above the plane; only an open wire END may be grounded");
                if (i != 0 && i != orderedNodes.Count - 1)
                    return WireGridResult.Failure(
                        $"the wire touches the ground plane mid-run at ({node.X:g4}, {node.Y:g4}) — " +
                        "image theory needs the structure strictly above it; end a wire ON the plane to ground it");
                orderedNodes[i] = new Vector3D(node.X, node.Y, z0);
                if (i == 0) startGrounded = true; else endGrounded = true;
            }
            for (int i = 0; i + 1 < orderedNodes.Count; i++)
                if (orderedNodes[i].Z == z0 && orderedNodes[i + 1].Z == z0)
                    return WireGridResult.Failure(
                        "a wire segment lies IN the ground plane — image theory needs the run " +
                        "strictly above it (only the end point may touch)");
        }

        // ---- Sheet attachment: the named END carries the junction's half hat. ----
        if (attachmentPoint is { } attachAt)
        {
            if (ground is not null)
                return WireGridResult.Failure(
                    "a wire cannot be both attached to a sheet and imaged against a ground plane — " +
                    "the hybrid wire/surface solve is free space");
            if (isLoop)
                return WireGridResult.Failure(
                    "a closed loop has no free end to attach to a sheet");
            double toStart = (orderedNodes[0] - attachAt).Length;
            double toEnd = (orderedNodes[^1] - attachAt).Length;
            if (Math.Min(toStart, toEnd) > tolerance)
                return WireGridResult.Failure(
                    $"the attachment point ({attachAt.X:g4}, {attachAt.Y:g4}, {attachAt.Z:g4}) is not a " +
                    $"wire END (the nearest end is {Math.Min(toStart, toEnd):g3} m away) — a wire meets a " +
                    "sheet at one of its two ends, and a guessed contact is a wrong placement");
            if (toStart <= toEnd) startGrounded = true; else endGrounded = true;
        }

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

        int basisCount = isLoop
            ? nodes.Count
            : nodes.Count - 2 + (startGrounded ? 1 : 0) + (endGrounded ? 1 : 0);
        if (basisCount < 1)
            return WireGridResult.Failure(
                "the discretized wire has no interior nodes — it is too short relative to " +
                "its radius to carry a thin-wire current basis");
        if (basisCount > maxUnknowns)
            return WireGridResult.Failure(
                $"{basisCount} current unknowns exceed the {maxUnknowns} cap (dense LU is O(N³)) — " +
                "raise the maximum element length or lower the frequency");

        return WireGridResult.Success(
            new WireStructure(nodes, radii, isLoop, ground, startGrounded, endGrounded)) with
        {
            Warnings = warnings
        };
    }

    /// <summary>
    /// The general topology: a connected GRAPH of wires, including nodes where three or more
    /// meet. Chains and loops keep the ordered path above — it is the same physics, and leaving
    /// it untouched is what pins every pre-existing structure bitwise; this method is reached
    /// only when some node has degree ≠ 2, or when a caller asks for it explicitly to compare
    /// the two constructions.
    ///
    /// <para><b>The junction bases.</b> A node where N elements meet carries N−1 unknowns, each
    /// pairing one incident element against a common reference element. Writing the pair as
    ///
    /// <code>  basis_k = −σ_k·H_k + σ_ref·H_ref,     σ = +1 when the node is the element's B end</code>
    ///
    /// makes Kirchhoff's current law an IDENTITY rather than a constraint: H_k delivers σ_k of
    /// current into the node per unit coefficient, so the two legs deliver −1 and +1 whatever the
    /// incident elements' drawing directions were. There is no continuity equation to add, no
    /// Lagrange multiplier, and no basis that has to be eliminated afterwards. For the ordinary
    /// two-element node the reference is the element ARRIVING at it (σ_ref = +1) and the pair
    /// (σ_ref, −σ_k) = (+1, +1) — the plain rooftop, with the halves emitted in the same order,
    /// which is why forcing a chain through this path reproduces the ordered path bitwise (a
    /// gate, not a hope).</para>
    ///
    /// <para>An anchored node — a grounded end, or the end attached to a sheet — instead carries
    /// ONE basis PER incident element, because there the current does not have to close: it
    /// leaves through the image plane or across the attachment disc. Degree 1 free ⇒ no basis at
    /// all (an open end carries no current), which is the same rule seen from the other side.</para>
    ///
    /// <para>Scope: a node ON the ground plane must have degree 1. A grounded multi-wire junction
    /// is a typed failure — the image of a junction is a junction, and its bookkeeping is a named
    /// follow-up rather than something to get silently wrong.</para>
    /// </summary>
    private static WireGridResult BuildGeneral(
        (int A, int B, double Radius)[] edges, Dictionary<int, List<int>> degree,
        List<Vector3D> nodePositions, double tolerance, double maxElementLength,
        int maxUnknowns, GroundPlane? ground, Vector3D? attachmentPoint)
    {
        int clusterCount = nodePositions.Count;

        // ---- One connected structure. ----
        var seen = new bool[clusterCount];
        var stack = new Stack<int>();
        stack.Push(edges[0].A);
        seen[edges[0].A] = true;
        int reached = 1;
        while (stack.Count > 0)
        {
            int v = stack.Pop();
            foreach (int e in degree[v])
            {
                int w = edges[e].A == v ? edges[e].B : edges[e].A;
                if (seen[w]) continue;
                seen[w] = true;
                reached++;
                stack.Push(w);
            }
        }
        if (reached != clusterCount)
            return WireGridResult.Failure(
                "the wires form disconnected pieces — one connected run is required");

        // ---- Anchored nodes: where the current need NOT vanish (grounded, or on a sheet). ----
        var anchored = new HashSet<int>();
        if (ground is not null)
        {
            double z0 = ground.SurfaceZ;
            for (int v = 0; v < clusterCount; v++)
            {
                var p = nodePositions[v];
                bool onPlane = Math.Abs(p.Z - z0) <= tolerance;
                if (!onPlane && p.Z < z0)
                    return WireGridResult.Failure(
                        $"the wire goes below the ground plane at ({p.X:g4}, {p.Y:g4}, {p.Z:g4}) — " +
                        "image theory needs the structure strictly above it");
                if (!onPlane) continue;
                if (degree[v].Count != 1)
                    return WireGridResult.Failure(
                        $"{degree[v].Count} wires meet ON the ground plane at ({p.X:g4}, {p.Y:g4}) — " +
                        "a grounded multi-wire junction is not modeled (the image of a junction is " +
                        "itself a junction, and that bookkeeping is a named follow-up); ground a " +
                        "single wire END instead");
                nodePositions[v] = new Vector3D(p.X, p.Y, z0);
                anchored.Add(v);
            }
        }
        if (attachmentPoint is { } attachAt)
        {
            if (ground is not null)
                return WireGridResult.Failure(
                    "a wire cannot be both attached to a sheet and imaged against a ground plane — " +
                    "the hybrid wire/surface solve is free space");
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int v = 0; v < clusterCount; v++)
            {
                double distance = (nodePositions[v] - attachAt).Length;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = v;
            }
            if (bestDistance > tolerance)
                return WireGridResult.Failure(
                    $"the attachment point ({attachAt.X:g4}, {attachAt.Y:g4}, {attachAt.Z:g4}) is not a " +
                    $"wire node (the nearest is {bestDistance:g3} m away) — a guessed contact is a " +
                    "wrong placement");
            if (degree[best].Count != 1)
                return WireGridResult.Failure(
                    $"{degree[best].Count} wires meet at the attachment point " +
                    $"({attachAt.X:g4}, {attachAt.Y:g4}, {attachAt.Z:g4}) — a wire meets a sheet at a " +
                    "free END; a multi-wire junction standing on the sheet is a named follow-up");
            anchored.Add(best);
        }

        // ---- Branches: maximal chains between nodes of degree ≠ 2. ----
        var special = new List<int>();
        for (int v = 0; v < clusterCount; v++) if (degree[v].Count != 2) special.Add(v);
        if (special.Count == 0)
            return WireGridResult.Failure(
                "every node has exactly two wires — a pure loop takes the ordered path");
        special.Sort((x, y) =>
        {
            var a = nodePositions[x];
            var b = nodePositions[y];
            int c = a.X.CompareTo(b.X);
            if (c != 0) return c;
            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.Z.CompareTo(b.Z);
        });

        var nodes = new List<Vector3D>();
        var elements = new List<(int A, int B)>();
        var radii = new List<double>();
        var globalOf = new Dictionary<int, int>();
        foreach (int s in special)
        {
            globalOf[s] = nodes.Count;
            nodes.Add(nodePositions[s]);
        }

        var warnings = new List<string>();
        bool tooShortForKernel = false;
        var usedEdge = new bool[edges.Length];

        foreach (int s in special)
            foreach (int first in degree[s].OrderBy(e => e))
            {
                if (usedEdge[first]) continue;

                // Walk the branch to the next node of degree ≠ 2, collecting its runs.
                var path = new List<Vector3D> { nodePositions[s] };
                var pathRadii = new List<double>();
                int previous = s, edge = first, endNode;
                while (true)
                {
                    usedEdge[edge] = true;
                    int next = edges[edge].A == previous ? edges[edge].B : edges[edge].A;
                    path.Add(nodePositions[next]);
                    pathRadii.Add(edges[edge].Radius);
                    if (degree[next].Count != 2) { endNode = next; break; }
                    previous = next;
                    edge = degree[next].First(e => !usedEdge[e]);
                }
                if (endNode == s && pathRadii.Count == 1)
                    return WireGridResult.Failure(
                        $"a wire loops from ({path[0].X:g4}, {path[0].Y:g4}, {path[0].Z:g4}) straight " +
                        "back to itself — a single-element self loop carries no current basis");

                MergeSlivers(path, pathRadii, isLoop: false, maxElementLength);

                // Split each run to the element ceiling, floored at ~2 radii (the reduced
                // thin-wire kernel's validity limit — a cap that bites warns, never silences).
                var splitPoints = new List<Vector3D> { path[0] };
                var splitRadii = new List<double>();
                for (int r = 0; r < pathRadii.Count; r++)
                {
                    var a = path[r];
                    var b = path[r + 1];
                    double radius = pathRadii[r];
                    double length = (b - a).Length;
                    int pieces = Math.Max(1, (int)Math.Ceiling(length / maxElementLength));
                    int kernelCap = Math.Max(1, (int)Math.Floor(length / (MinElementRadiusRatio * radius)));
                    pieces = Math.Min(pieces, kernelCap);
                    if (length / pieces < MinElementRadiusRatio * radius) tooShortForKernel = true;
                    for (int p = 1; p < pieces; p++)
                    {
                        splitPoints.Add(a + (b - a) * ((double)p / pieces));
                        splitRadii.Add(radius);
                    }
                    splitPoints.Add(b);
                    splitRadii.Add(radius);
                }

                // The branch's two ends are shared special nodes; everything between is new.
                var ids = new int[splitPoints.Count];
                ids[0] = globalOf[s];
                ids[^1] = globalOf[endNode];
                for (int i = 1; i < splitPoints.Count - 1; i++)
                {
                    nodes.Add(splitPoints[i]);
                    ids[i] = nodes.Count - 1;
                }
                for (int i = 0; i + 1 < splitPoints.Count; i++)
                {
                    elements.Add((ids[i], ids[i + 1]));
                    radii.Add(splitRadii[i]);
                }
            }

        if (usedEdge.Any(u => !u))
            return WireGridResult.Failure(
                "some wires form a closed loop reachable only through itself — attach it to the " +
                "structure at a node, or solve it as a standalone loop");
        if (ground is not null)
            foreach (var (a, b) in elements)
                if (nodes[a].Z == ground.SurfaceZ && nodes[b].Z == ground.SurfaceZ)
                    return WireGridResult.Failure(
                        "a wire segment lies IN the ground plane — image theory needs the run " +
                        "strictly above it (only the end point may touch)");

        // ---- Bases: (degree − 1) per free node, (degree) per anchored one. ----
        var incident = new List<(int Element, bool Rising)>[nodes.Count];
        for (int v = 0; v < nodes.Count; v++) incident[v] = new List<(int, bool)>(2);
        for (int e = 0; e < elements.Count; e++)
        {
            incident[elements[e].A].Add((e, false));   // a basis peaking at A FALLS along it
            incident[elements[e].B].Add((e, true));    // a basis peaking at B RISES into it
        }
        var anchoredGlobal = new HashSet<int>(anchored.Select(v => globalOf[v]));

        var halves = new List<WireBasisHalf[]>();
        var basisNodes = new List<int>();
        for (int v = 0; v < nodes.Count; v++)
        {
            var inc = incident[v];
            if (anchoredGlobal.Contains(v))
            {
                foreach (var (element, rising) in inc)
                {
                    halves.Add(new[] { new WireBasisHalf(element, rising, 1.0) });
                    basisNodes.Add(v);
                }
                continue;
            }
            if (inc.Count < 2) continue;               // a free open end carries no current
            var (referenceElement, referenceRising) = inc[0];
            double referenceSign = referenceRising ? 1.0 : -1.0;
            for (int kIndex = 1; kIndex < inc.Count; kIndex++)
            {
                var (element, rising) = inc[kIndex];
                double sign = rising ? 1.0 : -1.0;
                halves.Add(new[]
                {
                    new WireBasisHalf(referenceElement, referenceRising, referenceSign),
                    new WireBasisHalf(element, rising, -sign)
                });
                basisNodes.Add(v);
            }
        }

        if (tooShortForKernel)
            warnings.Add("Some elements are shorter than twice their wire radius; the thin-wire " +
                         "kernel loses accuracy there (typical for very short, wide traces).");
        if (halves.Count < 1)
            return WireGridResult.Failure(
                "the discretized structure has no interior nodes — it is too short relative to " +
                "its radius to carry a thin-wire current basis");
        if (halves.Count > maxUnknowns)
            return WireGridResult.Failure(
                $"{halves.Count} current unknowns exceed the {maxUnknowns} cap (dense LU is O(N³)) — " +
                "raise the maximum element length or lower the frequency");

        return WireGridResult.Success(
            new WireStructure(nodes, elements, radii, halves, basisNodes, ground)) with
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
