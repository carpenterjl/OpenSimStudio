using System.Globalization;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Parameters 1–5, which every source type shares (help, "Parameters Common to All Source
/// Objects"): "# Layout Rays", "# Analysis Rays", "Power (units)" ("the total power over the
/// defined range of the source", in the system's source units), "Wavenumber" ("Zero means
/// polychromatic; which chooses ray wavelengths randomly with the weighting defined on the
/// wavelength data editor") and "Color #" (the pen colour, drawing only).
/// </summary>
public sealed record NscSourceCommon
{
    public int LayoutRays { get; init; } = 100;
    public int AnalysisRays { get; init; } = 1_000_000;
    public double Power { get; init; } = 1;
    public int Wavenumber { get; init; }
    public int Color { get; init; }

    /// <summary>Every problem, one line each, prefixed by <paramref name="where"/>.</summary>
    public void Validate(string where, List<string> problems)
    {
        if (LayoutRays < 0) problems.Add($"{where}: \"# Layout Rays\" must be zero or more; it is {LayoutRays}.");
        if (AnalysisRays < 0) problems.Add($"{where}: \"# Analysis Rays\" must be zero or more; it is {AnalysisRays}.");
        if (!(Power > 0) || !double.IsFinite(Power)) problems.Add($"{where}: the power must be positive and finite; it is {Power.ToString("g6", CultureInfo.InvariantCulture)}.");
        if (Wavenumber < 0) problems.Add($"{where}: the wavenumber must be 0 (polychromatic) or a wavelength row; it is {Wavenumber}.");
    }
}

/// <summary>The source's sampling method on the Sources tab ("random and Sobol"; help,
/// "Sources", "Random vs Sobol Sampling").</summary>
public enum NscSourceSampling
{
    Sobol = 0,
    Random = 1,
}

/// <summary>"Bulk Scatter" on the Sources tab (help, "Sources"): how often each branch of the
/// source's rays may undergo volume events (OP-81).</summary>
public enum NscSourceBulkScatter
{
    Many = 0,
    Once = 1,
    Never = 2,
}

/// <summary>"Array Type" on the Sources tab (help, "Sources"; ZPL SETNSCPROPERTY code 112:
/// "0 for none, 1 for rectangular, 2 for circular, 3 for hexapolar, and 4 for hexagonal").</summary>
public enum NscSourceArrayType
{
    None = 0,
    Rectangular = 1,
    Circular = 2,
    Hexapolar = 3,
    Hexagonal = 4,
}

/// <summary>
/// The source array of the Sources tab (help, "Sources", "Array"): identical copies of the source
/// in its local XY plane. "The parameter settings for the source, such as number of layout rays,
/// number of analysis rays, and power apply to each source in the array. For example, a 3 x 3
/// array of 1 watt sources will produce 9 watts." The integer arguments are SETNSCPROPERTY codes
/// 161–162 and the real ones 165–166.
/// <list type="bullet">
/// <item>Rectangular: <see cref="Count1"/> × <see cref="Count2"/> sources (1–2000 each) spaced
/// <see cref="Size1"/> and <see cref="Size2"/> (meters) in x and y; source 1 "at the location of
/// the parent object", numbered along x first.</item>
/// <item>Circular: <see cref="Count1"/> sources on a circle of radius <see cref="Size1"/>, the
/// first at 0° on the XY plane, then counter-clockwise.</item>
/// <item>Hexapolar: <see cref="Count1"/> rings (1–20) of radial spacing <see cref="Size1"/>; ring 1
/// is the parent's location, ring r holds 6(r − 1) sources starting at +90°.</item>
/// <item>Hexagonal: not built (refused by name).</item>
/// </list>
/// </summary>
public sealed record NscSourceArray
{
    public NscSourceArrayType Type { get; init; }
    public int Count1 { get; init; } = 1;
    public int Count2 { get; init; } = 1;
    public double Size1 { get; init; }
    public double Size2 { get; init; }

    /// <summary>The element offsets in the source's local XY plane, meters, in OpticStudio's
    /// numbering order. Throws <see cref="ArgumentException"/> naming <paramref name="where"/>.</summary>
    public IReadOnlyList<(double X, double Y)> Offsets(string where)
    {
        switch (Type)
        {
            case NscSourceArrayType.None:
                return new[] { (0.0, 0.0) };
            case NscSourceArrayType.Rectangular:
            {
                if (Count1 < 1 || Count1 > 2000 || Count2 < 1 || Count2 > 2000)
                    throw new ArgumentException($"{where}: a rectangular source array has 1 to 2000 sources in each direction (help, \"Sources\"); it has {Count1} × {Count2}.");
                Finite(Size1, Size2, where);
                var list = new List<(double, double)>(Count1 * Count2);
                for (int j = 0; j < Count2; j++)
                    for (int i = 0; i < Count1; i++)
                        list.Add((i * Size1, j * Size2));
                return list;
            }
            case NscSourceArrayType.Circular:
            {
                if (Count1 < 1 || Count1 > 2000)
                    throw new ArgumentException($"{where}: a circular source array has 1 to 2000 sources; it has {Count1}.");
                Finite(Size1, 0, where);
                var list = new List<(double, double)>(Count1);
                for (int i = 0; i < Count1; i++)
                {
                    double phi = 2 * Math.PI * i / Count1;
                    list.Add((Size1 * Math.Cos(phi), Size1 * Math.Sin(phi)));
                }
                return list;
            }
            case NscSourceArrayType.Hexapolar:
            {
                if (Count1 < 1 || Count1 > 20)
                    throw new ArgumentException($"{where}: a hexapolar source array has 1 to 20 rings (help, \"Sources\"); it has {Count1}.");
                Finite(Size1, 0, where);
                var list = new List<(double, double)> { (0.0, 0.0) };
                for (int r = 2; r <= Count1; r++)
                {
                    int n = 6 * (r - 1);
                    double radius = (r - 1) * Size1;
                    for (int i = 0; i < n; i++)
                    {
                        double phi = Math.PI / 2 + 2 * Math.PI * i / n;
                        list.Add((radius * Math.Cos(phi), radius * Math.Sin(phi)));
                    }
                }
                return list;
            }
            case NscSourceArrayType.Hexagonal:
                throw new NotSupportedException($"{where}: the hexagonal source array is not built (its column layout from \"the full height of the hexagonal region\" is not stated precisely enough in the help, \"Sources\"); use a rectangular, circular or hexapolar array.");
            default:
                throw new ArgumentException($"{where}: unknown source array type {(int)Type}.");
        }
    }

    private static void Finite(double a, double b, string where)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
            throw new ArgumentException($"{where}: the source array spacing must be finite.");
    }
}

