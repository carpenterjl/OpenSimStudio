using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

// OP-55A: paraxial ray data (help: Constraints on Paraxial Ray Data), by the paraxial model's
// marginal and chief rays (ParaxialSystem, §11), and real ray data (help: Constraints on Real Ray
// Data), read off the real trace's surface hits (SurfaceHit, §13, §15).
public static partial class ImagingOperands
{
    private const string ParaxialRayPage = "Constraints on Paraxial Ray Data";
    private const string ParaxialRayCategory = "Constraints on paraxial ray data";
    private const string RealRayPage = "Constraints on Real Ray Data";
    private const string RealRayCategory = "Constraints on real ray data";

    private static readonly string[] RayData = { "Hx", "Hy", "Px", "Py" };

    // ------------------------------------------------------------------ paraxial rays

    private enum ParaxialValue { X, Y, R, TanX, TanY }

    private static IEnumerable<MeritOperandDefinition> ParaxialRayDefinitions()
    {
        yield return ParaxialRay("PARX", "Paraxial ray x", "x of the paraxial ray on surface Surf's vertex plane, lens units", ParaxialValue.X);
        yield return ParaxialRay("PARY", "Paraxial ray y", "y of the paraxial ray on surface Surf's vertex plane, lens units", ParaxialValue.Y);
        yield return ParaxialRay("PARR", "Paraxial ray radial coordinate", "√(x² + y²) of the paraxial ray on surface Surf's vertex plane, lens units", ParaxialValue.R);
        yield return ParaxialRay("PATX", "Paraxial ray x tangent", "Tangent dx/dz of the paraxial ray in the x-z plane after surface Surf", ParaxialValue.TanX);
        yield return ParaxialRay("PATY", "Paraxial ray y tangent", "Tangent dy/dz of the paraxial ray in the y-z plane after surface Surf", ParaxialValue.TanY);
    }

    /// <summary>
    /// The paraxial ray (Hx, Hy, Px, Py): the paraxial model traces the x and y planes
    /// independently, and its rays are linear, so the ray is Px (Py) times the marginal ray (axial
    /// object point to the rim of the paraxial entrance pupil) plus the paraxial chief ray of the
    /// field (Hx, Hy) in field units (<see cref="ParaxialSystem.TraceChiefRay"/>), in each plane.
    /// </summary>
    private static MeritOperandDefinition ParaxialRay(string code, string name, string description, ParaxialValue value) => new()
    {
        Code = code, Name = name, Category = ParaxialRayCategory, Source = ParaxialRayPage,
        Description = description + ", at wavelength Wave, field (Hx, Hy) and pupil (Px, Py): Px·(marginal ray) + (chief ray of the field) in each plane of the paraxial model.",
        Int1 = "Surf", Int2 = "Wave", Data = RayData,
        Compute = c =>
        {
            int surf = c.Surface(c.Row.Int1);
            var p = c.Paraxial(c.Wavelength(c.Row.Int2));
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
            var (fx, fy) = OpticsConventions.DenormalizeField((hx, hy), p.System.Fields.Scale);
            var (cx, cy) = Engine("the paraxial chief ray", () => p.TraceChiefRay(new FieldPoint { X = fx, Y = fy }));
            var m = p.MarginalRay;
            double x = px * m.Heights[surf] + cx.Heights[surf];
            double y = py * m.Heights[surf] + cy.Heights[surf];
            double ux = px * m.Slopes[surf] + cx.Slopes[surf];
            double uy = py * m.Slopes[surf] + cy.Slopes[surf];
            if (value is ParaxialValue.X or ParaxialValue.Y or ParaxialValue.R && !(double.IsFinite(x) && double.IsFinite(y)))
                throw new MeritOperandException($"the paraxial ray has no height on surface {surf} (the object is at infinity).");
            return value switch
            {
                ParaxialValue.X => c.Length(x),
                ParaxialValue.Y => c.Length(y),
                ParaxialValue.R => c.Length(Math.Sqrt(x * x + y * y)),
                ParaxialValue.TanX => ux,
                _ => uy,
            };
        },
    };

