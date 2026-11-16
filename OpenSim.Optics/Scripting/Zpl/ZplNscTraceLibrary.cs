using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.Scripting.Zpl;

// wire-15: the non-sequential trace and the detectors. NSTR and NSTR2 trace the system's own
// sources through OP-77's runner (ProjectNscSources.Trace); the detector data they leave is kept
// in the session, read by NSDD, NSDE, NSDP and NSDC (NscDetectorQueries), cleared by NSDD, NSDE
// and NSDP with object 0 or −n, saved and loaded by SAVEDETECTOR and LOADDETECTOR (OP-79's
// NscDetectorFile); LOST reads the last trace's ledger. Each follows its OpticStudio 2025 R2 help
// page under plans/sources/optics/op-99-help/ (NSTR, NSTR2, SAVEDETECTOR (keywords),
// LOADDETECTOR (keywords), Numeric Functions) and, for the pixel and data codes the functions
// share with the operands, op-55b-help/ "Non-sequential Ray Tracing and Detector Operands"
// (docs/Optics-Conventions.md §56 and §73).

public static partial class ZplLibrary
{
    private static OpticsAnalysisContext NscContext(ZplInterpreter m) =>
        new("Macro", m.Session.Current) { Catalogs = m.Session.Catalogs };

    private static ZplRuntimeException NscFailed(Exception e, string who) =>
        new($"{who}: {e.Message.Split('\n')[0].TrimEnd('\r')}");

    /// <summary>The data on detector object <paramref name="number"/>: what traces and LOADDETECTOR
    /// left, or the detector's empty data (a detector never traced, or cleared, reads zero).</summary>
    private static NscDetectorData NscDetector(ZplInterpreter m, double surface, int number, string who)
    {
        NscSurface(surface, who);
        if (m.Session.NscDetectors.TryGetValue(number, out var held)) return held;
        var context = NscContext(m);
        NscScene? scene;
        try { scene = ProjectNonSequential.Scene(context, m.Session.Unit); }
        catch (ArgumentException e) { throw NscFailed(e, who); }
        int count = scene?.Objects.Count ?? 0;
        if (number < 1 || number > count)
            throw new ZplRuntimeException($"{who}: object {number} does not exist; the system has {count} non-sequential object{(count == 1 ? "" : "s")}.");
        double primary = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Wavelengths.Primary.Micrometers;
        return NscDetectorQueries.Empty(scene!, number, primary)
            ?? throw new ZplRuntimeException($"{who}: object {number} ({scene!.Objects[number - 1].Geometry.TypeName}) is not a detector object (Detector Rectangle, Color or Polar).");
    }

    /// <summary>NSDD, NSDE and NSDP's clearing: object 0 clears every detector, −n detector n; 0 is returned.</summary>
    private static bool NscClear(ZplInterpreter m, double surface, int number, string who)
    {
        NscSurface(surface, who);
        if (number > 0) return false;
        if (number == 0) m.Session.NscDetectors.Clear();
        else m.Session.NscDetectors.Remove(-number);
        return true;
    }

