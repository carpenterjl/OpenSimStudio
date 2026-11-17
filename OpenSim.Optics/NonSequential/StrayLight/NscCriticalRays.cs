using System.ComponentModel;
using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>The ray pattern a critical rayset was made with (help, "Critical Rayset Generator":
/// "The rayset flag is 0 for an XY fan, 1 for an X fan, 2 for a Y fan, 3 for a ring and the chief
/// ray, 4 for a list of rays, 6 for a grid of rays, and 8 for the marginals and the chief").</summary>
public enum NscRaysetPattern
{
    XyFan = 0,
    XFan = 1,
    YFan = 2,
    ChiefAndRing = 3,
    List = 4,
    Grid = 6,
    ChiefAndMarginals = 8,
}

/// <summary>Each ray's role (help, "Critical Rayset Generator": "0 for the chief ray, 1 for
/// marginal rays, 2 for grid rays, 3 for ring rays, 4 for rays that are part of an Y-Fan and are
/// neither the chief or a marginal, 5 ... X-Fan ..., 6 ... XY-Fan ..., 7 for rays that are part
/// of a list").</summary>
public enum NscCriticalRayFlag
{
    Chief = 0,
    Marginal = 1,
    Grid = 2,
    Ring = 3,
    YFan = 4,
    XFan = 5,
    XyFan = 6,
    List = 7,
}

/// <summary>One critical ray: start and target end (meters, unit directions, relative to the
/// global reference: the non-sequential frame), wavelength (µm), intensity, field number (0 for a
/// list ray) and flag.</summary>
public sealed record NscCriticalRay(
    Vector3D Start, Vector3D StartDirection, double WavelengthMicrometers, double Intensity, int Field,
    NscCriticalRayFlag Flag, Vector3D End, Vector3D EndDirection);

/// <summary>
/// A critical rayset and its CRS file (plan item OP-84; help, "Critical Rayset Generator",
/// "Critical Ray Tracer"; docs/Optics-Conventions.md §78). The file is ASCII: "a header line
/// containing the number or rays (N), the units flag, the date the file was created in the format
/// YYYYMMDD, a rayset flag ... and the tilt around x,y, and z for the detectors used to measure the
/// locations of the final ray segment"; "a second header line which describes each column", "a
/// comment and ... not necessary if the .CRS file is created by hand"; then N rows of X Y Z L M N
/// (start), wavelength (µm), intensity, field number, flag, X Y Z L M N (end). "The units flag is 0
/// for meters, 1 for inches, 2 for centimeters, 3 for feet, and 4 for millimeters"; "a line
/// starting with "!" is a comment and is ignored".
/// <para>
/// Readings (§10 item 74): numbers are separated by spaces, tabs or commas; a non-numeric line
/// right after the header is the column line; a header with four tilts reads the first as the
/// "extra 180 degree rotation around the X-axis prepended to the other tilts" (image in mirror
/// space), applied before them. The writer folds that rotation into the three tilts, writes the
/// column line as a "!" comment and the tilts in the object tilt order of §69 (about x, then the
/// new y, then the new z).
/// </para>
/// </summary>
public sealed record NscCriticalRayset
{
    public required IReadOnlyList<NscCriticalRay> Rays { get; init; }
    /// <summary>The units the file states (lengths are held in meters).</summary>
    public required int UnitsFlag { get; init; }
    /// <summary>YYYYMMDD.</summary>
    public required int Date { get; init; }
    public required NscRaysetPattern Pattern { get; init; }
    /// <summary>The target detectors' orientation: local to the global frame, rotation only.</summary>
    public required RigidTransform TargetRotation { get; init; }

    /// <summary>Meters per file unit for <paramref name="flag"/>. The foot is 12 inches (derived
    /// from the inch factor of <see cref="OpticsConventions"/>).</summary>
    public static double MetersPerUnit(int flag) => flag switch
    {
        0 => 1,
        1 => OpticsConventions.ToMeters(1, LensUnit.Inch),
        2 => OpticsConventions.ToMeters(1, LensUnit.Centimeter),
        3 => 12 * OpticsConventions.ToMeters(1, LensUnit.Inch),
        4 => OpticsConventions.ToMeters(1, LensUnit.Millimeter),
        _ => throw new InvalidDataException($"CRS units flag {flag}: the help defines 0 (meters), 1 (inches), 2 (centimeters), 3 (feet) and 4 (millimeters)."),
    };

    /// <summary>The CRS units flag of a lens unit.</summary>
    public static int UnitsFlagOf(LensUnit unit) => unit switch
    {
        LensUnit.Meter => 0,
        LensUnit.Inch => 1,
        LensUnit.Centimeter => 2,
        LensUnit.Millimeter => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "No CRS units flag for this lens unit."),
    };

    /// <summary>Reads a CRS file; throws <see cref="InvalidDataException"/> naming the line.</summary>
    public static NscCriticalRayset Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path), Path.GetFileName(path));
    }

    public static NscCriticalRayset Parse(IReadOnlyList<string> lines, string what = "CRS file")
    {
        ArgumentNullException.ThrowIfNull(lines);
        int k = 0;
        string[]? Next(out int lineNumber)
        {
            while (k < lines.Count)
            {
                string t = lines[k++].Trim();
                if (t.Length == 0 || t.StartsWith('!')) continue;
                lineNumber = k;
                return t.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
            lineNumber = k;
            return null;
        }
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

        var header = Next(out int hl) ?? throw new InvalidDataException($"{what}: the file is empty.");
        var h = new double[header.Length];
        for (int i = 0; i < header.Length; i++)
            if (!Num(header[i], out h[i])) throw new InvalidDataException($"{what}, line {hl}: the header \"{string.Join(" ", header)}\" holds \"{header[i]}\", which is not a number.");
        if (h.Length is not (7 or 8))
            throw new InvalidDataException($"{what}, line {hl}: the header must hold N, the units flag, the date, the rayset flag and the x, y, z tilts (7 numbers, or 8 with the mirror-space rotation first); it holds {h.Length}.");
        if (h[0] < 0 || h[0] != Math.Floor(h[0])) throw new InvalidDataException($"{what}, line {hl}: N = {header[0]} is not a ray count.");
        int n = (int)h[0], unitsFlag = (int)h[1], date = (int)h[2], pattern = (int)h[3];
        double scale = MetersPerUnit(unitsFlag);
        if (!Enum.IsDefined(typeof(NscRaysetPattern), pattern))
            throw new InvalidDataException($"{what}, line {hl}: rayset flag {pattern} is not one of the help's 0, 1, 2, 3, 4, 6, 8.");
        var rotation = h.Length == 8
            ? RigidTransform.Compose(Rotation(h[4], 0, 0), Rotation(h[5], h[6], h[7]))
            : Rotation(h[4], h[5], h[6]);

        var rays = new List<NscCriticalRay>();
        bool first = true;
        while (true)
        {
            var row = Next(out int ln);
            if (row is null) break;
            var v = new double[row.Length];
            bool numeric = true;
            for (int i = 0; i < row.Length && numeric; i++) numeric = Num(row[i], out v[i]);
            if (!numeric)
            {
                if (first) { first = false; continue; }   // the column line
                throw new InvalidDataException($"{what}, line {ln}: \"{string.Join(" ", row)}\" is not a row of numbers.");
            }
            first = false;
            if (v.Length != 16)
                throw new InvalidDataException($"{what}, line {ln}: a ray row holds 16 numbers (start X Y Z L M N, wavelength, intensity, field, flag, end X Y Z L M N); it holds {v.Length}.");
            int flag = (int)v[9];
            if (v[9] != flag || !Enum.IsDefined(typeof(NscCriticalRayFlag), flag))
                throw new InvalidDataException($"{what}, line {ln}: ray flag {row[9]} is not 0 to 7.");
            if (!(v[6] > 0)) throw new InvalidDataException($"{what}, line {ln}: the wavelength {row[6]} µm must be positive.");
            var d0 = new Vector3D(v[3], v[4], v[5]);
            var d1 = new Vector3D(v[13], v[14], v[15]);
            if (!(d0.Length > 0) || !(d1.Length > 0)) throw new InvalidDataException($"{what}, line {ln}: a direction is zero.");
            rays.Add(new NscCriticalRay(new Vector3D(v[0], v[1], v[2]) * scale, d0 / d0.Length, v[6], v[7], (int)v[8],
                (NscCriticalRayFlag)flag, new Vector3D(v[10], v[11], v[12]) * scale, d1 / d1.Length));
        }
        if (rays.Count != n) throw new InvalidDataException($"{what}: the header says {n} rays; the file holds {rays.Count}.");
        return new NscCriticalRayset { Rays = rays, UnitsFlag = unitsFlag, Date = date, Pattern = (NscRaysetPattern)pattern, TargetRotation = rotation };
    }

    private static RigidTransform Rotation(double tx, double ty, double tz) =>
        new NscPlacement(0, 0, 0, tx, ty, tz).ToTransform();

    /// <summary>The tilts about x, then the new y, then the new z that rebuild a rotation
    /// (R = Rx(a)Ry(b)Rz(c): b = atan2(R13, √(R11² + R12²)), a = atan2(−R23, R33),
    /// c = atan2(−R12, R11), as §76 derives for NTxV).</summary>
    public static (double X, double Y, double Z) Tilts(RigidTransform r)
    {
        // Rij: row i, column j; column j is the image of local axis j.
        double r11 = r.Col0.X, r12 = r.Col1.X, r13 = r.Col2.X, r23 = r.Col2.Y, r33 = r.Col2.Z;
        double b = Math.Atan2(r13, Math.Sqrt(r11 * r11 + r12 * r12));
        double a = Math.Atan2(-r23, r33), c = Math.Atan2(-r12, r11);
        return (a * 180 / Math.PI, b * 180 / Math.PI, c * 180 / Math.PI);
    }

    /// <summary>The file's text in <paramref name="unitsFlag"/>'s unit.</summary>
    public string Format(int unitsFlag)
    {
        double scale = 1 / MetersPerUnit(unitsFlag);
        var (tx, ty, tz) = Tilts(TargetRotation);
        string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var b = new StringBuilder();
        b.Append(string.Join(" ", Rays.Count.ToString(CultureInfo.InvariantCulture), unitsFlag.ToString(CultureInfo.InvariantCulture),
            Date.ToString(CultureInfo.InvariantCulture), ((int)Pattern).ToString(CultureInfo.InvariantCulture), N(tx), N(ty), N(tz))).Append('\n');
        b.Append("! X Y Z L M N Wavelength Intensity Field Flag Xend Yend Zend Lend Mend Nend\n");
        foreach (var r in Rays)
            b.Append(string.Join(" ", N(r.Start.X * scale), N(r.Start.Y * scale), N(r.Start.Z * scale),
                N(r.StartDirection.X), N(r.StartDirection.Y), N(r.StartDirection.Z), N(r.WavelengthMicrometers), N(r.Intensity),
                r.Field.ToString(CultureInfo.InvariantCulture), ((int)r.Flag).ToString(CultureInfo.InvariantCulture),
                N(r.End.X * scale), N(r.End.Y * scale), N(r.End.Z * scale), N(r.EndDirection.X), N(r.EndDirection.Y), N(r.EndDirection.Z))).Append('\n');
        return b.ToString();
    }

    public void Write(string path) => File.WriteAllText(path, Format(UnitsFlag), new UTF8Encoding(false));

    /// <summary>The help's names of the ray flags.</summary>
    public static string FlagName(NscCriticalRayFlag f) => f switch
    {
        NscCriticalRayFlag.Chief => "Chief",
        NscCriticalRayFlag.Marginal => "Marginal",
        NscCriticalRayFlag.Grid => "Grid",
        NscCriticalRayFlag.Ring => "Ring",
        NscCriticalRayFlag.YFan => "Y-Fan",
        NscCriticalRayFlag.XFan => "X-Fan",
        NscCriticalRayFlag.XyFan => "XY-Fan",
        _ => "List",
    };
}

