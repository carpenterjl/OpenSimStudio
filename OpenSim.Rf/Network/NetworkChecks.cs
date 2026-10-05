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
