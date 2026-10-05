using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Thermal;

/// <summary>
/// A component as the board's thermal model sees it: where it touches the board, how much
/// it dissipates, and the datasheet's two resistances — junction to case top (θJC) and
/// junction to board (θJB). The junction is one node; heat leaves it down through θJB into
/// the footprint and up through θJC to the case top, and from there to the air (a film on
/// the case top) or into a heatsink.
/// </summary>
public sealed record ThermalComponent
{
    public required string RefDes { get; init; }
    public string? Part { get; init; }

    /// <summary>Mounted on the top side (copper layer 1); false = bottom side.</summary>
    public bool OnTop { get; init; } = true;

    /// <summary>The area through which the part exchanges heat with the board.</summary>
    public required Polygon2 Footprint { get; init; }

    /// <summary>Dissipated power [W].</summary>
    public double PowerWatts { get; init; }

    /// <summary>Junction to case top [K/W].</summary>
    public double ThetaJc { get; init; }

    /// <summary>Junction to board [K/W].</summary>
    public double ThetaJb { get; init; }

    /// <summary>
    /// Case top to ambient [K/W] when something other than bare air takes the heat — a
    /// heatsink's own resistance plus its interface. Null: the film
    /// <see cref="CaseFilmCoefficient"/> on <see cref="CaseArea"/>.
    /// <see cref="double.PositiveInfinity"/>: nothing leaves through the top.
    /// </summary>
    public double? CaseToAmbient { get; init; }

    /// <summary>Film coefficient on the bare case top [W/(m²·K)], convection and radiation
    /// together. A setting, not a computed value.</summary>
    public double CaseFilmCoefficient { get; init; } = 10.0;

    /// <summary>Case top area [m²]; null = the footprint's area.</summary>
    public double? CaseAreaSquareMeters { get; init; }

    public double CaseArea => CaseAreaSquareMeters ?? Footprint.Area();

    /// <summary>Case top to ambient [K/W] as it enters the network.</summary>
    public double TopResistance()
    {
        if (CaseToAmbient is { } given) return given;
        double conductance = CaseFilmCoefficient * CaseArea;
        return conductance > 0 ? 1.0 / conductance : double.PositiveInfinity;
    }

    public void Validate()
    {
        if (ThetaJc < 0 || ThetaJb < 0 || double.IsNaN(ThetaJc) || double.IsNaN(ThetaJb))
            throw new InvalidOperationException($"{RefDes}: θJC and θJB cannot be negative.");
        if (CaseToAmbient is < 0)
            throw new InvalidOperationException($"{RefDes}: the case-to-ambient resistance cannot be negative.");
        if (Footprint.Area() <= 0)
            throw new InvalidOperationException($"{RefDes}: the footprint has no area.");
    }
}

/// <summary>A part's place on the board, from the layout or from a placement file.</summary>
/// <param name="PadExtent">The rectangle around the part's pads on its mounting side;
/// null when only a centre is known (placement file).</param>
public sealed record PlacedPart(string RefDes, string? Part, bool OnTop, Point2 Center,
    double RotationDegrees, Polygon2? PadExtent, int PadCount)
{
    /// <summary>The part as a thermal component on its pad extent, or on a body of the
    /// given size about its centre.</summary>
    public ThermalComponent ToComponent(double powerWatts, double thetaJc, double thetaJb,
        double? bodyWidth = null, double? bodyHeight = null)
    {
        Polygon2 footprint;
        if (bodyWidth is { } w && bodyHeight is { } h)
            footprint = ComponentPlacement.Rectangle(Center, w, h, RotationDegrees);
        else
            footprint = PadExtent ?? throw new InvalidOperationException(
                $"{RefDes}: the placement gives a centre only; give the body's width and height.");
        return new ThermalComponent
        {
            RefDes = RefDes, Part = Part, OnTop = OnTop, Footprint = footprint,
            PowerWatts = powerWatts, ThetaJc = thetaJc, ThetaJb = thetaJb
        };
    }
}

