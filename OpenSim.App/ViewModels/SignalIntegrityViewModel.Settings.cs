using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The SI workspace's inputs in and out of the project file (D23): the cross-section and eye
/// panel here, the channel panel and the calculator below. The board nets and their ends, the
/// IBIS file with its model and corner, and the Touchstone file are not saved; results are
/// recomputed.
/// </summary>
public partial class SignalIntegrityViewModel
{
    public SignalIntegritySettings ToSettings() => new()
    {
        LineCount = LineCount,
        TraceWidthMm = TraceWidthMm,
        TraceGapMm = TraceGapMm,
        SubstrateHeightMm = SubstrateHeightMm,
        EpsR = SiEpsR,
        TanD = SiTanD,
        CopperThicknessUm = CopperThicknessUm,
        LineLengthMm = LineLengthMm,
        SignalType = SignalType,
        DrivenLine = DrivenLine,
        BitRateGbps = BitRateGbps,
        RiseFractionPercent = RiseFractionPercent,
        AmplitudeVolts = AmplitudeVolts,
        SourceOhms = SourceOhms,
        LoadOhms = LoadOhms,
        LoadPicofarads = LoadPicofarads,
        AggressorsEnabled = AggressorsEnabled,
        ProximityEffect = ProximityEffect,
        RoughnessModel = RoughnessModel,
        RoughnessUm = RoughnessUm,
        HuraySurfaceRatio = HuraySurfaceRatio,
        Foil = Foil,
        SideWallSolve = SideWallSolve,
        CornerDiscontinuities = CornerDiscontinuities,
        ScanRiseTimeNs = ScanRiseTimeNs,
        Channel = Channel.ToSettings(),
        Calculator = Calculator.ToSettings()
    };

    /// <summary>Sets the panels from a loaded project's settings; null (an older project) leaves
    /// them as they are. Returns notes for the log: a label the panels no longer offer.</summary>
    public IReadOnlyList<string> LoadSettings(SignalIntegritySettings? saved)
    {
        var notes = new List<string>();
        if (saved is null) return notes;
        LineCount = saved.LineCount;
        TraceWidthMm = saved.TraceWidthMm;
        TraceGapMm = saved.TraceGapMm;
        SubstrateHeightMm = saved.SubstrateHeightMm;
        SiEpsR = saved.EpsR;
        SiTanD = saved.TanD;
        CopperThicknessUm = saved.CopperThicknessUm;
        LineLengthMm = saved.LineLengthMm;
        SignalType = SavedChoice.Pick(saved.SignalType, SignalTypes, SignalType, "SI signal", notes);
        DrivenLine = saved.DrivenLine;
        BitRateGbps = saved.BitRateGbps;
        RiseFractionPercent = saved.RiseFractionPercent;
        AmplitudeVolts = saved.AmplitudeVolts;
        SourceOhms = saved.SourceOhms;
        LoadOhms = saved.LoadOhms;
        LoadPicofarads = saved.LoadPicofarads;
        AggressorsEnabled = saved.AggressorsEnabled;
        ProximityEffect = saved.ProximityEffect;
        RoughnessModel = SavedChoice.Pick(saved.RoughnessModel, RoughnessModels, RoughnessModel, "SI roughness model", notes);
        RoughnessUm = saved.RoughnessUm;
        HuraySurfaceRatio = saved.HuraySurfaceRatio;
        Foil = SavedChoice.Pick(saved.Foil, Foils, Foil, "SI foil", notes, "Merge the foils file it came from, then pick it.");
        SideWallSolve = saved.SideWallSolve;
        CornerDiscontinuities = saved.CornerDiscontinuities;
        ScanRiseTimeNs = saved.ScanRiseTimeNs;
        Channel.LoadSettings(saved.Channel, notes);
        Calculator.LoadSettings(saved.Calculator, notes);
        return notes;
    }
}

