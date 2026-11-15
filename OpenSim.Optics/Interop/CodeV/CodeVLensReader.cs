using System.Globalization;
using System.Text.RegularExpressions;
using OpenSim.Core.Model;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Interop.CodeV;

/// <summary>
/// Reads the text of a CODE V sequence file (.seq) into the saved optical model
/// (<see cref="OpticsSettings"/>), plan item OP-95, docs/Optics-Conventions.md section 54.
/// <para>
/// CODE V's command language is not published by its vendor. Commands and their meanings come
/// from two permissively licensed readers, read as documentation: RayOptics 0.9.10 (BSD
/// 3-clause), <c>rayoptics/codev/reader.py</c> (comments, continuation, separators),
/// <c>cmdproc.py</c> and <c>tla_mapping.csv</c> (the command table); Optiland 0.6.3 (MIT),
/// <c>optiland/fileio/codev/reader/parser.py</c> and <c>converter.py</c>. Each conversion below
/// names the function it is read from. Where the two disagree the command is refused, never
/// guessed.
/// </para>
/// <para>
/// <b>Conventions converted, not copied.</b> CODE V's tilts ADE and BDE turn the other way from
/// this program's: RayOptics <c>util/misc_math.py</c>, <c>euler2opt</c>, "convert right-handed
/// euler angles to optical design convention, i.e. alpha and beta are left-handed", used by
/// <c>euler2rot3d</c> with the rotating axes x, y, z (<c>axes='rxyz'</c>); the decenter is applied
/// before the tilts (<c>elem/surface.py</c>, <c>DecenterData.tform_before_surf</c>, composed in
/// <c>elem/transform.py</c>, <c>forward_transform</c>). Section 4's tilts are right-handed about
/// x, the new y and the new z after the decenter, so a CODE V surface decenter is saved as
/// TiltX = −ADE, TiltY = −BDE, TiltZ = CDE in the order "decenter then tilt". Its kinds
/// (<c>DecenterData</c>): a basic decenter moves every later surface (a "before" tilt only); DAR
/// returns after the surface (the "after" mode "reverse this surface"); BEN turns the axis again
/// after the surface by the same rotation without the decenter (explicit "after" tilts); REV
/// applies the inverse after the surface and nothing before (explicit "after" values: the other
/// order, every value negated). Radii and thicknesses follow the same signs as section 3
/// (positive radius toward +z, thickness negative after an odd number of mirrors): REFL leaves
/// the medium as it was (<c>cmdproc.py</c>, <c>update_surface_and_gap</c>).
/// </para>
/// <para>
/// Every command is read, read and deliberately not used (named in the notes), or unsupported.
/// Unsupported content fails the import with every problem listed (line and surface), unless
/// <see cref="ZemaxImportOptions.UnsupportedAsWarnings"/> lists them instead.
/// </para>
/// </summary>
public static class CodeVLensReader
{
    public const string Extension = ".seq";

    /// <summary>Commands read and deliberately not used, with the reason the notes give.</summary>
    public static IReadOnlyDictionary<string, string> Ignored { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LEN"] = "starts a new lens; its quoted version text is not data (RayOptics cmdproc.py spec_data passes LEN)",
        ["INI"] = "the designer's initials (Optiland parser.py: \"designer initials - ignored\")",
        ["CWL"] = "the coating reference wavelengths (Optiland parser.py: \"coating ref wavelength - ignored\"); coatings are not read",
        ["WID"] = "the wide-angle ray-aiming flag (RayOptics field_spec_data); ray aiming is not read",
        ["POL"] = "the polarization ray-trace setting; polarization is not read",
        ["CSO"] = "the glass catalog search order (RayOptics spec_data); glasses are looked for in every loaded catalog",
        ["GO"] = "ends the input in CODE V-written files and carries no data (section 54, §10 item 50)",
        ["DER"] = "optimization data that do not change the lens data (section 54, §10 item 50)",
    };

    /// <summary>Variable codes and solves: the value written is used and the code is not kept.</summary>
    public static IReadOnlyCollection<string> CodesNotKept { get; } = new[] { "CCY", "CCX", "THC", "GLC", "PIM" };

    // RayOptics tla_mapping.csv: the qualifiers of CUY, CUX and THI that make the command a solve.
    private static readonly HashSet<string> SolveQualifiers = new(StringComparer.Ordinal)
    {
        "UMY", "UCY", "IMY", "ICY", "AMY", "ACY", "UMX", "UCX", "IMX", "ICX", "AMX", "ACX",
        "HMX", "HMY", "HCX", "HCY", "OAL", "ET",
    };

    // RayOptics cmdproc.py profile_data: A … J are the coefficients on r^4 … r^20; the Even
    // Asphere's parameter n is the coefficient on r^(2n), so A is parameter 2 … G parameter 8.
    private static readonly string AsphereLetters = "ABCDEFGHJ";

