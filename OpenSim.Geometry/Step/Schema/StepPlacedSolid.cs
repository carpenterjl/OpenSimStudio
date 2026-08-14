namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// One occurrence of a solid in the assembly tree: which solid, what it is called, and
/// where it sits in the root frame.
/// </summary>
/// <param name="SolidId">#id of the MANIFOLD_SOLID_BREP / BREP_WITH_VOIDS. Repeats across
/// occurrences of the same part — the tessellation is computed once and reused.</param>
/// <param name="Name">Occurrence name: the assembly reference designator when the file
/// gives one, otherwise the product name, disambiguated with ":2", ":3"… .</param>
/// <param name="Transform">Placement into the root frame.</param>
/// <param name="PlacedByAssembly">False when the file carried no product structure for
/// this solid and it was taken at identity — the note says so, because "no transform" and
/// "identity transform" are different claims.</param>
public sealed record StepPlacedSolid(int SolidId, string Name,
    StepRigidTransform Transform, bool PlacedByAssembly);
