namespace OpenSim.Tests.Core;

/// <summary>
/// The eighteen nonlinear least-squares problems of MINPACK's LMDER test, numbered as there.
/// Residuals, data and standard starting points are transcribed from subroutines SSQFCN and INITPT
/// of MINPACK's test driver (netlib.org/minpack/ex/file17, Garbow, Hillstrom and Moré, Argonne,
/// 1980). The documented minima are from J. J. Moré, B. S. Garbow and K. E. Hillstrom, "Testing
/// unconstrained optimization software", Argonne report ANL-80-74 (1980), section 3 (published as
/// ACM TOMS 7(1), 17–41, 1981), where the problems are listed in a different order.
/// </summary>
internal static class MghProblems
{
    public static string Name(int problem) => problem switch
    {
        1 => "Linear, full rank", 2 => "Linear, rank 1", 3 => "Linear, rank 1 with zero columns and rows",
        4 => "Rosenbrock", 5 => "Helical valley", 6 => "Powell singular", 7 => "Freudenstein and Roth",
        8 => "Bard", 9 => "Kowalik and Osborne", 10 => "Meyer", 11 => "Watson", 12 => "Box 3-dimensional",
        13 => "Jennrich and Sampson", 14 => "Brown and Dennis", 15 => "Chebyquad", 16 => "Brown almost-linear",
        17 => "Osborne 1", 18 => "Osborne 2",
        _ => throw new ArgumentOutOfRangeException(nameof(problem))
    };

    // SSQFCN data statements.
    private static readonly double[] V = { 4.0, 2.0, 1.0, 5.0e-1, 2.5e-1, 1.67e-1, 1.25e-1, 1.0e-1, 8.33e-2, 7.14e-2, 6.25e-2 };
    private static readonly double[] Y1 =
        { 1.4e-1, 1.8e-1, 2.2e-1, 2.5e-1, 2.9e-1, 3.2e-1, 3.5e-1, 3.9e-1, 3.7e-1, 5.8e-1, 7.3e-1, 9.6e-1, 1.34, 2.1, 4.39 };
    private static readonly double[] Y2 =
        { 1.957e-1, 1.947e-1, 1.735e-1, 1.6e-1, 8.44e-2, 6.27e-2, 4.56e-2, 3.42e-2, 3.23e-2, 2.35e-2, 2.46e-2 };
    private static readonly double[] Y3 =
    {
        3.478e4, 2.861e4, 2.365e4, 1.963e4, 1.637e4, 1.372e4, 1.154e4, 9.744e3, 8.261e3, 7.03e3, 6.005e3, 5.147e3,
        4.427e3, 3.82e3, 3.307e3, 2.872e3
    };
    private static readonly double[] Y4 =
    {
        8.44e-1, 9.08e-1, 9.32e-1, 9.36e-1, 9.25e-1, 9.08e-1, 8.81e-1, 8.5e-1, 8.18e-1, 7.84e-1, 7.51e-1, 7.18e-1,
        6.85e-1, 6.58e-1, 6.28e-1, 6.03e-1, 5.8e-1, 5.58e-1, 5.38e-1, 5.22e-1, 5.06e-1, 4.9e-1, 4.78e-1, 4.67e-1,
        4.57e-1, 4.48e-1, 4.38e-1, 4.31e-1, 4.24e-1, 4.2e-1, 4.14e-1, 4.11e-1, 4.06e-1
    };
    private static readonly double[] Y5 =
    {
        1.366, 1.191, 1.112, 1.013, 9.91e-1, 8.85e-1, 8.31e-1, 8.47e-1, 7.86e-1, 7.25e-1, 7.46e-1, 6.79e-1, 6.08e-1,
        6.55e-1, 6.16e-1, 6.06e-1, 6.02e-1, 6.26e-1, 6.51e-1, 7.24e-1, 6.49e-1, 6.49e-1, 6.94e-1, 6.44e-1, 6.24e-1,
        6.61e-1, 6.12e-1, 5.58e-1, 5.33e-1, 4.95e-1, 5.0e-1, 4.23e-1, 3.95e-1, 3.75e-1, 3.72e-1, 3.91e-1, 3.96e-1,
        4.05e-1, 4.28e-1, 4.29e-1, 5.23e-1, 5.62e-1, 6.07e-1, 6.53e-1, 6.72e-1, 7.08e-1, 6.33e-1, 6.68e-1, 6.45e-1,
        6.32e-1, 5.91e-1, 5.59e-1, 5.97e-1, 6.25e-1, 7.39e-1, 7.1e-1, 7.29e-1, 7.2e-1, 6.36e-1, 5.81e-1, 4.28e-1,
        2.92e-1, 1.62e-1, 9.8e-2, 5.4e-2
    };

