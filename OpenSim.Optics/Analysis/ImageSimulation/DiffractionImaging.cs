using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>The complex degree of coherence's form (help, "Partially Coherent Image Analysis",
/// "Gamma": the Gaussian γ(r) = e^(−r·r/α²) and the sinc γ(r) = SINC(x/α)·SINC(y/α),
/// SINC(x) = sin(πx)/(πx)).</summary>
public enum CoherenceForm
{
    Gaussian = 0,
    Sinc = 1,
}

/// <summary>How the partially coherent sum is organised (help: "Mostly Incoherent" assumes γ is
/// narrow, "Mostly Coherent" that it is wide).</summary>
public enum PartialCoherenceMethod
{
    /// <summary>A sum over the separations Δ where γ is significant (narrow γ).</summary>
    MostlyIncoherent = 0,
    /// <summary>Abbe's sum over the illumination directions of the source whose transform is γ (wide γ).</summary>
    MostlyCoherent = 1,
}

/// <summary>
/// The coherent transfer function of a sampled pupil (OP-42; help, "Partially Coherent Image
/// Analysis", "Comment About the Coherent Optical Transfer Function": "The coherent optical
/// transfer function is assumed to be the complex pupil function H(fx, fy) =
/// P(λ·di·fx, λ·di·fy)·e^(ikW(λ·di·fx, λ·di·fy))"). With OP-38's pupil function, whose node P
/// stands for the image-space direction offset a·P (<see cref="PupilFunction"/>), the amplitude PSF
/// is U(x) = Σ A·e^{2πi(W − n·a·P·x/λ)} = ∫ H(f)·e^{2πif·x} df, so H(f) is the pupil at
/// P = −λ·f/(n·a) (derived): zero outside the unit circle, inside it the amplitude and the OPD
/// interpolated bilinearly between the lit nodes around P (the weights of unlit nodes dropped,
/// the rest renormalised), or the nearest lit node within one node diagonal when none of the four
/// is lit (the rim between the outer nodes and the circle). The cutoff is therefore the unit
/// circle itself, f_c = n·|a|/λ on each axis.
/// </summary>
public sealed class CoherentTransferFunction
{
    private readonly PupilFunction _p;

    public CoherentTransferFunction(PupilFunction pupil)
    {
        _p = pupil ?? throw new ArgumentNullException(nameof(pupil));
    }

    public PupilFunction Pupil => _p;

    /// <summary>The cutoff frequency n·|a|/λ on x, cycles per meter.</summary>
    public double CutoffX => _p.ImageIndex * Math.Abs(_p.ScaleX) / _p.Wavelength;
    public double CutoffY => _p.ImageIndex * Math.Abs(_p.ScaleY) / _p.Wavelength;

    /// <summary>H at spatial frequency (fx, fy), cycles per meter.</summary>
    public Complex At(double fx, double fy)
    {
        double px = -_p.Wavelength * fx / (_p.ImageIndex * _p.ScaleX);
        double py = -_p.Wavelength * fy / (_p.ImageIndex * _p.ScaleY);
        if (px * px + py * py > 1) return Complex.Zero;
        int n = _p.Sampling;
        double step = _p.NodeSpacing;
        double sx = (px + 1) / step, sy = (py + 1) / step;
        int k0 = Math.Clamp((int)Math.Floor(sx), 0, n - 2), i0 = Math.Clamp((int)Math.Floor(sy), 0, n - 2);
        double u = sx - k0, v = sy - i0;
        double wsum = 0, a = 0, w = 0;
        for (int di = 0; di < 2; di++)
            for (int dk = 0; dk < 2; dk++)
            {
                int i = i0 + di, k = k0 + dk;
                double amp = _p.Amplitude[i, k];
                if (amp == 0) continue;
                double t = (di == 0 ? 1 - v : v) * (dk == 0 ? 1 - u : u);
                wsum += t; a += t * amp; w += t * _p.Waves[i, k];
            }
        if (wsum > 0)
        {
            a /= wsum; w /= wsum;
        }
        else
        {
            // The rim: the nearest lit node within one node diagonal.
            double best = 2 * step * step;
            bool found = false;
            for (int i = Math.Max(0, i0 - 1); i <= Math.Min(n - 1, i0 + 2); i++)
                for (int k = Math.Max(0, k0 - 1); k <= Math.Min(n - 1, k0 + 2); k++)
                {
                    if (_p.Amplitude[i, k] == 0) continue;
                    double dx = _p.Nodes[k] - px, dy = _p.Nodes[i] - py, d2 = dx * dx + dy * dy;
                    if (d2 <= best) { best = d2; a = _p.Amplitude[i, k]; w = _p.Waves[i, k]; found = true; }
                }
            if (!found) return Complex.Zero;
        }
        double phase = 2 * Math.PI * (w - Math.Floor(w));
        return new Complex(a * Math.Cos(phase), a * Math.Sin(phase));
    }
}

