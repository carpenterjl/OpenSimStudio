using System.Numerics;
using OpenSim.Solvers.Magnetics;

namespace OpenSim.Pcb.Magnetics;

/// <summary>What a planar-magnetics solve of a winding section needs: the turns (cut from a board
/// or entered by hand), which winding carries the 1 A drive, the frequency (0 = static), an
/// optional core, and how the turns conduct. SI units.</summary>
public sealed record PlanarMagneticsInput
{
    public required IReadOnlyList<WindingTurn> Turns { get; init; }

    /// <summary>The winding driven with 1 A; null = the first of <see cref="Windings"/>.</summary>
    public string? Drive { get; init; }

    /// <summary>The windings in the order their mutuals are listed; null = the order they first
    /// appear in the turns.</summary>
    public IReadOnlyList<string>? Windings { get; init; }

    /// <summary>0 solves the static field (inductance only); above 0 the time-harmonic one, where
    /// solid turns show skin and proximity effect.</summary>
    public double FrequencyHz { get; init; }

    public AxisymmetricCore? Core { get; init; }

    /// <summary>A stranded (litz) winding's strand diameter [m]; 0 = solid copper turns when AC.</summary>
    public double StrandDiameter { get; init; }
    public int StrandsPerTurn { get; init; } = 1;
    public double CopperConductivity { get; init; } = 5.8e7;
}

/// <summary>A solved winding section: the model, the field, and the driven winding's inductance,
/// AC resistance (solid turns), strand-loss resistance (stranded AC) and its mutual inductance to
/// every other winding.</summary>
public sealed record PlanarMagneticsResult(PlanarMagneticsInput Input, PlanarMagneticModel Built, MagneticSolution2D Solution,
    string Drive, bool SolidTurns, Complex Inductance, double? AcResistance, double? StrandResistance,
    IReadOnlyList<(string Winding, Complex Mutual)> Mutuals)
{
    /// <summary>The results as the command line prints them: L, R_ac, the strands' R_ac, M per winding.</summary>
    public IReadOnlyList<(string Key, string Value)> Lines()
    {
        double f = Input.FrequencyHz;
        var lines = new List<(string, string)> { ($"L({Drive})", $"{Inductance.Real * 1e9:g5} nH") };
        if (AcResistance is { } r) lines.Add(($"R_ac({Drive}) at {f:g4} Hz", $"{r * 1e3:g5} mΩ"));
        if (StrandResistance is { } rs) lines.Add(($"R_ac({Drive}) at {f:g4} Hz, strands", $"{rs * 1e3:g5} mΩ"));
        foreach (var (winding, m) in Mutuals) lines.Add(($"M({Drive},{winding})", $"{m.Real * 1e9:g5} nH"));
        return lines;
    }

    /// <summary>The solve as a report: the inputs, the results, the turns, and the solve's log and
    /// notes. <paramref name="axis"/> says where the section was cut (null for turns entered by hand);
    /// <paramref name="cutNotes"/> are the averaged cut's notes.</summary>
    public OpenSim.Core.PostProcessing.StudyReport Report(string title, string? axis = null, IReadOnlyList<string>? cutNotes = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var windings = Input.Windings ?? Built.Windings.Keys.ToList();
        var core = Input.Core;
        var inputs = new List<(string, string)> { ("Windings", string.Join(", ", windings)), ("Driven", Drive) };
        if (axis is not null) inputs.Add(("Axis", axis));
        inputs.Add(("Frequency", Input.FrequencyHz > 0 ? $"{Input.FrequencyHz:g5} Hz" : "static"));
        inputs.Add(("Core", core is null ? "none" : $"post {core.PostRadius * 1e3:g4} mm, gap {core.CentreGap * 1e3:g4} mm, µr {core.Material.RelativePermeability:g4}"));
        return new OpenSim.Core.PostProcessing.StudyReport
        {
            Title = title,
            Inputs = inputs,
            Results = Lines().ToList(),
            Sections = new[]
            {
                new OpenSim.Core.PostProcessing.ReportSection("Turns")
                {
                    TableHeaders = new[] { "Winding", "Layer", "r0 (mm)", "r1 (mm)", "z0 (µm)", "z1 (µm)" },
                    TableRows = Input.Turns.Select(t => (IReadOnlyList<string>)new[]
                    {
                        t.Net, t.LayerOrder.ToString(inv), (t.R0 * 1e3).ToString("0.###", inv),
                        (t.R1 * 1e3).ToString("0.###", inv), (t.Z0 * 1e6).ToString("0.#", inv),
                        (t.Z1 * 1e6).ToString("0.#", inv)
                    }).ToList()
                },
                new OpenSim.Core.PostProcessing.ReportSection("Solve")
                {
                    Lines = (cutNotes ?? Array.Empty<string>()).Concat(Solution.Log).Concat(Built.Notes).ToList()
                }
            },
            Assumptions = PlanarWindingSection.Assumptions.Concat(MagneticSolver2D.Assumptions).ToList()
        };
    }
}

