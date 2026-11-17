using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Polarization;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>What a reverse-radiance estimate traces (docs/Optics-Conventions.md §78).</summary>
public sealed record NscReverseRadianceOptions
{
    /// <summary>The detector object (Detector Rectangle or Detector Color), 1-based.</summary>
    public required int Detector { get; init; }
    /// <summary>The pixel column and row, 1-based from the detector's (−x, −y) corner.</summary>
    public required int PixelX { get; init; }
    public required int PixelY { get; init; }
    /// <summary>Backward rays launched per wavelength.</summary>
    public int Rays { get; init; } = 100_000;
    /// <summary>The sources whose light is counted (1-based); null for every source of the scene.</summary>
    public IReadOnlyList<int>? Sources { get; init; }
    /// <summary>The system wavelengths (µm in air) and weights, and the source unit: the sources'
    /// spectra are read from them as the forward trace reads them.</summary>
    public required NscSourceEmitOptions Emission { get; init; }
}

/// <summary>One wavelength's backward estimate: the pixel flux, its standard error, the source
/// power counted at that wavelength and the rays traced.</summary>
public sealed record NscReverseRadianceRun(double WavelengthMicrometers, double Flux, double StandardError, double SourcePower, int Rays, long Contributing);

/// <summary>The backward estimate of one pixel's flux (sum over wavelengths) with its standard error.</summary>
public sealed record NscReverseRadianceResult
{
    public required double Flux { get; init; }
    public required double StandardError { get; init; }
    public required IReadOnlyList<NscReverseRadianceRun> Runs { get; init; }
    public required double PixelArea { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Backward tracing from a detector pixel by reciprocity (plan item OP-84;
/// docs/Optics-Conventions.md §78). OpticStudio's ReverseRadiance detector, target and analysis
/// have no page in the 2025 R2 public help, so the method here is the textbook one and its inputs
/// are this program's own.
/// <para>
/// The flux a pixel of area A collects is Φ = ∫_A ∫ L_in(x, ω) |cos θ| dω dA, L_in the radiance
/// arriving at x from direction ω. A backward ray starts at a point x uniform on the pixel
/// (pdf 1/A) along −ω with ω drawn cosine-weighted over the hemisphere the detector receives from
/// (pdf |cos θ|/π; both hemispheres with probability ½ each unless Front Only), so each ray is an
/// unbiased sample of A·π·s·L_in (s = 2 for two hemispheres, else 1). It is traced by the same
/// tracer as forward rays (geometric optics is reversible; Fresnel power transmittance, TIR,
/// mirrors and Beer–Lambert absorption are the same both ways), starting with intensity 1, so a
/// segment's intensity is the path's transmittance T. Where a segment crosses a source's emitting
/// face against the source's emission (the forward ray would leave the face along it), the source's
/// radiance there, L_s, contributes T·L_s·(n_d/n_s)² (radiance over n² is invariant along a ray;
/// n_d the index at the pixel, n_s at the crossing). Sources pass rays through (§72), so every
/// crossing on every segment of the tree counts, as every forward emission does.
/// </para>
/// <para>
/// <b>Source radiance.</b> Built for Source Ellipse and Source Rectangle in the cosine mode
/// (Cn &gt; 0): intensity ∝ cos^Cn θ over the forward hemisphere, power P spread uniformly over
/// the area S (the annulus's when Min half widths are set), so
/// L = P·(Cn + 1)·cos^(Cn−1) θ / (2π·S) (derived: P = ∫_S ∫ L cos θ dω dS with
/// ∫ cos^Cn θ dω = 2π/(Cn + 1)). P at each wavelength is the source's power times that line's weight
/// over the sum of its spectrum's weights (the expectation of the forward trace's wavelength
/// draw); lumens are converted to watts as the emitter does. Point and collimated sources have no
/// area or no solid angle and cannot be reached backwards; the Gaussian mode, arrays,
/// pre-propagation, reversed rays and every other source type are refused by name.
/// </para>
/// The standard error is the sample standard deviation of the per-ray samples over √N. Rays are
/// drawn from SplitMix64 stream g·N + k (g the wavelength, k the ray) of the seed and traced by
/// <see cref="NscTracer.Run(IReadOnlyList{NscLaunchRay}, NscRayRecorder?, CancellationToken)"/>, so
/// the estimate is the same to the bit at any thread count.
/// </summary>
public static class NscReverseRadiance
{
    public static NscReverseRadianceResult Estimate(NscScene scene, NscTraceSettings settings, NscReverseRadianceOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        const string who = "Reverse radiance";
        if (settings.ScatterRays)
            throw new NotSupportedException($"{who}: scattering in the backward trace is not built (importance sampling and Scatter To lists are not reciprocal); trace with scattering off.");
        // Wave 17 (OP-81): volume events are not reciprocal in this estimator either; refuse up front,
        // by name, rather than only when a backward ray happens to scatter.
        if (scene.Objects.Select((o, i) => (o, i)).Where(x => x.o.VolumePhysics is { Model: not Volume.NscVolumeModel.None }).Select(x => $"{x.i + 1} ({x.o.Geometry.TypeName})").ToList() is { Count: > 0 } volumes)
            throw new NotSupportedException($"{who}: object{(volumes.Count > 1 ? "s" : "")} {string.Join(", ", volumes)} {(volumes.Count > 1 ? "have" : "has")} volume physics; volume scattering and photoluminescence in the backward trace are not built. Set the Volume Physics model to None or use the forward trace.");
        if (options.Rays < 2) throw new ArgumentException($"{who}: at least 2 backward rays are needed for an error estimate (it is {options.Rays}).");
        int n = scene.Objects.Count;
        if (options.Detector < 1 || options.Detector > n || scene.Objects[options.Detector - 1].Geometry is not NscFlatDetector det)
            throw new ArgumentException($"{who}: object {options.Detector} is not a Detector Rectangle or Detector Color of the scene.");
        if (det.Mirroring != 0) throw new NotSupportedException($"{who}: detector object {options.Detector} mirrors its data (flag {det.Mirroring}); a mirrored pixel collects from more than its own area, which the backward launch does not model. Use 0.");
        if (det.PixelInterpolation) throw new NotSupportedException($"{who}: detector object {options.Detector} uses pixel interpolation, which shares each ray among neighbouring pixels; turn it off.");
        if (options.PixelX < 1 || options.PixelX > det.PixelsX || options.PixelY < 1 || options.PixelY > det.PixelsY)
            throw new ArgumentException($"{who}: pixel ({options.PixelX}, {options.PixelY}) is outside detector object {options.Detector}'s {det.PixelsX} × {det.PixelsY} pixels (counted from 1).");

        var placements = scene.GlobalPlacements();
        var sourceNumbers = options.Sources?.ToList() ?? Enumerable.Range(1, n).Where(i => scene.Objects[i - 1].Geometry is NscSource).ToList();
        if (sourceNumbers.Count == 0) throw new ArgumentException($"{who}: the scene has no source objects.");
        var notes = new List<string>();
        var sources = new List<Emitter>();
        foreach (int s in sourceNumbers)
            sources.Add(Emitter.For(scene, placements, s, settings, options.Emission, notes));

        var detPlacement = placements[options.Detector - 1];
        double dx = 2 * det.HalfWidthX / det.PixelsX, dy = 2 * det.HalfWidthY / det.PixelsY;
        double x0 = -det.HalfWidthX + (options.PixelX - 1) * dx, y0 = -det.HalfWidthY + (options.PixelY - 1) * dy;
        double area = dx * dy;
        double sides = det.FrontOnly ? 1 : 2;
        double weight = area * Math.PI * sides;
        int inside = NscScene.Resolve(scene.Objects[options.Detector - 1].InsideOf, options.Detector);
        double offset = settings.GlueDistance;

        var wavelengths = sources.SelectMany(e => e.Lines.Select(l => l.Micrometers)).Distinct().OrderBy(w => w).ToArray();
        var runs = new List<NscReverseRadianceRun>();
        NscLedgerBuilder.Sum total = default;
        double variance = 0;
        for (int g = 0; g < wavelengths.Length; g++)
        {
            cancellation.ThrowIfCancellationRequested();
            double wave = wavelengths[g];
            var rays = new NscLaunchRay[options.Rays];
            for (int k = 0; k < rays.Length; k++)
            {
                var rng = SplitMix64.ForStream(settings.Seed, (long)g * options.Rays + k);
                double x = x0 + rng.NextDouble() * dx, y = y0 + rng.NextDouble() * dy;
                double side = det.FrontOnly ? -1 : rng.NextDouble() < 0.5 ? 1 : -1;
                double c = Math.Sqrt(rng.NextDouble()), sn = Math.Sqrt(Math.Max(0, 1 - c * c)), phi = 2 * Math.PI * rng.NextDouble();
                var local = new Vector3D(sn * Math.Cos(phi), sn * Math.Sin(phi), side * c);
                var d = detPlacement.ApplyDirection(local);
                var p = detPlacement.Apply(new Vector3D(x, y, 0)) + d * offset;
                rays[k] = new NscLaunchRay { Position = p, Direction = d, Intensity = 1, InsideOf = inside, BulkScatter = NscSourceBulkScatter.Many };   // wave 17: deliberate; volume physics is refused above, and any B segment is refused below
            }
            var tracer = new NscTracer(scene, wave, settings with { Seed = unchecked((long)SplitMix64.ForStream(settings.Seed, -1L - g).Next()) });
            var alpha = new double[n];
            if (settings.AccountsEnergy)
                for (int i = 0; i < n; i++)
                {
                    var o = scene.Objects[i];
                    if (o.Geometry.IsVolume && o.Material.Kind == NscMaterialKind.Refractive)
                        alpha[i] = BulkAbsorption.Coefficient(o.Material.Medium ?? Paraxial.ParaxialMedium.Air, wave);
                }
            var recorder = new NscRayRecorder();
            tracer.Run(rays, recorder, cancellation);
            var db = recorder.Database;
            var power = sources.Select(e => e.PowerAt(wave)).ToArray();
            double mean = 0, m2 = 0;
            long count = 0, contributing = 0;
            foreach (var ray in db.Rays)
            {
                double sample = weight * Contribution(ray, sources, power, alpha, ray.RayNumber, wave);
                if (sample != 0) contributing++;
                count++;
                double delta = sample - mean;   // Welford, in ray order
                mean += delta / count;
                m2 += delta * (sample - mean);
            }
            double se = Math.Sqrt(m2 / (count - 1) / count);
            runs.Add(new NscReverseRadianceRun(wave, mean, se, power.Sum(), rays.Length, contributing));
            total.Add(mean);
            variance += se * se;
        }
        return new NscReverseRadianceResult { Flux = total.Total, StandardError = Math.Sqrt(variance), Runs = runs, PixelArea = area, Notes = notes };
    }

    private static double Contribution(NscRecordedRay ray, List<Emitter> sources, double[] power, double[] alpha, long number, double wave)
    {
        var s = ray.Segments;
        double nd = s[0].Index, c = 0;
        for (int k = 1; k < s.Count; k++)
        {
            var seg = s[k];
            if (seg.Events.HasFlag(NscEvents.BulkScattered))
                throw new NotSupportedException($"Reverse radiance: backward ray {number} at {wave} µm was bulk scattered (segment {k}); volume scattering in the backward trace is not built.");
            Vector3D a;
            double tEnd;
            if (seg.Hit > 0) { a = s[seg.Parent].Position; tEnd = seg.PathLength; }
            else { a = seg.Position; tEnd = double.PositiveInfinity; }
            double ratio = nd / seg.Index;
            for (int j = 0; j < sources.Count; j++)
            {
                if (power[j] == 0) continue;
                double l = sources[j].Radiance(a, seg.Direction, tEnd, out double t);
                if (l == 0) continue;
                double intensity = seg.Hit > 0 && seg.Inside > 0 && alpha[seg.Inside - 1] > 0
                    ? seg.Intensity * Math.Exp(alpha[seg.Inside - 1] * (tEnd - t)) : seg.Intensity;
                c += intensity * power[j] * l * ratio * ratio;
            }
        }
        return c;
    }

    /// <summary>A source's radiance per watt and its spectrum.</summary>
    private sealed class Emitter
    {
        public required RigidTransform Placement { get; init; }
        public required NscSourceEllipse Shape { get; init; }
        public required IReadOnlyList<NscSpectralLine> Lines { get; init; }
        public required double Power { get; init; }
        public required double Area { get; init; }

        public double PowerAt(double wave)
        {
            double sum = Lines.Sum(l => l.Weight), w = Lines.Where(l => l.Micrometers == wave).Sum(l => l.Weight);
            return Power * w / sum;
        }

        /// <summary>Radiance per watt where the line a + t·d (0 &lt; t ≤ tEnd) crosses the
        /// emitting face against the emission; 0 when it does not.</summary>
        public double Radiance(Vector3D a, Vector3D d, double tEnd, out double t)
        {
            var p = Placement.InverseApply(a);
            var v = Placement.InverseApplyDirection(d);
            t = 0;
            if (!(v.Z < 0)) return 0;
            t = -p.Z / v.Z;
            if (!(t > 0) || t > tEnd) return 0;
            double x = p.X + t * v.X, y = p.Y + t * v.Y;
            var e = Shape;
            if (e.IsRectangle)
            {
                if (Math.Abs(x) > e.HalfWidthX || Math.Abs(y) > e.HalfWidthY) return 0;
                if (e.MinHalfWidthX > 0 && e.MinHalfWidthY > 0 && Math.Abs(x) < e.MinHalfWidthX && Math.Abs(y) < e.MinHalfWidthY) return 0;
            }
            else
            {
                double u = x / e.HalfWidthX, w = y / e.HalfWidthY;
                if (u * u + w * w > 1) return 0;
                if (e.MinHalfWidthX > 0 && e.MinHalfWidthY > 0)
                {
                    double ui = x / e.MinHalfWidthX, wi = y / e.MinHalfWidthY;
                    if (ui * ui + wi * wi < 1) return 0;
                }
            }
            double mu = -v.Z, cn = e.CosineExponent;
            return (cn + 1) * Math.Pow(mu, cn - 1) / (2 * Math.PI * Area);
        }

        public static Emitter For(NscScene scene, RigidTransform[] placements, int number, NscTraceSettings settings,
            NscSourceEmitOptions options, List<string> notes)
        {
            if (number < 1 || number > scene.Objects.Count || scene.Objects[number - 1].Geometry is not NscSource source)
                throw new ArgumentException($"Reverse radiance: object {number} is not a source.");
            string where = $"Reverse radiance: source object {number} ({source.TypeName})";
            if (source is not NscSourceEllipse e)
                throw new NotSupportedException($"{where}: the radiance of this source type is not built for backward tracing; Source Ellipse and Source Rectangle in the cosine mode are.");
            if (e.Mode != "cosine")
                throw new NotSupportedException(e.Mode is "point" or "collimated"
                    ? $"{where}: in the {e.Mode} mode the source has no spread in {(e.Mode == "point" ? "position as seen from each direction" : "direction")}, so backward rays reach it with probability zero; use a cosine exponent above 0."
                    : $"{where}: the Gaussian mode's radiance (its normalisation over the hemisphere) is not built; use the cosine mode.");
            var props = source.Properties;
            if (props.Array.Type != NscSourceArrayType.None) throw new NotSupportedException($"{where}: source arrays are not built for backward tracing.");
            if (props.PrePropagation != 0) throw new NotSupportedException($"{where}: pre-propagation moves the start points off the face; not built for backward tracing.");
            if (props.ReverseRays) throw new NotSupportedException($"{where}: reversed rays are not built for backward tracing.");
            var lines = props.Spectrum.Lines(options.SystemWavelengths, source.Common.Wavenumber, settings.Environment.VacuumMicrometers, where);
            double p = source.Common.Power;
            if (options.Units == SourceUnit.Lumens)
            {
                var v = options.LuminousEfficiency ?? throw new NotSupportedException($"{where}: lumens need V(λ) in the emission options.");
                double wv = lines.Sum(l => l.Weight * v(l.Micrometers)), ws = lines.Sum(l => l.Weight);
                double efficacy = NscSourceEmitter.LuminousEfficacy * wv / ws;
                if (!(efficacy > 0)) throw new ArgumentException($"{where}: the source is stated in lumens but its spectrum has no visible light.");
                p /= efficacy;
                notes.Add($"{where}: {StudyReport.Number(source.Common.Power, "g6")} lm counted as {StudyReport.Number(p, "g6")} W.");
            }
            double outer = e.IsRectangle ? 4 * e.HalfWidthX * e.HalfWidthY : Math.PI * e.HalfWidthX * e.HalfWidthY;
            double hole = e.MinHalfWidthX > 0 && e.MinHalfWidthY > 0
                ? e.IsRectangle ? 4 * e.MinHalfWidthX * e.MinHalfWidthY : Math.PI * e.MinHalfWidthX * e.MinHalfWidthY : 0;
            if (!(outer - hole > 0)) throw new ArgumentException($"{where}: the emitting area is zero.");
            return new Emitter { Placement = placements[number - 1], Shape = e, Lines = lines, Power = p, Area = outer - hole };
        }
    }
}

/// <summary>Settings of the <see cref="NscReverseRadianceAnalysis"/> (this program's own;
/// OpticStudio's ReverseRadiance analysis has no page in the 2025 R2 public help).</summary>
public sealed record NscReverseRadianceSettings
{
    [DisplayName("Detector"), Description("The Detector Rectangle or Detector Color object whose pixel is estimated (0: the first such detector).")]
    public int Detector { get; init; }

    [DisplayName("Pixel X"), Description("The pixel column, 1 at the detector's −x edge.")]
    public int PixelX { get; init; } = 1;

    [DisplayName("Pixel Y"), Description("The pixel row, 1 at the detector's −y edge.")]
    public int PixelY { get; init; } = 1;

    [DisplayName("Backward rays"), Description("Backward rays launched from the pixel per wavelength.")]
    public int Rays { get; init; } = 100_000;

    [DisplayName("Source"), Description("The source whose light is counted; 0 for every source.")]
    public int Source { get; init; }

    [DisplayName("Split NSC rays"), Description("Split backward (and forward) rays at refractive interfaces.")]
    public bool SplitRays { get; init; }

    [DisplayName("Use polarization"), Description("The polarization trace (Fresnel and coating energy); splitting turns it on.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Compare with forward trace"), Description("Also trace the sources forward and show the pixel's flux and standard error from the detector.")]
    public bool CompareForward { get; init; } = true;
}

/// <summary>The reverse-radiance estimate of one pixel as a registered analysis (id <c>NscReverseRadiance</c>).</summary>
public sealed class NscReverseRadianceAnalysis : OpticsAnalysis<NscReverseRadianceSettings>
{
    public const string SavedId = "NscReverseRadiance";
    public override string Id => SavedId;
    public override string Name => "Reverse radiance";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscReverseRadianceSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var (scene, trace, unit) = NscStrayLightInput.Trace(context, settings.SplitRays, settings.UsePolarization, ignoreErrors: false, scatter: false, notes, Name);
        int detector = settings.Detector;
        if (detector == 0)
            detector = Enumerable.Range(1, scene.Objects.Count).FirstOrDefault(i => scene.Objects[i - 1].Geometry is NscFlatDetector);
        if (detector == 0) throw new ArgumentException($"{Name}: the scene has no Detector Rectangle or Detector Color.");
        var sources = settings.Source == 0 ? null : new[] { settings.Source };
        var r = NscReverseRadiance.Estimate(scene, trace, new NscReverseRadianceOptions
        {
            Detector = detector, PixelX = settings.PixelX, PixelY = settings.PixelY, Rays = settings.Rays, Sources = sources,
            Emission = ProjectNscSources.EmitOptions(context, NscEmissionPurpose.Analysis),
        }, cancellation);
        string fluxUnit = ProjectNscSources.EmitOptions(context, NscEmissionPurpose.Analysis).Units == SourceUnit.Joules ? "J" : "W";
        string F(double v) => StudyReport.Number(v, "g8") + " " + fluxUnit;
        var pairs = new List<(string, string)>
        {
            ("Detector", $"object {detector}, pixel ({settings.PixelX}, {settings.PixelY}), area {StudyReport.Number(OpticsConventions.FromMeters(OpticsConventions.FromMeters(r.PixelArea, unit), unit), "g8")} {Paraxial.ParaxialReport.UnitName(unit)}²"),
            ("Sources", sources is null ? "every source" : $"object {settings.Source}"),
            ("Backward estimate", $"{F(r.Flux)} ± {F(r.StandardError)} (one standard error)"),
        };
        if (settings.CompareForward)
        {
            var run = ProjectNscSources.Trace(context, sources, split: settings.SplitRays, usePolarization: settings.UsePolarization, cancellation: cancellation);
            var data = run.Detectors?.OfType<NscFlatDetectorData>().FirstOrDefault(d => d.ObjectNumber == detector)
                ?? throw new InvalidOperationException($"{Name}: the forward trace returned no data for detector object {detector}.");
            int i = data.Index(settings.PixelX - 1, settings.PixelY - 1);
            double f = data.Flux[i], se = data.FluxStandardError(i);
            double combined = Math.Sqrt(se * se + r.StandardError * r.StandardError);
            pairs.Add(("Forward estimate", $"{F(f)} ± {F(se)} ({run.Ledger.Rays} rays)"));
            pairs.Add(("Backward − forward", $"{F(r.Flux - f)} ({StudyReport.Number(combined > 0 ? (r.Flux - f) / combined : 0, "g4")} combined standard errors)"));
        }
        var rows = r.Runs.Select(x => (IReadOnlyList<string>)new[]
        {
            StudyReport.Number(x.WavelengthMicrometers, "g8"), F(x.SourcePower), F(x.Flux), F(x.StandardError),
            x.Rays.ToString(CultureInfo.InvariantCulture), x.Contributing.ToString(CultureInfo.InvariantCulture),
        }).ToList();
        var sections = new List<ReportSection>
        {
            new("Reverse radiance") { Pairs = pairs },
            new("Wavelengths")
            {
                TableHeaders = new[] { "Wavelength (µm)", "Source power", "Pixel flux", "Standard error", "Backward rays", "Rays reaching a source" },
                TableRows = rows,
            },
        };
        notes.AddRange(r.Notes);
        notes.Add("Backward rays start uniformly on the pixel with cosine-weighted directions over the hemispheres the detector receives from, are traced by the forward tracer, and collect each source's radiance where they cross its face against its emission (docs/Optics-Conventions.md §78).");
        notes.Add("OpticStudio's ReverseRadiance detector, target and analysis have no page in the 2025 R2 public help; their objects and settings are not built.");
        return OpticsAnalysisResult.FromSections(Name, sections, notes);
    }
}
