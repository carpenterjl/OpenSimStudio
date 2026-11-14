using System.Numerics;
using OpenSim.Optics.Coatings;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-15 gates for the thin-film engine (<see cref="ThinFilm"/>): closed forms computed in the
/// test, energy balance in every case, and cross-check values from the tmm package. Indices
/// here are test constants chosen for the cases, not material data.
/// </summary>
public sealed class ThinFilmTests
{
    private static readonly ThinFilmLayer[] None = Array.Empty<ThinFilmLayer>();
    private const double Deg = Math.PI / 180;

    /// <summary>Bare surface at normal incidence: R = ((n0 − n1)/(n0 + n1))², the same for s and p,
    /// T = 1 − R, A = 0; and for an absorbing substrate N = n + ik, r = (n0 − N)/(n0 + N), so
    /// R = ((n0 − n)² + k²)/((n0 + n)² + k²) with everything not reflected absorbed.</summary>
    [Fact]
    public void BareSurface_AtNormalIncidence_GivesTheFresnelReflectance()
    {
        foreach (var (n0, n1) in new[] { (1.0, 1.52), (1.52, 1.0), (1.33, 2.4) })
        {
            var r = ThinFilm.Compute(n0, None, n1, 0.55, 0);
            double expected = Math.Pow((n0 - n1) / (n0 + n1), 2);
            Assert.Equal(expected, r.S.Reflectance, 15);
            Assert.Equal(expected, r.P.Reflectance, 15);
            Assert.Equal(1 - expected, r.S.Transmittance, 14);
            Assert.Equal(0, r.S.Absorptance, 14);
            // The sign convention (docs/Optics-Conventions.md §12, as tmm): r_s = (n0 − n1)/(n0 + n1),
            // r_p = −r_s at normal incidence, t = 2 n0 / (n0 + n1) for both.
            Assert.Equal((n0 - n1) / (n0 + n1), r.S.R.Real, 15);
            Assert.Equal(-(n0 - n1) / (n0 + n1), r.P.R.Real, 15);
            Assert.Equal(2 * n0 / (n0 + n1), r.S.T.Real, 15);
            Assert.Equal(2 * n0 / (n0 + n1), r.P.T.Real, 15);
        }

        var metal = new Complex(0.7, 7.0);
        var m = ThinFilm.Compute(1, None, metal, 0.55, 0);
        double rMetal = (Math.Pow(1 - 0.7, 2) + 49) / (Math.Pow(1 + 0.7, 2) + 49);
        Assert.Equal(rMetal, m.S.Reflectance, 14);
        Assert.Equal(1 - rMetal, m.S.Transmittance + m.S.Absorptance, 14);
        Assert.Equal(0, m.S.Absorptance, 14);   // a semi-infinite substrate: what enters it is "transmitted"
    }

    /// <summary>At Brewster's angle, tan θB = n1/n0, the p reflectance vanishes; s does not.</summary>
    [Fact]
    public void BrewsterAngle_GivesZeroPReflectance()
    {
        foreach (var (n0, n1) in new[] { (1.0, 1.52), (1.52, 1.0), (1.0, 2.4) })
        {
            double brewster = Math.Atan(n1 / n0);
            var r = ThinFilm.Compute(n0, None, n1, 0.6328, brewster);
            Assert.True(r.P.Reflectance < 1e-28, $"Rp = {r.P.Reflectance} at Brewster for {n0} → {n1}");
            Assert.True(r.S.Reflectance > 1e-3);
            Assert.Equal(1, r.P.Transmittance, 13);
        }
    }

    /// <summary>One quarter-wave layer, n1 d = λ/4, at normal incidence: the admittance seen at the
    /// front is n1²/ns, so R = ((n0 ns − n1²)/(n0 ns + n1²))². A half-wave layer is absent.</summary>
    [Fact]
    public void QuarterWaveLayer_GivesTheClosedFormReflectance()
    {
        double n0 = 1.0, n1 = 1.38, ns = 1.52, lambda = 0.55;
        var quarter = new[] { new ThinFilmLayer(n1, lambda / (4 * n1)) };
        var r = ThinFilm.Compute(n0, quarter, ns, lambda, 0);
        double expected = Math.Pow((n0 * ns - n1 * n1) / (n0 * ns + n1 * n1), 2);
        Assert.Equal(expected, r.S.Reflectance, 14);
        Assert.Equal(expected, r.P.Reflectance, 14);
        Assert.Equal(1, r.S.Reflectance + r.S.Transmittance + r.S.Absorptance, 14);

        var half = new[] { new ThinFilmLayer(n1, lambda / (2 * n1)) };
        var bare = ThinFilm.Compute(n0, None, ns, lambda, 0);
        Assert.Equal(bare.S.Reflectance, ThinFilm.Compute(n0, half, ns, lambda, 0).S.Reflectance, 14);
    }

