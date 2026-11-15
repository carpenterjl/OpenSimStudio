using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

/// <summary>
/// IGES 5.3 output (OP-96): surfaces and linework. Every entity, field and code is copied from
/// US PRO, "Initial Graphics Exchange Specification 5.3" (1996, freely distributed; saved under
/// plans/sources/optics/op-96-help/IGES-5.3.pdf), cited by section:
/// <list type="bullet">
/// <item><b>File</b> (§2.2): the fixed ASCII form, 80-column lines with the section letter in
/// column 73 and the sequence number in 74–80; Start (S), Global (G), Directory Entry (D, two lines
/// of ten 8-column fields per entity, §2.2.4.4, Table 2), Parameter Data (P, free format to column
/// 64, the DE pointer in 66–72, §2.2.4.5) and Terminate (T, §2.2.4.6). Global parameters 1–26 in
/// Table 1's order: the unit flag and name (§2.2.4.3.14–15, <see cref="CadUnits.Iges"/>), the
/// IEEE 64-bit magnitude and significance 308 and 15 and 32-bit 38 and 6 (the example of
/// §2.2.4.3.11), version flag 11 for 5.3 (§2.2.4.3.23), the date as 15HYYYYMMDD.HHNNSS
/// (§2.2.4.3.18) and the export tolerance as the minimum user-intended resolution
/// (§2.2.4.3.19). Reals carry a decimal point or an exponent; the exponent letter is D, double
/// precision (§2.2.2.2).</item>
/// <item><b>Surfaces</b>: each piece of a body's meridian revolved as one Rational B-Spline
/// Surface Entity, type 128 (§4.24): the first parametric direction a full circle (degree 2, nine
/// control points on the circumscribed square, closed: PROP1 = 1), the second the meridian piece
/// (degree 1 for a straight piece, a rational quadratic arc for a sphere, the cubic profile fitted
/// to the export tolerance for any other cap, <see cref="BSplineFit.FitProfile"/>), with the form
/// number 1 plane, 2 cylinder, 3 cone, 4 sphere or 6 surface of revolution. The control points
/// are written in the global frame (a rational B-spline is carried exactly by its control points
/// under a rigid motion), so no transformation entity is needed. The surface is the face itself
/// (its natural domain), so no trimming entity (142/144) is needed either. A bare mirror is its
/// surfaces; IGES carries no solid here (the B-rep entity 186 is not built).</item>
/// <item><b>Linework</b>: every rim of a body as a Circular Arc Entity, type 100 (§4.3), a full
/// circle (start = terminate) in the XT–YT plane of its definition space at ZT, placed by a
/// Transformation Matrix Entity, type 124 (§4.21: R11 R12 R13 T1 R21 … T3, model = R·def + T); each
/// ray as a Copious Data Entity, type 106 form 12 (§4.6: IP = 2, a 3-D piecewise linear curve).
/// Lens geometry and rays go on the levels (DE field 5) of <see cref="CadWriteOptions.LensLayer"/>
/// and <see cref="CadWriteOptions.RayLayer"/>.</item>
/// </list>
/// <para>
/// <b>Exactness</b> (derived here, The NURBS Book not being available): a rational quadratic Bézier
/// arc with its end points on a circle, its middle control point where the end tangents meet and
/// middle weight cos(θ/2), θ the arc's central angle, is the circular arc itself; the quarter
/// circles of the u direction have θ = 90°, weight √2/2. The surface of revolution is the tensor
/// product Pᵢⱼ = rⱼ·cᵢ + zⱼ·ẑ, wᵢⱼ = wᵢ·wⱼ: its denominators factor, so S(u, v) = r(v)·c(u) + z(v)·ẑ
/// with c(u) the unit circle, exactly. The tests evaluate the written surfaces on the sphere.
/// </para>
/// </summary>
public static class IgesWriter
{
    public static CadWriteReport Write(CadScene scene, string path, CadWriteOptions options)
    {
        var (text, report) = WriteText(scene, options);
        File.WriteAllText(path, text, Encoding.ASCII);
        return report;
    }

    public static (string Text, CadWriteReport Report) WriteText(CadScene scene, CadWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (scene.Bodies.Count == 0 && scene.Rays.Count == 0) throw new InvalidOperationException("IGES export: there is nothing to write.");
        double s = options.PerUnit;
        var entities = new List<Entity>();
        double maxFit = 0;
        double maxCoordinate = 0;
        int surfaces = 0, circles = 0;

        void Track(Vector3D p)
        {
            maxCoordinate = Math.Max(maxCoordinate, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) * s);
        }

