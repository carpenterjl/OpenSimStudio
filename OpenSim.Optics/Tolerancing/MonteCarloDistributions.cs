using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Tolerancing;

// OP-65: the statistics a Monte Carlo trial draws each tolerance from (docs/Optics-Conventions.md
// §45). Sources: OpticStudio 2025 R2 help, "Monte Carlo Analysis", "Normal Statistical
// Distribution", "Uniform Statistical Distribution", "Parabolic Statistical Distribution", "User
// Defined Statistical Distribution" (saved under plans/sources/optics/op-65-help/, the equation
// images under images/), "STAT: Define Statistics" (op-63-help).

/// <summary>
/// One tolerance's distribution over [Min, Max]: the help's normal, uniform, parabolic or user
/// defined statistics. Offsets are measured from the midpoint of the two extremes, and
/// Δ = (Max − Min)/2 ("The Δ value is one half of the difference between the max and min tolerance
/// values").
/// <para>
/// <b>Normal.</b> "A modified Gaussian normal distribution ... the randomly selected value x
/// (measured as an offset from the midpoint between the two extreme tolerances) is restricted to
/// lie within n standard deviations of zero ... The standard deviation is set to half the range of
/// the tolerances divided by n" (default n = 2). The restriction is read as a truncation: a draw
/// outside ±Δ is drawn again (this program's reading, §10 item 41).
/// </para>
/// <para>
/// <b>Uniform.</b> "The randomly selected value will lie somewhere between the specified extreme
/// tolerances with uniform probability": p(x) = 1/(2Δ) on −Δ ≤ x ≤ Δ.
/// </para>
/// <para>
/// <b>Parabolic.</b> p(x) = 3x²/(2Δ³) on −Δ ≤ x ≤ Δ (the help's equation image): values "more
/// likely to be at the extreme ends". Drawn by inverting its distribution function
/// F(x) = (x³ + Δ³)/(2Δ³) (derived): x = Δ·∛(2u − 1).
/// </para>
/// <para>
/// <b>User defined.</b> A tabulated probability T(X) on 0 ≤ X ≤ 1 (<see cref="UserDefinedDistribution"/>)
/// mapped onto [Min, Max]: "if a value of 100.0 has a tolerance of -0.0 and +2.0, the probability
/// distribution will extend over the range of 100.0 to 102.0".
/// </para>
/// <para>
/// <b>Radial operands.</b> For TEDR, TSDR, TARR and TRLR the normal and parabolic pages say "if
/// either Max or Min is 0 then the distribution will be centered at 0. The random radial direction
/// of the roll ensures that this remains a symmetric distribution". This program then takes the
/// half-width Δ = max(|Min|, |Max|) about zero (its reading, §10 item 41); the uniform and user
/// defined pages say nothing of it, so they keep [Min, Max].
/// </para>
/// </summary>
public sealed class ToleranceDistribution
{
    private ToleranceDistribution(ToleranceStatistics statistics, double min, double max, int standardDeviations,
        UserDefinedDistribution? table, bool centredAtZero)
    {
        Statistics = statistics;
        Min = min;
        Max = max;
        StandardDeviations = standardDeviations;
        Table = table;
        if (centredAtZero)
        {
            Mid = 0;
            HalfWidth = Math.Max(Math.Abs(min), Math.Abs(max));
        }
        else
        {
            Mid = (min + max) / 2;
            HalfWidth = (max - min) / 2;
        }
    }

    public ToleranceStatistics Statistics { get; }
    public double Min { get; }
    public double Max { get; }

    /// <summary>The midpoint the offsets are measured from (0 for a radial operand centred at zero).</summary>
    public double Mid { get; }

    /// <summary>Δ, the half-width of the range.</summary>
    public double HalfWidth { get; }

    /// <summary>The normal distribution's n; 0 for the others.</summary>
    public int StandardDeviations { get; }

    /// <summary>The user defined table; null for the others.</summary>
    public UserDefinedDistribution? Table { get; }

    /// <summary>The normal distribution's σ = Δ/n; NaN for the others.</summary>
    public double Sigma => Statistics == ToleranceStatistics.Normal ? HalfWidth / StandardDeviations : double.NaN;

