using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-37: Relative Illumination.

/// <summary>Settings of the <see cref="RelativeIlluminationAnalysis"/>.</summary>
public sealed record RelativeIlluminationSettings
{
    [DisplayName("Ray density")]
    [Description("n: about n² pupil points per field (⌈n/2⌉ rings by n·2 spokes); at least 5.")]
    public int RayDensity { get; init; } = 20;

    [DisplayName("Field density")]
    [Description("Fields from the axis to the largest field along the scan, evenly spaced; 2 to 1001.")]
    public int FieldDensity { get; init; } = 21;

    [DisplayName("Use polarization")]
    [Description("Weight each ray by its transmission (Fresnel or coating, bulk absorption) for unpolarized light.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Remove vignetting factors")]
    [Description("Replace the fields' vignetting factors with surface apertures first; off traces through the factors.")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Scan")]
    [Description("The direction of the field scan.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Log scale")]
    [Description("Plot the relative illumination on a logarithmic scale.")]
    public bool LogScale { get; init; }
}

/// <summary>The relative illumination's numbers.</summary>
public sealed record RelativeIlluminationResult
{
    /// <summary>Normalised scan coordinate of each field.</summary>
    public required IReadOnlyList<double> H { get; init; }
    /// <summary>A: the weighted projected solid angle of the exit pupil seen from the image
    /// point, steradians (area in direction-cosine space).</summary>
    public required IReadOnlyList<double> ProjectedSolidAngle { get; init; }
    /// <summary>A / max A.</summary>
    public required IReadOnlyList<double> Relative { get; init; }
    /// <summary>√(π/(4A)).</summary>
    public required IReadOnlyList<double> EffectiveFNumber { get; init; }
    public required int RaysPerField { get; init; }
}

/// <summary>
/// The relative illumination (OP-37), docs/Optics-Conventions.md §27.
/// <para>
/// <b>What the help says</b> (Ansys OpticStudio 2025 R2 help, "Relative Illumination"): RI "is
/// defined as the intensity of illumination per unit area of image surface normalized to the
/// illumination at the point in the field that has maximum illumination"; the method is "based upon
/// one described in M. Rimmer, 'Relative illumination calculations', Proc. SPIE Vol. 655, p99
/// (1986)", extended "to include apodization, transmission, polarization, and non-planar image
/// surface effects"; "The relative illumination is computed by integration of the effective area of
/// the exit pupil as seen from the image point(s). The integration is carried out in direction
/// cosine space"; and the Effective F/# is E = √(π/(4A)), "where A is the area of the projected
/// solid angle of the pupil in cosine space weighted for system transmission". Rimmer's paper was
/// not available to this program; the integral below is derived here.
/// </para>
/// <para>
/// <b>Derivation.</b> A plane Lambertian object of uniform radiance L sends radiance L·n′²/n² (times
/// the ray's transmission τ) along every ray that reaches the image, since basic radiance L/n² is
/// conserved along a ray. The irradiance at an image point P on a surface of unit normal n̂ is the
/// radiance integrated over the solid angle of the rays arriving there, each weighted by the
/// cosine of its direction d̂ to n̂: E = ∫ L′·(d̂·n̂) dΩ. In the image surface's local frame with
/// direction cosines (L, M, N), dΩ = dL dM/|N|, so E ∝ ∫∫ τ·(d̂·n̂)/|N| dL dM over the region of
/// direction-cosine space the arriving rays fill: for a plane image surface n̂ = ẑ and the weight
/// is τ alone, the area of that region, the projected solid angle. The region is the image of the
/// pupil: the rays from one object point, parametrised by their normalised pupil coordinates
/// (Px, Py), map the unit disk onto it, so
/// A = ∫∫_disk τ·a(Px, Py)²·(d̂·n̂)/|N|·|∂(L, M)/∂(Px, Py)| dPx dPy, with a the pupil apodization's
/// amplitude. The map must be one to one (no caustic in angle space, the help's own assumption),
/// and the arriving rays are taken to meet at one point (the chief ray's), which holds to the
/// size of the aberrations. RI(H) = A(H)/max A; the object-side factors are the same at every
/// field for a uniform Lambertian object and cancel.
/// </para>
/// <para>
/// <b>Quadrature.</b> The disk integral is a product rule: ⌈n/2⌉ Gauss–Legendre nodes in the pupil
/// radius ρ (weight ρ) times 2n equally spaced angles, exact for smooth periodic integrands as n
/// grows; a ray stopped by an aperture or failing contributes zero, so a vignetted pupil's edge is
/// resolved to the node spacing. The Jacobian is by central differences at ±10⁻⁵ in Px and Py,
/// traced with apertures ignored (so a node just inside an aperture still has its neighbours),
/// one-sided where a neighbour cannot be traced at all. This differs from the help's "uniform grid
/// in image cosine space".
/// </para>
/// <para>
/// <b>Apodization</b> (help, "Apodization Type"): Gaussian amplitude A(ρ) = e^(−Gρ²), G the
/// apodization factor; cosine cubed A(ρ) = 1/(1 + (ρ·tan α)²)^(3/4), "where tan α is the tangent of
/// the angle between the z axis and the marginal ray. OpticStudio uses the entrance pupil position
/// and size to compute tan α": here the paraxial entrance pupil radius over its distance from the
/// object point. The intensity weight is A². ρ is the radius of the pupil point the ray is
/// launched at (after any vignetting factors). The help's solid-angle variant for the object cone
/// angle aperture type is not built and is refused.
/// </para>
/// </summary>
public static class RelativeIllumination
{
    public const double JacobianStep = 1e-5;

