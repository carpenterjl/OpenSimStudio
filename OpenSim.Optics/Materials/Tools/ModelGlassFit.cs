using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Materials;

/// <summary>
/// Model (fictitious) glasses from nd, Vd and ΔP_g,F (OpticStudio's "model glass"; plan item
/// OP-14). OpticStudio's own model is a fit over its catalogs that is not published, so this is
/// a documented stand-in of this program's making, <b>not OpticStudio's formula</b>; a model
/// glass here will not give OpticStudio's indices away from the d line.
/// <para>
/// <b>The model.</b> Write a glass's index as n(λ) = nd + (nF − nC)·P(λ), with P(λ) its relative
/// partial dispersion. Across a catalog P(λ) at each wavelength is close to linear in P_g,F,
/// so the model is P(λ) = a(λ) + b(λ)·P_g,F, with a and b each a combination of the six
/// Herzberger terms (1, L, L², λ², λ⁴, λ⁶; L = 1/(λ² − 0.028), formula 3 of
/// <see cref="DispersionFormulas"/>). Because n is then linear in the Herzberger terms, a model
/// glass is an ordinary formula-3 glass: C = nd·e₁ + (nF − nC)·(α + P_g,F·β).
/// </para>
/// <para>
/// <b>The fit.</b> α and β (twelve numbers) are found by least squares over every training glass
/// and every spectral line of <see cref="SpectralLines"/> inside the training range, each row
/// weighted by the glass's nF − nC so the residual is in index units. Six equality constraints
/// (<see cref="ConstrainedLeastSquares"/>) make a(d) = b(d) = 0, a(F) − a(C) = 1,
/// b(F) − b(C) = 0, a(g) − a(F) = 0 and b(g) − b(F) = 1, so every model glass has exactly the
/// requested nd, Vd and P_g,F, to rounding, whatever the training set. The training glasses'
/// nd, Vd and P_g,F are computed from their coefficients (<see cref="GlassProperties"/>).
/// </para>
/// <para>
/// The valid range of a model glass is the training range: by default the h line (0.4046561 µm)
/// to the t line (1.01398 µm) of TIE-29 Table 2.3-1, chosen so that most visible glasses cover
/// it; a glass that does not cover the whole range is left out of training and named.
/// </para>
/// </summary>
public sealed class ModelGlassFit
{
    /// <summary>The Herzberger pole constant as both readers print it (formula 3).</summary>
    private const double HerzbergerPole = 0.028;

    private ModelGlassFit(double[] alpha, double[] beta, double min, double max, double[] wavelengths,
        int trainingCount, IReadOnlyList<string> catalogs, IReadOnlyList<(OpticalMaterial Glass, string Reason)> leftOut,
        double rms, double worst, string worstGlass)
    {
        Alpha = alpha;
        Beta = beta;
        MinMicrometers = min;
        MaxMicrometers = max;
        Wavelengths = wavelengths;
        TrainingCount = trainingCount;
        TrainingCatalogs = catalogs;
        LeftOut = leftOut;
        RmsIndexResidual = rms;
        MaxIndexResidual = worst;
        WorstGlass = worstGlass;
    }

    /// <summary>The default training range: TIE-29's h line …</summary>
    public static double DefaultMinMicrometers => SpectralLines.H.Micrometers;

    /// <summary>… to its t line.</summary>
    public static double DefaultMaxMicrometers => SpectralLines.T.Micrometers;

    /// <summary>a(λ) as Herzberger coefficients.</summary>
    public IReadOnlyList<double> Alpha { get; }

    /// <summary>b(λ) as Herzberger coefficients.</summary>
    public IReadOnlyList<double> Beta { get; }

    public double MinMicrometers { get; }
    public double MaxMicrometers { get; }

    /// <summary>The spectral lines the fit used, µm.</summary>
    public IReadOnlyList<double> Wavelengths { get; }

    public int TrainingCount { get; }
    public IReadOnlyList<string> TrainingCatalogs { get; }

