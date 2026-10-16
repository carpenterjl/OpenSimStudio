using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Rf.Pdn;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// The multi-plane stack (FU-32): with lossless plates its cavities are independent pairs, so the
/// stack must give exactly what <see cref="PlanePair"/> gives for each; at low frequency every
/// arrangement of plates, holes and stitching vias is a network of plate capacitors whose value
/// can be written down; a via tied at a point must have the inductance of the same via tied
/// through its patch; and plates of different outlines leave the cavity model of their overlap.
/// </summary>
public class PlaneStackTests
{
    private const double Epsilon0 = 8.8541878128e-12;

    private readonly ITestOutputHelper _output;
    public PlaneStackTests(ITestOutputHelper output) => _output = output;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) => PlanePairTests.Rect(x0, y0, x1, y1);

    private static readonly PlanePort Site1 = PlanePort.Rectangle("S1", new Point2(0.020, 0.030), 2e-3, 2e-3);
    private static readonly PlanePort Site2 = PlanePort.Rectangle("S2", new Point2(0.070, 0.050), 2e-3, 2e-3);

    private static StackLayer Layer(string name, IReadOnlyList<Polygon2> shape, double gap = 0, double er = 4.4,
        double tan = 0.02, double copper = 35e-6) => new()
    {
        Name = name, Plates = new[] { new StackPlate(name, shape) }, GapBelowMeters = gap,
        GapPermittivity = er, GapLossTangent = tan, CopperThicknessMeters = copper
    };

    private static PlanePairSpec Pair(IReadOnlyList<Polygon2> shape, double d, double er, double sigma, double mesh) => new()
    {
        Shape = shape, SeparationMeters = d, RelativePermittivity = er, LossTangent = 0.02, WidebandDielectric = false,
        ConductivitySiemensPerMeter = sigma, Ports = new[] { Site1, Site2 }, MeshEdgeMeters = mesh
    };

    private static double Worst(Complex[,] a, Complex[,] b)
    {
        double worst = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude / Math.Max(b[i, j].Magnitude, 1e-300));
        return worst;
    }

    [Fact]
    public void LosslessPlates_AreIndependentCavities()
    {
        // Three plates of one outline, perfect conductors: a port from the top to the bottom plate
        // sees the two cavities in series, a port in one cavity sees that cavity alone, and a port
        // in the other cavity sees nothing of it.
        var shape = new[] { Rect(0, 0, 0.100, 0.080) };
        double d1 = 0.2e-3, d2 = 0.5e-3, h = 4e-3;
        PlaneStackSpec Stack(params StackPort[] ports) => new()
        {
            Layers = new[] { Layer("t", shape, d1, 4.4), Layer("m", shape, d2, 3.5), Layer("b", shape) },
            Ports = ports, WidebandDielectric = false, ConductivitySiemensPerMeter = double.PositiveInfinity,
            MeshEdgeMeters = h
        };
        // The band solve, as the pairs have: the identity is the assembly's, to round-off (the sparse
        // solve has its own test against the band one).
        var through = new PlaneStack(Stack(new StackPort(Site1, 0, 2), new StackPort(Site2, 0, 2)), sparse: false);
        var mixed = new PlaneStack(Stack(new StackPort(Site1, 0, 1), new StackPort(Site2, 1, 2)), sparse: false);
        var upper = new PlanePair(Pair(shape, d1, 4.4, double.PositiveInfinity, h));
        var lower = new PlanePair(Pair(shape, d2, 3.5, double.PositiveInfinity, h));
        _output.WriteLine($"{through.NodeCount} nodes, {through.UnknownCount} unknowns, half-bandwidth {through.HalfBandwidth}");
        Assert.Equal(2 * upper.NodeCount, through.UnknownCount);

        foreach (double f in new[] { 1e6, 3e8, 7.1e8, 1.2e9 })
        {
            var zu = upper.Impedance(f);
            var zl = lower.Impedance(f);
            var sum = new Complex[2, 2];
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) sum[i, j] = zu[i, j] + zl[i, j];
            var zt = through.Impedance(f);
            var zm = mixed.Impedance(f);
            _output.WriteLine($"{f / 1e6:g4} MHz: through {Worst(zt, sum):e2} off the sum of the pairs; mixed Z11 " +
                              $"{(zm[0, 0] - zu[0, 0]).Magnitude / zu[0, 0].Magnitude:e2}, Z22 {(zm[1, 1] - zl[1, 1]).Magnitude / zl[1, 1].Magnitude:e2}, " +
                              $"|Z12| {zm[0, 1].Magnitude:e2} Ω");
            Assert.True(Worst(zt, sum) < 1e-9);
            Assert.True((zm[0, 0] - zu[0, 0]).Magnitude < 1e-9 * zu[0, 0].Magnitude);
            Assert.True((zm[1, 1] - zl[1, 1]).Magnitude < 1e-9 * zl[1, 1].Magnitude);
            Assert.True(zm[0, 1].Magnitude < 1e-9 * zu[0, 0].Magnitude);
        }

        // The resonances are the two pairs' together.
        var modes = through.Modes(6).Select(m => m.FrequencyHz).ToList();
        var pairs = upper.Modes(6).Concat(lower.Modes(6)).Select(m => m.FrequencyHz).OrderBy(f => f).Take(6).ToList();
        for (int k = 0; k < 6; k++)
        {
            _output.WriteLine($"mode {k + 1}: stack {modes[k] / 1e6:f3} MHz, pairs {pairs[k] / 1e6:f3} MHz");
            Assert.Equal(pairs[k], modes[k], 1e-6 * pairs[k]);
        }
    }

    [Fact]
    public void TheSparseSolve_AgreesWithTheBandSolve()
    {
        // Three plates, two ports, lossy: the nested-dissection LDLᵀ must give what the pivoting band
        // LU gives, through the resonances and above them. Lower down both carry the round-off of
        // the static modes (the stiffness dwarfs the capacitance as 1/f²), and differ by it.
        var shape = new[] { Rect(0, 0, 0.100, 0.080) };
        var spec = new PlaneStackSpec
        {
            Layers = new[] { Layer("t", shape, 0.2e-3, 4.4), Layer("m", shape, 0.5e-3, 3.5), Layer("b", shape) },
            Ports = new[] { new StackPort(Site1, 0, 2), new StackPort(Site2, 1, 0) },
            MeshEdgeMeters = 4e-3
        };
        var sparse = new PlaneStack(spec, sparse: true);
        var band = new PlaneStack(spec, sparse: false);
        _output.WriteLine($"{sparse.UnknownCount} unknowns: half-bandwidth {sparse.HalfBandwidth}, the sparse factor holds {sparse.FactorNonZeros} entries");
        Assert.True(sparse.SparseSolve);
        Assert.False(band.SparseSolve);
        foreach (var (f, tolerance) in new[] { (1e5, 1e-4), (1e7, 1e-8), (3e7, 1e-8), (7.1e8, 1e-8), (1.2e9, 1e-8) })
        {
            var a = sparse.Impedance(f);
            var b = band.Impedance(f);
            double worst = 0, scale = 0;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
                    scale = Math.Max(scale, b[i, j].Magnitude);
                }
            _output.WriteLine($"{f / 1e6:g4} MHz: largest difference {worst / scale:e2} of the largest entry");
            Assert.True(worst < tolerance * scale);
        }
    }

    [Fact]
    public void AThickSharedPlate_Shields_AThinOneCouples()
    {
        // The middle plate carries one cavity's return on its top face and the other's current
        // on its bottom face; through a plate many skin depths thick nothing passes.
        var shape = new[] { Rect(0, 0, 0.100, 0.080) };
        Complex Transfer(double middle, double f)
        {
            var stack = new PlaneStack(new PlaneStackSpec
            {
                Layers = new[] { Layer("t", shape, 0.2e-3), Layer("m", shape, 0.2e-3, copper: middle), Layer("b", shape) },
                Ports = new[] { new StackPort(Site1, 0, 1), new StackPort(Site2, 1, 2) },
                WidebandDielectric = false, MeshEdgeMeters = 5e-3
            });
            var z = stack.Impedance(f);
            return z[0, 1] / Complex.Sqrt(z[0, 0] * z[1, 1]);
        }
        // At 10 MHz the skin depth in copper is 21 µm.
        double thin = Transfer(5e-6, 1e7).Magnitude, normal = Transfer(35e-6, 1e7).Magnitude, thick = Transfer(300e-6, 1e7).Magnitude;
        _output.WriteLine($"normalised transfer at 10 MHz: 5 µm {thin:e3}, 35 µm {normal:e3}, 300 µm {thick:e3}");
        Assert.True(thin > normal && normal > thick);
        Assert.True(thick < 1e-3 * thin);
        // At 100 kHz (δ = 206 µm) even 35 µm couples nearly as much as 5 µm does at that frequency.
        double low5 = Transfer(5e-6, 1e5).Magnitude, low35 = Transfer(35e-6, 1e5).Magnitude;
        _output.WriteLine($"at 100 kHz: 5 µm {low5:e3}, 35 µm {low35:e3}");
        Assert.True(low35 > 0.1 * low5);
    }

    [Fact]
    public void AtLowFrequency_TheStackIsItsPlateCapacitors()
    {
        // At 100 kHz every plate is equipotential and the port sees the capacitance network (lower,
        // the solve's round-off grows as 1/f²; the plates' inductance is still 10⁻⁷ of it here).
        var full = new[] { Rect(0, 0, 0.100, 0.080) };
        var holed = new[] { new Polygon2(Rect(0, 0, 0.100, 0.080).Outer,
            new[] { Rect(0.030, 0.020, 0.060, 0.050).Outer.Reverse().ToList() }) };
        double d1 = 0.2e-3, d2 = 0.3e-3, tm = 35e-6, e1 = 4.4, e2 = 3.8, area = 0.100 * 0.080, hole = 0.030 * 0.030;
        double f = 1e5, omega = 2 * Math.PI * f;
        double Capacitance(PlaneStack stack) => (1 / stack.Impedance(f)[0, 0]).Imaginary / omega;

        // A hole in the floating middle plate: over the hole the outer plates face each other
        // across both dielectrics and the middle layer's thickness (filled with resin, the mean of
        // the dielectrics either side), elsewhere they see the middle plate's two capacitors in series.
        var holeInMiddle = new PlaneStack(new PlaneStackSpec
        {
            Layers = new[] { Layer("t", full, d1, e1, 0), Layer("m", holed, d2, e2, 0, tm), Layer("b", full) },
            Ports = new[] { new StackPort(Site1, 0, 2) }, WidebandDielectric = false, MeshEdgeMeters = 5e-3
        });
        double c1 = Epsilon0 * e1 * (area - hole) / d1, c2 = Epsilon0 * e2 * (area - hole) / d2;
        double expected = Epsilon0 / (d1 / e1 + d2 / e2 + tm / (0.5 * (e1 + e2))) * hole + c1 * c2 / (c1 + c2);
        double got = Capacitance(holeInMiddle);
        _output.WriteLine($"hole in the middle plate: {got * 1e12:f4} pF, plate capacitors {expected * 1e12:f4} pF; {holeInMiddle.UnknownCount} unknowns");
        Assert.Equal(expected, got, 1e-6 * expected);

        // A hole in the floating bottom plate, port between the top two: the bottom plate takes
        // the middle one's potential and adds nothing (and over the hole the reference plate of
        // the unknowns changes from the bottom plate to the middle one).
        var holeInBottom = new PlaneStack(new PlaneStackSpec
        {
            Layers = new[] { Layer("t", full, d1, e1, 0), Layer("m", full, d2, e2, 0), Layer("b", holed) },
            Ports = new[] { new StackPort(Site1, 0, 1) }, WidebandDielectric = false, MeshEdgeMeters = 5e-3
        });
        expected = Epsilon0 * e1 * area / d1;
        got = Capacitance(holeInBottom);
        _output.WriteLine($"hole in the bottom plate: {got * 1e12:f4} pF, top cavity {expected * 1e12:f4} pF");
        Assert.Equal(expected, got, 1e-6 * expected);

        // Stitching vias tie the outer plates: the middle plate sees both cavities in parallel.
        var ties = new List<StackTie>();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 3; j++)
                ties.Add(new StackTie(new Point2(0.012 + 0.025 * i, 0.012 + 0.027 * j), 0.15e-3, 0, 2) { Name = $"V{i}{j}" });
        var tied = new PlaneStack(new PlaneStackSpec
        {
            Layers = new[] { Layer("t", full, d1, e1, 0), Layer("m", full, d2, e2, 0), Layer("b", full) },
            Ports = new[] { new StackPort(Site1, 1, 0) }, Ties = ties, WidebandDielectric = false, MeshEdgeMeters = 5e-3
        });
        expected = Epsilon0 * (e1 / d1 + e2 / d2) * area;
        got = Capacitance(tied);
        _output.WriteLine($"outer plates tied by {ties.Count} vias: {got * 1e12:f4} pF, both cavities {expected * 1e12:f4} pF");
        foreach (string note in tied.Notes) _output.WriteLine("  " + note);
        Assert.Equal(expected, got, 1e-5 * expected);
    }

    [Fact]
    public void AViaTiedAtAPoint_HasTheInductanceOfTheSameViaTiedThroughItsPatch()
    {
        // Current enters the bottom plate at S1 and leaves the top one there; a stitching via at S2
        // joins the two, so at 1 MHz the port sees the loop's inductance, the via's own included.
        // Tied at a point the via is a barrel of radius r (its near field below the mesh added in
        // series); tied through a port's patch it is the patch's uniform current, whose mean
        // potential is that of a barrel of radius r·e^(−1/4).
        var full = new[] { Rect(0, 0, 0.100, 0.080) };
        double r = 0.25e-3;
        var round2 = new PlanePort("S2", Site2.Center, r);
        double Inductance(double h, bool patch)
        {
            var spec = new PlaneStackSpec
            {
                Layers = new[] { Layer("t", full, 0.2e-3, tan: 0), Layer("m", full, 0.3e-3, tan: 0), Layer("b", full) },
                Ports = patch
                    ? new[] { new StackPort(Site1, 2, 0), new StackPort(round2, 2, 0) }
                    : new[] { new StackPort(Site1, 2, 0) },
                Ties = new[] { new StackTie(Site2.Center, patch ? r : r * Math.Exp(-0.25), 0, 2) { Name = "V" } },
                WidebandDielectric = false, MeshEdgeMeters = h
            };
            var stack = new PlaneStack(spec);
            var z = stack.Impedance(1e6)[0, 0];
            return z.Imaginary / (2 * Math.PI * 1e6);
        }
        foreach (double h in new[] { 5e-3, 3.5e-3 })
        {
            double point = Inductance(h, patch: false), patch = Inductance(h, patch: true);
            _output.WriteLine($"h {h * 1e3} mm: tied at a point {point * 1e12:f2} pH, through its patch {patch * 1e12:f2} pH ({point / patch - 1:p2})");
            Assert.Equal(patch, point, 0.01 * patch);
        }
    }

    [Fact]
    public void PlatesOfDifferentOutlines_AreTheCavityOfTheirOverlap()
    {
        // A ground plane larger than the power plane: the cavity is the power plane's rectangle,
        // and the cavity model of that rectangle holds (the overhang carries no field here).
        var power = new[] { Rect(0, 0, PlanePairTests.A, PlanePairTests.B) };
        var ground = new[] { Rect(-0.010, -0.015, PlanePairTests.A + 0.020, PlanePairTests.B + 0.005) };
        var reference = PlanePairTests.ReferenceSpec();
        var stack = new PlaneStack(new PlaneStackSpec
        {
            Layers = new[]
            {
                Layer("power", power, PlanePairTests.D, PlanePairTests.Er, PlanePairTests.TanD, PlanePairTests.T),
                Layer("ground", ground, copper: PlanePairTests.T)
            },
            Ports = reference.Ports.Select(p => new StackPort(p, 0, 1)).ToList(),
            WidebandDielectric = false, ConductivitySiemensPerMeter = PlanePairTests.Sigma, MaxFrequencyHz = 1.5e9
        });
        // The mesher drops sub-degree slivers at the boundary: a few parts in 10⁷ of the area.
        Assert.Equal(PlanePairTests.A * PlanePairTests.B, stack.AreaSquareMeters, 1e-6 * PlanePairTests.A * PlanePairTests.B);
        foreach (double f in new[] { 3e7, 2e8, 8e8 })
        {
            var z = stack.Impedance(f);
            for (int i = 0; i < 2; i++)
                for (int j = i; j < 2; j++)
                {
                    var exact = PlanePairTests.Cavity(f, i, j);
                    double error = (z[i, j] - exact).Magnitude / exact.Magnitude;
                    _output.WriteLine($"{f / 1e6:g4} MHz Z{i + 1}{j + 1}: {z[i, j].Magnitude:e4} Ω, cavity model {exact.Magnitude:e4} Ω ({error:p3})");
                    Assert.True(error < 0.01);
                }
        }
    }
}
