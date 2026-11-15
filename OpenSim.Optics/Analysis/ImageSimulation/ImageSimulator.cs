using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>How the Image Simulation models the aberrations (help, "Image Simulation",
/// "Aberrations": "None to ignore aberrations, Geometric to consider only ray aberrations, or
/// Diffraction to use the Huygens PSF").</summary>
public enum ImageSimulationAberrations
{
    None = 0,
    Geometric = 1,
    Diffraction = 2,
}

/// <summary>The diffraction PSF engine (OP-38): the help's Huygens PSF, or the FFT PSF (faster,
/// on the plane normal to the chief ray) offered beyond the help.</summary>
public enum DiffractionPsfEngine
{
    Huygens = 0,
    Fft = 1,
}

/// <summary>The point the detector is centred on (help, "Image Simulation", "Reference": "chief
/// ray, surface vertex, or primary wavelength chief ray").</summary>
public enum ImageSimulationReference
{
    ChiefRay = 0,
    Vertex = 1,
    PrimaryChiefRay = 2,
}

/// <summary>One colour of the simulation: which scene map is imaged at which wavelength row,
/// with a gain (the 1+2+3 wavelength weights for a grey IMA).</summary>
public sealed record SimulationChannel(int SceneChannel, int Wavelength, double Gain, string Name);

/// <summary>What <see cref="ImageSimulator.Simulate"/> computes.</summary>
public sealed record ImageSimulationRequest
{
    /// <summary>The picture after oversampling, guard band, flip and rotation.</summary>
    public required SceneImage Scene { get; init; }

    /// <summary>The 0-based field row the picture is centred on.</summary>
    public required int Field { get; init; }

    /// <summary>The picture's full height in field units (degrees, or meters for heights).</summary>
    public required double FieldHeight { get; init; }

    public required IReadOnlyList<SimulationChannel> Channels { get; init; }

    public int PupilSampling { get; init; } = 32;

    /// <summary>The fewest PSF samples across the PSF window (help: "Image Sampling").</summary>
    public int ImageSampling { get; init; } = 32;

    public int PsfPointsX { get; init; } = 3;
    public int PsfPointsY { get; init; } = 3;
    public ImageSimulationAberrations Aberrations { get; init; } = ImageSimulationAberrations.Geometric;
    public DiffractionPsfEngine Engine { get; init; } = DiffractionPsfEngine.Huygens;
    public bool UseRelativeIllumination { get; init; }
    public int RelativeIlluminationDensity { get; init; } = 20;
    public ImageSimulationReference Reference { get; init; } = ImageSimulationReference.ChiefRay;

    /// <summary>Detector columns; 0 for the picture's.</summary>
    public int DetectorX { get; init; }

    /// <summary>Detector rows; 0 for the picture's.</summary>
    public int DetectorY { get; init; }

    /// <summary>Detector pixel size, meters; 0 for the help's default.</summary>
    public double PixelSize { get; init; }

    public SceneFlip FlipImage { get; init; } = SceneFlip.None;
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>The simulated image.</summary>
public sealed record ImageSimulationResult
{
    /// <summary>One detector image per channel, [row, column], row 0 the top (+y).</summary>
    public required IReadOnlyList<double[,]> Images { get; init; }
    public required IReadOnlyList<SimulationChannel> Channels { get; init; }
    public required double PixelSize { get; init; }
    /// <summary>The help's default pixel size (whether or not it was used), meters.</summary>
    public required double DefaultPixelSize { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    /// <summary>The detector centre, meters in the image surface frame.</summary>
    public required double CentreX { get; init; }
    public required double CentreY { get; init; }
    /// <summary>The kernel of each PSF grid node [b·gx + a] per channel.</summary>
    public required IReadOnlyList<IReadOnlyList<PixelKernel>> Kernels { get; init; }
    /// <summary>The relative illumination at each node per channel (1 when not used).</summary>
    public required IReadOnlyList<double[]> Illumination { get; init; }
    /// <summary>The factor the image was multiplied by so its peak equals the picture's.</summary>
    public required double Normalisation { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// OpticStudio's Image Simulation (OP-42; help, "Image Simulation"; docs/Optics-Conventions.md
/// §67). The help's four steps, as built:
/// <list type="number">
/// <item>The picture arrives already oversampled, guard-banded, flipped and rotated
/// (<see cref="SceneTransforms"/>). Pixel (r, c) of a W × H picture of field height F is the
/// field point f₀ + ((c − (W − 1)/2)·δ, ((H − 1)/2 − r)·δ), δ = F/H, f₀ the chosen field.</item>
/// <item>A grid of PSFs (help: "The PSF grid ... is like the grid distribution type in Fields
/// Wizard"; corners at f₀ ± (W·δ/2, H·δ/2), spacing W·δ/(n − 1)), one per node and colour,
/// "centered on the chief ray for the field at that grid point": none (a delta), geometric (the
/// rays of an n × n pupil grid binned) or diffraction (OP-38's Huygens or FFT PSF sampled finely
/// and integrated over each source pixel's image). The PSF is turned into a kernel on source
/// pixel offsets through the local Jacobian J = ∂(image point)/∂(pixel) of the chief-ray map at
/// the node, so it is expressed in the picture's own pixels; each kernel is normalised to 1.</item>
/// <item>The picture, weighted by the relative illumination interpolated from the nodes, is
/// convolved with the PSF interpolated bilinearly between the nodes
/// (<see cref="SpatiallyVariantConvolution"/>).</item>
/// <item>Each detector pixel takes the blurred picture at the picture point whose real chief ray
/// lands on it (<see cref="ChiefRayMap.Inverse"/>; distortion and, colour by colour, lateral
/// colour), averaged over s × s points of the detector pixel when it is larger than a source
/// pixel's image (s = ⌈p/√|det J|⌉, at most 8), bilinear between pixel centres and zero outside
/// the picture. The image is then scaled so its peak equals the picture's (help: "normalizing
/// the image to have the same peak brightness as the input image").</item>
/// </list>
/// The diffraction PSF switches to the geometric one for a node and colour whose RMS spot radius
/// exceeds 20 Airy radii 1.22·λ·F (the help's "threshold is 20 times the diffraction limit"; the
/// measure is a reading, §10), and says so.
/// </summary>
public static class ImageSimulator
{
    /// <summary>The help's diffraction-to-geometric switch: 20 times the diffraction limit.</summary>
    public const double SwitchFactor = 20;

    /// <summary>The most chief-ray map nodes across (the map is bilinear between them).</summary>
    public const int MaximumMapNodes = 65;

    /// <summary>The most PSF samples across a kernel window.</summary>
    public const int MaximumPsfSamples = 2049;

    public static ImageSimulationResult Simulate(SequentialLens lens, OpticalSystemData system, ImageSimulationRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        const string who = "Image simulation";
        var scene = request.Scene;
        int W = scene.Width, H = scene.Height;
        if (request.Field < 0 || request.Field >= system.Fields.Points.Count)
            throw new ArgumentOutOfRangeException(nameof(request), request.Field, $"{who}: field {request.Field + 1} is not one of the {system.Fields.Points.Count} fields.");
        if (!(request.FieldHeight > 0) || !double.IsFinite(request.FieldHeight))
            throw new ArgumentException($"{who}: the field height must be positive (it is {request.FieldHeight:g6}).");
        if (request.Channels.Count == 0) throw new ArgumentException($"{who}: at least one colour.");
        if (request.PsfPointsX < 1 || request.PsfPointsY < 1 || request.PsfPointsX > 33 || request.PsfPointsY > 33)
            throw new ArgumentException($"{who}: the PSF grid must be 1 to 33 points each way (it is {request.PsfPointsX} × {request.PsfPointsY}).");
        if (request.PupilSampling < 4 || request.PupilSampling > 512)
            throw new ArgumentException($"{who}: the pupil sampling must be 4 to 512 (it is {request.PupilSampling}).");
        if (request.ImageSampling < 4 || request.ImageSampling > 1024)
            throw new ArgumentException($"{who}: the image sampling must be 4 to 1024 (it is {request.ImageSampling}).");
        if (request.DetectorX < 0 || request.DetectorY < 0 || request.DetectorX > 16000 || request.DetectorY > 16000)
            throw new ArgumentException($"{who}: the detector pixels must be 0 (the picture's) to 16000.");
        if (!(request.PixelSize >= 0) || !double.IsFinite(request.PixelSize))
            throw new ArgumentException($"{who}: the pixel size must be 0 (default) or positive.");
        if (system.Fields.Points.Any(p => p.IsVignetted))
            throw new InvalidOperationException($"{who}: the fields have vignetting factors, which cannot be interpolated across the picture; turn on \"Remove vignetting factors\" (they are replaced by surface apertures).");
        foreach (var ch in request.Channels)
        {
            if (ch.SceneChannel < 0 || ch.SceneChannel >= scene.Channels.Count)
                throw new ArgumentException($"{who}: colour {ch.Name} reads picture map {ch.SceneChannel + 1}; the picture has {scene.Channels.Count}.");
            if (ch.Wavelength < 0 || ch.Wavelength >= system.Wavelengths.Wavelengths.Count)
                throw new ArgumentException($"{who}: colour {ch.Name} uses wavelength row {ch.Wavelength + 1}; the system has {system.Wavelengths.Wavelengths.Count}.");
        }

        var notes = new List<string>();
        var warnings = new List<string>();
        var centre = system.Fields.Points[request.Field];
        double delta = request.FieldHeight / H;
        double halfW = W * delta / 2, halfH = H * delta / 2;
        (double X, double Y) FieldAt(double cx, double ry) =>
            (centre.X + (cx - (W - 1) / 2.0) * delta, centre.Y + ((H - 1) / 2.0 - ry) * delta);

        // The system with the picture's corners and the PSF grid nodes appended as fields: the
        // corners fix the normalisation (no node lies beyond them), the nodes give the PSF
        // engines a field row each.
        int gx = request.PsfPointsX, gy = request.PsfPointsY;
        var corners = new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }
            .Select(s => new FieldPoint { X = centre.X + s.Item1 * halfW, Y = centre.Y + s.Item2 * halfH, Weight = 0 }).ToList();
        if (FieldTypes.IsAngle(system.Fields.Type) && corners.Any(c => !(Math.Abs(c.X) < 90) || !(Math.Abs(c.Y) < 90)))
            throw new ArgumentException($"{who}: the picture reaches beyond 90° from the axis; shrink the field height.");
        var nodeFields = new List<FieldPoint>();
        for (int b = 0; b < gy; b++)
            for (int a = 0; a < gx; a++)
            {
                var (fx, fy) = FieldAt(SpatiallyVariantConvolution.NodeCoordinate(a, gx, W), SpatiallyVariantConvolution.NodeCoordinate(b, gy, H));
                nodeFields.Add(new FieldPoint { X = fx, Y = fy, Weight = 0 });
            }
        int firstNode = system.Fields.Points.Count + corners.Count;
        var extended = system with { Fields = system.Fields with { Points = system.Fields.Points.Concat(corners).Concat(nodeFields).ToArray() } };
        var launcher = new RayLauncher(lens, extended);
        var scale = extended.Fields.Scale;
        int image = lens.ImageIndex;
        var traces = new Dictionary<int, SequentialTrace>();
        SequentialTrace Trace(int w) => traces.TryGetValue(w, out var t) ? t : traces[w] = SequentialTrace.Create(lens, extended, w);
        var unapertured = new TraceOptions { IgnoreApertures = true };
        string WaveName(int w) => $"{extended.Wavelengths.Wavelengths[w].Micrometers:g6} µm";

        (double X, double Y) Chief(double fx, double fy, int w)
        {
            var (hx, hy) = OpticsConventions.NormalizeField((fx, fy), scale);
            var ray = RaySampler.AtSurface(Trace(w), launcher, hx, hy, 0, 0, image, unapertured);
            if (!ray.Passed)
                throw new InvalidOperationException($"{who}: the chief ray of field ({fx:g6}, {fy:g6}) at {WaveName(w)} does not reach the image ({RayStatuses.Label(ray.Final)} at surface {ray.FinalSurface}); shrink the field height or move the picture.");
            return (ray.Position.X, ray.Position.Y);
        }

        // Chief-ray maps, one per colour.
        int mx = Math.Min(W, MaximumMapNodes - 1) + 1, my = Math.Min(H, MaximumMapNodes - 1) + 1;
        var maps = new Dictionary<int, ChiefRayMap>();
        foreach (int w in request.Channels.Select(c => c.Wavelength).Distinct())
        {
            var xs = new double[my, mx];
            var ys = new double[my, mx];
            for (int b = 0; b < my; b++)
                for (int a = 0; a < mx; a++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (fx, fy) = FieldAt(ChiefRayMap.Node(a, mx, W), ChiefRayMap.Node(b, my, H));
                    (xs[b, a], ys[b, a]) = Chief(fx, fy, w);
                }
            maps[w] = new ChiefRayMap(xs, ys, W, H);
        }
        notes.Add($"Distortion and lateral colour: real chief rays traced at {mx} × {my} points spanning the picture's edges, per wavelength, bilinear between them; each detector pixel looks up the picture point whose chief ray lands on it.");

        // The reference point and the default pixel size.
        int referenceWave = request.Reference == ImageSimulationReference.PrimaryChiefRay ? extended.Wavelengths.PrimaryIndex
            : request.Channels.Count == 1 ? request.Channels[0].Wavelength
            : request.Channels.Any(c => c.Wavelength == extended.Wavelengths.PrimaryIndex) ? extended.Wavelengths.PrimaryIndex
            : request.Channels[request.Channels.Count / 2].Wavelength;
        var (refX, refY) = request.Reference == ImageSimulationReference.Vertex ? (0.0, 0.0) : Chief(centre.X, centre.Y, referenceWave);
        var upper = Chief(centre.X + 0.5 * delta, centre.Y + 0.5 * delta, referenceWave);
        var lower = Chief(centre.X - 0.5 * delta, centre.Y - 0.5 * delta, referenceWave);
        double defaultPixel = Math.Sqrt(2) / 2 * Math.Sqrt((upper.X - lower.X) * (upper.X - lower.X) + (upper.Y - lower.Y) * (upper.Y - lower.Y));
        if (!(defaultPixel > 0))
            throw new InvalidOperationException($"{who}: the chief rays of the centre pixel's corners land on one point, so there is no default pixel size; give the pixel size.");
        double pixel = request.PixelSize > 0 ? request.PixelSize : defaultPixel;
        int nx = request.DetectorX > 0 ? request.DetectorX : W, ny = request.DetectorY > 0 ? request.DetectorY : H;

        // Per node and colour: Jacobian, illumination, kernel.
        var nodeCoords = new List<(double Cx, double Ry)>();
        for (int b = 0; b < gy; b++)
            for (int a = 0; a < gx; a++)
                nodeCoords.Add((SpatiallyVariantConvolution.NodeCoordinate(a, gx, W), SpatiallyVariantConvolution.NodeCoordinate(b, gy, H)));
        var illumination = new List<double[]>();
        if (request.UseRelativeIllumination)
        {
            var setup = new FieldScanSetup { Lens = lens, System = extended, Launcher = launcher, Notes = new List<string>() };
            var points = nodeFields.Select(p => OpticsConventions.NormalizeField((p.X, p.Y), scale)).ToArray();
            var raw = new List<double[]>();
            foreach (var ch in request.Channels)
            {
                cancellation.ThrowIfCancellationRequested();
                raw.Add(RelativeIllumination.Compute(setup, ch.Wavelength, FieldScan.PlusY, request.RelativeIlluminationDensity, Array.Empty<double>(),
                    null, cancellation, points).ProjectedSolidAngle.ToArray());
            }
            double max = raw.SelectMany(r => r).Max();
            if (!(max > 0)) throw new InvalidOperationException($"{who}: no ray reaches the image at any PSF grid point, so the relative illumination is zero everywhere.");
            illumination.AddRange(raw.Select(r => r.Select(v => v / max).ToArray()));
            notes.Add($"Relative illumination: OP-37's projected solid angle of the exit pupil (ray density {request.RelativeIlluminationDensity}) at each PSF grid point and colour, over the largest; bilinear between the points.");
        }
        else
        {
            foreach (var _ in request.Channels) illumination.Add(Enumerable.Repeat(1.0, gx * gy).ToArray());
            notes.Add("Relative illumination not used: every pixel whose chief ray reaches the image weighs 1 (help: \"the calculation uses the chief ray to establish the overall intensity\").");
        }

        var kernels = new List<IReadOnlyList<PixelKernel>>();
        var switched = new List<string>();
        var vignettedNodes = new List<string>();
        double minCaptured = 1;
        for (int ci = 0; ci < request.Channels.Count; ci++)
        {
            var ch = request.Channels[ci];
            var map = maps[ch.Wavelength];
            var list = new PixelKernel[gx * gy];
            for (int g = 0; g < gx * gy; g++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (cx, ry) = nodeCoords[g];
                // The map's derivative straddling the node (central difference over one pixel).
                var (p1x, p1y) = map.Evaluate(cx + 0.5, ry);
                var (p0x, p0y) = map.Evaluate(cx - 0.5, ry);
                var (q1x, q1y) = map.Evaluate(cx, ry + 0.5);
                var (q0x, q0y) = map.Evaluate(cx, ry - 0.5);
                var jac = new Jacobian(p1x - p0x, p1y - p0y, q1x - q0x, q1y - q0y);
                string where = $"PSF grid point ({g % gx + 1}, {g / gx + 1}), field ({nodeFields[g].X:g6}, {nodeFields[g].Y:g6}), {WaveName(ch.Wavelength)}";
                list[g] = request.Aberrations switch
                {
                    ImageSimulationAberrations.None => PixelKernel.Delta,
                    ImageSimulationAberrations.Geometric => GeometricKernel(lens, extended, launcher, Trace(ch.Wavelength), firstNode + g, ch.Wavelength, request.PupilSampling, jac, where, vignettedNodes),
                    _ => DiffractionKernel(lens, extended, launcher, Trace(ch.Wavelength), firstNode + g, ch.Wavelength, request, jac, where, warnings, switched, vignettedNodes, ref minCaptured, cancellation),
                };
            }
            kernels.Add(list);
        }
        notes.Add(request.Aberrations switch
        {
            ImageSimulationAberrations.None => "Aberrations: none; the PSF grid is an array of delta functions (help), so only distortion, lateral colour and illumination act.",
            ImageSimulationAberrations.Geometric => $"Aberrations: geometric; at each PSF grid point a {request.PupilSampling} × {request.PupilSampling} grid of pupil rays (weighted by the apodization squared) is binned into source pixels about the chief ray.",
            _ => $"Aberrations: diffraction, OP-38's {(request.Engine == DiffractionPsfEngine.Huygens ? "Huygens" : "FFT")} PSF of a {request.PupilSampling} × {request.PupilSampling} pupil, sampled at no more than λF/2 and half a source pixel's image (at least {request.ImageSampling} samples across its window) and summed over each source pixel's image.",
        });
        notes.Add($"PSF grid {gx} × {gy} spanning the picture's edges (help, PSF-grid figures); the PSF between the points is interpolated bilinearly, applied as one convolution per point over that point's neighbourhood (overlap-add).");
        if (switched.Count > 0)
            notes.Add($"Switched from the diffraction to the geometric PSF (RMS spot radius beyond {SwitchFactor:g3} Airy radii): " + string.Join("; ", switched) + ".");
        if (vignettedNodes.Count > 0)
            warnings.Add("No ray reaches the image at: " + string.Join("; ", vignettedNodes) + "; the picture there carries no light.");
        if (minCaptured < 0.95)
            warnings.Add($"The PSF window holds as little as {minCaptured:P1} of a PSF's energy at some grid point (the rest lies beyond half the PSF period of the pupil sampling); raise the pupil sampling.");

        // Blur each colour in the picture's pixels.
        var blurred = new double[request.Channels.Count][,];
        for (int ci = 0; ci < request.Channels.Count; ci++)
        {
            var ch = request.Channels[ci];
            var src = scene.Channels[ch.SceneChannel];
            var weighted = new double[H, W];
            var ri = illumination[ci];
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    double v = src[r, c] * ch.Gain;
                    if (v == 0) continue;
                    double t = 0;
                    for (int b = 0; b < gy; b++)
                    {
                        double tb = SpatiallyVariantConvolution.Tent(b, gy, H, r);
                        if (tb == 0) continue;
                        for (int a = 0; a < gx; a++) t += tb * SpatiallyVariantConvolution.Tent(a, gx, W, c) * ri[b * gx + a];
                    }
                    weighted[r, c] = v * t;
                }
            var ks = kernels[ci];
            blurred[ci] = SpatiallyVariantConvolution.Apply(weighted, gx, gy, (a, b) => ks[b * gx + a], request.MaxDegreeOfParallelism, cancellation);
        }

        // Detector sampling through the chief-ray maps.
        var images = new double[request.Channels.Count][,];
        for (int ci = 0; ci < request.Channels.Count; ci++)
        {
            var ch = request.Channels[ci];
            var map = maps[ch.Wavelength];
            var src = blurred[ci];
            var d = map.Derivative((W - 1) / 2.0, (H - 1) / 2.0);
            double cellImage = Math.Sqrt(Math.Abs(d.Xc * d.Yr - d.Xr * d.Yc));
            int s = (int)Math.Clamp(Math.Ceiling(pixel / cellImage - 1e-9), 1, 8);
            var output = new double[ny, nx];
            var rows = ParallelSlots.Compute(ny, request.MaxDegreeOfParallelism, i =>
            {
                cancellation.ThrowIfCancellationRequested();
                var line = new double[nx];
                double startC = (W - 1) / 2.0, startR = (H - 1) / 2.0;
                for (int j = 0; j < nx; j++)
                {
                    double sum = 0;
                    for (int u = 0; u < s; u++)
                        for (int v = 0; v < s; v++)
                        {
                            double x = refX + (j - (nx - 1) / 2.0 + ((v + 0.5) / s - 0.5)) * pixel;
                            double y = refY + ((ny - 1) / 2.0 - i - ((u + 0.5) / s - 0.5)) * pixel;
                            var (cx, ry) = map.Inverse(x, y, startC, startR, who);
                            startC = cx; startR = ry;
                            sum += Sample(src, cx, ry);
                        }
                    line[j] = sum / (s * s);
                }
                return line;
            }, cancellation);
            for (int i = 0; i < ny; i++)
                for (int j = 0; j < nx; j++) output[i, j] = rows[i][j];
            images[ci] = SceneTransforms.Flip(output, request.FlipImage);
            if (s > 1) notes.Add($"{ch.Name}: each detector pixel averages {s} × {s} points (it is larger than a source pixel's image, {cellImage * 1e6:g4} µm).");
        }

        // The peak of the image equals the picture's.
        double inPeak = 0, outPeak = 0;
        foreach (var ch in request.Channels)
            foreach (var v in scene.Channels[ch.SceneChannel]) inPeak = Math.Max(inPeak, v * ch.Gain);
        foreach (var img in images) foreach (var v in img) outPeak = Math.Max(outPeak, v);
        if (!(outPeak > 0))
            throw new InvalidOperationException($"{who}: no light reaches the detector (the picture is dark, or every pixel's image falls outside the {nx} × {ny} detector of {pixel * 1e3:g6} mm pixels).");
        double factor = inPeak / outPeak;
        foreach (var img in images)
            for (int i = 0; i < ny; i++)
                for (int j = 0; j < nx; j++) img[i, j] *= factor;

        return new ImageSimulationResult
        {
            Images = images, Channels = request.Channels, PixelSize = pixel, DefaultPixelSize = defaultPixel, Columns = nx, Rows = ny,
            CentreX = refX, CentreY = refY, Kernels = kernels, Illumination = illumination, Normalisation = factor,
            Notes = notes, Warnings = warnings.Distinct().ToArray(),
        };
    }

    /// <summary>The picture at fractional pixel coordinates: bilinear between pixel centres, the
    /// edge pixel's value out to its edge, zero beyond.</summary>
    public static double Sample(double[,] map, double cx, double ry)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        if (cx < -0.5 || ry < -0.5 || cx > w - 0.5 || ry > h - 0.5) return 0;
        double x = Math.Clamp(cx, 0, w - 1), y = Math.Clamp(ry, 0, h - 1);
        int c0 = Math.Min((int)Math.Floor(x), Math.Max(0, w - 2)), r0 = Math.Min((int)Math.Floor(y), Math.Max(0, h - 2));
        double u = w == 1 ? 0 : x - c0, v = h == 1 ? 0 : y - r0;
        int c1 = Math.Min(c0 + 1, w - 1), r1 = Math.Min(r0 + 1, h - 1);
        return (1 - v) * ((1 - u) * map[r0, c0] + u * map[r0, c1]) + v * ((1 - u) * map[r1, c0] + u * map[r1, c1]);
    }

    /// <summary>The image displacement per source column (Xc, Yc) and per source row (Xr, Yr).</summary>
    internal readonly record struct Jacobian(double Xc, double Yc, double Xr, double Yr)
    {
        public double Det => Xc * Yr - Xr * Yc;

        /// <summary>The source pixel offset (columns, rows) of an image offset.</summary>
        public (double Dc, double Dr) Back(double ux, double uy) => ((Yr * ux - Xr * uy) / Det, (-Yc * ux + Xc * uy) / Det);

        /// <summary>The smaller singular value: the short side of a pixel's image.</summary>
        public double MinimumStretch
        {
            get
            {
                double a = Xc * Xc + Yc * Yc, d = Xr * Xr + Yr * Yr, b = Xc * Xr + Yc * Yr;
                double mean = (a + d) / 2, diff = Math.Sqrt((a - d) * (a - d) / 4 + b * b);
                return Math.Sqrt(Math.Max(0, mean - diff));
            }
        }
    }

    /// <summary>Weighted image offsets binned into whole source-pixel offsets (the nearest),
    /// normalised to 1; null when nothing has weight.</summary>
    internal static PixelKernel? Bin(IEnumerable<(double Ux, double Uy, double Weight)> samples, Jacobian jac)
    {
        var cells = new Dictionary<(int, int), double>();
        double total = 0;
        foreach (var (ux, uy, wgt) in samples)
        {
            if (!(wgt > 0)) continue;
            var (dc, dr) = jac.Back(ux, uy);
            var key = ((int)Math.Round(dr, MidpointRounding.AwayFromZero), (int)Math.Round(dc, MidpointRounding.AwayFromZero));
            cells[key] = cells.TryGetValue(key, out double v) ? v + wgt : wgt;
            total += wgt;
        }
        if (!(total > 0)) return null;
        int rMin = cells.Keys.Min(k => k.Item1), rMax = cells.Keys.Max(k => k.Item1);
        int cMin = cells.Keys.Min(k => k.Item2), cMax = cells.Keys.Max(k => k.Item2);
        // Symmetric about the centre so the kernel's centre is the zero offset.
        int rr = Math.Max(-rMin, rMax), cc = Math.Max(-cMin, cMax);
        var weights = new double[2 * rr + 1, 2 * cc + 1];
        foreach (var kv in cells.OrderBy(k => k.Key))
            weights[kv.Key.Item1 + rr, kv.Key.Item2 + cc] = kv.Value / total;
        return new PixelKernel(weights, rr, cc);
    }

    private static PixelKernel Dark => new(new double[,] { { 0 } }, 0, 0);

    private static PixelKernel GeometricKernel(SequentialLens lens, OpticalSystemData system, RayLauncher launcher, SequentialTrace trace,
        int field, int wave, int sampling, Jacobian jac, string where, List<string> vignetted)
    {
        var (hx, hy) = system.Fields.Normalized(field);
        var apodization = PsfEngine.Apodization(lens, system, "Image simulation");
        int image = lens.ImageIndex;
        var chief = RaySampler.AtSurface(trace, launcher, hx, hy, 0, 0, image, new TraceOptions { IgnoreApertures = true });
        var (pupil, _) = PsfEngine.Nodes(sampling);
        var samples = new List<(double, double, double)>();
        foreach (var (px, py) in pupil)
        {
            var ray = RaySampler.AtSurface(trace, launcher, hx, hy, px, py, image, new TraceOptions());
            if (!ray.Passed) continue;
            double a = apodization(Math.Sqrt(px * px + py * py));
            samples.Add((ray.Position.X - chief.Position.X, ray.Position.Y - chief.Position.Y, a * a));
        }
        var k = Bin(samples, jac);
        if (k is null) { vignetted.Add(where); return Dark; }
        return k;
    }

    private static PixelKernel DiffractionKernel(SequentialLens lens, OpticalSystemData system, RayLauncher launcher, SequentialTrace trace,
        int field, int wave, ImageSimulationRequest request, Jacobian jac, string where, List<string> warnings, List<string> switched,
        List<string> vignetted, ref double minCaptured, CancellationToken cancellation)
    {
        int n = request.PupilSampling;
        var options = PsfRuns.Options(false, checkApertures: true, allow: false);
        double lambda, fNumber, period, rms;
        Func<double[], double[], double[,]> evaluate;
        double cellEnergy = double.NaN;
        try
        {
            if (request.Engine == DiffractionPsfEngine.Fft)
            {
                var p = PsfEngine.BuildPupil(lens, system, field, wave, n, options, cancellation);
                lambda = p.Wavelength;
                fNumber = Math.Min(p.FNumberX, p.FNumberY);
                double tx = p.Wavelength / (p.ImageIndex * Math.Abs(p.ScaleX) * p.NodeSpacing);
                double ty = p.Wavelength / (p.ImageIndex * Math.Abs(p.ScaleY) * p.NodeSpacing);
                period = Math.Min(tx, ty);
                cellEnergy = p.PowerSum * tx * ty / (p.AmplitudeSum * p.AmplitudeSum);
                var c = p.Opd!.ChiefRay.Position;
                rms = Rms(p.Opd.Samples.Where(s => s.IsValid).Select(s => (s.Ray.Position.X - c.X, s.Ray.Position.Y - c.Y, 1.0)));
                warnings.AddRange(PsfEngine.CheckSampling(p).Warnings.Select(x => $"{where}: {x}"));
                evaluate = (xs, ys) => PsfEngine.Sample(p, xs, ys, 1, cancellation).Intensity;
            }
            else
            {
                var hp = PsfEngine.BuildHuygens(lens, system, field, wave, n, options, null, false, cancellation);
                lambda = hp.Wavelength;
                fNumber = hp.FNumber;
                period = 2 * fNumber * lambda / hp.NodeSpacing;
                var c = hp.Centre;
                rms = Rms(hp.Rays.Select(r => (r.Position.X - c.X, r.Position.Y - c.Y, r.Weight * r.Weight)));
                evaluate = (xs, ys) =>
                {
                    warnings.AddRange(PsfEngine.HuygensWarnings(hp, xs, ys).Select(x => $"{where}: {x}"));
                    return PsfEngine.Huygens(hp, xs, ys, 1, cancellation).Intensity;
                };
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("too few rays", StringComparison.Ordinal) || ex.Message.Contains("no light", StringComparison.OrdinalIgnoreCase))
        {
            vignetted.Add(where);
            return Dark;
        }
        double airy = 1.22 * lambda * fNumber;
        if (rms > SwitchFactor * airy)
        {
            switched.Add($"{where} (RMS radius {rms * 1e6:g4} µm, Airy radius {airy * 1e6:g4} µm)");
            return GeometricKernel(lens, system, launcher, trace, field, wave, n, jac, where, vignetted);
        }
        double lf = lambda * fNumber;
        double window = Math.Min(period / 2, Math.Max(8 * lf, 3 * rms + 4 * lf));
        double h = Math.Min(Math.Min(lf / 2, jac.MinimumStretch / 2), 2 * window / request.ImageSampling);
        int m = (int)Math.Ceiling(window / h);
        if (2 * m + 1 > MaximumPsfSamples)
            throw new ArgumentException($"Image simulation, {where}: the PSF window needs {2 * m + 1} samples across (more than {MaximumPsfSamples}): a source pixel's image ({jac.MinimumStretch * 1e6:g4} µm) is far smaller than the PSF ({window * 2e6:g4} µm). Lower the oversampling or raise the field height.");
        var grid = new double[2 * m + 1];
        for (int i = 0; i < grid.Length; i++) grid[i] = (i - m) * h;
        var intensity = evaluate(grid, grid);
        var samples = new List<(double, double, double)>(grid.Length * grid.Length);
        double sum = 0;
        for (int r = 0; r < grid.Length; r++)
            for (int c = 0; c < grid.Length; c++)
            {
                double v = intensity[r, c];
                sum += v;
                samples.Add((grid[c], grid[r], v));
            }
        if (double.IsFinite(cellEnergy) && cellEnergy > 0)
            minCaptured = Math.Min(minCaptured, sum * h * h / cellEnergy);
        var k = Bin(samples, jac);
        if (k is null) { vignetted.Add(where); return Dark; }
        return k;
    }

    private static double Rms(IEnumerable<(double X, double Y, double W)> points)
    {
        double sw = 0, sx = 0, sy = 0, sxx = 0;
        var list = points.ToList();
        foreach (var (x, y, w) in list) { sw += w; sx += w * x; sy += w * y; }
        if (!(sw > 0)) return 0;
        double mx = sx / sw, my = sy / sw;
        foreach (var (x, y, w) in list) sxx += w * ((x - mx) * (x - mx) + (y - my) * (y - my));
        return Math.Sqrt(sxx / sw);
    }
}