/// <summary>
/// The planar-magnetics solve the command line's <c>magnetics</c> command and the app's panel both
/// run: the turns built into the axisymmetric model (with the core), one winding driven with 1 A,
/// the static or time-harmonic field, and the driven winding's L, R_ac and mutuals read off it. For
/// the full winding impedance matrix (one solve per winding) see
/// <see cref="PlanarWindingSection.ImpedanceMatrix"/>.
/// </summary>
public static class PlanarMagneticsStudy
{
    public static PlanarMagneticsResult Run(PlanarMagneticsInput input)
    {
        if (input.Turns.Count == 0) throw new ArgumentException("No turns: the cut crossed no copper of the windings.", nameof(input));
        var windings = input.Windings ?? input.Turns.Select(t => t.Net).Distinct().ToList();
        string drive = input.Drive ?? windings.FirstOrDefault()
            ?? throw new ArgumentException("No winding to drive.", nameof(input));
        double frequency = input.FrequencyHz;
        bool solid = frequency > 0 && input.StrandDiameter == 0;
        var built = PlanarWindingSection.Build(input.Turns, new Dictionary<string, Complex> { [drive] = 1 }, input.Core,
            solidTurns: solid, input.CopperConductivity, strandDiameter: input.StrandDiameter, strandsPerTurn: input.StrandsPerTurn);
        if (!built.Windings.ContainsKey(drive))
            throw new ArgumentException($"The driven winding '{drive}' has no turns in the cut.", nameof(input));
        var solver = new MagneticSolver2D();
        var solution = frequency > 0 ? solver.SolveTimeHarmonic(built.Model, frequency) : solver.SolveStatic(built.Model);

        var l = built.Inductance(solution, drive);
        double? acResistance = frequency > 0 && solid ? built.Resistance(solution, drive) : null;
        // Every winding's strand loss loads the drive (1 A), the passive windings' proximity loss included.
        double? strandResistance = frequency > 0 && input.StrandDiameter > 0 ? 2 * built.StrandLossWatts(solution) : null;
        // Mutual inductance to the undriven windings: static or stranded, their turns carry no
        // current, so their flux linkage per ampere of the driven winding is M; solid at AC, their
        // open-circuit voltage over jω is.
        var mutuals = new List<(string, Complex)>();
        foreach (var other in windings.Where(n => n != drive && built.Windings.ContainsKey(n)))
        {
            var m = solid
                ? built.Windings[other].Aggregate(Complex.Zero, (s, t) => s + solution.ConductorVoltage(t.Name))
                  / new Complex(0, 2 * Math.PI * frequency)
                : built.Windings[other].Aggregate(Complex.Zero, (s, t) => s + solution.FluxLinkage(t.Name));
            mutuals.Add((other, m));
        }
        return new PlanarMagneticsResult(input, built, solution, drive, solid, l, acResistance, strandResistance, mutuals);
    }
}