    private static double NscQuery(Func<double> query, string who)
    {
        try { return query(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { throw NscFailed(e, who); }
    }

    private static void NscTrace(ZplInterpreter m, ZplArgs a, string who, bool paths)
    {
        int max = paths ? 13 : 11;
        if (a.Count < 7 || a.Count > max)
            throw new ZplRuntimeException($"{who} takes surf, source, split, scatter, usepolar, ignore_errors, random_seed [, save, savefilename, filter, zrd_format{(paths ? ", savepaths, pathfilename" : "")}]; {a.Count} arguments given.");
        NscSurface(a.Num(m, 0, "the surface"), who);
        int source = a.Int(m, 1, $"{who}: the source");
        if (source < 0) throw new ZplRuntimeException($"{who}: the source is {source}; it must be 0 (every source) or a source object's number.");
        bool split = a.Num(m, 2, "split") != 0;
        if (a.Num(m, 3, "scatter") != 0)
            throw new ZplRuntimeException($"{who}: scatter is on; surface and volume scattering are not built (plan items OP-80 and OP-81). Use 0.");
        bool polar = a.Num(m, 4, "usepolar") != 0 || split;   // "If splitting is on polarization is automatically selected."
        bool ignoreErrors = a.Num(m, 5, "ignore_errors") != 0;
        int seed = a.Int(m, 6, $"{who}: the random seed");
        if (a.Has(7) && a.Num(m, 7, "save") != 0)
            throw new ZplRuntimeException($"{who}: save is on; saving rays to ZRD, DAT or SDF files is not built (the ray database is plan item OP-83).");
        if (paths && a.Has(11) && a.Num(m, 11, "savepaths") != 0)
            throw new ZplRuntimeException($"{who}: savepaths is on; PAF path files are not built (plan item OP-83).");
        // "If random_seed is zero, then the random number generator will be seeded with a random
        // value": the macro's own generator (RANDOMIZE seeds it) draws one.
        long traceSeed = seed != 0 ? seed : m.Session.Random.NextInt64(1, long.MaxValue);

        NscSourceRunResult run;
        try
        {
            run = ProjectNscSources.Trace(NscContext(m), source == 0 ? null : new[] { source }, split, polar, ignoreErrors, traceSeed, m.Session.CancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { throw NscFailed(e, who); }
        m.Session.NscLedger = run.Ledger;
        foreach (var d in run.Detectors ?? Array.Empty<NscDetectorData>())
        {
            if (m.Session.NscDetectors.TryGetValue(d.ObjectNumber, out var held))
            {
                try { m.Session.NscDetectors[d.ObjectNumber] = held.Combine(d); }
                catch (ArgumentException e)
                {
                    throw new ZplRuntimeException($"{who}: detector object {d.ObjectNumber}'s new data cannot be added to the data it holds ({e.Message}); clear it first (NSDD(1, -{d.ObjectNumber}, 0, 0)).");
                }
            }
            else m.Session.NscDetectors[d.ObjectNumber] = d;
        }
    }

    private static string NscFolder(ZplInterpreter m) =>
        (m.Session.LensPath is { } lens ? Path.GetDirectoryName(lens) : null) ?? m.Session.LensFolder ?? m.Session.MacroFolder ?? "";

    /// <summary>The file's data on detector object <paramref name="number"/>: it must be of the
    /// object's type, pixel counts and size (sizes and angles to 1e-9 relative, as the file holds
    /// them in its lens unit); the object's own sizes are kept so later traces add to it.</summary>
    private static NscDetectorData Fitted(NscDetectorData detector, NscDetectorData file, int number, string name)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b)) || a == b;
        string Refuse(string what) => throw new ZplRuntimeException($"LOADDETECTOR: '{name}' holds a {file.TypeName} {what}; object {number} is a {detector.TypeName} that does not match.");
        switch (detector, file)
        {
            case (NscFlatDetectorData d, NscFlatDetectorData f) when d.IsColor == f.IsColor:
                if (d.PixelsX != f.PixelsX || d.PixelsY != f.PixelsY || !Same(d.HalfWidthX, f.HalfWidthX) || !Same(d.HalfWidthY, f.HalfWidthY)
                    || !Same(d.AngleXMin, f.AngleXMin) || !Same(d.AngleXMax, f.AngleXMax) || !Same(d.AngleYMin, f.AngleYMin) || !Same(d.AngleYMax, f.AngleYMax))
                    Refuse($"of {f.PixelsX} × {f.PixelsY} pixels and another size or angle range");
                return f with
                {
                    ObjectNumber = number, HalfWidthX = d.HalfWidthX, HalfWidthY = d.HalfWidthY,
                    AngleXMin = d.AngleXMin, AngleXMax = d.AngleXMax, AngleYMin = d.AngleYMin, AngleYMax = d.AngleYMax,
                };
            case (NscPolarDetectorData d, NscPolarDetectorData f):
                if (d.PolarPixels != f.PolarPixels || d.AzimuthPixels != f.AzimuthPixels || !Same(d.MaxAngle, f.MaxAngle))
                    Refuse($"of {f.PolarPixels} × {f.AzimuthPixels} pixels to {f.MaxAngle}°");
                return f with { ObjectNumber = number, MaxAngle = d.MaxAngle, RadialSize = d.RadialSize };
            default:
                Refuse("");
                return file;
        }
    }

    private static void RegisterNscTrace()
    {
        Kw("NSTR", "Traces the system's sources (source 0: all; surface 1) through OP-77's runner with the analysis ray counts, splitting, polarization (on whenever splitting is), Ignore Errors and the seed (0: one drawn from the macro's RAND generator); the detector data is added to what each detector holds (clear it with NSDD(1, 0, 0, 0)). Refused by name: scatter (OP-80, OP-81) and saving rays (ZRD, DAT, SDF: OP-83).", (m, a) => NscTrace(m, a, "NSTR", paths: false));
        Kw("NSTR2", "As NSTR, with NSTR2's arguments; savepaths (PAF files) is refused as saving rays is (OP-83).", (m, a) => NscTrace(m, a, "NSTR2", paths: true));
        Kw("SAVEDETECTOR", "Writes the data on a detector object (surface 1) to a DDR, DDC or DDP file (OP-79's format, Conventions §73); a name without a folder is in the lens file's folder.", (m, a) =>
        {
            a.Exactly(3, "SAVEDETECTOR");
            int number = a.Int(m, 1, "SAVEDETECTOR: the object");
            var data = NscDetector(m, a.Num(m, 0, "the surface"), number, "SAVEDETECTOR");
            string path = ZplSession.Resolve(a.FileName(m, 2, "the detector file"), NscFolder(m), "lens folder");
            try { NscDetectorFile.Write(path, data, m.Session.Unit); }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { throw NscFailed(e, "SAVEDETECTOR"); }
        });
        Kw("LOADDETECTOR", "Reads a DDR, DDC or DDP file onto a detector object (surface 1) of the same type and size, replacing its data; a name without a folder is in the lens file's folder. Detector Volume (DDV) is not built.", (m, a) =>
        {
            a.Exactly(3, "LOADDETECTOR");
            int number = a.Int(m, 1, "LOADDETECTOR: the object");
            m.Session.NscDetectors.Remove(number);
            var empty = NscDetector(m, a.Num(m, 0, "the surface"), number, "LOADDETECTOR");
            string path = ZplSession.Resolve(a.FileName(m, 2, "the detector file"), NscFolder(m), "lens folder");
            NscDetectorData read;
            try { read = NscDetectorFile.Read(path); }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or NotSupportedException or FormatException)
            {
                throw NscFailed(e, "LOADDETECTOR");
            }
            m.Session.NscDetectors[number] = Fitted(empty, read, number, Path.GetFileName(path));
        });

        Fn("LOST", 1, 1, "The energy the most recent NSTR trace lost: code 1 to errors, 2 to the intensity thresholds (its ledger, source units).", (m, v) =>
        {
            int code = Int(v, 0, "the code");
            var ledger = m.Session.NscLedger ?? throw new ZplRuntimeException("no NSTR trace has run in this macro.");
            return code switch
            {
                1 => N(ledger.Errors),
                2 => N(ledger.Thresholded),
                _ => throw new ZplRuntimeException($"the code is {code}; it must be 1 (errors) or 2 (thresholds)."),
            };
        });
        Fn("NSDD", 4, 4, "Detector Rectangle data (surface 1) by the NSDD operand's pixel and data codes with # Ignored 0: pixel > 0 that pixel, 0 total, −1/−2 max/min, −3 rays, −4/−5 SD/mean of non-zero pixels, −6 to −12 centroid and RMS (Data 0 flux, 1 flux/m², 2 flux/sr); object 0 clears every detector, −n detector n, and returns 0. Refused by name: Data 3 (bitmap), 4 and 5 (objects as detectors), −13, −14 and −15.", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDD")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDD");
            return N(NscQuery(() => NscDetectorQueries.Nsdd(data, Int(v, 2, "the pixel"), Int(v, 3, "the data"), 0, m.Session.Unit), "NSDD"));
        });
        Fn("NSDE", 6, 6, "Detector Color data (surface 1) by the NSDE operand's codes with # Ignored 0: pixel > 0, 0 (sum; chromaticity averaged over lit pixels), −1/−2, −3; angle 0 position, 1 angle space; Data 1 W, 2 W/m² or W/sr, 3 lm, 4 lm/m² or lm/sr, 5–8 CIE x, y, u′, v′, 9–11 X, Y, Z per area; object 0 or −n clears and returns 0. Data 12–21 (CRI, CCT, bitmap, spectral bins) are refused by name.", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDE")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDE");
            return N(NscQuery(() => NscDetectorQueries.Nsde(data, Int(v, 2, "the pixel"), Int(v, 3, "the angle flag"), Int(v, 4, "the data"), 0), "NSDE"));
        });
        Fn("NSDP", 4, 4, "Detector Polar data (surface 1) by the NSDP operand's codes: pixel > 0, 0 (sum), −1/−2, −3 rays, −4 RMS angle; Data 1 W, 2 W/sr, 3 lm, 4 lm/sr, 5–8 chromaticity, 9–11 X, Y, Z per sr; object 0 or −n clears and returns 0. Pixel 0 with per-steradian data is refused (the help does not say how it sums).", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDP")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDP");
            return N(NscQuery(() => NscDetectorQueries.Nsdp(data, Int(v, 2, "the pixel"), Int(v, 3, "the data")), "NSDP"));
        });
        Fn("NSDC", 4, 4, "Detector Rectangle coherent data (surface 1): pixel > 0 that pixel, 0 the sum; Data 0 real, 1 imaginary, 2 amplitude, 3 power (coherent irradiance × pixel area).", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDC");
            return N(NscQuery(() => NscDetectorQueries.Nsdc(data, Int(v, 2, "the pixel"), Int(v, 3, "the data")), "NSDC"));
        });
    }
}
