using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Excellon;

/// <summary>One drilled hole: position and diameter, in meters.</summary>
public readonly record struct DrillHit(Point2 Position, double Diameter);

/// <summary>One routed slot (G85): a segment milled at the tool diameter, in meters.</summary>
public readonly record struct DrillSlot(Point2 Start, Point2 End, double Diameter);

/// <summary>A parsed Excellon drill file.</summary>
public sealed class DrillFile
{
    public required IReadOnlyList<DrillHit> Hits { get; init; }
    public required IReadOnlyList<DrillSlot> Slots { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Parser for the Excellon v1 subset KiCad-style tools emit: M48 header with
/// INCH/METRIC and Tn C&lt;dia&gt; tool definitions, then Tn selects, X/Y hits, and
/// single-line G85 slots ('X..Y..G85X..Y..'), all with explicit decimal points.
/// Multi-line slot forms and repeats (Rn) fail loudly — a silently missing hole
/// would corrupt the copper image.
/// </summary>
public sealed class ExcellonParser
{
    public DrillFile ParseFile(string filePath) => Parse(File.ReadAllText(filePath));

    public DrillFile Parse(string content)
    {
        var warnings = new List<string>();
        var tools = new Dictionary<int, double>();
        var hits = new List<DrillHit>();
        var slots = new List<DrillSlot>();
        double unitScale = 0;
        int currentTool = -1;
        bool inHeader = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (line == "M48") { inHeader = true; continue; }
            if (line is "%" or "M95") { inHeader = false; continue; }
            if (line is "M30" or "M00") break;
            if (line.StartsWith("INCH", StringComparison.Ordinal)) { unitScale = 0.0254; continue; }
            if (line.StartsWith("METRIC", StringComparison.Ordinal)) { unitScale = 1e-3; continue; }
            if (line is "G90" or "G05" or "FMAT,2" or "M71" or "M72") continue;

            if (line.Contains("G85"))
            {
                // Single-line slot: start coordinates, G85, end coordinates. The tool
                // diameter is the slot width. Missing end axes are modal (inherit from
                // the start point) — KiCad emits e.g. 'X1.0Y1.0G85X2.0'.
                int g85 = line.IndexOf("G85", StringComparison.Ordinal);
                string startPart = line[..g85];
                string endPart = line[(g85 + 3)..];
                if (startPart.Length == 0 || endPart.Length == 0)
                    throw new InvalidDataException(
                        $"Slot '{line}': only the single-line 'X..Y..G85X..Y..' form is supported — " +
                        "multi-line G85 slots are not.");
                if (currentTool < 0)
                    throw new InvalidDataException($"Slot '{line}' before any tool selection.");
                if (!startPart.Contains('.') || !endPart.Contains('.'))
                    throw new InvalidDataException(
                        $"Slot coordinate '{line}' has no decimal point; zero-suppressed integer " +
                        "coordinates are not supported — export with decimal coordinates.");
                var start = ParseXy(startPart, unitScale);
                var end = ParseXy(endPart, unitScale, start);
                slots.Add(new DrillSlot(start, end, tools[currentTool]));
                continue;
            }
            if (line[0] == 'R' && line.Length > 1 && char.IsDigit(line[1]))
                throw new InvalidDataException($"Repeat command '{line}' is not supported.");

            if (line[0] == 'T')
            {
                int cIdx = line.IndexOf('C');
                if (inHeader && cIdx > 0)
                {
                    int tool = int.Parse(line[1..cIdx], CultureInfo.InvariantCulture);
                    double dia = double.Parse(line[(cIdx + 1)..], CultureInfo.InvariantCulture);
                    if (unitScale == 0)
                        throw new InvalidDataException("Tool definition before INCH/METRIC unit selection.");
                    tools[tool] = dia * unitScale;
                }
                else
                {
                    int tool = int.Parse(line[1..], CultureInfo.InvariantCulture);
                    if (tool == 0) { currentTool = -1; continue; }          // T0 = tool unload
                    currentTool = tools.ContainsKey(tool)
                        ? tool
                        : throw new InvalidDataException($"Tool T{tool} selected but never defined.");
                }
                continue;
            }

            if (line[0] == 'X' || line[0] == 'Y')
            {
                if (currentTool < 0)
                    throw new InvalidDataException($"Drill hit '{line}' before any tool selection.");
                if (!line.Contains('.'))
                    throw new InvalidDataException(
                        $"Drill coordinate '{line}' has no decimal point; zero-suppressed integer " +
                        "coordinates are not supported — export with decimal coordinates.");
                hits.Add(new DrillHit(ParseXy(line, unitScale), tools[currentTool]));
                continue;
            }

            warnings.Add($"Ignored unrecognized drill statement '{line}'.");
        }

        return new DrillFile { Hits = hits, Slots = slots, Warnings = warnings };
    }

    private static Point2 ParseXy(string line, double unitScale, Point2 defaults = default)
    {
        double x = defaults.X, y = defaults.Y;
        int i = 0;
        while (i < line.Length)
        {
            char axis = line[i++];
            int start = i;
            while (i < line.Length && (char.IsDigit(line[i]) || line[i] is '.' or '+' or '-'))
                i++;
            double value = double.Parse(line[start..i], CultureInfo.InvariantCulture) * unitScale;
            if (axis == 'X') x = value;
            else if (axis == 'Y') y = value;
            else throw new InvalidDataException($"Unexpected '{axis}' in drill coordinate '{line}'.");
        }
        return new Point2(x, y);
    }
}
