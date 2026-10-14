using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Channel;

public sealed record ChannelRepairOptions
{
    /// <summary>Stop adding poles to an entry once its fit is this close to the file
    /// everywhere (absolute, in S).</summary>
    public double FitTolerance { get; init; } = 1e-4;

    /// <summary>Most complex pole pairs per entry.</summary>
    public int MaxPolePairs { get; init; } = 64;

    public bool EnforcePassivity { get; init; } = true;

    /// <summary>After repair the largest singular value of S is at most 1 − this, on a grid
    /// four times finer than the file's.</summary>
    public double PassivityMargin { get; init; } = 1e-4;
}

/// <summary>What a repair did.</summary>
/// <param name="Repaired">The repaired data at the file's own frequencies.</param>
/// <param name="CausalDeviation">Largest |causal model − file| over every entry and point:
/// how far the file is from the nearest causal response the fit found.</param>
/// <param name="PassivityChange">Largest change the passivity step made to the causal model.</param>
/// <param name="LargestSingularValueBefore">‖S‖₂ at worst over the file's points.</param>
/// <param name="LargestSingularValueAfter">‖S‖₂ at worst over the repaired model, on a grid
/// four times finer than the file's.</param>
/// <param name="Poles">Poles per entry (a complex pair counts two), row by row.</param>
/// <param name="PassivityEnforced">Whether the passivity step ran.</param>
public sealed record ChannelRepairResult(TouchstoneData Repaired, double CausalDeviation, double PassivityChange,
    double LargestSingularValueBefore, double LargestSingularValueAfter, int[,] Poles, bool PassivityEnforced,
    IReadOnlyList<string> Notes)
{
    /// <summary>Above this the file is reported as not causal.</summary>
    public const double CausalityTolerance = 0.01;

    public bool IsCausal => CausalDeviation <= CausalityTolerance;

    public IReadOnlyList<string> Describe()
    {
        int largest = Poles.Cast<int>().Max();
        bool wasPassive = LargestSingularValueBefore <= 1 + NetworkChecks.PassivityTolerance;
        var lines = new List<string>
        {
            IsCausal
                ? $"Causal: a causal model (at most {largest} poles per entry) follows the file to {CausalDeviation:e1}."
                : $"NOT CAUSAL: the nearest causal model (up to {largest} poles per entry) is {CausalDeviation:f3} away from " +
                  "the file somewhere — a response before its cause, or a frequency step too coarse for the data.",
            !PassivityEnforced
                ? $"{(wasPassive ? "Passive" : "NOT PASSIVE")}: ‖S‖ {LargestSingularValueBefore:f4} in the file " +
                  $"({LargestSingularValueAfter:f4} in the causal model); passivity was not enforced."
                : wasPassive
                    ? $"Passive: ‖S‖ {LargestSingularValueBefore:f4} in the file, {LargestSingularValueAfter:f4} repaired."
                    : $"Passivity enforced: ‖S‖ was {LargestSingularValueBefore:f4}, now {LargestSingularValueAfter:f4} " +
                      $"(largest change {PassivityChange:e1})."
        };
        lines.AddRange(Notes);
        return lines;
    }
}

