using System.Text;
using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Interop.Zemax.ZemaxText;

namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// Writes the saved optical model as a Zemax lens file (.zmx), plan item OP-94,
/// docs/Optics-Conventions.md section 52. It writes exactly the subset
/// <see cref="ZemaxLensReader"/> reads, in the line forms that reader accepts, so that export
/// then import gives the same record (the gate; radii pass through a curvature and may move by
/// one rounding). Anything the subset cannot hold — a surface type without a TYPE code here,
/// surface tilts and decenters, composite add-ons, apertures other than circular and floating, solves other than
/// the pickups and variables listed in section 52, the merit function, tolerances, more than one
/// configuration, system settings away from their defaults that the file's lines are not read
/// for — fails the export with every item listed, or with
/// <see cref="ZemaxExportOptions.UnsupportedAsWarnings"/> is listed and left out.
/// <para>
/// The header lines VERS and the tail of UNIT are written as Optiland's writer writes them
/// (optiland/fileio/zemax/writer/encoder.py, <c>_encode_header</c>); no reader interprets them.
/// </para>
/// </summary>
public static class ZemaxLensWriter
{
    /// <summary>The longest NOTE line text; longer notes are split over several NOTE lines,
    /// which the reader joins back (the vendor files split their notes the same way).</summary>
    public const int NoteChunk = 200;

