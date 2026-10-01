using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Meshing.Audit;

namespace OpenSim.Meshing;

/// <summary>Overrides for the audit's derived thresholds.</summary>
public sealed record MeshAuditOptions
{
    /// <summary>T_D [m]: how far the mesh skin and the geometry may sit from each other.
    /// Null takes <see cref="MeshAudit.DistanceFactor"/>·h.</summary>
    public double? DistanceTolerance { get; init; }

    /// <summary>
    /// Leave the sub-resolution guard (A0) out. For a mesher that conforms to the geometry
    /// by construction — the structured lattice lays exact planes at any cell size, so
    /// "thinner than two cells" is not a feature it can lose.
    /// </summary>
    public bool SkipResolutionGuard { get; init; }
}

/// <summary>
/// The post-mesh audit: does this mesh represent this geometry, and could a mesh at this
/// edge length represent it at all?
/// <para>
/// The Delaunay mesher has no boundary recovery — its skin is whatever tetrahedron faces
/// survive centroid clipping — so a concavity narrower than about one element is bridged, a
/// wall thinner than about half of one becomes a hole, and two bodies a hair apart mesh as
/// one. None of that shows in the volume (two unit cubes 0.005 apart weld at 1.9997 of 2.0).
/// A conforming mesher is the cure; until then the audit is what stops a mesh that is not
/// the geometry from reaching a solver. Every failed check is fatal; nothing here repairs.
/// </para>
/// <para>
/// The checks, in order (A0 looks at the geometry alone and runs before any mesh exists):
/// A0 no feature below 2·h · A1 element validity · A2 skin manifoldness · A3 skin closure
/// and orientation · A4 volume per material region · A5 face-id coverage · A6 connectivity
/// against material regions · A7 skin → geometry distance · A8 geometry → skin coverage ·
/// A9 shell-by-shell correspondence (count, nesting, genus) · A10 local thickness and
/// clearance by ray casting.
/// </para>
/// </summary>
public sealed class MeshAudit
{
    /// <summary>
    /// A0: the smallest wall or gap, in units of h, the mesher is asked to carry. Fixed, not
    /// derived: it is the coarsest ratio at which the clip-and-cull pipeline has any margin
    /// (walls under ≈ 0.45·h lose their interior points, concavities under ≈ h are bridged),
    /// rounded up to one memorable number.
    /// </summary>
    public const double ResolutionFactor = 2.0;

    /// <summary>
    /// T_D / h: how far, in element edge lengths, the skin and the geometry may sit apart.
    /// Derived, not chosen — twice the worst that sound meshes of the threshold fixtures
    /// show (<c>MeshAuditIntegrationTests.Thresholds_AreTwiceWhatTheFixturesShow</c>):
    /// unit cube 0.001·h (the surface jitter), 48-facet cylinder 0.03·h, 13-facet cylinder
    /// 0.119·h. The last one sets it. Thirteen facets is the coarsest faceting that face
    /// detection still reads as one smooth face (27.7° between facets, under the 30° crease
    /// angle), so its edges are not feature edges, the mesher does not hug them, and the
    /// skin cuts each by up to (h/2)·tan(θ/2). That is the mesher's stated chord model, not
    /// a defect, and a tolerance below it would refuse coarsely faceted geometry at every
    /// edge length.
    /// </summary>
    public const double DistanceFactor = 0.24;

    /// <summary>T_V = <see cref="VolumeFactor"/>·h·A/V + <see cref="VolumeFloor"/>, relative:
    /// a skin displaced by a fraction of h over the wetted area A. The factor is twice the
    /// worst of the same fixtures (0.0058, the small 32-facet cylinder).</summary>
    public const double VolumeFactor = 0.012;
    public const double VolumeFloor = 1e-6;

    /// <summary>T_A: relative boundary-area error allowed per geometric face id — twice
    /// the worst of the same fixtures (0.0086, the caps of the 48-facet cylinder).</summary>
    public const double AreaTolerance = 0.02;

    /// <summary>Faces meeting at no more than this are one smooth sheet — the crease angle
    /// face detection uses.</summary>
    private const double CreaseAngleDegrees = 30;
    private static readonly double CosCrease = Math.Cos(CreaseAngleDegrees * Math.PI / 180);

    /// <summary>Failures are counted in full but only the first few are described.</summary>
    private const int DescribedFailures = 3;

    private readonly TriangleMesh _source;
    private readonly AuditSurface _geometry;
    private readonly double _h;
    private readonly double _distanceTolerance;
    private readonly double _epsilon;
    private readonly MeshAuditOptions _options;

    // Smooth sheets of the geometry and how they neighbour each other.
    private readonly int[] _sheetOf;
    private readonly int _sheetCount;
    private readonly HashSet<long> _adjacentSheets = new();
    private readonly List<(Vector3D A, Vector3D B)> _creases = new();

    private readonly List<Sample> _samples = new();
    private readonly List<string> _notes = new();

    /// <summary>A geometry surface point, strictly inside one triangle, with what a ray
    /// along its normal meets in each direction.</summary>
    private struct Sample
    {
        public Vector3D Position;
        public int Triangle;
        /// <summary>Solid thickness: first exit of the inward ray (∞ if none).</summary>
        public double Thickness;
        public int ThicknessTriangle;
        /// <summary>Void clearance: first re-entry of the outward ray (∞ if none).</summary>
        public double Clearance;
        public int ClearanceTriangle;
        /// <summary>The hit is the other face of a wedge this sample sits on, not a feature
        /// with a size — see <see cref="IsWedge"/>.</summary>
        public bool ThicknessIsWedge, ClearanceIsWedge;
    }

    /// <summary>A0, evaluated on the geometry alone.</summary>
    public MeshAuditCheck Resolution { get; }

    /// <summary>The edge length the audit was set up for.</summary>
    public double EdgeLength => _h;

    /// <summary>T_D in metres.</summary>
    public double DistanceTolerance => _distanceTolerance;

    /// <summary>How many hit/miss disagreements between a geometry ray and its mesh ray the
    /// last skin comparison settled by closest approach — silhouettes, not failures.</summary>
    internal int ResolvedSilhouettes { get; private set; }

    /// <summary>How many geometry surface samples the audit uses.</summary>
    internal int SampleCount => _samples.Count;

    internal AuditSurface Geometry => _geometry;

    private MeshAudit(TriangleMesh geometry, double h, MeshAuditOptions options)
    {
        if (!(h > 0) || !double.IsFinite(h))
            throw new ArgumentOutOfRangeException(nameof(h), "The audit needs a positive edge length.");
        _source = geometry;
        _h = h;
        _options = options;
        _distanceTolerance = options.DistanceTolerance ?? DistanceFactor * h;
        _geometry = new AuditSurface(geometry.Vertices,
            geometry.Triangles.Select(t => (t.A, t.B, t.C)).ToArray());
        _epsilon = 1e-9 * _geometry.Bvh.Diagonal;

        _sheetOf = BuildSheets(out _sheetCount);
        BuildSamples();
        Resolution = options.SkipResolutionGuard
            ? new MeshAuditCheck("A0", "feature resolution", MeshAuditOutcome.Skipped,
                "this mesher conforms to the geometry at any cell size")
            : CheckResolution();
    }

    /// <summary>
    /// Analyses the geometry and evaluates A0. Nothing is thrown here: call
    /// <see cref="ThrowIfUnresolved"/> to refuse before meshing, then <see cref="Check"/>
    /// on the finished mesh.
    /// </summary>
    public static MeshAudit Begin(TriangleMesh geometry, double edgeLength, MeshAuditOptions? options = null) =>
        new(geometry, edgeLength, options ?? new MeshAuditOptions());

    /// <summary>Audits a finished mesh against its geometry; throws
    /// <see cref="MeshAuditException"/> if any check fails.</summary>
    public static MeshAuditReport Check(FeMesh mesh, TriangleMesh geometry, double edgeLength,
        MeshAuditOptions? options = null) =>
        Begin(geometry, edgeLength, options).Check(mesh);

    /// <summary>Refuses, before any meshing work, a geometry with a feature the mesh could
    /// not carry at this edge length.</summary>
    public void ThrowIfUnresolved()
    {
        if (Resolution.Outcome == MeshAuditOutcome.Failed)
            throw new MeshAuditException(new MeshAuditReport(_h, new[] { Resolution }, _notes.ToArray()));
    }

    /// <summary>A remark every report from this audit will carry - how its edge length
    /// was arrived at, say. Not a failure.</summary>
    public void AddNote(string note) => _notes.Add(note);

