using System.Reflection;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-83: the ray database, filter strings, named filters, ZRD files, the Ray Database Viewer and
/// Playback ZRD on Detector (docs/Optics-Conventions.md §75). Gate: a filter and its complement
/// partition the rays (the branches of every ray tree); replaying a saved trace reproduces the
/// detectors to the bit. Flag semantics are checked on hand-built segment lists, including the
/// S, F, D, E and B flags that no built physics sets yet (OP-80, OP-81, OP-82).
/// </summary>
public sealed class NscRayDatabaseTests
{
    private const double Mm = 1e-3;

    private static NscObject Glass(NscGeometry g, double n, NscPlacement placement = default) => new()
    {
        Geometry = g, Material = NscMaterial.Of(ParaxialMedium.Constant(n)), Placement = placement,
    };

    /// <summary>A transparent detector, a glass sphere and an absorbing detector on the z axis.</summary>
    private static NscScene LensScene() => new()
    {
        Objects = new NscObject[]
        {
            new() { Geometry = new NscDetectorRectangle(20 * Mm, 20 * Mm, 8, 8), Placement = new NscPlacement(Z: 10 * Mm) },
            Glass(new NscSphere(8 * Mm), 1.6, new NscPlacement(Z: 30 * Mm)),
            new() { Geometry = new NscDetectorRectangle(30 * Mm, 30 * Mm, 12, 12), Placement = new NscPlacement(Z: 60 * Mm), Material = NscMaterial.Absorb },
        }
    };

