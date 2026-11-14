using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// One traced ray as the layout views draw it: the polyline of its intersection points in the
/// global frame (meters), launch point first, and which field and wavelength it belongs to (zero
/// based, in the order of the system's field and wavelength lists). A ray that failed ends at
/// the surface it failed at and carries its <see cref="Status"/>; it is drawn unless the view
/// is asked to delete vignetted rays (docs/Optics-Conventions.md §5: never dropped silently).
/// </summary>
public sealed record LayoutRayPath(IReadOnlyList<Vector3D> Points, int FieldIndex, int WavelengthIndex,
    RayStatus Status = RayStatus.Ok);
