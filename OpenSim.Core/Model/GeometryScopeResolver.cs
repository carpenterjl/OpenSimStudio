using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// Translates a boundary condition's GEOMETRY-space edge and vertex scope into the mesh-space
/// ids the solvers resolve to nodes.
/// <para>
/// The two id spaces exist for one reason: mesh ids are derived from the boundary skin, so
/// remeshing renumbers them and a project can only name them until the element size changes.
/// Geometry ids come from the tessellation and outlive every remesh, so they are what a
/// project stores — and this is where they are turned into something a mesh understands, on
/// a transient copy, at solve time. Nothing is written back: a stored copy of a derived id
/// could only go stale again, which is the problem this exists to end.
/// </para>
/// <para>
/// Every failure is typed and names what could not be matched. An unmatched support is not a
/// cosmetic problem — a static solve with a dropped constraint does not fail, it returns a
/// wrong answer.
/// </para>
/// </summary>
public static class GeometryScopeResolver
{
    /// <summary>
    /// Resolves one condition against the geometry it was scoped on and the mesh it will be
    /// solved on. A condition carrying no geometry scope is returned unchanged — the same
    /// instance — so every existing path is untouched.
    /// </summary>
    public static BoundaryCondition Resolve(BoundaryCondition condition, TriangleMesh? geometry, FeMesh mesh)
    {
        if (!condition.HasGeometryScope) return condition;

        if (geometry is null)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' is scoped to geometry edges or vertices, but " +
                "its body carries no geometry to resolve them against.");

        var features = geometry.FeatureEdges;

        var edgeIds = new List<int>(condition.EdgeIds ?? Array.Empty<int>());
        foreach (int id in condition.GeometryEdgeIds ?? Array.Empty<int>())
            edgeIds.Add(ResolveEdge(condition, id, features, geometry, mesh));

        var vertexIds = new List<int>(condition.VertexIds ?? Array.Empty<int>());
        foreach (int id in condition.GeometryVertexIds ?? Array.Empty<int>())
            vertexIds.Add(ResolveVertex(condition, id, features, geometry, mesh));

        return condition with
        {
            EdgeIds = edgeIds.Count > 0 ? edgeIds : null,
            VertexIds = vertexIds.Count > 0 ? vertexIds : null,
            GeometryEdgeIds = null,
            GeometryVertexIds = null
        };
    }

    /// <summary>Resolves every condition of a body against its own geometry and mesh.</summary>
    public static IReadOnlyList<BoundaryCondition> ResolveForBody(Body body)
    {
        if (body.Mesh is not { } mesh)
            throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
        if (!body.BoundaryConditions.Any(c => c.HasGeometryScope)) return body.BoundaryConditions;

        return body.BoundaryConditions.Select(c => Resolve(c, body.Geometry, mesh)).ToList();
    }

    /// <summary>
    /// Matches a geometric edge to the mesh edge that lies along it.
    /// <para>
    /// The face PAIR is the handle: both meshers carry the geometry's face ids onto the
    /// skin, so a geometric edge and its mesh edge always separate the same two faces. That
    /// alone settles it whenever the pair meets along a single curve. When it meets along
    /// several — a washer's two rims — the candidates are matched to the nearest geometric
    /// component and the match must then pass a SEPARATION test: every node of the mesh edge
    /// must lie closer to its geometric curve than half the distance to the nearest other
    /// candidate curve. That is decisive rather than merely plausible, because the meshers
    /// hug their edges: the right curve reads ~0 and any other reads a geometry-scale
    /// distance. A near-tie is refused rather than guessed.
    /// </para>
    /// </summary>
    private static int ResolveEdge(BoundaryCondition condition, int geometryEdgeId,
        BoundaryEdgeSet features, TriangleMesh geometry, FeMesh mesh)
    {
        if (features.EdgeById(geometryEdgeId) is not { } target)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' names geometry edge {geometryEdgeId}, but this " +
                $"geometry has {features.Edges.Count} feature edge(s). Re-select the scope.");

        var candidates = mesh.Edges.Edges
            .Where(e => e.FaceA == target.FaceA && e.FaceB == target.FaceB)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' names geometry edge {geometryEdgeId} " +
                $"(between faces {target.FaceA} and {target.FaceB}), but the mesh has no edge between " +
                "those faces. Remesh the body, or re-select the scope.");

        // The geometric curves this face pair meets along — the alternatives a match must
        // beat. With only one there is nothing to confuse it with.
        var siblings = features.Edges
            .Where(e => e.FaceA == target.FaceA && e.FaceB == target.FaceB && e.Id != geometryEdgeId)
            .ToList();

        var best = candidates[0];
        double bestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            double distance = MaxNodeDistance(candidate, mesh, target, geometry);
            if (distance < bestDistance) (best, bestDistance) = (candidate, distance);
        }

        if (siblings.Count > 0)
        {
            double nearestOther = siblings.Min(s => MaxNodeDistance(best, mesh, s, geometry));
            if (bestDistance >= 0.5 * nearestOther)
                throw new InvalidOperationException(
                    $"Boundary condition '{condition.Name}' names geometry edge {geometryEdgeId}, but the " +
                    $"mesh edge nearest it ({bestDistance:g3} m away) is no closer than another curve " +
                    $"between faces {target.FaceA} and {target.FaceB} ({nearestOther:g3} m). " +
                    "Re-select the scope on the meshed body.");
        }

        return best.Id;
    }

    private static int ResolveVertex(BoundaryCondition condition, int geometryVertexId,
        BoundaryEdgeSet features, TriangleMesh geometry, FeMesh mesh)
    {
        if (features.VertexById(geometryVertexId) is not { } target)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' names geometry vertex {geometryVertexId}, but this " +
                $"geometry has {features.Vertices.Count} feature vertex/vertices. Re-select the scope.");

        var position = geometry.Vertices[target.NodeId];
        var faces = target.FaceIds.ToHashSet();

        int best = -1;
        double bestDistance = double.MaxValue;
        foreach (var candidate in mesh.Edges.Vertices)
        {
            if (!faces.SetEquals(candidate.FaceIds)) continue;
            double distance = Vector3D.Distance(mesh.Nodes[candidate.NodeId], position);
            if (distance < bestDistance) (best, bestDistance) = (candidate.Id, distance);
        }

        if (best < 0)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' names geometry vertex {geometryVertexId} " +
                $"(where faces {string.Join(", ", target.FaceIds)} meet), but the mesh has no vertex on " +
                "those faces. Remesh the body, or re-select the scope.");
        return best;
    }

    /// <summary>
    /// How far the mesh edge strays from the geometric curve: the largest distance from any
    /// of its nodes to the curve's polyline. The maximum rather than the mean, because a
    /// mesh edge that runs along the right curve for half its length and then leaves it is
    /// not a match.
    /// </summary>
    private static double MaxNodeDistance(BoundaryEdge meshEdge, FeMesh mesh,
        BoundaryEdge geometryEdge, TriangleMesh geometry)
    {
        double worst = 0;
        foreach (int node in meshEdge.NodeIds)
        {
            var p = mesh.Nodes[node];
            double nearest = double.MaxValue;
            foreach (var segment in geometryEdge.Segments)
                nearest = Math.Min(nearest, SegmentDistance.PointToSegment(
                    p, geometry.Vertices[segment.A], geometry.Vertices[segment.B]));
            worst = Math.Max(worst, nearest);
        }
        return worst;
    }
}
