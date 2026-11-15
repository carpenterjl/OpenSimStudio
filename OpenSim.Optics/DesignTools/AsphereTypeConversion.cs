using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics.Optimization;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.DesignTools;

// OP-61: Convert Asphere Types (docs/Optics-Conventions.md §59).

/// <summary>The asphere forms Convert Asphere Types converts to: "the even, extended, and Q-type
/// (Forbes) aspheres are supported" (OpticStudio 2025 R2 help, "Convert Asphere Types";
/// plans/sources/optics/aspheres/Convert_Asphere_Types.html). The Q-type's two kinds are two targets.</summary>
public enum AsphereTarget
{
    EvenAsphere,
    ExtendedAsphere,
    QTypeQbfs,
    QTypeQcon,
}

/// <summary>Settings of <see cref="AsphereTypeConversion.Run"/>: the help's Surface, Asphere Type
/// and Number of Terms.</summary>
public sealed record AsphereTypeConversionOptions
{
    /// <summary>The surface to convert (0-based numbering of the Lens Data Editor).</summary>
    public required int Surface { get; init; }

    public AsphereTarget Target { get; init; } = AsphereTarget.QTypeQcon;

    /// <summary>The number of terms, or null for the help's "automatic"
    /// (<see cref="AsphereTypeConversion.AutomaticTerms"/>).</summary>
    public int? Terms { get; init; }

    /// <summary>The help's rule: "If the radius or conic of the starting asphere are either
    /// non-zero or variable, then these parameters are also made variable in the fitting
    /// process". False keeps the curvature and conic as OP-23's fit does
    /// (<see cref="AsphereConversion"/>).</summary>
    public bool VaryRadiusAndConic { get; init; } = true;

    /// <summary>The fitting region and normalization radius in lens units; null takes the
    /// surface's clear semi-diameter ("The optical surface edge is used", help).</summary>
    public double? ApertureRadius { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>The outcome of a conversion. Lengths in lens units.</summary>
public sealed record AsphereTypeConversionResult
{
    /// <summary>The record with the converted surface.</summary>
    public required OpticsSettings Settings { get; init; }
    public required int Surface { get; init; }
    public required string FromType { get; init; }
    public required AsphereTarget Target { get; init; }
    public required int Terms { get; init; }
    public required double ApertureRadius { get; init; }
    /// <summary>The help's "residual RMS surface sag error in lens units", over the fitting samples.</summary>
    public required double RmsResidual { get; init; }
    public required double MaxResidual { get; init; }
    public double? RadiusBefore { get; init; }
    public double? RadiusAfter { get; init; }
    public double ConicBefore { get; init; }
    public double ConicAfter { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReportLines()
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Convert Asphere Types: surface {Surface}, {FromType} to {AsphereTypeConversion.Label(Target)}, {Terms} term{(Terms == 1 ? "" : "s")}.",
            string.Create(ci, $"Fitted over 0 ≤ r ≤ {ApertureRadius:G10} (the normalization radius where the form has one)."),
            string.Create(ci, $"Radius {Radius(RadiusBefore)} → {Radius(RadiusAfter)}; conic {ConicBefore:G10} → {ConicAfter:G10}."),
            string.Create(ci, $"Residual sag error: RMS {RmsResidual:G6}, largest {MaxResidual:G6} (lens units)."),
        };
        lines.AddRange(Notes);
        return lines;
    }

    private static string Radius(double? r) => r is double v ? v.ToString("G12", CultureInfo.InvariantCulture) : "infinity";
}

/// <summary>
/// Convert Asphere Types (plan item OP-61; OpticStudio 2025 R2 help, "Convert Asphere Types"):
/// "converts an aspheric surface from one type of asphere to another, and computes the
/// coefficients that best match the sag of the existing surface type" by "a least squares
/// fitting so that the new asphere provides the minimum RMS difference in the surface sag". The
/// linear fit is OP-23's <see cref="AsphereConversion"/> (Even, Qcon, Qbfs, and the Extended
/// Asphere added for this tool). On top of it, when the radius or conic is "non-zero or
/// variable" (help), the base curvature and conic are fitted too, by damped least squares
/// (<see cref="DampedLeastSquares"/>) on the sag residuals with the polynomial coefficients
/// solved linearly at every step (variable projection). The refined fit is kept only when it
/// lowers the RMS residual by more than <see cref="RefinementGain"/>; a curvature or conic held by
/// a solve or a pickup is not varied (the solve would undo it). The Qbfs's curvature is not an
/// independent unknown: it is the best-fit conic's through the vertex and the edge.
/// </summary>
public static class AsphereTypeConversion
{
    /// <summary>The automatic term count stops growing once the RMS residual is at most this
    /// fraction of the aperture radius (this program's choice; the help says only that
    /// OpticStudio "makes a reasonable guess").</summary>
    public const double AutomaticRelativeTolerance = 1e-9;