    private sealed class Decenter
    {
        public int Line;
        public double X, Y, Z, A, B, C;
        public string Kind = "decenter";
        public int KindLine;
    }

    private sealed class Aperture
    {
        public int Line;
        public char Shape;           // 'C' circular, 'R' rectangular, 'E' elliptical
        public bool Obscuration;
        public double R, X, Y, Dx, Dy;
    }

    private sealed class Reader
    {
        public required LensFileState St;
        public required IReadOnlyList<OpticalCatalog>? Catalogs;
        public readonly List<LensFileSurface> Surfaces = new();
        public readonly Dictionary<LensFileSurface, Decenter> Decenters = new();
        public readonly Dictionary<LensFileSurface, Aperture> Apertures = new();
        public bool HaveImage;
        public bool? RadiusMode;
        public string? Title;
        public LensUnit? Unit;
        public ApertureType? ApertureKind;
        public double ApertureValue;
        public FieldType? FieldKind;
        public int FieldLine;
        public readonly Dictionary<string, (int Line, double[] Values)> FieldColumns = new(StringComparer.Ordinal);
        public double[]? Wavelengths;
        public double[]? WavelengthWeights;
        public int? Reference;
        public int ReferenceLine;
        public double ImageShift;
        public bool InPrivateCatalog;
        public LensFileSurface? Current => Surfaces.Count == 0 ? null : Surfaces[^1];
    }

