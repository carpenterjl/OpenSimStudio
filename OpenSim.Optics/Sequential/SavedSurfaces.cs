using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// The saved surfaces of a project or lens file (<see cref="OpticsSurfaceSettings"/>, lens units)
/// to and from the real ray tracer's <see cref="SequentialLens"/> (meters), plan item OP-31;
/// docs/Optics-Conventions.md section 20.
/// <para>
/// Only OpticStudio's Standard surface converts in this build. Every other type (aspheres OP-23,
/// special surfaces OP-27, coordinate breaks OP-21) is saved and edited as its name and
/// numbered parameters, and converting a lens that holds one is refused with a message naming
/// the surface and the type, never traced as something else. Cell flags (variables, solves,
/// pickups) are stored, not evaluated: the values in the cells are what is traced.
/// </para>
/// </summary>
public static class SavedSurfaces
{
    /// <summary>The Standard surface's type name; a null type is this.</summary>
    public const string StandardType = "Standard";

    /// <summary>The material name of a mirror.</summary>
    public const string Mirror = "MIRROR";

    // Column keys, shared by the saved flags, the editor's sheet and the editing tools.
    public const string CommentColumn = "Comment";
    public const string RadiusColumn = "Radius";
    public const string ThicknessColumn = "Thickness";
    public const string MaterialColumn = "Material";
    public const string CoatingColumn = "Coating";
    public const string SemiDiameterColumn = "SemiDiameter";
    public const string ChipZoneColumn = "ChipZone";
    public const string MechanicalSemiDiameterColumn = "MechanicalSemiDiameter";
    public const string ConicColumn = "Conic";
    public const string TceColumn = "Tce";

    /// <summary>The key of parameter column <paramref name="number"/> (1-based): "Par1" …</summary>
    public static string ParameterColumn(int number) => "Par" + number.ToString(CultureInfo.InvariantCulture);

    // Flag kinds and aperture types as saved.
    public const string VariableFlag = "Variable";
    public const string SolveFlag = "Solve";
    public const string PickupFlag = "Pickup";

    public const string CircularAperture = "Circular";
    public const string RectangularAperture = "Rectangular";
    public const string EllipticalAperture = "Elliptical";
    public const string SpiderAperture = "Spider";
    public const string UserDefinedAperture = "UserDefined";
    public const string FloatingAperture = "Floating";

    /// <summary>The saved aperture types, in the order a picker lists them.</summary>
    public static IReadOnlyList<string> ApertureTypes { get; } = new[]
    {
        CircularAperture, RectangularAperture, EllipticalAperture, SpiderAperture, UserDefinedAperture, FloatingAperture
    };

    /// <summary>True when the type name is the Standard surface (null included).</summary>
    public static bool IsStandard(OpticsSurfaceSettings surface) =>
        surface.Type is null || string.Equals(surface.Type, StandardType, StringComparison.Ordinal);

