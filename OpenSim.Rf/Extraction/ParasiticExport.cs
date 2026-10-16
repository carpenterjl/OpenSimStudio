using System.Globalization;
using System.Numerics;
using System.Text;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Extraction;

/// <summary>A wideband SPICE subcircuit and how well its fit holds the data.</summary>
/// <param name="WorstFitFraction">Largest |fit − data| over the sweep, as a fraction of |Z|
/// at that point (floored at a thousandth of the entry's largest |Z|).</param>
/// <param name="MinimumResistanceEigenvalue">Smallest eigenvalue of Re Z of the FIT over the
/// sweep [Ω]: negative means the fitted model is not passive there.</param>
/// <param name="NegativeElements">R, L or C elements that came out negative — they realize
/// the fit exactly (SPICE accepts them), but a transient simulator may mind.</param>
public sealed record WidebandSubcircuit(string Text, double WorstFitFraction,
    double MinimumResistanceEigenvalue, int NegativeElements, IReadOnlyList<string> Notes);

/// <summary>A capacitor of an exported subcircuit, between two of its pins (or pin and
/// ground, "0").</summary>
public sealed record ExportCapacitor(string NodeA, string NodeB, double Farads);

/// <summary>
/// Extracted resistance and inductance as files a circuit simulator reads.
/// </summary>
public static class ParasiticExport
{
    /// <summary>
    /// A SPICE subcircuit for a port impedance matrix Z = R + jωL at ONE frequency: port k
    /// lies between pins <c>pk</c> and <c>nk</c> and is a resistor R_kk and an inductor L_kk
    /// in series; inductors are coupled by K statements (k = L_ij/√(L_ii·L_jj)), and the
    /// resistance two ports share (R_ij, the loss one port's current causes in the other's
    /// path) by current-controlled voltage sources. R and L move with frequency through
    /// skin and proximity effect, and a subcircuit of fixed elements holds them at the
    /// frequency named in its header — the Touchstone export carries the whole sweep.
    /// </summary>
    public static string SpiceSubcircuit(string name, IReadOnlyList<string> portNames, Complex[,] impedance,
        double frequencyHz, IReadOnlyList<ExportCapacitor>? capacitors = null)
    {
        int n = impedance.GetLength(0);
        if (portNames.Count != n) throw new ArgumentException($"Expected {n} port names.");
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        double omega = 2 * Math.PI * frequencyHz;
        var r = new double[n, n];
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                r[i, j] = impedance[i, j].Real;
                l[i, j] = impedance[i, j].Imaginary / omega;
            }
        for (int i = 0; i < n; i++)
            if (!(l[i, i] > 0 && r[i, i] >= 0))
                throw new InvalidOperationException(
                    $"Port {i + 1} ({portNames[i]}) has no positive inductance at {frequencyHz:g4} Hz; "
                    + "a series R–L subcircuit cannot represent it.");

        string id = new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var text = new StringBuilder();
        text.AppendLine($"* OpenSim Studio extracted parasitics: {name}");
        text.AppendLine(Inv($"* R and L as solved at {frequencyHz:G6} Hz (they change with frequency)."));
        for (int k = 0; k < n; k++) text.AppendLine($"* port {k + 1} (p{k + 1}, n{k + 1}): {portNames[k]}");
        text.AppendLine($".subckt {id} {string.Join(" ", Enumerable.Range(1, n).Select(k => $"p{k} n{k}"))}");
        for (int i = 0; i < n; i++)
        {
            int k = i + 1;
            text.AppendLine(Inv($"R{k} p{k} a{k} {r[i, i]:G9}"));
            text.AppendLine($"V{k} a{k} b{k} 0");
            // The shared-resistance sources sit between the inductor and the negative pin.
            var shared = Enumerable.Range(0, n).Where(j => j != i && Math.Abs(r[i, j]) > 1e-9 * Math.Sqrt(Math.Max(r[i, i] * r[j, j], 1e-300))).ToList();
            string node = shared.Count == 0 ? $"n{k}" : $"c{k}_0";
            text.AppendLine(Inv($"L{k} b{k} {node} {l[i, i]:G9}"));
            for (int s = 0; s < shared.Count; s++)
            {
                int j = shared[s];
                string next = s == shared.Count - 1 ? $"n{k}" : $"c{k}_{s + 1}";
                text.AppendLine(Inv($"H{k}_{j + 1} c{k}_{s} {next} V{j + 1} {r[i, j]:G9}"));
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double k = l[i, j] / Math.Sqrt(l[i, i] * l[j, j]);
                if (Math.Abs(k) < 1e-9) continue;
                text.AppendLine(Inv($"K{i + 1}_{j + 1} L{i + 1} L{j + 1} {Math.Clamp(k, -0.999999, 0.999999):G9}"));
            }
        if (capacitors is not null)
            for (int c = 0; c < capacitors.Count; c++)
                text.AppendLine(Inv($"C{c + 1} {capacitors[c].NodeA} {capacitors[c].NodeB} {capacitors[c].Farads:G9}"));
        text.AppendLine($".ends {id}");
        return text.ToString();
    }

