using OpenSim.Core.Model;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// The temperature (or velocity) range a correlation was fitted over. Outside it the
/// formulas here still evaluate — they are smooth — but the caller logs one note saying
/// so. Throwing mid-transient because one panel drifted past a band edge would be worse
/// than an extrapolated number the user is told about.
/// </summary>
public readonly record struct ValidityBand(string Correlation, string Quantity, double Min, double Max)
{
    /// <summary>Whether <paramref name="value"/> lies inside the fitted range.</summary>
    public bool Contains(double value) => value >= Min && value <= Max;

    /// <summary>The log note for an out-of-band evaluation.</summary>
    public string Note(double value) =>
        $"{Correlation}: {Quantity} = {value:g3} is outside the correlated range " +
        $"{Min:g2}…{Max:g2}; the correlation was extrapolated there.";
}

/// <summary>
/// The classical convection correlations, as pure functions of the dimensionless groups.
/// <para>
/// Every one of these is an EMPIRICAL fit carrying roughly ±10–20% against experiment —
/// that band is a property of the physics, and it belongs in the documentation and in the
/// solve log, not in the tolerance of a test. The tests here therefore gate the FORMULAS
/// to machine precision (they are exactly reproducible), gate the identities the family
/// guarantees (branch continuity, the average-equals-twice-local law), and cross-check
/// each correlation against an INDEPENDENT correlation of the same experiments with a
/// band that states the disagreement measured.
/// </para>
/// Sources: Churchill &amp; Chu (1975) for the plate and cylinder natural-convection forms,
/// McAdams for the horizontal-plate forms, Churchill &amp; Bernstein (1977) for cylinder
/// crossflow, as collected in Incropera &amp; DeWitt, <i>Fundamentals of Heat and Mass
/// Transfer</i>, chapters 7 and 9.
/// </summary>
public static class ConvectionCorrelations
{
    /// <summary>Stefan–Boltzmann constant σ [W/(m²·K⁴)] — exact under the 2019 SI, being a
    /// combination of the defined k_B, h and c.</summary>
    public const double StefanBoltzmann = 5.670374419e-8;

    /// <summary>The Reynolds number at which a flat-plate boundary layer is taken to trip.</summary>
    public const double TransitionReynolds = 5e5;

    /// <summary>
    /// The constant subtracted in the mixed laminar+turbulent flat-plate correlation.
    /// <para>
    /// Textbooks quote 871; that number IS this expression evaluated and rounded, and it is
    /// defined by requiring the laminar and turbulent branches to meet at
    /// <see cref="TransitionReynolds"/>. Keeping it unrounded costs nothing and makes the
    /// branches meet to the last bit, which a gate can then assert as an identity instead
    /// of a tolerance. The 0.08% difference from 871 is four orders of magnitude inside the
    /// correlation's own accuracy.
    /// </para>
    /// </summary>
    public static readonly double MixedPlateConstant =
        0.037 * Math.Pow(TransitionReynolds, 0.8) - 0.664 * Math.Sqrt(TransitionReynolds);

    /// <summary>Ra = gβ|ΔT|L³/(ν·α) — buoyancy over the diffusive resistances. |β| is used
    /// so that water below its density maximum (β &lt; 0) gives a positive Rayleigh number;
    /// the sign of β flips which SIDE the plume rises on, which the caller handles.</summary>
    public static double Rayleigh(FluidState fluid, double gravity, double temperatureDifference,
        double length) =>
        gravity * Math.Abs(fluid.ThermalExpansion) * Math.Abs(temperatureDifference)
        * length * length * length / (fluid.KinematicViscosity * fluid.ThermalDiffusivity);

    /// <summary>Re = U·L/ν.</summary>
    public static double Reynolds(FluidState fluid, double speed, double length) =>
        speed * length / fluid.KinematicViscosity;

    /// <summary>
    /// Churchill–Chu for a vertical plate, in the form valid over the WHOLE Rayleigh range
    /// (laminar through turbulent) so there is no branch to cross mid-transient. In the
    /// laminar range it sits a couple of percent below Churchill–Chu's laminar-only form,
    /// which is the documented price of the single expression.
    /// </summary>
    public static double NaturalVerticalPlate(double rayleigh, double prandtl)
    {
        double denominator = 1 + Math.Pow(0.492 / prandtl, 9.0 / 16.0);
        double root = 0.825 + 0.387 * Math.Pow(rayleigh, 1.0 / 6.0)
            / Math.Pow(denominator, 8.0 / 27.0);
        return root * root;
    }

