using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Lightning;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

// wire-17: NSLT (OP-85's LightningTrace), SETNSCPROPERTY and NPRO codes 81–86 (OP-81's Volume
// Physics tab) and OP-84's path filters _n reaching ZRDFILTER and ZRDPLAYBACK. Each follows its
// help page under plans/sources/optics/op-99-help/ (NSLT, SETNSCPROPERTY).
public sealed partial class ZplConformanceTests
{
    private const string NsltMacro =
        "NSLT 1, 0, 0, 1\nOPTRETURN 0, NSDD(1, 2, 0, 0)\nNSLT 1, 1, 0, 1\nOPTRETURN 1, NSDD(1, 2, 0, 0)";

    /// <summary>NSLT fills the detector with the project's LightningTrace (the same call, so the
    /// same data to the bit) and adds to what it holds, as NSTR does; the help's ranges
    /// (ray_sampling 0–5, edge_sampling 0–4) and a non-source object are refused.</summary>
    [Fact]
    public void Nslt_FillsTheDetector_WithTheProjectsLightningTrace()
    {
        var r = Run(NsltMacro, PointOnRectangle()).OptReturn;
        var direct = ProjectNscLightning.Trace(new OpticsAnalysisContext("Macro", PointOnRectangle()) { Catalogs = NoCatalogs }, 0, 1);
        double total = ((NscFlatDetectorData)direct.Detectors.Single(d => d.ObjectNumber == 2)).Flux.Sum();
        Assert.True(total > 0);
        Near(total, r[0]);   // the same data; NSDD sums it in its own order
        Near(2 * total, r[1]);

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, PointOnRectangle())).Message;
        Assert.Contains("ray_sampling", Fails("NSLT 1, 0, 6, 0"));
        Assert.Contains("edge_sampling", Fails("NSLT 1, 0, 0, 5"));
        Assert.Contains("not a source object", Fails("NSLT 1, 2, 0, 0"));
        Assert.Contains("OP-86", Fails("NSLT 2, 0, 0, 0"));
    }

    private const string VolumeCodesMacro =
        "SETNSCPROPERTY 1, 1, 81, 0, 2\nSETNSCPROPERTY 1, 1, 82, 0, 2.5\nSETNSCPROPERTY 1, 1, 83, 0, 15\n"
        + "SETNSCPROPERTY 1, 1, 84, 0, \"Mie.dll\"\nSETNSCPROPERTY 1, 1, 85, 3, 0.7\nSETNSCPROPERTY 1, 1, 86, 0, \"1, 2, 1\"\n"
        + "OPTRETURN 0, NPRO(1, 1, 81, 0)\nOPTRETURN 1, NPRO(1, 1, 82, 0)\nOPTRETURN 2, NPRO(1, 1, 83, 0)\n"
        + "OPTRETURN 3, NPRO(1, 1, 85, 3)\nOPTRETURN 4, NPRO(1, 1, 85, 1)\nOPTRETURN 5, NPRO(1, 1, 85, 9)\n"
        + "x = NPRO(1, 1, 84, 0)\nPRINT $BUFFER()\nx = NPRO(1, 1, 86, 0)\nPRINT $BUFFER()\n"
        + "OPTRETURN 6, NPRO(1, 2, 81, 0)";

    /// <summary>Codes 81 to 86 set and read the Volume Physics tab (help, SETNSCPROPERTY: 81 the
    /// model, "0 for 'No Bulk Scattering', 1 for 'Angle Scattering', and 2 for 'DLL Defined
    /// Scattering'"; 82 mean free path; 83 angle; 84 DLL name; 85 the DLL parameter numbered by the
    /// face argument from 1; 86 the wavelength shift string). Parameters not set read 0; an object
    /// without volume physics reads model 0.</summary>
    [Fact]
    public void VolumeCodes_SetAndReadTheVolumePhysicsTab()
    {
        var result = Run(VolumeCodesMacro, WithObjects());
        Assert.Equal(new double?[] { 2, 2.5, 15, 0.7, 0, 0, 0 }, result.OptReturn.Take(7));
        Assert.Equal(Lines("Mie.dll", "1, 2, 1"), result.Output);
        var v = result.System.NonSequential!.Objects![0].VolumePhysics!;
        Assert.Equal("DLL Defined Scattering", v.Model);
        Assert.Equal(2.5, v.MeanPath);
        Assert.Equal(15, v.Angle);
        Assert.Equal("Mie.dll", v.Dll);
        Assert.Equal(new List<double> { 0, 0, 0.7 }, v.DllParameters);
        Assert.Equal("1, 2, 1", v.WavelengthShift);

        string Fails(string macro, OpticsSettings? system = null) => Assert.Throws<ZplException>(() => Run(macro, system ?? WithObjects())).Message;
        Assert.Contains("Phosphors and Fluorescence has no code", Fails("SETNSCPROPERTY 1, 1, 81, 0, 3"));
        Assert.Contains("The first parameter is 1", Fails("SETNSCPROPERTY 1, 1, 85, 0, 1"));
        Assert.Contains("in, out, prob", Fails("SETNSCPROPERTY 1, 1, 86, 0, \"1, 2\""));
        Assert.Contains("must be a number", Fails("SETNSCPROPERTY 1, 1, 82, 0, \"long\""));
        var phosphor = WithObjects() with
        {
            NonSequential = WithObjects().NonSequential! with
            {
                Objects = WithObjects().NonSequential!.Objects!.Select((o, i) => i == 0 ? o with { VolumePhysics = new OpticsNscVolumePhysicsSettings { Model = "Phosphors and Fluorescence" } } : o).ToList(),
            },
        };
        Assert.Contains("numbers only 0", Fails("x = NPRO(1, 1, 81, 0)", phosphor));
    }

    private const string PathFilterPlaybackMacro =
        "NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"all.ZRD\", \"\", 3\nOPTRETURN 0, NSDD(1, 2, 0, 0)\n"
        + "ZRDFILTER \"all.ZRD\", \"p1.ZRD\", \"_1\"\nZRDPLAYBACK \"p1.ZRD\", 1, 2, 1\nOPTRETURN 1, NSDD(1, 2, 0, 0)\n"
        + "ZRDPLAYBACK \"all.ZRD\", 1, 2, 1, \"_1\"\nOPTRETURN 2, NSDD(1, 2, 0, 0)";

    /// <summary>The project's path filter _1 = "1, 2" (source 1, then object 2 by any face) reaches
    /// the macro filters through NscRayDatabaseProject.Filter: every ray that ends on the absorbing
    /// detector has exactly that path, so filtering and playing back by _1 gives the traced total;
    /// an undefined _2 is refused by name.</summary>
    [Fact]
    public void PathFilters_ReachZrdFilterAndZrdPlayback()
    {
        var system = PointOnRectangle() with
        {
            NonSequential = PointOnRectangle().NonSequential! with { PathFilters = new List<string> { "1, 2" } },
        };
        var r = Run(PathFilterPlaybackMacro, system).OptReturn;
        Assert.True(r[0] > 0);
        Near(r[0]!.Value, r[1]);
        Near(r[0]!.Value, r[2]);
        Assert.Contains("_2 is not defined", Assert.Throws<ZplException>(() => Run("ZRDPLAYBACK \"all.ZRD\", 1, 2, 1, \"_2\"", system)).Message);
    }
}
