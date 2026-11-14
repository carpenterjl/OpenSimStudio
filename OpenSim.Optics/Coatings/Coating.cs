using System.Globalization;
using System.Numerics;

namespace OpenSim.Optics.Coatings;

/// <summary>
/// A coating in the catalog. The kinds follow the sections of OpticStudio's coating file
/// (OpticStudio 2025 R2 help, "Coating File Data Syntax" and the pages it links; saved under
/// plans/sources/optics/coatingfile/): <see cref="LayerCoating"/> (COAT), <see cref="IdealCoating"/>
/// (IDEAL and the COAT I.xx shorthand), <see cref="Ideal2Coating"/> (IDEAL2) and
/// <see cref="TableCoating"/> (TABLE). An encrypted coating (ENCRYPTED) is not supported.
/// </summary>
public abstract record Coating
{
    public required string Name { get; init; }

    /// <summary>What is wrong with the coating, one line each, given the catalog's materials
    /// and tapers; empty when it can be used.</summary>
    public abstract IReadOnlyList<string> Validate(CoatingCatalog catalog);

    protected static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}

/// <summary>One line of a layer coating: the material, the thickness, whether it is absolute,
/// the loop index and the taper (OpticStudio help, "The COAT Data Section").
/// <para>A relative thickness T is the optical thickness in waves of the primary wavelength in
/// the material: the physical thickness is d = λ0 T / n0, λ0 the primary wavelength in µm and n0
/// the real part of the material's index there. An absolute thickness is in µm.</para></summary>
public sealed record CoatingLayer
{
    public required string Material { get; init; }
    public double Thickness { get; init; }
    public bool IsAbsolute { get; init; }

    /// <summary>The loop index: a group of lines is repeated this many times; it is written on
    /// both the first and the last line of the group, and 0 means the same as 1 (OpticStudio
    /// help, "Defining Replicated Groups of Coating Layers").</summary>
    public int LoopIndex { get; init; }

    /// <summary>The taper's name, or null for a uniform layer.</summary>
    public string? Taper { get; init; }
}

/// <summary>
/// A coating given layer by layer (COAT), listed from the incident side: the first line is the
/// outermost layer, the substrate is not listed (OpticStudio help, "Specifying Coatings on
/// Surfaces"). On a mirror the last layer is the substrate, taken as semi-infinite.
/// </summary>
public sealed record LayerCoating : Coating
{
    public IReadOnlyList<CoatingLayer> Layers { get; init; } = Array.Empty<CoatingLayer>();

    /// <summary>
    /// The layers with every looped group written out. A group starts at a line with a loop index
    /// above zero and ends at the next such line, which must carry the same index; the group is
    /// written out that many times. Groups cannot nest or overlap (the help says so); an
    /// unmatched or mismatched loop index is refused, naming the line.
    /// </summary>
    public IReadOnlyList<(CoatingLayer Layer, int Line)> Expand()
    {
        var expanded = new List<(CoatingLayer, int)>();
        int i = 0;
        while (i < Layers.Count)
        {
            var layer = Layers[i];
            if (layer.LoopIndex < 0)
                throw new InvalidOperationException($"Coating '{Name}', line {i + 1}: the loop index must not be negative (it is {layer.LoopIndex}).");
            if (layer.LoopIndex == 0)
            {
                expanded.Add((layer, i));
                i++;
                continue;
            }
            int end = i + 1;
            while (end < Layers.Count && Layers[end].LoopIndex == 0) end++;
            if (end >= Layers.Count)
                throw new InvalidOperationException(
                    $"Coating '{Name}', line {i + 1}: loop index {layer.LoopIndex} starts a group of layers that no later line closes. Write the same loop index on the group's last line.");
            if (Layers[end].LoopIndex != layer.LoopIndex)
                throw new InvalidOperationException(
                    $"Coating '{Name}': the group from line {i + 1} to line {end + 1} has loop index {layer.LoopIndex} on its first line and {Layers[end].LoopIndex} on its last; they must be equal.");
            for (int repeat = 0; repeat < layer.LoopIndex; repeat++)
                for (int k = i; k <= end; k++) expanded.Add((Layers[k], k));
            i = end + 1;
        }
        return expanded;
    }

