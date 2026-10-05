using System.Globalization;
using System.Text;

namespace OpenSim.Solvers;

/// <summary>
/// A Foster network: R‖C pairs in series, Z_th(t) = Σ Rᵢ·(1 − e^{−t/τᵢ}). It reproduces a
/// curve at its terminals; its inner nodes mean nothing physically, and it must not be
/// extended (a heatsink cannot be hung on its last node). Use the Cauer form for that.
/// </summary>
public sealed record FosterNetwork(IReadOnlyList<double> Resistances, IReadOnlyList<double> TimeConstants)
{
    public int Stages => Resistances.Count;
    public double TotalResistance => Resistances.Sum();
    public IReadOnlyList<double> Capacitances => Resistances.Select((r, i) => TimeConstants[i] / r).ToList();

    public double Impedance(double time)
    {
        double sum = 0;
        for (int i = 0; i < Stages; i++) sum += Resistances[i] * (1 - Math.Exp(-time / TimeConstants[i]));
        return sum;
    }

    /// <summary>Z(s) = Σ Rᵢ/(1 + s·τᵢ) at a real s.</summary>
    public double Laplace(double s)
    {
        double sum = 0;
        for (int i = 0; i < Stages; i++) sum += Resistances[i] / (1 + s * TimeConstants[i]);
        return sum;
    }

    /// <summary>
    /// Peak rise per watt of PEAK power once a rectangular pulse train has gone on for
    /// ever — the family of curves a datasheet draws against pulse width, one per duty
    /// cycle. Each stage: Rᵢ·(1 − e^{−t_on/τᵢ})/(1 − e^{−T/τᵢ}).
    /// </summary>
    public double PulsedPeak(double periodSeconds, double dutyCycle)
    {
        if (dutyCycle >= 1) return TotalResistance;
        if (dutyCycle <= 0) return 0;
        double on = dutyCycle * periodSeconds, sum = 0;
        for (int i = 0; i < Stages; i++)
            sum += Resistances[i] * OneMinusExp(on / TimeConstants[i]) / OneMinusExp(periodSeconds / TimeConstants[i]);
        return sum;
    }

    /// <summary>SPICE subcircuit: a current of P amperes into <c>j</c> gives the junction's
    /// rise above <c>amb</c> in volts (1 A = 1 W, 1 V = 1 K, 1 Ω = 1 K/W, 1 F = 1 J/K).</summary>
    public string ToSpice(string name = "ZTH_FOSTER")
    {
        var text = new StringBuilder();
        text.AppendLine($"* Foster thermal network, {Stages} stages, total {Num(TotalResistance)} K/W");
        text.AppendLine("* 1 A = 1 W, 1 V = 1 K. Inner nodes have no physical meaning; do not extend this network.");
        text.AppendLine($".SUBCKT {name} j amb");
        for (int i = 0; i < Stages; i++)
        {
            string from = i == 0 ? "j" : $"n{i}", to = i == Stages - 1 ? "amb" : $"n{i + 1}";
            text.AppendLine($"R{i + 1} {from} {to} {Num(Resistances[i])}");
            text.AppendLine($"C{i + 1} {from} {to} {Num(TimeConstants[i] / Resistances[i])}");
        }
        text.AppendLine($".ENDS {name}");
        return text.ToString();
    }

    /// <summary>The stages as a table (R [K/W], τ [s], C [J/K]) for tools that take the
    /// values typed in.</summary>
    public string ToCsv()
    {
        var text = new StringBuilder("stage,R [K/W],tau [s],C [J/K]\n");
        for (int i = 0; i < Stages; i++)
            text.Append(CultureInfo.InvariantCulture, $"{i + 1},{Num(Resistances[i])},{Num(TimeConstants[i])},{Num(TimeConstants[i] / Resistances[i])}\n");
        return text.ToString();
    }