/// <summary>
/// Diffraction images of an extended object on a periodic N × N grid of spacing d (OP-42;
/// docs/Optics-Conventions.md §67): the object is transformed, filtered by the coherent transfer
/// function or the optical transfer function, and transformed back (help, "Partially Coherent
/// Image Analysis", "The Optical Transfer Function Algorithm": "the image is transformed into
/// frequency space, multiplied by the OTF, and transformed back into position space").
/// <para>
/// <b>Conventions.</b> Sample [r, c] sits at x = c·d, y = −r·d from sample [0, 0] (row 0 the top).
/// The DFT index (kr, kc), each taken in −N/2 … N/2 − 1, is the frequency fx = kc/(N·d),
/// fy = −kr/(N·d). <see cref="Lattice"/> samples a transfer function there. The coherent PSF on
/// the grid is h = IDFT(H); the coherent image of amplitude a is a ⊛ h = IDFT(H·DFT(a)) (circular).
/// </para>
/// <para>
/// <b>Incoherent.</b> I = I₀ ⊛ |h|²/Σ|h|² = IDFT(OTF·DFT(I₀)), OTF = DFT(|h|²)/DFT(|h|²)₀: the
/// discrete autocorrelation of the sampled H, normalised to 1 at zero frequency.
/// </para>
/// <para>
/// <b>Partially coherent</b> (help: I(r) = ∬ √I₀(r₁)·√I₀(r₂)·γ(r₁ − r₂)·h*(r − r₁)·h(r − r₂) dr₁dr₂,
/// after Saleh and Rabbani, Applied Optics 21, 2770 (1982), which was not available here; the two
/// sums below are derived from the help's integral on the grid). With a = √I₀:
/// <list type="bullet">
/// <item>Abbe (<see cref="PartialCoherenceMethod.MostlyCoherent"/>): writing γ_Δ = Σ_s S_s·e^{2πi s·Δ/N},
/// S = DFT(γ)/N², gives I = Σ_s S_s·|IDFT(H_k·Â_{k+s})|², one coherent image per illumination
/// direction s; few terms when γ is wide (S narrow).</item>
/// <item>Separations (<see cref="PartialCoherenceMethod.MostlyIncoherent"/>): putting r₂ = r₁ + Δ,
/// I = Σ_Δ γ(−Δ)·[g_Δ ⊛ q_Δ], g_Δ(r) = a(r)·a(r + Δ), q_Δ(u) = h*(u)·h(u − Δ); few terms when γ is
/// narrow.</item>
/// </list>
/// Both are exact on the periodic grid with every term kept; "Fraction" keeps the largest terms
/// (|S| or |γ|²) until that share of their sum is reached. γ ≡ 1 gives the coherent image exactly;
/// γ = δ gives (1/N²)·Σ|H|² times the incoherent image (derived: S_s = 1/N² for every s).
/// </para>
/// </summary>
public static class DiffractionImaging
{
    /// <summary>The signed frequency index of DFT index j of n.</summary>
    public static int Signed(int j, int n) => j < (n + 1) / 2 ? j : j - n;

