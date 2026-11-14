namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>
/// OpticStudio's Periodic surface (OpticStudio 2025 R2 help, "Periodic"):
/// z = conic − A·[¼(1 + cos 2παx)(1 + cos 2πβy) − 1], A the peak-to-valley height of the
/// modulation (meters) and α, β the spatial frequencies in x and y (1/m). The sag is zero at
/// the vertex. Slopes (derived): ∂z/∂x = conic_x + A·(πα/2)·sin(2παx)(1 + cos 2πβy), and
/// likewise in y.
/// </summary>
public sealed class PeriodicSurface : SagSurfaceShape
{
    public PeriodicSurface(double amplitude, double frequencyX, double frequencyY)
    {
        if (!double.IsFinite(amplitude) || !double.IsFinite(frequencyX) || !double.IsFinite(frequencyY))
            throw new ArgumentException($"Periodic: A = {amplitude}, α = {frequencyX}, β = {frequencyY}; all must be finite.");
        Amplitude = amplitude;
        FrequencyX = frequencyX;
        FrequencyY = frequencyY;
    }

    /// <summary>A, meters (peak to valley).</summary>
    public double Amplitude { get; }

    /// <summary>α, 1/m.</summary>
    public double FrequencyX { get; }

    /// <summary>β, 1/m.</summary>
    public double FrequencyY { get; }

    public override string TypeName => "Periodic";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        double r = Math.Sqrt(x * x + y * y);
        dzdx = dzdy = 0;
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr)) return false;
        if (r > 0)
        {
            dzdx = dzdr * (x / r);
            dzdy = dzdr * (y / r);
        }
        double ax = 2 * Math.PI * FrequencyX * x, by = 2 * Math.PI * FrequencyY * y;
        double cx = 1 + Math.Cos(ax), cy = 1 + Math.Cos(by);
        z -= Amplitude * (0.25 * cx * cy - 1);
        dzdx += Amplitude * (Math.PI * FrequencyX / 2) * Math.Sin(ax) * cy;
        dzdy += Amplitude * (Math.PI * FrequencyY / 2) * Math.Sin(by) * cx;
        return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy);
    }

    /// <summary>
    /// The y–z vertex curvature c + 2π²Aβ² (TC-6; the paraxial engine traces the y meridian,
    /// <see cref="SurfaceShape.ParaxialEquivalent"/>). Derived from the sag of "Periodic": on
    /// x = 0, ¼(1 + 1)(1 + cos 2πβy) − 1 = −(1 − cos 2πβy)/2, so the modulation adds
    /// (A/2)(1 − cos 2πβy) = (A/2)(2πβy)²/2 + O(y⁴) = π²Aβ²y² + O(y⁴), whose second derivative at
    /// 0 is 2π²Aβ². The x–z curvature, c + 2π²Aα², is not passed on: the surface is anamorphic
    /// when α ≠ β. This is the curvature at the vertex only; for a modulation whose period is
    /// short against the beam, first-order data describe a region far smaller than the beam.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new()
        {
            Curvature = ParaxialCurvature.Add(surface.Curvature,
                2 * Math.PI * Math.PI * Amplitude * FrequencyY * FrequencyY),
        };
}

/// <summary>One cosine term A·s^P·cos(B·θ + C) of the <see cref="OddCosineSurface"/>.</summary>
/// <param name="A">Amplitude, meters.</param>
/// <param name="P">Power of the normalised radius s (dimensionless).</param>
/// <param name="B">Angular frequency (dimensionless).</param>
/// <param name="C">Phase, radians.</param>
public readonly record struct OddCosineTerm(double A, double P, double B, double C);

/// <summary>
/// OpticStudio's Odd Cosine (OpticStudio 2025 R2 help, "Odd Cosine"):
/// z = conic + Σ_{i=1}^{16} β_i r^i + Σ_{i=1}^{m} A_i s^(P_i) cos(B_i θ + C_i), with s = r/R,
/// R the normalization radius ("If R is zero or negative, the cosine terms are ignored"), m from
/// 0 to 6, and θ the polar angle with tan θ = y/x (taken as atan2(y, x)). β_i in m^(1 − i).
/// <para>
/// Slopes (derived): with ∂/∂r = A P s^P/r·cos(·) and ∂/∂θ = −A B s^P sin(·),
/// ∂z/∂x = ∂_r·x/r − ∂_θ·y/r², ∂z/∂y = ∂_r·y/r + ∂_θ·x/r². On the axis the slopes of the
/// odd and cosine terms are taken as zero (their gradient has no limit there in general).
/// </para>
/// </summary>
public sealed class OddCosineSurface : SagSurfaceShape
{
    private readonly double[] _beta;
    private readonly OddCosineTerm[] _terms;

