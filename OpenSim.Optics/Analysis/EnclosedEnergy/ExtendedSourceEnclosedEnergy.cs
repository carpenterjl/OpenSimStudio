using System.Buffers.Binary;
using System.Text;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.EnclosedEnergy;

/// <summary>A square IMA source image: one or more n × n pixel maps, [row, column], row 0 the
/// top of the image and column 0 its left.</summary>
public sealed record ImaImage
{
    public required int Size { get; init; }

    /// <summary>The pixel maps: one for every wavelength, or one per wavelength (binary files).
    /// Values 0–9 (text) or 0–255 (binary).</summary>
    public required IReadOnlyList<double[,]> Maps { get; init; }

    public bool Binary { get; init; }
}

/// <summary>
/// OpticStudio's IMA source files (help, "Geometric Image Analysis", "The IMA format"), read for
/// the extended source enclosed energy (OP-41).
/// <list type="bullet">
/// <item>Text: "At the top of the file is one number which indicates the size of the file in
/// pixels. The remaining rows and columns contain the pixel data, with one character to each
/// pixel. All IMA files must be square"; "The "intensity" at each pixel can be any digit between
/// 0 and 9".</item>
/// <item>Binary: "3 16-bit header values. The first 16-bit value is a signed integer that must be
/// equal to zero. The second 16-bit signed integer is the width of the pixel map in pixels, which
/// can be any number from 1 to 8000. The third 16-bit signed integer is the number of pixel maps";
/// each pixel "an unsigned byte"; "The data for each color is stored by columns for each row (the
/// column index changes faster than the row index)".</item>
/// </list>
/// The help does not give the binary byte order; little-endian (the PC's) is read. The help's
/// letter-F example lists its top row first, so row 0 is the top of the image here for both kinds.
/// The BIM format is described for the Geometric Image Analysis only and is not read.
/// </summary>
public static class ImaFile
{
    public const int MaximumWidth = 8000;

    /// <summary>Reads the file at <paramref name="path"/> (a full path).</summary>
    public static ImaImage Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("IMA file: no file named; give the full path of a .IMA file.");
        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"IMA file: \"{path}\" is not a full path; give the full path of the .IMA file.");
        if (!string.Equals(Path.GetExtension(path), ".ima", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"IMA file: \"{path}\" does not end in .IMA (help: \"the file must end in the extension IMA\"); BIM files are not read.");
        if (!File.Exists(path)) throw new FileNotFoundException($"IMA file: \"{path}\" does not exist.", path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Parses the bytes of an IMA file: binary when the first 16-bit value is zero,
    /// text otherwise (a text file starts with a digit).</summary>
    public static ImaImage Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == 0) return ParseBinary(bytes, name);
        return ParseText(bytes, name);
    }

    private static ImaImage ParseBinary(byte[] bytes, string name)
    {
        if (bytes.Length < 6) throw new FormatException($"IMA file {name}: a binary IMA file needs a 6-byte header (it has {bytes.Length} bytes).");
        int width = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2));
        int maps = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4));
        if (width < 1 || width > MaximumWidth)
            throw new FormatException($"IMA file {name}: the width is {width}; the help allows 1 to {MaximumWidth}.");
        if (maps < 1) throw new FormatException($"IMA file {name}: the number of pixel maps is {maps}; at least 1.");
        long expected = 6 + (long)maps * width * width;
        if (bytes.Length < expected)
            throw new FormatException($"IMA file {name}: {maps} map(s) of {width} × {width} pixels need {expected} bytes; the file has {bytes.Length}.");
        var list = new List<double[,]>();
        int offset = 6;
        for (int m = 0; m < maps; m++)
        {
            var map = new double[width, width];
            for (int r = 0; r < width; r++)
                for (int c = 0; c < width; c++) map[r, c] = bytes[offset++];
            list.Add(map);
        }
        return new ImaImage { Size = width, Maps = list, Binary = true };
    }

    private static ImaImage ParseText(byte[] bytes, string name)
    {
        var lines = Encoding.ASCII.GetString(bytes).Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        int first = lines.FindIndex(l => l.Length > 0);
        if (first < 0) throw new FormatException($"IMA file {name}: the file is empty.");
        if (!int.TryParse(lines[first].Trim(), out int n) || n < 1 || n > MaximumWidth)
            throw new FormatException($"IMA file {name}: the first line must be the size in pixels, 1 to {MaximumWidth} (it is \"{lines[first].Trim()}\").");
        var rows = lines.Skip(first + 1).Where(l => l.Length > 0).ToList();
        if (rows.Count != n)
            throw new FormatException($"IMA file {name}: the size is {n} but {rows.Count} pixel rows follow; the image must be square.");
        var map = new double[n, n];
        for (int r = 0; r < n; r++)
        {
            string row = rows[r];
            if (row.Length != n)
                throw new FormatException($"IMA file {name}: pixel row {r + 1} has {row.Length} characters; it needs {n} (one digit per pixel, no separators).");
            for (int c = 0; c < n; c++)
            {
                char ch = row[c];
                if (ch < '0' || ch > '9')
                    throw new FormatException($"IMA file {name}: pixel row {r + 1}, column {c + 1} is '{ch}'; each pixel is a digit 0 to 9.");
                map[r, c] = ch - '0';
            }
        }
        return new ImaImage { Size = n, Maps = new[] { map } };
    }
}

