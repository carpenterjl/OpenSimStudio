using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Diffractive;

/// <summary>
/// The one mechanism of OP-26's diffractive, grating and holographic surfaces
/// (docs/Optics-Conventions.md §57): a substrate that gives the shape (the Standard conic of the
/// surface's own radius and conic, or another <see cref="SurfaceShape"/> such as the Even
/// Asphere or the Toroidal), and a phase Φ(P) in radians, diffraction order included, that turns
/// the ray and adds to its optical path.
/// <para>
/// <b>The rule.</b> OpticStudio's help ("Binary 1 (sequential surfaces, lens data editor)",
/// section Diffractive Surfaces; 2025 R2, saved under plans/sources/optics/op-26-help/) writes it
/// for a plane surface as l′ = l + (λ/2π)·∂Φ/∂x, m′ = m + (λ/2π)·∂Φ/∂y and "Optical Path =
/// Optical Path + (λ/2π)·Φ", and for the ruled grating as n₂·sin θ₂ − n₁·sin θ₁ = M·λ·T
/// ("Diffraction Grating (sequential surfaces, lens data editor)"). The Hologram 1 page gives
/// Welford's vector form n̂ × (r′ₒ − r′ᵣ) = (m·λp/λc)·n̂ × (rₒ − rᵣ), with the readout and
/// diffracted unit vectors multiplied by their indices ("This is already handled by
/// OpticStudio"). Welford's book (Aberrations of Optical Systems, 1986) is not on this machine,
/// so the general form used here is derived, and agrees with all three:
/// </para>
/// <list type="number">
/// <item>The phase is a scalar field whose 3-D gradient ∇Φ at the intersection point P is given
/// by the type: (∂Φ/∂x, ∂Φ/∂y, 0) for a phase written in the surface's x and y (a binary, Zernike
/// or grating phase, "All ray-intercept points are divided by this number to determine the x and
/// y coordinates"), or m·(2π/λc)·(rₒ − rᵣ) for a two-point hologram.</item>
/// <item>Its surface gradient is the projection on the tangent plane, G = ∇Φ − (∇Φ·n̂)·n̂: for a
/// tangent vector t, G·t = ∇Φ·t is the rate of change of Φ along the surface in that direction.
/// On a plane this is (∂Φ/∂x, ∂Φ/∂y, 0); for the hologram it is n̂ × (… ) × n̂ of Welford's
/// form.</item>
/// <item>The tangential part of the optical direction vector n·d changes by (λ/2π)·G:
/// v_t = n·(d − (d·n̂)n̂) + (λ/2π)·G. With G = 0 this is Snell's law (the tangential part of n·d
/// is conserved).</item>
/// <item>The normal part follows from |n′·d′| = n′: n′·d′ = v_t + s·√(n′² − |v_t|²)·n̂, with s the
/// sign of d·n̂ in transmission and its opposite in reflection (material MIRROR, n′ = n). When
/// n′² &lt; |v_t|² the order does not propagate: <see cref="RayStatus.EvanescentOrder"/> (OpticStudio
/// reports "Ray missed surface"; this program has the more specific code). With no phase gradient
/// the same condition is total internal reflection and is reported as such.</item>
/// <item>The optical path gains (λ/2π)·Φ(P), λ in the same units as the path.</item>
/// </list>
/// <para>
/// λ is the wavelength in the system's air (<see cref="SurfaceRedirectInput.AirWavelength"/>),
/// because the indices and the optical path are relative to that air. A phase "independent of
/// the wavelength" (Zernike Fringe Phase) thus deviates rays in proportion to λ, which is what
/// the help says.
/// </para>
/// <para>
/// <b>Paraxial model.</b> Φ expanded along the surface at the vertex: a slope (a grating, a tilt
/// term, an off-axis hologram) deviates the axis ray and has no place in the rotationally
/// symmetric engine, so <see cref="ParaxialEquivalent"/> refuses it with the surface named; a
/// mixed ∂²Φ/∂x∂y is refused for the same reason as the anamorphic sags' (§23); ∂²Φ/∂y² gives the
/// wavelength-dependent power φ = −λ·Φ″/(2π) (<see cref="ParaxialShapeEquivalent.DiffractivePowerPerWavelength"/>),
/// added to the substrate's own model.
/// </para>
/// Instances are immutable and safe to share between threads.
/// </summary>
public abstract class DiffractiveShape : SurfaceShape
{
    private const double TwoPi = 2 * Math.PI;

    /// <param name="substrate">The shape that gives the sag, or null for the Standard conic of
    /// the surface's radius and conic.</param>
    protected DiffractiveShape(SurfaceShape? substrate)
    {
        Substrate = substrate;
    }

    /// <summary>The substrate shape, or null for the Standard conic.</summary>
    public SurfaceShape? Substrate { get; }

