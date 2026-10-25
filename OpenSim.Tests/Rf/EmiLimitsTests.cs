using OpenSim.Rf.Emi;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-19 (the FCC half): the <c>Fcc…</c> limit lines against the regulation's own text,
/// 47 CFR §§ 15.107 and 15.109 as published on eCFR (Title 47 as of 2026-10-19; the cells are
/// transcribed in <c>OpenSim.Tests/References/FU-19</c>). The radiated tables are in µV/m and
/// the lines hold 20·log₁₀ of them, not a rounded dB figure. The CISPR 32 lines are not gated
/// here — that standard is not on this machine — and must keep saying so.
/// </summary>
public class EmiLimitsTests
{
    private static double Db(double microvolts) => 20 * Math.Log10(microvolts);

    [Theory]
    [InlineData(30.0, 100.0)] [InlineData(60.0, 100.0)] [InlineData(100.0, 150.0)] [InlineData(215.9, 150.0)]
    [InlineData(300.0, 200.0)] [InlineData(959.0, 200.0)] [InlineData(961.0, 500.0)] [InlineData(1000.0, 500.0)]
    public void ClassB_RadiatedAt3m_IsTheTableInMicrovoltsPerMetre(double fMHz, double microvoltsPerMetre)
    {
        // § 15.109(a): 30–88 MHz 100, 88–216 MHz 150, 216–960 MHz 200, above 960 MHz 500 µV/m.
        Assert.Equal(Db(microvoltsPerMetre), EmiLimits.FccClassB3m.At(fMHz * 1e6)!.Value, 12);
    }

    [Theory]
    [InlineData(30.0, 90.0)] [InlineData(87.0, 90.0)] [InlineData(150.0, 150.0)] [InlineData(500.0, 210.0)] [InlineData(1000.0, 300.0)]
    public void ClassA_RadiatedAt10m_IsTheTableInMicrovoltsPerMetre(double fMHz, double microvoltsPerMetre)
    {
        // § 15.109(b): 30–88 MHz 90, 88–216 MHz 150, 216–960 MHz 210, above 960 MHz 300 µV/m.
        Assert.Equal(Db(microvoltsPerMetre), EmiLimits.FccClassA10m.At(fMHz * 1e6)!.Value, 12);
    }

    [Fact]
    public void RadiatedLines_RunFrom30MHz_ToTheQuasiPeakDetectorsEndAt1GHz()
    {
        foreach (var line in new[] { EmiLimits.FccClassB3m, EmiLimits.FccClassA10m })
        {
            Assert.Equal(30e6, line.MinFrequency);
            Assert.Equal(1000e6, line.MaxFrequency);            // § 15.35(a): quasi-peak to 1000 MHz; average above
            Assert.Null(line.At(29.9e6));
            Assert.Null(line.At(1000.1e6));
            Assert.Equal("dBµV/m", line.Unit);
        }
        // The dB figures these replace were rounded (43.5, 46, 54); the table's values are not.
        Assert.Equal(43.521825181113625, EmiLimits.FccClassB3m.At(100e6)!.Value, 12);
        Assert.Equal(46.020599913279625, EmiLimits.FccClassB3m.At(500e6)!.Value, 12);
        Assert.Equal(53.979400086720375, EmiLimits.FccClassB3m.At(999e6)!.Value, 12);
    }

    [Theory]
    [InlineData(88.0, 100.0, 90.0)] [InlineData(216.0, 150.0, 150.0)] [InlineData(960.0, 200.0, 210.0)]
    public void RadiatedBandEdges_TakeTheTighterLimit(double edgeMHz, double classB, double classA)
    {
        // § 15.109(c): "the tighter limit applies at the band edges" — for both classes.
        Assert.Equal(Db(classB), EmiLimits.FccClassB3m.At(edgeMHz * 1e6)!.Value, 12);
        Assert.Equal(Db(classA), EmiLimits.FccClassA10m.At(edgeMHz * 1e6)!.Value, 12);
    }

    [Theory]
    [InlineData(0.15, 66.0, 56.0)] [InlineData(0.5, 56.0, 46.0)] [InlineData(0.5001, 56.0, 46.0)]
    [InlineData(4.999, 56.0, 46.0)] [InlineData(5.0, 56.0, 46.0)] [InlineData(5.001, 60.0, 50.0)] [InlineData(30.0, 60.0, 50.0)]
    public void ClassB_Conducted_IsTheTable_WithTheLowerLimitAtTheBandEdges(double fMHz, double quasiPeak, double average)
    {
        // § 15.107(a): 0.15–0.5 MHz 66→56 / 56→46, 0.5–5 MHz 56 / 46, 5–30 MHz 60 / 50 dBµV;
        // "the lower limit applies at the band edges" (0.5 MHz: 56 either way; 5 MHz: 56, not 60).
        Assert.Equal(quasiPeak, EmiLimits.FccConductedClassBQuasiPeak.At(fMHz * 1e6)!.Value, 9);
        Assert.Equal(average, EmiLimits.FccConductedClassBAverage.At(fMHz * 1e6)!.Value, 9);
    }

    [Theory]
    [InlineData(0.15, 79.0, 66.0)] [InlineData(0.4999, 79.0, 66.0)] [InlineData(0.5, 73.0, 60.0)] [InlineData(30.0, 73.0, 60.0)]
    public void ClassA_Conducted_IsTheTable_WithTheLowerLimitAtTheBoundary(double fMHz, double quasiPeak, double average)
    {
        // § 15.107(b): 0.15–0.5 MHz 79 / 66, 0.5–30 MHz 73 / 60 dBµV; "the lower limit applies
        // at the boundary between the frequency ranges".
        Assert.Equal(quasiPeak, EmiLimits.FccConductedClassAQuasiPeak.At(fMHz * 1e6)!.Value, 9);
        Assert.Equal(average, EmiLimits.FccConductedClassAAverage.At(fMHz * 1e6)!.Value, 9);
    }

