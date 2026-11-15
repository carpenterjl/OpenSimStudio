namespace OpenSim.Core.Model;

/// <summary>
/// The Optimization Wizard's saved settings (plan item OP-56; OpticStudio's "Save Settings"
/// button of the wizard). Null on <see cref="OpticsSettings.MeritWizard"/> — every file written
/// before OP-56, or a project whose wizard settings were never saved — is the wizard's defaults;
/// so is each null property. Enumerated choices are saved by name, so a later build's choice this
/// one does not know loads as the default and is named. OpenSim.Optics'
/// <c>DefaultMeritWizardSettings</c> converts to and from its options. Lengths in lens units.
/// </summary>
public sealed record OpticsMeritWizardSettings
{
    /// <summary>"Wavefront", "SpotRadius", "SpotX", "SpotY", "SpotXY", "Angular" or "Contrast".</summary>
    public string? Criterion { get; init; }

    /// <summary>"Centroid", "ChiefRay" or "Unreferenced".</summary>
    public string? Reference { get; init; }

    /// <summary>"GaussianQuadrature" or "RectangularArray".</summary>
    public string? Integration { get; init; }

    public int? Rings { get; init; }

    public int? Arms { get; init; }

    public int? Grid { get; init; }

    public bool? DeleteVignetted { get; init; }

    /// <summary>True or false; null is automatic (on when the lens is rotationally symmetric).</summary>
    public bool? AssumeAxialSymmetry { get; init; }

    public bool? IgnoreLateralColor { get; init; }

    /// <summary>The start row; 0 the end; null the row after DMFS.</summary>
    public int? StartAt { get; init; }

    public double? OverallWeight { get; init; }

    public double? RelativeXWeight { get; init; }

    public double? RelativeYWeight { get; init; }

    /// <summary>Contrast: the weights of the sagittal and tangential phase differences.</summary>
    public double? RelativeSWeight { get; init; }

    public double? RelativeTWeight { get; init; }

    /// <summary>Contrast: the spatial frequency, cycles/mm.</summary>
    public double? SpatialFrequency { get; init; }

    /// <summary>"Current" or "All".</summary>
    public string? Configurations { get; init; }

    public bool? GlassBoundaries { get; init; }

    public double? GlassMinimum { get; init; }

    public double? GlassMaximum { get; init; }

    public double? GlassEdge { get; init; }

    public bool? AirBoundaries { get; init; }

    public double? AirMinimum { get; init; }

    public double? AirMaximum { get; init; }

    public double? AirEdge { get; init; }
}
