using System.Runtime.CompilerServices;
using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Tolerancing;

// OP-64: the tolerance criterion of one lens (docs/Optics-Conventions.md §42).

/// <summary>A criterion that cannot be evaluated for a perturbed lens (OpticStudio prints
/// "Infinity", help "Trouble Shooting the Tolerance Results"); the message names what and where.</summary>
public sealed class ToleranceEvaluationException : InvalidOperationException
{
    public ToleranceEvaluationException(string message) : base(message) { }
}

/// <summary>One field point of the criterion, in the field type's unit (degrees or meters).</summary>
internal sealed record ToleranceField(double X, double Y, double Weight, string Label);

/// <summary>
/// Evaluates the tolerance criterion (help, "Tolerancing", Criterion tab) of a lens of the
/// configuration being toleranced. Spot and wavefront: OP-40's RMS core over the Gaussian
/// quadrature with <c>Sampling</c> rings and twice as many arms (help: "The number of arms is always
/// twice the number of rays along each arm"), about the centroid ("OpticStudio always uses a
/// centroid reference for tolerancing"), polychromatic over the system's wavelengths and weights;
/// the field values are combined as √(Σ wᵢ vᵢ² / Σ wᵢ), the form of OpticStudio's merit function,
/// since the help says OpticStudio "uses the Criterion, Field, MTF Frequency, and Sampling settings
/// ... to define an appropriate merit function" and takes its value as the criterion. The Strehl
/// ratio (OP-38's engine through the RMS core) is combined as the weighted mean. Boresight error:
/// "the radial chief ray coordinate traced for the on axis field divided by the effective focal
/// length", at the primary wavelength, radians. Merit function: the lens's own merit function.
/// </summary>
internal sealed class ToleranceCriterionEvaluator
{
    private readonly ToleranceRunOptions _o;
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;
    private readonly PupilQuadratureRule _rule;
    private readonly ConditionalWeakTable<SequentialLens, Setup> _setups = new();

    private sealed class Setup
    {
        public RmsSetup? Rms;
        public string? Failure;
    }

    public ToleranceCriterionEvaluator(ToleranceRunOptions options, IReadOnlyList<OpticalCatalog> catalogs, int configuration,
        IReadOnlyList<ToleranceField> fields, MeritFunction? merit)
    {
        _o = options;
        _catalogs = catalogs;
        Configuration = configuration;
        Fields = fields;
        Merit = merit;
        _rule = PupilQuadrature.Gaussian(options.Sampling, 2 * options.Sampling);
        Info = ToleranceCriteria.Info(options.Criterion);
    }

    public ToleranceCriterionInfo Info { get; }

    /// <summary>The configuration toleranced (1 = the first).</summary>
    public int Configuration { get; }

    public IReadOnlyList<ToleranceField> Fields { get; }

    /// <summary>The lens's merit function, for the merit function criterion.</summary>
    public MeritFunction? Merit { get; }

    public PupilQuadratureRule Rule => _rule;

