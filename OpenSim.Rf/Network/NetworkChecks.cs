using System.Numerics;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Network;

/// <summary>What a set of S-parameters says about itself at one frequency.</summary>
/// <param name="ReciprocityError">Largest |S_ij − S_ji|.</param>
/// <param name="LargestSingularValue">‖S‖₂; above 1 the network gives back more power than it
/// was sent for some excitation.</param>
/// <param name="AbsorbedFraction">For each port driven alone with the others matched,
/// 1 − Σ_i |S_ij|²: the share of the incident power that does not come back out of any
/// port — radiated or lost.</param>
public sealed record NetworkCheck(double FrequencyHz, double ReciprocityError, double LargestSingularValue,
    IReadOnlyList<double> AbsorbedFraction)
{
    public bool IsReciprocal(double tolerance = NetworkChecks.ReciprocityTolerance) => ReciprocityError <= tolerance;
    public bool IsPassive(double tolerance = NetworkChecks.PassivityTolerance) => LargestSingularValue <= 1 + tolerance;
}

/// <summary>
/// Reciprocity and passivity of S-parameters. A network of linear, time-invariant, isotropic
/// materials has S = Sᵀ; one with no source inside has ‖S‖₂ ≤ 1. A solve that breaks either
/// by more than its numerical noise is wrong somewhere, whatever its curves look like, so
/// every S-parameter result carries these two numbers.
/// </summary>
public static class NetworkChecks
{
    public const double ReciprocityTolerance = 1e-3;
    public const double PassivityTolerance = 1e-3;

    public static NetworkCheck Check(double frequencyHz, Complex[,] s)
    {
        int n = s.GetLength(0);
        double reciprocity = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                reciprocity = Math.Max(reciprocity, (s[i, j] - s[j, i]).Magnitude);
        var absorbed = new double[n];
        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += s[i, j].Magnitude * s[i, j].Magnitude;
            absorbed[j] = 1 - sum;
        }
        return new NetworkCheck(frequencyHz, reciprocity, LargestSingularValue(s), absorbed);
    }

    /// <summary>‖S‖₂ by power iteration on SᴴS (port-count sizes; converges to rounding in
    /// a few hundred steps even for close singular values, and stops when it has).</summary>
    public static double LargestSingularValue(Complex[,] s)
    {
        int n = s.GetLength(0);
        if (n == 1) return s[0, 0].Magnitude;
        double best = 0;
        // Start from each unit vector in turn: a start orthogonal to the top vector cannot hide it.
        for (int start = 0; start < n; start++)
        {
            var x = new Complex[n];
            x[start] = 1;
            double value = 0;
            for (int iteration = 0; iteration < 500; iteration++)
            {
                var y = new Complex[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) y[i] += s[i, j] * x[j];
                var z = new Complex[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) z[i] += Complex.Conjugate(s[j, i]) * y[j];
                double norm = Math.Sqrt(z.Sum(c => c.Magnitude * c.Magnitude));
                if (norm == 0) { value = 0; break; }
                double next = Math.Sqrt(norm);
                for (int i = 0; i < n; i++) x[i] = z[i] / norm;
                if (Math.Abs(next - value) <= 1e-14 * next) { value = next; break; }
                value = next;
            }
            best = Math.Max(best, value);
        }
        return best;
    }

    /// <summary>The worst of both over a sweep, as the lines a result prints.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<NetworkCheck> checks)
    {
        if (checks.Count == 0) return Array.Empty<string>();
        var lines = new List<string>();
        var worstPassive = checks.MaxBy(c => c.LargestSingularValue)!;
        lines.Add(worstPassive.IsPassive()
            ? $"Passive: the largest singular value of S is {worstPassive.LargestSingularValue:f4} (at {worstPassive.FrequencyHz / 1e6:g4} MHz)."
            : $"NOT PASSIVE: the largest singular value of S reaches {worstPassive.LargestSingularValue:f4} at "
              + $"{worstPassive.FrequencyHz / 1e6:g4} MHz — the solve returns more power than it was given there.");
        if (checks[0].AbsorbedFraction.Count > 1)
        {
            var worstReciprocal = checks.MaxBy(c => c.ReciprocityError)!;
            lines.Add(worstReciprocal.IsReciprocal()
                ? $"Reciprocal: |S_ij − S_ji| is at most {worstReciprocal.ReciprocityError:e1}."
                : $"NOT RECIPROCAL: |S_ij − S_ji| reaches {worstReciprocal.ReciprocityError:e1} at "
                  + $"{worstReciprocal.FrequencyHz / 1e6:g4} MHz.");
        }
        return lines;
    }

    /// <summary>S from a port admittance matrix: S = (I − Z₀Y)(I + Z₀Y)⁻¹.</summary>
    public static Complex[,] AdmittanceToScattering(Complex[,] y, double referenceOhms)
    {
        int n = y.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = (i == j ? 1 : 0) + referenceOhms * y[i, j];
                minus[i, j] = (i == j ? 1 : 0) - referenceOhms * y[i, j];
            }
        return NetworkParameters.Multiply(minus, NetworkParameters.Invert(plus));
    }
}

