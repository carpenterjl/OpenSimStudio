namespace OpenSim.Pcb.Inductance;

/// <summary>
/// Partial mutual inductance of two PARALLEL rectangular bars carrying uniform current —
/// the PEEC cell kernel (Hoer &amp; Love 1965, NBS J. Res. 69C, 127; Ruehli 1972). One
/// kernel covers every parallel pair: self (the bar with itself), side-by-side, stacked
/// (a trace and its image), collinear adjoining, staggered and unequal sections — so a
/// straight bar's inductance cannot depend on how it is subdivided.
///
/// Geometry (Hoer–Love's): bar 1 occupies [0, a] × [0, b] × [0, l₁]; bar 2 occupies
/// [E, E+c] × [P, P+d] × [l₃, l₃+l₂]. The third axis is the common current direction;
/// (E, P, l₃) are CORNER-to-corner offsets.
///
/// Two evaluations of the same sixfold integral, chosen by conditioning:
///
/// 1. The CLOSED FORM — 64 signed evaluations of a degree-5 primitive. Its terms are
///    quartic in the coordinate differences, so rounding grows like ε·(L/s)⁴ with L the
///    largest extent or offset and s the smallest of the six box dimensions; compensated
///    summation cannot recover digits lost inside a primitive. It is used only inside
///    <see cref="ClosedFormMaxRatio"/> (derived against the quadrature in the tests) and
///    only when finite.
///
/// 2. The DIFFERENCE-DOMAIN QUADRATURE — the stable kernel for everything else, which
///    for printed copper (35 µm thick, millimetres long) is nearly every pair. Averaging
///    the exact parallel-filament kernel over both sections reduces exactly to a 2-D
///    integral over the section-difference vector δ,
///        M = ∬ K(δx)·K(δy)·M_fil(ρ = |δ|) dδx dδy,
///    with K the (trapezoidal) cross-correlation of the two section extents. No term
///    cancels against another, so it holds for any aspect ratio.
/// </summary>
internal static class RectangularBarKernel
{
    private const double Mu0Over4Pi = 1e-7;                  // µ₀/4π [H/m]

    /// <summary>
    /// Largest conditioning ratio (<see cref="ConditioningRatio"/>) at which the closed
    /// form is used. Derived, not chosen: the tests sweep self, side-by-side, stacked,
    /// diagonal, collinear, unequal-length and dimension-permuted pairs and find the
    /// largest ratio at which the closed form still agrees with the quadrature to 1e-8;
    /// the binding family is a pair far apart on all three axes (the inductance decays
    /// like 1/L while the primitives grow like L⁵), which first parts from the
    /// quadrature by 1e-8 at L/s ≈ 12. Inside 8 the worst disagreement found is 6e-10.
    /// Printed copper is 35 µm thick and millimetres long, so in practice this admits
    /// only stubby cells of a finely subdivided bar; everything else is quadrature.
    /// </summary>
    internal const double ClosedFormMaxRatio = 8;

    // ------------------------------------------------------------------
    // Regime selection
    // ------------------------------------------------------------------

    /// <summary>L/s: the largest of every extent and offset of the two boxes over the
    /// smallest of the six box dimensions (both lengths included — a bar is as
    /// ill-conditioned short-and-wide as it is long-and-thin).</summary>
    internal static double ConditioningRatio(double a, double b, double l1,
        double c, double d, double l2, double e, double p, double l3)
    {
        double largest = Math.Max(Math.Max(Math.Max(a, b), Math.Max(c, d)), Math.Max(l1, l2));
        largest = Math.Max(largest, Math.Max(Math.Abs(e), Math.Max(Math.Abs(p), Math.Abs(l3))));
        double smallest = Math.Min(Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Min(l1, l2));
        return largest / smallest;
    }

    /// <summary>Whether the closed form is certified for this pair.</summary>
    internal static bool ClosedFormIsCertified(double a, double b, double l1,
        double c, double d, double l2, double e, double p, double l3) =>
        ConditioningRatio(a, b, l1, c, d, l2, e, p, l3) <= ClosedFormMaxRatio;