    /// <summary>The file text of an optical model; throws <see cref="ZemaxFileException"/>
    /// listing every item the subset cannot hold in the strict mode.</summary>
    public static ZemaxExportResult Write(OpticsSettings optics, ZemaxExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        options ??= new ZemaxExportOptions();
        var problems = new List<string>();
        var notes = new List<string>();
        var o = new StringBuilder();
        void L(string line) => o.Append(line).Append("\r\n");

        if (optics.Mode is { } mode && mode != OpticsModes.SavedName(OpticsMode.Sequential))
            problems.Add($"Mode: only a sequential system is written (the mode is '{mode}').");
        var sys = optics.SystemData ?? new OpticsSystemSettings();
        var surfaces = optics.Surfaces ?? new List<OpticsSurfaceSettings>();
        if (surfaces.Count < 3) problems.Add($"Surfaces: a lens file needs at least 3 surfaces (the system has {surfaces.Count}).");

        L("VERS 240000 3 0");
        L("MODE SEQ");
        L("NAME " + (sys.Title ?? "").Replace("\r", " ").Replace("\n", " "));
        if (sys.Title is { } title && title.Contains('\n')) notes.Add("Title: line breaks written as spaces.");
        string noteText = sys.Notes ?? "";
        if (noteText.Contains('\n') || noteText.Contains('\r'))
        {
            notes.Add("Notes: line breaks written as spaces (a NOTE line holds one line of text).");
            noteText = noteText.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
        if (noteText.Length == 0) L("NOTE 0");
        for (int i = 0; i < noteText.Length; i += NoteChunk)
            L("NOTE 0 " + noteText.Substring(i, Math.Min(NoteChunk, noteText.Length - i)));

        LensUnit unit = LensUnit.Millimeter;
        if (sys.LensUnit is { } u && !ConventionNames.LensUnits.TryParse(u, out unit))
            problems.Add($"System data: the lens unit '{u}' is not one this version knows.");
        string? unitCode = unit switch { LensUnit.Millimeter => "MM", LensUnit.Centimeter => "CM", LensUnit.Inch => "IN", _ => null };
        if (unitCode is null) { problems.Add("System data: meters have no UNIT code this build writes (MM, CM, IN)."); unitCode = "MM"; }
        L($"UNIT {unitCode} X W X CM MR CPMM");
        L($"ENVD {Number(sys.TemperatureCelsius)} {Number(sys.PressureAtmospheres)} 0");

        var aperture = ApertureType.EntrancePupilDiameter;
        if (sys.ApertureType is { } at && !ApertureTypes.Names.TryParse(at, out aperture))
            problems.Add($"System data: the aperture type '{at}' is not one this version knows.");
        L(aperture switch
        {
            ApertureType.EntrancePupilDiameter => $"ENPD {Number(sys.ApertureValue)}",
            ApertureType.ImageSpaceFNumber => $"FNUM {Number(sys.ApertureValue)} 0",
            ApertureType.ParaxialWorkingFNumber => $"FNUM {Number(sys.ApertureValue)} 1",
            ApertureType.ObjectSpaceNumericalAperture => $"OBNA {Number(sys.ApertureValue)} 0",
            ApertureType.ObjectConeAngle => $"OBNA {Number(sys.ApertureValue)} 1",
            _ => "FLOA",
        });

        var fieldType = FieldType.Angle;
        if (sys.FieldType is { } ft && !FieldTypes.Names.TryParse(ft, out fieldType))
            problems.Add($"System data: the field type '{ft}' is not one this version knows.");
        var fields = sys.Fields ?? new List<OpticsFieldSettings> { new() };
        var waves = sys.Wavelengths ?? new List<OpticsWavelengthSettings> { new() };
        L($"FTYP {(int)fieldType} {(sys.TelecentricObjectSpace ? 1 : 0)} {fields.Count} {waves.Count} 0 0 {(sys.AfocalImageSpace ? 1 : 0)}");
        UnwrittenSystem(sys, problems);

        var catalogs = optics.Materials?.ActiveCatalogs?.ToList();
        var extraCatalogs = new List<string>();
        foreach (var s in surfaces)
            if (s?.Material is { } m && m.IndexOf(':') is int colon and > 0)
            {
                string cat = m[..colon].Trim();
                if (catalogs is not null && !catalogs.Contains(cat, StringComparer.OrdinalIgnoreCase) && !extraCatalogs.Contains(cat, StringComparer.OrdinalIgnoreCase))
                    extraCatalogs.Add(cat);
            }
        if (catalogs is not null)
        {
            catalogs.AddRange(extraCatalogs);
            if (catalogs.Count > 0) L("GCAT " + string.Join(' ', catalogs));
        }

        string Column(Func<OpticsFieldSettings, double> f) => string.Join(' ', fields.Select(x => Number(f(x))));
        L("XFLN " + Column(f => f.X));
        L("YFLN " + Column(f => f.Y));
        L("FWGN " + Column(f => f.Weight));
        L("VDXN " + Column(f => f.DecenterX));
        L("VDYN " + Column(f => f.DecenterY));
        L("VCXN " + Column(f => f.CompressionX));
        L("VCYN " + Column(f => f.CompressionY));
        L("VANN " + Column(f => f.TiltDegrees));
        for (int i = 0; i < waves.Count; i++)
            L($"WAVM {i + 1} {Number(waves[i].Micrometers)} {Number(waves[i].Weight)}");
        L($"PWAV {sys.PrimaryWavelength}");

        for (int j = 0; j < surfaces.Count; j++)
            WriteSurface(surfaces[j] ?? new OpticsSurfaceSettings(), j, surfaces.Count, L, problems, notes);

        UnwrittenParts(optics, problems, notes);

        if (problems.Count > 0 && !options.UnsupportedAsWarnings)
            throw new ZemaxFileException("The optical system cannot be written as a Zemax lens file as it stands (export with unsupported content listed as warnings to write the rest):", problems);
        return new ZemaxExportResult(o.ToString(), options.UnsupportedAsWarnings ? problems.ToArray() : Array.Empty<string>(), notes.ToArray());
    }

    private static void WriteSurface(OpticsSurfaceSettings s, int j, int count, Action<string> L, List<string> problems, List<string> notes)
    {
        string where = SavedSurfaces.Where(j, count);
        string? code = ZemaxKeywords.CodeOf(s.Type);
        if (code is null)
        {
            if (s.Type is not null && ZemaxKeywords.RefusalOf(s.Type) is { } refused)   // OP-25: Grid Sag
                problems.Add($"{where}: the surface type '{s.Type}' is not written: {refused}.");
            else problems.Add($"{where}: the surface type '{s.Type}' has no TYPE code this build writes ({string.Join(", ", ZemaxKeywords.SurfaceTypes.Select(x => x.TypeName ?? SavedSurfaces.StandardType))}).");
            code = "STANDARD";
        }
        // OP-32: composite add-ons. No source shows how a .zmx stores them (OpticStudio keeps each
        // add-on as its own row with "Composite Surface: Add sag to next surface" checked, help
        // "Composite Surface", but no reader or OpticStudio-written file shows that property's
        // keyword), so a surface with add-ons is refused by name rather than written without them.
        if (s.AddOns is { Count: > 0 } addOns)
            problems.Add($"{where}: its {addOns.Count} composite add-on{(addOns.Count == 1 ? " is" : "s are")} not written: no source shows how a .zmx stores a composite surface's add-ons; keep the lens in this program's own file.");
        var flags = new Dictionary<string, OpticsCellFlagSettings>(StringComparer.Ordinal);
        foreach (var f in s.Flags ?? new List<OpticsCellFlagSettings>())
        {
            // "Automatic" on a semi-diameter is the sizing a null value already means; DIAM writes it.
            if (f.Kind == SavedSurfaces.SolveFlag && f.Solve == SavedSolves.Automatic
                && f.Column is SavedSurfaces.SemiDiameterColumn or SavedSurfaces.MechanicalSemiDiameterColumn) continue;
            if (f.Column is null || !flags.TryAdd(f.Column, f)) { problems.Add($"{where}: a flag without a column, or two flags on one cell."); continue; }
            if (!Supported(f, out string? why)) { problems.Add($"{where}, {f.Column}: {why}"); flags.Remove(f.Column); }
        }

        L($"SURF {j}");
        L($"  TYPE {code}");
        if (s.Comment is { Length: > 0 } comment)
        {
            if (comment.Contains('\n') || comment.Contains('\r')) notes.Add($"{where}: comment line breaks written as spaces.");
            L("  COMM " + comment.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '));
        }
        if (s.IsStop) L("  STOP");

        double curvature = SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
        L(flags.TryGetValue(SavedSurfaces.RadiusColumn, out var rf)
            ? rf.Kind == SavedSurfaces.VariableFlag ? $"  CURV {Number(curvature)} 1 0 0 0 \"\"" : $"  CURV {Number(curvature)} 4 {rf.PickupSurface} {Number(rf.PickupScale)} 0 \"\""
            : $"  CURV {Number(curvature)} 0 0 0 0 \"\"");

        foreach (var p in (s.Parameters ?? new()).OrderBy(p => p.Number))
        {
            if (p.Number < 0 || p.Number >= Sequential.SurfaceTypes.FirstExtraData) { problems.Add($"{where}: parameter {p.Number} is outside PARM 0–12."); continue; }
            if (p.Value != 0) L($"  PARM {p.Number} {Number(p.Value)}");
        }
        foreach (var (column, f) in flags.Where(kv => SavedSurfaces.ParameterNumber(kv.Key) is not null).OrderBy(kv => ZemaxLensReader.FlagRank(kv.Key)))
        {
            int n = SavedSurfaces.ParameterNumber(column)!.Value;
            L(f.Kind == SavedSurfaces.VariableFlag ? $"  VPAR {n}" : $"  PPAR {n} {f.PickupSurface} {Number(f.PickupScale)} 0 0");
        }
        foreach (var x in (s.ExtraData ?? new()).OrderBy(p => p.Number))
        {
            if (x.Number < Sequential.SurfaceTypes.FirstExtraData) { problems.Add($"{where}: extra data {x.Number} is below {Sequential.SurfaceTypes.FirstExtraData}."); continue; }
            if (x.Value != 0) L($"  XDAT {x.Number - Sequential.SurfaceTypes.FirstExtraData + 1} {Number(x.Value)} 0 0 1 0 0 \"\"");
        }

        L(s.ThicknessIsInfinite ? "  DISZ INFINITY" : $"  DISZ {Number(s.Thickness)}");
        if (flags.TryGetValue(SavedSurfaces.ThicknessColumn, out var tf))
            L(tf.Kind == SavedSurfaces.VariableFlag ? "  VDSZ 0 0" : $"  PZUP {tf.PickupSurface} {Number(tf.PickupScale)} 0 0");

        WriteGlass(s, where, flags, L, problems);
        if (s.Conic != 0) L($"  CONI {Number(s.Conic)}");
        if (s.Coating is { Length: > 0 } coat) L("  COAT " + coat);

        double sd = s.SemiDiameter ?? 0;
        if (flags.TryGetValue(SavedSurfaces.SemiDiameterColumn, out var sf))
        {
            if (s.SemiDiameter is null) notes.Add($"{where}: an automatic semi-diameter with a pickup is written as 0 and reads back as a fixed 0 with the pickup.");
            L($"  DIAM {Number(sd)} 2 {sf.PickupSurface} 0 1 \"\"");
        }
        else L(s.SemiDiameter is null ? "  DIAM 0 0 0 0 1 \"\"" : $"  DIAM {Number(sd)} 1 0 0 1 \"\"");

        if (s.Aperture is { } a)
        {
            if (a.IsObscuration) problems.Add($"{where}: an obscuration is not written.");
            if (a.Type == SavedSurfaces.CircularAperture) L($"  CLAP {Number(a.MinRadius)} {Number(a.MaxRadius)} 0");
            else if (a.Type == SavedSurfaces.FloatingAperture) L($"  FLAP 0 {Number(sd)} 0");
            else problems.Add($"{where}: the aperture type '{a.Type}' is not written (circular and floating are).");
            if ((a.Type == SavedSurfaces.CircularAperture || a.Type == SavedSurfaces.FloatingAperture) && (a.DecenterX != 0 || a.DecenterY != 0))
                L($"  OBDC {Number(a.DecenterX)} {Number(a.DecenterY)}");
        }

        if (s.ChipZone != 0) problems.Add($"{where}: the chip zone is not written.");
        if (s.MechanicalSemiDiameter is not null) problems.Add($"{where}: the mechanical semi-diameter is not written.");
        if (s.Tce != 0) problems.Add($"{where}: the TCE column is not written.");
        if (s.IsIgnored) problems.Add($"{where}: 'ignore this surface' is not written.");
        if (s.SkipRaysTo) problems.Add($"{where}: 'skip rays to this surface' is not written.");
        if (s.HideRaysTo) problems.Add($"{where}: 'hide rays to this surface' is not written.");
        if (s.DoNotDraw) problems.Add($"{where}: 'do not draw this surface' is not written.");
        if (s.RowColor is not null) problems.Add($"{where}: the row colour is not written.");
        if (s.TiltBefore is not null) problems.Add($"{where}: the tilt/decenter before the surface is not written (no reader interprets its keywords).");
        if (s.TiltAfter is not null) problems.Add($"{where}: the tilt/decenter after the surface is not written (no reader interprets its keywords).");
        if (s.CoordinateReturn is not null) problems.Add($"{where}: the coordinate return is not written.");
    }

    private static bool Supported(OpticsCellFlagSettings f, out string? why)
    {
        why = null;
        bool variable = f.Kind == SavedSurfaces.VariableFlag, pickup = f.Kind == SavedSurfaces.PickupFlag;
        bool sameColumn = f.PickupColumn is null || f.PickupColumn.Length == 0 || f.PickupColumn == f.Column;
        int? parameter = SavedSurfaces.ParameterNumber(f.Column);
        if (pickup && !sameColumn) { why = $"a pickup from another column ({f.PickupColumn}) is not written."; return false; }
        if (pickup && f.PickupOffset != 0 && f.Column != SavedSurfaces.RadiusColumn) { why = "a pickup offset is not written."; return false; }
        bool ok = f.Column switch
        {
            SavedSurfaces.RadiusColumn => variable || (pickup && f.PickupOffset == 0),
            SavedSurfaces.ThicknessColumn => variable || pickup,
            SavedSurfaces.MaterialColumn => pickup,
            SavedSurfaces.SemiDiameterColumn => pickup && f.PickupScale == 1,
            _ => parameter is >= 0 and < Sequential.SurfaceTypes.FirstExtraData && (variable || pickup),
        };
        if (!ok) why = $"a {(f.Kind == SavedSurfaces.SolveFlag ? $"'{f.Solve}' solve" : f.Kind?.ToLowerInvariant() ?? "flag")} on this column is not written (written: radius variable or pickup, thickness variable or pickup, material pickup, semi-diameter pickup, parameter 0–12 variable or pickup).";
        return ok;
    }

    private static void WriteGlass(OpticsSurfaceSettings s, string where, Dictionary<string, OpticsCellFlagSettings> flags, Action<string> L, List<string> problems)
    {
        bool pickup = flags.TryGetValue(SavedSurfaces.MaterialColumn, out var gf);
        if (SavedSurfaces.IsMirror(s)) { L("  GLAS MIRROR 0 0 0 0 0 0 0 0 0 0"); return; }
        if (s.ModelGlass is { } m)
        {
            if (pickup) problems.Add($"{where}: a material pickup on a model glass is not written.");
            L($"  GLAS ___BLANK 1 0 {Number(m.Nd)} {Number(m.Vd)} {Number(m.DeltaPgF)} 0 0 0 0 0");
            return;
        }
        if (s.Index is double n)
        {
            if (pickup) problems.Add($"{where}: a material pickup on a constant index is not written.");
            L($"  GLAS ___BLANK 1 0 {Number(n)} 0 0 0 0 0 0 0");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.Material))
        {
            if (pickup) problems.Add($"{where}: a material pickup on air is not written.");
            return;
        }
        string name = s.Material.Trim();
        int colon = name.IndexOf(':');
        if (colon > 0) name = name[(colon + 1)..].Trim();
        if (name.Any(char.IsWhiteSpace)) { problems.Add($"{where}: the glass name '{name}' has a space, which a GLAS line cannot hold."); return; }
        L(pickup ? $"  GLAS {name} 2 {gf!.PickupSurface} 0 0 0 0 0 0 0 0" : $"  GLAS {name} 0 0 0 0 0 0 0 0 0 0");
    }

    private static void UnwrittenSystem(OpticsSystemSettings sys, List<string> problems)
    {
        var d = SystemDataSettings.ToSettings(OpticalSystemData.Default);
        void Check<T>(string name, T? value, T? fallback)
        {
            if (value is not null && !EqualityComparer<T>.Default.Equals(value, fallback))
                problems.Add($"System data: {name} ({value}) is not written; the lens file lines for it are not interpreted by the reference readers.");
        }
        Check("the source unit", sys.SourceUnit, d.SourceUnit);
        Check("the analysis area unit", sys.AnalysisAreaUnit, d.AnalysisAreaUnit);
        Check("the MTF unit", sys.MtfUnit, d.MtfUnit);
        Check("the apodization", sys.Apodization, d.Apodization);
        Check("the apodization factor", (double?)sys.ApodizationFactor, d.ApodizationFactor);
        Check("the semi-diameter margin", (double?)sys.SemiDiameterMargin, d.SemiDiameterMargin);
        Check("the semi-diameter margin %", (double?)sys.SemiDiameterMarginPercent, d.SemiDiameterMarginPercent);
        Check("the field normalization", sys.FieldNormalization, d.FieldNormalization);
        Check("'adjust index to environment'", (bool?)sys.AdjustIndexToEnvironment, d.AdjustIndexToEnvironment);
        Check("the air index model", sys.AirIndexModel, d.AirIndexModel);
        Check("the CO2 fraction", sys.CarbonDioxideMicromolesPerMole, d.CarbonDioxideMicromolesPerMole);
        Check("the relative humidity", sys.RelativeHumidityPercent, d.RelativeHumidityPercent);
        Check("the polarization", sys.Polarization, d.Polarization);
        Check("ray aiming", sys.RayAiming, d.RayAiming);
        Check("robust ray aiming", sys.RobustRayAiming, d.RobustRayAiming);
        Check("the pupil shift x", sys.PupilShiftX, d.PupilShiftX);
        Check("the pupil shift y", sys.PupilShiftY, d.PupilShiftY);
        Check("the pupil shift z", sys.PupilShiftZ, d.PupilShiftZ);
        Check("the pupil compress x", sys.PupilCompressX, d.PupilCompressX);
        Check("the pupil compress y", sys.PupilCompressY, d.PupilCompressY);
        Check("'scale pupil shift by field'", sys.ScalePupilShiftByField, d.ScalePupilShiftByField);
    }

    private static void UnwrittenParts(OpticsSettings optics, List<string> problems, List<string> notes)
    {
        if (optics.MeritFunction?.Operands is { Count: > 0 } mf)
            problems.Add($"Merit function: {mf.Count} rows are not written (the row layout of the lens file is not published).");
        if (optics.Tolerances?.Operands is { Count: > 0 } tol)
            problems.Add($"Tolerances: {tol.Count} rows are not written (the row layout of the lens file is not published).");
        if (optics.MultiConfiguration is { } mc && (mc.ConfigurationCount > 1 || mc.Operands is { Count: > 0 }))
            problems.Add($"Multi-configuration: {mc.ConfigurationCount} configurations and {mc.Operands?.Count ?? 0} rows are not written (the row layout of the lens file is not published).");
        if (optics.Coatings?.Coatings is { Count: > 0 } coats)
            problems.Add($"Coatings: the coating catalog ({coats.Count} coatings) is not in a lens file; surfaces keep their coating names.");
        if (optics.Thermal?.GlassMaterials is { Count: > 0 }) problems.Add("Thermal: the glass-to-material links are not written.");
        if (optics.GlassSubstitution is not null) problems.Add("Glass substitution template: not written.");
        if (optics.Analyses is not null || optics.MeritWizard is not null)
            notes.Add("The analysis windows and the optimization wizard settings are this program's own and are not written.");
    }
}
