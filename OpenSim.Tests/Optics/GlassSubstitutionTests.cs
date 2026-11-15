using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-59: the glass distance, the Glass Substitution Template, the RGLA and GCOS operands, the
/// model-glass variables and the model-to-real conversion. Catalog values are read from the
/// repository's fixture catalog (OpenSim.Tests/Optics/Fixtures/OP11_FIXTURE.agf), never typed;
/// expected values are derived here from the help's definitions (plans/sources/optics/op-59-help/).
/// The gate (an achromat recovering a crown–flint pair) is in Gates/GlassSubstitutionGateTests.
/// </summary>
public sealed class GlassSubstitutionTests
{
    private static OpticalCatalog Fixture() => AgfReader.Read(AgfReaderTests.FixturePath("OP11_FIXTURE.agf"));

    private static readonly double[] Fdc = { SpectralLines.F.Micrometers, SpectralLines.LowerD.Micrometers, SpectralLines.C.Micrometers };

    private static OpticsSystemSettings System(double epd = 10) => new()
    {
        ApertureValue = epd,
        Wavelengths = Fdc.Select(w => new OpticsWavelengthSettings { Micrometers = w }).ToList(),
        PrimaryWavelength = 2,
    };

    /// <summary>A thin singlet in air, glass named by <paramref name="material"/> or a model glass.</summary>
    private static OpticsSettings Singlet(string? material, OpticsModelGlassSettings? model = null, List<OpticsCellFlagSettings>? flags = null) => new()
    {
        SystemData = System(),
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 0.001, Material = material, ModelGlass = model, IsStop = true, Flags = flags },
            new() { Radius = -50, Thickness = 100 },
            new(),
        },
    };

    // ------------------------------------------------------------------ distance

    /// <summary>RGLA's distance (help: Optimizing Using Model Glasses) on two made-up points:
    /// d² = Wn·ΔNd² + Wa·ΔVd² + Wp·ΔPgF², here with ΔNd = 0.1, ΔVd = 10, ΔPgF = 0.01 and the
    /// default weights 1, 1e-4, 1e2: d² = 0.01 + 0.01 + 0.01, d = √0.03. Zero weights take the
    /// defaults; an unknown ΔPgF gives no distance unless Wp is zero.</summary>
    [Fact]
    public void TheDistance_IsTheHelpsWeightedRootSumOfSquares()
    {
        var m = new OpticalMaterial { Name = "x" };
        var a = new GlassPoint(m, 1.6, 40, 0.01, false);
        var b = new GlassPoint(m, 1.5, 50, 0.0, false);
        var (wn, wa, wp) = GlassDistance.Weights(0, 0, 0);
        Assert.Equal((GlassDistance.DefaultIndexWeight, GlassDistance.DefaultAbbeWeight, GlassDistance.DefaultPartialDispersionWeight), (wn, wa, wp));
        Assert.Equal(Math.Sqrt(0.03), GlassDistance.Distance(a, b, wn, wa, wp)!.Value, 15);
        Assert.Equal(Math.Sqrt(0.02), GlassDistance.Distance(a, b, wn, wa, 0)!.Value, 15);
        Assert.Null(GlassDistance.Distance(a with { DeltaPgF = null }, b, wn, wa, wp));
        Assert.Equal(Math.Sqrt(0.01 + 2 * 0.01 + 0.01), GlassDistance.Distance(a, b, 1, 2e-4, 100)!.Value, 15);
    }

    /// <summary>A glass's point is its printed nd and Vd (NM line) where the catalog prints them.</summary>
    [Fact]
    public void APoint_IsThePrintedNdAndVd()
    {
        var glass = Fixture().Find("SCHOTT-FORM")!;
        Assert.True(GlassDistance.TryPoint(glass, out var p, out _));
        Assert.Equal(glass.Nd!.Value, p!.Nd);
        Assert.Equal(glass.Vd!.Value, p.Vd);
    }

    // ------------------------------------------------------------------ template

    /// <summary>Each template test against the fixture's own OD and NM data (help: Glass
    /// Substitution Template): a limit at the glass's value passes, just below fails; zero ignores
    /// it; a missing datum passes unless incomplete data are excluded; the status boxes are
    /// additive; with the template off nothing is tested.</summary>
    [Fact]
    public void TheTemplate_TestsEachLimit_AgainstTheCatalogsOwnData()
    {
        var catalog = Fixture();
        var form = catalog.Find("SCHOTT-FORM")!;     // OD 1.0 2 0 1 2.3 2.3, status 0
        var bk7 = catalog.Find("SELLMEIER1-BK7")!;   // OD _ 1 0 1 - -: no cost, AR or PR
        Assert.NotNull(form.RelativeCost);
        Assert.Null(bk7.RelativeCost);

        var t = new OpticsGlassSubstitutionSettings { UseTemplate = true, MaxRelativeCost = form.RelativeCost };
        Assert.Null(GlassSubstitutionTemplate.Reject(t, form));
        Assert.Contains("relative cost", GlassSubstitutionTemplate.Reject(t with { MaxRelativeCost = form.RelativeCost * 0.99 }, form));
        Assert.Null(GlassSubstitutionTemplate.Reject(t with { MaxRelativeCost = 0 }, form));
        Assert.Null(GlassSubstitutionTemplate.Reject(t with { UseTemplate = false, MaxRelativeCost = form.RelativeCost * 0.99 }, form));

        Assert.Null(GlassSubstitutionTemplate.Reject(t, bk7));
        Assert.Contains("no relative cost", GlassSubstitutionTemplate.Reject(t with { ExcludeIncompleteData = true }, bk7));
        Assert.Null(GlassSubstitutionTemplate.Reject(t with { ExcludeIncompleteData = true }, bk7, excludeIncomplete: false));

        var resist = new OpticsGlassSubstitutionSettings { UseTemplate = true };
        Assert.Contains("climatic", GlassSubstitutionTemplate.Reject(resist with { MaxClimaticResistance = form.ClimaticResistance - 0.5 }, form));
        Assert.Contains("acid", GlassSubstitutionTemplate.Reject(resist with { MaxAcidResistance = form.AcidResistance - 0.5 }, form));
        Assert.Contains("alkali", GlassSubstitutionTemplate.Reject(resist with { MaxAlkaliResistance = form.AlkaliResistance - 0.5 }, form));
        Assert.Contains("phosphate", GlassSubstitutionTemplate.Reject(resist with { MaxPhosphateResistance = form.PhosphateResistance - 0.5 }, form));
        Assert.Null(GlassSubstitutionTemplate.Reject(resist with { MaxClimaticResistance = form.ClimaticResistance }, form));

        Assert.Equal(GlassStatus.Standard, form.Status);
        Assert.Equal(GlassStatus.Preferred, bk7.Status);
        var noStandard = resist with { AllowStandard = false };
        Assert.Contains("status", GlassSubstitutionTemplate.Reject(noStandard, form));
        Assert.Null(GlassSubstitutionTemplate.Reject(noStandard, bk7));
    }

    /// <summary>The candidates leave out a glass flagged Exclude Substitution (SELLMEIER2-FORM's NM
    /// line) and one whose range misses a wavelength (UV-ONLY, 0.3–0.45 µm, at F, d and C), and
    /// name a template catalog that is not active.</summary>
    [Fact]
    public void TheCandidates_LeaveOutExcludedAndOutOfRangeGlasses()
    {
        var catalog = Fixture();
        Assert.True(catalog.Find("SELLMEIER2-FORM")!.ExcludeSubstitution);
        var names = GlassSubstitutionTemplate.Candidates(new[] { catalog }, Fdc, null).Select(c => c.Glass.Name).ToArray();
        Assert.DoesNotContain("SELLMEIER2-FORM", names);
        Assert.DoesNotContain("UV-ONLY", names);
        Assert.Contains("SCHOTT-FORM", names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(GlassSubstitutionTemplate.Candidates(new[] { catalog }, Fdc, null), c => Assert.Equal($"{catalog.Name}:{c.Glass.Name}", c.MaterialText));

        var e = Assert.Throws<ArgumentException>(() => GlassSubstitutionTemplate.Candidates(new[] { catalog }, Fdc,
            new OpticsGlassSubstitutionSettings { Catalogs = new() { "HOYA" } }));
        Assert.Contains("'HOYA'", e.Message);
    }

    // ------------------------------------------------------------------ operands

    private static MeritEvaluation EvaluateOn(OpticsSettings optics, IReadOnlyList<OpticalCatalog> lensCatalogs,
        IReadOnlyList<OpticalCatalog>? operandCatalogs, params MeritOperandRow[] rows)
    {
        var lens = ProjectLens.From(optics, lensCatalogs)!;
        var system = SystemDataSettings.FromSettings(optics.SystemData, new List<string>());
        return MeritEvaluator.Evaluate(new MeritFunction(rows), new SingleMeritConfiguration(lens, system) { Catalogs = operandCatalogs });
    }

    /// <summary>GCOS is the catalog's relative cost; it fails for a glass without one and for air.</summary>
    [Fact]
    public void Gcos_IsTheCatalogsRelativeCost()
    {
        var catalog = Fixture();
        var form = catalog.Find("SCHOTT-FORM")!;
        var e = EvaluateOn(Singlet($"{catalog.Name}:SCHOTT-FORM"), new[] { catalog }, null,
            MeritOperandRow.Of("GCOS", 1, 0, 0, 1), MeritOperandRow.Of("GCOS", 2, 0, 0, 1));
        Assert.Equal(form.RelativeCost!.Value, e.Rows[0].Value);
        Assert.Equal(MeritRowStatus.Failed, e.Rows[1].Status);    // surface 2 is followed by air

        var none = EvaluateOn(Singlet($"{catalog.Name}:SELLMEIER1-BK7"), new[] { catalog }, null, MeritOperandRow.Of("GCOS", 1, 0, 0, 1));
        Assert.Contains("no relative cost", none.Rows[0].Error);
    }

    /// <summary>
    /// RGLA against a one-glass catalog: the lens's glass SCHOTT-FORM, the catalog only
    /// SELLMEIER3-FORM. d is the help's distance between the two points (written out here); with
    /// target 0 the value is d, with a target above d it is the target (the bound holds), and with
    /// the lens's own glass in the catalog it is the target. Without catalogs it fails.
    /// </summary>
    [Fact]
    public void Rgla_IsTheDistanceToTheNearestGlass_BeyondTheTarget()
    {
        var catalog = Fixture();
        var other = catalog.Find("SELLMEIER3-FORM")!;
        var pool = new[] { new OpticalCatalog { Name = "ONE", Materials = new[] { other } } };
        var optics = Singlet($"{catalog.Name}:SCHOTT-FORM");
        Assert.True(GlassDistance.TryPoint(catalog.Find("SCHOTT-FORM")!, out var a, out _));
        Assert.True(GlassDistance.TryPoint(other, out var b, out _));
        double dn = a!.Nd - b!.Nd, dv = a.Vd - b.Vd, dp = a.DeltaPgF!.Value - b.DeltaPgF!.Value;
        double d = Math.Sqrt(1.0 * dn * dn + 1e-4 * dv * dv + 1e2 * dp * dp);   // help defaults 1, 1E-04, 1E+02
        Assert.True(d > 0);

        var e = EvaluateOn(optics, new[] { catalog }, pool,
            MeritOperandRow.Of("RGLA", 0, 3, target: 0, weight: 1),
            MeritOperandRow.Of("RGLA", 0, 3, target: 2 * d, weight: 1),
            MeritOperandRow.Of("RGLA", 1, 1, target: 0, weight: 1, 2, 0, 0));
        Assert.Equal(d, e.Rows[0].Value, 14);
        Assert.Equal(2 * d, e.Rows[1].Value);
        Assert.Equal(Math.Sqrt(2.0 * dn * dn + 1e-4 * dv * dv + 1e2 * dp * dp), e.Rows[2].Value, 14);

        var own = EvaluateOn(optics, new[] { catalog }, new[] { catalog }, MeritOperandRow.Of("RGLA", 1, 1, target: 0, weight: 1));
        Assert.Equal(0, own.Rows[0].Value);

        var without = EvaluateOn(optics, new[] { catalog }, null, MeritOperandRow.Of("RGLA", 1, 1, target: 0, weight: 1));
        Assert.Contains("catalogs", without.Rows[0].Error);
        var air = EvaluateOn(optics, new[] { catalog }, pool, MeritOperandRow.Of("RGLA", 2, 3, target: 0, weight: 1));
        Assert.Contains("no glass", air.Rows[0].Error);
    }

    /// <summary>The project's configurations hand the operands their catalogs.</summary>
    [Fact]
    public void TheProjectConfigurations_CarryTheCatalogs()
    {
        var catalogs = new[] { Fixture() };
        Assert.Same(catalogs, new ProjectMeritConfigurations(Singlet($"{catalogs[0].Name}:SCHOTT-FORM"), catalogs).Catalogs);
        Assert.NotNull(MeritOperandRegistry.CreateDefault().Find("RGLA"));
        Assert.NotNull(MeritOperandRegistry.CreateDefault().Find("GCOS"));
    }

    // ------------------------------------------------------------------ variables

    /// <summary>Model-glass flags are variables of their own kinds; values read, apply and copy;
    /// the substitute flag is not a continuous variable; a model flag without a model glass is refused.</summary>
    [Fact]
    public void ModelGlassFlags_AreVariables()
    {
        var flags = new List<OpticsCellFlagSettings>
        {
            new() { Column = GlassVariables.ModelNdColumn, Kind = "Variable" },
            new() { Column = GlassVariables.ModelVdColumn, Kind = "Variable" },
            new() { Column = GlassVariables.ModelDeltaPgFColumn, Kind = "Variable" },
            new() { Column = GlassVariables.SubstituteColumn, Kind = "Variable" },
        };
        var optics = Singlet(null, new OpticsModelGlassSettings { Nd = 1.6, Vd = 40, DeltaPgF = 0.002 }, flags);
        var variables = LensVariables.Find(optics);
        Assert.Equal(new[] { LensVariableKind.ModelIndex, LensVariableKind.ModelAbbe, LensVariableKind.ModelPartialDispersion }, variables.Select(v => v.Kind));
        Assert.Equal(new[] { 1.6, 40, 0.002 }, LensVariables.Values(optics, variables));
        var changed = LensVariables.Apply(optics, variables, new[] { 1.7, 30, -0.001 });
        Assert.Equal(new OpticsModelGlassSettings { Nd = 1.7, Vd = 30, DeltaPgF = -0.001 }, changed.Surfaces![1].ModelGlass);
        Assert.Equal(optics.Surfaces![1].ModelGlass, LensVariables.CopyCells(optics, changed, variables).Surfaces![1].ModelGlass);
        Assert.Equal(new[] { 1 }, GlassVariables.SubstituteSurfaces(optics));

        var e = Assert.Throws<ArgumentException>(() => LensVariables.Find(Singlet("N-BK7", null, flags.Take(1).ToList())));
        Assert.Contains("no model glass", e.Message);
    }

    // ------------------------------------------------------------------ conversion

    /// <summary>A model glass at a catalog glass's own printed nd and Vd converts to that glass at
    /// distance 0; the model flags go; a template no glass meets is set aside and said so.</summary>
    [Fact]
    public void AModelGlass_ConvertsToTheNearestCatalogGlass()
    {
        var catalog = Fixture();
        var form = catalog.Find("SCHOTT-FORM")!;
        var nearest = ModelGlassConversion.Nearest(form.Nd!.Value, form.Vd!.Value, new[] { catalog }, Fdc, null);
        Assert.Same(form, nearest.Glass.Glass);
        Assert.Equal(0, nearest.Distance);
        Assert.Equal("", nearest.Note);

        var flags = new List<OpticsCellFlagSettings> { new() { Column = GlassVariables.ModelVdColumn, Kind = "Variable" }, new() { Column = "Radius", Kind = "Variable" } };
        var optics = Singlet(null, new OpticsModelGlassSettings { Nd = form.Nd.Value, Vd = form.Vd.Value }, flags);
        var converted = ModelGlassConversion.ConvertAll(optics, new[] { catalog });
        var s = converted.Converted.Surfaces![1];
        Assert.Equal($"{catalog.Name}:SCHOTT-FORM", s.Material);
        Assert.Null(s.ModelGlass);
        Assert.Equal(new[] { "Radius" }, s.Flags!.Select(f => f.Column));
        Assert.Single(converted.Items);

        // No status box ticked and a cost limit with incomplete data excluded: no fixture glass
        // passes (statuses 0–3 are refused; the one Melt glass, CONRADY-FORM, has no OD line, so
        // no cost). The help's next step allows incomplete data, and CONRADY-FORM then passes.
        Assert.Equal(GlassStatus.Melt, catalog.Find("CONRADY-FORM")!.Status);
        Assert.Null(catalog.Find("CONRADY-FORM")!.RelativeCost);
        var impossible = optics with { GlassSubstitution = new OpticsGlassSubstitutionSettings
        {
            UseTemplate = true, AllowStandard = false, AllowPreferred = false, AllowObsolete = false, AllowSpecial = false,
            ExcludeIncompleteData = true, MaxRelativeCost = 1e-6,
        } };
        var relaxed = ModelGlassConversion.ConvertAll(impossible, new[] { catalog });
        Assert.Contains("incomplete", relaxed.Items[0].Nearest.Note);
        Assert.Equal("CONRADY-FORM", relaxed.Items[0].Nearest.Glass.Glass.Name);

        Assert.Throws<InvalidOperationException>(() => ModelGlassConversion.Nearest(1.5, 60, new[] { catalog }, new[] { 20.0 }, null));
    }

    // ------------------------------------------------------------------ search refusals

    [Fact]
    public void TheSearch_RefusesALensWithNothingToSubstitute()
    {
        var catalogs = new[] { Fixture() };
        var e = Assert.Throws<ArgumentException>(() => GlassSubstitution.Run(Singlet($"{catalogs[0].Name}:SCHOTT-FORM"), catalogs));
        Assert.Contains("flagged for glass substitution", e.Message);
        var air = Assert.Throws<ArgumentException>(() => GlassSubstitution.Run(Singlet(null), catalogs, new GlassSubstitutionOptions { Surfaces = new[] { 1 } }));
        Assert.Contains("no glass to substitute", air.Message);
    }
}
