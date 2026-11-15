using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Merit;

/// <summary>How a merit function row came out.</summary>
public enum MeritRowStatus
{
    /// <summary>The value was computed.</summary>
    Evaluated,

    /// <summary>The value could not be computed (a failed ray, a surface that does not exist, a
    /// row it reads that failed...); <see cref="MeritRowResult.Error"/> says why. The row is
    /// penalised (<see cref="MeritEvaluationOptions.FailedRowPenalty"/>).</summary>
    Failed,

    /// <summary>Not evaluated: skipped by GOTO, SKIS or SKIN, or after ENDX.</summary>
    Skipped,

    /// <summary>A control or CONF row, evaluated; it has no value.</summary>
    Control,
}

/// <summary>One row's result.</summary>
public sealed record MeritRowResult
{
    /// <summary>The row's number (1 = the first).</summary>
    public required int Number { get; init; }

    public required MeritOperandRow Row { get; init; }

    public required MeritRowStatus Status { get; init; }

    /// <summary>The configuration the row was evaluated in; 0 when it was skipped.</summary>
    public int Configuration { get; init; }

    /// <summary>The value (a bound's value is the target while it holds); NaN unless evaluated.</summary>
    public double Value { get; init; } = double.NaN;

    /// <summary>The share of the merit function in percent: 100·W(V − T)² / Σ W(V − T)².</summary>
    public double Contribution { get; init; }

    /// <summary>This row's entry of the residual vector: √(W/ΣW)·(V − T), so the sum of the
    /// squared residuals is the merit function squared; zero for rows that do not count.</summary>
    public double Residual { get; init; }

    /// <summary>True when the row is in the merit function's sums (evaluated or failed, uses the
    /// target and weight, non-zero weight).</summary>
    public bool Counts { get; init; }

    /// <summary>Why the row failed, naming the row; null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>The evaluation of a merit function.</summary>
public sealed record MeritEvaluation
{
    public required IReadOnlyList<MeritRowResult> Rows { get; init; }

    /// <summary>The merit function, MF = √(Σ W(V − T)² / Σ W), W = |weight| (OpticStudio 2025 R2
    /// help, Modifying the merit function, "Merit function definition"); a failed row stands in
    /// with the penalty for V − T. Zero when no row has a weight.</summary>
    public required double Value { get; init; }

    /// <summary>Σ W over the rows that count.</summary>
    public required double SumOfWeights { get; init; }

    /// <summary>One residual per row, in row order (<see cref="MeritRowResult.Residual"/>).</summary>
    public required IReadOnlyList<double> Residuals { get; init; }

    /// <summary>Whether SKIS and SKIN took the lens as rotationally symmetric, by configuration (index 0 = configuration 1).</summary>
    public required IReadOnlyList<bool> Symmetric { get; init; }

    /// <summary>The number of distinct real rays traced (shared between rows).</summary>
    public int RaysTraced { get; init; }

    /// <summary>The failed rows' errors, one line each.</summary>
    public IReadOnlyList<string> Errors => Rows.Where(r => r.Error is not null).Select(r => r.Error!).ToArray();

    public bool HasFailures => Rows.Any(r => r.Status == MeritRowStatus.Failed);
}

/// <summary>Settings of <see cref="MeritEvaluator.Evaluate"/>.</summary>
public sealed record MeritEvaluationOptions
{
    /// <summary>What a failed row puts in place of V − T. OpticStudio's help does not say how a
    /// row whose ray fails is scored; this program penalises that row alone by this amount
    /// (docs/Optics-Conventions.md §31).</summary>
    public double FailedRowPenalty { get; init; } = 1e6;

    /// <summary>The configuration evaluation starts in, before any CONF row (1 = the first).</summary>
    public int StartConfiguration { get; init; } = 1;

