namespace OpenSim.Pcb.Import;

/// <summary>The functional role of a Gerber/drill file in a board fabrication set.</summary>
public enum GerberLayerType
{
    Unknown,
    CopperSignal,
    CopperPlane,
    Profile,        // board outline
    Drill,          // plated or non-plated holes (Gerber- or Excellon-format)
    SolderPaste,
    SolderMask,
    Legend          // silkscreen
}

/// <summary>One classified file in a board set.</summary>
public sealed record BoardLayer(
    string FileName,
    GerberLayerType Type,
    int CopperOrder,     // 1 = top … N = bottom; 0 when not copper
    bool IsTopSide);

/// <summary>
/// Classifies board files by their Gerber <c>%TF.FileFunction</c> attribute (the reliable
/// signal Altium/KiCad both emit), falling back to filename keywords. Reads only the file
/// header, so it is cheap to run across a whole archive.
/// </summary>
public static class GerberLayerClassifier
{
    public static BoardLayer Classify(string fileName, string headerText)
    {
        string? ff = ExtractFileFunction(headerText);
        if (ff is not null)
        {
            var fields = ff.Split(',', StringSplitOptions.TrimEntries);
            switch (fields[0].ToLowerInvariant())
            {
                case "copper":
                    // Copper,L<n>,<Top|Bot|Inr>,<Signal|Plane|Mixed>
                    int order = fields.Length > 1 && fields[1].StartsWith('L')
                        ? int.TryParse(fields[1][1..], out int l) ? l : 0
                        : 0;
                    bool top = fields.Any(f => f.Equals("Top", StringComparison.OrdinalIgnoreCase));
                    var type = fields.Any(f => f.Equals("Plane", StringComparison.OrdinalIgnoreCase))
                        ? GerberLayerType.CopperPlane
                        : GerberLayerType.CopperSignal;
                    return new BoardLayer(fileName, type, order, top);
                case "profile":
                    return new BoardLayer(fileName, GerberLayerType.Profile, 0, false);
                case "plated":
                case "nonplated":
                    return new BoardLayer(fileName, GerberLayerType.Drill, 0, false);
                case "paste":
                    return Sided(fileName, GerberLayerType.SolderPaste, fields);
                case "soldermask":
                    return Sided(fileName, GerberLayerType.SolderMask, fields);
                case "legend":
                    return Sided(fileName, GerberLayerType.Legend, fields);
            }
        }
        return ClassifyByName(fileName);
    }

    /// <summary>
    /// What a drill file says about its holes: plated or not, and — for blind and buried
    /// drills — the copper layers the holes run between. Read from the FileFunction
    /// attribute, <c>Plated,1,2,PTH</c> / <c>NonPlated,1,4,NPTH</c>, in a Gerber header or in
    /// an Excellon <c>; #@! TF.FileFunction,…</c> comment. Only a file with no such
    /// attribute is judged by its name. A span of (0, 0) means through the whole board.
    /// </summary>
    public static (bool Plated, int FromLayer, int ToLayer, bool FromAttribute) DrillFunction(
        string fileName, string headerText)
    {
        string? ff = ExtractFileFunction(headerText);
        if (ff is not null)
        {
            var fields = ff.Split(',', StringSplitOptions.TrimEntries);
            string kind = fields[0].ToLowerInvariant();
            if (kind is "plated" or "nonplated")
            {
                int from = fields.Length > 1 && int.TryParse(fields[1], out int a) ? a : 0;
                int to = fields.Length > 2 && int.TryParse(fields[2], out int b) ? b : 0;
                if (from <= 0 || to <= 0) from = to = 0;
                return (kind == "plated", Math.Min(from, to), Math.Max(from, to), true);
            }
        }
        bool plated = !fileName.Contains("NPTH", StringComparison.OrdinalIgnoreCase)
                      && !fileName.Contains("NonPlated", StringComparison.OrdinalIgnoreCase);
        return (plated, 0, 0, false);
    }

