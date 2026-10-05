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
            result[e] = RationalSweep.Interpolate(FrequenciesHz, column, frequencyHz);
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
    /// the port impedances). Called once per sample.</param>
    /// <param name="tolerance">Largest allowed disagreement between successive interpolants,
    /// relative to the largest magnitude the response has reached.</param>
    public static RationalSweepResult Run(Func<double, Complex[]> evaluate, double fromHz, double toHz,
        double tolerance = 1e-3, int initialSamples = 5, int maxSamples = 40, int gridPoints = 400,
        CancellationToken cancellationToken = default)
    {
        if (!(fromHz > 0 && toHz > fromHz)) throw new ArgumentException("The sweep needs 0 < from < to.");
        initialSamples = Math.Max(initialSamples, 3);
        var samples = new SortedDictionary<double, Complex[]>();
        var order = new List<double>();                          // in the order they were taken
        void Sample(double f)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples[f] = evaluate(f);
            order.Add(f);
        }
        // Chebyshev points: closer together toward the band edges, where a rational fit is loosest.
        for (int k = 0; k < initialSamples; k++)
        {
            double t = 0.5 * (1 - Math.Cos(Math.PI * k / (initialSamples - 1)));
            Sample(fromHz + (toHz - fromHz) * t);
        }

        var grid = new double[gridPoints];
        for (int g = 0; g < gridPoints; g++) grid[g] = fromHz + (toHz - fromHz) * (g + 0.5) / gridPoints;
        double error = double.PositiveInfinity;
        int agreed = 0;
        while (true)
        {
            int entries = samples.Values.First().Length;
            var all = samples.Keys.ToList();
            var fewer = all.Where(f => f != order[^1]).ToList();
            double scale = samples.Values.Max(v => v.Max(c => c.Magnitude));
            double worst = 0, worstAt = double.NaN;
            var columnAll = new Complex[all.Count];
            var columnFewer = new Complex[fewer.Count];
            foreach (double f in grid)
            {
                double nearest = all.Min(s => Math.Abs(s - f));
                if (nearest < 1e-9 * (toHz - fromHz)) continue;
                double difference = 0;
                for (int e = 0; e < entries; e++)
                {
                    for (int k = 0; k < all.Count; k++) columnAll[k] = samples[all[k]][e];
                    for (int k = 0; k < fewer.Count; k++) columnFewer[k] = samples[fewer[k]][e];
                    Complex a = Interpolate(all, columnAll, f), b = Interpolate(fewer, columnFewer, f);
                    double d = (a - b).Magnitude;
                    if (double.IsNaN(d)) d = double.PositiveInfinity;
                    difference = Math.Max(difference, d);
                }
                if (difference > worst) { worst = difference; worstAt = f; }
            }
            error = scale > 0 ? worst / scale : 0;
            // Two rounds in a row under the tolerance: one agreement can be a coincidence.
            agreed = error <= tolerance ? agreed + 1 : 0;
            if (agreed >= 2 || samples.Count >= maxSamples || double.IsNaN(worstAt)) break;
            Sample(worstAt);
        }
        return new RationalSweepResult(samples.Keys.ToList(), samples.Values.ToList(), error, agreed >= 2);
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
