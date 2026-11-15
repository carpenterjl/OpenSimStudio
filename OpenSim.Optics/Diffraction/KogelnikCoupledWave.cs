using System.Numerics;

namespace OpenSim.Optics.Diffraction;

/// <summary>The inputs of Kogelnik's two-wave theory in his notation (H. Kogelnik, "Coupled wave
/// theory for thick hologram gratings", Bell System Technical Journal 48, 2909–2947, 1969; saved
/// under plans/sources/optics/op-47-help/papers/): the coupling constant κ (1/m, eq. 6, complex for
/// an absorption grating; times the polarization factor r·s of eq. 88–90), the mean absorption
/// constant α (1/m, eq. 5), the dephasing measure ϑ (1/m, eq. 17), the obliquity factors c_R and c_S
/// (eq. 23), and the grating thickness d (m).</summary>
public readonly record struct KogelnikParameters(Complex Kappa, double Alpha, double Dephasing, double CR, double CS, double Thickness);

/// <summary>The diffracted (signal) efficiency η = |c_S|/c_R·|S|² (eq. 40) and the power left in
/// the undiffracted (reference) wave at its exit, |R(d)|² (transmission) or |R(d)|² at z = d for
/// a reflection grating; their sum is 1 for a lossless grating (eq. 24).</summary>
public readonly record struct KogelnikEfficiency(double Diffracted, double Undiffracted, bool Reflection);

/// <summary>
/// Kogelnik's coupled-wave solution for a thick sinusoidal grating (OP-47; the method OpticStudio's
/// sequential Diffraction Efficiency analyses use, help page "Calculation Assumptions and
/// Limitations"). The coupled-wave equations c_R R′ + αR = −iκS, c_S S′ + (α + iϑ)S = −iκR (eq. 21,
/// 22; Kogelnik's j is i here) have solutions R = Σ r_k e^{γ_k z}, S = Σ s_k e^{γ_k z} with
/// <code>
/// γ₁,₂ = −½(α/c_R + α/c_S + iϑ/c_S) ± ½[(α/c_R − α/c_S − iϑ/c_S)² − 4κ²/(c_R c_S)]^½   (eq. 30)
/// </code>
/// and s_k = g_k r_k, g_k = −iκ/(c_S γ_k + α + iϑ) (eq. 28). A transmission grating (c_S &gt; 0) has
/// R(0) = 1, S(0) = 0 (eq. 31), so r₁ = g₂/(g₂ − g₁), r₂ = −g₁/(g₂ − g₁) and
/// S(d) = g₁g₂(e^{γ₁d} − e^{γ₂d})/(g₂ − g₁) (equal to eq. 34), R(d) = (g₂e^{γ₁d} − g₁e^{γ₂d})/(g₂ − g₁).
/// A reflection grating (c_S &lt; 0) has R(0) = 1, S(d) = 0 (eq. 35), so
/// S(0) = g₁g₂(e^{γ₂d} − e^{γ₁d})/(g₂e^{γ₂d} − g₁e^{γ₁d}) (equal to eq. 39) and
/// R(d) = (g₂ − g₁)e^{(γ₁+γ₂)d}/(g₂e^{γ₂d} − g₁e^{γ₁d}); these quotients are evaluated with the
/// exponentials divided by the larger one so no term overflows. The derivations from eq. 25–28 and
/// the boundary conditions are this file's; the closed forms they reduce to for the lossless
/// grating (eq. 43, 57, 59) are the tests' references.
/// </summary>
public static class KogelnikCoupledWave
{
    public static KogelnikEfficiency Solve(KogelnikParameters p)
    {
        if (!double.IsFinite(p.CR) || !(p.CR > 0))
            throw new ArgumentException($"Kogelnik coupled waves: the reference obliquity c_R is {p.CR}; it must be positive (the reference wave enters the grating).");
        if (!double.IsFinite(p.CS) || p.CS == 0)
            throw new ArgumentException($"Kogelnik coupled waves: the signal obliquity c_S is {p.CS}; a signal wave travelling along the grating (c_S = 0) is outside the theory.");
        if (!(p.Thickness > 0) || !double.IsFinite(p.Thickness))
            throw new ArgumentException($"Kogelnik coupled waves: the thickness is {p.Thickness} m; it must be positive.");
        if (!double.IsFinite(p.Alpha) || p.Alpha < 0 || !double.IsFinite(p.Dephasing)
            || !double.IsFinite(p.Kappa.Real) || !double.IsFinite(p.Kappa.Imaginary))
            throw new ArgumentException("Kogelnik coupled waves: κ, α and ϑ must be finite and α ≥ 0.");

        var i = Complex.ImaginaryOne;
        double cr = p.CR, cs = p.CS, alpha = p.Alpha, d = p.Thickness;
        var kappa = p.Kappa;
        var mean = -0.5 * (alpha / cr + alpha / cs + i * p.Dephasing / cs);
        var diff = alpha / cr - alpha / cs - i * p.Dephasing / cs;
        var root = 0.5 * Complex.Sqrt(diff * diff - 4 * kappa * kappa / (cr * cs));
        var g1Den = cs * (mean + root) + alpha + i * p.Dephasing;
        var g2Den = cs * (mean - root) + alpha + i * p.Dephasing;
        if (kappa == Complex.Zero)
            return new KogelnikEfficiency(0, Math.Exp(-2 * alpha * d / cr), cs < 0);
        var gamma1 = mean + root;
        var gamma2 = mean - root;
        var g1 = -i * kappa / g1Den;
        var g2 = -i * kappa / g2Den;
        var gap = g2 - g1;
        if (!(gap.Magnitude > 1e-14 * (g1.Magnitude + g2.Magnitude)))
            throw new InvalidOperationException("Kogelnik coupled waves: the two partial waves coincide (the discriminant of eq. 30 is zero, the edge of a reflection grating's stop band); move the angle or wavelength slightly.");

        Complex s, r;
        bool reflection = cs < 0;
        if (!reflection)
        {
            var e1 = Complex.Exp(gamma1 * d);
            var e2 = Complex.Exp(gamma2 * d);
            s = g1 * g2 * (e1 - e2) / gap;
            r = (g2 * e1 - g1 * e2) / gap;
        }
        else
        {
            // Divide numerator and denominator by e^{γ_big d}, γ_big the exponent of larger real part.
            bool firstBig = gamma1.Real >= gamma2.Real;
            var big = firstBig ? gamma1 : gamma2;
            var e1 = Complex.Exp((gamma1 - big) * d);
            var e2 = Complex.Exp((gamma2 - big) * d);
            var denominator = g2 * e2 - g1 * e1;
            s = g1 * g2 * (e2 - e1) / denominator;
            // R(d) = (g₂ − g₁)e^{(γ₁+γ₂)d}/(g₂e^{γ₂d} − g₁e^{γ₁d}) = gap·e^{γ_small d}/denominator.
            var small = firstBig ? gamma2 : gamma1;
            r = gap * Complex.Exp(small * d) / denominator;
        }
        double eta = Math.Abs(cs) / cr * (s.Real * s.Real + s.Imaginary * s.Imaginary);
        double rest = r.Real * r.Real + r.Imaginary * r.Imaginary;
        if (!double.IsFinite(eta) || !double.IsFinite(rest))
            throw new InvalidOperationException($"Kogelnik coupled waves: the efficiency came out non-finite (η {eta}, undiffracted {rest}).");
        return new KogelnikEfficiency(eta, rest, reflection);
    }