    /// <summary>The thread limit for the ray traces; null for no limit. The result is bitwise
    /// the same at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>
/// Evaluates a merit function (OP-54) over the configurations of <see cref="IMeritConfigurations"/>.
/// <para>
/// In three passes, so the result does not depend on threads: (1) the order of evaluation, from
/// the control rows alone (GOTO, SKIS, SKIN, ENDX, CONF; USYM anywhere makes every
/// configuration symmetric), which never depends on a value; (2) every real ray the evaluated
/// rows read, collected once per configuration, wavelength, field and pupil point and traced in
/// parallel, each into its own slot; (3) the rows in order, reading the traced rays and the
/// values of earlier rows. The merit function's sums add their terms in increasing order of
/// size, so reordering rows that do not read each other does not change it, to the bit.
/// </para>
/// <para>
/// A row that cannot be computed fails alone with a message naming the row; it is penalised
/// (<see cref="MeritEvaluationOptions.FailedRowPenalty"/>) and the other rows are untouched,
/// except math rows that read it, which fail in turn naming it. Problems of the merit function
/// itself — an unknown code, a GOTO to an earlier row, a math row reading a later one, a CONF to a
/// configuration that does not exist — are refused before anything is computed
/// (<see cref="Validate"/>).
/// </para>
/// </summary>
public static class MeritEvaluator
{
    /// <summary>The problems of the merit function itself, one line each naming the row; empty
    /// when it can be evaluated.</summary>
    public static IReadOnlyList<string> Validate(MeritFunction function, MeritOperandRegistry registry, int configurationCount)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(registry);
        var problems = new List<string>();
        int count = function.Rows.Count;
        for (int n = 1; n <= count; n++)
        {
            var row = function.Row(n);
            var d = registry.Find(row.Code);
            if (d is null)
            {
                problems.Add($"Row {n}: there is no operand '{row.Code}'.");
                continue;
            }
            string at = $"Row {n} ({d.Code})";
            switch (d.Control)
            {
                case MeritControl.GoTo or MeritControl.SkipIfSymmetric or MeritControl.SkipIfNotSymmetric:
                    if (row.Int1 <= n || row.Int1 > count)
                        problems.Add($"{at}: Op# {row.Int1} must be a later row, {n + 1} to {count}.");
                    break;
                case MeritControl.Configuration:
                    if (row.Int1 < 1 || row.Int1 > configurationCount)
                        problems.Add($"{at}: Cfg# {row.Int1} is not a configuration; there {(configurationCount == 1 ? "is 1" : $"are {configurationCount}")}.");
                    break;
            }
            if (d.References is null) continue;
            foreach (int r in d.References(row).Distinct())
            {
                if (r < 1 || r >= n)
                {
                    problems.Add($"{at}: it reads row {r}; a math operand reads earlier rows only, 1 to {n - 1}.");
                    continue;
                }
                var target = registry.Find(function.Row(r).Code);
                if (target is { Control: not MeritControl.None })
                    problems.Add($"{at}: it reads row {r}, a {target.Code} row, which has no value.");
            }
        }
        return problems;
    }

    /// <summary>True when the lens is rotationally symmetric for SKIS and SKIN: no tilt, decenter
    /// or coordinate break, and every surface traced is a Standard (conic) surface, a radial
    /// asphere or a Paraxial surface. Other surface types count as not symmetric, so SKIS never
    /// skips rows on a lens it cannot vouch for. The help gives no test of its own; this one is
    /// this program's (docs/Optics-Conventions.md §31).</summary>
    public static bool IsRotationallySymmetric(SequentialLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (SurfacePlacement.HasPlacementData(lens)) return false;
        return lens.Surfaces.All(s => s.IsIgnored || (s.Shape is null or RadialSagShape or ParaxialShape)
            && !Sequential.Composite.CompositeSurfaces.HasActiveAddOns(s));   // OP-32: add-ons may be placed off axis
    }

