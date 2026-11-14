using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-33: the Zernike terms against the published tables, their orthonormality by quadrature,
/// the radial recurrence against the exact factorial sum, the derivatives, and the index maps.
/// <para>
/// Sources (each table below is typed from these, never from memory):
/// <list type="bullet">
/// <item>Wyant and Creath, "Basic Wavefront Aberration Theory for Optical Metrology", Applied
/// Optics and Optical Engineering XI (1992), Table III "Zernike Radial Polynomials", numbers
/// 0–36 (the PDF Wyant hosts at wp.optics.arizona.edu/jcwyant/…/Zernikes.pdf). Table IV of the
/// same chapter names term 1 "x-tilt", so θ is measured from +x.</item>
/// <item>Ansys OpticStudio 2026 R1 Help: "Zernike Fringe Coefficients" (37 terms), "Zernike
/// Standard Coefficients" (terms 1–28; cites Noll, JOSA 66, 207, 1976), "Zernike Annular
/// Coefficients" (terms 1–22; cites Mahajan, JOSA 71, 75, 1981). Each says "the angle φ is
/// measured counter clockwise from the local +x axis".</item>
/// <item>Mahajan, "Zernike annular polynomials and optical aberrations of systems with annular
/// pupils", Applied Optics 33, 8125 (1994), Table 1 "Zernike radial annular polynomials"
/// R_n^m(ρ; ε), as shown on the publisher's abstract page.</item>
/// <item>"Zernike polynomials", Wikipedia: the first 20 Noll and Fringe indices.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ZernikeTests
{
    private static readonly (double X, double Y)[] SamplePoints =
    {
        (0, 0), (0.3, 0), (0, -0.45), (0.21, 0.37), (-0.62, 0.18), (-0.4, -0.71),
        (0.55, -0.55), (0.98, 0.1), (Math.Cos(2.2), Math.Sin(2.2)), (-0.05, 0.02),
    };

    private static void ForEachPoint(Action<double, double, double, double> check)
    {
        foreach (var (x, y) in SamplePoints)
            check(x, y, Math.Sqrt(x * x + y * y), Math.Atan2(y, x));
    }

    // ---- Every term against its published expression ----

    /// <summary>
    /// Wyant and Creath (1992) Table III, numbers 0–36, as Fringe 1–37. OpticStudio's Fringe
    /// help table agrees term by term except its term 30, printed "21ρ⁴ − 30ρ⁴ + 10": Wyant and
    /// Creath's number 29 has 21ρ⁴ − 30ρ² + 10, the only form that is a Zernike radial polynomial
    /// (R_7^3), so the help's ρ⁴ is a misprint and Wyant and Creath is used.
    /// </summary>
    [Fact]
    public void Fringe_EveryTermIsWyantCreathTableIII()
    {
        var table = new Func<double, double, double>[]
        {
            (r, t) => 1,                                                                    // 0
            (r, t) => r * Math.Cos(t),                                                      // 1
            (r, t) => r * Math.Sin(t),                                                      // 2
            (r, t) => 2 * r * r - 1,                                                        // 3
            (r, t) => r * r * Math.Cos(2 * t),                                              // 4
            (r, t) => r * r * Math.Sin(2 * t),                                              // 5
            (r, t) => (3 * r * r - 2) * r * Math.Cos(t),                                    // 6
            (r, t) => (3 * r * r - 2) * r * Math.Sin(t),                                    // 7
            (r, t) => 6 * P(r, 4) - 6 * r * r + 1,                                          // 8
            (r, t) => P(r, 3) * Math.Cos(3 * t),                                            // 9
            (r, t) => P(r, 3) * Math.Sin(3 * t),                                            // 10
            (r, t) => (4 * r * r - 3) * r * r * Math.Cos(2 * t),                            // 11
            (r, t) => (4 * r * r - 3) * r * r * Math.Sin(2 * t),                            // 12
            (r, t) => (10 * P(r, 4) - 12 * r * r + 3) * r * Math.Cos(t),                    // 13
            (r, t) => (10 * P(r, 4) - 12 * r * r + 3) * r * Math.Sin(t),                    // 14
            (r, t) => 20 * P(r, 6) - 30 * P(r, 4) + 12 * r * r - 1,                         // 15
            (r, t) => P(r, 4) * Math.Cos(4 * t),                                            // 16
            (r, t) => P(r, 4) * Math.Sin(4 * t),                                            // 17
            (r, t) => (5 * r * r - 4) * P(r, 3) * Math.Cos(3 * t),                          // 18
            (r, t) => (5 * r * r - 4) * P(r, 3) * Math.Sin(3 * t),                          // 19
            (r, t) => (15 * P(r, 4) - 20 * r * r + 6) * r * r * Math.Cos(2 * t),            // 20
            (r, t) => (15 * P(r, 4) - 20 * r * r + 6) * r * r * Math.Sin(2 * t),            // 21
            (r, t) => (35 * P(r, 6) - 60 * P(r, 4) + 30 * r * r - 4) * r * Math.Cos(t),     // 22
            (r, t) => (35 * P(r, 6) - 60 * P(r, 4) + 30 * r * r - 4) * r * Math.Sin(t),     // 23
            (r, t) => 70 * P(r, 8) - 140 * P(r, 6) + 90 * P(r, 4) - 20 * r * r + 1,         // 24
            (r, t) => P(r, 5) * Math.Cos(5 * t),                                            // 25
            (r, t) => P(r, 5) * Math.Sin(5 * t),                                            // 26
            (r, t) => (6 * r * r - 5) * P(r, 4) * Math.Cos(4 * t),                          // 27
            (r, t) => (6 * r * r - 5) * P(r, 4) * Math.Sin(4 * t),                          // 28
            (r, t) => (21 * P(r, 4) - 30 * r * r + 10) * P(r, 3) * Math.Cos(3 * t),         // 29
            (r, t) => (21 * P(r, 4) - 30 * r * r + 10) * P(r, 3) * Math.Sin(3 * t),         // 30
            (r, t) => (56 * P(r, 6) - 105 * P(r, 4) + 60 * r * r - 10) * r * r * Math.Cos(2 * t), // 31
            (r, t) => (56 * P(r, 6) - 105 * P(r, 4) + 60 * r * r - 10) * r * r * Math.Sin(2 * t), // 32
            (r, t) => (126 * P(r, 8) - 280 * P(r, 6) + 210 * P(r, 4) - 60 * r * r + 5) * r * Math.Cos(t), // 33
            (r, t) => (126 * P(r, 8) - 280 * P(r, 6) + 210 * P(r, 4) - 60 * r * r + 5) * r * Math.Sin(t), // 34
            (r, t) => 252 * P(r, 10) - 630 * P(r, 8) + 560 * P(r, 6) - 210 * P(r, 4) + 30 * r * r - 1, // 35
            (r, t) => 924 * P(r, 12) - 2772 * P(r, 10) + 3150 * P(r, 8) - 1680 * P(r, 6)
                      + 420 * P(r, 4) - 42 * r * r + 1,                                     // 36
        };
        Assert.Equal(ZernikeIndex.FringeTermCount, table.Length);
        var basis = new ZernikeBasis(ZernikeOrdering.Fringe, 37);
        for (int j = 1; j <= 37; j++)
            ForEachPoint((x, y, r, t) =>
                AssertNear(table[j - 1](r, t), basis.Evaluate(j, x, y), 1e-11, $"Fringe {j} (Wyant–Creath {j - 1}) at ({x}, {y})"));
    }

    /// <summary>OpticStudio "Zernike Standard Coefficients" table, terms 1–28 (Noll's order and
    /// normalisation; even index cos, odd index sin).</summary>
    [Fact]
    public void Standard_First28AreOpticStudioNollTable()
    {
        var table = new Func<double, double, double>[]
        {
            (r, t) => 1,
            (r, t) => Math.Sqrt(4) * r * Math.Cos(t),
            (r, t) => Math.Sqrt(4) * r * Math.Sin(t),
            (r, t) => Math.Sqrt(3) * (2 * r * r - 1),
            (r, t) => Math.Sqrt(6) * r * r * Math.Sin(2 * t),
            (r, t) => Math.Sqrt(6) * r * r * Math.Cos(2 * t),
            (r, t) => Math.Sqrt(8) * (3 * P(r, 3) - 2 * r) * Math.Sin(t),
            (r, t) => Math.Sqrt(8) * (3 * P(r, 3) - 2 * r) * Math.Cos(t),
            (r, t) => Math.Sqrt(8) * P(r, 3) * Math.Sin(3 * t),
            (r, t) => Math.Sqrt(8) * P(r, 3) * Math.Cos(3 * t),
            (r, t) => Math.Sqrt(5) * (6 * P(r, 4) - 6 * r * r + 1),
            (r, t) => Math.Sqrt(10) * (4 * P(r, 4) - 3 * r * r) * Math.Cos(2 * t),
            (r, t) => Math.Sqrt(10) * (4 * P(r, 4) - 3 * r * r) * Math.Sin(2 * t),
            (r, t) => Math.Sqrt(10) * P(r, 4) * Math.Cos(4 * t),
            (r, t) => Math.Sqrt(10) * P(r, 4) * Math.Sin(4 * t),
            (r, t) => Math.Sqrt(12) * (10 * P(r, 5) - 12 * P(r, 3) + 3 * r) * Math.Cos(t),
            (r, t) => Math.Sqrt(12) * (10 * P(r, 5) - 12 * P(r, 3) + 3 * r) * Math.Sin(t),
            (r, t) => Math.Sqrt(12) * (5 * P(r, 5) - 4 * P(r, 3)) * Math.Cos(3 * t),
            (r, t) => Math.Sqrt(12) * (5 * P(r, 5) - 4 * P(r, 3)) * Math.Sin(3 * t),
            (r, t) => Math.Sqrt(12) * P(r, 5) * Math.Cos(5 * t),
            (r, t) => Math.Sqrt(12) * P(r, 5) * Math.Sin(5 * t),
            (r, t) => Math.Sqrt(7) * (20 * P(r, 6) - 30 * P(r, 4) + 12 * r * r - 1),
            (r, t) => Math.Sqrt(14) * (15 * P(r, 6) - 20 * P(r, 4) + 6 * r * r) * Math.Sin(2 * t),
            (r, t) => Math.Sqrt(14) * (15 * P(r, 6) - 20 * P(r, 4) + 6 * r * r) * Math.Cos(2 * t),
            (r, t) => Math.Sqrt(14) * (6 * P(r, 6) - 5 * P(r, 4)) * Math.Sin(4 * t),
            (r, t) => Math.Sqrt(14) * (6 * P(r, 6) - 5 * P(r, 4)) * Math.Cos(4 * t),
            (r, t) => Math.Sqrt(14) * P(r, 6) * Math.Sin(6 * t),
            (r, t) => Math.Sqrt(14) * P(r, 6) * Math.Cos(6 * t),
        };
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 28);
        for (int j = 1; j <= 28; j++)
            ForEachPoint((x, y, r, t) =>
                AssertNear(table[j - 1](r, t), basis.Evaluate(j, x, y), 1e-12, $"Standard {j} at ({x}, {y})"));
    }

    /// <summary>
    /// OpticStudio "Zernike Annular Coefficients" table, the terms whose formulas are legible in
    /// the help's text: 1–11, 14, 15 and 22. (Terms 12, 13 and 16–21 are images there; their
    /// radial parts R_4^2, R_5^1, R_5^3 and R_5^5 are checked against Mahajan's Table 1 below.)
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void Annular_TermsAreOpticStudioMahajanTable(double e)
    {
        double e2 = e * e, e4 = e2 * e2, e6 = e4 * e2, e8 = e6 * e2;
        var table = new Dictionary<int, Func<double, double, double>>
        {
            [1] = (r, t) => 1,
            [2] = (r, t) => 2 * r * Math.Cos(t) / Math.Sqrt(1 + e2),
            [3] = (r, t) => 2 * r * Math.Sin(t) / Math.Sqrt(1 + e2),
            [4] = (r, t) => Math.Sqrt(3) * (1 + e2 - 2 * r * r) / (e2 - 1),
            [5] = (r, t) => Math.Sqrt(6) * r * r * Math.Sin(2 * t) / Math.Sqrt(1 + e2 + e4),
            [6] = (r, t) => Math.Sqrt(6) * r * r * Math.Cos(2 * t) / Math.Sqrt(1 + e2 + e4),
            [7] = (r, t) => 2 * Math.Sqrt(2) * r * (-2 - 2 * e4 + 3 * r * r + e2 * (-2 + 3 * r * r)) * Math.Sin(t)
                            / Math.Sqrt((e2 - 1) * (e2 - 1) * (1 + e2) * (1 + 4 * e2 + e4)),
            [8] = (r, t) => 2 * Math.Sqrt(2) * r * (-2 - 2 * e4 + 3 * r * r + e2 * (-2 + 3 * r * r)) * Math.Cos(t)
                            / Math.Sqrt((e2 - 1) * (e2 - 1) * (1 + e2) * (1 + 4 * e2 + e4)),
            [9] = (r, t) => 2 * Math.Sqrt(2) * P(r, 3) * Math.Sin(3 * t) / Math.Sqrt(1 + e2 + e4 + e6),
            [10] = (r, t) => 2 * Math.Sqrt(2) * P(r, 3) * Math.Cos(3 * t) / Math.Sqrt(1 + e2 + e4 + e6),
            [11] = (r, t) => Math.Sqrt(5) * (1 + e4 - 6 * r * r + 6 * P(r, 4) + e2 * (4 - 6 * r * r)) / ((e2 - 1) * (e2 - 1)),
            [14] = (r, t) => Math.Sqrt(10) * P(r, 4) * Math.Cos(4 * t) / Math.Sqrt(1 + e2 + e4 + e6 + e8),
            [15] = (r, t) => Math.Sqrt(10) * P(r, 4) * Math.Sin(4 * t) / Math.Sqrt(1 + e2 + e4 + e6 + e8),
            [22] = (r, t) => Math.Sqrt(7) * (1 + e6 - 12 * r * r + 30 * P(r, 4) - 20 * P(r, 6)
                                             - 3 * e4 * (-3 + 4 * r * r) + 3 * e2 * (3 - 12 * r * r + 10 * P(r, 4)))
                             / Math.Pow(e2 - 1, 3),
        };
        var basis = new ZernikeBasis(ZernikeOrdering.Annular, 22, e);
        foreach (var (j, expected) in table)
            foreach (double rho in new[] { e, 0.5 * (1 + e), 0.9, 1.0 })
                foreach (double theta in new[] { 0.0, 0.7, 2.5, -1.9 })
                    AssertNear(expected(rho, theta), basis.EvaluatePolar(j, rho, theta), 1e-11,
                        $"Annular {j}, ε = {e}, ρ = {rho}, θ = {theta}");
    }

    /// <summary>Mahajan (1994) Table 1, the radial annular polynomials R_n^m(ρ; ε). R_8^0 is
    /// left out: its ε⁶ term is cut off on the source page.</summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.35)]
    [InlineData(0.6)]
    [InlineData(0.9)]
    public void AnnularRadial_IsMahajanTable1(double e)
    {
        double E(int power) => Math.Pow(e, power);
        double Sum(int top) { double s = 0; for (int i = 0; i <= top; i += 2) s += E(i); return s; }
        var table = new (int N, int M, Func<double, double> R)[]
        {
            (1, 1, r => r / Math.Sqrt(1 + E(2))),
            (2, 0, r => (2 * r * r - 1 - E(2)) / (1 - E(2))),
            (2, 2, r => r * r / Math.Sqrt(1 + E(2) + E(4))),
            (3, 1, r => (3 * (1 + E(2)) * P(r, 3) - 2 * (1 + E(2) + E(4)) * r)
                        / ((1 - E(2)) * Math.Sqrt((1 + E(2)) * (1 + 4 * E(2) + E(4))))),
            (3, 3, r => P(r, 3) / Math.Sqrt(Sum(6))),
            (4, 0, r => (6 * P(r, 4) - 6 * (1 + E(2)) * r * r + 1 + 4 * E(2) + E(4)) / Math.Pow(1 - E(2), 2)),
            (4, 2, r => (4 * P(r, 4) - 3 * ((1 - E(8)) / (1 - E(6))) * r * r)
                        / Math.Sqrt((16 * (1 - E(10)) - 15 * Math.Pow(1 - E(8), 2) / (1 - E(6))) / (1 - E(2)))),
            (4, 4, r => P(r, 4) / Math.Sqrt(Sum(8))),
            (5, 1, r => (10 * (1 + 4 * E(2) + E(4)) * P(r, 5) - 12 * (1 + 4 * E(2) + 4 * E(4) + E(6)) * P(r, 3)
                         + 3 * (1 + 4 * E(2) + 10 * E(4) + 4 * E(6) + E(8)) * r)
                        / (Math.Pow(1 - E(2), 2) * Math.Sqrt((1 + 4 * E(2) + E(4)) * (1 + 9 * E(2) + 9 * E(4) + E(6))))),
            (5, 3, r => (5 * P(r, 5) - 4 * ((1 - E(10)) / (1 - E(8))) * P(r, 3))
                        / Math.Sqrt((25 * (1 - E(12)) - 24 * Math.Pow(1 - E(10), 2) / (1 - E(8))) / (1 - E(2)))),
            (5, 5, r => P(r, 5) / Math.Sqrt(Sum(10))),
            (6, 0, r => (20 * P(r, 6) - 30 * (1 + E(2)) * P(r, 4) + 12 * (1 + 3 * E(2) + E(4)) * r * r
                         - (1 + 9 * E(2) + 9 * E(4) + E(6))) / Math.Pow(1 - E(2), 3)),
            (6, 2, r => (15 * (1 + 4 * E(2) + 10 * E(4) + 4 * E(6) + E(8)) * P(r, 6)
                         - 20 * (1 + 4 * E(2) + 10 * E(4) + 10 * E(6) + 4 * E(8) + E(10)) * P(r, 4)
                         + 6 * (1 + 4 * E(2) + 10 * E(4) + 20 * E(6) + 10 * E(8) + 4 * E(10) + E(12)) * r * r)
                        / (Math.Pow(1 - E(2), 2) * Math.Sqrt((1 + 4 * E(2) + 10 * E(4) + 4 * E(6) + E(8))
                                                            * (1 + 9 * E(2) + 45 * E(4) + 65 * E(6) + 45 * E(8) + 9 * E(10) + E(12))))),
            (6, 4, r => (6 * P(r, 6) - 5 * ((1 - E(12)) / (1 - E(10))) * P(r, 4))
                        / Math.Sqrt((36 * (1 - E(14)) - 35 * Math.Pow(1 - E(12), 2) / (1 - E(10))) / (1 - E(2)))),
            (6, 6, r => P(r, 6) / Math.Sqrt(Sum(12))),
            (7, 5, r => (7 * P(r, 7) - 6 * ((1 - E(14)) / (1 - E(12))) * P(r, 5))
                        / Math.Sqrt((49 * (1 - E(16)) - 48 * Math.Pow(1 - E(14), 2) / (1 - E(12))) / (1 - E(2)))),
            (7, 7, r => P(r, 7) / Math.Sqrt(Sum(14))),
        };
        var basis = new ZernikeBasis(ZernikeOrdering.Annular, 1, e);
        foreach (var (n, m, expected) in table)
            foreach (double rho in new[] { e, e + 0.3 * (1 - e), e + 0.75 * (1 - e), 1.0 })
                AssertNear(expected(rho), basis.RadialPolynomial(n, m, rho), 2e-11,
                    $"R_{n}^{m}(ρ = {rho}; ε = {e})");
    }

    // ---- Orthonormality by quadrature ----

    /// <summary>
    /// The plan's gate: each term's RMS over the pupil equals its normalisation, and different
    /// terms are orthogonal. Gauss–Legendre in ρ (exact to degree 2·32 − 1) times the uniform rule
    /// in θ (exact for every trigonometric product here), so the Gram matrix is exact to rounding.
    /// Standard and Annular: unit RMS. Fringe: mean square 1/(n + 1) for m = 0 and 1/(2(n + 1))
    /// otherwise, Wyant and Creath's Eqs. (52)–(53) with R(1) = 1.
    /// </summary>
    [Theory]
    [InlineData(ZernikeOrdering.Fringe, 37, 0.0)]
    [InlineData(ZernikeOrdering.Standard, 231, 0.0)]
    [InlineData(ZernikeOrdering.Annular, 136, 0.5)]
    [InlineData(ZernikeOrdering.Annular, 66, 0.9)]
    public void Terms_AreOrthogonalWithTheirNormalisation(ZernikeOrdering ordering, int count, double e)
    {
        var basis = new ZernikeBasis(ordering, count, e);
        var gram = Gram(basis, 32, 64);
        for (int i = 0; i < count; i++)
        {
            var term = basis.Term(i + 1);
            double expected = ordering == ZernikeOrdering.Fringe
                ? 1.0 / ((term.M == 0 ? 1 : 2) * (term.N + 1))
                : 1.0;
            for (int j = 0; j < count; j++)
                AssertNear(i == j ? expected : 0, gram[i, j], 1e-13,
                    $"{ordering} ⟨Z{i + 1}, Z{j + 1}⟩, ε = {e}");
        }
    }

    // ---- The recurrence ----

    /// <summary>
    /// The recurrence against the factorial sum R_n^m(ρ) = Σ_s (−1)^s (n − s)! /
    /// (s! ((n+m)/2 − s)! ((n−m)/2 − s)!) ρ^(n−2s) evaluated exactly in integers at rational ρ, to
    /// n = 80 (where the same sum in doubles has no correct digit).
    /// </summary>
    [Fact]
    public void CircleRadial_RecurrenceIsTheExactFactorialSum()
    {
        var basis = new ZernikeBasis(ZernikeOrdering.Fringe, 1);
        foreach (var (p, q) in new[] { (3, 10), (7, 10), (19, 20), (99, 100), (1, 1) })
            for (int n = 0; n <= 80; n++)
                for (int m = n % 2; m <= n; m += 2)
                    AssertNear(ExactRadial(n, m, p, q), basis.RadialPolynomial(n, m, (double)p / q), 1e-13,
                        $"R_{n}^{m}({p}/{q})");
    }

    /// <summary>The annular recurrence (discretised Stieltjes) at ε = 0 is the closed-form Jacobi
    /// recurrence of the circle, so the two independent constructions agree.</summary>
    [Fact]
    public void AnnularRecurrence_AtZeroObscurationIsTheCircle()
    {
        foreach (int m in new[] { 0, 1, 2, 5, 12 })
        {
            const int maxK = 15;
            var circle = ZernikeRadialRecurrence.Circle(m, maxK);
            var annulus = ZernikeRadialRecurrence.Annulus(m, maxK, 0);
            double[] pc = new double[maxK + 1], dc = new double[maxK + 1];
            double[] pa = new double[maxK + 1], da = new double[maxK + 1];
            foreach (double rho in new[] { 0.0, 0.2, 0.55, 0.9, 1.0 })
            {
                double u = rho * rho;
                circle.Evaluate(u, maxK + 1, pc, dc);
                annulus.Evaluate(u, maxK + 1, pa, da);
                for (int k = 0; k <= maxK; k++)
                {
                    double value = circle.Scale(k) * pc[k], slope = circle.Scale(k) * dc[k];
                    AssertNear(value, annulus.Scale(k) * pa[k], 1e-12 * (1 + Math.Abs(value)), $"m = {m}, k = {k}, ρ = {rho}");
                    AssertNear(slope, annulus.Scale(k) * da[k], 1e-11 * (1 + Math.Abs(slope)), $"d/du, m = {m}, k = {k}, ρ = {rho}");
                }
            }
        }
    }

    [Fact]
    public void Annular_AtZeroObscurationIsStandard()
    {
        var standard = new ZernikeBasis(ZernikeOrdering.Standard, 66);
        var annular = new ZernikeBasis(ZernikeOrdering.Annular, 66, 0);
        ForEachPoint((x, y, _, _) =>
        {
            for (int j = 1; j <= 66; j++) Assert.Equal(standard.Evaluate(j, x, y), annular.Evaluate(j, x, y));
        });
    }

    // ---- Derivatives ----

    /// <summary>∂Z/∂x and ∂Z/∂y against central differences (h = 1e-6, error O(h²) plus
    /// rounding/h), including the pupil centre where a polar form would divide by ρ.</summary>
    [Theory]
    [InlineData(ZernikeOrdering.Fringe, 37, 0.0)]
    [InlineData(ZernikeOrdering.Standard, 66, 0.0)]
    [InlineData(ZernikeOrdering.Annular, 66, 0.3)]
    public void Gradient_IsTheFiniteDifference(ZernikeOrdering ordering, int count, double e)
    {
        var basis = new ZernikeBasis(ordering, count, e);
        const double h = 1e-6;
        var values = new double[count];
        var dx = new double[count];
        var dy = new double[count];
        ForEachPoint((x, y, _, _) =>
        {
            basis.EvaluateAll(x, y, values, dx, dy);
            for (int j = 1; j <= count; j++)
            {
                var single = basis.EvaluateWithGradient(j, x, y);
                Assert.Equal(single.Value, values[j - 1], 14);
                double fx = (basis.Evaluate(j, x + h, y) - basis.Evaluate(j, x - h, y)) / (2 * h);
                double fy = (basis.Evaluate(j, x, y + h) - basis.Evaluate(j, x, y - h)) / (2 * h);
                double scale = 1 + Math.Abs(single.DX) + Math.Abs(single.DY);
                AssertNear(fx, single.DX, 2e-8 * scale, $"{ordering} ∂Z{j}/∂x at ({x}, {y})");
                AssertNear(fy, single.DY, 2e-8 * scale, $"{ordering} ∂Z{j}/∂y at ({x}, {y})");
                AssertNear(single.DX, dx[j - 1], 1e-13 * scale, $"EvaluateAll ∂Z{j}/∂x");
                AssertNear(single.DY, dy[j - 1], 1e-13 * scale, $"EvaluateAll ∂Z{j}/∂y");
            }
        });
    }

    // ---- Index maps ----

    /// <summary>"Zernike polynomials" (Wikipedia), Noll's indices j = 1 … 20 as (n, m), m &lt; 0
    /// for sin; and OpticStudio's Standard table for the cos/sin of 21–28.</summary>
    [Fact]
    public void Noll_IndicesAreThePublishedTable()
    {
        var table = new (int N, int M)[]
        {
            (0, 0), (1, 1), (1, -1), (2, 0), (2, -2), (2, 2), (3, -1), (3, 1), (3, -3), (3, 3),
            (4, 0), (4, 2), (4, -2), (4, 4), (4, -4), (5, 1), (5, -1), (5, 3), (5, -3), (5, 5),
            (5, -5), (6, 0), (6, -2), (6, 2), (6, -4), (6, 4), (6, -6), (6, 6),
        };
        for (int j = 1; j <= table.Length; j++)
        {
            Assert.Equal(new ZernikeTerm(table[j - 1].N, table[j - 1].M), ZernikeIndex.FromNoll(j));
            Assert.Equal(j, ZernikeIndex.ToNoll(new ZernikeTerm(table[j - 1].N, table[j - 1].M)));
        }
    }

    /// <summary>"Zernike polynomials" (Wikipedia), Fringe indices 1 … 20 as (n, m); and the 37th
    /// is the 12th-degree spherical term of Wyant and Creath's number 36.</summary>
    [Fact]
    public void Fringe_IndicesAreThePublishedTable()
    {
        var table = new (int N, int M)[]
        {
            (0, 0), (1, 1), (1, -1), (2, 0), (2, 2), (2, -2), (3, 1), (3, -1), (4, 0), (3, 3),
            (3, -3), (4, 2), (4, -2), (5, 1), (5, -1), (6, 0), (4, 4), (4, -4), (5, 3), (5, -3),
        };
        for (int j = 1; j <= table.Length; j++)
            Assert.Equal(new ZernikeTerm(table[j - 1].N, table[j - 1].M), ZernikeIndex.FromFringe(j));
        Assert.Equal(new ZernikeTerm(12, 0), ZernikeIndex.FromFringe(37));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZernikeIndex.FromFringe(38));
    }

    /// <summary>Noll: every j to (n, m) and back, and every (n, m) with n ≤ 60 reached once.</summary>
    [Fact]
    public void Noll_MapIsBijective()
    {
        int last = 61 * 62 / 2;
        var seen = new HashSet<ZernikeTerm>();
        for (int j = 1; j <= last; j++)
        {
            var term = ZernikeIndex.FromNoll(j);
            Assert.Equal(j, ZernikeIndex.ToNoll(term));
            Assert.True(seen.Add(term), $"Noll {j} repeats {term}");
            Assert.True(term.N <= 60);
            // Noll's rules: n never falls, |m| never falls within one n, even j is cos.
            if (j > 1)
            {
                var before = ZernikeIndex.FromNoll(j - 1);
                Assert.True(term.N > before.N || (term.N == before.N && term.AbsM >= before.AbsM), $"order at {j}");
            }
            if (term.M != 0) Assert.Equal((j & 1) == 0, term.M > 0);
        }
        Assert.Equal(last, seen.Count);
    }

    /// <summary>Fringe: 37 distinct terms, each to its Noll index and back; every Fringe term is a
    /// Standard term, and exactly those 37 Noll terms have a Fringe index.</summary>
    [Fact]
    public void FringeAndNoll_AreBijectiveOverTheSharedTerms()
    {
        var nollOfFringe = new HashSet<int>();
        for (int f = 1; f <= 37; f++)
        {
            var term = ZernikeIndex.FromFringe(f);
            Assert.Equal(f, ZernikeIndex.ToFringe(term));
            int noll = ZernikeIndex.FringeToNoll(f);
            Assert.True(nollOfFringe.Add(noll), $"Fringe {f} maps to Noll {noll} twice");
            Assert.True(ZernikeIndex.TryNollToFringe(noll, out int back));
            Assert.Equal(f, back);
        }
        int maxNoll = nollOfFringe.Max();
        int withFringe = 0;
        for (int j = 1; j <= maxNoll; j++)
            if (ZernikeIndex.TryNollToFringe(j, out _)) withFringe++;
        Assert.Equal(37, withFringe);
    }

    /// <summary>Fringe coefficients converted to Standard, and back, describe the same
    /// wavefront at every point.</summary>
    [Fact]
    public void CoefficientConversion_KeepsTheWavefront()
    {
        var rng = new SplitMix64(33);
        var fringe = new double[37];
        for (int j = 0; j < 37; j++) fringe[j] = rng.NextDouble() - 0.5;
        var standard = ZernikeIndex.ConvertCoefficients(fringe, ZernikeOrdering.Fringe, ZernikeOrdering.Standard);
        var fringeBasis = new ZernikeBasis(ZernikeOrdering.Fringe, 37);
        var standardBasis = new ZernikeBasis(ZernikeOrdering.Standard, standard.Length);
        ForEachPoint((x, y, _, _) =>
            AssertNear(fringeBasis.Sum(fringe, x, y), standardBasis.Sum(standard, x, y), 1e-12, $"at ({x}, {y})"));
        var back = ZernikeIndex.ConvertCoefficients(standard, ZernikeOrdering.Standard, ZernikeOrdering.Fringe);
        Assert.Equal(37, back.Length);
        for (int j = 0; j < 37; j++) AssertNear(fringe[j], back[j], 1e-15, $"Fringe {j + 1}");

        var notFringe = new double[ZernikeIndex.ToNoll(new ZernikeTerm(7, 7))];
        notFringe[^1] = 1;
        Assert.Throws<ArgumentException>(() =>
            ZernikeIndex.ConvertCoefficients(notFringe, ZernikeOrdering.Standard, ZernikeOrdering.Fringe));
    }

    [Fact]
    public void Basis_RejectsWhatItCannotRepresent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZernikeBasis(ZernikeOrdering.Fringe, 38));
        Assert.Throws<ArgumentException>(() => new ZernikeBasis(ZernikeOrdering.Standard, 10, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZernikeBasis(ZernikeOrdering.Annular, 10, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZernikeTerm(3, 2));
    }

    // ---- Helpers ----

    private static double P(double r, int power) => Math.Pow(r, power);

    private static void AssertNear(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (off by {Math.Abs(expected - actual):g3})");

    /// <summary>The pupil-averaged products (1/area) ∫∫ Z_i Z_j ρ dρ dθ.</summary>
    private static double[,] Gram(ZernikeBasis basis, int radialPoints, int angularPoints)
    {
        int count = basis.TermCount;
        double e = basis.Obscuration;
        var (nodes, weights) = GaussLegendre.Rule(radialPoints, e, 1);
        var gram = new double[count, count];
        var values = new double[count];
        double area = Math.PI * (1 - e * e);
        for (int a = 0; a < nodes.Length; a++)
            for (int t = 0; t < angularPoints; t++)
            {
                double theta = 2 * Math.PI * t / angularPoints;
                basis.EvaluateAll(nodes[a] * Math.Cos(theta), nodes[a] * Math.Sin(theta), values);
                double w = weights[a] * nodes[a] * (2 * Math.PI / angularPoints) / area;
                for (int i = 0; i < count; i++)
                {
                    double wi = w * values[i];
                    for (int j = 0; j < count; j++) gram[i, j] += wi * values[j];
                }
            }
        return gram;
    }

    /// <summary>The factorial sum for R_n^m at ρ = p/q in exact integer arithmetic, rounded to
    /// double once at the end.</summary>
    private static double ExactRadial(int n, int m, int p, int q)
    {
        BigInteger numerator = 0;
        for (int s = 0; s <= (n - m) / 2; s++)
        {
            BigInteger c = Factorial(n - s) / (Factorial(s) * Factorial((n + m) / 2 - s) * Factorial((n - m) / 2 - s));
            // ρ^(n−2s) over the common denominator q^n.
            BigInteger term = c * BigInteger.Pow(p, n - 2 * s) * BigInteger.Pow(q, 2 * s);
            numerator += (s & 1) == 0 ? term : -term;
        }
        const int bits = 120;
        BigInteger scaled = (numerator << bits) / BigInteger.Pow(q, n);
        return (double)scaled / Math.Pow(2, bits);
    }

    private static BigInteger Factorial(int k)
    {
        BigInteger result = BigInteger.One;
        for (int i = 2; i <= k; i++) result *= i;
        return result;
    }
}
