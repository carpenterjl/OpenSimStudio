using System.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The proximity-effect filament solve gates (SI Stage S8). The hard oracle is the EXACT
/// round-wire skin effect: a disk tiled into current sheets, R_ac/R_dc compared to the
/// Kelvin-function closed form (ber/bei via the complex J₀), and the internal inductance
/// compared to μ₀/8π through a reference-cancelling frequency difference. Physics gates
/// (DC limit, √f growth, proximity monotonicity, passivity) ride on the coupled-strip
/// path that feeds the MTL network.
/// </summary>
public class ProximityExtractorTests
{
    private const double Mu0 = 4e-7 * Math.PI;

    private static LayeredStackup Slab(double epsR, double h) =>
        new(new[] { new LayeredStackup.Layer(epsR, 0, h) });

    // ------------------------------------------------------------------
    // Complex Bessel J₀/J₁ (test-local) → Kelvin ber/bei for the exact oracle.
    // ------------------------------------------------------------------

    private static Complex J0(Complex z)
    {
        Complex q = -0.25 * z * z, term = Complex.One, sum = Complex.One;
        for (int k = 1; k < 100; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term.Magnitude < 1e-18 * sum.Magnitude) break;
        }
        return sum;
    }

    private static Complex J1(Complex z)
    {
        Complex q = -0.25 * z * z, term = z / 2, sum = z / 2;
        for (int k = 1; k < 100; k++)
        {
            term *= q / (k * (k + 1.0));
            sum += term;
            if (term.Magnitude < 1e-18 * sum.Magnitude) break;
        }
        return sum;
    }

    /// <summary>Exact internal impedance of an isolated round wire per unit length:
    /// Z_int = (jωμ₀)/(2πγa)·J₀(γa)/J₁(γa), γ = √(jωμ₀σ) (the e^{jωt} convention the filament
    /// solve uses — the DC limit is +R_dc). R_ac = Re(Z_int).</summary>
    private static Complex ExactWireInternalImpedance(double radius, double sigma, double freq)
    {
        double omega = 2 * Math.PI * freq;
        Complex gamma = Complex.Sqrt(new Complex(0, omega * Mu0 * sigma));
        Complex ga = gamma * radius;
        return new Complex(0, omega * Mu0) / (2 * Math.PI * gamma * radius) * (J0(ga) / J1(ga));
    }

    /// <summary>Tiles a disk of the given radius into a grid of current-sheet filaments
    /// (all one conductor). Cells outside the circle are dropped; each kept cell is a strip
    /// at its z-center with the true area for resistance.</summary>
    private static List<ConductionFilament> DiskFilaments(double radius, double sigma, int cells)
    {
        var filaments = new List<ConductionFilament>();
        double d = 2 * radius / cells;
        // Lift the disk well above the (unused) ground line so images are negligible AND
        // z stays positive; the solve is called with groundImage:false anyway.
        double zOffset = 100 * radius;
        for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
            {
                double xc = -radius + (i + 0.5) * d;
                double zc = -radius + (j + 0.5) * d;
                if (xc * xc + zc * zc > radius * radius) continue;   // inside the circle
                filaments.Add(new ConductionFilament(
                    xc - d / 2, xc + d / 2, zOffset + zc, d * d, sigma, 0));
            }
        return filaments;
    }

    // ------------------------------------------------------------------
    // The hard gate: round-wire R_ac/R_dc vs the exact Kelvin-function form.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(2.0)]   // moderate skin depth: a/δ ≈ 1.4
    [InlineData(4.0)]   // a/δ ≈ 2.8, R_ac/R_dc ≈ 1.8
    public void RoundWire_RacOverRdc_MatchesTheExactSkinEffect(double aOverDelta)
    {
        const double radius = 0.5e-3, sigma = 5.8e7;
        // Pick f so a/δ hits the target: δ = √(2/(ωμσ)) ⇒ ω = 2/(μσ·(a/δ · … )).
        double delta = radius / aOverDelta;
        double omega = 2 / (Mu0 * sigma * delta * delta);
        double freq = omega / (2 * Math.PI);

        double exact = ExactWireInternalImpedance(radius, sigma, freq).Real
                       / (1.0 / (sigma * Math.PI * radius * radius));

        // The filament R_ac/R_dc approaches the exact value as the tiling refines. The disk's
        // STAIRCASED boundary makes the approach noisy (a few tenths of a percent per step
        // wobble as corner cells appear/vanish), so the gate is overall convergence
        // (coarse→fine error falls) plus a discretization band at the finest, not per-step
        // monotonicity — the STEP-benchmark "sharp against the discretization model" style.
        double Ratio(int cells)
        {
            var fils = DiskFilaments(radius, sigma, cells);
            double rDcFil = 1.0 / (sigma * fils.Sum(f => f.Area));   // discrete disk area
            return ProximityExtractor.SolveConductorImpedance(fils, 1, freq, groundImage: false)[0, 0].Real
                   / rDcFil;
        }
        double coarse = Ratio(16), fine = Ratio(32);
        Assert.True(Math.Abs(fine - exact) < Math.Abs(coarse - exact) + 1e-9,
            $"a/δ={aOverDelta}: refinement must converge (coarse {coarse:g5}, fine {fine:g5}, exact {exact:g5})");
        Assert.True(Math.Abs(fine - exact) / exact < 0.035,
            $"a/δ={aOverDelta}: R_ac/R_dc {fine:g5} vs exact {exact:g5} ({(fine - exact) / exact:P2})");
    }

    [Fact]
    public void RoundWire_InternalInductance_ApproachesMu0Over8Pi()
    {
        // The DC internal inductance of a round wire is μ₀/8π, reference-INDEPENDENT. The 2D
        // solve's absolute L carries the ambiguous reference constant, so we measure the
        // reference-cancelling difference L_int(low) − L_int(high): low ≈ μ₀/8π, high ≈ 0.
        const double radius = 0.5e-3, sigma = 5.8e7;
        double fLow = 1e2;    // a/δ ≪ 1: current uniform, full internal L
        double fHigh = 5e9;   // a/δ ≫ 1: current on the surface, internal L → 0
        double expected = Mu0 / (8 * Math.PI);

        // The reference-cancelling difference L_int(low) − L_int(high) → μ₀/8π. The staircased
        // disk under-fills near the surface, biasing L_int LOW; the gate is convergence toward
        // μ₀/8π from below plus a discretization band at the finest tiling.
        double Lint(int cells)
        {
            var fils = DiskFilaments(radius, sigma, cells);
            double lLow = ProximityExtractor.SolveConductorImpedance(fils, 1, fLow, false)[0, 0].Imaginary
                          / (2 * Math.PI * fLow);
            double lHigh = ProximityExtractor.SolveConductorImpedance(fils, 1, fHigh, false)[0, 0].Imaginary
                           / (2 * Math.PI * fHigh);
            return lLow - lHigh;
        }
        double coarse = Lint(20), fine = Lint(32);
        Assert.True(Math.Abs(fine - expected) < Math.Abs(coarse - expected) + 1e-12,
            $"internal L must converge to μ₀/8π (coarse {coarse:g5}, fine {fine:g5})");
        Assert.True(fine < expected && Math.Abs(fine - expected) / expected < 0.10,
            $"internal L {fine:g5} vs μ₀/8π {expected:g5} ({(fine - expected) / expected:P2}) — "
            + "the staircase biases it LOW (one-sided) and converges from below");
    }

    // ------------------------------------------------------------------
    // Coupled-strip path (what feeds the MTL network): limits, proximity, passivity.
    // ------------------------------------------------------------------

    private static CoupledLineCrossSection Pair(double gap)
    {
        const double h = 0.2e-3, w = 0.3e-3;
        return new CoupledLineCrossSection(Slab(4.4, h), 0, new[]
        {
            TraceCrossSection.Copper(-(gap + w) / 2, w),
            TraceCrossSection.Copper(+(gap + w) / 2, w),
        });
    }

    [Fact]
    public void CoupledStrips_DcLimit_IsTheForwardResistance()
    {
        var section = Pair(0.3e-3);
        var result = ProximityExtractor.Extract(section, 1e3, 1e11, points: 20);
        // At the low end (a/δ ≪ 1) R → 1/(σwt), the same DC value the v1 model uses.
        var r = result.ResistanceMatrix(1e3);
        double rDc = 1.0 / (5.8e7 * 0.3e-3 * 35e-6);
        Assert.True(Math.Abs(r[0, 0] - rDc) / rDc < 0.02,
            $"DC R {r[0, 0]:g5} vs 1/(σwt) {rDc:g5} ({(r[0, 0] - rDc) / rDc:P2})");
        // Off-diagonal (mutual) resistance at DC is ~0 (no shared dissipation without skin).
        Assert.True(Math.Abs(r[0, 1]) < 0.02 * r[0, 0], "DC mutual R ≈ 0");
    }

    [Fact]
    public void CoupledStrips_HighFrequency_GrowsAndStaysPassive()
    {
        var result = ProximityExtractor.Extract(Pair(0.3e-3), 1e3, 1e11, points: 24);
        double rLow = result.ResistanceMatrix(1e4)[0, 0];
        double rHigh = result.ResistanceMatrix(1e11)[0, 0];
        Assert.True(rHigh > 3 * rLow, $"skin effect must raise R (low {rLow:g4}, high {rHigh:g4})");

        // Passivity: R(f) is symmetric positive-definite at every sampled frequency.
        foreach (double f in new[] { 1e6, 1e8, 1e10 })
        {
            var r = result.ResistanceMatrix(f);
            Assert.True(Math.Abs(r[0, 1] - r[1, 0]) <= 1e-9 * Math.Abs(r[0, 0]), "R symmetric");
            Assert.True(r[0, 0] > 0 && r[1, 1] > 0, "R diagonal positive");
            // 2×2 SPD ⇔ positive diagonal + positive determinant.
            Assert.True(r[0, 0] * r[1, 1] - r[0, 1] * r[1, 0] > 0, $"R SPD at {f:g2} Hz");
        }
    }

    [Fact]
    public void CoupledStrips_ProximityRisesAsTheGapCloses_AndCollapsesWideApart()
    {
        // Proximity crowding: at a fixed high frequency, tighter coupling raises R.
        const double f = 5e9;
        double rTight = ProximityExtractor.Extract(Pair(0.15e-3), 1e6, 1e11, points: 12)
            .ResistanceMatrix(f)[0, 0];
        double rLoose = ProximityExtractor.Extract(Pair(1.0e-3), 1e6, 1e11, points: 12)
            .ResistanceMatrix(f)[0, 0];
        double rFar = ProximityExtractor.Extract(Pair(20e-3), 1e6, 1e11, points: 12)
            .ResistanceMatrix(f)[0, 0];
        Assert.True(rTight > rLoose, $"closer strips must crowd more (tight {rTight:g4} > loose {rLoose:g4})");
        Assert.True(rLoose > rFar * 0.999, "coupling monotone with gap");

        // Wide apart, the mutual resistance collapses toward the isolated line.
        var far = ProximityExtractor.Extract(Pair(20e-3), 1e6, 1e11, points: 12).ResistanceMatrix(f);
        Assert.True(Math.Abs(far[0, 1]) < 0.05 * far[0, 0], "20 mm apart, mutual R negligible");
    }

    [Fact]
    public void InternalInductance_IsMonotoneDecreasing_AndVanishesAtTheTop()
    {
        var result = ProximityExtractor.Extract(Pair(0.3e-3), 1e3, 1e11, points: 20);
        double prev = double.MaxValue;
        foreach (double f in new[] { 1e3, 1e5, 1e7, 1e9 })
        {
            double dl = result.InternalInductance(f)[0, 0];
            Assert.True(dl >= -1e-12, "internal ΔL is non-negative");
            Assert.True(dl <= prev + 1e-12, $"ΔL must fall with frequency (at {f:g2}: {dl:g4})");
            prev = dl;
        }
        // ΔL(f_max) = 0 by construction (the reference is the top sample).
        Assert.True(Math.Abs(result.InternalInductance(1e11)[0, 0]) < 1e-15, "ΔL → 0 at the band top");
    }

    [Fact]
    public void SelfConverges_UnderFilamentRefinement()
    {
        const double f = 2e9;
        var section = Pair(0.3e-3);
        double Rat(int nx, int nz)
        {
            var result = ProximityExtractor.Extract(section, f, f * 10, points: 2,
                lateralCells: nx, thicknessCells: nz);
            return result.ResistanceMatrix(f)[0, 0];
        }
        double r1 = Rat(12, 6), r2 = Rat(20, 10), r3 = Rat(28, 14);
        double d1 = Math.Abs(r2 - r1), d2 = Math.Abs(r3 - r2);
        Assert.True(d2 < d1, "filament refinement must converge");
        Assert.True(d2 / r3 < 0.05, $"20→28 filaments moved R by {d2 / r3:P2}");
    }

    [Fact]
    public void FilamentSolve_IsBitwiseIdentical_AtAnyDegreeOfParallelism()
    {
        var fils = DiskFilaments(0.5e-3, 5.8e7, 20);
        var serial = ProximityExtractor.SolveConductorImpedance(fils, 1, 1e9, false, maxDegreeOfParallelism: 1);
        var parallel = ProximityExtractor.SolveConductorImpedance(fils, 1, 1e9, false, maxDegreeOfParallelism: 8);
        Assert.Equal(serial[0, 0].Real, parallel[0, 0].Real);           // exact, not approximate
        Assert.Equal(serial[0, 0].Imaginary, parallel[0, 0].Imaginary);
    }

    [Fact]
    public void MtlNetwork_ProviderPath_MatchesTheScalarPath_WhenProvidersEchoTheScalarModel()
    {
        // A provider R(f) that returns exactly the scalar R_dc diagonal and ΔL = 0 must give
        // the SAME chain matrix as the scalar path — the new MTL branch is a faithful
        // generalization, not a behavior change.
        const double l = 250e-9, c = 100e-12, rdc = 5.0;
        var scalar = new RlgcResult(1,
            new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { rdc }, new[] { 0.0 }, Array.Empty<string>());
        var withProvider = scalar with
        {
            ResistanceMatrixOhmsPerMeter = _ => new[,] { { rdc } },
            InternalInductanceHenriesPerMeter = _ => new[,] { { 0.0 } },
        };
        var netScalar = new MtlNetwork(new[] { new MtlSection(scalar, 0.05) });
        var netProvider = new MtlNetwork(new[] { new MtlSection(withProvider, 0.05) });
        var a = netScalar.ChainMatrix(2e9);
        var b = netProvider.ChainMatrix(2e9);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-15 * (1 + a[i, j].Magnitude),
                    $"[{i},{j}] scalar {a[i, j]} vs provider {b[i, j]}");
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        var section = Pair(0.3e-3);
        Assert.Throws<ArgumentException>(() => ProximityExtractor.Extract(section, 1e9, 1e9)); // min≥max
        Assert.Throws<ArgumentException>(() =>
            ProximityExtractor.SolveConductorImpedance(Array.Empty<ConductionFilament>(), 1, 1e9, false));
    }
}
