namespace OpenSim.Optics;

/// <summary>
/// The aperture group of the system data: how the beam is sized, its apodization, the margins
/// added to automatic clear semi-diameters, and the telecentric and afocal modes. Lengths are
/// meters, angles degrees (docs/Optics-Conventions.md §1). The record holds what the user
/// entered; <see cref="Validate"/> says what is wrong with it, so an editor can keep a value
/// being typed without the record refusing it.
/// </summary>
public sealed record Aperture
{
    public ApertureType Type { get; init; } = ApertureType.EntrancePupilDiameter;

    /// <summary>
    /// The value the type takes: the entrance-pupil diameter in meters; an F-number; a
    /// numerical aperture; the object cone half-angle in degrees. Ignored for
    /// <see cref="ApertureType.FloatByStopSize"/>.
    /// </summary>
    public double Value { get; init; } = 0.01;

    public ApodizationType Apodization { get; init; } = ApodizationType.Uniform;

    /// <summary>The apodization factor; used by <see cref="ApodizationType.Gaussian"/> only.</summary>
    public double ApodizationFactor { get; init; }

    /// <summary>A length added to every clear semi-diameter the program sizes automatically,
    /// meters.</summary>
    public double SemiDiameterMargin { get; init; }

    /// <summary>A percentage added to every automatically sized clear semi-diameter, after
    /// <see cref="SemiDiameterMargin"/>.</summary>
    public double SemiDiameterMarginPercent { get; init; }

    /// <summary>The entrance pupil is at infinity: chief rays in object space are parallel to
    /// the axis.</summary>
    public bool TelecentricObjectSpace { get; init; }

    /// <summary>The image is at infinity: image-space results are angles, not positions.</summary>
    public bool AfocalImageSpace { get; init; }

    /// <summary>Adds one actionable line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        string label = ApertureTypes.Names.Label(Type);
        if (!ApertureTypes.HasNoValue(Type))
        {
            if (!double.IsFinite(Value) || !(Value > 0))
                problems.Add($"Aperture: the {label} must be a positive number (it is {Value}).");
            else if (Type == ApertureType.ObjectConeAngle && !(Value < 90))
                problems.Add($"Aperture: the object cone angle is a half-angle and must be below 90° (it is {Value}°).");
        }
        if (!double.IsFinite(ApodizationFactor) || ApodizationFactor < 0)
            problems.Add($"Aperture: the apodization factor must be zero or positive (it is {ApodizationFactor}).");
        if (!double.IsFinite(SemiDiameterMargin) || SemiDiameterMargin < 0)
            problems.Add($"Aperture: the semi-diameter margin must be zero or positive (it is {SemiDiameterMargin}).");
        if (!double.IsFinite(SemiDiameterMarginPercent) || SemiDiameterMarginPercent < 0)
            problems.Add($"Aperture: the semi-diameter margin percentage must be zero or positive (it is {SemiDiameterMarginPercent}).");

        // Combinations with no meaning, by the definitions on ApertureType.
        if (TelecentricObjectSpace && Type == ApertureType.EntrancePupilDiameter)
            problems.Add("Aperture: with telecentric object space the entrance pupil is at infinity, so it has no diameter; state the aperture as an object space NA, an object cone angle, a working F/# or by the stop size.");
        if (TelecentricObjectSpace && Type == ApertureType.ImageSpaceFNumber)
            problems.Add("Aperture: the image space F/# is defined with the entrance-pupil diameter, which telecentric object space puts at infinity; use another aperture type.");
        if (AfocalImageSpace && Type is ApertureType.ImageSpaceFNumber or ApertureType.ParaxialWorkingFNumber)
            problems.Add($"Aperture: an afocal system has no focal length or image-space cone, so the {label} is undefined; use the entrance pupil diameter or an object-space type.");
    }
}
