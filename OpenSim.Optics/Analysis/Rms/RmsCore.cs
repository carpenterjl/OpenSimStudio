using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

// OP-40: what the four RMS analyses share.

/// <summary>What an RMS analysis plots (OpticStudio's "Data").</summary>
public enum RmsData
{
    /// <summary>RMS wavefront error, waves.</summary>
    WavefrontError = 0,
    /// <summary>RMS radial spot radius, µm.</summary>
    SpotRadius = 1,
    /// <summary>RMS spot size in x, µm.</summary>
    SpotX = 2,
    /// <summary>RMS spot size in y, µm.</summary>
    SpotY = 3,
    /// <summary>The Strehl ratio: the FFT PSF's peak over the unaberrated peak, by OP-38's engine
    /// (<see cref="PsfEngine.Strehl"/>; docs/Optics-Conventions.md §33, §34).</summary>
    StrehlRatio = 4,
}

/// <summary>What the RMS Field Map plots: the <see cref="RmsData"/> choices and, with the
/// rectangular array, the wavefront's peak to valley.</summary>
public enum RmsMapData
{
    WavefrontError = 0,
    SpotRadius = 1,
    SpotX = 2,
    SpotY = 3,
    /// <summary>Peak to valley of the wavefront over the rectangular array's rays, waves (the
    /// help's "rough estimate of the PTV"); one wavelength only.</summary>
    WavefrontPeakToValley = 4,
    /// <summary>The Strehl ratio, as <see cref="RmsData.StrehlRatio"/>.</summary>
    StrehlRatio = 5,
}

/// <summary>The point an RMS is measured about (OpticStudio's "Refer To").</summary>
public enum RmsReference
{
    /// <summary>Spot: the chief ray's image point. Wavefront: piston removed.</summary>
    ChiefRay = 0,
    /// <summary>Spot: the weighted centroid. Wavefront: piston and tilt removed.</summary>
    Centroid = 1,
}

/// <summary>Which curves an analysis with every wavelength shows (OpticStudio's "All", "Poly
/// Only" and "All With No Poly").</summary>
public enum RmsWavelengthCurves
{
    MonochromaticAndPolychromatic = 0,
    PolychromaticOnly = 1,
    MonochromaticOnly = 2,
}

/// <summary>The quantity one RMS evaluation returns.</summary>
internal enum RmsQuantity
{
    WavefrontRms,
    WavefrontPeakToValley,
    SpotRadius,
    SpotX,
    SpotY,
    /// <summary>The Strehl ratio by OP-38's PSF engine (not an RMS over the quadrature).</summary>
    Strehl,
}

/// <summary>One RMS value (waves, meters for a spot, a pure number for the Strehl ratio), or why
/// there is none.</summary>
internal sealed record RmsValue(double Value, string? Failure, int Launched, int Used, IReadOnlyList<string> RayFailures)
{
    public bool Succeeded => Failure is null;

    /// <summary>Warnings about the value, each naming the point (the Strehl ratio's PSF sampling
    /// and validity warnings); empty for the RMS quantities.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>One point to evaluate: a field position in the field type's unit (degrees or
/// meters), the wavelength rows (0-based, the system's table) or one wavelength not in the
/// table, polychromatic or not, and a focus shift (meters, added to the thickness before the
/// image surface).</summary>
internal sealed record RmsPoint
{
    public required double FieldX { get; init; }
    public required double FieldY { get; init; }
    public IReadOnlyList<int> Rows { get; init; } = Array.Empty<int>();
    public double? Micrometers { get; init; }
    public bool Polychromatic { get; init; }
    public double Focus { get; init; }
    /// <summary>What the point is, for messages ("field 0.5°, 0.55 µm").</summary>
    public required string Label { get; init; }
}

/// <summary>The lens and system an RMS analysis evaluates, prepared once.</summary>
internal sealed class RmsSetup
{
    public required SequentialLens Lens { get; init; }
    public required OpticalSystemData System { get; init; }
    public required RayLauncher Launcher { get; init; }
    public required List<string> Notes { get; init; }
    public bool ReplacedVignetting { get; init; }

    public LensUnit Unit => System.Units.LensUnit;

    /// <summary>A field value (degrees or meters) as shown: degrees, or lens units.</summary>
    public double FieldShown(double value) =>
        FieldTypes.IsAngle(System.Fields.Type) ? value : OpticsConventions.FromMeters(value, Unit);

