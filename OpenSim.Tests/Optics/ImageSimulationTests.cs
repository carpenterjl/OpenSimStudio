using System.Numerics;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-42: the scene file readers and writers (IMA through OP-41, BIM, BMP, PNG; JPG refused), the
/// source transforms, the field-varying convolution's identities, the partially coherent sums'
/// limits, the geometric scene tracer, the analyses and their registration. Expected values are
/// derived in the comments. The gate is <c>Gates/ImageSimulationGateTests</c>. File layouts are
/// read from OpticStudio's 2025 R2 help ("Geometric Image Analysis"), the W3C PNG specification
/// (third edition) and Microsoft's BITMAPINFOHEADER pages, saved under
/// plans/sources/optics/op-42-help/.
/// </summary>
public sealed class ImageSimulationTests
{
    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    internal static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, SurfaceShape? shape = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, Shape = shape,
    };

    internal static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    /// <summary>An ideal lens of focal length f imaging an object a in front of it at
    /// z = 1/(1/f − 1/a) behind (OP-27's paraxial surface, mode 3 at those conjugates), so the
    /// magnification is −z/a.</summary>
    internal static (SequentialLens Lens, double Magnification) FiniteIdealLens(double f, double a)
    {
        double z = 1 / (1 / f - 1 / a);
        return (Lens(a, S(double.PositiveInfinity, z, stop: true, shape: new ParaxialShape(f, -a))), -z / a);
    }

    internal static OpticalSystemData HeightSystem(double epd, double[] heights, params double[] wavelengths) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet { Type = FieldType.ObjectHeight, Points = heights.Select(h => new FieldPoint { Y = h }).ToArray() },
        Wavelengths = new WavelengthSet { Wavelengths = wavelengths.Select(w => new Wavelength { Micrometers = w }).ToArray() },
    };

    internal static OpticalSystemData AngleSystem(double epd, double[] angles, params double[] wavelengths) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet { Type = FieldType.Angle, Points = angles.Select(h => new FieldPoint { Y = h }).ToArray() },
        Wavelengths = new WavelengthSet { Wavelengths = wavelengths.Select(w => new Wavelength { Micrometers = w }).ToArray() },
    };

    internal static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    private static double[,] Random(int rows, int columns, int seed)
    {
        var rng = new System.Random(seed);
        var m = new double[rows, columns];
        for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) m[r, c] = rng.NextDouble();
        return m;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-op42-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Files ----

    [Fact]
    public void Bim_RoundTrips_BottomRowFirst_AndRefusesNonSquareFiles()
    {
        // The help: "The first pixel is the bottom left corner, and the remaining pixels are
        // listed by rows along x"; so the first double is row H − 1, column 0 of the map.
        var map = new double[,] { { 1, 2 }, { 3, 4.5 } };
        var bytes = BimFile.Format(map);
        Assert.Equal(8 + 4 * 8, bytes.Length);
        Assert.Equal(3.0, BitConverter.ToDouble(bytes, 8));
        Assert.Equal(4.5, BitConverter.ToDouble(bytes, 16));
        var back = BimFile.Parse(bytes, "t.bim");
        Assert.Equal(SceneFileKind.Bim, back.Kind);
        Assert.Equal(map, back.Channels[0]);
        var odd = new byte[8 + 6 * 8];
        BitConverter.GetBytes(3).CopyTo(odd, 0);
        BitConverter.GetBytes(2).CopyTo(odd, 4);
        Assert.Throws<FormatException>(() => BimFile.Parse(odd, "odd.bim"));
    }

    [Fact]
    public void Bmp_RoundTrips24Bit_ReadsTopDownAnd32Bit_AndRefusesPalettes()
    {
        byte[,] R = { { 1, 2, 3 }, { 4, 5, 6 } }, G = { { 10, 20, 30 }, { 40, 50, 60 } }, B = { { 100, 110, 120 }, { 130, 140, 250 } };
        var bytes = BmpFile.Format(R, G, B);
        // Width 3 × 3 bytes = 9, stride rounded up to 12 (((3·24 + 31) & ~31) >> 3).
        Assert.Equal(54 + 12 * 2, bytes.Length);
        // Bottom-up: the first stored line is the bottom row, its first pixel blue, green, red.
        Assert.Equal(new byte[] { 130, 40, 4 }, bytes[54..57]);
        var scene = BmpFile.Parse(bytes, "t.bmp");
        Assert.True(scene.IsRgb);
        Assert.Equal(6, scene.Channels[0][1, 2]);
        Assert.Equal(20, scene.Channels[1][0, 1]);
        Assert.Equal(250, scene.Channels[2][1, 2]);

        // A 32-bit top-down bitmap of one row: negative biHeight, 4 bytes a pixel.
        var top = new byte[54 + 8];
        top[0] = 0x42; top[1] = 0x4D;
        BitConverter.GetBytes(54).CopyTo(top, 10);
        BitConverter.GetBytes(40).CopyTo(top, 14);
        BitConverter.GetBytes(2).CopyTo(top, 18);
        BitConverter.GetBytes(-1).CopyTo(top, 22);
        BitConverter.GetBytes((short)1).CopyTo(top, 26);
        BitConverter.GetBytes((short)32).CopyTo(top, 28);
        new byte[] { 7, 8, 9, 0, 17, 18, 19, 0 }.CopyTo(top, 54);
        var t = BmpFile.Parse(top, "top.bmp");
        Assert.Equal(9, t.Channels[0][0, 0]);
        Assert.Equal(17, t.Channels[2][0, 1]);

        var palette = (byte[])bytes.Clone();
        BitConverter.GetBytes((short)8).CopyTo(palette, 28);
        Assert.Contains("8 bits", Assert.Throws<FormatException>(() => BmpFile.Parse(palette, "p.bmp")).Message);
    }

    /// <summary>A PNG whose lines use each of the five filter types, encoded here by the
    /// specification's filter functions (Table 11, PaethPredictor of §9.4), decodes to the
    /// original bytes.</summary>
    [Fact]
    public void Png_DecodesEveryFilterType_AndRoundTrips()
    {
        const int w = 4, h = 5, samples = 3;
        var rng = new System.Random(7);
        var pixels = new byte[h, w * samples];
        for (int r = 0; r < h; r++) for (int i = 0; i < w * samples; i++) pixels[r, i] = (byte)rng.Next(256);
        var raw = new List<byte>();
        for (int r = 0; r < h; r++)
        {
            int filter = r % 5;
            raw.Add((byte)filter);
            for (int i = 0; i < w * samples; i++)
            {
                int x = pixels[r, i];
                int a = i >= samples ? pixels[r, i - samples] : 0;
                int b = r > 0 ? pixels[r - 1, i] : 0;
                int c = r > 0 && i >= samples ? pixels[r - 1, i - samples] : 0;
                int p = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => PngFile.Paeth(a, b, c) };
                raw.Add((byte)(x - p));
            }
        }
        var png = BuildPng(w, h, 2, raw.ToArray());
        var scene = PngFile.Parse(png, "f.png");
        Assert.True(scene.IsRgb);
        for (int r = 0; r < h; r++)
            for (int col = 0; col < w; col++)
                for (int m = 0; m < 3; m++) Assert.Equal(pixels[r, col * 3 + m], scene.Channels[m][r, col]);

        // The writer's output reads back; a damaged CRC is refused; interlacing is refused.
        var grey = new byte[,] { { 0, 128 }, { 255, 7 } };
        var back = PngFile.Parse(PngFile.Format(new[] { grey }), "g.png");
        Assert.Single(back.Channels);
        Assert.Equal(255, back.Channels[0][1, 0]);
        var damaged = (byte[])png.Clone();
        damaged[20] ^= 1;
        Assert.Contains("CRC", Assert.Throws<FormatException>(() => PngFile.Parse(damaged, "d.png")).Message);
        var interlaced = BuildPng(w, h, 2, raw.ToArray(), interlace: 1);
        Assert.Contains("interlaced", Assert.Throws<FormatException>(() => PngFile.Parse(interlaced, "i.png")).Message);
    }

    private static byte[] BuildPng(int w, int h, int colour, byte[] raw, int interlace = 0)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len);
            ms.Write(len);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body);
            var crc = BitConverter.GetBytes(PngFile.Crc(body)); Array.Reverse(crc);
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        var bw = BitConverter.GetBytes(w); Array.Reverse(bw); bw.CopyTo(ihdr, 0);
        var bh = BitConverter.GetBytes(h); Array.Reverse(bh); bh.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = (byte)colour; ihdr[12] = (byte)interlace;
        Chunk("IHDR", ihdr);
        var z = new MemoryStream();
        using (var zl = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zl.Write(raw);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>The CRC of the four bytes "IEND" is AE 42 60 82, the trailer every PNG ends
    /// with (W3C PNG specification, 11.2.4 and Annex D's algorithm; the bytes are those of the
    /// empty IEND chunk in the specification's own datastream layout).</summary>
    [Fact]
    public void PngCrc_OfIend_IsTheStandardTrailer()
    {
        // Derived independently by the bitwise CRC-32 (reflected polynomial 0xedb88320 of Annex D,
        // one bit at a time instead of the table).
        uint crc = 0xffffffffu;
        foreach (byte b in Encoding.ASCII.GetBytes("IEND"))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        Assert.Equal(crc ^ 0xffffffffu, PngFile.Crc(Encoding.ASCII.GetBytes("IEND")));
    }

    [Fact]
    public void SceneFile_RefusesJpgByName_AndOtherFormats()
    {
        Assert.Contains("JPG", Assert.Throws<ArgumentException>(() => SceneFile.Read(@"C:\x\photo.jpg", SceneFileUse.Any, "T")).Message);
        Assert.Contains("JPG", Assert.Throws<ArgumentException>(() => SceneFile.Read(@"C:\x\photo.JPEG", SceneFileUse.Bitmap, "T")).Message);
        Assert.Throws<ArgumentException>(() => SceneFile.Read(@"C:\x\a.tif", SceneFileUse.Any, "T"));
        Assert.Throws<ArgumentException>(() => SceneFile.Read(@"C:\x\a.bmp", SceneFileUse.ImaOrBim, "T"));
        Assert.Throws<ArgumentException>(() => SceneFile.Read(@"C:\x\a.ima", SceneFileUse.Bitmap, "T"));
        Assert.Throws<ArgumentException>(() => SceneFile.Read("", SceneFileUse.Any, "T"));
        Assert.Throws<ArgumentException>(() => SceneFile.WriteBitmap(@"C:\x\out.jpg", new[] { new double[2, 2] }, 1, "T"));
    }

    // ---- Transforms ----

    [Fact]
    public void Transforms_FollowTheHelp()
    {
        var map = new double[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        // Oversampling copies each pixel into n × n.
        var o = SceneTransforms.Oversample(map, 2);
        Assert.Equal(4, o.GetLength(0));
        Assert.Equal(6, o.GetLength(1));
        Assert.Equal(5, o[3, 2]);
        Assert.Equal(5, o[2, 3]);
        // Zero padding of the help: 7 → 13 (2n − 1 for odd n), 6 → 12.
        Assert.Equal(13, SceneTransforms.ZeroPad(new double[7, 7], 2).GetLength(0));
        Assert.Equal(12, SceneTransforms.ZeroPad(new double[6, 6], 2).GetLength(0));
        var padded = SceneTransforms.ZeroPad(new double[,] { { 0, 0, 0 }, { 0, 9, 0 }, { 0, 0, 0 } }, 2);
        Assert.Equal(9, padded[2, 2]); // the centre pixel stays the centre
        // Quarter turns counter-clockwise: the top-right corner goes to the top-left.
        var r90 = SceneTransforms.Rotate(map, SceneRotation.Rotate90);
        Assert.Equal(3, r90[0, 0]);
        Assert.Equal(4, r90[2, 1]);
        Assert.Equal(map, SceneTransforms.Rotate(r90, SceneRotation.Rotate270));
        Assert.Equal(SceneTransforms.Flip(map, SceneFlip.Both), SceneTransforms.Rotate(map, SceneRotation.Rotate180));
        Assert.Equal(6, SceneTransforms.Flip(map, SceneFlip.Both)[0, 0]);
        Assert.Equal(3, SceneTransforms.Flip(map, SceneFlip.LeftRight)[0, 0]);
        Assert.Equal(4, SceneTransforms.Flip(map, SceneFlip.TopBottom)[0, 0]);
        // The guard band centres the picture on a black canvas n times as large.
        var g = SceneTransforms.GuardBand(SceneImage.Grey(map), 2);
        Assert.Equal(4, g.Height);
        Assert.Equal(6, g.Width);
        Assert.Equal(1, g.Channels[0][1, 1]);
        Assert.Equal(0, g.Channels[0][0, 0]);
        Assert.Throws<ArgumentException>(() => SceneTransforms.CheckPowerOfTwo(3, "T", "oversampling"));
    }

    // ---- The field-varying convolution ----

    /// <summary>
    /// The decomposition B = Σ_g (t_g·S) ⊛ K_g is the per-pixel interpolated PSF exactly:
    /// B(q) = Σ_p S(p)·Σ_g t_g(p)·K_g(q − p). Here the right side is summed directly, pixel by
    /// pixel, with different asymmetric kernels at every node, and the two agree to rounding
    /// (the FFT's error, ~1e-15 per term, over ~10³ terms).
    /// </summary>
    [Fact]
    public void TheOverlapAdd_EqualsThePerPixelInterpolatedKernel()
    {
        const int h = 13, w = 17, gx = 3, gy = 4;
        var source = Random(h, w, 1);
        var kernels = new PixelKernel[gx * gy];
        for (int g = 0; g < gx * gy; g++)
        {
            var k = Random(3 + g % 3, 2 + g % 4, 100 + g);
            kernels[g] = new PixelKernel(k, g % 2, (g + 1) % 2);
        }
        var fast = SpatiallyVariantConvolution.Apply(source, gx, gy, (a, b) => kernels[b * gx + a], maxDegreeOfParallelism: 3);
        var direct = new double[h, w];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                for (int g = 0; g < gx * gy; g++)
                {
                    double t = SpatiallyVariantConvolution.Tent(g % gx, gx, w, c) * SpatiallyVariantConvolution.Tent(g / gx, gy, h, r);
                    if (t == 0) continue;
                    var k = kernels[g];
                    for (int i = 0; i < k.Rows; i++)
                        for (int j = 0; j < k.Columns; j++)
                        {
                            int y = r + i - k.CentreRow, x = c + j - k.CentreColumn;
                            if (y >= 0 && y < h && x >= 0 && x < w) direct[y, x] += source[r, c] * t * k.Weights[i, j];
                        }
                }
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) Close(direct[r, c], fast[r, c], 1e-12, $"pixel ({r}, {c})");
        // The tents are a partition of unity over the picture.
        for (int c = 0; c < w; c++) Close(1, Enumerable.Range(0, gx).Sum(a => SpatiallyVariantConvolution.Tent(a, gx, w, c)), 1e-15, "tents");
        // Bitwise the same on one thread.
        var serial = SpatiallyVariantConvolution.Apply(source, gx, gy, (a, b) => kernels[b * gx + a], maxDegreeOfParallelism: 1);
        Assert.Equal(fast, serial);
    }

    // ---- Partially coherent sums ----

    private static Complex[,] Disk(int n, double radius) =>
        DiffractionImaging.Lattice(n, 1, (fx, fy) => fx * fx + fy * fy <= radius * radius / ((double)n * n) ? Complex.One : Complex.Zero);

    /// <summary>
    /// The limits derived in <see cref="DiffractionImaging"/>: γ ≡ 1 gives the coherent image
    /// exactly (by both sums), γ = δ gives (1/N²)·Σ|H|² times the incoherent image, and the
    /// Abbe and separation sums agree for a Gaussian γ with every term kept. All are identities on
    /// the periodic grid, so only rounding separates them (N² terms of ~1e-16).
    /// </summary>
    [Fact]
    public void PartialCoherence_ReachesItsLimits_AndBothSumsAgree()
    {
        const int n = 16;
        var obj = Random(n, n, 3);
        var h = Disk(n, 4.3);
        var ones = new double[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) ones[r, c] = 1;
        var a = new double[n, n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) a[r, c] = Math.Sqrt(obj[r, c]);
        var coherent = DiffractionImaging.Intensity(DiffractionImaging.CoherentAmplitude(a, h));
        foreach (var method in new[] { PartialCoherenceMethod.MostlyCoherent, PartialCoherenceMethod.MostlyIncoherent })
        {
            var (img, _, _) = DiffractionImaging.PartiallyCoherent(obj, h, ones, method, 1);
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) Close(coherent[r, c], img[r, c], 1e-11, $"γ ≡ 1 ({method}) at ({r}, {c})");
        }
        var delta = new double[n, n];
        delta[0, 0] = 1;
        var incoherent = DiffractionImaging.Incoherent(obj, h);
        double sumH = 0;
        foreach (var z in h) sumH += z.Magnitude * z.Magnitude;
        double factor = sumH / (n * n);
        foreach (var method in new[] { PartialCoherenceMethod.MostlyCoherent, PartialCoherenceMethod.MostlyIncoherent })
        {
            var (img, _, _) = DiffractionImaging.PartiallyCoherent(obj, h, delta, method, 1);
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) Close(factor * incoherent[r, c], img[r, c], 1e-11, $"γ = δ ({method}) at ({r}, {c})");
        }
        var gamma = DiffractionImaging.GammaLattice(n, 1, CoherenceForm.Gaussian, 2.5);
        var abbe = DiffractionImaging.PartiallyCoherent(obj, h, gamma, PartialCoherenceMethod.MostlyCoherent, 1).Image;
        var sep = DiffractionImaging.PartiallyCoherent(obj, h, gamma, PartialCoherenceMethod.MostlyIncoherent, 1).Image;
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) Close(abbe[r, c], sep[r, c], 1e-10, $"Abbe against separations at ({r}, {c})");
        // A fraction below 1 keeps fewer terms.
        Assert.True(DiffractionImaging.PartiallyCoherent(obj, h, gamma, PartialCoherenceMethod.MostlyCoherent, 0.9).Terms < n * n);
    }

    /// <summary>The object transform of luminous squares one grid pitch wide, one per sample,
    /// is the DFT of the samples times sinc(fx·d)·sinc(fy·d), and of delta functions the DFT
    /// itself (derived in <see cref="DiffractionImaging.ObjectSpectrum"/>).</summary>
    [Fact]
    public void TheObjectSpectrum_OfSamples_IsTheirDft()
    {
        const int n = 8;
        const double d = 2e-6;
        var v = Random(n, n, 5);
        var dft = OpenSim.Core.Numerics.Fft2D.Forward(PartiallyCoherentImagingAccess.ToComplex(v));
        var deltas = DiffractionImaging.ObjectSpectrum(v, d, 0, 0, n, d, deltas: true);
        var squares = DiffractionImaging.ObjectSpectrum(v, d, 0, 0, n, d, deltas: false);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                Close(0, (deltas[r, c] - dft[r, c]).Magnitude, 1e-12, $"deltas ({r}, {c})");
                double s = DiffractionImaging.Sinc(DiffractionImaging.Signed(r, n) / (double)n) * DiffractionImaging.Sinc(DiffractionImaging.Signed(c, n) / (double)n);
                Close(0, (squares[r, c] - s * dft[r, c]).Magnitude, 1e-12, $"squares ({r}, {c})");
            }
    }

    [Fact]
    public void TheMtfEstimate_OfACosine_IsItsModulation()
    {
        // y = 1 + 0.5·cos(2πx/8) sampled 8 to a period over 6 periods: peaks of 1.5 and troughs
        // of 0.5 are sampled exactly, so (Imax − Imin)/(Imax + Imin) = 1/2.
        var y = Enumerable.Range(0, 49).Select(i => 1 + 0.5 * Math.Cos(2 * Math.PI * i / 8)).ToArray();
        Close(0.5, ExtendedSceneSupport.MtfEstimate(y), 1e-12, "modulation");
        Assert.True(double.IsNaN(ExtendedSceneSupport.MtfEstimate(new double[] { 0, 1, 0, 1, 0 })));
    }

    // ---- The geometric scene ----

    /// <summary>
    /// Two lit pixels of a 2 × 2 IMA picture (top-left 1, bottom-right 3) through an ideal lens of
    /// magnification m = −1/3: every ray lands inside the image of its pixel, the square
    /// m·(pixel), and the light divides 1 : 3. Turning the object 90° counter-clockwise moves the
    /// top-left pixel's centre (−δ/2, +δ/2) to (−δ/2, −δ/2). The ray positions in each pixel come
    /// from the Sobol sequence, whose mean over 2000 points of a (0, 1)-sequence is within
    /// (log₂N + 1)/N ≈ 0.006 of ½ per axis; 0.02·δ·|m| allows it.
    /// </summary>
    [Fact]
    public void TheSceneTracer_ImagesEachPixelOntoItsSquare_AndTurnsTheObject()
    {
        var (lens, m) = FiniteIdealLens(0.05, 0.2);
        var system = HeightSystem(0.01, new[] { 0.0, 0.002 }, 0.55);
        const double delta = 1e-4;
        var scene = SceneImage.Grey(new double[,] { { 1, 0 }, { 0, 3 } });
        foreach (double rotation in new[] { 0.0, 90.0 })
        {
            var result = GeometricSceneTracer.Trace(lens, system, new GeometricSceneRequest
            {
                Scene = scene, Field = 0, PixelField = delta, Channels = new[] { new TracedChannel(0, 0, 1, "g") }, TotalRays = 8000,
                RotationDegrees = rotation, Surface = lens.ImageIndex,
            }, "T");
            Assert.Equal(8000, result.RaysLaunched);
            Assert.Equal(8000, result.Hits.Count);
            Close(4, result.Hits.Sum(x => x.Weight), 1e-12, "light carried (value × weight summed)");
            var (cos, sin) = (Math.Cos(rotation * Math.PI / 180), Math.Sin(rotation * Math.PI / 180));
            (double X, double Y) Turn(double u, double v) => (u * cos - v * sin, u * sin + v * cos);
            var topLeft = Turn(-delta / 2, delta / 2);
            var bottomRight = Turn(delta / 2, -delta / 2);
            var first = result.Hits.Where(x => Math.Abs(x.X - m * topLeft.X) < Math.Abs(m) * delta / 2 * (1 + 1e-9)
                && Math.Abs(x.Y - m * topLeft.Y) < Math.Abs(m) * delta / 2 * (1 + 1e-9)).ToList();
            var second = result.Hits.Where(x => Math.Abs(x.X - m * bottomRight.X) < Math.Abs(m) * delta / 2 * (1 + 1e-9)
                && Math.Abs(x.Y - m * bottomRight.Y) < Math.Abs(m) * delta / 2 * (1 + 1e-9)).ToList();
            Assert.Equal(8000, first.Count + second.Count);
            Close(1, first.Sum(x => x.Weight), 1e-12, "the value-1 pixel's light");
            Close(m * topLeft.X, first.Average(x => x.X), 0.02 * delta * Math.Abs(m), "centroid x of the top-left pixel");
            Close(m * topLeft.Y, first.Average(x => x.Y), 0.02 * delta * Math.Abs(m), "centroid y of the top-left pixel");
        }
    }

    /// <summary>The parallel paths (the scene tracer's rows, the simulator's convolution and
    /// detector rows, the partially coherent term batches) give bitwise the same result on one
    /// thread and on many: each slot is computed alone and the slots are added in order.</summary>
    [Fact]
    public void TheParallelPaths_AreBitwiseTheSameAtAnyThreadCount()
    {
        // A singlet with real aberrations, so the geometric kernels are not deltas.
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));
        var system = AngleSystem(0.012, new[] { 0.0, 2.0 }, 0.55);
        var picture = Random(9, 12, 21);
        ImageSimulationResult Sim(int? dop) => ImageSimulator.Simulate(lens, system, new ImageSimulationRequest
        {
            Scene = SceneImage.Grey(picture), Field = 0, FieldHeight = 1.0, Channels = new[] { new SimulationChannel(0, 0, 1, "g") },
            Aberrations = ImageSimulationAberrations.Geometric, PupilSampling = 12, PsfPointsX = 3, PsfPointsY = 2, MaxDegreeOfParallelism = dop,
        });
        Assert.Equal(Sim(1).Images[0], Sim(null).Images[0]);

        GeometricSceneResult Trace(int? dop) => GeometricSceneTracer.Trace(lens, system, new GeometricSceneRequest
        {
            Scene = SceneImage.Grey(picture), Field = 1, PixelField = 0.05, Channels = new[] { new TracedChannel(0, 0, 1, "g") }, RaysPerPixel = 3,
            Surface = lens.ImageIndex, MaxDegreeOfParallelism = dop,
        }, "T");
        Assert.Equal(Trace(1).Hits, Trace(null).Hits);

        var obj = Random(16, 16, 4);
        var h = Disk(16, 3.7);
        var gamma = DiffractionImaging.GammaLattice(16, 1, CoherenceForm.Sinc, 3);
        Assert.Equal(DiffractionImaging.PartiallyCoherent(obj, h, gamma, PartialCoherenceMethod.MostlyCoherent, 1, 1).Image,
            DiffractionImaging.PartiallyCoherent(obj, h, gamma, PartialCoherenceMethod.MostlyCoherent, 1, null).Image);
    }

    /// <summary>
    /// The diffraction kernels (OP-38's FFT and Huygens PSFs) of an ideal lens at F/10, λ = 0.5 µm
    /// (λF = 5 µm), on source pixels 0.2° wide (f·tan 0.2° ≈ 349 µm in the image): the PSF window,
    /// at most ±max(8λF, 3·RMS + 4λF) = ±40 µm here (the RMS spot of a perfect lens is zero), lies
    /// inside one pixel's image, so every kernel is the delta exactly.
    /// </summary>
    [Theory]
    [InlineData(DiffractionPsfEngine.Fft)]
    [InlineData(DiffractionPsfEngine.Huygens)]
    public void DiffractionKernels_SmallerThanAPixel_AreDeltas(DiffractionPsfEngine engine)
    {
        const double f = 0.1;
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = AngleSystem(0.01, new[] { 0.0, 1.0 }, 0.5);
        var picture = Random(4, 5, 8);
        var result = ImageSimulator.Simulate(lens, system, new ImageSimulationRequest
        {
            Scene = SceneImage.Grey(picture), Field = 0, FieldHeight = 0.8, Channels = new[] { new SimulationChannel(0, 0, 1, "g") },
            Aberrations = ImageSimulationAberrations.Diffraction, Engine = engine, PupilSampling = 32, ImageSampling = 16, PsfPointsX = 2, PsfPointsY = 2,
        });
        foreach (var k in result.Kernels[0])
        {
            Assert.Equal(1, k.Rows);
            Assert.Equal(1, k.Columns);
            Close(1, k.Weights[0, 0], 1e-12, "the whole PSF in the pixel");
        }
        // OP-38's Huygens engine warns, rightly, that its planar phase reference is off by more
        // than λ/4 across the image window at the off-axis grid points; that is the PSF engine's
        // own note and does not touch the delta kernels. Nothing else may warn.
        var unexpected = result.Warnings.Where(w => !(engine == DiffractionPsfEngine.Huygens && w.Contains("spherical phase reference"))).ToList();
        Assert.True(unexpected.Count == 0, string.Join(" | ", unexpected));
    }

    // ---- The analyses ----

    [Fact]
    public void TheAnalyses_AreRegistered_AndLightSourceAnalysisIsNot()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[] { ImageSimulationAnalysis.SavedId, GeometricImageAnalysis.SavedId, GeometricBitmapImageAnalysis.SavedId,
                     PartiallyCoherentImageAnalysis.SavedId, ExtendedDiffractionImageAnalysis.SavedId, ImaBimFileViewerAnalysis.SavedId, BitmapFileViewerAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var a), id);
            Assert.Equal("Extended scene", a.Category);
            var defaults = a.CreateDefaultSettings();
            Assert.Equal(defaults, OpticsAnalysisSettingsCodec.Clone(a, defaults));
        }
        Assert.DoesNotContain(registry.All, a => a.Name.Contains("Light source", StringComparison.OrdinalIgnoreCase));
        Assert.True(ExtendedSceneNotBuilt.Analyses.ContainsKey("Light Source Analysis"));
    }

    [Fact]
    public void TheViewers_ShowTheFiles()
    {
        string dir = TempDir();
        try
        {
            string ima = Path.Combine(dir, "f.ima");
            File.WriteAllText(ima, "3\n110\n100\n104\n");
            var context = Context(HeightSystem(0.01, new[] { 0.0 }, 0.55));
            var view = new ImaBimFileViewerAnalysis().Compute(context, new ImaBimFileViewerSettings { File = ima }, default);
            var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(view.Plots));
            // Normalised to the largest value 4; row 1 of the file is the top (the plot's last row).
            Close(0.25, map.Field.Values[2, 0], 0, "top-left");
            Close(1, map.Field.Values[0, 2], 0, "bottom-right");

            string bmp = Path.Combine(dir, "c.bmp");
            File.WriteAllBytes(bmp, BmpFile.Format(new byte[,] { { 1, 2 } }, new byte[,] { { 3, 4 } }, new byte[,] { { 5, 6 } }));
            var bitmap = new BitmapFileViewerAnalysis().Compute(context, new BitmapFileViewerSettings { File = bmp }, default);
            Assert.Equal(3, bitmap.Plots.Count);
            Close(6, Assert.IsType<AnalysisMapPlot>(bitmap.Plots[2]).Field.Values[0, 1], 0, "blue of the second pixel");
            Assert.Contains("JPG", Assert.Throws<ArgumentException>(() =>
                new BitmapFileViewerAnalysis().Compute(context, new BitmapFileViewerSettings { File = Path.Combine(dir, "x.jpg") }, default)).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The Geometric Image Analysis of a one-pixel source through the ideal lens: every
    /// ray reaches a detector wider than the pixel's image, so the efficiency is 100 % and the
    /// pixels hold the total watts; polarization and scattering are refused by name.</summary>
    [Fact]
    public void GeometricImageAnalysis_CountsEveryRay_OnAWideDetector()
    {
        var (lens, _) = FiniteIdealLens(0.05, 0.2);
        var system = HeightSystem(0.01, new[] { 0.0, 0.002 }, 0.55);
        var context = Context(system);
        string dir = TempDir();
        try
        {
            string ima = Path.Combine(dir, "dot.ima");
            File.WriteAllText(ima, "1\n9\n");
            var analysis = new GeometricImageAnalysis(_ => lens);
            var settings = new GeometricImageSettings { File = ima, FieldSize = 0.1, ImageSize = 0.1, Pixels = 20, RaysThousands = 1, TotalWatts = 2 };
            var result = analysis.Compute(context, settings, default);
            Assert.Contains("100 %", result.Text);
            var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots));
            double sum = 0;
            foreach (var v in map.Field.Values) sum += v;
            Close(2, sum, 1e-12, "total watts on the detector");
            Assert.Contains("polarization", Assert.Throws<ArgumentException>(() => analysis.Compute(context, settings with { UsePolarization = true }, default)).Message);
            Assert.Contains("scattering", Assert.Throws<ArgumentException>(() => analysis.Compute(context, settings with { ScatterRays = true }, default)).Message);
            string bim = Path.Combine(dir, "out.bim");
            analysis.Compute(context, settings with { SaveAsBimFile = bim }, default);
            var back = BimFile.Read(bim);
            Assert.Equal(20, back.Width);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The Geometric Bitmap Image Analysis's channel rules (help): RGB adds 0.656, 0.587,
    /// 0.486 µm; 1+2+3 with fewer than three wavelengths reverts to RGB; a single wavelength k uses
    /// blue for 1, green for 2 and red for 3 and above.</summary>
    [Fact]
    public void GeometricBitmap_MapsChannelsToWavelengths_AsTheHelpSays()
    {
        var system = HeightSystem(0.01, new[] { 0.0 }, 0.55, 0.6);
        var scene = SceneImage.Rgb(new double[1, 1], new double[1, 1], new double[1, 1]);
        var notes = new List<string>();
        var (sys, channels) = GeometricBitmapImageAnalysis.Channels(system, scene, new GeometricBitmapImageSettings { Wavelengths = SceneWavelengths.Wavelengths123 }, notes);
        Assert.Contains(notes, n => n.Contains("reverts to RGB"));
        Assert.Equal(new[] { 0.656, 0.587, 0.486 }, channels.Select(c => sys.Wavelengths.Wavelengths[c.Wavelength].Micrometers));
        Assert.Equal(new[] { 0, 1, 2 }, channels.Select(c => c.SceneChannel));
        int Single(int k) => GeometricBitmapImageAnalysis.Channels(HeightSystem(0.01, new[] { 0.0 }, 0.5, 0.55, 0.6, 0.65), scene,
            new GeometricBitmapImageSettings { Wavelengths = SceneWavelengths.Single, WavelengthNumber = k }, new List<string>()).Channels[0].SceneChannel;
        Assert.Equal(2, Single(1));
        Assert.Equal(1, Single(2));
        Assert.Equal(0, Single(3));
        Assert.Equal(0, Single(4));
        // The Image Simulation's rule differs: blue for 4, 7, …
        Assert.Equal(2, ExtendedSceneSupport.ChannelOf(4, redAboveThree: false));
        Assert.Equal(1, ExtendedSceneSupport.ChannelOf(5, redAboveThree: false));
        Assert.Equal(0, ExtendedSceneSupport.ChannelOf(6, redAboveThree: false));
    }

    /// <summary>
    /// The Geometric Bitmap Image Analysis of a 2 × 2 RGB bitmap through the ideal lens (m = −1/3)
    /// on a detector whose pixels are the source pixels' images: every ray of a source pixel lands
    /// in the detector pixel turned 180° from it (an inverted image), so with peak normalisation the
    /// detected image is the source turned 180° with its largest value 255, the source's own
    /// (each pixel's rays, of weight value/count, all land in one detector pixel, so each detector
    /// pixel sums to its source value up to rounding).
    /// </summary>
    [Fact]
    public void GeometricBitmap_ThroughAnIdealLens_IsTheSourceTurned180Degrees()
    {
        var (lens, m) = FiniteIdealLens(0.05, 0.2);
        var system = HeightSystem(0.01, new[] { 0.0, 0.002 }, 0.55);
        var context = Context(system);
        string dir = TempDir();
        try
        {
            byte[,] r = { { 255, 0 }, { 0, 100 } }, g = { { 0, 50 }, { 0, 0 } }, b = { { 0, 0 }, { 200, 0 } };
            string file = Path.Combine(dir, "s.png");
            File.WriteAllBytes(file, PngFile.Format(new[] { r, g, b }));
            double fieldY = 0.2; // lens units (mm): δ = 0.1 mm a pixel
            double pixel = OpticsConventions.FromMeters(Math.Abs(m) * OpticsConventions.ToMeters(fieldY / 2, system.Units.LensUnit), system.Units.LensUnit);
            var result = new GeometricBitmapImageAnalysis(_ => lens).Compute(context, new GeometricBitmapImageSettings
            {
                File = file, FieldYSize = fieldY, RaysPerPixel = 50, XPixels = 2, YPixels = 2, XPixelSize = pixel, YPixelSize = pixel,
                Normalize = BitmapNormalization.Peak, ApplyFixedApertures = false,
            }, default);
            Assert.Equal(3, result.Plots.Count);
            var red = Assert.IsType<AnalysisMapPlot>(result.Plots[0]).Field.Values;
            // Detector pixel [i, j] holds source pixel [1 − i, 1 − j]; the plot's rows run bottom to
            // top (plot [r, c] = detector [1 − r, c]), so plot [r, c] = source [r, 1 − c].
            Close(255, red[0, 1], 1e-9, "plot (0, 1) = source top-left");
            Close(100, red[1, 0], 1e-9, "plot (1, 0) = source bottom-right");
            Close(0, red[0, 0], 0, "plot (0, 0) = source top-right");
            var blue = Assert.IsType<AnalysisMapPlot>(result.Plots[2]).Field.Values;
            Close(200, blue[1, 1], 1e-9, "plot (1, 1) = source bottom-left");
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Test access to an internal helper.</summary>
internal static class PartiallyCoherentImagingAccess
{
    public static Complex[,] ToComplex(double[,] x) => PartiallyCoherentImaging.ToComplex(x);
}
