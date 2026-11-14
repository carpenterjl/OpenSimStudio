using OpenSim.Optics;
using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-13: a glass's index at a temperature and pressure (SCHOTT TIE-19, "Temperature Coefficient
/// of the Refractive Index", Version August 2023, equations (2)–(5)). The plan's gate: at the
/// reference conditions the model returns the catalog index exactly; dn/dT by a finite difference
/// of the integrated form (3) equals the differential form (2) to 1e-9; a vacuum returns the
/// absolute index. TIE-19's own Table 1 is the independent check of the whole chain.
/// </summary>
public sealed class ThermalIndexTests
{
    /// <summary>
    /// Five SCHOTT glasses as the AGF catalog shipped with ZemaxGlass (MIT, N. Hagen) prints them:
    /// plans/sources/optics/zemaxglass/AGF_files/schott.agf, the NM, CD, TD and LD lines of N-BK7
    /// (line 744), SF57 (line 3481), N-PK51 (line 2009), F2 (line 3) and N-LAF2 (line 1210). All
    /// are formula 2 (Sellmeier 1) with reference temperature 20 °C.
    /// </summary>
    private static OpticalMaterial Glass(string name) => name switch
    {
        "N-BK7" => Make(name, new[] { 1.039612120E+00, 6.000698670E-03, 2.317923440E-01, 2.001791440E-02, 1.010469450E+00, 1.035606530E+02 },
            new ThermalData(1.860000E-06, 1.310000E-08, -1.370000E-11, 4.340000E-07, 6.270000E-10, 1.700000E-01, 2.000000E+01), 0.3),
        "SF57" => Make(name, new[] { 1.816513710E+00, 1.437041980E-02, 4.288936410E-01, 5.928011720E-02, 1.071862780E+00, 1.214199420E+02 },
            new ThermalData(7.260000E-06, 1.880000E-08, -5.140000E-11, 1.960000E-06, 1.790000E-09, 2.760000E-01, 2.000000E+01), 0.37),
        "N-PK51" => Make(name, new[] { 1.156107750E+00, 5.855974020E-03, 1.532293440E-01, 1.940724160E-02, 7.856189660E-01, 1.405370460E+02 },
            new ThermalData(-1.980000E-05, -6.060000E-09, 1.600000E-11, 4.160000E-07, 5.010000E-10, 1.340000E-01, 2.000000E+01), 0.3),
        "F2" => Make(name, new[] { 1.345333590E+00, 9.977438710E-03, 2.090731760E-01, 4.704507670E-02, 9.373571620E-01, 1.118867640E+02 },
            new ThermalData(1.510000E-06, 1.560000E-08, -2.780000E-11, 9.340000E-07, 1.040000E-09, 2.500000E-01, 2.000000E+01), 0.32),
        "N-LAF2" => Make(name, new[] { 1.809842270E+00, 1.017116220E-02, 1.572955500E-01, 4.424317650E-02, 1.093003700E+00, 1.006877480E+02 },
            new ThermalData(-3.640000E-06, 9.200000E-09, -6.000000E-12, 6.430000E-07, 6.110000E-10, 2.200000E-01, 2.000000E+01), 0.35),
        _ => throw new ArgumentException(name)
    };

    private static OpticalMaterial Make(string name, double[] cd, ThermalData td, double ldMin) => new()
    {
        Name = name, Catalog = "schott", FormulaNumber = 2, Coefficients = cd, Thermal = td, MinMicrometers = ldMin, MaxMicrometers = 2.5
    };

    private static readonly string[] Names = { "N-BK7", "SF57", "N-PK51", "F2", "N-LAF2" };

    private static readonly double[] Lines =
    {
        SpectralLines.G.Micrometers, SpectralLines.F.Micrometers, SpectralLines.E.Micrometers,
        SpectralLines.LowerD.Micrometers, SpectralLines.C.Micrometers, SpectralLines.NdGlassLaser.Micrometers
    };

    private static AirConditions Room(double celsius = 20, double pascals = AirIndex.StandardPressurePascals) => new(celsius, pascals);

    /// <summary>Gate 1: at the glass's reference temperature and the catalog pressure the model
    /// returns the catalog index bit for bit, in every air model, with any CO2 and humidity
    /// (the reference air is given the system's composition).</summary>
    [Fact]
    public void AtTheReferenceConditions_TheCatalogIndexComesBackExactly()
    {
        foreach (var name in Names)
        foreach (var model in AirIndexModels.Names.All)
        foreach (double um in Lines)
        {
            var glass = Glass(name);
            var air = new AirConditions(20, AirIndex.StandardPressurePascals, 420, 40);
            var r = ThermalIndex.AtAirWavelength(glass, um, model, air);
            Assert.Equal(glass.RelativeIndex(um), r.Relative);
            Assert.Equal(um, r.FormulaMicrometers);
            Assert.Equal(r.SystemAirIndex, r.ReferenceAirIndex);

            var environment = new OpticsEnvironment { AirModel = model, AdjustIndexToEnvironment = true, CarbonDioxideMicromolesPerMole = 420, RelativeHumidityPercent = 40 };
            Assert.Equal(glass.RelativeIndex(um), environment.GlassIndex(glass, um));
        }
    }

