using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Tests.Optics;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Cli;

/// <summary>OP-99: "opensim optics macro".</summary>
[Collection(ZplMacroFolderCollection.Name)]
public sealed class OpticsMacroCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-macro-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsMacroCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void AMacro_RunsOnALensFile_PrintsItsOutput_AndSavesTheChangedLens()
    {
        string lens = Path.Combine(_dir, "singlet" + OpticsFileSerializer.FileExtension);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = "singlet", Optics = ZplConformanceTests.Singlet() }, lens);
        string macro = Path.Combine(_dir, "edit.zpl");
        File.WriteAllText(macro, "A$ = $GETARG(\"t\")\nSURP 2, THIC, SVAL(A$)\nPRINT THIC(2)\nB$ = $GETARG(\"IsAutomated\")\nPRINT B$\n");
        string saved = Path.Combine(_dir, "out" + OpticsFileSerializer.FileExtension);
        string text = Path.Combine(_dir, "out.txt");

        var run = Run("optics", "macro", macro, "--lens", lens, "--arg", "t=42", "--out", text, "--save-lens", saved, "--builtin-catalogs");
        Assert.True(run.Code == 0, run.Err);
        Assert.Contains("42.0000" + Environment.NewLine + "True", run.Out);
        Assert.Equal("42.0000" + Environment.NewLine + "True" + Environment.NewLine, File.ReadAllText(text));
        Assert.Equal(42, new OpticsFileSerializer().Load(saved).Optics!.Surfaces![2].Thickness);
    }

    [Fact]
    public void AMacroError_NamesTheMacroAndLine_WithExitCode2()
    {
        string macro = Path.Combine(_dir, "bad.zpl");
        File.WriteAllText(macro, "x = 1\nBEEP\n");
        var run = Run("optics", "macro", macro, "--builtin-catalogs");
        Assert.Equal(2, run.Code);
        Assert.Contains("bad.zpl, line 2", run.Err);
        Assert.Contains("BEEP", run.Err);
    }
}
