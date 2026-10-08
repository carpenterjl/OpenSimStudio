using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>
/// The 2D electrostatic BEM for conductors of finite thickness: every trace is the trapezoid it
/// is (base on the metal interface, top face at its thickness, side walls between), so the
/// charge on the side walls — and the coupling between the side walls of close neighbours —
/// is solved for instead of folded into an effective width.
///
/// <para><b>Kernel.</b> The conductors stand in the region just above the metal interface: the
/// open half-space of a surface microstrip, or the layer over the traces (a cover, or the
/// dielectric up to a stripline's upper plane), of permittivity ε_u and thickness h. For a line
/// charge at height z′ and an observer at z, both in that region, the potential's transform is
/// (in 1/ε₀ units)</para>
/// <code>  2ε_u·k·G̃ = e^{−k|z−z′|} + (R↓/D)·e^{−kz}e^{−kz′} + (R↑/D)·e^{−k(h−z)}e^{−k(h−z′)}
///                 + (R↓R↑e^{−kh}/D)·(e^{−kz}e^{−k(h−z′)} + e^{−k(h−z)}e^{−kz′}),   D = 1 − R↓R↑e^{−2kh}</code>
/// <para>with R↓ = (ε_u·k − Y↓)/(ε_u·k + Y↓) the reflection at the interface (Y↓ the stack below,
/// as the strip BEM computes it) and R↑ the same at the region's top. Every term but the first
/// is a product of a function of z and one of z′, so its Galerkin moments are products of panel
/// transforms ∫e^{ikx}e^{∓kz}ds, which are closed forms on a straight panel. The first term and
/// the static limits of the reflections (images at −z′ and at 2h − z′ with R↓(∞), R↑(∞)) are
/// done in space, as 2D log kernels between straight segments, with one deep image of
/// coefficient −(1 + R↓(∞) + R↑(∞)) that makes the spatial part charge-neutral (and so free of
/// the 2D log's reference) and the spectral remainder finite at k = 0. The remainder decays like
/// e^{−2k·h_below} or e^{−2k(h − t)} and is integrated numerically.</para>
///
/// <para><b>Panels.</b> Pulse bases on cosine-graded panels on each of the four faces, so the
/// corner singularities are resolved from both sides. Galerkin moments throughout: parallel
/// segment pairs by the same closed form the strip BEM uses, other pairs by Gauss quadrature
/// of the exact inner integral, graded toward the point where the two segments come closest.</para>
/// </summary>
internal static class ThickConductorBem
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double SpectralCutExponent = 36;
    private const int SpectralPointsPerPanel = 6;

    internal readonly record struct Segment(double X0, double Z0, double X1, double Z1, int Conductor)
    {
        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Z1 - Z0) * (Z1 - Z0));
    }

    /// <summary>Panels on the four faces of every trace: <paramref name="perFace"/> on the base
    /// and on the top, and on each wall in proportion to its height (at least six).</summary>
    public static List<Segment> Panels(IReadOnlyList<TraceCrossSection> traces, int perFace)
    {
        var panels = new List<Segment>();
        for (int c = 0; c < traces.Count; c++)
        {
            var trace = traces[c];
            double t = trace.ThicknessMeters;
            double top = trace.TopWidthMeters ?? trace.WidthMeters;
            double b0 = trace.CenterMeters - trace.WidthMeters / 2, b1 = trace.CenterMeters + trace.WidthMeters / 2;
            double t0 = trace.CenterMeters - top / 2, t1 = trace.CenterMeters + top / 2;
            int wall = Math.Max(6, (int)Math.Ceiling(perFace * t / Math.Max(trace.WidthMeters, top)));
            Face(panels, b0, 0, b1, 0, perFace, c);
            Face(panels, b1, 0, t1, t, wall, c);
            Face(panels, t1, t, t0, t, perFace, c);
            Face(panels, t0, t, b0, 0, wall, c);
        }
        return panels;
    }

    private static void Face(List<Segment> panels, double x0, double z0, double x1, double z1, int count, int conductor)
    {
        double px = x0, pz = z0;
        for (int j = 1; j <= count; j++)
        {
            double f = 0.5 * (1 - Math.Cos(Math.PI * j / count));
            double x = x0 + (x1 - x0) * f, z = z0 + (z1 - z0) * f;
            panels.Add(new Segment(px, pz, x, z, conductor));
            (px, pz) = (x, z);
        }
    }

    /// <summary>The Maxwell capacitance matrix [F/m] (complex with lossy layers: C − jC″) of
    /// the traces standing on interface <paramref name="metalInterface"/> of the stack.</summary>
    public static Complex[,] Capacitance(IReadOnlyList<TraceCrossSection> traces, LayeredStackup stackup,
        int metalInterface, bool topGround, int perFace)
    {
        var panels = Panels(traces, perFace);
        var matrix = Moments(panels, stackup, metalInterface, topGround, traces.Max(t => t.ThicknessMeters));
        int m = panels.Count, n = traces.Count;
        var lu = ComplexLu.Factor(matrix);
        var result = new Complex[n, n];
        for (int k = 0; k < n; k++)
        {
            var rhs = new Complex[m];
            for (int a = 0; a < m; a++)
                rhs[a] = panels[a].Conductor == k ? panels[a].Length : Complex.Zero;
            var charge = lu.Solve(rhs);
            for (int a = 0; a < m; a++)
                result[panels[a].Conductor, k] += charge[a] * panels[a].Length;
        }
        // Galerkin moments are symmetric; the solve is to rounding.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                result[i, j] = result[j, i] = 0.5 * (result[i, j] + result[j, i]);
        return result;
    }

    /// <summary>The region the conductors stand in and its reflections.</summary>
    private sealed record Region(Complex EpsilonUpper, double Height, bool Bounded,
        Complex DownAtInfinity, Complex UpAtInfinity, double DeepImage);

    private static Region RegionOf(LayeredStackup stackup, int metalInterface, bool topGround, double thickness)
    {
        var layers = stackup.Layers;
        bool bounded = metalInterface + 1 < layers.Count;
        Complex epsU = bounded ? layers[metalInterface + 1].ComplexPermittivity : Complex.One;
        double h = bounded ? layers[metalInterface + 1].ThicknessMeters : double.PositiveInfinity;
        if (bounded && !(thickness < h))
            throw new ArgumentException(
                $"The traces ({thickness * 1e6:g3} µm) are as thick as the layer over them ({h * 1e6:g3} µm); "
                + "the side-wall model needs them to stand inside it.");
        Complex epsD = layers[metalInterface].ComplexPermittivity;
        Complex down = (epsU - epsD) / (epsU + epsD);
        Complex up = Complex.Zero;
        if (bounded)
        {
            if (metalInterface + 2 < layers.Count)
            {
                var next = layers[metalInterface + 2].ComplexPermittivity;
                up = (epsU - next) / (epsU + next);
            }
            else up = topGround ? -Complex.One : (epsU - 1) / (epsU + 1);
        }
        return new Region(epsU, h, bounded, down, up, 2 * layers[metalInterface].ThicknessMeters);
    }

    /// <summary>R↓(k) and R↑(k) (R↑ = 0 for the open half-space).</summary>
    private static (Complex Down, Complex Up) Reflections(LayeredStackup stackup, int metalInterface,
        bool topGround, Region region, double k)
    {
        var layers = stackup.Layers;
        Complex ek = region.EpsilonUpper * k;
        Complex yDown = RlgcExtractor.AdmittanceDown(layers, metalInterface, k);
        Complex down = (ek - yDown) / (ek + yDown);
        if (!region.Bounded) return (down, Complex.Zero);
        Complex up;
        if (metalInterface + 2 < layers.Count)
        {
            Complex yUp = RlgcExtractor.AdmittanceUp(layers, metalInterface + 1, k, topGround);
            up = (ek - yUp) / (ek + yUp);
        }
        else up = topGround ? -Complex.One : (ek - k) / (ek + k);
        return (down, up);
    }

    private static ComplexDenseMatrix Moments(List<Segment> panels, LayeredStackup stackup, int metalInterface,
        bool topGround, double thickness)
    {
        int m = panels.Count;
        var region = RegionOf(stackup, metalInterface, topGround, thickness);
        Complex epsU = region.EpsilonUpper;
        double h = region.Height;
        Complex deep = -(1 + region.DownAtInfinity + region.UpAtInfinity);

        // ---- Spectral remainder.
        double decay = 2 * stackup.Layers[metalInterface].ThicknessMeters;
        if (region.Bounded) decay = Math.Min(decay, 2 * (h - thickness));
        decay = Math.Min(decay, region.DeepImage);
        double kMax = SpectralCutExponent / decay;
        double span = panels.Max(p => Math.Max(p.X0, p.X1)) - panels.Min(p => Math.Min(p.X0, p.X1));
        int kPanels = Math.Max(48, (int)Math.Ceiling(kMax * span / Math.PI));
        var (unitNodes, unitWeights) = GaussLegendre.Rule(SpectralPointsPerPanel, 0, 1);
        int nk = kPanels * SpectralPointsPerPanel;
        double dk = kMax / kPanels;
        var kNodes = new double[nk];
        var wA = new Complex[nk];
        var wB = new Complex[nk];
        var wE = new Complex[nk];
        for (int q = 0; q < kPanels; q++)
            for (int g = 0; g < SpectralPointsPerPanel; g++)
            {
                int j = q * SpectralPointsPerPanel + g;
                double k = (q + unitNodes[g]) * dk;
                kNodes[j] = k;
                var (rd, ru) = Reflections(stackup, metalInterface, topGround, region, k);
                Complex d = region.Bounded ? 1 - rd * ru * Math.Exp(-2 * k * h) : Complex.One;
                Complex scale = unitWeights[g] * dk / (Math.PI * Epsilon0) / (2 * epsU * k);
                wA[j] = scale * (rd / d - region.DownAtInfinity - deep * Math.Exp(-k * region.DeepImage));
                if (region.Bounded)
                {
                    wB[j] = scale * (ru / d - region.UpAtInfinity);
                    wE[j] = scale * (rd * ru * Math.Exp(-k * h) / d);
                }
            }

        // Panel transforms ∫e^{ikx}e^{−kz}ds and ∫e^{ikx}e^{−k(h−z)}ds.
        var down = new Complex[m][];
        var upT = region.Bounded ? new Complex[m][] : null;
        Parallel.For(0, m, a =>
        {
            var p = panels[a];
            var dn = new Complex[nk];
            var upRow = region.Bounded ? new Complex[nk] : null;
            for (int j = 0; j < nk; j++)
            {
                dn[j] = Transform(p, kNodes[j], -1, 0);
                if (upRow is not null) upRow[j] = Transform(p, kNodes[j], +1, h);
            }
            down[a] = dn;
            if (upT is not null) upT[a] = upRow!;
        });

        // ---- Spatial part: the direct term and the three images, as log moments.
        Complex spatialScale = -1 / (2 * Math.PI * Epsilon0 * epsU);
        var images = new List<(Complex Coeff, Func<Segment, Segment> Map)>
        {
            (1, s => s),
            (region.DownAtInfinity, s => s with { Z0 = -s.Z0, Z1 = -s.Z1 }),
            (deep, s => s with { Z0 = -s.Z0 - region.DeepImage, Z1 = -s.Z1 - region.DeepImage }),
        };
        if (region.Bounded && region.UpAtInfinity != Complex.Zero)
            images.Add((region.UpAtInfinity, s => s with { Z0 = 2 * h - s.Z0, Z1 = 2 * h - s.Z1 }));

        var matrix = new ComplexDenseMatrix(m, m);
        var rows = new Complex[m][];
        Parallel.For(0, m, a =>
        {
            var row = new Complex[m];
            var da = down[a];
            var ua = upT?[a];
            for (int b = a; b < m; b++)
            {
                var db = down[b];
                var ub = upT?[b];
                Complex sum = Complex.Zero;
                for (int j = 0; j < nk; j++)
                {
                    sum += wA[j] * RealProduct(da[j], db[j]);
                    if (ua is not null)
                        sum += wB[j] * RealProduct(ua[j], ub![j])
                             + wE[j] * (RealProduct(da[j], ub![j]) + RealProduct(ua[j], db[j]));
                }
                Complex spatial = Complex.Zero;
                foreach (var (coeff, map) in images)
                    spatial += coeff * LogMoment(panels[a], map(panels[b]));
                row[b] = sum + spatialScale * spatial;
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

    /// <summary>Re(p·conj(q)).</summary>
    private static double RealProduct(Complex p, Complex q) => p.Real * q.Real + p.Imaginary * q.Imaginary;

    /// <summary>Over the panel: ∫e^{ikx − kz}ds (sign −1), or ∫e^{ikx − k(offset − z)}ds (sign +1).</summary>
    private static Complex Transform(Segment p, double k, int sign, double offset)
    {
        double length = p.Length;
        double cx = (p.X1 - p.X0) / length, cz = (p.Z1 - p.Z0) / length;
        // Exponent at the start, and its rate along the panel.
        var start = new Complex(sign * k * p.Z0 - (sign > 0 ? k * offset : 0), k * p.X0);
        var rate = new Complex(sign * k * cz, k * cx);
        var x = rate * length;
        Complex integral = x.Magnitude < 1e-6
            ? length * (1 + x / 2 + x * x / 6)
            : (Complex.Exp(x) - 1) / rate;
        return Complex.Exp(start) * integral;
    }

    // ------------------------------------------------------------------ log moments

    /// <summary>∬ ln|r − r′| ds ds′ over two straight segments.</summary>
    internal static double LogMoment(Segment a, Segment b)
    {
        double la = a.Length, lb = b.Length;
        double eax = (a.X1 - a.X0) / la, eaz = (a.Z1 - a.Z0) / la;
        double ebx = (b.X1 - b.X0) / lb, ebz = (b.Z1 - b.Z0) / lb;
        double cross = eax * ebz - eaz * ebx;
        if (Math.Abs(cross) < 1e-12)
        {
            // Parallel: along a's direction b spans [s0, s1] at a perpendicular offset.
            double s0 = (b.X0 - a.X0) * eax + (b.Z0 - a.Z0) * eaz;
            double s1 = (b.X1 - a.X0) * eax + (b.Z1 - a.Z0) * eaz;
            double offset = Math.Abs((b.X0 - a.X0) * eaz - (b.Z0 - a.Z0) * eax);
            return RlgcExtractor.LogMoment(0, la, Math.Min(s0, s1), Math.Max(s0, s1), offset);
        }

        double Inner(double s)
        {
            double rx = a.X0 + eax * s - b.X0, rz = a.Z0 + eaz * s - b.Z0;
            double u = rx * ebx + rz * ebz;
            double v = Math.Abs(rx * ebz - rz * ebx);
            return Antiderivative(u, v) - Antiderivative(u - lb, v);
        }

        var (closest, distance) = Closest(a, b, la, eax, eaz);
        if (distance > 4 * (la + lb))
            return Gauss(Inner, 0, la, 6);
        double total = 0;
        if (closest > 0) total += Graded(Inner, closest, 0, distance);
        if (closest < la) total += Graded(Inner, closest, la, distance);
        return total;
    }

    /// <summary>∫₀ᵖ ln√(q² + v²) dq.</summary>
    private static double Antiderivative(double p, double v)
    {
        if (v < 1e-300)
            return p == 0 ? 0 : p * Math.Log(Math.Abs(p)) - p;
        return 0.5 * p * Math.Log(p * p + v * v) - p + v * Math.Atan(p / v);
    }

    /// <summary>The integral from <paramref name="near"/> to <paramref name="far"/>, in
    /// intervals growing geometrically away from <paramref name="near"/> (where the integrand's
    /// derivative may be logarithmically singular), down to a tenth of the segments' distance.</summary>
    private static double Graded(Func<double, double> f, double near, double far, double distance)
    {
        double length = Math.Abs(far - near), direction = Math.Sign(far - near);
        double floor = Math.Max(0.1 * distance, 1e-9 * length);
        double total = 0, outer = length;
        while (outer > floor)
        {
            double inner = 0.2 * outer;
            if (inner < floor) inner = 0;
            total += Gauss(f, near + direction * inner, near + direction * outer, 8);
            outer = inner;
            if (inner == 0) break;
        }
        if (outer > 0) total += Gauss(f, near, near + direction * outer, 8);
        return total;
    }

    private static double Gauss(Func<double, double> f, double from, double to, int points)
    {
        var (nodes, weights) = GaussLegendre.Rule(points, Math.Min(from, to), Math.Max(from, to));
        double sum = 0;
        for (int i = 0; i < nodes.Length; i++) sum += weights[i] * f(nodes[i]);
        return sum;
    }

    /// <summary>The parameter on a closest to segment b, and the distance there.</summary>
    private static (double S, double Distance) Closest(Segment a, Segment b, double la, double eax, double eaz)
    {
        double DistanceToB(double px, double pz)
        {
            double bx = b.X1 - b.X0, bz = b.Z1 - b.Z0;
            double t = Math.Clamp(((px - b.X0) * bx + (pz - b.Z0) * bz) / (bx * bx + bz * bz), 0, 1);
            double dx = px - (b.X0 + t * bx), dz = pz - (b.Z0 + t * bz);
            return Math.Sqrt(dx * dx + dz * dz);
        }
        var candidates = new List<double> { 0, la };
        foreach (var (px, pz) in new[] { (b.X0, b.Z0), (b.X1, b.Z1) })
            candidates.Add(Math.Clamp((px - a.X0) * eax + (pz - a.Z0) * eaz, 0, la));
        // Where the two lines cross.
        double ebx = b.X1 - b.X0, ebz = b.Z1 - b.Z0;
        double denominator = eax * ebz - eaz * ebx;
        if (Math.Abs(denominator) > 1e-300)
        {
            double s = ((b.X0 - a.X0) * ebz - (b.Z0 - a.Z0) * ebx) / denominator;
            candidates.Add(Math.Clamp(s, 0, la));
        }
        double best = 0, bestDistance = double.MaxValue;
        foreach (double s in candidates)
        {
            double d = DistanceToB(a.X0 + eax * s, a.Z0 + eaz * s);
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }
        return (best, bestDistance);
    }
}
