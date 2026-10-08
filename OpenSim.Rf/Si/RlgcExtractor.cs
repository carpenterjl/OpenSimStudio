using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>
/// Per-unit-length RLGC matrices of an N-conductor coupled line (SI Stage S3). All
/// per-unit-length; matrices are N×N in the cross-section's trace order.
/// L = µ₀ε₀·C_air⁻¹ (the quasi-TEM identity — exact in the TEM limit, the model's stated
/// regime). What R, G and the frequency dependence of C are depends on the
/// <see cref="RlgcModel"/> the extraction ran with. The kernel model: R(f) =
/// max(R_dc, R_skin·√f) per conductor (two-sided strip only, continuous where δ = t/2) and
/// G(ω) = ω·C″ with constant C. The board model: the full R(f) matrix and internal
/// inductance through <see cref="ResistanceMatrixOhmsPerMeter"/> /
/// <see cref="InternalInductanceHenriesPerMeter"/>, and C(f), G(f) through
/// <see cref="CapacitancePerMeter"/> / <see cref="ConductancePerMeter"/>.
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

    /// <summary>The dielectric's frequency dependence. Null is the constant complex ε the
    /// solve was run at (C and C″ the same at every frequency, which is not causal); the
    /// board model sets the Djordjevic–Sarkar shape, and <see cref="CapacitanceFaradsPerMeter"/>
    /// / <see cref="CapacitanceLossFaradsPerMeter"/> are then the values AT its reference
    /// frequency.</summary>
    public WidebandDebye? Dielectric { get; init; }

    /// <summary>The cross-section solved at several frequencies with every layer at its own
    /// ε(f); when present, <see cref="CapacitancePerMeter"/> and <see cref="ConductancePerMeter"/>
    /// come from it instead of from the first-order form.</summary>
    internal DielectricNodes? DielectricSolves { get; init; }

    /// <summary>The part of the skin resistance that belongs to the reference plane(s)
    /// [Ω/m/√Hz], N×N — set by the board model. A filament solve of the strips over a perfect
    /// plane (the proximity option) has to add it; the scalar model already contains it.</summary>
    public double[,]? PlaneSkinResistanceOhmsPerMeterPerSqrtHz { get; init; }

    /// <summary>The surface roughness the board model applied to the series impedance, and the
    /// conductivity its factor was evaluated with. A series impedance attached later (the
    /// proximity option) applies the same factor.</summary>
    public SurfaceRoughness? Roughness { get; init; }

    public double RoughnessConductivitySiemensPerMeter { get; init; }

    /// <summary>The capacitance matrix at f [F/m]: C′ + C″·Re ψ(f) with the dielectric model,
    /// the constant C′ without one.</summary>
    public double[,] CapacitancePerMeter(double frequencyHz)
    {
        if (DielectricSolves is { } nodes) return nodes.Capacitance(frequencyHz);
        if (Dielectric is null) return CapacitanceFaradsPerMeter;
        double shape = Dielectric.Shape(frequencyHz).Real;
        int n = ConductorCount;
        var c = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                c[i, j] = CapacitanceFaradsPerMeter[i, j] + shape * CapacitanceLossFaradsPerMeter[i, j];
        return c;
    }

    /// <summary>The conductance matrix at ω [S/m] (dielectric loss only): G(ω) = ω·C″ for the
    /// constant-ε solve, −ω·C″·Im ψ(f) with the dielectric model (the same number at its
    /// reference frequency).</summary>
    public double[,] ConductancePerMeter(double frequencyHz)
    {
        if (DielectricSolves is { } nodes) return nodes.Conductance(frequencyHz);
        double w = 2 * Math.PI * frequencyHz;
        if (Dielectric is not null) w *= -Dielectric.Shape(frequencyHz).Imaginary;
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
/// <para><b>Spectral kernel.</b> The image series is used for the single grounded slab
/// (the microstrip the wizard builds — its arithmetic is untouched). A section with
/// <see cref="CoupledLineCrossSection.TopGround"/> (a stripline) has no image series worth
/// truncating — two PEC planes reflect every image for ever — and a several-layer open
/// stack can defeat the truncation (the residual monopole does not close). For both, the
/// kernel is taken in the spectral domain instead, where it is one line: the line charge
/// sees the stack below and the stack above in parallel, G̃(k) = 1/(Y↓(k) + Y↑(k)), each Y
/// the electrostatic input admittance of its layers (ε·k·coth(kh) for one layer on a PEC,
/// k for the open half-space; the usual tanh recursion through several). The log singularity is removed in
/// closed form — G̃ → 1/(ε₀k(ε↓+ε↑)) at large k, and (1 − e^{−kd})/k transforms to
/// ½ln((x²+d²)/x²), which the SAME G₂ four-corner moments integrate exactly — and the
/// remainder, bounded at k = 0 and decaying like e^{−2k·h_min}, is integrated numerically
/// with the Galerkin panel integrals done analytically under the k integral
/// (∫e^{ikx}dx over a panel = w·sinc(kw/2)·e^{ikx_c}). Same panels, same solve.</para>
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
        var airLayers = section.Stackup.Layers
            .Select(l => new LayeredStackup.Layer(1.0, 0.0, l.ThicknessMeters)).ToArray();
        var airStackup = new LayeredStackup(airLayers);
        Complex[,] cComplex, cAirComplex;
        if (section.TopGround || section.Stackup.Layers.Count > 1)
        {
            cComplex = SolveCapacitance(panels, n, SpectralMoments(
                panels, section.Stackup, section.MetalInterface, section.TopGround));
            cAirComplex = SolveCapacitance(panels, n, SpectralMoments(
                panels, airStackup, section.MetalInterface, section.TopGround));
        }
        else
        {
            cComplex = SolveCapacitance(panels, n,
                ImageMoments(panels, StaticImages(section.Stackup, section.MetalInterface)));
            cAirComplex = SolveCapacitance(panels, n,
                ImageMoments(panels, StaticImages(airStackup, section.MetalInterface)));
        }

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
        if (section.TopGround)
            assumptions.Add("The conductors lie between TWO infinite reference planes (stripline): "
                + "the static kernel is the two-ground spectral Green's function over the "
                + "dielectric layers on each side; both planes are lossless returns.");
        return new RlgcResult(n, c, cLoss, cAir, inductance, rDc, rSkin, assumptions);
    }

    /// <summary>
    /// The extraction with a stated physical model on top of the kernel (see
    /// <see cref="RlgcModel"/>): trace thickness through an effective width, conductor loss
    /// with the return path and current crowding by the incremental-inductance rule and the
    /// internal inductance that goes with it, and a causal wideband dielectric.
    /// <see cref="RlgcModel.Kernel"/> returns exactly what the two-argument overload does.
    /// </summary>
    public static RlgcResult Extract(CoupledLineCrossSection section, RlgcModel model,
        int panelsPerTrace = 48)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.ThicknessCorrection && !model.SurfaceImpedance && !model.WidebandDielectric && !model.SideWalls)
            return Extract(section, panelsPerTrace);
        if (panelsPerTrace < 4)
            throw new ArgumentOutOfRangeException(nameof(panelsPerTrace),
                "At least 4 panels per trace are needed to resolve the edge charge.");

        int n = section.Traces.Count;
        var layers = section.Stackup.Layers;
        int metal = section.MetalInterface;
        bool spectral = section.TopGround || layers.Count > 1;
        bool limited = false;
        bool sideWalls = model.SideWalls;

        // The cross-section with every strip surface receded into the metal by `strip` and
        // every plane surface by `plane` (both zero for the geometry as drawn): the strips'
        // widths as the air solve and as the dielectric solve should see them, and the stack.
        (LayeredStackup Stack, TraceCrossSection[] Air, TraceCrossSection[] Dielectric)
            Geometry(double strip, double plane)
        {
            var thickness = layers.Select(l => l.ThicknessMeters).ToArray();
            thickness[metal] += strip;
            thickness[0] += plane;
            if (section.TopGround) thickness[^1] += plane;
            // A strip's top face recedes away from what is above it. A thick trace stands in a
            // zone of the dielectric above, as thick as the copper (the layer above is the
            // dielectric over the traces' top faces, as the board stackup and Wheeler's plane
            // spacing b = below + t + above have it); it recedes inside itself, so its base
            // rises with the interface and the zone loses what the layer below gains.
            if (metal + 1 < layers.Count && sideWalls)
                thickness[metal + 1] += section.Traces.Max(tr => tr.ThicknessMeters) - strip;
            else if (section.TopGround)
                thickness[metal + 1] += strip;
            var stack = new LayeredStackup(layers.Select((l, i) =>
                new LayeredStackup.Layer(l.RelativePermittivity, l.LossTangent, thickness[i])).ToArray());
            double below = 0, above = 0;
            for (int i = 0; i < thickness.Length; i++)
                if (i <= metal) below += thickness[i]; else above += thickness[i];

            var widenAir = new double[n];
            var widenDielectric = new double[n];
            for (int i = 0; i < n; i++)
            {
                var trace = section.Traces[i];
                double w = trace.WidthMeters - 2 * strip, t = trace.ThicknessMeters - 2 * strip;
                if (!model.ThicknessCorrection || sideWalls) continue;
                if (section.TopGround)
                    widenAir[i] = widenDielectric[i] = ThicknessCorrection.Stripline(w, t, below + above);
                else
                    (widenAir[i], widenDielectric[i]) = ThicknessCorrection.Microstrip(
                        w, t, below, layers[metal].RelativePermittivity);
            }
            // Widening must not close a gap: neighbours may take at most half of it between
            // them. Past that the effective width no longer stands for the side walls.
            for (int i = 0; i + 1 < n; i++)
            {
                var a = section.Traces[i];
                var b = section.Traces[i + 1];
                double gap = (b.CenterMeters - b.WidthMeters / 2) - (a.CenterMeters + a.WidthMeters / 2)
                    + 2 * strip;
                double taken = 0.5 * (widenAir[i] + widenAir[i + 1]);
                if (taken <= 0.5 * gap) continue;
                double scale = 0.5 * gap / taken;
                widenAir[i] *= scale; widenAir[i + 1] *= scale;
                widenDielectric[i] *= scale; widenDielectric[i + 1] *= scale;
                limited = true;
            }
            var air = new TraceCrossSection[n];
            var dielectric = new TraceCrossSection[n];
            if (sideWalls)
            {
                for (int i = 0; i < n; i++)
                {
                    var trace = section.Traces[i];
                    air[i] = dielectric[i] = trace with
                    {
                        WidthMeters = trace.WidthMeters - 2 * strip,
                        TopWidthMeters = trace.TopWidthMeters is { } top ? top - 2 * strip : null,
                        ThicknessMeters = trace.ThicknessMeters - 2 * strip,
                    };
                }
                return (stack, air, dielectric);
            }
            for (int i = 0; i < n; i++)
            {
                double w = section.Traces[i].WidthMeters - 2 * strip;
                air[i] = section.Traces[i] with { WidthMeters = w + widenAir[i] };
                dielectric[i] = section.Traces[i] with { WidthMeters = w + widenDielectric[i] };
            }
            return (stack, air, dielectric);
        }

        static LayeredStackup AirOf(LayeredStackup stack) => new(stack.Layers
            .Select(l => new LayeredStackup.Layer(1.0, 0.0, l.ThicknessMeters)).ToArray());

        Complex[,] Maxwell(LayeredStackup stack, IReadOnlyList<TraceCrossSection> traces)
        {
            if (sideWalls)
                return ThickConductorBem.Capacitance(traces, stack, metal, section.TopGround,
                    Math.Max(8, panelsPerTrace / 2));
            var panels = BuildPanels(traces, panelsPerTrace);
            return SolveCapacitance(panels, n, spectral
                ? SpectralMoments(panels, stack, metal, section.TopGround)
                : ImageMoments(panels, StaticImages(stack, metal)));
        }

        double[,] AirCapacitance(double strip, double plane)
        {
            var g = Geometry(strip, plane);
            var solved = Maxwell(AirOf(g.Stack), g.Air);
            var real = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) real[i, j] = solved[i, j].Real;
            return real;
        }

        // ---- C and L. Hammerstad–Jensen compose the thick line from three zero-thickness
        // ones: L from the air line at w + Δw₁, and C = C_d(w_r)·C_a(w₁)⁻¹·C_a(w_r) (their
        // ε_eff·[Z₀₁(w₁)/Z₀₁(w_r)]² in matrix form). Between two planes Δw₁ = Δw_r and the
        // product collapses to C_d.
        var drawn = Geometry(0, 0);
        var cAir = AirCapacitance(0, 0);
        var airInverse = Invert(cAir);
        var inductance = ScaleMatrix(airInverse, Mu0 * Epsilon0);
        bool twoWidths = false;
        for (int i = 0; i < n; i++)
            twoWidths |= drawn.Air[i].WidthMeters != drawn.Dielectric[i].WidthMeters;
        var airAtDielectricWidth = twoWidths ? Maxwell(AirOf(drawn.Stack), drawn.Dielectric) : null;
        Complex[,] DielectricCapacitance(LayeredStackup stack)
        {
            var solved = Maxwell(stack, drawn.Dielectric);
            if (airAtDielectricWidth is null) return solved;
            var composed = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < n; k++)
                        for (int m = 0; m < n; m++)
                            composed[i, j] += solved[i, k] * airInverse[k, m]
                                * airAtDielectricWidth[m, j].Real;
            var symmetric = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    symmetric[i, j] = 0.5 * (composed[i, j] + composed[j, i]);
            return symmetric;
        }
        var cComplex = DielectricCapacitance(drawn.Stack);

        // ---- The dielectric across the band: the cross-section solved again with every layer
        // at its own ε(f) (Djordjevic–Sarkar), at nodes a decade apart and at the reference.
        // The strips keep the effective widths of the reference ε (the thickness correction's
        // dielectric width moves with ε too, by about 0.2 % of C three decades away).
        DielectricNodes? dielectricNodes = null;
        if (model.WidebandDielectric && model.DielectricNodesPerDecade > 0
            && layers.Any(l => l.LossTangent > 0))
        {
            var debye = new WidebandDebye(model.DielectricReferenceHz);
            var frequencies = DielectricNodes.Frequencies(model.DielectricReferenceHz, model.DielectricNodesPerDecade);
            var solved = frequencies.Select(f => f == model.DielectricReferenceHz
                ? cComplex
                : DielectricCapacitance(new LayeredStackup(drawn.Stack.Layers
                    .Select(l => DielectricNodes.Dispersed(l, debye.Shape(f))).ToArray()))).ToArray();
            dielectricNodes = new DielectricNodes(debye, frequencies, solved);
        }

        var c = new double[n, n];
        var cLoss = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                c[i, j] = cComplex[i, j].Real;
                cLoss[i, j] = -cComplex[i, j].Imaginary;
            }

        // ---- Conductors.
        var rDc = new double[n];
        var rSkin = new double[n];
        double stripSigma = 0;
        for (int i = 0; i < n; i++)
        {
            var t = section.Traces[i];
            rDc[i] = 1.0 / (t.ConductivitySiemensPerMeter * t.WidthMeters * t.ThicknessMeters);
            rSkin[i] = Math.Sqrt(Math.PI * Mu0 / t.ConductivitySiemensPerMeter) / (2 * t.WidthMeters);
            stripSigma += t.ConductivitySiemensPerMeter / n;
        }

        double[,]? skinMatrix = null, planeSkin = null;
        if (model.SurfaceImpedance)
        {
            // Wheeler: R = (R_s/µ₀)·∂L/∂n, n the recession of the metal surfaces. Central
            // differences on the air solve, strips and planes separately (they may differ in
            // conductivity). The step is small against every dimension it changes.
            double hMin = Math.Min(layers[metal].ThicknessMeters,
                section.TopGround ? layers[metal + 1].ThicknessMeters : double.MaxValue);
            double step = Math.Min(section.Traces.Min(t => t.ThicknessMeters) / 20, hMin / 50);
            double[,] Derivative(double strip, double plane)
            {
                var up = ScaleMatrix(Invert(AirCapacitance(strip, plane)), Mu0 * Epsilon0);
                var down = ScaleMatrix(Invert(AirCapacitance(-strip, -plane)), Mu0 * Epsilon0);
                var d = new double[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        d[i, j] = (up[i, j] - down[i, j]) / (2 * step);
                return d;
            }
            var planeDerivative = Derivative(0, step);
            double planeScale = Math.Sqrt(Math.PI * Mu0 / model.PlaneConductivitySiemensPerMeter) / Mu0;
            double stripScale = Math.Sqrt(Math.PI * Mu0 / stripSigma) / Mu0;
            planeSkin = new double[n, n];
            skinMatrix = new double[n, n];
            // Without the thickness correction the strip is a zero-thickness sheet, for which
            // the rule has no finite answer (the edge current is not square-integrable); the
            // kernel's uniform two-sided strip term is kept there and only the plane is added.
            var stripDerivative = model.ThicknessCorrection || sideWalls ? Derivative(step, 0) : null;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    planeSkin[i, j] = planeScale * 0.5 * (planeDerivative[i, j] + planeDerivative[j, i]);
                    double strip = stripDerivative is null
                        ? (i == j ? rSkin[i] : 0)
                        : stripScale * 0.5 * (stripDerivative[i, j] + stripDerivative[j, i]);
                    skinMatrix[i, j] = strip + planeSkin[i, j];
                }
            for (int i = 0; i < n; i++) rSkin[i] = skinMatrix[i, i];
        }

        var assumptions = new List<string>
        {
            "Quasi-TEM per-unit-length model: C from the 2D layered electrostatic BEM, "
                + "L = µ₀ε₀·C_air⁻¹ (exact in the TEM limit).",
            sideWalls
                ? "Traces are solved as the trapezoids they are: base, top and side walls carry charge, "
                  + "so the coupling between the side walls of close traces is in the field solve."
                : model.ThicknessCorrection
                ? "Trace thickness enters C and L through an effective width ("
                  + (section.TopGround ? "Wheeler's, for a strip between two planes"
                                       : "Hammerstad–Jensen's, for a strip over one plane")
                  + "), not through side-wall charge: edge-to-edge coupling of thick, closely "
                  + "spaced traces is approximate."
                  + (limited ? " The widening was LIMITED to half of a gap here — the traces are "
                               + "thicker than the correction is meant for, and the coupling "
                               + "between them is under-stated." : "")
                : "Traces are zero-thickness strips for C/L (w ≫ t).",
            model.SurfaceImpedance
                ? "Conductor loss by the incremental-inductance rule on this extraction's own L: "
                  + "strips (both faces and edges) and reference plane"
                  + (section.TopGround ? "s" : "") + " together, as a full N×N R(f) with its internal "
                  + "inductance, joined to R_dc by Z = √(R_dc² + 2jK²f). "
                  + (model.Roughness is { } rough
                      ? $"Surface roughness: {rough.Describe()}, one value on every metal surface, "
                        + "as a real multiplier on the frequency-dependent part of the series impedance."
                      : "Smooth copper: surface roughness is NOT modelled and adds loss above a few GHz.")
                : "R = max(R_dc, R_s(f)/2w) per conductor: forward resistance only, the return "
                  + "plane is lossless.",
            model.WidebandDielectric
                ? "Dielectric by the Djordjevic–Sarkar wideband Debye model, with the stackup's εr "
                  + $"and tan δ taken as their values at {model.DielectricReferenceHz / 1e9:g3} GHz; "
                  + (dielectricNodes is not null
                      ? $"the section is solved again with every layer at its own ε(f) at {dielectricNodes.FrequenciesHz.Count} frequencies "
                        + $"from {DielectricNodes.LowestHz / 1e6:g3} MHz to {DielectricNodes.HighestHz / 1e9:g3} GHz, and C(f), G(f) follow those solves."
                      : "every lossy layer is given the same dispersion shape, to first order in tan δ.")
                : "G = ω·C″ with a frequency-independent complex ε (not causal).",
            "All conductors are coplanar at one stackup interface; broadside coupling "
                + "across layers is out of scope by construction.",
        };
        if (section.TopGround)
            assumptions.Add("The conductors lie between TWO infinite reference planes (stripline): "
                + "the static kernel is the two-ground spectral Green's function over the "
                + "dielectric layers on each side.");
        foreach (var trace in section.Traces)
            if (model.ThicknessCorrection && !sideWalls && trace.ThicknessMeters > 0.5 * trace.WidthMeters)
            {
                assumptions.Add($"A trace is {trace.ThicknessMeters / trace.WidthMeters:g2}× as thick "
                    + "as it is wide; the effective-width correction is meant for t well below w.");
                break;
            }

        var result = new RlgcResult(n, c, cLoss, cAir, inductance, rDc, rSkin, assumptions)
        {
            Dielectric = model.WidebandDielectric
                ? new WidebandDebye(model.DielectricReferenceHz) : null,
            DielectricSolves = dielectricNodes,
            PlaneSkinResistanceOhmsPerMeterPerSqrtHz = planeSkin,
        };
        if (skinMatrix is null) return result;
        var conductors = new ConductorImpedance(rDc, skinMatrix);
        if (model.Roughness is not { } roughness)
            return result with
            {
                ResistanceMatrixOhmsPerMeter = conductors.Resistance,
                InternalInductanceHenriesPerMeter = conductors.InternalInductance,
            };

        // Rough copper: the part of the series impedance that depends on frequency is the
        // surface's, and that part is multiplied.
        double sigma = stripSigma;
        return result with
        {
            Roughness = roughness,
            RoughnessConductivitySiemensPerMeter = sigma,
            ResistanceMatrixOhmsPerMeter = f =>
            {
                var r = conductors.Resistance(f);
                double k = roughness.Factor(f, sigma);
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double dc = i == j ? rDc[i] : 0;
                        r[i, j] = dc + k * (r[i, j] - dc);
                    }
                return r;
            },
            InternalInductanceHenriesPerMeter = f =>
            {
                var l = conductors.InternalInductance(f);
                double k = roughness.Factor(f, sigma);
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) l[i, j] *= k;
                return l;
            },
        };
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

    /// <summary>The Galerkin potential matrix from the image series (open-top stacks).</summary>
    private static ComplexDenseMatrix ImageMoments(List<Panel> panels,
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
        return matrix;
    }

    /// <summary>Decay exponent at which the two-ground remainder integral is cut: the
    /// integrand falls like e^{−2k·h_min}, so 2k·h_min = 36 leaves e^{−36} ≈ 2e−16.</summary>
    private const double SpectralCutExponent = 36;

    /// <summary>Gauss–Legendre points per π of the fastest oscillation k·(section span).</summary>
    private const int SpectralPointsPerPanel = 6;

    /// <summary>
    /// The Galerkin potential matrix from the spectral kernel — see the class remarks.
    /// <paramref name="stackup"/> lists the layers ground-up; those up to
    /// <paramref name="metalInterface"/> lie below the conductors and end on the PEC
    /// ground, the rest lie above and end on a second PEC (<paramref name="topGround"/>)
    /// or on the open half-space. Deterministic at any degree of parallelism: every entry
    /// is its own sequential sum over the k nodes.
    /// </summary>
    private static ComplexDenseMatrix SpectralMoments(List<Panel> panels,
        LayeredStackup stackup, int metalInterface, bool topGround)
    {
        int m = panels.Count;
        var layers = stackup.Layers;
        var below = layers[metalInterface];
        var above = metalInterface + 1 < layers.Count ? layers[metalInterface + 1] : null;
        Complex epsSum = below.ComplexPermittivity + (above?.ComplexPermittivity ?? Complex.One);
        // The remainder decays like e^{−2k·h} of the thinnest layer touching the metal
        // (an open half-space directly above contributes none).
        double hMin = Math.Min(below.ThicknessMeters, above?.ThicknessMeters ?? double.MaxValue);
        double d = 2 * hMin;                                      // regularizing depth

        // k nodes: [0, kMax] in panels no longer than π / span (and no fewer than 48).
        double span = panels.Max(p => p.End) - panels.Min(p => p.Start);
        double kMax = SpectralCutExponent / (2 * hMin);
        int kPanels = Math.Max(48, (int)Math.Ceiling(kMax * span / Math.PI));
        var (unitNodes, unitWeights) = GaussLegendre.Rule(SpectralPointsPerPanel, 0, 1);
        int nk = kPanels * SpectralPointsPerPanel;
        var weightRe = new double[nk];
        var weightIm = new double[nk];
        var kNodes = new double[nk];
        double dk = kMax / kPanels;
        for (int q = 0; q < kPanels; q++)
            for (int g = 0; g < SpectralPointsPerPanel; g++)
            {
                int j = q * SpectralPointsPerPanel + g;
                double k = (q + unitNodes[g]) * dk;
                kNodes[j] = k;
                // Remainder D(k) = G̃(k) − (1 − e^{−kd}) / (ε₀·k·(ε↓+ε↑)), in 1/ε₀ units.
                Complex green = 1.0 / (AdmittanceDown(layers, metalInterface, k)
                                       + AdmittanceUp(layers, metalInterface, k, topGround));
                double x = k * d;                                 // 1 − e^{−x} without cancellation
                double oneMinusExp = x < 1e-5 ? x * (1 - x / 2 + x * x / 6) : 1 - Math.Exp(-x);
                Complex remainder = green - oneMinusExp / k / epsSum;
                Complex weighted = remainder * (unitWeights[g] * dk / (Math.PI * Epsilon0));
                weightRe[j] = weighted.Real;
                weightIm[j] = weighted.Imaginary;
            }

        // Panel transforms A_a(k) = w·sinc(kw/2)·e^{ik·x_c}, as cos/sin rows over k.
        var cos = new double[m][];
        var sin = new double[m][];
        Parallel.For(0, m, a =>
        {
            double width = panels[a].Width, center = 0.5 * (panels[a].Start + panels[a].End);
            var c = new double[nk];
            var s = new double[nk];
            for (int j = 0; j < nk; j++)
            {
                double half = 0.5 * kNodes[j] * width;
                double amplitude = width * (half < 1e-8 ? 1 - half * half / 6 : Math.Sin(half) / half);
                c[j] = amplitude * Math.Cos(kNodes[j] * center);
                s[j] = amplitude * Math.Sin(kNodes[j] * center);
            }
            cos[a] = c;
            sin[a] = s;
        });

        var matrix = new ComplexDenseMatrix(m, m);
        var rows = new Complex[m][];
        Complex singularScale = 1.0 / (Math.PI * Epsilon0 * epsSum);
        Parallel.For(0, m, a =>
        {
            var row = new Complex[m];
            double[] ca = cos[a], sa = sin[a];
            for (int b = a; b < m; b++)
            {
                double[] cb = cos[b], sb = sin[b];
                double re = 0, im = 0;
                for (int j = 0; j < nk; j++)
                {
                    double t = ca[j] * cb[j] + sa[j] * sb[j];     // Re(A_a·conj(A_b))
                    re += weightRe[j] * t;
                    im += weightIm[j] * t;
                }
                row[b] = new Complex(re, im)
                    + singularScale * (LogMoment(panels[a], panels[b], d)
                                       - LogMoment(panels[a], panels[b], 0));
            }
            rows[a] = row;
        });
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                matrix[a, b] = rows[a][b];
                matrix[b, a] = rows[a][b];
            }
        return matrix;
    }

    /// <summary>
    /// Electrostatic input admittance (per ε₀) at spatial frequency k, looking DOWN from
    /// interface <paramref name="metalInterface"/>: ε·k·coth(kh) for layer 0 on the PEC,
    /// then Y ← ε·k·(Y + ε·k·tanh(kh)) / (ε·k + Y·tanh(kh)) through each layer up to the metal.
    /// </summary>
    internal static Complex AdmittanceDown(IReadOnlyList<LayeredStackup.Layer> layers,
        int metalInterface, double k)
    {
        Complex y = layers[0].ComplexPermittivity * k / Math.Tanh(k * layers[0].ThicknessMeters);
        for (int i = 1; i <= metalInterface; i++)
            y = Through(layers[i], y, k);
        return y;
    }

    /// <summary>The same looking UP: the layers above the metal, ending on a PEC
    /// (<paramref name="topGround"/>) or on the open half-space, whose admittance is k.</summary>
    internal static Complex AdmittanceUp(IReadOnlyList<LayeredStackup.Layer> layers,
        int metalInterface, double k, bool topGround)
    {
        int i = layers.Count - 1;
        Complex y;
        if (topGround)
        {
            y = layers[i].ComplexPermittivity * k / Math.Tanh(k * layers[i].ThicknessMeters);
            i--;
        }
        else
        {
            y = k;
        }
        for (; i > metalInterface; i--)
            y = Through(layers[i], y, k);
        return y;
    }

    private static Complex Through(LayeredStackup.Layer layer, Complex load, double k)
    {
        Complex ek = layer.ComplexPermittivity * k;
        double t = Math.Tanh(k * layer.ThicknessMeters);
        return ek * (load + ek * t) / (ek + load * t);
    }

    /// <summary>The Maxwell capacitance matrix: Galerkin BEM with unit-potential drives.
    /// One factorization serves all N right-hand sides.</summary>
    private static Complex[,] SolveCapacitance(List<Panel> panels, int conductors,
        ComplexDenseMatrix matrix)
    {
        int m = panels.Count;
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
