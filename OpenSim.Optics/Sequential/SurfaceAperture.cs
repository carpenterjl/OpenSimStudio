using System.Globalization;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// A surface aperture: the region of the surface's local (x, y) plane that passes light
/// (docs/Optics-Conventions.md §13). The test is made at the ray's intersection with the
/// surface, in the surface's local frame, on x and y only (the shape is projected along the
/// local z). A ray outside the open region stops with <see cref="RayStatus.Vignetted"/> at that
/// surface; it is never dropped.
/// <para>
/// Every shape may be decentered (<see cref="DecenterX"/>, <see cref="DecenterY"/>, meters,
/// the shape's centre in the local frame) and turned into an obscuration
/// (<see cref="IsObscuration"/>), which blocks the shape and passes everything else. Lengths
/// are meters.
/// </para>
/// </summary>
public abstract record SurfaceAperture
{
    /// <summary>The shape's centre along the local x, meters.</summary>
    public double DecenterX { get; init; }

    /// <summary>The shape's centre along the local y, meters.</summary>
    public double DecenterY { get; init; }

    /// <summary>True to block the shape and pass the rest (OpticStudio's obscuration types).</summary>
    public bool IsObscuration { get; init; }

    /// <summary>The shape's name for reports ("Circular", "Spider", …).</summary>
    public abstract string Kind { get; }

    /// <summary>True when the point (relative to the shape's centre) is inside the shape.</summary>
    /// <param name="semiDiameter">The surface's clear semi-diameter (meters), read only by a
    /// <see cref="FloatingAperture"/>; NaN when the surface has none.</param>
    protected abstract bool Inside(double x, double y, double semiDiameter);

    /// <summary>True when a ray meeting the surface at local (x, y) passes the aperture.</summary>
    public bool Passes(double x, double y, double semiDiameter) =>
        Inside(x - DecenterX, y - DecenterY, semiDiameter) != IsObscuration;

    /// <summary>"Circular aperture, 0 to 5 mm" style text, lengths in meters.</summary>
    public string Describe() => $"{Kind} {(IsObscuration ? "obscuration" : "aperture")}"
        + (Parameters() is { Length: > 0 } p ? $" ({p})" : "")
        + (DecenterX != 0 || DecenterY != 0 ? $", decentered {G(DecenterX)}, {G(DecenterY)} m" : "");

    /// <summary>The shape's own parameters, for <see cref="Describe"/>.</summary>
    protected abstract string Parameters();

    /// <summary>Adds one line per problem, naming the surface.</summary>
    public void Validate(string where, ICollection<string> problems)
    {
        if (!double.IsFinite(DecenterX) || !double.IsFinite(DecenterY))
            problems.Add($"{where}: the aperture decenters must be numbers (they are {DecenterX}, {DecenterY}).");
        ValidateShape(where, problems);
    }

    protected abstract void ValidateShape(string where, ICollection<string> problems);

    protected static string G(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}

/// <summary>A circle or an annulus: open from <see cref="MinRadius"/> to <see cref="MaxRadius"/>
/// (both included). A minimum radius of zero is a full circle.</summary>
public sealed record CircularAperture(double MinRadius, double MaxRadius) : SurfaceAperture
{
    public override string Kind => "Circular";

    protected override bool Inside(double x, double y, double semiDiameter)
    {
        double r2 = x * x + y * y;
        return r2 <= MaxRadius * MaxRadius && r2 >= MinRadius * MinRadius;
    }

    protected override string Parameters() => $"radius {G(MinRadius)} to {G(MaxRadius)} m";

    protected override void ValidateShape(string where, ICollection<string> problems)
    {
        if (!double.IsFinite(MinRadius) || !double.IsFinite(MaxRadius) || MinRadius < 0 || !(MaxRadius > MinRadius))
            problems.Add($"{where}: a circular aperture needs 0 ≤ minimum radius < maximum radius (they are {MinRadius}, {MaxRadius}).");
    }
}

/// <summary>A rectangle of half-widths <see cref="HalfWidthX"/> and <see cref="HalfWidthY"/>,
/// edges included.</summary>
public sealed record RectangularAperture(double HalfWidthX, double HalfWidthY) : SurfaceAperture
{
    public override string Kind => "Rectangular";

    protected override bool Inside(double x, double y, double semiDiameter) =>
        Math.Abs(x) <= HalfWidthX && Math.Abs(y) <= HalfWidthY;

    protected override string Parameters() => $"half-widths {G(HalfWidthX)}, {G(HalfWidthY)} m";

    protected override void ValidateShape(string where, ICollection<string> problems)
    {
        if (!double.IsFinite(HalfWidthX) || !double.IsFinite(HalfWidthY) || !(HalfWidthX > 0) || !(HalfWidthY > 0))
            problems.Add($"{where}: a rectangular aperture needs positive half-widths (they are {HalfWidthX}, {HalfWidthY}).");
    }
}

/// <summary>An ellipse of semi-axes <see cref="SemiAxisX"/> and <see cref="SemiAxisY"/>, edge
/// included: (x/a)² + (y/b)² ≤ 1.</summary>
public sealed record EllipticalAperture(double SemiAxisX, double SemiAxisY) : SurfaceAperture
{
    public override string Kind => "Elliptical";