    internal static RelativeIlluminationResult Compute(FieldScanSetup setup, int row, FieldScan scan, int rayDensity, double[] h,
        CoatingCatalog? polarizationCatalog, CancellationToken cancellation)
    {
        const string who = "Relative illumination";
        var system = setup.System;
        var lens = setup.Lens;
        var launcher = setup.Launcher;
        var trace = setup.Trace(row);
        var polar = polarizationCatalog is null ? null
            : new PolarizationTracer(trace, polarizationCatalog, PolarizationOptions.From(system.Polarization, bulkAbsorption: true));
        var unapertured = new TraceOptions { IgnoreApertures = true };
        var apodization = Apodization(setup, who);

        int rings = (rayDensity + 1) / 2, spokes = 2 * rayDensity;
        var (nodes, weights) = GaussLegendre.Rule(rings, 0, 1);
        var (dx, dy) = FieldScans.Direction(scan);
        var image = lens.Surfaces[lens.ImageIndex];
        string wl = FieldScanCommon.WavelengthLabel(system, row);

        var area = new double[h.Length];
        for (int f = 0; f < h.Length; f++)
        {
            double hx = h[f] * dx, hy = h[f] * dy;
            double sum = 0;
            for (int i = 0; i < rings; i++)
            {
                double rho = nodes[i];
                for (int k = 0; k < spokes; k++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double phi = 2 * Math.PI * k / spokes;
                    double px = rho * Math.Cos(phi), py = rho * Math.Sin(phi);
                    var launch = launcher.Launch(trace, hx, hy, px, py);
                    double transmission;
                    Ray ray;
                    if (polar is null)
                    {
                        ray = trace.Trace(launch);
                        transmission = ray.IsOk ? 1 : 0;
                    }
                    else
                    {
                        var path = trace.TracePath(launch);
                        ray = path.Final;
                        transmission = !ray.IsOk ? 0
                            : (polar.TracePath(path, JonesVector.AlongS).Transmission + polar.TracePath(path, JonesVector.AlongP).Transmission) / 2;
                    }
                    if (!(transmission > 0)) continue;
                    var (vx, vy) = launcher.VignettedPupil(hx, hy, px, py);
                    double amplitude = apodization(Math.Sqrt(vx * vx + vy * vy));
                    var d = ray.Direction;
                    // OP-32: composite add-ons
                    var normal = OpenSim.Optics.Sequential.Composite.CompositeSurfaces.EffectiveShape(image) is { } shape ? shape.Normal(ray.Position, image) : ConicIntersection.Normal(ray.Position, image.Curvature, image.Conic);
                    double obliquity = Math.Abs(Vector3D.Dot(d, normal)) / Math.Abs(d.Z);
                    var (lx, mx) = Derivative(trace, launcher, hx, hy, px, py, true, d, unapertured, who, wl);
                    var (ly, my) = Derivative(trace, launcher, hx, hy, px, py, false, d, unapertured, who, wl);
                    double jacobian = Math.Abs(lx * my - ly * mx);
                    sum += weights[i] * rho * (2 * Math.PI / spokes) * transmission * amplitude * amplitude * obliquity * jacobian;
                }
            }
            area[f] = sum;
        }
        double max = area.Max();
        if (!(max > 0))
            throw new InvalidOperationException($"{who}: no ray reaches the image surface at any field ({wl}), so there is no illumination to normalise.");
        return new RelativeIlluminationResult
        {
            H = h, ProjectedSolidAngle = area, Relative = area.Select(a => a / max).ToArray(),
            EffectiveFNumber = area.Select(a => a > 0 ? Math.Sqrt(Math.PI / (4 * a)) : double.PositiveInfinity).ToArray(),
            RaysPerField = rings * spokes,
        };
    }

