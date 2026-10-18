namespace OpenSim.Core.Model;

/// <summary>
/// The signal-integrity workspace's inputs — the cross-section and eye panel, the Touchstone
/// channel panel and the impedance calculator — saved with the project (evaluation D23). In the
/// panels' own units, as the names say; the defaults are the panels'. A choice from one of the
/// panels' lists is its label, null meaning "the panel's default". What belongs to a board or a
/// file the session loaded — the board nets and their ends, the IBIS file with its model and
/// corner, the Touchstone file itself — is not saved, and is picked or opened again. A foil is
/// saved by name; one from a foils file the session merged needs that file merged again. Null
/// on a project — every older file — leaves the panels as they are.
/// </summary>
public sealed record SignalIntegritySettings
{
    // The wizard cross-section [mm, µm].
    public int LineCount { get; init; } = 2;
    public double TraceWidthMm { get; init; } = 0.3;
    public double TraceGapMm { get; init; } = 0.3;
    public double SubstrateHeightMm { get; init; } = 0.2;
    public double EpsR { get; init; } = 4.4;
    public double TanD { get; init; } = 0.02;
    public double CopperThicknessUm { get; init; } = 35;
    public double LineLengthMm { get; init; } = 50;

    // The signal and terminations; the driven line is 1-based as in the panel.
    public string? SignalType { get; init; }
    public int DrivenLine { get; init; } = 1;
    public double BitRateGbps { get; init; } = 1.0;
    public double RiseFractionPercent { get; init; } = 25;
    public double AmplitudeVolts { get; init; } = 1.0;
    public double SourceOhms { get; init; } = 50;
    public double LoadOhms { get; init; } = 50;
    public double LoadPicofarads { get; init; }
    public bool AggressorsEnabled { get; init; }

    // The conductor model.
    public bool ProximityEffect { get; init; }
    public string? RoughnessModel { get; init; }
    public double RoughnessUm { get; init; }
    public double HuraySurfaceRatio { get; init; }
    public string? Foil { get; init; }
    public bool SideWallSolve { get; init; }
    public bool CornerDiscontinuities { get; init; }

    /// <summary>The board crosstalk scan's edge [ns].</summary>
    public double ScanRiseTimeNs { get; init; } = 0.5;

    /// <summary>The Touchstone channel panel; null leaves it as it is.</summary>
    public ChannelSettings? Channel { get; init; }

    /// <summary>The impedance calculator; null leaves it as it is.</summary>
    public ImpedanceCalculatorSettings? Calculator { get; init; }
}

/// <summary>The Touchstone channel panel's inputs: the port order (by its label), the ports
/// (1-based), the bit stream, the terminations and the lumped parts (0 = absent).</summary>
public sealed record ChannelSettings
{
    public string? PortOrder { get; init; }
    public bool Differential { get; init; }
    public int DrivenPort { get; init; } = 1;
    public int ReceiverPort { get; init; } = 2;
    public double BitRateGbps { get; init; } = 1;
    public double RisePercent { get; init; } = 20;
    public int PrbsOrder { get; init; } = 7;
    public double SwingVolts { get; init; } = 1;
    public double SourceOhms { get; init; } = 50;
    public double LoadOhms { get; init; } = 50;
    public double TdrRisePs { get; init; }
    public bool TdrTakeOutLoss { get; init; }
    public double SourceDiePf { get; init; }
    public double LoadDiePf { get; init; }
    public double PackageNh { get; init; }
    public double CouplingNf { get; init; }
}

/// <summary>The impedance calculator's inputs [mm, µm, GHz]: the structure (by its label), the
/// trace or pair, the dielectrics below and above, and the conductor model.</summary>
public sealed record ImpedanceCalculatorSettings
{
    public string? Structure { get; init; }
    public double WidthMm { get; init; } = 0.3;
    public double TopWidthMm { get; init; }
    public double CopperThicknessUm { get; init; } = 35;
    public bool IsPair { get; init; }
    public double PairGapMm { get; init; } = 0.2;
    public double SecondWidthMm { get; init; }
    public bool HasCoplanarGround { get; init; }
    public double CoplanarGapMm { get; init; } = 0.2;
    public double HeightMm { get; init; } = 0.2;
    public double EpsR { get; init; } = 4.4;
    public double TanD { get; init; } = 0.02;
    public double UpperHeightMm { get; init; } = 0.2;
    public double UpperEpsR { get; init; } = 4.4;
    public double UpperTanD { get; init; } = 0.02;
    public double FrequencyGhz { get; init; } = 1;
    public string? RoughnessModel { get; init; }
    public double RoughnessUm { get; init; }
    public double HuraySurfaceRatio { get; init; }
    public string? Foil { get; init; }
    public bool SideWallSolve { get; init; }
}
