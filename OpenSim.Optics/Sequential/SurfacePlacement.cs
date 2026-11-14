using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// Where every surface of a <see cref="SequentialLens"/> sits (OP-21, docs/Optics-Conventions.md
/// §4 and §15): the coordinate breaks, the surfaces' own tilts and decenters before and after,
/// coordinate returns and pick-ups resolved into one rigid transform per surface.
/// <para>
/// <b>Frames.</b> The <i>global</i> frame has object space's axes and its origin at surface 1's
/// vertex before surface 1's own tilt and decenter; rays are launched in it. Each surface j has
/// its own local frame (vertex at the origin, the surface's axis along z), in which the tracer
/// intersects it. Going from surface j − 1 to surface j:
/// <code>
/// frame(j) = frame(j − 1) · After(j − 1) · Translate(0, 0, t(j − 1)) · Before(j)
/// </code>
/// where Before(j) is the coordinate break's transform for a break, the surface's
/// <see cref="SequentialSurface.TiltBefore"/> otherwise, and After(j − 1) the resolved
/// <see cref="SequentialSurface.TiltAfter"/> (identity for a break). So a surface's thickness is
/// measured along z after all its tilts and decenters, and a break's thickness is applied last,
/// as OpticStudio's help pages "Coordinate Break" and "Tilt/Decenter" (2025 R2) state.
/// </para>
/// <para>
/// <b>Coordinate return.</b> A break returning to surface k is solved against frame(k), the
/// target's own frame (after its "before" tilt): see <see cref="CoordinateReturnMode"/>.
/// </para>
/// </summary>
public sealed class SurfacePlacement
{
    private readonly RigidTransform[] _global;
    private readonly RigidTransform[] _step;
    private readonly RigidTransform[] _vertexFrame;
    private readonly double[] _thickness;
    private readonly TiltDecenter[] _before;
    private readonly TiltDecenter[] _after;

    public SequentialLens Lens { get; }

    private SurfacePlacement(SequentialLens lens)
    {
        Lens = lens;
        var surfaces = lens.Surfaces;
        int count = surfaces.Count;
        _global = new RigidTransform[count];
        _step = new RigidTransform[count];
        _vertexFrame = new RigidTransform[count];
        _thickness = new double[count];
        _before = new TiltDecenter[count];
        _after = new TiltDecenter[count];
        var beforeT = new RigidTransform[count];
        var afterT = new RigidTransform[count];

        for (int j = 0; j < count; j++)
        {
            _thickness[j] = surfaces[j].Thickness;
            if (!surfaces[j].IsCoordinateBreak)
            {
                _before[j] = surfaces[j].TiltBefore ?? TiltDecenter.None;
                beforeT[j] = Transform(_before[j]);
            }
            _after[j] = ResolveAfter(surfaces, j);
            afterT[j] = Transform(_after[j]);
        }

        _vertexFrame[0] = lens.ObjectAtInfinity
            ? RigidTransform.Identity
            : RigidTransform.FromTranslation(new Vector3D(0, 0, -surfaces[0].Thickness));
        _global[0] = _vertexFrame[0];
        _step[0] = _global[0];

        for (int j = 1; j < count; j++)
        {
            var s = surfaces[j];
            // The frame before this surface's own tilt: the previous surface's frame, its "after"
            // tilt, then its thickness along the resulting z.
            RigidTransform reach = j == 1
                ? RigidTransform.Identity
                : RigidTransform.Compose(afterT[j - 1], RigidTransform.FromTranslation(new Vector3D(0, 0, _thickness[j - 1])));
            _vertexFrame[j] = j == 1 ? RigidTransform.Identity : RigidTransform.Compose(_global[j - 1], reach);

            if (s.CoordinateBreak is { } cb)
            {
                var (transform, values, thickness) = ResolveBreak(j, cb, _vertexFrame[j]);
                beforeT[j] = transform;
                _before[j] = values;
                _thickness[j] = thickness;
            }
            _step[j] = j == 1 ? beforeT[1] : RigidTransform.Compose(reach, beforeT[j]);
            _global[j] = j == 1 ? beforeT[1] : RigidTransform.Compose(_global[j - 1], _step[j]);
        }
    }

    /// <summary>
    /// The placement of <paramref name="lens"/>. Throws <see cref="ArgumentException"/> listing
    /// every problem of the lens, and <see cref="InvalidOperationException"/> when a coordinate
    /// return cannot be solved in the break's order.
    /// </summary>
    public static SurfacePlacement Compute(SequentialLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        var problems = lens.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Surface placement refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return new SurfacePlacement(lens);
    }

    /// <summary>For a lens already validated.</summary>
    internal static SurfacePlacement ComputeValidated(SequentialLens lens) => new(lens);

    /// <summary>Surface <paramref name="j"/>'s local frame in the global frame (for the object
    /// surface at a finite distance its plane at z = −distance; at infinity the identity).</summary>
    public RigidTransform Global(int j) => _global[j];

