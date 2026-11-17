// OP-88
using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Editing;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// The non-sequential merit operands (plan item OP-88; docs/Optics-Conventions.md §76), read from
/// OpticStudio's 2025 R2 help pages "Non-sequential Ray Tracing and Detector Operands" and
/// "Constraints on Non-sequential Object Data" (plans/sources/optics/op-55b-help/) and "NSC
/// Operands" (plans/sources/optics/op-88-help/):
/// <list type="bullet">
/// <item><b>Traces and detectors.</b> NSTR traces the project's sources through OP-77's runner
/// (<see cref="ProjectNscSources.Trace"/>) into the evaluation's detectors; NSDD, NSDE and NSDP
/// clear them (Det# 0 every detector, −n detector n) or read them, NSDC reads coherent data,
/// through OP-79's queries (<see cref="NscDetectorQueries"/>). The data lives for one evaluation
/// (<see cref="NscMeritSession"/>), per configuration; rows run in row order, so the NSDx rows
/// after an NSTR read what it traced. NSRA traces one ray of a single-ray source.</item>
/// <item><b>Object data.</b> NPXV..NTZL give an object's position and tilts, NPGT/NPLT/NPVA a
/// parameter, NSRM a rotation-matrix element, read from the saved objects.</item>
/// </list>
/// <para>
/// Columns (a reading, Conventions §10 item 72): the help names each operand's arguments but not
/// which of Int1, Int2 and Data1–Data6 holds them; they follow the pattern OP-54 read from
/// "Defining complex operands" — integers in Int1 then Int2, the rest in Data1 on, in the order
/// the help names them: Int1 Surf and Int2 the object (Det#, Src#, Object) for every operand;
/// NSDD Data1 Pix#, Data2 Data, Data3 # Ignored; NSDE Data1 Pix#, Data2 Angle?, Data3 Data, Data4
/// # Ignored; NSDP and NSDC Data1 Pix#, Data2 Data; NSTR Data1 Splt?, Data2 Scat?, Data3 Pol?,
/// Data4 IgEr?; NSRA Data1 Splt?, Data2 Pol?, Data3 Seg#, Data4 Data, Data5 Source #; the
/// position and tilt operands and NSRM Data1 Ref? (NSRM Data2 Data); NPGT/NPLT/NPVA Data1 Param.
/// </para>
/// </summary>
public static class NscOperands
{
    public const string ObjectCategory = "Constraints on non-sequential object data";
    public const string TraceCategory = "Non-sequential ray tracing and detector operands";
    private const string ObjectPage = "Constraints on Non-sequential Object Data";
    private const string TracePage = "Non-sequential Ray Tracing and Detector Operands; NSC Operands";

    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        // ---- traces and detectors ----
        yield return Trace("NSTR", "Non-sequential trace",
            "Traces the analysis rays of source Src# (0: every source) into the detectors, adding to what they hold; splitting (Splt?), polarization (Pol?, on whenever splitting is) and Ignore Errors (IgEr?) as set. Always 0. A row with Det# 0 (NSDD, NSDE or NSDP) must clear the detectors before it. Each evaluation traces with the same random numbers.",
            "Src#", new[] { "Splt?", "Scat?", "Pol?", "IgEr?" }, Nstr);
        yield return Trace("NSDD", "Non-sequential incoherent detector data",
            "Det# 0 clears every detector, −n detector n (value 0). Otherwise Detector Rectangle data: Pix# > 0 that pixel; 0 the total (Data 0 flux, 1 average flux/area, 2 angle-space flux); −1/−2 maximum/minimum; −3 rays; −4/−5 standard deviation/mean of the non-zero pixels; −6 to −8 centroid; −9 to −12 RMS about it; Data 3 the pixel's flux over the peak of the consecutive NSDD Data 3 rows of the detector. Flux/area per m².",
            "Det#", new[] { "Pix#", "Data", "# Ignored" }, Nsdd);
        yield return Trace("NSDE", "Non-sequential color detector data",
            "Det# 0 or −n clears as NSDD. Otherwise Detector Color data: Data 1 power, 2 power/area, 3 lumens, 4 lumens/area, 5–8 chromaticity, 9–11 tristimulus X, Y, Z per area, 14–16 X, Y, Z over the peak Y of the consecutive NSDE rows of the detector; Pix# and Angle? as NSDD.",
            "Det#", new[] { "Pix#", "Angle?", "Data", "# Ignored" }, Nsde);
        yield return Trace("NSDP", "Non-sequential polar detector data",
            "Det# 0 or −n clears as NSDD. Otherwise Detector Polar data: Data 1 power, 2 power/sr, 3 lumens, 4 lumens/sr, 5–8 chromaticity, 9–11 X, Y, Z per sr; Pix# > 0 a pixel, 0 the sum, −1/−2 maximum/minimum, −3 rays, −4 the radial RMS angle.",
            "Det#", new[] { "Pix#", "Data" }, Nsdp);
        yield return Trace("NSDC", "Non-sequential coherent detector data",
            "Detector Rectangle coherent data: Data 0 real, 1 imaginary, 2 amplitude, 3 power; Pix# > 0 a pixel, 0 the sum.",
            "Det#", new[] { "Pix#", "Data" }, Nsdc);
        yield return Trace("NSRA", "Non-sequential single ray trace",
            "Traces the one analysis ray of source Src# (element Source # of an array) without scattering, errors considered, and returns data of segment Seg# (−1 the last): 1–3 position, 4–6 direction cosines, 7–9 the normal of the object hit, 10 path from the parent, 11 intensity, 14 index, 16 path and 17 optical path from the source; 31–39 as 1–9 (one frame in a pure non-sequential system). Lengths in lens units.",
            "Src#", new[] { "Splt?", "Pol?", "Seg#", "Data", "Source #" }, Nsra);

