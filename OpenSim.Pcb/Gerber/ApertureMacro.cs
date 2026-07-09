using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Gerber;

/// <summary>
/// A raw %AM% aperture-macro definition: the body statements are stored verbatim and
/// evaluated at %ADD% time against that definition's actual parameters (parameters are
/// per-AD, so the evaluated aperture is immutable concrete geometry afterwards).
/// </summary>
public sealed record MacroDefinition(string Name, IReadOnlyList<string> BodyStatements);

/// <summary>
/// One evaluated macro primitive. Convention: all geometry is fully resolved at
/// evaluation time — unit-scaled to meters and with the spec's rotation-about-the-
/// MACRO-ORIGIN baked into the stored points. Vector lines (20), center lines (21),
/// outlines (4) and regular polygons (5) all resolve to an explicit vertex ring;
/// only circles and thermals stay symbolic because their tessellation density is the
/// consumer's chord-tolerance decision.
/// </summary>
public abstract record MacroPrimitive(bool Exposure);

/// <summary>Primitive 1: a circle (also the warned approximation of a moiré, primitive 6).</summary>
public sealed record MacroCircle(bool Exposure, Point2 Center, double Diameter) : MacroPrimitive(Exposure);

/// <summary>Primitives 4/5/20/21 resolved to one explicit polygon ring (may be degenerate —
/// real emitters produce zero-height center lines; the polygon engine drops them).</summary>
public sealed record MacroRing(bool Exposure, IReadOnlyList<Point2> Vertices) : MacroPrimitive(Exposure);

/// <summary>
/// Primitive 7: a thermal relief — outer circle minus inner circle minus a crosshair of
/// two gap rectangles. <paramref name="RotationDeg"/> orients the crosshair (the center
/// is already origin-rotated); always exposure-on per spec.
/// </summary>
public sealed record MacroThermal(Point2 Center, double OuterDiameter, double InnerDiameter,
    double GapWidth, double RotationDeg) : MacroPrimitive(true);
