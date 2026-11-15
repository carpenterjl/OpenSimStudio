using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Interop;

/// <summary>
/// The model-building layer the CODE V (.seq) and OSLO (.len) readers share (plan item OP-95,
/// docs/Optics-Conventions.md section 54): one surface as read, the problem and note lists, glass
/// lookup in the loaded catalogs, and the saved surface record. Lengths stay in the file's lens
/// unit, as the saved records keep them. The OP-94 reader (<see cref="ZemaxLensReader"/>) keeps
/// its own copy of the same steps, unchanged.
/// </summary>
internal sealed class LensFileSurface
{
    public int Number;
    public int Line;
    public string? Comment;
    public double? Radius;
    public double Thickness;
    public bool Infinite;
    public string? Material;
    public double? Index;
    public OpticsModelGlassSettings? ModelGlass;
    public double? SemiDiameter;
    public double Conic;
    public bool Stop;
    public OpticsSurfaceApertureSettings? Aperture;

    /// <summary>Even Asphere parameters by number (parameter n is the coefficient on r^(2n),
    /// SurfaceTypes "Even Asphere"); empty for a Standard surface.</summary>
    public readonly SortedDictionary<int, double> Parameters = new();

    public OpticsTiltDecenterSettings? TiltBefore;
    public OpticsTiltDecenterSettings? TiltAfter;

    public string At => $"surface {Number}, ";
}

/// <summary>The problems (content not read) and notes of one import.</summary>
internal sealed class LensFileState
{
    public LensFileState(ZemaxImportOptions options) => Options = options;

    public ZemaxImportOptions Options { get; }
    public List<string> Problems { get; } = new();
    public List<string> Notes { get; } = new();
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    /// <summary>Catalogs a glass was found in, in the order first used (the record's active list).</summary>
    public List<string> UsedCatalogs { get; } = new();

    public void Problem(int line, string text) => Problems.Add(line > 0 ? $"Line {line}: {text}" : text);

    /// <summary>A note, once per key.</summary>
    public void Note(string key, string text)
    {
        if (_noted.Add(key)) Notes.Add(text);
    }
}

