using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Diffraction;

/// <summary>
/// Thin-element (scalar) diffraction efficiencies of periodic phase profiles (OP-47,
/// docs/Optics-Conventions.md §61). The element multiplies the field by t(x) = e^{−iφ(x)}, φ the
/// phase delay, periodic with period Λ; order m is the Fourier coefficient
/// c_m = ∫₀¹ t(u)·e^{+i2πmu} du (u = x/Λ), travelling with transverse wavenumber k_x + 2πm/Λ, and
/// its efficiency is |c_m|² (Swanson, "Binary Optics Technology: The Theory and Design of
/// Multi-Level Diffractive Optical Elements", MIT Lincoln Laboratory Technical Report 854, 1989,
/// DTIC ADA213404, p. 4: "The diffraction efficiency of the m-th order is the absolute value of the
/// amplitude of the m-th order squared"). The scalar model ignores Fresnel reflection, shadowing
/// and polarization; the rigorous model is <see cref="Rcwa1D"/>.
/// <para>
/// <b>Phase depth.</b> α is the peak phase delay of the profile in waves. For a surface relief of
/// depth d between media n and n₀ at wavelength λ, α = (n − n₀)·d/λ (Swanson eq. 2.3 writes
/// it βT with T the period and β = (n − 1)d/(λT); eq. 2.8 sets d = λ₀/(n − 1), so α = λ₀/λ when
/// dispersion is neglected). <see cref="DepthInWaves"/> gives it.
/// </para>
/// <para>
/// <b>Sign.</b> A profile whose phase delay increases with x within each period (a sawtooth rising
/// along +x, thicker toward +x) sends light into positive orders, the way a prism deviates toward
/// its base.
/// </para>
/// </summary>
public static class ScalarGratingEfficiency
{
    /// <summary>α = (n − n₀)·d/λ: the peak phase delay of a relief of depth d, in waves.</summary>
    public static double DepthInWaves(double index, double surroundingIndex, double depth, double wavelength)
    {
        if (!(wavelength > 0) || !double.IsFinite(wavelength))
            throw new ArgumentOutOfRangeException(nameof(wavelength), wavelength, "The wavelength must be positive.");
        return (index - surroundingIndex) * depth / wavelength;
    }

    /// <summary>sinc u = sin(πu)/(πu), 1 at u = 0.</summary>
    public static double Sinc(double u)
    {
        if (u == 0) return 1;
        double pu = Math.PI * u;
        return Math.Abs(u) < 1e-4 ? 1 - pu * pu / 6 + pu * pu * pu * pu / 120 : Math.Sin(pu) / pu;
    }

    /// <summary>
    /// The continuous blazed (kinoform, sawtooth) profile φ(u) = 2πα·u on [0, 1):
    /// η_m = sinc²(α − m) (Swanson eq. 2.4; derived here: c_m = ∫₀¹ e^{i2π(m − α)u} du, whose
    /// modulus is |sinc(m − α)|). At α = 1 all light is in order 1; a depth error ε (α = 1 + ε)
    /// leaves sinc²(ε) (eq. 2.7), and a wavelength λ for a design λ₀ leaves sinc²(λ₀/λ − 1) (eq. 2.8).
    /// </summary>
    public static double Blazed(int order, double alpha)
    {
        Finite(alpha, "the phase depth α");
        double s = Sinc(alpha - order);
        return s * s;
    }

    /// <summary>
    /// The N-level staircase approximating the blazed profile: φ(u) = 2πα·⌊Nu⌋/N (levels of width
    /// 1/N and steps of α/N waves; the highest step is (N − 1)α/N). Derived here, exactly: level k
    /// contributes e^{−i2παk/N}·∫_{k/N}^{(k+1)/N} e^{i2πmu}du = e^{−i2παk/N}·e^{iπm(2k+1)/N}·sinc(m/N)/N,
    /// and the geometric sum over k gives
    /// <code>
    /// η_m = sinc²(m/N) · [ sin(π(α − m)) / (N·sin(π(α − m)/N)) ]²,
    /// </code>
    /// the bracket taken as 1 where (α − m)/N is an integer (its limit). At α = 1 it reduces to
    /// Swanson's eq. 3.2, η₁ = sinc²(1/N) (p. 14), and it sends the rest of the light into orders
    /// m = 1 ± N, 1 ± 2N, … with sinc²(m/N). (Swanson's eq. 3.1, the product
    /// sinc²(α − m)·sinc²(α/N), is the approximation that drops those orders; this is exact.)
    /// N → ∞ gives <see cref="Blazed"/>.
    /// </summary>
    public static double Multilevel(int order, double alpha, int levels)
    {
        Finite(alpha, "the phase depth α");
        if (levels < 2) throw new ArgumentOutOfRangeException(nameof(levels), levels, "A multilevel profile has at least 2 levels.");
        double s = Sinc((double)order / levels);
        double delta = alpha - order;
        double ratio = delta / levels;
        double bracket;
        if (Math.Abs(ratio - Math.Round(ratio)) < 1e-12) bracket = 1;
        else bracket = Math.Sin(Math.PI * delta) / (levels * Math.Sin(Math.PI * ratio));
        return s * s * bracket * bracket;
    }

