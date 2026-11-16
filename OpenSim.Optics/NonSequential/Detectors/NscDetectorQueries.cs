namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// The values OpticStudio's detector operands and ZPL functions read from detector data (wave 15
/// wiring; docs/Optics-Conventions.md §73): NSDD (Detector Rectangle, incoherent), NSDE (Detector
/// Color), NSDP (Detector Polar) and NSDC (Detector Rectangle, coherent), as the help defines them
/// (OpticStudio 2025 R2, "Non-sequential Ray Tracing and Detector Operands", saved under
/// plans/sources/optics/op-55b-help/, with the variance formula images NSDD1.png and NSDD2.png
/// under wire15-help/; and the ZPL "Numeric Functions" page, op-99-help/).
/// <para>
/// Units: flux in the rays' units (source units); positions in lens units in the detector's
/// local frame; angles in degrees; areas in square meters (this build has no Analysis Units
/// setting; square meters is one of the help's choices, and the Detector Viewer uses it); solid
/// angles in steradians. Pixels are numbered from 1 as the detectors' help pages number them.
/// What the help leaves open is refused by name, never guessed.
/// </para>
/// </summary>
public static class NscDetectorQueries
{
    // ================================================================ NSDD

    /// <summary>
    /// NSDD on a Detector Rectangle's data: <paramref name="pixel"/> &gt; 0 that pixel; 0 the
    /// total (Data 0 flux, 1 the average flux/area, 2 the angle-space flux); −1/−2 the maximum and
    /// minimum; −3 the rays striking the detector; −4/−5 the standard deviation (RMS from the mean)
    /// and the mean of the non-zero pixels; −6/−7/−8 the x, y, z centroid; −9 to −12 the weighted
    /// RMS radius, x, y, z about the centroid, σ² = Σw(x − x*)²/Σw with x* = Σwx/Σw. Data 0 is
    /// flux, 1 flux/area, 2 flux/solid angle (angle space). <paramref name="ignored"/> edge pixels
    /// are left out of every pixel ≤ 0 value but −3.
    /// </summary>
    public static double Nsdd(NscDetectorData data, int pixel, int dataCode, int ignored, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(data);
        string who = $"NSDD, detector object {data.ObjectNumber}";
        if (data is NscPolarDetectorData) throw new ArgumentException($"{who} is a Detector Polar; the help reads it with NSDP.");
        var d = (NscFlatDetectorData)data;
        if (d.IsColor) throw new ArgumentException($"{who} is a Detector Color; the help reads it with NSDE.");
        if (dataCode == 3)
            throw new NotSupportedException($"{who}: Data 3 (flux normalized to the peak of all consecutive NSDD operands of the detector) is the NSC Bitmap Merit Function's, which is not built.");
        if (dataCode is 4 or 5)
            throw new NotSupportedException($"{who}: Data {dataCode} (absorbed flux) is for objects as detectors (\"Object Is A Detector\"), which are not built (Conventions §73).");
        if (dataCode is < 0 or > 2) throw new ArgumentException($"{who}: Data is {dataCode}; it must be 0 (flux), 1 (flux/area) or 2 (flux/solid angle).");
        if (pixel is -14 or -15)
            throw new NotSupportedException($"{who}: Pix# {pixel} (the geometric MTF) is not built (the non-sequential image-quality analyses are plan item OP-87).");
        if (pixel == -13)
            throw new NotSupportedException($"{who}: Pix# −13 is the square root of the xy second moment, which can be negative; the help does not say what is returned then.");
        if (pixel == -3) return d.SpatialHits;
        bool angle = dataCode == 2;
        double Value(int ix, int iy) => dataCode switch
        {
            0 => d.Flux[d.Index(ix, iy)],
            1 => d.Irradiance(d.Index(ix, iy)),
            _ => d.RadiantIntensity(ix, iy),
        };
        if (pixel > 0)
        {
            var (ix, iy) = FlatPixel(d, pixel, who);
            return Value(ix, iy);
        }
        if (pixel < -13) throw new ArgumentException($"{who}: Pix# is {pixel}; it must be a pixel (1 to {d.PixelCount}) or 0 to −15.");
        var (x0, x1, y0, y1) = Included(d, ignored, who);
        if (pixel == 0)
        {
            double sum = 0;
            for (int iy = y0; iy < y1; iy++)
                for (int ix = x0; ix < x1; ix++)
                    sum += dataCode == 2 ? d.AngleFlux[d.Index(ix, iy)] : d.Flux[d.Index(ix, iy)];
            return dataCode == 1 ? sum / ((x1 - x0) * (y1 - y0) * d.PixelArea) : sum;
        }
        var values = new List<double>();
        for (int iy = y0; iy < y1; iy++)
            for (int ix = x0; ix < x1; ix++) values.Add(Value(ix, iy));
        switch (pixel)
        {
            case -1: return values.Max();
            case -2: return values.Min();
            case -4:
            case -5:
            {
                var nonZero = values.Where(v => v != 0).ToArray();
                if (nonZero.Length == 0) return 0;
                double mean = nonZero.Average();
                return pixel == -5 ? mean : Math.Sqrt(nonZero.Sum(v => (v - mean) * (v - mean)) / nonZero.Length);
            }
        }
        // −6 to −12: moments of the pixel coordinates weighted by the pixel values.
        int axis = pixel switch { -6 or -10 => 0, -7 or -11 => 1, -8 or -12 => 2, _ => -1 };
        if (angle && axis == 2)
            throw new NotSupportedException($"{who}: Pix# {pixel} asks for a z coordinate in angle space, which has only x and y angles; the help does not say what it is.");
        double sw = 0, sx = 0, sy = 0;
        for (int iy = y0; iy < y1; iy++)
            for (int ix = x0; ix < x1; ix++)
            {
                double w = Value(ix, iy);
                var (x, y) = Coordinate(d, ix, iy, angle, unit);
                sw += w; sx += w * x; sy += w * y;
            }
        if (!(sw > 0)) throw new InvalidOperationException($"{who}: the included pixels hold no {Name(dataCode)}, so there is no centroid.");
        double cx = sx / sw, cy = sy / sw;
        if (pixel is -6 or -7 or -8) return axis switch { 0 => cx, 1 => cy, _ => 0 };
        double vx = 0, vy = 0;
        for (int iy = y0; iy < y1; iy++)
            for (int ix = x0; ix < x1; ix++)
            {
                double w = Value(ix, iy);
                var (x, y) = Coordinate(d, ix, iy, angle, unit);
                vx += w * (x - cx) * (x - cx); vy += w * (y - cy) * (y - cy);
            }
        vx /= sw; vy /= sw;
        return pixel switch
        {
            -9 => Math.Sqrt(vx + vy),     // the flat detector's z is 0 at every pixel, so σz² = 0
            -10 => Math.Sqrt(vx),
            -11 => Math.Sqrt(vy),
            _ => 0,                         // −12: σz of a flat detector
        };
    }

