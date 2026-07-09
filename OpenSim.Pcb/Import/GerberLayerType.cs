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

    private static BoardLayer Sided(string file, GerberLayerType type, string[] fields) =>
        new(file, type, 0, fields.Any(f => f.Equals("Top", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Filename-keyword fallback for files without a FileFunction attribute.</summary>
    public static BoardLayer ClassifyByName(string fileName)
    {
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
            return new BoardLayer(fileName, GerberLayerType.CopperSignal, top ? 1 : 99, top);
        return new BoardLayer(fileName, GerberLayerType.Unknown, 0, top);
    }

    private static string? ExtractFileFunction(string header)
    {
        const string tag = "%TF.FileFunction,";
        int i = header.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return null;
        i += tag.Length;
        int end = header.IndexOf("*%", i, StringComparison.Ordinal);
        return end < 0 ? null : header[i..end];
    }
}
