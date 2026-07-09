using System.Globalization;
using System.Text.RegularExpressions;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Gerber;

/// <summary>Options controlling Gerber parsing.</summary>
public sealed record GerberParseOptions
{
    /// <summary>Maximum chord deviation when tessellating arcs [m]. Default 5 µm.</summary>
    public double ChordTolerance { get; init; } = 5e-6;
}

/// <summary>
/// Parser for the Gerber RS-274X subset real EDA tools emit: FS (LA only), MO,
/// AD (C/R/O with optional holes, polygon P, %AM% macros with expressions — evaluated
/// at AD time), G01/G02/G03 with G75 arcs, D01/D02/D03, regions G36/G37, polarity
/// LPD/LPC, step-repeat %SR% (replayed at block close), comments, and M02. Attributes
/// (TF/TA/TO/TD) are metadata; anything genuinely unknown fails loudly. All coordinates
/// are converted to meters.
/// </summary>
public sealed partial class GerberParser
{
    private enum Interpolation { Linear, Clockwise, CounterClockwise }

    private readonly GerberParseOptions _options;

    public GerberParser(GerberParseOptions? options = null) => _options = options ?? new GerberParseOptions();

    public GerberDocument ParseFile(string filePath) => Parse(File.ReadAllText(filePath));

    public GerberDocument Parse(string content)
    {
        var state = new ParseState();
        foreach (var (statement, isExtended) in Tokenize(content))
        {
            if (isExtended) ParseExtended(statement, state);
            else ParseWord(statement, state);
            if (state.Ended) break;
        }
        if (!state.Ended)
            state.Warnings.Add("File ended without M02.");
        state.FlushDraw();
        CloseStepRepeat(state);                                   // an SR left open at EOF still replays

        return new GerberDocument
        {
            Apertures = state.Apertures,
            Ops = state.Ops,
            Warnings = state.Warnings
        };
    }

    // ---------------- Tokenization ----------------

