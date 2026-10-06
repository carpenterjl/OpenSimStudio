using System.Globalization;
using System.Text;

namespace OpenSim.Core.Studies;

/// <summary>One swept quantity and the values it takes.</summary>
public sealed record SweepParameter
{
    public required string Name { get; init; }

    /// <summary>The values, in the order they are run. The nominal is <see cref="Nominal"/>.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>The design value, used by the one-at-a-time and corner plans for the
    /// parameters that are not being varied. Defaults to the middle value.</summary>
    public double? NominalValue { get; init; }

    public string? Unit { get; init; }

    public double Nominal => NominalValue ?? Values[Values.Count / 2];

    /// <summary>Evenly spaced values from min to max.</summary>
    public static SweepParameter Linear(string name, double min, double max, int points, string? unit = null)
    {
        if (points < 1) throw new ArgumentOutOfRangeException(nameof(points));
        var values = new double[points];
        for (int i = 0; i < points; i++)
            values[i] = points == 1 ? min : min + (max - min) * i / (points - 1);
        return new SweepParameter { Name = name, Values = values, Unit = unit };
    }

    /// <summary>A nominal with a symmetric tolerance: nominal·(1 ± fraction) at the ends,
    /// evenly spaced between, the nominal itself among them when the count is odd.</summary>
    public static SweepParameter Tolerance(string name, double nominal, double fraction, int points = 3, string? unit = null)
    {
        if (!(fraction >= 0)) throw new ArgumentOutOfRangeException(nameof(fraction));
        var p = Linear(name, nominal * (1 - fraction), nominal * (1 + fraction), points, unit);
        return p with { NominalValue = nominal };
    }

    /// <summary>A nominal with an absolute ± band.</summary>
    public static SweepParameter PlusMinus(string name, double nominal, double delta, int points = 3, string? unit = null)
    {
        if (!(delta >= 0)) throw new ArgumentOutOfRangeException(nameof(delta));
        var p = Linear(name, nominal - delta, nominal + delta, points, unit);
        return p with { NominalValue = nominal };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("A sweep parameter needs a name.");
        if (Values.Count == 0) throw new InvalidOperationException($"Sweep parameter '{Name}' has no values.");
        if (Values.Any(v => !double.IsFinite(v))) throw new InvalidOperationException($"Sweep parameter '{Name}' has a non-finite value.");
    }
}

/// <summary>How the parameter values are combined into runs.</summary>
public enum SweepMode
{
    /// <summary>Every combination of every parameter's values.</summary>
    FullFactorial,

    /// <summary>The nominal point, then each parameter alone through its values with the
    /// others at nominal: the sensitivity study.</summary>
    OneAtATime,

    /// <summary>The nominal point and every corner (each parameter at its smallest or
    /// largest value): the tolerance stack-up's worst cases.</summary>
    Corners
}

/// <summary>One run of a sweep: the parameter values it was made at.</summary>
public sealed record SweepPoint(int Index, IReadOnlyDictionary<string, double> Values)
{
    public double this[string name] => Values[name];

    /// <summary>The parameter this point varies alone (one-at-a-time plans); null at the
    /// nominal point and in other plans.</summary>
    public string? Varied { get; init; }
}

/// <summary>The parameters and the plan.</summary>
public sealed record SweepPlan
{
    public required IReadOnlyList<SweepParameter> Parameters { get; init; }
    public SweepMode Mode { get; init; } = SweepMode.FullFactorial;

    public void Validate()
    {
        if (Parameters.Count == 0) throw new InvalidOperationException("A sweep needs at least one parameter.");
        foreach (var p in Parameters) p.Validate();
        if (Parameters.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Parameters.Count)
            throw new InvalidOperationException("Sweep parameter names must be distinct.");
    }

    public int Count => Mode switch
    {
        SweepMode.FullFactorial => Parameters.Aggregate(1, (n, p) => n * p.Values.Count),
        SweepMode.OneAtATime => 1 + Parameters.Sum(p => p.Values.Count),
        _ => 1 + (1 << Parameters.Count)
    };

    private Dictionary<string, double> Nominal() => Parameters.ToDictionary(p => p.Name, p => p.Nominal);

