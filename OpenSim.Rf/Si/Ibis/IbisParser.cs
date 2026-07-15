using System.Globalization;

namespace OpenSim.Rf.Si.Ibis;

/// <summary>
/// A first-party IBIS (.ibs) reader for the behavioral I/O-buffer subset the SI transient
/// engine consumes (Stage S11): [Model] blocks with Model_type, C_comp, the V-I tables
/// ([Pullup]/[Pulldown]/[GND Clamp]/[POWER Clamp]), [Ramp], [Rising/Falling Waveform] V-T
/// tables, and the reference/range keywords. Every numeric column is a min/typ/max triple
/// with "NA" mapped to null; SPICE-style scale suffixes (T/G/M/k/m/u/n/p/f, case-sensitive
/// M = mega vs m = milli) are honored and trailing units (V/A/F/s/ohm) ignored. Comments
/// begin with '|'. Structural keywords not needed by the engine ([Pin], [Package],
/// [Model Selector], …) are skipped with a warning — never silently, mirroring the Gerber
/// reader's "warn, don't misrender" rule. A malformed numeric row in a table is a loud
/// typed failure naming the line.
/// </summary>
public sealed class IbisParser
{
    public IbisFile ParseFile(string path) => Parse(File.ReadAllText(path));

    // Sub-sections a data line can belong to.
    private enum Section { None, Pullup, Pulldown, GndClamp, PowerClamp, Ramp, Rising, Falling }

    public IbisFile Parse(string content)
    {
        var warnings = new List<string>();
        var models = new List<IbisModel>();
        string? component = null;

        ModelBuilder? model = null;
        var section = Section.None;
        WaveformBuilder? waveform = null;

        void FlushWaveform()
        {
            if (model is null || waveform is null) return;
            var wf = new IbisWaveform(waveform.RFixture, waveform.VFixture, waveform.Rows);
            (section == Section.Rising ? model.Rising : model.Falling).Add(wf);
            waveform = null;
        }
        void FlushModel()
        {
            FlushWaveform();
            if (model is not null) models.Add(model.Build());
            model = null;
            section = Section.None;
        }

        var lines = content.Split('\n');
        for (int lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var line = StripComment(lines[lineNo]).Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                FlushWaveform();
                var (keyword, argument) = SplitKeyword(line);
                switch (keyword.ToLowerInvariant())
                {
                    case "component": component = argument; break;
                    case "model":
                        FlushModel();
                        model = new ModelBuilder { Name = argument };
                        section = Section.None;
                        break;
                    case "pullup": section = Section.Pullup; break;
                    case "pulldown": section = Section.Pulldown; break;
                    case "gnd clamp": section = Section.GndClamp; break;
                    case "power clamp": section = Section.PowerClamp; break;
                    case "ramp": section = Section.Ramp; break;
                    case "rising waveform":
                        section = Section.Rising; waveform = new WaveformBuilder(); break;
                    case "falling waveform":
                        section = Section.Falling; waveform = new WaveformBuilder(); break;
                    case "voltage range":
                        if (model is not null) model.VoltageRange = ParseCorner(argument, line); break;
                    case "pullup reference":
                        if (model is not null) model.PullupRef = ParseNumber(FirstToken(argument), line); break;
                    case "pulldown reference":
                        if (model is not null) model.PulldownRef = ParseNumber(FirstToken(argument), line); break;
                    case "gnd clamp reference":
                        if (model is not null) model.GndClampRef = ParseNumber(FirstToken(argument), line); break;
                    case "power clamp reference":
                        if (model is not null) model.PowerClampRef = ParseNumber(FirstToken(argument), line); break;
                    case "end":
                        FlushModel();
                        return new IbisFile(component, models, warnings);
                    default:
                        // A new top-level keyword ends any table section; note the ones we skip.
                        section = Section.None;
                        if (keyword is not ("ibis ver" or "file name" or "file rev" or "date"
                            or "source" or "notes" or "disclaimer" or "copyright" or "manufacturer"))
                            warnings.Add($"IBIS: skipped unsupported keyword [{keyword}] (line {lineNo + 1}).");
                        break;
                }
                continue;
            }

            if (model is null) continue;   // data before the first [Model] — header noise
            var first = FirstToken(line);
            var firstLower = first.ToLowerInvariant();

            // Model sub-parameters (unbracketed key/value lines).
            if (firstLower == "model_type") { model.ModelType = Rest(line); continue; }
            if (firstLower == "c_comp") { model.CComp = ParseCorner(Rest(line), line); continue; }
            if (section is Section.Rising or Section.Falling && waveform is not null)
            {
                if (firstLower == "r_fixture") { waveform.RFixture = ReqNum(Rest(line), line); continue; }
                if (firstLower == "v_fixture") { waveform.VFixture = ReqNum(Rest(line), line); continue; }
                if (firstLower is "c_fixture" or "l_fixture" or "r_dut" or "c_dut" or "l_dut") continue;
            }
            if (section == Section.Ramp)
            {
                if (firstLower == "dv/dt_r") { model.RampRise = ParseRampEdge(Rest(line), line); continue; }
                if (firstLower == "dv/dt_f") { model.RampFall = ParseRampEdge(Rest(line), line); continue; }
                if (firstLower is "r_load") continue;
            }

            // A numeric data row inside a table section.
            if (section is Section.Pullup or Section.Pulldown or Section.GndClamp or Section.PowerClamp)
            {
                var (v, c) = ParseIvRow(line, lineNo + 1);
                var target = section switch
                {
                    Section.Pullup => model.Pullup,
                    Section.Pulldown => model.Pulldown,
                    Section.GndClamp => model.GndClamp,
                    _ => model.PowerClamp,
                };
                target.Add(new IbisIvRow(v, c));
                continue;
            }
            if (section is Section.Rising or Section.Falling && waveform is not null)
            {
                var (t, v) = ParseVtRow(line, lineNo + 1);
                waveform.Rows.Add(new IbisVtRow(t, v));
                continue;
            }
            // Any other unbracketed line inside a model is an informational sub-parameter.
        }

