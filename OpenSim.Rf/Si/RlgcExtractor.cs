using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>
/// Per-unit-length RLGC matrices of an N-conductor coupled line (SI Stage S3). All
/// per-unit-length; matrices are N×N in the cross-section's trace order.
/// R(f) = max(R_dc, R_skin·√f) per conductor — DC plus the two-sided skin-effect form,
/// continuous at the crossover BY CONSTRUCTION (R_skin·√f_c = R_dc exactly where
/// δ = t/2). G(ω) = ω·C″ from the complex-ε solve. L = µ₀ε₀·C_air⁻¹ (the quasi-TEM
/// identity — exact in the TEM limit, the model's stated regime).
/// </summary>
public sealed record RlgcResult(
    int ConductorCount,
    double[,] CapacitanceFaradsPerMeter,
    double[,] CapacitanceLossFaradsPerMeter,
    double[,] AirCapacitanceFaradsPerMeter,
    double[,] InductanceHenriesPerMeter,
    double[] ResistanceDcOhmsPerMeter,
    double[] SkinResistanceOhmsPerMeterPerSqrtHz,
    IReadOnlyList<string> Assumptions,
    /// <summary>Optional full N×N series-resistance matrix R(f) [Ω/m] from the proximity-
    /// effect filament solve (Stage S8). When present it REPLACES the per-conductor
    /// <see cref="ResistancePerMeter"/> diagonal in the MTL generator — carrying the
    /// current-crowding coupling the scalar model cannot. Null ⇒ the v1 scalar model.</summary>
    Func<double, double[,]>? ResistanceMatrixOhmsPerMeter = null,
    /// <summary>Optional frequency-dependent INTERNAL inductance ΔL(f) [H/m], N×N, added to
    /// the external <see cref="InductanceHenriesPerMeter"/> (Stage S8). → 0 at high frequency
    /// (current on the surface, external only) by construction. Null ⇒ no internal-L term.</summary>
    Func<double, double[,]>? InternalInductanceHenriesPerMeter = null)
{
    /// <summary>Per-conductor series resistance at f [Ω/m]: the DC/skin crossover.</summary>
    public double ResistancePerMeter(int conductor, double frequencyHz) =>
        Math.Max(ResistanceDcOhmsPerMeter[conductor],
            SkinResistanceOhmsPerMeterPerSqrtHz[conductor] * Math.Sqrt(Math.Max(0, frequencyHz)));

    /// <summary>The conductance matrix at ω [S/m]: G(ω) = ω·C″ (dielectric loss only).</summary>
    public double[,] ConductancePerMeter(double frequencyHz)
    {
        double w = 2 * Math.PI * frequencyHz;
        int n = ConductorCount;
        var g = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                g[i, j] = w * CapacitanceLossFaradsPerMeter[i, j];
        return g;
    }
}