/// <summary>One ray's outcome in the Critical Ray Tracer.</summary>
public sealed record NscCriticalRayOutcome
{
    public required int Number { get; init; }
    public required NscCriticalRay Ray { get; init; }
    public required bool Pass { get; init; }
    /// <summary>The object the ray terminated on; 0 for none; −1 for the inserted target.</summary>
    public required int TerminationObject { get; init; }
    /// <summary>The terminating segment's end point and direction (NaN when the ray was not traced).</summary>
    public required Vector3D End { get; init; }
    public required Vector3D EndDirection { get; init; }
    public required double PositionError { get; init; }
    public required double AngleErrorDegrees { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// The Critical Ray Tracer (plan item OP-84; help, "Critical Ray Tracer (analyze tab,
/// non-sequential)"; docs/Optics-Conventions.md §78). Each ray of a critical rayset "is launched at
/// the starting position and direction given in the input file and traced through the current
/// system" and graded: "the Critical Ray Tracer inserts a detector at the target XYZ values with
/// the tilts given in the file header. If the ray terminates on any other object or does not
/// terminate on any object, the ray will FAIL. If the ray lands on the inserted detector, but the
/// XYZ values or direction of the ray on the detector are outside of the tolerance ... the ray will
/// FAIL." "Errors are ignored. Polarization, splitting and scattering are not used." The inserted
/// detector is an absorbing plane through the target point (one plane for every target on it,
/// within the glue distance), large enough to cross the whole scene; it is the last object, so it
/// decides a contact it shares. A ray whose wavelength matches no system wavelength (within 10⁻⁶
/// relative) aborts the run, naming it.
/// </summary>
public static class NscCriticalRayTracer
{
    /// <summary>The help's limit: "(max number of fields)*(max number of wavelengths)*(max number
    /// of rays)^2 = 127008".</summary>
    public const int MaxRays = 127_008;

    public static IReadOnlyList<NscCriticalRayOutcome> Trace(NscScene scene, NscTraceSettings settings, NscCriticalRayset rayset,
        IReadOnlyList<double> systemWavelengths, double positionTolerance, double angleToleranceDegrees, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rayset);
        ArgumentNullException.ThrowIfNull(systemWavelengths);
        if (rayset.Rays.Count > MaxRays)
            throw new ArgumentException($"Critical ray tracer: {rayset.Rays.Count} rays; the help limits a rayset to {MaxRays}.");
        if (!(positionTolerance >= 0) || !(angleToleranceDegrees >= 0))
            throw new ArgumentException("Critical ray tracer: the tolerances must be 0 or more.");
        var waves = new double[rayset.Rays.Count];
        for (int i = 0; i < waves.Length; i++)
        {
            double w = rayset.Rays[i].WavelengthMicrometers;
            int match = -1;
            for (int j = 0; j < systemWavelengths.Count; j++)
                if (Math.Abs(systemWavelengths[j] - w) <= 1e-6 * systemWavelengths[j]) { match = j; break; }
            if (match < 0)
                throw new ArgumentException($"Critical ray tracer: ray {i + 1}'s wavelength {StudyReport.Number(w, "g8")} µm matches no system wavelength ({string.Join(", ", systemWavelengths.Select(x => StudyReport.Number(x, "g8")))} µm); the help: \"the Critical Ray Tracer issues an error message and aborts\".");
            waves[i] = systemWavelengths[match];
        }
        var traceSettings = settings with
        {
            Splitting = NscSplitting.None, UsePolarization = false, ScatterRays = false, IgnoreErrors = true,
        };
        var normal = rayset.TargetRotation.ApplyDirection(Vector3D.UnitZ);
        double size = SceneSize(scene);
        foreach (var r in rayset.Rays) size = Math.Max(size, r.End.Length + r.Start.Length);
        double half = 10 * size + 1;

        // Group rays by wavelength and target plane.
        var groups = new List<(double Wave, Vector3D Point, List<int> Rays)>();
        for (int i = 0; i < rayset.Rays.Count; i++)
        {
            var e = rayset.Rays[i].End;
            int g = groups.FindIndex(x => x.Wave == waves[i] && Math.Abs(Vector3D.Dot(e - x.Point, normal)) <= traceSettings.GlueDistance);
            if (g < 0) groups.Add((waves[i], e, new List<int> { i }));
            else groups[g].Rays.Add(i);
        }
        var outcomes = new NscCriticalRayOutcome[rayset.Rays.Count];
        int target = scene.Objects.Count + 1;
        foreach (var (wave, point, members) in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var (tx, ty, tz) = NscCriticalRayset.Tilts(rayset.TargetRotation);
            var plane = new NscObject
            {
                Geometry = new NscRectangle(half, half),
                Placement = new NscPlacement(point.X, point.Y, point.Z, tx, ty, tz),
                Material = NscMaterial.Absorb,
                Comment = "Critical ray tracer target",
            };
            var withTarget = scene with { Objects = scene.Objects.Append(plane).ToArray() };
            var tracer = new NscTracer(withTarget, wave, traceSettings);
            foreach (int i in members)
            {
                var ray = rayset.Rays[i];
                NscRayTree tree;
                try
                {
                    tree = tracer.TraceSingle(new NscLaunchRay { Position = ray.Start, Direction = ray.StartDirection, Intensity = ray.Intensity > 0 ? ray.Intensity : 1 }, i);
                }
                catch (ArgumentException e)
                {
                    var nan = new Vector3D(double.NaN, double.NaN, double.NaN);
                    outcomes[i] = new NscCriticalRayOutcome
                    {
                        Number = i + 1, Ray = ray, Pass = false, TerminationObject = 0, End = nan, EndDirection = nan,
                        PositionError = double.NaN, AngleErrorDegrees = double.NaN, Reason = e.Message,
                    };
                    continue;
                }
                var last = tree.Segments[^1];
                int termination = last.Hit == target ? -1 : last.Hit;
                double dp = (last.Position - ray.End).Length;
                double da = Math.Atan2(Vector3D.Cross(last.Direction, ray.EndDirection).Length, Vector3D.Dot(last.Direction, ray.EndDirection)) * 180 / Math.PI;
                bool onTarget = termination == -1;
                bool pass = onTarget && dp <= positionTolerance && da <= angleToleranceDegrees;
                string reason = !onTarget ? (last.Hit == 0 ? "terminated on no object" : $"terminated on object {last.Hit}")
                    : pass ? "" : dp > positionTolerance ? "position outside the tolerance" : "direction outside the tolerance";
                if (tree.Errors.Count > 0) reason += (reason.Length > 0 ? "; " : "") + "trace error: " + tree.Errors[0];
                outcomes[i] = new NscCriticalRayOutcome
                {
                    Number = i + 1, Ray = ray, Pass = pass, TerminationObject = termination, End = last.Position, EndDirection = last.Direction,
                    PositionError = onTarget ? dp : double.NaN, AngleErrorDegrees = onTarget ? da : double.NaN, Reason = reason,
                };
            }
        }
        return outcomes;
    }

    private static double SceneSize(NscScene scene)
    {
        var placements = scene.GlobalPlacements();
        double size = 0;
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var b = scene.Objects[i].Geometry.LocalBounds;
            size = Math.Max(size, placements[i].Translation.Length + (b.Max - b.Min).Length);
        }
        return size;
    }
}

