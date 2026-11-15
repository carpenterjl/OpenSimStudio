using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Diffractive;

namespace OpenSim.Optics.Diffraction;

/// <summary>
/// The volume-hologram data of OpticStudio's Hologram 1, Hologram 2 and Toroidal Hologram surfaces
/// (help pages "Hologram 1", "Hologram 2", "Toroidal Hologram (sequential surfaces, lens data
/// editor)", 2025 R2; parameters 14–21, or 23–30 on the Toroidal Hologram, "only available when Par
/// 13 ≠ 0"). "Hologram Thickness (only used for efficiency calculations not ray-tracing)": the ray
/// trace is the surface hologram's whatever these hold; they feed the efficiency analyses (OP-47,
/// docs/Optics-Conventions.md §61).
/// </summary>
public sealed record VolumeHologram
{
    /// <param name="thickness">"Hologram Thickness", meters.</param>
    /// <param name="n1">"Refractive index seen by construction beam 1 outside hologram, n1".</param>
    /// <param name="n2">"… construction beam 2 outside hologram, n2".</param>
    /// <param name="meanIndex">"Average refractive index of the hologram emulsion, n".</param>
    /// <param name="modulation">"Modulation of the refractive index, dn".</param>
    /// <param name="shrinkage">"Shrinkage (0 for no shrinkage, else scale of thickness e.g. 0.98 is 2% shrinkage)".</param>
    /// <param name="indexShift">"Index Shift (change of average refractive index after developing)".</param>
    /// <param name="considerFresnel">"Consider Fresnel? (0 for false, 1 for true)"; true is not built and is refused.</param>
    public VolumeHologram(double thickness, double n1, double n2, double meanIndex, double modulation,
        double shrinkage, double indexShift, bool considerFresnel)
    {
        const string form = "Volume hologram";
        if (!(thickness > 0) || !double.IsFinite(thickness))
            throw new ArgumentException($"{form}: the hologram thickness is {thickness}; it must be positive.");
        if (!(n1 > 0) || !double.IsFinite(n1) || !(n2 > 0) || !double.IsFinite(n2))
            throw new ArgumentException($"{form}: the construction indices n1 = {n1} and n2 = {n2} must be positive.");
        if (!(meanIndex > 0) || !double.IsFinite(meanIndex))
            throw new ArgumentException($"{form}: the average emulsion index n is {meanIndex}; it must be positive.");
        if (!double.IsFinite(modulation) || modulation < 0)
            throw new ArgumentException($"{form}: the index modulation dn is {modulation}; it must be zero or positive.");
        if (!double.IsFinite(shrinkage) || shrinkage < 0)
            throw new ArgumentException($"{form}: the shrinkage is {shrinkage}; it must be 0 (none) or a positive thickness scale.");
        if (!double.IsFinite(indexShift) || !(meanIndex + indexShift > 0))
            throw new ArgumentException($"{form}: the index shift {indexShift} leaves the developed index {meanIndex + indexShift}; it must stay positive.");
        if (considerFresnel)
            throw new ArgumentException($"{form}: \"Consider Fresnel?\" = 1 (Fresnel losses at the emulsion's faces) is not built in this program; set it to 0.");
        Thickness = thickness;
        N1 = n1;
        N2 = n2;
        MeanIndex = meanIndex;
        Modulation = modulation;
        Shrinkage = shrinkage;
        IndexShift = indexShift;
    }

    public double Thickness { get; }
    public double N1 { get; }
    public double N2 { get; }
    public double MeanIndex { get; }
    public double Modulation { get; }
    public double Shrinkage { get; }
    public double IndexShift { get; }

    /// <summary>The thickness scale after development: the shrinkage, or 1 when it is 0.</summary>
    public double ThicknessScale => Shrinkage == 0 ? 1 : Shrinkage;
}