    private static string Name(int dataCode) => dataCode switch { 0 => "flux", 1 => "flux/area", _ => "flux/solid angle" };

    private static (double X, double Y) Coordinate(NscFlatDetectorData d, int ix, int iy, bool angle, LensUnit unit)
    {
        if (angle) return d.AngleCenter(ix, iy);
        var (x, y) = d.PixelCenter(ix, iy);
        return (OpticsConventions.FromMeters(x, unit), OpticsConventions.FromMeters(y, unit));
    }

    private static (int Ix, int Iy) FlatPixel(NscFlatDetectorData d, int pixel, string who)
    {
        if (pixel > d.PixelCount) throw new ArgumentException($"{who}: pixel {pixel} does not exist; the detector has pixels 1 to {d.PixelCount} ({d.PixelsX} × {d.PixelsY}).");
        int i = pixel - 1;
        return (i % d.PixelsX, i / d.PixelsX);
    }

    private static (int X0, int X1, int Y0, int Y1) Included(NscFlatDetectorData d, int ignored, string who)
    {
        if (ignored < 0) throw new ArgumentException($"{who}: # Ignored is {ignored}; it must be 0 or more.");
        int x0 = ignored, x1 = d.PixelsX - ignored, y0 = ignored, y1 = d.PixelsY - ignored;
        if (x1 <= x0 || y1 <= y0)
            throw new ArgumentException($"{who}: ignoring {ignored} edge pixels on each side of a {d.PixelsX} × {d.PixelsY} detector leaves none.");
        return (x0, x1, y0, y1);
    }

    // ================================================================ NSDE

