using System.Xml;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// The IPC-2581 style dictionaries: <c>DictionaryLineDesc</c> (<c>EntryLineDesc id</c> →
/// stroke width + cap style) and <c>DictionaryFillDesc</c> (<c>EntryFillDesc id</c> →
/// fill property). Cadence exports route ALL trace widths through <c>LineDescRef</c>
/// (66k+ Polylines on a real board) and fill modes through <c>FillDescRef</c>, so these
/// are geometry-critical, not cosmetic: an unresolved LineDescRef is an unknown trace
/// width and a HOLLOW fill property turns a ring from a pour into an outline stroke.
/// </summary>
public sealed class Ipc2581StyleDictionary
{
    private readonly Dictionary<string, (double Width, string? LineEnd)> _lineDescs = new();
    private readonly Dictionary<string, string> _fillDescs = new();

    /// <summary>Parses a <c>DictionaryLineDesc</c> subtree (reader on the element, consumed).</summary>
    public void ReadLineDescDictionary(XmlReader reader, double scale, Ipc2581Diagnostics diag)
    {
        using var sub = reader.ReadSubtree();
        sub.Read();
        string? id = null;
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "EntryLineDesc")
                id = sub.GetAttribute("id");
            else if (sub.LocalName == "LineDesc" && id is not null)
            {
                if (PolyShapeReader.TryAttr(sub, "lineWidth", scale, out double width))
                    _lineDescs[id] = (width, sub.GetAttribute("lineEnd"));
                else
                    diag.Warn($"IPC-2581: EntryLineDesc '{id}' has no readable lineWidth" +
                              $"{PolyShapeReader.LinePosition(sub)}; the entry is ignored.");
                id = null;
            }
        }
    }

    /// <summary>Parses a <c>DictionaryFillDesc</c> subtree (reader on the element, consumed).</summary>
    public void ReadFillDescDictionary(XmlReader reader, Ipc2581Diagnostics diag)
    {
        using var sub = reader.ReadSubtree();
        sub.Read();
        string? id = null;
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "EntryFillDesc")
                id = sub.GetAttribute("id");
            else if (sub.LocalName == "FillDesc" && id is not null)
            {
                string? property = sub.GetAttribute("fillProperty");
                if (property is not null) _fillDescs[id] = property;
                id = null;
            }
        }
    }

    /// <summary>The stroke style an <c>EntryLineDesc</c> id resolves to; false when unknown.</summary>
    public bool TryGetLineDesc(string id, out double width, out string? lineEnd)
    {
        if (_lineDescs.TryGetValue(id, out var entry))
        {
            (width, lineEnd) = entry;
            return true;
        }
        width = 0;
        lineEnd = null;
        return false;
    }

    /// <summary>The fill property an <c>EntryFillDesc</c> id resolves to (FILL when unknown,
    /// with a warning — filling is the conservative copper interpretation).</summary>
    public string FillProperty(string id, Ipc2581Diagnostics diag, string layerRef)
    {
        if (_fillDescs.TryGetValue(id, out var property)) return property;
        diag.Warn($"IPC-2581: FillDescRef '{id}' on '{layerRef}' not found in the dictionary; " +
                  "treated as FILL.");
        return "FILL";
    }
}
