using System.Numerics;

namespace OpenSim.Rf.Network;

/// <summary>An adaptively sampled frequency response.</summary>
/// <param name="FrequenciesHz">Where the solver was run, ascending.</param>
/// <param name="Values">The solver's values there (one array of entries per frequency).</param>
/// <param name="EstimatedError">Largest difference between the last two interpolants over
/// the band, relative to the response's size: what the stopping test saw.</param>
public sealed record RationalSweepResult(IReadOnlyList<double> FrequenciesHz, IReadOnlyList<Complex[]> Values,
    double EstimatedError, bool Converged)
{
    /// <summary>The sweep was sampled, and is interpolated, in ln f.</summary>
    public bool Logarithmic { get; init; }

    /// <summary>The response at any frequency in the band, by rational interpolation through
    /// every sample.</summary>
    public Complex[] At(double frequencyHz)
    {
        int entries = Values[0].Length;
        var result = new Complex[entries];
        var column = new Complex[FrequenciesHz.Count];
        for (int e = 0; e < entries; e++)
        {
            for (int k = 0; k < column.Length; k++) column[k] = Values[k][e];
            result[e] = Logarithmic
                ? RationalSweep.Interpolate(FrequenciesHz.Select(f => Math.Log(f)).ToList(), column, Math.Log(frequencyHz))
                : RationalSweep.Interpolate(FrequenciesHz, column, frequencyHz);
        }
        return result;
    }
}