/// <summary>Kogelnik efficiencies of a volume hologram at one ray: TE and TM (relative to the plane
/// of the incident and diffracted wave vectors) and their mix for the given polarization, with
/// the geometry that produced them.</summary>
public sealed record VolumeHologramEfficiency(KogelnikEfficiency TE, KogelnikEfficiency TM, double TEFraction,
    double Diffracted, double Undiffracted, double IncidenceDegrees, double SlantDegrees, bool Reflection);

/// <summary>
/// The efficiency of a two-point volume hologram at one ray (OP-47; docs/Optics-Conventions.md
/// §61). This program's reading of the help, listed in §10 item 57:
/// <list type="bullet">
/// <item>The recorded fringes are those of the two construction beams inside the emulsion: beam j
/// travels along r_j at the point (the unit vectors of the surface hologram's rule, Hologram 2's
/// second beam converging) in a medium of index n_j, and refracts into the emulsion of average
/// index n keeping its tangential wave vector: k_j = (2π/λc)(n_j r_j,t + s_j√(n² − n_j²|r_j,t|²)·n̂),
/// s_j the sign of r_j·n̂. The grating vector is K_g = k₁ − k₂, whose tangential part is the
/// surface hologram's m = 1 phase gradient when n₁ = n₂ = 1.</item>
/// <item>Shrinkage s ≠ 0 scales the thickness to s·d and so the normal component of K_g by 1/s;
/// the index shift changes the average index to n + Δn_shift on playback.</item>
/// <item>On playback the ray (index n₀ before the surface, air wavelength λ) enters the emulsion
/// keeping its tangential wave vector, |ρ| = 2π(n + shift)/λ. Order m sends it to σ = ρ + m·K_g, so
/// Kogelnik's grating vector (σ = ρ − K, eq. 11) is K = −m·K_g; the two-wave theory covers m = ±1
/// only, and other orders are refused.</item>
/// <item>The polarization mix: η = f·η_TE + (1 − f)·η_TM, f the fraction of the incident field
/// along ρ × σ; unpolarized light has f = ½.</item>
/// </list>
/// </summary>
public static class VolumeHologramEfficiencies
{
    public static VolumeHologramEfficiency At(TwoPointHologram hologram, Vector3D point, Vector3D incoming, Vector3D normal,
        double indexBefore, double airWavelength, (System.Numerics.Complex Ex, System.Numerics.Complex Ey)? jones, int surface)
    {
        ArgumentNullException.ThrowIfNull(hologram);
        string where = $"{hologram.TypeName} at surface {surface}";
        var volume = hologram.Volume
            ?? throw new ArgumentException($"{where} is not set up as a volume hologram (Volume Hologram? = 0), so it has no efficiency data; set parameter {(hologram is ToroidalHologram ? 22 : 13)} to 1 and fill in the volume parameters.");
        if (Math.Abs(hologram.Order) != 1)
            throw new ArgumentException($"{where}: the diffraction order is {hologram.Order}; Kogelnik's two-wave theory gives the efficiency of the orders ±1 only.");
        if (!hologram.TryConstructionDirections(point, out var r1, out var r2))
            throw new InvalidOperationException($"{where}: a construction point lies on the ray's intersection point, where the fringes are not defined.");
        var n = normal / normal.Length;
        double kc = 2 * Math.PI / OpticsConventions.MicrometersToMeters(hologram.ConstructionMicrometers);
        var k1 = Inside(r1, n, volume.N1, volume.MeanIndex, where, "construction beam 1") * kc;
        var k2 = Inside(r2, n, volume.N2, volume.MeanIndex, where, "construction beam 2") * kc;
        var kg = k1 - k2;
        double scale = volume.ThicknessScale;
        if (scale != 1)
        {
            double along = Vector3D.Dot(kg, n);
            kg += (along / scale - along) * n;
        }
        double playbackIndex = volume.MeanIndex + volume.IndexShift;
        var d = incoming / incoming.Length;
        var inEmulsion = Inside(d, n, indexBefore, playbackIndex, where, "the playback ray");
        inEmulsion /= inEmulsion.Length;
        var grating = -hologram.Order * kg;
        double thickness = volume.Thickness * scale;
        var te = KogelnikCoupledWave.ForIndexGrating(inEmulsion, n, grating, playbackIndex, volume.Modulation, airWavelength, thickness, GratingPolarization.TE);
        var tm = KogelnikCoupledWave.ForIndexGrating(inEmulsion, n, grating, playbackIndex, volume.Modulation, airWavelength, thickness, GratingPolarization.TM);
        var eTe = KogelnikCoupledWave.Solve(te);
        var eTm = KogelnikCoupledWave.Solve(tm);
        double beta = 2 * Math.PI * playbackIndex / airWavelength;
        var rho = beta * inEmulsion;
        var sigma = rho - grating;
        double f = jones is { } j ? TEFraction(j.Ex, j.Ey, d, Vector3D.Cross(rho / rho.Length, sigma.Length > 0 ? sigma / sigma.Length : sigma), n) : 0.5;
        double diffracted = f * eTe.Diffracted + (1 - f) * eTm.Diffracted;
        double undiffracted = f * eTe.Undiffracted + (1 - f) * eTm.Undiffracted;
        var zAxis = Vector3D.Dot(inEmulsion, n) < 0 ? -n : n;
        double incidence = Math.Acos(Math.Clamp(Vector3D.Dot(inEmulsion, zAxis), -1, 1)) * 180 / Math.PI;
        double slant = grating.Length > 0 ? Math.Acos(Math.Clamp(Vector3D.Dot(grating / grating.Length, zAxis), -1, 1)) * 180 / Math.PI : double.NaN;
        return new VolumeHologramEfficiency(eTe, eTm, f, diffracted, undiffracted, incidence, slant, eTe.Reflection);
    }

