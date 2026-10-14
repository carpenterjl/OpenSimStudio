using System.Globalization;
using System.Numerics;
using OpenSim.Rf.Network;

namespace OpenSim.Rf.Si;

/// <summary>Network data read from a Touchstone file, as single-ended S-parameters in one
/// reference resistance.</summary>
public sealed record TouchstoneData(
    IReadOnlyList<double> FrequenciesHz,
    IReadOnlyList<Complex[,]> Scattering,
    double ReferenceOhms)
{
    public int Ports => Scattering.Count > 0 ? Scattering[0].GetLength(0) : 0;

    /// <summary>"1.0", "1.1" (per-port references on the option line), "2.0" or "2.1".</summary>
    public string Version { get; init; } = "1.0";

    /// <summary>The file's own per-port references when they were not all
    /// <see cref="ReferenceOhms"/>: the S-parameters were renormalized from these.</summary>
    public IReadOnlyList<double>? FileReferencesOhms { get; init; }

    /// <summary>What the reader did to the data beyond reading it (renormalization,
    /// mixed-mode conversion, a port count taken from the keyword over the extension).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Touchstone import, versions 1.0, 1.1, 2.0 and 2.1 (IBIS Open Forum, Touchstone File Format
/// Specification 2.1, 2024).
/// <para>
/// Version 1: the option line's frequency unit, parameter kind (S, Y or Z — Y and Z are
/// normalised to the reference), number format (RI, MA, DB) and reference resistance — one
/// for all ports, or one per port at the end of the line (1.1); the two-port column-major
/// data order; data wrapped over several lines; comments. The noise block a two-port file
/// may carry after its network data is skipped. The port count comes from the extension.
/// </para>
/// <para>
/// Version 2: [Version], the option line, [Number of Ports], [Two-Port Data Order]
/// (12_21 or 21_12), [Number of Frequencies], [Reference] (per port, possibly over several
/// lines), [Matrix Format] (Full, Lower, Upper), [Mixed-Mode Order] (D, C and S descriptors,
/// converted back to single-ended ports by the specification's Appendix A transformation),
/// [Begin Information]…[End Information] (skipped), [Network Data], [Noise Data] (skipped)
/// and [End]. Y and Z data are not normalised in version 2. Hybrid (G, H) data is not read.
/// </para>
/// <para>
/// S-parameters use the power-wave definition a = (V + RI)/(2√R), b = (V − RI)/(2√R) per
/// port. Data whose ports have different references is renormalized to one: the option
/// line's R (50 Ω when it has none, or when it lists one per port); the original references
/// are kept in <see cref="TouchstoneData.FileReferencesOhms"/>.
/// </para>
/// </summary>
public static class TouchstoneReader
{
    public static TouchstoneData ReadFile(string path)
    {
        string text = File.ReadAllText(path);
        int? ports = PortsFromExtension(path);
        if (IsVersion2(text)) return Read(text, ports);
        if (ports is null)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}': a version 1 file's port count is read from the extension (.s1p, .s2p, …).");
        return Read(text, ports);
    }

    /// <param name="ports">The port count a version 1 file needs (from its extension); a
    /// version 2 file takes its own from [Number of Ports] and only notes a disagreement.</param>
    public static TouchstoneData Read(string text, int? ports)
    {
        if (ports is < 1) throw new ArgumentOutOfRangeException(nameof(ports));
        if (IsVersion2(text)) return ReadVersion2(text, ports);
        if (ports is not { } count)
            throw new InvalidDataException("A version 1 Touchstone file needs its port count (from the extension).");
        return ReadVersion1(text, count);
    }

    private static int? PortsFromExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension.Length >= 4 && extension[1] == 's' && extension[^1] == 'p'
               && int.TryParse(extension[2..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1
            ? n
            : null;
    }

    /// <summary>A version 2 file opens with [Version] before anything else that is not a
    /// comment or blank.</summary>
    private static bool IsVersion2(string text)
    {
        foreach (string raw in SplitLines(text))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            return line.StartsWith("[version]", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string StripComment(string line)
    {
        int comment = line.IndexOf('!');
        return comment >= 0 ? line[..comment] : line;
    }

    private static double Number(string token, string where)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new InvalidDataException($"'{token}' in the Touchstone {where} is not a number.");
        return value;
    }

    private sealed class Options
    {
        public double Unit = 1e9;
        public string Kind = "S";
        public string Format = "MA";
        public List<double> References = new() { 50 };
        public bool ReferenceGiven;
    }

    /// <summary>The option line's tokens (after the #). Version 1.1 and 2.1 allow R with one
    /// value per port, last on the line.</summary>
    private static Options ParseOptions(string line)
    {
        var options = new Options();
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i].ToUpperInvariant();
            switch (token)
            {
                case "HZ": options.Unit = 1; break;
                case "KHZ": options.Unit = 1e3; break;
                case "MHZ": options.Unit = 1e6; break;
                case "GHZ": options.Unit = 1e9; break;
                case "S": case "Y": case "Z": options.Kind = token; break;
                case "G": case "H":
                    throw new InvalidDataException($"Touchstone {token}-parameter files are not read.");
                case "RI": case "MA": case "DB": options.Format = token; break;
                case "R":
                    options.References = new List<double>();
                    while (i + 1 < tokens.Length && double.TryParse(tokens[i + 1], NumberStyles.Float,
                               CultureInfo.InvariantCulture, out double value))
                    {
                        if (!(value > 0))
                            throw new InvalidDataException("The option line's reference resistances must be positive.");
                        options.References.Add(value);
                        i++;
                    }
                    if (options.References.Count == 0)
                        throw new InvalidDataException("The option line's R is not followed by a reference resistance.");
                    options.ReferenceGiven = true;
                    break;
                default:
                    throw new InvalidDataException($"Unknown token '{tokens[i]}' on the Touchstone option line.");
            }
        }
        return options;
    }

    private static Complex Pair(string format, double a, double b) => format switch
    {
        "RI" => new Complex(a, b),
        "MA" => Complex.FromPolarCoordinates(a, b * Math.PI / 180),
        _ => Complex.FromPolarCoordinates(Math.Pow(10, a / 20), b * Math.PI / 180)
    };

    // ---------------- Version 1 ----------------

    private static TouchstoneData ReadVersion1(string text, int ports)
    {
        Options? options = null;
        var numbers = new List<double>();
        foreach (string raw in SplitLines(text))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
                throw new InvalidDataException(
                    "Touchstone keyword lines (such as [Number of Ports]) belong to version 2 files, which open with [Version].");
            if (line.StartsWith('#'))
            {
                options ??= ParseOptions(line[1..]);   // only the first option line counts
                continue;
            }
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                numbers.Add(Number(token, "data"));
        }
        options ??= new Options();

        bool perPort = options.References.Count > 1;
        if (perPort && options.References.Count != ports)
            throw new InvalidDataException(
                $"The option line gives {options.References.Count} reference resistances for a {ports}-port file.");
        if (perPort && options.Kind != "S")
            throw new InvalidDataException(
                "Version 1.1 Y- and Z-parameter data is normalised to per-port references, and the specification does " +
                "not say how; only S-parameters are read with one reference per port.");

        int perPoint = 1 + 2 * ports * ports;
        var frequencies = new List<double>();
        var matrices = new List<Complex[,]>();
        for (int at = 0; at + perPoint <= numbers.Count; at += perPoint)
        {
            double f = numbers[at] * options.Unit;
            // A two-port file may continue with noise parameters: their frequencies start
            // again from below the last network frequency.
            if (frequencies.Count > 0 && f <= frequencies[^1]) break;
            var m = new Complex[ports, ports];
            for (int k = 0; k < ports * ports; k++)
            {
                // Two ports: S11 S21 S12 S22. Every other size: row by row.
                int row = ports == 2 ? k % 2 : k / ports;
                int column = ports == 2 ? k / 2 : k % ports;
                m[row, column] = Pair(options.Format, numbers[at + 1 + 2 * k], numbers[at + 2 + 2 * k]);
            }
            frequencies.Add(f);
            double reference = options.References[0];
            matrices.Add(options.Kind switch
            {
                "Z" => NetworkParameters.ImpedanceToScattering(Scale(m, reference), reference),
                "Y" => NetworkChecks.AdmittanceToScattering(Scale(m, 1 / reference), reference),
                _ => m
            });
        }
        if (frequencies.Count == 0)
            throw new InvalidDataException($"No {ports}-port network data was found in the Touchstone text.");
        if (!perPort)
            return new TouchstoneData(frequencies, matrices, options.References[0]);
        return Renormalized(frequencies, matrices, options.References, 50, "1.1", new List<string>());
    }

    // ---------------- Version 2 ----------------

    private enum Section { Header, Information, NetworkData, NoiseData, End }

    private static TouchstoneData ReadVersion2(string text, int? portsFromName)
    {
        string? version = null;
        Options? options = null;
        int ports = 0;
        string? twoPortOrder = null;
        int frequencyCount = 0, noiseCount = 0;
        string matrixFormat = "FULL";
        List<double>? references = null;
        List<string>? mixedOrder = null;
        bool informationSeen = false, networkSeen = false, noiseSeen = false;
        var section = Section.Header;
        var numbers = new List<double>();
        // A keyword whose arguments may continue on the following lines.
        string? continuing = null;
        var seen = new HashSet<string>();

        foreach (string raw in SplitLines(text))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (section == Section.End)
                throw new InvalidDataException($"'{line}' follows [End]; nothing but comments may.");

            if (line.StartsWith('['))
            {
                int close = line.IndexOf(']');
                if (close < 0) throw new InvalidDataException($"'{line}': a keyword's closing bracket is missing.");
                string keyword = string.Join(' ', line[1..close].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .ToLowerInvariant();
                string argument = line[(close + 1)..].Trim();
                if (section == Section.Information && keyword != "end information") continue;
                CheckComplete(continuing, references, mixedOrder);
                continuing = null;
                if (version is null && keyword != "version")
                    throw new InvalidDataException("A version 2 Touchstone file must open with [Version].");
                if (keyword is not ("begin information" or "end information") && !seen.Add(keyword))
                    throw new InvalidDataException($"[{keyword}] appears more than once.");
                if (keyword is not ("version" or "number of ports") && version is not null && ports == 0)
                    throw new InvalidDataException("[Number of Ports] must be the first keyword after the option line.");
                bool header = keyword is "two-port data order" or "number of frequencies" or "number of noise frequencies"
                    or "reference" or "matrix format" or "mixed-mode order" or "begin information" or "end information";
                if (header && section != Section.Header && !(section == Section.Information && keyword == "end information"))
                    throw new InvalidDataException($"[{keyword}] must come before [Network Data].");

                switch (keyword)
                {
                    case "version":
                        if (argument is not ("2.0" or "2.1"))
                            throw new InvalidDataException($"[Version] {argument}: this reader knows versions 2.0 and 2.1.");
                        version = argument;
                        break;
                    case "number of ports":
                        if (options is null) throw new InvalidDataException("The option line must come before [Number of Ports].");
                        ports = PositiveInteger(argument, keyword);
                        break;
                    case "two-port data order":
                        twoPortOrder = argument.ToUpperInvariant();
                        if (twoPortOrder is not ("12_21" or "21_12"))
                            throw new InvalidDataException($"[Two-Port Data Order] takes 12_21 or 21_12, not '{argument}'.");
                        break;
                    case "number of frequencies":
                        frequencyCount = PositiveInteger(argument, keyword);
                        break;
                    case "number of noise frequencies":
                        noiseCount = PositiveInteger(argument, keyword);
                        break;
                    case "reference":
                        references = new List<double>();
                        AddReferences(references, argument);
                        continuing = keyword;
                        break;
                    case "matrix format":
                        matrixFormat = argument.ToUpperInvariant();
                        if (matrixFormat is not ("FULL" or "LOWER" or "UPPER"))
                            throw new InvalidDataException($"[Matrix Format] takes Full, Lower or Upper, not '{argument}'.");
                        break;
                    case "mixed-mode order":
                        mixedOrder = new List<string>();
                        mixedOrder.AddRange(argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        continuing = keyword;
                        break;
                    case "begin information":
                        if (informationSeen) throw new InvalidDataException("[Begin Information] appears more than once.");
                        informationSeen = true;
                        section = Section.Information;
                        break;
                    case "end information":
                        if (section != Section.Information)
                            throw new InvalidDataException("[End Information] without [Begin Information].");
                        section = Section.Header;
                        break;
                    case "network data":
                        section = Section.NetworkData;
                        networkSeen = true;
                        break;
                    case "noise data":
                        if (!networkSeen) throw new InvalidDataException("[Noise Data] must follow [Network Data].");
                        section = Section.NoiseData;
                        noiseSeen = true;
                        break;
                    case "end":
                        section = Section.End;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown Touchstone keyword [{line[1..close]}].");
                }
                if (argument.Length > 0 && keyword is "begin information" or "end information" or "network data"
                        or "noise data" or "end")
                    throw new InvalidDataException($"[{keyword}] takes no argument; '{argument}' follows it.");
                continue;
            }

            if (section == Section.Information) continue;
            if (line.StartsWith('#'))
            {
                if (options is null)
                {
                    if (version is null) throw new InvalidDataException("A version 2 Touchstone file must open with [Version].");
                    options = ParseOptions(line[1..]);
                }
                continue;   // option lines after the first are ignored
            }
            switch (continuing)
            {
                case "reference":
                    AddReferences(references!, line);
                    continue;
                case "mixed-mode order":
                    mixedOrder!.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    continue;
            }
            switch (section)
            {
                case Section.NetworkData:
                    foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        numbers.Add(Number(token, "network data"));
                    break;
                case Section.NoiseData:
                    break;
                default:
                    throw new InvalidDataException($"'{line}' is neither a keyword nor network data.");
            }
        }

        if (version is null) throw new InvalidDataException("A version 2 Touchstone file must open with [Version].");
        if (options is null) throw new InvalidDataException("The option line (#) is missing.");
        if (ports == 0) throw new InvalidDataException("[Number of Ports] is missing.");
        if (frequencyCount == 0) throw new InvalidDataException("[Number of Frequencies] is missing.");
        if (!networkSeen) throw new InvalidDataException("[Network Data] is missing.");
        if (section != Section.End) throw new InvalidDataException("[End] is missing.");
        if (ports != 2 && twoPortOrder is not null)
            throw new InvalidDataException("[Two-Port Data Order] is only allowed in a two-port file.");
        if ((noiseCount > 0) != noiseSeen)
            throw new InvalidDataException("[Number of Noise Frequencies] and [Noise Data] go together.");
        if (noiseSeen && ports != 2) throw new InvalidDataException("Noise data is only allowed in a two-port file.");
        if (options.References.Count > 1)
            throw new InvalidDataException("A version 2 file gives per-port references with [Reference], not on the option line.");
        if (references is not null && references.Count != ports)
            throw new InvalidDataException($"[Reference] gives {references.Count} resistances for {ports} ports.");

        var notes = new List<string>();
        if (ports == 2 && twoPortOrder is null)
        {
            // Required by the specification, yet its own Example 20 leaves it out.
            twoPortOrder = "21_12";
            notes.Add("[Two-Port Data Order] is missing; the version 1 order (N11 N21 N12 N22) was assumed.");
        }
        if (portsFromName is { } named && named != ports)
            notes.Add($"The file name says {named} ports; [Number of Ports] says {ports}, which is what was read.");

        var descriptors = mixedOrder is null ? null : MixedModeDescriptor.Parse(mixedOrder, ports);
        if (descriptors is not null && options.Kind == "S")
            foreach (var d in descriptors.Where(d => d.Mode != 'S'))
                if (references is not null && references[d.Plus] != references[d.Minus])
                    throw new InvalidDataException(
                        $"Ports {d.Plus + 1} and {d.Minus + 1} form a mixed-mode pair, so their [Reference] values must be equal.");

        bool triangle = matrixFormat != "FULL";
        int perPoint = 1 + (triangle ? ports * ports + ports : 2 * ports * ports);
        if (numbers.Count != frequencyCount * perPoint)
            throw new InvalidDataException(
                $"[Number of Frequencies] {frequencyCount} needs {frequencyCount * perPoint} values under [Network Data] " +
                $"({perPoint} per frequency); the file has {numbers.Count}.");

        double common = options.References[0];
        var perPortReferences = references ?? Enumerable.Repeat(common, ports).ToList();
        var frequencies = new List<double>(frequencyCount);
        var matrices = new List<Complex[,]>(frequencyCount);
        for (int p = 0; p < frequencyCount; p++)
        {
            int at = p * perPoint;
            double f = numbers[at] * options.Unit;
            if (frequencies.Count > 0 && !(f > frequencies[^1]))
                throw new InvalidDataException("The network data's frequencies must rise.");
            var m = new Complex[ports, ports];
            int k = at + 1;
            Complex Next()
            {
                var value = Pair(options.Format, numbers[k], numbers[k + 1]);
                k += 2;
                return value;
            }
            if (!triangle)
            {
                if (ports == 2 && twoPortOrder == "21_12")
                {
                    m[0, 0] = Next(); m[1, 0] = Next(); m[0, 1] = Next(); m[1, 1] = Next();
                }
                else
                    for (int i = 0; i < ports; i++)
                        for (int j = 0; j < ports; j++) m[i, j] = Next();
            }
            else
                for (int i = 0; i < ports; i++)
                    for (int j = 0; j < ports; j++)
                    {
                        bool stored = matrixFormat == "LOWER" ? j <= i : j >= i;
                        if (stored) m[i, j] = m[j, i] = Next();
                    }
            frequencies.Add(f);
            if (descriptors is not null) m = MixedModeDescriptor.ToSingleEnded(m, descriptors, options.Kind);
            matrices.Add(options.Kind switch
            {
                // Version 2 Y and Z are in siemens and ohms, not normalised.
                "Z" => NetworkParameters.ImpedanceToScattering(m, common),
                "Y" => NetworkChecks.AdmittanceToScattering(m, common),
                _ => m
            });
        }
        if (descriptors is not null)
            notes.Add($"Mixed-mode data ({string.Join(' ', mixedOrder!)}) converted to the {ports} single-ended ports.");
        if (options.Kind != "S")
            notes.Add($"{options.Kind}-parameters (not normalised in version 2) converted to S in {common:g4} Ω.");

        if (options.Kind != "S" || perPortReferences.All(r => r == common))
            return new TouchstoneData(frequencies, matrices, common) { Version = version, Notes = notes };
        return Renormalized(frequencies, matrices, perPortReferences, common, version, notes);
    }

    private static int PositiveInteger(string argument, string keyword)
    {
        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1)
            throw new InvalidDataException($"[{keyword}] takes a positive integer, not '{argument}'.");
        return value;
    }

    private static void AddReferences(List<double> references, string text)
    {
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            double value = Number(token, "[Reference]");
            if (!(value > 0)) throw new InvalidDataException("[Reference] resistances must be positive.");
            references.Add(value);
        }
    }

    private static void CheckComplete(string? continuing, List<double>? references, List<string>? mixedOrder)
    {
        if (continuing == "reference" && references!.Count == 0)
            throw new InvalidDataException("[Reference] has no resistances.");
        if (continuing == "mixed-mode order" && mixedOrder!.Count == 0)
            throw new InvalidDataException("[Mixed-Mode Order] has no descriptors.");
    }

    /// <summary>
    /// S in per-port references R_i renormalized to one reference R₀, through the port
    /// voltages and currents: with D = diag(√R_i), V = D(I + S)a and I = D⁻¹(I − S)a, the new
    /// waves are a′ = ½D₀⁻¹(V + R₀I) = P·a and b′ = ½D₀⁻¹(V − R₀I) = Q·a, so S′ = Q·P⁻¹ —
    /// which needs neither Z nor Y to exist.
    /// </summary>
    public static Complex[,] Renormalize(Complex[,] s, IReadOnlyList<double> fromOhms, double toOhms)
    {
        int n = s.GetLength(0);
        var p = new Complex[n, n];
        var q = new Complex[n, n];
        double root = Math.Sqrt(toOhms);
        for (int i = 0; i < n; i++)
        {
            double di = Math.Sqrt(fromOhms[i]);
            for (int j = 0; j < n; j++)
            {
                Complex plus = (i == j ? 1 : 0) + s[i, j], minus = (i == j ? 1 : 0) - s[i, j];
                Complex v = di * plus, current = minus / di;
                p[i, j] = 0.5 * (v + toOhms * current) / root;
                q[i, j] = 0.5 * (v - toOhms * current) / root;
            }
        }
        return NetworkParameters.Multiply(q, NetworkParameters.Invert(p));
    }

    private static TouchstoneData Renormalized(List<double> frequencies, List<Complex[,]> matrices,
        IReadOnlyList<double> references, double common, string version, List<string> notes)
    {
        var renormalized = matrices.Select(m => Renormalize(m, references, common)).ToList();
        notes.Add($"The file's port references ({string.Join(", ", references.Select(r => r.ToString("g4", CultureInfo.InvariantCulture)))} Ω) " +
                  $"were renormalized to {common:g4} Ω at every port.");
        return new TouchstoneData(frequencies, renormalized, common)
        {
            Version = version, FileReferencesOhms = references.ToArray(), Notes = notes
        };
    }

    private static Complex[,] Scale(Complex[,] m, double factor)
    {
        int n = m.GetLength(0);
        var result = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                result[i, j] = m[i, j] * factor;
        return result;
    }
}

