using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Wavefront;

/// <summary>
/// The complex pupil function A·e^{2πiW} on an n × n grid of normalised pupil nodes from −1 to 1
/// (OP-38, docs/Optics-Conventions.md §33). Row i is Py = node i, column k is Px = node k
/// (<see cref="OpdEngine.Node"/>). A pupil node stands for the image-space direction offset
/// (<see cref="ScaleX"/>·Px, <see cref="ScaleY"/>·Py) from the reference ray, in direction cosines
/// on two unit axes normal to the chief ray, so the image field at a point x on the plane normal to
/// the chief ray is U(x) = Σ A·e^{2πi(W − n·(a_x·Px·x + a_y·Py·y)/λ)} (the kernel derived in
/// <see cref="FoucaultShadowgram"/>), λ the wavelength in the system's air and n the image-space
/// index relative to it.
/// </summary>
public sealed class PupilFunction
{
    /// <param name="amplitude">A ≥ 0 at each node, [row = Py, column = Px]; zero outside the unit
    /// circle and where no light arrives (a vignetted or failed ray).</param>
    /// <param name="waves">W, the OPD in waves, at each node where A &gt; 0 (ignored elsewhere).</param>
    /// <param name="scaleX">a_x: the direction-cosine offset per unit Px (non-zero; its sign is
    /// the image orientation).</param>
    /// <param name="scaleY">a_y, the same per unit Py.</param>
    /// <param name="wavelength">λ in the system's air, meters.</param>
    /// <param name="imageIndex">n, the image-space index relative to the system's air.</param>
    public PupilFunction(double[,] amplitude, double[,] waves, double scaleX, double scaleY, double wavelength, double imageIndex)
    {
        ArgumentNullException.ThrowIfNull(amplitude);
        ArgumentNullException.ThrowIfNull(waves);
        int n = amplitude.GetLength(0);
        if (n < 4 || amplitude.GetLength(1) != n || waves.GetLength(0) != n || waves.GetLength(1) != n)
            throw new ArgumentException($"PSF: the pupil function must be a square grid of at least 4 × 4 nodes with amplitude and OPD of the same size (got {amplitude.GetLength(0)} × {amplitude.GetLength(1)} and {waves.GetLength(0)} × {waves.GetLength(1)}).");
        if (!(Math.Abs(scaleX) > 0) || !(Math.Abs(scaleY) > 0) || !double.IsFinite(scaleX) || !double.IsFinite(scaleY))
            throw new ArgumentException($"PSF: the pupil scale must be finite and non-zero (it is {scaleX:g6}, {scaleY:g6}).");
        if (!(wavelength > 0) || !double.IsFinite(wavelength))
            throw new ArgumentException($"PSF: the wavelength must be positive and finite (it is {wavelength:g6} m).");
        if (!(imageIndex > 0) || !double.IsFinite(imageIndex))
            throw new ArgumentException($"PSF: the image-space index must be positive and finite (it is {imageIndex:g6}).");
        var nodes = new double[n];
        for (int k = 0; k < n; k++) nodes[k] = OpdEngine.Node(k, n);
        int circle = 0;
        double sum = 0, power = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double a = amplitude[i, k];
                bool inside = nodes[i] * nodes[i] + nodes[k] * nodes[k] <= 1;
                if (inside) circle++;
                if (!(a >= 0) || !double.IsFinite(a))
                    throw new ArgumentException($"PSF: the pupil amplitude at node ({nodes[k]:g6}, {nodes[i]:g6}) is {a:g6}; it must be finite and not negative.");
                if (a == 0) continue;
                if (!inside)
                    throw new ArgumentException($"PSF: the pupil amplitude at node ({nodes[k]:g6}, {nodes[i]:g6}) is {a:g6} outside the unit circle; the pupil is normalised to it.");
                if (!double.IsFinite(waves[i, k]))
                    throw new ArgumentException($"PSF: the OPD at node ({nodes[k]:g6}, {nodes[i]:g6}) is {waves[i, k]:g6} where the amplitude is {a:g6}.");
                sum += a;
                power += a * a;
            }
        if (!(sum > 0))
            throw new ArgumentException("PSF: no light reaches the image: every pupil amplitude is zero.");
        Amplitude = amplitude;
        Waves = waves;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Wavelength = wavelength;
        ImageIndex = imageIndex;
        Nodes = nodes;
        CircleNodes = circle;
        AmplitudeSum = sum;
        PowerSum = power;
    }

    /// <summary>A pupil function from closed forms: <paramref name="amplitude"/> and
    /// <paramref name="waves"/> of (Px, Py), evaluated at the nodes inside the unit circle.</summary>
    public static PupilFunction FromFunctions(int sampling, Func<double, double, double> amplitude, Func<double, double, double> waves,
        double scaleX, double scaleY, double wavelength, double imageIndex = 1)
    {
        ArgumentNullException.ThrowIfNull(amplitude);
        ArgumentNullException.ThrowIfNull(waves);
        if (sampling < 4) throw new ArgumentException($"PSF: the sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        var a = new double[sampling, sampling];
        var w = new double[sampling, sampling];
        for (int i = 0; i < sampling; i++)
            for (int k = 0; k < sampling; k++)
            {
                double px = OpdEngine.Node(k, sampling), py = OpdEngine.Node(i, sampling);
                if (px * px + py * py > 1) continue;
                a[i, k] = amplitude(px, py);
                w[i, k] = a[i, k] > 0 ? waves(px, py) : 0;
            }
        return new PupilFunction(a, w, scaleX, scaleY, wavelength, imageIndex);
    }

    /// <summary>A, [row = Py, column = Px]. Held, not copied.</summary>
    public double[,] Amplitude { get; }

    /// <summary>W in waves, [row = Py, column = Px]; meaningful where A &gt; 0.</summary>
    public double[,] Waves { get; }

    public double ScaleX { get; }
    public double ScaleY { get; }
    public double Wavelength { get; }
    public double ImageIndex { get; }

    /// <summary>The node coordinates, the same on both axes.</summary>
    public IReadOnlyList<double> Nodes { get; }

    public int Sampling => Nodes.Count;

    /// <summary>ΔP = 2/(n − 1), the node spacing in normalised pupil units.</summary>
    public double NodeSpacing => 2.0 / (Sampling - 1);

    /// <summary>The number of nodes inside the unit circle: a clear, uniform pupil's Σ A².</summary>
    public int CircleNodes { get; }

    /// <summary>Σ A: the unaberrated PSF's peak is its square.</summary>
    public double AmplitudeSum { get; }

    /// <summary>Σ A².</summary>
    public double PowerSum { get; }

    /// <summary>Σ A² / (nodes in the unit circle): the fraction of a clear, uniform pupil's
    /// power that the pupil passes. The PSF's energy equals it (Parseval).</summary>
    public double Transmission => PowerSum / CircleNodes;

    /// <summary>1/(2n·|a|) on each axis: the working F/# the pupil scale stands for (paraxial
    /// relation of the image-space numerical aperture n·|a| to the F/#).</summary>
    public double FNumberX => 1 / (2 * ImageIndex * Math.Abs(ScaleX));
    public double FNumberY => 1 / (2 * ImageIndex * Math.Abs(ScaleY));

    /// <summary>The largest |transverse offset| of a ray's image-surface intercept from the
    /// reference point along each PSF axis (meters), when the pupil came from a ray trace; NaN
    /// for a pupil built from closed forms.</summary>
    public double RaySpreadX { get; init; } = double.NaN;
    public double RaySpreadY { get; init; } = double.NaN;

    /// <summary>What the pupil function assumes, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Conditions the help warns about (fast cone, oblique chief ray, rays that failed).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The OPD the pupil was built from, when it came from a ray trace.</summary>
    public OpdResult? Opd { get; init; }

    /// <summary>A·e^{2πiW}, [row = Py, column = Px]; whole waves are taken out before the
    /// trigonometry so a large OPD keeps its phase digits.</summary>
    public Complex[,] Field()
    {
        int n = Sampling;
        var f = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double a = Amplitude[i, k];
                if (a == 0) continue;
                double w = Waves[i, k];
                double phase = 2 * Math.PI * (w - Math.Floor(w));
                f[i, k] = new Complex(a * Math.Cos(phase), a * Math.Sin(phase));
            }
        return f;
    }

    private Complex[,]? _field;

    /// <summary><see cref="Field"/>, computed once and shared by the engine's transforms, which
    /// only read it (a race computes the same values twice, harmlessly).</summary>
    internal Complex[,] SharedField => _field ??= Field();

    /// <summary>The same pupil with every OPD set to zero: the unaberrated reference.</summary>
    public PupilFunction Unaberrated() => new(Amplitude, new double[Sampling, Sampling], ScaleX, ScaleY, Wavelength, ImageIndex);
}

