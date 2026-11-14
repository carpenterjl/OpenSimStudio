namespace OpenSim.Optics;

/// <summary>
/// The field group of the system data: the field type, how fields are normalised to (Hx, Hy)
/// (docs/Optics-Conventions.md §7) and the field points, in order (field 1 first).
/// </summary>
public sealed record FieldSet
{
    public FieldType Type { get; init; } = FieldType.Angle;

    public FieldNormalization Normalization { get; init; } = FieldNormalization.Radial;

    /// <summary>The field points; the default is one on-axis field.</summary>
    public IReadOnlyList<FieldPoint> Points { get; init; } = new[] { new FieldPoint() };

    /// <summary>The scale that turns these fields into normalised coordinates
    /// (<see cref="OpticsConventions.FieldScale"/>).</summary>
    public (double X, double Y) Scale => OpticsConventions.FieldScale(Points.Select(p => (p.X, p.Y)), Normalization);

    /// <summary>Normalised field (Hx, Hy) of field <paramref name="index"/> (0-based).</summary>
    public (double Hx, double Hy) Normalized(int index) =>
        OpticsConventions.NormalizeField((Points[index].X, Points[index].Y), Scale);

    /// <summary>Adds one line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        if (Points.Count == 0)
        {
            problems.Add("Fields: a system needs at least one field point.");
            return;
        }
        for (int i = 0; i < Points.Count; i++) Points[i].Validate(i + 1, Type, problems);
        if (Points.All(p => !(p.Weight > 0)))
            problems.Add("Fields: at least one field needs a weight above zero, or weighted results have nothing to average.");
    }

    /// <summary>
    /// The field wizard's evenly spread fields along +y: <paramref name="count"/> points from
    /// the axis to <paramref name="maxY"/>, each of weight 1. Evenly spaced puts them at
    /// maxY·i/(n−1); equal area at maxY·√(i/(n−1)), so each ring between neighbouring fields
    /// covers the same area of a circular field. One point is the axis.
    /// </summary>
    public static IReadOnlyList<FieldPoint> AlongY(double maxY, int count, bool equalArea)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "The wizard needs at least one field.");
        if (!double.IsFinite(maxY)) throw new ArgumentOutOfRangeException(nameof(maxY), maxY, "The largest field must be a number.");
        var points = new FieldPoint[count];
        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0 : (double)i / (count - 1);
            points[i] = new FieldPoint { Y = maxY * (equalArea ? Math.Sqrt(t) : t) };
        }
        return points;
    }
}