    /// <summary>All checks; throws <see cref="MeshAuditException"/> if any fails.</summary>
    public MeshAuditReport Check(FeMesh mesh)
    {
        var report = Evaluate(mesh);
        if (!report.Passed) throw new MeshAuditException(report);
        return report;
    }

    // =====================================================================  geometry

    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Smooth sheets: geometry triangles joined across every edge that is not a crease.
    /// A sheet is a property of the geometry, not of how finely it happens to be
    /// triangulated — a plane is one sheet at any tessellation — and the two walls of
    /// anything bounded by creases are different sheets.
    /// </summary>
    private int[] BuildSheets(out int sheetCount)
    {
        int n = _geometry.Triangles.Length;
        var parent = new int[n];
        for (int t = 0; t < n; t++) parent[t] = t;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        var normals = _geometry.Outward;
        foreach (var (_, pair) in _geometry.EdgeTriangles)
        {
            if (Vector3D.Dot(normals[pair.T0], normals[pair.T1]) < CosCrease) continue;
            int ra = Find(pair.T0), rb = Find(pair.T1);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        var sheetOf = new int[n];
        var index = new Dictionary<int, int>();
        for (int t = 0; t < n; t++)
        {
            int root = Find(t);
            if (!index.TryGetValue(root, out int s)) index[root] = s = index.Count;
            sheetOf[t] = s;
        }
        sheetCount = index.Count;

        foreach (var (edge, pair) in _geometry.EdgeTriangles)
        {
            int s0 = sheetOf[pair.T0], s1 = sheetOf[pair.T1];
            if (s0 == s1) continue;
            _adjacentSheets.Add(PairKey(s0, s1));
            _creases.Add((_geometry.Vertices[edge.Item1], _geometry.Vertices[edge.Item2]));
        }
        return sheetOf;
    }

    /// <summary>
    /// The sample set: every vertex and every centroid of the surface refined until no edge
    /// is longer than h, plus the midpoint of every refined edge longer than h/2 —
    /// exhaustive over the refined surface rather than spaced along it, so a face smaller
    /// than the spacing still gets interior samples. Each sample is pulled a thousandth of
    /// the way into one triangle, which gives it one unambiguous normal; a point on a
    /// crease is sampled once per sheet meeting there, each with that sheet's normal, never
    /// with an averaged one.
    /// <para>
    /// Refinement is by bisecting the longest edge, not by the mesher's uniform k × k split:
    /// the samples are only ever used one at a time, so nothing needs the pieces to conform,
    /// and a long thin facet (a cylinder's side, a tessellated fillet) then costs its length
    /// over h rather than that squared.
    /// </para>
    /// </summary>
    private void BuildSamples()
    {
        const double nudge = 1e-3;
        var seen = new HashSet<(long, long, long, int)>();
        double quantum = Math.Max(1e-9 * _geometry.Bvh.Diagonal, double.Epsilon);
        var pending = new Stack<(Vector3D A, Vector3D B, Vector3D C)>();

        void Add(Vector3D exact, Vector3D toward, int triangle, bool shared)
        {
            if (shared && !seen.Add(((long)Math.Round(exact.X / quantum), (long)Math.Round(exact.Y / quantum),
                    (long)Math.Round(exact.Z / quantum), _sheetOf[triangle])))
                return;
            _samples.Add(new Sample { Position = exact + (toward - exact) * nudge, Triangle = triangle });
        }

        for (int t = 0; t < _geometry.Triangles.Length; t++)
        {
            if (!(_geometry.TriangleAreas[t] > 0)) continue;
            pending.Push(_geometry.Bvh.Corners(t));
            while (pending.Count > 0)
            {
                var (a, b, c) = pending.Pop();
                double ab = (b - a).Length, bc = (c - b).Length, ca = (a - c).Length;
                double longest = Math.Max(ab, Math.Max(bc, ca));
                if (longest > _h)
                {
                    // Bisect the longest edge; rotate so that edge is a-b.
                    if (bc == longest) (a, b, c, ab, bc, ca) = (b, c, a, bc, ca, ab);
                    else if (ca == longest) (a, b, c, ab, bc, ca) = (c, a, b, ca, ab, bc);
                    var mid = (a + b) / 2;
                    pending.Push((a, mid, c));
                    pending.Push((mid, b, c));
                    continue;
                }

                var centre = (a + b + c) / 3.0;
                Add(centre, centre, t, shared: false);
                Add(a, centre, t, shared: true);
                Add(b, centre, t, shared: true);
                Add(c, centre, t, shared: true);
                if (ab > _h / 2) Add((a + b) / 2, centre, t, shared: true);
                if (bc > _h / 2) Add((b + c) / 2, centre, t, shared: true);
                if (ca > _h / 2) Add((c + a) / 2, centre, t, shared: true);
            }
        }

        // What each sample's normal ray meets, through the geometry itself.
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_samples);
        for (int s = 0; s < span.Length; s++)
        {
            ref var sample = ref span[s];
            var n = _geometry.Outward[sample.Triangle];
            if (_geometry.Bvh.FirstHit(sample.Position, -n, _epsilon, out var inward))
            {
                sample.Thickness = inward.T;
                sample.ThicknessTriangle = inward.Triangle;
                sample.ThicknessIsWedge = IsWedge(sample.Triangle, inward.Triangle);
            }
            else
            {
                sample.Thickness = double.PositiveInfinity;
                sample.ThicknessTriangle = -1;
            }
            if (_geometry.Bvh.FirstHit(sample.Position, n, _epsilon, out var outward))
            {
                sample.Clearance = outward.T;
                sample.ClearanceTriangle = outward.Triangle;
                sample.ClearanceIsWedge = IsWedge(sample.Triangle, outward.Triangle);
            }
            else
            {
                sample.Clearance = double.PositiveInfinity;
                sample.ClearanceTriangle = -1;
            }
        }
    }

    /// <summary>
    /// Whether a normal ray from one face has struck the OTHER FACE OF A WEDGE rather than
    /// the far side of a wall or gap: the two faces are the same sheet or meet along a
    /// crease, and they are not within the crease angle of facing each other.
    /// <para>
    /// A wall, rib, tab, slot, bore or gap has a size — two faces that oppose each other —
    /// and that size is what A0 holds to 2·h. Where two faces meet at an angle the distance
    /// between them runs to zero at the edge whatever h is; that is a sharp edge, not a
    /// small feature, and counting it would refuse every prism, cone and V-notch at every
    /// edge length. Faces within the crease angle of antiparallel ALWAYS count, adjacent
    /// or not, so a blade thinner than a 30° wedge is still refused.
    /// </para>
    /// </summary>
    private bool IsWedge(int sampleTriangle, int hitTriangle)
    {
        if (Vector3D.Dot(_geometry.Outward[sampleTriangle], _geometry.Outward[hitTriangle]) < -CosCrease)
            return false;
        int s0 = _sheetOf[sampleTriangle], s1 = _sheetOf[hitTriangle];
        return s0 == s1 || _adjacentSheets.Contains(PairKey(s0, s1));
    }

    /// <summary>Thickness and clearance as a feature size: a wedge's other face has none.</summary>
    private static double FeatureSize(double distance, bool isWedge) =>
        isWedge ? double.PositiveInfinity : distance;

    /// <summary>A0: every wall and every gap is at least 2·h across.</summary>
    private MeshAuditCheck CheckResolution()
    {
        double limit = ResolutionFactor * _h;
        double threshold = limit * (1 - 1e-6);
        int violations = 0;
        double worst = double.PositiveInfinity;
        Vector3D worstAt = default;
        bool worstIsThickness = true;

        foreach (var sample in _samples)
        {
            double thickness = FeatureSize(sample.Thickness, sample.ThicknessIsWedge);
            double clearance = FeatureSize(sample.Clearance, sample.ClearanceIsWedge);
            if (thickness >= threshold && clearance >= threshold) continue;
            violations++;
            double least = Math.Min(thickness, clearance);
            if (least < worst)
            {
                worst = least;
                worstAt = sample.Position;
                worstIsThickness = thickness <= clearance;
            }
        }

        if (violations == 0)
        {
            double smallest = double.PositiveInfinity;
            foreach (var sample in _samples)
                smallest = Math.Min(smallest, Math.Min(FeatureSize(sample.Thickness, sample.ThicknessIsWedge),
                    FeatureSize(sample.Clearance, sample.ClearanceIsWedge)));
            return new MeshAuditCheck("A0", "feature resolution", MeshAuditOutcome.Passed,
                double.IsFinite(smallest) ? $"smallest wall or gap {smallest:g4} m ≥ 2·h = {limit:g4} m" : "",
                double.IsFinite(smallest) ? smallest : null, limit);
        }

        return new MeshAuditCheck("A0", "feature resolution", MeshAuditOutcome.Failed,
            $"feature below mesh resolution near {Format(worstAt)}: " +
            $"{(worstIsThickness ? "thickness" : "clearance")} = {worst:g4} m < 2·h = {limit:g4} m " +
            $"({violations:N0} of {_samples.Count:N0} surface samples); reduce h to at most {worst / ResolutionFactor:g3} m " +
            "or accept the simplification",
            worst, limit, worstAt);
    }