    // ------------------------------------------------------------------ real rays

    private static MeritRayRequest RayOf(MeritOperandRow row) => new(row.Int2, row.DataAt(1), row.DataAt(2), row.DataAt(3), row.DataAt(4));

    private static IEnumerable<MeritOperandDefinition> RealRayDefinitions()
    {
        yield return Hit("REAA", "Real ray x direction cosine", "x direction cosine of the real ray after surface Surf, local frame", (c, h) => h.Direction.X);
        yield return Hit("REAB", "Real ray y direction cosine", "y direction cosine of the real ray after surface Surf, local frame", (c, h) => h.Direction.Y);
        yield return Hit("REAC", "Real ray z direction cosine", "z direction cosine of the real ray after surface Surf, local frame", (c, h) => h.Direction.Z);
        yield return Hit("RENA", "Real ray surface normal x", "x component of the unit surface normal (the sense that is +z at the vertex) at the real ray's intercept on surface Surf, local frame", (c, h) => h.Normal.X);
        yield return Hit("RENB", "Real ray surface normal y", "y component of the unit surface normal at the real ray's intercept on surface Surf, local frame", (c, h) => h.Normal.Y);
        yield return Hit("RENC", "Real ray surface normal z", "z component of the unit surface normal at the real ray's intercept on surface Surf, local frame", (c, h) => h.Normal.Z);
        yield return Hit("RAID", "Real ray angle of incidence", "Angle between the incident real ray and the surface normal at surface Surf, degrees, 0 to 90", (c, h) => h.AngleOfIncidenceDegrees);
        yield return Hit("RAED", "Real ray angle of exitance", "Angle between the real ray after refraction or reflection and the surface normal at surface Surf, degrees, 0 to 90", (c, h) => h.AngleOfExitDegrees);
        yield return Hit("RAIN", "Real ray incidence cosine", "Cosine of the angle of incidence at surface Surf, |d·n| of the incident direction and the normal", (c, h) => Math.Abs(Vector3D.Dot(h.IncomingDirection, h.Normal)));
        yield return Hit("RAEN", "Real ray exitance cosine", "Cosine of the angle of exitance at surface Surf, |d·n| of the leaving direction and the normal", (c, h) => Math.Abs(Vector3D.Dot(h.Direction, h.Normal)));
        yield return Hit("RANG", "Real ray angle to z axis", "Angle, radians, between the real ray after surface Surf and the surface's local z axis: acos of the z direction cosine (0 to π)", (c, h) => Math.Acos(Math.Clamp(h.Direction.Z, -1, 1)));
        yield return Hit("RETX", "Real ray x tangent", "Slope dx/dz (L/N) of the real ray after surface Surf, local frame", (c, h) => Tangent(h.Direction.X, h.Direction.Z));
        yield return Hit("RETY", "Real ray y tangent", "Slope dy/dz (M/N) of the real ray after surface Surf, local frame", (c, h) => Tangent(h.Direction.Y, h.Direction.Z));
        yield return Hit("RAGX", "Global real ray x", "x of the real ray on surface Surf in the global frame (object-space axes, origin at surface 1's vertex: OpticStudio's default global reference surface 1), lens units", (c, h) => c.Length(h.GlobalPosition.X));
        yield return Hit("RAGY", "Global real ray y", "y of the real ray on surface Surf in the global frame, lens units", (c, h) => c.Length(h.GlobalPosition.Y));
        yield return Hit("RAGZ", "Global real ray z", "z of the real ray on surface Surf in the global frame, lens units", (c, h) => c.Length(h.GlobalPosition.Z));
        yield return Hit("RAGA", "Global real ray x direction cosine", "x direction cosine of the real ray after surface Surf in the global frame", (c, h) => GlobalDirection(c, h).X);
        yield return Hit("RAGB", "Global real ray y direction cosine", "y direction cosine of the real ray after surface Surf in the global frame", (c, h) => GlobalDirection(c, h).Y);
        yield return Hit("RAGC", "Global real ray z direction cosine", "z direction cosine of the real ray after surface Surf in the global frame", (c, h) => GlobalDirection(c, h).Z);
        yield return Hit("OPTH", "Optical path length", "Optical path of the real ray to surface Surf, lens units, from the object point (finite conjugates) or, object at infinity, from the plane normal to the field's direction through the field's chief-ray intercept on surface 1 (the help's reference phase plane)", OpticalPath);
        yield return Hit("HHCN", "Hyperhemisphere test", "1 when the real ray's intercept z on surface Surf differs from the surface's sag at the intercept's x and y (by more than 10⁻⁹ m + 10⁻⁹·|z|), else 0", Hyperhemisphere);
        yield return Hit("TRAI", "Transverse aberration radius at a surface", "√(Δx² + Δy²) between the real ray and the primary wavelength's chief ray of the same field on surface Surf, local frame, lens units", TransverseAtSurface);

        yield return new MeritOperandDefinition
        {
            Code = "PLEN", Name = "Path length between surfaces", Category = RealRayCategory, Source = RealRayPage,
            Description = "Optical path of the real ray (Hx, Hy, Px, Py) at the primary wavelength from surface Surf1 to surface Surf2 (OPTH at Surf2 minus OPTH at Surf1), lens units.",
            Int1 = "Surf1", Int2 = "Surf2", Data = RayData,
            Compute = c =>
            {
                int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
                int wave = c.System.Wavelengths.PrimaryIndex + 1;
                double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
                var path = For(c).Path(wave, hx, hy, px, py);
                string ray = Ray(hx, hy, px, py, wave);
                return c.Length(HitOn(path, b, ray).OpticalPath - HitOn(path, a, ray).OpticalPath);
            },
        };

        yield return AngleBound("MNRI", "Minimum real ray angle of incidence", incidence: true, atLeast: true);
        yield return AngleBound("MXRI", "Maximum real ray angle of incidence", incidence: true, atLeast: false);
        yield return AngleBound("MNRE", "Minimum real ray angle of exitance", incidence: false, atLeast: true);
        yield return AngleBound("MXRE", "Maximum real ray angle of exitance", incidence: false, atLeast: false);
    }

