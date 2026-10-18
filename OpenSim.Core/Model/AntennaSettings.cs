namespace OpenSim.Core.Model;

/// <summary>
/// The antenna panel's inputs (the RF workspace), saved with the project so a study re-opens
/// the way it was set up (evaluation D23). In the panel's own units, as the names say (mm, MHz,
/// µm); the defaults are the panel's. A choice from one of the panel's lists is its label, null
/// meaning "the panel's default". What belongs to a board or a file the session loaded — the
/// antenna net and island, the ground pour, the printed antenna's layer — is not saved: a loaded
/// project opens without the board, and those are picked again after importing it. Results are
/// not saved; a solve recomputes them. Null on a project — every older file — leaves the panel
/// as it is.
/// </summary>
public sealed record AntennaSettings
{
    /// <summary>The geometry source (the panel's mode label).</summary>
    public string? SourceMode { get; init; }

    // Wire wizard [mm].
    public double DipoleLengthMm { get; init; } = 500;
    public double LoopRadiusMm { get; init; } = 80;
    public double MonopoleHeightMm { get; init; } = 250;
    public double WireRadiusMm { get; init; } = 0.5;

    // The image ground plane [mm].
    public bool UseGroundPlane { get; init; }
    public double GroundZMm { get; init; }
    public double HeightAboveGroundMm { get; init; } = 250;

    // Surface wizard [mm] and the substrate.
    public double PlateWidthMm { get; init; } = 300;
    public double PlateLengthMm { get; init; } = 500;
    public double PatchHeightMm { get; init; } = 50;
    public double SubstrateEpsR { get; init; } = 1.0;
    public double SubstrateTanD { get; init; }

    // The edge-fed patch's gap [mm]; 0 = the automatic offset and width.
    public double PatchGapOffsetMm { get; init; }
    public double PatchGapWidthMm { get; init; }

    // The coaxial probe [mm].
    public double ProbeXMm { get; init; }
    public double ProbeYMm { get; init; } = -20;
    public double ProbeRadiusMm { get; init; } = 0.2;
    public int ProbeSegments { get; init; } = 3;

    // The wire-fed plate's attached wire [mm, degrees].
    public double AttachedWireLengthMm { get; init; } = 60;
    public double AttachedWireDegrees { get; init; } = 90;
    public double AttachXMm { get; init; }
    public double AttachYMm { get; init; }

    // The covered patch's cover [mm]; εr ≤ 0 and tanδ < 0 track the substrate.
    public double CoverThicknessMm { get; init; } = 0.8;
    public double CoverEpsR { get; init; }
    public double CoverTanD { get; init; } = -1;

    // Frequencies [MHz] and the mesh.
    public double FrequencyMHz { get; init; } = 300;
    public double SweepFMinMHz { get; init; } = 100;
    public double SweepFMaxMHz { get; init; } = 1000;
    public int SweepPoints { get; init; } = 7;
    public bool AdaptiveSweep { get; init; }
    public bool LogarithmicSweep { get; init; }
    public double MeshCellsPerWavelength { get; init; } = 10;
    public bool CheckMeshConvergence { get; init; } = true;
    public bool ThreePassMeshCheck { get; init; }

    // The network: reference impedance, conductor and ground loss.
    public double ReferenceOhms { get; init; } = 50;
    public bool ConductorLoss { get; init; }
    public bool GroundLoss { get; init; }
    public double MetalConductivity { get; init; } = 5.8e7;
    public double MetalThicknessUm { get; init; } = 35;

    // The L-section match; 0 MHz = the sweep's best-match point.
    public double MatchFrequencyMHz { get; init; }
    public double InductorQ { get; init; } = 40;
    public double CapacitorQ { get; init; } = 200;

    // Fields: the near-field grid and the board overlay.
    public int GridResolution { get; init; } = 9;
    public string? OverlayFieldType { get; init; }
    public string? OverlayScaleMode { get; init; }
    public double OverlayHeightMm { get; init; } = 1.0;
    public int OverlayGridN { get; init; } = 61;
    public bool OverlayAutoRange { get; init; } = true;
    public double OverlayMinVPerM { get; init; }
    public double OverlayMaxVPerM { get; init; } = 100;
    public int OverlayDecades { get; init; } = 3;
    public double OverlayOpacityPercent { get; init; } = 60;
    public bool OverlayOverBoard { get; init; }

    /// <summary>The port the far and near fields drive; null = the first.</summary>
    public string? FarFieldPort { get; init; }

    /// <summary>The pins (patch or island); null leaves the panel's.</summary>
    public List<AntennaPinSettings>? Pins { get; init; }

    // The printed antenna [mm].
    public double BridgeWidthMm { get; init; } = 1;
    public double FeedXMm { get; init; }
    public double FeedYMm { get; init; }
    public double GroundClipMm { get; init; }
    public bool PrintedOnLaminate { get; init; } = true;

    // Sheet metal [mm]: the plates and port lines (null leaves the panel's), the image ground,
    // the finest element, and the PIFA filler.
    public List<SheetPartSettings>? SheetParts { get; init; }
    public List<SheetPortSettings>? SheetPorts { get; init; }
    public bool SheetImageGround { get; init; }
    public double SheetMinEdgeMm { get; init; }
    public double PifaGroundWidthMm { get; init; } = 40;
    public double PifaGroundLengthMm { get; init; } = 80;
    public double PifaTopWidthMm { get; init; } = 20;
    public double PifaTopLengthMm { get; init; } = 30;
    public double PifaHeightMm { get; init; } = 6;
    public double PifaTabWidthMm { get; init; } = 2;
    public double PifaFeedSpacingMm { get; init; } = 5;
}

/// <summary>One pin of the antenna panel [mm]: its role (feed or short) and a feed's closure by
/// the panel's labels, null meaning the panel's default.</summary>
public sealed record AntennaPinSettings
{
    public string Name { get; init; } = "feed";
    public double XMm { get; init; }
    public double YMm { get; init; }
    public double RadiusMm { get; init; } = 0.2;
    public string? Role { get; init; }
    public string? Load { get; init; }
    public double Value { get; init; }
    public double Q { get; init; }
}

/// <summary>One sheet-metal plate [mm]: two opposite corners.</summary>
public sealed record SheetPartSettings
{
    public string Name { get; init; } = "plate";
    public double X0Mm { get; init; }
    public double Y0Mm { get; init; }
    public double Z0Mm { get; init; }
    public double X1Mm { get; init; }
    public double Y1Mm { get; init; }
    public double Z1Mm { get; init; }
}

/// <summary>One sheet-metal port line [mm]: its two ends, the direction the current crosses it,
/// and its closure, by the panel's labels.</summary>
public sealed record SheetPortSettings
{
    public string Name { get; init; } = "feed";
    public double FromXMm { get; init; }
    public double FromYMm { get; init; }
    public double FromZMm { get; init; }
    public double ToXMm { get; init; }
    public double ToYMm { get; init; }
    public double ToZMm { get; init; }
    public string? Direction { get; init; }
    public string? Load { get; init; }
    public double Value { get; init; }
    public double Q { get; init; }
}
