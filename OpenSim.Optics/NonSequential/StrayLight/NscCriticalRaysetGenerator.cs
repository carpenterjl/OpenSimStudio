using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>What the Critical Rayset Generator traces (help, "Critical Rayset Generator").</summary>
public sealed record NscCriticalRaysetOptions
{
    /// <summary>"Number of Rays": per field and wavelength along a fan, around the ring (N − 1 on
    /// the ring), or per side of the grid; ignored for Chief and Marginals.</summary>
    public int Rays { get; init; } = 5;
    public NscRaysetPattern Pattern { get; init; } = NscRaysetPattern.ChiefAndMarginals;
    /// <summary>"Fields, On Axis Only (Otherwise Use 'All')".</summary>
    public bool OnAxisOnly { get; init; }
    /// <summary>"Wavelengths, Primary Only (Otherwise Use 'All')".</summary>
    public bool PrimaryOnly { get; init; }
    /// <summary>"Effective Input Distance", meters (infinite conjugates only).</summary>
    public double EffectiveInputDistance { get; init; }
    /// <summary>The default glue distance in lens units the help's margins refer to.</summary>
    public double DefaultGlueDistanceLensUnits { get; init; } = 1e-6;
}

/// <summary>
/// The Critical Rayset Generator (plan item OP-84; help, "Critical Rayset Generator";
/// docs/Optics-Conventions.md §78): critical rays traced through the sequential lens, each kept
/// with its start (on the object surface, or for an object at infinity on a dummy plane the
/// Effective Input Distance before surface 1) and its end at the image surface, in the launch
/// frame (§13: object-space axes, origin at surface 1's vertex; this program has no global
/// reference surface setting), for the Critical Ray Tracer or a Source File.
/// <para>
/// Refused as the help says: an aperture other than Float By Stop Size, Object Space NA or Object
/// Cone Angle; ray aiming off; a surface (other than coordinate breaks) with a semi-diameter of
/// zero; more than one configuration. Sequential scattering is not built, so its check is moot.
/// The List pattern (the 3D viewer's raylist file) is not built: that help page is not among the
/// sources. A dummy plane for input angles over 90° (the help's curved dummy surface) is refused.
/// "Vignetted or obscured rays are not saved".
/// </para>
/// <para>
/// Readings (§10 item 74). Pupil points: Chief and Marginals is (0, 0), (0, ±1), (±1, 0); a fan
/// of N rays is evenly spaced from −1 to 1 (the chief flagged chief and the ends marginal when
/// they fall there); Chief and Ring is the chief and N − 1 rays at equal angles on the rim
/// (ring rays); the grid is N × N points evenly spaced from −1 to 1 in both directions, those
/// inside the unit circle kept. Every pupil coordinate is scaled by 1 − 50·g/R for Float By Stop
/// Size (g the default glue distance, R the stop radius: the help's "within 50*(the default glue
/// distance)" of the edge) and by 1 − 10⁻⁶ for Object Space NA and Object Cone Angle ("1E-6*(Aperture
/// Value)"). In mirror space the target detectors are turned 180° about their own x axis.
/// </para>
/// </summary>
public static class NscCriticalRaysetGenerator
{
    public static NscCriticalRayset Generate(SequentialLens lens, OpticalSystemData system, NscCriticalRaysetOptions options,
        int configurations, List<string> notes, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(options);
        const string who = "Critical rayset generator";
        var unit = system.Units.LensUnit;
        if (system.Aperture.Type is not (ApertureType.FloatByStopSize or ApertureType.ObjectSpaceNumericalAperture or ApertureType.ObjectConeAngle))
            throw new ArgumentException($"{who}: the aperture type is {system.Aperture.Type}; the help allows only Float By Stop Size, Object Space NA or Object Cone Angle.");
        if (system.RayAiming.Mode == RayAimingMode.Off)
            throw new ArgumentException($"{who}: ray aiming is off; the help requires it on.");
        if (configurations > 1)
            throw new ArgumentException($"{who}: the system has {configurations} configurations; the help refuses multiple configurations.");
        if (options.Pattern == NscRaysetPattern.List)
            throw new NotSupportedException($"{who}: the List pattern reads the 3D viewer's raylist file, whose format page is not among this build's sources; not built.");
        if (options.Pattern != NscRaysetPattern.ChiefAndMarginals && options.Rays < (options.Pattern == NscRaysetPattern.ChiefAndRing ? 2 : 1))
            throw new ArgumentException($"{who}: the number of rays must be at least {(options.Pattern == NscRaysetPattern.ChiefAndRing ? 2 : 1)} (it is {options.Rays}).");

        IReadOnlyList<double>? semi = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semi = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            if (lens.Surfaces[j].IsCoordinateBreak) continue;
            double sd = lens.Surfaces[j].SemiDiameter ?? semi?[j] ?? double.NaN;
            if (sd == 0) throw new ArgumentException($"{who}: surface {j} has a semi-diameter of 0; the help refuses it.");
        }

