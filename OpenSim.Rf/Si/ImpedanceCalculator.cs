using System.Globalization;
using System.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>Where the trace sits in the stackup.</summary>
public enum LineStructure
{
    /// <summary>On the outer layer: dielectric below to the plane, air above.</summary>
    Microstrip,

    /// <summary>One plane below, and a dielectric layer over the trace (solder mask, or the
    /// prepreg of a buried layer with no plane above), then air.</summary>
    EmbeddedMicrostrip,

    /// <summary>Between two planes; the two dielectrics may differ in thickness and εr.</summary>
    Stripline
}

/// <summary>
/// A controlled-impedance line as a designer specifies it. Lengths in metres.
/// </summary>
public sealed record LineSpec
{
    public LineStructure Structure { get; init; } = LineStructure.Microstrip;

    /// <summary>Trace width at its base (the face on the laminate it was etched on).</summary>
    public required double WidthMeters { get; init; }

    /// <summary>Trace width at the opposite face, for an etched (trapezoidal) trace. Null is a
    /// rectangular trace.</summary>
    public double? TopWidthMeters { get; init; }

    public double ThicknessMeters { get; init; } = 35e-6;
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Edge-to-edge gap (at the base) of an edge-coupled pair. Null is a single trace.</summary>
    public double? PairGapMeters { get; init; }

    /// <summary>Base width of the pair's second trace, for an asymmetric pair. Null (or the
    /// first trace's width) is a symmetric pair. An etched second trace loses the same width
    /// from base to top as the first.</summary>
    public double? SecondWidthMeters { get; init; }

    /// <summary>Edge-to-edge gap (at the base) to a coplanar ground on each side. Null is no
    /// coplanar ground.</summary>
    public double? CoplanarGapMeters { get; init; }

    /// <summary>Width of each coplanar ground strip. 0 picks a width beyond which it no longer
    /// matters at the accuracy of the solve.</summary>
    public double CoplanarGroundWidthMeters { get; init; }

    /// <summary>Dielectric between the trace and the plane below.</summary>
    public required double HeightMeters { get; init; }
    public double RelativePermittivity { get; init; } = 4.4;
    public double LossTangent { get; init; } = 0.02;

    /// <summary>The layer over the trace: the cover of an embedded microstrip, or the
    /// dielectric up to the upper plane of a stripline. Unused by a microstrip.</summary>
    public double UpperHeightMeters { get; init; }
    public double UpperRelativePermittivity { get; init; } = 4.4;
    public double UpperLossTangent { get; init; } = 0.02;

    /// <summary>What the solve models; <see cref="RlgcModel.Board"/> by default, with
    /// roughness added through <see cref="RlgcModel.Roughness"/>.</summary>
    public RlgcModel Model { get; init; } = RlgcModel.Board;

    /// <summary>The frequency the impedance, delay and loss are quoted at.</summary>
    public double FrequencyHz { get; init; } = 1e9;
}

/// <summary>One propagating mode of the line at the report frequency.</summary>
/// <param name="Name">"single", "odd" or "even".</param>
/// <param name="ImpedanceOhms">Real part of the mode's characteristic impedance.</param>
/// <param name="EffectivePermittivity">(β·c/ω)².</param>
/// <param name="DelaySecondsPerMeter">β/ω.</param>
/// <param name="ConductorLossDbPerMeter">Attenuation with the dielectric loss removed.</param>
/// <param name="DielectricLossDbPerMeter">Attenuation with the conductor loss removed.</param>
public sealed record LineMode(string Name, double ImpedanceOhms, double EffectivePermittivity,
    double DelaySecondsPerMeter, double ConductorLossDbPerMeter, double DielectricLossDbPerMeter,
    double ResistanceOhmsPerMeter, double InductanceHenriesPerMeter,
    double CapacitanceFaradsPerMeter, double ConductanceSiemensPerMeter)
{
    public double LossDbPerMeter => ConductorLossDbPerMeter + DielectricLossDbPerMeter;
}

