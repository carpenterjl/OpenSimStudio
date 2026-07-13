using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The multi-layer quasi-static image series — the k_ρ → ∞ asymptote of the layered
/// kernels that <see cref="SommerfeldIntegrator"/> subtracts so the remaining Sommerfeld
/// integral is regular at ρ = 0 and decays algebraically. It generalizes the single-slab
/// two-term extraction (primary c₀ = 2/(ε_c+1) at ρ, one image c₁ = −4ε_c/(ε_c+1)² at 2d)
/// to a stackup: a geometrically-decaying series of dynamic images at depths that are
/// sums of round-trip layer thicknesses.
///
/// <para><b>G̃_A (TE)</b> is ε-INDEPENDENT in this limit: with k_z,i → −jk_ρ for every
/// layer, A_x solves the grounded-half-space Laplace problem regardless of the dielectric,
/// so its image set stays exactly the single-slab (1 at depth 0, −1 at depth 2·d_total) —
/// primary plus the PEC ground image.</para>
///
/// <para><b>K̃_Φ</b> is the layered electrostatic potential Green's function. In the
/// transmission-line picture the potential is a line of characteristic admittance Y_i ∝ ε_i,
/// SHORTED at the PEC ground (V = 0) and loaded above by the air half-space (Y₀ ∝ 1). The
/// static kernel is K̃_Φ,∞(k_ρ) = (2/ε₀)·κG̃/κ with κG̃ = B/(1 + B), B = (1/ε_top)(1+Γ)/(1−Γ)
/// the normalized input admittance looking down the stack and Γ the bottom-up reflection
/// (ground = short). Expanded in the layer phases E_i = e^{−k_ρ t_i} this is a series
/// Σ c_m e^{−k_ρ D_m}/k_ρ, D_m = 2·Σ n_i t_i, whose first two terms for one slab are exactly
/// (c₀, c₁) — the whole method is that identity, generalized. Each term promotes to a
/// DYNAMIC image c_m·e^{−jk_z0 D_m}/(jk_z0) (spatial c_m·g(√(ρ²+D_m²))), matching the
/// kernel's leading behaviour so the remainder decays; deep images are truncated below a
/// coefficient floor (they are exponentially small AND regular — the integrator absorbs
/// the leftover).</para>
///
/// The series is FREQUENCY-INDEPENDENT (pure geometry + ε): built once per stackup. It is
/// gated (F-extraction tests) to reproduce the single-slab (c₀, c₁…) at N = 1, collapse to
/// primary + ground image when every layer is air, satisfy split-slab invariance, and make
/// the K̃_Φ remainder decay algebraically.
/// </summary>
internal static class MultiLayerImages
{
    // Drop images whose coefficient falls this far below the primary — they are
    // exponentially small in k_ρ and regular, so the Sommerfeld remainder carries them
    // with no loss of the C1-style self-convergence.
    private const double CoefficientFloor = 1e-11;
    // Intermediate series are pruned to terms within this ratio of the running largest —
    // safe because a dropped term's descendants (products of |Γ| ≤ 1, |γ| < 1 factors)
    // stay proportionally small, so it bounds the working set to the geometrically-few
    // significant images without changing the emitted series past ~this ratio.
    private const double RelativePrune = 1e-10;
    // Round-trip depth ceiling relative to the stack height: images deeper than a few
    // stack heights are e^{−k_ρ D}-suppressed across the ENTIRE integration range (which
    // reaches only k_ρ ~ a few/d), so they belong in the remainder, not the image set —
    // and this guarantees the reciprocal geometric series terminates. The integrator's
    // reach is a ≈ k1 + 6/d_total, so e^{−a·D} < 1e-9 already by D ≈ 1.5·d_total; a cap of
    // 8·d_total is deep margin AND keeps the monomial expansion small.
    private const double DepthCapFactor = 8;
    // A runaway guard: an extremely thin, high-contrast lossless layer could in principle
    // sustain thousands of significant bounces. Fail LOUDLY rather than hang.
    private const int MaxTerms = 400_000;

    /// <summary>One image term: its depth (2·Σ round-trip thicknesses) and complex
    /// coefficient. The spectral subtraction is Coeff·e^{−jk_z0·Depth}/(jk_z0) and the
    /// spatial image is Coeff·g(√(ρ²+Depth²)); the primary is (Depth 0, c₀).</summary>
    public readonly record struct Image(double Depth, Complex Coeff);

    /// <summary>G̃_A's images: (1, 0) and (−1, 2·d_total) — the ε-independent primary +
    /// PEC ground image (subtract as µ₀·Coeff·e^{−jk_z0 D}/(jk_z0)).</summary>
    public static IReadOnlyList<Image> GaImages(LayeredStackup stackup) => new[]
    {
        new Image(0, Complex.One),
        new Image(2 * stackup.TotalThicknessMeters, -Complex.One),
    };

