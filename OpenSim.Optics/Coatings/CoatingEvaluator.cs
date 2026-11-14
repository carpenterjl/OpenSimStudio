using System.Globalization;
using System.Numerics;

namespace OpenSim.Optics.Coatings;

/// <summary>A change applied to one definition line of a layer coating, or to all of them:
/// the coating multiplier scales the thickness (0–10; OpticStudio's help, "Optimizing Coatings",
/// gives 10 as the largest multiplier) and the offsets are added to the layer's n and k.</summary>
public sealed record CoatingLayerAdjustment(double Multiplier = 1, double IndexOffset = 0, double ExtinctionOffset = 0);

/// <summary>
/// What a coating is evaluated for: the media on either side, the wavelength and angle, the
/// primary wavelength that relative thicknesses refer to, the point on the surface for tapers,
/// and the coating multipliers and index offsets.
/// </summary>
public sealed record CoatingConditions
{
    /// <summary>The incident medium's index; must be real (k = 0).</summary>
    public Complex Incident { get; init; } = Complex.One;

    /// <summary>The substrate's index n + ik. Not used on a mirror: there the coating's last
    /// layer is the substrate, or, uncoated, <see cref="CoatingEvaluator.UncoatedMirrorIndex"/>.</summary>
    public Complex Substrate { get; init; } = Complex.One;

    /// <summary>True when the surface is a mirror: everything not reflected is absorbed.</summary>
    public bool IsMirror { get; init; }

    /// <summary>True to apply the layers in reverse order, as OpticStudio does on a surface
    /// going from glass to air (its help, "Specifying Coatings on Surfaces").</summary>
    public bool ReverseLayers { get; init; }

    /// <summary>The wavelength (µm) and the primary wavelength (µm) relative thicknesses use.</summary>
    public double Micrometers { get; init; }
    public double PrimaryMicrometers { get; init; }

    /// <summary>The angle of incidence in the incident medium, degrees, in [0, 90).</summary>
    public double AngleDegrees { get; init; }

    /// <summary>The surface point (lens units) and the surface's radius of curvature (lens
    /// units, infinite for a plane) at which tapers are evaluated.</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double SurfaceRadius { get; init; } = double.PositiveInfinity;

    /// <summary>Applied to every line of a layer coating.</summary>
    public CoatingLayerAdjustment All { get; init; } = new();

    /// <summary>Applied to the definition line of the same index (before loops are written
    /// out), on top of <see cref="All"/>: multipliers multiply, offsets add.</summary>
    public IReadOnlyList<CoatingLayerAdjustment>? Lines { get; init; }
}

/// <summary>
/// Evaluates a coating of a catalog — or an uncoated surface — for given conditions.
/// </summary>
public static class CoatingEvaluator
{
    /// <summary>The index OpticStudio gives an uncoated mirror: a thick layer of aluminium,
    /// 0.7 − 7.0i in its convention (OpticStudio 2025 R2 help, "Properties of Uncoated Surfaces"
    /// and "Defining Coatings"), which is 0.7 + 7.0i in this program's n + ik.</summary>
    public static readonly Complex UncoatedMirrorIndex = new(0.7, 7.0);

    /// <summary>The response of <paramref name="coating"/> (null: an uncoated surface) under
    /// <paramref name="conditions"/>. Throws, naming the coating and what is wrong, when it
    /// cannot be evaluated.</summary>
    public static CoatingResponse Evaluate(CoatingCatalog catalog, Coating? coating, CoatingConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(conditions);
        if (!(conditions.AngleDegrees >= 0 && conditions.AngleDegrees < 90))
            throw new ArgumentOutOfRangeException(nameof(conditions), conditions.AngleDegrees, "The angle of incidence must lie in [0°, 90°).");
        if (!(conditions.Micrometers > 0) || !double.IsFinite(conditions.Micrometers))
            throw new ArgumentOutOfRangeException(nameof(conditions), conditions.Micrometers, "The wavelength must be positive.");
        if (conditions.Incident.Imaginary != 0)
            throw new ArgumentException(
                $"The incident medium absorbs (k = {F(conditions.Incident.Imaginary)}); a coating is evaluated from a lossless incident medium only.", nameof(conditions));
        if (coating is not null && coating.Validate(catalog) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems));

