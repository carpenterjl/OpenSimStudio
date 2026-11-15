using OpenSim.Optics.Coatings;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>What the Ghost Focus Generator computes (help, "Ghost Focus Generator").</summary>
public sealed record GhostFocusOptions
{
    /// <summary>Double-bounce ghosts (true) or single-bounce ghosts (false).</summary>
    public bool DoubleBounce { get; init; } = true;

    /// <summary>The first and last surfaces to consider reflections from (original numbers; the
    /// last may be the image surface).</summary>
    public int FirstSurface { get; init; } = 1;
    public int LastSurface { get; init; }

    /// <summary>0-based wavelength row.</summary>
    public int WavelengthRow { get; init; }

    /// <summary>The help's Ghost Reflector Coating, or blank.</summary>
    public string? ReflectorCoating { get; init; }

    /// <summary>Rays across the pupil diameter of the on-axis real-ray grid (inside the unit circle).</summary>
    public int PupilGrid { get; init; } = 10;
}

/// <summary>One surface of a ghost system as the listing gives it.</summary>
public sealed record GhostSurfaceData
{
    /// <summary>The surface's index in the ghost system.</summary>
    public required int GhostSurface { get; init; }
    public required GhostSurfaceOrigin Origin { get; init; }

    /// <summary>Paraxial marginal and chief (full field) ray heights at the vertex plane, m.</summary>
    public required double MarginalHeight { get; init; }
    public required double ChiefHeight { get; init; }

    /// <summary>The paraxial ray F/# in the space after the surface, 1/(2|n·u|) of the marginal ray; infinite for a collimated beam.</summary>
    public required double FNumber { get; init; }

    /// <summary>RMS radius of the on-axis real rays about their centroid on the surface, m; NaN when none arrived.</summary>
    public required double RmsRadius { get; init; }

    /// <summary>Real rays that reached the surface.</summary>
    public required int RaysArrived { get; init; }

    /// <summary>True when the space after the surface is glass and the paraxial marginal ray
    /// crosses the axis in it: a possible internal focus.</summary>
    public required bool InternalFocus { get; init; }
}

/// <summary>One ghost path's results; <see cref="Error"/> names why it could not be computed.</summary>
public sealed record GhostResult
{
    public required int FirstBounce { get; init; }
    public int? SecondBounce { get; init; }
    public GhostSystem? Ghost { get; init; }
    public string? Error { get; init; }

    /// <summary>Unpolarized normal-incidence reflectance of the first and second bounce.</summary>
    public double FirstReflectance { get; init; }
    public double? SecondReflectance { get; init; }

    /// <summary>The relative intensity of the ghost: the product of the bounce reflectances (R1·R2).</summary>
    public double RelativeIntensity { get; init; }

    /// <summary>R1·R2 times the transmittance of every other surface the ghost path crosses, normal incidence.</summary>
    public double PathTransmittance { get; init; }

    /// <summary>The ghost's paraxial focus from the ghost image surface along z, m; null when the beam leaves collimated.</summary>
    public double? FocusFromImage { get; init; }

    /// <summary>Paraxial marginal and chief ray heights on the image surface, m.</summary>
    public double MarginalAtImage { get; init; }
    public double ChiefAtImage { get; init; }

    /// <summary>The pupil ghost size: diameter of the ghost beam on the image surface, 2|y|, m.</summary>
    public double BeamDiameterAtImage { get; init; }

    /// <summary>The ghost system's effective focal length, m; null when afocal.</summary>
    public double? EffectiveFocalLength { get; init; }

    /// <summary>Where the ghost images the pupil (its exit pupil) from the image surface, m; its
    /// diameter and that diameter over the entrance pupil diameter.</summary>
    public double? PupilFromImage { get; init; }
    public double? PupilDiameter { get; init; }
    public double? PupilMagnification { get; init; }

    public IReadOnlyList<GhostSurfaceData> Surfaces { get; init; } = Array.Empty<GhostSurfaceData>();

    /// <summary>Real rays launched and those that failed, with one line per failure kind and surface.</summary>
    public int RaysLaunched { get; init; }
    public int RaysFailed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public string Label => SecondBounce is int i ? $"{FirstBounce} → {i}" : $"{FirstBounce}";
}

/// <summary>The generator's output: every ghost and what the numbers assume.</summary>
public sealed record GhostFocusRun(IReadOnlyList<int> Candidates, IReadOnlyList<GhostResult> Ghosts, IReadOnlyList<string> Notes);

