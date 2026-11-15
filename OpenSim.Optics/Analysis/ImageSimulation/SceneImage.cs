using OpenSim.Optics.Analysis.EnclosedEnergy;

namespace OpenSim.Optics.Analysis.ImageSimulation;

// ---------------------------------------------------------------------------------------------
// OP-42: extended scenes. A scene is one or more pixel maps read from an IMA, BIM, BMP or PNG
// file (docs/Optics-Conventions.md §67). The file layouts come from the Ansys OpticStudio 2025 R2
// help pages "Geometric Image Analysis" (IMA and BIM), the W3C PNG specification (third edition)
// and Microsoft's BITMAPFILEHEADER, BITMAPINFOHEADER, RGBTRIPLE and RGBQUAD pages, saved under
// plans/sources/optics/op-41-help/ and op-42-help/ (op-42-help/formats/ for PNG and BMP).
// ---------------------------------------------------------------------------------------------

/// <summary>The file a scene was read from.</summary>
public enum SceneFileKind
{
    /// <summary>Text IMA: one digit 0–9 per pixel.</summary>
    ImaText = 0,
    /// <summary>Binary IMA: one byte per pixel, one map per colour.</summary>
    ImaBinary = 1,
    /// <summary>BIM: doubles.</summary>
    Bim = 2,
    Bmp = 3,
    Png = 4,
    /// <summary>Made in memory (tests, transforms of nothing read).</summary>
    Memory = 5,
}

/// <summary>
/// A scene: <see cref="Channels"/> pixel maps of <see cref="Height"/> rows by <see cref="Width"/>
/// columns, [row, column], row 0 the top of the picture and column 0 its left. A bitmap holds its
/// red, green and blue maps in that order (one map for a grey PNG); an IMA file its pixel maps in
/// file order; a BIM file one map. Values are the file's own (0–9, 0–255, or the BIM doubles).
/// </summary>
public sealed record SceneImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>The pixel maps, each [Height, Width]. Held, not copied.</summary>
    public required IReadOnlyList<double[,]> Channels { get; init; }

    /// <summary>A name per map ("red", "map 2", "grey").</summary>
    public required IReadOnlyList<string> ChannelNames { get; init; }

    public SceneFileKind Kind { get; init; } = SceneFileKind.Memory;

    /// <summary>The value of a full-scale pixel: 9 (text IMA), 255 (binary IMA, BMP, PNG), or the
    /// largest value of a BIM or in-memory scene.</summary>
    public double FullScale { get; init; } = double.NaN;

    /// <summary>True for a bitmap with red, green and blue maps.</summary>
    public bool IsRgb => Channels.Count == 3 && Kind is SceneFileKind.Bmp or SceneFileKind.Png or SceneFileKind.Memory
        && ChannelNames.Count == 3 && ChannelNames[0] == "red";

    /// <summary>The largest value over every map.</summary>
    public double Maximum
    {
        get
        {
            double max = 0;
            foreach (var map in Channels)
                foreach (var v in map) if (v > max) max = v;
            return max;
        }
    }

    /// <summary>A scene of one map.</summary>
    public static SceneImage Grey(double[,] map, SceneFileKind kind = SceneFileKind.Memory, double fullScale = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Checked(new SceneImage
        {
            Width = map.GetLength(1), Height = map.GetLength(0), Channels = new[] { map }, ChannelNames = new[] { "grey" },
            Kind = kind, FullScale = double.IsNaN(fullScale) ? MaxOf(map) : fullScale,
        });
    }

    /// <summary>A red, green, blue scene.</summary>
    public static SceneImage Rgb(double[,] red, double[,] green, double[,] blue, SceneFileKind kind = SceneFileKind.Memory, double fullScale = 255)
    {
        return Checked(new SceneImage
        {
            Width = red.GetLength(1), Height = red.GetLength(0), Channels = new[] { red, green, blue },
            ChannelNames = new[] { "red", "green", "blue" }, Kind = kind, FullScale = fullScale,
        });
    }

    /// <summary>An IMA image as a scene (OP-41's reader).</summary>
    public static SceneImage FromIma(ImaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Checked(new SceneImage
        {
            Width = image.Size, Height = image.Size, Channels = image.Maps,
            ChannelNames = image.Maps.Count == 1 ? new[] { "grey" } : image.Maps.Select((_, i) => $"map {i + 1}").ToArray(),
            Kind = image.Binary ? SceneFileKind.ImaBinary : SceneFileKind.ImaText, FullScale = image.Binary ? 255 : 9,
        });
    }

    /// <summary>The same scene with other maps of the same names.</summary>
    public SceneImage WithChannels(IReadOnlyList<double[,]> maps) => Checked(this with
    {
        Channels = maps, Width = maps[0].GetLength(1), Height = maps[0].GetLength(0),
    });

    internal static SceneImage Checked(SceneImage s)
    {
        if (s.Channels.Count == 0) throw new ArgumentException("Scene: at least one pixel map.");
        if (s.ChannelNames.Count != s.Channels.Count) throw new ArgumentException("Scene: one name per pixel map.");
        foreach (var m in s.Channels)
        {
            if (m.GetLength(0) != s.Height || m.GetLength(1) != s.Width)
                throw new ArgumentException($"Scene: every pixel map must be {s.Height} × {s.Width} (one is {m.GetLength(0)} × {m.GetLength(1)}).");
            foreach (var v in m)
                if (!double.IsFinite(v) || v < 0)
                    throw new ArgumentException($"Scene: a pixel value is {v}; values must be finite and not negative.");
        }
        if (s.Width < 1 || s.Height < 1) throw new ArgumentException("Scene: at least one pixel.");
        return s;
    }

    private static double MaxOf(double[,] map)
    {
        double max = 0;
        foreach (var v in map) if (v > max) max = v;
        return max;
    }
}