    /// <summary>The normal distribution with n standard deviations from the midpoint to the extremes.</summary>
    /// <param name="centredAtZero">A radial operand with a zero limit (see the class remarks).</param>
    public static ToleranceDistribution Normal(double min, double max, int standardDeviations, bool centredAtZero = false)
    {
        Check(min, max);
        if (standardDeviations < 1 || standardDeviations > 10)
            throw new ArgumentException($"The normal distribution needs 1 to 10 standard deviations between the mean and the extremes (help, \"STAT\": \"capped at 10\"), not {standardDeviations}.");
        return new(ToleranceStatistics.Normal, min, max, standardDeviations, null, centredAtZero);
    }

    public static ToleranceDistribution Uniform(double min, double max)
    {
        Check(min, max);
        return new(ToleranceStatistics.Uniform, min, max, 0, null, false);
    }

    public static ToleranceDistribution Parabolic(double min, double max, bool centredAtZero = false)
    {
        Check(min, max);
        return new(ToleranceStatistics.Parabolic, min, max, 0, null, centredAtZero);
    }

    public static ToleranceDistribution UserDefined(double min, double max, UserDefinedDistribution table)
    {
        Check(min, max);
        ArgumentNullException.ThrowIfNull(table);
        return new(ToleranceStatistics.UserDefined, min, max, 0, table, false);
    }

    private static void Check(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) throw new ArgumentException($"A tolerance's limits must be finite numbers ({min}, {max}).");
        if (min > max) throw new ArgumentException($"A tolerance's Min {min} lies above its Max {max}.");
    }

    /// <summary>One value drawn from <paramref name="random"/>. The number of uniforms used
    /// depends only on the values drawn, so a stream gives the same values every time.</summary>
    public double Draw(SplitMix64 random)
    {
        ArgumentNullException.ThrowIfNull(random);
        double d = HalfWidth;
        switch (Statistics)
        {
            case ToleranceStatistics.Normal:
            {
                if (d == 0) return Mid;
                double sigma = d / StandardDeviations;
                while (true)
                {
                    double x = sigma * random.NextNormal();
                    if (Math.Abs(x) <= d) return Mid + x;
                }
            }
            case ToleranceStatistics.Uniform:
                return Mid + d * (2 * random.NextDouble() - 1);
            case ToleranceStatistics.Parabolic:
                return Mid + d * Math.Cbrt(2 * random.NextDouble() - 1);
            default:
                return Min + (Max - Min) * Table!.Inverse(random.NextDouble());
        }
    }
}

/// <summary>
/// A user defined statistical distribution (help, "User Defined Statistical Distribution"): "two
/// columns of data ... where the X values are monotonically increasing floating point numbers
/// between 0.0 and 1.0, inclusively, and the T values are the probabilities of getting that X
/// value ... the first X1 value defined MUST be equal to 0.0 (it may have any probability T1,
/// including zero), and the last value defined MUST have an Xn value of 1.0. Up to 200 points may
/// be used". The file has the extension .UDD and is named in the comment of its STAT row.
/// <para>
/// The help says the table "may be numerically integrated, and from the integral of the tabulated
/// values an estimated X may be randomly generated". This program reads T as linear between the
/// points, integrates it exactly (trapezoids) and inverts that integral exactly within each
/// interval (its reading, §10 item 41). X must increase strictly. More than 200 points is accepted
/// with a note, as the help issues "a warning".
/// </para>
/// </summary>
public sealed class UserDefinedDistribution
{
    /// <summary>The help's limit on the number of points.</summary>
    public const int MaximumPoints = 200;

    private readonly double[] _x;
    private readonly double[] _t;
    private readonly double[] _cumulative;

