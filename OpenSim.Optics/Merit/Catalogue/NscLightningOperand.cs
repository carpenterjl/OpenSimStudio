// OP-85
using System.Globalization;
using OpenSim.Optics.NonSequential.Lightning;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// NSLT, "Non-sequential LightningTrace" (plan item OP-85; docs/Optics-Conventions.md §79; help,
/// "Non-sequential Ray Tracing and Detector Operands", plans/sources/optics/op-55b-help/): "traces
/// mesh rays as defined by the LightningTrace analysis. Surf is the surface number of the
/// Non-sequential surface. Src# refers to the object number of the desired source from which to
/// trace mesh rays. If Src# is zero, rays will be traced from all sources. Ray Samp defines the
/// sampling to use in defining the ray mesh: 0 = Low (1X), 1 = 4X, 2 = 16X, 3 = 64X, 4 = 256X, 5 =
/// 1024X. Edge Samp ... with the same interpretation ... (but with inputs only going from 0 to 4).
/// RT? defines whether mesh rays should be traced (when RT? = 0) or whether all analysis rays should
/// be traced using conventional ray tracing (when RT? = 1). If RT? = 1, then this operand will act
/// just like the NSTR operand with polarization, splitting, and scattering all turned off. An NSDD,
/// NSDE, or NSDP operand with Det# set equal to 0 must be included in the merit function prior to
/// this operand."
/// <para>
/// Columns (a reading, Conventions §10 item 75, OP-88's pattern): Int1 Surf, Int2 Src#, Data1 Ray
/// Samp, Data2 Edge Samp, Data3 RT?. The mesh rays' detector data are added to what the
/// evaluation's detectors hold (OP-88's <see cref="NscMeritSession"/>), so the NSDD, NSDE and NSDP
/// rows after it read them as they read NSTR's. RT? 1 traces like NSTR with Splt?, Scat?, Pol? and
/// IgEr? 0, drawing the next NSTR random stream. Returns 0.
/// </para>
/// </summary>
public static class NscLightningOperand
{
    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        yield return new MeritOperandDefinition
        {
            Code = "NSLT", Name = "Non-sequential LightningTrace", Category = NscOperands.TraceCategory,
            Source = "Non-sequential Ray Tracing and Detector Operands; Lightning Trace", NonSequential = true,
            Description = "Traces LightningTrace's mesh rays of source Src# (0: every source) into the detectors, adding to what they hold: Ray Samp 0 (Low, 1X) to 5 (1024X), Edge Samp 0 to 4. RT? 1 traces the analysis rays conventionally instead, as NSTR with splitting, scattering, polarization and Ignore Errors off. Always 0. A row with Det# 0 (NSDD, NSDE or NSDP) must clear the detectors before it.",
            Int1 = "Surf", Int2 = "Src#", Data = new[] { "Ray Samp", "Edge Samp", "RT?" },
            Compute = Guard(Nslt),
        };
    }

    private static Func<MeritRowContext, double> Guard(Func<MeritRowContext, double> compute) => c =>
    {
        try { return compute(c); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new MeritOperandException(NscMeritSession.OneLine(e.Message));
        }
    };

    private static int Whole(MeritRowContext c, int k, string label, int min, int max)
    {
        double v = c.Row.DataAt(k);
        if (v != Math.Floor(v) || !double.IsFinite(v) || v < min || v > max)
            throw new MeritOperandException($"{label} (Data{k}) is {v.ToString("g9", CultureInfo.InvariantCulture)}; it must be a whole number from {min} to {max} (help, NSLT).");
        return (int)v;
    }

    private static double Nslt(MeritRowContext c)
    {
        if (c.Row.Int1 != 1)
            throw new MeritOperandException($"Surf is {c.Row.Int1}; it must be 1, the non-sequential group of a pure non-sequential system (non-sequential surfaces in a sequential lens, mixed mode, are plan item OP-86).");
        var s = NscMeritSession.Of(c);
        if (!s.ClearedAll)
            throw new MeritOperandException("no row before it clears every detector: \"An NSDD, NSDE, or NSDP operand with Det# set equal to 0 must be included in the merit function prior to this operand\" (help, NSLT). Add one above this row.");
        int source = c.Row.Int2;
        if (source < 0) throw new MeritOperandException($"Src# is {source}; it must be 0 (every source) or a source object's number.");
        if (source > 0)
        {
            s.CheckObject(source, "Src#");
            if (s.Scene.Objects[source - 1].Geometry is not NscSource)
                throw new MeritOperandException($"Src# {source} is a {s.Scene.Objects[source - 1].Geometry.TypeName}, not a source object.");
        }
        int ray = Whole(c, 1, "Ray Samp", 0, NscLightningSettings.MaxLevel);
        int edge = Whole(c, 2, "Edge Samp", 0, 4);   // help: "with inputs only going from 0 to 4"
        double rt = c.Row.DataAt(3);
        if (rt != 0 && rt != 1)
            throw new MeritOperandException($"RT? (Data3) is {rt.ToString("g9", CultureInfo.InvariantCulture)}; it must be 0 (mesh rays) or 1 (conventional ray tracing).");
        var sources = source == 0 ? null : new[] { source };
        if (rt == 1)
        {
            long seed = NscMeritRandom.SeedOf(NscMeritSession.NextTrace(c));
            var run = ProjectNscSources.Trace(s.Context, sources, split: false, usePolarization: false, ignoreErrors: false, seed: seed,
                cancellation: c.Cancellation, maxDegreeOfParallelism: c.MaxDegreeOfParallelism, scatter: false);
            s.Ledger = run.Ledger;
            s.Add(run.Detectors);
            return 0;
        }
        var result = ProjectNscLightning.Trace(s.Context, ray, edge, sources, c.MaxDegreeOfParallelism, c.Cancellation);
        s.Add(result.Detectors);
        return 0;
    }
}
