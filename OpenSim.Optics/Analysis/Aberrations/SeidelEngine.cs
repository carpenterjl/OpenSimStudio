using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Analysis;

// OP-36: third-order (Seidel) aberration coefficients from the paraxial marginal and chief rays.

/// <summary>
/// The five Seidel sums and the two primary chromatic sums of one surface or of a range of
/// surfaces, in meters (lengths relative to the system's air, like every paraxial quantity).
/// </summary>
public readonly record struct SeidelTerms(double S1, double S2, double S3, double S4, double S5, double CL, double CT)
{
    public static SeidelTerms operator +(SeidelTerms a, SeidelTerms b) =>
        new(a.S1 + b.S1, a.S2 + b.S2, a.S3 + b.S3, a.S4 + b.S4, a.S5 + b.S5, a.CL + b.CL, a.CT + b.CT);

    /// <summary>The seven values in OpticStudio's column order: SPHA, COMA, ASTI, FCUR, DIST, CLA, CTR.</summary>
    public double[] ToArray() => new[] { S1, S2, S3, S4, S5, CL, CT };

    /// <summary>OpticStudio's column names, in <see cref="ToArray"/>'s order (help page "Seidel Coefficients").</summary>
    public static readonly string[] Names = { "SPHA S1", "COMA S2", "ASTI S3", "FCUR S4", "DIST S5", "CLA CL", "CTR CT" };
}

/// <summary>One surface's contribution. <see cref="Terms"/> includes <see cref="Aspheric"/>,
/// the part due to the surface's fourth-order departure from its paraxial sphere.</summary>
public sealed record SeidelSurface(int Surface, SeidelTerms Terms, SeidelTerms Aspheric);

/// <summary>
/// The wavefront coefficients of OpticStudio's "Seidel Coefficients" (W040, W131, W222, W220P,
/// W311, W020, W111, W220S, W220M, W220T), in waves at the edge of the exit pupil.
/// </summary>
public readonly record struct SeidelWaveCoefficients(double W040, double W131, double W222, double W220P, double W311,
    double W020, double W111, double W220S, double W220M, double W220T)
{
    public double[] ToArray() => new[] { W040, W131, W222, W220P, W311, W020, W111, W220S, W220M, W220T };

    public static readonly string[] Names = { "W040", "W131", "W222", "W220P", "W311", "W020", "W111", "W220S", "W220M", "W220T" };
}

/// <summary>
/// The ten ray-aberration coefficients of OpticStudio's "Seidel Coefficients", transverse
/// (TSPH, TSCO, TTCO, TAST, TPFC, TSFC, TTFC, TDIS, TAXC, TLAC) or longitudinal (LSPH … LLAC),
/// meters.
/// </summary>
public readonly record struct SeidelRayCoefficients(double Spherical, double SagittalComa, double TangentialComa,
    double Astigmatism, double PetzvalField, double SagittalField, double TangentialField, double Distortion,
    double AxialColour, double LateralColour)
{
    public double[] ToArray() => new[]
    {
        Spherical, SagittalComa, TangentialComa, Astigmatism, PetzvalField, SagittalField, TangentialField, Distortion,
        AxialColour, LateralColour,
    };

    public static readonly string[] TransverseNames = { "TSPH", "TSCO", "TTCO", "TAST", "TPFC", "TSFC", "TTFC", "TDIS", "TAXC", "TLAC" };
    public static readonly string[] LongitudinalNames = { "LSPH", "LSCO", "LTCO", "LAST", "LPFC", "LSFC", "LTFC", "LDIS", "LAXC", "LLAC" };
}

/// <summary>The Seidel sums of a lens at one wavelength (<see cref="SeidelEngine.Compute"/>).</summary>
public sealed record SeidelResult
{
    /// <summary>Surfaces 1 to the last before the image, in order.</summary>
    public required IReadOnlyList<SeidelSurface> Surfaces { get; init; }

    /// <summary>The sum over every surface.</summary>
    public required SeidelTerms Sum { get; init; }

    /// <summary>0-based wavelength row of the computation.</summary>
    public required int Wavelength { get; init; }

    /// <summary>That wavelength in µm in the system's air: one wave of the wave coefficients.</summary>
    public required double WavelengthMicrometers { get; init; }

    /// <summary>The 0-based rows of the shortest and longest table wavelengths the colour sums
    /// difference, or null when the table has one wavelength (no colour).</summary>
    public (int Shortest, int Longest)? ColourRows { get; init; }