        var launcher = new RayLauncher(lens, system);
        double scale = system.Aperture.Type == ApertureType.FloatByStopSize
            ? 1 - 50 * OpticsConventions.ToMeters(options.DefaultGlueDistanceLensUnits, unit) / launcher.StopRadius
            : 1 - 1e-6;
        if (!(scale > 0 && scale < 1))
            throw new ArgumentException($"{who}: the stop radius ({launcher.StopRadius} m) is too small for the help's edge margin of 50 glue distances.");

        double input = 0;
        if (lens.ObjectAtInfinity)
        {
            double min = OpticsConventions.ToMeters(10 * options.DefaultGlueDistanceLensUnits, unit);
            input = options.EffectiveInputDistance;
            if (!(input >= min))
            {
                notes.Add($"The effective input distance is raised to the help's minimum, 10 default glue distances ({StudyReport.Number(10 * options.DefaultGlueDistanceLensUnits, "g6")} lens units).");
                input = min;
            }
        }

        // Fields and wavelengths.
        var fields = new List<(int Number, double Hx, double Hy)>();
        if (options.OnAxisOnly)
        {
            int axis = Enumerable.Range(0, system.Fields.Points.Count).FirstOrDefault(i => system.Fields.Points[i].X == 0 && system.Fields.Points[i].Y == 0, -1);
            if (axis < 0) throw new ArgumentException($"{who}: \"On Axis Only\" needs an on-axis field point (0, 0); the system has none.");
            fields.Add((axis + 1, 0, 0));
        }
        else
            for (int i = 0; i < system.Fields.Points.Count; i++)
            {
                var (hx, hy) = system.Fields.Normalized(i);
                fields.Add((i + 1, hx, hy));
            }
        var waves = options.PrimaryOnly ? new[] { system.Wavelengths.PrimaryIndex } : Enumerable.Range(0, system.Wavelengths.Wavelengths.Count).ToArray();
        var pupil = Pupil(options);

