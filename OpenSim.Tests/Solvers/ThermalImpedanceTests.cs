using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Feature 10: pulsed power in the transient solve, thermal impedance curves, and Foster
/// and Cauer networks. The references are the lumped body (one R, one C), the
/// semi-infinite body (Z_th ∝ √t) and ladders worked by hand.
/// </summary>
public class ThermalImpedanceTests
{
    private readonly ITestOutputHelper _out;
    public ThermalImpedanceTests(ITestOutputHelper output) => _out = output;

    private const double K = 400, Density = 8960, SpecificHeat = 385;

    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = Density,
        ThermalConductivity = K, SpecificHeat = SpecificHeat, Emissivity = 0.9
    };

    private static readonly int[] AllFaces =
    {
        StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceXMax, StructuredBoxMesh.FaceYMin,
        StructuredBoxMesh.FaceYMax, StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceZMax
    };

    // ---------------- Power profiles ----------------

    [Fact]
    public void PowerProfile_MeanOverAnInterval_IsExact()
    {
        var pulse = PowerProfile.Pulse(period: 1, dutyCycle: 0.3, onScale: 2, offScale: 0.5, delay: 0.2);
        pulse.Validate();
        Assert.Equal(0.5, pulse.At(0.1));
        Assert.Equal(2, pulse.At(0.3));
        Assert.Equal(0.5, pulse.At(0.6));
        Assert.Equal(2, pulse.At(1.25));
        Assert.Equal(0.5, pulse.Mean(0, 0.2), 12);
        Assert.Equal(2, pulse.Mean(0.2, 0.5), 12);
        Assert.Equal(0.875, pulse.Mean(0, 1.2), 12);
        Assert.Equal(0.95, pulse.Mean(0.2, 10.2), 12);
        Assert.Equal(0.3, pulse.ShortestInterval(), 12);

        var table = PowerProfile.Table(new[] { 0.0, 1, 2 }, new[] { 0.0, 2, 2 });
        table.Validate();
        Assert.Equal(1, table.At(0.5), 12);
        Assert.Equal(1, table.Mean(0, 1), 12);
        Assert.Equal(1.75, table.Mean(0.5, 1.5), 12);
        Assert.Equal(2, table.Mean(2, 5), 12);
        Assert.Equal(2, table.At(7));

        Assert.Equal(1, new PowerProfile().Mean(3, 4));
        Assert.Throws<InvalidOperationException>(() => PowerProfile.Pulse(0, 0.5).Validate());
        Assert.Throws<InvalidOperationException>(() => PowerProfile.Pulse(1, 1.5).Validate());
        Assert.Throws<InvalidOperationException>(() => PowerProfile.Table(new[] { 0.0, 0 }, new[] { 1.0, 2 }).Validate());
    }

    // ---------------- The lumped body ----------------

    private const double Side = 5e-3, FilmCoefficient = 50;
    private static double CubeResistance => 1 / (FilmCoefficient * 6 * Side * Side);
    private static double CubeCapacity => Density * SpecificHeat * Side * Side * Side;

    private static SolveInput Cube(bool cooled, double watts = 1.0)
    {
        var mesh = StructuredBoxMesh.Build(0, Side, 0, Side, 0, Side, 4, 4, 4);
        var conditions = new List<BoundaryCondition>();
        if (cooled)
            conditions.Add(new Convection { Name = "air", FaceIds = AllFaces, Coefficient = FilmCoefficient, AmbientTemperature = 300 });
        return new SolveInput
        {
            Mesh = mesh, Material = Copper, BoundaryConditions = conditions,
            ElementHeatSource = Enumerable.Repeat(watts / (Side * Side * Side), mesh.ElementCount).ToArray()
        };
    }

    [Fact]
    public void LumpedBody_ImpedanceCurve_IsOneRcStage()
    {
        var result = ThermalImpedanceSolver.Solve(Cube(cooled: true),
            new[] { new ThermalProbe { Name = "centre", Position = new Vector3D(Side / 2, Side / 2, Side / 2) } },
            new ThermalImpedanceSettings { StartTime = 0.1, EndTime = 600, PointsPerDecade = 10 });
        var curve = result.Curves.Single();
        double r = CubeResistance, tau = r * CubeCapacity, worst = 0;
        for (int i = 0; i < curve.TimesSeconds.Count; i++)
        {
            double exact = r * (1 - Math.Exp(-curve.TimesSeconds[i] / tau));
            worst = Math.Max(worst, Math.Abs(curve.KelvinPerWatt[i] / exact - 1));
        }
        _out.WriteLine($"R = {r:f2} K/W, τ = {tau:f2} s: worst deviation from R(1 − e^(−t/τ)) {worst * 100:f3} %; " +
                       $"steady {curve.SteadyKelvinPerWatt:f3} K/W");
        _out.WriteLine(string.Join("\n", result.Log));
        Assert.Equal(1.0, result.PowerWatts, 9);
        Assert.True(worst < 0.003, $"curve off by {worst * 100:f2} %");
        Assert.InRange(curve.SteadyKelvinPerWatt / r, 0.999, 1.002);   // Biot number 3e-4

        // One stage is found, with the body's R and C.
        var fit = ThermalNetworkFit.FitFoster(curve.TimesSeconds, curve.KelvinPerWatt);
        _out.WriteLine(fit.Describe());
        Assert.Equal(1, fit.Network.Stages);
        Assert.InRange(fit.Network.Resistances[0] / r, 0.99, 1.01);
        Assert.InRange(fit.Network.Capacitances[0] / CubeCapacity, 0.99, 1.01);
    }

    [Fact]
    public void LumpedBody_PulsedPower_FollowsTheClosedForm_AndTheEnergyIsExact()
    {
        var profile = PowerProfile.Pulse(period: 20, dutyCycle: 0.25);
        double r = CubeResistance, tau = r * CubeCapacity;

        // Exact rise of one RC stage under the pulse train, switch by switch.
        double Exact(double time)
        {
            double rise = 0, t = 0;
            while (t < time)
            {
                double phase = t - Math.Floor(t / 20) * 20;
                double until = Math.Min(time, t - phase + (phase < 5 ? 5 : 20));
                double target = phase < 5 ? r : 0;
                rise = target + (rise - target) * Math.Exp(-(until - t) / tau);
                t = until;
            }
            return rise;
        }

        var cooled = new TransientThermalSolver().Solve(Cube(cooled: true) with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 305, TimeStep = 0.25, PowerProfile = profile
            }
        });
        var last = ((NodalScalarField)cooled.Fields.First(f => f.Name == "Temperature")).Values;
        double measured = last.Average() - 300, exact = Exact(305);
        _out.WriteLine($"end of the 16th pulse: rise {measured:f4} K, one RC stage {exact:f4} K ({(measured / exact - 1) * 100:+0.00;-0.00} %)");
        Assert.InRange(measured / exact, 0.997, 1.003);
        Assert.Contains(cooled.Log, l => l.Contains("Power profile"));

        // No cooling, and a step that does not line up with the pulse edges: what is stored
        // is what the profile delivered, to rounding.
        var adiabatic = new TransientThermalSolver().Solve(Cube(cooled: false) with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 96, TimeStep = 6, PowerProfile = profile
            }
        });
        var stored = ((NodalScalarField)adiabatic.Fields.First(f => f.Name == "Temperature")).Values;
        double energy = profile.Mean(0, 96) * 96;   // joules at 1 W peak: five 5 s pulses
        _out.WriteLine($"adiabatic, 6 s steps across 5 s pulses: mean rise {stored.Average() - 300:f6} K, energy/C {energy / CubeCapacity:f6} K");
        Assert.Equal(25.0, energy, 12);
        Assert.Equal(energy / CubeCapacity, stored.Average() - 300, 8);
        Assert.Contains(adiabatic.Log, l => l.StartsWith("WARNING") && l.Contains("shortest interval"));

        // The same answer by superposition of the impedance curve.
        var curve = ThermalImpedanceSolver.Solve(Cube(cooled: true), null,
            new ThermalImpedanceSettings { StartTime = 0.05, EndTime = 600, PointsPerDecade = 12 }).Curves.Single();
        double superposed = curve.RiseAt(305, profile, 1.0);
        _out.WriteLine($"by superposition of Z_th: {superposed:f4} K");
        Assert.InRange(superposed / exact, 0.99, 1.01);
    }

    [Fact]
    public void ConstantProfile_LeavesTheSolveAsItWas()
    {
        var settings = new TransientThermalSettings { InitialTemperature = 300, Duration = 20, TimeStep = 1 };
        var plain = new TransientThermalSolver().Solve(Cube(cooled: true) with { TransientThermal = settings });
        var constant = new TransientThermalSolver().Solve(Cube(cooled: true) with
        {
            TransientThermal = settings with { PowerProfile = new PowerProfile() }
        });
        var a = ((NodalScalarField)plain.Fields.First(f => f.Name == "Temperature")).Values;
        var b = ((NodalScalarField)constant.Fields.First(f => f.Name == "Temperature")).Values;
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    // ---------------- The semi-infinite body ----------------

    private static SolveInput Rod(double length, int cells, bool sinkAtFarEnd)
    {
        const double side = 2e-3;
        var mesh = StructuredBoxMesh.Build(0, length, 0, side, 0, side, cells, 2, 2);
        var conditions = new List<BoundaryCondition>
        {
            new HeatFlux { Name = "source", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, TotalPower = 2.0 }
        };
        if (sinkAtFarEnd)
            conditions.Add(new FixedTemperature { Name = "sink", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = 300 });
        return new SolveInput { Mesh = mesh, Material = Copper, BoundaryConditions = conditions };
    }

    [Fact]
    public void SemiInfiniteBody_ImpedanceGrowsAsRootTime()
    {
        const double area = 4e-6;
        var result = ThermalImpedanceSolver.Solve(Rod(0.05, 400, sinkAtFarEnd: false), null,
            new ThermalImpedanceSettings { StartTime = 1e-3, EndTime = 0.3, PointsPerDecade = 12 });
        var curve = result.Curves.Single();
        double worst = 0;
        foreach (var (t, z) in curve.TimesSeconds.Zip(curve.KelvinPerWatt))
        {
            if (t < 0.01) continue;   // before that the heated layer is under ten elements deep
            double exact = 2 * Math.Sqrt(t) / (area * Math.Sqrt(Math.PI * K * Density * SpecificHeat));
            worst = Math.Max(worst, Math.Abs(z / exact - 1));
        }
        _out.WriteLine($"0.01 s … 0.3 s: worst deviation from 2√t/(A√(πkρc)) {worst * 100:f3} %");
        _out.WriteLine(string.Join("\n", result.Log));
        Assert.Equal(2.0, result.PowerWatts, 9);
        Assert.True(worst < 0.002, $"off by {worst * 100:f2} %");
        Assert.True(double.IsNaN(curve.SteadyKelvinPerWatt));
        Assert.Contains(result.Log, l => l.Contains("no steady value"));
    }

    // ---------------- Networks ----------------

    [Fact]
    public void FosterFit_RecoversAKnownNetwork()
    {
        var known = new FosterNetwork(new[] { 0.5, 2.0, 6.0 }, new[] { 1e-3, 5e-2, 3.0 });
        var times = Enumerable.Range(0, 71).Select(i => 1e-5 * Math.Pow(10, i / 10.0)).ToList();
        var curve = times.Select(known.Impedance).ToList();
        var fit = ThermalNetworkFit.FitFoster(times, curve);
        _out.WriteLine(fit.Describe());
        _out.WriteLine("R: " + string.Join(", ", fit.Network.Resistances.Select(v => v.ToString("g6"))) +
                       "; τ: " + string.Join(", ", fit.Network.TimeConstants.Select(v => v.ToString("g6"))));
        Assert.Equal(3, fit.Network.Stages);
        Assert.True(fit.WorstError < 1e-4);
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(fit.Network.Resistances[i] / known.Resistances[i], 0.995, 1.005);
            Assert.InRange(fit.Network.TimeConstants[i] / known.TimeConstants[i], 0.99, 1.01);
        }
        // Asked for fewer stages than there are, the fit says how far off it is.
        var coarse = ThermalNetworkFit.FitFoster(times, curve, stages: 1);
        Assert.Equal(1, coarse.Network.Stages);
        Assert.True(coarse.WorstError > 0.02);
        Assert.Throws<ArgumentException>(() => ThermalNetworkFit.FitFoster(new[] { 1.0 }, new[] { 1.0 }));
    }

    /// <summary>The two-stage ladder C1 ∥ (R1 + (C2 ∥ R2)) as its Foster equivalent, by
    /// partial fractions of Z(s) = (R1 + R2 + sR1R2C2)/(1 + s(R2C2 + C1(R1 + R2)) + s²C1R1R2C2).</summary>
    private static FosterNetwork LadderAsFoster(double r1, double c1, double r2, double c2)
    {
        double a = c1 * r1 * r2 * c2, b = r2 * c2 + c1 * (r1 + r2);
        double root = Math.Sqrt(b * b - 4 * a);
        double tauSlow = (b + root) / 2, tauFast = a / tauSlow;
        double Numerator(double s) => r1 + r2 + s * r1 * r2 * c2;
        double fast = Numerator(-1 / tauFast) / (1 - tauSlow / tauFast);
        double slow = Numerator(-1 / tauSlow) / (1 - tauFast / tauSlow);
        return new FosterNetwork(new[] { fast, slow }, new[] { tauFast, tauSlow });
    }

    [Fact]
    public void FosterToCauer_ReturnsTheLadder()
    {
        const double r1 = 1.0, c1 = 0.01, r2 = 4.0, c2 = 0.5;
        var ladder = ThermalNetworkFit.ToCauer(LadderAsFoster(r1, c1, r2, c2));
        _out.WriteLine($"ladder R {ladder.Resistances[0]:g12}, {ladder.Resistances[1]:g12}; C {ladder.Capacitances[0]:g12}, {ladder.Capacitances[1]:g12}");
        Assert.Equal(r1, ladder.Resistances[0], 9);
        Assert.Equal(r2, ladder.Resistances[1], 9);
        Assert.Equal(c1, ladder.Capacitances[0], 11);
        Assert.Equal(c2, ladder.Capacitances[1], 9);

        // Eight stages over seven decades: the ladder has the Foster network's impedance.
        var wide = new FosterNetwork(
            new[] { 0.02, 0.05, 0.1, 0.3, 0.2, 0.8, 1.5, 3.0 },
            new[] { 1e-6, 1.3e-5, 2e-4, 1.1e-3, 9e-3, 0.12, 1.4, 10.0 });
        var cauer = ThermalNetworkFit.ToCauer(wide);
        double worst = 0;
        for (double s = 1e-3; s < 1e7; s *= 3.1)
            worst = Math.Max(worst, Math.Abs(cauer.Laplace(s) / wide.Laplace(s) - 1));
        _out.WriteLine($"8 stages over 7 decades: worst |Z_cauer/Z_foster − 1| {worst:e2}; ΣR {cauer.TotalResistance:g12}");
        Assert.True(worst < 1e-9, $"ladder impedance off by {worst:e2}");
        Assert.Equal(wide.TotalResistance, cauer.TotalResistance, 9);
        Assert.All(cauer.Resistances, r => Assert.True(r > 0));
        Assert.All(cauer.Capacitances, c => Assert.True(c > 0));
        Assert.Throws<InvalidOperationException>(() =>
            ThermalNetworkFit.ToCauer(new FosterNetwork(new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 })));
    }

    [Fact]
    public void StructureFunction_ShowsTheLaddersSteps()
    {
        const double r1 = 1.0, c1 = 0.01, r2 = 4.0, c2 = 0.5;
        var exact = LadderAsFoster(r1, c1, r2, c2);
        var times = Enumerable.Range(0, 85).Select(i => 1e-5 * Math.Pow(10, i / 12.0)).ToList();
        var structure = ThermalNetworkFit.Structure(times, times.Select(exact.Impedance).ToList());
        double first = structure.CapacitanceAt(0.5 * r1), second = structure.CapacitanceAt(r1 + 0.5 * r2);
        _out.WriteLine($"{structure.Ladder.Stages} ladder stages; ΣC half-way through R1: {first:g4} (C1 = {c1}); " +
                       $"half-way through R2: {second:g4} (C1 + C2 = {c1 + c2}); ΣR {structure.CumulativeResistance[^1]:g5}");
        Assert.InRange(structure.CumulativeResistance[^1] / (r1 + r2), 0.995, 1.005);
        Assert.InRange(first / c1, 0.95, 1.05);
        Assert.InRange(second / (c1 + c2), 0.97, 1.03);
        // Cumulative sums only rise.
        for (int k = 1; k < structure.CumulativeResistance.Count; k++)
        {
            Assert.True(structure.CumulativeResistance[k] > structure.CumulativeResistance[k - 1]);
            Assert.True(structure.CumulativeCapacitance[k] > structure.CumulativeCapacitance[k - 1]);
        }
    }

    [Fact]
    public void RodToSink_Fit_Ladder_PulsedPeak_AndExport()
    {
        const double length = 0.02, area = 4e-6;
        var result = ThermalImpedanceSolver.Solve(Rod(length, 80, sinkAtFarEnd: true), null,
            new ThermalImpedanceSettings { StartTime = 1e-4, EndTime = 30, PointsPerDecade = 12 });
        var curve = result.Curves.Single();
        double steady = length / (K * area);
        Assert.InRange(curve.SteadyKelvinPerWatt / steady, 0.999, 1.001);
        Assert.InRange(curve.KelvinPerWatt[^1] / steady, 0.999, 1.001);

        var fit = ThermalNetworkFit.FitFoster(curve.TimesSeconds, curve.KelvinPerWatt);
        var ladder = ThermalNetworkFit.ToCauer(fit.Network);
        _out.WriteLine(fit.Describe());
        _out.WriteLine($"ladder: ΣR {ladder.TotalResistance:g6} K/W (L/kA = {steady:g6}), ΣC {ladder.Capacitances.Sum():g4} J/K " +
                       $"(the rod holds {Density * SpecificHeat * length * area:g4})");
        Assert.True(fit.WorstError < 0.01, fit.Describe());
        Assert.InRange(ladder.TotalResistance / steady, 0.99, 1.01);
        double worst = 0;
        for (double s = 1e-2; s < 1e4; s *= 2.7)
            worst = Math.Max(worst, Math.Abs(ladder.Laplace(s) / fit.Network.Laplace(s) - 1));
        Assert.True(worst < 1e-8, $"ladder and Foster differ by {worst:e2}");

        // A 20 % pulse train repeated until it has settled: the fitted network's closed form
        // against the superposition of the solved curve.
        var profile = PowerProfile.Pulse(period: 0.1, dutyCycle: 0.2);
        double end = 400 * 0.1 + 0.02;   // end of the 401st pulse
        double superposed = curve.RiseAt(end, profile, 1.0), closed = fit.Network.PulsedPeak(0.1, 0.2);
        _out.WriteLine($"pulsed peak, 20 % of 0.1 s: {closed:f4} K/W from the network, {superposed:f4} by superposition; " +
                       $"single pulse {curve.At(0.02):f4}, steady {steady:f4}");
        Assert.InRange(closed / superposed, 0.99, 1.01);
        Assert.True(closed > curve.At(0.02) && closed < steady);
        Assert.Equal(steady * 1.0, fit.Network.PulsedPeak(0.1, 1.0), 1);

        string foster = fit.Network.ToSpice("ROD"), cauer = ladder.ToSpice();
        Assert.Contains(".SUBCKT ROD j amb", foster);
        Assert.Contains($"R{fit.Network.Stages} ", foster);
        Assert.Contains(".ENDS ZTH_CAUER", cauer);
        Assert.Contains("C1 j amb ", cauer);
        Assert.Equal(fit.Network.Stages + 1, fit.Network.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(ladder.Stages + 1, ladder.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TwoCurves_PartWhereTheirPathsDo()
    {
        // Same first stage (the part), different second stage (what it is mounted on).
        var times = Enumerable.Range(0, 61).Select(i => 1e-5 * Math.Pow(10, i / 10.0)).ToList();
        ThermalImpedanceCurve Curve(double r2, double tau2)
        {
            var network = new FosterNetwork(new[] { 1.0, r2 }, new[] { 1e-3, tau2 });
            return new ThermalImpedanceCurve("x", times, times.Select(network.Impedance).ToList());
        }
        var (value, at) = ThermalNetworkFit.Separation(Curve(2.0, 1.0), Curve(5.0, 1.0), 0.05);
        _out.WriteLine($"curves part at {value:f3} K/W, t = {at:g3} s (shared stage 1.0 K/W)");
        Assert.InRange(value, 0.95, 1.15);
        Assert.True(double.IsNaN(ThermalNetworkFit.Separation(Curve(2.0, 1.0), Curve(2.0, 1.0), 0.05).KelvinPerWatt));
    }

    // ---------------- Environment, refusals ----------------

    [Fact]
    public void Environment_IsHeldAtItsSteadyCoefficients()
    {
        var input = Cube(cooled: false, watts: 0.2) with { Environment = new EnvironmentSettings { AmbientTemperature = 300 } };
        var steady = new HeatConductionSolver().Solve(input);
        double rise = ((NodalScalarField)steady.Fields.First(f => f.Name == "Temperature")).Values.Average() - 300;
        var result = ThermalImpedanceSolver.Solve(input, null,
            new ThermalImpedanceSettings { StartTime = 1, EndTime = 3000, PointsPerDecade = 8 });
        var curve = result.Curves.Single();
        _out.WriteLine($"cube in still air at 0.2 W: steady rise {rise:f2} K; Z_th steady {curve.SteadyKelvinPerWatt:f2} K/W × 0.2 W = {curve.SteadyKelvinPerWatt * 0.2:f2} K");
        Assert.InRange(curve.SteadyKelvinPerWatt * 0.2 / rise, 0.995, 1.005);
        Assert.Contains(result.Log, l => l.Contains("held at their steady values"));
    }

    [Fact]
    public void Refusals()
    {
        var noSource = Cube(cooled: true) with { ElementHeatSource = null };
        Assert.Throws<InvalidOperationException>(() => ThermalImpedanceSolver.Solve(noSource));
        Assert.Throws<InvalidOperationException>(() => ThermalImpedanceSolver.Solve(Cube(true), null,
            new ThermalImpedanceSettings { StartTime = 1, EndTime = 0.5 }));
        Assert.Throws<InvalidOperationException>(() => new TransientThermalSolver().Solve(Cube(true) with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 10, TimeStep = 1, PowerProfile = PowerProfile.Pulse(-1, 0.5)
            }
        }));
    }
}
