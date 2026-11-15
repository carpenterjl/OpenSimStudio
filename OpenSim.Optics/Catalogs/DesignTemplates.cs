using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Catalogs;

// OP-16: design templates (docs/Optics-Conventions.md section 53).

/// <summary>One input of a design template.</summary>
/// <param name="Name">The input's name, the key of the values passed to <see cref="DesignTemplate.Build"/>.</param>
/// <param name="Unit">Its unit ("mm", "°") or empty.</param>
/// <param name="Default">The value used when none is given, as text.</param>
/// <param name="Description">What it sets.</param>
public sealed record DesignTemplateParameter(string Name, string Unit, string Default, string Description);

/// <summary>
/// A starting design built from a few inputs (OpticStudio's Design Templates tool: "ready made
/// design starting points, with the ability to filter on any of their parameters"; OpticStudio
/// 2025 R2 help, Libraries tab › Design Templates Group › Design Templates;
/// plans/sources/optics/op-16-help/Design_Templates.html). Every template here is this
/// program's own prescription, derived in closed form from first-order optics (section 53); none
/// is copied from a book, a patent or another program.
/// </summary>
public sealed class DesignTemplate
{
    private readonly Func<Inputs, IReadOnlyList<OpticalCatalog>, List<string>, OpticsSettings> _build;

    internal DesignTemplate(string id, string name, string kind, int elements, string description,
        IReadOnlyList<DesignTemplateParameter> parameters, Func<Inputs, IReadOnlyList<OpticalCatalog>, List<string>, OpticsSettings> build)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Elements = elements;
        Description = description;
        Parameters = parameters;
        _build = build;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>"Singlet", "Doublet", "Mirror" or "Afocal telescope".</summary>
    public string Kind { get; }

    public int Elements { get; }
    public string Description { get; }
    public IReadOnlyList<DesignTemplateParameter> Parameters { get; }

    /// <summary>
    /// The design as an optics record (millimeters, the F, d and C lines with d primary, an
    /// entrance-pupil aperture and angle fields 0 and the half-field). <paramref name="values"/>
    /// override the defaults by parameter name (case ignored); a glass input is a glass name in
    /// <paramref name="catalogs"/> or, where the template allows, a number (a constant index).
    /// Throws <see cref="ArgumentException"/> naming the template and the input when a value is
    /// not a number, is out of range, names a glass not loaded, or the design does not exist.
    /// </summary>
    public DesignTemplateResult Build(IReadOnlyDictionary<string, string>? values, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        if (values is not null)
            foreach (var key in values.Keys)
                if (!Parameters.Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"{Name}: there is no input '{key}' (the inputs are {string.Join(", ", Parameters.Select(p => p.Name))}).");
        var notes = new List<string>();
        var optics = _build(new Inputs(this, values), catalogs, notes);
        return new DesignTemplateResult(optics, notes);
    }

    /// <summary>The template's inputs, read by name.</summary>
    internal sealed class Inputs
    {
        private readonly DesignTemplate _t;
        private readonly IReadOnlyDictionary<string, string>? _values;

        public Inputs(DesignTemplate t, IReadOnlyDictionary<string, string>? values) { _t = t; _values = values; }

        public string Text(string name)
        {
            var p = _t.Parameters.First(q => q.Name == name);
            if (_values is not null)
                foreach (var (k, v) in _values)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            return p.Default;
        }

        public double Number(string name)
        {
            string text = Text(name);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                throw new ArgumentException($"{_t.Name}: the input '{name}' ('{text}') is not a number.");
            return v;
        }

        public double Positive(string name)
        {
            double v = Number(name);
            if (!(v > 0)) throw new ArgumentException(FormattableString.Invariant($"{_t.Name}: the input '{name}' must be above zero (it is {v:G10})."));
            return v;
        }

        public double NonZero(string name)
        {
            double v = Number(name);
            if (v == 0) throw new ArgumentException($"{_t.Name}: the input '{name}' must not be zero.");
            return v;
        }

        public string Template => _t.Name;
    }
}

/// <summary>A design built from a template, and what the user should know about it.</summary>
public sealed record DesignTemplateResult(OpticsSettings Optics, IReadOnlyList<string> Notes);

