using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// Stage B2 — the IBIS values that were PARSED and then quietly not used. Each of these was a
/// silent substitution rather than a missing feature: a clamp evaluated against the wrong
/// supply, a falling edge run at the rising slew, a worst-case corner driven from the typical
/// rail. The gates are analytic wherever the substitution is decidable in closed form, and the
/// defaults are pinned to reproduce the previous behavior exactly, so files that never carried
/// the keywords are unchanged.
/// </summary>
public class IbisFidelityTests
{
    /// <summary>A split-rail part: the output stage pulls to 3.3 V but the POWER clamp diode
    /// returns to a 5.0 V rail, and the pull-down is referenced to a −0.5 V rail. Evaluating
    /// either table against the output supply puts its knee at the wrong voltage.</summary>
    private const string SplitRail = @"
[IBIS Ver]      2.1
[File Name]     split.ibs
[Component]     ACME_SPLIT
[Model]         DRV_SPLIT
Model_type      Output
C_comp          0.0
[Voltage Range] 3.3      3.0      3.6
[Pullup Reference]     3.3
[Pulldown Reference]  -0.5
[GND Clamp Reference] -0.5
[POWER Clamp Reference] 5.0
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        10.0m       10.0m       10.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0        -10.0m      -10.0m      -10.0m
[GND Clamp]
   -1.0       -8.0m       -8.0m       -8.0m
    0.0        0.0         0.0         0.0
[POWER Clamp]
    0.0        0.0         0.0         0.0
   -1.0       -7.0m       -7.0m       -7.0m
[Ramp]
dV/dt_r     1.65/0.5n    1.65/0.5n    1.65/0.5n
dV/dt_f     1.65/0.5n    1.65/0.5n    1.65/0.5n
[End]
";

    private static IbisModel Model(string text, string name) =>
        new IbisParser().Parse(text).Model(name);

