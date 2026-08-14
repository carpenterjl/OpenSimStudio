using OpenSim.Core.Model;

namespace OpenSim.Geometry.Step;

/// <summary>One body of an imported assembly, already placed in the root frame.</summary>
/// <param name="Name">Occurrence name (reference designator or product name).</param>
/// <param name="Mesh">Welded, outward-oriented surface mesh in world coordinates.</param>
/// <param name="SignedVolume">Enclosed volume [m³]; positive for a correctly oriented solid.</param>
/// <param name="SolidId">#id of the source solid — repeats when a part is instanced.</param>
/// <param name="PlacedByAssembly">False when the file gave no product structure for this
/// body and it was taken in the global frame.</param>
public sealed record StepAssemblyBody(string Name, TriangleMesh Mesh, double SignedVolume,
    int SolidId, bool PlacedByAssembly);

/// <summary>
/// Result of a multi-body STEP import.
/// <para>
/// <see cref="Bodies"/> is in the assembly resolver's deterministic tree order (roots by
/// ascending PRODUCT_DEFINITION #id, children by ascending NEXT_ASSEMBLY_USAGE_OCCURRENCE
/// #id). That order is carried into the project's body list and therefore becomes the
/// REGION NUMBERING of the merged assembly mesh — so it is a contract, not a display
/// detail, and must not be re-sorted downstream.
/// </para>
/// </summary>
public sealed record StepAssemblyReport(IReadOnlyList<StepAssemblyBody> Bodies,
    IReadOnlyList<string> Notes);