    /// <summary>
    /// The parameters for a lossless sinusoidal index grating n(r) = n + Δn·cos(K·r) of thickness d
    /// between planes normal to <paramref name="normal"/>, read by a plane wave of free-space
    /// wavelength λ: β = 2πn/λ (eq. 8), ρ = β·d̂ the incident wave vector (d̂ entering the grating,
    /// d̂·ẑ &gt; 0), σ = ρ − K (eq. 11), c_R = ρ·ẑ/β, c_S = σ·ẑ/β (eq. 23, 86), ϑ = (β² − σ·σ)/(2β)
    /// (eq. 17, 87), κ = πΔn/λ (eq. 9) times r·s (eq. 88–90): 1 for TE (E normal to the plane of ρ and
    /// σ) and ρ̂·σ̂ for TM (derived: the two polarization vectors lie in that plane at right angles to
    /// ρ and σ, so their product is the cosine of the angle between ρ and σ; at Bragg incidence it is
    /// −cos 2(θ₀ − φ), eq. 90).
    /// </summary>
    /// <param name="grating">K, the grating vector of Kogelnik's eq. 3 and 11 (rad/m).</param>
    public static KogelnikParameters ForIndexGrating(Core.Numerics.Vector3D direction, Core.Numerics.Vector3D normal,
        Core.Numerics.Vector3D grating, double meanIndex, double modulation, double wavelength, double thickness, GratingPolarization polarization)
    {
        if (!(meanIndex > 0) || !double.IsFinite(meanIndex)) throw new ArgumentException($"Kogelnik coupled waves: the mean index is {meanIndex}; it must be positive.");
        if (!(wavelength > 0) || !double.IsFinite(wavelength)) throw new ArgumentException($"Kogelnik coupled waves: the wavelength is {wavelength} m; it must be positive.");
        var z = normal / normal.Length;
        var dir = direction / direction.Length;
        if (Core.Numerics.Vector3D.Dot(dir, z) < 0) z = -z;
        double beta = 2 * Math.PI * meanIndex / wavelength;
        var rho = beta * dir;
        var sigma = rho - grating;
        double cr = Core.Numerics.Vector3D.Dot(rho, z) / beta;
        double cs = Core.Numerics.Vector3D.Dot(sigma, z) / beta;
        double dephasing = (beta * beta - sigma.LengthSquared) / (2 * beta);
        double factor = 1;
        if (polarization == GratingPolarization.TM)
        {
            double sl = sigma.Length;
            factor = sl > 0 ? Core.Numerics.Vector3D.Dot(rho, sigma) / (beta * sl) : 1;
        }
        double kappa = Math.PI * modulation / wavelength * factor;
        return new KogelnikParameters(kappa, 0, dephasing, cr, cs, thickness);
    }
}
