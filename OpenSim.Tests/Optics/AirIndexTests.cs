using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-13: the index of air. The Ciddor and modified Edlén equations are checked against the
/// values NIST publishes for them, and against each other where NIST states how closely they
/// agree; the TIE-19 formula against its own temperature coefficient. Sources:
/// <list type="bullet">
/// <item>NIST: J. A. Stone and J. H. Zimmerman, Engineering Metrology Toolbox, "Refractive Index
/// of Air", Documentation (https://emtoolbox.nist.gov/Wavelength/Documentation.asp), Section VII
/// Table 1 and Table 2, Appendix A.</item>
/// <item>SCHOTT TIE-19, "Temperature Coefficient of the Refractive Index", Version August 2023,
/// equations (8)–(10).</item>
/// </list>
/// </summary>
public sealed class AirIndexTests
{
    /// <summary>
    /// NIST documentation Section VII, Table 1: "Index of refraction as calculated using the
    /// Modified Edlén equation and Ciddor equation, assuming 450 µmol/mol CO2 concentration";
    /// the 100 % humidity rows use the IAPWS vapour pressure. Columns: temperature (°C), relative
    /// humidity (%), pressure (kPa), wavelength (nm; the page takes the vacuum wavelength,
    /// Section IV), n (Ciddor), n (modified Edlén). Copied row by row.
    /// </summary>
    public static IEnumerable<object[]> NistTable1 => new[]
    {
        new object[] { 20.0, 0.0, 101.325, 633.0, 1.000271800, 1.000271799 },
        new object[] { 20.0, 0.0, 60.0, 633.0, 1.000160924, 1.000160920 },
        new object[] { 20.0, 0.0, 120.0, 633.0, 1.000321916, 1.000321918 },
        new object[] { 50.0, 0.0, 100.0, 633.0, 1.000243285, 1.000243270 },
        new object[] { 5.0, 0.0, 100.0, 633.0, 1.000282756, 1.000282750 },
        new object[] { -40.0, 0.0, 100.0, 633.0, 1.000337580, 1.000337471 },
        new object[] { 50.0, 100.0, 120.0, 633.0, 1.000287924, 1.000287864 },
        new object[] { 40.0, 75.0, 120.0, 633.0, 1.000299418, 1.000299406 },
        new object[] { 20.0, 100.0, 100.0, 633.0, 1.000267394, 1.000267394 },
        new object[] { 40.0, 100.0, 110.0, 1700.0, 1.000270247, 1.000270237 },
        new object[] { 20.0, 0.0, 101.325, 1700.0, 1.000268479, 1.000268483 },
        new object[] { 40.0, 100.0, 110.0, 300.0, 1.000289000, 1.000288922 },
        new object[] { 20.0, 0.0, 101.325, 300.0, 1.000286581, 1.000286579 },
        new object[] { -40.0, 0.0, 120.0, 300.0, 1.000427233, 1.000427072 },
    };

    /// <summary>
    /// Both equations reproduce NIST's own numbers. Table 1 prints nine decimals, so the
    /// tolerance is one unit in that last printed digit, 1e-9: rounding alone allows 5e-10, and
    /// the largest deviation seen when this test was written was 5.4e-10 (the 40 °C, 100 %,
    /// 1700 nm Ciddor row), which rounding of NIST's inputs or intermediate output could explain.
    /// The equations themselves differ by up to 1.6e-7 in this table, so 1e-9 tells them apart.
    /// </summary>
    [Theory]
    [MemberData(nameof(NistTable1))]
    public void CiddorAndEdlen_ReproduceNistTable1(double celsius, double rh, double kilopascals, double nanometers,
        double ciddor, double edlen)
    {
        var air = new AirConditions(celsius, kilopascals * 1000, 450, rh);
        Assert.Equal(ciddor, AirIndex.Absolute(AirIndexModel.Ciddor, nanometers / 1000, air), 1e-9);
        Assert.Equal(edlen, AirIndex.Absolute(AirIndexModel.ModifiedEdlen, nanometers / 1000, air), 1e-9);
    }

    /// <summary>NIST Table 2, the IAPWS saturation vapour pressure "rounded off to the nearest
    /// Pascal": 100 °C 101418, 50 °C 12351, 40 °C 7384, 20 °C 2339, −10 °C 260 (over ice, A8–A13).</summary>
    [Theory]
    [InlineData(100.0, 101418.0)]
    [InlineData(50.0, 12351.0)]
    [InlineData(40.0, 7384.0)]
    [InlineData(20.0, 2339.0)]
    [InlineData(-10.0, 260.0)]
    public void SaturationVaporPressure_IsNistTable2(double celsius, double pascals) =>
        Assert.InRange(AirIndex.SaturationVaporPressure(celsius), pascals - 0.5, pascals + 0.5);

