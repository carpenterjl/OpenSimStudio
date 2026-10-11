using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-37 — the heat an impedance ground takes from the sheet currents of a layered solve:
/// ½Re(Z_s)·∫|H_t|² over the whole plane, H_t being the ground's surface current.
///
/// <para>By Parseval, ∫|H_t|² dA = (1/4π²)∫∫|Ĥ_t(k)|² d²k, and the ground field of a horizontal
/// current J̃ is read straight off the two solves of the transmission-line Green's function
/// (<see cref="TransmissionLineGreens.GroundAmplitudes"/>): with H = ∇×A/µ₀, A_t = Ĝ·J̃ and
/// A_z = −j(k⃗·J̃)·ã,</para>
/// <code>
///   Ĥ_t(0) = [(∂_zĜ + k_ρ²ã)·J̃_∥·(ẑ × k̂) − ∂_zĜ·J̃_⊥·k̂] / µ₀,
/// </code>
/// <para>where the table's spectral kernels are Sommerfeld-normalized, twice the plain transform
/// (the TE source jump is −2µ₀), so Ĝ = G̃/2. Hence</para>
/// <code>
///   P = Re(Z_s)/(32π²µ₀²) · ∫ k_ρ dk_ρ [ |∂_zG̃ + k_ρ²ã|²·∮|J̃_∥|²dα + |∂_zG̃|²·∮|J̃_⊥|²dα ].
/// </code>
/// <para>Over air between the current and a perfect ground this is |J̃|²·|e^{−jk_z h}|² — the
/// image current — and that is the check the form was derived against. The surface waves the
/// ground guides decay into it, so their power is in this integral too (as Lorentzians of width
/// |Im k_p| about each pole, which the radial panels are graded onto): on a lossy ground the
/// ledger is P_in = P_rad + P_ground (+ heat in the sheet, + the dielectric's), with no separate
/// surface-wave term.</para>
///
/// <para>The current transform is EXACT for the RWG currents — a linear field on each triangle,
/// whose transform is a divided difference of the exponential (Hermite–Genocchi) — because the
/// integral reaches k_ρ of a few over the substrate height, where a point rule on the mesh's
/// triangles no longer resolves e^{jk⃗·r⃗}, and the ledger closes only on the transform of the very
/// current the solve has.</para>
/// </summary>
public static class GroundPlaneLoss
{
    /// <summary>The radial integral stops where the ground field has decayed by e^{−TailDecay}
    /// (e^{−2k_ρ h} from a source h above it).</summary>
    private const double TailDecay = 18;

    /// <summary>J̃ evaluations (triangles × azimuths × radial nodes) above which the integral is
    /// not attempted: the cost grows as (size/height)², and a board-sized sheet on a thin
    /// substrate would take hours.</summary>
    public const double MaxWork = 4e9;

    /// <summary>Heat in the impedance ground [W] for the solved sheet currents; 0 on a perfect
    /// ground. Null when the integral would cost more than <see cref="MaxWork"/>.</summary>
    public static double? Watts(SurfaceStructure surface, MultiLayerKernelTable kernel, SurfaceMomSolution solution)
        => Watts(surface, kernel, solution.EdgeCurrents);