/// <summary>
/// One entry of a Touchstone [Mixed-Mode Order]: S&lt;p&gt; a single-ended port, D&lt;p,q&gt;
/// and C&lt;p,q&gt; the differential and common mode of ports p and q, q the reference
/// ("−") port: V_D = V_p − V_q, I_D = (I_p − I_q)/2, V_C = (V_p + V_q)/2, I_C = I_p + I_q, and
/// with equal references at p and q (2R for the differential mode, R/2 for the common one)
/// a_D = (a_p − a_q)/√2, a_C = (a_p + a_q)/√2.
/// </summary>
public readonly record struct MixedModeDescriptor(char Mode, int Plus, int Minus)
{
    public static IReadOnlyList<MixedModeDescriptor> Parse(IReadOnlyList<string> tokens, int ports)
    {
        var descriptors = new List<MixedModeDescriptor>();
        foreach (string raw in tokens)
        {
            string token = raw.ToUpperInvariant();
            char mode = token[0];
            string[] parts = token[1..].Split(',');
            bool ok = mode is 'S' or 'D' or 'C' && parts.Length == (mode == 'S' ? 1 : 2);
            var numbers = new int[parts.Length];
            for (int i = 0; ok && i < parts.Length; i++)
                ok = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])
                     && numbers[i] >= 1 && numbers[i] <= ports;
            if (!ok) throw new InvalidDataException($"'{raw}' is not a [Mixed-Mode Order] descriptor for a {ports}-port file.");
            descriptors.Add(new MixedModeDescriptor(mode, numbers[0] - 1, mode == 'S' ? -1 : numbers[1] - 1));
        }
        if (descriptors.Count != ports)
            throw new InvalidDataException($"[Mixed-Mode Order] lists {descriptors.Count} descriptors for {ports} ports.");
        var uses = new int[ports];
        foreach (var d in descriptors)
        {
            uses[d.Plus]++;
            if (d.Mode != 'S')
            {
                if (d.Plus == d.Minus) throw new InvalidDataException($"A mixed-mode pair needs two ports: {d.Mode}{d.Plus + 1},{d.Minus + 1}.");
                uses[d.Minus]++;
                char other = d.Mode == 'D' ? 'C' : 'D';
                if (!descriptors.Contains(new MixedModeDescriptor(other, d.Plus, d.Minus)))
                    throw new InvalidDataException(
                        $"{d.Mode}{d.Plus + 1},{d.Minus + 1} needs {other}{d.Plus + 1},{d.Minus + 1} as well.");
            }
        }
        for (int p = 0; p < ports; p++)
        {
            bool single = descriptors.Contains(new MixedModeDescriptor('S', p, -1));
            if (single ? uses[p] != 1 : uses[p] != 2)
                throw new InvalidDataException($"Port {p + 1} must appear once as S or in one D and C pair under [Mixed-Mode Order].");
        }
        return descriptors;
    }

    /// <summary>
    /// The single-ended matrix from a mixed-mode one (the specification's Appendix A). With
    /// X_mm = T_X·X and U_mm = T_U·U for the stimulus X and response U (a and b for S, V and I
    /// for Y, I and V for Z), F = T_U⁻¹·F_mm·T_X.
    /// </summary>
    public static Complex[,] ToSingleEnded(Complex[,] mixed, IReadOnlyList<MixedModeDescriptor> order, string kind)
    {
        int n = order.Count;
        var voltage = new double[n, n];   // rows of T for waves (S), voltages (V) and currents (I)
        var current = new double[n, n];
        var wave = new double[n, n];
        double r = Math.Sqrt(0.5);
        for (int row = 0; row < n; row++)
        {
            var d = order[row];
            switch (d.Mode)
            {
                case 'S':
                    voltage[row, d.Plus] = current[row, d.Plus] = wave[row, d.Plus] = 1;
                    break;
                case 'D':
                    voltage[row, d.Plus] = 1; voltage[row, d.Minus] = -1;
                    current[row, d.Plus] = 0.5; current[row, d.Minus] = -0.5;
                    wave[row, d.Plus] = r; wave[row, d.Minus] = -r;
                    break;
                default:
                    voltage[row, d.Plus] = 0.5; voltage[row, d.Minus] = 0.5;
                    current[row, d.Plus] = 1; current[row, d.Minus] = 1;
                    wave[row, d.Plus] = r; wave[row, d.Minus] = r;
                    break;
            }
        }
        var (input, output) = kind switch
        {
            "Y" => (voltage, current),
            "Z" => (current, voltage),
            _ => (wave, wave)
        };
        var outputInverse = NetworkParameters.Invert(ToComplex(output));
        return NetworkParameters.Multiply(NetworkParameters.Multiply(outputInverse, mixed), ToComplex(input));
    }

    private static Complex[,] ToComplex(double[,] m)
    {
        int n = m.GetLength(0);
        var c = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) c[i, j] = m[i, j];
        return c;
    }
}

