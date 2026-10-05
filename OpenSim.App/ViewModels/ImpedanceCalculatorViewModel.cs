using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The stackup and impedance calculator: one line specification in, Z0 / Z_diff, delay and
/// loss out, from the same cross-section solve the board extraction uses
/// (<see cref="ImpedanceCalculator"/>).
/// </summary>
public partial class ImpedanceCalculatorViewModel : ObservableObject
{
    public const string Microstrip = "Microstrip", Embedded = "Microstrip under a cover layer", Stripline = "Stripline";

    public ObservableCollection<string> Structures { get; } = new() { Microstrip, Embedded, Stripline };

    /// <summary>Nullable so a ComboBox transient null push lands harmlessly.</summary>
    [ObservableProperty] private string? _structure = Microstrip;

    [ObservableProperty] private double _widthMm = 0.3;
    /// <summary>Width at the top of an etched trace [mm]; 0 = rectangular.</summary>
    [ObservableProperty] private double _topWidthMm;
    [ObservableProperty] private double _copperThicknessUm = 35;

    [ObservableProperty] private bool _isPair;
    [ObservableProperty] private double _pairGapMm = 0.2;
    [ObservableProperty] private bool _hasCoplanarGround;
    [ObservableProperty] private double _coplanarGapMm = 0.2;

    [ObservableProperty] private double _heightMm = 0.2;
    [ObservableProperty] private double _epsR = 4.4;
    [ObservableProperty] private double _tanD = 0.02;

    /// <summary>The layer over the trace (cover, or up to the upper plane).</summary>
    [ObservableProperty] private double _upperHeightMm = 0.2;
    [ObservableProperty] private double _upperEpsR = 4.4;
    [ObservableProperty] private double _upperTanD = 0.02;

    [ObservableProperty] private double _frequencyGhz = 1;
    /// <summary>Hammerstad RMS roughness [µm]; 0 = smooth copper.</summary>
    [ObservableProperty] private double _roughnessUm;

    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _assumptions = "";

    private LineSpec BuildSpec() => new()
    {
        Structure = Structure switch
        {
            Embedded => LineStructure.EmbeddedMicrostrip,
            Stripline => LineStructure.Stripline,
            _ => LineStructure.Microstrip
        },
        WidthMeters = WidthMm * 1e-3,
        TopWidthMeters = TopWidthMm > 0 ? TopWidthMm * 1e-3 : null,
        ThicknessMeters = CopperThicknessUm * 1e-6,
        PairGapMeters = IsPair ? PairGapMm * 1e-3 : null,
        CoplanarGapMeters = HasCoplanarGround ? CoplanarGapMm * 1e-3 : null,
        HeightMeters = HeightMm * 1e-3,
        RelativePermittivity = EpsR,
        LossTangent = TanD,
        UpperHeightMeters = UpperHeightMm * 1e-3,
        UpperRelativePermittivity = UpperEpsR,
        UpperLossTangent = UpperTanD,
        FrequencyHz = FrequencyGhz * 1e9,
        Model = RoughnessUm > 0
            ? RlgcModel.Board with { Roughness = SurfaceRoughness.Hammerstad(RoughnessUm * 1e-6) }
            : RlgcModel.Board
    };

    [RelayCommand]
    private async Task Calculate()
    {
        Result = "Solving…";
        Assumptions = "";
        try
        {
            var spec = BuildSpec();
            var report = await Task.Run(() => ImpedanceCalculator.Solve(spec));
            Result = string.Join(Environment.NewLine, report.Describe());
            Assumptions = "Assumptions: " + string.Join(" ", report.Assumptions);
        }
        catch (Exception ex)
        {
            Result = "Not solved: " + ex.Message;
        }
    }
}
