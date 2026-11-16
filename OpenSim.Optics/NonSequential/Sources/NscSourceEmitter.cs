using System.Globalization;
using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>What a source emission needs beyond the scene: which ray count, the system
/// wavelengths, the source units and, for lumens, the photopic luminous efficiency V(λ).</summary>
public sealed record NscSourceEmitOptions
{
    public NscEmissionPurpose Purpose { get; init; } = NscEmissionPurpose.Analysis;

    /// <summary>The system wavelengths (µm in air) and weights, row 1 first (for the System
    /// Wavelength colour model).</summary>
    public required IReadOnlyList<NscSpectralLine> SystemWavelengths { get; init; }

    public SourceUnit Units { get; init; } = SourceUnit.Watts;

    /// <summary>V(λ), λ in µm (in air), the CIE photopic luminous efficiency normalised to 1 at
    /// its peak; needed for lumens only. ProjectNscSources.EmitOptions passes OP-79's Photometric.PhotopicEfficiency; without it lumens are
    /// refused.</summary>
    public Func<double, double>? LuminousEfficiency { get; init; }
}

/// <summary>One source's rays, by wavelength, and its power: <see cref="StatedPower"/> in the
/// source units, <see cref="TracedPower"/> the sum of the launched rays' powers (watts for
/// watts and lumens, joules for joules).</summary>
public sealed record NscSourceEmissionSet(
    int ObjectNumber, string TypeName, long Rays, double StatedPower, SourceUnit StatedUnit, double TracedPower,
    IReadOnlyList<NscEmission> Emissions, IReadOnlyList<string> Notes);

/// <summary>
/// Turns one source object of a scene into launch rays (plan item OP-77;
/// docs/Optics-Conventions.md §72).
/// <list type="bullet">
/// <item><b>Units</b> (the system's <see cref="SourceUnit"/>; help, "Units": "Source units may be
/// either watts, lumens, or joules"). Watts and joules are traced as stated: "every ray has equal
/// power (or energy), and the total power of all the rays traced will exactly equal the total
/// power of the source" (help, "How rays are selected from the spectrum"). Lumens are converted to
/// watts with the source's spectrum first ("Each ray is then assigned an equal wattage"); the unit
/// prefixes only rename the unit and are not modelled.</item>
/// <item><b>Rays and power.</b> N rays per array element (N the layout or analysis count), each
/// carrying P/N where P is the element's power in watts or joules: "The initial intensity of each
/// ray from a given source is the total power divided by the number of rays to be traced" (help,
/// "Ray Trace"). Lumens are first converted to watts with the source's spectrum,
/// P_W = P_lm / (K·Σwᵢ V(λᵢ)/Σwᵢ), K = 683 lm/W (the SI definition of the candela: "the luminous
/// efficacy of monochromatic radiation of frequency 540 × 10¹² Hz, K_cd, to be 683 when expressed
/// in the unit lm W⁻¹"; NIST, "Definitions of the SI base units",
/// plans/sources/optics/op-77-help/nist/nist_definitions_si_base_units.html). V(λ) is taken as 1
/// at 555 nm, so a 555 nm watt is 683 lm, the convention plan item OP-79's gate uses.</item>
/// <item><b>Ray k</b> (0-based, element e's ray j is k = e·N + j) takes Sobol point k + 1 (Joe and
/// Kuo's sequence, the origin skipped) or the first numbers of the stream SplitMix64.ForStream(s, k),
/// s the source's seed; rejection steps draw from that stream (after the Sobol case's fresh start).
/// The last number picks the wavelength by weight. So ray k depends only on k, the source and the
/// seed, and the rays are the same to the bit at any thread count.</item>
/// <item><b>The seed</b> is Source Ray's own nonzero "Random Seed", else derived from the trace
/// settings' seed and the object number.</item>
/// <item><b>Placement.</b> Local point and direction, then the array offset, then the
/// pre-propagation along the unit direction, then the reversal, then the object's global
/// placement. Rays start inside the source's resolved "Inside Of".</item>
/// <item><b>Polarization</b>: unpolarized (no field) or the Jones vector's field
/// (<see cref="NscSourcePolarization"/>) in the local frame, rotated to the scene frame.</item>
/// </list>
/// </summary>
public static class NscSourceEmitter
{
    /// <summary>K_cd, lm/W (NIST, "Definitions of the SI base units", the candela).</summary>
    public const double LuminousEfficacy = 683;

    private const int RaysPerChunk = 4096;