    public IEnumerable<SweepPoint> Points()
    {
        Validate();
        int index = 0;
        switch (Mode)
        {
            case SweepMode.FullFactorial:
            {
                var counters = new int[Parameters.Count];
                while (true)
                {
                    var values = new Dictionary<string, double>();
                    for (int i = 0; i < Parameters.Count; i++) values[Parameters[i].Name] = Parameters[i].Values[counters[i]];
                    yield return new SweepPoint(index++, values);
                    int k = Parameters.Count - 1;
                    while (k >= 0 && ++counters[k] == Parameters[k].Values.Count) counters[k--] = 0;
                    if (k < 0) yield break;
                }
            }
            case SweepMode.OneAtATime:
                yield return new SweepPoint(index++, Nominal());
                foreach (var p in Parameters)
                    foreach (double v in p.Values)
                    {
                        var values = Nominal();
                        values[p.Name] = v;
                        yield return new SweepPoint(index++, values) { Varied = p.Name };
                    }
                yield break;
            default:
                yield return new SweepPoint(index++, Nominal());
                for (int corner = 0; corner < 1 << Parameters.Count; corner++)
                {
                    var values = new Dictionary<string, double>();
                    for (int i = 0; i < Parameters.Count; i++)
                        values[Parameters[i].Name] = (corner >> i & 1) == 0 ? Parameters[i].Values.Min() : Parameters[i].Values.Max();
                    yield return new SweepPoint(index++, values);
                }
                yield break;
        }
    }
}