    /// <summary>The criterion of <paramref name="settings"/>, with each configuration's lens moved
    /// by <paramref name="focusShifts"/> meters at the image (index 0 = configuration 1; null for none).</summary>
    public double Evaluate(OpticsSettings settings, IReadOnlyList<double>? focusShifts, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_o.Criterion == ToleranceCriterion.MeritFunction)
        {
            IMeritConfigurations configurations = new ProjectMeritConfigurations(settings, _catalogs);
            if (focusShifts is not null) configurations = new FocusedConfigurations(configurations, focusShifts);
            var e = MeritEvaluator.Evaluate(Merit!, configurations, null, new MeritEvaluationOptions { MaxDegreeOfParallelism = 1 }, cancellation);
            if (e.HasFailures)
                throw new ToleranceEvaluationException("The merit function cannot be evaluated: " + string.Join(" ", e.Errors));
            return e.Value;
        }
        var (lens, system) = Build(settings, Configuration, _catalogs);
        if (focusShifts is not null) lens = RmsCore.Focused(lens, focusShifts[Configuration - 1]);
        return OfLens(lens, system, cancellation);
    }

    /// <summary>Configuration <paramref name="configuration"/> of a record as a lens with its solves and its system data.</summary>
    public static (SequentialLens Lens, OpticalSystemData System) Build(OpticsSettings settings, int configuration, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var s = MultiConfiguration.SettingsFor(settings, configuration, catalogs);
        var lens = ProjectLens.From(s, catalogs)
            ?? throw new ToleranceEvaluationException($"Configuration {configuration} has no sequential surfaces.");
        return (lens, SystemDataSettings.FromSettings(s.SystemData, new List<string>()));
    }

    /// <summary>The criterion of one lens (every criterion but the merit function).</summary>
    public double OfLens(SequentialLens lens, OpticalSystemData system, CancellationToken cancellation)
    {
        if (_o.Criterion == ToleranceCriterion.BoresightError) return Boresight(lens, system);
        var values = new double[Fields.Count];
        for (int f = 0; f < Fields.Count; f++) values[f] = FieldValue(lens, system, f, cancellation);
        return Combine(values);
    }

    /// <summary>The field values combined (see the class remarks).</summary>
    public double Combine(IReadOnlyList<double> values)
    {
        double sw = 0, s = 0;
        for (int f = 0; f < Fields.Count; f++)
        {
            double w = Fields[f].Weight;
            sw += w;
            s += Info.HigherIsBetter ? w * values[f] : w * values[f] * values[f];
        }
        return Info.HigherIsBetter ? s / sw : Math.Sqrt(s / sw);
    }

    /// <summary>The criterion's value at field <paramref name="f"/> (spot in lens units, wavefront
    /// in waves, the Strehl ratio).</summary>
    public double FieldValue(SequentialLens lens, OpticalSystemData system, int f, CancellationToken cancellation)
    {
        var setup = _setups.GetValue(lens, l =>
        {
            try { return new Setup { Rms = RmsCore.Prepare(l, system, false, "Tolerance criterion", new List<string>(), cancellation) }; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return new Setup { Failure = e.Message }; }
        });
        if (setup.Failure is { } why) throw new ToleranceEvaluationException(why);
        var field = Fields[f];
        var point = new RmsPoint
        {
            FieldX = field.X, FieldY = field.Y,
            Rows = Enumerable.Range(0, system.Wavelengths.Wavelengths.Count).ToArray(),
            Polychromatic = system.Wavelengths.Wavelengths.Count > 1,
            Label = field.Label,
        };
        var q = Quantity(_o.Criterion);
        var v = RmsCore.Evaluate(setup.Rms!, point, q, RmsReference.Centroid, _rule, cancellation, _o.PsfSampling);
        if (!v.Succeeded) throw new ToleranceEvaluationException(v.Failure!);
        return RmsCore.IsSpot(q) ? OpticsConventions.FromMeters(v.Value, system.Units.LensUnit) : v.Value;
    }

    internal static RmsQuantity Quantity(ToleranceCriterion c) => c switch
    {
        ToleranceCriterion.RmsSpotRadius => RmsQuantity.SpotRadius,
        ToleranceCriterion.RmsSpotX => RmsQuantity.SpotX,
        ToleranceCriterion.RmsSpotY => RmsQuantity.SpotY,
        ToleranceCriterion.RmsWavefront => RmsQuantity.WavefrontRms,
        ToleranceCriterion.StrehlRatio => RmsQuantity.Strehl,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Not a field criterion."),
    };

    /// <summary>The boresight error, radians: the radial image coordinate of the on-axis chief ray
    /// at the primary wavelength over the paraxial effective focal length (its magnitude).</summary>
    public static double Boresight(SequentialLens lens, OpticalSystemData system)
    {
        int primary = system.Wavelengths.PrimaryIndex;
        var trace = SequentialTrace.Create(lens, system, primary);
        var launcher = new RayLauncher(lens, system);
        var aimed = launcher.LaunchAimed(trace, 0, 0, 0, 0);
        if (aimed.Failure is { } miss) throw new ToleranceEvaluationException($"Boresight error: the on-axis chief ray cannot be aimed: {miss}");
        var ray = trace.Trace(aimed.Ray);
        if (!ray.IsOk)
            throw new ToleranceEvaluationException($"Boresight error: the on-axis chief ray fails ({RayStatuses.Label(ray.Status)} at surface {ray.FailedAtSurface}).");
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system, primary);
        if (p.EffectiveFocalLength is not double efl || efl == 0)
            throw new ToleranceEvaluationException("Boresight error: the system is afocal, so there is no effective focal length to divide by.");
        return Math.Sqrt(ray.Position.X * ray.Position.X + ray.Position.Y * ray.Position.Y) / Math.Abs(efl);
    }

    /// <summary>
    /// The lens's defocus, meters: the thickness before the image minus the paraxial image distance
    /// −y/u of the marginal ray leaving the last surface (primary wavelength). The paraxial focus
    /// compensator keeps it at the nominal lens's value.
    /// </summary>
    public static double Defocus(SequentialLens lens, OpticalSystemData system, string where)
    {
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        int last = lens.LastSurfaceIndex;
        double y = p.MarginalRay.Heights[last], u = p.MarginalRay.Slopes[last];
        if (p.ImageAtInfinity || u == 0)
            throw new ToleranceEvaluationException($"Paraxial focus compensator ({where}): the marginal ray leaves the last surface parallel to the axis, so there is no paraxial focus to hold.");
        return lens.Surfaces[last].Thickness - (-y / u);
    }

    /// <summary>The criterion's field points (help, "Tolerancing", "Fields"). For the symmetric
    /// sets the largest field coordinate is the largest radial distance of the system's field
    /// points (this program's reading of "the maximum field coordinate"); when every field is on
    /// the axis the set is the one on-axis point.</summary>
    public static IReadOnlyList<ToleranceField> FieldsOf(OpticalSystemData system, ToleranceFieldSet set, List<string> notes)
    {
        var points = system.Fields.Points;
        if (set == ToleranceFieldSet.UserDefined)
            return points.Select((p, i) => new ToleranceField(p.X, p.Y, p.Weight, $"field {i + 1}")).ToArray();
        double m = points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        if (m == 0)
        {
            notes.Add("Every field point is on the axis, so the symmetric field set is the one on-axis point.");
            return new[] { new ToleranceField(0, 0, 1, "field (0, 0)") };
        }
        string L(double x, double y) => $"field ({StudyReport.Number(x, "g4")}, {StudyReport.Number(y, "g4")})";
        var list = new List<ToleranceField>();
        foreach (double k in new[] { 1.0, 0.7, 0.0, -0.7, -1.0 })
            list.Add(new ToleranceField(0, k * m, k == 0 ? (set == ToleranceFieldSet.XYSymmetric ? 4 : 2) : 1, L(0, k * m)));
        if (set == ToleranceFieldSet.XYSymmetric)
            foreach (double k in new[] { -1.0, -0.7, 0.7, 1.0 })
                list.Add(new ToleranceField(k * m, 0, 1, L(k * m, 0)));
        return list;
    }
}

/// <summary>Configurations whose lenses are moved at the image by given focus shifts (meters).</summary>
internal sealed class FocusedConfigurations : IMeritConfigurations
{
    private readonly IMeritConfigurations _inner;
    private readonly IReadOnlyList<double> _shifts;

    public FocusedConfigurations(IMeritConfigurations inner, IReadOnlyList<double> shifts)
    {
        _inner = inner;
        _shifts = shifts;
    }

    public int Count => _inner.Count;

    public SequentialLens LensFor(int configuration) => RmsCore.Focused(_inner.LensFor(configuration), _shifts[configuration - 1]);

    public OpticalSystemData SystemFor(int configuration) => _inner.SystemFor(configuration);
}
