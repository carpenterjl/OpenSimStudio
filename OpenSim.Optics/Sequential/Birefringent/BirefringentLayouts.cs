using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>
/// The parameter columns of the Birefringent In and Birefringent Out surfaces (OP-53), copied
/// from OpticStudio 2025 R2 help, "Birefringent In and Birefringent Out", tables "Parameter
/// Definitions for Birefringent "In" Surfaces" and "... "Out" Surfaces" (saved under
/// plans/sources/optics/op-53-help/). Registered by <see cref="SurfaceTypes"/>.
/// <para>
/// Refused by name: shape 1 (parameter 6; the toroidal substrate) and a non-zero gradient mode
/// (parameter 13; crystals whose axis varies with position, Jenkins et al., JOSA A 24 (2007)
/// 2089, and the user-defined birefringence DLL), which are not built. The polynomial
/// coefficients of the gradient modes (parameters 14 and up) have no columns.
/// </para>
/// </summary>
internal static class BirefringentLayouts
{
    private static SurfaceParameterInfo P(int number, string name, int power, string? description = null) =>
        new(number, name, power) { Description = description };

    private static SurfaceParameterInfo Int(int number, string name, int min, int max, string description) =>
        new(number, name, 0) { IsInteger = true, Range = (min, max), Description = description };

    /// <summary>6 shape, 7 radius of rotation, 8–12 the 2nd to 10th order aspheric
    /// coefficients (on r², r⁴ … r¹⁰, units lens units^(1 − 2i)).</summary>
    private static IEnumerable<SurfaceParameterInfo> SubstrateColumns() => new[]
    {
        Int(6, "Shape", 0, 1, "0: the Standard substrate (radius and conic). 1, the toroidal substrate of parameters 7–12, is not built."),
        P(7, "Radius of rotation", 1, "Shape 1 only (not built): radius of rotation, lens units."),
    }.Concat(Enumerable.Range(1, 5).Select(i => P(7 + i, $"α{i}", 1 - 2 * i, $"Shape 1 only (not built): coefficient on r^{2 * i}.")));

    private static void CheckSubstrate(string name, Func<int, double> v)
    {
        if (v(6) != 0)
            throw new ArgumentException($"{name}: shape {v(6)} (parameter 6), the toroidal substrate, is not built; use shape 0 (the Standard substrate of the radius and conic).");
    }

    private static double[] Aspheric(Func<int, double> v)
    {
        var a = Enumerable.Range(8, 5).Select(v).ToArray();
        int n = a.Length;
        while (n > 0 && a[n - 1] == 0) n--;
        return a[..n];
    }

    private static Dictionary<int, double> Substrate(BirefringentShape s)
    {
        var d = new Dictionary<int, double> { [6] = 0, [7] = s.RadiusOfRotation };
        for (int i = 0; i < s.Aspheric.Count; i++) d[8 + i] = s.Aspheric[i];
        return d;
    }

    internal static IEnumerable<SurfaceTypeLayout> Build()
    {
        const string source = "OpticStudio 2025 R2 help, \"Birefringent In and Birefringent Out\", table Parameter Definitions for Birefringent \"In\" Surfaces";
        yield return new SurfaceTypeLayout(BirefringentInShape.Name, source,
            new[]
            {
                P(0, "Draw length", 1, "Length to draw the crystal axis, 0 to not draw (saved; the layouts do not draw it)."),
                Int(1, "Mode", 0, 3, "0 ordinary ray, 1 extraordinary ray; 2 and 3 trace the ordinary or extraordinary ray and keep the other mode's phase in the polarization trace."),
                P(2, "X-cosine", 0, "Crystal axis x direction cosine, this surface's local frame (normalised internally)."),
                P(3, "Y-cosine", 0, "Crystal axis y direction cosine."),
                P(4, "Z-cosine", 0, "Crystal axis z direction cosine."),
                Int(5, "Parax Ignore", 0, 1, "1: paraxial rays ignore the extraordinary index (use the ordinary one)."),
            }.Concat(SubstrateColumns())
             .Append(Int(13, "Gradient mode", 0, 9, "0: homogeneous crystal. Modes 1–9 (axis varying with position) are not built."))
             .ToArray(),
            v =>
            {
                CheckSubstrate(BirefringentInShape.Name, v);
                if (v(13) != 0)
                    throw new ArgumentException($"{BirefringentInShape.Name}: gradient mode {v(13)} (parameter 13), a crystal axis that varies with position, is not built; use 0 (homogeneous).");
                return new BirefringentInShape((int)v(1), new Vector3D(v(2), v(3), v(4)), v(0), (int)v(5), v(7), Aspheric(v));
            },
            s =>
            {
                var b = (BirefringentInShape)s;
                var d = Substrate(b);
                d[0] = b.DrawLength;
                d[1] = b.Mode;
                d[2] = b.Axis.X;
                d[3] = b.Axis.Y;
                d[4] = b.Axis.Z;
                d[5] = b.ParaxIgnore;
                d[13] = 0;
                return d;
            });

        yield return new SurfaceTypeLayout(BirefringentOutShape.Name,
            "OpticStudio 2025 R2 help, \"Birefringent In and Birefringent Out\", table Parameter Definitions for Birefringent \"Out\" Surfaces (0–5 unused)",
            SubstrateColumns().ToArray(),
            v =>
            {
                CheckSubstrate(BirefringentOutShape.Name, v);
                return new BirefringentOutShape(v(7), Aspheric(v));
            },
            s => Substrate((BirefringentShape)s));
    }
}
