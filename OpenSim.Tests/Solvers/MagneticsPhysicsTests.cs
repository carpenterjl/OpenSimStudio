using System.Numerics;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>FU-39: nonlinear time-harmonic (effective permeability), iGSE core loss, strand eddy
/// loss, Maxwell-stress forces and AC winding coupling, each against a closed form or an
/// independent numerical solve.</summary>
public class MagneticsPhysicsTests
{
    private readonly ITestOutputHelper _out;
    public MagneticsPhysicsTests(ITestOutputHelper output) => _out = output;

    private const double Mu0 = MagneticConstants.Mu0;

    private static BhCurve SteelCurve() => new(new[]
    {
        (100.0, 0.5), (300.0, 1.0), (1000.0, 1.4), (5000.0, 1.7), (20000.0, 1.9)
    });

    /// <summary>The parallel-plate line of MagneticsTests: ±I in two flat conductors between
    /// symmetry planes, H = I/W in the gap by Ampère's law.</summary>
    private static MagneticModel2D ParallelPlates(double current, MagneticMaterial gap,
        double strandDiameter = 0, int strandsPerTurn = 1, double s = 2e-3, double t = 0.5e-3, double w = 10e-3)
    {
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -5e-3, DomainX1 = 5e-3, DomainY0 = 0, DomainY1 = w,
            NaturalSides = DomainSides.Bottom | DomainSides.Top | DomainSides.Right,
            MaxElement = 0.5e-3, MinElement = 0.05e-3
        };
        model.Regions.Add(new MagneticRegion("go", -s / 2 - t, -s / 2, 0, w, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("return", s / 2, s / 2 + t, 0, w, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("gap", -s / 2, s / 2, 0, w, gap));
        model.Coils.Add(new StrandedCoil("line", new[] { ("go", 1), ("return", -1) }, 1, current)
        {
            StrandDiameter = strandDiameter,
            StrandsPerTurn = strandsPerTurn
        });
        return model;
    }

    [Fact]
    public void TheEffectiveReluctivity_IsExactWhenLinear_AndMatchesItsDefinition()
    {
        // A two-point curve is a straight line below its knee, so ν_eff is ν itself.
        var linear = new BhCurve(new[] { (1000.0, 1.0) });
        Assert.Equal(1000.0, linear.EffectiveReluctivity(0.8), 6);

        // On the steel curve the definition ν_eff(B̂) = (8/(π·B̂²))·∫₀^{π/2} w(B̂ sinθ) dθ,
        // w = ∫H dB, re-integrated here by the test's own trapezoid rule.
        var curve = SteelCurve();
        const double bAmp = 1.3;
        const int n = 1500;
        double W(double b)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double b0 = b * i / n, b1 = b * (i + 1) / n;
                sum += (b1 - b0) * 0.5 * (curve.Evaluate(b0).H + curve.Evaluate(b1).H);
            }
            return sum;
        }
        double integral = 0;
        for (int i = 0; i < n; i++)
        {
            double t0 = Math.PI / 2 * i / n, t1 = Math.PI / 2 * (i + 1) / n;
            integral += (t1 - t0) * 0.5 * (W(bAmp * Math.Sin(t0)) + W(bAmp * Math.Sin(t1)));
        }
        double expected = 8 * integral / (Math.PI * bAmp * bAmp);
        double got = curve.EffectiveReluctivity(bAmp);
        _out.WriteLine($"ν_eff({bAmp} T) {got:g8} vs the test's quadrature {expected:g8} ({got / expected - 1:e2})");
        Assert.True(Math.Abs(got - expected) / expected <= 1e-4, $"{got} vs {expected}");
    }

    [Fact]
    public void ASaturatingGap_UnderAc_SettlesOnTheEffectiveCurve()
    {
        // Between the plates H = I/W whatever the material (Ampère), so the converged AC solve
        // must satisfy ν_eff(B̂)·B̂ = Î/W. At Î = 30 A over 10 mm that is 3000 A/m, on the knee.
        var steel = new MagneticMaterial("steel", 1) { Curve = SteelCurve(), Steinmetz = new SteinmetzCoefficients(2.0, 1.4, 2.3) };
        var solution = new MagneticSolver2D().SolveTimeHarmonic(ParallelPlates(30, steel), 50);
        foreach (var line in solution.Log) _out.WriteLine(line);
        Assert.Contains(solution.Log, l => l.StartsWith("Effective permeability"));
        Assert.DoesNotContain(solution.Log, l => l.StartsWith("WARNING"));
        double bAmp = solution.FluxDensityAt(0, 5e-3).By.Magnitude;
        double h = SteelCurve().EffectiveReluctivity(bAmp) * bAmp;
        _out.WriteLine($"B̂ {bAmp:g8} T; ν_eff(B̂)·B̂ {h:g8} A/m vs Î/W {3000}");
        Assert.True(Math.Abs(h - 3000) / 3000 <= 1e-4, $"{h} vs 3000");

        // The iGSE core loss of a sinusoidal waveform is the Steinmetz loss, element by element.
        var sine = Enumerable.Range(0, 800).Select(i => ((double)i / 800, Math.Sin(2 * Math.PI * i / 800))).ToList();
        double igse = solution.CoreLossIgse(sine);
        double steinmetz = solution.CoreLoss();
        _out.WriteLine($"core loss: iGSE sine {igse:g8} W/m vs Steinmetz {steinmetz:g8}");
        Assert.True(Math.Abs(igse - steinmetz) / steinmetz <= 1e-4, $"{igse} vs {steinmetz}");
    }

    [Fact]
    public void TheIgseOfASinusoid_IsTheSteinmetzLoss()
    {
        var law = new SteinmetzCoefficients(2.0, 1.4, 2.3);
        const int n = 800;
        var wave = Enumerable.Range(0, n).Select(i => ((double)i / n, Math.Sin(2 * Math.PI * i / n))).ToList();
        double igse = law.IgseLossDensity(100e3, 0.3, wave);
        double steinmetz = law.LossDensity(100e3, 0.3);
        _out.WriteLine($"iGSE {igse:g8} W/m³ vs Steinmetz {steinmetz:g8} ({igse / steinmetz - 1:e2})");
        Assert.True(Math.Abs(igse - steinmetz) / steinmetz <= 1e-4, $"{igse} vs {steinmetz}");
    }

    [Fact]
    public void ATriangularFlux_FollowsTheDutyCycle()
    {
        var law = new SteinmetzCoefficients(2.0, 1.4, 2.3);
        const double f = 100e3, bPk = 0.3;
        double P(double duty) => law.IgseLossDensity(f, bPk, new[] { (0.0, -1.0), (duty, 1.0) });
        double Closed(double duty)
        {
            // Two linear ramps, ΔB = 2B̂: P = k_i·(2B̂)^(β−α)·Σ|dB/dt|^α·Δt / T.
            double t = 1 / f;
            double up = 2 * bPk / (duty * t), down = 2 * bPk / ((1 - duty) * t);
            return law.IgseCoefficient * Math.Pow(2 * bPk, 2.3 - 1.4)
                * (Math.Pow(up, 1.4) * duty * t + Math.Pow(down, 1.4) * (1 - duty) * t) / t;
        }
        _out.WriteLine($"P(0.5) {P(0.5):g6} W/m³, P(0.2) {P(0.2):g6} W/m³");
        Assert.True(Math.Abs(P(0.5) - Closed(0.5)) / Closed(0.5) <= 1e-12);
        Assert.True(Math.Abs(P(0.2) - Closed(0.2)) / Closed(0.2) <= 1e-12);
        Assert.True(P(0.2) > P(0.5), "an asymmetric ramp costs more than the symmetric one");
    }

    [Fact]
    public void TheRoundWireSkinFactor_MatchesARadialDiffusionSolve()
    {
        // (1/r)(r·E′)′ = jωµσ·E on [0, a], E regular at the axis, E(a) = 1: finite differences and
        // a complex Thomas solve; I = σ∫E·2πr dr, F = Re(E(a)/I)·σπa².
        const double sigma = 5.8e7, d = 1e-3;
        foreach (double ratio in new[] { 0.5, 2.0, 6.0 })
        {
            double delta = d / 2 / ratio; // a/δ = ratio
            double f = 1 / (Math.PI * Mu0 * sigma * delta * delta);
            double got = RoundWire.SkinResistanceFactor(d, sigma, f);

            const int n = 4000;
            double a = d / 2, h = a / n;
            var k2 = new Complex(0, 2 * Math.PI * f * Mu0 * sigma);
            var diag = new Complex[n + 1];
            var lower = new Complex[n + 1];
            var upper = new Complex[n + 1];
            var rhs = new Complex[n + 1];
            diag[0] = -4 / (h * h) - k2; upper[0] = 4 / (h * h);
            for (int i = 1; i < n; i++)
            {
                double r = i * h;
                lower[i] = 1 / (h * h) - 1 / (2 * r * h);
                diag[i] = -2 / (h * h) - k2;
                upper[i] = 1 / (h * h) + 1 / (2 * r * h);
            }
            diag[n] = 1; rhs[n] = 1;
            for (int i = 1; i <= n; i++)
            {
                var m = lower[i] / diag[i - 1];
                diag[i] -= m * upper[i - 1];
                rhs[i] -= m * rhs[i - 1];
            }
            var e = new Complex[n + 1];
            e[n] = rhs[n] / diag[n];
            for (int i = n - 1; i >= 0; i--) e[i] = (rhs[i] - upper[i] * e[i + 1]) / diag[i];
            Complex current = Complex.Zero;
            for (int i = 0; i < n; i++)
                current += 0.5 * (e[i] * (i * h) + e[i + 1] * ((i + 1) * h)) * h;
            current *= sigma * 2 * Math.PI;
            double expected = (1 / current).Real * sigma * Math.PI * a * a;
            _out.WriteLine($"a/δ {ratio}: F {got:g8} vs radial solve {expected:g8} ({got / expected - 1:e2})");
            Assert.True(Math.Abs(got - expected) / expected <= 1e-4, $"a/δ {ratio}: {got} vs {expected}");
        }
    }

    [Fact]
    public void AStrandedWinding_CarriesTheHomogenisedStrandLoss()
    {
        // A passive litz winding sits in the uniform gap field of the plate line: its proximity
        // loss is exactly (σω²d²/32)·B̂²·fill·volume. The driven line's DC resistance is its two
        // legs' strands in parallel, and its skin factor is the round-wire closed form.
        const double f = 20e3, d = 0.2e-3, w = 10e-3, current = 2;
        const int strands = 5;
        var model = ParallelPlates(current, MagneticMaterial.Air, d, strands);
        model.Regions.Add(new MagneticRegion("sense", -0.5e-3, 0.5e-3, 2e-3, 8e-3, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("sense", new[] { ("sense", 1) }, 3, 0) { StrandDiameter = d, StrandsPerTurn = strands });
        var solution = new MagneticSolver2D().SolveTimeHarmonic(model, f);

        var sense = solution.StrandLoss("sense");
        double b = Mu0 * current / w;
        double area = 1e-3 * 6e-3;
        double fill = 3 * strands * Math.PI * d * d / 4 / area;
        double omega = 2 * Math.PI * f;
        double expectedProximity = 5.8e7 * omega * omega * d * d / 32 * b * b * fill * area;
        _out.WriteLine($"sense proximity {sense.ProximityWatts:g8} W/m vs {expectedProximity:g8} ({sense.ProximityWatts / expectedProximity - 1:e2})");
        Assert.True(Math.Abs(sense.ProximityWatts - expectedProximity) / expectedProximity <= 1e-4,
            $"{sense.ProximityWatts} vs {expectedProximity}");
        Assert.Equal(0.0, sense.TransportWatts);
        Assert.Null(sense.AcResistanceOhms);
        Assert.Empty(sense.Notes);

        var line = solution.StrandLoss("line");
        double strandArea = Math.PI * d * d / 4;
        double rdcExpected = 2 * 1.0 / (5.8e7 * strands * strandArea); // go + return, one turn, per metre
        double skinExpected = RoundWire.SkinResistanceFactor(d, 5.8e7, f);
        _out.WriteLine($"line R_dc {line.DcResistanceOhms:g8} Ω/m vs {rdcExpected:g8}; skin factor {line.SkinFactor:g8}");
        Assert.True(Math.Abs(line.DcResistanceOhms - rdcExpected) / rdcExpected <= 1e-9,
            $"{line.DcResistanceOhms} vs {rdcExpected}");
        Assert.Equal(skinExpected, line.SkinFactor, 12);
        Assert.True(Math.Abs(line.TransportWatts - 0.5 * current * current * rdcExpected * skinExpected)
            / line.TransportWatts <= 1e-9);
        Assert.NotNull(line.AcResistanceOhms);
    }

    [Fact]
    public void TheForceBetweenParallelPlates_IsTheMagneticPressure()
    {
        // Two wide sheets carrying ±I repel with the magnetic pressure µ0·(I/W)²/2 over their
        // width: F' = µ0·I²/(2W) per metre, toward −x on the left ('go') plate.
        var model = ParallelPlates(1, MagneticMaterial.Air);
        var solution = new MagneticSolver2D().SolveStatic(model);
        var (fx, fy) = solution.Force("go");
        double expected = -Mu0 * 1 * 1 / (2 * 10e-3);
        _out.WriteLine($"Fx {fx * 1e6:F6} µN/m vs {expected * 1e6:F6}; Fy {fy * 1e6:g3} µN/m");
        Assert.True(Math.Abs(fx - expected) / Math.Abs(expected) <= 1e-6, $"{fx} vs {expected}");
        Assert.True(Math.Abs(fy) <= 1e-5 * Math.Abs(expected), $"Fy {fy}");

        // The time-average force at amplitude 1 is half the static force at 1 (no conductivity
        // anywhere, so the AC field equals the static one).
        var ac = new MagneticSolver2D().SolveTimeHarmonic(ParallelPlates(1, MagneticMaterial.Air), 50).Force("go");
        Assert.True(Math.Abs(ac.Fx - 0.5 * fx) / Math.Abs(0.5 * fx) <= 1e-9, $"{ac.Fx} vs {0.5 * fx}");
    }

    [Fact]
    public void TheForceBetweenCoaxialRings_IsTheGradientOfTheirMutual()
    {
        const double r = 10e-3, c = 0.2e-3, sep = 5e-3;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 0.15, DomainY0 = -0.15, DomainY1 = 0.15,
            MaxElement = 6e-3, MinElement = 0.025e-3, Growth = 1.3
        };
        model.Regions.Add(new MagneticRegion("ring1", r - c / 2, r + c / 2, -c / 2, c / 2, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("ring2", r - c / 2, r + c / 2, sep - c / 2, sep + c / 2, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("ring1", new[] { ("ring1", 1) }, 1, 1));
        model.Coils.Add(new StrandedCoil("ring2", new[] { ("ring2", 1) }, 1, 1));
        var solution = new MagneticSolver2D().SolveStatic(model);
        var (_, fz) = solution.Force(8, "ring2");
        const double h = 0.05e-3;
        double expected = (MagneticsTests.LoopMutual(r, r, sep + h) - MagneticsTests.LoopMutual(r, r, sep - h)) / (2 * h);
        _out.WriteLine($"Fz {fz * 1e6:F4} µN vs I²·dM/dz {expected * 1e6:F4} ({fz / expected - 1:e2})");
        Assert.True(fz < 0, "same-sense rings attract");
        Assert.True(Math.Abs(fz - expected) / Math.Abs(expected) <= 0.02, $"{fz} vs {expected}");
    }

    [Fact]
    public void TheWindingImpedanceMatrix_IsReciprocal_AndHasTheClosedForms()
    {
        // Two coaxial copper rings as one-turn windings: at 1 Hz the current is uniform, so
        // Re Z11 is the ring's DC resistance 2π/(σ·c·ln(r1/r0)) and Im Z12/ω is Maxwell's mutual;
        // Z is reciprocal there and with the skin effect in play.
        const double r = 10e-3, width = 0.2e-3, c = 0.2e-3, sep = 5e-3, sigma = 5.8e7;
        var turns = new[]
        {
            new WindingTurn("P", 1, r - width / 2, r + width / 2, -c / 2, c / 2),
            new WindingTurn("S", 2, r - width / 2, r + width / 2, sep - c / 2, sep + c / 2)
        };
        var zm = PlanarWindingSection.ImpedanceMatrix(turns, core: null, frequencyHz: 1.0, copperConductivity: sigma,
            minElement: 0.025e-3);
        double rdc = 2 * Math.PI / (sigma * c * Math.Log((r + width / 2) / (r - width / 2)));
        double m = MagneticsTests.LoopMutual(r, r, sep);
        double mGot = zm.InductanceHenries("P", "S");
        double offDiagonal = (zm.Z("P", "S") - zm.Z("S", "P")).Magnitude / zm.Z("P", "S").Magnitude;
        // The proven static path on the same mesh and domain: at 1 Hz (δ ≈ 66 mm ≫ the section)
        // the solid turns carry uniform current, so the AC mutual must equal the static one.
        var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core: null,
            solidTurns: false, copperConductivity: sigma, minElement: 0.025e-3);
        var mStatic = new MagneticSolver2D().SolveStatic(built.Model)
            .FluxLinkage(turns[1].Name).Real;
        _out.WriteLine($"R11 {zm.Z("P", "P").Real:g8} Ω vs {rdc:g8}; M {mGot * 1e9:F5} nH vs static {mStatic * 1e9:F5} "
            + $"and Maxwell {m * 1e9:F5}; |Z12−Z21|/|Z12| {offDiagonal:e2}; k {zm.CouplingCoefficient("P", "S"):F4}");
        Assert.True(Math.Abs(zm.Z("P", "P").Real - rdc) / rdc <= 1e-3, $"{zm.Z("P", "P").Real} vs {rdc}");
        Assert.True(Math.Abs(mGot - mStatic) / mStatic <= 1e-3, $"{mGot} vs static {mStatic}");
        // Build's domain stops at 4 ring radii where the old ring test used 15, and the A = 0 wall
        // clips a far-coupled pair: Maxwell's closed form is a sanity band only (measured −2.4 %).
        Assert.True(Math.Abs(mGot - m) / m <= 0.04, $"{mGot} vs {m}");
        Assert.True(offDiagonal <= 1e-6, $"asymmetry {offDiagonal}");
        Assert.InRange(zm.CouplingCoefficient("P", "S"), 0.01, 1.0);

        var skin = PlanarWindingSection.ImpedanceMatrix(turns, core: null, frequencyHz: 1e6, copperConductivity: sigma);
        double skinAsymmetry = (skin.Z("P", "S") - skin.Z("S", "P")).Magnitude / skin.Z("P", "S").Magnitude;
        _out.WriteLine($"1 MHz: R11 {skin.Z("P", "P").Real:g6} Ω, |Z12−Z21|/|Z12| {skinAsymmetry:e2}");
        Assert.True(skinAsymmetry <= 1e-6, $"asymmetry {skinAsymmetry}");
        Assert.True(skin.Z("P", "P").Real > zm.Z("P", "P").Real, "the skin effect raises R");
    }

    [Fact]
    public void CommandLine_ReportsStrandLossAndMutual()
    {
        // The square single-turn Gerber loop of MagneticsTests, solved stranded at 100 kHz.
        string dir = Path.Combine(Path.GetTempPath(), "opensim-magnetics-" + Guid.NewGuid().ToString("N"));
        string board = Path.Combine(dir, "board");
        Directory.CreateDirectory(board);
        try
        {
            File.WriteAllText(Path.Combine(board, "top.gbr"), """
                %TF.FileFunction,Copper,L1,Top,Signal*%
                %TF.FilePolarity,Positive*%
                %FSLAX46Y46*%
                %MOMM*%
                %ADD10R,12.000X1.000*%
                %ADD11R,1.000X12.000*%
                %TO.N,P*%
                D10*
                X0Y5500000D03*
                X0Y-5500000D03*
                D11*
                X5500000Y0D03*
                X-5500000Y0D03*
                %TD*%
                M02*
                """);
            File.WriteAllText(Path.Combine(board, "bottom.gbr"), """
                %TF.FileFunction,Copper,L2,Bot,Signal*%
                %TF.FilePolarity,Positive*%
                %FSLAX46Y46*%
                %MOMM*%
                %ADD10R,1.000X1.000*%
                %TO.N,X*%
                D10*
                X20000000Y20000000D03*
                %TD*%
                M02*
                """);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int code = OpenSim.Cli.CliRunner.Run(new[]
            {
                "magnetics", board, "--nets", "P", "--center", "0,0", "--frequency", "100000",
                "--strand", "0.03,5", "--out", Path.Combine(dir, "out")
            }, stdout, stderr);
            _out.WriteLine(stdout.ToString());
            Assert.Equal("", stderr.ToString());
            Assert.Equal(0, code);
            Assert.Contains("strands", stdout.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
