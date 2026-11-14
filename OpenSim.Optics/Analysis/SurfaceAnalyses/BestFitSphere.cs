using OpenSim.Core.Numerics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>OpticStudio's "Best Fit Sphere Criterion" (Ansys OpticStudio 2025 R2 help, "Sag
/// Table", The Tolerance Tab » Manufacturing Drawings and Data Group).</summary>
public enum BestFitSphereCriterion
{
    /// <summary>"Minimum Volume will minimize the volume difference between the BFS surface and
    /// the selected lens surface": the sphere lies wholly on the air side of the surface and the
    /// volume of material to remove is least.</summary>
    MinimumVolume,
    /// <summary>"Minimum RMS (No Offset)": least RMS difference with the vertices together.</summary>
    MinimumRmsNoOffset,
    /// <summary>"Minimum RMS (With Offset)": least RMS difference with a vertex offset allowed.</summary>
    MinimumRmsWithOffset,
}

/// <summary>A best-fit sphere and the figures OpticStudio's Sag Table reports with it. Lengths
/// meters, curvature 1/m, volume m³.</summary>
public sealed record BestFitSphereFit
{
    public required BestFitSphereCriterion Criterion { get; init; }

    /// <summary>The sphere's curvature, 1/m (0 for a plane).</summary>
    public required double Curvature { get; init; }

    /// <summary>The z of the sphere's vertex in the surface's local frame (0 for "no offset").</summary>
    public required double VertexOffset { get; init; }

    /// <summary>The radial range of the fit, meters.</summary>
    public required double MinRadius { get; init; }
    public required double MaxRadius { get; init; }

    /// <summary>+1 when the air is on the −z side of the surface ("+z goes from air to glass",
    /// as OpticStudio's listing words it), −1 when it is on the +z side. Remove depths are
    /// measured into the material: AirSign·(sag − sphere).</summary>
    public required int AirSign { get; init; }

    /// <summary>The criterion's objective at the fit: the volume to remove (m³) for minimum
    /// volume, the area-weighted RMS difference (m) for the RMS criteria.</summary>
    public required double Objective { get; init; }

    /// <summary>The largest and the area-weighted RMS depth to remove (minimum volume only; NaN
    /// otherwise, as OpticStudio lists them only for that criterion).</summary>
    public double MaxDepthToRemove { get; init; } = double.NaN;
    public double RmsDepthToRemove { get; init; } = double.NaN;

    /// <summary>The volume to remove, m³ (minimum volume only; NaN otherwise).</summary>
    public double VolumeToRemove { get; init; } = double.NaN;

    /// <summary>The largest |dz/dr − BFS dz/dr| and its RMS over 1000 equally spaced y in the
    /// fit range (the help's "1000 equally spaced Y-points").</summary>
    public required double MaxSlopeDeviation { get; init; }
    public required double RmsSlopeDeviation { get; init; }

    /// <summary>The sphere's radius, meters; +∞ for a plane.</summary>
    public double Radius => OpticsConventions.RadiusFromCurvature(Curvature);

    /// <summary>The sphere's sag from its own vertex at radius r (no offset), meters.</summary>
    public double SphereSag(double r) => BestFitSphere.SphereSag(Curvature, r);
}

/// <summary>
/// The best-fit sphere of a surface (OP-43, docs/Optics-Conventions.md §28) by the three
/// criteria of OpticStudio's Sag Table. As the help says of the Sag Table, "only the
/// Y-coordinate of the surface is considered": the fit uses the profile z(0, y) for y in
/// [minimum radius, maximum radius] and treats it as a surface of revolution.
/// <para>
/// The profile is sampled at <see cref="ProfileIntervals"/> + 1 equally spaced radii and every
/// integral over the annulus is composite Simpson with the area weight 2πr. For a curvature c
/// the sphere's sag is s(r) = c·r²/(1 + √(1 − c²r²)), defined for |c|·r<sub>max</sub> &lt; 1.
/// </para>
/// <list type="bullet">
/// <item>Minimum RMS, no offset: c minimises ∫(z − s)²·2πr dr / area.</item>
/// <item>Minimum RMS, with offset: for each c the offset d is the area-weighted mean of
/// z − s (the least-squares value), and c minimises the RMS of z − s − d.</item>
/// <item>Minimum volume: for each c the sphere is moved along z until it touches the surface
/// from the air side (d = min of z − s over the samples when the air is at −z, the max when it
/// is at +z), so every depth to remove AirSign·(z − s − d) is ≥ 0, and c minimises the volume
/// ∫ depth·2πr dr.</item>
/// </list>
/// The minimisation over c scans <see cref="ScanPoints"/> curvatures across the open interval
/// |c| &lt; 1/r<sub>max</sub>, refines the best with Brent's method in the neighbouring scan
/// interval, and for the RMS criteria finishes with Gauss–Newton steps on c using
/// ∂s/∂c = r²/(φ(1 + φ)), φ = √(1 − c²r²) (derived), kept only while they lower the
/// objective.
/// </summary>
public static class BestFitSphere
{
    /// <summary>Equal intervals of the radial profile (even, for Simpson's rule).</summary>
    public const int ProfileIntervals = 2000;