    internal static string Num(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>1 − e^{−x} without the cancellation at small x.</summary>
    internal static double OneMinusExp(double x) => x < 1e-5 ? x * (1 - x * (0.5 - x / 6)) : 1 - Math.Exp(-x);
}

/// <summary>
/// A Cauer network: a ladder with every capacitor to the reference. Stage k is Cₖ at its
/// node and Rₖ on to the next node; the last R ends at the reference (ambient). Its nodes
/// follow the heat's path, so the ladder can be cut and continued.
/// </summary>
public sealed record CauerNetwork(IReadOnlyList<double> Resistances, IReadOnlyList<double> Capacitances)
{
    public int Stages => Resistances.Count;
    public double TotalResistance => Resistances.Sum();

    /// <summary>Z(s) at a real s, by the ladder's continued fraction.</summary>
    public double Laplace(double s)
    {
        double z = 0;
        for (int k = Stages - 1; k >= 0; k--)
            z = 1 / (s * Capacitances[k] + 1 / (Resistances[k] + z));
        return z;
    }

    /// <summary>The cumulative structure function: the heat capacity met (ΣC) against the
    /// resistance crossed (ΣR), stage by stage from the source.</summary>
    public (IReadOnlyList<double> Resistance, IReadOnlyList<double> Capacitance) Cumulative()
    {
        var r = new double[Stages];
        var c = new double[Stages];
        double sumR = 0, sumC = 0;
        for (int k = 0; k < Stages; k++)
        {
            sumC += Capacitances[k];
            c[k] = sumC;
            sumR += Resistances[k];
            r[k] = sumR;
        }
        return (r, c);
    }

    public string ToSpice(string name = "ZTH_CAUER")
    {
        var text = new StringBuilder();
        text.AppendLine($"* Cauer thermal network, {Stages} stages, total {FosterNetwork.Num(TotalResistance)} K/W");
        text.AppendLine("* 1 A = 1 W, 1 V = 1 K. Capacitors go to amb; the ladder may be continued from its last resistor.");
        text.AppendLine($".SUBCKT {name} j amb");
        for (int k = 0; k < Stages; k++)
        {
            string node = k == 0 ? "j" : $"n{k}", next = k == Stages - 1 ? "amb" : $"n{k + 1}";
            text.AppendLine($"C{k + 1} {node} amb {FosterNetwork.Num(Capacitances[k])}");
            text.AppendLine($"R{k + 1} {node} {next} {FosterNetwork.Num(Resistances[k])}");
        }
        text.AppendLine($".ENDS {name}");
        return text.ToString();
    }

    public string ToCsv()
    {
        var text = new StringBuilder("stage,R [K/W],C [J/K]\n");
        for (int k = 0; k < Stages; k++)
            text.Append(CultureInfo.InvariantCulture, $"{k + 1},{FosterNetwork.Num(Resistances[k])},{FosterNetwork.Num(Capacitances[k])}\n");
        return text.ToString();
    }
}

/// <summary>A fitted network and how well it follows the curve.</summary>
public sealed record FosterFit(FosterNetwork Network, double WorstError, double RmsError)
{
    /// <summary>Errors are fractions of the curve's last value.</summary>
    public string Describe() =>
        $"{Network.Stages}-stage Foster fit: worst error {WorstError * 100:f2} %, rms {RmsError * 100:f2} % of the final value; " +
        $"total {Network.TotalResistance:g5} K/W.";
}

/// <summary>The structure function of a curve: its time constants resolved finely, turned
/// into a ladder, and summed along it.</summary>
public sealed record StructureFunction(
    IReadOnlyList<double> CumulativeResistance, IReadOnlyList<double> CumulativeCapacitance,
    FosterNetwork Spectrum, CauerNetwork Ladder)
{
    /// <summary>ΣC at a ΣR along the ladder (a staircase: the capacity met by then).</summary>
    public double CapacitanceAt(double cumulativeResistance)
    {
        double value = 0;
        for (int k = 0; k < CumulativeResistance.Count; k++)
        {
            value = CumulativeCapacitance[k];
            if (CumulativeResistance[k] >= cumulativeResistance) break;
        }
        return value;
    }
}

/// <summary>
/// From a thermal impedance curve to networks: a Foster fit of a few stages, its Cauer
/// equivalent, and the structure function.
/// </summary>
public static class ThermalNetworkFit
{
    /// <summary>
    /// Fits Σ Rᵢ(1 − e^{−t/τᵢ}) to a curve. A non-negative least-squares solve over a dense
    /// grid of time constants finds where the curve's time constants are; neighbours are
    /// merged down to the number of stages asked for; then each τ is tuned in turn, the R's
    /// re-solved every time. R's cannot come out negative.
    /// </summary>
    /// <param name="stages">0 = the fewest (up to 8) that follow the curve within 0.5 %.</param>
    public static FosterFit FitFoster(IReadOnlyList<double> times, IReadOnlyList<double> impedance, int stages = 0)
    {
        if (times.Count != impedance.Count || times.Count < 4)
            throw new ArgumentException("A fit needs at least four points of time and impedance.");
        if (stages > 0) return Fit(times, impedance, stages);
        FosterFit? best = null;
        for (int n = 1; n <= 8; n++)
        {
            var fit = Fit(times, impedance, n);
            if (best is null || fit.WorstError < best.WorstError) best = fit;
            if (fit.WorstError < 0.005) return fit;
        }
        return best!;
    }