        // ---- object data ----
        foreach (char axis in "XYZ")
        {
            int a = axis - 'X';
            string lower = char.ToLowerInvariant(axis).ToString();
            yield return Object($"NP{axis}V", $"Non-sequential object position {lower}", $"The {lower} position of Object in lens units: Ref? 0 relative to its reference object (the cell), 1 to the non-sequential origin, 2 to the global reference (the same frame here).", null, c => Position(c, a));
            yield return Object($"NP{axis}G", $"Non-sequential object position {lower} greater than", $"The {lower} position of Object, bounded below by the target (Ref? as NP{axis}V).", true, c => Position(c, a));
            yield return Object($"NP{axis}L", $"Non-sequential object position {lower} less than", $"The {lower} position of Object, bounded above by the target (Ref? as NP{axis}V).", false, c => Position(c, a));
            yield return Object($"NT{axis}V", $"Non-sequential object tilt about {lower}", $"The tilt about {lower} of Object in degrees: Ref? 0 the cell; 1 and 2 the angles of its rotation relative to the non-sequential origin, in its own rotation order.", null, c => Tilt(c, a));
            yield return Object($"NT{axis}G", $"Non-sequential object tilt about {lower} greater than", $"The tilt about {lower} of Object, bounded below by the target (Ref? as NT{axis}V).", true, c => Tilt(c, a));
            yield return Object($"NT{axis}L", $"Non-sequential object tilt about {lower} less than", $"The tilt about {lower} of Object, bounded above by the target (Ref? as NT{axis}V).", false, c => Tilt(c, a));
        }
        yield return Parameter("NPVA", "Non-sequential parameter value", "Parameter Param of Object, in its column's unit.", null);
        yield return Parameter("NPGT", "Non-sequential parameter greater than", "Parameter Param of Object, bounded below by the target.", true);
        yield return Parameter("NPLT", "Non-sequential parameter less than", "Parameter Param of Object, bounded above by the target.", false);
        yield return new MeritOperandDefinition
        {
            Code = "NSRM", Name = "Non-sequential rotation matrix", Category = ObjectCategory, Source = ObjectPage, NonSequential = true,
            Description = "Element Data (1 R[1][1], 2 R[1][2], ... 9 R[3][3]) of Object's rotation matrix, local to the frame of Ref?: 0 its reference object (always the identity, as the help says), 1 the non-sequential origin, 2 the global reference (the same frame here).",
            Int1 = "Surf", Int2 = "Object", Data = new[] { "Ref?", "Data" },
            Compute = Guard(Nsrm),
        };
    }

    // ================================================================ builders

    private static MeritOperandDefinition Trace(string code, string name, string description, string int2, string[] data,
        Func<MeritRowContext, double> compute) => new()
    {
        Code = code, Name = name, Category = TraceCategory, Source = TracePage, Description = description, NonSequential = true,
        Int1 = "Surf", Int2 = int2, Data = data, Compute = Guard(compute),
    };

    private static MeritOperandDefinition Object(string code, string name, string description, bool? atLeast, Func<MeritRowContext, double> actual) => new()
    {
        Code = code, Name = name, Category = ObjectCategory, Source = ObjectPage + (atLeast is null ? "" : "; Understanding Boundary Operands"),
        Description = description, NonSequential = true, IsBound = atLeast is not null,
        Int1 = "Surf", Int2 = "Object", Data = new[] { "Ref?" },
        Compute = Guard(atLeast switch
        {
            null => actual,
            true => c => MeritOperandRegistry.AtLeast(actual(c), c.Row.Target),
            false => c => MeritOperandRegistry.AtMost(actual(c), c.Row.Target),
        }),
    };

    private static MeritOperandDefinition Parameter(string code, string name, string description, bool? atLeast) => new()
    {
        Code = code, Name = name, Category = ObjectCategory, Source = ObjectPage + (atLeast is null ? "" : "; Understanding Boundary Operands"),
        Description = description, NonSequential = true, IsBound = atLeast is not null,
        Int1 = "Surf", Int2 = "Object", Data = new[] { "Param" },
        Compute = Guard(atLeast switch
        {
            null => ParameterValue,
            true => c => MeritOperandRegistry.AtLeast(ParameterValue(c), c.Row.Target),
            false => c => MeritOperandRegistry.AtMost(ParameterValue(c), c.Row.Target),
        }),
    };

    /// <summary>Turns the engines' refusals into the row's failure.</summary>
    private static Func<MeritRowContext, double> Guard(Func<MeritRowContext, double> compute) => c =>
    {
        try { return compute(c); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new MeritOperandException(NscMeritSession.OneLine(e.Message));
        }
    };

    // ================================================================ columns

    /// <summary>Surf (Int1): this build has pure non-sequential systems, whose group is surface 1.</summary>
    private static void Surf(MeritRowContext c)
    {
        if (c.Row.Int1 != 1)
            throw new MeritOperandException($"Surf is {c.Row.Int1}; it must be 1, the non-sequential group of a pure non-sequential system (non-sequential surfaces in a sequential lens, mixed mode, are plan item OP-86).");
    }

    /// <summary>Data<paramref name="k"/> as a whole number.</summary>
    private static int Whole(MeritRowContext c, int k, string label)
    {
        double v = c.Row.DataAt(k);
        if (v != Math.Floor(v) || !double.IsFinite(v) || Math.Abs(v) > int.MaxValue)
            throw new MeritOperandException($"{label} (Data{k}) is {v.ToString("g9", CultureInfo.InvariantCulture)}; it must be a whole number.");
        return (int)v;
    }

    private static bool Flag(MeritRowContext c, int k) => c.Row.DataAt(k) != 0;

    // ================================================================ NSTR

    private static double Nstr(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        if (!s.ClearedAll)
            throw new MeritOperandException("no row before it clears every detector: \"An NSDD, NSDE, or NSDP operand with Det# set equal to 0 must be included in the merit function prior to this operand\" (help, NSTR). Add one above this row.");
        if (Flag(c, 2))
            throw new MeritOperandException("Scat? is on; scattering is not built here (surface scattering is plan item OP-80, bulk scattering OP-81). Use 0.");
        int source = c.Row.Int2;
        if (source < 0) throw new MeritOperandException($"Src# is {source}; it must be 0 (every source) or a source object's number.");
        if (source > 0)
        {
            s.CheckObject(source, "Src#");
            if (s.Scene.Objects[source - 1].Geometry is not NscSource)
                throw new MeritOperandException($"Src# {source} is a {s.Scene.Objects[source - 1].Geometry.TypeName}, not a source object.");
        }
        bool split = Flag(c, 1);
        bool polar = Flag(c, 3) || split;   // help: "If splitting is on polarization is automatically selected."
        bool ignore = Flag(c, 4);
        long seed = NscMeritRandom.SeedOf(NscMeritSession.NextTrace(c));
        var run = ProjectNscSources.Trace(s.Context, source == 0 ? null : new[] { source }, split, polar, ignore, seed,
            c.Cancellation, maxDegreeOfParallelism: c.MaxDegreeOfParallelism);
        s.Ledger = run.Ledger;
        s.Add(run.Detectors);
        return 0;
    }

    // ================================================================ clearing

    /// <summary>NSDD, NSDE and NSDP with Det# 0 or −n: clears and returns true.</summary>
    private static bool Clear(MeritRowContext c, NscMeritSession s)
    {
        int det = c.Row.Int2;
        if (det > 0) return false;
        if (det == 0)
        {
            s.Detectors.Clear();
            s.ClearedAll = true;
            return true;
        }
        s.CheckObject(-det, "Det#");
        s.Detectors.Remove(-det);
        return true;
    }

    // ================================================================ NSDD

    private static double Nsdd(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        if (Clear(c, s)) return 0;
        int det = c.Row.Int2;
        int pixel = Whole(c, 1, "Pix#"), data = Whole(c, 2, "Data"), ignored = Whole(c, 3, "# Ignored");
        var held = s.DataOf(det, "Det#");
        if (data != 3) return NscDetectorQueries.Nsdd(held, pixel, data, ignored, s.Unit);
        // Data 3: "the returned flux for the pixel is normalized to the peak flux for all
        // consecutive NSDD operands in the Merit Function Editor which refer to the same surface
        // and detector" (help, NSDD); only for a positive Pix#.
        if (pixel <= 0) throw new MeritOperandException($"Data 3 (normalized flux) needs a pixel number; Pix# is {pixel} (\"A value of 3 is only supported when Pix# is a positive integer\", help).");
        double flux = NscDetectorQueries.Nsdd(held, pixel, 0, 0, s.Unit);
        double peak = GroupPeak(c, s, "NSDD", r => r.DataAt(2) == 3,
            r => NscDetectorQueries.Nsdd(held, PixelOf(r), 0, 0, s.Unit));
        return flux / peak;
    }

    // ================================================================ NSDE

    private static double Nsde(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        if (Clear(c, s)) return 0;
        int det = c.Row.Int2;
        int pixel = Whole(c, 1, "Pix#"), angle = Whole(c, 2, "Angle?"), data = Whole(c, 3, "Data"), ignored = Whole(c, 4, "# Ignored");
        var held = s.DataOf(det, "Det#");
        if (data is < 14 or > 16) return NscDetectorQueries.Nsde(held, pixel, angle, data, ignored);
        // Data 14–16: "Identical to the values returned by Data 9, 10, and 11, but normalized to the
        // peak tristimulus Y value for all consecutive NSDE operands ... which refer to the same
        // surface and detector ... Only valid if Pix# is a positive integer and Angle? is zero" (help, NSDE).
        if (pixel <= 0 || angle != 0)
            throw new MeritOperandException($"Data {data} (normalized tristimulus) needs a pixel number and Angle? 0; Pix# is {pixel}, Angle? {angle}.");
        double value = NscDetectorQueries.Nsde(held, pixel, 0, data - 5, 0);
        double peak = GroupPeak(c, s, "NSDE", r => r.DataAt(3) is >= 14 and <= 16 && r.DataAt(2) == 0,
            r => NscDetectorQueries.Nsde(held, PixelOf(r), 0, 10, 0));
        return value / peak;
    }

    private static int PixelOf(MeritOperandRow r) => (int)r.DataAt(1);

    /// <summary>The largest of <paramref name="value"/> over the run of consecutive rows of
    /// <paramref name="code"/> around this one with the same Surf and Det#, a positive whole
    /// Pix# and <paramref name="member"/> true (a reading of "all consecutive ... operands which
    /// refer to the same surface and detector", Conventions §10 item 72); computed once per run
    /// and evaluation. Throws when the peak is not positive.</summary>
    private static double GroupPeak(MeritRowContext c, NscMeritSession s, string code, Func<MeritOperandRow, bool> member,
        Func<MeritOperandRow, double> value)
    {
        var f = c.Function ?? throw new MeritOperandException($"normalized data needs the whole merit function to find the consecutive {code} rows, and this evaluation did not supply it.");
        bool In(int n)
        {
            if (n < 1 || n > f.Rows.Count) return false;
            var r = f.Row(n);
            double p = r.DataAt(1);
            return string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase) && r.Int1 == c.Row.Int1 && r.Int2 == c.Row.Int2
                && p > 0 && p == Math.Floor(p) && p <= int.MaxValue && member(r);
        }
        int first = c.Number;
        while (In(first - 1)) first--;
        if (s.GroupPeaks.TryGetValue(first, out double cached)) return cached;
        double peak = 0;
        for (int n = first; In(n); n++) peak = Math.Max(peak, value(f.Row(n)));
        if (!(peak > 0))
            throw new MeritOperandException($"the consecutive {code} rows {first} on read nothing on detector {c.Row.Int2}, so there is no peak to normalize to (trace with NSTR first).");
        s.GroupPeaks[first] = peak;
        return peak;
    }

    // ================================================================ NSDP, NSDC

    private static double Nsdp(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        if (Clear(c, s)) return 0;
        return NscDetectorQueries.Nsdp(s.DataOf(c.Row.Int2, "Det#"), Whole(c, 1, "Pix#"), Whole(c, 2, "Data"));
    }

    private static double Nsdc(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        if (c.Row.Int2 < 1)
            throw new MeritOperandException($"Det# is {c.Row.Int2}; NSDC reads a detector and does not clear (clear with NSDD, NSDE or NSDP).");
        return NscDetectorQueries.Nsdc(s.DataOf(c.Row.Int2, "Det#"), Whole(c, 1, "Pix#"), Whole(c, 2, "Data"));
    }

    // ================================================================ NSRA

    private static double Nsra(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        int source = c.Row.Int2;
        s.CheckObject(source, "Src#");
        if (s.Scene.Objects[source - 1].Geometry is not NscSource src)
            throw new MeritOperandException($"Src# {source} is a {s.Scene.Objects[source - 1].Geometry.TypeName}, not a source object.");
        bool split = Flag(c, 1), polar = Flag(c, 2) || split;
        int segment = Whole(c, 3, "Seg#"), data = Whole(c, 4, "Data"), element = Whole(c, 5, "Source #");
        CheckRayData(data);
        if (src.Common.AnalysisRays != 1)
            throw new MeritOperandException($"source object {source} has {src.Common.AnalysisRays} analysis rays; NSRA's source \"must be defined to trace just a single analysis ray\" (help, NSRA). Set # Analysis Rays to 1.");
        int elements = src.Properties.Array.Offsets($"Object {source}").Count;
        if (element < 0 || element > elements)
            throw new MeritOperandException($"Source # is {element}; the source has {elements} array element{(elements == 1 ? "" : "s")} (1 to {elements}; 0 is the first).");
        int e = Math.Max(1, element);
        var key = (source, split, polar, e);
        if (!s.SingleRays.TryGetValue(key, out var tree)) s.SingleRays[key] = tree = TraceOne(s, source, split, polar, e, c.Cancellation);
        if (tree.Errors.Count > 0)
            throw new MeritOperandException($"the ray of source object {source} failed (\"Errors are always considered\", help, NSRA): {tree.Errors[0]}");
        var segments = tree.Segments;
        var seg = segment == -1 ? segments[^1]
            : segments.FirstOrDefault(x => x.Number == segment)
              ?? throw new MeritOperandException($"Seg# {segment} does not exist; the ray has segments 0 to {segments.Count - 1} (−1 the last).");
        double L(double meters) => OpticsConventions.FromMeters(meters, s.Unit);
        int d = data > 30 ? data - 30 : data;
        return d switch
        {
            1 => L(seg.Position.X), 2 => L(seg.Position.Y), 3 => L(seg.Position.Z),
            4 => seg.Direction.X, 5 => seg.Direction.Y, 6 => seg.Direction.Z,
            7 => seg.Normal.X, 8 => seg.Normal.Y, 9 => seg.Normal.Z,
            10 => L(seg.PathLength),
            11 => seg.Intensity,
            14 => seg.Index,
            16 => L(PathFromSource(segments, seg)),
            _ => L(seg.OpticalPath),   // 17
        };
    }

    private static void CheckRayData(int data)
    {
        if (data is >= 1 and <= 11 or 14 or 16 or 17 or >= 31 and <= 39) return;
        if (data is 12 or 13 or 15)
            throw new MeritOperandException($"Data {data} (a phase) is not built: the tracer's segments record no phase (the ray database is plan item OP-83).");
        if (data is >= 21 and <= 29)
            throw new MeritOperandException($"Data {data} (an electric field component or its phase) is not built: the tracer's segments record no field.");
        throw new MeritOperandException($"Data is {data}; the help defines 1–17, 21–29 and 31–39.");
    }

    private static double PathFromSource(IReadOnlyList<NscSegment> segments, NscSegment seg)
    {
        var byNumber = segments.ToDictionary(x => x.Number);
        double sum = 0;
        for (var at = seg; ; at = byNumber[at.Parent])
        {
            sum += at.PathLength;
            if (at.Parent < 0 || !byNumber.ContainsKey(at.Parent)) break;
        }
        return sum;
    }

    /// <summary>The one ray of array element <paramref name="element"/> (1-based) of the source,
    /// traced at its wavelength without scattering.</summary>
    private static NscRayTree TraceOne(NscMeritSession s, int source, bool split, bool polar, int element, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(s.Settings.SystemData, notes);
        var saved = s.Settings.NonSequential;
        var settings = ProjectNonSequential.Settings(saved, s.Unit, new NscTraceSettings
        {
            UsePolarization = polar,
            Splitting = !split ? NscSplitting.None : saved?.SimpleRaySplitting == true ? NscSplitting.Simple : NscSplitting.Split,
            Environment = system.Environment,
            Coatings = Coatings.CoatingCatalogSettings.FromSettings(s.Settings.Coatings, notes),
            PrimaryMicrometers = system.Wavelengths.Primary.Micrometers,
        });
        var set = NscSourceEmitter.Emit(s.Scene, source, settings, ProjectNscSources.EmitOptions(s.Context, NscEmissionPurpose.Analysis), cancellation);
        long wanted = element - 1;   // one ray per element: element e's ray is number e − 1
        foreach (var emission in set.Emissions)
            for (int i = 0; i < emission.Rays.Count; i++)
            {
                long number = emission.SourceRayNumbers?[i] ?? i;
                if (number != wanted) continue;
                var tracer = new NscTracer(s.Scene, emission.WavelengthMicrometers, settings);
                return tracer.TraceSingle(emission.Rays[i], number);
            }
        throw new MeritOperandException($"source object {source} launched no ray for element {element}.");
    }

    // ================================================================ object data

    private static (NscMeritSession S, int Number, OpticsNscObjectSettings Row) ObjectRow(MeritRowContext c)
    {
        Surf(c);
        var s = NscMeritSession.Of(c);
        var rows = s.Rows;
        int number = c.Row.Int2;
        if (number < 1 || number > rows.Count)
            throw new MeritOperandException(rows.Count == 0
                ? "the system has no non-sequential objects."
                : $"Object {number} is not an object; the system has objects 1 to {rows.Count}.");
        return (s, number, rows[number - 1] ?? new OpticsNscObjectSettings());
    }

    private static int Reference(MeritRowContext c)
    {
        int r = Whole(c, 1, "Ref?");
        if (r is < 0 or > 2) throw new MeritOperandException($"Ref? is {r}; it must be 0 (the reference object), 1 (the non-sequential origin) or 2 (the global reference).");
        return r;
    }

    private static double Position(MeritRowContext c, int axis)
    {
        var (s, number, row) = ObjectRow(c);
        if (Reference(c) == 0) return axis switch { 0 => row.X ?? 0, 1 => row.Y ?? 0, _ => row.Z ?? 0 };
        var t = NscObjectFrames.Global(s.Rows, number, s.Unit).Translation;
        return OpticsConventions.FromMeters(axis switch { 0 => t.X, 1 => t.Y, _ => t.Z }, s.Unit);
    }

    private static double Tilt(MeritRowContext c, int axis)
    {
        var (s, number, row) = ObjectRow(c);
        if (Reference(c) == 0) return axis switch { 0 => row.TiltX ?? 0, 1 => row.TiltY ?? 0, _ => row.TiltZ ?? 0 };
        var angles = NscObjectFrames.Tilts(NscObjectFrames.Global(s.Rows, number, s.Unit), row.GlobalXyzRotationOrder ?? false);
        return axis switch { 0 => angles.X, 1 => angles.Y, _ => angles.Z };
    }

    private static double Nsrm(MeritRowContext c)
    {
        var (s, number, _) = ObjectRow(c);
        int reference = Reference(c);
        int data = Whole(c, 2, "Data");
        if (data is < 1 or > 9) throw new MeritOperandException($"Data is {data}; it must be 1 (R[1][1]) to 9 (R[3][3]).");
        int i = (data - 1) / 3, j = (data - 1) % 3;
        if (reference == 0) return i == j ? 1 : 0;   // help: "always be the identity matrix when using this reference"
        return NscObjectFrames.Element(NscObjectFrames.Global(s.Rows, number, s.Unit), i, j);
    }

    private static double ParameterValue(MeritRowContext c)
    {
        var (_, number, row) = ObjectRow(c);
        int param = Whole(c, 1, "Param");
        var saved = row.Parameters;
        var d = NscObjectCatalogue.Default.Find(row.Type ?? "");
        int count = Math.Max(saved?.Count ?? 0, d?.Parameters.Count ?? 0);
        if (param < 1 || param > count)
            throw new MeritOperandException($"Param {param} is not a parameter of object {number} ({row.Type ?? "no type"}); it has parameters 1 to {count}.");
        return saved is not null && param <= saved.Count ? saved[param - 1] : d!.Parameters[param - 1].Default;
    }
}

