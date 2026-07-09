using System.Globalization;
using System.Text;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// Programmatic STEP fixture generator — the STL tests synthesize their inputs the same
/// way. Fixtures are exact AP214 B-rep text built from first principles (windings CCW
/// about each face's outward normal), so golden assertions can be sharp.
/// </summary>
internal static class StepFixtures
{
    public enum Unit { Millimetre, Metre, Inch }

    internal sealed class Builder
    {
        private readonly StringBuilder _data = new();
        private int _next = 1;

        public int Add(FormattableString record)
        {
            int id = _next++;
            _data.Append('#').Append(id).Append('=')
                 .Append(FormattableString.Invariant(record)).Append(";\n");
            return id;
        }

        public string Render() =>
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
            "FILE_NAME('fixture','2026-07-08',(''),(''),'','StepFixtures','');\n" +
            "FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }'));\nENDSEC;\nDATA;\n" +
            _data + "ENDSEC;\nEND-ISO-10303-21;\n";

        public void AddUnits(Unit unit)
        {
            int length;
            switch (unit)
            {
                case Unit.Millimetre:
                    length = Add($"(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
                    break;
                case Unit.Metre:
                    length = Add($"(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.))");
                    break;
                default: // inch = 25.4 mm via CONVERSION_BASED_UNIT
                    int mm = Add($"(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
                    int measure = Add($"MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#{mm})");
                    int dim = Add($"DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.)");
                    length = Add($"(CONVERSION_BASED_UNIT('INCH',#{measure})LENGTH_UNIT()NAMED_UNIT(#{dim}))");
                    break;
            }
            Add($"(GLOBAL_UNIT_ASSIGNED_CONTEXT((#{length}))REPRESENTATION_CONTEXT('',''))");
        }

        public int Point(double x, double y, double z) =>
            Add($"CARTESIAN_POINT('',({x:R},{y:R},{z:R}))");

        public int Direction(double x, double y, double z) =>
            Add($"DIRECTION('',({x:R},{y:R},{z:R}))");

        public int Placement(int originPoint, (double x, double y, double z) zAxis,
            (double x, double y, double z) xAxis)
        {
            int z = Direction(zAxis.x, zAxis.y, zAxis.z);
            int x = Direction(xAxis.x, xAxis.y, xAxis.z);
            return Add($"AXIS2_PLACEMENT_3D('',#{originPoint},#{z},#{x})");
        }
    }

    /// <summary>An axis-aligned box with its corner at <paramref name="origin"/>. Returns the solid's #id.</summary>
    internal static int EmitBox(Builder b, (double x, double y, double z) origin,
        double dx, double dy, double dz, bool flipTopFace = false)
    {
        var (ox, oy, oz) = origin;
        var corners = new (double x, double y, double z)[]
        {
            (ox, oy, oz), (ox + dx, oy, oz), (ox + dx, oy + dy, oz), (ox, oy + dy, oz),
            (ox, oy, oz + dz), (ox + dx, oy, oz + dz), (ox + dx, oy + dy, oz + dz), (ox, oy + dy, oz + dz)
        };
        var points = new int[8];
        var vertices = new int[8];
        for (int i = 0; i < 8; i++)
        {
            points[i] = b.Point(corners[i].x, corners[i].y, corners[i].z);
            vertices[i] = b.Add($"VERTEX_POINT('',#{points[i]})");
        }

        // Edge k connects corners edgeDefs[k]: 0-3 bottom ring, 4-7 top ring, 8-11 risers.
        var edgeDefs = new (int a, int bIdx)[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7)
        };
        var edges = new int[12];
        for (int k = 0; k < 12; k++)
        {
            var (a, bi) = edgeDefs[k];
            double ex = corners[bi].x - corners[a].x;
            double ey = corners[bi].y - corners[a].y;
            double ez = corners[bi].z - corners[a].z;
            double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            int dir = b.Direction(ex / len, ey / len, ez / len);
            int vec = b.Add($"VECTOR('',#{dir},{len:R})");
            int line = b.Add($"LINE('',#{points[a]},#{vec})");
            edges[k] = b.Add($"EDGE_CURVE('',#{vertices[a]},#{vertices[bi]},#{line},.T.)");
        }

        // Each face: loop traversal CCW about the outward normal, plane frame Z = normal.
        var faces = new (int originCorner, (double, double, double) z, (double, double, double) x,
            (int edge, bool fwd)[] loop)[]
        {
            (0, (0, 0, -1), (1, 0, 0), new[] { (3, false), (2, false), (1, false), (0, false) }), // bottom
            (4, (0, 0, 1), (1, 0, 0), new[] { (4, true), (5, true), (6, true), (7, true) }),      // top
            (0, (0, -1, 0), (1, 0, 0), new[] { (0, true), (9, true), (4, false), (8, false) }),   // y = 0
            (1, (1, 0, 0), (0, 1, 0), new[] { (1, true), (10, true), (5, false), (9, false) }),   // x = dx
            (2, (0, 1, 0), (0, 0, 1), new[] { (2, true), (11, true), (6, false), (10, false) }),  // y = dy
            (3, (-1, 0, 0), (0, 0, 1), new[] { (3, true), (8, true), (7, false), (11, false) })   // x = 0
        };

        var faceIds = new List<int>();
        for (int f = 0; f < faces.Length; f++)
        {
            var (corner, z, x, loop) = faces[f];
            var oriented = loop.Select(e =>
                b.Add($"ORIENTED_EDGE('',*,*,#{edges[e.edge]},{(e.fwd ? ".T." : ".F.")})")).ToList();
            int edgeLoop = b.Add($"EDGE_LOOP('',({string.Join(",", oriented.Select(i => $"#{i}"))}))");
            int bound = b.Add($"FACE_OUTER_BOUND('',#{edgeLoop},.T.)");

            bool flip = flipTopFace && f == 1;
            // A flipped face states the SAME outward orientation two ways at once: the
            // plane normal reversed AND same_sense = .F. — the tessellator must compose
            // both and still emit outward triangles.
            int placement = b.Placement(points[corner], flip ? (0, 0, -1) : z, x);
            int plane = b.Add($"PLANE('',#{placement})");
            faceIds.Add(b.Add($"ADVANCED_FACE('',(#{bound}),#{plane},{(flip ? ".F." : ".T.")})"));
        }

        int shell = b.Add($"CLOSED_SHELL('',({string.Join(",", faceIds.Select(i => $"#{i}"))}))");
        return b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
    }

    public static string Box(double dx, double dy, double dz,
        Unit unit = Unit.Millimetre, bool flipTopFace = false)
    {
        var b = new Builder();
        b.AddUnits(unit);
        EmitBox(b, (0, 0, 0), dx, dy, dz, flipTopFace);
        return b.Render();
    }

    /// <summary>
    /// A full cylinder (radius r, height h, axis +z, base at z = 0) with the OCC-style
    /// seam topology: the lateral face's loop is [base ring, seam up, top ring reversed,
    /// seam down] — the same seam edge twice, once each way.
    /// </summary>
    public static string Cylinder(double r, double h)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);

        int pb = b.Point(r, 0, 0);
        int pt = b.Point(r, 0, h);
        int vb = b.Add($"VERTEX_POINT('',#{pb})");
        int vt = b.Add($"VERTEX_POINT('',#{pt})");

        int frameB = b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0));
        int frameT = b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0));
        int circleB = b.Add($"CIRCLE('',#{frameB},{r:R})");
        int circleT = b.Add($"CIRCLE('',#{frameT},{r:R})");
        int ringB = b.Add($"EDGE_CURVE('',#{vb},#{vb},#{circleB},.T.)");
        int ringT = b.Add($"EDGE_CURVE('',#{vt},#{vt},#{circleT},.T.)");

        int seamDir = b.Direction(0, 0, 1);
        int seamVec = b.Add($"VECTOR('',#{seamDir},{h:R})");
        int seamLine = b.Add($"LINE('',#{pb},#{seamVec})");
        int seam = b.Add($"EDGE_CURVE('',#{vb},#{vt},#{seamLine},.T.)");

        int oe1 = b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.T.)");
        int oe2 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.T.)");
        int oe3 = b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.F.)");
        int oe4 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.F.)");
        int lateralLoop = b.Add($"EDGE_LOOP('',(#{oe1},#{oe2},#{oe3},#{oe4}))");
        int lateralBound = b.Add($"FACE_OUTER_BOUND('',#{lateralLoop},.T.)");
        int cylSurface = b.Add($"CYLINDRICAL_SURFACE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{r:R})");
        int lateral = b.Add($"ADVANCED_FACE('',(#{lateralBound}),#{cylSurface},.T.)");

        int bottomLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.F.)")}))");
        int bottomBound = b.Add($"FACE_OUTER_BOUND('',#{bottomLoop},.T.)");
        int bottomPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, -1), (1, 0, 0))})");
        int bottom = b.Add($"ADVANCED_FACE('',(#{bottomBound}),#{bottomPlane},.T.)");

        int topLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.T.)")}))");
        int topBound = b.Add($"FACE_OUTER_BOUND('',#{topLoop},.T.)");
        int topPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0))})");
        int top = b.Add($"ADVANCED_FACE('',(#{topBound}),#{topPlane},.T.)");

        int shell = b.Add($"CLOSED_SHELL('',(#{lateral},#{bottom},#{top}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>A conical frustum: radius r1 at z = 0, r2 at z = h, seam topology as the cylinder.</summary>
    public static string ConeFrustum(double r1, double r2, double h)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);
        double semiAngle = Math.Atan((r2 - r1) / h);

        int pb = b.Point(r1, 0, 0);
        int pt = b.Point(r2, 0, h);
        int vb = b.Add($"VERTEX_POINT('',#{pb})");
        int vt = b.Add($"VERTEX_POINT('',#{pt})");

        int circleB = b.Add($"CIRCLE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{r1:R})");
        int circleT = b.Add($"CIRCLE('',#{b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0))},{r2:R})");
        int ringB = b.Add($"EDGE_CURVE('',#{vb},#{vb},#{circleB},.T.)");
        int ringT = b.Add($"EDGE_CURVE('',#{vt},#{vt},#{circleT},.T.)");

        double slant = Math.Sqrt((r2 - r1) * (r2 - r1) + h * h);
        int seamDir = b.Direction((r2 - r1) / slant, 0, h / slant);
        int seamVec = b.Add($"VECTOR('',#{seamDir},{slant:R})");
        int seam = b.Add($"EDGE_CURVE('',#{vb},#{vt},#{b.Add($"LINE('',#{pb},#{seamVec})")},.T.)");

        int oe1 = b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.T.)");
        int oe2 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.T.)");
        int oe3 = b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.F.)");
        int oe4 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.F.)");
        int lateralLoop = b.Add($"EDGE_LOOP('',(#{oe1},#{oe2},#{oe3},#{oe4}))");
        int coneSurface = b.Add(
            $"CONICAL_SURFACE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{r1:R},{semiAngle:R})");
        int lateral = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{lateralLoop},.T.)")}),#{coneSurface},.T.)");

        int bottomLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.F.)")}))");
        int bottomPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, -1), (1, 0, 0))})");
        int bottom = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{bottomLoop},.T.)")}),#{bottomPlane},.T.)");

        int topLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.T.)")}))");
        int topPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0))})");
        int top = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{topLoop},.T.)")}),#{topPlane},.T.)");

        int shell = b.Add($"CLOSED_SHELL('',(#{lateral},#{bottom},#{top}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>
    /// A full cone: base radius R at z = 0, apex at (0,0,h). The cone frame points down
    /// (−z) so the ISO semi-angle stays positive; the apex is a parameterization pole —
    /// this fixture exercises the NaN-u continuity path.
    /// </summary>
    public static string FullCone(double radius, double h)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);
        double semiAngle = Math.Atan(radius / h);

        int pb = b.Point(radius, 0, 0);
        int pa = b.Point(0, 0, h);
        int vb = b.Add($"VERTEX_POINT('',#{pb})");
        int va = b.Add($"VERTEX_POINT('',#{pa})");

        int circleB = b.Add($"CIRCLE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{radius:R})");
        int ringB = b.Add($"EDGE_CURVE('',#{vb},#{vb},#{circleB},.T.)");

        double slant = Math.Sqrt(radius * radius + h * h);
        int seamDir = b.Direction(-radius / slant, 0, h / slant);
        int seamVec = b.Add($"VECTOR('',#{seamDir},{slant:R})");
        int seam = b.Add($"EDGE_CURVE('',#{vb},#{va},#{b.Add($"LINE('',#{pb},#{seamVec})")},.T.)");

        int oe1 = b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.T.)");
        int oe2 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.T.)");
        int oe3 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.F.)");
        int lateralLoop = b.Add($"EDGE_LOOP('',(#{oe1},#{oe2},#{oe3}))");
        // Frame Z = −z so the semi-angle stays in (0, π/2): radius R at the base (v = 0)
        // shrinks to 0 at the apex (v = −h).
        int coneSurface = b.Add(
            $"CONICAL_SURFACE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, -1), (1, 0, 0))},{radius:R},{semiAngle:R})");
        int lateral = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{lateralLoop},.T.)")}),#{coneSurface},.T.)");

        int bottomLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.F.)")}))");
        int bottomPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, -1), (1, 0, 0))})");
        int bottom = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{bottomLoop},.T.)")}),#{bottomPlane},.T.)");

        int shell = b.Add($"CLOSED_SHELL('',(#{lateral},#{bottom}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>A full sphere bounded only by a vertex loop — the full-domain synthesis path.</summary>
    public static string Sphere(double r)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);
        int pole = b.Point(0, 0, r);
        int vertex = b.Add($"VERTEX_POINT('',#{pole})");
        int loop = b.Add($"VERTEX_LOOP('',#{vertex})");
        int bound = b.Add($"FACE_BOUND('',#{loop},.T.)");
        int surface = b.Add($"SPHERICAL_SURFACE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{r:R})");
        int face = b.Add($"ADVANCED_FACE('',(#{bound}),#{surface},.T.)");
        int shell = b.Add($"CLOSED_SHELL('',(#{face}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>A full torus (major R, minor r) bounded only by a vertex loop.</summary>
    public static string Torus(double major, double minor)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);
        int seed = b.Point(major + minor, 0, 0);
        int vertex = b.Add($"VERTEX_POINT('',#{seed})");
        int loop = b.Add($"VERTEX_LOOP('',#{vertex})");
        int bound = b.Add($"FACE_BOUND('',#{loop},.T.)");
        int surface = b.Add(
            $"TOROIDAL_SURFACE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{major:R},{minor:R})");
        int face = b.Add($"ADVANCED_FACE('',(#{bound}),#{surface},.T.)");
        int shell = b.Add($"CLOSED_SHELL('',(#{face}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>
    /// A "pillow": a bicubic B-spline patch over [0,a]×[0,b] whose four interior control
    /// points are lifted to z = bump while every boundary control point stays at z = 0 —
    /// so the four boundary curves are exact segments of the base rectangle and a flat
    /// bottom plane closes the solid. The surface is written as a RATIONAL complex
    /// instance (weights 1) to exercise the distributed-attribute parsing path with real
    /// topology around it.
    /// </summary>
    public static string BSplinePillow(double a, double bDim, double bump)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);

        // 4×4 control grid: x = a·i/3, y = b·j/3 (equally spaced ⇒ x(u) = a·u exactly).
        var grid = new int[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double z = i is 1 or 2 && j is 1 or 2 ? bump : 0;
                grid[i, j] = b.Point(a * i / 3, bDim * j / 3, z);
            }

        string Rows(Func<int, int, int> pick) =>
            string.Join(",", Enumerable.Range(0, 4).Select(i =>
                "(" + string.Join(",", Enumerable.Range(0, 4).Select(j => $"#{pick(i, j)}")) + ")"));

        const string weights = "((1.,1.,1.,1.),(1.,1.,1.,1.),(1.,1.,1.,1.),(1.,1.,1.,1.))";
        int surface = b.Add(
            $"(B_SPLINE_SURFACE(3,3,({Rows((i, j) => grid[i, j])}),.UNSPECIFIED.,.F.,.F.,.F.)B_SPLINE_SURFACE_WITH_KNOTS((4,4),(4,4),(0.,1.),(0.,1.),.UNSPECIFIED.)RATIONAL_B_SPLINE_SURFACE({weights})BOUNDED_SURFACE()GEOMETRIC_REPRESENTATION_ITEM()REPRESENTATION_ITEM('')SURFACE())");

        int Curve(params int[] ctrl) => b.Add(
            $"B_SPLINE_CURVE_WITH_KNOTS('',3,({string.Join(",", ctrl.Select(c => $"#{c}"))}),.UNSPECIFIED.,.F.,.F.,(4,4),(0.,1.),.UNSPECIFIED.)");

        int v00 = b.Add($"VERTEX_POINT('',#{grid[0, 0]})");
        int v10 = b.Add($"VERTEX_POINT('',#{grid[3, 0]})");
        int v11 = b.Add($"VERTEX_POINT('',#{grid[3, 3]})");
        int v01 = b.Add($"VERTEX_POINT('',#{grid[0, 3]})");

        int eBottom = b.Add($"EDGE_CURVE('',#{v00},#{v10},#{Curve(grid[0, 0], grid[1, 0], grid[2, 0], grid[3, 0])},.T.)");
        int eRight = b.Add($"EDGE_CURVE('',#{v10},#{v11},#{Curve(grid[3, 0], grid[3, 1], grid[3, 2], grid[3, 3])},.T.)");
        int eTop = b.Add($"EDGE_CURVE('',#{v01},#{v11},#{Curve(grid[0, 3], grid[1, 3], grid[2, 3], grid[3, 3])},.T.)");
        int eLeft = b.Add($"EDGE_CURVE('',#{v00},#{v01},#{Curve(grid[0, 0], grid[0, 1], grid[0, 2], grid[0, 3])},.T.)");

        int t1 = b.Add($"ORIENTED_EDGE('',*,*,#{eBottom},.T.)");
        int t2 = b.Add($"ORIENTED_EDGE('',*,*,#{eRight},.T.)");
        int t3 = b.Add($"ORIENTED_EDGE('',*,*,#{eTop},.F.)");
        int t4 = b.Add($"ORIENTED_EDGE('',*,*,#{eLeft},.F.)");
        int topLoop = b.Add($"EDGE_LOOP('',(#{t1},#{t2},#{t3},#{t4}))");
        int topFace = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{topLoop},.T.)")}),#{surface},.T.)");

        int b1 = b.Add($"ORIENTED_EDGE('',*,*,#{eLeft},.T.)");
        int b2 = b.Add($"ORIENTED_EDGE('',*,*,#{eTop},.T.)");
        int b3 = b.Add($"ORIENTED_EDGE('',*,*,#{eRight},.F.)");
        int b4 = b.Add($"ORIENTED_EDGE('',*,*,#{eBottom},.F.)");
        int bottomLoop = b.Add($"EDGE_LOOP('',(#{b1},#{b2},#{b3},#{b4}))");
        int bottomPlane = b.Add($"PLANE('',#{b.Placement(grid[0, 0], (0, 0, -1), (1, 0, 0))})");
        int bottomFace = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{bottomLoop},.T.)")}),#{bottomPlane},.T.)");

        int shell = b.Add($"CLOSED_SHELL('',(#{topFace},#{bottomFace}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>
    /// A barrel: a circular arc from (r0,0,0) to (r0,0,h) bulging to r0+bulge at
    /// mid-height, revolved about the z axis (SURFACE_OF_REVOLUTION), with the seam
    /// topology of the cylinder and flat caps.
    /// </summary>
    public static string Barrel(double r0, double h, double bulge)
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);

        // Arc centre/radius from the bulge: d = distance centre→r0 along x.
        double d = (h * h / 4 - bulge * bulge) / (2 * bulge);
        double cx = r0 - d;
        double arcR = d + bulge;

        int pb = b.Point(r0, 0, 0);
        int pt = b.Point(r0, 0, h);
        int vb = b.Add($"VERTEX_POINT('',#{pb})");
        int vt = b.Add($"VERTEX_POINT('',#{pt})");

        // Profile arc in the xz half-plane: circle frame Z = −y so the plane is y = 0.
        int arc = b.Add($"CIRCLE('',#{b.Placement(b.Point(cx, 0, h / 2), (0, -1, 0), (1, 0, 0))},{arcR:R})");
        int seam = b.Add($"EDGE_CURVE('',#{vb},#{vt},#{arc},.T.)");

        int circleB = b.Add($"CIRCLE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0))},{r0:R})");
        int circleT = b.Add($"CIRCLE('',#{b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0))},{r0:R})");
        int ringB = b.Add($"EDGE_CURVE('',#{vb},#{vb},#{circleB},.T.)");
        int ringT = b.Add($"EDGE_CURVE('',#{vt},#{vt},#{circleT},.T.)");

        int axisDir = b.Direction(0, 0, 1);
        int axis = b.Add($"AXIS1_PLACEMENT('',#{b.Point(0, 0, 0)},#{axisDir})");
        int surface = b.Add($"SURFACE_OF_REVOLUTION('',#{arc},#{axis})");

        int oe1 = b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.T.)");
        int oe2 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.T.)");
        int oe3 = b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.F.)");
        int oe4 = b.Add($"ORIENTED_EDGE('',*,*,#{seam},.F.)");
        int lateralLoop = b.Add($"EDGE_LOOP('',(#{oe1},#{oe2},#{oe3},#{oe4}))");
        int lateral = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{lateralLoop},.T.)")}),#{surface},.T.)");

        int bottomLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringB},.F.)")}))");
        int bottomPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, 0), (0, 0, -1), (1, 0, 0))})");
        int bottom = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{bottomLoop},.T.)")}),#{bottomPlane},.T.)");

        int topLoop = b.Add($"EDGE_LOOP('',(#{b.Add($"ORIENTED_EDGE('',*,*,#{ringT},.T.)")}))");
        int topPlane = b.Add($"PLANE('',#{b.Placement(b.Point(0, 0, h), (0, 0, 1), (1, 0, 0))})");
        int top = b.Add($"ADVANCED_FACE('',(#{b.Add($"FACE_OUTER_BOUND('',#{topLoop},.T.)")}),#{topPlane},.T.)");

        int shell = b.Add($"CLOSED_SHELL('',(#{lateral},#{bottom},#{top}))");
        b.Add($"MANIFOLD_SOLID_BREP('',#{shell})");
        return b.Render();
    }

    /// <summary>Two disjoint boxes; the second (larger) is offset in +x. Largest-solid policy fodder.</summary>
    public static string TwoBoxes()
    {
        var b = new Builder();
        b.AddUnits(Unit.Millimetre);
        EmitBox(b, (0, 0, 0), 1, 1, 1);
        EmitBox(b, (10, 0, 0), 2, 2, 2);
        return b.Render();
    }
}