    /// <summary>
    /// A WIDEBAND SPICE subcircuit (FU-33): each entry of the swept impedance matrix is fitted
    /// as sL∞ + d + Σ r/(s − p) by vector fitting (relative weighting, poles added until the
    /// fit is within a part in 10⁴ of |Z| or <paramref name="maxPolePairs"/> pairs are spent),
    /// and realized with real elements — the series inductance and resistance, a parallel R‖C
    /// per real pole and an R‖C‖(R–L) block per complex pair — valid over the whole sweep, not
    /// at one frequency. Off-diagonal entries couple through a grounded copy of the same
    /// realization driven by the other port's current (one F and one E source per direction);
    /// the diagonal path carries only plain elements. A fit term that comes out non-passive is
    /// realized exactly with a negative element and counted in the result.
    /// </summary>
    public static WidebandSubcircuit SpiceWideband(string name, IReadOnlyList<string> portNames,
        IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex[,]> impedance, int maxPolePairs = 6,
        IReadOnlyList<ExportCapacitor>? capacitors = null)
    {
        int n = impedance.Count > 0 ? impedance[0].GetLength(0) : 0;
        if (portNames.Count != n) throw new ArgumentException($"Expected {n} port names.");
        if (frequenciesHz.Count != impedance.Count) throw new ArgumentException("One matrix per frequency.");
        if (frequenciesHz.Count < 6)
            throw new ArgumentException("A wideband model needs a swept extraction: at least 6 frequencies.");
        if (maxPolePairs < 1) throw new ArgumentOutOfRangeException(nameof(maxPolePairs));

        // ---- Fit every entry (the matrix is symmetrized first, as the solve already is).
        var fits = new EntryFit[n, n];
        double worst = 0;
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                var values = new Complex[frequenciesHz.Count];
                for (int k = 0; k < values.Length; k++)
                    values[k] = 0.5 * (impedance[k][i, j] + impedance[k][j, i]);
                var fit = FitEntry(frequenciesHz, values, maxPolePairs);
                fits[i, j] = fits[j, i] = fit;
                worst = Math.Max(worst, fit.WorstFraction);
            }

