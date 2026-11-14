using OpenSim.Core.Numerics;

namespace OpenSim.Optics;

/// <summary>
/// The wavelength group of the system data: the table in order (wavelength 1 first) and the
/// primary wavelength, the one paraxial data and single-wavelength results use.
/// </summary>
public sealed record WavelengthSet
{
    /// <summary>The default is one wavelength, 0.55 µm.</summary>
    public IReadOnlyList<Wavelength> Wavelengths { get; init; } = new[] { new Wavelength() };

    /// <summary>The primary wavelength's 0-based index into <see cref="Wavelengths"/>.</summary>
    public int PrimaryIndex { get; init; }

    /// <summary>The primary wavelength. Throws when the index is out of range, which
    /// <see cref="Validate"/> reports first.</summary>
    public Wavelength Primary => Wavelengths[PrimaryIndex];

    /// <summary>Adds one line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        if (Wavelengths.Count == 0)
        {
            problems.Add("Wavelengths: a system needs at least one wavelength.");
            return;
        }
        for (int i = 0; i < Wavelengths.Count; i++)
        {
            var w = Wavelengths[i];
            if (!double.IsFinite(w.Micrometers) || !(w.Micrometers > 0))
                problems.Add($"Wavelength {i + 1}: must be a positive number of micrometers (it is {w.Micrometers}).");
            if (!double.IsFinite(w.Weight) || w.Weight < 0)
                problems.Add($"Wavelength {i + 1}: the weight must be zero or positive (it is {w.Weight}).");
        }
        if (Wavelengths.All(w => !(w.Weight > 0)))
            problems.Add("Wavelengths: at least one wavelength needs a weight above zero, or weighted results have nothing to average.");
        if (PrimaryIndex < 0 || PrimaryIndex >= Wavelengths.Count)
            problems.Add($"Wavelengths: the primary wavelength must be one of the {Wavelengths.Count} in the table (it is number {PrimaryIndex + 1}).");
    }

    /// <summary>
    /// Wavelengths and weights for a band by Gauss–Legendre quadrature in wavelength: the
    /// <paramref name="count"/> nodes of the rule on [<paramref name="minMicrometers"/>,
    /// <paramref name="maxMicrometers"/>] (<see cref="GaussLegendre.Rule(int, double, double)"/>),
    /// with the rule's weights scaled so the largest is 1 (only relative weights matter). A
    /// weighted sum over them integrates any result that is a polynomial of degree up to
    /// 2·count − 1 in wavelength exactly, for light of flat spectrum over the band. The primary
    /// is the node nearest the band's centre (the lower of the two central nodes for an even
    /// count). This is our own definition; OpticStudio's Gaussian-quadrature selection has not
    /// been read at the source or compared with it.
    /// </summary>
    public static WavelengthSet GaussianQuadrature(double minMicrometers, double maxMicrometers, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "Quadrature needs at least one wavelength.");
        if (!double.IsFinite(minMicrometers) || !(minMicrometers > 0) || !double.IsFinite(maxMicrometers) || !(maxMicrometers > minMicrometers))
            throw new ArgumentException($"The band must run from a positive wavelength up to a longer one (it is {minMicrometers} to {maxMicrometers} µm).");
        var (nodes, weights) = GaussLegendre.Rule(count, minMicrometers, maxMicrometers);
        double largest = weights.Max();
        var rows = new Wavelength[count];
        for (int i = 0; i < count; i++) rows[i] = new Wavelength { Micrometers = nodes[i], Weight = weights[i] / largest };
        return new WavelengthSet { Wavelengths = rows, PrimaryIndex = (count - 1) / 2 };
    }
}
