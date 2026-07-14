using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The MTL network gates (SI Stage S4). The chain matrix comes from expm, so the
/// exponential is gated first (scalar/rotation/inverse/scaled identities at 1e-12);
/// the single lossless line is then EXACT against the textbook ABCD/Zin/matched forms;
/// the coupled solve is pinned by the even/odd machine identity, reciprocity,
/// lossless energy conservation, the cascade identity, and the weak-coupling
/// NEXT/FEXT first-order closed forms (banded — they are first-order in coupling).
/// </summary>
public class MtlNetworkTests
{
    private const double C0 = 299792458.0;

    // ------------------------------------------------------------------
    // Matrix exponential.
    // ------------------------------------------------------------------

    [Fact]
    public void Expm_ScalarRotationInverseAndScaling()
    {
        // Scalar: expm([[z]]) = e^z.
        var scalar = new ComplexDenseMatrix(1, 1);
        scalar[0, 0] = new Complex(0.3, -1.7);
        Assert.True((ComplexMatrixExponential.Exponential(scalar)[0, 0]
                     - Complex.Exp(scalar[0, 0])).Magnitude < 1e-14);

        // Rotation generator (large norm exercises the scaling-and-squaring path).
        foreach (double theta in new[] { 0.7, 50.0 })
        {
            var g = new ComplexDenseMatrix(2, 2);
            g[0, 1] = -theta;
            g[1, 0] = theta;
            var r = ComplexMatrixExponential.Exponential(g);
            Assert.True((r[0, 0] - Math.Cos(theta)).Magnitude < 1e-12);
            Assert.True((r[0, 1] + Math.Sin(theta)).Magnitude < 1e-12);
            Assert.True((r[1, 0] - Math.Sin(theta)).Magnitude < 1e-12);
        }

        // expm(A)·expm(−A) = I for a random-ish dense complex A.
        var a = new ComplexDenseMatrix(4, 4);
        var minus = new ComplexDenseMatrix(4, 4);
        var rng = new Random(11);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                a[i, j] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                minus[i, j] = -a[i, j];
            }
        var product = ComplexMatrixExponential.Multiply(
            ComplexMatrixExponential.Exponential(a), ComplexMatrixExponential.Exponential(minus));
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((product[i, j] - (i == j ? Complex.One : Complex.Zero)).Magnitude < 1e-12);
    }

    // ------------------------------------------------------------------
    // Fixtures: hand-built RLGC results (the network layer is independent of the
    // extractor — these are exact ideal lines).
    // ------------------------------------------------------------------

    private static RlgcResult SingleLine(double l, double c, double rdc = 0)
        => new(1,
            new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { rdc }, new[] { 0.0 }, Array.Empty<string>());

    private static RlgcResult CoupledPair(double l, double lm, double c, double cm)
        => new(2,
            new[,] { { c, -cm }, { -cm, c } }, new double[2, 2], new[,] { { c, -cm }, { -cm, c } },
            new[,] { { l, lm }, { lm, l } }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 },
            Array.Empty<string>());

    // 50 Ω air line: Z0 = √(L/C), v = 1/√(LC) = c₀.
    private static readonly double AirL = 50.0 / C0;
    private static readonly double AirC = 1.0 / (50.0 * C0);

    // ------------------------------------------------------------------
    // Single lossless line: exact textbook identities.
    // ------------------------------------------------------------------

    [Fact]
    public void SingleLine_ChainMatrixIsTheTextbookAbcd()
    {
        const double length = 0.05;
        const double f = 2e9;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var t = network.ChainMatrix(f);
        double beta = 2 * Math.PI * f / C0, bl = beta * length;
        Assert.True((t[0, 0] - Math.Cos(bl)).Magnitude < 1e-12);
        Assert.True((t[0, 1] - new Complex(0, 50 * Math.Sin(bl))).Magnitude < 1e-10);
        Assert.True((t[1, 0] - new Complex(0, Math.Sin(bl) / 50)).Magnitude < 1e-14);
        Assert.True((t[1, 1] - Math.Cos(bl)).Magnitude < 1e-12);
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(100.0)]
    [InlineData(double.PositiveInfinity)]
    public void SingleLine_TerminatedInputImpedance_MatchesTheClosedForm(double loadOhms)
    {
        const double length = 0.03, f = 1.7e9;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var solution = network.SolveTerminated(f,
            new[] { new LineTermination(50, loadOhms) }, new[] { Complex.One });
        var zin = solution.NearVoltages[0] / solution.NearCurrents[0];

        double bl = 2 * Math.PI * f / C0 * length;
        Complex tan = Math.Tan(bl);
        Complex expected = double.IsPositiveInfinity(loadOhms)
            ? 50 / (Complex.ImaginaryOne * tan)
            : 50 * (loadOhms + Complex.ImaginaryOne * 50 * tan)
              / (50 + Complex.ImaginaryOne * loadOhms * tan);
        Assert.True((zin - expected).Magnitude / expected.Magnitude < 1e-10,
            $"Zin {zin} vs closed form {expected}");
    }

    [Fact]
    public void MatchedLine_IsReflectionlessWithExactPhaseDelay()
    {
        const double length = 0.08, f = 3e9;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var s = network.Scattering(f);
        double bl = 2 * Math.PI * f / C0 * length;
        Assert.True(s[0, 0].Magnitude < 1e-10, $"|S11| = {s[0, 0].Magnitude:g3}");
        Assert.True((s[1, 0] - Complex.Exp(new Complex(0, -bl))).Magnitude < 1e-10,
            "S21 must be the pure delay e^{−jβl}");
    }

    // ------------------------------------------------------------------
    // Coupled identities.
    // ------------------------------------------------------------------

    [Fact]
    public void SymmetricPair_EvenOddDecomposition_IsExact()
    {
        const double length = 0.04, f = 2.5e9;
        // A mildly coupled, slightly inhomogeneous-like pair (Lm/L ≠ Cm/C).
        double l = 3.5e-7, lm = 6e-8, c = 1.3e-10, cm = 1.5e-11;
        var pair = new MtlNetwork(new[] { new MtlSection(CoupledPair(l, lm, c, cm), length) });
        var even = new MtlNetwork(new[] { new MtlSection(SingleLine(l + lm, c - cm), length) });
        var odd = new MtlNetwork(new[] { new MtlSection(SingleLine(l - lm, c + cm), length) });

        var terms = new[] { new LineTermination(40, 60), new LineTermination(40, 60) };
        var singleTerm = new[] { new LineTermination(40, 60) };

        var evenDrive = pair.SolveTerminated(f, terms, new[] { Complex.One, Complex.One });
        var evenRef = even.SolveTerminated(f, singleTerm, new[] { Complex.One });
        Assert.True((evenDrive.FarVoltages[0] - evenRef.FarVoltages[0]).Magnitude
                    / evenRef.FarVoltages[0].Magnitude < 1e-10, "even mode");
        Assert.True((evenDrive.FarVoltages[0] - evenDrive.FarVoltages[1]).Magnitude
                    / evenRef.FarVoltages[0].Magnitude < 1e-12, "even symmetry");

        var oddDrive = pair.SolveTerminated(f, terms, new[] { Complex.One, -Complex.One });
        var oddRef = odd.SolveTerminated(f, singleTerm, new[] { Complex.One });
        Assert.True((oddDrive.FarVoltages[0] - oddRef.FarVoltages[0]).Magnitude
                    / oddRef.FarVoltages[0].Magnitude < 1e-10, "odd mode");
    }

    [Fact]
    public void Scattering_IsReciprocal_AndLosslessEnergyConserving()
    {
        const double length = 0.06, f = 4e9;
        var pair = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), length) });
        var s = pair.Scattering(f);
        for (int i = 0; i < 4; i++)
        {
            double columnPower = 0;
            for (int j = 0; j < 4; j++)
            {
                Assert.True((s[i, j] - s[j, i]).Magnitude < 1e-10, "reciprocity S = Sᵀ");
                columnPower += s[j, i].Magnitude * s[j, i].Magnitude;
            }
            Assert.True(Math.Abs(columnPower - 1) < 1e-10,
                $"lossless column power Σ|S|² = {columnPower:R} ≠ 1");
        }
    }

    [Fact]
    public void Cascade_TwoHalvesEqualTheWhole()
    {
        const double f = 3.3e9;
        var rlgc = CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11);
        var whole = new MtlNetwork(new[] { new MtlSection(rlgc, 0.05) });
        var halves = new MtlNetwork(new[]
            { new MtlSection(rlgc, 0.025), new MtlSection(rlgc, 0.025) });
        var a = whole.Scattering(f);
        var b = halves.Scattering(f);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-12, "cascade identity");
    }

    [Fact]
    public void WeakCoupling_MatchesFirstOrderNextFextForms()
    {
        // First-order closed forms for a matched weakly coupled pair:
        // NEXT = kb·(1 − e^{−2jβl}), kb = (Lm/L + Cm/C)/4;
        // FEXT = −(jβl/2)·(Lm/L − Cm/C)·e^{−jβl}.
        // First-order in coupling ⇒ a 10% band at ~3% coupling, not machine.
        double l = 3.5e-7, c = 1.3e-10;
        double lm = 0.03 * l, cm = 0.02 * c;
        const double length = 0.02;
        double z0 = Math.Sqrt(l / c);
        const double f = 1.5e9;

        var pair = new MtlNetwork(new[] { new MtlSection(CoupledPair(l, lm, c, cm), length) });
        var s = pair.Scattering(f, z0);

        double beta = 2 * Math.PI * f * Math.Sqrt(l * c);
        double bl = beta * length;
        Complex delay = Complex.Exp(new Complex(0, -bl));
        Complex next = (lm / l + cm / c) / 4 * (1 - delay * delay);
        Complex fext = new Complex(0, -bl / 2) * (lm / l - cm / c) * delay;

        Assert.True((s[1, 0] - next).Magnitude / next.Magnitude < 0.10,
            $"NEXT {s[1, 0]} vs first-order {next}");
        Assert.True((s[3, 0] - fext).Magnitude / fext.Magnitude < 0.10,
            $"FEXT {s[3, 0]} vs first-order {fext}");
    }

    // ------------------------------------------------------------------
    // Receiver R∥C and typed failures.
    // ------------------------------------------------------------------

    [Fact]
    public void CapacitiveReceiver_MatchesTheLumpedDivider()
    {
        // A line short enough to be transparent (βl ≪ 1): the far voltage approaches
        // the source divided by Rs against R∥C — the receiver model's sanity anchor.
        const double f = 1e8;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 1e-4) });
        var solution = network.SolveTerminated(f,
            new[] { new LineTermination(50, 100, LoadCapacitanceFarads: 3e-12) },
            new[] { Complex.One });
        Complex yl = 1.0 / 100 + new Complex(0, 2 * Math.PI * f * 3e-12);
        Complex zl = 1 / yl;
        Complex expected = zl / (zl + 50);
        Assert.True((solution.FarVoltages[0] - expected).Magnitude / expected.Magnitude < 1e-3,
            $"V_far {solution.FarVoltages[0]} vs divider {expected}");
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        var one = SingleLine(AirL, AirC);
        var two = CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11);
        Assert.Throws<ArgumentException>(() => new MtlNetwork(new[]
            { new MtlSection(one, 0.01), new MtlSection(two, 0.01) }));
        Assert.Throws<ArgumentException>(() => new MtlNetwork(new[] { new MtlSection(one, 0) }));
        var net = new MtlNetwork(new[] { new MtlSection(one, 0.01) });
        Assert.Throws<ArgumentException>(() => net.SolveTerminated(1e9,
            new[] { new LineTermination(50, -5) }, new[] { Complex.One }));
    }

    // ------------------------------------------------------------------
    // Touchstone round trip.
    // ------------------------------------------------------------------

    [Fact]
    public void Touchstone_WritesSpecCompliantDataThatRoundTrips()
    {
        var pair = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.05) });
        var freqs = new[] { 1e9, 2e9 };
        var matrices = freqs.Select(f => pair.Scattering(f)).ToArray();
        string text = TouchstoneWriter.Write(freqs, matrices);

        Assert.Contains("# HZ S RI R 50", text);
        var dataLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('!') && !line.StartsWith('#'))
            .Select(line => line.Trim()).ToArray();
        // 4-port: 4 rows per frequency, first row carries the frequency (1 + 8 numbers).
        Assert.Equal(8, dataLines.Length);
        var first = dataLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(9, first.Length);
        Assert.Equal(1e9, first[0]);
        Assert.Equal(matrices[0][0, 0].Real, first[1], 10);
        Assert.Equal(matrices[0][0, 3].Imaginary, first[8], 10);
        // Row 2 of the matrix (no frequency prefix).
        var second = dataLines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, second.Length);
    }
}
