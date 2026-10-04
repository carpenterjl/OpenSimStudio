namespace OpenSim.Rf.Si.Ibis;

/// <summary>
/// Monotone piecewise-linear V-I table with a value + slope, linear-extrapolated past both
/// ends (so a Newton iterate never runs off a flat table).
///
/// <para>Promoted out of <c>NonlinearLink.IbisDriver</c> so the driver and the switching-
/// coefficient extractor evaluate the buffer's tables through ONE implementation. They must
/// agree exactly: the extractor backs Ku/Kd out of the same currents the driver will then push
/// through the channel, and two interpolators that disagree in the last bit would put a small
/// systematic error into every extracted coefficient. The logic is byte-identical to the
/// version it replaces.</para>
/// </summary>
internal sealed class PwlTable
{
    private readonly double[] _v, _i;

    private PwlTable(double[] v, double[] i) { _v = v; _i = i; }

    public static PwlTable FromTable(IReadOnlyList<IbisIvRow> table, IbisCornerSelection corner)
    {
        if (table.Count == 0) return new PwlTable(Array.Empty<double>(), Array.Empty<double>());
        var pts = table.Select(r => (V: r.VoltageVolts, I: r.CurrentAmps.At(corner) ?? 0))
                       .OrderBy(p => p.V).ToArray();
        // Two rows at ONE voltage have no slope between them: the segment's conductance is
        // 0/0 or ±∞, and it then surfaces as a "singular" or "stalled" Newton step that blames
        // the table's sign convention. The first row at each voltage is kept (the sort is
        // stable); the parser warns about the others.
        var v = new List<double>(pts.Length);
        var i = new List<double>(pts.Length);
        foreach (var p in pts)
        {
            if (v.Count > 0 && p.V - v[^1] <= 1e-12 * Math.Max(1.0, Math.Abs(p.V))) continue;
            v.Add(p.V);
            i.Add(p.I);
        }
        return new PwlTable(v.ToArray(), i.ToArray());
    }

    /// <summary>True when the table is absent — an unpopulated keyword contributes no current.</summary>
    public bool IsEmpty => _v.Length == 0;

    public (double I, double G) Eval(double v)
    {
        int n = _v.Length;
        if (n == 0) return (0, 0);
        if (n == 1) return (_i[0], 0);
        int hi = 1;
        while (hi < n - 1 && _v[hi] < v) hi++;       // segment [hi-1, hi], extrapolate at ends
        double g = (_i[hi] - _i[hi - 1]) / (_v[hi] - _v[hi - 1]);
        return (_i[hi - 1] + g * (v - _v[hi - 1]), g);
    }
}