/// <summary>Where the parts are: from the pads' component names (IPC-2581 PinRef, Gerber
/// X2 %TO.P / %TO.C) or from a pick-and-place file.</summary>
public static class ComponentPlacement
{
    /// <summary>
    /// One part per component name on the board's pads. The side is the one most of its
    /// pads are on (a through-hole part has pads on both and counts as top); the extent is
    /// the rectangle around the pads of that side.
    /// </summary>
    public static IReadOnlyList<PlacedPart> FromPads(PcbBoard board)
    {
        int bottom = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder)
            : board.Pads.Count > 0 ? board.Pads.Max(p => p.LayerOrder) : 1;
        var parts = new List<PlacedPart>();
        foreach (var group in board.Pads.Where(p => p.ComponentRef is not null)
                     .GroupBy(p => p.ComponentRef!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int onTop = group.Count(p => p.LayerOrder == 1);
            int onBottom = bottom > 1 ? group.Count(p => p.LayerOrder == bottom) : 0;
            bool top = onTop >= onBottom;
            var side = group.Where(p => p.LayerOrder == (top ? 1 : bottom)).ToList();
            if (side.Count == 0) continue;   // pads on inner layers only: not a mounted part
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var pad in side)
                foreach (var p in pad.Shape.Outer)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
            if (!(maxX > minX) || !(maxY > minY)) continue;
            var centre = new Point2(0.5 * (minX + maxX), 0.5 * (minY + maxY));
            parts.Add(new PlacedPart(group.Key, group.Select(p => p.PartName).FirstOrDefault(n => n is not null),
                top, centre, 0, Rectangle(centre, maxX - minX, maxY - minY), side.Count));
        }
        return parts;
    }

    /// <summary>A rectangle of the given size about a centre, turned counter-clockwise.</summary>
    public static Polygon2 Rectangle(Point2 center, double width, double height, double rotationDegrees = 0)
    {
        double a = rotationDegrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        Point2 Corner(double x, double y) => new(center.X + x * cos - y * sin, center.Y + x * sin + y * cos);
        double hw = 0.5 * width, hh = 0.5 * height;
        return new Polygon2(new[] { Corner(-hw, -hh), Corner(hw, -hh), Corner(hw, hh), Corner(-hw, hh) },
            Array.Empty<IReadOnlyList<Point2>>());
    }

    /// <summary>
    /// Reads a pick-and-place table: one row per part with a designator, a centre and
    /// (optionally) rotation, side and value. The header row names the columns; commas,
    /// semicolons, tabs or runs of spaces separate them, and a leading '#' on the header
    /// (KiCad) is accepted. Lengths are millimetres unless the header or a value says
    /// "mil", "in" or "mm".
    /// </summary>
    public static IReadOnlyList<PlacedPart> ReadPlacementFile(string text, out IReadOnlyList<string> notes)
    {
        var log = new List<string>();
        notes = log;
        var parts = new List<PlacedPart>();
        int[]? columns = null;           // ref, x, y, rotation, side, value
        double headerScale = 1e-3;
        char separator = ' ';
        int skipped = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (columns is null)
            {
                string candidate = line.TrimStart('#', ' ');
                separator = candidate.Contains(',') ? ',' : candidate.Contains(';') ? ';' : candidate.Contains('\t') ? '\t' : ' ';
                var names = Split(candidate, separator).Select(Normalize).ToList();
                int Find(params string[] keys) => names.FindIndex(n => keys.Any(k => n == k || n.StartsWith(k, StringComparison.Ordinal)));
                int r = Find("refdes", "ref", "designator", "reference");
                int x = Find("posx", "midx", "centerx", "centrex", "locationx", "x");
                int y = Find("posy", "midy", "centery", "centrey", "locationy", "y");
                if (r < 0 || x < 0 || y < 0) continue;      // preamble before the header
                columns = new[] { r, x, y, Find("rot"), Find("side", "layer", "tb"), Find("val", "comment", "package", "footprint") };
                string unitHint = Split(candidate, separator)[x].ToLowerInvariant();
                headerScale = unitHint.Contains("mil") ? 25.4e-6 : unitHint.Contains("in") ? 25.4e-3 : 1e-3;
                continue;
            }
            if (line[0] == '#') continue;
            var cells = Split(line, separator);
            if (cells.Count <= Math.Max(columns[0], Math.Max(columns[1], columns[2]))) { skipped++; continue; }
            if (!TryLength(cells[columns[1]], headerScale, out double px) || !TryLength(cells[columns[2]], headerScale, out double py))
            {
                skipped++;
                continue;
            }
            double rotation = 0;
            if (columns[3] >= 0 && columns[3] < cells.Count)
                double.TryParse(cells[columns[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out rotation);
            bool top = true;
            if (columns[4] >= 0 && columns[4] < cells.Count)
            {
                string side = cells[columns[4]].Trim().ToLowerInvariant();
                top = !(side.StartsWith('b') || side.StartsWith("back", StringComparison.Ordinal));
            }
            string? value = columns[5] >= 0 && columns[5] < cells.Count && cells[columns[5]].Length > 0 ? cells[columns[5]] : null;
            parts.Add(new PlacedPart(cells[columns[0]], value, top, new Point2(px, py), rotation, null, 0));
        }
        if (columns is null)
            throw new InvalidOperationException(
                "No header row was found: the placement file needs columns for the designator and the X and Y position.");
        if (skipped > 0) log.Add($"{skipped} row(s) could not be read and were skipped.");
        log.Add($"{parts.Count} part(s) read; lengths taken as {(headerScale == 1e-3 ? "mm" : headerScale == 25.4e-6 ? "mil" : "inch")} unless a value carries its own unit.");
        return parts;
    }

    private static string Normalize(string header)
    {
        int paren = header.IndexOf('(');
        if (paren >= 0) header = header[..paren];
        return new string(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static List<string> Split(string line, char separator)
    {
        if (separator == ' ')
            return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == separator && !quoted) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static bool TryLength(string cell, double defaultScale, out double meters)
    {
        string s = cell.Trim().ToLowerInvariant();
        double scale = defaultScale;
        if (s.EndsWith("mm", StringComparison.Ordinal)) { scale = 1e-3; s = s[..^2]; }
        else if (s.EndsWith("mil", StringComparison.Ordinal)) { scale = 25.4e-6; s = s[..^3]; }
        else if (s.EndsWith("in", StringComparison.Ordinal)) { scale = 25.4e-3; s = s[..^2]; }
        bool ok = double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
        meters = value * scale;
        return ok;
    }
}
