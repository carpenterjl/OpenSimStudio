using OpenSim.Geometry.Step.Part21;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// Length-unit context resolved from a STEP file: the factor converting the file's model
/// coordinates to meters, and the exporter's stated coordinate uncertainty (already in
/// meters) when present. The uncertainty later anchors the B-spline inversion acceptance
/// tolerance — the file's own accuracy statement, not a magic epsilon.
/// </summary>
public sealed record StepUnitContext(double MetersPerUnit, double? UncertaintyMeters);

/// <summary>
/// Resolves the length unit from the GLOBAL_UNIT_ASSIGNED_CONTEXT chain. STL is unitless
/// but STEP is not, and everything downstream of the importer assumes meters — so a
/// missing or ambiguous unit context is a hard failure ("refusing to guess"), never a
/// silent 1000× error.
/// </summary>
public static class StepUnits
{
    /// <summary>
    /// Resolves the file-wide length unit. Multiple unit contexts are accepted only when
    /// they agree; disagreeing contexts (one representation in mm, another in inches) are
    /// rejected loudly because the importer applies a single scale to all geometry.
    /// </summary>
    public static StepUnitContext Resolve(StepFile file)
    {
        double? scale = null;
        int scaleSource = 0;
        double? uncertainty = null;

        foreach (var (id, inst) in file.Instances)
        {
            var unitContext = inst.Find("GLOBAL_UNIT_ASSIGNED_CONTEXT");
            if (unitContext is not null)
            {
                double? contextScale = null;
                foreach (var unitRef in unitContext.Args[0].AsList())
                {
                    double? s = TryLengthUnitScale(file, file.Get(unitRef.AsRef()), depth: 0);
                    if (s is null) continue;
                    if (contextScale is not null && !Agrees(contextScale.Value, s.Value))
                        throw new StepImportException(
                            $"#{id}: unit context declares two disagreeing length units " +
                            $"({contextScale} and {s} m); cannot determine the length unit");
                    contextScale = s;
                }
                if (contextScale is not null)
                {
                    if (scale is not null && !Agrees(scale.Value, contextScale.Value))
                        throw new StepImportException(
                            $"ambiguous length units: #{scaleSource} declares {scale} m/unit but " +
                            $"#{id} declares {contextScale} m/unit; refusing to guess");
                    scale = contextScale;
                    scaleSource = id;
                }
            }

            var uncContext = inst.Find("GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT");
            if (uncContext is not null)
            {
                foreach (var uncRef in uncContext.Args[0].AsList())
                {
                    double? u = TryUncertaintyMeters(file, file.Get(uncRef.AsRef()));
                    // The loosest stated accuracy wins — conservative for tolerance anchoring.
                    if (u is not null && (uncertainty is null || u > uncertainty)) uncertainty = u;
                }
            }
        }

        if (scale is null)
            throw new StepImportException(
                "cannot determine the length unit: no GLOBAL_UNIT_ASSIGNED_CONTEXT with a " +
                "length unit was found; refusing to guess");
        return new StepUnitContext(scale.Value, uncertainty);
    }

    private static bool Agrees(double a, double b) => Math.Abs(a - b) <= 1e-12 * Math.Max(a, b);

    /// <summary>
    /// Meters per unit for a length-unit instance; null when the instance is not a length
    /// unit at all (mass, plane angle, …). A unit that IS declared as a length unit but
    /// cannot be interpreted fails loudly.
    /// </summary>
    private static double? TryLengthUnitScale(StepFile file, StepInstance unit, int depth)
    {
        if (depth > 8)
            throw new StepImportException($"#{unit.Id}: circular CONVERSION_BASED_UNIT chain");

        bool declaredLength = unit.Has("LENGTH_UNIT");
        var si = unit.Find("SI_UNIT");
        if (si is not null)
        {
            string name = si.Args[1].AsEnum();
            if (!name.Equals("METRE", StringComparison.OrdinalIgnoreCase))
                return declaredLength
                    ? throw new StepImportException(
                        $"#{unit.Id}: length unit is SI_UNIT .{name}. — only METRE-based lengths are meaningful")
                    : null; // a non-length SI unit (kilogram, radian, …)
            double prefix = si.Args[0] switch
            {
                StepValue.Null => 1.0,
                StepValue.Enumeration e => SiPrefix(e.Value, unit.Id),
                var other => throw new StepImportException($"#{unit.Id}: unexpected SI prefix {other}")
            };
            return prefix;
        }

        var conversion = unit.Find("CONVERSION_BASED_UNIT");
        if (conversion is not null)
        {
            if (!declaredLength) return null; // e.g. a degree (plane angle) conversion unit
            var measure = file.Get(conversion.Args[1].AsRef());
            var mwu = measure.Find("MEASURE_WITH_UNIT")
                      ?? measure.Find("LENGTH_MEASURE_WITH_UNIT")
                      ?? throw new StepImportException(
                          $"#{measure.Id}: expected MEASURE_WITH_UNIT for conversion unit #{unit.Id}");
            double value = mwu.Args[0] switch
            {
                StepValue.Typed t => t.Args[0].AsReal(), // LENGTH_MEASURE(25.4)
                var plain => plain.AsReal()
            };
            var inner = TryLengthUnitScale(file, file.Get(mwu.Args[1].AsRef()), depth + 1)
                        ?? throw new StepImportException(
                            $"#{unit.Id}: conversion unit's base #{mwu.Args[1].AsRef()} is not a length unit");
            return value * inner;
        }

        return declaredLength
            ? throw new StepImportException(
                $"#{unit.Id}: length unit is neither SI_UNIT nor CONVERSION_BASED_UNIT — unsupported")
            : null;
    }

    private static double? TryUncertaintyMeters(StepFile file, StepInstance inst)
    {
        var rec = inst.Find("UNCERTAINTY_MEASURE_WITH_UNIT") ?? inst.Find("MEASURE_WITH_UNIT");
        if (rec is null) return null;
        double value = rec.Args[0] switch
        {
            StepValue.Typed t => t.Args[0].AsReal(), // LENGTH_MEASURE(0.01)
            var plain => plain.AsReal()
        };
        double? unitScale = TryLengthUnitScale(file, file.Get(rec.Args[1].AsRef()), depth: 0);
        return unitScale is null ? null : value * unitScale.Value;
    }

    private static double SiPrefix(string prefix, int id) => prefix.ToUpperInvariant() switch
    {
        "EXA" => 1e18, "PETA" => 1e15, "TERA" => 1e12, "GIGA" => 1e9, "MEGA" => 1e6,
        "KILO" => 1e3, "HECTO" => 1e2, "DECA" => 1e1,
        "DECI" => 1e-1, "CENTI" => 1e-2, "MILLI" => 1e-3, "MICRO" => 1e-6,
        "NANO" => 1e-9, "PICO" => 1e-12, "FEMTO" => 1e-15, "ATTO" => 1e-18,
        var p => throw new StepImportException($"#{id}: unknown SI prefix .{p}.")
    };
}
