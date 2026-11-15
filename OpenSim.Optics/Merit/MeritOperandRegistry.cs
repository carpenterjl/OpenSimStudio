using System.Globalization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The merit operands this build knows, found by OpticStudio's four-letter code or searched by
/// code or OpenSim's descriptive name (OQ-13). <see cref="CreateDefault"/> is OP-54's starter set:
/// the control operands, CONF, the general math operands except EQUA, and a few first-order,
/// lens-data and real-ray operands. OP-55 adds the rest of the catalogue.
/// <para>
/// Every definition was read from OpticStudio's 2025 R2 help, saved under
/// <c>plans/sources/optics/op-54-help/</c>: the alphabetical operand list
/// (Optimization_Operands_Alphabetically.html) and the category pages named in each
/// <see cref="MeritOperandDefinition.Source"/>. The help names each operand's parameters (Surf,
/// Wave, Op#...) but not, for most, which of Int1, Int2 and Data1 to Data6 holds them. What it
/// does say: Data1 to Data4 hold Hx, Hy, Px and Py (Modifying the merit function, "Hx, Hy, Px,
/// and Py"); CTGT's surface is in Int1 (Understanding Boundary Operands); and the tables of
/// Defining complex operands put TTHI's two surfaces, CTVA's, CVVA's and COVA's surface, and
/// SUMM's, DIVI's, PROD's and DIFF's two rows and OPGT's row in Int1 and Int2. The rest follow
/// the same pattern (integers in Int1 then Int2, reals in Data1 on) and are this program's
/// reading, listed in docs/Optics-Conventions.md §10 item 27: Wave in Int2 (EFFL and the real-ray
/// operands), the trigonometric Flag in Int2, and DIVB's and PROB's Factor in Data1.
/// </para>
/// </summary>
public sealed class MeritOperandRegistry
{
    private readonly List<MeritOperandDefinition> _all = new();
    private readonly Dictionary<string, MeritOperandDefinition> _byCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every operand, in registration order.</summary>
    public IReadOnlyList<MeritOperandDefinition> All => _all;

    /// <summary>Adds an operand; a code may be registered once.</summary>
    public void Register(MeritOperandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Code.Length != 4 || !definition.Code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            throw new ArgumentException($"Merit operand code '{definition.Code}' is not four upper-case letters or digits.", nameof(definition));
        if (!_byCode.TryAdd(definition.Code, definition))
            throw new ArgumentException($"Merit operand {definition.Code} is registered twice.", nameof(definition));
        if (definition.Control == MeritControl.None && definition.Compute is null)
            throw new ArgumentException($"Merit operand {definition.Code} has no value computation.", nameof(definition));
        if (definition.Data.Count > MeritOperandRow.DataCount)
            throw new ArgumentException($"Merit operand {definition.Code} names {definition.Data.Count} real parameters; there are {MeritOperandRow.DataCount}.", nameof(definition));
        _all.Add(definition);
    }

    /// <summary>The operand with this code (ignoring case), or null.</summary>
    public MeritOperandDefinition? Find(string code) =>
        code is not null && _byCode.TryGetValue(code.Trim(), out var d) ? d : null;

    /// <summary>The operand with this code; throws naming the code when there is none.</summary>
    public MeritOperandDefinition Get(string code) =>
        Find(code) ?? throw new ArgumentException($"There is no merit operand '{code}'.", nameof(code));