    // =====================================================================  mesh

    /// <summary>Every check, as a report; nothing is thrown.</summary>
    public MeshAuditReport Evaluate(FeMesh mesh)
    {
        var checks = new List<MeshAuditCheck> { Resolution };
        var warnings = new List<string>();

        var elements = new ElementView(mesh);
        var faceUse = elements.BuildFaceUse();

        checks.Add(CheckElements(mesh, elements, faceUse));
        bool elementsValid = checks[^1].Outcome == MeshAuditOutcome.Passed;

        // The skin as a surface in its own right.
        var skinTriangles = mesh.BoundaryTriangles.Select(t => (t.A, t.B, t.C)).ToArray();
        if (skinTriangles.Length == 0)
        {
            checks.Add(new MeshAuditCheck("A2", "skin manifoldness", MeshAuditOutcome.Failed,
                "the mesh has no boundary skin"));
            return new MeshAuditReport(_h, checks, warnings.Concat(_notes).ToArray());
        }
        var skin = new AuditSurface(mesh.Nodes, skinTriangles);

        checks.Add(CheckManifold(skin));
        bool manifold = checks[^1].Outcome == MeshAuditOutcome.Passed;

        checks.Add(CheckClosure(mesh, elements, faceUse, skin));
        bool closed = checks[^1].Outcome == MeshAuditOutcome.Passed;

        // Which geometry triangle each skin triangle sits on, and how far off it.
        var match = MatchSkin(skin);

        var connectivity = CheckConnectivity(mesh, elements, faceUse, skin, match, out var componentRegion,
            out var componentVolume);
        checks.Add(CheckVolume(mesh, componentRegion, componentVolume));
        checks.Add(CheckFaceCoverage(mesh, skin));
        checks.Add(connectivity);

        AddSkinChecks(checks, skin, match, manifold && closed && elementsValid);
        return new MeshAuditReport(_h, checks, warnings.Concat(_notes).ToArray());
    }

    /// <summary>
    /// A7–A10 on a bare closed surface standing in for a mesh skin. The comparison between
    /// a geometry and a skin does not need the elements behind the skin, and the fixtures
    /// that pin A10's behaviour are far easier to state as two surfaces.
    /// </summary>
    internal MeshAuditReport EvaluateSkin(IReadOnlyList<Vector3D> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        var checks = new List<MeshAuditCheck> { Resolution };
        var skin = new AuditSurface(vertices, triangles);
        checks.Add(CheckManifold(skin));
        bool manifold = checks[^1].Outcome == MeshAuditOutcome.Passed;
        AddSkinChecks(checks, skin, MatchSkin(skin), manifold);
        return new MeshAuditReport(_h, checks, _notes.ToArray());
    }

    private void AddSkinChecks(List<MeshAuditCheck> checks, AuditSurface skin, SkinMatch match, bool skinIsSound)
    {
        checks.Add(CheckSkinDistance(skin, match));
        var nearest = new Vector3D[_samples.Count];
        var covered = new bool[_samples.Count];
        checks.Add(CheckCoverage(skin, nearest, covered));
        if (skinIsSound)
        {
            checks.Add(CheckShells(skin, match));
            checks.Add(CheckLocalThickness(skin, nearest));
        }
        else
        {
            const string why = "needs a closed, manifold, outward-wound skin";
            checks.Add(new MeshAuditCheck("A9", "shell correspondence", MeshAuditOutcome.Skipped, why));
            checks.Add(new MeshAuditCheck("A10", "local thickness and clearance", MeshAuditOutcome.Skipped, why));
        }
    }

    // ---------------------------------------------------------------- A1

    /// <summary>A1: every element positively oriented; no face shared by more than two.</summary>
    private MeshAuditCheck CheckElements(FeMesh mesh, ElementView elements, Dictionary<FaceKey, FaceUse> faceUse)
    {
        int nonPositive = 0, firstBad = -1;
        for (int e = 0; e < elements.Count; e++)
        {
            double volume = mesh.ElementVolume(e);
            if (volume > 0 && double.IsFinite(volume)) continue;
            if (nonPositive++ == 0) firstBad = e;
        }
        int overused = 0;
        FaceKey firstOverused = default;
        foreach (var (key, use) in faceUse)
        {
            if (use.Count <= 2) continue;
            if (overused++ == 0) firstOverused = key;
        }

        if (nonPositive == 0 && overused == 0)
            return new MeshAuditCheck("A1", "element validity", MeshAuditOutcome.Passed,
                $"{elements.Count:N0} elements");

        var parts = new List<string>();
        Vector3D? at = null;
        if (nonPositive > 0)
        {
            at = elements.Centroid(firstBad);
            parts.Add($"{nonPositive:N0} element(s) with non-positive volume, first near {Format(at.Value)}");
        }
        if (overused > 0)
        {
            var where = mesh.Nodes[firstOverused.A >= 0 ? firstOverused.A : firstOverused.B];
            at ??= where;
            parts.Add($"{overused:N0} face(s) shared by more than two elements, first near {Format(where)}");
        }
        return new MeshAuditCheck("A1", "element validity", MeshAuditOutcome.Failed,
            string.Join("; ", parts), Location: at);
    }

    // ---------------------------------------------------------------- A2

    /// <summary>A2: every skin edge in exactly two skin triangles; every skin vertex's
    /// triangles form one fan.</summary>
    private static MeshAuditCheck CheckManifold(AuditSurface skin)
    {
        // Vertex fans: the triangles at a vertex, joined through the edges at that vertex,
        // must be one connected set. Two fans touching at a point are a pinch.
        var incident = new Dictionary<int, List<int>>();
        for (int t = 0; t < skin.Triangles.Length; t++)
        {
            var (a, b, c) = skin.Triangles[t];
            foreach (int v in new[] { a, b, c })
            {
                if (!incident.TryGetValue(v, out var list)) incident[v] = list = new List<int>(6);
                list.Add(t);
            }
        }
        int pinched = 0;
        int firstPinched = -1;
        var viaNeighbour = new Dictionary<int, int>();
        foreach (var (v, list) in incident)
        {
            if (list.Count < 2) continue;
            var parent = new int[list.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            viaNeighbour.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var (a, b, c) = skin.Triangles[list[i]];
                foreach (int other in new[] { a, b, c })
                {
                    if (other == v) continue;
                    if (viaNeighbour.TryGetValue(other, out int j)) parent[Find(i)] = Find(j);
                    else viaNeighbour[other] = i;
                }
            }
            int roots = 0;
            for (int i = 0; i < parent.Length; i++) if (Find(i) == i) roots++;
            if (roots > 1 && pinched++ == 0) firstPinched = v;
        }

        if (skin.IrregularEdgeCount == 0 && pinched == 0)
            return new MeshAuditCheck("A2", "skin manifoldness", MeshAuditOutcome.Passed,
                $"{skin.Triangles.Length:N0} skin triangles");

        var parts = new List<string>();
        if (skin.IrregularEdgeCount > 0)
            parts.Add($"{skin.IrregularEdgeCount:N0} skin edge(s) not shared by exactly two skin triangles");
        Vector3D? at = null;
        if (pinched > 0)
        {
            at = skin.Vertices[firstPinched];
            parts.Add($"{pinched:N0} skin vertex/vertices where the surface pinches, first at {Format(at.Value)}");
        }
        return new MeshAuditCheck("A2", "skin manifoldness", MeshAuditOutcome.Failed,
            string.Join("; ", parts) + "; the boundary is not a closed 2-manifold", Location: at);
    }

    // ---------------------------------------------------------------- A3