    /// <summary>K̃_Φ's quasi-static image series (subtract as Coeff·e^{−jk_z0 D}/(jk_z0 ε₀)).
    /// The first entry is always the primary (Depth 0, c₀ = 2/(ε_c,top + 1)).</summary>
    public static IReadOnlyList<Image> PhiImages(LayeredStackup stackup)
    {
        int n = stackup.Layers.Count;
        var t = new double[n];
        var eps = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = stackup.Layers[i].ThicknessMeters;
            eps[i] = stackup.Layers[i].ComplexPermittivity;
        }
        double depthCap = DepthCapFactor * stackup.TotalThicknessMeters;
        var ctx = new Context(n, t, depthCap);

        // Γ: bottom-up electrostatic reflection, ground = short (−1), Y_i ∝ ε_i.
        var gamma = ctx.Constant(-Complex.One);
        gamma = ctx.Shift(gamma, 0);                       // propagate through layer 0
        for (int i = 1; i < n; i++)
        {
            Complex fresnel = (eps[i] - eps[i - 1]) / (eps[i] + eps[i - 1]);
            // Γ ← (γ + Γ)/(1 + γΓ)
            var num = ctx.Add(ctx.Constant(fresnel), gamma);
            var den = ctx.Add(ctx.Constant(Complex.One), ctx.Scale(gamma, fresnel));
            gamma = ctx.Multiply(num, ctx.Reciprocal(den));
            gamma = ctx.Shift(gamma, i);                   // propagate through layer i
        }

        // B = (1/ε_top)(1 + Γ)/(1 − Γ); κG̃ = B/(1 + B); c_m = 2·(κG̃)_m.
        Complex invEpsTop = 1 / eps[n - 1];
        var onePlus = ctx.Add(ctx.Constant(Complex.One), gamma);
        var oneMinus = ctx.Add(ctx.Constant(Complex.One), ctx.Scale(gamma, -Complex.One));
        var b = ctx.Scale(ctx.Multiply(onePlus, ctx.Reciprocal(oneMinus)), invEpsTop);
        var kg = ctx.Multiply(b, ctx.Reciprocal(ctx.Add(ctx.Constant(Complex.One), b)));