        FlushModel();
        return new IbisFile(component, models, warnings);
    }

    // ------------------------------------------------------------------
    // Row / value parsing.
    // ------------------------------------------------------------------

    private static (double V, IbisCorner C) ParseIvRow(string line, int lineNo)
    {
        var t = Tokens(line);
        if (t.Length < 2)
            throw new InvalidDataException($"IBIS V-I row (line {lineNo}) '{line}' needs a voltage and at least one current.");
        double v = ReqNum(t[0], line);
        return (v, ReadCorner(t, 1, line));
    }

    private static (double T, IbisCorner V) ParseVtRow(string line, int lineNo)
    {
        var t = Tokens(line);
        if (t.Length < 2)
            throw new InvalidDataException($"IBIS V-T row (line {lineNo}) '{line}' needs a time and at least one voltage.");
        double time = ReqNum(t[0], line);
        return (time, ReadCorner(t, 1, line));
    }

    /// <summary>Reads a typ/min/max triple from tokens starting at <paramref name="start"/>
    /// (IBIS column order is typ, min, max; missing/NA → null).</summary>
    private static IbisCorner ReadCorner(string[] t, int start, string line)
    {
        double? typ = start < t.Length ? ParseNumber(t[start], line) : null;
        double? min = start + 1 < t.Length ? ParseNumber(t[start + 1], line) : null;
        double? max = start + 2 < t.Length ? ParseNumber(t[start + 2], line) : null;
        return new IbisCorner(typ, min, max);
    }

    private static IbisCorner ParseCorner(string text, string line) => ReadCorner(Tokens(text), 0, line);

    private static IbisRampEdge ParseRampEdge(string text, string line)
    {
        // "2.20/1.06n   1.99/1.32n   2.42/0.91n" — each corner is Δv/Δt.
        var cols = Tokens(text);
        double?[] dv = new double?[3], dt = new double?[3];
        for (int i = 0; i < 3 && i < cols.Length; i++)
        {
            if (cols[i].Equals("NA", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = cols[i].Split('/');
            if (parts.Length != 2)
                throw new InvalidDataException($"IBIS [Ramp] value '{cols[i]}' (line '{line}') is not Δv/Δt.");
            dv[i] = ReqNum(parts[0], line);
            dt[i] = ReqNum(parts[1], line);
        }
        return new IbisRampEdge(new IbisCorner(dv[0], dv[1], dv[2]), new IbisCorner(dt[0], dt[1], dt[2]));
    }

    /// <summary>Parses one IBIS numeric token (with an optional scale suffix + unit), or null
    /// for "NA". Scale letters are case-sensitive: M = mega (1e6), m = milli (1e-3), f = femto
    /// (1e-15) — an uppercase F is the Farad UNIT, ignored. Trailing units (V/A/F/s/ohm) after
    /// the scale letter are ignored.</summary>
    internal static double? ParseNumber(string token, string line)
    {
        token = token.Trim();
        if (token.Length == 0 || token.Equals("NA", StringComparison.OrdinalIgnoreCase)) return null;
        int i = 0;
        if (i < token.Length && (token[i] == '+' || token[i] == '-')) i++;
        while (i < token.Length && (char.IsDigit(token[i]) || token[i] == '.')) i++;
        if (i < token.Length && (token[i] == 'e' || token[i] == 'E'))
        {
            i++;
            if (i < token.Length && (token[i] == '+' || token[i] == '-')) i++;
            while (i < token.Length && char.IsDigit(token[i])) i++;
        }
        if (!double.TryParse(token[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new InvalidDataException($"IBIS number '{token}' (line '{line}') is not numeric.");
        if (i < token.Length)
        {
            value *= token[i] switch
            {
                'T' => 1e12, 'G' => 1e9, 'M' => 1e6, 'k' => 1e3, 'K' => 1e3,
                'm' => 1e-3, 'u' => 1e-6, 'n' => 1e-9, 'p' => 1e-12, 'f' => 1e-15,
                _ => 1.0,   // a unit letter (V/A/F/s/o…), no scaling
            };
        }
        return value;
    }

    private static double ReqNum(string token, string line) =>
        ParseNumber(token, line) ?? throw new InvalidDataException(
            $"IBIS value '{token}' (line '{line}') must be a number, not NA.");

    // ------------------------------------------------------------------
    // Small text helpers.
    // ------------------------------------------------------------------

    private static string StripComment(string line)
    {
        int bar = line.IndexOf('|');
        return bar < 0 ? line : line[..bar];
    }

    private static (string Keyword, string Argument) SplitKeyword(string line)
    {
        int close = line.IndexOf(']');
        if (close < 0) return (line.Trim('[', ' ').ToLowerInvariant(), "");
        string keyword = line[1..close].Trim();
        string argument = line[(close + 1)..].Trim();
        return (keyword, argument);
    }

    private static string[] Tokens(string line) =>
        line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);

    private static string FirstToken(string line)
    {
        var t = Tokens(line);
        return t.Length > 0 ? t[0] : "";
    }

    private static string Rest(string line)
    {
        var t = Tokens(line);
        return t.Length > 1 ? string.Join(' ', t[1..]) : "";
    }

    // ------------------------------------------------------------------
    // Mutable builders.
    // ------------------------------------------------------------------

    private sealed class WaveformBuilder
    {
        public double RFixture = 50;
        public double VFixture;
        public List<IbisVtRow> Rows { get; } = new();
    }

    private sealed class ModelBuilder
    {
        public string Name = "";
        public string ModelType = "";
        public IbisCorner CComp;
        public List<IbisIvRow> Pullup { get; } = new();
        public List<IbisIvRow> Pulldown { get; } = new();
        public List<IbisIvRow> GndClamp { get; } = new();
        public List<IbisIvRow> PowerClamp { get; } = new();
        public IbisRampEdge? RampRise;
        public IbisRampEdge? RampFall;
        public List<IbisWaveform> Rising { get; } = new();
        public List<IbisWaveform> Falling { get; } = new();
        public IbisCorner? VoltageRange;
        public double? PullupRef, PulldownRef, GndClampRef, PowerClampRef;

        public IbisModel Build() => new()
        {
            Name = Name,
            ModelType = ModelType,
            CComp = CComp,
            Pullup = Pullup,
            Pulldown = Pulldown,
            GndClamp = GndClamp,
            PowerClamp = PowerClamp,
            Ramp = RampRise is not null || RampFall is not null
                ? new IbisRamp(RampRise ?? Zero(), RampFall ?? Zero())
                : null,
            RisingWaveforms = Rising,
            FallingWaveforms = Falling,
            VoltageRange = VoltageRange,
            PullupReferenceVolts = PullupRef,
            PulldownReferenceVolts = PulldownRef,
            GndClampReferenceVolts = GndClampRef,
            PowerClampReferenceVolts = PowerClampRef,
        };

        private static IbisRampEdge Zero() =>
            new(new IbisCorner(0, 0, 0), new IbisCorner(1, 1, 1));
    }
}
