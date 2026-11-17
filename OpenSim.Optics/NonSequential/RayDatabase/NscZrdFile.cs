using System.Numerics;
using System.Text;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.Polarization;

namespace OpenSim.Optics.NonSequential.RayDatabase;

/// <summary>The ZRD formats (help, "Ray Database (ZRD) Files"; NSTR's zrd_format 0, 1, 2) and this
/// program's own lossless format.</summary>
public enum NscZrdFormat
{
    /// <summary>OpticStudio's Uncompressed Full Data (UFD), version 2003: written and read.</summary>
    UncompressedFullData = 0,
    /// <summary>Compressed Basic Data (CBD): undocumented compression, refused.</summary>
    CompressedBasicData = 1,
    /// <summary>Compressed Full Data (CFD): undocumented compression, refused.</summary>
    CompressedFullData = 2,
    /// <summary>This program's full-data format (docs/Optics-Conventions.md §75): every segment
    /// field to the bit, the runs and every detector hit, so it replays onto detectors exactly.</summary>
    OpenSimFull = 3,
}

/// <summary>
/// Ray database files (plan item OP-83; docs/Optics-Conventions.md §75).
/// <para>
/// <b>UFD</b> follows the help ("The ZRD Uncompressed Full Data Format (UFD)", "ZRD Format And
/// Version Numbers"): a 32-bit version-and-format record (2003 for UFD), a 32-bit maximum number of
/// segments, then for each ray a 32-bit segment count and that many RAYPATH records: ten 32-bit
/// integers (status, level, hit_object, hit_face, unused, in_object, parent, storage, xybin,
/// lmbin), 23 doubles (index, starting_phase, x, y, z, l, m, n, nx, ny, nz, path_to, intensity,
/// phase_of, phase_at, exr, exi, eyr, eyi, ezr, ezi, optical_path_w, optical_path_lu) and two
/// 32-bit integers (order_x, order_y): 232 bytes, the size the help states ("the structure size is
/// 232 bytes for each ray segment"; its worked example says 208, which the listed members do not
/// add up to). Lengths are in lens units. Little-endian.
/// </para>
/// <para>
/// <b>OpenSim full</b> starts with the 32-bit magic 0x4452534F ("OSRD" in file order), which no
/// ZRD version record takes (2003, 12003, 22003); its layout is in §75.
/// </para>
/// </summary>
public static class NscZrdFile
{
    /// <summary>"The current version number is 2003" (help, "ZRD Format And Version Numbers").</summary>
    public const int UfdVersion = 2003;

    /// <summary>The CBD and CFD format numbers added to the version (same page): 10000 and 20000.</summary>
    public const int CbdFormat = 10000, CfdFormat = 20000;

    /// <summary>Bytes per UFD segment (help, "The ZRD Uncompressed Full Data Format (UFD)").</summary>
    public const int UfdSegmentBytes = 232;

    /// <summary>The OpenSim format's first record ("OSRD").</summary>
    public const int OpenSimMagic = 0x4452534F;

    public const int OpenSimVersion = 1;

    // OP-81
    /// <summary>The OpenSim format version written when some segment's wavelength was shifted by a
    /// volume event: each segment adds its wavelength (µm, NaN for the run's) and wavelength number
    /// after its index, and its detector hits are at that wavelength. Databases without shifts are
    /// still written as version 1, byte for byte as before.</summary>
    public const int OpenSimShiftedVersion = 2;

    /// <summary>UFD status bits (help, "The ZRD Uncompressed Full Data Format (UFD)", "status").</summary>
    private const uint SxTerminated = 1, SxReflected = 2, SxRefracted = 4, SxScattered = 8, SxDiffracted = 16, SxGhost = 32,
        SxDiffractedFromPrevious = 64, SxScatteredFromPrevious = 128, SxError = 256, SxBulk = 512;

    // ================================================================== detection

    /// <summary>The format of the file at <paramref name="path"/>, from its first record. Throws
    /// <see cref="InvalidDataException"/> for a record no format uses.</summary>
    public static NscZrdFormat Detect(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> b = stackalloc byte[4];
        if (stream.Read(b) != 4) throw new InvalidDataException($"{Path.GetFileName(path)}: the file is shorter than its first record.");
        return Classify(BitConverter.ToInt32(b), Path.GetFileName(path));
    }

