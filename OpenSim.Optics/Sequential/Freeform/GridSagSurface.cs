using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>How a Grid Sag surface interpolates between its points (parameter 0).</summary>
public enum GridSagInterpolation
{
    /// <summary>0: "bicubic spline" (the default).</summary>
    Bicubic = 0,

    /// <summary>1: linear.</summary>
    Linear = 1,
}

/// <summary>
/// OpticStudio's Grid Sag surface (OP-25, docs/Optics-Conventions.md §55): the Even Asphere's
/// sag with Zernike Standard terms (Z_base) plus a sag interpolated from a rectangular grid,
/// Z = Z_base + Z_grid(x − x_dec, y − y_dec) (OpticStudio 2025 R2 help, "Grid Sag", its
/// equations and parameter table: 0 the interpolation method, 1–8 α1–α8, 9 and 10 the Zernike
/// decenter, 13 the number of Zernike terms (up to 231), 14 the normalization radius, 15–245 the
/// coefficients). The grid comes from its data file (<see cref="GridSagData"/>).
/// <para>
/// <b>Interpolation.</b> Bicubic: in each cell the tensor product of cubic Hermite polynomials
/// that takes the four corners' z, ∂z/∂x, ∂z/∂y and ∂²z/∂x∂y ("The derivative values are
/// required for smooth bicubic interpolation … smooth in both sag and the first derivative",
/// "exact results at the grid points"); its error on a smooth sag is O(h⁴), 1/16 for each halving
/// of the spacing. "If all of the derivative values … are zero for every point in the file, then
/// OpticStudio will automatically estimate the derivatives using a finite difference method":
/// here second-order central differences inside and second-order one-sided differences on the
/// edges, the cross derivative as the x difference of the y differences (that method is this
/// program's; OpticStudio does not state its own). Linear: bilinear in each cell, derivatives
/// not used. A point outside the grid has no sag: "Rays traced outside the grid area are
/// treated as a ray miss error".
/// </para>
/// <para>
/// <b>Readings.</b> The grid's points are x_i = x_dec + (i − (nx − 1)/2)·delx, i = 0 … nx − 1, and
/// the same in y (the grid centred on its decenter, as Z(x_i − xdec, …) reads). The Grid Sag has
/// no extrapolate flag, so its Zernike terms are used wherever the ray lands. A nodata point is
/// zero in all four values, as the help says the line is replaced with zeros.
/// </para>
/// </summary>
public sealed class GridSagSurface : SagSurfaceShape
{
    private readonly double[] _alpha;
    private readonly FreeformZernikeTerms _zernike;
    // SI, indexed [j·nx + i] with j = 0 the −y row.
    private readonly double[] _z, _zx, _zy, _zxy;
    private readonly int _nx, _ny;
    private readonly double _hx, _hy, _x0, _y0;

    /// <param name="data">The grid as its file gives it.</param>
    /// <param name="interpolation">Parameter 0.</param>
    /// <param name="alpha">α1 … α8, m^(1 − 2i).</param>
    /// <param name="zernike">Zernike Standard coefficients, meters (up to 231).</param>
    /// <param name="normalizationRadius">Meters.</param>
    /// <param name="zernikeDecenterX">Parameter 9, meters.</param>
    /// <param name="zernikeDecenterY">Parameter 10, meters.</param>
    public GridSagSurface(GridSagData data, GridSagInterpolation interpolation = GridSagInterpolation.Bicubic,
        IReadOnlyList<double>? alpha = null, IReadOnlyList<double>? zernike = null, double normalizationRadius = 0,
        double zernikeDecenterX = 0, double zernikeDecenterY = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Enum.IsDefined(interpolation))
            throw new ArgumentException($"Grid Sag: the interpolation method {(int)interpolation} is not 0 (bicubic) or 1 (linear).");
        Data = data;
        Interpolation = interpolation;
        _alpha = CheckedCopy(alpha ?? Array.Empty<double>(), "Grid Sag", "coefficient α", 8);
        _zernike = new FreeformZernikeTerms("Grid Sag", ZernikeOrdering.Standard, zernike ?? Array.Empty<double>(),
            normalizationRadius, extrapolate: true, maximum: 231, zernikeDecenterX, zernikeDecenterY);

