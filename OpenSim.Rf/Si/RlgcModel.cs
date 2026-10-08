using OpenSim.Rf.Layered;
using System.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>
/// What the per-unit-length extraction models beyond the zero-thickness electrostatic kernel.
/// <see cref="Board"/> is what a board designer needs and what the app uses; <see cref="Kernel"/>
/// is the bare kernel (zero-thickness strips, forward-conductor skin resistance only, one
/// frequency-independent complex ε), which is what <see cref="RlgcExtractor.Extract(CoupledLineCrossSection, int)"/>
/// returns and what the closed-form kernel gates are measured against.
/// </summary>
public sealed record RlgcModel
{
    /// <summary>Trace thickness enters C and L through an effective width (Hammerstad–Jensen
    /// for a line over one plane, Wheeler for a line between two).</summary>
    public bool ThicknessCorrection { get; init; } = true;

    /// <summary>Solve each trace as the trapezoid it is — base on the interface, top face at its
    /// thickness, side walls between (<see cref="ThickConductorBem"/>) — instead of a strip of
    /// effective width. The coupling between the side walls of close traces is then in the
    /// field solve, and an etched trace's slope too. Takes the place of
    /// <see cref="ThicknessCorrection"/> when set. A layer above the traces is the dielectric
    /// over their top faces; the traces stand in a zone of it as thick as the copper. Off by
    /// default.</summary>
    public bool SideWalls { get; init; }

    /// <summary>Conductor loss by Wheeler's incremental-inductance rule on the extraction's own
    /// L: every metal surface (both faces and both edges of each strip, and the reference
    /// plane or planes) recedes by half a skin depth. This carries the return-path loss and the
    /// crowding of current toward the plane and the strip edges, and the matching internal
    /// inductance ωL_int = R that a surface impedance (1 + j)R_s implies.</summary>
    public bool SurfaceImpedance { get; init; } = true;

    /// <summary>Dielectric by the Djordjevic–Sarkar wideband Debye model: the stackup's εr and
    /// tan δ are taken as measured at <see cref="DielectricReferenceHz"/>, and ε′ then falls
    /// with frequency as causality requires of a material with that loss.</summary>
    public bool WidebandDielectric { get; init; } = true;

    /// <summary>The frequency the stackup's εr and tan δ are quoted at (laminate data sheets
    /// usually say 1 GHz).</summary>
    public double DielectricReferenceHz { get; init; } = 1e9;

    /// <summary>With <see cref="WidebandDielectric"/>: the cross-section is solved again with
    /// every layer at its own ε(f) at this many frequencies per decade, from
    /// <see cref="DielectricNodes.LowestHz"/> to <see cref="DielectricNodes.HighestHz"/>, and C(f),
    /// G(f) follow those solves. 0 keeps the first-order form C′ + C″·ψ(f) from the one solve
    /// at the reference frequency, which is exact only to first order in tan δ.</summary>
    public int DielectricNodesPerDecade { get; init; } = 1;

    /// <summary>Conductivity of the reference plane(s) [S/m]; copper by default.</summary>
    public double PlaneConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Copper surface roughness, applied to every metal surface alike. Null is smooth
    /// copper. It acts on the surface-impedance loss, so it needs
    /// <see cref="SurfaceImpedance"/>.</summary>
    public SurfaceRoughness? Roughness { get; init; }

    public static RlgcModel Board { get; } = new();

    public static RlgcModel Kernel { get; } = new()
    {
        ThicknessCorrection = false, SurfaceImpedance = false, WidebandDielectric = false
    };
}

