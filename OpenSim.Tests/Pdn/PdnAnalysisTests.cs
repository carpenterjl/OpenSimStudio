using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Pdn;
using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// The network on top of the plane solve: a capacitor on the planes against the cavity model
/// with the same capacitor attached by hand, the target mask, the mounting inductance against
/// the two-wire line it reduces to, the reading of a board, and Touchstone import.
/// </summary>
public class PdnAnalysisTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private readonly ITestOutputHelper _output;
    public PdnAnalysisTests(ITestOutputHelper output) => _output = output;

    private static readonly CapacitorModel Cap100n = new()
        { PartName = "100n 0402", CapacitanceFarads = 100e-9, EsrOhms = 0.03, EslHenries = 0.4e-9 };

    private static PdnSetup OneCapacitor(double mounting = 0.6e-9) => new()
    {
        Planes = PlanePairTests.ReferenceSpec(),
        LoadPorts = new[] { 0 },
        Capacitors = new[] { new PlacedCapacitor("C1", 1, Cap100n, mounting) },
        MinFrequencyHz = 1e5,
        MaxFrequencyHz = 1.5e9,
        PointsPerDecade = 30,
        ModeCount = 3
    };

    /// <summary>The cavity model's two-port with the capacitor hung on port 2 by hand.</summary>
    private static Complex Exact(double f, double mounting)
    {
        Complex z11 = PlanePairTests.Cavity(f, 0, 0, 1500), z12 = PlanePairTests.Cavity(f, 0, 1, 1500),
            z22 = PlanePairTests.Cavity(f, 1, 1, 1500);
        Complex zc = Cap100n.Impedance(f) + new Complex(0, 2 * Math.PI * f * mounting);
        return z11 - z12 * z12 / (z22 + zc);
    }

    [Fact]
    public void ACapacitorOnThePlanes_ResonatesAndAntiResonatesWhereTheCavityModelSays()
    {
        double mounting = 0.6e-9;
        var result = PdnAnalysis.Solve(OneCapacitor(mounting));
        foreach (string line in result.Describe()) _output.WriteLine(line);

        // Pointwise, away from the peaks.
        foreach (double f in new[] { 1e5, 1e6, 1e7, 1e8, 4e8 })
        {
            int k = Enumerable.Range(0, result.FrequenciesHz.Count).MinBy(i => Math.Abs(Math.Log(result.FrequenciesHz[i] / f)));
            double at = result.FrequenciesHz[k];
            Complex exact = Exact(at, mounting);
            double error = (result.Impedance[k] - exact).Magnitude / exact.Magnitude;
            _output.WriteLine($"{at / 1e6:g4} MHz: {result.Impedance[k].Magnitude:e4} Ω, cavity + capacitor {exact.Magnitude:e4} Ω ({error:p2})");
            Assert.True(error < 0.015);
        }

        // Series resonance of the mounted part: the dip sits at 1/(2π√(L·C)) with L the ESL,
        // the mounting and the planes' own inductance between the two ports.
        Complex z11 = PlanePairTests.Cavity(5e6, 0, 0), z12 = PlanePairTests.Cavity(5e6, 0, 1), z22 = PlanePairTests.Cavity(5e6, 1, 1);
        double spreading = (z11 + z22 - 2 * z12).Imaginary / (2 * Math.PI * 5e6);
        double loop = Cap100n.EslHenries + mounting + spreading;
        double series = 1 / (2 * Math.PI * Math.Sqrt(loop * Cap100n.CapacitanceFarads));
        var dip = result.Extrema.Where(e => !e.IsMaximum).MinBy(e => Math.Abs(Math.Log(e.FrequencyHz / series)))!;
        _output.WriteLine($"series resonance: dip at {dip.FrequencyHz / 1e6:f2} MHz, 1/(2π√LC) = {series / 1e6:f2} MHz (L = {loop * 1e9:f3} nH)");
        Assert.Equal(series, dip.FrequencyHz, series * 0.05);

        // Anti-resonance: the capacitor's inductance against the plane capacitance. The
        // lumped figure, and then the peak itself against the cavity model's.
        double planeC = 8.8541878128e-12 * PlanePairTests.Er * PlanePairTests.A * PlanePairTests.B / PlanePairTests.D;
        double lumped = 1 / (2 * Math.PI * Math.Sqrt(loop * planeC * Cap100n.CapacitanceFarads / (planeC + Cap100n.CapacitanceFarads)));
        var peak = result.Extrema.Where(e => e.IsMaximum).MinBy(e => Math.Abs(Math.Log(e.FrequencyHz / lumped)))!;
        double bestExact = 0, atExact = 0;
        for (double f = peak.FrequencyHz * 0.9; f <= peak.FrequencyHz * 1.1; f *= 1.002)
        {
            double m = Exact(f, mounting).Magnitude;
            if (m > bestExact) { bestExact = m; atExact = f; }
        }
        _output.WriteLine($"anti-resonance: {peak.Ohms:f4} Ω at {peak.FrequencyHz / 1e6:f2} MHz; cavity + capacitor " +
                          $"{bestExact:f4} Ω at {atExact / 1e6:f2} MHz; lumped estimate {lumped / 1e6:f2} MHz");
        Assert.Equal(lumped, peak.FrequencyHz, lumped * 0.15);     // a lumped estimate, no more
        Assert.Equal(atExact, peak.FrequencyHz, atExact * 0.005);
        Assert.Equal(bestExact, peak.Ohms, bestExact * 0.02);

        // The bare planes are what is left with the capacitor taken off.
        var bare = PdnAnalysis.Solve(OneCapacitor() with { Capacitors = Array.Empty<PlacedCapacitor>() });
        int j = bare.FrequenciesHz.Count / 3;
        Assert.Equal(bare.Impedance[j].Magnitude, bare.BareImpedance[j].Magnitude, 1e-12);
    }

    [Fact]
    public void TheTargetMask_FindsTheBandOverIt()
    {
        var setup = OneCapacitor() with { Target = TargetImpedance.Flat(1.0, 5, 0.5, 2e8) };   // 100 mΩ to 200 MHz
        Assert.Equal(0.1, setup.Target!.At(1e6), 1e-12);
        Assert.True(double.IsNaN(setup.Target.At(3e8)));
        var result = PdnAnalysis.Solve(setup);
        foreach (string line in result.Describe()) _output.WriteLine(line);
        Assert.False(result.Pass);
        // Below the capacitor's reach the planes alone are far over 100 mΩ, and so is the
        // anti-resonance; each band's worst point is over its limit and inside the band.
        Assert.Contains(result.Violations, v => v.FromHz <= 1.01e5);
        foreach (var v in result.Violations)
        {
            Assert.True(v.WorstOhms > v.TargetOhms);
            Assert.InRange(v.WorstHz, v.FromHz, v.ToHz);
            Assert.True(v.ToHz <= 2e8);
        }

        // A regulator holds the low end, more capacitance the middle: the low band goes.
        var held = PdnAnalysis.Solve(setup with
        {
            Regulators = new[] { new Regulator("U2", 1, 0.005, 20e-9) },
            Capacitors = setup.Capacitors.Append(new PlacedCapacitor("C2", 1,
                new CapacitorModel { PartName = "10u", CapacitanceFarads = 10e-6, EsrOhms = 0.005, EslHenries = 0.5e-9 }, 0.6e-9)).ToList()
        });
        foreach (string line in held.Describe()) _output.WriteLine(line);
        Assert.DoesNotContain(held.Violations, v => v.FromHz <= 1.01e5);
        // At 100 kHz the regulator is 5 mΩ + j12.6 mΩ; the 10.1 µF beside it (−j158 mΩ) lifts
        // that to 5.9 + j13.5 mΩ, and the planes between the two ports add their sheet
        // resistance (about 1.2 mΩ) and 0.4 nH: 15.5 mΩ in all.
        Assert.InRange(held.Impedance[0].Magnitude, 0.0150, 0.0160);
        Assert.Contains("frequency_Hz", held.ToCsv(setup.Target));
        Assert.StartsWith("! OpenSim", held.ToTouchstone());
    }

    [Fact]
    public void LoadPortsTiedTogether_AreLowerThanEitherAlone()
    {
        var pair = new PlanePair(PlanePairTests.ReferenceSpec());
        var f = new[] { 3e8 };
        var planes = pair.Sweep(f);
        Complex Both(params int[] ports) => PdnAnalysis.Evaluate(pair, f, planes,
            new PdnSetup { Planes = pair.Spec, LoadPorts = ports }).Impedance[0];
        var z = planes[0];
        Complex expected = (z[0, 0] * z[1, 1] - z[0, 1] * z[0, 1]) / (z[0, 0] + z[1, 1] - 2 * z[0, 1]);
        Assert.True((Both(0, 1) - expected).Magnitude < 1e-9 * expected.Magnitude);
        Assert.True((Both(0) - z[0, 0]).Magnitude < 1e-12);
    }

    // ------------------------------------------------------------------ mounting

    [Fact]
    public void TwoViasOnAPlane_AreHalfTheTwoWireLoopOfTwiceTheLength()
    {
        // Vias only (pads on the vias). With their images the two barrels are a two-wire
        // line of length 2h, and the plane loop links half of that line's flux.
        double h = 0.3e-3, s = 1.0e-3, d = 0.3e-3;
        var g = new MountingGeometry(new Point2(0, 0), new Point2(0, 0), d, new Point2(s, 0), new Point2(s, 0), d, h, 0.5e-3, 35e-6);
        double loop = DecapMounting.LoopInductance(g);
        double self = PartialInductance.RoundTubeSelfInductance(2 * h, d / 2);
        double mutual = FilamentMutual.Between(new Vector3D(0, 0, -h), new Vector3D(0, 0, h), new Vector3D(s, 0, -h), new Vector3D(s, 0, h));
        _output.WriteLine($"two vias {h * 1e3} mm tall, {s * 1e3} mm apart: {loop * 1e12:f2} pH");
        Assert.Equal(self - mutual, loop, 1e-6 * loop);

        // Tall and close together it tends to the line formula (µ₀/π)·h·ln(s/r).
        double tall = 20e-3;
        double longLoop = DecapMounting.LoopInductance(g with { HeightMeters = tall });
        double line = Mu0 / Math.PI * tall * Math.Log(s / (d / 2));
        _output.WriteLine($"20 mm tall: {longLoop * 1e9:f4} nH, line formula {line * 1e9:f4} nH");
        Assert.Equal(line, longLoop, 0.03 * line);

        // Escape traces add to it, and more with length.
        double near = DecapMounting.LoopInductance(g with { PadA = new Point2(-0.5e-3, 0), PadB = new Point2(s + 0.5e-3, 0) });
        double far = DecapMounting.LoopInductance(g with { PadA = new Point2(-1.5e-3, 0), PadB = new Point2(s + 1.5e-3, 0) });
        _output.WriteLine($"with 0.5 mm escapes {near * 1e12:f1} pH, with 1.5 mm escapes {far * 1e12:f1} pH");
        Assert.True(near > loop && far > near);
    }

    // ------------------------------------------------------------------ board

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) => PlanePairTests.Rect(x0, y0, x1, y1);

    /// <summary>Four layers: parts on L1, ground plane L2 (60 × 40 mm), power plane L3
    /// (50 × 40 mm), nothing on L4. U1 and C1…C3 on L1, each pad with a via 0.8 mm away;
    /// C3's power pad has none.</summary>
    private static (PcbBoard Board, CopperNet Power, CopperNet Ground) Board()
    {
        var islands = new List<CopperIsland>();
        var pads = new List<CopperPad>();
        var vias = new List<Via>();
        var powerIslands = new List<CopperIsland>();
        var groundIslands = new List<CopperIsland>();
        var powerVias = new List<ViaBridge>();
        var groundVias = new List<ViaBridge>();
        CopperIsland Add(List<CopperIsland> net, int layer, Polygon2 shape)
        {
            var island = new CopperIsland(islands.Count, layer, $"L{layer}", shape);
            islands.Add(island);
            net.Add(island);
            return island;
        }
        // Clearance holes: where a power via passes the ground plane and the reverse.
        var groundHoles = new List<IReadOnlyList<Point2>>();
        var powerHoles = new List<IReadOnlyList<Point2>>();
        IReadOnlyList<Point2> Hole(double x, double y) =>
            Rect(x - 0.4e-3, y - 0.4e-3, x + 0.4e-3, y + 0.4e-3).Outer.Reverse().ToList();

        void Part(string refdes, double x, double y, bool powerVia = true)
        {
            foreach (var (isPower, dx, pin) in new[] { (true, -0.5e-3, "1"), (false, 0.5e-3, "2") })
            {
                var shape = Rect(x + dx - 0.3e-3, y - 0.3e-3, x + dx + 0.3e-3, y + 0.3e-3);
                // Pad and via land on one piece of top copper.
                double viaX = x + dx + (isPower ? -0.8e-3 : 0.8e-3);
                bool hasVia = !isPower || powerVia;
                var copper = hasVia
                    ? Rect(Math.Min(x + dx, viaX) - 0.3e-3, y - 0.3e-3, Math.Max(x + dx, viaX) + 0.3e-3, y + 0.3e-3)
                    : shape;
                Add(isPower ? powerIslands : groundIslands, 1, copper);
                pads.Add(new CopperPad(1, new Point2(x + dx, y), shape, 0.6e-3) { ComponentRef = refdes, Pin = pin, PartName = "CAP-0402" });
                if (!hasVia) continue;
                var via = new Via(new Point2(viaX, y), 0.3e-3, true);
                vias.Add(via);
                if (isPower) { powerVias.Add(new ViaBridge(via, new[] { 1, 3 })); groundHoles.Add(Hole(viaX, y)); }
                else { groundVias.Add(new ViaBridge(via, new[] { 1, 2 })); powerHoles.Add(Hole(viaX, y)); }
            }
        }
        Part("U1", 0.015, 0.020);
        Part("C1", 0.020, 0.020);
        Part("C2", 0.040, 0.030);
        Part("C3", 0.030, 0.010, powerVia: false);
        Part("R1", 0.045, 0.010);

        Add(groundIslands, 2, new Polygon2(Rect(0, 0, 0.060, 0.040).Outer, groundHoles));
        Add(powerIslands, 3, new Polygon2(Rect(0.005, 0, 0.055, 0.040).Outer, powerHoles));

        var power = new CopperNet(1, powerIslands) { Name = "+3V3", StitchingVias = powerVias };
        var ground = new CopperNet(2, groundIslands) { Name = "GND", StitchingVias = groundVias };
        var board = new PcbBoard
        {
            Outline = new[] { Rect(0, 0, 0.060, 0.040) },
            Islands = islands,
            Pads = pads,
            Vias = vias,
            Nets = new[] { power, ground },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>()
        };
        return (board, power, ground);
    }

    private static readonly BoardStackup Stackup = new()
    {
        GapThickness = new Dictionary<int, double> { [1] = 0.2e-3, [2] = 0.1e-3, [3] = 1.0e-3 },
        GapPermittivity = new Dictionary<int, double> { [1] = 4.2, [2] = 4.0, [3] = 4.5 },
        GapLossTangent = new Dictionary<int, double> { [1] = 0.02, [2] = 0.015, [3] = 0.02 },
        Source = "test"
    };

    [Fact]
    public void TheBoardGivesThePlanePair_TheCapacitorsAndTheirMounting()
    {
        var (board, power, ground) = Board();
        var options = new PdnBoardOptions { Stackup = Stackup };
        var model = PdnBoard.Extract(board, power, ground, options);
        foreach (string note in model.Notes) _output.WriteLine(note);
        foreach (var c in model.Capacitors)
            _output.WriteLine($"{c.RefDes}: {(c.Site is null ? c.Problem : $"port at ({c.Site.Position.X * 1e3:f2}, {c.Site.Position.Y * 1e3:f2}) mm, mounting {c.MountingInductanceHenries * 1e12:f1} pH")}");

        Assert.Equal(3, model.PowerLayer);
        Assert.Equal(2, model.GroundLayer);
        Assert.Equal(0.1e-3, model.SeparationMeters, 1e-12);
        Assert.Equal(4.0, model.RelativePermittivity, 1e-9);
        Assert.Equal(0.015, model.LossTangent, 1e-9);
        // The overlap is the smaller plane; the 0.64 mm² clearance holes are filled.
        Assert.Equal(0.050 * 0.040, model.AreaSquareMeters, 1e-9);
        Assert.All(model.Shape, p => Assert.Empty(p.Holes));

        // R1 is not a capacitor; C3 has no power via.
        Assert.Equal(new[] { "C1", "C2", "C3" }, model.Capacitors.Select(c => c.RefDes));
        Assert.Null(model.Capacitors[2].Site);
        Assert.Contains("no via", model.Capacitors[2].Problem);

        // Ground is the nearer plane, so the power via crosses the pair: the port is there.
        var c1 = model.Capacitors[0];
        Assert.Equal(0.020 - 0.5e-3 - 0.8e-3, c1.Site!.Position.X, 1e-9);
        Assert.Equal(0.15e-3, c1.Site.RadiusMeters, 1e-12);
        // Height: half the pad copper plus the L1–L2 gap.
        double expected = DecapMounting.LoopInductance(new MountingGeometry(
            new Point2(0.0195, 0.020), new Point2(0.0187, 0.020), 0.3e-3,
            new Point2(0.0205, 0.020), new Point2(0.0213, 0.020), 0.3e-3,
            0.2e-3 + 17.5e-6, 0.6e-3, 35e-6));
        Assert.Equal(expected, c1.MountingInductanceHenries, 1e-9 * expected);
        Assert.InRange(c1.MountingInductanceHenries, 0.2e-9, 1.5e-9);

        // The load's site, the problem as a whole, and a solve.
        var setup = PdnBoard.Setup(board, power, ground, model, "U1", _ => Cap100n,
            options: options) with { MinFrequencyHz = 1e6, MaxFrequencyHz = 1e9, PointsPerDecade = 20, ModeCount = 2 };
        Assert.Equal(3, setup.Planes.Ports.Count);                 // U1, C1, C2
        Assert.Equal(2, setup.Capacitors.Count);
        Assert.Single(setup.LoadPorts);
        var result = PdnAnalysis.Solve(setup);
        foreach (string line in result.Describe()) _output.WriteLine(line);
        // With two 100 nF parts the low end is their capacitance plus the planes'.
        double planeC = 8.8541878128e-12 * 4.0 * 0.050 * 0.040 / 0.1e-3;
        double low = 1 / (2 * Math.PI * 1e6 * (200e-9 + planeC));
        Assert.Equal(low, result.Impedance[0].Magnitude, 0.02 * low);
        // First plane resonance of a 50 × 40 mm pair in εr 4: c/(2·0.05·2).
        Assert.Equal(299792458.0 / (2 * 0.050 * 2), result.Modes[0].FrequencyHz, 1e-3 * 1.5e9);
    }

    [Fact]
    public void ARailWithNoPlane_IsRefusedWithTheReason()
    {
        var (board, power, ground) = Board();
        var traceOnly = new CopperNet(3, power.Islands.Where(i => i.LayerOrder == 1).ToList()) { Name = "+1V8" };
        var ex = Assert.Throws<InvalidOperationException>(() => PdnBoard.Extract(board, traceOnly, ground));
        Assert.Contains("no plane pair", ex.Message);
        Assert.Throws<InvalidOperationException>(() => PdnBoard.SitesOf(board, "U9", power, ground,
            PdnBoard.Extract(board, power, ground, new PdnBoardOptions { Stackup = Stackup })));
    }

    // ------------------------------------------------------------------ Touchstone

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Touchstone_ReadsBackWhatTheWriterWrote(int ports)
    {
        var random = new Random(ports);
        var frequencies = new[] { 1e6, 1e7, 1e8 };
        var matrices = frequencies.Select(_ =>
        {
            var m = new Complex[ports, ports];
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    m[i, j] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            return m;
        }).ToList();
        var read = TouchstoneReader.Read(TouchstoneWriter.Write(frequencies, matrices, 75), ports);
        Assert.Equal(75, read.ReferenceOhms);
        Assert.Equal(frequencies, read.FrequenciesHz);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    Assert.True((read.Scattering[k][i, j] - matrices[k][i, j]).Magnitude < 1e-10);
    }

    [Fact]
    public void Touchstone_FormatsUnitsAndParameterKinds()
    {
        // 2-port, MHz, dB/angle, column-major order: S21 is the second pair.
        var db = TouchstoneReader.Read("! a comment\n# MHz S DB R 50\n100  -20 0  -6.0206 90  -40 180  -20 -90 ! tail\n", 2);
        Assert.Equal(1e8, db.FrequenciesHz[0]);
        Assert.True((db.Scattering[0][0, 0] - 0.1).Magnitude < 1e-9);
        Assert.True((db.Scattering[0][1, 0] - new Complex(0, 0.5)).Magnitude < 1e-5);
        Assert.True((db.Scattering[0][0, 1] - (-0.01)).Magnitude < 1e-9);

        // 1-port Z-parameters, normalised: z = 2 − j1 on 50 Ω is 100 − j50 Ω.
        var z = TouchstoneReader.Read("# GHz Z RI R 50\n1 2 -1\n", 1);
        Complex back = NetworkParameters.ScatteringToImpedance(z.Scattering[0], 50)[0, 0];
        Assert.True((back - new Complex(100, -50)).Magnitude < 1e-9);

        // Data wrapped over lines, magnitude/angle by default unit GHz.
        var wrapped = TouchstoneReader.Read("# S MA\n2\n0.5 90\n", 1);
        Assert.Equal(2e9, wrapped.FrequenciesHz[0]);
        Assert.True((wrapped.Scattering[0][0, 0] - new Complex(0, 0.5)).Magnitude < 1e-12);

        Assert.Throws<InvalidDataException>(() => TouchstoneReader.Read("[Version] 2.0\n", 1));
        Assert.Throws<InvalidDataException>(() => TouchstoneReader.Read("# Hz S RI\n", 1));
    }

    [Theory]
    [InlineData(CapacitorFixture.OnePort)]
    [InlineData(CapacitorFixture.TwoPortShunt)]
    [InlineData(CapacitorFixture.TwoPortSeries)]
    public void AMeasuredCapacitorModel_GivesBackThePartItWasMeasuredOn(CapacitorFixture fixture)
    {
        // S-parameters of the RLC part in each fixture, from the circuit, then read back.
        var frequencies = PdnAnalysis.LogSweep(1e5, 1e9, 10);
        var matrices = new List<Complex[,]>();
        foreach (double f in frequencies)
        {
            Complex z = Cap100n.Impedance(f);
            matrices.Add(fixture switch
            {
                CapacitorFixture.OnePort => new[,] { { (z - 50) / (z + 50) } },
                CapacitorFixture.TwoPortShunt => NetworkParameters.ImpedanceToScattering(new[,] { { z, z }, { z, z } }, 50),
                // A series element has no Z matrix; its S-parameters directly.
                _ => new[,] { { z / (z + 100), 100 / (z + 100) }, { 100 / (z + 100), z / (z + 100) } }
            });
        }
        var data = TouchstoneReader.Read(TouchstoneWriter.Write(frequencies, matrices), fixture == CapacitorFixture.OnePort ? 1 : 2);
        var measured = CapacitorModel.FromTouchstone("vendor", data, fixture);
        foreach (double f in new[] { 2e5, 3.3e6, 2.5e7, 7e8 })
        {
            Complex expected = Cap100n.Impedance(f), got = measured.Impedance(f);
            Assert.True((got - expected).Magnitude < 0.03 * expected.Magnitude,
                $"{fixture} at {f:g3} Hz: {got} against {expected}");
        }
        // Outside the measured band it carries on as a capacitor below and an inductor above.
        Assert.Equal(Cap100n.Impedance(1e4).Imaginary, measured.Impedance(1e4).Imaginary, 0.01 * Math.Abs(Cap100n.Impedance(1e4).Imaginary));
        Assert.Equal(Cap100n.Impedance(5e9).Imaginary, measured.Impedance(5e9).Imaginary, 0.01 * Cap100n.Impedance(5e9).Imaginary);
    }
}
