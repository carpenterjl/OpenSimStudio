using OpenSim.Optics;

namespace OpenSim.Tests.Optics;

/// <summary>OP-18: the system data records, their validation, the saved names, the field wizard,
/// the spectral-line presets and the wavelength quadrature.</summary>
public sealed class SystemDataTests
{
    [Fact]
    public void TheDefaultSystem_IsValid()
    {
        var data = OpticalSystemData.Default;
        Assert.Empty(data.Validate());
        Assert.Equal(ApertureType.EntrancePupilDiameter, data.Aperture.Type);
        Assert.Equal(0.01, data.Aperture.Value);                        // 10 mm, in meters
        Assert.Equal(LensUnit.Millimeter, data.Units.LensUnit);
        Assert.Equal(20, data.Environment.TemperatureCelsius);
        Assert.Equal(1, data.Environment.PressureAtmospheres);
        Assert.Single(data.Fields.Points);
        Assert.Equal(0.55, data.Wavelengths.Primary.Micrometers);
    }

    public static IEnumerable<object[]> AllNameTables() => new[]
    {
        new object[] { ApertureTypes.Names }, new object[] { ApodizationTypes.Names }, new object[] { FieldTypes.Names },
        new object[] { SourceUnits.Names }, new object[] { MtfUnits.Names }, new object[] { ConventionNames.LensUnits },
        new object[] { ConventionNames.FieldNormalizations },
    };

    [Theory]
    [MemberData(nameof(AllNameTables))]
    public void EveryChoice_HasALabel_AndASavedNameThatReadsBack(object table)
    {
        // Each table is a SavedNameTable<T>; walk it through reflection so one test covers all.
        var type = table.GetType();
        var all = (System.Collections.IEnumerable)type.GetProperty("All")!.GetValue(table)!;
        var enumType = type.GetGenericArguments()[0];
        Assert.Equal(Enum.GetValues(enumType).Length, all.Cast<object>().Count());
        foreach (var value in all)
        {
            var saved = (string)type.GetMethod("SavedName")!.Invoke(table, new[] { value })!;
            var label = (string)type.GetMethod("Label")!.Invoke(table, new[] { value })!;
            Assert.False(string.IsNullOrWhiteSpace(saved));
            Assert.False(string.IsNullOrWhiteSpace(label));
            var args = new object?[] { saved, null };
            Assert.True((bool)type.GetMethod("TryParse")!.Invoke(table, args)!);
            Assert.Equal(value, args[1]);
        }
    }

    /// <summary>The names written to files. A rename of an enum member must not change them.</summary>
    [Fact]
    public void SavedNames_AreTheOnesWrittenToFiles()
    {
        Assert.Equal(new[] { "EntrancePupilDiameter", "ImageSpaceFNumber", "ObjectSpaceNA", "FloatByStopSize", "ParaxialWorkingFNumber", "ObjectConeAngle" },
            ApertureTypes.Names.All.Select(ApertureTypes.Names.SavedName));
        Assert.Equal(new[] { "Uniform", "Gaussian", "CosineCubed" }, ApodizationTypes.Names.All.Select(ApodizationTypes.Names.SavedName));
        Assert.Equal(new[] { "Angle", "ObjectHeight", "ParaxialImageHeight", "RealImageHeight", "TheodoliteAngle" },
            FieldTypes.Names.All.Select(FieldTypes.Names.SavedName));
        Assert.Equal(new[] { "Millimeter", "Centimeter", "Inch", "Meter" }, ConventionNames.LensUnits.All.Select(ConventionNames.LensUnits.SavedName));
        Assert.Equal(new[] { "Radial", "Rectangular" }, ConventionNames.FieldNormalizations.All.Select(ConventionNames.FieldNormalizations.SavedName));
        Assert.Equal(new[] { "Watts", "Lumens", "Joules" }, SourceUnits.Names.All.Select(SourceUnits.Names.SavedName));
        Assert.Equal(new[] { "CyclesPerMillimeter", "CyclesPerMilliradian" }, MtfUnits.Names.All.Select(MtfUnits.Names.SavedName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("entrancePupilDiameter")]   // names are exact: a near miss is a hand edit
    [InlineData("WorkingFNumber")]
    public void AnUnknownSavedName_IsRefused(string? name)
    {
        Assert.False(ApertureTypes.Names.TryParse(name, out var fallback));
        Assert.Equal(ApertureType.EntrancePupilDiameter, fallback);
    }

    [Fact]
    public void ANameTableMissingAValue_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SavedNameTable<MtfUnit>((MtfUnit.CyclesPerMillimeter, "a", "A")));
        Assert.Throws<ArgumentException>(() => new SavedNameTable<MtfUnit>(
            (MtfUnit.CyclesPerMillimeter, "a", "A"), (MtfUnit.CyclesPerMilliradian, "a", "B")));
    }

