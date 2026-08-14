using OpenSim.Core.Model;
using OpenSim.Solvers.Environment;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates on the convection correlations themselves.
///
/// The correlations are empirical fits carrying ±10–20% against experiment. That band is a
/// property of the physics and is documented, not spent as test slack. What IS gated here:
/// the formulas reproduce their published expressions to machine precision, the identities
/// the family guarantees hold exactly (branch continuity, average = twice local), and each
/// correlation agrees with an INDEPENDENT correlation of the same experiments to a
/// disagreement this file measures and records.
/// </summary>
public class ConvectionCorrelationTests
{
    [Theory]
    [InlineData(1e4, 0.71)]
    [InlineData(1e7, 0.71)]
    [InlineData(1e10, 7.0)]
    [InlineData(1e2, 100.0)]
    public void VerticalPlate_ReproducesTheChurchillChuExpression(double rayleigh, double prandtl)
    {
        // Hand-written from the published form, independently of the implementation.
        double bracket = Math.Pow(1 + Math.Pow(0.492 / prandtl, 0.5625), 8.0 / 27.0);
        double expected = Math.Pow(0.825 + 0.387 * Math.Pow(rayleigh, 1.0 / 6.0) / bracket, 2);

        Assert.Equal(expected, ConvectionCorrelations.NaturalVerticalPlate(rayleigh, prandtl), 12);
    }

    [Fact]
    public void VerticalPlate_AgreesWithTheLaminarOnlyFormWithinItsDocumentedPenalty()
    {
        // Churchill & Chu publish two forms: one valid over ALL Ra (used by the solver, so no
        // branch has to be crossed mid-transient) and a laminar-only one that fits better
        // below Ra ≈ 1e9. Over the range where the flow really is laminar they must agree to
        // within the documented price of the single expression — measured worst case 6.7%,
        // best 2.3% around Ra = 1e6.
        double worst = 0;
        for (double exponent = 4; exponent <= 7; exponent += 0.5)
        {
            double ra = Math.Pow(10, exponent);
            double all = ConvectionCorrelations.NaturalVerticalPlate(ra, 0.71);
            double laminar = ConvectionCorrelations.NaturalVerticalPlateLaminar(ra, 0.71);
            worst = Math.Max(worst, Math.Abs(all - laminar) / laminar);
        }
        Assert.InRange(worst, 0.02, 0.10);

        // Approaching the transition the all-Ra form pulls AWAY and upward — it is already
        // blending turbulence in where the laminar-only fit still cannot. The direction of
        // the divergence is the check; its size (33% at Ra = 1e9) is why the laminar-only
        // form is kept in the file as a cross-check rather than used.
        foreach (double ra in new[] { 1e8, 1e9 })
            Assert.True(ConvectionCorrelations.NaturalVerticalPlate(ra, 0.71)
                        > ConvectionCorrelations.NaturalVerticalPlateLaminar(ra, 0.71));
    }

    [Fact]
    public void HorizontalPlateBranches_MeetExactlyWhereTheMaximumSwaps()
    {
        // 0.54·Ra^¼ = 0.15·Ra^⅓ at Ra = (0.54/0.15)^12: taking the maximum of the two
        // branches makes the correlation continuous there by construction, with no fitted
        // blending constant to get wrong.
        double crossing = Math.Pow(0.54 / 0.15, 12);
        double laminar = 0.54 * Math.Pow(crossing, 0.25);
        double turbulent = 0.15 * Math.Cbrt(crossing);
        Assert.Equal(laminar, turbulent, 9);

        Assert.Equal(0.54 * Math.Pow(crossing / 10, 0.25),
            ConvectionCorrelations.NaturalHorizontalPlateBuoyant(crossing / 10), 12);
        Assert.Equal(0.15 * Math.Cbrt(crossing * 10),
            ConvectionCorrelations.NaturalHorizontalPlateBuoyant(crossing * 10), 12);
    }

    [Fact]
    public void HorizontalPlate_TrappedSideIsAboutHalfThePlumeSide()
    {
        // The physical content of the pair: a hot plate facing DOWN traps its plume and
        // convects roughly half as well as the same plate facing up.
        double ratio = ConvectionCorrelations.NaturalHorizontalPlateStagnant(1e6)
                       / ConvectionCorrelations.NaturalHorizontalPlateBuoyant(1e6);
        Assert.Equal(0.27 / 0.54, ratio, 12);
    }

