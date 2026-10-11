using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>A trace of a cross-section whose conductors lie on more than one interface: the strip,
/// and the stackup interface it lies on (see <see cref="LayeredStackup.InterfaceHeights"/>).</summary>
public sealed record LayeredTrace(TraceCrossSection Trace, int Interface);

/// <summary>
/// FU-29 — the 2D quasi-static extraction for traces on DIFFERENT interfaces of one grounded stack:
/// broadside coupling, a trace over a trace with no plane between them. The coplanar extraction
/// (<see cref="RlgcExtractor"/>) keeps every conductor on one interface and refuses a broadside
/// pair; this one takes its kernel to every pair of interfaces.
///
/// <para><b>Kernel.</b> In the spectral domain the static potential of a line charge obeys, in each
/// layer, φ″ = k²φ, so a layer of permittivity ε and thickness t is a two-port between the
/// potentials at its faces with nodal admittance (per ε₀) ε·k·[[coth kt, −csch kt], [−csch kt, coth kt]]
/// (from φ = A·cosh + B·sinh and the flux ε·φ′ at each face). The ground is a node held at zero, an
/// open top adds the half-space's admittance k at the top interface, a second plane holds the top
/// at zero. A unit line charge on interface i is a unit current injected at node i, and the
/// potentials that solve the nodal system are G̃_ji(k) on every interface j at once. For i = j this
/// is the coplanar kernel's 1/(Y↓ + Y↑) exactly — the same node seen through the same layers.</para>
///
/// <para><b>Moments.</b> Galerkin pulses on cosine-graded panels, M_ab = (1/πε₀)∫₀^∞ G̃_ab(k)·
/// Re(A_a(k)·conj A_b(k)) dk with A the panel transform w·sinc(kw/2)·e^{ikx_c}, as the coplanar
/// spectral kernel has it. On one interface the log singularity is taken out in closed form, as
/// there; between two interfaces the kernel decays as e^{−k·Δz} and is integrated as it is, out to
/// where that is e^{−36}.</para>
/// </summary>
public static class BroadsideExtractor
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;
    private const double CutExponent = 36;
    private const int PointsPerPanel = 6;

    private readonly record struct Panel(double Start, double End, int Conductor, int Interface)
    {
        public double Width => End - Start;
        public double Center => 0.5 * (Start + End);
    }

    /// <summary>C, C″, C_air, L = µ₀ε₀·C_air⁻¹ and the kernel model's R (two-sided strip,
    /// R_dc/skin crossover) of traces on interfaces of <paramref name="stackup"/>, a plane under it
    /// and air above, or a second plane above (<paramref name="topGround"/>). Traces are
    /// zero-thickness strips for C and L.</summary>
    public static RlgcResult Extract(LayeredStackup stackup, bool topGround, IReadOnlyList<LayeredTrace> traces,
        int panelsPerTrace = 48)
    {
        if (traces is null || traces.Count == 0) throw new ArgumentException("At least one trace is required.", nameof(traces));
        int layers = stackup.Layers.Count;
        foreach (var t in traces)
        {
            if (t.Interface < 0 || t.Interface >= layers)
                throw new ArgumentOutOfRangeException(nameof(traces), $"Interface {t.Interface} is not in a {layers}-layer stack.");
            if (topGround && t.Interface == layers - 1)
                throw new ArgumentException("With a top plane no trace can lie on the stack's top: it would be on the plane.", nameof(traces));
            if (!(t.Trace.WidthMeters > 0)) throw new ArgumentException("Every trace needs a positive width.", nameof(traces));
        }
        for (int a = 0; a < traces.Count; a++)
            for (int b = a + 1; b < traces.Count; b++)
            {
                if (traces[a].Interface != traces[b].Interface) continue;
                var (p, q) = (traces[a].Trace, traces[b].Trace);
                double gap = Math.Abs(p.CenterMeters - q.CenterMeters) - (p.WidthMeters + q.WidthMeters) / 2;
                if (gap <= 0)
                    throw new ArgumentException($"Traces {a} and {b} overlap on interface {traces[a].Interface}.", nameof(traces));
            }

        int n = traces.Count;
        var panels = new List<Panel>();
        for (int conductor = 0; conductor < n; conductor++)
        {
            var trace = traces[conductor];
            double half = trace.Trace.WidthMeters / 2, center = trace.Trace.CenterMeters, previous = center - half;
            for (int j = 1; j <= panelsPerTrace; j++)
            {
                double edge = center - half * Math.Cos(Math.PI * j / panelsPerTrace);
                panels.Add(new Panel(previous, edge, conductor, trace.Interface));
                previous = edge;
            }
        }

        var air = new LayeredStackup(stackup.Layers.Select(l => new LayeredStackup.Layer(1, 0, l.ThicknessMeters)).ToArray());
        var cComplex = Solve(panels, n, Moments(panels, stackup, topGround));
        var cAirComplex = Solve(panels, n, Moments(panels, air, topGround));
        var c = new double[n, n];
        var cLoss = new double[n, n];
        var cAir = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                c[i, j] = cComplex[i, j].Real;
                cLoss[i, j] = -cComplex[i, j].Imaginary;
                cAir[i, j] = cAirComplex[i, j].Real;
            }
        var inductance = Invert(cAir);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) inductance[i, j] *= Mu0 * Epsilon0;

        var rDc = new double[n];
        var rSkin = new double[n];
        for (int i = 0; i < n; i++)
        {
            var t = traces[i].Trace;
            rDc[i] = 1.0 / (t.ConductivitySiemensPerMeter * t.WidthMeters * t.ThicknessMeters);
            rSkin[i] = Math.Sqrt(Math.PI * Mu0 / t.ConductivitySiemensPerMeter) / (2 * t.WidthMeters);
        }
        var assumptions = new List<string>
        {
            "Quasi-TEM per-unit-length model: C from a 2D layered electrostatic BEM with the traces on their "
                + "own interfaces of the stack (broadside coupling), L = µ₀ε₀·C_air⁻¹, G = ω·C″.",
            "Traces are zero-thickness strips for C and L; R = max(R_dc, R_s(f)/2w) per conductor, the return "
                + "plane lossless.",
            topGround
                ? "Between two infinite, solid reference planes; nothing else is on the layers the traces use."
                : "Over one infinite, solid reference plane, the board's remaining dielectric and then air above.",
        };
        return new RlgcResult(n, c, cLoss, cAir, inductance, rDc, rSkin, assumptions);
    }

    /// <summary>The nodal potentials on every interface for a unit line charge on interface
    /// <paramref name="source"/>, in 1/ε₀ units: G̃_j,source(k).</summary>
    internal static Complex[] Green(LayeredStackup stackup, bool topGround, double k, int source)
    {
        var layers = stackup.Layers;
        int n = layers.Count;
        var y = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
        {
            Complex ek = layers[i].ComplexPermittivity * k;
            double x = k * layers[i].ThicknessMeters;
            double e2 = Math.Exp(-2 * x);
            // coth x = (1 + e^{−2x})/(1 − e^{−2x}), csch x = 2e^{−x}/(1 − e^{−2x}): no overflow.
            double oneMinus = x < 1e-8 ? 2 * x : 1 - e2;
            double coth = (1 + e2) / oneMinus, csch = 2 * Math.Exp(-x) / oneMinus;
            // Layer i joins node i − 1 (the ground for i = 0) to node i.
            y[i, i] += ek * coth;
            if (i > 0)
            {
                y[i - 1, i - 1] += ek * coth;
                y[i - 1, i] -= ek * csch;
                y[i, i - 1] -= ek * csch;
            }
        }
        if (topGround)
        {
            // The top interface IS the plane: hold it at zero.
            for (int j = 0; j < n; j++) { y[n - 1, j] = 0; y[j, n - 1] = 0; }
            y[n - 1, n - 1] = 1;
        }
        else y[n - 1, n - 1] += k;                                  // the half-space of air above
        var rhs = new Complex[n];
        rhs[source] = 1;
        return ComplexLu.Factor(y).Solve(rhs);
    }

    private static ComplexDenseMatrix Moments(List<Panel> panels, LayeredStackup stackup, bool topGround)
    {
        var layers = stackup.Layers;
        int interfaces = layers.Count, m = panels.Count;
        var used = panels.Select(p => p.Interface).Distinct().OrderBy(i => i).ToArray();

        // Per interface used: ε above + ε below, and the regularizing depth 2·h_min of the layers
        // touching it — the coplanar kernel's own choices.
        var epsSum = new Complex[interfaces];
        var depth = new double[interfaces];
        double kMax = 0;
        foreach (int i in used)
        {
            var below = layers[i];
            var above = i + 1 < interfaces ? layers[i + 1] : null;
            epsSum[i] = below.ComplexPermittivity + (above?.ComplexPermittivity ?? Complex.One);
            double hMin = Math.Min(below.ThicknessMeters, above?.ThicknessMeters ?? double.MaxValue);
            depth[i] = 2 * hMin;
            kMax = Math.Max(kMax, CutExponent / depth[i]);
        }
        var heights = stackup.InterfaceHeights();
        foreach (int i in used)
            foreach (int j in used)
                if (i < j) kMax = Math.Max(kMax, CutExponent / (heights[j] - heights[i]));

        double span = panels.Max(p => p.End) - panels.Min(p => p.Start);
        int kPanels = Math.Max(48, (int)Math.Ceiling(kMax * span / Math.PI));
        var (unit, unitWeights) = GaussLegendre.Rule(PointsPerPanel, 0, 1);
        int nk = kPanels * PointsPerPanel;
        double dk = kMax / kPanels;
        var kNodes = new double[nk];
        // weight[i, j][node]: the remainder kernel between interfaces i and j, times the rule's weight.
        var weights = new Dictionary<(int, int), Complex[]>();
        foreach (int i in used) foreach (int j in used) weights[(i, j)] = new Complex[nk];
        Parallel.For(0, nk, node =>
        {
            int q = node / PointsPerPanel, g = node % PointsPerPanel;
            double k = (q + unit[g]) * dk;
            kNodes[node] = k;
            double w = unitWeights[g] * dk / (Math.PI * Epsilon0);
            foreach (int source in used)
            {
                var potentials = Green(stackup, topGround, k, source);
                foreach (int target in used)
                {
                    Complex kernel = potentials[target];
                    if (target == source)
                    {
                        double x = k * depth[source];
                        double oneMinusExp = x < 1e-5 ? x * (1 - x / 2 + x * x / 6) : 1 - Math.Exp(-x);
                        kernel -= oneMinusExp / k / epsSum[source];
                    }
                    weights[(target, source)][node] = kernel * w;
                }
            }
        });

        var cos = new double[m][];
        var sin = new double[m][];
        Parallel.For(0, m, a =>
        {
            double width = panels[a].Width, center = panels[a].Center;
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

        var rows = new Complex[m][];
        Parallel.For(0, m, a =>
        {
            var row = new Complex[m];
            for (int b = a; b < m; b++)
            {
                var kernel = weights[(panels[a].Interface, panels[b].Interface)];
                double[] ca = cos[a], sa = sin[a], cb = cos[b], sb = sin[b];
                Complex sum = Complex.Zero;
                for (int j = 0; j < nk; j++) sum += kernel[j] * (ca[j] * cb[j] + sa[j] * sb[j]);
                if (panels[a].Interface == panels[b].Interface)
                {
                    int i = panels[a].Interface;
                    sum += 1.0 / (Math.PI * Epsilon0 * epsSum[i])
                           * (RlgcExtractor.LogMoment(panels[a].Start, panels[a].End, panels[b].Start, panels[b].End, depth[i])
                              - RlgcExtractor.LogMoment(panels[a].Start, panels[a].End, panels[b].Start, panels[b].End, 0));
                }
                row[b] = sum;
            }
            rows[a] = row;
        });
        var matrix = new ComplexDenseMatrix(m, m);
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                // The kernel is reciprocal (G̃_ij = G̃_ji): one triangle serves both.
                matrix[a, b] = rows[a][b];
                matrix[b, a] = rows[a][b];
            }
        return matrix;
    }

    private static Complex[,] Solve(List<Panel> panels, int conductors, ComplexDenseMatrix matrix)
    {
        int m = panels.Count;
        var lu = ComplexLu.Factor(matrix);
        var result = new Complex[conductors, conductors];
        for (int k = 0; k < conductors; k++)
        {
            var rhs = new Complex[m];
            for (int a = 0; a < m; a++) rhs[a] = panels[a].Conductor == k ? panels[a].Width : Complex.Zero;
            var charge = lu.Solve(rhs);
            for (int a = 0; a < m; a++) result[panels[a].Conductor, k] += charge[a] * panels[a].Width;
        }
        return result;
    }

    private static double[,] Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var matrix = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) matrix[i, j] = a[i, j];
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
}
