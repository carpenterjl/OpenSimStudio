using System.Globalization;
using System.Text;

namespace OpenSim.Geometry.Export;

/// <summary>The length unit a CAD file is written in.</summary>
public enum CadLengthUnit
{
    Millimeter = 0,
    Centimeter = 1,
    Inch = 2,
    Meter = 3,
}

/// <summary>Unit codes of the formats, each copied from its specification (cited at the member).</summary>
public static class CadUnits
{
    public static double MetersPer(CadLengthUnit unit) => unit switch
    {
        CadLengthUnit.Millimeter => 1e-3,
        CadLengthUnit.Centimeter => 1e-2,
        CadLengthUnit.Meter => 1.0,
        // The inch as 25.4 mm, as the STEP fixtures of the importer write it
        // (OpenSim.Tests/Geometry/Step/StepFixtures.cs, CONVERSION_BASED_UNIT('INCH', 25.4 mm)).
        CadLengthUnit.Inch => 25.4e-3,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>IGES 5.3 Global Section parameters 14 and 15, the units flag and name
    /// (US PRO, Initial Graphics Exchange Specification 5.3, §2.2.4.3.14–15): 1 inches (2HIN),
    /// 2 millimeters (2HMM), 6 meters (1HM), 10 centimeters (2HCM).</summary>
    public static (int Flag, string Name) Iges(CadLengthUnit unit) => unit switch
    {
        CadLengthUnit.Inch => (1, "IN"),
        CadLengthUnit.Millimeter => (2, "MM"),
        CadLengthUnit.Meter => (6, "M"),
        CadLengthUnit.Centimeter => (10, "CM"),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>The DXF header variable $INSUNITS (Autodesk, AutoCAD 2012 DXF Reference, "HEADER
    /// Section", $INSUNITS): 1 inches, 4 millimeters, 5 centimeters, 6 meters.</summary>
    public static int DxfInsUnits(CadLengthUnit unit) => unit switch
    {
        CadLengthUnit.Inch => 1,
        CadLengthUnit.Millimeter => 4,
        CadLengthUnit.Centimeter => 5,
        CadLengthUnit.Meter => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>Plain 7-bit text for a name in a file (the formats' strings are ASCII): anything
    /// else becomes '_'.</summary>
    public static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(c is >= ' ' and <= '~' ? c : '_');
        return sb.ToString();
    }

    internal static string G(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}

/// <summary>What every writer needs besides the scene.</summary>
public sealed record CadWriteOptions
{
    /// <summary>The length unit of the file.</summary>
    public CadLengthUnit Unit { get; init; } = CadLengthUnit.Millimeter;

    /// <summary>
    /// The export tolerance, meters: the largest deviation a spline fit (STEP, IGES) may have from
    /// the optical surface, and the chordal error of a triangulation (STL) or a sampled profile
    /// (DXF). Stated in the files: STEP's uncertainty measure, IGES's minimum resolution.
    /// </summary>
    public double Tolerance { get; init; } = 1e-7;

    /// <summary>The product or file name written into the file's header.</summary>
    public string Name { get; init; } = "lens";

    /// <summary>The file's time stamp; null for now (tests fix it so a file is reproducible).</summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>The IGES level of lens geometry and of rays (OpticStudio's "Lens/Ray Layer").</summary>
    public int LensLayer { get; init; } = 1;

    public int RayLayer { get; init; } = 2;

    internal DateTime Stamp => Timestamp ?? DateTime.Now;

    internal double PerUnit => 1 / CadUnits.MetersPer(Unit);

    internal void Validate()
    {
        if (!(Tolerance > 0) || !double.IsFinite(Tolerance))
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "The export tolerance must be positive.");
        if (LensLayer < 0 || RayLayer < 0)
            throw new ArgumentOutOfRangeException(nameof(LensLayer), "Layers must not be negative.");
    }
}

/// <summary>What a writer wrote: counts and the largest spline deviation, for the log.</summary>
public sealed record CadWriteReport
{
    public int Solids { get; init; }
    public int Sheets { get; init; }
    public int Faces { get; init; }
    public int Triangles { get; init; }
    public int Polylines { get; init; }

    /// <summary>The largest |spline − surface| any fit measured, meters (0 when nothing was fitted).</summary>
    public double MaxFitDeviation { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}
