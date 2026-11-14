using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-91: thermal analysis of lenses — Make Thermal, the thermal pickup solves and the expansion
/// rules of OpticStudio's help (System Explorer › Environment pages, plans/sources/optics/op-91-help;
/// Make Thermal and Solve Types in op-30-help). The plan's gate: a singlet's focal shift with
/// temperature equals the closed-form thermo-optic expression f·(α − (dn/dT)/(n − 1)) in the
/// thin-lens limit. The glasses are GlassToolsTests' SCHOTT records, copied from the ZemaxGlass
/// AGF catalog with their source cited there; every radius, thickness, semi-diameter, TCE column
/// and temperature below is an arbitrary input.
/// </summary>
public sealed class ThermalAnalysisTests
{
    private static OpticalMaterial Bk7 => GlassToolsTests.Schott("N-BK7");
    private static OpticalMaterial Sf57 => GlassToolsTests.Schott("SF57");

    private static IReadOnlyList<OpticalCatalog> Catalogs(params OpticalMaterial[] glasses) =>
        new[] { new OpticalCatalog { Name = "schott", Materials = glasses } };

    private static OpticsConfigValueSettings V(double v) => new() { Value = v };

    private static OpticsConfigValueSettings Thermal(int reference) => new() { Solve = MultiConfiguration.ThermalPickup, PickupConfiguration = reference };

    // ------------------------------------------------------------------ the gate

