using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>
/// Takes conductors of an extraction out of the port list by tying them to the reference —
/// what a coplanar ground strip, a guard trace stitched at both ends, or a grounded
/// neighbour is. The extraction already has every conductor's own charge and current; a
/// grounded one keeps them and loses only its voltage.
/// <para>
/// With the grounded conductors at 0 V the Maxwell capacitance of the rest is the sub-block
/// (its rows and columns), and the series impedance is the inverse of the sub-block of the
/// series admittance: Z_red = ((Z⁻¹)_ss)⁻¹. For the lossless line that is
/// L_red = µ₀ε₀·(C_air,ss)⁻¹, so the two stay consistent, and with loss it carries the
/// resistance of the return current that flows in the grounded strips.
/// </para>
/// </summary>
public static class RlgcReduction
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;

    public static RlgcResult GroundConductors(RlgcResult full, IReadOnlyCollection<int> grounded)
    {
        ArgumentNullException.ThrowIfNull(full);
        int n = full.ConductorCount;
        var drop = new HashSet<int>(grounded);
        if (drop.Any(i => i < 0 || i >= n))
            throw new ArgumentOutOfRangeException(nameof(grounded), "A grounded conductor index is out of range.");
        var keep = Enumerable.Range(0, n).Where(i => !drop.Contains(i)).ToArray();
        if (keep.Length == 0)
            throw new ArgumentException("Grounding every conductor leaves no line.", nameof(grounded));
        if (keep.Length == n) return full;
        int m = keep.Length;

        double[,] Sub(double[,] a)
        {
            var s = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++) s[i, j] = a[keep[i], keep[j]];
            return s;
        }

        var airSub = Sub(full.AirCapacitanceFaradsPerMeter);
        var external = Real(Invert(ToComplex(airSub)), Mu0 * Epsilon0);

        // Z_red(f) from the full series impedance.
        Complex[,] Reduced(double f)
        {
            double w = 2 * Math.PI * f;
            var r = full.ResistanceMatrixOhmsPerMeter?.Invoke(f);
            var li = full.InternalInductanceHenriesPerMeter?.Invoke(f);
            var z = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double re = r is not null ? r[i, j] : i == j ? full.ResistancePerMeter(i, f) : 0;
                    double l = full.InductanceHenriesPerMeter[i, j] + (li is not null ? li[i, j] : 0);
                    z[i, j] = new Complex(re, w * l);
                }
            var y = Invert(z);
            var ySub = new Complex[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++) ySub[i, j] = y[keep[i], keep[j]];
            return Invert(ySub);
        }

        // Below this the reduction is evaluated at it: at DC the reference plane of the model
        // has no resistance and takes all the return, so the limit is the kept conductors' own.
        const double floorHz = 1.0;
        double[,] Resistance(double f)
        {
            var z = Reduced(Math.Max(f, floorHz));
            var r = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++) r[i, j] = z[i, j].Real;
            return r;
        }
        double[,] InternalInductance(double f)
        {
            double at = Math.Max(f, floorHz);
            var z = Reduced(at);
            var l = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    l[i, j] = z[i, j].Imaginary / (2 * Math.PI * at) - external[i, j];
            return l;
        }

        var assumptions = new List<string>(full.Assumptions)
        {
            $"{drop.Count} of the {n} conductors solved are tied to the reference (0 V along their "
                + "whole length, as stitching vias at a spacing well under a wavelength make them); "
                + "the line's R and L include the return current they carry."
        };
        return new RlgcResult(m,
            Sub(full.CapacitanceFaradsPerMeter), Sub(full.CapacitanceLossFaradsPerMeter), airSub, external,
            keep.Select(i => full.ResistanceDcOhmsPerMeter[i]).ToArray(),
            keep.Select(i => full.SkinResistanceOhmsPerMeterPerSqrtHz[i]).ToArray(),
            assumptions, Resistance, InternalInductance)
        {
            Dielectric = full.Dielectric,
        };
    }

    private static Complex[,] ToComplex(double[,] a)
    {
        int n = a.GetLength(0);
        var c = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) c[i, j] = a[i, j];
        return c;
    }

    private static double[,] Real(Complex[,] a, double scale)
    {
        int n = a.GetLength(0);
        var r = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) r[i, j] = a[i, j].Real * scale;
        return r;
    }

    private static Complex[,] Invert(Complex[,] a)
    {
        int n = a.GetLength(0);
        var matrix = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) matrix[i, j] = a[i, j];
        var lu = ComplexLu.Factor(matrix);
        var inverse = new Complex[n, n];
        for (int k = 0; k < n; k++)
        {
            var rhs = new Complex[n];
            rhs[k] = Complex.One;
            var column = lu.Solve(rhs);
            for (int i = 0; i < n; i++) inverse[i, k] = column[i];
        }
        return inverse;
    }
}