    /// <summary>Surface <paramref name="j"/>'s local frame in surface j − 1's (for surface 1, in
    /// the global frame).</summary>
    public RigidTransform Step(int j) => _step[j];

    /// <summary>The frame surface <paramref name="j"/>'s own tilt and decenter (or, for a
    /// coordinate break, the break) starts from, in the global frame: where its vertex would be
    /// with none.</summary>
    public RigidTransform VertexFrame(int j) => _vertexFrame[j];

    /// <summary>The thickness used after surface <paramref name="j"/>: the entered one, or the
    /// solved one for a coordinate break with an XYZ return.</summary>
    public double Thickness(int j) => _thickness[j];

    /// <summary>The coordinate break's values with any return solved; for another surface its
    /// "before" tilt and decenter (<see cref="TiltDecenter.None"/> if it has none). A solved
    /// break's trace uses the exact solved transform; these degrees are read back from it.</summary>
    public TiltDecenter ResolvedBefore(int j) => _before[j];

    /// <summary>Surface <paramref name="j"/>'s "after" tilt and decenter with any pick-up resolved.</summary>
    public TiltDecenter ResolvedAfter(int j) => _after[j];

    /// <summary>Surface <paramref name="j"/>'s local frame in the local frame of surface
    /// <paramref name="reference"/> (OpticStudio's global vertex with a global reference surface).</summary>
    public RigidTransform RelativeTo(int j, int reference) =>
        RigidTransform.Compose(_global[reference].Inverse(), _global[j]);

    /// <summary>A point in surface <paramref name="j"/>'s local frame, in the global frame.</summary>
    public Vector3D LocalToGlobal(int j, Vector3D point) => _global[j].Apply(point);

    /// <summary>A point in the global frame, in surface <paramref name="j"/>'s local frame.</summary>
    public Vector3D GlobalToLocal(int j, Vector3D point) => _global[j].InverseApply(point);

    /// <summary>True when any surface is a coordinate break or has a tilt or decenter.</summary>
    public static bool HasPlacementData(SequentialLens lens) =>
        lens.Surfaces.Any(s => s.IsCoordinateBreak || s.TiltBefore is not null || s.TiltAfter is not null);

    private static RigidTransform Transform(TiltDecenter t) => t == TiltDecenter.None ? RigidTransform.Identity : t.ToTransform();

    private static TiltDecenter ResolveAfter(IReadOnlyList<SequentialSurface> surfaces, int j)
    {
        var after = surfaces[j].TiltAfter;
        if (after is null || surfaces[j].IsCoordinateBreak) return TiltDecenter.None;
        TiltDecenter BeforeOf(int n) => surfaces[n].TiltBefore ?? TiltDecenter.None;
        return after.Kind switch
        {
            TiltAfterKind.Explicit => after.Values,
            TiltAfterKind.PickUpThisSurface => BeforeOf(j),
            TiltAfterKind.ReverseThisSurface => BeforeOf(j).Reversed(),
            TiltAfterKind.PickUpSurface => BeforeOf(after.Surface),
            TiltAfterKind.ReverseSurface => BeforeOf(after.Surface).Reversed(),
            _ => throw new ArgumentOutOfRangeException(nameof(surfaces), after.Kind, $"Surface {j}: unknown tilt-after option."),
        };
    }

    /// <summary>
    /// A break's transform, the values it reports, and the thickness after it. With a return to
    /// surface k, Q = P⁻¹·K is frame(k) seen from the frame P the break starts from; the break's
    /// rotation is Q's. Then (derived here, docs/Optics-Conventions.md §15), with R = Q's
    /// rotation, q = Q's translation and r₃ = R's third column:
    /// <list type="bullet">
    /// <item>Decenter then tilt: the break's vertex is (dx, dy, 0). XY puts it on K's z axis,
    /// (dx, dy, 0) = q + s·r₃ with s = −q_z / r₃z; XYZ adds the thickness −s, which takes the next
    /// vertex to q. Needs r₃z ≠ 0 (the target's axis not at right angles to P's).</item>
    /// <item>Tilt then decenter: the vertex is R·(dx, dy, 0) and the next one R·(dx, dy, t), so
    /// (dx, dy, t) = Rᵀ·q, always solvable.</item>
    /// </list>
    /// </summary>
    private (RigidTransform Transform, TiltDecenter Values, double Thickness) ResolveBreak(int j, CoordinateBreak cb, RigidTransform start)
    {
        var values = cb.Values;
        double thickness = Lens.Surfaces[j].Thickness;
        if (cb.Return == CoordinateReturnMode.None)
            return (Transform(values), values, thickness);

        int k = cb.ReturnToSurface;
        var q = RigidTransform.Compose(start.Inverse(), _global[k]);
        var rotation = q with { Translation = Vector3D.Zero };
        double dx = values.DecenterX, dy = values.DecenterY;
        var order = values.Order;
        if (cb.Return is CoordinateReturnMode.OrientationXY or CoordinateReturnMode.OrientationXYZ)
        {
            if (order == TiltDecenterOrder.DecenterThenTilt)
            {
                var r3 = q.Col2;
                if (Math.Abs(r3.Z) < 1e-12)
                    throw new InvalidOperationException(
                        $"Surface {j}: the coordinate return to surface {k} cannot be solved with the order 'decenter then tilt', because surface {k}'s axis is at right angles to the axis before the break; use the order 'tilt then decenter'.");
                double s = -q.Translation.Z / r3.Z;
                dx = q.Translation.X + s * r3.X;
                dy = q.Translation.Y + s * r3.Y;
                if (cb.Return == CoordinateReturnMode.OrientationXYZ) thickness = -s;
            }
            else
            {
                var local = q.InverseApplyDirection(q.Translation);
                dx = local.X;
                dy = local.Y;
                if (cb.Return == CoordinateReturnMode.OrientationXYZ) thickness = local.Z;
            }
        }
        var decenter = RigidTransform.FromTranslation(new Vector3D(dx, dy, 0));
        var transform = order == TiltDecenterOrder.DecenterThenTilt
            ? RigidTransform.Compose(decenter, rotation)
            : RigidTransform.Compose(rotation, decenter);
        var tilts = TiltDecenter.FromTransform(rotation, order, out _);
        return (transform, tilts with { DecenterX = dx, DecenterY = dy, Order = order }, thickness);
    }

