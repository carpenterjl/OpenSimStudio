using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Analysis.Sampling;

/// <summary>How an RMS over the pupil is integrated (OpticStudio's "Method" of the RMS analyses
/// and its "Pupil Integration" of the merit function).</summary>
public enum PupilIntegration
{
    /// <summary>Rings and arms with Gauss–Legendre weights in ρ² (<see cref="PupilQuadrature.Gaussian"/>).</summary>
    GaussianQuadrature = 0,

    /// <summary>A square grid of equally weighted points inside the pupil (<see cref="PupilQuadrature.Rectangular"/>).</summary>
    RectangularArray = 1,
}

/// <summary>
/// A pupil integration rule: normalised pupil points (Px, Py) and their weights, which sum to 1,
/// so Σ wᵢ f(Pxᵢ, Pyᵢ) estimates the mean of f over the (possibly obscured) unit pupil.
/// </summary>
/// <param name="Method">Which rule this is.</param>
/// <param name="Points">The pupil points, ring by ring (arm by arm within a ring) or row by row.</param>
/// <param name="Weights">One weight per point; they sum to 1 (to rounding).</param>
/// <param name="Density">Rings (Gaussian quadrature) or points across (rectangular array).</param>
/// <param name="Arms">Arms of the Gaussian rule; 0 for the rectangular array.</param>
/// <param name="Obscuration">The linear obscuration ratio ε of the Gaussian rule; 0 for none.</param>
public sealed record PupilQuadratureRule(PupilIntegration Method, IReadOnlyList<(double Px, double Py)> Points,
    IReadOnlyList<double> Weights, int Density, int Arms, double Obscuration)
{
    /// <summary>Σ wᵢ f(Pxᵢ, Pyᵢ): the rule's estimate of the mean of f over the pupil.</summary>
    public double Mean(Func<double, double, double> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        double sum = 0;
        for (int i = 0; i < Points.Count; i++) sum += Weights[i] * f(Points[i].Px, Points[i].Py);
        return sum;
    }

    /// <summary>One line saying how the pupil was sampled, for an analysis's assumptions.</summary>
    public string Describe() => Method == PupilIntegration.GaussianQuadrature
        ? $"Pupil integration: Gaussian quadrature, {Density} rings × {Arms} arms = {Points.Count} rays per field and wavelength"
          + (Obscuration > 0 ? $", over the annulus ε = {Obscuration:g6} ≤ ρ ≤ 1" : "")
          + $" (Gauss–Legendre nodes in ρ², arms equally spaced from +x); exact when the integrand is a polynomial of degree ≤ {PupilQuadrature.ExactDegree(Density, Arms)} in (Px, Py), "
          + $"so an RMS is exact when the OPD or the ray error is a polynomial of degree ≤ {PupilQuadrature.ExactDegree(Density, Arms) / 2} (docs/Optics-Conventions.md §34)."
        : $"Pupil integration: rectangular array, {Density} × {Density} cell-centred grid, the {Points.Count} points inside the unit pupil weighted equally.";
}

/// <summary>
/// Pupil quadrature for RMS spot radius and RMS wavefront (OP-40; docs/Optics-Conventions.md
/// §34).
/// <para>
/// <b>What OpticStudio's help says</b> (Ansys OpticStudio 2025 R2 help, ansyshelp.ansys.com,
/// OpticStudio_Help/topics; copies in <c>plans/sources/optics/op-40-help/</c>): "Gaussian
/// Quadrature": "The GQ algorithm requires specification of the number of "Rings", the number of
/// "Arms", and the "Obscuration" factor"; arms are "equally spaced (in angle)"; "The highest order
/// of aberration the GQ algorithm can accurately integrate is (2*n - 1) where n is the number of
/// rings"; the obscuration "is done as a linear fraction, not as a fraction of the pupil area";
/// "the weighting and ray set selection used is based on a method described in a paper by G. W.
/// Forbes, JOSA A 5, p1943, 1988". "RMS vs. Field": for GQ "the ray density specifies the number
/// of radial rays to be traced ... The maximum number is 20"; for the rectangular array "the ray
/// density indicates the grid size. Rays outside the circular entrance pupil are ignored".
/// </para>
/// <para>
/// <b>The rule, derived</b> (Forbes's paper was not available to this program, so the rule is
/// derived here from the help's description, not copied from it). The mean of f over the annulus
/// ε ≤ ρ ≤ 1 is (1/(π(1 − ε²)))∫∫ f ρ dρ dθ. With u = ρ², ρ dρ = du/2, so it is
/// (1/(2π(1 − ε²)))∫_{ε²}^{1}∫_0^{2π} f(√u, θ) dθ du: a plain integral in u. The rings sit at the n
/// Gauss–Legendre nodes uᵢ of [ε², 1] (<see cref="GaussLegendre"/>), ρᵢ = √uᵢ, with weights
/// gᵢ/(1 − ε²) (gᵢ the rule's weights on that interval, summing to 1 − ε²); the N arms at
/// θⱼ = 2πj/N, j = 0 … N − 1, the first on +x, each with weight 1/N. A point's weight is
/// gᵢ/((1 − ε²)N), and the weights sum to 1.
/// </para>
/// <para>
/// <b>Exactness, derived.</b> Write a polynomial f of degree D in (Px, Py) as
/// Σₖ ρ^|k| e^{ikθ} pₖ(ρ²). The N equally spaced arms sum e^{ikθ} exactly to zero for every k that is
/// not a multiple of N, so every k ≠ 0 term with |k| ≤ D vanishes exactly when N > D. The k = 0
/// term p₀(u) has degree ≤ D/2 in u, which n Gauss–Legendre nodes integrate exactly when
/// D/2 ≤ 2n − 1. So the rule is exact for D ≤ min(4n − 1, N − 1) (<see cref="ExactDegree"/>). An RMS
/// integrates a square: with N ≥ 4n arms (the default, <see cref="DefaultArms"/>), the RMS of an
/// OPD or a ray error that is a polynomial of degree ≤ 2n − 1 is exact, which is the help's
/// "(2*n - 1)".
/// </para>
/// <para>
/// <b>Arms</b> (this program's choice): the RMS analyses expose only the ray density, so the
/// number of arms is 4n, the fewest equally spaced arms that keep the angular sum exact to the
/// radial rule's degree. OpticStudio's merit function offers 6 to 12 arms and halves them for
/// symmetric systems; that saving is not built here (every arm is traced).
/// </para>
/// </summary>
public static class PupilQuadrature
{
    /// <summary>The help's largest ray density for Gaussian quadrature ("The maximum number is 20").</summary>
    public const int MaximumRings = 20;

