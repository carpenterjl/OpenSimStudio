using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>Which local axis a fold mirror turns about (OpticStudio's Add Fold Mirror "tilt type").</summary>
public enum FoldTiltAxis
{
    X = 0,
    Y = 1,
}

/// <summary>
/// Lens tools that rewrite the coordinate breaks of a <see cref="SequentialLens"/> without moving
/// any surface (OP-21, docs/Optics-Conventions.md §15): OpticStudio's Tilt/Decenter Elements,
/// Add Fold Mirror, Delete Fold Mirror, Local To Global and Global To Local (help pages of those
/// names, OpticStudio 2025 R2). Each returns a new lens; surface numbers that a coordinate return
/// or a tilt pick-up names are renumbered. Every tool refuses, with the reason, a lens it cannot
/// rewrite exactly.
/// </summary>
public static class CoordinateBreakTools
{
    // ---- Tilt/Decenter Elements ----

    /// <summary>
    /// Tilts and decenters surfaces <paramref name="first"/> to <paramref name="last"/> as one
    /// rigid group about a pivot, and restores the coordinate system after the group, so every
    /// surface after it stays where it was. Inserted, in order: a dummy plane at the group's
    /// original vertex frame (the return target); when the pivot is not the origin, a break to the
    /// pivot (decenter x, y, thickness z); the break holding <paramref name="tilt"/> (thickness 0);
    /// when the pivot is not the origin, a break back from the pivot; then the group; then a break
    /// returning (orientation and XYZ, order tilt then decenter) to the dummy; then a break whose
    /// values and thickness carry the original step from the group's start to the surface after
    /// it (a plain thickness when the group had no tilts).
    /// </summary>
    /// <param name="pivot">The point of rotation, meters: in the frame the first surface's own
    /// tilt starts from (<see cref="SurfacePlacement.VertexFrame"/>), or in the global frame when
    /// <paramref name="pivotIsGlobal"/>.</param>
    public static SequentialLens TiltDecenterElements(SequentialLens lens, int first, int last, TiltDecenter tilt,
        Vector3D pivot = default, bool pivotIsGlobal = false)
    {
        var placement = SurfacePlacement.Compute(lens);
        var surfaces = lens.Surfaces;
        int image = lens.ImageIndex;
        if (first < 1 || last < first || last >= image)
            throw new ArgumentException($"Tilt/Decenter Elements: the group must run from surface 1 or later to a surface before the image (asked {first} to {last}, image {image}).");
        if (!(double.IsFinite(pivot.X) && double.IsFinite(pivot.Y) && double.IsFinite(pivot.Z)))
            throw new ArgumentException("Tilt/Decenter Elements: the pivot must be finite.");
        var start = placement.VertexFrame(first);
        var local = pivotIsGlobal ? start.InverseApply(pivot) : pivot;
        var media = lens.EffectiveMedia();

        var b = new Builder(surfaces.Count);
        for (int j = 0; j < first; j++) b.Copy(surfaces, j);
        int dummy = b.Add(new SequentialSurface { Medium = media[first - 1], Comment = "Tilt/decenter elements: return target" });
        bool pivoted = local != Vector3D.Zero;
        if (pivoted)
            b.Add(Break(new TiltDecenter(local.X, local.Y, 0, 0, 0), local.Z, "Tilt/decenter elements: to pivot"));
        b.Add(Break(tilt, 0, "Tilt/decenter elements"));
        if (pivoted)
            b.Add(Break(new TiltDecenter(-local.X, -local.Y, 0, 0, 0), -local.Z, "Tilt/decenter elements: from pivot"));
        for (int j = first; j <= last; j++) b.Copy(surfaces, j);
        b.Add(new SequentialSurface
        {
            CoordinateBreak = CoordinateBreak.ReturnTo(dummy, CoordinateReturnMode.OrientationXYZ, TiltDecenterOrder.TiltThenDecenter),
            Comment = "Tilt/decenter elements: return",
        });
        var step = RigidTransform.Compose(start.Inverse(), placement.VertexFrame(last + 1));
        var (values, thickness) = Decompose(step, TiltDecenterOrder.TiltThenDecenter, "Tilt/Decenter Elements");
        b.Add(Break(values, thickness, "Tilt/decenter elements: restore"));
        for (int j = last + 1; j < surfaces.Count; j++) b.Copy(surfaces, j);
        return b.Build(lens);
    }

    // ---- Fold mirrors ----

    /// <summary>
    /// Turns the plane dummy surface <paramref name="fold"/> into a fold mirror that turns the
    /// beam by <paramref name="reflectAngleDegrees"/> (the angle between the incident and the
    /// reflected beam, so the mirror is tilted by half of it). Inserted: a break before the mirror
    /// tilting by half the angle about the local x or y axis (thickness 0) and the same break after
    /// it, carrying the fold surface's thickness with its sign changed; the mirror's own thickness
    /// becomes 0. Every later surface is reversed so rays follow the same path: thickness and
    /// curvature change sign, and so do the x and y tilts of later breaks and surface tilts
    /// (a reflection z → −z of their frames; decenters and z tilts are unchanged).
    /// <para>
    /// OpticStudio makes the second break a pick-up of the first; this program has no pick-up of
    /// break values, so the value is copied. Refused: a fold surface that is not a plane dummy in
    /// one medium (curvature, conic or shape, a mirror, a break, its own tilt, or a change of
    /// medium), a later surface with a shape (its sag cannot be reversed here), and a later
    /// coordinate return or tilt pick-up that names a surface before the fold.
    /// </para>
    /// </summary>
    public static SequentialLens AddFoldMirror(SequentialLens lens, int fold, double reflectAngleDegrees, FoldTiltAxis axis = FoldTiltAxis.X)
    {
        _ = SurfacePlacement.Compute(lens);
        var surfaces = lens.Surfaces;
        int image = lens.ImageIndex;
        if (fold < 1 || fold >= image)
            throw new ArgumentException($"Add Fold Mirror: the fold surface must lie between the object and the image (it is {fold}).");
        if (!double.IsFinite(reflectAngleDegrees))
            throw new ArgumentException("Add Fold Mirror: the reflect angle must be a number of degrees.");
        var s = surfaces[fold];
        var media = lens.EffectiveMedia();
        if (s.IsCoordinateBreak || s.IsMirror || s.IsIgnored || s.Curvature != 0 || s.Conic != 0 || s.Shape is not null
            || s.TiltBefore is not null || s.TiltAfter is not null || !Equals(media[fold], media[fold - 1]))
            throw new ArgumentException($"Add Fold Mirror: surface {fold} must be a plane dummy surface in one medium, with no tilt of its own (place a dummy at the fold first).");
        CheckReversible(surfaces, fold + 1, "Add Fold Mirror");

        double half = reflectAngleDegrees / 2;
        var tilt = axis == FoldTiltAxis.X ? new TiltDecenter(0, 0, half, 0, 0) : new TiltDecenter(0, 0, 0, half, 0);
        var b = new Builder(surfaces.Count);
        for (int j = 0; j < fold; j++) b.Copy(surfaces, j);
        b.Add(Break(tilt, 0, "Fold"));
        b.Copy(surfaces, fold, x => x with { IsMirror = true, Thickness = 0 });
        b.Add(Break(tilt, -s.Thickness, "Fold"));
        for (int j = fold + 1; j < surfaces.Count; j++) b.Copy(surfaces, j, Reversed);
        return b.Build(lens);
    }

    /// <summary>
    /// Removes the plane fold mirror <paramref name="mirror"/> (OpticStudio's Delete Fold Mirror):
    /// the break before it when that break has zero thickness, the mirror, and the break after it
    /// when the mirror has zero thickness. The thicknesses removed (those after the mirror with
    /// their sign changed back) are added to the surface before, and every later surface is
    /// reversed as <see cref="AddFoldMirror"/> does. The result is the unfolded lens exactly when
    /// the two breaks are equal tilts about one axis, as <see cref="AddFoldMirror"/> makes them;
    /// otherwise it is what the rule gives (OpticStudio warns the same). Refused: a mirror that is
    /// not plane, is the stop or has its own tilt, and the conditions of
    /// <see cref="AddFoldMirror"/> on later surfaces.
    /// </summary>
    public static SequentialLens DeleteFoldMirror(SequentialLens lens, int mirror)
    {
        _ = SurfacePlacement.Compute(lens);
        var surfaces = lens.Surfaces;
        int image = lens.ImageIndex;
        if (mirror < 1 || mirror >= image || !surfaces[mirror].IsMirror)
            throw new ArgumentException($"Delete Fold Mirror: surface {mirror} is not a mirror.");
        var m = surfaces[mirror];
        if (m.Curvature != 0 || m.Conic != 0 || m.Shape is not null || m.TiltBefore is not null || m.TiltAfter is not null)
            throw new ArgumentException($"Delete Fold Mirror: surface {mirror} must be a plane mirror with no tilt of its own.");
        if (m.IsStop)
            throw new ArgumentException($"Delete Fold Mirror: surface {mirror} is the stop; move the stop first.");

        bool before = mirror - 1 >= 1 && surfaces[mirror - 1].IsCoordinateBreak && surfaces[mirror - 1].Thickness == 0;
        bool after = m.Thickness == 0 && mirror + 1 < image && surfaces[mirror + 1].IsCoordinateBreak;
        int firstRemoved = before ? mirror - 1 : mirror;
        int lastRemoved = after ? mirror + 1 : mirror;
        CheckReversible(surfaces, lastRemoved + 1, "Delete Fold Mirror");
        double added = (before ? surfaces[mirror - 1].Thickness : 0) - m.Thickness - (after ? surfaces[mirror + 1].Thickness : 0);

        var b = new Builder(surfaces.Count);
        for (int j = 0; j < firstRemoved; j++)
        {
            Func<SequentialSurface, SequentialSurface>? change = j == firstRemoved - 1 ? x => x with { Thickness = x.Thickness + added } : null;
            b.Copy(surfaces, j, change);
        }
        for (int j = firstRemoved; j <= lastRemoved; j++) b.Drop(j);
        for (int j = lastRemoved + 1; j < surfaces.Count; j++) b.Copy(surfaces, j, Reversed);
        return b.Build(lens);
    }

    // ---- Local To Global / Global To Local ----

    /// <summary>
    /// Places every group of surfaces from <paramref name="first"/> to <paramref name="last"/>
    /// relative to surface <paramref name="reference"/> (OpticStudio's Local To Global). A group
    /// starts at <paramref name="first"/> and after every coordinate break in the range; the
    /// breaks in the range are removed and each group gets three in front of it: a return
    /// (orientation and XYZ) to the reference, a break that moves by the group's x, y and z in the
    /// reference's frame (decenters and thickness), and a break that turns it (tilts only, order
    /// decenter then tilt). No surface moves.
    /// </summary>
    public static SequentialLens LocalToGlobal(SequentialLens lens, int first, int last, int reference)
    {
        var placement = SurfacePlacement.Compute(lens);
        var surfaces = lens.Surfaces;
        CheckRange(lens, first, last, "Local To Global");
        if (reference < 1 || reference >= first)
            throw new ArgumentException($"Local To Global: the reference surface must be 1 or later and before surface {first} (it is {reference}).");
        var referenceFrame = placement.Global(reference);

        var b = new Builder(surfaces.Count);
        for (int j = 0; j < first; j++) b.Copy(surfaces, j);
        bool groupStart = true;
        for (int j = first; j <= last; j++)
        {
            if (surfaces[j].IsCoordinateBreak)
            {
                b.Drop(j);
                groupStart = true;
                continue;
            }
            if (groupStart)
            {
                var a = RigidTransform.Compose(referenceFrame.Inverse(), placement.VertexFrame(j));
                var turn = TiltDecenter.FromTransform(a with { Translation = Vector3D.Zero }, TiltDecenterOrder.DecenterThenTilt, out _);
                b.Add(new SequentialSurface
                {
                    CoordinateBreak = CoordinateBreak.ReturnTo(b.NewIndex(reference), CoordinateReturnMode.OrientationXYZ, TiltDecenterOrder.TiltThenDecenter),
                    Comment = "Local to global: return",
                });
                b.Add(Break(new TiltDecenter(a.Translation.X, a.Translation.Y, 0, 0, 0), a.Translation.Z, "Local to global: position"));
                b.Add(Break(turn with { DecenterX = 0, DecenterY = 0 }, 0, "Local to global: rotation"));
                groupStart = false;
            }
            b.Copy(surfaces, j);
        }
        for (int j = last + 1; j < surfaces.Count; j++) b.Copy(surfaces, j);
        return b.Build(lens);
    }

    /// <summary>
    /// Replaces every run of consecutive coordinate breaks between <paramref name="first"/> and
    /// <paramref name="last"/> by one break in <paramref name="order"/> with explicit values
    /// (OpticStudio's Global To Local), so each group is placed relative to the one before it.
    /// The surface before a run keeps its thickness; the single break takes the rest of the step
    /// as its values and thickness. No surface moves. In the order decenter then tilt a run whose
    /// net turn puts the new axis at right angles to the old one cannot be written as one break;
    /// that is refused with the advice to use the other order.
    /// </summary>
    public static SequentialLens GlobalToLocal(SequentialLens lens, int first, int last, TiltDecenterOrder order = TiltDecenterOrder.DecenterThenTilt)
    {
        var placement = SurfacePlacement.Compute(lens);
        var surfaces = lens.Surfaces;
        CheckRange(lens, first, last, "Global To Local");

        var b = new Builder(surfaces.Count);
        for (int j = 0; j <= first; j++) b.Copy(surfaces, j);
        int i = first + 1;
        while (i <= last)
        {
            if (!surfaces[i].IsCoordinateBreak)
            {
                b.Copy(surfaces, i);
                i++;
                continue;
            }
            int runStart = i, previous = i - 1;
            while (surfaces[i].IsCoordinateBreak) i++;   // the last surface is not a break
            // S: the previous surface's frame after its "after" tilt; then its thickness; then X·T(t).
            var exit = RigidTransform.Compose(placement.Global(previous), placement.ResolvedAfter(previous).ToTransform());
            var thickness = RigidTransform.FromTranslation(new Vector3D(0, 0, placement.Thickness(previous)));
            var step = RigidTransform.Compose(RigidTransform.Compose(exit, thickness).Inverse(), placement.VertexFrame(i));
            var (values, t) = Decompose(step, order, "Global To Local");
            for (int r = runStart; r < i; r++) b.Drop(r);
            b.Add(Break(values, t, surfaces[runStart].Comment));
        }
        for (int j = last + 1; j < surfaces.Count; j++) b.Copy(surfaces, j);
        return b.Build(lens);
    }

    /// <summary>
    /// The break values (in <paramref name="order"/>) and the thickness after the break that
    /// reproduce <paramref name="m"/> = Break·Translate(0, 0, t). With R the rotation and q the
    /// translation of m (derived here): tilt then decenter gives (dx, dy, t) = Rᵀ·q; decenter
    /// then tilt gives t = q_z / R₃₃, dx = q_x − R₁₃·t, dy = q_y − R₂₃·t, which needs R₃₃ ≠ 0.
    /// </summary>
    public static (TiltDecenter Values, double Thickness) Decompose(RigidTransform m, TiltDecenterOrder order, string tool = "Coordinate break")
    {
        ArgumentNullException.ThrowIfNull(m);
        var rotation = m with { Translation = Vector3D.Zero };
        var tilts = TiltDecenter.FromTransform(rotation, order, out _);
        if (order == TiltDecenterOrder.TiltThenDecenter)
        {
            var d = m.InverseApplyDirection(m.Translation);
            return (tilts with { DecenterX = d.X, DecenterY = d.Y, Order = order }, d.Z);
        }
        var r3 = m.Col2;
        if (Math.Abs(r3.Z) < 1e-12)
            throw new InvalidOperationException($"{tool}: the new axis is at right angles to the old one, which one break in the order 'decenter then tilt' cannot hold; use 'tilt then decenter'.");
        double t = m.Translation.Z / r3.Z;
        return (tilts with { DecenterX = m.Translation.X - r3.X * t, DecenterY = m.Translation.Y - r3.Y * t, Order = order }, t);
    }

    // ---- Helpers ----

    private static SequentialSurface Break(TiltDecenter values, double thickness, string comment) => new()
    {
        CoordinateBreak = new CoordinateBreak(values),
        Thickness = thickness,
        Comment = comment,
    };

    private static void CheckRange(SequentialLens lens, int first, int last, string tool)
    {
        int image = lens.ImageIndex;
        if (first < 1 || last <= first || last > image)
            throw new ArgumentException($"{tool}: the first surface must precede the last, from surface 1 to the image (asked {first} to {last}).");
        if (lens.Surfaces[first].IsCoordinateBreak || lens.Surfaces[last].IsCoordinateBreak)
            throw new ArgumentException($"{tool}: neither the first nor the last surface may be a coordinate break.");
    }

    /// <summary>The surfaces from <paramref name="from"/> on can be reversed by
    /// <see cref="Reversed"/>, and none names a surface before <paramref name="from"/>.</summary>
    private static void CheckReversible(IReadOnlyList<SequentialSurface> surfaces, int from, string tool)
    {
        for (int j = from; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            if (s.Shape is not null)
                throw new ArgumentException($"{tool}: surface {j} has the shape '{s.Shape.TypeName}', which cannot be reversed here.");
            if (s.CoordinateBreak is { Return: not CoordinateReturnMode.None } cb && cb.ReturnToSurface < from)
                throw new ArgumentException($"{tool}: surface {j}'s coordinate return goes to surface {cb.ReturnToSurface}, across the fold; remove it first.");
            if (s.TiltAfter is { Kind: TiltAfterKind.PickUpSurface or TiltAfterKind.ReverseSurface } after && after.Surface < from)
                throw new ArgumentException($"{tool}: surface {j}'s tilt after picks up surface {after.Surface}, across the fold; remove it first.");
        }
    }

    /// <summary>A surface seen through the reflection z → −z of its frames: thickness, curvature
    /// and the x and y tilts change sign.</summary>
    private static SequentialSurface Reversed(SequentialSurface s)
    {
        static TiltDecenter Flip(TiltDecenter t) => t with { TiltXDegrees = -t.TiltXDegrees, TiltYDegrees = -t.TiltYDegrees };
        return s with
        {
            Thickness = -s.Thickness,
            Curvature = -s.Curvature,
            CoordinateBreak = s.CoordinateBreak is { } cb ? cb with { Values = Flip(cb.Values) } : null,
            TiltBefore = s.TiltBefore is { } before ? Flip(before) : null,
            TiltAfter = s.TiltAfter is { Kind: TiltAfterKind.Explicit } after ? after with { Values = Flip(after.Values) } : s.TiltAfter,
        };
    }

    /// <summary>Builds a new surface list, keeping the old-to-new map so the surface numbers in
    /// coordinate returns and tilt pick-ups follow their surfaces. Returns and pick-ups always
    /// name earlier surfaces, which are mapped by the time a surface naming them is copied.</summary>
    private sealed class Builder
    {
        private readonly List<SequentialSurface> _surfaces = new();
        private readonly int[] _map;

        public Builder(int count)
        {
            _map = new int[count];
            Array.Fill(_map, -1);
        }

        public int NewIndex(int old) => _map[old] >= 0 ? _map[old]
            : throw new InvalidOperationException($"Surface {old} has not been placed yet.");

        public int Add(SequentialSurface s)
        {
            _surfaces.Add(s);
            return _surfaces.Count - 1;
        }

        public void Drop(int old) => _map[old] = -1;

        public void Copy(IReadOnlyList<SequentialSurface> surfaces, int old, Func<SequentialSurface, SequentialSurface>? change = null)
        {
            var s = surfaces[old];
            if (s.CoordinateBreak is { Return: not CoordinateReturnMode.None } cb)
                s = s with { CoordinateBreak = cb with { ReturnToSurface = Mapped(cb.ReturnToSurface, old, "coordinate return") } };
            if (s.TiltAfter is { Kind: TiltAfterKind.PickUpSurface or TiltAfterKind.ReverseSurface } after)
                s = s with { TiltAfter = after with { Surface = Mapped(after.Surface, old, "tilt pick-up") } };
            if (change is not null) s = change(s);
            _map[old] = _surfaces.Count;
            _surfaces.Add(s);
        }

        private int Mapped(int target, int from, string what)
        {
            if (target < 0 || target >= _map.Length || _map[target] < 0)
                throw new ArgumentException($"Surface {from}'s {what} names surface {target}, which this tool removes or rewrites; remove the {what} first.");
            return _map[target];
        }

        public SequentialLens Build(SequentialLens original)
        {
            var lens = original with { Surfaces = _surfaces.ToArray() };
            var problems = lens.Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException("The rewritten lens is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            return lens;
        }
    }
}
