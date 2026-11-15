using System.Globalization;
using System.Runtime.CompilerServices;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// OP-55A: the imaging and ray half of OpticStudio's operand catalogue (docs/Optics-Conventions.md
/// §63): first-order properties, aberrations, MTF, Strehl, encircled energy, paraxial and real ray
/// data, relative illumination and the best-fit sphere. Every operand is a thin adapter over the
/// engine that computes the quantity on main (the paraxial model, the Seidel sums, OP-37's field
/// scans, OP-40's RMS core, OP-39's MTF engine, OP-38's PSF runs, OP-41's enclosed energy, the
/// real trace's surface hits, OP-43's best-fit sphere); none computes a quantity a second way.
/// <para>
/// <b>Definitions</b> are OpticStudio 2025 R2's help, "Optimization Operands (Alphabetically)" and
/// the category pages (copies in <c>plans/sources/optics/op-54-help/</c>), read operand by
/// operand. The help names each operand's parameters but not which of Int1, Int2 and Data1 to
/// Data6 holds them. OP-54's reading is followed (<see cref="MeritOperandRegistry"/>, Conventions
/// §10 item 27): integers in the order the help names them in Int1 then Int2, Hx, Hy, Px and Py in
/// Data1 to Data4 (help, "Modifying the merit function"), other reals after them in the help's
/// order; a third integer goes to the next free data column. The placements are listed per
/// operand in the descriptions and in §63, and are readings of the help (§10 item 59).
/// </para>
/// <para>
/// <b>Caches.</b> Operands whose value needs a whole analysis (RMS, MTF, Strehl, enclosed energy,
/// relative illumination, the field scans) keep their result in a cache keyed on the lens and
/// system instances (immutable records, one instance per configuration for a whole evaluation;
/// a new lens, as every optimizer step makes, starts a new entry), as <see cref="MtfOperands"/>
/// and <see cref="RayAberrationOperands"/> do, so neighbouring rows that differ only in what they
/// read from the same result compute it once.
/// </para>
/// </summary>
public static partial class ImagingOperands
{
    private const string AlphaPage = "Optimization Operands Alphabetically";

    /// <summary>The OP-55A operands, in registration order.</summary>
    public static IEnumerable<MeritOperandDefinition> Definitions() =>
        FirstOrderDefinitions()
            .Concat(AberrationDefinitions())
            .Concat(ImageQualityDefinitions())
            .Concat(ParaxialRayDefinitions())
            .Concat(RealRayDefinitions());

    /// <summary>The codes of OP-55A's brief that are not registered, with why (OP-55B's not-built
    /// table carries them).</summary>
    public static IReadOnlyDictionary<string, string> NotBuilt { get; } = new Dictionary<string, string>
    {
        ["EFLA"] = "the effective focal length \"in air\" of a surface pair needs the pair re-indexed to air, which the paraxial engine does not do.",
        ["POWF"] = "Power Field Map's local power (LocalPower) is built, but the operand's sphere/cylinder/max/min data codes are not read against it yet.",
        ["POWP"] = "as POWF (Power Pupil Map).",
        ["SFNO"] = "the help does not define the sagittal working F/#; the real working F/# engine gives the four-ray mean only.",
        ["TFNO"] = "as SFNO (tangential).",
        ["ABCD"] = "the grid distortion engine does not expose OpticStudio's ABCD predicted-magnification fit.",
        ["DISA"] = "as ABCD (user-defined ABCD distortion).",
        ["DISC"] = "no calibrated f-theta deviation-from-linearity engine.",
        ["DISG"] = "generalized distortion with OpticStudio's predicted magnification is not computed by the grid distortion engine.",
        ["DIMX"] = "as DISG (same calculation).",
        ["SMIA"] = "the SMIA-TV distortion of the grid distortion analysis is not exposed as a single value at a field width.",
        ["BIOC"] = "no biocular convergence engine.",
        ["BIOD"] = "no biocular dipvergence engine.",
        ["OSCD"] = "no offense-against-the-sine-condition engine (Welford's definition and Shack's real-ray form).",
        ["SPCH"] = "spherochromatism needs the real marginal axial colour of the extreme wavelengths; the longitudinal aberration engine gives the crossings, but the help's sign and zone reading is not done.",
        ["TRAD"] = "the x twin of TRAE (\"the x component of the TRAR only\" with TRAC's restrictions); not built for the same reason as TRAE.",
        ["TRAE"] = "the help's definition (\"the y component of the TRAR only\" with TRAC's restrictions) contradicts itself; not built.",
        ["CEHX"] = "the Huygens PSF engine does not return the PSF centroid's image position.",
        ["CEHY"] = "as CEHX.",
        ["CENX"] = "no n × n grid centroid with polarization at an arbitrary surface is exposed by the spot engines.",
        ["CENY"] = "as CENX.",
        ["CNAX"] = "as CENX (angular centroid).",
        ["CNAY"] = "as CENX (angular centroid).",
        ["CNPX"] = "as CENX.",
        ["CNPY"] = "as CENX.",
        ["DXDX"] = "no ray-fan slope engine; a finite difference here would be a second implementation of the fan.",
        ["DXDY"] = "as DXDX.",
        ["DYDX"] = "as DXDX.",
        ["DYDY"] = "as DXDX.",
        ["IMAE"] = "reads the Geometric Image Analysis' saved settings file, which this program does not have.",
        ["XENC"] = "reads the extended source encircled energy's saved settings file, which this program does not have.",
        ["XENF"] = "as XENC.",
        ["ERFP"] = "the geometric edge spread engine's default integration window (Max Radius 0) is not defined by the help.",
        ["FOUC"] = "reads the Foucault analysis' saved settings file, which this program does not have.",
        ["TOLR"] = "reads a tolerance settings file by number, which this program does not have.",
        ["PARZ"] = "the paraxial engine traces on vertex planes; the help does not define a paraxial ray's z on a surface.",
        ["PARA"] = "the help does not define the direction cosines of a paraxial ray (the engine gives slopes, PATX/PATY).",
        ["PARB"] = "as PARA.",
        ["PARC"] = "as PARA.",
        ["PANA"] = "the paraxial engine has no surface normal at a paraxial intercept.",
        ["PANB"] = "as PANA.",
        ["PANC"] = "as PANA.",
    };