/// <summary>What <see cref="ExtendedSourceEnclosedEnergy.Trace"/> traces.</summary>
public sealed record ExtendedSourceRequest
{
    public required ImaImage Image { get; init; }

    /// <summary>The 0-based field row the image is centred on.</summary>
    public required int Field { get; init; }

    /// <summary>The image's full width in field coordinates: degrees for angle fields, meters for
    /// height fields (help: "the full width of the square image file in field coordinates").</summary>
    public required double FieldSize { get; init; }

    public required IReadOnlyList<int> Wavelengths { get; init; }

    /// <summary>About 1000 × this many rays (help, "Rays x 1000").</summary>
    public int RaysThousands { get; init; } = 1;

    public EnclosedEnergyReference Reference { get; init; } = EnclosedEnergyReference.ChiefRay;

    /// <summary>The surface to read the rays at; null for the image.</summary>
    public int? Surface { get; init; }

    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>
/// OpticStudio's Extended Source enclosed energy (OP-41; help, "Extended Source": "Computes
/// encircled energy using an extended source similar to the geometric image analysis feature").
/// <para>
/// Each lit pixel of the IMA image is a patch of field points: pixel (row r, column c) of an n × n
/// image spans the field (f_x + ((c + u)/n − ½)·S, f_y + (½ − (r + v)/n)·S), u, v ∈ [0, 1), S the
/// field size and f the chosen field. Each pixel traces at least one ray per wavelength and in all
/// about 1000 × "Rays x 1000" rays, shared in proportion to pixel value × wavelength weight (help:
/// "the distribution of rays over the pixels in the image must be uniform"; "at least 1500 rays
/// will be traced"; "The distribution of rays at each wavelength is in proportion to the
/// wavelength weights"). Each ray weighs its pixel's value × wavelength weight over the pixel's
/// ray count, times the pupil apodization A(ρ)², so the light per pixel and wavelength is exact
/// whatever the count. The help picks ray positions "randomly from coordinates within the pixel
/// cell" and pupil coordinates "uniform over ... the circular paraxial entrance pupil"; here the
/// positions come from a 4-D Sobol sequence (u, v in the pixel; ρ = √s, θ = 2πt in the pupil), one
/// running index over all rays, so a result repeats exactly. Each wavelength traces its own rays
/// instead of drawing one wavelength per ray at random; the expected light is the same.
/// </para>
/// <para>
/// The field points between the defined fields are launched by normalising against the defined
/// fields plus the image's four corners (weight 0), so a source wider than the defined fields
/// still launches; the corners change no defined field's coordinates. Vignetting factors cannot
/// be interpolated across the image this way, so a system with them is refused unless they are
/// removed (replaced by apertures, as OP-37 and OP-40 do).
/// </para>
/// </summary>
public static class ExtendedSourceEnclosedEnergy
{
    /// <summary>The largest number of rays one run traces.</summary>
    public const int MaximumRays = 2_000_000;

