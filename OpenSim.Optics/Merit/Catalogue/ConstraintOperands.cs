// OP-55B
using System.Globalization;
using System.Runtime.CompilerServices;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// OP-55B: the constraint and system-data half of the operand catalogue (docs/Optics-Conventions.md
/// §64): lens data, lens properties, parameter data, glass data, element positions, system data,
/// multi-configuration data, paraxial Gaussian beam data and TCE data. Each operand is a thin
/// adapter over an engine already in this program (the surface placement, the clear semi-diameter
/// sizing, <see cref="ElementVolume"/>, <see cref="SurfaceProbe"/>, the glass map, the paraxial
/// Gaussian beam, the multi-configuration table); a code whose engine is missing is listed in
/// <see cref="NotBuiltOperands"/> instead.
/// <para>
/// Every definition was read from OpticStudio's 2025 R2 help, saved under
/// <c>plans/sources/optics/op-54-help/</c> (the category pages Constraints on Lens Data,
/// Constraints on Lens Properties, Constraints on Parameter Data, Constraints on Element
/// Positions, Changing System Data, Multi-Configuration (Zoom) Data) and
/// <c>plans/sources/optics/op-55b-help/</c> (Constraints on Glass Data, Gaussian Beam Data,
/// Thermal Coefficient of Expansion Data, Paraxial Gaussian Beam). The help names each operand's
/// parameters but not which of Int1, Int2 and Data1 to Data6 holds them. This program's reading,
/// as OP-54's (§10 item 27): integers in Int1 then Int2 in the order the help names them, then
/// the rest in Data1 on in that order; Wave alone in Int2 as EFFL. Every placement is listed in
/// §10 item 60.
/// </para>
/// </summary>
public static partial class ConstraintOperands
{
    /// <summary>Every OP-55B operand, in the help's category order.</summary>
    public static IEnumerable<MeritOperandDefinition> Definitions() =>
        LensData()
            .Concat(LensProperties())
            .Concat(ParameterData())
            .Concat(GlassData())
            .Concat(ElementPositions())
            .Concat(SystemData())
            .Concat(MultiConfigurationData())
            .Concat(GaussianBeamData())
            .Concat(ThermalData());

    // ------------------------------------------------------------------ categories (help names)

    private const string LensDataCategory = "Constraints on lens data";
    private const string LensPropertiesCategory = "Constraints on lens properties";
    private const string ParameterCategory = "Constraints on parameter data";
    private const string GlassCategory = "Constraints on glass data";
    private const string PositionCategory = "Constraints on element positions";
    private const string SystemCategory = "Changing system data";
    private const string MultiConfigCategory = "Multi-configuration (zoom) data";
    private const string BeamCategory = "Gaussian beam data";
    private const string ThermalCategory = "Thermal Coefficient of Expansion data";

    private const string LensDataPage = "Constraints on Lens Data";
    private const string LensPropertiesPage = "Constraints on Lens Properties";

    /// <summary>How a value, greater-than or less-than operand turns its quantity into its value
    /// (help: Understanding Boundary Operands): a bound's value is the target while it holds.</summary>
    internal enum Kind { Value, AtLeast, AtMost }

    internal static double Apply(Kind kind, double actual, double target) => kind switch
    {
        Kind.AtLeast => MeritOperandRegistry.AtLeast(actual, target),
        Kind.AtMost => MeritOperandRegistry.AtMost(actual, target),
        _ => actual,
    };

    internal static string Bounded(Kind kind) => kind switch
    {
        Kind.AtLeast => ", bounded below by the target",
        Kind.AtMost => ", bounded above by the target",
        _ => "",
    };

    /// <summary>A range bound over surfaces (MNCT's pattern, help: Understanding Boundary Operands
    /// and OP-54's MNCT): the target less (lower bound) or plus (upper bound) the sum of the
    /// violations of every quantity in the range.</summary>
    internal static double RangeBound(bool atLeast, double target, IEnumerable<double> quantities)
    {
        double violation = 0;
        foreach (double q in quantities)
        {
            if (!double.IsFinite(q)) throw new MeritOperandException($"a quantity in the range is {F(q)}.");
            if (atLeast && q < target) violation += target - q;
            else if (!atLeast && q > target) violation += q - target;
        }
        return atLeast ? target - violation : target + violation;
    }

    // ------------------------------------------------------------------ shared reading of rows

    /// <summary>Runs <paramref name="compute"/>, turning the engines' argument and state
    /// exceptions into the row's failure (the evaluator lets only <see cref="MeritOperandException"/>
    /// fail a row).</summary>
    internal static Func<MeritRowContext, double> Guard(Func<MeritRowContext, double> compute) => c =>
    {
        try { return compute(c); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new MeritOperandException(FirstLine(e.Message));
        }
    };

    /// <summary>Surf1 and Surf2 checked and in order.</summary>
    internal static (int A, int B) Range(MeritRowContext c)
    {
        int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
        if (a > b) throw new MeritOperandException($"Surf1 ({a}) is after Surf2 ({b}).");
        return (a, b);
    }

    /// <summary>A whole-number flag read from a column (Mode, Code, Remove...), checked against
    /// its allowed values.</summary>
    internal static int Flag(double value, string label, int min, int max, string meaning)
    {
        if (value != Math.Floor(value) || value < min || value > max)
            throw new MeritOperandException($"{label} is {F(value)}; it must be a whole number {min} to {max} ({meaning}).");
        return (int)value;
    }

