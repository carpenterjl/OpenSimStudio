using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>
/// One 2D conduction filament: a strip [<paramref name="X0"/>, <paramref name="X1"/>] at
/// height <paramref name="Z"/> above the PEC ground, infinitely long in y. Treated as
/// ZERO-thickness for the magnetic (log) kernel — the current sheet — but carrying a true
/// cross-sectional <paramref name="Area"/> for the per-unit-length resistance
/// 1/(σ·Area). A conductor is tiled into a stack of these in x and z so the current can
/// redistribute (edge crowding + skin effect); many thin z-levels resolve the surface.
/// </summary>
public readonly record struct ConductionFilament(
    double X0, double X1, double Z, double Area, double ConductivitySiemensPerMeter, int Conductor)
{
    public double Width => X1 - X0;

    /// <summary>DC series resistance of this filament per unit length [Ω/m].</summary>
    public double ResistancePerMeter => 1.0 / (ConductivitySiemensPerMeter * Area);
}

/// <summary>
/// The frequency-dependent proximity-effect result (Stage S8): a log-spaced table of the
/// N×N series-resistance matrix R(f) and the frequency-dependent INTERNAL inductance
/// ΔL(f) (the part that vanishes at high frequency, over the external inductance). Its
/// <see cref="ResistanceMatrix"/>/<see cref="InternalInductance"/> providers interpolate
/// the table (piecewise-linear in log f, clamped at the ends) — cheap to call per
/// FFT bin from the MTL transient.
/// </summary>
public sealed class ProximityResult
{
    private readonly double[] _logF;
    private readonly double[][,] _r;
    private readonly double[][,] _dl;

    internal ProximityResult(int conductorCount, double[] frequenciesHz,
        double[][,] resistance, double[][,] internalInductance)
    {
        ConductorCount = conductorCount;
        FrequenciesHz = frequenciesHz;
        _r = resistance;
        _dl = internalInductance;
        _logF = frequenciesHz.Select(f => Math.Log(Math.Max(f, double.Epsilon))).ToArray();
    }

    public int ConductorCount { get; }
    public IReadOnlyList<double> FrequenciesHz { get; }

    /// <summary>N×N series resistance R(f) [Ω/m] at an arbitrary frequency.</summary>
    public double[,] ResistanceMatrix(double frequencyHz) => Interpolate(_r, frequencyHz);

    /// <summary>N×N internal inductance ΔL(f) [H/m] to ADD to the external L (→ 0 high-f).</summary>
    public double[,] InternalInductance(double frequencyHz) => Interpolate(_dl, frequencyHz);

    private double[,] Interpolate(double[][,] table, double frequencyHz)
    {
        int n = ConductorCount;
        double logf = Math.Log(Math.Max(frequencyHz, double.Epsilon));
        // Below/above the tabulated band: clamp (R plateaus at R_dc below, √f slope above
        // is captured by the top sample; ΔL → its endpoint). No extrapolation past the band.
        if (logf <= _logF[0]) return table[0];
        if (logf >= _logF[^1]) return table[^1];
        int hi = 1;
        while (_logF[hi] < logf) hi++;
        double t = (logf - _logF[hi - 1]) / (_logF[hi] - _logF[hi - 1]);
        var lo = table[hi - 1];
        var up = table[hi];
        var r = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] = lo[i, j] * (1 - t) + up[i, j] * t;   // convex combo keeps SPD
        return r;
    }
}

