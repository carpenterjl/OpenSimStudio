using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Tessellate;

namespace OpenSim.Geometry.Export;

/// <summary>
/// A cubic B-spline basis on [<see cref="Start"/>, <see cref="End"/>] with a clamped, uniform
/// knot vector: four knots at each end and <see cref="Spans"/> − 1 equally spaced interior knots,
/// so <see cref="Count"/> = spans + 3 basis functions. Evaluation uses the importer's
/// first-party basis routines (<see cref="Nurbs"/>, Piegl and Tiller's A2.1 and A2.2 as cited
/// there).
/// <para>
/// <see cref="Greville"/> holds the Greville abscissae ξᵢ = (tᵢ₊₁ + tᵢ₊₂ + tᵢ₊₃)/3. With the
/// control abscissae placed there the spline reproduces the identity, Σ ξᵢ Nᵢ(t) = t (the linear
/// case of Marsden's identity; it is not taken on trust here: the tests evaluate it), so a curve or
/// surface whose x (and y) control coordinates are Greville abscissae has x(t) = t exactly and its
/// only deviation from the fitted function is in z.
/// </para>
/// </summary>
public sealed class CubicBasis
{
    public const int Degree = 3;

    public CubicBasis(double start, double end, int spans)
    {
        if (!(end > start)) throw new ArgumentOutOfRangeException(nameof(end), "The basis needs end > start.");
        if (spans < 1) throw new ArgumentOutOfRangeException(nameof(spans), spans, "At least one span is needed.");
        Start = start;
        End = end;
        Spans = spans;
        Count = spans + Degree;
        Knots = new double[Count + Degree + 1];
        for (int i = 0; i < Knots.Length; i++)
        {
            int k = Math.Clamp(i - Degree, 0, spans);
            Knots[i] = k == spans ? end : start + (end - start) * k / spans;
        }
        Greville = new double[Count];
        for (int i = 0; i < Count; i++)
            Greville[i] = i == 0 ? start : i == Count - 1 ? end : (Knots[i + 1] + Knots[i + 2] + Knots[i + 3]) / 3;
    }

    public double Start { get; }
    public double End { get; }
    public int Spans { get; }
    public int Count { get; }

    /// <summary>The flat knot vector (length Count + 4).</summary>
    public double[] Knots { get; }

    public double[] Greville { get; }

    /// <summary>The knot values without repetition, and their multiplicities (STEP's form).</summary>
    public (double[] Values, int[] Multiplicities) DistinctKnots()
    {
        var values = new double[Spans + 1];
        var mult = new int[Spans + 1];
        for (int k = 0; k <= Spans; k++)
        {
            values[k] = k == Spans ? End : Start + (End - Start) * k / Spans;
            mult[k] = k == 0 || k == Spans ? Degree + 1 : 1;
        }
        return (values, mult);
    }

    /// <summary>The span index and the four non-zero basis values at t.</summary>
    public (int Span, double[] N) Evaluate(double t)
    {
        t = Math.Clamp(t, Start, End);
        int span = Nurbs.FindSpan(Knots, Degree, Count, t);
        var n = new double[Degree + 1];
        Nurbs.Basis(Knots, Degree, span, t, n);
        return (span, n);
    }

    /// <summary>Σ cᵢ Nᵢ(t).</summary>
    public double Value(IReadOnlyList<double> coefficients, double t)
    {
        var (span, n) = Evaluate(t);
        double sum = 0;
        for (int j = 0; j <= Degree; j++) sum += coefficients[span - Degree + j] * n[j];
        return sum;
    }
}

/// <summary>
/// A cubic profile curve fitted to a cap's sag: control points (ξᵢ, <see cref="Z"/>ᵢ) with ξ the
/// Greville abscissae of <see cref="Basis"/> on [0, rim], so r(t) = t and z(t) approximates the sag.
/// <see cref="MaxDeviation"/> is the largest |z(t) − sag(t)| found on the check grid.
/// </summary>
public sealed record ProfileFit(CubicBasis Basis, double[] Z, double MaxDeviation);

/// <summary>
/// A bicubic patch fitted to a cap's sag over the square [−a, a]² of its frame's (x, y), a =
/// <see cref="HalfWidth"/>: control points (ξᵢ, ξₖ, <see cref="Z"/>[i, k]) with ξ the Greville
/// abscissae of <see cref="Basis"/>, so x(u) = u, y(v) = v and z(u, v) approximates the sag at
/// r = √(u² + v²) inside the rim. <see cref="MaxDeviation"/> is the largest |z − sag| found on
/// the check grid inside the rim.
/// </summary>
public sealed record PatchFit(CubicBasis Basis, double[,] Z, double HalfWidth, double MaxDeviation)
{
    public double Value(double u, double v)
    {
        var (su, nu) = Basis.Evaluate(u);
        var (sv, nv) = Basis.Evaluate(v);
        double sum = 0;
        for (int a = 0; a <= CubicBasis.Degree; a++)
            for (int b = 0; b <= CubicBasis.Degree; b++)
                sum += Z[su - CubicBasis.Degree + a, sv - CubicBasis.Degree + b] * nu[a] * nv[b];
        return sum;
    }
}