/// <summary>
/// Causality and passivity enforcement for imported S-parameters. Each entry is fitted by
/// <see cref="VectorFit"/> as a delay times a rational function with stable poles — causal by
/// construction — with poles added until it follows the file to
/// <see cref="ChannelRepairOptions.FitTolerance"/> or stops improving; the delay is where
/// the entry's impulse response first rises (taken early, so no part of the response is put
/// before it). Passivity is then enforced on that model: wherever a singular value of S
/// exceeds 1 − margin the excess is taken off along its singular vectors,
/// ΔS = U·(min(Σ, 1 − margin) − Σ)·Vᴴ, and the change is fitted with each entry's own poles
/// and added to its residues — the poles, and so causality, stay as they were. The
/// repaired file is the model at the file's frequencies.
/// </summary>
public static class ChannelRepair
{
    public static ChannelRepairResult Repair(TouchstoneData data, ChannelRepairOptions? options = null)
    {
        options ??= new ChannelRepairOptions();
        var channel = new ImportedChannel(data);
        int n = data.Ports, points = data.FrequenciesHz.Count;
        var frequencies = data.FrequenciesHz;
        var notes = new List<string>();
        int cap = Math.Max(1, Math.Min(options.MaxPolePairs, (points - 2) / 2));
        if (cap < options.MaxPolePairs)
            notes.Add($"{points} points carry at most {cap} pole pairs per entry.");

        bool reciprocal = n > 1 && channel.Checks.All(c => c.IsReciprocal());
        if (reciprocal) notes.Add("Reciprocal: S_ij and S_ji are fitted as one (their mean), so the repair keeps S = Sᵀ.");
        var entries = new List<(int Row, int Column)>();
        for (int i = 0; i < n; i++)
            for (int j = reciprocal ? i : 0; j < n; j++) entries.Add((i, j));

        var models = new RationalResponse[entries.Count];
        var deviations = new double[entries.Count];
        Parallel.For(0, entries.Count, e =>
        {
            var (i, j) = entries[e];
            var values = new Complex[points];
            for (int k = 0; k < points; k++)
                values[k] = reciprocal ? 0.5 * (data.Scattering[k][i, j] + data.Scattering[k][j, i]) : data.Scattering[k][i, j];
            double delay = EarliestArrival(channel, i, j);
            // Start from as many pole pairs as the delay-free response has turns and double
            // until the fit is close enough — or two doublings in a row have not halved the
            // misfit (vector fitting often sits on a plateau for one doubling before the
            // misfit falls away, so one is not enough to give up on).
            int start = Math.Clamp(Turns(frequencies, values, delay), 2, cap);
            RationalResponse? best = null;
            double bestDeviation = double.MaxValue;
            int stalled = 0;
            for (int pairs = start; ; pairs = Math.Min(2 * pairs, cap))
            {
                var model = VectorFit.Fit(frequencies, values, pairs, delay);
                double deviation = VectorFit.LargestDeviation(model, frequencies, values);
                stalled = deviation < 0.5 * bestDeviation ? 0 : stalled + 1;
                if (deviation < bestDeviation) { best = model; bestDeviation = deviation; }
                if (bestDeviation <= options.FitTolerance || pairs >= cap || stalled >= 2) break;
            }
            models[e] = best!;
            deviations[e] = bestDeviation;
        });
        // A reciprocal file's own asymmetry counts against the fit to either entry.
        double causal = 0;
        for (int e = 0; e < entries.Count; e++)
        {
            var (i, j) = entries[e];
            double worst = deviations[e];
            if (reciprocal && i != j)
                for (int k = 0; k < points; k++)
                {
                    Complex fit = models[e].At(frequencies[k]);
                    worst = Math.Max(worst, Math.Max((fit - data.Scattering[k][i, j]).Magnitude, (fit - data.Scattering[k][j, i]).Magnitude));
                }
            causal = Math.Max(causal, worst);
        }

        int[] index = new int[n * n];
        for (int e = 0; e < entries.Count; e++)
        {
            var (i, j) = entries[e];
            index[i * n + j] = e;
            index[j * n + i] = reciprocal ? e : index[j * n + i];
        }
        Complex[,] Evaluate(double f)
        {
            var s = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) s[i, j] = models[index[i * n + j]].At(f);
            return s;
        }

        double before = channel.Checks.Max(c => c.LargestSingularValue);
        var causalModels = models.ToArray();
        if (options.EnforcePassivity)
            EnforcePassivity(models, entries, index, n, frequencies, options.PassivityMargin, reciprocal, notes);
        double change = 0;
        var fine = Refine(frequencies, 4);
        foreach (double f in fine)
            for (int e = 0; e < entries.Count; e++)
                change = Math.Max(change, (models[e].At(f) - causalModels[e].At(f)).Magnitude);
        double after = fine.Max(f => NetworkChecks.LargestSingularValue(Evaluate(f)));