    private static FosterFit Fit(IReadOnlyList<double> times, IReadOnlyList<double> impedance, int stages)
    {
        double final = impedance.Max();
        var spectrum = Spectrum(times, impedance, 8);
        // Clusters of neighbouring active time constants, each one stage.
        var groups = spectrum.Select(s => (R: s.R, LogTau: Math.Log(s.Tau))).ToList();
        while (groups.Count > stages)
        {
            int at = 0;
            double nearest = double.MaxValue;
            for (int i = 0; i + 1 < groups.Count; i++)
            {
                double gap = groups[i + 1].LogTau - groups[i].LogTau;
                if (gap < nearest) (nearest, at) = (gap, i);
            }
            var (a, b) = (groups[at], groups[at + 1]);
            groups[at] = (a.R + b.R, (a.R * a.LogTau + b.R * b.LogTau) / (a.R + b.R));
            groups.RemoveAt(at + 1);
        }
        var logTau = groups.Select(g => g.LogTau).ToArray();
        // Fewer active time constants than stages: spread the extra ones over the span.
        if (logTau.Length < stages)
        {
            double lo = Math.Log(times[0]), hi = Math.Log(times[^1]);
            logTau = Enumerable.Range(0, stages).Select(i => lo + (hi - lo) * (i + 0.5) / stages).ToArray();
        }

        var weights = new double[times.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = 1 / (impedance[i] + 0.02 * final);
        double[] resistances = Array.Empty<double>();
        double Residual(double[] taus)
        {
            resistances = SolveResistances(times, impedance, weights, taus);
            double sum = 0;
            for (int i = 0; i < times.Count; i++)
            {
                double model = 0;
                for (int j = 0; j < taus.Length; j++) model += resistances[j] * (1 - Math.Exp(-times[i] / Math.Exp(taus[j])));
                double r = (model - impedance[i]) * weights[i];
                sum += r * r;
            }
            return sum;
        }
        double current = Residual(logTau);
        for (int sweep = 0; sweep < 6; sweep++)
        {
            double before = current;
            for (int j = 0; j < logTau.Length; j++)
            {
                // Golden-section search for this τ between its neighbours.
                double lo = j > 0 ? 0.5 * (logTau[j - 1] + logTau[j]) : logTau[j] - 2.3;
                double hi = j + 1 < logTau.Length ? 0.5 * (logTau[j] + logTau[j + 1]) : logTau[j] + 2.3;
                const double golden = 0.6180339887498949;
                double x1 = hi - golden * (hi - lo), x2 = lo + golden * (hi - lo);
                double original = logTau[j];
                logTau[j] = x1; double f1 = Residual(logTau);
                logTau[j] = x2; double f2 = Residual(logTau);
                for (int it = 0; it < 24; it++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - golden * (hi - lo); logTau[j] = x1; f1 = Residual(logTau); }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + golden * (hi - lo); logTau[j] = x2; f2 = Residual(logTau); }
                }
                double found = f1 < f2 ? x1 : x2, value = Math.Min(f1, f2);
                if (value < current) { logTau[j] = found; current = value; }
                else logTau[j] = original;
            }
            if (before - current < 1e-9 * before) break;
        }
        current = Residual(logTau);