    /// <summary>∂(L, M)/∂Px or ∂Py at a pupil point, by central differences (one-sided when one
    /// neighbour cannot be traced).</summary>
    private static (double L, double M) Derivative(SequentialTrace trace, RayLauncher launcher, double hx, double hy,
        double px, double py, bool alongX, Vector3D centre, TraceOptions options, string who, string wl)
    {
        double e = JacobianStep;
        Ray Neighbour(double s) => trace.Trace(alongX ? launcher.Launch(trace, hx, hy, px + s, py) : launcher.Launch(trace, hx, hy, px, py + s), options);
        var plus = Neighbour(e);
        var minus = Neighbour(-e);
        if (plus.IsOk && minus.IsOk)
            return ((plus.Direction.X - minus.Direction.X) / (2 * e), (plus.Direction.Y - minus.Direction.Y) / (2 * e));
        if (plus.IsOk) return ((plus.Direction.X - centre.X) / e, (plus.Direction.Y - centre.Y) / e);
        if (minus.IsOk) return ((centre.X - minus.Direction.X) / e, (centre.Y - minus.Direction.Y) / e);
        throw new InvalidOperationException($"{who}: near pupil point ({px:g6}, {py:g6}) at field ({hx:g6}, {hy:g6}), {wl}, no neighbouring ray reaches the image surface ({FieldScanCommon.Failure(plus)}), so the area of the pupil there cannot be found.");
    }

