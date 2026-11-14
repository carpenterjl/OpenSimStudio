using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// A sequential lenslet array. OpticStudio's Lenslet Array surface is a user-defined surface
/// (OpticStudio 2025 R2 help, "Lenslet Array", which points to "User Defined"): "the ray trace
/// determines which segment of the array is struck, then uses the local lens curvature to
/// determine the refraction", and the sample US_ARRAYEVEN "models an n x m array of lenses,
/// each of dimensions H x W ... rectangular in outline but "even aspheric" in shape; that is,
/// plane, spherical, conic asphere, and radial polynomial asphere. The number of lenses in each
/// direction must be odd." That is what this surface is: nx × ny cells of width W (x) and
/// height H (y), centred on the vertex, each holding the even asphere of the surface's curvature
/// and conic and the coefficients α₁…α₈, with its vertex at the cell centre on the vertex plane.
/// The DLL's source is not on hand, so this is built from that description only.
/// <para>
/// Intersection: the cell under the ray's vertex-plane crossing is tried first (closed-form
/// conic root, then Newton's iteration for the polynomial); if the point found lies in another
/// cell, that cell is tried, up to four times. A ray whose point lies outside the array, or that
/// keeps changing cell, misses the surface (<see cref="RayStatus.MissedSurface"/>). Refraction
/// is the trace's, with the lenslet's normal. The paraxial engine sees the centre lenslet.
/// </para>
/// </summary>
public sealed class LensletArrayShape : SurfaceShape
{
    public LensletArrayShape(int countX, int countY, double width, double height, IReadOnlyList<double>? alpha = null)
    {
        if (countX < 1 || countY < 1 || countX % 2 == 0 || countY % 2 == 0)
            throw new ArgumentException($"Lenslet array: the number of lenslets in each direction must be odd and positive (they are {countX} × {countY}).");
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentException($"Lenslet array: the cell width and height must be positive (they are {width} and {height}).");
        EvenProfile.CheckCoefficients(alpha, "Lenslet array");
        CountX = countX;
        CountY = countY;
        Width = width;
        Height = height;
        Alpha = alpha?.ToArray() ?? Array.Empty<double>();
    }

    public override string TypeName => "Lenslet Array";

    public int CountX { get; }
    public int CountY { get; }
    /// <summary>Cell width along x, meters.</summary>
    public double Width { get; }
    /// <summary>Cell height along y, meters.</summary>
    public double Height { get; }
    public IReadOnlyList<double> Alpha { get; }

    private EvenProfile Profile(SequentialSurface s) => new(s.Curvature, s.Conic, Alpha);

    /// <summary>The cell (i, j) holding (x, y), centred on the vertex, or null outside the array.</summary>
    private (int I, int J)? Cell(double x, double y)
    {
        int i = (int)Math.Round(x / Width, MidpointRounding.AwayFromZero);
        int j = (int)Math.Round(y / Height, MidpointRounding.AwayFromZero);
        if (Math.Abs(x) > CountX * Width / 2 || Math.Abs(y) > CountY * Height / 2) return null;
        return (i, j);
    }

    private (double Z, double Zx, double Zy) Local(EvenProfile profile, double x, double y, (int I, int J) cell)
    {
        double lx = x - cell.I * Width, ly = y - cell.J * Height;
        double rho = lx * lx + ly * ly;
        double slope = profile.SlopeRho(rho);
        return (profile.Sag(rho), 2 * lx * slope, 2 * ly * slope);
    }

    public override double Sag(double x, double y, SequentialSurface surface) =>
        Cell(x, y) is { } cell ? Local(Profile(surface), x, y, cell).Z : double.NaN;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        var profile = Profile(surface);
        var cell = Cell(x, y);
        for (int attempt = 0; attempt < 4 && cell is { } c; attempt++)
        {
            double cx = c.I * Width, cy = c.J * Height;
            double start = 0;
            if (ConicIntersection.Intersect(x - cx, y - cy, direction, surface.Curvature, surface.Conic, out _, out double conic))
                start = conic;
            if (!SagNewton.Intersect(x, y, direction, start, (px, py) => Local(profile, px, py, c), out point, out distance))
                break;
            var found = Cell(point.X, point.Y);
            if (found == c) return true;
            cell = found;
        }
        point = default;
        distance = double.NaN;
        return false;
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface)
    {
        if (Cell(point.X, point.Y) is not { } cell) return new Vector3D(double.NaN, double.NaN, double.NaN);
        var (_, zx, zy) = Local(Profile(surface), point.X, point.Y, cell);
        return SagNewton.Normal(zx, zy);
    }

    /// <summary>Stops a ray where the lenslet's slope is not defined (NaN normal); otherwise
    /// the trace refracts or reflects with <see cref="Normal"/>.</summary>
    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        direction = input.Incoming;
        extraOpticalPath = 0;
        var n = input.Normal;
        bool defined = double.IsFinite(n.X) && double.IsFinite(n.Y) && double.IsFinite(n.Z);
        failure = defined ? RayStatus.Ok : RayStatus.MissedSurface;
        return !defined;
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = Profile(surface).VertexCurvature };

    public override string? OpdLimitation(SequentialSurface surface) =>
        CountX == 1 && CountY == 1 ? null
            : "A lenslet array splits the pupil among several images, so OPD against one reference sphere describes only the rays of the centre lenslet.";
}