    protected override bool Inside(double x, double y, double semiDiameter)
    {
        double u = x / SemiAxisX, v = y / SemiAxisY;
        return u * u + v * v <= 1;
    }

    protected override string Parameters() => $"semi-axes {G(SemiAxisX)}, {G(SemiAxisY)} m";

    protected override void ValidateShape(string where, ICollection<string> problems)
    {
        if (!double.IsFinite(SemiAxisX) || !double.IsFinite(SemiAxisY) || !(SemiAxisX > 0) || !(SemiAxisY > 0))
            problems.Add($"{where}: an elliptical aperture needs positive semi-axes (they are {SemiAxisX}, {SemiAxisY}).");
    }
}

/// <summary>
/// A spider: <see cref="ArmCount"/> straight arms of full width <see cref="ArmWidth"/> running
/// from the centre outwards without end, the first along +x and the others evenly spaced
/// counter-clockwise (toward +y). The arms block light and the rest is open, so a spider is an
/// obscuration as it stands; <see cref="SurfaceAperture.IsObscuration"/> inverts that (light
/// then passes only along the arms). The arm layout is this program's choice; it has not been
/// compared with OpticStudio's (docs/Optics-Conventions.md §10, item 9).
/// </summary>
public sealed record SpiderAperture(double ArmWidth, int ArmCount) : SurfaceAperture
{
    public override string Kind => "Spider";

    /// <summary>Inside means on the open area, off every arm.</summary>
    protected override bool Inside(double x, double y, double semiDiameter)
    {
        double half = ArmWidth / 2;
        for (int k = 0; k < ArmCount; k++)
        {
            var (s, c) = OpticsConventions.SinCosDegrees(360.0 * k / ArmCount);
            double along = x * c + y * s;
            double across = Math.Abs(y * c - x * s);
            if (along >= 0 && across <= half) return false;
        }
        return true;
    }

    protected override string Parameters() => $"{ArmCount} arms {G(ArmWidth)} m wide";

    protected override void ValidateShape(string where, ICollection<string> problems)
    {
        if (!double.IsFinite(ArmWidth) || !(ArmWidth > 0) || ArmCount < 1)
            problems.Add($"{where}: a spider needs a positive arm width and at least one arm (they are {ArmWidth}, {ArmCount}).");
    }
}

/// <summary>
/// A user-defined aperture (OpticStudio's UDA): a closed polygon through
/// <see cref="Vertices"/> (local x, y in meters; the last vertex joins the first). A point is
/// inside by the even-odd rule, so a self-crossing outline alternates open and closed regions.
/// A point exactly on an edge may fall either way. Curved UDA segments and multi-outline UDA
/// files are not built.
/// </summary>
public sealed record PolygonAperture(IReadOnlyList<(double X, double Y)> Vertices) : SurfaceAperture
{
    public override string Kind => "User-defined";

    protected override bool Inside(double x, double y, double semiDiameter)
    {
        bool inside = false;
        int n = Vertices.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = Vertices[i];
            var (xj, yj) = Vertices[j];
            if ((yi > y) != (yj > y) && x < xj + (y - yj) * (xi - xj) / (yi - yj))
                inside = !inside;
        }
        return inside;
    }

    protected override string Parameters() => $"{Vertices.Count} vertices";

    protected override void ValidateShape(string where, ICollection<string> problems)
    {
        if (Vertices is null || Vertices.Count < 3)
        {
            problems.Add($"{where}: a user-defined aperture needs at least three vertices.");
            return;
        }
        if (Vertices.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y)))
        {
            problems.Add($"{where}: every vertex of the user-defined aperture must be a number.");
            return;
        }
        double area2 = 0;
        for (int i = 0, j = Vertices.Count - 1; i < Vertices.Count; j = i++)
            area2 += Vertices[j].X * Vertices[i].Y - Vertices[i].X * Vertices[j].Y;
        if (area2 == 0)
            problems.Add($"{where}: the user-defined aperture encloses no area.");
    }
}

/// <summary>
/// A floating aperture: a circle of the surface's clear semi-diameter, fixed or automatic
/// (<see cref="SemiDiameterSizing"/>), centred on the vertex unless decentered.
/// </summary>
public sealed record FloatingAperture : SurfaceAperture
{
    public override string Kind => "Floating";

    protected override bool Inside(double x, double y, double semiDiameter)
    {
        if (double.IsNaN(semiDiameter))
            throw new InvalidOperationException(
                "A floating aperture needs the surface's semi-diameter; give the surface a fixed semi-diameter or trace with automatic semi-diameters (SemiDiameterSizing).");
        return x * x + y * y <= semiDiameter * semiDiameter;
    }

    protected override string Parameters() => "the clear semi-diameter";

    protected override void ValidateShape(string where, ICollection<string> problems) { }
}