    /// <summary>A shown field value back in degrees or meters.</summary>
    public double FieldFromShown(double shown) =>
        FieldTypes.IsAngle(System.Fields.Type) ? shown : OpticsConventions.ToMeters(shown, Unit);

    /// <summary>"°" or the lens unit.</summary>
    public string FieldUnit => FieldTypes.IsAngle(System.Fields.Type) ? "°" : ParaxialReport.UnitName(Unit);
}

/// <summary>
/// The RMS spot radius and RMS wavefront error of one field, wavelength set and focus over a
/// <see cref="PupilQuadratureRule"/> (OP-40; docs/Optics-Conventions.md §34).
/// <para>
/// <b>Any field, any wavelength.</b> OpticStudio samples field and wavelength between the table's
/// values. Here the point's field is appended to the field table as one more field point (with
/// the vignetting factors the launcher applies at that field, or none), and a wavelength not in
/// the table is appended to the wavelength table, so that <see cref="OpdEngine"/> traces it like
/// any table row. The appended point changes nothing else: the primary wavelength stays the
/// primary, and floating semi-diameters are frozen at their values for the system as defined
/// (<see cref="Prepare"/>), so the appended field cannot widen an aperture.
/// </para>
/// <para>
/// <b>Focus</b> (help, "RMS vs. Focus"): "OpticStudio adds the specified focus shift to the value
/// of the thickness of the surface prior to the image surface"; so is it here.
/// </para>
/// <para>
/// <b>Spot</b> (help, "RMS vs. Field"; OP-34's definitions, §25): the weighted RMS of the rays'
/// image-surface positions about the reference point, radial, x or y. The chief ray reference is
/// the chief ray of the wavelength computed, or of the primary wavelength when polychromatic ("For
/// polychromatic calculations, the primary wavelength is used for reference"); the centroid is the
/// weighted mean position.
/// </para>
/// <para>
/// <b>Wavefront</b> (help, "RMS vs. Field"): the OPD against the reference sphere centred on the
/// chief ray (<see cref="OpdEngine"/>), its weighted mean removed ("Both reference points subtract
/// out wavefront piston"); the centroid reference "also subtracts out the tilt of the wavefront",
/// done here as the weighted least-squares fit of a + b·Px + c·Py removed (this program's form of
/// the help's "mean shifted, tilted reference sphere"; Rimmer's correction, which the help cites,
/// was not available and is not built). Polychromatic: "the RMS polychromatic is done for all
/// wavelengths at the same time and for all the pupil", every wavelength referred to the primary
/// wavelength's reference sphere, each ray weighted by its pupil weight times its wavelength's
/// weight, the OPD in waves of the primary wavelength.
/// </para>
/// <para>
/// <b>Failed rays.</b> Gaussian quadrature assumes the whole pupil is traced (help, "Gaussian
/// Quadrature": "the algorithm is not accurate if some of the rays are clipped"): a point where any
/// ray fails has no value, and the failure names the rays. The rectangular array leaves failed rays
/// out ("Rays outside the circular entrance pupil are ignored"; vignetted rays likewise) and counts
/// them.
/// </para>
/// <para>
/// <b>Strehl ratio</b>: OP-38's PSF-peak ratio at the same appended field, wavelength and focus
/// (<see cref="EvaluateStrehl"/>); the quadrature rule is not used for it.
/// </para>
/// </summary>
internal static class RmsCore
{
    /// <summary>The help's diffraction limit of RMS wavefront ("for RMS wavefront, 0.072 waves is
    /// used"; "RMS vs. Field", "Show Diffraction Limit").</summary>
    public const double WavefrontDiffractionLimitWaves = 0.072;

    /// <summary>The help's diffraction limit of the Strehl ratio ("For Strehl ratio, 0.8 is used";
    /// "RMS vs. Field", "Show Diffraction Limit").</summary>
    public const double StrehlDiffractionLimit = 0.8;

    /// <summary>The default pupil sampling of the Strehl ratio: the default of OP-38's Strehl ratio
    /// analysis (<see cref="StrehlRatioSettings.Sampling"/>).</summary>
    public const int DefaultPsfSampling = 64;

