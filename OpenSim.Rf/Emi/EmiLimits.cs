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
/// Limit lines of two provenances, told apart by each line's <see cref="EmiLimitLine.Source"/>.
///
/// <para><b>FCC Part 15, subpart B</b> (the <c>Fcc…</c> lines): read from the regulation's text,
/// 47 CFR § 15.107 (conducted, 150 kHz–30 MHz, 50 µH/50 Ω LISN, quasi-peak and average) and
/// § 15.109 (radiated, Class B at 3 m and Class A at 10 m, in µV/m, converted here as
/// 20·log₁₀), eCFR point-in-time text of Title 47 as of 2026-10-19 (see
/// <c>OpenSim.Tests/References/FU-19</c>). The rules the text states are the ones
/// <see cref="EmiLimitLine.At"/> applies: "the lower limit applies at the band edges"
/// (§ 15.107(a)) and "at the boundary between the frequency ranges" (§ 15.107(b)), "the
/// tighter limit applies at the band edges" (§ 15.109(c)), and the 150–500 kHz conducted
/// limit "decreases with the logarithm of the frequency". The radiated tables' last row is
/// "above 960 MHz" with no end; the lines here stop at 1000 MHz, where § 15.35 changes the
/// detector from quasi-peak (at or below 1000 MHz) to average with a peak limit 20 dB above
/// it — so <see cref="EmiLimitLine.At"/> returning null above 1 GHz means "not drawn", not
/// "no limit": the 500 / 300 µV/m row keeps applying there, on the average detector. The
/// CISPR 22 alternative § 15.109(g) offers for the radiated tables is not drawn either.</para>
///
/// <para><b>CISPR 32</b> (the other lines, conducted at the mains port and radiated below
/// 1 GHz): the standard's text is not on this machine, so these are typed as commonly
/// reproduced and carry <see cref="Unverified"/> in their source — check them against your
/// copy; every line exports to CSV and a checked one loads back in its place. The FCC's
/// conducted tables carry, cell for cell, the same numbers as the CISPR Class A and B
/// conducted lines typed here, which supports but does not verify them.</para>
/// </summary>
public static class EmiLimits
{
    public const string Unverified =
        "typed as commonly reproduced, not checked against the standard's text — verify against your copy";

    /// <summary>The provenance the <c>Fcc…</c> lines carry.</summary>
    public const string FccSource =
        "47 CFR Part 15 subpart B, §§ 15.107 and 15.109 (detectors and the 1000 MHz end: § 15.35), eCFR text of Title 47 as of 2026-10-19";

    private static EmiLimitLine Line(string name, string unit, params (double F0MHz, double F1MHz, double L0, double L1)[] parts) =>
        Line(name, unit, "CISPR 32 " + Unverified, parts);

    private static EmiLimitLine Line(string name, string unit, string source, params (double F0MHz, double F1MHz, double L0, double L1)[] parts) =>
        new(name, unit, parts.Select(p => new LimitSegment(p.F0MHz * 1e6, p.F1MHz * 1e6, p.L0, p.L1)).ToList(), source);

    /// <summary>A field strength the regulation prints in µV/m, as the dBµV/m the line holds.</summary>
    private static double DbMicrovolts(double microvolts) => 20 * Math.Log10(microvolts);

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
    // ---- FCC Part 15 subpart B, from the regulation's text (FccSource).

    /// <summary>§ 15.107(a), quasi-peak: 66→56 dBµV over 0.15–0.5 MHz (log), 56 to 5 MHz, 60 to 30 MHz.</summary>
    public static EmiLimitLine FccConductedClassBQuasiPeak { get; } = Line("FCC § 15.107(a) Class B conducted, quasi-peak", "dBµV", FccSource,
        (0.15, 0.5, 66, 56), (0.5, 5, 56, 56), (5, 30, 60, 60));

    /// <summary>§ 15.107(a), average: 56→46 dBµV over 0.15–0.5 MHz (log), 46 to 5 MHz, 50 to 30 MHz.</summary>
    public static EmiLimitLine FccConductedClassBAverage { get; } = Line("FCC § 15.107(a) Class B conducted, average", "dBµV", FccSource,
        (0.15, 0.5, 56, 46), (0.5, 5, 46, 46), (5, 30, 50, 50));

    /// <summary>§ 15.107(b), quasi-peak: 79 dBµV to 0.5 MHz, 73 to 30 MHz.</summary>
    public static EmiLimitLine FccConductedClassAQuasiPeak { get; } = Line("FCC § 15.107(b) Class A conducted, quasi-peak", "dBµV", FccSource,
        (0.15, 0.5, 79, 79), (0.5, 30, 73, 73));

    /// <summary>§ 15.107(b), average: 66 dBµV to 0.5 MHz, 60 to 30 MHz.</summary>
    public static EmiLimitLine FccConductedClassAAverage { get; } = Line("FCC § 15.107(b) Class A conducted, average", "dBµV", FccSource,
        (0.15, 0.5, 66, 66), (0.5, 30, 60, 60));

    /// <summary>§ 15.109(a), Class B at 3 m: 100 / 150 / 200 / 500 µV/m over 30–88 / 88–216 /
    /// 216–960 / above 960 MHz (drawn to 1000 MHz, the quasi-peak detector's end).</summary>
    public static EmiLimitLine FccClassB3m { get; } = Line("FCC § 15.109(a) Class B radiated at 3 m, quasi-peak", "dBµV/m", FccSource,
        (30, 88, DbMicrovolts(100), DbMicrovolts(100)), (88, 216, DbMicrovolts(150), DbMicrovolts(150)),
        (216, 960, DbMicrovolts(200), DbMicrovolts(200)), (960, 1000, DbMicrovolts(500), DbMicrovolts(500)));

    /// <summary>§ 15.109(b), Class A at 10 m: 90 / 150 / 210 / 300 µV/m over 30–88 / 88–216 /
    /// 216–960 / above 960 MHz (drawn to 1000 MHz).</summary>
    public static EmiLimitLine FccClassA10m { get; } = Line("FCC § 15.109(b) Class A radiated at 10 m, quasi-peak", "dBµV/m", FccSource,
        (30, 88, DbMicrovolts(90), DbMicrovolts(90)), (88, 216, DbMicrovolts(150), DbMicrovolts(150)),
        (216, 960, DbMicrovolts(210), DbMicrovolts(210)), (960, 1000, DbMicrovolts(300), DbMicrovolts(300)));

    public static IReadOnlyList<EmiLimitLine> All { get; } = new[]
    {
        ConductedClassBQuasiPeak, ConductedClassBAverage, ConductedClassAQuasiPeak, ConductedClassAAverage,
        RadiatedClassB10m, RadiatedClassB3m, RadiatedClassA10m,
        FccConductedClassBQuasiPeak, FccConductedClassBAverage, FccConductedClassAQuasiPeak, FccConductedClassAAverage,
        FccClassB3m, FccClassA10m
    };
}
