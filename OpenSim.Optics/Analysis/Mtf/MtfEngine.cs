using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-39: the optical transfer function by three engines (docs/Optics-Conventions.md §38). The
// settings and their meaning come from the Ansys OpticStudio 2025 R2 help pages "MTF", "FFT MTF",
// "FFT Through Focus MTF", "FFT Surface MTF", "FFT MTF vs. Field", "FFT MTF Map", "Huygens MTF",
// "Huygens Through Focus MTF", "Huygens Surface MTF", "Huygens MTF vs. Field", "Geometric MTF",
// "Geometric Through Focus MTF", "Geometric MTF vs. Field", "Geometric MTF Map", "MTF Data",
// "ZemaxMTFUnits MTFUnits" and "Contrast Loss Map (MTF)", saved with the square-wave equation
// image under plans/sources/optics/op-39-help/. Goodman was not on hand: every closed form is
// derived here or in the gate's comments.
// ---------------------------------------------------------------------------------------------

/// <summary>Which way the target's bars run. Tangential: the frequency runs along y (pupil y for
/// the FFT engine, image-surface y for the Huygens and geometric engines); sagittal: along x
/// (help, "FFT MTF", "Huygens MTF", "Geometric MTF").</summary>
public enum MtfOrientation
{
    Tangential = 0,
    Sagittal = 1,
}

/// <summary>The three ways the OTF is computed.</summary>
public enum MtfMethod
{
    /// <summary>The autocorrelation of the pupil function (the transform of OP-38's FFT PSF).</summary>
    Fft = 0,
    /// <summary>The transform of OP-38's Huygens PSF.</summary>
    Huygens = 1,
    /// <summary>The transform of the ray-density spot (the geometric line spread function).</summary>
    Geometric = 2,
}

/// <summary>One ray of a geometric MTF: where it lands relative to a common origin (meters on the
/// image surface; afocal, radians of direction) and the power it carries.</summary>
public readonly record struct GeometricRay(double X, double Y, double Weight);

/// <summary>
/// One wavelength's pupil function prepared for the FFT MTF: its autocorrelation along the two
/// pupil axes (and over the whole lag plane when asked), normalised to 1 at zero lag.
/// <para>
/// <b>Derivation.</b> OP-38's image field is U(x) = Σ_P f(P)·e^{−2πi·c·P·x}, f = A·e^{2πiW} on the
/// pupil nodes and c = n·a/λ (cycles per meter per unit pupil coordinate; afocal, n·b/λ cycles
/// per radian, b the beam's half-width per unit pupil coordinate). The OTF is the normalised
/// transform of the intensity, OTF(ν) = ∫|U|²·e^{−2πiνx}dx / ∫|U|²dx; |U|² = Σ_P Σ_Q f(P)·f*(Q)·
/// e^{−2πic(P−Q)x}, whose component at frequency ν has Q = P + ν/c, so
/// OTF(ν) = Σ_P f(P)·f*(P + ν/c) / Σ_P |f(P)|²: the autocorrelation of the pupil function at a
/// shift of ν/c pupil units. A shift of one node (ΔP = 2/(n − 1)) is the frequency step
/// c·ΔP; the pupil disk's two copies stop overlapping at a shift of 2, the cutoff 2c, which is
/// 2n·a/λ = 1/(λF) with the working F/# F = 1/(2n·a) (help, "FFT MTF": "the cutoff frequency at
/// any one wavelength is given by one over the wavelength times the working F/#"; afocal, 2n·b/λ,
/// "the exit pupil diameter divided by the wavelength" in air).
/// </para>
/// <para>
/// The autocorrelation is computed at every whole-node lag by the FFT (the pupil zero-padded to
/// 2n so the circular correlation equals the linear one) and interpolated linearly, in its real
/// and imaginary parts, between lags. The node sum stands in for the pupil integral: the error is
/// the jagged edge of the sampled disk, of order 10⁻³ at n = 128 (the gate).
/// </para>
/// </summary>
public sealed class MtfPupil
{
    private readonly Complex[] _alongX;      // R(0, k), k = 0 … n−1: Σ f[i, j + k]·f*[i, j] / Σ|f|²
    private readonly Complex[] _alongY;      // R(k, 0)
    private readonly Complex[,]? _plane;     // R(kr, kc), kr, kc = −(n−1) … n−1 at [kr + n − 1, kc + n − 1]

