using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// The parameter-column layouts of OP-25's surface types (docs/Optics-Conventions.md §55),
/// registered by <see cref="SurfaceTypes"/>. Each layout is copied from the type's OpticStudio
/// 2025 R2 help page, saved under plans/sources/optics/op-25-help/ and named in the layout.
/// Not registered: Chebyshev Polynomial (the page does not give the order of its coefficients in
/// parameters 13–236), Toroidal NURBS, Q-Type Freeform, TrueFreeForm (§55 says why).
/// </summary>
internal static class FreeformLayouts
{
    /// <summary>The Grid Sag's type name.</summary>
    public const string GridSag = "Grid Sag";

    private static SurfaceParameterInfo P(int number, string name, int power, string? description = null) =>
        new(number, name, power) { Description = description };

    private static SurfaceParameterInfo Count(int number, string name, int min, int max, string? description = null) =>
        new(number, name, 0) { IsInteger = true, Range = (min, max), Description = description };

    private static SurfaceParameterInfo Flag(int number, string name, string description) =>
        new(number, name, 0) { IsInteger = true, Range = (0, 1), Description = description };

    private static double[] Range(Func<int, double> value, int first, int count) =>
        Enumerable.Range(first, count).Select(value).ToArray();

    private static double[] Trimmed(double[] a)
    {
        int n = a.Length;
        while (n > 0 && a[n - 1] == 0) n--;
        return n == a.Length ? a : a[..n];
    }

    private static Dictionary<int, double> Numbered(IEnumerable<double> values, int first, Dictionary<int, double>? into = null)
    {
        var d = into ?? new Dictionary<int, double>();
        int i = first;
        foreach (double v in values) d[i++] = v;
        return d;
    }

    private static IEnumerable<SurfaceParameterInfo> EvenTerms() =>
        Enumerable.Range(1, 8).Select(i => P(i, $"α{i}", 1 - 2 * i, $"Coefficient on r^{2 * i}."));

    private static SurfaceParameterInfo Extrapolate() => Flag(0, "Extrapolate",
        "0: the Zernike terms are ignored outside the normalization radius; 1: they are used wherever the ray lands.");