    /// <summary>n′u′ of the paraxial marginal ray in image space (the signed index times the
    /// slope dy/dz, docs/Optics-Conventions.md §11).</summary>
    public required double ImageReducedSlope { get; init; }

    /// <summary>u′ = dy/dz of the paraxial marginal ray in image space.</summary>
    public required double ImageSlope { get; init; }

    /// <summary>The image-space index with the sign of the direction of travel.</summary>
    public required double ImageIndex { get; init; }

    /// <summary>The Lagrange invariant H of the marginal ray and the chief ray of the largest radial field, meters.</summary>
    public required double LagrangeInvariant { get; init; }

    /// <summary>The largest radial field the chief ray is traced at, in the field type's unit (degrees or meters).</summary>
    public required double FullField { get; init; }

    /// <summary>The Petzval radius (the inverse of the Petzval sum), meters, signed as a radius
    /// (§3); infinite when the Petzval sum is zero.</summary>
    public required double PetzvalRadius { get; init; }

    /// <summary>True when the marginal ray leaves parallel to the axis (afocal image space), so
    /// the transverse and longitudinal coefficients are undefined.</summary>
    public required bool ImageAtInfinity { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The wavefront coefficients of <paramref name="t"/>, in waves of <see cref="WavelengthMicrometers"/>
    /// (help page "Seidel Coefficients": S1 = 8 W040, S2 = 2 W131, S3 = 2 W222, S4 = 4 W220 − 2 W222,
    /// S5 = 2 W311; W220P = S4/4, W220S = (S3 + S4)/4, W220M = (2 S3 + S4)/4, W020 = CL/2, W111 = CT
    /// from the help's table; W220T = (3 S3 + S4)/4, see <see cref="SeidelEngine"/>).
    /// </summary>
    public SeidelWaveCoefficients Wave(SeidelTerms t)
    {
        double w = WavelengthMicrometers * 1e-6;
        return new SeidelWaveCoefficients(
            t.S1 / 8 / w, t.S2 / 2 / w, t.S3 / 2 / w, t.S4 / 4 / w, t.S5 / 2 / w,
            t.CL / 2 / w, t.CT / w,
            (t.S3 + t.S4) / 4 / w, (2 * t.S3 + t.S4) / 4 / w, (3 * t.S3 + t.S4) / 4 / w);
    }

    /// <summary>
    /// The transverse coefficients of <paramref name="t"/>, meters: the image-plane ray errors
    /// k·S/(n′u′) with OpticStudio's factors k (help page "Seidel Coefficients"), n′u′ =
    /// <see cref="ImageReducedSlope"/>. NaN when <see cref="ImageAtInfinity"/>.
    /// </summary>
    public SeidelRayCoefficients Transverse(SeidelTerms t) => Ray(t, ImageAtInfinity ? double.NaN : ImageReducedSlope);

    /// <summary>
    /// The longitudinal coefficients of <paramref name="t"/>, meters: k·S/(|n′|u′²), positive when
    /// the rays cross the axis before the paraxial image (along the light). NaN when
    /// <see cref="ImageAtInfinity"/>.
    /// </summary>
    public SeidelRayCoefficients Longitudinal(SeidelTerms t) =>
        Ray(t, ImageAtInfinity ? double.NaN : Math.Abs(ImageIndex) * ImageSlope * ImageSlope);

    private static SeidelRayCoefficients Ray(SeidelTerms t, double d) => new(
        t.S1 / (2 * d), t.S2 / (2 * d), 3 * t.S2 / (2 * d), t.S3 / d, t.S4 / (2 * d), (t.S3 + t.S4) / (2 * d),
        (3 * t.S3 + t.S4) / (2 * d), t.S5 / (2 * d), t.CL / d, t.CT / d);
}

/// <summary>
/// Surface-by-surface Seidel sums from the paraxial marginal ray (y, u) and the chief ray of the
/// largest radial field (ȳ, ū) (OP-36, docs/Optics-Conventions.md §32).
/// <para>
/// <b>Formulas.</b> Each surface j has curvature c (its paraxial curvature, which includes an
/// asphere's r² term), signed indices n before and n′ after (§11), the refraction invariants
/// A = n(u + y c) and Ā = n(ū + ȳ c), the Lagrange invariant H = n(ū y − u ȳ), Δ(u/n) = u′/n′ − u/n,
/// Δ(1/n²) = 1/n′² − 1/n² and P = c(1/n′ − 1/n). Then
/// S1 = −A² y Δ(u/n), S2 = −A Ā y Δ(u/n), S3 = −Ā² y Δ(u/n), S4 = −H² P,
/// S5 = −Ā(Ā² Δ(1/n²) y − (H + Ā y) ȳ P), and with δn = n(shortest) − n(longest) of each space
/// (signed like n), CL = A y Δ(δn/n), CT = Ā y Δ(δn/n). A surface whose sag departs from its
/// paraxial sphere by G r⁴ at fourth order adds 8 G (n′ − n) times y⁴, y³ȳ, y²ȳ² and y ȳ³ to S1,
/// S2, S3 and S5. These are RayOptics 0.9.10's (Michael J. Hayford, BSD 3-Clause;
/// <c>rayoptics/parax/thirdorder.py</c>, <c>compute_third_order_and_color</c> and
/// <c>aspheric_seidel_contribution</c>, saved under plans/sources/optics/pyref-venv), written
/// here in this program's own code; RayOptics takes the aspheric chief-ray factor as ȳ/y, which is
/// what the products above are. G here is the general fourth-order departure (the r⁴ coefficient
/// of the sag minus c_p³/8 of the paraxial sphere), which is RayOptics' k c³/8 + A4 when the r²
/// term is zero. Welford's "Aberrations of Optical Systems", which OpticStudio's help cites, was
/// not on hand.
/// </para>
/// <para>
/// <b>Conversions</b> (help page "Seidel Coefficients", 2025 R2, saved under
/// plans/sources/optics/op-36-help/). The wave coefficients follow the page's relations
/// S1 = 8 W040, S2 = 2 W131, S3 = 2 W222, S4 = 4 W220 − 2 W222, S5 = 2 W311 and its table's
/// W220P = S4/4, W220S = (S3 + S4)/4, W220M = (2 S3 + S4)/4, W020 = CL/2, W111 = CT. The page's
/// table also prints S1/2, S3/4 and (3 S3 + S4)/2 for W040, W222 and W220T, which contradict
/// those relations (they would give S1 = 2 W040 and S3 = 4 W222, and the tangential focus would
/// not be three times the sagittal focus's distance from the Petzval surface); the relations are
/// used, so W220T = (3 S3 + S4)/4 (§10 item 28). The transverse coefficients are the page's
/// k·S/(n′u′) with k = 1/2, 1/2, 3/2, 1, 1/2, 1/2, 3/2, 1/2, 1, 1 for TSPH … TLAC, and the
/// longitudinal ones k·S/(n′u′²) with the same k. The page prints a minus sign on every
/// transverse form; with this program's slope u′ = dy/dz (negative for a converging beam above
/// the axis) the transverse coefficient without it is the image-plane ray error of the marginal
/// ray (the tests check TSPH against real-ray fans), so the sign is dropped: the page's u′ is
/// read as the angle measured the other way (§10 item 28). The longitudinal form uses |n′|, so
/// it is positive when the rays cross the axis before the paraxial image along the light, as
/// the page's form is for an undercorrected lens.
/// </para>
/// <para>
/// <b>Surfaces.</b> The help says the coefficients hold for "Standard, Even Asphere, Odd Asphere,
/// Extended Asphere, and Extended Odd Asphere" surfaces; any other shape (and the alternate-root
/// forms) is refused naming the surface. An Odd or Extended Odd Asphere with a non-zero r or r³
/// term is refused too: those terms have no place in a power series in r² (third-order theory),
/// and the help does not say what OpticStudio does with them. Coordinate breaks and tilts are
/// accepted when they only fold the axis (<see cref="SequentialLens.ToParaxialLens"/>);
/// otherwise the paraxial model itself refuses the lens.
/// </para>
/// </summary>
public static class SeidelEngine
{
    /// <summary>
    /// The Seidel sums of <paramref name="lens"/> at 0-based wavelength row
    /// <paramref name="wavelength"/> (the primary when null). The chief ray is that of the largest
    /// radial field; a real-image-height field type is read as paraxial image height. Throws,
    /// naming the surface, for a surface type third-order theory does not cover here.
    /// </summary>
    public static SeidelResult Compute(SequentialLens lens, OpticalSystemData system, int? wavelength = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var notes = new List<string>();
        int last = lens.LastSurfaceIndex;
        if (last < 1)
            throw new ArgumentException("Seidel coefficients: the lens needs at least one surface between the object and the image.");

        // Fourth-order departure of every surface, refusing the types the theory does not cover.
        var departure = new double[lens.Surfaces.Count];
        for (int j = 1; j <= last; j++) departure[j] = FourthOrderDeparture(lens.Surfaces[j], j);

        // The paraxial model, with the chief ray at the largest radial field.
        var fields = system.Fields;
        if (fields.Normalization != FieldNormalization.Radial)
        {
            fields = fields with { Normalization = FieldNormalization.Radial };
            notes.Add("The chief ray is that of the largest radial field (the fields are normalised radially for the Seidel sums).");
        }
        if (fields.Type == FieldType.RealImageHeight)
        {
            fields = fields with { Type = FieldType.ParaxialImageHeight };
            notes.Add("Real image height fields are read as paraxial image heights (the paraxial chief ray is traced to that height).");
        }
        var data = system with { Fields = fields };
        var paraxialLens = lens.ToParaxialLens();
        int row = wavelength ?? data.Wavelengths.PrimaryIndex;
        var p = ParaxialSystem.Create(paraxialLens, data, row);
        if (p.FullField == 0)
            notes.Add("Every field is on the axis, so the chief ray is zero and only spherical aberration and axial colour can be non-zero.");

        // Colour: the shortest and the longest table wavelengths.
        var waves = data.Wavelengths.Wavelengths;
        int shortest = 0, longest = 0;
        for (int i = 1; i < waves.Count; i++)
        {
            if (waves[i].Micrometers < waves[shortest].Micrometers) shortest = i;
            if (waves[i].Micrometers > waves[longest].Micrometers) longest = i;
        }
        var deltaN = new double[lens.Surfaces.Count];
        (int, int)? colourRows = null;
        if (waves[shortest].Micrometers < waves[longest].Micrometers)
        {
            var ps = ParaxialSystem.Create(paraxialLens, data, shortest);
            var pl = ParaxialSystem.Create(paraxialLens, data, longest);
            for (int j = 0; j < deltaN.Length; j++) deltaN[j] = ps.SignedIndices[j] - pl.SignedIndices[j];
            colourRows = (shortest, longest);
        }
        else notes.Add("The wavelength table has one wavelength, so the colour sums CL and CT are zero.");

        var m = p.MarginalRay;
        var c = p.ChiefRay;
        var n = p.SignedIndices;
        double h = p.LagrangeInvariant;
        var surfaces = new List<SeidelSurface>(last);
        var sum = default(SeidelTerms);
        double petzval = 0;
        for (int j = 1; j <= last; j++)
        {
            var s = paraxialLens.Surfaces[j];
            if (s.ThinLensPower != 0 || s.SlopeMatrix is not null)
                throw new InvalidOperationException($"Seidel coefficients: surface {j} is an idealised surface (thin-lens power or ABCD matrix), which third-order theory does not cover.");
            double cv = s.Curvature;
            double nb = n[j - 1], na = n[j];
            double y = m.Heights[j], yb = c.Heights[j];
            double u = m.Slopes[j - 1], ua = m.Slopes[j], ub = c.Slopes[j - 1];
            double a = nb * (u + y * cv), ab = nb * (ub + yb * cv);
            double dun = ua / na - u / nb;
            double pz = cv * (1 / na - 1 / nb);
            double dn2 = 1 / (na * na) - 1 / (nb * nb);
            double s1 = -a * a * y * dun, s2 = -a * ab * y * dun, s3 = -ab * ab * y * dun, s4 = -h * h * pz;
            double s5 = -ab * (ab * ab * dn2 * y - (h + ab * y) * yb * pz);
            double drel = deltaN[j] / na - deltaN[j - 1] / nb;
            double cl = a * y * drel, ct = ab * y * drel;

            var aspheric = default(SeidelTerms);
            if (departure[j] != 0)
            {
                double d = 8 * departure[j] * (na - nb);
                aspheric = new SeidelTerms(d * y * y * y * y, d * y * y * y * yb, d * y * y * yb * yb, 0, d * y * yb * yb * yb, 0, 0);
            }
            var terms = new SeidelTerms(s1, s2, s3, s4, s5, cl, ct) + aspheric;
            surfaces.Add(new SeidelSurface(j, terms, aspheric));
            sum += terms;
            if (cv != 0) petzval += cv * (na - nb) / (na * nb);
        }

        double nImage = n[last];
        double inverse = -Math.Abs(nImage) * petzval;
        bool atInfinity = p.ImageAtInfinity;
        if (atInfinity)
            notes.Add("The marginal ray leaves parallel to the axis (afocal image space), so the transverse and longitudinal coefficients are undefined and not listed.");
        return new SeidelResult
        {
            Surfaces = surfaces, Sum = sum, Wavelength = row, WavelengthMicrometers = p.WavelengthMicrometers, ColourRows = colourRows,
            ImageReducedSlope = m.ReducedSlopes[last], ImageSlope = m.Slopes[last], ImageIndex = nImage,
            LagrangeInvariant = h, FullField = p.FullField, PetzvalRadius = inverse == 0 ? double.PositiveInfinity : 1 / inverse,
            ImageAtInfinity = atInfinity, Notes = notes,
        };
    }