    /// <summary>Evaluates <paramref name="function"/>. Throws <see cref="ArgumentException"/>
    /// listing the problems of <see cref="Validate"/>; a row that cannot be computed fails alone.</summary>
    public static MeritEvaluation Evaluate(MeritFunction function, IMeritConfigurations configurations,
        MeritOperandRegistry? registry = null, MeritEvaluationOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(configurations);
        registry ??= MeritOperandRegistry.CreateDefault();
        options ??= new MeritEvaluationOptions();
        if (configurations.Count < 1) throw new ArgumentException("There must be at least one configuration.", nameof(configurations));
        var problems = Validate(function, registry, configurations.Count).ToList();
        if (options.StartConfiguration < 1 || options.StartConfiguration > configurations.Count)
            problems.Add($"The start configuration {options.StartConfiguration} is not one of the {configurations.Count}.");
        if (!(options.FailedRowPenalty >= 0) || double.IsInfinity(options.FailedRowPenalty))
            problems.Add("The failed-row penalty must be finite and not negative.");
        if (problems.Count > 0)
            throw new ArgumentException("Merit function:" + Environment.NewLine + string.Join(Environment.NewLine, problems));

        int count = function.Rows.Count;
        var defs = function.Rows.Select(r => registry.Get(r.Code)).ToArray();
        var states = new ConfigurationState?[configurations.Count + 1];
        ConfigurationState State(int c) => states[c] ??= new ConfigurationState(configurations, c);

        // ---- Pass 1: which rows run, in which configuration. ----
        bool assumeSymmetric = defs.Any(d => d.Control == MeritControl.AssumeSymmetry);
        var configOf = new int[count + 1];          // 0 = not evaluated
        int configuration = options.StartConfiguration;
        for (int n = 1; n <= count; n++)
        {
            var d = defs[n - 1];
            configOf[n] = configuration;
            if (d.Control == MeritControl.End) break;
            int? jump = d.Control switch
            {
                MeritControl.GoTo => function.Row(n).Int1,
                MeritControl.SkipIfSymmetric when assumeSymmetric || State(configuration).Symmetric => function.Row(n).Int1,
                MeritControl.SkipIfNotSymmetric when !(assumeSymmetric || State(configuration).Symmetric) => function.Row(n).Int1,
                _ => null,
            };
            if (d.Control == MeritControl.Configuration) configuration = function.Row(n).Int1;
            if (jump is { } to) n = to - 1;         // Validate made it a later row
        }

        // ---- Pass 2: the rays, each traced once. ----
        var keys = new List<(int Configuration, MeritRayRequest Ray)>();
        var keyIndex = new Dictionary<(int, MeritRayRequest), int>();
        for (int n = 1; n <= count; n++)
        {
            if (configOf[n] == 0 || defs[n - 1].Ray is not { } rayOf) continue;
            var state = State(configOf[n]);
            var request = rayOf(function.Row(n));
            if (state.System is not { } system || request.Wavelength < 1 || request.Wavelength > system.Wavelengths.Wavelengths.Count)
                continue;                           // the row fails in pass 3, naming why
            var key = (configOf[n], request);
            if (keyIndex.TryAdd(key, keys.Count)) keys.Add(key);
        }
        foreach (var (c, request) in keys) State(c).PrepareTrace(request.Wavelength, options.MaxDegreeOfParallelism, cancellation);
        var traced = ParallelSlots.Compute(keys.Count, options.MaxDegreeOfParallelism, i =>
        {
            var (c, request) = keys[i];
            return states[c]!.TraceRay(request);
        }, cancellation);

        // ---- Pass 3: the rows in order. ----
        var results = new MeritRowResult[count];
        for (int n = 1; n <= count; n++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = function.Row(n);
            var d = defs[n - 1];
            int c = configOf[n];
            if (c == 0)
            {
                results[n - 1] = new MeritRowResult { Number = n, Row = row, Status = MeritRowStatus.Skipped };
                continue;
            }
            if (d.Control != MeritControl.None)
            {
                results[n - 1] = new MeritRowResult { Number = n, Row = row, Status = MeritRowStatus.Control, Configuration = c };
                continue;
            }
            string at = $"Row {n} ({d.Code}{(configurations.Count > 1 ? $", configuration {c}" : "")})";
            try
            {
                var state = State(c);
                if (state.Error is { } error) throw new MeritOperandException(error);
                var context = new MeritRowContext(row, n, c, state.Lens!, state.System!,
                    number => ValueOf(results, number),
                    request => keyIndex.TryGetValue((c, request), out int i)
                        ? traced[i].Path ?? throw new MeritOperandException(traced[i].Error!)
                        : throw new InvalidOperationException($"{at}: its ray was not collected for tracing."),
                    state.Paraxial) { Catalogs = configurations.Catalogs };   // OP-59: the catalogs for RGLA
                double value = d.Compute!(context);
                if (!double.IsFinite(value)) throw new MeritOperandException($"the value is {value.ToString(CultureInfo.InvariantCulture)}.");
                results[n - 1] = new MeritRowResult { Number = n, Row = row, Status = MeritRowStatus.Evaluated, Configuration = c, Value = value };
            }
            catch (MeritOperandException e)
            {
                results[n - 1] = new MeritRowResult { Number = n, Row = row, Status = MeritRowStatus.Failed, Configuration = c, Error = $"{at}: {e.Message}" };
            }
        }

        return Sum(results, defs, options.FailedRowPenalty, states.Skip(1).Select((s, i) => assumeSymmetric || (s?.Symmetric ?? false)).ToArray(), keys.Count);
    }

    private static double ValueOf(MeritRowResult[] results, int number)
    {
        var r = results[number - 1];
        return r.Status switch
        {
            MeritRowStatus.Evaluated => r.Value,
            MeritRowStatus.Failed => throw new MeritOperandException($"it reads row {number}, which failed."),
            _ => throw new MeritOperandException($"it reads row {number}, which was not evaluated."),
        };
    }