    /// <summary>Curvatures scanned before the Brent refinement.</summary>
    public const int ScanPoints = 400;

    /// <summary>Points of the slope-deviation figures: the help's "1000 equally spaced
    /// Y-points" (Ansys OpticStudio 2025 R2 help, "Sag Table", Results).</summary>
    public const int SlopePoints = 1000;

    /// <summary>s(r) = c·r²/(1 + √(1 − c²r²)); NaN where c²r² ≥ 1.</summary>
    public static double SphereSag(double curvature, double r)
    {
        double a = 1 - curvature * curvature * r * r;
        return a >= 0 ? curvature * r * r / (1 + Math.Sqrt(a)) : double.NaN;
    }

    /// <summary>ds/dr = c·r/√(1 − c²r²); NaN where c²r² ≥ 1.</summary>
    public static double SphereSlope(double curvature, double r)
    {
        double a = 1 - curvature * curvature * r * r;
        return a > 0 ? curvature * r / Math.Sqrt(a) : double.NaN;
    }

    /// <summary>The y profile the fit reads, with its Simpson area weights (sum = annulus area).</summary>
    public sealed class Profile
    {
        internal Profile(double[] r, double[] z, double[] w, double rMin, double rMax)
        {
            R = r; Z = z; W = w; MinRadius = rMin; MaxRadius = rMax;
            Area = w.Sum();
        }

        public IReadOnlyList<double> R { get; }
        public IReadOnlyList<double> Z { get; }
        public IReadOnlyList<double> W { get; }
        public double MinRadius { get; }
        public double MaxRadius { get; }
        public double Area { get; }
    }