        var rays = new List<NscCriticalRay>();
        int vignetted = 0;
        foreach (int w in waves)
        {
            var trace = SequentialTrace.Create(lens, system, w, semi);
            foreach (var (number, hx, hy) in fields)
                foreach (var (px, py, flag) in pupil)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = trace.TracePath(launcher.Launch(trace, hx, hy, px * scale, py * scale));
                    if (!path.Final.IsOk) { vignetted++; continue; }
                    var h0 = path.Hits[0];
                    var d0 = trace.GlobalPlacement(h0.Surface).ApplyDirection(h0.Direction);
                    var p0 = h0.GlobalPosition;
                    if (lens.ObjectAtInfinity)
                    {
                        if (!(d0.Z > 0))
                            throw new NotSupportedException($"{who}: field {number} enters at more than 90° to the axis; the help's curved dummy surface is not built.");
                        p0 += d0 * ((-input - p0.Z) / d0.Z);
                    }
                    var hi = path.Hits[^1];
                    if (hi.Surface != lens.ImageIndex)
                        throw new InvalidOperationException($"{who}: field {number}, pupil ({px}, {py}): the trace did not record the image surface.");
                    var di = trace.GlobalPlacement(hi.Surface).ApplyDirection(hi.IncomingDirection);
                    rays.Add(new NscCriticalRay(p0, d0 / d0.Length, trace.AirMicrometers, 1, number, flag, hi.GlobalPosition, di / di.Length));
                }
        }
        if (vignetted > 0) notes.Add($"{vignetted} vignetted or obscured ray(s) were not saved (help: \"Vignetted or obscured rays are not saved to the .CRS file\").");

        var imagePlacement = SequentialTrace.Create(lens, system, system.Wavelengths.PrimaryIndex, semi).GlobalPlacement(lens.ImageIndex);
        var rotation = imagePlacement with { Translation = Vector3D.Zero };
        int mirrors = lens.Surfaces.Take(lens.ImageIndex).Count(s => s.IsMirror);
        if (mirrors % 2 == 1)
        {
            rotation = RigidTransform.Compose(rotation, RigidTransform.RotationAboutX(0, -1));
            notes.Add("The image is in mirror space: the target detectors are turned 180° about their x axis (help: \"an extra 180 degree rotation around the X-axis\").");
        }
        return new NscCriticalRayset
        {
            Rays = rays, UnitsFlag = NscCriticalRayset.UnitsFlagOf(unit),
            Date = int.Parse(DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            Pattern = options.Pattern, TargetRotation = rotation,
        };
    }

    /// <summary>The pupil points of a pattern (before the edge margin) and their flags.</summary>
    public static IReadOnlyList<(double Px, double Py, NscCriticalRayFlag Flag)> Pupil(NscCriticalRaysetOptions options)
    {
        int n = options.Rays;
        var list = new List<(double, double, NscCriticalRayFlag)>();
        double At(int i) => n == 1 ? 0 : -1 + 2.0 * i / (n - 1);
        NscCriticalRayFlag FanFlag(double v, NscCriticalRayFlag other) =>
            v == 0 ? NscCriticalRayFlag.Chief : Math.Abs(v) == 1 ? NscCriticalRayFlag.Marginal : other;
        switch (options.Pattern)
        {
            case NscRaysetPattern.ChiefAndMarginals:
                list.Add((0, 0, NscCriticalRayFlag.Chief));
                list.Add((0, 1, NscCriticalRayFlag.Marginal));
                list.Add((0, -1, NscCriticalRayFlag.Marginal));
                list.Add((1, 0, NscCriticalRayFlag.Marginal));
                list.Add((-1, 0, NscCriticalRayFlag.Marginal));
                break;
            case NscRaysetPattern.YFan:
                for (int i = 0; i < n; i++) list.Add((0, At(i), FanFlag(At(i), NscCriticalRayFlag.YFan)));
                break;
            case NscRaysetPattern.XFan:
                for (int i = 0; i < n; i++) list.Add((At(i), 0, FanFlag(At(i), NscCriticalRayFlag.XFan)));
                break;
            case NscRaysetPattern.XyFan:
                for (int i = 0; i < n; i++) list.Add((At(i), 0, FanFlag(At(i), NscCriticalRayFlag.XyFan)));
                for (int i = 0; i < n; i++) if (At(i) != 0) list.Add((0, At(i), FanFlag(At(i), NscCriticalRayFlag.XyFan)));
                break;
            case NscRaysetPattern.ChiefAndRing:
                list.Add((0, 0, NscCriticalRayFlag.Chief));
                for (int k = 0; k < n - 1; k++)
                {
                    double a = 2 * Math.PI * k / (n - 1);
                    list.Add((Math.Sin(a), Math.Cos(a), NscCriticalRayFlag.Ring));
                }
                break;
            case NscRaysetPattern.Grid:
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        double x = At(i), y = At(j);
                        if (x * x + y * y <= 1) list.Add((x, y, NscCriticalRayFlag.Grid));
                    }
                break;
            default:
                throw new NotSupportedException($"Critical rayset generator: the {options.Pattern} pattern is not built.");
        }
        return list;
    }
}

/// <summary>Settings of the <see cref="NscCriticalRaysetGeneratorAnalysis"/> (help, "Critical Rayset Generator").</summary>
public sealed record NscCriticalRaysetGeneratorSettings
{
    [DisplayName("Number of rays"), Description("Rays per field and wavelength along a fan, around the ring (N − 1 on it), or per side of the grid; ignored for Chief and Marginals.")]
    public int Rays { get; init; } = 5;

    [DisplayName("Ray pattern"), Description("Chief and marginals, XY fan, X fan, Y fan, chief and ring or grid (List is not built).")]
    public NscRaysetPattern Pattern { get; init; } = NscRaysetPattern.ChiefAndMarginals;