    /// <summary>
    /// NSDE on a Detector Color's data: <paramref name="pixel"/> &gt; 0 that pixel, 0 the sum over
    /// the pixels (chromaticity: the average over the pixels with non-zero lumens; per-area data
    /// over the whole detector's area or solid angle), −1/−2 the maximum and minimum, −3 the rays
    /// striking it. <paramref name="angle"/> 0 position space, 1 angle space. Data 1 power, 2
    /// power/area, 3 lumens, 4 lumens/area, 5/6 CIE 1931 x/y, 7/8 CIE 1976 u′/v′, 9/10/11 the
    /// tristimulus X/Y/Z per area. Data 12–21 are refused by name.
    /// </summary>
    public static double Nsde(NscDetectorData data, int pixel, int angle, int dataCode, int ignored)
    {
        ArgumentNullException.ThrowIfNull(data);
        string who = $"NSDE, detector object {data.ObjectNumber}";
        if (data is not NscFlatDetectorData { IsColor: true } d)
            throw new ArgumentException($"{who} is a {data.TypeName}; NSDE reads Detector Color objects (NSDD reads Detector Rectangle, NSDP Detector Polar).");
        if (dataCode is 12 or 13)
            throw new NotSupportedException($"{who}: Data {dataCode} (the colour rendering index and correlated colour temperature) is not built.");
        if (dataCode is >= 14 and <= 16)
            throw new NotSupportedException($"{who}: Data {dataCode} (tristimulus normalized over consecutive NSDE operands) is the NSC Bitmap Merit Function's, which is not built.");
        if (dataCode is >= 17 and <= 21)
            throw new NotSupportedException($"{who}: Data {dataCode} (centroid and RMS in one spectral bin) needs per-pixel spectral data; the Detector Color records its spectrum for the whole detector only (Conventions §73).");
        if (dataCode is < 1 or > 11) throw new ArgumentException($"{who}: Data is {dataCode}; it must be 1 to 21.");
        if (angle is not (0 or 1)) throw new ArgumentException($"{who}: Angle? is {angle}; it must be 0 (position space) or 1 (angle space).");
        bool ang = angle == 1;
        if (pixel == -3) return ang ? d.AngularHits : d.SpatialHits;
        double[] flux = ang ? d.AngleFlux : d.Flux;
        double[]? lum = ang ? d.AngleLuminous : d.Luminous;
        double[]? X = ang ? d.AngleX : d.X, Y = ang ? d.AngleY : d.Y, Z = ang ? d.AngleZ : d.Z;
        if (dataCode >= 3 && (lum is null || X is null || Y is null || Z is null))
            throw new InvalidOperationException($"{who}: the data holds no photometric or colour values (data read without them).");
        double Area(int ix, int iy) => ang ? d.AngleSolidAngle(ix, iy) : d.PixelArea;
        double Value(int ix, int iy, double area)
        {
            int i = d.Index(ix, iy);
            return dataCode switch
            {
                1 => flux[i],
                2 => flux[i] / area,
                3 => lum![i],
                4 => lum![i] / area,
                5 or 6 or 7 or 8 => Chromaticity(X![i], Y![i], Z![i], dataCode),
                9 => X![i] / area,
                10 => Y![i] / area,
                _ => Z![i] / area,
            };
        }
        bool chroma = dataCode is >= 5 and <= 8;
        if (pixel > 0)
        {
            var (ix, iy) = FlatPixel(d, pixel, who);
            return Value(ix, iy, Area(ix, iy));
        }
        if (pixel < -3) throw new ArgumentException($"{who}: Pix# is {pixel}; it must be a pixel (1 to {d.PixelCount}) or 0 to −3.");
        var (x0, x1, y0, y1) = Included(d, ignored, who);
        if (pixel == 0)
        {
            if (chroma)
            {
                double s = 0; int n = 0;
                for (int iy = y0; iy < y1; iy++)
                    for (int ix = x0; ix < x1; ix++)
                        if (lum![d.Index(ix, iy)] != 0) { s += Value(ix, iy, 1); n++; }
                return n == 0 ? throw new InvalidOperationException($"{who}: no pixel has non-zero lumens, so there is no average chromaticity.") : s / n;
            }
            double sum = 0, area = 0;
            for (int iy = y0; iy < y1; iy++)
                for (int ix = x0; ix < x1; ix++) { sum += Value(ix, iy, 1); area += Area(ix, iy); }
            return dataCode is 2 or 4 or 9 or 10 or 11 ? sum / area : sum;
        }
        var values = new List<double>();
        for (int iy = y0; iy < y1; iy++)
            for (int ix = x0; ix < x1; ix++)
            {
                if (chroma && lum![d.Index(ix, iy)] == 0) continue;
                values.Add(Value(ix, iy, Area(ix, iy)));
            }
        if (values.Count == 0) throw new InvalidOperationException($"{who}: no pixel has non-zero lumens, so there is no chromaticity to compare.");
        return pixel == -1 ? values.Max() : values.Min();
    }