/// <summary>
/// Object frames from the saved rows alone (no shapes built), for the object operands and the
/// variables (plan item OP-88): each object's placement relative to its reference object
/// (<see cref="NscPlacement"/>, Conventions §4 and §69) chained to the non-sequential origin, as
/// <see cref="NscScene.GlobalPlacements"/> chains them.
/// </summary>
public static class NscObjectFrames
{
    /// <summary>Object <paramref name="number"/>'s local-to-origin transform (meters). Throws
    /// <see cref="ArgumentException"/> naming the object when a reference is not an earlier object.</summary>
    public static RigidTransform Global(IReadOnlyList<OpticsNscObjectSettings> rows, int number, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (number < 1 || number > rows.Count) throw new ArgumentException($"Object {number} is not an object; there are {rows.Count}.");
        var r = rows[number - 1] ?? new OpticsNscObjectSettings();
        var local = new NscPlacement(
            OpticsConventions.ToMeters(r.X ?? 0, unit), OpticsConventions.ToMeters(r.Y ?? 0, unit), OpticsConventions.ToMeters(r.Z ?? 0, unit),
            r.TiltX ?? 0, r.TiltY ?? 0, r.TiltZ ?? 0, r.GlobalXyzRotationOrder ?? false).ToTransform();
        int reference = NscScene.Resolve(r.ReferenceObject ?? 0, number);
        if (reference == 0) return local;
        if (reference < 0 || reference >= number)
            throw new ArgumentException($"Object {number}: the reference object {r.ReferenceObject} resolves to {reference}; it must be 0 or an earlier object (help, \"Object Placement\").");
        return RigidTransform.Compose(Global(rows, reference, unit), local);
    }

