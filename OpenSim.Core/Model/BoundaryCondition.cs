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
[JsonDerivedType(typeof(ElectricCharge), "charge")]
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

    /// <summary>
    /// Geometric edge ids in the GEOMETRY's own numbering (see
    /// <see cref="TriangleMesh.FeatureEdges"/>) rather than the mesh's.
    /// <para>
    /// This is the scope worth storing. Mesh edge ids are derived from the skin, so they are
    /// renumbered by every remesh and a saved project can only name them until the element
    /// size changes; geometry ids change only when the geometry does. They are resolved to
    /// mesh ids at solve time by <see cref="GeometryScopeResolver"/> — never written back,
    /// because a stored copy of a derived id could only ever go stale again.
    /// </para>
    /// Null, never an empty list, so every project written before geometry scoping existed
    /// deserializes unchanged.
    /// </summary>
    public IReadOnlyList<int>? GeometryEdgeIds { get; init; }

    /// <summary>Geometric vertex ids in the geometry's own numbering, for the same reason
    /// as <see cref="GeometryEdgeIds"/>.</summary>
    public IReadOnlyList<int>? GeometryVertexIds { get; init; }

    /// <summary>Whether this condition still carries an unresolved geometry-space scope.</summary>
    [JsonIgnore]
    public bool HasGeometryScope =>
        GeometryEdgeIds is { Count: > 0 } || GeometryVertexIds is { Count: > 0 };

    /// <summary>Whether this condition names any edge or vertex — a zero-AREA scope.
    /// Counts the geometry-space ids too, so the refusal fires when the condition is
    /// CREATED rather than only after it has been resolved.</summary>
    [JsonIgnore]
    public bool HasZeroAreaScope =>
        EdgeIds is { Count: > 0 } || VertexIds is { Count: > 0 } || HasGeometryScope;

    /// <summary>Whether this condition names nothing at all.</summary>
    [JsonIgnore]
    public bool IsEmptyScope =>
        FaceIds.Count == 0 && EdgeIds is not { Count: > 0 } && VertexIds is not { Count: > 0 }
        && !HasGeometryScope;

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
            int edges = (EdgeIds?.Count ?? 0) + (GeometryEdgeIds?.Count ?? 0);
            int vertices = (VertexIds?.Count ?? 0) + (GeometryVertexIds?.Count ?? 0);
            if (edges > 0) parts.Add($"{edges} edge(s)");
            if (vertices > 0) parts.Add($"{vertices} vertex/vertices");
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
/// A total current [A] injected through the selected faces (positive flows into the body).
/// By default it enters at UNIFORM density: distributed area-weighted over the face nodes so
/// the resultant equals <see cref="TotalCurrent"/> exactly, and the face is not an
/// equipotential. With <see cref="Equipotential"/> the face is a terminal: one potential
/// over the whole face, found by the solve, carrying the total current — what a metal pad
/// or a bus bar's end is. The two agree where the current would be uniform anyway (the end
/// of a bar) and differ where it crowds (a pad feeding a trace from one side).
/// </summary>
public sealed record CurrentFlow : BoundaryCondition
{
    public required double TotalCurrent { get; init; }

    /// <summary>The face is one equipotential terminal carrying <see cref="TotalCurrent"/>,
    /// rather than a uniform-density injection. False — what every file written before the
    /// option existed loads as — leaves the solve bitwise as it was.</summary>
    public bool Equipotential { get; init; }
}

/// <summary>
/// A prescribed total charge [C] for an electrostatic solve. On a conductor the faces
/// touch, the whole conductor piece is an electrode of unknown potential carrying this net
/// charge (a charged floating conductor; the solve reports its potential). On a dielectric
/// surface it is a uniform surface charge density <see cref="TotalCharge"/>/A — the Neumann
/// condition D·n = σ — distributed area-weighted over the face nodes so the total is exact.
/// </summary>
public sealed record ElectricCharge : BoundaryCondition
{
    public required double TotalCharge { get; init; }
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
    /// <summary>Heat transfer coefficient h [W/(m²·K)]: everywhere on the faces, or with a
    /// <see cref="Profile"/> the faces' mean (a developing boundary layer) or the base its
    /// factors multiply.</summary>
    public required double Coefficient { get; init; }

    /// <summary>Ambient temperature [K].</summary>
    public required double AmbientTemperature { get; init; }

    /// <summary>How h varies over the faces (a boundary layer growing along a board, the
    /// wakes behind parts). Null: uniform, as it always was.</summary>
    public ConvectionProfile? Profile { get; init; }
}

/// <summary>
/// The variation of a convection coefficient over its faces, along a flow. Distance x is
/// measured downstream of <see cref="LeadingEdge"/> along <see cref="FlowDirection"/>.
/// The local coefficient is
/// <c>h(x) = Coefficient · g(x) · table(x) · Π wakes</c>, each factor 1 when not given:
/// <list type="bullet">
/// <item>g, the boundary layer's development: h ∝ x^<see cref="Exponent"/>, scaled so the
/// faces' area-mean of Coefficient·g is Coefficient (−½ for a laminar layer, about −⅕ for a
/// turbulent one). Each triangle takes the law's exact mean over its area (the
/// singularity at the leading edge is integrable); upstream of the edge x counts as 0.</item>
/// <item>table, a factor against x (straight lines between the points, flat beyond them).</item>
/// <item>a wake: behind a part, a band of the flow's direction narrower than
/// <see cref="ConvectionWake.HalfWidth"/> either side of the part's axis, the factor
/// rising from <see cref="ConvectionWake.Factor"/> just behind the part to 1 at
/// <see cref="ConvectionWake.Length"/> downstream.</item>
/// </list>
/// Each triangle takes the mean of h over its area. The shapes are the user's: the
/// profile carries no correlation of its own.
/// </summary>
public sealed record ConvectionProfile
{
    /// <summary>Where the flow meets the surface first [m].</summary>
    public required Vector3D LeadingEdge { get; init; }

    /// <summary>The direction the flow runs (normalized when used).</summary>
    public required Vector3D FlowDirection { get; init; }

    /// <summary>Boundary-layer development, h ∝ x^Exponent with the faces' mean held at
    /// <see cref="Convection.Coefficient"/>; must lie in (−1, 0]. Null: none.</summary>
    public double? Exponent { get; init; }

    /// <summary>Downstream distances [m] of a factor table, rising.</summary>
    public IReadOnlyList<double>? Distances { get; init; }

    /// <summary>The table's factors, one per distance.</summary>
    public IReadOnlyList<double>? Factors { get; init; }

    /// <summary>Wakes behind parts.</summary>
    public IReadOnlyList<ConvectionWake> Wakes { get; init; } = Array.Empty<ConvectionWake>();
}

/// <summary>A part's wake on a convecting surface: from <paramref name="Position"/> (the
/// part's footprint centre) downstream for <paramref name="Length"/> [m], within
/// <paramref name="HalfWidth"/> [m] of the axis through the position along the flow, the
/// coefficient is multiplied by <paramref name="Factor"/> just behind the part, recovering
/// linearly to 1 at the wake's end.</summary>
public sealed record ConvectionWake(Vector3D Position, double HalfWidth, double Length, double Factor);