    /// <summary>The fraction of the power of a field with complex components (ex, ey) along the
    /// surface's local x and y, made transverse to the incident direction d, that lies along
    /// <paramref name="teAxis"/> (ρ × σ; d × n̂ when that vanishes; ½ when both vanish).</summary>
    internal static double TEFraction(System.Numerics.Complex ex, System.Numerics.Complex ey, Vector3D d, Vector3D teAxis, Vector3D n)
    {
        var e = new[] { ex, ey, System.Numerics.Complex.Zero };
        var dc = new[] { d.X, d.Y, d.Z };
        var along = e[0] * dc[0] + e[1] * dc[1];
        for (int i = 0; i < 3; i++) e[i] -= along * dc[i];
        double total = 0;
        for (int i = 0; i < 3; i++) total += e[i].Real * e[i].Real + e[i].Imaginary * e[i].Imaginary;
        if (!(total > 0)) return 0.5;
        var s = teAxis;
        if (s.Length < 1e-9) s = Vector3D.Cross(d, n);
        if (s.Length < 1e-12) return 0.5;
        s /= s.Length;
        var proj = e[0] * s.X + e[1] * s.Y + e[2] * s.Z;
        return (proj.Real * proj.Real + proj.Imaginary * proj.Imaginary) / total;
    }

    /// <summary>The unit wave vector inside the emulsion (index <paramref name="inside"/>) of a beam
    /// along <paramref name="r"/> in a medium of index <paramref name="outside"/>, times the inside index.</summary>
    private static Vector3D Inside(Vector3D r, Vector3D n, double outside, double inside, string where, string what)
    {
        var u = r / r.Length;
        double rn = Vector3D.Dot(u, n);
        var tangential = outside * (u - rn * n);
        double normal2 = inside * inside - tangential.LengthSquared;
        if (!(normal2 > 0))
            throw new ArgumentException($"{where}: {what} cannot enter the emulsion (index {inside}): its tangential index {Math.Sqrt(tangential.LengthSquared):g6} exceeds it (total internal reflection).");
        double sign = rn < 0 ? -1 : 1;
        return tangential + sign * Math.Sqrt(normal2) * n;
    }
}