    /// <summary>
    /// A quarter-wave stack (HL)^N on a substrate at its centre wavelength, normal incidence.
    /// Derivation: a quarter-wave layer of index n turns the admittance Y behind it into n²/Y.
    /// Reading from the substrate (Y = ns) towards the incident side, the layers met are L, H,
    /// L, H, …; one L then one H turns Y into nH² / (nL² / Y) = (nH/nL)² Y. After N pairs
    /// Y = (nH/nL)^(2N) ns, and R = ((n0 − Y)/(n0 + Y))².
    /// </summary>
    [Fact]
    public void QuarterWaveStack_CentreReflectance_MatchesTheClosedForm()
    {
        double n0 = 1.0, nH = 2.35, nL = 1.38, ns = 1.52, lambda = 0.633;
        foreach (int pairs in new[] { 1, 3, 8, 15 })
        {
            var layers = new List<ThinFilmLayer>();
            for (int i = 0; i < pairs; i++)
            {
                layers.Add(new ThinFilmLayer(nH, lambda / (4 * nH)));
                layers.Add(new ThinFilmLayer(nL, lambda / (4 * nL)));
            }
            var r = ThinFilm.Compute(n0, layers, ns, lambda, 0);
            double y = Math.Pow(nH / nL, 2 * pairs) * ns;
            double expected = Math.Pow((n0 - y) / (n0 + y), 2);
            Assert.Equal(expected, r.S.Reflectance, 13);
            Assert.Equal(expected, r.P.Reflectance, 13);
            Assert.Equal(1 - expected, r.S.Transmittance, 13);
        }
    }

    /// <summary>R + T + A = 1 for every polarization, angle and stack, lossless or absorbing,
    /// including total internal reflection and an opaque layer; A = 0 when nothing absorbs.
    /// A is computed from its own expression, not as 1 − R − T.</summary>
    [Fact]
    public void EnergyBalances_InEveryCase()
    {
        var lossless = new[] { new ThinFilmLayer(2.1, 0.07), new ThinFilmLayer(1.46, 0.11), new ThinFilmLayer(2.1, 0.05) };
        var absorbing = new[] { new ThinFilmLayer(new Complex(2.2, 0.05), 0.08), new ThinFilmLayer(1.46, 0.12), new ThinFilmLayer(new Complex(0.2, 3.0), 0.015) };
        var opaque = new[] { new ThinFilmLayer(new Complex(0.7, 7.0), 50.0) };
        var cases = new (double N0, ThinFilmLayer[] Layers, Complex Ns, bool Lossless)[]
        {
            (1.0, lossless, 1.52, true),
            (1.52, lossless, 1.0, true),             // into air from glass: TIR beyond 41°
            (1.0, absorbing, 1.52, false),
            (1.5, absorbing, new Complex(3.9, 0.02), false),
            (1.0, opaque, 1.52, false),
            (1.0, None, new Complex(0.7, 7.0), false),
        };
        foreach (var (n0, layers, ns, isLossless) in cases)
            for (double angle = 0; angle < 90; angle += 7.5)
            {
                var r = ThinFilm.Compute(n0, layers, ns, 0.55, angle * Deg);
                foreach (var a in new[] { r.S, r.P })
                {
                    Assert.Equal(1, a.Reflectance + a.Transmittance + a.Absorptance, 12);
                    Assert.InRange(a.Reflectance, 0, 1 + 1e-12);
                    Assert.InRange(a.Transmittance, 0, 1 + 1e-12);
                    if (isLossless) Assert.Equal(0, a.Absorptance, 12);
                    else Assert.True(a.Absorptance > -1e-12);
                }
            }
    }

    [Fact]
    public void TotalInternalReflection_ReflectsEverything()
    {
        var r = ThinFilm.Compute(1.52, None, 1.0, 0.55, 60 * Deg);
        Assert.Equal(1, r.S.Reflectance, 14);
        Assert.Equal(1, r.P.Reflectance, 14);
        Assert.Equal(0, r.S.Transmittance, 14);
    }

