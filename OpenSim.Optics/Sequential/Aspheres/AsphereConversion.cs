using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>The surface a conversion produced and how far its sag is from the original's
/// over the fitted aperture (meters, at the fitting samples).</summary>
public sealed record AsphereFitResult(SequentialSurface Surface, double RmsResidual, double MaxResidual);

/// <summary>
/// Conversion between the Even Asphere and the Q-Type Asphere by a linear least-squares fit of
/// the sag over the aperture 0 ≤ r ≤ ρ (OpticStudio's "Convert Asphere Types" does the same:
/// "a least squares fitting so that the new asphere provides the minimum RMS difference in the
/// surface sag", the optical surface edge being the fitting region and the normalization
/// radius; OpticStudio 2025 R2 help). The fit uses <see cref="HouseholderLeastSquares"/> on
/// equal-area samples u_j = √((j + ½)/n), n = max(256, 32 × terms); the residuals are
/// reported, not hidden.
/// <list type="bullet">
/// <item>To Qcon: the curvature and conic are kept; the departure from that conic is fitted
/// with x²Q^con_m(x). A departure with an r² term (the Even Asphere's α1) cannot be
/// represented and shows in the residual.</item>
/// <item>To Qbfs: the conic constant κ is kept and the curvature becomes that of the conic
/// through the vertex and the edge point (r = ρ, z = f(ρ)): c(ρ² + (1 + κ)f²) = 2f, which is
/// Forbes's best-fit sphere for κ = 0 (Opt. Express 18, 19700, Eq. 4.2); the departure is
/// fitted with (ψ/φ)·x(1 − x)Q^bfs_m(x).</item>
/// <item>To Even Asphere: the curvature and conic are kept; the departure is fitted with r²ⁱ,
/// i = 1…terms (at most 8), on the normalised radius r/ρ to keep the columns well scaled.</item>
/// </list>
/// The source may be the Standard surface or any <see cref="RadialSagShape"/>.
/// </summary>
public static class AsphereConversion
{
    /// <summary>The sag of a rotationally symmetric surface at radius r; throws for a shape
    /// that is not rotationally symmetric or a radius where the sag is not defined.</summary>
    public static double RadialSag(SequentialSurface surface, double r)
    {
        ArgumentNullException.ThrowIfNull(surface);
        double z;
        bool ok = surface.Shape switch
        {
            null => ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out _),
            RadialSagShape radial => radial.TryRadialSag(r, surface, out z, out _),
            _ => throw new ArgumentException(
                $"Asphere conversion: the surface type '{surface.Shape.TypeName}' is not rotationally symmetric; only the Standard surface and the radial asphere forms convert."),
        };
        if (!ok || !double.IsFinite(z))
            throw new InvalidOperationException($"Asphere conversion: the sag is not defined at r = {r} m (outside the conic's square root?).");
        return z;
    }

    /// <summary>Fits a Q-Type Asphere of <paramref name="terms"/> coefficients (1…100) to the
    /// surface over 0 ≤ r ≤ <paramref name="apertureRadius"/>, which is also its normalization
    /// radius.</summary>
    public static AsphereFitResult ToQType(SequentialSurface source, QTypeKind kind, int terms, double apertureRadius)
    {
        CheckArguments(source, terms, 100, apertureRadius, "Q-Type");
        double rho = apertureRadius;
        double c = source.Curvature, k = source.Conic;
        if (kind == QTypeKind.Qbfs)
        {
            double fEdge = RadialSag(source, rho);
            double denominator = rho * rho + (1 + k) * fEdge * fEdge;
            c = denominator == 0 ? 0 : 2 * fEdge / denominator;
            if (!(1 - c * (1 + k) * fEdge > 0))
                throw new InvalidOperationException(
                    $"Asphere conversion to Qbfs: no conic of conic constant {k} passes through the vertex and the edge point (r = {rho} m, z = {fEdge} m) on its vertex sheet.");
        }
        else if (kind != QTypeKind.Qcon)
            throw new ArgumentException($"Asphere conversion: unknown Q type {(int)kind}.");

        var samples = Samples(terms);
        var design = new double[samples.Length, terms];
        var rhs = new double[samples.Length];
        for (int j = 0; j < samples.Length; j++)
        {
            double u = samples[j], x = u * u, r = u * rho;
            if (!ConicSag.TryEvaluate(r, c, k, out double conic, out _))
                throw new InvalidOperationException($"Asphere conversion: the base conic (c = {c} 1/m, k = {k}) is not defined at r = {r} m.");
            rhs[j] = RadialSag(source, r) - conic;
            double factor;
            double[] basis;
            if (kind == QTypeKind.Qcon)
            {
                factor = x * x;
                basis = QPolynomials.ConBasis(terms - 1, x);
            }
            else
            {
                double phi2 = 1 - (1 + k) * c * c * r * r, psi2 = 1 - k * c * c * r * r;
                if (!(phi2 > 0) || !(psi2 > 0))
                    throw new InvalidOperationException($"Asphere conversion to Qbfs: the base conic is not defined at r = {r} m.");
                factor = Math.Sqrt(psi2) / Math.Sqrt(phi2) * x * (1 - x);
                basis = QPolynomials.BfsBasis(terms - 1, x);
            }
            for (int m = 0; m < terms; m++) design[j, m] = factor * basis[m];
        }
        var a = HouseholderLeastSquares.Solve(design, rhs);
        var surface = source with { Curvature = c, Conic = k, Shape = new QTypeAsphere(kind, rho, a) };
        return Residuals(source, surface, samples, rho);
    }

    /// <summary>Fits an Even Asphere of <paramref name="terms"/> coefficients (1…8) to the
    /// surface over 0 ≤ r ≤ <paramref name="apertureRadius"/>, keeping its curvature and conic.</summary>
    public static AsphereFitResult ToEvenAsphere(SequentialSurface source, double apertureRadius, int terms = 8)
    {
        CheckArguments(source, terms, 8, apertureRadius, "Even Asphere");
        double rho = apertureRadius, c = source.Curvature, k = source.Conic;
        var samples = Samples(terms);
        var design = new double[samples.Length, terms];
        var rhs = new double[samples.Length];
        for (int j = 0; j < samples.Length; j++)
        {
            double u = samples[j], x = u * u, r = u * rho;
            if (!ConicSag.TryEvaluate(r, c, k, out double conic, out _))
                throw new InvalidOperationException($"Asphere conversion: the base conic (c = {c} 1/m, k = {k}) is not defined at r = {r} m.");
            rhs[j] = RadialSag(source, r) - conic;
            double power = 1;
            for (int i = 0; i < terms; i++)
            {
                power *= x;
                design[j, i] = power;      // (r/ρ)^(2i + 2)
            }
        }
        var scaled = HouseholderLeastSquares.Solve(design, rhs);
        var alpha = new double[terms];
        for (int i = 0; i < terms; i++) alpha[i] = scaled[i] / Math.Pow(rho, 2 * (i + 1));
        var surface = source with { Shape = new EvenAsphere(alpha) };
        return Residuals(source, surface, samples, rho);
    }

    private static void CheckArguments(SequentialSurface source, int terms, int maximum, double apertureRadius, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (terms < 1 || terms > maximum)
            throw new ArgumentOutOfRangeException(nameof(terms), terms, $"Asphere conversion to {target}: 1 to {maximum} terms.");
        if (!(apertureRadius > 0) || !double.IsFinite(apertureRadius))
            throw new ArgumentException($"Asphere conversion to {target}: the aperture radius is {apertureRadius}; it must be a positive length.");
    }

    private static double[] Samples(int terms)
    {
        int n = Math.Max(256, 32 * terms);
        var u = new double[n];
        for (int j = 0; j < n; j++) u[j] = Math.Sqrt((j + 0.5) / n);
        return u;
    }

    private static AsphereFitResult Residuals(SequentialSurface source, SequentialSurface fitted, double[] samples, double rho)
    {
        double sum = 0, max = 0;
        foreach (double u in samples)
        {
            double d = RadialSag(fitted, u * rho) - RadialSag(source, u * rho);
            sum += d * d;
            max = Math.Max(max, Math.Abs(d));
        }
        return new AsphereFitResult(fitted, Math.Sqrt(sum / samples.Length), max);
    }
}