    /// <summary>True when the surface's material is a mirror.</summary>
    public static bool IsMirror(OpticsSurfaceSettings surface) =>
        string.Equals(surface.Material?.Trim(), Mirror, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the medium after the surface is the system's air (no material, index
    /// or model glass).</summary>
    public static bool IsAir(OpticsSurfaceSettings surface) =>
        string.IsNullOrWhiteSpace(surface.Material) && surface.Index is null && surface.ModelGlass is null;

    /// <summary>A plane: no radius, zero, or infinite.</summary>
    public static bool IsPlane(OpticsSurfaceSettings surface) =>
        surface.Radius is not double r || r == 0 || double.IsInfinity(r);

    /// <summary>The value of parameter <paramref name="number"/>, zero when not listed.</summary>
    public static double Parameter(OpticsSurfaceSettings surface, int number) =>
        surface.Parameters?.LastOrDefault(p => p.Number == number)?.Value ?? 0;

    // ------------------------------------------------------------------ to the trace

    /// <summary>
    /// The lens the saved surfaces describe, in meters. Glass names are looked up with
    /// <paramref name="findGlass"/> (null when not found); a model glass is made by
    /// <paramref name="modelGlass"/>, which may be null when the lens has none. Every problem
    /// is collected first and thrown together as one <see cref="ArgumentException"/>, one line a
    /// surface; the lens is then also checked by <see cref="SequentialLens.Validate"/>.
    /// </summary>
    public static SequentialLens ToLens(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensUnit unit,
        Func<string, OpticalMaterial?> findGlass, Func<OpticsModelGlassSettings, OpticalMaterial>? modelGlass = null)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(findGlass);
        double perUnit = OpticsConventions.MetersPer(unit);
        var problems = new List<string>();
        if (surfaces.Count < 3)
            problems.Add("Saved lens: it needs the object surface, at least one surface and the image surface.");
        var result = new SequentialSurface[surfaces.Count];
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j] ?? new OpticsSurfaceSettings();
            string where = Where(j, surfaces.Count);
            if (!IsStandard(s))
            {
                problems.Add($"{where}: the surface type '{s.Type}' is saved with the lens but this build traces only the {StandardType} surface; change the type or remove the surface to trace the lens.");
                continue;
            }
            if (s.Parameters?.FirstOrDefault(p => p.Value != 0) is { } p)
                problems.Add($"{where}: a {StandardType} surface has no parameters, but parameter {p.Number} is {p.Value.ToString("R", CultureInfo.InvariantCulture)}.");
            double? radius = s.Radius;
            if (radius is double rr && double.IsNaN(rr)) problems.Add($"{where}: the radius must be a number.");
            if (!s.ThicknessIsInfinite && !double.IsFinite(s.Thickness)) problems.Add($"{where}: the thickness must be a number.");
            bool mirror = IsMirror(s);
            var medium = Medium(s, mirror, where, findGlass, modelGlass, problems);
            SurfaceAperture? aperture = null;
            if (s.Aperture is { } a)
                aperture = Aperture(a, perUnit, where, problems);
            result[j] = new SequentialSurface
            {
                Curvature = radius is double r ? OpticsConventions.CurvatureFromRadius(r * perUnit) : 0,
                Conic = s.Conic,
                Thickness = s.ThicknessIsInfinite ? double.PositiveInfinity : s.Thickness * perUnit,
                Medium = medium ?? ParaxialMedium.Air,
                IsStop = s.IsStop,
                IsMirror = mirror,
                IsIgnored = s.IsIgnored,
                SkipRaysTo = s.SkipRaysTo,
                Aperture = aperture,
                SemiDiameter = s.SemiDiameter * perUnit,
                MechanicalSemiDiameter = s.MechanicalSemiDiameter * perUnit,
                ChipZone = s.ChipZone * perUnit,
                Coating = string.IsNullOrWhiteSpace(s.Coating) ? null : s.Coating,
                ThermalExpansion = s.Tce,
                Comment = s.Comment ?? "",
            };
        }
        if (problems.Count == 0)
        {
            var lens = new SequentialLens { Surfaces = result };
            problems.AddRange(lens.Validate());
            if (problems.Count == 0) return lens;
        }
        throw new ArgumentException(string.Join(Environment.NewLine, problems));
    }

    private static ParaxialMedium? Medium(OpticsSurfaceSettings s, bool mirror, string where,
        Func<string, OpticalMaterial?> findGlass, Func<OpticsModelGlassSettings, OpticalMaterial>? modelGlass, List<string> problems)
    {
        int given = (string.IsNullOrWhiteSpace(s.Material) ? 0 : 1) + (s.Index is null ? 0 : 1) + (s.ModelGlass is null ? 0 : 1);
        if (given > 1)
        {
            problems.Add($"{where}: the material is given more than one way (a name, an index or a model glass); keep one.");
            return null;
        }
        if (mirror || given == 0) return ParaxialMedium.Air;
        if (s.Index is double n)
        {
            if (!double.IsFinite(n) || !(n > 0)) { problems.Add($"{where}: the index must be a positive number (it is {n})."); return null; }
            return ParaxialMedium.Constant(n);
        }
        if (s.ModelGlass is { } model)
        {
            if (modelGlass is null) { problems.Add($"{where}: a model glass needs the glass catalogs to train on, and none were given."); return null; }
            try { return ParaxialMedium.Of(modelGlass(model)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                problems.Add($"{where}: the model glass ({Format(model)}) could not be made: {ex.Message}");
                return null;
            }
        }
        string name = s.Material!.Trim();
        var glass = findGlass(name);
        if (glass is null)
        {
            problems.Add($"{where}: the glass '{name}' is not in the active catalogs.");
            return null;
        }
        return ParaxialMedium.Of(glass);
    }

    private static SurfaceAperture? Aperture(OpticsSurfaceApertureSettings a, double perUnit, string where, List<string> problems)
    {
        SurfaceAperture? shape = a.Type switch
        {
            CircularAperture => new Sequential.CircularAperture(a.MinRadius * perUnit, a.MaxRadius * perUnit),
            RectangularAperture => new Sequential.RectangularAperture(a.HalfWidthX * perUnit, a.HalfWidthY * perUnit),
            EllipticalAperture => new Sequential.EllipticalAperture(a.HalfWidthX * perUnit, a.HalfWidthY * perUnit),
            SpiderAperture => new Sequential.SpiderAperture(a.ArmWidth * perUnit, a.ArmCount),
            UserDefinedAperture => new PolygonAperture((a.Vertices ?? new()).Select(v => (v.X * perUnit, v.Y * perUnit)).ToArray()),
            FloatingAperture => new Sequential.FloatingAperture(),
            _ => null,
        };
        if (shape is null)
        {
            problems.Add($"{where}: the aperture type '{a.Type}' is not one this version knows ({string.Join(", ", ApertureTypes)}).");
            return null;
        }
        return shape with { DecenterX = a.DecenterX * perUnit, DecenterY = a.DecenterY * perUnit, IsObscuration = a.IsObscuration };
    }

    // ------------------------------------------------------------------ from the trace

    /// <summary>
    /// The saved form of a lens built in code: Standard surfaces only (a lens with a
    /// <see cref="SequentialSurface.Shape"/> is refused), lengths in <paramref name="unit"/>, a
    /// catalog material by "catalog:name" (or its name when it has no catalog). A model glass
    /// cannot be told from another formula glass and is written by name.
    /// </summary>
    public static List<OpticsSurfaceSettings> FromLens(SequentialLens lens, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(lens);
        double perUnit = OpticsConventions.MetersPer(unit);
        var list = new List<OpticsSurfaceSettings>(lens.Surfaces.Count);
        for (int j = 0; j < lens.Surfaces.Count; j++)
        {
            var s = lens.Surfaces[j];
            if (s.Shape is { } shape)
                throw new ArgumentException($"{Where(j, lens.Surfaces.Count)}: the surface type '{shape.TypeName}' cannot be saved by this build; only the {StandardType} surface converts.");
            var m = s.Medium;
            list.Add(new OpticsSurfaceSettings
            {
                Comment = s.Comment.Length > 0 ? s.Comment : null,
                Coating = s.Coating,
                Tce = s.ThermalExpansion,
                Radius = s.Curvature == 0 ? null : 1.0 / s.Curvature / perUnit,
                Thickness = double.IsPositiveInfinity(s.Thickness) ? 0 : s.Thickness / perUnit,
                ThicknessIsInfinite = double.IsPositiveInfinity(s.Thickness),
                Material = s.IsMirror ? Mirror
                    : m.Material is { } glass ? (glass.Catalog.Length > 0 ? $"{glass.Catalog}:{glass.Name}" : glass.Name)
                    : null,
                Index = s.IsMirror || m.Index is not double n || n == 1.0 ? null : n,
                SemiDiameter = s.SemiDiameter / perUnit,
                ChipZone = s.ChipZone / perUnit,
                MechanicalSemiDiameter = s.MechanicalSemiDiameter / perUnit,
                Conic = s.Conic,
                IsStop = s.IsStop,
                IsIgnored = s.IsIgnored,
                SkipRaysTo = s.SkipRaysTo,
                Aperture = s.Aperture is { } a ? FromAperture(a, perUnit) : null,
            });
        }
        return list;
    }

    private static OpticsSurfaceApertureSettings FromAperture(SurfaceAperture a, double perUnit)
    {
        var saved = new OpticsSurfaceApertureSettings
        {
            IsObscuration = a.IsObscuration, DecenterX = a.DecenterX / perUnit, DecenterY = a.DecenterY / perUnit
        };
        return a switch
        {
            Sequential.CircularAperture c => saved with { Type = CircularAperture, MinRadius = c.MinRadius / perUnit, MaxRadius = c.MaxRadius / perUnit },
            Sequential.RectangularAperture r => saved with { Type = RectangularAperture, HalfWidthX = r.HalfWidthX / perUnit, HalfWidthY = r.HalfWidthY / perUnit },
            Sequential.EllipticalAperture e => saved with { Type = EllipticalAperture, HalfWidthX = e.SemiAxisX / perUnit, HalfWidthY = e.SemiAxisY / perUnit },
            Sequential.SpiderAperture s => saved with { Type = SpiderAperture, ArmWidth = s.ArmWidth / perUnit, ArmCount = s.ArmCount },
            PolygonAperture p => saved with { Type = UserDefinedAperture, Vertices = p.Vertices.Select(v => new OpticsPointSettings { X = v.X / perUnit, Y = v.Y / perUnit }).ToList() },
            Sequential.FloatingAperture => saved with { Type = FloatingAperture },
            _ => throw new ArgumentException($"The aperture kind '{a.Kind}' has no saved form."),
        };
    }

    // ------------------------------------------------------------------ materials as text

    /// <summary>The material cell's text: empty for air, MIRROR, the glass name, the index, or
    /// "nd, Vd, ΔPgF" for a model glass (<see cref="ParseMaterial"/> reads it back).</summary>
    public static string FormatMaterial(OpticsSurfaceSettings s)
    {
        if (s.ModelGlass is { } model) return Format(model);
        if (s.Index is double n) return n.ToString("R", CultureInfo.InvariantCulture);
        return s.Material ?? "";
    }

    /// <summary>
    /// Reads the material cell: empty is air; "MIRROR" (any case) a mirror; one number a
    /// constant index; two or three numbers separated by commas a model glass (nd, Vd and
    /// optionally ΔPgF); anything else a glass name, looked up when the lens is traced. Returns
    /// the surface with its material fields set, or null and the reason.
    /// </summary>
    public static OpticsSurfaceSettings? ParseMaterial(OpticsSurfaceSettings surface, string? text, out string? error)
    {
        error = null;
        string t = (text ?? "").Trim();
        var cleared = surface with { Material = null, Index = null, ModelGlass = null };
        if (t.Length == 0) return cleared;
        if (string.Equals(t, Mirror, StringComparison.OrdinalIgnoreCase)) return cleared with { Material = Mirror };
        var parts = t.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new double[parts.Length];
        bool allNumbers = parts.Length <= 3;
        for (int i = 0; i < parts.Length && allNumbers; i++)
            allNumbers = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) && double.IsFinite(numbers[i]);
        if (!allNumbers)
        {
            if (parts.Length > 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                error = $"'{t}' is neither a glass name nor a model glass 'nd, Vd' or 'nd, Vd, ΔPgF'.";
                return null;
            }
            return cleared with { Material = t };
        }
        if (parts.Length == 1)
        {
            if (!(numbers[0] > 0)) { error = $"An index must be positive (it is {parts[0]})."; return null; }
            return cleared with { Index = numbers[0] };
        }
        if (!(numbers[0] > 1) || !(numbers[1] > 0))
        {
            error = $"A model glass needs nd above 1 and a positive Vd (they are {parts[0]}, {parts[1]}).";
            return null;
        }
        return cleared with { ModelGlass = new OpticsModelGlassSettings { Nd = numbers[0], Vd = numbers[1], DeltaPgF = parts.Length == 3 ? numbers[2] : 0 } };
    }

    private static string Format(OpticsModelGlassSettings m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Nd:R}, {m.Vd:R}, {m.DeltaPgF:R}");

    /// <summary>"Object surface", "Surface j" or "Image surface".</summary>
    public static string Where(int j, int count) =>
        j == 0 ? "Object surface" : j == count - 1 ? "Image surface" : $"Surface {j}";
}
