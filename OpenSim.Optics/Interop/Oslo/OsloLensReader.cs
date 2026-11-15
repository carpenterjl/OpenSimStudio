using System.Globalization;
using System.Text.RegularExpressions;
using OpenSim.Core.Model;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Interop.Oslo;

/// <summary>
/// Reads the text of an OSLO lens file (.len) into the saved optical model
/// (<see cref="OpticsSettings"/>), plan item OP-95, docs/Optics-Conventions.md section 54.
/// <para>
/// The command meanings come from Optiland 0.6.3 (MIT), <c>optiland/fileio/oslo/</c>, read as
/// documentation: <c>reader/parser.py</c> (<c>OsloDataParser</c>), <c>reader/converter.py</c>,
/// <c>reader/coordinates.py</c>, <c>reader/geometry.py</c>, <c>reader/pickups.py</c>,
/// <c>constants.py</c> and <c>syntax.py</c>. Optiland cites the OSLO Program Reference and Optics
/// Reference (Lambda Research) for each; those manuals were not opened here, so each conversion
/// names the Optiland function and the page it cites.
/// </para>
/// <para>
/// <b>Conventions converted, not copied.</b> OSLO's tilts TLA and TLB turn the other way from
/// this program's: Optiland <c>coordinates.py</c>, <c>surface_coordinates</c>, "OSLO rotations are
/// intrinsic, about −x, −y and +z. DT=1 translates then rotates XYZ; DT=−1 rotates ZYX then
/// translates" (citing Optics Reference pp. 142–145 and Program Reference pp. 65–68). Section 4's
/// tilts are right-handed about the axes as the earlier tilts left them, so a surface decenter is
/// saved as TiltX = −TLA, TiltY = −TLB, TiltZ = TLC, DT 1 as "decenter then tilt" and DT −1 as
/// "tilt then decenter" (Optiland applies the decenter along the tilted axes then, the same as
/// section 4). A decenter moves every later surface (a "before" tilt); RCO on the surface itself
/// returns to the frame before the decenter (the "after" mode "reverse this surface"); BEN on a
/// mirror turns the axis again by the same single-axis tilt (explicit "after" tilts).
/// </para>
/// <para>
/// Every command is read, read and deliberately not used (named in the notes), or unsupported.
/// Unsupported content fails the import with every problem listed (line and surface), unless
/// <see cref="ZemaxImportOptions.UnsupportedAsWarnings"/> lists them instead.
/// </para>
/// </summary>
public static class OsloLensReader
{
    public const string Extension = ".len";

    /// <summary>OSLO's default wavelengths in micrometers, used when the file gives none:
    /// Optiland constants.py <c>DEFAULT_WAVELENGTHS_UM</c>, citing OSLO Program Reference p. 123,
    /// "Wavelength".</summary>
    public static IReadOnlyList<double> DefaultWavelengths { get; } = new[] { 0.58756, 0.48613, 0.65627 };

    /// <summary>Object distances at or above this are infinite: Optiland constants.py
    /// <c>OBJECT_INFINITY_THRESHOLD</c>, citing OSLO Optics Reference p. 151.</summary>
    public const double ObjectInfinity = 1e8;

    /// <summary>Other thicknesses at or above this are OSLO's infinite sentinel: Optiland
    /// constants.py <c>THICKNESS_INFINITY_THRESHOLD</c>.</summary>
    public const double ThicknessInfinity = 9.9e9;

    /// <summary>Commands read and deliberately not used: drawing-only data (Optiland parser.py
    /// <c>parse</c>: "Drawing-only data, without optical effects") and others named.</summary>
    public static IReadOnlyDictionary<string, string> Ignored { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DRW"] = "drawing data", ["LDP"] = "drawing data", ["CBK"] = "drawing data", ["ELMDF1"] = "drawing data",
        ["ELMDF2"] = "drawing data", ["BDI"] = "drawing data", ["BDD"] = "drawing data", ["VX"] = "drawing data",
        ["PF"] = "drawing data", ["LMN"] = "drawing data", ["LME"] = "drawing data",
        ["TCE"] = "the thermal expansion coefficient; its unit is not given by the reader",
        ["DES"] = "a designer text",
    };

    private sealed class Pickup
    {
        public required string Kind;
        public required string[] Args;
        public int Line;
    }

    private sealed class Surf
    {
        public int Index;
        public int Line;
        public double? Rd;              // null: not written; infinity: a plane
        public double Cc;
        public readonly SortedDictionary<int, double> Asphere = new();
        public double? Th;
        public string Medium = "AIR";   // AIR, RFL or GLA
        public string[]? GlassArgs;
        public int GlassLine;
        public double? Ap;
        public bool ApChecked;
        public bool Ast;
        public string? Note;
        public double Dcx, Dcy, Dcz, Tla, Tlb, Tlc, Tox, Toy, Toz;
        public int Dt = 1;
        public int DecenterLine;
        public bool Ben;
        public int? Rco;
        public int RcoLine;
        public int? Gc;
        public readonly List<Pickup> Pickups = new();
        public readonly Dictionary<string, (string Command, int Line)> Solves = new(StringComparer.Ordinal);   // "CV"/"TH"
        public bool HasDecenter => Dcx != 0 || Dcy != 0 || Dcz != 0 || Tla != 0 || Tlb != 0 || Tlc != 0 || Tox != 0 || Toy != 0 || Toz != 0;
        public string At => $"surface {Index}, ";
    }

