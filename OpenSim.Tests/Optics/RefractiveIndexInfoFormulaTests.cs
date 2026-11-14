using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-12: the refractiveindex.info database's nine dispersion formulas, as defined in
/// M. Polyanskiy, "Dispersion formulas", RefractiveIndex.INFO, 2014-06-29
/// (database/doc/Dispersion formulas.pdf, plans/sources/optics/riidb). Each formula is read from
/// an inline YAML page through the reader, so the type string, the coefficient list and the range
/// are exercised with the arithmetic. Expected values are worked by hand from the PDF's
/// expressions (the coefficients are chosen for the test; they are not reference data) or come
/// from an independent evaluator: OP-11's OpticStudio formulas for the forms the two lists share.
/// </summary>
public sealed class RefractiveIndexInfoFormulaTests
{
    private static OpticalMaterial Page(int formula, string coefficients, string range = "0.2 20") =>
        RefractiveIndexInfoReader.Read($"""
            REFERENCES: |
                Test page.
            DATA:
              - type: formula {formula}
                wavelength_range: {range}
                coefficients: {coefficients}
            """, "test/formula.yml", "TEST");

    /// <summary>201 wavelengths from <paramref name="lo"/> to <paramref name="hi"/>, ends included exactly.</summary>
    private static IEnumerable<double> Grid(double lo, double hi) =>
        Enumerable.Range(0, 201).Select(i => i == 200 ? hi : lo + (hi - lo) * i / 200);

    /// <summary>
    /// At λ = 2 µm (λ² = 4, λ⁻² = 0.25) the coefficients below make each term a power of two, so
    /// the hand-worked sums are exact in binary and the comparison is exact. Formula numbers and
    /// expressions as the PDF prints them.
    /// </summary>
    [Theory]
    // 1 Sellmeier: n² − 1 = C1 + C2 λ²/(λ² − C3²) = 0.5 + 0.25·4/4 → n² = 1.75
    [InlineData(1, "0.5 0.25 0", 1.75)]
    // 2 Sellmeier-2: n² − 1 = C1 + C2 λ²/(λ² − C3) + C4 λ²/(λ² − C5) + C6 λ²/(λ² − C7)
    //   = 0.5 + 0.25·1 + 0.125·2 + 0.03125·4 → n² = 2.125
    [InlineData(2, "0.5 0.25 0 0.125 2 0.03125 3", 2.125)]
    // 3 Polynomial: n² = C1 + C2 λ^C3 + C4 λ^C5 + C6 λ^C7 = 1 + 0.125·4 + 1·0.25 + 2·0.0625 → 1.875
    [InlineData(3, "1 0.125 2 1 -2 2 -4", 1.875)]
    // 4 RefractiveIndex.INFO: n² = C1 + C2 λ^C3/(λ² − C4^C5) + C6 λ^C7/(λ² − C8^C9) + C10 λ^C11
    //   = 1 + 0.25·4/(4 − 2) + 0.5·1/(4 − 3) + 0.0625·4 → 2.25
    [InlineData(4, "1 0.25 2 2 1 0.5 0 3 1 0.0625 2", 2.25)]
    // 8 Retro: (n² − 1)/(n² + 2) = C1 + C2 λ²/(λ² − C3) + C4 λ² = 0.25 + 0.0625·2 + 0.03125·4 = 0.5 → n² = 4
    [InlineData(8, "0.25 0.0625 2 0.03125", 4.0)]
    // 9 Exotic: n² = C1 + C2/(λ² − C3) + C4 (λ − C5)/((λ − C5)² + C6) = 1.25 + 1/2 + 1·1/(1 + 1) → 2.25
    [InlineData(9, "1.25 1 2 1 1 1", 2.25)]
    public void SquaredForms_GiveTheHandWorkedValue_AtTwoMicrometers(int formula, string coefficients, double nSquared)
    {
        Assert.Equal(Math.Sqrt(nSquared), Page(formula, coefficients).RelativeIndex(2.0));
    }