    /// <summary>The lens and system ready for evaluation: afocal refused, the vignetting factors
    /// replaced by apertures when asked, and floating semi-diameters frozen.</summary>
    public static RmsSetup Prepare(SequentialLens lens, OpticalSystemData system, bool removeVignettingFactors, string who,
        List<string> notes, CancellationToken cancellation)
    {
        FieldScanCommon.RefuseAfocal(system, who);
        bool replaced = false;
        if (removeVignettingFactors && system.Fields.Points.Any(p => p.IsVignetted))
        {
            var r = VignettingTools.ReplaceWithApertures(lens, system, cancellation: cancellation);
            lens = r.Lens;
            system = r.System;
            replaced = true;
            notes.AddRange(r.Notes);
        }
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
        {
            var clear = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
            int stop = lens.StopIndex;
            lens = lens with
            {
                Surfaces = lens.Surfaces.Select((s, j) => s.Aperture is FloatingAperture && s.SemiDiameter is null && j != stop
                    ? s with { SemiDiameter = clear[j] } : s).ToArray(),
            };
            notes.Add("Floating apertures keep the semi-diameters sized for the system's own fields and wavelengths; the sampled fields do not resize them.");
        }
        return new RmsSetup
        {
            Lens = lens, System = system, Launcher = new RayLauncher(lens, system), Notes = notes, ReplacedVignetting = replaced,
        };
    }

    /// <summary>The system with <paramref name="point"/>'s field (and wavelength, when not in
    /// the table) appended; the 0-based field row and wavelength rows to trace.</summary>
    internal static (OpticalSystemData System, int Field, int[] Rows) Extended(RmsSetup setup, RmsPoint point)
    {
        var system = setup.System;
        var factors = default(VignettingFactors);
        if (system.Fields.Points.Any(p => p.IsVignetted))
        {
            var (hx, hy) = OpticsConventions.NormalizeField((point.FieldX, point.FieldY), system.Fields.Scale);
            factors = setup.Launcher.VignettingAt(hx, hy);
        }
        var field = new FieldPoint
        {
            X = point.FieldX, Y = point.FieldY, Weight = 1,
            DecenterX = factors.DecenterX, DecenterY = factors.DecenterY,
            CompressionX = factors.CompressionX, CompressionY = factors.CompressionY, TiltDegrees = factors.TiltDegrees,
        };
        var extended = system with { Fields = system.Fields with { Points = system.Fields.Points.Append(field).ToArray() } };
        int[] rows;
        if (point.Micrometers is double um)
        {
            extended = extended with
            {
                Wavelengths = extended.Wavelengths with
                {
                    Wavelengths = extended.Wavelengths.Wavelengths.Append(new Wavelength { Micrometers = um, Weight = 1 }).ToArray(),
                },
            };
            rows = new[] { extended.Wavelengths.Wavelengths.Count - 1 };
        }
        else rows = point.Rows.ToArray();
        return (extended, extended.Fields.Points.Count - 1, rows);
    }

    /// <summary>The lens with <paramref name="focus"/> meters added to the thickness before the image.</summary>
    internal static SequentialLens Focused(SequentialLens lens, double focus)
    {
        if (focus == 0) return lens;
        int last = lens.LastSurfaceIndex;
        return lens with
        {
            Surfaces = lens.Surfaces.Select((s, j) => j == last ? s with { Thickness = s.Thickness + focus } : s).ToArray(),
        };
    }

    /// <summary>Evaluates many points, one slot each, the same bits at any degree of parallelism
    /// (a Strehl ratio's PSF transforms run on one thread inside its slot).</summary>
    public static RmsValue[] EvaluateAll(RmsSetup setup, IReadOnlyList<RmsPoint> points, RmsQuantity quantity, RmsReference reference,
        PupilQuadratureRule rule, CancellationToken cancellation, int? maxDegreeOfParallelism = null, int psfSampling = DefaultPsfSampling) =>
        ParallelSlots.Compute(points.Count, maxDegreeOfParallelism,
            i => Evaluate(setup, points[i], quantity, reference, rule, cancellation, psfSampling), cancellation);