/// <summary>What the calculator returns for a <see cref="LineSpec"/>.</summary>
public sealed record LineReport
{
    public required LineSpec Spec { get; init; }

    /// <summary>The cross-section that was solved, coplanar grounds included.</summary>
    public required CoupledLineCrossSection Section { get; init; }

    /// <summary>Per-unit-length matrices of the signal conductors (1×1 or 2×2).</summary>
    public required RlgcResult Rlgc { get; init; }

    /// <summary>One mode for a single trace; odd then even for a pair.</summary>
    public required IReadOnlyList<LineMode> Modes { get; init; }

    public bool IsPair => Modes.Count == 2;

    /// <summary>Real part of the characteristic impedance matrix Z_c of an ASYMMETRIC pair (the
    /// forward wave's V = Z_c·I); null for a single trace or a symmetric pair, where the modes
    /// say everything.</summary>
    public double[,]? CharacteristicImpedanceOhms { get; init; }

    /// <summary>Z0 of a single trace; for a pair, the first trace's impedance with the other
    /// terminated (Z_c11; = (Z_odd + Z_even)/2 for a symmetric pair).</summary>
    public double ImpedanceOhms => CharacteristicImpedanceOhms is { } z ? z[0, 0]
        : IsPair ? 0.5 * (Modes[0].ImpedanceOhms + Modes[1].ImpedanceOhms) : Modes[0].ImpedanceOhms;

    /// <summary>V_diff/I for a forward wave carrying equal and opposite currents:
    /// Z_c11 + Z_c22 − 2·Z_c12 (= 2·Z_odd for a symmetric pair). Null for a single trace.</summary>
    public double? DifferentialOhms => CharacteristicImpedanceOhms is { } z ? z[0, 0] + z[1, 1] - z[0, 1] - z[1, 0]
        : IsPair ? 2 * Modes[0].ImpedanceOhms : null;

    /// <summary>The voltage over the total current for a forward wave with both traces at the same
    /// voltage (driven together): 1/Σ(Z_c⁻¹)ᵢⱼ (= Z_even/2 for a symmetric pair; the two lines in
    /// parallel when they are far apart). Null for a single trace.</summary>
    public double? CommonOhms => CharacteristicImpedanceOhms is { } z ? 1 / SumOfInverse(z)
        : IsPair ? Modes[1].ImpedanceOhms / 2 : null;

    /// <summary>Σᵢⱼ (Z⁻¹)ᵢⱼ of a 2×2 matrix: the total current per volt with both traces at it.</summary>
    private static double SumOfInverse(double[,] z) =>
        (z[0, 0] + z[1, 1] - z[0, 1] - z[1, 0]) / (z[0, 0] * z[1, 1] - z[0, 1] * z[1, 0]);

    /// <summary>Saturated backward (near-end) crosstalk between the two traces, as a fraction
    /// of the aggressor's step: ¼(C_m/C + L_m/L). Null for a single trace.</summary>
    public double? NearEndCoupling { get; init; }

    /// <summary>Forward (far-end) crosstalk coefficient [s/m]: the far-end pulse is this times
    /// the coupled length times the aggressor's dV/dt, ½(C_m·Z0 − L_m/Z0). Zero in a
    /// homogeneous dielectric. Null for a single trace.</summary>
    public double? FarEndCouplingSecondsPerMeter { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }

