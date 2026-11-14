using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-11: the thirteen dispersion formulas, term by term. Each expected value is worked by hand
/// from the expression as AGFFileReader.jl (<c>src/utilities.jl</c> lines 133–183) and
/// ZemaxGlass.py (<c>get_dispersion</c>, lines 845–888) print it.
/// </summary>
public sealed class DispersionFormulaTests
{
    /// <summary>
    /// At λ = 2 µm (λ² = 4) every power of λ is a power of two, and the coefficients below are
    /// chosen so each term adds a different power of two to the sum (and Sellmeier poles at
    /// C = 0, 2, 3, 3.5, 3.75 make λ²/(λ² − C) = 1, 2, 4, 8, 16). A term with the wrong power,
    /// the wrong coefficient or the wrong sign changes the sum, and the sums are exact in binary,
    /// so the comparison is exact.
    /// </summary>
    [Theory]
    // Schott: n² = C1 + C2 λ² + C3 λ⁻² + C4 λ⁻⁴ + C5 λ⁻⁶ + C6 λ⁻⁸ = 1 + 0.5 + 0.25 + 0.125 + 0.0625 + 0.03125
    [InlineData(1, new[] { 1, 0.125, 1, 2, 4, 8 }, 1.96875)]
    // Sellmeier 1: n² = 1 + C1·1 + C3·2 + C5·4 = 1 + 0.5 + 0.25 + 0.125
    [InlineData(2, new[] { 0.5, 0, 0.125, 2, 0.03125, 3 }, 1.875)]
    // Sellmeier 3: four Sellmeier terms, 1 + 0.5 + 0.25 + 0.125 + 0.0625
    [InlineData(6, new[] { 0.5, 0, 0.125, 2, 0.03125, 3, 0.0078125, 3.5 }, 1.9375)]
    // Handbook of Optics 1: n² = C1 + C2/(λ² − C3) − C4 λ² = 2 + 1/2 − 0.25
    [InlineData(7, new[] { 2, 1, 2, 0.0625 }, 2.25)]
    // Handbook of Optics 2: n² = C1 + C2 λ²/(λ² − C3) − C4 λ² = 1.5 + 0.5 − 0.125
    [InlineData(8, new[] { 1.5, 0.25, 2, 0.03125 }, 1.875)]
    // Sellmeier 4: n² = C1 + C2 λ²/(λ² − C3) + C4 λ²/(λ² − C5) = 1 + 0.5 + 0.25
    [InlineData(9, new[] { 1, 0.25, 2, 0.0625, 3 }, 1.75)]
    // Extended 1: n² = C1 + C2 λ² + C3 λ⁻² … + C8 λ⁻¹² = 1 + 0.25 + 0.125 + … + 0.00390625
    [InlineData(10, new[] { 1, 0.0625, 0.5, 1, 2, 4, 8, 16 }, 1.49609375)]
    // Sellmeier 5: five Sellmeier terms, 1 + 0.5 + 0.25 + 0.125 + 0.0625 + 0.015625
    [InlineData(11, new[] { 0.5, 0, 0.125, 2, 0.03125, 3, 0.0078125, 3.5, 0.0009765625, 3.75 }, 1.953125)]
    // Extended 2: n² = C1 + C2 λ² + C3 λ⁻² + C4 λ⁻⁴ + C5 λ⁻⁶ + C6 λ⁻⁸ + C7 λ⁴ + C8 λ⁶
    //   = 1 + 0.25 + 0.125 + 0.0625 + 0.03125 + 0.015625 + 0.0078125 + 0.00390625
    [InlineData(12, new[] { 1, 0.0625, 0.5, 1, 2, 4, 1.0 / 2048, 1.0 / 16384 }, 1.49609375)]
    // Extended 3: n² = C1 + C2 λ² + C3 λ⁴ + C4 λ⁻² + … + C9 λ⁻¹²
    //   = 1 + 0.25 + 0.125 + 0.0625 + 0.03125 + 0.015625 + 0.0078125 + 0.00390625 + 0.001953125
    [InlineData(13, new[] { 1, 0.0625, 1.0 / 128, 0.25, 0.5, 1, 2, 4, 8 }, 1.498046875)]
    public void SquaredForms_GiveTheHandWorkedSum_AtTwoMicrometers(int formula, double[] c, double nSquared)
    {
        Assert.Equal(Math.Sqrt(nSquared), DispersionFormulas.Evaluate((DispersionFormula)formula, c, 2.0));
    }

    /// <summary>Conrady: n = C1 + C2/λ + C3/λ^3.5. At λ = 4 µm, λ^3.5 = 128:
    /// 1.5 + 0.5/4 + 8/128 = 1.6875.</summary>
    [Fact]
    public void Conrady_GivesTheHandWorkedValue()
    {
        Assert.Equal(1.6875, DispersionFormulas.Evaluate(DispersionFormula.Conrady, new[] { 1.5, 0.5, 8 }, 4.0), 15);
    }

