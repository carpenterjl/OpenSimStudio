using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>A blur kernel on whole source-pixel offsets: <see cref="Weights"/>[i, k] is the
/// share of a pixel's light that lands <c>i − CentreRow</c> rows and <c>k − CentreColumn</c>
/// columns away.</summary>
public sealed record PixelKernel(double[,] Weights, int CentreRow, int CentreColumn)
{
    public int Rows => Weights.GetLength(0);
    public int Columns => Weights.GetLength(1);

    /// <summary>All the light on the pixel itself.</summary>
    public static PixelKernel Delta { get; } = new(new double[,] { { 1 } }, 0, 0);

    /// <summary>Σ weights.</summary>
    public double Sum
    {
        get
        {
            double s = 0;
            foreach (var v in Weights) s += v;
            return s;
        }
    }
}

/// <summary>
/// The convolution of a picture with a point spread function that varies across it, the PSF
/// interpolated bilinearly between a grid of PSFs (OP-42; docs/Optics-Conventions.md §67).
/// <para>
/// <b>Derivation.</b> The grid has gx × gy nodes spanning the picture's outer edges (help,
/// "Image Simulation", the PSF-grid figures: corners at f ± H/2, spacing H/(n − 1)); node (a, b)
/// sits at column coordinate x_a = −½ + a·W/(gx − 1) and row coordinate y_b = −½ + b·H/(gy − 1),
/// pixel (r, c) having its centre at (c, r). The interpolated kernel at pixel p is
/// K_p = Σ_g t_g(p)·K_g with t_g the bilinear tent weights (Σ_g t_g = 1 everywhere), so the
/// blurred picture is B(q) = Σ_p S(p)·Σ_g t_g(p)·K_g(q − p) = Σ_g [(t_g·S) ⊛ K_g](q): one
/// ordinary convolution per node of the picture weighted by that node's tent, which is zero
/// beyond the neighbouring nodes. Each is computed over the tent's support only, by the FFT, and
/// added into place (overlap-add). The identity is exact, so a grid of equal kernels gives one
/// ordinary convolution of the whole picture. A single node (gx = gy = 1) weighs 1 everywhere.
/// </para>
/// <para>
/// The result has the picture's size: light spread beyond its edges is lost (the help: "the
/// convolution loses the "spread" outside the original bitmap ... The guard band adds a black
/// region around the original image"). Nodes are computed in parallel and added in node order,
/// so the result does not depend on the thread count.
/// </para>
/// </summary>
public static class SpatiallyVariantConvolution
{
    /// <summary>The column (or row) coordinate of node <paramref name="a"/> of
    /// <paramref name="count"/> across <paramref name="pixels"/> pixels.</summary>
    public static double NodeCoordinate(int a, int count, int pixels) =>
        count == 1 ? (pixels - 1) / 2.0 : -0.5 + a * (double)pixels / (count - 1);

    /// <summary>The tent weight of node <paramref name="a"/> at pixel coordinate <paramref name="x"/>.</summary>
    public static double Tent(int a, int count, int pixels, double x)
    {
        if (count == 1) return 1;
        double spacing = (double)pixels / (count - 1);
        double t = 1 - Math.Abs(x - NodeCoordinate(a, count, pixels)) / spacing;
        // The picture's centres lie inside the outer nodes, so the end tents need no extension.
        return t > 0 ? t : 0;
    }