/// <summary>
/// The Djordjevic–Sarkar wideband Debye dielectric as a SHAPE function. A dielectric whose loss
/// tangent is nearly flat over many decades cannot have a constant ε′: Kramers–Kronig ties the
/// two, and the causal pair is
///
/// <code>  ε(ω) = ε∞ + Δε/ln(ω₂/ω₁) · ln((ω₂ + jω)/(ω₁ + jω))</code>
///
/// with corners ω₁, ω₂ far below and above the band of use. Between them the imaginary part of
/// the logarithm is −π/2 (flat loss) and the real part falls as ln(1/ω): ε′ drops by
/// (2/π)·tan δ·ln 10 = 1.47·tan δ per decade.
///
/// <para>The extraction needs it without re-solving the cross-section at every frequency. Its
/// one complex-ε solve returns C′ − jC″, and to first order in tan δ the capacitance is linear
/// in each layer's small complex perturbation, so if every lossy layer follows the SAME shape,
/// ε_layer(ω) = ε′_layer·(1 + tan δ_layer·ψ(ω)) with ψ(ω_ref) = −j, then</para>
///
/// <code>  C(ω) = C′ + C″·ψ(ω)      ⇒   C(ω) = C′ + C″·Re ψ,   G(ω) = −ω·C″·Im ψ</code>
///
/// <para>which reproduces the solve at the reference frequency exactly and is causal at every
/// other one. Away from the reference it is exact only to first order in tan δ: where layers
/// of different ε and tan δ share the field, C is not linear in their ε′ over the 1.5·tan δ per
/// decade it moves. The board model therefore solves the section again at nodes across the band
/// (<see cref="DielectricNodes"/>) and uses this form only between them.</para>
/// </summary>
public sealed record WidebandDebye(double ReferenceHz, double LowCornerHz = 1e4,
    double HighCornerHz = 1e12)
{
    private static Complex Log(double f, double low, double high) =>
        Complex.Log(new Complex(high, f) / new Complex(low, f));

    /// <summary>ψ(f): −j at the reference frequency; real part (2/π)·ln(f_ref/f) and imaginary
    /// part −1 between the corners.</summary>
    public Complex Shape(double frequencyHz)
    {
        var reference = Log(ReferenceHz, LowCornerHz, HighCornerHz);
        var here = Log(Math.Max(0, frequencyHz), LowCornerHz, HighCornerHz);
        return (here - reference.Real) / -reference.Imaginary;
    }

    /// <summary>ψ as f → ∞ (real): what the capacitance tends to, and so the fastest wave.</summary>
    public double ShapeAtInfinity
    {
        get
        {
            var reference = Log(ReferenceHz, LowCornerHz, HighCornerHz);
            return -reference.Real / -reference.Imaginary;
        }
    }
}

/// <summary>
/// The dielectric part of a line, C(f) − j·C″(f), from cross-section solves at several
/// frequencies with every layer at its own Djordjevic–Sarkar ε(f) = ε′·(1 + tan δ·ψ(f)).
/// Between two nodes the complex capacitance is taken as linear in ψ — the first-order form,
/// now anchored a decade away at most instead of at the one reference frequency — so each
/// piece is causal and every node is reproduced exactly. Outside the nodes the end piece
/// carries on.
/// </summary>
internal sealed class DielectricNodes
{
    /// <summary>The band the nodes cover [Hz]: below 100 kHz and above 100 GHz the first-order
    /// form from the end node is used.</summary>
    public const double LowestHz = 1e5, HighestHz = 1e11;

    private readonly WidebandDebye _shape;
    private readonly double[] _frequencies;
    private readonly Complex[] _psi;
    private readonly Complex[][,] _solved;

    public DielectricNodes(WidebandDebye shape, double[] frequencies, Complex[][,] solved)
    {
        if (frequencies.Length < 2 || frequencies.Length != solved.Length)
            throw new ArgumentException("At least two solved frequencies are needed.");
        _shape = shape;
        _frequencies = frequencies;
        _psi = frequencies.Select(shape.Shape).ToArray();
        _solved = solved;
    }

    public IReadOnlyList<double> FrequenciesHz => _frequencies;

    /// <summary>The node frequencies: a decade grid from <see cref="LowestHz"/> to
    /// <see cref="HighestHz"/> at <paramref name="perDecade"/> per decade, and the reference.</summary>
    public static double[] Frequencies(double referenceHz, int perDecade)
    {
        int steps = (int)Math.Round(Math.Log10(HighestHz / LowestHz) * perDecade);
        var nodes = Enumerable.Range(0, steps + 1)
            .Select(k => LowestHz * Math.Pow(10, (double)k / perDecade)).ToList();
        nodes.RemoveAll(f => Math.Abs(Math.Log(f / referenceHz)) < 0.05);
        nodes.Add(referenceHz);
        nodes.Sort();
        return nodes.ToArray();
    }