/// <summary>A sampled point spread function.</summary>
public sealed record PsfImage
{
    /// <summary>Column coordinates, meters from the reference point, increasing.</summary>
    public required IReadOnlyList<double> X { get; init; }

    /// <summary>Row coordinates, meters from the reference point, increasing.</summary>
    public required IReadOnlyList<double> Y { get; init; }

    /// <summary>Intensity [row = y, column = x], divided by the peak of the unaberrated PSF
    /// (the same pupil amplitude with no OPD), so its peak is the Strehl ratio.</summary>
    public required double[,] Intensity { get; init; }

    /// <summary>The complex amplitude, divided by the square root of the unaberrated peak, with
    /// the pupil centre as the phase origin; null for a polychromatic PSF (where it means
    /// nothing).</summary>
    public Complex[,]? Amplitude { get; init; }

    /// <summary>Σ intensity × cell area over the unaberrated peak's units, as a fraction of a
    /// clear pupil's power: exact (Parseval) only for the full FFT period, NaN otherwise.</summary>
    public double EnergyFraction { get; init; } = double.NaN;

    public double DeltaX => X.Count > 1 ? X[1] - X[0] : double.NaN;
    public double DeltaY => Y.Count > 1 ? Y[1] - Y[0] : double.NaN;

    /// <summary>The largest sample.</summary>
    public double MaximumSample
    {
        get
        {
            double max = double.NegativeInfinity;
            foreach (var v in Intensity) if (v > max) max = v;
            return max;
        }
    }

    /// <summary>The index of the sample nearest the reference point (coordinate 0).</summary>
    public (int Row, int Column) Origin => (Nearest(Y, 0), Nearest(X, 0));

    internal static int Nearest(IReadOnlyList<double> values, double target)
    {
        int best = 0;
        for (int i = 1; i < values.Count; i++)
            if (Math.Abs(values[i] - target) < Math.Abs(values[best] - target)) best = i;
        return best;
    }
}

/// <summary>One ray of a Huygens sum: where it meets the image surface, its direction there, its
/// optical path relative to the chief ray and its amplitude weight.</summary>
/// <param name="Position">The image-surface intercept, meters in the image surface's frame.</param>
/// <param name="Direction">The unit direction at the image surface.</param>
/// <param name="PathWaves">(chief ray's path − this ray's path) to the image surface, in waves
/// of the wavelength in the system's air (OpticStudio's "OPD Absolute").</param>
/// <param name="Weight">The amplitude weight c (pupil amplitude × √(local pupil-to-direction
/// Jacobian)).</param>
public readonly record struct HuygensRay(Vector3D Position, Vector3D Direction, double PathWaves, double Weight);

/// <summary>The rays of one wavelength for a Huygens PSF, and the image plane they are summed on.</summary>
public sealed record HuygensPupil
{
    public required IReadOnlyList<HuygensRay> Rays { get; init; }
    /// <summary>λ in the system's air, meters.</summary>
    public required double Wavelength { get; init; }
    public required double ImageIndex { get; init; }
    /// <summary>The image plane's centre (image surface frame).</summary>
    public required Vector3D Centre { get; init; }
    /// <summary>The plane's unit axes.</summary>
    public required Vector3D AxisX { get; init; }
    public required Vector3D AxisY { get; init; }
    /// <summary>The fitted pupil scale (as in <see cref="PupilFunction.ScaleX"/>), for the
    /// default spacing and the sampling check.</summary>
    public double ScaleX { get; init; } = double.NaN;
    public double ScaleY { get; init; } = double.NaN;
    /// <summary>The pupil node spacing ΔP the rays were launched on.</summary>
    public double NodeSpacing { get; init; } = double.NaN;
    /// <summary>The largest change of the wavefront (against the plane per ray through the centre)
    /// between neighbouring pupil nodes, in waves.</summary>
    public double MaxStepWaves { get; init; } = double.NaN;
    /// <summary>The paraxial exit pupil centre (image surface frame), or null at infinity.</summary>
    public Vector3D? ExitPupil { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>1/(2n·√|a_x·a_y|), the working F/# the fitted pupil scale stands for.</summary>
    public double FNumber => 1 / (2 * ImageIndex * Math.Sqrt(Math.Abs(ScaleX * ScaleY)));
}

/// <summary>The sampling check of a pupil function (OP-38): see <see cref="PsfEngine.CheckSampling"/>.</summary>
/// <param name="MaxStepWaves">The largest |ΔW| between neighbouring lit pupil nodes, waves.</param>
/// <param name="PeriodX">The image period λ/(n·|a_x|·ΔP) on x, meters: the PSF repeats with it.</param>
/// <param name="PeriodY">The same on y.</param>
public sealed record PsfSamplingCheck(double MaxStepWaves, double PeriodX, double PeriodY, IReadOnlyList<string> Warnings);

/// <summary>
/// Point spread functions and the Strehl ratio (OP-38, docs/Optics-Conventions.md §33): the FFT
/// method, the matrix-Fourier sampling of the same sum on any grid (polychromatic resampling,
/// image delta, peak search), the Huygens sum of ray wavelets on the image surface, line and edge
/// spread, and the sampling rules with their warnings. OpticStudio's behaviour is read from its
/// 2025 R2 help pages "PSF", "FFT PSF", "FFT Cross Section", "FFT Line/Edge Spread", "Huygens
/// PSF", "Huygens Cross Section" and "Strehl Ratio", saved under
/// <c>plans/sources/optics/op-38-help/</c>. Goodman and Born and Wolf were not on hand; the
/// closed forms the tests use are derived there.
/// </summary>
public static class PsfEngine
{
    /// <summary>The FFT zero-padding factor: "for a 32 x 32 sampling, OpticStudio uses the center
    /// portion of a 64 x 64 grid" (help, "FFT PSF").</summary>
    public const int DefaultPadding = 2;

    /// <summary>The largest wavefront change between neighbouring pupil samples, in waves, before
    /// the FFT image is aliased: a local slope of s waves per sample puts light at s image
    /// periods from the reference point (derived in <see cref="CheckSampling"/>), and the window
    /// holds ±½ period.</summary>
    public const double AliasStepWaves = 0.5;

