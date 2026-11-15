using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

/// <summary>
/// STEP output (OP-96): an ISO 10303-21 exchange file under the AP214 schema
/// (AUTOMOTIVE_DESIGN), each closed body a MANIFOLD_SOLID_BREP of ADVANCED_FACEs, the rays
/// POLYLINEs. Sources, all open beside this item (plans/sources/optics/op-96-help/):
/// <list type="bullet">
/// <item><b>File structure</b> (header groups FILE_DESCRIPTION, FILE_NAME, FILE_SCHEMA; instance
/// names; complex instances as their single entities in alphabetical order; '$' for unset; reals
/// with a decimal point): the Wikipedia article "ISO 10303-21" (read 2026-11-15), whose minimal
/// AP214 example gives the product records written here verbatim in form (APPLICATION_CONTEXT
/// 'mechanical design', APPLICATION_PROTOCOL_DEFINITION 'automotive_design' 2003,
/// PRODUCT_DEFINITION_CONTEXT 'part definition' 'manufacturing', PRODUCT_DEFINITION_FORMATION,
/// PRODUCT_RELATED_PRODUCT_CATEGORY 'part') and the schema string
/// 'AUTOMOTIVE_DESIGN { 1 0 10303 214 2 1 1}'. ISO 10303-21 and the AP214 text themselves are
/// sold by ISO and were not available.</item>
/// <item><b>Entities and attribute order</b>: OpenSim's own STEP importer
/// (OpenSim.Geometry/Step/Schema/StepEntityResolver.cs, StepUnits.cs, StepAssemblyResolver.cs),
/// which reads AP203/AP214 files from commercial CAD systems; the unit and uncertainty context is
/// written exactly as its tests do (OpenSim.Tests/Geometry/Step/StepUnitsTests.cs). The geometry
/// parameterizations (SPHERICAL_SURFACE, CYLINDRICAL_SURFACE, CONICAL_SURFACE, CIRCLE) are the
/// ISO 10303-42 ones its schema classes state.</item>
/// </list>
/// <para>
/// <b>Topology.</b> A body's meridian joints become rim circles, each cut into four quarter arcs
/// at the frame's ±x and ±y, so every face is bounded by closed loops of edges that two faces
/// share in opposite directions (a closed, manifold shell). A plane cap is one PLANE face inside
/// its rim; a flat annulus one PLANE face with an outer and an inner bound; a sphere cap four
/// SPHERICAL_SURFACE faces meeting at a pole vertex on the axis along four meridian arcs; an edge
/// band four CYLINDRICAL_SURFACE or CONICAL_SURFACE faces joined by four straight lines; any other
/// cap one B_SPLINE_SURFACE_WITH_KNOTS face, a bicubic patch over the square around the rim fitted
/// to the export tolerance (<see cref="BSplineFit.FitPatch"/>) and trimmed by the rim's exact
/// circle. No face's loop winds around a periodic direction, so no seam edges are needed (the
/// importer's v1 limit, SurfaceUvMapper.CheckClosure, and a sphere's pole is reached along
/// meridians from both sides of a quarter).
/// </para>
/// <para>
/// <b>Orientation.</b> Each ADVANCED_FACE's same_sense says whether the surface's own normal
/// (ISO 10303-42: outward from a sphere's centre and a cylinder's axis, the placement's axis for
/// a plane, ∂u×∂v for a B-spline) points out of the solid, decided from the meridian's sense
/// (<see cref="RevolvedBody.MeridianSense"/>). Every outer loop is checked to run
/// counterclockwise about the outward normal (its Newell vector area, sampled along the edges)
/// and reversed if not; inner loops clockwise.
/// </para>
/// <para>
/// <b>Units.</b> The length unit of <see cref="CadWriteOptions.Unit"/>; angles in radians; the
/// export tolerance as the context's distance uncertainty, which the importer reads as the
/// accuracy B-spline faces are inverted to.
/// </para>
/// Open bodies (a mirror without a substrate) are refused: sheets are not built for STEP.
/// </summary>
public static class StepWriter
{
    /// <summary>The FILE_SCHEMA string, from the Wikipedia article's AP214 example.</summary>
    public const string Schema = "AUTOMOTIVE_DESIGN { 1 0 10303 214 2 1 1}";