    /// <summary>INITPT with FACTOR = 1.</summary>
    public static double[] Start(int problem, int n)
    {
        var x = new double[n];
        switch (problem)
        {
            case 1: case 2: case 3: Array.Fill(x, 1.0); break;
            case 4: x[0] = -1.2; x[1] = 1; break;
            case 5: x[0] = -1; break;
            case 6: x[0] = 3; x[1] = -1; x[2] = 0; x[3] = 1; break;
            case 7: x[0] = 0.5; x[1] = -2; break;
            case 8: x[0] = 1; x[1] = 1; x[2] = 1; break;
            case 9: x[0] = 0.25; x[1] = 0.39; x[2] = 0.415; x[3] = 0.39; break;
            case 10: x[0] = 0.02; x[1] = 4000; x[2] = 250; break;
            case 11: break;
            case 12: x[0] = 0; x[1] = 10; x[2] = 20; break;
            case 13: x[0] = 0.3; x[1] = 0.4; break;
            case 14: x[0] = 25; x[1] = 5; x[2] = -5; x[3] = -1; break;
            case 15:
                for (int j = 0; j < n; j++) x[j] = (j + 1) / (double)(n + 1);
                break;
            case 16: Array.Fill(x, 0.5); break;
            case 17: x[0] = 0.5; x[1] = 1.5; x[2] = -1; x[3] = 0.01; x[4] = 0.02; break;
            case 18:
                double[] start = { 1.3, 0.65, 0.65, 0.7, 0.6, 3, 5, 7, 2, 4.5, 5.5 };
                Array.Copy(start, x, 11);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
        return x;
    }

    /// <summary>SSQFCN: the m residuals of problem <paramref name="problem"/> at x.</summary>
    public static void Residuals(int problem, double[] x, double[] f)
    {
        int n = x.Length, m = f.Length;
        switch (problem)
        {
            case 1:
            {
                double sum = 0;
                for (int j = 0; j < n; j++) sum += x[j];
                double temp = 2 * sum / m + 1;
                for (int i = 0; i < m; i++) f[i] = -temp + (i < n ? x[i] : 0);
                break;
            }
            case 2:
            {
                double sum = 0;
                for (int j = 0; j < n; j++) sum += (j + 1) * x[j];
                for (int i = 0; i < m; i++) f[i] = (i + 1) * sum - 1;
                break;
            }
            case 3:
            {
                double sum = 0;
                for (int j = 2; j <= n - 1; j++) sum += j * x[j - 1];
                for (int i = 0; i < m; i++) f[i] = i * sum - 1;
                f[m - 1] = -1;
                break;
            }
            case 4:
                f[0] = 10 * (x[1] - x[0] * x[0]);
                f[1] = 1 - x[0];
                break;
            case 5:
            {
                double tpi = 8 * Math.Atan(1.0);
                double tmp1 = x[1] >= 0 ? 0.25 : -0.25; // DSIGN(0.25, X(2))
                if (x[0] > 0) tmp1 = Math.Atan(x[1] / x[0]) / tpi;
                if (x[0] < 0) tmp1 = Math.Atan(x[1] / x[0]) / tpi + 0.5;
                double tmp2 = Math.Sqrt(x[0] * x[0] + x[1] * x[1]);
                f[0] = 10 * (x[2] - 10 * tmp1);
                f[1] = 10 * (tmp2 - 1);
                f[2] = x[2];
                break;
            }
            case 6:
                f[0] = x[0] + 10 * x[1];
                f[1] = Math.Sqrt(5.0) * (x[2] - x[3]);
                f[2] = (x[1] - 2 * x[2]) * (x[1] - 2 * x[2]);
                f[3] = Math.Sqrt(10.0) * (x[0] - x[3]) * (x[0] - x[3]);
                break;
            case 7:
                f[0] = -13 + x[0] + ((5 - x[1]) * x[1] - 2) * x[1];
                f[1] = -29 + x[0] + ((1 + x[1]) * x[1] - 14) * x[1];
                break;
            case 8:
                for (int i = 1; i <= 15; i++)
                {
                    double tmp1 = i, tmp2 = 16 - i, tmp3 = i > 8 ? tmp2 : tmp1;
                    f[i - 1] = Y1[i - 1] - (x[0] + tmp1 / (x[1] * tmp2 + x[2] * tmp3));
                }
                break;
            case 9:
                for (int i = 0; i < 11; i++)
                {
                    double tmp1 = V[i] * (V[i] + x[1]);
                    double tmp2 = V[i] * (V[i] + x[2]) + x[3];
                    f[i] = Y2[i] - x[0] * tmp1 / tmp2;
                }
                break;
            case 10:
                for (int i = 1; i <= 16; i++)
                {
                    double temp = 5.0 * i + 45 + x[2];
                    f[i - 1] = x[0] * Math.Exp(x[1] / temp) - Y3[i - 1];
                }
                break;
            case 11:
                for (int i = 1; i <= 29; i++)
                {
                    double div = i / 29.0;
                    double s1 = 0, dx = 1;
                    for (int j = 2; j <= n; j++)
                    {
                        s1 += (j - 1) * dx * x[j - 1];
                        dx *= div;
                    }
                    double s2 = 0;
                    dx = 1;
                    for (int j = 1; j <= n; j++)
                    {
                        s2 += dx * x[j - 1];
                        dx *= div;
                    }
                    f[i - 1] = s1 - s2 * s2 - 1;
                }
                f[29] = x[0];
                f[30] = x[1] - x[0] * x[0] - 1;
                break;
            case 12:
                for (int i = 1; i <= m; i++)
                {
                    double temp = i, tmp1 = temp / 10;
                    f[i - 1] = Math.Exp(-tmp1 * x[0]) - Math.Exp(-tmp1 * x[1]) + (Math.Exp(-temp) - Math.Exp(-tmp1)) * x[2];
                }
                break;
            case 13:
                for (int i = 1; i <= m; i++)
                    f[i - 1] = 2 + 2.0 * i - Math.Exp(i * x[0]) - Math.Exp(i * x[1]);
                break;
            case 14:
                for (int i = 1; i <= m; i++)
                {
                    double temp = i / 5.0;
                    double tmp1 = x[0] + temp * x[1] - Math.Exp(temp);
                    double tmp2 = x[2] + Math.Sin(temp) * x[3] - Math.Cos(temp);
                    f[i - 1] = tmp1 * tmp1 + tmp2 * tmp2;
                }
                break;
            case 15:
            {
                Array.Clear(f);
                for (int j = 0; j < n; j++)
                {
                    double tmp1 = 1, tmp2 = 2 * x[j] - 1, temp = 2 * tmp2;
                    for (int i = 0; i < m; i++)
                    {
                        f[i] += tmp2;
                        double ti = temp * tmp2 - tmp1;
                        tmp1 = tmp2;
                        tmp2 = ti;
                    }
                }
                double scale = 1.0 / n;
                int iev = -1;
                for (int i = 1; i <= m; i++)
                {
                    f[i - 1] *= scale;
                    if (iev > 0) f[i - 1] += 1.0 / ((double)i * i - 1);
                    iev = -iev;
                }
                break;
            }
            case 16:
            {
                double sum = -(n + 1.0), prod = 1;
                for (int j = 0; j < n; j++)
                {
                    sum += x[j];
                    prod *= x[j];
                }
                for (int i = 0; i < n; i++) f[i] = x[i] + sum;
                f[n - 1] = prod - 1;
                break;
            }
            case 17:
                for (int i = 1; i <= 33; i++)
                {
                    double temp = 10.0 * (i - 1);
                    f[i - 1] = Y4[i - 1] - (x[0] + x[1] * Math.Exp(-x[3] * temp) + x[2] * Math.Exp(-x[4] * temp));
                }
                break;
            case 18:
                for (int i = 1; i <= 65; i++)
                {
                    double temp = (i - 1) / 10.0;
                    double tmp1 = Math.Exp(-x[4] * temp);
                    double tmp2 = Math.Exp(-x[5] * (temp - x[8]) * (temp - x[8]));
                    double tmp3 = Math.Exp(-x[6] * (temp - x[9]) * (temp - x[9]));
                    double tmp4 = Math.Exp(-x[7] * (temp - x[10]) * (temp - x[10]));
                    f[i - 1] = Y5[i - 1] - (x[0] * tmp1 + x[1] * tmp2 + x[2] * tmp3 + x[3] * tmp4);
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
    }
}
