using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Thermal;

/// <summary>
/// IPC-2152's external-trace heating as a published fit of its charts, and the copper
/// the charts' cross-sectional areas are defined with.
/// <para>
/// IPC-2152 ("Standard for Determining Current Carrying Capacity in Printed Board Design",
/// August 2009) is a paid document and is not on this machine; its charts are not
/// reproduced here. What is here was read from two freely published documents:
/// </para>
/// <list type="bullet">
/// <item>D. G. Brooks and J. Adam, "Trace Currents and Temperatures Revisited", UltraCAD
/// Design / ADAM Research, April 2015 (edit 7/20/15): the IPC-2152 external-trace charts
/// for 2 oz and 3 oz copper (Figures A-24 and 5-3 of the standard) were digitized along
/// constant-width lines and fitted by Eq. 3-2, ΔT = 215.3·I²·W⁻¹·¹⁵·Th⁻¹·⁰ (ΔT in °C, I in
/// A, W and Th in mils), "the fits are obviously extremely good" (section 3, Table 3-1,
/// Appendix 6). IPC-2152 has no separate 1 oz external chart (section 3), so for 1 oz the
/// equation is the authors' extrapolation. The charts run from 1 °C to 100 °C rise.</item>
/// <item>UltraCAD PCB Trace Calculator v4.0 operator's manual, which implements the same
/// equations: "For a 1 Oz. trace, the thickness is about 1.35 mils" (p. 3), and its worked
/// example, a 100 mil 1 oz external trace at 10 A rising 79.9 °C (p. 4); the paper's own
/// example, 100 mil 2 oz at 10 A "just under 40 °C" (Figure 6-1), needs the same 1.35 mil
/// per ounce. That convention fixes what the fit means by W and Th.</item>
/// </list>
/// <para>
/// The IPC data were measured on the coupon of IPC-TM-650 2.5.4.1A
/// (<see cref="IpcTm650Coupon"/>), a bare board with no planes suspended in still air; a
/// trace over a plane, in an enclosure, beside other heated traces or on a thinner board
/// runs at another temperature (IPC-2152 Appendix A; Brooks and Adam section 5).
/// </para>
/// </summary>
public static class Ipc2152
{
    /// <summary>One mil [m].</summary>
    public const double Mil = 25.4e-6;

    /// <summary>Copper thickness per ounce the fit is expressed in: 1 oz = 1.35 mil
    /// (UltraCAD manual p. 3; confirmed by both documents' worked examples).</summary>
    public const double MilPerOunce = 1.35;

    /// <summary>The copper thickness [m] the fit assigns to a copper weight.</summary>
    public static double OunceThickness(double ounces) => ounces * MilPerOunce * Mil;

    /// <summary>Brooks and Adam Eq. 3-2: the constant and the three exponents.</summary>
    public const double ExternalConstant = 215.3;
    public const double ExternalCurrentExponent = 2.0;
    public const double ExternalWidthExponent = -1.15;
    public const double ExternalThicknessExponent = -1.0;

    /// <summary>The rise of the charts [°C] range the fit was made over.</summary>
    public const double ChartMinimumRise = 1.0, ChartMaximumRise = 100.0;

    public const string ExternalSource =
        "Brooks & Adam, \"Trace Currents and Temperatures Revisited\" (2015), Eq. 3-2: a fit of the IPC-2152 " +
        "external-trace charts (2 oz and 3 oz, still air, bare 0.070 in polyimide coupon of IPC-TM-650 2.5.4.1A), " +
        "with 1 oz = 1.35 mil (UltraCAD PCB Trace Calculator v4.0 manual)";

    /// <summary>
    /// The temperature rise [K] of an external trace in still air on the IPC coupon, by the
    /// fit of the IPC-2152 charts. Outside the charts' 1–100 °C band it is an extrapolation.
    /// </summary>
    public static double ExternalRise(double amps, double widthMeters, double thicknessMeters)
    {
        if (amps < 0) throw new ArgumentOutOfRangeException(nameof(amps));
        if (widthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(widthMeters));
        if (thicknessMeters <= 0) throw new ArgumentOutOfRangeException(nameof(thicknessMeters));
        return ExternalConstant * Math.Pow(amps, ExternalCurrentExponent)
               * Math.Pow(widthMeters / Mil, ExternalWidthExponent)
               * Math.Pow(thicknessMeters / Mil, ExternalThicknessExponent);
    }

    /// <summary>The current [A] that gives a rise, the inverse of <see cref="ExternalRise"/>.</summary>
    public static double ExternalCurrent(double riseKelvin, double widthMeters, double thicknessMeters)
    {
        if (riseKelvin < 0) throw new ArgumentOutOfRangeException(nameof(riseKelvin));
        return Math.Pow(riseKelvin / ExternalRise(1.0, widthMeters, thicknessMeters), 1 / ExternalCurrentExponent);
    }

