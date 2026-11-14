using System.Text.Json;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.MultiConfig;
using M = OpenSim.App.ViewModels.OpticsMultiConfigEditorViewModel;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-30: the Multi-Configuration Editor as the Optics workspace edits it. The lens and
/// the thicknesses are arbitrary inputs.</summary>
public sealed class OpticsMultiConfigEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();
    private M Editor => Optics.MultiConfig;
    private ProjectSession Session => _app.Resolve<ProjectSession>();

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    private static OpticsConfigValueSettings V(double v) => new() { Value = v };

    /// <summary>Two groups in air (mm), the gap after surface 2 and the back distance zoomed.</summary>
    private static OpticsSettings Zoom(int current = 1) => new()
    {
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = -50, Thickness = 10 },
            new() { Radius = -30, Thickness = 3, Index = 1.6 },
            new() { Radius = 80, Thickness = 60 },
            new(),
        },
        MultiConfiguration = new()
        {
            ConfigurationCount = 2,
            CurrentConfiguration = current,
            Operands = new()
            {
                new() { Type = "THIC", Number1 = 2, Values = new() { V(10), V(25) } },
                new() { Type = "THIC", Number1 = 4, Values = new() { V(60), V(45) } },
            },
        },
    };

    private void Load(OpticsSettings settings) => Assert.Empty(Optics.LoadSettings(settings));

    [Fact]
    public void TheEditor_StartsWithOneConfiguration_AndSavesNoTable()
    {
        Assert.Equal(1, Editor.ConfigurationCount);
        Assert.Equal(0, Editor.Document.RowCount);
        Assert.Null(Editor.ToSettings());
        Assert.Null(Optics.ToSettings().MultiConfiguration);
        Assert.Equal(M.FirstConfigurationSlot + 1 + 1, Editor.Document.ColumnCount);   // type, three numbers, one configuration
    }

    [Fact]
    public void AFilesTable_RoundTripsThroughTheEditor()
    {
        var zoom = Zoom();
        Load(zoom);
        Assert.Equal(2, Editor.ConfigurationCount);
        Assert.Equal(2, Editor.Document.RowCount);
        Assert.Equal("THIC", Editor.Document.Rows[0].TypeName);
        Assert.Equal(Json(zoom.MultiConfiguration), Json(Editor.ToSettings()));
        Assert.Equal(Json(zoom.MultiConfiguration), Json(Session.OpticsSystem!.MultiConfiguration));
        Assert.False(Editor.HasProblems, Editor.ProblemsText);
    }

    [Fact]
    public void AFileWithoutATable_EmptiesTheEditor()
    {
        Load(Zoom());
        Load(Zoom() with { MultiConfiguration = null });
        Assert.Equal(1, Editor.ConfigurationCount);
        Assert.Null(Editor.ToSettings());
    }

    [Fact]
    public void ChangingTheCurrentConfiguration_PublishesIt()
    {
        Load(Zoom());
        Editor.NextConfigurationCommand.Execute(null);
        Assert.Equal(2, Editor.CurrentConfiguration);
        Assert.Equal(2, Session.OpticsSystem!.MultiConfiguration!.CurrentConfiguration);
        Assert.Equal(25, MultiConfiguration.CurrentSettings(Session.OpticsSystem).Surfaces![2].Thickness);
        Editor.NextConfigurationCommand.Execute(null);
        Assert.Equal(2, Editor.CurrentConfiguration);      // stays on the last
        Editor.CurrentConfiguration = 1;
        Assert.Equal(1, Session.OpticsSystem!.MultiConfiguration!.CurrentConfiguration);
        Assert.Equal("Configuration 1 of 2", Editor.Summary);
    }

    [Fact]
    public void InsertAndDeleteConfiguration_ChangeTheColumns()
    {
        Load(Zoom());
        Editor.CursorConfiguration = 2;
        Editor.InsertConfigurationCommand.Execute(null);
        Assert.Equal(3, Editor.ConfigurationCount);
        Assert.Equal(M.FirstConfigurationSlot + 3 + 1, Editor.Document.ColumnCount);
        Assert.Equal(25, Editor.ToSettings()!.Operands![0].Values![1].Value);
        Assert.Equal(3, Session.OpticsSystem!.MultiConfiguration!.ConfigurationCount);

        Editor.CursorConfiguration = 1;
        Editor.DeleteConfigurationCommand.Execute(null);
        Assert.Equal(2, Editor.ConfigurationCount);
        Assert.Equal(25, Editor.ToSettings()!.Operands![0].Values![0].Value);
    }

    [Fact]
    public void InsertWithPickups_PicksUpConfigurationOne_AndShowsItsValue()
    {
        Load(Zoom());
        Editor.CursorConfiguration = 3;
        Editor.InsertConfigurationWithPickupsCommand.Execute(null);
        var table = Editor.ToSettings()!;
        Assert.Equal(MultiConfiguration.Pickup, table.Operands![0].Values![2].Solve);
        Assert.Equal(1, table.Operands[0].Values![2].PickupConfiguration);
        Assert.Equal(1, table.Operands[0].Values![2].PickupOperand);
        Assert.Equal(CellFlagKind.Pickup, Editor.Document.Rows[0].Cells[M.FirstConfigurationSlot + 2].Flag.Kind);
        Assert.False(Editor.HasProblems, Editor.ProblemsText);
    }

    [Fact]
    public void APickupSetInTheSheet_IsSaved_AndItsValueShown()
    {
        Load(Zoom());
        var doc = Editor.Document;
        var result = doc.SetFlag(1, M.FirstConfigurationSlot + 2, CellFlag.PickupFrom(doc.Rows[0], M.FirstConfigurationSlot + 1, scale: 2, offset: 1));
        Assert.True(result.Succeeded, result.Message);
        var cell = Editor.ToSettings()!.Operands![1].Values![1];
        Assert.Equal(MultiConfiguration.Pickup, cell.Solve);
        Assert.Equal((1, 1, 2.0, 1.0), (cell.PickupOperand, cell.PickupConfiguration, cell.PickupScale, cell.PickupOffset));
        Assert.Equal(21, doc.Rows[1].Cells[M.FirstConfigurationSlot + 1].Value.Number);   // 10 × 2 + 1
    }

    [Fact]
    public void AnUnsupportedOperand_IsListedByName()
    {
        Load(Zoom());
        Editor.CurrentRow = 2;
        Editor.InsertOperandCommand.Execute(null);
        Assert.True(Editor.Document.SetText(2, 0, "NPOS").Succeeded);
        Assert.True(Editor.HasProblems);
        Assert.Contains("NPOS", Editor.ProblemsText);
    }

    [Fact]
    public void AddAllData_AddsTheMissingThicknesses()
    {
        Load(Zoom());
        Editor.AddAllDataCode = "THIC";
        Editor.AddAllDataCommand.Execute(null);
        Assert.Equal(5, Editor.Document.RowCount);
        Assert.Equal(new[] { 2, 4, 0, 1, 3 }, Editor.ToSettings()!.Operands!.Select(o => o.Number1));
        Assert.False(Editor.HasProblems, Editor.ProblemsText);
    }

    [Fact]
    public void MakeSingleConfiguration_LeavesTheLensInTheCurrentConfiguration()
    {
        Load(Zoom(current: 2));
        Editor.MakeSingleConfigurationCommand.Execute(null);
        Assert.Null(Editor.ToSettings());
        Assert.Equal(25, Optics.LensEditor.ToSettings()[2].Thickness);
        Assert.Equal(45, Optics.LensEditor.ToSettings()[4].Thickness);
        Assert.Null(Session.OpticsSystem!.MultiConfiguration);
    }

    [Fact]
    public void TheSchema_HasARowTypeForEveryOperand_AndKeepsUnknownCodes()
    {
        var schema = M.BuildSchema(2, new[] { "ZZZZ" });
        Assert.Equal(MultiConfigOperands.Off, schema.DefaultRowType.Name);
        Assert.Equal(MultiConfigOperands.All.Count + 1, schema.RowTypes.Count);
        Assert.NotNull(schema.FindRowType("ZZZZ"));
        Assert.Equal(CellKind.Text, schema.FindRowType("GLSS")!.ColumnAt(M.FirstConfigurationSlot)!.Kind);
        Assert.True(schema.FindRowType("THIC")!.ColumnAt(M.FirstConfigurationSlot)!.AllowsInfinity);
    }
}
