using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Optics;
using OpenSim.Optics.Materials;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-18: the system data as the Optics panel edits it.</summary>
public sealed class OpticsSystemDataTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    [Fact]
    public void ThePanel_StartsWithTheDefaultSystem()
    {
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(OpticalSystemData.Default),
            System.Text.Json.JsonSerializer.Serialize(Optics.BuildSystemData()));
        Assert.False(Optics.HasSystemProblems);
        Assert.Equal("Entrance pupil diameter [mm]", Optics.ApertureValueLabel);
    }

    /// <summary>OP-51: the system polarization. The Jones fields show only when the light is
    /// polarized; a zero Jones vector is a system problem unless the light is unpolarized; the
    /// panel's choices reach the record the engines take.</summary>
    [Fact]
    public void ThePolarization_IsEdited_Shown_AndChecked()
    {
        var optics = Optics;
        Assert.Equal(SystemPolarization.Default, optics.BuildSystemData().Polarization);
        Assert.True(optics.PolarizationTakesJones);
        Assert.Equal(3, optics.PolarizationReferenceChoices.Count);

        optics.PolarizationJx = 0;
        Assert.True(optics.HasSystemProblems);
        Assert.Contains("Jx and Jy are both zero", optics.SystemCheck);
        optics.Unpolarized = true;
        Assert.False(optics.PolarizationTakesJones);
        Assert.False(optics.HasSystemProblems);

        optics.PolarizationJy = 0.5;
        optics.PolarizationYPhaseDegrees = 90;
        optics.SelectedPolarizationReference = optics.PolarizationReferenceChoices.Single(c => c.Value == PolarizationReference.ZAxis);
        optics.ConvertThinFilmPhase = false;
        Assert.Equal(new SystemPolarization
        {
            Unpolarized = true, Jx = 0, Jy = 0.5, YPhaseDegrees = 90, Reference = PolarizationReference.ZAxis, ConvertThinFilmPhase = false,
        }, optics.BuildSystemData().Polarization);
    }

    /// <summary>OP-13: the air model picker. Ciddor is the default (owner decision OQ-10); the
    /// CO2 and humidity fields show only for the models that read them; the line under the
    /// group names the model in use; a range the model refuses is a system problem.</summary>
    [Fact]
    public void TheAirModel_IsPicked_Shown_AndChecked()
    {
        var optics = Optics;
        Assert.Equal(AirIndexModel.Ciddor, optics.SelectedAirModel.Value);
        Assert.Equal(3, optics.AirModelChoices.Count);
        Assert.True(optics.AirModelUsesCarbonDioxide);
        Assert.True(optics.AirModelUsesHumidity);
        Assert.Contains("Ciddor", optics.AirModelDescription);
        Assert.Contains("450 µmol/mol", optics.AirModelDescription);

        var raised = new List<string?>();
        optics.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        optics.RelativeHumidityPercent = 40;
        Assert.Contains(nameof(OpticsViewModel.AirModelDescription), raised);
        Assert.Contains("40 %", optics.AirModelDescription);
        Assert.Equal(40, optics.BuildSystemData().Environment.RelativeHumidityPercent);

        optics.SelectedAirModel = optics.AirModelChoices.Single(c => c.Value == AirIndexModel.Schott);
        Assert.False(optics.AirModelUsesCarbonDioxide);
        Assert.False(optics.AirModelUsesHumidity);
        Assert.Contains("OpticStudio-compatible", optics.AirModelDescription);
        Assert.Equal(AirIndexModel.Schott, optics.BuildSystemData().Environment.AirModel);

        // An infrared wavelength is outside Ciddor's range but not the TIE-19 formula's.
        optics.Wavelengths[0].Micrometers = 10.6;
        Assert.False(optics.HasSystemProblems);
        optics.SelectedAirModel = optics.AirModelChoices.Single(c => c.Value == AirIndexModel.Ciddor);
        Assert.True(optics.HasSystemProblems);
        Assert.Contains("0.3–1.7 µm", optics.SystemCheck);
    }

    [Fact]
    public void LengthsAreShownInLensUnits_AndBuiltInMeters()
    {
        var optics = Optics;
        optics.ApertureValue = 25;
        optics.SemiDiameterMargin = 0.5;
        optics.SelectedFieldType = optics.FieldTypeChoices.Single(c => c.Value == FieldType.ObjectHeight);
        optics.Fields[0].Y = 4;
        var data = optics.BuildSystemData();
        Assert.Equal(0.025, data.Aperture.Value, 15);
        Assert.Equal(0.0005, data.Aperture.SemiDiameterMargin, 15);
        Assert.Equal(0.004, data.Fields.Points[0].Y, 15);

        // An angle field and an F-number are not lengths.
        optics.SelectedFieldType = optics.FieldTypeChoices.Single(c => c.Value == FieldType.Angle);
        optics.SelectedApertureType = optics.ApertureTypeChoices.Single(c => c.Value == ApertureType.ImageSpaceFNumber);
        optics.ApertureValue = 4;
        data = optics.BuildSystemData();
        Assert.Equal(4, data.Fields.Points[0].Y);
        Assert.Equal(4, data.Aperture.Value);
    }

    [Fact]
    public void ANewLensUnit_KeepsEveryLengthsSize()
    {
        var optics = Optics;
        optics.ApertureValue = 10;
        optics.SelectedFieldType = optics.FieldTypeChoices.Single(c => c.Value == FieldType.ObjectHeight);
        optics.Fields[0].Y = 25.4;
        optics.SelectedLensUnit = optics.LensUnitChoices.Single(c => c.Value == LensUnit.Centimeter);
        Assert.Equal(1, optics.ApertureValue, 14);
        Assert.Equal("Entrance pupil diameter [cm]", optics.ApertureValueLabel);
        optics.SelectedLensUnit = optics.LensUnitChoices.Single(c => c.Value == LensUnit.Inch);
        Assert.Equal(1, optics.Fields[0].Y, 14);
        Assert.Equal(0.0254, optics.BuildSystemData().Fields.Points[0].Y, 15);
        Assert.Equal(0.01, optics.BuildSystemData().Aperture.Value, 15);
    }

    [Fact]
    public void TheCheck_FollowsEveryEdit_IncludingRows()
    {
        var optics = Optics;
        var raised = new List<string?>();
        optics.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        optics.Wavelengths[0].Micrometers = -1;
        Assert.Contains(nameof(OpticsViewModel.SystemCheck), raised);
        Assert.True(optics.HasSystemProblems);
        Assert.Contains("Wavelength 1", optics.SystemCheck);

        optics.Wavelengths[0].Micrometers = 0.6;
        Assert.False(optics.HasSystemProblems);
        optics.PrimaryWavelength = 3;
        Assert.Contains("primary", optics.SystemCheck);
    }

    [Fact]
    public void ThePresetAndTheQuadrature_ReplaceTheWavelengths()
    {
        var optics = Optics;
        optics.SelectedWavelengthPreset = SpectralLines.Presets[0];
        optics.ApplyWavelengthPresetCommand.Execute(null);
        Assert.Equal(new[] { 0.4861327, 0.5875618, 0.6562725 }, optics.Wavelengths.Select(w => w.Micrometers));
        Assert.Equal(2, optics.PrimaryWavelength);
        Assert.Equal(new[] { 1, 2, 3 }, optics.Wavelengths.Select(w => w.Number));

        optics.QuadratureMinMicrometers = 0.4;
        optics.QuadratureMaxMicrometers = 0.7;
        optics.QuadratureCount = 5;
        optics.ApplyWavelengthQuadratureCommand.Execute(null);
        Assert.Equal(5, optics.Wavelengths.Count);
        Assert.Equal(3, optics.PrimaryWavelength);
        Assert.False(optics.HasSystemProblems);
    }

    [Fact]
    public void RemovingAWavelength_KeepsThePrimaryOnTheSameLine()
    {
        var optics = Optics;
        optics.ApplyWavelengthPresetCommand.Execute(null);             // F, d, C; d primary
        optics.RemoveWavelengthCommand.Execute(optics.Wavelengths[0]);
        Assert.Equal(1, optics.PrimaryWavelength);
        Assert.Equal(0.5875618, optics.Wavelengths[optics.PrimaryWavelength - 1].Micrometers);
        optics.RemoveWavelengthCommand.Execute(optics.Wavelengths[0]);
        optics.RemoveWavelengthCommand.Execute(optics.Wavelengths[0]);   // the last row stays
        Assert.Single(optics.Wavelengths);
        Assert.Equal(1, optics.PrimaryWavelength);
    }

    [Fact]
    public void TheFieldWizard_AndRowCommands()
    {
        var optics = Optics;
        optics.WizardMaxField = 20;
        optics.WizardFieldCount = 3;
        optics.WizardEqualArea = false;
        optics.ApplyFieldWizardCommand.Execute(null);
        Assert.Equal(new[] { 0.0, 10, 20 }, optics.Fields.Select(f => f.Y));
        optics.AddFieldCommand.Execute(null);
        Assert.Equal(4, optics.Fields.Count);
        Assert.Equal(4, optics.Fields[3].Number);
        optics.RemoveFieldCommand.Execute(optics.Fields[0]);
        Assert.Equal(new[] { 1, 2, 3 }, optics.Fields.Select(f => f.Number));
    }

    [Fact]
    public void LoadSystemData_ShowsLengthsInTheDatasLensUnit_AndResetRestoresTheDefault()
    {
        var optics = Optics;
        var data = new OpticalSystemData
        {
            Title = "Doublet",
            Units = new SystemUnits { LensUnit = LensUnit.Inch, MtfUnit = MtfUnit.CyclesPerMilliradian },
            Aperture = new Aperture { Value = 0.0508, Apodization = ApodizationType.Gaussian, ApodizationFactor = 1.5 },
            Fields = new FieldSet { Type = FieldType.ObjectHeight, Points = new[] { new FieldPoint(), new FieldPoint { Y = 0.0254, CompressionY = 0.3 } } },
            Wavelengths = SpectralLines.Presets[0].ToWavelengthSet(),
            Environment = new OpticsEnvironment { TemperatureCelsius = 35, AdjustIndexToEnvironment = true }
        };
        optics.LoadSystemData(data);
        Assert.Equal(2, optics.ApertureValue, 14);
        Assert.Equal(1, optics.Fields[1].Y, 14);
        Assert.Equal(0.3, optics.Fields[1].CompressionY);
        Assert.Equal("in", optics.LengthUnitSymbol);
        Assert.Equal("Doublet", optics.SystemTitle);
        Assert.True(optics.ApodizationTakesFactor);

        optics.Reset();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(OpticalSystemData.Default),
            System.Text.Json.JsonSerializer.Serialize(optics.BuildSystemData()));
    }
}
