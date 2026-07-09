using System.Text.Json.Serialization;

namespace OpenSim.Core.Model;

/// <summary>Finite-element interpolation order of the generated mesh.</summary>
public enum ElementOrder
{
    /// <summary>4-node linear tetrahedra (TET4).</summary>
    Linear = 0,

    /// <summary>10-node quadratic tetrahedra (TET10) — fixes TET4's bending lock.</summary>
    Quadratic = 1
}

/// <summary>Meshing parameters for a body.</summary>
public sealed record MeshSettings
{
    /// <summary>Target element edge length [m]. 0 means "auto" (derived from geometry size).</summary>
    public double TargetEdgeLength { get; init; }

    /// <summary>
    /// Radius-ratio quality below which the mesher inserts refinement points; 0 disables
    /// refinement. 0.08 bounds the worst tets ~25× above the historical sliver floor
    /// while staying cheap; targets ≥0.2 risk budget explosion against the jittered
    /// surface skin. Old project files predate this property and get the default.
    /// </summary>
    public double TargetMinQuality { get; init; } = 0.08;

    /// <summary>Hard cap on refinement point insertions. 0 = automatic (max(1024, seed count)).</summary>
    public int MaxRefinementPoints { get; init; }

    /// <summary>Element order; Linear (TET4) is the default and what old files load as.</summary>
    public ElementOrder ElementOrder { get; init; } = ElementOrder.Linear;
}

/// <summary>
/// A single simulated body: its surface geometry, mesh settings, generated FE mesh,
/// assigned material and boundary conditions. Milestone 1 supports one body per project.
/// </summary>
public sealed class Body
{
    public required string Name { get; set; }

    /// <summary>Where the geometry came from — a file path for imports, or a description for primitives.</summary>
    public string? GeometrySource { get; set; }

    public TriangleMesh? Geometry { get; set; }
    public MeshSettings MeshSettings { get; set; } = new();
    public FeMesh? Mesh { get; set; }
    public Material? Material { get; set; }

    /// <summary>
    /// Per-region material names (region id → library material name) for multi-material
    /// PCB meshes. Null for single-material bodies. Resolved against the material library
    /// on load; the region ids match <see cref="FeMesh.ElementRegionIds"/>.
    /// </summary>
    public Dictionary<int, string>? RegionMaterialNames { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<BoundaryCondition> BoundaryConditions { get; } = new();
}

/// <summary>The root document a user edits: bodies, settings and the latest results.</summary>
public sealed class SimProject
{
    public string Name { get; set; } = "Untitled Project";

    /// <summary>
    /// The selected analysis type ("Static", "Electrical", "Thermal", "JouleCoupled").
    /// Stored as a string so old files (null) and unknown future types stay loadable.
    /// </summary>
    public string? AnalysisType { get; set; }

    /// <summary>The PCB stackup, when this project was built from a board import. Null otherwise.</summary>
    public PcbStackupSettings? Stackup { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Body> Bodies { get; } = new();
}
