// OP-44
using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// The cardinal planes of a range of surfaces (docs/Optics-Conventions.md §29): object-space
/// positions measured from the first surface's vertex, image-space positions from the last
/// surface's, along z, meters; null for a plane at infinity. Null throughout when the range is
/// afocal.
/// </summary>
public sealed record CardinalPlanes
{
    public required int FirstSurface { get; init; }
    public required int LastSurface { get; init; }
    public required double WavelengthMicrometers { get; init; }

    /// <summary>The signed indices before the first and after the last surface (−n after an odd
    /// number of mirrors, docs/Optics-Conventions.md §11).</summary>
    public required double ObjectIndex { get; init; }
    public required double ImageIndex { get; init; }

    /// <summary>The range's matrix on (y, n·u) from just before the first to just after the last surface.</summary>
    public required ParaxialMatrix Matrix { get; init; }

    public bool IsAfocal { get; init; }

    /// <summary>Power −C, 1/m.</summary>
    public double Power { get; init; }

    public double? EffectiveFocalLength { get; init; }
    public double? ObjectFocalLength { get; init; }
    public double? ImageFocalLength { get; init; }

    public (double Object, double Image)? Focal { get; init; }
    public (double Object, double Image)? Principal { get; init; }
    public (double Object, double Image)? AntiPrincipal { get; init; }
    public (double Object, double Image)? Nodal { get; init; }
    public (double Object, double Image)? AntiNodal { get; init; }

    /// <summary>
    /// The planes of surfaces <paramref name="first"/> to <paramref name="last"/> of a paraxial
    /// model. With M = [[A, B], [C, D]] the range's matrix on (y, ν), n and n′ the signed indices
    /// before and after it, an object plane at z (from the first vertex) and its image at z′
    /// (from the last) are conjugate with lateral magnification m when
    /// <code>
    /// z′ = n′(m − A)/C,   z = n(D − 1/m)/C
    /// </code>
    /// (derived: T(z′/n′)·M·T(−z/n) has B = 0, its A is m and its D is 1/m since the determinant is
    /// 1). The angular magnification of such a pair is n/(n′m). So the principal planes are m = +1,
    /// the anti-principal m = −1, the nodal m = n/n′ (angular +1), the anti-nodal m = −n/n′
    /// (angular −1), and the focal planes m → 0 (image side, z′ = −n′A/C) and m → ∞ (object side,
    /// z = nD/C). These reproduce <see cref="ParaxialSystem"/>'s focal, principal and nodal
    /// positions for the whole lens.
    /// </summary>
    public static CardinalPlanes Compute(ParaxialSystem model, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(model);
        var m = model.Matrix(first, last);
        double n = model.SignedIndices[first - 1], np = model.SignedIndices[last];
        double span = 0;
        for (int j = first; j < last; j++) span += Math.Abs(model.Lens.Surfaces[j].Thickness / model.SignedIndices[j]);
        // Afocal when C is zero to rounding: |C|·(a length of the range) is a rounding error of
        // the dimensionless entries.
        double scale = Math.Max(span, Math.Abs(m.B));
        bool afocal = m.C == 0 || (scale > 0 && Math.Abs(m.C) * scale <= 1e-12 * (Math.Abs(m.A) + Math.Abs(m.D) + 1));
        var planes = new CardinalPlanes
        {
            FirstSurface = first, LastSurface = last, WavelengthMicrometers = model.WavelengthMicrometers,
            ObjectIndex = n, ImageIndex = np, Matrix = m, IsAfocal = afocal, Power = -m.C,
        };
        if (afocal) return planes;
        double c = m.C;
        (double, double) Conjugate(double mag) => (n * (m.D - 1 / mag) / c, np * (mag - m.A) / c);
        return planes with
        {
            EffectiveFocalLength = -1 / c,
            ObjectFocalLength = n / c,
            ImageFocalLength = -np / c,
            Focal = (n * m.D / c, -np * m.A / c),
            Principal = Conjugate(1),
            AntiPrincipal = Conjugate(-1),
            Nodal = Conjugate(n / np),
            AntiNodal = Conjugate(-n / np),
        };
    }