    /// <summary>The rays of the extended source read at the surface, with the reference point.
    /// <paramref name="lens"/> and <paramref name="system"/> should come through
    /// <c>RmsCore.Prepare</c> (floating apertures frozen, vignetting factors removed when asked).</summary>
    public static GeometricEnclosedEnergySpot Trace(SequentialLens lens, OpticalSystemData system, ExtendedSourceRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        string who = "Extended source enclosed energy";
        var image = request.Image;
        int f = request.Field;
        if (f < 0 || f >= system.Fields.Points.Count)
            throw new ArgumentOutOfRangeException(nameof(request), f, $"{who}: field {f + 1} is not one of the {system.Fields.Points.Count} fields.");
        if (!(request.FieldSize > 0) || !double.IsFinite(request.FieldSize))
            throw new ArgumentException($"{who}: the field size must be positive (it is {request.FieldSize:g6}).");
        if (request.RaysThousands < 1) throw new ArgumentException($"{who}: Rays x 1000 must be at least 1 (it is {request.RaysThousands}).");
        if (request.Reference == EnclosedEnergyReference.Middle)
            throw new ArgumentException($"{who}: the help offers the chief ray, the centroid or the vertex as the reference, not the middle.");
        if (system.Fields.Points.Any(p => p.IsVignetted))
            throw new InvalidOperationException($"{who}: the fields have vignetting factors, which cannot be interpolated across the source image; turn on \"Remove vignetting factors\" (they are replaced by surface apertures).");
        var waves = request.Wavelengths;
        if (waves.Count == 0) throw new ArgumentException($"{who}: at least one wavelength.");
        if (image.Maps.Count != 1 && image.Maps.Count != system.Wavelengths.Wavelengths.Count)
            throw new ArgumentException($"{who}: the IMA file has {image.Maps.Count} pixel maps; it needs 1, or one per wavelength ({system.Wavelengths.Wavelengths.Count}).");

        // The fields with the image's corners appended, so every pixel normalises inside them.
        int n = image.Size;
        var centre = system.Fields.Points[f];
        double half = request.FieldSize / 2;
        var corners = new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }
            .Select(s => new FieldPoint { X = centre.X + s.Item1 * half, Y = centre.Y + s.Item2 * half, Weight = 0 }).ToArray();
        if (FieldTypes.IsAngle(system.Fields.Type) && corners.Any(c => !(Math.Abs(c.X) < 90) || !(Math.Abs(c.Y) < 90)))
            throw new ArgumentException($"{who}: the image reaches {request.FieldSize / 2:g6}° from field {f + 1} ({centre.X:g6}°, {centre.Y:g6}°), beyond 90° from the axis; shrink the field size.");
        var extended = system with { Fields = system.Fields with { Points = system.Fields.Points.Concat(corners).ToArray() } };
        var scale = extended.Fields.Scale;
        var launcher = new RayLauncher(lens, extended);
        var apodization = PsfEngine.Apodization(lens, system, who);
        int surface = request.Surface ?? lens.ImageIndex;
        if (surface < 1 || surface > lens.ImageIndex)
            throw new ArgumentOutOfRangeException(nameof(request), surface, $"{who}: surface {surface} is not one of the surfaces 1 to {lens.ImageIndex} (the image).");
        var traces = waves.Select(w => SequentialTrace.Create(lens, extended, w)).ToArray();
        bool poly = waves.Count > 1;
        var weights = waves.Select(w => poly ? extended.Wavelengths.Wavelengths[w].Weight : 1.0).ToArray();
        for (int i = 0; i < weights.Length; i++)
            if (!(weights[i] >= 0) || !double.IsFinite(weights[i]))
                throw new ArgumentException($"{who}: wavelength {waves[i] + 1}'s weight is {weights[i]:g6}; it must be finite and not negative.");

