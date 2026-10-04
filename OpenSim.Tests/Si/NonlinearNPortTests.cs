using OpenSim.Core.Signals;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// Stage B1 — the N-port nonlinear transient: coupled lines with an arbitrary nonlinear element
/// at every port. This is what makes coupled-line IBIS and nonlinear receiver clamps possible;
/// the single-line engine could do neither, because it folds a LINEAR receiver admittance into
/// the channel reduction itself.
///
/// <para>The anchor gate is the same one that pinned the single-line engine, one dimension up:
/// an ALL-LINEAR run through the nonlinear machinery must reproduce
/// <see cref="TransientLink.SolvePeriodic"/>, which solves the linear network exactly per FFT
/// bin. Everything the new engine adds — the reference-terminated reduction, the matrix FIR,
/// the 2N-unknown Newton — has to cancel out to nothing on a linear problem, and a coupled
/// fixture makes the off-diagonal channel terms carry crosstalk through that comparison too.</para>
/// </summary>
public class NonlinearNPortTests
{
    private const double C0 = 299_792_458.0;
    private static readonly double AirL = 50.0 / C0;
    private static readonly double AirC = 1.0 / (50.0 * C0);

    private static RlgcResult SingleLine(double l, double c)
        => new(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
               new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());

    private static RlgcResult CoupledPair(double l, double lm, double c, double cm)
        => new(2, new[,] { { c, -cm }, { -cm, c } }, new double[2, 2],
               new[,] { { c, -cm }, { -cm, c } }, new[,] { { l, lm }, { lm, l } },
               new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, Array.Empty<string>());

    /// <summary>A periodic trapezoid, sampled — the same waveform the linear engine drives with,
    /// so both engines see identical excitation.</summary>
    private static double[] Trapezoid(int periodSamples, int samplesPerUi, bool[] bits,
        double amplitude, int edgeSamples)
    {
        var v = new double[periodSamples];
        for (int b = 0; b < bits.Length; b++)
        {
            int target = bits[b] ? 1 : 0;
            int prev = bits[(b - 1 + bits.Length) % bits.Length] ? 1 : 0;
            for (int s = 0; s < samplesPerUi; s++)
            {
                double frac = target;
                if (prev != target && s < edgeSamples)
                    frac = prev + (target - prev) * (s + 1.0) / edgeSamples;
                v[b * samplesPerUi + s] = amplitude * frac;
            }
        }
        return v;
    }

