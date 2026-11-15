using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// Reads the text of a Zemax lens file (.zmx) into the saved optical model
/// (<see cref="OpticsSettings"/>), plan item OP-94, docs/Optics-Conventions.md section 52.
/// <para>
/// Every line is either read (<see cref="ZemaxKeywords.Read"/>), read and deliberately not
/// used (<see cref="ZemaxKeywords.Ignored"/>, named in the notes), or unsupported. Unsupported
/// content — an unknown keyword, a surface type or solve code this build does not read, a
/// merit-function, tolerance or multi-configuration row, a glass not in the catalogs — fails
/// the import with every problem listed, naming the keyword, the line and the surface, unless
/// <see cref="ZemaxImportOptions.UnsupportedAsWarnings"/> lists them as warnings instead.
/// Nothing is dropped without a line saying so.
/// </para>
/// <para>
/// Lengths stay in the file's lens unit, as the saved records keep them. Parameters PARM n
/// (n ≤ 12) are the saved parameter n; extra data XDAT n is the saved extra-data number n + 12
/// (<see cref="Sequential.SurfaceTypes.FirstExtraData"/>), which is how the saved layouts number
/// OpticStudio's extra-data columns. Solve and pickup encodings read are only those seen in
/// OpticStudio-written files (section 52 lists them); a solve code not in that list is
/// unsupported, never guessed.
/// </para>
/// </summary>
public static class ZemaxLensReader
{
    private sealed class SurfaceBuilder
    {
        public int Number;
        public int Line;
        public string Code = "STANDARD";
        public string? Type;
        public string? Comment;
        public double? Radius;
        public double Thickness;
        public bool Infinite;
        public string? Material;
        public int MaterialLine;
        public double? Index;
        public OpticsModelGlassSettings? ModelGlass;
        public string? Coating;
        public double? SemiDiameter;
        public double Conic;
        public bool Stop;
        public OpticsSurfaceApertureSettings? Aperture;
        public (double X, double Y)? ApertureDecenter;
        public readonly SortedDictionary<int, double> Parameters = new();
        public readonly SortedDictionary<int, double> Extra = new();
        public readonly Dictionary<string, OpticsCellFlagSettings> Flags = new(StringComparer.Ordinal);
    }

    private sealed class State
    {
        public required ZemaxImportOptions Options;
        public readonly List<string> Problems = new();
        public readonly List<string> Notes = new();
        public readonly HashSet<string> NotedKeywords = new(StringComparer.Ordinal);
        public readonly List<SurfaceBuilder> Surfaces = new();
        public SurfaceBuilder? Current;
        public bool InConfigurations;

        public string? Title;
        public string Notes2 = "";
        public bool HasNotes;
        public string? LensUnit;
        public string? ApertureType;
        public double ApertureValue = 10;
        public bool HaveAperture;
        public int FieldType;
        public bool Telecentric, Afocal;
        public int? FieldCount, WavelengthCount;
        public readonly Dictionary<string, List<double>> FieldColumns = new(StringComparer.Ordinal);
        public bool LegacyFields;
        public readonly SortedDictionary<int, (double Micrometers, double Weight)> WavelengthSlots = new();
        public List<double>? LegacyWavelengths, LegacyWeights;
        public int PrimaryWavelength = 1;
        public double? Temperature, Pressure;
        public List<string>? Catalogs;

        public void Problem(int line, string text) => Problems.Add($"Line {line}: {text}");

        public void Note(string keyword, string text)
        {
            if (NotedKeywords.Add(keyword)) Notes.Add(text);
        }
    }

