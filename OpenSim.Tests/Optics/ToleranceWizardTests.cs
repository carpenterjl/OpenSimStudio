using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-66: the Tolerance Wizard (OpticStudio 2025 R2 help, "Tolerance Wizard", saved under
/// plans/sources/optics/op-64-help/; its dialog figure under op-66-help/images/) and the Tolerance
/// Summary. The expected rows follow the help's rules as the wizard's remarks quote them; the lens
/// data are arbitrary inputs.
/// </summary>
public sealed class ToleranceWizardTests
{
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    /// <summary>A cemented doublet (surfaces 1–3, constant indices), a dummy plane in air (4), and a plano-convex
    /// singlet of model glass nd 1.6, Vd 40 (5–6, surface 6 plano), image after surface 6.</summary>
    internal static OpticsSettings Lens() => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = 8,
            Fields = new() { new OpticsFieldSettings() },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Index = 1.5, IsStop = true },
            new() { Radius = -40, Thickness = 2, Index = 1.6 },
            new() { Radius = -200, Thickness = 10 },
            new() { Thickness = 5 },
            new() { Radius = 100, Thickness = 4, ModelGlass = new OpticsModelGlassSettings { Nd = 1.6, Vd = 40 } },
            new() { Thickness = 50 },
            new(),
        },
    };

    private static ToleranceOperandRow[] Of(ToleranceWizardResult r, string code) => r.Table.Rows.Where(x => x.Code == code).ToArray();

    [Fact]
    public void TheDefaults_AreTheHelpsFigure()
    {
        // The values the help's figure of the dialog shows (op-66-help/images/ToleranceWizard.png).
        var o = new ToleranceWizardOptions();
        Assert.Equal(0.2, o.RadiusValue);
        Assert.Equal(ToleranceRadiusUnit.LensUnits, o.RadiusUnit);
        Assert.Equal(0.2, o.ThicknessValue);
        Assert.Equal(0.2, o.DecenterXValue);
        Assert.Equal(0.2, o.TiltXValue);
        Assert.Equal(0.2, o.IrregularityFringes);
        Assert.Equal(0.2, o.ElementTiltXDegrees);
        Assert.Equal(0.001, o.IndexValue);
        Assert.Equal(1, o.AbbePercent);
        Assert.Equal(0.633, o.TestWavelength);
        Assert.Equal(1, o.StartAtRow);
        Assert.True(o.FocusCompensator);
        Assert.Null(o.FocusCompensatorRange);
    }

    /// <summary>The default rows on the lens above, rule by rule (help, "Tolerance Wizard" and "TTHI").</summary>
    [Fact]
    public void TheDefaultRows_FollowTheHelpsRules()
    {
        var lens = Lens();
        var r = ToleranceWizard.Generate(lens, new ToleranceTable(), NoCatalogs);
        Assert.Equal(1, r.FirstRow);
        Assert.Equal(r.Table.Rows.Count, r.RowCount);
        Assert.Empty(ToleranceValidation.Validate(r.Table, lens));

        var twav = Assert.Single(Of(r, "TWAV"));
        Assert.Equal(0.633, twav.Min);
        Assert.Same(twav, r.Table.Rows[0]);

        // Radius on the surfaces with power: not on the dummy plane 4 (air on both sides); the plano surface 6 in fringes.
        Assert.Equal(new[] { 1, 2, 3, 5 }, Of(r, "TRAD").Select(x => x.Int1));
        Assert.All(Of(r, "TRAD"), x => { Assert.Equal(0, x.Int2); Assert.Equal(-0.2, x.Min); Assert.Equal(0.2, x.Max); });
        Assert.Equal(6, Assert.Single(Of(r, "TFRN")).Int1);
        Assert.Contains(r.Notes, n => n.Contains("Surface 6 is plano"));

        // Thickness on every vertex separation, taken up by the first air space after it; the last has none.
        Assert.Equal(new[] { (1, 3), (2, 3), (3, 4), (4, 6), (5, 6), (6, 6) }, Of(r, "TTHI").Select(x => (x.Int1, x.Int2)));

        // Decenters, TIR tilts and irregularity on each Standard surface with power.
        foreach (var code in new[] { "TSDX", "TSDY", "TIRX", "TIRY", "TIRR" })
            Assert.Equal(new[] { 1, 2, 3, 5, 6 }, Of(r, code).Select(x => x.Int1));

        // Element tolerances on the two groups: the doublet 1–3 and the singlet 5–6.
        foreach (var code in new[] { "TEDX", "TEDY", "TETX", "TETY" })
            Assert.Equal(new[] { (1, 3), (5, 6) }, Of(r, code).Select(x => (x.Int1, x.Int2)));

        // Index on each glass; Abbe only on the model glass (1 % of Vd 40 = 0.4, derived); a constant index has none.
        Assert.Equal(new[] { 1, 2, 5 }, Of(r, "TIND").Select(x => x.Int1));
        var abb = Assert.Single(Of(r, "TABB"));
        Assert.Equal(5, abb.Int1);
        Assert.Equal(0.4, abb.Max, 15);
        Assert.Contains(r.Notes, n => n.Contains("Surface 1: a constant index has no Abbe number"));

        // The focus compensator on the thickness before the image, with no range given.
        var comp = Assert.Single(Of(r, "COMP"));
        Assert.Equal((6, 0, 0.0, 0.0), (comp.Int1, comp.Int2, comp.Min, comp.Max));
        Assert.Contains(r.Notes, n => n.Contains("Min = Max = 0"));
        Assert.Same(comp, r.Table.Rows[^1]);
    }

    [Fact]
    public void Units_StartAndStop_AndTheFocusRange_AreHonoured()
    {
        var r = ToleranceWizard.Generate(Lens(), new ToleranceTable(), NoCatalogs, new ToleranceWizardOptions
        {
            RadiusUnit = ToleranceRadiusUnit.Percent, RadiusValue = 0.5, TiltXUnit = ToleranceTiltUnit.Degrees, TiltYUnit = ToleranceTiltUnit.Degrees,
            StartSurface = 1, StopSurface = 3, FocusCompensatorRange = 2, Irregularity = false,
        });
        Assert.All(Of(r, "TRAD"), x => { Assert.Equal(1, x.Int2); Assert.Equal(0.5, x.Max); });
        Assert.Equal(new[] { 1, 2, 3 }, Of(r, "TSTX").Select(x => x.Int1));
        Assert.Empty(Of(r, "TIRX"));
        Assert.Empty(Of(r, "TIRR"));
        Assert.Equal(new[] { (1, 3) }, Of(r, "TEDX").Select(x => (x.Int1, x.Int2)));
        Assert.DoesNotContain(Of(r, "TIND"), x => x.Int1 > 3);
        var comp = Assert.Single(Of(r, "COMP"));
        Assert.Equal((-2.0, 2.0), (comp.Min, comp.Max));
        Assert.DoesNotContain(r.Notes, n => n.Contains("Min = Max = 0"));

        Assert.Contains("Start and Stop", Assert.Throws<ArgumentException>(() =>
            ToleranceWizard.Generate(Lens(), new ToleranceTable(), NoCatalogs, new ToleranceWizardOptions { StopSurface = 7 })).Message);
        Assert.Contains("Decenter X", Assert.Throws<ArgumentException>(() =>
            ToleranceWizard.Generate(Lens(), new ToleranceTable(), NoCatalogs, new ToleranceWizardOptions { DecenterXValue = -1 })).Message);
    }

    /// <summary>"Start At Row": 1 replaces the rows, n &gt; 1 keeps rows 1 to n − 1, −1 appends (help).</summary>
    [Fact]
    public void StartAtRow_ReplacesKeepsOrAppends()
    {
        var existing = new ToleranceTable(new[]
        {
            new ToleranceOperandRow { Code = "COMM", Comment = "first" },
            new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Min = -1, Max = 1 },
            new ToleranceOperandRow { Code = "TCON", Int1 = 1, Min = -1, Max = 1 },
        });
        var one = ToleranceWizard.Generate(Lens(), existing, NoCatalogs);
        var two = ToleranceWizard.Generate(Lens(), existing, NoCatalogs, new ToleranceWizardOptions { StartAtRow = 2 });
        var append = ToleranceWizard.Generate(Lens(), existing, NoCatalogs, new ToleranceWizardOptions { StartAtRow = -1 });
        int n = one.RowCount;
        Assert.Equal(n, one.Table.Rows.Count);
        Assert.Equal(1 + n, two.Table.Rows.Count);
        Assert.Equal("first", two.Table.Rows[0].Comment);
        Assert.Equal(2, two.FirstRow);
        Assert.Equal(3 + n, append.Table.Rows.Count);
        Assert.Equal(existing.Rows, append.Table.Rows.Take(3));
        Assert.Equal(4, append.FirstRow);
        Assert.Throws<ArgumentException>(() => ToleranceWizard.Generate(Lens(), existing, NoCatalogs, new ToleranceWizardOptions { StartAtRow = 5 }));
    }

    [Fact]
    public void WizardSettings_RoundTripEveryField_AndNameAnUnknownUnit()
    {
        var o = new ToleranceWizardOptions
        {
            Radius = false, RadiusUnit = ToleranceRadiusUnit.Fringes, RadiusValue = 1.5, Thickness = false, ThicknessValue = 0.05,
            DecenterX = false, DecenterXValue = 0.01, DecenterY = false, DecenterYValue = 0.02,
            TiltX = false, TiltXUnit = ToleranceTiltUnit.Degrees, TiltXValue = 0.03, TiltY = false, TiltYUnit = ToleranceTiltUnit.Degrees, TiltYValue = 0.04,
            Irregularity = false, IrregularityFringes = 0.25,
            ElementDecenterX = false, ElementDecenterXValue = 0.06, ElementDecenterY = false, ElementDecenterYValue = 0.07,
            ElementTiltX = false, ElementTiltXDegrees = 0.08, ElementTiltY = false, ElementTiltYDegrees = 0.09,
            Index = false, IndexValue = 0.0005, Abbe = false, AbbePercent = 0.8,
            StartAtRow = -1, TestWavelength = 0.5461, StartSurface = 2, StopSurface = 4, FocusCompensator = false, FocusCompensatorRange = 1.5,
        };
        var saved = ToleranceWizardSettings.ToSettings(o)!;
        var notes = new List<string>();
        Assert.Equal(o, ToleranceWizardSettings.FromSettings(saved, notes));
        Assert.Empty(notes);
        Assert.Null(ToleranceWizardSettings.ToSettings(new ToleranceWizardOptions()));
        Assert.Equal(new ToleranceWizardOptions(), ToleranceWizardSettings.FromSettings(null, notes));

        var back = ToleranceWizardSettings.FromSettings(new OpticsToleranceWizardSettings { RadiusUnit = "Microns" }, notes);
        Assert.Equal(ToleranceRadiusUnit.LensUnits, back.RadiusUnit);
        Assert.Contains(notes, n => n.Contains("'Microns'"));
        Assert.Equal(1.5, new OpticsToleranceSettings { Wizard = saved }.Wizard!.FocusCompensatorRange);
    }

    /// <summary>The Tolerance Summary names each tolerance in words, with no operand codes in the text (help,
    /// "Tolerance Summary": "it uses no mnemonics").</summary>
    [Fact]
    public void TheSummary_UsesWordsNotCodes()
    {
        var lens = Lens();
        var table = new ToleranceTable(new[]
        {
            new ToleranceOperandRow { Code = "TWAV", Min = 0.633 },
            new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Min = -0.2, Max = 0.2 },
            new ToleranceOperandRow { Code = "TTHI", Int1 = 1, Int2 = 3, Min = -0.05, Max = 0.1 },
            new ToleranceOperandRow { Code = "TETX", Int1 = 1, Int2 = 3, Min = -0.2, Max = 0.2 },
            new ToleranceOperandRow { Code = "COMP", Int1 = 6, Int2 = 0, Min = -1, Max = 1 },
        });
        var report = ToleranceSummary.Build(lens, table);
        var tolerances = report.Sections.Single(s => s.Heading == "Tolerances");
        Assert.Equal(3, tolerances.TableRows.Count);
        Assert.Equal(new[] { "2", "surface 1", "radius of curvature", "50", "± 0.2", "mm", "" }, tolerances.TableRows[0]);
        Assert.Equal("-0.05 / +0.1", tolerances.TableRows[1][4]);
        Assert.Equal("element of surfaces 1 to 3", tolerances.TableRows[2][1]);
        Assert.Contains("Thickness after surface 6 (mm), allowed change -1 to 1.", report.Sections.Single(s => s.Heading == "Compensators").Lines);
        string text = report.ToText();
        Assert.Contains("0.633 µm", text);
        foreach (var code in new[] { "TRAD", "TTHI", "TETX", "COMP", "TWAV" }) Assert.DoesNotContain(code, text);
    }
}
