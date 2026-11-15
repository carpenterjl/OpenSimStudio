using System.Globalization;
using System.Text;
using OpenSim.Optics.Analysis;

namespace OpenSim.Cli;

// OP-47: "optics efficiency" computes a one-dimensional grating's efficiency per order by RCWA or the
// scalar theory (docs/Optics-Conventions.md §61), without a project.

public static partial class CliRunner
{
    /// <summary>
    /// <c>opensim optics efficiency --period UM --wavelength UM [options]</c>: the
    /// <see cref="GratingEfficiencyAnalysis"/>'s solver from the command line. Options:
    /// <c>--method rcwa|scalar</c>, <c>--profile lamellar|blazed|multilevel|sinusoidal</c>,
    /// <c>--depth UM</c>, <c>--fill F</c>, <c>--levels N</c>, <c>--slices N</c>, <c>--ridge N[,K]</c>,
    /// <c>--groove N</c>, <c>--cover N</c>, <c>--substrate N[,K]</c>, <c>--angle DEG</c>,
    /// <c>--polarization te|tm|unpolarized</c>, <c>--harmonics N</c>, <c>--out FILE</c> (CSV).
    /// Prints one line per order with a non-zero efficiency, the sums and the absorbed fraction.
    /// </summary>
    private static int OpticsEfficiency(Dictionary<string, string> o, TextWriter stdout)
    {
        var ci = CultureInfo.InvariantCulture;
        if (!o.ContainsKey("period")) throw new UsageException("'optics efficiency' needs --period UM (the grating period, micrometers).");
        if (!o.ContainsKey("wavelength")) throw new UsageException("'optics efficiency' needs --wavelength UM (micrometers).");
        (double N, double K) Index(string key, double fallback)
        {
            if (!o.TryGetValue(key, out var s)) return (fallback, 0);
            var parts = s.Split(',');
            if (parts.Length > 2 || !double.TryParse(parts[0], NumberStyles.Float, ci, out double n)
                || (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, ci, out _)))
                throw new UsageException($"--{key} takes N or N,K (index and extinction), not '{s}'.");
            return (n, parts.Length == 2 ? double.Parse(parts[1], NumberStyles.Float, ci) : 0);
        }
        int Whole(string key, int fallback)
        {
            double v = Number(o, key, fallback);
            return v == Math.Floor(v) && v >= 0 && v <= int.MaxValue ? (int)v : throw new UsageException($"--{key} takes a whole number, not '{o[key]}'.");
        }
        var ridge = Index("ridge", 1.5);
        var substrate = Index("substrate", 1.5);
        var settings = new GratingEfficiencySettings
        {
            Method = Choice(o, "method", GratingEfficiencyMethod.Rcwa, new() { ["rcwa"] = GratingEfficiencyMethod.Rcwa, ["scalar"] = GratingEfficiencyMethod.Scalar }),
            Profile = Choice(o, "profile", GratingProfileKind.Lamellar, new()
            {
                ["lamellar"] = GratingProfileKind.Lamellar, ["blazed"] = GratingProfileKind.Blazed,
                ["multilevel"] = GratingProfileKind.Multilevel, ["sinusoidal"] = GratingProfileKind.Sinusoidal,
            }),
            DepthMicrometers = Number(o, "depth", 0.5),
            FillFactor = Number(o, "fill", 0.5),
            Levels = Whole("levels", 8),
            Slices = Whole("slices", 16),
            RidgeIndex = ridge.N, RidgeExtinction = ridge.K,
            GrooveIndex = Number(o, "groove", 1),
            CoverIndex = Number(o, "cover", 1),
            SubstrateIndex = substrate.N, SubstrateExtinction = substrate.K,
            Polarization = Choice(o, "polarization", GratingPolarizationSetting.TE, new()
            {
                ["te"] = GratingPolarizationSetting.TE, ["tm"] = GratingPolarizationSetting.TM, ["unpolarized"] = GratingPolarizationSetting.Unpolarized,
            }),
            Harmonics = Whole("harmonics", 15),
        };
        double period = Number(o, "period", 1);
        double wavelength = Number(o, "wavelength", 0.55);
        if (!(period > 0)) throw new UsageException($"--period must be positive, not {period.ToString(ci)}.");
        if (!(wavelength > 0)) throw new UsageException($"--wavelength must be positive, not {wavelength.ToString(ci)}.");
        double angle = Number(o, "angle", 0);
        var point = GratingEfficiencyAnalysis.Evaluate(settings, period * 1e-6, angle, wavelength * 1e-6);

        var csv = new StringBuilder("order,reflected,transmitted,reflected_angle_deg,transmitted_angle_deg\n");
        stdout.WriteLine(string.Create(ci, $"{(settings.Method == GratingEfficiencyMethod.Rcwa ? $"RCWA, orders -{settings.Harmonics}..{settings.Harmonics}" : "Scalar")}, {settings.Profile}, period {period:g8} um, wavelength {wavelength:g8} um, angle {angle:g8} deg, {settings.Polarization}"));
        stdout.WriteLine("order  reflected        transmitted");
        foreach (var e in point.Orders)
        {
            csv.Append(string.Create(ci, $"{e.Order},{e.Reflected:R},{e.Transmitted:R},{e.ReflectedAngleDegrees:R},{e.TransmittedAngleDegrees:R}\n"));
            if (e.Reflected > 1e-12 || e.Transmitted > 1e-12)
                stdout.WriteLine(string.Create(ci, $"{e.Order,5}  {e.Reflected,-15:g8}  {e.Transmitted:g8}"));
        }
        stdout.WriteLine(string.Create(ci, $"sum reflected {point.TotalReflected:g10}, sum transmitted {(point.TransmissionDefined ? point.TotalTransmitted.ToString("g10", ci) : "not defined (absorbing substrate)")}, absorbed {point.Absorbed:g6}"));
        if (o.TryGetValue("out", out var outPath) && outPath != "true")
        {
            File.WriteAllText(outPath, csv.ToString());
            stdout.WriteLine($"Wrote {outPath}.");
        }
        return 0;
    }
}
