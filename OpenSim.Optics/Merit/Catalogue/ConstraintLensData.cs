// OP-55B
namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// Constraints on lens data (help: Constraints on Lens Data, 2025 R2, saved under
/// plans/sources/optics/op-54-help/). OP-54 and OP-56 registered COVA, CTGT, CTLT, CTVA, CVGT,
/// CVLT, CVVA, TTHI, MNCT, MXCT, MNCA, MNCG, MNEA, MNEG, MXCA and MXCG; this file adds the rest the
/// engines support. DCRV, DPHS, DSAG, DSLP and QSLP are in <see cref="NotBuiltOperands"/>.
/// </summary>
public static partial class ConstraintOperands
{
    /// <summary>Points from the vertex to the edge where FTGT/FTLT and BLTH sample the sags
    /// ("computed at 200 points between the vertex and edge", help).</summary>
    public const int RadialPoints = 200;

    /// <summary>Points round the perimeter where XNET and its family sample the edge thickness.
    /// The help says "numerous points"; 64 equally spaced from +x is this program's choice
    /// (§10 item 60).</summary>
    public const int PerimeterPoints = 64;

    internal static MeritOperandDefinition Make(string code, string name, string category, string source, string description,
        Func<MeritRowContext, double> compute, string? int1 = null, string? int2 = null, string?[]? data = null, bool bound = false) => new()
    {
        Code = code, Name = name, Category = category, Source = source, Description = description,
        Int1 = int1, Int2 = int2, Data = data ?? Array.Empty<string?>(), IsBound = bound, Compute = Guard(compute),
    };