    /// <summary>A transfer function sampled at the grid's DFT frequencies.</summary>
    public static Complex[,] Lattice(int n, double d, Func<double, double, Complex> transfer)
    {
        var h = new Complex[n, n];
        for (int r = 0; r < n; r++)
        {
            double fy = -Signed(r, n) / (n * d);
            for (int c = 0; c < n; c++) h[r, c] = transfer(Signed(c, n) / (n * d), fy);
        }
        return h;
    }

    private static Complex[,] ToComplex(double[,] x)
    {
        int rows = x.GetLength(0), cols = x.GetLength(1);
        var z = new Complex[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) z[r, c] = x[r, c];
        return z;
    }

    private static void CheckSquare(double[,] x, Complex[,] h, string who)
    {
        int n = x.GetLength(0);
        if (x.GetLength(1) != n || h.GetLength(0) != n || h.GetLength(1) != n)
            throw new ArgumentException($"{who}: the object and the transfer function must be N × N on one grid (they are {x.GetLength(0)} × {x.GetLength(1)} and {h.GetLength(0)} × {h.GetLength(1)}).");
    }

    /// <summary>The coherent amplitude image IDFT(H·DFT(a)).</summary>
    public static Complex[,] CoherentAmplitude(double[,] amplitude, Complex[,] ctf, int? threads = 1)
    {
        CheckSquare(amplitude, ctf, "Coherent image");
        return CoherentAmplitudeFromSpectrum(Fft2D.Forward(ToComplex(amplitude), threads), ctf, threads);
    }

    /// <summary>IDFT(H·A) for an object spectrum A already on the grid.</summary>
    public static Complex[,] CoherentAmplitudeFromSpectrum(Complex[,] spectrum, Complex[,] ctf, int? threads = 1)
    {
        int n = ctf.GetLength(0);
        var s = new Complex[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) s[r, c] = spectrum[r, c] * ctf[r, c];
        return Fft2D.Inverse(s, threads);
    }

    /// <summary>|a|².</summary>
    public static double[,] Intensity(Complex[,] a)
    {
        int rows = a.GetLength(0), cols = a.GetLength(1);
        var i = new double[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) { var z = a[r, c]; i[r, c] = z.Real * z.Real + z.Imaginary * z.Imaginary; }
        return i;
    }

