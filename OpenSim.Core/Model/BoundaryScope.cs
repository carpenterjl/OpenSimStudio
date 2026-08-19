namespace OpenSim.Core.Model;

/// <summary>
/// The rules that decide whether a boundary condition scope is meaningful, shared by every
/// solver so a scope kind is accepted or refused identically wherever it appears.
/// </summary>
public static class BoundaryScope
{
    /// <summary>
    /// Whether a condition may be scoped to an edge or a vertex.
    /// <para>
    /// Only pure Dirichlet conditions may. A distributed quantity — a total force, current
    /// or heat flow — is spread over the scope area-weighted, and the area of a curve or a
    /// point is zero: the distribution is undefined, not merely awkward. Refusing it is the
    /// difference between a typed failure and a division by zero that reaches a result
    /// field. (A genuine line or point load is its own modelling concept and would need its
    /// own condition type carrying N/m or N.)
    /// </para>
    /// </summary>
    public static bool AcceptsZeroAreaScope(BoundaryCondition condition) =>
        condition is FixedSupport or FixedTemperature or VoltagePotential;

    /// <summary>
    /// Validates one condition scope against the mesh it will be solved on. Throws a typed
    /// failure naming the condition; never returns quietly having ignored part of a scope.
    /// </summary>
    public static void Validate(BoundaryCondition condition, FeMesh mesh)
    {
        if (condition.IsEmptyScope)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' has no faces, edges or vertices assigned.");

        if (condition.HasZeroAreaScope && !AcceptsZeroAreaScope(condition))
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' ({condition.GetType().Name}) distributes a total " +
                "quantity over its scope, so it cannot be applied to an edge or a vertex — their area " +
                "is zero and the distribution is undefined. Scope it to a face instead.");

        // A named id the mesh does not carry would otherwise be silently dropped, and the
        // solve would quietly under-constrain.
        if (condition.EdgeIds is { } edgeIds)
            foreach (int id in edgeIds)
                if (mesh.Edges.EdgeById(id) is null)
                    throw new InvalidOperationException(
                        $"Boundary condition '{condition.Name}' names edge {id}, but this mesh has " +
                        $"{mesh.Edges.Edges.Count} geometric edge(s). Re-select the scope after remeshing.");

        if (condition.VertexIds is { } vertexIds)
            foreach (int id in vertexIds)
                if (mesh.Edges.VertexById(id) is null)
                    throw new InvalidOperationException(
                        $"Boundary condition '{condition.Name}' names vertex {id}, but this mesh has " +
                        $"{mesh.Edges.Vertices.Count} geometric vertex/vertices. Re-select the scope after remeshing.");

        if (mesh.GetScopeNodes(condition).Count == 0)
            throw new InvalidOperationException(
                $"Boundary condition '{condition.Name}' targets {condition.ScopeSummary} that do not " +
                "exist on the mesh.");
    }
}