    /// <summary>R[i][j] (0-based) of the rotation: column j is the local axis j in the origin's frame.</summary>
    public static double Element(RigidTransform t, int i, int j)
    {
        var col = j switch { 0 => t.Col0, 1 => t.Col1, _ => t.Col2 };
        return i switch { 0 => col.X, 1 => col.Y, _ => col.Z };
    }

    /// <summary>
    /// Tilts about x, y and z (degrees) that rebuild the rotation of <paramref name="t"/> in the
    /// object's order (<see cref="NscPlacement"/>): R = Rx(a)·Ry(b)·Rz(c) by default, or
    /// Rz(c)·Ry(b)·Rx(a) in the global XYZ order. Derived from the matrix products: by default
    /// R[0][2] = sin b, R[1][2] = −sin a cos b, R[2][2] = cos a cos b, R[0][1] = −cos b sin c,
    /// R[0][0] = cos b cos c; in the global order R[2][0] = −sin b, R[2][1] = cos b sin a,
    /// R[2][2] = cos b cos a, R[1][0] = sin c cos b, R[0][0] = cos c cos b. The branch is
    /// −90° ≤ b ≤ 90°; at |b| = 90° (gimbal lock) c is taken as 0. A reading: the help does not
    /// say which of the equivalent angle sets the NTxx operands return (Conventions §10 item 72).
    /// </summary>
    public static (double X, double Y, double Z) Tilts(RigidTransform t, bool globalOrder)
    {
        double R(int i, int j) => Element(t, i, j);
        const double deg = 180 / Math.PI;
        double a, b, c;
        if (!globalOrder)
        {
            double cb = Math.Sqrt(R(0, 0) * R(0, 0) + R(0, 1) * R(0, 1));
            b = Math.Atan2(R(0, 2), cb);
            if (cb > 1e-12)
            {
                a = Math.Atan2(-R(1, 2), R(2, 2));
                c = Math.Atan2(-R(0, 1), R(0, 0));
            }
            else
            {
                c = 0;   // then R[1][0] = sin a sin b, R[1][1] = cos a
                a = Math.Atan2(R(1, 0) * Math.Sign(R(0, 2)), R(1, 1));
            }
        }
        else
        {
            double cb = Math.Sqrt(R(2, 1) * R(2, 1) + R(2, 2) * R(2, 2));
            b = Math.Atan2(-R(2, 0), cb);
            if (cb > 1e-12)
            {
                a = Math.Atan2(R(2, 1), R(2, 2));
                c = Math.Atan2(R(1, 0), R(0, 0));
            }
            else
            {
                c = 0;   // then R[0][1] = sin b sin a, R[1][1] = cos a
                a = Math.Atan2(R(0, 1) * Math.Sign(-R(2, 0)), R(1, 1));
            }
        }
        return (a * deg, b * deg, c * deg);
    }
}
