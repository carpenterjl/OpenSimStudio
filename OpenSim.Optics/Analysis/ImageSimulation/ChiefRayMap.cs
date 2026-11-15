namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>
/// Where the real chief ray of each point of a picture lands on the image surface, for one
/// wavelength (OP-42): distortion and, wavelength by wavelength, lateral colour. The chief rays
/// are traced at a grid of nodes spanning the picture's outer edges (pixel coordinate −½ to
/// W − ½ across, −½ to H − ½ down); between them the landing point is interpolated bilinearly
/// and beyond them extrapolated from the edge cells. <see cref="Inverse"/> finds the picture
/// point that lands on a given image point by Newton's method on that interpolant.
/// </summary>
public sealed class ChiefRayMap
{
    private readonly double[,] _x, _y;
    private readonly int _w, _h;

    /// <param name="x">Image x (meters, image surface frame) at node [b, a].</param>
    /// <param name="y">Image y at node [b, a].</param>
    /// <param name="width">The picture's columns.</param>
    /// <param name="height">The picture's rows.</param>
    public ChiefRayMap(double[,] x, double[,] y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.GetLength(0) < 2 || x.GetLength(1) < 2 || y.GetLength(0) != x.GetLength(0) || y.GetLength(1) != x.GetLength(1))
            throw new ArgumentException("Chief-ray map: at least 2 × 2 nodes, x and y of one size.");
        _x = x; _y = y; _w = width; _h = height;
    }

    public int NodesX => _x.GetLength(1);
    public int NodesY => _x.GetLength(0);

    /// <summary>The pixel coordinate of node a of n across p pixels: −½ + a·p/(n − 1).</summary>
    public static double Node(int a, int n, int pixels) => -0.5 + a * (double)pixels / (n - 1);

    /// <summary>The landing point of picture point (column coordinate cx, row coordinate ry).</summary>
    public (double X, double Y) Evaluate(double cx, double ry)
    {
        var (a, u) = Cell(cx, NodesX, _w);
        var (b, v) = Cell(ry, NodesY, _h);
        double X = (1 - v) * ((1 - u) * _x[b, a] + u * _x[b, a + 1]) + v * ((1 - u) * _x[b + 1, a] + u * _x[b + 1, a + 1]);
        double Y = (1 - v) * ((1 - u) * _y[b, a] + u * _y[b, a + 1]) + v * ((1 - u) * _y[b + 1, a] + u * _y[b + 1, a + 1]);
        return (X, Y);
    }

    /// <summary>∂(X, Y)/∂(cx, ry) of the interpolant at a point: (∂X/∂cx, ∂Y/∂cx, ∂X/∂ry, ∂Y/∂ry).</summary>
    public (double Xc, double Yc, double Xr, double Yr) Derivative(double cx, double ry)
    {
        var (a, u) = Cell(cx, NodesX, _w);
        var (b, v) = Cell(ry, NodesY, _h);
        double sx = (double)_w / (NodesX - 1), sy = (double)_h / (NodesY - 1);
        double xc = ((1 - v) * (_x[b, a + 1] - _x[b, a]) + v * (_x[b + 1, a + 1] - _x[b + 1, a])) / sx;
        double yc = ((1 - v) * (_y[b, a + 1] - _y[b, a]) + v * (_y[b + 1, a + 1] - _y[b + 1, a])) / sx;
        double xr = ((1 - u) * (_x[b + 1, a] - _x[b, a]) + u * (_x[b + 1, a + 1] - _x[b, a + 1])) / sy;
        double yr = ((1 - u) * (_y[b + 1, a] - _y[b, a]) + u * (_y[b + 1, a + 1] - _y[b, a + 1])) / sy;
        return (xc, yc, xr, yr);
    }

    private static (int Cell, double T) Cell(double coordinate, int nodes, int pixels)
    {
        double s = (coordinate + 0.5) * (nodes - 1) / pixels;
        int i = (int)Math.Floor(s);
        if (i < 0) i = 0;
        if (i > nodes - 2) i = nodes - 2;
        return (i, s - i);
    }

    /// <summary>The picture point landing on (X, Y), from a start point; throws when Newton's
    /// method does not converge (a fold in the map: the chief rays cross).</summary>
    public (double Cx, double Ry) Inverse(double x, double y, double startCx, double startRy, string who)
    {
        double cx = startCx, ry = startRy;
        for (int k = 0; k < 60; k++)
        {
            var (fx, fy) = Evaluate(cx, ry);
            var (xc, yc, xr, yr) = Derivative(cx, ry);
            double det = xc * yr - xr * yc;
            if (!(Math.Abs(det) > 0) || !double.IsFinite(det))
                throw new InvalidOperationException($"{who}: the chief-ray map is singular near picture point ({cx:g6}, {ry:g6}) (the image of a pixel has no area), so the image point ({x:g6}, {y:g6}) m cannot be traced back to the picture.");
            double ex = fx - x, ey = fy - y;
            double dc = (yr * ex - xr * ey) / det, dr = (-yc * ex + xc * ey) / det;
            cx -= dc; ry -= dr;
            if (Math.Abs(dc) < 1e-10 && Math.Abs(dr) < 1e-10) return (cx, ry);
        }
        throw new InvalidOperationException($"{who}: the image point ({x:g6}, {y:g6}) m could not be traced back to the picture (Newton's method on the chief-ray map did not converge: the map folds, so the distortion is not one to one there).");
    }
}
