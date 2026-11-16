using System.Text;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// Save Detector Data and Load Detector Data in OpticStudio's binary formats (help, "Saving and
/// Loading Detector Data"; docs/Optics-Conventions.md §73): .DDR (Detector Rectangle), .DDC
/// (Detector Color) and .DDP (Detector Polar). .DDV (Detector Volume) is not built, as the
/// detector is not.
/// <para>
/// Layout, little-endian: the header <c>int version, type, lens_units, source_units,
/// source_multiplier; int i_data[50]; double d_data[50];</c> with the 4-byte gap the help notes
/// between <c>i_data</c> and <c>d_data</c> (so <c>d_data</c> starts at byte 224 and the header is
/// 624 bytes); then a 32-bit "ray trace method" (2: NSC ray trace); then one block of doubles per
/// pixel in the help's pixel order: Rectangle <c>inc_int_pos, inc_int_ang, coh_real, coh_imag,
/// coh_amp</c>; Color <c>pos_P, pos_X, pos_Y, pos_Z, ang_P, ang_X, ang_Y, ang_Z</c>; Polar four
/// unused values then <c>ang_P, ang_X, ang_Y, ang_Z</c>. Version 1004. Lengths in the file are in
/// its lens unit (0 mm, 1 cm, 2 in, 3 m).
/// </para>
/// <para>
/// Written by this program: source_units 0 (watts) and source_multiplier 5, its reading of "0
/// through 9 for source unit multipliers femto through Tera" with the unit prefixes the Units page
/// lists (femto, pico, nano, micro, milli, none, kilo, mega, giga, tera: none is 5). Reading
/// refuses any other source unit or multiplier, as OpticStudio refuses data whose units do not
/// match. The noise sums and the luminous-flux layer are not in the format, so loaded data has
/// no noise estimate and, for Detector Rectangle, no photometric view.
/// </para>
/// </summary>
public static class NscDetectorFile
{
    public const int Version = 1004;
    private const int HeaderBytes = 624, DataOffset = 224;

    /// <summary>The extension OpticStudio uses for the data's type (".DDR", ".DDC", ".DDP").</summary>
    public static string Extension(NscDetectorData data) => data switch
    {
        NscFlatDetectorData { IsColor: false } => ".DDR",
        NscFlatDetectorData => ".DDC",
        NscPolarDetectorData => ".DDP",
        _ => throw new NotSupportedException($"{data.TypeName}: Save Detector Data is not built for this type."),
    };

    private static int UnitCode(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => 0,
        LensUnit.Centimeter => 1,
        LensUnit.Inch => 2,
        LensUnit.Meter => 3,
        _ => throw new NotSupportedException($"The lens unit {unit} has no detector-file code."),
    };

    private static LensUnit UnitOf(int code, string path) => code switch
    {
        0 => LensUnit.Millimeter,
        1 => LensUnit.Centimeter,
        2 => LensUnit.Inch,
        3 => LensUnit.Meter,
        _ => throw new InvalidDataException($"{path}: lens_units {code} is not 0 (mm), 1 (cm), 2 (in) or 3 (m)."),
    };

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> with lengths in <paramref name="unit"/>.</summary>
    public static void Write(string path, NscDetectorData data, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = File.Create(path);
        Write(stream, data, unit);
    }