        var response = coating switch
        {
            null => Layers(catalog, null, conditions),
            LayerCoating layers => Layers(catalog, layers, conditions),
            IdealCoating ideal => Ideal(ideal, conditions),
            Ideal2Coating ideal2 => Ideal2(ideal2, conditions),
            TableCoating table => Table(table, conditions),
            _ => throw new NotSupportedException($"Coating '{coating.Name}' is of a kind ({coating.GetType().Name}) this build cannot evaluate.")
        };
        if (!conditions.IsMirror) return response;
        // On a mirror nothing passes: what enters the substrate is absorbed in it.
        return new CoatingResponse(
            response.S with { A = response.S.A + response.S.T, T = 0 },
            response.P with { A = response.P.A + response.P.T, T = 0 });
    }

    /// <summary>The physical thickness (µm) of every layer, in the order the light meets them,
    /// with the substrate index; exposed for tests and the text listing.</summary>
    public static (IReadOnlyList<ThinFilmLayer> Layers, Complex Substrate) Stack(CoatingCatalog catalog, LayerCoating? coating, CoatingConditions conditions)
    {
        if (conditions.IsMirror && conditions.ReverseLayers)
            throw new ArgumentException("A mirror coating cannot be reversed: its last layer is the mirror's substrate.", nameof(conditions));
        var lines = coating?.Expand() ?? Array.Empty<(CoatingLayer, int)>();
        var stack = new List<ThinFilmLayer>(lines.Count);
        foreach (var (layer, line) in lines)
        {
            var material = catalog.FindMaterial(layer.Material)
                ?? throw new InvalidOperationException($"Coating '{coating!.Name}': material '{layer.Material}' is not defined in the catalog.");
            var adjust = Combined(conditions, line);
            double thickness = layer.Thickness;
            if (!layer.IsAbsolute)
            {
                if (!(conditions.PrimaryMicrometers > 0))
                    throw new InvalidOperationException(
                        $"Coating '{coating!.Name}', line {line + 1}: the thickness is in waves, which needs a primary wavelength; none was given.");
                double n0 = material.Index(conditions.PrimaryMicrometers).Real;
                thickness = conditions.PrimaryMicrometers * layer.Thickness / n0;
            }
            if (!(adjust.Multiplier >= 0 && adjust.Multiplier <= 10))
                throw new InvalidOperationException(
                    $"Coating '{coating!.Name}', line {line + 1}: the coating multiplier must lie in 0–10 (it is {F(adjust.Multiplier)}).");
            thickness *= adjust.Multiplier;
            if (layer.Taper is { Length: > 0 } taperName)
                thickness *= catalog.FindTaper(taperName)!.Factor(conditions.X, conditions.Y, conditions.SurfaceRadius);
            var index = material.Index(conditions.Micrometers) + new Complex(adjust.IndexOffset, adjust.ExtinctionOffset);
            if (index.Real <= 0 || index.Imaginary < 0)
                throw new InvalidOperationException(
                    $"Coating '{coating!.Name}', line {line + 1} ('{material.Name}'): with the index offsets the index is {F(index.Real)} + {F(index.Imaginary)}i; n must stay positive and k zero or positive.");
            stack.Add(new ThinFilmLayer(index, thickness));
        }
        if (conditions.ReverseLayers) stack.Reverse();

        Complex substrate = conditions.Substrate;
        if (conditions.IsMirror)
        {
            if (stack.Count == 0) substrate = UncoatedMirrorIndex;
            else
            {
                // On a mirror the coating's last layer is the substrate (help, "Specifying Coatings on Surfaces").
                substrate = stack[^1].Index;
                stack.RemoveAt(stack.Count - 1);
            }
        }
        return (stack, substrate);
    }

    private static CoatingLayerAdjustment Combined(CoatingConditions c, int line)
    {
        var all = c.All;
        if (c.Lines is not { } lines || line >= lines.Count || lines[line] is not { } own) return all;
        return new CoatingLayerAdjustment(all.Multiplier * own.Multiplier, all.IndexOffset + own.IndexOffset,
            all.ExtinctionOffset + own.ExtinctionOffset);
    }

    private static CoatingResponse Layers(CoatingCatalog catalog, LayerCoating? coating, CoatingConditions c)
    {
        var (stack, substrate) = Stack(catalog, coating, c);
        var result = ThinFilm.Compute(c.Incident.Real, stack, substrate, c.Micrometers, c.AngleDegrees * Math.PI / 180);
        return new CoatingResponse(FromFilm(result.S), FromFilm(result.P));
    }

    /// <summary>OpticStudio's phase is the argument in its n − ik convention: −arg of ours.</summary>
    private static CoatingPolarizationResponse FromFilm(ThinFilmAmplitudes a) =>
        new(a.Reflectance, a.Transmittance, a.Absorptance,
            CoatingResponse.Wrap(-a.R.Phase), CoatingResponse.Wrap(-a.T.Phase));

    private static bool IncidentHigher(CoatingConditions c) => !c.IsMirror && c.Incident.Real > c.Substrate.Real;

    /// <summary>True when the light is totally internally reflected at a bare interface with
    /// the substrate (real indices compared).</summary>
    private static bool TotalInternalReflection(CoatingConditions c) =>
        !c.IsMirror && c.Incident.Real * Math.Sin(c.AngleDegrees * Math.PI / 180) >= c.Substrate.Real;

    private static CoatingResponse Ideal(IdealCoating ideal, CoatingConditions c)
    {
        double reflectionPhase = c.IsMirror || !IncidentHigher(c) ? Math.PI : 0;
        if (TotalInternalReflection(c))
        {
            double tir = ideal.IsSimple ? 1 : ideal.TotalInternalReflection;
            var side = new CoatingPolarizationResponse(tir, 0, 1 - tir, reflectionPhase, 0);
            return new CoatingResponse(side, side);
        }
        var (t, r) = ideal.IsSimple ? (ideal.Transmission, 1 - ideal.Transmission) : ideal.Effective;
        var both = new CoatingPolarizationResponse(r, t, 1 - r - t, reflectionPhase, 0);
        return new CoatingResponse(both, both);
    }

    private static CoatingResponse Ideal2(Ideal2Coating ideal, CoatingConditions c)
    {
        bool flip = IncidentHigher(c) && !ideal.NoPiFlag;
        if (TotalInternalReflection(c))
        {
            double tir = ideal.TotalInternalReflection;
            var s0 = new CoatingPolarizationResponse(tir, 0, 1 - tir, Phase(flip ? -ideal.SReflection : ideal.SReflection), 0);
            var p0 = new CoatingPolarizationResponse(tir, 0, 1 - tir, Phase(flip ? -ideal.PReflection : ideal.PReflection), 0);
            return new CoatingResponse(s0, p0);
        }
        return new CoatingResponse(Side(ideal.SReflection, ideal.STransmission, flip), Side(ideal.PReflection, ideal.PTransmission, flip));

        static CoatingPolarizationResponse Side(Complex r, Complex t, bool flip)
        {
            if (flip) r = -r;
            double rr = r.Magnitude * r.Magnitude, tt = t.Magnitude * t.Magnitude;
            return new CoatingPolarizationResponse(rr, tt, 1 - rr - tt, Phase(r), Phase(t));
        }
    }

    /// <summary>The argument wrapped to (−π, π]; the argument of zero is zero.</summary>
    private static double Phase(Complex z) => z == Complex.Zero ? 0 : CoatingResponse.Wrap(z.Phase);

    private static CoatingResponse Table(TableCoating table, CoatingConditions c)
    {
        double angle = c.AngleDegrees;
        bool higher = IncidentHigher(c);
        if (higher)
        {
            // From high to low index the table is read at the refracted angle in the low-index medium.
            double sine = c.Incident.Real * Math.Sin(angle * Math.PI / 180) / c.Substrate.Real;
            if (sine >= 1)
            {
                var tir = new CoatingPolarizationResponse(1, 0, 0, Math.PI, 0);
                return new CoatingResponse(tir, tir);
            }
            angle = Math.Asin(sine) * 180 / Math.PI;
        }
        var row = Interpolate(table, angle, c.Micrometers);
        double extra = higher ? Math.PI : 0;
        const double deg = Math.PI / 180;
        var s = new CoatingPolarizationResponse(row.Rs, row.Ts, 1 - row.Rs - row.Ts,
            CoatingResponse.Wrap(row.PhaseRs * deg + extra), CoatingResponse.Wrap(row.PhaseTs * deg));
        var p = new CoatingPolarizationResponse(row.Rp, row.Tp, 1 - row.Rp - row.Tp,
            CoatingResponse.Wrap(row.PhaseRp * deg + extra), CoatingResponse.Wrap(row.PhaseTp * deg));
        return new CoatingResponse(s, p);
    }

    /// <summary>The table's row at <paramref name="angleDegrees"/> and <paramref name="micrometers"/>:
    /// linear in cos θ between angles (the end angles' data beyond them), linear in wavelength.</summary>
    public static CoatingTableRow Interpolate(TableCoating table, double angleDegrees, double micrometers)
    {
        var angles = table.Angles;
        if (angleDegrees <= angles[0].AngleDegrees) return AtWavelength(table, angles[0], micrometers);
        if (angleDegrees >= angles[^1].AngleDegrees) return AtWavelength(table, angles[^1], micrometers);
        int hi = 1;
        while (angles[hi].AngleDegrees < angleDegrees) hi++;
        var a = angles[hi - 1];
        var b = angles[hi];
        double ca = Math.Cos(a.AngleDegrees * Math.PI / 180), cb = Math.Cos(b.AngleDegrees * Math.PI / 180);
        double u = (Math.Cos(angleDegrees * Math.PI / 180) - ca) / (cb - ca);
        return Lerp(AtWavelength(table, a, micrometers), AtWavelength(table, b, micrometers), u);
    }

    private static CoatingTableRow AtWavelength(TableCoating table, CoatingTableAngle angle, double micrometers)
    {
        var rows = angle.Rows;
        double lo = rows[0].Micrometers, hi = rows[^1].Micrometers;
        if (rows.Count == 1 ? micrometers != lo : micrometers < lo || micrometers > hi)
            throw new ArgumentOutOfRangeException(nameof(micrometers), micrometers,
                $"Table coating '{table.Name}': {F(micrometers)} µm is outside its wavelengths, {F(lo)}–{F(hi)} µm. Table data are not extrapolated.");
        if (rows.Count == 1) return rows[0];
        int k = 1;
        while (k < rows.Count - 1 && rows[k].Micrometers < micrometers) k++;
        var a = rows[k - 1];
        var b = rows[k];
        return Lerp(a, b, (micrometers - a.Micrometers) / (b.Micrometers - a.Micrometers));
    }

    private static CoatingTableRow Lerp(CoatingTableRow a, CoatingTableRow b, double u)
    {
        double L(double x, double y) => u == 0 ? x : u == 1 ? y : x + u * (y - x);
        return new CoatingTableRow(L(a.Micrometers, b.Micrometers), L(a.Rs, b.Rs), L(a.Rp, b.Rp), L(a.Ts, b.Ts), L(a.Tp, b.Tp),
            L(a.PhaseRs, b.PhaseRs), L(a.PhaseRp, b.PhaseRp), L(a.PhaseTs, b.PhaseTs), L(a.PhaseTp, b.PhaseTp));
    }

    private static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