    /// <summary>The forms that give n directly. 5 Cauchy: n = C1 + C2 λ^C3 + C4 λ^C5 = 1.25 + 0.5·0.5
    /// + 0.0625·4 = 1.75. 6 Gases: n − 1 = C1 + C2/(C3 − λ⁻²) + C4/(C5 − λ⁻²) = 0.125 + 0.0625/0.5
    /// + 0.25/2 = 0.375.</summary>
    [Theory]
    [InlineData(5, "1.25 0.5 -1 0.0625 2", 1.75)]
    [InlineData(6, "0.125 0.0625 0.75 0.25 2.25", 1.375)]
    public void LinearForms_GiveTheHandWorkedValue_AtTwoMicrometers(int formula, string coefficients, double n)
    {
        Assert.Equal(n, Page(formula, coefficients).RelativeIndex(2.0));
    }

    /// <summary>7 Herzberger: n = C1 + C2/(λ² − 0.028) + C3 (1/(λ² − 0.028))² + C4 λ² + C5 λ⁴ + C6 λ⁶.
    /// At λ = 2 with C2 = 0.25·3.972 and C3 = 0.125·3.972² the two L terms are 0.25 and 0.125, so
    /// n = 1 + 0.25 + 0.125 + 4/64 + 16/512 + 64/4096 = 1.484375; 1/3.972 is not exact in binary,
    /// hence 14 digits.</summary>
    [Fact]
    public void Herzberger_GivesTheHandWorkedValue()
    {
        string c = FormattableString.Invariant($"1 {0.25 * 3.972:R} {0.125 * 3.972 * 3.972:R} {1.0 / 64:R} {1.0 / 512:R} {1.0 / 4096:R}");
        Assert.Equal(1.484375, Page(7, c).RelativeIndex(2.0), 1e-14);
    }

    /// <summary>
    /// Formula 2 with C1 = 0 is OpticStudio's Sellmeier 1 (OP-11's formula 2) with the same B, C
    /// pairs. The coefficients are N-BK7's as the database's copy of the SCHOTT catalog gives them
    /// (database/data/specs/schott/optical/N-BK7.yml, "coefficients: 0 1.03961212 0.00600069867
    /// 0.231792344 0.0200179144 1.01046945 103.560653"); the two evaluators are separate code.
    /// </summary>
    [Fact]
    public void SellmeierTwo_AgreesWith_OpticStudioSellmeierOne()
    {
        var page = Page(2, "0 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653", "0.3 2.5");
        double[] agf = { 1.03961212, 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653 };
        foreach (double w in Grid(0.3, 2.5))
            Assert.Equal(DispersionFormulas.Evaluate(DispersionFormula.Sellmeier1, agf, w), page.RelativeIndex(w), 1e-14);
    }

