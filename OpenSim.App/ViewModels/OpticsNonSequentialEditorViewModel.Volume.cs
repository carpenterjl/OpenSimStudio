using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.NonSequential.Volume;

namespace OpenSim.App.ViewModels;

// OP-81: the Volume Physics fields of the current object (docs/Optics-Conventions.md §77). The
// fields show the current object's saved values (Read loads them; changing the current object
// reloads them); Apply writes them into the object and publishes. Apply is not a step on the
// sheet's undo stack.
public sealed partial class OpticsNonSequentialEditorViewModel
{
    /// <summary>The Volume Physics models (help, "Volume Physics").</summary>
    public IReadOnlyList<string> VolumeModels => ProjectNscVolume.ModelNames;

    /// <summary>Standard and Basic photoluminescence.</summary>
    public IReadOnlyList<string> PhotoluminescenceModels => ProjectNscVolume.PhotoluminescenceNames;

    [ObservableProperty] private string _volumeModel = "None";
    /// <summary>Lens units.</summary>
    [ObservableProperty] private double _volumeMeanPath;
    [ObservableProperty] private double _volumeAngle;
    [ObservableProperty] private string _volumeDll = "";
    /// <summary>The DLL parameters separated by spaces, parameter 1 first.</summary>
    [ObservableProperty] private string _volumeDllParameters = "";
    [ObservableProperty] private string _wavelengthShift = "";
    [ObservableProperty] private string _photoluminescenceModel = "Standard";
    [ObservableProperty] private string _absorptionFile = "";
    [ObservableProperty] private string _emissionFile = "";
    [ObservableProperty] private string _quantumYieldFile = "";
    [ObservableProperty] private double _extinctionCoefficient;
    [ObservableProperty] private double _extinctionWavelength;
    [ObservableProperty] private double _phosphorDensity;
    [ObservableProperty] private bool _considerMie;
    [ObservableProperty] private double _particleIndex = 1.5;
    /// <summary>µm.</summary>
    [ObservableProperty] private double _particleRadius;
    /// <summary>cm⁻³.</summary>
    [ObservableProperty] private double _particleDensity;
    /// <summary>Lens units.</summary>
    [ObservableProperty] private double _mieMeanFreePath;
    [ObservableProperty] private bool _useMieMeanFreePath;

    /// <summary>The photoluminescence mean free path at the extinction wavelength (Standard model),
    /// shown after Read or Apply; empty when it cannot be computed.</summary>
    [ObservableProperty] private string _volumeInfo = "";

    /// <summary>Loads the current object's Volume Physics fields.</summary>
    [RelayCommand]
    private void ReadVolume() => LoadVolumeFields();

    private void LoadVolumeFields()
    {
        if (Document.RowCount == 0) return;
        var v = ReadRow(Document.Rows[Current]).VolumePhysics;
        VolumeModel = ProjectNscVolume.ModelOf(v?.Model) is { } m ? ProjectNscVolume.ModelNames[(int)m] : v?.Model ?? "None";
        VolumeMeanPath = v?.MeanPath ?? 0;
        VolumeAngle = v?.Angle ?? 0;
        VolumeDll = v?.Dll ?? "";
        VolumeDllParameters = string.Join(" ", (v?.DllParameters ?? new List<double>()).Select(p => p.ToString("R", CultureInfo.InvariantCulture)));
        WavelengthShift = v?.WavelengthShift ?? "";
        PhotoluminescenceModel = v?.PhotoluminescenceModel ?? "Standard";
        AbsorptionFile = v?.AbsorptionFile ?? "";
        EmissionFile = v?.EmissionFile ?? "";
        QuantumYieldFile = v?.QuantumYieldFile ?? "";
        ExtinctionCoefficient = v?.ExtinctionCoefficient ?? 0;
        ExtinctionWavelength = v?.ExtinctionWavelength ?? 0;
        PhosphorDensity = v?.PhosphorDensity ?? 0;
        ConsiderMie = v?.ConsiderMie ?? false;
        ParticleIndex = v?.ParticleIndex ?? 1.5;
        ParticleRadius = v?.ParticleRadius ?? 0;
        ParticleDensity = v?.ParticleDensity ?? 0;
        MieMeanFreePath = v?.MieMeanFreePath ?? 0;
        UseMieMeanFreePath = v?.UseMieMeanFreePath ?? false;
        VolumeInfo = Info(v);
    }

