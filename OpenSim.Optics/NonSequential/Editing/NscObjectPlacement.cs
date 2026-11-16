using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// Placement arithmetic on saved object rows, in lens units (a rigid motion does not care about
/// the length unit): every object's global frame through its chain of reference objects, and the
/// position and tilts that reproduce a given frame (plan item OP-71, docs/Optics-Conventions.md
/// §71; the placement itself is §69 and <see cref="NscPlacement"/>).
/// </summary>
public static class NscObjectPlacement
{
    /// <summary>The saved row's placement relative to its reference object (lens units).</summary>
    public static NscPlacement Local(OpticsNscObjectSettings row) => new(
        row.X ?? 0, row.Y ?? 0, row.Z ?? 0, row.TiltX ?? 0, row.TiltY ?? 0, row.TiltZ ?? 0, row.GlobalXyzRotationOrder ?? false);

    /// <summary>The transform from each object's local frame to the scene frame, G′ = O′ + R′(O + R·L)
    /// (help, "Object Placement"). Throws <see cref="ArgumentException"/> naming the object when a
    /// reference does not resolve to an earlier object.</summary>
    public static RigidTransform[] Globals(IReadOnlyList<OpticsNscObjectSettings> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var global = new RigidTransform[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i] ?? new OpticsNscObjectSettings();
            var local = Local(row);
            if (!double.IsFinite(local.X) || !double.IsFinite(local.Y) || !double.IsFinite(local.Z)
                || !double.IsFinite(local.TiltXDegrees) || !double.IsFinite(local.TiltYDegrees) || !double.IsFinite(local.TiltZDegrees))
                throw new ArgumentException($"Object {i + 1}: its position and tilts must be finite numbers.");
            int reference = NscScene.Resolve(row.ReferenceObject ?? 0, i + 1);
            if (reference < 0 || reference > i)
                throw new ArgumentException($"Object {i + 1}: the reference object {row.ReferenceObject} resolves to {reference}; it must be 0 or an earlier object (help, \"Object Placement\").");
            var t = local.ToTransform();
            global[i] = reference == 0 ? t : RigidTransform.Compose(global[reference - 1], t);
        }
        return global;
    }

    /// <summary>
    /// The decenters and tilts (degrees) that give <paramref name="transform"/> in the asked
    /// rotation order: the translation is the decenter (§69 decenters first), and the rotation is
    /// split as Rx(a)·Ry(b)·Rz(c) for the default order or Rz(c)·Ry(b)·Rx(a) for
    /// <paramref name="globalXyzOrder"/>. The split is <see cref="TiltDecenter.FromTransform"/>'s
    /// (Rx·Ry·Rz); the global order uses (Rz·Ry·Rx)ᵀ = Rx(−a)·Ry(−b)·Rz(−c). At a y tilt of ±90°
    /// only a ± c is defined and the z tilt comes back as zero; the rotation is still reproduced.
    /// </summary>
    public static NscPlacement FromTransform(RigidTransform transform, bool globalXyzOrder)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var rotation = new RigidTransform(transform.Col0, transform.Col1, transform.Col2, Vector3D.Zero);
        double a, b, c;
        if (!globalXyzOrder)
        {
            var td = TiltDecenter.FromTransform(rotation, TiltDecenterOrder.DecenterThenTilt, out _);
            (a, b, c) = (td.TiltXDegrees, td.TiltYDegrees, td.TiltZDegrees);
        }
        else
        {
            var td = TiltDecenter.FromTransform(rotation.Inverse(), TiltDecenterOrder.DecenterThenTilt, out _);
            (a, b, c) = (-td.TiltXDegrees, -td.TiltYDegrees, -td.TiltZDegrees);
        }
        var d = transform.Translation;
        return new NscPlacement(d.X, d.Y, d.Z, a + 0.0, b + 0.0, c + 0.0, globalXyzOrder);
    }

    /// <summary>
    /// OpticStudio's Modify Reference Object (help, "Modify Reference Object"): objects
    /// <paramref name="first"/> to <paramref name="last"/> (1-based) are re-referenced to
    /// <paramref name="referTo"/>, their position and tilts recomputed "to retain their current
    /// position and orientation". "The selected reference object must precede the first object
    /// selected" (0, the scene frame, is allowed). An object whose reference was relative gets a
    /// relative reference to the new object, an absolute one an absolute reference (the help does
    /// not say; this program keeps the form). Each object keeps its rotation order. Every other
    /// field is unchanged, and so is every object outside the range: an object referring to one in
    /// the range keeps its global place because that object's global frame is unchanged.
    /// </summary>
    public static List<OpticsNscObjectSettings> ModifyReference(IReadOnlyList<OpticsNscObjectSettings> rows, int first, int last, int referTo)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (first < 1 || last < first || last > rows.Count)
            throw new ArgumentOutOfRangeException(nameof(first), $"Modify Reference Object: the range {first} to {last} is not within objects 1 to {rows.Count}.");
        if (referTo < 0 || referTo >= first)
            throw new ArgumentOutOfRangeException(nameof(referTo), referTo, $"Modify Reference Object: the new reference object must precede the first object selected ({first}); it is {referTo}.");
        var global = Globals(rows);
        var result = rows.Select(r => r ?? new OpticsNscObjectSettings()).ToList();
        var inverse = referTo == 0 ? RigidTransform.Identity : global[referTo - 1].Inverse();
        for (int number = first; number <= last; number++)
        {
            var row = result[number - 1];
            bool globalOrder = row.GlobalXyzRotationOrder ?? false;
            var p = FromTransform(RigidTransform.Compose(inverse, global[number - 1]), globalOrder);
            int reference = referTo == 0 ? 0 : (row.ReferenceObject ?? 0) < 0 ? referTo - number : referTo;
            result[number - 1] = row with
            {
                ReferenceObject = reference,
                X = p.X, Y = p.Y, Z = p.Z,
                TiltX = p.TiltXDegrees, TiltY = p.TiltYDegrees, TiltZ = p.TiltZDegrees,
            };
        }
        return result;
    }
}