/// <summary>Which way round a lumped port's L sits (<see cref="LineFromTwoLengths.Calibrate"/>).</summary>
public enum PortModel
{
    /// <summary>The series element at the terminals, the shunt on the line side: a feed's
    /// inductance, then the capacitance where it meets the line (a foot port to ground).</summary>
    SeriesAtTerminals,

    /// <summary>The shunt element across the terminals, the series one on the line side: a gap
    /// in the strip, whose own capacitance bridges the terminals, then the stub beyond it.</summary>
    ShuntAtTerminals
}

/// <summary>
/// What two lengths of a line between the same ports give (<see cref="LineFromTwoLengths.Calibrate"/>):
/// the line's γ [1/m] and Z₀ [Ω], and each port's series impedance and shunt admittance — the
/// error box, which <see cref="DeEmbed"/> takes off any two-port measured between the same ports.
/// </summary>
public sealed record LineCalibration(Complex Gamma, Complex CharacteristicOhms,
    Complex PortSeriesOhms, Complex PortShuntSiemens, PortModel Model = PortModel.SeriesAtTerminals)
{
    /// <summary>The ABCD of a two-port measured between the calibrated ports (in
    /// <paramref name="referenceOhms"/>) with both port networks removed, and the reference planes
    /// then moved <paramref name="shiftMeters"/> along the line at each end — positive moves them
    /// INTO the device, removing that much line from each side.</summary>
    public Complex[,] DeEmbed(Complex[,] s, double referenceOhms, double shiftMeters = 0)
    {
        // Series-then-shunt from the terminals, and its mirror; the other order swaps the two.
        var seriesFirst = new[,]
        {
            { 1 + PortSeriesOhms * PortShuntSiemens, PortSeriesOhms },
            { PortShuntSiemens, Complex.One }
        };
        var shuntFirst = new[,]
        {
            { Complex.One, PortSeriesOhms },
            { PortShuntSiemens, 1 + PortSeriesOhms * PortShuntSiemens }
        };
        var (port, mirrored) = Model == PortModel.SeriesAtTerminals ? (seriesFirst, shuntFirst) : (shuntFirst, seriesFirst);
        var inner = NetworkParameters.Multiply(NetworkParameters.Multiply(NetworkParameters.Invert(port),
            LineFromTwoLengths.ScatteringToAbcd(s, referenceOhms)), NetworkParameters.Invert(mirrored));
        if (shiftMeters == 0) return inner;
        var removed = NetworkParameters.Invert(Line(shiftMeters));
        return NetworkParameters.Multiply(NetworkParameters.Multiply(removed, inner), removed);
    }

    /// <summary>The same, as S-parameters referenced to the line's own impedance — a matched
    /// line reads S11 = 0 there whatever its Z₀.</summary>
    public Complex[,] DeEmbedToLine(Complex[,] s, double referenceOhms, double shiftMeters = 0)
    {
        var m = DeEmbed(s, referenceOhms, shiftMeters);
        // Normalize by Z₀ (complex on a lossy line): S in the line's own wave basis.
        Complex a = m[0, 0], b = m[0, 1] / CharacteristicOhms, c = m[1, 0] * CharacteristicOhms, d = m[1, 1];
        Complex denominator = a + b + c + d;
        return new[,]
        {
            { (a + b - c - d) / denominator, 2 * (a * d - b * c) / denominator },
            { 2 / denominator, (-a + b - c + d) / denominator }
        };
    }

    /// <summary>ABCD of <paramref name="lengthMeters"/> of the calibrated line.</summary>
    public Complex[,] Line(double lengthMeters)
    {
        Complex c = Complex.Cosh(Gamma * lengthMeters), s = Complex.Sinh(Gamma * lengthMeters);
        return new[,] { { c, CharacteristicOhms * s }, { s / CharacteristicOhms, c } };
    }
}