/// <summary>
/// A frequency sweep that puts its solver runs where the response needs them. A resonance is
/// a pole pair close to the frequency axis; a rational function through a few samples
/// carries poles where a polynomial or a straight line between sweep points cannot, so a
/// sharp resonance between two samples shows in the interpolant before any sample has landed
/// on it. Each round adds the frequency where the interpolant through all samples and the
/// one through all but the newest disagree most, and stops when they agree everywhere.
/// (Adaptive frequency sampling as used with moment-method solvers; the interpolant is the
/// Bulirsch–Stoer diagonal rational one.)
/// </summary>
public static class RationalSweep
{
    /// <param name="evaluate">The solver: frequency → the response's entries (for instance
    /// the port impedances). Called once per sample, from several threads at once when
    /// <paramref name="maxDegreeOfParallelism"/> allows.</param>
    /// <param name="tolerance">Largest allowed disagreement between successive interpolants,
    /// relative to the largest magnitude the response has reached.</param>
    /// <param name="batch">Samples added per round: the frequencies of the largest disagreement
    /// between the interpolants, one per separate peak of it, solved together.</param>
    /// <param name="logarithmic">Sample and interpolate in ln f — for a band of several decades,
    /// where Chebyshev points in f would leave the low decades empty.</param>
    public static RationalSweepResult Run(Func<double, Complex[]> evaluate, double fromHz, double toHz,
        double tolerance = 1e-3, int initialSamples = 5, int maxSamples = 40, int gridPoints = 400,
        CancellationToken cancellationToken = default, int batch = 1, bool logarithmic = false,
        int maxDegreeOfParallelism = 1)
    {
        if (!(fromHz > 0 && toHz > fromHz)) throw new ArgumentException("The sweep needs 0 < from < to.");
        if (batch < 1) throw new ArgumentOutOfRangeException(nameof(batch));
        initialSamples = Math.Max(initialSamples, 3);
        // Work on an abscissa u: f itself, or ln f.
        double ToU(double f) => logarithmic ? Math.Log(f) : f;
        double ToF(double u) => logarithmic ? Math.Exp(u) : u;
        double uFrom = ToU(fromHz), uTo = ToU(toHz);
        var samples = new SortedDictionary<double, Complex[]>();
        var lastRound = new HashSet<double>();
        void Sample(IReadOnlyList<double> us)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Complex[us.Count][];
            Parallel.For(0, us.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken
            }, i => values[i] = evaluate(ToF(us[i])));
            lastRound.Clear();
            for (int i = 0; i < us.Count; i++)
            {
                samples[us[i]] = values[i];
                lastRound.Add(us[i]);
            }
        }
        // Chebyshev points: closer together toward the band edges, where a rational fit is loosest.
        var initial = new double[initialSamples];
        for (int k = 0; k < initialSamples; k++)
            initial[k] = uFrom + (uTo - uFrom) * 0.5 * (1 - Math.Cos(Math.PI * k / (initialSamples - 1)));
        Sample(initial);
        // The first round's "newest" is the last Chebyshev point, as a one-at-a-time start would have it.
        lastRound.Clear();
        lastRound.Add(initial[^1]);

        var grid = new double[gridPoints];
        for (int g = 0; g < gridPoints; g++) grid[g] = uFrom + (uTo - uFrom) * (g + 0.5) / gridPoints;
        double error = double.PositiveInfinity;
        int agreed = 0;
        while (true)
        {
            int entries = samples.Values.First().Length;
            var all = samples.Keys.ToList();
            var fewer = all.Where(u => !lastRound.Contains(u)).ToList();
            double scale = samples.Values.Max(v => v.Max(c => c.Magnitude));
            var columnAll = new Complex[all.Count];
            var columnFewer = new Complex[fewer.Count];
            var difference = new double[gridPoints];
            for (int g = 0; g < gridPoints; g++)
            {
                double u = grid[g];
                double nearest = all.Min(s => Math.Abs(s - u));
                if (nearest < 1e-9 * (uTo - uFrom)) continue;
                double worstHere = 0;
                for (int e = 0; e < entries; e++)
                {
                    for (int k = 0; k < all.Count; k++) columnAll[k] = samples[all[k]][e];
                    for (int k = 0; k < fewer.Count; k++) columnFewer[k] = samples[fewer[k]][e];
                    Complex a = Interpolate(all, columnAll, u), b = Interpolate(fewer, columnFewer, u);
                    double d = (a - b).Magnitude;
                    if (double.IsNaN(d)) d = double.PositiveInfinity;
                    worstHere = Math.Max(worstHere, d);
                }
                difference[g] = worstHere;
            }
            double worst = difference.Max();
            error = scale > 0 ? worst / scale : 0;
            // Two rounds in a row under the tolerance: one agreement can be a coincidence.
            agreed = error <= tolerance ? agreed + 1 : 0;
            if (agreed >= 2 || samples.Count >= maxSamples || !(worst > 0)) break;

            // The next samples: the largest peaks of the disagreement, one per peak.
            var peaks = Enumerable.Range(0, gridPoints)
                .Where(g => difference[g] > 0
                    && (g == 0 || difference[g] >= difference[g - 1])
                    && (g == gridPoints - 1 || difference[g] > difference[g + 1]))
                .OrderByDescending(g => difference[g]).ThenBy(g => g)
                .Take(Math.Min(batch, maxSamples - samples.Count))
                .Select(g => grid[g]).ToList();
            if (peaks.Count == 0) break;
            Sample(peaks);
        }
        return new RationalSweepResult(samples.Keys.Select(ToF).ToList(), samples.Values.ToList(), error, agreed >= 2)
        {
            Logarithmic = logarithmic
        };
    }
    /// <summary>
    /// The diagonal rational function through all the points, evaluated at
    /// <paramref name="x"/> (Bulirsch and Stoer's recurrence, Numerical Recipes' ratint in
    /// form): degree (n−1)/2 over (n−1)/2 for n points.
    /// </summary>
    public static Complex Interpolate(IReadOnlyList<double> xs, IReadOnlyList<Complex> ys, double x)
    {
        int n = xs.Count;
        if (n == 0) throw new ArgumentException("No points to interpolate.");
        // Work on a normalised abscissa, nearest point first.
        double span = Math.Max(xs[n - 1] - xs[0], double.Epsilon);
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double h = Math.Abs(x - xs[i]);
            if (h == 0) return ys[i];
            if (h < best) { best = h; nearest = i; }
        }
        var c = new Complex[n];
        var d = new Complex[n];
        const double tiny = 1e-300;
        for (int i = 0; i < n; i++) { c[i] = ys[i]; d[i] = ys[i] + tiny; }
        Complex y = ys[nearest];
        int ns = nearest - 1;
        for (int m = 1; m < n; m++)
        {
            for (int i = 0; i < n - m; i++)
            {
                Complex w = c[i + 1] - d[i];
                double h = (xs[i + m] - x) / span;
                Complex t = (xs[i] - x) / span * d[i] / h;
                Complex dd = t - c[i + 1];
                if (dd == Complex.Zero) return double.NaN;          // a pole of the interpolant at x
                dd = w / dd;
                d[i] = c[i + 1] * dd;
                c[i] = t * dd;
            }
            y += 2 * (ns + 1) < n - m ? c[ns + 1] : d[ns--];
        }
        return y;
    }
}