    /// <summary>MF² = Σ W(V − T)² / Σ W and the percent contributions (help: Modifying the merit
    /// function; Notes on Operand Weights). Each sum adds its terms smallest first.</summary>
    private static MeritEvaluation Sum(MeritRowResult[] results, MeritOperandDefinition[] defs, double penalty,
        IReadOnlyList<bool> symmetric, int rays)
    {
        int count = results.Length;
        var weight = new double[count];
        var term = new double[count];
        var difference = new double[count];
        for (int i = 0; i < count; i++)
        {
            var r = results[i];
            if (!defs[i].UsesTargetAndWeight || r.Status is not (MeritRowStatus.Evaluated or MeritRowStatus.Failed)) continue;
            double w = Math.Abs(r.Row.Weight);
            if (w == 0) continue;
            difference[i] = r.Status == MeritRowStatus.Failed ? penalty : r.Value - r.Row.Target;
            weight[i] = w;
            term[i] = w * difference[i] * difference[i];
        }
        double sumW = SortedSum(weight), sumTerms = SortedSum(term);
        double value = sumW > 0 ? Math.Sqrt(sumTerms / sumW) : 0;
        var residuals = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (weight[i] == 0) continue;
            residuals[i] = Math.Sqrt(weight[i] / sumW) * difference[i];
            results[i] = results[i] with
            {
                Counts = true,
                Residual = residuals[i],
                Contribution = sumTerms > 0 ? 100 * term[i] / sumTerms : 0,
            };
        }
        return new MeritEvaluation
        {
            Rows = results, Value = value, SumOfWeights = sumW, Residuals = residuals, Symmetric = symmetric, RaysTraced = rays,
        };
    }

    /// <summary>The sum of non-negative terms, smallest first: the same bits in any order.</summary>
    private static double SortedSum(double[] terms)
    {
        var sorted = (double[])terms.Clone();
        Array.Sort(sorted);
        double s = 0;
        foreach (double t in sorted) s += t;
        return s;
    }

    /// <summary>A configuration's lens, system data, symmetry, traces and paraxial models, built
    /// on first use. Traces are prepared sequentially before the parallel pass and only read in it.</summary>
    private sealed class ConfigurationState
    {
        private readonly int _number;
        private RayLauncher? _launcher;
        private string? _traceError;
        private bool _tracePrepared;
        private IReadOnlyList<double>? _semiDiameters;
        private readonly Dictionary<int, SequentialTrace> _traces = new();
        private readonly Dictionary<int, string> _traceErrors = new();
        private readonly Dictionary<int, ParaxialSystem> _paraxial = new();

        public ConfigurationState(IMeritConfigurations configurations, int number)
        {
            _number = number;
            try
            {
                Lens = configurations.LensFor(number);
                System = configurations.SystemFor(number);
                Symmetric = IsRotationallySymmetric(Lens);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Error = $"configuration {number} cannot be built: {e.Message}";
            }
        }

        public SequentialLens? Lens { get; }
        public OpticalSystemData? System { get; }
        public bool Symmetric { get; }
        public string? Error { get; }

        /// <summary>The launcher, the automatic semi-diameters and the trace at a wavelength
        /// (1-based), each built once; failures are kept and reported by the rays that need them.</summary>
        public void PrepareTrace(int wavelength, int? dop, CancellationToken cancellation)
        {
            if (Error is not null) return;
            if (!_tracePrepared)
            {
                _tracePrepared = true;
                try
                {
                    if (Lens!.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
                        _semiDiameters = SemiDiameterSizing.Compute(Lens, System!, maxDegreeOfParallelism: dop, cancellation: cancellation).Clear;
                    _launcher = new RayLauncher(Lens, System!);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    _traceError = $"the lens cannot be traced in configuration {_number}: {e.Message}";
                }
            }
            if (_traceError is not null || _traces.ContainsKey(wavelength) || _traceErrors.ContainsKey(wavelength)) return;
            try { _traces[wavelength] = SequentialTrace.Create(Lens!, System!, wavelength - 1, _semiDiameters); }
            catch (ArgumentException e) { _traceErrors[wavelength] = $"the lens cannot be traced at wavelength {wavelength}: {e.Message}"; }
        }

        /// <summary>One ray; thread-safe (reads only what <see cref="PrepareTrace"/> built).</summary>
        public (RayPath? Path, string? Error) TraceRay(MeritRayRequest r)
        {
            if (Error is not null) return (null, Error);
            if (_traceError is not null) return (null, _traceError);
            if (_traceErrors.TryGetValue(r.Wavelength, out var error)) return (null, error);
            var trace = _traces[r.Wavelength];
            try { return (trace.TracePath(_launcher!.Launch(trace, r.Hx, r.Hy, r.Px, r.Py)), null); }
            catch (InvalidOperationException e) { return (null, e.Message); }
        }

        /// <summary>The paraxial model at a wavelength (1-based); a real image height field is
        /// read as the paraxial one, as the ray launcher does.</summary>
        public ParaxialSystem Paraxial(int wavelength)
        {
            if (_paraxial.TryGetValue(wavelength, out var p)) return p;
            try
            {
                var system = System!.Fields.Type == FieldType.RealImageHeight
                    ? System with { Fields = System.Fields with { Type = FieldType.ParaxialImageHeight } }
                    : System;
                p = ParaxialSystem.Create(Lens!.ToParaxialLens(), system, wavelength - 1);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                throw new MeritOperandException($"the paraxial model cannot be built: {e.Message}");
            }
            _paraxial[wavelength] = p;
            return p;
        }
    }
}
