using System.Globalization;
using System.Numerics;

namespace OpenSim.Optics.Coatings;

/// <summary>One layer of a thin-film stack: its complex index n + ik (k ≥ 0 absorbs) and its
/// physical thickness, in the same length unit as the wavelength given to <see cref="ThinFilm.Compute"/>.</summary>
public readonly record struct ThinFilmLayer(Complex Index, double Thickness);

/// <summary>
/// The response of a stack to one polarization. <see cref="R"/> and <see cref="T"/> are the
/// amplitude coefficients of the electric field in the convention of docs/Optics-Conventions.md
/// section 12 (that of the tmm package, S. Byrnes): time factor e^(−iωt), index n + ik, and for p
/// light the sign for which r_p = −r_s at normal incidence. <see cref="Reflectance"/>,
/// <see cref="Transmittance"/> and <see cref="Absorptance"/> are power fractions of the incident
/// power, each computed from its own expression (not one as the remainder of the others), so
/// R + T + A = 1 is a check on the calculation.
/// </summary>
public readonly record struct ThinFilmAmplitudes(Complex R, Complex T, double Reflectance, double Transmittance, double Absorptance);

/// <summary>The s and p responses of a stack at one wavelength and angle.</summary>
public readonly record struct ThinFilmResult(ThinFilmAmplitudes S, ThinFilmAmplitudes P);

/// <summary>
/// The characteristic-matrix (admittance) calculation of a planar multilayer for s and p light,
/// with absorbing layers and an absorbing substrate: the tilted-admittance method of thin-film
/// texts such as Macleod's Thin-Film Optical Filters (not on this machine, so not cited by page),
/// derived in full in docs/Optics-Conventions.md section 12 in this program's sign convention and
/// checked against the tmm package (OpenSim.Tests/Optics/ThinFilmTests.cs).
/// <para>
/// For each layer j: q_j = N_j cos θ_j = √(N_j² − (n0 sin θ0)²), the root with Im q ≥ 0 (the
/// forward, decaying wave); the phase thickness δ_j = 2π q_j d_j / λ; the tilted admittance
/// η_j = q_j (s) or N_j² / q_j (p). The layer's matrix maps the tangential fields at its back face
/// to its front face, [E; H]_front = [[cos δ, −i sin δ / η], [−i η sin δ, cos δ]] [E; H]_back,
/// and [B; C] = Π M_j [1; η_substrate]. Then ρ = (η0 B − C)/(η0 B + C) and the tangential
/// transmission 2η0/(η0 B + C), with R = |ρ|², T = 4 η0 Re(η_sub) / |η0 B + C|² and
/// A = 4 η0 Re(B C* − η_sub) / |η0 B + C|².
/// </para>
/// <para>
/// <b>Opaque layers.</b> Each matrix is multiplied by e^(iδ_j), whose modulus e^(−Im δ) is at
/// most 1, so its entries become (1 ± e^(2iδ))/2 and stay bounded however thick or absorbing the
/// layer; the product of the factors is carried separately and restores B and C. A layer so
/// opaque that the factor underflows gives T = 0 exactly, which is then the true answer to
/// double precision. Nothing is clamped (tmm, by contrast, caps Im δ at 35).
/// </para>
/// </summary>
public static class ThinFilm
{
    /// <summary>
    /// Computes the stack. <paramref name="incident"/> must be real (a lossless incident
    /// medium, since the incident power is otherwise not defined by one plane wave), the angle
    /// in [0, 90°) radians, the wavelength and every thickness positive or zero, in one unit.
    /// Throws, naming the offending input, otherwise.
    /// </summary>
    public static ThinFilmResult Compute(double incident, IReadOnlyList<ThinFilmLayer> layers, Complex substrate,
        double wavelength, double angleRadians)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (!double.IsFinite(incident) || incident <= 0)
            throw new ArgumentOutOfRangeException(nameof(incident), incident, "The incident medium's index must be real and positive.");
        if (!double.IsFinite(wavelength) || wavelength <= 0)
            throw new ArgumentOutOfRangeException(nameof(wavelength), wavelength, "The wavelength must be positive.");
        if (!double.IsFinite(angleRadians) || angleRadians < 0 || angleRadians >= Math.PI / 2)
            throw new ArgumentOutOfRangeException(nameof(angleRadians), angleRadians,
                "The angle of incidence must lie in [0°, 90°): at grazing incidence the incident power through the surface is zero.");
        CheckIndex(substrate, "substrate");
        for (int i = 0; i < layers.Count; i++)
        {
            CheckIndex(layers[i].Index, $"layer {i + 1}");
            if (!double.IsFinite(layers[i].Thickness) || layers[i].Thickness < 0)
                throw new ArgumentOutOfRangeException(nameof(layers), layers[i].Thickness,
                    $"Layer {i + 1}: the thickness must be zero or positive (it is {Format(layers[i].Thickness)}).");
        }

