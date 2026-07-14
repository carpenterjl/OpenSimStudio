using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The RLGC extraction gates (SI Stage S3). Absolute accuracy is pinned three
/// independent ways: (1) the strip-over-ground EXACT anchor through the classic
/// r = w/4 equivalence (C = 2πε₀/acosh(h/r)); (2) the Hammerstad–Jensen microstrip
/// fits across a w/h × εr grid (the fit itself is ~0.2% — the band is the fit's
/// accuracy, stated); (3) an INDEPENDENT test-local finite-difference oracle
/// (∇·(ε∇φ) = 0 on a grid, house CSR + CG) for the coupled C12 that no closed form
/// covers — the CPS conformal map converges only LOGARITHMICALLY in ground distance
/// (the ground's C₁g/2 contaminates the odd mode at the 25% level even 200 spans away),
/// so it cannot gate a grounded solver and is deliberately absent. Matrix identities
/// and collapses hold at machine/solver precision.
/// </summary>
public class RlgcExtractorTests
{
    private const double Eps0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;
    private const double C0 = 299792458.0;

    private static LayeredStackup Slab(double epsR, double h, double tanD = 0)
        => new(new[] { new LayeredStackup.Layer(epsR, tanD, h) });

    private static RlgcResult Microstrip(double epsR, double h, double w,
        int panels = 64, double tanD = 0)
        => RlgcExtractor.Extract(new CoupledLineCrossSection(Slab(epsR, h, tanD), 0,
            new[] { TraceCrossSection.Copper(0, w) }), panels);

    private static (double Z0, double EpsEff) LineParameters(RlgcResult r)
    {
        double c = r.CapacitanceFaradsPerMeter[0, 0];
        double cAir = r.AirCapacitanceFaradsPerMeter[0, 0];
        return (1.0 / (C0 * Math.Sqrt(c * cAir)), c / cAir);
    }

    // ------------------------------------------------------------------
    // Exact anchor: thin strip high over ground ≡ round wire of radius w/4.
    // ------------------------------------------------------------------

    [Fact]
    public void ThinStripOverGround_MatchesEquivalentRoundWire()
    {
        // Air "dielectric", h/w = 50 ⇒ h/r = 200: the w/4 equivalence error is O((r/h)²)
        // ≈ 2e-5, so the 1% band is all discretization. Exact C = 2πε₀/acosh(h/r).
        const double h = 10e-3, w = h / 50;
        var result = Microstrip(1.0, h, w, panels: 64);
        double exact = 2 * Math.PI * Eps0 / Math.Acosh(h / (w / 4));
        double measured = result.CapacitanceFaradsPerMeter[0, 0];
        Assert.True(Math.Abs(measured - exact) / exact < 0.01,
            $"C = {measured:g6} vs exact {exact:g6} ({(measured - exact) / exact:P2})");
        // Air stack ⇒ the dielectric and air solves are the same problem: L·C = µ₀ε₀.
        double lc = result.InductanceHenriesPerMeter[0, 0] * measured;
        Assert.True(Math.Abs(lc - Mu0 * Eps0) / (Mu0 * Eps0) < 1e-9);
    }

    // ------------------------------------------------------------------
    // Published fit: Hammerstad–Jensen microstrip Z₀ and ε_eff.
    // ------------------------------------------------------------------

    private static (double Z0, double EpsEff) HammerstadJensen(double u, double epsR)
    {
        const double eta0 = 376.730313668;
        double f = 6 + (2 * Math.PI - 6) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        double z01 = eta0 / (2 * Math.PI)
                     * Math.Log(f / u + Math.Sqrt(1 + Math.Pow(2 / u, 2)));
        double a = 1 + Math.Log((Math.Pow(u, 4) + Math.Pow(u / 52, 2))
                                / (Math.Pow(u, 4) + 0.432)) / 49
                     + Math.Log(1 + Math.Pow(u / 18.1, 3)) / 18.7;
        double b = 0.564 * Math.Pow((epsR - 0.9) / (epsR + 3), 0.053);
        double epsEff = (epsR + 1) / 2 + (epsR - 1) / 2 * Math.Pow(1 + 10 / u, -a * b);
        return (z01 / Math.Sqrt(epsEff), epsEff);
    }