/// <summary>
/// Proximity-effect R(f) + internal L(f) by a 2D magneto-quasi-static VOLUME-FILAMENT
/// solve (SI Stage S8) — the honest current-redistribution model the v1 RLGC extractor's
/// per-conductor <c>max(R_dc, R_s√f)</c> could not carry.
///
/// <para><b>Formulation.</b> Every conductor's cross-section is tiled into thin current
/// sheets (filaments). Each filament k obeys E_y = J_k/σ = U_c − jω·A_y(k), where U_c is
/// the conductor's common axial voltage drop per unit length and A_y is the magnetic
/// vector potential from ALL filaments (with the PEC-ground image, current-reversed at
/// −z). In matrix form Z·I = B·U with Z_km = δ_km/(σ·area_k) + jω·M_km, M the partial
/// mutual inductance p.u.l., and B the conductor-incidence. Imposing Bᵀ·I = I_c per
/// conductor gives the bordered system [[Z, −B],[Bᵀ, 0]]·[I; U] = [0; I_c]; one LU per
/// frequency serves all N conductor drives, and Z_cond = (Bᵀ Z⁻¹ B)⁻¹ falls out of the
/// U block. R(f) = Re(Z_cond), L_full(f) = Im(Z_cond)/ω.</para>
///
/// <para><b>Kernel reuse.</b> The partial mutual inductance of two zero-thickness strips
/// at heights z_k, z_m is the SAME 2D log moment the electrostatic BEM uses:
/// M_km = (µ₀/2π)/(w_k·w_m)·[LogMoment(k,m,z_k+z_m) − LogMoment(k,m,|z_k−z_m|)] (the
/// second term is the ground image). No new kernel, no near-singular quadrature — the
/// self term is the LogMoment's D → 0 collinear form.</para>
///
/// <para><b>The 2D reference.</b> An isolated 2D conductor's external inductance is
/// reference-ambiguous (a uniform additive constant on M). That constant is purely
/// IMAGINARY in Z and, because a per-conductor drive's filament currents sum to a
/// conductor constant, it shifts only Im(Z_cond) — R(f) = Re(Z_cond) is exact regardless.
/// The internal inductance is gated through frequency DIFFERENCES, where the constant
/// cancels. With the ground image the total L is fully referenced.</para>
/// </summary>
public static class ProximityExtractor
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Epsilon0 = 8.8541878128e-12;

    /// <summary>
    /// Solves the per-unit-length conductor impedance matrix Z_cond [Ω/m] at one frequency
    /// for an arbitrary filament set (this is the physics kernel — the round-wire skin-effect
    /// gate drives it with a disk tiling). With <paramref name="groundImage"/> the PEC image
    /// at −z is included; without it the solve models an isolated conductor (R is still exact;
    /// the inductance carries the 2D reference constant).
    /// </summary>
    public static Complex[,] SolveConductorImpedance(
        IReadOnlyList<ConductionFilament> filaments, int conductorCount,
        double frequencyHz, bool groundImage, int? maxDegreeOfParallelism = null)
    {
        int m = filaments.Count;
        if (m == 0) throw new ArgumentException("At least one filament is required.", nameof(filaments));
        double omega = 2 * Math.PI * frequencyHz;

        // Bordered system order M + N: unknowns [filament currents; conductor voltages].
        int size = m + conductorCount;
        var system = new ComplexDenseMatrix(size, size);
        for (int k = 0; k < m; k++)
        {
            var fk = filaments[k];
            // Diagonal resistance + self inductance.
            for (int j = 0; j < m; j++)
            {
                double mkj = MutualInductance(fk, filaments[j], groundImage);
                Complex z = new Complex(0, omega * mkj);
                if (k == j) z += fk.ResistancePerMeter;
                system[k, j] = z;
            }
            // −B block (conductor incidence).
            system[k, m + fk.Conductor] = -Complex.One;
            // Bᵀ block: Σ_{k∈c} I_k = I_c.
            system[m + fk.Conductor, k] = Complex.One;
        }

        var lu = ComplexLu.Factor(system, maxDegreeOfParallelism);
        var zCond = new Complex[conductorCount, conductorCount];
        var rhs = new Complex[size];
        for (int c = 0; c < conductorCount; c++)
        {
            Array.Clear(rhs);
            rhs[m + c] = Complex.One;                  // unit current into conductor c
            var x = lu.Solve(rhs);
            for (int r = 0; r < conductorCount; r++)
                zCond[r, c] = x[m + r];                // the conductor voltages ARE Z_cond·e_c
        }
        return zCond;
    }

    /// <summary>Partial mutual inductance per unit length between two current-sheet
    /// filaments [Ω-independent, H/m]: (µ₀/2π)·⟨−ln r⟩ averaged over both strips, plus the
    /// ground image (a −current sheet at −z) when requested.</summary>
    private static double MutualInductance(in ConductionFilament a, in ConductionFilament b, bool groundImage)
    {
        double norm = Mu0 / (2 * Math.PI) / (a.Width * b.Width);
        // ⟨ln r⟩ = LogMoment / (w_a w_b); A_y ∝ −ln r ⇒ the direct term is negative.
        double direct = -RlgcExtractor.LogMoment(a.X0, a.X1, b.X0, b.X1, Math.Abs(a.Z - b.Z));
        double value = direct;
        if (groundImage)
            // Image current −1 at z = −b.Z ⇒ +ln r at offset a.Z + b.Z.
            value += RlgcExtractor.LogMoment(a.X0, a.X1, b.X0, b.X1, a.Z + b.Z);
        return norm * value;
    }

    // ------------------------------------------------------------------
    // High-level: a coupled cross-section over a frequency band → R(f), ΔL(f) table.
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts the proximity R(f)/ΔL(f) table for a coupled cross-section over
    /// [<paramref name="minFrequencyHz"/>, <paramref name="maxFrequencyHz"/>] on a log grid
    /// of <paramref name="points"/> samples. Conductors are tiled <paramref name="lateralCells"/>
    /// (cosine-graded in x) × <paramref name="thicknessCells"/> (cosine-graded in z) filaments;
    /// the ground image references the total inductance. ΔL(f) = L_full(f) − L_full(f_max) is
    /// the internal part (→ 0 at the top of the band, where the current is on the surface).
    /// Frequency samples run in parallel into ordered slots — bitwise-identical at any DOP
    /// (the LU is, and each sample is independent).
    /// </summary>
    public static ProximityResult Extract(CoupledLineCrossSection section,
        double minFrequencyHz, double maxFrequencyHz, int points = 24,
        int lateralCells = 24, int thicknessCells = 10, int? maxDegreeOfParallelism = null)
    {
        if (minFrequencyHz <= 0 || maxFrequencyHz <= minFrequencyHz)
            throw new ArgumentException("Need 0 < minFrequency < maxFrequency.", nameof(minFrequencyHz));
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points), "At least 2 samples.");

        int n = section.Traces.Count;
        double metalZ = section.Stackup.InterfaceHeights()[section.MetalInterface];
        var filaments = BuildFilaments(section.Traces, metalZ, lateralCells, thicknessCells);

        var freqs = new double[points];
        double logLo = Math.Log(minFrequencyHz), logHi = Math.Log(maxFrequencyHz);
        for (int k = 0; k < points; k++)
            freqs[k] = Math.Exp(logLo + (logHi - logLo) * k / (points - 1));

        var rTable = new double[points][,];
        var lFull = new double[points][,];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 };
        Parallel.For(0, points, options, k =>
        {
            double omega = 2 * Math.PI * freqs[k];
            var z = SolveConductorImpedance(filaments, n, freqs[k], groundImage: true, maxDegreeOfParallelism: 1);
            var r = new double[n, n];
            var l = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    r[i, j] = z[i, j].Real;
                    l[i, j] = z[i, j].Imaginary / omega;
                }
            rTable[k] = r;
            lFull[k] = l;
        });

        // ΔL(f) = L_full(f) − L_full(top): the frequency-dependent INTERNAL part, referenced
        // so the external inductance (µ₀ε₀C_air⁻¹) carries the rest without double-counting.
        var lExternal = lFull[^1];
        var dlTable = new double[points][,];
        for (int k = 0; k < points; k++)
        {
            var dl = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    dl[i, j] = lFull[k][i, j] - lExternal[i, j];
            dlTable[k] = dl;
        }
        return new ProximityResult(n, freqs, rTable, dlTable);
    }

    /// <summary>Builds a cosine-graded (x) × cosine-graded (z) filament tiling of every
    /// trace: the grading clusters cells at the strip edges AND the two z-surfaces, where
    /// the current crowds. Filaments sit at z ∈ [metalZ, metalZ + t] (copper on the interface).</summary>
    private static List<ConductionFilament> BuildFilaments(
        IReadOnlyList<TraceCrossSection> traces, double metalZ, int nx, int nz)
    {
        if (nx < 2 || nz < 1)
            throw new ArgumentOutOfRangeException(nameof(nx), "Need ≥2 lateral and ≥1 thickness cells.");
        var filaments = new List<ConductionFilament>(traces.Count * nx * nz);
        for (int c = 0; c < traces.Count; c++)
        {
            var trace = traces[c];
            double half = trace.WidthMeters / 2, center = trace.CenterMeters;
            double t = trace.ThicknessMeters, sigma = trace.ConductivitySiemensPerMeter;
            for (int i = 0; i < nx; i++)
            {
                // Cosine (Chebyshev) edges clustering at both strip sides.
                double x0 = center - half * Math.Cos(Math.PI * i / nx);
                double x1 = center - half * Math.Cos(Math.PI * (i + 1) / nx);
                double dx = x1 - x0;
                for (int j = 0; j < nz; j++)
                {
                    double z0 = metalZ + t * (1 - Math.Cos(Math.PI * j / nz)) / 2;
                    double z1 = metalZ + t * (1 - Math.Cos(Math.PI * (j + 1) / nz)) / 2;
                    double zc = 0.5 * (z0 + z1);
                    filaments.Add(new ConductionFilament(x0, x1, zc, dx * (z1 - z0), sigma, c));
                }
            }
        }
        return filaments;
    }

    /// <summary>The external inductance the internal ΔL(f) rides on: µ₀ε₀·C_air⁻¹ from the
    /// electrostatic dual (the SAME reference <see cref="RlgcExtractor"/> uses for L). Kept
    /// here so callers compose Z′ = R(f) + jω(L_ext + ΔL(f)) consistently.</summary>
    public static double Vacuum => Mu0 * Epsilon0;
}