    public UserDefinedDistribution(IReadOnlyList<double> x, IReadOnlyList<double> t, string name = "user defined distribution")
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(t);
        Name = name;
        if (x.Count != t.Count) throw new ArgumentException($"{name}: {x.Count} X values but {t.Count} T values.");
        if (x.Count < 2) throw new ArgumentException($"{name}: at least two points (X = 0 and X = 1) are needed.");
        if (x[0] != 0) throw new ArgumentException($"{name}: the first X must be 0.0 (help: \"the first X1 value defined MUST be equal to 0.0\"); it is {F(x[0])}.");
        if (x[^1] != 1) throw new ArgumentException($"{name}: the last X must be 1.0 (help: \"the last value defined MUST have an Xn value of 1.0\"); it is {F(x[^1])}.");
        for (int i = 0; i < x.Count; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(t[i])) throw new ArgumentException($"{name}: point {i + 1} is not a pair of finite numbers.");
            if (t[i] < 0) throw new ArgumentException($"{name}: point {i + 1} has the negative probability {F(t[i])}.");
            if (i > 0 && !(x[i] > x[i - 1])) throw new ArgumentException($"{name}: X must increase from point to point; point {i + 1} ({F(x[i])}) does not exceed point {i} ({F(x[i - 1])}).");
        }
        _x = x.ToArray();
        _t = t.ToArray();
        _cumulative = new double[_x.Length];
        for (int i = 1; i < _x.Length; i++) _cumulative[i] = _cumulative[i - 1] + (_x[i] - _x[i - 1]) * (_t[i] + _t[i - 1]) / 2;
        if (!(_cumulative[^1] > 0)) throw new ArgumentException($"{name}: every probability is zero.");
        Notes = _x.Length > MaximumPoints
            ? new[] { $"{name}: {_x.Length} points; the help allows up to {MaximumPoints} and warns beyond them. All are used." }
            : Array.Empty<string>();
    }

    public string Name { get; }

    public IReadOnlyList<double> X => _x;
    public IReadOnlyList<double> T => _t;

    /// <summary>Warnings about the table (more points than the help allows).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The probability density at <paramref name="x"/> (T linear between points, scaled to unit area).</summary>
    public double Density(double x)
    {
        if (x < 0 || x > 1) return 0;
        int i = Interval(x);
        double h = _x[i + 1] - _x[i];
        double t = _t[i] + (_t[i + 1] - _t[i]) * (x - _x[i]) / h;
        return t / _cumulative[^1];
    }

    /// <summary>The X below which a fraction <paramref name="u"/> (0 to 1) of the probability lies.</summary>
    public double Inverse(double u)
    {
        double target = Math.Clamp(u, 0, 1) * _cumulative[^1];
        int i = Array.BinarySearch(_cumulative, target);
        if (i >= 0)
        {
            // Exactly on a point: skip forward over intervals of zero area.
            while (i < _x.Length - 1 && _cumulative[i + 1] == _cumulative[i]) i++;
            return _x[i];
        }
        i = ~i - 1;                       // _cumulative[i] < target < _cumulative[i + 1]
        if (i >= _x.Length - 1) return 1;
        double a = target - _cumulative[i];
        double h = _x[i + 1] - _x[i];
        double p0 = _t[i], k = (_t[i + 1] - _t[i]) / h;
        // ∫₀ˢ (p0 + k σ) dσ = a  ⇒  s = 2a / (p0 + √(p0² + 2ka)) (derived; stable for k = 0 and p0 = 0).
        double s = 2 * a / (p0 + Math.Sqrt(Math.Max(0, p0 * p0 + 2 * k * a)));
        return Math.Min(_x[i] + s, _x[i + 1]);
    }

    private int Interval(double x)
    {
        int i = Array.BinarySearch(_x, x);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _x.Length - 2);
    }

    /// <summary>Reads a .UDD file's text: one "X T" pair per line, separated by spaces, tabs or a
    /// comma; blank lines are skipped. A line that is not two numbers is refused, naming it.</summary>
    public static UserDefinedDistribution Parse(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        var x = new List<double>();
        var t = new List<double>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
                throw new ArgumentException($"{name}, line {n}: \"{line}\" is not two numbers \"X T\" (help, \"User Defined Statistical Distribution\").");
            x.Add(a);
            t.Add(b);
        }
        return new UserDefinedDistribution(x, t, name);
    }

    private static string F(double v) => v.ToString("G8", CultureInfo.InvariantCulture);
}
