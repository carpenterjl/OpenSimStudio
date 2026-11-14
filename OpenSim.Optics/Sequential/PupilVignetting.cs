namespace OpenSim.Optics.Sequential;

/// <summary>The five vignetting factors of one field (decenters and compressions as fractions of
/// the pupil radius, the angle in degrees); all zero leaves the pupil as it is.</summary>
public readonly record struct VignettingFactors(double DecenterX, double DecenterY, double CompressionX, double CompressionY,
    double TiltDegrees)
{
    public static VignettingFactors Of(FieldPoint field) =>
        new(field.DecenterX, field.DecenterY, field.CompressionX, field.CompressionY, field.TiltDegrees);

    public bool IsZero => DecenterX == 0 && DecenterY == 0 && CompressionX == 0 && CompressionY == 0 && TiltDegrees == 0;

    /// <summary>The fraction of the pupil's area these factors keep, (1 − VCX)(1 − VCY).</summary>
    public double AreaFraction => (1 - CompressionX) * (1 - CompressionY);
}

/// <summary>
/// The pupil remapping of vignetting factors, OpticStudio's two successive transformations
/// (Ansys OpticStudio 2025 R2 help, Conventions and Definitions, "Vignetting Factors",
/// ansyshelp.ansys.com, OpticStudio_Help/topics/Vignetting_Factors.html, its four equations):
/// first scaled and shifted, P′x = VDX + Px(1 − VCX), P′y = VDY + Py(1 − VCY); then turned by
/// the tangential angle θ, P″x = P′x cos θ − P′y sin θ, P″y = P′x sin θ + P′y cos θ. With ray
/// aiming off the remapped point is on the paraxial entrance pupil; with it on, on the stop
/// (same page: "If ray aiming is turned on, then the remapping is done at the stop surface").
/// <para>
/// Between defined fields the same page says OpticStudio interpolates between adjacent field
/// points when the system is rotationally symmetric or every field lies on the y axis, and uses
/// the closest defined field otherwise. Here: a field point that equals a defined field (in
/// normalised coordinates) takes its factors; when every defined field has x = 0 and the point
/// has Hx = 0, the factors are interpolated linearly in Hy between the two neighbouring fields
/// (held at the end values beyond them); otherwise the closest defined field's are used (the
/// first of equals). Reading "rotationally symmetric" as "fields on the y axis" is this
/// program's choice (docs/Optics-Conventions.md §16).
/// </para>
/// </summary>
public sealed class PupilVignetting
{
    private readonly (double Hx, double Hy, VignettingFactors F)[] _fields;
    private readonly (double Hy, VignettingFactors F)[] _alongY;

    /// <summary>True when any field has a non-zero factor.</summary>
    public bool Any { get; }

    public PupilVignetting(FieldSet fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = new (double, double, VignettingFactors)[fields.Points.Count];
        for (int i = 0; i < _fields.Length; i++)
        {
            var (hx, hy) = fields.Normalized(i);
            _fields[i] = (hx, hy, VignettingFactors.Of(fields.Points[i]));
        }
        Any = _fields.Any(f => !f.F.IsZero);
        _alongY = fields.Points.All(p => p.X == 0)
            ? _fields.Select(f => (f.Hy, f.F)).OrderBy(f => f.Hy).ToArray()
            : Array.Empty<(double, VignettingFactors)>();
    }

    /// <summary>The factors at normalised field (hx, hy).</summary>
    public VignettingFactors At(double hx, double hy)
    {
        if (!Any) return default;
        foreach (var f in _fields)
            if (f.Hx == hx && f.Hy == hy) return f.F;
        if (_alongY.Length > 0 && hx == 0)
        {
            if (hy <= _alongY[0].Hy) return _alongY[0].F;
            if (hy >= _alongY[^1].Hy) return _alongY[^1].F;
            for (int i = 1; i < _alongY.Length; i++)
            {
                var (h1, b) = _alongY[i];
                if (hy > h1) continue;
                var (h0, a) = _alongY[i - 1];
                double t = (hy - h0) / (h1 - h0);
                return new VignettingFactors(
                    a.DecenterX + t * (b.DecenterX - a.DecenterX), a.DecenterY + t * (b.DecenterY - a.DecenterY),
                    a.CompressionX + t * (b.CompressionX - a.CompressionX), a.CompressionY + t * (b.CompressionY - a.CompressionY),
                    a.TiltDegrees + t * (b.TiltDegrees - a.TiltDegrees));
            }
        }
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < _fields.Length; i++)
        {
            double dx = _fields[i].Hx - hx, dy = _fields[i].Hy - hy, d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return _fields[best].F;
    }

    /// <summary>The remapped pupil point (P″x, P″y) of (px, py) under <paramref name="f"/>.
    /// All-zero factors return the point unchanged, bit for bit.</summary>
    public static (double Px, double Py) Apply(VignettingFactors f, double px, double py)
    {
        if (f.IsZero) return (px, py);
        double x = f.DecenterX + px * (1 - f.CompressionX);
        double y = f.DecenterY + py * (1 - f.CompressionY);
        if (f.TiltDegrees == 0) return (x, y);
        var (s, c) = OpticsConventions.SinCosDegrees(f.TiltDegrees);
        return (x * c - y * s, x * s + y * c);
    }
}
