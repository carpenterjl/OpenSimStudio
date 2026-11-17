using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential;

/// <summary>What an object's material makes it do (OpticStudio help, "Refraction and Reflection
/// From NSC Objects").</summary>
public enum NscMaterialKind
{
    /// <summary>Blank (index 1, the system's air) or a glass: a volume refracts at every face;
    /// a surface object is ignored.</summary>
    Refractive = 0,

    /// <summary>MIRROR: every face reflects. Uncoated, the face is a thick aluminium layer
    /// (0.7 − 7.0i in OpticStudio's convention) when energy is accounted.</summary>
    Mirror = 1,

    /// <summary>ABSORB: every face absorbs, "even if a reflective coating is applied".</summary>
    Absorb = 2,
}

/// <summary>An object's material: its kind and, for a refractive object, its medium (null is a
/// blank material, index 1 relative to the system's air).</summary>
public sealed record NscMaterial
{
    public NscMaterialKind Kind { get; init; }

    /// <summary>The medium of a refractive object; null for blank. Ignored for MIRROR and ABSORB.</summary>
    public ParaxialMedium? Medium { get; init; }

    public static NscMaterial Blank { get; } = new();
    public static NscMaterial Mirror { get; } = new() { Kind = NscMaterialKind.Mirror };
    public static NscMaterial Absorb { get; } = new() { Kind = NscMaterialKind.Absorb };
    public static NscMaterial Of(ParaxialMedium medium) => new() { Medium = medium ?? throw new ArgumentNullException(nameof(medium)) };

    /// <summary>"MIRROR", "ABSORB", "(blank)" or the medium.</summary>
    public string Describe() => Kind switch
    {
        NscMaterialKind.Mirror => "MIRROR",
        NscMaterialKind.Absorb => "ABSORB",
        _ => Medium?.Describe() ?? "(blank)",
    };
}

/// <summary>"Face Is" on OpticStudio's Coat/Scatter tab (help, "Coat/Scatter").</summary>
public enum NscFaceBehaviour
{
    /// <summary>Refractive, reflective or absorbing as the material says.</summary>
    ObjectDefault = 0,

    /// <summary>Always reflective; uncoated, a thick aluminium layer 0.7 − 7.0i.</summary>
    Reflective = 1,

    /// <summary>Always absorbing.</summary>
    Absorbing = 2,
}

/// <summary>The properties of one face of an object: "Face Is" and a coating name from the
/// system coating catalog (null for uncoated), and its scattering (OP-80).</summary>
public sealed record NscFace
{
    public NscFaceBehaviour FaceIs { get; init; }
    public string? Coating { get; init; }

    // OP-80
    /// <summary>The face's scatter model (help, "Coat/Scatter"); null for none. The trace
    /// scatters only with <see cref="NscTraceSettings.ScatterRays"/> on.</summary>
    public Scattering.NscFaceScatter? Scatter { get; init; }
}

/// <summary>
/// Where an object sits (OpticStudio help, "Object Placement"; docs/Optics-Conventions.md §4
/// and §69): decenters x, y, z (meters) and tilts about x, y, z (degrees) relative to its
/// reference object. By default the tilts are about the local x, then the new y, then the new
/// z (G = O + Rx·Ry·Rz·L, as a coordinate break with order flag 0); with
/// <see cref="GlobalXyzRotationOrder"/> they are about the fixed x, y, z (Rz·Ry·Rx; help,
/// "General Settings", "Use Global XYZ Rotation Order").
/// </summary>
public readonly record struct NscPlacement(
    double X = 0, double Y = 0, double Z = 0,
    double TiltXDegrees = 0, double TiltYDegrees = 0, double TiltZDegrees = 0,
    bool GlobalXyzRotationOrder = false)
{
    /// <summary>The transform from the object's local frame to its reference object's frame.</summary>
    public RigidTransform ToTransform()
    {
        var (sx, cx) = OpticsConventions.SinCosDegrees(TiltXDegrees);
        var (sy, cy) = OpticsConventions.SinCosDegrees(TiltYDegrees);
        var (sz, cz) = OpticsConventions.SinCosDegrees(TiltZDegrees);
        var rx = RigidTransform.RotationAboutX(sx, cx);
        var ry = RigidTransform.RotationAboutY(sy, cy);
        var rz = RigidTransform.RotationAboutZ(sz, cz);
        var rotation = GlobalXyzRotationOrder
            ? RigidTransform.Compose(rz, RigidTransform.Compose(ry, rx))
            : RigidTransform.Compose(rx, RigidTransform.Compose(ry, rz));
        return RigidTransform.Compose(RigidTransform.FromTranslation(new Vector3D(X, Y, Z)), rotation);
    }
}

