using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>Clear and mechanical semi-diameters of every surface (meters), and how each was found.</summary>
public sealed record SemiDiameterReport
{
    /// <summary>The clear semi-diameter of each surface (0 for an object at infinity).</summary>
    public required IReadOnlyList<double> Clear { get; init; }

    /// <summary>The mechanical semi-diameter: the larger of the given mechanical one (or the
    /// clear one) and clear plus chip zone.</summary>
    public required IReadOnlyList<double> Mechanical { get; init; }

    /// <summary>True where the clear semi-diameter was sized from rays, false where it was given.</summary>
    public required IReadOnlyList<bool> Automatic { get; init; }

    /// <summary>Rays that failed while sizing, one line each; surfaces after a failure are
    /// sized without that ray.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Automatic semi-diameters (docs/Optics-Conventions.md §13): for every field and wavelength,
/// the chief ray and <c>rimPoints</c> rays round the rim of the paraxial pupil (the first at
/// Py = 1, then evenly spaced) are traced with apertures ignored, and each surface's clear
/// semi-diameter is the largest radial height √(x² + y²) any of them reaches there, grown by
/// the system's margins: (h + margin)·(1 + percent/100), the margin first as
/// <see cref="Aperture.SemiDiameterMarginPercent"/> states. Four rim points (the marginal and
/// sagittal rays) is the fast choice; more find the rim's maximum more closely on a decentered
/// beam. A surface with a fixed semi-diameter keeps it. The order in which the two margins
/// apply was set by OP-18 and has not been compared with OpticStudio (§10, item 9).
/// </summary>
public static class SemiDiameterSizing
{
    public static SemiDiameterReport Compute(SequentialLens lens, OpticalSystemData system, int rimPoints = 4,
        int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        if (rimPoints < 1) throw new ArgumentOutOfRangeException(nameof(rimPoints), rimPoints, "At least one rim point is needed.");
        var launcher = new RayLauncher(lens, system);
        int wavelengths = system.Wavelengths.Wavelengths.Count, fields = system.Fields.Points.Count;
        var traces = new SequentialTrace[wavelengths];
        for (int w = 0; w < wavelengths; w++) traces[w] = SequentialTrace.Create(lens, system, w);

        var pupil = new List<(double Px, double Py)> { (0, 0) };
        for (int i = 0; i < rimPoints; i++)
        {
            var (s, c) = OpticsConventions.SinCosDegrees(360.0 * i / rimPoints);
            pupil.Add((s, c));
        }

        int count = lens.Surfaces.Count;
        var options = new TraceOptions { IgnoreApertures = true };
        var slots = ParallelSlots.Compute(fields * wavelengths, maxDegreeOfParallelism, slot =>
        {
            int f = slot / wavelengths, w = slot % wavelengths;
            var (hx, hy) = system.Fields.Normalized(f);
            var heights = new double[count];
            var notes = new List<string>();
            foreach (var (px, py) in pupil)
            {
                cancellation.ThrowIfCancellationRequested();
                var path = traces[w].TracePath(launcher.Launch(traces[w], hx, hy, px, py), options);
                foreach (var hit in path.Hits)
                {
                    double r = Math.Sqrt(hit.Position.X * hit.Position.X + hit.Position.Y * hit.Position.Y);
                    if (r > heights[hit.Surface]) heights[hit.Surface] = r;
                }
                if (!path.Final.IsOk)
                    notes.Add($"Field {f + 1}, wavelength {w + 1}, pupil ({px:g4}, {py:g4}): {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}; later surfaces are sized without this ray.");
            }
            return (heights, notes);
        }, cancellation);

        var clear = new double[count];
        var automatic = new bool[count];
        var allNotes = new List<string>();
        foreach (var (heights, notes) in slots)
        {
            for (int j = 0; j < count; j++) clear[j] = Math.Max(clear[j], heights[j]);
            allNotes.AddRange(notes);
        }
        if (lens.ObjectAtInfinity) clear[0] = 0;
        double grow = 1 + system.Aperture.SemiDiameterMarginPercent / 100;
        var mechanical = new double[count];
        for (int j = 0; j < count; j++)
        {
            var s = lens.Surfaces[j];
            if (s.SemiDiameter is double given) clear[j] = given;
            else
            {
                automatic[j] = true;
                if (!(j == 0 && lens.ObjectAtInfinity)) clear[j] = (clear[j] + system.Aperture.SemiDiameterMargin) * grow;
            }
            mechanical[j] = Math.Max(s.MechanicalSemiDiameter ?? clear[j], clear[j] + s.ChipZone);
        }
        return new SemiDiameterReport { Clear = clear, Mechanical = mechanical, Automatic = automatic, Notes = allNotes };
    }
}