    /// <summary>Half <see cref="AliasStepWaves"/>: beyond it the light reaches the outer half of
    /// the window, where the PSF's diffraction tails wrap round; a caution, not yet aliasing.</summary>
    public const double CautionStepWaves = 0.25;

    // ------------------------------------------------------------------ FFT

    /// <summary>The image spacing of the FFT on each axis: λ/(n·|a|·M·ΔP), M = padding × n the
    /// padded size.</summary>
    public static (double Dx, double Dy) NativeSpacing(PupilFunction pupil, int padding = DefaultPadding)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        int m = PaddedSize(pupil, padding);
        double common = pupil.Wavelength / (pupil.ImageIndex * m * pupil.NodeSpacing);
        return (common / Math.Abs(pupil.ScaleX), common / Math.Abs(pupil.ScaleY));
    }

    private static int PaddedSize(PupilFunction pupil, int padding)
    {
        if (padding < 1 || padding > 16) throw new ArgumentException($"PSF: the FFT padding factor must be 1 to 16 (it is {padding}).", nameof(padding));
        return padding * pupil.Sampling;
    }

    /// <summary>
    /// The FFT PSF: the pupil function zero-padded to M × M (M = padding × n) and transformed
    /// (<see cref="Fft2D"/>). Sample p (0 ≤ p &lt; M) of each axis is at (p − M/2)·Δ with Δ from
    /// <see cref="NativeSpacing"/>; the window is one period of the PSF, so its energy sums
    /// exactly (Parseval) to <see cref="PupilFunction.Transmission"/>.
    /// </summary>
    public static PsfImage Fft(PupilFunction pupil, int padding = DefaultPadding, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        int n = pupil.Sampling, m = PaddedSize(pupil, padding);
        var field = pupil.SharedField;
        // A negative scale reverses the image: U(y) with a < 0 is the transform of the pupil
        // reflected through its centre with |a| (the nodes are symmetric about 0).
        bool flipRows = pupil.ScaleY < 0, flipColumns = pupil.ScaleX < 0;
        var padded = new Complex[m, m];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
                padded[i, k] = field[flipRows ? n - 1 - i : i, flipColumns ? n - 1 - k : k];
        cancellation.ThrowIfCancellationRequested();
        var spectrum = Fft2D.Shift(Fft2D.Forward(padded, maxDegreeOfParallelism));
        cancellation.ThrowIfCancellationRequested();

        var (dx, dy) = NativeSpacing(pupil, padding);
        var xs = new double[m];
        var ys = new double[m];
        for (int p = 0; p < m; p++) { xs[p] = (p - m / 2) * dx; ys[p] = (p - m / 2) * dy; }
        double peak = pupil.AmplitudeSum * pupil.AmplitudeSum;
        double root = pupil.AmplitudeSum;
        var intensity = new double[m, m];
        var amplitude = new Complex[m, m];
        double energy = 0;
        for (int r = 0; r < m; r++)
            for (int c = 0; c < m; c++)
            {
                // The FFT's origin is the node at P = −1; the pupil centre's phase origin is
                // restored by e^{+2πi(u + v)}, u = (c − M/2)/(M·ΔP) cycles per unit P.
                double u = (c - m / 2) / (m * pupil.NodeSpacing), v = (r - m / 2) / (m * pupil.NodeSpacing);
                var s = spectrum[r, c];
                double e = s.Real * s.Real + s.Imaginary * s.Imaginary;
                energy += e;
                intensity[r, c] = e / peak;
                amplitude[r, c] = s * Turn(u + v) / root;
            }
        return new PsfImage
        {
            X = xs, Y = ys, Intensity = intensity, Amplitude = amplitude,
            EnergyFraction = energy / ((double)m * m * pupil.CircleNodes),
        };
    }

    /// <summary>
    /// The PSF of the pupil at any points: U(x, y) = Σ A·e^{2πi(W − n(a_x·Px·x + a_y·Py·y)/λ)}
    /// by the matrix Fourier transform (<see cref="MatrixFourierTransform"/>), the same sum the FFT
    /// samples on its own grid. Used for an image delta of the user's, for resampling each
    /// wavelength of a polychromatic PSF onto one grid, and for the peak search.
    /// </summary>
    public static PsfImage Sample(PupilFunction pupil, IReadOnlyList<double> xs, IReadOnlyList<double> ys,
        int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        if (xs.Count == 0 || ys.Count == 0) throw new ArgumentException("PSF: at least one image point on each axis.");
        double k = pupil.ImageIndex / pupil.Wavelength;
        var fy = ys.Select(y => k * pupil.ScaleY * y).ToArray();
        var fx = xs.Select(x => k * pupil.ScaleX * x).ToArray();
        cancellation.ThrowIfCancellationRequested();
        var u = MatrixFourierTransform.Transform(pupil.SharedField, pupil.Nodes, pupil.Nodes, fy, fx, inverse: false, maxDegreeOfParallelism);
        cancellation.ThrowIfCancellationRequested();
        double peak = pupil.AmplitudeSum * pupil.AmplitudeSum, root = pupil.AmplitudeSum;
        var intensity = new double[ys.Count, xs.Count];
        var amplitude = new Complex[ys.Count, xs.Count];
        for (int r = 0; r < ys.Count; r++)
            for (int c = 0; c < xs.Count; c++)
            {
                var s = u[r, c];
                intensity[r, c] = (s.Real * s.Real + s.Imaginary * s.Imaginary) / peak;
                amplitude[r, c] = s / root;
            }
        return new PsfImage { X = xs.ToArray(), Y = ys.ToArray(), Intensity = intensity, Amplitude = amplitude };
    }

    /// <summary>
    /// An m × m grid of spacing (dx, dy) centred on the reference point, sample p at (p − m/2)·Δ
    /// (the help's "coordinate (n/2+1)" in 1-based counting is the centre).
    /// </summary>
    public static (double[] X, double[] Y) Grid(int m, double dx, double dy)
    {
        if (m < 2) throw new ArgumentException($"PSF: the image grid needs at least 2 samples per side (it has {m}).", nameof(m));
        if (!(dx > 0) || !(dy > 0)) throw new ArgumentException($"PSF: the image spacing must be positive (it is {dx:g6}, {dy:g6}).");
        var xs = new double[m];
        var ys = new double[m];
        for (int p = 0; p < m; p++) { xs[p] = (p - m / 2) * dx; ys[p] = (p - m / 2) * dy; }
        return (xs, ys);
    }

    /// <summary>
    /// A polychromatic FFT PSF: each wavelength's sum evaluated on the shortest wavelength's FFT
    /// grid (the help: "the longer wavelengths will be scaled to have smaller effective grids";
    /// here each wavelength is sampled at the same image points by
    /// <see cref="Sample"/>, which needs no interpolation), weighted by w·(λ_min/λ)² over its own
    /// unaberrated peak. The (λ_min/λ)² factor makes every wavelength's normalised PSF carry
    /// energy in proportion to its weight (the normalised PSF's area grows as λ²); it is the
    /// help's "wFactor" of the Huygens PSF (help, "Huygens PSF") without the configuration
    /// weight. The result is divided by Σ w·(λ_min/λ)², the peak of the unaberrated polychromatic
    /// PSF, so its peak is the polychromatic Strehl ratio.
    /// </summary>
    public static PsfImage FftPolychromatic(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> weights, int padding = DefaultPadding,
        double imageDelta = 0, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        var (xs, ys) = PolychromaticGrid(pupils, weights, padding, imageDelta);
        return Combine(pupils.Select(p => p.Wavelength).ToArray(), weights, i => Sample(pupils[i], xs, ys, maxDegreeOfParallelism, cancellation), xs, ys);
    }

