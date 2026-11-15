using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// The grid of a Grid Sag surface as its data file gives it (OP-25, docs/Optics-Conventions.md
/// §55), in the file's own unit so a saved lens writes the numbers back digit for digit.
/// Layout from OpticStudio 2025 R2 help, "Grid Sag", "Importing Grid Data" (saved under
/// plans/sources/optics/op-25-help/):
/// <list type="bullet">
/// <item>First line: "nx ny delx dely unitflag xdec ydec": the number of points in x and y (each
/// at least 5), the spacings ("only the absolute values of delx and dely are taken into
/// account"), the unit flag "0 for mm, 1 for cm, 2 for in, and 3 for meters", and the decenter
/// of the grid relative to the base surface.</item>
/// <item>Then nx·ny lines "z dz/dx dz/dy d2z/dxdy nodata": "The first data line in the file
/// corresponds to the upper left corner of the surface, that is, the corner defined by -x and +y
/// limits. Each point that follows is read across the face of the surface from left to right.
/// After nx points, the nx+1 point is read in as the first value in row 2".</item>
/// <item>"sag and cross derivative values have dimensions, and are therefore scaled, but first
/// derivative values are dimensionless"; a nodata value of 1 replaces the whole line with zeros;
/// lines starting with "!" are comments.</item>
/// </list>
/// Points are stored in file order (row 0 the +y row, each row from −x to +x).
/// </summary>
public sealed class GridSagData
{
    /// <summary>OpticStudio's smallest grid: "The nx and ny values must be no smaller than 5".</summary>
    public const int MinPoints = 5;

    public GridSagData(int nx, int ny, double deltaX, double deltaY, int unitFlag, double decenterX, double decenterY,
        IReadOnlyList<double> sag, IReadOnlyList<double> slopeX, IReadOnlyList<double> slopeY,
        IReadOnlyList<double> cross, IReadOnlyList<bool>? noData = null)
    {
        if (nx < MinPoints || ny < MinPoints)
            throw new ArgumentException($"Grid Sag data: the grid is {nx} × {ny} points; nx and ny must each be at least {MinPoints}.");
        if ((long)nx * ny > 100_000_000)
            throw new ArgumentException($"Grid Sag data: {nx} × {ny} points is more than this program holds (10⁸).");
        deltaX = Math.Abs(deltaX);
        deltaY = Math.Abs(deltaY);
        if (!(deltaX > 0) || !(deltaY > 0) || !double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentException($"Grid Sag data: the spacings ({deltaX}, {deltaY}) must be positive lengths.");
        if (unitFlag is < 0 or > 3)
            throw new ArgumentException($"Grid Sag data: the unit flag is {unitFlag}; it must be 0 (mm), 1 (cm), 2 (in) or 3 (m).");
        if (!double.IsFinite(decenterX) || !double.IsFinite(decenterY))
            throw new ArgumentException($"Grid Sag data: the decenter ({decenterX}, {decenterY}) must be finite.");
        int n = nx * ny;
        void Check(IReadOnlyList<double> a, string what)
        {
            ArgumentNullException.ThrowIfNull(a, what);
            if (a.Count != n)
                throw new ArgumentException($"Grid Sag data: {a.Count} {what} values for a {nx} × {ny} grid; there must be {n}.");
            for (int i = 0; i < n; i++)
                if (!double.IsFinite(a[i]))
                    throw new ArgumentException($"Grid Sag data: point {i + 1} ({what}) is {a[i]}; every value must be finite.");
        }
        Check(sag, "sag");
        Check(slopeX, "dz/dx");
        Check(slopeY, "dz/dy");
        Check(cross, "d2z/dxdy");
        if (noData is not null && noData.Count != n)
            throw new ArgumentException($"Grid Sag data: {noData.Count} nodata flags for a {nx} × {ny} grid; there must be {n}.");
        Nx = nx;
        Ny = ny;
        DeltaX = deltaX;
        DeltaY = deltaY;
        UnitFlag = unitFlag;
        DecenterX = decenterX;
        DecenterY = decenterY;
        Sag = sag.ToArray();
        SlopeX = slopeX.ToArray();
        SlopeY = slopeY.ToArray();
        Cross = cross.ToArray();
        NoData = noData is null || !noData.Any(b => b) ? null : noData.ToArray();
    }

    public int Nx { get; }
    public int Ny { get; }

    /// <summary>The x spacing, in the file's unit.</summary>
    public double DeltaX { get; }

    /// <summary>The y spacing, in the file's unit.</summary>
    public double DeltaY { get; }

    /// <summary>0 mm, 1 cm, 2 in, 3 m (the values of <see cref="LensUnit"/>).</summary>
    public int UnitFlag { get; }

    /// <summary>The file's unit.</summary>
    public LensUnit Unit => (LensUnit)UnitFlag;

    /// <summary>The grid's decenter, in the file's unit.</summary>
    public double DecenterX { get; }
    public double DecenterY { get; }

    /// <summary>z per point in file order, the file's unit.</summary>
    public IReadOnlyList<double> Sag { get; }

    /// <summary>dz/dx per point, dimensionless.</summary>
    public IReadOnlyList<double> SlopeX { get; }

    /// <summary>dz/dy per point, dimensionless.</summary>
    public IReadOnlyList<double> SlopeY { get; }

    /// <summary>d²z/dxdy per point, inverse file units.</summary>
    public IReadOnlyList<double> Cross { get; }

    /// <summary>The nodata flags, or null when no point has one.</summary>
    public IReadOnlyList<bool>? NoData { get; }
}

/// <summary>Reads and writes the Grid Sag data file (<see cref="GridSagData"/>).</summary>
public static class GridSagFile
{
    /// <summary>Reads a grid file; throws <see cref="FormatException"/> naming the line.</summary>
    public static GridSagData Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new StreamReader(path);
        return Read(reader, Path.GetFileName(path));
    }