    /// <summary>
    /// The phase Φ at <paramref name="point"/> (local frame, meters, on the surface), radians
    /// with the diffraction order included, and its 3-D gradient (radians per meter, before
    /// projection on the tangent plane). False where the phase is not defined (a variable line
    /// space grating's square root, a value that is not finite); the ray then stops with
    /// <see cref="RayStatus.MissedSurface"/>.
    /// </summary>
    public abstract bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient);

    /// <summary>
    /// Φ expanded along the surface at the vertex: its value, its slopes along x and y and its
    /// second derivatives along the surface (radians, meters), for the paraxial model.
    /// <paramref name="yCurvature"/> is the substrate's y–z vertex curvature (1/m), which a phase
    /// defined in 3-D (a hologram) needs.
    /// </summary>
    internal abstract VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature);

    // ---- the substrate ----

    public override double Sag(double x, double y, SequentialSurface surface)
    {
        if (Substrate is not null) return Substrate.Sag(x, y, surface);
        return ConicSag.TryEvaluate(Math.Sqrt(x * x + y * y), surface.Curvature, surface.Conic, out double z, out _) ? z : double.NaN;
    }

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance) =>
        Substrate is not null
            ? Substrate.Intersect(x, y, direction, surface, out point, out distance)
            : ConicIntersection.Intersect(x, y, direction, surface.Curvature, surface.Conic, out point, out distance);

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) =>
        Substrate is not null
            ? Substrate.Normal(point, surface)
            : ConicIntersection.Normal(point, surface.Curvature, surface.Conic);

    // ---- the rule ----

    public sealed override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        direction = input.Incoming;
        extraOpticalPath = 0;
        failure = RayStatus.Ok;
        double lambda = input.AirWavelength;
        if (!(lambda > 0) || !double.IsFinite(lambda))
            throw new InvalidOperationException(
                $"{TypeName} at surface {input.SurfaceIndex}: the trace did not give the wavelength in the system's air ({lambda}); a diffractive surface cannot turn the ray without it.");
        if (!TryPhase(input.Point, input.Surface, out double phase, out var gradient)
            || !double.IsFinite(phase) || !double.IsFinite(gradient.X) || !double.IsFinite(gradient.Y) || !double.IsFinite(gradient.Z))
        {
            failure = RayStatus.MissedSurface;
            return true;
        }
        if (!Diffract(input.Incoming, input.Normal, input.IndexBefore, input.IndexAfter, input.Surface.IsMirror,
                lambda / TwoPi, gradient, out direction, out failure))
            return true;
        extraOpticalPath = lambda / TwoPi * phase;
        return true;
    }

    /// <summary>
    /// The vector rule of this class's summary (steps 2–4): the unit direction after a surface of
    /// unit normal <paramref name="normal"/> (either sense) whose phase has the 3-D gradient
    /// <paramref name="gradient"/>, with <paramref name="scale"/> = λ/2π (meters). False with
    /// <see cref="RayStatus.EvanescentOrder"/>, or <see cref="RayStatus.TotalInternalReflection"/>
    /// when the projected gradient is zero, when the order does not propagate.
    /// </summary>
    public static bool Diffract(Vector3D incoming, Vector3D normal, double indexBefore, double indexAfter, bool mirror,
        double scale, Vector3D gradient, out Vector3D direction, out RayStatus failure)
    {
        double dn = Vector3D.Dot(incoming, normal);
        var g = gradient - Vector3D.Dot(gradient, normal) * normal;
        var vt = indexBefore * (incoming - dn * normal) + scale * g;
        double nAfter = mirror ? indexBefore : indexAfter;
        double nn = nAfter * nAfter - vt.LengthSquared;
        if (!(nn >= 0))
        {
            direction = incoming;
            failure = g.LengthSquared == 0 ? RayStatus.TotalInternalReflection : RayStatus.EvanescentOrder;
            return false;
        }
        double side = dn < 0 ? -1 : 1;
        if (mirror) side = -side;
        var v = vt + side * Math.Sqrt(nn) * normal;
        direction = v / v.Length;
        failure = RayStatus.Ok;
        return true;
    }

    // ---- the paraxial model ----

    public sealed override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        var substrate = Substrate?.ParaxialEquivalent(surface) ?? new ParaxialShapeEquivalent { Curvature = surface.Curvature };
        var t = PhaseAtVertex(surface, substrate.Curvature);
        if (t.Gx != 0 || t.Gy != 0)
            throw new ArgumentException($"{TypeName}: the phase has the slope ({t.Gx:R}, {t.Gy:R}) rad/m at the vertex (a grating, a tilt term or an off-axis construction point), so it deviates the axis ray and the rotationally symmetric paraxial engine has no model of the surface. Paraxial data, ray aiming and solves are not available for this lens; the real ray trace is.");
        if (t.Hxy != 0)
            throw new ArgumentException($"{TypeName}: ∂²Φ/∂x∂y at the vertex is {t.Hxy:R} rad/m², so the phase's astigmatism is not along x and y and a y-meridian paraxial ray leaves its plane. Paraxial data, ray aiming and solves are not available for this lens.");
        return substrate with { DiffractivePowerPerWavelength = substrate.DiffractivePowerPerWavelength - t.Hyy / TwoPi };
    }

    /// <summary>The substrate's own limitation; the phase's path term is exact.</summary>
    public override string? OpdLimitation(SequentialSurface surface) => Substrate?.OpdLimitation(surface);

    // ---- helpers for the types ----

    /// <summary>Throws unless <paramref name="value"/> is finite, naming the type and the value.</summary>
    protected static double Finite(double value, string form, string what)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"{form}: {what} is {value}; it must be a finite number.");
        return value;
    }

    /// <summary>A copy of <paramref name="values"/> (at most <paramref name="maximum"/>), each finite.</summary>
    protected static double[] FiniteCopy(IReadOnlyList<double> values, string form, string what, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum)
            throw new ArgumentException($"{form}: {values.Count} {what} given; OpticStudio's form holds at most {maximum}.");
        var copy = new double[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = Finite(values[i], form, $"{what} {i + 1}");
        return copy;
    }
}