    private static NscDatabaseRun Trace(NscScene scene, NscTraceSettings settings, int rays, NscRayRecorder? recorder = null)
    {
        var launch = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 25, rays, 1, NscSampling.Random, 5);
        var rec = recorder ?? new NscRayRecorder();
        var result = new NscTracer(scene, 0.55, settings).Run(launch, rec);
        return new NscDatabaseRun(result, rec.Database);
    }

    private sealed record NscDatabaseRun(NscRunResult Result, NscRayDatabase Database);

    private static readonly NscTraceSettings Split = new() { UsePolarization = true, Splitting = NscSplitting.Split };
    private static readonly NscTraceSettings Plain = new() { UsePolarization = false };

    // ================================================================= gate: partition

    /// <summary>
    /// The first half of the gate: for every branch of every ray tree of a split trace, exactly
    /// one of F and !F passes, for filters over every family of flags; and the segments that F
    /// passes together with those !F passes are every segment (each segment lies on a branch).
    /// </summary>
    [Fact]
    public void Gate_AFilterAndItsComplement_PartitionTheBranchesOfEveryRay()
    {
        var db = Trace(LensScene(), Split, 400).Database;
        Assert.Equal(400, db.Rays.Count);
        Assert.Contains(db.Rays, r => NscRayFilter.Branches(r.Segments).Count > 2);   // the trace does split
        var filters = new[]
        {
            "H2", "H2 & !G2", "(T2 | R2) ^ L3", "X_HITS(4,0)", "$000102#", "~000001002003", "X_SEGMENTS(2,4)", "M3 | Z",
            "O0 & W1", "G0", "X_GHOST(0,1)", "X_HIT(2,2)", "X_IRLT(3, 0.5)", "X_TIR(2,1) | X_GHOSTN(2,1,0)",
        };
        foreach (var text in filters)
        {
            var f = NscRayFilter.Parse(text)!;
            var not = NscRayFilter.Not(f);
            long passF = 0, passNot = 0;
            foreach (var ray in db.Rays)
            {
                var run = db.Runs[ray.Run];
                foreach (var b in NscRayFilter.Branches(ray.Segments))
                {
                    bool a = f.BranchPasses(ray, b, run), c = not.BranchPasses(ray, b, run);
                    Assert.True(a ^ c, $"\"{text}\": ray {ray.RayNumber}, branch ending at segment {b[^1]} passes {(a ? "both" : "neither")}.");
                    if (a) passF++; else passNot++;
                }
            }
            var withF = NscRayFilter.Select(db, f).ToDictionary(x => x.Ray.RayNumber, x => x.Segments);
            var withNot = NscRayFilter.Select(db, not).ToDictionary(x => x.Ray.RayNumber, x => x.Segments);
            foreach (var ray in db.Rays)
                for (int s = 0; s < ray.Segments.Count; s++)
                    Assert.True((withF.TryGetValue(ray.RayNumber, out var x) && x[s]) || (withNot.TryGetValue(ray.RayNumber, out var y) && y[s]),
                        $"\"{text}\": ray {ray.RayNumber}, segment {s} is on no passing branch of either.");
            Assert.True(passF + passNot > 0);
        }
        // Some filters must split the set non-trivially, or the test proves nothing.
        foreach (var text in new[] { "H2", "G0", "H2 & !G2" })
        {
            var f = NscRayFilter.Parse(text)!;
            int pass = db.Rays.Count(r => f.TreePasses(r, db.Runs[r.Run]));
            Assert.InRange(pass, 1, db.Rays.Count - (text == "H2" ? 1 : 0));
        }
    }

    /// <summary>
    /// Without splitting each tree is one branch, so F and !F split the detector hits exactly: the
    /// two playbacks' hit counts add up to the whole, and their fluxes add up to the whole to
    /// rounding. Each flux is a sum of positive terms, whose rounding error is at most (n − 1)·u
    /// times the sum (derived by induction: each addition of positives errs by at most u relative);
    /// three such sums with n ≤ N rays give |F + !F − all| ≤ 3·N·u·all.
    /// </summary>
    [Fact]
    public void Gate_AFilterAndItsComplement_SplitTheDetectorFlux_WithoutSplitting()
    {
        var scene = LensScene();
        const int n = 600;
        var db = Trace(scene, Plain, n).Database;
        var f = NscRayFilter.Parse("H2")!;
        var all = NscRayPlayback.Play(db, scene);
        var a = NscRayPlayback.Play(db, scene, f);
        var b = NscRayPlayback.Play(db, scene, NscRayFilter.Not(f));
        double u = Math.Pow(2, -53);
        for (int d = 0; d < all.Count; d++)
        {
            var (x, y, z) = ((NscFlatDetectorData)all[d], (NscFlatDetectorData)a[d], (NscFlatDetectorData)b[d]);
            Assert.Equal(x.SpatialHits, y.SpatialHits + z.SpatialHits);
            for (int k = 0; k < x.Flux.Length; k++)
                Assert.True(Math.Abs(y.Flux[k] + z.Flux[k] - x.Flux[k]) <= 3 * n * u * x.Flux[k],
                    $"detector {x.ObjectNumber}, pixel {k}: {y.Flux[k]:R} + {z.Flux[k]:R} against {x.Flux[k]:R}.");
        }
        Assert.True(((NscFlatDetectorData)a[1]).SpatialHits > 0 && ((NscFlatDetectorData)b[1]).SpatialHits > 0, "both halves reach the far detector");
    }

    // ================================================================= gate: bitwise playback

    private static void SameBits(NscDetectorData expected, NscDetectorData actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (var p in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var e = p.GetValue(expected);
            var a = p.GetValue(actual);
            switch (e)
            {
                case double[] de:
                    var da = Assert.IsType<double[]>(a);
                    Assert.Equal(de.Length, da.Length);
                    for (int i = 0; i < de.Length; i++)
                        Assert.True(BitConverter.DoubleToInt64Bits(de[i]) == BitConverter.DoubleToInt64Bits(da[i]),
                            $"{expected.TypeName} object {expected.ObjectNumber}, {p.Name}[{i}]: {de[i]:R} replayed as {da[i]:R}.");
                    break;
                case long or int or double or bool:
                    Assert.Equal(e, a);
                    break;
                case null:
                    if (p.PropertyType == typeof(double[])) Assert.Null(a);
                    break;
            }
        }
    }

    /// <summary>
    /// The second half of the gate: two wavelengths of a source traced with splitting and the
    /// polarization trace onto a transparent rectangle, a colour detector and a polar detector,
    /// saved in the OpenSim format, read back and played back, give every detector array to the
    /// bit (pixel fluxes, the per-ray squares of the noise estimate, coherent sums, colour and
    /// luminous data, hit counts and the launched-ray count). Recording does not change the trace:
    /// the ledger and the detectors with a recorder equal those without, to the bit.
    /// </summary>
    [Fact]
    public void Gate_ReplayingASavedTrace_ReproducesTheDetectorsBitwise()
    {
        var source = new NscSourcePoint(30, new NscSourceCommon { AnalysisRays = 700, Power = 2 });
        var scene = new NscScene
        {
            Objects = new NscObject[]
            {
                new() { Geometry = source },
                new() { Geometry = new NscDetectorRectangle(15 * Mm, 15 * Mm, 9, 7), Placement = new NscPlacement(Z: 10 * Mm) },
                Glass(new NscSphere(8 * Mm), 1.6, new NscPlacement(Z: 30 * Mm)),
                new() { Geometry = new NscDetectorColor(40 * Mm, 40 * Mm, 6, 6), Placement = new NscPlacement(Z: 70 * Mm), Material = NscMaterial.Absorb },
                new() { Geometry = new NscDetectorPolar(180, 0.5, 19, 24) },
            }
        };
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.5, 1), new NscSpectralLine(0.6, 2) } };
        var settings = Split with { Seed = 11 };
        var recorder = new NscRayRecorder();
        var traced = NscSourceRun.Run(scene, settings, options, recorder: recorder);
        var plain = NscSourceRun.Run(scene, settings, options);
        Assert.Equal(plain.Ledger, traced.Ledger with { DetectedByObject = plain.Ledger.DetectedByObject });
        Assert.Equal(plain.Ledger.DetectedByObject, traced.Ledger.DetectedByObject);
        for (int d = 0; d < plain.Detectors!.Count; d++) SameBits(plain.Detectors[d], traced.Detectors![d]);

        var db = recorder.Database;
        Assert.Equal(2, db.Runs.Count);
        Assert.Equal(new[] { 1, 2 }, db.Runs.Select(r => r.WavelengthNumber));
        Assert.All(db.Rays, r => Assert.Equal(1, r.SourceObject));
        Assert.Equal(traced.Ledger.Rays, db.Rays.Count);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zrd");
        try
        {
            NscZrdFile.Write(path, db, NscZrdFormat.OpenSimFull, LensUnit.Millimeter);
            Assert.Equal(NscZrdFormat.OpenSimFull, NscZrdFile.Detect(path));
            var back = NscZrdFile.Read(path, LensUnit.Millimeter);
            Assert.Equal(NscRayDatabaseOrigin.OpenSimFile, back.Origin);
            var replay = NscRayPlayback.Play(back, scene);
            Assert.Equal(traced.Detectors!.Count, replay.Count);
            for (int d = 0; d < replay.Count; d++) SameBits(traced.Detectors[d], replay[d]);

            var one = Assert.Single(NscRayPlayback.Play(back, scene, detector: 4));
            SameBits(traced.Detectors.Single(x => x.ObjectNumber == 4), one);

            // The file keeps every segment field to the bit.
            for (int k = 0; k < db.Rays.Count; k += 37)
                for (int s = 0; s < db.Rays[k].Segments.Count; s++)
                {
                    var (x, y) = (db.Rays[k].Segments[s], back.Rays[k].Segments[s]);
                    Assert.Equal(x with { Fate = "", DetectorHits = null }, y with { Fate = "", DetectorHits = null });
                    Assert.Equal(x.DetectorHits?.Count ?? 0, y.DetectorHits?.Count ?? 0);
                }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A filtered playback equals the playback of the subset that filter saves (both keep
    /// the same hits of the same rays in the same chunks), to the bit; and the recorder's save
    /// filter keeps exactly the trees that pass.</summary>
    [Fact]
    public void AFilteredPlayback_EqualsThePlaybackOfTheSavedSubset_AndTheSaveFilterKeepsPassingTrees()
    {
        var scene = LensScene();
        var db = Trace(scene, Split, 300).Database;
        var f = NscRayFilter.Parse("G2 | X_HITS(4,0)")!;
        var filtered = NscRayPlayback.Play(db, scene, f);
        var subset = NscZrdFile.Subset(db, f);
        Assert.True(subset.Rays.Count > 0 && subset.Rays.Count < db.Rays.Count);
        subset.Validate();
        var replay = NscRayPlayback.Play(subset, scene);
        for (int d = 0; d < filtered.Count; d++) SameBits(filtered[d], replay[d]);

        var saveFilter = NscRayFilter.Parse("H3")!;
        var recorder = new NscRayRecorder(saveFilter);
        Trace(scene, Split, 300, recorder);
        int expected = db.Rays.Count(r => saveFilter.TreePasses(r, db.Runs[r.Run]));
        Assert.Equal(expected, recorder.Database.Rays.Count);
        Assert.Equal(300 - expected, recorder.Rejected);
        Assert.Equal(300, recorder.Database.Runs.Single().LaunchedRays);

        var ranged = new NscRayRecorder(NscRayFilter.Parse("{#5-9} H3"));
        Trace(scene, Split, 300, ranged);
        Assert.Equal(5, ranged.Database.Rays.Count);
    }

    // ================================================================= UFD

    /// <summary>
    /// A UFD file follows the help's layout ("The ZRD Uncompressed Full Data Format (UFD)"): the
    /// version record 2003, the maximum segment count, then per ray its segment count and 232-byte
    /// records, so the file size is 8 + Σ(4 + 232·n) (derived from that layout); segment 0 holds
    /// the source object in hit_object, the wavelength number in storage and the wavelength in nx.
    /// Read back (lens units mm), the structure is the same, positions agree to the rounding of the
    /// mm conversion, and TIR comes back as a plain reflection (UFD has no TIR bit).
    /// </summary>
    [Fact]
    public void Ufd_FollowsTheHelpsLayout_AndReadsBack()
    {
        var source = new NscSourcePoint(25, new NscSourceCommon { AnalysisRays = 120 });
        var scene = new NscScene { Objects = new NscObject[] { new() { Geometry = source }, Glass(new NscSphere(8 * Mm), 1.6, new NscPlacement(Z: 30 * Mm)) } };
        var recorder = new NscRayRecorder();
        NscSourceRun.Run(scene, Split, new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } }, recorder: recorder);
        var db = recorder.Database;
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zrd");
        try
        {
            NscZrdFile.Write(path, db, NscZrdFormat.UncompressedFullData, LensUnit.Millimeter);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(8 + db.Rays.Sum(r => 4 + 232L * r.Segments.Count), bytes.Length);
            Assert.Equal(2003, BitConverter.ToInt32(bytes, 0));
            Assert.Equal(db.MaxSegments, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(db.Rays[0].Segments.Count, BitConverter.ToInt32(bytes, 8));
            int seg0 = 12;
            Assert.Equal(1, BitConverter.ToInt32(bytes, seg0 + 8));         // hit_object: the source
            Assert.Equal(1, BitConverter.ToInt32(bytes, seg0 + 28));        // storage: wavelength 1
            Assert.Equal(0.55, BitConverter.ToDouble(bytes, seg0 + 40 + 8 * 8));   // nx: the wavelength (µm)

            var back = NscZrdFile.Read(path, LensUnit.Millimeter);
            Assert.Equal(NscRayDatabaseOrigin.UfdFile, back.Origin);
            Assert.False(back.HasDetectorHits);
            Assert.Equal(db.Rays.Count, back.Rays.Count);
            Assert.Equal(0.55, back.Runs.Single().AirMicrometers);
            for (int k = 0; k < db.Rays.Count; k++)
            {
                Assert.Equal(1, back.Rays[k].SourceObject);
                for (int s = 0; s < db.Rays[k].Segments.Count; s++)
                {
                    var (x, y) = (db.Rays[k].Segments[s], back.Rays[k].Segments[s]);
                    Assert.Equal((x.Parent, x.Level, x.Inside, x.Hit, x.Face), (y.Parent, y.Level, y.Inside, y.Hit, y.Face));
                    var events = x.Events.HasFlag(NscEvents.TotalInternalReflection) ? (x.Events & ~NscEvents.TotalInternalReflection) | NscEvents.Reflected : x.Events;
                    Assert.Equal(events, y.Events);
                    // Meters → mm → meters: two roundings of relative size ≤ 2^-53 each (derived).
                    double scale = Math.Abs(x.Position.X) + Math.Abs(x.Position.Y) + Math.Abs(x.Position.Z);
                    Assert.True((x.Position - y.Position).Length <= 4 * Math.Pow(2, -53) * scale, $"ray {k}, segment {s} position");
                    Assert.Equal(x.Intensity, y.Intensity);
                    Assert.Equal(x.Direction, y.Direction);
                }
            }
            Assert.Contains("UFD", Assert.Throws<NotSupportedException>(() => NscRayPlayback.Play(back, LensScene())).Message);
            Assert.Throws<InvalidOperationException>(() => NscZrdFile.Write(new MemoryStream(), back, NscZrdFormat.OpenSimFull, LensUnit.Millimeter));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompressedAndUnknownZrdFiles_AreRefusedByName()
    {
        NscRayDatabase Read(int first)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(first));
            ms.Write(BitConverter.GetBytes(10));
            ms.Position = 0;
            return NscZrdFile.Read(ms, LensUnit.Millimeter, "t.zrd");
        }
        Assert.Contains("Compressed Basic Data", Assert.Throws<NotSupportedException>(() => Read(12003)).Message);   // 2003 + 10000 (help)
        Assert.Contains("Compressed Full Data", Assert.Throws<NotSupportedException>(() => Read(22003)).Message);    // 2003 + 20000 (help)
        Assert.Contains("2003", Assert.Throws<InvalidDataException>(() => Read(2002)).Message);
        var empty = new NscRayDatabase { Runs = Array.Empty<NscRayDatabaseRun>(), Rays = Array.Empty<NscRecordedRay>() };
        Assert.Contains("CBD", Assert.Throws<NotSupportedException>(() => NscZrdFile.Write(new MemoryStream(), empty, NscZrdFormat.CompressedBasicData, LensUnit.Millimeter)).Message);
        // A truncated UFD file names where it ends.
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(2003)); ms.Write(BitConverter.GetBytes(5)); ms.Write(BitConverter.GetBytes(3)); ms.Write(new byte[100]);
        ms.Position = 0;
        Assert.Contains("ray 0, segment 0", Assert.Throws<InvalidDataException>(() => NscZrdFile.Read(ms, LensUnit.Millimeter)).Message);
    }

    // ================================================================= flags on hand-built trees

    private static NscSegment Seg(int number, int parent, int level, int hit, int face, NscEvents events, double intensity = 1,
        Vector3D? position = null, int inside = 0) => new()
    {
        Number = number, Parent = parent, Level = level, Inside = inside, Hit = hit, Face = face,
        Position = position ?? Vector3D.Zero, Direction = Vector3D.UnitZ, Intensity = intensity, Index = 1, Events = events,
    };

    /// <summary>
    /// Source object 7 at wavelength number 2 (0.55 µm). Segment 1 hits object 3 face 1 and splits:
    /// segment 2 is the ghost (reflected) and ends on object 5; segment 3 is transmitted, ends on
    /// object 4 face 2 at local z = 1 mm (object 4 sits at z = 10 mm) with intensity 0.6, and is
    /// scattered there: segment 4 is the scattered child (F) ending on object 6, segment 5 the
    /// specular child that escapes. Branches: A = 0-1-2, B = 0-1-3-4, C = 0-1-3-5.
    /// </summary>
    private static (NscRayDatabase Db, NscRecordedRay Ray) HandBuilt()
    {
        var segs = new[]
        {
            Seg(0, -1, 0, 0, 0, NscEvents.None),
            Seg(1, 0, 1, 3, 1, NscEvents.Reflected | NscEvents.Transmitted, 0.9),
            Seg(2, 1, 2, 5, 0, NscEvents.Terminated | NscEvents.Ghost, 0.05),
            Seg(3, 1, 2, 4, 2, NscEvents.Scattered | NscEvents.Transmitted, 0.6, new Vector3D(0, 0, 11 * Mm)),
            Seg(4, 3, 3, 6, 0, NscEvents.Terminated | NscEvents.ScatteredFromParent, 0.2),
            Seg(5, 3, 3, 0, 0, NscEvents.Terminated, 0.4),
        };
        var ray = new NscRecordedRay { Run = 0, RayNumber = 0, SourceObject = 7, Segments = segs };
        var db = new NscRayDatabase { Runs = new[] { new NscRayDatabaseRun(0.55, 2, 1) }, Rays = new[] { ray } };
        return (db, ray);
    }

    /// <summary>Which of the branches A, B, C pass.</summary>
    private static string Passing(string filter, NscRayFilterFrame? frame = null, NscNamedFilters? named = null)
    {
        var (db, ray) = HandBuilt();
        var f = NscRayFilter.Parse(filter, named)!;
        var names = new[] { "A", "B", "C" };
        var branches = NscRayFilter.Branches(ray.Segments);
        Assert.Equal(3, branches.Count);
        return string.Concat(branches.Select((b, i) => f.BranchPasses(ray, b, db.Runs[0], frame) ? names[i] : ""));
    }

    [Theory]
    [InlineData("H5", "A")]
    [InlineData("M5", "BC")]
    [InlineData("L6", "B")]
    [InlineData("L4", "")]
    [InlineData("G3", "A")]
    [InlineData("G0", "A")]
    [InlineData("G3.1", "A")]
    [InlineData("G3.2", "")]
    [InlineData("F4", "B")]
    [InlineData("F0", "B")]
    [InlineData("F4.1", "")]
    [InlineData("S4", "BC")]
    [InlineData("S4.2", "BC")]
    [InlineData("S4.1", "")]
    [InlineData("T3", "ABC")]
    [InlineData("R3.1", "ABC")]
    [InlineData("H3.2", "")]
    [InlineData("O7", "ABC")]
    [InlineData("O8", "")]
    [InlineData("O0", "ABC")]
    [InlineData("W2", "ABC")]
    [InlineData("W1", "")]
    [InlineData("Z", "")]
    [InlineData("!H5", "BC")]
    [InlineData("H5 | F4", "AB")]
    [InlineData("H5 & F4", "")]
    [InlineData("H4 ^ H6", "C")]
    [InlineData("(H4 & !H6) | H5", "AC")]
    [InlineData("!(H4 | H5)", "")]
    [InlineData("h5", "A")]
    [InlineData("X_HIT(3,1)", "ABC")]
    [InlineData("X_HIT(4.2,1)", "BC")]
    [InlineData("X_HITFACE(4,2)", "BC")]
    [InlineData("X_HITFACE2(3,1,1)", "ABC")]
    [InlineData("X_HITS(3,0)", "B")]
    [InlineData("X_HITS(2,2)", "AC")]
    [InlineData("X_SEGMENTS(3,0)", "BC")]
    [InlineData("X_SEGMENTS(1,2)", "A")]
    [InlineData("X_SCATTER(0,1)", "B")]
    [InlineData("X_SCATTER(6,1)", "B")]
    [InlineData("X_SCATTER(5,1)", "")]
    [InlineData("X_SCATTERF(4,1)", "B")]
    [InlineData("X_SCATTERF(3,1)", "")]
    [InlineData("X_GHOST(0,1)", "A")]
    [InlineData("X_GHOST(5,1)", "A")]
    [InlineData("X_GHOST(0,0)", "BC")]
    [InlineData("X_GHOSTN(3,1,0)", "A")]
    [InlineData("X_IRGT(4.2, 0.5)", "BC")]
    [InlineData("X_IAGT(4, 0.7)", "")]
    [InlineData("X_IALT(5, 0.1)", "A")]
    [InlineData("X_WAVERANGE(3, 0.5, 0.6)", "ABC")]
    [InlineData("X_WAVERANGE(3, 0.56, 0.6)", "")]
    [InlineData("X_SEQ(7, 3.1, 4.2)", "C")]
    [InlineData("X_SEQ(7.0, 3, 4, 6)", "B")]
    [InlineData("$070304", "C")]
    [InlineData("$070304#", "BC")]
    [InlineData("~007003004006", "B")]
    [InlineData("$0703#", "ABC")]
    [InlineData("X_NDIF(0)", "ABC")]
    public void Flags_SelectTheBranchesTheHelpDescribes(string filter, string expected) =>
        Assert.Equal(expected, Passing(filter));

    /// <summary>The D, E and B flags (diffraction and bulk scatter, OP-82/OP-81) on a hand-built
    /// ray: D7 marks the segment that diffracted on object 7, E7 its child, B2 a bulk scatter inside
    /// object 2 (B0: inside any object).</summary>
    [Fact]
    public void DiffractionAndBulkScatterFlags_ReadTheirEvents()
    {
        var segs = new[]
        {
            Seg(0, -1, 0, 0, 0, NscEvents.None),
            Seg(1, 0, 1, 7, 1, NscEvents.Diffracted),
            Seg(2, 1, 2, 0, 0, NscEvents.BulkScattered, inside: 2),
            Seg(3, 2, 3, 9, 0, NscEvents.Terminated | NscEvents.DiffractedFromParent),
        };
        var ray = new NscRecordedRay { Run = 0, RayNumber = 0, SourceObject = 1, Segments = segs };
        var run = new NscRayDatabaseRun(0.5, 1, 1);
        bool P(string f) => NscRayFilter.Parse(f)!.BranchPasses(ray, NscRayFilter.Branches(segs)[0], run);
        Assert.True(P("D7"));
        Assert.False(P("D9"));
        Assert.True(P("B2") && P("B0"));
        Assert.False(P("B3"));
        Assert.True(P("X_NDIF(1)"));
        // E: the segment's parent hit object 7? Here segment 3's parent (segment 2) hit nothing.
        Assert.False(P("E7"));
        segs[3] = segs[3] with { Parent = 1 };
        var tree = new NscRecordedRay { Run = 0, RayNumber = 0, SourceObject = 1, Segments = new[] { segs[0], segs[1], segs[3] with { Number = 2 } } };
        Assert.True(NscRayFilter.Parse("E7")!.BranchPasses(tree, new[] { 0, 1, 2 }, run));
    }

    [Fact]
    public void LocalCoordinateFlags_UseTheObjectsFrame_InLensUnits()
    {
        var placements = Enumerable.Range(1, 6).Select(i => i == 4
            ? new NscPlacement(Z: 10 * Mm).ToTransform()
            : RigidTransform.Identity).ToArray();
        var frame = new NscRayFilterFrame(placements, LensUnit.Millimeter);
        Assert.Equal("BC", Passing("X_ZGT(4, 0.5)", frame));     // local z = 1 mm
        Assert.Equal("", Passing("X_ZGT(4, 1.5)", frame));
        Assert.Equal("BC", Passing("X_ZLT(4.2, 1.5)", frame));
        Assert.Equal("BC", Passing("X_NGT(4, 0.9)", frame));    // the segment runs along +z
        Assert.Equal("BC", Passing("X_AYZL(4, 1)", frame));     // 0° from +z
        Assert.Equal("BC", Passing("X_AXZL(4, 1)", frame));     // 0° from +z in the x-z plane
        Assert.Equal("", Passing("X_AXZG(4, 1)", frame));
        Assert.Contains("placements", Assert.Throws<ArgumentException>(() => Passing("X_ZGT(4, 0.5)")).Message);
    }

    [Fact]
    public void NamedFilters_ExpandWhereverFiltersAreUsed()
    {
        var named = NscNamedFilters.Parse(new[] { "MyFilter; H5 & H6 & M9", "", "Scat; F0 | S4", "Both; MyFilter | Scat" });
        Assert.Equal(3, named.Filters.Count);
        Assert.Equal("BC", Passing("Scat", named: named));
        Assert.Equal("BC", Passing("myfilter | scat", named: named));
        Assert.Equal("BC", Passing("Both", named: named));
        Assert.Equal("((H5 & H6 & M9) | (F0 | S4))", NscRayFilter.Parse("Both", named)!.Expanded);

        var cyclic = NscNamedFilters.Parse(new[] { "A1; B1x", "B1x; A1 | H3" });
        Assert.Contains("refers to itself", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("A1", cyclic)).Message);
        Assert.Contains("reads as a filter flag", Assert.Throws<ArgumentException>(() => NscNamedFilters.Parse(new[] { "H5; H3" })).Message);
        Assert.Contains("filtername; filterstring", Assert.Throws<ArgumentException>(() => NscNamedFilters.Parse(new[] { "NoSemicolon" })).Message);
        Assert.Contains("defined twice", Assert.Throws<ArgumentException>(() => NscNamedFilters.Parse(new[] { "Ab; H1", "AB; H2" })).Message);
    }

    [Theory]
    [InlineData("H1 & H2 | H3", "parentheses")]
    [InlineData("J3", "J flag")]
    [InlineData("_1", "OP-84")]
    [InlineData("SEQ[1:2]", "OP-84")]
    [InlineData("{@5}H1", "OP-84")]
    [InlineData("X_DIFF(1,1,1)", "OP-82")]
    [InlineData("X_SEQ(2,4.1/(2,*),4.2)", "OP-82")]
    [InlineData("X_WAVESHIFT(1,2)", "OP-81")]
    [InlineData("X_EXT(1,1)", "birefringent")]
    [InlineData("X_HIT(3)", "2 arguments")]
    [InlineData("H0", "1 or more")]
    [InlineData("(H1", "')'")]
    [InlineData("Q5", "not a filter flag")]
    [InlineData("H1 H2", "missing operator")]
    [InlineData("$123", "2-digit")]
    [InlineData("O3.1", "no face")]
    public void MalformedAndUnbuiltFilters_AreRefusedByName(string filter, string words) =>
        Assert.Contains(words, Assert.Throws<ArgumentException>(() => NscRayFilter.Parse(filter)).Message);

    [Fact]
    public void ARayRange_KeepsTheNthPassingTrees_AndHasNoComplement()
    {
        var (db, ray) = HandBuilt();
        var rays = Enumerable.Range(0, 6).Select(i => ray with
        {
            RayNumber = i,
            Segments = i % 2 == 0 ? ray.Segments : ray.Segments.Select(s => s with { Hit = s.Hit == 5 ? 8 : s.Hit }).ToArray(),
        }).ToArray();
        var many = db with { Runs = new[] { new NscRayDatabaseRun(0.55, 2, 6) }, Rays = rays };
        Assert.Equal(new long[] { 0, 2 }, NscRayFilter.Select(many, NscRayFilter.Parse("{#2}(H5)")).Select(x => x.Ray.RayNumber));
        Assert.Equal(new long[] { 2, 4 }, NscRayFilter.Select(many, NscRayFilter.Parse("{#2-3} H5")).Select(x => x.Ray.RayNumber));
        Assert.Equal(new long[] { 0, 1, 2 }, NscRayFilter.Select(many, NscRayFilter.Parse("{#3}")).Select(x => x.Ray.RayNumber));
        Assert.Throws<ArgumentException>(() => NscRayFilter.Not(NscRayFilter.Parse("{#2}H5")!));
        Assert.Null(NscRayFilter.Parse("  "));
    }

    // ================================================================= the analyses

    private static OpticsAnalysisContext SourceAndDetector() => new("P", new OpticsSettings
    {
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new OpticsNscObjectSettings { Type = "Source Point", Parameters = new() { 10, 500, 1, 0, 0, 40 } },
                new OpticsNscObjectSettings { Type = "Detector Rectangle", Z = 10, Material = "ABSORB", Parameters = new() { 5, 5, 10, 10 } },
            },
            NamedFilters = new() { "OnDet; H2" },
        },
    });

    [Fact]
    public void TheViewer_ListsAFile_ExpandsBranches_AndSavesTheSubset()
    {
        var scene = LensScene();
        var db = Trace(scene, Split, 20).Database;
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zrd");
        string subset = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zrd");
        try
        {
            NscZrdFile.Write(path, db, NscZrdFormat.OpenSimFull, LensUnit.Millimeter);
            var analysis = new NscRayDatabaseViewerAnalysis();
            var context = new OpticsAnalysisContext("P", new OpticsSettings());
            var all = analysis.Compute(context, new NscRayDatabaseViewerSettings { File = path, LastRay = 5 }, default);
            var table = all.Sections.Single(s => s.Heading == "Segments");
            Assert.Equal(db.Rays.Take(5).Sum(r => r.Segments.Count), table.TableRows.Count);

            var branches = analysis.Compute(context, new NscRayDatabaseViewerSettings { File = path, LastRay = 5, ExpandIntoBranches = true }, default);
            int expected = db.Rays.Take(5).Sum(r => NscRayFilter.Branches(r.Segments).Sum(b => b.Length));
            Assert.Equal(expected, branches.Sections.Single(s => s.Heading == "Segments").TableRows.Count);

            var saved = analysis.Compute(context, new NscRayDatabaseViewerSettings
            {
                File = path, LastRay = -1, ApplyFilter = true, Filter = "H3", SaveSubsetAs = subset,
            }, default);
            Assert.DoesNotContain(saved.Sections, s => s.Heading == "Segments");   // Last Ray −1 suppresses the listing
            var back = NscZrdFile.Read(subset, LensUnit.Millimeter);
            Assert.Equal(NscZrdFile.Subset(db, NscRayFilter.Parse("H3")).Rays.Count, back.Rays.Count);
            Assert.Contains("not built", Assert.Throws<NotSupportedException>(() =>
                analysis.Compute(context, new NscRayDatabaseViewerSettings { File = path, SaveRaysOnObject = 3 }, default)).Message);
            Assert.Contains("no non-sequential objects", Assert.Throws<ArgumentException>(() =>
                analysis.Compute(context, new NscRayDatabaseViewerSettings(), default)).Message);
        }
        finally
        {
            File.Delete(path);
            File.Delete(subset);
        }
    }

    [Fact]
    public void TheViewer_TracesTheProject_AndTheDetectorViewerPlaysTheSavedRaysBack()
    {
        var context = SourceAndDetector();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zrd");
        try
        {
            var listing = new NscRayDatabaseViewerAnalysis().Compute(context, new NscRayDatabaseViewerSettings { SaveRaysAs = path, LastRay = 2 }, default);
            Assert.Contains("saved to", listing.Text);
            var traced = new NscDetectorViewerAnalysis().Compute(context, new NscDetectorViewerSettings(), default);
            var played = new NscDetectorViewerAnalysis().Compute(context, new NscDetectorViewerSettings { PlaybackZrd = path }, default);
            Assert.Contains("played back", played.Text);
            foreach (var (a, b) in traced.Sections.Zip(played.Sections))
            {
                Assert.Equal(a.Pairs, b.Pairs);
                Assert.Equal(a.TableRows.Select(r => string.Join("|", r)), b.TableRows.Select(r => string.Join("|", r)));
            }
            // The named filter "OnDet" (H2) passes every ray that reaches the detector, so the flux is the same.
            var named = new NscDetectorViewerAnalysis().Compute(context, new NscDetectorViewerSettings { PlaybackZrd = path, PlaybackFilter = "OnDet" }, default);
            Assert.Equal(traced.Sections[0].Pairs, named.Sections[0].Pairs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheViewer_IsRegistered()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.Contains(registry.All, a => a.Id == NscRayDatabaseViewerAnalysis.SavedId);
    }
}