    public static void Write(Stream stream, NscDetectorData data, LensUnit unit)
    {
        var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var i = new int[50];
        var d = new double[50];
        double Len(double meters) => OpticsConventions.FromMeters(meters, unit);
        int type;
        switch (data)
        {
            case NscFlatDetectorData f:
                type = f.IsColor ? 2 : 1;
                i[0] = f.PixelsX; i[1] = f.PixelsY; i[2] = Clamp(f.SpatialHits); i[3] = Clamp(f.AngularHits);
                d[0] = Len(f.HalfWidthX); d[1] = Len(f.HalfWidthY);
                d[2] = f.AngleXMin; d[3] = f.AngleXMax; d[4] = f.AngleYMin; d[5] = f.AngleYMax;
                break;
            case NscPolarDetectorData p:
                type = 3;
                i[0] = p.PolarPixels; i[1] = p.AzimuthPixels; i[3] = Clamp(p.Hits);
                d[0] = p.MaxAngle; d[1] = Len(p.RadialSize);
                break;
            default:
                throw new NotSupportedException($"{data.TypeName}: Save Detector Data is not built for this type.");
        }
        w.Write(Version); w.Write(type); w.Write(UnitCode(unit)); w.Write(0); w.Write(5);
        foreach (var v in i) w.Write(v);
        w.Write(0);                                   // the 4-byte gap before d_data
        foreach (var v in d) w.Write(v);
        w.Write(2);                                   // ray trace method: NSC ray trace
        switch (data)
        {
            case NscFlatDetectorData { IsColor: false } r:
                if (r.CoherentReal is null || r.CoherentImaginary is null || r.CoherentAmplitude is null)
                    throw new InvalidOperationException("Detector Rectangle data without its coherent sums cannot be written as a DDR file.");
                for (int k = 0; k < r.PixelCount; k++)
                {
                    w.Write(r.Flux[k]); w.Write(r.AngleFlux[k]);
                    w.Write(r.CoherentReal[k]); w.Write(r.CoherentImaginary[k]); w.Write(r.CoherentAmplitude[k]);
                }
                break;
            case NscFlatDetectorData c:
                if (c.X is null || c.Y is null || c.Z is null || c.AngleX is null || c.AngleY is null || c.AngleZ is null)
                    throw new InvalidOperationException("Detector Color data without its tristimulus values cannot be written as a DDC file.");
                for (int k = 0; k < c.PixelCount; k++)
                {
                    w.Write(c.Flux[k]); w.Write(c.X[k]); w.Write(c.Y[k]); w.Write(c.Z[k]);
                    w.Write(c.AngleFlux[k]); w.Write(c.AngleX[k]); w.Write(c.AngleY[k]); w.Write(c.AngleZ[k]);
                }
                break;
            case NscPolarDetectorData p:
                for (int k = 0; k < p.PixelCount; k++)
                {
                    w.Write(0.0); w.Write(0.0); w.Write(0.0); w.Write(0.0);
                    w.Write(p.Flux[k]); w.Write(p.X?[k] ?? 0); w.Write(p.Y?[k] ?? 0); w.Write(p.Z?[k] ?? 0);
                }
                break;
        }
        w.Flush();
    }

    private static int Clamp(long v) => (int)Math.Min(int.MaxValue, v);