/// <summary>
/// The source's polarization on the Sources tab (help, "Sources", "Polarization"): "Random
/// Polarization" (unpolarized, the default here), or the Jones vector "Jx, Jy: The magnitude of
/// the electric field in the local x and y directions" with "X-Phase, Y-Phase" in degrees.
/// The Jones vector is turned into the 3-D field by the "X Axis Reference" method, OpticStudio's
/// default (help, "Defining the Initial Polarization"): for a ray along K (local frame),
/// P = K × X̂ normalised, S = P × K, and E = Jx·e^{iφx}·S + Jy·e^{iφy}·P. For K = ẑ this is
/// Ex = Jx·e^{iφx}, Ey = Jy·e^{iφy}. A ray along ±x has no P and is refused.
/// </summary>
public sealed record NscSourcePolarization
{
    public bool Random { get; init; } = true;
    public double Jx { get; init; } = 1;
    public double Jy { get; init; }
    public double XPhaseDegrees { get; init; }
    public double YPhaseDegrees { get; init; }

    public void Validate(string where, List<string> problems)
    {
        if (Random) return;
        if (!double.IsFinite(Jx) || !double.IsFinite(Jy) || !double.IsFinite(XPhaseDegrees) || !double.IsFinite(YPhaseDegrees))
            problems.Add($"{where}: the Jones vector and its phases must be finite.");
        else if (Jx == 0 && Jy == 0)
            problems.Add($"{where}: the Jones vector (Jx, Jy) is zero; set Random Polarization or give a field.");
    }
}

/// <summary>
/// The Sources tab's properties (help, "Sources") that this build models or stores. What each
/// does here:
/// <list type="bullet">
/// <item><see cref="Polarization"/>, <see cref="Sampling"/>, <see cref="Array"/>,
/// <see cref="Spectrum"/>: modelled.</item>
/// <item><see cref="PrePropagation"/> (meters): "moves the starting point of rays forward or
/// backward along the ray direction cosines"; then <see cref="ReverseRays"/> "will reverse the
/// direction cosines of every ray ... done after the Pre-Propagation distance is considered".
/// The help adds that pre-propagation alters the initial phase and field; the field here is a
/// plane-wave polarization state that propagation in a uniform medium does not change, and the
/// phase is not traced.</item>
/// <item><see cref="InitialPhaseDegrees"/>, <see cref="CoherenceLength"/> (meters): stored only;
/// they act on coherent detector data, which the tracer does not carry yet (OP-79/OP-82).</item>
/// <item><see cref="BulkScatter"/>: Many, Once or Never volume events per branch (OP-81).</item>
/// </list>
/// </summary>
public sealed record NscSourceProperties
{
    public NscSourcePolarization Polarization { get; init; } = new();
    public double InitialPhaseDegrees { get; init; }
    public double CoherenceLength { get; init; }
    public bool ReverseRays { get; init; }
    public double PrePropagation { get; init; }
    public NscSourceBulkScatter BulkScatter { get; init; }
    public NscSourceSampling Sampling { get; init; } = NscSourceSampling.Sobol;
    public NscSourceArray Array { get; init; } = new();
    public NscSpectrum Spectrum { get; init; } = new();

    public static NscSourceProperties Default { get; } = new();

    public void Validate(string where, List<string> problems)
    {
        Polarization.Validate(where, problems);
        if (!double.IsFinite(PrePropagation)) problems.Add($"{where}: the pre-propagation distance must be finite.");
        if (!double.IsFinite(InitialPhaseDegrees)) problems.Add($"{where}: the initial phase must be finite.");
        if (!(CoherenceLength >= 0) || !double.IsFinite(CoherenceLength)) problems.Add($"{where}: the coherence length must be zero or positive.");
        if (!Enum.IsDefined(Sampling)) problems.Add($"{where}: unknown sampling method {(int)Sampling}.");
        if (!Enum.IsDefined(BulkScatter)) problems.Add($"{where}: unknown bulk scatter mode {(int)BulkScatter}.");
        Spectrum.Validate(where, problems);
    }
}