    /// <summary>The help's "mode flag": 0 (the default) the mechanical semi-diameter, 1 the clear one.</summary>
    internal static bool Mechanical(double mode, string label) =>
        Flag(mode, label, 0, 1, "0 the mechanical semi-diameter, 1 the clear semi-diameter") == 0;

    // ------------------------------------------------------------------ geometry

    /// <summary>The clear semi-diameters and placement the report analyses use, shared with
    /// the ray-aberration operands' cache (one sizing trace per lens and system).</summary>
    internal static LensGeometry Geometry(MeritRowContext c) => RayAberrationOperands.For(c.Lens, c.System).Geometry();

    private static readonly ConditionalWeakTable<SequentialLens, SurfacePlacement> Placements = new();

    /// <summary>The surface placement (OP-21), cached per lens instance.</summary>
    internal static SurfacePlacement Placement(MeritRowContext c) => Placements.GetValue(c.Lens, SurfacePlacement.Compute);

    /// <summary>The clear semi-diameter of surface j, meters; fails when it is automatic and
    /// could not be sized.</summary>
    internal static double Clear(MeritRowContext c, int j)
    {
        var g = Geometry(c);
        double a = g.SemiDiameter(j);
        if (!double.IsFinite(a))
            throw new MeritOperandException($"surface {j}'s clear semi-diameter could not be sized ({g.SizingError ?? "automatic"}).");
        return a;
    }

    /// <summary>The mechanical semi-diameter of surface j, meters: as entered, else the clear one
    /// plus the chip zone (the edge-thickness operands' rule, OP-56).</summary>
    internal static double MechanicalSemi(MeritRowContext c, int j) =>
        c.Lens.Surfaces[j].MechanicalSemiDiameter ?? Clear(c, j) + c.Lens.Surfaces[j].ChipZone;

    /// <summary>The semi-diameter the mode flag selects, meters.</summary>
    internal static double Semi(MeritRowContext c, int j, bool mechanical) => mechanical ? MechanicalSemi(c, j) : Clear(c, j);

    /// <summary>The sag of a face at (x, y), meters (composite add-ons included); fails where it
    /// is not defined.</summary>
    internal static double Sag(SequentialSurface face, int j, double x, double y)
    {
        double z = ElementVolume.Sag(face, x, y);
        if (!double.IsFinite(z))
            throw new MeritOperandException($"surface {j}'s sag is not defined at (x, y) = ({F(x)}, {F(y)}) m.");
        return z;
    }

    /// <summary>The axial distance from surface j's vertex to surface j + 1's, meters; fails
    /// when j + 1 is a coordinate break or is tilted or decentered against j.</summary>
    internal static double Axial(MeritRowContext c, int j)
    {
        if (j + 1 > c.Lens.ImageIndex) throw new MeritOperandException($"surface {j} is the image surface; there is no surface after it.");
        if (c.Lens.Surfaces[j].IsCoordinateBreak || c.Lens.Surfaces[j + 1].IsCoordinateBreak)
            throw new MeritOperandException($"surface {j} or {j + 1} is a coordinate break, which has no shape; set the range to skip it.");
        double t = c.Lens.Surfaces[j].Thickness;
        if (!double.IsFinite(t)) throw new MeritOperandException($"surface {j} has an infinite thickness.");
        return Coaxial(c, j, j + 1)
            ?? throw new MeritOperandException($"surface {j + 1} is tilted or decentered against surface {j}, so the thickness between them is not defined along the axis.");
    }

    /// <summary>The axial distance from surface j's vertex to surface k's when k's frame is j's
    /// moved along j's z only (to rounding, as <see cref="LensGeometry.CoaxialDistance"/>); null
    /// otherwise.</summary>
    internal static double? Coaxial(MeritRowContext c, int j, int k)
    {
        var t = Placement(c).RelativeTo(k, j);
        const double tol = 1e-12;
        bool rotationFree = (t.Col0 - OpenSim.Core.Numerics.Vector3D.UnitX).Length <= tol
            && (t.Col1 - OpenSim.Core.Numerics.Vector3D.UnitY).Length <= tol
            && (t.Col2 - OpenSim.Core.Numerics.Vector3D.UnitZ).Length <= tol;
        double lateral = Math.Sqrt(t.Translation.X * t.Translation.X + t.Translation.Y * t.Translation.Y);
        double size = Math.Max(Math.Abs(t.Translation.Z), 1e-9);
        return rotationFree && lateral <= tol * size ? t.Translation.Z : null;
    }

    /// <summary>The local thickness from surface j to j + 1 at (x, y) of j's vertex plane:
    /// axial distance plus the next sag minus this sag, meters.</summary>
    internal static double LocalThickness(MeritRowContext c, int j, double x, double y) =>
        Axial(c, j) + Sag(c.Lens.Surfaces[j + 1], j + 1, x, y) - Sag(c.Lens.Surfaces[j], j, x, y);

    /// <summary>The help's axis Code: 0 +y, 1 +x, 2 −y, 3 −x, as a unit direction (x, y).</summary>
    internal static (double X, double Y) AxisOf(int code) => code switch
    {
        0 => (0, 1),
        1 => (1, 0),
        2 => (0, -1),
        _ => (-1, 0),
    };

    /// <summary>True when the space after surface j holds glass (not the system's air); a
    /// mirror, a coordinate break and an ignored surface keep the medium before them.</summary>
    internal static bool IsGlass(MeritRowContext c, int j) => !ReportText.IsAir(ReportText.SpaceMedia(c.Lens)[j]);

    internal static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);

    internal static string FirstLine(string message)
    {
        int at = message.IndexOf('\n');
        return (at < 0 ? message : message[..at]).TrimEnd('\r', ' ');
    }
}
