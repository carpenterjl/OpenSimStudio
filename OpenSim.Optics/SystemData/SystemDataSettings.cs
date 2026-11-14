using OpenSim.Core.Model;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics;

/// <summary>
/// The system data between its record in meters (<see cref="OpticalSystemData"/>) and its file
/// form in lens units (<see cref="OpticsSystemSettings"/>), for code that reads a project or lens
/// without the editor (the command line, scripting, tests). The editor reads and writes the file
/// form directly, so what the user typed reads back digit for digit; through this class a length
/// is scaled by the lens unit, which can move it by an ulp.
/// </summary>
public static class SystemDataSettings
{
    public static OpticsSystemSettings ToSettings(OpticalSystemData data)
    {
        double perUnit = OpticsConventions.MetersPer(data.Units.LensUnit);
        var a = data.Aperture;
        double fieldScale = FieldTypes.IsAngle(data.Fields.Type) ? 1.0 : perUnit;
        return new OpticsSystemSettings
        {
            Title = data.Title,
            Notes = data.Notes,
            LensUnit = ConventionNames.LensUnits.SavedName(data.Units.LensUnit),
            SourceUnit = SourceUnits.Names.SavedName(data.Units.SourceUnit),
            AnalysisAreaUnit = ConventionNames.LensUnits.SavedName(data.Units.AnalysisAreaUnit),
            MtfUnit = MtfUnits.Names.SavedName(data.Units.MtfUnit),
            ApertureType = ApertureTypes.Names.SavedName(a.Type),
            ApertureValue = ApertureTypes.IsLength(a.Type) ? a.Value / perUnit : a.Value,
            Apodization = ApodizationTypes.Names.SavedName(a.Apodization),
            ApodizationFactor = a.ApodizationFactor,
            SemiDiameterMargin = a.SemiDiameterMargin / perUnit,
            SemiDiameterMarginPercent = a.SemiDiameterMarginPercent,
            TelecentricObjectSpace = a.TelecentricObjectSpace,
            AfocalImageSpace = a.AfocalImageSpace,
            FieldType = FieldTypes.Names.SavedName(data.Fields.Type),
            FieldNormalization = ConventionNames.FieldNormalizations.SavedName(data.Fields.Normalization),
            Fields = data.Fields.Points.Select(p => new OpticsFieldSettings
            {
                X = p.X / fieldScale, Y = p.Y / fieldScale, Weight = p.Weight,
                DecenterX = p.DecenterX, DecenterY = p.DecenterY, CompressionX = p.CompressionX,
                CompressionY = p.CompressionY, TiltDegrees = p.TiltDegrees
            }).ToList(),
            Wavelengths = data.Wavelengths.Wavelengths
                .Select(w => new OpticsWavelengthSettings { Micrometers = w.Micrometers, Weight = w.Weight }).ToList(),
            PrimaryWavelength = data.Wavelengths.PrimaryIndex + 1,
            TemperatureCelsius = data.Environment.TemperatureCelsius,
            PressureAtmospheres = data.Environment.PressureAtmospheres,
            AdjustIndexToEnvironment = data.Environment.AdjustIndexToEnvironment,
            AirIndexModel = AirIndexModels.Names.SavedName(data.Environment.AirModel),
            CarbonDioxideMicromolesPerMole = data.Environment.CarbonDioxideMicromolesPerMole,
            RelativeHumidityPercent = data.Environment.RelativeHumidityPercent,
            Polarization = PolarizationToSettings(data.Polarization)
        };
    }

    // OP-51
    /// <summary>The system polarization in its file form.</summary>
    public static OpticsPolarizationSettings PolarizationToSettings(SystemPolarization p) => new()
    {
        Unpolarized = p.Unpolarized, Jx = p.Jx, Jy = p.Jy, XPhaseDegrees = p.XPhaseDegrees, YPhaseDegrees = p.YPhaseDegrees,
        Reference = PolarizationReferences.Names.SavedName(p.Reference), ConvertThinFilmPhase = p.ConvertThinFilmPhase
    };

    /// <summary>The system polarization a file holds; null (a file from before OP-51) is the
    /// default. An unknown reference name keeps the X axis reference and is named in a note.</summary>
    public static SystemPolarization PolarizationFromSettings(OpticsPolarizationSettings? saved, ICollection<string> notes)
    {
        var d = SystemPolarization.Default;
        if (saved is null) return d;
        return new SystemPolarization
        {
            Unpolarized = saved.Unpolarized, Jx = saved.Jx, Jy = saved.Jy,
            XPhaseDegrees = saved.XPhaseDegrees, YPhaseDegrees = saved.YPhaseDegrees,
            Reference = Pick(PolarizationReferences.Names, saved.Reference, d.Reference, "Polarization reference", notes),
            ConvertThinFilmPhase = saved.ConvertThinFilmPhase
        };
    }