    /// <summary>A layer at the frequency whose shape value is <paramref name="psi"/>, in the
    /// solver's form ε′(1 − j·tan δ).</summary>
    public static LayeredStackup.Layer Dispersed(LayeredStackup.Layer layer, Complex psi)
    {
        if (layer.LossTangent <= 0) return layer;
        double real = 1 + layer.LossTangent * psi.Real;
        return new LayeredStackup.Layer(layer.RelativePermittivity * real,
            -layer.LossTangent * psi.Imaginary / real, layer.ThicknessMeters);
    }

    private Complex[,] Value(double frequencyHz)
    {
        double f = Math.Max(0, frequencyHz);
        int node = Array.IndexOf(_frequencies, f);
        if (node >= 0) return (Complex[,])_solved[node].Clone();     // a node is its own solve, exactly
        int lo = 0;
        while (lo < _frequencies.Length - 2 && f > _frequencies[lo + 1]) lo++;
        int hi = lo + 1;
        var t = (_shape.Shape(f) - _psi[lo]) / (_psi[hi] - _psi[lo]);
        int n = _solved[lo].GetLength(0);
        var value = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                value[i, j] = _solved[lo][i, j] + t * (_solved[hi][i, j] - _solved[lo][i, j]);
        return value;
    }

    /// <summary>C(f) [F/m].</summary>
    public double[,] Capacitance(double frequencyHz)
    {
        var v = Value(frequencyHz);
        int n = v.GetLength(0);
        var c = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) c[i, j] = v[i, j].Real;
        return c;
    }

    /// <summary>G(f) = ω·C″(f) [S/m].</summary>
    public double[,] Conductance(double frequencyHz)
    {
        var v = Value(frequencyHz);
        double w = 2 * Math.PI * frequencyHz;
        int n = v.GetLength(0);
        var g = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) g[i, j] = -w * v[i, j].Imaginary;
        return g;
    }

    /// <summary>The same nodes for the conductors kept by a reduction.</summary>
    public DielectricNodes Sub(IReadOnlyList<int> keep) => new(_shape, _frequencies, _solved.Select(c =>
    {
        var s = new Complex[keep.Count, keep.Count];
        for (int i = 0; i < keep.Count; i++)
            for (int j = 0; j < keep.Count; j++) s[i, j] = c[keep[i], keep[j]];
        return s;
    }).ToArray());
}

/// <summary>
/// Effective-width corrections for a strip of finite thickness, so the zero-thickness kernel
/// can stand in for it.
/// </summary>
public static class ThicknessCorrection
{
    /// <summary>Hammerstad and Jensen (1980) for a strip over ONE plane: the width increase for
    /// the homogeneous (air) line, and the smaller one for the line on its dielectric,
    /// Δw_r = ½Δw₁(1 + 1/cosh√(εr − 1)). The characteristic impedance takes the air line at
    /// w + Δw_r and the effective permittivity is then scaled by [Z₀₁(w + Δw₁)/Z₀₁(w + Δw_r)]².</summary>
    public static (double Air, double Dielectric) Microstrip(double width, double thickness,
        double height, double relativePermittivity)
    {
        if (thickness <= 0 || width <= 0 || height <= 0) return (0, 0);
        double u = width / height, tn = thickness / height;
        double coth = 1 / Math.Tanh(Math.Sqrt(6.517 * u));
        double air = height * tn / Math.PI * Math.Log(1 + 4 * Math.E / (tn * coth * coth));
        double dielectric = 0.5 * air
            * (1 + 1 / Math.Cosh(Math.Sqrt(Math.Max(relativePermittivity - 1, 0))));
        return (air, dielectric);
    }