        double beta = incident * Math.Sin(angleRadians);    // n0 sin θ0, conserved through the stack
        double cos0 = Math.Cos(angleRadians);
        var s = Solve(Polarization.S, incident, beta, cos0, layers, substrate, wavelength);
        var p = Solve(Polarization.P, incident, beta, cos0, layers, substrate, wavelength);
        return new ThinFilmResult(s, p);
    }

    private enum Polarization { S, P }

    private static ThinFilmAmplitudes Solve(Polarization pol, double n0, double beta, double cos0,
        IReadOnlyList<ThinFilmLayer> layers, Complex substrate, double wavelength)
    {
        double eta0 = pol == Polarization.S ? n0 * cos0 : n0 / cos0;
        Complex qSub = NormalComponent(substrate, beta, "the substrate");
        Complex etaSub = Admittance(pol, substrate, qSub);

        // [B'; C'] = Π M'_j [1; η_sub], built from the back (substrate side) to the front.
        Complex b = Complex.One, c = etaSub;
        Complex factor = Complex.One;                       // Π e^(iδ_j)
        for (int j = layers.Count - 1; j >= 0; j--)
        {
            var layer = layers[j];
            Complex q = NormalComponent(layer.Index, beta, $"layer {j + 1}");
            Complex eta = Admittance(pol, layer.Index, q);
            Complex delta = 2 * Math.PI * q * layer.Thickness / wavelength;
            Complex e2 = Complex.Exp(2 * Complex.ImaginaryOne * delta);   // |e2| ≤ 1 since Im δ ≥ 0
            Complex m11 = (1 + e2) / 2, m12 = (1 - e2) / (2 * eta), m21 = eta * (1 - e2) / 2;
            (b, c) = (m11 * b + m12 * c, m21 * b + m11 * c);
            factor *= Complex.Exp(Complex.ImaginaryOne * delta);
        }

        Complex denominator = eta0 * b + c;
        if (denominator == Complex.Zero || !IsFinite(denominator))
            throw new InvalidOperationException("The thin-film calculation gave no finite result (the stack's admittance is singular).");
        Complex rho = (eta0 * b - c) / denominator;
        Complex tauTangential = 2 * eta0 * factor / denominator;
        double scale = 4 * eta0 / (denominator.Magnitude * denominator.Magnitude);
        double factor2 = factor.Magnitude * factor.Magnitude;
        double reflectance = rho.Magnitude * rho.Magnitude;
        double transmittance = scale * etaSub.Real * factor2;
        double absorptance = scale * ((b * Complex.Conjugate(c)).Real - etaSub.Real * factor2);

        if (pol == Polarization.S)
            return new ThinFilmAmplitudes(rho, tauTangential, reflectance, transmittance, absorptance);
        // p: tangential E is E cos θ. The reflected wave shares cos θ0; the sign for which
        // r_p = −r_s at normal incidence is the negative of the tangential ratio. The transmitted
        // full amplitude is the tangential one times cos θ0 / cos θ_sub, cos θ_sub = q_sub / N_sub.
        Complex cosSub = qSub / substrate;
        return new ThinFilmAmplitudes(-rho, tauTangential * cos0 / cosSub, reflectance, transmittance, absorptance);
    }

    /// <summary>N cos θ = √(N² − β²) with Im ≥ 0; for k ≥ 0 the principal root is that one.</summary>
    private static Complex NormalComponent(Complex index, double beta, string where)
    {
        Complex q = Complex.Sqrt(index * index - beta * beta);
        if (q.Imaginary < 0) q = -q;                        // only reachable through rounding at Im = 0
        if (q.Imaginary == 0 && q.Real < 0) q = -q;
        if (q == Complex.Zero)
            throw new InvalidOperationException(
                $"The light travels exactly parallel to the interfaces in {where} (the angle is that medium's critical angle); the p admittance is infinite there. Change the angle by a small amount.");
        return q;
    }

    private static Complex Admittance(Polarization pol, Complex index, Complex q) =>
        pol == Polarization.S ? q : index * index / q;

    private static void CheckIndex(Complex index, string where)
    {
        if (!IsFinite(index) || index.Real <= 0 || index.Imaginary < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"The index of {where} must have n > 0 and k ≥ 0 (n + ik, k ≥ 0 absorbs; it is {Format(index.Real)} + {Format(index.Imaginary)}i). A negative k would be gain, which this calculation does not model.");
    }

    private static bool IsFinite(Complex z) => double.IsFinite(z.Real) && double.IsFinite(z.Imaginary);

    private static string Format(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
