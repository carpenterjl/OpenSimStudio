using System.Buffers.Binary;
using System.IO.Compression;
using OpenSim.Optics.Analysis.EnclosedEnergy;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>
/// OpticStudio's BIM files (help, "Geometric Image Analysis", "The BIM format"): "1 32 bit
/// integer representing the number of x pixels, nx. 1 32 bit integer representing the number of y
/// pixels, ny. followed by n*n 64 bit double precision floating point values representing the
/// relative intensity. The first pixel is the bottom left corner, and the remaining pixels are
/// listed by rows along x"; "the nx and ny values must be identical or an error message will be
/// issued". The help gives no byte order; little-endian (the PC's) is read and written, as OP-41
/// reads the binary IMA.
/// </summary>
public static class BimFile
{
    /// <summary>The largest width, the help's limit for IMA and BIM inputs of the Image
    /// Simulation ("IMA abd BIM files must have more than 1 but no more than 8,000 pixels").</summary>
    public const int MaximumWidth = 8000;

    public static SceneImage Read(string path)
    {
        SceneFile.CheckPath(path, "BIM file");
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static SceneImage Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 8) throw new FormatException($"BIM file {name}: the 8-byte header (nx, ny) is missing ({bytes.Length} bytes).");
        int nx = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0));
        int ny = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (nx != ny) throw new FormatException($"BIM file {name}: nx = {nx} and ny = {ny}; the help: \"the nx and ny values must be identical\".");
        if (nx < 1 || nx > MaximumWidth) throw new FormatException($"BIM file {name}: the width is {nx}; 1 to {MaximumWidth} pixels.");
        long expected = 8 + 8L * nx * ny;
        if (bytes.Length < expected) throw new FormatException($"BIM file {name}: {nx} × {ny} doubles need {expected} bytes; the file has {bytes.Length}.");
        var map = new double[ny, nx];
        int offset = 8;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++, offset += 8)
            {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset));
                if (!double.IsFinite(v) || v < 0)
                    throw new FormatException($"BIM file {name}: pixel x {i + 1}, y {j + 1} (from the bottom left) is {v}; intensities must be finite and not negative.");
                map[ny - 1 - j, i] = v; // the file starts at the bottom row
            }
        return SceneImage.Grey(map, SceneFileKind.Bim);
    }

    /// <summary>The bytes of a BIM file holding <paramref name="map"/> ([row, column], row 0 the top).</summary>
    public static byte[] Format(double[,] map)
    {
        ArgumentNullException.ThrowIfNull(map);
        int ny = map.GetLength(0), nx = map.GetLength(1);
        if (nx != ny) throw new ArgumentException($"BIM file: the map must be square (it is {ny} × {nx}); the help: \"the nx and ny values must be identical\".");
        var bytes = new byte[8 + 8L * nx * ny];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), nx);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), ny);
        int offset = 8;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++, offset += 8)
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset), map[ny - 1 - j, i]);
        return bytes;
    }

    public static void Write(string path, double[,] map)
    {
        SceneFile.CheckOutputPath(path, ".bim", "BIM output");
        File.WriteAllBytes(path, Format(map));
    }
}

/// <summary>
/// Uncompressed Windows bitmaps, first-party (Microsoft, "BITMAPFILEHEADER structure",
/// "BITMAPINFOHEADER structure", "RGBTRIPLE", "RGBQUAD"; saved under op-42-help/formats/): a
/// 14-byte file header whose bfType "must be 0x4d42 (the ASCII string "BM")" and whose bfOffBits
/// is "the offset ... to the bitmap bits"; then the info header (biWidth, biHeight — "if biHeight
/// is positive, the bitmap is a bottom-up DIB with the origin at the lower left corner. If biHeight
/// is negative, the bitmap is a top-down DIB" —, biBitCount, biCompression BI_RGB); rows padded to
/// the stride ((biWidth·biBitCount + 31) &amp; ~31) &gt;&gt; 3; a 24-bit pixel is blue, green,
/// red (RGBTRIPLE) and a 32-bit one blue, green, red, reserved (RGBQUAD). Read: 24- and 32-bit
/// BI_RGB. Refused by name: palette (8 bits or fewer), 16-bit, bit-field and compressed bitmaps.
/// Written: 24-bit bottom-up BI_RGB.
/// </summary>
public static class BmpFile
{
    /// <summary>The Image Simulation's limit for BMP, JPG and PNG inputs ("more than 1 and no more
    /// than 16,000 pixels in each direction").</summary>
    public const int MaximumSide = 16000;