    /// <summary>x = X/(X+Y+Z), y = Y/(X+Y+Z); u′ = 4X/(X+15Y+3Z), v′ = 9Y/(X+15Y+3Z) (as
    /// <see cref="Photometry.Photometric"/> defines them, CIE e-ILV 17-23-053 and 17-23-073); 0
    /// where the pixel holds nothing.</summary>
    private static double Chromaticity(double x, double y, double z, int code)
    {
        double s = code is 5 or 6 ? x + y + z : x + 15 * y + 3 * z;
        if (!(s > 0)) return 0;
        return code switch { 5 => x / s, 6 => y / s, 7 => 4 * x / s, _ => 9 * y / s };
    }

    // ================================================================ NSDP

    /// <summary>
    /// NSDP on a Detector Polar's data: <paramref name="pixel"/> &gt; 0 that pixel (polar index
    /// fastest), 0 the sum (chromaticity: the average over the pixels with non-zero lumens), −1/−2
    /// the maximum and minimum, −3 the rays, −4 the radial RMS angle about 0° (Data 1 or 3; any
    /// other Data gives 0, as the help says). Data 1 power, 2 power/sr, 3 lumens, 4 lumens/sr,
    /// 5–8 chromaticity, 9–11 X/Y/Z per sr. Pixel 0 with per-steradian data is refused: the help
    /// does not say whether the intensities are summed or the sum is divided by the detector's
    /// solid angle. For −4 a pixel's polar angle is its nominal angle j·inc, the centre of its bin
    /// [(j − ½)inc, (j + ½)inc] (the end bins are half bins; a reading, Conventions §10 item 69).
    /// </summary>
    public static double Nsdp(NscDetectorData data, int pixel, int dataCode)
    {
        ArgumentNullException.ThrowIfNull(data);
        string who = $"NSDP, detector object {data.ObjectNumber}";
        if (data is not NscPolarDetectorData d)
            throw new ArgumentException($"{who} is a {data.TypeName}; NSDP reads Detector Polar objects (NSDD reads Detector Rectangle, NSDE Detector Color).");
        if (dataCode is < 1 or > 11) throw new ArgumentException($"{who}: Data is {dataCode}; it must be 1 to 11.");
        if (pixel == -3) return d.Hits;
        if (dataCode >= 3 && (d.Luminous is null || d.X is null || d.Y is null || d.Z is null))
            throw new InvalidOperationException($"{who}: the data holds no photometric or colour values (data read without them).");
        bool chroma = dataCode is >= 5 and <= 8;
        double Value(int j, int a)
        {
            int i = d.Index(j, a);
            double omega = d.SolidAngle(j);
            return dataCode switch
            {
                1 => d.Flux[i],
                2 => d.Flux[i] / omega,
                3 => d.Luminous![i],
                4 => d.Luminous![i] / omega,
                5 or 6 or 7 or 8 => Chromaticity(d.X![i], d.Y![i], d.Z![i], dataCode),
                9 => d.X![i] / omega,
                10 => d.Y![i] / omega,
                _ => d.Z![i] / omega,
            };
        }
        if (pixel > 0)
        {
            if (pixel > d.PixelCount) throw new ArgumentException($"{who}: pixel {pixel} does not exist; the detector has pixels 1 to {d.PixelCount}.");
            return Value((pixel - 1) % d.PolarPixels, (pixel - 1) / d.PolarPixels);
        }
        switch (pixel)
        {
            case 0:
            {
                if (dataCode is 2 or 4 or 9 or 10 or 11)
                    throw new NotSupportedException($"{who}: Pix# 0 with Data {dataCode} (per steradian): the help says \"sum of all data\" without saying whether the intensities are summed or the sum is divided by the detector's solid angle.");
                double s = 0; int n = 0;
                for (int a = 0; a < d.AzimuthPixels; a++)
                    for (int j = 0; j < d.PolarPixels; j++)
                    {
                        if (chroma && d.Luminous![d.Index(j, a)] == 0) continue;
                        s += Value(j, a); n++;
                    }
                if (!chroma) return s;
                return n == 0 ? throw new InvalidOperationException($"{who}: no pixel has non-zero lumens, so there is no average chromaticity.") : s / n;
            }
            case -1:
            case -2:
            {
                var values = new List<double>();
                for (int a = 0; a < d.AzimuthPixels; a++)
                    for (int j = 0; j < d.PolarPixels; j++)
                        if (!chroma || d.Luminous![d.Index(j, a)] != 0) values.Add(Value(j, a));
                if (values.Count == 0) throw new InvalidOperationException($"{who}: no pixel has non-zero lumens, so there is no chromaticity to compare.");
                return pixel == -1 ? values.Max() : values.Min();
            }
            case -4:
            {
                if (dataCode is not (1 or 3)) return 0;   // help: "All other inputs will return a value of zero."
                double sw = 0, s2 = 0;
                for (int a = 0; a < d.AzimuthPixels; a++)
                    for (int j = 0; j < d.PolarPixels; j++)
                    {
                        double w = Value(j, a), theta = j * d.Increment;
                        sw += w; s2 += w * theta * theta;
                    }
                if (!(sw > 0)) throw new InvalidOperationException($"{who}: the detector holds no {(dataCode == 1 ? "power" : "lumens")}, so there is no RMS angle.");
                return Math.Sqrt(s2 / sw);
            }
            default:
                throw new ArgumentException($"{who}: Pix# is {pixel}; it must be a pixel (1 to {d.PixelCount}) or 0 to −4.");
        }
    }