    /// <summary>
    /// Gate 2: a central difference of equation (3) equals equation (2) to 1e-9 of its value.
    /// Equation (3) is a cubic in ΔT, so the difference's truncation error is exactly
    /// D2·(n² − 1)/(2n)·h², 2·10⁻¹⁶ at h = 10⁻³ K for these glasses, about 10⁻¹⁰ of dn/dT; the
    /// rounding of Δn (about 10⁻⁴ in size) over 2h adds about 10⁻¹⁷.
    /// </summary>
    [Fact]
    public void DnDT_ByDifferenceOfTheIntegratedForm_EqualsTheDifferentialForm()
    {
        const double h = 1e-3;
        foreach (var name in Names)
        foreach (double um in Lines)
        foreach (double deltaT in new[] { -120.0, -40, -5, 0, 7.5, 20, 60, 120 })
        {
            var glass = Glass(name);
            var td = glass.Thermal!;
            double n = glass.RelativeIndex(um);
            double fd = (ThermalIndex.AbsoluteIndexChange(td, n, um, deltaT + h) - ThermalIndex.AbsoluteIndexChange(td, n, um, deltaT - h)) / (2 * h);
            double eq2 = ThermalIndex.AbsoluteIndexTemperatureCoefficient(td, n, um, deltaT);
            Assert.True(Math.Abs(fd - eq2) <= 1e-9 * Math.Abs(eq2), $"{name} {um} µm ΔT {deltaT}: (2) {eq2:E12}, difference of (3) {fd:E12}");
        }
    }

    /// <summary>
    /// The same through the whole chain: the absolute index at a vacuum wavelength depends on the
    /// system temperature only through Δn_abs (the catalog index and the reference air do not
    /// move), so its central difference is equation (2) up to the cubic's truncation
    /// |D2|·(n² − 1)/(2n)·h² and the rounding of an index near 1.5 over 2h, about 4·eps·n/h.
    /// </summary>
    [Fact]
    public void TheAbsoluteIndex_MovesWithTemperatureByEquation2()
    {
        const double h = 0.5;
        foreach (var name in Names)
        foreach (double t in new[] { -30.0, 20, 45 })
        {
            var glass = Glass(name);
            double um = SpectralLines.E.Micrometers;
            double Abs(double c) => ThermalIndex.AtVacuumWavelength(glass, um, AirIndexModel.Schott, Room(c)).Absolute;
            var r = ThermalIndex.AtVacuumWavelength(glass, um, AirIndexModel.Schott, Room(t));
            double fd = (Abs(t + h) - Abs(t - h)) / (2 * h);
            double eq2 = ThermalIndex.AbsoluteIndexTemperatureCoefficient(glass.Thermal!, r.Catalog, um, t - 20);
            double k = (r.Catalog * r.Catalog - 1) / (2 * r.Catalog);
            double bound = Math.Abs(glass.Thermal!.D2) * k * h * h + 4 * 2.2e-16 * r.Absolute / h;
            Assert.True(Math.Abs(fd - eq2) <= bound, $"{name} {t} °C: {fd:E10} vs {eq2:E10} (bound {bound:E2})");
        }
    }

    /// <summary>Gate 3: in a vacuum the index relative to the system's "air" is the absolute
    /// index, bit for bit, and it is the catalog index times the reference air's index.</summary>
    [Fact]
    public void InAVacuum_TheResultIsTheAbsoluteIndex()
    {
        foreach (var name in Names)
        foreach (var model in AirIndexModels.Names.All)
        foreach (double t in new[] { 20.0, -30 })
        {
            var glass = Glass(name);
            var r = ThermalIndex.AtAirWavelength(glass, SpectralLines.LowerD.Micrometers, model, Room(t, 0));
            Assert.Equal(1.0, r.SystemAirIndex);
            Assert.Equal(SpectralLines.LowerD.Micrometers, r.VacuumMicrometers);
            Assert.Equal(r.Absolute, r.Relative);
            if (t != 20) continue;
            Assert.Equal(r.Catalog * r.ReferenceAirIndex, r.Absolute);   // no Δn at the reference temperature
            Assert.True(r.Absolute > r.Catalog);                         // relative to air, not to vacuum
        }
    }

