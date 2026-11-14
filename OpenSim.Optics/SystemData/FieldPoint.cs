namespace OpenSim.Optics;

/// <summary>
/// One field point: where in the scene light comes from, in the unit of the field type
/// (degrees or meters), its weight in merit functions and averaged results, and its
/// vignetting factors. The vignetting factors move (decenter) and shrink (compress) the pupil
/// a field's rays are launched into, and tilt the shrunk pupil by an angle; ray aiming (OP-22)
/// applies them. All five are zero for an unvignetted field.
/// </summary>
public sealed record FieldPoint
{
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>Relative weight; zero leaves the field out of weighted results.</summary>
    public double Weight { get; init; } = 1;

    /// <summary>Pupil decenter in x, a fraction of the pupil radius (OpticStudio's VDX).</summary>
    public double DecenterX { get; init; }

    /// <summary>Pupil decenter in y, a fraction of the pupil radius (VDY).</summary>
    public double DecenterY { get; init; }

    /// <summary>Pupil compression in x, a fraction of the pupil radius (VCX); below 1, since a
    /// compression of 1 closes the pupil.</summary>
    public double CompressionX { get; init; }

    /// <summary>Pupil compression in y (VCY); below 1.</summary>
    public double CompressionY { get; init; }

    /// <summary>The angle the vignetted pupil is turned by, in degrees (TAN).</summary>
    public double TiltDegrees { get; init; }

    /// <summary>True when any vignetting factor is non-zero.</summary>
    public bool IsVignetted =>
        DecenterX != 0 || DecenterY != 0 || CompressionX != 0 || CompressionY != 0 || TiltDegrees != 0;

    /// <summary>Adds one line to <paramref name="problems"/> per problem, naming the field by
    /// its 1-based number.</summary>
    public void Validate(int number, FieldType type, ICollection<string> problems)
    {
        string name = $"Field {number}";
        if (!double.IsFinite(X) || !double.IsFinite(Y))
            problems.Add($"{name}: x and y must be numbers.");
        else if (type == FieldType.Angle && (!(Math.Abs(X) < 90) || !(Math.Abs(Y) < 90)))
            problems.Add($"{name}: an angle field must be below 90° from the axis on each axis (it is {X}°, {Y}°).");
        if (!double.IsFinite(Weight) || Weight < 0)
            problems.Add($"{name}: the weight must be zero or positive (it is {Weight}).");
        if (!double.IsFinite(DecenterX) || !double.IsFinite(DecenterY) || !double.IsFinite(TiltDegrees))
            problems.Add($"{name}: the vignetting decenters and angle must be numbers.");
        if (!double.IsFinite(CompressionX) || !(CompressionX < 1) || !double.IsFinite(CompressionY) || !(CompressionY < 1))
            problems.Add($"{name}: the vignetting compressions must be below 1, which would close the pupil (they are {CompressionX}, {CompressionY}).");
    }
}
