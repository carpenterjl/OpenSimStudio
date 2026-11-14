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
}
