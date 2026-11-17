using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.NonSequential.StrayLight;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-84: the stray-light tools (docs/Optics-Conventions.md §78): Path Analysis, path filters _n,
/// the Sequence Selector's sequences and SEQ[a:b], the CRS file, the Critical Ray Tracer and
/// Critical Rayset Generator, and reverse radiance. Gate: path energies sum to the detector total
/// (the same hits); forward and backward estimates of one detector pixel agree within their
/// stated error bars. Geometry and powers are arbitrary test inputs; every expected value is
/// derived here (closed forms, independent sums, a quadrature), never typed in.
/// </summary>
public sealed class NscStrayLightTests
{
    private const double Mm = 1e-3;

    /// <summary>The unit roundoff of doubles, 2⁻⁵³.</summary>
    private static readonly double U = Math.Pow(2, -53);

    private static NscObject Glass(NscGeometry g, double n, NscPlacement placement = default) => new()
    {
        Geometry = g, Material = NscMaterial.Of(ParaxialMedium.Constant(n)), Placement = placement,
    };

    /// <summary>A transparent detector (1), a glass sphere (2) and an absorbing detector (3) on the z axis.</summary>
    private static NscScene LensScene() => new()
    {
        Objects = new NscObject[]
        {
            new() { Geometry = new NscDetectorRectangle(20 * Mm, 20 * Mm, 8, 8), Placement = new NscPlacement(Z: 10 * Mm) },
            Glass(new NscSphere(8 * Mm), 1.6, new NscPlacement(Z: 30 * Mm)),
            new() { Geometry = new NscDetectorRectangle(30 * Mm, 30 * Mm, 12, 12), Placement = new NscPlacement(Z: 60 * Mm), Material = NscMaterial.Absorb },
        }
    };

    private static readonly NscTraceSettings Split = new() { UsePolarization = true, Splitting = NscSplitting.Split };