    internal MtfPupil(Complex[,] field, double amplitudeSum, double powerSum, double stepX, double stepY, double wavelength, double imageIndex,
        bool afocal, bool keepPlane, int? maxDegreeOfParallelism)
    {
        int n = field.GetLength(0);
        if (!(powerSum > 0)) throw new ArgumentException("MTF: no light reaches the image: every pupil amplitude is zero.");
        if (!(Math.Abs(stepX) > 0) || !(Math.Abs(stepY) > 0) || !double.IsFinite(stepX) || !double.IsFinite(stepY))
            throw new ArgumentException($"MTF: the frequency step per pupil node must be finite and non-zero (it is {stepX:g6}, {stepY:g6}).");
        int m = 2 * n;
        var padded = new Complex[m, m];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++) padded[i, k] = field[i, k];
        var spectrum = Fft2D.Forward(padded, maxDegreeOfParallelism);
        for (int i = 0; i < m; i++)
            for (int k = 0; k < m; k++)
            {
                var s = spectrum[i, k];
                spectrum[i, k] = new Complex(s.Real * s.Real + s.Imaginary * s.Imaginary, 0);
            }
        // Inverse DFT of |F|²: R[k] = Σ_y f[y + k]·f*[y] (circular, and linear because m ≥ 2n − 1).
        var r = Fft2D.Inverse(spectrum, maxDegreeOfParallelism);
        double zero = r[0, 0].Real;
        _alongX = new Complex[n];
        _alongY = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            _alongX[k] = r[0, k] / zero;
            _alongY[k] = r[k, 0] / zero;
        }
        if (keepPlane)
        {
            _plane = new Complex[2 * n - 1, 2 * n - 1];
            for (int kr = -(n - 1); kr <= n - 1; kr++)
                for (int kc = -(n - 1); kc <= n - 1; kc++)
                    _plane[kr + n - 1, kc + n - 1] = r[(kr + m) % m, (kc + m) % m] / zero;
        }
        Sampling = n;
        StepX = stepX;
        StepY = stepY;
        Wavelength = wavelength;
        ImageIndex = imageIndex;
        Afocal = afocal;
        AmplitudeSum = amplitudeSum;
        PowerSum = powerSum;
    }

    /// <summary>The OTF of a pupil function (focal image space), its axis cuts only unless
    /// <paramref name="keepPlane"/>.</summary>
    public static MtfPupil FromPupil(PupilFunction pupil, bool keepPlane = false, int? maxDegreeOfParallelism = 1) =>
        FromPupil(pupil, keepPlane, maxDegreeOfParallelism, pupil?.Notes ?? Array.Empty<string>(), pupil?.Warnings ?? Array.Empty<string>(), pupil!);

    internal static MtfPupil FromPupil(PupilFunction pupil, bool keepPlane, int? maxDegreeOfParallelism, IReadOnlyList<string> notes,
        IReadOnlyList<string> warnings, PupilFunction source)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        double c = pupil.ImageIndex / pupil.Wavelength;
        return new MtfPupil(pupil.SharedField, pupil.AmplitudeSum, pupil.PowerSum, c * pupil.ScaleX * pupil.NodeSpacing, c * pupil.ScaleY * pupil.NodeSpacing,
            pupil.Wavelength, pupil.ImageIndex, afocal: false, keepPlane, maxDegreeOfParallelism)
        {
            Source = source, Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>n, the pupil nodes per side.</summary>
    public int Sampling { get; }

    /// <summary>The frequency of a lag of one node along +Px (+Py): c·ΔP, cycles per meter (afocal:
    /// cycles per radian), signed as the pupil scale (a negative scale is an inverted image).</summary>
    public double StepX { get; }
    public double StepY { get; }

    /// <summary>2|c|, the frequency at which the shifted pupil no longer overlaps itself along
    /// each axis (cycles per meter, afocal per radian).</summary>
    public double CutoffX => Math.Abs(StepX) * (Sampling - 1);
    public double CutoffY => Math.Abs(StepY) * (Sampling - 1);

    /// <summary>λ in the system's air, meters.</summary>
    public double Wavelength { get; }
    public double ImageIndex { get; }
    public bool Afocal { get; }

    /// <summary>Σ A and Σ A² of the pupil (for the polychromatic energy weights).</summary>
    public double AmplitudeSum { get; }
    public double PowerSum { get; }

    /// <summary>The pupil function this came from (focal), else null.</summary>
    public PupilFunction? Source { get; init; }

    /// <summary>The largest wavefront change between neighbouring lit nodes, waves (afocal only;
    /// a focal pupil's check is <see cref="PsfEngine.CheckSampling"/>).</summary>
    public double MaxStepWaves { get; init; } = double.NaN;

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The energy of this wavelength's term in OP-38's polychromatic PSF, before the wavelength's
    /// weight and the (λ_min/λ)² factor: each wavelength's PSF there is |U|²/(Σ A)², and over one
    /// image period T_x·T_y (T = 1/|step|) Parseval gives ∫|U|² = T_x·T_y·Σ|f|², so the energy is
    /// Σ A² / ((Σ A)²·|step_x·step_y|) (derived). Weighting each wavelength's OTF by its PSF's
    /// energy makes the polychromatic OTF the transform of the polychromatic PSF.
    /// </summary>
    public double PsfEnergy => PowerSum / (AmplitudeSum * AmplitudeSum * Math.Abs(StepX * StepY));

    /// <summary>The OTF at <paramref name="frequency"/> (cycles per meter; afocal, per radian) along
    /// y (tangential) or x (sagittal).</summary>
    public Complex Otf(double frequency, MtfOrientation orientation)
    {
        bool tangential = orientation == MtfOrientation.Tangential;
        double lag = frequency / (tangential ? StepY : StepX);
        var cut = tangential ? _alongY : _alongX;
        return Interpolate(cut, lag);
    }

    /// <summary>The OTF at a frequency (fx, fy) anywhere in the plane; needs the whole lag plane
    /// (<see cref="FromPupil"/> with keepPlane), bilinear between lags.</summary>
    public Complex Otf(double fx, double fy)
    {
        if (_plane is null) throw new InvalidOperationException("MTF: this pupil kept only the axis cuts of its autocorrelation; build it with the whole lag plane for a surface MTF.");
        // OTF(ν) = Σ f(P)·f*(P + ν/c) = conj R(ν/c) with R(k) = Σ f(P + k)·f*(P).
        double lx = fx / StepX, ly = fy / StepY;
        int n = Sampling;
        if (Math.Abs(lx) >= n || Math.Abs(ly) >= n) return Complex.Zero;
        int c0 = (int)Math.Floor(lx), r0 = (int)Math.Floor(ly);
        double tx = lx - c0, ty = ly - r0;
        Complex At(int kr, int kc) => Math.Abs(kr) > n - 1 || Math.Abs(kc) > n - 1 ? Complex.Zero : _plane[kr + n - 1, kc + n - 1];
        var v = (1 - ty) * ((1 - tx) * At(r0, c0) + tx * At(r0, c0 + 1)) + ty * ((1 - tx) * At(r0 + 1, c0) + tx * At(r0 + 1, c0 + 1));
        return Complex.Conjugate(v);
    }

    /// <summary>The OTF at a whole lag (kx, ky) in nodes, exactly; needs the lag plane.</summary>
    public Complex OtfAtLag(int kx, int ky)
    {
        if (_plane is null) throw new InvalidOperationException("MTF: this pupil kept only the axis cuts of its autocorrelation.");
        int n = Sampling;
        if (Math.Abs(kx) > n - 1 || Math.Abs(ky) > n - 1) return Complex.Zero;
        return Complex.Conjugate(_plane[ky + n - 1, kx + n - 1]);
    }

    private static Complex Interpolate(Complex[] cut, double lag)
    {
        // G(k) = OTF at lag k = conj R(k) for k ≥ 0, and G(−k) = R(k) = conj G(k).
        int n = cut.Length;
        double a = Math.Abs(lag);
        if (!(a < n)) return Complex.Zero;
        int k0 = (int)Math.Floor(a);
        double t = a - k0;
        var r0 = cut[k0];
        var r1 = k0 + 1 < n ? cut[k0 + 1] : Complex.Zero;
        var r = (1 - t) * r0 + t * r1;
        return lag >= 0 ? Complex.Conjugate(r) : r;
    }
}

