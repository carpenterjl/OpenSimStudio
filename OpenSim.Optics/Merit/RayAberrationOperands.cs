using System.Globalization;
using System.Runtime.CompilerServices;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Merit;

/// <summary>
/// OP-56: the operands the Optimization Wizard writes (<see cref="DefaultMeritWizard"/>): the
/// ray-aberration operands of the default merit function and its glass and air thickness
/// boundaries (docs/Optics-Conventions.md §40).
/// <para>
/// <b>Definitions</b> are read from OpticStudio's 2025 R2 help, the alphabetical operand list
/// (Optimization_Operands_Alphabetically, copy in <c>plans/sources/optics/op-54-help/</c>):
/// TRAR "Transverse aberration radial direction measured in image space at the wavelength defined
/// by Wave with respect to the chief ray. See ANAR"; ANAR "with respect to the primary wavelength
/// chief ray", ρ = √((l − l_c)² + (m − m_c)²), l and m the ray's x and y direction cosines; TRAX,
/// TRAY, ANAX, ANAY their x and y components; TRAC "with respect to the centroid ... OpticStudio
/// traces all TRAC rays with a common field point together, and then uses the collective data to
/// compute the centroid of all the rays"; TRCX, TRCY, ANAC, ANCX and ANCY the same with the
/// centroid; OPDC "Optical path difference with respect to chief ray in waves at the wavelength
/// defined by Wave"; OPDM "with respect to the mean OPD over the pupil"; OPDX "with respect to the
/// mean OPD over the pupil with tilt removed". The boundary operands MNCG, MXCG, MNEG, MNCA, MXCA
/// and MNEA constrain "each of the [center or edge] thicknesses of surfaces from Surf1 to Surf2
/// which have a non-air glass type" (G) or "air (i.e. no glass) as the glass type" (A).
/// </para>
/// <para>
/// <b>Groups, and why these operands trace their own rays.</b> The evaluator's
/// <see cref="MeritOperandDefinition.Ray"/> hook collects one ray per row, and a row's
/// <see cref="MeritRowContext.Ray"/> can read only rays some row asked for. A centroid, a mean
/// OPD or a fitted tilt needs every ray of the row's field, and every chief-ray reference needs a
/// second ray (the chief), so neither fits the one-ray hook; and an OPD needs the reference sphere,
/// which a traced path alone does not give. In OpticStudio the group is "TRAC operands ... grouped
/// together by field position and wavelength" in the editor; a row's
/// <see cref="MeritOperandDefinition.Compute"/> here sees only its own row, so the centroid
/// operands carry their group in their own columns instead: Int1 the rings (Gaussian quadrature)
/// or the grid (rectangular array), Data5 the arms (0 for the rectangular array) and Data6 0 for
/// one reference over every weighted wavelength of the field (OpticStudio's default, lateral
/// colour counted) or 1 for one per wavelength ("Ignore Lateral Color"). From these
/// <see cref="Compute"/> rebuilds the pupil rule (OP-40's <see cref="PupilQuadrature"/>), traces the
/// whole group once per lens, system and group, keeps it in a cache keyed on the lens and
/// system instances (both immutable records, and one instance per configuration for a whole
/// evaluation; a new lens, as every optimizer step makes, starts a new cache entry, and the entries
/// go with their lens), and reads the row's own ray from it. The group is always traced whole, so
/// the reference does not depend on which rows exist: the wizard's symmetric halving removes rows,
/// never reference rays. The chief-ray operands (TRAR, TRAX, TRAY, ANAR, ANAX, ANAY) use the
/// <see cref="MeritOperandDefinition.Ray"/> hook for their own ray, and the cache for the primary
/// wavelength's chief ray. Pass 3 of the evaluator is sequential, so the group traces are too; their
/// values do not depend on threads.
/// </para>
/// <para>
/// <b>Tracing.</b> The spot and angular rays are traced as the evaluator traces a row's ray
/// (<see cref="SequentialTrace"/> with the automatic semi-diameters sized once, launched by
/// <see cref="RayLauncher"/>), so a chief-ray row's own ray and its reference come from the same
/// trace. The OPD is <see cref="OpdEngine"/>'s, against the exit-pupil reference sphere centred
/// on the chief ray, on the lens with its floating semi-diameters frozen, exactly as OP-40's RMS
/// analyses compute it (<c>RmsCore</c>): a centroid group over every wavelength is referred to the
/// primary wavelength's sphere, one per wavelength to its own. The OPD rows' values are in waves of
/// the row's own wavelength, as OPDC's definition says.
/// </para>
/// <para>
/// <b>Failed rays.</b> Gaussian quadrature "assumes the pupil is a circle" (help, "Gaussian
/// Quadrature"): one failed ray in a Gaussian group fails every row of that group, naming the rays.
/// A rectangular-array group leaves its failed rays out of the reference (help, "Rectangular
/// Array": rays outside the pupil are omitted); a row whose own ray failed fails.
/// </para>
/// </summary>
public static class RayAberrationOperands
{
    private const string AlphaPage = "Optimization Operands Alphabetically";
    private const string ImageCategory = "Aberrations";
    private const string BoundaryCategory = "Constraints on lens data";