        var order = new int[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) order[i, j] = models[index[i * n + j]].Order;
        var repaired = new TouchstoneData(frequencies.ToArray(), frequencies.Select(Evaluate).ToList(), data.ReferenceOhms)
        {
            Version = data.Version, FileReferencesOhms = data.FileReferencesOhms,
            Notes = data.Notes.Concat(new[] { "Repaired: causal and passive model (vector fitting)." }).ToList()
        };
        return new ChannelRepairResult(repaired, causal, change, before, after, order, options.EnforcePassivity, notes);
    }

    /// <summary>
    /// Where entry (i, j)'s response begins, in a form safe to take out before fitting:
    /// the impulse response under the same Gaussian edge TDR uses (40 dB down at the last
    /// frequency), the first time it reaches 5 % of its peak, less two of the edge's
    /// standard deviations — the delay-free remainder then starts after t = 0 with room
    /// to spare, and anything genuinely earlier stays in it to be seen.
    /// </summary>
    internal static double EarliestArrival(ImportedChannel channel, int i, int j)
    {
        double fMax = channel.MaxFrequencyHz, step = Math.Min(channel.FinestStepHz, fMax / 256);
        double sigma = Math.Sqrt(2 * Math.Log(100)) / (2 * Math.PI * fMax);
        int size = 1;
        while (size < 8 * fMax / step) size <<= 1;
        size = Math.Min(size, 1 << 18);
        step = Math.Max(step, 8 * fMax / size);
        var spectrum = new Complex[size];
        for (int k = 0; k <= size / 2; k++)
        {
            double f = k * step;
            if (f > fMax) break;
            Complex value = channel.At(f)[i, j] * Math.Exp(-0.5 * Math.Pow(2 * Math.PI * f * sigma, 2));
            if (k == 0) value = value.Real;
            spectrum[k] = value;
            if (k > 0 && k < size / 2) spectrum[size - k] = Complex.Conjugate(value);
        }
        var impulse = Fft.Inverse(spectrum);
        double dt = 1.0 / (size * step), peak = 0;
        for (int k = 0; k < size; k++) peak = Math.Max(peak, Math.Abs(impulse[k].Real));
        if (peak == 0) return 0;
        for (int k = 0; k < size / 2; k++)
            if (Math.Abs(impulse[k].Real) >= 0.05 * peak)
                return Math.Max(0, k * dt - 2 * sigma);
        return 0;
    }

    /// <summary>Turns of an entry once its delay is out: a quarter of the extrema of its real
    /// and imaginary parts (a full turn has two of each).</summary>
    private static int Turns(IReadOnlyList<double> frequencies, IReadOnlyList<Complex> values, double delay)
    {
        int extrema = 0;
        Complex Flat(int k) => values[k] * Complex.FromPolarCoordinates(1, 2 * Math.PI * frequencies[k] * delay);
        for (int k = 1; k + 1 < values.Count; k++)
        {
            Complex a = Flat(k - 1), b = Flat(k), c = Flat(k + 1);
            if ((b.Real - a.Real) * (c.Real - b.Real) < 0) extrema++;
            if ((b.Imaginary - a.Imaginary) * (c.Imaginary - b.Imaginary) < 0) extrema++;
        }
        return extrema / 4 + 2;
    }

    /// <summary>The file's frequencies with <paramref name="factor"/> − 1 points between
    /// each neighbouring pair.</summary>
    private static List<double> Refine(IReadOnlyList<double> frequencies, int factor)
    {
        var grid = new List<double>();
        for (int k = 0; k + 1 < frequencies.Count; k++)
            for (int m = 0; m < factor; m++)
                grid.Add(frequencies[k] + (frequencies[k + 1] - frequencies[k]) * m / factor);
        grid.Add(frequencies[^1]);
        return grid;
    }

    private static void EnforcePassivity(RationalResponse[] models, List<(int Row, int Column)> entries, int[] index,
        int n, IReadOnlyList<double> frequencies, double margin, bool reciprocal, List<string> notes)
    {
        var grid = Refine(frequencies, 2);
        int rounds = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            for (int round = 0; round < 40; round++, rounds++)
            {
                var corrections = new Complex[grid.Count][,];
                bool violated = false;
                Parallel.For(0, grid.Count, g =>
                {
                    var s = new Complex[n, n];
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++) s[i, j] = models[index[i * n + j]].At(grid[g]);
                    var (u, sigma, v) = ComplexJacobiSvd.Decompose(s);
                    if (sigma[0] <= 1 - 0.5 * margin) return;
                    violated = true;
                    var delta = new Complex[n, n];
                    for (int k = 0; k < n; k++)
                    {
                        if (sigma[k] <= 1 - margin) break;
                        double shrink = 1 - margin - sigma[k];
                        for (int i = 0; i < n; i++)
                            for (int j = 0; j < n; j++)
                                delta[i, j] += u[i, k] * shrink * Complex.Conjugate(v[j, k]);
                    }
                    corrections[g] = delta;
                });
                if (!violated) break;
                Parallel.For(0, entries.Count, e =>
                {
                    var (i, j) = entries[e];
                    var change = new Complex[grid.Count];
                    for (int g = 0; g < grid.Count; g++)
                    {
                        if (corrections[g] is not { } delta) continue;
                        Complex value = reciprocal ? 0.5 * (delta[i, j] + delta[j, i]) : delta[i, j];
                        // Delay-free, as the residues are.
                        change[g] = value * Complex.FromPolarCoordinates(1, 2 * Math.PI * grid[g] * models[e].DelaySeconds);
                    }
                    models[e] = models[e].WithAdded(grid, change);
                });
            }
            // Between the grid's points: check four times finer, and take in what fails.
            var fine = Refine(frequencies, 4);
            var failing = fine.Where(f =>
            {
                var s = new Complex[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) s[i, j] = models[index[i * n + j]].At(f);
                return NetworkChecks.LargestSingularValue(s) > 1 - 0.5 * margin;
            }).ToList();
            if (failing.Count == 0) break;
            grid = grid.Concat(failing).Distinct().OrderBy(f => f).ToList();
        }
        if (rounds > 0) notes.Add($"Passivity enforced in {rounds} correction rounds.");
    }
}