        // ---- Passivity of the fit: the smallest eigenvalue of Re Z over the sweep.
        double minimumEigenvalue = double.MaxValue;
        foreach (double f in frequenciesHz)
        {
            var real = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) real[i, j] = fits[i, j].At(f).Real;
            minimumEigenvalue = Math.Min(minimumEigenvalue, SmallestEigenvalue(real));
        }

        // ---- The netlist.
        string id = new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var text = new StringBuilder();
        int negatives = 0;
        text.AppendLine($"* OpenSim Studio extracted parasitics (wideband): {name}");
        text.AppendLine(Inv($"* Fitted over {frequenciesHz[0]:G4}–{frequenciesHz[^1]:G4} Hz; worst deviation {worst:P2} of |Z|."));
        string passive = minimumEigenvalue < 0 ? " (the fit is NOT passive there)" : "";
        text.AppendLine(Inv($"* Smallest eigenvalue of Re Z over the sweep: {minimumEigenvalue:G4} ohm{passive}."));
        for (int k = 0; k < n; k++)
            text.AppendLine($"* port {k + 1} (p{k + 1}, n{k + 1}): {portNames[k]}"
                + Inv($" — order {fits[k, k].Order}, worst {fits[k, k].WorstFraction:P2}"));
        text.AppendLine($".subckt {id} {string.Join(" ", Enumerable.Range(1, n).Select(k => $"p{k} n{k}"))}");
        for (int i = 0; i < n; i++)
        {
            int k = i + 1;
            // The diagonal chain, then one E per coupled port, then the current sensor.
            var couplings = Enumerable.Range(0, n).Where(j => j != i && !fits[i, j].IsZero).ToList();
            string node = Realize(text, fits[i, i], $"p{k}", $"{k}", ref negatives);
            for (int c = 0; c < couplings.Count; c++)
            {
                int j = couplings[c];
                string next = $"e{k}_{c + 1}";
                text.AppendLine($"E{k}_{j + 1} {node} {next} x{k}_{j + 1} 0 1");
                node = next;
            }
            text.AppendLine($"V{k} {node} n{k} 0");
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i == j || fits[i, j].IsZero) continue;
                // v(x) = Z_ij(s) · I_j, sensed into port i's chain by E above.
                int k = i + 1, m = j + 1;
                text.AppendLine($"F{k}_{m} 0 x{k}_{m} V{m} 1");
                string last = Realize(text, fits[i, j], $"x{k}_{m}", $"{k}_{m}", ref negatives);
                text.AppendLine(Inv($"Rt{k}_{m} {last} 0 {1e-12:G9}"));
            }
        if (capacitors is not null)
            for (int c = 0; c < capacitors.Count; c++)
                text.AppendLine(Inv($"C{c + 1} {capacitors[c].NodeA} {capacitors[c].NodeB} {capacitors[c].Farads:G9}"));
        text.AppendLine($".ends {id}");

        var notes = new List<string>
        {
            Inv($"Wideband subcircuit: every entry fitted to {worst:P2} of |Z| over {frequenciesHz[0]:G4}–{frequenciesHz[^1]:G4} Hz; outside the sweep the model extrapolates its poles."),
        };
        notes.Add(minimumEigenvalue < 0
            ? Inv($"The fitted model is not passive everywhere (smallest eigenvalue of Re Z {minimumEigenvalue:G3} ohm): a transient run can see gain there. Refit with more points or fewer poles if it misbehaves.")
            : "The fitted model is passive over the sweep (Re Z stayed positive definite).");
        if (negatives > 0)
            notes.Add($"{negatives} element(s) are negative: they realize the fit exactly and most simulators accept "
                + "them, but they are worth knowing about.");
        return new WidebandSubcircuit(text.ToString(), worst, minimumEigenvalue, negatives, notes);
    }

    private sealed record EntryFit(double SeriesHenries, double ConstantOhms, Complex[] Poles, Complex[] Residues,
        double WorstFraction)
    {
        public bool IsZero => SeriesHenries == 0 && ConstantOhms == 0 && Poles.Length == 0;
        public int Order => Poles.Sum(p => p.Imaginary == 0 ? 1 : 2);

        /// <summary>The fitted impedance at a frequency (poles and residues in rad/s).</summary>
        public Complex At(double frequencyHz)
        {
            var s = new Complex(0, 2 * Math.PI * frequencyHz);
            Complex sum = ConstantOhms + s * SeriesHenries;
            for (int k = 0; k < Poles.Length; k++)
            {
                sum += Residues[k] / (s - Poles[k]);
                if (Poles[k].Imaginary != 0) sum += Complex.Conjugate(Residues[k]) / (s - Complex.Conjugate(Poles[k]));
            }
            return sum;
        }
    }

    private static EntryFit FitEntry(IReadOnlyList<double> frequenciesHz, Complex[] values, int maxPolePairs)
    {
        double scale = values.Max(v => v.Magnitude);
        if (scale == 0) return new EntryFit(0, 0, Array.Empty<Complex>(), Array.Empty<Complex>(), 0);
        // The high-frequency inductance first, so the remainder is bounded; then a relative fit.
        double omegaTop = 2 * Math.PI * frequenciesHz[^1];
        double seriesHenries = values[^1].Imaginary / omegaTop;
        var remainder = new Complex[values.Length];
        var weights = new double[values.Length];
        for (int k = 0; k < values.Length; k++)
        {
            remainder[k] = values[k] - new Complex(0, 2 * Math.PI * frequenciesHz[k] * seriesHenries);
            weights[k] = 1 / Math.Max(values[k].Magnitude, 1e-3 * scale);
        }
        RationalResponse? best = null;
        double bestWorst = double.MaxValue;
        int pairLimit = Math.Min(maxPolePairs, (frequenciesHz.Count - 2) / 2);
        for (int pairs = 1; pairs <= Math.Max(1, pairLimit); pairs++)
        {
            var model = VectorFit.Fit(frequenciesHz, remainder, pairs, weights: weights);
            double worst = 0;
            for (int k = 0; k < values.Length; k++)
                worst = Math.Max(worst, (model.At(frequenciesHz[k]) - remainder[k]).Magnitude * weights[k]);
            if (worst < bestWorst) { bestWorst = worst; best = model; }
            if (bestWorst < 1e-4) break;
        }
        // Physical units: the fit's s is jf/f_n, so poles and residues scale by 2π·f_n.
        double radPerSecond = 2 * Math.PI * best!.FrequencyScaleHz;
        var poles = best.Poles.Select(p => p * radPerSecond).ToArray();
        var residues = best.Residues.Select(r => r * radPerSecond).ToArray();
        return new EntryFit(seriesHenries, best.Constant, poles, residues, bestWorst);
    }

    /// <summary>
    /// Writes the chain of elements for one fitted entry starting at <paramref name="from"/>
    /// and returns the node the chain reached (the caller closes it). Every pole is plain R,
    /// L, C: a real pole is R‖C, a complex pair is C‖R‖(R–L).
    /// </summary>
    private static string Realize(StringBuilder text, EntryFit fit, string from, string tag,
        ref int negatives)
    {
        int step = 0;
        int below = 0;
        string node = from;
        string Next() => $"w{tag}_{++step}";
        void Count(params double[] values)
        {
            foreach (double v in values) if (v < 0) below++;
        }
        void Element(string kind, string a, string b, double value)
        {
            text.AppendLine(Inv($"{kind}{tag}_{step} {a} {b} {value:G9}"));
        }
        if (fit.ConstantOhms != 0)
        {
            string next = Next();
            Element("R", node, next, fit.ConstantOhms);
            Count(fit.ConstantOhms);
            node = next;
        }
        if (fit.SeriesHenries != 0)
        {
            string next = Next();
            Element("L", node, next, fit.SeriesHenries);
            Count(fit.SeriesHenries);
            node = next;
        }
        for (int k = 0; k < fit.Poles.Length; k++)
        {
            Complex p = fit.Poles[k], r = fit.Residues[k];
            string next = Next();
            if (p.Imaginary == 0)
            {
                // r/(s − p) = 1/(C·s + G): C = 1/r, R = −r/p.
                double c = 1 / r.Real, resistor = -r.Real / p.Real;
                Element("C", node, next, c);
                text.AppendLine(Inv($"Rp{tag}_{step} {node} {next} {resistor:G9}"));
                Count(c, resistor);
            }
            else
            {
                // The pair (c1·s + c0)/(s² + d1·s + d0): its admittance divides into
                // C·s + G + 1/(L·s + R).
                double c1 = 2 * r.Real;
                double c0 = -2 * (r * Complex.Conjugate(p)).Real;
                double d1 = -2 * p.Real;
                double d0 = p.Magnitude * p.Magnitude;
                if (Math.Abs(c1) * p.Magnitude < 1e-12 * Math.Abs(c0))
                {
                    // A numerator with no s: nothing a passive block holds; keep its DC part.
                    double dc = c0 / d0;
                    Element("R", node, next, dc);
                    Count(dc);
                    node = next;
                    continue;
                }
                double cap = 1 / c1;
                double conductance = (d1 - c0 / c1) / c1;
                double rem = d0 - c0 * (d1 - c0 / c1) / c1;
                Element("C", node, next, cap);
                Count(cap);
                if (conductance != 0)
                {
                    double resistor = 1 / conductance;
                    text.AppendLine(Inv($"Rp{tag}_{step} {node} {next} {resistor:G9}"));
                    Count(resistor);
                }
                if (rem != 0)
                {
                    double inductor = c1 / rem, series = c0 / rem;
                    string middle = $"w{tag}_{step}m";
                    text.AppendLine(Inv($"Ls{tag}_{step} {node} {middle} {inductor:G9}"));
                    text.AppendLine(Inv($"Rs{tag}_{step} {middle} {next} {series:G9}"));
                    Count(inductor, series);
                }
            }
            node = next;
        }
        if (node == from)
        {
            // Nothing to write (a zero entry): a near-zero link keeps the chain whole.
            string next = Next();
            Element("R", node, next, 1e-12);
            node = next;
        }
        negatives += below;
        return node;
    }

    private static double SmallestEigenvalue(double[,] symmetric)
    {
        int n = symmetric.GetLength(0);
        if (n == 1) return symmetric[0, 0];
        // Jacobi sweeps; n is the port count, a handful at most.
        var a = (double[,])symmetric.Clone();
        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            if (off < 1e-24) break;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (a[i, j] == 0) continue;
                    double theta = 0.5 * Math.Atan2(2 * a[i, j], a[j, j] - a[i, i]);
                    double c = Math.Cos(theta), s = Math.Sin(theta);
                    for (int k = 0; k < n; k++)
                    {
                        double aik = a[i, k], ajk = a[j, k];
                        a[i, k] = c * aik - s * ajk;
                        a[j, k] = s * aik + c * ajk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double aki = a[k, i], akj = a[k, j];
                        a[k, i] = c * aki - s * akj;
                        a[k, j] = s * aki + c * akj;
                    }
                }
        }
        double smallest = double.MaxValue;
        for (int i = 0; i < n; i++) smallest = Math.Min(smallest, a[i, i]);
        return smallest;
    }

    /// <summary>The swept port impedance as Touchstone S-parameters.</summary>
    public static string Touchstone(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex[,]> impedance,
        IReadOnlyList<string> portNames, double referenceOhms = 50) =>
        TouchstoneWriter.Write(frequenciesHz,
            impedance.Select(z => NetworkParameters.ImpedanceToScattering(z, referenceOhms)).ToList(),
            referenceOhms, portNames);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