    /// <summary>Churchill–Chu's laminar-only vertical plate form (Ra ≤ 1e9). Not used by the
    /// solver — it exists as the independent cross-check for <see cref="NaturalVerticalPlate"/>,
    /// which is the honest way to bound an empirical fit.</summary>
    public static double NaturalVerticalPlateLaminar(double rayleigh, double prandtl)
    {
        double denominator = 1 + Math.Pow(0.492 / prandtl, 9.0 / 16.0);
        return 0.68 + 0.670 * Math.Pow(rayleigh, 0.25) / Math.Pow(denominator, 4.0 / 9.0);
    }

    /// <summary>
    /// A horizontal plate on the side the plume LEAVES from — a hot plate facing up, or a
    /// cold plate facing down. The maximum of the laminar (0.54·Ra^¼) and turbulent
    /// (0.15·Ra^⅓) branches: they cross at Ra = (0.54/0.15)^12 ≈ 4.7e6, and taking the
    /// maximum makes the correlation continuous there by construction.
    /// </summary>
    public static double NaturalHorizontalPlateBuoyant(double rayleigh) =>
        Math.Max(0.54 * Math.Pow(rayleigh, 0.25), 0.15 * Math.Cbrt(rayleigh));

    /// <summary>A horizontal plate on the side the plume is TRAPPED against — a hot plate
    /// facing down, or a cold plate facing up. Convection there is weaker by roughly the
    /// factor 0.27/0.54 = ½ the correlation states.</summary>
    public static double NaturalHorizontalPlateStagnant(double rayleigh) =>
        0.27 * Math.Pow(rayleigh, 0.25);

    /// <summary>Churchill–Chu for a long horizontal cylinder in a quiescent fluid.</summary>
    public static double NaturalHorizontalCylinder(double rayleigh, double prandtl)
    {
        double denominator = 1 + Math.Pow(0.559 / prandtl, 9.0 / 16.0);
        double root = 0.60 + 0.387 * Math.Pow(rayleigh, 1.0 / 6.0)
            / Math.Pow(denominator, 8.0 / 27.0);
        return root * root;
    }

    /// <summary>Morgan's power-law correlation for the same geometry (C·Ra^n over
    /// 1e4 ≤ Ra ≤ 1e7). Like the laminar plate form it exists to CHECK
    /// <see cref="NaturalHorizontalCylinder"/> against an independent fit, not to be used.</summary>
    public static double NaturalHorizontalCylinderMorgan(double rayleigh) =>
        0.480 * Math.Pow(rayleigh, 0.250);

    /// <summary>
    /// Average Nusselt number of a flat plate in parallel flow: the Pohlhausen laminar
    /// result 0.664·Re^½·Pr^⅓ up to <see cref="TransitionReynolds"/>, and the mixed
    /// laminar-then-turbulent result (0.037·Re^⅘ − A)·Pr^⅓ above it. Valid for Pr ≥ 0.6.
    /// </summary>
    public static double ForcedFlatPlate(double reynolds, double prandtl) =>
        (reynolds <= TransitionReynolds
            ? 0.664 * Math.Sqrt(reynolds)
            : 0.037 * Math.Pow(reynolds, 0.8) - MixedPlateConstant) * Math.Cbrt(prandtl);

    /// <summary>
    /// A flat plate standing NORMAL to the stream: Nu_D = 0.228·Re_D^0.731·Pr^⅓, the
    /// Hilpert-form fit for a plate in cross flow (Incropera Table 7.3, "vertical plate").
    /// D is the plate's extent across the flow. It is an average over the whole plate —
    /// the stagnation face runs above it and the wake face below.
    /// </summary>
    public static double ForcedNormalPlate(double reynolds, double prandtl) =>
        0.228 * Math.Pow(reynolds, 0.731) * Math.Cbrt(prandtl);

    /// <summary>The LOCAL laminar Nusselt number 0.332·Re_x^½·Pr^⅓ at the same station.
    /// Because the laminar film coefficient falls as x^(−½), the average over 0…x is
    /// exactly twice the local value — an identity the tests use as a sharp gate.</summary>
    public static double ForcedFlatPlateLocalLaminar(double reynolds, double prandtl) =>
        0.332 * Math.Sqrt(reynolds) * Math.Cbrt(prandtl);

