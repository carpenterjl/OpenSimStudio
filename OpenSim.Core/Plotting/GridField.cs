namespace OpenSim.Core.Plotting;

/// <summary>
/// A scalar sampled on a regular grid: sample [r, c] sits at (<see cref="XAt"/>(c),
/// <see cref="YAt"/>(r)), with column 0 at <see cref="X0"/>, the last column at <see cref="X1"/>,
/// row 0 at <see cref="Y0"/> and the last row at <see cref="Y1"/>. The node convention (samples on
/// the corners, not at cell centres) is what contouring and bilinear interpolation need; an image
/// drawn from it therefore extends half a sample beyond X0..X1 on each side.
/// <para>
/// The array is held, not copied (a 1024 × 1024 PSF is 8 MB); callers must not change it after
/// handing it over. Non-finite samples are allowed and mean "no data": contours skip the cells
/// that touch them and readouts report them as such.
/// </para>
/// </summary>
public sealed class GridField
{
    public GridField(double[,] values, double x0, double x1, double y0, double y1)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.GetLength(0) < 2 || values.GetLength(1) < 2)
            throw new ArgumentException(
                $"A grid field needs at least 2 x 2 samples, got {values.GetLength(0)} x {values.GetLength(1)}.",
                nameof(values));
        if (!(x1 > x0) || !(y1 > y0) || !double.IsFinite(x1 - x0) || !double.IsFinite(y1 - y0))
            throw new ArgumentException(
                $"A grid field needs x1 > x0 and y1 > y0, got x {x0}..{x1}, y {y0}..{y1}.");
        Values = values;
        X0 = x0; X1 = x1; Y0 = y0; Y1 = y1;
    }

    /// <summary>Samples, [row, column]: rows run in y, columns in x.</summary>
    public double[,] Values { get; }
    public double X0 { get; }
    public double X1 { get; }
    public double Y0 { get; }
    public double Y1 { get; }
    public int Rows => Values.GetLength(0);
    public int Columns => Values.GetLength(1);
    public double DeltaX => (X1 - X0) / (Columns - 1);
    public double DeltaY => (Y1 - Y0) / (Rows - 1);

    public double XAt(int column) => column == Columns - 1 ? X1 : X0 + column * DeltaX;
    public double YAt(int row) => row == Rows - 1 ? Y1 : Y0 + row * DeltaY;

    /// <summary>Bilinear interpolation inside the grid; NaN outside it or where a corner of the
    /// containing cell has no data.</summary>
    public double Interpolate(double x, double y)
    {
        if (!(x >= X0 && x <= X1 && y >= Y0 && y <= Y1)) return double.NaN;
        double u = (x - X0) / DeltaX, v = (y - Y0) / DeltaY;
        int c = Math.Min((int)Math.Floor(u), Columns - 2);
        int r = Math.Min((int)Math.Floor(v), Rows - 2);
        double fu = u - c, fv = v - r;
        double a = Values[r, c], b = Values[r, c + 1], d = Values[r + 1, c], e = Values[r + 1, c + 1];
        return (1 - fv) * ((1 - fu) * a + fu * b) + fv * ((1 - fu) * d + fu * e);
    }

    /// <summary>The sample nearest (x, y), or null when the point is more than half a sample
    /// outside the grid (the extent an image of the field covers).</summary>
    public (int Row, int Column)? NearestSample(double x, double y)
    {
        double u = Math.Round((x - X0) / DeltaX), v = Math.Round((y - Y0) / DeltaY);
        if (!(u >= 0 && u <= Columns - 1 && v >= 0 && v <= Rows - 1)) return null;
        return ((int)v, (int)u);
    }

    /// <summary>Smallest and largest finite sample; (NaN, NaN) when there is none.</summary>
    public (double Min, double Max) FiniteRange()
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in Values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return min <= max ? (min, max) : (double.NaN, double.NaN);
    }

    /// <summary>Samples a function at the grid nodes (tests and synthetic fields).</summary>
    public static GridField FromFunction(Func<double, double, double> f, int rows, int columns,
        double x0, double x1, double y0, double y1)
    {
        var values = new double[rows, columns];
        var shape = new GridField(values, x0, x1, y0, y1);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                values[r, c] = f(shape.XAt(c), shape.YAt(r));
        return shape;
    }
}
