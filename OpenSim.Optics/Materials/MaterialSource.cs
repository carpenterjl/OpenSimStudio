using System.Net;
using System.Text.RegularExpressions;

namespace OpenSim.Optics.Materials;

/// <summary>
/// Where a material's data came from: the database, the page within it, and the page's own
/// statement of its sources. Set for the shipped refractiveindex.info materials (OP-12) and for
/// any page read with <see cref="RefractiveIndexInfoReader"/>; null for an AGF glass, whose
/// catalog file is its source.
/// </summary>
public sealed partial record MaterialSource
{
    /// <summary>The database, e.g. "refractiveindex.info database (CC0 1.0)".</summary>
    public required string Database { get; init; }

    /// <summary>The page's path in the database, e.g. "main/SiO2/nk/Malitson.yml".</summary>
    public required string PagePath { get; init; }

    /// <summary>The book and page titles from the database's catalog, as printed (may hold HTML
    /// such as &lt;sub&gt;); "" when not known.</summary>
    public string BookTitle { get; init; } = "";
    public string PageTitle { get; init; } = "";

    /// <summary>The page's REFERENCES text as written (HTML or Markdown markup kept).</summary>
    public string References { get; init; } = "";

    /// <summary>The page's COMMENTS (or, in newer files, NOTES) text as written.</summary>
    public string Comments { get; init; } = "";

    /// <summary>The page's CONDITIONS and PROPERTIES, flattened to "key" / "parent.key" and the
    /// value text, in file order. Kept for display; only nd and Vd are read into the material.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Conditions { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The references as plain text: line breaks for &lt;br&gt;, other tags removed,
    /// entities decoded.</summary>
    public string PlainReferences => PlainText(References);

    /// <summary>"main/SiO2/nk/Malitson.yml: I. H. Malitson. …", one string for display and logs.</summary>
    public string Citation => $"{Database}, page {PagePath}" + (References.Trim().Length > 0 ? $". {PlainReferences.Replace('\n', ' ')}" : "");

    /// <summary>HTML markup of a database text field reduced to plain text.</summary>
    public static string PlainText(string html)
    {
        string t = BreakTag().Replace(html, "\n");
        t = AnyTag().Replace(t, "");
        t = WebUtility.HtmlDecode(t);
        var lines = t.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();
}