    /// <summary>Herzberger: n = C1 + C2 L + C3 L² + C4 λ² + C5 λ⁴ + C6 λ⁶ with L = 1/(λ² − 0.028).
    /// At λ = 2 µm, L = 1/3.972; with C2 = 0.25·3.972 and C3 = 0.125·3.972² the L terms are 0.25
    /// and 0.125, so n = 1 + 0.25 + 0.125 + 4/64 + 16/512 + 64/4096 = 1.484375. L is not exact in
    /// binary, so a few ulps.</summary>
    [Fact]
    public void Herzberger_GivesTheHandWorkedValue()
    {
        double[] c = { 1, 0.25 * 3.972, 0.125 * 3.972 * 3.972, 1.0 / 64, 1.0 / 512, 1.0 / 4096 };
        Assert.Equal(1.484375, DispersionFormulas.Evaluate(DispersionFormula.Herzberger, c, 2.0), 14);
    }

    /// <summary>An independent value: AGFFileReader's own test suite ("test against true
    /// values", <c>test/runtests.jl</c> line 191) gives n = 1.519417351519283 for its
    /// Examples_N_BK7 (Sellmeier 1, coefficients in <c>src/BaseGlasses.jl</c> line 8) at 533 nm
    /// and the reference temperature and pressure, where its air corrections cancel. Its
    /// tolerance there is 1e−14.</summary>
    [Fact]
    public void Sellmeier1_MatchesAgfFileReadersNbk7Value()
    {
        double[] c = { 1.03961212, 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653 };
        Assert.Equal(1.519417351519283, DispersionFormulas.Evaluate(DispersionFormula.Sellmeier1, c, 0.533), 14);
    }

    /// <summary>
    /// Sellmeier 2 is UNVERIFIED (DispersionFormulas.IsVerified): this pins which of the two
    /// readers' forms is implemented, it does not confirm it. With C4 = 0 the forms agree:
    /// n² = 1 + C1 + C2 λ²/(λ² − C3²) = 1 + 0.5 + 0.125·4/(4 − 2.25). With C4 ≠ 0 the code must
    /// give AGFFileReader's C4 λ²/(λ² − C5²) = 0.25·4/3, not ZemaxGlass's C4/(λ² − C5²) = 0.25/3.
    /// </summary>
    [Fact]
    public void Sellmeier2_IsAgfFileReadersForm_AndMarkedUnverified()
    {
        Assert.False(DispersionFormulas.IsVerified(DispersionFormula.Sellmeier2));
        Assert.True(Enum.GetValues<DispersionFormula>().Where(f => f != DispersionFormula.Sellmeier2).All(DispersionFormulas.IsVerified));

        double agree = DispersionFormulas.Evaluate(DispersionFormula.Sellmeier2, new[] { 0.5, 0.125, 1.5, 0, 1 }, 2.0);
        Assert.Equal(Math.Sqrt(1 + 0.5 + 0.125 * 4 / 1.75), agree, 15);

        double agf = DispersionFormulas.Evaluate(DispersionFormula.Sellmeier2, new[] { 0.5, 0.125, 1.5, 0.25, 1 }, 2.0);
        Assert.Equal(Math.Sqrt(1 + 0.5 + 0.125 * 4 / 1.75 + 0.25 * 4 / 3), agf, 15);
        Assert.NotEqual(Math.Sqrt(1 + 0.5 + 0.125 * 4 / 1.75 + 0.25 / 3), agf, 6);
    }

    [Fact]
    public void NumberingAndNames_FollowAgfFileReader()
    {
        // DISPFORM_NAMES, src/constants.jl line 12, 1-based.
        string[] names = { "Schott", "Sellmeier1", "Herzberger", "Sellmeier2", "Conrady", "Sellmeier3", "HandbookOfOptics1",
                           "HandbookOfOptics2", "Sellmeier4", "Extended1", "Sellmeier5", "Extended2", "Extended3" };
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(names[i], ((DispersionFormula)(i + 1)).ToString());
            Assert.Equal(names[i], DispersionFormulas.Name((DispersionFormula)(i + 1)).Replace(" ", "").Replace("of", "Of"));
        }
        Assert.False(DispersionFormulas.IsDefined(0));
        Assert.False(DispersionFormulas.IsDefined(-2));
        Assert.False(DispersionFormulas.IsDefined(14));
    }

    [Fact]
    public void AShortCoefficientList_IsRefused_NamingTheFormula()
    {
        var ex = Assert.Throws<ArgumentException>(() => DispersionFormulas.Evaluate(DispersionFormula.Extended3, new double[8], 0.5));
        Assert.Contains("13 (Extended 3) reads 9", ex.Message);
    }
}
