using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Diffraction;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Diffractive;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-47: diffraction efficiency (docs/Optics-Conventions.md §61). Scalar thin-element formulas
/// against Swanson (MIT Lincoln Laboratory TR-854, 1989, DTIC ADA213404) and derived closed forms;
/// RCWA (<see cref="Rcwa1D"/>) against derived thin-film closed forms, energy conservation, the
/// independent EMpy RCWA (ElectroMagneticPython 2.2.4, MIT licence, run with numpy 2.5.4 and scipy
/// 1.18.1 in plans/sources/optics/pyref-venv; inputs beside each stored value) and the scalar limit;
/// Kogelnik's coupled waves against the closed forms and stated values of Kogelnik, Bell Syst. Tech.
/// J. 48, 2909 (1969) (saved under plans/sources/optics/op-47-help/papers/). Geometry, indices and
/// wavelengths are arbitrary test inputs, not reference data.
/// </summary>
public sealed class DiffractionEfficiencyTests
{
    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static double SincSq(double u) => u == 0 ? 1 : Math.Pow(Math.Sin(Math.PI * u) / (Math.PI * u), 2);

    // ================= Scalar =================

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.8)]
    [InlineData(1.37)]
    [InlineData(2.0)]
    public void Blazed_IsSincSquaredOfTheDetuning(double alpha)
    {
        // Swanson eq. 2.4: η_m = [sin(π(α − m))/(π(α − m))]²; at α = 1 + ε eq. 2.7 gives sinc²(ε).
        for (int m = -3; m <= 4; m++)
            Close(SincSq(alpha - m), ScalarGratingEfficiency.Blazed(m, alpha), 1e-15, $"η_{m} at α = {alpha}");
        Close(SincSq(alpha - 1), ScalarGratingEfficiency.Blazed(1, alpha), 1e-15, "eq. 2.7 with ε = α − 1");
        // Eq. 2.8: designed for λ₀, used at λ: α = λ₀/λ.
        double lambda0 = 0.55, lambda = 0.55 / alpha;
        Close(SincSq(lambda0 / lambda - 1), ScalarGratingEfficiency.Blazed(1, ScalarGratingEfficiency.DepthInWaves(1.5, 1, lambda0 / 0.5, lambda)), 1e-14, "eq. 2.8");
        // Derived (Parseval): the orders sum to 1; the tails beyond ±M add about 2/(π²M).
        double sum = 0;
        for (int m = -5000; m <= 5000; m++) sum += ScalarGratingEfficiency.Blazed(m, alpha);
        Close(1, sum, 1e-4, "Σ η_m");
    }

    [Fact]
    public void Multilevel_IsTheExactFourierPowerOfTheStaircase()
    {
        // The closed form against the level-by-level Fourier sum (ScalarGratingEfficiency.Levels,
        // a separate evaluation of the same integral) for the staircase φ = 2πα⌊Nu⌋/N.
        foreach (int levels in new[] { 2, 3, 4, 8, 16 })
            foreach (double alpha in new[] { 1.0, 0.9, 1.25, 2.0 })
            {
                var widths = Enumerable.Repeat(1.0 / levels, levels).ToArray();
                var phases = Enumerable.Range(0, levels).Select(k => 2 * Math.PI * alpha * k / levels).ToArray();
                double sum = 0;
                for (int m = -40; m <= 40; m++)
                {
                    double exact = ScalarGratingEfficiency.Multilevel(m, alpha, levels);
                    Close(ScalarGratingEfficiency.Levels(m, widths, phases), exact, 1e-13, $"N = {levels}, α = {alpha}, m = {m}");
                    sum += exact;
                }
                Assert.True(sum <= 1 + 1e-12, $"Σ η = {sum:R}");
            }
        // N → ∞ is the blazed profile: the difference is O(1/N²) (sinc(m/N) → 1, the bracket → sinc(α − m)).
        for (int m = -2; m <= 3; m++)
            Close(ScalarGratingEfficiency.Blazed(m, 1.1), ScalarGratingEfficiency.Multilevel(m, 1.1, 4096), 1e-6, $"N → ∞, m = {m}");
    }

    [Theory]
    // Swanson, Table 3-1 "Multi-level Diffraction Efficiency for Various Numbers of Phase Levels",
    // p. 15: first-order efficiency η₁ᴺ at the design wavelength, two decimals.
    [InlineData(2, 0.41)]
    [InlineData(3, 0.68)]
    [InlineData(4, 0.81)]
    [InlineData(5, 0.87)]
    [InlineData(6, 0.91)]
    [InlineData(8, 0.95)]
    [InlineData(12, 0.98)]
    [InlineData(16, 0.99)]
    public void Multilevel_AtTheDesignWavelength_MatchesSwansonsTable31(int levels, double table)
    {
        double eta = ScalarGratingEfficiency.Multilevel(1, 1, levels);
        Close(SincSq(1.0 / levels), eta, 1e-15, "eq. 3.2: sinc²(1/N)");
        // The table rounds to two decimals; its N = 5 entry (0.87 for sinc²(1/5) = 0.8751…) is cut
        // rather than rounded, so the tolerance is just over half a unit in the last place.
        Close(table, eta, 0.0052, $"Table 3-1, N = {levels}");
    }

    [Fact]
    public void Binary_IsTheTwoLevelStaircase_AndConservesEnergy()
    {
        // Derived: f = ½ and φ = πα is the N = 2 staircase.
        foreach (double alpha in new[] { 0.5, 1.0, 1.3 })
            for (int m = -5; m <= 5; m++)
                Close(ScalarGratingEfficiency.Multilevel(m, alpha, 2), ScalarGratingEfficiency.Binary(m, Math.PI * alpha, 0.5), 1e-15, $"α = {alpha}, m = {m}");
        // π step, half fill: η₀ = 0 and η±1 = 4/π² (derived from the closed form).
        Close(0, ScalarGratingEfficiency.Binary(0, Math.PI, 0.5), 1e-15, "η₀");
        Close(4 / (Math.PI * Math.PI), ScalarGratingEfficiency.Binary(1, Math.PI, 0.5), 1e-15, "η₁");
        double sum = 0;
        for (int m = -20000; m <= 20000; m++) sum += ScalarGratingEfficiency.Binary(m, 2.1, 0.3);
        Close(1, sum, 1e-4, "Σ η");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.8412)]
    [InlineData(2.405)]
    [InlineData(5.0)]
    public void ThinSinusoidalPhaseGrating_GivesBesselSquared(double peakPhase)
    {
        // The Jacobi–Anger result η_m = J_m(a)² against the Fourier power of the sinusoid sampled
        // in 8192 levels (midpoint phases): the level sum differs from the integral by
        // O((2πa/N)²), about 1e-6 at a = 5 (tolerance 1e-5).
        int levels = 8192;
        var widths = Enumerable.Repeat(1.0 / levels, levels).ToArray();
        var phases = Enumerable.Range(0, levels).Select(k => peakPhase * Math.Sin(2 * Math.PI * (k + 0.5) / levels)).ToArray();
        double sum = 0;
        for (int m = -12; m <= 12; m++)
        {
            double j = Bessel.Jn(m, peakPhase);
            Close(j * j, ScalarGratingEfficiency.Sinusoidal(m, peakPhase), 1e-15, $"η_{m} = J_m²");
            Close(j * j, ScalarGratingEfficiency.Levels(m, widths, phases), 1e-5, $"sampled sinusoid, m = {m}");
            sum += j * j;
        }
        Close(1, sum, 1e-12, "Σ J_m²");
    }

    // ================= RCWA =================

    private static readonly Complex Glass = new(1.5, 0);

    private static GratingStructure CaseA() => new(1.5e-6, 1.0,
        new[] { GratingLayer.Lamellar(0.5e-6, 0.5, Glass, Complex.One) }, Glass);

    [Theory]
    [InlineData(GratingPolarization.TE, 0.0)]
    [InlineData(GratingPolarization.TE, 30.0)]
    [InlineData(GratingPolarization.TM, 30.0)]
    [InlineData(GratingPolarization.TM, 70.0)]
    public void BareInterface_IsFresnel(GratingPolarization polarization, double angle)
    {
        // Derived: with no layer the boundary system gives t = 2Y_c/(Y_c + Y_s) (TE) and the H-field
        // t = 2(Y_c/ε_c)/(Y_c/ε_c + Y_s/ε_s) (TM), so R = ((a − b)/(a + b))² with a = n₁cos θ₁,
        // b = n₂cos θ₂ (TE) or a = cos θ₁/n₁, b = cos θ₂/n₂ (TM), and R + T = 1.
        double n1 = 1, n2 = 1.5, t1 = angle * Math.PI / 180;
        double s2 = n1 * Math.Sin(t1) / n2, c2 = Math.Sqrt(1 - s2 * s2), c1 = Math.Cos(t1);
        double a = polarization == GratingPolarization.TE ? n1 * c1 : c1 / n1;
        double b = polarization == GratingPolarization.TE ? n2 * c2 : c2 / n2;
        double r = (a - b) / (a + b);
        var result = Rcwa1D.Solve(new GratingStructure(1e-6, n1, Array.Empty<GratingLayer>(), n2), new RcwaIncidence(0.6e-6, angle, polarization, 3));
        Close(r * r, result.Order(0)!.Reflected, 1e-14, "R₀");
        Close(1 - r * r, result.Order(0)!.Transmitted, 1e-14, "T₀");
        Close(angle == 0 ? 0 : Math.Asin(s2) * 180 / Math.PI, result.Order(0)!.TransmittedAngleDegrees, 1e-10, "Snell");
    }

    [Theory]
    [InlineData(GratingPolarization.TE, 0.0, 0.0)]
    [InlineData(GratingPolarization.TE, 25.0, 0.0)]
    [InlineData(GratingPolarization.TM, 25.0, 0.0)]
    [InlineData(GratingPolarization.TE, 40.0, 0.3)]
    [InlineData(GratingPolarization.TM, 40.0, 0.3)]
    public void HomogeneousFilm_IsTheAiryFormula(GratingPolarization polarization, double angle, double extinction)
    {
        // Derived (thin-film interference, e^{+iωt}): with k_z,j/k₀ = √(ε_j − (n₁ sin θ)²) (Im ≤ 0),
        // p_j = k_z,j (TE) or k_z,j/ε_j (TM), r_ij = (p_i − p_j)/(p_i + p_j), t_ij = 2p_i/(p_i + p_j)
        // and β = k₀·k_z,2·h: r = (r₁₂ + r₂₃e^{−2iβ})/(1 + r₁₂r₂₃e^{−2iβ}),
        // t = t₁₂t₂₃e^{−iβ}/(1 + r₁₂r₂₃e^{−2iβ}), R = |r|², T = |t|²·Re(p₃)/p₁.
        double lambda = 0.633e-6, h = 0.41e-6, n1 = 1.0;
        var film = new Complex(2.1, extinction);
        Complex n3 = 1.52;
        double kx = n1 * Math.Sin(angle * Math.PI / 180);
        Complex Eps(Complex n) { var c = Complex.Conjugate(n); return c * c; }
        Complex Kz(Complex e) { var s = Complex.Sqrt(e - kx * kx); return s.Imaginary > 0 ? -s : s; }
        bool te = polarization == GratingPolarization.TE;
        Complex P(Complex n) => te ? Kz(Eps(n)) : Kz(Eps(n)) / Eps(n);
        Complex p1 = P(n1), p2 = P(film), p3 = P(n3);
        Complex r12 = (p1 - p2) / (p1 + p2), r23 = (p2 - p3) / (p2 + p3);
        Complex t12 = 2 * p1 / (p1 + p2), t23 = 2 * p2 / (p2 + p3);
        Complex beta = 2 * Math.PI / lambda * Kz(Eps(film)) * h;
        Complex phase2 = Complex.Exp(-2 * Complex.ImaginaryOne * beta);
        Complex r = (r12 + r23 * phase2) / (1 + r12 * r23 * phase2);
        Complex t = t12 * t23 * Complex.Exp(-Complex.ImaginaryOne * beta) / (1 + r12 * r23 * phase2);
        double rr = r.Magnitude * r.Magnitude, tt = t.Magnitude * t.Magnitude * p3.Real / p1.Real;
        var result = Rcwa1D.Solve(new GratingStructure(0.8e-6, n1, new[] { GratingLayer.Film(h, film) }, n3),
            new RcwaIncidence(lambda, angle, polarization, 4));
        Close(rr, result.Order(0)!.Reflected, 1e-13, "R");
        Close(tt, result.Order(0)!.Transmitted, 1e-13, "T");
        Close(1 - rr - tt, result.Absorbed, 1e-13, "absorbed");
        if (extinction == 0) Close(1, result.TotalReflected + result.TotalTransmitted, 1e-13, "R + T");
    }

    [Theory]
    [InlineData(GratingPolarization.TE, 0.0, 0.5, 15)]
    [InlineData(GratingPolarization.TM, 0.0, 0.5, 15)]
    [InlineData(GratingPolarization.TE, 33.0, 0.3, 25)]
    [InlineData(GratingPolarization.TM, 33.0, 0.3, 25)]
    [InlineData(GratingPolarization.TM, -12.0, 0.7, 30)]
    public void LosslessGratings_ConserveEnergyOverAllOrders(GratingPolarization polarization, double angle, double fill, int harmonics)
    {
        // A lossless stack has no absorption: Σ R + Σ T = 1, to the rounding of the linear algebra
        // (1e-10 here; the solves are well conditioned at these sizes). Silicon-like ridges (n 3.5)
        // under a film, a deep lamellar layer and a staircase.
        var layers = new List<GratingLayer> { GratingLayer.Film(0.1e-6, new Complex(1.46, 0)) };
        layers.Add(GratingLayer.Lamellar(0.9e-6, fill, new Complex(3.5, 0), Complex.One));
        layers.AddRange(GratingProfiles.Blazed(0.3e-6, 3, new Complex(2.0, 0), Complex.One));
        var structure = new GratingStructure(0.9e-6, 1.0, layers, new Complex(1.46, 0));
        var result = Rcwa1D.Solve(structure, new RcwaIncidence(0.8e-6, angle, polarization, harmonics));
        Close(1, result.TotalReflected + result.TotalTransmitted, 1e-10, "Σ R + Σ T");
        Assert.True(result.TransmissionDefined);
        Assert.All(result.Orders, o => Assert.True(o.Reflected >= 0 && o.Transmitted >= 0));
    }

    [Theory]
    // EMpy (ElectroMagneticPython 2.2.4, MIT, IsotropicRCWA, Moharam 1995 / Lalanne 1996), run on
    // 2026-11-15 with: cover air, BinaryGrating(glass n 1.5, air, duty 0.5, pitch 1.5 µm, thickness
    // 0.5 µm), substrate glass n 1.5, λ 1 µm, alpha 10°, delta 0, phi 90°, n = 40 orders; psi 90° (TE)
    // and 0° (TM). EMpy's order i is k_x = k₁ₓ − iK, this program's m = −i.
    [InlineData(GratingPolarization.TE, 1, 0.00146097437646787, 0.238894148962097)]
    [InlineData(GratingPolarization.TE, 0, 0.0244870768624191, 0.536476885444474)]
    [InlineData(GratingPolarization.TE, -1, 0.00234255855895917, 0.154397538328429)]
    [InlineData(GratingPolarization.TE, -2, 0.0, 0.0419408174671049)]
    [InlineData(GratingPolarization.TM, 1, 0.00210518917155844, 0.189924738506688)]
    [InlineData(GratingPolarization.TM, 0, 0.0267428520579249, 0.609926659800093)]
    [InlineData(GratingPolarization.TM, -1, 9.45324754130249e-05, 0.163502815056221)]
    [InlineData(GratingPolarization.TM, -2, 0.0, 0.00770321293207363)]
    public void DielectricLamellarGrating_MatchesEMpy(GratingPolarization polarization, int order, double reflected, double transmitted)
    {
        var result = Rcwa1D.Solve(CaseA(), new RcwaIncidence(1e-6, 10, polarization, 40));
        Close(reflected, result.Order(order)!.Reflected, 1e-10, $"R_{order}");
        Close(transmitted, result.Order(order)!.Transmitted, 1e-10, $"T_{order}");
    }

    [Theory]
    // EMpy as above: cover air, BinaryGrating(metal, air, duty 0.5, pitch 1 µm, thickness 0.2 µm),
    // substrate the same metal, λ 1 µm, alpha 20°, n = 30 orders. The metal is EMpy's
    // n0_const = 0.2 − 6j, which is this program's 0.2 + 6i (EMpy writes the absorbing index n − ik).
    [InlineData(GratingPolarization.TE, 0, 0.714520705403701)]
    [InlineData(GratingPolarization.TE, -1, 0.259371899453009)]
    [InlineData(GratingPolarization.TM, 0, 0.0783112550031369)]
    [InlineData(GratingPolarization.TM, -1, 0.842159667308333)]
    public void MetallicLamellarGrating_MatchesEMpy_AndReportsAbsorption(GratingPolarization polarization, int order, double reflected)
    {
        var metal = new Complex(0.2, 6.0);
        var structure = new GratingStructure(1e-6, 1.0, new[] { GratingLayer.Lamellar(0.2e-6, 0.5, metal, Complex.One) }, metal);
        var result = Rcwa1D.Solve(structure, new RcwaIncidence(1e-6, 20, polarization, 30));
        Close(reflected, result.Order(order)!.Reflected, 1e-10, $"R_{order}");
        Assert.False(result.TransmissionDefined);
        Assert.Equal(0, result.TotalTransmitted);
        Close(1 - result.TotalReflected, result.Absorbed, 1e-15, "absorbed = 1 − Σ R");
        Assert.True(result.Absorbed > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void RcwaApproachesTheScalarResult_AsThePeriodGrows()
    {
        // An 8-level staircase (α = 1 in transmission, n 1.5 into air-cut grooves, on n 1.5) at
        // normal incidence, TE. The scalar theory ignores Fresnel reflection, so the comparison is the
        // share of the transmitted power in order +1, T₁/ΣT, against sinc²(1/8) (Swanson eq. 3.2).
        // The stated trend: the gap to the scalar value shrinks as Λ/λ goes 4 → 8 → 16 (each step at
        // least by a third), +1 dominates −1 (the profile thickens toward +x), and energy is conserved.
        double lambda = 1e-6;
        double depth = lambda / 0.5;   // (n − 1)·H/λ = 1
        double scalar = ScalarGratingEfficiency.Multilevel(1, 1, 8);
        double previous = double.MaxValue;
        foreach (int ratio in new[] { 4, 8, 16 })
        {
            var layers = GratingProfiles.Multilevel(depth, 8, Glass, Complex.One);
            var structure = new GratingStructure(ratio * lambda, 1.0, layers, Glass);
            var result = Rcwa1D.Solve(structure, new RcwaIncidence(lambda, 0, GratingPolarization.TE, 3 * ratio));
            double share = result.Order(1)!.Transmitted / result.TotalTransmitted;
            double gap = Math.Abs(share - scalar);
            Assert.True(gap < previous * (2.0 / 3.0), $"Λ/λ = {ratio}: gap {gap:R} did not shrink from {previous:R}");
            Assert.True(result.Order(1)!.Transmitted > 10 * result.Order(-1)!.Transmitted, $"Λ/λ = {ratio}: +1 should dominate −1");
            Close(1, result.TotalReflected + result.TotalTransmitted, 1e-9, "energy");
            previous = gap;
        }
    }

    [Fact]
    public void Rcwa_RefusesWhatItCannotModel_ByName()
    {
        Assert.Contains("harmonics", Assert.Throws<ArgumentException>(() => Rcwa1D.Solve(CaseA(), new RcwaIncidence(1e-6, 0, GratingPolarization.TE, Rcwa1D.MaxHarmonics + 1))).Message);
        Assert.Contains("angle of incidence", Assert.Throws<ArgumentException>(() => Rcwa1D.Solve(CaseA(), new RcwaIncidence(1e-6, 90, GratingPolarization.TE, 3))).Message);
        Assert.Contains("cover index", Assert.Throws<ArgumentException>(() => new GratingStructure(1e-6, 0, Array.Empty<GratingLayer>(), Glass)).Message);
        Assert.Contains("gain", Assert.Throws<ArgumentException>(() => GratingLayer.Film(1e-7, new Complex(1.5, -0.1))).Message);
        Assert.Contains("add to", Assert.Throws<ArgumentException>(() => new GratingLayer(1e-7, new[] { new GratingSegment(0.4, Glass), new GratingSegment(0.4, Complex.One) })).Message);
    }

    // ================= Kogelnik =================

    [Theory]
    [InlineData(0.9, 0.8, 0.7, 0.0)]
    [InlineData(0.9, 0.8, 1.2, 1.5)]
    [InlineData(1.0, 0.6, 2.0, -3.0)]
    public void TransmissionGrating_IsKogelnikEquation43(double cr, double cs, double nu, double xi)
    {
        // Eq. 42–43: ν = κd/√(c_R c_S), ξ = ϑd/(2c_S), η = sin²√(ν² + ξ²)/(1 + ξ²/ν²); lossless, so
        // the undiffracted wave carries 1 − η (eq. 24).
        double d = 1e-5;
        double kappa = nu * Math.Sqrt(cr * cs) / d;
        double dephasing = 2 * cs * xi / d;
        var e = KogelnikCoupledWave.Solve(new KogelnikParameters(kappa, 0, dephasing, cr, cs, d));
        double root = Math.Sqrt(nu * nu + xi * xi);
        double expected = Math.Pow(Math.Sin(root), 2) / (1 + xi * xi / (nu * nu));
        Assert.False(e.Reflection);
        Close(expected, e.Diffracted, 1e-13, "η");
        Close(1 - expected, e.Undiffracted, 1e-13, "1 − η");
    }

    [Theory]
    [InlineData(0.9, -0.8, 0.7, 0.0)]
    [InlineData(0.9, -0.8, 1.2, 0.6)]
    [InlineData(1.0, -0.6, 2.0, 1.9)]
    public void ReflectionGrating_IsKogelnikEquation57(double cr, double cs, double nu, double xi)
    {
        // Eq. 56–57 (c_S < 0): ν = κd/√(c_R|c_S|), ξ = −ϑd/(2c_S),
        // η = 1/(1 + (1 − ξ²/ν²)/sinh²√(ν² − ξ²)), here inside the stop band (ξ < ν).
        double d = 1e-5;
        double kappa = nu * Math.Sqrt(cr * -cs) / d;
        double dephasing = -2 * cs * xi / d;
        var e = KogelnikCoupledWave.Solve(new KogelnikParameters(kappa, 0, dephasing, cr, cs, d));
        double sh = Math.Sinh(Math.Sqrt(nu * nu - xi * xi));
        double expected = 1 / (1 + (1 - xi * xi / (nu * nu)) / (sh * sh));
        Assert.True(e.Reflection);
        Close(expected, e.Diffracted, 1e-13, "η");
        Close(1 - expected, e.Undiffracted, 1e-13, "1 − η");
    }

    [Theory]
    // Kogelnik p. 2923 (transmission, Fig. 6): "ν = π/4 and a peak diffraction efficiency of η₀ = 0.5,
    // with ν = π/2 and a peak efficiency of η₀ = 1, and with ν = 3π/4 and η₀ = 0.5".
    [InlineData(1, false, 0.5)]
    [InlineData(2, false, 1.0)]
    [InlineData(3, false, 0.5)]
    // Kogelnik pp. 2932–2933 (reflection, Fig. 12): "ν = π/4 and a peak efficiency of 43 percent, a
    // grating with ν = π/2 and η₀ = 0.84, and … ν = 3π/4 and η₀ = 0.96" (eq. 59, tanh²ν, two decimals).
    [InlineData(1, true, 0.43)]
    [InlineData(2, true, 0.84)]
    [InlineData(3, true, 0.96)]
    public void PeakEfficiencies_MatchKogelnik(int quarters, bool reflection, double stated)
    {
        double nu = quarters * Math.PI / 4;
        var e = KogelnikCoupledWave.Solve(new KogelnikParameters(nu, 0, 0, 1, reflection ? -1 : 1, 1));
        Close(stated, e.Diffracted, 0.005, $"η₀ at ν = {quarters}π/4");
        Close(reflection ? Math.Pow(Math.Tanh(nu), 2) : Math.Pow(Math.Sin(nu), 2), e.Diffracted, 1e-14, "eq. 59 / eq. 43 at ξ = 0");
    }

    [Fact]
    public void LossyGrating_WithoutCoupling_DecaysAsBeersLaw()
    {
        // Derived from eq. 21 with κ = 0: c_R R′ + αR = 0, so |R(d)|² = e^{−2αd/c_R}; with κ ≠ 0 the
        // two outputs add to less than 1.
        var e = KogelnikCoupledWave.Solve(new KogelnikParameters(0, 2e3, 0, 0.8, 0.7, 1e-4));
        Close(Math.Exp(-2 * 2e3 * 1e-4 / 0.8), e.Undiffracted, 1e-15, "|R(d)|²");
        var lossy = KogelnikCoupledWave.Solve(new KogelnikParameters(new Complex(1.5e4, -2e3), 2e3, 300, 0.8, 0.7, 1e-4));
        Assert.True(lossy.Diffracted + lossy.Undiffracted < 1);
    }

    // ---- the volume hologram surface ----

    private const double ConstructionMicrometers = 0.5;
    private const double EmulsionIndex = 1.5, Modulation = 0.01;
    private static readonly double ConstructionAngle = 20 * Math.PI / 180;

    /// <summary>An unslanted transmission hologram recorded in air by two plane waves at ±20°
    /// (construction points at infinity, Hologram 1, order −1), thickness for ν = π/2 at Bragg.</summary>
    private static (SequentialLens Lens, double InsideAngle, double Thickness) UnslantedHologram()
    {
        var r1 = new Vector3D(0, Math.Sin(ConstructionAngle), Math.Cos(ConstructionAngle));
        var r2 = new Vector3D(0, -Math.Sin(ConstructionAngle), Math.Cos(ConstructionAngle));
        double inside = Math.Asin(Math.Sin(ConstructionAngle) / EmulsionIndex);
        double lambda = ConstructionMicrometers * 1e-6;
        double d = 0.5 * lambda * Math.Cos(inside) / Modulation;   // ν = πΔn·d/(λ cos θ) = π/2
        var hologram = new HologramShape(1, -1e6 * r1, -1e6 * r2, ConstructionMicrometers, -1)
        {
            Volume = new VolumeHologram(d, 1, 1, EmulsionIndex, Modulation, 0, 0, false),
        };
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = 0.01, Medium = ParaxialMedium.Constant(1), IsStop = true, Shape = hologram },
                new SequentialSurface(),
            }
        };
        return (lens, inside, d);
    }

    private static OpticalSystemData System(double micrometers, bool unpolarized = false) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = micrometers } } },
        Polarization = new SystemPolarization { Unpolarized = unpolarized },
    };

    [Fact]
    public void VolumeHologram_AtBragg_GivesSinSquaredNu_TEAndTM()
    {
        // Derived: the construction beams refract into the emulsion symmetrically, so K_g lies in
        // the surface (unslanted), and the readout along the first beam at λc is Bragg matched for
        // m = −1 (σ = k₂). Eq. 43 at ξ = 0: η_TE = sin²ν = 1 by the chosen thickness; eq. 90 for TM:
        // κ‖ = κ·cos 2θ (θ the angle inside the emulsion), η_TM = sin²(ν cos 2θ). The system's default
        // Jones vector (1, 0) lies along x, normal to the y–z plane of incidence: TE.
        var (lens, inside, _) = UnslantedHologram();
        var s = VolumeHologramScan.Sample(lens, System(ConstructionMicrometers), 1, 0, 20, ConstructionMicrometers);
        Assert.Null(s.Failure);
        Close(1, s.TE, 1e-12, "η_TE");
        Close(Math.Pow(Math.Sin(Math.PI / 2 * Math.Cos(2 * inside)), 2), s.TM, 1e-12, "η_TM");
        Close(s.TE, s.Diffracted, 1e-15, "x-polarized light is TE here");
        Close(inside * 180 / Math.PI, s.IncidenceDegrees, 1e-9, "angle in the emulsion");
        var unpolarized = VolumeHologramScan.Sample(lens, System(ConstructionMicrometers, unpolarized: true), 1, 0, 20, ConstructionMicrometers);
        Close(0.5 * (s.TE + s.TM), unpolarized.Diffracted, 1e-15, "unpolarized = mean");
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(0.505)]
    [InlineData(0.52)]
    public void VolumeHologram_OffBraggWavelength_FollowsEquation43(double micrometers)
    {
        // Derived: K = 2·(2π/λc)·sin 20° along y (|K_g|, unslanted, φ = 90° in Kogelnik's frame); the
        // readout keeps its 20° in air, so inside sin θ = sin 20°/n and β = 2πn/λ. Eq. 17:
        // ϑ = K cos(φ − θ) − K²λ/(4πn) = K sin θ − K²λ/(4πn); c_R = c_S = cos θ (eq. 23 with φ = 90°);
        // ν = πΔn·d/(λ cos θ), ξ = ϑd/(2 cos θ), and eq. 43.
        var (lens, inside, d) = UnslantedHologram();
        double lambda = micrometers * 1e-6;
        double k = 2 * (2 * Math.PI / (ConstructionMicrometers * 1e-6)) * Math.Sin(ConstructionAngle);
        double dephasing = k * Math.Sin(inside) - k * k * lambda / (4 * Math.PI * EmulsionIndex);
        double nu = Math.PI * Modulation * d / (lambda * Math.Cos(inside));
        double xi = dephasing * d / (2 * Math.Cos(inside));
        double expected = Math.Pow(Math.Sin(Math.Sqrt(nu * nu + xi * xi)), 2) / (1 + xi * xi / (nu * nu));
        var s = VolumeHologramScan.Sample(lens, System(micrometers), 1, 0, 20, micrometers);
        Close(expected, s.TE, 1e-11, $"η_TE at {micrometers} µm");
        Close(1, s.TE + VolumeHologramEfficiencies.At((TwoPointHologram)lens.Surfaces[1].Shape!, new Vector3D(0, 0, 0),
            new Vector3D(0, Math.Sin(ConstructionAngle), Math.Cos(ConstructionAngle)), Vector3D.UnitZ, 1, lambda, (1, 0), 1).Undiffracted, 1e-12, "η + undiffracted");
    }

    [Fact]
    public void VolumeHologramAnalyses_ScanAndPlot()
    {
        var (lens, _, _) = UnslantedHologram();
        var context = new OpticsAnalysisContext("t", new OpticsSettings
        {
            SystemData = new OpticsSystemSettings
            {
                Fields = new List<OpticsFieldSettings> { new() { X = 0, Y = 20 } },
                Wavelengths = new List<OpticsWavelengthSettings> { new() { Micrometers = ConstructionMicrometers } },
            }
        });
        var settings = new VolumeHologramEfficiencySettings { Sampling = 5 };
        var angle = new EfficiencyVsAngleAnalysis(_ => lens).Compute(context, settings, CancellationToken.None);
        var curve = ((LinePlot)angle.Plots[0]).Curves[0];
        Assert.Equal(5, curve.X.Count);
        Close(19.8, curve.X[0], 1e-12, "99% of the field's Y");
        Close(20.2, curve.X[^1], 1e-12, "101% of the field's Y");
        Close(1, curve.Y[2], 1e-9, "Bragg in the middle");
        var wave = new EfficiencyVsWavelengthAnalysis(_ => lens).Compute(context, settings, CancellationToken.None);
        Assert.Equal(5, ((LinePlot)wave.Plots[0]).Curves[0].X.Count);
        var map = new DiffractionEfficiencyAnalysis(_ => lens).Compute(context, settings, CancellationToken.None);
        var field = ((AnalysisMapPlot)map.Plots[0]).Field;
        Assert.Equal(5, field.Rows);
        Close(1, field.Values[2, 2], 1e-9, "Bragg at the centre of the map");
        // A lens without a volume hologram is refused by name.
        var plain = new SequentialLens { Surfaces = new[] { lens.Surfaces[0], lens.Surfaces[1] with { Shape = null }, lens.Surfaces[2] } };
        Assert.Contains("volume hologram", Assert.Throws<ArgumentException>(() => new EfficiencyVsAngleAnalysis(_ => plain).Compute(context, settings, CancellationToken.None)).Message);
    }

    [Fact]
    public void VolumeHologramParameters_RoundTripThroughTheLayouts()
    {
        var h1 = SurfaceTypes.Find("Hologram 1")!;
        var values = new Dictionary<int, double>
        {
            [3] = -0.1, [6] = -0.2, [7] = 0.6328, [8] = 1, [13] = 1,
            [14] = 2e-5, [15] = 1, [16] = 1.2, [17] = 1.5, [18] = 0.02, [19] = 0.98, [20] = 0.003, [21] = 0,
        };
        var shape = (HologramShape)h1.Build(n => values.TryGetValue(n, out double v) ? v : 0)!;
        Assert.NotNull(shape.Volume);
        Assert.Equal(1.2, shape.Volume!.N2);
        var read = h1.Read(shape);
        foreach (var (number, value) in values) Assert.Equal(value, read.TryGetValue(number, out double r) ? r : 0);
        values[21] = 1;
        Assert.Contains("Consider Fresnel", Assert.Throws<ArgumentException>(() => h1.Build(n => values.TryGetValue(n, out double v) ? v : 0)).Message);
        var torus = SurfaceTypes.Find("Toroidal Hologram")!;
        var t = new Dictionary<int, double> { [15] = -0.1, [18] = -0.2, [19] = 0.6328, [20] = 1, [21] = 1, [22] = 1, [23] = 1e-5, [24] = 1, [25] = 1, [26] = 1.5, [27] = 0.01 };
        var toroidal = (ToroidalHologram)torus.Build(n => t.TryGetValue(n, out double v) ? v : 0)!;
        Assert.Equal(1e-5, toroidal.Volume!.Thickness);
        // The ray trace ignores the volume data: with and without it, the same ray leaves alike.
        var plainShape = new HologramShape(1, new Vector3D(0, 0.01, -0.1), new Vector3D(0, -0.01, -0.2), 0.6328, 1);
        var volumeShape = new HologramShape(1, new Vector3D(0, 0.01, -0.1), new Vector3D(0, -0.01, -0.2), 0.6328, 1)
        {
            Volume = new VolumeHologram(2e-5, 1, 1, 1.5, 0.02, 0, 0, false),
        };
        Vector3D After(HologramShape h)
        {
            var lens = new SequentialLens { Surfaces = new[] { new SequentialSurface { Thickness = 0.1 }, new SequentialSurface { Thickness = 0.1, IsStop = true, Shape = h }, new SequentialSurface() } };
            var trace = SequentialTrace.Create(lens, System(0.6328));
            var path = trace.TracePath(trace.NewRay(new Vector3D(0, 0, -0.1), new Vector3D(0, 0.02, 1).Normalized()));
            return path.Hits[1].Direction;
        }
        Assert.Equal(After(plainShape), After(volumeShape));
    }

    // ---- the grating analysis ----

    [Fact]
    public void GratingEfficiencyAnalysis_RcwaAndScalar()
    {
        var context = new OpticsAnalysisContext("t", new OpticsSettings
        {
            SystemData = new OpticsSystemSettings { Wavelengths = new List<OpticsWavelengthSettings> { new() { Micrometers = 1.0 } } }
        });
        var analysis = new GratingEfficiencyAnalysis(_ => null);
        var rcwa = analysis.Compute(context, new GratingEfficiencySettings { PeriodMicrometers = 1.5, DepthMicrometers = 0.5, AngleDegrees = 10, Harmonics = 40 }, CancellationToken.None);
        Assert.Contains("0.238894", rcwa.Text);   // EMpy's T₊₁ above
        var point = GratingEfficiencyAnalysis.Evaluate(new GratingEfficiencySettings { Harmonics = 10, Polarization = GratingPolarizationSetting.Unpolarized }, 1.5e-6, 10, 1e-6);
        Close(1, point.TotalReflected + point.TotalTransmitted, 1e-10, "unpolarized energy");
        var scalar = GratingEfficiencyAnalysis.Evaluate(new GratingEfficiencySettings
        {
            Method = GratingEfficiencyMethod.Scalar, Profile = GratingProfileKind.Blazed, DepthMicrometers = 2, Harmonics = 3,
        }, 10e-6, 0, 1e-6);
        Close(1, scalar.Orders.First(o => o.Order == 1).Transmitted, 1e-15, "α = (1.5 − 1)·2/1 = 1: all in +1");
        var swept = analysis.Compute(context, new GratingEfficiencySettings { Sweep = GratingSweep.Angle, SweepMinimum = 0, SweepMaximum = 20, SweepSamples = 3, Harmonics = 8 }, CancellationToken.None);
        Assert.Equal(2, swept.Plots.Count);
    }
}