    /// <summary>The image grid of <see cref="FftPolychromatic"/>: M × M at the shortest
    /// wavelength's native spacing, or at <paramref name="imageDelta"/> (meters) when positive.</summary>
    public static (double[] X, double[] Y) PolychromaticGrid(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> weights, int padding, double imageDelta)
    {
        ArgumentNullException.ThrowIfNull(pupils);
        ArgumentNullException.ThrowIfNull(weights);
        if (pupils.Count == 0 || pupils.Count != weights.Count)
            throw new ArgumentException($"PSF: one weight per wavelength ({pupils.Count} pupils, {weights.Count} weights).");
        int shortest = 0;
        for (int i = 1; i < pupils.Count; i++) if (pupils[i].Wavelength < pupils[shortest].Wavelength) shortest = i;
        var (dx, dy) = imageDelta > 0 ? (imageDelta, imageDelta) : NativeSpacing(pupils[shortest], padding);
        return Grid(PaddedSize(pupils[shortest], padding), dx, dy);
    }

    /// <summary>Σ w·(λ_min/λ)²·I_λ / Σ w·(λ_min/λ)², each I_λ already over its own unaberrated peak.</summary>
    public static PsfImage Combine(IReadOnlyList<double> wavelengths, IReadOnlyList<double> weights, Func<int, PsfImage> compute,
        IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        ArgumentNullException.ThrowIfNull(wavelengths);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(compute);
        if (wavelengths.Count == 0 || wavelengths.Count != weights.Count)
            throw new ArgumentException($"PSF: one weight per wavelength ({wavelengths.Count} wavelengths, {weights.Count} weights).");
        double lmin = wavelengths.Min();
        var sum = new double[ys.Count, xs.Count];
        double total = 0;
        for (int i = 0; i < wavelengths.Count; i++)
        {
            double w = weights[i];
            if (!(w >= 0) || !double.IsFinite(w))
                throw new ArgumentException($"PSF: wavelength {i + 1}'s weight is {w:g6}; it must be finite and not negative.");
            if (w == 0) continue;
            double f = w * (lmin / wavelengths[i]) * (lmin / wavelengths[i]);
            total += f;
            var image = compute(i);
            if (image.X.Count != xs.Count || image.Y.Count != ys.Count)
                throw new InvalidOperationException($"PSF: wavelength {i + 1}'s image is {image.Y.Count} × {image.X.Count}, not the common {ys.Count} × {xs.Count} grid.");
            for (int r = 0; r < ys.Count; r++)
                for (int c = 0; c < xs.Count; c++) sum[r, c] += f * image.Intensity[r, c];
        }
        if (!(total > 0)) throw new ArgumentException("PSF: every wavelength weight is zero.");
        for (int r = 0; r < ys.Count; r++)
            for (int c = 0; c < xs.Count; c++) sum[r, c] /= total;
        return new PsfImage { X = xs.ToArray(), Y = ys.ToArray(), Intensity = sum };
    }

    // ------------------------------------------------------------------ Strehl

    /// <summary>
    /// The Strehl ratio, "the peak intensity of the diffraction point spread function (PSF)
    /// divided by the peak intensity of the diffraction point spread function (PSF) in the absence
    /// of aberrations" (help, "Strehl Ratio"): the unaberrated PSF is the same pupil amplitude with
    /// no OPD, whose peak (Σ A)² is at the reference point. The aberrated peak is the FFT's
    /// largest sample refined by <see cref="RefinePeak"/>, so it does not depend on where the
    /// grid falls. The help adds that the ratio "is not useful when the aberrations are large
    /// enough to make the peak of the PSF ambiguous, or for Strehl ratios smaller than about 0.1".
    /// </summary>
    public static double Strehl(PupilFunction pupil, int padding = DefaultPadding, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        var image = Fft(pupil, padding, maxDegreeOfParallelism, cancellation);
        return RefinePeak(image, (x, y) => Sample(pupil, x, y, maxDegreeOfParallelism, cancellation).Intensity).Value;
    }