/// <summary>"Rays to Display".</summary>
public enum NscCriticalRaysShown
{
    All = 0,
    Fail = 1,
    Pass = 2,
}

/// <summary>"Hit Data".</summary>
public enum NscCriticalHitData
{
    Xyz = 0,
    Lmn = 1,
    XyzAndLmn = 2,
}

/// <summary>Settings of the <see cref="NscCriticalRayTracerAnalysis"/> (help, "Critical Ray Tracer (analyze tab, non-sequential)").</summary>
public sealed record NscCriticalRayTracerSettings
{
    [DisplayName("Input file"), Description("The full path of the CRS file (the help's search of the current directory for \"-PROD.CRS\" needs a lens folder, which analyses do not have).")]
    public string InputFile { get; init; } = "";

    [DisplayName("Rays to display"), Description("All rays, only failed rays, or only passing rays.")]
    public NscCriticalRaysShown RaysToDisplay { get; init; } = NscCriticalRaysShown.All;

    [DisplayName("Position tolerance"), Description("Largest allowed distance between the target and the actual end point, lens units.")]
    public double PositionTolerance { get; init; } = 0.001;

    [DisplayName("Angle tolerance"), Description("Largest allowed angle between the target and the actual ray vector, degrees.")]
    public double AngleTolerance { get; init; } = 0.01;

