using OpenSim.Core.Model;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

// wire-14: the conformance macros of FICL (OP-50's single-mode engine), EXPORTCAD (OP-96's CAD
// export) and the non-sequential object keywords and functions (OP-70's saved objects). Each
// follows its help page under plans/sources/optics/op-99-help/ ("Using the FICL() Function",
// "EXPORTCAD (keywords)", "SETNSCPARAMETER", "SETNSCPOSITION", "SETNSCPROPERTY" and the Numeric
// Functions page for NOBJ, NPAR, NPOS, NPRO, OBJC).
public sealed partial class ZplConformanceTests
{
    // ================================================================ FICL

    /// <summary>The fixture of FiberCouplingTests as saved settings: the object point at the centre
    /// of curvature of a concave mirror of radius 100 mm, imaged back onto itself; entrance pupil
    /// 87 mm, one field (0, 0) by object height, one wavelength 1 µm. Arbitrary test inputs.</summary>
    private static OpticsSettings FiberMirror() => new()
    {
        SystemData = new OpticsSystemSettings
        {
            Title = "Fiber mirror", ApertureType = "EntrancePupilDiameter", ApertureValue = 87, FieldType = "ObjectHeight",
            Fields = new() { new() },
            Wavelengths = new() { new() { Micrometers = 1.0 } },
        },
        Surfaces = new()
        {
            new() { Thickness = 100 },
            new() { Radius = -100, Thickness = -100, Material = "MIRROR", IsStop = true },
            new(),
        },
    };

    private const string FiclMacro =
        "VEC1 0, 2\nVEC1 1, 1\nVEC1 2, 1\nVEC1 4, 0.1\nVEC1 5, 0.1\nOPTRETURN 0, FICL(1)\n"
        + "VEC1 18, 1\nOPTRETURN 1, FICL(1)\nVEC1 18, 0\n"
        + "VEC1 10, 0.001 / (ACOS(-1) * 0.1)\nOPTRETURN 2, FICL(1)";

    [Fact]
    public void Ficl_GivesTheSingleModeCoupling_OfTheVectorsSettings()
    {
        // Positions per the help: 0 sampling (2 → 64 × 64), 1 wavelength, 2 field, 4 source NA x,
        // 5 receiver NA x (NA y 0 → NA x), 18 Huygens, 10 receiver decenter x in lens units.
        // Matched source and receiver NA 0.1 through a mirror imaging its centre of curvature onto
        // itself couple fully, fast and Huygens; a decenter of one waist w = λ/(π·NA) gives
        // exp(−d²/w²) = exp(−1). Closed forms derived in FiberCouplingTests (MatchedGaussians,
        // LateralOffset); 1e-7 is the tolerance that suite holds on the same fixture at 64 × 64.
        var r = Run(FiclMacro, FiberMirror()).OptReturn;
        Near(1, r[0], 1e-7);
        Near(1, r[1], 1e-7);
        Near(Math.Exp(-1), r[2], 1e-7);

        string Fails(string extra) => Assert.Throws<ZplException>(() => Run("VEC1 0, 2\nVEC1 1, 1\nVEC1 2, 1\nVEC1 4, 0.1\nVEC1 5, 0.1\n" + extra + "\nx = FICL(1)", FiberMirror())).Message;
        Assert.Contains("not built", Fails("VEC1 15, 1"));          // polarization: OP-50 refuses it
        Assert.Contains("0 or 1", Fails("VEC1 3, 2"));
        Assert.Contains("1 (32 × 32) to 7", Fails("VEC1 0, 0"));
        Assert.Contains("1 to 1", Fails("VEC1 2, 2"));
        Assert.Contains("1 to 4", Assert.Throws<ZplException>(() => Run("x = FICL(5)", FiberMirror())).Message);
    }

    // ================================================================ EXPORTCAD

    private const string ExportCadMacro =
        "VEC1 1, 1\nVEC1 2, 32\nVEC1 3, 1\nVEC1 4, 2\nVEC1 8, 1\nVEC1 9, 2\nVEC1 10, 3\nVEC1 19, 0\nEXPORTCAD \"singlet.step\"";

