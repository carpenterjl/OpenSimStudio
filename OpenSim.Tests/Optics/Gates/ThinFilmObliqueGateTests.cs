using System.Numerics;
using OpenSim.Optics.Coatings;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-101 additions to the OP-15 gate: the closed forms of ThinFilmTests are at normal incidence
/// (and Brewster's zero), and oblique incidence is checked only against tmm's numbers. These pin
/// oblique incidence by closed forms derived here from the tilted-admittance definitions that
/// <see cref="ThinFilm"/> states (η = n cos θ for s, n / cos θ for p, docs/Optics-Conventions.md
/// §12): a bare interface reflects ((η0 − ηs)/(η0 + ηs))², which is Fresnel's pair; a layer of
/// phase thickness δ = 2π n1 d cos θ1 / λ = π/2 has the matrix [[0, −i/η1], [−iη1, 0]] and turns
/// the substrate admittance ηs into η1²/ηs; δ = π gives −I, so the layer is absent. Indices are
/// arbitrary test inputs, not material data.
/// </summary>
public sealed class ThinFilmObliqueGateTests
{
    private const double Deg = Math.PI / 180;
    private static readonly ThinFilmLayer[] None = Array.Empty<ThinFilmLayer>();

    /// <summary>cos θ in a medium of index n for the incident invariant n0 sin θ0 (no TIR here).</summary>
    private static double Cos(double n0, double theta0, double n) =>
        Math.Sqrt(1 - Math.Pow(n0 * Math.Sin(theta0) / n, 2));

    private static double R(double a, double b) => Math.Pow((a - b) / (a + b), 2);

    private static readonly (double N0, double N1, double Ns)[] Cases =
    {
        (1.0, 1.38, 1.52), (1.0, 2.1, 1.75), (1.33, 1.46, 2.4), (1.0, 1.65, 4.0),
    };

    [Fact]
    public void ABareInterface_AtEveryAngle_GivesFresnelsPair()
    {
        foreach (var (n0, _, ns) in Cases)
            for (double deg = 0; deg < 90; deg += 5)
            {
                double t = deg * Deg, c0 = Math.Cos(t), cs = Cos(n0, t, ns);
                var r = ThinFilm.Compute(n0, None, ns, 0.55, t);
                double rs = R(n0 * c0, ns * cs), rp = R(n0 / c0, ns / cs);
                Assert.True(Math.Abs(rs - r.S.Reflectance) <= 1e-14, $"Rs at {deg}° for {n0} → {ns}: {r.S.Reflectance:R} vs {rs:R}");
                Assert.True(Math.Abs(rp - r.P.Reflectance) <= 1e-14, $"Rp at {deg}° for {n0} → {ns}: {r.P.Reflectance:R} vs {rp:R}");
                // Lossless: the rest is transmitted.
                Assert.True(Math.Abs(1 - rs - r.S.Transmittance) <= 1e-14, $"Ts at {deg}°");
                Assert.True(Math.Abs(1 - rp - r.P.Transmittance) <= 1e-14, $"Tp at {deg}°");
            }
    }

    [Fact]
    public void AQuarterWaveLayerAtItsTiltedThickness_TurnsTheAdmittance()
    {
        double lambda = 0.55;
        foreach (var (n0, n1, ns) in Cases)
            foreach (double deg in new[] { 0.0, 15, 30, 45, 60, 75 })
            {
                double t = deg * Deg, c0 = Math.Cos(t), c1 = Cos(n0, t, n1), cs = Cos(n0, t, ns);
                // Quarter wave at this angle: n1 d cos θ1 = λ/4.
                var layer = new[] { new ThinFilmLayer(n1, lambda / (4 * n1 * c1)) };
                var r = ThinFilm.Compute(n0, layer, ns, lambda, t);
                double etaS1 = n1 * c1, etaP1 = n1 / c1;
                double rs = R(n0 * c0, etaS1 * etaS1 / (ns * cs));
                double rp = R(n0 / c0, etaP1 * etaP1 / (ns / cs));
                Assert.True(Math.Abs(rs - r.S.Reflectance) <= 1e-14, $"Rs, quarter wave at {deg}°, {n0}/{n1}/{ns}: {r.S.Reflectance:R} vs {rs:R}");
                Assert.True(Math.Abs(rp - r.P.Reflectance) <= 1e-14, $"Rp, quarter wave at {deg}°, {n0}/{n1}/{ns}: {r.P.Reflectance:R} vs {rp:R}");
            }
    }

    [Fact]
    public void AHalfWaveLayerAtItsTiltedThickness_IsAbsent_AndOnlyAtThatThickness()
    {
        double lambda = 0.6328;
        foreach (var (n0, n1, ns) in Cases)
            foreach (double deg in new[] { 10.0, 35, 55, 70 })
            {
                double t = deg * Deg, c1 = Cos(n0, t, n1);
                var bare = ThinFilm.Compute(n0, None, ns, lambda, t);
                var half = ThinFilm.Compute(n0, new[] { new ThinFilmLayer(n1, lambda / (2 * n1 * c1)) }, ns, lambda, t);
                Assert.True(Math.Abs(bare.S.Reflectance - half.S.Reflectance) <= 1e-14, $"Rs, half wave at {deg}°");
                Assert.True(Math.Abs(bare.P.Reflectance - half.P.Reflectance) <= 1e-14, $"Rp, half wave at {deg}°");
                // The amplitude too: the matrix is −I, so ρ is unchanged in sign as well.
                Assert.True(Complex.Abs(bare.S.R - half.S.R) <= 1e-14, $"rs, half wave at {deg}°");
                Assert.True(Complex.Abs(bare.P.R - half.P.R) <= 1e-14, $"rp, half wave at {deg}°");

                // The normal-incidence half-wave thickness is NOT half a wave at this angle
                // (cos θ1 < 1), so it is not absent: the angle enters through cos θ1.
                var normalHalf = ThinFilm.Compute(n0, new[] { new ThinFilmLayer(n1, lambda / (2 * n1)) }, ns, lambda, t);
                Assert.True(Math.Abs(bare.S.Reflectance - normalHalf.S.Reflectance) > 1e-6,
                    $"a normal-incidence half wave looks absent at {deg}°, so the layer phase ignores the angle");
            }
    }
}