    /// <summary>Reads a DDR, DDC or DDP file; lengths come back in meters. Throws
    /// <see cref="InvalidDataException"/> naming the file and what is wrong.</summary>
    public static NscDetectorData Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Detector data file: no file name given.");
        if (!File.Exists(path)) throw new FileNotFoundException($"Detector data file '{path}' does not exist.", path);
        var bytes = File.ReadAllBytes(path);
        return Read(bytes, path);
    }

    public static NscDetectorData Read(byte[] bytes, string name)
    {
        if (bytes.Length < HeaderBytes + 4) throw new InvalidDataException($"{name}: {bytes.Length} bytes is shorter than the {HeaderBytes + 4}-byte header of a detector data file.");
        int I(int index) => BitConverter.ToInt32(bytes, 20 + 4 * index);
        double D(int index) => BitConverter.ToDouble(bytes, DataOffset + 8 * index);
        int version = BitConverter.ToInt32(bytes, 0), type = BitConverter.ToInt32(bytes, 4);
        if (version != Version) throw new InvalidDataException($"{name}: version {version}; this program reads version {Version} only.");
        var unit = UnitOf(BitConverter.ToInt32(bytes, 8), name);
        int sourceUnits = BitConverter.ToInt32(bytes, 12), multiplier = BitConverter.ToInt32(bytes, 16);
        if (sourceUnits != 0 || multiplier != 5)
            throw new InvalidDataException($"{name}: source units {sourceUnits} with multiplier {multiplier}; this program's detector data is in watts (0 with multiplier 5), and data in other units is refused, as OpticStudio refuses data whose units do not match.");
        int method = BitConverter.ToInt32(bytes, HeaderBytes);
        if (method == -1) throw new InvalidDataException($"{name}: the file says it holds no data (ray trace method −1).");
        double M(double v) => OpticsConventions.ToMeters(v, unit);
        int at = HeaderBytes + 4;
        double[] Block(int pixels, int per, out int count)
        {
            count = pixels;
            long need = at + (long)pixels * per * 8;
            if (pixels <= 0 || bytes.Length != need)
                throw new InvalidDataException($"{name}: {pixels} pixels of {per} values need {need} bytes; the file has {bytes.Length}.");
            var v = new double[pixels * per];
            for (int k = 0; k < v.Length; k++) v[k] = BitConverter.ToDouble(bytes, at + 8 * k);
            return v;
        }
        double[] Column(double[] v, int per, int c, int n)
        {
            var r = new double[n];
            for (int k = 0; k < n; k++) r[k] = v[k * per + c];
            return r;
        }
        double[] Nan(int n) => Enumerable.Repeat(double.NaN, n).ToArray();
        switch (type)
        {
            case 1:
            case 2:
            {
                int nx = I(0), ny = I(1);
                int per = type == 1 ? 5 : 8;
                var v = Block(nx * ny, per, out int n);
                var data = new NscFlatDetectorData
                {
                    ObjectNumber = 0, LaunchedRays = 0, IsColor = type == 2,
                    HalfWidthX = M(D(0)), HalfWidthY = M(D(1)), PixelsX = nx, PixelsY = ny,
                    AngleXMin = D(2), AngleXMax = D(3), AngleYMin = D(4), AngleYMax = D(5),
                    SpatialHits = I(2), AngularHits = I(3),
                    Flux = Column(v, per, 0, n), FluxSquares = Nan(n),
                    AngleFlux = Column(v, per, type == 1 ? 1 : 4, n), AngleFluxSquares = Nan(n),
                };
                return type == 1
                    ? data with { CoherentReal = Column(v, per, 2, n), CoherentImaginary = Column(v, per, 3, n), CoherentAmplitude = Column(v, per, 4, n) }
                    : data with
                    {
                        X = Column(v, per, 1, n), Y = Column(v, per, 2, n), Z = Column(v, per, 3, n),
                        AngleX = Column(v, per, 5, n), AngleY = Column(v, per, 6, n), AngleZ = Column(v, per, 7, n),
                        Luminous = Column(v, per, 2, n), AngleLuminous = Column(v, per, 6, n),
                    };
            }
            case 3:
            {
                int np = I(0), na = I(1);
                var v = Block(np * na, 8, out int n);
                return new NscPolarDetectorData
                {
                    ObjectNumber = 0, LaunchedRays = 0, MaxAngle = D(0), RadialSize = M(D(1)), PolarPixels = np, AzimuthPixels = na,
                    Hits = I(3), Flux = Column(v, 8, 4, n), FluxSquares = Nan(n),
                    X = Column(v, 8, 5, n), Y = Column(v, 8, 6, n), Z = Column(v, 8, 7, n), Luminous = Column(v, 8, 6, n),
                };
            }
            case 4:
                throw new InvalidDataException($"{name}: Detector Volume data (DDV) is not read: {NscDetectorTypes.VolumeReason}");
            default:
                throw new InvalidDataException($"{name}: detector type {type} is not 1 (Rectangle), 2 (Color), 3 (Polar) or 4 (Volume).");
        }
    }
}