    /// <summary>
    /// The volume resistivity [Ω·m] IPC-TM-650 2.5.4.1A (5.3.3) defines the conductor's
    /// cross-sectional area with — A = ρ·L/R from the measured resistance: 10.827 Ω·cmil/ft
    /// at 25 °C for 1 oz and heavier, 11.529 for ½ oz. A chart's "area" is therefore a
    /// resistance per length in disguise, and a model that uses this ρ with the chart's area
    /// dissipates what the coupon did whatever the real copper was.
    /// </summary>
    public static double MethodResistivity(double ounces) =>
        (ounces < 1 ? 11.529 : 10.827) * OhmCircularMilPerFoot;

    /// <summary>A built-in library material by name — the shipped entry, not a user's
    /// override of it, so these references do not move with a user's materials file.</summary>
    internal static Material BuiltIn(string name) =>
        new MaterialLibrary().Materials.FirstOrDefault(m => m.Name == name && m.IsBuiltIn)
        ?? throw new InvalidOperationException($"The material library has no built-in '{name}'.");

    /// <summary>1 Ω·cmil/ft in Ω·m (a circular mil is π/4 square mil).</summary>
    public const double OhmCircularMilPerFoot = Math.PI / 4 * Mil * Mil / 0.3048;

    /// <summary>The method's reference temperature [K]: the ambient of 25 ± 5 °C (5.1.4).</summary>
    public const double MethodReferenceTemperature = 298.15;

    /// <summary>
    /// Copper as the method defines it: σ = 1/ρ(25 °C) of <see cref="MethodResistivity"/>,
    /// with the library's temperature coefficient (0.00393 /K, re-referenced to 25 °C) and
    /// its thermal properties; emissivity that of bare copper, since the method strips every
    /// coating (5.1.1).
    /// </summary>
    public static Material MethodCopper(double ounces)
    {
        var copper = BuiltIn("Copper (annealed)");
        double alpha20 = copper.ResistivityTemperatureCoefficient ?? 0.00393;
        // α referenced at 25 °C from α at 20 °C: 1/α25 = 1/α20 + 5 (the method's own 5.3.2 form).
        double alpha25 = 1 / (1 / alpha20 + (MethodReferenceTemperature - copper.ResistivityReferenceTemperature));
        return copper with
        {
            Name = $"Copper, IPC-TM-650 2.5.4.1A ({ounces:g2} oz)",
            ElectricalConductivity = 1 / MethodResistivity(ounces),
            ResistivityTemperatureCoefficient = alpha25,
            ResistivityReferenceTemperature = MethodReferenceTemperature
        };
    }
}

/// <summary>
/// The test pattern of IPC-TM-650 2.5.4.1A ("Conductor Temperature Rise Due to Current
/// Changes in Conductors", 8/97, Figure 1), on which the IPC-2152 data were taken: a
/// 365.7 × 142.2 mm [14.4 × 5.6 in] board carrying four straight conductors 0.63, 1.27,
/// 1.9 and 2.54 mm [25, 50, 75, 100 mil] wide, each 304.8 mm [12 in] between the centres
/// of two 5 mm terminal areas 30.5 mm from the board's ends, with 0.5 × 10 mm test tabs
/// 76.2 mm [3 in] in from each terminal, so the test section is the middle 152.4 mm
/// [6 in, "L = 0.5 ft", 5.3.3]. Only one conductor carries current at a time (3.0). The
/// board hangs by its four corners in an enclosure free of forced air, conductors
/// horizontal (5.2.1), ambient 25 ± 5 °C measured 101.6 mm from the conductor side (5.1.4).
/// The conductor's temperature is the mean over the test section, from its resistance:
/// Δt = (R_t/R_t1 − 1)/α (5.4).
/// <para>
/// Not in the method: the board's thickness and material. IPC-2152's baseline coupon is
/// 0.070 in thick polyimide (M. Jouppi, "The Value of IPC-2152", IPC; J. Olson,
/// "Determining Circuit Board Current Carrying Capability"), which is what
/// <see cref="BoardThickness"/> and <see cref="Polyimide"/> are.
/// </para>
/// <para>
/// The drawing's vertical dimensions (25.4 mm to the first conductor, three 30.5 mm
/// pitches, 30.5 mm to the far edge) add to 147.4 mm against the overall 142.2 mm it also
/// prints; the conductors here are placed from the far (bottom) edge by the 30.5 mm
/// dimension and the pitch, so the widest sits 30.5 mm from the bottom edge.
/// </para>
/// </summary>
public static class IpcTm650Coupon
{
    public const double BoardLength = 365.7e-3;
    public const double BoardWidth = 142.2e-3;

    /// <summary>The four conductor widths [m], narrowest first (25, 50, 75, 100 mil).</summary>
    public static readonly IReadOnlyList<double> ConductorWidths = new[] { 0.63e-3, 1.27e-3, 1.9e-3, 2.54e-3 };

