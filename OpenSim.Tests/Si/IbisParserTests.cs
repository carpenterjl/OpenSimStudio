using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// The IBIS parser gates (SI Stage S11a): a synthetic golden .ibs exercising every supported
/// keyword is parsed VERTEX-exact (scale suffixes, min/typ/max column order, NA → null),
/// corner selection falls back to typ, and a malformed table row is a loud typed failure.
/// </summary>
public class IbisParserTests
{
    // A complete driver model: Model_type, C_comp, the four V-I tables, [Ramp], both
    // waveforms, ranges/references. Deliberately mixes scale suffixes and an NA.
    private const string Golden = @"
[IBIS Ver]      2.1
[File Name]     golden.ibs
[Component]     ACME_DRV
[Model]         DRV33
Model_type      Output
C_comp          2.0pF    1.5pF    2.5pF     | die capacitance
[Voltage Range] 3.3      3.0      3.6
[Pullup Reference]   3.3
[Pulldown Reference] 0.0
[Pulldown]
|  Voltage    I(typ)      I(min)      I(max)
   0.0        0.0         0.0         0.0
   1.65       20.0m       17.0m       24.0m
   3.3        45.0m       38.0m       53.0m
[Pullup]
   0.0        0.0         0.0         0.0
   -1.65      -20.0m      -17.0m      -24.0m
   -3.3       -45.0m      -38.0m      NA
[GND Clamp]
   -1.0       -5.0m       -4.0m       -6.0m
    0.0        0.0         0.0         0.0
[POWER Clamp]
    0.0        0.0         0.0         0.0
    1.0       -5.0m       -4.0m       -6.0m
[Ramp]
dV/dt_r     1.65/0.5n    1.50/0.6n    1.80/0.4n
dV/dt_f     1.65/0.5n    1.50/0.6n    1.80/0.4n
[Rising Waveform]
R_fixture   50.0
V_fixture   0.0
   0.0        0.0         0.0         0.0
   0.5n       1.65        1.50        1.80
   1.0n       3.3         3.0         3.6
[Falling Waveform]
R_fixture   50.0
V_fixture   3.3
   0.0        3.3         3.0         3.6
   0.5n       1.65        1.50        1.80
   1.0n       0.0         0.0         0.0
[Pin]           1  A_signal  DRV33     | should be skipped with a warning
[End]
";

    private static IbisModel ParsedModel() => new IbisParser().Parse(Golden).Model("DRV33");

    [Fact]
    public void ParsesEveryKeyword_WithScaleSuffixesAndCorners()
    {
        var file = new IbisParser().Parse(Golden);
        Assert.Equal("ACME_DRV", file.Component);
        var m = file.Model("DRV33");
        Assert.Equal("Output", m.ModelType);
        Assert.True(m.IsOutput);

        // C_comp = 2.0 pF typ, with min/max — the pico suffix must scale exactly.
        Assert.Equal(2.0e-12, m.CComp.Typ!.Value, 15);
        Assert.Equal(1.5e-12, m.CComp.Min!.Value, 15);
        Assert.Equal(2.5e-12, m.CComp.Max!.Value, 15);

        // Voltage range + references.
        Assert.Equal(3.3, m.VoltageRange!.Value.Typ!.Value, 12);
        Assert.Equal(3.3, m.PullupRail, 12);
        Assert.Equal(0.0, m.PulldownReferenceVolts!.Value, 12);

        // Pulldown table: milli suffix, typ/min/max order.
        Assert.Equal(3, m.Pulldown.Count);
        Assert.Equal(3.3, m.Pulldown[2].VoltageVolts, 12);
        Assert.Equal(45.0e-3, m.Pulldown[2].CurrentAmps.Typ!.Value, 15);
        Assert.Equal(38.0e-3, m.Pulldown[2].CurrentAmps.Min!.Value, 15);
        Assert.Equal(53.0e-3, m.Pulldown[2].CurrentAmps.Max!.Value, 15);

        // NA in the last pullup max column → null.
        Assert.Null(m.Pullup[2].CurrentAmps.Max);
        Assert.Equal(-45.0e-3, m.Pullup[2].CurrentAmps.Typ!.Value, 15);

        // Clamps present.
        Assert.Equal(2, m.GndClamp.Count);
        Assert.Equal(2, m.PowerClamp.Count);

        // Ramp: 1.65 V / 0.5 ns typ ⇒ slew 3.3e9 V/s.
        Assert.Equal(1.65, m.Ramp!.Rising.DeltaVolts.Typ!.Value, 12);
        Assert.Equal(0.5e-9, m.Ramp.Rising.DeltaSeconds.Typ!.Value, 15);

        // Waveforms: fixtures + 3 rows each; the ns time suffix must scale.
        Assert.Single(m.RisingWaveforms);
        Assert.Equal(50.0, m.RisingWaveforms[0].RFixtureOhms, 12);
        Assert.Equal(0.0, m.RisingWaveforms[0].VFixtureVolts, 12);
        Assert.Equal(1.0e-9, m.RisingWaveforms[0].Rows[2].TimeSeconds, 15);
        Assert.Equal(3.3, m.RisingWaveforms[0].Rows[2].VoltageVolts.Typ!.Value, 12);
        Assert.Single(m.FallingWaveforms);
        Assert.Equal(3.3, m.FallingWaveforms[0].VFixtureVolts, 12);

        // The unsupported [Pin] keyword is skipped with a warning, not silently.
        Assert.Contains(file.Warnings, w => w.Contains("[Pin]"));
    }

    [Fact]
    public void CornerSelection_FallsBackToTypWhenNA()
    {
        var m = ParsedModel();
        var last = m.Pullup[2].CurrentAmps;
        Assert.Equal(last.Typ, last.At(IbisCornerSelection.Max));   // NA max → typ
        Assert.Equal(last.Min, last.At(IbisCornerSelection.Min));
        Assert.Equal(last.Typ, last.At(IbisCornerSelection.Typ));
    }

    [Theory]
    [InlineData("2.0p", 2.0e-12)]
    [InlineData("2.0pF", 2.0e-12)]
    [InlineData("5.0m", 5.0e-3)]
    [InlineData("1.5n", 1.5e-9)]
    [InlineData("3.0M", 3.0e6)]     // M = mega (case-sensitive)
    [InlineData("2.0k", 2.0e3)]
    [InlineData("50.0", 50.0)]
    [InlineData("50.0ohm", 50.0)]
    [InlineData("1.0e-3", 1.0e-3)]
    [InlineData("2.5f", 2.5e-15)]   // lowercase f = femto
    public void ScaleSuffixes_ParseExactly(string token, double expected)
    {
        double? v = IbisParser.ParseNumber(token, token);
        Assert.NotNull(v);
        Assert.True(Math.Abs(v!.Value - expected) <= 1e-12 * Math.Abs(expected),
            $"{token} → {v} vs {expected}");   // relative (femto is below 15-decimal abs precision)
    }

    [Fact]
    public void MalformedTableRow_IsATypedFailure()
    {
        const string bad = @"
[Component] X
[Model] M
Model_type Output
[Pulldown]
   0.0    0.0    0.0    0.0
   1.0    oops   0.0    0.0
[End]
";
        var ex = Assert.Throws<InvalidDataException>(() => new IbisParser().Parse(bad));
        Assert.Contains("oops", ex.Message);
    }

    [Fact]
    public void MissingModel_IsATypedFailure()
    {
        var file = new IbisParser().Parse(Golden);
        var ex = Assert.Throws<ArgumentException>(() => file.Model("NOPE"));
        Assert.Contains("DRV33", ex.Message);   // names the available choices
    }
}
