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

    /// <summary>Z0 of a single trace; for a pair, one trace's impedance with the other
    /// terminated (Z_c11 = (Z_odd + Z_even)/2).</summary>
    public double ImpedanceOhms => IsPair
        ? 0.5 * (Modes[0].ImpedanceOhms + Modes[1].ImpedanceOhms) : Modes[0].ImpedanceOhms;

    /// <summary>2·Z_odd. Null for a single trace.</summary>
    public double? DifferentialOhms => IsPair ? 2 * Modes[0].ImpedanceOhms : null;

    /// <summary>Z_even/2. Null for a single trace.</summary>
    public double? CommonOhms => IsPair ? Modes[1].ImpedanceOhms / 2 : null;

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
        if (IsPair)
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
            string name = IsPair ? (mode.Name == "odd" ? "Differential (odd) mode" : "Common (even) mode") : "Line";
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
            assumptions.Add($"Etched trace ({baseWidth * 1e6:g4} µm at the base, {t * 1e6:g4} µm at the top): solved as a "
                + $"rectangle of the mean width {width * 1e6:g4} µm with the same copper area. The slope of "
                + "the side walls is not in the field solve.");

        var layers = new List<LayeredStackup.Layer>
        {
            new(spec.RelativePermittivity, spec.LossTangent, spec.HeightMeters)
        };
        if (spec.Structure != LineStructure.Microstrip)
            layers.Add(new(spec.UpperRelativePermittivity, spec.UpperLossTangent, spec.UpperHeightMeters));
        bool topGround = spec.Structure == LineStructure.Stripline;
        if (spec.Structure == LineStructure.EmbeddedMicrostrip)
            assumptions.Add("The layer over the trace is flat and of the given thickness measured from the "
                + "trace's base; a conformal solder mask that follows the copper is not modelled.");

        // Signal traces, centred on x = 0.
        var traces = new List<TraceCrossSection>();
        double signalHalfSpan;
        if (spec.PairGapMeters is { } gap)
        {
            double pitch = baseWidth + gap;
            traces.Add(new(-pitch / 2, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            traces.Add(new(pitch / 2, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            signalHalfSpan = pitch / 2 + baseWidth / 2;
        }
        else
        {
            traces.Add(new(0, width, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            signalHalfSpan = baseWidth / 2;
        }
        int signals = traces.Count;

        var grounded = new List<int>();
        if (spec.CoplanarGapMeters is { } coplanarGap)
        {
            double tallest = Math.Max(spec.HeightMeters, topGround ? spec.UpperHeightMeters : 0);
            double groundWidth = spec.CoplanarGroundWidthMeters > 0
                ? spec.CoplanarGroundWidthMeters
                : Math.Max(6 * tallest, 4 * (2 * signalHalfSpan + 2 * coplanarGap));
            double center = signalHalfSpan + coplanarGap + groundWidth / 2;
            // The grounds are etched like the traces: their near edges recede by the same amount.
            double g = groundWidth - shrink / 2;
            traces.Add(new(-center - shrink / 4, g, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            traces.Add(new(center + shrink / 4, g, spec.ThicknessMeters, spec.ConductivitySiemensPerMeter));
            assumptions.Add($"Coplanar grounds: two strips {groundWidth * 1e3:g3} mm wide, {coplanarGap * 1e6:g4} µm from "
                + "the trace, solved as conductors and tied to the plane (stitched along their length).");
        }

        var section = new CoupledLineCrossSection(new LayeredStackup(layers), 0, traces, topGround);
        var full = RlgcExtractor.Extract(section, spec.Model, panelsPerTrace);
        // The section sorts its traces by centre: the grounds are the first and the last.
        if (spec.CoplanarGapMeters is not null) { grounded.Add(0); grounded.Add(traces.Count - 1); }
        var rlgc = RlgcReduction.GroundConductors(full, grounded);

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
        if (signals == 1)
        {
            modes.Add(Mode("single", R(0, 0), L(0, 0), c[0, 0], g2[0, 0]));
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
            NearEndCoupling = nearEnd,
            FarEndCouplingSecondsPerMeter = farEnd,
            Assumptions = assumptions
        };
    }
}
