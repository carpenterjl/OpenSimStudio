using System.Globalization;

namespace OpenSim.Rf.Si;

/// <summary>
/// What a coupled-line extraction is reported as, and the frequency grid its S-parameters
/// are exported on.
/// <para>
/// The impedances come from L and C together. For N lines the quantity that generalizes
/// "Z0" is the characteristic impedance MATRIX Z_c = C⁻¹·(C·L)^½: terminate the lines in
/// it and nothing reflects. Even- and odd-mode impedances exist only for a symmetric
/// pair, where they are the eigenvalues of Z_c: Z_c11 ± Z_c12.
/// </para>
/// </summary>
public static class LineReadout
{
    /// <summary>Z_c = C⁻¹·(C·L)^½ [Ω] for per-unit-length L [H/m] and Maxwell C [F/m], both
    /// symmetric positive definite. Computed as U⁻ᵀ·(Uᵀ·L·U)^½·U⁻¹ with C = U·Uᵀ, which is
    /// symmetric by construction.</summary>
    public static double[,] CharacteristicImpedance(double[,] inductance, double[,] capacitance)
    {
        int n = inductance.GetLength(0);
        if (inductance.GetLength(1) != n || capacitance.GetLength(0) != n || capacitance.GetLength(1) != n)
            throw new ArgumentException("L and C must be square and the same size.");

        // C = U·Uᵀ (lower-triangular U).
        var u = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = capacitance[i, j];
                for (int k = 0; k < j; k++) sum -= u[i, k] * u[j, k];
                if (i == j)
                {
                    if (!(sum > 0))
                        throw new InvalidOperationException("The capacitance matrix is not positive definite.");
                    u[i, i] = Math.Sqrt(sum);
                }
                else u[i, j] = sum / u[j, j];
            }