    public override IReadOnlyList<string> Validate(CoatingCatalog catalog)
    {
        var problems = new List<string>();
        for (int i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            string where = $"Coating '{Name}', line {i + 1}";
            if (catalog.FindMaterial(layer.Material) is null)
                problems.Add($"{where}: material '{layer.Material}' is not defined in the catalog.");
            if (!double.IsFinite(layer.Thickness) || layer.Thickness < 0)
                problems.Add($"{where}: the thickness must be zero or positive (it is {F(layer.Thickness)}).");
            if (layer.Taper is { Length: > 0 } taper && catalog.FindTaper(taper) is null)
                problems.Add($"{where}: taper '{taper}' is not defined in the catalog.");
        }
        try { Expand(); }
        catch (InvalidOperationException e) { problems.Add(e.Message); }
        return problems;
    }
}

/// <summary>
/// An ideal coating given by intensities (IDEAL, OpticStudio help "The IDEAL Data Section"):
/// transmission T, reflection R and the intensity reflected under total internal reflection.
/// If T + R exceeds 1 both are scaled so that T + R = 1; A = 1 − R − T. The transmission phase is
/// zero; the reflection phase is π on a mirror, zero when the incident index exceeds the
/// substrate's, π otherwise. <see cref="IsSimple"/> marks the COAT I.xx shorthand (help,
/// "Defining Simple Ideal Coatings"): T as named, R = 1 − T, total internal reflection 1.
/// </summary>
public sealed record IdealCoating : Coating
{
    public double Transmission { get; init; }
    public double Reflection { get; init; }
    public double TotalInternalReflection { get; init; } = 1;
    public bool IsSimple { get; init; }

    /// <summary>T and R as used: scaled to sum to 1 when they exceed it.</summary>
    public (double T, double R) Effective
    {
        get
        {
            double sum = Transmission + Reflection;
            return sum > 1 ? (Transmission / sum, Reflection / sum) : (Transmission, Reflection);
        }
    }