    /// <summary>
    /// The plan's gate. An equiconvex N-BK7 singlet (R = 100 mm, centre thickness 10⁻³ mm, so
    /// thin) is made thermal at 0 and 40 °C about its nominal 20 °C, with the index adjusted to
    /// the environment (OP-13's TIE-19 model).
    /// <para>
    /// <b>Derivation.</b> A thin equiconvex lens has f = R/(2(n − 1)). Its radius expands with
    /// the glass, R(T) = R(1 + αΔT), and its index moves by Δn, so
    /// f(T)/f = (1 + αΔT)/(1 + Δn/(n − 1)); to first order Δf/f = αΔT − Δn/(n − 1), i.e.
    /// df/dT = f·(α − (dn/dT)/(n − 1)): the plan's expression. The remainder of the first-order
    /// form is exactly (1 + a)/(1 + b) − 1 − (a − b) = b(b − a)/(1 + b), with a = αΔT and
    /// b = Δn/(n − 1); that is the tolerance, plus the thickness's share (below). α is the glass
    /// record's catalog TCE and n, Δn come from the code's thermal model
    /// (<see cref="OpticsEnvironment.GlassIndex"/> at each configuration's temperature and the
    /// primary wavelength), so dn/dT here is the secant Δn/ΔT over the step — nothing is typed.
    /// </para>
    /// <para>
    /// The exact thick-lens power φ = (n − 1)(c₁ − c₂) + (n − 1)²t c₁c₂/n with every length
    /// scaled by 1 + αΔT is also checked to 10⁻¹² of f: the thermal configuration is the same lens
    /// built by hand (derived).
    /// </para>
    /// </summary>
    [Fact]
    public void Gate_ASingletsFocalShiftWithTemperature_IsTheThermoOpticExpression()
    {
        const double r = 100, t = 1e-3;
        var catalogs = Catalogs(Bk7);
        var settings = new OpticsSettings
        {
            SystemData = new OpticsSystemSettings { TemperatureCelsius = 20, AdjustIndexToEnvironment = true },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = r, Thickness = t, Material = "N-BK7", IsStop = true, SemiDiameter = 5 },
                new() { Radius = -r, Thickness = 95, SemiDiameter = 5, Tce = 23 },
                new(),
            },
        };
        var made = MultiConfigTools.MakeThermal(settings, new MakeThermalOptions { Configurations = 2, MinimumTemperature = 0, MaximumTemperature = 40 }, catalogs);
        var thermal = made.Settings;
        Assert.Equal(3, MultiConfiguration.ConfigurationCount(thermal));

        var nominal = MultiConfiguration.SettingsFor(thermal, 1, catalogs);
        double f0 = Efl(nominal, catalogs);
        double n0 = IndexOf(nominal);
        double alpha = Bk7.ThermalExpansion!.Value * 1e-6;   // the catalog TCE, 10⁻⁶/K (help: TCE)
        foreach (int k in new[] { 2, 3 })
        {
            var config = MultiConfiguration.SettingsFor(thermal, k, catalogs);
            double deltaT = config.SystemData!.TemperatureCelsius - 20;
            Assert.Equal(k == 2 ? -20 : 20, deltaT);
            double fk = Efl(config, catalogs);
            double nk = IndexOf(config);
            double dndT = (nk - n0) / deltaT;                   // the code's model, secant over the step
            double a = alpha * deltaT, b = dndT * deltaT / (n0 - 1);

            // The plan's expression, first order: Δf/f = (α − (dn/dT)/(n − 1))·ΔT.
            double predicted = (alpha - dndT / (n0 - 1)) * deltaT;
            double measured = fk / f0 - 1;
            // Thickness share: the thin-lens ratio differs from the thick one by δ = (n−1)t c₁c₂/(n(c₁−c₂))
            // relative, which itself changes by at most 4|δ|(|a| + |b|) between temperatures (derived).
            double delta = Math.Abs((n0 - 1) * t * (1 / r) * (-1 / r) / (n0 * (2 / r)));
            double tolerance = Math.Abs(b * (b - a) / (1 + b)) + 4 * delta * (Math.Abs(a) + Math.Abs(b)) + 1e-14;
            Assert.True(Math.Abs(measured - predicted) <= tolerance,
                $"ΔT = {deltaT}: Δf/f = {measured:R}, f(α − (dn/dT)/(n−1))ΔT/f = {predicted:R}, tolerance {tolerance:R}.");
            Assert.True(Math.Abs(predicted) > 100 * tolerance, "the focal shift must be resolved well above the tolerance");

            // The exact thick lens with every length scaled by 1 + αΔT (derived).
            double s = 1 + a;
            double c1 = 1 / (r * s), c2 = -1 / (r * s), ts = t * s;
            double phiK = (nk - 1) * (c1 - c2) + (nk - 1) * (nk - 1) * ts * c1 * c2 / nk;
            double phi0 = (n0 - 1) * (2 / r) + (n0 - 1) * (n0 - 1) * t * (1 / r) * (-1 / r) / n0;
            Assert.True(Math.Abs(fk / f0 - phi0 / phiK) <= 1e-12, $"ΔT = {deltaT}: f ratio {fk / f0:R} against the hand-built lens {phi0 / phiK:R}.");
        }
    }

    private static double Efl(OpticsSettings settings, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var lens = ProjectLens.From(settings, catalogs)!;
        var data = SystemDataSettings.FromSettings(settings.SystemData, new List<string>());
        return ParaxialSystem.Create(lens.ToParaxialLens(), data).EffectiveFocalLength!.Value;
    }

    /// <summary>N-BK7's index relative to the configuration's air at its primary wavelength.</summary>
    private static double IndexOf(OpticsSettings settings)
    {
        var data = SystemDataSettings.FromSettings(settings.SystemData, new List<string>());
        return data.Environment.GlassIndex(Bk7, data.Wavelengths.Primary.Micrometers);
    }

    // ------------------------------------------------------------------ Make Thermal

    private static OpticsSettings Doublet(double mountTce = 23) => new()
    {
        SystemData = new OpticsSystemSettings { TemperatureCelsius = 20, AdjustIndexToEnvironment = true },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Material = "N-BK7", IsStop = true, SemiDiameter = 10 },
            new() { Radius = -50, Thickness = 10, SemiDiameter = 10, MechanicalSemiDiameter = 11, Tce = mountTce },
            new() { Radius = -30, Thickness = 3, Material = "SF57", SemiDiameter = 9, MechanicalSemiDiameter = 9.5 },
            new() { Radius = 80, Thickness = 40, SemiDiameter = 9 },
            new(),
        },
    };

    [Fact]
    public void MakeThermal_DeletingTheTable_MakesANominalAndEqualTemperatureSteps_WithThermalPickups()
    {
        var made = MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { Configurations = 3, MinimumTemperature = -10, MaximumTemperature = 50 }, Catalogs(Bk7, Sf57));
        var table = made.Settings.MultiConfiguration!;
        Assert.Equal(4, table.ConfigurationCount);                      // help: "3 configurations ... for a total of 4"
        Assert.Equal(new[] { -10.0, 20, 50 }, MultiConfigTools.ThermalTemperatures(new MakeThermalOptions { Configurations = 3, MinimumTemperature = -10, MaximumTemperature = 50 }));
        var rows = table.Operands!;
        // Sorted by operand type: TEMP, PRES, then the curvatures, thicknesses and semi-diameters
        // of surfaces 1 to 4 (the image has none; surface 4's mechanical semi-diameter is automatic).
        Assert.Equal(new[] { "TEMP", "PRES", "CRVT", "CRVT", "CRVT", "CRVT", "THIC", "THIC", "THIC", "THIC", "SDIA", "SDIA", "SDIA", "SDIA", "MCSD", "MCSD" },
            rows.Select(o => o.Type).ToArray());
        Assert.Equal(new[] { 20.0, -10, 20, 50 }, rows[0].Values!.Select(v => v.Value).ToArray());
        Assert.Equal(new[] { 1.0, 1, 1, 1 }, rows[1].Values!.Select(v => v.Value).ToArray());   // "the same as the nominal pressure"
        foreach (var row in rows.Skip(2))
        {
            Assert.Null(row.Values![0].Solve);
            Assert.All(row.Values.Skip(1), v => { Assert.Equal(MultiConfiguration.ThermalPickup, v.Solve); Assert.Equal(1, v.PickupConfiguration); });
        }
        // Every configuration applies, and the nominal is the lens as entered.
        var catalogs = Catalogs(Bk7, Sf57);
        Assert.Equal(-50, MultiConfiguration.SettingsFor(made.Settings, 1, catalogs).Surfaces![2].Radius!.Value, 12);
        for (int k = 2; k <= 4; k++) MultiConfiguration.SettingsFor(made.Settings, k, catalogs);
        // The configuration at the nominal temperature is the nominal, value for value.
        var at20 = MultiConfiguration.SettingsFor(made.Settings, 3, catalogs);
        var nominal = MultiConfiguration.SettingsFor(made.Settings, 1, catalogs);
        for (int j = 1; j <= 4; j++)
        {
            Assert.Equal(nominal.Surfaces![j].Radius, at20.Surfaces![j].Radius);
            Assert.Equal(nominal.Surfaces[j].Thickness, at20.Surfaces[j].Thickness);
        }

        var bySurface = MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { Configurations = 1, MinimumTemperature = 40, MaximumTemperature = 40, SortBySurface = true }, catalogs);
        Assert.Equal(new[] { "TEMP", "PRES", "CRVT 1", "THIC 1", "SDIA 1", "CRVT 2", "THIC 2", "SDIA 2", "MCSD 2", "CRVT 3", "THIC 3", "SDIA 3", "MCSD 3", "CRVT 4", "THIC 4", "SDIA 4" },
            bySurface.Settings.MultiConfiguration!.Operands!.Select(o => o.Type is "TEMP" or "PRES" ? o.Type : $"{o.Type} {o.Number1}").ToArray());
    }

    [Fact]
    public void MakeThermal_KeepingTheTable_AddsEachNominalsTemperatures_AfterTheOriginals()
    {
        var zoom = Doublet() with
        {
            MultiConfiguration = new()
            {
                ConfigurationCount = 2,
                Operands = new() { new() { Type = "THIC", Number1 = 4, Values = new() { V(40), V(35) } }, new() { Type = "MCOM", Number1 = 1, Values = new() { new() { Text = "a" }, new() { Text = "b" } } } },
            },
        };
        var catalogs = Catalogs(Bk7, Sf57);
        var made = MultiConfigTools.MakeThermal(zoom, new MakeThermalOptions { Configurations = 2, MinimumTemperature = 0, MaximumTemperature = 40, KeepExisting = true, Pressure = 0.5 }, catalogs);
        var table = made.Settings.MultiConfiguration!;
        Assert.Equal(6, table.ConfigurationCount);                      // help: n + n × new = 2 + 2 × 2
        var rows = table.Operands!;
        // The existing rows keep their place, so pickups of them still name the right operands.
        Assert.Equal("THIC", rows[0].Type);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, rows[0].Values!.Select(v => v.Solve == MultiConfiguration.ThermalPickup ? v.PickupConfiguration : 0).ToArray());
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, rows[1].Values!.Select(v => v.Solve == MultiConfiguration.Pickup ? v.PickupConfiguration : 0).ToArray());
        var temp = rows.Single(o => o.Type == "TEMP");
        Assert.Equal(new[] { 20.0, 20, 0, 40, 0, 40 }, temp.Values!.Select(v => v.Value).ToArray());
        Assert.Equal(new[] { 1.0, 1, 0.5, 0.5, 0.5, 0.5 }, rows.Single(o => o.Type == "PRES").Values!.Select(v => v.Value).ToArray());
        Assert.DoesNotContain(rows, o => o.Type == "THIC" && o.Number1 == 4 && !ReferenceEquals(o, rows[0]));   // already in the table
        // Configuration 6 is nominal 2 at 40 °C: its comment picked up, its back thickness expanded from 35.
        var c6 = MultiConfiguration.SettingsFor(made.Settings, 6, catalogs);
        Assert.Equal("b", c6.Surfaces![1].Comment);
        Assert.NotEqual(35, c6.Surfaces[4].Thickness);
        Assert.Equal(35, MultiConfiguration.SettingsFor(made.Settings, 2, catalogs).Surfaces![4].Thickness);
    }

    [Fact]
    public void MakeThermal_RefusesWhatItCannotDo_ByName()
    {
        var catalogs = Catalogs(Bk7, Sf57);
        Assert.Contains("below the minimum", Assert.Throws<ArgumentException>(() =>
            MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { MinimumTemperature = 30, MaximumTemperature = 10 }, catalogs)).Message);
        Assert.Contains("absolute zero", Assert.Throws<ArgumentException>(() =>
            MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { MinimumTemperature = -300, MaximumTemperature = 10 }, catalogs)).Message);
        Assert.Contains("at least one", Assert.Throws<ArgumentException>(() =>
            MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { Configurations = 0 }, catalogs)).Message);
        Assert.Contains("160", Assert.Throws<ArgumentException>(() =>
            MultiConfigTools.MakeThermal(Doublet(), new MakeThermalOptions { Configurations = 160 }, catalogs)).Message);
        // A thickness under a Lens Data Editor solve gets no row, and the notes say so.
        var solved = Doublet();
        solved.Surfaces![4] = solved.Surfaces[4] with { Flags = new() { new() { Column = "Thickness", Kind = "Solve", Solve = "MarginalRayHeight" } } };
        var made = MultiConfigTools.MakeThermal(solved, new MakeThermalOptions(), catalogs);
        Assert.DoesNotContain(made.Settings.MultiConfiguration!.Operands!, o => o.Type == "THIC" && o.Number1 == 4);
        Assert.Contains(made.Notes, n => n.StartsWith("THIC 4:"));
    }

    // ------------------------------------------------------------------ the thermal pickup

    [Fact]
    public void AThermalPickup_AtTheReferenceTemperature_IsTheReferenceValue_WithoutCatalogs()
    {
        var t = Doublet() with
        {
            MultiConfiguration = new()
            {
                ConfigurationCount = 2,
                Operands = new() { new() { Type = "THIC", Number1 = 2, Values = new() { V(12.5), Thermal(1) } }, new() { Type = "CRVT", Number1 = 1, Values = new() { V(0.02), Thermal(1) } } },
            },
        };
        var c2 = MultiConfiguration.SettingsFor(t, 2);
        Assert.Equal(12.5, c2.Surfaces![2].Thickness);
        Assert.Equal(50, c2.Surfaces[1].Radius!.Value, 12);
    }

    [Fact]
    public void ThermalPickups_OfRadiiAndGlassThicknesses_ScaleByTheGlassExpansion()
    {
        var catalogs = Catalogs(Bk7, Sf57);
        var t = Doublet() with
        {
            MultiConfiguration = new()
            {
                ConfigurationCount = 2,
                Operands = new()
                {
                    new() { Type = "TEMP", Values = new() { V(20), V(70) } },
                    new() { Type = "CRVT", Number1 = 1, Values = new() { V(1.0 / 50), Thermal(1) } },
                    new() { Type = "CRVT", Number1 = 2, Values = new() { V(-1.0 / 50), Thermal(1) } },
                    new() { Type = "THIC", Number1 = 1, Values = new() { V(5), Thermal(1) } },
                    new() { Type = "SDIA", Number1 = 3, Values = new() { V(9), Thermal(1) } },
                    new() { Type = "CONN", Number1 = 1, Values = new() { V(-0.5), Thermal(1) } },
                },
            },
        };
        var values = MultiConfiguration.ValuesIn(t, 2, catalogs);
        Assert.All(values, v => Assert.Null(v.Error));
        double sBk7 = 1 + Bk7.ThermalExpansion!.Value * 1e-6 * 50, sSf57 = 1 + Sf57.ThermalExpansion!.Value * 1e-6 * 50;
        Assert.Equal(70, values[0].Value.Number);
        Assert.Equal(1.0 / 50 / sBk7, values[1].Value.Number, 15);     // L' = L(1 + αΔT): curvature 1/(R s)
        Assert.Equal(-1.0 / 50 / sBk7, values[2].Value.Number, 15);    // air after N-BK7: the previous glass's TCE
        Assert.Equal(5 * sBk7, values[3].Value.Number, 13);
        Assert.Equal(9 * sSf57, values[4].Value.Number, 13);           // SF57 surface: its own glass
        Assert.Equal(-0.5, values[5].Value.Number);                     // help: All Other Values, a straight pickup
        // Without the record, the table alone cannot evaluate a length's thermal pickup.
        Assert.Contains("needs the lens", MultiConfiguration.ValuesIn(t.MultiConfiguration!, 2)[1].Error);
    }

    /// <summary>The radius rule (help: Radius of Curvature (CRVT) Values): a cemented interface
    /// takes the second glass, the air after it the second glass, a mirror after air its own TCE
    /// column, a mirror after glass that glass; a constant index its TCE column.</summary>
    [Fact]
    public void TheRadiusRule_PicksTheGlassOrTheColumn_AsTheHelpSays()
    {
        var catalogs = Catalogs(Bk7, Sf57);
        var s = new OpticsSettings
        {
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 50, Thickness = 5, Material = "N-BK7", Tce = 99 },
                new() { Radius = -40, Thickness = 2, Material = "SF57", Tce = 99 },
                new() { Radius = -90, Thickness = 20, Tce = 99 },
                new() { Radius = -200, Thickness = -20, Material = "MIRROR", Tce = 3 },
                new() { Radius = 30, Thickness = 4, Index = 1.6, Tce = 11 },
                new() { Radius = 30, Thickness = 4, Material = "N-BK7" },
                new() { Radius = 30, Thickness = 4, Material = "MIRROR", Tce = 5 },
                new(),
            },
        };
        double bk7 = Bk7.ThermalExpansion!.Value * 1e-6, sf57 = Sf57.ThermalExpansion!.Value * 1e-6;
        Assert.Equal(bk7, LensThermalModel.SurfaceExpansion(s, 1, catalogs).Alpha);
        Assert.Equal(sf57, LensThermalModel.SurfaceExpansion(s, 2, catalogs).Alpha);
        Assert.Equal(sf57, LensThermalModel.SurfaceExpansion(s, 3, catalogs).Alpha);
        Assert.Equal(3 * 1e-6, LensThermalModel.SurfaceExpansion(s, 4, catalogs).Alpha);   // the column times 10⁻⁶
        Assert.Equal(11 * 1e-6, LensThermalModel.SurfaceExpansion(s, 5, catalogs).Alpha);
        Assert.Equal(bk7, LensThermalModel.SurfaceExpansion(s, 7, catalogs).Alpha);
    }

    /// <summary>OQ-12: a glass linked to a mechanical material expands with the material's
    /// coefficient; a linked material without one fails by name; an unlinked glass without a
    /// catalog TCE fails by name.</summary>
    [Fact]
    public void AGlassLinkedToAMaterial_UsesTheMaterialsExpansion()
    {
        var catalogs = Catalogs(Bk7, Sf57);
        var material = new Material { Name = "Test body", YoungsModulus = 1e11, PoissonRatio = 0.3, Density = 2500, ThermalExpansionCoefficient = 2.5e-5 };
        var linked = Doublet() with { Thermal = new() { GlassMaterials = new() { new() { Glass = "schott:n-bk7", Material = material } } } };
        Assert.Equal(2.5e-5, LensThermalModel.SurfaceExpansion(linked, 1, catalogs).Alpha);
        Assert.Equal(2.5e-5, LensThermalModel.SurfaceExpansion(linked, 2, catalogs).Alpha);
        Assert.Equal(Sf57.ThermalExpansion!.Value * 1e-6, LensThermalModel.SurfaceExpansion(linked, 3, catalogs).Alpha);

        var noCoefficient = Doublet() with { Thermal = new() { GlassMaterials = new() { new() { Glass = "N-BK7", Material = material with { ThermalExpansionCoefficient = null } } } } };
        Assert.Contains("Test body", Assert.Throws<ArgumentException>(() => LensThermalModel.SurfaceExpansion(noCoefficient, 1, catalogs)).Message);

        var noTce = Catalogs(Bk7 with { ThermalExpansion = null }, Sf57);
        Assert.Contains("no TCE", Assert.Throws<ArgumentException>(() => LensThermalModel.SurfaceExpansion(Doublet(), 1, noTce)).Message);
    }

    /// <summary>
    /// The mount (help: Thickness (THIC) Values): the air space after surface 2 is a spacer of
    /// length E = T − Z₂(h₂) + Z₃(h₃) at the mechanical semi-diameters, expanding with surface 2's
    /// TCE column; its contacts move to min(h(1 + α_mΔT), h(1 + α_lensΔT)). The expected value is
    /// that construction with the sphere's sag z = cr²/(1 + √(1 − c²r²)) (derived), for a mount
    /// that expands more than both glasses (contacts at the lens edges) and less (contacts on the
    /// mount).
    /// </summary>
    [Theory]
    [InlineData(23.0)]
    [InlineData(1.0)]
    public void AnAirSpace_ExpandsAlongItsEdge_WithTheMountsTce(double mountTce)
    {
        var catalogs = Catalogs(Bk7, Sf57);
        var s = Doublet(mountTce);
        const double deltaT = 60, t = 10;
        double sa = 1 + Bk7.ThermalExpansion!.Value * 1e-6 * deltaT;   // surface 2: air after N-BK7
        double sb = 1 + Sf57.ThermalExpansion!.Value * 1e-6 * deltaT;  // surface 3: SF57
        double sm = 1 + mountTce * 1e-6 * deltaT;
        double ha = 11, hb = 9.5;                                      // the mechanical semi-diameters entered
        double ca = -1.0 / 50, cb = -1.0 / 30;
        double edge = t - Sag(ca, ha) + Sag(cb, hb);
        double haNew = Math.Min(ha * sm, ha * sa), hbNew = Math.Min(hb * sm, hb * sb);
        double expected = edge * sm + sa * Sag(ca, haNew / sa) - sb * Sag(cb, hbNew / sb);
        double got = LensThermalModel.Thickness(s, 2, t, deltaT, catalogs);
        Assert.True(Math.Abs(got - expected) <= 1e-12, $"thermal air space {got:R} mm against {expected:R} mm.");
        Assert.NotEqual(t * sm, got, 9);   // the edge is not the axis: the radii's change moves the centre
        Assert.Equal(t, LensThermalModel.Thickness(s, 2, t, 0, catalogs));
    }

    private static double Sag(double c, double r) => c * r * r / (1 + Math.Sqrt(1 - c * c * r * r));

    /// <summary>When every surface and the mount expand alike the whole lens scales, so the air
    /// space is T(1 + αΔT) (derived from the construction: every sag and edge scale by s).
    /// Constant-index lenses take their TCE columns (help: Adding TCE data); the semi-diameters
    /// are sized by rays.</summary>
    [Fact]
    public void WithEqualExpansionEverywhere_AnAirSpaceScalesLikeEverythingElse()
    {
        var s = new OpticsSettings
        {
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 40, Thickness = 6, Index = 1.6, IsStop = true, Tce = 10 },
                new() { Radius = -60, Thickness = 8, Tce = 10 },
                new() { Radius = -25, Thickness = 2, Index = 1.7, Tce = 10 },
                new() { Radius = 200, Thickness = 50, Tce = 10 },
                new(),
            },
        };
        const double deltaT = -45;
        double scale = 1 + 10e-6 * deltaT;
        double got = LensThermalModel.Thickness(s, 2, 8, deltaT, Array.Empty<OpticalCatalog>());
        Assert.True(Math.Abs(got - 8 * scale) <= 1e-12, $"{got:R} against {8 * scale:R}.");
    }

    [Fact]
    public void ThermalPickups_FailByName_WhereTheyCannotBeEvaluated()
    {
        var catalogs = Catalogs(Bk7, Sf57);
        OpticsSettings With(params OpticsConfigOperandSettings[] rows) => Doublet() with
        {
            MultiConfiguration = new() { ConfigurationCount = 2, Operands = rows.ToList() },
        };
        var temp = new OpticsConfigOperandSettings { Type = "TEMP", Values = new() { V(20), V(60) } };
        // No catalogs at a changed temperature.
        Assert.Contains("active catalogs", Assert.Throws<ArgumentException>(() =>
            MultiConfiguration.SettingsFor(With(temp, new() { Type = "THIC", Number1 = 1, Values = new() { V(5), Thermal(1) } }), 2)).Message);
        // The reference must be an earlier configuration.
        Assert.Contains("earlier configuration", Assert.Throws<ArgumentException>(() =>
            MultiConfiguration.SettingsFor(With(temp, new() { Type = "THIC", Number1 = 1, Values = new() { V(5), Thermal(2) } }), 2, catalogs)).Message);
        // Parameters: not built.
        Assert.Contains("not built", Assert.Throws<ArgumentException>(() =>
            MultiConfiguration.SettingsFor(With(temp, new() { Type = "PRAM", Number1 = 1, Number2 = 1, Values = new() { V(0), Thermal(1) } }), 2, catalogs)).Message);
        // Two TEMP rows are two environments in one configuration: not built.
        Assert.Contains("TEMP rows", Assert.Throws<ArgumentException>(() =>
            MultiConfiguration.SettingsFor(With(temp, temp, new() { Type = "THIC", Number1 = 1, Values = new() { V(5), Thermal(1) } }), 2, catalogs)).Message);
        // A pickup cannot read a thermal pickup of its own configuration.
        Assert.Contains("thermal pickup", Assert.Throws<ArgumentException>(() =>
            MultiConfiguration.SettingsFor(With(temp,
                new() { Type = "THIC", Number1 = 1, Values = new() { V(5), Thermal(1) } },
                new() { Type = "THIC", Number1 = 3, Values = new() { V(3), new() { Solve = MultiConfiguration.Pickup, PickupConfiguration = 2, PickupOperand = 2, PickupScale = 1 } } }), 2, catalogs)).Message);
    }

    [Fact]
    public void TheMaterialsExpansion_IsNamedWhenItDiffers()
    {
        var a = new Material { Name = "Body", YoungsModulus = 1e11, PoissonRatio = 0.3, Density = 2500, ThermalExpansionCoefficient = 2.5e-5 };
        Assert.Equal(new[] { "thermal expansion coefficient 2.5E-05 vs unset 1/K" },
            MaterialDivergence.Differences(a, a with { ThermalExpansionCoefficient = null }));
    }
}