    /// <summary>A 50 µm layer of index 0.7 + 7i has Im δ near 4000, where cos δ overflows a double.
    /// The scaled matrices keep it finite: no light passes, and the reflectance is that of the
    /// bare metal.</summary>
    [Fact]
    public void AnOpaqueLayer_StaysFinite_AndActsAsTheBulkMaterial()
    {
        var metal = new Complex(0.7, 7.0);
        var thick = ThinFilm.Compute(1, new[] { new ThinFilmLayer(metal, 50.0) }, 1.52, 0.55, 30 * Deg);
        var bulk = ThinFilm.Compute(1, None, metal, 0.55, 30 * Deg);
        Assert.Equal(0, thick.S.Transmittance);
        Assert.Equal(bulk.S.Reflectance, thick.S.Reflectance, 14);
        Assert.Equal(bulk.P.Reflectance, thick.P.Reflectance, 14);
        Assert.True(Complex.Abs(thick.P.R - bulk.P.R) < 1e-14);
    }

    /// <summary>
    /// Cross-check against tmm (S. Byrnes, MIT licence), version 0.2.0 (Changes.txt, 2024-11-26),
    /// tmm_core.coh_tmm, run 2026-11-14 with numpy 2.5.3 by this script (python -I, the tmm folder
    /// plans/sources/optics/tmm passed on sys.path):
    /// <code>
    /// cases = {"A": ([1.0, 2.2+0.05j, 1.46, 0.2+3.0j, 1.52], [inf, 80.0, 120.0, 15.0, inf], 600.0, 35.0),
    ///          "B": ([1.5, 2.2+0.05j, 1.46, 0.2+3.0j, 3.9+0.02j], [inf, 80.0, 120.0, 15.0, inf], 550.0, 20.0)}
    /// for each case and pol in "sp": coh_tmm(pol, n, d, deg*pi/180, lam) → r, t, R, T, power_entering
    /// </code>
    /// (thicknesses and wavelength in nm). tmm's amplitudes use the same convention as this
    /// program's (docs/Optics-Conventions.md §12), so r and t compare directly; its absorbed
    /// fraction is power_entering − T.
    /// </summary>
    [Theory]
    [InlineData("A", 's', -0.6547440193746038, -0.36708411920669765, -0.4178059550798214, 0.15769307503343888, 0.5634404814805686, 0.34269739677632954, 0.43655951851943137)]
    [InlineData("A", 'p', 0.4329018099461374, 0.43344891802840724, -0.533457341234853, 0.08775850097583054, 0.37528194159463857, 0.5022491617764743, 0.6247180584053612)]
    [InlineData("B", 's', -0.2918685074182186, -0.6359239686478667, -0.3668658536374278, -0.11992120020679285, 0.48958651952339166, 0.40860225873797895, 0.5104134804766083)]
    [InlineData("B", 'p', 0.1597742093402194, 0.6162977497218021, -0.38438419504763516, -0.1666263998594381, 0.40535071428244923, 0.4814078646092551, 0.5946492857175507)]
    public void AbsorbingMultilayer_AtObliqueIncidence_MatchesTmm(string caseName, char pol,
        double rRe, double rIm, double tRe, double tIm, double reflectance, double transmittance, double entering)
    {
        var layers = new[]
        {
            new ThinFilmLayer(new Complex(2.2, 0.05), 80), new ThinFilmLayer(1.46, 120), new ThinFilmLayer(new Complex(0.2, 3.0), 15)
        };
        var result = caseName == "A"
            ? ThinFilm.Compute(1.0, layers, 1.52, 600, 35 * Deg)
            : ThinFilm.Compute(1.5, layers, new Complex(3.9, 0.02), 550, 20 * Deg);
        var a = pol == 's' ? result.S : result.P;
        Assert.True(Complex.Abs(a.R - new Complex(rRe, rIm)) < 1e-13, $"r = {a.R}");
        Assert.True(Complex.Abs(a.T - new Complex(tRe, tIm)) < 1e-13, $"t = {a.T}");
        Assert.Equal(reflectance, a.Reflectance, 13);
        Assert.Equal(transmittance, a.Transmittance, 13);
        Assert.Equal(entering - transmittance, a.Absorptance, 13);
    }

    [Fact]
    public void BadInputs_AreRefusedByName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThinFilm.Compute(1, None, 1.5, 0.55, 90 * Deg));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThinFilm.Compute(1, None, new Complex(1.5, -0.1), 0.55, 0));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ThinFilm.Compute(1, new[] { new ThinFilmLayer(1.4, -1) }, 1.5, 0.55, 0));
        Assert.Contains("Layer 1", ex.Message);
    }
}