    internal static double? Watts(SurfaceStructure surface, MultiLayerKernelTable kernel, IReadOnlyList<Complex> edgeCurrents)
    {
        var stackup = kernel.Stackup;
        if (!stackup.HasLossyGround) return 0;
        if (kernel.IsShielded || kernel.IsUngrounded)
            throw new ArgumentException("Ground loss is defined for an open stack on a ground.", nameof(kernel));
        int m = kernel.SourceInterface ?? stackup.Layers.Count - 1;
        double height = stackup.InterfaceHeights()[m];
        double k0 = kernel.K0, omega = 2 * Math.PI * kernel.FrequencyHz;
        double resistance = stackup.GroundSurfaceImpedance(omega).Real;
        var sheet = new SheetCurrent(surface, edgeCurrents);

        var nodes = RadialNodes(stackup, k0, kernel.Poles, sheet.Diameter, height);
        double work = nodes.Sum(n => (double)AzimuthCount(n.KRho, sheet.Diameter)) * sheet.TriangleCount;
        if (work > MaxWork) return null;

        var values = new double[nodes.Count];
        Parallel.For(0, nodes.Count, i =>
        {
            var (kRho, kz0, _) = nodes[i];
            var (dz, az) = TransmissionLineGreens.GroundAmplitudes(stackup, k0, kRho, kz0, m);
            Complex parallelFactor = dz + kRho * kRho * az;
            int count = AzimuthCount(kRho, sheet.Diameter);
            double along = 0, across = 0;
            for (int a = 0; a < count; a++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * a / count);
                var (jx, jy) = sheet.Transform(kRho * cos, kRho * sin);
                Complex jPar = cos * jx + sin * jy, jPerp = -sin * jx + cos * jy;
                along += jPar.Real * jPar.Real + jPar.Imaginary * jPar.Imaginary;
                across += jPerp.Real * jPerp.Real + jPerp.Imaginary * jPerp.Imaginary;
            }
            double dAlpha = 2 * Math.PI / count;
            values[i] = kRho * dAlpha * (Square(parallelFactor) * along + Square(dz) * across);
        });
        double sum = 0;
        for (int i = 0; i < nodes.Count; i++) sum += nodes[i].Weight * values[i];
        return resistance / (32 * Math.PI * Math.PI * RfConstants.Mu0 * RfConstants.Mu0) * sum;
    }

    private static double Square(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

    /// <summary>Azimuths for the trapezoid on ∮|J̃|²dα. Each point r of the sheet contributes
    /// e^{jk_ρ r cos(α − θ)} = Σ jⁿJ_n(k_ρ r)e^{jn(α − θ)}, negligible beyond n ≈ k_ρ r; so |J̃|², a
    /// translation-invariant product of two such sums, is a trigonometric polynomial of degree about
    /// k_ρ·(the sheet's diameter), which a trapezoid of more points than that integrates exactly.</summary>
    private static int AzimuthCount(double kRho, double diameter) => 48 + (int)Math.Ceiling(1.1 * kRho * diameter);

    /// <summary>The radial rule: k_ρ = k₀ sin t on [0, k₀] (the square root at k₀ is then smooth),
    /// k_ρ = √(k₀² + s²) beyond, broken at every pole's real part with panels graded geometrically
    /// out from |Im k_p| so each Lorentzian is resolved, and panels no wider than π/(diameter) — the
    /// period |J̃|² oscillates on — or half the height.</summary>
    private static List<(double KRho, Complex Kz0, double Weight)> RadialNodes(LayeredStackup stackup,
        double k0, IReadOnlyList<SurfaceWavePole> poles, double diameter, double height)
    {
        var (gauss, gaussWeights) = GaussLegendre.Rule(8, -1, 1);
        var nodes = new List<(double, Complex, double)>();
        double width = Math.Min(Math.PI / Math.Max(diameter, 1e-12), 0.5 / height);

        int first = 4 + (int)Math.Ceiling(k0 / width);
        for (int p = 0; p < first; p++)
        {
            double t0 = Math.PI / 2 * p / first, t1 = Math.PI / 2 * (p + 1) / first;
            double mid = 0.5 * (t0 + t1), half = 0.5 * (t1 - t0);
            for (int i = 0; i < gauss.Length; i++)
            {
                var (sin, cos) = Math.SinCos(mid + half * gauss[i]);
                nodes.Add((k0 * sin, new Complex(k0 * cos, 0), gaussWeights[i] * half * k0 * cos));
            }
        }

        double kMax = k0 * Math.Sqrt(stackup.Layers.Max(l => l.RelativePermittivity));
        double sMax = Math.Sqrt(Math.Pow(kMax + TailDecay / (2 * height), 2) - k0 * k0);
        var breaks = new SortedSet<double> { 0, sMax };
        foreach (var pole in poles)
        {
            double re = pole.KRho.Real;
            if (re <= k0) continue;
            double sp = Math.Sqrt(re * re - k0 * k0);
            if (sp >= sMax) continue;
            breaks.Add(sp);
            // The Lorentzian's width in s: dk_ρ = (s/k_ρ) ds.
            double w = Math.Max(Math.Abs(pole.KRho.Imaginary) * re / sp, 1e-12 * k0);
            for (double step = w; step < width; step *= 2)
            {
                if (sp - step > 0) breaks.Add(sp - step);
                if (sp + step < sMax) breaks.Add(sp + step);
            }
        }
        double previous = double.NaN;
        foreach (double b in breaks)
        {
            if (!double.IsNaN(previous))
            {
                int panels = Math.Max(1, (int)Math.Ceiling((b - previous) / width));
                for (int p = 0; p < panels; p++)
                {
                    double s0 = previous + (b - previous) * p / panels, s1 = previous + (b - previous) * (p + 1) / panels;
                    double mid = 0.5 * (s0 + s1), half = 0.5 * (s1 - s0);
                    for (int i = 0; i < gauss.Length; i++)
                    {
                        double s = mid + half * gauss[i];
                        double kRho = Math.Sqrt(k0 * k0 + s * s);
                        nodes.Add((kRho, new Complex(0, -s), gaussWeights[i] * half * s / kRho));
                    }
                }
            }
            previous = b;
        }
        return nodes;
    }

    /// <summary>The sheet current as one linear field per triangle, J(r) = α·r + β (the RWG sum),
    /// and its exact 2D transform ∫J e^{jk⃗·r⃗} dA — the convention of the far field's transform.</summary>
    private sealed class SheetCurrent
    {
        private readonly double[] _x, _y;           // 3 per triangle, relative to its centroid
        private readonly double[] _cx, _cy, _area;
        private readonly Complex[] _alpha, _betaX, _betaY;
        public int TriangleCount => _area.Length;
        public double Diameter { get; }

        public SheetCurrent(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents)
        {
            var kept = new List<int>();
            for (int t = 0; t < surface.Triangles.Count; t++)
                if (surface.TriangleSupports[t].Count > 0) kept.Add(t);
            int n = kept.Count;
            _x = new double[3 * n]; _y = new double[3 * n];
            _cx = new double[n]; _cy = new double[n]; _area = new double[n];
            _alpha = new Complex[n]; _betaX = new Complex[n]; _betaY = new Complex[n];
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                int t = kept[k];
                var (ia, ib, ic) = surface.Triangles[t];
                var va = surface.Vertices[ia]; var vb = surface.Vertices[ib]; var vc = surface.Vertices[ic];
                double cx = (va.X + vb.X + vc.X) / 3, cy = (va.Y + vb.Y + vc.Y) / 3;
                _cx[k] = cx; _cy[k] = cy;
                _x[3 * k] = va.X - cx; _x[3 * k + 1] = vb.X - cx; _x[3 * k + 2] = vc.X - cx;
                _y[3 * k] = va.Y - cy; _y[3 * k + 1] = vb.Y - cy; _y[3 * k + 2] = vc.Y - cy;
                double area = surface.TriangleAreas[t];
                _area[k] = area;
                // J = Σ c·(r − r_opposite), c = I·sign·ℓ/(2A): in centroid coordinates α·r + β.
                Complex alpha = Complex.Zero, bx = Complex.Zero, by = Complex.Zero;
                foreach (var (basis, sign, opposite) in surface.TriangleSupports[t])
                {
                    Complex c = edgeCurrents[basis] * (sign * surface.Edges[basis].Length / (2 * area));
                    alpha += c;
                    bx -= c * (surface.Vertices[opposite].X - cx);
                    by -= c * (surface.Vertices[opposite].Y - cy);
                }
                _alpha[k] = alpha; _betaX[k] = bx; _betaY[k] = by;
                foreach (var v in new[] { va, vb, vc })
                {
                    minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                    minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                }
            }
            Diameter = n == 0 ? 0 : Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        }

        /// <summary>Σ over triangles of ∫_T (α·r + β) e^{jk⃗·r⃗} dA. With λ the barycentric
        /// coordinates and x_i = jk⃗·r⃗_i, ∫_T e^{jk⃗·r⃗} = 2A·e[x₁,x₂,x₃] and ∫_T λ_i e^{jk⃗·r⃗} =
        /// 2A·e[x₁,x₂,x₃,x_i] (Hermite–Genocchi, and ∂/∂x_i of a divided difference repeats x_i).</summary>
        public (Complex Jx, Complex Jy) Transform(double kx, double ky)
        {
            Complex jx = Complex.Zero, jy = Complex.Zero;
            Span<Complex> d = stackalloc Complex[4];
            for (int k = 0; k < _area.Length; k++)
            {
                var (sinC, cosC) = Math.SinCos(kx * _cx[k] + ky * _cy[k]);
                Complex centre = new(cosC, sinC);
                Complex y1 = new(0, kx * _x[3 * k] + ky * _y[3 * k]);
                Complex y2 = new(0, kx * _x[3 * k + 1] + ky * _y[3 * k + 1]);
                Complex y3 = new(0, kx * _x[3 * k + 2] + ky * _y[3 * k + 2]);
                ExpDividedDifferences(y1, y2, y3, d);
                double twiceArea = 2 * _area[k];
                Complex i0 = twiceArea * d[0];
                Complex i1 = twiceArea * d[1], i2 = twiceArea * d[2], i3 = twiceArea * d[3];
                // ∫ r e^{…} in centroid coordinates: Σ r_i·I_i.
                Complex rx = _x[3 * k] * i1 + _x[3 * k + 1] * i2 + _x[3 * k + 2] * i3;
                Complex ry = _y[3 * k] * i1 + _y[3 * k + 1] * i2 + _y[3 * k + 2] * i3;
                jx += centre * (_alpha[k] * rx + _betaX[k] * i0);
                jy += centre * (_alpha[k] * ry + _betaY[k] * i0);
            }
            return (jx, jy);
        }
    }

    /// <summary>
    /// e[y₁,y₂,y₃] and e[y₁,y₂,y₃,y_i] (i = 1, 2, 3) — divided differences of the exponential —
    /// as entries of exp(N), N the 6×6 upper-bidiagonal matrix with diagonal (y₁,y₂,y₃,y₁,y₂,y₃)
    /// and ones above it: exp(N)[i, j] is the divided difference over the diagonal entries i…j
    /// (Opitz). Scaling and squaring with a Taylor series keeps it exact for confluent and for
    /// well-separated points alike, where the textbook recursion cancels.
    /// </summary>
    internal static void ExpDividedDifferences(Complex y1, Complex y2, Complex y3, Span<Complex> result)
    {
        Complex d12 = y1 - y2, d13 = y1 - y3, d23 = y2 - y3;
        if (d12.Magnitude >= Separated && d13.Magnitude >= Separated && d23.Magnitude >= Separated)
        {
            // Distinct points: e[a,b,c] = Σ e^a/((a−b)(a−c)), and e[a,b,c,a] = ∂/∂a of it. With the
            // points at least ¼ apart the terms exceed the result by at most ~4³, two digits.
            Complex e1 = Complex.Exp(y1), e2 = Complex.Exp(y2), e3 = Complex.Exp(y3);
            Complex t1 = e1 / (d12 * d13), t2 = e2 / (-d12 * d23), t3 = e3 / (d13 * d23);
            result[0] = t1 + t2 + t3;
            result[1] = t1 * (1 - 1 / d12 - 1 / d13) + t2 / -d12 + t3 / -d13;
            result[2] = t1 / d12 + t2 * (1 + 1 / d12 - 1 / d23) + t3 / -d23;
            result[3] = t1 / d13 + t2 / d23 + t3 * (1 + 1 / d13 + 1 / d23);
            return;
        }
        if (Math.Max(Math.Max(y1.Magnitude, y2.Magnitude), y3.Magnitude) <= SmallPoints)
        {
            // Small points: e[x₁…x_n] = Σ_k h_k(x)/(k + n − 1)!, h_k the complete homogeneous
            // symmetric polynomials, built by h_k(x…, x_m) = h_k(x…) + x_m·h_{k−1}(x…, x_m).
            // h_k of four points is at most C(k+3, 3)·1.5^k: the k = 20 term is below 1e-16.
            Complex a = 1, b = 1, c = 1, da = 1, db = 1, dc = 1;
            double f3 = 2, f4 = 6;
            Complex s3 = 0.5, sa = 1.0 / 6, sb = 1.0 / 6, sc = 1.0 / 6;
            for (int k = 1; k <= 20; k++)
            {
                a *= y1;
                b = a + y2 * b;
                c = b + y3 * c;
                da = c + y1 * da;
                db = c + y2 * db;
                dc = c + y3 * dc;
                f3 *= k + 2;
                f4 *= k + 3;
                s3 += c / f3;
                sa += da / f4; sb += db / f4; sc += dc / f4;
            }
            result[0] = s3; result[1] = sa; result[2] = sb; result[3] = sc;
            return;
        }
        ExpDividedDifferencesByMatrix(y1, y2, y3, result);
    }

    /// <summary>Points closer than this go to the series or the matrix exponential.</summary>
    private const double Separated = 0.25;

    /// <summary>Points all within this of the origin take the series.</summary>
    private const double SmallPoints = 1.5;

    internal static void ExpDividedDifferencesByMatrix(Complex y1, Complex y2, Complex y3, Span<Complex> result)
    {
        const int n = 6;
        double norm = Math.Max(Math.Max(y1.Magnitude, y2.Magnitude), y3.Magnitude) + 1;
        int squarings = Math.Max(0, (int)Math.Ceiling(Math.Log2(2 * norm)));
        double scale = 1.0 / (1 << squarings);
        Span<Complex> diag = stackalloc Complex[n] { y1, y2, y3, y1, y2, y3 };
        // Upper-triangular storage, row-major full 6×6 for simplicity.
        Span<Complex> a = stackalloc Complex[n * n];
        Span<Complex> term = stackalloc Complex[n * n];
        Span<Complex> next = stackalloc Complex[n * n];
        Span<Complex> sum = stackalloc Complex[n * n];
        a.Clear();
        for (int i = 0; i < n; i++)
        {
            a[i * n + i] = diag[i] * scale;
            if (i + 1 < n) a[i * n + i + 1] = scale;
        }
        // Taylor: Σ a^k/k!, ‖a‖ ≤ ½ ⇒ 14 terms reach 1e-17.
        sum.Clear(); term.Clear();
        for (int i = 0; i < n; i++) { sum[i * n + i] = 1; term[i * n + i] = 1; }
        for (int k = 1; k <= 14; k++)
        {
            UpperMultiply(term, a, next, n);
            for (int i = 0; i < n * n; i++) term[i] = next[i] / k;
            for (int i = 0; i < n * n; i++) sum[i] += term[i];
        }
        for (int s = 0; s < squarings; s++)
        {
            UpperMultiply(sum, sum, next, n);
            next.CopyTo(sum);
        }
        result[0] = sum[0 * n + 2];
        result[1] = sum[0 * n + 3];
        result[2] = sum[1 * n + 4];
        result[3] = sum[2 * n + 5];
    }

    private static void UpperMultiply(ReadOnlySpan<Complex> p, ReadOnlySpan<Complex> q, Span<Complex> r, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                Complex s = Complex.Zero;
                for (int k = i; k <= j; k++) s += p[i * n + k] * q[k * n + j];
                r[i * n + j] = s;
            }
        for (int i = 1; i < n; i++)
            for (int j = 0; j < i; j++) r[i * n + j] = Complex.Zero;
    }
}
