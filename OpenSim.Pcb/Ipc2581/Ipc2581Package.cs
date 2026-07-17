using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>One LandPattern pad of a footprint package, in the package's LOCAL frame:
/// the pin name, the pad's local placement, and its primitive-dictionary shape ref.</summary>
public sealed record Ipc2581PackagePad(
    string? Pin,
    Point2 Location,
    double RotationDeg,
    bool Mirror,
    string? PrimitiveRef);

/// <summary>
/// A footprint package from the <c>Package</c> element: the LandPattern pad list in
/// local coordinates. Placed-pad GEOMETRY comes from the conductor LayerFeatures (every
/// exporter flashes the placed pads there); the package pads exist for the model and as
/// the test-side transform oracle — instantiating them under each Component's placement
/// must land on the placed copper, which pins the Xform mirror convention empirically.
/// </summary>
public sealed record Ipc2581Package(string Name, IReadOnlyList<Ipc2581PackagePad> Pads);