    [Fact]
    public void EachTableIsReferencedToItsOwnRail()
    {
        var m = Model(SplitRail, "DRV_SPLIT");
        Assert.Equal(3.3, m.PullupRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(-0.5, m.PulldownRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(-0.5, m.GndClampRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(5.0, m.PowerClampRailAt(IbisCornerSelection.Typ), 12);
    }

    [Fact]
    public void MissingReferences_FallBackToThePreviousHardcodedRails()
    {
        // The back-compat pin: a file with no reference keywords must give exactly the rails
        // the engine used to hardcode — pull-down and GND clamp at ground, POWER clamp at the
        // pull-up rail — so its results do not move.
        const string bare = @"
[IBIS Ver]      2.1
[File Name]     bare.ibs
[Model]         B
Model_type      Output
C_comp          0.0
[Voltage Range] 2.5      2.5      2.5
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
    1.0      -1.0m       -1.0m       -1.0m
[End]
";
        var m = Model(bare, "B");
        foreach (var c in new[] { IbisCornerSelection.Typ, IbisCornerSelection.Min, IbisCornerSelection.Max })
        {
            Assert.Equal(0.0, m.PulldownRailAt(c), 15);
            Assert.Equal(0.0, m.GndClampRailAt(c), 15);
            Assert.Equal(2.5, m.PullupRailAt(c), 12);
            Assert.Equal(m.PullupRailAt(c), m.PowerClampRailAt(c), 12);
        }
    }

    [Fact]
    public void ThePullupRailFollowsTheRequestedCorner()
    {
        // [Voltage Range] 3.3 / 3.0 / 3.6 with no [Pullup Reference]: a Min run drives against
        // 3.0 V and a Max run against 3.6 V. Using Typ for both would report a typical-supply
        // swing for a worst-case corner — the precise thing a corner run exists to avoid.
        const string ranged = @"
[IBIS Ver]      2.1
[File Name]     ranged.ibs
[Model]         R
Model_type      Output
C_comp          0.0
[Voltage Range] 3.3      3.0      3.6
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[End]
";
        var m = Model(ranged, "R");
        Assert.Equal(3.3, m.PullupRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(3.0, m.PullupRailAt(IbisCornerSelection.Min), 12);
        Assert.Equal(3.6, m.PullupRailAt(IbisCornerSelection.Max), 12);
    }

    [Fact]
    public void AnExplicitPullupReferenceOverridesTheVoltageRange_AtEveryCorner()
    {
        var m = Model(SplitRail, "DRV_SPLIT");
        foreach (var c in new[] { IbisCornerSelection.Typ, IbisCornerSelection.Min,
                                  IbisCornerSelection.Max })
            Assert.Equal(3.3, m.PullupRailAt(c), 12);
    }

    [Theory]
    [InlineData("Output", true)]
    [InlineData("3-state", true)]
    [InlineData("I/O", true)]
    [InlineData("Open_drain", true)]
    [InlineData("Input", false)]
    [InlineData("Terminator", false)]
    [InlineData("Series", false)]
    [InlineData("", true)]              // never declared → not refused on a type check
    [InlineData("Something_New", true)] // unrecognized → allowed, not refused
    public void ModelTypeDecidesWhetherAModelMayDrive(string type, bool drivable)
    {
        var m = new IbisModel { Name = "X", ModelType = type };
        Assert.Equal(drivable, m.TypeIsDriverCapable);
    }

    [Fact]
    public void AnInputModelCarryingDriverTables_IsATypedFailure()
    {
        // IsOutput asks whether the TABLES are present; Model_type is the file's own
        // declaration. When they contradict each other, believing the tables would drive a
        // receiver model — so it refuses and says which two facts disagree.
        const string contradictory = @"
[IBIS Ver]      2.1
[File Name]     contradictory.ibs
[Model]         RX
Model_type      Input
C_comp          0.0
[Voltage Range] 3.3      3.3      3.3
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[End]
";
        var m = Model(contradictory, "RX");
        Assert.True(m.IsOutput);                 // the tables are there
        Assert.False(m.TypeIsDriverCapable);     // the declaration says otherwise
        var e = Assert.Throws<ArgumentException>(() => IbisDriver.FromBits(
            m, IbisCornerSelection.Typ, new[] { true, false }, 16, 1e-12));
        Assert.Contains("Model_type", e.Message);
        Assert.Contains("Input", e.Message);
    }

    [Fact]
    public void RisingAndFallingEdgesUseTheirOwnRampSlews()
    {
        // An asymmetric buffer: the falling edge is four times slower than the rising one.
        // [Ramp].Falling was parsed and never read, so both edges ran at the rising slew.
        // Ku is the fraction pulled up, so the schedule's edge lengths are directly readable.
        const string asymmetric = @"
[IBIS Ver]      2.1
[File Name]     asym.ibs
[Model]         A
Model_type      Output
C_comp          0.0
[Voltage Range] 1.0      1.0      1.0
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[Ramp]
dV/dt_r     1.0/1.0n     1.0/1.0n     1.0/1.0n
dV/dt_f     1.0/4.0n     1.0/4.0n     1.0/4.0n
[End]
";
        var m = Model(asymmetric, "A");
        const int spui = 64;
        const double dt = 0.125e-9;               // 8 ns UI
        var driver = IbisDriver.FromBits(
            m, IbisCornerSelection.Typ, new[] { true, false }, spui, dt);

        // Rising edge: 1 V at 1 V/ns → 1 ns → 8 samples. Falling: 4 ns → 32 samples.
        Assert.Equal(8, EdgeLength(driver, bit: 0, spui, dt));
        Assert.Equal(32, EdgeLength(driver, bit: 1, spui, dt));
    }

    /// <summary>Reads the Ku schedule back through the public evaluator: with linear tables and
    /// a known rail the into-line current is affine in Ku, so counting the samples before it
    /// settles measures the edge length without reaching into the driver's private state.</summary>
    private static int EdgeLength(IbisDriver driver, int bit, int spui, double dt)
    {
        double settled = driver.Evaluate(0.5, (bit * spui + spui - 1) * dt).Current;
        for (int s = 0; s < spui; s++)
        {
            double cur = driver.Evaluate(0.5, (bit * spui + s) * dt).Current;
            if (Math.Abs(cur - settled) < 1e-15) return s + 1;
        }
        return spui;
    }

    [Fact]
    public void SymmetricRamps_LeaveTheScheduleUnchanged()
    {
        // The back-compat pin for the falling-slew fix: when both edges declare the same slew —
        // as the golden file and most real parts do — every sample must be what it always was.
        const string symmetric = @"
[IBIS Ver]      2.1
[File Name]     sym.ibs
[Model]         S
Model_type      Output
C_comp          0.0
[Voltage Range] 1.0      1.0      1.0
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[Ramp]
dV/dt_r     1.0/2.0n     1.0/2.0n     1.0/2.0n
dV/dt_f     1.0/2.0n     1.0/2.0n     1.0/2.0n
[End]
";
        var m = Model(symmetric, "S");
        const int spui = 32;
        const double dt = 0.25e-9;
        var driver = IbisDriver.FromBits(
            m, IbisCornerSelection.Typ, new[] { true, false }, spui, dt);
        Assert.Equal(EdgeLength(driver, 0, spui, dt),
                     EdgeLength(driver, 1, spui, dt));
    }

    [Fact]
    public void UnmodelledSubParameters_AreWarnedOncePerKeyword()
    {
        // The file's own rule is warn-don't-misrender. Reactive fixture elements and [Ramp]
        // R_load were skipped in silence; they are now named — once each, because a waveform
        // block repeats them per table and a per-occurrence list would bury the real warnings.
        const string withExtras = @"
[IBIS Ver]      2.1
[File Name]     extras.ibs
[Model]         E
Model_type      Output
C_comp          0.0
[Voltage Range] 1.0      1.0      1.0
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[Ramp]
dV/dt_r     1.0/1.0n     1.0/1.0n     1.0/1.0n
dV/dt_f     1.0/1.0n     1.0/1.0n     1.0/1.0n
R_load      50.0
[Rising Waveform]
R_fixture   50.0
V_fixture   0.0
C_fixture   1.0p
L_fixture   1.0n
   0.0        0.0         0.0         0.0
   1.0n       1.0         1.0         1.0
[Falling Waveform]
R_fixture   50.0
V_fixture   1.0
C_fixture   1.0p
   0.0        1.0         1.0         1.0
   1.0n       0.0         0.0         0.0
[End]
";
        var file = new IbisParser().Parse(withExtras);
        Assert.Contains(file.Warnings, w => w.Contains("C_fixture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(file.Warnings, w => w.Contains("L_fixture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(file.Warnings, w => w.Contains("R_load", StringComparison.OrdinalIgnoreCase));
        // C_fixture appears in BOTH waveform blocks but must be named once.
        Assert.Equal(1, file.Warnings.Count(
            w => w.Contains("C_fixture", StringComparison.OrdinalIgnoreCase)));
        // And the resistive-fixture assumption is stated where it bites.
        Assert.Contains(file.Warnings, w => w.Contains("resistive fixture"));
    }
}