    /// <summary>The operands whose code or name contains <paramref name="text"/> (ignoring case),
    /// exact code matches first; every operand for empty text.</summary>
    public IReadOnlyList<MeritOperandDefinition> Search(string text)
    {
        string q = (text ?? "").Trim();
        if (q.Length == 0) return _all;
        return _all.Where(d => d.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => string.Equals(d.Code, q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private const string ControlPage = "Merit Function Control Operands";
    private const string MathPage = "General Math Operands";

    /// <summary>OP-54's starter set (see the class summary).</summary>
    public static MeritOperandRegistry CreateDefault()
    {
        var r = new MeritOperandRegistry();

        // ---- Merit function control operands (help: Merit Function Control Operands). ----
        r.Register(Control("BLNK", "Blank", MeritControl.Blank,
            "Does nothing. Used for separating portions of the operand list; the comment is shown in the editor."));
        r.Register(Control("DMFS", "Default merit function start", MeritControl.DefaultStart,
            "Marks where a default merit function is appended: the row after it is the Optimization Wizard's default start row. Does nothing in the evaluation."));
        r.Register(Control("ENDX", "End", MeritControl.End,
            "Ends the computation of the merit function; all remaining operands are ignored."));
        r.Register(Control("GOTO", "Go to row", MeritControl.GoTo,
            "Skips the operands between this row and row Op#; evaluation continues at row Op#.", int1: "Op#"));
        r.Register(Control("OOFF", "Unused", MeritControl.Off,
            "An unused entry: an operand type that was not recognised. Evaluated as BLNK."));
        r.Register(Control("SKIN", "Skip if not symmetric", MeritControl.SkipIfNotSymmetric,
            "When the lens is not rotationally symmetric, evaluation continues at row Op#.", int1: "Op#"));
        r.Register(Control("SKIS", "Skip if symmetric", MeritControl.SkipIfSymmetric,
            "When the lens is rotationally symmetric, evaluation continues at row Op#.", int1: "Op#"));
        r.Register(Control("USYM", "Assume symmetry", MeritControl.AssumeSymmetry,
            "Present anywhere in the merit function, the lens is taken as rotationally symmetric even when it is not."));
        // CONF (help: Changing System Data; Multi-Configuration (Zoom) Data).
        r.Register(new MeritOperandDefinition
        {
            Code = "CONF", Name = "Configuration", Category = "Changing system data", Source = "Changing System Data",
            Description = "Changes the configuration to Cfg# for the rows after it. Does not use the target or weight.",
            Int1 = "Cfg#", UsesTargetAndWeight = false, Control = MeritControl.Configuration,
        });

        // ---- First-order and lens properties. ----
        r.Register(new MeritOperandDefinition
        {
            Code = "EFFL", Name = "Effective focal length", Category = "First-order optical properties", Source = "First-Order Optical Properties",
            Description = "Effective focal length in lens units at wavelength Wave; paraxial.",
            Int2 = "Wave",
            Compute = c =>
            {
                var p = c.Paraxial(c.Wavelength(c.Row.Int2));
                return p.EffectiveFocalLength is { } f ? c.Length(f)
                    : throw new MeritOperandException("the system is afocal; it has no effective focal length.");
            },
        });
        r.Register(new MeritOperandDefinition
        {
            Code = "TOTR", Name = "Total track", Category = "Constraints on lens properties", Source = "Constraints on Lens Properties; Total Track",
            Description = "Length of the lens in lens units: the greatest minus the least vertex z from surface 1 to the image surface, by adding thicknesses and ignoring coordinate rotations.",
            Compute = c => c.Length(TotalTrack(c.Lens)),
        });

        // ---- Lens data constraints (help: Constraints on Lens Data). ----
        r.Register(SurfaceValue("CTVA", "Center thickness value", "Center thickness of surface Surf.", c => Thickness(c, c.Row.Int1)));
        r.Register(SurfaceBound("CTGT", "Center thickness greater than", "Center thickness of surface Surf, bounded below by the target.", atLeast: true, c => Thickness(c, c.Row.Int1)));
        r.Register(SurfaceBound("CTLT", "Center thickness less than", "Center thickness of surface Surf, bounded above by the target.", atLeast: false, c => Thickness(c, c.Row.Int1)));
        r.Register(SurfaceValue("CVVA", "Curvature value", "Curvature of surface Surf in 1/lens unit.", c => c.Curvature(c.Lens.Surfaces[c.Surface(c.Row.Int1)].Curvature)));
        r.Register(SurfaceBound("CVGT", "Curvature greater than", "Curvature of surface Surf, bounded below by the target.", atLeast: true, c => c.Curvature(c.Lens.Surfaces[c.Surface(c.Row.Int1)].Curvature)));
        r.Register(SurfaceBound("CVLT", "Curvature less than", "Curvature of surface Surf, bounded above by the target.", atLeast: false, c => c.Curvature(c.Lens.Surfaces[c.Surface(c.Row.Int1)].Curvature)));
        r.Register(SurfaceValue("COVA", "Conic value", "Conic constant of surface Surf.", c => c.Lens.Surfaces[c.Surface(c.Row.Int1)].Conic));
        r.Register(new MeritOperandDefinition
        {
            Code = "TTHI", Name = "Thickness sum", Category = "Constraints on lens data", Source = "Constraints on Lens Data",
            Description = "Sum of the thicknesses of surfaces Surf1 to Surf2, both included.",
            Int1 = "Surf1", Int2 = "Surf2",
            Compute = c =>
            {
                var (a, b) = SurfaceRange(c);
                double sum = 0;
                for (int j = a; j <= b; j++) sum += Thickness(c, j);
                return sum;
            },
        });
        r.Register(RangeBound("MNCT", "Minimum center thickness", atLeast: true,
            "Each center thickness of surfaces Surf1 to Surf2 bounded below by the target; the value is the target less the sum of the violations."));
        r.Register(RangeBound("MXCT", "Maximum center thickness", atLeast: false,
            "Each center thickness of surfaces Surf1 to Surf2 bounded above by the target; the value is the target plus the sum of the violations."));

        // ---- Real ray data (help: Constraints on Real Ray Data). ----
        r.Register(RealRay("REAX", "Real ray x", "Local x of the real ray at surface Surf, lens units.", h => h.Position.X));
        r.Register(RealRay("REAY", "Real ray y", "Local y of the real ray at surface Surf, lens units.", h => h.Position.Y));
        r.Register(RealRay("REAZ", "Real ray z", "Local z of the real ray at surface Surf, lens units.", h => h.Position.Z));
        r.Register(RealRay("REAR", "Real ray radial coordinate", "Radial coordinate √(x² + y²) of the real ray at surface Surf, lens units.",
            h => Math.Sqrt(h.Position.X * h.Position.X + h.Position.Y * h.Position.Y)));

        // ---- General math operands (help: General Math Operands). EQUA is not built: the help's
        // description gives a value that is zero when the rows agree but does not say how that
        // value relates to the target, so its residual cannot be read off the page. ----
        r.Register(Unary("ABSO", "Absolute value", "Absolute value of row Op#.", Math.Abs));
        r.Register(Trig("ACOS", "Arc cosine", "Arc cosine of row Op#; radians, or degrees when Flag is not 0.", inverse: true, x => Domain(x, Math.Acos, "arc cosine")));
        r.Register(Trig("ASIN", "Arc sine", "Arc sine of row Op#; radians, or degrees when Flag is not 0.", inverse: true, x => Domain(x, Math.Asin, "arc sine")));
        r.Register(Trig("ATAN", "Arc tangent", "Arc tangent of row Op#; radians, or degrees when Flag is not 0.", inverse: true, Math.Atan));
        r.Register(new MeritOperandDefinition
        {
            Code = "CONS", Name = "Constant", Category = "General math operands", Source = MathPage,
            Description = "A constant for other rows to use: the value is the target.",
            Compute = c => c.Row.Target,
        });
        r.Register(Trig("COSI", "Cosine", "Cosine of row Op#; radians, or degrees when Flag is not 0.", inverse: false, Math.Cos));
        r.Register(Binary("DIFF", "Difference", "Row Op#1 minus row Op#2.", (a, b) => a - b));
        r.Register(Factor("DIVB", "Divide by factor", "Row Op# divided by Factor.", (x, f) =>
            f == 0 ? throw new MeritOperandException("Factor is zero; cannot divide by it.") : x / f));
        r.Register(Binary("DIVI", "Quotient", "Row Op#1 divided by row Op#2.", (a, b) =>
            b == 0 ? throw new MeritOperandException("the divisor row's value is zero.") : a / b));
        r.Register(Unary("LOGE", "Natural logarithm", "Natural logarithm of row Op#; zero when the value is zero or less (as the help says).", x => x <= 0 ? 0 : Math.Log(x)));
        r.Register(Unary("LOGT", "Base-10 logarithm", "Base-10 logarithm of row Op#; zero when the value is zero or less (as the help says).", x => x <= 0 ? 0 : Math.Log10(x)));
        r.Register(Range("MAXX", "Largest of rows", "The largest value of rows Op#1 to Op#2.", v => v.Max()));
        r.Register(Range("MINN", "Smallest of rows", "The smallest value of rows Op#1 to Op#2.", v => v.Min()));
        r.Register(Unary("OPGT", "Row greater than", "Row Op#, bounded below by the target.", x => x, bound: true, atLeast: true));
        r.Register(Unary("OPLT", "Row less than", "Row Op#, bounded above by the target.", x => x, bound: true, atLeast: false));
        r.Register(Unary("OPVA", "Row value", "The value of row Op#, held to the target.", x => x));
        r.Register(Range("OSUM", "Sum of rows", "Sum of rows Op#1 to Op#2.", Sum));
        r.Register(Factor("PROB", "Multiply by factor", "Row Op# times Factor.", (x, f) => x * f));
        r.Register(Binary("PROD", "Product", "Row Op#1 times row Op#2.", (a, b) => a * b));
        r.Register(Range("QSUM", "Root sum of squares", "Square root of the sum of the squares of rows Op#1 to Op#2.", v => Math.Sqrt(Sum(v.Select(x => x * x)))));
        r.Register(Unary("RECI", "Reciprocal", "Reciprocal of row Op#.", x =>
            x == 0 ? throw new MeritOperandException("the row's value is zero; it has no reciprocal.") : 1 / x));
        r.Register(Unary("SQRT", "Square root", "Square root of row Op#.", x =>
            x < 0 ? throw new MeritOperandException($"the row's value {F(x)} is negative; it has no real square root.") : Math.Sqrt(x)));
        r.Register(Binary("SUMM", "Sum", "Row Op#1 plus row Op#2.", (a, b) => a + b));
        r.Register(Trig("SINE", "Sine", "Sine of row Op#; radians, or degrees when Flag is not 0.", inverse: false, Math.Sin));
        r.Register(Trig("TANG", "Tangent", "Tangent of row Op#; radians, or degrees when Flag is not 0.", inverse: false, Math.Tan));
        r.Register(Unary("ABGT", "Absolute value greater than", "Absolute value of row Op#, bounded below by the target.", Math.Abs, bound: true, atLeast: true));
        r.Register(Unary("ABLT", "Absolute value less than", "Absolute value of row Op#, bounded above by the target.", Math.Abs, bound: true, atLeast: false));

        // OP-56: the ray-aberration operands the Optimization Wizard writes (TRAR, TRAX, TRAY,
        // ANAR, ANAX, ANAY, OPDC; the centroid groups TRAC, TRCX, TRCY, ANAC, ANCX, ANCY, OPDM,
        // OPDX) and its thickness boundaries (MNCG, MXCG, MNEG, MNCA, MXCA, MNEA); see
        // RayAberrationOperands.
        foreach (var definition in RayAberrationOperands.Definitions()) r.Register(definition);

        // OP-39 into OP-56 (wave 7): the MTF and Moore–Elliott contrast operands (help: MTF Data; MtfOperands).
        foreach (var definition in MtfOperands.Definitions()) r.Register(definition);

        // OP-59: glass operands (help: Constraints on Glass Data; GlassOperands).
        r.Register(GlassOperands.ReasonableGlass());
        r.Register(GlassOperands.GlassCost());
        return r;
    }

    // ------------------------------------------------------------------ bounds

    /// <summary>A lower bound's value (help: Understanding Boundary Operands): the target while
    /// the bound holds, so the row contributes nothing; the actual value when it is violated.</summary>
    public static double AtLeast(double actual, double target) => actual >= target ? target : actual;

    /// <summary>An upper bound's value: the target while the bound holds, else the actual value.</summary>
    public static double AtMost(double actual, double target) => actual <= target ? target : actual;

    // ------------------------------------------------------------------ builders

    private static MeritOperandDefinition Control(string code, string name, MeritControl control, string description, string? int1 = null) => new()
    {
        Code = code, Name = name, Category = "Merit function control operands", Source = ControlPage,
        Description = description, Int1 = int1, UsesTargetAndWeight = false, Control = control,
    };

    private static MeritOperandDefinition SurfaceValue(string code, string name, string description, Func<MeritRowContext, double> compute) => new()
    {
        Code = code, Name = name, Category = "Constraints on lens data", Source = "Constraints on Lens Data",
        Description = description, Int1 = "Surf", Compute = compute,
    };

    private static MeritOperandDefinition SurfaceBound(string code, string name, string description, bool atLeast,
        Func<MeritRowContext, double> actual) => new()
    {
        Code = code, Name = name, Category = "Constraints on lens data", Source = "Constraints on Lens Data; Understanding Boundary Operands",
        Description = description, Int1 = "Surf", IsBound = true,
        Compute = c => atLeast ? AtLeast(actual(c), c.Row.Target) : AtMost(actual(c), c.Row.Target),
    };

    /// <summary>MNCT and MXCT: "the difference between the value of the operand and the target is
    /// the sum of the difference between [the target] and the thicknesses of all surfaces ...
    /// whose center thickness is less than [it]" (help: Understanding Boundary Operands, its
    /// MNCT example). MXCT is read the same way with the sign turned.</summary>
    private static MeritOperandDefinition RangeBound(string code, string name, bool atLeast, string description) => new()
    {
        Code = code, Name = name, Category = "Constraints on lens data", Source = "Constraints on Lens Data; Understanding Boundary Operands",
        Description = description, Int1 = "Surf1", Int2 = "Surf2", IsBound = true,
        Compute = c =>
        {
            var (a, b) = SurfaceRange(c);
            double t = c.Row.Target, violation = 0;
            for (int j = a; j <= b; j++)
            {
                double thickness = c.Length(c.Lens.Surfaces[j].Thickness);
                if (atLeast && thickness < t) violation += t - thickness;
                else if (!atLeast && thickness > t) violation += thickness - t;
            }
            if (double.IsInfinity(violation))
                throw new MeritOperandException($"a surface between {a} and {b} has an infinite thickness.");
            return atLeast ? t - violation : t + violation;
        },
    };

    private static MeritOperandDefinition RealRay(string code, string name, string description, Func<SurfaceHit, double> coordinate) => new()
    {
        Code = code, Name = name, Category = "Constraints on real ray data", Source = "Constraints on Real Ray Data",
        Description = description + " The ray is at wavelength Wave, field (Hx, Hy) and pupil (Px, Py).",
        Int1 = "Surf", Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py" },
        Ray = RayOf,
        Compute = c =>
        {
            int surface = c.Surface(c.Row.Int1);
            c.Wavelength(c.Row.Int2);
            var path = c.Ray(RayOf(c.Row));
            if (!path.Final.IsOk && path.FailedAtSurface <= surface)
                throw new MeritOperandException(
                    $"the ray (Hx {F(c.Row.DataAt(1))}, Hy {F(c.Row.DataAt(2))}, Px {F(c.Row.DataAt(3))}, Py {F(c.Row.DataAt(4))}, wavelength {c.Row.Int2}) failed: {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}.");
            var hit = path.Hits.FirstOrDefault(h => h.Surface == surface)
                ?? throw new MeritOperandException($"the ray has no point on surface {surface}.");
            return c.Length(coordinate(hit));
        },
    };

    private static MeritRayRequest RayOf(MeritOperandRow row) => new(row.Int2, row.DataAt(1), row.DataAt(2), row.DataAt(3), row.DataAt(4));

    private static MeritOperandDefinition Unary(string code, string name, string description, Func<double, double> f,
        bool bound = false, bool atLeast = false) => new()
    {
        Code = code, Name = name, Category = "General math operands", Source = MathPage,
        Description = description, Int1 = "Op#", IsBound = bound,
        References = row => new[] { row.Int1 },
        Compute = c =>
        {
            double v = f(c.ValueOf(c.Row.Int1));
            return !bound ? v : atLeast ? AtLeast(v, c.Row.Target) : AtMost(v, c.Row.Target);
        },
    };

    private static MeritOperandDefinition Trig(string code, string name, string description, bool inverse, Func<double, double> f) => new()
    {
        Code = code, Name = name, Category = "General math operands", Source = MathPage,
        Description = description, Int1 = "Op#", Int2 = "Flag",
        References = row => new[] { row.Int1 },
        Compute = c =>
        {
            bool degrees = c.Row.Int2 != 0;
            double x = c.ValueOf(c.Row.Int1);
            if (inverse) { double a = f(x); return degrees ? a * (180 / Math.PI) : a; }
            return f(degrees ? x * (Math.PI / 180) : x);
        },
    };

    private static MeritOperandDefinition Binary(string code, string name, string description, Func<double, double, double> f) => new()
    {
        Code = code, Name = name, Category = "General math operands", Source = MathPage,
        Description = description, Int1 = "Op#1", Int2 = "Op#2",
        References = row => new[] { row.Int1, row.Int2 },
        Compute = c => f(c.ValueOf(c.Row.Int1), c.ValueOf(c.Row.Int2)),
    };

    private static MeritOperandDefinition Factor(string code, string name, string description, Func<double, double, double> f) => new()
    {
        Code = code, Name = name, Category = "General math operands", Source = MathPage,
        Description = description, Int1 = "Op#", Data = new[] { "Factor" },
        References = row => new[] { row.Int1 },
        Compute = c => f(c.ValueOf(c.Row.Int1), c.Row.DataAt(1)),
    };

    /// <summary>A range of rows, Op#1 to Op#2, both included ("between the two operands defined
    /// by Op#1 and Op#2" read as inclusive, as TTHI's surface range is).</summary>
    private static MeritOperandDefinition Range(string code, string name, string description, Func<IReadOnlyList<double>, double> f) => new()
    {
        Code = code, Name = name, Category = "General math operands", Source = MathPage,
        Description = description, Int1 = "Op#1", Int2 = "Op#2",
        References = row => row.Int1 <= row.Int2 ? Enumerable.Range(row.Int1, row.Int2 - row.Int1 + 1) : new[] { row.Int1, row.Int2 },
        Compute = c =>
        {
            if (c.Row.Int1 > c.Row.Int2)
                throw new MeritOperandException($"Op#1 ({c.Row.Int1}) is after Op#2 ({c.Row.Int2}).");
            var values = new double[c.Row.Int2 - c.Row.Int1 + 1];
            for (int k = 0; k < values.Length; k++) values[k] = c.ValueOf(c.Row.Int1 + k);
            return f(values);
        },
    };

    // ------------------------------------------------------------------ helpers

    private static double Sum(IEnumerable<double> values)
    {
        double s = 0;
        foreach (double v in values) s += v;
        return s;
    }

    private static double Domain(double x, Func<double, double> f, string what) =>
        x is >= -1 and <= 1 ? f(x) : throw new MeritOperandException($"the row's value {F(x)} is outside −1 to 1, so it has no {what}.");

    private static double Thickness(MeritRowContext c, int surface)
    {
        double t = c.Lens.Surfaces[c.Surface(surface)].Thickness;
        return double.IsFinite(t) ? c.Length(t)
            : throw new MeritOperandException($"surface {surface} has an infinite thickness.");
    }

    private static (int A, int B) SurfaceRange(MeritRowContext c)
    {
        int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
        if (a > b) throw new MeritOperandException($"Surf1 ({a}) is after Surf2 ({b}).");
        return (a, b);
    }

    /// <summary>Total track, meters: vertex z from surface 1 (z = 0) by adding each thickness up
    /// to the image surface, then the greatest minus the least (help: Total Track).</summary>
    public static double TotalTrack(SequentialLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        double z = 0, min = 0, max = 0;
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            double t = lens.Surfaces[j].Thickness;
            if (!double.IsFinite(t)) throw new MeritOperandException($"surface {j} has an infinite thickness.");
            z += t;
            min = Math.Min(min, z);
            max = Math.Max(max, z);
        }
        return max - min;
    }

    private static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
}
