using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-94's gate (plan): "Imported vendor lenses reproduce the focal length printed in the
/// vendor's own catalog; export then import is the identity on the supported subset." The second
/// half is <see cref="ZemaxLensFileTests.ExportThenImport_IsTheIdentity_OverEverySupportedSurfaceTypeAndSolve"/>.
/// <para>
/// <b>The vendor half, as far as it is built.</b> The vendor's catalog pages could not be fetched:
/// thorlabs.com serves every product page, spec-sheet and catalog-page address as a script-only
/// page with no data (tried 2026-11-14, see docs/Optics-Gates.md), so no focal length from a
/// catalog page is asserted. What is asserted instead is the focal length the vendor prints in
/// the lens file itself: the Thorlabs file <c>354710-C-Zemax(ZMX).zmx</c> shipped as test data
/// with RayOptics 0.9.10 (BSD 3-clause, <c>rayoptics/zemax/tests/</c>) has the title line
/// "NAME Ø=2.65mm, f=1.49mm,  NA=0.53 D-ZK3 Asphere,-C"; 1.49 mm, to half a unit of its last
/// printed digit (0.005 mm). The files and the AGF catalogs (ZemaxGlass's
/// <c>zemaxglass/AGF_files</c>) are under the gitignored <c>plans/sources/optics</c>; without
/// them these tests do nothing and say so.
/// </para>
/// </summary>
public sealed class ZemaxImportGateTests
{
    private readonly ITestOutputHelper _out;

    public ZemaxImportGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>plans/sources/optics, found from the test binaries upward (a worktree's own
    /// solution folder has no plans/, so the search goes on past it).</summary>
    private static string? Sources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string folder = Path.Combine(dir.FullName, "plans", "sources", "optics");
            if (Directory.Exists(folder)) return folder;
        }
        return null;
    }

    private static string RayOpticsFile(string sources, params string[] parts) =>
        Path.Combine(new[] { sources, "pyref-venv", "Lib", "site-packages", "rayoptics" }.Concat(parts).ToArray());

    private static OpticalCatalog Catalog(string sources, string file) =>
        AgfReader.Read(Path.Combine(sources, "zemaxglass", "AGF_files", file));

    [Fact]
    public void ThorlabsLightPath354710C_ReproducesTheFocalLengthItsFilePrints()
    {
        string? sources = Sources();
        string? path = sources is null ? null : RayOpticsFile(sources, "zemax", "tests", "354710-C-Zemax(ZMX).zmx");
        if (path is null || !File.Exists(path))
        {
            _out.WriteLine("DID NOTHING: the RayOptics test files are not on this machine.");
            return;
        }
        var catalogs = new[] { Catalog(sources!, "lightpath.agf"), Catalog(sources!, "schott.agf") };
        // Surface 3 is a plane-parallel cover window of BK7, a glass the current Schott catalog no
        // longer lists; N-BK7 stands in. A plane-parallel plate has no power, so the substitution
        // cannot change the focal length (derived: its paraxial matrix leaves the ray angle unchanged).
        var result = ZemaxLensFiles.Import(path, catalogs, new ZemaxImportOptions { GlassSubstitutions = new Dictionary<string, string> { ["BK7"] = "N-BK7" } });
        Assert.Empty(result.Warnings);
        foreach (var n in result.Notes) _out.WriteLine(n);
        Assert.Contains("f=1.49mm", result.Optics.SystemData!.Title);
        Assert.Equal("D-ZK3M", result.Optics.Surfaces![1].Material);

        var lens = ProjectLens.From(result.Optics, catalogs)!;
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(result.Optics.SystemData, notes);
        double efl = ParaxialSystem.Create(lens.ToParaxialLens(), system).EffectiveFocalLength!.Value * 1e3;
        _out.WriteLine($"EFL {efl:R} mm at {result.Optics.SystemData.Wavelengths![0].Micrometers} µm");
        Assert.InRange(efl, 1.49 - 0.005, 1.49 + 0.005);
    }

    /// <summary>The other two vendor files RayOptics ships read with exactly the problems they
    /// should have: ACL3026U's glass B270 is in none of the free catalogs, and ASL5040-UV ends with
    /// an RCMF row no reader interprets. With the problems listed as warnings the rest reads.</summary>
    [Fact]
    public void TheOtherVendorFiles_FailOnlyOnWhatIsReallyUnsupported()
    {
        string? sources = Sources();
        string? acl = sources is null ? null : RayOpticsFile(sources, "elem", "tests", "ACL3026U-Zemax(ZMX).zmx");
        string? asl = sources is null ? null : RayOpticsFile(sources, "zemax", "tests", "ASL5040-UV-Zemax(ZMX).zmx");
        if (acl is null || !File.Exists(acl) || !File.Exists(asl))
        {
            _out.WriteLine("DID NOTHING: the RayOptics test files are not on this machine.");
            return;
        }
        var all = Directory.GetFiles(Path.Combine(sources!, "zemaxglass", "AGF_files"), "*.agf").Select(AgfReader.Read).ToArray();

        var aclProblem = Assert.Single(Assert.Throws<ZemaxFileException>(() => ZemaxLensFiles.Import(acl, all)).Problems);
        Assert.Contains("GLAS B270", aclProblem);

        var aslProblem = Assert.Single(Assert.Throws<ZemaxFileException>(() => ZemaxLensFiles.Import(asl, all)).Problems);
        Assert.Contains("RCMF", aslProblem);
        var read = ZemaxLensFiles.Import(asl, all, new ZemaxImportOptions { UnsupportedAsWarnings = true });
        var q = read.Optics.Surfaces![1];
        Assert.Equal("Q-Type Asphere", q.Type);
        // XDAT 1 = 0 (Qbfs, left out as zero), 2 = 6 terms, 3 = 27 normalization radius, 4 = first coefficient.
        Assert.Equal(6, q.ExtraData!.Single(x => x.Number == 14).Value);
        Assert.Equal(27, q.ExtraData!.Single(x => x.Number == 15).Value);
        Assert.Equal(-8.148370167576E-01, q.ExtraData!.Single(x => x.Number == 16).Value);
        Assert.Equal("C79-80", q.Material);
    }
}