    /// <summary>
    /// A3: the skin is exactly the element faces used once, wound consistently and outward,
    /// and encloses the mesh volume (Σ signed skin volume = Σ element volume to 1e-9).
    /// </summary>
    private static MeshAuditCheck CheckClosure(FeMesh mesh, ElementView elements,
        Dictionary<FaceKey, FaceUse> faceUse, AuditSurface skin)
    {
        var problems = new List<string>();
        Vector3D? at = null;

        // The skin against the faces the elements actually leave exposed.
        int exposed = faceUse.Values.Count(u => u.Count == 1);
        int missing = 0, inward = 0;
        if (mesh.IsHex)
        {
            var quads = mesh.BoundaryQuads!;
            var allowed = new HashSet<FaceKey>();
            foreach (var q in quads)
            {
                var key = FaceKey.Of(q.A, q.B, q.C, q.D);
                if (!faceUse.TryGetValue(key, out var use) || use.Count != 1) { missing++; continue; }
                if (!elements.FacesOutward(use, mesh.Nodes[q.A], mesh.Nodes[q.B], mesh.Nodes[q.C])) inward++;
                allowed.Add(FaceKey.Of(q.A, q.B, q.C)); allowed.Add(FaceKey.Of(q.A, q.C, q.D));
                allowed.Add(FaceKey.Of(q.A, q.B, q.D)); allowed.Add(FaceKey.Of(q.B, q.C, q.D));
            }
            if (quads.Count != exposed)
                problems.Add($"{quads.Count:N0} skin quads but {exposed:N0} exposed element faces");
            if (mesh.BoundaryTriangles.Count != 2 * quads.Count)
                problems.Add($"{mesh.BoundaryTriangles.Count:N0} skin triangles for {quads.Count:N0} skin quads");
            foreach (var t in mesh.BoundaryTriangles)
                if (!allowed.Contains(FaceKey.Of(t.A, t.B, t.C))) missing++;
        }
        else
        {
            if (mesh.BoundaryTriangles.Count != exposed)
                problems.Add($"{mesh.BoundaryTriangles.Count:N0} skin triangles but {exposed:N0} exposed element faces");
            foreach (var t in mesh.BoundaryTriangles)
            {
                if (!faceUse.TryGetValue(FaceKey.Of(t.A, t.B, t.C), out var use) || use.Count != 1)
                {
                    if (missing++ == 0) at = mesh.Nodes[t.A];
                    continue;
                }
                if (!elements.FacesOutward(use, mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C]) && inward++ == 0)
                    at ??= mesh.Nodes[t.A];
            }
        }
        if (missing > 0) problems.Add($"{missing:N0} skin face(s) that are not an exposed element face");
        if (inward > 0) problems.Add($"{inward:N0} skin face(s) wound inward");

        // Consistent winding: each directed edge once.
        var directed = new HashSet<(int, int)>();
        int repeated = 0;
        foreach (var (a, b, c) in skin.Triangles)
            if (!directed.Add((a, b)) | !directed.Add((b, c)) | !directed.Add((c, a))) repeated++;
        if (repeated > 0) problems.Add($"{repeated:N0} skin triangle(s) wound against a neighbour");

        // Divergence theorem: the skin encloses what the elements fill.
        double enclosed = skin.Shells.Sum(s => s.SignedVolume);
        double filled = mesh.TotalVolume();
        double relative = Math.Abs(enclosed - filled) / Math.Max(Math.Abs(filled), double.Epsilon);
        if (!(relative <= 1e-9))
            problems.Add($"the skin encloses {enclosed:g10} m³ but the elements fill {filled:g10} m³");

