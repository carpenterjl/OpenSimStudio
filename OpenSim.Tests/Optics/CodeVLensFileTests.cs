using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Interop.CodeV;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Sequential;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-95: CODE V sequence files (docs/Optics-Conventions.md section 54). The files are written
/// here from the command rules section 54 lists; none is copied from a vendor. The gate (a lens by
/// hand and imported trace identically) is <see cref="CodeVOsloImportGateTests"/>.
/// </summary>
public sealed class CodeVLensFileTests
{
    private const string Head = "RDM\nEPD 10\nWL 550\nYAN 0\nSO 0 1e20\n";

    private static ZemaxImportResult Lenient(string text) =>
        CodeVLensReader.Read(text, CodeVOsloImportGateTests.Catalogs(), new ZemaxImportOptions { UnsupportedAsWarnings = true });

    [Fact]
    public void Commands_FollowTheReadersTextRules()
    {
        var c = CodeVLensReader.Commands("TIT 'a; b ! c' ! a comment\nS 1 &\n 2 ; K 3\n\nWL 1 2");
        Assert.Equal(4, c.Count);
        Assert.Equal(new[] { 1, 2, 2, 5 }, c.Select(x => x.Line));
        Assert.Equal(new[] { "TIT", "a; b ! c" }, c[0].Tokens);
        Assert.Equal(new[] { "S", "1", "2" }, c[1].Tokens);
        Assert.Equal(new[] { "K", "3" }, c[2].Tokens);
        Assert.Equal(new[] { "WL", "1", "2" }, c[3].Tokens);
    }

    [Fact]
    public void Curvatures_AreRadii_UnderRdmN_AndReflIsAMirror()
    {
        var read = CodeVLensReader.Read("rdm n\nepd 10\nwl 550\nso 0 1e20\ns 0.02 5 refl\nsi 0 0\n", catalogs: null);
        var s = read.Optics.Surfaces![1];
        Assert.Equal(1 / 0.02, s.Radius);
        Assert.Equal(SavedSurfaces.Mirror, s.Material);
        Assert.True(s.IsStop);
        Assert.Contains(read.Notes, n => n.Contains("STO"));
    }