    /// <summary>The rays of source object <paramref name="objectNumber"/> (1-based). Throws
    /// <see cref="ArgumentException"/> naming the object for a bad source, and
    /// <see cref="NotSupportedException"/> for a model that is not built.</summary>
    public static NscSourceEmissionSet Emit(NscScene scene, int objectNumber, NscTraceSettings settings,
        NscSourceEmitOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Emit(scene, scene.GlobalPlacements(), objectNumber, settings, options, cancellation);
    }

    internal static NscSourceEmissionSet Emit(NscScene scene, RigidTransform[] placements, int objectNumber, NscTraceSettings settings,
        NscSourceEmitOptions options, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (objectNumber < 1 || objectNumber > scene.Objects.Count)
            throw new ArgumentException($"Source object {objectNumber}: the scene has objects 1 to {scene.Objects.Count}.");
        var o = scene.Objects[objectNumber - 1];
        if (o.Geometry is not NscSource source)
            throw new ArgumentException($"Object {objectNumber} ({o.Geometry.TypeName}) is not a source.");
        string where = $"Object {objectNumber} ({source.TypeName})";
        var problems = new List<string>();
        source.Common.Validate(where, problems);
        source.Properties.Validate(where, problems);
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));

        var notes = new List<string>(source.Notes);
        int n = options.Purpose == NscEmissionPurpose.Layout ? source.Common.LayoutRays : source.Common.AnalysisRays;
        var offsets = source.Properties.Array.Offsets(where);
        var lines = source.Properties.Spectrum.Lines(options.SystemWavelengths, source.Common.Wavenumber,
            settings.Environment.VacuumMicrometers, where);

        double elementPower = source.Common.Power;
        if (options.Units == SourceUnit.Lumens)
        {
            var v = options.LuminousEfficiency
                ?? throw new NotSupportedException($"{where}: the source units are lumens, which need the CIE photopic luminous efficiency V(λ), and these emission options carry none (ProjectNscSources.EmitOptions passes Photometric.PhotopicEfficiency). Use watts or joules, or give V(λ).");
            var weighted = new NscLedgerBuilder.Sum();
            var weights = new NscLedgerBuilder.Sum();
            foreach (var l in lines)
            {
                double vl = v(l.Micrometers);
                if (!(vl >= 0) || !double.IsFinite(vl)) throw new ArgumentException($"{where}: V({l.Micrometers} µm) is {vl}.");
                weighted.Add(l.Weight * vl);
                weights.Add(l.Weight);
            }
            double efficacy = LuminousEfficacy * weighted.Total / weights.Total;
            if (!(efficacy > 0))
                throw new ArgumentException($"{where}: the source is stated in lumens but its spectrum has no visible light (V(λ) is zero at every wavelength), so no watts give that many lumens.");
            elementPower = source.Common.Power / efficacy;
            notes.Add($"{where}: {Num(source.Common.Power)} lm per element traced as {Num(elementPower)} W (spectral luminous efficacy {Num(efficacy)} lm/W).");
        }

        long total = (long)n * offsets.Count;
        if (n == 0)
        {
            notes.Add($"{where}: no {(options.Purpose == NscEmissionPurpose.Layout ? "layout" : "analysis")} rays; nothing launched.");
            return new NscSourceEmissionSet(objectNumber, source.TypeName, 0, source.Common.Power * offsets.Count, options.Units, 0, Array.Empty<NscEmission>(), notes);
        }
        if (total > int.MaxValue || total >= SobolSequence.MaxIndex)
            throw new ArgumentException($"{where}: {total} rays ({n} per element × {offsets.Count} elements) is more than one run holds.");

        int count = (int)total;
        double rayPower = elementPower / n;
        long seed = source.OwnSeed != 0 ? source.OwnSeed : unchecked((long)SplitMix64.ForStream(settings.Seed, objectNumber).Next());
        int dims = source.Dimensions + 1;
        bool sobol = source.Properties.Sampling == NscSourceSampling.Sobol;
        var sequence = sobol ? new SobolSequence(dims) : null;
        var cumulative = new double[lines.Count];
        {
            var s = new NscLedgerBuilder.Sum();
            for (int j = 0; j < lines.Count; j++) { s.Add(lines[j].Weight); cumulative[j] = s.Total; }
        }
        var transform = placements[objectNumber - 1];
        int insideOf = NscScene.Resolve(o.InsideOf, objectNumber);
        var pol = source.Properties.Polarization;
        double pre = source.Properties.PrePropagation;
        bool reverse = source.Properties.ReverseRays;

        var rays = new NscLaunchRay[count];
        var line = new int[count];
        int chunks = (count + RaysPerChunk - 1) / RaysPerChunk;
        ParallelSlots.Compute(chunks, settings.MaxDegreeOfParallelism, c =>
        {
            cancellation.ThrowIfCancellationRequested();
            Span<double> u = stackalloc double[dims];
            int end = Math.Min(count, (c + 1) * RaysPerChunk);
            for (int k = c * RaysPerChunk; k < end; k++)
            {
                SplitMix64 extra = SplitMix64.ForStream(seed, k);
                if (sequence is not null) sequence.Point(k + 1L, u);
                else for (int i = 0; i < dims; i++) u[i] = extra.NextDouble();
                source.Sample(u, extra, out var p, out var d);
                double length = d.Length;
                if (!(length > 0) || !double.IsFinite(length) || !double.IsFinite(p.X + p.Y + p.Z))
                    throw new InvalidOperationException($"{where}, ray {k}: the sampled ray is not finite (position {p}, direction {d}).");
                d /= length;
                var (ox, oy) = offsets[k / n];
                p = new Vector3D(p.X + ox, p.Y + oy, p.Z);
                if (pre != 0) p += d * pre;
                if (reverse) d = -d;
                rays[k] = new NscLaunchRay
                {
                    Position = transform.Apply(p),
                    Direction = transform.ApplyDirection(d),
                    Intensity = rayPower,
                    InsideOf = insideOf,
                    Field = pol.Random ? null : Field(pol, d, where, k).Rotated(transform),
                };
                line[k] = Pick(cumulative, u[dims - 1]);
            }
            return 0;
        }, cancellation);

        var lists = new List<NscLaunchRay>[lines.Count];
        var numberLists = new List<long>[lines.Count];
        for (int j = 0; j < lines.Count; j++) { lists[j] = new List<NscLaunchRay>(); numberLists[j] = new List<long>(); }
        for (int k = 0; k < count; k++) { lists[line[k]].Add(rays[k]); numberLists[line[k]].Add(k); }
        var emissions = new List<NscEmission>();
        var traced = new NscLedgerBuilder.Sum();
        for (int j = 0; j < lines.Count; j++)
        {
            var list = lists[j];
            var numbers = numberLists[j];
            if (list.Count == 0) continue;
            foreach (var r in list) traced.Add(r.Intensity);
            emissions.Add(new NscEmission(lines[j].Micrometers, list)
            {
                SourceObjects = Enumerable.Repeat(objectNumber, list.Count).ToArray(),
                SourceRayNumbers = numbers,
            });
        }
        if (source.Properties.CoherenceLength != 0 || source.Properties.InitialPhaseDegrees != 0)
            notes.Add($"{where}: the coherence length and initial phase are stored but not used (no coherent detector data is traced yet).");
        if (source.Properties.BulkScatter != NscSourceBulkScatter.Many)
            notes.Add($"{where}: the bulk scatter setting is stored but not used (volume physics is plan item OP-81).");
        return new NscSourceEmissionSet(objectNumber, source.TypeName, count, source.Common.Power * offsets.Count, options.Units,
            traced.Total, emissions, notes);
    }

    /// <summary>The first line whose cumulative weight exceeds u·total.</summary>
    private static int Pick(double[] cumulative, double u)
    {
        double target = u * cumulative[^1];
        for (int j = 0; j < cumulative.Length - 1; j++)
            if (target < cumulative[j]) return j;
        return cumulative.Length - 1;
    }

    /// <summary>The Jones vector as a field for a ray along the unit local direction
    /// <paramref name="k"/>, by the X Axis Reference method (help, "Defining the Initial
    /// Polarization"): P = K × X̂ normalised, S = P × K, E = (Jx·e^{iφx}·S + Jy·e^{iφy}·P)/|J|.</summary>
    public static RayField Field(NscSourcePolarization j, Vector3D k, string where = "Source", long ray = 0)
    {
        var p = new Vector3D(0, k.Z, -k.Y);        // K × X̂
        double length = p.Length;
        if (!(length > 1e-12))
            throw new InvalidOperationException($"{where}, ray {ray}: the ray runs along the local x axis, where the X Axis Reference method gives the Jones vector no plane (help, \"Defining the Initial Polarization\"); use random polarization or turn the source.");
        p /= length;
        var s = Vector3D.Cross(p, k);
        double norm = Math.Sqrt(j.Jx * j.Jx + j.Jy * j.Jy);
        var ex = Complex.FromPolarCoordinates(j.Jx / norm, j.XPhaseDegrees * Math.PI / 180);
        var ey = Complex.FromPolarCoordinates(j.Jy / norm, j.YPhaseDegrees * Math.PI / 180);
        return new RayField(ex * s.X + ey * p.X, ex * s.Y + ey * p.Y, ex * s.Z + ey * p.Z);
    }

    private static string Num(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