        return problems.Count == 0
            ? new MeshAuditCheck("A3", "skin closure and orientation", MeshAuditOutcome.Passed,
                $"enclosed volume matches to {relative:e1}", relative, 1e-9)
            : new MeshAuditCheck("A3", "skin closure and orientation", MeshAuditOutcome.Failed,
                string.Join("; ", problems), relative, 1e-9, at);
    }

    // ---------------------------------------------------------------- skin ↔ geometry

    /// <summary>For each skin triangle: the geometry triangle nearest its centroid.</summary>
    private sealed record SkinMatch(int[] GeometryTriangle, double[] Distance);

    private SkinMatch MatchSkin(AuditSurface skin)
    {
        int n = skin.Triangles.Length;
        var triangle = new int[n];
        var distance = new double[n];
        for (int t = 0; t < n; t++)
        {
            _geometry.Bvh.Closest(skin.Bvh.Centroid(t), double.PositiveInfinity, null,
                out triangle[t], out distance[t], out _);
        }
        return new SkinMatch(triangle, distance);
    }

    // ---------------------------------------------------------------- A6

    /// <summary>
    /// A6: as many face-connected element components as the geometry has material regions,
    /// one per region, and no element reaching from one region to another. Regions, not
    /// shells: a hollow solid's wall elements legitimately run from the outer shell to the
    /// void's.
    /// </summary>
    private MeshAuditCheck CheckConnectivity(FeMesh mesh, ElementView elements,
        Dictionary<FaceKey, FaceUse> faceUse, AuditSurface skin, SkinMatch match,
        out int[]? componentRegion, out double[] componentVolume)
    {
        int count = elements.Count;
        var parent = new int[count];
        for (int e = 0; e < count; e++) parent[e] = e;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var use in faceUse.Values)
        {
            if (use.Count != 2) continue;
            int ra = Find(use.First), rb = Find(use.Second);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        var componentOf = new int[count];
        var index = new Dictionary<int, int>();
        for (int e = 0; e < count; e++)
        {
            int root = Find(e);
            if (!index.TryGetValue(root, out int c)) index[root] = c = index.Count;
            componentOf[e] = c;
        }
        int components = index.Count;
        componentVolume = new double[components];
        for (int e = 0; e < count; e++) componentVolume[componentOf[e]] += mesh.ElementVolume(e);

        // Region of every skin node, from the geometry its skin triangles sit on.
        var nodeRegions = new Dictionary<int, int>();   // node → region, or −2 when it carries two
        var componentRegions = new HashSet<int>[components];
        for (int c = 0; c < components; c++) componentRegions[c] = new HashSet<int>();
        for (int t = 0; t < skin.Triangles.Length; t++)
        {
            int region = _geometry.RegionOfTriangle(match.GeometryTriangle[t]);
            var (a, b, c) = skin.Triangles[t];
            foreach (int node in new[] { a, b, c })
                nodeRegions[node] = nodeRegions.TryGetValue(node, out int seen) && seen != region ? -2 : region;
            if (faceUse.TryGetValue(FaceKey.Of(a, b, c), out var use) && use.Count == 1)
                componentRegions[componentOf[use.First]].Add(region);
            else if (mesh.IsHex && elements.OwnerOfSkinTriangle(faceUse, a, b, c) is int owner and >= 0)
                componentRegions[componentOf[owner]].Add(region);
        }

        int spanning = 0, firstSpanning = -1;
        var corners = new int[8];
        for (int e = 0; e < count; e++)
        {
            int cornerCount = elements.Corners(e, corners);
            int region = -1;
            bool spans = false;
            for (int i = 0; i < cornerCount && !spans; i++)
            {
                if (!nodeRegions.TryGetValue(corners[i], out int r)) continue;
                if (r == -2 || (region >= 0 && r != region)) spans = true;
                region = r;
            }
            if (spans && spanning++ == 0) firstSpanning = e;
        }

        int regions = _geometry.RegionCount;
        bool oneEach = components == regions
            && componentRegions.All(set => set.Count == 1)
            && componentRegions.Select(set => set.First()).Distinct().Count() == regions;
        componentRegion = oneEach ? componentRegions.Select(set => set.First()).ToArray() : null;

        if (oneEach && spanning == 0)
            return new MeshAuditCheck("A6", "connectivity vs material regions", MeshAuditOutcome.Passed,
                regions == 1 ? "one region, one connected mesh" : $"{regions} regions, {components} connected meshes",
                components, regions);

        var parts = new List<string>();
        if (components != regions)
            parts.Add($"the geometry has {regions} separate material region(s) but the mesh has {components} " +
                      "connected component(s)" +
                      (components < regions ? " — separate bodies were meshed as one" : ""));
        else if (!oneEach)
            parts.Add("the connected mesh components do not correspond one-to-one to the geometry's material regions");
        Vector3D? at = null;
        if (spanning > 0)
        {
            at = elements.Centroid(firstSpanning);
            parts.Add($"{spanning:N0} element(s) span two different material regions, first near {Format(at.Value)}");
        }
        return new MeshAuditCheck("A6", "connectivity vs material regions", MeshAuditOutcome.Failed,
            string.Join("; ", parts), components, regions, at);
    }

    // ---------------------------------------------------------------- A4

    /// <summary>A4: each region's mesh volume within T_V(h) = c·h·A/V + floor of its
    /// geometric volume.</summary>
    private MeshAuditCheck CheckVolume(FeMesh mesh, int[]? componentRegion, double[] componentVolume)
    {
        double worst = 0, worstLimit = 0;
        bool failed = false;
        string detail = "";

        void Compare(double meshVolume, double volume, double area, string what)
        {
            double limit = VolumeFactor * _h * area / volume + VolumeFloor;
            double error = Math.Abs(meshVolume - volume) / volume;
            if (error / limit >= worst / Math.Max(worstLimit, double.Epsilon) || worstLimit == 0)
            {
                worst = error;
                worstLimit = limit;
                detail = $"{what}: mesh {meshVolume:g6} m³ vs geometry {volume:g6} m³ ({error:p3}, allowed {limit:p3})";
            }
            if (!(error <= limit)) failed = true;
        }

        if (componentRegion is not null)
        {
            for (int c = 0; c < componentRegion.Length; c++)
            {
                int r = componentRegion[c];
                Compare(componentVolume[c], _geometry.RegionVolumes[r], _geometry.RegionAreas[r],
                    componentRegion.Length == 1 ? "volume" : $"region {r + 1} of {componentRegion.Length}");
            }
        }
        else
        {
            // No region-by-region correspondence (A6 says why): the totals still must agree.
            Compare(mesh.TotalVolume(), _geometry.RegionVolumes.Sum(), _geometry.RegionAreas.Sum(), "total volume");
        }

        return new MeshAuditCheck("A4", "volume per region",
            failed ? MeshAuditOutcome.Failed : MeshAuditOutcome.Passed, detail, worst, worstLimit);
    }

    // ---------------------------------------------------------------- A5

    /// <summary>A5: every geometric face larger than h² is on the skin, with its area
    /// within T_A. Only where the geometry distinguishes faces at all.</summary>
    private MeshAuditCheck CheckFaceCoverage(FeMesh mesh, AuditSurface skin)
    {
        if (_source.FaceCount <= 1)
            return new MeshAuditCheck("A5", "face-id coverage", MeshAuditOutcome.Skipped,
                "the geometry carries a single face id");

        var geometryArea = new double[_source.FaceCount];
        for (int t = 0; t < _source.Triangles.Count; t++)
        {
            int face = _source.TriangleFaceIds[t];
            if (face >= 0) geometryArea[face] += _geometry.TriangleAreas[t];
        }
        var meshArea = new double[_source.FaceCount];
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            int face = mesh.BoundaryTriangles[t].FaceId;
            if (face >= 0 && face < meshArea.Length) meshArea[face] += skin.TriangleAreas[t];
        }

        double worst = 0;
        int worstFace = -1, absent = 0, off = 0, checkedFaces = 0;
        for (int face = 0; face < geometryArea.Length; face++)
        {
            if (!(geometryArea[face] > _h * _h)) continue;
            checkedFaces++;
            double error = Math.Abs(meshArea[face] - geometryArea[face]) / geometryArea[face];
            if (meshArea[face] <= 0) absent++;
            else if (error > AreaTolerance) off++;
            if (error > worst) { worst = error; worstFace = face; }
        }

        if (absent == 0 && off == 0)
            return new MeshAuditCheck("A5", "face-id coverage", MeshAuditOutcome.Passed,
                $"{checkedFaces} face(s), worst area error {worst:p2}", worst, AreaTolerance);

        var parts = new List<string>();
        if (absent > 0) parts.Add($"{absent} geometric face(s) larger than h² have no boundary triangle");
        if (off > 0) parts.Add($"{off} geometric face(s) have a boundary area off by more than {AreaTolerance:p0}");
        parts.Add($"worst: face {worstFace}, mesh {meshArea[worstFace]:g5} m² vs geometry {geometryArea[worstFace]:g5} m²");
        return new MeshAuditCheck("A5", "face-id coverage", MeshAuditOutcome.Failed,
            string.Join("; ", parts) + " — a face was bridged over or collapsed", worst, AreaTolerance);
    }

    // ---------------------------------------------------------------- A7

    /// <summary>A7: no skin triangle (centroid or corner) further than T_D from the geometry.</summary>
    private MeshAuditCheck CheckSkinDistance(AuditSurface skin, SkinMatch match)
    {
        double worst = 0;
        Vector3D worstAt = default;
        int beyond = 0;
        for (int t = 0; t < skin.Triangles.Length; t++)
        {
            if (match.Distance[t] > _distanceTolerance) beyond++;
            if (match.Distance[t] > worst) { worst = match.Distance[t]; worstAt = skin.Bvh.Centroid(t); }
        }
        var used = new HashSet<int>();
        foreach (var (a, b, c) in skin.Triangles) { used.Add(a); used.Add(b); used.Add(c); }
        foreach (int v in used)
        {
            double d = _geometry.Bvh.Distance(skin.Vertices[v]);
            if (d > _distanceTolerance) beyond++;
            if (d > worst) { worst = d; worstAt = skin.Vertices[v]; }
        }

        return beyond == 0
            ? new MeshAuditCheck("A7", "skin to geometry distance", MeshAuditOutcome.Passed,
                $"worst {worst / _h:f4}·h", worst, _distanceTolerance, worstAt)
            : new MeshAuditCheck("A7", "skin to geometry distance", MeshAuditOutcome.Failed,
                $"the mesh skin is {worst:g4} m ({worst / _h:f3}·h) from the geometry near {Format(worstAt)}, " +
                $"over the {_distanceTolerance:g4} m allowed ({beyond:N0} skin points) — the skin is not on the surface; reduce h",
                worst, _distanceTolerance, worstAt);
    }

    // ---------------------------------------------------------------- A8

    /// <summary>A8: no geometry surface sample further than T_D from the mesh skin — the
    /// reverse of A7, and the check that does not need face ids to see a filled hole.</summary>
    private MeshAuditCheck CheckCoverage(AuditSurface skin, Vector3D[] nearest, bool[] covered)
    {
        double worst = 0;
        Vector3D worstAt = default;
        int uncovered = 0;
        for (int s = 0; s < _samples.Count; s++)
        {
            var p = _samples[s].Position;
            skin.Bvh.Closest(p, double.PositiveInfinity, null, out _, out double d, out nearest[s]);
            covered[s] = d <= _distanceTolerance;
            if (!covered[s]) uncovered++;
            if (d > worst) { worst = d; worstAt = p; }
        }

        return uncovered == 0
            ? new MeshAuditCheck("A8", "geometry to skin coverage", MeshAuditOutcome.Passed,
                $"worst {worst / _h:f4}·h over {_samples.Count:N0} samples", worst, _distanceTolerance, worstAt)
            : new MeshAuditCheck("A8", "geometry to skin coverage", MeshAuditOutcome.Failed,
                $"the geometry surface near {Format(worstAt)} is {worst:g4} m ({worst / _h:f3}·h) from the mesh skin, " +
                $"over the {_distanceTolerance:g4} m allowed ({uncovered:N0} of {_samples.Count:N0} samples) — " +
                "feature smaller than the target edge length (a hole, slot or concavity was filled or bridged); " +
                "reduce h or accept the simplification",
                worst, _distanceTolerance, worstAt);
    }

    // ---------------------------------------------------------------- A9

    /// <summary>
    /// A9: the skin's shells against the geometry's, one to one — same count, each skin
    /// shell lying on a single geometry shell, same nesting depth, same genus. An aggregate
    /// Euler characteristic is not enough: a through-bore (χ 0) and a void (χ 2) both filled
    /// leave a plain cube (χ 2).
    /// </summary>
    private MeshAuditCheck CheckShells(AuditSurface skin, SkinMatch match)
    {
        var problems = new List<string>();
        if (skin.Shells.Length != _geometry.Shells.Length)
            problems.Add($"the geometry has {_geometry.Shells.Length} closed shell(s), the mesh skin has {skin.Shells.Length}");

        var matchedTo = new int[_geometry.Shells.Length];
        Array.Fill(matchedTo, -1);
        foreach (var shell in skin.Shells)
        {
            int target = -1;
            bool split = false;
            foreach (int t in shell.Triangles)
            {
                int g = _geometry.ShellOf[match.GeometryTriangle[t]];
                if (target < 0) target = g;
                else if (g != target) { split = true; break; }
            }
            var where = skin.Bvh.Centroid(shell.Triangles[0]);
            if (split)
            {
                problems.Add($"the skin shell at {Format(where)} lies on more than one geometry shell");
                continue;
            }
            if (matchedTo[target] >= 0)
            {
                problems.Add($"two skin shells lie on the geometry shell at {Format(where)}");
                continue;
            }
            matchedTo[target] = shell.Id;
            var geometryShell = _geometry.Shells[target];
            if (shell.Depth != geometryShell.Depth)
                problems.Add($"the shell at {Format(where)} is nested {shell.Depth} deep in the mesh but " +
                             $"{geometryShell.Depth} deep in the geometry");
            if (shell.Genus != geometryShell.Genus)
                problems.Add($"the shell at {Format(where)} has {Handles(shell.Genus)} in the mesh but " +
                             $"{Handles(geometryShell.Genus)} in the geometry — a hole was filled or opened");
        }
        for (int g = 0; g < matchedTo.Length; g++)
            if (matchedTo[g] < 0)
                problems.Add($"the geometry shell at {Format(_geometry.Bvh.Centroid(_geometry.Shells[g].Triangles[0]))} " +
                             "has no counterpart in the mesh skin — a void or body vanished or merged");

        return problems.Count == 0
            ? new MeshAuditCheck("A9", "shell correspondence", MeshAuditOutcome.Passed,
                $"{skin.Shells.Length} shell(s) matched", skin.Shells.Length, _geometry.Shells.Length)
            : new MeshAuditCheck("A9", "shell correspondence", MeshAuditOutcome.Failed,
                string.Join("; ", problems.Take(DescribedFailures)) +
                (problems.Count > DescribedFailures ? $"; and {problems.Count - DescribedFailures} more" : ""),
                skin.Shells.Length, _geometry.Shells.Length);
    }

    private static string Handles(double genus) =>
        genus == Math.Floor(genus) ? $"{genus:f0} through-hole(s)" : "a non-manifold topology";

    // ---------------------------------------------------------------- A10

    /// <summary>
    /// A10: at every geometry sample, a ray along the normal into the solid and one out of
    /// it must meet the same things in the mesh as in the geometry.
    /// <para>
    /// Single rays, and hit POINTS compared against the sheet the other ray struck — never
    /// distances along the ray, which blow up at grazing incidence, and with no incidence
    /// cutoff, which only moves that failure. A hit on one side and a miss on the other is
    /// a silhouette if the ray that missed passed within tolerance of the corresponding
    /// sheet, and a failure otherwise. Displacing a thin feature about its own midplane
    /// keeps every hit point within tolerance; that case is carried by the occupancy
    /// probes, which sit a tenth of the local thickness (or clearance) off the surface.
    /// </para>
    /// <para>
    /// "Corresponding sheet" is defined from the geometry, never from the mesh's own
    /// dihedrals (coarse faceting of a curved sheet creases everywhere): a skin triangle
    /// belongs to every geometry sheet within tolerance of it that it faces the same way
    /// as — strictly positive normal dot, no tighter. A facet that legitimately chamfers a
    /// convex crease within tolerance therefore belongs to both sheets. One that replaces a
    /// REENTRANT crease at more than 90° to a face it stands in for belongs to neither and
    /// is refused although its distances pass; that false rejection is accepted (a band
    /// around creases exempt from the normal test aliases opposing walls closer than twice
    /// the tolerance, which is a false ACCEPTANCE), and costs a re-mesh at smaller h.
    /// </para>
    /// </summary>
    private MeshAuditCheck CheckLocalThickness(AuditSurface skin, Vector3D[] nearest)
    {
        var comparison = new SkinComparison(this, skin);
        int failures = 0, compared = 0;
        var described = new List<string>();
        Vector3D? firstAt = null;

        for (int s = 0; s < _samples.Count && failures < 1000; s++)
        {
            compared++;
            string? problem = comparison.Compare(_samples[s], nearest[s]);
            if (problem is null) continue;
            failures++;
            firstAt ??= _samples[s].Position;
            if (described.Count < DescribedFailures)
                described.Add($"at {Format(_samples[s].Position)}: {problem}");
        }
        ResolvedSilhouettes = comparison.ResolvedSilhouettes;

        return failures == 0
            ? new MeshAuditCheck("A10", "local thickness and clearance", MeshAuditOutcome.Passed,
                $"{compared:N0} samples, {comparison.ResolvedSilhouettes:N0} silhouette(s)")
            : new MeshAuditCheck("A10", "local thickness and clearance", MeshAuditOutcome.Failed,
                $"{failures:N0}{(failures >= 1000 ? "+" : "")} of {compared:N0} surface samples see a different solid " +
                $"in the mesh than in the geometry — {string.Join("; ", described)}; reduce h",
                failures, 0, firstAt);
    }

    /// <summary>The state one geometry-against-skin comparison needs: the skin's inside
    /// test and the lazily built sheet labels of its triangles.</summary>
    private sealed class SkinComparison
    {
        private readonly MeshAudit _audit;
        private readonly AuditSurface _geometry;
        private readonly AuditSurface _skin;
        private readonly Dictionary<int, (int Sheet, double Distance)[]> _labels = new();
        private readonly List<(int Triangle, double Distance)> _near = new();
        private readonly double _tie;

        public int ResolvedSilhouettes { get; private set; }

        public SkinComparison(MeshAudit audit, AuditSurface skin)
        {
            _audit = audit;
            _geometry = audit._geometry;
            _skin = skin;
            _tie = 1e-9 * Math.Max(_geometry.Bvh.Diagonal, skin.Bvh.Diagonal);
        }

        /// <summary>Null when the mesh agrees with the geometry at this sample.</summary>
        public string? Compare(in Sample sample, Vector3D onSkin)
        {
            var normal = _geometry.Outward[sample.Triangle];
            double thickness = FeatureSize(sample.Thickness, sample.ThicknessIsWedge);
            double clearance = FeatureSize(sample.Clearance, sample.ClearanceIsWedge);
            double tolerance = Math.Min(_audit._distanceTolerance, Math.Min(0.2 * thickness, 0.2 * clearance));

            string? problem =
                CompareRay(sample.Position, onSkin, -normal, sample.Thickness, sample.ThicknessTriangle, tolerance, "into the solid")
                ?? CompareRay(sample.Position, onSkin, normal, sample.Clearance, sample.ClearanceTriangle, tolerance, "out of the solid");
            if (problem is not null) return problem;

            // Occupancy a tenth of the way in and out: relative to the feature, not to T_D,
            // so a feature thinned or shifted about its midplane cannot hide inside T_D.
            double inDepth = Math.Min(sample.Thickness / 10, _audit._distanceTolerance);
            var inner = sample.Position - normal * inDepth;
            if (!_skin.Bvh.IsInside(inner) && !WithinToleranceOfSkin(inner, tolerance))
                return $"the mesh has no material {inDepth:g3} m beneath the surface " +
                       $"(local thickness {sample.Thickness:g3} m)";

            double outDepth = Math.Min(sample.Clearance / 10, _audit._distanceTolerance);
            var outer = sample.Position + normal * outDepth;
            if (_skin.Bvh.IsInside(outer) && !WithinToleranceOfSkin(outer, tolerance))
                return $"the mesh has material {outDepth:g3} m above the surface " +
                       $"(local clearance {(double.IsFinite(sample.Clearance) ? sample.Clearance.ToString("g3") + " m" : "open")})";
            return null;
        }

        /// <summary>A probe on the wrong side of the skin is still acceptable within the
        /// tolerance of it. The comparison carries a rounding margin: a probe set exactly one
        /// tolerance off an exact skin is AT the tolerance, not beyond it.</summary>
        private bool WithinToleranceOfSkin(Vector3D point, double tolerance) =>
            _skin.Bvh.Distance(point, tolerance + _tie) <= tolerance + _tie;

        /// <summary>
        /// One direction from one sample. The two rays' crossings are walked in order: a
        /// crossing of the geometry and a crossing of the mesh that are the same sheet at
        /// the same place settle it. Until then, a crossing only one ray has must be a
        /// SILHOUETTE - the surface it crosses has its counterpart within tolerance on the
        /// other side, and the other ray passes within tolerance of that counterpart
        /// without crossing it - and is stepped over. Anything else is a failure.
        /// <para>
        /// A ray from a sample beside a crease runs along the neighbouring face for its whole
        /// length, and the mesh's copy of that face (a jitter amplitude off the plane) crosses
        /// it at will; a ray past a faceted cylinder clips one faceting and misses the other.
        /// Both are the same thing, and "passes within tolerance without crossing" is what
        /// distinguishes them from a wall that is in the wrong place, which IS crossed.
        /// </para>
        /// </summary>
        private string? CompareRay(Vector3D onGeometry, Vector3D onSkin, Vector3D direction,
            double geometryDistance, int geometryTriangle, double tolerance, string which)
        {
            bool geometryHits = geometryTriangle >= 0;
            bool skinHits = _skin.Bvh.FirstHit(onSkin, direction, _audit._epsilon, out var skinFirst);
            if (!geometryHits && !skinHits) return null;
            if (geometryHits && skinHits && Corresponds(onGeometry + direction * geometryDistance, geometryTriangle,
                    onSkin + direction * skinFirst.T, skinFirst.Triangle, tolerance))
                return null;

            var g = Crossings(_geometry.Bvh, onGeometry, direction);
            var m = Crossings(_skin.Bvh, onSkin, direction);
            double slack = tolerance + Vector3D.Distance(onGeometry, onSkin);
            HashSet<int>? sheetsBesideGeometryRay = null;
            int[]? skinBesideMeshRay = null;

            // A mesh crossing with no geometry crossing to pair with.
            bool MeshCrossingIsSilhouette(SurfaceBvh.RayHit hit)
            {
                var point = onSkin + direction * hit.T;
                var facing = _skin.Outward[hit.Triangle];
                foreach (var (sheet, distance) in Labels(hit.Triangle))
                {
                    if (distance > tolerance || !OnGeometrySheet(point, sheet, tolerance, facing)) continue;
                    bool crossed = false;
                    foreach (var other in g)
                        if (other.T <= hit.T + slack && _audit._sheetOf[other.Triangle] == sheet) { crossed = true; break; }
                    if (crossed) continue;
                    if (sheetsBesideGeometryRay is null)
                    {
                        sheetsBesideGeometryRay = new HashSet<int>();
                        _near.Clear();
                        _geometry.Bvh.NearRay(onGeometry, direction, tolerance, _near);
                        foreach (var (triangle, _) in _near) sheetsBesideGeometryRay.Add(_audit._sheetOf[triangle]);
                    }
                    if (sheetsBesideGeometryRay.Contains(sheet)) return true;
                }
                return false;
            }

            // A geometry crossing with no mesh crossing to pair with.
            bool GeometryCrossingIsSilhouette(SurfaceBvh.RayHit hit)
            {
                int sheet = _audit._sheetOf[hit.Triangle];
                var facing = _geometry.Outward[hit.Triangle];
                if (!OnSkinSheet(onGeometry + direction * hit.T, sheet, tolerance, facing))
                    return false;
                // "Crosses the sheet" is about where the crossing IS, not only which
                // triangle it goes through: a skin triangle with one corner on a crease
                // carries the far face's label across its whole extent.
                foreach (var other in m)
                    if (other.T <= hit.T + slack && HasLabel(other.Triangle, sheet, tolerance)
                        && OnGeometrySheet(onSkin + direction * other.T, sheet, tolerance, facing))
                        return false;
                if (skinBesideMeshRay is null)
                {
                    _near.Clear();
                    _skin.Bvh.NearRay(onSkin, direction, tolerance, _near);
                    skinBesideMeshRay = _near.Select(n => n.Triangle).ToArray();
                }
                foreach (int triangle in skinBesideMeshRay)
                    if (HasLabel(triangle, sheet, tolerance)) return true;
                return false;
            }

            bool CorrespondsWithASimultaneousCrossing(List<SurfaceBvh.RayHit> gs, int gAt, List<SurfaceBvh.RayHit> ms, int mAt)
            {
                for (int i = gAt; i < gs.Count && gs[i].T <= gs[gAt].T + _tie; i++)
                    for (int j = mAt; j < ms.Count && ms[j].T <= ms[mAt].T + _tie; j++)
                        if ((i != gAt || j != mAt)
                            && Corresponds(onGeometry + direction * gs[i].T, gs[i].Triangle,
                                onSkin + direction * ms[j].T, ms[j].Triangle, tolerance))
                            return true;
                return false;
            }

            int gi = 0, mi = 0;
            bool stepped = false;
            while (gi < g.Count || mi < m.Count)
            {
                bool hasG = gi < g.Count, hasM = mi < m.Count;
                Vector3D geometryPoint = hasG ? onGeometry + direction * g[gi].T : default;
                Vector3D skinPoint = hasM ? onSkin + direction * m[mi].T : default;
                if (hasG && hasM && Corresponds(geometryPoint, g[gi].Triangle, skinPoint, m[mi].Triangle, tolerance))
                    break;
                // A ray through an edge crosses both triangles on it at once, and which is
                // listed first is rounding: crossings at the same place are interchangeable.
                if (hasG && hasM && CorrespondsWithASimultaneousCrossing(g, gi, m, mi))
                    break;

                // Nearer crossing first; either may be the silhouette.
                bool meshFirst = hasM && (!hasG || m[mi].T <= g[gi].T);
                if (meshFirst && MeshCrossingIsSilhouette(m[mi])) { mi++; stepped = true; continue; }
                if (hasG && GeometryCrossingIsSilhouette(g[gi])) { gi++; stepped = true; continue; }
                if (!meshFirst && hasM && MeshCrossingIsSilhouette(m[mi])) { mi++; stepped = true; continue; }

                if (hasG && hasM)
                {
                    int sheet = _audit._sheetOf[g[gi].Triangle];
                    return !HasLabel(m[mi].Triangle, sheet, tolerance)
                        ? $"the ray {which} strikes a mesh face at {Format(skinPoint)} that corresponds to no part of " +
                          $"the geometry face it should meet at {Format(geometryPoint)}{_audit.CreaseNote(geometryPoint)}"
                        : $"the ray {which} leaves the mesh at {Format(skinPoint)} but the geometry at " +
                          $"{Format(geometryPoint)}, and the two faces are more than {tolerance:g3} m apart there";
                }
                return hasG
                    ? $"the ray {which} meets the geometry at {Format(geometryPoint)} but nothing in the mesh " +
                      $"within {tolerance:g3} m of it"
                    : $"the ray {which} meets the mesh at {Format(skinPoint)} where the geometry has nothing " +
                      $"within {tolerance:g3} m";
            }
            if (stepped) ResolvedSilhouettes++;
            return null;
        }

        /// <summary>Every crossing of a ray with a surface, nearest first.</summary>
        private List<SurfaceBvh.RayHit> Crossings(SurfaceBvh surface, Vector3D origin, Vector3D direction)
        {
            var hits = new List<SurfaceBvh.RayHit>();
            surface.AllHits(origin, direction, hits);
            hits.RemoveAll(h => h.T <= _audit._epsilon);
            hits.Sort((x, y) => x.T.CompareTo(y.T));
            return hits;
        }

        /// <summary>A geometry crossing and a mesh crossing are the same event: the mesh
        /// face belongs to the geometry sheet, and each hit point lies on the other
        /// surface's copy of that sheet, on the side the geometry face looks.</summary>
        private bool Corresponds(Vector3D geometryPoint, int geometryTriangle, Vector3D skinPoint, int skinTriangle,
            double tolerance)
        {
            int sheet = _audit._sheetOf[geometryTriangle];
            var facing = _geometry.Outward[geometryTriangle];
            return HasLabel(skinTriangle, sheet, tolerance)
                   && OnGeometrySheet(skinPoint, sheet, tolerance, facing)
                   && OnSkinSheet(geometryPoint, sheet, tolerance, facing);
        }

        /// <summary>
        /// The geometry sheets a skin triangle belongs to, each with the distance at which
        /// it does: every sheet some point of the triangle (corners and centroid) lies
        /// within T_D of while facing the same way as the sheet does there.
        /// </summary>
        private (int Sheet, double Distance)[] Labels(int skinTriangle)
        {
            if (_labels.TryGetValue(skinTriangle, out var cached)) return cached;

            var (a, b, c) = _skin.Bvh.Corners(skinTriangle);
            var facing = _skin.Outward[skinTriangle];
            var found = new List<(int Sheet, double Distance)>(2);
            foreach (var point in new[] { a, b, c, (a + b + c) / 3.0 })
            {
                _near.Clear();
                _geometry.Bvh.Within(point, _audit._distanceTolerance, _near);
                if (_near.Count == 0) continue;
                // Per sheet: the closest triangle, and whether the sheet faces our way there.
                _near.Sort((x, y) =>
                {
                    int sx = _audit._sheetOf[x.Triangle], sy = _audit._sheetOf[y.Triangle];
                    return sx != sy ? sx.CompareTo(sy) : x.Distance.CompareTo(y.Distance);
                });
                for (int i = 0; i < _near.Count;)
                {
                    int sheet = _audit._sheetOf[_near[i].Triangle];
                    double closest = _near[i].Distance;
                    bool sameSide = false;
                    int j = i;
                    for (; j < _near.Count && _audit._sheetOf[_near[j].Triangle] == sheet; j++)
                        if (_near[j].Distance <= closest + _tie
                            && Vector3D.Dot(facing, _geometry.Outward[_near[j].Triangle]) > 0)
                            sameSide = true;
                    i = j;
                    if (!sameSide) continue;
                    int at = found.FindIndex(f => f.Sheet == sheet);
                    if (at < 0) found.Add((sheet, closest));
                    else if (closest < found[at].Distance) found[at] = (sheet, closest);
                }
            }
            var labels = found.ToArray();
            _labels[skinTriangle] = labels;
            return labels;
        }

        private bool HasLabel(int skinTriangle, int sheet, double tolerance)
        {
            foreach (var (labelled, distance) in Labels(skinTriangle))
                if (labelled == sheet && distance <= tolerance) return true;
            return false;
        }

        /// <summary>The point is within tolerance of the geometry sheet, on the side the
        /// reference normal faces.</summary>
        private bool OnGeometrySheet(Vector3D point, int sheet, double tolerance, Vector3D reference)
        {
            _near.Clear();
            _geometry.Bvh.Within(point, tolerance, _near);
            double closest = double.PositiveInfinity;
            foreach (var (triangle, distance) in _near)
                if (_audit._sheetOf[triangle] == sheet && distance < closest) closest = distance;
            if (double.IsPositiveInfinity(closest)) return false;
            foreach (var (triangle, distance) in _near)
                if (_audit._sheetOf[triangle] == sheet && distance <= closest + _tie
                    && Vector3D.Dot(_geometry.Outward[triangle], reference) > 0)
                    return true;
            return false;
        }

        /// <summary>The point is within tolerance of the skin triangles that belong to the
        /// geometry sheet, on the side the reference normal faces.</summary>
        private bool OnSkinSheet(Vector3D point, int sheet, double tolerance, Vector3D reference)
        {
            _near.Clear();
            _skin.Bvh.Within(point, tolerance, _near);
            var candidates = _near.ToArray();      // Labels() reuses the buffer
            double closest = double.PositiveInfinity;
            var member = new bool[candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                member[i] = HasLabel(candidates[i].Triangle, sheet, tolerance);
                if (member[i] && candidates[i].Distance < closest) closest = candidates[i].Distance;
            }
            if (double.IsPositiveInfinity(closest)) return false;
            for (int i = 0; i < candidates.Length; i++)
                if (member[i] && candidates[i].Distance <= closest + _tie
                    && Vector3D.Dot(_skin.Outward[candidates[i].Triangle], reference) > 0)
                    return true;
            return false;
        }
    }

    /// <summary>" at the geometry crease near (…)" when the point is within T_D of one —
    /// the case where a mesh facet has replaced the crease.</summary>
    private string CreaseNote(Vector3D point)
    {
        double best = double.PositiveInfinity;
        Vector3D at = default;
        foreach (var (a, b) in _creases)
        {
            var ab = b - a;
            double lengthSquared = ab.LengthSquared;
            double t = lengthSquared > 0 ? Math.Clamp(Vector3D.Dot(point - a, ab) / lengthSquared, 0, 1) : 0;
            var q = a + ab * t;
            double d = Vector3D.Distance(point, q);
            if (d < best) { best = d; at = q; }
        }
        return best <= _distanceTolerance
            ? $" — a mesh facet replaces the geometry crease near {Format(at)} at more than 90° to one of its faces"
            : "";
    }

    private static string Format(Vector3D p) => $"({p.X:g5}, {p.Y:g5}, {p.Z:g5})";

    // =====================================================================  elements

    /// <summary>An element face by its sorted corner nodes (A = −1 for a triangle).</summary>
    private readonly record struct FaceKey(int A, int B, int C, int D)
    {
        public static FaceKey Of(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return new FaceKey(-1, a, b, c);
        }

        public static FaceKey Of(int a, int b, int c, int d)
        {
            if (a > b) (a, b) = (b, a);
            if (c > d) (c, d) = (d, c);
            if (a > c) (a, c) = (c, a);
            if (b > d) (b, d) = (d, b);
            if (b > c) (b, c) = (c, b);
            return new FaceKey(a, b, c, d);
        }
    }

    /// <summary>How many elements use a face, and the first two of them.</summary>
    private readonly record struct FaceUse(int Count, int First, int Second);

    /// <summary>Tetrahedra and hexahedra behind one face/corner enumeration.</summary>
    private sealed class ElementView
    {
        private static readonly int[][] HexFaces =
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 }
        };

        private readonly FeMesh _mesh;

        public ElementView(FeMesh mesh) => _mesh = mesh;

        public int Count => _mesh.ElementCount;

        public int Corners(int element, int[] corners)
        {
            if (_mesh.HexElements is { } hexes)
            {
                var h = hexes[element];
                corners[0] = h.N0; corners[1] = h.N1; corners[2] = h.N2; corners[3] = h.N3;
                corners[4] = h.N4; corners[5] = h.N5; corners[6] = h.N6; corners[7] = h.N7;
                return 8;
            }
            var e = _mesh.Elements[element];
            corners[0] = e.N0; corners[1] = e.N1; corners[2] = e.N2; corners[3] = e.N3;
            return 4;
        }

        public Vector3D Centroid(int element)
        {
            var corners = new int[8];
            int n = Corners(element, corners);
            var sum = Vector3D.Zero;
            for (int i = 0; i < n; i++) sum += _mesh.Nodes[corners[i]];
            return sum / n;
        }

        public Dictionary<FaceKey, FaceUse> BuildFaceUse()
        {
            var use = new Dictionary<FaceKey, FaceUse>(Count * 2 + 1);
            void Touch(FaceKey key, int element) =>
                use[key] = use.TryGetValue(key, out var seen)
                    ? new FaceUse(seen.Count + 1, seen.First, seen.Count == 1 ? element : seen.Second)
                    : new FaceUse(1, element, -1);

            if (_mesh.HexElements is { } hexes)
            {
                var c = new int[8];
                for (int e = 0; e < hexes.Count; e++)
                {
                    Corners(e, c);
                    foreach (var f in HexFaces) Touch(FaceKey.Of(c[f[0]], c[f[1]], c[f[2]], c[f[3]]), e);
                }
            }
            else
            {
                for (int e = 0; e < _mesh.Elements.Count; e++)
                {
                    var t = _mesh.Elements[e];
                    Touch(FaceKey.Of(t.N1, t.N2, t.N3), e);
                    Touch(FaceKey.Of(t.N0, t.N2, t.N3), e);
                    Touch(FaceKey.Of(t.N0, t.N1, t.N3), e);
                    Touch(FaceKey.Of(t.N0, t.N1, t.N2), e);
                }
            }
            return use;
        }

        /// <summary>Whether the face a–b–c, as wound, turns its normal away from the one
        /// element that owns it.</summary>
        public bool FacesOutward(FaceUse use, Vector3D a, Vector3D b, Vector3D c) =>
            Vector3D.Dot(Vector3D.Cross(b - a, c - a), Centroid(use.First) - a) < 0;

        /// <summary>The hexahedron whose exposed quad face contains this skin triangle, or −1.</summary>
        public int OwnerOfSkinTriangle(Dictionary<FaceKey, FaceUse> faceUse, int a, int b, int c)
        {
            if (_mesh.BoundaryQuads is not { } quads) return -1;
            _quadOfTriangle ??= BuildQuadLookup(quads);
            return _quadOfTriangle.TryGetValue(FaceKey.Of(a, b, c), out var quad)
                   && faceUse.TryGetValue(quad, out var use) && use.Count == 1
                ? use.First
                : -1;
        }

        private Dictionary<FaceKey, FaceKey>? _quadOfTriangle;

        private static Dictionary<FaceKey, FaceKey> BuildQuadLookup(IReadOnlyList<BoundaryQuad> quads)
        {
            var lookup = new Dictionary<FaceKey, FaceKey>(quads.Count * 4);
            foreach (var q in quads)
            {
                var key = FaceKey.Of(q.A, q.B, q.C, q.D);
                lookup[FaceKey.Of(q.A, q.B, q.C)] = key; lookup[FaceKey.Of(q.A, q.C, q.D)] = key;
                lookup[FaceKey.Of(q.A, q.B, q.D)] = key; lookup[FaceKey.Of(q.B, q.C, q.D)] = key;
            }
            return lookup;
        }
    }
}