    [Fact]
    public void ExportCad_WritesTheStepFile_OfCadExporter_WithTheVectorsSettings()
    {
        // VEC1(1) = 1 STEP, (2) 32 spline points, (3)–(4) surfaces 1 to 2, (8) = 1 solids,
        // (9) = 2 the Y fan, (10) 3 rays, (11) and (12) 0 all wavelengths and fields, (19) = 0
        // the tolerance 1E-4 (help, EXPORTCAD). The file must be CadExporter's own for the same
        // settings, apart from the FILE_NAME header line, which carries the time it was written.
        Run(ExportCadMacro);
        string macroFile = Path.Combine(_dir, "singlet.step");
        Assert.True(File.Exists(macroFile));
        string direct = Path.Combine(_dir, "direct.step");
        CadExporter.Export(Singlet(), NoCatalogs, new CadExportSettings
        {
            Format = CadExportFormat.Step, FirstSurface = 1, LastSurface = 2, Tolerance = 1e-4,
            RayPattern = CadRayPattern.YFan, NumberOfRays = 3, Field = 0, Wavelength = 0,
        }, direct);
        static string[] Body(string p) => File.ReadAllLines(p).Where(l => !l.StartsWith("FILE_NAME(", StringComparison.Ordinal)).ToArray();
        Assert.StartsWith("ISO-10303-21;", File.ReadAllText(macroFile));
        Assert.Equal(Body(direct), Body(macroFile));

        string Fails(string extra) => Assert.Throws<ZplException>(() => Run(ExportCadMacro.Replace("EXPORTCAD", extra + "\nEXPORTCAD"))).Message;
        Assert.Contains("OP-76", Fails("VEC1 1, 2"));                 // SAT
        Assert.Contains("layers", Fails("VEC1 5, 1"));
        Assert.Contains("solids only", Fails("VEC1 8, 0"));
        Assert.Contains("16, 32, 64", Fails("VEC1 2, 20"));
        Assert.Contains("not built", Fails("VEC1 9, 7"));            // solid beams
        Assert.Contains("SETCONFIG", Fails("VEC1 18, 1"));
        Assert.Contains("non-sequential export is not built", Assert.Throws<ZplException>(() => Run(ExportCadMacro + ", \"h2\"")).Message);   // wire-16
    }

    // ================================================================ Non-sequential objects

