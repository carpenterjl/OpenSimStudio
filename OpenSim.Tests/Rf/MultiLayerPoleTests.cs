using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F pole gates: the multi-layer TLGF pole finder (locations from the TE/TM
/// dispersion, residues from the analytic null-vector matrix method) must reproduce the
/// pinned single-slab finder at N = 1, satisfy the split-slab invariance, and — the
/// independent check — match a Richardson limit (k_ρ − k_p)·K̃_Φ of the general kernel.
/// </summary>
public class MultiLayerPoleTests
{
    private static double K0 => 2 * Math.PI * 10e9 / 299_792_458.0;

    [Fact]
    public void SingleSlab_MatchesThePinnedFinder_LocationAndResidues()
    {
        var substrate = new SubstrateStackup(2.2, 0, 1.588e-3);
        double k0 = K0;
        var reference = SurfaceWavePoles.Find(substrate, k0);
        var general = SurfaceWavePoles.Find(LayeredStackup.FromSubstrate(substrate), k0);

        Assert.Equal(reference.Count, general.Count);
        for (int i = 0; i < reference.Count; i++)
        {
            var r = reference[i];
            var g = general.Single(p => p.IsTm == r.IsTm
                && Math.Abs(p.KRho.Real - r.KRho.Real) < 1e-6 * k0);
            Assert.True((g.KRho - r.KRho).Magnitude < 1e-9 * k0,
                $"k_p {g.KRho} vs pinned {r.KRho}");
            Assert.True((g.ResiduePhi - r.ResiduePhi).Magnitude < 1e-7 * r.ResiduePhi.Magnitude,
                $"Res_Φ {g.ResiduePhi} vs pinned {r.ResiduePhi}");
            if (r.ResidueA.Magnitude > 0)
                Assert.True((g.ResidueA - r.ResidueA).Magnitude < 1e-7 * r.ResidueA.Magnitude,
                    $"Res_A {g.ResidueA} vs pinned {r.ResidueA}");
        }
    }

    [Fact]
    public void LossySlab_MatchesThePinnedFinder()
    {
        var substrate = new SubstrateStackup(6.0, 0.02, 1.0e-3);
        double k0 = K0;
        var reference = SurfaceWavePoles.Find(substrate, k0);
        var general = SurfaceWavePoles.Find(LayeredStackup.FromSubstrate(substrate), k0);
        Assert.Equal(reference.Count, general.Count);
        foreach (var r in reference)
        {
            var g = general.Single(p => p.IsTm == r.IsTm
                && Math.Abs(p.KRho.Real - r.KRho.Real) < 1e-4 * k0);
            Assert.True((g.KRho - r.KRho).Magnitude < 1e-8 * k0, $"lossy k_p {g.KRho} vs {r.KRho}");
            Assert.True((g.ResiduePhi - r.ResiduePhi).Magnitude < 1e-6 * r.ResiduePhi.Magnitude,
                $"lossy Res_Φ {g.ResiduePhi} vs {r.ResiduePhi}");
        }
    }

    [Fact]
    public void Residues_MatchARichardsonLimitOfTheGeneralKernel()
    {
        // A genuinely two-layer stack so the residue is not the single-slab closed form.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(10.2, 0, 0.635e-3),   // high-εr base carries the TM0
            new LayeredStackup.Layer(2.2, 0, 0.5e-3),
        });
        double k0 = K0;
        var poles = SurfaceWavePoles.Find(stackup, k0);
        Assert.NotEmpty(poles);
        foreach (var pole in poles)
        {
            double kp = pole.KRho.Real;
            // Res_Φ = lim (k_ρ − k_p) K̃_Φ. r(x) = x·K̃_Φ(k_p+x) = Res + c₁x + c₂x² + …; a
            // two-stage Romberg (kill O(x), then O(x²)) reaches the residue independently.
            Complex R(double step) =>
                step * TransmissionLineGreens.Evaluate(stackup, k0, new Complex(kp + step, 0)).KPhi;
            double h = 2e-3 * k0;
            Complex R1(double x) => 2 * R(x) - R(2 * x);              // removes O(x)
            Complex R2(double x) => (4 * R1(x / 2) - R1(x)) / 3;      // removes O(x²)
            Complex romberg = (8 * R2(h / 2) - R2(h)) / 7;           // removes O(x³)
            Assert.True((pole.ResiduePhi - romberg).Magnitude < 1e-6 * romberg.Magnitude,
                $"Res_Φ {pole.ResiduePhi} vs Richardson {romberg} (pole IsTm={pole.IsTm})");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void SplitSlab_PolesAndResiduesAreInvariant(int parts)
    {
        var substrate = new SubstrateStackup(6.0, 0, 1.2e-3);
        double k0 = K0;
        var one = SurfaceWavePoles.Find(LayeredStackup.FromSubstrate(substrate), k0);
        var split = SurfaceWavePoles.Find(new LayeredStackup(
            Enumerable.Range(0, parts)
                .Select(_ => new LayeredStackup.Layer(6.0, 0, substrate.ThicknessMeters / parts))
                .ToArray()), k0);

        Assert.Equal(one.Count, split.Count);
        foreach (var a in one)
        {
            var b = split.Single(p => p.IsTm == a.IsTm && Math.Abs(p.KRho.Real - a.KRho.Real) < 1e-6 * k0);
            Assert.True((b.KRho - a.KRho).Magnitude < 1e-9 * k0, $"split k_p {b.KRho} vs {a.KRho}");
            Assert.True((b.ResiduePhi - a.ResiduePhi).Magnitude < 1e-8 * a.ResiduePhi.Magnitude,
                $"split Res_Φ {b.ResiduePhi} vs {a.ResiduePhi}");
        }
    }

    [Fact]
    public void ThickHighEpsStack_SupportsMultipleModes()
    {
        // Electrically thick, high-εr ⇒ several surface-wave branches (TM0, TE1, TM1, …).
        var stackup = new LayeredStackup(new[] { new LayeredStackup.Layer(10.2, 0, 6.0e-3) });
        double k0 = K0;
        var poles = SurfaceWavePoles.Find(stackup, k0);
        Assert.True(poles.Count >= 3, $"expected multiple modes, got {poles.Count}");
        // Every pole sits in the bound-mode window and on/under the real axis.
        foreach (var p in poles)
        {
            Assert.InRange(p.KRho.Real / k0, 1.0, Math.Sqrt(10.2));
            Assert.True(p.KRho.Imaginary <= 1e-12 * k0);
        }
    }
}