    /// <summary>The RMS (or P-V, or the Strehl ratio, <see cref="EvaluateStrehl"/>) of one point;
    /// see the class remarks.</summary>
    public static RmsValue Evaluate(RmsSetup setup, RmsPoint point, RmsQuantity quantity, RmsReference reference,
        PupilQuadratureRule rule, CancellationToken cancellation, int psfSampling = DefaultPsfSampling)
    {
        cancellation.ThrowIfCancellationRequested();
        if (quantity == RmsQuantity.Strehl) return EvaluateStrehl(setup, point, psfSampling, cancellation);
        var (system, field, rows) = Extended(setup, point);
        var lens = Focused(setup.Lens, point.Focus);
        bool wavefront = quantity is RmsQuantity.WavefrontRms or RmsQuantity.WavefrontPeakToValley;
        int primary = system.Wavelengths.PrimaryIndex;
        var options = new OpdOptions
        {
            ReferencePoint = OpdReferencePoint.ChiefRay,
            Reference = OpdReference.ExitPupil,
            ReferenceToPrimary = point.Polychromatic && wavefront,
            AllowOpdLimitations = !wavefront,
        };

        var results = new List<(OpdResult Result, double WaveWeight)>();
        OpdResult? primaryResult = null;
        try
        {
            foreach (int row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                double waveWeight = point.Polychromatic ? system.Wavelengths.Wavelengths[row].Weight : 1;
                bool needed = waveWeight > 0;
                if (!needed && !(point.Polychromatic && row == primary)) continue;
                var r = OpdEngine.Compute(lens, system, field, row, rule.Points, options, cancellation);
                if (row == primary) primaryResult = r;
                if (needed) results.Add((r, waveWeight));
            }
            if (point.Polychromatic && primaryResult is null)
                primaryResult = OpdEngine.Compute(lens, system, field, primary, new[] { (0.0, 0.0) }, options, cancellation);
        }
        catch (InvalidOperationException e)
        {
            return new RmsValue(double.NaN, $"{point.Label}: {e.Message}", 0, 0, Array.Empty<string>());
        }
        if (results.Count == 0)
            return new RmsValue(double.NaN, $"{point.Label}: every wavelength has weight zero, so there is nothing to average.", 0, 0, Array.Empty<string>());

        // ---- Gather the rays ----
        var px = new List<double>();
        var py = new List<double>();
        var weights = new List<double>();
        var values = new List<double>();
        var xs = new List<double>();
        var ys = new List<double>();
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int launched = 0;
        double primaryWave = primaryResult is null ? double.NaN : OpticsConventions.MicrometersToMeters(primaryResult.AirMicrometers);
        foreach (var (result, waveWeight) in results)
        {
            for (int i = 0; i < result.Samples.Count; i++)
            {
                launched++;
                var s = result.Samples[i];
                bool ok = wavefront ? s.IsValid : s.Ray.IsOk;
                if (!ok)
                {
                    string why = wavefront ? s.Failure! : $"{RayStatuses.Label(s.Ray.Status)} at surface {s.Ray.FailedAtSurface}";
                    failures[why] = failures.TryGetValue(why, out int c) ? c + 1 : 1;
                    continue;
                }
                px.Add(s.Px);
                py.Add(s.Py);
                weights.Add(rule.Weights[i] * waveWeight);
                if (wavefront) values.Add(point.Polychromatic ? s.Path / primaryWave : s.Waves);
                else
                {
                    xs.Add(s.Ray.Position.X);
                    ys.Add(s.Ray.Position.Y);
                }
            }
        }
        var rayFailures = failures.Select(f => $"{f.Value} {f.Key.ToLowerInvariant()}").ToArray();
        int used = weights.Count;
        if (rule.Method == PupilIntegration.GaussianQuadrature && rayFailures.Length > 0)
            return new RmsValue(double.NaN,
                $"{point.Label}: Gaussian quadrature needs every ray of the pupil, but {launched - used} of {launched} did not give a value ({string.Join("; ", rayFailures)}); use the rectangular array for a vignetted pupil.",
                launched, used, rayFailures);
        if (used == 0)
            return new RmsValue(double.NaN, $"{point.Label}: no ray gave a value ({string.Join("; ", rayFailures)}).", launched, 0, rayFailures);

        double value;
        if (wavefront)
        {
            var residual = Residual(px, py, weights, values, reference, point.Label);
            if (quantity == RmsQuantity.WavefrontPeakToValley) value = residual.Max() - residual.Min();
            else
            {
                double sw = 0, s2 = 0;
                for (int i = 0; i < used; i++) { sw += weights[i]; s2 += weights[i] * residual[i] * residual[i]; }
                value = Math.Sqrt(s2 / sw);
            }
        }
        else
        {
            var chiefResult = point.Polychromatic ? primaryResult! : results[0].Result;
            var chief = (chiefResult.ChiefRay.Position.X, chiefResult.ChiefRay.Position.Y);
            var spot = new SpotPoint[used];
            for (int i = 0; i < used; i++) spot[i] = new SpotPoint(xs[i], ys[i], weights[i]);
            var stats = SpotStatistics.Compute(spot, reference == RmsReference.ChiefRay ? SpotReference.ChiefRay : SpotReference.Centroid, chief);
            value = quantity switch
            {
                RmsQuantity.SpotRadius => stats.Rms,
                RmsQuantity.SpotX => stats.RmsX,
                RmsQuantity.SpotY => stats.RmsY,
                _ => throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Unknown RMS quantity."),
            };
        }
        return new RmsValue(value, null, launched, used, rayFailures);
    }

