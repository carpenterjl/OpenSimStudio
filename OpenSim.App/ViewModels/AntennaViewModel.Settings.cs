using System.ComponentModel;
using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The panel's inputs in and out of the project file (D23). The board's net, island, ground pour
/// and printed layer are not saved — a loaded project opens without the board — and results are
/// recomputed.
/// </summary>
public partial class AntennaViewModel
{
    public AntennaSettings ToSettings() => new()
    {
        SourceMode = SourceMode,
        DipoleLengthMm = DipoleLengthMm,
        LoopRadiusMm = LoopRadiusMm,
        MonopoleHeightMm = MonopoleHeightMm,
        WireRadiusMm = WireRadiusMm,
        UseGroundPlane = UseGroundPlane,
        GroundZMm = GroundZMm,
        HeightAboveGroundMm = HeightAboveGroundMm,
        PlateWidthMm = PlateWidthMm,
        PlateLengthMm = PlateLengthMm,
        PatchHeightMm = PatchHeightMm,
        SubstrateEpsR = SubstrateEpsR,
        SubstrateTanD = SubstrateTanD,
        PatchGapOffsetMm = PatchGapOffsetMm,
        PatchGapWidthMm = PatchGapWidthMm,
        ProbeXMm = ProbeXMm,
        ProbeYMm = ProbeYMm,
        ProbeRadiusMm = ProbeRadiusMm,
        ProbeSegments = ProbeSegments,
        AttachedWireLengthMm = AttachedWireLengthMm,
        AttachedWireDegrees = AttachedWireDegrees,
        AttachXMm = AttachXMm,
        AttachYMm = AttachYMm,
        CoverThicknessMm = CoverThicknessMm,
        CoverEpsR = CoverEpsR,
        CoverTanD = CoverTanD,
        FrequencyMHz = FrequencyMHz,
        SweepFMinMHz = SweepFMinMHz,
        SweepFMaxMHz = SweepFMaxMHz,
        SweepPoints = SweepPoints,
        AdaptiveSweep = AdaptiveSweep,
        LogarithmicSweep = LogarithmicSweep,
        MeshCellsPerWavelength = MeshCellsPerWavelength,
        CheckMeshConvergence = CheckMeshConvergence,
        ThreePassMeshCheck = ThreePassMeshCheck,
        ReferenceOhms = ReferenceOhms,
        ConductorLoss = ConductorLoss,
        GroundLoss = GroundLoss,
        MetalConductivity = MetalConductivity,
        MetalThicknessUm = MetalThicknessUm,
        MatchFrequencyMHz = MatchFrequencyMHz,
        InductorQ = InductorQ,
        CapacitorQ = CapacitorQ,
        GridResolution = GridResolution,
        OverlayFieldType = OverlayFieldType,
        OverlayScaleMode = OverlayScaleMode,
        OverlayHeightMm = OverlayHeightMm,
        OverlayGridN = OverlayGridN,
        OverlayAutoRange = OverlayAutoRange,
        OverlayMinVPerM = OverlayMinVPerM,
        OverlayMaxVPerM = OverlayMaxVPerM,
        OverlayDecades = OverlayDecades,
        OverlayOpacityPercent = OverlayOpacityPercent,
        OverlayOverBoard = OverlayOverBoard,
        FarFieldPort = FarFieldPort,
        Pins = Pins.Select(p => new AntennaPinSettings
        {
            Name = p.Name, XMm = p.XMm, YMm = p.YMm, RadiusMm = p.RadiusMm, Role = p.Role, Load = p.Load,
            Value = p.Value, Q = p.Q
        }).ToList(),
        BridgeWidthMm = BridgeWidthMm,
        FeedXMm = FeedXMm,
        FeedYMm = FeedYMm,
        GroundClipMm = GroundClipMm,
        PrintedOnLaminate = PrintedOnLaminate,
        SheetParts = SheetParts.Select(p => new SheetPartSettings
        {
            Name = p.Name, X0Mm = p.X0Mm, Y0Mm = p.Y0Mm, Z0Mm = p.Z0Mm, X1Mm = p.X1Mm, Y1Mm = p.Y1Mm, Z1Mm = p.Z1Mm
        }).ToList(),
        SheetPorts = SheetPorts.Select(p => new SheetPortSettings
        {
            Name = p.Name, FromXMm = p.FromXMm, FromYMm = p.FromYMm, FromZMm = p.FromZMm,
            ToXMm = p.ToXMm, ToYMm = p.ToYMm, ToZMm = p.ToZMm, Direction = p.Direction, Load = p.Load,
            Value = p.Value, Q = p.Q
        }).ToList(),
        SheetImageGround = SheetImageGround,
        SheetMinEdgeMm = SheetMinEdgeMm,
        PifaGroundWidthMm = PifaGroundWidthMm,
        PifaGroundLengthMm = PifaGroundLengthMm,
        PifaTopWidthMm = PifaTopWidthMm,
        PifaTopLengthMm = PifaTopLengthMm,
        PifaHeightMm = PifaHeightMm,
        PifaTabWidthMm = PifaTabWidthMm,
        PifaFeedSpacingMm = PifaFeedSpacingMm
    };

