using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Gerber;

/// <summary>Object polarity: dark adds copper, clear erases what is already painted.</summary>
public enum GerberPolarity
{
    Dark,
    Clear
}

/// <summary>A Gerber aperture definition. All dimensions in meters.</summary>
public abstract record Aperture(int Code);

/// <summary>Standard circle aperture (C), with an optional round hole.</summary>
public sealed record CircleAperture(int Code, double Diameter, double? HoleDiameter = null) : Aperture(Code);

/// <summary>Standard rectangle aperture (R), axis-aligned, with an optional round hole.</summary>
public sealed record RectangleAperture(int Code, double Width, double Height, double? HoleDiameter = null) : Aperture(Code);

/// <summary>Standard obround aperture (O): a rectangle with semicircular caps on the short
/// sides, with an optional round hole.</summary>
public sealed record ObroundAperture(int Code, double Width, double Height, double? HoleDiameter = null) : Aperture(Code);

/// <summary>
/// Standard polygon aperture (P): a regular polygon of 3–12 vertices inscribed in
/// <see cref="OuterDiameter"/>, first vertex on +X before rotation (about its center).
/// </summary>
public sealed record PolygonAperture(int Code, double OuterDiameter, int VertexCount,
    double RotationDeg, double? HoleDiameter = null) : Aperture(Code);

/// <summary>
/// An aperture defined by an %AM% macro, already evaluated against this definition's
/// parameters into concrete primitives (meters, rotation baked). <see cref="BoundingSize"/>
/// is the largest footprint dimension of the exposure-on primitives — pad extraction
/// classifies flashes by it.
/// </summary>
public sealed record MacroAperture(int Code, string MacroName,
    IReadOnlyList<MacroPrimitive> Primitives, double BoundingSize) : Aperture(Code);

/// <summary>
/// An aperture the parser cannot render faithfully (currently only macro moiré
/// fallbacks and other approximations). It is flashed as a circle of
/// <see cref="ApproximateDiameter"/> and reported as a warning — never silently
/// misrendered without notice.
/// </summary>
public sealed record UnsupportedAperture(int Code, string Kind, double ApproximateDiameter) : Aperture(Code);

/// <summary>One image operation, in file order (order matters for polarity).</summary>
public abstract record GerberOp(GerberPolarity Polarity);

/// <summary>
/// A stroked polyline (D01 draws chained while the aperture and polarity stay the same;
/// arcs are tessellated to segments at parse time using the configured chord tolerance).
/// </summary>
public sealed record DrawOp(IReadOnlyList<Point2> Path, Aperture Aperture, GerberPolarity Polarity)
    : GerberOp(Polarity);

/// <summary>An aperture flash (D03).</summary>
public sealed record FlashOp(Point2 Position, Aperture Aperture, GerberPolarity Polarity)
    : GerberOp(Polarity);

/// <summary>A filled region (G36/G37), possibly with several contours.</summary>
public sealed record RegionOp(IReadOnlyList<IReadOnlyList<Point2>> Contours, GerberPolarity Polarity)
    : GerberOp(Polarity);

/// <summary>A parsed Gerber layer: aperture table, ordered image operations, warnings.</summary>
public sealed class GerberDocument
{
    public required IReadOnlyDictionary<int, Aperture> Apertures { get; init; }
    public required IReadOnlyList<GerberOp> Ops { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