/// <summary>
/// The propagation constant of a uniform line from two lengths of it between the same two
/// ports — the thru–line step of TRL calibration, applied to simulated S-parameters. Whatever
/// the ports do to the wave (a feed's inductance, a gap's fringing) is the same error box X in
/// both, T_long·T_short⁻¹ = X·diag(e^{−γΔl}, e^{+γΔl})·X⁻¹, and its eigenvalues do not
/// depend on X.
/// </summary>
public static class LineFromTwoLengths
{
    /// <summary>γ = α + jβ [1/m]. <paramref name="betaEstimate"/> picks the forward wave and
    /// the branch of the logarithm (any value within π/(2Δl) of the true β).</summary>
    public static Complex PropagationConstant(Complex[,] sShort, Complex[,] sLong, double lengthDifference,
        double betaEstimate)
    {
        var product = NetworkParameters.Multiply(Transfer(sLong), NetworkParameters.Invert(Transfer(sShort)));
        Complex trace = product[0, 0] + product[1, 1];
        Complex determinant = product[0, 0] * product[1, 1] - product[0, 1] * product[1, 0];
        Complex root = Complex.Sqrt(trace * trace / 4 - determinant);
        Complex a = trace / 2 + root, b = trace / 2 - root;
        // The two eigenvalues are e^{−γΔl} and e^{+γΔl}. On a low-loss line both have
        // magnitude 1 to within the solve's noise, so the forward one is told by its phase:
        // the one whose β is nearer the estimate.
        double period = 2 * Math.PI / lengthDifference;
        Complex Gamma(Complex eigenvalue)
        {
            Complex g = -Complex.Log(eigenvalue) / lengthDifference;
            return new Complex(g.Real, g.Imaginary + period * Math.Round((betaEstimate - g.Imaginary) / period));
        }
        Complex ga = Gamma(a), gb = Gamma(b);
        return Math.Abs(ga.Imaginary - betaEstimate) <= Math.Abs(gb.Imaginary - betaEstimate) ? ga : gb;
    }

