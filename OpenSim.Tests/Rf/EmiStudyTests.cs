using OpenSim.Rf.Emi;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>FU-47: the EMI study the panel and the command line share is exactly the library chain it
/// replaced — converter, conducted estimate, radiated estimate — and its report carries the margins.
/// The command line's own outputs are held by EmiTests' job test.</summary>
public class EmiStudyTests
{
    private readonly ITestOutputHelper _out;
    public EmiStudyTests(ITestOutputHelper output) => _out = output;

    private static EmiStudyInput Buck() => new()
    {
        Vin = 12, Iout = 3, FrequencyHz = 500e3, Duty = 0.4, RiseSeconds = 10e-9, FallSeconds = 10e-9,
        SwitchNodeCapacitance = 20e-12, InputCapacitance = 10e-6, InputEsr = 5e-3, InputEsl = 1e-9,
        HotLoopInductance = 2e-9, HotLoopResistance = 20e-3, SwitchNodeToReturnCapacitance = 300e-12,
        ReverseRecoveryCharge = 20e-9, LoopArea = 1e-4, CableLength = 1, Distance = 3
    };

    [Fact]
    public void Study_IsTheLibraryChain()
    {
        var spec = Buck();
        var result = EmiStudy.Run(spec, EmiLimits.ConductedClassBQuasiPeak, EmiLimits.RadiatedClassB3m);
        var direct = ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 20e-12, 10e-6, 5e-3, 1e-9) with
        {
            HotLoopInductance = 2e-9, HotLoopResistance = 20e-3, SwitchNodeToReturnCapacitance = 300e-12,
            ReverseRecoveryCharge = 20e-9
        };
        var conducted = ConductedEmission.Estimate(direct, EmiLimits.ConductedClassBQuasiPeak);
        Assert.Equal(conducted.Lines, result.Conducted.Lines);
        var radiated = RadiatedEstimate.Converter(direct, 1e-4, 1, 3, EmiLimits.RadiatedClassB3m)
            .Select(r => new RadiatedLine(r.FrequencyHz, r.LoopDbuVm, r.CableDbuVm, r.LimitDbuVm)).ToList();
        Assert.Equal(radiated, result.Radiated!);
        Assert.Same(EmiLimits.RadiatedClassB3m, result.RadiatedLimit);
        Assert.True(result.OverLimit);
        _out.WriteLine(result.Conducted.Describe());
        _out.WriteLine("Radiated: " + result.StrongestRadiatedText);

        string report = result.Report("buck").ToMarkdown();
        Assert.Contains("Conducted margin", report);
        Assert.Contains("Radiated, strongest (comparative)", report);
        Assert.Contains("Ringing at", report);
        Assert.Contains(EmiStudy.RadiatedAssumption, report);
        Assert.StartsWith("frequency_Hz,loop_dBuV_m,cable_dBuV_m,limit_dBuV_m\n", result.RadiatedCsv());
    }

    [Fact]
    public void Study_WithoutLoopOrCable_HasNoRadiatedEstimate_AndRefusesMissingNumbers()
    {
        var bare = Buck() with { LoopArea = 0, CableLength = 0 };
        var result = EmiStudy.Run(bare, EmiLimits.ConductedClassBQuasiPeak, EmiLimits.RadiatedClassB3m);
        Assert.Null(result.Radiated);
        Assert.Null(result.RadiatedLimit);
        Assert.Null(result.StrongestRadiatedText);
        var refusal = Assert.Throws<ArgumentException>(() => EmiStudy.Run(new EmiStudyInput { Vin = 12 }, null));
        Assert.Contains("switchNodeCapacitance", refusal.Message);
    }

    [Fact]
    public void NamedLimits_AreTheBuiltInLines()
    {
        Assert.Same(EmiLimits.ConductedClassBQuasiPeak, EmiStudy.NamedLimit("ConductedClassBQuasiPeak"));
        Assert.Equal(EmiLimits.All.Count, EmiStudy.LimitNames.Count);
        foreach (string name in EmiStudy.LimitNames) Assert.Contains(EmiStudy.NamedLimit(name), EmiLimits.All);
        var unknown = Assert.Throws<ArgumentException>(() => EmiStudy.NamedLimit("ClassZ"));
        Assert.Contains("RadiatedClassB3m", unknown.Message);
    }
}