    public static IEnumerable<SurfaceTypeLayout> Build()
    {
        // "Polynomial": parameters 1–8 are γ1–γ8; z = γ1x² + γ2x⁴ + γ3x⁶ + γ4x⁸ + γ5y² + … + γ8y⁸;
        // "The base radius of curvature and the conic constant are not used".
        yield return new SurfaceTypeLayout("Polynomial", "OpticStudio 2025 R2 help, \"Polynomial\", table Parameter Definitions for Polynomial Surfaces",
            Enumerable.Range(1, 4).Select(i => P(i, $"γ{i}", 1 - 2 * i, $"Coefficient on x^{2 * i}."))
                .Concat(Enumerable.Range(1, 4).Select(i => P(4 + i, $"γ{4 + i}", 1 - 2 * i, $"Coefficient on y^{2 * i}.")))
                .ToArray(),
            v => new PolynomialSurface(Trimmed(Range(v, 1, 8))),
            s => Numbered(((PolynomialSurface)s).Gamma, 1))
        { UsesRadiusAndConic = false };

        // "Extended Polynomial": 13 maximum term number (up to 230), 14 normalization radius,
        // 15–244 the polynomial terms (lens units).
        yield return new SurfaceTypeLayout("Extended Polynomial", "OpticStudio 2025 R2 help, \"Extended Polynomial\", table Parameter Definitions for Extended Polynomial Surfaces",
            new[] { Count(13, "Max term #", 0, 230, "Maximum term number; terms beyond it are ignored."), P(14, "Norm radius", 1, "Normalization radius, lens units.") }
                .Concat(Enumerable.Range(1, 230).Select(i => P(14 + i, Special.ExtendedPolynomialTerms.Exponents(i) is var (px, py) ? $"x{px}y{py}" : "", 1,
                    $"Coefficient on extended polynomial term {i}, lens units.")))
                .ToArray(),
            v => new ExtendedPolynomialSurface(v(14), Range(v, 15, (int)v(13))),
            s =>
            {
                var e = (ExtendedPolynomialSurface)s;
                var d = Numbered(e.Terms, 15);
                d[13] = e.Terms.Count;
                d[14] = e.NormalizationRadius;
                return d;
            });

        // "Zernike Fringe Sag" / "Zernike Standard Sag": 0 extrapolate, 1–8 α1–α8, 9 and 10 the
        // Zernike decenter (lens units), 13 number of terms (up to 37 / 231), 14 normalization
        // radius, 15 … the coefficients (lens units).
        foreach (var ordering in new[] { ZernikeOrdering.Fringe, ZernikeOrdering.Standard })
        {
            var o = ordering;
            int max = ZernikeSagSurface.MaxTerms(o);
            string name = ZernikeSagSurface.NameOf(o);
            yield return new SurfaceTypeLayout(name, $"OpticStudio 2025 R2 help, \"{name}\", table Parameter Definitions for {name} Surfaces",
                new[] { Extrapolate() }
                    .Concat(EvenTerms())
                    .Append(P(9, "Zernike decenter X", 1, "Lens units."))
                    .Append(P(10, "Zernike decenter Y", 1, "Lens units."))
                    .Append(Count(13, "Number of terms", 0, max, "The highest Zernike term used; terms beyond it are ignored."))
                    .Append(P(14, "Norm radius", 1, "Normalization radius, lens units."))
                    .Concat(Enumerable.Range(1, max).Select(i => P(14 + i, $"Zernike {i}", 1, $"Coefficient on Zernike {(o == ZernikeOrdering.Fringe ? "Fringe" : "Standard")} polynomial {i}, lens units.")))
                    .ToArray(),
                v => new ZernikeSagSurface(o, Trimmed(Range(v, 1, 8)), Range(v, 15, (int)v(13)), v(14), v(0) != 0, v(9), v(10)),
                s =>
                {
                    var z = (ZernikeSagSurface)s;
                    var d = Numbered(z.Alpha, 1);
                    Numbered(z.Zernike, 15, d);
                    d[0] = z.Extrapolate ? 1 : 0;
                    d[9] = z.DecenterX;
                    d[10] = z.DecenterY;
                    d[13] = z.Zernike.Count;
                    d[14] = z.NormalizationRadius;
                    return d;
                });
        }

        // "Zernike Annular Standard Sag": 0 extrapolate, 1–8 the aspheric coefficients (2nd to
        // 16th order), 13 obscuration, 14 maximum term number (80), 15 normalization radius,
        // 16–95 the coefficients on Zernike polynomials 1–80 (lens units).
        {
            const string name = "Zernike Annular Standard Sag";
            yield return new SurfaceTypeLayout(name, "OpticStudio 2025 R2 help, \"Zernike Annular Standard Sag\", table Parameter Definitions for Zernike Annular Standard Sag Surfaces",
                new[] { Extrapolate() }
                    .Concat(EvenTerms())
                    .Append(P(13, "Obscuration", 0, "The annulus's inner radius over the normalization radius, from 0 up to (not including) 1."))
                    .Append(Count(14, "Max term #", 0, 80, "The highest Zernike term used; terms beyond it are ignored."))
                    .Append(P(15, "Norm radius", 1, "Normalization radius, lens units."))
                    .Concat(Enumerable.Range(1, 80).Select(i => P(15 + i, $"Zernike {i}", 1, $"Coefficient on Zernike annular polynomial {i}, lens units.")))
                    .ToArray(),
                v => new ZernikeSagSurface(ZernikeOrdering.Annular, Trimmed(Range(v, 1, 8)), Range(v, 16, (int)v(14)), v(15), v(0) != 0, obscuration: v(13)),
                s =>
                {
                    var z = (ZernikeSagSurface)s;
                    var d = Numbered(z.Alpha, 1);
                    Numbered(z.Zernike, 16, d);
                    d[0] = z.Extrapolate ? 1 : 0;
                    d[13] = z.Obscuration;
                    d[14] = z.Zernike.Count;
                    d[15] = z.NormalizationRadius;
                    return d;
                });
        }

        // "Off-Axis Conic Freeform": 13 offset Y0, 14 maximum term number, 15 normalization
        // radius, 16–245 the coefficients (lens units). The term table's order is the Extended
        // Polynomial's (A1,0, A0,1, A2,0, A1,1, A0,2, …).
        yield return new SurfaceTypeLayout("Off-Axis Conic Freeform", "OpticStudio 2025 R2 help, \"Off-Axis Conic Freeform\", its parameter table and freeform term table",
            new[]
            {
                P(13, "Offset", 1, "The Y0 off-axis distance, lens units."),
                Count(14, "Max term #", 0, 230, "Maximum number of polynomial terms."),
                P(15, "Norm radius", 1, "The normalization radius of the polynomial terms, lens units."),
            }
                .Concat(Enumerable.Range(1, 230).Select(i => P(15 + i, Special.ExtendedPolynomialTerms.Exponents(i) is var (px, py) ? $"X{px}Y{py}" : "", 1,
                    $"Coefficient A{Special.ExtendedPolynomialTerms.Exponents(i).Px},{Special.ExtendedPolynomialTerms.Exponents(i).Py}, lens units.")))
                .ToArray(),
            v => new OffAxisConicFreeform(v(13), v(15), Range(v, 16, (int)v(14))),
            s =>
            {
                var o = (OffAxisConicFreeform)s;
                var d = Numbered(o.Terms, 16);
                d[13] = o.Offset;
                d[14] = o.Terms.Count;
                d[15] = o.NormalizationRadius;
                return d;
            });

        // "Grid Sag": 0 interpolation (0 bicubic, 1 linear), 1–8 α1–α8, 9 and 10 the Zernike
        // decenter, 13 number of terms (up to 231), 14 normalization radius, 15–245 the Zernike
        // Standard coefficients. The grid itself is imported from its data file and saved with the
        // surface (OpticsSurfaceSettings.GridSag); the layout alone cannot build the shape.
        yield return new SurfaceTypeLayout(GridSag, "OpticStudio 2025 R2 help, \"Grid Sag\", table Parameter Definitions for Grid Sag Surfaces",
            new[] { Flag(0, "Interpolation", "0: bicubic spline; 1: linear.") }
                .Concat(EvenTerms())
                .Append(P(9, "Zernike decenter X", 1, "Lens units."))
                .Append(P(10, "Zernike decenter Y", 1, "Lens units."))
                .Append(Count(13, "Number of terms", 0, 231, "The highest Zernike Standard term used."))
                .Append(P(14, "Norm radius", 1, "Normalization radius, lens units."))
                .Concat(Enumerable.Range(1, 231).Select(i => P(14 + i, $"Zernike {i}", 1, $"Coefficient on Zernike Standard polynomial {i}, lens units.")))
                .ToArray(),
            _ => throw new ArgumentException("Grid Sag: no grid data has been imported; import the grid's .DAT file (OpticStudio's 'nx ny delx dely unitflag xdec ydec' format) for this surface."),
            s =>
            {
                var g = (GridSagSurface)s;
                var d = Numbered(g.Alpha, 1);
                Numbered(g.Zernike, 15, d);
                d[0] = (int)g.Interpolation;
                d[9] = g.ZernikeDecenterX;
                d[10] = g.ZernikeDecenterY;
                d[13] = g.Zernike.Count;
                d[14] = g.NormalizationRadius;
                return d;
            });

        // "Radial NURBS": 13 number of control points (4 to 60), then y, z, w of point n in
        // 3n + 11, 3n + 12, 3n + 13 (y, z lens units, w dimensionless); no radius or conic.
        var nurbs = new List<SurfaceParameterInfo> { Count(13, "Control points", RadialNurbsSurface.MinPoints, RadialNurbsSurface.MaxPoints, "Number of control points (at least 4, at most 60).") };
        for (int n = 1; n <= RadialNurbsSurface.MaxPoints; n++)
        {
            nurbs.Add(P(3 * n + 11, $"y{n}", 1, $"y coordinate of control point {n}, lens units."));
            nurbs.Add(P(3 * n + 12, $"z{n}", 1, $"z coordinate (sag) of control point {n}, lens units."));
            nurbs.Add(P(3 * n + 13, $"w{n}", 0, $"Weight of control point {n}."));
        }
        yield return new SurfaceTypeLayout("Radial NURBS", "OpticStudio 2025 R2 help, \"Radial NURBS\", table Parameter Definitions for Radial NURBS Surfaces",
            nurbs.ToArray(),
            v =>
            {
                int count = (int)v(13);
                return new RadialNurbsSurface(
                    Enumerable.Range(1, count).Select(n => v(3 * n + 11)).ToArray(),
                    Enumerable.Range(1, count).Select(n => v(3 * n + 12)).ToArray(),
                    Enumerable.Range(1, count).Select(n => v(3 * n + 13)).ToArray());
            },
            s =>
            {
                var r = (RadialNurbsSurface)s;
                var d = new Dictionary<int, double> { [13] = r.Y.Count };
                for (int n = 1; n <= r.Y.Count; n++)
                {
                    d[3 * n + 11] = r.Y[n - 1];
                    d[3 * n + 12] = r.Z[n - 1];
                    d[3 * n + 13] = r.W[n - 1];
                }
                return d;
            })
        { UsesRadiusAndConic = false };
    }

    /// <summary>A Grid Sag shape of the SI parameter values and the grid; throws
    /// <see cref="ArgumentException"/> naming the value when one is not allowed (the layout's
    /// checks are made first).</summary>
    public static GridSagSurface BuildGridSag(SurfaceTypeLayout layout, Func<int, double> v, GridSagData data)
    {
        foreach (var p in layout.Parameters)
        {
            double x = v(p.Number);
            if (!double.IsFinite(x)) throw new ArgumentException($"{GridSag}: {SurfaceTypeLayout.Label(p)} is {x}; it must be a finite number.");
            if (p.IsInteger && Math.Round(x) != x) throw new ArgumentException($"{GridSag}: {SurfaceTypeLayout.Label(p)} is {x:R}; it must be a whole number.");
            if (p.Range is { } r && (x < r.Min || x > r.Max)) throw new ArgumentException($"{GridSag}: {SurfaceTypeLayout.Label(p)} is {x:R}; it must be from {r.Min} to {r.Max}.");
        }
        return new GridSagSurface(data, (GridSagInterpolation)(int)v(0), Trimmed(Range(v, 1, 8)), Range(v, 15, (int)v(13)), v(14), v(9), v(10));
    }
}
