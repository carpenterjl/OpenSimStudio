using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Catalogs;

// OP-16: lens search and the catalog report (docs/Optics-Conventions.md section 53).

/// <summary>
/// The Lens Catalogs tool's search criteria (OpticStudio 2025 R2 help, Libraries tab › Stock
/// Parts Group › Lens Catalogs): "Use Effective Focal Length" with Focal Length Min/Max, "Use
/// Entrance Pupil Diameter" with Diameter Min/Max (both in millimeters), the shape boxes
/// (Equi-, Bi-, Plano-, Meniscus), the type boxes (Spherical, GRIN, Aspheric, Toroidal) and
/// "# Elements" ("Any #" is null). An empty shape or type set accepts every lens, as the help's
/// "If any of these options are checked, then the search will be limited to lenses meeting at
/// least one of the selected criteria" implies. The part-name filter is this program's addition.
/// </summary>
public sealed record LensSearchCriteria
{
    /// <summary>The help's percentage around a surface group's values: "adds plus/minus 5% to
    /// define the default search ranges" (help, Lens Catalogs).</summary>
    public const double DefaultRangeFraction = 0.05;

    public bool UseEffectiveFocalLength { get; init; }
    public double FocalLengthMinMm { get; init; }
    public double FocalLengthMaxMm { get; init; }
    public bool UseEntrancePupilDiameter { get; init; }
    public double DiameterMinMm { get; init; }
    public double DiameterMaxMm { get; init; }

    /// <summary>Accepted shapes; empty accepts all. The help's boxes are Equi-, Bi-, Plano- and
    /// Meniscus; <see cref="StockLensShape.Other"/> can be asked for here too.</summary>
    public IReadOnlySet<StockLensShape> Shapes { get; init; } = new HashSet<StockLensShape>();

    /// <summary>Accepted types; empty accepts all.</summary>
    public IReadOnlySet<StockLensType> Types { get; init; } = new HashSet<StockLensType>();

    /// <summary>The number of elements, or null for "Any #".</summary>
    public int? Elements { get; init; }

    /// <summary>Text the part name or title must contain (case ignored), or null.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The default ranges for a surface group (<see cref="StockLensProperties.OfGroup"/>): its EFL
    /// and diameter plus and minus 5%, each range used when the value exists (an afocal group has
    /// no focal-length range, a group of zero diameter no diameter range).
    /// </summary>
    public static LensSearchCriteria Around(StockLensGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var c = new LensSearchCriteria();
        if (group.EffectiveFocalLengthMm is double f)
        {
            double d = DefaultRangeFraction * Math.Abs(f);
            c = c with { UseEffectiveFocalLength = true, FocalLengthMinMm = f - d, FocalLengthMaxMm = f + d };
        }
        if (group.DiameterMm > 0)
        {
            double d = DefaultRangeFraction * group.DiameterMm;
            c = c with { UseEntrancePupilDiameter = true, DiameterMinMm = group.DiameterMm - d, DiameterMaxMm = group.DiameterMm + d };
        }
        return c;
    }