        var kept = Enumerable.Range(0, logTau.Length).Where(j => resistances[j] > 1e-12 * final)
            .OrderBy(j => logTau[j]).ToList();
        var network = new FosterNetwork(kept.Select(j => resistances[j]).ToList(), kept.Select(j => Math.Exp(logTau[j])).ToList());
        double worst = 0, squares = 0;
        for (int i = 0; i < times.Count; i++)
        {
            double error = (network.Impedance(times[i]) - impedance[i]) / final;
            worst = Math.Max(worst, Math.Abs(error));
            squares += error * error;
        }
        return new FosterFit(network, worst, Math.Sqrt(squares / times.Count));
    }

    /// <summary>
    /// The curve's time-constant spectrum on a grid of <paramref name="perDecade"/> time
    /// constants per decade, by non-negative least squares; neighbouring active grid points
    /// are returned merged (a time constant between two grid points shows up on both).
    /// </summary>
    private static List<(double R, double Tau)> Spectrum(IReadOnlyList<double> times, IReadOnlyList<double> impedance,
        int perDecade, bool merge = true)
    {
        double lo = Math.Log10(times[0]) - 0.5, hi = Math.Log10(times[^1]) + 0.5;
        int count = Math.Max(2, (int)Math.Ceiling((hi - lo) * perDecade) + 1);
        var taus = Enumerable.Range(0, count).Select(i => Math.Pow(10, lo + (hi - lo) * i / (count - 1))).ToArray();
        double final = impedance.Max();
        var weights = new double[times.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = 1 / (impedance[i] + 0.02 * final);
        var r = SolveResistances(times, impedance, weights, taus.Select(tau => Math.Log(tau)).ToArray());
        var active = new List<(double R, double Tau)>();
        int k = 0;
        while (k < count)
        {
            if (!(r[k] > 1e-9 * final)) { k++; continue; }
            if (!merge) { active.Add((r[k], taus[k])); k++; continue; }
            double sumR = 0, sumLog = 0;
            while (k < count && r[k] > 1e-9 * final)
            {
                sumR += r[k];
                sumLog += r[k] * Math.Log(taus[k]);
                k++;
            }
            active.Add((sumR, Math.Exp(sumLog / sumR)));
        }
        if (active.Count == 0) active.Add((final, Math.Sqrt(times[0] * times[^1])));
        return active;
    }

    private static double[] SolveResistances(IReadOnlyList<double> times, IReadOnlyList<double> impedance,
        double[] weights, double[] logTau)
    {
        int m = times.Count, n = logTau.Length;
        var a = new double[m, n];
        var b = new double[m];
        for (int i = 0; i < m; i++)
        {
            b[i] = impedance[i] * weights[i];
            for (int j = 0; j < n; j++) a[i, j] = FosterNetwork.OneMinusExp(times[i] / Math.Exp(logTau[j])) * weights[i];
        }
        return NonNegativeLeastSquares.Solve(a, b);
    }

    /// <summary>
    /// The Cauer ladder with the same terminal impedance as a Foster network. Z(s) is a
    /// Stieltjes function, bᵀ(sI + Λ)⁻¹b with Λ = diag(1/τᵢ) and bᵢ² = Rᵢ/τᵢ; the Lanczos
    /// recursion started from b turns Λ into the tridiagonal matrix of the ladder, and the
    /// ladder's C's and R's are read off it in order. Done in double-double arithmetic: in
    /// plain doubles the recursion loses the slow stages of a network spanning many decades.
    /// </summary>
    public static CauerNetwork ToCauer(FosterNetwork foster)
    {
        int n = foster.Stages;
        if (n == 0) throw new ArgumentException("The network has no stages.");
        var order = Enumerable.Range(0, n).OrderBy(i => foster.TimeConstants[i]).ToArray();
        for (int i = 1; i < n; i++)
            if (!(foster.TimeConstants[order[i]] > foster.TimeConstants[order[i - 1]] * (1 + 1e-9)))
                throw new InvalidOperationException("Two stages share a time constant; merge them before converting.");
        var lambda = order.Select(i => DoubleDouble.One / foster.TimeConstants[i]).ToArray();
        var weight = order.Select((i, k) => (DoubleDouble)foster.Resistances[i] * lambda[k]).ToArray();
        DoubleDouble total = DoubleDouble.Zero;
        foreach (var w in weight) total += w;

        // Lanczos with full reorthogonalisation.
        var basis = new List<DoubleDouble[]>();
        var q = weight.Select(w => DoubleDouble.Sqrt(w / total)).ToArray();
        var alpha = new DoubleDouble[n];
        var betaSquared = new DoubleDouble[n];
        for (int k = 0; k < n; k++)
        {
            basis.Add(q);
            var v = new DoubleDouble[n];
            DoubleDouble a = DoubleDouble.Zero;
            for (int i = 0; i < n; i++)
            {
                v[i] = lambda[i] * q[i];
                a += q[i] * v[i];
            }
            alpha[k] = a;
            for (int pass = 0; pass < 2; pass++)
                foreach (var p in basis)
                {
                    DoubleDouble dot = DoubleDouble.Zero;
                    for (int i = 0; i < n; i++) dot += p[i] * v[i];
                    for (int i = 0; i < n; i++) v[i] -= dot * p[i];
                }
            DoubleDouble norm = DoubleDouble.Zero;
            for (int i = 0; i < n; i++) norm += v[i] * v[i];
            betaSquared[k] = norm;
            if (k == n - 1) break;
            var root = DoubleDouble.Sqrt(norm);
            q = v.Select(x => x / root).ToArray();
        }

        // Ladder from the tridiagonal matrix: J₁₁ = g₁/C₁, J_{k,k+1}² = g_k²/(C_k·C_{k+1}),
        // J_kk = (g_{k−1} + g_k)/C_k.
        var resistances = new double[n];
        var capacitances = new double[n];
        DoubleDouble c = DoubleDouble.One / total, previous = DoubleDouble.Zero;
        for (int k = 0; k < n; k++)
        {
            DoubleDouble g = alpha[k] * c - previous;
            if (!(g.Value > 0) || !(c.Value > 0) || double.IsNaN(g.Value))
                throw new InvalidOperationException(
                    "The Foster network could not be turned into a ladder: its stages are too close together or too many for the arithmetic.");
            capacitances[k] = c.Value;
            resistances[k] = (DoubleDouble.One / g).Value;
            if (k + 1 < n) c = g * g / (betaSquared[k] * c);
            previous = g;
        }
        return new CauerNetwork(resistances, capacitances);
    }

    /// <summary>
    /// The structure function of a curve, by the steps of the JEDEC transient method: the
    /// time-constant spectrum of Z_th(t), discretised into a Foster chain, the chain turned
    /// into a Cauer ladder, and ΣC against ΣR along the ladder. The spectrum here comes
    /// from a non-negative least-squares fit on a grid of time constants rather than from
    /// deconvolving the curve's logarithmic derivative; both resolve the same thing and
    /// both smear a sharp step in the structure over a fraction of a decade.
    /// </summary>
    public static StructureFunction Structure(IReadOnlyList<double> times, IReadOnlyList<double> impedance,
        int timeConstantsPerDecade = 10)
    {
        var spectrum = Spectrum(times, impedance, timeConstantsPerDecade, merge: false).OrderBy(s => s.Tau).ToList();
        var foster = new FosterNetwork(spectrum.Select(s => s.R).ToList(), spectrum.Select(s => s.Tau).ToList());
        var ladder = ToCauer(foster);
        var (r, c) = ladder.Cumulative();
        return new StructureFunction(r, c, foster, ladder);
    }

    /// <summary>
    /// Where two curves of the same part part company — the reading of the transient
    /// dual-interface measurement: the same die and package on two different interfaces
    /// to the cold plate share their curve up to the case and diverge after it. Returns the
    /// impedance at the first time the curves differ by more than
    /// <paramref name="threshold"/> [K/W], with that time; NaN when they never do. The
    /// threshold is the caller's: the JEDEC procedure fixes its own criterion, which is not
    /// reproduced here.
    /// </summary>
    public static (double KelvinPerWatt, double TimeSeconds) Separation(ThermalImpedanceCurve first,
        ThermalImpedanceCurve second, double threshold)
    {
        var times = first.TimesSeconds;
        for (int i = 0; i < times.Count; i++)
        {
            double a = first.KelvinPerWatt[i], b = second.At(times[i]);
            if (Math.Abs(a - b) <= threshold) continue;
            return (0.5 * (a + b), times[i]);
        }
        return (double.NaN, double.NaN);
    }
}

/// <summary>Non-negative least squares, min ‖A·x − b‖ with x ≥ 0, by the Lawson–Hanson
/// active-set method. Dense; for the tens of unknowns of a time-constant fit.</summary>
internal static class NonNegativeLeastSquares
{
    public static double[] Solve(double[,] a, double[] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var x = new double[n];
        var passive = new bool[n];
        var gradient = new double[n];
        var residual = (double[])b.Clone();
        double scale = 0;
        for (int i = 0; i < m; i++) scale = Math.Max(scale, Math.Abs(b[i]));
        double tolerance = 1e-12 * Math.Max(scale, 1e-300) * m;

        for (int outer = 0; outer < 3 * n + 10; outer++)
        {
            for (int j = 0; j < n; j++)
            {
                double g = 0;
                for (int i = 0; i < m; i++) g += a[i, j] * residual[i];
                gradient[j] = g;
            }
            int enter = -1;
            double best = tolerance;
            for (int j = 0; j < n; j++)
                if (!passive[j] && gradient[j] > best) (best, enter) = (gradient[j], j);
            if (enter < 0) break;
            passive[enter] = true;

            for (int inner = 0; inner < 3 * n + 10; inner++)
            {
                var columns = Enumerable.Range(0, n).Where(j => passive[j]).ToArray();
                var s = LeastSquares(a, b, columns);
                bool feasible = true;
                for (int c = 0; c < columns.Length; c++) feasible &= s[c] > 0;
                if (feasible)
                {
                    Array.Clear(x);
                    for (int c = 0; c < columns.Length; c++) x[columns[c]] = s[c];
                    break;
                }
                // Step toward s as far as the non-negativity allows, and drop what hits zero.
                double alpha = 1;
                for (int c = 0; c < columns.Length; c++)
                    if (s[c] <= 0)
                    {
                        double xj = x[columns[c]];
                        alpha = Math.Min(alpha, xj / (xj - s[c]));
                    }
                for (int c = 0; c < columns.Length; c++)
                {
                    int j = columns[c];
                    x[j] += alpha * (s[c] - x[j]);
                    if (x[j] <= 1e-14 * Math.Abs(s[c]) || x[j] <= 0) { x[j] = 0; passive[j] = false; }
                }
            }
            for (int i = 0; i < m; i++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++) if (x[j] != 0) sum += a[i, j] * x[j];
                residual[i] = b[i] - sum;
            }
        }
        return x;
    }