    /// <summary>The apodization's amplitude against normalised pupil radius.</summary>
    private static Func<double, double> Apodization(FieldScanSetup setup, string who)
    {
        var aperture = setup.System.Aperture;
        switch (aperture.Apodization)
        {
            case ApodizationType.Uniform:
                return _ => 1;
            case ApodizationType.Gaussian:
            {
                double g = aperture.ApodizationFactor;
                return rho => Math.Exp(-g * rho * rho);
            }
            case ApodizationType.CosineCubed:
            {
                if (aperture.Type == ApertureType.ObjectConeAngle)
                    throw new InvalidOperationException($"{who}: cosine-cubed apodization with an object cone angle aperture uses OpticStudio's solid-angle ray distribution, which is not built.");
                var p = setup.Launcher.Paraxial;
                if (setup.Lens.ObjectAtInfinity || p.EntrancePupilAtInfinity)
                    throw new InvalidOperationException($"{who}: cosine-cubed apodization needs an object at a finite distance and an entrance pupil at a finite distance (it models a point source illuminating the pupil).");
                double distance = setup.Lens.Surfaces[0].Thickness + p.EntrancePupilPosition!.Value;
                double tanAlpha = p.EntrancePupilDiameter!.Value / 2 / distance;
                return rho => Math.Pow(1 + rho * tanAlpha * (rho * tanAlpha), -0.75);
            }
            default:
                throw new InvalidOperationException($"{who}: apodization {aperture.Apodization} is not known.");
        }
    }
}

/// <summary>OpticStudio's Relative Illumination (OP-37); see <see cref="RelativeIllumination"/>.</summary>
public sealed class RelativeIlluminationAnalysis : OpticsAnalysis<RelativeIlluminationSettings>
{
    public const string SavedId = "RelativeIllumination";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RelativeIlluminationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Relative illumination";
    public override string Category => "Illumination";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RelativeIlluminationSettings settings, CancellationToken cancellation)
    {
        const string who = "Relative illumination";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        // The help: "The Ray Density minimum value is 5."
        FieldScanCommon.CheckCount(settings.RayDensity, 5, 400, who, "ray density");
        FieldScanCommon.CheckCount(settings.FieldDensity, 2, 1001, who, "field density");
        FieldScanCommon.RefuseAfocal(system, who);
        int row = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: false)[0];
        bool replaced = false;
        if (settings.RemoveVignettingFactors && system.Fields.Points.Any(p => p.IsVignetted))
        {
            var r = VignettingTools.ReplaceWithApertures(lens, system, cancellation: cancellation);
            lens = r.Lens;
            system = r.System;
            replaced = true;
            notes.AddRange(r.Notes);
        }
        system = FieldScanCommon.ScanSystem(system, realImageHeightAsParaxial: false, notes);
        var setup = FieldScanCommon.Prepare(lens, system, notes, cancellation);
        if (!(setup.ScanScale(settings.Scan) > 0))
            throw new ArgumentException($"{who}: every field is on the axis, so there is no field to scan; add an off-axis field.");
        var catalog = settings.UsePolarization ? CoatingCatalogSettings.FromSettings(context.System.Coatings, notes) : null;
        var h = FieldScanCommon.Even(settings.FieldDensity);
        var result = RelativeIllumination.Compute(setup, row, settings.Scan, settings.RayDensity, h, catalog, cancellation);

        string fieldLabel = setup.FieldLabel(settings.Scan);
        var fieldShown = h.Select(v => setup.FieldShown(v, settings.Scan)).ToArray();
        var rows = Enumerable.Range(0, h.Length).Select(i => (IReadOnlyList<string>)new[]
        {
            FieldScanCommon.N(fieldShown[i], "g6"), FieldScanCommon.N(result.Relative[i], "g8"),
            FieldScanCommon.N(result.ProjectedSolidAngle[i], "g8"), FieldScanCommon.N(result.EffectiveFNumber[i], "g8"),
        }).ToList();
        var sections = new[]
        {
            new ReportSection("Relative illumination")
            {
                Pairs = new[]
                {
                    ("Wavelength", $"{row + 1} ({FieldScanCommon.WavelengthLabel(system, row)})"),
                    ("Pupil points per field", result.RaysPerField.ToString()),
                },
                TableHeaders = new[] { fieldLabel, "Relative illumination", "Projected solid angle A (sr)", "Effective F/#" },
                TableRows = rows,
            },
        };
        var assumptions = new List<string>
        {
            "Uniform, plane, Lambertian object; RI is the projected solid angle of the exit pupil seen from the chief ray's image point (an integral in image-space direction-cosine space over the pupil, docs/Optics-Conventions.md §27), normalised to its largest value over the scan.",
            "Effective F/# = √(π/(4A)) with A from geometric direction cosines (an image space of index n′ ≠ 1 is not scaled by n′²).",
            settings.UsePolarization
                ? "Each ray is weighted by its transmission for unpolarized light (mean of two orthogonal states): Fresnel or coating amplitudes and bulk absorption (docs/Optics-Conventions.md §21)."
                : "Transmission is not included (Use polarization is off): every ray that reaches the image counts fully.",
            $"Pupil apodization: {ApodizationTypes.Names.Label(system.Aperture.Apodization)}.",
            replaced ? "The fields' vignetting factors were replaced with surface apertures before tracing (docs/Optics-Conventions.md §16)."
                : system.Fields.Points.Any(p => p.IsVignetted) ? "The vignetting factors were kept: rays go into each field's remapped pupil." : "No field has vignetting factors.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot("Relative illumination", fieldLabel, "Relative illumination",
                new[] { new AnalysisCurve(FieldScanCommon.WavelengthLabel(system, row), fieldShown, result.Relative) }) { LogY = settings.LogScale },
        });
    }
}