    /// <summary>
    /// Splits the file into '*'-terminated statements; '%…%' blocks are extended
    /// commands (and may contain several inner statements, e.g. AM macro bodies —
    /// those are yielded as one block).
    /// </summary>
    private static IEnumerable<(string Statement, bool IsExtended)> Tokenize(string content)
    {
        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '%')
            {
                int end = content.IndexOf('%', i + 1);
                if (end < 0)
                    throw new InvalidDataException("Unterminated '%' extended command block.");
                yield return (Strip(content[(i + 1)..end]), true);
                i = end + 1;
            }
            else
            {
                int end = content.IndexOf('*', i);
                if (end < 0)
                    throw new InvalidDataException($"Statement without '*' terminator near '{content[i..Math.Min(i + 30, content.Length)]}'.");
                yield return (Strip(content[i..end]), false);
                i = end + 1;
            }
        }
    }

    private static string Strip(string s) =>
        string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    // ---------------- Extended (%…%) commands ----------------

    private void ParseExtended(string block, ParseState state)
    {
        // Blocks may hold multiple '*'-separated statements (e.g. AM bodies) —
        // dispatch on the first one.
        var statements = block.Split('*', StringSplitOptions.RemoveEmptyEntries);
        var first = statements[0];

        if (first.StartsWith("FS", StringComparison.Ordinal))
        {
            var m = FormatSpecRegex().Match(first);
            if (!m.Success)
                throw new InvalidDataException($"Unsupported format specification '{first}'. " +
                    "Only 'FSLAX<n><m>Y<n><m>' (leading-zero omission, absolute coordinates) is supported.");
            state.DecimalsX = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            state.DecimalsY = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        }
        else if (first.StartsWith("MO", StringComparison.Ordinal))
        {
            state.UnitScale = first[2..] switch
            {
                "MM" => 1e-3,
                "IN" => 0.0254,
                _ => throw new InvalidDataException($"Unknown unit mode '{first}'.")
            };
        }
        else if (first.StartsWith("AD", StringComparison.Ordinal))
        {
            ParseApertureDefinition(first, state);
        }
        else if (first.StartsWith("AM", StringComparison.Ordinal))
        {
            string name = first[2..];
            if (name.Length == 0)
                throw new InvalidDataException("Aperture macro (%AM%) without a name.");
            // The body statements are the rest of the block; they are stored raw and
            // evaluated at %ADD% time, when the actual parameters are known.
            state.Macros[name] = new MacroDefinition(name, statements[1..]);
        }
        else if (first is "LPD" or "LPC")
        {
            state.FlushDraw();
            state.Polarity = first == "LPD" ? GerberPolarity.Dark : GerberPolarity.Clear;
        }
        else if (first.StartsWith("SR", StringComparison.Ordinal))
        {
            // Per spec an SR statement always closes the open block (replaying it),
            // then a parameterized one opens the next — blocks cannot nest.
            state.FlushDraw();
            CloseStepRepeat(state);
            if (first.Length > 2)
                OpenStepRepeat(first[2..], state);
        }
        else if (first.StartsWith("TF", StringComparison.Ordinal) || first.StartsWith("TA", StringComparison.Ordinal)
                 || first.StartsWith("TO", StringComparison.Ordinal) || first.StartsWith("TD", StringComparison.Ordinal))
        {
            // File/aperture/object attributes carry metadata only, never image content.
        }
        else if (first.StartsWith("LN", StringComparison.Ordinal) || first.StartsWith("IP", StringComparison.Ordinal))
        {
            // Deprecated layer name / image polarity 'IPPOS' — no image effect for positive.
            if (first == "IPNEG")
                throw new InvalidDataException("Negative image polarity (%IPNEG%) is not supported.");
        }
        else
        {
            throw new InvalidDataException($"Unknown extended command '%{first}*%'.");
        }
    }

    private void ParseApertureDefinition(string statement, ParseState state)
    {
        var m = ApertureDefRegex().Match(statement);
        if (!m.Success)
            throw new InvalidDataException($"Malformed aperture definition '%{statement}*%'.");
        int code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string template = m.Groups[2].Value;
        // Parameters stay RAW here: whether one is a length (× unit scale), a vertex
        // count, or an angle depends on the template and the parameter position.
        double[] p = m.Groups[4].Success
            ? m.Groups[4].Value.Split('X').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()
            : Array.Empty<double>();
        double s = state.UnitScale;

        Aperture aperture = template switch
        {
            "C" when p.Length >= 1 => new CircleAperture(code, p[0] * s,
                p.Length >= 2 ? p[1] * s : null),
            "R" when p.Length >= 2 => new RectangleAperture(code, p[0] * s, p[1] * s,
                p.Length >= 3 ? p[2] * s : null),
            "O" when p.Length >= 2 => new ObroundAperture(code, p[0] * s, p[1] * s,
                p.Length >= 3 ? p[2] * s : null),
            "P" when p.Length >= 2 => BuildPolygonAperture(code, p, s, statement),
            _ when state.Macros.TryGetValue(template, out var macro) =>
                BuildMacroAperture(code, macro, p, s, state),
            _ => throw new InvalidDataException($"Unknown aperture template '{template}' in '%{statement}*%'.")
        };
        state.Apertures[code] = aperture;
    }

    private static PolygonAperture BuildPolygonAperture(int code, double[] p, double s, string statement)
    {
        int vertices = (int)Math.Round(p[1]);                     // vertex count: NOT unit-scaled
        if (vertices is < 3 or > 12 || Math.Abs(p[1] - vertices) > 1e-9)
            throw new InvalidDataException(
                $"Polygon aperture '%{statement}*%': vertex count must be an integer in 3–12, got {p[1]}.");
        double rotation = p.Length >= 3 ? p[2] : 0;               // degrees: NOT unit-scaled
        return new PolygonAperture(code, p[0] * s, vertices, rotation,
            p.Length >= 4 ? p[3] * s : null);
    }

    private static MacroAperture BuildMacroAperture(int code, MacroDefinition macro, double[] p,
        double s, ParseState state)
    {
        // Macro parameters pass raw ($n values are file-unit lengths where the macro
        // body uses them as such); the evaluator applies the unit scale when it
        // constructs primitive geometry.
        var primitives = MacroEvaluator.Evaluate(macro, p, s, state.Warnings);
        return new MacroAperture(code, macro.Name, primitives, MacroBoundingSize(primitives));
    }

    /// <summary>Largest footprint dimension of the exposure-on primitives — what pad
    /// extraction sizes a macro flash by.</summary>
    private static double MacroBoundingSize(IReadOnlyList<MacroPrimitive> primitives)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Include(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        foreach (var primitive in primitives)
        {
            if (!primitive.Exposure) continue;
            switch (primitive)
            {
                case MacroCircle c:
                    Include(c.Center.X - c.Diameter / 2, c.Center.Y - c.Diameter / 2);
                    Include(c.Center.X + c.Diameter / 2, c.Center.Y + c.Diameter / 2);
                    break;
                case MacroRing r:
                    foreach (var v in r.Vertices) Include(v.X, v.Y);
                    break;
                case MacroThermal t:
                    Include(t.Center.X - t.OuterDiameter / 2, t.Center.Y - t.OuterDiameter / 2);
                    Include(t.Center.X + t.OuterDiameter / 2, t.Center.Y + t.OuterDiameter / 2);
                    break;
            }
        }
        return maxX < minX ? 0 : Math.Max(maxX - minX, maxY - minY);
    }

    // ---------------- Step-repeat (%SR%) ----------------

    private static void OpenStepRepeat(string parameters, ParseState state)
    {
        var m = StepRepeatRegex().Match(parameters);
        if (!m.Success)
            throw new InvalidDataException($"Malformed step-repeat '%SR{parameters}*%'.");
        int nx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int ny = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (nx < 1 || ny < 1)
            throw new InvalidDataException($"Step-repeat counts must be ≥ 1 in '%SR{parameters}*%'.");
        if (state.UnitScale == 0)
            throw new InvalidDataException("Step-repeat seen before the %MO% unit mode.");
        // I/J are plain decimal distances in the current unit, not FS fixed-point.
        state.SrNx = nx;
        state.SrNy = ny;
        state.SrStepX = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * state.UnitScale;
        state.SrStepY = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * state.UnitScale;
        state.SrOpStart = state.Ops.Count;
        state.SrOpen = true;
    }

    /// <summary>
    /// Replays the ops recorded since the block opened for every grid cell except
    /// (0,0) — the original ops already are that cell. Replay at close (not per-op)
    /// keeps file order per repeat, so polarity batches stay correct.
    /// </summary>
    private static void CloseStepRepeat(ParseState state)
    {
        if (!state.SrOpen) return;
        state.SrOpen = false;
        int start = state.SrOpStart;
        int count = state.Ops.Count - start;
        long total = (long)state.SrNx * state.SrNy * count;
        if (total > 1_000_000)
            throw new InvalidDataException(
                $"Step-repeat would expand to {total} operations ({state.SrNx}×{state.SrNy} of {count}) — refusing.");
        for (int i = 0; i < state.SrNx; i++)
        {
            for (int j = 0; j < state.SrNy; j++)
            {
                if (i == 0 && j == 0) continue;
                var offset = new Point2(i * state.SrStepX, j * state.SrStepY);
                for (int k = 0; k < count; k++)
                    state.Ops.Add(Translate(state.Ops[start + k], offset));
            }
        }
    }

    private static GerberOp Translate(GerberOp op, Point2 offset) => op switch
    {
        FlashOp f => f with { Position = f.Position + offset },
        DrawOp d => d with { Path = d.Path.Select(p => p + offset).ToList() },
        RegionOp r => r with
        {
            Contours = r.Contours
                .Select(c => (IReadOnlyList<Point2>)c.Select(p => p + offset).ToList()).ToList()
        },
        _ => throw new NotSupportedException($"Step-repeat of {op.GetType().Name}.")
    };

    // ---------------- Word commands ----------------

    private void ParseWord(string statement, ParseState state)
    {
        if (statement.Length == 0) return;

        if (statement.StartsWith("G04", StringComparison.Ordinal)) return;   // comment
        switch (statement)
        {
            case "G01": state.Mode = Interpolation.Linear; return;
            case "G02": state.Mode = Interpolation.Clockwise; return;
            case "G03": state.Mode = Interpolation.CounterClockwise; return;
            case "G74":
                state.Warnings.Add("Single-quadrant arc mode (G74) is deprecated; arcs assume G75 semantics.");
                return;
            case "G75": return;                                              // multi-quadrant: the supported mode
            case "G36":
                state.FlushDraw();
                state.InRegion = true;
                state.RegionContours.Clear();
                state.CurrentContour.Clear();
                return;
            case "G37":
                state.EndRegion();
                return;
            case "G70": state.UnitScale = 0.0254; return;                    // deprecated unit selects
            case "G71": state.UnitScale = 1e-3; return;
            case "G90": return;                                              // absolute (the only supported mode)
            case "G91": throw new InvalidDataException("Incremental coordinates (G91) are not supported.");
            case "M02":
            case "M00":
                state.FlushDraw();
                state.Ended = true;
                return;
        }

        if (statement.StartsWith("G54", StringComparison.Ordinal))
            statement = statement[3..];                                      // deprecated "select aperture" prefix

        if (statement.Length >= 2 && statement[0] == 'D' && !statement.Contains('X') && !statement.Contains('Y'))
        {
            int code = int.Parse(statement[1..], CultureInfo.InvariantCulture);
            if (code >= 10)
            {
                state.FlushDraw();
                state.CurrentAperture = state.Apertures.TryGetValue(code, out var ap)
                    ? ap
                    : throw new InvalidDataException($"Aperture D{code} selected but never defined.");
                return;
            }
            // Bare D01/D02/D03 reuses the previous coordinates.
            ExecuteOperation(code, state.Current, state.Current, null, state);
            return;
        }

        if (statement[0] is 'X' or 'Y' or 'I' or 'J' or 'D')
        {
            ParseCoordinateStatement(statement, state);
            return;
        }

        throw new InvalidDataException($"Unknown Gerber statement '{statement}*'.");
    }

    private void ParseCoordinateStatement(string statement, ParseState state)
    {
        double? x = null, y = null, iOfs = null, jOfs = null;
        int op = -1;
        int i = 0;
        while (i < statement.Length)
        {
            char letter = statement[i++];
            int start = i;
            while (i < statement.Length && (char.IsDigit(statement[i]) || statement[i] is '+' or '-'))
                i++;
            string digits = statement[start..i];
            switch (letter)
            {
                case 'X': x = state.ToMeters(digits, state.DecimalsX); break;
                case 'Y': y = state.ToMeters(digits, state.DecimalsY); break;
                case 'I': iOfs = state.ToMeters(digits, state.DecimalsX); break;
                case 'J': jOfs = state.ToMeters(digits, state.DecimalsY); break;
                case 'D': op = int.Parse(digits, CultureInfo.InvariantCulture); break;
                default: throw new InvalidDataException($"Unexpected '{letter}' in coordinate statement '{statement}*'.");
            }
        }
        if (op is not (1 or 2 or 3))
            throw new InvalidDataException($"Coordinate statement '{statement}*' must end with D01, D02 or D03.");

        var target = new Point2(x ?? state.Current.X, y ?? state.Current.Y);
        Point2? arcOffset = iOfs.HasValue || jOfs.HasValue ? new Point2(iOfs ?? 0, jOfs ?? 0) : null;
        ExecuteOperation(op, state.Current, target, arcOffset, state);
    }

    private void ExecuteOperation(int op, Point2 from, Point2 to, Point2? arcOffset, ParseState state)
    {
        switch (op)
        {
            case 2:                                                          // move
                state.FlushDraw();
                if (state.InRegion)
                    state.CloseContour();
                state.Current = to;
                return;

            case 3:                                                          // flash
                if (state.InRegion)
                    throw new InvalidDataException("D03 flash inside a G36 region is not allowed.");
                state.FlushDraw();
                state.Ops.Add(new FlashOp(to, state.RequireAperture(), state.Polarity));
                state.Current = to;
                return;

            case 1:                                                          // draw / arc
                var segment = state.Mode == Interpolation.Linear
                    ? new[] { to }
                    : TessellateArc(from, to, arcOffset, state.Mode == Interpolation.Clockwise);
                if (state.InRegion)
                {
                    if (state.CurrentContour.Count == 0)
                        state.CurrentContour.Add(from);
                    state.CurrentContour.AddRange(segment);
                }
                else
                {
                    if (state.PendingDraw.Count == 0)
                        state.PendingDraw.Add(from);
                    state.PendingDraw.AddRange(segment);
                }
                state.Current = to;
                return;
        }
    }

    /// <summary>
    /// Multi-quadrant (G75) arc from <paramref name="from"/> to <paramref name="to"/>
    /// around from+offset, tessellated at the configured chord tolerance. Identical
    /// endpoints trace a full circle, per the G75 convention.
    /// </summary>
    private Point2[] TessellateArc(Point2 from, Point2 to, Point2? offset, bool clockwise)
    {
        if (offset is null)
            throw new InvalidDataException("Arc draw (G02/G03) requires I/J center offsets.");
        var center = from + offset.Value;
        double radius = (from - center).Length;
        if (radius <= 0)
            return new[] { to };

        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        double a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double sweep = clockwise ? a0 - a1 : a1 - a0;
        sweep = (sweep % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        if (sweep < 1e-12) sweep = 2 * Math.PI;                              // full circle

        double maxStep = 2 * Math.Acos(Math.Max(0.0, 1 - _options.ChordTolerance / radius));
        int steps = Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(maxStep, 1e-4)));
        var points = new Point2[steps];
        for (int s = 1; s <= steps; s++)
        {
            double a = a0 + (clockwise ? -1 : 1) * sweep * s / steps;
            points[s - 1] = new Point2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        points[^1] = to == from && sweep >= 2 * Math.PI - 1e-12 ? from : to; // land exactly on the endpoint
        return points;
    }

    // ---------------- Parse state ----------------

    private sealed class ParseState
    {
        public int DecimalsX = -1, DecimalsY = -1;
        public double UnitScale;                                             // 0 until %MO% seen
        public Interpolation Mode = Interpolation.Linear;
        public GerberPolarity Polarity = GerberPolarity.Dark;
        public Aperture? CurrentAperture;
        public Point2 Current;
        public bool InRegion;
        public bool Ended;

        public readonly Dictionary<int, Aperture> Apertures = new();
        public readonly List<GerberOp> Ops = new();
        public readonly List<string> Warnings = new();
        public readonly Dictionary<string, MacroDefinition> Macros = new();

        // Open step-repeat block: ops from SrOpStart onward are replayed at close.
        public bool SrOpen;
        public int SrNx, SrNy, SrOpStart;
        public double SrStepX, SrStepY;

        /// <summary>Consecutive D01 segments chained into one polyline (better stroking).</summary>
        public readonly List<Point2> PendingDraw = new();
        public readonly List<IReadOnlyList<Point2>> RegionContours = new();
        public readonly List<Point2> CurrentContour = new();

        public double ToMeters(string digits, int decimals)
        {
            if (decimals < 0)
                throw new InvalidDataException("Coordinate seen before the %FS% format specification.");
            if (UnitScale == 0)
                throw new InvalidDataException("Coordinate seen before the %MO% unit mode.");
            return long.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, decimals) * UnitScale;
        }

        public Aperture RequireAperture() =>
            CurrentAperture ?? throw new InvalidDataException("Draw/flash before any aperture was selected.");

        public void FlushDraw()
        {
            if (PendingDraw.Count >= 2)
                Ops.Add(new DrawOp(PendingDraw.ToList(), RequireAperture(), Polarity));
            PendingDraw.Clear();
        }

        public void CloseContour()
        {
            if (CurrentContour.Count >= 3)
                RegionContours.Add(CurrentContour.ToList());
            CurrentContour.Clear();
        }

        public void EndRegion()
        {
            CloseContour();
            if (RegionContours.Count > 0)
                Ops.Add(new RegionOp(RegionContours.ToList(), Polarity));
            RegionContours.Clear();
            InRegion = false;
        }
    }

    [GeneratedRegex(@"^FSLAX(\d)(\d)Y(\d)(\d)$")]
    private static partial Regex FormatSpecRegex();

    [GeneratedRegex(@"^ADD(\d+)([A-Za-z_.$][\w.$]*?)(,([\d.X+-]*))?$")]
    private static partial Regex ApertureDefRegex();

    [GeneratedRegex(@"^X(\d+)Y(\d+)I([\d.+-]+)J([\d.+-]+)$")]
    private static partial Regex StepRepeatRegex();
}