    public IReadOnlyList<string> Describe()
    {
        static string F(double v, string format = "g4") => v.ToString(format, CultureInfo.InvariantCulture);
        var lines = new List<string>();
        double ghz = Spec.FrequencyHz / 1e9;
        if (CharacteristicImpedanceOhms is { } zc)
        {
            lines.Add($"Z_diff = {F(DifferentialOhms!.Value)} Ω, Z_common = {F(CommonOhms!.Value)} Ω at {F(ghz)} GHz " +
                      $"(asymmetric pair, Z_c11 {F(zc[0, 0])} Ω, Z_c22 {F(zc[1, 1])} Ω, Z_c12 {F(zc[0, 1])} Ω; " +
                      "no even and odd mode exists, and each mode drives the two traces unequally)");
        }
        else if (IsPair)
        {
            lines.Add($"Z_diff = {F(DifferentialOhms!.Value)} Ω, Z_common = {F(CommonOhms!.Value)} Ω " +
                      $"(Z_odd {F(Modes[0].ImpedanceOhms)} Ω, Z_even {F(Modes[1].ImpedanceOhms)} Ω; " +
                      $"one trace alone {F(ImpedanceOhms)} Ω) at {F(ghz)} GHz");
        }
        else
        {
            lines.Add($"Z0 = {F(ImpedanceOhms)} Ω at {F(ghz)} GHz");
        }
        foreach (var mode in Modes)
        {
            string name = mode.Name switch
            {
                "odd" => "Differential (odd) mode",
                "even" => "Common (even) mode",
                "pi" => "π mode (traces in opposite phase)",
                "c" => "c mode (traces in phase)",
                _ => "Line"
            };
            lines.Add($"{name}: ε_eff {F(mode.EffectivePermittivity)}, delay {F(mode.DelaySecondsPerMeter * 1e9)} ns/m " +
                      $"({F(mode.DelaySecondsPerMeter * 1e12 * 0.0254)} ps/in), loss {F(mode.LossDbPerMeter)} dB/m = " +
                      $"conductor {F(mode.ConductorLossDbPerMeter)} + dielectric {F(mode.DielectricLossDbPerMeter)} " +
                      $"({F(mode.LossDbPerMeter * 0.0254)} dB/in)");
        }
        if (NearEndCoupling is { } near && FarEndCouplingSecondsPerMeter is { } far)
            lines.Add($"Between the two traces: near-end crosstalk {F(near * 100, "g3")} % of the step (saturated), " +
                      $"far-end {F(far * 1e12 * 1e-3, "g3")} ps per mm of run (times the edge's V/ps).");
        return lines;
    }
}

/// <summary>
/// The stackup and impedance calculator: a <see cref="LineSpec"/> in, the same 2D field solve
/// the board extraction uses (<see cref="RlgcExtractor"/> with an <see cref="RlgcModel"/>), and
/// the numbers a fabricator's stackup note is written in out.
/// <para>
/// Coplanar grounds are solved as conductors of their own, finite in width, and then tied to
/// the reference (<see cref="RlgcReduction"/>); nothing about them is taken from a formula.
/// </para>
/// </summary>
public static class ImpedanceCalculator
{
    private const double C0 = 299_792_458.0;
    private const double NeperToDb = 8.685889638065035;