    /// <summary>The number of arms the RMS analyses use with <paramref name="rings"/> rings: 4n.</summary>
    public static int DefaultArms(int rings) => 4 * rings;

    /// <summary>The largest total degree in (Px, Py) of a polynomial the Gaussian rule of
    /// <paramref name="rings"/> rings and <paramref name="arms"/> arms averages exactly:
    /// min(4n − 1, N − 1).</summary>
    public static int ExactDegree(int rings, int arms) => Math.Min(4 * rings - 1, arms - 1);

    /// <summary>
    /// The Gaussian rule of <paramref name="rings"/> rings (1 to <see cref="MaximumRings"/>) by
    /// <paramref name="arms"/> arms (default 4 × rings) over the annulus of linear obscuration
    /// <paramref name="obscuration"/> (0 ≤ ε &lt; 1).
    /// </summary>
    public static PupilQuadratureRule Gaussian(int rings, int? arms = null, double obscuration = 0)
    {
        if (rings < 1 || rings > MaximumRings)
            throw new ArgumentOutOfRangeException(nameof(rings), rings, $"Gaussian quadrature: the rings must lie in 1–{MaximumRings} (it is {rings}).");
        int n = arms ?? DefaultArms(rings);
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(arms), n, $"Gaussian quadrature: at least one arm is needed (it is {n}).");
        if (!(obscuration >= 0 && obscuration < 1))
            throw new ArgumentOutOfRangeException(nameof(obscuration), obscuration, $"Gaussian quadrature: the obscuration must lie in [0, 1) (it is {obscuration}).");
        double low = obscuration * obscuration;
        var (nodes, weights) = GaussLegendre.Rule(rings, low, 1);
        var points = new (double, double)[rings * n];
        var w = new double[rings * n];
        double span = 1 - low;
        for (int i = 0; i < rings; i++)
        {
            double rho = Math.Sqrt(nodes[i]);
            double ringWeight = weights[i] / (span * n);
            for (int j = 0; j < n; j++)
            {
                double angle = 2 * Math.PI * j / n;
                points[i * n + j] = (rho * Math.Cos(angle), rho * Math.Sin(angle));
                w[i * n + j] = ringWeight;
            }
        }
        return new PupilQuadratureRule(PupilIntegration.GaussianQuadrature, points, w, rings, n, obscuration);
    }

    /// <summary>The rectangular array: <see cref="PupilSampling.Square"/>'s cell-centred
    /// n × n grid, the points inside the unit circle, each of weight 1/count.</summary>
    public static PupilQuadratureRule Rectangular(int raysAcross)
    {
        if (raysAcross < 1)
            throw new ArgumentOutOfRangeException(nameof(raysAcross), raysAcross, $"Rectangular array: at least one ray across is needed (it is {raysAcross}).");
        var points = PupilSampling.Square(raysAcross);
        if (points.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(raysAcross), raysAcross, $"Rectangular array: a {raysAcross} × {raysAcross} grid has no point inside the pupil.");
        var w = new double[points.Count];
        Array.Fill(w, 1.0 / points.Count);
        return new PupilQuadratureRule(PupilIntegration.RectangularArray, points, w, raysAcross, 0, 0);
    }

    /// <summary>The rule an RMS analysis uses: Gaussian with <paramref name="density"/> rings and
    /// 4 × rings arms, or the rectangular array <paramref name="density"/> across.</summary>
    public static PupilQuadratureRule For(PupilIntegration method, int density) => method switch
    {
        PupilIntegration.GaussianQuadrature => Gaussian(density),
        PupilIntegration.RectangularArray => Rectangular(density),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown pupil integration method."),
    };
}
