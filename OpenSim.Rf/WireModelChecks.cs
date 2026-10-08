namespace OpenSim.Rf;

/// <summary>
/// What the thin-wire model assumes about a discretized structure at a given frequency, checked
/// rather than assumed. Every item returned is a sentence to show next to the result.
/// </summary>
public static class WireModelChecks
{
    /// <summary>Elements no longer than this fraction of a wavelength resolve the current.</summary>
    public const double MaxElementWavelengths = 0.1;

    /// <summary>The thin-wire kernel takes the current as a filament on the axis observed at
    /// the radius, which needs k·a ≪ 1; above about 0.1 (a circumference of a tenth of a
    /// wavelength) the azimuthal variation it ignores is no longer small.</summary>
    public const double MaxKa = 0.1;

    public static IReadOnlyList<string> ThinWire(WireStructure wire, double frequencyHz)
    {
        var warnings = new List<string>();
        double wavelength = RfConstants.SpeedOfLight / frequencyHz;
        double k = 2 * Math.PI / wavelength;

        double longest = 0, widest = 0;
        for (int e = 0; e < wire.ElementCount; e++)
        {
            longest = Math.Max(longest, wire.ElementLength(e));
            widest = Math.Max(widest, wire.ElementRadii[e]);
        }
        if (longest > MaxElementWavelengths * wavelength * (1 + 1e-9))
            warnings.Add(
                $"The longest element is λ/{wavelength / longest:g3} at {frequencyHz / 1e6:g4} MHz; " +
                "the current needs λ/10 or shorter to be resolved.");
        if (k * widest > MaxKa)
            warnings.Add(
                $"k·a = {k * widest:g3} for the widest conductor (equivalent radius " +
                $"{widest * 1e3:g3} mm) at {frequencyHz / 1e6:g4} MHz; the thin-wire kernel assumes " +
                $"k·a well below {MaxKa:g2}. Model that conductor as sheet metal instead.");
        return warnings;
    }

    /// <summary>Where a feed at <paramref name="node"/> can sit: each basis that has its peak there,
    /// with the directions of the two wire pieces its gap would lie between. At a junction of
    /// three or more wires there are several, and the choice is the user's.</summary>
    public static IReadOnlyList<(int Basis, string Description)> FeedOptionsAt(WireStructure wire, int node)
    {
        var options = new List<(int, string)>();
        for (int b = 0; b < wire.BasisCount; b++)
        {
            if (wire.BasisNode(b) != node) continue;
            var toward = wire.BasisHalves(b).Select(leg =>
            {
                var (a, c) = wire.Elements[leg.Element];
                var far = wire.Nodes[a == node ? c : a];
                return $"({far.X * 1e3:g4}, {far.Y * 1e3:g4}, {far.Z * 1e3:g4}) mm";
            }).ToList();
            options.Add((b, toward.Count == 2
                ? $"between the wire toward {toward[0]} and the one toward {toward[1]}"
                : $"at the end of the wire toward {string.Join(", ", toward)}"));
        }
        return options;
    }

    /// <summary>A delta gap sits in ONE basis. Where three or more wires meet, the node carries
    /// several bases (each pairs one wire against a common reference), so a feed placed there
    /// drives the current between two particular wires — which two is a numbering detail, not
    /// something the user chose. Returns a sentence saying so, or null for an ordinary node.</summary>
    public static string? FeedAtJunction(WireStructure wire, int feedBasis)
    {
        int node = wire.BasisNode(feedBasis);
        int sharing = 0;
        for (int b = 0; b < wire.BasisCount; b++)
            if (wire.BasisNode(b) == node) sharing++;
        if (sharing < 2) return null;
        var p = wire.Nodes[node];
        return $"The feed lands on a junction of {sharing + 1} wires at " +
               $"({p.X * 1e3:g4}, {p.Y * 1e3:g4}, {p.Z * 1e3:g4}) mm; the gap is placed between two " +
               "of them, chosen by element order. Move the feed point along the wire that should " +
               "carry it.";
    }
}