    /// <summary>The criteria's problems (a range with min above max, a negative diameter, a
    /// non-positive element count), one line each; empty when the criteria can be searched.</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (UseEffectiveFocalLength && !(FocalLengthMinMm <= FocalLengthMaxMm))
            p.Add(FormattableString.Invariant($"Focal length: the minimum {FocalLengthMinMm:G10} mm is above the maximum {FocalLengthMaxMm:G10} mm."));
        if (UseEntrancePupilDiameter && !(DiameterMinMm <= DiameterMaxMm))
            p.Add(FormattableString.Invariant($"Diameter: the minimum {DiameterMinMm:G10} mm is above the maximum {DiameterMaxMm:G10} mm."));
        if (UseEntrancePupilDiameter && DiameterMaxMm < 0)
            p.Add("Diameter: the range is below zero.");
        if (Elements is int n && n < 1)
            p.Add($"# Elements: {n} is not a number of elements; use Any # or 1 and up.");
        return p;
    }

    /// <summary>True when the lens meets every criterion in use. A lens with no focal length
    /// (afocal) or no pupil fails a range on it.</summary>
    public bool Accepts(StockLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (UseEffectiveFocalLength && !(lens.EffectiveFocalLengthMm is double f && f >= FocalLengthMinMm && f <= FocalLengthMaxMm)) return false;
        if (UseEntrancePupilDiameter && !(lens.EntrancePupilDiameterMm is double d && d >= DiameterMinMm && d <= DiameterMaxMm)) return false;
        if (Shapes.Count > 0 && !Shapes.Contains(lens.Shape)) return false;
        if (Types.Count > 0 && !Types.Contains(lens.Type)) return false;
        if (Elements is int n && lens.Elements != n) return false;
        if (!string.IsNullOrWhiteSpace(Text)
            && !lens.PartName.Contains(Text.Trim(), StringComparison.OrdinalIgnoreCase)
            && !(lens.Title?.Contains(Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        return true;
    }
}

/// <summary>The Lens Catalogs tool's search and its Catalog Report.</summary>
public static class LensCatalogSearch
{
    /// <summary>
    /// The lenses of <paramref name="catalogs"/> that meet <paramref name="criteria"/>, in catalog
    /// order and within a catalog by part name, as the Search Results list. Throws
    /// <see cref="ArgumentException"/> listing the criteria's problems.
    /// </summary>
    public static IReadOnlyList<StockLens> Find(IEnumerable<LensCatalog> catalogs, LensSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Validate() is { Count: > 0 } problems) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        return catalogs.SelectMany(c => c.Lenses).Where(criteria.Accepts).ToArray();
    }

    /// <summary>One line of the Search Results list: part name, focal length, diameter and the
    /// three-part code (help: "part name, focal length, and diameter … After the diameter, a 3 part code").</summary>
    public static string ResultLine(StockLens lens) =>
        string.Create(CultureInfo.InvariantCulture, $"{lens.PartName}  {Mm(lens.EffectiveFocalLengthMm)}  {Mm(lens.EntrancePupilDiameterMm, "-")}  {lens.Code}");

    /// <summary>
    /// The Catalog Report (help, Lens Catalogs: "The values for the criteria used to filter the
    /// lenses are displayed at the top of the report. The lenses are then listed 3 times: once
    /// sorted alphabetically by part name, once sorted by increasing effective focal length (EFL),
    /// and once sorted by increasing entrance pupil diameter (EPD). In each listing … part name,
    /// EFL, EPD, Shape, Type, and # of Elements"). Lenses with no EFL or EPD go last in that listing.
    /// </summary>
    public static IReadOnlyList<string> Report(string catalogName, LensSearchCriteria criteria, IReadOnlyList<StockLens> lenses)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(lenses);
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Catalog report: {catalogName}",
            criteria.UseEffectiveFocalLength ? string.Create(ci, $"Effective focal length: {criteria.FocalLengthMinMm:G10} to {criteria.FocalLengthMaxMm:G10} mm") : "Effective focal length: any",
            criteria.UseEntrancePupilDiameter ? string.Create(ci, $"Entrance pupil diameter: {criteria.DiameterMinMm:G10} to {criteria.DiameterMaxMm:G10} mm") : "Entrance pupil diameter: any",
            "Shapes: " + (criteria.Shapes.Count == 0 ? "any" : string.Join(", ", criteria.Shapes.OrderBy(s => s).Select(s => s.ToString()))),
            "Types: " + (criteria.Types.Count == 0 ? "any" : string.Join(", ", criteria.Types.OrderBy(t => t).Select(t => t.ToString()))),
            "Elements: " + (criteria.Elements is int n ? n.ToString(ci) : "any"),
        };
        if (!string.IsNullOrWhiteSpace(criteria.Text)) lines.Add($"Part name or title contains: {criteria.Text.Trim()}");
        lines.Add($"{lenses.Count} lens{(lenses.Count == 1 ? "" : "es")}.");
        void Listing(string title, IEnumerable<StockLens> sorted)
        {
            lines.Add("");
            lines.Add(title);
            lines.Add(string.Format(ci, "{0,-24} {1,14} {2,12} {3,6} {4,5} {5,9}", "Part", "EFL (mm)", "EPD (mm)", "Shape", "Type", "Elements"));
            foreach (var l in sorted)
                lines.Add(string.Format(ci, "{0,-24} {1,14} {2,12} {3,6} {4,5} {5,9}", l.PartName, Mm(l.EffectiveFocalLengthMm), Mm(l.EntrancePupilDiameterMm, "-"),
                    l.ShapeCode, l.TypeCode, l.Elements));
        }
        Listing("Sorted by part name", lenses.OrderBy(l => l.PartName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Source, StringComparer.OrdinalIgnoreCase));
        Listing("Sorted by effective focal length", lenses.OrderBy(l => l.EffectiveFocalLengthMm is null).ThenBy(l => l.EffectiveFocalLengthMm ?? 0)
            .ThenBy(l => l.PartName, StringComparer.OrdinalIgnoreCase));
        Listing("Sorted by entrance pupil diameter", lenses.OrderBy(l => l.EntrancePupilDiameterMm is null).ThenBy(l => l.EntrancePupilDiameterMm ?? 0)
            .ThenBy(l => l.PartName, StringComparer.OrdinalIgnoreCase));
        return lines;
    }

    private static string Mm(double? v, string none = "afocal") => v is double d ? d.ToString("G8", CultureInfo.InvariantCulture) : none;
}
