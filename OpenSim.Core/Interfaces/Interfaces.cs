using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Core.Interfaces;

/// <summary>Imports body geometry from a file. Implementations are the plugin seam for new formats.</summary>
public interface IGeometryImporter
{
    /// <summary>Human-readable format name, e.g. "STL".</summary>
    string FormatName { get; }

    /// <summary>Supported file extensions including the dot, lower-case, e.g. [".stl"].</summary>
    IReadOnlyList<string> FileExtensions { get; }

    TriangleMesh Import(string filePath);
}

/// <summary>Generates a finite element mesh from surface geometry.</summary>
public interface IMeshGenerator
{
    string Name { get; }

    FeMesh Generate(TriangleMesh geometry, MeshSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Progress report emitted during a solve.</summary>
public sealed record SolverProgress(string Stage, double Fraction);

/// <summary>The complete, validated input to a solver run.</summary>
public sealed record SolveInput
{
    public required FeMesh Mesh { get; init; }

    /// <summary>The material for region 0, and the fallback for regions without an entry in <see cref="RegionMaterials"/>.</summary>
    public required Material Material { get; init; }

    public required IReadOnlyList<BoundaryCondition> BoundaryConditions { get; init; }

    /// <summary>
    /// Optional per-region materials for multi-material meshes (keyed by
    /// <see cref="FeMesh.ElementRegionIds"/> values). Null for single-material solves.
    /// </summary>
    public IReadOnlyDictionary<int, Material>? RegionMaterials { get; init; }

    /// <summary>
    /// Optional volumetric heat source per element [W/m³] (e.g. Joule heating from a
    /// preceding electrical solve). Consumed by the thermal solver, ignored by others.
    /// </summary>
    public IReadOnlyList<double>? ElementHeatSource { get; init; }

    /// <summary>
    /// Time-integration settings for a transient thermal solve. Consumed by the transient
    /// thermal solver (and the Joule study's thermal leg); ignored by others. Null for
    /// steady-state solves.
    /// </summary>
    public TransientThermalSettings? TransientThermal { get; init; }

    /// <summary>Settings for a modal analysis. Null (the default mode count applies)
    /// or ignored outside the modal solver.</summary>
    public ModalSettings? Modal { get; init; }

    /// <summary>Frequency-sweep settings for the harmonic electrical solver. Null for
    /// all other analyses.</summary>
    public HarmonicElectricSettings? HarmonicElectric { get; init; }

    /// <summary>The material governing one element, honouring <see cref="RegionMaterials"/>.</summary>
    public Material MaterialOf(int elementIndex) =>
        RegionMaterials is null
            ? Material
            : RegionMaterials.GetValueOrDefault(Mesh.RegionOf(elementIndex)) ?? Material;
}

/// <summary>The standardized output of any solver: named result fields plus a log.</summary>
public sealed record SolveOutput
{
    public required IReadOnlyList<IResultField> Fields { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>
    /// Optional scalar summary values (label → value), e.g. computed resistance, total
    /// current or dissipated power — for display without parsing the log. Null when the
    /// solver produces none.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Summary { get; init; }

    /// <summary>
    /// Optional multi-frame results (time steps, modes, frequency points), ordered by
    /// <see cref="ResultFrame.Value"/>. When set, <see cref="Fields"/> must be the default
    /// frame's field list (by convention: the final time step, the first mode, or the first
    /// frequency), so single-frame consumers keep working unchanged. Null for ordinary
    /// single-result solves.
    /// </summary>
    public IReadOnlyList<ResultFrame>? Frames { get; init; }

    /// <summary>The frame axis caption for the UI ("Time", "Mode", "Frequency"); null when <see cref="Frames"/> is null.</summary>
    public string? FrameAxis { get; init; }
}

/// <summary>A physics solver. All solvers consume the same mesh and produce standard result fields.</summary>
public interface ISolver
{
    string Name { get; }

    /// <summary>Throws with an actionable message when the input cannot be solved.</summary>
    void Validate(SolveInput input);

    SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
