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
/// assigned material and boundary conditions. A project holds one body for the
/// single-part workflows and one per part for an imported assembly, each meshed
/// independently and merged for the solve.
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
    /// Uniform heat dissipated inside this body [W] — the natural way to say "this chip
    /// burns 3 W". Null means no internal source. It is spread over the body's VOLUME at
    /// solve time (q = P/V), which is why it lives here and not on a face condition.
    /// </summary>
    public double? HeatSourcePower { get; set; }

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

    /// <summary>What surrounds the bodies (vacuum / still fluid / moving fluid) for
    /// environment heat-flow studies. Null — the value every older project loads with —
    /// means no environment, so those projects solve exactly as they always did.</summary>
    public EnvironmentSettings? Environment { get; set; }

    /// <summary>How the bodies are joined when several are solved together (contact
    /// conductance, gap tolerance). Null — every older project — means the defaults.</summary>
    public AssemblySettings? Assembly { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Body> Bodies { get; } = new();
}