    /// <summary>Samples the surface's y profile over [rMin, rMax]; throws naming the first
    /// radius where the sag is not defined.</summary>
    public static Profile Sample(SurfaceProbe probe, double rMin, double rMax, string where)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!(rMin >= 0) || !(rMax > rMin) || !double.IsFinite(rMax))
            throw new ArgumentException($"{where}: the fit needs 0 ≤ minimum radius < maximum radius (they are {rMin} and {rMax} m).");
        int n = ProfileIntervals;
        var r = new double[n + 1];
        var z = new double[n + 1];
        var w = new double[n + 1];
        double h = (rMax - rMin) / n;
        for (int i = 0; i <= n; i++)
        {
            r[i] = i == n ? rMax : rMin + i * h;
            z[i] = probe.Sag(0, r[i]);
            if (!double.IsFinite(z[i]))
                throw new ArgumentException($"{where}: the sag is not defined at y = {r[i]:g6} m, inside the fit range; reduce the maximum radius.");
            double simpson = i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2;
            w[i] = simpson * h / 3 * 2 * Math.PI * r[i];
        }
        return new Profile(r, z, w, rMin, rMax);
    }

    /// <summary>
    /// For curvature <paramref name="curvature"/>, the criterion's vertex offset and objective
    /// (volume in m³, or RMS difference in m); NaN where the sphere does not reach r<sub>max</sub>.
    /// </summary>
    public static (double Offset, double Objective) Evaluate(Profile profile, BestFitSphereCriterion criterion,
        int airSign, double curvature)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (airSign is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(airSign), airSign, "The air sign is +1 or −1.");
        int count = profile.R.Count;
        var e = new double[count];
        for (int i = 0; i < count; i++)
        {
            double s = SphereSag(curvature, profile.R[i]);
            if (double.IsNaN(s)) return (double.NaN, double.NaN);
            e[i] = profile.Z[i] - s;
        }
        double offset;
        switch (criterion)
        {
            case BestFitSphereCriterion.MinimumRmsNoOffset:
                offset = 0;
                break;
            case BestFitSphereCriterion.MinimumRmsWithOffset:
            {
                double sum = 0;
                for (int i = 0; i < count; i++) sum += profile.W[i] * e[i];
                offset = sum / profile.Area;
                break;
            }
            case BestFitSphereCriterion.MinimumVolume:
            {
                offset = e[0];
                for (int i = 1; i < count; i++)
                    offset = airSign > 0 ? Math.Min(offset, e[i]) : Math.Max(offset, e[i]);
                double volume = 0;
                for (int i = 0; i < count; i++) volume += profile.W[i] * airSign * (e[i] - offset);
                return (offset, volume);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(criterion), criterion, "Unknown best-fit-sphere criterion.");
        }
        double squares = 0;
        for (int i = 0; i < count; i++)
        {
            double d = e[i] - offset;
            squares += profile.W[i] * d * d;
        }
        return (offset, Math.Sqrt(squares / profile.Area));
    }

    /// <summary>The best-fit sphere of <paramref name="probe"/>'s y profile over
    /// [<paramref name="rMin"/>, <paramref name="rMax"/>] by <paramref name="criterion"/>.</summary>
    /// <param name="airSign">+1 when the air is on the −z side of the surface, −1 on the +z side.</param>
    /// <param name="where">Names the analysis and surface in error messages.</param>
    public static BestFitSphereFit Fit(SurfaceProbe probe, double rMin, double rMax, BestFitSphereCriterion criterion,
        int airSign, string where, CancellationToken cancellation = default)
    {
        var profile = Sample(probe, rMin, rMax, where);
        cancellation.ThrowIfCancellationRequested();
        double Objective(double c) => Evaluate(profile, criterion, airSign, c).Objective;

        // Scan the open interval |c| < 1/rMax, then refine in the neighbouring interval.
        double cMax = 1 / rMax * (1 - 1e-12);
        double step = 2 * cMax / (ScanPoints + 1);
        int best = -1;
        double bestValue = double.PositiveInfinity;
        var cs = new double[ScanPoints];
        for (int i = 0; i < ScanPoints; i++)
        {
            cs[i] = -cMax + (i + 1) * step;
            double v = Objective(cs[i]);
            if (v < bestValue) { bestValue = v; best = i; }
        }
        // The surface's own vertex curvature is a candidate too: for a sphere it is the answer.
        double vertex = probe.Surface.Curvature;
        double startC = best >= 0 ? cs[best] : double.NaN;
        if (Math.Abs(vertex) < cMax && Objective(vertex) is var vv && vv < bestValue) { bestValue = vv; startC = vertex; }
        if (!double.IsFinite(bestValue))
            throw new InvalidOperationException($"{where}: no sphere spanning the fit range gives a finite {criterion} objective.");
        cancellation.ThrowIfCancellationRequested();

        double a = Math.Max(-cMax, startC - step), b = Math.Min(cMax, startC + step);
        double c = startC, value = bestValue;
        if (a < b)
        {
            var (x, fx) = BrentMinimizer.Minimize(Objective, a, b, relativeTolerance: 1e-10, absoluteTolerance: 1e-14 / rMax);
            if (fx <= value) { c = x; value = fx; }
        }
        if (criterion != BestFitSphereCriterion.MinimumVolume)
            (c, value) = GaussNewton(profile, criterion, c, value, cMax);
        cancellation.ThrowIfCancellationRequested();

        var (offset, objective) = Evaluate(profile, criterion, airSign, c);
        double maxDepth = double.NaN, rmsDepth = double.NaN, volume = double.NaN;
        if (criterion == BestFitSphereCriterion.MinimumVolume)
        {
            maxDepth = 0;
            double squares = 0;
            for (int i = 0; i < profile.R.Count; i++)
            {
                double depth = airSign * (profile.Z[i] - SphereSag(c, profile.R[i]) - offset);
                maxDepth = Math.Max(maxDepth, depth);
                squares += profile.W[i] * depth * depth;
            }
            rmsDepth = Math.Sqrt(squares / profile.Area);
            volume = objective;
        }

        double maxSlope = 0, slopeSquares = 0;
        for (int k = 0; k < SlopePoints; k++)
        {
            double y = k == SlopePoints - 1 ? rMax : rMin + k * (rMax - rMin) / (SlopePoints - 1);
            if (!probe.TrySlope(0, y, out _, out _, out double zy))
                throw new ArgumentException($"{where}: the slope is not defined at y = {y:g6} m, inside the fit range.");
            double delta = zy - SphereSlope(c, y);
            maxSlope = Math.Max(maxSlope, Math.Abs(delta));
            slopeSquares += delta * delta;
        }
        return new BestFitSphereFit
        {
            Criterion = criterion, Curvature = c, VertexOffset = offset, MinRadius = rMin, MaxRadius = rMax, AirSign = airSign,
            Objective = objective, MaxDepthToRemove = maxDepth, RmsDepthToRemove = rmsDepth, VolumeToRemove = volume,
            MaxSlopeDeviation = maxSlope, RmsSlopeDeviation = Math.Sqrt(slopeSquares / SlopePoints),
        };
    }

    /// <summary>Gauss–Newton on c for the RMS criteria (the offset, when free, eliminated by its
    /// weighted mean); a step is kept only when it lowers the objective.</summary>
    private static (double C, double Value) GaussNewton(Profile p, BestFitSphereCriterion criterion, double c, double value, double cMax)
    {
        int count = p.R.Count;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var e = new double[count];
            var j = new double[count];
            for (int i = 0; i < count; i++)
            {
                double r = p.R[i], phi2 = 1 - c * c * r * r;
                if (!(phi2 > 0)) return (c, value);
                double phi = Math.Sqrt(phi2);
                e[i] = p.Z[i] - SphereSag(c, r);
                j[i] = r * r / (phi * (1 + phi));
            }
            if (criterion == BestFitSphereCriterion.MinimumRmsWithOffset)
            {
                double me = 0, mj = 0;
                for (int i = 0; i < count; i++) { me += p.W[i] * e[i]; mj += p.W[i] * j[i]; }
                me /= p.Area; mj /= p.Area;
                for (int i = 0; i < count; i++) { e[i] -= me; j[i] -= mj; }
            }
            double num = 0, den = 0;
            for (int i = 0; i < count; i++) { num += p.W[i] * j[i] * e[i]; den += p.W[i] * j[i] * j[i]; }
            if (!(den > 0)) return (c, value);
            double next = c + num / den;
            if (!(Math.Abs(next) < cMax)) return (c, value);
            double nextValue = Evaluate(p, criterion, 1, next).Objective;
            if (!(nextValue < value)) return (c, value);
            c = next;
            value = nextValue;
        }
        return (c, value);
    }

    /// <summary>
    /// Which side of surface <paramref name="j"/> is air, as +1 (air at −z: "+z goes from air
    /// to glass") or −1, at the primary wavelength: the side with the lower index (relative to
    /// the system's air) is air; for a mirror the side the light arrives from. The light
    /// travels +z after an even number of mirrors and −z after an odd number (§3). When both
    /// sides have the same index the side cannot be told, +1 is assumed and
    /// <paramref name="note"/> says so. <paramref name="reverse"/> (OpticStudio's "Reverse
    /// Direction") takes the other side.
    /// </summary>
    public static int AirSign(SequentialLens lens, OpticalSystemData system, int j, bool reverse, out string note)
    {
        ArgumentNullException.ThrowIfNull(lens);
        var indices = SequentialTrace.Create(lens, system).Indices;
        int mirrors = 0;
        for (int i = 1; i < j; i++)
            if (lens.Surfaces[i].IsMirror && !lens.Surfaces[i].IsIgnored) mirrors++;
        int arriving = mirrors % 2 == 0 ? +1 : -1; // +1: the light arrives from −z.
        var s = lens.Surfaces[j];
        double before = indices[j - 1], after = indices[j];
        int sign;
        if (s.IsMirror)
        {
            sign = arriving;
            note = "Air side: the side the light arrives from (a mirror).";
        }
        else if (before < after)
        {
            sign = arriving;
            note = $"Air side: the side the light arrives from (index {before:g6} before, {after:g6} after, primary wavelength).";
        }
        else if (before > after)
        {
            sign = -arriving;
            note = $"Air side: the side the light leaves into (index {before:g6} before, {after:g6} after, primary wavelength).";
        }
        else
        {
            sign = +1;
            note = $"Air side: both sides have index {before:g6}, so the side cannot be told; +z is assumed to go from air to glass.";
        }
        if (reverse)
        {
            sign = -sign;
            note += " Reverse direction is on, so the other side is taken.";
        }
        return sign;
    }
}
