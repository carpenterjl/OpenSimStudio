using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>
/// The shape shared by OpticStudio's Birefringent In and Birefringent Out surfaces (OP-53,
/// docs/Optics-Conventions.md §62; OpticStudio 2025 R2 help, "Birefringent In and Birefringent
/// Out", saved under plans/sources/optics/op-53-help/): "The default substrate shape is the same
/// as a Standard surface ... defined by the radius of curvature and a conic constant. This
/// standard substrate is called shape 0." Shape 0 is traced exactly as the Standard surface (the
/// same <see cref="ConicIntersection"/> calls). Shape 1 (toroidal with aspheric terms, parameters
/// 7–12) is not built: <see cref="BirefringentLayouts"/> refuses a non-zero shape. Parameters 7–12
/// are kept as entered and, as the help says ("If the shape is not zero, parameters 7–12 are
/// used"), unused for shape 0.
/// <para>
/// The shape does not turn rays itself: the trace refracts at a birefringent surface through
/// <see cref="BirefringentSpaces"/>, which knows the crystal on each side.
/// </para>
/// </summary>
public abstract class BirefringentShape : SurfaceShape
{
    private protected BirefringentShape(double radiusOfRotation, IReadOnlyList<double> aspheric)
    {
        RadiusOfRotation = radiusOfRotation;
        Aspheric = aspheric.ToArray();
    }

    /// <summary>Parameter 7, the radius of rotation of shape 1 (meters; unused for shape 0).</summary>
    public double RadiusOfRotation { get; }

    /// <summary>Parameters 8–12, the 2nd to 10th order aspheric coefficients of shape 1 (unused for shape 0).</summary>
    public IReadOnlyList<double> Aspheric { get; }

    public override double Sag(double x, double y, SequentialSurface surface) =>
        ConicSag.TryEvaluate(Math.Sqrt(x * x + y * y), surface.Curvature, surface.Conic, out double z, out _) ? z : double.NaN;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance) =>
        ConicIntersection.Intersect(x, y, direction, surface.Curvature, surface.Conic, out point, out distance);

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) =>
        ConicIntersection.Normal(point, surface.Curvature, surface.Conic);
}

/// <summary>
/// OpticStudio's Birefringent In surface: the interface into a homogeneous uniaxial crystal.
/// The surface's medium is the crystal's ordinary index; <see cref="Extraordinary"/> is its
/// extraordinary index ("OpticStudio appends "-E" to the glass name, and then looks for this
/// material in the glass catalog", help; a saved lens does that lookup in
/// <see cref="SavedSurfaces.ToLens"/>). Parameters (help, "Parameter Definitions for Birefringent
/// "In" Surfaces"): 0 length to draw the crystal axis, 1 mode, 2–4 the x, y and z direction
/// cosines of the crystal axis in this surface's local frame, 5 Parax Ignore, 6 shape, 7 radius of
/// rotation, 8–12 aspheric coefficients, 13 gradient mode.
/// </summary>
public sealed class BirefringentInShape : BirefringentShape
{
    public const string Name = "Birefringent In";

    public BirefringentInShape(int mode, Vector3D axis, double drawLength = 0, int paraxIgnore = 0,
        double radiusOfRotation = 0, IReadOnlyList<double>? aspheric = null, ParaxialMedium? extraordinary = null)
        : base(radiusOfRotation, aspheric ?? Array.Empty<double>())
    {
        if (mode is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(mode), mode, $"{Name}: the mode is 0, 1, 2 or 3.");
        if (paraxIgnore is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(paraxIgnore), paraxIgnore, $"{Name}: Parax Ignore is 0 or 1.");
        Mode = mode;
        Axis = axis;
        DrawLength = drawLength;
        ParaxIgnore = paraxIgnore;
        Extraordinary = extraordinary;
    }

    public override string TypeName => Name;

    /// <summary>Parameter 1: 0 and 2 trace the ordinary ray, 1 and 3 the extraordinary one; 2
    /// and 3 also carry the other mode's part of the field with its phase (help, "Determining
    /// which ray is traced" and "Accounting for phase rotation").</summary>
    public int Mode { get; }

    /// <summary>Parameters 2–4 as entered (not normalised; "OpticStudio internally converts to
    /// unit vector components"), in this surface's local frame.</summary>
    public Vector3D Axis { get; }

    /// <summary>Parameter 0, meters. Saved; the layouts do not draw the axis.</summary>
    public double DrawLength { get; }

    /// <summary>Parameter 5: "If 1, paraxial rays ignore the extraordinary index."</summary>
    public int ParaxIgnore { get; }

    /// <summary>The extraordinary index, or null when not given (the trace then refuses the lens).</summary>
    public ParaxialMedium? Extraordinary { get; }

    /// <summary>The mode traced through the crystal.</summary>
    public CrystalMode TracedMode => Mode is 0 or 2 ? CrystalMode.Ordinary : CrystalMode.Extraordinary;

    /// <summary>True for modes 2 and 3, which keep the other mode's part of the field.</summary>
    public bool KeepsOtherMode => Mode >= 2;

    /// <summary>The same surface with another extraordinary index.</summary>
    public BirefringentInShape WithExtraordinary(ParaxialMedium? extraordinary) =>
        new(Mode, Axis, DrawLength, ParaxIgnore, RadiusOfRotation, Aspheric, extraordinary);

    /// <summary>
    /// The vertex curvature with the ordinary index of the surface's medium ("Most OpticStudio
    /// calculations, such as EFL, EPD, F/#, etc. use the ordinary index of refraction"). With an
    /// extraordinary mode and Parax Ignore 0 the help has paraxial rays use the extraordinary
    /// index, which is not built: refused, naming parameter 5.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        if (TracedMode == CrystalMode.Extraordinary && ParaxIgnore == 0)
            throw new ArgumentException($"{Name}: paraxial rays through an extraordinary mode (mode {Mode}) with Parax Ignore 0 would use the extraordinary index, which is not built; set Parax Ignore (parameter 5) to 1 so paraxial rays use the ordinary index.");
        return base.ParaxialEquivalent(surface);
    }
}

/// <summary>
/// OpticStudio's Birefringent Out surface: the interface out of the crystal that the last
/// Birefringent In began, into the surface's own (isotropic) medium. Parameters 0–5 are unused,
/// 6 shape, 7 radius of rotation, 8–12 aspheric coefficients (help, "Parameter Definitions for
/// Birefringent "Out" Surfaces").
/// </summary>
public sealed class BirefringentOutShape : BirefringentShape
{
    public const string Name = "Birefringent Out";

    public BirefringentOutShape(double radiusOfRotation = 0, IReadOnlyList<double>? aspheric = null)
        : base(radiusOfRotation, aspheric ?? Array.Empty<double>())
    {
    }

    public override string TypeName => Name;
}
