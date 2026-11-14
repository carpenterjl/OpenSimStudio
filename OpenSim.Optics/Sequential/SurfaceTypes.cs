using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// One parameter column of a surface type: OpticStudio's parameter number, its meaning, and how
/// its value scales with the lens unit. A value v in lens units is v·u^<see cref="LengthPower"/>
/// in SI, u the meters per lens unit (a length is power 1, an inverse length −1, a coefficient
/// on r⁴ of the Even Asphere −3, a dimensionless number or an angle 0). The same power gives
/// Scale Lens's factor ("data measured in units of lens units squared … will be scaled by X
/// squared. Some polynomial coefficients … have units that change from term to term";
/// OpticStudio 2025 R2 help, "Scale Lens").
/// </summary>
/// <param name="Number">OpticStudio's parameter number as the type's help page gives it.</param>
/// <param name="Name">The column's header, from the help page's definition.</param>
/// <param name="LengthPower">The power of length of the value's unit.</param>
public sealed record SurfaceParameterInfo(int Number, string Name, int LengthPower)
{
    /// <summary>A whole number (a type flag, a maximum term number, an OPD mode).</summary>
    public bool IsInteger { get; init; }

    /// <summary>An angle in degrees (the tilts of a coordinate break).</summary>
    public bool IsAngle { get; init; }

    /// <summary>The smallest and largest allowed values of an integer column, or null.</summary>
    public (int Min, int Max)? Range { get; init; }

    /// <summary>A sentence for the editor's tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>True when the value is saved in the extra-data list
    /// (<see cref="SurfaceTypes.FirstExtraData"/> and above).</summary>
    public bool IsExtraData => Number >= SurfaceTypes.FirstExtraData;

    /// <summary>The unit in words for a tooltip: "lens units", "lens units^-3", "degrees", "dimensionless".</summary>
    public string UnitText => IsAngle ? "degrees" : IsInteger ? "a whole number" : LengthPower switch
    {
        0 => "dimensionless",
        1 => "lens units",
        _ => "lens units^" + LengthPower.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The parameter-column layout of one OpticStudio surface type and the conversion between its
/// parameters (SI) and the <see cref="SurfaceShape"/> that traces it. Built by
/// <see cref="SurfaceTypes"/>; see there for the sources.
/// </summary>
public sealed class SurfaceTypeLayout
{
    private readonly Func<Func<int, double>, SurfaceShape?> _build;
    private readonly Func<SurfaceShape, IReadOnlyDictionary<int, double>> _read;
    private readonly Dictionary<int, SurfaceParameterInfo> _byNumber;

    internal SurfaceTypeLayout(string name, string source, IReadOnlyList<SurfaceParameterInfo> parameters,
        Func<Func<int, double>, SurfaceShape?> build, Func<SurfaceShape, IReadOnlyDictionary<int, double>> read)
    {
        Name = name;
        Source = source;
        Parameters = parameters;
        _build = build;
        _read = read;
        _byNumber = parameters.ToDictionary(p => p.Number);
    }

    /// <summary>The type's name as OpticStudio's editor lists it (and <see cref="SurfaceShape.TypeName"/>).</summary>
    public string Name { get; }

    /// <summary>The help page the layout is copied from.</summary>
    public string Source { get; }

    /// <summary>Every parameter column the type uses, in number order.</summary>
    public IReadOnlyList<SurfaceParameterInfo> Parameters { get; }

    /// <summary>False for the types whose shape is a plane that ignores the surface's radius and
    /// conic (Paraxial, Paraxial XY, ABCD, Retro Reflect, Conjugate) and for the coordinate
    /// break; a radius or conic given on them is refused rather than silently unused.</summary>
    public bool UsesRadiusAndConic { get; init; } = true;

    /// <summary>True for the coordinate break, which is not a shape (<see cref="SequentialSurface.CoordinateBreak"/>).</summary>
    public bool IsCoordinateBreak { get; init; }

    /// <summary>The column numbered <paramref name="number"/>, or null when the type has none.</summary>
    public SurfaceParameterInfo? Parameter(int number) => _byNumber.TryGetValue(number, out var p) ? p : null;

    /// <summary>The largest parameter number, or 0 for a type with none.</summary>
    public int LastNumber => Parameters.Count == 0 ? 0 : Parameters[^1].Number;

    /// <summary>The shape of the given SI parameter values (missing numbers are zero); throws
    /// <see cref="ArgumentException"/> naming the type and the value when one is not allowed.
    /// Null for the coordinate break.</summary>
    public SurfaceShape? Build(Func<int, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var p in Parameters)
        {
            double v = value(p.Number);
            if (!double.IsFinite(v))
                throw new ArgumentException($"{Name}: {Label(p)} is {v}; it must be a finite number.");
            if (p.IsInteger && Math.Round(v) != v)
                throw new ArgumentException($"{Name}: {Label(p)} is {v.ToString("R", CultureInfo.InvariantCulture)}; it must be a whole number.");
            if (p.Range is { } r && (v < r.Min || v > r.Max))
                throw new ArgumentException($"{Name}: {Label(p)} is {v.ToString("R", CultureInfo.InvariantCulture)}; it must be from {r.Min} to {r.Max}.");
        }
        return _build(value);
    }

    /// <summary>The SI parameter values of a shape of this type, every column the shape fixes
    /// (zeros included); <see cref="Build"/> of them gives the same shape.</summary>
    public IReadOnlyDictionary<int, double> Read(SurfaceShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!string.Equals(shape.TypeName, Name, StringComparison.Ordinal))
            throw new ArgumentException($"A '{shape.TypeName}' shape is not a {Name}.");
        return _read(shape);
    }