    /// <summary>
    /// The Strehl ratio of one point, computed as OP-38's Strehl ratio analysis computes it
    /// (<see cref="StrehlRatioAnalysis"/>): each wavelength's pupil function
    /// (<see cref="PsfEngine.BuildPupil"/> with <see cref="PsfRuns.Options"/>: apertures checked,
    /// the OPD against the exit-pupil sphere on the chief ray, referred to the primary wavelength's
    /// chief ray when polychromatic), then <see cref="PsfEngine.Strehl"/> for one wavelength or
    /// <see cref="PsfRuns.PolychromaticStrehl"/> (OP-38's polychromatic FFT PSF and its peak) for
    /// several. The help defines the ratio as "the peak intensity of the diffraction point spread
    /// function (PSF) divided by the peak intensity of the diffraction point spread function (PSF)
    /// in the absence of aberrations" (help, "Strehl Ratio"). A ray that fails carries no light
    /// (OP-38's rule), so the point keeps a value and the failure is a warning. The sampling check
    /// (<see cref="PsfEngine.CheckSampling"/>) and the pupil's own warnings are returned, each
    /// naming the point. The transforms run on one thread: the caller's slots are the parallelism,
    /// and the engine's transforms are bitwise the same at any thread count.
    /// </summary>
    internal static RmsValue EvaluateStrehl(RmsSetup setup, RmsPoint point, int sampling, CancellationToken cancellation)
    {
        var (system, field, rows) = Extended(setup, point);
        var lens = Focused(setup.Lens, point.Focus);
        var options = PsfRuns.Options(point.Polychromatic, checkApertures: true, allow: false);
        var pupils = new List<PupilFunction>();
        var warnings = new List<string>();
        int launched = 0, used = 0;
        double value;
        try
        {
            foreach (int row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var p = PsfEngine.BuildPupil(lens, system, field, row, sampling, options, cancellation);
                pupils.Add(p);
                string label = point.Polychromatic ? $"{point.Label}, {StudyReport.Number(p.Wavelength * 1e6, "g6")} µm: " : $"{point.Label}: ";
                warnings.AddRange(PsfEngine.CheckSampling(p).Warnings.Select(w => label + w));
                warnings.AddRange(p.Warnings.Select(w => label + w));
                if (p.Opd is { } opd)
                {
                    launched += opd.Samples.Count;
                    used += opd.Samples.Count(s => s.IsValid);
                }
            }
            if (point.Polychromatic)
            {
                var weights = rows.Select(r => system.Wavelengths.Wavelengths[r].Weight).ToArray();
                value = PsfRuns.PolychromaticStrehl(pupils, weights, 1, cancellation);
            }
            else value = PsfEngine.Strehl(pupils[0], PsfEngine.DefaultPadding, 1, cancellation);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return new RmsValue(double.NaN, $"{point.Label}: {e.Message}", launched, used, Array.Empty<string>()) { Warnings = warnings };
        }
        if (value < 0.1)
            warnings.Add($"{point.Label}: Strehl ratio {StudyReport.Number(value, "g3")} is below about 0.1, where the help says the ratio \"is not useful\" (help, \"Strehl Ratio\").");
        return new RmsValue(value, null, launched, used, Array.Empty<string>()) { Warnings = warnings };
    }