    [Theory]
    [InlineData(2.2, 0.5)]
    [InlineData(2.2, 1.0)]
    [InlineData(2.2, 3.0)]
    [InlineData(4.4, 0.5)]
    [InlineData(4.4, 1.0)]
    [InlineData(4.4, 3.0)]
    [InlineData(9.8, 1.0)]
    [InlineData(9.8, 3.0)]
    public void Microstrip_MatchesHammerstadJensen(double epsR, double u)
    {
        // Band 2%: the H-J fit is quoted ~0.2%, the zero-thickness BEM adds ~0.5% —
        // the band is the fit's own documented accuracy plus margin, not a loosened gate.
        const double h = 0.5e-3;
        var (z0, epsEff) = LineParameters(Microstrip(epsR, h, u * h, panels: 64));
        var (z0Ref, epsEffRef) = HammerstadJensen(u, epsR);
        Assert.True(Math.Abs(z0 - z0Ref) / z0Ref < 0.02,
            $"εr={epsR} u={u}: Z0 {z0:g5} vs H-J {z0Ref:g5} ({(z0 - z0Ref) / z0Ref:P2})");
        Assert.True(Math.Abs(epsEff - epsEffRef) / epsEffRef < 0.02,
            $"εr={epsR} u={u}: ε_eff {epsEff:g5} vs H-J {epsEffRef:g5}");
    }

    // ------------------------------------------------------------------
    // Independent FD oracle: coupled microstrip C11/C12, and the buried metal case.
    // ------------------------------------------------------------------

    [Fact]
    public void CoupledMicrostrip_MatchesIndependentFdOracle()
    {
        const double h = 0.5e-3, w = 0.5e-3, s = 0.5e-3;
        var section = new CoupledLineCrossSection(Slab(4.4, h), 0, new[]
        {
            TraceCrossSection.Copper(-(s + w) / 2, w),
            TraceCrossSection.Copper(+(s + w) / 2, w),
        });
        var bem = RlgcExtractor.Extract(section, panelsPerTrace: 64);

        var oracle = RichardsonFdOracle(
            layers: new[] { (4.4, h) }, metalInterface: 0,
            strips: new[] { (-(s + w) / 2 - w / 2, -(s + w) / 2 + w / 2),
                            (+(s + w) / 2 - w / 2, +(s + w) / 2 + w / 2) });

        // The raw FD values converge FIRST-ORDER in cell size (the edge-charge
        // singularity), measured live: C12 −9.83/−9.41/−9.21/−9.01 e-12 at h/8..h/24
        // against the panel-converged BEM −8.65e-12. The gate therefore compares the
        // oracle's Richardson limit (measured to land 0.007% / 0.7% from the BEM).
        double c11 = bem.CapacitanceFaradsPerMeter[0, 0];
        double c12 = bem.CapacitanceFaradsPerMeter[0, 1];
        Assert.True(Math.Abs(c11 - oracle[0, 0]) / oracle[0, 0] < 0.02,
            $"C11 BEM {c11:g5} vs FD limit {oracle[0, 0]:g5} ({(c11 - oracle[0, 0]) / oracle[0, 0]:P2})");
        Assert.True(Math.Abs(c12 - oracle[0, 1]) / Math.Abs(oracle[0, 1]) < 0.03,
            $"C12 BEM {c12:g5} vs FD limit {oracle[0, 1]:g5} ({(c12 - oracle[0, 1]) / oracle[0, 1]:P2})");
    }

