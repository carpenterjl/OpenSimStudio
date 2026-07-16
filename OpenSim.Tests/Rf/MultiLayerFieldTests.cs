using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage S9b gates: the multi-layer / covered per-observation-height FIELD kernels
/// (<see cref="TransmissionLineGreens.EvaluateField"/> and the per-z pole residues) and
/// the near-field maps they drive. The engine is pinned three ways: (a) at N = 1, top
/// source, the per-z kernels reproduce the single-slab <see cref="SpectralProfiles"/> to
/// 1e-12 (the identity gate); (b) genuine multi-layer and covered (interior-source) per-z
/// kernels match an INDEPENDENT global-basis boundary-value solve; (c) at the map level the
/// εr = 1 layered E and H reproduce the free-space + PEC-image assemblies (the house
/// identity), and ∇·H = 0.
/// </summary>
public class MultiLayerFieldTests
{
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * 299_792_458.0 * 299_792_458.0);
    private const double BalanisF = 10e9;
    private static double K0 => 2 * Math.PI * BalanisF / 299_792_458.0;

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var s = Complex.Sqrt(kSq - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    private static void AssertRel(Complex expected, Complex actual, double tol, string what)
    {
        double scale = Math.Max(expected.Magnitude, actual.Magnitude);
        if (scale == 0) { Assert.True(actual.Magnitude < tol, $"{what}: expected 0, got {actual}"); return; }
        double rel = (expected - actual).Magnitude / scale;
        Assert.True(rel <= tol, $"{what}: expected {expected}, got {actual} (rel {rel:e2})");
    }

    /// <summary>The INDEPENDENT per-z oracle: an N-layer spectral BVP for a horizontal source
    /// at interface <paramref name="m"/>, solved as ONE coupled dense system in the global
    /// e^{±jk_z z} basis (no reduced-referencing algebra — algebraically unlike the production
    /// path), then the six field kernels evaluated from the global-basis amplitudes at z. The
    /// A_x source jump lives at interface m (top plane when m = n−1); the A_z line is launched
    /// by the ε-contrasts at every interface as usual. Valid at moderate k_ρ / thin stacks
    /// (the growing global exponential is bounded there).</summary>
    private static (Complex GA, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        OracleField(LayeredStackup stackup, double k0, Complex kRho, int m, double z)
    {
        int n = stackup.Layers.Count;
        double k0Sq = k0 * k0;
        var kz = new Complex[n];
        var eps = new Complex[n];
        var h = stackup.InterfaceHeights();
        double d = h[n - 1];
        for (int i = 0; i < n; i++)
        {
            eps[i] = stackup.Layers[i].ComplexPermittivity;
            kz[i] = Kz(eps[i] * k0Sq, kRho);
        }
        var j = Complex.ImaginaryOne;
        var kz0 = Kz(k0Sq, kRho);
        int size = 4 * n + 2, cIdx = 2 * n, sIdx = 4 * n + 1, pBase = 2 * n + 1;
        var mat = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        Complex Ep(int i, double zz) => Complex.Exp(-j * kz[i] * zz);
        Complex Em(int i, double zz) => Complex.Exp(j * kz[i] * zz);
        int row = 0;

        // ===== TE: A_x. Ground A_x(0) = 0. =====
        mat[row, 0] = Ep(0, 0); mat[row, 1] = Em(0, 0); row++;
        for (int i = 0; i < n - 1; i++)
        {
            double zi = h[i];
            int a = 2 * i, b = 2 * (i + 1);
            mat[row, a] = Ep(i, zi); mat[row, a + 1] = Em(i, zi);
            mat[row, b] = -Ep(i + 1, zi); mat[row, b + 1] = -Em(i + 1, zi); row++;
            // deriv_i − deriv_{i+1}: continuity (0), or the source jump (+2µ0) at interface m.
            mat[row, a] = -j * kz[i] * Ep(i, zi); mat[row, a + 1] = j * kz[i] * Em(i, zi);
            mat[row, b] = j * kz[i + 1] * Ep(i + 1, zi); mat[row, b + 1] = -j * kz[i + 1] * Em(i + 1, zi);
            if (i == m) rhs[row] = 2 * Mu0;
            row++;
        }
        mat[row, 2 * (n - 1)] = Ep(n - 1, d); mat[row, 2 * (n - 1) + 1] = Em(n - 1, d);
        mat[row, cIdx] = -1; row++;
        // Top jump: deriv_above − deriv_below = −2µ0 only when the source IS the top plane.
        mat[row, cIdx] = -j * kz0;
        mat[row, 2 * (n - 1)] = j * kz[n - 1] * Ep(n - 1, d);
        mat[row, 2 * (n - 1) + 1] = -j * kz[n - 1] * Em(n - 1, d);
        if (m == n - 1) rhs[row] = -2 * Mu0;
        row++;

        // ===== TM: A_z (ã_z). Ground ∂_z A_z(0) = 0. =====
        mat[row, pBase] = -j * kz[0] * Ep(0, 0); mat[row, pBase + 1] = j * kz[0] * Em(0, 0); row++;
        for (int i = 0; i < n - 1; i++)
        {
            double zi = h[i];
            int p = pBase + 2 * i, q = pBase + 2 * (i + 1), a = 2 * i;
            mat[row, p] = Ep(i, zi); mat[row, p + 1] = Em(i, zi);
            mat[row, q] = -Ep(i + 1, zi); mat[row, q + 1] = -Em(i + 1, zi); row++;
            Complex above = 1 / eps[i + 1], below = 1 / eps[i];
            mat[row, q] = above * (-j * kz[i + 1]) * Ep(i + 1, zi);
            mat[row, q + 1] = above * (j * kz[i + 1]) * Em(i + 1, zi);
            mat[row, p] = -below * (-j * kz[i]) * Ep(i, zi);
            mat[row, p + 1] = -below * (j * kz[i]) * Em(i, zi);
            Complex src = below - above;
            mat[row, a] += -src * Ep(i, zi); mat[row, a + 1] += -src * Em(i, zi); row++;
        }
        mat[row, pBase + 2 * (n - 1)] = Ep(n - 1, d); mat[row, pBase + 2 * (n - 1) + 1] = Em(n - 1, d);
        mat[row, sIdx] = -1; row++;
        Complex epsTop = eps[n - 1], invTop = 1 / epsTop;
        mat[row, sIdx] = -j * kz0;
        mat[row, pBase + 2 * (n - 1)] = -invTop * (-j * kz[n - 1]) * Ep(n - 1, d);
        mat[row, pBase + 2 * (n - 1) + 1] = -invTop * (j * kz[n - 1]) * Em(n - 1, d);
        mat[row, cIdx] += -(invTop - 1); row++;

        var sol = ComplexLu.Factor(mat).Solve(rhs);

        // Profile the global-basis amplitudes at z.
        Complex ax, dzAx, az, dzAz, epsAt, kzAt;
        if (z >= d)
        {
            var e0 = Complex.Exp(-j * kz0 * (z - d));
            ax = sol[cIdx] * e0; dzAx = -j * kz0 * ax;
            az = sol[sIdx] * e0; dzAz = -j * kz0 * az;
            epsAt = Complex.One; kzAt = kz0;
        }
        else
        {
            int i = 0;
            while (i < n - 1 && z > h[i]) i++;
            int a = 2 * i, p = pBase + 2 * i;
            ax = sol[a] * Ep(i, z) + sol[a + 1] * Em(i, z);
            dzAx = -j * kz[i] * sol[a] * Ep(i, z) + j * kz[i] * sol[a + 1] * Em(i, z);
            az = sol[p] * Ep(i, z) + sol[p + 1] * Em(i, z);
            dzAz = -j * kz[i] * sol[p] * Ep(i, z) + j * kz[i] * sol[p + 1] * Em(i, z);
            epsAt = eps[i]; kzAt = kz[i];
        }
        Complex norm = Mu0 * Eps0 * epsAt;
        var phi = (ax + dzAz) / norm;
        var dzPhi = (dzAx - kzAt * kzAt * az) / norm;
        return (ax, az, phi, dzPhi, dzAx, dzAz);
    }

    public static IEnumerable<object[]> MultiLayerSamples()
    {
        double k0 = K0;
        foreach (double kRhoOverK0 in new[] { 0.3, 0.95, 1.2, 3.0, 8.0 })
            foreach (double zOverD in new[] { 0.1, 0.4, 0.7, 0.95, 1.0, 1.4, 2.0 })
                yield return new object[] { kRhoOverK0, zOverD };
    }

    [Theory]
    [MemberData(nameof(MultiLayerSamples))]
    public void PerZFieldKernels_MatchIndependentMultiLayerBvp(double kRhoOverK0, double zOverD)
    {
        // FR4 / adhesive / Rogers, all distinct — a genuine multi-gap stack, top source.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        double k0 = K0, d = stackup.TotalThicknessMeters;
        var kr = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kr);
        int m = stackup.Layers.Count - 1;
        double z = zOverD * d;
        var got = TransmissionLineGreens.EvaluateField(stackup, k0, kr, kz0, m, z);
        var want = OracleField(stackup, k0, kr, m, z);
        AssertRel(want.GA, got.GA, 1e-10, $"G̃_A z/d={zOverD}");
        AssertRel(want.W, got.W, 1e-10, $"W̃ z/d={zOverD}");
        AssertRel(want.Phi, got.Phi, 1e-10, $"K̃_Φ z/d={zOverD}");
        AssertRel(want.DzPhi, got.DzPhi, 1e-10, $"∂zK̃_Φ z/d={zOverD}");
        AssertRel(want.DzA, got.DzA, 1e-10, $"∂zG̃_A z/d={zOverD}");
        AssertRel(want.DzW, got.DzW, 1e-10, $"∂zW̃ z/d={zOverD}");
    }

    [Theory]
    [MemberData(nameof(MultiLayerSamples))]
    public void PerZFieldKernels_CoveredPatch_MatchIndependentBvp(double kRhoOverK0, double zOverD)
    {
        // A covered patch: εr = 2.2 substrate + same-εr cover, source buried at interface 0.
        var stackup = LayeredStackup.CoveredPatch(2.2, 0.0, 0.8e-3, 0.6e-3);
        double k0 = K0, d = stackup.TotalThicknessMeters;
        double hSrc = stackup.InterfaceHeights()[0];
        var kr = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kr);
        int m = LayeredStackup.CoveredPatchMetalInterface;
        // Maps live at or above the buried metal (in the cover / above).
        double z = hSrc + zOverD * (d - hSrc);
        var got = TransmissionLineGreens.EvaluateField(stackup, k0, kr, kz0, m, z);
        var want = OracleField(stackup, k0, kr, m, z);
        AssertRel(want.GA, got.GA, 1e-10, $"G̃_A z/d={zOverD}");
        AssertRel(want.W, got.W, 1e-10, $"W̃ z/d={zOverD}");
        AssertRel(want.Phi, got.Phi, 1e-10, $"K̃_Φ z/d={zOverD}");
        AssertRel(want.DzPhi, got.DzPhi, 1e-10, $"∂zK̃_Φ z/d={zOverD}");
        AssertRel(want.DzA, got.DzA, 1e-10, $"∂zG̃_A z/d={zOverD}");
        AssertRel(want.DzW, got.DzW, 1e-10, $"∂zW̃ z/d={zOverD}");
    }

    // ---- Map-level gates (the full LayeredFieldEvaluator pipeline) ----

    [Fact]
    public void MultiLayerMap_AtNEqualsOne_MatchesTheSingleSlabEvaluator()
    {
        // The headline map gate: on ONE solved patch, the multi-layer field evaluator
        // (FromSubstrate stackup + MultiLayerKernelTable) must reproduce the already-gated
        // single-slab evaluator's E AND H at every probe point above the metal. The FieldAt
        // assembly is shared, so this isolates the multi-layer kernel-table path; both
        // reconstruct the same total spectral kernel (only the image split / spline differ),
        // so they agree to spline grade. Transitively this pins εr = 1 ≡ free-space too.
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        var stackup = LayeredStackup.FromSubstrate(substrate);
        double f = BalanisF, d = substrate.ThicknessMeters;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: d, portFraction: 0);
        var table = new LayeredKernelTable(substrate, f, 0.03);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        var mlTable = new MultiLayerKernelTable(stackup, f, 0.03);

        var points = new List<Vector3D>();
        foreach (double zf in new[] { 1.2, 1.8, 3.0 })
            foreach (double xf in new[] { 0.0, 0.3, 0.7 })
                points.Add(new Vector3D(xf * 1.186e-2, 0.2e-2, zf * d));

        var single = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, points);
        var multi = LayeredFieldEvaluator.Evaluate(grid.Structure!, mlTable, solution, points);
        for (int i = 0; i < points.Count; i++)
        {
            double relE = Math.Abs(single.Magnitude[i] - multi.Magnitude[i])
                          / Math.Max(single.Magnitude[i], 1e-30);
            Assert.True(relE <= 2e-2, $"|E| at {points[i]}: single {single.Magnitude[i]:e3} " +
                $"vs multi {multi.Magnitude[i]:e3} (rel {relE:e2})");
            double relH = Math.Abs(single.HMagnitude![i] - multi.HMagnitude![i])
                          / Math.Max(single.HMagnitude[i], 1e-30);
            Assert.True(relH <= 2e-2, $"|H| at {points[i]}: single {single.HMagnitude[i]:e3} " +
                $"vs multi {multi.HMagnitude[i]:e3} (rel {relH:e2})");
        }
    }

    [Fact]
    public void CoveredPatchMap_IsDivergenceFree()
    {
        // ∇·B = 0 on a genuine covered patch (2-layer, εr = 2.2 buried source): the assembled
        // H (including the A_z/W̃ leg) must be a real curl. A wrong sign or missing leg breaks
        // ∇·H at O(|H|/δ); a correct assembly leaves the map-grade quadrature floor.
        var stackup = LayeredStackup.CoveredPatch(2.2, 0.0, 1.0e-3, 0.6e-3);
        double f = BalanisF;
        double hMetal = stackup.InterfaceHeights()[LayeredStackup.CoveredPatchMetalInterface];
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: hMetal, portFraction: 0);
        var table = new MultiLayerKernelTable(stackup, f, 0.03,
            sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);

        double delta = 0.15e-3;
        var c = new Vector3D(0.35 * 1.186e-2, 0.15e-2, hMetal + 0.4e-3);   // inside the cover
        Complex Comp(Vector3D p, int axis)
        {
            var h = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, new[] { p }).H![0];
            return axis == 0 ? h.X : axis == 1 ? h.Y : h.Z;
        }
        var dx = new Vector3D(delta, 0, 0);
        var dy = new Vector3D(0, delta, 0);
        var dz = new Vector3D(0, 0, delta);
        Complex divH = (Comp(c + dx, 0) - Comp(c - dx, 0)
                      + Comp(c + dy, 1) - Comp(c - dy, 1)
                      + Comp(c + dz, 2) - Comp(c - dz, 2)) / (2 * delta);
        double hScale = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, new[] { c })
            .HMagnitude![0];
        double relative = divH.Magnitude * delta / Math.Max(hScale, 1e-30);
        Assert.True(relative < 0.05, $"∇·H·δ/|H| = {relative:e3} (should be the quadrature floor)");
    }

    [Fact]
    public void MultiLayerMap_IsBitwiseIdentical_AtAnyDop()
    {
        var stackup = LayeredStackup.CoveredPatch(2.2, 0.0, 1.0e-3, 0.6e-3);
        double f = BalanisF;
        double hMetal = stackup.InterfaceHeights()[LayeredStackup.CoveredPatchMetalInterface];
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: hMetal, portFraction: 0);
        var table = new MultiLayerKernelTable(stackup, f, 0.03,
            sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        var points = new[]
        {
            new Vector3D(0, 0, hMetal + 0.3e-3),
            new Vector3D(4e-3, 1e-3, hMetal + 1.0e-3),
        };
        var serial = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, points,
            maxDegreeOfParallelism: 1);
        var parallel = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, points);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(serial.E[i], parallel.E[i]);
            Assert.Equal(serial.H![i], parallel.H![i]);
        }
    }

    [Fact]
    public void PerZPoleResidues_AtNEqualsOne_MatchTheSingleSlabResidues()
    {
        // The multi-layer null-vector per-z residues must reproduce the single-slab analytic
        // per-z residues at every observation height, at the TM0 pole Balanis carries.
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        var stackup = LayeredStackup.FromSubstrate(substrate);
        double k0 = K0, d = substrate.ThicknessMeters;
        var poles = SurfaceWavePoles.Find(substrate, k0);
        Assert.NotEmpty(poles);
        foreach (var pole in poles)
            // Strictly off the source plane z = d (where ∂zΦ jumps — the source sheet /
            // ε discontinuity); a field map never samples the metal plane itself.
            foreach (double z in new[] { 0.3 * d, 0.7 * d, 0.99 * d, 1.5 * d, 2.5 * d })
            {
                var want = LayeredFieldKernels.PoleResidues(substrate, k0, pole.KRho, z);
                var got = TransmissionLineGreens.PoleFieldResidues(
                    stackup, k0, pole.KRho, pole.IsTm, m: 0, z);
                // A common scale (the largest residue component) — a TM pole makes G̃_A/∂zG̃_A
                // vanish, so a per-component relative test divides ~0 by ~0; scale by the
                // dominant K̃_Φ residue instead.
                double scale = new[] { want.A, want.W, want.Phi, want.DzPhi, want.DzA, want.DzW }
                    .Max(c => c.Magnitude);
                void Near(Complex w, Complex g, string what) =>
                    Assert.True((w - g).Magnitude <= 1e-8 * scale,
                        $"{what} z/d={z / d:g3}: want {w}, got {g} (scale {scale:e2})");
                Near(want.A, got.GA, "Res G̃_A");
                Near(want.W, got.W, "Res W̃");
                Near(want.Phi, got.Phi, "Res K̃_Φ");
                Near(want.DzPhi, got.DzPhi, "Res ∂zK̃_Φ");
                Near(want.DzA, got.DzA, "Res ∂zG̃_A");
                Near(want.DzW, got.DzW, "Res ∂zW̃");
            }
    }

    // ---- N = 1, top source: the per-z kernels ≡ SpectralProfiles ----

    public static IEnumerable<object[]> ProfileSamples()
    {
        double k0 = K0;
        double k1 = k0 * Math.Sqrt(2.2);
        double d = 1.588e-3;
        foreach (double kRho in new[] { 0.3 * k0, 0.999 * k0, 0.5 * (k0 + k1), 1.2 * k1, 5 * k1, 40 * k1 })
            foreach (double z in new[] { 0.01 * d, 0.25 * d, 0.5 * d, 0.99 * d, d, 1.5 * d, 2 * d })
                yield return new object[] { kRho, z };
    }

    [Theory]
    [MemberData(nameof(ProfileSamples))]
    public void PerZFieldKernels_AtNEqualsOne_MatchSpectralProfiles(double kRho, double z)
    {
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        var stackup = LayeredStackup.FromSubstrate(substrate);
        double k0 = K0;
        var kr = new Complex(kRho, 0);
        var kz0 = Kz(k0 * k0, kr);
        var (ga, w, phi, dzPhi, dzA, dzW) =
            TransmissionLineGreens.EvaluateField(stackup, k0, kr, kz0, m: 0, z);

        var (sGa, sW, sPhi) = SpectralProfiles.Evaluate(substrate, k0, kr, kz0, z);
        var (sDzA, sDzW, sDzPhi) = SpectralProfiles.EvaluateDz(substrate, k0, kr, kz0, z);
        AssertRel(sGa, ga, 1e-11, $"G̃_A(kρ={kRho:g4}, z={z:g4})");
        AssertRel(sW, w, 1e-11, $"W̃(kρ={kRho:g4}, z={z:g4})");
        AssertRel(sPhi, phi, 1e-11, $"K̃_Φ(kρ={kRho:g4}, z={z:g4})");
        AssertRel(sDzA, dzA, 1e-11, $"∂zG̃_A(kρ={kRho:g4}, z={z:g4})");
        AssertRel(sDzW, dzW, 1e-11, $"∂zW̃(kρ={kRho:g4}, z={z:g4})");
        AssertRel(sDzPhi, dzPhi, 1e-11, $"∂zK̃_Φ(kρ={kRho:g4}, z={z:g4})");
    }
}