        double u = OpticsConventions.MetersPer(data.Unit);
        _nx = data.Nx;
        _ny = data.Ny;
        _hx = data.DeltaX * u;
        _hy = data.DeltaY * u;
        _x0 = data.DecenterX * u - 0.5 * (_nx - 1) * _hx;
        _y0 = data.DecenterY * u - 0.5 * (_ny - 1) * _hy;
        int n = _nx * _ny;
        _z = new double[n];
        _zx = new double[n];
        _zy = new double[n];
        _zxy = new double[n];
        bool anyDerivative = false;
        for (int row = 0; row < _ny; row++)
        {
            int j = _ny - 1 - row;                 // file row 0 is the +y edge
            for (int i = 0; i < _nx; i++)
            {
                int f = row * _nx + i, g = j * _nx + i;
                _z[g] = data.Sag[f] * u;
                _zx[g] = data.SlopeX[f];
                _zy[g] = data.SlopeY[f];
                _zxy[g] = data.Cross[f] / u;
                anyDerivative |= _zx[g] != 0 || _zy[g] != 0 || _zxy[g] != 0;
            }
        }
        DerivativesEstimated = !anyDerivative;
        if (DerivativesEstimated) EstimateDerivatives();
    }

    /// <summary>The grid as read (for saving).</summary>
    public GridSagData Data { get; }

    public GridSagInterpolation Interpolation { get; }

    /// <summary>True when every derivative in the file was zero and they were estimated.</summary>
    public bool DerivativesEstimated { get; }

    public IReadOnlyList<double> Alpha => _alpha;
    public IReadOnlyList<double> Zernike => _zernike.Coefficients;
    public double NormalizationRadius => _zernike.NormalizationRadius;
    public double ZernikeDecenterX => _zernike.DecenterX;
    public double ZernikeDecenterY => _zernike.DecenterY;

    public override string TypeName => "Grid Sag";

    /// <summary>The same grid with other parameters (a solve or an edit of parameter 0–245).</summary>
    public GridSagSurface WithParameters(GridSagInterpolation interpolation, IReadOnlyList<double> alpha,
        IReadOnlyList<double> zernike, double normalizationRadius, double decenterX, double decenterY) =>
        new(Data, interpolation, alpha, zernike, normalizationRadius, decenterX, decenterY);

    private void EstimateDerivatives()
    {
        // Second-order differences: (f[i+1] − f[i−1])/2h inside, (−3f0 + 4f1 − f2)/2h at the
        // first point and (3fn − 4fn−1 + fn−2)/2h at the last (derived from the Taylor series).
        static void Diff(Func<int, double> f, int n, double h, Action<int, double> set)
        {
            for (int i = 0; i < n; i++)
            {
                double d = i == 0 ? (-3 * f(0) + 4 * f(1) - f(2)) / (2 * h)
                    : i == n - 1 ? (3 * f(n - 1) - 4 * f(n - 2) + f(n - 3)) / (2 * h)
                    : (f(i + 1) - f(i - 1)) / (2 * h);
                set(i, d);
            }
        }
        for (int j = 0; j < _ny; j++)
        {
            int jj = j;
            Diff(i => _z[jj * _nx + i], _nx, _hx, (i, d) => _zx[jj * _nx + i] = d);
        }
        for (int i = 0; i < _nx; i++)
        {
            int ii = i;
            Diff(j => _z[j * _nx + ii], _ny, _hy, (j, d) => _zy[j * _nx + ii] = d);
        }
        for (int j = 0; j < _ny; j++)
        {
            int jj = j;
            Diff(i => _zy[jj * _nx + i], _nx, _hx, (i, d) => _zxy[jj * _nx + i] = d);
        }
    }

    /// <summary>The cell (i, j) holding (x, y) and the local coordinates t, s in [0, 1]; false
    /// outside the grid.</summary>
    private bool Locate(double x, double y, out int i, out int j, out double t, out double s)
    {
        double gx = (x - _x0) / _hx, gy = (y - _y0) / _hy;
        i = j = 0;
        t = s = 0;
        if (!(gx >= 0 && gx <= _nx - 1 && gy >= 0 && gy <= _ny - 1)) return false;
        i = Math.Min((int)Math.Floor(gx), _nx - 2);
        j = Math.Min((int)Math.Floor(gy), _ny - 2);
        t = gx - i;
        s = gy - j;
        return true;
    }

    /// <summary>The interpolated grid sag and its first and second derivatives in cell (i, j)
    /// at local (t, s), SI.</summary>
    internal void Patch(int i, int j, double t, double s, out double z, out double zx, out double zy,
        out double zxx, out double zyy, out double zxy)
    {
        int a = j * _nx + i, b = a + 1, c = a + _nx, d = c + 1;   // (i,j), (i+1,j), (i,j+1), (i+1,j+1)
        if (Interpolation == GridSagInterpolation.Linear)
        {
            double z00 = _z[a], z10 = _z[b], z01 = _z[c], z11 = _z[d];
            z = z00 * (1 - t) * (1 - s) + z10 * t * (1 - s) + z01 * (1 - t) * s + z11 * t * s;
            zx = ((z10 - z00) * (1 - s) + (z11 - z01) * s) / _hx;
            zy = ((z01 - z00) * (1 - t) + (z11 - z10) * t) / _hy;
            zxx = zyy = 0;
            zxy = (z11 - z10 - z01 + z00) / (_hx * _hy);
            return;
        }
        // Cubic Hermite basis on [0, 1] (value at 0, value at 1, slope at 0, slope at 1) and
        // its first and second derivatives.
        Span<double> ht = stackalloc double[4], dht = stackalloc double[4], d2ht = stackalloc double[4];
        Span<double> hs = stackalloc double[4], dhs = stackalloc double[4], d2hs = stackalloc double[4];
        Hermite(t, _hx, ht, dht, d2ht);
        Hermite(s, _hy, hs, dhs, d2hs);
        // Corner data: per corner (p in x: 0 at i, 1 at i+1; q in y), the values multiply
        // H_p(t)·H_q(s), the x slopes G_p(t)·H_q(s), the y slopes H_p(t)·G_q(s), the cross
        // derivatives G_p(t)·G_q(s), where index 0,1 of the spans are H0, H1 and 2,3 are G0, G1
        // (G already scaled by the spacing).
        z = zx = zy = zxx = zyy = zxy = 0;
        for (int q = 0; q < 2; q++)
            for (int p = 0; p < 2; p++)
            {
                int k = (q == 0 ? a : c) + p;
                double f = _z[k], fx = _zx[k], fy = _zy[k], fxy = _zxy[k];
                // Coefficient functions in x (index p for H, 2 + p for G) and in y.
                double Hx = ht[p], Gx = ht[2 + p], Hy = hs[q], Gy = hs[2 + q];
                double dHx = dht[p], dGx = dht[2 + p], dHy = dhs[q], dGy = dhs[2 + q];
                double d2Hx = d2ht[p], d2Gx = d2ht[2 + p], d2Hy = d2hs[q], d2Gy = d2hs[2 + q];
                z += f * Hx * Hy + fx * Gx * Hy + fy * Hx * Gy + fxy * Gx * Gy;
                zx += f * dHx * Hy + fx * dGx * Hy + fy * dHx * Gy + fxy * dGx * Gy;
                zy += f * Hx * dHy + fx * Gx * dHy + fy * Hx * dGy + fxy * Gx * dGy;
                zxx += f * d2Hx * Hy + fx * d2Gx * Hy + fy * d2Hx * Gy + fxy * d2Gx * Gy;
                zyy += f * Hx * d2Hy + fx * Gx * d2Hy + fy * Hx * d2Gy + fxy * Gx * d2Gy;
                zxy += f * dHx * dHy + fx * dGx * dHy + fy * dHx * dGy + fxy * dGx * dGy;
            }
    }

    /// <summary>
    /// The cubic Hermite basis at local u ∈ [0, 1] on a cell of width h, as functions of the
    /// physical coordinate: b[0] = H0 = 2u³ − 3u² + 1, b[1] = H1 = −2u³ + 3u², b[2] = h·G0 =
    /// h(u³ − 2u² + u), b[3] = h·G1 = h(u³ − u²), with their first and second derivatives with
    /// respect to x = x_i + u·h (so d/dx = (1/h)·d/du).
    /// </summary>
    private static void Hermite(double u, double h, Span<double> b, Span<double> d, Span<double> d2)
    {
        double u2 = u * u, u3 = u2 * u;
        b[0] = 2 * u3 - 3 * u2 + 1;
        b[1] = -2 * u3 + 3 * u2;
        b[2] = h * (u3 - 2 * u2 + u);
        b[3] = h * (u3 - u2);
        d[0] = (6 * u2 - 6 * u) / h;
        d[1] = (-6 * u2 + 6 * u) / h;
        d[2] = 3 * u2 - 4 * u + 1;
        d[3] = 3 * u2 - 2 * u;
        d2[0] = (12 * u - 6) / (h * h);
        d2[1] = (-12 * u + 6) / (h * h);
        d2[2] = (6 * u - 4) / h;
        d2[3] = (6 * u - 2) / h;
    }

    /// <summary>The grid's own sag and slopes at (x, y) (no base), SI; false outside the grid.</summary>
    public bool TryGridSag(double x, double y, out double z, out double dzdx, out double dzdy)
    {
        if (!Locate(x, y, out int i, out int j, out double t, out double s))
        {
            z = dzdx = dzdy = double.NaN;
            return false;
        }
        Patch(i, j, t, s, out z, out dzdx, out dzdy, out _, out _, out _);
        return true;
    }

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        dzdx = dzdy = 0;
        if (!TryGridSag(x, y, out double gz, out double gx, out double gy))
        {
            z = double.NaN;
            return false;
        }
        double r = Math.Sqrt(x * x + y * y);
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr)) return false;
        PowerSeries.EvenInR(_alpha, r, out double p, out double dp);
        z += p + gz;
        dzdr += dp;
        if (r > 0)
        {
            dzdx = dzdr * x / r;
            dzdy = dzdr * y / r;
        }
        dzdx += gx;
        dzdy += gy;
        if (!_zernike.Add(x, y, ref z, ref dzdx, ref dzdy) || !double.IsFinite(z) || !double.IsFinite(dzdx) || !double.IsFinite(dzdy))
        {
            z = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>
    /// "OpticStudio computes the effective paraxial curvature using the vertex radius of
    /// curvature, asphere coefficient data, and grid data points": here c + 2α1, the Zernike
    /// terms' expansion and the interpolated grid's value, slopes and second derivatives at the
    /// vertex, by §23's rule (a grid whose sag or slope at the vertex is not zero has no
    /// paraxial model). The bicubic interpolant is only C¹, so where the vertex lies on a cell
    /// edge the second derivatives are the mean over the cells that meet there (this program's
    /// reading). A vertex outside the grid has no sag and is refused.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        double gx = (0 - _x0) / _hx, gy = (0 - _y0) / _hy;
        if (!(gx >= 0 && gx <= _nx - 1 && gy >= 0 && gy <= _ny - 1))
            throw new ArgumentException("Grid Sag: the vertex (0, 0) lies outside the grid, where the surface has no sag; the paraxial engine has no model of it.");
        static int[] Cells(double g, int n)
        {
            int f = (int)Math.Floor(g);
            if (f == g) return new[] { f - 1, f }.Where(c => c >= 0 && c <= n - 2).ToArray();
            return new[] { Math.Min(f, n - 2) };
        }
        double v = 0, sx = 0, sy = 0, hxx = 0, hyy = 0, hxy = 0;
        int count = 0;
        foreach (int i in Cells(gx, _nx))
            foreach (int j in Cells(gy, _ny))
            {
                Patch(i, j, gx - i, gy - j, out double z, out double zx, out double zy, out double zxx, out double zyy, out double zxy);
                if (count == 0) { v = z; sx = zx; sy = zy; }
                hxx += zxx;
                hyy += zyy;
                hxy += zxy;
                count++;
            }
        double a1 = 2 * ParaxialCurvature.Term(_alpha, 0);
        var t = new VertexTaylor { Value = v, Gx = sx, Gy = sy, Hxx = a1 + hxx / count, Hyy = a1 + hyy / count, Hxy = hxy / count }
            + _zernike.AtVertex();
        return AnamorphicParaxial.YCurvature(TypeName, surface.Curvature, t, "from its grid and Zernike terms");
    }
}