    /// <summary>The largest automatic term count of the Extended and Q-type targets (the Even
    /// Asphere's is its 8).</summary>
    public const int AutomaticMaximum = 20;

    /// <summary>A refined (curvature and conic varied) fit replaces the fixed one only when its
    /// RMS is below (1 − this) times the fixed fit's, so rounding never moves a radius.</summary>
    public const double RefinementGain = 1e-3;

    /// <summary>A fixed fit whose RMS residual is at most this fraction of the aperture radius is
    /// exact for every purpose (0.01 nm on a 10 mm aperture); the curvature and conic are then not
    /// fitted, so rounding noise never moves them.</summary>
    public const double ExactRelativeResidual = 1e-12;

    public static string Label(AsphereTarget target) => target switch
    {
        AsphereTarget.EvenAsphere => "Even Asphere",
        AsphereTarget.ExtendedAsphere => "Extended Asphere",
        AsphereTarget.QTypeQbfs => "Q-Type Asphere (Qbfs)",
        _ => "Q-Type Asphere (Qcon)",
    };

    /// <summary>The most terms a target holds (its type's parameter layout).</summary>
    public static int MaximumTerms(AsphereTarget target) => target switch
    {
        AsphereTarget.EvenAsphere => 8,
        AsphereTarget.ExtendedAsphere => 240,
        _ => 100,
    };

    /// <exception cref="ArgumentException">The surface is the object or image, not rotationally
    /// symmetric, or the term count or aperture is out of range.</exception>
    /// <exception cref="InvalidOperationException">The sag is not defined over the aperture.</exception>
    public static AsphereTypeConversionResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, AsphereTypeConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(options);
        var surfaces = optics.Surfaces ?? throw new ArgumentException("Convert Asphere Types: the lens has no surfaces.");
        int j = options.Surface;
        if (j < 1 || j > surfaces.Count - 2)
            throw new ArgumentException($"Convert Asphere Types: surface {j} is not a lens surface; choose 1 to {surfaces.Count - 2}.");
        var saved = surfaces[j];
        var unit = DesignToolSupport.Unit(optics);
        double perUnit = OpticsConventions.MetersPer(unit);
        var context = new LensEditContext(optics.SystemData, catalogs);
        var source = context.ToLens(surfaces).Surfaces[j];
        string fromType = source.Shape?.TypeName ?? SavedSurfaces.StandardType;
        if (source.Shape is not (null or RadialSagShape))
            throw new ArgumentException($"Convert Asphere Types: surface {j} is a {fromType} surface, which is not rotationally symmetric; only the Standard surface and the radial asphere forms convert.");
        double rhoUnits = options.ApertureRadius ?? ApertureTools.ClearSemiDiameters(surfaces, context)[j];
        if (!(rhoUnits > 0) || !double.IsFinite(rhoUnits))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Convert Asphere Types: surface {j}'s fitting radius is {rhoUnits}; give it a semi-diameter."));
        double rho = rhoUnits * perUnit;
        int max = MaximumTerms(options.Target);
        if (options.Terms is int t && (t < 1 || t > max))
            throw new ArgumentException($"Convert Asphere Types: {Label(options.Target)} takes 1 to {max} terms, not {t}.");

