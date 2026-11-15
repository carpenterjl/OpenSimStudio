using OpenSim.Core.Numerics;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Diffractive;

/// <summary>
/// The parameter-column layouts of OP-26's surface types (docs/Optics-Conventions.md §57),
/// registered by <see cref="SurfaceTypes"/>. Each is copied from the type's OpticStudio 2025 R2
/// help page (saved under plans/sources/optics/op-26-help/), named in the layout. Grating
/// frequencies (lines per micrometer), construction wavelengths (micrometers), phase coefficients
/// (radians or waves) and orders do not scale with the lens unit (length power 0).
/// <para>
/// Not registered (§57 says why): Radial Grating, Elliptical Grating 1 and 2, Extended Toroidal
/// Grating, Binary Optic 3 and 4, Grid Phase, Zone Plate and Optically Fabricated Hologram.
/// </para>
/// </summary>
internal static class DiffractiveLayouts
{
    private static SurfaceParameterInfo P(int number, string name, int power, string? description = null) =>
        new(number, name, power) { Description = description };

    private static SurfaceParameterInfo Count(int number, string name, int max, string? description = null) =>
        new(number, name, 0) { IsInteger = true, Range = (0, max), Description = description };

    private static SurfaceParameterInfo Flag(int number, string name, string description) =>
        new(number, name, 0) { IsInteger = true, Range = (0, 1), Description = description };

    private static IEnumerable<SurfaceParameterInfo> EvenTerms(int first = 1, int count = 8) =>
        Enumerable.Range(1, count).Select(i => P(first + i - 1, $"α{i}", 1 - 2 * i, $"Coefficient on {(first == 1 ? "r" : "y")}^{2 * i} of the substrate."));

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

    private static SurfaceParameterInfo Order(int number) => P(number, "Diffraction order", 0, "The diffraction order M (its sign convention is arbitrary).");

    private static SurfaceParameterInfo Lines(int number) => P(number, "Lines/µm", 0, "Grating lines per micrometer, whatever the lens units.");

    private static SurfaceParameterInfo Volume(int number) => Flag(number, "Volume hologram?",
        "0: a surface hologram (deviates rays only). 1, the volume hologram, is not built in this program.");