/// <summary>
/// The optical transfer function (OP-39): the FFT engine (<see cref="MtfPupil"/>), the Huygens
/// engine (the transform of a Huygens PSF), the geometric engine (the transform of the ray spot),
/// the diffraction-limited MTF of a circular pupil, the square-wave response by Coltman's series,
/// and <see cref="Evaluate"/>, the tangential and sagittal modulation of one field, wavelength set
/// and frequency that merit-function criteria call.
/// </summary>
public static class MtfEngine
{
    /// <summary>Settings frequencies are in MTF units (cycles per millimeter, or cycles per
    /// milliradian afocal); the engine works in cycles per meter (per radian): × 1000 both ways.</summary>
    public const double PerMtfUnit = 1000;

    /// <summary>
    /// The MTF of an unaberrated, uniformly lit circular pupil at s = ν/ν_c:
    /// (2/π)(acos s − s√(1 − s²)) for 0 ≤ s ≤ 1, zero beyond. Derived: the autocorrelation of the
    /// unit disk at a shift 2s is the area of the lens two unit circles a distance 2s apart make,
    /// 2(acos s − s√(1 − s²)), over the disk's area π.
    /// </summary>
    public static double DiffractionLimitedCircular(double s)
    {
        s = Math.Abs(s);
        if (s >= 1) return 0;
        return 2 / Math.PI * (Math.Acos(s) - s * Math.Sqrt(1 - s * s));
    }

