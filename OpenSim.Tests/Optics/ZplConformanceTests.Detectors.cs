using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Photometry;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

// wire-15: the conformance macros of the non-sequential trace and the detectors: NSTR, NSTR2
// (OP-77's runner), NSDD, NSDE, NSDP, NSDC, LOST, SAVEDETECTOR and LOADDETECTOR (OP-79's data and
// files), and the Sources tab and pixel-interpolation codes of SETNSCPROPERTY and NPRO. Each
// follows its help page under plans/sources/optics/op-99-help/ (NSTR, NSTR2, SAVEDETECTOR,
// LOADDETECTOR, SETNSCPROPERTY, Numeric Functions) and, for the pixel and data codes,
// op-55b-help/ "Non-sequential Ray Tracing and Detector Operands".
public sealed partial class ZplConformanceTests
{
    /// <summary>A Source Point (1 W, 1000 analysis rays, a 40° semi-cone, object 1) 10 mm in front
    /// of an absorbing Detector Rectangle (±5 mm, 10 × 10 pixels, object 2), at 0.555 µm. The cone
    /// edge (10·tan 40° = 8.4 mm) passes the detector's corner (7.07 mm), so some rays escape.
    /// Arbitrary test inputs.</summary>
    private static OpticsSettings PointOnRectangle() => new()
    {
        SystemData = new OpticsSystemSettings { Title = "NSC", Wavelengths = new() { new() { Micrometers = 0.555 } } },
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Source Point", Parameters = new() { 10, 1000, 1, 0, 0, 40 }, Comment = "source" },
                new() { Type = "Detector Rectangle", Z = 10, Material = "ABSORB", Parameters = new() { 5, 5, 10, 10 } },
            },
        },
    };

    /// <summary>A Source Point (1 W, 1000 rays, a 30° semi-cone), a transparent Detector Color
    /// (±10 mm, 4 × 4 pixels) 10 mm away, and an absorbing Detector Polar (90°, radial size 20 mm,
    /// 10 × 12 pixels) about the source, at 0.555 µm. The cone (5.8 mm at the colour detector)
    /// lies inside both, so every ray crosses the colour detector once and ends on the polar one.</summary>
    private static OpticsSettings PointOnColorAndPolar() => new()
    {
        SystemData = new OpticsSystemSettings { Title = "NSC", Wavelengths = new() { new() { Micrometers = 0.555 } } },
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Source Point", Parameters = new() { 10, 1000, 1, 0, 0, 30 } },
                new() { Type = "Detector Color", Z = 10, Parameters = new() { 10, 10, 4, 4 } },
                new() { Type = "Detector Polar", Material = "ABSORB", Parameters = new() { 90, 20, 10, 12 } },
            },
        },
    };

    /// <summary>The same trace as the macro's NSTR (all sources, no splitting, scattering or
    /// polarization, the given seed), run directly.</summary>
    private static NscSourceRunResult Direct(OpticsSettings system, long seed) =>
        ProjectNscSources.Trace(new OpticsAnalysisContext("Macro", system) { Catalogs = NoCatalogs }, seed: seed);

    private const string RectangleDetectorMacro =
        "OPTRETURN 13, NSDD(1, 2, 0, 0)\n"
        + "NSTR 1, 0, 0, 0, 0, 0, 7\n"
        + "OPTRETURN 0, NSDD(1, 2, 0, 0)\nOPTRETURN 1, NSDD(1, 2, 0, 1)\nOPTRETURN 2, NSDD(1, 2, -3, 0)\nOPTRETURN 3, NSDD(1, 2, 45, 0)\n"
        + "OPTRETURN 4, NSDD(1, 2, -6, 0)\nOPTRETURN 5, NSDD(1, 2, -9, 0)\nOPTRETURN 6, NSDD(1, 2, -1, 2)\nOPTRETURN 7, LOST(2)\n"
        + "OPTRETURN 8, NSDC(1, 2, 0, 2)\nSAVEDETECTOR 1, 2, \"rect.DDR\"\n"
        + "NSTR2 1, 1, 0, 0, 0, 0, 7, 0, \"\", \"\", 0, 0, \"\"\nOPTRETURN 9, NSDD(1, 2, 0, 0)\n"
        + "x = NSDD(1, 0, 0, 0)\nOPTRETURN 10, NSDD(1, 2, 0, 0)\n"
        + "LOADDETECTOR 1, 2, \"rect.DDR\"\nOPTRETURN 11, NSDD(1, 2, 0, 0)\nOPTRETURN 12, NSDD(1, 2, -5, 0)";

    [Fact]
    public void Nstr_FillsTheDetectorRectangle_AndNsddReadsItAsTheHelpDefines()
    {
        var r = Run(RectangleDetectorMacro, PointOnRectangle()).OptReturn;
        var run = Direct(PointOnRectangle(), 7);
        var d = Assert.IsType<NscFlatDetectorData>(Assert.Single(run.Detectors!));
        double total = d.Flux.Sum();

        Assert.Equal(0, r[13]);                                  // never traced: the detector reads zero
        Near(total, r[0]);                                      // Pix# 0, Data 0: total flux
        // Every ray ends detected or in another ledger line (the detector absorbs): the total is
        // the emitted 1 W less the rest, to rounding.
        var l = run.Ledger;
        Near(l.Emitted - (l.Absorbed + l.Escaped + l.Thresholded + l.NotTraced + l.LimitTerminated + l.Errors), r[0]);
        Assert.True(l.Escaped > 0);
        Near(total / 1e-4, r[1]);                               // Data 1: flux per area, (10 mm)² = 1e-4 m² (derived)
        Assert.Equal(d.SpatialHits, r[2]);                      // Pix# −3: rays striking
        Near(d.Flux[44], r[3]);                                 // pixel 45: the help's numbering, x fastest from (−x, −y)
        // Centroid and RMS radius over the pixel centres (−5 + (i + ½)·1 mm), weights the flux:
        // x* = Σwx/Σw, σ² = Σw(x − x*)²/Σw (help, NSDD1.png and NSDD2.png); derived here.
        double sw = 0, sx = 0, sy = 0;
        for (int iy = 0; iy < 10; iy++)
            for (int ix = 0; ix < 10; ix++) { double w = d.Flux[iy * 10 + ix]; sw += w; sx += w * (-4.5 + ix); sy += w * (-4.5 + iy); }
        double cx = sx / sw, cy = sy / sw, v = 0;
        for (int iy = 0; iy < 10; iy++)
            for (int ix = 0; ix < 10; ix++) { double w = d.Flux[iy * 10 + ix]; v += w * ((-4.5 + ix - cx) * (-4.5 + ix - cx) + (-4.5 + iy - cy) * (-4.5 + iy - cy)); }
        Near(cx, r[4], 1e-12);
        Near(Math.Sqrt(v / sw), r[5], 1e-12);
        double peak = 0;
        for (int iy = 0; iy < 10; iy++)
            for (int ix = 0; ix < 10; ix++) peak = Math.Max(peak, d.RadiantIntensity(ix, iy));
        Near(peak, r[6]);                                       // Pix# −1, Data 2: the largest flux/solid angle
        Near(l.Thresholded, r[7]);                              // LOST(2): energy below the thresholds
        Near(d.CoherentAmplitude!.Sum(), r[8]);                 // NSDC Data 2: the summed amplitude
        Near(2 * total, r[9]);                                  // NSTR2 with the same seed adds the same data again
        Assert.Equal(0, r[10]);                                 // NSDD with object 0 cleared every detector
        Near(total, r[11]);                                     // LOADDETECTOR put the saved data back
        var nonZero = d.Flux.Where(f => f != 0).ToArray();
        Near(nonZero.Average(), r[12]);                         // Pix# −5: the mean of the non-zero pixels

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, PointOnRectangle())).Message;
        Assert.Contains("OP-80", Fails("NSTR 1, 0, 0, 1, 0, 0, 7"));
        Assert.Contains("OP-83", Fails("NSTR 1, 0, 0, 0, 0, 0, 7, 1, \"a.ZRD\", \"\", 0"));
        Assert.Contains("OP-86", Fails("NSTR 2, 0, 0, 0, 0, 0, 7"));
        Assert.Contains("Bitmap", Fails("x = NSDD(1, 2, 0, 3)"));
        Assert.Contains("NSDE reads Detector Color", Fails("x = NSDE(1, 2, 0, 0, 1, 0)"));
        Assert.Contains("not a detector object", Fails("x = NSDD(1, 1, 0, 0)"));
        Assert.Contains("no NSTR trace", Fails("x = LOST(1)"));
        Assert.Contains("pixel 101 does not exist", Fails("x = NSDD(1, 2, 101, 0)"));
    }

    private const string ColorPolarDetectorMacro =
        "NSTR 1, 0, 0, 0, 0, 0, 3\n"
        + "OPTRETURN 0, NSDE(1, 2, 0, 0, 1, 0)\nOPTRETURN 1, NSDE(1, 2, 0, 0, 3, 0)\nOPTRETURN 2, NSDE(1, 2, 0, 0, 5, 0)\nOPTRETURN 3, NSDE(1, 2, -3, 0, 1, 0)\n"
        + "OPTRETURN 4, NSDP(1, 3, 0, 1)\nOPTRETURN 5, NSDP(1, 3, -3, 1)\nOPTRETURN 6, NSDP(1, 3, -4, 1)\nOPTRETURN 7, NSDP(1, 3, -4, 2)\n"
        + "OPTRETURN 8, NSDE(1, 2, 0, 1, 1, 0)\nSAVEDETECTOR 1, 3, \"polar.DDP\"\nx = NSDP(1, -3, 0, 0)\nOPTRETURN 9, NSDP(1, 3, 0, 1)\n"
        + "LOADDETECTOR 1, 3, \"polar.DDP\"\nOPTRETURN 10, NSDP(1, 3, 0, 1)";

    [Fact]
    public void Nstr_FillsTheColourAndPolarDetectors_AndNsdeAndNsdpReadThem()
    {
        var r = Run(ColorPolarDetectorMacro, PointOnColorAndPolar()).OptReturn;
        var run = Direct(PointOnColorAndPolar(), 3);
        var polar = Assert.IsType<NscPolarDetectorData>(run.Detectors!.Single(x => x.ObjectNumber == 3));

        // Every ray of the 1 W source crosses the colour detector once and ends on the polar one.
        Near(1, r[0]);                                          // NSDE Data 1: power, position space
        // Data 3: lumens = 683·V(λ) per watt at 0.555 µm (K_cd, SI Brochure 9th ed. §2.3.1; V from
        // the CIE table, OP-79), the same factor for every ray.
        Near(Photometric.LumensPerWatt(0.555), r[1], 1e-12);
        // Data 5: x = X/(X + Y + Z); one wavelength gives every lit pixel the same x (derived).
        var (X, Y, Z) = Photometric.TristimulusPerWatt(0.555);
        Near(X / (X + Y + Z), r[2], 1e-12);
        Assert.Equal(1000, r[3]);                               // Pix# −3: every ray struck it once
        Near(1, r[4]);                                          // NSDP Data 1: the polar detector's power
        Assert.Equal(1000, r[5]);
        // Pix# −4, Data 1: √(Σw θ²/Σw) with θ = j·inc, inc = 90°/(10 − 1) = 10° (Conventions §73).
        double sw = 0, s2 = 0;
        for (int a = 0; a < 12; a++)
            for (int j = 0; j < 10; j++) { double w = polar.Flux[a * 10 + j]; sw += w; s2 += w * (10.0 * j) * (10.0 * j); }
        Near(Math.Sqrt(s2 / sw), r[6], 1e-12);
        Assert.Equal(0, r[7]);                                  // "All other inputs will return a value of zero."
        Near(1, r[8]);                                          // angle space: the ±90° range holds every ray
        Assert.Equal(0, r[9]);                                  // NSDP with object −3 cleared detector 3
        Near(r[4]!.Value, r[10]);                               // LOADDETECTOR read back what SAVEDETECTOR wrote

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run("NSTR 1, 0, 0, 0, 0, 0, 3\n" + macro, PointOnColorAndPolar())).Message;
        Assert.Contains("without saying whether", Fails("x = NSDP(1, 3, 0, 2)"));
        Assert.Contains("colour rendering index", Fails("x = NSDE(1, 2, 0, 0, 12, 0)"));
        Assert.Contains("NSDP reads Detector Polar", Fails("x = NSDP(1, 2, 0, 1)"));
        Assert.Contains("coherent data is recorded by Detector Rectangle", Fails("x = NSDC(1, 2, 0, 0)"));
    }

    private const string SourceTabMacro =
        "OPTRETURN 0, NPRO(1, 1, 110, 0)\nSETNSCPROPERTY 1, 1, 110, 0, 0\nOPTRETURN 1, NPRO(1, 1, 110, 0)\n"
        + "SETNSCPROPERTY 1, 1, 112, 0, 1\nSETNSCPROPERTY 1, 1, 161, 0, 3\nSETNSCPROPERTY 1, 1, 165, 0, 2.5\n"
        + "OPTRETURN 2, NPRO(1, 1, 161, 0)\nOPTRETURN 3, NPRO(1, 1, 103, 0)\nSETNSCPROPERTY 1, 1, 101, 0, 0\nOPTRETURN 4, NPRO(1, 1, 101, 0)\n"
        + "SETNSCPROPERTY 1, 2, 20, 0, 1\nOPTRETURN 5, NPRO(1, 2, 20, 0)\nx = NPRO(1, 2, 0, 0)\nPRINT $BUFFER()\nx = NPRO(1, 1, 0, 0)\nPRINT $BUFFER()\n"
        + "OPTRETURN 6, NPAR(1, 1, 6)";

    [Fact]
    public void NscProperties_ReadAndSetTheSourcesTab_AndPixelInterpolation()
    {
        // SETNSCPROPERTY codes (help): 110 sampling (0 random, 1 Sobol; Sobol is the saved
        // default), 112 array mode (1 rectangular), 161 array integer 1, 165 array size 1, 103
        // Jones x (default 1), 101 random polarization (default on), 20 "Use Pixel Interpolation";
        // code 0 gives the help's type codes NSC_DETE and NSC_SRCP; NPAR 6 is the cone angle.
        var result = Run(SourceTabMacro, PointOnRectangle());
        Assert.Equal(new double?[] { 1, 0, 3, 1, 0, 1, 40 }, result.OptReturn.Take(7));
        Assert.Equal(Lines("NSC_DETE", "NSC_SRCP"), result.Output);
        var objects = result.System.NonSequential!.Objects!;
        var source = objects[0].Source!;
        Assert.Equal("Random", source.Sampling);
        Assert.Equal("Rectangular", source.ArrayType);
        Assert.Equal(3, source.ArrayCount1);
        Assert.Equal(2.5, source.ArraySize1);
        Assert.False(source.RandomPolarization);
        Assert.True(objects[1].DetectorPixelInterpolation);

        string Fails(string macro) => Assert.Throws<ZplException>(() => Run(macro, PointOnRectangle())).Message;
        Assert.Contains("dialog's list", Fails("x = NPRO(1, 1, 113, 0)"));
        Assert.Contains("not a source", Fails("SETNSCPROPERTY 1, 2, 101, 0, 1"));
        Assert.Contains("Detector Rectangle and Detector Color property", Fails("SETNSCPROPERTY 1, 1, 20, 0, 1"));
        Assert.Contains("0 (Random), 1 (Sobol)", Fails("SETNSCPROPERTY 1, 1, 110, 0, 2"));
    }
}
