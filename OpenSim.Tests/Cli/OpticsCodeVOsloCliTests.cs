using System.Text.Json;
using OpenSim.Cli;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Interop.CodeV;
using OpenSim.Optics.Interop.Oslo;

namespace OpenSim.Tests.Cli;

/// <summary>OP-95: "opensim optics import" reads CODE V .seq and OSLO .len files by their extension.</summary>
public sealed class OpticsCodeVOsloCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-codev-oslo-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsCodeVOsloCliTests() => Directory.CreateDirectory(_dir);

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

    // Written for the test from section 54's rules: a fictitious glass (CODE V) and a constant
    // index (OSLO), so no catalog glass is needed.
    private const string Seq = "RDM\nTIT 'cli singlet'\nEPD 10\nWL 550\nYAN 0 3\nSO 0 1e20\nS 40 4 1.5:60\nSTO\nS -40 38\nSI 0 0\n";
    private const string Len = "LEN NEW \"cli singlet\" 1 3\nEBR 5; ANG 3; WV 0.55\nTH 1e20\nNXT; RD 40; TH 4; GLA 1.5; AST\nNXT; RD -40; TH 38\nNXT\nEND 3\n";

    [Fact]
    public void Import_ReadsSeqAndLen_ByExtension()
    {
        string seq = Path.Combine(_dir, "lens.seq"), len = Path.Combine(_dir, "lens.len");
        File.WriteAllText(seq, Seq);
        File.WriteAllText(len, Len);

        string seqOut = Path.Combine(_dir, "seq" + OpticsFileSerializer.FileExtension);
        var a = Run("optics", "import", seq, "--out", seqOut, "--builtin-catalogs");
        Assert.True(a.Code == 0, a.Err);
        Assert.Contains("Imported", a.Out);
        Assert.Equal(JsonSerializer.Serialize(CodeVLensReader.Read(Seq, null).Optics), JsonSerializer.Serialize(new OpticsFileSerializer().Load(seqOut).Optics));

        string lenOut = Path.Combine(_dir, "len" + OpticsFileSerializer.FileExtension);
        var b = Run("optics", "import", len, "--out", lenOut, "--builtin-catalogs");
        Assert.True(b.Code == 0, b.Err);
        Assert.Equal(JsonSerializer.Serialize(OsloLensReader.Read(Len, null).Optics), JsonSerializer.Serialize(new OpticsFileSerializer().Load(lenOut).Optics));

        // Unsupported content fails with exit code 2, naming the command; --lenient goes on.
        File.WriteAllText(seq, Seq.Replace("STO\n", "STO\nFOO 1\n"));
        var strict = Run("optics", "import", seq, "--out", seqOut, "--builtin-catalogs");
        Assert.Equal(2, strict.Code);
        Assert.Contains("FOO", strict.Err);
        var lenient = Run("optics", "import", seq, "--out", seqOut, "--builtin-catalogs", "--lenient");
        Assert.True(lenient.Code == 0, lenient.Err);
        Assert.Contains("Not read:", lenient.Out);
    }
}
