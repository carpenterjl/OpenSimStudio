using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Analysis.EnclosedEnergy;

// ---------------------------------------------------------------------------------------------
// OP-41: enclosed energy. The kinds of region and reference point, and the Airy disk's enclosed
// energy. OpticStudio's behaviour is read from its 2025 R2 help pages "Enclosed Energy",
// "Diffraction (enclosed energy)", "Geometric", "Geometric Line/Edge Spread", "Extended Source"
// and "Geometric Image Analysis", saved under plans/sources/optics/op-41-help/
// (docs/Optics-Conventions.md §39).
// ---------------------------------------------------------------------------------------------

/// <summary>The region whose enclosed energy is computed (help, "Diffraction (enclosed energy)":
/// "either encircled (radial), X-only, Y-only, or ensquared").</summary>
public enum EnclosedEnergyType
{
    /// <summary>A circle of the given radius about the reference point.</summary>
    Encircled = 0,
    /// <summary>The slit |x − x_ref| ≤ size, infinite in y (help: "the X-only enclosed energy for
    /// an x-value of 10 microns represents the fraction of energy in the region 20 microns across
    /// in the x-direction, centered on the reference point, and infinite in the other
    /// direction").</summary>
    XOnly = 1,
    /// <summary>The slit |y − y_ref| ≤ size, infinite in x.</summary>
    YOnly = 2,
    /// <summary>The square max(|x − x_ref|, |y − y_ref|) ≤ size: the size is the half-width.</summary>
    Ensquared = 3,
}

/// <summary>The point the regions are centred on (OpticStudio's "Refer To").</summary>
public enum EnclosedEnergyReference
{
    /// <summary>The chief ray: the selected wavelength's, or the primary wavelength's when every
    /// wavelength is used.</summary>
    ChiefRay = 0,
    /// <summary>The centroid of the light (the weighted mean ray intercept).</summary>
    Centroid = 1,
    /// <summary>The surface's local (0, 0) (help: "Vertex refers to the coordinates (0, 0) on the
    /// image surface").</summary>
    Vertex = 2,
    /// <summary>Geometric only: "the coordinate upon which the smallest circle enclosing all the
    /// ray intercepts is centered" (help, "Geometric").</summary>
    Middle = 3,
}

/// <summary>
/// The enclosed energy of the Airy pattern of a clear circular pupil, E(v) = 1 − J₀(v)² − J₁(v)²,
/// v = π·r/(λ·F), λ the wavelength in the system's air and F = 1/(2n·a) the working F/#, n the
/// image-space index and a the pupil scale (the pairing of §33).
/// <para>
/// <b>Derivation.</b> The Airy intensity over its peak is (2J₁(v)/v)² with v = 2π·n·a·r/λ
/// (§33's kernel for a clear pupil of scale a), and its integral over the plane, in v, is
/// ∫ (2J₁/v)² 2πv dv = 4π. So E(v) = (1/4π)∫₀^v (2J₁/t)² 2πt dt = ∫₀^v 2J₁(t)²/t dt. With
/// J₀′ = −J₁ and J₁′ = J₀ − J₁/v (DLMF 10.6.2), d/dv[−J₀² − J₁²] = 2J₀J₁ − 2J₁(J₀ − J₁/v) = 2J₁²/v,
/// and −J₀(0)² − J₁(0)² = −1, so E(v) = 1 − J₀(v)² − J₁(v)², which tends to 1 as v → ∞.
/// With F = 1/(2n·a), 2π·n·a = π/F.
/// </para>
/// </summary>
public static class AiryEnclosedEnergy
{
    /// <summary>E(v) = 1 − J₀(v)² − J₁(v)².</summary>
    public static double Fraction(double v)
    {
        if (!double.IsFinite(v) || v < 0) throw new ArgumentOutOfRangeException(nameof(v), v, "Airy enclosed energy: v must be finite and not negative.");
        double j0 = Bessel.J0(v), j1 = Bessel.J1(v);
        return Math.Max(0, 1 - j0 * j0 - j1 * j1);
    }

    /// <summary>E at radius r (meters) for wavelength λ (meters) and working F/# F.</summary>
    public static double AtRadius(double radius, double wavelength, double fNumber)
    {
        if (!(wavelength > 0) || !(fNumber > 0))
            throw new ArgumentOutOfRangeException(nameof(wavelength), $"Airy enclosed energy: the wavelength ({wavelength:g6} m) and F/# ({fNumber:g6}) must be positive.");
        return Fraction(Math.PI * radius / (wavelength * fNumber));
    }
}

/// <summary>Labels and the region metric shared by the enclosed-energy analyses.</summary>
public static class EnclosedEnergyRegions
{
    /// <summary>The distance in the region's own metric: the smallest size whose region holds
    /// the offset (dx, dy) from the reference.</summary>
    public static double Distance(EnclosedEnergyType type, double dx, double dy) => type switch
    {
        EnclosedEnergyType.Encircled => Math.Sqrt(dx * dx + dy * dy),
        EnclosedEnergyType.XOnly => Math.Abs(dx),
        EnclosedEnergyType.YOnly => Math.Abs(dy),
        EnclosedEnergyType.Ensquared => Math.Max(Math.Abs(dx), Math.Abs(dy)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown enclosed-energy type."),
    };

    /// <summary>What the size means, for an axis label.</summary>
    public static string SizeLabel(EnclosedEnergyType type) => type switch
    {
        EnclosedEnergyType.Encircled => "Radius from the reference (µm)",
        EnclosedEnergyType.XOnly => "Half-width in x from the reference (µm)",
        EnclosedEnergyType.YOnly => "Half-width in y from the reference (µm)",
        _ => "Half-width of the square about the reference (µm)",
    };

    public static string TypeLabel(EnclosedEnergyType type) => type switch
    {
        EnclosedEnergyType.Encircled => "encircled",
        EnclosedEnergyType.XOnly => "x-only (slit infinite in y)",
        EnclosedEnergyType.YOnly => "y-only (slit infinite in x)",
        _ => "ensquared",
    };

    public static string ReferenceLabel(EnclosedEnergyReference reference) => reference switch
    {
        EnclosedEnergyReference.ChiefRay => "chief ray",
        EnclosedEnergyReference.Centroid => "centroid",
        EnclosedEnergyReference.Vertex => "vertex (local 0, 0)",
        _ => "middle (centre of the smallest circle holding every ray)",
    };

    /// <summary><paramref name="count"/> + 1 sizes from 0 to <paramref name="maximum"/>.</summary>
    public static double[] Sizes(double maximum, int count = 100)
    {
        var sizes = new double[count + 1];
        for (int i = 0; i <= count; i++) sizes[i] = maximum * i / count;
        sizes[count] = maximum;
        return sizes;
    }
}