    private static BoardLayer Sided(string file, GerberLayerType type, string[] fields) =>
        new(file, type, 0, fields.Any(f => f.Equals("Top", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The copper order the filename fallback gives a bottom layer: a placeholder meaning
    /// "last", not a layer count. <see cref="PcbBoardReader"/> renumbers a set that used
    /// the fallback to contiguous 1..N, so this value never reaches the stackup.
    /// </summary>
    public const int BottomByName = 99;

    /// <summary>Filename fallback for files without a FileFunction attribute: the Protel
    /// extension when it names the layer by itself, else keywords in the name. An inner
    /// layer named only by extension (.g1, .gp1) gets copper order 0 — its position in
    /// the stack is not in the name, and the reader says so when it places it.</summary>
    public static BoardLayer ClassifyByName(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        switch (ext)
        {
            case ".gtl": return new BoardLayer(fileName, GerberLayerType.CopperSignal, 1, true);
            case ".gbl": return new BoardLayer(fileName, GerberLayerType.CopperSignal, BottomByName, false);
        }
        if (IsInnerExtension(ext, out bool plane))
            return new BoardLayer(fileName,
                plane ? GerberLayerType.CopperPlane : GerberLayerType.CopperSignal, 0, false);

        string n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        bool top = n.Contains("top") || n.EndsWith("_t") || n.Contains("_l1");
        if (n.Contains("profile") || n.Contains("outline") || n.Contains("edge") || n.Contains("boardoutline"))
            return new BoardLayer(fileName, GerberLayerType.Profile, 0, false);
        if (n.Contains("drill") || n.Contains("nc") || fileName.EndsWith(".drl", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".xln", StringComparison.OrdinalIgnoreCase))
            return new BoardLayer(fileName, GerberLayerType.Drill, 0, false);
        if (n.Contains("paste")) return new BoardLayer(fileName, GerberLayerType.SolderPaste, 0, top);
        if (n.Contains("mask")) return new BoardLayer(fileName, GerberLayerType.SolderMask, 0, top);
        if (n.Contains("silk") || n.Contains("legend")) return new BoardLayer(fileName, GerberLayerType.Legend, 0, top);
        if (n.Contains("plane")) return new BoardLayer(fileName, GerberLayerType.CopperPlane, 0, top);
        if (n.Contains("copper") || n.Contains("signal") || n.Contains("gtl") || n.Contains("gbl"))
            return new BoardLayer(fileName, GerberLayerType.CopperSignal, top ? 1 : BottomByName, top);
        return new BoardLayer(fileName, GerberLayerType.Unknown, 0, top);
    }

    /// <summary>Protel inner-layer extensions: <c>.g1 … .g30</c> (mid signal layers) and
    /// <c>.gp1 … .gp16</c> (internal planes).</summary>
    public static bool IsInnerExtension(string extension, out bool plane)
    {
        plane = false;
        string e = extension.ToLowerInvariant();
        if (!e.StartsWith(".g", StringComparison.Ordinal) || e.Length < 3) return false;
        string digits = e[2..];
        if (digits.StartsWith('p')) { plane = true; digits = digits[1..]; }
        return digits.Length is > 0 and <= 2 && digits.All(char.IsDigit);
    }

    private static string? ExtractFileFunction(string header)
    {
        const string tag = "%TF.FileFunction,";
        int i = header.IndexOf(tag, StringComparison.Ordinal);
        if (i >= 0)
        {
            i += tag.Length;
            int end = header.IndexOf("*%", i, StringComparison.Ordinal);
            return end < 0 ? null : header[i..end];
        }
        // Excellon carries the same attribute in a structured comment line.
        const string comment = "#@! TF.FileFunction,";
        i = header.IndexOf(comment, StringComparison.Ordinal);
        if (i < 0) return null;
        i += comment.Length;
        int eol = header.IndexOfAny(new[] { '\r', '\n' }, i);
        return (eol < 0 ? header[i..] : header[i..eol]).Trim();
    }
}
