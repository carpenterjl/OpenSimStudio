namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>
/// OpticStudio's Superconic (OpticStudio 2025 R2 help, "Superconic", after A. Greynolds): the
/// implicit surface A z² − 2B z + C = 0 with s² = x² + y² + z², A = k/R, B = 1 + U1 s² + U2 s⁴
/// + …, C = s²/R + V1 s⁴ + V2 s⁶ + …, up to 120 pairs (U_i, V_i). Written with the curvature
/// c = 1/R so that a plane is c = 0: A = k·c, C = c·s² + Σ V_i s^(2i+2). With every U and V
/// zero it is the conic c(r² + (1 + k)z²) − 2z = 0 of the Standard surface.
/// <para>
/// The sag at r is the root of F(z) = A z² − 2B(q) z + C(q), q = r² + z², reached by damped
/// Newton from the conic's sag (or 0 where the conic has none); the root must lie on the sheet
/// through the vertex, where ∂F/∂z keeps its vertex value's sign (−2). Its slope is
/// dz/dr = −F_r/F_z with F_r = 2r(C′(q) − 2zB′(q)) and F_z = 2Az − 2B + 2z(C′(q) − 2zB′(q))
/// (derived). U_i are in m^(−2i), V_i in m^(−2i−1).
/// </para>
/// </summary>
public sealed class SuperconicSurface : RadialSagShape
{
    private const int NewtonCap = 64;
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;
    private readonly double[] _u;
    private readonly double[] _v;

    public SuperconicSurface(IReadOnlyList<double> u, IReadOnlyList<double> v)
    {
        _u = CheckedCopy(u, "Superconic", "U coefficient", 120);
        _v = CheckedCopy(v, "Superconic", "V coefficient", 120);
        if (_u.Length != _v.Length)
            throw new ArgumentException($"Superconic: {_u.Length} U and {_v.Length} V coefficients; they come in pairs.");
    }

    public IReadOnlyList<double> U => _u;
    public IReadOnlyList<double> V => _v;

    public override string TypeName => "Superconic";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        double c = surface.Curvature, k = surface.Conic;
        if (!ConicSag.TryEvaluate(r, c, k, out z, out dzdr))
        {
            if (_u.Length == 0) return false;
            z = 0;
        }
        if (_u.Length == 0) return true;

        double a = k * c, r2 = r * r;
        if (!Evaluate(a, c, r2, z, out double f, out double fz, out double fq)) return false;
        bool converged = f == 0;
        for (int i = 0; i < NewtonCap && !converged; i++)
        {
            if (!(fz != 0)) return false;
            double step = -f / fz, lambda = 1;
            bool accepted = false;
            for (int h = 0; h < 60; h++, lambda *= 0.5)
            {
                double trial = z + lambda * step;
                if (Evaluate(a, c, r2, trial, out double ft, out double fzt, out double fqt) && Math.Abs(ft) <= Math.Abs(f))
                {
                    double moved = Math.Abs(trial - z);
                    z = trial; f = ft; fz = fzt; fq = fqt;
                    accepted = true;
                    if (f == 0 || moved <= 8 * Eps * Math.Max(Math.Max(Math.Abs(z), r), double.Epsilon)) converged = true;
                    break;
                }
            }
            if (!accepted)
            {
                converged = Math.Abs(step) <= 512 * Eps * Math.Max(Math.Max(Math.Abs(z), r), double.Epsilon);
                break;
            }
        }
        if (!converged || !(fz < 0)) return false;
        // F_r = ∂F/∂q · 2r with ∂F/∂q = C′(q) − 2zB′(q) (fq below).
        dzdr = -(2 * r * fq) / fz;
        return double.IsFinite(z) && double.IsFinite(dzdr);
    }

    /// <summary>F, ∂F/∂z (with q = r² + z² following z) and ∂F/∂q = C′(q) − 2zB′(q).</summary>
    private bool Evaluate(double a, double c, double r2, double z, out double f, out double fz, out double fq)
    {
        double q = r2 + z * z;
        // B(q) = 1 + Σ U_i q^i, B′ = Σ i U_i q^(i−1); C(q) = c q + Σ V_i q^(i+1), C′ = c + Σ (i+1) V_i q^i.
        double bq = 0, dbq = 0, cq = 0, dcq = 0;
        for (int i = _u.Length - 1; i >= 0; i--)
        {
            dbq = dbq * q + bq;
            bq = bq * q + _u[i];
            dcq = dcq * q + cq;
            cq = cq * q + _v[i];
        }
        // Here bq = Σ U_i q^(i−1), dbq its derivative; likewise for V.
        double b = 1 + q * bq, db = bq + q * dbq;
        double cc = c * q + q * q * cq, dc = c + 2 * q * cq + q * q * dcq;
        f = a * z * z - 2 * b * z + cc;
        fq = dc - 2 * z * db;
        fz = 2 * a * z - 2 * b + 2 * z * fq;
        return double.IsFinite(f) && double.IsFinite(fz);
    }
}
