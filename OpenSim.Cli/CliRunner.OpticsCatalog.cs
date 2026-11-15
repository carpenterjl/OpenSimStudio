using System.Globalization;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.Cli;

// OP-16: stock lens catalogs on the command line (docs/Optics-Conventions.md section 53):
// "optics catalog" indexes a folder of .zmx/.zar/.seq/.len files (or loads a private catalog), searches it
// and prints the search results and the Catalog Report; --save writes the private catalog.

public static partial class CliRunner
{
    private static int OpticsCatalog(string source, Dictionary<string, string> o, TextWriter stdout)
    {
        var options = new ZemaxImportOptions { UnsupportedAsWarnings = o.ContainsKey("lenient") };
        LensCatalog catalog;
        if (Directory.Exists(source))
        {
            var glass = LoadedCatalogs(o, line => stdout.WriteLine(line));
            catalog = LensCatalog.FromFolder(source, glass, CatalogOption(o, "name"), options);
        }
        else if (File.Exists(source) && string.Equals(Path.GetExtension(source), LensCatalog.FileExtension, StringComparison.OrdinalIgnoreCase))
            catalog = LensCatalog.Load(source);
        else
            throw new UsageException($"'optics catalog' needs a folder of .zmx/.zar/.seq/.len files or a {LensCatalog.FileExtension} file; '{source}' is neither.");
        foreach (var p in catalog.Problems) stdout.WriteLine("Problem: " + p);

        LensSearchCriteria criteria;
        try
        {
            criteria = new LensSearchCriteria
            {
            Elements = CatalogOption(o, "elements") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : null,
            Text = CatalogOption(o, "text"),
            Shapes = (CatalogOption(o, "shape") ?? "").Select(StockLensCodes.ParseShape).ToHashSet(),
            Types = (CatalogOption(o, "type") ?? "").Select(StockLensCodes.ParseType).ToHashSet(),
            };
        }
        catch (FormatException ex) { throw new UsageException($"optics catalog: {ex.Message}"); }
        if (CatalogRange(o, "efl") is var (fMin, fMax)) criteria = criteria with { UseEffectiveFocalLength = true, FocalLengthMinMm = fMin, FocalLengthMaxMm = fMax };
        if (CatalogRange(o, "epd") is var (dMin, dMax)) criteria = criteria with { UseEntrancePupilDiameter = true, DiameterMinMm = dMin, DiameterMaxMm = dMax };
        if (criteria.Validate() is { Count: > 0 } bad) throw new UsageException(string.Join(" ", bad));
        var found = LensCatalogSearch.Find(new[] { catalog }, criteria);
        foreach (var line in LensCatalogSearch.Report(catalog.Name, criteria, found)) stdout.WriteLine(line);

        if (CatalogOption(o, "save") is { } save)
        {
            catalog.Save(save, CatalogOption(o, "name"));
            stdout.WriteLine($"Wrote the private catalog {save} ({catalog.Lenses.Count} lenses).");
        }
        return 0;
    }

    private static string? CatalogOption(Dictionary<string, string> o, string key) =>
        o.TryGetValue(key, out var v) && v != "true" && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>"MIN:MAX" in millimeters, or null when the option is absent.</summary>
    private static (double Min, double Max)? CatalogRange(Dictionary<string, string> o, string key)
    {
        if (CatalogOption(o, key) is not { } text) return null;
        var parts = text.Split(':');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            throw new UsageException($"--{key} needs MIN:MAX in millimeters (it is '{text}').");
        return (a, b);
    }
}