    /// <summary>B = Σ_g (t_g·S) ⊛ K_g on the picture's own pixels.</summary>
    /// <param name="source">The picture [row, column].</param>
    /// <param name="gridX">Nodes across.</param>
    /// <param name="gridY">Nodes down.</param>
    /// <param name="kernel">The kernel of node (a across, b down).</param>
    public static double[,] Apply(double[,] source, int gridX, int gridY, Func<int, int, PixelKernel> kernel,
        int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(kernel);
        int h = source.GetLength(0), w = source.GetLength(1);
        if (gridX < 1 || gridY < 1) throw new ArgumentException($"PSF grid: at least one node each way (it is {gridX} × {gridY}).");
        var kernels = new PixelKernel[gridX * gridY];
        for (int b = 0; b < gridY; b++)
            for (int a = 0; a < gridX; a++) kernels[b * gridX + a] = kernel(a, b);

        var parts = ParallelSlots.Compute<Part?>(gridX * gridY, maxDegreeOfParallelism, g =>
        {
            cancellation.ThrowIfCancellationRequested();
            int a = g % gridX, b = g / gridX;
            var (c0, c1) = Support(a, gridX, w);
            var (r0, r1) = Support(b, gridY, h);
            if (c1 < c0 || r1 < r0) return null;
            int rows = r1 - r0 + 1, columns = c1 - c0 + 1;
            var weighted = new double[rows, columns];
            bool any = false;
            for (int r = 0; r < rows; r++)
            {
                double ty = Tent(b, gridY, h, r0 + r);
                for (int c = 0; c < columns; c++)
                {
                    double v = source[r0 + r, c0 + c] * ty * Tent(a, gridX, w, c0 + c);
                    weighted[r, c] = v;
                    any |= v != 0;
                }
            }
            if (!any) return null;
            var full = Convolve(weighted, kernels[g].Weights);
            return new Part(full, r0 - kernels[g].CentreRow, c0 - kernels[g].CentreColumn);
        }, cancellation);

        var result = new double[h, w];
        foreach (var part in parts)
        {
            if (part is null) continue;
            int rows = part.Values.GetLength(0), columns = part.Values.GetLength(1);
            for (int r = 0; r < rows; r++)
            {
                int y = part.Row + r;
                if (y < 0 || y >= h) continue;
                for (int c = 0; c < columns; c++)
                {
                    int x = part.Column + c;
                    if (x >= 0 && x < w) result[y, x] += part.Values[r, c];
                }
            }
        }
        return result;
    }

    private sealed record Part(double[,] Values, int Row, int Column);

    /// <summary>The first and last pixel where node a's tent is positive.</summary>
    private static (int First, int Last) Support(int a, int count, int pixels)
    {
        if (count == 1) return (0, pixels - 1);
        double spacing = (double)pixels / (count - 1);
        double x = NodeCoordinate(a, count, pixels);
        int first = Math.Max(0, (int)Math.Floor(x - spacing) + 1);
        int last = Math.Min(pixels - 1, (int)Math.Ceiling(x + spacing) - 1);
        while (first <= last && Tent(a, count, pixels, first) <= 0) first++;
        while (last >= first && Tent(a, count, pixels, last) <= 0) last--;
        return (first, last);
    }

    /// <summary>The full linear convolution of two real arrays (size m + k − 1 each way) by the
    /// FFT; a direct sum when one is a single sample.</summary>
    public static double[,] Convolve(double[,] x, double[,] k)
    {
        int xr = x.GetLength(0), xc = x.GetLength(1), kr = k.GetLength(0), kc = k.GetLength(1);
        int rows = xr + kr - 1, columns = xc + kc - 1;
        var result = new double[rows, columns];
        if (kr == 1 && kc == 1)
        {
            double s = k[0, 0];
            for (int r = 0; r < xr; r++)
                for (int c = 0; c < xc; c++) result[r, c] = x[r, c] * s;
            return result;
        }
        int fr = NextFast(rows), fc = NextFast(columns);
        var a = new Complex[fr, fc];
        var b = new Complex[fr, fc];
        for (int r = 0; r < xr; r++) for (int c = 0; c < xc; c++) a[r, c] = x[r, c];
        for (int r = 0; r < kr; r++) for (int c = 0; c < kc; c++) b[r, c] = k[r, c];
        var fa = Fft2D.Forward(a);
        var fb = Fft2D.Forward(b);
        for (int r = 0; r < fr; r++) for (int c = 0; c < fc; c++) fa[r, c] *= fb[r, c];
        var p = Fft2D.Inverse(fa);
        for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) result[r, c] = p[r, c].Real;
        return result;
    }

    /// <summary>The next power of two (radix-2 is the FFT's fast path).</summary>
    internal static int NextFast(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