    /// <summary>The mutual inductance [H] of the two bars, currents co-directed.</summary>
    internal static double Mutual(double a, double b, double l1,
        double c, double d, double l2, double e, double p, double l3)
    {
        if (!(a > 0 && b > 0 && l1 > 0 && c > 0 && d > 0 && l2 > 0))
            throw new ArgumentOutOfRangeException(nameof(a), "Bar dimensions must be positive.");
        if (!(double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(l1)
              && double.IsFinite(c) && double.IsFinite(d) && double.IsFinite(l2)
              && double.IsFinite(e) && double.IsFinite(p) && double.IsFinite(l3)))
            throw new ArgumentOutOfRangeException(nameof(e), "Bar dimensions and offsets must be finite.");

        if (ClosedFormIsCertified(a, b, l1, c, d, l2, e, p, l3))
        {
            double closed = ClosedForm(a, b, l1, c, d, l2, e, p, l3);
            if (double.IsFinite(closed))
                return closed;
        }
        return Quadrature(a, b, l1, c, d, l2, e, p, l3);
    }

    // ------------------------------------------------------------------
    // Regime 1 — Hoer–Love closed form
    //
    //   M = (µ₀/4π)/(abcd) · Σ_{i,j,k=1..4} (−1)^(i+j+k+1) f(q_i, r_j, s_k)
    //   q = {E−a, E+c−a, E+c, E},  r = {P−b, P+d−b, P+d, P},
    //   s = {l₃−l₁, l₃+l₂−l₁, l₃+l₂, l₃}.
    // ------------------------------------------------------------------

    internal static double ClosedForm(double a, double b, double l1,
        double c, double d, double l2, double e, double p, double l3)
    {
        Span<double> q = stackalloc double[] { e - a, e + c - a, e + c, e };
        Span<double> r = stackalloc double[] { p - b, p + d - b, p + d, p };
        Span<double> s = stackalloc double[] { l3 - l1, l3 + l2 - l1, l3 + l2, l3 };

        var sum = new CompensatedSum();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                {
                    // (−1)^(i+j+k+1) with 1-based indices = −(−1)^(i+j+k) with 0-based.
                    double sign = ((i + j + k) & 1) == 0 ? 1.0 : -1.0;
                    AddPrimitive(ref sum, sign, q[i], r[j], s[k]);
                }
        return Mu0Over4Pi * sum.Value / (a * b * c * d);
    }

    /// <summary>Hoer–Love's primitive f(x, y, z) — the sixfold antiderivative of 1/r.</summary>
    internal static double Primitive(double x, double y, double z)
    {
        var sum = new CompensatedSum();
        AddPrimitive(ref sum, 1.0, x, y, z);
        return sum.Value;
    }

    private static void AddPrimitive(ref CompensatedSum sum, double sign, double x, double y, double z)
    {
        double x2 = x * x, y2 = y * y, z2 = z * z;
        double r = Math.Sqrt(x2 + y2 + z2);

        sum.Add(sign * (y2 * z2 / 4 - y2 * y2 / 24 - z2 * z2 / 24) * LogTerm(x, y2 + z2));
        sum.Add(sign * (x2 * z2 / 4 - x2 * x2 / 24 - z2 * z2 / 24) * LogTerm(y, x2 + z2));
        sum.Add(sign * (x2 * y2 / 4 - x2 * x2 / 24 - y2 * y2 / 24) * LogTerm(z, x2 + y2));
        sum.Add(sign * (x2 * x2 + y2 * y2 + z2 * z2 - 3 * x2 * y2 - 3 * y2 * z2 - 3 * z2 * x2) * r / 60);
        sum.Add(-sign * x * y * z * z2 / 6 * PrincipalAtan(x * y, z * r));
        sum.Add(-sign * x * y * y2 * z / 6 * PrincipalAtan(x * z, y * r));
        sum.Add(-sign * x * x2 * y * z / 6 * PrincipalAtan(y * z, x * r));
    }

    /// <summary>
    /// p·ln((p + r)/ρ) with r = √(p² + ρ²), evaluated as p·asinh(p/ρ) — the same
    /// function, but the literal form rounds r − ρ to zero for |p| ≪ ρ and returns ln 0
    /// against a non-zero coefficient (two unit boxes a hair apart come out NaN). The
    /// ρ → 0 limit is 0: the coefficient multiplying it vanishes like ρ⁴.
    /// </summary>
    private static double LogTerm(double p, double rhoSquared)
    {
        if (rhoSquared <= 0) return 0;
        return p * Math.Asinh(p / Math.Sqrt(rhoSquared));
    }

