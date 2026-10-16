using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Pdn;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// FU-32's circuit around the planes: two capacitor mountings in one network couple as their two
/// loops do when composed by hand with their images; and a regulator with a control loop, sensing
/// at its own port or at the load's, gives the impedance and the loop gain that two ports' worth
/// of algebra give.
/// </summary>
public class PdnCircuitTests
{
    private readonly ITestOutputHelper _output;
    public PdnCircuitTests(ITestOutputHelper output) => _output = output;

    /// <summary>A capacitor mounting as DecapMounting draws it: pads 1 mm apart, a via 0.8 mm out
    /// from each, the barrels down to the plate.</summary>
    private static (Point2 PadA, Point2 ViaA, Point2 PadB, Point2 ViaB) Mount(double x, double y) =>
        (new Point2(x - 0.5e-3, y), new Point2(x - 1.3e-3, y), new Point2(x + 0.5e-3, y), new Point2(x + 1.3e-3, y));

    private const double H = 0.2e-3, Diameter = 0.3e-3, Width = 0.6e-3, Copper = 35e-6;

    /// <summary>The loop and its image as one chain, the way DecapMounting composes it.</summary>
    private static List<TraceSegment3D> Doubled((Point2 PadA, Point2 ViaA, Point2 PadB, Point2 ViaB) m)
    {
        Vector3D At(Point2 p, double z) => new(p.X, p.Y, z);
        return new List<TraceSegment3D>
        {
            new(At(m.ViaA, -H), At(m.ViaA, H), Diameter, 0, SegmentProfile.RoundTube),
            new(At(m.ViaA, H), At(m.PadA, H), Width, Copper),
            new(At(m.PadB, H), At(m.ViaB, H), Width, Copper),
            new(At(m.ViaB, H), At(m.ViaB, -H), Diameter, 0, SegmentProfile.RoundTube),
            new(At(m.ViaB, -H), At(m.PadB, -H), Width, Copper),
            new(At(m.PadA, -H), At(m.ViaA, -H), Width, Copper)
        };
    }

    private static (int A, int B) Add(MountingNetwork network, (Point2 PadA, Point2 ViaA, Point2 PadB, Point2 ViaB) m, string name)
    {
        int a = network.AddNode($"{name}.A"), ta = network.AddNode($"{name}.A via");
        int b = network.AddNode($"{name}.B"), tb = network.AddNode($"{name}.B via");
        network.AddTrace(m.PadA, m.ViaA, H, Width, Copper, a, ta);
        network.AddBarrel(m.ViaA, Diameter, H, ta, MountingNetwork.Plate);
        network.AddTrace(m.PadB, m.ViaB, H, Width, Copper, b, tb);
        network.AddBarrel(m.ViaB, Diameter, H, tb, MountingNetwork.Plate);
        return (a, b);
    }

    [Fact]
    public void TwoMountingsInOneNetwork_CoupleAsTheirLoopsComposedByHand()
    {
        foreach (double apart in new[] { 3e-3, 10e-3 })
        {
            var first = Mount(0, 0);
            var second = Mount(0, apart);
            var network = new MountingNetwork();
            var (a1, b1) = Add(network, first, "C1");
            var (a2, b2) = Add(network, second, "C2");

            // The two loops' 2 × 2 inductance from the network: the terminals' inductance against
            // the plate is Γ⁻¹, and a loop's voltage is its A terminal's less its B terminal's.
            var gamma = network.Reduce(new[] { a1, b1, a2, b2 });
            var inverse = new double[4, 4];
            {
                var flat = new double[16];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) flat[4 * i + j] = gamma[i, j];
                var lu = DenseLu.FactorInPlace(flat, 4, new int[4]);
                var e = new double[4];
                var x = new double[4];
                for (int c = 0; c < 4; c++)
                {
                    Array.Clear(e); e[c] = 1;
                    lu.Solve(e, x);
                    for (int r = 0; r < 4; r++) inverse[r, c] = x[r];
                }
            }
            double Loop(int p, int q) => inverse[p, q] - inverse[p, q + 1] - inverse[p + 1, q] + inverse[p + 1, q + 1];
            double l1 = Loop(0, 0), l2 = Loop(2, 2), m12 = Loop(0, 2);

            // By hand: each loop with its image as one chain, half of what Compose gives; the mutual
            // from the two chains composed together.
            var composer = new LoopComposer();
            double d1 = composer.Compose(Doubled(first)).LoopInductance, d2 = composer.Compose(Doubled(second)).LoopInductance;
            double both = composer.Compose(Doubled(first).Concat(Doubled(second)).ToList()).LoopInductance;
            double handMutual = (both - d1 - d2) / 4;
            _output.WriteLine($"{apart * 1e3} mm apart: L {l1 * 1e12:f3} pH (by hand {d1 / 2 * 1e12:f3}), M {m12 * 1e12:f4} pH (by hand {handMutual * 1e12:f4})");
            Assert.Equal(d1 / 2, l1, 1e-9 * l1);
            Assert.Equal(d2 / 2, l2, 1e-9 * l2);
            Assert.Equal(handMutual, m12, 1e-9 * l1);
            Assert.True(m12 > 0);
        }

