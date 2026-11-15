using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Interop.Oslo;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Sequential;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-95: OSLO lens files (docs/Optics-Conventions.md section 54). The files are written here
/// from the command rules section 54 lists; none is copied from a vendor. The gate (a lens by hand
/// and imported trace identically) is <see cref="CodeVOsloImportGateTests"/>.
/// </summary>
public sealed class OsloLensFileTests
{
    private static ZemaxImportResult Strict(string text) => OsloLensReader.Read(text, CodeVOsloImportGateTests.Catalogs());

    private static ZemaxImportResult Lenient(string text) =>
        OsloLensReader.Read(text, CodeVOsloImportGateTests.Catalogs(), new ZemaxImportOptions { UnsupportedAsWarnings = true });

    /// <summary>A three-surface lens (object, one surface, image) with the given system lines
    /// and object distance.</summary>
    private static string Simple(string system, string objectDistance = "1e20", string surface1 = "TH 5") =>
        $"LEN NEW \"t\" 1 2\n{system}\nTH {objectDistance}\nNXT\n{surface1}\nAST\nNXT\nEND 2\n";

    [Fact]
    public void Statements_SplitOnSemicolons_AndCommentsOutsideQuotes()
    {
        Assert.Equal(new[] { "RD 5", " TH 2 " }, OsloLensReader.Statements("RD 5; TH 2 // a comment ; not a command"));
        Assert.Equal(new[] { "NOT \"a;b // \\\" c\" ", " AST" }, OsloLensReader.Statements("NOT \"a;b // \\\" c\" ; AST"));
        Assert.Throws<FormatException>(() => OsloLensReader.Statements("NOT \"open"));
    }

