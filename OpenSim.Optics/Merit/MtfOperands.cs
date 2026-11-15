using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The MTF operands (wave 7 wiring of OP-39 into OP-54/OP-56; docs/Optics-Conventions.md §40):
/// the diffraction MTF (MTFT, MTFS, MTFA), the geometric MTF (GMTT, GMTS, GMTA), both through
/// OP-39's <see cref="MtfEngine.Evaluate"/>, and the Moore–Elliott contrast operands (MECT, MECS,
/// MECA) the Optimization Wizard's Contrast criterion writes.
/// <para>
/// <b>Definitions</b> are OpticStudio 2025 R2's help pages "MTF Data" (plans/sources/optics/op-39-help/)
/// and "Optimization Operands Alphabetically" (op-54-help/). MTFA: "Diffraction modulation transfer
/// function, average of sagittal and tangential. The parameters are: Samp ... Wave: The wavelength
/// number to use (use 0 for polychromatic). Field: The field number ... Freq: The spatial frequency
/// in MTF units ... Data Type: ... An input of 0 will return the modulation amplitude"; GMTA the
/// same for the geometric MTF with "!Scl: If zero, then the diffraction limit will be used to scale
/// the results (recommended) otherwise, no scaling is done". The help names the parameters in that
/// order but does not number the editor's columns: this program puts them in that order (Int1 Samp,
/// Int2 Wave, Data1 Field, Data2 Freq, then Data3 and Data4), a reading of the help.
/// </para>
/// <para>
/// <b>What is not OpticStudio's.</b> The help's "fast, sparse sampling integration method"
/// (Grid = 0) is not described, so every MTF operand computes the MTF on a pupil grid, as the help's
/// Grid = 1 does ("the alternate grid-based algorithm used by the MTF analysis feature"); the Grid
/// column is read and either value gives the grid result. Samp n is the grid 32·2ⁿ⁻¹ nodes per side
/// (n = 1 to 6), the help's mapping for the Huygens MTF operands ("1 yields 32 x 32, 2 yields 64 x
/// 64 etc.") and the tolerancing MTF ("a sampling of 1 yielding a 32 x 32 grid, sampling of 2
/// yielding a 64 x 64 grid, etc."), read as doubling. Data Type 1 to 3 (real part, imaginary part,
/// phase) and Field 0 (the diffraction limit) are not built and are refused, naming the row.
/// </para>
/// <para>
/// <b>Moore–Elliott contrast.</b> The help ("Optimizing for MTF", op-36-help/, and its equation
/// images in op-39-help/images/): the MTF at frequency ξ is the autocorrelation of the pupil at the
/// shift Δ with ξ(Δ) = Δ/(D/2)·ξ_c, ξ_c = 1/(λ(F/#)), and it is maximised by driving the phase
/// differences φ(x_j, y_k) − φ(x_j − Δ, y_k) to zero. So a MEC row's value is
/// <code>OPD(Px, Py) − OPD(Px − Δx, Py − Δy)</code>
/// in waves at the row's wavelength. The help's shift is stated two ways that disagree (its text
/// puts the cutoff at Δ = D/2, where two pupils a radius apart still overlap); as OP-39's Contrast
/// Loss Map does (§10 item 34), the shift here is the autocorrelation shift of the MTF at ξ, which
/// reaches the cutoff when the pupils just touch: Δ/D = ξ/ξ_c, so in normalised pupil units (1 =
/// the radius) the shift is 2·ξ·λ·(F/#). It runs along pupil y for MECT (tangential, as OP-39's FFT engine orients the tangential MTF) and along
/// pupil x for MECS. The OPD is OPDC's (<see cref="RayAberrationOperands"/>: against the reference
/// sphere centred on the row wavelength's chief ray). F/# is the paraxial working F/# at the row's
/// wavelength (1/(2n′u′), which carries the image-space index); λ the wavelength in air. MECA is
/// (MECT + MECS)/2, the help's "average of sagittal and tangential". The help names the parameters
/// Wave, Field, Freq and "Hx, Hy, Px, and Py": this program takes the field from Hx and Hy (Int2
/// Wave, Data1–4 Hx, Hy, Px, Py, Data5 Freq; Int1 unused). The shifted point must lie inside the
/// unit pupil; a row whose partner lies outside is refused. These columns, the unit (waves), the
/// sign and the F/# used are readings of the help, not checked against OpticStudio's output
/// (§10 item 36).
/// </para>
/// </summary>
public static class MtfOperands
{
    private const string Page = "MTF Data; Optimization Operands Alphabetically";
    private const string Category = "MTF data";

    /// <summary>The largest Samp: 32·2⁵ = 1024 nodes per side.</summary>
    public const int MaximumSamp = 6;