    /// <summary>Formula 1 squares its pole coefficients where formula 2 does not, so formula 1
    /// with C3, C5, C7 equals formula 2 with C3², C5², C7². Coefficients: Malitson's fused silica
    /// (database/data/main/SiO2/nk/Malitson.yml, "coefficients: 0 0.6961663 0.0684043 0.4079426
    /// 0.1162414 0.8974794 9.896161").</summary>
    [Fact]
    public void SellmeierOne_IsSellmeierTwo_WithSquaredPoles()
    {
        double[] c = { 0, 0.6961663, 0.0684043, 0.4079426, 0.1162414, 0.8974794, 9.896161 };
        var one = Page(1, string.Join(' ', c.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))), "0.21 6.7");
        double[] squared = { c[0], c[1], c[2] * c[2], c[3], c[4] * c[4], c[5], c[6] * c[6] };
        var two = Page(2, string.Join(' ', squared.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))), "0.21 6.7");
        foreach (double w in Grid(0.21, 6.7))
            Assert.Equal(two.RelativeIndex(w), one.RelativeIndex(w), 1e-13);
    }

    /// <summary>Formula 7 is OpticStudio's Herzberger (OP-11's formula 3) term for term, both with
    /// 0.028 as printed. Coefficients chosen for the test.</summary>
    [Fact]
    public void Herzberger_AgreesWith_OpticStudioHerzberger()
    {
        double[] c = { 1.5, 0.004, -0.0002, -0.001, 1e-5, -1e-7 };
        var page = Page(7, string.Join(' ', c.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))), "0.4 2.0");
        foreach (double w in Grid(0.4, 2.0))
            Assert.Equal(DispersionFormulas.Evaluate(DispersionFormula.Herzberger, c, w), page.RelativeIndex(w), 1e-14);
    }

    /// <summary>Formula 3 with exponents 0, 2, −2, −4, −6, −8 is OpticStudio's Schott formula
    /// (OP-11's formula 1). Coefficients chosen for the test.</summary>
    [Fact]
    public void Polynomial_AgreesWith_OpticStudioSchott()
    {
        double[] a = { 2.27, -0.0101, 0.0106, 2.1e-4, -8e-6, 6e-7 };
        string c = FormattableString.Invariant($"{a[0]:R} {a[1]:R} 2 {a[2]:R} -2 {a[3]:R} -4 {a[4]:R} -6 {a[5]:R} -8");
        var page = Page(3, c, "0.4 2.0");
        foreach (double w in Grid(0.4, 2.0))
            Assert.Equal(DispersionFormulas.Evaluate(DispersionFormula.Schott, a, w), page.RelativeIndex(w), 1e-13);
    }

    /// <summary>A page whose coefficients stop part-way through a term is refused at reading.
    /// The database has one: main/AgGaSe2/nk/Boyd-o.yml gives formula 2 four coefficients
    /// ("3.6453 2.2057 0.1879 1.8377"), a C4 with no C5.</summary>
    [Fact]
    public void ACoefficientListEndingInsideATerm_IsRefused()
    {
        var e = Assert.Throws<InvalidDataException>(() => Page(2, "3.6453 2.2057 0.1879 1.8377", "0.725 13.5"));
        Assert.Contains("test/formula.yml line 6", e.Message);
        Assert.Contains("1, 3, 5, 7", e.Message);
        Assert.Contains("4 were given", e.Message);
        Assert.Throws<InvalidDataException>(() => Page(4, "1 2 3"));                 // half of the first fraction
        Assert.Throws<InvalidDataException>(() => Page(1, string.Join(' ', Enumerable.Repeat("0", 19))));   // past C17
    }

    [Fact]
    public void ANumberOutsideOneToNine_IsRefused_AndAnIndexThatIsNotRealIsNamed()
    {
        var e = Assert.Throws<InvalidDataException>(() => Page(10, "1"));
        Assert.Contains("'formula 10' is not one of the database's formulas 1–9", e.Message);

        // n² = C1 = −1: no real index. Evaluating names the material and the wavelength.
        var negative = Page(3, "-1");
        var ex = Assert.Throws<InvalidOperationException>(() => negative.RelativeIndex(1.0));
        Assert.Contains("Glass 'TEST'", ex.Message);
        Assert.Contains("refractiveindex.info formula 3 (Polynomial)", ex.Message);
    }

    [Fact]
    public void TheNames_AreTheDefinitionsOwn()
    {
        string[] expected = { "Sellmeier (preferred)", "Sellmeier-2", "Polynomial", "RefractiveIndex.INFO", "Cauchy", "Gases", "Herzberger", "Retro", "Exotic" };
        Assert.Equal(expected, Enumerable.Range(1, 9).Select(RefractiveIndexInfoFormulas.Name));
        Assert.Equal("refractiveindex.info formula 2 (Sellmeier-2)", Page(2, "0 1 0").FormulaName);
        Assert.Null(Page(2, "0 1 0").Formula);                  // not OpticStudio's formula 2
    }
}