    /// <summary>
    /// The peak of a PSF that can be evaluated anywhere: start at the image's largest sample,
    /// then evaluate 7 × 7 patches of spacing h about the best point so far, h starting at the
    /// image spacing and divided by 3 each round (each patch spans the previous one's spacing),
    /// eight rounds (h ends at 1/6561 of the image spacing).
    /// </summary>
    public static (double Value, double X, double Y) RefinePeak(PsfImage image, Func<double[], double[], double[,]> evaluate)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(evaluate);
        int br = 0, bc = 0;
        double best = double.NegativeInfinity;
        for (int r = 0; r < image.Y.Count; r++)
            for (int c = 0; c < image.X.Count; c++)
                if (image.Intensity[r, c] > best) { best = image.Intensity[r, c]; br = r; bc = c; }
        double x0 = image.X[bc], y0 = image.Y[br];
        double hx = image.X.Count > 1 ? image.DeltaX : 1e-9, hy = image.Y.Count > 1 ? image.DeltaY : 1e-9;
        for (int round = 0; round < 8; round++)
        {
            var xs = Enumerable.Range(-3, 7).Select(j => x0 + j * hx).ToArray();
            var ys = Enumerable.Range(-3, 7).Select(j => y0 + j * hy).ToArray();
            var values = evaluate(xs, ys);
            double nx = x0, ny = y0;
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 7; c++)
                    if (values[r, c] > best) { best = values[r, c]; nx = xs[c]; ny = ys[r]; }
            x0 = nx; y0 = ny;
            hx /= 3; hy /= 3;
        }
        return (best, x0, y0);
    }

    // ------------------------------------------------------------------ Sampling

    /// <summary>
    /// The sampling rules, with their warnings (help, "FFT PSF": "if the transverse aberrations
    /// exceed a few hundred wavelengths, the computation is likely not accurate"; the pupil grid
    /// and the image grid trade against each other).
    /// <para>
    /// <b>Derivation.</b> The sum over pupil nodes spaced ΔP is periodic in the image with period
    /// T = λ/(n·|a|·ΔP) on each axis. Where the wavefront has slope dW/dP the light lands at
    /// x = λ·(dW/dP)/(n·a) (stationary phase of the kernel), so a change of s waves between
    /// neighbouring nodes puts it at s·T from the reference point. The FFT window holds one period,
    /// ±T/2, so s must stay below ½ wave per sample or the light wraps round (aliasing); above ¼
    /// it reaches the window's outer half, where the diffraction tails already wrap. The
    /// geometric spread of the traced rays is checked against T/2 the same way.
    /// </para>
    /// </summary>
    /// <param name="imageWidthX">The width of an image grid sampled off the FFT grid (Huygens,
    /// a chosen image delta), meters; it must not exceed the period. Null for the FFT's own
    /// window, which is one period.</param>
    public static PsfSamplingCheck CheckSampling(PupilFunction pupil, double? imageWidthX = null, double? imageWidthY = null)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        int n = pupil.Sampling;
        double step = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                if (pupil.Amplitude[i, k] == 0) continue;
                if (k + 1 < n && pupil.Amplitude[i, k + 1] > 0) step = Math.Max(step, Math.Abs(pupil.Waves[i, k + 1] - pupil.Waves[i, k]));
                if (i + 1 < n && pupil.Amplitude[i + 1, k] > 0) step = Math.Max(step, Math.Abs(pupil.Waves[i + 1, k] - pupil.Waves[i, k]));
            }
        double common = pupil.Wavelength / (pupil.ImageIndex * pupil.NodeSpacing);
        double tx = common / Math.Abs(pupil.ScaleX), ty = common / Math.Abs(pupil.ScaleY);
        var warnings = SamplingWarnings(step, n, tx, ty, pupil.RaySpreadX, pupil.RaySpreadY, imageWidthX, imageWidthY);
        return new PsfSamplingCheck(step, tx, ty, warnings);
    }

    internal static List<string> SamplingWarnings(double step, int n, double tx, double ty, double spreadX, double spreadY, double? widthX, double? widthY)
    {
        var warnings = new List<string>();
        if (step > AliasStepWaves)
            warnings.Add($"ALIASED: the wavefront changes by up to {step:g3} waves between neighbouring pupil samples (the limit is {AliasStepWaves} wave), so part of the light lands outside the {tx * 1e6:g4} × {ty * 1e6:g4} µm image period and wraps back in. Raise the pupil sampling above {n} or bring the image nearer focus.");
        else if (step > CautionStepWaves)
            warnings.Add($"Near the sampling limit: the wavefront changes by up to {step:g3} waves between neighbouring pupil samples (aliasing starts at {AliasStepWaves}); the outer half of the image period may hold wrapped diffraction tails. Raise the pupil sampling above {n} for a clean edge.");
        if (double.IsFinite(spreadX) && double.IsFinite(spreadY) && (spreadX > tx / 2 || spreadY > ty / 2))
            warnings.Add($"ALIASED: the rays reach {spreadX * 1e6:g4} µm in x and {spreadY * 1e6:g4} µm in y from the reference point, beyond the half period ±{tx / 2 * 1e6:g4} × ±{ty / 2 * 1e6:g4} µm the pupil sampling resolves.");
        if (widthX is double wx && wx > tx * (1 + 1e-12) || widthY is double wy && wy > ty * (1 + 1e-12))
            warnings.Add($"ALIASED: the image grid ({(widthX ?? 0) * 1e6:g4} × {(widthY ?? 0) * 1e6:g4} µm) is wider than the period ({tx * 1e6:g4} × {ty * 1e6:g4} µm) of a pupil sampled {n} × {n}, so copies of the PSF appear in it. Raise the pupil sampling or shrink the image.");
        return warnings;
    }

    // ------------------------------------------------------------------ Huygens

    /// <summary>
    /// The Huygens PSF (help, "Huygens PSF"): "each ray represents a particular amplitude and phase
    /// wavelet. The diffraction intensity at any point on the image surface is the complex sum of
    /// all these wavelets, squared", with "a planar reference": each ray is a plane wave, so its
    /// phase at an image-plane point r is its phase at its own intercept q plus the path n·d·(r − q):
    /// U(r) = Σ c·e^{2πi[P − n·d·(r − q)/λ]}, P the ray's path relative to the chief ray in waves.
    /// The plane is the given centre and axes; r = centre + u·AxisX + v·AxisY. Each image row is
    /// summed on its own, so the result is bitwise the same at any thread count. Intensity is over
    /// the unaberrated peak (Σ c)² (every wavelet in phase).
    /// </summary>
    public static PsfImage Huygens(HuygensPupil pupil, IReadOnlyList<double> us, IReadOnlyList<double> vs,
        int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        ArgumentNullException.ThrowIfNull(us);
        ArgumentNullException.ThrowIfNull(vs);
        var rays = pupil.Rays;
        if (rays.Count == 0) throw new ArgumentException("Huygens PSF: no ray reaches the image.");
        if (us.Count == 0 || vs.Count == 0) throw new ArgumentException("Huygens PSF: at least one image point on each axis.");
        double scale = pupil.ImageIndex / pupil.Wavelength;
        int count = rays.Count;
        var alpha = new double[count];
        var beta = new double[count];
        var gamma = new double[count];
        var weight = new double[count];
        double sum = 0;
        for (int j = 0; j < count; j++)
        {
            var ray = rays[j];
            if (!(ray.Weight >= 0) || !double.IsFinite(ray.Weight) || !double.IsFinite(ray.PathWaves))
                throw new ArgumentException($"Huygens PSF: ray {j + 1} has weight {ray.Weight:g6} and path {ray.PathWaves:g6} waves; both must be finite and the weight not negative.");
            alpha[j] = ray.PathWaves + scale * Vector3D.Dot(ray.Direction, ray.Position - pupil.Centre);
            beta[j] = scale * Vector3D.Dot(ray.Direction, pupil.AxisX);
            gamma[j] = scale * Vector3D.Dot(ray.Direction, pupil.AxisY);
            weight[j] = ray.Weight;
            sum += ray.Weight;
        }
        if (!(sum > 0)) throw new ArgumentException("Huygens PSF: every ray weight is zero.");

        // The x factors e^{−2πi·β·u} are shared by every row; held in column blocks of at most
        // 2^22 values so the memory stays bounded.
        int nu = us.Count, nv = vs.Count;
        int block = Math.Max(1, Math.Min(nu, (1 << 22) / count));
        var rows = new Complex[nv][];
        for (int r = 0; r < nv; r++) rows[r] = new Complex[nu];
        for (int c0 = 0; c0 < nu; c0 += block)
        {
            cancellation.ThrowIfCancellationRequested();
            int width = Math.Min(block, nu - c0);
            var xFactor = new Complex[count * width];
            for (int j = 0; j < count; j++)
                for (int c = 0; c < width; c++) xFactor[j * width + c] = Turn(-beta[j] * us[c0 + c]);
            var parts = ParallelSlots.Compute(nv, maxDegreeOfParallelism, r =>
            {
                var line = new Complex[width];
                double v = vs[r];
                for (int j = 0; j < count; j++)
                {
                    if (weight[j] == 0) continue;
                    var a = weight[j] * Turn(alpha[j] - gamma[j] * v);
                    int offset = j * width;
                    for (int c = 0; c < width; c++) line[c] += a * xFactor[offset + c];
                }
                return line;
            }, cancellation);
            for (int r = 0; r < nv; r++) Array.Copy(parts[r], 0, rows[r], c0, width);
        }

        double peak = sum * sum;
        var intensity = new double[nv, nu];
        var amplitude = new Complex[nv, nu];
        for (int r = 0; r < nv; r++)
            for (int c = 0; c < nu; c++)
            {
                var s = rows[r][c];
                intensity[r, c] = (s.Real * s.Real + s.Imaginary * s.Imaginary) / peak;
                amplitude[r, c] = s / sum;
            }
        return new PsfImage { X = us.ToArray(), Y = vs.ToArray(), Intensity = intensity, Amplitude = amplitude };
    }

    /// <summary>The Huygens Strehl ratio: the peak of <see cref="Huygens"/> over (Σ c)², the
    /// largest sample refined by <see cref="RefinePeak"/>.</summary>
    public static double HuygensStrehl(HuygensPupil pupil, PsfImage image, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default) =>
        RefinePeak(image, (x, y) => Huygens(pupil, x, y, maxDegreeOfParallelism, cancellation).Intensity).Value;

    /// <summary>The help's default Huygens image delta, Δ = λF/√n_p (help, "Huygens PSF"), n_p the
    /// pupil sampling and F the working F/#.</summary>
    public static double DefaultHuygensDelta(double wavelength, double fNumber, int pupilSampling) =>
        wavelength * fNumber / Math.Sqrt(pupilSampling);

    // ------------------------------------------------------------------ Line and edge spread

    /// <summary>
    /// The line and edge spread of a sampled PSF (help, "FFT Line/Edge Spread": "the line spread
    /// function, LSF is the cross section of the image of a line object. The edge spread function
    /// (ESF) is the cross section of the image of an edge"; "The X-orientation means the line or
    /// edge is parallel to the X-axis"). X orientation sums each row over x and runs along y; Y
    /// sums each column over y and runs along x. The LSF is divided by its largest value; the ESF
    /// is the running sum with half the current sample, divided by the total (0 to 1 across the
    /// window).
    /// </summary>
    public static (IReadOnlyList<double> Coordinate, double[] Line, double[] Edge) LineEdgeSpread(PsfImage image, bool parallelToX)
    {
        ArgumentNullException.ThrowIfNull(image);
        var coordinate = parallelToX ? image.Y : image.X;
        int count = coordinate.Count, across = parallelToX ? image.X.Count : image.Y.Count;
        var line = new double[count];
        for (int p = 0; p < count; p++)
        {
            double s = 0;
            for (int q = 0; q < across; q++) s += parallelToX ? image.Intensity[p, q] : image.Intensity[q, p];
            line[p] = s;
        }
        double total = line.Sum(), max = line.Max();
        if (!(total > 0)) throw new InvalidOperationException("Line/edge spread: the PSF holds no light in the window.");
        var edge = new double[count];
        double run = 0;
        for (int p = 0; p < count; p++)
        {
            edge[p] = (run + line[p] / 2) / total;
            run += line[p];
        }
        for (int p = 0; p < count; p++) line[p] /= max;
        return (coordinate, line, edge);
    }

    // ------------------------------------------------------------------ From the lens

    /// <summary>
    /// The pupil function of one field and wavelength from the lens (OP-38): the OPD
    /// (<see cref="OpdEngine"/>, the given reference) on an n × n grid of pupil nodes, the
    /// amplitude the pupil apodization (<see cref="Apodization"/>) where the ray reaches the image
    /// and zero where it is vignetted or fails (each failure is counted by reason in the warnings),
    /// and the pupil scale fitted by least squares to the rays' image-space direction offsets from
    /// the chief ray, projected on two axes normal to the chief ray (x̂ and ŷ of the image surface
    /// with the chief ray's component taken out): the FFT PSF's plane "lies perpendicular to the
    /// incident chief ray" (help, "FFT PSF"). Afocal image space has no focus and is refused.
    /// </summary>
    public static PupilFunction BuildPupil(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        OpdOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        string where = $"PSF (field {field + 1}, wavelength {wavelength + 1})";
        if (sampling < 4) throw new ArgumentException($"{where}: the pupil sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{where}: afocal image space has no focus, so there is no point spread function; turn afocal image space off.");
        options ??= new OpdOptions();
        var (pupil, index) = Nodes(sampling);
        var opd = OpdEngine.Compute(lens, system, field, wavelength, pupil, options, cancellation);
        var apodization = Apodization(lens, system, where);
        var c = opd.ChiefRay;
        var (e1, e2) = PlaneAxes(c.Direction);

        int n = sampling;
        var amplitude = new double[n, n];
        var waves = new double[n, n];
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        double sxx = 0, sx = 0, syy = 0, sy = 0, spreadX = 0, spreadY = 0;
        int lit = 0;
        for (int j = 0; j < pupil.Count; j++)
        {
            var s = opd.Samples[j];
            if (!s.IsValid)
            {
                string reason = s.Failure ?? "failed";
                failures[reason] = failures.TryGetValue(reason, out int m) ? m + 1 : 1;
                continue;
            }
            var (row, column) = index[j];
            amplitude[row, column] = apodization(Math.Sqrt(s.Px * s.Px + s.Py * s.Py));
            waves[row, column] = s.Waves;
            var dd = s.Ray.Direction - c.Direction;
            sxx += s.Px * s.Px; sx += Vector3D.Dot(dd, e1) * s.Px;
            syy += s.Py * s.Py; sy += Vector3D.Dot(dd, e2) * s.Py;
            var dq = s.Ray.Position - c.Position;
            spreadX = Math.Max(spreadX, Math.Abs(Vector3D.Dot(dq, e1)));
            spreadY = Math.Max(spreadY, Math.Abs(Vector3D.Dot(dq, e2)));
            lit++;
        }
        if (lit == 0 || sxx == 0 || syy == 0)
            throw new InvalidOperationException($"{where}: too few rays reach the image to span the pupil in x and y ({lit} of {pupil.Count} reach it).");
        double ax = sx / sxx, ay = sy / syy;
        if (!(Math.Abs(ax) > 0) || !(Math.Abs(ay) > 0))
            throw new InvalidOperationException($"{where}: the image-space ray directions do not change across the pupil (scale {ax:g3}, {ay:g3}), so there is no focus.");

        double lambda = OpticsConventions.MicrometersToMeters(opd.AirMicrometers);
        var warnings = new List<string>();
        if (failures.Count > 0)
            warnings.Add($"{pupil.Count - lit} of {pupil.Count} pupil rays carry no light: " + string.Join("; ", failures.Select(f => $"{f.Value} × {f.Key}")) + ".");
        double incidence = IncidenceDegrees(lens, c);
        if (incidence > 20)
            warnings.Add($"The chief ray meets the image surface at {incidence:g3}° from its normal; the help says the FFT PSF is accurate for chief rays \"less than perhaps 20 degrees\" from normal and \"computes overly optimistic\" results beyond (help, \"FFT PSF\"). Compare with the Huygens PSF.");
        double fx = 1 / (2 * opd.ImageIndex * Math.Abs(ax)), fy = 1 / (2 * opd.ImageIndex * Math.Abs(ay));
        if (Math.Min(fx, fy) < 1.5)
            warnings.Add($"The image cone is F/{Math.Min(fx, fy):g3}; scalar diffraction \"predicts overly optimistic (a smaller PSF) results when the F/# is very fast\", around F/1.5 (in air) or faster (help, \"FFT PSF\"). Vector diffraction is not built.");
        return new PupilFunction(amplitude, waves, ax, ay, lambda, opd.ImageIndex)
        {
            RaySpreadX = spreadX, RaySpreadY = spreadY, Opd = opd, Warnings = warnings,
            Notes = OpdNotes(opd).Concat(new[]
            {
                $"Pupil {n} × {n} nodes from −1 to 1 ({pupil.Count} inside the unit circle); amplitude {ApodizationTypes.Names.Label(system.Aperture.Apodization)} where the ray reaches the image, zero where it does not.",
                $"Pupil scale (direction cosine per unit pupil coordinate, normal to the chief ray) fitted to the rays: {ax:g6} in x, {ay:g6} in y; working F/# 1/(2n|a|) = {fx:g5} in x, {fy:g5} in y.",
            }).ToArray(),
        };
    }

    /// <summary>
    /// The rays of one wavelength for a Huygens PSF (OP-38): an n × n grid of pupil nodes traced to
    /// the image surface (<see cref="OpdEngine"/> with the Absolute reference, so each ray's path
    /// relative to the chief ray is its own to the image surface), on the plane "tangent to the
    /// image surface at the chief ray intercept. Note the imaginary plane is normal to the normal
    /// of the surface, not the chief ray" (help, "Huygens PSF"). Each ray's weight is its pupil
    /// apodization times √J, J = |∂(d·x̂′, d·ŷ′)/∂(Px, Py)| the area of direction-cosine space per
    /// unit pupil area (central differences between neighbouring nodes, one-sided at the edge): the
    /// rays sample the entrance pupil evenly and carry equal power, so the angular amplitude is
    /// √(power/J) and its integral over directions weighs each ray by √J (derived; it keeps a
    /// distorted pupil's energy where it belongs).
    /// </summary>
    /// <param name="centre">The image plane's centre in the image surface's frame, or null for
    /// this wavelength's chief-ray intercept (or the centroid with <paramref name="useCentroid"/>).</param>
    /// <param name="useCentroid">Centre on the geometric centroid of the rays (power-weighted
    /// mean intercept) when no centre is given (help: "Use Centroid").</param>
    public static HuygensPupil BuildHuygens(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        OpdOptions? options = null, Vector3D? centre = null, bool useCentroid = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        string where = $"Huygens PSF (field {field + 1}, wavelength {wavelength + 1})";
        if (sampling < 4) throw new ArgumentException($"{where}: the pupil sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{where}: afocal image space has no focus, so there is no point spread function; turn afocal image space off.");
        options = (options ?? new OpdOptions()) with { Reference = OpdReference.Absolute, ReferencePoint = OpdReferencePoint.ChiefRay, ReferenceToPrimary = false };
        var (pupil, index) = Nodes(sampling);
        var opd = OpdEngine.Compute(lens, system, field, wavelength, pupil, options, cancellation);
        var apodization = Apodization(lens, system, where);
        var chief = opd.ChiefRay;

        // The plane: normal to the image surface at the chief ray's intercept, oriented along the
        // chief ray; axes x̂ and ŷ of the image surface with the normal component taken out.
        var normal = SurfaceNormal(lens, chief.Position);
        if (Vector3D.Dot(normal, chief.Direction) < 0) normal = -normal;
        var (ex, ey) = PlaneAxes(normal);

        int n = sampling;
        double step = 2.0 / (n - 1);
        var grid = new int[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) grid[i, k] = -1;
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < pupil.Count; j++)
        {
            var s = opd.Samples[j];
            if (s.IsValid) grid[index[j].Row, index[j].Column] = j;
            else
            {
                string reason = s.Failure ?? "failed";
                failures[reason] = failures.TryGetValue(reason, out int m) ? m + 1 : 1;
            }
        }

        // Fitted scale (for the default spacing and the sampling check), and the centroid.
        double sxx = 0, sx = 0, syy = 0, sy = 0, power = 0;
        Vector3D centroid = Vector3D.Zero;
        foreach (var s in opd.Samples)
        {
            if (!s.IsValid) continue;
            var dd = s.Ray.Direction - chief.Direction;
            sxx += s.Px * s.Px; sx += Vector3D.Dot(dd, ex) * s.Px;
            syy += s.Py * s.Py; sy += Vector3D.Dot(dd, ey) * s.Py;
            double a = apodization(Math.Sqrt(s.Px * s.Px + s.Py * s.Py));
            centroid += s.Ray.Position * (a * a);
            power += a * a;
        }
        if (sxx == 0 || syy == 0 || !(power > 0))
            throw new InvalidOperationException($"{where}: too few rays reach the image to span the pupil in x and y.");
        double ax = sx / sxx, ay = sy / syy;
        if (!(Math.Abs(ax) > 0) || !(Math.Abs(ay) > 0))
            throw new InvalidOperationException($"{where}: the image-space ray directions do not change across the pupil (scale {ax:g3}, {ay:g3}), so there is no focus.");
        centroid /= power;
        var c = centre ?? (useCentroid ? centroid : chief.Position);

        double lambda = OpticsConventions.MicrometersToMeters(opd.AirMicrometers);
        double nImage = opd.ImageIndex;
        var rays = new List<HuygensRay>();
        int fallback = 0;
        var waves = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                int j = grid[i, k];
                if (j < 0) continue;
                var s = opd.Samples[j];
                // The wavefront against the plane per ray through the centre, for the step check.
                waves[i, k] = s.Waves + nImage * Vector3D.Dot(s.Ray.Direction, s.Ray.Position - c) / lambda;
                (double U, double V) D(int jj) => (Vector3D.Dot(opd.Samples[jj].Ray.Direction, ex), Vector3D.Dot(opd.Samples[jj].Ray.Direction, ey));
                (double, double)? Partial(int a, int b, bool alongPx)
                {
                    int Get(int ii, int kk) => ii >= 0 && ii < n && kk >= 0 && kk < n ? grid[ii, kk] : -1;
                    int plus = alongPx ? Get(a, b + 1) : Get(a + 1, b);
                    int minus = alongPx ? Get(a, b - 1) : Get(a - 1, b);
                    if (plus >= 0 && minus >= 0) { var (pu, pv) = D(plus); var (mu, mv) = D(minus); return ((pu - mu) / (2 * step), (pv - mv) / (2 * step)); }
                    var (cu, cv) = D(grid[a, b]);
                    if (plus >= 0) { var (pu, pv) = D(plus); return ((pu - cu) / step, (pv - cv) / step); }
                    if (minus >= 0) { var (mu, mv) = D(minus); return ((cu - mu) / step, (cv - mv) / step); }
                    return null;
                }
                var dPx = Partial(i, k, true);
                var dPy = Partial(i, k, false);
                if (dPx is null || dPy is null) fallback++;
                var (uPx, vPx) = dPx ?? (ax, 0.0);
                var (uPy, vPy) = dPy ?? (0.0, ay);
                double jac = Math.Abs(uPx * vPy - uPy * vPx);
                double amp = apodization(Math.Sqrt(s.Px * s.Px + s.Py * s.Py));
                rays.Add(new HuygensRay(s.Ray.Position, s.Ray.Direction, s.Waves, amp * Math.Sqrt(jac)));
            }

        double maxStep = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                if (grid[i, k] < 0) continue;
                if (k + 1 < n && grid[i, k + 1] >= 0) maxStep = Math.Max(maxStep, Math.Abs(waves[i, k + 1] - waves[i, k]));
                if (i + 1 < n && grid[i + 1, k] >= 0) maxStep = Math.Max(maxStep, Math.Abs(waves[i + 1, k] - waves[i, k]));
            }

        var warnings = new List<string>();
        if (failures.Count > 0)
            warnings.Add($"{pupil.Count - rays.Count} of {pupil.Count} pupil rays carry no light: " + string.Join("; ", failures.Select(f => $"{f.Value} × {f.Key}")) + ".");
        var notes = new List<string>(OpdNotes(opd).Where(x => !x.StartsWith("OPD = chief", StringComparison.Ordinal)))
        {
            $"Pupil {n} × {n} nodes from −1 to 1 ({pupil.Count} inside the unit circle), {rays.Count} rays reaching the image; amplitude {ApodizationTypes.Names.Label(system.Aperture.Apodization)} × √(direction-space area per unit pupil area).",
            $"Image plane tangent to the image surface at {(centre is not null ? "the given centre" : useCentroid ? "the rays' centroid" : "the chief ray's intercept")}, normal ({normal.X:g4}, {normal.Y:g4}, {normal.Z:g4}) in the image surface's frame; planar phase reference (each ray a plane wave).",
        };
        if (fallback > 0)
            notes.Add($"{fallback} rays had no traced neighbour along an axis; their Jacobian uses the fitted pupil scale there.");
        return new HuygensPupil
        {
            Rays = rays, Wavelength = lambda, ImageIndex = nImage, Centre = c, AxisX = ex, AxisY = ey,
            ScaleX = ax, ScaleY = ay, NodeSpacing = step, MaxStepWaves = maxStep, ExitPupil = opd.Geometry.ExitPupil,
            Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>
    /// The Huygens sampling warnings: the wavefront step and the image width against the pupil
    /// period (<see cref="CheckSampling"/>'s rules), and the help's test for the planar phase
    /// reference: "computing the distance from the exit pupil to image center, and the distance
    /// from the exit pupil to corner of the image. If these two distances differ by more than λ/4,
    /// the plane wave approximation will not be a good representation" (help, "Huygens PSF"); λ is
    /// read as the wavelength in the image medium. Only the planar reference is built, so this is
    /// a warning, not a switch.
    /// </summary>
    public static IReadOnlyList<string> HuygensWarnings(HuygensPupil pupil, IReadOnlyList<double> us, IReadOnlyList<double> vs)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        double common = pupil.Wavelength / (pupil.ImageIndex * pupil.NodeSpacing);
        double tx = common / Math.Abs(pupil.ScaleX), ty = common / Math.Abs(pupil.ScaleY);
        int n = (int)Math.Round(2 / pupil.NodeSpacing) + 1;
        var warnings = SamplingWarnings(pupil.MaxStepWaves, n, tx, ty, double.NaN, double.NaN,
            us[^1] - us[0] + (us.Count > 1 ? us[1] - us[0] : 0), vs[^1] - vs[0] + (vs.Count > 1 ? vs[1] - vs[0] : 0));
        if (pupil.ExitPupil is Vector3D e)
        {
            var corner = pupil.Centre + pupil.AxisX * Math.Max(Math.Abs(us[0]), Math.Abs(us[^1])) + pupil.AxisY * Math.Max(Math.Abs(vs[0]), Math.Abs(vs[^1]));
            double difference = Math.Abs((e - corner).Length - (e - pupil.Centre).Length);
            double quarter = pupil.Wavelength / pupil.ImageIndex / 4;
            if (difference > quarter)
                warnings.Add($"The distances from the exit pupil to the image centre and to its corner differ by {difference / (pupil.Wavelength / pupil.ImageIndex):g3} waves (more than λ/4); OpticStudio would switch to a spherical phase reference here (help, \"Huygens PSF\"), which is not built. The planar reference is used: shrink the image or check against the FFT PSF.");
        }
        return warnings;
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>The pupil apodization's amplitude against normalised pupil radius ρ (help,
    /// "Apodization Type", as <c>RelativeIllumination</c> reads it): uniform 1; Gaussian e^{−Gρ²};
    /// cosine cubed 1/(1 + (ρ·tan α)²)^{3/4}, tan α the paraxial entrance pupil radius over its
    /// distance from the object point (refused for an object at infinity, an entrance pupil at
    /// infinity, or the object cone angle aperture type).</summary>
    public static Func<double, double> Apodization(SequentialLens lens, OpticalSystemData system, string who)
    {
        var aperture = system.Aperture;
        switch (aperture.Apodization)
        {
            case ApodizationType.Uniform:
                return _ => 1;
            case ApodizationType.Gaussian:
            {
                double g = aperture.ApodizationFactor;
                return rho => Math.Exp(-g * rho * rho);
            }
            case ApodizationType.CosineCubed:
            {
                if (aperture.Type == ApertureType.ObjectConeAngle)
                    throw new InvalidOperationException($"{who}: cosine-cubed apodization with an object cone angle aperture uses OpticStudio's solid-angle ray distribution, which is not built.");
                var p = new RayLauncher(lens, system).Paraxial;
                if (lens.ObjectAtInfinity || p.EntrancePupilAtInfinity)
                    throw new InvalidOperationException($"{who}: cosine-cubed apodization needs an object at a finite distance and an entrance pupil at a finite distance (it models a point source illuminating the pupil).");
                double distance = lens.Surfaces[0].Thickness + p.EntrancePupilPosition!.Value;
                double tanAlpha = p.EntrancePupilDiameter!.Value / 2 / distance;
                return rho => Math.Pow(1 + rho * tanAlpha * (rho * tanAlpha), -0.75);
            }
            default:
                throw new InvalidOperationException($"{who}: apodization {aperture.Apodization} is not known.");
        }
    }

    /// <summary>The nodes of an n × n grid inside the unit circle, row by row from Py = −1, and
    /// each one's [row, column].</summary>
    internal static (List<(double Px, double Py)> Pupil, List<(int Row, int Column)> Index) Nodes(int n)
    {
        var pupil = new List<(double, double)>();
        var index = new List<(int, int)>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double px = OpdEngine.Node(k, n), py = OpdEngine.Node(i, n);
                if (px * px + py * py > 1) continue;
                pupil.Add((px, py));
                index.Add((i, k));
            }
        return (pupil, index);
    }

    /// <summary>Two unit axes normal to <paramref name="normal"/>: x̂ and then ŷ with the
    /// components along the normal (and, for ŷ, along the first axis) taken out.</summary>
    internal static (Vector3D X, Vector3D Y) PlaneAxes(Vector3D normal)
    {
        var x = new Vector3D(1, 0, 0);
        var ex = x - normal * Vector3D.Dot(x, normal);
        if (ex.Length < 1e-6)
        {
            var z = new Vector3D(0, 0, 1);
            ex = z - normal * Vector3D.Dot(z, normal);
        }
        ex = ex.Normalized();
        var y = new Vector3D(0, 1, 0);
        var ey = y - normal * Vector3D.Dot(y, normal) - ex * Vector3D.Dot(y, ex);
        if (ey.Length < 1e-6) ey = Vector3D.Cross(normal, ex);
        return (ex, ey.Normalized());
    }

    private static Vector3D SurfaceNormal(SequentialLens lens, Vector3D point)
    {
        var image = lens.Surfaces[lens.ImageIndex];
        var normal = image.Shape is { } shape ? shape.Normal(point, image) : ConicIntersection.Normal(point, image.Curvature, image.Conic);
        return normal.Normalized();
    }

    private static double IncidenceDegrees(SequentialLens lens, Ray chief)
    {
        var normal = SurfaceNormal(lens, chief.Position);
        double cos = Math.Min(1, Math.Abs(Vector3D.Dot(normal, chief.Direction)));
        return Math.Acos(cos) * 180 / Math.PI;
    }

    private static IEnumerable<string> OpdNotes(OpdResult r)
    {
        foreach (var n in r.Notes) yield return n;
        foreach (var l in r.Limitations) yield return "OPD limitation: " + l;
    }

    /// <summary>e^{2πi·t}, with the whole turns taken out first.</summary>
    internal static Complex Turn(double t)
    {
        double f = 2 * Math.PI * (t - Math.Floor(t));
        return new Complex(Math.Cos(f), Math.Sin(f));
    }
}