    [Theory]
    [InlineData(ApertureType.EntrancePupilDiameter, 0.0, true)]
    [InlineData(ApertureType.EntrancePupilDiameter, -1.0, true)]
    [InlineData(ApertureType.ImageSpaceFNumber, double.NaN, true)]
    [InlineData(ApertureType.ObjectSpaceNumericalAperture, 0.25, false)]
    [InlineData(ApertureType.ObjectConeAngle, 89.0, false)]
    [InlineData(ApertureType.ObjectConeAngle, 90.0, true)]
    [InlineData(ApertureType.FloatByStopSize, 0.0, false)]          // takes no value
    [InlineData(ApertureType.ParaxialWorkingFNumber, 4.0, false)]
    public void ApertureValues_AreChecked(ApertureType type, double value, bool wrong)
    {
        var problems = new List<string>();
        new Aperture { Type = type, Value = value }.Validate(problems);
        Assert.Equal(wrong, problems.Count > 0);
    }

    [Fact]
    public void ApertureCombinationsWithNoMeaning_AreNamed()
    {
        static IReadOnlyList<string> Check(Aperture a) => new OpticalSystemData { Aperture = a }.Validate();
        Assert.Contains(Check(new Aperture { TelecentricObjectSpace = true }), p => p.Contains("telecentric"));
        Assert.Contains(Check(new Aperture { Type = ApertureType.ImageSpaceFNumber, Value = 4, TelecentricObjectSpace = true }), p => p.Contains("telecentric"));
        Assert.Contains(Check(new Aperture { Type = ApertureType.ParaxialWorkingFNumber, Value = 4, AfocalImageSpace = true }), p => p.Contains("afocal"));
        Assert.Empty(Check(new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.1, TelecentricObjectSpace = true }));
        Assert.Empty(Check(new Aperture { AfocalImageSpace = true }));
        Assert.NotEmpty(Check(new Aperture { ApodizationFactor = -1 }));
        Assert.NotEmpty(Check(new Aperture { SemiDiameterMargin = -1e-3 }));
        Assert.NotEmpty(Check(new Aperture { SemiDiameterMarginPercent = -5 }));
    }

    [Fact]
    public void Fields_AreChecked()
    {
        static IReadOnlyList<string> Check(FieldSet f) => new OpticalSystemData { Fields = f }.Validate();
        Assert.NotEmpty(Check(new FieldSet { Points = Array.Empty<FieldPoint>() }));
        Assert.NotEmpty(Check(new FieldSet { Points = new[] { new FieldPoint { Y = 90 } } }));                  // angle at 90°
        Assert.Empty(Check(new FieldSet { Type = FieldType.ObjectHeight, Points = new[] { new FieldPoint { Y = 90 } } }));
        Assert.NotEmpty(Check(new FieldSet { Points = new[] { new FieldPoint { Weight = 0 } } }));            // nothing weighted
        Assert.NotEmpty(Check(new FieldSet { Points = new[] { new FieldPoint { Weight = -1 } } }));
        Assert.NotEmpty(Check(new FieldSet { Points = new[] { new FieldPoint { CompressionY = 1 } } }));      // closes the pupil
        Assert.Empty(Check(new FieldSet { Points = new[] { new FieldPoint { CompressionY = 0.4, DecenterY = -0.2, TiltDegrees = 15 } } }));
        var problems = Check(new FieldSet { Points = new[] { new FieldPoint(), new FieldPoint { X = double.NaN } } });
        Assert.Contains(problems, p => p.StartsWith("Field 2"));                                               // 1-based, as the table numbers rows
    }

    [Fact]
    public void Fields_NormaliseByTheConventionsScale()
    {
        var fields = new FieldSet { Points = new[] { new FieldPoint { X = 10 }, new FieldPoint { X = -10, Y = -3 } } };
        double fr = Math.Sqrt(10 * 10 + 3 * 3);
        Assert.Equal((fr, fr), fields.Scale);
        Assert.Equal((10 / fr, 0.0), fields.Normalized(0));
        var rectangular = fields with { Normalization = FieldNormalization.Rectangular };
        Assert.Equal((10.0, 3.0), rectangular.Scale);
        Assert.Equal((-1.0, -1.0), rectangular.Normalized(1));
    }

    [Fact]
    public void TheFieldWizard_SpacesFieldsEvenlyOrByEqualArea()
    {
        var even = FieldSet.AlongY(20, 5, equalArea: false);
        Assert.Equal(new[] { 0.0, 5, 10, 15, 20 }, even.Select(p => p.Y));
        var area = FieldSet.AlongY(20, 5, equalArea: true);
        Assert.Equal(0, area[0].Y);
        Assert.Equal(20, area[^1].Y);
        // Equal area: the squared radii are evenly spaced.
        for (int i = 1; i < area.Count; i++)
            Assert.Equal(400.0 / 4, area[i].Y * area[i].Y - area[i - 1].Y * area[i - 1].Y, 10);
        Assert.All(area, p => { Assert.Equal(0, p.X); Assert.Equal(1, p.Weight); });
        Assert.Equal(0, Assert.Single(FieldSet.AlongY(20, 1, true)).Y);
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldSet.AlongY(20, 0, true));
    }

    [Fact]
    public void Wavelengths_AreChecked()
    {
        static IReadOnlyList<string> Check(WavelengthSet w) => new OpticalSystemData { Wavelengths = w }.Validate();
        Assert.NotEmpty(Check(new WavelengthSet { Wavelengths = Array.Empty<Wavelength>() }));
        Assert.NotEmpty(Check(new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0 } } }));
        Assert.NotEmpty(Check(new WavelengthSet { Wavelengths = new[] { new Wavelength { Weight = 0 } } }));
        Assert.NotEmpty(Check(new WavelengthSet { PrimaryIndex = 1 }));
        Assert.NotEmpty(Check(new WavelengthSet { PrimaryIndex = -1 }));
        Assert.Equal(0.55e-6, new Wavelength().Meters, 1e-20);
    }

    [Fact]
    public void Environment_IsChecked()
    {
        static IReadOnlyList<string> Check(OpticsEnvironment e) => new OpticalSystemData { Environment = e }.Validate();
        Assert.Empty(Check(new OpticsEnvironment { PressureAtmospheres = 0 }));            // a vacuum
        Assert.NotEmpty(Check(new OpticsEnvironment { PressureAtmospheres = -0.1 }));
        Assert.NotEmpty(Check(new OpticsEnvironment { TemperatureCelsius = -273.15 }));
        Assert.Empty(Check(new OpticsEnvironment { TemperatureCelsius = -273.0 }));
    }

    /// <summary>The presets carry SCHOTT TIE-29 Table 2.3-1's digits exactly (see SpectralLines).</summary>
    [Fact]
    public void ThePresets_AreTheTableLines()
    {
        var fdc = SpectralLines.Presets[0].ToWavelengthSet();
        Assert.Equal(new[] { 0.4861327, 0.5875618, 0.6562725 }, fdc.Wavelengths.Select(w => w.Micrometers));
        Assert.Equal(0.5875618, fdc.Primary.Micrometers);
        Assert.Empty(new OpticalSystemData { Wavelengths = fdc }.Validate());
        Assert.Equal(new[] { 0.4799914, 0.546074, 0.6438469 },
            SpectralLines.Presets[1].ToWavelengthSet().Wavelengths.Select(w => w.Micrometers));
        foreach (var preset in SpectralLines.Presets)
        {
            Assert.Empty(new OpticalSystemData { Wavelengths = preset.ToWavelengthSet() }.Validate());
            Assert.All(preset.Lines, l => Assert.Contains(l, SpectralLines.All));
        }
        Assert.Equal(23, SpectralLines.All.Count);                     // every row of the table
        Assert.Equal(SpectralLines.All.OrderByDescending(l => l.Micrometers), SpectralLines.All);
    }

    [Fact]
    public void GaussianQuadrature_IntegratesPolynomialsOfDegree2NMinus1Exactly()
    {
        const double a = 0.45, b = 0.65;
        var set = WavelengthSet.GaussianQuadrature(a, b, 3);
        Assert.Equal(3, set.Wavelengths.Count);
        Assert.Equal(1, set.PrimaryIndex);
        Assert.Equal(0.55, set.Primary.Micrometers, 14);                // the middle node is the band centre
        Assert.Equal(1.0, set.Wavelengths.Max(w => w.Weight));
        Assert.All(set.Wavelengths, w => Assert.InRange(w.Micrometers, a, b));
        Assert.Empty(new OpticalSystemData { Wavelengths = set }.Validate());

        // The weights are relative: rescaled to the band's width they integrate λ^k exactly for k ≤ 5.
        double sum = set.Wavelengths.Sum(w => w.Weight);
        for (int k = 0; k <= 5; k++)
        {
            double quadrature = set.Wavelengths.Sum(w => w.Weight * Math.Pow(w.Micrometers, k)) / sum * (b - a);
            double exact = (Math.Pow(b, k + 1) - Math.Pow(a, k + 1)) / (k + 1);
            Assert.Equal(exact, quadrature, 14);
        }
        // Degree 6 is beyond a three-point rule.
        double q6 = set.Wavelengths.Sum(w => w.Weight * Math.Pow(w.Micrometers, 6)) / sum * (b - a);
        Assert.NotEqual((Math.Pow(b, 7) - Math.Pow(a, 7)) / 7, q6, 14);

        Assert.Equal(1, WavelengthSet.GaussianQuadrature(a, b, 4).PrimaryIndex);
        Assert.Throws<ArgumentException>(() => WavelengthSet.GaussianQuadrature(b, a, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavelengthSet.GaussianQuadrature(a, b, 0));
    }
}