    public static SceneImage Read(string path)
    {
        SceneFile.CheckPath(path, "BMP file");
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static SceneImage Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 54 || bytes[0] != 0x42 || bytes[1] != 0x4D)
            throw new FormatException($"BMP file {name}: not a Windows bitmap (it must start with \"BM\" and hold a 40-byte info header).");
        int offBits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14));
        if (headerSize < 40) throw new FormatException($"BMP file {name}: the info header is {headerSize} bytes; only BITMAPINFOHEADER (40 bytes) and its successors are read.");
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18));
        int heightField = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30));
        if (compression != 0)
            throw new FormatException($"BMP file {name}: compression {compression} is not read; only uncompressed BI_RGB 24- and 32-bit bitmaps are (save it as a 24-bit BMP or a PNG).");
        if (bits != 24 && bits != 32)
            throw new FormatException($"BMP file {name}: {bits} bits per pixel is not read; only 24- and 32-bit bitmaps are (palette and 16-bit bitmaps are not built).");
        bool bottomUp = heightField > 0;
        int height = Math.Abs(heightField);
        if (width < 1 || height < 1 || width > MaximumSide || height > MaximumSide)
            throw new FormatException($"BMP file {name}: {width} × {height} pixels; 1 to {MaximumSide} each way.");
        int stride = ((width * bits + 31) & ~31) >> 3;
        long need = offBits + (long)stride * height;
        if (offBits < 54 || bytes.Length < need)
            throw new FormatException($"BMP file {name}: the pixels need {need} bytes from offset {offBits}; the file has {bytes.Length}.");
        int step = bits / 8;
        var r = new double[height, width];
        var g = new double[height, width];
        var b = new double[height, width];
        for (int line = 0; line < height; line++)
        {
            int row = bottomUp ? height - 1 - line : line;
            int at = offBits + line * stride;
            for (int c = 0; c < width; c++, at += step)
            {
                b[row, c] = bytes[at];
                g[row, c] = bytes[at + 1];
                r[row, c] = bytes[at + 2];
            }
        }
        return SceneImage.Rgb(r, g, b, SceneFileKind.Bmp);
    }

    /// <summary>A 24-bit bottom-up bitmap of the red, green and blue bytes [row, column].</summary>
    public static byte[] Format(byte[,] red, byte[,] green, byte[,] blue)
    {
        int height = red.GetLength(0), width = red.GetLength(1);
        int stride = ((width * 24 + 31) & ~31) >> 3;
        int size = 54 + stride * height;
        var bytes = new byte[size];
        bytes[0] = 0x42; bytes[1] = 0x4D;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), size);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), stride * height);
        for (int line = 0; line < height; line++)
        {
            int row = height - 1 - line;
            int at = 54 + line * stride;
            for (int c = 0; c < width; c++, at += 3)
            {
                bytes[at] = blue[row, c];
                bytes[at + 1] = green[row, c];
                bytes[at + 2] = red[row, c];
            }
        }
        return bytes;
    }
}

