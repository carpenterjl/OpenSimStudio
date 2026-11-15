using System.ComponentModel;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-39: the thirteen MTF analyses of OpticStudio's Image Quality group (help page "MTF"), over
// the three engines of MtfEngine, each reading the project's saved lens (ProjectLens) unless a
// lens source is given. Settings are those the help pages list (saved under
// plans/sources/optics/op-39-help/); defaults the help does not give are this program's.
// docs/Optics-Conventions.md §38.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="FftMtfAnalysis"/> (help, "FFT MTF").</summary>
public sealed record FftMtfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Max frequency"), Description("The largest frequency plotted, in MTF units (cycles/mm; afocal, cycles/mrad); 0 for the largest diffraction cutoff.")]
    public double MaxFrequency { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Type"), Description("Modulation, real part, imaginary part, phase (degrees) or square-wave response.")]
    public MtfDataType Type { get; init; } = MtfDataType.Modulation;

    [DisplayName("Show diffraction limit"), Description("Draw the MTF of the reference field with the OPD set to zero.")]
    public bool ShowDiffractionLimit { get; init; } = true;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light; off traces through every aperture.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FftThroughFocusMtfAnalysis"/> (help, "FFT Through Focus MTF").</summary>
public sealed record FftThroughFocusMtfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Delta focus"), Description("The ± range of the focus shift, lens units, added to the thickness before the image surface.")]
    public double DeltaFocus { get; init; } = 0.1;

    [DisplayName("Frequency"), Description("The spatial frequency plotted, in MTF units (cycles/mm).")]
    public double Frequency { get; init; } = 30;

    [DisplayName("# Steps"), Description("Focus planes from −delta to +delta; 2 to 100.")]
    public int Steps { get; init; } = 11;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Type"), Description("Modulation, real part, imaginary part, phase (degrees) or square-wave response.")]
    public MtfDataType Type { get; init; } = MtfDataType.Modulation;

    [DisplayName("Show diffraction limit"), Description("Draw the reference field's diffraction-limited value at this frequency.")]
    public bool ShowDiffractionLimit { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FftSurfaceMtfAnalysis"/> (help, "FFT Surface MTF").</summary>
public sealed record FftSurfaceMtfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 512.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Type"), Description("Modulation, real part or imaginary part of the OTF.")]
    public MtfSurfaceType Type { get; init; } = MtfSurfaceType.Modulation;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FftMtfVsFieldAnalysis"/> (help, "FFT MTF vs. Field").</summary>
public sealed record FftMtfVsFieldSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 1024.")]
    public int Sampling { get; init; } = 32;

    [DisplayName("Frequency 1"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency1 { get; init; } = 10;
    [DisplayName("Frequency 2"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency2 { get; init; } = 20;
    [DisplayName("Frequency 3"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency3 { get; init; } = 30;
    [DisplayName("Frequency 4"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency4 { get; init; }
    [DisplayName("Frequency 5"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency5 { get; init; }
    [DisplayName("Frequency 6"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency6 { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors with surface apertures first (the help's default).")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Field density"), Description("Points from the axis to the largest defined field in the scan direction; 2 to 100.")]
    public int FieldDensity { get; init; } = 10;

    [DisplayName("Scan type"), Description("The field direction scanned: +y, +x, −y or −x.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }

    internal double[] Frequencies => new[] { Frequency1, Frequency2, Frequency3, Frequency4, Frequency5, Frequency6 };
}

/// <summary>Settings of the <see cref="FftMtfMapAnalysis"/> (help, "FFT MTF Map").</summary>
public sealed record FftMtfMapSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 1024.")]
    public int Sampling { get; init; } = 32;

    [DisplayName("X field width"), Description("The total width of the map in x, field units (degrees or lens units); 0 for twice the largest radial field.")]
    public double XFieldWidth { get; init; }

    [DisplayName("Y field width"), Description("The total height of the map in y, field units; 0 for twice the largest radial field.")]
    public double YFieldWidth { get; init; }

    [DisplayName("Frequency"), Description("The spatial frequency mapped, in MTF units.")]
    public double Frequency { get; init; } = 30;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("X pixels"), Description("Pixels across the map in x, each computed at its centre; 2 to 101.")]
    public int XPixels { get; init; } = 11;

    [DisplayName("Y pixels"), Description("Pixels across the map in y; 2 to 101.")]
    public int YPixels { get; init; } = 11;

    [DisplayName("MTF data"), Description("Tangential, sagittal, their average, minimum or maximum.")]
    public MtfMapData Data { get; init; } = MtfMapData.Tangential;

    [DisplayName("Reference field"), Description("The field row at the centre of the map; 0 for the field (0, 0).")]
    public int ReferenceField { get; init; }

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors with surface apertures first (the help's default).")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensMtfAnalysis"/> (help, "Huygens MTF").</summary>
public sealed record HuygensMtfSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: an m × m grid of image points; 8 to 512.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Max frequency"), Description("The largest frequency plotted, cycles/mm; 0 for the largest diffraction cutoff.")]
    public double MaxFrequency { get; init; }

    [DisplayName("Show diffraction limit"), Description("Draw the reference field's FFT MTF with the OPD set to zero.")]
    public bool ShowDiffractionLimit { get; init; } = true;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensThroughFocusMtfAnalysis"/> (help, "Huygens Through Focus MTF").</summary>
public sealed record HuygensThroughFocusMtfSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: an m × m grid of image points; 8 to 512.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Delta focus"), Description("The ± range of the focus shift, lens units.")]
    public double DeltaFocus { get; init; } = 0.1;

    [DisplayName("Frequency"), Description("The spatial frequency plotted, cycles/mm.")]
    public double Frequency { get; init; } = 30;

    [DisplayName("# Steps"), Description("Focus planes from −delta to +delta; 2 to 100.")]
    public int Steps { get; init; } = 11;

    [DisplayName("Show diffraction limit"), Description("Draw the reference field's diffraction-limited value at this frequency.")]
    public bool ShowDiffractionLimit { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensSurfaceMtfAnalysis"/> (help, "Huygens Surface MTF").</summary>
public sealed record HuygensSurfaceMtfSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: an m × m grid of image points; 8 to 256.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Type"), Description("Modulation, real part or imaginary part of the OTF.")]
    public MtfSurfaceType Type { get; init; } = MtfSurfaceType.Modulation;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensMtfVsFieldAnalysis"/> (help, "Huygens MTF vs. Field").</summary>
public sealed record HuygensMtfVsFieldSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: an m × m grid of image points; 8 to 512.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Frequency 1"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency1 { get; init; } = 10;
    [DisplayName("Frequency 2"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency2 { get; init; } = 20;
    [DisplayName("Frequency 3"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency3 { get; init; } = 30;
    [DisplayName("Frequency 4"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency4 { get; init; }
    [DisplayName("Frequency 5"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency5 { get; init; }
    [DisplayName("Frequency 6"), Description("A spatial frequency plotted, cycles/mm; 0 leaves it out.")]
    public double Frequency6 { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors with surface apertures first (the help's default).")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Field density"), Description("Points from the axis to the largest defined field in the scan direction; 2 to 100.")]
    public int FieldDensity { get; init; } = 10;

    [DisplayName("Scan type"), Description("The field direction scanned: +y, +x, −y or −x.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }

    internal double[] Frequencies => new[] { Frequency1, Frequency2, Frequency3, Frequency4, Frequency5, Frequency6 };
}

/// <summary>Settings of the <see cref="GeometricMtfAnalysis"/> (help, "Geometric MTF").</summary>
public sealed record GeometricMtfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Max frequency"), Description("The largest frequency plotted, in MTF units; 0 for the largest diffraction cutoff.")]
    public double MaxFrequency { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Multiply by diffraction limit"), Description("Scale the geometric MTF by the diffraction-limited MTF of the same pupil (the help: \"Should always be used\").")]
    public bool MultiplyByDiffractionLimit { get; init; } = true;

    [DisplayName("Show diffraction limit"), Description("Draw the reference field's FFT MTF with the OPD set to zero.")]
    public bool ShowDiffractionLimit { get; init; } = true;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;
}

/// <summary>Settings of the <see cref="GeometricThroughFocusMtfAnalysis"/> (help, "Geometric Through Focus MTF").</summary>
public sealed record GeometricThroughFocusMtfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Delta focus"), Description("The ± range of the focus shift, lens units.")]
    public double DeltaFocus { get; init; } = 0.1;

    [DisplayName("Frequency"), Description("The spatial frequency plotted, cycles/mm.")]
    public double Frequency { get; init; } = 30;

    [DisplayName("# Steps"), Description("Focus planes from −delta to +delta; 2 to 100.")]
    public int Steps { get; init; } = 11;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Multiply by diffraction limit"), Description("Scale the geometric MTF by the diffraction-limited MTF of the same pupil.")]
    public bool MultiplyByDiffractionLimit { get; init; } = true;

    [DisplayName("Show diffraction limit"), Description("Draw the reference field's diffraction-limited value at this frequency.")]
    public bool ShowDiffractionLimit { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;
}

/// <summary>Settings of the <see cref="GeometricMtfVsFieldAnalysis"/> (help, "Geometric MTF vs. Field").</summary>
public sealed record GeometricMtfVsFieldSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 1024.")]
    public int Sampling { get; init; } = 32;

    [DisplayName("Frequency 1"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency1 { get; init; } = 10;
    [DisplayName("Frequency 2"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency2 { get; init; } = 20;
    [DisplayName("Frequency 3"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency3 { get; init; } = 30;
    [DisplayName("Frequency 4"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency4 { get; init; }
    [DisplayName("Frequency 5"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency5 { get; init; }
    [DisplayName("Frequency 6"), Description("A spatial frequency plotted, in MTF units; 0 leaves it out.")]
    public double Frequency6 { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Multiply by diffraction limit"), Description("Scale the geometric MTF by the diffraction-limited MTF of the same pupil.")]
    public bool MultiplyByDiffractionLimit { get; init; } = true;

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors with surface apertures first (the help's default).")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Field density"), Description("Points from the axis to the largest defined field in the scan direction; 2 to 100.")]
    public int FieldDensity { get; init; } = 10;

    [DisplayName("Scan type"), Description("The field direction scanned: +y, +x, −y or −x.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    internal double[] Frequencies => new[] { Frequency1, Frequency2, Frequency3, Frequency4, Frequency5, Frequency6 };
}

/// <summary>Settings of the <see cref="GeometricMtfMapAnalysis"/> (help, "Geometric MTF Map").</summary>
public sealed record GeometricMtfMapSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 1024.")]
    public int Sampling { get; init; } = 32;

    [DisplayName("X field width"), Description("The total width of the map in x, field units; 0 for twice the largest radial field.")]
    public double XFieldWidth { get; init; }

    [DisplayName("Y field width"), Description("The total height of the map in y, field units; 0 for twice the largest radial field.")]
    public double YFieldWidth { get; init; }

    [DisplayName("Frequency"), Description("The spatial frequency mapped, in MTF units.")]
    public double Frequency { get; init; } = 30;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("X pixels"), Description("Pixels across the map in x, each computed at its centre; 2 to 101.")]
    public int XPixels { get; init; } = 11;

    [DisplayName("Y pixels"), Description("Pixels across the map in y; 2 to 101.")]
    public int YPixels { get; init; } = 11;

    [DisplayName("MTF data"), Description("Tangential, sagittal or their average (the help offers no minimum or maximum here).")]
    public MtfMapData Data { get; init; } = MtfMapData.Tangential;

    [DisplayName("Reference field"), Description("The field row at the centre of the map; 0 for the field (0, 0).")]
    public int ReferenceField { get; init; }

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Multiply by diffraction limit"), Description("Scale the geometric MTF by the diffraction-limited MTF of the same pupil.")]
    public bool MultiplyByDiffractionLimit { get; init; } = true;

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors with surface apertures first (the help's default).")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;
}

/// <summary>What the OP-39 analyses share: the category, the lens source, the thread limit.</summary>
public abstract class MtfAnalysisBase<TSettings> : WavefrontAnalysisBase<TSettings> where TSettings : class, new()
{
    protected MtfAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) : base(lensSource) { }

    public override string Category => "MTF";

    /// <summary>The thread limit over field points and focus planes (null: none); the result is the
    /// same bits at any value, each point computed in its own slot on one thread.</summary>
    internal int? MaxDegreeOfParallelism { get; init; }

    internal static MtfMethodSettings Fft(int sampling, bool check, bool allow) =>
        new() { Method = MtfMethod.Fft, Sampling = sampling, CheckApertures = check, AllowOpdLimitations = allow };

    internal static MtfMethodSettings Huygens(int pupil, int image, double deltaMicrometers, bool check, bool allow) =>
        new() { Method = MtfMethod.Huygens, Sampling = pupil, ImageSampling = image, ImageDelta = deltaMicrometers * 1e-6, CheckApertures = check, AllowOpdLimitations = allow };

    internal static MtfMethodSettings Geometric(int sampling, bool multiply, bool check) =>
        new() { Method = MtfMethod.Geometric, Sampling = sampling, MultiplyByDiffractionLimit = multiply, CheckApertures = check, AllowOpdLimitations = true };

    internal static void HuygensChecks(string who, int pupil, int image, int maxImage, double delta)
    {
        MtfRuns.CheckSampling(pupil, 8, 512, who, "pupil sampling");
        MtfRuns.CheckSampling(image, 8, maxImage, who, "image sampling");
        if (!double.IsFinite(delta) || delta < 0) throw new ArgumentException($"{who}: the image delta must be 0 or positive (it is {delta}).");
    }
}

/// <summary>OpticStudio's FFT MTF (OP-39): the pupil autocorrelation against frequency.</summary>
public sealed class FftMtfAnalysis : MtfAnalysisBase<FftMtfSettings>
{
    public const string SavedId = "FftMtf";
    public FftMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "FFT MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, FftMtfSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 16, 1024, Name, "sampling");
        return MtfRuns.Frequency(Name, "FFT", Fft(s.Sampling, s.CheckApertures, s.AllowOpdLimitations), lens, system, notes, s.Field, s.Wavelength,
            s.MaxFrequency, s.Type, s.ShowDiffractionLimit, cancellation);
    }
}

/// <summary>OpticStudio's FFT Through Focus MTF (OP-39).</summary>
public sealed class FftThroughFocusMtfAnalysis : MtfAnalysisBase<FftThroughFocusMtfSettings>
{
    public const string SavedId = "FftThroughFocusMtf";
    public FftThroughFocusMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "FFT through focus MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, FftThroughFocusMtfSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 16, 1024, Name, "sampling");
        return MtfRuns.ThroughFocus(Name, "FFT", Fft(s.Sampling, s.CheckApertures, s.AllowOpdLimitations), lens, system, notes, s.Field, s.Wavelength,
            s.Frequency, s.DeltaFocus, s.Steps, s.Type, s.ShowDiffractionLimit, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's FFT Surface MTF (OP-39): the OTF over the frequency plane.</summary>
public sealed class FftSurfaceMtfAnalysis : MtfAnalysisBase<FftSurfaceMtfSettings>
{
    public const string SavedId = "FftSurfaceMtf";
    public FftSurfaceMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "FFT surface MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, FftSurfaceMtfSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 16, 512, Name, "sampling");
        return MtfRuns.Surface(Name, "FFT", Fft(s.Sampling, s.CheckApertures, s.AllowOpdLimitations), lens, system, notes, s.Field, s.Wavelength, s.Type, s.ShowAs, cancellation);
    }
}

/// <summary>OpticStudio's FFT MTF vs. Field (OP-39).</summary>
public sealed class FftMtfVsFieldAnalysis : MtfAnalysisBase<FftMtfVsFieldSettings>
{
    public const string SavedId = "FftMtfVsField";
    public FftMtfVsFieldAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "FFT MTF vs field";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, FftMtfVsFieldSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 16, 1024, Name, "sampling");
        return MtfRuns.VsField(Name, "FFT", Fft(s.Sampling, s.CheckApertures, s.AllowOpdLimitations), lens, system, notes, s.Frequencies, s.Wavelength,
            s.FieldDensity, s.Scan, s.RemoveVignettingFactors, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's FFT MTF Map (OP-39).</summary>
public sealed class FftMtfMapAnalysis : MtfAnalysisBase<FftMtfMapSettings>
{
    public const string SavedId = "FftMtfMap";
    public FftMtfMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "FFT MTF map";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, FftMtfMapSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 16, 1024, Name, "sampling");
        return MtfRuns.Map(Name, "FFT", Fft(s.Sampling, s.CheckApertures, s.AllowOpdLimitations), lens, system, notes, s.XFieldWidth, s.YFieldWidth,
            s.Frequency, s.Wavelength, s.XPixels, s.YPixels, s.Data, s.ReferenceField, s.ShowAs, s.RemoveVignettingFactors, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's Huygens MTF (OP-39): the transform of the Huygens PSF.</summary>
public sealed class HuygensMtfAnalysis : MtfAnalysisBase<HuygensMtfSettings>
{
    public const string SavedId = "HuygensMtf";
    public HuygensMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Huygens MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, HuygensMtfSettings s, CancellationToken cancellation)
    {
        HuygensChecks(Name, s.PupilSampling, s.ImageSampling, 512, s.ImageDeltaMicrometers);
        return MtfRuns.Frequency(Name, "Huygens", Huygens(s.PupilSampling, s.ImageSampling, s.ImageDeltaMicrometers, s.CheckApertures, s.AllowOpdLimitations),
            lens, system, notes, s.Field, s.Wavelength, s.MaxFrequency, MtfDataType.Modulation, s.ShowDiffractionLimit, cancellation);
    }
}

/// <summary>OpticStudio's Huygens Through Focus MTF (OP-39).</summary>
public sealed class HuygensThroughFocusMtfAnalysis : MtfAnalysisBase<HuygensThroughFocusMtfSettings>
{
    public const string SavedId = "HuygensThroughFocusMtf";
    public HuygensThroughFocusMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Huygens through focus MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, HuygensThroughFocusMtfSettings s, CancellationToken cancellation)
    {
        HuygensChecks(Name, s.PupilSampling, s.ImageSampling, 512, s.ImageDeltaMicrometers);
        return MtfRuns.ThroughFocus(Name, "Huygens", Huygens(s.PupilSampling, s.ImageSampling, s.ImageDeltaMicrometers, s.CheckApertures, s.AllowOpdLimitations),
            lens, system, notes, s.Field, s.Wavelength, s.Frequency, s.DeltaFocus, s.Steps, MtfDataType.Modulation, s.ShowDiffractionLimit, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's Huygens Surface MTF (OP-39).</summary>
public sealed class HuygensSurfaceMtfAnalysis : MtfAnalysisBase<HuygensSurfaceMtfSettings>
{
    public const string SavedId = "HuygensSurfaceMtf";
    public HuygensSurfaceMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Huygens surface MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, HuygensSurfaceMtfSettings s, CancellationToken cancellation)
    {
        HuygensChecks(Name, s.PupilSampling, s.ImageSampling, 256, s.ImageDeltaMicrometers);
        return MtfRuns.Surface(Name, "Huygens", Huygens(s.PupilSampling, s.ImageSampling, s.ImageDeltaMicrometers, s.CheckApertures, s.AllowOpdLimitations),
            lens, system, notes, s.Field, s.Wavelength, s.Type, s.ShowAs, cancellation);
    }
}

/// <summary>OpticStudio's Huygens MTF vs. Field (OP-39).</summary>
public sealed class HuygensMtfVsFieldAnalysis : MtfAnalysisBase<HuygensMtfVsFieldSettings>
{
    public const string SavedId = "HuygensMtfVsField";
    public HuygensMtfVsFieldAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Huygens MTF vs field";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, HuygensMtfVsFieldSettings s, CancellationToken cancellation)
    {
        HuygensChecks(Name, s.PupilSampling, s.ImageSampling, 512, s.ImageDeltaMicrometers);
        return MtfRuns.VsField(Name, "Huygens", Huygens(s.PupilSampling, s.ImageSampling, s.ImageDeltaMicrometers, s.CheckApertures, s.AllowOpdLimitations),
            lens, system, notes, s.Frequencies, s.Wavelength, s.FieldDensity, s.Scan, s.RemoveVignettingFactors, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's Geometric MTF (OP-39): the transform of the ray spot.</summary>
public sealed class GeometricMtfAnalysis : MtfAnalysisBase<GeometricMtfSettings>
{
    public const string SavedId = "GeometricMtf";
    public GeometricMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Geometric MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, GeometricMtfSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 8, 1024, Name, "sampling");
        return MtfRuns.Frequency(Name, "Geometric", Geometric(s.Sampling, s.MultiplyByDiffractionLimit, s.CheckApertures), lens, system, notes,
            s.Field, s.Wavelength, s.MaxFrequency, MtfDataType.Modulation, s.ShowDiffractionLimit, cancellation);
    }
}

/// <summary>OpticStudio's Geometric Through Focus MTF (OP-39).</summary>
public sealed class GeometricThroughFocusMtfAnalysis : MtfAnalysisBase<GeometricThroughFocusMtfSettings>
{
    public const string SavedId = "GeometricThroughFocusMtf";
    public GeometricThroughFocusMtfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Geometric through focus MTF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, GeometricThroughFocusMtfSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 8, 1024, Name, "sampling");
        return MtfRuns.ThroughFocus(Name, "Geometric", Geometric(s.Sampling, s.MultiplyByDiffractionLimit, s.CheckApertures), lens, system, notes,
            s.Field, s.Wavelength, s.Frequency, s.DeltaFocus, s.Steps, MtfDataType.Modulation, s.ShowDiffractionLimit, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's Geometric MTF vs. Field (OP-39).</summary>
public sealed class GeometricMtfVsFieldAnalysis : MtfAnalysisBase<GeometricMtfVsFieldSettings>
{
    public const string SavedId = "GeometricMtfVsField";
    public GeometricMtfVsFieldAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Geometric MTF vs field";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, GeometricMtfVsFieldSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 8, 1024, Name, "sampling");
        return MtfRuns.VsField(Name, "Geometric", Geometric(s.Sampling, s.MultiplyByDiffractionLimit, s.CheckApertures), lens, system, notes,
            s.Frequencies, s.Wavelength, s.FieldDensity, s.Scan, s.RemoveVignettingFactors, MaxDegreeOfParallelism, cancellation);
    }
}

/// <summary>OpticStudio's Geometric MTF Map (OP-39).</summary>
public sealed class GeometricMtfMapAnalysis : MtfAnalysisBase<GeometricMtfMapSettings>
{
    public const string SavedId = "GeometricMtfMap";
    public GeometricMtfMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Geometric MTF map";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, GeometricMtfMapSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 8, 1024, Name, "sampling");
        if (s.Data is MtfMapData.Minimum or MtfMapData.Maximum)
            throw new ArgumentException($"{Name}: the help offers tangential, sagittal or average data for the geometric MTF map (\"Choose Tangential, Sagittal, or average GMTF\"); minimum and maximum are the FFT MTF map's.");
        return MtfRuns.Map(Name, "Geometric", Geometric(s.Sampling, s.MultiplyByDiffractionLimit, s.CheckApertures), lens, system, notes, s.XFieldWidth, s.YFieldWidth,
            s.Frequency, s.Wavelength, s.XPixels, s.YPixels, s.Data, s.ReferenceField, s.ShowAs, s.RemoveVignettingFactors, MaxDegreeOfParallelism, cancellation);
    }
}
