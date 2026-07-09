using System.Text.Json.Serialization;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// A boundary condition applied to one or more geometric faces of a body.
/// Solvers resolve the face ids to mesh nodes/triangles via <see cref="FeMesh"/>.
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