    public static IEnumerable<SurfaceTypeLayout> Build()
    {
        // "Diffraction Grating (sequential surfaces, lens data editor)": 1 grating lines per
        // micrometer, 2 diffraction order.
        yield return new SurfaceTypeLayout("Diffraction Grating", "OpticStudio 2025 R2 help, \"Diffraction Grating (sequential surfaces, lens data editor)\", table Parameter Definitions for Diffraction Grating Surfaces",
            new[] { Lines(1), Order(2) },
            v => new DiffractionGrating(v(1), v(2)),
            s =>
            {
                var g = (DiffractionGrating)s;
                return new Dictionary<int, double> { [1] = g.LinesPerMicrometer, [2] = g.Order };
            });

        // "Toroidal Grating": 1 radius of rotation, 2 grating lines per micrometer, 3 order.
        yield return new SurfaceTypeLayout("Toroidal Grating", "OpticStudio 2025 R2 help, \"Toroidal Grating\", table Parameter Definitions for Toroidal Grating Surfaces",
            new[] { P(1, "Radius of rotation", 1, "Radius of rotation in lens units; 0 is infinite (a cylinder flat in x)."), Lines(2), Order(3) },
            v => new ToroidalGrating(v(1), v(2), v(3)),
            s =>
            {
                var g = (ToroidalGrating)s;
                return new Dictionary<int, double> { [1] = g.RadiusOfRotation, [2] = g.LinesPerMicrometer, [3] = g.Order };
            });

        // "Variable Line Space Grating": 1 M, 2 L, 3 cos(α0), 4 cos(β0), 5 λ0. "Only plane
        // gratings are supported", so a radius or conic is refused.
        yield return new SurfaceTypeLayout("Variable Line Space Grating", "OpticStudio 2025 R2 help, \"Variable Line Space Grating\", table Parameter Definitions for Variable Line Space Grating Surfaces",
            new[]
            {
                P(1, "M", 0, "The diffraction order upon playback."),
                P(2, "L", 1, "The (positive) radius of the concave focal surface, lens units."),
                P(3, "cos(α0)", 0, "Cosine of the angle the incident beam makes with the plane of the grating."),
                P(4, "cos(β0)", 0, "Cosine of the angle the reflected beam makes with the plane of the grating."),
                P(5, "λ0", 0, "The construction wavelength, micrometers."),
            },
            v => new VariableLineSpaceGrating(v(1), v(2), v(3), v(4), v(5)),
            s =>
            {
                var g = (VariableLineSpaceGrating)s;
                return new Dictionary<int, double>
                {
                    [1] = g.Order, [2] = g.Radius, [3] = g.CosAlpha0, [4] = g.CosBeta0, [5] = g.ConstructionMicrometers,
                };
            })
        { UsesRadiusAndConic = false };

        // "Binary 1 (sequential surfaces, lens data editor)": 0 M, 1–8 α1–α8, 9 absolute, 13
        // maximum term number (up to 230), 14 normalization radius, 15–244 the polynomial terms
        // (radians).
        yield return new SurfaceTypeLayout("Binary Optic 1", "OpticStudio 2025 R2 help, \"Binary 1 (sequential surfaces, lens data editor)\", table Parameter Definitions for Binary Optic 1 Surfaces",
            new[] { Order(0) }
                .Concat(EvenTerms())
                .Append(P(9, "Absolute?", 0, "Any value other than zero evaluates the phase at |x| and |y|."))
                .Append(Count(13, "Max term #", 230, "Maximum term number; terms beyond it are ignored."))
                .Append(P(14, "Norm radius", 1, "Normalization radius, lens units."))
                .Concat(Enumerable.Range(1, 230).Select(i => P(14 + i, Special.ExtendedPolynomialTerms.Exponents(i) is var (px, py) ? $"x{px}y{py}" : "", 0,
                    $"Phase coefficient on extended polynomial term {i}, radians.")))
                .ToArray(),
            v => new BinaryOptic1(v(0), Trimmed(Range(v, 1, 8)), v(9) != 0, v(14), Range(v, 15, (int)v(13))),
            s =>
            {
                var b = (BinaryOptic1)s;
                var d = Numbered(b.Alpha, 1);
                Numbered(b.Terms, 15, d);
                d[0] = b.Order;
                d[9] = b.Absolute ? 1 : 0;
                d[13] = b.Terms.Count;
                d[14] = b.NormalizationRadius;
                return d;
            });

        // "Binary 2 (sequential surfaces, lens data editor)": 0 M, 1–8 α1–α8, 13 maximum term
        // number (up to 240), 14 normalization radius, 15 on ρ², 16 on ρ⁴, 17–254 on ρ^(2(n − 14)).
        yield return new SurfaceTypeLayout("Binary Optic 2", "OpticStudio 2025 R2 help, \"Binary 2 (sequential surfaces, lens data editor)\", table Parameter Definitions for Binary Optic 2 Surfaces",
            new[] { Order(0) }
                .Concat(EvenTerms())
                .Append(Count(13, "Max term #", BinaryOptic2.MaxTerms, "Maximum term number; terms beyond it are ignored."))
                .Append(P(14, "Norm radius", 1, "Normalization radius, lens units."))
                .Concat(Enumerable.Range(1, BinaryOptic2.MaxTerms).Select(i => P(14 + i, $"ρ^{2 * i}", 0, $"Phase coefficient on ρ^{2 * i}, radians.")))
                .ToArray(),
            v => new BinaryOptic2(v(0), Trimmed(Range(v, 1, 8)), v(14), Range(v, 15, (int)v(13))),
            s =>
            {
                var b = (BinaryOptic2)s;
                var d = Numbered(b.Alpha, 1);
                Numbered(b.Terms, 15, d);
                d[0] = b.Order;
                d[13] = b.Terms.Count;
                d[14] = b.NormalizationRadius;
                return d;
            });

        // "Zernike Fringe Phase" / "Zernike Standard Phase": 0 order, 1 extrapolate, 13 number of
        // terms (up to 37 / 231), 14 normalization radius, 15 … the coefficients in waves.
        // "Zernike Annular Phase" adds 3 obscuration (and is limited to 80 terms).
        foreach (var ordering in new[] { ZernikeOrdering.Fringe, ZernikeOrdering.Standard, ZernikeOrdering.Annular })
        {
            var o = ordering;
            int max = ZernikePhaseShape.MaxTerms(o);
            string name = ZernikePhaseShape.NameOf(o);
            string kind = o switch { ZernikeOrdering.Fringe => "Fringe", ZernikeOrdering.Standard => "Standard", _ => "annular" };
            var columns = new List<SurfaceParameterInfo>
            {
                Order(0),
                Flag(1, "Extrapolate", "0: the Zernike terms are ignored outside the normalization radius; 1: they are used wherever the ray lands."),
            };
            if (o == ZernikeOrdering.Annular)
                columns.Add(P(3, "Obscuration", 0, "The annulus's inner radius over the normalization radius, from 0 up to (not including) 1."));
            columns.Add(Count(13, "Number of terms", max, "The highest Zernike term used; terms beyond it are ignored."));
            columns.Add(P(14, "Norm radius", 1, "Normalization radius, lens units."));
            columns.AddRange(Enumerable.Range(1, max).Select(i => P(14 + i, $"Zernike {i}", 0, $"Coefficient on Zernike {kind} polynomial {i}, waves.")));
            yield return new SurfaceTypeLayout(name, $"OpticStudio 2025 R2 help, \"{name}\", table Parameter Definitions for {name} Surfaces",
                columns.ToArray(),
                v => new ZernikePhaseShape(o, v(0), v(1) != 0, v(14), Range(v, 15, (int)v(13)), o == ZernikeOrdering.Annular ? v(3) : 0),
                s =>
                {
                    var z = (ZernikePhaseShape)s;
                    var d = Numbered(z.Coefficients, 15);
                    d[0] = z.Order;
                    d[1] = z.Extrapolate ? 1 : 0;
                    if (o == ZernikeOrdering.Annular) d[3] = z.Obscuration;
                    d[13] = z.Coefficients.Count;
                    d[14] = z.NormalizationRadius;
                    return d;
                });
        }

        // "Hologram 1" / "Hologram 2": 1–3 the first construction point, 4–6 the second (lens
        // units), 7 construction wavelength (µm), 8 diffraction order, 13 volume hologram flag.
        // Parameters 14–21 ("only available when Par 13 ≠ 0") belong to the volume hologram,
        // which is not built, and are not offered.
        foreach (int kind in new[] { 1, 2 })
        {
            int k = kind;
            string name = $"Hologram {k}";
            yield return new SurfaceTypeLayout(name, $"OpticStudio 2025 R2 help, \"{name}\", table Parameter Definitions for {name} Surfaces",
                Points(1).Append(P(7, "Construction λ", 0, "The construction wavelength λc, micrometers.")).Append(Order(8)).Append(Volume(13)).ToArray(),
                v =>
                {
                    TwoPointHologram.RefuseVolume(name, v(13));
                    return new HologramShape(k, new Vector3D(v(1), v(2), v(3)), new Vector3D(v(4), v(5), v(6)), v(7), v(8));
                },
                s =>
                {
                    var h = (HologramShape)s;
                    return With(PointValues(h, 1), (7, h.ConstructionMicrometers), (8, h.Order), (13, 0));
                });
        }

        // "Toroidal Hologram (sequential surfaces, lens data editor)": 1 radius of rotation, 2–8
        // α1–α7, 13–18 the construction points, 19 construction wavelength, 20 order, 21
        // converge/diverge flag, 22 volume hologram flag (23–30 belong to the volume hologram).
        yield return new SurfaceTypeLayout("Toroidal Hologram", "OpticStudio 2025 R2 help, \"Toroidal Hologram (sequential surfaces, lens data editor)\", table Parameter Definitions for Toroidal Hologram Surfaces",
            new[] { P(1, "Radius of rotation", 1, "Radius of rotation in lens units; 0 is infinite (a cylinder flat in x).") }
                .Concat(EvenTerms(2, 7))
                .Concat(Points(13))
                .Append(P(19, "Construction λ", 0, "The construction wavelength λc, micrometers."))
                .Append(Order(20))
                .Append(P(21, "Converge/diverge", 0, "+1 (any positive value): both beams diverge or both converge; −1 (any negative value): one converges, one diverges."))
                .Append(Volume(22))
                .ToArray(),
            v =>
            {
                TwoPointHologram.RefuseVolume("Toroidal Hologram", v(22));
                return new ToroidalHologram(v(1), Trimmed(Range(v, 2, 7)), new Vector3D(v(13), v(14), v(15)), new Vector3D(v(16), v(17), v(18)), v(19), v(20), v(21));
            },
            s =>
            {
                var h = (ToroidalHologram)s;
                var d = With(PointValues(h, 13), (1, h.RadiusOfRotation), (19, h.ConstructionMicrometers), (20, h.Order), (21, h.BothDiverge ? 1 : -1), (22, 0));
                return Numbered(h.Alpha, 2, d);
            });
    }

    private static IEnumerable<SurfaceParameterInfo> Points(int first) => new[]
    {
        P(first, "Construct X1", 1, "x of the first construction point, lens units."),
        P(first + 1, "Construct Y1", 1, "y of the first construction point, lens units."),
        P(first + 2, "Construct Z1", 1, "z of the first construction point, lens units."),
        P(first + 3, "Construct X2", 1, "x of the second construction point, lens units."),
        P(first + 4, "Construct Y2", 1, "y of the second construction point, lens units."),
        P(first + 5, "Construct Z2", 1, "z of the second construction point, lens units."),
    };

    private static Dictionary<int, double> PointValues(TwoPointHologram h, int first) => new()
    {
        [first] = h.First.X, [first + 1] = h.First.Y, [first + 2] = h.First.Z,
        [first + 3] = h.Second.X, [first + 4] = h.Second.Y, [first + 5] = h.Second.Z,
    };

    private static Dictionary<int, double> With(Dictionary<int, double> d, params (int Number, double Value)[] more)
    {
        foreach (var (n, v) in more) d[n] = v;
        return d;
    }
}
