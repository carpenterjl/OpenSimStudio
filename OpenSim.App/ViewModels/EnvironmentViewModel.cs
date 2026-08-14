using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.App.ViewModels;

/// <summary>A medium choice as the picker shows it.</summary>
public sealed record MediumOption(string Label, MediumKind Kind)
{
    public static readonly IReadOnlyList<MediumOption> All = new[]
    {
        new MediumOption("Free space (vacuum — radiation only)", MediumKind.Vacuum),
        new MediumOption("Still fluid (natural convection)", MediumKind.StillFluid),
        new MediumOption("Moving fluid (forced convection)", MediumKind.MovingFluid)
    };
}

/// <summary>
/// Edits the environment the bodies sit in. <see cref="EnvironmentSettings"/> is an
/// immutable record, so every change rewrites <c>Project.Environment</c> from the
/// current field values — the project always holds a complete, self-consistent
/// environment rather than one being edited in place.
/// </summary>
public partial class EnvironmentViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private bool _loading;

    public EnvironmentViewModel(ProjectSession session)
    {
        _session = session;
        session.GeometryReplaced += (_, _) => LoadFromProject();
        LoadFromProject();
    }

    public IReadOnlyList<MediumOption> Mediums => MediumOption.All;
    public IReadOnlyList<FluidProperties> Fluids => FluidLibrary.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsFluid))]
    [NotifyPropertyChangedFor(nameof(NeedsFlow))]
    [NotifyPropertyChangedFor(nameof(Description))]
    private MediumOption _selectedMedium = MediumOption.All[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private FluidProperties _selectedFluid = FluidLibrary.Air;

    /// <summary>Ambient temperature [K] — the convective sink AND the radiative
    /// surroundings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private double _ambientTemperature = 293.15;

    /// <summary>Free-stream speed [m/s], along <see cref="FlowDirection"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private double _flowSpeed = 1.0;

    /// <summary>Free-stream direction; normalized when the settings are built.</summary>
    [ObservableProperty] private string _flowDirection = "1, 0, 0";

    /// <summary>Gravity direction — it classifies surfaces (vertical / facing up / facing
    /// down), which is what selects the natural-convection correlation.</summary>
    [ObservableProperty] private string _gravityDirection = "0, 0, -1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private bool _includeRadiation = true;

    /// <summary>A vacuum has no fluid to pick.</summary>
    public bool NeedsFluid => SelectedMedium.Kind != MediumKind.Vacuum;

    /// <summary>Only a moving medium has a free-stream velocity.</summary>
    public bool NeedsFlow => SelectedMedium.Kind == MediumKind.MovingFluid;

    /// <summary>The one-line summary that also goes into the solve log.</summary>
    public string Description => Build().Describe();

    partial void OnSelectedMediumChanged(MediumOption value) => Push();
    partial void OnSelectedFluidChanged(FluidProperties value) => Push();
    partial void OnAmbientTemperatureChanged(double value) => Push();
    partial void OnFlowSpeedChanged(double value) => Push();
    partial void OnFlowDirectionChanged(string value) => Push();
    partial void OnGravityDirectionChanged(string value) => Push();
    partial void OnIncludeRadiationChanged(bool value) => Push();

    /// <summary>The settings the solve consumes.</summary>
    public EnvironmentSettings Build() => new()
    {
        Medium = SelectedMedium.Kind,
        AmbientTemperature = AmbientTemperature,
        FluidName = SelectedFluid.Name,
        Gravity = ParseDirection(GravityDirection, new Vector3D(0, 0, -1)) * 9.80665,
        FlowVelocity = ParseDirection(FlowDirection, new Vector3D(1, 0, 0)) * FlowSpeed,
        IncludeRadiation = IncludeRadiation
    };

    private void Push()
    {
        if (_loading) return;
        _session.Project.Environment = Build();
        OnPropertyChanged(nameof(Description));
    }

    private void LoadFromProject()
    {
        var env = _session.Project.Environment;
        _loading = true;
        try
        {
            if (env is null)
            {
                // No environment on the project yet: keep the current editor state and
                // publish it, so a fresh project is always solvable as-is.
                _loading = false;
                Push();
                return;
            }
            SelectedMedium = MediumOption.All.First(m => m.Kind == env.Medium);
            SelectedFluid = env.ResolveFluid() ?? FluidLibrary.Air;
            AmbientTemperature = env.AmbientTemperature;
            FlowSpeed = env.FlowVelocity.Length;
            FlowDirection = FormatDirection(env.FlowVelocity, new Vector3D(1, 0, 0));
            GravityDirection = FormatDirection(env.Gravity, new Vector3D(0, 0, -1));
            IncludeRadiation = env.IncludeRadiation;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Description));
    }

    /// <summary>Parses "x, y, z" into a UNIT vector; anything unparseable or degenerate
    /// falls back to the default direction rather than producing a zero-length axis that
    /// would make the surface classifier meaningless.</summary>
    private static Vector3D ParseDirection(string text, Vector3D fallback)
    {
        var parts = (text ?? string.Empty).Split(new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !double.TryParse(parts[0], out double x)
            || !double.TryParse(parts[1], out double y)
            || !double.TryParse(parts[2], out double z))
            return fallback;
        var v = new Vector3D(x, y, z);
        return v.Length > 1e-12 ? v * (1.0 / v.Length) : fallback;
    }

    private static string FormatDirection(Vector3D v, Vector3D fallback)
    {
        var d = v.Length > 1e-12 ? v * (1.0 / v.Length) : fallback;
        return $"{d.X:g4}, {d.Y:g4}, {d.Z:g4}";
    }
}
