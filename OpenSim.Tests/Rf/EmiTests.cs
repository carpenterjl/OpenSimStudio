using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Emi;
using OpenSim.Rf.Extraction;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>Feature 14: the conducted-emission estimate's parts against hand calculations.</summary>
public class EmiTests
{
    private readonly ITestOutputHelper _out;
    public EmiTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TrapezoidLines_AreTheDoubleSincEnvelope()
    {
        // Equal edges: |c_n| = A·d·|sinc(nπd)|·|sinc(nπ·tr/T)|, d the 50 % duty.
        const double a = 12, f = 500e3, d = 0.37, tr = 20e-9;
        var w = PiecewiseLinearWaveform.Trapezoid(a, f, d, tr, tr);
        static double Sinc(double x) => x == 0 ? 1 : Math.Sin(x) / x;
        Assert.Equal(a * d, w.Mean, 9);
        foreach (int n in new[] { 1, 2, 7, 60, 333, 999 })
        {
            double expected = a * d * Math.Abs(Sinc(n * Math.PI * d)) * Math.Abs(Sinc(n * Math.PI * tr * f));
            double got = w.Coefficient(n).Magnitude;
            Assert.True(Math.Abs(got - expected) <= 1e-9 * a, $"n {n}: {got} vs {expected}");
        }
    }

    private static Complex Par(Complex a, Complex b) => a * b / (a + b);

    private static Complex LisnImpedance(LisnModel m, double f)
    {
        var jw = new Complex(0, 2 * Math.PI * f);
        var mains = jw * m.SeriesInductance + 1 / (jw * m.MainsCapacitance) + m.MainsResistance;
        var receiver = 1 / (jw * m.CouplingCapacitance) + Par(m.ReceiverOhms, m.DischargeOhms);
        return Par(mains, receiver);
    }

    private static Complex ReceiverShare(LisnModel m, double f)
    {
        var jw = new Complex(0, 2 * Math.PI * f);
        var r = Par(m.ReceiverOhms, m.DischargeOhms);
        return r / (1 / (jw * m.CouplingCapacitance) + r);
    }

    [Theory]
    [InlineData(150e3)]
    [InlineData(2e6)]
    [InlineData(30e6)]
    public void Lisn_PresentsItsNetworkImpedance(double f)
    {
        var lisn = new LisnModel();
        var c = new AcCircuit();
        c.Current("I", "0", "eut", _ => 1);
        lisn.AddTo(c, "eut", "L");
        var s = c.Solve(f);
        var z = LisnImpedance(lisn, f);
        _out.WriteLine($"{f / 1e6} MHz: |Z| {s["eut"].Magnitude:F4} Ω vs {z.Magnitude:F4}");
        Assert.True((s["eut"] - z).Magnitude <= 1e-12 * z.Magnitude);
        Assert.True((s["L.rx"] - z * ReceiverShare(lisn, f)).Magnitude <= 1e-12 * z.Magnitude);
    }

    [Fact]
    public void DifferentialAndCommonMode_SplitAsTheHandCircuitsSay()
    {
        const double f = 1e6;
        var input = ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 20e-12, 10e-6, 5e-3, 1e-9);
        var circuit = ConductedEmission.Build(input, new LisnModel());
        var lisn = new LisnModel();
        var zL = LisnImpedance(lisn, f);
        var share = ReceiverShare(lisn, f);
        var jw = new Complex(0, 2 * Math.PI * f);
        var zCin = 5e-3 + jw * 1e-9 + 1 / (jw * 10e-6);

        // DM alone: 1 A from p to n through the source divides between C_in and the outside path
        // (line LISN, earth, then the neutral LISN in parallel with C_sw — the switch-node source,
        // switched off, ties the switch node to n).
        var dm = circuit.Solve(f, new Dictionary<string, Complex> { ["Idm"] = 1 });
        var zSwitch = 1 / (jw * 20e-12);
        var zNeutral = Par(zL, zSwitch);
        var iLisn = zCin / (zCin + zL + zNeutral);
        Assert.True((dm["L.rx"] - (-iLisn * zL * share)).Magnitude <= 1e-9 * (iLisn * zL * share).Magnitude,
            $"DM line {dm["L.rx"]} vs {-iLisn * zL * share}");
        Assert.True((dm["n"] - iLisn * zNeutral).Magnitude <= 1e-9 * (iLisn * zNeutral).Magnitude);