    /// <summary>
    /// The line and the ports both, from the same two lengths. γ comes from
    /// <see cref="PropagationConstant"/>; the rest needs a model of the port, because two lines
    /// alone fix the line's impedance only up to an ideal transformer hidden in the error box
    /// (TRL's reference-impedance ambiguity). The model is the lumped port, an L of a series
    /// impedance Z_s and a shunt admittance Y_s, the same at both ends, mirrored, in one of two
    /// orders (<see cref="PortModel"/>). With it each length's ABCD is
    ///   A = c·u + s·v,  B = c·p + s·q,  C = c·r + s·w   (c = cosh γl, s = sinh γl),
    /// linear in (u, v), (p, q), (r, w), so two lengths solve each pair; with a = 1 + Z_sY_s,
    ///  • series at the terminals: u = 1 + 2Z_sY_s, p = 2aZ_s, r = 2Y_s, q = Z_s²/Z₀ + a²Z₀,
    ///    w = 1/Z₀ + Y_s²Z₀, hence Z_s = p/(1 + u), Y_s = r/2, Z₀ = (q − Z_s²·w)/u;
    ///  • shunt at the terminals: u as before, p = 2Z_s, r = 2aY_s, q = Z_s²/Z₀ + Z₀,
    ///    w = a²/Z₀ + Y_s²Z₀, hence Z_s = p/2, Y_s = r/(1 + u), Z₀ = (a²q − Z_s²·w)/u.
    /// Exact when the port is such a network; a port that is more than that (a length of a
    /// different line, say) biases Z₀ — and a series element much larger than Z₀ multiplies the
    /// bias, since Z₀ comes out of a difference of terms in Z_s². A gap port near an open end is
    /// that case (the short stub beyond it is a large capacitive reactance): in a stripline, a
    /// gap 0.25 mm from the end read Z₀ 22 % high, 3 mm from it 1 % (see ShieldedKernelTests).
    /// </summary>
    public static LineCalibration Calibrate(Complex[,] sShort, Complex[,] sLong, double shortLength,
        double longLength, double referenceOhms, double betaEstimate, PortModel model = PortModel.SeriesAtTerminals)
    {
        if (!(longLength > shortLength && shortLength > 0))
            throw new ArgumentException("The second line must be the longer, and both of positive length.");
        Complex gamma = PropagationConstant(sShort, sLong, longLength - shortLength, betaEstimate);
        var m1 = ScatteringToAbcd(sShort, referenceOhms);
        var m2 = ScatteringToAbcd(sLong, referenceOhms);
        Complex c1 = Complex.Cosh(gamma * shortLength), s1 = Complex.Sinh(gamma * shortLength);
        Complex c2 = Complex.Cosh(gamma * longLength), s2 = Complex.Sinh(gamma * longLength);
        Complex det = c1 * s2 - c2 * s1;                           // sinh(γΔl): zero only for Δl = 0
        (Complex, Complex) Pair(Complex y1, Complex y2) => ((y1 * s2 - y2 * s1) / det, (c1 * y2 - c2 * y1) / det);
        // A and D agree for mirrored ports; their mean is the better-conditioned reading.
        var (u, _) = Pair(0.5 * (m1[0, 0] + m1[1, 1]), 0.5 * (m2[0, 0] + m2[1, 1]));
        var (p, q) = Pair(m1[0, 1], m2[0, 1]);
        var (r, w) = Pair(m1[1, 0], m2[1, 0]);
        if (model == PortModel.SeriesAtTerminals)
        {
            Complex zs = p / (1 + u), ys = r / 2;
            return new LineCalibration(gamma, (q - zs * zs * w) / u, zs, ys, model);
        }
        else
        {
            Complex zs = p / 2, ys = r / (1 + u), a = (1 + u) / 2;
            return new LineCalibration(gamma, (a * a * q - zs * zs * w) / u, zs, ys, model);
        }
    }

    /// <summary>ABCD of a two-port from its S-parameters in a real reference impedance.</summary>
    internal static Complex[,] ScatteringToAbcd(Complex[,] s, double referenceOhms)
    {
        if (s.GetLength(0) != 2) throw new ArgumentException("Two-port S-parameters are needed.");
        Complex s11 = s[0, 0], s12 = s[0, 1], s21 = s[1, 0], s22 = s[1, 1];
        Complex twice = 2 * s21;
        double z0 = referenceOhms;
        return new[,]
        {
            { ((1 + s11) * (1 - s22) + s12 * s21) / twice, z0 * ((1 + s11) * (1 + s22) - s12 * s21) / twice },
            { ((1 - s11) * (1 - s22) - s12 * s21) / (twice * z0), ((1 - s11) * (1 + s22) + s12 * s21) / twice }
        };
    }

    /// <summary>S-parameters of a two-port from its ABCD in a real reference impedance.</summary>
    internal static Complex[,] AbcdToScattering(Complex[,] m, double referenceOhms)
    {
        Complex a = m[0, 0], b = m[0, 1] / referenceOhms, c = m[1, 0] * referenceOhms, d = m[1, 1];
        Complex denominator = a + b + c + d;
        return new[,]
        {
            { (a + b - c - d) / denominator, 2 * (a * d - b * c) / denominator },
            { 2 / denominator, (-a + b - c + d) / denominator }
        };
    }

    /// <summary>Wave-cascading matrix of a two-port: [b1; a1] = T·[a2; b2].</summary>
    private static Complex[,] Transfer(Complex[,] s)
    {
        if (s.GetLength(0) != 2) throw new ArgumentException("Two-port S-parameters are needed.");
        Complex s11 = s[0, 0], s12 = s[0, 1], s21 = s[1, 0], s22 = s[1, 1];
        return new[,]
        {
            { -(s11 * s22 - s12 * s21) / s21, s11 / s21 },
            { -s22 / s21, 1 / s21 }
        };
    }
}