/// <summary>
/// OpticStudio's Ghost Focus Generator (OP-46, docs/Optics-Conventions.md §49): every
/// single-bounce ghost, or every double-bounce pair of candidate surfaces (n(n − 1)/2 for n
/// candidates), built by <see cref="GhostSystems.Build"/>, traced paraxially and with real rays.
/// The help lists "for each ghost system, the paraxial marginal ray height, paraxial ray F/#, and
/// real ray RMS spot radius on axis", flags "glass surfaces which may have an internal focus", and
/// for the image surface of a double-bounce ghost "the paraxial marginal and chief ray heights,
/// distance from the image surface to the ghost, and the EFL of the ghost system" (2025 R2 help,
/// "Ghost Focus Generator", plans/sources/optics/op-46-help/). The ghost's relative intensity
/// (R1·R2, from the coatings of OP-15 or the uncoated Fresnel interface), the pupil ghost size and
/// the ghost pupil are this program's additions from the plan.
/// </summary>
public static class GhostFocusGenerator
{
    /// <summary>Runs the generator on <paramref name="lens"/>. A ghost that cannot be built or
    /// traced is reported with its error, never dropped.</summary>
    public static GhostFocusRun Run(SequentialLens lens, OpticalSystemData system, GhostFocusOptions options, CoatingCatalog catalog,
        IReadOnlyList<double>? semiDiameters = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        GhostSystems.CheckSupported(lens);
        if (options.PupilGrid < 1 || options.PupilGrid > 200)
            throw new ArgumentException($"Ghost focus: the pupil grid must be 1 to 200 rays across (it is {options.PupilGrid}).");
        int image = lens.ImageIndex;
        int last = options.LastSurface == 0 ? image : options.LastSurface;
        int row = options.WavelengthRow;

        var trace = SequentialTrace.Create(lens, system, row, semiDiameters);
        var launcher = new RayLauncher(lens, system);
        var paraxial = Paraxial.ParaxialSystem.Create(lens.ToParaxialLens(), system, row);
        var intensity = new GhostIntensity(lens, system, row, catalog, options.ReflectorCoating);
        var candidates = GhostSystems.Candidates(lens, trace.Indices, options.FirstSurface, last, includeImage: true);
        var notes = new List<string>(launcher.Notes);
        notes.AddRange(paraxial.Notes);

        var paths = new List<(int First, int? Second)>();
        if (options.DoubleBounce) paths.AddRange(GhostSystems.Pairs(candidates).Select(p => (p.First, (int?)p.Second)));
        else paths.AddRange(candidates.Select(j => (j, (int?)null)));

        // The on-axis real rays, launched once in the original lens.
        var grid = PupilGrid(options.PupilGrid);
        var launches = grid.Select(p => launcher.Launch(trace, 0, 0, p.Px, p.Py)).ToArray();

        var results = new List<GhostResult>(paths.Count);
        foreach (var (first, second) in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                results.Add(Compute(lens, system, row, paraxial, intensity, first, second, launches, cancellation));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                results.Add(new GhostResult { FirstBounce = first, SecondBounce = second, Error = e.Message });
            }
        }
        return new GhostFocusRun(candidates, results, notes.Distinct().ToArray());
    }

    /// <summary>The square grid of pupil points inside the unit circle: cell centres of an
    /// n × n grid over [−1, 1]² (n = 1 is the chief ray).</summary>
    public static IReadOnlyList<(double Px, double Py)> PupilGrid(int n)
    {
        var list = new List<(double, double)>();
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                double px = n == 1 ? 0 : -1 + (2.0 * a + 1) / n, py = n == 1 ? 0 : -1 + (2.0 * b + 1) / n;
                if (px * px + py * py <= 1) list.Add((px, py));
            }
        return list;
    }

    /// <summary>One ghost: built, traced paraxially and with <paramref name="launches"/>.</summary>
    public static GhostResult Compute(SequentialLens lens, OpticalSystemData system, int row, Paraxial.ParaxialSystem paraxial,
        GhostIntensity intensity, int first, int? second, IReadOnlyList<Ray> launches, CancellationToken cancellation = default)
    {
        var ghost = GhostSystems.Build(lens, first, second);
        var model = new GhostParaxial(ghost, system.Wavelengths.Wavelengths[row].Micrometers, system.Environment);
        var marginal = model.Trace(paraxial.MarginalRay.Heights[1], paraxial.MarginalRay.ReducedSlopes[0]);
        var chief = model.Trace(paraxial.ChiefRay.Heights[1], paraxial.ChiefRay.ReducedSlopes[0]);
        int count = ghost.Lens.Surfaces.Count, gImage = count - 1;

        // Intensity: the bounce reflectances, then every other crossing.
        double r1 = intensity.Reflectance(first, forward: true);
        double? r2 = second is int i2 ? intensity.Reflectance(i2, forward: false) : null;
        double product = r1 * (r2 ?? 1);
        double path = product;
        for (int g = 1; g < gImage; g++)
        {
            var o = ghost.Origins[g];
            bool forward = o.Pass is GhostPass.In or GhostPass.Out;
            if (o.Pass is GhostPass.In or GhostPass.Back or GhostPass.Out && intensity.Acts(o.Original))
                path *= intensity.Transmittance(o.Original, forward);
        }

        // Real rays through the ghost.
        var trace = SequentialTrace.Create(ghost.Lens, system, row);
        var paths = new RayPath[launches.Count];
        for (int k = 0; k < launches.Count; k++)
        {
            if ((k & 63) == 0) cancellation.ThrowIfCancellationRequested();
            paths[k] = launches[k].IsOk
                ? trace.TracePath(launches[k])
                : new RayPath { Hits = Array.Empty<SurfaceHit>(), Final = launches[k] };
        }
        var failures = paths.Where(p => !p.Final.IsOk)
            .GroupBy(p => (p.Final.Status, p.Final.FailedAtSurface))
            .OrderBy(g => g.Key.FailedAtSurface)
            .Select(g => $"{g.Count()} ray{(g.Count() == 1 ? "" : "s")}: {RayStatuses.Label(g.Key.Status).ToLowerInvariant()} at {Where(ghost, g.Key.FailedAtSurface)}.")
            .ToArray();

        var media = ghost.Lens.EffectiveMedia();
        var surfaces = new List<GhostSurfaceData>(count);
        for (int g = 1; g < count; g++)
        {
            double sx = 0, sy = 0; int n = 0;
            foreach (var p in paths)
                if (p.Hits.Count > g && p.Hits[g].Status == RayStatus.Ok) { sx += p.Hits[g].Position.X; sy += p.Hits[g].Position.Y; n++; }
            double rms = double.NaN;
            if (n > 0)
            {
                double cx = sx / n, cy = sy / n, s2 = 0;
                foreach (var p in paths)
                    if (p.Hits.Count > g && p.Hits[g].Status == RayStatus.Ok)
                    {
                        double dx = p.Hits[g].Position.X - cx, dy = p.Hits[g].Position.Y - cy;
                        s2 += dx * dx + dy * dy;
                    }
                rms = Math.Sqrt(s2 / n);
            }
            double nu = marginal.ReducedSlopes[g];
            bool glass = g < gImage && GhostSystems.IsGlass(media[g]);
            bool focus = glass && marginal.Heights[g] * marginal.Heights[g + 1] <= 0 && marginal.Heights[g] != marginal.Heights[g + 1];
            surfaces.Add(new GhostSurfaceData
            {
                GhostSurface = g, Origin = ghost.Origins[g],
                MarginalHeight = marginal.Heights[g], ChiefHeight = chief.Heights[g],
                FNumber = nu == 0 ? double.PositiveInfinity : 1 / (2 * Math.Abs(nu)),
                RmsRadius = rms, RaysArrived = n, InternalFocus = focus,
            });
        }

        double power = model.Power();
        double? pupil = model.AxisCrossingFromImage(chief);
        double? pupilDiameter = pupil is double zp ? 2 * Math.Abs(model.HeightAt(marginal, zp)) : null;
        double? magnification = pupilDiameter is double dp && paraxial.EntrancePupilDiameter is double epd && epd > 0 ? dp / epd : null;
        return new GhostResult
        {
            FirstBounce = first, SecondBounce = second, Ghost = ghost,
            FirstReflectance = r1, SecondReflectance = r2, RelativeIntensity = product, PathTransmittance = path,
            FocusFromImage = model.AxisCrossingFromImage(marginal),
            MarginalAtImage = marginal.Heights[gImage], ChiefAtImage = chief.Heights[gImage],
            BeamDiameterAtImage = 2 * Math.Abs(marginal.Heights[gImage]),
            EffectiveFocalLength = power == 0 ? null : 1 / power,
            PupilFromImage = pupil, PupilDiameter = pupilDiameter, PupilMagnification = magnification,
            Surfaces = surfaces,
            RaysLaunched = launches.Count, RaysFailed = paths.Count(p => !p.Final.IsOk), Failures = failures,
        };
    }

    /// <summary>"ghost surface 5 (surface 3, on the way back)".</summary>
    public static string Where(GhostSystem ghost, int g)
    {
        if (g < 0 || g >= ghost.Origins.Count) return $"ghost surface {g}";
        var o = ghost.Origins[g];
        return $"ghost surface {g} ({PassLabel(o, ghost.Lens.ImageIndex == g)})";
    }

    /// <summary>"surface 3, on the way back".</summary>
    public static string PassLabel(GhostSurfaceOrigin o, bool ghostImage) => o.Pass switch
    {
        GhostPass.In => $"surface {o.Original}, on the way in",
        GhostPass.FirstBounce => $"surface {o.Original}, first bounce",
        GhostPass.Back => $"surface {o.Original}, on the way back",
        GhostPass.SecondBounce => $"surface {o.Original}, second bounce",
        GhostPass.Out => $"surface {o.Original}, on the way out",
        _ => ghostImage ? (o.Original == 0 ? "ghost image on the object plane" : "image surface") : "object",
    };
}