    private static MeritOperandDefinition Hit(string code, string name, string description, Func<MeritRowContext, SurfaceHit, double> value) => new()
    {
        Code = code, Name = name, Category = RealRayCategory, Source = RealRayPage,
        Description = description + ". The ray is at wavelength Wave, field (Hx, Hy) and pupil (Px, Py).",
        Int1 = "Surf", Int2 = "Wave", Data = RayData,
        Ray = RayOf,
        Compute = c =>
        {
            int surf = c.Surface(c.Row.Int1);
            int wave = c.Wavelength(c.Row.Int2);
            var path = c.Ray(RayOf(c.Row));
            var hit = HitOn(path, surf, Ray(c.Row.DataAt(1), c.Row.DataAt(2), c.Row.DataAt(3), c.Row.DataAt(4), wave));
            return value(c, hit);
        },
    };

    private static double Tangent(double component, double z) =>
        z != 0 ? component / z : throw new MeritOperandException("the ray runs perpendicular to the local z axis, so its tangent is infinite.");

    private static Vector3D GlobalDirection(MeritRowContext c, SurfaceHit h)
    {
        if (h.Surface < 1)
            throw new MeritOperandException("Surf 0 (the object) has no global direction here; give surface 1 or later.");
        return For(c).Placement().Global(h.Surface).ApplyDirection(h.Direction);
    }