        var notes = new List<string>();
        bool HeldBy(string column) => saved.Flags?.Any(f => f.Column == column
            && (f.Kind == SavedSurfaces.SolveFlag || f.Kind == SavedSurfaces.PickupFlag)) == true;
        bool IsVariable(string column) => saved.Flags?.Any(f => f.Column == column && f.Kind == SavedSurfaces.VariableFlag) == true;
        bool varyC = options.VaryRadiusAndConic && options.Target != AsphereTarget.QTypeQbfs
            && (source.Curvature != 0 || IsVariable(SavedSurfaces.RadiusColumn));
        bool varyK = options.VaryRadiusAndConic && (source.Conic != 0 || IsVariable(SavedSurfaces.ConicColumn));
        if (varyC && HeldBy(SavedSurfaces.RadiusColumn)) { varyC = false; notes.Add("The radius is held by a solve or pickup, so it was kept in the fit."); }
        if (varyK && HeldBy(SavedSurfaces.ConicColumn)) { varyK = false; notes.Add("The conic is held by a solve or pickup, so it was kept in the fit."); }

        AsphereFitResult Best(int terms)
        {
            var fixedFit = Fit(source, options.Target, terms, rho, source.Curvature, source.Conic);
            if (!(varyC || varyK) || fixedFit.RmsResidual <= ExactRelativeResidual * rho) return fixedFit;
            var refined = Refine(source, options.Target, terms, rho, varyC, varyK, options.CancellationToken);
            return refined is not null && refined.RmsResidual < (1 - RefinementGain) * fixedFit.RmsResidual ? refined : fixedFit;
        }

        AsphereFitResult fit;
        int count;
        if (options.Terms is int fixedTerms)
        {
            count = fixedTerms;
            fit = Best(count);
        }
        else
        {
            count = Math.Min(max, AutomaticTerms(source, options.Target));
            int cap = options.Target == AsphereTarget.EvenAsphere ? 8 : Math.Max(count, AutomaticMaximum);
            fit = Best(count);
            while (fit.RmsResidual > AutomaticRelativeTolerance * rho && count < cap)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                count++;
                var next = Best(count);
                if (next.RmsResidual < fit.RmsResidual) fit = next;
                else { count--; break; }
            }
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Automatic: {count} term{(count == 1 ? "" : "s")} (from the source's order, raised while the RMS residual exceeded {AutomaticRelativeTolerance:G3} of the fitting radius)."));
        }

        var result = fit.Surface;
        var written = DesignToolSupport.WithShape(saved, result.Shape, unit);
        double? radiusAfter = saved.Radius;
        if (result.Curvature != source.Curvature)
        {
            radiusAfter = result.Curvature == 0 ? null : 1 / result.Curvature / perUnit;
            written = written with { Radius = radiusAfter };
        }
        if (result.Conic != source.Conic) written = written with { Conic = result.Conic };
        if (saved.Flags?.Any(f => SavedSurfaces.ParameterNumber(f.Column) is not null) == true)
            notes.Add("Flags on the old type's parameter cells were removed; the new coefficients are fixed.");
        var list = surfaces.ToList();
        list[j] = written;
        return new AsphereTypeConversionResult
        {
            Settings = optics with { Surfaces = list },
            Surface = j, FromType = fromType, Target = options.Target, Terms = count, ApertureRadius = rhoUnits,
            RmsResidual = fit.RmsResidual / perUnit, MaxResidual = fit.MaxResidual / perUnit,
            RadiusBefore = SavedSurfaces.IsPlane(saved) ? null : saved.Radius, RadiusAfter = radiusAfter,
            ConicBefore = saved.Conic, ConicAfter = written.Conic,
            Notes = notes,
        };
    }

