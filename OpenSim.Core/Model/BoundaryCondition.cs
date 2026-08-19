using System.Text.Json.Serialization;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// A boundary condition applied to one or more geometric faces, edges or vertices of a
/// body. Solvers resolve the scope to mesh nodes/triangles via <see cref="FeMesh"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FixedSupport), "fixedSupport")]
[JsonDerivedType(typeof(ForceLoad), "force")]
[JsonDerivedType(typeof(PressureLoad), "pressure")]
[JsonDerivedType(typeof(VoltagePotential), "voltage")]
[JsonDerivedType(typeof(CurrentFlow), "current")]
[JsonDerivedType(typeof(FixedTemperature), "temperature")]
[JsonDerivedType(typeof(HeatFlux), "heatFlux")]
[JsonDerivedType(typeof(Convection), "convection")]
public abstract record BoundaryCondition
{
    public required string Name { get; init; }

    /// <summary>Geometric face ids this condition applies to.</summary>
    public required IReadOnlyList<int> FaceIds { get; init; }

    /// <summary>
    /// Geometric edge ids this condition additionally applies to (see
    /// <see cref="BoundaryEdgeSet"/>). Null — never an empty list from an old file —
    /// means face scoping only, so every existing project deserializes unchanged and
    /// every existing solver path is bitwise untouched.
    /// </summary>
    public IReadOnlyList<int>? EdgeIds { get; init; }

    /// <summary>
    /// Geometric vertex ids this condition additionally applies to. Null means none, for
    /// the same back-compatibility reason as <see cref="EdgeIds"/>.
    /// </summary>
    public IReadOnlyList<int>? VertexIds { get; init; }

    /// <summary>Whether this condition names any edge or vertex — a zero-AREA scope.</summary>
    [JsonIgnore]
    public bool HasZeroAreaScope => EdgeIds is { Count: > 0 } || VertexIds is { Count: > 0 };

    /// <summary>Whether this condition names nothing at all.</summary>
    [JsonIgnore]
    public bool IsEmptyScope =>
        FaceIds.Count == 0 && EdgeIds is not { Count: > 0 } && VertexIds is not { Count: > 0 };

    /// <summary>
    /// A human-readable summary of the scope, for solver logs and the conditions list.
    /// Derived, so it is never persisted — the ids are the record.
    /// </summary>
    [JsonIgnore]
    public string ScopeSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (FaceIds.Count > 0) parts.Add($"{FaceIds.Count} face(s)");
            if (EdgeIds is { Count: > 0 } e) parts.Add($"{e.Count} edge(s)");
            if (VertexIds is { Count: > 0 } v) parts.Add($"{v.Count} vertex/vertices");
            return parts.Count == 0 ? "nothing" : string.Join(" + ", parts);
        }
    }
}

/// <summary>All translational degrees of freedom fixed on the selected faces.</summary>
public sealed record FixedSupport : BoundaryCondition;

/// <summary>
/// A total force [N] distributed over the nodes of the selected faces
/// (area-weighted, so the resultant equals <see cref="TotalForce"/> exactly).
/// </summary>
public sealed record ForceLoad : BoundaryCondition
{
    public required Vector3D TotalForce { get; init; }
}

/// <summary>
/// A uniform pressure [Pa] acting along the inward surface normal of the selected
/// faces (positive pushes into the body, the usual engineering convention).
/// </summary>
public sealed record PressureLoad : BoundaryCondition
{
    public required double Magnitude { get; init; }
}

/// <summary>A prescribed electric potential [V] on the selected faces (Dirichlet).</summary>
public sealed record VoltagePotential : BoundaryCondition
{
    public required double Volts { get; init; }
}

/// <summary>
/// A total current [A] injected through the selected faces, distributed area-weighted
/// over the face nodes so the resultant equals <see cref="TotalCurrent"/> exactly
/// (positive flows into the body).
/// </summary>
public sealed record CurrentFlow : BoundaryCondition
{
    public required double TotalCurrent { get; init; }
}

/// <summary>A prescribed temperature [K] on the selected faces (Dirichlet).</summary>
public sealed record FixedTemperature : BoundaryCondition
{
    public required double Kelvin { get; init; }
}

/// <summary>
/// A total heat flow [W] injected through the selected faces, distributed area-weighted
/// over the face nodes (positive heats the body).
/// </summary>
public sealed record HeatFlux : BoundaryCondition
{
    public required double TotalPower { get; init; }
}

/// <summary>
/// Convective heat exchange with an ambient fluid on the selected faces (Robin):
/// q = h·(T − T_ambient) leaving the surface.
/// </summary>
public sealed record Convection : BoundaryCondition
{
    /// <summary>Heat transfer coefficient h [W/(m²·K)].</summary>
    public required double Coefficient { get; init; }

    /// <summary>Ambient temperature [K].</summary>
    public required double AmbientTemperature { get; init; }
}