    public static LineReport Solve(LineSpec spec, int panelsPerTrace = 48)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(spec.WidthMeters > 0)) throw new ArgumentException("The trace needs a positive width.");
        if (!(spec.HeightMeters > 0)) throw new ArgumentException("The dielectric height to the plane must be positive.");
        if (!(spec.ThicknessMeters > 0)) throw new ArgumentException("The copper thickness must be positive.");
        if (!(spec.FrequencyHz > 0)) throw new ArgumentException("The report frequency must be positive.");
        if (spec.TopWidthMeters is { } top && !(top > 0))
            throw new ArgumentException("The top width of an etched trace must be positive.");
        if (spec.PairGapMeters is { } pg && !(pg > 0)) throw new ArgumentException("The pair gap must be positive.");
        if (spec.CoplanarGapMeters is { } cg && !(cg > 0)) throw new ArgumentException("The coplanar gap must be positive.");
        if (spec.Structure != LineStructure.Microstrip && !(spec.UpperHeightMeters > 0))
            throw new ArgumentException(spec.Structure == LineStructure.Stripline
                ? "A stripline needs the dielectric height up to the upper plane."
                : "An embedded microstrip needs the thickness of the layer over the trace.");

        var assumptions = new List<string>();

        // An etched trace: the solve takes a rectangle of the mean width. The base is drawn
        // at the nominal width and the gaps at the base, so the mean-width strip sits centred
        // on the drawn one and every gap grows by the difference.
        double baseWidth = spec.WidthMeters;
        double width = 0.5 * (baseWidth + (spec.TopWidthMeters ?? baseWidth));
        double shrink = baseWidth - width;                    // total, both edges together
        if (spec.TopWidthMeters is { } t && t != baseWidth)
            assumptions.Add(spec.Model.SideWalls
                ? $"Etched trace ({baseWidth * 1e6:g4} µm at the base, {t * 1e6:g4} µm at the top): solved as that trapezoid."
                : $"Etched trace ({baseWidth * 1e6:g4} µm at the base, {t * 1e6:g4} µm at the top): solved as a "
                  + $"rectangle of the mean width {width * 1e6:g4} µm with the same copper area. The slope of "
                  + "the side walls is not in the field solve.");

        var layers = new List<LayeredStackup.Layer>
        {
            new(spec.RelativePermittivity, spec.LossTangent, spec.HeightMeters)
        };
        if (spec.Structure != LineStructure.Microstrip)
        {
            // A cover is measured from the trace's base; the side-wall model takes the layer above
            // from the traces' top faces, so it is given what lies over them.
            double upper = spec.UpperHeightMeters;
            if (spec.Model.SideWalls && spec.Structure == LineStructure.EmbeddedMicrostrip)
            {
                upper -= spec.ThicknessMeters;
                if (!(upper > 0))
                    throw new ArgumentException("The cover is no thicker than the trace; a conformal cover over the copper is not modelled.");
            }
            layers.Add(new(spec.UpperRelativePermittivity, spec.UpperLossTangent, upper));
        }
        bool topGround = spec.Structure == LineStructure.Stripline;
        if (spec.Structure == LineStructure.EmbeddedMicrostrip)
            assumptions.Add("The layer over the trace is flat and of the given thickness measured from the "
                + "trace's base; a conformal solder mask that follows the copper is not modelled.");

        // Signal traces: a single trace or a symmetric pair centred on x = 0; an asymmetric
        // pair with its gap centred on x = 0. lowEdge/highEdge are the outer base edges.
        var traces = new List<TraceCrossSection>();
        double lowEdge, highEdge;
        bool asymmetric = spec.PairGapMeters is not null && spec.SecondWidthMeters is { } second && second != baseWidth;
        if (spec.SecondWidthMeters is { } w2 && !(w2 - shrink > 0))
            throw new ArgumentException("The second trace needs a positive width (after the etch).");
        if (asymmetric)
        {
            double gap = spec.PairGapMeters!.Value, secondBase = spec.SecondWidthMeters!.Value;
            traces.Add(new(-gap / 2 - baseWidth / 2, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            traces.Add(new(gap / 2 + secondBase / 2, secondBase - shrink, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            lowEdge = -gap / 2 - baseWidth;
            highEdge = gap / 2 + secondBase;
        }
        else if (spec.PairGapMeters is { } gap)
        {
            double pitch = baseWidth + gap;
            traces.Add(new(-pitch / 2, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            traces.Add(new(pitch / 2, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            highEdge = pitch / 2 + baseWidth / 2;
            lowEdge = -highEdge;
        }
        else
        {
            traces.Add(new(0, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            highEdge = baseWidth / 2;
            lowEdge = -highEdge;
        }
        int signals = traces.Count;
        if (spec.Model.SideWalls && shrink != 0)
            // The side-wall model solves the trapezoid itself: base as drawn, top narrower.
            for (int i = 0; i < signals; i++)
                traces[i] = traces[i] with
                {
                    WidthMeters = traces[i].WidthMeters + shrink, TopWidthMeters = traces[i].WidthMeters - shrink
                };

        if (spec.CoplanarGapMeters is { } coplanarGap)
        {
            double tallest = Math.Max(spec.HeightMeters, topGround ? spec.UpperHeightMeters : 0);
            double groundWidth = spec.CoplanarGroundWidthMeters > 0
                ? spec.CoplanarGroundWidthMeters
                : Math.Max(6 * tallest, 4 * (highEdge - lowEdge + 2 * coplanarGap));
            // The grounds are etched like the traces: their near edges recede by the same amount.
            double g = groundWidth - shrink / 2;
            traces.Add(new(lowEdge - coplanarGap - groundWidth / 2 - shrink / 4, g,
                spec.ThicknessMeters, spec.ConductivitySiemensPerMeter) { IsGround = true });
            traces.Add(new(highEdge + coplanarGap + groundWidth / 2 + shrink / 4, g,
                spec.ThicknessMeters, spec.ConductivitySiemensPerMeter) { IsGround = true });
            assumptions.Add($"Coplanar grounds: two strips {groundWidth * 1e3:g3} mm wide, {coplanarGap * 1e6:g4} µm from "
                + "the trace, solved as conductors and tied to the plane (stitched along their length).");
            if (spec.Model.SideWalls && shrink != 0)
            {
                // Trapezoids at the drawn base, each wall receding by the etch (a far wall too).
                traces[^2] = traces[^2] with { CenterMeters = lowEdge - coplanarGap - groundWidth / 2, WidthMeters = groundWidth, TopWidthMeters = groundWidth - 2 * shrink };
                traces[^1] = traces[^1] with { CenterMeters = highEdge + coplanarGap + groundWidth / 2, WidthMeters = groundWidth, TopWidthMeters = groundWidth - 2 * shrink };
            }
        }

        var section = new CoupledLineCrossSection(new LayeredStackup(layers), 0, traces, topGround);
        var full = RlgcExtractor.Extract(section, spec.Model, panelsPerTrace);
        var rlgc = RlgcReduction.GroundConductors(full, section.GroundIndices);

        double f = spec.FrequencyHz, w = 2 * Math.PI * f;
        var r = rlgc.ResistanceMatrixOhmsPerMeter?.Invoke(f);
        var lInternal = rlgc.InternalInductanceHenriesPerMeter?.Invoke(f);
        var c = rlgc.CapacitancePerMeter(f);
        var g2 = rlgc.ConductancePerMeter(f);
        double R(int i, int j) => r is not null ? r[i, j] : i == j ? rlgc.ResistancePerMeter(i, f) : 0;
        double L(int i, int j) => rlgc.InductanceHenriesPerMeter[i, j] + (lInternal is not null ? lInternal[i, j] : 0);

        LineMode Mode(string name, double rm, double lm, double cm, double gm)
        {
            var series = new Complex(rm, w * lm);
            var shunt = new Complex(gm, w * cm);
            var z = Complex.Sqrt(series / shunt);
            var gamma = Complex.Sqrt(series * shunt);
            double beta = gamma.Imaginary;
            double conductor = Complex.Sqrt(series * new Complex(0, w * cm)).Real;
            double dielectric = Complex.Sqrt(new Complex(0, w * lm) * shunt).Real;
            return new LineMode(name, z.Real, Math.Pow(beta * C0 / w, 2), beta / w,
                conductor * NeperToDb, dielectric * NeperToDb, rm, lm, cm, gm);
        }

        var modes = new List<LineMode>();
        double? nearEnd = null, farEnd = null;
        double[,]? characteristic = null;
        if (signals == 1)
        {
            modes.Add(Mode("single", R(0, 0), L(0, 0), c[0, 0], g2[0, 0]));
        }
        else if (asymmetric)
        {
            var series = new Complex[2, 2];
            var shunt = new Complex[2, 2];
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    series[i, j] = new Complex(R(i, j), w * L(i, j));
                    shunt[i, j] = new Complex(g2[i, j], w * c[i, j]);
                }
            var (pi, cMode, zc) = AsymmetricModes(series, shunt, w);
            modes.Add(pi);
            modes.Add(cMode);
            characteristic = zc;

            // Coupling coefficients with the two traces' own L and C as geometric means
            // (they reduce to the symmetric pair's when the traces are alike).
            double cOwn = Math.Sqrt(c[0, 0] * c[1, 1]), cMutual = -0.5 * (c[0, 1] + c[1, 0]);
            var lExt = rlgc.InductanceHenriesPerMeter;
            double lOwn = Math.Sqrt(lExt[0, 0] * lExt[1, 1]), lMutual = 0.5 * (lExt[0, 1] + lExt[1, 0]);
            double z0 = Math.Sqrt(lOwn / cOwn);
            nearEnd = 0.25 * (cMutual / cOwn + lMutual / lOwn);
            farEnd = 0.5 * (cMutual * z0 - lMutual / z0);
            assumptions.Add("Asymmetric pair: Z_diff is that of a forward wave with equal and opposite currents "
                + "on the two traces (a floating source), Z_common that of one with both traces at the same "
                + "voltage, both from the characteristic impedance matrix; "
                + "the π and c modes are the line's own modes, and their impedance is quoted for a voltage "
                + "vector of unit length. The crosstalk coefficients use the geometric mean of the two traces' "
                + "own L and C.");
        }
        else
        {
            // A symmetric pair: the modes are the sum and the difference.
            double Own(Func<int, int, double> m) => 0.5 * (m(0, 0) + m(1, 1));
            double Mutual(Func<int, int, double> m) => 0.5 * (m(0, 1) + m(1, 0));
            double C(int i, int j) => c[i, j];
            double G(int i, int j) => g2[i, j];
            modes.Add(Mode("odd", Own(R) - Mutual(R), Own(L) - Mutual(L), Own(C) - Mutual(C), Own(G) - Mutual(G)));
            modes.Add(Mode("even", Own(R) + Mutual(R), Own(L) + Mutual(L), Own(C) + Mutual(C), Own(G) + Mutual(G)));

            double cOwn = Own(C), cMutual = -Mutual(C);
            double lOwn = rlgc.InductanceHenriesPerMeter[0, 0], lMutual = rlgc.InductanceHenriesPerMeter[0, 1];
            double z0 = Math.Sqrt(lOwn / cOwn);
            nearEnd = 0.25 * (cMutual / cOwn + lMutual / lOwn);
            farEnd = 0.5 * (cMutual * z0 - lMutual / z0);
        }

        assumptions.AddRange(rlgc.Assumptions);
        return new LineReport
        {
            Spec = spec,
            Section = section,
            Rlgc = rlgc,
            Modes = modes,
            CharacteristicImpedanceOhms = characteristic,
            NearEndCoupling = nearEnd,
            FarEndCouplingSecondsPerMeter = farEnd,
            Assumptions = assumptions
        };
    }

    /// <summary>
    /// The two modes of a pair of unlike traces and its characteristic impedance matrix, from
    /// the per-unit-length series impedance Z and shunt admittance Y at one frequency.
    /// <para>
    /// The voltage modes are the eigenvectors of Z·Y, with γ² its eigenvalues. Z·Y is 2×2, so
    /// its square root is (Z·Y + γ₁γ₂·I)/(γ₁ + γ₂) (the Cayley–Hamilton form, valid when the
    /// two γ are equal too), and the forward wave has V = Z_c·I with Z_c = (Z·Y)^−½·Z.
    /// </para>
    /// <para>
    /// Each mode's own z and y are the projections iᵀ·Z·i and vᵀ·Y·v of its current and voltage
    /// vectors, normalised to vᵀ·v = 1 and iᵀ·v = 1; for a symmetric pair that is
    /// Z₁₁ ∓ Z₁₂, exactly what the even/odd split gives. The mode with opposite-signed
    /// voltages is the π mode, the other the c mode.
    /// </para>
    /// </summary>
    internal static (LineMode Pi, LineMode C, double[,] Characteristic) AsymmetricModes(
        Complex[,] z, Complex[,] y, double w)
    {
        var m = Multiply(z, y);
        Complex trace = m[0, 0] + m[1, 1], det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        Complex root = Complex.Sqrt(trace * trace / 4 - det);
        var lambdas = new[] { trace / 2 + root, trace / 2 - root };
        var gammas = lambdas.Select(PrincipalRoot).ToArray();

        // √(Z·Y) and Z_c.
        Complex sum = gammas[0] + gammas[1], product = gammas[0] * gammas[1];
        var sqrt = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                sqrt[i, j] = (m[i, j] + (i == j ? product : Complex.Zero)) / sum;
        var zc = Multiply(Invert(sqrt), z);
        var characteristic = new double[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++) characteristic[i, j] = 0.5 * (zc[i, j].Real + zc[j, i].Real);

        var built = new LineMode[2];
        var zcInverse = Invert(zc);
        for (int k = 0; k < 2; k++)
        {
            // Voltage eigenvector of Z·Y for λ_k: the larger of the two null-space forms.
            var a = new[] { m[0, 1], lambdas[k] - m[0, 0] };
            var b = new[] { lambdas[k] - m[1, 1], m[1, 0] };
            var v = Norm(a) >= Norm(b) ? a : b;
            // vᵀ·v = 1 (not vᴴ·v): an eigenvector carries an arbitrary complex phase, which
            // would turn z/y, though not z·y; this normalisation removes it.
            Complex length = Complex.Sqrt(v[0] * v[0] + v[1] * v[1]);
            v = new[] { v[0] / length, v[1] / length };
            // Its current: the forward wave's I = Z_c⁻¹·V, scaled so iᵀ·v = 1.
            var i = new[] { zcInverse[0, 0] * v[0] + zcInverse[0, 1] * v[1], zcInverse[1, 0] * v[0] + zcInverse[1, 1] * v[1] };
            Complex dot = i[0] * v[0] + i[1] * v[1];
            i = new[] { i[0] / dot, i[1] / dot };
            Complex zm = Bilinear(i, z, i), ym = Bilinear(v, y, v);
            built[k] = ModeOf(zm, ym, w, (v[0] * Complex.Conjugate(v[1])).Real < 0 ? "pi" : "c");
        }
        if (built[0].Name == built[1].Name)
        {
            // Nearly degenerate modes (a homogeneous dielectric) have no well-defined vectors:
            // the faster one is called the π mode, as it is in a microstrip.
            int fast = built[0].DelaySecondsPerMeter <= built[1].DelaySecondsPerMeter ? 0 : 1;
            built[fast] = built[fast] with { Name = "pi" };
            built[1 - fast] = built[1 - fast] with { Name = "c" };
        }
        return (built.First(x => x.Name == "pi"), built.First(x => x.Name == "c"), characteristic);
    }

    private static LineMode ModeOf(Complex series, Complex shunt, double w, string name)
    {
        var z = Complex.Sqrt(series / shunt);
        var gamma = Complex.Sqrt(series * shunt);
        double beta = gamma.Imaginary;
        double lm = series.Imaginary / w, cm = shunt.Imaginary / w;
        double conductor = Complex.Sqrt(series * new Complex(0, w * cm)).Real;
        double dielectric = Complex.Sqrt(new Complex(0, w * lm) * shunt).Real;
        return new LineMode(name, z.Real, Math.Pow(beta * C0 / w, 2), beta / w,
            conductor * NeperToDb, dielectric * NeperToDb, series.Real, lm, cm, shunt.Real);
    }

    /// <summary>The root with a positive real part: a wave that decays as it travels.</summary>
    private static Complex PrincipalRoot(Complex lambda)
    {
        var r = Complex.Sqrt(lambda);
        return r.Real < 0 || (r.Real == 0 && r.Imaginary < 0) ? -r : r;
    }

    private static double Norm(Complex[] v) => Math.Sqrt(v[0].Magnitude * v[0].Magnitude + v[1].Magnitude * v[1].Magnitude);

    private static Complex Bilinear(Complex[] a, Complex[,] m, Complex[] b) =>
        a[0] * (m[0, 0] * b[0] + m[0, 1] * b[1]) + a[1] * (m[1, 0] * b[0] + m[1, 1] * b[1]);

    private static Complex[,] Multiply(Complex[,] a, Complex[,] b)
    {
        var r = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j];
        return r;
    }

    private static Complex[,] Invert(Complex[,] a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        return new[,] { { a[1, 1] / det, -a[0, 1] / det }, { -a[1, 0] / det, a[0, 0] / det } };
    }
}
