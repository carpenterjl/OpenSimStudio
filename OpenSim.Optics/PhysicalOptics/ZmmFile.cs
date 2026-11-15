using System.Globalization;
using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>One command of a ZMM multimode file.</summary>
/// <param name="Keyword">COHERENT, INCOHERENT, PHASE, GW or TH.</param>
/// <param name="Values">Its numbers (lengths in the file's units, which are lens units).</param>
/// <param name="RandomPhase">PHASE RANDOM.</param>
/// <param name="Line">The line number, from 1.</param>
public sealed record ZmmCommand(string Keyword, IReadOnlyList<double> Values, bool RandomPhase, int Line);

/// <summary>A parsed ZMM file: its commands in order, and the lens unit its lengths are in.</summary>
public sealed record ZmmBeam(IReadOnlyList<ZmmCommand> Commands, LensUnit Unit, string Name);

/// <summary>
/// The multimode beam (OpticStudio 2025 R2 help, "Multimode" and its command pages "!,Comment",
/// "COHERENT", "INCOHERENT", "PHASE", "GW", "TH", "Using Random Values", "Using the Scale
/// Factor"; OP-49): a sum of Gaussian waist and top hat beams, coherent or incoherent, each
/// scaled to its weight ("the dimensionless relative total power of this beam"), optionally
/// with a phase, and with random weights or phases from a seed. The DLL command is refused.
/// <para>
/// The random numbers come from this program's SplitMix64 generator seeded with the seed, so a
/// seed gives the same beam on every run here but not OpticStudio's beam (its generator is not
/// documented).
/// </para>
/// </summary>
public static class ZmmFile
{
    public static ZmmBeam Read(string path, LensUnit unit = LensUnit.Millimeter)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ZMM file: no file named; give the full path of a .ZMM multimode file.");
        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"ZMM file: \"{path}\" is not a full path; give the full path of the .ZMM file.");
        if (!string.Equals(Path.GetExtension(path), ".zmm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"ZMM file: \"{path}\" does not end in .ZMM (help: \"The file must end in the extension ZMM\").");
        if (!File.Exists(path)) throw new FileNotFoundException($"ZMM file: \"{path}\" does not exist.", path);
        return Parse(File.ReadAllText(path), Path.GetFileName(path), unit);
    }

    public static ZmmBeam Parse(string text, string name, LensUnit unit = LensUnit.Millimeter)
    {
        ArgumentNullException.ThrowIfNull(text);
        var commands = new List<ZmmCommand>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int bang = line.IndexOf('!');
            if (bang >= 0) line = line[..bang];   // "Any data following the ! symbol is ignored."
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            string key = t[0].ToUpperInvariant();
            int n = i + 1;
            switch (key)
            {
                case "COHERENT":
                case "INCOHERENT":
                    commands.Add(new ZmmCommand(key, Array.Empty<double>(), false, n));
                    break;
                case "PHASE":
                    if (t.Length < 2) throw new FormatException($"ZMM file {name}, line {n}: PHASE needs an angle in degrees or RANDOM.");
                    if (t[1].Equals("RANDOM", StringComparison.OrdinalIgnoreCase))
                        commands.Add(new ZmmCommand(key, Array.Empty<double>(), true, n));
                    else
                        commands.Add(new ZmmCommand(key, new[] { Number(t[1], name, n) }, false, n));
                    break;
                case "GW":
                case "TH":
                    int count = key == "GW" ? 9 : 5;
                    if (t.Length - 1 < count)
                        throw new FormatException($"ZMM file {name}, line {n}: {key} needs {count} values ({(key == "GW" ? "weight waistx waisty decenterx decentery aperturex aperturey orderx ordery" : "weight waistx waisty decenterx decentery")}); it has {t.Length - 1}.");
                    commands.Add(new ZmmCommand(key, t.Skip(1).Take(count).Select(v => Number(v, name, n)).ToArray(), false, n));
                    break;
                case "DLL":
                    throw new NotSupportedException($"ZMM file {name}, line {n}: the DLL command adds a beam computed by a Windows plug-in DLL, which this program does not load.");
                default:
                    throw new FormatException($"ZMM file {name}, line {n}: \"{t[0]}\" is not a ZMM command (COHERENT, INCOHERENT, PHASE, GW, TH, DLL or a ! comment).");
            }
        }
        if (!commands.Any(c => c.Keyword is "GW" or "TH"))
            throw new FormatException($"ZMM file {name}: it defines no beam (no GW or TH command).");
        return new ZmmBeam(commands, unit, name);
    }

    private static double Number(string s, string name, int line) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v
            : throw new FormatException($"ZMM file {name}, line {line}: \"{s}\" is not a number.");

    /// <summary>The summed field on the grid (physics convention), before normalisation.
    /// <paramref name="scale"/> multiplies every transverse value when it is not zero (help,
    /// "Using the Scale Factor").</summary>
    public static Complex[,] Sum(ZmmBeam beam, int nx, int ny, double dx, double dy, double airWavelength, long seed,
        double scale, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(beam);
        double per = OpticsConventions.MetersPer(beam.Unit) * (scale == 0 ? 1 : scale);
        var random = new SplitMix64(seed);
        var master = new Complex[ny, nx];
        bool coherent = true;
        Complex phase = Complex.One;
        int beams = 0;
        foreach (var c in beam.Commands)
        {
            switch (c.Keyword)
            {
                case "COHERENT": coherent = true; continue;
                case "INCOHERENT": coherent = false; continue;
                case "PHASE":
                    double degrees = c.RandomPhase ? -180 + 360 * random.NextDouble() : c.Values[0];
                    // The help's phase sign is the conjugate of the field's (docs §66).
                    phase = Complex.FromPolarCoordinates(1, -degrees * Math.PI / 180);
                    continue;
            }
            var v = c.Values;
            double weight = v[0] < 0 ? -v[0] * random.NextDouble() : v[0];   // "Using Random Values"
            var component = new Complex[ny, nx];
            double wx = v[1] * per, wy = v[2] * per, cx = v[3] * per, cy = v[4] * per;
            if (!(wx > 0) || !(wy > 0))
                throw new ArgumentException($"ZMM file {beam.Name}, line {c.Line}: the waists must be positive.");
            if (c.Keyword == "GW")
            {
                int l = (int)v[7], m = (int)v[8];
                if (l < 0 || m < 0 || l != v[7] || m != v[8])
                    throw new ArgumentException($"ZMM file {beam.Name}, line {c.Line}: the orders must be whole numbers, 0 or more.");
                if (l > PopBeams.MaximumOrder) { notes.Add($"ZMM file {beam.Name}, line {c.Line}: x order {l} above {PopBeams.MaximumOrder}; order 0 used, as the help says."); l = 0; }
                if (m > PopBeams.MaximumOrder) { notes.Add($"ZMM file {beam.Name}, line {c.Line}: y order {m} above {PopBeams.MaximumOrder}; order 0 used, as the help says."); m = 0; }
                PopBeams.HermiteGaussian(component, dx, dy, wx, wy, cx, cy, l, m);
                double ax = v[5] * per, ay = v[6] * per;
                if (ax != 0 || ay != 0)
                {
                    if (!(ax > 0) || !(ay > 0)) throw new ArgumentException($"ZMM file {beam.Name}, line {c.Line}: the truncating apertures must both be positive, or both zero.");
                    PopBeams.Truncate(component, dx, dy, ax, ay, cx, cy);
                }
            }
            else
                PopBeams.TopHat(component, dx, dy, wx, wy, cx, cy, Complex.One);
            double power = 0;
            foreach (var u in component) power += u.Real * u.Real + u.Imaginary * u.Imaginary;
            power *= dx * dy;
            if (!(power > 0))
                throw new ArgumentException($"ZMM file {beam.Name}, line {c.Line}: the {c.Keyword} beam has no power on the grid (decentered off it, or smaller than a sample).");
            var factor = phase * Math.Sqrt(weight / power);
            for (int r = 0; r < ny; r++)
                for (int k = 0; k < nx; k++)
                {
                    var b = component[r, k] * factor;
                    if (coherent) master[r, k] += b;
                    else
                    {
                        // "summing the point-by-point intensity of the two beams, taking the square
                        // root to determine the amplitude, and setting the real part of the combined
                        // beam to this amplitude, and the imaginary part to zero."
                        double i = master[r, k].Real * master[r, k].Real + master[r, k].Imaginary * master[r, k].Imaginary
                            + b.Real * b.Real + b.Imaginary * b.Imaginary;
                        master[r, k] = new Complex(Math.Sqrt(i), 0);
                    }
                }
            beams++;
        }
        notes.Add($"Physical optics, multimode beam {beam.Name}: {beams} component beams summed; random weights and phases use this program's generator with seed {seed}, not OpticStudio's.");
        return master;
    }
}