    /// <summary>The OP-56 operands, in registration order.</summary>
    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        // Chief-ray references (primary wavelength's chief ray).
        yield return Chief("TRAR", "Transverse aberration radius (chief ray)",
            "Radial transverse aberration in image space, lens units, at wavelength Wave, from the primary wavelength's chief ray.",
            ChiefQuantity.SpotRadius);
        yield return Chief("TRAX", "Transverse aberration x (chief ray)",
            "x transverse aberration in image space, lens units, at wavelength Wave, from the primary wavelength's chief ray.",
            ChiefQuantity.SpotX);
        yield return Chief("TRAY", "Transverse aberration y (chief ray)",
            "y transverse aberration in image space, lens units, at wavelength Wave, from the primary wavelength's chief ray.",
            ChiefQuantity.SpotY);
        yield return Chief("ANAR", "Angular aberration radius (chief ray)",
            "√((l − l_c)² + (m − m_c)²) in image space at wavelength Wave, l and m the ray's direction cosines, c the primary wavelength's chief ray.",
            ChiefQuantity.AngularRadius);
        yield return Chief("ANAX", "Angular aberration x (chief ray)",
            "l − l_c in image space at wavelength Wave, l the ray's x direction cosine, c the primary wavelength's chief ray.",
            ChiefQuantity.AngularX);
        yield return Chief("ANAY", "Angular aberration y (chief ray)",
            "m − m_c in image space at wavelength Wave, m the ray's y direction cosine, c the primary wavelength's chief ray.",
            ChiefQuantity.AngularY);
        yield return new MeritOperandDefinition
        {
            Code = "OPDC", Name = "Optical path difference (chief ray)", Category = ImageCategory, Source = AlphaPage,
            Description = "OPD of the ray against the reference sphere centred on its own wavelength's chief ray, in waves at wavelength Wave (no mean or tilt removed).",
            Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py" },
            Compute = c => ComputeOpdc(c),
        };

        // Centroid (grouped) references.
        yield return Grouped("TRAC", "Transverse aberration radius (centroid)",
            "Radial transverse aberration in image space, lens units, at wavelength Wave, from the weighted centroid of the row's group.",
            GroupQuantity.SpotRadius);
        yield return Grouped("TRCX", "Transverse aberration x (centroid)",
            "x transverse aberration in image space, lens units, at wavelength Wave, from the weighted centroid of the row's group.",
            GroupQuantity.SpotX);
        yield return Grouped("TRCY", "Transverse aberration y (centroid)",
            "y transverse aberration in image space, lens units, at wavelength Wave, from the weighted centroid of the row's group.",
            GroupQuantity.SpotY);
        yield return Grouped("ANAC", "Angular aberration radius (centroid)",
            "√((l − l_c)² + (m − m_c)²) at wavelength Wave, c the weighted centroid of the direction cosines of the row's group.",
            GroupQuantity.AngularRadius);
        yield return Grouped("ANCX", "Angular aberration x (centroid)",
            "l − l_c at wavelength Wave, c the weighted centroid of the x direction cosines of the row's group.",
            GroupQuantity.AngularX);
        yield return Grouped("ANCY", "Angular aberration y (centroid)",
            "m − m_c at wavelength Wave, c the weighted centroid of the y direction cosines of the row's group.",
            GroupQuantity.AngularY);
        yield return Grouped("OPDM", "Optical path difference, mean removed",
            "OPD in waves at wavelength Wave less the weighted mean OPD of the row's group.",
            GroupQuantity.OpdMean);
        yield return Grouped("OPDX", "Optical path difference, mean and tilt removed",
            "OPD in waves at wavelength Wave less the weighted least-squares plane a + b·Px + c·Py of the row's group.",
            GroupQuantity.OpdTilt);