    /// <summary>Materials offered for training but not used, with the reason.</summary>
    public IReadOnlyList<(OpticalMaterial Glass, string Reason)> LeftOut { get; }

    /// <summary>The RMS and largest |model − glass| index over the training glasses and lines:
    /// how well a model glass with a training glass's nd, Vd and P_g,F reproduces it.</summary>
    public double RmsIndexResidual { get; }
    public double MaxIndexResidual { get; }
    public string WorstGlass { get; }

    /// <summary>Trains the model on <paramref name="glasses"/> over the given range.</summary>
    public static ModelGlassFit Train(IEnumerable<OpticalMaterial> glasses, double? minMicrometers = null, double? maxMicrometers = null)
    {
        ArgumentNullException.ThrowIfNull(glasses);
        double min = minMicrometers ?? DefaultMinMicrometers, max = maxMicrometers ?? DefaultMaxMicrometers;
        double g = SpectralLines.G.Micrometers, f = SpectralLines.F.Micrometers, d = SpectralLines.LowerD.Micrometers, c = SpectralLines.C.Micrometers;
        if (!(min <= g) || !(max >= c) || !(min > Math.Sqrt(HerzbergerPole)))
            throw new ArgumentException(
                $"The training range {N(min)}–{N(max)} µm must contain the g, F, d and C lines and stay above the Herzberger pole at {N(Math.Sqrt(HerzbergerPole))} µm.");
        var lines = SpectralLines.All.Select(l => l.Micrometers).Where(w => w >= min && w <= max).Distinct().OrderBy(w => w).ToArray();

        var used = new List<(GlassProperties P, double[] N)>();
        var leftOut = new List<(OpticalMaterial, string)>();
        foreach (var glass in glasses)
        {
            if (!GlassProperties.TryCompute(glass, out var p, out string reason)) { leftOut.Add((glass, reason)); continue; }
            if (p!.PgF is null) { leftOut.Add((glass, $"{glass.Who}: its valid range does not reach the g line.")); continue; }
            if (!glass.Covers(min) || !glass.Covers(max)) { leftOut.Add((glass, $"{glass.Who}: its valid range does not cover the training range {N(min)}–{N(max)} µm.")); continue; }
            try
            {
                used.Add((p, lines.Select(glass.RelativeIndex).ToArray()));
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                leftOut.Add((glass, e.Message.Split(" (Parameter")[0]));
            }
        }
        if (used.Count < 2)
            throw new InvalidOperationException(
                $"A model glass needs at least two training glasses with indices over {N(min)}–{N(max)} µm; {used.Count} of the materials offered qualify. Activate a catalog of visible glasses.");

        // Unknowns: α₀…α₅, β₀…β₅. Row (i, k): Δᵢ·Σⱼ (αⱼ + Pᵢβⱼ)φⱼ(λₖ) ≈ nᵢ(λₖ) − ndᵢ.
        var phi = lines.Select(Basis).ToArray();
        var a = new double[used.Count * lines.Length, 12];
        var b = new double[used.Count * lines.Length];
        int row = 0;
        foreach (var (p, n) in used)
        {
            double delta = p.PrincipalDispersion, pgf = p.PgF!.Value;
            for (int k = 0; k < lines.Length; k++, row++)
            {
                for (int j = 0; j < 6; j++)
                {
                    a[row, j] = delta * phi[k][j];
                    a[row, 6 + j] = delta * pgf * phi[k][j];
                }
                b[row] = n[k] - p.Nd;
            }
        }
        double[] pd = Basis(d), pf = Basis(f), pc = Basis(c), pg = Basis(g);
        var gm = new double[6, 12];
        var h = new double[6];
        for (int j = 0; j < 6; j++)
        {
            gm[0, j] = pd[j];               // a(d) = 0
            gm[1, j] = pf[j] - pc[j];       // a(F) − a(C) = 1
            gm[2, j] = pg[j] - pf[j];       // a(g) − a(F) = 0
            gm[3, 6 + j] = pd[j];           // b(d) = 0
            gm[4, 6 + j] = pf[j] - pc[j];   // b(F) − b(C) = 0
            gm[5, 6 + j] = pg[j] - pf[j];   // b(g) − b(F) = 1
        }
        h[1] = 1;
        h[5] = 1;
        var x = ConstrainedLeastSquares.Solve(a, b, gm, h);
        var alpha = x.Take(6).ToArray();
        var beta = x.Skip(6).ToArray();

        double sum = 0, worst = 0;
        string worstGlass = "";
        int count = 0;
        foreach (var (p, n) in used)
            for (int k = 0; k < lines.Length; k++)
            {
                double model = p.Nd + p.PrincipalDispersion * Combine(alpha, beta, p.PgF!.Value, phi[k]);
                double r = Math.Abs(model - n[k]);
                sum += r * r;
                count++;
                if (r > worst) { worst = r; worstGlass = p.Material.Who; }
            }
        var catalogs = used.Select(u => u.P.Material.Catalog).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ModelGlassFit(alpha, beta, min, max, lines, used.Count, catalogs, leftOut, Math.Sqrt(sum / count), worst, worstGlass);
    }