    private static IEnumerable<MeritOperandDefinition> LensData()
    {
        const string page = LensDataPage + "; Understanding Boundary Operands";

        // ---- Conic bounds ----
        foreach (var (code, name, kind) in new[] { ("COGT", "Conic greater than", Kind.AtLeast), ("COLT", "Conic less than", Kind.AtMost) })
            yield return Make(code, name, LensDataCategory, page, $"Conic constant of surface Surf{Bounded(kind)}.",
                c => Apply(kind, c.Lens.Surfaces[c.Surface(c.Row.Int1)].Conic, c.Row.Target), int1: "Surf", bound: true);

        // ---- Diameter: two times the semi-diameter the mode flag selects ----
        foreach (var (code, name, kind) in new[] { ("DMGT", "Diameter greater than", Kind.AtLeast), ("DMLT", "Diameter less than", Kind.AtMost), ("DMVA", "Diameter value", Kind.Value) })
            yield return Make(code, name, LensDataCategory, page,
                $"Diameter of surface Surf, two times its mechanical (Mode 0) or clear (Mode 1) semi-diameter{Bounded(kind)}.",
                c =>
                {
                    int j = c.Surface(c.Row.Int1);
                    return Apply(kind, c.Length(2 * Semi(c, j, Mechanical(c.Row.Int2, "Mode (Int2)"))), c.Row.Target);
                }, int1: "Surf", int2: "Mode", bound: kind != Kind.Value);

        // ---- Edge thickness along an axis ----
        foreach (var (code, name, kind) in new[] { ("ETGT", "Edge thickness greater than", Kind.AtLeast), ("ETLT", "Edge thickness less than", Kind.AtMost), ("ETVA", "Edge thickness value", Kind.Value) })
            yield return Make(code, name, LensDataCategory, page,
                $"Edge thickness of surface Surf: the axial distance to the next surface plus its sag minus this one's, at the mechanical (Mode 0) or clear (Mode 1) semi-diameter along +y (Code 0), +x (1), −y (2) or −x (3){Bounded(kind)}.",
                c =>
                {
                    int j = c.Surface(c.Row.Int1);
                    var (ux, uy) = AxisOf(Flag(c.Row.Int2, "Code (Int2)", 0, 3, "0 +y, 1 +x, 2 −y, 3 −x"));
                    double a = Semi(c, j, Mechanical(c.Row.DataAt(1), "Mode (Data1)"));
                    return Apply(kind, c.Length(LocalThickness(c, j, a * ux, a * uy)), c.Row.Target);
                }, int1: "Surf", int2: "Code", data: new[] { "Mode" }, bound: kind != Kind.Value);

        // ---- Full thickness at 200 points along +y ----
        foreach (var (code, name, atLeast) in new[] { ("FTGT", "Full thickness greater than", true), ("FTLT", "Full thickness less than", false) })
            yield return Make(code, name, LensDataCategory, page,
                $"Thickness from surface Surf to the next, sags included, at {RadialPoints} points from the vertex to the mechanical (Mode 0) or clear (Mode 1) semi-diameter along +y; the {(atLeast ? "least" : "greatest")} of them, bounded {(atLeast ? "below" : "above")} by the target.",
                c =>
                {
                    int j = c.Surface(c.Row.Int1);
                    double a = Semi(c, j, Mechanical(c.Row.Int2, "Mode (Int2)"));
                    double extreme = atLeast ? double.PositiveInfinity : double.NegativeInfinity;
                    for (int i = 0; i < RadialPoints; i++)
                    {
                        double t = LocalThickness(c, j, 0, a * i / (RadialPoints - 1));
                        extreme = atLeast ? Math.Min(extreme, t) : Math.Max(extreme, t);
                    }
                    return Apply(atLeast ? Kind.AtLeast : Kind.AtMost, c.Length(extreme), c.Row.Target);
                }, int1: "Surf", int2: "Mode", bound: true);

        // ---- Blank thickness ----
        yield return Make("BLTH", "Blank thickness", LensDataCategory, LensDataPage,
            $"Least thickness of a blank that holds the volume after surface Surf: the greatest z of the next surface minus the least z of this one, both sampled at {RadialPoints} radial points to the mechanical (Mode 0) or clear (Mode 1) semi-diameter along +y (Code 0), +x (1), −y (2), −x (3) or all four axes (4).",
            c =>
            {
                int j = c.Surface(c.Row.Int1);
                int code = Flag(c.Row.Int2, "Code (Int2)", 0, 4, "0 +y, 1 +x, 2 −y, 3 −x, 4 all four axes");
                double a = Semi(c, j, Mechanical(c.Row.DataAt(1), "Mode (Data1)"));
                double t = Axial(c, j);
                if (!(t > 0)) throw new MeritOperandException($"surface {j}'s thickness is {F(c.Length(t))}; a blank needs a positive thickness.");
                var front = c.Lens.Surfaces[j];
                var back = c.Lens.Surfaces[j + 1];
                double minFront = double.PositiveInfinity, maxBack = double.NegativeInfinity;
                foreach (int axis in code == 4 ? new[] { 0, 1, 2, 3 } : new[] { code })
                {
                    var (ux, uy) = AxisOf(axis);
                    for (int i = 0; i < RadialPoints; i++)
                    {
                        double r = a * i / (RadialPoints - 1);
                        minFront = Math.Min(minFront, Sag(front, j, r * ux, r * uy));
                        maxBack = Math.Max(maxBack, t + Sag(back, j + 1, r * ux, r * uy));
                    }
                }
                return c.Length(maxBack - minFront);
            }, int1: "Surf", int2: "Code", data: new[] { "Mode" });

        // ---- Curvature range bounds ----
        foreach (var (code, name, atLeast) in new[] { ("MNCV", "Minimum curvature", true), ("MXCV", "Maximum curvature", false) })
            yield return Make(code, name, LensDataCategory, page,
                $"Each curvature of surfaces Surf1 to Surf2 bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations (coordinate breaks skipped).",
                c =>
                {
                    var (a, b) = Range(c);
                    var q = new List<double>();
                    for (int j = a; j <= b; j++)
                        if (!c.Lens.Surfaces[j].IsCoordinateBreak) q.Add(c.Curvature(c.Lens.Surfaces[j].Curvature));
                    return RangeBound(atLeast, c.Row.Target, q);
                }, int1: "Surf1", int2: "Surf2", bound: true);

        // ---- Edge thickness range bounds along +y (MNEG/MNEA are OP-56's) ----
        yield return EdgeRange("MNET", "Minimum edge thickness", medium: 0, atLeast: true, perimeter: false);
        yield return EdgeRange("MXET", "Maximum edge thickness", medium: 0, atLeast: false, perimeter: false);
        yield return EdgeRange("MXEA", "Maximum edge thickness air", medium: -1, atLeast: false, perimeter: false);
        yield return EdgeRange("MXEG", "Maximum edge thickness glass", medium: 1, atLeast: false, perimeter: false);

        // ---- Edge thickness round the perimeter ----
        yield return EdgeRange("XNET", "Minimum edge thickness round the perimeter", medium: 0, atLeast: true, perimeter: true);
        yield return EdgeRange("XNEA", "Minimum edge thickness round the perimeter, air", medium: -1, atLeast: true, perimeter: true);
        yield return EdgeRange("XNEG", "Minimum edge thickness round the perimeter, glass", medium: 1, atLeast: true, perimeter: true);
        yield return EdgeRange("XXET", "Maximum edge thickness round the perimeter", medium: 0, atLeast: false, perimeter: true);
        yield return EdgeRange("XXEA", "Maximum edge thickness round the perimeter, air", medium: -1, atLeast: false, perimeter: true);
        yield return EdgeRange("XXEG", "Maximum edge thickness round the perimeter, glass", medium: 1, atLeast: false, perimeter: true);

        // ---- Semi-diameters ----
        yield return SemiRange("MNSD", "Minimum clear semi-diameter", mechanical: false, atLeast: true);
        yield return SemiRange("MXSD", "Maximum clear semi-diameter", mechanical: false, atLeast: false);
        yield return SemiRange("OMMI", "Minimum mechanical semi-diameter", mechanical: true, atLeast: true);
        yield return SemiRange("OMMX", "Maximum mechanical semi-diameter", mechanical: true, atLeast: false);
        yield return Make("OMSD", "Mechanical semi-diameter", LensDataCategory, LensDataPage,
            "Mechanical semi-diameter of surface Surf, lens units: as entered, else the clear semi-diameter plus the chip zone.",
            c => c.Length(MechanicalSemi(c, c.Surface(c.Row.Int1))), int1: "Surf");

        // ---- Thickness sums ----
        yield return Make("TGTH", "Glass thickness sum", LensDataCategory, LensDataPage,
            "Sum of the thicknesses of the surfaces Surf1 to Surf2 (both included) that are followed by glass.",
            c =>
            {
                var (a, b) = Range(c);
                double sum = 0;
                for (int j = a; j <= b && j < c.Lens.ImageIndex; j++)
                {
                    if (!IsGlass(c, j)) continue;
                    double t = c.Lens.Surfaces[j].Thickness;
                    if (!double.IsFinite(t)) throw new MeritOperandException($"surface {j} has an infinite thickness.");
                    sum += t;
                }
                return c.Length(sum);
            }, int1: "Surf1", int2: "Surf2");

        // ---- Total thickness ----
        foreach (var (code, name, kind) in new[] { ("TTGT", "Total thickness greater than", Kind.AtLeast), ("TTLT", "Total thickness less than", Kind.AtMost), ("TTVA", "Total thickness value", Kind.Value) })
            yield return Make(code, name, LensDataCategory, page,
                $"Total thickness of surface Surf and the next: the extent along the axis of their vertices and their points at their own clear semi-diameters along +y (Code 0), +x (1), −y (2) or −x (3), positive in a mirror space too{Bounded(kind)}.",
                c =>
                {
                    int j = c.Surface(c.Row.Int1);
                    var (ux, uy) = AxisOf(Flag(c.Row.Int2, "Code (Int2)", 0, 3, "0 +y, 1 +x, 2 −y, 3 −x"));
                    double t = Axial(c, j);
                    double a1 = Clear(c, j), a2 = Clear(c, j + 1);
                    double f0 = 0, f1 = Sag(c.Lens.Surfaces[j], j, a1 * ux, a1 * uy);
                    double b0 = t, b1 = t + Sag(c.Lens.Surfaces[j + 1], j + 1, a2 * ux, a2 * uy);
                    double total = t >= 0
                        ? Math.Max(b0, b1) - Math.Min(f0, f1)
                        : Math.Max(f0, f1) - Math.Min(b0, b1);
                    return Apply(kind, c.Length(total), c.Row.Target);
                }, int1: "Surf", int2: "Code", bound: kind != Kind.Value);

        // ---- Zoom thickness ----
        yield return Make("ZTHI", "Thickness sum spread over configurations", LensDataCategory, LensDataPage + "; Multi-Configuration (Zoom) Data",
            "The greatest minus the least, over every configuration, of the sum of the thicknesses of surfaces Surf1 to Surf2 (TTHI); the target while the spread is within it.",
            c =>
            {
                var (a, b) = Range(c);
                var configs = c.Configurations;
                int count = configs?.Count ?? 1;
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                for (int k = 1; k <= count; k++)
                {
                    var lens = configs?.LensFor(k) ?? c.Lens;
                    var unit = configs?.SystemFor(k).Units.LensUnit ?? c.Unit;
                    if (b > lens.ImageIndex) throw new MeritOperandException($"configuration {k} has surfaces 0 to {lens.ImageIndex}; Surf2 is {b}.");
                    double sum = 0;
                    for (int j = a; j <= b; j++)
                    {
                        double t = lens.Surfaces[j].Thickness;
                        if (!double.IsFinite(t)) throw new MeritOperandException($"surface {j} has an infinite thickness in configuration {k}.");
                        sum += t;
                    }
                    double v = OpticsConventions.FromMeters(sum, unit);
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
                double spread = max - min;
                return spread > c.Row.Target ? spread : c.Row.Target;
            }, int1: "Surf1", int2: "Surf2", bound: true);
    }

    /// <summary>MNET, MXET, MXEA, MXEG (the +y edge, OP-56's MNEG rule) and XNET, XNEA, XNEG,
    /// XXET, XXEA, XXEG (round the perimeter): over the surfaces Surf1 to Surf2 followed by any
    /// medium (0), glass (1) or air (−1), skipping coordinate breaks and ignored surfaces, each
    /// edge thickness (the least, or greatest, of the perimeter points) bounded by the target as
    /// MNCT is. Zone, if not zero, scales the radius; Mode 0 the mechanical, 1 the clear
    /// semi-diameter. Data1 and Data2 hold Zone and Mode, as OP-56's MNEG.</summary>
    private static MeritOperandDefinition EdgeRange(string code, string name, int medium, bool atLeast, bool perimeter)
    {
        string which = medium switch { 1 => " followed by glass", -1 => " followed by air", _ => "" };
        string where = perimeter
            ? $"at {PerimeterPoints} points round the perimeter (the {(atLeast ? "least" : "greatest")} per surface)"
            : "along +y";
        return Make(code, name, LensDataCategory, LensDataPage + "; Understanding Boundary Operands",
            $"Each edge thickness of surfaces Surf1 to Surf2{which}, {where} at the mechanical (Mode 0) or clear (Mode 1) semi-diameter scaled by Zone when not zero, bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations.",
            c =>
            {
                var (a, b) = Range(c);
                if (b >= c.Lens.ImageIndex) b = c.Lens.ImageIndex - 1;   // the image surface has no thickness
                double zone = c.Row.DataAt(1);
                if (!(zone >= 0 && double.IsFinite(zone))) throw new MeritOperandException($"Zone (Data1) is {F(zone)}; it must be 0 (the full semi-diameter) or a positive scale.");
                bool mechanical = Mechanical(c.Row.DataAt(2), "Mode (Data2)");
                var q = new List<double>();
                for (int j = Math.Max(a, 1); j <= b; j++)
                {
                    var s = c.Lens.Surfaces[j];
                    if (s.IsCoordinateBreak || s.IsIgnored) continue;
                    if (medium != 0 && IsGlass(c, j) != (medium > 0)) continue;
                    double r = Semi(c, j, mechanical) * (zone == 0 ? 1 : zone);
                    double edge;
                    if (!perimeter) edge = LocalThickness(c, j, 0, r);
                    else
                    {
                        edge = atLeast ? double.PositiveInfinity : double.NegativeInfinity;
                        for (int k = 0; k < PerimeterPoints; k++)
                        {
                            var (sin, cos) = Math.SinCos(2 * Math.PI * k / PerimeterPoints);
                            double e = LocalThickness(c, j, r * cos, r * sin);
                            edge = atLeast ? Math.Min(edge, e) : Math.Max(edge, e);
                        }
                    }
                    q.Add(c.Length(edge));
                }
                return RangeBound(atLeast, c.Row.Target, q);
            }, int1: "Surf1", int2: "Surf2", data: new[] { "Zone", "Mode" }, bound: true);
    }

    /// <summary>MNSD, MXSD (clear) and OMMI, OMMX (mechanical): each semi-diameter of surfaces
    /// Surf1 to Surf2 (coordinate breaks skipped) bounded by the target as MNCT is.</summary>
    private static MeritOperandDefinition SemiRange(string code, string name, bool mechanical, bool atLeast) =>
        Make(code, name, LensDataCategory, LensDataPage + "; Understanding Boundary Operands",
            $"Each {(mechanical ? "mechanical" : "clear")} semi-diameter of surfaces Surf1 to Surf2 bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations.",
            c =>
            {
                var (a, b) = Range(c);
                var q = new List<double>();
                for (int j = Math.Max(a, 1); j <= b; j++)
                    if (!c.Lens.Surfaces[j].IsCoordinateBreak) q.Add(c.Length(Semi(c, j, mechanical)));
                return RangeBound(atLeast, c.Row.Target, q);
            }, int1: "Surf1", int2: "Surf2", bound: true);
}