    /// <summary>
    /// The optical model of a lens file's text. <paramref name="catalogs"/> are the glass
    /// catalogs glass names are checked against (searched in the order of the file's GCAT line;
    /// null skips the check, with a note). Throws <see cref="ZemaxFileException"/> listing every
    /// problem in the strict mode.
    /// </summary>
    public static ZemaxImportResult Read(string text, IReadOnlyList<OpticalCatalog>? catalogs,
        ZemaxImportOptions? options = null, string encoding = "text", IReadOnlyList<OpticalCatalog>? archiveCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new ZemaxImportOptions();
        var st = new State { Options = options };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var t = ZemaxText.Tokens(trimmed);
            // The text fields (NAME, NOTE, COMM, COAT) keep their trailing spaces: a NOTE split
            // over lines may break at a space.
            try { Line(st, i + 1, raw.TrimStart(), t, raw.Length > 0 && char.IsWhiteSpace(raw[0])); }
            catch (FormatException ex) { st.Problem(i + 1, $"{t[0]}: {ex.Message}"); }
        }
        var optics = Finish(st, catalogs, archiveCatalogs ?? Array.Empty<OpticalCatalog>());
        if (st.Problems.Count > 0 && !options.UnsupportedAsWarnings)
            throw new ZemaxFileException("The Zemax lens file cannot be read as it stands (open it with unsupported content listed as warnings to read the rest):", st.Problems);
        return new ZemaxImportResult(optics, options.UnsupportedAsWarnings ? st.Problems.ToArray() : Array.Empty<string>(),
            st.Notes.ToArray(), archiveCatalogs ?? Array.Empty<OpticalCatalog>(), encoding);
    }

    // ------------------------------------------------------------------ one line

    private static double Num(string[] t, int i, string what)
    {
        if (i >= t.Length) throw new FormatException($"the {what} (field {i}) is missing.");
        if (!ZemaxText.TryNumber(t[i], out double v)) throw new FormatException($"the {what} '{t[i]}' (field {i}) is not a number.");
        return v;
    }

    private static double NumOr(string[] t, int i, double fallback) =>
        i < t.Length && ZemaxText.TryNumber(t[i], out double v) ? v : fallback;

    private static int Whole(string[] t, int i, string what)
    {
        double v = Num(t, i, what);
        if (Math.Round(v) != v || Math.Abs(v) > int.MaxValue) throw new FormatException($"the {what} '{t[i]}' is not a whole number.");
        return (int)v;
    }

    private static void Line(State st, int n, string line, string[] t, bool indented)
    {
        string k = t[0];
        var s = st.Current;
        string at = s is null ? "" : $"surface {s.Number}, ";

        if (ZemaxKeywords.Ignored.TryGetValue(k, out string? why))
        {
            st.Note(k, $"{k}: not read ({why}).");
            return;
        }
        if (ZemaxKeywords.PartlyRead.TryGetValue(k, out string? partly)) st.Note(k, partly);

        switch (k)
        {
            // ---------------- system data
            case "MODE":
                if (t.Length < 2 || t[1] != "SEQ") st.Problem(n, $"MODE {(t.Length > 1 ? t[1] : "")}: only sequential lens files (MODE SEQ) are read.");
                return;
            case "NAME":
                st.Title = ZemaxText.Rest(line, k).Trim().Trim('"');
                return;
            case "NOTE":
            {
                string rest = ZemaxText.Rest(line, k);
                int space = rest.IndexOf(' ');
                string text = space < 0 ? "" : rest[(space + 1)..];
                st.Notes2 += text;
                st.HasNotes |= text.Length > 0;
                return;
            }
            case "UNIT":
                st.LensUnit = t.Length < 2 ? null : t[1] switch
                {
                    "MM" => ConventionNames.LensUnits.SavedName(Optics.LensUnit.Millimeter),
                    "CM" => ConventionNames.LensUnits.SavedName(Optics.LensUnit.Centimeter),
                    "IN" or "INCH" => ConventionNames.LensUnits.SavedName(Optics.LensUnit.Inch),
                    _ => null,
                };
                if (st.LensUnit is null) st.Problem(n, $"UNIT {(t.Length > 1 ? t[1] : "")}: the lens unit is not one this build reads (MM, CM, IN, INCH; RayOptics zmxread.py process_line).");
                return;
            case "ENVD":
                st.Temperature = Num(t, 1, "temperature");
                st.Pressure = Num(t, 2, "pressure");
                return;
            case "ENPD":
                SetAperture(st, Optics.ApertureType.EntrancePupilDiameter, Num(t, 1, "entrance pupil diameter"));
                return;
            case "FNUM":
                // Optiland parser.py _read_fno: flag 0 image-space F/#, 1 paraxial working F/#.
                SetAperture(st, Whole(t, 2, "F/# kind") switch
                {
                    0 => Optics.ApertureType.ImageSpaceFNumber,
                    1 => Optics.ApertureType.ParaxialWorkingFNumber,
                    _ => throw new FormatException("the F/# kind must be 0 (image space) or 1 (paraxial working)."),
                }, Num(t, 1, "F/#"));
                return;
            case "OBNA":
                // Optiland parser.py _read_object_na: flag 0 object-space NA, 1 object cone angle.
                SetAperture(st, Whole(t, 2, "kind") switch
                {
                    0 => Optics.ApertureType.ObjectSpaceNumericalAperture,
                    1 => Optics.ApertureType.ObjectConeAngle,
                    _ => throw new FormatException("the kind must be 0 (object NA) or 1 (object cone angle)."),
                }, Num(t, 1, "value"));
                return;
            case "FLOA":
                SetAperture(st, Optics.ApertureType.FloatByStopSize, 10);
                return;
            case "FTYP":
            {
                // Optiland parser.py _read_config_data: 1 type, 2 telecentric, 3 field count,
                // 4 wavelength count, 7 afocal; short (legacy) lines default.
                int type = t.Length > 1 ? Whole(t, 1, "field type") : 0;
                if (type < 0 || type > 4) { st.Problem(n, $"FTYP: the field type {type} is not one this build reads (0 angle, 1 object height, 2 paraxial image height, 3 real image height, 4 theodolite angle)."); type = 0; }
                st.FieldType = type;
                st.Telecentric = t.Length > 2 && Whole(t, 2, "telecentric flag") == 1;
                st.FieldCount = t.Length > 3 ? Whole(t, 3, "field count") : null;
                st.WavelengthCount = t.Length > 4 ? Whole(t, 4, "wavelength count") : null;
                st.Afocal = t.Length > 7 && Whole(t, 7, "afocal flag") == 1;
                return;
            }
            case "XFLN": case "YFLN": case "XFLD": case "YFLD": case "FWGN": case "FWGT":
            case "VDXN": case "VDYN": case "VCXN": case "VCYN": case "VANN":
            {
                string key = k switch { "XFLD" => "XFLN", "YFLD" => "YFLN", "FWGT" => "FWGN", _ => k };
                if (k is "XFLD" or "YFLD" or "FWGT") st.LegacyFields = true;
                st.FieldColumns[key] = Enumerable.Range(1, t.Length - 1).Select(i => Num(t, i, "field value")).ToList();
                return;
            }
            case "WAVM":
                st.WavelengthSlots[Whole(t, 1, "wavelength number")] = (Num(t, 2, "wavelength"), t.Length > 3 ? Num(t, 3, "weight") : 1);
                return;
            case "WAVL":
                st.LegacyWavelengths = Enumerable.Range(1, t.Length - 1).Select(i => Num(t, i, "wavelength")).ToList();
                return;
            case "WWGT":
                st.LegacyWeights = Enumerable.Range(1, t.Length - 1).Select(i => Num(t, i, "weight")).ToList();
                return;
            case "PWAV":
                st.PrimaryWavelength = Whole(t, 1, "primary wavelength");
                return;
            case "GCAT":
                st.Catalogs = t.Skip(1).ToList();
                return;

            // ---------------- rows after the surfaces
            case "MNUM":
                st.InConfigurations = true;
                if (t.Length < 3 || Whole(t, 1, "count") != 1 || Whole(t, 2, "count") != 1)
                    st.Problem(n, $"MNUM {string.Join(' ', t.Skip(1))}: multi-configuration data are not read (the row layout of the lens file is not published); only a single configuration (MNUM 1 1) is.");
                return;
            case "MOFF":
                return;   // an unused multi-configuration row
            case "TOL":
                if (t.Length < 2 || t[1] != "TOFF")
                    st.Problem(n, $"TOL {(t.Length > 1 ? t[1] : "")}: tolerance rows are not read (the row layout of the lens file is not published).");
                return;
            case "BLNK":
                st.Note(k, "BLNK: blank merit-function rows are not read.");
                return;
        }

        // ---------------- surface lines
        if (k == "SURF")
        {
            int number = Whole(t, 1, "surface number");
            if (number != st.Surfaces.Count) st.Problem(n, $"SURF {number}: surfaces must be numbered in order from 0; expected {st.Surfaces.Count}.");
            st.Current = new SurfaceBuilder { Number = st.Surfaces.Count, Line = n };
            st.Surfaces.Add(st.Current);
            return;
        }
        if (s is not null && IsSurfaceKeyword(k))
        {
            SurfaceLine(st, s, n, line, t, at);
            return;
        }
        if (IsSurfaceKeyword(k))
        {
            st.Problem(n, $"{k}: a surface keyword before the first SURF line.");
            return;
        }

        if (st.InConfigurations)
            st.Problem(n, $"{k}: multi-configuration row not read (the row layout of the lens file is not published).");
        else if (st.Surfaces.Count > 0 && !indented)
            st.Problem(n, $"{k}: a row after the surfaces (a merit-function operand or other data this build does not read).");
        else
            st.Problem(n, $"{at}{k}: keyword not read by this build.");
    }

    private static bool IsSurfaceKeyword(string k) => k is "TYPE" or "COMM" or "STOP" or "CURV" or "DISZ" or "CONI" or "GLAS"
        or "DIAM" or "PARM" or "XDAT" or "PZUP" or "PPAR" or "VPAR" or "VDSZ" or "CLAP" or "FLAP" or "OBDC" or "COAT";

    private static void SetAperture(State st, ApertureType type, double value)
    {
        st.ApertureType = ApertureTypes.Names.SavedName(type);
        st.ApertureValue = value;
        st.HaveAperture = true;
    }

    private static void SurfaceLine(State st, SurfaceBuilder s, int n, string line, string[] t, string at)
    {
        string k = t[0];
        switch (k)
        {
            case "TYPE":
                s.Code = t.Length > 1 ? t[1] : "";
                if (!ZemaxKeywords.TryTypeName(s.Code, out s.Type))
                {
                    if (ZemaxKeywords.RefusalOf(s.Code) is { } refused)   // OP-25: Grid Sag
                        st.Problem(n, $"{at}TYPE {s.Code}: {refused}.");
                    else st.Problem(n, $"{at}TYPE {s.Code}: the surface type is not one this build reads ({string.Join(", ", ZemaxKeywords.SurfaceTypes.Select(x => x.Code))}).");
                    s.Type = null;
                }
                return;
            case "COMM":
                s.Comment = ZemaxText.Rest(line, k);
                return;
            case "STOP":
                s.Stop = true;
                return;
            case "CURV":
            {
                double c = Num(t, 1, "curvature");
                s.Radius = c == 0 ? null : 1 / c;
                int code = t.Length > 2 ? Whole(t, 2, "solve code") : 0;
                switch (code)
                {
                    case 0: break;
                    case 1: AddFlag(st, s, n, new OpticsCellFlagSettings { Column = SavedSurfaces.RadiusColumn, Kind = SavedSurfaces.VariableFlag }); break;
                    case 4:
                        if (NumOr(t, 5, 0) != 0) { st.Problem(n, $"{at}CURV: a curvature pickup from another column (field 5 = {t[5]}) is not read."); break; }
                        AddFlag(st, s, n, new OpticsCellFlagSettings
                        {
                            Column = SavedSurfaces.RadiusColumn, Kind = SavedSurfaces.PickupFlag,
                            PickupSurface = Whole(t, 3, "pickup surface"), PickupScale = Num(t, 4, "pickup scale"),
                        });
                        break;
                    default:
                        st.Problem(n, $"{at}CURV: the curvature solve code {code} is not read (codes read: 0 fixed, 1 variable, 4 pickup).");
                        break;
                }
                return;
            }
            case "DISZ":
            {
                double v = Num(t, 1, "thickness");
                s.Infinite = double.IsPositiveInfinity(v);
                s.Thickness = s.Infinite ? 0 : v;
                return;
            }
            case "CONI":
                s.Conic = Num(t, 1, "conic");
                return;
            case "COAT":
                s.Coating = t.Length > 1 ? ZemaxText.Rest(line, k).Trim() : null;
                return;
            case "GLAS":
                Glass(st, s, n, t, at);
                return;
            case "DIAM":
            {
                double v = Num(t, 1, "semi-diameter");
                int code = t.Length > 2 ? Whole(t, 2, "solve code") : 0;
                switch (code)
                {
                    case 0: s.SemiDiameter = null; break;
                    case 1: s.SemiDiameter = v; break;
                    case 2:
                        s.SemiDiameter = v;
                        if (NumOr(t, 4, 0) != 0 || NumOr(t, 5, 1) != 1)
                        {
                            st.Problem(n, $"{at}DIAM: a semi-diameter pickup with fields 4–5 '{string.Join(' ', t.Skip(4).Take(2))}' is not read (only the observed '0 1').");
                            break;
                        }
                        AddFlag(st, s, n, new OpticsCellFlagSettings
                        {
                            Column = SavedSurfaces.SemiDiameterColumn, Kind = SavedSurfaces.PickupFlag, PickupSurface = Whole(t, 3, "pickup surface"),
                        });
                        break;
                    default:
                        st.Problem(n, $"{at}DIAM: the semi-diameter solve code {code} is not read (codes read: 0 automatic, 1 fixed, 2 pickup).");
                        break;
                }
                return;
            }
            case "PARM":
            {
                int p = Whole(t, 1, "parameter number");
                double v = Num(t, 2, "parameter value");
                if (p < 0 || p >= Sequential.SurfaceTypes.FirstExtraData) { st.Problem(n, $"{at}PARM {p}: parameter numbers 0 to 12 are read."); return; }
                if (v != 0) s.Parameters[p] = v;
                return;
            }
            case "XDAT":
            {
                int x = Whole(t, 1, "extra data number");
                double v = Num(t, 2, "extra data value");
                if (x < 1) { st.Problem(n, $"{at}XDAT {x}: extra data are numbered from 1."); return; }
                if (NumOr(t, 3, 0) != 0) st.Problem(n, $"{at}XDAT {x}: a solve or variable on extra data (field 3 = {t[3]}) is not read.");
                if (v != 0) s.Extra[x + Sequential.SurfaceTypes.FirstExtraData - 1] = v;
                return;
            }
            case "PZUP":
                if (NumOr(t, 3, 0) != 0 || NumOr(t, 4, 0) != 0)
                {
                    st.Problem(n, $"{at}PZUP: a thickness pickup with fields 3–4 '{string.Join(' ', t.Skip(3).Take(2))}' (offset or column) is not read; only '0 0' is.");
                    return;
                }
                AddFlag(st, s, n, new OpticsCellFlagSettings
                {
                    Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.PickupFlag,
                    PickupSurface = Whole(t, 1, "pickup surface"), PickupScale = Num(t, 2, "pickup scale"),
                });
                return;
            case "PPAR":
            {
                int p = Whole(t, 1, "parameter number");
                if (NumOr(t, 4, 0) != 0 || NumOr(t, 5, 0) != 0)
                {
                    st.Problem(n, $"{at}PPAR {p}: a parameter pickup with fields 4–5 '{string.Join(' ', t.Skip(4).Take(2))}' (offset or column) is not read; only '0 0' is.");
                    return;
                }
                if (p < 0 || p >= Sequential.SurfaceTypes.FirstExtraData) { st.Problem(n, $"{at}PPAR {p}: pickups on parameters 0 to 12 are read."); return; }
                AddFlag(st, s, n, new OpticsCellFlagSettings
                {
                    Column = SavedSurfaces.ParameterColumn(p), Kind = SavedSurfaces.PickupFlag,
                    PickupSurface = Whole(t, 2, "pickup surface"), PickupScale = Num(t, 3, "pickup scale"),
                });
                return;
            }
            case "VPAR":
            {
                int p = Whole(t, 1, "parameter number");
                if (p < 0 || p >= Sequential.SurfaceTypes.FirstExtraData) { st.Problem(n, $"{at}VPAR {p}: variables on parameters 0 to 12 are read."); return; }
                AddFlag(st, s, n, new OpticsCellFlagSettings { Column = SavedSurfaces.ParameterColumn(p), Kind = SavedSurfaces.VariableFlag });
                return;
            }
            case "VDSZ":
                AddFlag(st, s, n, new OpticsCellFlagSettings { Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.VariableFlag });
                return;
            case "CLAP":
                // Optiland parser.py _read_circular_aperture: field 1 minimum, field 2 maximum radius.
                SetSurfaceAperture(st, s, n, at, new OpticsSurfaceApertureSettings
                {
                    Type = SavedSurfaces.CircularAperture, MinRadius = Num(t, 1, "minimum radius"), MaxRadius = Num(t, 2, "maximum radius"),
                });
                return;
            case "FLAP":
                SetSurfaceAperture(st, s, n, at, new OpticsSurfaceApertureSettings { Type = SavedSurfaces.FloatingAperture });
                return;
            case "OBDC":
                // Optiland parser.py _read_aperture_decenter: the aperture's x and y decenter.
                s.ApertureDecenter = (Num(t, 1, "decenter x"), Num(t, 2, "decenter y"));
                return;
        }
    }

    private static void SetSurfaceAperture(State st, SurfaceBuilder s, int n, string at, OpticsSurfaceApertureSettings a)
    {
        if (s.Aperture is not null) { st.Problem(n, $"{at}{(a.Type == SavedSurfaces.FloatingAperture ? "FLAP" : "CLAP")}: the surface already has an aperture."); return; }
        s.Aperture = a;
    }

    private static void AddFlag(State st, SurfaceBuilder s, int n, OpticsCellFlagSettings flag)
    {
        if (s.Flags.ContainsKey(flag.Column!))
        {
            st.Problem(n, $"surface {s.Number}: the {flag.Column} cell has two solves or variables.");
            return;
        }
        s.Flags[flag.Column!] = flag;
    }

    private static void Glass(State st, SurfaceBuilder s, int n, string[] t, string at)
    {
        if (t.Length < 2) { st.Problem(n, $"{at}GLAS: no glass name."); return; }
        string name = t[1];
        if (string.Equals(name, SavedSurfaces.Mirror, StringComparison.OrdinalIgnoreCase))
        {
            s.Material = SavedSurfaces.Mirror;
            return;
        }
        int code = t.Length > 2 ? Whole(t, 2, "solve code") : 0;
        switch (code)
        {
            case 0:
                if (double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    st.Problem(n, $"{at}GLAS {name}: a glass given by its six-digit code is not read.");
                    return;
                }
                s.Material = name;
                s.MaterialLine = n;
                return;
            case 1:
            {
                // RayOptics zmxread.py ZmxGlassHandler: a model glass's nd and Vd follow the
                // code and one more field; Vd = 0 is a constant index. ΔPgF, the next field, is observed.
                double nd = Num(t, 4, "nd"), vd = Num(t, 5, "Vd");
                if (vd == 0) s.Index = nd;
                else s.ModelGlass = new OpticsModelGlassSettings { Nd = nd, Vd = vd, DeltaPgF = NumOr(t, 6, 0) };
                return;
            }
            case 2:
                s.Material = name;
                s.MaterialLine = n;
                AddFlag(st, s, n, new OpticsCellFlagSettings
                {
                    Column = SavedSurfaces.MaterialColumn, Kind = SavedSurfaces.PickupFlag, PickupSurface = Whole(t, 3, "pickup surface"),
                });
                return;
            default:
                st.Problem(n, $"{at}GLAS {name}: the glass solve code {code} is not read (codes read: 0 fixed, 1 model, 2 pickup).");
                return;
        }
    }

    // ------------------------------------------------------------------ the record

    private static readonly string[] FlagOrder =
    {
        SavedSurfaces.RadiusColumn, SavedSurfaces.ThicknessColumn, SavedSurfaces.MaterialColumn, SavedSurfaces.SemiDiameterColumn,
    };

    /// <summary>The canonical order of a surface's flags, which the writer also uses: radius,
    /// thickness, material, semi-diameter, then parameters by number.</summary>
    internal static int FlagRank(string? column)
    {
        int i = Array.IndexOf(FlagOrder, column);
        if (i >= 0) return i;
        return SavedSurfaces.ParameterNumber(column) is int p ? 100 + p : 1000;
    }

    private static OpticsSettings Finish(State st, IReadOnlyList<OpticalCatalog>? catalogs, IReadOnlyList<OpticalCatalog> archive)
    {
        if (!st.HaveAperture) st.Problems.Add("The file gives no aperture (ENPD, FNUM, OBNA or FLOA).");
        if (st.Surfaces.Count < 3) st.Problems.Add($"The file has {st.Surfaces.Count} surfaces; a lens needs the object, at least one surface and the image.");

        var system = new OpticsSystemSettings
        {
            Title = string.IsNullOrEmpty(st.Title) ? null : st.Title,
            Notes = st.HasNotes ? st.Notes2 : null,
            LensUnit = st.LensUnit ?? ConventionNames.LensUnits.SavedName(Optics.LensUnit.Millimeter),
            ApertureType = st.ApertureType,
            ApertureValue = st.ApertureValue,
            TelecentricObjectSpace = st.Telecentric,
            AfocalImageSpace = st.Afocal,
            FieldType = FieldTypes.Names.SavedName((FieldType)st.FieldType),
            Fields = Fields(st),
            Wavelengths = Wavelengths(st, out int primary),
            PrimaryWavelength = primary,
            TemperatureCelsius = st.Temperature ?? 20,
            PressureAtmospheres = st.Pressure ?? 1,
        };
        if (st.Temperature is null) st.Notes.Add("The file has no ENVD line; the temperature and pressure are this program's defaults (20 °C, 1 atm).");

        var search = SearchCatalogs(st, catalogs, archive);
        var surfaces = st.Surfaces.Select(b => Surface(st, b, search)).ToList();

        return new OpticsSettings
        {
            Mode = OpticsModes.SavedName(OpticsMode.Sequential),
            SystemData = system,
            Materials = st.Catalogs is null ? null : new OpticsMaterialsSettings { ActiveCatalogs = st.Catalogs.ToList() },
            Surfaces = surfaces,
        };
    }

    private static List<OpticsFieldSettings> Fields(State st)
    {
        var c = st.FieldColumns;
        int count;
        if (st.FieldCount is int declared) count = declared;
        else if (st.LegacyFields || c.Count == 0) count = c.Values.Select(v => v.Count).DefaultIfEmpty(1).Max();
        else
        {
            st.Problems.Add("The field columns (XFLN, YFLN) need the field count of an FTYP line, which the file does not give.");
            count = 0;
        }
        double At(string key, int i, double fallback) => c.TryGetValue(key, out var v) && i < v.Count ? v[i] : fallback;
        foreach (var (key, list) in c)
            if (list.Count < count && key is "XFLN" or "YFLN")
                st.Problems.Add($"{key}: {list.Count} values for {count} fields.");
        return Enumerable.Range(0, count).Select(i => new OpticsFieldSettings
        {
            X = At("XFLN", i, 0), Y = At("YFLN", i, 0), Weight = At("FWGN", i, 1),
            DecenterX = At("VDXN", i, 0), DecenterY = At("VDYN", i, 0),
            CompressionX = At("VCXN", i, 0), CompressionY = At("VCYN", i, 0), TiltDegrees = At("VANN", i, 0),
        }).ToList();
    }

    private static List<OpticsWavelengthSettings> Wavelengths(State st, out int primary)
    {
        primary = st.PrimaryWavelength;
        var list = new List<OpticsWavelengthSettings>();
        if (st.WavelengthSlots.Count > 0)
        {
            int count = st.WavelengthCount ?? st.WavelengthSlots.Keys.Max();
            for (int i = 1; i <= count; i++)
            {
                if (!st.WavelengthSlots.TryGetValue(i, out var w)) { st.Problems.Add($"WAVM: wavelength {i} of {count} is missing."); continue; }
                list.Add(new OpticsWavelengthSettings { Micrometers = w.Micrometers, Weight = w.Weight });
            }
        }
        else if (st.LegacyWavelengths is { } wl)
        {
            for (int i = 0; i < wl.Count; i++)
                list.Add(new OpticsWavelengthSettings { Micrometers = wl[i], Weight = st.LegacyWeights is { } ww && i < ww.Count ? ww[i] : 1 });
        }
        else st.Problems.Add("The file gives no wavelengths (WAVM or WAVL).");
        if (list.Count > 0 && (primary < 1 || primary > list.Count))
            st.Problems.Add($"PWAV {primary}: the primary wavelength must be 1 to {list.Count}.");
        return list;
    }

    private static List<OpticalCatalog>? SearchCatalogs(State st, IReadOnlyList<OpticalCatalog>? catalogs, IReadOnlyList<OpticalCatalog> archive)
    {
        if (catalogs is null && archive.Count == 0)
        {
            st.Notes.Add("Glass names were not checked against any catalog.");
            return null;
        }
        var all = archive.Concat(catalogs ?? Array.Empty<OpticalCatalog>()).ToList();
        if (st.Catalogs is null) return all;
        var ordered = new List<OpticalCatalog>(archive);
        foreach (string name in st.Catalogs)
        {
            var found = all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is null) st.Notes.Add($"GCAT: the catalog '{name}' is not loaded.");
            else if (!ordered.Contains(found)) ordered.Add(found);
        }
        return ordered;
    }

    private static OpticsSurfaceSettings Surface(State st, SurfaceBuilder b, List<OpticalCatalog>? search)
    {
        string where = $"Surface {b.Number} (line {b.Line})";
        var layout = Sequential.SurfaceTypes.Find(b.Type);
        if (!ZemaxKeywords.TryTypeName(b.Code, out _))
        {
            // The type was listed as a problem; read on (only with the warnings option), the
            // surface is a Standard one without the unknown type's numbers.
            if (b.Parameters.Count > 0 || b.Extra.Count > 0 || b.Flags.Keys.Any(k => SavedSurfaces.ParameterNumber(k) is not null))
                st.Notes.Add($"{where}: the parameters and extra data of the {b.Code} surface are left out; the surface is read as Standard.");
            b.Parameters.Clear();
            b.Extra.Clear();
            foreach (var key in b.Flags.Keys.Where(k => SavedSurfaces.ParameterNumber(k) is not null).ToList()) b.Flags.Remove(key);
        }
        if (b.Type is null && (b.Parameters.Count > 0 || b.Extra.Count > 0) && ZemaxKeywords.TryTypeName(b.Code, out _))
            st.Problems.Add($"{where}: a {b.Code} surface has no parameters, but PARM/XDAT give {string.Join(", ", b.Parameters.Keys.Select(k => "PARM " + k).Concat(b.Extra.Keys.Select(k => "XDAT " + (k - 12))))}.");
        if (layout is not null)
        {
            foreach (var p in b.Parameters.Keys.Concat(b.Extra.Keys))
                if (layout.Parameter(p) is null)
                    st.Problems.Add($"{where}: the {b.Code} surface has no {(p >= 13 ? $"XDAT {p - 12}" : $"PARM {p}")}.");
            foreach (var f in b.Flags.Values)
                if (SavedSurfaces.ParameterNumber(f.Column) is int fp && layout.Parameter(fp) is null)
                    st.Problems.Add($"{where}: the {b.Code} surface has no parameter {fp} to solve.");
        }

        string? material = b.Material;
        if (material is not null && material != SavedSurfaces.Mirror && search is not null && !Found(search, material))
        {
            if (st.Options.GlassSubstitutions is { } map
                && map.FirstOrDefault(kv => string.Equals(kv.Key, material, StringComparison.OrdinalIgnoreCase)) is { Value: { } sub }
                && Found(search, sub))
            {
                st.Notes.Add($"{where}: the glass '{material}' is not in the catalogs; replaced by '{sub}' as asked.");
                material = sub;
            }
            else
                st.Problem(b.MaterialLine, $"surface {b.Number}, GLAS {material}: the glass is not in the catalogs {string.Join(", ", search.Select(c => c.Name))}.");
        }
        // OP-53: a Birefringent In names its ordinary glass on the GLAS line; the extraordinary
        // one is that name with "-E" appended (help, "Birefringent In and Birefringent Out").
        if (b.Type == Sequential.Birefringent.BirefringentInShape.Name)
        {
            if (material is null || material == SavedSurfaces.Mirror)
                st.Problems.Add($"{where}: a BIRE__IN surface needs a GLAS line with a glass name (its ordinary glass; the extraordinary glass is that name with \"-E\" appended); a model glass, a constant index, air or a mirror is not read.");
            else if (search is not null && !Found(search, material + "-E"))
                st.Problem(b.MaterialLine, $"surface {b.Number}, GLAS {material}: a BIRE__IN surface takes its extraordinary index from the glass '{material}-E', which is not in the catalogs {string.Join(", ", search.Select(c => c.Name))}.");
        }

        var aperture = b.Aperture;
        if (b.ApertureDecenter is { } d)
        {
            if (aperture is null) st.Notes.Add($"{where}: OBDC (aperture decenter) without an aperture is not used.");
            else aperture = aperture with { DecenterX = d.X, DecenterY = d.Y };
        }

        var flags = b.Flags.Values.OrderBy(f => FlagRank(f.Column)).ToList();
        return new OpticsSurfaceSettings
        {
            Type = b.Type,
            Comment = string.IsNullOrEmpty(b.Comment) ? null : b.Comment,
            Radius = b.Radius,
            Thickness = b.Thickness,
            ThicknessIsInfinite = b.Infinite,
            Material = material,
            Index = b.Index,
            ModelGlass = b.ModelGlass,
            Coating = string.IsNullOrEmpty(b.Coating) ? null : b.Coating,
            SemiDiameter = b.SemiDiameter,
            Conic = b.Conic,
            IsStop = b.Stop,
            Aperture = aperture,
            Parameters = b.Parameters.Count == 0 ? null : b.Parameters.Select(p => new OpticsSurfaceParameterSettings { Number = p.Key, Value = p.Value }).ToList(),
            ExtraData = b.Extra.Count == 0 ? null : b.Extra.Select(p => new OpticsSurfaceParameterSettings { Number = p.Key, Value = p.Value }).ToList(),
            Flags = flags.Count == 0 ? null : flags,
        };
    }

    private static bool Found(List<OpticalCatalog> search, string name) => search.Any(c => c.Find(name) is not null);
}