        // Thickness boundaries of the wizard (help: Boundary Values).
        yield return MediumBound("MNCG", "Minimum center thickness glass", glass: true, atLeast: true, edge: false);
        yield return MediumBound("MXCG", "Maximum center thickness glass", glass: true, atLeast: false, edge: false);
        yield return MediumBound("MNEG", "Minimum edge thickness glass", glass: true, atLeast: true, edge: true);
        yield return MediumBound("MNCA", "Minimum center thickness air", glass: false, atLeast: true, edge: false);
        yield return MediumBound("MXCA", "Maximum center thickness air", glass: false, atLeast: false, edge: false);
        yield return MediumBound("MNEA", "Minimum edge thickness air", glass: false, atLeast: true, edge: true);
    }

    /// <summary>The group columns' labels: Int1, Data5 and Data6 (see the class remarks).</summary>
    public const string RingsLabel = "Rings/Grid";
    public const string ArmsLabel = "Arms";
    public const string PerWaveLabel = "Per-wave ref";

    /// <summary>The largest arms and grid a row may name (the rectangular array's limit is the RMS
    /// analyses' 1001, OP-40).</summary>
    public const int MaximumArms = 1000;
    public const int MaximumGrid = 1001;

    /// <summary>The pupil rule a grouped row names: Gaussian quadrature of Int1 rings × Data5 arms,
    /// or (Data5 = 0) the rectangular array Int1 across. Throws naming the column.</summary>
    public static PupilQuadratureRule RuleOf(MeritOperandRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        double armsValue = row.DataAt(5);
        if (!(armsValue >= 0) || armsValue != Math.Floor(armsValue) || armsValue > MaximumArms)
            throw new MeritOperandException($"{ArmsLabel} (Data5) is {F(armsValue)}; it must be a whole number of arms, 1 to {MaximumArms}, or 0 for the rectangular array. {Regenerate}");
        int arms = (int)armsValue;
        int density = row.Int1;
        if (arms == 0 && (density < 1 || density > MaximumGrid))
            throw new MeritOperandException($"{RingsLabel} (Int1) is {density}; the rectangular array needs 1 to {MaximumGrid} rays across. {Regenerate}");
        try { return arms > 0 ? PupilQuadrature.Gaussian(density, arms) : PupilQuadrature.Rectangular(density); }
        catch (ArgumentOutOfRangeException e)
        {
            throw new MeritOperandException($"{RingsLabel} (Int1) {density}, {ArmsLabel} (Data5) {arms}: {FirstLine(e.Message)} {Regenerate}");
        }
    }

    /// <summary>Whether a grouped row's reference is per wavelength (Data6 = 1) or over every
    /// weighted wavelength of the field (Data6 = 0).</summary>
    public static bool PerWave(MeritOperandRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DataAt(6) switch
        {
            0 => false,
            1 => true,
            var v => throw new MeritOperandException($"{PerWaveLabel} (Data6) is {F(v)}; it must be 0 (one reference for every wavelength of the field) or 1 (one per wavelength)."),
        };
    }

    private const string Regenerate = "These columns are written by the Optimization Wizard; regenerate the rows with it.";

    // ------------------------------------------------------------------ chief-ray operands

    private enum ChiefQuantity { SpotRadius, SpotX, SpotY, AngularRadius, AngularX, AngularY }

    private static MeritOperandDefinition Chief(string code, string name, string description, ChiefQuantity q) => new()
    {
        Code = code, Name = name, Category = ImageCategory, Source = AlphaPage, Description = description,
        Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py" },
        Ray = RayOf,
        Compute = c =>
        {
            c.Wavelength(c.Row.Int2);
            var own = c.Ray(RayOf(c.Row));
            if (!own.Final.IsOk) throw new MeritOperandException($"{Describe(c.Row)} failed: {RayStatuses.Label(own.Status)} at surface {own.FailedAtSurface}.");
            var chief = For(c).PrimaryChief(c.Row.DataAt(1), c.Row.DataAt(2));
            var p = own.Final.Position;
            var d = own.Final.Direction;
            double dx = p.X - chief.Position.X, dy = p.Y - chief.Position.Y;
            double dl = d.X - chief.Direction.X, dm = d.Y - chief.Direction.Y;
            return q switch
            {
                ChiefQuantity.SpotRadius => c.Length(Math.Sqrt(dx * dx + dy * dy)),
                ChiefQuantity.SpotX => c.Length(dx),
                ChiefQuantity.SpotY => c.Length(dy),
                ChiefQuantity.AngularRadius => Math.Sqrt(dl * dl + dm * dm),
                ChiefQuantity.AngularX => dl,
                ChiefQuantity.AngularY => dm,
                _ => throw new ArgumentOutOfRangeException(nameof(q)),
            };
        },
    };

    private static MeritRayRequest RayOf(MeritOperandRow row) => new(row.Int2, row.DataAt(1), row.DataAt(2), row.DataAt(3), row.DataAt(4));

    private static double ComputeOpdc(MeritRowContext c)
    {
        int wave = c.Wavelength(c.Row.Int2);
        double path = For(c).SingleOpd(wave, c.Row.DataAt(1), c.Row.DataAt(2), c.Row.DataAt(3), c.Row.DataAt(4), toPrimary: false, out double lambda);
        return OpticsConventions.InWaves(path, lambda);
    }

    // ------------------------------------------------------------------ grouped operands

    private enum GroupQuantity { SpotRadius, SpotX, SpotY, AngularRadius, AngularX, AngularY, OpdMean, OpdTilt }

    private static MeritOperandDefinition Grouped(string code, string name, string description, GroupQuantity q) => new()
    {
        Code = code, Name = name, Category = ImageCategory, Source = AlphaPage,
        Description = description + " The group is the field's rays over the pupil rule of Rings/Grid (Int1) and Arms (Data5, 0 for the rectangular array), at every weighted wavelength (Per-wave ref, Data6, 0) or at Wave alone (1).",
        Int1 = RingsLabel, Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py", ArmsLabel, PerWaveLabel },
        Compute = c =>
        {
            int wave = c.Wavelength(c.Row.Int2);
            var rule = RuleOf(c.Row);
            bool perWave = PerWave(c.Row);
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
            var prepared = For(c);
            if (q is GroupQuantity.OpdMean or GroupQuantity.OpdTilt)
            {
                var g = prepared.OpdGroupOf(hx, hy, rule, perWave ? wave : 0);
                if (q == GroupQuantity.OpdTilt && double.IsNaN(g.A))
                    throw new MeritOperandException($"the OPD group of field (Hx {F(hx)}, Hy {F(hy)}) does not span a plane in the pupil, so its tilt cannot be fitted.");
                double path = g.Find(wave, px, py) is { } s ? s.Value
                    : prepared.SingleOpd(wave, hx, hy, px, py, toPrimary: !perWave, out _);
                double reference = q == GroupQuantity.OpdMean ? g.Mean : g.Plane(px, py);
                return OpticsConventions.InWaves(path - reference, g.LambdaOf(wave, prepared));
            }
            var rays = prepared.RayGroupOf(hx, hy, rule, perWave ? wave : 0);
            var own = rays.Find(wave, px, py) ?? prepared.SingleRay(wave, hx, hy, px, py);
            double dx = own.X - rays.Cx, dy = own.Y - rays.Cy, dl = own.L - rays.Cl, dm = own.M - rays.Cm;
            return q switch
            {
                GroupQuantity.SpotRadius => c.Length(Math.Sqrt(dx * dx + dy * dy)),
                GroupQuantity.SpotX => c.Length(dx),
                GroupQuantity.SpotY => c.Length(dy),
                GroupQuantity.AngularRadius => Math.Sqrt(dl * dl + dm * dm),
                GroupQuantity.AngularX => dl,
                GroupQuantity.AngularY => dm,
                _ => throw new ArgumentOutOfRangeException(nameof(q)),
            };
        },
    };

    // ------------------------------------------------------------------ boundaries

    /// <summary>MNCG, MXCG, MNEG, MNCA, MXCA, MNEA: the MNCT pattern (help: Understanding Boundary
    /// Operands; <see cref="MeritOperandRegistry"/>'s MNCT), over the surfaces of Surf1 to Surf2
    /// whose following medium is glass (G) or the system's air (A). The medium after a mirror or a
    /// coordinate break is the one before it (the report analyses' rule, docs/Optics-Conventions.md
    /// §29). The edge thickness is the axial distance to the next surface plus that surface's sag
    /// minus this one's at (0, a), the "+y" edge the help names, a this surface's mechanical
    /// semi-diameter (Mode 0, the help's default; the clear one plus the chip zone when no
    /// mechanical one is set) or clear semi-diameter (Mode 1), scaled by Zone when Zone is not
    /// zero. Which data columns hold Zone and Mode the help does not say; Data1 and Data2 is this
    /// program's reading (§10 item 36).</summary>
    private static MeritOperandDefinition MediumBound(string code, string name, bool glass, bool atLeast, bool edge) => new()
    {
        Code = code, Name = name, Category = BoundaryCategory, Source = "Constraints on Lens Data; Understanding Boundary Operands; Optimization Wizard, Boundary Values",
        Description = $"Each {(edge ? "edge" : "center")} thickness of surfaces Surf1 to Surf2 followed by {(glass ? "glass" : "air")}, bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations."
            + (edge ? " Edge thickness along +y at the mechanical (Mode 0) or clear (Mode 1) semi-diameter, scaled by Zone when not zero." : ""),
        Int1 = "Surf1", Int2 = "Surf2", Data = edge ? new[] { "Zone", "Mode" } : Array.Empty<string?>(), IsBound = true,
        Compute = c =>
        {
            int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
            if (a > b) throw new MeritOperandException($"Surf1 ({a}) is after Surf2 ({b}).");
            if (b >= c.Lens.ImageIndex) b = c.Lens.ImageIndex - 1;   // the image surface has no thickness to constrain
            double zone = edge ? c.Row.DataAt(1) : 0, modeValue = edge ? c.Row.DataAt(2) : 0;
            if (edge && modeValue is not (0 or 1)) throw new MeritOperandException($"Mode (Data2) is {F(modeValue)}; it must be 0 (mechanical semi-diameter) or 1 (clear).");
            if (edge && !(zone >= 0 && double.IsFinite(zone))) throw new MeritOperandException($"Zone (Data1) is {F(zone)}; it must be 0 (the full semi-diameter) or a positive scale.");
            var media = ReportText.SpaceMedia(c.Lens);
            double t = c.Row.Target, violation = 0;
            for (int j = Math.Max(a, 1); j <= b; j++)
            {
                var s = c.Lens.Surfaces[j];
                if (s.IsCoordinateBreak || s.IsIgnored) continue;
                if (ReportText.IsAir(media[j]) == glass) continue;
                double thickness = edge ? EdgeThickness(c, j, zone, mechanical: modeValue == 0) : c.Length(s.Thickness);
                if (!double.IsFinite(thickness)) throw new MeritOperandException($"surface {j} has an infinite thickness.");
                if (atLeast && thickness < t) violation += t - thickness;
                else if (!atLeast && thickness > t) violation += thickness - t;
            }
            return atLeast ? t - violation : t + violation;
        },
    };

    private static double EdgeThickness(MeritRowContext c, int j, double zone, bool mechanical)
    {
        var geometry = For(c).Geometry();
        var s = c.Lens.Surfaces[j];
        double clear = geometry.SemiDiameter(j);
        if (!double.IsFinite(clear))
            throw new MeritOperandException($"surface {j}'s semi-diameter could not be sized ({geometry.SizingError ?? "automatic"}), so its edge thickness is unknown.");
        double radius = mechanical ? s.MechanicalSemiDiameter ?? clear + s.ChipZone : clear;
        if (zone != 0) radius *= zone;
        var next = c.Lens.Surfaces[j + 1];
        if (next.IsCoordinateBreak)
            throw new MeritOperandException($"surface {j + 1} is a coordinate break, so surface {j}'s edge thickness is not defined (the help: these operands ignore coordinate breaks; set the range to skip it).");
        if (geometry.CoaxialDistance(j, j + 1) is not double axial)
            throw new MeritOperandException($"surface {j + 1} is tilted or decentered against surface {j}, so the edge thickness along +y is not defined.");
        double edge = axial + ElementVolume.Sag(next, 0, radius) - ElementVolume.Sag(s, 0, radius);
        if (!double.IsFinite(edge))
            throw new MeritOperandException($"a sag is undefined at y = {F(c.Length(radius))} on surface {j} or {j + 1}, so the edge thickness is unknown.");
        return c.Length(edge);
    }

    // ------------------------------------------------------------------ the cache

    private static readonly ConditionalWeakTable<SequentialLens, ConditionalWeakTable<OpticalSystemData, Prepared>> Cache = new();

    private static Prepared For(MeritRowContext c) => For(c.Lens, c.System);

    /// <summary>The cached tracer and groups of one lens and system instance.</summary>
    internal static Prepared For(SequentialLens lens, OpticalSystemData system)
    {
        var bySystem = Cache.GetValue(lens, _ => new ConditionalWeakTable<OpticalSystemData, Prepared>());
        return bySystem.GetValue(system, s => new Prepared(lens, s));
    }

    private static string Describe(MeritOperandRow row) =>
        $"the ray (Hx {F(row.DataAt(1))}, Hy {F(row.DataAt(2))}, Px {F(row.DataAt(3))}, Py {F(row.DataAt(4))}, wavelength {row.Int2})";

    internal static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);

    private static string FirstLine(string message)
    {
        int at = message.IndexOf('\n');
        return (at < 0 ? message : message[..at]).TrimEnd('\r', ' ');
    }

    /// <summary>A traced ray of a group: wavelength (1-based), pupil point, weight (pupil weight
    /// times wavelength weight), image position (meters) and direction cosines.</summary>
    internal readonly record struct GroupRay(int Wave, double Px, double Py, double Weight, double X, double Y, double L, double M);

    /// <summary>A traced OPD of a group: path in meters, against the group's reference sphere.</summary>
    internal readonly record struct GroupOpd(int Wave, double Px, double Py, double Weight, double Value);

    /// <summary>Points of a group match a row's when within this distance in normalised pupil
    /// coordinates (the rule is rebuilt from the same integers, so a row the wizard wrote matches
    /// to the bit; the margin covers a value retyped to fewer digits).</summary>
    private const double PupilMatch = 1e-12;

    internal sealed class RayGroup
    {
        public required IReadOnlyList<GroupRay> Rays { get; init; }
        public double Cx { get; init; }
        public double Cy { get; init; }
        public double Cl { get; init; }
        public double Cm { get; init; }

        public GroupRay? Find(int wave, double px, double py)
        {
            foreach (var r in Rays)
                if (r.Wave == wave && Math.Abs(r.Px - px) <= PupilMatch && Math.Abs(r.Py - py) <= PupilMatch) return r;
            return null;
        }
    }

    internal sealed class OpdGroup
    {
        public required IReadOnlyList<GroupOpd> Samples { get; init; }
        public required IReadOnlyDictionary<int, double> Lambdas { get; init; }
        public double Mean { get; init; }
        public double A { get; init; }
        public double B { get; init; }
        public double C { get; init; }

        public double Plane(double px, double py) => A + B * px + C * py;

        public GroupOpd? Find(int wave, double px, double py)
        {
            foreach (var s in Samples)
                if (s.Wave == wave && Math.Abs(s.Px - px) <= PupilMatch && Math.Abs(s.Py - py) <= PupilMatch) return s;
            return null;
        }

        public double LambdaOf(int wave, Prepared prepared) => Lambdas.TryGetValue(wave, out double l) ? l : prepared.Lambda(wave);
    }

    private readonly record struct GroupKey(bool Opd, double Hx, double Hy, PupilIntegration Method, int Density, int Arms, int Wave);

    /// <summary>One lens and system: the tracer the evaluator would build, the frozen lens for the
    /// OPD, and the groups and chief rays traced so far. Guarded by one lock, so concurrent
    /// evaluations sharing a lens get the same values.</summary>
    internal sealed class Prepared
    {
        private readonly object _lock = new();
        private readonly SequentialLens _lens;
        private readonly OpticalSystemData _system;
        private bool _tracerReady;
        private string? _tracerError;
        private IReadOnlyList<double>? _semi;
        private RayLauncher? _launcher;
        private SequentialLens? _frozen;
        private LensGeometry? _geometry;
        private readonly Dictionary<int, SequentialTrace> _traces = new();
        private readonly Dictionary<int, string> _traceErrors = new();
        private readonly Dictionary<GroupKey, (object? Group, string? Error)> _groups = new();
        private readonly Dictionary<(double, double), (Ray Chief, string? Error)> _chiefs = new();

        public Prepared(SequentialLens lens, OpticalSystemData system)
        {
            _lens = lens;
            _system = system;
        }

        /// <summary>The air wavelength of wavelength number <paramref name="wave"/>, meters.</summary>
        public double Lambda(int wave)
        {
            lock (_lock) return OpticsConventions.MicrometersToMeters(Trace(wave).AirMicrometers);
        }

        public LensGeometry Geometry()
        {
            lock (_lock) return _geometry ??= LensGeometry.Create(_lens, _system, CancellationToken.None);
        }

        // ---- The evaluator's tracer (MeritEvaluator.ConfigurationState) ----

        private void PrepareTracer()
        {
            if (_tracerReady) return;
            _tracerReady = true;
            try
            {
                if (_lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
                    _semi = SemiDiameterSizing.Compute(_lens, _system).Clear;
                _launcher = new RayLauncher(_lens, _system);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                _tracerError = $"the lens cannot be traced: {FirstLine(e.Message)}";
            }
        }

        private SequentialTrace Trace(int wave)
        {
            PrepareTracer();
            if (_tracerError is not null) throw new MeritOperandException(_tracerError);
            if (_traces.TryGetValue(wave, out var t)) return t;
            if (_traceErrors.TryGetValue(wave, out var error)) throw new MeritOperandException(error);
            try { return _traces[wave] = SequentialTrace.Create(_lens, _system, wave - 1, _semi); }
            catch (ArgumentException e)
            {
                _traceErrors[wave] = $"the lens cannot be traced at wavelength {wave}: {FirstLine(e.Message)}";
                throw new MeritOperandException(_traceErrors[wave]);
            }
        }

        /// <summary>One ray's final state, or why it has none.</summary>
        private (Ray Ray, string? Error) TraceOne(int wave, double hx, double hy, double px, double py)
        {
            var trace = Trace(wave);
            try
            {
                var path = trace.TracePath(_launcher!.Launch(trace, hx, hy, px, py));
                return path.Final.IsOk ? (path.Final, null) : (path.Final, $"{RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}");
            }
            catch (InvalidOperationException e) { return (default, FirstLine(e.Message)); }
        }

        /// <summary>Why the ray fails, or null when it reaches the image (the wizard's vignetting check).</summary>
        public string? RayFailure(int wave, double hx, double hy, double px, double py)
        {
            lock (_lock) return TraceOne(wave, hx, hy, px, py).Error;
        }

        public Ray PrimaryChief(double hx, double hy)
        {
            lock (_lock)
            {
                if (!_chiefs.TryGetValue((hx, hy), out var entry))
                {
                    int primary = _system.Wavelengths.PrimaryIndex + 1;
                    var (ray, error) = TraceOne(primary, hx, hy, 0, 0);
                    entry = (ray, error is null ? null : $"the primary wavelength's chief ray (Hx {F(hx)}, Hy {F(hy)}) failed: {error}; there is no reference.");
                    _chiefs[(hx, hy)] = entry;
                }
                return entry.Error is null ? entry.Chief : throw new MeritOperandException(entry.Error);
            }
        }

        public GroupRay SingleRay(int wave, double hx, double hy, double px, double py)
        {
            lock (_lock)
            {
                var (ray, error) = TraceOne(wave, hx, hy, px, py);
                if (error is not null)
                    throw new MeritOperandException($"the ray (Hx {F(hx)}, Hy {F(hy)}, Px {F(px)}, Py {F(py)}, wavelength {wave}) failed: {error}.");
                return new GroupRay(wave, px, py, 1, ray.Position.X, ray.Position.Y, ray.Direction.X, ray.Direction.Y);
            }
        }

        /// <summary>The spot and angular group: every weighted wavelength (or <paramref name="onlyWave"/>)
        /// × every point of <paramref name="rule"/>, and the weighted centroids.</summary>
        public RayGroup RayGroupOf(double hx, double hy, PupilQuadratureRule rule, int onlyWave)
        {
            var key = new GroupKey(false, hx, hy, rule.Method, rule.Density, rule.Arms, onlyWave);
            lock (_lock)
            {
                if (!_groups.TryGetValue(key, out var entry))
                {
                    try { entry = (BuildRayGroup(hx, hy, rule, onlyWave), null); }
                    catch (MeritOperandException e) { entry = (null, e.Message); }
                    _groups[key] = entry;
                }
                return entry.Error is null ? (RayGroup)entry.Group! : throw new MeritOperandException(entry.Error);
            }
        }

        private IReadOnlyList<(int Wave, double Weight)> Waves(int onlyWave, string where)
        {
            if (onlyWave > 0) return new[] { (onlyWave, 1.0) };
            var waves = _system.Wavelengths.Wavelengths.Select((w, i) => (Wave: i + 1, w.Weight)).Where(w => w.Weight > 0).ToArray();
            if (waves.Length == 0)
                throw new MeritOperandException($"{where}: every wavelength has weight zero, so the group has no rays to reference.");
            return waves;
        }

        private RayGroup BuildRayGroup(double hx, double hy, PupilQuadratureRule rule, int onlyWave)
        {
            string where = $"the group of field (Hx {F(hx)}, Hy {F(hy)})";
            var rays = new List<GroupRay>();
            var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int launched = 0;
            foreach (var (wave, waveWeight) in Waves(onlyWave, where))
            {
                for (int i = 0; i < rule.Points.Count; i++)
                {
                    launched++;
                    var (px, py) = rule.Points[i];
                    var (ray, error) = TraceOne(wave, hx, hy, px, py);
                    if (error is not null)
                    {
                        failures[error] = failures.TryGetValue(error, out int n) ? n + 1 : 1;
                        continue;
                    }
                    rays.Add(new GroupRay(wave, px, py, rule.Weights[i] * waveWeight, ray.Position.X, ray.Position.Y, ray.Direction.X, ray.Direction.Y));
                }
            }
            CheckFailures(rule, where, launched, rays.Count, failures);
            double sw = 0, sx = 0, sy = 0, sl = 0, sm = 0;
            foreach (var r in rays)
            {
                sw += r.Weight;
                sx += r.Weight * r.X;
                sy += r.Weight * r.Y;
                sl += r.Weight * r.L;
                sm += r.Weight * r.M;
            }
            return new RayGroup { Rays = rays, Cx = sx / sw, Cy = sy / sw, Cl = sl / sw, Cm = sm / sw };
        }

        private static void CheckFailures(PupilQuadratureRule rule, string where, int launched, int used, SortedDictionary<string, int> failures)
        {
            string list = string.Join("; ", failures.Select(f => $"{f.Value} {f.Key}"));
            if (rule.Method == PupilIntegration.GaussianQuadrature && failures.Count > 0)
                throw new MeritOperandException(
                    $"{where}: Gaussian quadrature needs every ray of the pupil, but {launched - used} of {launched} failed ({list}). The pupil is vignetted: regenerate the rows with the rectangular array.");
            if (used == 0)
                throw new MeritOperandException($"{where}: no ray of the group reached the image ({list}).");
        }

        // ---- The OPD (OP-40's RmsCore setup) ----

        /// <summary>The lens with its floating semi-diameters frozen at their sized values, as
        /// RmsCore.Prepare freezes them.</summary>
        private SequentialLens Frozen()
        {
            if (_frozen is not null) return _frozen;
            var lens = _lens;
            if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            {
                PrepareTracer();
                if (_tracerError is not null) throw new MeritOperandException(_tracerError);
                var clear = _semi!;
                int stop = lens.StopIndex;
                lens = lens with
                {
                    Surfaces = lens.Surfaces.Select((s, j) => s.Aperture is FloatingAperture && s.SemiDiameter is null && j != stop
                        ? s with { SemiDiameter = clear[j] } : s).ToArray(),
                };
            }
            return _frozen = lens;
        }

        /// <summary>The system and the 0-based field row of (Hx, Hy): a table field that normalises
        /// to it, else the field appended with the launcher's vignetting factors there (as RmsCore
        /// appends a sampled field).</summary>
        private (OpticalSystemData System, int Field) FieldFor(double hx, double hy)
        {
            var fields = _system.Fields;
            var scale = fields.Scale;
            for (int i = 0; i < fields.Points.Count; i++)
            {
                var (nx, ny) = OpticsConventions.NormalizeField((fields.Points[i].X, fields.Points[i].Y), scale);
                if (Math.Abs(nx - hx) <= PupilMatch && Math.Abs(ny - hy) <= PupilMatch) return (_system, i);
            }
            PrepareTracer();
            if (_tracerError is not null) throw new MeritOperandException(_tracerError);
            var factors = fields.Points.Any(p => p.IsVignetted) ? _launcher!.VignettingAt(hx, hy) : default;
            var (x, y) = OpticsConventions.DenormalizeField((hx, hy), scale);
            var point = new FieldPoint
            {
                X = x, Y = y, Weight = 1,
                DecenterX = factors.DecenterX, DecenterY = factors.DecenterY,
                CompressionX = factors.CompressionX, CompressionY = factors.CompressionY, TiltDegrees = factors.TiltDegrees,
            };
            var extended = _system with { Fields = fields with { Points = fields.Points.Append(point).ToArray() } };
            return (extended, extended.Fields.Points.Count - 1);
        }

        private static OpdOptions OpdOptionsFor(bool toPrimary) => new()
        {
            ReferencePoint = OpdReferencePoint.ChiefRay, Reference = OpdReference.ExitPupil, ReferenceToPrimary = toPrimary,
        };

        /// <summary>One OPD (meters) and its wavelength (meters).</summary>
        public double SingleOpd(int wave, double hx, double hy, double px, double py, bool toPrimary, out double lambda)
        {
            lock (_lock)
            {
                var (system, field) = FieldFor(hx, hy);
                OpdResult r;
                try { r = OpdEngine.Compute(Frozen(), system, field, wave - 1, new[] { (px, py) }, OpdOptionsFor(toPrimary)); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                {
                    throw new MeritOperandException($"the OPD at field (Hx {F(hx)}, Hy {F(hy)}), wavelength {wave} cannot be computed: {FirstLine(e.Message)}");
                }
                var s = r.Samples[0];
                if (!s.IsValid) throw new MeritOperandException($"the ray (Hx {F(hx)}, Hy {F(hy)}, Px {F(px)}, Py {F(py)}, wavelength {wave}) has no OPD: {s.Failure}.");
                lambda = OpticsConventions.MicrometersToMeters(r.AirMicrometers);
                return s.Path;
            }
        }

        public OpdGroup OpdGroupOf(double hx, double hy, PupilQuadratureRule rule, int onlyWave)
        {
            var key = new GroupKey(true, hx, hy, rule.Method, rule.Density, rule.Arms, onlyWave);
            lock (_lock)
            {
                if (!_groups.TryGetValue(key, out var entry))
                {
                    try { entry = (BuildOpdGroup(hx, hy, rule, onlyWave), null); }
                    catch (MeritOperandException e) { entry = (null, e.Message); }
                    _groups[key] = entry;
                }
                return entry.Error is null ? (OpdGroup)entry.Group! : throw new MeritOperandException(entry.Error);
            }
        }

        private OpdGroup BuildOpdGroup(double hx, double hy, PupilQuadratureRule rule, int onlyWave)
        {
            string where = $"the OPD group of field (Hx {F(hx)}, Hy {F(hy)})";
            var (system, field) = FieldFor(hx, hy);
            var options = OpdOptionsFor(toPrimary: onlyWave == 0);
            var samples = new List<GroupOpd>();
            var lambdas = new Dictionary<int, double>();
            var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int launched = 0;
            foreach (var (wave, waveWeight) in Waves(onlyWave, where))
            {
                OpdResult r;
                try { r = OpdEngine.Compute(Frozen(), system, field, wave - 1, rule.Points, options); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                {
                    throw new MeritOperandException($"{where}, wavelength {wave}: {FirstLine(e.Message)}");
                }
                lambdas[wave] = OpticsConventions.MicrometersToMeters(r.AirMicrometers);
                for (int i = 0; i < r.Samples.Count; i++)
                {
                    launched++;
                    var s = r.Samples[i];
                    if (!s.IsValid)
                    {
                        failures[s.Failure!] = failures.TryGetValue(s.Failure!, out int n) ? n + 1 : 1;
                        continue;
                    }
                    samples.Add(new GroupOpd(wave, s.Px, s.Py, rule.Weights[i] * waveWeight, s.Path));
                }
            }
            CheckFailures(rule, where, launched, samples.Count, failures);

            // Piston: the weighted mean. Piston and tilt: the weighted least-squares plane
            // a + b·Px + c·Py (OP-40's RmsCore.Residual), from its normal equations.
            double sw = 0, sv = 0;
            var m = new double[3, 3];
            var rhs = new double[3];
            foreach (var s in samples)
            {
                sw += s.Weight;
                sv += s.Weight * s.Value;
                double[] phi = { 1, s.Px, s.Py };
                for (int j = 0; j < 3; j++)
                {
                    rhs[j] += s.Weight * phi[j] * s.Value;
                    for (int k = 0; k < 3; k++) m[j, k] += s.Weight * phi[j] * phi[k];
                }
            }
            var plane = Solve3(m, rhs);
            return new OpdGroup
            {
                Samples = samples, Lambdas = lambdas, Mean = sv / sw,
                A = plane?[0] ?? double.NaN, B = plane?[1] ?? double.NaN, C = plane?[2] ?? double.NaN,
            };
        }
    }

    /// <summary>Gaussian elimination with partial pivoting (the same as RmsCore's); null when singular.</summary>
    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        double scale = 0;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) scale = Math.Max(scale, Math.Abs(m[i, j]));
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (!(Math.Abs(m[pivot, col]) > 1e-12 * scale)) return null;
            if (pivot != col)
            {
                for (int k = 0; k < 3; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                (y[col], y[pivot]) = (y[pivot], y[col]);
            }
            for (int row = col + 1; row < 3; row++)
            {
                double f = m[row, col] / m[col, col];
                for (int k = col; k < 3; k++) m[row, k] -= f * m[col, k];
                y[row] -= f * y[col];
            }
        }
        var x = new double[3];
        for (int row = 2; row >= 0; row--)
        {
            double s = y[row];
            for (int k = row + 1; k < 3; k++) s -= m[row, k] * x[k];
            x[row] = s / m[row, row];
        }
        return x;
    }
}