    /// <summary>The placement rules of OP-21, one actionable line per problem (called by
    /// <see cref="SequentialLens.Validate"/>).</summary>
    internal static void Validate(IReadOnlyList<SequentialSurface> surfaces, bool objectAtInfinity, Func<int, string> where, List<string> problems)
    {
        int image = surfaces.Count - 1;
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            string w = where(j);
            if (s.CoordinateBreak is { } cb)
            {
                if (j == 0 || j == image)
                    problems.Add($"{w}: the object and image surfaces cannot be coordinate breaks.");
                if (s.IsMirror) problems.Add($"{w}: a coordinate break cannot be a mirror.");
                if (s.IsIgnored) problems.Add($"{w}: a coordinate break cannot be ignored.");
                if (s.Curvature != 0 || s.Conic != 0 || s.Shape is not null)
                    problems.Add($"{w}: a coordinate break is a plane; leave its curvature, conic and shape at zero.");
                if (s.Aperture is not null) problems.Add($"{w}: a coordinate break cannot have an aperture.");
                if (s.TiltBefore is not null || s.TiltAfter is not null)
                    problems.Add($"{w}: a coordinate break cannot have a surface tilt or decenter; put the values in the break.");
                CheckValues(cb.Values, w + " (coordinate break)", problems);
                if (!Enum.IsDefined(cb.Return))
                    problems.Add($"{w}: unknown coordinate return {(int)cb.Return}.");
                else if (cb.Return != CoordinateReturnMode.None && (cb.ReturnToSurface < 1 || cb.ReturnToSurface >= j))
                    problems.Add($"{w}: the coordinate return must go to a prior surface, 1 to {j - 1} (it is {cb.ReturnToSurface}).");
                continue;
            }
            if (s.TiltBefore is { } before)
            {
                if (j == 0) problems.Add($"{w}: a tilt or decenter of the object surface is not built.");
                else if (j == 1 && objectAtInfinity)
                    problems.Add($"{w}: a tilt or decenter before surface 1 is not allowed with the object at infinity; tilt the field instead.");
                CheckValues(before, w + " (tilt before)", problems);
            }
            if (s.TiltAfter is { } after)
            {
                if (j == 0 || j == image)
                    problems.Add($"{w}: the object and image surfaces cannot have a tilt or decenter after them.");
                if (!Enum.IsDefined(after.Kind))
                    problems.Add($"{w}: unknown tilt-after option {(int)after.Kind}.");
                if (after.Kind == TiltAfterKind.Explicit)
                    CheckValues(after.Values, w + " (tilt after)", problems);
                if (after.Kind is TiltAfterKind.PickUpSurface or TiltAfterKind.ReverseSurface)
                {
                    if (after.Surface < 1 || after.Surface >= j)
                        problems.Add($"{w}: the tilt after picks up a prior surface, 1 to {j - 1} (it is {after.Surface}).");
                    else if (surfaces[after.Surface] is { IsCoordinateBreak: true })
                        problems.Add($"{w}: the tilt after picks up surface {after.Surface}, which is a coordinate break; pick up a surface with a tilt before.");
                }
            }
        }
    }

    private static void CheckValues(TiltDecenter t, string where, List<string> problems)
    {
        if (!double.IsFinite(t.DecenterX) || !double.IsFinite(t.DecenterY) || !double.IsFinite(t.TiltXDegrees)
            || !double.IsFinite(t.TiltYDegrees) || !double.IsFinite(t.TiltZDegrees))
            problems.Add($"{where}: the decenters and tilts must be numbers.");
        if (!Enum.IsDefined(t.Order))
            problems.Add($"{where}: unknown tilt/decenter order {(int)t.Order}.");
    }
}
