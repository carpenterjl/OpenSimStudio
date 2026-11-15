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
/// The Standard surface and every type of <see cref="SurfaceTypes"/> (the aspheres of OP-23,
/// the special surfaces of OP-27 and the coordinate break of OP-21) convert, with their
/// parameters scaled by each column's unit; surface tilts and decenters before and after, and
/// a coordinate break's return, convert too (TC-6). A type not registered there is saved and
/// edited as its name and numbered parameters, and converting a lens that holds one is refused
/// with a message naming the surface and the type, never traced as something else. Cell flags
/// are not read here: <see cref="Analysis.ProjectLens"/> evaluates the solves and pickups
/// (<see cref="SavedSolves"/>) on the lens this returns.
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

    /// <summary>The value of parameter <paramref name="number"/> in the parameter list, zero when
    /// not listed.</summary>
    public static double Parameter(OpticsSurfaceSettings surface, int number) =>
        surface.Parameters?.LastOrDefault(p => p.Number == number)?.Value ?? 0;

    /// <summary>True when parameter <paramref name="number"/> of the surface is saved in the
    /// extra-data list: a registered type's numbers from <see cref="SurfaceTypes.FirstExtraData"/>
    /// on (<see cref="SurfaceTypes"/>); an unregistered type keeps every number in the parameter list.</summary>
    public static bool IsExtraData(OpticsSurfaceSettings surface, int number) =>
        number >= SurfaceTypes.FirstExtraData && SurfaceTypes.Find(surface.Type) is not null;

    /// <summary>The value of parameter column <paramref name="number"/> ("Par n") wherever the
    /// surface's type keeps it, zero when not listed.</summary>
    public static double Value(OpticsSurfaceSettings surface, int number) => IsExtraData(surface, number)
        ? surface.ExtraData?.LastOrDefault(p => p.Number == number)?.Value ?? 0
        : Parameter(surface, number);

    /// <summary>The number of a "Par n" column key, or null for another key.</summary>
    public static int? ParameterNumber(string? column) =>
        column is not null && column.StartsWith("Par", StringComparison.Ordinal)
        && int.TryParse(column.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 ? n : null;

    // Saved names of the tilt/decenter fields (OpticsTiltDecenterSettings, OpticsCoordinateReturnSettings).
    public static string OrderName(TiltDecenterOrder order) => order.ToString();
    public static string AfterModeName(TiltAfterKind kind) => kind.ToString();
    public static string ReturnModeName(CoordinateReturnMode mode) => mode.ToString();

    /// <summary>The saved tilt/decenter as the trace's (lengths × <paramref name="perUnit"/>), or
    /// null and a problem when its order is not a known name.</summary>
    public static TiltDecenter? ToTiltDecenter(OpticsTiltDecenterSettings t, double perUnit, string where, ICollection<string> problems)
    {
        if (!TryParse(t.Order, TiltDecenterOrder.DecenterThenTilt, out TiltDecenterOrder order))
        {
            problems.Add($"{where}: the order '{t.Order}' is not one this version knows ({string.Join(", ", Enum.GetNames<TiltDecenterOrder>())}).");
            return null;
        }
        return new TiltDecenter(t.DecenterX * perUnit, t.DecenterY * perUnit, t.TiltX, t.TiltY, t.TiltZ, order);
    }

    private static bool TryParse<T>(string? name, T fallback, out T value) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(name)) { value = fallback; return true; }
        return Enum.TryParse(name.Trim(), ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    private static OpticsTiltDecenterSettings FromTiltDecenter(TiltDecenter t, double perUnit) => new()
    {
        DecenterX = t.DecenterX / perUnit, DecenterY = t.DecenterY / perUnit,
        TiltX = t.TiltXDegrees, TiltY = t.TiltYDegrees, TiltZ = t.TiltZDegrees,
        Order = t.Order == TiltDecenterOrder.DecenterThenTilt ? null : OrderName(t.Order),
    };

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
            SurfaceShape? shape = null;
            CoordinateBreak? coordinateBreak = null;
            if (IsStandard(s))
            {
                if (s.Parameters?.FirstOrDefault(p => p.Value != 0) is { } p)
                    problems.Add($"{where}: a {StandardType} surface has no parameters, but parameter {p.Number} is {p.Value.ToString("R", CultureInfo.InvariantCulture)}.");
                if (s.ExtraData?.FirstOrDefault(p => p.Value != 0) is { } x)
                    problems.Add($"{where}: a {StandardType} surface has no extra data, but extra data {x.Number} is {x.Value.ToString("R", CultureInfo.InvariantCulture)}.");
            }
            else if (SurfaceTypes.Find(s.Type) is not { } layout)
            {
                problems.Add($"{where}: the surface type '{s.Type}' is saved with the lens but this build does not trace it (the types it traces are {StandardType}, {string.Join(", ", SurfaceTypes.All.Select(t => t.Name))}); change the type or remove the surface to trace the lens.");
                continue;
            }
            else if (!TypedSurface(s, layout, perUnit, where, problems, out shape, out coordinateBreak))
                continue;
            if (s.CoordinateReturn is not null && coordinateBreak is null)
                problems.Add($"{where}: only a coordinate break has a coordinate return; remove it or make the surface a {SurfaceTypes.CoordinateBreak}.");
            double? radius = s.Radius;
            if (radius is double rr && double.IsNaN(rr)) problems.Add($"{where}: the radius must be a number.");
            if (!s.ThicknessIsInfinite && !double.IsFinite(s.Thickness)) problems.Add($"{where}: the thickness must be a number.");
            bool mirror = IsMirror(s);
            var medium = Medium(s, mirror, where, findGlass, modelGlass, problems);
            // OP-53: a Birefringent In's extraordinary index is the glass named as its own with "-E" appended.
            if (shape is Birefringent.BirefringentInShape crystal)
                shape = crystal.WithExtraordinary(Extraordinary(s, where, findGlass, problems));
            SurfaceAperture? aperture = null;
            if (s.Aperture is { } a)
                aperture = Aperture(a, perUnit, where, problems);
            TiltDecenter? tiltBefore = s.TiltBefore is { } tb ? ToTiltDecenter(tb, perUnit, where + ", tilt/decenter before", problems) : null;
            SurfaceTiltAfter? tiltAfter = s.TiltAfter is { } ta ? TiltAfter(ta, perUnit, where, problems) : null;
            var addOns = s.AddOns is { Count: > 0 } savedAddOns ? AddOnsFrom(savedAddOns, perUnit, where, problems) : null;   // OP-32
            result[j] = new SequentialSurface
            {
                Shape = shape,
                CoordinateBreak = coordinateBreak,
                TiltBefore = tiltBefore,
                TiltAfter = tiltAfter,
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
                AddOns = addOns,   // OP-32
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

    /// <summary>
    /// A registered type's shape or coordinate break from its saved parameters: each value in
    /// lens units times u^power of its column (<see cref="SurfaceParameterInfo.LengthPower"/>),
    /// numbers 1–12 from the parameter list and 13 on from the extra data. A value in a column
    /// the type does not have, a radius or conic on a type that ignores them, and a value the
    /// shape refuses are problems naming the surface and the parameter.
    /// </summary>
    private static bool TypedSurface(OpticsSurfaceSettings s, SurfaceTypeLayout layout, double perUnit, string where,
        List<string> problems, out SurfaceShape? shape, out CoordinateBreak? coordinateBreak)
    {
        shape = null;
        coordinateBreak = null;
        int before = problems.Count;
        foreach (var p in s.Parameters ?? new List<OpticsSurfaceParameterSettings>())
            if (p.Value != 0 && (layout.Parameter(p.Number) is not { } info || info.IsExtraData))
                problems.Add($"{where}: the {layout.Name} has no parameter {p.Number} in its parameter list, but it is {p.Value.ToString("R", CultureInfo.InvariantCulture)}.");
        foreach (var p in s.ExtraData ?? new List<OpticsSurfaceParameterSettings>())
            if (p.Value != 0 && (layout.Parameter(p.Number) is not { } info || !info.IsExtraData))
                problems.Add($"{where}: the {layout.Name} has no extra data {p.Number}, but it is {p.Value.ToString("R", CultureInfo.InvariantCulture)}.");
        if (!layout.UsesRadiusAndConic)
        {
            if (!IsPlane(s)) problems.Add($"{where}: a {layout.Name} surface has no radius (its shape is its own); leave the radius infinite.");
            if (s.Conic != 0) problems.Add($"{where}: a {layout.Name} surface has no conic; leave it at zero.");
        }
        double Si(int number) => layout.Parameter(number) is { } info && Value(s, number) is var v && v != 0
            ? v * SurfaceTypes.Factor(info.LengthPower, perUnit)
            : 0;
        if (layout.IsCoordinateBreak)
        {
            if (!IsAir(s)) problems.Add($"{where}: a coordinate break takes the medium of the surface before it; leave its material empty.");
            if (s.TiltBefore is not null || s.TiltAfter is not null)
                problems.Add($"{where}: a coordinate break cannot have a surface tilt or decenter; put the values in its parameters.");
            var values = SurfaceTypes.BreakFrom(Si);
            var mode = CoordinateReturnMode.None;
            int to = 0;
            if (s.CoordinateReturn is { } r)
            {
                if (!TryParse(r.Mode, CoordinateReturnMode.None, out mode))
                    problems.Add($"{where}: the coordinate return '{r.Mode}' is not one this version knows ({string.Join(", ", Enum.GetNames<CoordinateReturnMode>())}).");
                to = r.Surface;
            }
            coordinateBreak = new CoordinateBreak { Values = values, Return = mode, ReturnToSurface = mode == CoordinateReturnMode.None ? 0 : to };
            return problems.Count == before;
        }
        // OP-25: a Grid Sag is built from its parameters and its saved grid.
        if (s.GridSag is not null && layout.Name != Freeform.FreeformLayouts.GridSag)
            problems.Add($"{where}: only a {Freeform.FreeformLayouts.GridSag} surface has grid data, but this {layout.Name} has some; remove it or make the surface a {Freeform.FreeformLayouts.GridSag}.");
        if (layout.Name == Freeform.FreeformLayouts.GridSag && s.GridSag is { } grid)
        {
            try { shape = Freeform.FreeformLayouts.BuildGridSag(layout, Si, Freeform.GridSagSaved.ToData(grid)); }
            catch (ArgumentException ex) { problems.Add($"{where}: {ex.Message}"); }
            return problems.Count == before;
        }
        try { shape = layout.Build(Si); }
        catch (ArgumentException ex) { problems.Add($"{where}: {ex.Message}"); }
        return problems.Count == before;
    }

    private static SurfaceTiltAfter? TiltAfter(OpticsTiltDecenterSettings t, double perUnit, string where, List<string> problems)
    {
        if (!TryParse(t.Mode, TiltAfterKind.Explicit, out TiltAfterKind kind))
        {
            problems.Add($"{where}, tilt/decenter after: the mode '{t.Mode}' is not one this version knows ({string.Join(", ", Enum.GetNames<TiltAfterKind>())}).");
            return null;
        }
        return kind switch
        {
            TiltAfterKind.Explicit => ToTiltDecenter(t, perUnit, where + ", tilt/decenter after", problems) is { } values ? SurfaceTiltAfter.Explicit(values) : null,
            TiltAfterKind.PickUpThisSurface => SurfaceTiltAfter.PickUpThis,
            TiltAfterKind.ReverseThisSurface => SurfaceTiltAfter.ReverseThis,
            TiltAfterKind.PickUpSurface => SurfaceTiltAfter.PickUp(t.Surface),
            _ => SurfaceTiltAfter.Reverse(t.Surface),
        };
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

    // OP-53
    /// <summary>The extraordinary index of a Birefringent In: "To compute the extraordinary
    /// index, OpticStudio appends "-E" to the glass name, and then looks for this material in the
    /// glass catalog ... If not found, an error message is issued" (help, "Birefringent In and
    /// Birefringent Out"). A constant index or a model glass has no such name and is refused.</summary>
    private static ParaxialMedium? Extraordinary(OpticsSurfaceSettings s, string where, Func<string, OpticalMaterial?> findGlass, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(s.Material) || IsMirror(s))
        {
            problems.Add($"{where}: a {Birefringent.BirefringentInShape.Name} needs a glass name for its ordinary index; its extraordinary index is the glass of that name with \"-E\" appended (OpticStudio help, \"Birefringent In and Birefringent Out\").");
            return null;
        }
        string name = s.Material.Trim() + "-E";
        var glass = findGlass(name);
        if (glass is null)
        {
            problems.Add($"{where}: the extraordinary glass '{name}' is not in the active catalogs (a {Birefringent.BirefringentInShape.Name} takes its extraordinary index from the glass named as its own with \"-E\" appended).");
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
    /// The saved form of a lens built in code: the Standard surface and every registered type
    /// (<see cref="SurfaceTypes"/>; a shape of another type is refused, naming it), with each
    /// parameter divided by u^power of its column; tilts, decenters and coordinate breaks with
    /// their returns; lengths in <paramref name="unit"/>; a catalog material by "catalog:name"
    /// (or its name when it has no catalog). A model glass cannot be told from another formula
    /// glass and is written by name. Parameters equal to zero are left out (a missing number is
    /// zero).
    /// </summary>
    public static List<OpticsSurfaceSettings> FromLens(SequentialLens lens, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(lens);
        double perUnit = OpticsConventions.MetersPer(unit);
        var list = new List<OpticsSurfaceSettings>(lens.Surfaces.Count);
        for (int j = 0; j < lens.Surfaces.Count; j++)
        {
            var s = lens.Surfaces[j];
            string? type = null;
            IReadOnlyDictionary<int, double>? values = null;
            SurfaceTypeLayout? layout = null;
            OpticsCoordinateReturnSettings? coordinateReturn = null;
            if (s.CoordinateBreak is { } cb)
            {
                layout = SurfaceTypes.Find(SurfaceTypes.CoordinateBreak)!;
                values = SurfaceTypes.BreakValues(cb.Values);
                if (cb.Return != CoordinateReturnMode.None)
                    coordinateReturn = new OpticsCoordinateReturnSettings { Mode = ReturnModeName(cb.Return), Surface = cb.ReturnToSurface };
            }
            else if (s.Shape is { } shape)
            {
                layout = SurfaceTypes.Find(shape.TypeName)
                    ?? throw new ArgumentException($"{Where(j, lens.Surfaces.Count)}: the surface type '{shape.TypeName}' cannot be saved by this build; it has no parameter layout (the saved types are {StandardType}, {string.Join(", ", SurfaceTypes.All.Select(t => t.Name))}).");
                values = layout.Read(shape);
            }
            List<OpticsSurfaceParameterSettings>? parameters = null, extra = null;
            if (layout is not null)
            {
                type = layout.Name;
                foreach (var (number, v) in values!.OrderBy(p => p.Key))
                {
                    if (v == 0) continue;
                    var info = layout.Parameter(number)!;
                    var entry = new OpticsSurfaceParameterSettings { Number = number, Value = v / SurfaceTypes.Factor(info.LengthPower, perUnit) };
                    if (info.IsExtraData) (extra ??= new()).Add(entry);
                    else (parameters ??= new()).Add(entry);
                }
            }
            var m = s.Medium;
            // OP-53: the saved form names only the ordinary glass; the extraordinary one must be its "-E" twin.
            if (s.Shape is Birefringent.BirefringentInShape crystal
                && !(m.Material is { } ordinaryGlass && crystal.Extraordinary?.Material is { } eGlass
                     && string.Equals(eGlass.Name, ordinaryGlass.Name + "-E", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(eGlass.Catalog, ordinaryGlass.Catalog, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{Where(j, lens.Surfaces.Count)}: a {Birefringent.BirefringentInShape.Name} is saved by its glass name only, so its extraordinary index must be the glass of the same name with \"-E\" appended in the same catalog; this one is {crystal.Extraordinary?.Describe() ?? "missing"} with {m.Describe()}.");
            list.Add(new OpticsSurfaceSettings
            {
                Type = type,
                Parameters = parameters,
                ExtraData = extra,
                CoordinateReturn = coordinateReturn,
                GridSag = s.Shape is Freeform.GridSagSurface gridSag ? Freeform.GridSagSaved.FromData(gridSag.Data) : null, // OP-25
                TiltBefore = s.TiltBefore is { } tb ? FromTiltDecenter(tb, perUnit) : null,
                TiltAfter = s.TiltAfter is { } ta ? FromTiltAfter(ta, perUnit) : null,
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
                AddOns = s.AddOns is { Count: > 0 } addOns ? addOns.Select((x, k) => FromAddOn(x, perUnit, $"{Where(j, lens.Surfaces.Count)}, composite add-on {k + 1}")).ToList() : null,   // OP-32
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ OP-32: composite add-ons

    /// <summary>The saved add-ons as the trace's (<see cref="Composite.CompositeAddOn"/>), each
    /// shape built from its type and parameters exactly as a surface's is; problems name the
    /// surface and the add-on. The rules on which types may be add-ons are
    /// <see cref="Composite.CompositeSurfaces.Check"/>'s (run by the lens check).</summary>
    private static List<Composite.CompositeAddOn> AddOnsFrom(List<OpticsCompositeAddOnSettings> saved, double perUnit, string where, List<string> problems)
    {
        var list = new List<Composite.CompositeAddOn>(saved.Count);
        for (int k = 0; k < saved.Count; k++)
        {
            var a = saved[k] ?? new OpticsCompositeAddOnSettings();
            string w = $"{where}, composite add-on {k + 1}";
            var asSurface = new OpticsSurfaceSettings
            {
                Type = a.Type, Radius = a.Radius, Conic = a.Conic,
                Parameters = a.Parameters, ExtraData = a.ExtraData, GridSag = a.GridSag,
            };
            SurfaceShape? shape = null;
            if (IsStandard(asSurface))
            {
                if (a.Parameters?.FirstOrDefault(p => p.Value != 0) is { } p)
                    problems.Add($"{w}: a {StandardType} add-on has no parameters, but parameter {p.Number} is {p.Value.ToString("R", CultureInfo.InvariantCulture)}.");
                if (a.ExtraData?.FirstOrDefault(p => p.Value != 0) is { } x)
                    problems.Add($"{w}: a {StandardType} add-on has no extra data, but extra data {x.Number} is {x.Value.ToString("R", CultureInfo.InvariantCulture)}.");
                if (a.GridSag is not null)
                    problems.Add($"{w}: only a {Freeform.FreeformLayouts.GridSag} add-on has grid data; remove it or make the add-on a {Freeform.FreeformLayouts.GridSag}.");
            }
            else if (SurfaceTypes.Find(a.Type) is not { } layout)
            {
                problems.Add($"{w}: the surface type '{a.Type}' is not one this build traces (the composite types are {string.Join(", ", Composite.CompositeSurfaces.SupportedTypes)}).");
                continue;
            }
            else if (layout.IsCoordinateBreak)
            {
                problems.Add($"{w}: a {SurfaceTypes.CoordinateBreak} cannot be a composite add-on.");
                continue;
            }
            else if (!TypedSurface(asSurface, layout, perUnit, w, problems, out shape, out _))
                continue;
            if (a.Radius is double rr && double.IsNaN(rr)) problems.Add($"{w}: the radius must be a number.");
            list.Add(new Composite.CompositeAddOn
            {
                Shape = shape,
                Curvature = a.Radius is double r ? OpticsConventions.CurvatureFromRadius(r * perUnit) : 0,
                Conic = a.Conic,
                Placement = new TiltDecenter(a.DecenterX * perUnit, a.DecenterY * perUnit, a.TiltX, a.TiltY, a.TiltZ),
                IsIgnored = a.IsIgnored,
                SemiDiameter = a.SemiDiameter * perUnit,
                Comment = a.Comment ?? "",
            });
        }
        return list;
    }

    /// <summary>One add-on in the saved form (lens units), its type and parameters written as a
    /// surface's are.</summary>
    private static OpticsCompositeAddOnSettings FromAddOn(Composite.CompositeAddOn a, double perUnit, string where)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Placement.Order != TiltDecenterOrder.DecenterThenTilt)
            throw new ArgumentException($"{where}: an add-on's placement is saved as decenter then tilt; this one is {a.Placement.Order}.");
        string? type = null;
        List<OpticsSurfaceParameterSettings>? parameters = null, extra = null;
        if (a.Shape is { } shape)
        {
            var layout = SurfaceTypes.Find(shape.TypeName)
                ?? throw new ArgumentException($"{where}: the surface type '{shape.TypeName}' cannot be saved by this build; it has no parameter layout.");
            type = layout.Name;
            foreach (var (number, v) in layout.Read(shape).OrderBy(p => p.Key))
            {
                if (v == 0) continue;
                var info = layout.Parameter(number)!;
                var entry = new OpticsSurfaceParameterSettings { Number = number, Value = v / SurfaceTypes.Factor(info.LengthPower, perUnit) };
                if (info.IsExtraData) (extra ??= new()).Add(entry);
                else (parameters ??= new()).Add(entry);
            }
        }
        return new OpticsCompositeAddOnSettings
        {
            Type = type,
            Parameters = parameters,
            ExtraData = extra,
            GridSag = a.Shape is Freeform.GridSagSurface gridSag ? Freeform.GridSagSaved.FromData(gridSag.Data) : null,
            Radius = a.Curvature == 0 ? null : 1.0 / a.Curvature / perUnit,
            Conic = a.Conic,
            SemiDiameter = a.SemiDiameter / perUnit,
            DecenterX = a.Placement.DecenterX / perUnit,
            DecenterY = a.Placement.DecenterY / perUnit,
            TiltX = a.Placement.TiltXDegrees,
            TiltY = a.Placement.TiltYDegrees,
            TiltZ = a.Placement.TiltZDegrees,
            IsIgnored = a.IsIgnored,
            Comment = a.Comment.Length > 0 ? a.Comment : null,
        };
    }

    private static OpticsTiltDecenterSettings FromTiltAfter(SurfaceTiltAfter t, double perUnit) => t.Kind switch
    {
        TiltAfterKind.Explicit => FromTiltDecenter(t.Values, perUnit),
        TiltAfterKind.PickUpSurface or TiltAfterKind.ReverseSurface => new OpticsTiltDecenterSettings { Mode = AfterModeName(t.Kind), Surface = t.Surface },
        _ => new OpticsTiltDecenterSettings { Mode = AfterModeName(t.Kind) },
    };

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
