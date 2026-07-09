using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Tessellate;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// Resolves the raw Part 21 instance table into typed geometry and topology, converting
/// to meters at CARTESIAN_POINT/VECTOR resolution time. Memoized so shared entities
/// (vertices, curves, placements) resolve to shared objects — edge identity matters
/// downstream because both adjacent faces must receive the SAME sampled polyline.
/// Everything outside the v1 subset fails as a typed
/// <see cref="StepUnsupportedEntityException"/> naming the entity and where it was used;
/// unreachable exotica (PMI, colours, presentation) are never touched.
/// </summary>
public sealed class StepEntityResolver
{
    private readonly StepFile _file;
    private readonly double _scale;
    private readonly Dictionary<int, object> _memo = new();

    public StepEntityResolver(StepFile file, double metersPerUnit)
    {
        _file = file;
        _scale = metersPerUnit;
    }

    /// <summary>
    /// All solids in the file, ascending #id. Solids are discovered by scanning for
    /// MANIFOLD_SOLID_BREP / BREP_WITH_VOIDS records directly — robust across exporter
    /// representation-chain dialects. Zero solids is a hard failure that distinguishes
    /// sheet/faceted bodies (exportable as solids) from a genuinely empty file.
    /// </summary>
    public IReadOnlyList<StepSolid> ResolveSolids()
    {
        var solids = new List<StepSolid>();
        foreach (var (id, inst) in _file.Instances)
        {
            if (inst.Has("MANIFOLD_SOLID_BREP") || inst.Has("BREP_WITH_VOIDS"))
                solids.Add(Solid(id));
        }
        if (solids.Count > 0) return solids;

        foreach (var (id, inst) in _file.Instances)
        {
            if (inst.Has("OPEN_SHELL") || inst.Has("SHELL_BASED_SURFACE_MODEL") || inst.Has("FACETED_BREP"))
                throw new StepImportException(
                    $"#{id} {inst.Keyword}: sheet/faceted bodies are not solids; export a solid body");
        }
        throw new StepImportException("no MANIFOLD_SOLID_BREP found — the file contains no solid body");
    }

    /// <summary>True when assembly placement machinery is present (ignored in v1, but never silently).</summary>
    public bool HasAssemblyTransforms()
    {
        foreach (var inst in _file.Instances.Values)
            if (inst.Has("ITEM_DEFINED_TRANSFORMATION") || inst.Has("REPRESENTATION_MAP"))
                return true;
        return false;
    }

    // ---------------- geometry primitives ----------------