    [DisplayName("Output file"), Description("The full path of the CRS file to write; blank to list the rays only.")]
    public string OutputFile { get; init; } = "";

    [DisplayName("Fields, on axis only"), Description("Trace the on-axis field point only; otherwise every field.")]
    public bool OnAxisOnly { get; init; }

    [DisplayName("Wavelengths, primary only"), Description("Trace the primary wavelength only; otherwise every wavelength.")]
    public bool PrimaryOnly { get; init; }

    [DisplayName("Effective input distance"), Description("Infinite conjugates: the distance of the dummy start plane before surface 1, lens units (at least 10 default glue distances).")]
    public double EffectiveInputDistance { get; init; } = 1;
}

/// <summary>The Critical Rayset Generator as a registered analysis (id <c>NscCriticalRaysetGenerator</c>).</summary>
public sealed class NscCriticalRaysetGeneratorAnalysis : OpticsAnalysis<NscCriticalRaysetGeneratorSettings>
{
    public const string SavedId = "NscCriticalRaysetGenerator";
    public override string Id => SavedId;
    public override string Name => "Critical rayset generator";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscCriticalRaysetGeneratorSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = ProjectLens.Sequential(context)
            ?? throw new ArgumentException($"{Name}: the system has no sequential surfaces to make critical rays from.");
        var unit = system.Units.LensUnit;
        var rayset = NscCriticalRaysetGenerator.Generate(lens, system, new NscCriticalRaysetOptions
        {
            Rays = settings.Rays, Pattern = settings.Pattern, OnAxisOnly = settings.OnAxisOnly, PrimaryOnly = settings.PrimaryOnly,
            EffectiveInputDistance = OpticsConventions.ToMeters(settings.EffectiveInputDistance, unit),
        }, context.System.MultiConfiguration?.ConfigurationCount ?? 1, notes, cancellation);
        if (!string.IsNullOrWhiteSpace(settings.OutputFile))
        {
            rayset.Write(settings.OutputFile);
            notes.Add($"{rayset.Rays.Count} ray(s) written to {settings.OutputFile}.");
        }
        string u = Paraxial.ParaxialReport.UnitName(unit);
        string L(double m) => StudyReport.Number(OpticsConventions.FromMeters(m, unit), "g10");
        string N(double v) => StudyReport.Number(v, "g10");
        var rows = rayset.Rays.Select((r, i) => (IReadOnlyList<string>)new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), r.Field.ToString(CultureInfo.InvariantCulture), StudyReport.Number(r.WavelengthMicrometers, "g8"),
            NscCriticalRayset.FlagName(r.Flag), L(r.Start.X), L(r.Start.Y), L(r.Start.Z), N(r.StartDirection.X), N(r.StartDirection.Y), N(r.StartDirection.Z),
            L(r.End.X), L(r.End.Y), L(r.End.Z), N(r.EndDirection.X), N(r.EndDirection.Y), N(r.EndDirection.Z),
        }).ToList();
        var (tx, ty, tz) = NscCriticalRayset.Tilts(rayset.TargetRotation);
        var sections = new[]
        {
            new ReportSection("Critical rayset")
            {
                Pairs = new (string, string)[]
                {
                    ("Rays", rayset.Rays.Count.ToString(CultureInfo.InvariantCulture)),
                    ("Pattern", $"{settings.Pattern} (rayset flag {(int)settings.Pattern})"),
                    ("Units flag", $"{rayset.UnitsFlag} ({u})"),
                    ("Target detector tilts", $"{N(tx)}°, {N(ty)}°, {N(tz)}° about x, the new y, the new z"),
                },
                TableHeaders = new[] { "Ray", "Field", "Wavelength (µm)", "Flag", $"X ({u})", $"Y ({u})", $"Z ({u})", "L", "M", "N",
                    $"X end ({u})", $"Y end ({u})", $"Z end ({u})", "L end", "M end", "N end" },
                TableRows = rows,
                Lines = new[] { "Positions and directions are in the launch frame (object-space axes, origin at surface 1's vertex): place the non-sequential objects in the same frame to trace the set with the Critical Ray Tracer." },
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, notes);
    }
}