    /// <summary>
    /// The optical model of a sequence file's text. <paramref name="catalogs"/> are the glass
    /// catalogs glass names are checked against (null skips the check, with a note). Throws
    /// <see cref="ZemaxFileException"/> listing every problem in the strict mode.
    /// </summary>
    public static ZemaxImportResult Read(string text, IReadOnlyList<OpticalCatalog>? catalogs, ZemaxImportOptions? options = null, string encoding = "text")
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new ZemaxImportOptions();
        var r = new Reader { St = new LensFileState(options), Catalogs = catalogs };
        foreach (var (line, tokens) in Commands(text))
        {
            try { Command(r, line, tokens); }
            catch (FormatException ex) { r.St.Problem(line, $"{tokens[0].ToUpperInvariant()}: {ex.Message}"); }
        }
        var optics = Finish(r);
        if (r.St.Problems.Count > 0 && !options.UnsupportedAsWarnings)
            throw new ZemaxFileException("The CODE V sequence file cannot be read as it stands (open it with unsupported content listed as warnings to read the rest):", r.St.Problems);
        return new ZemaxImportResult(optics, options.UnsupportedAsWarnings ? r.St.Problems.ToArray() : Array.Empty<string>(),
            r.St.Notes.ToArray(), Array.Empty<OpticalCatalog>(), encoding);
    }

    // ------------------------------------------------------------------ the text

    /// <summary>
    /// The commands of a sequence file, each with the line it starts on. RayOptics reader.py
    /// (<c>strip_comments</c>, <c>next_line</c>, <c>read_seq_buffer</c>, <c>tokenize_command</c>)
    /// and Optiland parser.py (<c>_load_and_preprocess</c>): "!" starts a comment to the end of
    /// the line, a line ending in "&amp;" continues on the next, ";" separates commands, tokens
    /// are separated by blanks and a quoted string (single or double quotes) is one token.
    /// </summary>
    internal static List<(int Line, string[] Tokens)> Commands(string text)
    {
        var result = new List<(int, string[])>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? pending = null;
        int start = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripComment(lines[i]).TrimEnd();
            if (pending is null) start = i + 1;
            else line = pending + " " + line;
            if (line.EndsWith('&'))
            {
                pending = line[..^1];
                continue;
            }
            pending = null;
            foreach (var segment in SplitOutsideQuotes(line, ';'))
            {
                var tokens = Tokenize(segment);
                if (tokens.Length > 0) result.Add((start, tokens));
            }
        }
        if (pending is not null)
            foreach (var segment in SplitOutsideQuotes(pending, ';'))
            {
                var tokens = Tokenize(segment);
                if (tokens.Length > 0) result.Add((start, tokens));
            }
        return result;
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '!') return line[..i];
        }
        return line;
    }

    private static IEnumerable<string> SplitOutsideQuotes(string line, char separator)
    {
        char quote = '\0';
        int from = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == separator) { yield return line[from..i]; from = i + 1; }
        }
        yield return line[from..];
    }

    private static readonly Regex TokenPattern = new("'[^']*'|\"[^\"]*\"|\\S+", RegexOptions.CultureInvariant);

    private static string[] Tokenize(string segment) =>
        TokenPattern.Matches(segment).Select(m => m.Value.Length >= 2 && (m.Value[0] is '\'' or '"') && m.Value[^1] == m.Value[0]
            ? m.Value[1..^1] : m.Value).ToArray();

    // ------------------------------------------------------------------ one command

    private static double Num(string[] t, int i, string what)
    {
        if (i >= t.Length) throw new FormatException($"the {what} (field {i}) is missing.");
        if (!LensFileModel.TryNumber(t[i], out double v)) throw new FormatException($"the {what} '{t[i]}' (field {i}) is not a number.");
        return v;
    }

    private static double[] Numbers(string[] t, int from, string what) =>
        Enumerable.Range(from, Math.Max(0, t.Length - from)).Select(i => Num(t, i, what)).ToArray();

    /// <summary>The command's three-letter name: RayOptics cmdproc.py <c>process_command</c>
    /// reads the first three letters ("TITLE" is TIT); shorter names are kept whole.</summary>
    private static string Name(string token)
    {
        string k = token.ToUpperInvariant();
        return k.Length > 3 ? k[..3] : k;
    }

    private static readonly Regex SurfaceQualifier = new("^(S[0-9]+|SO|SI)$", RegexOptions.CultureInvariant);
    private static readonly Regex ZoomQualifier = new("^Z[0-9]+$", RegexOptions.CultureInvariant);

    private static void Command(Reader r, int n, string[] t)
    {
        var st = r.St;
        string raw = t[0].ToUpperInvariant();
        string k = Name(t[0]);

        if (r.InPrivateCatalog)
        {
            if (k == "END") r.InPrivateCatalog = false;
            return;   // the block was listed once at PRV
        }
        if (t.Skip(1).Any(x => ZoomQualifier.IsMatch(x.ToUpperInvariant())))
        {
            st.Problem(n, $"{raw}: zoom positions (Z qualifiers) are not read; only a single configuration is.");
            return;
        }
        if (Ignored.TryGetValue(k, out string? why))
        {
            st.Note(k, $"{k}: not read ({why}).");
            return;
        }
        if (CodesNotKept.Contains(k))
        {
            st.Note(k, $"{k}: a variable code or solve; the value the file writes is used and the code is not kept (section 54).");
            return;
        }

        switch (raw)
        {
            case "SO":
                if (r.Surfaces.Count > 0) { st.Problem(n, "SO: the object surface must be the first surface."); return; }
                NewSurface(r, n, t, isObject: true);
                return;
            case "S":
                if (r.Surfaces.Count == 0) { st.Problem(n, "S: a surface before the object surface (SO); the object must come first."); return; }
                if (r.HaveImage) { st.Problem(n, "S: a surface after the image surface (SI)."); return; }
                NewSurface(r, n, t, isObject: false);
                return;
            case "SI":
                if (r.HaveImage) { st.Problem(n, "SI: a second image surface."); return; }
                if (r.Surfaces.Count == 0) { st.Problem(n, "SI: an image surface before the object surface (SO)."); return; }
                NewSurface(r, n, t, isObject: false);
                r.HaveImage = true;
                // RayOptics cmdproc.py update_surface_and_gap: "at image surface, apply defocus
                // to previous thickness".
                r.ImageShift = r.Current!.Thickness;
                r.Current.Thickness = 0;
                return;
        }

        switch (k)
        {
            // ---------------- system data
            case "RDM":
                // RayOptics spec_data (Boolean: a value starting with N is false, none is true);
                // Optiland _read_rdm.
                r.RadiusMode = t.Length < 2 || !t[1].StartsWith("N", StringComparison.OrdinalIgnoreCase);
                return;
            case "TIT":
                r.Title = string.Join(' ', t.Skip(1)).Trim();
                return;
            case "DIM":
            {
                string d = t.Length > 1 ? t[1].ToUpperInvariant() : "";
                // RayOptics spec_data: M or MM, C or CM, I or IN.
                r.Unit = d switch { "M" or "MM" => LensUnit.Millimeter, "C" or "CM" => LensUnit.Centimeter, "I" or "IN" => LensUnit.Inch, _ => null };
                if (r.Unit is null) st.Problem(n, $"DIM {d}: the dimension is not one this build reads (M, C, I; RayOptics cmdproc.py spec_data).");
                return;
            }
            case "TEM":
            {
                double v = Num(t, 1, "temperature");
                if (v == 20) st.Note(k, "TEM 20: not read (neither reader gives its unit); the temperature is this program's default, 20 °C.");
                else st.Problem(n, $"TEM {t[1]}: the temperature is not read (neither reader gives its unit); only 20, which matches this program's default 20 °C, is accepted.");
                return;
            }
            case "PRE":
                st.Problem(n, "PRE: the pressure is not read (neither reader gives its unit).");
                return;
            // RayOptics cmdproc.py pupil_spec_data and Optiland converter.py: EPD the entrance
            // pupil diameter, NAO the object-space NA, FNO the image-space F/# (RayOptics key
            // ('image', 'f/#'), turned into the paraxial image-space slope −1/(2·FNO) at the
            // conjugates in use, parax/etendue.py get_slope_from_aperture: the paraxial working F/#).
            case "EPD":
                SetAperture(r, n, ApertureType.EntrancePupilDiameter, Num(t, 1, "entrance pupil diameter"));
                return;
            case "FNO":
                SetAperture(r, n, ApertureType.ParaxialWorkingFNumber, Num(t, 1, "F/#"));
                return;
            case "NAO":
                SetAperture(r, n, ApertureType.ObjectSpaceNumericalAperture, Num(t, 1, "object NA"));
                return;
            case "NA":
                st.Problem(n, "NA: an image-space numerical aperture has no aperture type here (Optiland converter.py refuses it too).");
                return;
            case "WL":
                r.Wavelengths = Enumerable.Range(1, t.Length - 1).Select(i =>
                    LensFileModel.TryScaledNumber(t[i], -3, out double um) ? um : throw new FormatException($"the wavelength '{t[i]}' is not a number.")).ToArray();
                return;
            case "WTW":
                r.WavelengthWeights = Numbers(t, 1, "weight");
                return;
            case "REF":
                double reference = Num(t, 1, "reference wavelength");
                if (Math.Round(reference) != reference) throw new FormatException($"the reference wavelength '{t[1]}' is not a whole number.");
                r.Reference = (int)reference;
                r.ReferenceLine = n;
                return;
            case "XAN": case "YAN": case "XOB": case "YOB": case "XIM": case "YIM": case "XRI": case "YRI":
            {
                // RayOptics cmdproc.py field_spec_data: AN object angle, OB object height, IM
                // (paraxial) image height, RI real image height.
                var kind = k[1..] switch
                {
                    "AN" => FieldType.Angle, "OB" => FieldType.ObjectHeight, "IM" => FieldType.ParaxialImageHeight, _ => FieldType.RealImageHeight,
                };
                if (r.FieldKind is { } had && had != kind) st.Problem(n, $"{k}: the fields are already given as {FieldTypes.Names.SavedName(had)} (line {r.FieldLine}); one field type is read.");
                r.FieldKind = kind;
                r.FieldLine = n;
                r.FieldColumns[k[..1]] = (n, Numbers(t, 1, "field value"));
                return;
            }
            case "WTF": case "VUX": case "VLX": case "VUY": case "VLY":
                r.FieldColumns[k] = (n, Numbers(t, 1, k == "WTF" ? "field weight" : "vignetting factor"));
                return;
            case "PRV":
                st.Problem(n, "PRV: a private glass catalog is not read (its glasses are tabulated indices, which a surface here cannot hold); the block to its END is left out.");
                r.InPrivateCatalog = true;
                return;
        }

        // ---------------- surface commands
        var s = Target(r, n, t, out int first);
        if (s is null) return;
        string at = s.At;
        switch (k)
        {
            case "STO":
                foreach (var other in r.Surfaces) other.Stop = false;
                s.Stop = true;
                return;
            case "SLB":
                s.Comment = string.Join(' ', t.Skip(first)).Trim();
                return;
            case "SPH": case "CON": case "ASP":
                return;   // the profile follows from the coefficients given (RayOptics surface_data)
            case "K":
                s.Conic = Num(t, first, "conic constant");
                return;
            case "A": case "B": case "C": case "D": case "E": case "F": case "G": case "H": case "J":
            {
                double v = Num(t, first, "aspheric coefficient");
                int p = AsphereLetters.IndexOf(k[0]) + 2;
                if (p > 8) { if (v != 0) st.Problem(n, $"{at}{k}: the coefficient on r^{2 * p} is not held (the Even Asphere ends at r^16)."); return; }
                if (v != 0) s.Parameters[p] = v; else s.Parameters.Remove(p);
                return;
            }
            case "RDY": case "CUY":
            {
                if (t.Length > first && SolveQualifiers.Contains(t[first].ToUpperInvariant())) { NoteSolve(st, k); return; }
                double v = Num(t, first, k == "RDY" ? "radius" : "curvature");
                s.Radius = v == 0 ? null : k == "RDY" ? v : 1 / v;
                return;
            }
            case "RDX": case "CUX":
                if (t.Length > first && SolveQualifiers.Contains(t[first].ToUpperInvariant())) { NoteSolve(st, k); return; }
                st.Problem(n, $"{at}{k}: an x radius or curvature (a toroid) is not read.");
                return;
            case "THI":
            {
                if (t.Length > first && SolveQualifiers.Contains(t[first].ToUpperInvariant())) { NoteSolve(st, k); return; }
                double v = Num(t, first, "thickness");
                if (ReferenceEquals(s, r.Surfaces[^1]) && r.HaveImage) r.ImageShift = v;
                else Thickness(r, s, v, n);
                return;
            }
            case "GLA":
                if (t.Length <= first) { st.Problem(n, $"{at}GLA: no glass."); return; }
                Glass(r, s, t[first], n);
                return;
            case "RMD":
            {
                string mode = t.Length > first ? t[first].ToUpperInvariant() : "";
                if (mode == "REFL") Glass(r, s, "REFL", n);
                else if (mode != "REFR") st.Problem(n, $"{at}RMD {mode}: only REFL (reflect) and REFR (refract) are read.");
                return;
            }
            case "XDE": case "YDE": case "ZDE": case "ADE": case "BDE": case "CDE":
            {
                var d = DecenterOf(r, s, n);
                double v = Num(t, first, "decenter");
                switch (k[0])
                {
                    case 'X': d.X = v; break;
                    case 'Y': d.Y = v; break;
                    case 'Z': d.Z = v; break;
                    case 'A': d.A = v; break;
                    case 'B': d.B = v; break;
                    default: d.C = v; break;
                }
                return;
            }
            case "DAR": case "BEN": case "REV":
            {
                var d = DecenterOf(r, s, n);
                if (d.Kind != "decenter" && d.Kind != k) st.Problem(n, $"{at}{k}: the surface already has {d.Kind} (line {d.KindLine}); one decenter kind is read.");
                d.Kind = k;
                d.KindLine = n;
                return;
            }
            case "CIR": case "REX": case "REY": case "ELX": case "ELY":
                ApertureCommand(r, s, n, k, t, first);
                return;
            case "ADX": case "ADY": case "ARO":
            {
                if (!r.Apertures.TryGetValue(s, out var a)) { st.Problem(n, $"{at}{k}: an aperture offset without an aperture."); return; }
                double v = Num(t.Where(x => !IsApertureQualifier(x)).ToArray(), first, "offset");
                if (k == "ADX") a.Dx = v;
                else if (k == "ADY") a.Dy = v;
                else if (v != 0) st.Problem(n, $"{at}ARO: a rotated aperture is not read.");
                return;
            }
        }
        st.Problem(n, $"{at}{raw}: command not read by this build.");
    }

    private static void NoteSolve(LensFileState st, string k) =>
        st.Note("solve:" + k, $"{k} with a solve qualifier: the solve is not kept; the value the surface line writes is used (section 54).");

    private static bool IsApertureQualifier(string token) => token.ToUpperInvariant() is "CLR" or "EDG" or "OBS" or "HOL";

    /// <summary>The surface a command acts on: the one named by an S qualifier (Sk, SO, SI)
    /// right after the command, else the last surface read.</summary>
    private static LensFileSurface? Target(Reader r, int n, string[] t, out int first)
    {
        first = 1;
        if (t.Length > 1 && SurfaceQualifier.IsMatch(t[1].ToUpperInvariant()))
        {
            first = 2;
            string q = t[1].ToUpperInvariant();
            int index = q == "SO" ? 0 : q == "SI" ? (r.HaveImage ? r.Surfaces.Count - 1 : -1) : int.Parse(q[1..], CultureInfo.InvariantCulture);
            if (index < 0 || index >= r.Surfaces.Count)
            {
                r.St.Problem(n, $"{t[0].ToUpperInvariant()} {q}: the surface is not defined yet.");
                return null;
            }
            return r.Surfaces[index];
        }
        if (r.Current is null)
        {
            r.St.Problem(n, $"{t[0].ToUpperInvariant()}: command not read by this build, or a surface command before the first surface.");
            return null;
        }
        return r.Current;
    }

    private static void SetAperture(Reader r, int n, ApertureType type, double value)
    {
        if (r.ApertureKind is { } had && had != type)
            r.St.Note("aperture-twice", $"Line {n}: a second aperture specification; the last one given is used.");
        r.ApertureKind = type;
        r.ApertureValue = value;
    }

    private static void NewSurface(Reader r, int n, string[] t, bool isObject)
    {
        var s = new LensFileSurface { Number = r.Surfaces.Count, Line = n };
        r.Surfaces.Add(s);
        string at = s.At;
        if (t.Length < 3) { r.St.Problem(n, $"{at}{t[0].ToUpperInvariant()}: the radius (or curvature) and thickness fields are missing."); return; }
        double v = Num(t, 1, "radius or curvature");
        if (v != 0)
        {
            if (r.RadiusMode is null)
            {
                r.St.Problem(n, $"{at}{t[0].ToUpperInvariant()}: the file does not say (RDM) whether surface values are radii or curvatures, and the readers' defaults differ (RayOptics: curvature, Optiland: radius).");
                s.Radius = v;
            }
            else s.Radius = r.RadiusMode.Value ? v : 1 / v;
        }
        double thickness = Num(t, 2, "thickness");
        if (isObject)
        {
            // Optiland converter.py _build_surface_params: "Large object distances (≥ 1e10)
            // represent optical infinity in CODE V".
            if (Math.Abs(thickness) >= 1e10)
            {
                s.Infinite = true;
                r.St.Note("object-infinity", $"The object distance {t[2]} (≥ 1e10) is read as infinite (Optiland converter.py).");
            }
            else s.Thickness = thickness;
        }
        else s.Thickness = thickness;
        if (t.Length > 3) Glass(r, s, t[3], n);
        if (t.Length > 4) r.St.Problem(n, $"{at}{t[0].ToUpperInvariant()}: the field '{string.Join(' ', t.Skip(4))}' after the glass is not read.");
    }

    private static void Thickness(Reader r, LensFileSurface s, double v, int n)
    {
        if (s.Number == 0 && Math.Abs(v) >= 1e10) { s.Infinite = true; s.Thickness = 0; return; }
        s.Infinite = false;
        s.Thickness = v;
    }

    private static readonly Regex DecimalGlassCode = new("^([0-9]{3})\\.([0-9]{1,3})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A glass token (Optiland parser.py <c>_parse_glass</c>, RayOptics cmdproc.py
    /// <c>CVGlassHandler.process_glass_data</c>): REFL a mirror; AIR air; nd:Vd a fictitious glass
    /// (Optiland <c>_parse_glass_abbe_ratio</c>); NNN.VVV a fictitious glass nd = 1.NNN, Vd = VV.V,
    /// read alike by both (Optiland <c>_parse_glass_legacy_decimal_code</c>, RayOptics
    /// <c>fictitious_glass_decode</c>), only with three digits before the point, where they agree;
    /// name_catalog a glass of that catalog (split at the last underscore, Optiland
    /// <c>_parse_glass_name_catalog</c>); otherwise a glass name. A six-digit code is refused:
    /// RayOptics looks it up as a catalog glass code, Optiland reads nd and Vd from it.
    /// </summary>
    private static void Glass(Reader r, LensFileSurface s, string token, int n)
    {
        string at = s.At;
        string g = token.Trim();
        string upper = g.ToUpperInvariant();
        s.Material = null; s.Index = null; s.ModelGlass = null;
        if (upper == "REFL") { s.Material = SavedSurfaces.Mirror; return; }
        if (upper == "AIR") return;
        int colon = g.IndexOf(':');
        if (colon > 0)
        {
            if (LensFileModel.TryNumber(g[..colon], out double nd) && LensFileModel.TryNumber(g[(colon + 1)..], out double vd))
            {
                s.ModelGlass = new OpticsModelGlassSettings { Nd = nd, Vd = vd };
                FictitiousNote(r);
            }
            else r.St.Problem(n, $"{at}glass {g}: an nd:Vd glass whose fields are not numbers.");
            return;
        }
        var m = DecimalGlassCode.Match(g);
        if (m.Success)
        {
            string v = m.Groups[2].Value.PadRight(3, '0');
            s.ModelGlass = new OpticsModelGlassSettings
            {
                Nd = double.Parse("1." + m.Groups[1].Value, CultureInfo.InvariantCulture),
                Vd = double.Parse(v[..2] + "." + v[2..], CultureInfo.InvariantCulture),
            };
            FictitiousNote(r);
            return;
        }
        if (LensFileModel.TryNumber(g, out _))
        {
            r.St.Problem(n, $"{at}glass {g}: a numeric glass code other than NNN.VVV is not read (the readers disagree on six-digit codes and on longer decimal codes).");
            return;
        }
        int us = g.LastIndexOf('_');
        string name = us > 0 ? g[..us] : g;
        string? catalog = us > 0 && us < g.Length - 1 ? g[(us + 1)..] : null;
        s.Material = LensFileModel.ResolveGlass(r.St, name, catalog, n, at, r.Catalogs, hyphenCandidates: true);
    }

    private static void FictitiousNote(Reader r) => r.St.Note("fictitious",
        "A fictitious glass (nd, Vd) is read as this program's model glass with ΔPgF = 0; its dispersion away from the d line is this program's model glass formula, not CODE V's (section 54, §10 item 50).");

    private static Decenter DecenterOf(Reader r, LensFileSurface s, int n)
    {
        if (!r.Decenters.TryGetValue(s, out var d)) r.Decenters[s] = d = new Decenter { Line = n };
        return d;
    }

    /// <summary>RayOptics cmdproc.py <c>aperture_data</c>: CIR a circle's radius, REX/REY a
    /// rectangle's half-widths, ELX/ELY an ellipse's; qualifiers CLR (clear, the default), OBS
    /// (obscuration), EDG (edge aperture), HOL (hole).</summary>
    private static void ApertureCommand(Reader r, LensFileSurface s, int n, string k, string[] t, int first)
    {
        string at = s.At;
        var quals = t.Skip(first).Where(IsApertureQualifier).Select(x => x.ToUpperInvariant()).ToList();
        var rest = t.Take(first).Concat(t.Skip(first).Where(x => !IsApertureQualifier(x))).ToArray();
        double v = Num(rest, first, "aperture size");
        if (quals.Contains("HOL")) { r.St.Problem(n, $"{at}{k} HOL: a hole aperture is not read."); return; }
        if (quals.Contains("EDG"))
        {
            r.St.Note("edge-aperture", $"Line {n}: {at}{k} EDG: an edge (mechanical) aperture is not read; it does not limit rays.");
            return;
        }
        char shape = k[0];
        bool obscuration = quals.Contains("OBS");
        if (r.Apertures.TryGetValue(s, out var a))
        {
            if (a.Shape != shape || a.Obscuration != obscuration)
            {
                r.St.Problem(n, $"{at}{k}: the surface already has an aperture (line {a.Line}); one aperture per surface is held.");
                return;
            }
        }
        else r.Apertures[s] = a = new Aperture { Line = n, Shape = shape, Obscuration = obscuration };
        if (k == "CIR") a.R = v;
        else if (k[2] == 'X') a.X = v;
        else a.Y = v;
    }

    // ------------------------------------------------------------------ the record

    private static OpticsSettings Finish(Reader r)
    {
        var st = r.St;
        if (r.Surfaces.Count < 3) st.Problem(0, $"The file has {r.Surfaces.Count} surfaces; a lens needs the object (SO), at least one surface and the image (SI).");
        else if (!r.HaveImage) st.Problem(0, "The file has no image surface (SI).");
        if (r.ApertureKind is null) st.Problem(0, "The file gives no aperture (EPD, FNO or NAO).");
        if (r.Unit is null) st.Note("dim", "The file has no DIM; the lens unit is millimeters (both readers' default).");
        st.Note("environment", "The temperature and pressure are this program's defaults (20 °C, 1 atm).");

        if (r.ImageShift != 0 && r.Surfaces.Count >= 2)
        {
            var before = r.Surfaces[^2];
            before.Thickness += r.ImageShift;
            st.Notes.Add($"The image surface's thickness {r.ImageShift.ToString("R", CultureInfo.InvariantCulture)} is a focus shift; it is added to surface {before.Number}'s thickness (RayOptics cmdproc.py update_surface_and_gap).");
        }

        if (!r.Surfaces.Any(s => s.Stop) && r.Surfaces.Count >= 3)
        {
            r.Surfaces[1].Stop = true;
            st.Notes.Add("The file names no stop (STO); surface 1 is the stop (Optiland converter.py: \"if no STO in file, surface 1\").");
        }
        if (r.Surfaces.Count > 0 && r.Surfaces[0].Stop) st.Problem(0, "The object surface cannot be the stop.");

        foreach (var (s, d) in r.Decenters) Decenters(r, s, d);
        foreach (var (s, a) in r.Apertures)
        {
            s.Aperture = a.Shape switch
            {
                'C' => new OpticsSurfaceApertureSettings { Type = SavedSurfaces.CircularAperture, MaxRadius = a.R },
                'R' => new OpticsSurfaceApertureSettings { Type = SavedSurfaces.RectangularAperture, HalfWidthX = a.X, HalfWidthY = a.Y },
                _ => new OpticsSurfaceApertureSettings { Type = SavedSurfaces.EllipticalAperture, HalfWidthX = a.X, HalfWidthY = a.Y },
            } with { IsObscuration = a.Obscuration, DecenterX = a.Dx, DecenterY = a.Dy };
            if (s.Number == 0) st.Problem(a.Line, "surface 0: an aperture on the object surface is not read.");
        }

        var system = new OpticsSystemSettings
        {
            Title = string.IsNullOrEmpty(r.Title) ? null : r.Title,
            LensUnit = LensFileModel.Unit(r.Unit ?? LensUnit.Millimeter),
            ApertureType = r.ApertureKind is { } ak ? ApertureTypes.Names.SavedName(ak) : null,
            ApertureValue = r.ApertureKind is null ? 10 : r.ApertureValue,
            FieldType = FieldTypes.Names.SavedName(r.FieldKind ?? FieldType.Angle),
            Fields = Fields(r),
            Wavelengths = Wavelengths(r, out int primary),
            PrimaryWavelength = primary,
            TemperatureCelsius = 20,
            PressureAtmospheres = 1,
        };
        return new OpticsSettings
        {
            Mode = OpticsModes.SavedName(OpticsMode.Sequential),
            SystemData = system,
            Materials = st.UsedCatalogs.Count == 0 ? null : new OpticsMaterialsSettings { ActiveCatalogs = st.UsedCatalogs.ToList() },
            Surfaces = r.Surfaces.Select(LensFileModel.Surface).ToList(),
        };
    }

    private static double Neg(double v) => v == 0 ? 0 : -v;

    /// <summary>A surface decenter in this program's convention (see the class remarks).</summary>
    private static void Decenters(Reader r, LensFileSurface s, Decenter d)
    {
        var st = r.St;
        if (d.Z != 0) { st.Problem(d.Line, $"{s.At}ZDE: a z decenter is not held by a surface tilt and decenter (section 4 has no z shift)."); }
        if (s.Number == 0) { st.Problem(d.Line, $"{s.At}a decenter of the object surface is not read (section 15 refuses a tilted object)."); return; }
        bool isImage = r.HaveImage && ReferenceEquals(s, r.Surfaces[^1]);
        if (isImage && d.Kind != "decenter") { st.Problem(d.KindLine, $"{s.At}{d.Kind}: the image surface has no tilt after it."); return; }
        var forward = new OpticsTiltDecenterSettings { DecenterX = d.X, DecenterY = d.Y, TiltX = Neg(d.A), TiltY = Neg(d.B), TiltZ = d.C };
        bool any = d.X != 0 || d.Y != 0 || d.A != 0 || d.B != 0 || d.C != 0;
        switch (d.Kind)
        {
            case "decenter":
                s.TiltBefore = any ? forward : null;
                break;
            case "DAR":
                s.TiltBefore = forward;
                s.TiltAfter = new OpticsTiltDecenterSettings { Mode = LensFileModel.ReverseThisSurface };
                break;
            case "BEN":
                s.TiltBefore = forward;
                s.TiltAfter = new OpticsTiltDecenterSettings { TiltX = forward.TiltX, TiltY = forward.TiltY, TiltZ = forward.TiltZ };
                break;
            default:   // REV
                s.TiltAfter = new OpticsTiltDecenterSettings
                {
                    DecenterX = Neg(d.X), DecenterY = Neg(d.Y), TiltX = d.A, TiltY = d.B, TiltZ = Neg(d.C),
                    Order = LensFileModel.TiltThenDecenter,
                };
                break;
        }
    }

    private static List<OpticsFieldSettings> Fields(Reader r)
    {
        var st = r.St;
        var c = r.FieldColumns;
        if (!c.ContainsKey("X") && !c.ContainsKey("Y"))
        {
            st.Notes.Add("The file gives no field (XAN, YAN, XOB, YOB, XIM, YIM, XRI, YRI); one on-axis field is used (both readers' default).");
            return new List<OpticsFieldSettings> { new() };
        }
        int count = Math.Max(c.TryGetValue("X", out var x) ? x.Values.Length : 0, c.TryGetValue("Y", out var y) ? y.Values.Length : 0);
        foreach (var (key, (line, values)) in c)
            if (values.Length != count && !((key == "X" || key == "Y") && values.Length == 0))
                st.Problem(line, $"{(key.Length == 1 ? key + " field" : key)}: {values.Length} values for {count} fields.");
        double At(string key, int i, double fallback) => c.TryGetValue(key, out var v) && i < v.Values.Length ? v.Values[i] : fallback;
        // RayOptics zemax/zmxread.py relates OpticStudio's vignetting factors to CODE V's:
        // vly = vcy + vdy, vuy = vcy − vdy; so VDY = (VLY − VUY)/2 and VCY = (VLY + VUY)/2
        // (and the same in x). Section 16's P′y = VDY + Py(1 − VCY) then puts the pupil's upper
        // edge at 1 − VUY and its lower at −(1 − VLY), as RayOptics' Field.apply_vignetting does.
        return Enumerable.Range(0, count).Select(i => new OpticsFieldSettings
        {
            X = At("X", i, 0), Y = At("Y", i, 0), Weight = At("WTF", i, 1),
            DecenterX = (At("VLX", i, 0) - At("VUX", i, 0)) / 2, CompressionX = (At("VLX", i, 0) + At("VUX", i, 0)) / 2,
            DecenterY = (At("VLY", i, 0) - At("VUY", i, 0)) / 2, CompressionY = (At("VLY", i, 0) + At("VUY", i, 0)) / 2,
        }).ToList();
    }

    private static List<OpticsWavelengthSettings> Wavelengths(Reader r, out int primary)
    {
        var st = r.St;
        primary = 1;
        if (r.Wavelengths is not { Length: > 0 } wl)
        {
            st.Problem(0, "The file gives no wavelengths (WL).");
            return new List<OpticsWavelengthSettings>();
        }
        if (r.WavelengthWeights is { } w && w.Length != wl.Length) st.Problem(0, $"WTW: {w.Length} weights for {wl.Length} wavelengths.");
        if (r.Reference is int reference)
        {
            if (reference < 1 || reference > wl.Length) st.Problem(r.ReferenceLine, $"REF {reference}: the reference wavelength must be 1 to {wl.Length}.");
            else primary = reference;
        }
        else if (wl.Length > 1)
            st.Notes.Add("The file has no REF; the reference wavelength is the first (both readers' default: RayOptics WvlSpec ref_wl=0, Optiland primary_index 0).");
        return wl.Select((um, i) => new OpticsWavelengthSettings
        {
            Micrometers = um, Weight = r.WavelengthWeights is { } ww && i < ww.Length ? ww[i] : 1,
        }).ToList();
    }
}
