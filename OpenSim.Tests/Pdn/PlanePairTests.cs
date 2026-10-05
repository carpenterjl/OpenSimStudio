using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Rf.Pdn;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// The plane-pair finite-element solve against solutions that share no code with it: the
/// double-sum cavity model of a rectangular pair with rectangular ports (the same boundary
/// value problem, solved by eigenfunction expansion), the closed-form resonances of a
/// rectangle, and the radial solution of a round port at the centre of a disc.
/// </summary>
public class PlanePairTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Epsilon0 = 8.8541878128e-12;
    private const double C0 = 299792458.0;

    private readonly ITestOutputHelper _output;
    public PlanePairTests(ITestOutputHelper output) => _output = output;

    internal static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    // The reference pair: 100 × 80 mm, 0.2 mm of FR4, 35 µm copper, two 2 mm square ports.
    internal const double A = 0.100, B = 0.080, D = 0.2e-3, Er = 4.4, TanD = 0.02, Sigma = 5.8e7, T = 35e-6;
    internal static readonly (double X, double Y, double Sx, double Sy)[] Ports =
    {
        (0.020, 0.030, 2e-3, 2e-3),
        (0.070, 0.050, 2e-3, 2e-3)
    };

    internal static PlanePairSpec ReferenceSpec(double meshEdge = 0, double maxHz = 1.5e9) => new()
    {
        Shape = new[] { Rect(0, 0, A, B) },
        SeparationMeters = D,
        RelativePermittivity = Er,
        LossTangent = TanD,
        WidebandDielectric = false,
        UpperCopperThicknessMeters = T,
        LowerCopperThicknessMeters = T,
        ConductivitySiemensPerMeter = Sigma,
        Ports = Ports.Select((p, i) => PlanePort.Rectangle($"P{i + 1}", new Point2(p.X, p.Y), p.Sx, p.Sy)).ToList(),
        MeshEdgeMeters = meshEdge,
        MaxFrequencyHz = maxHz
    };

    /// <summary>Series impedance per square and shunt admittance per area, typed here from
    /// the physics and not taken from the solver.</summary>
    internal static (Complex Series, Complex Shunt) Constants(double f)
    {
        double omega = 2 * Math.PI * f;
        Complex k = Complex.Sqrt(new Complex(0, omega * Mu0 * Sigma));
        Complex surface = k / Sigma / Complex.Tanh(k * T);
        return (new Complex(0, omega * Mu0 * D) + 2 * surface,
            new Complex(0, omega * Epsilon0 * Er) * new Complex(1, -TanD) / D);
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;

    /// <summary>
    /// The cavity model: with ψ_mn = c_m·c_n·cos(mπx/a)·cos(nπy/b)/√(ab) the open-edge modes
    /// of the rectangle, Z_ij = Σ Z_sq·⟨ψ_mn⟩_i·⟨ψ_mn⟩_j/(k_mn² + Z_sq·Y), where ⟨·⟩ is the
    /// mean over the port's rectangle — a product of two sinc factors.
    /// </summary>
    internal static Complex Cavity(double f, int i, int j, int terms = 2500)
    {
        var (series, shunt) = Constants(f);
        Complex gamma2 = series * shunt;
        var pi = Ports[i]; var pj = Ports[j];
        var xs = new double[terms + 1];
        var ys = new double[terms + 1];
        for (int m = 0; m <= terms; m++)
        {
            double km = m * Math.PI / A;
            xs[m] = (m == 0 ? 1 : 2) * Math.Cos(km * pi.X) * Math.Cos(km * pj.X) * Sinc(km * pi.Sx / 2) * Sinc(km * pj.Sx / 2);
            double kn = m * Math.PI / B;
            ys[m] = (m == 0 ? 1 : 2) * Math.Cos(kn * pi.Y) * Math.Cos(kn * pj.Y) * Sinc(kn * pi.Sy / 2) * Sinc(kn * pj.Sy / 2);
        }
        Complex sum = Complex.Zero;
        for (int m = terms; m >= 0; m--)
        {
            double km2 = Math.Pow(m * Math.PI / A, 2);
            Complex row = Complex.Zero;
            for (int n = terms; n >= 0; n--)
                row += ys[n] / (km2 + Math.Pow(n * Math.PI / B, 2) + gamma2);
            sum += xs[m] * row;
        }
        return series * sum / (A * B);
    }

    [Fact]
    public void BandedLuSolvesAPivotingSystem()
    {
        // A pentadiagonal complex matrix with a zero diagonal entry: needs the interchange.
        int n = 40, b = 2;
        var dense = new Complex[n, n];
        var random = new Random(7);
        for (int i = 0; i < n; i++)
            for (int j = Math.Max(0, i - b); j <= Math.Min(n - 1, i + b); j++)
                dense[i, j] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        dense[0, 0] = 0; dense[7, 7] = 0;
        var lu = new BandedComplexLu(n, b);
        for (int i = 0; i < n; i++)
            for (int j = Math.Max(0, i - b); j <= Math.Min(n - 1, i + b); j++)
                lu.Add(i, j, dense[i, j]);
        lu.Factor();
        var truth = Enumerable.Range(0, n).Select(i => new Complex(Math.Sin(i), Math.Cos(2 * i))).ToArray();
        var rhs = new Complex[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) rhs[i] += dense[i, j] * truth[j];
        lu.Solve(rhs);
        for (int i = 0; i < n; i++)
            Assert.True((rhs[i] - truth[i]).Magnitude < 1e-10, $"x[{i}] off by {(rhs[i] - truth[i]).Magnitude:e2}");
    }

    [Fact]
    public void LowFrequencyIsThePlateCapacitance()
    {
        var pair = new PlanePair(ReferenceSpec());
        double f = 1e5;
        var z = pair.Impedance(f);
        double expected = Epsilon0 * Er * A * B / D;
        Assert.Equal(expected, pair.StaticCapacitanceFarads, expected * 1e-6);
        // Z = 1/(jωC(1 − j tanδ)): the reactance gives C, the real part tan δ.
        Complex y = 1 / z[0, 0];
        double c = y.Imaginary / (2 * Math.PI * f);
        _output.WriteLine($"C from Z11 at 100 kHz: {c * 1e9:f4} nF, plate {expected * 1e9:f4} nF; nodes {pair.NodeCount}");
        Assert.Equal(expected, c, expected * 2e-3);
        Assert.Equal(TanD, y.Real / y.Imaginary, 2e-3);
        Assert.True((z[0, 1] - z[0, 0]).Magnitude < 0.01 * z[0, 0].Magnitude);
    }

    public static IEnumerable<object[]> OffResonance() => new[]
    {
        new object[] { 1e6 }, new object[] { 3e7 }, new object[] { 2e8 }, new object[] { 5e8 },
        new object[] { 8e8 }, new object[] { 1.05e9 }, new object[] { 1.3e9 }
    };

    [Theory]
    [MemberData(nameof(OffResonance))]
    public void SelfAndTransferImpedanceFollowTheCavityModel(double f)
    {
        var pair = new PlanePair(ReferenceSpec());
        var z = pair.Impedance(f);
        foreach (var (i, j) in new[] { (0, 0), (1, 1), (0, 1) })
        {
            Complex exact = Cavity(f, i, j);
            double error = (z[i, j] - exact).Magnitude / exact.Magnitude;
            _output.WriteLine($"{f / 1e6,7:f1} MHz Z{i + 1}{j + 1}: FEM {z[i, j].Magnitude:e4} ∠{z[i, j].Phase * 180 / Math.PI:f2}°, " +
                              $"cavity {exact.Magnitude:e4} ∠{exact.Phase * 180 / Math.PI:f2}°, error {error:p2}");
            Assert.True(error < 0.015, $"Z{i + 1}{j + 1} at {f / 1e6:g4} MHz differs from the cavity model by {error:p2}");
        }
        Assert.True((z[0, 1] - z[1, 0]).Magnitude <= 1e-12 * z[0, 1].Magnitude);
    }

    [Fact]
    public void FirstResonancePeakMatchesTheCavityModel()
    {
        // The (1,0) resonance near 715 MHz, seen in the transfer impedance: where it peaks
        // and how high, on a 1 MHz grid, in both solutions.
        var pair = new PlanePair(ReferenceSpec());
        var grid = Enumerable.Range(0, 61).Select(k => 680e6 + k * 1e6).ToList();
        var fem = pair.Sweep(grid);
        double bestFem = 0, atFem = 0, bestExact = 0, atExact = 0;
        for (int k = 0; k < grid.Count; k++)
        {
            double m = fem[k][0, 1].Magnitude;
            if (m > bestFem) { bestFem = m; atFem = grid[k]; }
            double e = Cavity(grid[k], 0, 1, 1200).Magnitude;
            if (e > bestExact) { bestExact = e; atExact = grid[k]; }
        }
        _output.WriteLine($"peak |Z21|: FEM {bestFem:f4} Ω at {atFem / 1e6:f0} MHz, cavity {bestExact:f4} Ω at {atExact / 1e6:f0} MHz");
        Assert.True(Math.Abs(atFem - atExact) <= 1e6);
        Assert.True(Math.Abs(bestFem - bestExact) <= 0.01 * bestExact);
    }

    [Fact]
    public void ResonancesOfARectangle()
    {
        var pair = new PlanePair(ReferenceSpec());
        var modes = pair.Modes(5);
        _output.WriteLine($"{pair.NodeCount} unknowns, half-bandwidth {pair.HalfBandwidth}");
        var exact = new List<double>();
        for (int m = 0; m <= 4; m++)
            for (int n = 0; n <= 4; n++)
                if (m + n > 0)
                    exact.Add(C0 / (2 * Math.Sqrt(Er)) * Math.Sqrt(m * m / (A * A) + n * n / (B * B)));
        exact.Sort();
        Assert.Equal(5, modes.Count);
        for (int k = 0; k < 5; k++)
        {
            _output.WriteLine($"mode {k + 1}: FEM {modes[k].FrequencyHz / 1e6:f2} MHz, exact {exact[k] / 1e6:f2} MHz, " +
                              $"port coupling {string.Join(", ", modes[k].PortCoupling.Select(c => c.ToString("f2")))}");
            Assert.Equal(exact[k], modes[k].FrequencyHz, exact[k] * 2e-4);
        }
        // Mode (1,0) is cos(πx/a): port 1 at x = 20 mm sees cos(0.2π) = 0.81 of the peak.
        Assert.Equal(Math.Cos(Math.PI * 0.020 / A), modes[0].PortCoupling[0], 0.02);
    }

    [Theory]
    [InlineData(0.15e-3)]
    [InlineData(0.5e-3)]
    public void RoundPortAtTheCentreOfADisc(double portRadius)
    {
        // A lossless disc of radius R with a round port of radius r₀ at its centre. To
        // first order in frequency the port current flows out radially and leaves as
        // displacement current uniformly over the disc, K_r·2πr = min(r, r₀)²/r₀² − r²/R²,
        // and Z = 1/(jωC) + jωµ₀d·g with g = ⟨F⟩_disc − ⟨F⟩_port, F(r) = ∫₀ʳ K_r.
        double radius = 10e-3, d = 0.1e-3;
        int sides = 240;
        var ring = Enumerable.Range(0, sides).Select(i =>
            new Point2(radius * Math.Cos(2 * Math.PI * i / sides), radius * Math.Sin(2 * Math.PI * i / sides))).ToList();
        var pair = new PlanePair(new PlanePairSpec
        {
            Shape = new[] { new Polygon2(ring) },
            SeparationMeters = d,
            RelativePermittivity = Er,
            LossTangent = 0,
            ConductivitySiemensPerMeter = double.PositiveInfinity,
            Ports = new[] { new PlanePort("via", new Point2(0, 0), portRadius) },
            MeshEdgeMeters = 0.5e-3
        });

        double r0 = portRadius, R = radius;
        double fAtPort = (1 - r0 * r0 / (R * R)) / (4 * Math.PI);
        double meanPort = (1 - r0 * r0 / (R * R)) / (8 * Math.PI);
        double inner = r0 * r0 * (1 - r0 * r0 / (R * R)) / (16 * Math.PI);
        double outer = fAtPort * (R * R - r0 * r0) / 2
            + (R * R / 2 * Math.Log(R / r0) - (R * R - r0 * r0) / 4
               - Math.Pow(R * R - r0 * r0, 2) / (8 * R * R)) / (2 * Math.PI);
        double g = 2 / (R * R) * (inner + outer) - meanPort;
        double expected = Mu0 * d * g;

        double f = 20e6, omega = 2 * Math.PI * f;
        var z = pair.Impedance(f)[0, 0];
        double capacitance = Epsilon0 * Er * pair.AreaSquareMeters / d;
        double inductance = (z.Imaginary + 1 / (omega * capacitance)) / omega;
        _output.WriteLine($"r0 {portRadius * 1e3:g3} mm: L {inductance * 1e12:f3} pH, radial solution {expected * 1e12:f3} pH " +
                          $"({(inductance / expected - 1):p2}); nodes {pair.NodeCount}");
        Assert.Equal(expected, inductance, expected * 0.005);
    }

    [Fact]
    public void ACutOutRaisesTheTransferInductance()
    {
        // A slot across the path between the ports: the current has to go round it.
        var solid = new PlanePair(ReferenceSpec());
        var slotted = new PlanePair(ReferenceSpec() with
        {
            Shape = new[] { new Polygon2(Rect(0, 0, A, B).Outer,
                new IReadOnlyList<Point2>[] { new[] { new Point2(0.044, 0.010), new Point2(0.044, 0.070), new Point2(0.046, 0.070), new Point2(0.046, 0.010) } }) }
        });
        double f = 50e6;
        Complex Loop(PlanePair p)
        {
            var z = p.Impedance(f);
            return z[0, 0] + z[1, 1] - 2 * z[0, 1];           // port 1 to port 2 through the planes
        }
        double lSolid = Loop(solid).Imaginary / (2 * Math.PI * f), lSlot = Loop(slotted).Imaginary / (2 * Math.PI * f);
        _output.WriteLine($"port-to-port loop inductance: solid {lSolid * 1e12:f1} pH, with slot {lSlot * 1e12:f1} pH");
        Assert.True(lSlot > 1.15 * lSolid);
    }

    [Fact]
    public void APortOffThePlaneIsRefused()
    {
        var spec = ReferenceSpec() with { Ports = new[] { PlanePort.Rectangle("off", new Point2(0.0995, 0.04), 2e-3, 2e-3) } };
        var ex = Assert.Throws<InvalidOperationException>(() => new PlanePair(spec));
        Assert.Contains("not wholly on the plane pair", ex.Message);
    }
}
