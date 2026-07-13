using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F2b step 1: the interior-interface source (the covered patch — dielectric ABOVE
/// the metal). <see cref="TransmissionLineGreens.EvaluateInterior"/> places the HED source
/// at an arbitrary interface m and reads both potential kernels out at that same plane.
/// It is gated three independent ways: (a) an INDEPENDENT interior-source boundary-value
/// solve in the global-exponential basis (none of the production reduced-referencing algebra
/// — validates the SOLVE); (b) the air-cover collapse — a source at interface m with all-air
/// layers above must reduce EXACTLY to the trusted top-source path for the sub-slab beneath m
/// (validates the read-out where ε is continuous across the metal); and (c) m = n−1 must
/// reproduce the top-source <see cref="TransmissionLineGreens.Evaluate"/> path.
///
/// Scope note: these gates cover the case where ε is CONTINUOUS across the metal plane
/// (patch buried in a homogeneous slab, or air cover) — where the x-directed HED launches
/// no co-located ã_z contrast source, so ∂_z ã_z is single-valued at z_m. The
/// genuinely-different-εr co-located interface (a superstrate εr₂ ≠ substrate εr₁ AT the
/// current sheet) is a distinct read-out subtlety, named as the remaining follow-up.
/// </summary>
public class InteriorSourceKernelTests
{
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * 299_792_458.0 * 299_792_458.0);
    private static double K0 => 2 * Math.PI * 10e9 / 299_792_458.0;

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var s = Complex.Sqrt(kSq - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    // ---- The independent reference: an N-layer spectral BVP with the HED source at an
    // arbitrary interior interface m, solved as ONE coupled dense system in the global
    // e^{±j k_z z} basis, and read out (A_x + ∂_z ã_z) at z_m from the region just above.
    // TE (A_x) and TM (ã_z) solved together; the source jump sits at interface m, the top
    // interface is plain radiation continuity. Valid at moderate k_ρ / thin stacks (the
    // growing exponential is bounded there) — algebraically unlike the production path.
    private static (Complex GA, Complex KPhi) SolveInteriorBvp(
        LayeredStackup stackup, double k0, Complex kRho, int m)
    {
        int n = stackup.Layers.Count;
        double k0Sq = k0 * k0;
        var kz = new Complex[n];
        var eps = new Complex[n];
        var h = stackup.InterfaceHeights();      // top of each layer; h[n-1] = d
        double d = h[n - 1];
        for (int i = 0; i < n; i++)
        {
            eps[i] = stackup.Layers[i].ComplexPermittivity;
            kz[i] = Kz(eps[i] * k0Sq, kRho);
        }
        var j = Complex.ImaginaryOne;
        var kz0 = Kz(k0Sq, kRho);

        int size = 4 * n + 2;
        int cIdx = 2 * n, sIdx = 4 * n + 1;
        int pBase = 2 * n + 1;
        var mat = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        Complex Ep(int i, double z) => Complex.Exp(-j * kz[i] * z);
        Complex Em(int i, double z) => Complex.Exp(j * kz[i] * z);
        int row = 0;

        // ===== TE: A_x =====
        mat[row, 0] = Ep(0, 0); mat[row, 1] = Em(0, 0); row++;   // ground A_x(0)=0
        for (int i = 0; i < n - 1; i++)
        {
            double z = h[i];
            int a = 2 * i, b = 2 * (i + 1);
            // value continuity (always)
            mat[row, a] = Ep(i, z); mat[row, a + 1] = Em(i, z);
            mat[row, b] = -Ep(i + 1, z); mat[row, b + 1] = -Em(i + 1, z); row++;
            // derivative: jump −2µ0 at the source interface, continuity elsewhere
            mat[row, a] = -j * kz[i] * Ep(i, z); mat[row, a + 1] = j * kz[i] * Em(i, z);
            mat[row, b] = j * kz[i + 1] * Ep(i + 1, z); mat[row, b + 1] = -j * kz[i + 1] * Em(i + 1, z);
            // This row is written deriv_below − deriv_above (layer-i columns first); the HED
            // jump deriv_above − deriv_below = −2µ0 therefore lands as +2µ0 here. (The top row
            // below orders region-0 "above" first, so it keeps −2µ0.)
            if (i == m) rhs[row] = 2 * Mu0;
            row++;
        }
        // Top value: A_x(d) = C.
        mat[row, 2 * (n - 1)] = Ep(n - 1, d); mat[row, 2 * (n - 1) + 1] = Em(n - 1, d);
        mat[row, cIdx] = -1; row++;
        // Top jump: −jk_z0 C + jk_z,n-1 a Ep − jk_z,n-1 b Em = (−2µ0 if source is the top, else 0).
        mat[row, cIdx] = -j * kz0;
        mat[row, 2 * (n - 1)] = j * kz[n - 1] * Ep(n - 1, d);
        mat[row, 2 * (n - 1) + 1] = -j * kz[n - 1] * Em(n - 1, d);
        rhs[row] = m == n - 1 ? -2 * Mu0 : Complex.Zero; row++;

        // ===== TM: ã_z (−jk_x factored out); UNCHANGED from the top-source oracle: ã_z is
        // launched only by the ε-contrasts × A_x, never by the HED directly. =====
        mat[row, pBase] = -j * kz[0] * Ep(0, 0); mat[row, pBase + 1] = j * kz[0] * Em(0, 0); row++; // open ground
        for (int i = 0; i < n - 1; i++)
        {
            double z = h[i];
            int p = pBase + 2 * i, q = pBase + 2 * (i + 1);
            int a = 2 * i;
            mat[row, p] = Ep(i, z); mat[row, p + 1] = Em(i, z);
            mat[row, q] = -Ep(i + 1, z); mat[row, q + 1] = -Em(i + 1, z); row++;
            Complex above = 1 / eps[i + 1], below = 1 / eps[i];
            mat[row, q] = above * (-j * kz[i + 1]) * Ep(i + 1, z);
            mat[row, q + 1] = above * (j * kz[i + 1]) * Em(i + 1, z);
            mat[row, p] = -below * (-j * kz[i]) * Ep(i, z);
            mat[row, p + 1] = -below * (j * kz[i]) * Em(i, z);
            Complex src = below - above;
            mat[row, a] += -src * Ep(i, z); mat[row, a + 1] += -src * Em(i, z); row++;
        }
        mat[row, pBase + 2 * (n - 1)] = Ep(n - 1, d); mat[row, pBase + 2 * (n - 1) + 1] = Em(n - 1, d);
        mat[row, sIdx] = -1; row++;
        Complex epsTop = eps[n - 1], invTop = 1 / epsTop;
        mat[row, sIdx] = -j * kz0;
        mat[row, pBase + 2 * (n - 1)] = -invTop * (-j * kz[n - 1]) * Ep(n - 1, d);
        mat[row, pBase + 2 * (n - 1) + 1] = -invTop * (j * kz[n - 1]) * Em(n - 1, d);
        mat[row, cIdx] += -(invTop - 1); row++;

        var sol = ComplexLu.Factor(mat).Solve(rhs);

        // Read out at z_m from ABOVE: A_x(z_m) and ∂_z ã_z(z_m^+).
        double zm = h[m];
        Complex ax = sol[2 * m] * Ep(m, zm) + sol[2 * m + 1] * Em(m, zm);
        Complex dAz;
        Complex epsAbove;
        if (m == n - 1)
        {
            dAz = -j * kz0 * sol[sIdx];
            epsAbove = Complex.One;
        }
        else
        {
            int p = pBase + 2 * (m + 1);
            dAz = sol[p] * (-j * kz[m + 1]) * Ep(m + 1, zm)
                + sol[p + 1] * (j * kz[m + 1]) * Em(m + 1, zm);
            epsAbove = eps[m + 1];
        }
        // Φ = −∇·A/(jωµ0ε0·εr_local); divide by the permittivity of the read-out region.
        return (ax, (ax + dAz) / (Mu0 * Eps0 * epsAbove));
    }

    // A genuinely layered covered stack: FR4 substrate / patch buried / FR4 cover / air.
    // The source interface m separates substrate from cover; ε is continuous across it.
    private static LayeredStackup CoveredHomogeneous(double epsR, double tanD, double hSub, double hCover) =>
        new(new[]
        {
            new LayeredStackup.Layer(epsR, tanD, hSub),     // substrate on the ground (m = 0)
            new LayeredStackup.Layer(epsR, tanD, hCover),   // cover above the metal
        });

    public static TheoryData<double> ModerateKRho => new() { 0.3, 0.7, 0.95, 1.2, 3.0, 8.0 };

    [Theory]
    [MemberData(nameof(ModerateKRho))]
    public void InteriorSource_MatchesIndependentBvp_CoveredHomogeneous(double kRhoOverK0)
    {
        var stackup = CoveredHomogeneous(4.4, 0.02, 0.8e-3, 0.5e-3);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var (gA, kPhi) = TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m: 0);
        var (gARef, kPhiRef) = SolveInteriorBvp(stackup, k0, kRho, m: 0);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude, $"G_A {gA} vs BVP {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude, $"K_Φ {kPhi} vs BVP {kPhiRef}");
    }

    [Theory]
    [MemberData(nameof(ModerateKRho))]
    public void InteriorSource_MatchesIndependentBvp_ThreeDistinctLayers(double kRhoOverK0)
    {
        // Distinct εr below and above the source is fine for the SOLVE gate (it does not
        // rely on ε-continuity — only the covered-patch READ-OUT interpretation does).
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(6.0, 0.001, 0.6e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),   // source interface m = 1 sits on top of this
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
        });
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var (gA, kPhi) = TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m: 1);
        var (gARef, kPhiRef) = SolveInteriorBvp(stackup, k0, kRho, m: 1);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude, $"G_A {gA} vs BVP {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude, $"K_Φ {kPhi} vs BVP {kPhiRef}");
    }

    [Theory]
    [InlineData(2.2, 0.0, 0.5)]
    [InlineData(4.4, 0.02, 1.2)]
    [InlineData(6.0, 0.0, 3.0)]
    public void AirCover_CollapsesToTopSourceSubSlab(double epsR, double tanD, double kRhoOverK0)
    {
        // A source at interface m with all-air layers above must equal the trusted top-source
        // path (Evaluate) for the sub-slab beneath m — everything above z_m is uniform air, so
        // the two boundary-value problems are identical. This validates the interior read-out
        // against the SEPARATELY-PINNED single-slab closed form.
        double hSub = 1.0e-3;
        var subSlab = new SubstrateStackup(epsR, tanD, hSub);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var (gARef, kPhiRef) = TransmissionLineGreens.Evaluate(
            LayeredStackup.FromSubstrate(subSlab), k0, kRho, kz0);

        // Two air layers of different thicknesses above the substrate; source at interface 0.
        var covered = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(epsR, tanD, hSub),
            new LayeredStackup.Layer(1, 0, 0.4e-3),
            new LayeredStackup.Layer(1, 0, 0.7e-3),
        });
        var (gA, kPhi) = TransmissionLineGreens.EvaluateInterior(covered, k0, kRho, kz0, m: 0);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude, $"air-cover G_A {gA} vs top {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude, $"air-cover K_Φ {kPhi} vs top {kPhiRef}");
    }

    // ---- Interior-source quasi-static image extraction (F2b-3) ----

    // Compare two image lists by the SPECTRAL ASYMPTOTE they subtract — Σ Coeff·e^{−jk_z0 D}/(jk_z0)
    // at several k_ρ — rather than entry-by-entry (two lists can bin depths differently under
    // different layer subdivisions / depth caps yet subtract the identical asymptote).
    private static void AssertSameAsymptote(
        IReadOnlyList<MultiLayerImages.Image> a, IReadOnlyList<MultiLayerImages.Image> b, string what)
    {
        double k0 = K0;
        // Probe at k_ρ/k0 ≥ 20: two image sets built from different subdivisions / totals bin
        // and truncate their DEEP images differently (a sub-slab's smaller depth cap drops
        // substrate round trips the covered stack keeps), but those are e^{−k_ρ D}-suppressed to
        // machine zero by k_ρ/k0 ≈ 20 — leaving the significant (shallow) images, which must
        // agree. The 1e-7 tolerance sits above the image generator's 1e-10 relative pruning
        // noise (different monomial structures cull marginally differently) and far below any
        // physics tolerance.
        foreach (double ratio in new[] { 20.0, 45.0, 90.0 })
        {
            var kRho = new Complex(ratio * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var jk = Complex.ImaginaryOne * kz0;
            Complex Sum(IReadOnlyList<MultiLayerImages.Image> img)
            {
                Complex s = 0;
                foreach (var im in img) s += im.Coeff * Complex.Exp(-Complex.ImaginaryOne * kz0 * im.Depth) / jk;
                return s;
            }
            Complex sa = Sum(a), sb = Sum(b);
            Assert.True((sa - sb).Magnitude < 1e-7 * sb.Magnitude,
                $"{what}: asymptote at kρ/k0={ratio} {sa} vs {sb}");
        }
    }

    [Fact]
    public void InteriorImages_AtTopInterface_ReproduceTopSourceImages()
    {
        // m = n−1: the interior generator (Z_up = air = 1) must equal the top-source series.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        AssertSameAsymptote(MultiLayerImages.PhiImagesInterior(stackup, stackup.Layers.Count - 1),
            MultiLayerImages.PhiImages(stackup), "K_Φ m=n-1");
        AssertSameAsymptote(MultiLayerImages.GaImagesInterior(stackup, stackup.Layers.Count - 1),
            MultiLayerImages.GaImages(stackup), "G_A m=n-1");
    }

    [Fact]
    public void InteriorImages_WithAirCover_CollapseToSubSlab()
    {
        // Source at interface 0 with all-air layers above ≡ the top-source images of the
        // sub-slab beneath the metal (everything above z_0 is uniform air).
        double hSub = 1.0e-3;
        var subSlab = LayeredStackup.FromSubstrate(new SubstrateStackup(4.4, 0.02, hSub));
        var covered = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, hSub),
            new LayeredStackup.Layer(1, 0, 0.4e-3),
            new LayeredStackup.Layer(1, 0, 0.7e-3),
        });
        AssertSameAsymptote(MultiLayerImages.PhiImagesInterior(covered, 0),
            MultiLayerImages.PhiImages(subSlab), "air-cover K_Φ");
        // G_A ground image sits at 2·z_0 = 2·hSub, exactly the sub-slab's 2·d.
        var ga = MultiLayerImages.GaImagesInterior(covered, 0).OrderBy(i => i.Depth).ToArray();
        Assert.Equal(2 * hSub, ga[1].Depth, 12);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void InteriorImages_SplitInvariance(int split)
    {
        // Splitting the substrate AND the cover into K identical sub-layers leaves the
        // depth→coefficient image list unchanged (Emit merges equal depths exactly).
        double hSub = 0.9e-3, hCov = 0.6e-3;
        var one = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, hSub),
            new LayeredStackup.Layer(3.0, 0.01, hCov),
        });
        var refImg = MultiLayerImages.PhiImagesInterior(one, 0);   // metal between the two

        var parts = new List<LayeredStackup.Layer>();
        for (int k = 0; k < split; k++) parts.Add(new LayeredStackup.Layer(4.4, 0.02, hSub / split));
        for (int k = 0; k < split; k++) parts.Add(new LayeredStackup.Layer(3.0, 0.01, hCov / split));
        var splitStack = new LayeredStackup(parts);
        var splitImg = MultiLayerImages.PhiImagesInterior(splitStack, split - 1);   // metal at the same z

        AssertSameAsymptote(splitImg, refImg, $"split-{split} K_Φ");
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(80.0)]
    public void InteriorImages_MatchTheKernelAsymptote(double kRhoOverK0)
    {
        // The image series IS the k_ρ → ∞ asymptote the Sommerfeld integrator subtracts: far
        // up the evanescent tail the full interior kernel must equal Σ images to high relative
        // accuracy (the remainder decays algebraically), and the match must IMPROVE with k_ρ.
        var stackup = CoveredHomogeneous(4.4, 0.02, 0.8e-3, 0.5e-3);
        int m = 0;
        double k0 = K0;
        var phiImg = MultiLayerImages.PhiImagesInterior(stackup, m);
        var gaImg = MultiLayerImages.GaImagesInterior(stackup, m);

        (double phiRel, double gaRel) Probe(double ratio)
        {
            var kRho = new Complex(ratio * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var (gA, kPhi) = TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m);
            Complex phiAsym = 0, gaAsym = 0;
            var jk = Complex.ImaginaryOne * kz0;
            foreach (var im in phiImg) phiAsym += im.Coeff * Complex.Exp(-Complex.ImaginaryOne * kz0 * im.Depth) / (jk * Eps0);
            foreach (var im in gaImg) gaAsym += Mu0 * im.Coeff * Complex.Exp(-Complex.ImaginaryOne * kz0 * im.Depth) / jk;
            return ((kPhi - phiAsym).Magnitude / kPhi.Magnitude, (gA - gaAsym).Magnitude / gA.Magnitude);
        }

        var (p, g) = Probe(kRhoOverK0);
        // Both kernels' images capture the k_ρ → ∞ asymptote; the residual is the O(1/k_ρ²)
        // Sommerfeld REMAINDER (the same one the integrator carries for the top source), so it
        // is small and shrinks with k_ρ — see InteriorImages_AsymptoteImprovesWithKRho.
        Assert.True(p < 0.02, $"K_Φ asymptote rel {p} at kρ/k0={kRhoOverK0}");
        Assert.True(g < 0.01, $"G_A asymptote rel {g} at kρ/k0={kRhoOverK0}");
    }

    [Fact]
    public void InteriorImages_AsymptoteImprovesWithKRho()
    {
        var stackup = CoveredHomogeneous(4.4, 0.02, 0.8e-3, 0.5e-3);
        int m = 0;
        double k0 = K0;
        var phiImg = MultiLayerImages.PhiImagesInterior(stackup, m);
        double Rel(double ratio)
        {
            var kRho = new Complex(ratio * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var (_, kPhi) = TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m);
            Complex asym = 0; var jk = Complex.ImaginaryOne * kz0;
            foreach (var im in phiImg) asym += im.Coeff * Complex.Exp(-Complex.ImaginaryOne * kz0 * im.Depth) / (jk * Eps0);
            return (kPhi - asym).Magnitude / kPhi.Magnitude;
        }
        Assert.True(Rel(80) < Rel(40), $"asymptote should improve: rel(80)={Rel(80)} rel(40)={Rel(40)}");
    }

    // ---- Interior-source kernel TABLE: images + remainder + pole residues end to end ----

    [Theory]
    [InlineData(0.4e-3)]
    [InlineData(2.0e-3)]
    [InlineData(6.0e-3)]
    public void InteriorTable_AirCover_EqualsTopSourceSubSlab_Direct(double rho)
    {
        // THE end-to-end gate: the full spatial interior kernel (images + Sommerfeld remainder
        // + interior-plane pole residues, integrated directly) with an all-air cover must equal
        // the trusted top-source table for the sub-slab beneath the metal — everything above z_m
        // is uniform air, so the physics is identical. This exercises the whole interior pipeline
        // against a separately-pinned reference in one shot.
        double hSub = 1.588e-3;
        var subStack = LayeredStackup.FromSubstrate(new SubstrateStackup(2.2, 0.001, hSub));
        var covered = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(2.2, 0.001, hSub),
            new LayeredStackup.Layer(1, 0, 0.5e-3),
            new LayeredStackup.Layer(1, 0, 0.8e-3),
        });
        var top = new MultiLayerKernelTable(subStack, 10e9, 15e-3);
        var interior = new MultiLayerKernelTable(covered, 10e9, 15e-3, sourceInterface: 0);
        var (gaRef, phiRef) = top.EvaluateKernelsDirect(rho, refinement: 3);
        var (gA, kPhi) = interior.EvaluateKernelsDirect(rho, refinement: 3);
        Assert.True((gA - gaRef).Magnitude < 1e-6 * gaRef.Magnitude, $"G_A {gA} vs sub-slab {gaRef}");
        Assert.True((kPhi - phiRef).Magnitude < 1e-6 * phiRef.Magnitude, $"K_Φ {kPhi} vs sub-slab {phiRef}");
    }

    [Fact]
    public void InteriorPoleResidues_AtTop_ReproduceTopSourceResidues()
    {
        // m = n−1 interior residues ≡ the top-source residues (same mode, same read-out plane).
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(10.2, 0.0023, 0.6e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        double k0 = K0;
        var top = SurfaceWavePoles.Find(stackup, k0).OrderBy(p => p.KRho.Real).ToArray();
        var interior = SurfaceWavePoles.Find(stackup, k0, sourceInterface: stackup.Layers.Count - 1)
            .OrderBy(p => p.KRho.Real).ToArray();
        Assert.Equal(top.Length, interior.Length);
        for (int i = 0; i < top.Length; i++)
        {
            Assert.True((interior[i].KRho - top[i].KRho).Magnitude < 1e-10 * top[i].KRho.Magnitude);
            double sPhi = Math.Max(top[i].ResiduePhi.Magnitude, 1e-30);
            Assert.True((interior[i].ResiduePhi - top[i].ResiduePhi).Magnitude < 1e-9 * sPhi,
                $"pole {i} Res_Φ {interior[i].ResiduePhi} vs {top[i].ResiduePhi}");
            double sA = Math.Max(top[i].ResidueA.Magnitude, 1e-30);
            Assert.True((interior[i].ResidueA - top[i].ResidueA).Magnitude < 1e-9 * sA,
                $"pole {i} Res_A {interior[i].ResidueA} vs {top[i].ResidueA}");
        }
    }

    [Theory]
    [InlineData(0.5e-3)]
    [InlineData(3.0e-3)]
    public void InteriorTable_CoveredHomogeneous_SelfConverges(double rho)
    {
        // A genuine covered patch (dielectric cover, ε continuous across the metal): the full
        // interior kernel must be refinement-stable — the integration is converged.
        var stackup = CoveredHomogeneous(4.4, 0.02, 0.8e-3, 0.5e-3);
        var table = new MultiLayerKernelTable(stackup, 10e9, 15e-3, sourceInterface: 0);
        var (gA1, phi1) = table.EvaluateKernelsDirect(rho, refinement: 1);
        var (gA2, phi2) = table.EvaluateKernelsDirect(rho, refinement: 2);
        Assert.True((gA2 - gA1).Magnitude < 1e-7 * gA2.Magnitude, $"G_A refine 1→2 {(gA2 - gA1).Magnitude / gA2.Magnitude:e3}");
        Assert.True((phi2 - phi1).Magnitude < 1e-7 * phi2.Magnitude, $"K_Φ refine 1→2 {(phi2 - phi1).Magnitude / phi2.Magnitude:e3}");
    }

    [Theory]
    [InlineData(0.3e-3)]
    [InlineData(2.0e-3)]
    [InlineData(9.0e-3)]
    public void InteriorTable_CoveredHomogeneous_SplineMatchesDirect(double rho)
    {
        var stackup = CoveredHomogeneous(4.4, 0.02, 0.8e-3, 0.5e-3);
        var table = new MultiLayerKernelTable(stackup, 10e9, 15e-3, sourceInterface: 0);
        var (gaSpline, phiSpline) = table.EvaluateKernels(rho);
        var (gaDirect, phiDirect) = table.EvaluateKernelsDirect(rho, refinement: 3);
        Assert.True((gaSpline - gaDirect).Magnitude < 1e-5 * gaDirect.Magnitude, $"G_A spline {gaSpline} vs direct {gaDirect}");
        Assert.True((phiSpline - phiDirect).Magnitude < 1e-5 * phiDirect.Magnitude, $"K_Φ spline {phiSpline} vs direct {phiDirect}");
    }

    [Theory]
    [MemberData(nameof(ModerateKRho))]
    public void InteriorSourceAtTop_ReproducesTopSourcePath(double kRhoOverK0)
    {
        // m = n−1 (source at the top plane) must be the top-source Evaluate, bit-close.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var top = TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0);
        var interior = TransmissionLineGreens.EvaluateInterior(stackup, k0, kRho, kz0, m: 1);
        Assert.True((interior.GA - top.GA).Magnitude < 1e-12 * top.GA.Magnitude,
            $"m=n-1 G_A {interior.GA} vs top {top.GA}");
        Assert.True((interior.KPhi - top.KPhi).Magnitude < 1e-12 * top.KPhi.Magnitude,
            $"m=n-1 K_Φ {interior.KPhi} vs top {top.KPhi}");
    }
}