    /// <summary>
    /// NIST Section VII: "for dry air at temperatures between 5 °C and 40 °C, pressures between
    /// 60 kPa and 110 kPa, and wavelengths between 300 nm and 1700 nm, the maximum difference
    /// between the Ciddor and modified Edlén equations does not exceed 1×10⁻⁸"; "At 100 %
    /// humidity, the maximum difference within this range increases to 8×10⁻⁸"; "for wavelengths
    /// longer than 400 nm, the maximum discrepancy ... is only 2.7×10⁻⁸". Two independent
    /// equations agreeing as NIST says they do checks both transcriptions over the whole range.
    /// </summary>
    [Fact]
    public void CiddorAndEdlen_AgreeAsNistStates()
    {
        double dry = 0, wet = 0, wetAbove400 = 0;
        for (double t = 5; t <= 40; t += 5)
        for (double kpa = 60; kpa <= 110; kpa += 10)
        foreach (double nm in new[] { 300.0, 350, 400, 450, 500, 600, 700, 850, 1000, 1300, 1700 })
        {
            double Diff(double rh)
            {
                var air = new AirConditions(t, kpa * 1000, 450, rh);
                return Math.Abs(AirIndex.Absolute(AirIndexModel.Ciddor, nm / 1000, air) - AirIndex.Absolute(AirIndexModel.ModifiedEdlen, nm / 1000, air));
            }
            dry = Math.Max(dry, Diff(0));
            double w = Diff(100);
            wet = Math.Max(wet, w);
            if (nm > 400) wetAbove400 = Math.Max(wetAbove400, w);
        }
        Assert.True(dry <= 1e-8, $"dry: {dry:E3}");
        Assert.True(wet <= 8e-8, $"100 %: {wet:E3}");
        Assert.True(wetAbove400 <= 2.7e-8, $"100 %, above 400 nm: {wetAbove400:E3}");
    }

    /// <summary>The gate: a vacuum has index 1 exactly, in every model, at any wavelength and
    /// temperature (no range applies to a vacuum).</summary>
    [Fact]
    public void AVacuum_HasIndexOneExactly_InEveryModel()
    {
        foreach (var model in AirIndexModels.Names.All)
        foreach (double um in new[] { 0.2, 0.55, 1.0, 10.6 })
        foreach (double t in new[] { -100.0, 20, 150 })
        {
            var vacuum = new AirConditions(t, 0, 450, 50);
            Assert.Equal(1.0, AirIndex.Absolute(model, um, vacuum));
            Assert.Equal(um, AirIndex.VacuumFromAir(model, um, vacuum));
            Assert.Equal(um, AirIndex.AirFromVacuum(model, um, vacuum));
        }
    }

    /// <summary>
    /// TIE-19 equation (10) against a central difference of equation (8). They are not the same
    /// expression: (8) differentiates to −3.4785·10⁻³ (n − 1)/(1 + 3.4785·10⁻³ (T − 15)) =
    /// −(n − 1)/(1/3.4785·10⁻³ − 15 + T), and (10) is −(n − 1)/(1/0.00367 + T). The two
    /// denominators' constants are 272.48024 and 272.47956, so the coefficients differ by
    /// 6.7·10⁻⁴/(272.48 + T) of their value: below 3·10⁻⁶ from −40 °C to 100 °C. The difference
    /// step (0.01 K) adds rounding of about 10⁻⁸ of the value; 3.5·10⁻⁶ allows both.
    /// </summary>
    [Fact]
    public void Tie19Equation10_IsTheDerivativeOfEquation8_ToTheSourcesRounding()
    {
        const double h = 0.01, p = AirIndex.StandardPressurePascals;
        foreach (double um in new[] { 0.4, 0.6328, 1.0, 2.0 })
        for (double t = -40; t <= 100; t += 10)
        {
            double fd = (AirIndex.Schott(um, t + h, p) - AirIndex.Schott(um, t - h, p)) / (2 * h);
            double eq10 = AirIndex.SchottTemperatureCoefficient(um, t, p);
            Assert.True(Math.Abs(eq10 / fd - 1) <= 3.5e-6, $"{um} µm, {t} °C: eq. (10) {eq10:E9}, difference of (8) {fd:E9}");
        }
    }

