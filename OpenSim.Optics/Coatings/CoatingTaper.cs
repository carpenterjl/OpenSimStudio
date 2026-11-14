using System.Globalization;

namespace OpenSim.Optics.Coatings;

/// <summary>
/// A taper profile (OpticStudio's TAPR section): a factor that multiplies a layer's nominal
/// thickness over the surface. Read from the OpticStudio 2025 R2 help, "The TAPR Data Section"
/// (saved under plans/sources/optics/coatingfile/):
/// <list type="bullet">
/// <item>d_eff = d · (f_r + f_c + f_p), and a negative sum counts as zero;</item>
/// <item>x′ = (x − x_d) cos θ − (y − y_d) sin θ, y′ = (x − x_d) sin θ + (y − y_d) cos θ, θ the
/// rotation in degrees, r = √(x′² + y′²);</item>
/// <item>f_r = Σ_{i=0}^{22} β_i r^i (radial terms RT 0–22);</item>
/// <item>f_c = Σ_{i=0}^{5} Δ_i (cos α)^i with sin α = |r / R|, R the surface's radius of
/// curvature (cosine terms CT 0–5; a plane has R infinite, so cos α = 1);</item>
/// <item>f_p = Σ γ_i E_i with E_0 = 1, E_1 = x′, E_2 = y′, E_3 = x′², E_4 = x′y′, E_5 = y′²,
/// E_6 = x′³, … (polynomial terms PT 0–20).</item>
/// </list>
/// Every term not given is zero, so a taper with no terms gives a zero thickness — as the help
/// defines it. Coordinates, decenters and coefficients are in the lens's units, as OpticStudio
/// keeps them (the help: the coefficients' units depend on the lens units); this is the one
/// place coating data are not converted to meters, because the coefficients cannot be converted
/// without knowing the lens they were written for.
/// <para>
/// <b>Read and inferred.</b> The help lists E_0 to E_6 and then "etc."; the terms beyond E_6 are
/// taken to continue the same pattern (all monomials of degree 3, then 4 and 5, each in order of
/// falling power of x′), which gives exactly 21 terms (0–20) through degree 5, the range the help
/// gives for PT. The help's sum for f_p runs to 22 while its PT range stops at 20; 20 is used.
/// Both are listed in docs/Optics-Conventions.md section 10.
/// </para>
/// </summary>
public sealed record CoatingTaper
{
    public const int MaxRadialTerm = 22, MaxCosineTerm = 5, MaxPolynomialTerm = 20;

    public required string Name { get; init; }
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double RotationDegrees { get; init; }

    /// <summary>β_0…β_22, Δ_0…Δ_5 and γ_0…γ_20 by term number; a missing term is zero.</summary>
    public IReadOnlyDictionary<int, double> RadialTerms { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, double> CosineTerms { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, double> PolynomialTerms { get; init; } = new Dictionary<int, double>();

    /// <summary>The (x, y) powers of polynomial term <paramref name="term"/>, 0–20.</summary>
    public static (int PowerX, int PowerY) PolynomialPowers(int term)
    {
        if (term < 0 || term > MaxPolynomialTerm)
            throw new ArgumentOutOfRangeException(nameof(term), term, $"A taper polynomial term number lies in 0–{MaxPolynomialTerm}.");
        int degree = 0, first = 0;
        while (term >= first + degree + 1) { first += degree + 1; degree++; }
        int j = term - first;                                 // j-th monomial of this degree
        return (degree - j, j);
    }

    /// <summary>What is wrong with the taper, one line each; empty when it can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("A taper has no name.");
        Check(RadialTerms, MaxRadialTerm, "radial", problems);
        Check(CosineTerms, MaxCosineTerm, "cosine", problems);
        Check(PolynomialTerms, MaxPolynomialTerm, "polynomial", problems);
        if (!double.IsFinite(DecenterX) || !double.IsFinite(DecenterY) || !double.IsFinite(RotationDegrees))
            problems.Add($"Taper '{Name}': the decenters and the rotation must be finite.");
        return problems;
    }

    private void Check(IReadOnlyDictionary<int, double> terms, int max, string kind, List<string> problems)
    {
        foreach (var (term, value) in terms)
        {
            if (term < 0 || term > max) problems.Add($"Taper '{Name}': {kind} term {term} is outside 0–{max}.");
            if (!double.IsFinite(value)) problems.Add($"Taper '{Name}': {kind} term {term} is not a finite number.");
        }
    }

    /// <summary>The thickness factor f_r + f_c + f_p (zero if negative) at the surface point
    /// (<paramref name="x"/>, <paramref name="y"/>) of a surface of radius of curvature
    /// <paramref name="surfaceRadius"/> (lens units; 0 or infinite for a plane).</summary>
    public double Factor(double x, double y, double surfaceRadius = double.PositiveInfinity)
    {
        if (Validate() is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems));
        var (sin, cos) = OpticsConventions.SinCosDegrees(RotationDegrees);
        double dx = x - DecenterX, dy = y - DecenterY;
        double xp = dx * cos - dy * sin;
        double yp = dx * sin + dy * cos;
        double r = Math.Sqrt(xp * xp + yp * yp);

        double fr = 0;
        foreach (var (i, beta) in RadialTerms) fr += beta * Math.Pow(r, i);

        double fc = 0;
        if (CosineTerms.Count > 0)
        {
            double sinAlpha = surfaceRadius == 0 || double.IsInfinity(surfaceRadius) ? 0 : Math.Abs(r / surfaceRadius);
            if (sinAlpha > 1)
                throw new InvalidOperationException(
                    $"Taper '{Name}': the point at r = {F(r)} lies beyond the radius of curvature {F(surfaceRadius)} of the surface, so its cosine taper is not defined.");
            double cosAlpha = Math.Sqrt(1 - sinAlpha * sinAlpha);
            foreach (var (i, delta) in CosineTerms) fc += delta * Math.Pow(cosAlpha, i);
        }

        double fp = 0;
        foreach (var (i, gamma) in PolynomialTerms)
        {
            var (px, py) = PolynomialPowers(i);
            fp += gamma * Math.Pow(xp, px) * Math.Pow(yp, py);
        }
        return Math.Max(0, fr + fc + fp);
    }

    private static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