    [Fact]
    public void HorizontalCylinder_AgreesWithMorganWithinTheSpreadOfTwoFits()
    {
        // Churchill–Chu against Morgan's power law over Morgan's stated range: two
        // independent fits of the same experiments, so their disagreement IS the honest
        // uncertainty of the correlation. Measured worst case 0.11.
        double worst = 0;
        for (double exponent = 4; exponent <= 7; exponent += 0.5)
        {
            double ra = Math.Pow(10, exponent);
            double churchill = ConvectionCorrelations.NaturalHorizontalCylinder(ra, 0.71);
            double morgan = ConvectionCorrelations.NaturalHorizontalCylinderMorgan(ra);
            worst = Math.Max(worst, Math.Abs(churchill - morgan) / morgan);
        }
        Assert.InRange(worst, 0.02, 0.15);
    }

    [Fact]
    public void FlatPlate_LaminarAverageIsExactlyTwiceTheLocalValue()
    {
        // h_x ∝ x^(−½) in a laminar boundary layer, so its average over 0…x is exactly twice
        // the local value at x. An exact property of the family, gated as one.
        foreach (double reynolds in new[] { 1e3, 1e4, 1e5, 4.9e5 })
        {
            double average = ConvectionCorrelations.ForcedFlatPlate(reynolds, 0.71);
            double local = ConvectionCorrelations.ForcedFlatPlateLocalLaminar(reynolds, 0.71);
            Assert.Equal(2 * local, average, 12);
        }
    }

    [Fact]
    public void FlatPlate_BranchesMeetAtTheTransitionReynoldsNumber()
    {
        double re = ConvectionCorrelations.TransitionReynolds;
        double laminar = ConvectionCorrelations.ForcedFlatPlate(re, 0.71);
        double turbulent = ConvectionCorrelations.ForcedFlatPlate(re * (1 + 1e-15), 0.71);
        Assert.Equal(1.0, turbulent / laminar, 9);

        // The textbook rounds the matching constant to 871; keeping it unrounded is what
        // makes the branches meet to the last bit instead of jumping by 0.15%.
        Assert.Equal(871.0, ConvectionCorrelations.MixedPlateConstant, 0);
        Assert.NotEqual(871.0, ConvectionCorrelations.MixedPlateConstant);
    }

    [Fact]
    public void CylinderCrossflow_ReproducesTheChurchillBernsteinExpression()
    {
        const double re = 5e4, pr = 0.71;
        double expected = 0.3 + 0.62 * Math.Sqrt(re) * Math.Pow(pr, 1.0 / 3.0)
            / Math.Pow(1 + Math.Pow(0.4 / pr, 2.0 / 3.0), 0.25)
            * Math.Pow(1 + Math.Pow(re / 282000.0, 0.625), 0.8);
        Assert.Equal(expected, ConvectionCorrelations.ForcedCylinderCrossflow(re, pr), 10);
    }

    [Fact]
    public void MixedBlend_ReducesToEitherLimitAndBracketsTheLarger()
    {
        Assert.Equal(12.5, ConvectionCorrelations.BlendMixedConvection(12.5, 0), 12);
        Assert.Equal(4.25, ConvectionCorrelations.BlendMixedConvection(0, 4.25), 12);

        double blended = ConvectionCorrelations.BlendMixedConvection(9, 6);
        Assert.True(blended > 9, "the blend must exceed the larger contribution");
        Assert.True(blended < Math.Cbrt(2) * 9, "and must not exceed the equal-contribution bound");
        Assert.Equal(Math.Cbrt(2) * 7, ConvectionCorrelations.BlendMixedConvection(7, 7), 12);
    }

    [Theory]
    [InlineData(400.0, 300.0)]
    [InlineData(300.0, 400.0)]      // a COLD surface: the identity is not one-sided
    [InlineData(1200.0, 293.15)]
    public void RadiativeCoefficient_IsTheStefanBoltzmannLawFactored(double surface, double ambient)
    {
        // h_r·(T_s − T_a) ≡ εσ(T_s⁴ − T_a⁴) is an algebraic identity, not a linearization
        // about an operating point — which is why a converged fixed point solves the EXACT
        // nonlinear radiation problem.
        const double emissivity = 0.83;
        double h = ConvectionCorrelations.RadiativeFilmCoefficient(emissivity, surface, ambient);
        double exact = emissivity * ConvectionCorrelations.StefanBoltzmann
                       * (Math.Pow(surface, 4) - Math.Pow(ambient, 4));
        Assert.True(Math.Abs(h * (surface - ambient) - exact) <= Math.Abs(exact) * 1e-13,
            $"factored form gave {h * (surface - ambient):r}, Stefan–Boltzmann gives {exact:r}");
    }