    private static NscZrdFormat Classify(int first, string name) => first switch
    {
        UfdVersion => NscZrdFormat.UncompressedFullData,
        UfdVersion + CbdFormat => NscZrdFormat.CompressedBasicData,
        UfdVersion + CfdFormat => NscZrdFormat.CompressedFullData,
        OpenSimMagic => NscZrdFormat.OpenSimFull,
        _ => throw new InvalidDataException($"{name}: the first record is {first}; a ZRD file starts with version 2003 plus its format number (0 UFD, 10000 CBD, 20000 CFD), and an OpenSim ray database with \"OSRD\". Older ZRD versions are not read (OpticStudio converts them when it opens them)."),
    };

    private static NotSupportedException Compressed(NscZrdFormat f, string name) =>
        new($"{name}: {(f == NscZrdFormat.CompressedBasicData ? "Compressed Basic Data (CBD)" : "Compressed Full Data (CFD)")} ZRD files are \"dynamically compressed in a way that makes the file unsuitable for read/write operations outside of OpticStudio\" (help, \"Ray Database (ZRD) Files\"); not read or written. Save as Uncompressed Full Data or the OpenSim format.");

    // ================================================================== reading

    /// <summary>Reads a UFD or OpenSim ray database; UFD lengths are in <paramref name="unit"/>.</summary>
    public static NscRayDatabase Read(string path, LensUnit unit)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, unit, Path.GetFileName(path));
    }

    public static NscRayDatabase Read(Stream stream, LensUnit unit, string name = "Ray database")
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int first = Int(r, name, "the first record");
        var format = Classify(first, name);
        var db = format switch
        {
            NscZrdFormat.UncompressedFullData => ReadUfd(r, unit, name),
            NscZrdFormat.OpenSimFull => ReadOpenSim(r, name),
            _ => throw Compressed(format, name),
        };
        db.Validate(name);
        return db;
    }

    private static int Int(BinaryReader r, string name, string what)
    {
        try { return r.ReadInt32(); }
        catch (EndOfStreamException) { throw new InvalidDataException($"{name}: the file ends inside {what}."); }
    }

    /// <summary>The next 32-bit record, or null at a clean end of file.</summary>
    private static int? IntOrEnd(BinaryReader r, string name, string what)
    {
        Span<byte> b = stackalloc byte[4];
        int got = r.Read(b);
        if (got == 0) return null;
        if (got < 4)
        {
            int more = r.Read(b[got..]);
            if (got + more < 4) throw new InvalidDataException($"{name}: the file ends inside {what}.");
        }
        return BitConverter.ToInt32(b);
    }

    private static NscRayDatabase ReadUfd(BinaryReader r, LensUnit unit, string name)
    {
        int max = Int(r, name, "the maximum number of segments");
        if (max < 1) throw new InvalidDataException($"{name}: the maximum number of segments is {max}.");
        var runs = new List<NscRayDatabaseRun>();
        var runKeys = new List<(int Number, double Micrometers)>();
        var counts = new List<long>();
        var rays = new List<(int Run, int Source, NscSegment[] Segs)>();
        var buffer = new byte[UfdSegmentBytes];
        double M(double v) => OpticsConventions.ToMeters(v, unit);
        for (int k = 0; ; k++)
        {
            if (IntOrEnd(r, name, $"ray {k}'s segment count") is not int n) break;
            if (n < 1 || n > max)
                throw new InvalidDataException($"{name}: ray {k} has {n} segments; the file's maximum is {max}.");
            var segs = new NscSegment[n];
            int source = 0, run = -1;
            for (int s = 0; s < n; s++)
            {
                if (r.Read(buffer, 0, UfdSegmentBytes) is int got && got < UfdSegmentBytes)
                {
                    int more = got;
                    while (more < UfdSegmentBytes && r.Read(buffer, more, UfdSegmentBytes - more) is int m && m > 0) more += m;
                    if (more < UfdSegmentBytes) throw new InvalidDataException($"{name}: the file ends inside ray {k}, segment {s}.");
                }
                uint status = BitConverter.ToUInt32(buffer, 0);
                int I(int i) => BitConverter.ToInt32(buffer, 4 * i);
                double D(int i) => BitConverter.ToDouble(buffer, 40 + 8 * i);
                const uint known = SxTerminated | SxReflected | SxRefracted | SxScattered | SxDiffracted | SxGhost
                    | SxDiffractedFromPrevious | SxScatteredFromPrevious | SxError | SxBulk;
                if ((status & ~known) != 0)
                    throw new InvalidDataException($"{name}: ray {k}, segment {s}: status bits 0x{status & ~known:X} are not among those the help documents.");
                var events = NscEvents.None;
                if ((status & SxTerminated) != 0) events |= NscEvents.Terminated;
                if ((status & SxReflected) != 0) events |= NscEvents.Reflected;
                if ((status & SxRefracted) != 0) events |= NscEvents.Transmitted;
                if ((status & SxScattered) != 0) events |= NscEvents.Scattered;
                if ((status & SxDiffracted) != 0) events |= NscEvents.Diffracted;
                if ((status & SxGhost) != 0) events |= NscEvents.Ghost;
                if ((status & SxDiffractedFromPrevious) != 0) events |= NscEvents.DiffractedFromParent;
                if ((status & SxScatteredFromPrevious) != 0) events |= NscEvents.ScatteredFromParent;
                if ((status & SxError) != 0) events |= NscEvents.Error;
                if ((status & SxBulk) != 0) events |= NscEvents.BulkScattered;
                int parent = I(6);
                if (s == 0)
                {
                    source = I(2);
                    double micrometers = D(8);   // segment 0: "the nx value stores the wavelength"
                    var key = (I(7), micrometers);   // segment 0: "storage ... is the ray wavelength number"
                    run = runKeys.IndexOf(key);
                    if (run < 0)
                    {
                        if (!(micrometers > 0) || !double.IsFinite(micrometers))
                            throw new InvalidDataException($"{name}: ray {k}'s wavelength (segment 0's nx) is {micrometers}; it must be positive (µm).");
                        run = runKeys.Count;
                        runKeys.Add(key);
                        counts.Add(0);
                    }
                    counts[run]++;
                }
                segs[s] = new NscSegment
                {
                    Number = s,
                    Parent = s == 0 ? -1 : parent,
                    Level = I(1),
                    Inside = I(5),
                    Hit = s == 0 ? 0 : I(2),
                    Face = I(3),
                    Index = D(0),
                    Position = new Vector3D(M(D(2)), M(D(3)), M(D(4))),
                    Direction = new Vector3D(D(5), D(6), D(7)),
                    Normal = s == 0 ? Vector3D.Zero : new Vector3D(D(8), D(9), D(10)),
                    PathLength = M(D(11)),
                    Intensity = D(12),
                    OpticalPath = M(D(22)),
                    Events = events,
                };
            }
            rays.Add((run, source, segs));
        }
        for (int i = 0; i < runKeys.Count; i++) runs.Add(new NscRayDatabaseRun(runKeys[i].Micrometers, runKeys[i].Number, counts[i]));
        var numbers = new long[runKeys.Count];
        return new NscRayDatabase
        {
            Runs = runs,
            Rays = rays.Select(x => new NscRecordedRay { Run = x.Run, RayNumber = numbers[x.Run]++, SourceObject = x.Source, Segments = x.Segs }).ToArray(),
            Origin = NscRayDatabaseOrigin.UfdFile,
        };
    }

    private static NscRayDatabase ReadOpenSim(BinaryReader r, string name)
    {
        try
        {
            int version = r.ReadInt32();
            if (version != OpenSimVersion && version != OpenSimShiftedVersion)   // OP-81: version 2
                throw new InvalidDataException($"{name}: OpenSim ray database version {version} is not read (this program writes versions {OpenSimVersion} and {OpenSimShiftedVersion}).");
            int runCount = r.ReadInt32();
            if (runCount < 0) throw new InvalidDataException($"{name}: {runCount} runs.");
            var runs = new List<NscRayDatabaseRun>();
            for (int i = 0; i < runCount; i++)
            {
                double um = r.ReadDouble();
                int number = r.ReadInt32();
                long launched = r.ReadInt64();
                runs.Add(new NscRayDatabaseRun(um, number, launched));
            }
            var rays = new List<NscRecordedRay>();
            for (int k = 0; ; k++)
            {
                if (IntOrEnd(r, name, $"ray {k}") is not int run) break;
                if (run < 0 || run >= runCount) throw new InvalidDataException($"{name}: ray {k} names run {run}; the file has {runCount}.");
                long rayNumber = r.ReadInt64();
                int source = r.ReadInt32();
                int n = r.ReadInt32();
                if (n < 1) throw new InvalidDataException($"{name}: ray {k} has {n} segments.");
                var segs = new NscSegment[n];
                double um = runs[run].AirMicrometers;
                for (int s = 0; s < n; s++)
                {
                    int parent = r.ReadInt32(), level = r.ReadInt32(), inside = r.ReadInt32(), hit = r.ReadInt32(), face = r.ReadInt32(), events = r.ReadInt32();
                    var pos = V(r); var dir = V(r); var normal = V(r);
                    double intensity = r.ReadDouble(), path = r.ReadDouble(), optical = r.ReadDouble(), index = r.ReadDouble();
                    // OP-81: version 2 carries the segment's shifted wavelength.
                    double? segUm = null;
                    int? segNumber = null;
                    if (version == OpenSimShiftedVersion)
                    {
                        double w = r.ReadDouble();
                        int wn = r.ReadInt32();
                        if (!double.IsNaN(w))
                        {
                            if (!(w > 0)) throw new InvalidDataException($"{name}: ray {k}, segment {s}: wavelength {w} µm.");
                            segUm = w;
                            segNumber = wn;
                        }
                    }
                    int hits = r.ReadInt32();
                    if (hits < 0) throw new InvalidDataException($"{name}: ray {k}, segment {s}: {hits} detector hits.");
                    List<NscDetectorHit>? list = hits == 0 ? null : new List<NscDetectorHit>(hits);
                    for (int h = 0; h < hits; h++)
                    {
                        int obj = r.ReadInt32();
                        var lp = V(r); var ld = V(r);
                        double power = r.ReadDouble(), opl = r.ReadDouble(), mi = r.ReadDouble();
                        bool ghost = r.ReadByte() != 0;
                        CoherencyMatrix? phi = null;
                        if (r.ReadByte() != 0)
                        {
                            var m = new Complex[9];
                            for (int e = 0; e < 9; e++) m[e] = new Complex(r.ReadDouble(), r.ReadDouble());
                            phi = CoherencyMatrix.FromElements(m);
                        }
                        list!.Add(new NscDetectorHit(obj, rayNumber, lp, ld, power, opl, mi, segUm ?? um, phi, ghost));   // OP-81
                    }
                    segs[s] = new NscSegment
                    {
                        Number = s, Parent = parent, Level = level, Inside = inside, Hit = hit, Face = face,
                        Position = pos, Direction = dir, Normal = normal, Intensity = intensity, PathLength = path,
                        OpticalPath = optical, Index = index, Events = (NscEvents)events, DetectorHits = list,
                        AirMicrometers = segUm, WavelengthNumber = segNumber,   // OP-81
                    };
                }
                rays.Add(new NscRecordedRay { Run = run, RayNumber = rayNumber, SourceObject = source, Segments = segs });
            }
            return new NscRayDatabase { Runs = runs, Rays = rays, Origin = NscRayDatabaseOrigin.OpenSimFile };
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{name}: the file ends inside a record (truncated?).");
        }
    }

    private static Vector3D V(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());

    // ================================================================== writing

    /// <summary>Writes <paramref name="database"/> in <paramref name="format"/>; UFD lengths in
    /// <paramref name="unit"/>. CBD and CFD are refused by name.</summary>
    public static void Write(string path, NscRayDatabase database, NscZrdFormat format, LensUnit unit)
    {
        using var stream = File.Create(path);
        Write(stream, database, format, unit, Path.GetFileName(path));
    }

    public static void Write(Stream stream, NscRayDatabase database, NscZrdFormat format, LensUnit unit, string name = "Ray database")
    {
        ArgumentNullException.ThrowIfNull(database);
        database.Validate(name);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        switch (format)
        {
            case NscZrdFormat.UncompressedFullData: WriteUfd(w, database, unit); break;
            case NscZrdFormat.OpenSimFull: WriteOpenSim(w, database, name); break;
            default: throw Compressed(format, name);
        }
    }

    private static void WriteUfd(BinaryWriter w, NscRayDatabase db, LensUnit unit)
    {
        double L(double meters) => OpticsConventions.FromMeters(meters, unit);
        w.Write(UfdVersion);
        w.Write(Math.Max(1, db.MaxSegments));
        foreach (var ray in db.Rays)
        {
            var run = db.Runs[ray.Run];
            double waveMeters = OpticsConventions.MicrometersToMeters(run.AirMicrometers);
            w.Write(ray.Segments.Count);
            foreach (var s in ray.Segments)
            {
                bool zero = s.Number == 0;
                uint status = 0;
                var e = s.Events;
                if (e.HasFlag(NscEvents.Terminated)) status |= SxTerminated;
                if (e.HasFlag(NscEvents.Reflected) || e.HasFlag(NscEvents.TotalInternalReflection)) status |= SxReflected;
                if (e.HasFlag(NscEvents.Transmitted)) status |= SxRefracted;
                if (e.HasFlag(NscEvents.Scattered)) status |= SxScattered;
                if (e.HasFlag(NscEvents.Diffracted)) status |= SxDiffracted;
                if (e.HasFlag(NscEvents.Ghost)) status |= SxGhost;
                if (e.HasFlag(NscEvents.DiffractedFromParent)) status |= SxDiffractedFromPrevious;
                if (e.HasFlag(NscEvents.ScatteredFromParent)) status |= SxScatteredFromPrevious;
                if (e.HasFlag(NscEvents.Error)) status |= SxError;
                if (e.HasFlag(NscEvents.BulkScattered)) status |= SxBulk;
                w.Write(status);
                w.Write(s.Level);
                w.Write(zero ? ray.SourceObject : s.Hit);   // segment 0: "the source segment object number"
                w.Write(s.Face);
                w.Write(0);                                  // unused
                w.Write(s.Inside);
                w.Write(zero ? 0 : s.Parent);                // "If zero, the ray came from the original source point"
                w.Write(zero ? run.WavelengthNumber : 0);    // storage: segment 0's wavelength number
                w.Write(0);                                  // xybin: not recorded
                w.Write(0);                                  // lmbin
                w.Write(s.Index);
                w.Write(0.0);                                // starting_phase
                w.Write(L(s.Position.X)); w.Write(L(s.Position.Y)); w.Write(L(s.Position.Z));
                w.Write(s.Direction.X); w.Write(s.Direction.Y); w.Write(s.Direction.Z);
                w.Write(zero ? run.AirMicrometers : s.Normal.X);   // segment 0: nx is the wavelength (µm)
                w.Write(zero ? 0.0 : s.Normal.Y);
                w.Write(zero ? 0.0 : s.Normal.Z);
                w.Write(L(s.PathLength));
                w.Write(s.Intensity);
                w.Write(0.0);                                // phase_of: no phase objects
                w.Write(0.0);                                // phase_at: not computed
                for (int i = 0; i < 6; i++) w.Write(0.0);    // exr .. ezi: the trace carries coherency matrices, not fields
                w.Write(s.OpticalPath / waveMeters);         // optical_path_w (waves in the system's air)
                w.Write(L(s.OpticalPath));                   // optical_path_lu
                w.Write(0);                                  // order_x
                w.Write(0);                                  // order_y
            }
        }
    }

    private static void WriteOpenSim(BinaryWriter w, NscRayDatabase db, string name)
    {
        if (!db.HasDetectorHits)
            throw new InvalidOperationException($"{name}: a database read from a UFD file has no detector hits; save it as UFD, not in the OpenSim format, which promises them.");
        w.Write(OpenSimMagic);
        bool shifted = db.Rays.Any(ray => ray.Segments.Any(g => g.AirMicrometers is not null));   // OP-81
        w.Write(shifted ? OpenSimShiftedVersion : OpenSimVersion);
        w.Write(db.Runs.Count);
        foreach (var run in db.Runs)
        {
            w.Write(run.AirMicrometers);
            w.Write(run.WavelengthNumber);
            w.Write(run.LaunchedRays);
        }
        foreach (var ray in db.Rays)
        {
            double um = db.Runs[ray.Run].AirMicrometers;
            w.Write(ray.Run);
            w.Write(ray.RayNumber);
            w.Write(ray.SourceObject);
            w.Write(ray.Segments.Count);
            foreach (var s in ray.Segments)
            {
                w.Write(s.Parent); w.Write(s.Level); w.Write(s.Inside); w.Write(s.Hit); w.Write(s.Face); w.Write((int)s.Events);
                W(w, s.Position); W(w, s.Direction); W(w, s.Normal);
                w.Write(s.Intensity); w.Write(s.PathLength); w.Write(s.OpticalPath); w.Write(s.Index);
                if (shifted) { w.Write(s.AirMicrometers ?? double.NaN); w.Write(s.WavelengthNumber ?? 0); }   // OP-81
                double segUm = s.AirMicrometers ?? um;   // OP-81: hits are at the segment's wavelength
                var hits = s.DetectorHits ?? Array.Empty<NscDetectorHit>();
                w.Write(hits.Count);
                foreach (var h in hits)
                {
                    if (h.RayNumber != ray.RayNumber || h.AirMicrometers != segUm)
                        throw new InvalidOperationException($"{name}: ray {ray.RayNumber}, segment {s.Number}: a detector hit belongs to ray {h.RayNumber} at {h.AirMicrometers} µm, not to this segment at {segUm} µm.");
                    w.Write(h.ObjectNumber);
                    W(w, h.LocalPoint); W(w, h.LocalDirection);
                    w.Write(h.Power); w.Write(h.OpticalPath); w.Write(h.MediumIndex);
                    w.Write((byte)(h.Ghost ? 1 : 0));
                    w.Write((byte)(h.Polarization is null ? 0 : 1));
                    if (h.Polarization is { } phi)
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++) { w.Write(phi[r, c].Real); w.Write(phi[r, c].Imaginary); }
                }
            }
        }
    }

    private static void W(BinaryWriter w, Vector3D v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    // ================================================================== subsets and joins

    /// <summary>
    /// The rays and segments <paramref name="filter"/> passes (the Ray Database Viewer's "Save
    /// Subset Data As", ZPL ZRDFILTER): each kept tree keeps the segments on its passing branches,
    /// renumbered in order with their parents, detector hits and ray numbers; the runs are kept.
    /// </summary>
    public static NscRayDatabase Subset(NscRayDatabase database, NscRayFilter? filter, NscRayFilterFrame? frame = null,
        long firstRay = 1, long lastRay = -1)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (firstRay < 1) throw new ArgumentException($"Ray database subset: the first ray is counted from 1 (it is {firstRay}).");
        if (lastRay >= 0 && lastRay < firstRay) throw new ArgumentException($"Ray database subset: the last ray ({lastRay}) comes before the first ({firstRay}).");
        var window = database.Rays.Skip((int)Math.Min(int.MaxValue, firstRay - 1));
        if (lastRay >= 0) window = window.Take((int)Math.Min(int.MaxValue, lastRay - firstRay + 1));
        var within = database with { Rays = window.ToArray() };
        var rays = NscRayFilter.Select(within, filter, frame).Select(x => Prune(x.Ray, x.Segments)).ToArray();
        return database with { Rays = rays };
    }

    private static NscRecordedRay Prune(NscRecordedRay ray, bool[] keep)
    {
        if (keep.All(k => k)) return ray;
        var map = new int[keep.Length];
        var segs = new List<NscSegment>();
        for (int i = 0; i < keep.Length; i++)
        {
            if (!keep[i]) { map[i] = -1; continue; }
            map[i] = segs.Count;
            var s = ray.Segments[i];
            segs.Add(s with { Number = segs.Count, Parent = s.Parent < 0 ? -1 : map[s.Parent] });
        }
        return ray with { Segments = segs };
    }

    /// <summary>
    /// Two databases of the same system joined (ZPL ZRDSUM and ZRDAPPEND: "no attempt is made to
    /// modify object numbers or coordinates"): the second's runs follow the first's, each ray
    /// keeping its run. Both must come from the same kind of file.
    /// </summary>
    public static NscRayDatabase Join(NscRayDatabase first, NscRayDatabase second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.HasDetectorHits != second.HasDetectorHits)
            throw new ArgumentException("Ray databases joined: one has detector hits (OpenSim format) and the other does not (UFD); \"Both input files must use the same ZRD format\" (help, \"ZRDSUM\").");
        int offset = first.Runs.Count;
        return new NscRayDatabase
        {
            Runs = first.Runs.Concat(second.Runs).ToArray(),
            Rays = first.Rays.Concat(second.Rays.Select(r => r with { Run = r.Run + offset })).ToArray(),
            Origin = first.Origin,
        };
    }
}
