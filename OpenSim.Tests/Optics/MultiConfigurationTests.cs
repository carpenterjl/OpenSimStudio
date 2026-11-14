using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-30: the multi-configuration table applied to a system, its pickups, the editor's tools,
/// the analyses' current configuration, and the table in the files. The gate (plan, OP-30): a
/// two-position zoom defined by thickness operands gives each configuration's focal length equal
/// to the same lens entered as two single-configuration systems. Radii, thicknesses and indices
/// are arbitrary inputs; the comparison is between two routes to the same lens, so it is exact.
/// </summary>
public sealed class MultiConfigurationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("opensim-mce-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    /// <summary>A positive and a negative group in air (mm, constant indices), the gap after
    /// surface 2 and the back distance after surface 4 left to the caller.</summary>
    private static List<OpticsSurfaceSettings> Zoom(double gap, double back) => new()
    {
        new() { ThicknessIsInfinite = true },
        new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true },
        new() { Radius = -50, Thickness = gap },
        new() { Radius = -30, Thickness = 3, Index = 1.6 },
        new() { Radius = 80, Thickness = back },
        new(),
    };

    private static OpticsConfigValueSettings V(double v) => new() { Value = v };

    private static OpticsSettings ZoomTable() => new()
    {
        Surfaces = Zoom(gap: 99, back: 99),     // overridden in both configurations
        MultiConfiguration = new()
        {
            ConfigurationCount = 2,
            CurrentConfiguration = 1,
            Operands = new()
            {
                new() { Type = "THIC", Number1 = 2, Values = new() { V(10), V(25) } },
                new() { Type = "THIC", Number1 = 4, Values = new() { V(60), V(45) } },
            },
        },
    };

    private static double Efl(OpticsSettings settings)
    {
        var lens = ProjectLens.From(settings, NoCatalogs)!;
        var data = SystemDataSettings.FromSettings(settings.SystemData, new List<string>());
        return ParaxialSystem.Create(lens.ToParaxialLens(), data).EffectiveFocalLength!.Value;
    }

    [Fact]
    public void Gate_ATwoPositionZoomByThickness_HasTheFocalLengthsOfTheTwoSeparateLenses()
    {
        var table = ZoomTable();
        Assert.Equal(2, MultiConfiguration.ConfigurationCount(table));
        double efl1 = Efl(MultiConfiguration.SettingsFor(table, 1));
        double efl2 = Efl(MultiConfiguration.SettingsFor(table, 2));
        double separate1 = Efl(new OpticsSettings { Surfaces = Zoom(gap: 10, back: 60) });
        double separate2 = Efl(new OpticsSettings { Surfaces = Zoom(gap: 25, back: 45) });
        Assert.Equal(separate1, efl1);
        Assert.Equal(separate2, efl2);
        Assert.NotEqual(efl1, efl2);                       // the gap is a zoom: the focal length moves
        // The lens route agrees too: LensFor is the project lens of the configuration.
        var lens2 = MultiConfiguration.LensFor(table, 2, NoCatalogs)!;
        Assert.Equal(25e-3, lens2.Surfaces[2].Thickness, 15);
    }

    /// <summary>Wave 5 integration: the merit function (OP-54) switches configurations with CONF
    /// over the project's table. EFFL in configuration 1 and, after CONF 2, in configuration 2
    /// stand in the same ratio as the two separate lenses' focal lengths (a ratio, so the test
    /// does not depend on the unit EFFL reports in).</summary>
    [Fact]
    public void TheMeritFunction_SwitchesToEachConfigurationOfTheTable()
    {
        var table = ZoomTable();
        var configurations = new OpenSim.Optics.Merit.ProjectMeritConfigurations(table, NoCatalogs);
        Assert.Equal(2, configurations.Count);
        var function = new OpenSim.Optics.Merit.MeritFunction(new[]
        {
            OpenSim.Optics.Merit.MeritOperandRow.Of("EFFL", 0, 1, 0, 1, Array.Empty<double>()),
            OpenSim.Optics.Merit.MeritOperandRow.Of("CONF", 2, 0, 0, 0, Array.Empty<double>()),
            OpenSim.Optics.Merit.MeritOperandRow.Of("EFFL", 0, 1, 0, 1, Array.Empty<double>()),
        });
        var e = OpenSim.Optics.Merit.MeritEvaluator.Evaluate(function, configurations);
        double separate1 = Efl(new OpticsSettings { Surfaces = Zoom(gap: 10, back: 60) });
        double separate2 = Efl(new OpticsSettings { Surfaces = Zoom(gap: 25, back: 45) });
        double ratio = e.Rows[2].Value / e.Rows[0].Value;
        Assert.True(Math.Abs(ratio - separate2 / separate1) <= 1e-12 * Math.Abs(ratio), $"EFFL ratio {ratio} against {separate2 / separate1}.");
        Assert.Equal(25e-3, configurations.LensFor(2).Surfaces[2].Thickness, 15);
        Assert.Throws<ArgumentOutOfRangeException>(() => configurations.LensFor(3));
    }

    [Fact]
    public void SettingsFor_RemovesTheTable_AndLeavesTheInputUnchanged()
    {
        var table = ZoomTable();
        string before = JsonSerializer.Serialize(table);
        var config = MultiConfiguration.SettingsFor(table, 2);
        Assert.Null(config.MultiConfiguration);
        Assert.Equal(before, JsonSerializer.Serialize(table));
        // Without a table there is one configuration, the record itself.
        var single = new OpticsSettings { Surfaces = Zoom(10, 60) };
        Assert.Same(single, MultiConfiguration.SettingsFor(single, 1));
        Assert.Throws<ArgumentException>(() => MultiConfiguration.SettingsFor(single, 2));
    }

    [Fact]
    public void APickup_IsTargetTimesScalePlusOffset_FromAnEarlierCell()
    {
        var t = ZoomTable();
        var operands = t.MultiConfiguration!.Operands!;
        operands[1] = operands[1] with
        {
            Values = new() { V(60), new() { Solve = MultiConfiguration.Pickup, PickupConfiguration = 1, PickupOperand = 1, PickupScale = 2, PickupOffset = 1.5 } },
        };
        var config2 = MultiConfiguration.SettingsFor(t, 2);
        Assert.Equal(10 * 2 + 1.5, config2.Surfaces![4].Thickness);
    }

    [Fact]
    public void APickupFromALaterCell_FailsNamingTheCell()
    {
        var t = ZoomTable();
        var operands = t.MultiConfiguration!.Operands!;
        operands[0] = operands[0] with { Values = new() { new() { Solve = MultiConfiguration.Pickup, PickupConfiguration = 2, PickupOperand = 1 }, V(25) } };
        var ex = Assert.Throws<ArgumentException>(() => MultiConfiguration.SettingsFor(t, 1));
        Assert.Contains("Multi-configuration operand 1 (THIC 2), configuration 1", ex.Message);
    }

    [Fact]
    public void PUCN_MakesTheRowsBelowCopyAPreviousConfiguration_UntilTheNextPUCN()
    {
        var t = ZoomTable() with { };
        var table = t.MultiConfiguration! with
        {
            ConfigurationCount = 3,
            Operands = new()
            {
                new() { Type = "PUCN", Values = new() { V(0), V(0), V(1) } },
                new() { Type = "THIC", Number1 = 2, Values = new() { V(10), V(25), V(40) } },
                new() { Type = "PUCN", Values = new() { V(0), V(0), V(0) } },
                new() { Type = "THIC", Number1 = 4, Values = new() { V(60), V(45), V(30) } },
            },
        };
        var config3 = MultiConfiguration.SettingsFor(t with { MultiConfiguration = table }, 3);
        Assert.Equal(10, config3.Surfaces![2].Thickness);    // picked up from configuration 1
        Assert.Equal(30, config3.Surfaces[4].Thickness);     // after the closing PUCN: its own value
    }

    [Fact]
    public void AThermalPickup_AtTheSameTemperature_PicksUpItsReference()
    {
        // OP-91 evaluates thermal pickups (ThermalAnalysisTests); with no TEMP row both
        // configurations are at the system temperature, so the cell is its reference's value.
        var t = ZoomTable();
        var operands = t.MultiConfiguration!.Operands!;
        operands[0] = operands[0] with { Values = new() { V(10), new() { Solve = MultiConfiguration.ThermalPickup, PickupConfiguration = 1 } } };
        Assert.Equal(10, MultiConfiguration.SettingsFor(t, 1).Surfaces![2].Thickness);
        Assert.Equal(10, MultiConfiguration.SettingsFor(t, 2).Surfaces![2].Thickness);
    }

    [Theory]
    [InlineData("NPOS")]
    [InlineData("XYZW")]
    public void AnUnsupportedOrUnknownOperand_FailsWithItsName(string code)
    {
        var t = ZoomTable();
        t.MultiConfiguration!.Operands!.Add(new() { Type = code, Values = new() { V(0), V(0) } });
        var ex = Assert.Throws<ArgumentException>(() => MultiConfiguration.SettingsFor(t, 1));
        Assert.Contains(code, ex.Message);
        Assert.Contains("operand 3", ex.Message);
    }

    [Fact]
    public void EveryProblem_IsListed_NotOnlyTheFirst()
    {
        var t = ZoomTable();
        t.MultiConfiguration!.Operands!.Add(new() { Type = "THIC", Number1 = 40, Values = new() { V(1), V(1) } });
        t.MultiConfiguration.Operands.Add(new() { Type = "NPOS", Values = new() { V(0), V(0) } });
        var ex = Assert.Throws<ArgumentException>(() => MultiConfiguration.SettingsFor(t, 1));
        Assert.Contains("operand 3", ex.Message);
        Assert.Contains("operand 4", ex.Message);
    }

    [Fact]
    public void LaterRows_Win_WhereTwoControlTheSameValue()
    {
        var t = ZoomTable();
        t.MultiConfiguration!.Operands!.Add(new() { Type = "THIC", Number1 = 2, Values = new() { V(12), V(13) } });
        Assert.Equal(12, MultiConfiguration.SettingsFor(t, 1).Surfaces![2].Thickness);
    }

    [Fact]
    public void Weights_ComeFromTheLastCWGTRow_DefaultOne()
    {
        var t = ZoomTable();
        Assert.Equal(1, MultiConfiguration.WeightOf(t, 2));
        t.MultiConfiguration!.Operands!.Add(new() { Type = "CWGT", Values = new() { V(1), V(3) } });
        Assert.Equal(3, MultiConfiguration.WeightOf(t, 2));
        // CWGT changes nothing in the system.
        Assert.Equal(JsonSerializer.Serialize(MultiConfiguration.SettingsFor(ZoomTable(), 2)), JsonSerializer.Serialize(MultiConfiguration.SettingsFor(t, 2)));
    }

    [Fact]
    public void Variables_ListTheVariableCells_AndWithValueWritesOne()
    {
        var t = ZoomTable();
        var operands = t.MultiConfiguration!.Operands!;
        operands[0] = operands[0] with { Values = new() { V(10), new() { Value = 25, Solve = MultiConfiguration.Variable } } };
        var cells = MultiConfiguration.Variables(t);
        Assert.Equal(new[] { new MultiConfigCell(1, 2) }, cells);
        var moved = MultiConfiguration.WithValue(t, cells[0], 27.5);
        Assert.Equal(27.5, MultiConfiguration.ValueOf(moved, cells[0]));
        Assert.Equal(27.5, MultiConfiguration.SettingsFor(moved, 2).Surfaces![2].Thickness);
        Assert.Equal(MultiConfiguration.Variable, moved.MultiConfiguration!.Operands![0].Values![1].Solve);
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public void InsertConfiguration_CopiesTheColumnAtTheCursor_AndMovesReferences()
    {
        var t = ZoomTable();
        var operands = t.MultiConfiguration!.Operands!;
        operands[1] = operands[1] with { Values = new() { V(60), new() { Value = 45, Solve = MultiConfiguration.Pickup, PickupConfiguration = 2, PickupOperand = 1 } } };
        var table = MultiConfigTools.InsertConfiguration(t.MultiConfiguration with { CurrentConfiguration = 2 }, 2);
        Assert.Equal(3, table.ConfigurationCount);
        Assert.Equal(3, table.CurrentConfiguration);                       // still the same configuration
        Assert.Equal(25, table.Operands![0].Values![1].Value);             // the copy of old 2
        Assert.Null(table.Operands[1].Values![1].Solve);                   // copied as fixed
        Assert.Equal(3, table.Operands[1].Values![2].PickupConfiguration); // the pickup follows old 2, now 3
        var moved = t with { MultiConfiguration = table };
        Assert.Equal(JsonSerializer.Serialize(MultiConfiguration.SettingsFor(t, 2)), JsonSerializer.Serialize(MultiConfiguration.SettingsFor(moved, 3)));
    }

    [Fact]
    public void InsertConfigurationWithPickups_PicksUpConfigurationOne()
    {
        var t = ZoomTable();
        var table = MultiConfigTools.InsertConfigurationWithPickups(t.MultiConfiguration!, 3);
        var config3 = MultiConfiguration.SettingsFor(t with { MultiConfiguration = table }, 3);
        Assert.Equal(10, config3.Surfaces![2].Thickness);
        Assert.Equal(60, config3.Surfaces[4].Thickness);
        Assert.Throws<ArgumentException>(() => MultiConfigTools.InsertConfigurationWithPickups(t.MultiConfiguration!, 1));
    }

    [Fact]
    public void DeleteConfiguration_RemovesTheColumn_AndFixesPickupsFromIt()
    {
        var t = ZoomTable();
        var table = MultiConfigTools.InsertConfigurationWithPickups(t.MultiConfiguration!, 3);
        var edit = MultiConfigTools.DeleteConfiguration(table, 1);
        Assert.Equal(2, edit.Table.ConfigurationCount);
        Assert.Equal(2, edit.Notes.Count);                                  // two pickups named configuration 1
        Assert.Null(edit.Table.Operands![0].Values![1].Solve);
        Assert.Equal(25, edit.Table.Operands[0].Values![0].Value);         // old 2 is now 1
        Assert.Throws<ArgumentException>(() => MultiConfigTools.DeleteConfiguration(new OpticsMultiConfigSettings(), 1));
    }

    [Fact]
    public void MakeSingleConfiguration_KeepsTheCurrentConfiguration()
    {
        var t = ZoomTable() with { MultiConfiguration = ZoomTable().MultiConfiguration! with { CurrentConfiguration = 2 } };
        var single = MultiConfigTools.MakeSingleConfiguration(t);
        Assert.Null(single.MultiConfiguration);
        Assert.Equal(25, single.Surfaces![2].Thickness);
    }

    [Fact]
    public void AddAllData_AddsARowPerTarget_WithoutChangingAnyConfiguration()
    {
        var t = ZoomTable();
        var added = MultiConfigTools.AddAllData(t, "THIC");
        var rows = added.MultiConfiguration!.Operands!;
        // Surfaces 0 to 4 have a thickness (the image's is unused); 2 and 4 were in the table.
        Assert.Equal(new[] { 2, 4, 0, 1, 3 }, rows.Select(r => r.Number1));
        Assert.True(rows[2].Values![0].IsInfinite);
        for (int k = 1; k <= 2; k++)
            Assert.Equal(JsonSerializer.Serialize(MultiConfiguration.SettingsFor(t, k)), JsonSerializer.Serialize(MultiConfiguration.SettingsFor(added, k)));
        // Without a table, one configuration is made.
        var fresh = MultiConfigTools.AddAllData(new OpticsSettings { Surfaces = Zoom(10, 60) }, "CRVT");
        Assert.Equal(1, fresh.MultiConfiguration!.ConfigurationCount);
        Assert.Equal(6, fresh.MultiConfiguration.Operands!.Count);          // every surface, object and image included
        Assert.Throws<ArgumentException>(() => MultiConfigTools.AddAllData(t, "NPOS"));
    }

    [Fact]
    public void NextAndPrevious_StayWithinTheTable()
    {
        var table = ZoomTable().MultiConfiguration!;
        Assert.Equal(2, MultiConfigTools.Next(table).CurrentConfiguration);
        Assert.Equal(2, MultiConfigTools.Next(MultiConfigTools.Next(table)).CurrentConfiguration);
        Assert.Equal(1, MultiConfigTools.Previous(table).CurrentConfiguration);
        Assert.Throws<ArgumentException>(() => MultiConfigTools.SetCurrent(table, 3));
    }

    // ------------------------------------------------------------------ analyses

    private sealed class Capture : OpticsAnalysis<Capture.Settings>
    {
        public sealed class Settings { }
        public OpticsSettings? Seen;
        public override string Id => "Capture";
        public override string Name => "Capture";
        public override string Category => "Test";
        protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, Settings settings, CancellationToken cancellation)
        {
            Seen = context.System;
            return null!;
        }
    }

    [Fact]
    public void Analyses_ComputeOnTheCurrentConfiguration()
    {
        var t = ZoomTable() with { MultiConfiguration = ZoomTable().MultiConfiguration! with { CurrentConfiguration = 2 } };
        var analysis = new Capture();
        analysis.Compute(new OpticsAnalysisContext("Zoom", t), new Capture.Settings(), CancellationToken.None);
        Assert.Null(analysis.Seen!.MultiConfiguration);
        Assert.Equal(25, analysis.Seen.Surfaces![2].Thickness);
        Assert.Equal(25e-3, ProjectLens.Sequential(MultiConfiguration.ForCurrentConfiguration(new OpticsAnalysisContext("Zoom", t)))!.Surfaces[2].Thickness, 15);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public void TheTable_RoundTripsThroughTheProjectAndTheLensFile()
    {
        var t = ZoomTable();
        t.MultiConfiguration!.Operands!.Add(new()
        {
            Type = "GLSS", Number1 = 1,
            Values = new() { new() { Text = "N-BK7" }, new() { Solve = MultiConfiguration.Pickup, PickupConfiguration = 1, PickupOperand = 3, PickupScale = 1 } },
        });
        t.MultiConfiguration.Operands.Add(new() { Type = "THIC", Number1 = 0, Values = new() { new() { IsInfinite = true }, new() { Value = 1234.5678901234567, Solve = MultiConfiguration.Variable } } });
        t.MultiConfiguration.Operands.Add(new() { Type = "ZZZZ", Number1 = 7, Number2 = 8, Number3 = 9, Values = new() { V(1.0 / 3), new() { Solve = MultiConfiguration.ThermalPickup, PickupConfiguration = 1 } } });

        string projectPath = Path.Combine(_directory, "zoom.ossproj");
        new ProjectSerializer().Save(new SimProject { Name = "Zoom", AnalysisType = "Optics", Optics = t }, projectPath);
        var project = new ProjectSerializer().Load(projectPath);
        Assert.Equal(JsonSerializer.Serialize(t), JsonSerializer.Serialize(project.Optics));

        string lensPath = Path.Combine(_directory, "zoom" + OpticsFileSerializer.FileExtension);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = "Zoom", Optics = t }, lensPath);
        var lens = new OpticsFileSerializer().Load(lensPath);
        Assert.Equal(JsonSerializer.Serialize(t), JsonSerializer.Serialize(lens.Optics));
    }

    [Fact]
    public void AFileWithoutATable_IsOneConfiguration()
    {
        var single = new OpticsSettings { Surfaces = Zoom(10, 60) };
        string json = JsonSerializer.Serialize(single, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("multiConfiguration", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, MultiConfiguration.ConfigurationCount(single));
        Assert.Equal(1, MultiConfiguration.CurrentConfiguration(single));
    }
}