        // Two vias on each pad: the two loops in parallel, coupled — below the smaller of them,
        // above the two in parallel without their mutual inductance.
        var one = new MountingNetwork();
        var (p, q) = Add(one, Mount(0, 0), "C");
        double single = one.LoopInductance(p, q);
        var two = new MountingNetwork();
        var (pa, pb) = Add(two, Mount(0, 0), "C");
        var m = Mount(0, 0);
        int extraA = two.AddNode("A via 2"), extraB = two.AddNode("B via 2");
        two.AddTrace(m.PadA, new Point2(m.PadA.X, 0.8e-3), H, Width, Copper, pa, extraA);
        two.AddBarrel(new Point2(m.PadA.X, 0.8e-3), Diameter, H, extraA, MountingNetwork.Plate);
        two.AddTrace(m.PadB, new Point2(m.PadB.X, 0.8e-3), H, Width, Copper, pb, extraB);
        two.AddBarrel(new Point2(m.PadB.X, 0.8e-3), Diameter, H, extraB, MountingNetwork.Plate);
        double doubleVia = two.LoopInductance(pa, pb);
        var other = new MountingNetwork();
        int oa = other.AddNode("A"), ob = other.AddNode("B"), ta = other.AddNode("A via"), tb = other.AddNode("B via");
        other.AddTrace(m.PadA, new Point2(m.PadA.X, 0.8e-3), H, Width, Copper, oa, ta);
        other.AddBarrel(new Point2(m.PadA.X, 0.8e-3), Diameter, H, ta, MountingNetwork.Plate);
        other.AddTrace(m.PadB, new Point2(m.PadB.X, 0.8e-3), H, Width, Copper, ob, tb);
        other.AddBarrel(new Point2(m.PadB.X, 0.8e-3), Diameter, H, tb, MountingNetwork.Plate);
        double beside = other.LoopInductance(oa, ob);
        _output.WriteLine($"vias along the pads {single * 1e12:f2} pH, beside them {beside * 1e12:f2} pH, both {doubleVia * 1e12:f2} pH " +
                          $"(uncoupled in parallel {single * beside / (single + beside) * 1e12:f2} pH)");
        Assert.InRange(doubleVia, single * beside / (single + beside), Math.Min(single, beside));
        Assert.Equal(DecapMounting.LoopInductance(new MountingGeometry(m.PadA, m.ViaA, Diameter, m.PadB, m.ViaB, Diameter, H, Width, Copper)),
            single, 1e-9 * single);
    }

    [Fact]
    public void ARegulatorLoop_GivesTheImpedanceAndLoopGainOfItsTwoPortAlgebra()
    {
        // The reference plane pair; the load at port 1, a buck regulator (2 mΩ + 1 µH behind its
        // compensator) and a 100 µF capacitor at port 2. Port algebra, with Zp the planes' 2 × 2:
        //   local sense:  Z = Zp11 − Zp12·Zp21·Y/(1 + Zp22·Y),  Y = (1 + A)/Zo + 1/Zc;
        //   loop gain:    T = A·Zp22/Zo/(1 + Zp22·(1/Zo + 1/Zc)).
        var pair = new PlanePair(PlanePairTests.ReferenceSpec());
        var frequencies = PdnAnalysis.LogSweep(1e3, 1e8, 6).ToList();
        var planes = pair.Sweep(frequencies);
        var loop = new RegulatorLoop { GainHz = 5e3, ZerosHz = new[] { 8e3, 16e3 }, PolesHz = new[] { 2e5, 1e6 } };
        var bulk = new CapacitorModel { PartName = "100u", CapacitanceFarads = 100e-6, EsrOhms = 5e-3, EslHenries = 1e-9 };
        var regulator = new Regulator("U2", 1, 2e-3, 1e-6) { Loop = loop };
        var setup = new PdnSetup
        {
            Planes = pair.Spec, LoadPorts = new[] { 0 },
            Capacitors = new[] { new PlacedCapacitor("C9", 1, bulk, 0) }, Regulators = new[] { regulator }
        };
        var local = PdnAnalysis.Evaluate(pair, frequencies, planes, setup);
        var remote = PdnAnalysis.Evaluate(pair, frequencies, planes, setup with { Regulators = new[] { regulator with { SensePort = 0 } } });
        foreach (string line in local.Describe().Concat(remote.Describe())) _output.WriteLine(line);

        double worst = 0;
        for (int k = 0; k < frequencies.Count; k++)
        {
            double f = frequencies[k];
            var zp = planes[k];
            Complex a = loop.Gain(f), zo = regulator.Impedance(f), zc = bulk.Impedance(f);
            Complex y = (1 + a) / zo + 1 / zc;
            Complex localZ = zp[0, 0] - zp[0, 1] * zp[1, 0] * y / (1 + zp[1, 1] * y);
            Complex gain = a * zp[1, 1] / zo / (1 + zp[1, 1] * (1 / zo + 1 / zc));
            // Remote: E = −A·V1, I2 = −(A/Zo)·V1 − (1/Zo + 1/Zc)·V2, V = Zp·(1, I2).
            Complex g = 1 / zo + 1 / zc;
            Complex m11 = 1 + zp[0, 1] * a / zo, m12 = zp[0, 1] * g, m21 = zp[1, 1] * a / zo, m22 = 1 + zp[1, 1] * g;
            Complex remoteZ = (zp[0, 0] * m22 - m12 * zp[1, 0]) / (m11 * m22 - m12 * m21);
            // Both ways take the milliohms at the load as a difference of the planes' kilohms
            // (their capacitance at low frequency): agreement to round-off of the planes' scale.
            double scale = 1e-12 * zp[0, 0].Magnitude;
            worst = Math.Max(worst, (local.Impedance[k] - localZ).Magnitude / localZ.Magnitude);
            Assert.True((local.Impedance[k] - localZ).Magnitude < 1e-9 * localZ.Magnitude + scale, $"local {f:g3} Hz");
            Assert.True((local.LoopGain![k] - gain).Magnitude < 1e-9 * gain.Magnitude, $"loop gain {f:g3} Hz");
            Assert.True((remote.Impedance[k] - remoteZ).Magnitude < 1e-9 * remoteZ.Magnitude + scale, $"remote {f:g3} Hz");
            // Remote loop gain: E = 1 drives I2 = (1/Zo)/(1 + Zp22·(1/Zo + 1/Zc)) into the planes; V1 = Zp12·I2.
            Complex remoteGain = a * zp[0, 1] / zo / (1 + zp[1, 1] * g);
            Assert.True((remote.LoopGain![k] - remoteGain).Magnitude < 1e-9 * remoteGain.Magnitude, $"remote loop gain {f:g3} Hz");
        }
        _output.WriteLine($"closed-loop impedance against the algebra: worst {worst:e2}");
        _output.WriteLine($"at {frequencies[0]:g3} Hz: local sense {local.Impedance[0].Magnitude * 1e3:f4} mΩ, remote {remote.Impedance[0].Magnitude * 1e3:f4} mΩ");

        // The crossover the analysis reports: |T| = 1 there, and the margin is 180° + ∠T. It is
        // interpolated between grid points, so on a band fine enough to follow T through the
        // output filter's resonance.
        Assert.NotEmpty(local.Crossovers);
        var band = PdnAnalysis.LogSweep(0.5 * local.Crossovers[0].FrequencyHz, 2 * local.Crossovers[0].FrequencyHz, 200).ToList();
        var fine = PdnAnalysis.Evaluate(pair, band, pair.Sweep(band), setup);
        foreach (var crossover in fine.Crossovers)
        {
            Complex t = loop.Gain(crossover.FrequencyHz);
            var at = pair.Impedance(crossover.FrequencyHz);
            Complex zo1 = regulator.Impedance(crossover.FrequencyHz), zc1 = bulk.Impedance(crossover.FrequencyHz);
            t = t * at[1, 1] / zo1 / (1 + at[1, 1] * (1 / zo1 + 1 / zc1));
            double margin = 180 + t.Phase * 180 / Math.PI;
            if (margin > 180) margin -= 360;
            _output.WriteLine($"crossover {crossover.FrequencyHz:g4} Hz, margin {crossover.PhaseMarginDegrees:f1}°; |T| there {t.Magnitude:f3}, 180° + ∠T {margin:f1}°");
            Assert.Equal(1, t.Magnitude, 0.05);
            Assert.Equal(margin, crossover.PhaseMarginDegrees, 2);
        }

        Assert.Throws<ArgumentException>(() => PdnAnalysis.Evaluate(pair, frequencies, planes,
            setup with { Regulators = new[] { regulator with { Loop = new RegulatorLoop() } } }));
    }
}
