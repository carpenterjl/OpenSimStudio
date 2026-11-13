namespace OpenSim.Core.Numerics;

/// <summary>
/// A bicubic spline through values on a rectangular grid: the tensor product of natural cubic
/// splines. The surface passes through every grid value, and it and its first and second
/// derivatives are continuous everywhere inside the grid.
/// <para>
/// Construction fits one natural spline along y for each grid x. An evaluation at (x, y) takes
/// the value of each of those at y and fits a natural spline through them along x. Fitting a
/// spline is linear in its data, so the y derivative is the same x spline through the y
/// derivatives. One evaluation costs a number of operations proportional to the x count.
/// </para>
/// </summary>
public sealed class BicubicSpline
{
    private readonly double[] _x;
    private readonly NaturalCubicSpline[] _alongY;   // one per grid x

    /// <param name="x">Strictly increasing, at least 3 values.</param>
    /// <param name="y">Strictly increasing, at least 3 values.</param>
    /// <param name="values">values[i, j] is the surface at (x[i], y[j]).</param>
    public BicubicSpline(double[] x, double[] y, double[,] values)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(values);
        if (values.GetLength(0) != x.Length || values.GetLength(1) != y.Length)
            throw new ArgumentException(
                $"The value grid is {values.GetLength(0)} × {values.GetLength(1)} but the axes have {x.Length} and {y.Length} points.");
        if (x.Length < 3) throw new ArgumentException("A cubic spline needs at least 3 knots.", nameof(x));
        for (int i = 1; i < x.Length; i++)
            if (x[i] <= x[i - 1])
                throw new ArgumentException("Spline abscissa must be strictly increasing.", nameof(x));

        _x = (double[])x.Clone();
        var yKnots = (double[])y.Clone();
        _alongY = new NaturalCubicSpline[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            var row = new double[y.Length];
            for (int j = 0; j < y.Length; j++) row[j] = values[i, j];
            _alongY[i] = new NaturalCubicSpline(yKnots, row);
        }
    }

    public (double Min, double Max) RangeX => (_x[0], _x[^1]);

    public (double Min, double Max) RangeY => _alongY[0].Range;

    /// <summary>The surface value at (x, y). Throws outside the grid.</summary>
    public double Evaluate(double x, double y)
    {
        var column = new double[_x.Length];
        for (int i = 0; i < column.Length; i++) column[i] = _alongY[i].Evaluate(y);
        return new NaturalCubicSpline(_x, column).Evaluate(x);
    }

    /// <summary>The surface value and its two first derivatives at (x, y).</summary>
    public (double Value, double Dx, double Dy) EvaluateWithGradient(double x, double y)
    {
        var column = new double[_x.Length];
        var slope = new double[_x.Length];
        for (int i = 0; i < column.Length; i++)
        {
            column[i] = _alongY[i].Evaluate(y);
            slope[i] = _alongY[i].Derivative(y);
        }
        var alongX = new NaturalCubicSpline(_x, column);
        return (alongX.Evaluate(x), alongX.Derivative(x), new NaturalCubicSpline(_x, slope).Evaluate(x));
    }
}