    private static double OpticalPath(MeritRowContext c, SurfaceHit h)
    {
        if (!c.Lens.ObjectAtInfinity) return c.Length(h.OpticalPath);
        if (h.Surface < 1) throw new MeritOperandException("with the object at infinity the optical path at the object surface is not defined.");
        // The launch point lies on the plane through the global origin normal to the field's
        // direction d (RayLauncher), so the path from the plane through the chief ray's surface-1
        // intercept G is the traced path less n0·(G·d).
        int wave = c.Wavelength(c.Row.Int2);
        double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2);
        var own = c.Ray(RayOf(c.Row));
        var o1 = HitOn(own, 1, "the ray");
        var d = o1.GlobalPosition - own.Hits[0].GlobalPosition;
        double length = d.Length;
        if (!(length > 0)) throw new MeritOperandException("the ray's launch point lies on surface 1, so the field direction cannot be read.");
        d = d * (1 / length);
        var chief = For(c).Path(wave, hx, hy, 0, 0);
        var g = HitOn(chief, 1, $"the chief ray (Hx {F(hx)}, Hy {F(hy)}, wavelength {wave})").GlobalPosition;
        return c.Length(h.OpticalPath - o1.IndexBefore * Vector3D.Dot(g, d));
    }

    private static double Hyperhemisphere(MeritRowContext c, SurfaceHit h)
    {
        var s = c.Lens.Surfaces[h.Surface];
        if (h.Surface < 1 || h.Surface > c.Lens.ImageIndex || s.IsCoordinateBreak)
            throw new MeritOperandException($"surface {h.Surface} has no sag to test (the object or a coordinate break).");
        double sag = ElementVolume.Sag(s, h.Position.X, h.Position.Y);
        if (double.IsNaN(sag)) return 1;
        return Math.Abs(h.Position.Z - sag) > 1e-9 + 1e-9 * Math.Abs(h.Position.Z) ? 1 : 0;
    }

    private static double TransverseAtSurface(MeritRowContext c, SurfaceHit h)
    {
        int primary = c.System.Wavelengths.PrimaryIndex + 1;
        double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2);
        var chief = HitOn(For(c).Path(primary, hx, hy, 0, 0), h.Surface, $"the primary wavelength's chief ray (Hx {F(hx)}, Hy {F(hy)})");
        double dx = h.Position.X - chief.Position.X, dy = h.Position.Y - chief.Position.Y;
        return c.Length(Math.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>MNRI, MXRI, MNRE, MXRE: the least or greatest angle of incidence or exitance of the
    /// ray over surfaces Surf1 to Surf2, bounded below (MN) or above (MX) by the target (help:
    /// Understanding Boundary Operands: the value is the target while the bound holds).</summary>
    private static MeritOperandDefinition AngleBound(string code, string name, bool incidence, bool atLeast) => new()
    {
        Code = code, Name = name, Category = RealRayCategory, Source = RealRayPage + "; Understanding Boundary Operands",
        Description = $"The {(atLeast ? "least" : "greatest")} real ray angle of {(incidence ? "incidence" : "exitance")} (degrees, 0 to 90) over surfaces Surf1 to Surf2, bounded {(atLeast ? "below" : "above")} by the target; the ray at field (Hx, Hy), pupil (Px, Py), wavelength Wave (Data5; 0: primary).",
        Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Hx", "Hy", "Px", "Py", "Wave" }, IsBound = true,
        Compute = c =>
        {
            int a = Math.Max(1, c.Surface(c.Row.Int1, "Surf1")), b = c.Surface(c.Row.Int2, "Surf2");
            if (a > b) throw new MeritOperandException($"Surf1 ({a}) is after Surf2 ({b}).");
            int wave = WaveOrPrimary(c, Whole(c.Row.DataAt(5), "Wave"));
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
            var path = For(c).Path(wave, hx, hy, px, py);
            string ray = Ray(hx, hy, px, py, wave);
            double v = atLeast ? double.PositiveInfinity : double.NegativeInfinity;
            for (int j = a; j <= b; j++)
            {
                var h = HitOn(path, j, ray);
                double angle = incidence ? h.AngleOfIncidenceDegrees : h.AngleOfExitDegrees;
                v = atLeast ? Math.Min(v, angle) : Math.Max(v, angle);
            }
            return atLeast ? MeritOperandRegistry.AtLeast(v, c.Row.Target) : MeritOperandRegistry.AtMost(v, c.Row.Target);
        },
    };
}
