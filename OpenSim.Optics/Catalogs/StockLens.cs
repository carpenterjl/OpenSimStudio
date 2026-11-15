namespace OpenSim.Optics.Catalogs;

// OP-16: stock lens catalogs and lens search (docs/Optics-Conventions.md section 53).

/// <summary>
/// The shape code of a stock lens, as OpticStudio's Lens Catalogs tool lists it: "E, B, P, M, or
/// '?', for Equi-, Bi-, Plano-, Meniscus, or other. Other is used if the lens is multi-element"
/// (OpticStudio 2025 R2 help, Libraries tab › Stock Parts Group › Lens Catalogs, "Discussion";
/// plans/sources/optics/op-16-help/Lens_Catalogs.html). The rules that sort a singlet into these
/// classes are this program's (section 53), the help gives none.
/// </summary>
public enum StockLensShape
{
    Equi,
    Bi,
    Plano,
    Meniscus,
    Other,
}

/// <summary>
/// The type code of a stock lens: "S, G, A, or T, for Spherical, GRIN, Aspheric, or Toroidal";
/// "The 'Spherical' category is also used as 'other' for lenses which are not gradient index,
/// aspheric, or toroidal" (same help page).
/// </summary>
public enum StockLensType
{
    Spherical,
    Grin,
    Aspheric,
    Toroidal,
}

/// <summary>
/// One lens of a stock lens catalog: the part name, the catalog (vendor) it belongs to, the file
/// it was read from, and the search data the Lens Catalogs tool lists (EFL, entrance pupil
/// diameter, shape, type, number of elements). Lengths are millimeters, as the help gives the
/// search ranges ("in millimeters"), whatever the file's lens unit.
/// </summary>
public sealed record StockLens
{
    /// <summary>The part name: the first space-delimited word of the file name, as Make Private
    /// Lens Catalog assigns it ("the first space delimited word in the name of the lens file in
    /// the source folder is used as the 'part name'"; help, Make Private Lens Catalog).</summary>
    public required string PartName { get; init; }

    /// <summary>The catalog the lens is in (the vendor list's entry).</summary>
    public required string Vendor { get; init; }

    /// <summary>The file name (.zmx, .zar, .seq or .len) the lens was read from, without a folder.</summary>
    public required string Source { get; init; }

    /// <summary>The file's title (NAME line), or null.</summary>
    public string? Title { get; init; }

    /// <summary>The paraxial effective focal length at the file's primary wavelength, mm; null
    /// for an afocal lens.</summary>
    public double? EffectiveFocalLengthMm { get; init; }

    /// <summary>The paraxial entrance pupil diameter of the file's system, mm; null when the
    /// paraxial model gives none.</summary>
    public double? EntrancePupilDiameterMm { get; init; }

    public StockLensShape Shape { get; init; }

    public StockLensType Type { get; init; }

    /// <summary>The number of elements: lens surfaces followed by a glass, plus mirrors.</summary>
    public int Elements { get; init; }

    /// <summary>The file's lens unit, as saved ("mm", "cm", "in").</summary>
    public string? LensUnit { get; init; }

    /// <summary>The one-letter shape code E, B, P, M or ?.</summary>
    public string ShapeCode => StockLensCodes.Shape(Shape);

    /// <summary>The one-letter type code S, G, A or T.</summary>
    public string TypeCode => StockLensCodes.Type(Type);

    /// <summary>The three-part code the help shows after the diameter, "(P,S, 1)" written (P,S,1).</summary>
    public string Code => $"({ShapeCode},{TypeCode},{Elements})";
}

/// <summary>The one-letter codes of <see cref="StockLensShape"/> and <see cref="StockLensType"/>.</summary>
public static class StockLensCodes
{
    public static string Shape(StockLensShape shape) => shape switch
    {
        StockLensShape.Equi => "E",
        StockLensShape.Bi => "B",
        StockLensShape.Plano => "P",
        StockLensShape.Meniscus => "M",
        _ => "?",
    };

    public static string Type(StockLensType type) => type switch
    {
        StockLensType.Grin => "G",
        StockLensType.Aspheric => "A",
        StockLensType.Toroidal => "T",
        _ => "S",
    };

    /// <summary>The shape of a code letter (case ignored); throws naming the letter.</summary>
    public static StockLensShape ParseShape(char c) => char.ToUpperInvariant(c) switch
    {
        'E' => StockLensShape.Equi,
        'B' => StockLensShape.Bi,
        'P' => StockLensShape.Plano,
        'M' => StockLensShape.Meniscus,
        '?' => StockLensShape.Other,
        _ => throw new FormatException($"'{c}' is not a stock lens shape code (E, B, P, M or ?)."),
    };

    /// <summary>The type of a code letter (case ignored); throws naming the letter.</summary>
    public static StockLensType ParseType(char c) => char.ToUpperInvariant(c) switch
    {
        'S' => StockLensType.Spherical,
        'G' => StockLensType.Grin,
        'A' => StockLensType.Aspheric,
        'T' => StockLensType.Toroidal,
        _ => throw new FormatException($"'{c}' is not a stock lens type code (S, G, A or T)."),
    };
}