    // ================================================================ NSDC

    /// <summary>
    /// NSDC on a Detector Rectangle's coherent data: <paramref name="pixel"/> &gt; 0 that pixel, 0
    /// the sum over the pixels. Data 0 the real part ΣA cos φ, 1 the imaginary part ΣA sin φ, 2 the
    /// amplitude ΣA (A = √(ray flux)), 3 the power: the coherent irradiance (Conventions §73,
    /// Normalize Coherent Power as set) times the pixel area.
    /// </summary>
    public static double Nsdc(NscDetectorData data, int pixel, int dataCode)
    {
        ArgumentNullException.ThrowIfNull(data);
        string who = $"NSDC, detector object {data.ObjectNumber}";
        if (data is not NscFlatDetectorData { IsColor: false } d || d.CoherentReal is null || d.CoherentImaginary is null || d.CoherentAmplitude is null)
            throw new ArgumentException($"{who} is a {data.TypeName}; coherent data is recorded by Detector Rectangle objects only.");
        if (dataCode is < 0 or > 3) throw new ArgumentException($"{who}: Data is {dataCode}; it must be 0 (real), 1 (imaginary), 2 (amplitude) or 3 (power).");
        double[] values = dataCode switch
        {
            0 => d.CoherentReal,
            1 => d.CoherentImaginary,
            2 => d.CoherentAmplitude,
            _ => d.CoherentIrradiance().Select(v => v * d.PixelArea).ToArray(),
        };
        if (pixel > 0)
        {
            if (pixel > d.PixelCount) throw new ArgumentException($"{who}: pixel {pixel} does not exist; the detector has pixels 1 to {d.PixelCount}.");
            return values[pixel - 1];
        }
        if (pixel == 0) return values.Sum();
        throw new ArgumentException($"{who}: Pix# is {pixel}; the help defines a pixel (1 to {d.PixelCount}) or 0 (the sum).");
    }

    /// <summary>An empty data record of detector object <paramref name="number"/> of
    /// <paramref name="scene"/> (cleared detectors read zero), or null when it is not a detector.</summary>
    public static NscDetectorData? Empty(NscScene scene, int number, double airMicrometers)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (number < 1 || number > scene.Objects.Count || scene.Objects[number - 1].Geometry is not INscDetector detector) return null;
        return detector.CreateAccumulator(number, airMicrometers).Build(0);
    }
}