public partial class ChannelViewModel
{
    public ChannelSettings ToSettings() => new()
    {
        PortOrder = SelectedPortOrder,
        Differential = Differential,
        DrivenPort = DrivenPort,
        ReceiverPort = ReceiverPort,
        BitRateGbps = BitRateGbps,
        RisePercent = RisePercent,
        PrbsOrder = PrbsOrder,
        SwingVolts = SwingVolts,
        SourceOhms = SourceOhms,
        LoadOhms = LoadOhms,
        TdrRisePs = TdrRisePs,
        TdrTakeOutLoss = TdrTakeOutLoss,
        SourceDiePf = SourceDiePf,
        LoadDiePf = LoadDiePf,
        PackageNh = PackageNh,
        CouplingNf = CouplingNf
    };

    /// <summary>Sets the panel from saved settings; null leaves it as it is.</summary>
    public void LoadSettings(ChannelSettings? saved, ICollection<string> notes)
    {
        if (saved is null) return;
        SelectedPortOrder = SavedChoice.Pick(saved.PortOrder, PortOrders, SelectedPortOrder, "Channel port order", notes)
                            ?? SelectedPortOrder;
        Differential = saved.Differential;
        DrivenPort = saved.DrivenPort;
        ReceiverPort = saved.ReceiverPort;
        BitRateGbps = saved.BitRateGbps;
        RisePercent = saved.RisePercent;
        PrbsOrder = saved.PrbsOrder;
        SwingVolts = saved.SwingVolts;
        SourceOhms = saved.SourceOhms;
        LoadOhms = saved.LoadOhms;
        TdrRisePs = saved.TdrRisePs;
        TdrTakeOutLoss = saved.TdrTakeOutLoss;
        SourceDiePf = saved.SourceDiePf;
        LoadDiePf = saved.LoadDiePf;
        PackageNh = saved.PackageNh;
        CouplingNf = saved.CouplingNf;
    }
}

public partial class ImpedanceCalculatorViewModel
{
    public ImpedanceCalculatorSettings ToSettings() => new()
    {
        Structure = Structure,
        WidthMm = WidthMm,
        TopWidthMm = TopWidthMm,
        CopperThicknessUm = CopperThicknessUm,
        IsPair = IsPair,
        PairGapMm = PairGapMm,
        SecondWidthMm = SecondWidthMm,
        HasCoplanarGround = HasCoplanarGround,
        CoplanarGapMm = CoplanarGapMm,
        HeightMm = HeightMm,
        EpsR = EpsR,
        TanD = TanD,
        UpperHeightMm = UpperHeightMm,
        UpperEpsR = UpperEpsR,
        UpperTanD = UpperTanD,
        FrequencyGhz = FrequencyGhz,
        RoughnessModel = RoughnessModel,
        RoughnessUm = RoughnessUm,
        HuraySurfaceRatio = HuraySurfaceRatio,
        Foil = Foil,
        SideWallSolve = SideWallSolve
    };

    /// <summary>Sets the calculator from saved settings; null leaves it as it is.</summary>
    public void LoadSettings(ImpedanceCalculatorSettings? saved, ICollection<string> notes)
    {
        if (saved is null) return;
        Structure = SavedChoice.Pick(saved.Structure, Structures, Structure, "Calculator structure", notes);
        WidthMm = saved.WidthMm;
        TopWidthMm = saved.TopWidthMm;
        CopperThicknessUm = saved.CopperThicknessUm;
        IsPair = saved.IsPair;
        PairGapMm = saved.PairGapMm;
        SecondWidthMm = saved.SecondWidthMm;
        HasCoplanarGround = saved.HasCoplanarGround;
        CoplanarGapMm = saved.CoplanarGapMm;
        HeightMm = saved.HeightMm;
        EpsR = saved.EpsR;
        TanD = saved.TanD;
        UpperHeightMm = saved.UpperHeightMm;
        UpperEpsR = saved.UpperEpsR;
        UpperTanD = saved.UpperTanD;
        FrequencyGhz = saved.FrequencyGhz;
        RoughnessModel = SavedChoice.Pick(saved.RoughnessModel, RoughnessModels, RoughnessModel, "Calculator roughness model", notes);
        RoughnessUm = saved.RoughnessUm;
        HuraySurfaceRatio = saved.HuraySurfaceRatio;
        Foil = SavedChoice.Pick(saved.Foil, Foils, Foil, "Calculator foil", notes, "Merge the foils file it came from, then pick it.");
        SideWallSolve = saved.SideWallSolve;
    }
}