    /// <summary>
    /// The plan expected the OpticStudio-compatible formula and Ciddor to differ "at about 1e-5".
    /// Measured here, at 20 °C, 1 atm, dry air with 450 µmol/mol CO2, they differ by 3·10⁻⁹ to
    /// 1.7·10⁻⁸ over 0.4–1.7 µm (TIE-19 lower). This pins that measurement so a change in either
    /// formula shows; it is not a published number. Humidity, which TIE-19's formula does not
    /// model, moves Ciddor by about 4·10⁻⁷ at 50 % and 20 °C.
    /// </summary>
    [Fact]
    public void Tie19AndCiddor_DifferByAFewPartsIn10To8_InDryAir()
    {
        var air = new AirConditions(20, AirIndex.StandardPressurePascals);
        foreach (double um in new[] { 0.4, 0.5875618, 0.6328, 1.0, 1.7 })
        {
            double d = AirIndex.Absolute(AirIndexModel.Schott, um, air) - AirIndex.Absolute(AirIndexModel.Ciddor, um, air);
            Assert.InRange(d, -2e-8, 0);
        }
        double humid = AirIndex.Absolute(AirIndexModel.Ciddor, 0.5875618, air with { RelativeHumidityPercent = 50 });
        Assert.InRange(AirIndex.Absolute(AirIndexModel.Ciddor, 0.5875618, air) - humid, 3e-7, 5e-7);
    }

    /// <summary>λvac = n(λvac)·λair is solved to the last bit or two, and the two conversions undo
    /// each other.</summary>
    [Fact]
    public void AirAndVacuumWavelengths_ConvertBothWays()
    {
        foreach (var model in AirIndexModels.Names.All)
        foreach (double air in new[] { 0.4861327, 0.5875618, 0.6562725, 1.0 })
        {
            var conditions = new AirConditions(25, 95000, 450, 30);
            double vacuum = AirIndex.VacuumFromAir(model, air, conditions);
            double n = AirIndex.Absolute(model, vacuum, conditions);
            Assert.True(vacuum > air);
            Assert.Equal(air * n, vacuum, 1e-15);
            Assert.Equal(air, AirIndex.AirFromVacuum(model, vacuum, conditions), 1e-15);
        }
    }

    /// <summary>Outside a model's stated range the index is refused, naming the range and the
    /// way out; TIE-19, which states none, refuses only at its poles.</summary>
    [Fact]
    public void OutsideItsRange_AModelRefuses_AndSaysWhy()
    {
        var room = new AirConditions(20, AirIndex.StandardPressurePascals);
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 2.0, room));
        Assert.Contains("0.3–1.7 µm", e.Message);
        Assert.Contains("TIE-19", e.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.ModifiedEdlen, 0.25, room));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 0.55, room with { TemperatureCelsius = -41 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 0.55, room with { PressurePascals = 5000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 0.55, room with { RelativeHumidityPercent = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 0.55, room with { CarbonDioxideMicromolesPerMole = 2500 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Ciddor, 0.55, room with { PressurePascals = -1 }));

        Assert.True(AirIndex.Absolute(AirIndexModel.Schott, 10.6, room) > 1);
        Assert.True(AirIndex.Absolute(AirIndexModel.Schott, 0.55, room with { TemperatureCelsius = -100, PressurePascals = 5000 }) > 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Schott, 0.15, room));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirIndex.Absolute(AirIndexModel.Schott, 0.55, room with { TemperatureCelsius = -273 }));
    }

    [Fact]
    public void TheModels_HaveSavedNames_AndCiddorIsTheDefault()
    {
        Assert.Equal(AirIndexModel.Ciddor, AirIndexModels.Default);
        Assert.Equal(new[] { "Ciddor", "SchottTie19", "ModifiedEdlen" }, AirIndexModels.Names.All.Select(AirIndexModels.Names.SavedName));
        var air = new AirConditions(20, AirIndex.StandardPressurePascals);
        Assert.Contains("Ciddor", AirIndexModels.Describe(AirIndexModel.Ciddor, air));
        Assert.Contains("450 µmol/mol", AirIndexModels.Describe(AirIndexModel.Ciddor, air));
        Assert.Contains("OpticStudio-compatible", AirIndexModels.Describe(AirIndexModel.Schott, air));
        Assert.Contains("Edlén", AirIndexModels.Describe(AirIndexModel.ModifiedEdlen, air));
    }
}
