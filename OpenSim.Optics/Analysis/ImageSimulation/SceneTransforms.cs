namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>How a picture is flipped (help, "Image Simulation": "Flips the source bitmap
/// left-right, top-bottom, or both").</summary>
public enum SceneFlip
{
    None = 0,
    LeftRight = 1,
    TopBottom = 2,
    Both = 3,
}

/// <summary>A quarter-turn rotation of the source (help, "Image Simulation", "Rotate Source";
/// the help names no angles: quarter turns counter-clockwise are built, a reading in
/// docs/Optics-Conventions.md §10).</summary>
public enum SceneRotation
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270,
}

/// <summary>
/// The source edits the Image Simulation applies "before any effects of the optical system are
/// considered" (help, "Image Simulation"): oversampling ("copying one pixel into 2, 4, or more
/// identical adjacent pixels", "a factor of 2 at a time"), the guard band ("repeatedly doubling
/// the number of pixels ... a black "guard band" all around the original image"), flips and
/// rotations; and the diffraction analyses' zero padding (help, "Partially Coherent Image
/// Analysis": "The number of total pixels is thus 2*n-1 if n is odd, or 2*n if n is even").
/// </summary>
public static class SceneTransforms
{
    /// <summary>Each pixel copied into factor × factor pixels.</summary>
    public static SceneImage Oversample(SceneImage scene, int factor)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (factor < 1) throw new ArgumentException($"Oversampling must be at least 1 (it is {factor}).");
        if (factor == 1) return scene;
        return scene.WithChannels(scene.Channels.Select(m => Oversample(m, factor)).ToArray());
    }

    public static double[,] Oversample(double[,] map, int factor)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        var o = new double[h * factor, w * factor];
        for (int r = 0; r < h * factor; r++)
            for (int c = 0; c < w * factor; c++) o[r, c] = map[r / factor, c / factor];
        return o;
    }

    /// <summary>The picture centred on a black canvas factor times as wide and high.</summary>
    public static SceneImage GuardBand(SceneImage scene, int factor)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (factor < 1) throw new ArgumentException($"The guard band must be at least 1 (it is {factor}).");
        if (factor == 1) return scene;
        return scene.WithChannels(scene.Channels.Select(m => Pad(m, m.GetLength(0) * factor, m.GetLength(1) * factor)).ToArray());
    }

    /// <summary>The map centred in a zero canvas of rows × columns (extra odd pixel at the end).</summary>
    public static double[,] Pad(double[,] map, int rows, int columns)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        if (rows < h || columns < w) throw new ArgumentException("Padding cannot shrink a map.");
        var o = new double[rows, columns];
        int r0 = (rows - h) / 2, c0 = (columns - w) / 2;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++) o[r0 + r, c0 + c] = map[r, c];
        return o;
    }

    /// <summary>The help's zero padding of a square n × n map by a factor z: z·n − (z − 1)
    /// pixels when n is odd (keeping a centre pixel; the help's 2X case is 2n − 1), z·n when even.</summary>
    public static double[,] ZeroPad(double[,] map, int factor)
    {
        if (factor < 1) throw new ArgumentException($"The zero padding must be at least 1 (it is {factor}).");
        int n = map.GetLength(0);
        if (factor == 1) return map;
        int size = n % 2 == 1 ? factor * n - (factor - 1) : factor * n;
        return Pad(map, size, size);
    }

    public static SceneImage Flip(SceneImage scene, SceneFlip flip)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (flip == SceneFlip.None) return scene;
        return scene.WithChannels(scene.Channels.Select(m => Flip(m, flip)).ToArray());
    }

    public static double[,] Flip(double[,] map, SceneFlip flip)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        bool lr = flip is SceneFlip.LeftRight or SceneFlip.Both, tb = flip is SceneFlip.TopBottom or SceneFlip.Both;
        var o = new double[h, w];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++) o[r, c] = map[tb ? h - 1 - r : r, lr ? w - 1 - c : c];
        return o;
    }

    /// <summary>A counter-clockwise quarter-turn rotation as the picture is seen.</summary>
    public static SceneImage Rotate(SceneImage scene, SceneRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (rotation == SceneRotation.None) return scene;
        return scene.WithChannels(scene.Channels.Select(m => Rotate(m, rotation)).ToArray());
    }

    public static double[,] Rotate(double[,] map, SceneRotation rotation)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        switch (rotation)
        {
            case SceneRotation.None: return map;
            case SceneRotation.Rotate180: return Flip(map, SceneFlip.Both);
            case SceneRotation.Rotate90:
            {
                // Counter-clockwise: the right column becomes the top row.
                var o = new double[w, h];
                for (int r = 0; r < w; r++)
                    for (int c = 0; c < h; c++) o[r, c] = map[c, w - 1 - r];
                return o;
            }
            case SceneRotation.Rotate270:
            {
                var o = new double[w, h];
                for (int r = 0; r < w; r++)
                    for (int c = 0; c < h; c++) o[r, c] = map[h - 1 - c, r];
                return o;
            }
            default: throw new ArgumentException($"Rotation {rotation} is not a quarter turn.");
        }
    }

    /// <summary>A power of two from 1 to 32, as the help's doubling settings need.</summary>
    public static void CheckPowerOfTwo(int value, string who, string what)
    {
        if (value < 1 || value > 32 || (value & (value - 1)) != 0)
            throw new ArgumentException($"{who}: the {what} must be 1, 2, 4, 8, 16 or 32 (it is {value}); the help applies it \"a factor of 2 at a time\".");
    }
}