    /// <summary>
    /// Reads a grid from <paramref name="reader"/>; <paramref name="name"/> names it in messages.
    /// Blank lines and lines starting with "!" are skipped. A data line holds four numbers and an
    /// optional nodata flag (0 or blank: valid; 1: "the whole line of entries will be replaced
    /// with zeros"). OpticStudio replaces an entry that "is not a number" with zero; this reader
    /// refuses it, naming the line, rather than guess (§55).
    /// </summary>
    public static GridSagData Read(TextReader reader, string name = "grid file")
    {
        ArgumentNullException.ThrowIfNull(reader);
        int lineNumber = 0;
        string? line;
        string[]? header = null;
        int headerLine = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('!')) continue;
            header = Split(t);
            headerLine = lineNumber;
            break;
        }
        if (header is null) throw new FormatException($"{name}: the file is empty; the first line must be 'nx ny delx dely unitflag xdec ydec'.");
        if (header.Length < 7)
            throw new FormatException($"{name}, line {headerLine}: the first line has {header.Length} values; it must be 'nx ny delx dely unitflag xdec ydec' (7 values).");
        int nx = Integer(header[0], name, headerLine, "nx");
        int ny = Integer(header[1], name, headerLine, "ny");
        double dx = Number(header[2], name, headerLine, "delx");
        double dy = Number(header[3], name, headerLine, "dely");
        int unit = Integer(header[4], name, headerLine, "unitflag");
        double xdec = Number(header[5], name, headerLine, "xdec");
        double ydec = Number(header[6], name, headerLine, "ydec");
        if (nx < GridSagData.MinPoints || ny < GridSagData.MinPoints)
            throw new FormatException($"{name}, line {headerLine}: the grid is {nx} × {ny}; nx and ny must each be at least {GridSagData.MinPoints}.");
        if (unit is < 0 or > 3)
            throw new FormatException($"{name}, line {headerLine}: the unit flag is {unit}; it must be 0 (mm), 1 (cm), 2 (in) or 3 (m).");
        int n = checked(nx * ny);
        var z = new double[n];
        var zx = new double[n];
        var zy = new double[n];
        var zxy = new double[n];
        var nodata = new bool[n];
        int k = 0;
        while (k < n && (line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('!')) continue;
            var f = Split(t);
            if (f.Length < 4 || f.Length > 5)
                throw new FormatException($"{name}, line {lineNumber} (point {k + 1}): {f.Length} values; a data line is 'z dz/dx dz/dy d2z/dxdy [nodata]'.");
            bool missing = f.Length == 5 && Integer(f[4], name, lineNumber, "nodata") switch
            {
                0 => false,
                1 => true,
                var v => throw new FormatException($"{name}, line {lineNumber}: the nodata flag is {v}; it must be 0 or 1."),
            };
            if (!missing)
            {
                z[k] = Number(f[0], name, lineNumber, "z");
                zx[k] = Number(f[1], name, lineNumber, "dz/dx");
                zy[k] = Number(f[2], name, lineNumber, "dz/dy");
                zxy[k] = Number(f[3], name, lineNumber, "d2z/dxdy");
            }
            nodata[k] = missing;
            k++;
        }
        if (k < n)
            throw new FormatException($"{name}: the grid is {nx} × {ny} = {n} points but the file ends after {k}.");
        try
        {
            return new GridSagData(nx, ny, dx, dy, unit, xdec, ydec, z, zx, zy, zxy, nodata);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>The file text of <paramref name="data"/> (round-trip formatting).</summary>
    public static string Write(GridSagData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"{data.Nx} {data.Ny} {data.DeltaX:R} {data.DeltaY:R} {data.UnitFlag} {data.DecenterX:R} {data.DecenterY:R}\n");
        for (int i = 0; i < data.Nx * data.Ny; i++)
        {
            sb.Append(c, $"{data.Sag[i]:R} {data.SlopeX[i]:R} {data.SlopeY[i]:R} {data.Cross[i]:R}");
            if (data.NoData is { } nd && nd[i]) sb.Append(" 1");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string[] Split(string line) =>
        line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

    private static double Number(string s, string name, int line, string what)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) return v;
        throw new FormatException($"{name}, line {line}: {what} '{s}' is not a finite number.");
    }

    private static int Integer(string s, string name, int line, string what)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && Math.Round(d) == d && Math.Abs(d) < int.MaxValue) return (int)d;
        throw new FormatException($"{name}, line {line}: {what} '{s}' is not a whole number.");
    }
}