    [Fact]
    public void Units_AndApertures()
    {
        Assert.Equal(ConventionNames.LensUnits.SavedName(LensUnit.Inch), Strict(Simple("UNI 25.4; EBR 1; ANG 0")).Optics.SystemData!.LensUnit);
        Assert.Contains("UNI 2", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Simple("UNI 2; EBR 1; ANG 0"))).Problems));

        var nap = Strict(Simple("NAP 0.125; ANG 0")).Optics.SystemData!;
        Assert.Equal(ApertureTypes.Names.SavedName(ApertureType.ParaxialWorkingFNumber), nap.ApertureType);
        Assert.Equal(4, nap.ApertureValue);   // 1/(2·0.125)
        var fno = Strict(Simple("FNO 3; ANG 0")).Optics.SystemData!;
        Assert.Equal((ApertureTypes.Names.SavedName(ApertureType.ParaxialWorkingFNumber), 3.0), (fno.ApertureType, fno.ApertureValue));
        var nao = Strict(Simple("NAO 0.1; OBH 1", objectDistance: "50")).Optics.SystemData!;
        Assert.Equal((ApertureTypes.Names.SavedName(ApertureType.ObjectSpaceNumericalAperture), 0.1), (nao.ApertureType, nao.ApertureValue));

        Assert.Contains("NAO", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Simple("NAO 0.1; ANG 0"))).Problems));
        Assert.Contains("EBR", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Simple("EBR 1; OBH 1", objectDistance: "50"))).Problems));
        Assert.Contains("PUK", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Simple("PUK 0.1; ANG 0"))).Problems));
        Assert.Contains("no aperture", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Simple("ANG 0"))).Problems));
    }

    [Fact]
    public void Fields_FromTheReference_AndTheFieldTable()
    {
        // OBH with the object at infinity (1e9 ≥ 1e8) is the angle atan(OBH/distance).
        var obh = Strict(Simple("EBR 1; OBH 1e8", objectDistance: "1e9")).Optics.SystemData!;
        Assert.Equal(FieldTypes.Names.SavedName(FieldType.Angle), obh.FieldType);
        Assert.Equal(Math.Abs(Math.Atan(1e8 / 1e9) * 180 / Math.PI), obh.Fields![1].Y);

        var gih = Strict(Simple("NAO 0.1; GIH 2", objectDistance: "50")).Optics.SystemData!;
        Assert.Equal(FieldTypes.Names.SavedName(FieldType.ParaxialImageHeight), gih.FieldType);
        Assert.Equal(new[] { 0.0, 2.0 }, gih.Fields!.Select(f => f.Y));

        // A field table: fraction 0.5 of a 10° reference is atan(0.5·tan 10°) (the reader's
        // rule, Optiland converter.py), with pupil bounds ±0.5 (compression 0.5) and weight 3.
        string table = Simple("EBR 1; ANG 10") + "RST NEW\nF 1 0.5 0 0 0 0 -0.5 0.5 -1 1 3\nEND\n";
        var f = Assert.Single(Strict(table).Optics.SystemData!.Fields!);
        Assert.Equal(Math.Atan(0.5 * Math.Tan(10 * Math.PI / 180)) * 180 / Math.PI, f.Y);
        Assert.Equal((0.0, 3.0, 0.5, 0.0), (f.X, f.Weight, f.CompressionY, f.CompressionX));

        string asymmetric = Simple("EBR 1; ANG 10") + "RST NEW\nF 1 1 0 0 0 0 -0.5 0.75 -1 1 1\nEND\n";
        Assert.Contains("asymmetric", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(asymmetric)).Problems));
        string depth = Simple("EBR 1; ANG 10") + "RST NEW\nF 1 1 0 0.2 0 0 -1 1 -1 1 1\nEND\n";
        Assert.Contains("field depth", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(depth)).Problems));
    }

    [Fact]
    public void Pickups_AreResolvedToValues_InSurfaceOrder()
    {
        string text =
            "LEN NEW \"p\" 1 5\nEBR 5; ANG 0; WV 0.55\nTH 1e20\n" +
            "NXT; RD 40; TH 4; CC -1; AD 1e-5; GLA 1.5 1.5; AST\n" +
            "NXT; RD -40; TH 10\n" +
            "NXT; PK CVM 1 0.01; PK TH -2 1; PK GLA 1\n" +
            "NXT; PK LN 1 2\n" +   // line 7
            "NXT\nEND 5\n";
        var p = Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(text)).Problems);
        Assert.Contains("Line 7", p);
        Assert.Contains("PK LN", p);
        var read = Lenient(text);
        var s = read.Optics.Surfaces!;
        Assert.Equal(1.5, s[1].Index);
        Assert.Equal(1 / (-1.0 / 40 + 0.01), s[3].Radius);
        Assert.Equal(-1, s[3].Conic);
        Assert.Equal(-1e-5, Assert.Single(s[3].Parameters!).Value);
        Assert.Equal("Even Asphere", s[3].Type);
        Assert.Equal(1 * 4.0 + 1, s[3].Thickness);
        Assert.Equal(1.5, s[3].Index);
        Assert.Contains(read.Notes, n => n.StartsWith("Pickups"));
    }

    /// <summary>
    /// DT −1 is "tilt then decenter": Optiland coordinates.py builds the rotation from the
    /// intrinsic Z, Y, X angles (TLC, −TLB, −TLA) and the shift R·(DCX, DCY, 0); intrinsic
    /// rotations compose by right-multiplying, so a point p sits at Rz(TLC)·Ry(−TLB)·Rx(−TLA)·
    /// ((DCX, DCY, 0) + p) (derived from the cited recipe). The saved tilt must give that.
    /// </summary>
    [Fact]
    public void Decenters_AreConvertedToThisProgramsSigns_AndWhatIsNotHeldIsListed()
    {
        string text =
            "LEN NEW \"d\" 1 6\nEBR 5; ANG 0; WV 0.55\nTH 1e20\n" +
            "NXT; TH 10; AST\n" +
            "NXT; TH 10; DCX 0.3; DCY -0.2; TLA 3; TLB -1; TLC 5; DT -1\n" +
            "NXT; TH 10; TLA 1; RCO 1\n" +                 // line 6: a return to another surface
            "NXT; TH 10; DCZ 1\n" +                        // line 7
            "NXT; TH -10; RFL; TLA 1; TLB 1; BEN\n" +      // line 8: BEN about two axes
            "NXT\nEND 6\n";
        var problems = Assert.Throws<ZemaxFileException>(() => Strict(text)).Problems;
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("Line 6") && p.Contains("RCO 1"));
        Assert.Contains(problems, p => p.Contains("Line 7") && p.Contains("DCZ"));
        Assert.Contains(problems, p => p.Contains("Line 8") && p.Contains("BEN"));

        var t = Lenient(text).Optics.Surfaces![2].TiltBefore!;
        Assert.Equal((0.3, -0.2, -3.0, 1.0, 5.0), (t.DecenterX, t.DecenterY, t.TiltX, t.TiltY, t.TiltZ));
        Assert.Equal(SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter), t.Order);

        double rad = Math.PI / 180;
        var expected = RigidTransform.Compose(
            RigidTransform.Compose(RigidTransform.Compose(RigidTransform.RotationAboutZ(5 * rad), RigidTransform.RotationAboutY(-(-1) * rad)), RigidTransform.RotationAboutX(-3 * rad)),
            RigidTransform.FromTranslation(new Vector3D(0.3, -0.2, 0)));
        CodeVLensFileTests.AssertClose(expected, new TiltDecenter(t.DecenterX, t.DecenterY, t.TiltX, t.TiltY, t.TiltZ, TiltDecenterOrder.TiltThenDecenter).ToTransform());
    }

    [Fact]
    public void Media_AndSolves()
    {
        string Lens(string surface1) => Simple("EBR 1; ANG 0", surface1: surface1);
        Assert.Equal("TESTCAT:N-BK7", Strict(Lens("TH 5; GLA \"N-BK7\"")).Optics.Surfaces![1].Material);
        Assert.Equal(SavedSurfaces.Mirror, Strict(Lens("TH -5; RFL")).Optics.Surfaces![1].Material);
        Assert.Contains("tabulated", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Lens("TH 5; GLA 1.5 1.6"))).Problems));
        Assert.Contains("GLA MOD", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Lens("TH 5; GLA MOD 1.6 50"))).Problems));
        Assert.Contains("NOSUCH", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Lens("TH 5; GLA NOSUCH"))).Problems));

        var solved = Strict(Lens("RD 30; TH 5; PY 0"));
        Assert.Equal(5, solved.Optics.Surfaces![1].Thickness);
        Assert.Contains(solved.Notes, n => n.StartsWith("Solves"));
        Assert.Contains("PU", Assert.Single(Assert.Throws<ZemaxFileException>(() => Strict(Lens("TH 5; PU 0"))).Problems));

        var ap = Strict(Lens("TH 5; AP CHK 4")).Optics.Surfaces![1];
        Assert.Equal((4.0, SavedSurfaces.CircularAperture, 4.0), (ap.SemiDiameter!.Value, ap.Aperture!.Type, ap.Aperture.MaxRadius));
        var unchecked_ = Strict(Lens("TH 5; AP 4")).Optics.Surfaces![1];
        Assert.Equal(4, unchecked_.SemiDiameter);
        Assert.Null(unchecked_.Aperture);
    }

    [Fact]
    public void Defaults_AndUnsupportedContent()
    {
        var read = Strict(Simple("EBR 1; ANG 5"));
        Assert.Equal(OsloLensReader.DefaultWavelengths, read.Optics.SystemData!.Wavelengths!.Select(w => w.Micrometers));
        Assert.Equal(new[] { 0.0, 5.0 }, read.Optics.SystemData.Fields!.Select(f => f.Y));
        Assert.True(read.Optics.Surfaces![0].ThicknessIsInfinite);
        Assert.Contains(read.Notes, n => n.Contains("no wavelengths"));
        Assert.Contains(read.Notes, n => n.Contains("no field table"));

        string text = "LEN NEW \"u\" 1 2\nEBR 1; ANG 0\nTH 1e20\nNXT\nTH 5\nCVX 0.01\nFOO 1\nGTO 0\nNXT\nEND 2\nBAR\n";
        var problems = Assert.Throws<ZemaxFileException>(() => Strict(text)).Problems;
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("Line 6") && p.Contains("CVX"));
        Assert.Contains(problems, p => p.Contains("Line 7") && p.Contains("FOO"));
        Assert.Contains(problems, p => p.Contains("Line 8") && p.Contains("GTO"));
        Assert.Contains(problems, p => p.Contains("Line 11") && p.Contains("BAR"));
        Assert.Equal(problems, Lenient(text).Warnings);
    }
}