/// <summary>
/// The 2D quasi-static boundary-element extraction behind the SI track's RLGC engine.
///
/// <para><b>Kernel.</b> The layered electrostatic image series (`MultiLayerImages`,
/// the k_ρ → ∞ TLGF limit that Stage F derived for the RF extraction) transfers to 2D
/// verbatim: a LINE charge at the metal interface has potential
/// V(ρ) = −(1/2πε₀)·Σ cᵢ·ln√(ρ² + Dᵢ²) with the SAME coefficients cᵢ and depths Dᵢ as
/// the 3D point-charge series — the spectral e^{−kD}/k structure is dimension-
/// independent. The full grounded-stack series is charge-neutral (Σcᵢ = 0), which is
/// what makes the 2D potential reference-free; the expansion is deepened until the
/// truncated residue |Σcᵢ| is negligible (there is no Sommerfeld remainder here to
/// absorb a tail).</para>
///
/// <para><b>Discretization.</b> Galerkin pulse bases on cosine-graded panels (the charge
/// density's edge singularity is ~1/√distance; cosine grading resolves it without
/// adaptive machinery). Every conductor lies on ONE line, so each Galerkin moment is the
/// closed form ∬ ½ln((x−y)²+D²) = ΣG₂(corners), G₂(u) = ¼(u²−D²)ln(u²+D²) − ¾u² +
/// D·u·arctan(u/D) — no near-singular quadrature anywhere, and the D → 0 primary is the
/// classic u²(2ln|u|−3)/4 self-term. The matrix is symmetric by construction.</para>
///
/// <para><b>Matrices.</b> Column k of Maxwell C: conductor k at 1 V, others at 0 →
/// panel charges → per-conductor totals. C_air repeats the solve on an all-air stackup
/// of identical geometry (its image series collapses to primary + ground image — gated).
/// L = µ₀ε₀·C_air⁻¹; the complex-ε solve's −Im part is C″ (G = ωC″).</para>
/// </summary>
public static class RlgcExtractor
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>Deepen the image expansion until the truncated series' residual monopole
    /// falls below this fraction of the primary; the closure image then makes the kernel
    /// EXACTLY neutral, so what remains is a ≤1e-4 rearrangement at depth — far below the
    /// gates. The caps stop at 128 stack heights ON PURPOSE: the reciprocal's Neumann
    /// series has intermediate coefficients growing like |c₁/c₀|^k (>1.6^k on real
    /// substrates), so a 512-deep expansion needs ~20 cancelling digits and destroys
    /// itself in doubles — measured live as a 1e+20 "primary" at depth 384·d.</summary>
    private const double NeutralityTolerance = 1e-4;
    private static readonly double[] DepthCapFactors = { 32, 64, 128 };

    public static RlgcResult Extract(CoupledLineCrossSection section, int panelsPerTrace = 48)
    {
        if (panelsPerTrace < 4)
            throw new ArgumentOutOfRangeException(nameof(panelsPerTrace),
                "At least 4 panels per trace are needed to resolve the edge charge.");

        int n = section.Traces.Count;
        var panels = BuildPanels(section.Traces, panelsPerTrace);

        // Dielectric solve (complex ε carries tanδ) and the air solve for L.
        var images = StaticImages(section.Stackup, section.MetalInterface);
        var cComplex = SolveCapacitance(panels, n, images);

        var airLayers = section.Stackup.Layers
            .Select(l => new LayeredStackup.Layer(1.0, 0.0, l.ThicknessMeters)).ToArray();
        var airImages = StaticImages(new LayeredStackup(airLayers), section.MetalInterface);
        var cAirComplex = SolveCapacitance(panels, n, airImages);

        var c = new double[n, n];
        var cLoss = new double[n, n];
        var cAir = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                c[i, j] = cComplex[i, j].Real;
                cLoss[i, j] = -cComplex[i, j].Imaginary;   // Y = jωC_c ⇒ G = −ω·Im(C_c)
                cAir[i, j] = cAirComplex[i, j].Real;
            }

        var inductance = ScaleMatrix(Invert(cAir), Mu0 * Epsilon0);

        var rDc = new double[n];
        var rSkin = new double[n];
        for (int i = 0; i < n; i++)
        {
            var t = section.Traces[i];
            rDc[i] = 1.0 / (t.ConductivitySiemensPerMeter * t.WidthMeters * t.ThicknessMeters);
            // Two-sided surface conduction: R_ac = R_s/(2w), R_s = √(πfµ₀/σ). Crossing
            // R_dc exactly where δ = t/2, so max(R_dc, R_skin√f) is continuous.
            rSkin[i] = Math.Sqrt(Math.PI * Mu0 / t.ConductivitySiemensPerMeter)
                       / (2 * t.WidthMeters);
        }

        var assumptions = new List<string>
        {
            "Quasi-TEM per-unit-length model: C from the 2D layered electrostatic BEM, "
                + "L = µ₀ε₀·C_air⁻¹ (exact in the TEM limit), G = ω·C″ from the complex-ε "
                + "solve, R = max(R_dc, R_s(f)/2w) with a continuous skin crossover at δ = t/2.",
            "Traces are zero-thickness strips for C/L (w ≫ t); thickness enters R only. "
                + "Proximity-effect current crowding and return-plane resistance are NOT "
                + "modeled (named follow-ups) — R is per-conductor forward resistance.",
            "All conductors are coplanar at one stackup interface; broadside coupling "
                + "across layers is out of scope by construction.",
        };
        return new RlgcResult(n, c, cLoss, cAir, inductance, rDc, rSkin, assumptions);
    }

    // ------------------------------------------------------------------
    // Kernel: the deep static image series (2D ln kernels).
    // ------------------------------------------------------------------

    private static IReadOnlyList<MultiLayerImages.Image> StaticImages(
        LayeredStackup stackup, int metalInterface)
    {
        double residue = double.MaxValue, primary = 1;
        foreach (double bounces in DepthCapFactors)
        {
            // The stable-arithmetic budget is the Neumann ITERATION count, which scales
            // with the THINNEST layer's round trip — a cap in total-thickness multiples
            // let a thin-layer stack run hundreds of iterations and shred its digits
            // (found live: a 0.62 "residue" on the buried-metal fixture).
            double thinnest = stackup.Layers.Min(l => l.ThicknessMeters);
            double factor = bounces * 2 * thinnest / stackup.TotalThicknessMeters;
            var images = MultiLayerImages.PhiImagesInterior(stackup, metalInterface,
                depthCapFactor: factor, coefficientFloor: 1e-12, relativePrune: 1e-12);
            Complex sum = Complex.Zero;
            double deepest = 0;
            foreach (var image in images)
            {
                sum += image.Coeff;
                deepest = Math.Max(deepest, image.Depth);
            }
            primary = images[0].Coeff.Magnitude;
            residue = sum.Magnitude;
            if (residue > NeutralityTolerance * primary) continue;

            // Close the residual monopole with one image just past the kept tail: the
            // exact grounded-stack series is charge-neutral (Σcᵢ = 0 — the 2D log
            // potential is reference-free ONLY then), and the closure pins that exactly.
            // Its placement error is a rearrangement of ≤residue at depth — harmless.
            if (residue == 0) return images;
            var closed = new List<MultiLayerImages.Image>(images)
            {
                new(deepest + 2 * stackup.TotalThicknessMeters, -sum)
            };
            return closed;
        }
        throw new InvalidOperationException(
            "RLGC extraction could not neutralize the layered image series (residual "
            + $"monopole {residue / primary:g3} of primary at the deepest stable expansion) "
            + "— an extreme-contrast stackup; the extraction needs the priority-ordered "
            + "image search follow-up.");
    }

    // ------------------------------------------------------------------
    // BEM assembly + solve.
    // ------------------------------------------------------------------

    private readonly record struct Panel(double Start, double End, int Conductor)
    {
        public double Width => End - Start;
    }

    /// <summary>Cosine-graded panels per trace: edges x_j = c − (w/2)·cos(jπ/P) cluster
    /// panels toward the strip edges where the charge density diverges as 1/√distance.</summary>
    private static List<Panel> BuildPanels(IReadOnlyList<TraceCrossSection> traces, int perTrace)
    {
        var panels = new List<Panel>(traces.Count * perTrace);
        for (int c = 0; c < traces.Count; c++)
        {
            double half = traces[c].WidthMeters / 2;
            double center = traces[c].CenterMeters;
            double previous = center - half;
            for (int j = 1; j <= perTrace; j++)
            {
                double edge = center - half * Math.Cos(Math.PI * j / perTrace);
                panels.Add(new Panel(previous, edge, c));
                previous = edge;
            }
        }
        return panels;
    }

    /// <summary>The Maxwell capacitance matrix: Galerkin BEM with unit-potential drives.
    /// One factorization serves all N right-hand sides.</summary>
    private static Complex[,] SolveCapacitance(List<Panel> panels, int conductors,
        IReadOnlyList<MultiLayerImages.Image> images)
    {
        int m = panels.Count;
        var matrix = new ComplexDenseMatrix(m, m);
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                // ⟨V_b, pulse_a⟩ = −(1/2πε₀)·Σᵢ cᵢ·∬ ½ln((x−y)²+Dᵢ²) dx dy — symmetric,
                // evaluate once, scatter both ways (the MoM house pattern).
                Complex moment = Complex.Zero;
                foreach (var image in images)
                    moment += image.Coeff * LogMoment(panels[a], panels[b], image.Depth);
                var value = -moment / (2 * Math.PI * Epsilon0);
                matrix[a, b] = value;
                matrix[b, a] = value;
            }

        var lu = ComplexLu.Factor(matrix);
        var result = new Complex[conductors, conductors];
        for (int k = 0; k < conductors; k++)
        {
            var rhs = new Complex[m];
            for (int a = 0; a < m; a++)
                rhs[a] = panels[a].Conductor == k ? panels[a].Width : Complex.Zero;
            var charge = lu.Solve(rhs);
            for (int a = 0; a < m; a++)
                result[panels[a].Conductor, k] += charge[a] * panels[a].Width;
        }
        return result;
    }

    private static double LogMoment(in Panel a, in Panel b, double depth)
        => LogMoment(a.Start, a.End, b.Start, b.End, depth);

    /// <summary>∬_{x∈[a0,a1], y∈[b0,b1]} ½ln((x−y)² + D²) dx dy via the closed-form second
    /// antiderivative G₂ (G₂″(u) = ½ln(u²+D²)): the four-corner combination. Shared with the
    /// Stage S8 proximity filament solve (the SAME 2D log kernel, magnetic vector potential).</summary>
    internal static double LogMoment(double a0, double a1, double b0, double b1, double depth)
        => G2(a1 - b0, depth) + G2(a0 - b1, depth)
         - G2(a0 - b0, depth) - G2(a1 - b1, depth);

    /// <summary>G₂(u) = ¼(u²−D²)ln(u²+D²) − ¾u² + D·u·arctan(u/D); the D → 0 limit is
    /// the classic ½u²ln|u| − ¾u² collinear self-term (u = 0 ⇒ 0 — the log's zero is
    /// integrable and the combination needs no special-casing).</summary>
    internal static double G2(double u, double depth)
    {
        double r2 = u * u + depth * depth;
        if (r2 == 0) return 0;
        double value = 0.25 * (u * u - depth * depth) * Math.Log(r2) - 0.75 * u * u;
        if (depth > 0) value += depth * u * Math.Atan2(u, depth);
        return value;
    }

    // ------------------------------------------------------------------
    // Small dense helpers (N = conductor count, single digits).
    // ------------------------------------------------------------------

    private static double[,] Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var matrix = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                matrix[i, j] = a[i, j];
        var lu = ComplexLu.Factor(matrix);
        var inverse = new double[n, n];
        for (int k = 0; k < n; k++)
        {
            var rhs = new Complex[n];
            rhs[k] = Complex.One;
            var column = lu.Solve(rhs);
            for (int i = 0; i < n; i++) inverse[i, k] = column[i].Real;
        }
        return inverse;
    }

    private static double[,] ScaleMatrix(double[,] a, double scale)
    {
        int n = a.GetLength(0);
        var r = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] = a[i, j] * scale;
        return r;
    }
}