/// <summary>
/// The design templates (section 53). Closed forms used (each derived from the two-surface
/// paraxial y–nu trace of section 11, not copied): a thick lens of index n, curvatures c₁, c₂ and
/// thickness t in air has power φ = (n − 1)(c₁ − c₂) + (n − 1)² t c₁ c₂ / n; with c₂ = 0 this is
/// φ = (n − 1)c₁ for any t; with c₂ = −c₁ it is the quadratic (n − 1)² t c²/n − 2(n − 1)c + φ = 0,
/// whose root that tends to φ / (2(n − 1)) as t → 0 is c = n(1 − √(1 − tφ/n)) / ((n − 1)t). A
/// thin cemented pair is free of first-order axial colour when φ₁/V₁ + φ₂/V₂ = 0 with
/// φ₁ + φ₂ = φ, so φ₁ = φV₁/(V₁ − V₂) and φ₂ = −φV₂/(V₁ − V₂), V = (n_d − 1)/(n_F − n_C). A
/// paraboloid (conic −1) of radius −2f images an axial point at infinity at its focus f. Two
/// ideal (paraxial) lenses of focal lengths f₁ and f₂ separated by f₁ + f₂ are afocal with
/// angular magnification −f₁/f₂.
/// </summary>
public static class DesignTemplates
{
    private const string FieldHelp = "The largest field angle; the fields are 0 and this angle.";

    public static IReadOnlyList<DesignTemplate> All { get; } = Create();