    [Fact]
    public void AllLinearSingleLine_ReproducesTheExactLinearTransient()
    {
        // The one-line case of the anchor. Same channel, same source, same terminations — the
        // nonlinear engine must land on the linear solver's answer, which is exact.
        const int spui = 32, periodSamples = 32 * 8;
        const double dt = 5e-12, rs = 50, rl = 75, amplitude = 1.0;
        var bits = new[] { true, false, true, true, false, false, true, false };
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.05) });
        var source = Trapezoid(periodSamples, spui, bits, amplitude, 6);

        var linear = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(rs, rl) }, new[] { (double[]?)source }, dt);

        var near = new INonlinearDriver[]
            { new LinearTheveninDriver(t => Sample(source, t, dt), rs) };
        var far = new INonlinearDriver[] { new LinearLoadElement(rl) };
        var nport = NonlinearLink.SolveNPort(network, near, far, periodSamples, dt);

        AssertClose(linear.FarVoltages[0], nport.FarVolts[0], amplitude, 0.01, "far");
        AssertClose(linear.NearVoltages[0], nport.NearVolts[0], amplitude, 0.01, "near");
    }

    [Fact]
    public void AllLinearCoupledPair_ReproducesTheExactLinearTransient_OnBothLines()
    {
        // THE anchor. Two coupled lines, one driven and one quiet: the victim's waveform is
        // pure crosstalk, carried entirely by the channel's off-diagonal terms. If the
        // reference-terminated reduction or the matrix FIR mixed up a port index, the aggressor
        // might still look right while the victim would not — so both are checked.
        const int spui = 32, periodSamples = 32 * 8;
        const double dt = 5e-12, rs = 50, rl = 50, amplitude = 1.0;
        var bits = new[] { true, false, true, true, false, false, true, false };
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.04) });
        var source = Trapezoid(periodSamples, spui, bits, amplitude, 6);

        var linear = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(rs, rl), new LineTermination(rs, rl) },
            new[] { (double[]?)source, null }, dt);

        var near = new INonlinearDriver[]
        {
            new LinearTheveninDriver(t => Sample(source, t, dt), rs),
            new LinearTheveninDriver(_ => 0.0, rs),          // quiet victim
        };
        var far = new INonlinearDriver[] { new LinearLoadElement(rl), new LinearLoadElement(rl) };
        var nport = NonlinearLink.SolveNPort(network, near, far, periodSamples, dt);

        AssertClose(linear.FarVoltages[0], nport.FarVolts[0], amplitude, 0.01, "aggressor far");
        AssertClose(linear.NearVoltages[0], nport.NearVolts[0], amplitude, 0.01, "aggressor near");
        // The victim carries only coupled energy, so it is compared against the AGGRESSOR's
        // swing — judging it against its own tiny peak would be a far harsher bound than the
        // aggressor gets, for no physical reason.
        AssertClose(linear.FarVoltages[1], nport.FarVolts[1], amplitude, 0.01, "victim far");
        AssertClose(linear.NearVoltages[1], nport.NearVolts[1], amplitude, 0.01, "victim near");

        // And the victim must actually be carrying something, or the gate proves nothing.
        Assert.True(nport.FarVolts[1].Max(Math.Abs) > 1e-3 * amplitude,
            "the victim shows no crosstalk at all — the fixture is not coupled");
    }

    // ------------------------------------------------------------------
    // What the engine exists for: nonlinear elements at BOTH ends, on coupled lines.
    // ------------------------------------------------------------------

    /// <summary>A STRONG buffer (≈10 Ω output) with protection clamps that conduct a diode drop
    /// outside the rails. The low output impedance is deliberate: a 50 Ω-output buffer driving a
    /// 50 Ω line is source-matched and cannot overshoot at all, so it would give the clamps
    /// nothing to act on. At 10 Ω the incident step is most of the rail and a high-impedance far
    /// end nearly doubles it.
    ///
    /// <para><b>Clamp signs.</b> The engine's convention is into-line current = −I_table, so a
    /// clamp only DISSIPATES if its table current carries the same sign as its excursion past
    /// the rail: GND clamp negative at negative V; POWER clamp positive when V is ABOVE Vcc —
    /// which on the IBIS "Vcc relative" axis (V_table = Vcc − V) is a NEGATIVE table voltage.
    /// The opposite sign makes the element inject current where it should sink it — a negative
    /// resistance with no stable operating point, which the node solve refuses rather than
    /// returning a number for. (That refusal is how this fixture's first draft was caught.)</para></summary>
    private const string ClampedIbis = @"
[IBIS Ver]      2.1
[File Name]     clamped.ibs
[Component]     ACME
[Model]         DRV
Model_type      Output
C_comp          0.0
[Voltage Range] 1.0      1.0      1.0
[Pullup Reference]   1.0
[Pulldown Reference] 0.0
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0       100.0m      100.0m      100.0m
[Pullup]                                 | Vtable = Vcc - Voutput
   0.0        0.0         0.0         0.0
   1.0      -100.0m     -100.0m     -100.0m
[GND Clamp]
  -0.7      -500.0m     -500.0m     -500.0m
  -0.3        0.0         0.0         0.0
   0.0        0.0         0.0         0.0
[POWER Clamp]                            | Vtable = Vcc - Voutput
   0.0        0.0         0.0         0.0
  -0.3        0.0         0.0         0.0
  -0.7       500.0m     500.0m     500.0m
