using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.EnclosedEnergy;

/// <summary>
/// The diffraction enclosed energy of one pupil function (OP-41, docs/Optics-Conventions.md §39),
/// computed exactly for the sampled pupil rather than by summing image samples.
/// <para>
/// <b>Derivation.</b> The PSF of OP-38 (§33) is I(x) = |Σ_k p_k e^{−2πi·n(a_x·Px_k·x + a_y·Py_k·y)/λ}|²
/// with p = A·e^{2πiW} on nodes spaced ΔP. Expanding the square, with m = k − l the node-index
/// difference, I(x) = Σ_m C_m e^{−i k_m·x}, where C_m = Σ_l p_{l+m}·conj(p_l) is the pupil's
/// autocorrelation (the unnormalised OTF) and k_m = 2π·n·ΔP·(a_x·m_x, a_y·m_y)/λ. I is periodic
/// with period T = λ/(n·|a|·ΔP) on each axis (§33), and one period holds the energy C_0·T_x·T_y.
/// The energy in a region R centred on x₀ is Σ_m C_m e^{−i k_m·x₀} D_R(k_m), D_R(k) = ∫_R0 e^{−ik·x}dx
/// for R0 the region centred on 0: the disk of radius r gives 2πr·J₁(|k|r)/|k| (πr² at k = 0), the
/// square of half-width h gives (2 sin(k_x h)/k_x)(2 sin(k_y h)/k_y), and the slit |x| ≤ h over one
/// period in y keeps only m_y = 0, with 2 sin(k_x h)/k_x · T_y. C_{−m} = conj(C_m) and D is even,
/// so each ± pair adds 2·Re(C_m e^{−i k_m·x₀})·D. The enclosed fraction is that energy over
/// C_0·T_x·T_y. The only approximations are those of the sampled pupil itself (the node grid and
/// the periodic image of §33's sampling rule); no image grid enters. A region is refused once it
/// would overlap its own periodic copies (a radius or half-width above T/2).
/// </para>
/// <para>
/// C is computed by FFT of the pupil zero-padded to 2n × 2n (|FFT|² inverted: the circular
/// autocorrelation, equal to the linear one for lags up to n − 1). Lags with |m|·ΔP &gt; 2 are
/// dropped: two nodes inside the unit circle are never further apart than 2, so those C_m are zero
/// and what the FFT returns there is rounding.
/// </para>
/// </summary>
public sealed class DiffractionEnclosedEnergyKernel
{
    private readonly double _c0;
    // Half-plane pairs: m_y > 0, or m_y = 0 and m_x > 0.
    private readonly double[] _kx, _ky, _re, _im;
    // The m_y = 0, m_x > 0 terms (x-only) and the m_x = 0, m_y > 0 terms (y-only).
    private readonly double[] _rowK, _rowRe, _rowIm, _colK, _colRe, _colIm;

    private DiffractionEnclosedEnergyKernel(PupilFunction pupil, double c0, double[] kx, double[] ky, double[] re, double[] im,
        double[] rowK, double[] rowRe, double[] rowIm, double[] colK, double[] colRe, double[] colIm)
    {
        Pupil = pupil;
        _c0 = c0;
        _kx = kx; _ky = ky; _re = re; _im = im;
        _rowK = rowK; _rowRe = rowRe; _rowIm = rowIm;
        _colK = colK; _colRe = colRe; _colIm = colIm;
        double common = pupil.Wavelength / (pupil.ImageIndex * pupil.NodeSpacing);
        PeriodX = common / Math.Abs(pupil.ScaleX);
        PeriodY = common / Math.Abs(pupil.ScaleY);
    }

    /// <summary>The pupil the kernel was built from.</summary>
    public PupilFunction Pupil { get; }

    /// <summary>T_x = λ/(n·|a_x|·ΔP), meters: the PSF's period in x.</summary>
    public double PeriodX { get; }

    /// <summary>T_y, the same in y.</summary>
    public double PeriodY { get; }

    /// <summary>The number of autocorrelation terms kept (each ± pair counted once).</summary>
    public int TermCount => _kx.Length;

