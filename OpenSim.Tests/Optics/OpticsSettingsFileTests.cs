using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>OP-10: the optics settings in the project file and the standalone lens file.</summary>
public sealed class OpticsSettingsFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("opensim-optics-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathFor(string name) => Path.Combine(_directory, name);

    /// <summary>Every optics field round-trips through the project file, each set by reflection
    /// to a value unlike its default, so a field added later without reaching the file fails
    /// here (the pattern of ProjectSerializerTests.PanelSettings_RoundTripEveryField).</summary>
    [Fact]
    public void OpticsSettings_RoundTripEveryField_ThroughTheProjectFile()
    {
        int counter = 0;
        var optics = (OpticsSettings)Filled(typeof(OpticsSettings), ref counter);
        Assert.NotNull(optics.SystemData);
        Assert.Equal(2, optics.SystemData!.Fields!.Count);
        Assert.Equal(2, optics.Materials!.ActiveCatalogs!.Count);
        Assert.Equal(2, optics.Surfaces!.Count);                              // OP-31
        Assert.Equal(2, optics.Surfaces[0].Flags![0].SolveParameters!.Count);
        Assert.NotNull(optics.Surfaces[0].Flags![0].Macro);                  // OP-99: a macro solve's file name
        Assert.NotNull(optics.Surfaces[0].TiltBefore);                       // TC-6: tilts and returns
        Assert.NotNull(optics.Surfaces[0].TiltAfter!.Mode);
        Assert.NotNull(optics.Surfaces[0].CoordinateReturn!.Mode);
        Assert.Equal(2, optics.Surfaces[0].AddOns!.Count);                    // OP-32: composite add-ons
        Assert.Equal(2, optics.Surfaces[0].AddOns![0].Parameters!.Count);
        Assert.Equal(2, optics.MeritFunction!.Operands!.Count);              // OP-54
        Assert.Equal(2, optics.MeritFunction.Operands[1].Data!.Count);
        Assert.Equal(2, optics.MultiConfiguration!.Operands![0].Values!.Count);   // OP-30: the table
        Assert.Equal(2, optics.Tolerances!.Operands!.Count);                 // OP-63: the tolerance data
        Assert.NotNull(optics.Tolerances.IrregularityModel);
        Assert.NotNull(optics.Tolerances.Run?.Limit);                        // OP-64: the tolerance run settings
        Assert.Equal(2, optics.Tolerances.MonteCarlo!.Percentiles!.Count);   // OP-65: the Monte Carlo settings
        Assert.NotNull(optics.Tolerances.MonteCarlo.OverlayMtf);
        Assert.NotNull(optics.Thermal!.GlassMaterials![0].Material!.ThermalExpansionCoefficient);   // OP-91: the glass links
        Assert.Equal(2, optics.GlassSubstitution!.Catalogs!.Count);           // OP-59: the substitution template
        Assert.NotNull(optics.GlassSubstitution.MaxPhosphateResistance);

        Assert.NotNull(optics.MeritWizard!.AirEdge);                         // OP-56: the wizard's saved settings
        Assert.NotNull(optics.HighYield!.Weight);                            // OP-62: the High-Yield weight
        Assert.Equal(2, optics.NonSequential!.Objects![0].Faces!.Count);     // OP-70: the non-sequential objects
        Assert.Equal(2, optics.NonSequential.Objects[0].Source!.SpectrumWeights!.Count);   // OP-77: a source's Sources tab
        Assert.NotNull(optics.NonSequential.Objects[0].Source!.ArrayType);
        Assert.Equal(2, optics.NonSequential.NamedFilters!.Count);            // OP-83: the named filters
        var project = new SimProject { Name = "Lens", AnalysisType = "Optics", Optics = optics };
        string path = PathFor("lens.ossproj");

        var serializer = new ProjectSerializer();
        serializer.Save(project, path);
        var loaded = serializer.Load(path);

        Assert.Equal(5, ProjectSerializer.CurrentFormatVersion);             // the system data arrived in 5
        Assert.Equal(ProjectSerializer.CurrentFormatVersion, loaded.FormatVersion);
        Assert.Equal(JsonSerializer.Serialize(optics), JsonSerializer.Serialize(loaded.Optics));
        AssertBitwiseEqual(optics.SystemData, loaded.Optics!.SystemData!);
    }

    [Fact]
    public void OpticsSettings_RoundTripEveryField_ThroughTheLensFile()
    {
        int counter = 100;
        var optics = (OpticsSettings)Filled(typeof(OpticsSettings), ref counter);
        string path = PathFor("doublet" + OpticsFileSerializer.FileExtension);

        var serializer = new OpticsFileSerializer();
        serializer.Save(new OpticsLensFile { Name = "Doublet", Optics = optics }, path);
        var loaded = serializer.Load(path);

        Assert.Equal(OpticsFileSerializer.CurrentFormatVersion, loaded.FormatVersion);
        Assert.Equal("Doublet", loaded.Name);
        Assert.Equal(JsonSerializer.Serialize(optics), JsonSerializer.Serialize(loaded.Optics));
        AssertBitwiseEqual(optics.SystemData!, loaded.Optics!.SystemData!);
    }

    [Fact]
    public void OlderProjects_LoadWithoutTheSystemData()
    {
        var serializer = new ProjectSerializer();
        string path = PathFor("older.ossproj");

        File.WriteAllText(path, """{ "formatVersion": 3, "name": "Older", "bodies": [] }""");
        Assert.Null(serializer.Load(path).Optics);

        // OP-1's version 4: the mode, no system data. It reads as the default system.
        File.WriteAllText(path, """{ "formatVersion": 4, "name": "Optics", "analysisType": "Optics", "optics": { "mode": "NonSequential" }, "bodies": [] }""");
        var v4 = serializer.Load(path);
        Assert.Equal(4, v4.FormatVersion);
        Assert.Equal("NonSequential", v4.Optics!.Mode);
        Assert.Null(v4.Optics.SystemData);
        var notes = new List<string>();
        Assert.Equal(JsonSerializer.Serialize(OpticalSystemData.Default),
            JsonSerializer.Serialize(SystemDataSettings.FromSettings(v4.Optics.SystemData, notes)));
        Assert.Empty(notes);

        // A system data block that only names a few fields: the rest are the defaults.
        File.WriteAllText(path, """{ "formatVersion": 5, "name": "Sparse", "optics": { "systemData": { "apertureValue": 25 } }, "bodies": [] }""");
        var sparse = SystemDataSettings.FromSettings(serializer.Load(path).Optics!.SystemData, notes);
        Assert.Equal(0.025, sparse.Aperture.Value, 15);
        Assert.Empty(sparse.Validate());
        Assert.Empty(notes);
    }

    [Fact]
    public void AFileThatIsNotALens_IsRefusedByName()
    {
        string path = PathFor("project.osslens");
        File.WriteAllText(path, """{ "formatVersion": 5, "name": "A project", "bodies": [] }""");
        var ex = Assert.Throws<InvalidDataException>(() => new OpticsFileSerializer().Load(path));
        Assert.Contains("project.osslens", ex.Message);

        File.WriteAllText(path, "not json");
        Assert.Throws<InvalidDataException>(() => new OpticsFileSerializer().Load(path));
        Assert.Throws<ArgumentException>(() => new OpticsFileSerializer().Save(new OpticsLensFile(), path));
    }

    /// <summary>The headless form: the record in meters to the file form in lens units and back.
    /// Choices and every unscaled number come back exactly; lengths within an ulp of the scale.</summary>
    [Fact]
    public void SystemDataSettings_RoundTripTheRecord()
    {
        var data = new OpticalSystemData
        {
            Title = "Cooke triplet",
            Notes = "line 1\nline 2",
            Units = new SystemUnits { LensUnit = LensUnit.Inch, SourceUnit = SourceUnit.Lumens, AnalysisAreaUnit = LensUnit.Centimeter, MtfUnit = MtfUnit.CyclesPerMilliradian },
            Aperture = new Aperture
            {
                Type = ApertureType.EntrancePupilDiameter, Value = 0.0127, Apodization = ApodizationType.Gaussian, ApodizationFactor = 0.75,
                SemiDiameterMargin = 0.000254, SemiDiameterMarginPercent = 2.5, AfocalImageSpace = true
            },
            Fields = new FieldSet
            {
                Type = FieldType.ObjectHeight, Normalization = FieldNormalization.Rectangular,
                Points = new[] { new FieldPoint(), new FieldPoint { X = 0.00254, Y = -0.0508, Weight = 0.5, DecenterX = 0.1, DecenterY = -0.2, CompressionX = 0.3, CompressionY = 0.4, TiltDegrees = 12 } }
            },
            Wavelengths = SpectralLines.Presets[2].ToWavelengthSet() with { PrimaryIndex = 3 },
            Environment = new OpticsEnvironment
            {
                TemperatureCelsius = -10.5, PressureAtmospheres = 0, AdjustIndexToEnvironment = true,
                AirModel = AirIndexModel.ModifiedEdlen, CarbonDioxideMicromolesPerMole = 512.5, RelativeHumidityPercent = 37.5
            },
            Polarization = new SystemPolarization   // OP-51
            {
                Unpolarized = true, Jx = 0.5, Jy = 0.75, XPhaseDegrees = 30, YPhaseDegrees = -45.5,
                Reference = PolarizationReference.YAxis, ConvertThinFilmPhase = false
            }
        };
        var notes = new List<string>();
        var settings = SystemDataSettings.ToSettings(data);
        Assert.Equal("Inch", settings.LensUnit);
        Assert.Equal(0.5, settings.ApertureValue, 14);                   // 0.0127 m in inches
        Assert.Equal(4, settings.PrimaryWavelength);                     // 1-based in the file
        Assert.Equal("ModifiedEdlen", settings.AirIndexModel);           // OP-13: by saved name

        var back = SystemDataSettings.FromSettings(settings, notes);
        Assert.Empty(notes);
        Assert.Equal(data.Units, back.Units);
        Assert.Equal(data.Environment, back.Environment);
        Assert.Equal(data.Polarization, back.Polarization);              // OP-51
        Assert.Equal(data.Title, back.Title);
        Assert.Equal(data.Notes, back.Notes);
        Assert.Equal(data.Wavelengths.Wavelengths, back.Wavelengths.Wavelengths);   // micrometers, unscaled
        Assert.Equal(data.Wavelengths.PrimaryIndex, back.Wavelengths.PrimaryIndex);
        Assert.Equal(data.Aperture with { Value = 0, SemiDiameterMargin = 0 }, back.Aperture with { Value = 0, SemiDiameterMargin = 0 });
        AssertWithinAnUlpOrTwo(data.Aperture.Value, back.Aperture.Value);
        AssertWithinAnUlpOrTwo(data.Aperture.SemiDiameterMargin, back.Aperture.SemiDiameterMargin);
        Assert.Equal(data.Fields.Type, back.Fields.Type);
        Assert.Equal(data.Fields.Normalization, back.Fields.Normalization);
        for (int i = 0; i < data.Fields.Points.Count; i++)
        {
            var (a, b) = (data.Fields.Points[i], back.Fields.Points[i]);
            Assert.Equal(a with { X = 0, Y = 0 }, b with { X = 0, Y = 0 });
            AssertWithinAnUlpOrTwo(a.X, b.X);
            AssertWithinAnUlpOrTwo(a.Y, b.Y);
        }

        // An angle field is not scaled at all.
        var angles = data with { Fields = new FieldSet { Points = new[] { new FieldPoint { Y = 0.1 + 0.2 } } } };
        Assert.Equal(0.1 + 0.2, SystemDataSettings.FromSettings(SystemDataSettings.ToSettings(angles), notes).Fields.Points[0].Y);
    }

    /// <summary>OP-13: a file from before the air model (null fields) opens with the default
    /// air: Ciddor, 450 µmol/mol CO2, dry air.</summary>
    [Fact]
    public void AFileWithoutTheAirModel_OpensWithTheDefaultAir()
    {
        var notes = new List<string>();
        var environment = SystemDataSettings.FromSettings(new OpticsSystemSettings(), notes).Environment;
        Assert.Empty(notes);
        Assert.Equal(new OpticsEnvironment(), environment);
        Assert.Equal(AirIndexModel.Ciddor, environment.AirModel);
        Assert.Equal(450, environment.CarbonDioxideMicromolesPerMole);
        Assert.Equal(0, environment.RelativeHumidityPercent);

        environment = SystemDataSettings.FromSettings(new OpticsSystemSettings { AirIndexModel = "Kohlrausch" }, notes).Environment;
        Assert.Equal(AirIndexModel.Ciddor, environment.AirModel);
        Assert.Contains(notes, n => n.Contains("'Kohlrausch'"));
    }

    [Fact]
    public void ChoicesThisVersionDoesNotKnow_AreNamed_AndTheDefaultKept()
    {
        var notes = new List<string>();
        var data = SystemDataSettings.FromSettings(new OpticsSystemSettings
        {
            ApertureType = "ParaxialImageSpaceNA", FieldType = "Hologram", LensUnit = "Furlong", MtfUnit = null
        }, notes);
        Assert.Equal(ApertureType.EntrancePupilDiameter, data.Aperture.Type);
        Assert.Equal(FieldType.Angle, data.Fields.Type);
        Assert.Equal(LensUnit.Millimeter, data.Units.LensUnit);
        Assert.Equal(3, notes.Count);
        Assert.Contains(notes, n => n.Contains("'ParaxialImageSpaceNA'"));
        Assert.Contains(notes, n => n.Contains("'Hologram'"));
        Assert.Contains(notes, n => n.Contains("'Furlong'"));
    }

    /// <summary>A length scaled into lens units and back: the two roundings move it by at most
    /// two units in the last place.</summary>
    private static void AssertWithinAnUlpOrTwo(double expected, double actual)
    {
        double ulp = Math.BitIncrement(Math.Abs(expected)) - Math.Abs(expected);
        Assert.True(Math.Abs(expected - actual) <= 2 * ulp, $"{actual:R} is more than two ulps from {expected:R}.");
    }

    /// <summary>Every double of the system data, compared bit for bit: the ray trace reads
    /// nothing else, so equal bits here are an identical trace after a reload.</summary>
    private static void AssertBitwiseEqual(OpticsSystemSettings expected, OpticsSystemSettings actual)
    {
        static IEnumerable<double> Doubles(object o) => o.GetType().GetProperties().SelectMany(p =>
        {
            var v = p.GetValue(o);
            return v switch
            {
                double d => new[] { d },
                System.Collections.IEnumerable list and not string => list.Cast<object>().SelectMany(Doubles),
                _ => Array.Empty<double>()
            };
        });
        Assert.Equal(Doubles(expected).Select(BitConverter.DoubleToInt64Bits), Doubles(actual).Select(BitConverter.DoubleToInt64Bits));
    }

    /// <summary>An instance of a settings record with every public property unlike its default:
    /// numbers from a running counter (an int past 1000, a double whose shortest decimal form is
    /// long, so the JSON must carry every digit), booleans flipped, strings "label n", lists of two
    /// filled rows (two strings for a list of names), nested records filled.</summary>
    private static object Filled(Type type, ref int counter)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties().Where(p => p.CanWrite))
        {
            counter++;
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value;
            if (t == typeof(double)) value = counter / 7.0 + 0.1;
            else if (t == typeof(int)) value = 1000 + counter;
            else if (t == typeof(bool)) value = property.GetValue(instance) is bool b ? !b : true;
            // The analysis windows (OP-9) keep each analysis's settings as raw JSON.
            else if (t == typeof(JsonElement)) value = JsonFor(counter);
            else if (t == typeof(Dictionary<string, JsonElement>))
                value = new Dictionary<string, JsonElement> { [$"key {counter}"] = JsonFor(counter), [$"key {counter + 1}"] = JsonFor(counter + 1) };
            else if (t == typeof(string)) value = $"label {counter}";
            // The active glass catalogs (OP-11) are a list of names.
            else if (t == typeof(List<string>)) value = new List<string> { $"label {counter}", $"label {counter + 1}" };
            // OP-31: a solve's parameters are a list of numbers.
            else if (t == typeof(List<double>)) value = new List<double> { counter / 7.0 + 0.1, counter / 11.0 + 0.2 };
            // OP-64: the tolerance run's compensator rows are a list of row numbers.
            else if (t == typeof(List<int>)) value = new List<int> { 1000 + counter, 1001 + counter };
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (System.Collections.IList)Activator.CreateInstance(t)!;
                for (int i = 0; i < 2; i++) list.Add(Filled(t.GetGenericArguments()[0], ref counter));
                value = list;
            }
            else value = Filled(t, ref counter);
            Assert.NotEqual(property.GetValue(instance), value);
            property.SetValue(instance, value);
        }
        return instance;
    }

    private static JsonElement JsonFor(int counter) =>
        JsonDocument.Parse($"{{\"n\":{counter},\"s\":\"label {counter}\"}}").RootElement.Clone();
}
