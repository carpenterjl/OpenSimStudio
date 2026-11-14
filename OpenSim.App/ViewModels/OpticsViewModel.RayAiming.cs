using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The ray-aiming group of the system data (plan item OP-22): the mode, robust aiming, and the
/// pupil shift (lens units) and compression that seed the aim. Built into
/// <see cref="OpticalSystemData.RayAiming"/> by <see cref="BuildSystemData"/>.
/// </summary>
public partial class OpticsViewModel
{
    public IReadOnlyList<OpticsChoice<RayAimingMode>> RayAimingChoices { get; } = OpticsChoice<RayAimingMode>.From(RayAimingModes.Names);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RayAimingIsOn))]
    private OpticsChoice<RayAimingMode> _selectedRayAiming = Choice(RayAimingModes.Names, RayAimingMode.Off);

    [ObservableProperty] private bool _robustRayAiming;

    /// <summary>Pupil shifts, lens units.</summary>
    [ObservableProperty] private double _pupilShiftX;
    [ObservableProperty] private double _pupilShiftY;
    [ObservableProperty] private double _pupilShiftZ;

    [ObservableProperty] private double _pupilCompressX;
    [ObservableProperty] private double _pupilCompressY;
    [ObservableProperty] private bool _scalePupilShiftByField;

    /// <summary>True when rays are aimed at the stop; the shift and compression apply only then.</summary>
    public bool RayAimingIsOn => SelectedRayAiming.Value != RayAimingMode.Off;

    partial void OnSelectedRayAimingChanged(OpticsChoice<RayAimingMode> value) { if (value is null) SelectedRayAiming = RayAimingChoices[0]; }

    private RayAiming BuildRayAiming(double perUnit) => new()
    {
        Mode = SelectedRayAiming.Value,
        Robust = RobustRayAiming,
        PupilShiftX = PupilShiftX * perUnit,
        PupilShiftY = PupilShiftY * perUnit,
        PupilShiftZ = PupilShiftZ * perUnit,
        PupilCompressX = PupilCompressX,
        PupilCompressY = PupilCompressY,
        ScalePupilShiftByField = ScalePupilShiftByField,
    };

    private void LoadRayAiming(RayAiming aiming, double perUnit)
    {
        SelectedRayAiming = Choice(RayAimingChoices, aiming.Mode);
        RobustRayAiming = aiming.Robust;
        PupilShiftX = aiming.PupilShiftX / perUnit;
        PupilShiftY = aiming.PupilShiftY / perUnit;
        PupilShiftZ = aiming.PupilShiftZ / perUnit;
        PupilCompressX = aiming.PupilCompressX;
        PupilCompressY = aiming.PupilCompressY;
        ScalePupilShiftByField = aiming.ScalePupilShiftByField;
    }

    /// <summary>The ray-aiming group as the files store it (shifts in lens units).</summary>
    private OpticsSystemSettings WithRayAiming(OpticsSystemSettings settings) => settings with
    {
        RayAiming = RayAimingModes.Names.SavedName(SelectedRayAiming.Value),
        RobustRayAiming = RobustRayAiming,
        PupilShiftX = PupilShiftX,
        PupilShiftY = PupilShiftY,
        PupilShiftZ = PupilShiftZ,
        PupilCompressX = PupilCompressX,
        PupilCompressY = PupilCompressY,
        ScalePupilShiftByField = ScalePupilShiftByField,
    };

    /// <summary>Sets the group from a file; a file from before OP-22 (null fields) opens with
    /// aiming off and no shift or compression, not the panel's current values.</summary>
    private void LoadRayAimingSettings(OpticsSystemSettings saved, ICollection<string> notes)
    {
        var d = new RayAiming();
        SelectedRayAiming = saved.RayAiming is null ? Choice(RayAimingChoices, d.Mode)
            : Pick(RayAimingChoices, RayAimingModes.Names, saved.RayAiming, SelectedRayAiming, "Ray aiming", notes);
        RobustRayAiming = saved.RobustRayAiming ?? d.Robust;
        PupilShiftX = saved.PupilShiftX ?? 0;
        PupilShiftY = saved.PupilShiftY ?? 0;
        PupilShiftZ = saved.PupilShiftZ ?? 0;
        PupilCompressX = saved.PupilCompressX ?? d.PupilCompressX;
        PupilCompressY = saved.PupilCompressY ?? d.PupilCompressY;
        ScalePupilShiftByField = saved.ScalePupilShiftByField ?? d.ScalePupilShiftByField;
    }

    /// <summary>A new lens unit keeps the pupil shifts' lengths.</summary>
    private void ConvertRayAimingLengths(Func<double, double> convert)
    {
        PupilShiftX = convert(PupilShiftX);
        PupilShiftY = convert(PupilShiftY);
        PupilShiftZ = convert(PupilShiftZ);
    }
}