/// <summary>
/// Cubic B-spline fits of a cap's sag to a stated tolerance, by interpolation at the Greville
/// abscissae. The method is derived here, not copied (Piegl and Tiller's The NURBS Book, which
/// treats global interpolation in its chapter 9, was not available to this item):
/// <list type="bullet">
/// <item><b>Interpolation.</b> The collocation matrix Aᵢⱼ = Nⱼ(ξᵢ) is square and nonsingular because
/// each ξᵢ lies inside the support of Nᵢ (the Schoenberg–Whitney condition, met by the Greville
/// abscissae of a clamped knot vector). It is solved with the first-party dense LU.</item>
/// <item><b>Profile</b> (a curve z(r) on [0, rim], for the IGES surfaces of revolution): the first
/// two control values are made equal, so dz/dr = 0 on the axis and the revolved surface has no
/// cone point at its vertex; the first value interpolates sag(0), the others interpolate the sag
/// at ξ₂ … ξₙ₋₁ (a square system on the remaining Schoenberg–Whitney set).</item>
/// <item><b>Patch</b> (z(x, y) on a square slightly larger than the rim's circle, for the STEP
/// trimmed faces): tensor-product interpolation, C = A⁻¹·Z·A⁻ᵀ, two passes of the same LU. Beyond
/// the rim, where the trimmed face does not reach but the spline still needs values, the sag is
/// continued smoothly by its second-order Taylor expansion at the rim (the optical sag may be
/// undefined there).</item>
/// <item><b>Tolerance.</b> The deviation |z − sag| is measured on a check grid of
/// <see cref="ChecksPerSpan"/> points per knot span (inside the rim, plus the rim itself), and the
/// number of spans is doubled from <see cref="FirstSpans"/> until it is at most half the stated
/// tolerance, so the stated tolerance keeps a factor of two for the deviation between check
/// points. Past <see cref="MaxProfileSpans"/> or <see cref="MaxPatchSpans"/> the fit fails,
/// naming the surface. Since x and y are exact, |z − sag| bounds the distance from the true
/// surface along the axis, which bounds the normal distance too.</item>
/// </list>
/// </summary>
public static class BSplineFit
{
    public const int FirstSpans = 4;
    public const int MaxProfileSpans = 512;
    public const int MaxPatchSpans = 256;
    public const int ChecksPerSpan = 8;

    /// <summary>The patch's half-width as a multiple of the rim radius: the rim circle lies inside
    /// the square with a margin, so every rim point inverts inside the patch's domain.</summary>
    public const double PatchMargin = 1.05;

    public static ProfileFit FitProfile(CapShape shape, double rim, double tolerance, string what)
    {
        Check(shape, rim, tolerance, what);
        double worst = double.NaN;
        for (int spans = FirstSpans; spans <= MaxProfileSpans; spans *= 2)
        {
            var basis = new CubicBasis(0, rim, spans);
            int n = basis.Count;
            var a = new double[n * n];
            var rhs = new double[n];
            a[0] = 1;
            rhs[0] = Sag(shape, 0, rim, what);
            a[n] = -1;
            a[n + 1] = 1;
            for (int i = 2; i < n; i++)
            {
                var (span, nb) = basis.Evaluate(basis.Greville[i]);
                for (int j = 0; j <= CubicBasis.Degree; j++) a[i * n + span - CubicBasis.Degree + j] = nb[j];
                rhs[i] = Sag(shape, basis.Greville[i], rim, what);
            }
            var z = Solve(a, n, rhs, what);
            worst = 0;
            int checks = ChecksPerSpan * spans;
            for (int k = 0; k <= checks; k++)
            {
                double r = rim * k / checks;
                worst = Math.Max(worst, Math.Abs(basis.Value(z, r) - Sag(shape, r, rim, what)));
            }
            if (worst <= 0.5 * tolerance) return new ProfileFit(basis, z, worst);
        }
        throw new InvalidOperationException(FormattableString.Invariant(
            $"{what}: a cubic profile of {MaxProfileSpans} spans still deviates {worst:g3} m from the {shape.Describe()} (half the tolerance is {0.5 * tolerance:g3} m); loosen the export tolerance."));
    }

