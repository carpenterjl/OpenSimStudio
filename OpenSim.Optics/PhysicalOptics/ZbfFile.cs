using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>The pilot beam of a ZBF file in one direction, in the file's units.</summary>
/// <param name="Position">"The z position relative to the pilot beam waist".</param>
/// <param name="Rayleigh">"The Rayleigh distance for the pilot beam".</param>
/// <param name="Waist">"The waist in lens units of the pilot beam".</param>
public readonly record struct ZbfPilot(double Position, double Rayleigh, double Waist);

/// <summary>
/// A Zemax Beam File as its layout defines it (OpticStudio 2025 R2 help, "Zemax Beam File (ZBF)
/// Binary Format" and "... text format"; OP-49). Lengths are in the file's <see cref="Unit"/>
/// (0 mm, 1 cm, 2 in, 3 m, the same numbers as <see cref="LensUnit"/>); the samples are as the
/// file stores them: "Ex*Ex + Ey*Ey is in units of watts" (the power of each sample), in the
/// help's phase sign (positive when the wavefront is ahead of the reference).
/// </summary>
public sealed record ZbfBeam
{
    public int Version { get; init; } = 1;
    public required int Nx { get; init; }
    public required int Ny { get; init; }
    public bool IsPolarized { get; init; }
    public LensUnit Unit { get; init; } = LensUnit.Millimeter;
    public required double Dx { get; init; }
    public required double Dy { get; init; }
    public ZbfPilot PilotX { get; init; }
    public ZbfPilot PilotY { get; init; }
    /// <summary>"The wavelength in lens units of the beam in the current medium."</summary>
    public required double Wavelength { get; init; }
    public double Index { get; init; } = 1;
    public double ReceiverEfficiency { get; init; }
    public double SystemEfficiency { get; init; }

    /// <summary>Ex, [row (y), column (x)], row 0 the −y edge.</summary>
    public required Complex[,] Ex { get; init; }

    /// <summary>Ey when <see cref="IsPolarized"/>, else null.</summary>
    public Complex[,]? Ey { get; init; }
}

