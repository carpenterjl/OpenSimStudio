using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-30: every multi-configuration operand this build supports sets its target and reads the
/// same value back (the plan's gate: "every operand has a set/get round-trip test"); every one it
/// does not support fails with its code. The operand list and the integer codes are OpticStudio
/// 2025 R2 help, "Multi-Configuration Operands"
/// (plans/sources/optics/op-30-help/Multi_Configuration_Operands.html). The lens and the values
/// written are arbitrary inputs, chosen to differ from what the fixture holds and to be exact in
/// binary where the operand inverts them (CRVT 0.0625 = 1/16).
/// </summary>
public sealed class MultiConfigOperandTests
{
    /// <summary>A lens holding every kind of data the operands reach: a Standard stop with an
    /// aperture and tilts, an Even Asphere with a model glass, parameters and flags, a coordinate
    /// break with a return; two fields, three wavelengths.</summary>
    internal static OpticsSettings Fixture()
    {
        var system = SystemDataSettings.ToSettings(OpticalSystemData.Default) with
        {
            Fields = new() { new() { X = 0, Y = 0 }, new() { X = 0, Y = 10 } },
            Wavelengths = new() { new() { Micrometers = 0.486 }, new() { Micrometers = 0.587 }, new() { Micrometers = 0.656 } },
            PrimaryWavelength = 1,
        };
        return new OpticsSettings
        {
            SystemData = system,
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new()
                {
                    Radius = 50, Thickness = 5, Material = "N-BK7", SemiDiameter = 10, MechanicalSemiDiameter = 11, ChipZone = 0.5, Tce = 7.1,
                    Coating = "AR", Comment = "front", IsStop = true,
                    Aperture = new() { Type = SavedSurfaces.CircularAperture, MinRadius = 0, MaxRadius = 9 },
                    TiltBefore = new(), TiltAfter = new(),
                },
                new()
                {
                    Type = "Even Asphere", Radius = -40, Thickness = 30, ModelGlass = new() { Nd = 1.6, Vd = 40 },
                    Parameters = new() { new() { Number = 2, Value = 1e-5 } },
                    Flags = new()
                    {
                        new() { Column = SavedSurfaces.RadiusColumn, Kind = SavedSurfaces.PickupFlag, PickupSurface = 1, PickupScale = -1 },
                        new() { Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.SolveFlag, Solve = "Marginal ray height", SolveParameters = new() { 0, 0 } },
                        new() { Column = SavedSurfaces.ParameterColumn(2), Kind = SavedSurfaces.PickupFlag, PickupSurface = 1, PickupScale = 1 },
                    },
                    // OP-32: one composite add-on for CSDX–CSTZ.
                    AddOns = new() { new() { Type = "Even Asphere", Parameters = new() { new() { Number = 2, Value = 2e-6 } }, DecenterX = 0.25, TiltY = 1 } },
                },
                new() { Type = SurfaceTypes.CoordinateBreak, CoordinateReturn = new() { Mode = "None", Surface = 1 } },
                new(),
            },
        };
    }

    /// <summary>One case per supported operand with a target: code, arguments, value written.</summary>
    public static IEnumerable<object[]> Cases()
    {
        object[] N(string code, int a, int b, double v) => new object[] { code, a, b, v, "" };
        object[] T(string code, int a, string text) => new object[] { code, a, 0, double.NaN, text };
        // Surface data.
        yield return N("CRVT", 1, 0, 0.0625);
        yield return N("THIC", 1, 0, 7.5);
        yield return T("GLSS", 1, "F2");
        yield return N("CONN", 1, 0, -1.5);
        yield return N("SDIA", 1, 0, 12.5);
        yield return N("MCSD", 1, 0, 13);
        yield return N("CHZN", 1, 0, 0.25);
        yield return N("TCEX", 1, 0, 8.25);
        yield return T("COTN", 1, "MGF2");
        yield return T("MCOM", 1, "rear");
        // OP-32: composite add-on 1 of surface 2.
        yield return N("CSDX", 2, 1, 0.5);
        yield return N("CSDY", 2, 1, -0.75);
        yield return N("CSTX", 2, 1, 2.5);
        yield return N("CSTY", 2, 1, -3);
        yield return N("CSTZ", 2, 1, 45);
        yield return N("PRAM", 2, 3, 2.5e-7);
        for (int k = 1; k <= 8; k++) yield return N("PAR" + k, 2, 0, k * 0.125);
        yield return N("MIND", 2, 0, 1.7);
        yield return N("MABB", 2, 0, 45);
        yield return N("MDPG", 2, 0, 0.002);
        yield return N("IGNR", 1, 0, 1);
        yield return N("SDRW", 1, 0, 1);
        yield return N("SRTS", 1, 0, 1);
        yield return N("IGNM", 1, 2, 1);
        yield return N("STPS", 0, 0, 2);
        yield return N("APTP", 1, 0, 6);
        yield return N("APMN", 1, 0, 1.5);
        yield return N("APMX", 1, 0, 8.5);
        yield return N("APDX", 1, 0, 0.3);
        yield return N("APDY", 1, 0, -0.4);
        foreach (var p in new[] { "CB", "CA" })
        {
            yield return N(p + "DX", 1, 0, 0.1);
            yield return N(p + "DY", 1, 0, -0.2);
            yield return N(p + "TX", 1, 0, 1.5);
            yield return N(p + "TY", 1, 0, -2.5);
            yield return N(p + "TZ", 1, 0, 3.5);
            yield return N(p + "OR", 1, 0, 1);
        }
        yield return N("CROR", 3, 0, 2);
        yield return N("CRSR", 3, 0, 2);
        // Solve parameters: surface 2's radius and parameter 2 are pickups (surface, scale,
        // offset), its thickness a marginal-ray-height solve (its own parameters).
        yield return N("CSP1", 2, 0, 0);
        yield return N("CSP2", 2, 0, 0.5);
        yield return N("TSP1", 2, 0, 0.2);
        yield return N("TSP2", 2, 0, 0.7);
        yield return N("TSP3", 2, 0, 1.25);
        yield return N("PSP1", 2, 2, 0);
        yield return N("PSP2", 2, 2, 2);
        yield return N("PSP3", 2, 2, 0.125);
        // System data.
        yield return N("AFOC", 0, 0, 1);
        yield return N("TELE", 0, 0, 1);
        yield return N("APDF", 0, 0, 0.5);
        yield return N("APDT", 0, 0, 1);
        yield return N("SATP", 0, 0, 4);
        yield return N("APER", 0, 0, 3.5);
        yield return N("FLTP", 0, 0, 1);
        yield return N("RAAM", 0, 0, 2);
        yield return N("MTFU", 0, 0, 1);
        yield return N("TEMP", 0, 0, 25.5);
        yield return N("PRES", 0, 0, 0.5);
        yield return N("PSHX", 0, 0, 0.1);
        yield return N("PSHY", 0, 0, -0.1);
        yield return N("PSHZ", 0, 0, 0.2);
        yield return N("PSCX", 0, 0, 0.05);
        yield return N("PSCY", 0, 0, -0.05);
        yield return T("LTTL", 0, "Zoom");
        yield return N("PRWV", 0, 0, 2);
        yield return N("GPJX", 0, 0, 0.5);
        yield return N("GPJY", 0, 0, 0.75);
        yield return N("GPPX", 0, 0, 30);
        yield return N("GPPY", 0, 0, 45);
        yield return N("GPIU", 0, 0, 1);
        yield return N("XFIE", 2, 0, 1.5);
        yield return N("YFIE", 2, 0, 12);
        yield return N("FLWT", 2, 0, 0.5);
        yield return N("FVDX", 2, 0, 0.1);
        yield return N("FVDY", 2, 0, -0.1);
        yield return N("FVCX", 2, 0, 0.2);
        yield return N("FVCY", 2, 0, 0.3);
        yield return N("FTAN", 2, 0, 15);
        yield return N("FVAN", 2, 0, 20);
        yield return N("WAVE", 2, 0, 0.6);
        yield return N("WLWT", 2, 0, 0.3);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EverySupportedOperand_SetsItsTarget_AndReadsItBack(string code, int a, int b, double number, string text)
    {
        var op = MultiConfigOperands.Find(code)!;
        Assert.True(op.IsSupported);
        var value = op.Kind == MultiConfigValueKind.Text ? MultiConfigValue.Of(text) : MultiConfigValue.Of(number);
        var before = Fixture();
        Assert.NotEqual(value, op.Get(before, a, b));      // the case writes a new value
        var after = op.Set(before, a, b, 0, value);
        Assert.Equal(value, op.Get(after, a, b));
        Assert.Null(after.MultiConfiguration);
    }

    [Fact]
    public void TheCases_CoverEverySupportedOperandWithATarget()
    {
        var covered = Cases().Select(c => (string)c[0]).ToHashSet();
        var expected = MultiConfigOperands.Supported.Where(o => !o.HasNoTarget).Select(o => o.Code).ToHashSet();
        Assert.Equal(expected.Order(), covered.Order());
    }

    /// <summary>The help's table has 115 rows, one of them "GPEX, GPEY": 116 codes, the plan's count.</summary>
    [Fact]
    public void TheRegistry_HoldsEveryCodeOfTheHelpsTable()
    {
        Assert.Equal(116, MultiConfigOperands.All.Count);
        Assert.Equal(MultiConfigOperands.All.Count, MultiConfigOperands.All.Select(o => o.Code).Distinct().Count());
        foreach (var code in new[] { "AICN", "CPCN", "FCMM", "GCRS", "GPEX", "GPEY", "GQPO", "HLGV", "IGTO", "MCHI",
                                     "NCOM", "NCOT", "NGLS", "NPAR", "NPOS", "NPRO", "OPDR", "PXAR", "UDAF" })
            Assert.False(MultiConfigOperands.Find(code)!.IsSupported, code);
    }

    [Fact]
    public void AnUnsupportedOperand_FailsWithItsCode()
    {
        foreach (var op in MultiConfigOperands.All.Where(o => !o.IsSupported))
        {
            var ex = Assert.Throws<ArgumentException>(() => op.Set(Fixture(), 1, 1, 1, MultiConfigValue.Of(0)));
            Assert.Contains(op.Code, ex.Message);
            Assert.Contains("not supported", ex.Message);
        }
    }

    [Fact]
    public void ThicknessOfAnObjectAtInfinity_RoundTripsInfinity()
    {
        var op = MultiConfigOperands.Find("THIC")!;
        var finite = op.Set(Fixture(), 0, 0, 0, MultiConfigValue.Of(1000));
        Assert.False(finite.Surfaces![0].ThicknessIsInfinite);
        var infinite = op.Set(finite, 0, 0, 0, MultiConfigValue.Of(double.PositiveInfinity));
        Assert.True(infinite.Surfaces![0].ThicknessIsInfinite);
        Assert.Equal(double.PositiveInfinity, op.Get(infinite, 0).Number);
    }

    [Fact]
    public void SettingASolvedCell_DropsItsSolve_AndKeepsTheOthers()
    {
        var after = MultiConfigOperands.Find("THIC")!.Set(Fixture(), 2, 0, 0, MultiConfigValue.Of(12));
        var flags = after.Surfaces![2].Flags!;
        Assert.DoesNotContain(flags, f => f.Column == SavedSurfaces.ThicknessColumn);
        Assert.Contains(flags, f => f.Column == SavedSurfaces.RadiusColumn);
        Assert.Equal(12, after.Surfaces[2].Thickness);
    }

    [Fact]
    public void ApertureValue_UnderFloatByStopSize_IsTheStopsSemiDiameter()
    {
        var floating = MultiConfigOperands.Find("SATP")!.Set(Fixture(), 0, 0, 0, MultiConfigValue.Of(3));
        var after = MultiConfigOperands.Find("APER")!.Set(floating, 0, 0, 0, MultiConfigValue.Of(6.5));
        Assert.Equal(6.5, after.Surfaces![1].SemiDiameter);
        Assert.Equal(floating.SystemData!.ApertureValue, after.SystemData!.ApertureValue);
        Assert.Equal(6.5, MultiConfigOperands.Find("APER")!.Get(after).Number);
    }

    [Fact]
    public void ASpiderAperture_TakesArmWidthThenArmCount()
    {
        var spider = MultiConfigOperands.Find("APTP")!.Set(Fixture(), 1, 0, 0, MultiConfigValue.Of(3));
        Assert.Equal(SavedSurfaces.SpiderAperture, spider.Surfaces![1].Aperture!.Type);
        var after = MultiConfigOperands.Find("APMX")!.Set(spider, 1, 0, 0, MultiConfigValue.Of(4));
        Assert.Equal(4, after.Surfaces![1].Aperture!.ArmCount);
        Assert.Throws<ArgumentException>(() => MultiConfigOperands.Find("APMX")!.Set(spider, 1, 0, 0, MultiConfigValue.Of(2.5)));
    }

    [Theory]
    [InlineData("THIC", 9, 0)]      // no surface 9
    [InlineData("YFIE", 3, 0)]      // two fields
    [InlineData("WAVE", 0, 0)]      // wavelengths count from 1
    [InlineData("CROR", 1, 0)]      // not a coordinate break
    [InlineData("PRAM", 1, 1)]      // a Standard surface has no parameters
    [InlineData("TSP1", 1, 0)]      // surface 1's thickness has no solve
    [InlineData("APDX", 2, 0)]      // surface 2 has no aperture
    [InlineData("STPS", 0, 0)]      // the stop cannot be the object
    [InlineData("CSDX", 2, 2)]      // OP-32: surface 2 has one composite add-on
    [InlineData("CSTZ", 1, 1)]      // OP-32: surface 1 has none
    public void AMissingTarget_FailsNamingIt(string code, int a, int b)
    {
        var ex = Assert.Throws<ArgumentException>(() => MultiConfigOperands.Find(code)!.Set(Fixture(), a, b, 0, MultiConfigValue.Of(0)));
        Assert.StartsWith(code, ex.Message);
    }

    [Fact]
    public void AnOperandString_IsLimitedTo32Characters()
    {
        var ex = Assert.Throws<ArgumentException>(() => MultiConfigOperands.Find("LTTL")!.Set(Fixture(), 0, 0, 0, MultiConfigValue.Of(new string('x', 33))));
        Assert.Contains("32", ex.Message);
    }
}