        return ctx.Emit(kg, scale: 2);
    }

    /// <summary>G̃_A's images for a source at INTERIOR interface m (covered patch): still the
    /// ε-independent primary + PEC ground image, but the image now sits at depth 2·z_m (twice
    /// the height of interface m above the ground) instead of 2·d_total. m = n−1 recovers
    /// <see cref="GaImages"/>.</summary>
    public static IReadOnlyList<Image> GaImagesInterior(LayeredStackup stackup, int m) => new[]
    {
        new Image(0, Complex.One),
        new Image(2 * stackup.InterfaceHeights()[m], -Complex.One),
    };

    /// <summary>K̃_Φ's quasi-static image series for a source at INTERIOR interface m. The
    /// charge at node m now sees TWO loaded lines: the grounded substrate below (input
    /// impedance Z_down, ground = short) and the cover + air half-space above (Z_up); the
    /// node voltage per unit charge is their PARALLEL combination κG̃ = Z_down·Z_up/(Z_down +
    /// Z_up), whose expansion in the layer phases yields images from BOTH the down-going
    /// (ground) and up-going (cover/air) reflections. m = n−1 gives Z_up = Z_air = 1 and
    /// collapses to the top-source <see cref="PhiImages"/> κG̃ = B/(1+B); an all-air cover
    /// collapses to the sub-slab beneath m (both gated).</summary>
    public static IReadOnlyList<Image> PhiImagesInterior(LayeredStackup stackup, int m)
    {
        int n = stackup.Layers.Count;
        if (m < 0 || m >= n)
            throw new ArgumentOutOfRangeException(nameof(m),
                $"Source interface {m} is out of range for a {n}-layer stackup.");
        var t = new double[n];
        var eps = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = stackup.Layers[i].ThicknessMeters;
            eps[i] = stackup.Layers[i].ComplexPermittivity;
        }
        double depthCap = DepthCapFactor * stackup.TotalThicknessMeters;
        var ctx = new Context(n, t, depthCap);

        // Z_down: ground short (Γ = −1), reflections up through layers 0..m; impedance
        // referenced to the layer just BELOW node m (char admittance ε_m).
        var gDown = ctx.Shift(ctx.Constant(-Complex.One), 0);
        for (int i = 1; i <= m; i++)
        {
            Complex fresnel = (eps[i] - eps[i - 1]) / (eps[i] + eps[i - 1]);
            gDown = ctx.Multiply(ctx.Add(ctx.Constant(fresnel), gDown),
                ctx.Reciprocal(ctx.Add(ctx.Constant(Complex.One), ctx.Scale(gDown, fresnel))));
            gDown = ctx.Shift(gDown, i);
        }
        var zDown = Impedance(ctx, gDown, 1 / eps[m]);

        // Z_up: air half-space above (matched load, no reflection at infinity), reflections
        // down through the cover layers n−1..m+1; referenced to the layer just ABOVE node m
        // (ε_{m+1}). With no cover (m = n−1) the load is air directly: Z_up = 1.
        Dictionary<Key, Complex> zUp;
        if (m == n - 1)
        {
            zUp = ctx.Constant(Complex.One);
        }
        else
        {
            Complex rTop = (eps[n - 1] - Complex.One) / (eps[n - 1] + Complex.One);   // cover/air
            var gUp = ctx.Shift(ctx.Constant(rTop), n - 1);
            for (int i = n - 2; i >= m + 1; i--)
            {
                Complex fresnel = (eps[i] - eps[i + 1]) / (eps[i] + eps[i + 1]);
                gUp = ctx.Multiply(ctx.Add(ctx.Constant(fresnel), gUp),
                    ctx.Reciprocal(ctx.Add(ctx.Constant(Complex.One), ctx.Scale(gUp, fresnel))));
                gUp = ctx.Shift(gUp, i);
            }
            zUp = Impedance(ctx, gUp, 1 / eps[m + 1]);
        }

        // κG̃ = Z_down·Z_up/(Z_down + Z_up); c_m = 2·(κG̃)_m.
        var kg = ctx.Multiply(ctx.Multiply(zDown, zUp), ctx.Reciprocal(ctx.Add(zDown, zUp)));
        return ctx.Emit(kg, scale: 2);
    }

    /// <summary>Normalized input impedance (1/ε)(1 + Γ)/(1 − Γ) as a series.</summary>
    private static Dictionary<Key, Complex> Impedance(Context ctx, Dictionary<Key, Complex> gamma, Complex invEps)
    {
        var onePlus = ctx.Add(ctx.Constant(Complex.One), gamma);
        var oneMinus = ctx.Add(ctx.Constant(Complex.One), ctx.Scale(gamma, -Complex.One));
        return ctx.Scale(ctx.Multiply(onePlus, ctx.Reciprocal(oneMinus)), invEps);
    }

    /// <summary>Truncated power-series arithmetic in the per-layer round-trip phases
    /// x_i = e^{−2k_ρ t_i}. A series is a map from a multi-index (round-trip counts) to a
    /// complex coefficient; the term's depth is 2·Σ n_i t_i. Every product/reciprocal is
    /// truncated past the depth cap and below the coefficient floor, so the reciprocal
    /// geometric series (each factor raises the minimum depth) always terminates.</summary>
    private sealed class Context
    {
        private readonly int _n;
        private readonly double[] _t;
        private readonly double _depthCap;

        public Context(int n, double[] t, double depthCap)
        {
            _n = n;
            _t = t;
            _depthCap = depthCap;
        }

        public double Depth(int[] idx)
        {
            double d = 0;
            for (int i = 0; i < _n; i++) d += 2.0 * idx[i] * _t[i];
            return d;
        }

        public Dictionary<Key, Complex> Constant(Complex c) =>
            c == Complex.Zero ? new() : new() { [new Key(new int[_n])] = c };

        public Dictionary<Key, Complex> Add(Dictionary<Key, Complex> a, Dictionary<Key, Complex> b)
        {
            var r = new Dictionary<Key, Complex>(a);
            foreach (var (k, v) in b)
                r[k] = r.TryGetValue(k, out var e) ? e + v : v;
            Prune(r);
            return r;
        }

        public Dictionary<Key, Complex> Scale(Dictionary<Key, Complex> a, Complex c)
        {
            var r = new Dictionary<Key, Complex>(a.Count);
            foreach (var (k, v) in a) r[k] = v * c;
            return r;
        }

        public Dictionary<Key, Complex> Shift(Dictionary<Key, Complex> a, int layer)
        {
            var r = new Dictionary<Key, Complex>(a.Count);
            foreach (var (k, v) in a)
            {
                var idx = (int[])k.Index.Clone();
                idx[layer]++;
                if (Depth(idx) <= _depthCap) r[new Key(idx)] = v;
            }
            return r;
        }

        public Dictionary<Key, Complex> Multiply(Dictionary<Key, Complex> a, Dictionary<Key, Complex> b)
        {
            var r = new Dictionary<Key, Complex>();
            foreach (var (ka, va) in a)
                foreach (var (kb, vb) in b)
                {
                    var idx = new int[_n];
                    for (int i = 0; i < _n; i++) idx[i] = ka.Index[i] + kb.Index[i];
                    if (Depth(idx) > _depthCap) continue;
                    var key = new Key(idx);
                    r[key] = r.TryGetValue(key, out var e) ? e + va * vb : va * vb;
                }
            Prune(r);
            return r;
        }

        /// <summary>1/P for a series whose depth-0 coefficient p₀ ≠ 0 and whose remaining
        /// terms have strictly positive depth: 1/P = (1/p₀)·Σ_{k≥0} (−X)^k, X = P/p₀ − 1.
        /// Each factor of X raises the minimum depth, so the sum terminates at the cap.</summary>
        public Dictionary<Key, Complex> Reciprocal(Dictionary<Key, Complex> p)
        {
            var zero = new Key(new int[_n]);
            if (!p.TryGetValue(zero, out var p0) || p0 == Complex.Zero)
                throw new InvalidOperationException(
                    "MultiLayerImages.Reciprocal requires a non-zero depth-0 coefficient — "
                    + "the electrostatic recursion should never produce one.");
            // X = P/p₀ − 1 (drop the depth-0 term).
            var x = new Dictionary<Key, Complex>();
            foreach (var (k, v) in p)
                if (!k.Equals(zero)) x[k] = v / p0;

            var acc = Constant(Complex.One);
            var term = Constant(Complex.One);           // (−X)^0
            var negX = Scale(x, -Complex.One);
            for (int k = 1; k < 100_000; k++)
            {
                term = Multiply(term, negX);
                if (term.Count == 0) break;              // depths all past the cap
                acc = Add(acc, term);
                double maxTerm = 0;
                foreach (var v in term.Values) maxTerm = Math.Max(maxTerm, v.Magnitude);
                if (maxTerm < CoefficientFloor) break;
                if (k == 99_999)
                    throw new InvalidOperationException(
                        "MultiLayerImages.Reciprocal did not converge — a near-unit reflection "
                        + "in an extremely thin layer; the depth cap should have bounded this.");
            }
            return Scale(acc, 1 / p0);
        }

        /// <summary>Collapse the series to a depth-sorted image list (merging equal depths),
        /// coefficients multiplied by <paramref name="scale"/>, with the primary first.</summary>
        public IReadOnlyList<Image> Emit(Dictionary<Key, Complex> series, double scale)
        {
            var byDepth = new SortedDictionary<double, Complex>();
            foreach (var (k, v) in series)
            {
                double d = Depth(k.Index);
                byDepth[d] = byDepth.TryGetValue(d, out var e) ? e + v * scale : v * scale;
            }
            double primary = byDepth.TryGetValue(0, out var c0) ? c0.Magnitude : 1;
            var images = new List<Image>();
            foreach (var (d, c) in byDepth)
                if (d == 0 || c.Magnitude >= CoefficientFloor * primary)
                    images.Add(new Image(d, c));
            return images;
        }

        /// <summary>Bound the working set to the geometrically-few significant images: drop
        /// terms more than <see cref="RelativePrune"/> below the largest in the series. A
        /// dropped term's descendants stay proportionally small (products of |Γ| ≤ 1, |γ| &lt; 1),
        /// so this changes the emitted series by at most ~that ratio while keeping the
        /// combinatorial monomial count from exploding on thin/high-contrast stacks.</summary>
        private void Prune(Dictionary<Key, Complex> r)
        {
            if (r.Count > MaxTerms)
                throw new InvalidOperationException(
                    $"MultiLayerImages exceeded {MaxTerms} image terms — a near-unit reflection "
                    + "in an extremely thin lossless layer. Extraction for this stackup needs a "
                    + "priority-ordered image search, not the depth-capped expansion.");
            double max = 0;
            foreach (var v in r.Values) max = Math.Max(max, v.Magnitude);
            if (max == 0) { r.Clear(); return; }
            double floor = max * RelativePrune;
            List<Key>? dead = null;
            foreach (var (k, v) in r)
                if (v.Magnitude < floor) (dead ??= new()).Add(k);
            if (dead is not null) foreach (var k in dead) r.Remove(k);
        }
    }

    /// <summary>A multi-index (per-layer round-trip counts) with structural equality — the
    /// power-series monomial key. Depths are exact integer combinations of the layer
    /// thicknesses, so equal monomials merge exactly (no float-tolerance depth binning).</summary>
    private readonly struct Key : IEquatable<Key>
    {
        public readonly int[] Index;
        private readonly int _hash;

        public Key(int[] index)
        {
            Index = index;
            var h = new HashCode();
            foreach (var i in index) h.Add(i);
            _hash = h.ToHashCode();
        }

        public bool Equals(Key other)
        {
            if (_hash != other._hash || Index.Length != other.Index.Length) return false;
            for (int i = 0; i < Index.Length; i++)
                if (Index[i] != other.Index[i]) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is Key k && Equals(k);
        public override int GetHashCode() => _hash;
    }
}