        foreach (var body in scene.Bodies)
        {
            body.Validate();
            foreach (var piece in body.Pieces)
            {
                var (profile, form, fit) = Profile(piece, options.Tolerance, body.Name);
                maxFit = Math.Max(maxFit, fit);
                entities.Add(Surface(body, profile, form, s, options.LensLayer, Track));
                surfaces++;
            }
            foreach (var (r, z) in body.Joints().Where(j => j.R > 0).Distinct())
            {
                var p = body.Placement;
                var matrix = new Entity(124, 0, options.LensLayer, "00010000", new[]
                {
                    N(p.Col0.X), N(p.Col1.X), N(p.Col2.X), N(p.Translation.X * s),
                    N(p.Col0.Y), N(p.Col1.Y), N(p.Col2.Y), N(p.Translation.Y * s),
                    N(p.Col0.Z), N(p.Col1.Z), N(p.Col2.Z), N(p.Translation.Z * s),
                });
                entities.Add(matrix);
                entities.Add(new Entity(100, 0, options.LensLayer, "00000000",
                    new[] { N(z * s), N(0), N(0), N(r * s), N(0), N(r * s), N(0) }) { Transform = matrix });
                Track(body.Global(r, z, 1, 0));
                circles++;
            }
        }

        int rays = 0;
        foreach (var ray in scene.Rays)
        {
            if (ray.Points.Count < 2) continue;
            var values = new List<string> { "2", ray.Points.Count.ToString(CultureInfo.InvariantCulture) };
            foreach (var p in ray.Points)
            {
                values.Add(N(p.X * s));
                values.Add(N(p.Y * s));
                values.Add(N(p.Z * s));
                Track(p);
            }
            entities.Add(new Entity(106, 12, options.RayLayer, "00000000", values));
            rays++;
        }