    // ------------------------------------------------------------------ shared helpers

    internal static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>A real column that must hold a whole number.</summary>
    internal static int Whole(double value, string label)
    {
        if (!double.IsFinite(value) || value != Math.Round(value) || Math.Abs(value) > int.MaxValue)
            throw new MeritOperandException($"{label} {F(value)} is not a whole number.");
        return (int)value;
    }

    /// <summary>A field number (1-based) in a real column; returns the 0-based row.</summary>
    internal static int FieldRow(MeritRowContext c, double value, string label = "Field")
    {
        int field = Whole(value, label);
        int count = c.System.Fields.Points.Count;
        if (field < 1 || field > count)
            throw new MeritOperandException($"{label} {field} is not a field; the system has fields 1 to {count}.");
        return field - 1;
    }

    /// <summary>The 0-based wavelength rows of a Wave column: 0 for every wavelength
    /// (polychromatic), else the one named.</summary>
    internal static int[] WaveRows(MeritRowContext c, int wave, string label = "Wave") =>
        wave == 0 ? Enumerable.Range(0, c.System.Wavelengths.Wavelengths.Count).ToArray() : new[] { c.Wavelength(wave, label) - 1 };

    /// <summary>A Wave column where 0 means the primary wavelength; returns the 1-based number.</summary>
    internal static int WaveOrPrimary(MeritRowContext c, int wave, string label = "Wave") =>
        wave == 0 ? c.System.Wavelengths.PrimaryIndex + 1 : c.Wavelength(wave, label);

    /// <summary>A flag column OpticStudio offers whose non-zero value is not built: refused by name.</summary>
    internal static void NotBuiltFlag(double value, string label, string what)
    {
        if (value != 0) throw new MeritOperandException($"{label} {F(value)} ({what}) is not built; set it to 0.");
    }

    /// <summary>The pupil grid of a Samp column: 32·2ⁿ⁻¹ per side, n = 1 to 6 (the help's "1 yields
    /// 32 x 32, 2 yields 64 x 64 etc.", as <see cref="MtfOperands.GridOf"/> reads it).</summary>
    internal static int Grid(int samp) => MtfOperands.GridOf(samp);