    /// <summary>
    /// The binary (two-level) phase grating with phase step φ (radians) over a fraction f of the
    /// period (the "fill factor"). Derived here: t = 1 on [0, 1 − f), e^{−iφ} on [1 − f, 1), so
    /// c₀ = 1 − f + f·e^{−iφ} and, for m ≠ 0, |c_m| = f·|sinc(mf)|·|e^{−iφ} − 1|:
    /// <code>
    /// η₀ = 1 − 2f(1 − f)(1 − cos φ),   η_m = 4f²·sinc²(mf)·sin²(φ/2)  (m ≠ 0).
    /// </code>
    /// With f = ½ and φ = πα this equals <see cref="Multilevel"/> with N = 2.
    /// </summary>
    public static double Binary(int order, double phaseStep, double fill)
    {
        Finite(phaseStep, "the phase step");
        if (!(fill >= 0 && fill <= 1)) throw new ArgumentOutOfRangeException(nameof(fill), fill, "The fill factor is a fraction of the period, 0 to 1.");
        if (order == 0) return 1 - 2 * fill * (1 - fill) * (1 - Math.Cos(phaseStep));
        double s = Sinc(order * fill);
        double h = Math.Sin(0.5 * phaseStep);
        return 4 * fill * fill * s * s * h * h;
    }

    /// <summary>
    /// The thin sinusoidal phase grating φ(u) = a·sin(2πu) (peak phase a radians): by the
    /// Jacobi–Anger expansion e^{(z/2)(t − 1/t)} = Σ tᵐJ_m(z) (DLMF 10.12.1, t = e^{−i2πu}),
    /// t(u) = e^{−ia·sin 2πu} = Σ J_m(a)·e^{−i2πmu}, so η_m = J_m(a)² — the Raman–Nath result.
    /// The orders sum to 1 (DLMF 10.12.4 with its companion Σ J_m² = 1).
    /// </summary>
    public static double Sinusoidal(int order, double peakPhase)
    {
        Finite(peakPhase, "the peak phase");
        double j = Bessel.Jn(order, peakPhase);
        return j * j;
    }

    /// <summary>
    /// The efficiency of a general periodic phase profile given as levels: segment k spans the
    /// fraction <c>widths[k]</c> of the period (in order from u = 0) with phase delay
    /// <c>phases[k]</c> radians. Derived as for <see cref="Multilevel"/>:
    /// c_m = Σ_k e^{−iφ_k}·(e^{i2πm u_{k+1}} − e^{i2πm u_k})/(i2πm), c₀ = Σ_k w_k·e^{−iφ_k}.
    /// </summary>
    public static double Levels(int order, IReadOnlyList<double> widths, IReadOnlyList<double> phases)
    {
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(phases);
        if (widths.Count == 0 || widths.Count != phases.Count)
            throw new ArgumentException("A level profile needs as many phases as widths, at least one.");
        double total = 0;
        foreach (double w in widths)
        {
            if (!(w > 0) || !double.IsFinite(w)) throw new ArgumentException($"A level width is {w}; widths are positive fractions of the period.");
            total += w;
        }
        if (Math.Abs(total - 1) > 1e-12) throw new ArgumentException($"The level widths add to {total}; they must add to 1 (one period).");
        Complex c = Complex.Zero;
        double u = 0;
        for (int k = 0; k < widths.Count; k++)
        {
            double u1 = k == widths.Count - 1 ? 1 : u + widths[k];
            var t = Complex.FromPolarCoordinates(1, -Finite(phases[k], $"phase {k + 1}"));
            if (order == 0) c += t * (u1 - u);
            else
            {
                double m = 2 * Math.PI * order;
                c += t * (Complex.FromPolarCoordinates(1, m * u1) - Complex.FromPolarCoordinates(1, m * u)) / new Complex(0, m);
            }
            u = u1;
        }
        return c.Real * c.Real + c.Imaginary * c.Imaginary;
    }

    private static double Finite(double v, string what)
    {
        if (!double.IsFinite(v)) throw new ArgumentException($"Scalar grating efficiency: {what} is {v}; it must be a finite number.");
        return v;
    }
}