    /// <summary>
    /// The automatic number of terms (this program's rule; the help: "OpticStudio automatically
    /// determines the proper order for the new asphere based upon the order of the old asphere").
    /// The source's order D is the highest power of r in its departure from the conic: 2n for an
    /// Even Asphere whose last non-zero term is αn, 2N for an Extended Asphere, 2T + 2 for a
    /// Q-type of T terms (x²Q^con_(T−1) and x(1 − x)Q^bfs_(T−1) both reach x^(T+1)). The Even and
    /// Extended targets take D/2 terms and the Q-types D/2 − 1, so a polynomial source is
    /// reproduced at its own order; at least one. A Standard surface or another radial form starts
    /// at 4. <see cref="Run"/> then adds terms while the residual exceeds
    /// <see cref="AutomaticRelativeTolerance"/> of the aperture.
    /// </summary>
    public static int AutomaticTerms(SequentialSurface source, AsphereTarget target)
    {
        ArgumentNullException.ThrowIfNull(source);
        static int Last(IReadOnlyList<double> a) { int n = a.Count; while (n > 0 && a[n - 1] == 0) n--; return n; }
        int? degree = source.Shape switch
        {
            EvenAsphere e => 2 * Last(e.Alpha),
            ExtendedAsphere x => 2 * Last(x.Alpha),
            QTypeAsphere q => Last(q.Coefficients) is int n && n > 0 ? 2 * n + 2 : 0,
            _ => null,
        };
        if (degree is not int d || d == 0) return 4;
        int terms = target is AsphereTarget.EvenAsphere or AsphereTarget.ExtendedAsphere ? d / 2 : d / 2 - 1;
        return Math.Max(1, terms);
    }

    /// <summary>OP-23's linear fit of <paramref name="source"/>'s sag with the base conic (c, k).</summary>
    private static AsphereFitResult Fit(SequentialSurface source, AsphereTarget target, int terms, double rho, double c, double k)
    {
        var basis = source with { Curvature = c, Conic = k, Shape = new FixedSag(source) };
        var fit = target switch
        {
            AsphereTarget.EvenAsphere => AsphereConversion.ToEvenAsphere(basis, rho, terms),
            AsphereTarget.ExtendedAsphere => AsphereConversion.ToExtendedAsphere(basis, rho, terms),
            AsphereTarget.QTypeQbfs => AsphereConversion.ToQType(basis, QTypeKind.Qbfs, terms, rho),
            _ => AsphereConversion.ToQType(basis, QTypeKind.Qcon, terms, rho),
        };
        // The fitted surface carries the stand-in's other data; give it back the source's.
        return fit with { Surface = source with { Curvature = fit.Surface.Curvature, Conic = fit.Surface.Conic, Shape = fit.Surface.Shape } };
    }

    /// <summary>The fit with the curvature (as c·ρ) and conic varied by damped least squares on
    /// the sag residuals at equal-area samples; null when it does not run.</summary>
    private static AsphereFitResult? Refine(SequentialSurface source, AsphereTarget target, int terms, double rho,
        bool varyC, bool varyK, CancellationToken token)
    {
        int n = Math.Max(256, 32 * terms);
        var u = new double[n];
        var original = new double[n];
        for (int i = 0; i < n; i++)
        {
            u[i] = Math.Sqrt((i + 0.5) / n);
            original[i] = AsphereConversion.RadialSag(source, u[i] * rho);
        }
        var start = new List<double>();
        if (varyC) start.Add(source.Curvature * rho);
        if (varyK) start.Add(source.Conic);
        (double C, double K) Unpack(double[] x)
        {
            int p = 0;
            double c = varyC ? x[p++] / rho : source.Curvature;
            double k = varyK ? x[p] : source.Conic;
            return (c, k);
        }
        void Residuals(double[] x, double[] r)
        {
            var (c, k) = Unpack(x);
            var fit = Fit(source, target, terms, rho, c, k);
            for (int i = 0; i < n; i++) r[i] = AsphereConversion.RadialSag(fit.Surface, u[i] * rho) - original[i];
        }
        try
        {
            var solved = DampedLeastSquares.Solve(Residuals, n, start.ToArray(), new LeastSquaresOptions { MaxIterations = 200, CancellationToken = token });
            var (cBest, kBest) = Unpack(solved.X);
            return Fit(source, target, terms, rho, cBest, kBest);
        }
        catch (InvalidOperationException)
        {
            return null;   // the fixed fit stands; it was evaluated at the same start
        }
    }

    /// <summary>A stand-in shape whose sag is the source's whatever the base conic: it lets OP-23's
    /// fit use a base conic other than the source's own. Never traced.</summary>
    private sealed class FixedSag : RadialSagShape
    {
        private readonly SequentialSurface _source;

        public FixedSag(SequentialSurface source) => _source = source;

        public override string TypeName => "Convert Asphere Types source";

        public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
        {
            z = AsphereConversion.RadialSag(_source, r);
            dzdr = 0;
            return true;
        }
    }
}