    /// <summary>Wheeler (1978) for a strip BETWEEN two planes, with
    /// <paramref name="dielectricSpacing"/> the dielectric between the strip's two faces and the
    /// planes (below plus above), so the planes are b = spacing + t apart. His thick-strip
    /// impedance depends on the geometry only through (b − t)/(w + Δw), which is the
    /// zero-thickness strip of width w + Δw between planes b − t apart — exactly the section
    /// the kernel solves. Δw is returned.</summary>
    public static double Stripline(double width, double thickness, double dielectricSpacing)
    {
        if (thickness <= 0 || width <= 0 || dielectricSpacing <= 0) return 0;
        double planes = dielectricSpacing + thickness;
        double x = thickness / planes;
        double m = 2 / (1 + 2.0 / 3.0 * x / (1 - x));
        double a = x / (2 - x);
        double b = 0.0796 * x / (width / planes + 1.1 * x);
        return thickness / Math.PI * (1 - 0.5 * Math.Log(a * a + Math.Pow(b, m)));
    }
}

/// <summary>
/// The series internal impedance of a line's conductors as one causal function of frequency:
/// at high frequency the surface impedance (1 + j)·K·√f, at DC the resistance R_dc with a
/// finite internal inductance, and in between the square-root blend
///
/// <code>  Z_ii(f) = √(R_dc,i² + 2j·K_ii²·f)</code>
///
/// which is analytic in the lower half ω plane (the argument of the root never leaves the
/// right half plane), so R and L_int are a Kramers–Kronig pair by construction. The older
/// R = max(R_dc, K√f) with no reactance at all is not. Off-diagonal entries (the resistance
/// two lines share through the plane) have no DC value and are blended with the geometric
/// means of the two lines' constants: K_ij·(√(R̄² + 2jK̄²f) − R̄)/K̄.
/// </summary>
public sealed class ConductorImpedance
{
    private readonly double[] _dc;
    private readonly double[,] _skin;
    private readonly bool _sharedOnly;

    /// <param name="sharedOnly">Treat the DIAGONAL like the off-diagonal entries: a resistance
    /// with no DC value of its own (a reference plane's share), which vanishes at DC on the
    /// crossover set by <paramref name="resistanceDc"/> instead of starting from it.</param>
    public ConductorImpedance(double[] resistanceDc, double[,] skinOhmsPerMeterPerSqrtHz,
        bool sharedOnly = false)
    {
        _dc = resistanceDc;
        _skin = skinOhmsPerMeterPerSqrtHz;
        _sharedOnly = sharedOnly;
    }

    private static Complex Blend(double dc, double skin, double f) =>
        Complex.Sqrt(new Complex(dc * dc, 2 * skin * skin * f));

    private Complex Entry(int i, int j, double f)
    {
        if (i == j && !_sharedOnly) return Blend(_dc[i], _skin[i, i], f);
        double meanSkin = Math.Sqrt(_skin[i, i] * _skin[j, j]);
        double meanDc = Math.Sqrt(_dc[i] * _dc[j]);
        return meanSkin > 0 ? _skin[i, j] / meanSkin * (Blend(meanDc, meanSkin, f) - meanDc) : 0;
    }

    /// <summary>R(f) [Ω/m], N×N.</summary>
    public double[,] Resistance(double frequencyHz)
    {
        int n = _dc.Length;
        double f = Math.Max(0, frequencyHz);
        var r = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] = Entry(i, j, f).Real;
        return r;
    }

    /// <summary>L_int(f) [H/m], N×N: Im Z/ω, with its finite limit at f = 0.</summary>
    public double[,] InternalInductance(double frequencyHz)
    {
        int n = _dc.Length;
        double f = Math.Max(0, frequencyHz);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (f > 0)
                {
                    l[i, j] = Entry(i, j, f).Imaginary / (2 * Math.PI * f);
                    continue;
                }
                // √(R² + 2jK²f) → R + jK²f/R as f → 0 (the same limit on and off the diagonal).
                double meanSkin = Math.Sqrt(_skin[i, i] * _skin[j, j]);
                double meanDc = Math.Sqrt(_dc[i] * _dc[j]);
                l[i, j] = meanDc > 0 ? _skin[i, j] * meanSkin / meanDc / (2 * Math.PI) : 0;
            }
        return l;
    }
}