        string text = Render(entities, options, maxCoordinate);
        string unit = options.Unit.ToString().ToLowerInvariant();
        var notes = new List<string>
        {
            FormattableString.Invariant($"IGES 5.3: {surfaces} rational B-spline surfaces (type 128), {circles} rim circles (type 100), {rays} ray polylines (type 106 form 12); resolution {options.Tolerance * s:g3} {unit}") +
                (maxFit > 0 ? FormattableString.Invariant($"; largest profile deviation from the optical surface {maxFit * s:g3} {unit}.") : "; every surface is exact (no spline fit)."),
            "IGES: the bodies are written as surfaces; IGES solids (the manifold B-rep entity 186) are not built.",
        };
        return (text, new CadWriteReport { Faces = surfaces, Polylines = rays, MaxFitDeviation = maxFit, Notes = notes });
    }

    /// <summary>A meridian piece as a rational B-spline curve in (r, z): degree, knots, weights,
    /// control points; the 128 form number; the fit deviation (0 when exact).</summary>
    private static ((int Degree, double[] Knots, double[] Weights, (double R, double Z)[] Points) Curve, int Form, double Fit)
        Profile(MeridianPiece piece, double tolerance, string what)
    {
        switch (piece)
        {
            case LinePiece line:
            {
                int form = line.IsRadial ? 1 : line.IsAxial ? 2 : 3;
                return ((1, new double[] { 0, 0, 1, 1 }, new double[] { 1, 1 }, new[] { line.Start, line.End }), form, 0);
            }
            case CapPiece cap when cap.Shape is PlaneCap:
                return ((1, new double[] { 0, 0, 1, 1 }, new double[] { 1, 1 }, new[] { (0.0, cap.VertexZ), (cap.RimRadius, cap.RimZ) }), 1, 0);
            case CapPiece { Shape: SphereCap sphere } cap:
            {
                double zc = cap.VertexZ + sphere.Radius, rc = cap.RimRadius, zr = cap.RimZ, v = cap.VertexZ;
                // The end tangents: z = v at the vertex, and perpendicular to the radius (rc, zr − zc)
                // at the rim; they meet at (r1, v).
                double r1 = rc + (zr - v) * (zr - zc) / rc;
                double cosTheta = (v - zc) * (zr - zc) / (sphere.Radius * sphere.Radius);
                double w = Math.Sqrt(0.5 * (1 + Math.Clamp(cosTheta, -1, 1)));
                return ((2, new double[] { 0, 0, 0, 1, 1, 1 }, new[] { 1, w, 1 }, new[] { (0.0, v), (r1, v), (rc, zr) }), 4, 0);
            }
            case CapPiece cap:
            {
                var fit = BSplineFit.FitProfile(cap.Shape, cap.RimRadius, tolerance, $"{what}, {cap.Shape.Describe()}");
                var b = fit.Basis;
                var points = new (double, double)[b.Count];
                for (int i = 0; i < b.Count; i++) points[i] = (b.Greville[i], cap.VertexZ + fit.Z[i]);
                var weights = Enumerable.Repeat(1.0, b.Count).ToArray();
                return ((3, (double[])b.Knots.Clone(), weights, points), 6, fit.MaxDeviation);
            }
            default:
                throw new ArgumentException($"{what}: unknown meridian piece {piece.GetType().Name}.");
        }
    }

    private static Entity Surface(RevolvedBody body,
        (int Degree, double[] Knots, double[] Weights, (double R, double Z)[] Points) profile, int form, double s, int layer,
        Action<Vector3D> track)
    {
        // The unit circle: nine control points at 45° steps on the circumscribed square.
        double h = Math.Sqrt(0.5);
        var cx = new double[] { 1, 1, 0, -1, -1, -1, 0, 1, 1 };
        var cy = new double[] { 0, 1, 1, 1, 0, -1, -1, -1, 0 };
        var cw = new double[] { 1, h, 1, h, 1, h, 1, h, 1 };
        var uKnots = new double[] { 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 4 };
        int k1 = cx.Length - 1, m1 = 2;
        int k2 = profile.Points.Length - 1, m2 = profile.Degree;

        var values = new List<string>
        {
            // PROP1 closed in u (the circle), PROP2 open, PROP3 rational (the circle's weights), not periodic.
            I(k1), I(k2), I(m1), I(m2), "1", "0", "0", "0", "0",
        };
        values.AddRange(uKnots.Select(N));
        values.AddRange(profile.Knots.Select(t => N(t)));
        for (int j = 0; j <= k2; j++)
            for (int i = 0; i <= k1; i++) values.Add(N(cw[i] * profile.Weights[j]));
        for (int j = 0; j <= k2; j++)
            for (int i = 0; i <= k1; i++)
            {
                var (r, z) = profile.Points[j];
                var p = body.Placement.Apply(new Vector3D(r * cx[i], r * cy[i], z));
                track(p);
                values.Add(N(p.X * s));
                values.Add(N(p.Y * s));
                values.Add(N(p.Z * s));
            }
        values.Add(N(uKnots[0]));
        values.Add(N(uKnots[^1]));
        values.Add(N(profile.Knots[m2]));
        values.Add(N(profile.Knots[k2 + 1]));
        return new Entity(128, form, layer, "00000000", values);
    }

    // ---------------- the file ----------------

    private sealed class Entity
    {
        public Entity(int type, int form, int level, string status, IReadOnlyList<string> parameters)
        {
            Type = type;
            Form = form;
            Level = level;
            Status = status;
            Parameters = parameters;
        }

        public int Type { get; }
        public int Form { get; }
        public int Level { get; }
        public string Status { get; }
        public IReadOnlyList<string> Parameters { get; }
        public Entity? Transform { get; init; }
        public int DirectoryLine { get; set; }
    }

    private static string Render(List<Entity> entities, CadWriteOptions options, double maxCoordinate)
    {
        for (int i = 0; i < entities.Count; i++) entities[i].DirectoryLine = 2 * i + 1;

        var sb = new StringBuilder();
        var start = new[]
        {
            "OpenSim Studio optical CAD export (IGES 5.3, fixed ASCII form).",
            "Lens bodies as rational B-spline surfaces (type 128),",
            "rims as circles (100), rays as 3-D polylines (106 form 12).",
        };
        for (int i = 0; i < start.Length; i++) sb.Append(Line(start[i], 'S', i + 1));

        var (flag, unitName) = CadUnits.Iges(options.Unit);
        string name = CadUnits.Ascii(options.Name);
        if (name.Length > 40) name = name[..40]; // a Global field must fit one line here
        string date = H(options.Stamp.ToString("yyyyMMdd.HHmmss", CultureInfo.InvariantCulture));
        var global = new List<string>
        {
            "1H,", "1H;", H(name), H(name + ".igs"), H("OpenSim Studio"), H("OpenSim Studio OP-96"),
            "32", "38", "6", "308", "15", H(name), N(1), I(flag), H(unitName), "1", N(1), date,
            N(options.Tolerance * options.PerUnit), N(maxCoordinate), "", "", "11", "0", "", "",
        };
        var globalLines = Wrap(global, 72, ';');
        for (int i = 0; i < globalLines.Count; i++) sb.Append(Line(globalLines[i], 'G', i + 1));

        // Parameter data first (it fixes each entity's PD pointer and line count).
        var pd = new StringBuilder();
        var pointers = new int[entities.Count];
        var counts = new int[entities.Count];
        int pdLine = 1;
        for (int e = 0; e < entities.Count; e++)
        {
            var entity = entities[e];
            var tokens = new List<string> { I(entity.Type) };
            tokens.AddRange(entity.Parameters);
            var lines = Wrap(tokens, 64, ';');
            pointers[e] = pdLine;
            counts[e] = lines.Count;
            foreach (var l in lines)
            {
                pd.Append(l.PadRight(64)).Append(' ').Append(entity.DirectoryLine.ToString(CultureInfo.InvariantCulture).PadLeft(7))
                  .Append('P').Append(pdLine.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append('\n');
                pdLine++;
            }
        }

        for (int e = 0; e < entities.Count; e++)
        {
            var entity = entities[e];
            int d = entity.DirectoryLine;
            string Field(object v) => Convert.ToString(v, CultureInfo.InvariantCulture)!.PadLeft(8);
            sb.Append(Field(entity.Type)).Append(Field(pointers[e])).Append(Field(0)).Append(Field(0))
              .Append(Field(entity.Level)).Append(Field(0)).Append(Field(entity.Transform?.DirectoryLine ?? 0)).Append(Field(0))
              .Append(entity.Status.PadLeft(8)).Append('D').Append(d.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append('\n');
            sb.Append(Field(entity.Type)).Append(Field(0)).Append(Field(0)).Append(Field(counts[e])).Append(Field(entity.Form))
              .Append(new string(' ', 16)).Append(new string(' ', 8)).Append(Field(0))
              .Append('D').Append((d + 1).ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append('\n');
        }
        sb.Append(pd);

        string terminate = "S" + start.Length.ToString(CultureInfo.InvariantCulture).PadLeft(7)
                           + "G" + globalLines.Count.ToString(CultureInfo.InvariantCulture).PadLeft(7)
                           + "D" + (2 * entities.Count).ToString(CultureInfo.InvariantCulture).PadLeft(7)
                           + "P" + (pdLine - 1).ToString(CultureInfo.InvariantCulture).PadLeft(7);
        sb.Append(Line(terminate, 'T', 1));
        return sb.ToString();
    }

    private static string Line(string data, char section, int sequence) => data.Length > 72
        ? throw new InvalidOperationException($"IGES export: a {section} line of {data.Length} characters does not fit columns 1-72.")
        : data.PadRight(72) + section + sequence.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "\n";

    /// <summary>Delimited fields packed into lines of at most <paramref name="width"/> columns,
    /// never splitting a field; the last field ends with the record delimiter.</summary>
    private static List<string> Wrap(IReadOnlyList<string> fields, int width, char end)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < fields.Count; i++)
        {
            string token = fields[i] + (i == fields.Count - 1 ? end : ',');
            if (token.Length > width) throw new InvalidOperationException($"IGES export: a field of {token.Length} characters does not fit a line.");
            if (current.Length + token.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            current.Append(token);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>A Hollerith string (§2.2.2.3).</summary>
    private static string H(string text) => text.Length == 0 ? "" : text.Length.ToString(CultureInfo.InvariantCulture) + "H" + text;

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A real with a decimal point or a D exponent (§2.2.2.2).</summary>
    internal static string N(double v)
    {
        if (!double.IsFinite(v)) throw new InvalidOperationException($"IGES export: a value is not finite ({v}).");
        if (v == 0) return "0.";
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = e < 0 ? s : s[..e];
        string exponent = e < 0 ? "" : "D" + s[(e + 1)..];
        if (!mantissa.Contains('.')) mantissa += ".";
        return mantissa + exponent;
    }
}