    /// <summary>"parameter 3 (z1)".</summary>
    public static string Label(SurfaceParameterInfo p) => $"parameter {p.Number} ({p.Name})";

    public override string ToString() => Name;
}

/// <summary>
/// The surface types a saved lens can hold besides Standard (TC-6): for each, OpticStudio's
/// parameter columns (number, meaning, unit), copied from the type's OpticStudio 2025 R2 help
/// page, and the shape the parameters build. Saved surfaces (<see cref="SavedSurfaces"/>), the
/// lens data editor and the editing tools read the layouts from here, and parameter solves read
/// and write shape parameters through <see cref="TryGetParameter"/> and
/// <see cref="WithParameter"/> (docs/Optics-Conventions.md section 20).
/// <para>
/// Sources: the help pages "Even Asphere", "Odd Asphere", "Alternate Even", "Alternate Odd",
/// "Extended Asphere", "Extended Odd Asphere", "Q-Type Asphere (sequential surfaces, lens data
/// editor)", "Superconic" and "Periodic" (saved under plans/sources/optics/aspheres/); "ABCD",
/// "Conjugate", "Fresnel", "Extended Fresnel", "Cylinder Fresnel", "Generalized Fresnel",
/// "Paraxial (sequential surfaces, lens data editor)", "Paraxial XY" and "Retro Reflect" (under
/// plans/sources/optics/ansyshelp-op27/); "Coordinate Break" and "Tilt/Decenter" (downloaded
/// 2026-11-14 under plans/sources/optics/tc6-coordbreak/). Each layout below names its page.
/// </para>
/// <para>
/// <b>Readings</b> (where a page leaves the layout open):
/// <list type="bullet">
/// <item>Parameter numbers 13 and above are saved as extra data with the same number. The help
/// pages number these columns as parameters but call them "extra data" (Odd Cosine, Extended
/// Odd Asphere, Q-Type, Extended and Cylinder Fresnel), so both the number and the list follow
/// the pages.</item>
/// <item>A "maximum term number" N uses the first N coefficients after it and ignores the rest
/// ("terms beyond this number are ignored", Extended Asphere). For the Q-Type (A0 … A99, "the
/// maximum is 100") the first N are A0 … A(N − 1). For the Superconic ("up to 240 terms",
/// U1, V1 … U120, V120) a term is one U or V value, so N = 3 uses U1, V1 and U2 (V2 taken as
/// zero). Reading a shape back writes N as the number of coefficients the shape holds.</item>
/// <item>The ABCD surface's B is a length and C an inverse length, as its matrix acts on a
/// height and a slope (<see cref="AbcdShape"/>).</item>
/// <item>The Paraxial surface's OPD modes 2 (infinite conjugates) and 3 (paraxial conjugates,
/// resolved by <see cref="Analysis.ProjectLens"/>) are built; modes 0 and 1 are refused.</item>
/// <item>The coordinate break's order flag: 0 is decenter then tilt; "any other value (such as
/// unity)" tilt then decenter, written back as 1.</item>
/// </list>
/// </para>
/// <para>
/// <b>Not registered:</b> Odd Cosine (its page lists parameters 31–54 as "the A, P, B, and C
/// terms" without their order) and Lenslet Array (its page points to a user-defined surface DLL
/// whose four parameters it does not list). Such a type is saved and shown by parameter number
/// and refused when traced, as any type not listed here.
/// </para>
/// </summary>
public static class SurfaceTypes
{
    /// <summary>The first parameter number saved in the extra-data list.</summary>
    public const int FirstExtraData = 13;

