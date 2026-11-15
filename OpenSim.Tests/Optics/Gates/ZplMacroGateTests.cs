using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Scripting;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-99's gate, the operand clause: "a macro operand and the equivalent built-in operand return
/// the same value". The macro is the help's own user-defined TOTR (Optimizing with ZPL Macros:
/// "this is in effect a user-defined version of the TOTR operand"), written here in this test's
/// words: the sum of THIC(i) for i = 1 to NSUR(), returned with OPTRETURN 0. The conformance
/// suite (one macro per bound keyword) is ZplConformanceTests; the language core against
/// hand-evaluated programs is ZplLanguageTests. The macro solve is checked here as well, since it
/// shares the macro folder (a process-wide setting, so these tests run in one class).
/// </summary>
[Collection(ZplMacroFolderCollection.Name)]
public sealed class ZplMacroGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-zpl-gate", Guid.NewGuid().ToString("N"));
    private readonly string _previous = ZplMacroFolder.Current;

    public ZplMacroGateTests()
    {
        Directory.CreateDirectory(_dir);
        ZplMacroFolder.Current = _dir;
        File.WriteAllText(Path.Combine(_dir, ZplMacroOperand.MacroFile(15)),
            "n = NSUR()\nx = 0\nFOR i = 1, n, 1\nx = x + THIC(i)\nNEXT\nOPTRETURN 0, x\nOPTRETURN 1, PVHX() * x\n");
    }

    public void Dispose()
    {
        ZplMacroFolder.Current = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    /// <summary>A cemented doublet and an air gap: thicknesses 4, 2, 6 and 88 mm (arbitrary inputs).</summary>
    private static OpticsSettings Doublet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 10 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 60, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = -45, Thickness = 2, Index = 1.65 },
            new() { Radius = -200, Thickness = 6 },
            new() { Thickness = 88 },
            new(),
        },
    };

    private static MeritEvaluation Evaluate(OpticsSettings lens, params MeritOperandRow[] rows) =>
        OpticsScripting.EvaluateMerit(lens with { MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(rows)) }, NoCatalogs);

    [Fact]
    public void Gate_TheZplmOperand_EqualsTheBuiltInTotr()
    {
        // TOTR: "the greatest minus the least vertex z from surface 1 to the image", here
        // 4 + 2 + 6 + 88 = 100 mm; the macro sums the same thicknesses one by one (and the image's,
        // which is 0). The two differ only by rounding: TOTR adds meters and converts once, the
        // macro gets each thickness converted back to millimeters.
        var e = Evaluate(Doublet(), MeritOperandRow.Of("ZPLM", 15, 0), MeritOperandRow.Of("TOTR"));
        Assert.Empty(e.Errors);
        Assert.Equal(e.Rows[1].Value, e.Rows[0].Value, 1e-12 * e.Rows[1].Value);
        Assert.Equal(100, e.Rows[1].Value, 1e-12);
    }

    [Fact]
    public void TheZplmOperand_ReadsItsDataField_AndPassesData1ToData6()
    {
        // Data field 1 holds PVHX()·x with PVHX = Data1 = 2.5: 250.
        var e = Evaluate(Doublet(), MeritOperandRow.Of("ZPLM", 15, 1, 0, 0, 2.5));
        Assert.Empty(e.Errors);
        Assert.Equal(250, e.Rows[0].Value, 1e-10);
        // A field the macro did not fill, a macro that is not there, and a Mac# out of range fail by name.
        var bad = Evaluate(Doublet(), MeritOperandRow.Of("ZPLM", 15, 2), MeritOperandRow.Of("ZPLM", 16, 0), MeritOperandRow.Of("ZPLM", 100, 0));
        Assert.Contains("data field 2", bad.Rows[0].Error);
        Assert.Contains("ZPL16.ZPL", bad.Rows[1].Error);
        Assert.Contains("0 to 99", bad.Rows[2].Error);
    }

    [Fact]
    public void TheZplmOperand_RunsOnACopy_SoTheLensIsNotChanged()
    {
        // A macro that changes a thickness: the next row still sees the lens as it was ("the merit
        // function is always evaluated using a temporary copy of the lens").
        File.WriteAllText(Path.Combine(_dir, ZplMacroOperand.MacroFile(16)), "SURP 4, THIC, 1000\nOPTRETURN 0, THIC(4)\n");
        var e = Evaluate(Doublet(), MeritOperandRow.Of("ZPLM", 16, 0), MeritOperandRow.Of("TOTR"));
        Assert.Equal(1000, e.Rows[0].Value);
        Assert.Equal(100, e.Rows[1].Value, 1e-12);
    }

    [Fact]
    public void Opev_EqualsTheBuiltInOperands()
    {
        // OPEV computes "the same value as any optimization operand would" (help, Numeric Functions).
        var lens = Doublet();
        var e = Evaluate(lens, MeritOperandRow.Of("EFFL", 0, 1), MeritOperandRow.Of("TOTR"), MeritOperandRow.Of("CTVA", 2), MeritOperandRow.Of("REAY", 5, 1, 0, 0, 0, 0, 0, 1));
        var r = ZplMacro.Run("OPTRETURN 0, OPEV(OCOD(\"EFFL\"), 0, 1, 0, 0, 0, 0)\nOPTRETURN 1, OPEV(OCOD(\"TOTR\"), 0, 0, 0, 0, 0, 0)\n"
            + "OPTRETURN 2, OPEV(OCOD(\"CTVA\"), 2, 0, 0, 0, 0, 0)\nOPTRETURN 3, OPEV(OCOD(\"REAY\"), 5, 1, 0, 0, 0, 1)",
            new ZplRunOptions { System = lens, Catalogs = NoCatalogs }).OptReturn;
        for (int i = 0; i < 4; i++) Assert.Equal(e.Rows[i].Value, r[i]);
    }

    [Fact]
    public void AMacroSolve_SetsItsCell_FromSolvereturn()
    {
        // TZ on surface 4: the macro returns twice surface 1's thickness, 8 mm; CZ on surface 3
        // returns the curvature −1/100 per mm. Applied by the solve engine, read back by the trace.
        File.WriteAllText(Path.Combine(_dir, "thick.zpl"), "SOLVEBEFORESTOP\ns = SOSO(0)\nSOLVERETURN 2 * THIC(s - 3)\n");
        File.WriteAllText(Path.Combine(_dir, "curv.zpl"), "SOLVERETURN -0.01\n");
        var r = ZplMacro.Run("SOLVETYPE 4, TZ, thick.zpl\nSOLVETYPE 3, CZ, \"curv.zpl\"\nOPTRETURN 0, THIC(4)\nOPTRETURN 1, CURV(3)",
            new ZplRunOptions { System = Doublet(), Catalogs = NoCatalogs, MacroFolder = _dir });
        Assert.Equal(8, r.OptReturn[0]!.Value, 1e-12);
        Assert.Equal(-0.01, r.OptReturn[1]!.Value, 1e-15);
        var flag = r.System.Surfaces![4].Flags!.Single();
        Assert.Equal(("Thickness", "Solve", "ZPL Macro", "thick.zpl"), (flag.Column, flag.Kind, flag.Solve, flag.Macro));
        var solved = ProjectLens.Solve(r.System, NoCatalogs)!;
        Assert.True(solved.Succeeded, string.Join("; ", solved.Errors));
        Assert.Equal(0.008, solved.Lens.Surfaces[4].Thickness, 1e-15);

        // A macro that does not call SOLVERETURN marks the solve as failed, naming it.
        File.WriteAllText(Path.Combine(_dir, "none.zpl"), "x = 1\n");
        var failed = ProjectLens.Solve(Doublet() with
        {
            Surfaces = Doublet().Surfaces!.Select((s, j) => j == 4 ? s with { Flags = new() { new() { Column = "Thickness", Kind = "Solve", Solve = "ZPL Macro", Macro = "none.zpl" } } } : s).ToList(),
        }, NoCatalogs)!;
        Assert.False(failed.Succeeded);
        Assert.Contains("SOLVERETURN", string.Join(" ", failed.Errors));
    }
}

/// <summary>The tests that set the process-wide macro folder (<see cref="ZplMacroFolder.Current"/>)
/// run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class ZplMacroFolderCollection
{
    public const string Name = "ZPL macro folder";
}