/// <summary>Conversions between impedance and scattering matrices for one real reference
/// resistance at every port.</summary>
public static class NetworkParameters
{
    /// <summary>S = (Z − Z₀)(Z + Z₀)⁻¹.</summary>
    public static Complex[,] ImpedanceToScattering(Complex[,] z, double referenceOhms)
    {
        int n = z.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = z[i, j] + (i == j ? referenceOhms : 0);
                minus[i, j] = z[i, j] - (i == j ? referenceOhms : 0);
            }
        return Multiply(minus, Invert(plus));
    }

    /// <summary>Z = Z₀(I + S)(I − S)⁻¹.</summary>
    public static Complex[,] ScatteringToImpedance(Complex[,] s, double referenceOhms)
    {
        int n = s.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = (i == j ? 1 : 0) + s[i, j];
                minus[i, j] = (i == j ? 1 : 0) - s[i, j];
            }
        var z = Multiply(plus, Invert(minus));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                z[i, j] *= referenceOhms;
        return z;
    }

    public static Complex[,] Multiply(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1), inner = b.GetLength(0);
        var c = new Complex[n, m];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < inner; k++)
            {
                Complex aik = a[i, k];
                if (aik == Complex.Zero) continue;
                for (int j = 0; j < m; j++) c[i, j] += aik * b[k, j];
            }
        return c;
    }

    /// <summary>Inverse by Gauss–Jordan elimination with partial pivoting (port-count sizes).</summary>
    public static Complex[,] Invert(Complex[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (Complex[,])matrix.Clone();
        var inverse = new Complex[n, n];
        for (int i = 0; i < n; i++) inverse[i, i] = 1;
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++)
                if (a[i, k].Magnitude > a[pivot, k].Magnitude) pivot = i;
            if (a[pivot, k] == Complex.Zero)
                throw new InvalidOperationException("The network matrix is singular.");
            if (pivot != k)
                for (int j = 0; j < n; j++)
                {
                    (a[k, j], a[pivot, j]) = (a[pivot, j], a[k, j]);
                    (inverse[k, j], inverse[pivot, j]) = (inverse[pivot, j], inverse[k, j]);
                }
            Complex scale = 1 / a[k, k];
            for (int j = 0; j < n; j++) { a[k, j] *= scale; inverse[k, j] *= scale; }
            for (int i = 0; i < n; i++)
            {
                if (i == k) continue;
                Complex factor = a[i, k];
                if (factor == Complex.Zero) continue;
                for (int j = 0; j < n; j++)
                {
                    a[i, j] -= factor * a[k, j];
                    inverse[i, j] -= factor * inverse[k, j];
                }
            }
        }
        return inverse;
    }
}
