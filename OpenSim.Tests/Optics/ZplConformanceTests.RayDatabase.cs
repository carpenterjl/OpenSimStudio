using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

// wire-16: the conformance macros of NSTR's scatter flag (OP-80) and save arguments (OP-83), the
// ZRD keywords ZRDPLAYBACK, ZRDFILTER, ZRDSUM and ZRDAPPEND (OP-83's NscZrdFile and
// NscRayPlayback) and SETNSCPROPERTY/NPRO's Coat/Scatter and Scatter To codes (OP-80's saved
// fields). Each follows its help page under plans/sources/optics/op-99-help/ (NSTR, NSTR2,
// ZRDPLAYBACK, ZRDFILTER, ZRDSUM, ZRDAPPEND, ZRDSAVERAYS, SETNSCPROPERTY (keywords)).
public sealed partial class ZplConformanceTests
{
    /// <summary>A Source Point (1 W, 500 analysis rays, a 10° semi-cone along +z, object 1), a
    /// MIRROR Rectangle (±5 mm, object 2) 10 mm away facing it, and an absorbing Detector
    /// Rectangle (±5 mm, 4 × 4 pixels, object 3) in the plane x = 8 mm (tilted 90° about y, its
    /// centre at z = 5 mm), at 0.555 µm. Arbitrary test inputs. The cone (1.8 mm at the mirror)
    /// and its specular reflection (3.5 mm back at the source plane) stay within 3.6 mm of the
    /// axis, so without scattering no ray reaches the detector (derived from the geometry).</summary>
    private static OpticsSettings MirrorAndSideDetector() => new()
    {
        SystemData = new OpticsSystemSettings { Title = "NSC", Wavelengths = new() { new() { Micrometers = 0.555 } } },
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Source Point", Parameters = new() { 10, 500, 1, 0, 0, 10 } },
                new() { Type = "Rectangle", Z = 10, Material = "MIRROR", Parameters = new() { 5, 5 } },
                new() { Type = "Detector Rectangle", X = 8, Z = 5, TiltY = 90, Material = "ABSORB", Parameters = new() { 5, 5, 4, 4 } },
            },
        },
    };

    private const string ScatterCodesMacro =
        "SETNSCPROPERTY 1, 2, 7, 0, 1\nSETNSCPROPERTY 1, 2, 8, 0, 1\nSETNSCPROPERTY 1, 2, 9, 0, 1\n"
        + "OPTRETURN 0, NPRO(1, 2, 7, 0)\nOPTRETURN 1, NPRO(1, 2, 8, 0)\nOPTRETURN 2, NPRO(1, 2, 9, 0)\n"
        + "NSTR 1, 0, 0, 0, 0, 1, 5\nOPTRETURN 3, NSDD(1, 3, 0, 0)\nx = NSDD(1, 0, 0, 0)\n"
        + "NSTR 1, 0, 0, 1, 0, 1, 5\nOPTRETURN 4, NSDD(1, 3, 0, 0)\n"
        + "SETNSCPROPERTY 1, 2, 37, 0, 1\nOPTRETURN 5, NPRO(1, 2, 37, 0)\n"
        + "SETNSCPROPERTY 1, 2, 151, 0, 0\nSETNSCPROPERTY 1, 2, 153, 0, \"3\"\nx = NPRO(1, 2, 153, 0)\nPRINT $BUFFER()\n"
        + "SETNSCPROPERTY 1, 2, 152, 0, \"1 3 2.5 0.5\"\nOPTRETURN 6, NPRO(1, 2, 151, 0)\n"
        + "SETNSCPROPERTY 1, 2, 7, 0, 2\nSETNSCPROPERTY 1, 2, 10, 0, 0.2\nOPTRETURN 7, NPRO(1, 2, 10, 0)";

    [Fact]
    public void Nstr_ScattersWithTheScatterFlag_AndTheScatterCodesSetTheFace()
    {
        var result = Run(ScatterCodesMacro, MirrorAndSideDetector());
        var r = result.OptReturn;
        // Codes 7 (1 Lambertian), 8 (fraction) and 9 (number of rays) read back as set.
        Assert.Equal(new double?[] { 1, 1, 1 }, r.Take(3));
        Assert.Equal(0, r[3]);                          // scatter 0: no ray reaches the side detector (fixture comment)
        Assert.True(r[4] > 0);                          // scatter 1: the Lambertian mirror sends rays to it
        // The macro's scatter trace is OP-77's runner with scatter on, the same seed and settings.
        var system = MirrorAndSideDetector();
        var objects = system.NonSequential!.Objects!.ToList();
        objects[1] = objects[1] with { Faces = new() { new() { Face = 0, ScatterModel = "Lambertian", ScatterFraction = 1, ScatterRays = 1 } } };
        system = system with { NonSequential = system.NonSequential with { Objects = objects } };
        var direct = ProjectNscSources.Trace(new OpticsAnalysisContext("Macro", system) { Catalogs = NoCatalogs }, seed: 5, ignoreErrors: true, scatter: true);
        var d = Assert.IsType<NscFlatDetectorData>(direct.Detectors!.Single(x => x.ObjectNumber == 3));
        Near(d.Flux.Sum(), r[4]);
        Assert.Equal(1, r[5]);                          // code 37: Thin Window Scattering
        Assert.Equal(Lines("3"), result.Output);        // code 153: the Scatter To list
        Assert.Equal(0, r[6]);                          // code 151: 0 scatter to list
        Assert.Equal(0.2, r[7]);                        // code 10 on a Gaussian face: sigma
        var saved = result.System.NonSequential!.Objects![1];
        Assert.Equal("Gaussian", saved.Faces![0].ScatterModel);
        Assert.Equal(0.2, saved.Faces[0].ScatterSigma);
        Assert.True(saved.Faces[0].ThinWindowScattering);
        Assert.Equal("Scatter To List", saved.ScatterToMethod);
        Assert.Equal("3", saved.ScatterToList);
        var target = Assert.Single(saved.ScatterTargets!);
        Assert.Equal((3, 2.5, 0.5), (target.Object!.Value, target.Size!.Value, target.Limit!.Value));

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, MirrorAndSideDetector())).Message;
        Assert.Contains("no DLLs are loaded", Fails("SETNSCPROPERTY 1, 2, 7, 0, 4"));
        Assert.Contains("no DLLs are loaded", Fails("SETNSCPROPERTY 1, 2, 21, 0, 1"));
        Assert.Contains("scatter profiles", Fails("SETNSCPROPERTY 1, 2, 6, 0, \"P\""));
        Assert.Contains("set its scatter mode", Fails("SETNSCPROPERTY 1, 2, 7, 0, 1\nSETNSCPROPERTY 1, 2, 10, 0, 0.2"));
        Assert.Contains("full path", Fails("SETNSCPROPERTY 1, 2, 7, 0, 5\nSETNSCPROPERTY 1, 2, 31, 0, \"a.bsdf\""));
        Assert.Contains("0 to 300", Fails("SETNSCPROPERTY 1, 2, 9, 0, 301"));
        Assert.Contains("not bound for NPRO", Fails("x = NPRO(1, 2, 152, 0)"));
    }

    private const string ZrdMacro =
        "NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"all.ZRD\", \"\", 3\nOPTRETURN 0, NSDD(1, 2, 0, 0)\nx = NSDD(1, 0, 0, 0)\n"
        + "ZRDPLAYBACK \"all.ZRD\", 1, 0, 0\nOPTRETURN 1, NSDD(1, 2, 0, 0)\n"
        + "ZRDPLAYBACK \"all.ZRD\", 1, 2, 1, \"H2\"\nOPTRETURN 2, NSDD(1, 2, 0, 0)\n"
        + "ZRDPLAYBACK \"all.ZRD\", 1, 2, 0\nOPTRETURN 3, NSDD(1, 2, 0, 0)\n"
        + "ZRDFILTER \"all.ZRD\", \"hit.ZRD\", \"H2\"\nZRDPLAYBACK \"hit.ZRD\", 1, 2, 1\nOPTRETURN 4, NSDD(1, 2, 0, 0)\n"
        + "ZRDSUM \"all.ZRD\", \"hit.ZRD\", \"sum.ZRD\"\nZRDPLAYBACK \"sum.ZRD\", 1, 2, 1\nOPTRETURN 5, NSDD(1, 2, 0, 0)\n"
        + "ZRDAPPEND \"all.ZRD\", \"hit.ZRD\"\nZRDPLAYBACK \"hit.ZRD\", 1, 2, 1\nOPTRETURN 6, NSDD(1, 2, 0, 0)\n"
        + "NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"ufd.ZRD\", \"H2\", 0";

    [Fact]
    public void NstrSavesRays_AndTheZrdKeywordsPlayFilterSumAndAppendThem()
    {
        var r = Run(ZrdMacro, PointOnRectangle()).OptReturn;
        var run = Direct(PointOnRectangle(), 7);
        var d = Assert.IsType<NscFlatDetectorData>(Assert.Single(run.Detectors!));
        double total = d.Flux.Sum();
        Near(total, r[0]);
        // OP-83's playback repeats the trace's reduction: an unfiltered replay is the traced data to the bit.
        Assert.Equal(r[0], r[1]);
        // Only trees that hit the detector (object 2) leave flux on it, so the H2 replay holds it
        // all (adding the zeros of the other trees is exact).
        Near(total, r[2]);
        Near(2 * total, r[3]);                          // clear 0: added to what the detector held
        Near(total, r[4]);                              // ZRDFILTER kept the H2 trees
        Near(2 * total, r[5]);                          // ZRDSUM: all + hit
        Near(2 * total, r[6]);                          // ZRDAPPEND: all appended to hit
        // zrd_format 0 writes uncompressed full data; the H2 save filter keeps each tree that hit
        // the absorbing detector, one per ray striking it (NSDD Pix# −3).
        string ufd = Path.Combine(_dir, "ufd.ZRD");
        Assert.Equal(NscZrdFormat.UncompressedFullData, NscZrdFile.Detect(ufd));
        Assert.Equal(d.SpatialHits, NscZrdFile.Read(ufd, LensUnit.Millimeter).Rays.Count);
        Assert.Equal(NscZrdFormat.OpenSimFull, NscZrdFile.Detect(Path.Combine(_dir, "sum.ZRD")));

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, PointOnRectangle())).Message;
        Assert.Contains("compressed basic data", Fails("NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"a.ZRD\", \"\", 1"));
        Assert.Contains("source file", Fails("NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"2-a.DAT\", \"\", 0"));
        Assert.Contains("distinct", Fails("ZRDFILTER \"all.ZRD\", \"all.ZRD\", \"H2\""));
        Assert.Contains("same ZRD format", Fails("ZRDSUM \"all.ZRD\", \"ufd.ZRD\", \"mix.ZRD\""));
        Assert.Contains("UFD", Fails("ZRDPLAYBACK \"ufd.ZRD\", 1, 0, 1"));
        Assert.Contains("not a detector", Fails("ZRDPLAYBACK \"all.ZRD\", 1, 1, 1"));
        Assert.Contains("source file", Assert.Throws<ZplNotBoundException>(() => Run("ZRDSAVERAYS \"all.ZRD\", \"b.DAT\", \"\", 2", PointOnRectangle())).Message);
    }

    [Fact]
    public void Opev_EvaluatesANonSequentialObjectOperand_OnASystemWithoutSurfaces()
    {
        // NPZV (OP-88): Int1 Surf 1, Int2 Object 2, Data1 Ref? 0, the z position in lens units;
        // the Detector Rectangle of PointOnRectangle sits at z = 10.
        var r = Run("OPTRETURN 0, OPEV(OCOD(\"NPZV\"), 1, 2, 0, 0, 0, 0)", PointOnRectangle()).OptReturn;
        Assert.Equal(10, r[0]);
    }
}