    /// <summary>
    /// The whole chain against TIE-19's Table 1, "relative temperature coefficients of refractive
    /// index [+20/+40 deg. °C]" at the e-line, in 10⁻⁶/K: N-BK7 3.00, SF57 12.50, N-PK51 −6.70,
    /// F2 4.40, N-LAF2 1.00 (pages 3–5). Computed as (n_rel(40 °C) − n_rel(20 °C))/20 K at 1 atm
    /// with the TIE-19 air. When this test was written the differences were 0.03, 0.15, 0.003,
    /// 0.025 and 0.086 (×10⁻⁶/K). The table prints its values to 0.1 (every entry ends in 0), and
    /// the AGF file's TD constants may be from an older data sheet than the August 2023 table;
    /// that is not verifiable from the sources on disk, so the tolerance is 0.2·10⁻⁶/K: twice the
    /// table's resolution. A sign or factor error in Δn (3) or in the air terms moves these by
    /// whole units.
    /// </summary>
    [Theory]
    [InlineData("N-BK7", 3.00)]
    [InlineData("SF57", 12.50)]
    [InlineData("N-PK51", -6.70)]
    [InlineData("F2", 4.40)]
    [InlineData("N-LAF2", 1.00)]
    public void RelativeCoefficient_20To40C_MatchesTie19Table1(string name, double perKelvinE6)
    {
        var glass = Glass(name);
        double e = SpectralLines.E.Micrometers;
        double n20 = ThermalIndex.AtAirWavelength(glass, e, AirIndexModel.Schott, Room(20)).Relative;
        double n40 = ThermalIndex.AtAirWavelength(glass, e, AirIndexModel.Schott, Room(40)).Relative;
        Assert.Equal(perKelvinE6, (n40 - n20) / 20 * 1e6, 0.2);
    }

    /// <summary>A glass without a TD line has no dn/dT: its absolute index stays put and only the
    /// air moves its relative index. Its reference is TIE-19's T0, 20 °C.</summary>
    [Fact]
    public void WithoutATdLine_OnlyTheAirMoves()
    {
        var glass = Glass("N-BK7") with { Thermal = null };
        double d = SpectralLines.LowerD.Micrometers;
        var at20 = ThermalIndex.AtVacuumWavelength(glass, d, AirIndexModel.Ciddor, Room(20));
        var at40 = ThermalIndex.AtVacuumWavelength(glass, d, AirIndexModel.Ciddor, Room(40));
        Assert.False(at40.HasThermalData);
        Assert.Equal(20, at40.ReferenceTemperatureCelsius);
        Assert.Equal(at20.Absolute, at40.Absolute);
        Assert.True(at40.Relative > at20.Relative);                     // warmer air has a lower index
        Assert.Equal(at20.Absolute / at40.SystemAirIndex, at40.Relative, 1e-15);
    }

    /// <summary>
    /// TIE-19 writes λ² − λtk² (equations (2), (3)), as ZemaxGlass.py does; AGFFileReader.jl
    /// writes λ² − S·λtk² with S the sign of λtk (utilities.jl, <c>index</c>). This follows
    /// TIE-19, so the sign of λtk does not matter (docs/Optics-Conventions.md §10).
    /// </summary>
    [Fact]
    public void TheSignOfLambdaTk_DoesNotMatter_AsTie19WritesIt()
    {
        var td = Glass("N-BK7").Thermal!;
        var negative = td with { LambdaTk = -td.LambdaTk };
        Assert.Equal(ThermalIndex.AbsoluteIndexChange(td, 1.5, 0.5, 25), ThermalIndex.AbsoluteIndexChange(negative, 1.5, 0.5, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThermalIndex.AbsoluteIndexChange(td, 1.5, td.LambdaTk, 25));
    }

    /// <summary>The setting off gives the catalog index whatever the environment (OP-11's
    /// behaviour); on, the environment moves it.</summary>
    [Fact]
    public void AdjustOff_GivesTheCatalogIndex_AdjustOn_TheModel()
    {
        var glass = Glass("SF57");
        double d = SpectralLines.LowerD.Micrometers;
        var hot = new OpticsEnvironment { TemperatureCelsius = 60, PressureAtmospheres = 0.8 };
        Assert.Equal(glass.RelativeIndex(d), hot.GlassIndex(glass, d));
        var adjusted = hot with { AdjustIndexToEnvironment = true };
        Assert.Equal(ThermalIndex.AtAirWavelength(glass, d, AirIndexModel.Ciddor, adjusted.Air).Relative, adjusted.GlassIndex(glass, d));
        Assert.True(adjusted.GlassIndex(glass, d) > glass.RelativeIndex(d));     // SF57's dn/dT is positive
    }

    /// <summary>The reference air is the catalog's: 20 °C (the TD line) and 101325 Pa (TIE-19's
    /// P0), whatever the system's.</summary>
    [Fact]
    public void TheReferenceAir_IsTheCatalogs()
    {
        var reference = ThermalIndex.ReferenceAir(Glass("F2"), new AirConditions(55, 70000, 500, 10));
        Assert.Equal(new AirConditions(20, 101325, 500, 10), reference);
        Assert.Equal(101325, ThermalIndex.CatalogReferencePressurePascals);
    }
}