        // CM alone: 1 V on the switch node drives C_sw to earth; it returns through the neutral
        // LISN directly and through C_in then the line LISN.
        var cm = circuit.Solve(f, new Dictionary<string, Complex> { ["Vsw"] = 1 });
        var zSw = 1 / (jw * 20e-12);
        var zReturn = Par(zL, zCin + zL);
        var i = 1 / (zSw + zReturn);
        var vN = -i * zReturn;
        Assert.True((cm["n"] - vN).Magnitude <= 1e-9 * vN.Magnitude, $"CM neutral {cm["n"]} vs {vN}");
        Assert.True((cm["N.rx"] - vN * share).Magnitude <= 1e-9 * vN.Magnitude);
    }

    [Fact]
    public void LimitLines_SlopeInLogFrequency_AndTakeTheLowerValueAtASteps()
    {
        var qp = EmiLimits.ConductedClassBQuasiPeak;
        Assert.Equal(66, qp.At(150e3)!.Value, 9);
        Assert.Equal(66 - 10 * Math.Log10(2) / Math.Log10(0.5 / 0.15), qp.At(300e3)!.Value, 9);
        Assert.Equal(56, qp.At(5e6)!.Value, 9);
        Assert.Equal(60, qp.At(10e6)!.Value, 9);
        Assert.Null(qp.At(100e3));
        var back = EmiLimitLine.ParseCsv(qp.ToCsv(), qp.Name, qp.Unit, "round trip");
        Assert.Equal(qp.At(300e3)!.Value, back.At(300e3)!.Value, 9);
    }

    [Fact]
    public void RadiatedFormulas_HaveTheirTextbookCoefficients()
    {
        // Free space: loop 1.316e-14·f²·A·I/r, short cable 6.28e-7·f·ℓ·I/r.
        double loop = RadiatedEstimate.LoopField(100e6, 1e-4, 1e-3, 3, groundReflection: false);
        Assert.Equal(1.316e-14 * 1e16 * 1e-4 * 1e-3 / 3, loop, 1e-3 * loop);
        double cable = RadiatedEstimate.CableField(30e6, 0.5, 1e-6, 10, groundReflection: false);
        Assert.Equal(6.283e-7 * 30e6 * 0.5 * 1e-6 / 10, cable, 1e-3 * cable);
        // A cable longer than a quarter wave radiates as a quarter wave.
        Assert.Equal(RadiatedEstimate.CableField(300e6, 0.25, 1e-6, 10), RadiatedEstimate.CableField(300e6, 2, 1e-6, 10), 12);
    }

    [Fact]
    public void CommandLine_EmiJob_WritesTheReport_AndFlagsTheLimit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-emi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string job = Path.Combine(dir, "buck.json");
            File.WriteAllText(job, """
                { "vin": 12, "iout": 3, "frequencyHz": 5e5, "duty": 0.4, "riseSeconds": 1e-8, "fallSeconds": 1e-8,
                  "switchNodeCapacitance": 2e-11, "inputCapacitance": 1e-5, "inputEsr": 0.005, "inputEsl": 1e-9,
                  "loopArea": 1e-4, "cableLength": 1, "distance": 3,
                  "hotLoopInductance": 2e-8, "hotLoopResistance": 0.2, "switchNodeToReturnCapacitance": 5e-10, "reverseRecoveryCharge": 2e-8 }
                """);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int code = OpenSim.Cli.CliRunner.Run(new[] { "emi", job, "--out", Path.Combine(dir, "out") }, stdout, stderr);
            _out.WriteLine(stdout.ToString());
            Assert.Equal("", stderr.ToString());
            Assert.Equal(3, code);                                     // over the Class B line
            Assert.Contains("OVER the limit", stdout.ToString());
            Assert.True(File.Exists(Path.Combine(dir, "out", "conducted.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "out", "radiated.csv")));
            string report = File.ReadAllText(Path.Combine(dir, "out", "emi-report.md"));
            Assert.Contains("EMI pre-compliance", report);
            Assert.Contains("Ringing at", report);                      // the 50 MHz ring of 20 nH and 500 pF
            Assert.Contains("Diode recovery:", report);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void BuckConverter_FilterBuysItsAttenuation()
    {
        // A 500 kHz, 12 V → 3 A buck: the bare input fails Class B; a 1 µH / 10 µF filter
        // (corner 50 kHz) takes the differential lines down by at least 40 dB at 1 MHz and above.
        var bare = ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 20e-12, 10e-6, 5e-3, 1e-9);
        var filtered = bare with { FilterInductance = 1e-6, FilterResistance = 5e-3, FilterCapacitance = 10e-6, FilterCapacitorEsr = 5e-3 };
        var limit = EmiLimits.ConductedClassBQuasiPeak;
        var a = ConductedEmission.Estimate(bare, limit);
        var b = ConductedEmission.Estimate(filtered, limit);
        _out.WriteLine(a.Describe());
        _out.WriteLine(b.Describe());
        Assert.True(a.Worst!.MarginDb < 0, "the bare converter should fail");
        for (int k = 0; k < a.Lines.Count; k++)
            if (a.Lines[k].FrequencyHz >= 1e6 && a.Lines[k].DifferentialDbuV > 0)   // skip the duty cycle's nulls
                Assert.True(a.Lines[k].DifferentialDbuV - b.Lines[k].DifferentialDbuV >= 40,
                    $"{a.Lines[k].FrequencyHz}: {a.Lines[k].DifferentialDbuV} → {b.Lines[k].DifferentialDbuV}");
        Assert.Contains("frequency_Hz", a.ToCsv());

        var radiated = RadiatedEstimate.Converter(bare, 1e-4, 1.0, 3, EmiLimits.RadiatedClassB3m, 30e6, 300e6);
        Assert.NotEmpty(radiated);
        _out.WriteLine($"radiated at {radiated[0].FrequencyHz / 1e6:g4} MHz: loop {radiated[0].LoopDbuVm:F1}, cable {radiated[0].CableDbuVm:F1} dBµV/m");
    }

    // ------------------------------------------------------------------ FU-41: edges, paths, the board

    [Fact]
    public void ADampedRing_HasTheLinesOfItsPeriodicSum()
    {
        // 50 MHz ring, Q ≈ 10, repeated every 2 µs: the closed form against Simpson over the
        // periodic sum (every copy that has not decayed), and the composite is the sum of its parts.
        const double period = 2e-6, t0 = 0.3e-6, a = 7.5, alpha = 1.6e7, wd = 2 * Math.PI * 50e6, phi = 0.4;
        var ring = new DampedSinusoidTrain(period, t0, a, alpha, wd, phi);
        int copies = (int)Math.Ceiling(40 / (alpha * period)) + 1;        // e^{-40} of the first copy
        double Periodic(double t)
        {
            double sum = 0;
            for (int k = 0; k < copies; k++) sum += ring.Value(t + k * period);
            return sum;
        }
        // Simpson on each smooth piece: before t₀ only the earlier copies are there (the new copy
        // starts with a step of A·sin φ), from t₀ on all of them.
        double Earlier(double t)
        {
            double sum = 0;
            for (int k = 1; k < copies; k++) sum += ring.Value(t + k * period);
            return sum;
        }
        const int steps = 1 << 17;
        foreach (int n in new[] { 0, 1, 7, 99, 100, 101, 300 })
        {
            double w = 2 * Math.PI * n / period;
            Complex Simpson(Func<double, double> g, double from, double to)
            {
                double h = (to - from) / steps;
                Complex sum = Complex.Zero;
                for (int i = 0; i <= steps; i++)
                {
                    double t = from + i * h;
                    double weight = i == 0 || i == steps ? 1 : i % 2 == 1 ? 4 : 2;
                    sum += weight * g(t) * Complex.Exp(new Complex(0, -w * t));
                }
                return sum * h / 3;
            }
            var numeric = (Simpson(Earlier, 0, t0) + Simpson(Periodic, t0, period)) / period;
            var closed = ring.Coefficient(n);
            _out.WriteLine($"n {n}: closed {closed.Magnitude:e6}, Simpson {numeric.Magnitude:e6}");
            Assert.True((closed - numeric).Magnitude <= 1e-9 * a, $"n {n}: {closed} vs {numeric}");
        }
        var trapezoid = PiecewiseLinearWaveform.Trapezoid(12, 1 / period, 0.4, 10e-9, 10e-9);
        var both = new CompositeWaveform(new IPeriodicWaveform[] { trapezoid, ring });
        Assert.Equal(trapezoid.Coefficient(37) + ring.Coefficient(37), both.Coefficient(37));
        Assert.Throws<ArgumentException>(() => new CompositeWaveform(new IPeriodicWaveform[] { trapezoid, new DampedSinusoidTrain(1e-6, 0, 1, 1e6, 1e8) }));
    }

    [Fact]
    public void DiodeRecovery_SweepsItsChargeEveryCycle_AndRingsTheNodeAsTheEnergySays()
    {
        // 12 V → 3 A buck, 10 ns edges (di/dt 0.3 A/ns), 20 nC of recovery with equal slopes:
        // I_rr = √(Q·di/dt) = √6 A over 2·I_rr/(di/dt) = 16.3 ns, starting where the edge ends.
        const double f = 500e3, iOut = 3, rise = 10e-9, qrr = 20e-9;
        var bare = ConverterInput.Buck(12, iOut, f, 0.4, rise, rise, 20e-12, 10e-6, 5e-3, 1e-9);
        var input = bare with { ReverseRecoveryCharge = qrr };
        var recovery = input.Recovery()!;
        double didt = iOut / rise, peak = Math.Sqrt(2 * qrr * didt / 2);
        Assert.Equal(peak, recovery.PeakAmps, 1e-12 * peak);
        Assert.Equal(peak / didt, recovery.RiseSeconds, 1e-20);
        Assert.Equal(peak / didt, recovery.FallSeconds, 1e-20);
        Assert.Equal(rise, recovery.StartTime, 1e-20);
        Assert.Equal(qrr, recovery.ChargeCoulombs, 1e-9 * qrr);
        // The current's mean carries the charge once per cycle, exactly.
        var (current, _) = input.Sources();
        Assert.Equal(iOut * 0.4 + qrr * f, current.Coefficient(0).Real, 1e-9 * iOut);
        _out.WriteLine(recovery.Describe());

        // Snappy: the fall is a thousandth of the rise, the charge still Q_rr.
        var snappy = (input with { RecoverySoftness = 0 }).Recovery()!;
        Assert.Equal(Math.Sqrt(2 * qrr * didt), snappy.PeakAmps, 1e-12);
        Assert.Equal(qrr, snappy.ChargeCoulombs, 1e-6 * qrr);
        // A pulse that does not fit the period is refused.
        Assert.Throws<ArgumentException>(() => (input with { ReverseRecoveryCharge = 1e-3 }).Recovery());

        // The ring: 50 nH hot loop, 0.5 Ω, 2 nF on the node (Coss + copper): ω₀ = 1/√(LC), α = R/2L,
        // Z₀ = 5 Ω; the overshoot is the edge's V·|sinc(ω₀t_r/2)| and I_rr·Z₀ in quadrature.
        const double l = 50e-9, r = 0.5, c = 2e-9;
        var ringing = (input with { HotLoopInductance = l, HotLoopResistance = r, SwitchNodeToReturnCapacitance = c }).Ringing()!;
        double w0 = 1 / Math.Sqrt(l * c), alpha = r / (2 * l), wd = Math.Sqrt(w0 * w0 - alpha * alpha), z0 = Math.Sqrt(l / c);
        double x = 0.5 * w0 * rise, ramp = 12 * Math.Abs(Math.Sin(x) / x), fromRecovery = peak * z0;
        Assert.Equal(wd / (2 * Math.PI), ringing.FrequencyHz, 1e-9 * ringing.FrequencyHz);
        Assert.Equal(alpha, ringing.DecayPerSecond, 1e-9 * alpha);
        Assert.Equal(z0, ringing.CharacteristicOhms, 1e-12);
        Assert.Equal(ramp, ringing.RampOvershootVolts, 1e-12);
        Assert.Equal(fromRecovery, ringing.RecoveryOvershootVolts, 1e-12);
        Assert.Equal(Math.Sqrt(ramp * ramp + fromRecovery * fromRecovery), ringing.OvershootVolts, 1e-12);
        Assert.Equal(rise, ringing.StartTime, 1e-20);
        Assert.Equal(c * wd * ringing.OvershootVolts, ringing.LoopCurrent.Amplitude, 1e-12);
        _out.WriteLine(ringing.Describe());
        // Overdamped: no ring, and the notes say so.
        var damped = input with { HotLoopInductance = l, HotLoopResistance = 100, SwitchNodeToReturnCapacitance = c };
        Assert.Null(damped.Ringing());
        Assert.Contains(damped.SourceNotes(), s => s.Contains("overdamped"));
    }

    [Fact]
    public void Ringing_RaisesTheLinesAtTheLoopsResonance_AndLeavesTheLowOnesAlone()
    {
        // The same buck with a 15.9 MHz ring (50 nH, 2 nF, Q ≈ 10; an 11.5 V overshoot): the ring's
        // line at resonance (≈ V_os/(2αT) = 0.58 V) stands over the trapezoid's (0.14 V at n = 32),
        // so the strongest common-mode line moves to the ring and the line nearest it rises by more
        // than 6 dB; below 1 MHz the ring's lines are its 1/(ω_r T) share, under 0.1 dB.
        var bare = ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 20e-12, 10e-6, 5e-3, 1e-9);
        var ringing = bare with { HotLoopInductance = 50e-9, HotLoopResistance = 0.5, SwitchNodeToReturnCapacitance = 2e-9 };
        double fr = ringing.Ringing()!.FrequencyHz;
        var a = ConductedEmission.Estimate(bare, EmiLimits.ConductedClassBQuasiPeak);
        var b = ConductedEmission.Estimate(ringing, EmiLimits.ConductedClassBQuasiPeak);
        Assert.Equal(a.Lines.Count, b.Lines.Count);
        double maxLow = 0;
        for (int k = 0; k < a.Lines.Count; k++)
        {
            double f = a.Lines[k].FrequencyHz;
            double dCm = b.Lines[k].CommonDbuV - a.Lines[k].CommonDbuV, dDm = b.Lines[k].DifferentialDbuV - a.Lines[k].DifferentialDbuV;
            if (f < 1e6) maxLow = Math.Max(maxLow, Math.Max(Math.Abs(dCm), Math.Abs(dDm)));
            if (Math.Abs(f - fr) < 0.1 * fr) _out.WriteLine($"{f / 1e6:F2} MHz: CM {a.Lines[k].CommonDbuV:F1} → {b.Lines[k].CommonDbuV:F1} dBµV");
        }
        int nearest = Enumerable.Range(0, a.Lines.Count).MinBy(k => Math.Abs(a.Lines[k].FrequencyHz - fr));
        double rise = b.Lines[nearest].CommonDbuV - a.Lines[nearest].CommonDbuV;
        var strongest = b.Lines.MaxBy(l => l.CommonDbuV)!;
        _out.WriteLine($"ring at {fr / 1e6:F2} MHz: below 1 MHz the lines move by at most {maxLow:F4} dB; the nearest line rises {rise:F1} dB; strongest CM line at {strongest.FrequencyHz / 1e6:F1} MHz");
        _out.WriteLine(b.Describe());
        Assert.True(maxLow < 0.1, $"low lines moved {maxLow} dB");
        Assert.True(rise > 6, $"the line nearest the ring rose only {rise} dB");
        Assert.True(Math.Abs(strongest.FrequencyHz - fr) < 0.05 * fr);
        Assert.Contains(b.Assumptions, s => s.StartsWith("Ringing at"));
        // A ring without damping is refused: the resistance is what sets its lines.
        Assert.Throws<ArgumentException>(() => (ringing with { HotLoopResistance = 0 }).Ringing());
    }

    [Fact]
    public void TheInterwindingPath_IsTheHandCircuit()
    {
        // An isolated converter with no direct switch-node-to-earth path: 1 V on the switch node
        // drives C_iw into the secondary, which reaches earth through C_sec and comes back through
        // the LISNs — and, with a Y capacitor across the barrier, partly straight back to n.
        const double f = 2e6, ciw = 50e-12, csec = 100e-12, cy = 1e-9;
        var lisn = new LisnModel();
        var jw = new Complex(0, 2 * Math.PI * f);
        var zL = LisnImpedance(lisn, f);
        var share = ReceiverShare(lisn, f);
        var zCin = 5e-3 + jw * 1e-9 + 1 / (jw * 10e-6);
        var zReturn = Par(zL, zCin + zL);
        var isolated = ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 0, 10e-6, 5e-3, 1e-9) with
        {
            InterwindingCapacitance = ciw, SecondaryToEarthCapacitance = csec
        };
        var cm = ConductedEmission.Build(isolated, lisn).Solve(f, new Dictionary<string, Complex> { ["Vsw"] = 1 });
        var i = 1 / (1 / (jw * ciw) + 1 / (jw * csec) + zReturn);
        var vN = -i * zReturn;
        Assert.True((cm["n"] - vN).Magnitude <= 1e-9 * vN.Magnitude, $"neutral {cm["n"]} vs {vN}");
        Assert.True((cm["N.rx"] - vN * share).Magnitude <= 1e-9 * vN.Magnitude);
        // The cable's common-mode current is what the LISNs pass to earth: here all of i.
        // (positive from the LISNs into earth, so minus what the converter pushes in).
        var cable = ConductedEmission.CableCommonModeCurrent(cm, lisn);
        Assert.True((cable + i).Magnitude <= 1e-9 * i.Magnitude, $"cable {cable} vs {-i}");

        var withY = isolated with { SecondaryYCapacitance = cy };
        var cmY = ConductedEmission.Build(withY, lisn).Solve(f, new Dictionary<string, Complex> { ["Vsw"] = 1 });
        var zEarthWay = 1 / (jw * csec) + zReturn;
        var zSecToN = Par(zEarthWay, 1 / (jw * cy));
        var iY = 1 / (1 / (jw * ciw) + zSecToN);
        var iEarth = iY * (1 / (jw * cy)) / (1 / (jw * cy) + zEarthWay);
        var vNy = -iEarth * zReturn;
        Assert.True((cmY["n"] - vNy).Magnitude <= 1e-9 * vNy.Magnitude, $"with Y: {cmY["n"]} vs {vNy}");
        Assert.True(vNy.Magnitude < 0.2 * vN.Magnitude, "the Y capacitor should take most of the current back");
        Assert.Contains(ConductedEmission.AssumptionsFor(withY), s => s.Contains("interwinding"));

        // An interwinding capacitance with nowhere to go is refused.
        var ex = Assert.Throws<ArgumentException>(() => ConductedEmission.Build(isolated with { SecondaryToEarthCapacitance = 0 }, lisn));
        Assert.Contains("interwinding", ex.Message);
    }

    // ------------------------------------------------------------------ the board

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0 * 1e-3, y0 * 1e-3), new Point2(x1 * 1e-3, y0 * 1e-3), new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x0 * 1e-3, y1 * 1e-3) });

    /// <summary>A buck's input stage on two layers (mm): C1 at x = 10 (VIN pad at y 9.5, GND pad at
    /// y 10.5 with its via at y 11.3), Q1 at x 16 (VIN drain, SW source), Q2 at x 22 (SW drain, GND
    /// source with its via 0.8 mm right); VIN and SW traces 0.5 mm wide on L1 at y 9.5. The
    /// return is either a GND plane on L2 or a GND trace on L1 round the top (x 22.5 → y 11.3 → x 10).</summary>
    private static (PcbBoard Board, CopperNet Vin, CopperNet Gnd, CopperNet Sw, BoardStackup Stackup) BuckBoard(bool planeReturn)
    {
        var islands = new List<CopperIsland>();
        CopperIsland Add(int layer, Polygon2 shape)
        {
            var island = new CopperIsland(islands.Count, layer, $"L{layer}", shape);
            islands.Add(island);
            return island;
        }
        CopperPad Pad(string refDes, string pin, double x, double y) =>
            new(1, new Point2(x * 1e-3, y * 1e-3), Rect(x - 0.3, y - 0.3, x + 0.3, y + 0.3), 0.6e-3) { ComponentRef = refDes, Pin = pin };

        var vin = Add(1, Rect(9.7, 9.2, 15.8, 9.8));
        var sw = Add(1, Rect(16.2, 9.2, 21.8, 9.8));
        var gndIslands = new List<CopperIsland>();
        var vias = new List<Via>();
        var bridges = new List<ViaBridge>();
        var traces = new List<TraceCenterline>
        {
            new(1, new Point2(10e-3, 9.5e-3), new Point2(15.5e-3, 9.5e-3), 0.5e-3),
            new(1, new Point2(16.5e-3, 9.5e-3), new Point2(21.5e-3, 9.5e-3), 0.5e-3)
        };
        if (planeReturn)
        {
            gndIslands.Add(Add(1, Rect(9.7, 10.2, 10.3, 11.6)));            // C1's GND pad and via
            gndIslands.Add(Add(1, Rect(22.2, 9.2, 23.6, 9.8)));             // Q2's GND pad and via
            gndIslands.Add(Add(2, Rect(0, 0, 40, 20)));
            foreach (var (x, y) in new[] { (10.0, 11.3), (23.3, 9.5) })
            {
                var via = new Via(new Point2(x * 1e-3, y * 1e-3), 0.3e-3, true);
                vias.Add(via);
                bridges.Add(new ViaBridge(via, new[] { 1, 2 }));
            }
        }
        else
        {
            gndIslands.Add(Add(1, new Polygon2(new[]
            {
                new Point2(9.7e-3, 10.2e-3), new Point2(10.3e-3, 10.2e-3), new Point2(10.3e-3, 11.0e-3), new Point2(22.2e-3, 11.0e-3),
                new Point2(22.2e-3, 9.2e-3), new Point2(22.8e-3, 9.2e-3), new Point2(22.8e-3, 11.6e-3), new Point2(9.7e-3, 11.6e-3)
            })));
            traces.Add(new TraceCenterline(1, new Point2(22.5e-3, 9.5e-3), new Point2(22.5e-3, 11.3e-3), 0.6e-3));
            traces.Add(new TraceCenterline(1, new Point2(22.5e-3, 11.3e-3), new Point2(10e-3, 11.3e-3), 0.6e-3));
            traces.Add(new TraceCenterline(1, new Point2(10e-3, 11.3e-3), new Point2(10e-3, 10.5e-3), 0.6e-3));
        }
        var pads = new[]
        {
            Pad("C1", "1", 10, 9.5), Pad("C1", "2", 10, 10.5),
            Pad("Q1", "D", 15.5, 9.5), Pad("Q1", "S", 16.5, 9.5),
            Pad("Q2", "D", 21.5, 9.5), Pad("Q2", "S", 22.5, 9.5)
        };
        var vinNet = new CopperNet(1, new[] { vin }) { Name = "VIN" };
        var swNet = new CopperNet(2, new[] { sw }) { Name = "SW" };
        var gndNet = new CopperNet(3, gndIslands) { Name = "GND", StitchingVias = bridges };
        var board = new PcbBoard
        {
            Outline = new[] { Rect(0, 0, 40, 20) }, Islands = islands, Pads = pads, Vias = vias,
            Nets = new[] { vinNet, swNet, gndNet }, Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            TraceCenterlines = traces
        };
        var stackup = new BoardStackup
        {
            GapThickness = new Dictionary<int, double> { [1] = 0.2e-3 },
            GapPermittivity = new Dictionary<int, double> { [1] = 4.2 },
            GapLossTangent = new Dictionary<int, double> { [1] = 0.0 }
        };
        return (board, vinNet, gndNet, swNet, stackup);
    }

    private static readonly ConverterParts BuckParts = new("C1", "Q1", "Q2");

    [Fact]
    public void TheBoardGivesTheHotLoopOverItsPlane_AndTheSwitchNodesCapacitance()
    {
        var (board, vin, gnd, sw, stackup) = BuckBoard(planeReturn: true);
        // A coarse plane mesh (0.6 mm against a 0.22 mm height — the loop's L is over-stated and
        // the notes say so) keeps the two solves short; the identity below does not depend on it.
        var options = new ConverterBoardOptions { Stackup = stackup, FrequenciesHz = new[] { 1e7, 1e8 }, Loop = new LoopExtractionOptions { PlanePitchMeters = 0.6e-3, PlaneMarginMeters = 2e-3 } };
        var reading = ConverterBoard.Read(board, vin, gnd, sw, BuckParts, options);
        foreach (string line in reading.Describe()) _out.WriteLine(line);
        foreach (string note in reading.Notes) _out.WriteLine(note);
        Assert.True(reading.ReturnIsPlane);

        // The same loop built by hand: VIN trace, SW trace (Q1's body an ideal join), Q2's escape
        // bar and barrel down, the plane, C1's barrel up and escape bar. Copper 35 µm on L1 at
        // z 0.2525 mm (mid), the plane's top face at z 0.035.
        double zTop = 0.2e-3 + 35e-6 + 17.5e-6, planeTop = 35e-6;
        TraceSegment3D Bar(double x0, double y0, double x1, double y1, double w) =>
            new(new Vector3D(x0 * 1e-3, y0 * 1e-3, zTop), new Vector3D(x1 * 1e-3, y1 * 1e-3, zTop), w, 35e-6);
        TraceSegment3D Tube(double x, double y, double z0, double z1) =>
            new(new Vector3D(x * 1e-3, y * 1e-3, z0), new Vector3D(x * 1e-3, y * 1e-3, z1), 0.3e-3 + 25e-6, 25e-6, SegmentProfile.RoundTube);
        var outbound = new[]
        {
            Bar(10, 9.5, 15.5, 9.5, 0.5e-3), Bar(16.5, 9.5, 21.5, 9.5, 0.5e-3),
            Bar(22.5, 9.5, 23.3, 9.5, 0.6e-3), Tube(23.3, 9.5, zTop, planeTop)
        };
        var inbound = new[] { Tube(10, 11.3, planeTop, zTop), Bar(10, 11.3, 10, 10.5, 0.6e-3) };
        var byHand = LoopExtraction.LoopOverPlane(outbound, inbound, new[] { Rect(0, 0, 40, 20) }, (0, 35e-6), options.FrequenciesHz, options.Loop);
        Assert.Equal(byHand.Points[^1].Inductance(0, 0), reading.HotLoopInductance, 1e-12 * byHand.Points[^1].Inductance(0, 0));
        Assert.Equal(byHand.Points[^1].Resistance(0, 0), reading.HotLoopResistance, 1e-12 * byHand.Points[^1].Resistance(0, 0));
        Assert.Equal(LoopExtraction.AreaOverPlane(outbound.Concat(inbound).ToList(), (0, 35e-6)), reading.HotLoopAreaM2, 1e-18);
        // 12.1 mm of copper 0.2175 mm over the plane: a few nanohenries, an area of ℓ·h.
        Assert.InRange(reading.HotLoopInductance, 2e-9, 12e-9);
        Assert.Equal(12.1e-3 * (zTop - planeTop), reading.HotLoopAreaM2, 1e-12);
        Assert.Equal(12.1e-3 + 2 * (zTop - planeTop), reading.HotLoopLengthMeters, 1e-9);
        // The switch node's capacitance is the trace extraction's, and the input takes it all.
        var direct = TraceCapacitanceExtractor.Extract(board, sw, new BoardCoupledOptions { Stackup = stackup });
        Assert.Null(direct.FailureReason);
        Assert.Equal(direct.TotalFarads, reading.SwitchNodeToReturnCapacitance, 1e-9 * direct.TotalFarads);
        var input = reading.Apply(ConverterInput.Buck(12, 3, 500e3, 0.4, 10e-9, 10e-9, 20e-12, 10e-6, 5e-3, 1e-9) with
        {
            HotLoopInductance = 1e-9, HotLoopResistance = 20e-3, SwitchNodeToReturnCapacitance = 300e-12
        });
        Assert.Equal(1e-9 + reading.HotLoopInductance, input.HotLoopInductance, 1e-20);
        Assert.Equal(300e-12 + reading.SwitchNodeToReturnCapacitance, input.SwitchNodeToReturnCapacitance, 1e-24);
        Assert.Equal(reading.HotLoopAreaM2, input.HotLoopAreaM2);
        var ring = input.Ringing()!;
        Assert.Equal(1 / (2 * Math.PI * Math.Sqrt(input.HotLoopInductance * input.SwitchNodeToReturnCapacitance)), ring.FrequencyHz, 1e-3 * ring.FrequencyHz);
        _out.WriteLine(ring.Describe());
        // The radiated estimate takes the board's area when none is given.
        var radiated = RadiatedEstimate.Converter(input, 0, 1.0, 3, EmiLimits.RadiatedClassB3m, 30e6, 300e6);
        Assert.NotEmpty(radiated);

        // Refusals name the part and the net.
        var ex = Assert.Throws<InvalidOperationException>(() => ConverterBoard.Read(board, vin, gnd, sw, new ConverterParts("C9", "Q1", "Q2"), options));
        Assert.Contains("no pads of a part 'C9'", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => ConverterBoard.Read(board, vin, gnd, sw, new ConverterParts("C1", "Q2", "Q2"), options));
        Assert.Contains("'Q2' has no pad on VIN", ex.Message);
    }

    [Fact]
    public void TheBoardFollowsAReturnTrace_WhenThereIsNoPlane()
    {
        var (board, vin, gnd, sw, stackup) = BuckBoard(planeReturn: false);
        var reading = ConverterBoard.Read(board, vin, gnd, sw, BuckParts, new ConverterBoardOptions { Stackup = stackup, FrequenciesHz = new[] { 1e7, 1e8 } });
        foreach (string line in reading.Describe()) _out.WriteLine(line);
        Assert.False(reading.ReturnIsPlane);
        // One copper layer: the switch node has no reference plane, and the reading says so.
        Assert.NotNull(reading.SwitchNodeCapacitanceProblem);
        Assert.Equal(0, reading.SwitchNodeToReturnCapacitance);
        // The loop in plan view is the 12.5 × 1.8 mm rectangle between the traces at y 9.5 and 11.3.
        Assert.Equal(12.5e-3 * 1.8e-3, reading.HotLoopAreaM2, 1e-12);
        Assert.Equal(5.5e-3 + 5e-3 + 1.8e-3 + 12.5e-3 + 0.8e-3, reading.HotLoopLengthMeters, 1e-9);
        // A wide, open loop: more inductance than the one over the plane.
        var (planeBoard, planeVin, planeGnd, planeSw, _) = BuckBoard(planeReturn: true);
        var overPlane = ConverterBoard.Read(planeBoard, planeVin, planeGnd, planeSw, BuckParts,
            new ConverterBoardOptions { Stackup = stackup, FrequenciesHz = new[] { 1e7, 1e8 }, Loop = new LoopExtractionOptions { PlanePitchMeters = 0.6e-3, PlaneMarginMeters = 2e-3 } });
        _out.WriteLine($"over the plane {overPlane.HotLoopInductance * 1e9:F3} nH, round the trace {reading.HotLoopInductance * 1e9:F3} nH");
        Assert.True(reading.HotLoopInductance > 1.5 * overPlane.HotLoopInductance);
        Assert.Contains(reading.Notes, n => n.Contains("returning along GND's traces"));
    }
}
