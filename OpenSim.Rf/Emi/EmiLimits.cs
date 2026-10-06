using System.Globalization;
using System.Text;

namespace OpenSim.Rf.Emi;

/// <summary>One straight piece of a limit line: from f0 to f1 the limit runs from L0 to L1 dBµV
/// (or dBµV/m), linear in log f — how the standards draw a sloping limit.</summary>
public sealed record LimitSegment(double F0, double F1, double L0, double L1);

/// <summary>A named limit line (one detector, one class, one distance).</summary>
public sealed record EmiLimitLine(string Name, string Unit, IReadOnlyList<LimitSegment> Segments, string Source)
{
    /// <summary>The limit at <paramref name="frequencyHz"/>, or null outside the line. At a band
    /// edge the lower limit applies (the standards' rule for a step).</summary>
    public double? At(double frequencyHz)
    {
        double? best = null;
        foreach (var s in Segments)
        {
            if (frequencyHz < s.F0 || frequencyHz > s.F1) continue;
            double t = s.F1 == s.F0 ? 0 : Math.Log(frequencyHz / s.F0) / Math.Log(s.F1 / s.F0);
            double value = s.L0 + t * (s.L1 - s.L0);
            best = best is null ? value : Math.Min(best.Value, value);
        }
        return best;
    }

    public double MinFrequency => Segments.Min(s => s.F0);
    public double MaxFrequency => Segments.Max(s => s.F1);

    /// <summary>"f0_MHz,f1_MHz,limit0,limit1" per segment.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder($"# {Name} [{Unit}] — {Source}\nf0_MHz,f1_MHz,limit0,limit1\n");
        foreach (var s in Segments)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{s.F0 / 1e6},{s.F1 / 1e6},{s.L0},{s.L1}\n"));
        return sb.ToString();
    }

    public static EmiLimitLine ParseCsv(string text, string name, string unit, string source)
    {
        var segments = new List<LimitSegment>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || char.IsLetter(line[0])) continue;
            var cells = line.Split(',');
            if (cells.Length < 4) throw new FormatException($"Expected four numbers: '{line}'.");
            double P(int i) => double.Parse(cells[i], CultureInfo.InvariantCulture);
            var s = new LimitSegment(P(0) * 1e6, P(1) * 1e6, P(2), P(3));
            if (!(s.F1 >= s.F0 && s.F0 > 0)) throw new FormatException($"Bad frequency range: '{line}'.");
            segments.Add(s);
        }
        if (segments.Count == 0) throw new FormatException("No segments.");
        return new EmiLimitLine(name, unit, segments, source);
    }
}

/// <summary>
/// Limit lines as they are commonly reproduced from CISPR 32 (conducted, mains port; radiated,
/// below 1 GHz) and FCC Part 15.109 (radiated, Class B). The standards' texts were not on this
/// machine: the numbers are typed from their common reproductions and must be checked against
/// your copy — every line exports to CSV and a checked one loads back in its place.
/// </summary>
public static class EmiLimits
{
    public const string Unverified =
        "typed as commonly reproduced, not checked against the standard's text — verify against your copy";

    private static EmiLimitLine Line(string name, string unit, params (double F0MHz, double F1MHz, double L0, double L1)[] parts) =>
        new(name, unit, parts.Select(p => new LimitSegment(p.F0MHz * 1e6, p.F1MHz * 1e6, p.L0, p.L1)).ToList(),
            "CISPR 32 / FCC Part 15 " + Unverified);

    public static EmiLimitLine ConductedClassBQuasiPeak { get; } = Line("CISPR 32 Class B conducted, quasi-peak", "dBµV",
        (0.15, 0.5, 66, 56), (0.5, 5, 56, 56), (5, 30, 60, 60));
    public static EmiLimitLine ConductedClassBAverage { get; } = Line("CISPR 32 Class B conducted, average", "dBµV",
        (0.15, 0.5, 56, 46), (0.5, 5, 46, 46), (5, 30, 50, 50));
    public static EmiLimitLine ConductedClassAQuasiPeak { get; } = Line("CISPR 32 Class A conducted, quasi-peak", "dBµV",
        (0.15, 0.5, 79, 79), (0.5, 30, 73, 73));
    public static EmiLimitLine ConductedClassAAverage { get; } = Line("CISPR 32 Class A conducted, average", "dBµV",
        (0.15, 0.5, 66, 66), (0.5, 30, 60, 60));

    public static EmiLimitLine RadiatedClassB10m { get; } = Line("CISPR 32 Class B radiated at 10 m, quasi-peak", "dBµV/m",
        (30, 230, 30, 30), (230, 1000, 37, 37));
    public static EmiLimitLine RadiatedClassB3m { get; } = Line("CISPR 32 Class B radiated at 3 m, quasi-peak", "dBµV/m",
        (30, 230, 40, 40), (230, 1000, 47, 47));
    public static EmiLimitLine RadiatedClassA10m { get; } = Line("CISPR 32 Class A radiated at 10 m, quasi-peak", "dBµV/m",
        (30, 230, 40, 40), (230, 1000, 47, 47));
    public static EmiLimitLine FccClassB3m { get; } = Line("FCC Part 15 Class B radiated at 3 m, quasi-peak", "dBµV/m",
        (30, 88, 40, 40), (88, 216, 43.5, 43.5), (216, 960, 46, 46), (960, 1000, 54, 54));

    public static IReadOnlyList<EmiLimitLine> All { get; } = new[]
    {
        ConductedClassBQuasiPeak, ConductedClassBAverage, ConductedClassAQuasiPeak, ConductedClassAAverage,
        RadiatedClassB10m, RadiatedClassB3m, RadiatedClassA10m, FccClassB3m
    };
}
