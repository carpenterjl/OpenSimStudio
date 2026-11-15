using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Anamorphic;

namespace OpenSim.Optics.Sequential.Diffractive;

/// <summary>
/// OpticStudio's two-point holograms (OpticStudio 2025 R2 help, pages "Hologram 1", "Hologram 2"
/// and "Toroidal Hologram (sequential surfaces, lens data editor)", saved under
/// plans/sources/optics/op-26-help/). The rule is Welford's
/// n̂ × (r′ₒ − r′ᵣ) = (m·λp/λc)·n̂ × (rₒ − rᵣ), rₒ and rᵣ "the unit vector[s] along the first
/// [and second] construction beam", r′ᵣ the readout and r′ₒ the diffracted ray (each multiplied
/// by its index), λc and λp the construction and playback wavelengths and m the order. It is the
/// rule of <see cref="DiffractiveShape"/> for the phase
/// <code>
/// Φ(P) = m·(2π/λc)·(D₁(P) − σ·D₂(P)),   ∇Φ = m·(2π/λc)·(rₒ − rᵣ)
/// </code>
/// with D = |P − Q| for a beam diverging from the construction point Q (∇D = (P − Q)/|P − Q|, the
/// beam's direction at P): Hologram 1 "assumes that both construction beams diverge from the
/// specified construction points" (σ = +1, rᵣ = (P − Q₂)/|P − Q₂|); Hologram 2 that "one
/// construction beam converges to one construction point, and the other construction beam
/// diverges from the other" (σ = −1, rᵣ = (Q₂ − P)/|Q₂ − P|, the second beam taken as the
/// converging one: "Which beam is first or second does not matter due to reciprocity"); the
/// Toroidal Hologram's flag (extra data 9, parameter 21) chooses, "+1" for Hologram 1's case and
/// "−1" for Hologram 2's. D is taken as |P − Q| − |Q|, computed as (P·P − 2P·Q)/(|P − Q| + |Q|)
/// so the phase is small near the vertex without cancellation; the constant changes no OPD.
/// <para>
/// "If the distance to a source point is greater than 1.0E+08 lens units, the point is assumed to
/// be infinitely distant and the construction beam is a perfect plane wave": D = −Q̂·P, ∇D = −Q̂.
/// The shape holds meters and has no lens unit, so the threshold is applied as 10⁸ mm = 10⁵ m,
/// OpticStudio's value for millimeter lenses (a reading listed in §10).
/// </para>
/// <para>
/// The construction wavelength is in micrometers whatever the lens units ("always has units of
/// micrometers") and, like the playback wavelength, in air ("the construction beams are always
/// assumed to be in the AIR"). Not built: a negative construction wavelength (constructed in
/// transmission, used in reflection; "OPD tracing will not work") and the volume hologram
/// (parameter 13 non-zero, efficiency only); both are refused by name.
/// </para>
/// </summary>
public abstract class TwoPointHologram : DiffractiveShape
{
    /// <summary>Beyond this distance from the vertex a construction point is at infinity:
    /// 10⁸ lens units read as 10⁸ mm.</summary>
    public const double InfiniteDistance = 1e5;

    private protected TwoPointHologram(string form, SurfaceShape? substrate, Vector3D first, Vector3D second,
        double constructionMicrometers, double order, bool bothDiverge) : base(substrate)
    {
        Finite(first.X, form, "construction X1"); Finite(first.Y, form, "construction Y1"); Finite(first.Z, form, "construction Z1");
        Finite(second.X, form, "construction X2"); Finite(second.Y, form, "construction Y2"); Finite(second.Z, form, "construction Z2");
        if (!double.IsFinite(constructionMicrometers) || constructionMicrometers == 0)
            throw new ArgumentException($"{form}: the construction wavelength is {constructionMicrometers} µm; it must be a non-zero wavelength.");
        if (constructionMicrometers < 0)
            throw new ArgumentException($"{form}: a negative construction wavelength ({constructionMicrometers} µm, a hologram constructed in transmission and used in reflection) is not built in this program.");
        First = first;
        Second = second;
        ConstructionMicrometers = constructionMicrometers;
        Order = Finite(order, form, "the diffraction order");
        BothDiverge = bothDiverge;
    }

    /// <summary>The first construction point, local frame, meters.</summary>
    public Vector3D First { get; }

    /// <summary>The second construction point, local frame, meters.</summary>
    public Vector3D Second { get; }

    /// <summary>λc, micrometers.</summary>
    public double ConstructionMicrometers { get; }

    /// <summary>m.</summary>
    public double Order { get; }

    /// <summary>True when both beams diverge from (or both converge to) their points
    /// (Hologram 1); false when the second converges (Hologram 2).</summary>
    public bool BothDiverge { get; }

    private double K => Order * 2 * Math.PI / OpticsConventions.MicrometersToMeters(ConstructionMicrometers);