/// <summary>One run's outputs, or why it failed.</summary>
public sealed record SweepRow(SweepPoint Point, IReadOnlyDictionary<string, double> Outputs, string? Error = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>The sensitivity of one output to one parameter from a one-at-a-time plan.</summary>
/// <param name="Slope">Δoutput/Δparameter between the parameter's smallest and largest value.</param>
/// <param name="Normalized">(Δoutput/output_nominal)/(Δparameter/parameter_nominal): percent per percent.</param>
/// <param name="Range">Largest minus smallest output over the parameter's values.</param>
public sealed record Sensitivity(string Parameter, string Output, double Slope, double Normalized, double Range);

public sealed record SweepResult(SweepPlan Plan, IReadOnlyList<SweepRow> Rows)
{
    public IReadOnlyList<string> OutputNames =>
        Rows.Where(r => r.Succeeded).SelectMany(r => r.Outputs.Keys).Distinct().ToList();

    public int Failures => Rows.Count(r => !r.Succeeded);

    /// <summary>The smallest and largest value an output took, with the points they came from.</summary>
    public (double Min, SweepPoint AtMin, double Max, SweepPoint AtMax) Range(string output)
    {
        var rows = Rows.Where(r => r.Succeeded && r.Outputs.ContainsKey(output)).ToList();
        if (rows.Count == 0) throw new InvalidOperationException($"No run produced '{output}'.");
        var min = rows.MinBy(r => r.Outputs[output])!;
        var max = rows.MaxBy(r => r.Outputs[output])!;
        return (min.Outputs[output], min.Point, max.Outputs[output], max.Point);
    }

    /// <summary>The row at the plan's nominal point: the first row of a one-at-a-time or
    /// corner plan, the middle row of a factorial.</summary>
    public SweepRow? Nominal()
    {
        if (Rows.Count == 0) return null;
        if (Plan.Mode != SweepMode.FullFactorial) return Rows[0];
        var nominal = Plan.Parameters.ToDictionary(p => p.Name, p => p.Nominal);
        return Rows.FirstOrDefault(r => nominal.All(kv => r.Point.Values[kv.Key] == kv.Value));
    }

    /// <summary>Sensitivities from a one-at-a-time plan; empty for the other plans.</summary>
    public IReadOnlyList<Sensitivity> Sensitivities()
    {
        var result = new List<Sensitivity>();
        if (Plan.Mode != SweepMode.OneAtATime) return result;
        var nominal = Nominal();
        if (nominal is null || !nominal.Succeeded) return result;
        foreach (var p in Plan.Parameters)
        {
            var rows = Rows.Where(r => r.Succeeded && r.Point.Varied == p.Name).OrderBy(r => r.Point[p.Name]).ToList();
            if (rows.Count < 2) continue;
            double dp = rows[^1].Point[p.Name] - rows[0].Point[p.Name];
            foreach (string output in OutputNames)
            {
                if (!rows.All(r => r.Outputs.ContainsKey(output)) || !nominal.Outputs.TryGetValue(output, out double o0)) continue;
                double dOut = rows[^1].Outputs[output] - rows[0].Outputs[output];
                double slope = dp != 0 ? dOut / dp : double.NaN;
                double normalized = dp != 0 && o0 != 0 && p.Nominal != 0 ? (dOut / o0) / (dp / p.Nominal) : double.NaN;
                double range = rows.Max(r => r.Outputs[output]) - rows.Min(r => r.Outputs[output]);
                result.Add(new Sensitivity(p.Name, output, slope, normalized, range));
            }
        }
        return result;
    }

    /// <summary>A CSV table: one row per run, the parameters then the outputs; a failed run
    /// carries its error in the last column.</summary>
    public string ToCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        var outputs = OutputNames;
        var sb = new StringBuilder();
        sb.Append("run,");
        sb.Append(string.Join(",", Plan.Parameters.Select(p => Escape(p.Unit is null ? p.Name : $"{p.Name} [{p.Unit}]"))));
        if (outputs.Count > 0) sb.Append(',').Append(string.Join(",", outputs.Select(Escape)));
        sb.Append(",error\r\n");
        foreach (var row in Rows)
        {
            sb.Append(row.Point.Index.ToString(ci)).Append(',');
            sb.Append(string.Join(",", Plan.Parameters.Select(p => row.Point[p.Name].ToString("R", ci))));
            foreach (string o in outputs)
                sb.Append(',').Append(row.Outputs.TryGetValue(o, out double v) ? v.ToString("R", ci) : "");
            sb.Append(',').Append(Escape(row.Error ?? "")).Append("\r\n");
        }
        return sb.ToString();
    }

    public IReadOnlyList<string> Describe()
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Sweep: {Plan.Mode}, {Rows.Count} run(s) over {string.Join(", ", Plan.Parameters.Select(p => $"{p.Name} ({p.Values.Count} values)"))}" +
            (Failures > 0 ? $"; {Failures} failed." : ".")
        };
        foreach (string output in OutputNames)
        {
            var (min, atMin, max, atMax) = Range(output);
            lines.Add($"  {output}: {min.ToString("g5", ci)} at [{Point(atMin)}] to {max.ToString("g5", ci)} at [{Point(atMax)}]");
        }
        foreach (var s in Sensitivities())
            lines.Add($"  d({s.Output})/d({s.Parameter}) = {s.Slope.ToString("g4", ci)}" +
                      (double.IsNaN(s.Normalized) ? "" : $" ({(s.Normalized * 100).ToString("g3", ci)} % per % of nominal)") +
                      $", range {s.Range.ToString("g4", ci)}");
        return lines;

        string Point(SweepPoint p) => string.Join(", ", Plan.Parameters.Select(q => $"{q.Name} = {p[q.Name].ToString("g4", ci)}"));
    }

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// Runs a study at every point of a <see cref="SweepPlan"/>. The study is a function from
/// parameter values to named outputs; it is what ties the plan to a solver, an extractor or
/// a calculator, and it is the caller's — the sweep knows nothing of physics. A run that
/// throws is recorded as a failed row and the sweep goes on.
/// </summary>
public static class ParameterSweep
{
    public static SweepResult Run(SweepPlan plan,
        Func<IReadOnlyDictionary<string, double>, IReadOnlyDictionary<string, double>> study,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(study);
        plan.Validate();
        var points = plan.Points().ToList();
        var rows = new List<SweepRow>(points.Count);
        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outputs = study(point.Values);
                rows.Add(new SweepRow(point, new Dictionary<string, double>(outputs)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rows.Add(new SweepRow(point, new Dictionary<string, double>(), ex.Message));
            }
            progress?.Report((rows.Count, points.Count));
        }
        return new SweepResult(plan, rows);
    }
}