    private sealed class Reader
    {
        public required LensFileState St;
        public required IReadOnlyList<OpticalCatalog>? Catalogs;
        public bool SeenLen;
        public string? Title;
        public int Count = -1;
        public int Current;
        public readonly SortedDictionary<int, Surf> Surfaces = new();
        public bool Ended;
        public bool FieldTable;
        public bool InFooterIgnore;
        public string? ApertureCommand;
        public double ApertureValue;
        public int ApertureLine;
        public string? FieldCommand;
        public double FieldValue;
        public int FieldLine;
        public readonly SortedDictionary<int, (int Line, double Y, double X, double Weight, double Vy, double Vx)> FieldRows = new();
        public double? Unit;
        public int UnitLine;
        public List<double> Wavelengths = DefaultWavelengths.ToList();
        public bool WavelengthsGiven;
        public List<double> Weights = new();
        public bool Telecentric;
        public bool ApertureCheck = true;
        public readonly List<string> NoteLines = new();

        public Surf Surface
        {
            get
            {
                if (!Surfaces.TryGetValue(Current, out var s)) Surfaces[Current] = s = new Surf { Index = Current };
                return s;
            }
        }
    }

    /// <summary>
    /// The optical model of a lens file's text. <paramref name="catalogs"/> are the glass catalogs
    /// glass names are checked against (null skips the check, with a note). Throws
    /// <see cref="ZemaxFileException"/> listing every problem in the strict mode.
    /// </summary>
    public static ZemaxImportResult Read(string text, IReadOnlyList<OpticalCatalog>? catalogs, ZemaxImportOptions? options = null, string encoding = "text")
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new ZemaxImportOptions();
        var r = new Reader { St = new LensFileState(options), Catalogs = catalogs };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            List<string> statements;
            try { statements = Statements(lines[i]); }
            catch (FormatException ex) { r.St.Problem(i + 1, ex.Message); continue; }
            foreach (var statement in statements)
            {
                var tokens = Tokenize(statement);
                if (tokens.Length == 0) continue;
                tokens[0] = tokens[0].ToUpperInvariant();
                try { Command(r, i + 1, tokens); }
                catch (FormatException ex) { r.St.Problem(i + 1, $"{tokens[0]}: {ex.Message}"); }
            }
        }
        var optics = Finish(r);
        if (r.St.Problems.Count > 0 && !options.UnsupportedAsWarnings)
            throw new ZemaxFileException("The OSLO lens file cannot be read as it stands (open it with unsupported content listed as warnings to read the rest):", r.St.Problems);
        return new ZemaxImportResult(optics, options.UnsupportedAsWarnings ? r.St.Problems.ToArray() : Array.Empty<string>(),
            r.St.Notes.ToArray(), Array.Empty<OpticalCatalog>(), encoding);
    }

    // ------------------------------------------------------------------ the text

    /// <summary>Optiland parser.py <c>_statements</c>: ";" separates commands and "//" starts a
    /// comment, both outside double-quoted text, in which a backslash escapes the next character.</summary>
    internal static List<string> Statements(string line)
    {
        var result = new List<string>();
        int start = 0;
        bool quoted = false, escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quoted) escaped = true;
            else if (c == '"') quoted = !quoted;
            else if (!quoted)
            {
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { line = line[..i]; break; }
                if (c == ';') { result.Add(line[start..i]); start = i + 1; }
            }
        }
        if (quoted) throw new FormatException("an unterminated quoted string.");
        result.Add(line[start..]);
        return result;
    }

    // Optiland syntax.py tokenize: blanks or commas separate tokens; a quoted string is one.
    private static readonly Regex TokenPattern = new("\"(?:\\\\.|[^\"\\\\])*\"|[^\\s,]+", RegexOptions.CultureInvariant);

    private static string[] Tokenize(string statement) => TokenPattern.Matches(statement).Select(m => m.Value).ToArray();

    /// <summary>Optiland syntax.py <c>decode_text</c>: one pair of quotes removed, \" and \\ decoded.</summary>
    private static string Text(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return Regex.Replace(value, "\\\\([\"\\\\])", "$1");
    }

    // ------------------------------------------------------------------ one command

    private static double Num(string[] t, int i, string what)
    {
        if (i >= t.Length) throw new FormatException($"the {what} (field {i}) is missing.");
        if (!LensFileModel.TryNumber(t[i], out double v) || !double.IsFinite(v)) throw new FormatException($"the {what} '{t[i]}' (field {i}) is not a finite number.");
        return v;
    }

    private static int Whole(string[] t, int i, string what)
    {
        double v = Num(t, i, what);
        if (Math.Round(v) != v || Math.Abs(v) > 1e9) throw new FormatException($"the {what} '{t[i]}' is not a whole number.");
        return (int)v;
    }

    private static void One(string[] t)
    {
        if (t.Length != 2) throw new FormatException("expects exactly one value.");
    }

    private static readonly Regex Indexed = new("^(WV|WW|SNO|AS)([0-9]+)$", RegexOptions.CultureInvariant);

    private static void Command(Reader r, int n, string[] t)
    {
        var st = r.St;
        string k = t[0];
        if (r.Ended) { Footer(r, n, t); return; }

        if (Ignored.TryGetValue(k, out string? why))
        {
            st.Note(k, $"{k}: not read ({why}).");
            return;
        }
        var indexed = Indexed.Match(k);
        if (indexed.Success)
        {
            int slot = int.Parse(indexed.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (indexed.Groups[1].Value)
            {
                case "SNO":
                    r.NoteLines.Add(Text(string.Join(' ', t.Skip(1))));
                    return;
                case "AS":
                    st.Problem(n, $"{r.Surface.At}{k}: general aspheric coefficients (ASP ASR, ARA, ASX) are not read.");
                    return;
                default:
                    One(t);
                    Spectrum(r, n, indexed.Groups[1].Value == "WV", slot, Num(t, 1, "value"));
                    return;
            }
        }

        switch (k)
        {
            case "LEN":
                // Optiland parser.py _read_len: LEN NEW "name" scaling surface-count.
                if (r.SeenLen || t.Length != 5 || !string.Equals(t[1], "NEW", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("expects NEW \"name\" scaling surface-count, once.");
                r.SeenLen = true;
                r.Title = Text(t[2]);
                r.Count = Whole(t, 4, "surface count");
                if (r.Count < 2) throw new FormatException("a lens needs at least one surface between the object and the image.");
                if (Num(t, 3, "scaling") != 1) st.Note("LEN-scaling", $"LEN: the scaling value {t[3]} is not read.");
                r.Surface.Line = n;
                return;
            case "EBR": case "FNO": case "NAO": case "NAP": case "PUK":
                One(t);
                r.ApertureCommand = k;
                r.ApertureValue = Num(t, 1, "aperture value");
                r.ApertureLine = n;
                if (k != "PUK" && r.ApertureValue <= 0) throw new FormatException("the aperture value must be positive.");
                return;
            case "OBH": case "ANG": case "GIH":
                One(t);
                r.FieldCommand = k;
                r.FieldValue = Num(t, 1, "field value");
                r.FieldLine = n;
                return;
            case "UNI":
                One(t);
                r.Unit = Num(t, 1, "unit");
                r.UnitLine = n;
                return;
            case "WV": case "WW":
                Spectrum(r, n, k == "WV", 0, double.NaN, Enumerable.Range(1, t.Length - 1).Select(i => Num(t, i, "value")).ToList());
                return;
            case "TELE":
            {
                string v = t.Length > 1 ? t[1].ToUpperInvariant() : "";
                if (v is not ("ON" or "OFF" or "1" or "0")) throw new FormatException("expects ON or OFF.");
                r.Telecentric = v is "ON" or "1";
                return;
            }
            case "APCK":
            {
                string v = t.Length > 1 ? t[1].ToUpperInvariant() : "";
                if (v is not ("ON" or "OFF" or "1" or "0")) throw new FormatException("expects ON or OFF.");
                r.ApertureCheck = v is "ON" or "1";
                return;
            }
            case "LMO":
                if (t.Length > 1 && t[1].ToUpperInvariant() is "EGR" or "ELE") { st.Note("LMO", "LMO EGR/ELE: element group markers, not read."); return; }
                st.Problem(n, $"LMO {(t.Length > 1 ? t[1] : "")}: non-sequential groups are not read.");
                return;
            case "NXT":
                if (r.Count >= 0 && r.Current + 1 > r.Count) throw new FormatException("NXT goes past the surface count of the LEN line.");
                _ = r.Surface;
                r.Current++;
                _ = r.Surface;
                r.Surface.Line = n;
                return;
            case "GTO":
                st.Problem(n, "GTO: surfaces entered out of order are not read; only NXT is.");
                return;
            case "END":
                if (t.Length == 2 && Whole(t, 1, "surface count") != r.Count) throw new FormatException("the END surface count differs from the LEN line's.");
                _ = r.Surface;
                r.Ended = true;
                return;
        }

        if (!r.SeenLen) { st.Problem(n, $"{k}: a command before LEN NEW."); }
        var s = r.Surface;
        string at = s.At;
        switch (k)
        {
            case "AIR": case "AIF":
                s.Medium = "AIR"; s.GlassArgs = null;
                ClearPickups(s, "GLA");
                return;
            case "RFL": case "RFH":
                s.Medium = "RFL"; s.GlassArgs = null;
                ClearPickups(s, "GLA");
                return;
            case "GLA": case "GLF":
                if (t.Length < 2) throw new FormatException("no glass.");
                s.Medium = "GLA"; s.GlassArgs = t.Skip(1).ToArray(); s.GlassLine = n;
                ClearPickups(s, "GLA");
                return;
            case "RD": case "RDF":
            {
                One(t);
                double v = Num(t, 1, "radius");
                s.Rd = v == 0 ? double.PositiveInfinity : v;
                ClearPickups(s, "CV"); s.Solves.Remove("CV");
                return;
            }
            case "CV": case "CVF":
            {
                One(t);
                double v = Num(t, 1, "curvature");
                s.Rd = v == 0 ? double.PositiveInfinity : 1 / v;
                ClearPickups(s, "CV"); s.Solves.Remove("CV");
                return;
            }
            case "TH": case "THF":
                One(t);
                s.Th = Num(t, 1, "thickness");
                ClearPickups(s, "TH"); s.Solves.Remove("TH");
                return;
            case "CC":
                One(t);
                s.Cc = Num(t, 1, "conic constant");
                return;
            // Optiland geometry.py surface_geometry: AD, AE, AF, AG are the coefficients on r^4,
            // r^6, r^8, r^10 (citing Program Reference pp. 70–74); the Even Asphere's parameter n
            // is the coefficient on r^(2n).
            case "AD": case "AE": case "AF": case "AG":
            {
                One(t);
                double v = Num(t, 1, "aspheric coefficient");
                int p = "DEFG".IndexOf(k[1]) + 2;
                if (v != 0) s.Asphere[p] = v; else s.Asphere.Remove(p);
                return;
            }
            case "ASP":
            {
                string kind = t.Length > 1 ? t[1].ToUpperInvariant() : "";
                if (kind is "0" or "ADO") return;   // the AD … AG form (Optiland _read_asp)
                st.Problem(n, $"{at}ASP {kind}: the general asphere forms (ASR, ARA, ASX) are not read.");
                return;
            }
            case "AP": case "APF":
            {
                bool flag = t.Length > 1 && t[1].ToUpperInvariant() is "CHK" or "UNC";
                int index = flag ? 2 : 1;
                if (t.Length != index + 1) throw new FormatException("expects an optional CHK or UNC and one radius.");
                double v = Num(t, index, "aperture radius");
                if (v < 0) throw new FormatException("the aperture radius must not be negative.");
                s.Ap = v;
                s.ApChecked = flag && t[1].ToUpperInvariant() == "CHK";
                ClearPickups(s, "AP");
                return;
            }
            case "AST":
                foreach (var other in r.Surfaces.Values) other.Ast = false;
                s.Ast = true;
                return;
            case "NOT":
                s.Note = Text(string.Join(' ', t.Skip(1)));
                return;
            case "DCX": case "DCY": case "DCZ": case "TLA": case "TLB": case "TLC": case "TOX": case "TOY": case "TOZ":
            {
                One(t);
                double v = Num(t, 1, "value");
                switch (k)
                {
                    case "DCX": s.Dcx = v; break;
                    case "DCY": s.Dcy = v; break;
                    case "DCZ": s.Dcz = v; break;
                    case "TLA": s.Tla = v; break;
                    case "TLB": s.Tlb = v; break;
                    case "TLC": s.Tlc = v; break;
                    case "TOX": s.Tox = v; break;
                    case "TOY": s.Toy = v; break;
                    default: s.Toz = v; break;
                }
                s.DecenterLine = n;
                return;
            }
            case "DT":
            {
                One(t);
                int v = Whole(t, 1, "order");
                if (v is not (1 or -1)) throw new FormatException("DT must be 1 or −1.");
                s.Dt = v;
                return;
            }
            case "BEN":
                s.Ben = true;
                return;
            case "RCO":
                // Optiland parser.py _read_return: no value, or 0, is this surface.
                s.Rco = t.Length > 1 ? (Whole(t, 1, "surface") is int rv && rv != 0 ? rv : s.Index) : s.Index;
                s.RcoLine = n;
                return;
            case "GC":
                One(t);
                s.Gc = Whole(t, 1, "surface");
                st.Problem(n, $"{at}GC: a global coordinate reference is not read.");
                return;
            case "PY": case "PYC": case "EC":
                One(t);
                Num(t, 1, "solve value");
                ClearPickups(s, "TH");
                s.Solves["TH"] = (k, n);
                return;
            case "PU": case "PUC":
                One(t);
                Num(t, 1, "solve value");
                ClearPickups(s, "CV");
                s.Solves["CV"] = (k, n);
                return;
            case "PK":
            {
                if (t.Length < 3) throw new FormatException("PK needs a pickup type and a source surface.");
                string kind = t[1].ToUpperInvariant();
                string family = Family(kind) ?? "";
                if (family is "" or "TD" or "LN")
                {
                    st.Problem(n, $"{at}PK {kind}: this pickup is not read (CV, CVM, TH, THM, GLA and AP are).");
                    return;
                }
                int expected = family is "CV" or "TH" ? 4 : 3;
                if (t.Length != expected && t.Length != 3) throw new FormatException($"PK {kind} has the wrong number of fields.");
                ClearPickups(s, family);
                if (family == "CV") s.Solves.Remove("CV");
                if (family == "TH") s.Solves.Remove("TH");
                s.Pickups.Add(new Pickup { Kind = kind, Args = t.Skip(2).ToArray(), Line = n });
                return;
            }
            // Deletions (Optiland parser.py _read_delete).
            case "ATD":
                s.Cc = 0; s.Asphere.Clear();
                return;
            case "TDD":
                s.Dcx = s.Dcy = s.Dcz = s.Tla = s.Tlb = s.Tlc = s.Tox = s.Toy = s.Toz = 0;
                s.Dt = 1; s.Gc = null; s.Rco = null; s.Ben = false;
                ClearPickups(s, "TD");
                return;
            case "APD":
                s.Ap = null; s.ApChecked = false;
                ClearPickups(s, "AP");
                return;
            case "CSD":
                ClearPickups(s, "CV"); s.Solves.Remove("CV");
                return;
            case "TSD":
                ClearPickups(s, "TH"); s.Solves.Remove("TH");
                return;
            case "RCD":
                s.Rco = null;
                return;
            case "BED":
                s.Ben = false;
                return;
            case "GCD":
                s.Gc = null;
                return;
            case "CXD": case "PFD":
                return;   // nothing read to delete
            case "CVX": case "RDX":
                st.Problem(n, $"{at}{k}: an x curvature (a toric surface) is not read.");
                return;
            case "PFL": case "PFM":
                st.Problem(n, $"{at}{k}: OSLO's perfect lens is not read.");
                return;
            case "GSP": case "GOR":
                st.Problem(n, $"{at}{k}: a grating is not read.");
                return;
        }
        st.Problem(n, $"{at}{k}: command not read by this build.");
    }

    /// <summary>Optiland parser.py <c>_PICKUP_FAMILIES</c>.</summary>
    private static string? Family(string kind) => kind switch
    {
        "CV" or "CVM" => "CV",
        "TH" or "THM" => "TH",
        "LN" or "LNM" => "LN",
        "AP" => "AP",
        "GLA" => "GLA",
        "TD" or "TDM" => "TD",
        _ => null,
    };

    private static void ClearPickups(Surf s, string family) => s.Pickups.RemoveAll(p => Family(p.Kind) == family);

    /// <summary>WV/WW replace the list; WVn/WWn set slot n (Optiland parser.py <c>_read_spectrum</c>).</summary>
    private static void Spectrum(Reader r, int n, bool wavelength, int slot, double value, List<double>? all = null)
    {
        if (all is not null)
        {
            if (all.Count == 0 || all.Any(v => wavelength ? v <= 0 : v < 0)) throw new FormatException("wavelengths must be positive and weights not negative.");
            if (wavelength) { r.Wavelengths = all; r.WavelengthsGiven = true; }
            else r.Weights = all;
            return;
        }
        if (wavelength ? value <= 0 : value < 0) throw new FormatException("wavelengths must be positive and weights not negative.");
        var target = wavelength ? r.Wavelengths : r.Weights;
        int index = slot - 1;
        if (index < 0 || index > 1000) throw new FormatException("the slot number is out of range.");
        if (wavelength && index > target.Count) throw new FormatException($"WV{slot} leaves wavelength {target.Count + 1} undefined.");
        while (target.Count <= index) target.Add(1);
        target[index] = value;
        if (wavelength) r.WavelengthsGiven = true;
    }

    /// <summary>After END: the field-point table RST NEW … END (Optiland parser.py
    /// <c>_read_footer</c>); anything else after the prescription is a problem.</summary>
    private static void Footer(Reader r, int n, string[] t)
    {
        var st = r.St;
        string k = t[0];
        if (r.InFooterIgnore) return;
        if (k == "RST")
        {
            r.FieldTable = t.Length == 2 && t[1].ToUpperInvariant() == "NEW";
            if (!r.FieldTable) st.Problem(n, $"RST {(t.Length > 1 ? t[1] : "")}: only the field table RST NEW is read after END.");
            else r.FieldRows.Clear();
            return;
        }
        if (k == "END") { r.FieldTable = false; return; }
        if (k == "F" && r.FieldTable)
        {
            // F index, then ten values: y, x fractions, three must-be-zero entries, the pupil
            // bounds ymin, ymax, xmin, xmax and the weight (two optional extended flags).
            if (t.Length is not (12 or 14)) throw new FormatException("F needs an index and ten field-table values.");
            int index = Whole(t, 1, "field index");
            var v = Enumerable.Range(2, t.Length - 2).Select(i => Num(t, i, "value")).ToArray();
            if (index < 1 || v[9] < 0) throw new FormatException("F needs a positive index and a weight not negative.");
            if (v[2] != 0 || v[3] != 0 || v[4] != 0 || v.Skip(10).Any(x => x != 0))
            {
                st.Problem(n, $"F {index}: field depth, reference-ray aiming or extended flags are not read.");
                return;
            }
            double ymin = v[5], ymax = v[6], xmin = v[7], xmax = v[8];
            if (!(ymin < ymax && xmin < xmax)) throw new FormatException("the pupil bounds must increase.");
            if (ymin != -ymax || xmin != -xmax)
            {
                st.Problem(n, $"F {index}: asymmetric pupil bounds are not read (Optiland maps only symmetric ones).");
                return;
            }
            r.FieldRows[index] = (n, v[0], v[1], v[9], 1 - ymax, 1 - xmax);
            return;
        }
        if (k is "CFG" or "LEN")
        {
            st.Problem(n, $"{k}: further configurations after the prescription are not read; the rest of the file is left out.");
            r.InFooterIgnore = true;
            return;
        }
        st.Problem(n, $"{k}: a command after the prescription's END is not read.");
    }

    // ------------------------------------------------------------------ the record

    private static OpticsSettings Finish(Reader r)
    {
        var st = r.St;
        if (!r.SeenLen) st.Problem(0, "The file has no LEN NEW line.");
        if (!r.Ended) st.Note("no-end", "The file has no END line; the prescription is read to the end of the file.");
        if (r.Count >= 0 && (r.Surfaces.Count != r.Count + 1 || (r.Surfaces.Count > 0 && r.Surfaces.Keys.Max() != r.Count)))
            st.Problem(0, $"The LEN line declares surfaces 0 to {r.Count}; the file gives {r.Surfaces.Count}.");
        int last = r.Surfaces.Count == 0 ? -1 : r.Surfaces.Keys.Max();

        ResolvePickups(r);

        LensUnit unit = LensUnit.Millimeter;
        if (r.Unit is double u)
        {
            // Optiland parser.py _read_uni: "millimeters per lens unit".
            LensUnit? found = u switch { 1 => LensUnit.Millimeter, 10 => LensUnit.Centimeter, 25.4 => LensUnit.Inch, 1000 => LensUnit.Meter, _ => null };
            if (found is null) st.Problem(r.UnitLine, $"UNI {u.ToString("R", CultureInfo.InvariantCulture)}: only 1 (mm), 10 (cm), 25.4 (inch) and 1000 (m) millimeters per lens unit are read.");
            else unit = found.Value;
        }
        else st.Note("uni", "The file has no UNI; the lens unit is millimeters (Optiland's default, parser.py).");

        var surf0 = r.Surfaces.TryGetValue(0, out var s0) ? s0 : null;
        double objectDistance = surf0?.Th ?? 0;
        bool infinite = Math.Abs(objectDistance) >= ObjectInfinity;
        if (infinite) st.Note("object-infinity", $"The object distance {objectDistance.ToString("R", CultureInfo.InvariantCulture)} (≥ 1e8) is read as infinite (Optiland constants.py, citing OSLO Optics Reference p. 151).");

        var surfaces = new List<LensFileSurface>();
        foreach (var (index, s) in r.Surfaces)
        {
            var b = new LensFileSurface { Number = index, Line = s.Line, Comment = s.Note, Conic = s.Cc, Stop = s.Ast };
            surfaces.Add(b);
            string at = b.At;
            b.Radius = s.Rd is double rd && double.IsFinite(rd) ? rd : null;
            foreach (var (p, v) in s.Asphere) b.Parameters[p] = v;
            if (index == 0)
            {
                b.Infinite = infinite;
                b.Thickness = infinite ? 0 : objectDistance;
            }
            else if (index != last)
            {
                double th = s.Th ?? 0;
                if (Math.Abs(th) >= ThicknessInfinity) st.Problem(s.Line, $"{at}TH {th.ToString("R", CultureInfo.InvariantCulture)}: an infinite thickness after a surface other than the object is not held.");
                b.Thickness = th;
            }
            foreach (var (column, (command, line)) in s.Solves)
            {
                bool written = column == "TH" ? s.Th is not null : s.Rd is not null;
                if (!written) st.Problem(line, $"{at}{command}: the solve sets the {(column == "TH" ? "thickness" : "curvature")}, which the file does not write; solves are not evaluated here.");
                else st.Note("solve", "Solves (PY, PYC, PU, PUC, EC): the value the file writes is used and the solve is not kept (section 54).");
            }
            Medium(r, s, b);
            if (s.Ap is double ap)
            {
                b.SemiDiameter = ap;
                // Optiland converter.py _configure_surface: a checked aperture limits rays unless APCK is off.
                if (s.ApChecked && r.ApertureCheck && ap > 0)
                    b.Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.CircularAperture, MaxRadius = ap };
            }
            Decenter(r, s, b, index == last);
        }

        if (last >= 0 && r.Surfaces[last].Th is double shift && shift != 0)
        {
            if (Math.Abs(shift) >= ThicknessInfinity) st.Problem(r.Surfaces[last].Line, "The image surface's thickness is OSLO's infinite sentinel, which is not a focus shift.");
            else if (surfaces.Count >= 2)
            {
                var before = surfaces[^2];
                before.Thickness += shift;
                st.Notes.Add($"The image surface's thickness {shift.ToString("R", CultureInfo.InvariantCulture)} is a focus shift; it is added to surface {before.Number}'s thickness (Optiland converter.py _apply_image_focus, citing Program Reference pp. 46 and 122).");
            }
        }

        if (!surfaces.Any(s => s.Stop) && surfaces.Count >= 3)
        {
            surfaces[1].Stop = true;
            st.Notes.Add("The file names no stop (AST); surface 1 is the stop (Optiland converter.py: \"OSLO convention: if no stop is specified, it's surface 1\").");
        }
        if (surfaces.Count > 0 && surfaces[0].Stop) st.Problem(0, "The object surface cannot be the stop.");

        var (apertureType, apertureValue) = Aperture(r, infinite);
        var (fieldType, fields) = Fields(r, infinite, objectDistance);
        var wavelengths = r.Wavelengths;
        if (!r.WavelengthsGiven) st.Notes.Add("The file gives no wavelengths; OSLO's defaults 0.58756, 0.48613, 0.65627 µm are used (Optiland constants.py, citing Program Reference p. 123).");
        var weights = r.Weights.Concat(Enumerable.Repeat(1.0, wavelengths.Count)).Take(wavelengths.Count).ToList();
        if (r.Weights.Count > wavelengths.Count) st.Problem(0, $"WW: {r.Weights.Count} weights for {wavelengths.Count} wavelengths.");
        if (weights.Count > 0 && weights[0] <= 0) st.Problem(0, "The primary wavelength's weight (wavelength 1) must be positive (Optiland parser.py).");

        string notes = string.Join(Environment.NewLine, r.NoteLines.Where(x => x.Length > 0));
        var system = new OpticsSystemSettings
        {
            Title = string.IsNullOrEmpty(r.Title) ? null : r.Title,
            Notes = notes.Length == 0 ? null : notes,
            LensUnit = LensFileModel.Unit(unit),
            ApertureType = apertureType is { } at2 ? ApertureTypes.Names.SavedName(at2) : null,
            ApertureValue = apertureValue,
            TelecentricObjectSpace = r.Telecentric,
            FieldType = FieldTypes.Names.SavedName(fieldType),
            Fields = fields,
            // OSLO's primary wavelength is wavelength 1 (Optiland parser.py: "OSLO primary wavelength weight must be positive").
            Wavelengths = wavelengths.Select((w, i) => new OpticsWavelengthSettings { Micrometers = w, Weight = weights[i] }).ToList(),
            PrimaryWavelength = 1,
            TemperatureCelsius = 20,
            PressureAtmospheres = 1,
        };
        st.Note("environment", "The temperature and pressure are this program's defaults (20 °C, 1 atm).");
        return new OpticsSettings
        {
            Mode = OpticsModes.SavedName(OpticsMode.Sequential),
            SystemData = system,
            Materials = st.UsedCatalogs.Count == 0 ? null : new OpticsMaterialsSettings { ActiveCatalogs = st.UsedCatalogs.ToList() },
            Surfaces = surfaces.Select(LensFileModel.Surface).ToList(),
        };
    }

    /// <summary>
    /// Pickups resolved to values in surface order (Optiland pickups.py <c>resolve_pickups</c>):
    /// a negative source is relative to the surface; CV copies the source's curvature (plus an
    /// additive offset), conic and aspheric coefficients, CVM the same with the curvature and
    /// coefficients negated; TH/THM the thickness (negated for THM) plus an offset; GLA the medium
    /// (not a mirror); AP the aperture radius. The link itself is not kept (noted).
    /// </summary>
    private static void ResolvePickups(Reader r)
    {
        foreach (var (index, s) in r.Surfaces)
            foreach (var p in s.Pickups)
            {
                string at = s.At;
                int src;
                try { src = Whole(p.Args, 0, "source surface"); }
                catch (FormatException ex) { r.St.Problem(p.Line, $"{at}PK {p.Kind}: {ex.Message}"); continue; }
                if (src < 0) src += index;
                if (src < 0 || src >= index || !r.Surfaces.TryGetValue(src, out var o))
                {
                    r.St.Problem(p.Line, $"{at}PK {p.Kind}: the source surface must be an earlier surface.");
                    continue;
                }
                double sign = p.Kind is "CVM" or "THM" ? -1 : 1;
                double offset = p.Args.Length > 1 && LensFileModel.TryNumber(p.Args[1], out double off) ? off : 0;
                switch (Family(p.Kind))
                {
                    case "CV":
                    {
                        double c = (o.Rd is double rd && double.IsFinite(rd) ? sign / rd : 0) + offset;
                        s.Rd = c == 0 ? double.PositiveInfinity : 1 / c;
                        s.Cc = o.Cc;
                        s.Asphere.Clear();
                        foreach (var (k, v) in o.Asphere) s.Asphere[k] = sign * v;
                        break;
                    }
                    case "TH":
                        s.Th = sign * (o.Th ?? 0) + offset;
                        break;
                    case "GLA":
                        if (o.Medium == "RFL") { r.St.Problem(p.Line, $"{at}PK GLA: a reflector cannot be picked up."); continue; }
                        s.Medium = o.Medium; s.GlassArgs = o.GlassArgs; s.GlassLine = o.GlassLine;
                        break;
                    case "AP":
                        s.Ap = o.Ap;
                        break;
                }
                r.St.Note("pickup", "Pickups (PK) are read as the values they give; the link itself is not kept (section 54).");
            }
    }

    /// <summary>The medium after a surface: AIR, RFL (a mirror), GLA name (a catalog glass),
    /// GLA n … (indices at the system wavelengths: all equal is a constant index), GLA MOD
    /// (refused). Optiland converter.py <c>_resolve_material</c>.</summary>
    private static void Medium(Reader r, Surf s, LensFileSurface b)
    {
        string at = b.At;
        if (s.Medium == "RFL") { b.Material = SavedSurfaces.Mirror; return; }
        if (s.Medium != "GLA" || s.GlassArgs is null) return;
        var parts = s.GlassArgs;
        if (parts[0].ToUpperInvariant() == "MOD")
        {
            r.St.Problem(s.GlassLine, $"{at}GLA MOD: a model glass given by index samples is not read.");
            return;
        }
        string? name = null;
        int from = 0;
        if (!LensFileModel.TryNumber(parts[0], out _)) { name = Text(parts[0]); from = 1; }
        if (from == parts.Length)
        {
            b.Material = LensFileModel.ResolveGlass(r.St, name!, null, s.GlassLine, at, r.Catalogs, hyphenCandidates: true);
            return;
        }
        var indices = new List<double>();
        foreach (var x in parts.Skip(from))
        {
            if (!LensFileModel.TryNumber(x, out double v) || v <= 0) { r.St.Problem(s.GlassLine, $"{at}GLA: the index '{x}' is not a positive number."); return; }
            indices.Add(v);
        }
        if (indices.Distinct().Count() == 1)
        {
            b.Index = indices[0];
            if (name is not null) r.St.Notes.Add($"{Capital(at)}the glass '{name}' is given by its index {indices[0].ToString("R", CultureInfo.InvariantCulture)}, read as a constant index.");
            return;
        }
        r.St.Problem(s.GlassLine, $"{at}GLA: indices tabulated at the wavelengths ({string.Join(", ", indices.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}) are not held by a surface here.");
    }

    private static string Capital(string at) => at.Length == 0 ? "" : char.ToUpperInvariant(at[0]) + at[1..];

    private static double Neg(double v) => v == 0 ? 0 : -v;

    /// <summary>A surface decenter in this program's convention (see the class remarks).</summary>
    private static void Decenter(Reader r, Surf s, LensFileSurface b, bool isImage)
    {
        var st = r.St;
        string at = b.At;
        bool any = s.HasDecenter || s.Ben || s.Rco is not null;
        if (!any) return;
        if (s.Index == 0) { st.Problem(s.DecenterLine > 0 ? s.DecenterLine : s.Line, $"{at}a decenter of the object surface is not read (section 15 refuses a tilted object)."); return; }
        if (s.Dcz != 0) st.Problem(s.DecenterLine, $"{at}DCZ: a z decenter is not held by a surface tilt and decenter (section 4 has no z shift).");
        if (s.Tox != 0 || s.Toy != 0 || s.Toz != 0) st.Problem(s.DecenterLine, $"{at}TOX/TOY/TOZ: a tilt about an offset point is not read.");
        var before = new OpticsTiltDecenterSettings
        {
            DecenterX = s.Dcx, DecenterY = s.Dcy, TiltX = Neg(s.Tla), TiltY = Neg(s.Tlb), TiltZ = s.Tlc,
            Order = s.Dt == 1 ? null : LensFileModel.TiltThenDecenter,
        };
        bool tilted = s.Dcx != 0 || s.Dcy != 0 || s.Tla != 0 || s.Tlb != 0 || s.Tlc != 0;
        b.TiltBefore = tilted ? before : null;
        if (s.Rco is int rco)
        {
            int target = rco < 0 ? s.Index + rco : rco;
            if (target != s.Index) st.Problem(s.RcoLine, $"{at}RCO {rco}: a return to another surface's coordinates is not read; only a return on the surface itself is.");
            else if (isImage) st.Problem(s.RcoLine, $"{at}RCO: the image surface has no tilt after it.");
            else if (s.Ben) st.Problem(s.RcoLine, $"{at}RCO with BEN: one is read.");
            else if (tilted) b.TiltAfter = new OpticsTiltDecenterSettings { Mode = LensFileModel.ReverseThisSurface };
        }
        if (s.Ben && s.Rco is null)
        {
            // Optiland coordinates.py: BEN supports single-axis tilts of a reflecting surface; the
            // axis after the mirror is turned again by the same rotation, without the decenter.
            if ((s.Tla != 0 && s.Tlb != 0) || s.Tlc != 0) st.Problem(s.DecenterLine > 0 ? s.DecenterLine : s.Line, $"{at}BEN: only a tilt about one of x and y is read (Optiland coordinates.py).");
            else if (s.Medium != "RFL") st.Problem(s.Line, $"{at}BEN: the surface must be a mirror (RFL).");
            else if (isImage) st.Problem(s.Line, $"{at}BEN: the image surface has no tilt after it.");
            else if (s.Tla != 0 || s.Tlb != 0) b.TiltAfter = new OpticsTiltDecenterSettings { TiltX = before.TiltX, TiltY = before.TiltY };
        }
    }

    /// <summary>
    /// The aperture (Optiland converter.py <c>_configure_aperture</c>): EBR is the beam radius
    /// at surface 1 (Program Reference p. 120), the entrance pupil radius for an object at
    /// infinity, so the entrance pupil diameter is 2·EBR (refused for a finite object, where that
    /// plane need not be the pupil); FNO sets the image-space reduced slope n′u′ = 1/(2·FNO) and NAP
    /// sets n′u′ = NAP, so both are this program's paraxial working F/# (1/(2n′u′)): FNO as given,
    /// NAP as 1/(2·NAP); NAO is the object-space NA n·sin θ (Program Reference p. 120), for a
    /// finite object. PUK needs the image index and is refused.
    /// </summary>
    private static (ApertureType?, double) Aperture(Reader r, bool infinite)
    {
        var st = r.St;
        switch (r.ApertureCommand)
        {
            case null:
                st.Problem(0, "The file gives no aperture (EBR, FNO, NAO or NAP).");
                return (null, 10);
            case "EBR":
                if (!infinite) { st.Problem(r.ApertureLine, "EBR: the beam radius at surface 1 is the entrance pupil's only for an object at infinity; with a finite object it is not read."); return (null, 10); }
                return (ApertureType.EntrancePupilDiameter, 2 * r.ApertureValue);
            case "FNO":
                return (ApertureType.ParaxialWorkingFNumber, r.ApertureValue);
            case "NAP":
                st.Notes.Add($"NAP {r.ApertureValue.ToString("R", CultureInfo.InvariantCulture)} is read as the paraxial working F/# 1/(2·NAP).");
                return (ApertureType.ParaxialWorkingFNumber, 1 / (2 * r.ApertureValue));
            case "NAO":
                if (infinite) { st.Problem(r.ApertureLine, "NAO: an object-space NA needs a finite object (Optiland validation.py)."); return (null, 10); }
                return (ApertureType.ObjectSpaceNumericalAperture, r.ApertureValue);
            default:
                st.Problem(r.ApertureLine, "PUK: the image-space paraxial slope is not read (it needs the image-space index).");
                return (null, 10);
        }
    }

    /// <summary>
    /// The fields (Optiland converter.py <c>_configure_fields</c> and parser.py
    /// <c>_read_footer</c>). OSLO gives one reference field (ANG a half-angle in degrees, OBH an
    /// object height, GIH a Gaussian, i.e. paraxial, image height) and optionally a field table of
    /// fractions of it: a height fraction f is f times the reference; an angle fraction f is
    /// atan(f·tan(reference)). OBH with the object at infinity is the angle atan(OBH/distance)
    /// and, like ANG, is taken positive (Optiland: "OSLO OBH sign convention: negative means
    /// below axis - take abs()", "ANG is always positive in OSLO"). Without a field table the
    /// fields are the axis and the reference (noted).
    /// </summary>
    private static (FieldType, List<OpticsFieldSettings>) Fields(Reader r, bool infinite, double distance)
    {
        var st = r.St;
        if (r.FieldCommand is null)
        {
            st.Notes.Add("The file gives no field (ANG, OBH or GIH); one on-axis field is used.");
            return (FieldType.Angle, new List<OpticsFieldSettings> { new() });
        }
        FieldType type;
        double reference = r.FieldValue;
        switch (r.FieldCommand)
        {
            case "ANG":
                type = FieldType.Angle;
                reference = Math.Abs(reference);
                break;
            case "OBH" when infinite:
                type = FieldType.Angle;
                reference = Math.Abs(Math.Atan(reference / distance) * 180 / Math.PI);
                st.Notes.Add($"OBH {r.FieldValue.ToString("R", CultureInfo.InvariantCulture)} with the object at infinity is read as the field angle atan(OBH/distance) = {reference.ToString("R", CultureInfo.InvariantCulture)}° (Optiland converter.py).");
                break;
            case "OBH":
                type = FieldType.ObjectHeight;
                break;
            default:
                type = FieldType.ParaxialImageHeight;
                break;
        }
        if (type == FieldType.Angle && reference >= 90) st.Problem(r.FieldLine, $"{r.FieldCommand}: the reference field angle must be below 90° (Optiland converter.py, citing Program Reference p. 215).");
        double Scale(double f) => type == FieldType.Angle
            ? Math.Atan(f * Math.Tan(reference * Math.PI / 180)) * 180 / Math.PI
            : f * reference;
        if (r.FieldRows.Count > 0)
            return (type, r.FieldRows.Values.Select(row => new OpticsFieldSettings
            {
                Y = row.Y == 0 ? 0 : row.Y == 1 ? reference : Scale(row.Y),
                X = row.X == 0 ? 0 : row.X == 1 ? reference : Scale(row.X),
                Weight = row.Weight, CompressionY = row.Vy, CompressionX = row.Vx,
            }).ToList());
        st.Notes.Add("The file has no field table (RST NEW … F rows); the fields are the axis and the reference field.");
        var list = new List<OpticsFieldSettings> { new() };
        if (reference != 0) list.Add(new OpticsFieldSettings { Y = reference });
        return (type, list);
    }
}