    [Fact]
    public void BuriedMetal_MatchesFdOracle_AndHomogeneousScaling()
    {
        // Metal buried under a THICK same-εr cover: the interior-source image path.
        const double h = 0.5e-3, w = 0.5e-3, cover = 2.0e-3;
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0, h),
            new LayeredStackup.Layer(4.4, 0, cover),
        });
        var section = new CoupledLineCrossSection(stack, 0,
            new[] { TraceCrossSection.Copper(0, w) });
        var bem = RlgcExtractor.Extract(section, panelsPerTrace: 64);
        double c = bem.CapacitanceFaradsPerMeter[0, 0];

        var oracle = RichardsonFdOracle(new[] { (4.4, h), (4.4, cover) }, 0,
            new[] { (-w / 2, w / 2) });
        Assert.True(Math.Abs(c - oracle[0, 0]) / oracle[0, 0] < 0.02,
            $"buried C BEM {c:g5} vs FD limit {oracle[0, 0]:g5} "
            + $"({(c - oracle[0, 0]) / oracle[0, 0]:P2})");

        // Homogeneous-embedding identity: with the cover thick and matched, the line is
        // ~fully embedded ⇒ C approaches εr · C(air, same geometry) FROM BELOW (the
        // finite cover leaks some field into air, which can only reduce C).
        double cAir = bem.AirCapacitanceFaradsPerMeter[0, 0];
        Assert.True(c < 4.4 * cAir, "embedded C must sit below the fully-embedded limit");
        Assert.True(c > 0.9 * 4.4 * cAir,
            $"a 4h cover should be near-fully embedded: C = {c:g5} vs εr·C_air = {4.4 * cAir:g5}");
    }

    // ------------------------------------------------------------------
    // Coupled physics: symmetry, ordering, monotone trends, gap collapse.
    // ------------------------------------------------------------------

    [Fact]
    public void CoupledPair_SymmetryOrderingAndGapCollapse()
    {
        const double h = 0.5e-3, w = 0.5e-3;
        var single = Microstrip(4.4, h, w);
        var (z0Single, _) = LineParameters(single);

        double previousCoupling = double.MaxValue;
        foreach (double s in new[] { 0.25e-3, 0.5e-3, 1.0e-3, 2.0e-3 })
        {
            var r = RlgcExtractor.Extract(new CoupledLineCrossSection(Slab(4.4, h), 0, new[]
            {
                TraceCrossSection.Copper(-(s + w) / 2, w),
                TraceCrossSection.Copper(+(s + w) / 2, w),
            }), 48);
            var c = r.CapacitanceFaradsPerMeter;
            var cAir = r.AirCapacitanceFaradsPerMeter;

            Assert.True(Math.Abs(c[0, 0] - c[1, 1]) / c[0, 0] < 1e-9, "mirror symmetry");
            Assert.True(c[0, 1] < 0, "Maxwell off-diagonals are negative");
            Assert.True(c[0, 0] > -c[0, 1], "diagonal dominance");

            // Even/odd modal impedances of the symmetric pair bracket the single line.
            double ze = 1 / (C0 * Math.Sqrt((c[0, 0] + c[0, 1]) * (cAir[0, 0] + cAir[0, 1])));
            double zo = 1 / (C0 * Math.Sqrt((c[0, 0] - c[0, 1]) * (cAir[0, 0] - cAir[0, 1])));
            Assert.True(zo < z0Single && z0Single < ze,
                $"s={s * 1e3}mm: Z0o {zo:g4} < Z0 {z0Single:g4} < Z0e {ze:g4} violated");

            double coupling = -c[0, 1] / c[0, 0];
            Assert.True(coupling < previousCoupling, "coupling must fall monotonically with gap");
            previousCoupling = coupling;
        }

        // Wide-gap collapse: each line returns to the isolated single line.
        var far = RlgcExtractor.Extract(new CoupledLineCrossSection(Slab(4.4, h), 0, new[]
        {
            TraceCrossSection.Copper(-(20 * h + w) / 2, w),
            TraceCrossSection.Copper(+(20 * h + w) / 2, w),
        }), 48);
        double c11Far = far.CapacitanceFaradsPerMeter[0, 0];
        Assert.True(Math.Abs(c11Far - single.CapacitanceFaradsPerMeter[0, 0])
                    / single.CapacitanceFaradsPerMeter[0, 0] < 0.01,
            "20h apart, C11 must collapse to the isolated line within 1%");
        Assert.True(-far.CapacitanceFaradsPerMeter[0, 1] / c11Far < 0.05);
    }

    // ------------------------------------------------------------------
    // Matrix identities, loss, resistance, convergence, typed failures.
    // ------------------------------------------------------------------

    [Fact]
    public void Matrices_AreSymmetricAndPhysical()
    {
        var r = RlgcExtractor.Extract(new CoupledLineCrossSection(Slab(4.4, 0.5e-3), 0, new[]
        {
            TraceCrossSection.Copper(-0.6e-3, 0.4e-3),
            TraceCrossSection.Copper(0, 0.6e-3),
            TraceCrossSection.Copper(0.7e-3, 0.4e-3),
        }), 48);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.True(Math.Abs(r.CapacitanceFaradsPerMeter[i, j]
                    - r.CapacitanceFaradsPerMeter[j, i])
                    <= 1e-10 * Math.Abs(r.CapacitanceFaradsPerMeter[0, 0]), "C symmetric");
                Assert.True(Math.Abs(r.InductanceHenriesPerMeter[i, j]
                    - r.InductanceHenriesPerMeter[j, i])
                    <= 1e-9 * Math.Abs(r.InductanceHenriesPerMeter[0, 0]), "L symmetric");
                if (i == j)
                {
                    Assert.True(r.CapacitanceFaradsPerMeter[i, i] > 0);
                    Assert.True(r.InductanceHenriesPerMeter[i, i] > 0);
                }
                else
                {
                    Assert.True(r.CapacitanceFaradsPerMeter[i, j] < 0);
                    Assert.True(r.InductanceHenriesPerMeter[i, j] > 0,
                        "mutual inductance of parallel same-direction lines is positive");
                }
            }
    }

    [Fact]
    public void Loss_ProducesTheConductanceMatrix()
    {
        var lossless = Microstrip(4.4, 0.5e-3, 0.5e-3, tanD: 0);
        Assert.True(Math.Abs(lossless.CapacitanceLossFaradsPerMeter[0, 0])
                    <= 1e-12 * lossless.CapacitanceFaradsPerMeter[0, 0]);

        var lossy = Microstrip(4.4, 0.5e-3, 0.5e-3, tanD: 0.02);
        double cLoss = lossy.CapacitanceLossFaradsPerMeter[0, 0];
        Assert.True(cLoss > 0, "tanδ > 0 must yield C″ > 0");
        // The filling-factor bound: C″ ≤ tanδ·C′ (only the dielectric-filled part of the
        // field is lossy; equality is the fully-embedded limit).
        Assert.True(cLoss <= 0.02 * lossy.CapacitanceFaradsPerMeter[0, 0] * (1 + 1e-9));
        // G(ω) = ω·C″ exactly, by definition of the record's accessor.
        double f = 1e9, omega = 2 * Math.PI * f;
        Assert.Equal(omega * cLoss, lossy.ConductancePerMeter(f)[0, 0], 12);
    }

    [Fact]
    public void Resistance_DcValueAndContinuousSkinCrossover()
    {
        const double sigma = 5.8e7, w = 0.5e-3, t = 35e-6;
        var r = Microstrip(4.4, 0.5e-3, w);
        double rdc = 1 / (sigma * w * t);
        Assert.Equal(rdc, r.ResistanceDcOhmsPerMeter[0], 9);

        // The crossover frequency where δ = t/2: skin and DC forms agree EXACTLY there.
        double fc = 4 / (Math.PI * Mu0 * sigma * t * t);
        double skinAtFc = r.SkinResistanceOhmsPerMeterPerSqrtHz[0] * Math.Sqrt(fc);
        Assert.True(Math.Abs(skinAtFc - rdc) / rdc < 1e-9, "crossover must be continuous");
        Assert.Equal(rdc, r.ResistancePerMeter(0, fc / 4), 9);          // below: DC
        Assert.True(r.ResistancePerMeter(0, 4 * fc) > 1.9 * rdc);       // above: √f growth
    }

    [Fact]
    public void SelfConvergence_UnderPanelRefinement()
    {
        double c16 = Microstrip(4.4, 0.5e-3, 0.5e-3, panels: 16).CapacitanceFaradsPerMeter[0, 0];
        double c32 = Microstrip(4.4, 0.5e-3, 0.5e-3, panels: 32).CapacitanceFaradsPerMeter[0, 0];
        double c64 = Microstrip(4.4, 0.5e-3, 0.5e-3, panels: 64).CapacitanceFaradsPerMeter[0, 0];
        double d1 = Math.Abs(c32 - c16), d2 = Math.Abs(c64 - c32);
        Assert.True(d2 < d1, "refinement must converge");
        Assert.True(d2 / c64 < 0.003, $"32→64 panels moved C by {d2 / c64:P3}");
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        Assert.Throws<ArgumentException>(() => new CoupledLineCrossSection(
            Slab(4.4, 0.5e-3), 0, new[]
            {
                TraceCrossSection.Copper(0, 1e-3),
                TraceCrossSection.Copper(0.5e-3, 1e-3),   // overlaps the first
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoupledLineCrossSection(
            Slab(4.4, 0.5e-3), 2, new[] { TraceCrossSection.Copper(0, 1e-3) }));
    }

    // ------------------------------------------------------------------
    // The independent finite-difference oracle: ∇·(ε∇φ) = 0 on a uniform grid,
    // Dirichlet ground/box, strips as fixed-potential node runs at the metal row,
    // charges from the discrete Gauss flux. Deliberately a DIFFERENT method with
    // DIFFERENT error behaviour than the BEM (grid vs panels, first-order vs
    // analytic kernel) — agreement is evidence, shared bugs are implausible.
    // ------------------------------------------------------------------

    /// <summary>The oracle's Richardson limit: raw FD capacitances converge first-order
    /// in the cell size (edge singularity), so C₀ ≈ 2·C(g/2) − C(g). Measured against
    /// the panel-converged BEM: 0.007% on C11, 0.7% on C12.</summary>
    private static double[,] RichardsonFdOracle((double EpsR, double Thickness)[] layers,
        int metalInterface, (double X0, double X1)[] strips)
    {
        double h = layers[0].Thickness;
        var coarse = FdCapacitanceOracle(layers, metalInterface, strips, h / 8);
        var fine = FdCapacitanceOracle(layers, metalInterface, strips, h / 16);
        int n = strips.Length;
        var limit = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                limit[i, j] = 2 * fine[i, j] - coarse[i, j];
        return limit;
    }

    private static double[,] FdCapacitanceOracle((double EpsR, double Thickness)[] layers,
        int metalInterface, (double X0, double X1)[] strips, double cell)
    {
        double stackHeight = layers.Sum(l => l.Thickness);
        double metalZ = layers.Take(metalInterface + 1).Sum(l => l.Thickness);
        double xMin = strips.Min(s => s.X0), xMax = strips.Max(s => s.X1);
        double margin = 8 * Math.Max(stackHeight, xMax - xMin);

        int nx = (int)Math.Round((xMax - xMin + 2 * margin) / cell) + 1;
        int nz = (int)Math.Round((stackHeight + margin) / cell) + 1;
        double x0 = xMin - margin;
        int metalRow = (int)Math.Round(metalZ / cell);

        // Cell permittivity by row (cell r spans z ∈ [r·cell, (r+1)·cell]).
        var cellEps = new double[nz - 1];
        for (int r = 0; r < nz - 1; r++)
        {
            double zMid = (r + 0.5) * cell;
            double top = 0;
            cellEps[r] = 1.0;
            foreach (var (epsR, thickness) in layers)
            {
                top += thickness;
                if (zMid < top) { cellEps[r] = epsR; break; }
            }
        }

        int NodeId(int i, int j) => j * nx + i;
        int Conductor(int i, int j)
        {
            if (j != metalRow) return -1;
            double x = x0 + i * cell;
            for (int s = 0; s < strips.Length; s++)
                if (x >= strips[s].X0 - cell / 2 && x <= strips[s].X1 + cell / 2) return s;
            return -1;
        }
        bool IsBoundary(int i, int j) => i == 0 || i == nx - 1 || j == 0 || j == nz - 1;

        // Free-node numbering.
        var freeIndex = new int[nx * nz];
        Array.Fill(freeIndex, -1);
        int freeCount = 0;
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
                if (!IsBoundary(i, j) && Conductor(i, j) < 0)
                    freeIndex[NodeId(i, j)] = freeCount++;

        // Edge conductances on the square grid: horizontal edges average the cell rows
        // above/below; vertical edges take their own cell row (interfaces lie ON grid
        // rows by construction, so no edge straddles a jump).
        double HorizontalEps(int j) =>
            j == 0 ? cellEps[0]
            : j == nz - 1 ? cellEps[nz - 2]
            : 0.5 * (cellEps[j - 1] + cellEps[j]);
        double VerticalEps(int j) => cellEps[j];   // edge from row j to j+1 sits in cell j

        var builder = new SparseMatrixBuilder(freeCount, freeCount);
        var rhsPerConductor = new double[strips.Length][];
        for (int s = 0; s < strips.Length; s++) rhsPerConductor[s] = new double[freeCount];

        // One reusable neighbor buffer — never stackalloc inside a hot loop (it frees
        // only at method exit; a live testhost StackOverflow taught the RWG assembler).
        var neighbors = new (int I, int J, double W)[4];
        void FillNeighbors(int i, int j)
        {
            neighbors[0] = (i - 1, j, HorizontalEps(j));
            neighbors[1] = (i + 1, j, HorizontalEps(j));
            neighbors[2] = (i, j - 1, VerticalEps(j - 1));
            neighbors[3] = (i, j + 1, VerticalEps(j));
        }

        for (int j = 1; j < nz - 1; j++)
            for (int i = 1; i < nx - 1; i++)
            {
                int row = freeIndex[NodeId(i, j)];
                if (row < 0) continue;
                FillNeighbors(i, j);
                double diagonal = 0;
                foreach (var (ni, nj, weight) in neighbors)
                {
                    diagonal += weight;
                    int neighborFree = freeIndex[NodeId(ni, nj)];
                    if (neighborFree >= 0) builder.Add(row, neighborFree, -weight);
                    else
                    {
                        int conductor = Conductor(ni, nj);
                        if (conductor >= 0) rhsPerConductor[conductor][row] += weight;
                        // boundary nodes are 0 V — no rhs contribution
                    }
                }
                builder.Add(row, row, diagonal);
            }

        var matrix = builder.Build();
        var cg = new ConjugateGradientSolver { Tolerance = 1e-10 };
        var result = new double[strips.Length, strips.Length];
        for (int k = 0; k < strips.Length; k++)
        {
            var phi = new double[freeCount];
            var solve = cg.Solve(matrix, rhsPerConductor[k], phi);
            Assert.True(solve.Converged, "FD oracle CG must converge");

            // Discrete Gauss: Q_i = ε₀·Σ_{conductor-i nodes} Σ_edges ε_e·(V_node − φ_nb).
            for (int j = 1; j < nz - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    int conductor = Conductor(i, j);
                    if (conductor < 0) continue;
                    double v = conductor == k ? 1.0 : 0.0;
                    FillNeighbors(i, j);
                    foreach (var (ni, nj, weight) in neighbors)
                    {
                        int neighborConductor = Conductor(ni, nj);
                        if (neighborConductor >= 0) continue;   // internal to the strip run
                        int neighborFree = freeIndex[NodeId(ni, nj)];
                        double phiNeighbor = neighborFree >= 0 ? phi[neighborFree] : 0.0;
                        result[conductor, k] += Eps0 * weight * (v - phiNeighbor);
                    }
                }
        }
        return result;
    }
}