    /// <summary>
    /// The system data a file holds; null (an older file) is the default system. A saved name
    /// this build does not know keeps the default and adds a note naming it; null lists keep
    /// the default system's one field and one wavelength.
    /// </summary>
    public static OpticalSystemData FromSettings(OpticsSystemSettings? saved, ICollection<string> notes)
    {
        var d = OpticalSystemData.Default;
        if (saved is null) return d;
        var lensUnit = Pick(ConventionNames.LensUnits, saved.LensUnit, d.Units.LensUnit, "Lens unit", notes);
        var apertureType = Pick(ApertureTypes.Names, saved.ApertureType, d.Aperture.Type, "Aperture type", notes);
        var fieldType = Pick(FieldTypes.Names, saved.FieldType, d.Fields.Type, "Field type", notes);
        double perUnit = OpticsConventions.MetersPer(lensUnit);
        double fieldScale = FieldTypes.IsAngle(fieldType) ? 1.0 : perUnit;
        return new OpticalSystemData
        {
            Title = saved.Title ?? d.Title,
            Notes = saved.Notes ?? d.Notes,
            Units = new SystemUnits
            {
                LensUnit = lensUnit,
                SourceUnit = Pick(SourceUnits.Names, saved.SourceUnit, d.Units.SourceUnit, "Source unit", notes),
                AnalysisAreaUnit = Pick(ConventionNames.LensUnits, saved.AnalysisAreaUnit, d.Units.AnalysisAreaUnit, "Analysis area unit", notes),
                MtfUnit = Pick(MtfUnits.Names, saved.MtfUnit, d.Units.MtfUnit, "MTF unit", notes)
            },
            Aperture = new Aperture
            {
                Type = apertureType,
                Value = ApertureTypes.IsLength(apertureType) ? saved.ApertureValue * perUnit : saved.ApertureValue,
                Apodization = Pick(ApodizationTypes.Names, saved.Apodization, d.Aperture.Apodization, "Apodization", notes),
                ApodizationFactor = saved.ApodizationFactor,
                SemiDiameterMargin = saved.SemiDiameterMargin * perUnit,
                SemiDiameterMarginPercent = saved.SemiDiameterMarginPercent,
                TelecentricObjectSpace = saved.TelecentricObjectSpace,
                AfocalImageSpace = saved.AfocalImageSpace
            },
            Fields = new FieldSet
            {
                Type = fieldType,
                Normalization = Pick(ConventionNames.FieldNormalizations, saved.FieldNormalization, d.Fields.Normalization, "Field normalization", notes),
                Points = saved.Fields?.Select(p => new FieldPoint
                {
                    X = p.X * fieldScale, Y = p.Y * fieldScale, Weight = p.Weight,
                    DecenterX = p.DecenterX, DecenterY = p.DecenterY, CompressionX = p.CompressionX,
                    CompressionY = p.CompressionY, TiltDegrees = p.TiltDegrees
                }).ToArray() ?? d.Fields.Points
            },
            Wavelengths = saved.Wavelengths is null ? d.Wavelengths : new WavelengthSet
            {
                Wavelengths = saved.Wavelengths.Select(w => new Wavelength { Micrometers = w.Micrometers, Weight = w.Weight }).ToArray(),
                PrimaryIndex = saved.PrimaryWavelength - 1
            },
            Environment = new OpticsEnvironment
            {
                TemperatureCelsius = saved.TemperatureCelsius,
                PressureAtmospheres = saved.PressureAtmospheres,
                AdjustIndexToEnvironment = saved.AdjustIndexToEnvironment,
                AirModel = Pick(AirIndexModels.Names, saved.AirIndexModel, d.Environment.AirModel, "Air index model", notes),
                CarbonDioxideMicromolesPerMole = saved.CarbonDioxideMicromolesPerMole ?? d.Environment.CarbonDioxideMicromolesPerMole,
                RelativeHumidityPercent = saved.RelativeHumidityPercent ?? d.Environment.RelativeHumidityPercent
            },
            Polarization = PolarizationFromSettings(saved.Polarization, notes)
        };
    }

    /// <summary>A saved choice read back: null is the default; an unknown name keeps the default
    /// and is named in a note, never opened as another choice without a word.</summary>
    public static T Pick<T>(SavedNameTable<T> names, string? saved, T fallback, string what, ICollection<string> notes)
        where T : struct, Enum
    {
        if (saved is null) return fallback;
        if (names.TryParse(saved, out var value)) return value;
        notes.Add($"{what} '{saved}' is not one this version knows; kept '{names.Label(fallback)}'.");
        return fallback;
    }
}
