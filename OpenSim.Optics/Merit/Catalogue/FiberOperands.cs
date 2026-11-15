// OP-50
using OpenSim.Optics.Analysis;
using OpenSim.Optics.FiberCoupling;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// The fiber coupling operands FICL and FICP (OpticStudio 2025 R2 help, "Fiber Coupling
/// Operands", saved under plans/sources/optics/op-50-help/; OP-50).
/// <para>
/// FICL: Int1 Samp (1 → 32 × 32, 2 → 64 × 64, …), Int2 Wave, Data1 Field, Data2 IgSrc,
/// Data3 Sna, Data4 Rna, Data5 Data, Data6 Pol?. Data 0 and 1 are the power coupling by the
/// fast and the Huygens algorithm, 2/4 and 3/5 the real/imaginary part of the amplitude coupling
/// by each; 6 to 11 mean the same with the Single Mode Fiber Coupling analysis's saved settings,
/// a non-zero Samp, Wave, Field, Sna or Rna replacing the saved value (IgSrc and Pol? always from
/// the row). With Data 0 to 5 the tilts, decenters and alignment are the analysis's defaults
/// (none). FICP: Int1 Surf, Int2 Wave, Data1 Field, Data2 Data, as POPD (Data 0 total power
/// coupling, 1–6 the Ex and Ey amplitude coupling, its real and imaginary parts, which are 0 for
/// the unpolarized, scalar propagation, as the help says). The help names the parameters in this
/// order but not their columns; the mapping is unchecked (docs/Optics-Conventions.md §10, item 64).
/// </para>
/// </summary>
public static class FiberOperands
{
    public const string Category = "Fiber coupling operands";
    private const string Page = "Fiber Coupling Operands; Single Mode Coupling; Computing Fiber Coupling";

    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        yield return new MeritOperandDefinition
        {
            Code = "FICL", Name = "Fiber coupling efficiency (single mode)", Category = Category, Source = Page,
            Description = "Single-mode fiber coupling of field Field at wavelength Wave, pupil 32·2^(Samp−1): Data 0 power coupling (fast), 1 (Huygens), 2/4 real/imaginary amplitude coupling (fast), 3/5 (Huygens); 6–11 the same with the Single Mode Fiber Coupling analysis's saved settings. IgSrc ≠ 0 ignores the source fiber (NA Sna); Rna is the receiver NA. Pol? 1 is not built.",
            Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", "IgSrc", "Sna", "Rna", "Data", "Pol?" },
            Compute = Guard(Ficl),
        };
        yield return new MeritOperandDefinition
        {
            Code = "FICP", Name = "Fiber coupling (physical optics)", Category = Category, Source = Page + "; Physical Optics Propagation (POP) Results",
            Description = "Fiber coupling by the Physical Optics Propagation analysis with its saved settings and fiber data; Surf, Wave and Field replace the saved ones when not zero. Data 0 total power coupling; 1–6 Ex/Ey amplitude coupling and its parts, 0 because the propagation is unpolarized.",
            Int1 = "Surf", Int2 = "Wave", Data = new[] { "Field", "Data" },
            Compute = Guard(Ficp),
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

    private static int Whole(double v, string label)
    {
        int i = (int)v;
        if (i != v) throw new MeritOperandException($"{label} is {v}; it must be a whole number.");
        return i;
    }

    private static double Ficl(MeritRowContext c)
    {
        int data = Whole(c.Row.DataAt(5), "Data (Data5)");
        if (data is < 0 or > 11) throw new MeritOperandException($"FICL Data (Data5) is {data}; it must be 0 to 11.");
        if (c.Row.DataAt(6) != 0)
            throw new MeritOperandException("FICL Pol? 1 (polarization-dependent coupling) is not built; set Pol? to 0.");
        bool saved = data >= 6;
        int kind = data % 6;
        var settings = saved
            ? (SingleModeFiberCouplingSettings)OpticsAnalysisSettingsCodec.Load(new SingleModeFiberCouplingAnalysis(),
                c.Configurations?.AnalysisSettings(SingleModeFiberCouplingAnalysis.SavedId), out _)
            : new SingleModeFiberCouplingSettings();
        int samp = c.Row.Int1;
        if (samp < 0 || samp > 7) throw new MeritOperandException($"FICL Samp (Int1) is {samp}; it must be 1 to 7 (32 × 32 to 2048 × 2048){(saved ? ", or 0 for the saved sampling" : "")}.");
        if (samp == 0 && !saved) throw new MeritOperandException("FICL Samp (Int1) is 0; it must be 1 or more (1 gives 32 × 32).");
        int wave = c.Row.Int2;
        if (wave != 0) c.Wavelength(wave);
        else if (!saved) throw new MeritOperandException("FICL Wave (Int2) is 0; it must be a wavelength number.");
        int field = Whole(c.Row.DataAt(1), "Field (Data1)");
        if (field < 0 || field > c.System.Fields.Points.Count || (field == 0 && !saved))
            throw new MeritOperandException($"FICL Field (Data1) is {field}; it must be 1 to {c.System.Fields.Points.Count}{(saved ? ", or 0 for the saved field" : "")}.");
        bool ignore = c.Row.DataAt(2) != 0;
        double sna = c.Row.DataAt(3), rna = c.Row.DataAt(4);
        if (!saved && !(rna > 0)) throw new MeritOperandException($"FICL Rna (Data4) is {rna}; the receiving fiber's NA must be positive.");
        if (!saved && !ignore && !(sna > 0)) throw new MeritOperandException($"FICL Sna (Data3) is {sna}; the source fiber's NA must be positive (or IgSrc not 0).");
        settings = settings with
        {
            Sampling = samp != 0 ? 32 << (samp - 1) : settings.Sampling,
            Wavelength = wave != 0 ? wave : settings.Wavelength,
            Field = field != 0 ? field : settings.Field,
            IgnoreSourceFiber = ignore,
            SourceNaX = sna != 0 ? sna : settings.SourceNaX,
            SourceNaY = sna != 0 ? sna : settings.SourceNaY,
            ReceiverNaX = rna != 0 ? rna : settings.ReceiverNaX,
            ReceiverNaY = rna != 0 ? rna : settings.ReceiverNaY,
            UseHuygens = kind is 1 or 3 or 5,
            UsePolarization = false,
        };
        var result = SingleModeFiberCoupling.Compute(c.Lens, c.System, settings.ToRequest(c.System, "FICL"));
        return kind switch
        {
            0 or 1 => result.Coupling,
            2 or 3 => result.AmplitudeCoupling.Real,
            _ => result.AmplitudeCoupling.Imaginary,
        };
    }

    private static double Ficp(MeritRowContext c)
    {
        int data = Whole(c.Row.DataAt(2), "Data (Data2)");
        if (data is >= 1 and <= 6) return 0;   // help: 0.0 when the input beam is unpolarized (the propagation is scalar)
        if (data != 0) throw new MeritOperandException($"FICP Data (Data2) is {data}; it must be 0 to 6.");
        var (run, settings) = PopOperands.RunWithSettings(c);
        return PopFiberCoupling.Compute(run, settings.ToFiber(c.Unit), c.Unit).Coupling;
    }
}
