using System.Numerics;
using OpenSim.Rf.Emi;
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
                  "loopArea": 1e-4, "cableLength": 1, "distance": 3 }
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
            Assert.Contains("EMI pre-compliance", File.ReadAllText(Path.Combine(dir, "out", "emi-report.md")));
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
}