    /// <summary>The OPD with the weighted mean removed (chief ray) or the weighted least-squares
    /// plane a + b·Px + c·Py removed (centroid).</summary>
    internal static double[] Residual(IReadOnlyList<double> px, IReadOnlyList<double> py, IReadOnlyList<double> w,
        IReadOnlyList<double> v, RmsReference reference, string label)
    {
        int n = v.Count;
        var r = new double[n];
        if (reference == RmsReference.ChiefRay)
        {
            double sw = 0, sv = 0;
            for (int i = 0; i < n; i++) { sw += w[i]; sv += w[i] * v[i]; }
            double mean = sv / sw;
            for (int i = 0; i < n; i++) r[i] = v[i] - mean;
            return r;
        }
        // Normal equations of the weighted fit, basis (1, Px, Py).
        var a = new double[3, 3];
        var b = new double[3];
        for (int i = 0; i < n; i++)
        {
            double[] phi = { 1, px[i], py[i] };
            for (int j = 0; j < 3; j++)
            {
                b[j] += w[i] * phi[j] * v[i];
                for (int k = 0; k < 3; k++) a[j, k] += w[i] * phi[j] * phi[k];
            }
        }
        var c = Solve3(a, b) ?? throw new InvalidOperationException(
            $"{label}: the pupil points that gave a value do not span a plane, so the wavefront tilt cannot be fitted for the centroid reference.");
        for (int i = 0; i < n; i++) r[i] = v[i] - (c[0] + c[1] * px[i] + c[2] * py[i]);
        return r;
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        double scale = 0;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) scale = Math.Max(scale, Math.Abs(m[i, j]));
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (!(Math.Abs(m[pivot, col]) > 1e-12 * scale)) return null;
            if (pivot != col)
            {
                for (int k = 0; k < 3; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                (y[col], y[pivot]) = (y[pivot], y[col]);
            }
            for (int row = col + 1; row < 3; row++)
            {
                double f = m[row, col] / m[col, col];
                for (int k = col; k < 3; k++) m[row, k] -= f * m[col, k];
                y[row] -= f * y[col];
            }
        }
        var x = new double[3];
        for (int row = 2; row >= 0; row--)
        {
            double s = y[row];
            for (int k = row + 1; k < 3; k++) s -= m[row, k] * x[k];
            x[row] = s / m[row, row];
        }
        return x;
    }

    /// <summary>The help's diffraction limit of an RMS spot: "1.22 times the working F/# on axis
    /// times the wavelength" (meters), the working F/# of the on-axis field (0, 0) at that
    /// wavelength (<see cref="RealWorkingFNumber"/>); NaN and a note when it cannot be found.</summary>
    public static double SpotDiffractionLimit(RmsSetup setup, int? row, double? micrometers, List<string> notes)
    {
        var point = new RmsPoint { FieldX = 0, FieldY = 0, Rows = row is int r ? new[] { r } : Array.Empty<int>(), Micrometers = micrometers, Label = "axis" };
        var (system, _, rows) = Extended(setup, point);
        var trace = SequentialTrace.Create(setup.Lens, system, rows[0]);
        var w = RealWorkingFNumber.Compute(trace, new RayLauncher(setup.Lens, system), 0, 0);
        if (!w.Succeeded)
        {
            notes.Add($"No diffraction limit at {StudyReport.Number(trace.AirMicrometers, "g6")} µm: {w.Failure}.");
            return double.NaN;
        }
        return RealWorkingFNumber.AiryRadius(OpticsConventions.MicrometersToMeters(trace.AirMicrometers), w.Value);
    }

    /// <summary>The RMS quantity of a data choice.</summary>
    public static RmsQuantity Quantity(RmsData data) => data switch
    {
        RmsData.WavefrontError => RmsQuantity.WavefrontRms,
        RmsData.SpotRadius => RmsQuantity.SpotRadius,
        RmsData.SpotX => RmsQuantity.SpotX,
        RmsData.SpotY => RmsQuantity.SpotY,
        RmsData.StrehlRatio => RmsQuantity.Strehl,
        _ => throw new ArgumentOutOfRangeException(nameof(data), data, "Unknown RMS data."),
    };

    /// <summary>The Strehl ratio's pupil sampling: OP-38's Strehl analysis range, 16 to 1024
    /// (checked only when the Strehl ratio is plotted).</summary>
    public static void CheckPsfSampling(RmsQuantity q, int sampling, string who)
    {
        if (q == RmsQuantity.Strehl && (sampling < 16 || sampling > 1024))
            throw new ArgumentException($"{who}: the PSF sampling of the Strehl ratio must be 16 to 1024 (it is {sampling}).");
    }