    /// <summary>The I.xx shorthand: the name is "I." followed by the transmission's digits
    /// after the decimal point ("I.75" transmits 0.75).</summary>
    public static bool TryParseSimpleName(string name, out double transmission)
    {
        transmission = 0;
        if (name.Length < 3 || !name.StartsWith("I.", StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.Skip(2).All(char.IsAsciiDigit)) return false;
        transmission = double.Parse(name[1..], NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    public override IReadOnlyList<string> Validate(CoatingCatalog catalog)
    {
        var problems = new List<string>();
        if (!(Transmission >= 0) || !(Reflection >= 0) || !double.IsFinite(Transmission + Reflection))
            problems.Add($"Ideal coating '{Name}': T and R must be zero or positive (T = {F(Transmission)}, R = {F(Reflection)}).");
        if (!(TotalInternalReflection >= 0 && TotalInternalReflection <= 1))
            problems.Add($"Ideal coating '{Name}': the total-internal-reflection intensity must lie in 0–1 (it is {F(TotalInternalReflection)}).");
        if (IsSimple && !(Transmission > 0 && Transmission < 1))
            problems.Add($"Ideal coating '{Name}': an I.xx coating's transmission must lie strictly between 0 and 1 (it is {F(Transmission)}).");
        return problems;
    }
}

/// <summary>
/// An ideal coating given by complex amplitudes for s and p (IDEAL2, OpticStudio help "The IDEAL2
/// Data Section"), in OpticStudio's phase convention (index n − ik; this program reports phases
/// in that convention too, docs/Optics-Conventions.md section 12). When the substrate is not a
/// mirror and the incident index exceeds the substrate's, the reflection amplitudes change sign,
/// unless <see cref="NoPiFlag"/> is set. R = |r|², T = |t|², A = 1 − R − T.
/// </summary>
public sealed record Ideal2Coating : Coating
{
    public Complex SReflection { get; init; }
    public Complex STransmission { get; init; }
    public Complex PReflection { get; init; }
    public Complex PTransmission { get; init; }
    public bool NoPiFlag { get; init; }
    public double TotalInternalReflection { get; init; } = 1;

    public override IReadOnlyList<string> Validate(CoatingCatalog catalog)
    {
        var problems = new List<string>();
        foreach (var (label, r, t) in new[] { ("s", SReflection, STransmission), ("p", PReflection, PTransmission) })
        {
            double sum = r.Magnitude * r.Magnitude + t.Magnitude * t.Magnitude;
            if (!double.IsFinite(sum))
                problems.Add($"Ideal2 coating '{Name}': the {label} amplitudes must be finite.");
            else if (sum > 1 + 1e-12)
                problems.Add($"Ideal2 coating '{Name}': |r|² + |t|² for {label} light is {F(sum)}, above 1; the coating would create energy.");
        }
        if (!(TotalInternalReflection >= 0 && TotalInternalReflection <= 1))
            problems.Add($"Ideal2 coating '{Name}': the total-internal-reflection intensity must lie in 0–1 (it is {F(TotalInternalReflection)}).");
        return problems;
    }
}

/// <summary>One wavelength row of a table coating at one angle: intensities and phases (degrees).</summary>
public sealed record CoatingTableRow(double Micrometers, double Rs, double Rp, double Ts, double Tp,
    double PhaseRs = 0, double PhaseRp = 0, double PhaseTs = 0, double PhaseTp = 0);

/// <summary>The rows of a table coating at one angle of incidence (degrees).</summary>
public sealed record CoatingTableAngle(double AngleDegrees, IReadOnlyList<CoatingTableRow> Rows);

/// <summary>
/// A coating given as a table of intensities and phases against angle and wavelength (TABLE,
/// OpticStudio help "The TABLE Data Section"). Angles ascend and every angle lists the same
/// ascending wavelengths. Between angles the data interpolate linearly in cos θ, and outside the
/// angle range the nearest angle's data are used; between wavelengths they interpolate linearly
/// in wavelength. A wavelength outside the table is refused (the help does not say what
/// OpticStudio does there). Phases interpolate as given, without unwrapping. When the incident
/// index exceeds the substrate's, the angle used is the refracted angle in the substrate and
/// the reflection phase gains π.
/// </summary>
public sealed record TableCoating : Coating
{
    public IReadOnlyList<CoatingTableAngle> Angles { get; init; } = Array.Empty<CoatingTableAngle>();

    public override IReadOnlyList<string> Validate(CoatingCatalog catalog)
    {
        var problems = new List<string>();
        if (Angles.Count == 0)
        {
            problems.Add($"Table coating '{Name}' has no angles.");
            return problems;
        }
        var first = Angles[0].Rows.Select(r => r.Micrometers).ToArray();
        if (first.Length == 0) problems.Add($"Table coating '{Name}': the angle {F(Angles[0].AngleDegrees)}° has no wavelengths.");
        for (int i = 1; i < first.Length; i++)
            if (!(first[i] > first[i - 1]))
                problems.Add($"Table coating '{Name}': the wavelengths must ascend ({F(first[i - 1])} µm, then {F(first[i])} µm).");
        for (int a = 0; a < Angles.Count; a++)
        {
            var angle = Angles[a];
            if (!(angle.AngleDegrees >= 0 && angle.AngleDegrees < 90))
                problems.Add($"Table coating '{Name}': the angle {F(angle.AngleDegrees)}° is outside [0°, 90°).");
            if (a > 0 && !(angle.AngleDegrees > Angles[a - 1].AngleDegrees))
                problems.Add($"Table coating '{Name}': the angles must ascend ({F(Angles[a - 1].AngleDegrees)}°, then {F(angle.AngleDegrees)}°).");
            if (!angle.Rows.Select(r => r.Micrometers).SequenceEqual(first))
                problems.Add($"Table coating '{Name}': the angle {F(angle.AngleDegrees)}° does not list the same wavelengths as the first angle.");
            foreach (var row in angle.Rows)
                foreach (var (label, value) in new[] { ("Rs", row.Rs), ("Rp", row.Rp), ("Ts", row.Ts), ("Tp", row.Tp) })
                    if (!(value >= 0 && value <= 1))
                        problems.Add($"Table coating '{Name}': {label} at {F(angle.AngleDegrees)}°, {F(row.Micrometers)} µm is {F(value)}, outside 0–1.");
            foreach (var row in angle.Rows)
                if (row.Rs + row.Ts > 1 + 1e-12 || row.Rp + row.Tp > 1 + 1e-12)
                    problems.Add($"Table coating '{Name}': R + T exceeds 1 at {F(angle.AngleDegrees)}°, {F(row.Micrometers)} µm, which would make the absorption A = 1 − R − T negative.");
        }
        return problems;
    }
}
