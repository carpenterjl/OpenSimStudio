using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Composite;

/// <summary>
/// The effective shape of a surface with composite add-ons (OP-32, docs/Optics-Conventions.md
/// §58): z(x, y) = z_base(x, y) + Σₖ δₖ(x, y) over the active add-ons, as OpticStudio's help
/// defines the Composite Stack ("The Base surface profile will then be the summation of all the
/// Add-on profiles, plus its own profile", "Composite Surface").
/// <para>
/// <b>An add-on's contribution δ.</b> The add-on's sag f(x′, y′) is defined in its own frame,
/// placed in the base frame by its decenter and tilts (<see cref="CompositeAddOn.Placement"/>,
/// transform T). δ(x, y) is the distance along the base's z, on the line through (x, y, 0), from
/// the add-on's tilted vertex plane z′ = 0 to the add-on's surface: in the add-on frame the line
/// is q + t·a with q = T⁻¹(x, y, 0) and a = T⁻¹ẑ, it meets the plane at s = −q_z/a_z, and δ is
/// the add-on's own intersection distance from (q_x + s·a_x, q_y + s·a_y, 0) along a (the same
/// routine that traces the add-on's type). So a plane add-on adds nothing in any placement, an
/// untilted add-on adds f(x − dx, y − dy) exactly (that case is evaluated directly), and a
/// tilted one adds its departure from its own tilted reference plane. The slopes are
/// ∂δ/∂x = −n_x/n_z + p_x/p_z (and so for y), n the add-on's normal at the met point and p its
/// plane normal Tẑ, both in the base frame. The help states that the tilts are taken with
/// respect to the base's coordinate system but not how a tilted sag is summed; this reading is
/// Conventions §10 item 54.
/// </para>
/// <para>
/// The shape is a <see cref="SagSurfaceShape"/>, so the trace intersects it with the shared
/// damped-Newton routine (<see cref="AsphereIntersection"/>) starting at the base conic's root,
/// and its normal is (−∂z/∂x, −∂z/∂y, 1)/|…| from the summed gradient. A point where the base or
/// any active add-on has no sag is outside the surface (a ray miss). A zero contribution leaves
/// the sum's bits as the base's (<see cref="Add"/>), so an add-on whose sag is zero everywhere
/// changes no traced bit on a sag-type base.
/// </para>
/// Immutable and safe to share between threads.
/// </summary>
public sealed class CompositeSurfaceShape : SagSurfaceShape
{
    private readonly Term[] _terms;

    /// <param name="baseSurface">The base surface (its curvature, conic, shape and semi-diameter).</param>
    /// <param name="active">The active add-ons with their 1-based numbers in the surface's list.</param>
    internal CompositeSurfaceShape(SequentialSurface baseSurface, IReadOnlyList<(int Number, CompositeAddOn AddOn)> active)
    {
        ArgumentNullException.ThrowIfNull(baseSurface);
        BaseShape = baseSurface.Shape;
        _terms = active.Select(a => new Term(a.Number, a.AddOn, a.AddOn.AsSurface(baseSurface.SemiDiameter))).ToArray();
    }

    /// <summary>The base surface's own shape, null for the Standard conic.</summary>
    public SurfaceShape? BaseShape { get; }

    /// <summary>The active add-ons, in order.</summary>
    public IReadOnlyList<CompositeAddOn> AddOns => _terms.Select(t => t.AddOn).ToArray();