    /// <summary>The coordinate break's type name.</summary>
    public const string CoordinateBreak = "Coordinate Break";

    private static readonly SurfaceTypeLayout[] Layouts = BuildLayouts();
    private static readonly Dictionary<string, SurfaceTypeLayout> ByName =
        Layouts.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered type, in the order the editor lists them.</summary>
    public static IReadOnlyList<SurfaceTypeLayout> All => Layouts;

    /// <summary>The layout of the type named <paramref name="name"/> (ignoring case), or null.</summary>
    public static SurfaceTypeLayout? Find(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out var l) ? l : null;

    /// <summary>The factor from lens units to SI of a value with length power
    /// <paramref name="power"/>: u^power, by repeated multiplication (u^0 = 1 exactly).</summary>
    public static double Factor(int power, double metersPerUnit)
    {
        double f = 1;
        for (int i = 0; i < Math.Abs(power); i++) f *= metersPerUnit;
        return power >= 0 ? f : 1 / f;
    }

    // ------------------------------------------------------------------ parameter access for solves

    /// <summary>
    /// The SI value of parameter <paramref name="number"/> of a surface: of its shape when the
    /// shape's type is registered, of its coordinate break (1, 2 decenters in meters, 3–5 tilts in
    /// degrees, 6 the order 0 or 1). False when the surface has no such parameter.
    /// </summary>
    public static bool TryGetParameter(SequentialSurface surface, int number, out double value)
    {
        ArgumentNullException.ThrowIfNull(surface);
        value = 0;
        if (surface.CoordinateBreak is { } cb)
        {
            var v = BreakValues(cb.Values);
            return v.TryGetValue(number, out value);
        }
        if (surface.Shape is not { } shape || Find(shape.TypeName) is not { } layout || layout.Parameter(number) is null) return false;
        value = layout.Read(shape).TryGetValue(number, out double v2) ? v2 : 0;
        return true;
    }