    [Fact]
    public void ClassB_Conducted_DecreasesWithTheLogarithmOfTheFrequency_Between150And500kHz()
    {
        // § 15.107(a), table note: "Decreases with the logarithm of the frequency" — so the
        // limit at the geometric mean of the band edges is the mean of the end values.
        double fGeometric = Math.Sqrt(0.15e6 * 0.5e6);
        Assert.Equal(61.0, EmiLimits.FccConductedClassBQuasiPeak.At(fGeometric)!.Value, 9);
        Assert.Equal(51.0, EmiLimits.FccConductedClassBAverage.At(fGeometric)!.Value, 9);
        // A quarter of the way in log f: 66 − 2.5.
        double fQuarter = 0.15e6 * Math.Pow(0.5 / 0.15, 0.25);
        Assert.Equal(63.5, EmiLimits.FccConductedClassBQuasiPeak.At(fQuarter)!.Value, 9);
        Assert.Equal(0.15e6, EmiLimits.FccConductedClassBQuasiPeak.MinFrequency);
        Assert.Equal(30e6, EmiLimits.FccConductedClassBQuasiPeak.MaxFrequency);
    }

    [Fact]
    public void FccLines_NameTheRegulation_AndTheCisprLinesStillSayUnverified()
    {
        var fcc = new[]
        {
            EmiLimits.FccConductedClassBQuasiPeak, EmiLimits.FccConductedClassBAverage,
            EmiLimits.FccConductedClassAQuasiPeak, EmiLimits.FccConductedClassAAverage,
            EmiLimits.FccClassB3m, EmiLimits.FccClassA10m
        };
        foreach (var line in fcc)
        {
            Assert.Equal(EmiLimits.FccSource, line.Source);
            Assert.DoesNotContain("not checked", line.Source);
            Assert.StartsWith("FCC § 15.10", line.Name);
            Assert.Contains(line, EmiLimits.All);
        }
        foreach (var line in EmiLimits.All.Except(fcc))
        {
            Assert.StartsWith("CISPR 32", line.Name);
            Assert.Contains(EmiLimits.Unverified, line.Source);
        }
        Assert.Equal(13, EmiLimits.All.Count);
        // The command line and the panel find every line by its property name.
        Assert.Same(EmiLimits.FccClassA10m, EmiStudy.NamedLimit("FccClassA10m"));
        Assert.Same(EmiLimits.FccConductedClassBAverage, EmiStudy.NamedLimit("FccConductedClassBAverage"));
    }

    [Fact]
    public void TheFccConductedTables_CarryTheSameNumbers_AsTheTypedCisprConductedLines()
    {
        // A finding, not a verification of CISPR 32: the CISPR 32 conducted lines typed from
        // common reproductions agree with the FCC regulation's cells one for one. They keep
        // their unverified label regardless.
        var pairs = new[]
        {
            (EmiLimits.FccConductedClassBQuasiPeak, EmiLimits.ConductedClassBQuasiPeak),
            (EmiLimits.FccConductedClassBAverage, EmiLimits.ConductedClassBAverage),
            (EmiLimits.FccConductedClassAQuasiPeak, EmiLimits.ConductedClassAQuasiPeak),
            (EmiLimits.FccConductedClassAAverage, EmiLimits.ConductedClassAAverage),
        };
        foreach (var (fcc, cispr) in pairs)
            foreach (double fMHz in new[] { 0.15, 0.2, 0.3, 0.4999, 0.5, 1, 4.999, 5, 5.001, 10, 30 })
                Assert.Equal(fcc.At(fMHz * 1e6)!.Value, cispr.At(fMHz * 1e6)!.Value, 12);
    }

    [Fact]
    public void TheStudysAssumptions_NameTheChosenLinesSource()
    {
        // The fixed assumption line used to call every limit line unverified; it now reads
        // the line compared against.
        var input = new ConverterInput(PiecewiseLinearWaveform.Trapezoid(3, 500e3, 0.4, 20e-9, 20e-9),
            PiecewiseLinearWaveform.Trapezoid(12, 500e3, 0.4, 20e-9, 20e-9), 100e-12, 10e-6, 0.02, 0);
        var fcc = ConductedEmission.AssumptionsFor(input, EmiLimits.FccConductedClassBQuasiPeak);
        Assert.Contains(fcc, a => a.StartsWith("Limit line 'FCC § 15.107(a) Class B conducted, quasi-peak': 47 CFR Part 15"));
        Assert.DoesNotContain(fcc, a => a.Contains("not checked"));
        var cispr = ConductedEmission.AssumptionsFor(input, EmiLimits.ConductedClassBQuasiPeak);
        Assert.Contains(cispr, a => a.StartsWith("Limit line 'CISPR 32 Class B conducted, quasi-peak': CISPR 32 typed as commonly reproduced"));
        Assert.DoesNotContain(ConductedEmission.AssumptionsFor(input), a => a.StartsWith("Limit line"));
    }

    [Fact]
    public void ALine_RoundTripsThroughItsCsv_WithItsSource()
    {
        var line = EmiLimits.FccClassA10m;
        var back = EmiLimitLine.ParseCsv(line.ToCsv(), line.Name, line.Unit, line.Source);
        Assert.Equal(line.Segments.Count, back.Segments.Count);
        foreach (double fMHz in new[] { 30, 50, 88, 100, 216, 500, 960, 1000 })
            Assert.Equal(line.At(fMHz * 1e6)!.Value, back.At(fMHz * 1e6)!.Value, 9);
        Assert.Equal(EmiLimits.FccSource, back.Source);
    }
}