        // S = Uᵀ·L·U, symmetric positive definite.
        var lu = Multiply(inductance, u);
        var s = Multiply(Transpose(u), lu);
        var (values, vectors) = SymmetricEigen(s);
        var root = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                {
                    if (!(values[k] > 0))
                        throw new InvalidOperationException("The inductance matrix is not positive definite.");
                    sum += vectors[i, k] * Math.Sqrt(values[k]) * vectors[j, k];
                }
                root[i, j] = sum;
            }

        var uInverse = InvertLower(u);
        return Multiply(Transpose(uInverse), Multiply(root, uInverse));
    }

    /// <summary>The one-line read-out of an extraction: what each number is, with no
    /// label that only holds for some other geometry.</summary>
    public static string Describe(RlgcResult rlgc)
    {
        var c = rlgc.CapacitanceFaradsPerMeter;
        var l = rlgc.InductanceHenriesPerMeter;
        int n = rlgc.ConductorCount;
        var z = CharacteristicImpedance(l, c);
        string F(double value) => value.ToString("g4", CultureInfo.InvariantCulture);

        string text = $"C11 = {F(c[0, 0] * 1e12)} pF/m, L11 = {F(l[0, 0] * 1e9)} nH/m, " +
                      $"R_dc = {F(rlgc.ResistanceDcOhmsPerMeter[0])} Ω/m";
        if (n == 1)
        {
            double air = rlgc.AirCapacitanceFaradsPerMeter[0, 0];
            return text + $", Z0 = {F(z[0, 0])} Ω, ε_eff = {F(c[0, 0] / air)}";
        }

        text += $"; coupling to line 2: C_m/C11 = {(-c[0, 1] / c[0, 0]).ToString("P1", CultureInfo.InvariantCulture)}, " +
                $"L_m/L11 = {(l[0, 1] / l[0, 0]).ToString("P1", CultureInfo.InvariantCulture)}";
        if (IsSymmetricPair(l, c))
        {
            double even = z[0, 0] + z[0, 1], odd = z[0, 0] - z[0, 1];
            return text + $"; symmetric pair: Z_even = {F(even)} Ω, Z_odd = {F(odd)} Ω, " +
                   $"Z_diff = 2·Z_odd = {F(2 * odd)} Ω, Z_comm = Z_even/2 = {F(even / 2)} Ω";
        }

        var rows = new List<string>();
        for (int i = 0; i < n; i++)
            rows.Add("[" + string.Join(", ", Enumerable.Range(0, n).Select(j => F(z[i, j]))) + "]");
        return text + $"; characteristic impedance matrix Z_c = {string.Join(" ", rows)} Ω " +
               "(terminating the lines in it reflects nothing; Z_c[k,k] is line k's own entry. " +
               "Even- and odd-mode impedances exist only for a symmetric pair, which this is not)";
    }

    /// <summary>Two lines whose own L and C agree to 0.1 %.</summary>
    public static bool IsSymmetricPair(double[,] inductance, double[,] capacitance)
    {
        if (inductance.GetLength(0) != 2) return false;
        static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-3 * Math.Max(Math.Abs(a), Math.Abs(b));
        return Same(inductance[0, 0], inductance[1, 1]) && Same(capacitance[0, 0], capacitance[1, 1]);
    }

    /// <summary>
    /// The frequencies an S-parameter export is written on: DC to <paramref name="maxHz"/>
    /// inclusive, uniformly spaced (what a time-domain reader needs), at least
    /// <paramref name="minIntervals"/> intervals and fine enough that the line's phase
    /// advances by at most π/8 between points — Δf ≤ 1/(16·delay). Forty points to twice
    /// Nyquist put about π between points on a 300 mm line at 10 Gb/s, which no reader can
    /// interpolate. Capped at <paramref name="maxIntervals"/>.
    /// </summary>
    public static double[] ExportFrequencies(double maxHz, double delaySeconds,
        int minIntervals = 40, int maxIntervals = 4000)
    {
        if (!(maxHz > 0)) throw new ArgumentOutOfRangeException(nameof(maxHz));
        int intervals = minIntervals;
        if (delaySeconds > 0)
            intervals = Math.Max(intervals, (int)Math.Ceiling(maxHz * 16 * delaySeconds));
        intervals = Math.Min(intervals, maxIntervals);
        var frequencies = new double[intervals + 1];
        for (int k = 0; k <= intervals; k++) frequencies[k] = maxHz * k / intervals;
        return frequencies;
    }

    /// <summary>Port names of an N-line network's 2N ports, in port order: near ends
    /// first, then far ends.</summary>
    public static IReadOnlyList<string> PortNames(int lines)
    {
        var names = new List<string>(2 * lines);
        for (int k = 1; k <= lines; k++) names.Add($"line {k}, near end");
        for (int k = 1; k <= lines; k++) names.Add($"line {k}, far end");
        return names;
    }

    // ---------------------------------------------------------------- small dense helpers

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        int n = a.GetLength(0);
        var result = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++) sum += a[i, k] * b[k, j];
                result[i, j] = sum;
            }
        return result;
    }

    private static double[,] Transpose(double[,] a)
    {
        int n = a.GetLength(0);
        var result = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) result[i, j] = a[j, i];
        return result;
    }

    private static double[,] InvertLower(double[,] lower)
    {
        int n = lower.GetLength(0);
        var inverse = new double[n, n];
        for (int col = 0; col < n; col++)
            for (int row = col; row < n; row++)
            {
                double sum = row == col ? 1 : 0;
                for (int k = col; k < row; k++) sum -= lower[row, k] * inverse[k, col];
                inverse[row, col] = sum / lower[row, row];
            }
        return inverse;
    }

    /// <summary>Cyclic Jacobi: eigenvalues and orthonormal eigenvectors (columns) of a
    /// small symmetric matrix.</summary>
    private static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diagonal = 0;
            for (int i = 0; i < n; i++)
            {
                diagonal += a[i, i] * a[i, i];
                for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            }
            if (off <= 1e-30 * diagonal) break;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (a[p, q] == 0) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double cos = 1 / Math.Sqrt(t * t + 1), sin = t * cos;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = cos * akp - sin * akq;
                        a[k, q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = cos * apk - sin * aqk;
                        a[q, k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cos * vkp - sin * vkq;
                        v[k, q] = sin * vkp + cos * vkq;
                    }
                }
        }
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }
}