    public Vector3D Point(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "CARTESIAN_POINT");
        var c = rec.Args[1].AsList();
        if (c.Count != 3)
            throw new StepImportException($"#{id}: CARTESIAN_POINT has {c.Count} coordinates (3D required)");
        return new Vector3D(c[0].AsReal() * _scale, c[1].AsReal() * _scale, c[2].AsReal() * _scale);
    });

    /// <summary>Unit direction (dimensionless — no unit scaling).</summary>
    public Vector3D Direction(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "DIRECTION");
        var c = rec.Args[1].AsList();
        var d = new Vector3D(c[0].AsReal(), c.Count > 1 ? c[1].AsReal() : 0, c.Count > 2 ? c[2].AsReal() : 0);
        if (d.Length == 0) throw new StepImportException($"#{id}: DIRECTION is a zero vector");
        return d.Normalized();
    });

    /// <summary>VECTOR: direction × magnitude; the magnitude is a length and is scaled to meters.</summary>
    public Vector3D Vector(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "VECTOR");
        return Direction(rec.Args[1].AsRef()) * (rec.Args[2].AsReal() * _scale);
    });

    public Axis2Placement3D Placement(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "AXIS2_PLACEMENT_3D");
        var origin = Point(rec.Args[1].AsRef());
        Vector3D? axis = rec.Args[2].IsNull ? null : Direction(rec.Args[2].AsRef());
        Vector3D? refDir = rec.Args[3].IsNull ? null : Direction(rec.Args[3].AsRef());
        return Axis2Placement3D.FromAxes(origin, axis, refDir, id);
    });

    // ---------------- curves ----------------

    /// <summary>Resolves a curve used by edge #<paramref name="edgeId"/> (for error context).</summary>
    public StepCurve Curve(int id, int edgeId) => Memo<StepCurve>(id, inst =>
    {
        // OCC-written files wrap the 3D geometry: SURFACE_CURVE / SEAM_CURVE /
        // INTERSECTION_CURVE (name, curve_3d, associated_geometry, master_representation).
        // The 3D curve is authoritative; pcurves are at most Newton seeds (Phase 4).
        var wrapper = inst.Find("SURFACE_CURVE") ?? inst.Find("SEAM_CURVE") ?? inst.Find("INTERSECTION_CURVE");
        if (wrapper is not null) return Curve(wrapper.Args[1].AsRef(), edgeId);

        var line = inst.Find("LINE");
        if (line is not null)
            return new StepLine(Point(line.Args[1].AsRef()), Vector(line.Args[2].AsRef()));

        var circle = inst.Find("CIRCLE");
        if (circle is not null)
            return new StepCircle(Placement(circle.Args[1].AsRef()), circle.Args[2].AsReal() * _scale);

        var ellipse = inst.Find("ELLIPSE");
        if (ellipse is not null)
            return new StepEllipse(Placement(ellipse.Args[1].AsRef()),
                ellipse.Args[2].AsReal() * _scale, ellipse.Args[3].AsReal() * _scale);

        if (inst.Has("B_SPLINE_CURVE_WITH_KNOTS") || inst.Has("B_SPLINE_CURVE"))
            return BSplineCurve(inst);

        throw new StepUnsupportedEntityException(id, inst.Keyword, $"curve of edge #{edgeId}");
    });

    private StepBSplineCurve BSplineCurve(StepInstance inst)
    {
        // Simple form: B_SPLINE_CURVE_WITH_KNOTS(name, degree, ctrl, form, closed, selfint,
        // mults, knots, spec). Complex form distributes the attributes: B_SPLINE_CURVE
        // carries (degree, ctrl, form, closed, selfint); B_SPLINE_CURVE_WITH_KNOTS carries
        // (mults, knots, spec); RATIONAL_B_SPLINE_CURVE carries (weights).
        var withKnots = inst.Find("B_SPLINE_CURVE_WITH_KNOTS")
                        ?? throw new StepUnsupportedEntityException(inst.Id, inst.Keyword,
                            "B-spline curve without explicit knots (BEZIER/UNIFORM forms)");

        int degree;
        IReadOnlyList<StepValue> ctrlRefs, multsRaw, knotsRaw;
        if (inst.IsComplex && withKnots.Args.Count == 3)
        {
            var core = inst.Find("B_SPLINE_CURVE")
                       ?? throw new StepImportException(
                           $"#{inst.Id}: complex B-spline curve lacks the B_SPLINE_CURVE record");
            degree = (int)core.Args[0].AsInt();
            ctrlRefs = core.Args[1].AsList();
            multsRaw = withKnots.Args[0].AsList();
            knotsRaw = withKnots.Args[1].AsList();
        }
        else
        {
            degree = (int)withKnots.Args[1].AsInt();
            ctrlRefs = withKnots.Args[2].AsList();
            multsRaw = withKnots.Args[6].AsList();
            knotsRaw = withKnots.Args[7].AsList();
        }

        var ctrl = new Vector3D[ctrlRefs.Count];
        for (int i = 0; i < ctrlRefs.Count; i++) ctrl[i] = Point(ctrlRefs[i].AsRef());

        var mults = multsRaw.Select(v => v.AsInt()).ToArray();
        var knotValues = knotsRaw.Select(v => v.AsReal()).ToArray();
        var flat = Nurbs.ExpandKnots(mults, knotValues, ctrl.Length, degree, inst.Id);

        double[] weights;
        var rational = inst.Find("RATIONAL_B_SPLINE_CURVE");
        if (rational is not null)
        {
            weights = rational.Args[0].AsList().Select(v => v.AsReal()).ToArray();
            ValidateWeights(weights, ctrl.Length, inst.Id);
        }
        else
        {
            weights = Ones(ctrl.Length);
        }
        return new StepBSplineCurve(inst.Id, degree, ctrl, flat, weights);
    }

    // ---------------- surfaces ----------------

    /// <summary>Resolves a surface used by face #<paramref name="faceId"/> (for error context).</summary>
    public StepSurface Surface(int id, int faceId) => Memo<StepSurface>(id, inst =>
    {
        // The trimming loops define the face extent; the redundant rectangle is unwrapped.
        var trimmed = inst.Find("RECTANGULAR_TRIMMED_SURFACE");
        if (trimmed is not null) return Surface(trimmed.Args[1].AsRef(), faceId);

        var plane = inst.Find("PLANE");
        if (plane is not null) return new StepPlane(Placement(plane.Args[1].AsRef()));

        var cylinder = inst.Find("CYLINDRICAL_SURFACE");
        if (cylinder is not null)
            return new StepCylinder(Placement(cylinder.Args[1].AsRef()), cylinder.Args[2].AsReal() * _scale);

        var cone = inst.Find("CONICAL_SURFACE");
        if (cone is not null)
            return new StepCone(Placement(cone.Args[1].AsRef()),
                cone.Args[2].AsReal() * _scale, cone.Args[3].AsReal()); // semi-angle is plane angle — unscaled

        var sphere = inst.Find("SPHERICAL_SURFACE");
        if (sphere is not null)
            return new StepSphere(Placement(sphere.Args[1].AsRef()), sphere.Args[2].AsReal() * _scale);

        var torus = inst.Find("TOROIDAL_SURFACE");
        if (torus is not null)
        {
            double major = torus.Args[2].AsReal() * _scale;
            double minor = torus.Args[3].AsReal() * _scale;
            if (minor >= major)
                throw new StepUnsupportedEntityException(id, "TOROIDAL_SURFACE",
                    $"self-intersecting torus (minor {minor} ≥ major {major}) on face #{faceId}");
            return new StepTorus(Placement(torus.Args[1].AsRef()), major, minor);
        }

        var extrusion = inst.Find("SURFACE_OF_LINEAR_EXTRUSION");
        if (extrusion is not null)
            return new StepLinearExtrusionSurface(id,
                Curve(extrusion.Args[1].AsRef(), edgeId: faceId), Vector(extrusion.Args[2].AsRef()));

        var revolution = inst.Find("SURFACE_OF_REVOLUTION");
        if (revolution is not null)
        {
            // Axis is an AXIS1_PLACEMENT: (name, location, direction).
            var axis = _file.Get(revolution.Args[2].AsRef());
            var axisRec = Require(axis, "AXIS1_PLACEMENT");
            var axisDir = axisRec.Args[2].IsNull ? Vector3D.UnitZ : Direction(axisRec.Args[2].AsRef());
            return new StepRevolutionSurface(id,
                Curve(revolution.Args[1].AsRef(), edgeId: faceId),
                Point(axisRec.Args[1].AsRef()), axisDir);
        }

        if (inst.Has("B_SPLINE_SURFACE_WITH_KNOTS") || inst.Has("B_SPLINE_SURFACE"))
            return BSplineSurface(inst);

        throw new StepUnsupportedEntityException(id, inst.Keyword, $"surface of face #{faceId}");
    });

    private StepBSplineSurface BSplineSurface(StepInstance inst)
    {
        // Simple: B_SPLINE_SURFACE_WITH_KNOTS(name, degU, degV, grid, form, closedU,
        // closedV, selfint, multsU, multsV, knotsU, knotsV, spec). Complex distributes:
        // B_SPLINE_SURFACE(degU, degV, grid, form, closedU, closedV, selfint) +
        // B_SPLINE_SURFACE_WITH_KNOTS(multsU, multsV, knotsU, knotsV, spec) +
        // RATIONAL_B_SPLINE_SURFACE(weights grid).
        var withKnots = inst.Find("B_SPLINE_SURFACE_WITH_KNOTS")
                        ?? throw new StepUnsupportedEntityException(inst.Id, inst.Keyword,
                            "B-spline surface without explicit knots");

        int degU, degV;
        IReadOnlyList<StepValue> grid, multsU, multsV, knotsU, knotsV;
        if (inst.IsComplex && withKnots.Args.Count == 5)
        {
            var core = inst.Find("B_SPLINE_SURFACE")
                       ?? throw new StepImportException(
                           $"#{inst.Id}: complex B-spline surface lacks the B_SPLINE_SURFACE record");
            degU = (int)core.Args[0].AsInt();
            degV = (int)core.Args[1].AsInt();
            grid = core.Args[2].AsList();
            multsU = withKnots.Args[0].AsList();
            multsV = withKnots.Args[1].AsList();
            knotsU = withKnots.Args[2].AsList();
            knotsV = withKnots.Args[3].AsList();
        }
        else
        {
            degU = (int)withKnots.Args[1].AsInt();
            degV = (int)withKnots.Args[2].AsInt();
            grid = withKnots.Args[3].AsList();
            multsU = withKnots.Args[8].AsList();
            multsV = withKnots.Args[9].AsList();
            knotsU = withKnots.Args[10].AsList();
            knotsV = withKnots.Args[11].AsList();
        }

        int countU = grid.Count;
        if (countU == 0) throw new StepImportException($"#{inst.Id}: empty control grid");
        int countV = grid[0].AsList().Count;
        var ctrl = new Vector3D[countU * countV];
        for (int iu = 0; iu < countU; iu++)
        {
            var row = grid[iu].AsList();
            if (row.Count != countV)
                throw new StepImportException($"#{inst.Id}: ragged control grid (row {iu})");
            for (int iv = 0; iv < countV; iv++) ctrl[iu * countV + iv] = Point(row[iv].AsRef());
        }

        var flatU = Nurbs.ExpandKnots(multsU.Select(v => v.AsInt()).ToArray(),
            knotsU.Select(v => v.AsReal()).ToArray(), countU, degU, inst.Id);
        var flatV = Nurbs.ExpandKnots(multsV.Select(v => v.AsInt()).ToArray(),
            knotsV.Select(v => v.AsReal()).ToArray(), countV, degV, inst.Id);

        double[] weights;
        var rational = inst.Find("RATIONAL_B_SPLINE_SURFACE");
        if (rational is not null)
        {
            var wgrid = rational.Args[0].AsList();
            weights = new double[countU * countV];
            if (wgrid.Count != countU)
                throw new StepImportException($"#{inst.Id}: weight grid rows ({wgrid.Count}) ≠ control rows ({countU})");
            for (int iu = 0; iu < countU; iu++)
            {
                var row = wgrid[iu].AsList();
                for (int iv = 0; iv < countV; iv++) weights[iu * countV + iv] = row[iv].AsReal();
            }
            ValidateWeights(weights, weights.Length, inst.Id);
        }
        else
        {
            weights = Ones(countU * countV);
        }
        return new StepBSplineSurface(inst.Id, degU, degV, countU, countV, ctrl, flatU, flatV, weights);
    }

    // ---------------- topology ----------------

    public StepVertex Vertex(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "VERTEX_POINT");
        return new StepVertex(id, Point(rec.Args[1].AsRef()));
    });

    public StepEdge Edge(int id) => Memo(id, inst =>
    {
        var rec = Require(inst, "EDGE_CURVE");
        return new StepEdge(id,
            Vertex(rec.Args[1].AsRef()),
            Vertex(rec.Args[2].AsRef()),
            Curve(rec.Args[3].AsRef(), id),
            AsBool(rec.Args[4], id));
    });

    public StepLoop Loop(int id) => Memo(id, inst =>
    {
        var edgeLoop = inst.Find("EDGE_LOOP");
        if (edgeLoop is not null)
        {
            var uses = new List<StepEdgeUse>();
            foreach (var useRef in edgeLoop.Args[1].AsList())
            {
                var useInst = _file.Get(useRef.AsRef());
                var oriented = Require(useInst, "ORIENTED_EDGE");
                // ORIENTED_EDGE(name, start*, end*, edge, orientation)
                uses.Add(new StepEdgeUse(Edge(oriented.Args[3].AsRef()), AsBool(oriented.Args[4], useInst.Id)));
            }
            return new StepLoop(id, uses);
        }
        var vertexLoop = inst.Find("VERTEX_LOOP");
        if (vertexLoop is not null)
            return new StepLoop(id, Array.Empty<StepEdgeUse>(), Vertex(vertexLoop.Args[1].AsRef()));

        throw new StepUnsupportedEntityException(id, inst.Keyword,
            "face bound loop (POLY_LOOP is faceted geometry)");
    });

    public StepFace Face(int id) => Memo(id, inst =>
    {
        // ADVANCED_FACE and its supertype FACE_SURFACE share the argument layout.
        var rec = inst.Find("ADVANCED_FACE") ?? inst.Find("FACE_SURFACE")
                  ?? throw new StepUnsupportedEntityException(id, inst.Keyword, "shell face");
        var bounds = new List<StepFaceBound>();
        foreach (var boundRef in rec.Args[1].AsList())
        {
            var boundInst = _file.Get(boundRef.AsRef());
            var bound = boundInst.Find("FACE_OUTER_BOUND") ?? boundInst.Find("FACE_BOUND")
                        ?? throw new StepUnsupportedEntityException(boundInst.Id, boundInst.Keyword,
                            $"bound of face #{id}");
            bounds.Add(new StepFaceBound(
                Loop(bound.Args[1].AsRef()),
                AsBool(bound.Args[2], boundInst.Id),
                boundInst.Has("FACE_OUTER_BOUND")));
        }
        return new StepFace(id, Surface(rec.Args[2].AsRef(), id), AsBool(rec.Args[3], id), bounds);
    });

    public StepShell Shell(int id) => Memo(id, inst =>
    {
        bool orientation = true;
        var rec = inst.Find("ORIENTED_CLOSED_SHELL");
        if (rec is not null)
        {
            // ORIENTED_CLOSED_SHELL(name, cfs_faces*, closed_shell, orientation)
            orientation = AsBool(rec.Args[3], id);
            var inner = Shell(rec.Args[2].AsRef());
            return new StepShell(id, inner.Faces, orientation == inner.Orientation);
        }
        rec = Require(inst, "CLOSED_SHELL");
        var faces = new List<StepFace>();
        foreach (var faceRef in rec.Args[1].AsList()) faces.Add(Face(faceRef.AsRef()));
        return new StepShell(id, faces, orientation);
    });

    public StepSolid Solid(int id) => Memo(id, inst =>
    {
        var withVoids = inst.Find("BREP_WITH_VOIDS");
        if (withVoids is not null)
        {
            if (inst.IsComplex)
                throw new StepUnsupportedEntityException(id, "BREP_WITH_VOIDS",
                    "complex-instance encoding of a voided solid");
            // BREP_WITH_VOIDS(name, outer_shell, (void_shells))
            var voids = withVoids.Args[2].AsList().Select(v => Shell(v.AsRef())).ToList();
            return new StepSolid(id, Shell(withVoids.Args[1].AsRef()), voids);
        }
        var rec = Require(inst, "MANIFOLD_SOLID_BREP");
        return new StepSolid(id, Shell(rec.Args[1].AsRef()), Array.Empty<StepShell>());
    });

    // ---------------- helpers ----------------

    private T Memo<T>(int id, Func<StepInstance, T> resolve)
    {
        if (_memo.TryGetValue(id, out var cached)) return (T)cached;
        var result = resolve(_file.Get(id))!;
        _memo[id] = result;
        return result;
    }

    private static StepRecord Require(StepInstance inst, string keyword) =>
        inst.Find(keyword) ?? throw new StepUnsupportedEntityException(
            inst.Id, inst.Keyword, $"expected {keyword}");

    private static bool AsBool(StepValue v, int id) => v.AsEnum().ToUpperInvariant() switch
    {
        "T" => true,
        "F" => false,
        var e => throw new StepImportException($"#{id}: expected .T./.F., found .{e}.")
    };

    private static void ValidateWeights(double[] weights, int expected, int id)
    {
        if (weights.Length != expected)
            throw new StepImportException($"#{id}: {weights.Length} weights for {expected} control points");
        foreach (double w in weights)
            if (w <= 0) throw new StepImportException($"#{id}: non-positive B-spline weight {w}");
    }

    private static double[] Ones(int n)
    {
        var w = new double[n];
        Array.Fill(w, 1.0);
        return w;
    }
}