    /// <summary>The largest size the region may have before it overlaps its periodic copies:
    /// T/2 on the axis it limits (the smaller of the two for a circle or a square).</summary>
    public double MaximumSize(EnclosedEnergyType type) => type switch
    {
        EnclosedEnergyType.XOnly => PeriodX / 2,
        EnclosedEnergyType.YOnly => PeriodY / 2,
        _ => Math.Min(PeriodX, PeriodY) / 2,
    };

    /// <summary>The kernel of <paramref name="pupil"/> (see the class remarks).</summary>
    public static DiffractionEnclosedEnergyKernel FromPupil(PupilFunction pupil, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        int n = pupil.Sampling, m = 2 * n;
        var field = pupil.Field();
        var padded = new Complex[m, m];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++) padded[i, k] = field[i, k];
        cancellation.ThrowIfCancellationRequested();
        var spectrum = Fft2D.Forward(padded, maxDegreeOfParallelism);
        for (int i = 0; i < m; i++)
            for (int k = 0; k < m; k++)
            {
                var s = spectrum[i, k];
                spectrum[i, k] = new Complex(s.Real * s.Real + s.Imaginary * s.Imaginary, 0);
            }
        cancellation.ThrowIfCancellationRequested();
        var r = Fft2D.Inverse(spectrum, maxDegreeOfParallelism);
        cancellation.ThrowIfCancellationRequested();