/// <summary>
/// One row of the non-sequential scene: a shape, its placement, its material, its faces and
/// its nesting.
/// </summary>
public sealed record NscObject
{
    public required NscGeometry Geometry { get; init; }

    public NscPlacement Placement { get; init; }

    /// <summary>The object whose frame <see cref="Placement"/> is in: 0 for the scene's own frame
    /// (OpticStudio's NSC vertex); a positive number is an absolute object number; a negative one
    /// is relative ("adding the current object number to the negative reference object number").
    /// The reference object must come earlier in the list.</summary>
    public int ReferenceObject { get; init; }

    /// <summary>OpticStudio's "Inside Of": 0 for none; positive absolute, negative relative. An
    /// object (or a ray's start, <see cref="NscLaunchRay.InsideOf"/>) inside another names it, and
    /// that object names the one it is inside, and so on; each must come earlier in the list
    /// (help, "Placing Sources Inside Objects").</summary>
    public int InsideOf { get; init; }

    public NscMaterial Material { get; init; } = NscMaterial.Blank;

    /// <summary>Per-face properties by face number; a face not listed is the default
    /// (<see cref="NscFace"/> with no coating).</summary>
    public IReadOnlyDictionary<int, NscFace>? FaceProperties { get; init; }

    /// <summary>
    /// The minimal detecting face of OP-70, a stand-in for OP-79's detectors: every ray that
    /// meets this object is absorbed and its energy counted as detected (per object, in the
    /// energy ledger). Allowed on a surface object only. Pixels, angles, transparent detectors
    /// and every detector type are OP-79.
    /// </summary>
    public bool IsDetectingStandIn { get; init; }

    public string Comment { get; init; } = "";

    // OP-80
    /// <summary>The object's Scatter To section (a Scatter To list or importance-sampling
    /// targets, help "Scatter To"); null for none.</summary>
    public Scattering.NscScatterTo? ScatterTo { get; init; }

    // OP-81
    /// <summary>The object's Volume Physics section (bulk scattering, wavelength shift,
    /// phosphors; docs/Optics-Conventions.md §77); null for none. Refractive volumes only.</summary>
    public Volume.NscVolumePhysics? VolumePhysics { get; init; }

    /// <summary>The properties of face <paramref name="face"/>.</summary>
    public NscFace Face(int face) =>
        FaceProperties is not null && FaceProperties.TryGetValue(face, out var f) ? f : Default;

    private static readonly NscFace Default = new();
}

/// <summary>
/// The objects of a non-sequential system (docs/Optics-Conventions.md §69), numbered from 1 in
/// list order as in OpticStudio's Non-Sequential Component Editor. Where objects overlap or
/// touch, the later object decides: "if a ray strikes more than one object at the exact same
/// point in space; the last object listed in the NSC Editor determines the properties of the
/// surface or volume at that point" (help, "Object Placement").
/// </summary>
public sealed record NscScene
{
    public required IReadOnlyList<NscObject> Objects { get; init; }

    /// <summary>The medium outside every object (OpticStudio: the index of the NSC surface).
    /// The system's air by default. It must not absorb.</summary>
    public ParaxialMedium Background { get; init; } = ParaxialMedium.Air;

    /// <summary>The 1-based number of the object an absolute or relative reference names, from
    /// object <paramref name="objectNumber"/>; 0 for none.</summary>
    public static int Resolve(int reference, int objectNumber) => reference < 0 ? objectNumber + reference : reference;