    /// <summary>Unconstrained least squares on some columns, by Householder QR.</summary>
    private static double[] LeastSquares(double[,] a, double[] b, int[] columns)
    {
        int m = a.GetLength(0), n = columns.Length;
        var q = new double[m, n];
        for (int i = 0; i < m; i++)
            for (int c = 0; c < n; c++) q[i, c] = a[i, columns[c]];
        var rhs = (double[])b.Clone();
        var diagonal = new double[n];
        for (int k = 0; k < n; k++)
        {
            double norm = 0;
            for (int i = k; i < m; i++) norm += q[i, k] * q[i, k];
            norm = Math.Sqrt(norm);
            if (norm == 0) { diagonal[k] = 0; continue; }
            if (q[k, k] > 0) norm = -norm;
            for (int i = k; i < m; i++) q[i, k] /= -norm;
            q[k, k] += 1;
            for (int j = k + 1; j < n; j++)
            {
                double dot = 0;
                for (int i = k; i < m; i++) dot += q[i, k] * q[i, j];
                dot = -dot / q[k, k];
                for (int i = k; i < m; i++) q[i, j] += dot * q[i, k];
            }
            double d = 0;
            for (int i = k; i < m; i++) d += q[i, k] * rhs[i];
            d = -d / q[k, k];
            for (int i = k; i < m; i++) rhs[i] += d * q[i, k];
            diagonal[k] = norm;
        }
        var x = new double[n];
        for (int k = n - 1; k >= 0; k--)
        {
            double sum = rhs[k];
            for (int j = k + 1; j < n; j++) sum -= q[k, j] * x[j];
            x[k] = diagonal[k] != 0 ? sum / diagonal[k] : 0;
        }
        return x;
    }
}