    public sealed override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        phase = double.NaN;
        gradient = default;
        if (!Beam(point, First, out double d1, out var u1) || !Beam(point, Second, out double d2, out var u2)) return false;
        double sigma = BothDiverge ? 1 : -1, k = K;
        phase = k * (d1 - sigma * d2);
        gradient = k * (u1 - sigma * u2);
        return true;
    }

    /// <summary>D = |P − Q| − |Q| and ∇D = (P − Q)/|P − Q| for a finite point; D = −Q̂·P and
    /// ∇D = −Q̂ for one at infinity. False when P is the point itself.</summary>
    private static bool Beam(Vector3D p, Vector3D q, out double d, out Vector3D unit)
    {
        double qn = q.Length;
        if (qn > InfiniteDistance)
        {
            unit = -(q / qn);
            d = Vector3D.Dot(unit, p);
            return true;
        }
        var w = p - q;
        double wn = w.Length;
        if (!(wn > 0))
        {
            d = double.NaN;
            unit = default;
            return false;
        }
        unit = w / wn;
        d = (p.LengthSquared - 2 * Vector3D.Dot(p, q)) / (wn + qn);
        return true;
    }

    /// <summary>
    /// At the vertex, along the surface P(y) = (0, y, s(y)) with s″(0) = c the substrate's y–z
    /// curvature (derived: |D|″ = (D′·D′ + D·D″)/|D| − (D·D′)²/|D|³ with D = P − Q, so at y = 0,
    /// |D|′ = −Q_y/|Q| and |D|″ = (1 − c·Q_z)/|Q| − Q_y²/|Q|³; along x likewise, and
    /// ∂²|D|/∂x∂y = −Q_x·Q_y/|Q|³). A point at infinity gives −Q̂_x, −Q̂_y and −Q̂_z·c in y. The
    /// slopes of an off-axis point are refused by the paraxial model; only Hyy and Hxy are used.
    /// </summary>
    internal sealed override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature)
    {
        var a = BeamAtVertex(First, yCurvature);
        var b = BeamAtVertex(Second, yCurvature);
        double sigma = BothDiverge ? 1 : -1, k = K;
        return new VertexTaylor
        {
            Gx = k * (a.Gx - sigma * b.Gx), Gy = k * (a.Gy - sigma * b.Gy),
            Hyy = k * (a.Hyy - sigma * b.Hyy), Hxy = k * (a.Hxy - sigma * b.Hxy),
        };
    }

    private static VertexTaylor BeamAtVertex(Vector3D q, double c)
    {
        double qn = q.Length;
        if (qn > InfiniteDistance)
            return new VertexTaylor { Gx = -q.X / qn, Gy = -q.Y / qn, Hyy = -q.Z / qn * c };
        if (!(qn > 0))
            throw new ArgumentException("A hologram construction point lies on the vertex, where the phase is not defined, so the surface has no first-order model.");
        double q3 = qn * qn * qn;
        return new VertexTaylor
        {
            Gx = -q.X / qn, Gy = -q.Y / qn,
            Hyy = (1 - c * q.Z) / qn - q.Y * q.Y / q3,
            Hxy = -q.X * q.Y / q3,
        };
    }

    /// <summary>Throws for a non-zero volume-hologram flag (not built).</summary>
    internal static void RefuseVolume(string form, double flag)
    {
        if (flag != 0)
            throw new ArgumentException($"{form}: the volume hologram (Volume Hologram? ≠ 0) is not built in this program; set it to 0 (the surface hologram, which deviates rays only).");
    }
}

/// <summary>OpticStudio's Hologram 1 and Hologram 2 on a Standard substrate ("plane, spherical,
/// or conical"); parameters 1–6 the construction points, 7 λc (µm), 8 the order
/// (<see cref="TwoPointHologram"/>).</summary>
public sealed class HologramShape : TwoPointHologram
{
    /// <param name="kind">1 for Hologram 1 (both beams diverge), 2 for Hologram 2.</param>
    public HologramShape(int kind, Vector3D first, Vector3D second, double constructionMicrometers, double order)
        : base(NameOf(kind), null, first, second, constructionMicrometers, order, kind == 1)
    {
        Kind = kind;
    }

    private static string NameOf(int kind) => kind switch
    {
        1 => "Hologram 1",
        2 => "Hologram 2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A two-point hologram is Hologram 1 or Hologram 2."),
    };

    public int Kind { get; }

    public override string TypeName => NameOf(Kind);
}

/// <summary>OpticStudio's Toroidal Hologram: "The Toroidal Hologram surface shape is identical to
/// the Toroidal surface" (radius of rotation in parameter 1, α1–α7 in 2–8;
/// <see cref="ToroidalSurface"/> without Zernike terms), construction points in 13–18, λc in 19,
/// the order in 20 and the converge/diverge flag in 21 ("Any value less than zero is the same as
/// −1, and any value greater than zero is the same as +1"; zero, which the page leaves open, is
/// refused).</summary>
public sealed class ToroidalHologram : TwoPointHologram
{
    private const string Form = "Toroidal Hologram";

    public ToroidalHologram(double radiusOfRotation, IReadOnlyList<double> alpha, Vector3D first, Vector3D second,
        double constructionMicrometers, double order, double flag)
        : base(Form, new ToroidalSurface(radiusOfRotation, alpha), first, second, constructionMicrometers, order, CheckedFlag(flag))
    {
    }

    private static bool CheckedFlag(double flag)
    {
        if (!double.IsFinite(flag) || flag == 0)
            throw new ArgumentException($"{Form}: the converge/diverge flag (parameter 21) is {flag}; it must be positive (both beams diverge or both converge) or negative (one converges, one diverges).");
        return flag > 0;
    }

    public override string TypeName => Form;

    /// <summary>R, meters; 0 is infinite.</summary>
    public double RadiusOfRotation => ((ToroidalSurface)Substrate!).RadiusOfRotation;

    /// <summary>α1 … αn on y², y⁴, …</summary>
    public IReadOnlyList<double> Alpha => ((ToroidalSurface)Substrate!).Alpha;
}