[Ramp]
dV/dt_r     0.5/0.1n     0.5/0.1n     0.5/0.1n
dV/dt_f     0.5/0.1n     0.5/0.1n     0.5/0.1n
[End]
";

    [Fact]
    public void AClampedReceiver_LimitsTheOvershootAnUnclampedOneShows()
    {
        // The receiver clamps were parsed, gated by the parser tests, and applied at the DRIVER
        // only — so a receiver never clamped, and an overshoot a real part would clip was
        // reported at full height. Driving a badly-mismatched (open-ended) line makes the
        // reflection overshoot well past the rail; the clamped receiver must cut that peak,
        // and the unclamped one must not.
        const int spui = 32, periodSamples = 32 * 4;
        const double dt = 4e-12;
        var bits = new[] { true, false, true, false };
        var model = new IbisParser().Parse(ClampedIbis).Model("DRV");
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.06) });

        INonlinearDriver Driver() => IbisDriver.FromBits(
            model, IbisCornerSelection.Typ, bits, spui, dt);

        // A high-impedance far end: the reflection nearly doubles the incident step.
        var unclamped = NonlinearLink.SolveNPort(network,
            new[] { Driver() }, new INonlinearDriver[] { new LinearLoadElement(5000) },
            periodSamples, dt);
        var clamped = NonlinearLink.SolveNPort(network,
            new[] { Driver() },
            new INonlinearDriver[] { new IbisReceiverElement(model, IbisCornerSelection.Typ, 5000) },
            periodSamples, dt);

        double peakUnclamped = unclamped.FarVolts[0].Max();
        double peakClamped = clamped.FarVolts[0].Max();

        Assert.True(peakUnclamped > 1.05,
            $"the fixture does not overshoot the 1 V rail (peak {peakUnclamped:g4} V) — "
            + "the clamp has nothing to act on");
        Assert.True(peakClamped < peakUnclamped,
            $"the clamped receiver peak {peakClamped:g4} V is not below the unclamped "
            + $"{peakUnclamped:g4} V — the clamps are not conducting");
        // And it clips near the clamp's own knee rather than somewhere arbitrary.
        Assert.True(peakClamped < 1.7,
            $"clamped peak {peakClamped:g4} V is far past the POWER-clamp knee");
    }

    [Fact]
    public void ClampsThatNeverConduct_LeaveTheWaveformAlone()
    {
        // The complement, and the check that the clamp element is not simply loading the node:
        // into a MATCHED line nothing overshoots, so the clamped and unclamped receivers must
        // agree closely. A clamp that pulled current inside the rails would show here.
        const int spui = 32, periodSamples = 32 * 4;
        const double dt = 4e-12;
        var bits = new[] { true, false, true, false };
        var model = new IbisParser().Parse(ClampedIbis).Model("DRV");
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.06) });

        INonlinearDriver Driver() => IbisDriver.FromBits(
            model, IbisCornerSelection.Typ, bits, spui, dt);

        var plain = NonlinearLink.SolveNPort(network,
            new[] { Driver() }, new INonlinearDriver[] { new LinearLoadElement(50) },
            periodSamples, dt);
        var clamped = NonlinearLink.SolveNPort(network,
            new[] { Driver() },
            new INonlinearDriver[] { new IbisReceiverElement(model, IbisCornerSelection.Typ, 50) },
            periodSamples, dt);

        for (int i = 0; i < periodSamples; i++)
            Assert.True(Math.Abs(plain.FarVolts[0][i] - clamped.FarVolts[0][i]) < 1e-6,
                $"sample {i}: clamped {clamped.FarVolts[0][i]:g6} vs unclamped "
                + $"{plain.FarVolts[0][i]:g6} — the clamps conduct inside the rails");
    }

    [Fact]
    public void CoupledIbis_DrivesAnAggressorAndCouplesIntoAQuietVictim()
    {
        // Coupled-line IBIS: the case the single-line engine refused outright. A real IBIS
        // buffer drives line 0 while line 1 sits quiet behind its own buffer, and the victim
        // must pick up crosstalk — computed in the SAME nonlinear solve, so the victim's
        // clamps see the coupled energy rather than a linear superposition of it.
        // Two-UI holds: the fixture's ramp (0.1 ns for 20–80 %, so 0.167 ns for the whole
        // swing) is longer than its 0.128 ns unit interval, and since Fix 14 an edge is no
        // longer sped up to fit the bit — on 1010 the buffer never reaches its levels.
        const int spui = 32, periodSamples = 32 * 4;
        const double dt = 4e-12;
        var bits = new[] { true, true, false, false };
        var quiet = new[] { false, false, false, false };
        var model = new IbisParser().Parse(ClampedIbis).Model("DRV");
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 8e-8, 1.3e-10, 2.0e-11), 0.05) });

        var near = new INonlinearDriver[]
        {
            IbisDriver.FromBits(model, IbisCornerSelection.Typ, bits, spui, dt),
            IbisDriver.FromBits(model, IbisCornerSelection.Typ, quiet, spui, dt),
        };
        var far = new INonlinearDriver[]
        {
            new IbisReceiverElement(model, IbisCornerSelection.Typ, 50),
            new IbisReceiverElement(model, IbisCornerSelection.Typ, 50),
        };
        var result = NonlinearLink.SolveNPort(network, near, far, periodSamples, dt);

        double aggressorSwing = result.FarVolts[0].Max() - result.FarVolts[0].Min();
        double victimSwing = result.FarVolts[1].Max() - result.FarVolts[1].Min();
        Assert.True(aggressorSwing > 0.6,
            $"the aggressor barely switches (swing {aggressorSwing:g3} V)");
        Assert.True(victimSwing > 1e-3,
            $"the victim shows no crosstalk (swing {victimSwing:g3} V)");
        Assert.True(victimSwing < aggressorSwing,
            "the victim swings as much as the aggressor — the lines are not distinguishable");
    }

    [Fact]
    public void MismatchedElementCount_IsATypedFailure()
    {
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.04) });
        var one = new INonlinearDriver[] { new LinearLoadElement(50) };
        var e = Assert.Throws<ArgumentException>(() => NonlinearLink.SolveNPort(
            network, one, one, 64, 5e-12));
        Assert.Contains("2 conductor(s)", e.Message);
    }

    [Fact]
    public void AShortedLoadElement_IsATypedFailure()
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => new LinearLoadElement(0));
        Assert.Contains("positive", e.Message);
    }

    [Fact]
    public void SolveNPort_IsBitwiseIdenticalAtAnyDegreeOfParallelism()
    {
        // The channel FIR is built from independent per-bin solves into ordered slots (the
        // house recipe), so the thread count must not change a single bit of the result.
        const int spui = 16, periodSamples = 16 * 4;
        const double dt = 8e-12;
        var bits = new[] { true, false, true, false };
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.03) });
        var source = Trapezoid(periodSamples, spui, bits, 1.0, 4);

        INonlinearDriver[] Near() => new INonlinearDriver[]
        {
            new LinearTheveninDriver(t => Sample(source, t, dt), 50),
            new LinearTheveninDriver(_ => 0.0, 50),
        };
        INonlinearDriver[] Far() => new INonlinearDriver[]
            { new LinearLoadElement(50), new LinearLoadElement(50) };

        var serial = NonlinearLink.SolveNPort(network, Near(), Far(), periodSamples, dt,
            maxDegreeOfParallelism: 1);
        var parallel = NonlinearLink.SolveNPort(network, Near(), Far(), periodSamples, dt);

        for (int line = 0; line < 2; line++)
            for (int i = 0; i < periodSamples; i++)
            {
                Assert.Equal(serial.NearVolts[line][i], parallel.NearVolts[line][i]);
                Assert.Equal(serial.FarVolts[line][i], parallel.FarVolts[line][i]);
            }
    }

    private static double Sample(double[] periodic, double t, double dt)
    {
        int n = (int)Math.Round(t / dt);
        return periodic[((n % periodic.Length) + periodic.Length) % periodic.Length];
    }

    /// <summary>Compare against the exact linear answer as a fraction of the SWING, the same
    /// way the single-line identity gate is stated — an absolute-relative bound on a waveform
    /// that crosses zero would be meaningless near the crossings.</summary>
    private static void AssertClose(double[] expected, double[] actual, double swing,
        double fraction, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        double worst = 0;
        int worstAt = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            double d = Math.Abs(expected[i] - actual[i]);
            if (d > worst) { worst = d; worstAt = i; }
        }
        Assert.True(worst < fraction * swing,
            $"{what}: worst deviation {worst:e3} V at sample {worstAt} "
            + $"({worst / swing:P2} of swing) exceeds {fraction:P0}");
    }
}
