using System.IO;
using System.Text.Json;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Materials;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-10: the Optics panel's system data through the project file and the lens file.</summary>
public sealed class OpticsSettingsTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>A system unlike the default in every group, in inches so the lengths are not
    /// the default unit's.</summary>
    private static void Edit(OpticsViewModel optics)
    {
        optics.SelectedLensUnit = optics.LensUnitChoices.Single(c => c.Value == LensUnit.Inch);
        optics.SelectedSourceUnit = optics.SourceUnitChoices.Single(c => c.Value == SourceUnit.Joules);
        optics.SelectedAnalysisAreaUnit = optics.LensUnitChoices.Single(c => c.Value == LensUnit.Centimeter);
        optics.SelectedMtfUnit = optics.MtfUnitChoices.Single(c => c.Value == MtfUnit.CyclesPerMilliradian);
        optics.SystemTitle = "Petzval";
        optics.SystemNotes = "f/2, 100 mm";
        optics.ApertureValue = 0.1 + 0.2;                    // a value that needs all 17 digits
        optics.SelectedApodization = optics.ApodizationChoices.Single(c => c.Value == ApodizationType.Gaussian);
        optics.ApodizationFactor = 1.25;
        optics.SemiDiameterMargin = 0.02;
        optics.SemiDiameterMarginPercent = 3;
        optics.AfocalImageSpace = true;
        optics.SelectedFieldType = optics.FieldTypeChoices.Single(c => c.Value == FieldType.RealImageHeight);
        optics.SelectedFieldNormalization = optics.FieldNormalizationChoices.Single(c => c.Value == FieldNormalization.Rectangular);
        optics.Fields[0].Y = 1.0 / 3;
        optics.AddFieldCommand.Execute(null);
        optics.Fields[1].X = -0.7;
        optics.Fields[1].CompressionY = 0.25;
        optics.Fields[1].TiltDegrees = 7.5;
        optics.Fields[1].Weight = 2;
        optics.SelectedWavelengthPreset = SpectralLines.Presets[1];
        optics.ApplyWavelengthPresetCommand.Execute(null);
        optics.Wavelengths[2].Weight = 0.5;
        optics.PrimaryWavelength = 3;
        optics.TemperatureCelsius = 40;
        optics.PressureAtmospheres = 0.8;
        optics.AdjustIndexToEnvironment = true;
        optics.SelectedAirModel = optics.AirModelChoices.Single(c => c.Value == AirIndexModel.ModifiedEdlen);   // OP-13
        optics.CarbonDioxideMicromolesPerMole = 1000.0 / 3;
        optics.RelativeHumidityPercent = 45;
        optics.SelectedMode = optics.Modes.Single(m => m.Mode == OpticsMode.NonSequential);
        // OP-15: a coating catalog with a material, a layer coating and an ideal coating.
        optics.Coatings.AddMaterialCommand.Execute(null);
        optics.Coatings.IndexRows[0].K = 0.125;
        optics.Coatings.AddLayerCoatingCommand.Execute(null);
        optics.Coatings.AddLayerCommand.Execute(null);
        optics.Coatings.Layers[0].Thickness = 1.0 / 3;
        optics.Coatings.AddIdealCoatingCommand.Execute(null);
        optics.Coatings.IdealReflection = 0.1;
    }

    [Fact]
    public void TheSystemData_SavesWithTheProject_AndReloadsToTheSameSystem()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<ProjectSession>();
        main.EnterWorkspaceCommand.Execute(WorkspaceKind.Optics);
        Edit(main.Optics);
        string before = JsonSerializer.Serialize(main.Optics.BuildSystemData());
        var settingsBefore = JsonSerializer.Serialize(main.Optics.ToSettings());

        string path = Path.Combine(_app.UserDirectory, "petzval.ossproj");
        main.CaptureProjectSettings();
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);

        // Meanwhile the app moves on, so the restore below is seen to come from the file.
        main.NewProjectCommand.Execute(null);
        Assert.Equal(JsonSerializer.Serialize(OpticalSystemData.Default), JsonSerializer.Serialize(main.Optics.BuildSystemData()));

        main.LoadProjectFromPath(path);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(main.Optics.ToSettings()));
        // The record the engines take is the same, bit for bit (JSON writes doubles round-trip).
        Assert.Equal(before, JsonSerializer.Serialize(main.Optics.BuildSystemData()));
        Assert.Equal(0.1 + 0.2, main.Optics.ApertureValue);
        Assert.Equal(LensUnit.Inch, main.Optics.SelectedLensUnit.Value);       // the lengths were not rescaled on load
        Assert.Equal(OpticsMode.NonSequential, main.Optics.Mode);
        Assert.DoesNotContain(_app.Log.Entries, e => e.Contains("not one this version knows"));
    }

    [Fact]
    public void ALensFile_CarriesTheOpticalSystemOnItsOwn()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        Edit(optics);
        string before = JsonSerializer.Serialize(optics.ToSettings());
        string path = Path.Combine(_app.UserDirectory, "petzval" + OpticsFileSerializer.FileExtension);

        optics.SaveLensFile(path);
        Assert.Contains(path, optics.LensFileStatus);
        Assert.Equal("Petzval", new OpticsFileSerializer().Load(path).Name);

        optics.Reset();
        Assert.Empty(optics.LoadLensFile(path));
        Assert.Equal(before, JsonSerializer.Serialize(optics.ToSettings()));
        Assert.Contains("'Petzval'", optics.LensFileStatus);
    }

    [Fact]
    public void AnOP1Project_KeepsThePanelsSystem_AndAnUnknownChoiceIsNamed()
    {
        var main = _app.Resolve<MainViewModel>();
        main.Optics.ApertureValue = 12;
        string path = Path.Combine(_app.UserDirectory, "v4.ossproj");
        File.WriteAllText(path, """{ "formatVersion": 4, "name": "OP-1", "analysisType": "Optics", "optics": { "mode": "Sequential" }, "bodies": [] }""");
        main.LoadProjectFromPath(path);
        Assert.Equal(12, main.Optics.ApertureValue);                       // no system data: the panel is left as it is

        File.WriteAllText(path, """{ "formatVersion": 9, "name": "Later", "analysisType": "Optics", "optics": { "systemData": { "apertureType": "HyperNA", "apertureValue": 4 } }, "bodies": [] }""");
        main.LoadProjectFromPath(path);
        Assert.Equal(ApertureType.EntrancePupilDiameter, main.Optics.SelectedApertureType.Value);
        Assert.Equal(4, main.Optics.ApertureValue);
        Assert.Contains(_app.Log.Entries, e => e.Contains("'HyperNA'"));
    }
}