    public override string TypeName =>
        $"{BaseShape?.TypeName ?? SavedSurfaces.StandardType} + {_terms.Length} composite add-on{(_terms.Length == 1 ? "" : "s")}";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        if (!TrySagAndSlopes(BaseShape, surface, x, y, out z, out dzdx, out dzdy)) return Fail(out z, out dzdx, out dzdy);
        foreach (var term in _terms)
        {
            if (!term.TryDeparture(x, y, out double d, out double dx, out double dy)) return Fail(out z, out dzdx, out dzdy);
            z = Add(z, d);
            dzdx = Add(dzdx, dx);
            dzdy = Add(dzdy, dy);
        }
        return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy) || Fail(out z, out dzdx, out dzdy);
    }

    /// <summary>The first-order model: the base's, with each add-on's own vertex curvature added
    /// (the sags add, so their second-order terms add). An add-on that is decentered or tilted
    /// has no place in the rotationally symmetric paraxial engine and is refused by name, as a
    /// decentered or tilted surface with power is (§23).</summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        var eq = BaseShape?.ParaxialEquivalent(surface) ?? new ParaxialShapeEquivalent { Curvature = surface.Curvature };
        foreach (var term in _terms)
        {
            var a = term.AddOn;
            if (a.IsPlaced)
                throw new ArgumentException(
                    $"Composite add-on {term.Number} ({a.TypeName}) is decentered ({a.Placement.DecenterX:R}, {a.Placement.DecenterY:R}) m and tilted " +
                    $"({a.Placement.TiltXDegrees:R}°, {a.Placement.TiltYDegrees:R}°, {a.Placement.TiltZDegrees:R}°) relative to its base, so the surface is not " +
                    "rotationally symmetric about its vertex and the paraxial engine has no model of it. Paraxial data, ray aiming and solves are not available for this lens.");
            var p = a.Shape?.ParaxialEquivalent(term.Surface) ?? new ParaxialShapeEquivalent { Curvature = a.Curvature };
            if (p.ThinLensPower != 0 || p.SlopeMatrix is not null)
                throw new ArgumentException($"Composite add-on {term.Number} ({a.TypeName}) has a first-order model that is not a vertex curvature; it cannot be added to a sag.");
            eq = eq with { Curvature = Add(eq.Curvature, p.Curvature) };
        }
        return eq;
    }

    public override string? OpdLimitation(SequentialSurface surface) => BaseShape?.OpdLimitation(surface);

    /// <summary>a + b, but a itself (every bit, the sign of a zero included) when b is zero.</summary>
    private static double Add(double a, double b) => b == 0 ? a : a + b;

    private static bool Fail(out double z, out double zx, out double zy)
    {
        z = zx = zy = double.NaN;
        return false;
    }

    /// <summary>
    /// The sag and slopes of <paramref name="shape"/> on <paramref name="surface"/> at (x, y):
    /// the Standard conic in closed form, a sag type's own slopes, any other shape's sag with
    /// the slopes from its normal. False where any is not defined.
    /// </summary>
    internal static bool TrySagAndSlopes(SurfaceShape? shape, SequentialSurface surface, double x, double y,
        out double z, out double zx, out double zy)
    {
        switch (shape)
        {
            case null:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr)) return Fail(out z, out zx, out zy);
                if (r > 0)
                {
                    zx = dzdr * (x / r);
                    zy = dzdr * (y / r);
                }
                else zx = zy = 0;
                return true;
            }
            case SagSurfaceShape sag:
                return sag.TrySag(x, y, surface, out z, out zx, out zy) || Fail(out z, out zx, out zy);
            default:
            {
                z = shape.Sag(x, y, surface);
                if (!double.IsFinite(z)) return Fail(out z, out zx, out zy);
                var n = shape.Normal(new Vector3D(x, y, z), surface);
                if (!(n.Z > 0)) return Fail(out z, out zx, out zy);
                zx = -n.X / n.Z;
                zy = -n.Y / n.Z;
                return double.IsFinite(zx) && double.IsFinite(zy) || Fail(out z, out zx, out zy);
            }
        }
    }

    /// <summary>One active add-on with its frame.</summary>
    private sealed class Term
    {
        private readonly RigidTransform _frame;
        private readonly Vector3D _axis;       // T⁻¹ẑ: the base z in the add-on frame
        private readonly double _planeSx, _planeSy;   // p_x/p_z, p_y/p_z with p = Tẑ

        public Term(int number, CompositeAddOn addOn, SequentialSurface surface)
        {
            Number = number;
            AddOn = addOn;
            Surface = surface;
            _frame = addOn.Placement.ToTransform();
            _axis = _frame.InverseApplyDirection(Vector3D.UnitZ);
            var p = _frame.ApplyDirection(Vector3D.UnitZ);
            _planeSx = p.X / p.Z;
            _planeSy = p.Y / p.Z;
        }

        public int Number { get; }
        public CompositeAddOn AddOn { get; }
        public SequentialSurface Surface { get; }

        public bool TryDeparture(double x, double y, out double d, out double dx, out double dy)
        {
            if (!AddOn.IsTilted)
                return TrySagAndSlopes(AddOn.Shape, Surface, x - AddOn.Placement.DecenterX, y - AddOn.Placement.DecenterY, out d, out dx, out dy);

            var a = _axis;
            if (!(Math.Abs(a.Z) > 0)) return Fail(out d, out dx, out dy);
            var q = _frame.InverseApply(new Vector3D(x, y, 0));
            double s = -q.Z / a.Z;
            double u = q.X + s * a.X, v = q.Y + s * a.Y;
            Vector3D point;
            double delta;
            bool met = AddOn.Shape is { } shape
                ? shape.Intersect(u, v, a, Surface, out point, out delta)
                : ConicIntersection.Intersect(u, v, a, AddOn.Curvature, AddOn.Conic, out point, out delta);
            if (!met || !double.IsFinite(delta)) return Fail(out d, out dx, out dy);
            var local = AddOn.Shape is { } sh ? sh.Normal(point, Surface) : ConicIntersection.Normal(point, AddOn.Curvature, AddOn.Conic);
            var n = _frame.ApplyDirection(local);
            if (!(Math.Abs(n.Z) > 0)) return Fail(out d, out dx, out dy);
            d = delta;
            dx = -n.X / n.Z + _planeSx;
            dy = -n.Y / n.Z + _planeSy;
            return double.IsFinite(dx) && double.IsFinite(dy) || Fail(out d, out dx, out dy);
        }
    }
}
