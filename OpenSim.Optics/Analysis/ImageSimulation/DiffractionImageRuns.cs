using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>What the Partially Coherent Image Analysis shows (help, "Data Type": "the incoherent
/// image, coherent image, raw image, incoherent transfer function, coherent transfer function, or
/// the transform of the raw image", and "partially coherent image (either mostly coherent or
/// mostly incoherent), or partially coherent PSF or gamma").</summary>
public enum PartiallyCoherentDataType
{
    IncoherentImage = 0,
    CoherentImage = 1,
    RawImage = 2,
    IncoherentTransferFunction = 3,
    CoherentTransferFunction = 4,
    RawImageTransform = 5,
    PartiallyCoherentMostlyCoherent = 6,
    PartiallyCoherentMostlyIncoherent = 7,
    PartiallyCoherentTestPsf = 8,
    PartiallyCoherentTestGamma = 9,
}

/// <summary>What <see cref="PartiallyCoherentImaging.Compute"/> computes.</summary>
public sealed record PartiallyCoherentRequest
{
    /// <summary>The square object (relative irradiance).</summary>
    public required double[,] Object { get; init; }
    /// <summary>The object's full width in image space, meters.</summary>
    public required double FileSize { get; init; }
    public int Oversampling { get; init; } = 1;
    public int ZeroPadding { get; init; } = 1;
    public int OtfSampling { get; init; } = 64;
    public required int Field { get; init; }
    public required int Wavelength { get; init; }
    public bool DiffractionLimited { get; init; }
    public PartiallyCoherentDataType DataType { get; init; } = PartiallyCoherentDataType.IncoherentImage;
    public CoherenceForm Gamma { get; init; } = CoherenceForm.Gaussian;
    /// <summary>γ's scale α, meters.</summary>
    public double Alpha { get; init; } = 1e-5;
    public double Fraction { get; init; } = 0.95;
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>A computed diffraction image (or transfer function), [row, column], row 0 the top.</summary>
public sealed record DiffractionImageResult
{
    public required double[,] Values { get; init; }
    /// <summary>Sample pitch: meters for images, cycles per meter for transfer functions.</summary>
    public required double Pitch { get; init; }
    public required bool Frequency { get; init; }
    /// <summary>The grid's period (the transform size), samples.</summary>
    public required int TransformSize { get; init; }
    public required double CutoffFrequency { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// OpticStudio's Partially Coherent Image Analysis (OP-42; help, "Partially Coherent Image
/// Analysis"). The object, "the ideal image in image space" in "units of relative irradiance", is
/// oversampled (pixels replicated), zero padded (the help's 2n − 1 / 2n rule), and padded with
/// zeros to the next power of two "for transform purposes"; its samples, at d = file size /
/// (pixels × oversampling), are imaged on that periodic grid with the field's coherent transfer
/// function (<see cref="CoherentTransferFunction"/>, OTF Sampling the pupil grid) by
/// <see cref="DiffractionImaging"/>: one OTF "for the field point selected", assumed valid over
/// the whole image. The result shown is the zero-padded region.
/// </summary>
public static class PartiallyCoherentImaging
{
    public const int MaximumTransform = 2048;

    public static DiffractionImageResult Compute(SequentialLens lens, OpticalSystemData system, PartiallyCoherentRequest request,
        CancellationToken cancellation = default)
    {
        const string who = "Partially coherent image analysis";
        var obj = request.Object;
        int n = obj.GetLength(0);
        if (obj.GetLength(1) != n) throw new ArgumentException($"{who}: the file must be square (it is {obj.GetLength(0)} × {obj.GetLength(1)}).");
        if (!(request.FileSize > 0)) throw new ArgumentException($"{who}: the file size must be positive.");
        if (!(request.Fraction > 0 && request.Fraction <= 1)) throw new ArgumentException($"{who}: the fraction must be in (0, 1].");
        var o = SceneTransforms.Oversample(obj, request.Oversampling);
        double d = request.FileSize / (n * request.Oversampling);
        var padded = SceneTransforms.ZeroPad(o, request.ZeroPadding);
        int m = padded.GetLength(0);
        int big = SpatiallyVariantConvolution.NextFast(m);
        if (big > MaximumTransform) throw new ArgumentException($"{who}: the padded image is {m} pixels, a {big}-point transform (at most {MaximumTransform}); lower the oversampling or the zero padding.");
        var grid = SceneTransforms.Pad(padded, big, big);
        int offset = (big - m) / 2;

        var options = PsfRuns.Options(false, checkApertures: true, allow: false);
        var pupil = PsfEngine.BuildPupil(lens, system, request.Field, request.Wavelength, request.OtfSampling, options, cancellation);
        if (request.DiffractionLimited) pupil = pupil.Unaberrated();
        var ctf = new CoherentTransferFunction(pupil);
        var h = DiffractionImaging.Lattice(big, d, ctf.At);
        double fc = Math.Max(ctf.CutoffX, ctf.CutoffY);
        double nyquist = 1 / (2 * d);
        var notes = new List<string>
        {
            $"Grid {big} × {big} at {d * 1e6:g6} µm (the {m}-pixel image {(big > m ? $"padded with zeros to {big} for the transform" : "")}); periodic, so light leaving one side re-enters the other.",
            $"Coherent cutoff n·|a|/λ = {ctf.CutoffX * 1e-3:g6} (x), {ctf.CutoffY * 1e-3:g6} (y) cycles/mm from OP-38's {request.OtfSampling} × {request.OtfSampling} pupil{(request.DiffractionLimited ? ", aberrations removed (Diffraction Limited)" : "")}; the grid's Nyquist frequency is {nyquist * 1e-3:g6} cycles/mm.",
        };
        notes.AddRange(pupil.Notes);
        var warnings = new List<string>(pupil.Warnings);
        bool incoherentBand = request.DataType is not (PartiallyCoherentDataType.CoherentImage or PartiallyCoherentDataType.CoherentTransferFunction or PartiallyCoherentDataType.RawImage or PartiallyCoherentDataType.RawImageTransform);
        if (fc > nyquist)
            warnings.Add($"The coherent cutoff ({fc * 1e-3:g6} cycles/mm) is above the grid's Nyquist frequency ({nyquist * 1e-3:g6}); the pupil is cut by the grid and the image aliased. Raise the oversampling.");
        else if (incoherentBand && 2 * fc > nyquist)
            warnings.Add($"The incoherent pass band (2 × {fc * 1e-3:g6} cycles/mm) reaches beyond the grid's Nyquist frequency ({nyquist * 1e-3:g6}); the image is aliased. Raise the oversampling.");

        int? threads = request.MaxDegreeOfParallelism;
        double[,] full;
        bool frequency = false;
        switch (request.DataType)
        {
            case PartiallyCoherentDataType.RawImage:
                full = grid;
                break;
            case PartiallyCoherentDataType.CoherentImage:
            {
                var a = new double[big, big];
                for (int r = 0; r < big; r++) for (int c = 0; c < big; c++) a[r, c] = Math.Sqrt(grid[r, c]);
                full = DiffractionImaging.Intensity(DiffractionImaging.CoherentAmplitude(a, h, threads));
                notes.Add("Coherent image: amplitude √I₀ filtered by the coherent transfer function; the intensity is shown.");
                break;
            }
            case PartiallyCoherentDataType.IncoherentImage:
                full = DiffractionImaging.Incoherent(grid, h, threads);
                notes.Add("Incoherent image: I₀ filtered by the OTF, the autocorrelation of the sampled coherent transfer function.");
                break;
            case PartiallyCoherentDataType.PartiallyCoherentMostlyCoherent:
            case PartiallyCoherentDataType.PartiallyCoherentMostlyIncoherent:
            {
                var gamma = DiffractionImaging.GammaLattice(big, d, request.Gamma, request.Alpha);
                var method = request.DataType == PartiallyCoherentDataType.PartiallyCoherentMostlyCoherent ? PartialCoherenceMethod.MostlyCoherent : PartialCoherenceMethod.MostlyIncoherent;
                var (img, terms, total) = DiffractionImaging.PartiallyCoherent(grid, h, gamma, method, request.Fraction, threads, cancellation);
                full = img;
                notes.Add(method == PartialCoherenceMethod.MostlyCoherent
                    ? $"Partially coherent, mostly coherent: Abbe's sum over {terms} of the {total} illumination directions of the source whose transform is γ (those holding {request.Fraction:P0} of Σ|S|)."
                    : $"Partially coherent, mostly incoherent: the sum over {terms} of the {total} separations Δ (those holding {request.Fraction:P0} of Σγ²).");
                notes.Add($"γ: {(request.Gamma == CoherenceForm.Gaussian ? "e^(−r·r/α²)" : "SINC(x/α)·SINC(y/α)")}, α = {request.Alpha * 1e6:g6} µm (help, \"Gamma\" and \"Alpha\"; α read in lens units).");
                break;
            }
            case PartiallyCoherentDataType.PartiallyCoherentTestPsf:
                full = DiffractionImaging.Intensity(Fft2D.Shift(Fft2D.Inverse(h, threads)));
                notes.Add("The PSF |h|² of the coherent transfer function on the grid, centred.");
                break;
            case PartiallyCoherentDataType.PartiallyCoherentTestGamma:
            {
                var gamma = DiffractionImaging.GammaLattice(big, d, request.Gamma, request.Alpha);
                full = Fft2D.Shift(gamma);
                for (int r = 0; r < big; r++) for (int c = 0; c < big; c++) full[r, c] = Math.Abs(full[r, c]);
                notes.Add("|γ| on the grid's separations, centred.");
                break;
            }
            default:
            {
                frequency = true;
                Complex[,] spectrum = request.DataType switch
                {
                    PartiallyCoherentDataType.CoherentTransferFunction => h,
                    PartiallyCoherentDataType.IncoherentTransferFunction => DiffractionImaging.Otf(h, threads),
                    _ => Fft2D.Forward(ToComplex(grid), threads),
                };
                var shifted = Fft2D.Shift(spectrum);
                full = new double[big, big];
                // After the shift row 0 is the largest fy (the lattice's rows run along −fy), the top.
                for (int r = 0; r < big; r++) for (int c = 0; c < big; c++) full[r, c] = shifted[r, c].Magnitude;
                notes.Add("The modulus is shown against spatial frequency.");
                break;
            }
        }
        double[,] shown;
        if (frequency) shown = full;
        else
        {
            shown = new double[m, m];
            for (int r = 0; r < m; r++) for (int c = 0; c < m; c++) shown[r, c] = full[offset + r, offset + c];
        }
        return new DiffractionImageResult
        {
            Values = shown, Pitch = frequency ? 1 / (big * d) : d, Frequency = frequency, TransformSize = big,
            CutoffFrequency = fc, Notes = notes, Warnings = warnings,
        };
    }

    internal static Complex[,] ToComplex(double[,] x)
    {
        int rows = x.GetLength(0), cols = x.GetLength(1);
        var z = new Complex[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) z[r, c] = x[r, c];
        return z;
    }
}

/// <summary>What <see cref="ExtendedDiffractionImaging.Compute"/> computes.</summary>
public sealed record ExtendedDiffractionRequest
{
    public required double[,] Object { get; init; }
    /// <summary>The file's full width in image space, meters.</summary>
    public required double FileSize { get; init; }
    public int Oversampling { get; init; } = 1;
    /// <summary>The displayed image's width, meters.</summary>
    public required double DisplaySize { get; init; }
    public int OtfSampling { get; init; } = 64;
    /// <summary>OTF grid points each way across the file.</summary>
    public int OtfGrid { get; init; } = 3;
    public int Resolution { get; init; } = 1;
    public bool Coherent { get; init; }
    public bool DiffractionLimited { get; init; }
    public bool DeltaFunctions { get; init; }
    public required int Field { get; init; }
    public required int Wavelength { get; init; }
    public bool ConsiderDistortion { get; init; }
    public bool UseRelativeIllumination { get; init; }
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>
/// OpticStudio's Extended Diffraction Image Analysis (OP-42; help, "Extended Diffraction Image
/// Analysis"): as the partially coherent analysis for fully coherent or incoherent light, "except
/// the Optical Transfer Function (OTF) may vary over the field of view". The help: "considering
/// the source IMA file one pixel at a time, and computing the Fourier transform of the one pixel.
/// The one-pixel transform is multiplied by the OTF corresponding to that pixel. The sum over all
/// pixels is then computed in spatial frequency space"; the OTF of a pixel is interpolated from a
/// grid. With bilinear interpolation weights t_g per pixel this sum is Σ_g OTF_g·Â_g, Â_g the
/// transform of the object weighted by t_g (the sum is linear in the OTF), which is what is
/// computed: one object transform and one transfer function per grid point.
/// <para>
/// The object's pixels (width w = file size / (pixels × oversampling), centred on the display) are
/// luminous squares, or delta functions of the same weight with "Use Delta Functions"; their
/// transform is exact (<see cref="DiffractionImaging.ObjectSpectrum"/>) whatever w is against the
/// grid. The display holds 2 × OTF sampling × resolution samples across the display size (the
/// help's example: 64 × 64 OTF sampling at 2X resolution shows 256 × 256 pixels); the periodic
/// grid is the next power of two at least as wide as the display plus the file, so the object's
/// blur does not wrap into the display. The OTF grid spans the file's edges in image space; a
/// grid point's field is the chosen field plus the inverse of the paraxial chief-ray map's
/// Jacobian there (linear: the region is small) times its offset. "Consider Distortion" shifts each
/// grid point's transfer function by its real chief ray's departure from that linear placement
/// (a phase e^{−2πif·Δ}); "Use Relative Illumination" weighs it by OP-37's projected solid angle.
/// </para>
/// </summary>
public static class ExtendedDiffractionImaging
{
    public static DiffractionImageResult Compute(SequentialLens lens, OpticalSystemData system, ExtendedDiffractionRequest request,
        CancellationToken cancellation = default)
    {
        const string who = "Extended diffraction image analysis";
        var obj = request.Object;
        int n = obj.GetLength(0);
        if (obj.GetLength(1) != n) throw new ArgumentException($"{who}: the file must be square.");
        if (!(request.FileSize > 0) || !(request.DisplaySize > 0)) throw new ArgumentException($"{who}: the file and display sizes must be positive.");
        if (request.OtfGrid < 1 || request.OtfGrid > 33) throw new ArgumentException($"{who}: the OTF grid must be 1 to 33 points each way (it is {request.OtfGrid}).");
        if (request.Resolution < 1 || request.Resolution > 16) throw new ArgumentException($"{who}: the resolution must be 1 to 16 (it is {request.Resolution}).");
        if (system.Fields.Points.Any(p => p.IsVignetted))
            throw new InvalidOperationException($"{who}: the fields have vignetting factors, which cannot be interpolated across the file; turn on \"Remove vignetting factors\".");
        var o = SceneTransforms.Oversample(obj, request.Oversampling);
        int no = o.GetLength(0);
        double w = request.FileSize / no;
        int display = 2 * request.OtfSampling * request.Resolution;
        double d = request.DisplaySize / display;
        int big = SpatiallyVariantConvolution.NextFast((int)Math.Ceiling((request.DisplaySize + request.FileSize) / d));
        if (big > PartiallyCoherentImaging.MaximumTransform)
            throw new ArgumentException($"{who}: the display and file need a {big}-point transform (at most {PartiallyCoherentImaging.MaximumTransform}); lower the resolution or the OTF sampling, or shrink the file against the display.");
        int g = request.OtfGrid;

        // Grid points: image offsets (u, v) from the centre, and their fields.
        var centre = system.Fields.Points[request.Field];
        var launcher0 = new RayLauncher(lens, system);
        int imageIndex = lens.ImageIndex;
        (double X, double Y) Paraxial(double fx, double fy)
        {
            var (cx, cy) = launcher0.Paraxial.TraceChiefRay(new FieldPoint { X = fx, Y = fy });
            return (cx.Heights[imageIndex], cy.Heights[imageIndex]);
        }
        double step = 1e-4 * Math.Max(Math.Max(Math.Abs(system.Fields.Scale.X), Math.Abs(system.Fields.Scale.Y)), FieldTypes.IsAngle(system.Fields.Type) ? 1 : 1e-3);
        var px1 = Paraxial(centre.X + step, centre.Y); var px0 = Paraxial(centre.X - step, centre.Y);
        var py1 = Paraxial(centre.X, centre.Y + step); var py0 = Paraxial(centre.X, centre.Y - step);
        double a11 = (px1.X - px0.X) / (2 * step), a21 = (px1.Y - px0.Y) / (2 * step), a12 = (py1.X - py0.X) / (2 * step), a22 = (py1.Y - py0.Y) / (2 * step);
        double det = a11 * a22 - a12 * a21;
        if (!(Math.Abs(det) > 0)) throw new InvalidOperationException($"{who}: the paraxial image does not move with the field at field {request.Field + 1}, so the file cannot be placed in the field.");
        (double X, double Y) FieldOf(double u, double v) => (centre.X + (a22 * u - a12 * v) / det, centre.Y + (-a21 * u + a11 * v) / det);
        var nodes = new List<(double U, double V)>();
        for (int b = 0; b < g; b++)
            for (int a = 0; a < g; a++)
                nodes.Add((g == 1 ? 0 : -request.FileSize / 2 + a * request.FileSize / (g - 1), g == 1 ? 0 : request.FileSize / 2 - b * request.FileSize / (g - 1)));
        var cornerFields = new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }.Select(s => FieldOf(s.Item1 * request.FileSize / 2, s.Item2 * request.FileSize / 2))
            .Select(f => new FieldPoint { X = f.X, Y = f.Y, Weight = 0 });
        var nodeFields = nodes.Select(p => FieldOf(p.U, p.V)).Select(f => new FieldPoint { X = f.X, Y = f.Y, Weight = 0 }).ToArray();
        int first = system.Fields.Points.Count + 4;
        var extended = system with { Fields = system.Fields with { Points = system.Fields.Points.Concat(cornerFields).Concat(nodeFields).ToArray() } };
        var launcher = new RayLauncher(lens, extended);
        var scale = extended.Fields.Scale;
        var trace = SequentialTrace.Create(lens, extended, request.Wavelength);
        (double X, double Y) Chief(double fx, double fy)
        {
            var (hx, hy) = OpticsConventions.NormalizeField((fx, fy), scale);
            var ray = RaySampler.AtSurface(trace, launcher, hx, hy, 0, 0, imageIndex, new TraceOptions { IgnoreApertures = true });
            if (!ray.Passed) throw new InvalidOperationException($"{who}: the chief ray of field ({fx:g6}, {fy:g6}) does not reach the image; shrink the file size.");
            return (ray.Position.X, ray.Position.Y);
        }
        var reference = Chief(centre.X, centre.Y);

        double[]? illumination = null;
        var notes = new List<string>();
        if (request.UseRelativeIllumination)
        {
            var setup = new FieldScanSetup { Lens = lens, System = extended, Launcher = launcher, Notes = new List<string>() };
            var points = nodeFields.Select(p => OpticsConventions.NormalizeField((p.X, p.Y), scale)).ToArray();
            var area = RelativeIllumination.Compute(setup, request.Wavelength, FieldScan.PlusY, 20, Array.Empty<double>(), null, cancellation, points).ProjectedSolidAngle;
            double max = area.Max();
            illumination = area.Select(x => x / max).ToArray();
            notes.Add("Relative illumination: OP-37's projected solid angle at each OTF grid point (ray density 20), over the largest.");
        }

        var options = PsfRuns.Options(false, checkApertures: true, allow: false);
        var value = new double[no, no];
        for (int r = 0; r < no; r++) for (int c = 0; c < no; c++) value[r, c] = request.Coherent ? Math.Sqrt(o[r, c]) : o[r, c];
        // The object's top-left pixel centre relative to grid sample [0, 0] (at (−big/2·d, +big/2·d) from the centre).
        double x0 = -(no - 1) / 2.0 * w + big / 2 * d, y0 = (no - 1) / 2.0 * w - big / 2 * d;
        var sum = new Complex[big, big];
        var warnings = new List<string>();
        double fc = 0;
        for (int k = 0; k < nodes.Count; k++)
        {
            cancellation.ThrowIfCancellationRequested();
            var pupil = PsfEngine.BuildPupil(lens, extended, first + k, request.Wavelength, request.OtfSampling, options, cancellation);
            if (request.DiffractionLimited) pupil = pupil.Unaberrated();
            warnings.AddRange(pupil.Warnings.Select(x => $"OTF grid point {k % g + 1}, {k / g + 1}: {x}"));
            var ctf = new CoherentTransferFunction(pupil);
            fc = Math.Max(fc, Math.Max(ctf.CutoffX, ctf.CutoffY));
            var h = DiffractionImaging.Lattice(big, d, ctf.At);
            var transfer = request.Coherent ? h : DiffractionImaging.Otf(h, request.MaxDegreeOfParallelism);
            double gain = illumination is null ? 1 : request.Coherent ? Math.Sqrt(illumination[k]) : illumination[k];
            double sx = 0, sy = 0;
            if (request.ConsiderDistortion)
            {
                var real = Chief(nodeFields[k].X, nodeFields[k].Y);
                sx = real.X - reference.X - nodes[k].U;
                sy = real.Y - reference.Y - nodes[k].V;
            }
            // The object weighted by this grid point's tent.
            var weighted = new double[no, no];
            bool any = false;
            int ka = k % g, kb = k / g;
            for (int r = 0; r < no; r++)
            {
                double tb = SpatiallyVariantConvolution.Tent(kb, g, no, r);
                for (int c = 0; c < no; c++)
                {
                    double t = tb * SpatiallyVariantConvolution.Tent(ka, g, no, c) * value[r, c];
                    weighted[r, c] = t;
                    any |= t != 0;
                }
            }
            if (!any) continue;
            var spectrum = DiffractionImaging.ObjectSpectrum(weighted, w, x0, y0, big, d, request.DeltaFunctions);
            for (int r = 0; r < big; r++)
            {
                double fy = -DiffractionImaging.Signed(r, big) / (big * d);
                for (int c = 0; c < big; c++)
                {
                    double fx = DiffractionImaging.Signed(c, big) / (big * d);
                    var shift = sx == 0 && sy == 0 ? Complex.One : PsfEngine.Turn(-(fx * sx + fy * sy));
                    sum[r, c] += gain * transfer[r, c] * shift * spectrum[r, c];
                }
            }
        }
        var back = Fft2D.Inverse(sum, request.MaxDegreeOfParallelism);
        var shown = new double[display, display];
        int offset = big / 2 - display / 2;
        for (int r = 0; r < display; r++)
            for (int c = 0; c < display; c++)
            {
                var z = back[offset + r, offset + c];
                shown[r, c] = request.Coherent ? z.Real * z.Real + z.Imaginary * z.Imaginary : z.Real;
            }
        double nyquist = 1 / (2 * d);
        if (fc > nyquist)
            warnings.Add($"The coherent cutoff ({fc * 1e-3:g6} cycles/mm) is above the display grid's Nyquist frequency ({nyquist * 1e-3:g6}); the image is aliased. Raise the resolution or shrink the display.");
        else if (!request.Coherent && 2 * fc > nyquist)
            warnings.Add($"The incoherent pass band (2 × {fc * 1e-3:g6} cycles/mm) reaches beyond the display grid's Nyquist frequency ({nyquist * 1e-3:g6}); the image is aliased. Raise the resolution or shrink the display.");
        notes.Insert(0, $"Display {display} × {display} samples at {d * 1e6:g6} µm (2 × OTF sampling × resolution) on a periodic {big}-point grid; the file's {no} × {no} pixels {w * 1e6:g6} µm wide, {(request.DeltaFunctions ? "delta functions" : "luminous squares")}.");
        notes.Add($"OTF grid {g} × {g} spanning the file in image space, interpolated bilinearly per pixel; each point's transfer function from OP-38's {request.OtfSampling} × {request.OtfSampling} pupil{(request.DiffractionLimited ? " with the aberrations removed" : "")}{(request.ConsiderDistortion ? ", shifted by its chief ray's distortion" : "")}.");
        return new DiffractionImageResult
        {
            Values = shown, Pitch = d, Frequency = false, TransformSize = big, CutoffFrequency = fc, Notes = notes, Warnings = warnings.Distinct().ToArray(),
        };
    }
}