    /// <summary>Runs an engine call, turning its argument and state errors into a row failure
    /// naming what was computed.</summary>
    internal static T Engine<T>(string what, Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new MeritOperandException($"{what} cannot be computed: {FirstLine(e.Message)}");
        }
    }

    internal static string FirstLine(string message)
    {
        int at = message.IndexOf('\n');
        return (at < 0 ? message : message[..at]).TrimEnd('\r', ' ');
    }

    // ------------------------------------------------------------------ caches

    private static readonly ConditionalWeakTable<SequentialLens, ConditionalWeakTable<OpticalSystemData, Store>> Stores = new();

    /// <summary>The per-lens, per-system store of computed results and the tracer.</summary>
    internal static Store For(MeritRowContext c) =>
        Stores.GetValue(c.Lens, _ => new ConditionalWeakTable<OpticalSystemData, Store>()).GetValue(c.System, s => new Store(c.Lens, s));

    /// <summary>
    /// Results of one lens and system: a value cache keyed on the operand family and its columns,
    /// and a real-ray tracer built the evaluator's way (<c>MeritEvaluator.ConfigurationState</c>:
    /// floating semi-diameters sized once by <see cref="SemiDiameterSizing"/>, rays launched by
    /// <see cref="RayLauncher"/>, traced by <see cref="SequentialTrace"/>), for operands that need
    /// rays besides the row's own (a chief-ray reference, a ray at the primary wavelength, a range
    /// of surfaces). Pass 3 of the evaluator is sequential; the lock covers other callers.
    /// </summary>
    internal sealed class Store
    {
        private readonly object _lock = new();
        private readonly SequentialLens _lens;
        private readonly OpticalSystemData _system;
        private readonly Dictionary<object, object> _values = new();
        private bool _ready;
        private string? _error;
        private IReadOnlyList<double>? _semi;
        private RayLauncher? _launcher;
        private SurfacePlacement? _placement;
        private readonly Dictionary<int, SequentialTrace> _traces = new();

        public Store(SequentialLens lens, OpticalSystemData system)
        {
            _lens = lens;
            _system = system;
        }

        /// <summary>The value under <paramref name="key"/>, computed once.</summary>
        public T Get<T>(object key, Func<T> compute) where T : notnull
        {
            lock (_lock)
            {
                if (_values.TryGetValue(key, out var v)) return (T)v;
            }
            var value = compute();
            lock (_lock) _values[key] = value;
            return value;
        }

        public SurfacePlacement Placement()
        {
            lock (_lock) return _placement ??= SurfacePlacement.Compute(_lens);
        }

        public (SequentialTrace Trace, RayLauncher Launcher) Tracer(int wave)
        {
            lock (_lock)
            {
                if (!_ready)
                {
                    _ready = true;
                    try
                    {
                        if (_lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
                            _semi = SemiDiameterSizing.Compute(_lens, _system).Clear;
                        _launcher = new RayLauncher(_lens, _system);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        _error = $"the lens cannot be traced: {FirstLine(e.Message)}";
                    }
                }
                if (_error is not null) throw new MeritOperandException(_error);
                if (!_traces.TryGetValue(wave, out var t))
                {
                    try { t = _traces[wave] = SequentialTrace.Create(_lens, _system, wave - 1, _semi); }
                    catch (ArgumentException e) { throw new MeritOperandException($"the lens cannot be traced at wavelength {wave}: {FirstLine(e.Message)}"); }
                }
                return (t, _launcher!);
            }
        }

        /// <summary>The path of one ray (1-based wavelength), as the evaluator traces a row's ray.</summary>
        public RayPath Path(int wave, double hx, double hy, double px, double py)
        {
            var (trace, launcher) = Tracer(wave);
            try { return trace.TracePath(launcher.Launch(trace, hx, hy, px, py)); }
            catch (InvalidOperationException e) { throw new MeritOperandException($"{Ray(hx, hy, px, py, wave)} cannot be launched: {FirstLine(e.Message)}"); }
        }
    }

    internal static string Ray(double hx, double hy, double px, double py, int wave) =>
        $"the ray (Hx {F(hx)}, Hy {F(hy)}, Px {F(px)}, Py {F(py)}, wavelength {wave})";

    /// <summary>The hit of <paramref name="path"/> on <paramref name="surface"/>, failing loudly
    /// when the ray failed at or before it.</summary>
    internal static SurfaceHit HitOn(RayPath path, int surface, string ray)
    {
        if (!path.Final.IsOk && path.FailedAtSurface <= surface)
            throw new MeritOperandException($"{ray} failed: {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}.");
        return path.Hits.FirstOrDefault(h => h.Surface == surface)
            ?? throw new MeritOperandException($"{ray} has no point on surface {surface}.");
    }

    /// <summary>The paraxial model at a 0-based wavelength row, as the evaluator builds it (real
    /// image height read as paraxial image height).</summary>
    internal static ParaxialSystem ParaxialAt(MeritRowContext c, int row) => c.Paraxial(row + 1);
}