    [DisplayName("Hit data"), Description("XYZ, LMN, or both, for the data columns shown.")]
    public NscCriticalHitData HitData { get; init; } = NscCriticalHitData.Xyz;

    [DisplayName("Display start data"), Description("Show each ray's start.")]
    public bool DisplayStartData { get; init; }

    [DisplayName("Display actual end data"), Description("Show the terminating segment's end.")]
    public bool DisplayActualEndData { get; init; } = true;

    [DisplayName("Display target end data"), Description("Show the target end from the file.")]
    public bool DisplayTargetEndData { get; init; }
}

/// <summary>The Critical Ray Tracer as a registered analysis (id <c>NscCriticalRayTracer</c>).</summary>
public sealed class NscCriticalRayTracerAnalysis : OpticsAnalysis<NscCriticalRayTracerSettings>
{
    public const string SavedId = "NscCriticalRayTracer";
    public override string Id => SavedId;
    public override string Name => "Critical ray tracer";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscCriticalRayTracerSettings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.InputFile))
            throw new ArgumentException($"{Name}: give the full path of a CRS file (made by the Critical Rayset Generator or by hand).");
        var notes = new List<string>();
        var (scene, trace, unit) = NscStrayLightInput.Trace(context, false, false, true, false, notes, Name);
        var system = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>());
        var rayset = NscCriticalRayset.Read(settings.InputFile);
        var outcomes = NscCriticalRayTracer.Trace(scene, trace, rayset, system.Wavelengths.Wavelengths.Select(w => w.Micrometers).ToArray(),
            OpticsConventions.ToMeters(settings.PositionTolerance, unit), settings.AngleTolerance, cancellation);
        return OpticsAnalysisResult.FromSections(Name, Report(outcomes, settings, unit, rayset), notes.Append(
            "Errors are ignored; polarization, splitting and scattering are not used (help, \"Critical Ray Tracer\"). The target is an absorbing plane through each target point with the file's tilts.").ToArray());
    }

    public static IReadOnlyList<ReportSection> Report(IReadOnlyList<NscCriticalRayOutcome> outcomes, NscCriticalRayTracerSettings s, LensUnit unit,
        NscCriticalRayset rayset)
    {
        string u = Paraxial.ParaxialReport.UnitName(unit);
        string L(double m) => double.IsNaN(m) ? "—" : StudyReport.Number(OpticsConventions.FromMeters(m, unit), "g10");
        string N(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g10");
        int pass = outcomes.Count(o => o.Pass), total = outcomes.Count;
        double pp = total == 0 ? 0 : 100.0 * pass / total;
        var headers = new List<string> { "Ray", "Field", "Wavelength (µm)", "Ray flag", "Result", "Termination object" };
        bool xyz = s.HitData != NscCriticalHitData.Lmn, lmn = s.HitData != NscCriticalHitData.Xyz;
        void Cols(string what)
        {
            if (xyz) headers.AddRange(new[] { $"{what} X ({u})", $"{what} Y ({u})", $"{what} Z ({u})" });
            if (lmn) headers.AddRange(new[] { $"{what} L", $"{what} M", $"{what} N" });
        }
        if (s.DisplayStartData) Cols("Start");
        if (s.DisplayActualEndData) Cols("Actual end");
        if (s.DisplayTargetEndData) Cols("Target end");
        headers.AddRange(new[] { $"Position error ({u})", "Angle error (°)", "Why it failed" });
        var rows = new List<IReadOnlyList<string>>();
        foreach (var o in outcomes)
        {
            if (s.RaysToDisplay == NscCriticalRaysShown.Fail && o.Pass || s.RaysToDisplay == NscCriticalRaysShown.Pass && !o.Pass) continue;
            var row = new List<string>
            {
                o.Number.ToString(CultureInfo.InvariantCulture),
                o.Ray.Flag == NscCriticalRayFlag.List ? "NA" : o.Ray.Field.ToString(CultureInfo.InvariantCulture),
                StudyReport.Number(o.Ray.WavelengthMicrometers, "g8"), NscCriticalRayset.FlagName(o.Ray.Flag),
                o.Pass ? "pass" : "FAIL",
                o.TerminationObject == -1 ? "Target Location" : o.TerminationObject.ToString(CultureInfo.InvariantCulture),
            };
            void Data(Vector3D p, Vector3D d)
            {
                if (xyz) row.AddRange(new[] { L(p.X), L(p.Y), L(p.Z) });
                if (lmn) row.AddRange(new[] { N(d.X), N(d.Y), N(d.Z) });
            }
            if (s.DisplayStartData) Data(o.Ray.Start, o.Ray.StartDirection);
            if (s.DisplayActualEndData) Data(o.End, o.EndDirection);
            if (s.DisplayTargetEndData) Data(o.Ray.End, o.Ray.EndDirection);
            row.AddRange(new[] { L(o.PositionError), N(o.AngleErrorDegrees), o.Reason });
            rows.Add(row);
        }
        return new[]
        {
            new ReportSection("Critical ray tracer")
            {
                Lines = new[] { $"{StudyReport.Number(pp, "g6")} % of the rays pass; {StudyReport.Number(100 - pp, "g6")} % fail ({pass} of {total} pass)." },
                Pairs = new (string, string)[]
                {
                    ("Rayset", $"{rayset.Rays.Count} rays, pattern flag {(int)rayset.Pattern} ({rayset.Pattern}), units flag {rayset.UnitsFlag}, dated {rayset.Date}"),
                    ("Tolerances", $"{StudyReport.Number(s.PositionTolerance, "g6")} {u}, {StudyReport.Number(s.AngleTolerance, "g6")}°"),
                },
                TableHeaders = headers,
                TableRows = rows,
            },
        };
    }
}