/// <summary>Reads and writes <see cref="ZbfBeam"/> files, binary or text, and converts between
/// them and <see cref="PopField"/>.</summary>
public static class ZbfFile
{
    /// <summary>Reads the file at <paramref name="path"/> (a full path ending in .ZBF); text
    /// when its first character is "A" (help: "The first line must be the single character
    /// 'A'"), binary otherwise.</summary>
    public static ZbfBeam Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ZBF file: no file named; give the full path of a .ZBF beam file.");
        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"ZBF file: \"{path}\" is not a full path; give the full path of the .ZBF file.");
        if (!string.Equals(Path.GetExtension(path), ".zbf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"ZBF file: \"{path}\" does not end in .ZBF (help: \"The file must end in the extension ZBF\").");
        if (!File.Exists(path)) throw new FileNotFoundException($"ZBF file: \"{path}\" does not exist.", path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static ZbfBeam Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int start = 0;
        while (start < bytes.Length && (bytes[start] == ' ' || bytes[start] == '\t' || bytes[start] == '\r' || bytes[start] == '\n')) start++;
        if (start < bytes.Length && (bytes[start] == 'A' || bytes[start] == 'a')
            && (start + 1 == bytes.Length || char.IsWhiteSpace((char)bytes[start + 1])))
            return ParseText(Encoding.ASCII.GetString(bytes, start + 1, bytes.Length - start - 1), name);
        return ParseBinary(bytes, name);
    }

    private const int HeaderInts = 9, HeaderDoubles = 20;

    private static ZbfBeam ParseBinary(byte[] b, string name)
    {
        int header = HeaderInts * 4 + HeaderDoubles * 8;
        if (b.Length < header) throw new FormatException($"ZBF file {name}: the binary header needs {header} bytes; the file has {b.Length}.");
        int I(int k) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4 * k));
        double D(int k) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(HeaderInts * 4 + 8 * k));
        var ints = new double[] { I(0), I(1), I(2), I(3), I(4) };
        var doubles = new double[HeaderDoubles];
        for (int k = 0; k < HeaderDoubles; k++) doubles[k] = D(k);
        var (nx, ny, polarized) = CheckHeader(ints, name);
        long values = 2L * nx * ny * (polarized ? 2 : 1);
        long needed = header + 8 * values;
        if (b.Length < needed)
            throw new FormatException($"ZBF file {name}: {nx} x {ny} {(polarized ? "polarized" : "unpolarized")} samples need {needed} bytes; the file has {b.Length}.");
        int offset = header;
        double Next() { double v = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(offset)); offset += 8; return v; }
        return Build(ints, doubles, Next, name);
    }

    private static ZbfBeam ParseText(string text, string name)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int at = 0;
        double Token(string what)
        {
            if (at >= tokens.Length) throw new FormatException($"ZBF file {name}: the text ends before {what}.");
            string t = tokens[at++];
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException($"ZBF file {name}: \"{t}\" ({what}, value {at}) is not a number.");
            return v;
        }
        var ints = new double[5];
        string[] intNames = { "version", "nx", "ny", "ispol", "units" };
        for (int k = 0; k < 5; k++) ints[k] = Token(intNames[k]);
        for (int k = 0; k < 4; k++) Token("an unused header value");
        var doubles = new double[HeaderDoubles];
        for (int k = 0; k < HeaderDoubles; k++) doubles[k] = Token($"header value {k + 1} of 20");
        CheckHeader(ints, name);
        return Build(ints, doubles, () => Token("a sample value"), name);
    }

    private static (int Nx, int Ny, bool Polarized) CheckHeader(double[] ints, string name)
    {
        int nx = (int)ints[1], ny = (int)ints[2], pol = (int)ints[3], units = (int)ints[4];
        if (ints[1] != nx || ints[2] != ny || !PopField.IsSampling(nx) || !PopField.IsSampling(ny))
            throw new FormatException($"ZBF file {name}: the sampling is {ints[1]} x {ints[2]}; the help requires powers of 2 from {PopField.MinimumSampling} to {PopField.MaximumSampling}.");
        if (pol != 0 && pol != 1) throw new FormatException($"ZBF file {name}: the \"is polarized\" flag is {ints[3]}; it must be 0 or 1.");
        if (units < 0 || units > 3) throw new FormatException($"ZBF file {name}: the units flag is {ints[4]}; it must be 0 (mm), 1 (cm), 2 (in) or 3 (m).");
        return (nx, ny, pol == 1);
    }

    private static ZbfBeam Build(double[] ints, double[] d, Func<double> next, string name)
    {
        int nx = (int)ints[1], ny = (int)ints[2];
        bool polarized = (int)ints[3] == 1;
        Complex[,] Read()
        {
            var a = new Complex[ny, nx];
            for (int r = 0; r < ny; r++)
                for (int c = 0; c < nx; c++)
                {
                    double re = next(), im = next();
                    a[r, c] = new Complex(re, im);
                }
            return a;
        }
        var ex = Read();
        var ey = polarized ? Read() : null;
        var beam = new ZbfBeam
        {
            Version = (int)ints[0], Nx = nx, Ny = ny, IsPolarized = polarized, Unit = (LensUnit)(int)ints[4],
            Dx = d[0], Dy = d[1],
            PilotX = new ZbfPilot(d[2], d[3], d[4]), PilotY = new ZbfPilot(d[5], d[6], d[7]),
            Wavelength = d[8], Index = d[9], ReceiverEfficiency = d[10], SystemEfficiency = d[11],
            Ex = ex, Ey = ey,
        };
        if (!(beam.Dx > 0) || !(beam.Dy > 0) || !double.IsFinite(beam.Dx) || !double.IsFinite(beam.Dy))
            throw new FormatException($"ZBF file {name}: the point spacing is {beam.Dx} x {beam.Dy}; it must be positive.");
        return beam;
    }

    /// <summary>Writes <paramref name="beam"/> in the binary layout (unused values zero).</summary>
    public static byte[] ToBinary(ZbfBeam beam)
    {
        ArgumentNullException.ThrowIfNull(beam);
        Check(beam);
        int count = beam.Nx * beam.Ny * (beam.IsPolarized ? 2 : 1);
        var b = new byte[HeaderInts * 4 + HeaderDoubles * 8 + 16L * count];
        int[] ints = { beam.Version, beam.Nx, beam.Ny, beam.IsPolarized ? 1 : 0, (int)beam.Unit, 0, 0, 0, 0 };
        for (int k = 0; k < HeaderInts; k++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4 * k), ints[k]);
        int offset = HeaderInts * 4;
        foreach (double v in HeaderValues(beam)) { BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(offset), v); offset += 8; }
        foreach (double v in SampleValues(beam)) { BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(offset), v); offset += 8; }
        return b;
    }

    /// <summary>Writes <paramref name="beam"/> in the text layout, one value a line, "A" first.</summary>
    public static string ToText(ZbfBeam beam)
    {
        ArgumentNullException.ThrowIfNull(beam);
        Check(beam);
        var sb = new StringBuilder();
        sb.Append("A\n");
        foreach (int v in new[] { beam.Version, beam.Nx, beam.Ny, beam.IsPolarized ? 1 : 0, (int)beam.Unit, 0, 0, 0, 0 })
            sb.Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (double v in HeaderValues(beam).Concat(SampleValues(beam)))
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Writes <paramref name="beam"/> to <paramref name="path"/> (binary).</summary>
    public static void Write(string path, ZbfBeam beam)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException($"ZBF file: \"{path}\" is not a full path; give the full path of the .ZBF file to write.");
        if (!string.Equals(Path.GetExtension(path), ".zbf", StringComparison.OrdinalIgnoreCase))
            path += ".ZBF";   // help: "OpticStudio will add the extension "ZBF" to the provided file name"
        File.WriteAllBytes(path, ToBinary(beam));
    }

    private static void Check(ZbfBeam beam)
    {
        if (!PopField.IsSampling(beam.Nx) || !PopField.IsSampling(beam.Ny))
            throw new ArgumentException($"ZBF file: the sampling {beam.Nx} x {beam.Ny} is not a power of 2 from {PopField.MinimumSampling} to {PopField.MaximumSampling}.");
        if (beam.Ex.GetLength(0) != beam.Ny || beam.Ex.GetLength(1) != beam.Nx)
            throw new ArgumentException("ZBF file: Ex does not have Ny rows of Nx samples.");
        if (beam.IsPolarized && (beam.Ey is null || beam.Ey.GetLength(0) != beam.Ny || beam.Ey.GetLength(1) != beam.Nx))
            throw new ArgumentException("ZBF file: a polarized beam needs Ey with Ny rows of Nx samples.");
    }

    private static IEnumerable<double> HeaderValues(ZbfBeam b)
    {
        yield return b.Dx; yield return b.Dy;
        yield return b.PilotX.Position; yield return b.PilotX.Rayleigh; yield return b.PilotX.Waist;
        yield return b.PilotY.Position; yield return b.PilotY.Rayleigh; yield return b.PilotY.Waist;
        yield return b.Wavelength; yield return b.Index;
        yield return b.ReceiverEfficiency; yield return b.SystemEfficiency;
        for (int k = 0; k < 8; k++) yield return 0;
    }

    private static IEnumerable<double> SampleValues(ZbfBeam b)
    {
        foreach (var a in b.IsPolarized ? new[] { b.Ex, b.Ey! } : new[] { b.Ex })
            for (int r = 0; r < b.Ny; r++)
                for (int c = 0; c < b.Nx; c++) { yield return a[r, c].Real; yield return a[r, c].Imaginary; }
    }

    /// <summary>
    /// The beam file of <paramref name="field"/>: the samples √(power per sample) in the help's
    /// phase sign (the conjugate of the field's physics-convention samples, docs §66), the pilot
    /// in both directions, lengths in <paramref name="unit"/>. Fiber efficiencies are zero
    /// ("Zero if fiber coupling is not computed").
    /// </summary>
    public static ZbfBeam FromField(PopField field, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(field);
        double per = OpticsConventions.MetersPer(unit);
        double scale = Math.Sqrt(field.Dx * field.Dy);
        var ex = new Complex[field.Ny, field.Nx];
        for (int r = 0; r < field.Ny; r++)
            for (int c = 0; c < field.Nx; c++) ex[r, c] = Complex.Conjugate(field.Samples[r, c]) * scale;
        var p = field.Pilot;
        var pilot = new ZbfPilot(p.DistanceFromWaist / per, p.RayleighRange / per, p.WaistRadius / per);
        return new ZbfBeam
        {
            Nx = field.Nx, Ny = field.Ny, Unit = unit, Dx = field.Dx / per, Dy = field.Dy / per,
            PilotX = pilot, PilotY = pilot, Wavelength = p.MediumWavelength / per, Index = p.Index, Ex = ex,
        };
    }

    /// <summary>
    /// The field of a beam file in a medium of <paramref name="index"/> at the system's
    /// wavelength <paramref name="airWavelength"/> (help, "File (defining the initial beam)":
    /// "The Rayleigh distance is ignored on input and is automatically recomputed"; a wavelength
    /// that differs from the system's "is ignored and the beam will be propagated at the
    /// wavelength specified in the System Explorer instead"). A pilot whose waist entries are 0
    /// is fitted to the field (help, "Beam File Viewer"). Polarized files are refused: polarized
    /// propagation is not built.
    /// </summary>
    public static PopField ToField(ZbfBeam beam, double airWavelength, double index, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(beam);
        if (beam.IsPolarized)
            throw new NotSupportedException("Physical optics: the ZBF file is polarized (it holds Ex and Ey); polarized physical optics propagation is not built, so only unpolarized beam files can start a propagation.");
        double per = OpticsConventions.MetersPer(beam.Unit);
        double fileAir = beam.Wavelength * per * beam.Index;
        if (beam.Wavelength > 0 && Math.Abs(fileAir - airWavelength) > 1e-6 * airWavelength)
            notes.Add($"Physical optics: the ZBF file's wavelength ({fileAir:g8} m in air) differs from the system's ({airWavelength:g8} m); the system's is used, as the help says.");
        if (beam.Index > 0 && Math.Abs(beam.Index - index) > 1e-9 * index)
            notes.Add($"Physical optics: the ZBF file was written in a medium of index {beam.Index:g8}; the beam starts in a medium of index {index:g8}.");
        return ComponentToField(beam, beam.Ex, airWavelength, index, notes);
    }

    /// <summary>One polarization component of a beam file as a field (physics convention, U in
    /// √(W/m²)), with the file's pilot (fitted when its waist entries are 0).</summary>
    public static PopField ComponentToField(ZbfBeam beam, Complex[,] component, double airWavelength, double index, ICollection<string> notes)
    {
        double per = OpticsConventions.MetersPer(beam.Unit);
        double dx = beam.Dx * per, dy = beam.Dy * per;
        var samples = new Complex[beam.Ny, beam.Nx];
        double scale = 1 / Math.Sqrt(dx * dy);
        for (int r = 0; r < beam.Ny; r++)
            for (int c = 0; c < beam.Nx; c++) samples[r, c] = Complex.Conjugate(component[r, c]) * scale;
        PilotBeam pilot;
        if (beam.PilotX.Waist == 0 || beam.PilotY.Waist == 0)
            pilot = PopBeams.FitPilot(samples, dx, dy, airWavelength, index, notes, "ZBF beam");
        else
        {
            if (beam.PilotX.Waist != beam.PilotY.Waist || beam.PilotX.Position != beam.PilotY.Position)
                throw new NotSupportedException("Physical optics: the ZBF file has different x and y pilot beams (written with \"Separate X, Y\"); separate x and y propagation is not built.");
            pilot = PilotBeam.FromWaist(beam.PilotX.Waist * per, beam.PilotX.Position * per, index, airWavelength);
        }
        return new PopField(samples, dx, dy, pilot, pilot.IsInsideRayleighRange ? PopReference.Plane : PopReference.Sphere);
    }
}