    /// <summary>
    /// The paper's PRINCIPAL-branch atan(numerator/denominator) — deliberately not
    /// atan2, which shifts every negative-denominator term by π and leaves a finite,
    /// wrong sum (the unit cube comes out 334 % high). A zero denominator takes the
    /// ±π/2 limit; its prefactor carries the vanishing coordinate, so the term is 0.
    /// </summary>
    private static double PrincipalAtan(double numerator, double denominator)
    {
        if (numerator == 0) return 0;
        if (denominator == 0) return numerator > 0 ? Math.PI / 2 : -Math.PI / 2;
        return Math.Atan(numerator / denominator);
    }

    /// <summary>Neumaier's compensated sum: keeps the 448 signed terms from losing
    /// more than the primitives themselves already have.</summary>
    private struct CompensatedSum
    {
        private double _sum, _compensation;

        public void Add(double value)
        {
            double t = _sum + value;
            _compensation += Math.Abs(_sum) >= Math.Abs(value)
                ? (_sum - t) + value
                : (value - t) + _sum;
            _sum = t;
        }

        public readonly double Value => _sum + _compensation;
    }

    // ------------------------------------------------------------------
    // Regime 2 — difference-domain quadrature
    //
    // With A on [0, l₁] and B on [l₃, l₃+l₂] at perpendicular distance ρ, the filament
    // kernel is M_fil = (µ₀/4π)·G(ρ),
    //   G(ρ) = Ψ(l₃+l₂) + Ψ(l₁−l₃) − Ψ(l₃) − Ψ(l₁−l₃−l₂),
    //   Ψ(u) = |u|·asinh(|u|/ρ) − u²/(ρ + √(u²+ρ²)).
    // Ψ is FilamentMutual's Φ(u) = u·asinh(u/ρ) − √(u²+ρ²) plus ρ; the four ρ cancel
    // identically, and writing √(u²+ρ²) − ρ as u²/(ρ + √) keeps a short wide bar (ρ up
    // to a metre, u of micrometres) from losing eleven digits.
    //
    // G is analytic in δ except at ρ = 0 — a logarithm when the bars overlap along
    // their length, a cone when two ends align — and, for a collinear pair with a gap,
    // at the complex points ρ = ±i·|u|. So the distance from a cell to the nearest
    // singularity is known a priori: √(dist(cell, 0)² + u_min²). Cells are bisected
    // toward the origin until that distance covers the cell, and each cell takes the
    // Gauss–Legendre order its Bernstein ellipse calls for: eight points beside the
    // singularity, two or three far from it. Far pairs cost a few dozen evaluations;
    // no separate far-field approximation exists to be wrong.
    // ------------------------------------------------------------------

    /// <summary>Per-cell truncation target of the order rule.</summary>
    private const double CellTolerance = 1e-12;

    /// <summary>Highest per-direction order before a cell is bisected instead.</summary>
    private const int MaxOrder = 8;

    /// <summary>Bisection depth toward a singular origin. The last corner cell is
    /// 2^-22 of the support per side; its logarithm integrates to ≈ 1e-12 of the
    /// total at most, and it is still integrated, not dropped.</summary>
    private const int MaxDepth = 22;

    /// <summary>Breakpoints closer than this fraction of the support are one point — a
    /// centre offset of rounding size must not open a sliver cell.</summary>
    private const double SnapFraction = 1e-12;

    private static readonly (double[] Nodes, double[] Weights)[] Rules = BuildRules(2 * MaxOrder);

    /// <summary>
    /// The quadrature itself. <paramref name="orderScale"/> multiplies every cell's
    /// order (tests pass 2 to show the production rule has converged).
    /// </summary>
    internal static double Quadrature(double a, double b, double l1,
        double c, double d, double l2, double e, double p, double l3, int orderScale = 1)
    {
        if (orderScale < 1 || orderScale * MaxOrder > Rules.Length - 1)
            throw new ArgumentOutOfRangeException(nameof(orderScale));

        var integrator = new Integrator(a, b, l1, c, d, l2, e, p, l3, orderScale);
        double result = Mu0Over4Pi * integrator.Integrate();
        if (!double.IsFinite(result))
            throw new InvalidOperationException(
                "The bar-pair inductance integral did not evaluate to a finite value " +
                $"(sections {a:g4}×{b:g4} and {c:g4}×{d:g4} m, lengths {l1:g4} and {l2:g4} m).");
        return result;
    }