    /// <summary>Writes the fields into the current object's Volume Physics and publishes; refusals
    /// go to <see cref="Status"/>.</summary>
    [RelayCommand]
    private void ApplyVolume() => Status = ApplyVolumeFields() ?? $"Object {Current + 1}: Volume Physics applied.";

    /// <summary>As <see cref="ApplyVolumeCommand"/>; returns why not, or null.</summary>
    public string? ApplyVolumeFields()
    {
        if (Document.RowCount == 0) return "There is no object.";
        var model = ProjectNscVolume.ModelOf(VolumeModel);
        if (model is null) return $"The Volume Physics model '{VolumeModel}' is not one of {string.Join(", ", VolumeModels)}.";
        if (!PhotoluminescenceModels.Contains(PhotoluminescenceModel)) return $"The photoluminescence model '{PhotoluminescenceModel}' is not Standard or Basic.";
        var parameters = new List<double>();
        foreach (var t in (VolumeDllParameters ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return $"The DLL parameter '{t}' is not a number.";
            parameters.Add(p);
        }
        if (parameters.Count > 16) return $"{parameters.Count} DLL parameters; a bulk-scatter DLL takes at most 16 (help, \"Bulk scattering\").";
        if (!string.IsNullOrWhiteSpace(WavelengthShift))
        {
            try { NscWavelengthShift.Parse(WavelengthShift); }
            catch (FormatException e) { return e.Message; }
        }
        static string? S(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        OpticsNscVolumePhysicsSettings? saved = null;
        if (model != NscVolumeModel.None || S(WavelengthShift) is not null)
        {
            bool angleOrDll = model is NscVolumeModel.AngleScattering or NscVolumeModel.DllDefined;
            bool dll = model == NscVolumeModel.DllDefined, phosphor = model == NscVolumeModel.PhosphorsAndFluorescence;
            saved = new OpticsNscVolumePhysicsSettings
            {
                Model = VolumeModels[(int)model.Value],
                MeanPath = angleOrDll ? VolumeMeanPath : null,
                Angle = angleOrDll ? VolumeAngle : null,
                Dll = dll ? S(VolumeDll) : null,
                DllParameters = dll && parameters.Count > 0 ? parameters : null,
                WavelengthShift = S(WavelengthShift),
                PhotoluminescenceModel = phosphor ? PhotoluminescenceModel : null,
                AbsorptionFile = phosphor ? S(AbsorptionFile) : null,
                EmissionFile = phosphor ? S(EmissionFile) : null,
                QuantumYieldFile = phosphor ? S(QuantumYieldFile) : null,
                ExtinctionCoefficient = phosphor ? ExtinctionCoefficient : null,
                ExtinctionWavelength = phosphor ? ExtinctionWavelength : null,
                PhosphorDensity = phosphor ? PhosphorDensity : null,
                ConsiderMie = phosphor ? ConsiderMie : null,
                ParticleIndex = phosphor ? ParticleIndex : null,
                ParticleRadius = phosphor ? ParticleRadius : null,
                ParticleDensity = phosphor && !UseMieMeanFreePath ? ParticleDensity : null,
                MieMeanFreePath = phosphor && UseMieMeanFreePath ? MieMeanFreePath : null,
                UseMieMeanFreePath = phosphor ? UseMieMeanFreePath : null,
            };
        }
        var sheetRow = Document.Rows[Current];
        _base[sheetRow.Id] = ReadRow(sheetRow) with { VolumePhysics = saved };
        AfterChange(publish: true);
        VolumeInfo = Info(saved);
        return null;
    }

    /// <summary>"Mean Free Path at Extinction Wavelength" (help) in lens units, or why not.</summary>
    private string Info(OpticsNscVolumePhysicsSettings? saved)
    {
        if (saved is null || ProjectNscVolume.ModelOf(saved.Model) != NscVolumeModel.PhosphorsAndFluorescence) return "";
        var problems = new List<string>();
        var unit = _unit();
        var physics = ProjectNscVolume.Physics(saved, unit, $"Object {Current + 1}", problems);
        if (physics?.Phosphor is not { Model: NscPhotoluminescenceModel.Standard } p) return problems.Count > 0 ? problems[0] : "";
        var check = new List<string>();
        p.Validate("", check);
        if (check.Count > 0) return "";
        double l = p.PhotoluminescenceMeanFreePath(p.ExtinctionMicrometers);
        return $"Mean free path at the extinction wavelength: {OpticsConventions.FromMeters(l, unit).ToString("g6", CultureInfo.InvariantCulture)} lens units.";
    }
}