    /// <summary>Every problem with the scene, one line each naming the object.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Objects is null || Objects.Count == 0)
        {
            problems.Add("The non-sequential scene has no objects.");
            return problems;
        }
        for (int i = 0; i < Objects.Count; i++)
        {
            int number = i + 1;
            var o = Objects[i];
            string where = $"Object {number} ({o.Geometry?.TypeName ?? "no type"})";
            if (o.Geometry is null) { problems.Add($"{where}: it has no shape."); continue; }
            int reference = Resolve(o.ReferenceObject, number);
            if (reference < 0 || reference >= number)
                problems.Add($"{where}: the reference object {o.ReferenceObject} resolves to {reference}; it must be 0 or an earlier object (help, \"Object Placement\").");
            int inside = Resolve(o.InsideOf, number);
            if (inside < 0 || inside >= number)
                problems.Add($"{where}: \"Inside Of\" {o.InsideOf} resolves to {inside}; it must be 0 or an earlier object.");
            else if (inside > 0 && !Objects[inside - 1].Geometry.IsVolume)
                problems.Add($"{where}: it is said to be inside object {inside}, a {Objects[inside - 1].Geometry.TypeName} surface with no inside.");
            var p = o.Placement;
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)
                || !double.IsFinite(p.TiltXDegrees) || !double.IsFinite(p.TiltYDegrees) || !double.IsFinite(p.TiltZDegrees))
                problems.Add($"{where}: its position and tilts must be finite numbers.");
            if (!o.Geometry.IsVolume && o.Material.Kind == NscMaterialKind.Refractive && o.Geometry is NscSphere)
                problems.Add($"{where}: a hollow sphere must be MIRROR or ABSORB; \"refraction is not allowed\" (help, \"Sphere (non-sequential geometry objects)\").");
            if (o.IsDetectingStandIn && o.Geometry.IsVolume)
                problems.Add($"{where}: the detecting stand-in is a surface object only; objects as detectors are not built (OP-79 built the detector objects Detector Rectangle, Color and Polar).");
            if (o.Material.Kind == NscMaterialKind.Refractive && o.Material.Medium is { } m)
                m.Validate(where, problems);
            if (o.FaceProperties is { } faces)
                foreach (var key in faces.Keys)
                    if (!o.Geometry.Faces.ContainsKey(key))
                        problems.Add($"{where}: it has no face {key} (its faces are {string.Join(", ", o.Geometry.Faces.Keys)}).");
            // OP-80: scatter models and the Scatter To section.
            if (o.FaceProperties is { } scatterFaces)
                foreach (var (key, face) in scatterFaces)
                    face.Scatter?.Validate($"{where}, face {key}", problems);
            o.ScatterTo?.Validate(where, Objects.Count, problems);
            // OP-81: volume physics on refractive volumes only.
            if (o.VolumePhysics is { } volume)
            {
                if (!o.Geometry.IsVolume || o.Material.Kind != NscMaterialKind.Refractive)
                    problems.Add($"{where}: Volume Physics needs a refractive volume (a solid object of blank or glass material); this object is {(o.Geometry.IsVolume ? o.Material.Describe() : "a surface")}.");
                volume.Validate(where, problems);
            }
        }
        Background?.Validate("The background medium", problems);
        return problems;
    }

    /// <summary>The placement of every object's local frame in the scene frame, through its
    /// chain of reference objects: G′ = O′ + R′(O + R·L) (help, "Object Placement").</summary>
    public RigidTransform[] GlobalPlacements()
    {
        var problems = Validate();
        if (problems.Count > 0) throw new ArgumentException("Non-sequential scene refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        var global = new RigidTransform[Objects.Count];
        for (int i = 0; i < Objects.Count; i++)
        {
            var local = Objects[i].Placement.ToTransform();
            int reference = Resolve(Objects[i].ReferenceObject, i + 1);
            global[i] = reference == 0 ? local : RigidTransform.Compose(global[reference - 1], local);
        }
        return global;
    }

    internal static string Number(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