    private static (NscRunResult Result, NscRayDatabase Database) Trace(NscScene scene, NscTraceSettings settings, int rays)
    {
        var launch = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 25, rays, 1, NscSampling.Random, 5);
        var rec = new NscRayRecorder();
        rec.PrepareRun(Enumerable.Repeat(9, rays).ToArray(), 1);   // every ray from "source object 9" (X_SEQ refuses object 0)
        var result = new NscTracer(scene, 0.55, settings).Run(launch, rec);
        return (result, rec.Database);
    }

    private static double CompensatedSum(IEnumerable<double> xs)
    {
        double s = 0, c = 0;
        foreach (double x in xs)
        {
            double t = s + x;
            c += Math.Abs(s) >= Math.Abs(x) ? (s - t) + x : (x - t) + s;
            s = t;
        }
        return s + c;
    }

    // ================================================================= gate: path energies

    /// <summary>
    /// The first half of the gate. Path Analysis by detector groups every hit a split trace left on
    /// the absorbing detector; the paths and the uncategorized sum add up to an independent
    /// compensated sum of the same hits (both compensated: they agree to a few roundoffs of the
    /// total, bound 4u·total) and to the detector's own total flux (a plain sum of n positive
    /// terms in the tracer's chunk order, which errs by at most (n − 1)u of the total; derived by
    /// induction on the additions; bound 2n·u·total). Relative Minimum Flux and the path limit only
    /// move branches into the uncategorized sum, so the identity holds with them too.
    /// </summary>
    [Fact]
    public void Gate_PathEnergies_SumToTheDetectorTotal()
    {
        var scene = LensScene();
        var (result, db) = Trace(scene, Split, 500);
        var hits = db.Rays.SelectMany(r => r.Segments).SelectMany(s => s.DetectorHits ?? Array.Empty<NscDetectorHit>())
            .Where(h => h.ObjectNumber == 3).Select(h => h.Power).ToList();
        double independent = CompensatedSum(hits);
        double detectorTotal = ((NscFlatDetectorData)result.Detectors!.Single(d => d.ObjectNumber == 3)).Flux.Sum();
        Assert.True(hits.Count > 400);
        foreach (var options in new[]
        {
            new NscPathAnalysisOptions { Detector = 3 },
            new NscPathAnalysisOptions { Detector = 3, RelativeMinimumFlux = 0.05 },
            new NscPathAnalysisOptions { Detector = 3, MaxPaths = 2, Sort = NscPathSort.Branches },
        })
        {
            var r = NscPathAnalysis.Analyse(db, options);
            double sum = CompensatedSum(r.Paths.Select(p => p.Flux).Append(r.Uncategorized));
            Assert.Equal(hits.Count, r.Branches);
            Assert.True(Math.Abs(sum - independent) <= 4 * U * independent, $"paths {sum} vs hits {independent}");
            Assert.True(Math.Abs(r.Total - independent) <= 4 * U * independent);
            Assert.True(Math.Abs(sum - detectorTotal) <= 2 * hits.Count * U * detectorTotal, $"paths {sum} vs detector {detectorTotal}");
            Assert.All(r.Paths, p => Assert.True(p.Sequence.Prefix));
        }
        // With more than one path the split trace really has several (ghosts off the sphere).
        Assert.True(NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Detector = 3 }).Paths.Count > 2);
    }

    /// <summary>
    /// Branch mode (the help's analysis) with the filter L3 (the branch ends on the absorbing
    /// detector) groups the same energy: every hit on an absorbing detector ends a branch, whose
    /// last segment's intensity is the hit's power. And each path's filter _n, played back onto
    /// the detector, gives that path's flux (same hits; sums in a different order, bound 2n·u).
    /// </summary>
    [Fact]
    public void PathFilters_SelectEachPathsHits_AndBranchModeAgreesOnAnAbsorbingDetector()
    {
        var scene = LensScene();
        var (result, db) = Trace(scene, Split, 300);
        double total = ((NscFlatDetectorData)result.Detectors!.Single(d => d.ObjectNumber == 3)).Flux.Sum();
        var branch = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Filter = NscRayFilter.Parse("L3") });
        long n = branch.Branches;
        Assert.True(Math.Abs(branch.Total - total) <= 2 * n * U * total);
        Assert.All(branch.Paths, p => Assert.Equal(3, p.LastObject));

        var byDetector = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Detector = 3 });
        var named = NscNamedFilters.Empty.WithStrayLight(byDetector.Paths.Select(p => p.Sequence).ToArray(), null);
        foreach (var p in byDetector.Paths.Take(4))
        {
            var filter = NscRayFilter.Parse($"_{p.Number}", named);
            var played = (NscFlatDetectorData)NscRayPlayback.Play(db, scene, filter, 3).Single();
            double flux = played.Flux.Sum();
            Assert.True(Math.Abs(flux - p.Flux) <= 2 * p.Branches * U * p.Flux + 1e-300, $"_{p.Number}: playback {flux} vs path {p.Flux}");
        }
        // Branch-mode paths are exact sequences: _n equals X_SEQ of the same entries on every branch.
        var exact = branch.Paths.First().Sequence;
        var viaNamed = NscRayFilter.Parse("_1", NscNamedFilters.Empty.WithStrayLight(new[] { exact }, null))!;
        var viaXSeq = NscRayFilter.Parse(exact.XSeq)!;
        foreach (var ray in db.Rays)
            foreach (var b in NscRayFilter.Branches(ray.Segments))
                Assert.Equal(viaXSeq.BranchPasses(ray, b, db.Runs[ray.Run]), viaNamed.BranchPasses(ray, b, db.Runs[ray.Run]));
    }

    // ================================================================= paths and sequences by hand

    private static NscSegment Seg(int n, int parent, int hit, int face, double intensity, double opl = 0) => new()
    {
        Number = n, Parent = parent, Level = parent + 1, Inside = 0, Hit = hit, Face = face,
        Position = Vector3D.Zero, Direction = Vector3D.UnitZ, Intensity = intensity, Index = 1, OpticalPath = opl,
    };

    private static NscRecordedRay Ray(long number, int source, params NscSegment[] segments) =>
        new() { Run = 0, RayNumber = number, SourceObject = source, Segments = segments };

    private static NscRayDatabase Db(params NscRecordedRay[] rays) =>
        new() { Runs = new[] { new NscRayDatabaseRun(0.55, 1, rays.Length) }, Rays = rays };

    [Fact]
    public void PathAnalysis_GroupsBranches_SortsAndCollectsTheUncategorized()
    {
        // Ray 0: source 1 → 2.1 → 2.2 → 4.0 (end 0.5) ; ray 1: the same path (end 0.3);
        // ray 2: source 1 → 2.1 then splits: 4.0 (end 0.2) and a miss (end 0.01).
        var db = Db(
            Ray(0, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 2, 2, 0.9), Seg(3, 2, 4, 0, 0.5, 30)),
            Ray(1, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 2, 2, 0.9), Seg(3, 2, 4, 0, 0.3, 10)),
            Ray(2, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 4, 0, 0.2), Seg(3, 1, 0, 0, 0.01)));
        var r = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions());
        Assert.Equal(3, r.Paths.Count);
        Assert.Equal("1.0, 2.1, 2.2, 4.0", r.Paths[0].Sequence.Text);
        Assert.Equal(0.5 + 0.3, r.Paths[0].Flux, 15);
        Assert.Equal(2, r.Paths[0].Branches);
        Assert.Equal((0.5 + 0.3) / 3 * 100, r.Paths[0].RelativePercent, 12);   // flux in: 3 rays of 1
        Assert.Equal(20, r.Paths[0].AverageOpticalPath, 12);                     // (30 + 10) / 2
        Assert.Equal(2, r.Paths[0].UniqueObjects);
        Assert.Equal(3, r.Paths[0].ObjectsStruck);
        Assert.Equal("1.0, 2.1, 4.0", r.Paths[1].Sequence.Text);
        Assert.Equal("1.0, 2.1", r.Paths[2].Sequence.Text);   // the miss is not an entry

        // Relative Minimum Flux 0.1 sends the 0.01 branch to the uncategorized sum.
        var faint = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { RelativeMinimumFlux = 0.1 });
        Assert.Equal(2, faint.Paths.Count);
        Assert.Equal(0.01, faint.Uncategorized, 15);
        Assert.Equal(1, faint.UncategorizedBranches);

        // Sorting by objects struck puts the shortest first; by last object, the smallest number (2) first.
        Assert.Equal("1.0, 2.1", NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Sort = NscPathSort.ObjectsStruck }).Paths[0].Sequence.Text);
        Assert.Equal("1.0, 2.1", NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Sort = NscPathSort.LastObject }).Paths[0].Sequence.Text);

        // First/last ray and the filter.
        var one = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { FirstRay = 3, LastRay = 3 });
        Assert.Equal(1, one.RaysRead);
        Assert.Equal(2, one.Paths.Count);
        var only4 = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Filter = NscRayFilter.Parse("L4") });
        Assert.Equal(2, only4.Paths.Count);

        // The limit on distinct paths: later paths go to the uncategorized sum.
        var limited = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { MaxPaths = 1 });
        Assert.Single(limited.Paths);
        Assert.Equal(0.21, limited.Uncategorized, 15);
    }

    [Fact]
    public void PathSequences_ParseFormatMatchAndOverflow()
    {
        var p = NscPathSequence.Parse("1.0, 2.1, 2.2, 5.0, 4.0");
        Assert.Equal("1.0, 2.1, 2.2, 5.0, 4.0", p.Text);
        Assert.Equal("X_SEQ(1.0,2.1,2.2,5.0,4.0)", p.XSeq);
        Assert.True(NscPathSequence.Parse(p.XSeq).SameAs(p));
        var prefix = NscPathSequence.Parse("1.0, 2 #");
        Assert.True(prefix.Prefix);
        Assert.Null(prefix.Steps[1].Face);
        Assert.Equal("1.0, 2 #", prefix.Text);
        Assert.True(NscPathSequence.Parse("> 100").Overflow);
        Assert.Contains("OP-82", Assert.Throws<FormatException>(() => NscPathSequence.Parse("2, 4.1/(2,*), 4.2")).Message);
        Assert.Throws<FormatException>(() => NscPathSequence.Parse("1.0, x"));
        Assert.Throws<FormatException>(() => NscPathSequence.Parse(""));

        var ray = Ray(0, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 2, 2, 1), Seg(3, 2, 0, 0, 1));
        int[] branch = { 0, 1, 2, 3 };
        Assert.True(NscPathSequence.Parse("1.0, 2.1, 2.2").Matches(ray, branch));
        Assert.False(NscPathSequence.Parse("1.0, 2.1").Matches(ray, branch));
        Assert.True(NscPathSequence.Parse("1.0, 2.1 #").Matches(ray, branch));
        Assert.True(NscPathSequence.Parse("1, 2, 2").Matches(ray, branch));    // no face: any face

        // A branch with more than 100 entries is the overflow path.
        var segs = new List<NscSegment> { Seg(0, -1, 0, 0, 1) };
        for (int i = 1; i <= NscPathSequence.MaxEntries; i++) segs.Add(Seg(i, i - 1, 2, i % 2 + 1, 1));
        var longRay = Ray(0, 1, segs.ToArray());
        var longBranch = Enumerable.Range(0, segs.Count).ToArray();
        Assert.True(NscPathSequence.Of(longRay, longBranch).Overflow);
        Assert.True(NscPathSequence.OverflowPath.Matches(longRay, longBranch));
        Assert.False(NscPathSequence.OverflowPath.Matches(ray, branch));
    }

    [Theory]
    [InlineData("Xpath_2", "starts with X")]
    [InlineData("A1", "single letter")]
    [InlineData("h123", "single letter")]
    [InlineData("Z", "reserved")]
    [InlineData("SEQ", "SEQ[a:b]")]
    [InlineData("1abc", "must start with a letter")]
    [InlineData("Path-3", "must start with a letter")]
    public void SequenceNames_FollowTheHelpsRules(string name, string words) =>
        Assert.Contains(words, NscSequenceNames.Problem(name));

    [Theory]
    [InlineData("Path_3")]
    [InlineData("Z_1")]
    [InlineData("SEQ3")]
    [InlineData("ghost")]
    public void SequenceNames_AcceptTheHelpsExamples(string name) => Assert.Null(NscSequenceNames.Problem(name));

    [Fact]
    public void Sequences_NumberTheNextFreeSeq_SkipDuplicates_AndFilterByNameAndRange()
    {
        var a = NscPathSequence.Parse("1.0, 2.1, 2.2, 4.0");
        var b = NscPathSequence.Parse("1.0, 2.1, 4.0");
        var c = NscPathSequence.Parse("1.0, 2.1");
        var existing = new List<NscSequence> { new("SEQ2", a), new("Ghost", b) };
        var list = NscSequenceNames.Append(existing, new[] { a, c, c }, out int skipped);
        Assert.Equal(2, skipped);
        Assert.Equal(new[] { "SEQ2", "Ghost", "SEQ1" }, list.Select(s => s.Name));   // the next available number is 1
        Assert.Equal("SEQ3", NscSequenceNames.NextDefault(list));

        // The text form round-trips, check boxes included.
        var text = NscSequenceNames.Format(list.Select((s, i) => i == 1 ? s with { Enabled = false } : s).ToList());
        var back = NscSequenceNames.ParseText(text);
        Assert.False(back[1].Enabled);
        Assert.Equal(list.Select(s => s.Path.Text), back.Select(s => s.Path.Text));
        Assert.Contains("duplicate", Assert.Throws<ArgumentException>(() => NscSequenceNames.ParseText("S1a; 1.0, 2.1\nS2a; 1.0, 2.1")).Message);

        // By name (any case), SEQ[a:b] and SEQ[:] select the paths' branches.
        var named = NscNamedFilters.Empty.WithStrayLight(null, list);
        var db = Db(
            Ray(0, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 2, 2, 0.9), Seg(3, 2, 4, 0, 0.5)),
            Ray(1, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1), Seg(2, 1, 4, 0, 0.2)),
            Ray(2, 1, Seg(0, -1, 0, 0, 1), Seg(1, 0, 2, 1, 1)));
        long[] Pass(string f) => NscRayFilter.Select(db, NscRayFilter.Parse(f, named)).Select(x => x.Ray.RayNumber).ToArray();
        Assert.Equal(new long[] { 0 }, Pass("SEQ2"));
        Assert.Equal(new long[] { 1 }, Pass("ghost"));
        Assert.Equal(new long[] { 2 }, Pass("SEQ1"));
        Assert.Equal(new long[] { 0, 1 }, Pass("SEQ[1:2]"));
        Assert.Equal(new long[] { 0, 1, 2 }, Pass("SEQ[:]"));
        Assert.Equal(new long[] { 1, 2 }, Pass("SEQ[2:]"));
        Assert.Equal(new long[] { 0 }, Pass("SEQ[:1]"));
        Assert.Equal(new long[] { 1, 2 }, Pass("!SEQ2"));
        Assert.Contains("numbered 1 to 3", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("SEQ[2:4]", named)).Message);
        Assert.Contains("must not be below", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("SEQ[3:2]", named)).Message);
        Assert.Contains("is also a named filter", Assert.Throws<ArgumentException>(() =>
            NscNamedFilters.Parse(new[] { "Ghost; H2" }).WithStrayLight(null, list)).Message);

        // More than 100 sequences: a range of 101 is refused (the help returns nothing for it).
        var many = Enumerable.Range(1, 101).Select(i => new NscSequence($"SEQ{i}", NscPathSequence.Parse($"1.0, {i}.0"))).ToList();
        var manyNamed = NscNamedFilters.Empty.WithStrayLight(null, many);
        Assert.Contains("fewer than 100", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("SEQ[:]", manyNamed)).Message);
        Assert.NotNull(NscRayFilter.Parse("SEQ[2:101]", manyNamed));
    }

    [Fact]
    public void PathFilterNumbers_AreRefusedWithALeadingZeroOrWhenUndefined()
    {
        var named = NscNamedFilters.Empty.WithStrayLight(new[] { NscPathSequence.Parse("1.0, 2.0") }, null);
        Assert.NotNull(NscRayFilter.Parse("_1 & H2", named));
        Assert.Contains("leading zero", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("_01", named)).Message);
        Assert.Contains("_1 to _1", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("_2", named)).Message);
        Assert.Contains("for example _1", Assert.Throws<ArgumentException>(() => NscRayFilter.Parse("_", named)).Message);
    }

    [Fact]
    public void Generators_TakeChosenPathFilters_AndRefuseWithoutThem()
    {
        var paths = Enumerable.Range(2, 12).Select(i => NscPathSequence.Parse($"1.0, {i}.0")).ToArray();
        Assert.Equal(12, NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.AllPaths).Count);
        Assert.Equal(3, NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.First3Paths).Count);
        Assert.Equal(5, NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.First5Paths).Count);
        Assert.Equal(10, NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.First10Paths).Count);
        Assert.Same(paths[6], NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.OnePath, 7).Single());
        Assert.Contains("_1 to _12", Assert.Throws<ArgumentException>(() => NscSequenceGenerator.FromPathFilters(paths, NscSequencePaths.OnePath, 13)).Message);
        Assert.Contains("Generate Path Filters", Assert.Throws<ArgumentException>(() =>
            NscSequenceGenerator.FromPathFilters(Array.Empty<NscPathSequence>(), NscSequencePaths.AllPaths)).Message);
    }

    // ================================================================= the analyses on a project

    /// <summary>A point source (object 1, 40° cone) and an absorbing detector (object 2) 10 mm
    /// away (half width 5 mm, so part of the cone misses it); lengths in mm, the default unit.</summary>
    private static OpticsAnalysisContext SourceAndDetector(OpticsNonSequentialSettings? extra = null) => new("P", new OpticsSettings
    {
        NonSequential = (extra ?? new OpticsNonSequentialSettings()) with
        {
            Objects = new()
            {
                new OpticsNscObjectSettings { Type = "Source Point", Parameters = new() { 10, 500, 1, 0, 0, 40 } },
                new OpticsNscObjectSettings { Type = "Detector Rectangle", Z = 10, Material = "ABSORB", Parameters = new() { 5, 5, 10, 10 } },
            },
        },
    });

    [Fact]
    public void TheAnalyses_RunOnAProject_AndTheSavedListsReachEveryFilter()
    {
        var context = SourceAndDetector();
        var path = new NscPathAnalysisAnalysis().Compute(context, new NscPathAnalysisSettings { Detector = 2, GeneratePathFilters = true }, default);
        Assert.Contains("1.0, 2.0 #", path.Text);
        Assert.Contains(path.Sections, s => s.Heading == "Path filters");
        var branch = new NscPathAnalysisAnalysis().Compute(context, new NscPathAnalysisSettings(), default);
        Assert.Contains("1.0, 2.0", branch.Text);
        Assert.Contains("is not a detector", Assert.Throws<ArgumentException>(() =>
            new NscPathAnalysisAnalysis().Compute(context, new NscPathAnalysisSettings { Detector = 1 }, default)).Message);

        // Straight-through: one sequence for the one source, the path that ends on the detector.
        var (list, added, _, _) = NscSequenceSelectorAnalysis.Generate(context, new NscSequenceSelectorSettings());
        Assert.Equal(1, added);
        Assert.Equal("SEQ1", list.Single().Name);
        Assert.Equal("1.0, 2.0", list.Single().Path.Text);
        Assert.Contains("SEQ1", new NscSequenceSelectorAnalysis().Compute(context, new NscSequenceSelectorSettings(), default).Text);
        Assert.Contains("PAF", Assert.Throws<NotSupportedException>(() => NscSequenceSelectorAnalysis.Generate(context,
            new NscSequenceSelectorSettings { Method = NscSequenceMethod.RayDatabase, File = "C:\\x.PAF" })).Message);

        // Saved path filters and sequences are seen by every filter parsed with the project's names.
        var saved = SourceAndDetector(new OpticsNonSequentialSettings
        {
            PathFilters = new() { "1.0, 2.0" },
            Sequences = new() { new OpticsNscSequenceSettings { Name = "OnDetector", Sequence = "1.0, 2.0" } },
        });
        var named = NscRayDatabaseProject.NamedFilters(saved);
        Assert.Single(named.PathFilters);
        Assert.Single(named.Sequences);
        Assert.NotNull(NscRayDatabaseProject.Filter(saved, "_1 & OnDetector & SEQ[1:1]"));
        var played = new NscDetectorViewerAnalysis().Compute(saved, new NscDetectorViewerSettings(), default);
        Assert.NotNull(played);
    }

    // ================================================================= CRS files and the Critical Ray Tracer

    private static NscCriticalRayset Rayset(params NscCriticalRay[] rays) => new()
    {
        Rays = rays, UnitsFlag = 4, Date = 20261116, Pattern = NscRaysetPattern.ChiefAndMarginals,
        TargetRotation = new NscPlacement().ToTransform(),
    };

    private static NscCriticalRay CRay(Vector3D start, Vector3D dir, Vector3D end, Vector3D endDir, double wave = 0.55,
        NscCriticalRayFlag flag = NscCriticalRayFlag.Chief) => new(start, dir, wave, 1, 1, flag, end, endDir);

    [Fact]
    public void CrsFiles_RoundTrip_ReadTheHelpsUnits_AndRefuseMalformedRows()
    {
        var set = Rayset(
            CRay(new Vector3D(1 * Mm, 2 * Mm, -5 * Mm), Vector3D.UnitZ, new Vector3D(0.5 * Mm, 0.25 * Mm, 50 * Mm), Vector3D.UnitZ),
            CRay(Vector3D.Zero, new Vector3D(0, 0.6, 0.8), new Vector3D(0, 30 * Mm, 40 * Mm), new Vector3D(0, 0.6, 0.8), 0.65, NscCriticalRayFlag.Marginal));
        var text = set.Format(4);
        var back = NscCriticalRayset.Parse(text.Split('\n'));
        Assert.Equal(2, back.Rays.Count);
        Assert.Equal(4, back.UnitsFlag);
        Assert.Equal(NscCriticalRayFlag.Marginal, back.Rays[1].Flag);
        Assert.Equal(set.Rays[0].Start.X, back.Rays[0].Start.X, 15);
        Assert.Equal(set.Rays[1].End.Y, back.Rays[1].End.Y, 15);

        // The same rays in inches: values scale by 1 mm / 25.4 mm (derived from the inch factor).
        var inches = NscCriticalRayset.Parse(set.Format(1).Split('\n'));
        Assert.Equal(set.Rays[0].End.Z, inches.Rays[0].End.Z, 12);

        // A hand-made file: no column line, comments, commas, a mirror-space header with four tilts.
        var hand = NscCriticalRayset.Parse(new[]
        {
            "! hand made",
            "1, 0, 20261116, 8, 180, 0, 0, 0",
            "0 0 0 0 0 1 0.55 1 1 0 0 0 1 0 0 1",
        });
        Assert.Equal(1, hand.Rays[0].End.Z, 15);                         // meters (units flag 0)
        Assert.Equal(-1, hand.TargetRotation.ApplyDirection(Vector3D.UnitZ).Z, 12);   // turned 180° about x

        Assert.Contains("16 numbers", Assert.Throws<InvalidDataException>(() => NscCriticalRayset.Parse(new[] { "1 4 20261116 8 0 0 0", "0 0 0 0 0 1 0.55 1 1 0 0 0 1 0 0" })).Message);
        Assert.Contains("says 2 rays", Assert.Throws<InvalidDataException>(() => NscCriticalRayset.Parse(new[] { "2 4 20261116 8 0 0 0", "0 0 0 0 0 1 0.55 1 1 0 0 0 1 0 0 1" })).Message);
        Assert.Contains("units flag 7", Assert.Throws<InvalidDataException>(() => NscCriticalRayset.Parse(new[] { "1 7 20261116 8 0 0 0" })).Message);
        Assert.Contains("ray flag", Assert.Throws<InvalidDataException>(() => NscCriticalRayset.Parse(new[] { "1 4 20261116 8 0 0 0", "0 0 0 0 0 1 0.55 1 1 9 0 0 1 0 0 1" })).Message);
    }

    [Fact]
    public void TheCriticalRayTracer_PassesRaysOnTarget_AndFailsBlockedOrMisplacedOnes()
    {
        // An absorbing rectangle off the axis (x = 20 mm, half width 5 mm) at z = 20 mm.
        var scene = new NscScene
        {
            Objects = new NscObject[]
            {
                new() { Geometry = new NscRectangle(5 * Mm, 5 * Mm), Placement = new NscPlacement(X: 20 * Mm, Z: 20 * Mm), Material = NscMaterial.Absorb },
            }
        };
        var tilted = new Vector3D(0, Math.Sin(0.01), Math.Cos(0.01));
        var set = Rayset(
            CRay(Vector3D.Zero, Vector3D.UnitZ, new Vector3D(0, 0, 50 * Mm), Vector3D.UnitZ),                         // on target
            CRay(new Vector3D(20 * Mm, 0, 0), Vector3D.UnitZ, new Vector3D(20 * Mm, 0, 50 * Mm), Vector3D.UnitZ),     // blocked by object 1
            CRay(Vector3D.Zero, Vector3D.UnitZ, new Vector3D(1 * Mm, 0, 50 * Mm), Vector3D.UnitZ),                    // 1 mm off
            CRay(Vector3D.Zero, Vector3D.UnitZ, new Vector3D(0, 0, 50 * Mm), tilted));                               // 0.57° off
        var outcomes = NscCriticalRayTracer.Trace(scene, new NscTraceSettings(), set, new[] { 0.55 }, 1e-3 * Mm, 0.01);
        Assert.True(outcomes[0].Pass);
        Assert.Equal(-1, outcomes[0].TerminationObject);
        Assert.False(outcomes[1].Pass);
        Assert.Equal(1, outcomes[1].TerminationObject);
        Assert.False(outcomes[2].Pass);
        Assert.Equal(1 * Mm, outcomes[2].PositionError, 12);
        Assert.False(outcomes[3].Pass);
        Assert.Equal(0.01 * 180 / Math.PI, outcomes[3].AngleErrorDegrees, 9);
        Assert.Contains("matches no system wavelength", Assert.Throws<ArgumentException>(() =>
            NscCriticalRayTracer.Trace(scene, new NscTraceSettings(), set, new[] { 0.65 }, 1e-6, 0.01)).Message);

        var report = NscCriticalRayTracerAnalysis.Report(outcomes, new NscCriticalRayTracerSettings { RaysToDisplay = NscCriticalRaysShown.Fail }, LensUnit.Millimeter, set);
        Assert.Equal(3, report[0].TableRows.Count);
        Assert.Contains("25 % of the rays pass", report[0].Lines[0]);
    }

    // ================================================================= the Critical Rayset Generator

    private static SequentialLens Singlet() => CadExportFixtures.Singlet();

    private static OpticalSystemData FloatingAimed() => OpticalSystemData.Default with
    {
        Aperture = new Aperture { Type = ApertureType.FloatByStopSize },
        RayAiming = new RayAiming { Mode = RayAimingMode.Paraxial },
    };

    [Fact]
    public void TheGenerator_TracesChiefAndMarginals_FromTheDummyPlane_ToTheImage()
    {
        var notes = new List<string>();
        double d = 5 * Mm;
        var set = NscCriticalRaysetGenerator.Generate(Singlet(), FloatingAimed(), new NscCriticalRaysetOptions { EffectiveInputDistance = d, PrimaryOnly = true }, 1, notes);
        Assert.Equal(5, set.Rays.Count);
        // The singlet's stop is surface 1 (semi-diameter 12.5 mm); the on-axis field's rays are
        // parallel to z, so a marginal ray starts at the stop height scaled by the help's margin,
        // 1 − 50·g/R with g = 1e-6 lens units (derived), on the plane z = −d.
        double r = CadExportFixtures.Semi;
        double scale = 1 - 50 * 1e-6 * Mm / r;
        var chief = set.Rays[0];
        Assert.Equal(NscCriticalRayFlag.Chief, chief.Flag);
        Assert.Equal(-d, chief.Start.Z, 15);
        Assert.Equal(1, chief.StartDirection.Z, 15);
        Assert.Equal(0, chief.End.Y, 12);
        var top = set.Rays[1];
        Assert.Equal(NscCriticalRayFlag.Marginal, top.Flag);
        Assert.Equal(scale * r, top.Start.Y, 12);
        Assert.Equal(-d, top.Start.Z, 12);
        // Symmetric marginals land symmetrically.
        Assert.Equal(-set.Rays[1].End.Y, set.Rays[2].End.Y, 12);
        Assert.Equal(-set.Rays[3].End.X, set.Rays[4].End.X, 12);

        // Written and read back.
        var back = NscCriticalRayset.Parse(set.Format(set.UnitsFlag).Split('\n'));
        Assert.Equal(set.Rays[1].End.Y, back.Rays[1].End.Y, 15);
    }

    [Fact]
    public void TheGenerator_RefusesWhatTheHelpRefuses_AndLaysOutThePatterns()
    {
        var notes = new List<string>();
        var lens = Singlet();
        Assert.Contains("aperture type", Assert.Throws<ArgumentException>(() => NscCriticalRaysetGenerator.Generate(lens,
            FloatingAimed() with { Aperture = new Aperture() }, new NscCriticalRaysetOptions(), 1, notes)).Message);
        Assert.Contains("ray aiming is off", Assert.Throws<ArgumentException>(() => NscCriticalRaysetGenerator.Generate(lens,
            FloatingAimed() with { RayAiming = new RayAiming() }, new NscCriticalRaysetOptions(), 1, notes)).Message);
        Assert.Contains("configurations", Assert.Throws<ArgumentException>(() => NscCriticalRaysetGenerator.Generate(lens,
            FloatingAimed(), new NscCriticalRaysetOptions(), 2, notes)).Message);
        Assert.Throws<NotSupportedException>(() => NscCriticalRaysetGenerator.Generate(lens,
            FloatingAimed(), new NscCriticalRaysetOptions { Pattern = NscRaysetPattern.List }, 1, notes));

        var xy = NscCriticalRaysetGenerator.Pupil(new NscCriticalRaysetOptions { Pattern = NscRaysetPattern.XyFan, Rays = 5 });
        Assert.Equal(9, xy.Count);                                            // 5 along x, 4 more along y
        Assert.Single(xy, p => p.Flag == NscCriticalRayFlag.Chief);
        Assert.Equal(4, xy.Count(p => p.Flag == NscCriticalRayFlag.Marginal));
        var ring = NscCriticalRaysetGenerator.Pupil(new NscCriticalRaysetOptions { Pattern = NscRaysetPattern.ChiefAndRing, Rays = 5 });
        Assert.Equal(4, ring.Count(p => p.Flag == NscCriticalRayFlag.Ring));  // "N − 1 rays in the ring"
        var grid = NscCriticalRaysetGenerator.Pupil(new NscCriticalRaysetOptions { Pattern = NscRaysetPattern.Grid, Rays = 3 });
        Assert.Equal(5, grid.Count);                                          // the corners lie outside the unit circle
    }

    // ================================================================= gate: reverse radiance

    private static readonly NscSourceEmitOptions Emission = new() { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } };

    /// <summary>A Lambertian Source Rectangle (half widths 5 mm, 1 W, Cn = 1) at z = 0 facing +z
    /// and a 4 × 4 pixel Detector Rectangle (half width 4 mm) at z = 20 mm, optionally with a
    /// glass sphere between.</summary>
    private static NscScene RadianceScene(int forwardRays, bool sphere, bool frontOnly)
    {
        var objects = new List<NscObject>
        {
            new() { Geometry = new NscSourceRectangle(5 * Mm, 5 * Mm, cosineExponent: 1, common: new NscSourceCommon { AnalysisRays = forwardRays, Power = 1 }) },
        };
        if (sphere) objects.Add(Glass(new NscSphere(3 * Mm), 1.5, new NscPlacement(Z: 10 * Mm)));
        objects.Add(new() { Geometry = new NscDetectorRectangle(4 * Mm, 4 * Mm, 4, 4, frontOnly: frontOnly), Placement = new NscPlacement(Z: 20 * Mm) });
        return new NscScene { Objects = objects };
    }

    /// <summary>
    /// The second half of the gate, with no lens: the backward estimate of pixel (3, 3) (x and y
    /// from 0 to 2 mm) equals the flux a closed form gives, Φ = L ∫∫ cos θ_s cos θ_d / r² dA_s dA_d
    /// with L = P/(π·A_s) for a Lambertian source and cos θ_s = cos θ_d = D/r between parallel
    /// planes D apart (derived), integrated by the midpoint rule on 200 × 200 source and 8 × 8
    /// pixel cells (error O(h²) with h/D ≤ 2.5·10⁻³, under 10⁻⁴ relative); within 4 standard errors
    /// of the backward estimate plus that bound. The forward trace agrees within 4 combined
    /// standard errors (the forward one is the textbook σ/√N, which overstates Sobol's error).
    /// </summary>
    [Fact, Trait("Category", "Slow")]
    public void Gate_ForwardAndBackwardEstimates_OfOnePixel_Agree_WithoutALens()
    {
        var scene = RadianceScene(400_000, sphere: false, frontOnly: true);
        var settings = new NscTraceSettings();
        var back = NscReverseRadiance.Estimate(scene, settings, new NscReverseRadianceOptions
        {
            Detector = 2, PixelX = 3, PixelY = 3, Rays = 100_000, Emission = Emission,
        });

        double d = 20 * Mm, a = 5 * Mm, area = 4 * a * a, l = 1 / (Math.PI * area);
        int ns = 200, np = 8;
        double hs = 2 * a / ns, hp = 2 * Mm / np, sum = 0;
        for (int i = 0; i < np; i++)
            for (int j = 0; j < np; j++)
            {
                double x = (i + 0.5) * hp, y = (j + 0.5) * hp, inner = 0;
                for (int p = 0; p < ns; p++)
                    for (int q = 0; q < ns; q++)
                    {
                        double sx = -a + (p + 0.5) * hs - x, sy = -a + (q + 0.5) * hs - y;
                        double r2 = sx * sx + sy * sy + d * d;
                        inner += d * d / (r2 * r2);
                    }
                sum += inner * hs * hs;
            }
        double exact = l * sum * hp * hp;
        Assert.True(Math.Abs(back.Flux - exact) <= 4 * back.StandardError + 1e-4 * exact,
            $"backward {back.Flux} ± {back.StandardError} vs closed form {exact}");
        Assert.True(back.StandardError < 0.02 * back.Flux);   // the test resolves the flux to a few percent

        var forward = NscSourceRun.Run(scene, settings, Emission);
        var data = (NscFlatDetectorData)forward.Detectors!.Single();
        int k = data.Index(2, 2);
        double f = data.Flux[k], se = data.FluxStandardError(k);
        Assert.True(Math.Abs(back.Flux - f) <= 4 * Math.Sqrt(se * se + back.StandardError * back.StandardError),
            $"backward {back.Flux} ± {back.StandardError} vs forward {f} ± {se}");
    }

    /// <summary>The gate through a glass sphere with the polarization trace (Fresnel transmission
    /// at each face, the same both ways), detector receiving from both sides: forward and backward
    /// agree within 4 combined standard errors.</summary>
    [Fact, Trait("Category", "Slow")]
    public void Gate_ForwardAndBackwardEstimates_OfOnePixel_Agree_ThroughAGlassSphere()
    {
        var scene = RadianceScene(400_000, sphere: true, frontOnly: false);
        var settings = new NscTraceSettings { UsePolarization = true };
        var back = NscReverseRadiance.Estimate(scene, settings, new NscReverseRadianceOptions
        {
            Detector = 3, PixelX = 3, PixelY = 3, Rays = 100_000, Emission = Emission,
        });
        var forward = NscSourceRun.Run(scene, settings, Emission);
        var data = (NscFlatDetectorData)forward.Detectors!.Single();
        int k = data.Index(2, 2);
        double f = data.Flux[k], se = data.FluxStandardError(k);
        Assert.True(f > 0 && back.Flux > 0);
        Assert.True(Math.Abs(back.Flux - f) <= 4 * Math.Sqrt(se * se + back.StandardError * back.StandardError),
            $"backward {back.Flux} ± {back.StandardError} vs forward {f} ± {se}");
    }

    [Fact]
    public void ReverseRadiance_RefusesWhatItCannotModel()
    {
        var settings = new NscTraceSettings();
        var opts = new NscReverseRadianceOptions { Detector = 2, PixelX = 1, PixelY = 1, Rays = 10, Emission = Emission };
        Assert.Contains("scattering", Assert.Throws<NotSupportedException>(() =>
            NscReverseRadiance.Estimate(RadianceScene(10, false, false), settings with { ScatterRays = true }, opts)).Message);
        Assert.Contains("outside", Assert.Throws<ArgumentException>(() =>
            NscReverseRadiance.Estimate(RadianceScene(10, false, false), settings, opts with { PixelX = 5 })).Message);
        Assert.Contains("not a Detector Rectangle", Assert.Throws<ArgumentException>(() =>
            NscReverseRadiance.Estimate(RadianceScene(10, false, false), settings, opts with { Detector = 1 })).Message);
        var point = new NscScene
        {
            Objects = new NscObject[]
            {
                new() { Geometry = new NscSourceRectangle(5 * Mm, 5 * Mm, sourceDistance: 10 * Mm) },
                new() { Geometry = new NscDetectorRectangle(4 * Mm, 4 * Mm, 4, 4), Placement = new NscPlacement(Z: 20 * Mm) },
            }
        };
        Assert.Contains("probability zero", Assert.Throws<NotSupportedException>(() => NscReverseRadiance.Estimate(point, settings, opts)).Message);
    }
}