    public static PatchFit FitPatch(CapShape shape, double rim, double tolerance, string what)
    {
        Check(shape, rim, tolerance, what);
        double half = PatchMargin * rim;
        double worst = double.NaN;
        var extension = Extension(shape, rim, what);
        for (int spans = 2 * FirstSpans; spans <= MaxPatchSpans; spans *= 2)
        {
            var basis = new CubicBasis(-half, half, spans);
            int n = basis.Count;
            var a = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                var (span, nb) = basis.Evaluate(basis.Greville[i]);
                for (int j = 0; j <= CubicBasis.Degree; j++) a[i * n + span - CubicBasis.Degree + j] = nb[j];
            }
            var lu = DenseLu.FactorInPlace(a, n, new int[n]);
            var data = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                    data[i, k] = extension(Math.Sqrt(basis.Greville[i] * basis.Greville[i] + basis.Greville[k] * basis.Greville[k]));
            // C = A⁻¹·Z·A⁻ᵀ: solve along x for every column, then along y for every row.
            var column = new double[n];
            var solved = new double[n];
            var w = new double[n, n];
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++) column[i] = data[i, k];
                lu.Solve(column, solved);
                for (int i = 0; i < n; i++) w[i, k] = solved[i];
            }
            var z = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < n; k++) column[k] = w[i, k];
                lu.Solve(column, solved);
                for (int k = 0; k < n; k++) z[i, k] = solved[k];
            }
            var fit = new PatchFit(basis, z, half, 0);
            worst = PatchDeviation(fit, shape, rim, spans, what);
            if (worst <= 0.5 * tolerance) return fit with { MaxDeviation = worst };
        }
        throw new InvalidOperationException(FormattableString.Invariant(
            $"{what}: a bicubic patch of {MaxPatchSpans} × {MaxPatchSpans} spans still deviates {worst:g3} m from the {shape.Describe()} (half the tolerance is {0.5 * tolerance:g3} m); loosen the export tolerance."));
    }

    private static double PatchDeviation(PatchFit fit, CapShape shape, double rim, int spans, string what)
    {
        // Basis values at the check abscissae, once for both directions (the grid is square).
        int checks = ChecksPerSpan * spans;
        var t = new double[checks + 1];
        var span = new int[checks + 1];
        var nb = new double[checks + 1][];
        for (int i = 0; i <= checks; i++)
        {
            t[i] = -fit.HalfWidth + 2 * fit.HalfWidth * i / checks;
            (span[i], nb[i]) = fit.Basis.Evaluate(t[i]);
        }
        double worst = 0;
        for (int i = 0; i <= checks; i++)
            for (int k = 0; k <= checks; k++)
            {
                double r = Math.Sqrt(t[i] * t[i] + t[k] * t[k]);
                if (r > rim) continue;
                double sum = 0;
                for (int a = 0; a <= CubicBasis.Degree; a++)
                    for (int b = 0; b <= CubicBasis.Degree; b++)
                        sum += fit.Z[span[i] - CubicBasis.Degree + a, span[k] - CubicBasis.Degree + b] * nb[i][a] * nb[k][b];
                worst = Math.Max(worst, Math.Abs(sum - Sag(shape, r, rim, what)));
            }
        double zRim = Sag(shape, rim, rim, what);
        int around = 4 * checks;
        for (int k = 0; k < around; k++)
        {
            double phi = 2 * Math.PI * k / around;
            worst = Math.Max(worst, Math.Abs(fit.Value(rim * Math.Cos(phi), rim * Math.Sin(phi)) - zRim));
        }
        return worst;
    }

    /// <summary>The sag inside the rim; beyond it z(rim) + z′·d + ½z″·d², d = r − rim, with z″ from
    /// a backward difference of the exact slope (it only needs to be smooth: the trimmed face never
    /// reaches there).</summary>
    private static Func<double, double> Extension(CapShape shape, double rim, string what)
    {
        double z0 = Sag(shape, rim, rim, what);
        double s0 = shape.Slope(rim);
        double h = 1e-4 * rim;
        double s1 = shape.Slope(rim - h);
        if (!double.IsFinite(s0) || !double.IsFinite(s1))
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{what}: the slope of the {shape.Describe()} is not defined at its rim radius {rim:g6} m."));
        double curvature = (s0 - s1) / h;
        return r =>
        {
            if (r <= rim) return Sag(shape, r, rim, what);
            double d = r - rim;
            return z0 + s0 * d + 0.5 * curvature * d * d;
        };
    }

    private static double Sag(CapShape shape, double r, double rim, string what)
    {
        double z = shape.Sag(Math.Min(r, rim));
        if (!double.IsFinite(z))
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{what}: the sag of the {shape.Describe()} is not defined at r = {r:g6} m."));
        return z;
    }

    private static double[] Solve(double[] a, int n, double[] rhs, string what)
    {
        DenseLu lu;
        try { lu = DenseLu.FactorInPlace(a, n, new int[n]); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{what}: the spline fit's system is singular ({ex.Message}).", ex); }
        var x = new double[n];
        lu.Solve(rhs, x);
        return x;
    }

    private static void Check(CapShape shape, double rim, double tolerance, string what)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!(rim > 0) || !double.IsFinite(rim))
            throw new ArgumentOutOfRangeException(nameof(rim), rim, $"{what}: the rim radius must be positive.");
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, $"{what}: the fit tolerance must be positive.");
    }
}