internal static class LensFileModel
{
    /// <summary>A number token (invariant culture; "1e+11", ".05" and "0." read).</summary>
    public static bool TryNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// The value of a decimal token times 10^<paramref name="exponent"/>, rounded once: the token
    /// is read with the exponent added to its text, so "587.6" with −3 gives the double nearest
    /// 0.5876, the same double as the literal 0.5876 (a division by 1000 after reading could be
    /// one unit in the last place away). Used for CODE V's wavelengths in nanometers.
    /// </summary>
    public static bool TryScaledNumber(string token, int exponent, out double value)
    {
        value = 0;
        if (!TryNumber(token, out _)) return false;
        string t = token.Trim();
        int e = t.IndexOfAny(new[] { 'e', 'E' });
        int shift = exponent;
        if (e >= 0)
        {
            if (!int.TryParse(t[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int own)) return false;
            shift += own;
            t = t[..e];
        }
        return double.TryParse($"{t}E{shift.ToString(CultureInfo.InvariantCulture)}", NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Names to try for a glass written without its hyphen: the name as written, then with a
    /// hyphen after the first letter when the name starts with one of the prefix letters CODE V
    /// files drop the hyphen of (N, S, P, Q, E, H, L, M, K, F, G, C) followed by a letter
    /// ("NBK7" → "N-BK7"). Optiland 0.6.3, optiland/fileio/codev/reader/parser.py,
    /// <c>_glass_name_candidates</c> ("CODE V omits hyphens from glass names").
    /// </summary>
    public static IReadOnlyList<string> GlassNameCandidates(string name)
    {
        var list = new List<string> { name };
        if (name.Length > 2 && "NSPQEHLMKFGC".Contains(char.ToUpperInvariant(name[0])) && char.IsLetter(name[1]))
            list.Add($"{name[0]}-{name[1..]}");
        return list;
    }

    /// <summary>
    /// The saved material name ("CATALOG:Glass", the catalog's and the glass's own spelling) of a
    /// glass read from a file, searched in <paramref name="catalog"/> first when the file names
    /// one and then in every loaded catalog, each candidate spelling in turn
    /// (<see cref="GlassNameCandidates"/>). A glass not found is replaced by the import option's
    /// substitution when there is one (noted), and is otherwise a problem naming the line and
    /// keeps its name. With no catalogs the name is kept as written (noted once).
    /// </summary>
    public static string ResolveGlass(LensFileState st, string name, string? catalog, int line, string at,
        IReadOnlyList<OpticalCatalog>? catalogs, bool hyphenCandidates)
    {
        if (catalogs is null)
        {
            st.Note("glass-unchecked", "Glass names were not checked against any catalog; they are kept as written.");
            return catalog is null ? name : $"{catalog}:{name}";
        }
        var candidates = hyphenCandidates ? GlassNameCandidates(name) : new[] { name };
        var named = catalog is null ? null : catalogs.FirstOrDefault(c => string.Equals(c.Name, catalog, StringComparison.OrdinalIgnoreCase));
        if (catalog is not null && named is null)
            st.Note("catalog-missing:" + catalog, $"The glass catalog '{catalog}' a glass names is not loaded; its glasses were looked for in every loaded catalog.");
        if (named is not null)
            foreach (var c in candidates)
                if (named.Find(c) is { } m) return Use(st, named, m, name, at);
        foreach (var c in candidates)
            foreach (var cat in catalogs)
                if (cat.Find(c) is { } m) return Use(st, cat, m, name, at);

        if (st.Options.GlassSubstitutions is { } map
            && map.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is { Value: { } sub })
        {
            foreach (var cat in catalogs)
                if (cat.Find(sub) is { } m)
                {
                    st.Notes.Add($"{Capital(at)}the glass '{name}' is not in the catalogs; replaced by '{sub}' as asked.");
                    return Use(st, cat, m, name, at, note: false);
                }
        }
        st.Problem(line, $"{at}glass {name}{(catalog is null ? "" : $" (catalog {catalog})")}: the glass is not in the catalogs {string.Join(", ", catalogs.Select(c => c.Name))}.");
        return catalog is null ? name : $"{catalog}:{name}";
    }

    private static string Use(LensFileState st, OpticalCatalog cat, OpticalMaterial m, string written, string at, bool note = true)
    {
        if (!st.UsedCatalogs.Contains(cat.Name, StringComparer.OrdinalIgnoreCase)) st.UsedCatalogs.Add(cat.Name);
        if (note && !string.Equals(m.Name, written, StringComparison.OrdinalIgnoreCase))
            st.Notes.Add($"{Capital(at)}the glass '{written}' is read as {cat.Name}'s '{m.Name}'.");
        return $"{cat.Name}:{m.Name}";
    }

    private static string Capital(string at) => at.Length == 0 ? "" : char.ToUpperInvariant(at[0]) + at[1..];

    /// <summary>The saved record of a surface as read.</summary>
    public static OpticsSurfaceSettings Surface(LensFileSurface b) => new()
    {
        Type = b.Parameters.Count > 0 ? "Even Asphere" : null,
        Comment = string.IsNullOrEmpty(b.Comment) ? null : b.Comment,
        Radius = b.Radius,
        Thickness = b.Thickness,
        ThicknessIsInfinite = b.Infinite,
        Material = b.Material,
        Index = b.Index,
        ModelGlass = b.ModelGlass,
        SemiDiameter = b.SemiDiameter,
        Conic = b.Conic,
        IsStop = b.Stop,
        Aperture = b.Aperture,
        Parameters = b.Parameters.Count == 0 ? null : b.Parameters.Select(p => new OpticsSurfaceParameterSettings { Number = p.Key, Value = p.Value }).ToList(),
        TiltBefore = b.TiltBefore,
        TiltAfter = b.TiltAfter,
    };

    /// <summary>The saved name of an "after" mode (null for explicit values, the default).</summary>
    public static string ReverseThisSurface => SavedSurfaces.AfterModeName(TiltAfterKind.ReverseThisSurface);

    /// <summary>The saved name of the order "tilt then decenter" (null is "decenter then tilt").</summary>
    public static string TiltThenDecenter => SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter);

    /// <summary>The saved lens unit's name.</summary>
    public static string Unit(LensUnit unit) => ConventionNames.LensUnits.SavedName(unit);
}
