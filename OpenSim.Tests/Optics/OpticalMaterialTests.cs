using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>OP-11: the optical material's index, its range check, and tabulated materials.</summary>
public sealed class OpticalMaterialTests
{
    private static readonly double[] Nbk7 = { 1.03961212, 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653 };

    private static OpticalMaterial Bk7() => new()
    {
        Name = "SELLMEIER1-BK7", Catalog = "fixture", FormulaNumber = 2, Coefficients = Nbk7, MinMicrometers = 0.3, MaxMicrometers = 2.5
    };

    [Fact]
    public void TheIndex_IsTheFormulasValue_InsideTheRange_EndsIncluded()
    {
        var glass = Bk7();
        Assert.Equal(DispersionFormulas.Evaluate(DispersionFormula.Sellmeier1, Nbk7, 0.533), glass.RelativeIndex(0.533));
        Assert.True(glass.Covers(0.3) && glass.Covers(2.5));
        Assert.True(double.IsFinite(glass.RelativeIndex(0.3)) && double.IsFinite(glass.RelativeIndex(2.5)));
        Assert.Null(glass.ExtinctionCoefficient(0.5));          // a catalog glass carries no k
    }

    [Theory]
    [InlineData(0.29)]
    [InlineData(2.51)]
    [InlineData(double.NaN)]
    public void OutsideTheRange_FailsLoudly_NamingGlassCatalogWavelengthAndRange(double micrometers)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Bk7().RelativeIndex(micrometers));
        Assert.Contains("Glass 'SELLMEIER1-BK7' in catalog 'fixture'", ex.Message);
        Assert.Contains("0.3–2.5 µm", ex.Message);
        Assert.Contains("not extrapolated", ex.Message);
        if (double.IsFinite(micrometers)) Assert.Contains(micrometers.ToString(System.Globalization.CultureInfo.InvariantCulture) + " µm", ex.Message);
    }

    [Fact]
    public void AFormulaThatIsNotOpticStudios_IsRefusedByNumber()
    {
        var cauchy = Bk7() with { FormulaNumber = -2 };
        var ex = Assert.Throws<NotSupportedException>(() => cauchy.RelativeIndex(0.5));
        Assert.Contains("formula -2", ex.Message);
        Assert.Null(cauchy.Formula);
    }

    [Fact]
    public void NoRange_NoFormula_AShortCdLine_OrNoRealIndex_AreRefused()
    {
        Assert.Contains("no LD line", Assert.Throws<InvalidOperationException>(() => (Bk7() with { MaxMicrometers = null }).RelativeIndex(0.5)).Message);
        Assert.Contains("neither", Assert.Throws<InvalidOperationException>(() => (Bk7() with { FormulaNumber = null }).RelativeIndex(0.5)).Message);
        Assert.Contains("reads 6", Assert.Throws<InvalidOperationException>(() => (Bk7() with { Coefficients = Nbk7[..5] }).RelativeIndex(0.5)).Message);
        // Handbook of Optics 1 with C1 = −1: n² < 0, no real index.
        var imaginary = Bk7() with { FormulaNumber = 7, Coefficients = new[] { -1.0, 0, 0, 0 } };
        Assert.Contains("no real positive index", Assert.Throws<InvalidOperationException>(() => imaginary.RelativeIndex(0.5)).Message);
    }

    /// <summary>Linear interpolation between rows (TabulatedIndex's stated choice): halfway
    /// between rows is the mean, rows themselves are exact, and the ends of the table are the
    /// range.</summary>
    [Fact]
    public void ATabulatedMaterial_InterpolatesLinearly_AndDoesNotExtrapolate()
    {
        var table = new TabulatedIndex
        {
            Points = new[] { new TabulatedIndexPoint(0.4, 1.50, 0.0), new TabulatedIndexPoint(0.6, 1.48, 0.02), new TabulatedIndexPoint(1.0, 1.46, 0.0) }
        };
        var material = new OpticalMaterial { Name = "TAB", Tabulated = table };
        Assert.Empty(table.Validate());
        Assert.Equal((0.4, 1.0), material.ValidRange!.Value);
        Assert.Equal(1.48, material.RelativeIndex(0.6));
        Assert.Equal(1.49, material.RelativeIndex(0.5), 15);
        Assert.Equal(0.01, material.ExtinctionCoefficient(0.5)!.Value, 15);
        Assert.Equal(1.47, material.RelativeIndex(0.8), 15);
        Assert.Equal(0.01, material.ExtinctionCoefficient(0.8)!.Value, 15);
        Assert.Equal(1.50, material.RelativeIndex(0.4));
        Assert.Equal(1.46, material.RelativeIndex(1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => material.RelativeIndex(1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => material.ExtinctionCoefficient(0.39));
    }

    [Fact]
    public void ABadTable_IsNamedRowByRow_AndRefused()
    {
        var table = new TabulatedIndex
        {
            Points = new[] { new TabulatedIndexPoint(0.6, 1.5, 0), new TabulatedIndexPoint(0.5, -1, -0.1) }
        };
        var problems = table.Validate();
        Assert.Contains(problems, p => p.StartsWith("Row 2") && p.Contains("increase"));
        Assert.Contains(problems, p => p.StartsWith("Row 2") && p.Contains("n must be positive"));
        Assert.Contains(problems, p => p.StartsWith("Row 2") && p.Contains("k must be zero or positive"));
        var material = new OpticalMaterial { Name = "BAD", Tabulated = table };
        Assert.Contains("not usable", Assert.Throws<InvalidOperationException>(() => material.RelativeIndex(0.55)).Message);
        Assert.Single(new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.5, 1.5, 0) } }.Validate());
    }

    [Theory]
    [InlineData("1.62004", 5e-6)]
    [InlineData("1.90138E+00", 5e-6)]
    [InlineData("3.16400E+01", 5e-5)]
    [InlineData("52.3", 0.05)]
    [InlineData("1.000000", 5e-7)]
    [InlineData("2", 0.5)]
    [InlineData("-9.0E-3", 5e-5)]
    public void APrintedNumber_KnowsHalfItsLastDigit(string text, double half)
    {
        Assert.True(PrintedNumber.TryParse(text, out var number));
        Assert.Equal(half, number.HalfLastDigit, 15);
        Assert.Equal(text, number.Text);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("_")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("abc")]
    public void NotANumber_IsNotParsed(string text) => Assert.False(PrintedNumber.TryParse(text, out _));
}