    /// <summary>
    /// The square-wave response (help, "FFT MTF", its equation image saved under
    /// plans/sources/optics/op-39-help/images/eq_FFT_MTF_1.svg):
    /// S(ν) = (4/π)[M(ν)/1 − M(3ν)/3 + M(5ν)/5 − M(7ν)/7 …]. Derived as well: a square wave of unit
    /// modulation is (4/π)Σ (−1)^k cos((2k+1)·2πνx)/(2k+1); each harmonic is passed with modulation
    /// M((2k+1)ν), and the image's peak, at x = 0, is the sum (the OTF's phase left out, as the help's
    /// formula leaves it out). The series stops at the last odd harmonic below the cutoff
    /// (M is zero beyond it); S(0) = 1 since Σ (−1)^k/(2k+1) = π/4.
    /// </summary>
    /// <param name="modulation">M at a frequency in the same unit as the others.</param>
    /// <param name="cutoff">The frequency beyond which M is zero.</param>
    public static double SquareWave(Func<double, double> modulation, double frequency, double cutoff)
    {
        ArgumentNullException.ThrowIfNull(modulation);
        if (!(frequency >= 0) || !double.IsFinite(frequency)) throw new ArgumentException($"MTF: the square-wave frequency must be finite and not negative (it is {frequency:g6}).");
        if (frequency == 0) return 1;
        double sum = 0;
        for (long k = 0; ; k++)
        {
            double harmonic = (2 * k + 1) * frequency;
            if (harmonic >= cutoff) break;
            if (k > 1_000_000)
                throw new ArgumentException($"MTF: the square-wave series at {frequency:g6} needs more than a million harmonics below the cutoff {cutoff:g6}; choose a higher frequency.");
            double term = modulation(harmonic) / (2 * k + 1);
            sum += k % 2 == 0 ? term : -term;
        }
        return 4 / Math.PI * sum;
    }

    /// <summary>
    /// The OTF of several wavelengths: Σ E·OTF / Σ E with E = w·(λ_min/λ)²·<see cref="MtfPupil.PsfEnergy"/>,
    /// the energy of each wavelength's term in OP-38's polychromatic PSF (whose (λ_min/λ)² factor is
    /// the help's Huygens "wFactor"), so this is the transform of that PSF. Each pupil must carry its
    /// OPD against the same (primary wavelength's) reference so lateral colour shows as phase.
    /// </summary>
    public static Complex PolychromaticOtf(IReadOnlyList<MtfPupil> pupils, IReadOnlyList<double> weights, double frequency, MtfOrientation orientation)
    {
        var e = EnergyWeights(pupils, weights);
        Complex sum = 0;
        double total = 0;
        for (int i = 0; i < pupils.Count; i++)
        {
            if (e[i] == 0) continue;
            sum += e[i] * pupils[i].Otf(frequency, orientation);
            total += e[i];
        }
        return sum / total;
    }

    /// <summary>The polychromatic energy weights of <see cref="PolychromaticOtf"/>.</summary>
    public static double[] EnergyWeights(IReadOnlyList<MtfPupil> pupils, IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(pupils);
        ArgumentNullException.ThrowIfNull(weights);
        if (pupils.Count == 0 || pupils.Count != weights.Count)
            throw new ArgumentException($"MTF: one weight per wavelength ({pupils.Count} pupils, {weights.Count} weights).");
        double lmin = pupils.Min(p => p.Wavelength);
        var e = new double[pupils.Count];
        double total = 0;
        for (int i = 0; i < pupils.Count; i++)
        {
            double w = weights[i];
            if (!(w >= 0) || !double.IsFinite(w)) throw new ArgumentException($"MTF: wavelength {i + 1}'s weight is {w:g6}; it must be finite and not negative.");
            double r = lmin / pupils[i].Wavelength;
            e[i] = w * r * r * pupils[i].PsfEnergy;
            total += e[i];
        }
        if (!(total > 0)) throw new ArgumentException("MTF: every wavelength weight is zero.");
        return e;
    }

    /// <summary>
    /// The OTF of a sampled PSF by direct transform: Σ I·e^{−2πiν·y} / Σ I (tangential; x for
    /// sagittal) over every sample. This is the help's Huygens MTF ("an FFT of the Huygens PSF")
    /// evaluated at any frequency; it is exact for the samples, and meaningful up to the grid's
    /// Nyquist frequency 1/(2Δ) and while the PSF lies inside the grid.
    /// </summary>
    public static Complex PsfOtf(PsfImage image, double frequency, MtfOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(image);
        bool tangential = orientation == MtfOrientation.Tangential;
        int rows = image.Y.Count, columns = image.X.Count;
        // Sum each row (column) first: the transform runs along one axis only.
        int count = tangential ? rows : columns;
        Complex sum = 0;
        double total = 0;
        for (int p = 0; p < count; p++)
        {
            double line = 0;
            if (tangential) for (int c = 0; c < columns; c++) line += image.Intensity[p, c];
            else for (int r = 0; r < rows; r++) line += image.Intensity[r, p];
            total += line;
            double coordinate = tangential ? image.Y[p] : image.X[p];
            sum += line * PsfEngine.Turn(-frequency * coordinate);
        }
        if (!(total > 0)) throw new InvalidOperationException("MTF: the PSF holds no light in its window.");
        return sum / total;
    }