    private sealed class Integrator
    {
        private readonly double _a, _b, _c, _d, _e, _p;
        private readonly double _u0, _u1, _u2, _u3;          // |u| of the four Ψ terms (+, +, −, −)
        private readonly double _uMin;
        private readonly int _orderScale;
        private double _xMinStep, _yMinStep;
        private double _total;

        public Integrator(double a, double b, double l1, double c, double d, double l2,
            double e, double p, double l3, int orderScale)
        {
            _a = a; _b = b; _c = c; _d = d; _e = e; _p = p;
            _orderScale = orderScale;
            _u0 = Math.Abs(l3 + l2);
            _u1 = Math.Abs(l1 - l3);
            _u2 = Math.Abs(l3);
            _u3 = Math.Abs(l1 - l3 - l2);

            // Longitudinal overlap ⇒ logarithm at ρ = 0. Without overlap the nearest
            // singularity is |u|_min off the real axis (0 when two ends align: the cone).
            double overlap = Math.Min(l1, l3 + l2) - Math.Max(0, l3);
            _uMin = overlap > 1e-14 * Math.Max(l1, l2)
                ? 0
                : Math.Min(Math.Min(_u0, _u1), Math.Min(_u2, _u3));
        }

        public double Integrate()
        {
            var (xs, xFold) = Breakpoints(_e - _a, _e + _c - _a, _e + _c, _e);
            var (ys, yFold) = Breakpoints(_p - _b, _p + _d - _b, _p + _d, _p);
            _xMinStep = (xs[^1] - xs[0]) * Math.Pow(2, -MaxDepth);
            _yMinStep = (ys[^1] - ys[0]) * Math.Pow(2, -MaxDepth);

            _total = 0;
            for (int i = 0; i + 1 < xs.Count; i++)
                for (int j = 0; j + 1 < ys.Count; j++)
                    Cell(xs[i], xs[i + 1], ys[j], ys[j + 1]);
            return _total * xFold * yFold;
        }

        /// <summary>
        /// The kernel's kinks (and 0, when the support straddles it) in ascending
        /// order. A support symmetric about 0 — centred sections: self, collinear and
        /// stacked pairs — is folded onto its positive half and counted twice.
        /// </summary>
        private static (List<double> Points, double Fold) Breakpoints(double q1, double q2, double q3, double q4)
        {
            Span<double> sorted = stackalloc double[] { q1, q2, q3, q4 };
            sorted.Sort();
            double snap = SnapFraction * (sorted[3] - sorted[0]);

            bool symmetric = Math.Abs(sorted[0] + sorted[3]) <= snap
                             && Math.Abs(sorted[1] + sorted[2]) <= snap;
            var points = new List<double>(5);
            if (symmetric)
            {
                points.Add(0);
                for (int i = 2; i < 4; i++)
                    if (sorted[i] - points[^1] > snap) points.Add(sorted[i]);
                return (points, 2);
            }

            points.Add(sorted[0]);
            for (int i = 1; i < 4; i++)
                if (sorted[i] - points[^1] > snap) points.Add(sorted[i]);
            if (points[0] < 0 && points[^1] > 0)
            {
                int at = 0;
                while (points[at] < 0) at++;                   // first point at or above 0
                if (points[at] <= snap) points[at] = 0;        // a kink within rounding of 0
                else if (-points[at - 1] <= snap) points[at - 1] = 0;
                else points.Insert(at, 0);
            }
            return (points, 1);
        }