    [Fact]
    public void ANonZeroSurfaceValueWithoutRdm_IsAProblem()
    {
        var ex = Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read("EPD 10\nWL 550\nSO 0 1e20\nS 0.02 5\nSI 0 0\n", null));
        var p = Assert.Single(ex.Problems);
        Assert.Contains("Line 4", p);
        Assert.Contains("RDM", p);
    }

    private const string WithUnsupported =
        "RDM\nEPD 10\nWL 550\nYAN 0\nSO 0 1e20\n" +   // lines 1–5
        "S 50 5 517642\n" +                          // 6: a six-digit glass code
        "ZDE 1\n" +                                  // 7: a z decenter (listed when the lens is built)
        "H 1e-20\n" +                                // 8: r^18
        "FOO 3\n" +                                  // 9: unknown
        "NA 0.1\n" +                                 // 10: image-space NA
        "THI Z2 4\n" +                               // 11: a zoom position
        "PRV\nPWL 550\n'MYGLASS' 1.5\nEND\n" +       // 12–15: a private catalog, one problem
        "SI 0 0\n";

    [Fact]
    public void UnsupportedContent_FailsLoudly_NamingEachLine_OrIsListedAsWarnings()
    {
        var ex = Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read(WithUnsupported, CodeVOsloImportGateTests.Catalogs()));
        Assert.Equal(7, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("Line 6") && p.Contains("517642"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 7") && p.Contains("ZDE"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 8") && p.Contains("r^18"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 9") && p.Contains("FOO"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 10") && p.Contains("NA"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 11") && p.Contains("zoom"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 12") && p.Contains("PRV"));

        var lenient = Lenient(WithUnsupported);
        Assert.Equal(ex.Problems, lenient.Warnings);
        Assert.Equal(3, lenient.Optics.Surfaces!.Count);
    }

    [Fact]
    public void GlassTokens_ReadAsTheReadersDo()
    {
        string text = Head +
            "S 0 1 1.62:36.4\n" +     // 1: nd:Vd
            "S 0 1 569.631\n" +       // 2: NNN.VVV
            "S 0 1 AIR\n" +           // 3
            "S 0 1 NBK7_TESTCAT\n" +  // 4: hyphen dropped, catalog named
            "S 0 1 N-SF5\n" +         // 5: a bare name
            "S 0 1 NOSUCH\n" +        // 6: not in the catalogs
            "S 0 1\nRMD REFL\n" +     // 7: a mirror by RMD
            "SI 0 0\n";
        var ex = Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read(text, CodeVOsloImportGateTests.Catalogs()));
        Assert.Contains("NOSUCH", Assert.Single(ex.Problems));
        var s = Lenient(text).Optics.Surfaces!;
        Assert.Equal(1.62, s[1].ModelGlass!.Nd);
        Assert.Equal(36.4, s[1].ModelGlass!.Vd);
        Assert.Equal(0, s[1].ModelGlass!.DeltaPgF);
        Assert.Equal(1.569, s[2].ModelGlass!.Nd);
        Assert.Equal(63.1, s[2].ModelGlass!.Vd);
        Assert.True(SavedSurfaces.IsAir(s[3]));
        Assert.Equal("TESTCAT:N-BK7", s[4].Material);
        Assert.Equal("TESTCAT:N-SF5", s[5].Material);
        Assert.Equal("NOSUCH", s[6].Material);
        Assert.Equal(SavedSurfaces.Mirror, s[7].Material);
    }

    [Fact]
    public void SurfaceQualifiers_NameTheSurface_AndSolvesKeepTheWrittenValue()
    {
        var read = CodeVLensReader.Read(Head + "S 50 5\nS -50 10\n  THI HMY 0\nTHI S1 7\nSTO S2\nCCY 0\nSI 0 0\n", null);
        var s = read.Optics.Surfaces!;
        Assert.Equal(7, s[1].Thickness);
        Assert.Equal(10, s[2].Thickness);
        Assert.True(s[2].IsStop);
        Assert.False(s[1].IsStop);
        Assert.Contains(read.Notes, n => n.StartsWith("CCY"));
        Assert.Contains(read.Notes, n => n.StartsWith("THI with a solve qualifier"));
    }

    /// <summary>
    /// The decenter conversion. RayOptics (util/misc_math.py euler2rot3d with euler2opt) builds
    /// CODE V's rotation from the rotating axes x, y, z with α and β negated; intrinsic rotations
    /// compose by right-multiplying, so the rotation is Rx(−α)·Ry(−β)·Rz(γ) and a point p of the
    /// surface sits at (XDE, YDE, 0) + R·p (derived from the cited recipe). The saved "before"
    /// tilt must give that transform; REV must be its exact inverse after the surface.
    /// </summary>
    [Fact]
    public void Decenters_AreConvertedToThisProgramsSigns_AndRevIsTheExactInverse()
    {
        string text = Head + "S 0 10\nSTO\n" +
            "S 0 10\n  XDE 0.3; YDE -0.2; ADE 3; BDE -1; CDE 5; REV\n" +
            "S 0 10\n  XDE 0.3; YDE -0.2; ADE 3; BDE -1; CDE 5\n" +
            "SI 0 0\n";
        var s = CodeVLensReader.Read(text, null).Optics.Surfaces!;
        var before = s[3].TiltBefore!;
        Assert.Null(s[3].TiltAfter);
        Assert.Equal((0.3, -0.2, -3.0, 1.0, 5.0), (before.DecenterX, before.DecenterY, before.TiltX, before.TiltY, before.TiltZ));
        Assert.Null(before.Order);

        double alpha = 3, beta = -1, gamma = 5;
        double rad = Math.PI / 180;
        var expected = RigidTransform.Compose(RigidTransform.FromTranslation(new Vector3D(0.3, -0.2, 0)),
            RigidTransform.Compose(RigidTransform.Compose(RigidTransform.RotationAboutX(-alpha * rad), RigidTransform.RotationAboutY(-beta * rad)),
                RigidTransform.RotationAboutZ(gamma * rad)));
        var forward = new TiltDecenter(before.DecenterX, before.DecenterY, before.TiltX, before.TiltY, before.TiltZ);
        AssertClose(expected, forward.ToTransform());

        Assert.Null(s[2].TiltBefore);
        var after = s[2].TiltAfter!;
        Assert.Null(after.Mode);
        var reversed = forward.Reversed();
        Assert.Equal(SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter), after.Order);
        Assert.Equal((reversed.DecenterX, reversed.DecenterY, reversed.TiltXDegrees, reversed.TiltYDegrees, reversed.TiltZDegrees),
            (after.DecenterX, after.DecenterY, after.TiltX, after.TiltY, after.TiltZ));
    }

    internal static void AssertClose(RigidTransform a, RigidTransform b)
    {
        foreach (var (u, v) in new[] { (a.Col0, b.Col0), (a.Col1, b.Col1), (a.Col2, b.Col2), (a.Translation, b.Translation) })
        {
            Assert.Equal(u.X, v.X, 14);
            Assert.Equal(u.Y, v.Y, 14);
            Assert.Equal(u.Z, v.Z, 14);
        }
    }

    /// <summary>VUY 0.125 and VLY 0.25 give VDY = (0.25 − 0.125)/2 = 0.0625 and VCY = 0.1875, so
    /// section 16's P′y = VDY + Py(1 − VCY) puts the pupil's edges at 0.875 = 1 − VUY and
    /// −0.75 = −(1 − VLY) (derived; the binary fractions are exact).</summary>
    [Fact]
    public void VignettingFactors_BecomeDecenterAndCompression()
    {
        var f = CodeVLensReader.Read("RDM\nEPD 10\nWL 550\nYAN 0 5\nWTF 1 3\nVUY 0 0.125\nVLY 0 0.25\nSO 0 1e20\nS 0 5\nSI 0 0\n", null).Optics.SystemData!.Fields![1];
        Assert.Equal(5, f.Y);
        Assert.Equal(3, f.Weight);
        Assert.Equal(0.0625, f.DecenterY);
        Assert.Equal(0.1875, f.CompressionY);
        Assert.Equal(1 - 0.125, f.DecenterY + (1 - f.CompressionY));
        Assert.Equal(-(1 - 0.25), f.DecenterY - (1 - f.CompressionY));
    }

    [Fact]
    public void Apertures_OneEachSurface()
    {
        string text = Head +
            "S 0 5\n  CIR 4\n" +
            "S 0 5\n  CIR OBS 1; ADX 0.1; ADY -0.2\n" +
            "S 0 5\n  REX 3; REY 2\n" +
            "S 0 5\n  ELX 3; ELY 2\n  CIR EDG 5\n" +
            "S 0 5\n  CIR 4\n  REX 2\n" +     // line 17: a second aperture
            "SI 0 0\n";
        var p = Assert.Single(Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read(text, null)).Problems);
        Assert.Contains("Line 17", p);
        var read = Lenient(text);
        var s = read.Optics.Surfaces!;
        Assert.Equal((SavedSurfaces.CircularAperture, 0.0, 4.0, false), (s[1].Aperture!.Type, s[1].Aperture!.MinRadius, s[1].Aperture!.MaxRadius, s[1].Aperture!.IsObscuration));
        Assert.Equal((SavedSurfaces.CircularAperture, 1.0, true, 0.1, -0.2), (s[2].Aperture!.Type, s[2].Aperture!.MaxRadius, s[2].Aperture!.IsObscuration, s[2].Aperture!.DecenterX, s[2].Aperture!.DecenterY));
        Assert.Equal((SavedSurfaces.RectangularAperture, 3.0, 2.0), (s[3].Aperture!.Type, s[3].Aperture!.HalfWidthX, s[3].Aperture!.HalfWidthY));
        Assert.Equal((SavedSurfaces.EllipticalAperture, 3.0, 2.0), (s[4].Aperture!.Type, s[4].Aperture!.HalfWidthX, s[4].Aperture!.HalfWidthY));
        Assert.Contains(read.Notes, n => n.Contains("EDG"));
        Assert.Equal(4, s[5].Aperture!.MaxRadius);
    }

    [Fact]
    public void SystemData_FocusShift_ObjectDistance_AndDefaults()
    {
        var read = CodeVLensReader.Read("RDM\nFNO 4\nDIM I\nWL 486.1327 587.5618\nYRI 0 1.5\nTEM 20\nSO 0 100\nS 20 2\nS 0 10\nSI 0 -0.25\n", null);
        var sys = read.Optics.SystemData!;
        Assert.Equal(ApertureTypes.Names.SavedName(ApertureType.ParaxialWorkingFNumber), sys.ApertureType);
        Assert.Equal(4, sys.ApertureValue);
        Assert.Equal(ConventionNames.LensUnits.SavedName(LensUnit.Inch), sys.LensUnit);
        Assert.Equal(FieldTypes.Names.SavedName(FieldType.RealImageHeight), sys.FieldType);
        Assert.Equal(new[] { 0.0, 1.5 }, sys.Fields!.Select(f => f.Y));
        Assert.Equal(new[] { 0.4861327, 0.5875618 }, sys.Wavelengths!.Select(w => w.Micrometers));
        Assert.Equal(1, sys.PrimaryWavelength);
        var s = read.Optics.Surfaces!;
        Assert.False(s[0].ThicknessIsInfinite);
        Assert.Equal(100, s[0].Thickness);
        Assert.Equal(10 + -0.25, s[2].Thickness);
        Assert.Equal(0, s[3].Thickness);
        Assert.True(s[1].IsStop);
        Assert.Contains(read.Notes, n => n.Contains("REF"));
        Assert.Contains(read.Notes, n => n.StartsWith("TEM 20"));
        Assert.Contains(read.Notes, n => n.Contains("focus shift"));

        var tem = Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read("RDM\nEPD 1\nWL 550\nTEM 25\nSO 0 1e20\nS 0 1\nSI 0 0\n", null));
        Assert.Contains("TEM 25", Assert.Single(tem.Problems));
        var none = Assert.Throws<ZemaxFileException>(() => CodeVLensReader.Read("RDM\nWL 550\nSO 0 1e20\nS 0 1\nSI 0 0\n", null));
        Assert.Contains("no aperture", Assert.Single(none.Problems));
    }
}