/// <summary>
/// PNG images, first-party (W3C, "Portable Network Graphics (PNG) Specification (Third
/// Edition)", saved as op-42-help/formats/png-3.html): the signature 89 50 4E 47 0D 0A 1A 0A
/// (§5.2); chunks of a four-byte length, a four-byte type, the data and a CRC "calculated on the
/// preceding bytes in the chunk, including the chunk type field and chunk data fields, but not
/// including the length field" (§5.3) by the table algorithm of Annex D (polynomial 0xedb88320,
/// initialised to all ones, the complement sent); integers "in network byte order" (§7.1); IHDR
/// width, height, bit depth, colour type, compression, filter and interlace methods (§11.2.1);
/// the IDAT data a zlib stream (compression method 0, deflate; read with the BCL's
/// <see cref="ZLibStream"/>); each scanline preceded by its filter type 0–4, reconstructed by
/// Table 11 and the PaethPredictor of §9.4. Read: bit depth 8, colour types 0 (greyscale),
/// 2 (truecolour), 4 (greyscale with alpha) and 6 (truecolour with alpha), not interlaced; the
/// alpha channel is dropped (the scene is the colour, not its transparency). Refused by name:
/// 16-bit and sub-byte depths, indexed colour, interlacing. Written: 8-bit truecolour or
/// greyscale, filter type 0 on every line.
/// </summary>
public static class PngFile
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static SceneImage Read(string path)
    {
        SceneFile.CheckPath(path, "PNG file");
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static SceneImage Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
            throw new FormatException($"PNG file {name}: the PNG signature is missing.");
        int at = 8;
        int width = 0, height = 0, depth = 0, colour = -1, interlace = 0;
        bool sawHeader = false, sawEnd = false;
        var data = new MemoryStream();
        while (at + 12 <= bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            if (length > int.MaxValue || at + 12 + (long)length > bytes.Length)
                throw new FormatException($"PNG file {name}: a chunk at byte {at} runs past the end of the file.");
            var type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            var body = bytes.AsSpan(at + 8, (int)length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8 + (int)length));
            if (Crc(bytes.AsSpan(at + 4, 4 + (int)length)) != crc)
                throw new FormatException($"PNG file {name}: the CRC of chunk {type} at byte {at} is wrong; the file is damaged.");
            switch (type)
            {
                case "IHDR":
                    if (length != 13) throw new FormatException($"PNG file {name}: IHDR is {length} bytes, not 13.");
                    width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(body));
                    height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                    depth = body[8]; colour = body[9]; interlace = body[12];
                    if (body[10] != 0 || body[11] != 0)
                        throw new FormatException($"PNG file {name}: compression method {body[10]} and filter method {body[11]}; the specification defines only 0.");
                    sawHeader = true;
                    break;
                case "IDAT":
                    data.Write(body);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                case "PLTE":
                    break;
                default:
                    if ((type[0] & 0x20) == 0)
                        throw new FormatException($"PNG file {name}: critical chunk {type} is not known.");
                    break;
            }
            at += 12 + (int)length;
            if (sawEnd) break;
        }
        if (!sawHeader) throw new FormatException($"PNG file {name}: no IHDR chunk.");
        if (!sawEnd) throw new FormatException($"PNG file {name}: no IEND chunk; the file is cut short.");
        if (colour == 3) throw new FormatException($"PNG file {name}: indexed-colour PNG is not read; save it as truecolour or greyscale.");
        if (colour is not (0 or 2 or 4 or 6)) throw new FormatException($"PNG file {name}: colour type {colour} is not a PNG colour type.");
        if (depth != 8) throw new FormatException($"PNG file {name}: bit depth {depth} is not read; only 8-bit samples are.");
        if (interlace != 0) throw new FormatException($"PNG file {name}: interlaced PNG is not read; save it without interlacing.");
        if (width < 1 || height < 1 || width > BmpFile.MaximumSide || height > BmpFile.MaximumSide)
            throw new FormatException($"PNG file {name}: {width} × {height} pixels; 1 to {BmpFile.MaximumSide} each way.");
        int samples = colour switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        int stride = width * samples;
        var raw = new byte[(long)(stride + 1) * height];
        data.Position = 0;
        using (var z = new ZLibStream(data, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0) throw new FormatException($"PNG file {name}: the image data hold {read} bytes; {raw.Length} are needed.");
                read += n;
            }
        }
        var previous = new byte[stride];
        var line = new byte[stride];
        var maps = Enumerable.Range(0, colour is 0 or 4 ? 1 : 3).Select(_ => new double[height, width]).ToArray();
        for (int r = 0; r < height; r++)
        {
            int start = r * (stride + 1);
            int filter = raw[start];
            for (int i = 0; i < stride; i++)
            {
                int x = raw[start + 1 + i];
                int a = i >= samples ? line[i - samples] : 0;
                int b = previous[i];
                int c = i >= samples ? previous[i - samples] : 0;
                line[i] = (byte)(filter switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + (a + b) / 2,
                    4 => x + Paeth(a, b, c),
                    _ => throw new FormatException($"PNG file {name}: line {r + 1} has filter type {filter}; method 0 defines 0 to 4."),
                });
            }
            for (int col = 0; col < width; col++)
                for (int m = 0; m < maps.Length; m++) maps[m][r, col] = line[col * samples + m];
            (previous, line) = (line, previous);
        }
        return maps.Length == 1
            ? SceneImage.Grey(maps[0], SceneFileKind.Png, 255)
            : SceneImage.Rgb(maps[0], maps[1], maps[2], SceneFileKind.Png);
    }

    /// <summary>The PaethPredictor of the specification (§9.4), comparisons in its order.</summary>
    internal static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static readonly uint[] CrcTable = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>The chunk CRC of Annex D.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xffffffffu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }

    /// <summary>An 8-bit PNG of one (greyscale) or three (red, green, blue) byte maps.</summary>
    public static byte[] Format(IReadOnlyList<byte[,]> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (maps.Count is not (1 or 3)) throw new ArgumentException("PNG: one greyscale map or red, green and blue maps.");
        int height = maps[0].GetLength(0), width = maps[0].GetLength(1);
        int samples = maps.Count;
        var raw = new byte[(long)(width * samples + 1) * height];
        long at = 0;
        for (int r = 0; r < height; r++)
        {
            raw[at++] = 0;
            for (int c = 0; c < width; c++)
                for (int m = 0; m < samples; m++) raw[at++] = maps[m][r, c];
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = (byte)(samples == 1 ? 0 : 2);
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        output.Write(head);
        output.Write(data);
        var crcInput = new byte[4 + data.Length];
        Array.Copy(head, 4, crcInput, 0, 4);
        Array.Copy(data, 0, crcInput, 4, data.Length);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(crcInput));
        output.Write(crc);
    }
}