    /// <summary>Sets the panel from a loaded project's settings; null (an older project) leaves
    /// it as it is. Returns notes for the log: a label the panel no longer offers, and the board
    /// choices a board mode needs picked again.</summary>
    public IReadOnlyList<string> LoadSettings(AntennaSettings? saved)
    {
        var notes = new List<string>();
        if (saved is null) return notes;
        SourceMode = SavedChoice.Pick(saved.SourceMode, SourceModes, SourceMode, "Antenna source", notes);
        DipoleLengthMm = saved.DipoleLengthMm;
        LoopRadiusMm = saved.LoopRadiusMm;
        MonopoleHeightMm = saved.MonopoleHeightMm;
        WireRadiusMm = saved.WireRadiusMm;
        UseGroundPlane = saved.UseGroundPlane;
        GroundZMm = saved.GroundZMm;
        HeightAboveGroundMm = saved.HeightAboveGroundMm;
        PlateWidthMm = saved.PlateWidthMm;
        PlateLengthMm = saved.PlateLengthMm;
        PatchHeightMm = saved.PatchHeightMm;
        SubstrateEpsR = saved.SubstrateEpsR;
        SubstrateTanD = saved.SubstrateTanD;
        PatchGapOffsetMm = saved.PatchGapOffsetMm;
        PatchGapWidthMm = saved.PatchGapWidthMm;
        ProbeXMm = saved.ProbeXMm;
        ProbeYMm = saved.ProbeYMm;
        ProbeRadiusMm = saved.ProbeRadiusMm;
        ProbeSegments = saved.ProbeSegments;
        AttachedWireLengthMm = saved.AttachedWireLengthMm;
        AttachedWireDegrees = saved.AttachedWireDegrees;
        AttachXMm = saved.AttachXMm;
        AttachYMm = saved.AttachYMm;
        CoverThicknessMm = saved.CoverThicknessMm;
        CoverEpsR = saved.CoverEpsR;
        CoverTanD = saved.CoverTanD;
        FrequencyMHz = saved.FrequencyMHz;
        SweepFMinMHz = saved.SweepFMinMHz;
        SweepFMaxMHz = saved.SweepFMaxMHz;
        SweepPoints = saved.SweepPoints;
        AdaptiveSweep = saved.AdaptiveSweep;
        LogarithmicSweep = saved.LogarithmicSweep;
        MeshCellsPerWavelength = saved.MeshCellsPerWavelength;
        CheckMeshConvergence = saved.CheckMeshConvergence;
        ThreePassMeshCheck = saved.ThreePassMeshCheck;
        ReferenceOhms = saved.ReferenceOhms;
        ConductorLoss = saved.ConductorLoss;
        GroundLoss = saved.GroundLoss;
        MetalConductivity = saved.MetalConductivity;
        MetalThicknessUm = saved.MetalThicknessUm;
        MatchFrequencyMHz = saved.MatchFrequencyMHz;
        InductorQ = saved.InductorQ;
        CapacitorQ = saved.CapacitorQ;
        GridResolution = saved.GridResolution;
        OverlayFieldType = SavedChoice.Pick(saved.OverlayFieldType, OverlayFieldTypes, OverlayFieldType, "Overlay field", notes);
        OverlayScaleMode = SavedChoice.Pick(saved.OverlayScaleMode, OverlayScaleModes, OverlayScaleMode, "Overlay scale", notes);
        OverlayHeightMm = saved.OverlayHeightMm;
        OverlayGridN = saved.OverlayGridN;
        OverlayAutoRange = saved.OverlayAutoRange;
        OverlayMinVPerM = saved.OverlayMinVPerM;
        OverlayMaxVPerM = saved.OverlayMaxVPerM;
        OverlayDecades = saved.OverlayDecades;
        OverlayOpacityPercent = saved.OverlayOpacityPercent;
        OverlayOverBoard = saved.OverlayOverBoard;
        BridgeWidthMm = saved.BridgeWidthMm;
        FeedXMm = saved.FeedXMm;
        FeedYMm = saved.FeedYMm;
        GroundClipMm = saved.GroundClipMm;
        PrintedOnLaminate = saved.PrintedOnLaminate;
        SheetImageGround = saved.SheetImageGround;
        SheetMinEdgeMm = saved.SheetMinEdgeMm;
        PifaGroundWidthMm = saved.PifaGroundWidthMm;
        PifaGroundLengthMm = saved.PifaGroundLengthMm;
        PifaTopWidthMm = saved.PifaTopWidthMm;
        PifaTopLengthMm = saved.PifaTopLengthMm;
        PifaHeightMm = saved.PifaHeightMm;
        PifaTabWidthMm = saved.PifaTabWidthMm;
        PifaFeedSpacingMm = saved.PifaFeedSpacingMm;

        TrackPinRows();
        TrackSheetRows();
        if (saved.Pins is { } pins)
        {
            Pins.Clear();
            foreach (var p in pins)
                Pins.Add(new AntennaPinRow
                {
                    Name = p.Name, XMm = p.XMm, YMm = p.YMm, RadiusMm = p.RadiusMm,
                    Role = SavedChoice.Pick(p.Role, AntennaPinRow.Roles, AntennaPinRow.Feed, $"Pin '{p.Name}' role", notes),
                    Load = SavedChoice.Pick(p.Load, PortLoads.All, PortLoads.Port, $"Pin '{p.Name}' closure", notes),
                    Value = p.Value, Q = p.Q
                });
        }
        if (saved.SheetParts is { } parts)
        {
            SheetParts.Clear();
            foreach (var p in parts)
                SheetParts.Add(new SheetPartRow
                {
                    Name = p.Name, X0Mm = p.X0Mm, Y0Mm = p.Y0Mm, Z0Mm = p.Z0Mm, X1Mm = p.X1Mm, Y1Mm = p.Y1Mm, Z1Mm = p.Z1Mm
                });
        }
        if (saved.SheetPorts is { } ports)
        {
            SheetPorts.Clear();
            foreach (var p in ports)
                SheetPorts.Add(new SheetPortRow
                {
                    Name = p.Name, FromXMm = p.FromXMm, FromYMm = p.FromYMm, FromZMm = p.FromZMm,
                    ToXMm = p.ToXMm, ToYMm = p.ToYMm, ToZMm = p.ToZMm,
                    Direction = SavedChoice.Pick(p.Direction, SheetPortRow.Directions, "+z", $"Port '{p.Name}' direction", notes),
                    Load = SavedChoice.Pick(p.Load, PortLoads.All, PortLoads.Port, $"Port '{p.Name}' closure", notes),
                    Value = p.Value, Q = p.Q
                });
        }
        // The port list follows the rows; the saved driven port is taken when it is still a port.
        OnPortRowChanged(null, new PropertyChangedEventArgs(nameof(Pins)));
        if (saved.FarFieldPort is { } port && PortChoices.Contains(port)) FarFieldPort = port;

        if (SourceMode is NetMode or IslandMode or IslandPinsMode or PrintedMode)
            notes.Add($"The antenna source is '{SourceMode}': import the board and pick the net"
                      + (SourceMode == PrintedMode ? ", its ground pour and layer" : SourceMode == NetMode ? "" : " and its island")
                      + " again — board choices are not saved with the project.");
        return notes;
    }
}