    /// <summary>Churchill–Bernstein for a cylinder in crossflow; valid for Re·Pr ≥ 0.2 and
    /// spanning the whole Reynolds range in one expression.</summary>
    public static double ForcedCylinderCrossflow(double reynolds, double prandtl)
    {
        double numerator = 0.62 * Math.Sqrt(reynolds) * Math.Cbrt(prandtl);
        double denominator = Math.Pow(1 + Math.Pow(0.4 / prandtl, 2.0 / 3.0), 0.25);
        return 0.3 + numerator / denominator
            * Math.Pow(1 + Math.Pow(reynolds / 282000.0, 5.0 / 8.0), 4.0 / 5.0);
    }

    /// <summary>
    /// Blends a forced and a natural contribution: (x_f³ + x_n³)^⅓, the standard
    /// combination rule for mixed convection.
    /// <para>
    /// It is applied to the FILM COEFFICIENTS, not to the Nusselt numbers. The forced and
    /// natural correlations of the same panel are written on different length scales (the
    /// streamwise run versus the plate height), so their Nusselt numbers are not commensurate;
    /// their h values are, and h is what the boundary condition needs.
    /// </para>
    /// </summary>
    public static double BlendMixedConvection(double forced, double natural) =>
        Math.Cbrt(forced * forced * forced + natural * natural * natural);

    /// <summary>
    /// The radiative film coefficient h_r = εσ(T_s+T_a)(T_s²+T_a²) [W/(m²·K)].
    /// <para>
    /// This is the FACTORED form of the Stefan–Boltzmann law, not a linearization about
    /// some operating point: h_r·(T_s − T_a) ≡ εσ(T_s⁴ − T_a⁴) is an algebraic identity at
    /// every temperature. So a Picard iteration that recomputes h_r from the current
    /// surface temperature and converges is solving the EXACT nonlinear radiation problem,
    /// with zero linearization error left over — which is why the solvers lag this
    /// coefficient rather than forming a Newton tangent.
    /// </para>
    /// Small-body-in-large-surroundings is assumed (the body sees only the ambient, never
    /// itself): inter-body view factors are a named deferred feature.
    /// </summary>
    public static double RadiativeFilmCoefficient(double emissivity, double surface, double ambient) =>
        emissivity * StefanBoltzmann * (surface + ambient) * (surface * surface + ambient * ambient);

    /// <summary>Fitted range of <see cref="NaturalVerticalPlate"/>.</summary>
    public static readonly ValidityBand VerticalPlateBand =
        new("Churchill–Chu vertical plate", "Ra", 1e-1, 1e12);

    /// <summary>Fitted range of <see cref="NaturalHorizontalPlateBuoyant"/> (the union of
    /// its laminar and turbulent branches).</summary>
    public static readonly ValidityBand HorizontalPlateBuoyantBand =
        new("Horizontal plate, plume side", "Ra", 1e4, 1e11);

    /// <summary>Fitted range of <see cref="NaturalHorizontalPlateStagnant"/>.</summary>
    public static readonly ValidityBand HorizontalPlateStagnantBand =
        new("Horizontal plate, trapped side", "Ra", 1e5, 1e10);

    /// <summary>Fitted range of <see cref="NaturalHorizontalCylinder"/>.</summary>
    public static readonly ValidityBand HorizontalCylinderBand =
        new("Churchill–Chu horizontal cylinder", "Ra", 1e-5, 1e12);

    /// <summary>Fitted range of <see cref="ForcedFlatPlate"/>.</summary>
    public static readonly ValidityBand FlatPlateBand =
        new("Flat plate in parallel flow", "Re", 0, 1e8);

    /// <summary>Fitted range of <see cref="ForcedNormalPlate"/>.</summary>
    public static readonly ValidityBand NormalPlateBand =
        new("Plate normal to the flow", "Re", 4e3, 1.5e4);

    /// <summary>Fitted range of <see cref="ForcedCylinderCrossflow"/> (in Re·Pr).</summary>
    public static readonly ValidityBand CylinderCrossflowBand =
        new("Churchill–Bernstein cylinder", "Re·Pr", 0.2, 1e10);
}
