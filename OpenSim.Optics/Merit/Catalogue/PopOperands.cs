// OP-49
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.PhysicalOptics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// The physical optics operands POPD and POPI (OpticStudio 2025 R2 help, "Physical Optics
/// Propagation (POP) Results", saved under plans/sources/optics/op-49-help/; OP-49): each runs
/// the Physical Optics Propagation analysis with its saved settings (the project's last-used
/// settings of that analysis, or its defaults), with the row's Surf, Wave and Field replacing the
/// saved end surface, wavelength and field when they are not zero. Rows on the same lens with the
/// same settings share one propagation.
/// <para>
/// Columns: Int1 Surf, Int2 Wave, Data1 Field, Data2 Data, Data3 Xtr1 (POPD) or Pix# (POPI),
/// Data4 Xtr2. The help names the parameters in that order but does not give their columns;
/// this mapping is unchecked (docs/Optics-Conventions.md §10, item 62).
/// </para>
/// </summary>
public static class PopOperands
{
    public const string Category = "Physical Optics Propagation (POP) results";
    private const string Page = "Physical Optics Propagation (POP) Results; About Physical Optics Propagation";

    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        yield return new MeritOperandDefinition
        {
            Code = "POPD", Name = "Physical optics propagation data", Category = Category, Source = Page,
            Description = "Physical optics propagation data of the end surface: 3 total power, 4 peak irradiance, 5–10 pilot position, Rayleigh range and waist (x, y), 11–13 the array centre, 21–22 centroid, 23–26 beam widths and M², 27–29 Wigner second moments, 30–35 irradiance and phase variation, 40–42 enclosed power at radius Xtr1, 50–52 radius enclosing fraction Xtr1. Fiber coupling (0–2, 60–63) is OP-50 and refused.",
            Int1 = "Surf", Int2 = "Wave", Data = new[] { "Field", "Data", "Xtr1", "Xtr2" },
            Compute = Guard(Popd),
        };
        yield return new MeritOperandDefinition
        {
            Code = "POPI", Name = "Physical optics propagation pixel", Category = Category, Source = Page,
            Description = "One pixel (Pix# = x + y·nx) of the propagated beam: 0–2 irradiance (total, Ex, Ey), 3–6 Ex and Ey real and imaginary parts, 7–8 Ex and Ey phase in radians. The beam is scalar, so Ey is zero.",
            Int1 = "Surf", Int2 = "Wave", Data = new[] { "Field", "Data", "Pix#" },
            Compute = Guard(Popi),
        };
    }

    private static Func<MeritRowContext, double> Guard(Func<MeritRowContext, double> compute) => c =>
    {
        try { return compute(c); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException
            or FormatException or IOException)
        {
            throw new MeritOperandException(e.Message.Split('\n')[0].TrimEnd('\r'));
        }
    };

    private static readonly ConditionalWeakTable<SequentialLens, ConditionalWeakTable<OpticalSystemData, ConcurrentDictionary<string, Lazy<PopRun>>>> Cache = new();

    /// <summary>The run of the row: saved settings with Surf, Wave and Field applied.</summary>
    private static PopRun RunFor(MeritRowContext c)
    {
        var analysis = new PhysicalOpticsPropagationAnalysis();
        var saved = c.Configurations?.AnalysisSettings(PhysicalOpticsPropagationAnalysis.SavedId);
        var settings = (PhysicalOpticsSettings)OpticsAnalysisSettingsCodec.Load(analysis, saved, out _);
        int surf = c.Row.Int1, wave = c.Row.Int2;
        double fieldValue = c.Row.DataAt(1);
        int field = (int)fieldValue;
        if (field != fieldValue || field < 0)
            throw new MeritOperandException($"Field (Data1) is {fieldValue}; it must be a whole field number, or 0 for the saved one.");
        if (surf != 0) c.Surface(surf);
        if (wave != 0) c.Wavelength(wave);
        settings = settings with
        {
            EndSurface = surf != 0 ? surf : settings.EndSurface,
            Wavelength = wave != 0 ? wave : settings.Wavelength,
            Field = field != 0 ? field : settings.Field,
            SaveOutputBeamTo = "",   // an operand never writes beam files
            SaveBeamAtAllSurfaces = false,
        };
        string key = JsonSerializer.Serialize(settings, OpticsAnalysisSettingsCodec.SerializerOptions);
        var perSystem = Cache.GetOrCreateValue(c.Lens);
        var runs = perSystem.GetValue(c.System, _ => new ConcurrentDictionary<string, Lazy<PopRun>>());
        var lazy = runs.GetOrAdd(key, _ => new Lazy<PopRun>(() => PopRunner.Run(c.Lens, c.System, settings), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private static int Data(MeritRowContext c)
    {
        double v = c.Row.DataAt(2);
        int d = (int)v;
        if (d != v) throw new MeritOperandException($"Data (Data2) is {v}; it must be a whole number.");
        return d;
    }

    private static double Popd(MeritRowContext c)
    {
        int data = Data(c);
        if (data is 0 or 1 or 2 or >= 60 and <= 63)
            throw new MeritOperandException($"POPD data {data} is a fiber coupling result; fiber coupling is OP-50 and is not built.");
        bool defined = data is >= 3 and <= 13 or >= 21 and <= 35 or >= 40 and <= 42 or >= 50 and <= 52;
        if (!defined) return 0;   // help: "Undefined Data values will return 0."
        var run = RunFor(c);
        var f = run.Field;
        double per = OpticsConventions.MetersPer(c.Unit);
        double xtr1 = c.Row.DataAt(3);
        if (data is 11 or 12 or 13) return 0;   // the axial beam's array centre is the end surface's vertex
        if (data is >= 5 and <= 10)
        {
            var p = f.Pilot;
            return ((data - 5) % 3) switch
            {
                0 => p.DistanceFromWaist / per,
                1 => p.RayleighRange / per,
                _ => p.WaistRadius / per,
            };
        }
        if (data is >= 30 and <= 35)
        {
            var settings = (PhysicalOpticsSettings)OpticsAnalysisSettingsCodec.Load(new PhysicalOpticsPropagationAnalysis(),
                c.Configurations?.AnalysisSettings(PhysicalOpticsPropagationAnalysis.SavedId), out _);
            var (mean, rms, ptv) = PopBeamMetrics.Variation(f, phase: data >= 33, settings.ZeroPhaseBelow);
            double scale = data >= 33 ? 1 : per * per;   // irradiance in W per lens unit²
            return ((data - 30) % 3) switch { 0 => mean * scale, 1 => rms * scale, _ => ptv * scale };
        }
        var b = PopBeamMetrics.Compute(f);
        switch (data)
        {
            case 3: return b.Power;
            case 4: return b.PeakIrradiance * per * per;
            case 21: return b.CentroidX / per;
            case 22: return b.CentroidY / per;
            case 23: return (xtr1 == 1 ? b.PrincipalWidths.X : b.WidthX) / per;
            case 24: return (xtr1 == 1 ? b.PrincipalWidths.Y : b.WidthY) / per;
            case 25: return b.MSquaredX;
            case 26: return b.MSquaredY;
            case 27:
                return (int)xtr1 switch
                {
                    0 => Root(b.XX) / per, 1 => Root(b.ThetaXX), 2 => Root(b.XThetaX / per), 3 => Root(b.XThetaY / per),
                    _ => throw new MeritOperandException($"POPD 27 takes Xtr1 0 to 3 (it is {xtr1})."),
                };
            case 28:
                return (int)xtr1 switch
                {
                    0 => Root(b.YY) / per, 1 => Root(b.ThetaYY), 2 => Root(b.YThetaY / per), 3 => Root(b.YThetaX / per),
                    _ => throw new MeritOperandException($"POPD 28 takes Xtr1 0 to 3 (it is {xtr1})."),
                };
            case 29:
                return (int)xtr1 switch
                {
                    0 => Root(b.XY) / per, 1 => Root(b.ThetaXY),
                    _ => throw new MeritOperandException($"POPD 29 takes Xtr1 0 or 1 (it is {xtr1})."),
                };
        }
        if (data is >= 40 and <= 42)
        {
            if (!(xtr1 > 0)) throw new MeritOperandException($"POPD {data}: the radius Xtr1 must be positive (it is {xtr1}).");
            var (x0, y0) = data == 40 ? (b.CentroidX, b.CentroidY) : (0.0, 0.0);
            return PopBeamMetrics.EnclosedFraction(f, x0, y0, xtr1 * per);
        }
        // 50 to 52
        if (!(xtr1 > 0) || xtr1 > 1) throw new MeritOperandException($"POPD {data}: the fraction Xtr1 must be in (0, 1] (it is {xtr1}).");
        var (cx, cy) = data == 50 ? (b.CentroidX, b.CentroidY) : (0.0, 0.0);
        return PopBeamMetrics.RadiusEnclosing(f, cx, cy, xtr1) / per;
    }

    /// <summary>The help's "square root of second moment", signed for a negative mixed moment
    /// (docs §66).</summary>
    private static double Root(double v) => Math.Sign(v) * Math.Sqrt(Math.Abs(v));

    private static double Popi(MeritRowContext c)
    {
        int data = Data(c);
        if (data is < 0 or > 8) return 0;   // help: "Undefined Data values will return 0."
        var run = RunFor(c);
        var f = run.Field;
        double pv = c.Row.DataAt(3);
        long pixel = (long)pv;
        long count = (long)f.Nx * f.Ny;
        if (pixel != pv || pixel < 0 || pixel >= count)
            throw new MeritOperandException($"Pix# (Data3) is {pv}; it must be a whole number from 0 to {count - 1} (nx·ny − 1, nx = {f.Nx}, ny = {f.Ny}).");
        int x = (int)(pixel % f.Nx), y = (int)(pixel / f.Nx);
        double per = OpticsConventions.MetersPer(c.Unit);
        // The printed sign is the conjugate of the field's (docs §66); amplitudes in √(W/lens unit²).
        var u = System.Numerics.Complex.Conjugate(f.Samples[y, x]) * per;
        return data switch
        {
            0 or 1 => u.Real * u.Real + u.Imaginary * u.Imaginary,
            3 => u.Real,
            4 => u.Imaginary,
            7 => u == System.Numerics.Complex.Zero ? 0 : Math.Atan2(u.Imaginary, u.Real),
            _ => 0,   // 2, 5, 6, 8: Ey, zero in a scalar propagation
        };
    }
}