    /// <summary>The flat diffraction-limit line of a quantity that has one (help, "Show Diffraction
    /// Limit"): 0.072 waves for the wavefront, 0.8 for the Strehl ratio; NaN for a spot, whose
    /// limit depends on the wavelength and F/#.</summary>
    public static double FlatDiffractionLimit(RmsQuantity q) => q switch
    {
        RmsQuantity.Strehl => StrehlDiffractionLimit,
        RmsQuantity.WavefrontRms => WavefrontDiffractionLimitWaves,
        _ => double.NaN,
    };

    public static bool IsSpot(RmsQuantity q) => q is RmsQuantity.SpotRadius or RmsQuantity.SpotX or RmsQuantity.SpotY;

    /// <summary>A value in the plotted unit: waves as they are, a spot in µm.</summary>
    public static double Shown(RmsQuantity q, double value) => IsSpot(q) ? value * 1e6 : value;

    /// <summary>"RMS wavefront error (waves)" and the like.</summary>
    public static string AxisLabel(RmsQuantity q) => q switch
    {
        RmsQuantity.WavefrontRms => "RMS wavefront error (waves)",
        RmsQuantity.WavefrontPeakToValley => "Wavefront peak to valley (waves)",
        RmsQuantity.SpotRadius => "RMS spot radius (µm)",
        RmsQuantity.SpotX => "RMS spot x (µm)",
        RmsQuantity.SpotY => "RMS spot y (µm)",
        RmsQuantity.Strehl => "Strehl ratio",
        _ => q.ToString(),
    };

    /// <summary>"µm", "waves", or "" for the Strehl ratio (a pure number).</summary>
    public static string Unit(RmsQuantity q) => IsSpot(q) ? "µm" : q == RmsQuantity.Strehl ? "" : "waves";

    /// <summary>" (µm)", " (waves)", or nothing for the Strehl ratio.</summary>
    public static string UnitSuffix(RmsQuantity q) => Unit(q).Length == 0 ? "" : $" ({Unit(q)})";

    /// <summary>A shown value with its unit: "0.5 waves", or "0.93" for the Strehl ratio.</summary>
    public static string WithUnit(RmsQuantity q, double shown, string format = "g8") =>
        Unit(q).Length == 0 ? StudyReport.Number(shown, format) : $"{StudyReport.Number(shown, format)} {Unit(q)}";

    /// <summary>The density rules: Gaussian quadrature 1 to 20 rings, the rectangular array at least 2 across.</summary>
    public static PupilQuadratureRule Rule(PupilIntegration method, int density, string who)
    {
        if (method == PupilIntegration.GaussianQuadrature && (density < 1 || density > PupilQuadrature.MaximumRings))
            throw new ArgumentException($"{who}: with Gaussian quadrature the ray density (rings) must lie in 1–{PupilQuadrature.MaximumRings} (it is {density}).");
        if (method == PupilIntegration.RectangularArray && (density < 2 || density > 1001))
            throw new ArgumentException($"{who}: with the rectangular array the ray density (grid size) must lie in 2–1001 (it is {density}).");
        return PupilQuadrature.For(method, density);
    }

    /// <summary>The assumptions every RMS analysis states.</summary>
    public static List<string> Assumptions(RmsSetup setup, RmsQuantity q, RmsReference reference, PupilQuadratureRule rule,
        int psfSampling = DefaultPsfSampling)
    {
        if (q == RmsQuantity.Strehl) return StrehlAssumptions(setup, psfSampling);
        var lines = new List<string> { rule.Describe() };
        if (IsSpot(q))
            lines.Add(reference == RmsReference.ChiefRay
                ? "Spot RMS about the chief ray's image point (the primary wavelength's when polychromatic), weighted by the pupil weights times the wavelength weights (polychromatic); positions on the image surface in its local frame (docs/Optics-Conventions.md §25, §34)."
                : "Spot RMS about the weighted centroid of the rays, weighted by the pupil weights times the wavelength weights (polychromatic) (docs/Optics-Conventions.md §25, §34).");
        else
            lines.Add((reference == RmsReference.ChiefRay
                    ? "Wavefront against the reference sphere centred on the chief ray's image point and through the exit pupil, weighted mean (piston) removed"
                    : "Wavefront against the reference sphere centred on the chief ray's image point, with the weighted least-squares piston and tilt (a + b·Px + c·Py) removed (Rimmer's correction, which OpticStudio cites, is not built)")
                + "; polychromatic: every wavelength referred to the primary wavelength's sphere, one mean (and tilt) over all rays, in waves of the primary wavelength (OpticStudio 2025 R2 help, \"RMS vs. Field\"; docs/Optics-Conventions.md §26, §34).");
        lines.Add(rule.Method == PupilIntegration.GaussianQuadrature
            ? "Gaussian quadrature assumes the whole pupil is traced: a point where any ray fails or is vignetted has no value and is listed."
            : "Rectangular array: rays that fail or are vignetted are left out and counted.");
        lines.Add(VignettingNote(setup));
        lines.Add(RayAimingNote(setup));
        if (setup.System.Aperture.Apodization != ApodizationType.Uniform)
            lines.Add($"Pupil apodization ({ApodizationTypes.Names.Label(setup.System.Aperture.Apodization)}) is NOT applied: the pupil is weighted uniformly.");
        lines.Add("Polarization is not considered (Use Polarization is not built).");
        lines.Add(AppendedRowNote);
        return lines;
    }