    /// <summary>
    /// G: the coefficient of r⁴ in the sag of <paramref name="surface"/> minus c_p³/8, the r⁴
    /// coefficient of the sphere of its paraxial curvature c_p (1/m³). Derived: the conic
    /// c r²/(1 + √(1 − (1 + k)c²r²)) = c r²/2 + (1 + k)c³r⁴/8 + O(r⁶), so a Standard surface has
    /// G = k c³/8; a power series adds its r² coefficient a₂ to c_p/2 and its r⁴ coefficient a₄ to
    /// the r⁴ term, so G = (1 + k)c³/8 + a₄ − (c + 2a₂)³/8. Zero for ignored surfaces and
    /// coordinate breaks (the paraxial lens takes them as planes). Throws for the types the theory
    /// does not cover (see <see cref="SeidelEngine"/>).
    /// </summary>
    public static double FourthOrderDeparture(SequentialSurface surface, int index)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.IsIgnored || surface.IsCoordinateBreak) return 0;
        double c = surface.Curvature, k = surface.Conic;
        double conic4 = (1 + k) * c * c * c / 8;
        double a2, a4;
        switch (surface.Shape)
        {
            case null:
                return k * c * c * c / 8;
            case SagSurfaceShape { UsesAlternateRoot: true } alternate:
                throw Refuse(index, alternate.TypeName);
            case EvenAsphere even:
                a2 = Term(even.Alpha, 0);
                a4 = Term(even.Alpha, 1);
                break;
            case OddAsphere odd:
                RefuseOddTerms(index, odd.TypeName, Term(odd.Beta, 0), Term(odd.Beta, 2), "β1", "β3");
                a2 = Term(odd.Beta, 1);
                a4 = Term(odd.Beta, 3);
                break;
            case ExtendedAsphere ext:
            {
                double r2 = ext.NormalizationRadius * ext.NormalizationRadius;
                a2 = Term(ext.Alpha, 0) / r2;
                a4 = Term(ext.Alpha, 1) / (r2 * r2);
                break;
            }
            case ExtendedOddAsphere extOdd:
            {
                RefuseOddTerms(index, extOdd.TypeName, Term(extOdd.Alpha, 0), Term(extOdd.Alpha, 2), "the ρ¹ coefficient", "the ρ³ coefficient");
                double r2 = extOdd.NormalizationRadius * extOdd.NormalizationRadius;
                a2 = Term(extOdd.Alpha, 1) / r2;
                a4 = Term(extOdd.Alpha, 3) / (r2 * r2);
                break;
            }
            default:
                throw Refuse(index, surface.Shape.TypeName);
        }
        double cp = c + 2 * a2;
        return conic4 + a4 - cp * cp * cp / 8;
    }

    private static double Term(IReadOnlyList<double> values, int i) => i < values.Count ? values[i] : 0;

    private static void RefuseOddTerms(int index, string type, double first, double third, string firstName, string thirdName)
    {
        if (first != 0 || third != 0)
            throw new InvalidOperationException(
                $"Seidel coefficients: surface {index} ({type}) has {(first != 0 ? firstName : thirdName)} ≠ 0; an r or r³ term has no place in third-order (Seidel) theory, which expands the sag in r² and r⁴. Set it to zero or leave the Seidel analyses out for this lens.");
    }

    private static InvalidOperationException Refuse(int index, string type) => new(
        $"Seidel coefficients: surface {index} is a {type} surface; the Seidel sums are computed only for the Standard, Even Asphere, Odd Asphere, Extended Asphere and Extended Odd Asphere surfaces (the types OpticStudio's help lists), whose sag is a power series in r about the axis.");
}