/// <summary>Which files an analysis reads as its scene.</summary>
public enum SceneFileUse
{
    /// <summary>IMA, BIM, BMP or PNG (the Image Simulation).</summary>
    Any,
    /// <summary>IMA or BIM (Geometric Image Analysis, the diffraction image analyses, the IMA/BIM viewer).</summary>
    ImaOrBim,
    /// <summary>BMP or PNG (Geometric Bitmap Image Analysis, the Bitmap File Viewer).</summary>
    Bitmap,
}

/// <summary>Reads a scene by its extension; JPG and every other format are refused by name.</summary>
public static class SceneFile
{
    public static SceneImage Read(string path, SceneFileUse use, string who)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{who}: no source file named; give the full path of {Allowed(use)} file.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool imaBim = ext is ".ima" or ".bim", bitmap = ext is ".bmp" or ".png";
        if (ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif")
            throw new ArgumentException($"{who}: \"{Path.GetFileName(path)}\" is a JPG file; JPG decoding is not built (no image library below the app). Save it as a 24-bit BMP or a PNG.");
        if (!imaBim && !bitmap)
            throw new ArgumentException($"{who}: \"{Path.GetFileName(path)}\" is not {Allowed(use)} file (the extension \"{ext}\" is not read).");
        if (use == SceneFileUse.ImaOrBim && !imaBim)
            throw new ArgumentException($"{who}: \"{Path.GetFileName(path)}\" is a bitmap; this analysis reads IMA or BIM files (help).");
        if (use == SceneFileUse.Bitmap && !bitmap)
            throw new ArgumentException($"{who}: \"{Path.GetFileName(path)}\" is an IMA or BIM file; this analysis reads BMP or PNG files (help: \"BMP, JPG, or PNG\"; JPG is not built).");
        return ext switch
        {
            ".ima" => ReadIma(path),
            ".bim" => BimFile.Read(path),
            ".bmp" => BmpFile.Read(path),
            _ => PngFile.Read(path),
        };
    }

    private static SceneImage ReadIma(string path)
    {
        CheckPath(path, "IMA file");
        var scene = SceneImage.FromIma(ImaFile.Read(path));
        return scene;
    }

    private static string Allowed(SceneFileUse use) => use switch
    {
        SceneFileUse.ImaOrBim => "an .IMA or .BIM",
        SceneFileUse.Bitmap => "a .BMP or .PNG",
        _ => "an .IMA, .BIM, .BMP or .PNG",
    };

    internal static void CheckPath(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{what}: no file named; give its full path.");
        if (!Path.IsPathRooted(path)) throw new ArgumentException($"{what}: \"{path}\" is not a full path; give the full path of the file.");
        if (!File.Exists(path)) throw new FileNotFoundException($"{what}: \"{path}\" does not exist.", path);
    }

    internal static void CheckOutputPath(string path, string extension, string what)
    {
        if (!Path.IsPathRooted(path)) throw new ArgumentException($"{what}: \"{path}\" is not a full path; give the full path of the file to write.");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{what}: \"{path}\" must end in {extension}.");
    }

    /// <summary>Writes the maps (one grey or red, green, blue), each scaled so
    /// <paramref name="fullScale"/> is 255, to a .BMP or .PNG; JPG is refused by name.</summary>
    public static void WriteBitmap(string path, IReadOnlyList<double[,]> maps, double fullScale, string who)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            throw new ArgumentException($"{who}: the output \"{Path.GetFileName(path)}\" is a JPG; JPG encoding is not built. Use .BMP or .PNG.");
        if (ext is not (".bmp" or ".png"))
            throw new ArgumentException($"{who}: the output \"{Path.GetFileName(path)}\" must end in .BMP or .PNG.");
        if (!Path.IsPathRooted(path)) throw new ArgumentException($"{who}: the output \"{path}\" is not a full path.");
        if (!(fullScale > 0)) throw new ArgumentException($"{who}: the output's full scale must be positive.");
        var bytes = maps.Select(m =>
        {
            var b = new byte[m.GetLength(0), m.GetLength(1)];
            for (int r = 0; r < m.GetLength(0); r++)
                for (int c = 0; c < m.GetLength(1); c++)
                    b[r, c] = (byte)Math.Clamp(Math.Round(255 * m[r, c] / fullScale, MidpointRounding.AwayFromZero), 0, 255);
            return b;
        }).ToArray();
        if (ext == ".png") File.WriteAllBytes(path, PngFile.Format(bytes));
        else if (bytes.Length == 1) File.WriteAllBytes(path, BmpFile.Format(bytes[0], bytes[0], bytes[0]));
        else File.WriteAllBytes(path, BmpFile.Format(bytes[0], bytes[1], bytes[2]));
    }
}
