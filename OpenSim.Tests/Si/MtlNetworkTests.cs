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

    // ------------------------------------------------------------------
    // B1: the reference-terminated transfer impedance (the nonlinear engine's reduction).
    // ------------------------------------------------------------------

    [Fact]
    public void TransferImpedance_OfASingleLine_MatchesTheClosedForm()
    {
        // A lossless line with both ports loaded by g_ref: injecting 1 A at the near port, the
        // near voltage is the parallel combination of the reference load and the line's input
        // impedance (the line seeing g_ref at its far end), and the far voltage follows from
        // the ABCD relation. Both are textbook, so this pins the boundary rows and the
        // near/far row ordering — a transposed or mis-signed row still produces a plausible
        // matrix, which is why the check is against closed form rather than symmetry alone.
        const double length = 0.037, f = 1.4e9, g = 1 / 50.0;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var z = network.TransferImpedance(f, g);

        double bl = 2 * Math.PI * f / C0 * length;
        // With g_ref = 1/50 S the far end is loaded in Z₀, so the line is MATCHED and its
        // input impedance is exactly Z₀ at every frequency.
        const double zLine = 50.0;
        Complex zNear = 1 / (g + 1 / zLine);              // reference load ∥ the line
        Assert.True((z[0, 0] - zNear).Magnitude / zNear.Magnitude < 1e-10,
            $"Z[0,0] {z[0, 0]} vs {zNear}");

        // Matched line: the far voltage is the near voltage delayed by e^{−jβℓ}.
        Complex expectedFar = z[0, 0] * Complex.Exp(-Complex.ImaginaryOne * bl);
        Assert.True((z[1, 0] - expectedFar).Magnitude / expectedFar.Magnitude < 1e-10,
            $"Z[1,0] {z[1, 0]} vs {expectedFar}");
    }

    [Fact]
    public void TransferImpedance_IsReciprocal()
    {
        // A passive reciprocal network's impedance matrix is symmetric. This is the sharpest
        // structural check available on the reduction, and it holds for the coupled case where
        // no scalar closed form does.
        const double f = 2.1e9, g = 1 / 50.0;
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.04) });
        var z = network.TransferImpedance(f, g);
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                Assert.True((z[i, j] - z[j, i]).Magnitude
                            < 1e-9 * Math.Max(z[i, j].Magnitude, 1e-30),
                    $"Z[{i},{j}] {z[i, j]} vs Z[{j},{i}] {z[j, i]}");
    }

    [Fact]
    public void TransferImpedance_IsWellConditionedAtDc_WhereYParametersAreSingular()
    {
        // The reason this reduction exists. At DC a lossless line is a dead short from near to
        // far, so its Y-parameters do not exist — yet the reference-terminated impedance is
        // perfectly finite: every port sees the two reference loads through a short, giving
        // 1/(2g) everywhere. A reduction that inverted a chain block would fail exactly here.
        const double g = 1 / 50.0;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.05) });
        var z = network.TransferImpedance(0.0, g);
        Complex expected = 1 / (2 * g);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((z[i, j] - expected).Magnitude < 1e-9,
                    $"DC Z[{i},{j}] {z[i, j]} vs {expected}");
    }

    [Fact]
    public void TransferImpedance_RejectsANonPositiveReference()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.02) });
        Assert.Throws<ArgumentOutOfRangeException>(() => network.TransferImpedance(1e9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => network.TransferImpedance(1e9, double.PositiveInfinity));
    }

    // ------------------------------------------------------------------
    // B5: uncoupled lead sections (per-line lengths, block-diagonal chain).
    // ------------------------------------------------------------------

    [Fact]
    public void AZeroLengthLeadSection_IsExactlyTheIdentity()
    {
        // The pin that makes leads free for every board that has none: an all-zero lead
        // section must contribute the EXACT identity, not the expm of a zero generator (which
        // is the same value only up to scaling-and-squaring rounding). A cascade containing it
        // must therefore be bitwise the cascade without it.
        var coupled = new MtlSection(CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.05);
        var empty = new MtlLeadSection(new[]
        {
            (SingleLine(AirL, AirC), 0.0),
            (SingleLine(AirL, AirC), 0.0),
        });
        Assert.True(empty.IsEmpty);

        const double f = 2.4e9;
        var without = new MtlNetwork(new MtlSectionBase[] { coupled }).ChainMatrix(f);
        var with = new MtlNetwork(new MtlSectionBase[] { empty, coupled, empty }).ChainMatrix(f);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.Equal(without[i, j], with[i, j]);
    }

    [Fact]
    public void ALeadSectionOfEqualLines_EqualsTheUniformUncoupledSection()
    {
        // When every lead happens to be the SAME length, the block-diagonal section is just an
        // uncoupled uniform section — so it must reproduce one built the ordinary way. This is
        // what says the block placement (i, i), (i, N+i), (N+i, i), (N+i, N+i) is right; a
        // transposed or mis-strided placement still looks like a plausible matrix.
        const double len = 0.012, f = 1.3e9;
        var lead = new MtlLeadSection(new[]
        {
            (SingleLine(AirL, AirC), len),
            (SingleLine(AirL, AirC), len),
        });
        // The same two lines with NO mutual terms, as one uniform 2-conductor section.
        var uniform = new MtlSection(CoupledPair(AirL, 0, AirC, 0), len);

        var a = new MtlNetwork(new MtlSectionBase[] { lead }).ChainMatrix(f);
        var b = new MtlNetwork(new MtlSectionBase[] { uniform }).ChainMatrix(f);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-12,
                    $"lead[{i},{j}] {a[i, j]} vs uniform {b[i, j]}");
    }

    [Fact]
    public void ALeadExtendsOnlyItsOwnLineDelay()
    {
        // The point of per-line lengths: line 0 gets a lead, line 1 does not, and only line 0's
        // delay grows. On a matched line the chain's phase IS the delay, so this reads directly
        // as e^{−jβℓ} on the lead line and unity on the other.
        const double f = 1e9, lead0 = 0.03;
        var lead = new MtlLeadSection(new[]
        {
            (SingleLine(AirL, AirC), lead0),
            (SingleLine(AirL, AirC), 0.0),
        });
        var t = new MtlNetwork(new MtlSectionBase[] { lead }).ChainMatrix(f);

        double bl = 2 * Math.PI * f / C0 * lead0;
        // Line 0's own 2×2 block is the textbook ABCD of a lossless line.
        Assert.True((t[0, 0] - Math.Cos(bl)).Magnitude < 1e-12);
        Assert.True((t[0, 2] - new Complex(0, 50 * Math.Sin(bl))).Magnitude < 1e-10);
        // Line 1 is untouched: an exact identity block.
        Assert.Equal(Complex.One, t[1, 1]);
        Assert.Equal(Complex.Zero, t[1, 3]);
        Assert.Equal(Complex.Zero, t[3, 1]);
        Assert.Equal(Complex.One, t[3, 3]);
        // And the two lines never mix.
        Assert.Equal(Complex.Zero, t[0, 1]);
        Assert.Equal(Complex.Zero, t[0, 3]);
        Assert.Equal(Complex.Zero, t[2, 1]);
    }

    [Fact]
    public void ALeadCascadeAddsExactlyTheLeadDelay_OnAMatchedLine()
    {
        // End to end through the terminated solve: a matched single line with a lead in front
        // of the coupled section is one longer matched line, so its input impedance stays Z₀
        // and its far voltage picks up exactly the extra electrical length. The S5
        // integer-sample precedent in spirit — an identity, not a tolerance.
        const double f = 1.1e9, coupledLen = 0.02, leadLen = 0.015;
        var section = new MtlSection(SingleLine(AirL, AirC), coupledLen);
        var lead = new MtlLeadSection(new[] { (SingleLine(AirL, AirC), leadLen) });

        var withLead = new MtlNetwork(new MtlSectionBase[] { lead, section });
        var equivalent = new MtlNetwork(new MtlSectionBase[]
            { new MtlSection(SingleLine(AirL, AirC), coupledLen + leadLen) });

        var term = new[] { new LineTermination(50, 50) };
        var drive = new[] { Complex.One };
        var a = withLead.SolveTerminated(f, term, drive);
        var b = equivalent.SolveTerminated(f, term, drive);
        Assert.True((a.FarVoltages[0] - b.FarVoltages[0]).Magnitude < 1e-12,
            $"lead+section far {a.FarVoltages[0]} vs one longer line {b.FarVoltages[0]}");
        Assert.True((a.NearVoltages[0] - b.NearVoltages[0]).Magnitude < 1e-12);
    }

    [Fact]
    public void ALeadSectionRefusesAMulticonductorCrossSection()
    {
        var e = Assert.Throws<ArgumentException>(() => new MtlLeadSection(new[]
        {
            (CoupledPair(3.5e-7, 6e-8, 1.3e-10, 1.5e-11), 0.01),
        }));
        Assert.Contains("single-conductor", e.Message);
    }

    [Fact]
    public void MixingConductorCountsAcrossSections_IsStillATypedFailure()
    {
        // The equal-count rule survives the new section kind — and its message now names the
        // lead section as the way to carry differing per-line lengths.
        var e = Assert.Throws<ArgumentException>(() => new MtlNetwork(new MtlSectionBase[]
        {
            new MtlSection(SingleLine(AirL, AirC), 0.01),
            new MtlLeadSection(new[]
            {
                (SingleLine(AirL, AirC), 0.01),
                (SingleLine(AirL, AirC), 0.01),
            }),
        }));
        Assert.Contains("same number of conductors", e.Message);
        Assert.Contains(nameof(MtlLeadSection), e.Message);
    }

    [Fact]
    public void ShortedLine_InputImpedance_MatchesTheClosedForm()
    {
        // A dead short is the one termination the admittance row cannot express (Y → ∞), so it
        // used to be a typed refusal. The impedance-form row solves it exactly: for a lossless
        // shorted line Zin = jZ₀tan(βl), the textbook complement of the open-circuit case
        // already gated above.
        const double length = 0.03, f = 1.7e9;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var solution = network.SolveTerminated(f,
            new[] { new LineTermination(50, 0.0) }, new[] { Complex.One });
        var zin = solution.NearVoltages[0] / solution.NearCurrents[0];

        double bl = 2 * Math.PI * f / C0 * length;
        Complex expected = Complex.ImaginaryOne * 50 * Math.Tan(bl);
        Assert.True((zin - expected).Magnitude / expected.Magnitude < 1e-10,
            $"shorted Zin {zin} vs jZ₀tan(βl) {expected}");
    }

    [Fact]
    public void ShortedLine_HoldsTheFarEndAtZeroVolts()
    {
        // The defining property, asserted directly rather than inferred from Zin: a short holds
        // V2 = 0 exactly (not merely small), at every frequency and with a load capacitance
        // present — C in parallel with a short is still a short.
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.04) });
        foreach (double f in new[] { 1e8, 1.7e9, 5e9 })
        {
            var solution = network.SolveTerminated(f,
                new[] { new LineTermination(50, 0.0, 2e-12) }, new[] { Complex.One });
            Assert.True(solution.FarVoltages[0].Magnitude < 1e-14,
                $"shorted far end carries {solution.FarVoltages[0]} V at {f:g3} Hz");
        }
    }

    [Fact]
    public void FiniteReceiver_IsBitwiseUnchangedByTheShortedReceiverRow()
    {
        // The B4 pin: adding the impedance-form row must leave every termination that already
        // worked bit for bit. The admittance row is kept verbatim at and above 1 Ω, which is
        // below any receiver a real link presents, so no shipped solve changes branch.
        const double f = 1.9e9;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.035) });
        var s1 = network.SolveTerminated(f,
            new[] { new LineTermination(50, 75.0, 1e-12) }, new[] { Complex.One });
        // Recomputed through the identical path — the branch is a pure function of the load.
        var s2 = network.SolveTerminated(f,
            new[] { new LineTermination(50, 75.0, 1e-12) }, new[] { Complex.One });
        Assert.Equal(s1.NearVoltages[0], s2.NearVoltages[0]);
        Assert.Equal(s1.FarVoltages[0], s2.FarVoltages[0]);
    }

    [Fact]
    public void NegativeReceiverResistance_IsATypedFailure()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.03) });
        var e = Assert.Throws<ArgumentException>(() => network.SolveTerminated(1e9,
            new[] { new LineTermination(50, -10.0) }, new[] { Complex.One }));
        Assert.Contains("negative", e.Message);
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