        double dp = pupil.NodeSpacing;
        double fx = 2 * Math.PI * pupil.ImageIndex * pupil.ScaleX * dp / pupil.Wavelength;
        double fy = 2 * Math.PI * pupil.ImageIndex * pupil.ScaleY * dp / pupil.Wavelength;
        double limit = 4 * (1 + 1e-12);
        var kx = new List<double>(); var ky = new List<double>(); var re = new List<double>(); var im = new List<double>();
        var rowK = new List<double>(); var rowRe = new List<double>(); var rowIm = new List<double>();
        var colK = new List<double>(); var colRe = new List<double>(); var colIm = new List<double>();
        for (int my = 0; my < n; my++)
            for (int mx = -(n - 1); mx < n; mx++)
            {
                if (my == 0 && mx <= 0) continue;
                if (((double)mx * mx + (double)my * my) * dp * dp > limit) continue;
                // C_m = r[m_y mod M, m_x mod M] (row = Py lag, column = Px lag).
                var c = r[my, ((mx % m) + m) % m];
                kx.Add(fx * mx); ky.Add(fy * my); re.Add(c.Real); im.Add(c.Imaginary);
                if (my == 0) { rowK.Add(fx * mx); rowRe.Add(c.Real); rowIm.Add(c.Imaginary); }
                if (mx == 0) { colK.Add(fy * my); colRe.Add(c.Real); colIm.Add(c.Imaginary); }
            }
        double c0 = r[0, 0].Real;
        if (!(c0 > 0)) throw new ArgumentException("Diffraction enclosed energy: the pupil passes no light.");
        return new DiffractionEnclosedEnergyKernel(pupil, c0, kx.ToArray(), ky.ToArray(), re.ToArray(), im.ToArray(),
            rowK.ToArray(), rowRe.ToArray(), rowIm.ToArray(), colK.ToArray(), colRe.ToArray(), colIm.ToArray());
    }

    /// <summary>
    /// The fraction of the PSF's energy inside the region of <paramref name="type"/> and
    /// <paramref name="size"/> (radius or half-width, meters) centred on (x0, y0), meters from
    /// the PSF's reference point on its own axes (§33). Throws when the size exceeds
    /// <see cref="MaximumSize"/>.
    /// </summary>
    public double Fraction(EnclosedEnergyType type, double size, double x0 = 0, double y0 = 0)
    {
        if (!(size >= 0) || !double.IsFinite(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Diffraction enclosed energy: the size must be finite and not negative.");
        if (!double.IsFinite(x0) || !double.IsFinite(y0))
            throw new ArgumentOutOfRangeException(nameof(x0), "Diffraction enclosed energy: the reference offset must be finite.");
        double max = MaximumSize(type);
        if (size > max * (1 + 1e-12))
            throw new ArgumentOutOfRangeException(nameof(size), size,
                $"Diffraction enclosed energy: a size of {size * 1e6:g6} µm is beyond half the PSF's period ({max * 1e6:g6} µm), where the {EnclosedEnergyRegions.TypeLabel(type)} region would overlap the PSF's periodic copies. Raise the pupil sampling (the period grows with it) or ask for a smaller size.");
        if (size == 0) return 0;
        double sum;
        switch (type)
        {
            case EnclosedEnergyType.Encircled:
            {
                double rr = size;
                sum = _c0 * Math.PI * rr * rr;
                for (int j = 0; j < _kx.Length; j++)
                {
                    double q = Math.Sqrt(_kx[j] * _kx[j] + _ky[j] * _ky[j]);
                    double d = 2 * Math.PI * rr * Bessel.J1(q * rr) / q;
                    double phase = _kx[j] * x0 + _ky[j] * y0;
                    sum += 2 * (_re[j] * Math.Cos(phase) + _im[j] * Math.Sin(phase)) * d;
                }
                return sum / (_c0 * PeriodX * PeriodY);
            }
            case EnclosedEnergyType.Ensquared:
            {
                double h = size;
                sum = _c0 * 4 * h * h;
                for (int j = 0; j < _kx.Length; j++)
                {
                    double d = Slit(_kx[j], h) * Slit(_ky[j], h);
                    double phase = _kx[j] * x0 + _ky[j] * y0;
                    sum += 2 * (_re[j] * Math.Cos(phase) + _im[j] * Math.Sin(phase)) * d;
                }
                return sum / (_c0 * PeriodX * PeriodY);
            }
            case EnclosedEnergyType.XOnly:
                return SlitFraction(_rowK, _rowRe, _rowIm, size, x0, PeriodX);
            case EnclosedEnergyType.YOnly:
                return SlitFraction(_colK, _colRe, _colIm, size, y0, PeriodY);
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown enclosed-energy type.");
        }
    }

    private double SlitFraction(double[] k, double[] re, double[] im, double h, double offset, double period)
    {
        // The other axis integrates over one period, which cancels against the cell's energy.
        double sum = _c0 * 2 * h;
        for (int j = 0; j < k.Length; j++)
        {
            double phase = k[j] * offset;
            sum += 2 * (re[j] * Math.Cos(phase) + im[j] * Math.Sin(phase)) * Slit(k[j], h);
        }
        return sum / (_c0 * period);
    }

    /// <summary>∫_{−h}^{h} e^{−ikx} dx = 2 sin(kh)/k, 2h at k = 0.</summary>
    private static double Slit(double k, double h) => k == 0 ? 2 * h : 2 * Math.Sin(k * h) / k;

    /// <summary><see cref="Fraction"/> at each size, one slot per size (each summed in the same
    /// order, so the result is bitwise the same at any degree of parallelism).</summary>
    public double[] Fractions(EnclosedEnergyType type, IReadOnlyList<double> sizes, double x0 = 0, double y0 = 0,
        int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return ParallelSlots.Compute(sizes.Count, maxDegreeOfParallelism, i =>
        {
            cancellation.ThrowIfCancellationRequested();
            return Fraction(type, sizes[i], x0, y0);
        }, cancellation);
    }
}

/// <summary>
/// Everything one field's diffraction enclosed energy rests on (OP-41): a kernel per wavelength,
/// each wavelength's share of the light, and where the reference point lies in each wavelength's
/// PSF plane. Built by <see cref="DiffractionEnclosedEnergy.Prepare"/>.
/// </summary>
public sealed record DiffractionEnclosedEnergySetup
{
    public required IReadOnlyList<DiffractionEnclosedEnergyKernel> Kernels { get; init; }

    /// <summary>Each wavelength's share of the light, summing to 1: its weight times the
    /// fraction of a clear pupil it passes (<see cref="PupilFunction.Transmission"/>).</summary>
    public required IReadOnlyList<double> Shares { get; init; }

    /// <summary>The reference point in each wavelength's PSF plane, meters from that PSF's
    /// reference point on its own axes.</summary>
    public required IReadOnlyList<(double X, double Y)> Offsets { get; init; }

    /// <summary>0-based wavelength rows, one per kernel.</summary>
    public IReadOnlyList<int> Wavelengths { get; init; } = Array.Empty<int>();

    /// <summary>0-based field row; −1 for a setup built from pupils directly.</summary>
    public int Field { get; init; } = -1;

    public EnclosedEnergyReference Reference { get; init; }

    /// <summary>The reference point in the image surface's frame (meters), when it came from a
    /// lens; null otherwise.</summary>
    public Vector3D? ReferencePoint { get; init; }

    /// <summary>A size that shows the curve (meters): the smaller of the largest allowed size and
    /// the larger of 5·λ·F (λ the longest wavelength, F its slower working F/#) and 1.5 times the
    /// largest ray spread from the chief ray. The help gives no default; this is the program's.</summary>
    public double DefaultSize { get; init; } = double.NaN;

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The largest size every wavelength allows.</summary>
    public double MaximumSize(EnclosedEnergyType type) => Kernels.Min(k => k.MaximumSize(type));

    /// <summary>Σ share × each wavelength's fraction.</summary>
    public double Fraction(EnclosedEnergyType type, double size)
    {
        double sum = 0;
        for (int i = 0; i < Kernels.Count; i++)
            if (Shares[i] > 0) sum += Shares[i] * Kernels[i].Fraction(type, size, Offsets[i].X, Offsets[i].Y);
        return sum;
    }

    /// <summary><see cref="Fraction"/> at each size, one slot per size.</summary>
    public double[] Fractions(EnclosedEnergyType type, IReadOnlyList<double> sizes, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return ParallelSlots.Compute(sizes.Count, maxDegreeOfParallelism, i =>
        {
            cancellation.ThrowIfCancellationRequested();
            return Fraction(type, sizes[i]);
        }, cancellation);
    }
}

/// <summary>
/// OpticStudio's Diffraction Enclosed Energy (OP-41; help, "Diffraction (enclosed energy)": "the
/// percentage of total energy enclosed as a function of distance from either the chief ray or the
/// image centroid at the image of a point object") from OP-38's pupil function
/// (<see cref="PsfEngine.BuildPupil"/>) by <see cref="DiffractionEnclosedEnergyKernel"/>. Public so
/// that other items (sensitivity criteria) can ask for the fraction at one size.
/// </summary>
public static class DiffractionEnclosedEnergy
{
    /// <summary>
    /// The setup of one field (0-based row) at the given wavelength rows (more than one:
    /// polychromatic) on an n × n pupil grid. Polychromatic pupils are referenced to the primary
    /// wavelength's chief ray (§33), so every PSF's reference point is that ray's image point.
    /// The reference point is projected along each wavelength's chief ray onto its PSF plane.
    /// The centroid is the light-weighted mean image-surface intercept of the pupil rays (each ray
    /// A²/ΣA² within its wavelength, each wavelength its share). <see cref="EnclosedEnergyReference.Middle"/>
    /// is refused (the help offers it for the geometric enclosed energy only).
    /// </summary>
    public static DiffractionEnclosedEnergySetup Prepare(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> wavelengths,
        int sampling, EnclosedEnergyReference reference, bool checkApertures = true, bool allowOpdLimitations = false,
        int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(wavelengths);
        string who = $"Diffraction enclosed energy (field {field + 1})";
        if (field < 0 || field >= system.Fields.Points.Count)
            throw new ArgumentOutOfRangeException(nameof(field), field, $"{who}: field {field + 1} is not one of the {system.Fields.Points.Count} fields.");
        if (wavelengths.Count == 0) throw new ArgumentException($"{who}: at least one wavelength.", nameof(wavelengths));
        foreach (int w in wavelengths)
            if (w < 0 || w >= system.Wavelengths.Wavelengths.Count)
                throw new ArgumentOutOfRangeException(nameof(wavelengths), w, $"{who}: wavelength {w + 1} is not one of the {system.Wavelengths.Wavelengths.Count} wavelengths.");
        if (reference == EnclosedEnergyReference.Middle)
            throw new ArgumentException($"{who}: the middle reference belongs to the geometric enclosed energy; choose the chief ray, the centroid or the vertex.", nameof(reference));
        bool poly = wavelengths.Count > 1;
        var options = PsfRuns.Options(poly, checkApertures, allowOpdLimitations);
        var pupils = new List<PupilFunction>();
        var weights = new List<double>();
        foreach (int w in wavelengths)
        {
            cancellation.ThrowIfCancellationRequested();
            double weight = poly ? system.Wavelengths.Wavelengths[w].Weight : 1;
            if (!(weight >= 0) || !double.IsFinite(weight))
                throw new ArgumentException($"{who}: wavelength {w + 1}'s weight is {weight:g6}; it must be finite and not negative.");
            pupils.Add(PsfEngine.BuildPupil(lens, system, field, w, sampling, options, cancellation));
            weights.Add(weight);
        }
        var kernels = pupils.Select(p => DiffractionEnclosedEnergyKernel.FromPupil(p, maxDegreeOfParallelism, cancellation)).ToArray();
        var shares = Shares(pupils, weights, who);

        // The reference point in the image surface's frame, and its offset in each PSF plane.
        Vector3D? point = reference switch
        {
            EnclosedEnergyReference.Vertex => Vector3D.Zero,
            EnclosedEnergyReference.Centroid => Centroid(pupils, shares),
            _ => null,
        };
        var offsets = new (double, double)[pupils.Count];
        var warnings = new List<string>();
        var notes = new List<string>(pupils[0].Notes);
        for (int i = 0; i < pupils.Count; i++)
        {
            var opd = pupils[i].Opd!;
            if (point is Vector3D p)
            {
                var (e1, e2) = PsfEngine.PlaneAxes(opd.ChiefRay.Direction);
                var d = p - opd.Geometry.Point;
                offsets[i] = (Vector3D.Dot(d, e1), Vector3D.Dot(d, e2));
            }
            string label = poly ? $"{pupils[i].Wavelength * 1e6:g6} µm: " : "";
            warnings.AddRange(PsfEngine.CheckSampling(pupils[i]).Warnings.Select(x => label + x));
            warnings.AddRange(pupils[i].Warnings.Select(x => label + x));
            var (ox, oy) = offsets[i];
            if (Math.Abs(ox) > kernels[i].PeriodX / 4 || Math.Abs(oy) > kernels[i].PeriodY / 4)
                warnings.Add(label + $"The reference point lies {ox * 1e6:g4}, {oy * 1e6:g4} µm from the chief ray, beyond a quarter of the PSF's period ({kernels[i].PeriodX * 1e6:g4} × {kernels[i].PeriodY * 1e6:g4} µm), so light wrapped round from the PSF's periodic copies may fall in the regions (help: with the vertex, \"some care should be taken to set the sampling high enough\"). Raise the sampling.");
        }

        int longest = 0;
        for (int i = 1; i < pupils.Count; i++) if (pupils[i].Wavelength > pupils[longest].Wavelength) longest = i;
        double lambdaF = pupils[longest].Wavelength * Math.Max(pupils[longest].FNumberX, pupils[longest].FNumberY);
        double spread = pupils.Select(p => Math.Max(Finite(p.RaySpreadX), Finite(p.RaySpreadY))).Max();
        double shift = offsets.Select(o => Math.Sqrt(o.Item1 * o.Item1 + o.Item2 * o.Item2)).Max();
        double max = kernels.Min(k => k.MaximumSize(EnclosedEnergyType.Encircled));
        double defaultSize = Math.Min(max, Math.Max(5 * lambdaF, 1.5 * spread + shift));

        notes.Add($"Pupil {sampling} × {sampling} nodes (OP-38's pupil function); the enclosed energy is the exact integral of the sampled pupil's PSF over each region, from the pupil's autocorrelation (docs/Optics-Conventions.md §39), so no image grid enters. The PSF repeats every {kernels[0].PeriodX * 1e6:g5} × {kernels[0].PeriodY * 1e6:g5} µm{(poly ? $" at {pupils[0].Wavelength * 1e6:g6} µm" : "")}; sizes up to half of it are allowed.");
        notes.Add(reference switch
        {
            EnclosedEnergyReference.ChiefRay => $"Regions centred on the {(poly ? "primary wavelength's " : "")}chief ray's image point (the PSF's reference point).",
            EnclosedEnergyReference.Vertex => "Regions centred on the image surface's vertex (local 0, 0), projected along each wavelength's chief ray onto its PSF plane.",
            _ => $"Regions centred on the centroid of the light: the mean image-surface intercept of the pupil rays, each weighted by A² within its wavelength{(poly ? " and each wavelength by its share" : "")}, projected along the chief ray onto the PSF plane.",
        });
        if (poly)
            notes.Add("Polychromatic: each wavelength's fraction weighted by its weight times the fraction of a clear pupil it passes, so each carries its share of the light; every wavelength referenced to the primary wavelength's chief ray (lateral colour moves its PSF).");
        return new DiffractionEnclosedEnergySetup
        {
            Kernels = kernels, Shares = shares, Offsets = offsets, Wavelengths = wavelengths.ToArray(), Field = field,
            Reference = reference, ReferencePoint = point ?? pupils[0].Opd!.Geometry.Point, DefaultSize = defaultSize,
            Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>A setup from pupils built elsewhere (closed forms, tests, the diffraction limit):
    /// each pupil's share its weight times its transmission, the given offsets (zero when null).</summary>
    public static DiffractionEnclosedEnergySetup FromPupils(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> weights,
        IReadOnlyList<(double X, double Y)>? offsets = null, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pupils);
        ArgumentNullException.ThrowIfNull(weights);
        if (pupils.Count == 0 || pupils.Count != weights.Count || offsets is not null && offsets.Count != pupils.Count)
            throw new ArgumentException($"Diffraction enclosed energy: one weight (and offset) per pupil ({pupils.Count} pupils, {weights.Count} weights).");
        var kernels = pupils.Select(p => DiffractionEnclosedEnergyKernel.FromPupil(p, maxDegreeOfParallelism, cancellation)).ToArray();
        return new DiffractionEnclosedEnergySetup
        {
            Kernels = kernels, Shares = Shares(pupils, weights, "Diffraction enclosed energy"),
            Offsets = offsets ?? pupils.Select(_ => (0.0, 0.0)).ToArray(),
        };
    }

    /// <summary>The diffraction-limited setup of <paramref name="setup"/>'s pupils: the same
    /// amplitude with no OPD, about each PSF's own reference point (help, "Diffraction Limited":
    /// "the optical path difference is set to zero regardless of the actual (aberrated) optical
    /// path").</summary>
    public static DiffractionEnclosedEnergySetup Unaberrated(DiffractionEnclosedEnergySetup setup, int? maxDegreeOfParallelism = 1, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var pupils = setup.Kernels.Select(k => k.Pupil.Unaberrated()).ToArray();
        var kernels = pupils.Select(p => DiffractionEnclosedEnergyKernel.FromPupil(p, maxDegreeOfParallelism, cancellation)).ToArray();
        return setup with { Kernels = kernels, Offsets = pupils.Select(_ => (0.0, 0.0)).ToArray(), Warnings = Array.Empty<string>() };
    }

    /// <summary>
    /// The enclosed fraction of one field at one size (meters): the criterion form for other
    /// items. <paramref name="lens"/> and <paramref name="system"/> are one configuration's.
    /// </summary>
    public static double Fraction(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> wavelengths, int sampling,
        EnclosedEnergyType type, EnclosedEnergyReference reference, double size, bool checkApertures = true, bool allowOpdLimitations = false,
        CancellationToken cancellation = default) =>
        Prepare(lens, system, field, wavelengths, sampling, reference, checkApertures, allowOpdLimitations, 1, cancellation).Fraction(type, size);

    private static double[] Shares(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> weights, string who)
    {
        var shares = new double[pupils.Count];
        double total = 0;
        for (int i = 0; i < pupils.Count; i++)
        {
            if (!(weights[i] >= 0) || !double.IsFinite(weights[i]))
                throw new ArgumentException($"{who}: weight {i + 1} is {weights[i]:g6}; it must be finite and not negative.");
            shares[i] = weights[i] * pupils[i].Transmission;
            total += shares[i];
        }
        if (!(total > 0)) throw new ArgumentException($"{who}: every wavelength weight is zero, so there is no light.");
        for (int i = 0; i < shares.Length; i++) shares[i] /= total;
        return shares;
    }

    private static Vector3D Centroid(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> shares)
    {
        var sum = Vector3D.Zero;
        for (int i = 0; i < pupils.Count; i++)
        {
            if (!(shares[i] > 0)) continue;
            var p = pupils[i];
            var (_, index) = PsfEngine.Nodes(p.Sampling);
            var samples = p.Opd!.Samples;
            var part = Vector3D.Zero;
            double power = 0;
            for (int j = 0; j < samples.Count; j++)
            {
                double a = p.Amplitude[index[j].Row, index[j].Column];
                if (a == 0 || !samples[j].IsValid) continue;
                part += samples[j].Ray.Position * (a * a);
                power += a * a;
            }
            sum += part * (shares[i] / power);
        }
        return sum;
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;
}