    /// <summary>
    /// A model glass with the requested nd, Vd and ΔP_g,F (P_g,F = 0.6438 − 0.001682·Vd + ΔP_g,F,
    /// <see cref="GlassProperties.NormalLinePgF"/>): a formula-3 (Herzberger) glass valid over
    /// the training range, its catalog empty, its printed nd and Vd the requested values.
    /// </summary>
    public OpticalMaterial Create(double nd, double vd, double deltaPgF, string name = "MODEL")
    {
        if (!double.IsFinite(nd) || nd <= 1) throw new ArgumentOutOfRangeException(nameof(nd), nd, "A model glass needs nd above 1.");
        if (!double.IsFinite(vd) || vd <= 0) throw new ArgumentOutOfRangeException(nameof(vd), vd, "A model glass needs a positive Vd.");
        if (!double.IsFinite(deltaPgF)) throw new ArgumentOutOfRangeException(nameof(deltaPgF), deltaPgF, "ΔPgF must be a finite number.");
        double delta = (nd - 1) / vd, pgf = GlassProperties.NormalLinePgF(vd) + deltaPgF;
        var coefficients = new double[6];
        for (int j = 0; j < 6; j++) coefficients[j] = delta * (Alpha[j] + pgf * Beta[j]);
        coefficients[0] += nd;
        return new OpticalMaterial
        {
            Name = string.IsNullOrWhiteSpace(name) ? "MODEL" : name,
            Comment = $"Model glass: OpenSim's stand-in fitted to {TrainingCount} glasses, not OpticStudio's model.",
            FormulaNumber = (int)DispersionFormula.Herzberger,
            Coefficients = coefficients,
            MinMicrometers = MinMicrometers,
            MaxMicrometers = MaxMicrometers,
            Nd = new PrintedNumber(nd, nd.ToString("R", CultureInfo.InvariantCulture)),
            Vd = new PrintedNumber(vd, vd.ToString("R", CultureInfo.InvariantCulture)),
            DeltaPgF = deltaPgF,
        };
    }

    /// <summary>The six Herzberger terms at λ (µm): 1, L, L², λ², λ⁴, λ⁶.</summary>
    internal static double[] Basis(double micrometers)
    {
        double w2 = micrometers * micrometers, l = 1 / (w2 - HerzbergerPole);
        return new[] { 1, l, l * l, w2, w2 * w2, w2 * w2 * w2 };
    }

    private static double Combine(double[] alpha, double[] beta, double pgf, double[] phi)
    {
        double s = 0;
        for (int j = 0; j < 6; j++) s += (alpha[j] + pgf * beta[j]) * phi[j];
        return s;
    }

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