/// <summary>A number as an unevaluated sum of two doubles — about 31 significant digits,
/// for the one recursion here that needs them.</summary>
internal readonly struct DoubleDouble
{
    private readonly double _hi, _lo;
    private DoubleDouble(double hi, double lo) { _hi = hi; _lo = lo; }

    public static readonly DoubleDouble Zero = new(0, 0);
    public static readonly DoubleDouble One = new(1, 0);
    public double Value => _hi + _lo;

    public static implicit operator DoubleDouble(double value) => new(value, 0);

    private static DoubleDouble Renormalize(double hi, double lo)
    {
        double s = hi + lo;
        return new DoubleDouble(s, lo - (s - hi));
    }

    public static DoubleDouble operator +(DoubleDouble a, DoubleDouble b)
    {
        double s = a._hi + b._hi, v = s - a._hi;
        double e = (a._hi - (s - v)) + (b._hi - v) + a._lo + b._lo;
        return Renormalize(s, e);
    }

    public static DoubleDouble operator -(DoubleDouble a) => new(-a._hi, -a._lo);
    public static DoubleDouble operator -(DoubleDouble a, DoubleDouble b) => a + (-b);

    public static DoubleDouble operator *(DoubleDouble a, DoubleDouble b)
    {
        double p = a._hi * b._hi;
        double e = Math.FusedMultiplyAdd(a._hi, b._hi, -p) + a._hi * b._lo + a._lo * b._hi;
        return Renormalize(p, e);
    }

    public static DoubleDouble operator /(DoubleDouble a, DoubleDouble b)
    {
        double q1 = a._hi / b._hi;
        var r = a - b * q1;
        double q2 = r._hi / b._hi;
        r -= b * q2;
        double q3 = r._hi / b._hi;
        return Renormalize(q1, q2) + q3;
    }

    public static DoubleDouble Sqrt(DoubleDouble a)
    {
        if (a._hi <= 0) return Zero;
        double x = Math.Sqrt(a._hi);
        // One Newton step in double-double.
        var estimate = (DoubleDouble)x;
        return estimate + (a - estimate * estimate) / (estimate + estimate);
    }
}