    public static CadWriteReport Write(CadScene scene, string path, CadWriteOptions options)
    {
        var (text, report) = WriteText(scene, options);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return report;
    }

    public static (string Text, CadWriteReport Report) WriteText(CadScene scene, CadWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (scene.Bodies.Count == 0) throw new InvalidOperationException("STEP export: there is no body to write.");
        foreach (var body in scene.Bodies)
        {
            body.Validate();
            if (!body.IsClosed)
                throw new InvalidOperationException($"STEP export: {body.Name} is an open surface (a mirror without a substrate). Open sheets are not built for STEP; give the mirror a substrate thickness.");
        }
        var w = new Builder(options.PerUnit);
        string name = CadUnits.Ascii(options.Name);

        // Product structure (the Wikipedia article's AP214 example).
        int appContext = w.Add("APPLICATION_CONTEXT('mechanical design')");
        w.Add($"APPLICATION_PROTOCOL_DEFINITION('','automotive_design',2003,#{appContext})");
        int productContext = w.Add($"PRODUCT_CONTEXT('',#{appContext},'')");
        int product = w.Add($"PRODUCT({S(name)},{S(name)},'',(#{productContext}))");
        w.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,(#{product}))");
        int formation = w.Add($"PRODUCT_DEFINITION_FORMATION('1',$,#{product})");
        int definitionContext = w.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',#{appContext},'manufacturing')");
        int definition = w.Add($"PRODUCT_DEFINITION('0',$,#{formation},#{definitionContext})");
        int definitionShape = w.Add($"PRODUCT_DEFINITION_SHAPE('','',#{definition})");

        // Units and the uncertainty (as OpenSim.Tests/Geometry/Step/StepUnitsTests.cs writes them).
        int lengthUnit = options.Unit switch
        {
            CadLengthUnit.Millimeter => w.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))"),
            CadLengthUnit.Centimeter => w.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.CENTI.,.METRE.))"),
            CadLengthUnit.Meter => w.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.))"),
            _ => Inch(w),
        };
        int angleUnit = w.Add("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        int solidAngleUnit = w.Add("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        int uncertainty = w.Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE({F(options.Tolerance * options.PerUnit)}),#{lengthUnit},'distance_accuracy_value','export tolerance')");
        int context = w.Add($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{uncertainty}))GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lengthUnit},#{angleUnit},#{solidAngleUnit}))REPRESENTATION_CONTEXT('Context3D','3D context'))");

        var items = new List<int> { w.Placement(Vector3D.Zero, Vector3D.UnitZ, Vector3D.UnitX) };
        int faces = 0;
        double maxFit = 0;
        var notes = new List<string>();
        foreach (var body in scene.Bodies)
        {
            var solid = new SolidBuilder(w, body, options.Tolerance);
            items.Add(solid.Build());
            faces += solid.FaceCount;
            maxFit = Math.Max(maxFit, solid.MaxFitDeviation);
        }
        int brep = w.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION({S(name)},({Refs(items)}),#{context})");
        w.Add($"SHAPE_DEFINITION_REPRESENTATION(#{definitionShape},#{brep})");

        int polylines = 0;
        if (scene.Rays.Count > 0)
        {
            var curves = new List<int>();
            foreach (var ray in scene.Rays)
            {
                if (ray.Points.Count < 2) continue;
                var pts = ray.Points.Select(w.Point).ToList();
                curves.Add(w.Add($"POLYLINE({S(CadUnits.Ascii(ray.Name))},({Refs(pts)}))"));
            }
            if (curves.Count > 0)
            {
                int set = w.Add($"GEOMETRIC_CURVE_SET('rays',({Refs(curves)}))");
                int wire = w.Add($"GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION('rays',(#{set}),#{context})");
                w.Add($"SHAPE_REPRESENTATION_RELATIONSHIP('rays','',#{brep},#{wire})");
                polylines = curves.Count;
            }
        }

        double perUnit = options.PerUnit;
        string unit = options.Unit.ToString().ToLowerInvariant();
        notes.Add(FormattableString.Invariant(
            $"STEP AP214: {scene.Bodies.Count} solid(s), {faces} faces, {polylines} ray polyline(s); stated uncertainty {options.Tolerance * perUnit:g3} {unit}") +
            (maxFit > 0 ? FormattableString.Invariant($"; largest B-spline deviation from the optical surface {maxFit * perUnit:g3} {unit}.") : "; every face is analytic (no spline fit)."));

        string stamp = options.Stamp.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append("ISO-10303-21;\nHEADER;\n");
        text.Append("FILE_DESCRIPTION(('OpenSim Studio optical CAD export'),'2;1');\n");
        text.Append($"FILE_NAME({S(name)},'{stamp}',(''),(''),'OpenSim Studio','OpenSim Studio','');\n");
        text.Append($"FILE_SCHEMA(('{Schema}'));\nENDSEC;\nDATA;\n");
        text.Append(w.Data);
        text.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        return (text.ToString(), new CadWriteReport
        {
            Solids = scene.Bodies.Count, Faces = faces, Polylines = polylines, MaxFitDeviation = maxFit, Notes = notes,
        });
    }

    private static int Inch(Builder w)
    {
        int mm = w.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        int measure = w.Add($"MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#{mm})");
        int dim = w.Add("DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.)");
        return w.Add($"(CONVERSION_BASED_UNIT('INCH',#{measure})LENGTH_UNIT()NAMED_UNIT(#{dim}))");
    }

    /// <summary>A Part 21 string: quotes doubled.</summary>
    internal static string S(string text) => "'" + text.Replace("'", "''") + "'";

    internal static string Refs(IEnumerable<int> ids) => string.Join(",", ids.Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>A Part 21 real: shortest round-trip digits, always with a decimal point.</summary>
    internal static string F(double v)
    {
        if (!double.IsFinite(v)) throw new InvalidOperationException($"STEP export: a coordinate is not finite ({v}).");
        if (v == 0) return "0.";
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = e < 0 ? s : s[..e];
        string exponent = e < 0 ? "" : "E" + s[(e + 1)..];
        if (!mantissa.Contains('.')) mantissa += ".";
        return mantissa + exponent;
    }

    /// <summary>The instance table being written.</summary>
    internal sealed class Builder
    {
        private readonly StringBuilder _data = new();
        private int _next = 1;

        public Builder(double perUnit) => PerUnit = perUnit;

        public double PerUnit { get; }

        public string Data => _data.ToString();

        public int Add(string record)
        {
            int id = _next++;
            _data.Append('#').Append(id.ToString(CultureInfo.InvariantCulture)).Append('=').Append(record).Append(";\n");
            return id;
        }

        /// <summary>A CARTESIAN_POINT from a global point in meters.</summary>
        public int Point(Vector3D meters) =>
            Add($"CARTESIAN_POINT('',({F(meters.X * PerUnit)},{F(meters.Y * PerUnit)},{F(meters.Z * PerUnit)}))");

        public int Direction(Vector3D d)
        {
            var u = d.Normalized();
            return Add($"DIRECTION('',({F(u.X)},{F(u.Y)},{F(u.Z)}))");
        }

        public int Placement(Vector3D origin, Vector3D axis, Vector3D reference)
        {
            int o = Point(origin);
            int z = Direction(axis);
            int x = Direction(reference);
            return Add($"AXIS2_PLACEMENT_3D('',#{o},#{z},#{x})");
        }
    }

    /// <summary>One edge as written: its id, its vertices, and its geometry from start (s = 0)
    /// to end (s = 1) for the orientation check.</summary>
    private sealed record Edge(int Id, Func<double, Vector3D> At);

    private readonly record struct Use(Edge Edge, bool Forward);

    /// <summary>The B-rep of one closed body.</summary>
    private sealed class SolidBuilder
    {
        private const int Quarters = 4;
        private readonly Builder _w;
        private readonly RevolvedBody _body;
        private readonly double _tolerance;
        private readonly IReadOnlyList<(double R, double Z)> _joints;
        private readonly int _sense;
        private readonly Vector3D _x, _y, _z;
        private readonly List<int> _faces = new();
        private readonly Dictionary<int, (int[] Vertices, Edge[] Arcs)> _rings = new();
        private readonly Dictionary<int, int> _poles = new();

        public SolidBuilder(Builder w, RevolvedBody body, double tolerance)
        {
            _w = w;
            _body = body;
            _tolerance = tolerance;
            _joints = body.Joints();
            _sense = body.MeridianSense();
            _x = body.Placement.Col0;
            _y = body.Placement.Col1;
            _z = body.Placement.Col2;
        }

        public int FaceCount => _faces.Count;

        public double MaxFitDeviation { get; private set; }

        public int Build()
        {
            for (int p = 0; p < _body.Pieces.Count; p++)
            {
                var piece = _body.Pieces[p];
                switch (piece)
                {
                    case CapPiece cap when cap.Shape is PlaneCap: PlaneCapFace(cap, p); break;
                    case CapPiece { Shape: SphereCap sphere } cap: SphereCapFaces(cap, sphere, p); break;
                    case CapPiece cap: SplineCapFace(cap, p); break;
                    case LinePiece line when line.IsRadial: AnnulusFace(line, p); break;
                    case LinePiece line: BandFaces(line, p); break;
                }
            }
            int shell = _w.Add($"CLOSED_SHELL('',({Refs(_faces)}))");
            return _w.Add($"MANIFOLD_SOLID_BREP({S(CadUnits.Ascii(_body.Name))},#{shell})");
        }

        // ---------------- geometry helpers ----------------

        private Vector3D Local(double x, double y, double z) => _body.Placement.Apply(new Vector3D(x, y, z));

        private static (double Cos, double Sin) Quarter(int q) => (q % 4) switch
        {
            0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1),
        };

        private Vector3D Radial(double phi) => _x * Math.Cos(phi) + _y * Math.Sin(phi);

        /// <summary>The outward normal of piece p at meridian point (r, z) with the meridian
        /// tangent (dr, dz) taken in the piece's direction, at azimuth φ.</summary>
        private Vector3D Outward(double dr, double dz, double phi)
        {
            // The region lies left of the walk when the meridian is counterclockwise: outward is right.
            double nr = _sense > 0 ? dz : -dz;
            double nz = _sense > 0 ? -dr : dr;
            return (Radial(phi) * nr + _z * nz).Normalized();
        }

        private Vector3D AtMeridian(double r, double z, double phi) => _body.Placement.Apply(new Vector3D(r * Math.Cos(phi), r * Math.Sin(phi), z));

        /// <summary>The four vertices and quarter arcs of the rim circle at joint j (r &gt; 0).</summary>
        private (int[] Vertices, Edge[] Arcs) Ring(int j)
        {
            if (_rings.TryGetValue(j, out var ring)) return ring;
            var (r, z) = _joints[j];
            var vertices = new int[Quarters];
            for (int q = 0; q < Quarters; q++)
            {
                var (c, s) = Quarter(q);
                vertices[q] = _w.Add($"VERTEX_POINT('',#{_w.Point(Local(r * c, r * s, z))})");
            }
            int circle = _w.Add($"CIRCLE('',#{_w.Placement(Local(0, 0, z), _z, _x)},{F(r * _w.PerUnit)})");
            var arcs = new Edge[Quarters];
            for (int q = 0; q < Quarters; q++)
            {
                int id = _w.Add($"EDGE_CURVE('',#{vertices[q]},#{vertices[(q + 1) % Quarters]},#{circle},.T.)");
                double phi0 = q * Math.PI / 2;
                arcs[q] = new Edge(id, s => AtMeridian(r, z, phi0 + s * Math.PI / 2));
            }
            ring = (vertices, arcs);
            _rings[j] = ring;
            return ring;
        }

        private int Pole(int j)
        {
            if (_poles.TryGetValue(j, out int id)) return id;
            id = _w.Add($"VERTEX_POINT('',#{_w.Point(Local(0, 0, _joints[j].Z))})");
            _poles[j] = id;
            return id;
        }

        /// <summary>The joint index of a piece's rim end and of its axis end (−1 when none).</summary>
        private (int Rim, int Axis) Ends(int p)
        {
            int a = p, b = p + 1;
            if (_joints[a].R == 0) return (b, a);
            if (_joints[b].R == 0) return (a, b);
            return (-1, -1);
        }

        // ---------------- loops ----------------

        /// <summary>The loop's vector area (Newell), sampled along its edges.</summary>
        private static Vector3D VectorArea(IReadOnlyList<Use> loop)
        {
            const int samples = 8;
            var pts = new List<Vector3D>();
            foreach (var use in loop)
                for (int k = 0; k < samples; k++)
                {
                    double s = (double)k / samples;
                    pts.Add(use.Edge.At(use.Forward ? s : 1 - s));
                }
            var area = Vector3D.Zero;
            for (int i = 0; i < pts.Count; i++) area += Vector3D.Cross(pts[i], pts[(i + 1) % pts.Count]);
            return area * 0.5;
        }

        /// <summary>The loop, reversed if needed so it runs counterclockwise about
        /// <paramref name="normal"/> (or clockwise for an inner bound).</summary>
        private List<Use> Oriented(List<Use> loop, Vector3D normal, bool outer)
        {
            double d = Vector3D.Dot(VectorArea(loop), normal);
            if (d == 0) throw new InvalidOperationException($"STEP export: {_body.Name}: a face loop has no area.");
            if ((d > 0) == outer) return loop;
            var reversed = new List<Use>(loop.Count);
            for (int i = loop.Count - 1; i >= 0; i--) reversed.Add(new Use(loop[i].Edge, !loop[i].Forward));
            return reversed;
        }

        private int Bound(List<Use> loop, bool outer)
        {
            var uses = loop.Select(u => _w.Add($"ORIENTED_EDGE('',*,*,#{u.Edge.Id},{(u.Forward ? ".T." : ".F.")})")).ToList();
            int edgeLoop = _w.Add($"EDGE_LOOP('',({Refs(uses)}))");
            return _w.Add(outer ? $"FACE_OUTER_BOUND('',#{edgeLoop},.T.)" : $"FACE_BOUND('',#{edgeLoop},.T.)");
        }

        private void Face(IEnumerable<int> bounds, int surface, bool sameSense) =>
            _faces.Add(_w.Add($"ADVANCED_FACE('',({Refs(bounds)}),#{surface},{(sameSense ? ".T." : ".F.")})"));

        private List<Use> RimLoop(int j) => Ring(j).Arcs.Select(a => new Use(a, true)).ToList();

        // ---------------- faces ----------------

        /// <summary>The outward normal of a cap at half its rim radius.</summary>
        private Vector3D CapOutward(CapPiece cap, double phi)
        {
            double r = 0.5 * cap.RimRadius;
            double slope = cap.Shape.Slope(r);
            return cap.TowardAxis ? Outward(-1, -slope, phi) : Outward(1, slope, phi);
        }

        private void PlaneCapFace(CapPiece cap, int p)
        {
            var (rim, _) = Ends(p);
            var normal = CapOutward(cap, Math.PI / 4);
            var axis = Vector3D.Dot(normal, _z) >= 0 ? _z : -_z;
            int plane = _w.Add($"PLANE('',#{_w.Placement(Local(0, 0, cap.VertexZ), axis, _x)})");
            Face(new[] { Bound(Oriented(RimLoop(rim), axis, outer: true), outer: true) }, plane, true);
        }

        private void SplineCapFace(CapPiece cap, int p)
        {
            var (rim, _) = Ends(p);
            var fit = BSplineFit.FitPatch(cap.Shape, cap.RimRadius, _tolerance, $"{_body.Name}, {cap.Shape.Describe()}");
            MaxFitDeviation = Math.Max(MaxFitDeviation, fit.MaxDeviation);
            int n = fit.Basis.Count;
            var rows = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                var row = new List<int>(n);
                for (int k = 0; k < n; k++)
                    row.Add(_w.Point(Local(fit.Basis.Greville[i], fit.Basis.Greville[k], cap.VertexZ + fit.Z[i, k])));
                if (i > 0) rows.Append(',');
                rows.Append('(').Append(Refs(row)).Append(')');
            }
            var (values, mult) = fit.Basis.DistinctKnots();
            string m = string.Join(",", mult.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            string knots = string.Join(",", values.Select(v => F(v * _w.PerUnit)));
            int surface = _w.Add($"B_SPLINE_SURFACE_WITH_KNOTS('',3,3,({rows}),.UNSPECIFIED.,.F.,.F.,.F.,({m}),({m}),({knots}),({knots}),.UNSPECIFIED.)");
            // ∂u×∂v of the graph patch (x = u, y = v) has a positive component along the frame's z.
            var normal = CapOutward(cap, Math.PI / 4);
            bool sameSense = Vector3D.Dot(normal, _z) > 0;
            Face(new[] { Bound(Oriented(RimLoop(rim), normal, outer: true), outer: true) }, surface, sameSense);
        }

        private void SphereCapFaces(CapPiece cap, SphereCap sphere, int p)
        {
            var (rim, axisJoint) = Ends(p);
            double radius = Math.Abs(sphere.Radius);
            var centre = Local(0, 0, cap.VertexZ + sphere.Radius);
            int surface = _w.Add($"SPHERICAL_SURFACE('',#{_w.Placement(centre, _z, _x)},{F(radius * _w.PerUnit)})");
            int pole = Pole(axisJoint);
            var ring = Ring(rim);
            var (rr, zr) = _joints[rim];
            var poleAt = Local(0, 0, cap.VertexZ);

            // Meridian arcs from the rim vertex at quarter q to the pole: a CIRCLE about the
            // sphere's centre in the meridian plane, run the short way (same_sense from the angle).
            var meridians = new Edge[Quarters];
            for (int q = 0; q < Quarters; q++)
            {
                double phi = q * Math.PI / 2;
                var xc = Radial(phi);
                var axis = Vector3D.Cross(_z, xc); // the tangential direction
                var yc = Vector3D.Cross(axis, xc);
                var start = AtMeridian(rr, zr, phi);
                double t0 = Angle(start - centre, xc, yc), t1 = Angle(poleAt - centre, xc, yc);
                double delta = t1 - t0;
                while (delta < 0) delta += 2 * Math.PI;
                while (delta >= 2 * Math.PI) delta -= 2 * Math.PI;
                bool same = delta < Math.PI;
                double sweep = same ? delta : delta - 2 * Math.PI;
                int circle = _w.Add($"CIRCLE('',#{_w.Placement(centre, axis, xc)},{F(radius * _w.PerUnit)})");
                int id = _w.Add($"EDGE_CURVE('',#{ring.Vertices[q]},#{pole},#{circle},{(same ? ".T." : ".F.")})");
                meridians[q] = new Edge(id, s =>
                {
                    double t = t0 + s * sweep;
                    return centre + (xc * Math.Cos(t) + yc * Math.Sin(t)) * radius;
                });
            }

            bool sameSense = Vector3D.Dot(CapPoint(cap, Math.PI / 4) - centre, CapOutward(cap, Math.PI / 4)) > 0;
            for (int q = 0; q < Quarters; q++)
            {
                double mid = (q + 0.5) * Math.PI / 2;
                var loop = new List<Use>
                {
                    new(ring.Arcs[q], true),
                    new(meridians[(q + 1) % Quarters], true),
                    new(meridians[q], false),
                };
                Face(new[] { Bound(Oriented(loop, CapOutward(cap, mid), outer: true), outer: true) }, surface, sameSense);
            }
        }

        private Vector3D CapPoint(CapPiece cap, double phi)
        {
            double r = 0.5 * cap.RimRadius;
            return AtMeridian(r, cap.VertexZ + cap.Shape.Sag(r), phi);
        }

        private static double Angle(Vector3D v, Vector3D x, Vector3D y) => Math.Atan2(Vector3D.Dot(v, y), Vector3D.Dot(v, x));

        private void AnnulusFace(LinePiece line, int p)
        {
            int a = p, b = p + 1;
            int outerJoint = _joints[a].R > _joints[b].R ? a : b, innerJoint = outerJoint == a ? b : a;
            var normal = Outward(line.R1 - line.R0, 0, Math.PI / 4);
            var axis = Vector3D.Dot(normal, _z) >= 0 ? _z : -_z;
            int plane = _w.Add($"PLANE('',#{_w.Placement(Local(0, 0, line.Z0), axis, _x)})");
            var outer = Bound(Oriented(RimLoop(outerJoint), axis, outer: true), outer: true);
            var inner = Bound(Oriented(RimLoop(innerJoint), axis, outer: false), outer: false);
            Face(new[] { outer, inner }, plane, true);
        }

        private void BandFaces(LinePiece line, int p)
        {
            int a = p, b = p + 1;
            var ringA = Ring(a);
            var ringB = Ring(b);
            int surface;
            Func<Vector3D, double, bool> natural; // same_sense from the natural normal at a point and azimuth
            if (line.IsAxial)
            {
                surface = _w.Add($"CYLINDRICAL_SURFACE('',#{_w.Placement(Local(0, 0, 0), _z, _x)},{F(line.R0 * _w.PerUnit)})");
                natural = (n, phi) => Vector3D.Dot(Radial(phi), n) > 0;
            }
            else
            {
                // The small end at the placement's origin and its axis toward the large end, so the
                // semi-angle is in (0, π/2) as ISO 10303-42 requires.
                bool startSmall = line.R0 < line.R1;
                double rs = startSmall ? line.R0 : line.R1, zs = startSmall ? line.Z0 : line.Z1;
                double rb = startSmall ? line.R1 : line.R0, zb = startSmall ? line.Z1 : line.Z0;
                var axis = zb > zs ? _z : -_z;
                double tan = (rb - rs) / Math.Abs(zb - zs);
                surface = _w.Add($"CONICAL_SURFACE('',#{_w.Placement(Local(0, 0, zs), axis, _x)},{F(rs * _w.PerUnit)},{F(Math.Atan(tan))})");
                natural = (n, phi) => Vector3D.Dot(Radial(phi) - axis * tan, n) > 0;
            }

            var lines = new Edge[Quarters];
            for (int q = 0; q < Quarters; q++)
            {
                var (c, s) = Quarter(q);
                var start = Local(line.R0 * c, line.R0 * s, line.Z0);
                var end = Local(line.R1 * c, line.R1 * s, line.Z1);
                var d = end - start;
                int dir = _w.Direction(d);
                int vector = _w.Add($"VECTOR('',#{dir},{F(d.Length * _w.PerUnit)})");
                int curve = _w.Add($"LINE('',#{_w.Point(start)},#{vector})");
                int id = _w.Add($"EDGE_CURVE('',#{ringA.Vertices[q]},#{ringB.Vertices[q]},#{curve},.T.)");
                lines[q] = new Edge(id, t => start + d * t);
            }

            for (int q = 0; q < Quarters; q++)
            {
                double mid = (q + 0.5) * Math.PI / 2;
                var normal = Outward(line.R1 - line.R0, line.Z1 - line.Z0, mid);
                var loop = new List<Use>
                {
                    new(ringA.Arcs[q], true),
                    new(lines[(q + 1) % Quarters], true),
                    new(ringB.Arcs[q], false),
                    new(lines[q], false),
                };
                Face(new[] { Bound(Oriented(loop, normal, outer: true), outer: true) }, surface, natural(normal, mid));
            }
        }
    }
}