    public const double TerminalDiameter = 5e-3;
    public const double TerminalInset = 30.5e-3;
    public const double ConductorLength = 304.8e-3;
    public const double TabInset = 76.2e-3;
    public const double TestSectionLength = 152.4e-3;
    public const double ConductorPitch = 30.5e-3;
    public const double LastConductorFromBottom = 30.5e-3;

    /// <summary>IPC-2152's baseline board: 0.070 in polyimide.</summary>
    public const double BoardThickness = 0.070 * 25.4e-3;

    /// <summary>x of the two terminal centres [m].</summary>
    public static double TerminalX0 => TerminalInset;
    public static double TerminalX1 => TerminalInset + ConductorLength;

    /// <summary>x of the two test tabs [m]; the test section lies between them.</summary>
    public static double TabX0 => TerminalX0 + TabInset;
    public static double TabX1 => TerminalX1 - TabInset;

    /// <summary>y of a conductor's centre line [m], index 0 the narrowest.</summary>
    public static double ConductorY(int index) =>
        LastConductorFromBottom + (ConductorWidths.Count - 1 - index) * ConductorPitch;

    /// <summary>The board outline.</summary>
    public static Polygon2 Outline => new(new[]
    {
        new Point2(0, 0), new Point2(BoardLength, 0), new Point2(BoardLength, BoardWidth), new Point2(0, BoardWidth)
    });

    /// <summary>
    /// A glass-reinforced polyimide laminate: thermal conductivity 0.4 W/m·K (Isola P95/P25
    /// and P96/P26 data sheets, "Typical Values", ASTM E1952), Young's modulus 3892 ksi and
    /// Poisson's ratio 0.187 (P95 sheet, length direction). IPC-2152 reports its own
    /// measured coupon properties, which are not available here. Density, specific heat and
    /// emissivity are the material library's FR4 values (a steady solve reads only k and ε;
    /// bare laminate is taken to radiate like FR4).
    /// </summary>
    public static Material Polyimide
    {
        get
        {
            var fr4 = Ipc2152.BuiltIn("FR4 (PCB laminate)");
            return fr4 with
            {
                Name = "Polyimide laminate (Isola P95/P96 typical)",
                ThermalConductivity = 0.4,
                YoungsModulus = 3892e3 * 6894.757,
                PoissonRatio = 0.187,
                IsBuiltIn = false
            };
        }
    }

    /// <summary>
    /// One conductor of the pattern as a net: the straight trace with a terminal disc at
    /// each end, the two discs tagged as the pads "P" (power) and "R" (return). The test
    /// tabs carry no current and are not drawn.
    /// </summary>
    /// <param name="index">Which conductor, 0 = 25 mil … 3 = 100 mil.</param>
    /// <param name="discSegments">Chords around each terminal disc.</param>
    public static (CopperNet Net, IReadOnlyList<CopperPad> Pads) Conductor(int index, int discSegments = 24)
    {
        if (index < 0 || index >= ConductorWidths.Count) throw new ArgumentOutOfRangeException(nameof(index));
        double w = ConductorWidths[index], hw = w / 2, r = TerminalDiameter / 2, y = ConductorY(index);
        double x0 = TerminalX0, x1 = TerminalX1;
        // The outline, counter-clockwise: round the left disc from its upper junction with
        // the trace to its lower one, along the bottom edge, round the right disc, back
        // along the top edge. θ0 is the half-angle the trace cuts out of each disc.
        double theta0 = Math.Asin(hw / r);
        var outer = new List<Point2>();
        Arc(x0, theta0, 2 * Math.PI - theta0);
        Arc(x1, Math.PI + theta0, 3 * Math.PI - theta0);
        var shape = new Polygon2(outer);
        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", shape) })
        {
            Name = $"IPC-TM-650 2.5.4.1A conductor {w * 1e3:g3} mm"
        };
        var pads = new[]
        {
            new CopperPad(1, new Point2(x0, y), Disc(x0), TerminalDiameter) { ComponentRef = "P", Pin = "1" },
            new CopperPad(1, new Point2(x1, y), Disc(x1), TerminalDiameter) { ComponentRef = "R", Pin = "1" }
        };
        return (net, pads);

        void Arc(double cx, double from, double to)
        {
            int n = Math.Max(4, (int)Math.Ceiling(discSegments * (to - from) / (2 * Math.PI)));
            for (int i = 0; i <= n; i++)
            {
                double a = from + (to - from) * i / n;
                outer.Add(new Point2(cx + r * Math.Cos(a), y + r * Math.Sin(a)));
            }
        }

        Polygon2 Disc(double cx) => new(Enumerable.Range(0, discSegments)
            .Select(i => 2 * Math.PI * i / discSegments)
            .Select(a => new Point2(cx + r * Math.Cos(a), y + r * Math.Sin(a))).ToList());
    }

    /// <summary>Whether a point of the conductor lies in the test section between the tabs.</summary>
    public static bool InTestSection(double x) => x >= TabX0 && x <= TabX1;
}