    /// <summary>The OTF of a sampled PSF at (fx, fy): Σ I·e^{−2πi(fx·x + fy·y)} / Σ I.</summary>
    public static Complex PsfOtf(PsfImage image, double fx, double fy)
    {
        ArgumentNullException.ThrowIfNull(image);
        int rows = image.Y.Count, columns = image.X.Count;
        var xf = new Complex[columns];
        for (int c = 0; c < columns; c++) xf[c] = PsfEngine.Turn(-fx * image.X[c]);
        Complex sum = 0;
        double total = 0;
        for (int r = 0; r < rows; r++)
        {
            Complex line = 0;
            for (int c = 0; c < columns; c++)
            {
                double v = image.Intensity[r, c];
                total += v;
                line += v * xf[c];
            }
            sum += line * PsfEngine.Turn(-fy * image.Y[r]);
        }
        if (!(total > 0)) throw new InvalidOperationException("MTF: the PSF holds no light in its window.");
        return sum / total;
    }

    /// <summary>
    /// The geometric OTF: Σ w·e^{−2πiν·y} / Σ w over the rays (x for sagittal). The rays sample the
    /// pupil evenly, so their density on the image is the geometric PSF and its projection on one
    /// axis the geometric line spread function; this is that line spread function's transform,
    /// evaluated exactly for the ray set (no binning).
    /// </summary>
    public static Complex GeometricOtf(IReadOnlyList<GeometricRay> rays, double frequency, MtfOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(rays);
        bool tangential = orientation == MtfOrientation.Tangential;
        Complex sum = 0;
        double total = 0;
        foreach (var ray in rays)
        {
            if (ray.Weight == 0) continue;
            sum += ray.Weight * PsfEngine.Turn(-frequency * (tangential ? ray.Y : ray.X));
            total += ray.Weight;
        }
        if (!(total > 0)) throw new InvalidOperationException("Geometric MTF: no ray carries light to the image.");
        return sum / total;
    }

    // ------------------------------------------------------------------ From the lens