        // Rays per pixel and wavelength, and the running Sobol index each (wavelength, row) starts at.
        double total = 0;
        double[,] Map(int wi) => image.Maps.Count == 1 ? image.Maps[0] : image.Maps[waves[wi]];
        for (int wi = 0; wi < waves.Count; wi++)
        {
            var map = Map(wi);
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++) total += weights[wi] * map[r, c];
        }
        if (!(total > 0)) throw new ArgumentException($"{who}: the IMA image has no lit pixel at a wavelength of non-zero weight, so there is no light.");
        double target = 1000.0 * request.RaysThousands;
        var counts = new int[waves.Count][,];
        var starts = new long[waves.Count * n + 1];
        long rays = 0;
        for (int wi = 0; wi < waves.Count; wi++)
        {
            var map = Map(wi);
            counts[wi] = new int[n, n];
            for (int r = 0; r < n; r++)
            {
                starts[wi * n + r] = rays;
                for (int c = 0; c < n; c++)
                {
                    double e = weights[wi] * map[r, c];
                    if (!(e > 0)) continue;
                    int k = (int)Math.Max(1, Math.Round(target * e / total));
                    counts[wi][r, c] = k;
                    rays += k;
                }
                if (rays > MaximumRays)
                    throw new ArgumentException($"{who}: the image needs more than {MaximumRays} rays (every lit pixel traces at least one per wavelength); lower Rays x 1000 or use a smaller image.");
            }
        }
        starts[waves.Count * n] = rays;

        var options = new TraceOptions();
        var sobol = new SobolSequence(4);
        var slots = ParallelSlots.Compute(waves.Count * n, request.MaxDegreeOfParallelism, slot =>
        {
            int wi = slot / n, r = slot % n;
            var map = Map(wi);
            var points = new List<(SpotPoint Point, int Wave)>();
            var all = new List<SampledRay>();
            long index = starts[slot] + 1; // index 0 of the Sobol sequence is the corner (0, 0, 0, 0)
            Span<double> q = stackalloc double[4];
            for (int c = 0; c < n; c++)
            {
                int k = counts[wi][r, c];
                if (k == 0) continue;
                cancellation.ThrowIfCancellationRequested();
                double weight = weights[wi] * map[r, c] / k;
                for (int j = 0; j < k; j++, index++)
                {
                    sobol.Point(index, q);
                    double fx = centre.X + ((c + q[0]) / n - 0.5) * request.FieldSize;
                    double fy = centre.Y + (0.5 - (r + q[1]) / n) * request.FieldSize;
                    double rho = Math.Sqrt(q[2]), theta = 2 * Math.PI * q[3];
                    double px = rho * Math.Cos(theta), py = rho * Math.Sin(theta);
                    var (hx, hy) = OpticsConventions.NormalizeField((fx, fy), scale);
                    var ray = RaySampler.AtSurface(traces[wi], launcher, hx, hy, px, py, surface, options);
                    all.Add(ray);
                    if (!ray.Passed) continue;
                    double a = apodization(rho);
                    points.Add((new SpotPoint(ray.Position.X, ray.Position.Y, weight * a * a), waves[wi]));
                }
            }
            return (points, all);
        }, cancellation);

        var spot = new List<SpotPoint>();
        var waveOf = new List<int>();
        var traced = new List<SampledRay>();
        foreach (var (points, all) in slots)
        {
            foreach (var (p, w) in points) { spot.Add(p); waveOf.Add(w); }
            traced.AddRange(all);
        }
        if (spot.Count == 0)
            throw new InvalidOperationException($"{who}: no ray reached surface {surface}: " + string.Join("; ", RaySampler.DescribeFailures(traced)) + ".");

        // The chief ray of the centre field: the selected wavelength's, or the primary's for several.
        int chiefWave = poly ? extended.Wavelengths.PrimaryIndex : waves[0];
        var chiefTrace = SequentialTrace.Create(lens, extended, chiefWave);
        var (chx, chy) = OpticsConventions.NormalizeField((centre.X, centre.Y), scale);
        var chiefRay = RaySampler.AtSurface(chiefTrace, launcher, chx, chy, 0, 0, surface, new TraceOptions { IgnoreApertures = true });
        (double, double)? chief = chiefRay.AtSurface is null ? null : (chiefRay.Position.X, chiefRay.Position.Y);
        var (rx, ry) = GeometricEnclosedEnergy.ReferencePoint(spot, request.Reference, chief, who);
        return new GeometricEnclosedEnergySpot
        {
            Points = spot, WaveOf = waveOf, ReferenceX = rx, ReferenceY = ry, Reference = request.Reference, Chief = chief,
            Field = f, Wavelengths = waves.ToArray(), Surface = surface, Launched = traced.Count,
            Failures = RaySampler.DescribeFailures(traced),
        };
    }
}