    public OddCosineSurface(IReadOnlyList<double> beta, double normalizationRadius, IReadOnlyList<OddCosineTerm> cosineTerms)
    {
        _beta = CheckedCopy(beta, "Odd Cosine", "odd coefficient", 16);
        ArgumentNullException.ThrowIfNull(cosineTerms);
        if (cosineTerms.Count > 6)
            throw new ArgumentException($"Odd Cosine: {cosineTerms.Count} cosine terms; the number must be between 0 and 6.");
        if (!double.IsFinite(normalizationRadius))
            throw new ArgumentException($"Odd Cosine: the normalization radius is {normalizationRadius}; it must be finite.");
        foreach (var t in cosineTerms)
            if (!double.IsFinite(t.A) || !double.IsFinite(t.P) || !double.IsFinite(t.B) || !double.IsFinite(t.C))
                throw new ArgumentException($"Odd Cosine: cosine term {t} has a value that is not finite.");
        NormalizationRadius = normalizationRadius;
        _terms = cosineTerms.ToArray();
    }

    public IReadOnlyList<double> Beta => _beta;

    /// <summary>R, meters; zero or negative switches the cosine terms off.</summary>
    public double NormalizationRadius { get; }

    public IReadOnlyList<OddCosineTerm> CosineTerms => _terms;

    public override string TypeName => "Odd Cosine";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        double r = Math.Sqrt(x * x + y * y);
        dzdx = dzdy = 0;
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr)) return false;
        PowerSeries.AllPowersInR(_beta, r, out double p, out double dp);
        z += p;
        dzdr += dp;
        if (r > 0)
        {
            dzdx = dzdr * (x / r);
            dzdy = dzdr * (y / r);
        }
        if (NormalizationRadius > 0)
        {
            double s = r / NormalizationRadius;
            double theta = Math.Atan2(y, x);
            foreach (var t in _terms)
            {
                double sp = Math.Pow(s, t.P);
                double angle = t.B * theta + t.C;
                z += t.A * sp * Math.Cos(angle);
                if (r > 0)
                {
                    double dr = t.A * t.P * sp / r * Math.Cos(angle);
                    double dtheta = -t.A * t.B * sp * Math.Sin(angle);
                    dzdx += dr * x / r - dtheta * y / (r * r);
                    dzdy += dr * y / r + dtheta * x / (r * r);
                }
            }
        }
        return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy);
    }

    /// <summary>
    /// The y–z vertex curvature (TC-6), derived from the sag of "Odd Cosine" term by term:
    /// <list type="bullet">
    /// <item>The conic and Σ βᵢ rⁱ give c + 2β2, the limit of d²z/dr² as r → 0⁺; β1 r is a cone
    /// and is left out, as for <see cref="OddAsphere.ParaxialEquivalent"/>.</item>
    /// <item>A cosine term A s^P cos(Bθ + C), s = r/R, R &gt; 0, is A (|y|/R)^P cos(Bθ± + C) on
    /// the two halves of the y meridian, θ± = atan2(±1, 0) = ±π/2. Its second derivative there
    /// is A P(P − 1)|y|^(P−2)/R^P · cos(Bθ± + C). As y → 0 this tends to 2A cos(Bθ± + C)/R² for
    /// P = 2, to 0 for P &gt; 2 and for P = 1 (a cone, left out like β1) and P = 0 (a step in
    /// the sag at the vertex, no curvature), and has no finite limit for 0 &lt; P &lt; 2 with
    /// P ≠ 1 or for P &lt; 0.</item>
    /// <item>When B is not even the two halves differ (cos(Bπ/2 + C) ≠ cos(−Bπ/2 + C)), and the
    /// paraxial engine, which assumes a surface symmetric about the axis, is given their mean:
    /// the part of the sag even in y, which is also what a central second difference of the sag
    /// across the vertex measures.</item>
    /// </list>
    /// A non-zero cosine term whose curvature has no finite limit at the vertex is refused with
    /// an <see cref="ArgumentException"/>: the surface has no paraxial curvature there, so
    /// first-order data (and the real trace, which is launched from them) are not defined.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        double delta = 2 * ParaxialCurvature.Term(_beta, 1);
        if (NormalizationRadius > 0)
        {
            double up = Math.Atan2(1.0, 0.0), down = Math.Atan2(-1.0, 0.0);
            for (int i = 0; i < _terms.Length; i++)
            {
                var t = _terms[i];
                if (t.A == 0) continue;
                if (t.P < 0 || (t.P > 0 && t.P < 2 && t.P != 1))
                    throw new ArgumentException(
                        $"Odd Cosine: cosine term {i + 1} has the power P = {t.P}; its curvature A·P(P − 1)·s^(P − 2)/R² has no finite value at the vertex " +
                        "for 0 < P < 2 (P ≠ 1) or P < 0, so the surface has no paraxial curvature and first-order data are not defined. " +
                        "Use P = 0, 1, 2 or more than 2, or set its amplitude A to zero.");
                if (t.P != 2) continue;
                double mean = 0.5 * (Math.Cos(t.B * up + t.C) + Math.Cos(t.B * down + t.C));
                delta += 2 * t.A * mean / (NormalizationRadius * NormalizationRadius);
            }
        }
        return new() { Curvature = ParaxialCurvature.Add(surface.Curvature, delta) };
    }
}