    /// <summary>The singlet with two saved non-sequential objects: a Standard Lens (OpticStudio's
    /// nine parameters: front radius, conic, clear semi-diameter, edge, thickness, rear radius,
    /// conic, clear semi-diameter, edge) and a Rectangle (half widths). Arbitrary values; nothing
    /// is traced.</summary>
    private static OpticsSettings WithObjects() => Singlet() with
    {
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Standard Lens", Parameters = new() { 50, 0, 6, 6, 5, -50, 0, 6, 6 }, Material = "1.5", Comment = "lens" },
                new() { Type = "Rectangle", Parameters = new() { 2, 3 }, Z = 20, Comment = "Target", DetectingStandIn = true },
            },
        },
    };

    private const string NscObjectsMacro =
        "OPTRETURN 0, NOBJ(1)\nOPTRETURN 1, NPAR(1, 1, 5)\nSETNSCPARAMETER 1, 1, 5, 7\nOPTRETURN 2, NPAR(1, 1, 5)\n"
        + "SETNSCPOSITION 1, 2, 3, 30\nOPTRETURN 3, NPOS(1, 2, 3)\nSETNSCPOSITION 1, 2, 5, 10\nOPTRETURN 4, NPOS(1, 2, 5)\n"
        + "x = NPRO(1, 2, 0, 0)\nPRINT $BUFFER()\nx = NPRO(1, 2, 1, 0)\nPRINT $BUFFER()\n"
        + "SETNSCPROPERTY 1, 2, 1, 0, \"Screen\"\nOPTRETURN 5, OBJC(\"screen\")\nOPTRETURN 6, OBJC(\"none\")\n"
        + "SETNSCPROPERTY 1, 1, 29, 2, 1\nOPTRETURN 7, NPRO(1, 1, 29, 2)\nSETNSCPROPERTY 1, 1, 5, 1, \"AR\"\nx = NPRO(1, 1, 5, 1)\nPRINT $BUFFER()\n"
        + "SETNSCPROPERTY 1, 2, 2, 0, 1\nOPTRETURN 8, NPRO(1, 2, 2, 0)\nSETNSCPROPERTY 1, 1, 4, 0, \"MIRROR\"\n"
        + "SETNSCPROPERTY 1, 2, 15, 0, 1\nOPTRETURN 9, NPRO(1, 2, 15, 0)\nSETNSCPROPERTY 1, 2, 0, 0, \"nsc_sphe\"\nx = NPRO(1, 2, 0, 0)\nPRINT $BUFFER()";

    [Fact]
    public void NscObjectKeywords_ReadAndSetTheSavedObjects()
    {
        // NOBJ counts the objects; NPAR 5 of the Standard Lens is its thickness 5, then 7;
        // SETNSCPOSITION codes 3 (z) and 5 (tilt y); NPRO code 0 gives the help's type code
        // NSC_RECT and code 1 the comment; OBJC matches "Screen" ignoring case and gives −1 for
        // none; code 29 sets face 2's "Face Is" to 1 (Reflective); code 5 face 1's coating; code
        // 2 the reference object; code 4 the material; code 15 the global XYZ order; code 0 the
        // type by its code, case ignored (help, SETNSCPROPERTY).
        var result = Run(NscObjectsMacro, WithObjects());
        Assert.Equal(new double?[] { 2, 5, 7, 30, 10, 2, -1, 1, 1, 1 }, result.OptReturn.Take(10));
        Assert.Equal(Lines("NSC_RECT", "Target", "AR", "NSC_SPHE"), result.Output);
        var objects = result.System.NonSequential!.Objects!;
        Assert.Equal(7, objects[0].Parameters![4]);
        Assert.Equal("MIRROR", objects[0].Material);
        Assert.Contains(objects[0].Faces!, f => f.Face == 2 && f.FaceIs == "Reflective");
        Assert.Contains(objects[0].Faces!, f => f.Face == 1 && f.Coating == "AR");
        Assert.Equal("Sphere", objects[1].Type);
        Assert.Equal("Screen", objects[1].Comment);
        Assert.Equal(1, objects[1].ReferenceObject);
        Assert.Equal(30, objects[1].Z);
        Assert.Equal(10, objects[1].TiltY);
        Assert.True(objects[1].GlobalXyzRotationOrder);

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, WithObjects())).Message;
        Assert.Contains("OP-86", Fails("x = NOBJ(2)"));
        Assert.Contains("parameters 1 to 2", Fails("x = NPAR(1, 2, 3)"));
        Assert.Contains("object 3 does not exist", Fails("SETNSCPOSITION 1, 3, 1, 0"));
        Assert.Contains("OP-79", Fails("x = NPRO(1, 2, 17, 0)"));
        Assert.Contains("OP-72", Fails("SETNSCPROPERTY 1, 1, 0, 0, \"NSC_CONE\""));
        Assert.Contains("face 1 is not one", Fails("SETNSCPROPERTY 1, 2, 29, 1, 1"));
        // wire-15: NSTR is bound; this scene has no source objects, which the runner refuses by name.
        Assert.Contains("no source objects", Fails("NSTR 1, 0, 0, 0, 0, 1, 0"));
        Assert.Contains("no NSTR trace", Fails("x = LOST(1)"));
        Assert.Contains("OP-71", Assert.Throws<ZplNotBoundException>(() => Run("INSERTOBJECT 1, 1", WithObjects())).Message);
        Assert.Equal(0, Run("OPTRETURN 0, NOBJ(1)").OptReturn[0]);   // the singlet has no objects
    }
}