    /// <summary>The number of parameters a solve can address on the surface (numbers 0 to
    /// count − 1), 0 when none.</summary>
    public static int ParameterCount(SequentialSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.CoordinateBreak is not null) return 7;
        return surface.Shape is { } shape && Find(shape.TypeName) is { } layout ? layout.LastNumber + 1 : 0;
    }

    /// <summary>The surface with parameter <paramref name="number"/> set to the SI value;
    /// throws <see cref="ArgumentException"/> naming the parameter when it is not allowed.</summary>
    public static SequentialSurface WithParameter(SequentialSurface surface, int number, double value)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.CoordinateBreak is { } cb)
        {
            var v = BreakValues(cb.Values).ToDictionary(p => p.Key, p => p.Value);
            if (!v.ContainsKey(number))
                throw new ArgumentException($"{CoordinateBreak}: there is no parameter {number} (the break has parameters 1 to 6).");
            v[number] = value;
            return surface with { CoordinateBreak = cb with { Values = BreakFrom(n => v.TryGetValue(n, out double x) ? x : 0) } };
        }
        if (surface.Shape is not { } shape || Find(shape.TypeName) is not { } layout)
            throw new ArgumentException($"The surface ({surface.Shape?.TypeName ?? SavedSurfaces.StandardType}) has no parameters a solve can use.");
        var p = layout.Parameter(number)
            ?? throw new ArgumentException($"{layout.Name}: there is no parameter {number}.");
        if (shape is ParaxialShape paraxial)
        {
            if (number != 1) throw new ArgumentException($"{layout.Name}: {SurfaceTypeLayout.Label(p)} cannot be set by a solve.");
            return surface with { Shape = new ParaxialShape(value, paraxial.ObjectDistance) };
        }
        var values = layout.Read(shape).ToDictionary(k => k.Key, k => k.Value);
        values[number] = value;
        return surface with { Shape = layout.Build(n => values.TryGetValue(n, out double x) ? x : 0) };
    }

    /// <summary>The break's six parameters (SI: meters, degrees, order 0 or 1).</summary>
    public static IReadOnlyDictionary<int, double> BreakValues(TiltDecenter t) => new Dictionary<int, double>
    {
        [1] = t.DecenterX, [2] = t.DecenterY, [3] = t.TiltXDegrees, [4] = t.TiltYDegrees, [5] = t.TiltZDegrees,
        [6] = t.Order == TiltDecenterOrder.DecenterThenTilt ? 0 : 1,
    };

    /// <summary>The break of six SI parameter values (the order flag: 0 decenter then tilt, any
    /// other value tilt then decenter, "Coordinate Break").</summary>
    public static TiltDecenter BreakFrom(Func<int, double> value) => new(
        value(1), value(2), value(3), value(4), value(5),
        value(6) == 0 ? TiltDecenterOrder.DecenterThenTilt : TiltDecenterOrder.TiltThenDecenter);

    // ------------------------------------------------------------------ the layouts

    private static SurfaceParameterInfo P(int number, string name, int power, string? description = null) =>
        new(number, name, power) { Description = description };

    private static SurfaceParameterInfo Count(int number, string name, int max, string? description = null) =>
        new(number, name, 0) { IsInteger = true, Range = (0, max), Description = description };

    /// <summary>α1 … α8 on r², r⁴, … r¹⁶ in parameters 1–8 (units lens units^(1 − 2i)).</summary>
    private static IEnumerable<SurfaceParameterInfo> EvenTerms(string what = "") =>
        Enumerable.Range(1, 8).Select(i => P(i, $"α{i}{what}", 1 - 2 * i, $"Coefficient on r^{2 * i}{what}."));

    private static double[] Trimmed(IEnumerable<double> values)
    {
        var a = values.ToArray();
        int n = a.Length;
        while (n > 0 && a[n - 1] == 0) n--;
        return n == a.Length ? a : a[..n];
    }

    private static double[] Range(Func<int, double> value, int first, int count) =>
        Enumerable.Range(first, count).Select(value).ToArray();

    private static Dictionary<int, double> Numbered(IReadOnlyList<double> values, int first)
    {
        var d = new Dictionary<int, double>();
        for (int i = 0; i < values.Count; i++) d[first + i] = values[i];
        return d;
    }

    private static Dictionary<int, double> With(Dictionary<int, double> d, params (int Number, double Value)[] more)
    {
        foreach (var (n, v) in more) d[n] = v;
        return d;
    }

    private static int Whole(Func<int, double> value, int number) => (int)value(number);

    private static SurfaceTypeLayout[] BuildLayouts()
    {
        var list = new List<SurfaceTypeLayout>();

        // "Even Asphere": parameters 1–8 are α1–α8; coefficients on r^(2i).
        list.Add(new SurfaceTypeLayout("Even Asphere", "OpticStudio 2025 R2 help, \"Even Asphere\", table Parameter Definitions for Even Asphere Surfaces",
            EvenTerms().ToArray(),
            v => new EvenAsphere(Trimmed(Range(v, 1, 8))),
            s => Numbered(((EvenAsphere)s).Alpha, 1)));

        // "Odd Asphere": parameters 1–8 are β1–β8; coefficients on r^i.
        var oddTerms = Enumerable.Range(1, 8).Select(i => P(i, $"β{i}", 1 - i, $"Coefficient on r^{i}.")).ToArray();
        list.Add(new SurfaceTypeLayout("Odd Asphere", "OpticStudio 2025 R2 help, \"Odd Asphere\", its parameter table",
            oddTerms,
            v => new OddAsphere(Trimmed(Range(v, 1, 8))),
            s => Numbered(((OddAsphere)s).Beta, 1)));

        // "Alternate Even" / "Alternate Odd": "identical to the even (odd) asphere surface model,
        // except the alternate solution is used", so the same parameters.
        list.Add(new SurfaceTypeLayout("Alternate Even", "OpticStudio 2025 R2 help, \"Alternate Even\" (the Even Asphere's parameters)",
            EvenTerms().ToArray(),
            v => new AlternateEvenAsphere(Trimmed(Range(v, 1, 8))),
            s => Numbered(((AlternateEvenAsphere)s).Alpha, 1)));
        list.Add(new SurfaceTypeLayout("Alternate Odd", "OpticStudio 2025 R2 help, \"Alternate Odd\" (the Odd Asphere's parameters)",
            oddTerms,
            v => new AlternateOddAsphere(Trimmed(Range(v, 1, 8))),
            s => Numbered(((AlternateOddAsphere)s).Beta, 1)));

        // "Extended Asphere": 13 maximum term number (at most 240), 14 normalization radius,
        // 15 on ρ², 16 on ρ⁴, 17–254 the further terms; coefficients in lens units.
        list.Add(new SurfaceTypeLayout("Extended Asphere", "OpticStudio 2025 R2 help, \"Extended Asphere\", table Parameter Definitions for Extended Asphere Surfaces",
            new[] { Count(13, "Max term #", 240, "Maximum term number; terms beyond it are ignored."), P(14, "Norm radius", 1, "Normalization radius (lens units).") }
                .Concat(Enumerable.Range(1, 240).Select(i => P(14 + i, $"ρ^{2 * i}", 1, $"Coefficient on ρ^{2 * i}, lens units."))).ToArray(),
            v => new ExtendedAsphere(v(14), Range(v, 15, Whole(v, 13))),
            s =>
            {
                var e = (ExtendedAsphere)s;
                return With(Numbered(e.Alpha, 15), (13, e.Alpha.Count), (14, e.NormalizationRadius));
            }));

        // "Extended Odd Asphere": 13 maximum term number (at most 240), 14 normalization radius,
        // 15 on ρ¹, 16 on ρ², …, parameter n on ρ^(n − 14).
        list.Add(new SurfaceTypeLayout("Extended Odd Asphere", "OpticStudio 2025 R2 help, \"Extended Odd Asphere\", table Parameter Definitions for Extended Odd Asphere Surfaces",
            new[] { Count(13, "Max term #", 240, "Maximum term number; terms beyond it are ignored."), P(14, "Norm radius", 1, "Normalization radius (lens units).") }
                .Concat(Enumerable.Range(1, 240).Select(i => P(14 + i, $"ρ^{i}", 1, $"Coefficient on ρ^{i}, lens units."))).ToArray(),
            v => new ExtendedOddAsphere(v(14), Range(v, 15, Whole(v, 13))),
            s =>
            {
                var e = (ExtendedOddAsphere)s;
                return With(Numbered(e.Alpha, 15), (13, e.Alpha.Count), (14, e.NormalizationRadius));
            }));

        // "Q-Type Asphere (sequential…)": 13 type (0 Qbfs, 1 Qcon), 14 maximum term number (at
        // most 100), 15 normalization radius in lens units, 16–115 A0–A99.
        list.Add(new SurfaceTypeLayout("Q-Type Asphere", "OpticStudio 2025 R2 help, \"Q-Type Asphere (sequential surfaces, lens data editor)\", table Parameter Definitions for Q-Type Asphere Surfaces",
            new[]
            {
                new SurfaceParameterInfo(13, "Type", 0) { IsInteger = true, Range = (0, 1), Description = "0 for Qbfs, 1 for Qcon." },
                Count(14, "Max term #", 100, "Maximum term number: the first N coefficients A0 … A(N − 1) are used."),
                P(15, "Norm radius", 1, "Normalization radius in lens units."),
            }.Concat(Enumerable.Range(0, 100).Select(m => P(16 + m, $"A{m}", 1, $"Coefficient A{m}, lens units."))).ToArray(),
            v => new QTypeAsphere((QTypeKind)Whole(v, 13), v(15), Range(v, 16, Whole(v, 14))),
            s =>
            {
                var q = (QTypeAsphere)s;
                return With(Numbered(q.Coefficients, 16), (13, (int)q.Kind), (14, q.Coefficients.Count), (15, q.NormalizationRadius));
            }));

        // "Superconic": 13 maximum term number (at most 240), then U1, V1 in 14, 15, U2, V2 in
        // 16, 17, … U120, V120 in 252, 253. U_i in lens units^(−2i), V_i in lens units^(−2i−1)
        // (SuperconicSurface).
        var superTerms = new List<SurfaceParameterInfo> { Count(13, "Max term #", 240, "Maximum term number: the first N of U1, V1, U2, V2, … are used.") };
        for (int i = 1; i <= 120; i++)
        {
            superTerms.Add(P(12 + 2 * i, $"U{i}", -2 * i, $"U{i}, lens units^{-2 * i}."));
            superTerms.Add(P(13 + 2 * i, $"V{i}", -2 * i - 1, $"V{i}, lens units^{-2 * i - 1}."));
        }
        list.Add(new SurfaceTypeLayout("Superconic", "OpticStudio 2025 R2 help, \"Superconic\", table Parameter Definitions for Superconic Surfaces",
            superTerms.ToArray(),
            v =>
            {
                int n = Whole(v, 13), pairs = (n + 1) / 2;
                var u = new double[pairs];
                var w = new double[pairs];
                for (int i = 1; i <= pairs; i++)
                {
                    u[i - 1] = v(12 + 2 * i);
                    w[i - 1] = 2 * i <= n ? v(13 + 2 * i) : 0;
                }
                return new SuperconicSurface(u, w);
            },
            s =>
            {
                var c = (SuperconicSurface)s;
                var d = new Dictionary<int, double> { [13] = 2 * c.U.Count };
                for (int i = 1; i <= c.U.Count; i++) { d[12 + 2 * i] = c.U[i - 1]; d[13 + 2 * i] = c.V[i - 1]; }
                return d;
            }));

        // "Periodic": 1 A (peak to valley, lens units), 2 α and 3 β (inverse lens units).
        list.Add(new SurfaceTypeLayout("Periodic", "OpticStudio 2025 R2 help, \"Periodic\", table Parameter Definitions for Periodic Surfaces",
            new[]
            {
                P(1, "A", 1, "Peak-to-valley height of the modulation, lens units."),
                P(2, "α", -1, "Spatial frequency in x, inverse lens units."),
                P(3, "β", -1, "Spatial frequency in y, inverse lens units."),
            },
            v => new PeriodicSurface(v(1), v(2), v(3)),
            s =>
            {
                var p = (PeriodicSurface)s;
                return new Dictionary<int, double> { [1] = p.Amplitude, [2] = p.FrequencyX, [3] = p.FrequencyY };
            }));

        // "Fresnel": "The parameter values are exactly the same as for the even asphere model";
        // parameters 1–8 are α1–α8.
        list.Add(new SurfaceTypeLayout("Fresnel", "OpticStudio 2025 R2 help, \"Fresnel\", table Parameter Definitions for Fresnel Surfaces",
            EvenTerms().ToArray(),
            v => new FresnelShape(Trimmed(Range(v, 1, 8))),
            s => Numbered(((FresnelShape)s).Alpha, 1)));

        // "Extended Fresnel" and "Cylinder Fresnel": 1–8 the substrate's α1–α8, 13 maximum term
        // number n (at most 8), 14 the Fresnel curvature (not radius), 15 its conic, 16–23 its
        // coefficients on r² (y²) … r¹⁶ (y¹⁶).
        SurfaceParameterInfo[] FresnelPattern(string axis) => EvenTerms(" (substrate)")
            .Append(Count(13, "Max term #", 8, "Maximum aspheric polynomial term number of the Fresnel sag."))
            .Append(P(14, "Fresnel curvature", -1, $"The {axis}curvature (not radius) of the Fresnel surface, inverse lens units."))
            .Append(P(15, "Fresnel conic", 0, $"The {axis}conic constant of the Fresnel surface."))
            .Concat(Enumerable.Range(1, 8).Select(i => P(15 + i, $"Fresnel α{i}", 1 - 2 * i, $"Fresnel coefficient on {(axis.Length > 0 ? "y" : "r")}^{2 * i}.")))
            .ToArray();
        list.Add(new SurfaceTypeLayout("Extended Fresnel", "OpticStudio 2025 R2 help, \"Extended Fresnel\", table Parameter Definitions for Extended Fresnel Surfaces",
            FresnelPattern(""),
            v => new ExtendedFresnelShape(Trimmed(Range(v, 1, 8)), v(14), v(15), Range(v, 16, Whole(v, 13))),
            s =>
            {
                var f = (ExtendedFresnelShape)s;
                return With(Numbered(f.SubstrateAlpha, 1).Concat(Numbered(f.FresnelAlpha, 16)).ToDictionary(k => k.Key, k => k.Value),
                    (13, f.FresnelAlpha.Count), (14, f.FresnelCurvature), (15, f.FresnelConic));
            }));
        list.Add(new SurfaceTypeLayout("Cylinder Fresnel", "OpticStudio 2025 R2 help, \"Cylinder Fresnel\", table Parameter Definitions for Cylinder Fresnel Surfaces",
            FresnelPattern("Y direction "),
            v => new CylinderFresnelShape(Trimmed(Range(v, 1, 8)), v(14), v(15), Range(v, 16, Whole(v, 13))),
            s =>
            {
                var f = (CylinderFresnelShape)s;
                return With(Numbered(f.SubstrateAlpha, 1).Concat(Numbered(f.FresnelAlpha, 16)).ToDictionary(k => k.Key, k => k.Value),
                    (13, f.FresnelAlpha.Count), (14, f.FresnelCurvature), (15, f.FresnelConic));
            }));

        // "Generalized Fresnel": 1–8 α1–α8 (substrate), 13 maximum term number (up to 230), 14
        // normalization radius, 15–244 the extended polynomial terms (coefficients in lens units,
        // GeneralizedFresnelShape).
        list.Add(new SurfaceTypeLayout("Generalized Fresnel", "OpticStudio 2025 R2 help, \"Generalized Fresnel\", table Parameter Definitions for Generalized Fresnel Surfaces",
            EvenTerms(" (substrate)")
                .Append(Count(13, "Max term #", 230, "Maximum term number of the extended polynomial."))
                .Append(P(14, "Norm radius", 1, "Normalization radius, lens units."))
                .Concat(Enumerable.Range(1, 230).Select(i => P(14 + i, $"A{i}", 1, $"Coefficient on extended polynomial term {i}, lens units.")))
                .ToArray(),
            v => new GeneralizedFresnelShape(Trimmed(Range(v, 1, 8)), v(14), Range(v, 15, Whole(v, 13))),
            s =>
            {
                var g = (GeneralizedFresnelShape)s;
                return With(Numbered(g.SubstrateAlpha, 1).Concat(Numbered(g.Terms, 15)).ToDictionary(k => k.Key, k => k.Value),
                    (13, g.Terms.Count), (14, g.NormalizationRadius));
            }));

        // "ABCD": parameters 1–8 are Ax, Bx, Cx, Dx, Ay, By, Cy, Dy.
        list.Add(new SurfaceTypeLayout("ABCD", "OpticStudio 2025 R2 help, \"ABCD\", table Parameter Definitions for ABCD Surfaces",
            new[]
            {
                P(1, "Ax", 0), P(2, "Bx", 1, "Lens units."), P(3, "Cx", -1, "Inverse lens units."), P(4, "Dx", 0),
                P(5, "Ay", 0), P(6, "By", 1, "Lens units."), P(7, "Cy", -1, "Inverse lens units."), P(8, "Dy", 0),
            },
            v => new AbcdShape(new ParaxialMatrix(v(1), v(2), v(3), v(4)), new ParaxialMatrix(v(5), v(6), v(7), v(8))),
            s =>
            {
                var a = (AbcdShape)s;
                return new Dictionary<int, double>
                {
                    [1] = a.X.A, [2] = a.X.B, [3] = a.X.C, [4] = a.X.D, [5] = a.Y.A, [6] = a.Y.B, [7] = a.Y.C, [8] = a.Y.D,
                };
            })
        { UsesRadiusAndConic = false });

        // "Conjugate": parameters 1–6 are x1, y1, z1, x2, y2, z2.
        list.Add(new SurfaceTypeLayout("Conjugate", "OpticStudio 2025 R2 help, \"Conjugate\", table Parameter Definitions for Conjugate Surfaces",
            new[] { P(1, "x1", 1), P(2, "y1", 1), P(3, "z1", 1), P(4, "x2", 1), P(5, "y2", 1), P(6, "z2", 1) },
            v => new ConjugateShape(new Vector3D(v(1), v(2), v(3)), new Vector3D(v(4), v(5), v(6))),
            s =>
            {
                var c = (ConjugateShape)s;
                return new Dictionary<int, double>
                {
                    [1] = c.First.X, [2] = c.First.Y, [3] = c.First.Z, [4] = c.Second.X, [5] = c.Second.Y, [6] = c.Second.Z,
                };
            })
        { UsesRadiusAndConic = false });

        // "Paraxial": 1 focal length, 2 OPD mode.
        list.Add(new SurfaceTypeLayout("Paraxial", "OpticStudio 2025 R2 help, \"Paraxial (sequential surfaces, lens data editor)\", table Parameter Definitions for Paraxial Surfaces",
            new[]
            {
                P(1, "Focal length", 1, "The focal length in air, lens units; 0 is no power."),
                new SurfaceParameterInfo(2, "OPD mode", 0) { IsInteger = true, Range = (0, 3), Description = "2: infinite conjugates; 3: paraxial conjugates. Modes 0 and 1 are not built." },
            },
            v =>
            {
                int mode = Whole(v, 2);
                if (mode is 0 or 1)
                    throw new ArgumentException($"Paraxial: OPD mode {mode} is not built in this program; use 2 (infinite conjugates) or 3 (paraxial conjugates).");
                return new ParaxialShape(v(1));
            },
            s =>
            {
                var p = (ParaxialShape)s;
                return new Dictionary<int, double> { [1] = p.FocalLength, [2] = double.IsInfinity(p.ObjectDistance) ? 2 : 3 };
            })
        { UsesRadiusAndConic = false });

        // "Paraxial XY": 1 X power, 2 Y power (inverse lens units).
        list.Add(new SurfaceTypeLayout("Paraxial XY", "OpticStudio 2025 R2 help, \"Paraxial XY\", table Parameter Definitions for Paraxial XY Surfaces",
            new[] { P(1, "X power", -1, "Inverse lens units."), P(2, "Y power", -1, "Inverse lens units.") },
            v => new ParaxialXYShape(v(1), v(2)),
            s =>
            {
                var p = (ParaxialXYShape)s;
                return new Dictionary<int, double> { [1] = p.PowerX, [2] = p.PowerY };
            })
        { UsesRadiusAndConic = false });

        // "Retro Reflect": "This surface uses no parameter values."
        list.Add(new SurfaceTypeLayout("Retro Reflect", "OpticStudio 2025 R2 help, \"Retro Reflect\"",
            Array.Empty<SurfaceParameterInfo>(),
            _ => new RetroReflectShape(),
            _ => new Dictionary<int, double>())
        { UsesRadiusAndConic = false });

        // "Coordinate Break": 1 decenter X, 2 decenter Y, 3 tilt about X, 4 tilt about Y, 5 tilt
        // about Z, 6 order. Decenters in lens units, tilts in degrees.
        list.Add(new SurfaceTypeLayout(CoordinateBreak, "OpticStudio 2025 R2 help, \"Coordinate Break\", table Parameter Definitions for Coordinate Break Surfaces",
            new[]
            {
                P(1, "Decenter X", 1, "Lens units."),
                P(2, "Decenter Y", 1, "Lens units."),
                new SurfaceParameterInfo(3, "Tilt About X", 0) { IsAngle = true, Description = "Degrees, right-handed about +x." },
                new SurfaceParameterInfo(4, "Tilt About Y", 0) { IsAngle = true, Description = "Degrees, right-handed about the new +y." },
                new SurfaceParameterInfo(5, "Tilt About Z", 0) { IsAngle = true, Description = "Degrees, right-handed about the new +z." },
                P(6, "Order", 0, "0: decenter, then tilt about x, y, z; any other value: tilt about z, y, x, then decenter."),
            },
            _ => null,
            _ => throw new InvalidOperationException("A coordinate break has no shape."))
        { UsesRadiusAndConic = false, IsCoordinateBreak = true });

        return list.ToArray();
    }
}