    /// <summary>The planes as a report section, lengths in <paramref name="unit"/>.</summary>
    public ReportSection ToSection(LensUnit unit, string heading = "Cardinal points")
    {
        string u = ParaxialReport.UnitName(unit);
        string L(double meters) => StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g10");
        var pairs = new List<(string, string)>
        {
            ("Surfaces", $"{FirstSurface} to {LastSurface}"),
            ("Wavelength", $"{StudyReport.Number(WavelengthMicrometers)} µm"),
            ("Object space index", StudyReport.Number(ObjectIndex, "g10")),
            ("Image space index", StudyReport.Number(ImageIndex, "g10")),
        };
        if (IsAfocal)
            return new ReportSection(heading)
            {
                Pairs = pairs,
                Lines = new[] { "The surfaces are afocal (no power), so they have no focal, principal, anti-principal, nodal or anti-nodal planes." }
            };
        pairs.Add(("Effective focal length", $"{L(EffectiveFocalLength!.Value)} {u}"));
        pairs.Add(("Object space focal length", $"{L(ObjectFocalLength!.Value)} {u}"));
        pairs.Add(("Image space focal length", $"{L(ImageFocalLength!.Value)} {u}"));
        IReadOnlyList<string> Row(string name, (double O, double I)? p) => new[] { name, L(p!.Value.O), L(p.Value.I) };
        return new ReportSection(heading)
        {
            Pairs = pairs,
            TableHeaders = new[] { "Plane", $"Object space ({u}, from surface {FirstSurface})", $"Image space ({u}, from surface {LastSurface})" },
            TableRows = new[]
            {
                Row("Focal", Focal), Row("Principal", Principal), Row("Anti-principal", AntiPrincipal),
                Row("Nodal", Nodal), Row("Anti-nodal", AntiNodal),
            },
            Lines = new[]
            {
                "Positions along z from the named vertex, positive toward +z (negative after an odd number of mirrors); principal: lateral magnification +1, anti-principal −1, nodal: angular magnification +1, anti-nodal −1.",
            }
        };
    }
}

/// <summary>Settings of the <see cref="CardinalPointsAnalysis"/> (OpticStudio's First Surface, Last Surface, Wavelength).</summary>
public sealed record CardinalPointsSettings
{
    [DisplayName("First surface")]
    [Description("The first surface of the range; object-space planes are measured from its vertex.")]
    public int FirstSurface { get; init; } = 1;

    /// <summary>The last surface; 0 for the last surface before the image.</summary>
    [DisplayName("Last surface")]
    [Description("The last surface of the range, 0 for the last surface before the image; image-space planes are measured from its vertex, in the medium after it.")]
    public int LastSurface { get; init; }

    /// <summary>The wavelength row (1 = the first); 0 is the primary wavelength.</summary>
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
}

/// <summary>
/// OpticStudio's Cardinal Points report (2025 R2 help, "Cardinal Points (Reports group, the
/// Analyze tab, sequential ui mode)"): for a range of surfaces at one wavelength, the focal,
/// principal, anti-principal, nodal and anti-nodal planes, object-space ones from the first
/// surface and image-space ones from the last, with the material after the last surface
/// setting the image-space index. From the paraxial engine (<see cref="ParaxialSystem"/>);
/// the Orientation setting (YZ or XZ) is not built: the paraxial engine traces the y meridian.
/// </summary>
public sealed class CardinalPointsAnalysis : OpticsAnalysis<CardinalPointsSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "CardinalPoints";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    public CardinalPointsAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "Cardinal points";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, CardinalPointsSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return ReportText.NoSurfaces(Name);
        int lastSurface = lens.LastSurfaceIndex;
        int last = settings.LastSurface == 0 ? lastSurface : settings.LastSurface;
        if (settings.FirstSurface < 1 || settings.FirstSurface > lastSurface)
            throw new ArgumentException($"Cardinal points: the first surface must be 1 to {lastSurface} (it is {settings.FirstSurface}).");
        if (last < settings.FirstSurface || last > lastSurface)
            throw new ArgumentException($"Cardinal points: the last surface must be 0 (the last surface) or {settings.FirstSurface} to {lastSurface} (it is {settings.LastSurface}).");
        int row = ReportText.WavelengthRow("Cardinal points", settings.Wavelength, system);
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), system, row);
        cancellation.ThrowIfCancellationRequested();
        var planes = CardinalPlanes.Compute(model, settings.FirstSurface, last);
        var assumptions = new List<string>
        {
            "First-order (paraxial) optics of the range: surfaces are their vertex curvatures (or a special surface's paraxial equivalent).",
            "Object-space planes are measured from the first surface, image-space planes from the last; the material after the last surface is the image-space medium (OpticStudio 2025 R2 help, \"Cardinal Points\").",
            "Coordinate breaks and tilts must only fold the axis (docs/Optics-Conventions.md §15); a lens that is not axial is refused by the paraxial engine.",
            "Only the y-z (YZ) orientation is computed; OpticStudio's XZ orientation is not built.",
            model.System.Environment.AirModelDescription,
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, new[] { planes.ToSection(system.Units.LensUnit) }, assumptions);
    }
}