    /// <summary>
    /// The FFT MTF pupil of one field and wavelength. Focal: OP-38's pupil function
    /// (<see cref="PsfEngine.BuildPupil"/>, the same nodes, OPD reference and pupil scale), so the
    /// MTF is the transform of OP-38's FFT PSF. Afocal (<see cref="BuildAfocal"/>): the OPD against
    /// the afocal reference plane, and the frequency in cycles per radian.
    /// </summary>
    public static MtfPupil BuildPupil(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        OpdOptions? options = null, bool unaberrated = false, bool keepPlane = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.Aperture.AfocalImageSpace) return BuildAfocal(lens, system, field, wavelength, sampling, options, unaberrated, keepPlane, cancellation);
        var pupil = PsfEngine.BuildPupil(lens, system, field, wavelength, sampling, options, cancellation);
        var used = unaberrated ? pupil.Unaberrated() : pupil;
        var warnings = unaberrated ? pupil.Warnings : PsfEngine.CheckSampling(pupil).Warnings.Concat(pupil.Warnings).ToArray();
        return MtfPupil.FromPupil(used, keepPlane, 1, pupil.Notes, warnings, pupil);
    }

    /// <summary>
    /// An afocal pupil: <see cref="OpdEngine"/> on the same n × n nodes as OP-38 (the afocal
    /// reference plane at the exit pupil, normal to the chief ray), the amplitude OP-38's pupil
    /// apodization where the ray gives an OPD, and the beam's scale b (meters per unit pupil
    /// coordinate) fitted by least squares to where the rays cross that plane, on two axes normal
    /// to the chief ray. A node then stands for the transverse position b·P, and the far field
    /// at a direction offset θ is Σ f·e^{−2πi·n·b·P·θ/λ} (derived), so the frequency step is
    /// n·b·ΔP/λ cycles per radian and the cutoff 2n·b/λ, the exit pupil diameter over the
    /// wavelength in air (help, "FFT MTF").
    /// </summary>
    internal static MtfPupil BuildAfocal(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        OpdOptions? options, bool unaberrated, bool keepPlane, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(lens);
        string where = $"FFT MTF (field {field + 1}, wavelength {wavelength + 1})";
        if (sampling < 4) throw new ArgumentException($"{where}: the pupil sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        options ??= new OpdOptions();
        var (nodes, index) = PsfEngine.Nodes(sampling);
        var opd = OpdEngine.Compute(lens, system, field, wavelength, nodes, options, cancellation);
        var apodization = PsfEngine.Apodization(lens, system, where);
        var chief = opd.ChiefRay;
        var u = opd.Geometry.Normal ?? chief.Direction;
        var q = opd.Geometry.Point;
        var (e1, e2) = PsfEngine.PlaneAxes(u);
        int n = sampling;
        var amplitude = new double[n, n];
        var waves = new double[n, n];
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        double sxx = 0, sx = 0, syy = 0, sy = 0;
        int lit = 0;
        for (int j = 0; j < nodes.Count; j++)
        {
            var s = opd.Samples[j];
            if (!s.IsValid)
            {
                string reason = s.Failure ?? "failed";
                failures[reason] = failures.TryGetValue(reason, out int c) ? c + 1 : 1;
                continue;
            }
            var (row, column) = index[j];
            amplitude[row, column] = apodization(Math.Sqrt(s.Px * s.Px + s.Py * s.Py));
            waves[row, column] = unaberrated ? 0 : s.Waves;
            double du = Vector3D.Dot(s.Ray.Direction, u);
            var x = s.Ray.Position + s.Ray.Direction * (Vector3D.Dot(q - s.Ray.Position, u) / du) - q;
            sxx += s.Px * s.Px; sx += Vector3D.Dot(x, e1) * s.Px;
            syy += s.Py * s.Py; sy += Vector3D.Dot(x, e2) * s.Py;
            lit++;
        }
        if (lit == 0 || sxx == 0 || syy == 0)
            throw new InvalidOperationException($"{where}: too few rays reach the image to span the pupil in x and y ({lit} of {nodes.Count} reach it).");
        double bx = sx / sxx, by = sy / syy;
        if (!(Math.Abs(bx) > 0) || !(Math.Abs(by) > 0))
            throw new InvalidOperationException($"{where}: the afocal beam has no width across the pupil (scale {bx:g3}, {by:g3} m per unit pupil coordinate).");
        double lambda = OpticsConventions.MicrometersToMeters(opd.AirMicrometers);
        double dp = 2.0 / (n - 1);
        double cx = opd.ImageIndex * bx / lambda, cy = opd.ImageIndex * by / lambda;
        var field2 = new Complex[n, n];
        double asum = 0, psum = 0, step = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double a = amplitude[i, k];
                if (a == 0) continue;
                double w = waves[i, k];
                double phase = 2 * Math.PI * (w - Math.Floor(w));
                field2[i, k] = new Complex(a * Math.Cos(phase), a * Math.Sin(phase));
                asum += a; psum += a * a;
                if (k + 1 < n && amplitude[i, k + 1] > 0) step = Math.Max(step, Math.Abs(waves[i, k + 1] - w));
                if (i + 1 < n && amplitude[i + 1, k] > 0) step = Math.Max(step, Math.Abs(waves[i + 1, k] - w));
            }
        var warnings = new List<string>();
        if (failures.Count > 0)
            warnings.Add($"{nodes.Count - lit} of {nodes.Count} pupil rays carry no light: " + string.Join("; ", failures.Select(f => $"{f.Value} × {f.Key}")) + ".");
        if (step > PsfEngine.AliasStepWaves)
            warnings.Add($"ALIASED: the wavefront changes by up to {step:g3} waves between neighbouring pupil samples (the limit is {PsfEngine.AliasStepWaves} wave); the sampled autocorrelation is not a faithful MTF. Raise the pupil sampling above {n}.");
        else if (step > PsfEngine.CautionStepWaves)
            warnings.Add($"Near the sampling limit: the wavefront changes by up to {step:g3} waves between neighbouring pupil samples (aliasing starts at {PsfEngine.AliasStepWaves}). Raise the pupil sampling above {n}.");
        var notes = new List<string>(opd.Notes)
        {
            $"Afocal pupil {n} × {n} nodes from −1 to 1; amplitude {ApodizationTypes.Names.Label(system.Aperture.Apodization)} where the ray gives an OPD; beam scale fitted to the rays' crossings of the reference plane: {bx * 1e3:g6} mm in x, {by * 1e3:g6} mm in y per unit pupil coordinate (cutoff 2n·b/λ = {2 * Math.Abs(cx) / 1000:g6} × {2 * Math.Abs(cy) / 1000:g6} cycles/mrad).",
        };
        return new MtfPupil(field2, asum, psum, cx * dp, cy * dp, lambda, opd.ImageIndex, afocal: true, keepPlane, 1)
        {
            MaxStepWaves = step, Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>
    /// The rays of a geometric MTF for one field and wavelength: OP-38's n × n pupil nodes traced
    /// to the image surface (<see cref="OpdEngine"/>; only the rays are used, so a surface whose
    /// shape limits the OPD is accepted), each carrying its pupil apodization squared (power) where
    /// it reaches the image. Focal: its image-surface x and y (help, "Geometric MTF": "the
    /// computation is done in image space coordinates"). Afocal: its direction cosines along the
    /// image surface's x and y, read as radians (small angles). Also returns the unaberrated pupil
    /// (the same amplitudes, no OPD, OP-38's fitted scale) for the diffraction-limit factor.
    /// </summary>
    public static GeometricSpot BuildGeometric(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        bool checkApertures = true, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        string where = $"Geometric MTF (field {field + 1}, wavelength {wavelength + 1})";
        if (sampling < 4) throw new ArgumentException($"{where}: the pupil sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        bool afocal = system.Aperture.AfocalImageSpace;
        var options = new OpdOptions
        {
            // Only the rays are used; the Absolute reference needs no reference sphere.
            Reference = OpdReference.Absolute, ReferencePoint = OpdReferencePoint.ChiefRay,
            IgnoreApertures = !checkApertures, AllowOpdLimitations = true,
        };
        var (nodes, index) = PsfEngine.Nodes(sampling);
        var opd = OpdEngine.Compute(lens, system, field, wavelength, nodes, options, cancellation);
        var apodization = PsfEngine.Apodization(lens, system, where);
        var chief = opd.ChiefRay;
        var (e1, e2) = PsfEngine.PlaneAxes(chief.Direction);
        int n = sampling;
        var amplitude = new double[n, n];
        var rays = new List<GeometricRay>();
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        double sxx = 0, sx = 0, syy = 0, sy = 0;
        for (int j = 0; j < nodes.Count; j++)
        {
            var s = opd.Samples[j];
            if (!s.Ray.IsOk)
            {
                string reason = $"{RayStatuses.Label(s.Ray.Status)} at surface {s.Ray.FailedAtSurface}";
                failures[reason] = failures.TryGetValue(reason, out int c) ? c + 1 : 1;
                continue;
            }
            double a = apodization(Math.Sqrt(s.Px * s.Px + s.Py * s.Py));
            var (row, column) = index[j];
            amplitude[row, column] = a;
            double x = afocal ? s.Ray.Direction.X - chief.Direction.X : s.Ray.Position.X - chief.Position.X;
            double y = afocal ? s.Ray.Direction.Y - chief.Direction.Y : s.Ray.Position.Y - chief.Position.Y;
            rays.Add(new GeometricRay(x, y, a * a));
            var dd = s.Ray.Direction - chief.Direction;
            sxx += s.Px * s.Px; sx += Vector3D.Dot(dd, e1) * s.Px;
            syy += s.Py * s.Py; sy += Vector3D.Dot(dd, e2) * s.Py;
        }
        if (rays.Count == 0) throw new InvalidOperationException($"{where}: no ray of the {nodes.Count} launched reaches the image.");
        var warnings = new List<string>();
        if (failures.Count > 0)
            warnings.Add($"{nodes.Count - rays.Count} of {nodes.Count} pupil rays do not reach the image and carry no light: " + string.Join("; ", failures.Select(f => $"{f.Value} × {f.Key}")) + ".");
        double lambda = OpticsConventions.MicrometersToMeters(opd.AirMicrometers);

        // The unaberrated pupil for the diffraction-limited factor.
        MtfPupil? diffraction = null;
        double cutoffX = double.NaN, cutoffY = double.NaN;
        if (!afocal && sxx > 0 && syy > 0 && Math.Abs(sx / sxx) > 0 && Math.Abs(sy / syy) > 0)
        {
            var pupil = new PupilFunction(amplitude, new double[n, n], sx / sxx, sy / syy, lambda, opd.ImageIndex);
            diffraction = MtfPupil.FromPupil(pupil);
            cutoffX = diffraction.CutoffX; cutoffY = diffraction.CutoffY;
        }
        else if (afocal)
        {
            diffraction = BuildAfocal(lens, system, field, wavelength, sampling,
                new OpdOptions { IgnoreApertures = !checkApertures, AllowOpdLimitations = true }, unaberrated: true, keepPlane: false, cancellation);
            cutoffX = diffraction.CutoffX; cutoffY = diffraction.CutoffY;
        }
        var notes = new List<string>
        {
            $"Rays: {n} × {n} pupil nodes from −1 to 1 ({nodes.Count} inside the unit circle), {rays.Count} reaching the image, each weighted by its apodization squared ({ApodizationTypes.Names.Label(system.Aperture.Apodization)}).",
            afocal ? "Afocal: each ray's direction cosines along the image surface's x and y relative to the chief ray's, read as radians."
                : "Each ray's image-surface x and y relative to the chief ray's intercept.",
        };
        return new GeometricSpot
        {
            Rays = rays, Wavelength = lambda, Afocal = afocal, Diffraction = diffraction, CutoffX = cutoffX, CutoffY = cutoffY,
            ChiefX = afocal ? chief.Direction.X : chief.Position.X, ChiefY = afocal ? chief.Direction.Y : chief.Position.Y,
            Notes = notes, Warnings = warnings,
        };
    }

    // ------------------------------------------------------------------ One point

    /// <summary>
    /// The tangential and sagittal modulation of one field row and wavelength set at one frequency
    /// (MTF units: cycles/mm, or cycles/mrad in afocal image space), by any engine: the call a
    /// merit-function criterion makes (OP-56's contrast criterion, OP-64's MTF criterion). The
    /// lens and system are the configuration's own (resolve a multi-configuration system first).
    /// Several wavelength rows give the polychromatic MTF. Square wave on request (FFT only, as the
    /// help offers it).
    /// </summary>
    public static MtfValue Evaluate(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> wavelengths, double frequency,
        MtfOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(wavelengths);
        options ??= new MtfOptions();
        if (!(frequency >= 0) || !double.IsFinite(frequency))
            throw new ArgumentException($"MTF: the frequency must be finite and not negative (it is {frequency:g6}).", nameof(frequency));
        if (wavelengths.Count == 0) throw new ArgumentException("MTF: at least one wavelength row.", nameof(wavelengths));
        if (options.SquareWave && options.Method != MtfMethod.Fft)
            throw new ArgumentException("MTF: the square-wave response is offered with the FFT method only (help, \"FFT MTF\"; the Huygens and geometric MTF offer modulation only).");
        MtfUnitsCheck.Check(system, "MTF");
        var point = MtfCore.Evaluate(options.ToMethod(), lens, system, field, wavelengths.ToArray(), wavelengths.Count > 1, cancellation);
        double nu = frequency * PerMtfUnit;
        var type = options.SquareWave ? MtfDataType.SquareWave : MtfDataType.Modulation;
        return new MtfValue(MtfCore.Value(point, nu, MtfOrientation.Tangential, type), MtfCore.Value(point, nu, MtfOrientation.Sagittal, type), point.Warnings);
    }
}

/// <summary>The tangential and sagittal modulation at one frequency, and what to be wary of.</summary>
public sealed record MtfValue(double Tangential, double Sagittal, IReadOnlyList<string> Warnings)
{
    public double Average => (Tangential + Sagittal) / 2;
}

/// <summary>How <see cref="MtfEngine.Evaluate"/> computes the MTF.</summary>
public sealed record MtfOptions
{
    public MtfMethod Method { get; init; } = MtfMethod.Fft;
    /// <summary>Pupil nodes per side (FFT, geometric; the Huygens pupil sampling).</summary>
    public int Sampling { get; init; } = 64;
    /// <summary>Huygens: image points per side.</summary>
    public int ImageSampling { get; init; } = 64;
    /// <summary>Huygens: image spacing in meters; 0 for the help's default λF/√n.</summary>
    public double ImageDelta { get; init; }
    /// <summary>Geometric: multiply by the diffraction-limited MTF of the same pupil (help: "Should always be used").</summary>
    public bool MultiplyByDiffractionLimit { get; init; } = true;
    /// <summary>FFT: the square-wave response instead of the sine-wave modulation.</summary>
    public bool SquareWave { get; init; }
    public bool CheckApertures { get; init; } = true;
    public bool AllowOpdLimitations { get; init; }

    internal MtfMethodSettings ToMethod() => new()
    {
        Method = Method, Sampling = Sampling, ImageSampling = ImageSampling, ImageDelta = ImageDelta,
        MultiplyByDiffractionLimit = MultiplyByDiffractionLimit, CheckApertures = CheckApertures, AllowOpdLimitations = AllowOpdLimitations,
    };
}

/// <summary>The rays of one wavelength for a geometric MTF (<see cref="MtfEngine.BuildGeometric"/>).</summary>
public sealed record GeometricSpot
{
    /// <summary>Relative to the chief ray (meters on the image surface; afocal, radians).</summary>
    public required IReadOnlyList<GeometricRay> Rays { get; init; }
    public required double Wavelength { get; init; }
    public required bool Afocal { get; init; }
    /// <summary>The chief ray's x and y (meters; afocal, direction cosines), to bring several
    /// wavelengths to one origin.</summary>
    public double ChiefX { get; init; }
    public double ChiefY { get; init; }
    /// <summary>The unaberrated pupil's MTF (the diffraction-limited factor), or null when the
    /// pupil scale could not be fitted.</summary>
    public MtfPupil? Diffraction { get; init; }
    /// <summary>The diffraction cutoff of the same pupil along x and y (per meter or per radian).</summary>
    public double CutoffX { get; init; } = double.NaN;
    public double CutoffY { get; init; } = double.NaN;
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