    private enum Part { Tangential, Sagittal, Average }

    /// <summary>The operands, in registration order.</summary>
    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        yield return Mtf("MTFT", "Diffraction MTF tangential", Part.Tangential, geometric: false);
        yield return Mtf("MTFS", "Diffraction MTF sagittal", Part.Sagittal, geometric: false);
        yield return Mtf("MTFA", "Diffraction MTF average", Part.Average, geometric: false);
        yield return Mtf("GMTT", "Geometric MTF tangential", Part.Tangential, geometric: true);
        yield return Mtf("GMTS", "Geometric MTF sagittal", Part.Sagittal, geometric: true);
        yield return Mtf("GMTA", "Geometric MTF average", Part.Average, geometric: true);
        yield return Contrast("MECT", "Moore–Elliott contrast tangential", Part.Tangential);
        yield return Contrast("MECS", "Moore–Elliott contrast sagittal", Part.Sagittal);
        yield return Contrast("MECA", "Moore–Elliott contrast average", Part.Average);
    }

    /// <summary>The pupil grid of Samp <paramref name="samp"/> (1 to <see cref="MaximumSamp"/>).</summary>
    public static int GridOf(int samp)
    {
        if (samp < 1 || samp > MaximumSamp)
            throw new MeritOperandException($"Samp {samp} is not 1 to {MaximumSamp} (32 × 32 to 1024 × 1024 pupil nodes).");
        return 32 << (samp - 1);
    }

    // ------------------------------------------------------------------ MTF

    private static MeritOperandDefinition Mtf(string code, string name, Part part, bool geometric) => new()
    {
        Code = code, Name = name, Category = Category, Source = Page,
        Description = (geometric ? "Geometric MTF (OP-39's geometric engine" : "Diffraction MTF (OP-39's FFT engine")
            + $", {part.ToString().ToLowerInvariant()}{(part == Part.Average ? " of tangential and sagittal" : "")}) of field Field at Freq (MTF units), wavelength Wave (0: polychromatic), on a 32·2^(Samp−1) pupil grid."
            + (geometric ? " !Scl 0 multiplies by the diffraction-limited MTF of the same pupil." : " Data Type 0 (modulation) only."),
        Int1 = "Samp", Int2 = "Wave",
        Data = geometric ? new[] { "Field", "Freq", "!Scl", "Grid" } : new[] { "Field", "Freq", "Grid", "Data Type" },
        Compute = c =>
        {
            var v = MtfOf(c, geometric);
            return part switch { Part.Tangential => v.Tangential, Part.Sagittal => v.Sagittal, _ => v.Average };
        },
    };

    private readonly record struct MtfKey(bool Geometric, int Grid, int Wave, int Field, double Frequency, bool Scale);

    private static readonly ConditionalWeakTable<SequentialLens, ConditionalWeakTable<OpticalSystemData, ConcurrentDictionary<MtfKey, MtfValue>>> Cache = new();

    private static MtfValue MtfOf(MeritRowContext c, bool geometric)
    {
        var row = c.Row;
        int grid = GridOf(row.Int1);
        int wave = row.Int2 == 0 ? 0 : c.Wavelength(row.Int2);
        int field = WholeNumber(row.DataAt(1), "Field");
        int fields = c.System.Fields.Points.Count;
        if (field == 0)
            throw new MeritOperandException("Field 0 (the diffraction-limited MTF) is not built; give a field number.");
        if (field < 1 || field > fields)
            throw new MeritOperandException($"Field {field} is not a field; the system has fields 1 to {fields}.");
        double frequency = row.DataAt(2);
        if (!(frequency >= 0) || !double.IsFinite(frequency))
            throw new MeritOperandException($"Freq {RayAberrationOperands.F(frequency)} must be finite and not negative.");
        bool scale = true;
        if (geometric) scale = row.DataAt(3) == 0;
        else if (row.DataAt(4) != 0)
            throw new MeritOperandException($"Data Type {RayAberrationOperands.F(row.DataAt(4))} is not built; only 0 (the modulation) is.");
        var key = new MtfKey(geometric, grid, wave, field, frequency, scale);
        var table = Cache.GetValue(c.Lens, _ => new ConditionalWeakTable<OpticalSystemData, ConcurrentDictionary<MtfKey, MtfValue>>())
            .GetValue(c.System, _ => new ConcurrentDictionary<MtfKey, MtfValue>());
        if (table.TryGetValue(key, out var hit)) return hit;
        var waves = wave == 0 ? Enumerable.Range(0, c.System.Wavelengths.Wavelengths.Count).ToArray() : new[] { wave - 1 };
        var options = new MtfOptions { Method = geometric ? MtfMethod.Geometric : MtfMethod.Fft, Sampling = grid, MultiplyByDiffractionLimit = scale };
        MtfValue value;
        try { value = MtfEngine.Evaluate(c.Lens, c.System, field - 1, waves, frequency, options); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new MeritOperandException($"the MTF of field {field} cannot be computed: {e.Message}");
        }
        table[key] = value;
        return value;
    }

    private static int WholeNumber(double value, string label)
    {
        if (!double.IsFinite(value) || value != Math.Round(value) || Math.Abs(value) > int.MaxValue)
            throw new MeritOperandException($"{label} {RayAberrationOperands.F(value)} is not a whole number.");
        return (int)value;
    }

    // ------------------------------------------------------------------ Moore–Elliott contrast

    private static MeritOperandDefinition Contrast(string code, string name, Part part) => new()
    {
        Code = code, Name = name, Category = Category, Source = Page + "; Optimizing for MTF",
        Description = part switch
        {
            Part.Tangential => "OPD(Px, Py) − OPD(Px, Py − Δ) in waves at wavelength Wave, Δ = 2·Freq·λ·(F/#) in normalised pupil units: the pupil phase difference across the shift of frequency Freq along y.",
            Part.Sagittal => "OPD(Px, Py) − OPD(Px − Δ, Py) in waves at wavelength Wave, Δ = 2·Freq·λ·(F/#) in normalised pupil units: the pupil phase difference across the shift of frequency Freq along x.",
            _ => "The mean of the tangential and sagittal phase differences (MECT, MECS) at Freq.",
        },
        Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py", "Freq" },
        Compute = c =>
        {
            int wave = c.Wavelength(c.Row.Int2);
            double delta = ContrastShift(c.Lens, c.System, wave, c.Row.DataAt(5));
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
            var prepared = RayAberrationOperands.For(c.Lens, c.System);
            double Difference(double dx, double dy)
            {
                double qx = px - dx, qy = py - dy;
                if (qx * qx + qy * qy > 1 + 1e-12)
                    throw new MeritOperandException($"the shifted pupil point (Px {RayAberrationOperands.F(qx)}, Py {RayAberrationOperands.F(qy)}) lies outside the pupil; the shift is {RayAberrationOperands.F(delta)} of the pupil radius at Freq {RayAberrationOperands.F(c.Row.DataAt(5))}.");
                double a = prepared.SingleOpd(wave, hx, hy, px, py, toPrimary: false, out double lambda);
                double b = prepared.SingleOpd(wave, hx, hy, qx, qy, toPrimary: false, out _);
                return OpticsConventions.InWaves(a - b, lambda);
            }
            return part switch
            {
                Part.Tangential => Difference(0, delta),
                Part.Sagittal => Difference(delta, 0),
                _ => (Difference(0, delta) + Difference(delta, 0)) / 2,
            };
        },
    };

    /// <summary>
    /// The Moore–Elliott pupil shift in normalised pupil units (1 = the pupil radius) of frequency
    /// <paramref name="frequency"/> (cycles/mm) at wavelength number <paramref name="wave"/>:
    /// 2·ξ/ξ_c = 2·ξ·λ·(F/#), ξ_c = 1/(λ(F/#)) (help, "Optimizing for MTF", equation 1), with the
    /// cutoff where the pupils just touch (see the class remarks), λ the wavelength in air and F/#
    /// the paraxial working F/#. Afocal image space is refused (no working F/#).
    /// </summary>
    public static double ContrastShift(SequentialLens lens, OpticalSystemData system, int wave, double frequency)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        if (!(frequency > 0) || !double.IsFinite(frequency))
            throw new MeritOperandException($"Freq {RayAberrationOperands.F(frequency)} must be positive (cycles/mm).");
        ParaxialSystem p;
        try
        {
            var s = system.Fields.Type == FieldType.RealImageHeight
                ? system with { Fields = system.Fields with { Type = FieldType.ParaxialImageHeight } } : system;
            p = ParaxialSystem.Create(lens.ToParaxialLens(), s, wave - 1);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new MeritOperandException($"the paraxial model cannot be built: {e.Message}");
        }
        if (system.Aperture.AfocalImageSpace || p.WorkingFNumber is not double f || !(f > 0) || !double.IsFinite(f))
            throw new MeritOperandException("the Moore–Elliott contrast needs the working F/#, and this system has none (afocal image space is not built).");
        double lambda = RayAberrationOperands.For(lens, system).Lambda(wave);   // air, meters, as the OPD rows use
        return 2 * frequency * MtfEngine.PerMtfUnit * lambda * f;
    }
}
