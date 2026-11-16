using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Sampling a one-dimensional density by inversion of its cumulative distribution, for the
/// source distributions with no closed-form inverse (the diode's super-Gaussians, the radial
/// source's spline). x = F⁻¹(u) is a fixed, monotone function of u, so Sobol points keep their
/// stratification and ray k still depends only on k.
/// <list type="bullet">
/// <item><b>The CDF.</b> The interval is cut into panels (64 equal ones, then each split in two
/// until its 10-point Gauss–Legendre integral agrees with the sum over its halves to 1e-15 of
/// the coarse total, at most 40 levels deep), and the panel masses are summed in order.</item>
/// <item><b>The inverse.</b> The panel holding u·total is found by binary search, then
/// ∫ₚˣ f = residual is solved by Newton's method (the derivative is f itself) kept inside the
/// panel by bisection, to 1e-14 of the panel's mass.</item>
/// </list>
/// The sampled distribution therefore matches the exact one to about 1e-14 in probability, far
/// below the Monte Carlo noise of any ray count. A density negative somewhere (a spline's
/// overshoot) is taken as zero there and reported in <see cref="Notes"/>; a density with no
/// positive mass, or not finite, is refused. Immutable and thread-safe after construction.
/// </summary>
public sealed class NscInverseCdf
{
    private static readonly (double[] Nodes, double[] Weights) Rule = GaussLegendre.Rule(10);

    private readonly Func<double, double> _f;
    private readonly double[] _edges;      // panel i is [_edges[i], _edges[i+1]]
    private readonly double[] _cumulative; // mass before panel i; last = total

    public double Lower { get; }
    public double Upper { get; }

    /// <summary>The integral of the density (with negative parts taken as zero).</summary>
    public double Total => _cumulative[^1];

    /// <summary>What was done to the density ("negative between ... ; taken as zero").</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <param name="density">f(x) ≥ 0 on [<paramref name="lower"/>, <paramref name="upper"/>].</param>
    /// <param name="breakpoints">Points where f may not be smooth (a kink at 0), made panel edges.</param>
    /// <param name="where">Names the source and quantity in messages.</param>
    public NscInverseCdf(Func<double, double> density, double lower, double upper, string where, params double[] breakpoints)
    {
        ArgumentNullException.ThrowIfNull(density);
        if (!(upper > lower) || !double.IsFinite(lower) || !double.IsFinite(upper))
            throw new ArgumentException($"{where}: the sampling interval [{F(lower)}, {F(upper)}] is empty or not finite.");
        Lower = lower;
        Upper = upper;
        double negativeAt = double.NaN, mostNegative = 0;
        _f = x =>
        {
            double v = density(x);
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentException($"{where}: the density is {v} at {F(x)}.");
            return v > 0 ? v : 0;
        };
        // A probe on a fine grid finds negative parts for the note (the clamp above is what is sampled).
        for (int i = 0; i <= 4096; i++)
        {
            double x = lower + (upper - lower) * i / 4096;
            double v = density(x);
            if (v < mostNegative) { mostNegative = v; negativeAt = x; }
        }

        var starts = new List<double>();
        for (int i = 0; i <= 64; i++) starts.Add(lower + (upper - lower) * i / 64);
        foreach (var b in breakpoints)
            if (b > lower && b < upper && !starts.Contains(b)) starts.Add(b);
        starts.Sort();
        double coarse = 0;
        for (int i = 0; i + 1 < starts.Count; i++) coarse += Quad(starts[i], starts[i + 1]);
        if (!(coarse > 0) || !double.IsFinite(coarse))
            throw new ArgumentException($"{where}: the density has no positive mass on [{F(lower)}, {F(upper)}].");
        double tolerance = 1e-15 * coarse;

        var edges = new List<double> { starts[0] };
        var masses = new List<double>();
        for (int i = 0; i + 1 < starts.Count; i++)
            Refine(starts[i], starts[i + 1], Quad(starts[i], starts[i + 1]), 0, tolerance, edges, masses);
        _edges = edges.ToArray();
        _cumulative = new double[masses.Count + 1];
        var sum = new NscLedgerBuilder.Sum();
        for (int i = 0; i < masses.Count; i++)
        {
            sum.Add(masses[i]);
            _cumulative[i + 1] = sum.Total;
        }
        Notes = double.IsNaN(negativeAt)
            ? Array.Empty<string>()
            : new[] { $"{where}: the density is negative near {F(negativeAt)} (least value {F(mostNegative)}); it is taken as zero where negative." };
    }

    private void Refine(double a, double b, double whole, int depth, double tolerance, List<double> edges, List<double> masses)
    {
        double m = 0.5 * (a + b);
        double left = Quad(a, m), right = Quad(m, b);
        if (Math.Abs(whole - (left + right)) <= tolerance || depth >= 40 || !(m > a && m < b))
        {
            edges.Add(m); masses.Add(left);
            edges.Add(b); masses.Add(right);
            return;
        }
        Refine(a, m, left, depth + 1, tolerance, edges, masses);
        Refine(m, b, right, depth + 1, tolerance, edges, masses);
    }

    private double Quad(double a, double b)
    {
        double half = 0.5 * (b - a), mid = 0.5 * (a + b), s = 0;
        var (t, w) = Rule;
        for (int i = 0; i < t.Length; i++) s += w[i] * _f(mid + half * t[i]);
        return s * half;
    }

    /// <summary>The probability of x' ≤ <paramref name="x"/>.</summary>
    public double Cdf(double x)
    {
        if (x <= Lower) return 0;
        if (x >= Upper) return 1;
        int i = Array.BinarySearch(_edges, x);
        if (i >= 0) return _cumulative[Math.Min(i, _cumulative.Length - 1)] / Total;
        i = ~i - 1;
        return (_cumulative[i] + Quad(_edges[i], x)) / Total;
    }

    /// <summary>F⁻¹(<paramref name="u"/>) for u in [0, 1).</summary>
    public double Sample(double u)
    {
        double total = Total;
        double target = u * total;
        // The panel i with C[i] ≤ target < C[i+1] (the last panel with mass for target ≥ total).
        int lo = 0, hi = _cumulative.Length - 2;
        if (target >= _cumulative[hi + 1])
        {
            while (hi > 0 && !(_cumulative[hi + 1] > _cumulative[hi])) hi--;
            return _edges[hi + 1];
        }
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_cumulative[mid] <= target) lo = mid; else hi = mid - 1;
        }
        int panel = lo;
        while (!(_cumulative[panel + 1] > _cumulative[panel]) && panel + 1 < _cumulative.Length - 1) panel++;
        double p = _edges[panel], q = _edges[panel + 1];
        double mass = _cumulative[panel + 1] - _cumulative[panel];
        double r = Math.Clamp(target - _cumulative[panel], 0, mass);
        double a = p, b = q;
        double x = p + (q - p) * (r / mass);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            double g = Quad(p, x) - r;
            if (Math.Abs(g) <= 1e-14 * mass) break;
            if (g < 0) a = x; else b = x;
            double fx = _f(x);
            double next = fx > 0 ? x - g / fx : double.NaN;
            if (!(next > a && next < b)) next = 0.5 * (a + b);
            if (Math.Abs(next - x) <= 1e-16 * (Math.Abs(x) + (q - p))) { x = next; break; }
            x = next;
        }
        return x;
    }

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