    /// <summary>The OTF of a CTF lattice: DFT(|IDFT(H)|²), divided by its zero-frequency value.</summary>
    public static Complex[,] Otf(Complex[,] ctf, int? threads = 1)
    {
        int n = ctf.GetLength(0);
        var psf = Intensity(Fft2D.Inverse(ctf, threads));
        var otf = Fft2D.Forward(ToComplex(psf), threads);
        double zero = otf[0, 0].Real;
        if (!(zero > 0)) throw new InvalidOperationException("Incoherent image: the transfer function passes no light (the pupil is dark on this frequency grid).");
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) otf[r, c] /= zero;
        return otf;
    }

    /// <summary>The incoherent image IDFT(OTF·DFT(I₀)), real part.</summary>
    public static double[,] Incoherent(double[,] irradiance, Complex[,] ctf, int? threads = 1)
    {
        CheckSquare(irradiance, ctf, "Incoherent image");
        return IncoherentFromSpectrum(Fft2D.Forward(ToComplex(irradiance), threads), Otf(ctf, threads), threads);
    }

    /// <summary>IDFT(OTF·S), real part, for an irradiance spectrum S on the grid.</summary>
    public static double[,] IncoherentFromSpectrum(Complex[,] spectrum, Complex[,] otf, int? threads = 1)
    {
        int n = otf.GetLength(0);
        var s = new Complex[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) s[r, c] = spectrum[r, c] * otf[r, c];
        var img = Fft2D.Inverse(s, threads);
        var o = new double[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) o[r, c] = img[r, c].Real;
        return o;
    }

    /// <summary>γ sampled on the grid's separations, [Δr, Δc] in DFT index order: Δx = Δc·d,
    /// Δy = −Δr·d with signed indices.</summary>
    public static double[,] GammaLattice(int n, double d, CoherenceForm form, double alpha)
    {
        if (!(alpha > 0) || !double.IsFinite(alpha)) throw new ArgumentException($"Partial coherence: α must be positive (it is {alpha:g6}).");
        var g = new double[n, n];
        for (int r = 0; r < n; r++)
        {
            double y = -Signed(r, n) * d;
            for (int c = 0; c < n; c++)
            {
                double x = Signed(c, n) * d;
                g[r, c] = form == CoherenceForm.Gaussian ? Math.Exp(-(x * x + y * y) / (alpha * alpha)) : Sinc(x / alpha) * Sinc(y / alpha);
            }
        }
        return g;
    }

    /// <summary>sin(πx)/(πx), 1 at 0 (help's SINC).</summary>
    public static double Sinc(double x)
    {
        if (x == 0) return 1;
        double t = Math.PI * x;
        return Math.Sin(t) / t;
    }

    /// <summary>The partially coherent image of irradiance I₀ (help's integral; see the class remarks).</summary>
    /// <returns>The image and the number of terms summed.</returns>
    public static (double[,] Image, int Terms, int Total) PartiallyCoherent(double[,] irradiance, Complex[,] ctf, double[,] gamma,
        PartialCoherenceMethod method, double fraction, int? threads = null, CancellationToken cancellation = default)
    {
        CheckSquare(irradiance, ctf, "Partially coherent image");
        int n = ctf.GetLength(0);
        if (gamma.GetLength(0) != n || gamma.GetLength(1) != n) throw new ArgumentException("Partially coherent image: γ must be on the same grid.");
        if (!(fraction > 0 && fraction <= 1)) throw new ArgumentException($"Partially coherent image: the fraction must be in (0, 1] (it is {fraction:g6}).");
        var a = new double[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) a[r, c] = Math.Sqrt(irradiance[r, c]);
        var result = new double[n, n];
        if (method == PartialCoherenceMethod.MostlyCoherent)
        {
            var source = Fft2D.Forward(ToComplex(gamma), 1);
            var terms = new List<(int R, int C, double S)>();
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) terms.Add((r, c, source[r, c].Real / ((double)n * n)));
            var kept = Keep(terms, t => Math.Abs(t.S), fraction);
            var spectrum = Fft2D.Forward(ToComplex(a), 1);
            Accumulate(result, kept, threads, cancellation, t =>
            {
                var s = new Complex[n, n];
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++) s[r, c] = ctf[r, c] * spectrum[(r + t.R) % n, (c + t.C) % n];
                var img = Intensity(Fft2D.Inverse(s, 1));
                for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) img[r, c] *= t.S;
                return img;
            });
            return (result, kept.Count, terms.Count);
        }
        else
        {
            var h = Fft2D.Inverse(ctf, 1);
            var terms = new List<(int R, int C, double S)>();
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) terms.Add((r, c, gamma[r, c]));
            var kept = Keep(terms, t => t.S * t.S, fraction);
            Accumulate(result, kept, threads, cancellation, t =>
            {
                // Δ = (t.R, t.C) as indices; γ(−Δ) is the weight.
                double weight = gamma[(n - t.R) % n, (n - t.C) % n];
                var g = new Complex[n, n];
                var q = new Complex[n, n];
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        g[r, c] = a[r, c] * a[(r + t.R) % n, (c + t.C) % n];
                        q[r, c] = Complex.Conjugate(h[r, c]) * h[(r - t.R + n) % n, (c - t.C + n) % n];
                    }
                var fg = Fft2D.Forward(g, 1);
                var fq = Fft2D.Forward(q, 1);
                for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) fg[r, c] *= fq[r, c];
                var conv = Fft2D.Inverse(fg, 1);
                var img = new double[n, n];
                for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) img[r, c] = weight * conv[r, c].Real;
                return img;
            });
            return (result, kept.Count, terms.Count);
        }
    }

    /// <summary>The terms kept, largest measure first until <paramref name="fraction"/> of the
    /// total measure is reached, returned in index order (so the sum's order is fixed).</summary>
    private static List<(int R, int C, double S)> Keep(List<(int R, int C, double S)> terms, Func<(int R, int C, double S), double> measure, double fraction)
    {
        double total = terms.Sum(measure);
        if (!(total > 0)) throw new InvalidOperationException("Partially coherent image: the coherence function is zero everywhere on the grid.");
        if (fraction >= 1) return terms.Where(t => measure(t) != 0).ToList();
        var order = Enumerable.Range(0, terms.Count).OrderByDescending(i => measure(terms[i])).ThenBy(i => i).ToList();
        var keep = new List<int>();
        double sum = 0;
        foreach (int i in order)
        {
            keep.Add(i);
            sum += measure(terms[i]);
            if (sum >= fraction * total) break;
        }
        keep.Sort();
        return keep.Select(i => terms[i]).ToList();
    }

    /// <summary>Σ term images, computed in parallel batches and added in term order.</summary>
    private static void Accumulate(double[,] result, List<(int R, int C, double S)> kept, int? threads, CancellationToken cancellation,
        Func<(int R, int C, double S), double[,]> term)
    {
        int n = result.GetLength(0);
        const int batch = 32;
        for (int start = 0; start < kept.Count; start += batch)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = Math.Min(batch, kept.Count - start);
            var images = ParallelSlots.Compute(count, threads, i => term(kept[start + i]), cancellation);
            foreach (var img in images)
                for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) result[r, c] += img[r, c];
        }
    }

    /// <summary>
    /// The spectrum on the grid of an object of n × n square pixels of width w, values v[j, l]
    /// (row 0 the top), whose top-left pixel centre is at (x0, y0) relative to grid sample [0, 0]:
    /// A = (1/d²)·Σ v·w²·sinc(fx·w)·sinc(fy·w)·e^{−2πi(fx·x + fy·y)} for luminous squares, or
    /// without the sinc factors for delta functions of the same weight (help, "Extended Diffraction
    /// Image Analysis", "Use Delta Functions"). The 1/d² makes it the DFT of the object's samples
    /// when the object is sampled on the grid (a square of width d at a sample is that sample).
    /// </summary>
    public static Complex[,] ObjectSpectrum(double[,] v, double w, double x0, double y0, int n, double d, bool deltas)
    {
        int rows = v.GetLength(0), cols = v.GetLength(1);
        // Ex[kc, l] = e^{−2πi fx x_l}, Ey[kr, j] = e^{−2πi fy y_j}
        var ex = new Complex[n, cols];
        var ey = new Complex[n, rows];
        var sx = new double[n];
        for (int k = 0; k < n; k++)
        {
            double f = Signed(k, n) / (n * d);
            sx[k] = deltas ? 1 : Sinc(f * w);
            for (int l = 0; l < cols; l++) ex[k, l] = PsfEngine.Turn(-f * (x0 + l * w));
            for (int j = 0; j < rows; j++) ey[k, j] = PsfEngine.Turn(f * (y0 - j * w)); // fy = −f for row index k
        }
        // T[kr, l] = Σ_j Ey[kr, j]·v[j, l]
        var t = new Complex[n, cols];
        for (int k = 0; k < n; k++)
            for (int l = 0; l < cols; l++)
            {
                Complex s = 0;
                for (int j = 0; j < rows; j++) if (v[j, l] != 0) s += ey[k, j] * v[j, l];
                t[k, l] = s;
            }
        var spectrum = new Complex[n, n];
        double scale = w * w / (d * d);
        for (int kr = 0; kr < n; kr++)
            for (int kc = 0; kc < n; kc++)
            {
                Complex s = 0;
                for (int l = 0; l < cols; l++) s += t[kr, l] * ex[kc, l];
                spectrum[kr, kc] = s * scale * sx[kr] * sx[kc];
            }
        return spectrum;
    }
}