        private void Cell(double x0, double x1, double y0, double y1)
        {
            double hx = x1 - x0, hy = y1 - y0;
            double nearX = x0 <= 0 && x1 >= 0 ? 0 : Math.Min(Math.Abs(x0), Math.Abs(x1));
            double nearY = y0 <= 0 && y1 >= 0 ? 0 : Math.Min(Math.Abs(y0), Math.Abs(y1));
            double clearance = Math.Sqrt(nearX * nearX + nearY * nearY + _uMin * _uMin);

            // A cell is resolved once the singularity is at least a cell away; an
            // elongated cell is cut across its long side only.
            bool splitX = hx > clearance && hx > _xMinStep;
            bool splitY = hy > clearance && hy > _yMinStep;
            if (splitX && splitY)
            {
                if (hx >= 2 * hy) splitY = false;
                else if (hy >= 2 * hx) splitX = false;
            }
            if (splitX || splitY)
            {
                double xm = 0.5 * (x0 + x1), ym = 0.5 * (y0 + y1);
                if (splitX && splitY)
                {
                    Cell(x0, xm, y0, ym);
                    Cell(x0, xm, ym, y1);
                    Cell(xm, x1, y0, ym);
                    Cell(xm, x1, ym, y1);
                }
                else if (splitX)
                {
                    Cell(x0, xm, y0, y1);
                    Cell(xm, x1, y0, y1);
                }
                else
                {
                    Cell(x0, x1, y0, ym);
                    Cell(x0, x1, ym, y1);
                }
                return;
            }

            var (xNodes, xWeights) = Rules[Order(clearance, hx)];
            var (yNodes, yWeights) = Rules[Order(clearance, hy)];
            double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);
            double sum = 0;
            for (int i = 0; i < xNodes.Length; i++)
            {
                double x = xc + 0.5 * hx * xNodes[i];
                double kx = Correlation(x, _a, _c, _e);
                if (kx <= 0) continue;
                double row = 0;
                for (int j = 0; j < yNodes.Length; j++)
                {
                    double y = yc + 0.5 * hy * yNodes[j];
                    double ky = Correlation(y, _b, _d, _p);
                    if (ky <= 0) continue;
                    row += yWeights[j] * ky * Kernel(Math.Sqrt(x * x + y * y));
                }
                sum += xWeights[i] * kx * row;
            }
            _total += 0.25 * hx * hy * sum;
        }

        /// <summary>
        /// Gauss–Legendre order for an interval of length <paramref name="h"/> whose
        /// integrand's nearest singularity is <paramref name="clearance"/> away: the
        /// error decays like ϱ^(−2n) with ϱ = β + √(β²+1), β = 2·clearance/h (the
        /// Bernstein ellipse through a singularity abreast of the interval — the
        /// conservative placement).
        /// </summary>
        private int Order(double clearance, double h)
        {
            int n = MaxOrder;
            if (clearance > 0)
            {
                double beta = 2 * clearance / h;
                double bernstein = beta + Math.Sqrt(beta * beta + 1);
                double needed = -Math.Log(CellTolerance) / (2 * Math.Log(bernstein));
                if (needed < MaxOrder)
                    n = Math.Max(2, (int)Math.Ceiling(needed));
            }
            return n * _orderScale;
        }

        /// <summary>Normalised cross-correlation of the extents [0, first] and
        /// [offset, offset + second] at difference <paramref name="delta"/>: the
        /// density of the section-difference coordinate (integrates to 1).</summary>
        private static double Correlation(double delta, double first, double second, double offset)
        {
            double overlap = Math.Min(first, offset + second - delta) - Math.Max(0, offset - delta);
            return overlap > 0 ? overlap / (first * second) : 0;
        }

        private double Kernel(double rho)
        {
            if (rho <= 0) return 0;                            // measure-zero; never a Gauss node
            return Psi(_u0, rho) + Psi(_u1, rho) - Psi(_u2, rho) - Psi(_u3, rho);
        }

        private static double Psi(double u, double rho)
        {
            if (u <= 0) return 0;
            return u * Math.Asinh(u / rho) - u * u / (rho + Math.Sqrt(u * u + rho * rho));
        }
    }

    /// <summary>Gauss–Legendre nodes and weights on [−1, 1] for orders 1..max (index =
    /// order), by Newton iteration on the Legendre recurrence.</summary>
    private static (double[] Nodes, double[] Weights)[] BuildRules(int max)
    {
        var rules = new (double[] Nodes, double[] Weights)[max + 1];
        rules[0] = (Array.Empty<double>(), Array.Empty<double>());
        for (int n = 1; n <= max; n++)
        {
            var nodes = new double[n];
            var weights = new double[n];
            for (int i = 0; i < (n + 1) / 2; i++)
            {
                double x = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
                double derivative = 0;
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    double p0 = 1, p1 = x;
                    for (int k = 2; k <= n; k++)
                    {
                        double pk = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
                        p0 = p1;
                        p1 = pk;
                    }
                    derivative = n * (x * p1 - p0) / (x * x - 1);
                    double step = p1 / derivative;
                    x -= step;
                    if (Math.Abs(step) < 1e-15) break;
                }
                nodes[i] = -x;
                nodes[n - 1 - i] = x;
                weights[i] = weights[n - 1 - i] = 2 / ((1 - x * x) * derivative * derivative);
            }
            if (n % 2 == 1) nodes[n / 2] = 0;
            rules[n] = (nodes, weights);
        }
        return rules;
    }
}