    /// <summary>The template with that id (case ignored), or null.</summary>
    public static DesignTemplate? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The templates whose id, name, kind or description contain <paramref name="text"/>
    /// (case ignored, null or blank for all) and, when given, have <paramref name="elements"/> elements.</summary>
    public static IReadOnlyList<DesignTemplate> Filter(string? text, int? elements = null, string? kind = null) => All.Where(t =>
        (string.IsNullOrWhiteSpace(text) || new[] { t.Id, t.Name, t.Kind, t.Description }.Any(s => s.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)))
        && (elements is not int n || t.Elements == n)
        && (string.IsNullOrWhiteSpace(kind) || string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase))).ToArray();

    private static IReadOnlyList<DesignTemplate> Create() => new[]
    {
        new DesignTemplate("plano-singlet", "Plano singlet", "Singlet", 1,
            "A plano-convex (or, with a negative focal length, plano-concave) singlet, curved side first, stop on its front; R₁ = (n − 1)f gives the focal length exactly for any thickness. The back distance is a marginal-ray-height solve (the paraxial focus).",
            new[]
            {
                new DesignTemplateParameter("Focal length", "mm", "100", "The effective focal length at the d line."),
                new DesignTemplateParameter("Entrance pupil", "mm", "10", "The entrance pupil diameter."),
                new DesignTemplateParameter("Half field", "°", "1", FieldHelp),
                new DesignTemplateParameter("Thickness", "mm", "4", "The centre thickness."),
                new DesignTemplateParameter("Glass", "", "SiO2-Malitson", "A glass name in the loaded catalogs, or a number for a constant index."),
            },
            (v, catalogs, notes) =>
            {
                double f = v.NonZero("Focal length"), t = v.Positive("Thickness");
                var (glass, n) = Glass(v, "Glass", catalogs, allowIndex: true);
                var surfaces = new List<OpticsSurfaceSettings>
                {
                    new() { ThicknessIsInfinite = true },
                    Medium(new OpticsSurfaceSettings { Radius = (n - 1) * f, Thickness = t, IsStop = true }, glass),
                    new() { Thickness = 0 },
                    new(),
                };
                return Finish(v, catalogs, surfaces, notes);
            }),

        new DesignTemplate("equi-singlet", "Equi singlet", "Singlet", 1,
            "An equi-convex (or, with a negative focal length, equi-concave) singlet, stop on its front, with the curvature from the exact thick-lens power. The back distance is a marginal-ray-height solve.",
            new[]
            {
                new DesignTemplateParameter("Focal length", "mm", "100", "The effective focal length at the d line."),
                new DesignTemplateParameter("Entrance pupil", "mm", "10", "The entrance pupil diameter."),
                new DesignTemplateParameter("Half field", "°", "1", FieldHelp),
                new DesignTemplateParameter("Thickness", "mm", "4", "The centre thickness."),
                new DesignTemplateParameter("Glass", "", "SiO2-Malitson", "A glass name in the loaded catalogs, or a number for a constant index."),
            },
            (v, catalogs, notes) =>
            {
                double f = v.NonZero("Focal length"), t = v.Positive("Thickness");
                var (glass, n) = Glass(v, "Glass", catalogs, allowIndex: true);
                double phi = 1 / f, root = 1 - t * phi / n;
                if (!(root > 0))
                    throw new ArgumentException(FormattableString.Invariant($"{v.Template}: no equi-convex lens of index {n:G8} and thickness {t:G8} mm has a focal length of {f:G8} mm (t/n must be below the focal length)."));
                double c = n * (1 - Math.Sqrt(root)) / ((n - 1) * t);
                var surfaces = new List<OpticsSurfaceSettings>
                {
                    new() { ThicknessIsInfinite = true },
                    Medium(new OpticsSurfaceSettings { Radius = 1 / c, Thickness = t, IsStop = true }, glass),
                    new() { Radius = -1 / c, Thickness = 0 },
                    new(),
                };
                return Finish(v, catalogs, surfaces, notes);
            }),

        new DesignTemplate("cemented-achromat", "Cemented achromat", "Doublet", 2,
            "A cemented doublet, equi-convex crown first, with the thin-lens power split that cancels first-order axial colour between the F and C lines; the whole lens is then scaled with Make Focal to the focal length (thicknesses scale by the same small factor). Stop on the front; the back distance is a marginal-ray-height solve.",
            new[]
            {
                new DesignTemplateParameter("Focal length", "mm", "100", "The effective focal length at the d line."),
                new DesignTemplateParameter("Entrance pupil", "mm", "10", "The entrance pupil diameter."),
                new DesignTemplateParameter("Half field", "°", "1", FieldHelp),
                new DesignTemplateParameter("Crown thickness", "mm", "4", "The first element's centre thickness."),
                new DesignTemplateParameter("Flint thickness", "mm", "2", "The second element's centre thickness."),
                new DesignTemplateParameter("Crown glass", "", "poly_methyl_methacrylate-Sultanova", "The first element's glass (a name in the loaded catalogs)."),
                new DesignTemplateParameter("Flint glass", "", "polystyrene-Sultanova", "The second element's glass (a name in the loaded catalogs)."),
            },
            (v, catalogs, notes) =>
            {
                double f = v.NonZero("Focal length"), t1 = v.Positive("Crown thickness"), t2 = v.Positive("Flint thickness");
                var (g1, n1) = Glass(v, "Crown glass", catalogs, allowIndex: false);
                var (g2, n2) = Glass(v, "Flint glass", catalogs, allowIndex: false);
                double v1 = Abbe(v, "Crown glass", catalogs), v2 = Abbe(v, "Flint glass", catalogs);
                if (Math.Abs(v1 - v2) < 1e-9 * Math.Max(v1, v2))
                    throw new ArgumentException($"{v.Template}: the two glasses have the same Abbe number, so no power split cancels the colour.");
                double phi = 1 / f, phi1 = phi * v1 / (v1 - v2), phi2 = -phi * v2 / (v1 - v2);
                double c1 = phi1 / (2 * (n1 - 1)), c2 = -c1, c3 = c2 - phi2 / (n2 - 1);
                notes.Add(FormattableString.Invariant($"Abbe numbers {v1:G6} (crown) and {v2:G6} (flint) from the catalogs at the d, F and C lines; thin-lens element powers {phi1 * f:G6}φ and {phi2 * f:G6}φ."));
                var surfaces = new List<OpticsSurfaceSettings>
                {
                    new() { ThicknessIsInfinite = true },
                    Medium(new OpticsSurfaceSettings { Radius = 1 / c1, Thickness = t1, IsStop = true }, g1),
                    Medium(new OpticsSurfaceSettings { Radius = 1 / c2, Thickness = t2 }, g2),
                    new() { Radius = c3 == 0 ? null : 1 / c3, Thickness = 0 },
                    new(),
                };
                var system = SystemData(v);
                var scaled = MakeFocal.Apply(surfaces, f, new LensEditContext(system, catalogs));
                notes.AddRange(scaled.Notes);
                return Finish(v, catalogs, scaled.Surfaces.ToList(), notes);
            }),

        new DesignTemplate("parabolic-mirror", "Parabolic mirror", "Mirror", 1,
            "A concave paraboloid (radius −2f, conic −1), stop on the mirror, for an object at infinity; the image distance (negative after the mirror, section 3) is a marginal-ray-height solve.",
            new[]
            {
                new DesignTemplateParameter("Focal length", "mm", "500", "The focal length (a positive number)."),
                new DesignTemplateParameter("Entrance pupil", "mm", "50", "The entrance pupil diameter."),
                new DesignTemplateParameter("Half field", "°", "0.1", FieldHelp),
            },
            (v, catalogs, notes) =>
            {
                double f = v.Positive("Focal length");
                var surfaces = new List<OpticsSurfaceSettings>
                {
                    new() { ThicknessIsInfinite = true },
                    new() { Radius = -2 * f, Conic = -1, Material = SavedSurfaces.Mirror, IsStop = true, Thickness = 0 },
                    new(),
                };
                return Finish(v, catalogs, surfaces, notes, imageGapSurface: 1);
            }),

        Telescope("keplerian-telescope", "Keplerian telescope", "200", "25",
            "Two ideal (paraxial) lenses of positive focal lengths, separated by the sum of the focal lengths: an afocal telescope with an inverted image and angular magnification −f₁/f₂. Stop on the objective; the eyepiece is followed by an eye relief of one eyepiece focal length."),
        Telescope("galilean-telescope", "Galilean telescope", "200", "-50",
            "An ideal positive objective and an ideal negative eyepiece separated by the sum of the focal lengths: an afocal telescope with an erect image and angular magnification −f₁/f₂. Stop on the objective; the eyepiece is followed by an eye relief of the eyepiece focal length's magnitude."),
    };

    private static DesignTemplate Telescope(string id, string name, string f1Default, string f2Default, string description) =>
        new(id, name, "Afocal telescope", 2, description,
            new[]
            {
                new DesignTemplateParameter("Objective focal length", "mm", f1Default, "The first lens's focal length (positive)."),
                new DesignTemplateParameter("Eyepiece focal length", "mm", f2Default, "The second lens's focal length."),
                new DesignTemplateParameter("Entrance pupil", "mm", "20", "The entrance pupil diameter."),
                new DesignTemplateParameter("Half field", "°", "1", FieldHelp),
            },
            (v, catalogs, notes) =>
            {
                double f1 = v.Positive("Objective focal length"), f2 = v.NonZero("Eyepiece focal length");
                if (id == "keplerian-telescope" && f2 < 0)
                    throw new ArgumentException($"{v.Template}: the eyepiece focal length must be positive (a negative one is the Galilean telescope).");
                if (id == "galilean-telescope" && f2 > 0)
                    throw new ArgumentException($"{v.Template}: the eyepiece focal length must be negative (a positive one is the Keplerian telescope).");
                if (!(f1 + f2 > 0))
                    throw new ArgumentException(FormattableString.Invariant($"{v.Template}: the separation f₁ + f₂ = {f1 + f2:G8} mm must be positive."));
                static OpticsSurfaceSettings Ideal(double focal, double gap, bool stop) => new()
                {
                    Type = "Paraxial", Thickness = gap, IsStop = stop,
                    Parameters = new() { new() { Number = 1, Value = focal }, new() { Number = 2, Value = 2 } },
                };
                var surfaces = new List<OpticsSurfaceSettings>
                {
                    new() { ThicknessIsInfinite = true },
                    Ideal(f1, f1 + f2, true),
                    Ideal(f2, Math.Abs(f2), false),
                    new(),
                };
                notes.Add(FormattableString.Invariant($"Angular magnification −f₁/f₂ = {-f1 / f2:G8}."));
                return new OpticsSettings
                {
                    Mode = OpticsModes.SavedName(OpticsMode.Sequential),
                    SystemData = SystemData(v) with { AfocalImageSpace = true },
                    Surfaces = surfaces,
                };
            });

    /// <summary>The template's system data: title, millimeters, entrance pupil, angle fields 0 and
    /// the half field, the F, d and C lines (<see cref="SpectralLines"/>) with d primary.</summary>
    private static OpticsSystemSettings SystemData(DesignTemplate.Inputs v)
    {
        double epd = v.Positive("Entrance pupil");
        double field = v.Number("Half field");
        return new OpticsSystemSettings
        {
            Title = v.Template,
            LensUnit = ConventionNames.LensUnits.SavedName(LensUnit.Millimeter),
            ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
            ApertureValue = epd,
            FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
            Fields = field == 0 ? new() { new() } : new() { new(), new() { Y = field } },
            Wavelengths = new[] { SpectralLines.F, SpectralLines.LowerD, SpectralLines.C }
                .Select(l => new OpticsWavelengthSettings { Micrometers = l.Micrometers }).ToList(),
            PrimaryWavelength = 2,
        };
    }

    /// <summary>
    /// The record with the system data, and the gap before the image (after surface
    /// <paramref name="imageGapSurface"/>, the last lens surface by default) set to the paraxial
    /// focus with a marginal-ray-height solve (target 0, pupil zone 0) holding it there.
    /// </summary>
    private static OpticsSettings Finish(DesignTemplate.Inputs v, IReadOnlyList<OpticalCatalog> catalogs, List<OpticsSurfaceSettings> surfaces,
        List<string> notes, int? imageGapSurface = null)
    {
        var system = SystemData(v);
        int k = imageGapSurface ?? surfaces.Count - 2;
        var context = new LensEditContext(system, catalogs);
        var model = ParaxialSystem.Create(context.ToLens(surfaces).ToParaxialLens(), context.System());
        if (model.BackFocalLength is not double bfl)
            throw new ArgumentException($"{v.Template}: the design is afocal, so it has no focus to put the image at.");
        var solve = new OpticsCellFlagSettings
        {
            Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.SolveFlag, Solve = "Marginal ray height", SolveParameters = new() { 0, 0 },
        };
        surfaces[k] = SurfaceFlags.With(surfaces[k] with { Thickness = bfl / OpticsConventions.MetersPer(context.Unit) }, SavedSurfaces.ThicknessColumn, solve);
        return new OpticsSettings
        {
            Mode = OpticsModes.SavedName(OpticsMode.Sequential),
            SystemData = system,
            Surfaces = surfaces,
        };
    }

    private static OpticsSurfaceSettings Medium(OpticsSurfaceSettings s, (string? Name, double? Index) glass) =>
        glass.Name is not null ? s with { Material = glass.Name } : s with { Index = glass.Index };

    /// <summary>The glass input: a catalog glass (its name and d-line index) or, when allowed, a
    /// constant index.</summary>
    private static ((string? Name, double? Index) Glass, double Nd) Glass(DesignTemplate.Inputs v, string input, IReadOnlyList<OpticalCatalog> catalogs, bool allowIndex)
    {
        string text = v.Text(input);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double index))
        {
            if (!allowIndex) throw new ArgumentException($"{v.Template}: the input '{input}' needs a glass name (its dispersion is used), not a number.");
            if (!(index > 1) || !double.IsFinite(index))
                throw new ArgumentException(FormattableString.Invariant($"{v.Template}: the constant index '{input}' must be above 1 (it is {index:G10})."));
            return ((null, index), index);
        }
        var material = Material(v, input, catalogs);
        return ((text, null), Index(v, input, material, SpectralLines.LowerD.Micrometers));
    }

    private static OpticalMaterial Material(DesignTemplate.Inputs v, string input, IReadOnlyList<OpticalCatalog> catalogs) =>
        ProjectLens.FindGlass(v.Text(input), catalogs)
        ?? throw new ArgumentException($"{v.Template}: the glass '{v.Text(input)}' ({input}) is not in the loaded catalogs ({string.Join(", ", catalogs.Select(c => c.Name))}).");

    private static double Abbe(DesignTemplate.Inputs v, string input, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var m = Material(v, input, catalogs);
        double nd = Index(v, input, m, SpectralLines.LowerD.Micrometers);
        double nf = Index(v, input, m, SpectralLines.F.Micrometers);
        double nc = Index(v, input, m, SpectralLines.C.Micrometers);
        return (nd - 1) / (nf - nc);
    }

    private static double Index(DesignTemplate.Inputs v, string input, OpticalMaterial m, double micrometers)
    {
        try { return m.RelativeIndex(micrometers); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new ArgumentException($"{v.Template}: {input}: {ex.Message}", ex); }
    }
}