    [Fact]
    public void RadiativeCoefficient_StaysPositiveWhenTheSurfaceSitsAtAmbient()
    {
        // The factored form has no zero at ΔT = 0 (unlike the flux it carries), so the
        // Robin term keeps the surface exchange positive-definite even at equilibrium.
        double h = ConvectionCorrelations.RadiativeFilmCoefficient(0.9, 300, 300);
        Assert.Equal(0.9 * ConvectionCorrelations.StefanBoltzmann * 600 * 180000, h, 12);
        Assert.True(h > 0);
    }

    [Fact]
    public void DimensionlessGroups_MatchTheirDefinitions()
    {
        var state = new FluidState(1.2, 1.8e-5, 0.026, 1005, 1.0 / 350.0);
        Assert.Equal(1.8e-5 * 1005 / 0.026, state.Prandtl, 12);
        Assert.Equal(1.8e-5 / 1.2, state.KinematicViscosity, 15);

        double reynolds = ConvectionCorrelations.Reynolds(state, 2.5, 0.08);
        Assert.Equal(2.5 * 0.08 / state.KinematicViscosity, reynolds, 9);

        double rayleigh = ConvectionCorrelations.Rayleigh(state, 9.80665, 50, 0.08);
        double expected = 9.80665 * (1.0 / 350.0) * 50 * Math.Pow(0.08, 3)
                          / (state.KinematicViscosity * state.ThermalDiffusivity);
        Assert.Equal(expected, rayleigh, Math.Abs(expected) * 1e-12);
    }

    [Fact]
    public void Rayleigh_UsesTheMagnitudeOfBothDrivingSigns()
    {
        // Water below 4 °C has β < 0 and a cooled surface has ΔT < 0; the Rayleigh number
        // stays positive in both cases (the SIGN decides which way the plume runs, which is
        // the panel classifier's job, not this number's).
        var state = new FluidState(1000, 1.5e-3, 0.58, 4200, -3.0e-5);
        double negativeBeta = ConvectionCorrelations.Rayleigh(state, 9.80665, -20, 0.05);
        var mirrored = state with { ThermalExpansion = 3.0e-5 };
        Assert.True(negativeBeta > 0);
        Assert.Equal(ConvectionCorrelations.Rayleigh(mirrored, 9.80665, 20, 0.05), negativeBeta, 12);
    }

    [Fact]
    public void NusseltNumbers_RiseWithTheirDrivingGroup()
    {
        for (double exponent = 2; exponent < 11; exponent++)
        {
            double lo = Math.Pow(10, exponent), hi = Math.Pow(10, exponent + 1);
            Assert.True(ConvectionCorrelations.NaturalVerticalPlate(hi, 0.71)
                        > ConvectionCorrelations.NaturalVerticalPlate(lo, 0.71));
            Assert.True(ConvectionCorrelations.NaturalHorizontalPlateBuoyant(hi)
                        > ConvectionCorrelations.NaturalHorizontalPlateBuoyant(lo));
            Assert.True(ConvectionCorrelations.NaturalHorizontalCylinder(hi, 0.71)
                        > ConvectionCorrelations.NaturalHorizontalCylinder(lo, 0.71));
            Assert.True(ConvectionCorrelations.ForcedFlatPlate(hi, 0.71)
                        > ConvectionCorrelations.ForcedFlatPlate(lo, 0.71));
            Assert.True(ConvectionCorrelations.ForcedCylinderCrossflow(hi, 0.71)
                        > ConvectionCorrelations.ForcedCylinderCrossflow(lo, 0.71));
        }
    }

    [Fact]
    public void ValidityBand_NamesTheCorrelationTheQuantityAndTheRange()
    {
        var band = ConvectionCorrelations.HorizontalPlateStagnantBand;
        Assert.True(band.Contains(1e7));
        Assert.False(band.Contains(1e2));

        string note = band.Note(1e2);
        Assert.Contains("Horizontal plate", note);
        Assert.Contains("Ra", note);
        Assert.Contains("extrapolated", note);
    }

    [Fact]
    public void AirProperties_AreEvaluatedAtTheFilmTemperature()
    {
        // The film-property rule is worth a gate because getting it wrong is invisible: air's
        // conductivity moves ~14% over a 100 K excess, so evaluating at ambient instead would
        // shift h by several percent with nothing in the output to show for it.
        var ambient = FluidLibrary.Air.AtTemperature(300);
        var film = FluidLibrary.Air.AtTemperature(350);
        Assert.True(film.ThermalConductivity > ambient.ThermalConductivity * 1.1);

        // β = 1/T exactly for an ideal gas, not a tabulated approximation of it.
        Assert.Equal(1.0 / 350.0, film.ThermalExpansion, 15);
    }
}