    private const string AppendedRowNote = "A sampled field or wavelength not in the tables is traced as one appended table row; the primary wavelength is unchanged, and so is the field normalisation unless the sampled field lies beyond the largest defined field (the ray goes to the same field position either way).";

    private static string VignettingNote(RmsSetup setup) =>
        setup.ReplacedVignetting ? "The fields' vignetting factors were replaced by surface apertures before tracing (docs/Optics-Conventions.md §16)."
        : setup.System.Fields.Points.Any(p => p.IsVignetted) ? "The vignetting factors were kept; a sampled field between table fields takes the launcher's interpolated factors."
        : "No field has vignetting factors.";

    private static string RayAimingNote(RmsSetup setup) => setup.System.RayAiming.IsOn
        ? $"Ray aiming {RayAimingModes.Names.Label(setup.System.RayAiming.Mode).ToLowerInvariant()}: pupil coordinates are on the stop (§16)."
        : "Ray aiming is off: pupil coordinates are on the paraxial entrance pupil of the primary wavelength.";

    /// <summary>The assumptions of the Strehl ratio (<see cref="EvaluateStrehl"/>).</summary>
    private static List<string> StrehlAssumptions(RmsSetup setup, int sampling)
    {
        var lines = new List<string>
        {
            $"Strehl ratio: the FFT PSF's peak (refined off the grid) over the unaberrated PSF's peak, the same pupil amplitude with no OPD (help, \"Strehl Ratio\"), computed by OP-38's PSF engine as the Strehl ratio analysis computes it, on a {sampling} × {sampling} pupil zero-padded to {2 * sampling} × {2 * sampling}; OPD against the exit-pupil reference sphere centred on the chief ray, surface apertures checked (docs/Optics-Conventions.md §33, §34).",
            "OpticStudio's RMS pages compute the Strehl ratio \"using the method described in \"Strehl ratio approximation\"\", a section its online help does not contain; the PSF-peak definition of its \"Strehl Ratio\" page is used here, so values may differ from OpticStudio's RMS plots.",
            "The method, ray density and Refer to settings do not apply to the Strehl ratio: the peak is searched for wherever it lies in the PSF window, so the wavefront's piston and tilt do not change it.",
            "Polychromatic (OP-38's polychromatic FFT PSF): each wavelength referred to the primary wavelength's chief ray, sampled on the shortest wavelength's grid and weighted by its weight × (λ_min/λ)² over its own unaberrated peak. OpticStudio's RMS help offers the Strehl ratio for one wavelength only and points to the Huygens PSF for a polychromatic one.",
            "Rays that fail or are clipped by an aperture carry no light (zero pupil amplitude) and are listed as warnings; the point keeps its value. The PSF sampling and validity warnings (aliasing, a fast cone, an oblique chief ray) are listed with the point they belong to, and so is a ratio below about 0.1.",
            VignettingNote(setup),
            RayAimingNote(setup),
        };
        if (setup.System.Aperture.Apodization != ApodizationType.Uniform)
            lines.Add($"Pupil apodization ({ApodizationTypes.Names.Label(setup.System.Aperture.Apodization)}) is applied as the pupil amplitude (OP-38).");
        lines.Add("Polarization is not considered (Use Polarization is not built).");
        lines.Add(AppendedRowNote);
        return lines;
    }

    /// <summary>A gap-aware text value.</summary>
    public static string Text(RmsQuantity q, RmsValue v) => v.Succeeded ? StudyReport.Number(Shown(q, v.Value), "g8") : "—";
}
