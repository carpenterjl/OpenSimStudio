using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

// The resolved B-rep topology family: immutable records mirroring the STEP entities the
// tessellator walks. Grouped in one file the way the PCB model records are — they are
// data, not behaviour.

/// <summary>VERTEX_POINT: a topological vertex pinned to an exact 3D point (meters).</summary>
public sealed record StepVertex(int Id, Vector3D Point);

/// <summary>
/// EDGE_CURVE: a topological edge from Start to End along Curve. CurveSameSense is the
/// edge's same_sense flag: false means the edge runs against the curve's own
/// parameterization.
/// </summary>
public sealed record StepEdge(int Id, StepVertex Start, StepVertex End, StepCurve Curve, bool CurveSameSense)
{
    /// <summary>True for a ring edge (full circle/closed spline) whose two topological ends coincide.</summary>
    public bool IsRing => ReferenceEquals(Start, End) || Start.Point.Equals(End.Point);
}

/// <summary>ORIENTED_EDGE: an edge used forwards (true) or backwards (false) in a loop.</summary>
public sealed record StepEdgeUse(StepEdge Edge, bool Forward);

/// <summary>
/// EDGE_LOOP or VERTEX_LOOP. A vertex loop bounds a face with no boundary curve at all
/// (a full sphere, the tip cap of a cone) and contributes no edge samples.
/// </summary>
public sealed record StepLoop(int Id, IReadOnlyList<StepEdgeUse> Edges, StepVertex? VertexLoopVertex = null)
{
    public bool IsVertexLoop => VertexLoopVertex is not null;
}

/// <summary>FACE_BOUND / FACE_OUTER_BOUND: a loop with its orientation flag on this face.</summary>
public sealed record StepFaceBound(StepLoop Loop, bool Orientation, bool IsOuter);

/// <summary>
/// ADVANCED_FACE: a trimmed surface patch. SameSense false flips the face normal
/// relative to the surface's natural ∂u×∂v.
/// </summary>
public sealed record StepFace(int Id, StepSurface Surface, bool SameSense, IReadOnlyList<StepFaceBound> Bounds);

/// <summary>CLOSED_SHELL (with any ORIENTED_CLOSED_SHELL flip applied by the resolver).</summary>
public sealed record StepShell(int Id, IReadOnlyList<StepFace> Faces, bool Orientation);

/// <summary>MANIFOLD_SOLID_BREP / BREP_WITH_VOIDS: one outer shell plus internal cavities.</summary>
public sealed record StepSolid(int Id, StepShell Outer, IReadOnlyList<StepShell> Voids);
