// OP-55B
using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Composite;
using OpenSim.Optics.Sequential.Diffractive;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// Constraints on lens properties (help: Constraints on Lens Properties, 2025 R2, saved under
/// plans/sources/optics/op-54-help/). TOTR is OP-54's. The point operands read the surface
/// through OP-43's <see cref="SurfaceProbe"/> (analytic conic and asphere slopes, differenced
/// second derivatives elsewhere) and remove the base or best-fit sphere as OP-43's maps do
/// (<see cref="BestFitSphere"/>); the volumes are <see cref="ElementVolume"/>'s (OP-44); the
/// phase operands read OP-26's <see cref="DiffractiveShape.TryPhase"/>. Not built here:
/// off-axis coordinates (Off-axis 1), removing the Base or Composite sag (Remove 3, 4), and the
/// phase operands' removal of fitted terms (their Remove 1 to 3, SPHS's Data 1 and 2), each
/// refused by name.
/// </summary>
public static partial class ConstraintOperands
{
    private static IEnumerable<MeritOperandDefinition> LensProperties()
    {
        const string page = LensPropertiesPage;

        // ---- Cylinder volume ----
        yield return Make("CVOL", "Cylinder volume", LensPropertiesCategory, page,
            "Volume, cubic lens units, of the smallest cylinder holding surfaces Surf1 to Surf2: π times the largest mechanical (Mode 0) or clear (Mode 1) semi-diameter squared times the axial extent of the vertices; sags are not used.",
            c =>
            {
                var (a, b) = Range(c);
                bool mechanical = Mechanical(c.Row.DataAt(1), "Mode (Data1)");
                double z = 0, zMin = 0, zMax = 0, radius = 0;
                for (int j = a; j <= b; j++)
                {
                    var s = c.Lens.Surfaces[j];
                    if (s.IsCoordinateBreak) throw new MeritOperandException($"surface {j} is a coordinate break; the help says the range should not include one.");
                    if (j > 0) radius = Math.Max(radius, Semi(c, j, mechanical));
                    if (j < b)
                    {
                        if (!double.IsFinite(s.Thickness)) throw new MeritOperandException($"surface {j} has an infinite thickness.");
                        z += s.Thickness;
                        zMin = Math.Min(zMin, z);
                        zMax = Math.Max(zMax, z);
                    }
                }
                double r = c.Length(radius), length = c.Length(zMax - zMin);
                return Math.PI * r * r * length;
            }, int1: "Surf1", int2: "Surf2", data: new[] { "Mode" });

        // ---- Diameter to thickness ratio ----
        foreach (var (code, name, atLeast) in new[] { ("MNDT", "Minimum diameter to thickness ratio", true), ("MXDT", "Maximum diameter to thickness ratio", false) })
            yield return Make(code, name, LensPropertiesCategory, page + "; Understanding Boundary Operands",
                $"Each ratio of diameter (two times the mechanical, Mode 0, or clear, Mode 1, semi-diameter) to center thickness of the surfaces Surf1 to Surf2 followed by glass, bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations.",
                c =>
                {
                    var (a, b) = Range(c);
                    bool mechanical = Mechanical(c.Row.DataAt(1), "Mode (Data1)");
                    var q = new List<double>();
                    for (int j = Math.Max(a, 1); j <= b && j < c.Lens.ImageIndex; j++)
                    {
                        var s = c.Lens.Surfaces[j];
                        if (s.IsCoordinateBreak || s.IsMirror || !IsGlass(c, j)) continue;
                        double t = s.Thickness;
                        if (!(t > 0) || !double.IsFinite(t))
                            throw new MeritOperandException($"surface {j}'s center thickness is {F(c.Length(t))}; the ratio needs a positive one.");
                        q.Add(2 * Semi(c, j, mechanical) / t);
                    }
                    return RangeBound(atLeast, c.Row.Target, q);
                }, int1: "Surf1", int2: "Surf2", data: new[] { "Mode" }, bound: true);

        // ---- Sag at the clear semi-diameter ----
        yield return Make("SAGX", "Sag at the x edge", LensPropertiesCategory, page,
            "Sag of surface Surf at x = the clear semi-diameter, y = 0, lens units.",
            c => { int j = PointSurface(c); double a = Clear(c, j); return c.Length(Sag(c.Lens.Surfaces[j], j, a, 0)); }, int1: "Surf");
        yield return Make("SAGY", "Sag at the y edge", LensPropertiesCategory, page,
            "Sag of surface Surf at x = 0, y = the clear semi-diameter, lens units.",
            c => { int j = PointSurface(c); double a = Clear(c, j); return c.Length(Sag(c.Lens.Surfaces[j], j, 0, a)); }, int1: "Surf");

        // ---- Sag, slope and curvature at a point ----
        yield return Make("SSAG", "Sag at a point", LensPropertiesCategory, page,
            "Sag of surface Surf at (X, Y), lens units. Mode 0: X, Y in lens units, the part flat beyond the clear semi-diameter plus the chip zone; Mode 1: lens units, the surface equation everywhere; Mode 2: X, Y normalised by the clear semi-diameter. Remove 1 removes the vertex-radius sphere, 2 the best-fit sphere (BFS 0 minimum volume, 1 minimum RMS, 2 minimum RMS with offset, 3 minimum volume reversed).",
            c =>
            {
                int j = PointSurface(c);
                int mode = Flag(c.Row.Int2, "Mode (Int2)", 0, 2, "0 flat edge, 1 surface equation, 2 normalised");
                var (x, y) = PointOf(c, j, mode);
                OffAxis(c.Row.DataAt(3));
                if (mode == 0)
                {
                    double edge = Clear(c, j) + c.Lens.Surfaces[j].ChipZone, r = Math.Sqrt(x * x + y * y);
                    if (r > edge) { x *= edge / r; y *= edge / r; }
                }
                var probe = Probe(c, j);
                var (sphere, offset) = Reference(c, j, probe, c.Row.DataAt(4), c.Row.DataAt(5));
                double v = Finite(probe.Sag(x, y), j, x, y);
                if (sphere is not null) v -= Finite(sphere.Sag(x, y), j, x, y) + offset;
                return c.Length(v);
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y", "Off-axis", "Remove", "BFS" });

        yield return Make("SSLP", "Slope at a point", LensPropertiesCategory, page,
            "Slope (dimensionless) of surface Surf at (X, Y) along the tangential (Orientation 0), sagittal (1), x (2) or y (3) direction. Mode 0 or 1: X, Y in lens units; Mode 2: normalised by the clear semi-diameter. Remove and BFS as SSAG (the sphere's slope is subtracted).",
            c =>
            {
                int j = PointSurface(c);
                var (x, y) = PointOf(c, j, Flag(c.Row.Int2, "Mode (Int2)", 0, 2, "0 or 1 lens units, 2 normalised"));
                OffAxis(c.Row.DataAt(3));
                var data = Flag(c.Row.DataAt(6), "Orientation (Data6)", 0, 3, "0 tangential, 1 sagittal, 2 x, 3 y") switch
                {
                    0 => SurfaceSlopeData.Tangential, 1 => SurfaceSlopeData.Sagittal, 2 => SurfaceSlopeData.X, _ => SurfaceSlopeData.Y,
                };
                var probe = Probe(c, j);
                var (sphere, _) = Reference(c, j, probe, c.Row.DataAt(4), c.Row.DataAt(5));
                double v = SurfaceProbe.Slope(Slopes(probe, j, x, y), data, x, y);
                if (sphere is not null) v -= SurfaceProbe.Slope(Slopes(sphere, j, x, y), data, x, y);
                return v;
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y", "Off-axis", "Remove", "BFS", "Orientation" });

        yield return Make("SCRV", "Curvature at a point", LensPropertiesCategory, page,
            "Normal curvature, 1/lens unit, of surface Surf at (X, Y) along the tangential (Orientation 0), sagittal (1), x (2) or y (3) direction. Mode 0 or 1: X, Y in lens units; Mode 2: normalised by the clear semi-diameter. Remove and BFS as SSAG (the sphere's curvature is subtracted).",
            c =>
            {
                int j = PointSurface(c);
                var (x, y) = PointOf(c, j, Flag(c.Row.Int2, "Mode (Int2)", 0, 2, "0 or 1 lens units, 2 normalised"));
                OffAxis(c.Row.DataAt(3));
                var data = Flag(c.Row.DataAt(6), "Orientation (Data6)", 0, 3, "0 tangential, 1 sagittal, 2 x, 3 y") switch
                {
                    0 => SurfaceCurvatureData.Tangential, 1 => SurfaceCurvatureData.Sagittal, 2 => SurfaceCurvatureData.X, _ => SurfaceCurvatureData.Y,
                };
                var probe = Probe(c, j);
                var (sphere, _) = Reference(c, j, probe, c.Row.DataAt(4), c.Row.DataAt(5));
                double v = SurfaceProbe.Curvature(Derivatives(probe, j, x, y), data, x, y);
                if (sphere is not null) v -= SurfaceProbe.Curvature(Derivatives(sphere, j, x, y), data, x, y);
                return c.Curvature(v);
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y", "Off-axis", "Remove", "BFS", "Orientation" });

        // ---- Phase of a diffractive surface ----
        yield return Make("SPHS", "Surface phase at a point", LensPropertiesCategory, page,
            "Phase, waves (Φ/2π, diffraction order included), of the phase surface Surf at (X, Y). Mode 0 or 1: X, Y in lens units; Mode 2: normalised by the clear semi-diameter. Data 0 and Remove 0 only: the fitted-term removals are not built.",
            c =>
            {
                int j = PointSurface(c);
                var (x, y) = PointOf(c, j, Flag(c.Row.Int2, "Mode (Int2)", 0, 2, "0 or 1 lens units, 2 normalised"));
                if (c.Row.DataAt(3) != 0) throw new MeritOperandException($"Data (Data3) {F(c.Row.DataAt(3))} returns a removed tilt or power term, and removing fitted terms is not built; use Data 0.");
                if (c.Row.DataAt(4) != 0) throw new MeritOperandException($"Remove (Data4) {F(c.Row.DataAt(4))} is not built (removing the vertex phase, tilt or power needs the 33 × 33 fit); use Remove 0.");
                var (phase, _, _) = Phase(c, j, x, y);
                return phase / (2 * Math.PI);
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y", "Data", "Remove" });

        yield return Make("PSLP", "Phase slope at a point", LensPropertiesCategory, page,
            "Slope of the phase of the phase surface Surf at (X, Y), periods of 2π per lens unit, along the tangential (Orientation 0), sagittal (1), x (2) or y (3) direction of the vertex plane. Mode 0 or 1: X, Y in lens units; Mode 2: normalised by the clear semi-diameter. Remove 0 only.",
            c =>
            {
                int j = PointSurface(c);
                var (x, y) = PointOf(c, j, Flag(c.Row.Int2, "Mode (Int2)", 0, 2, "0 or 1 lens units, 2 normalised"));
                if (c.Row.DataAt(3) != 0) throw new MeritOperandException($"Remove (Data3) {F(c.Row.DataAt(3))} is not built (removing the vertex phase, tilt or power needs the 33 × 33 fit); use Remove 0.");
                int orientation = Flag(c.Row.DataAt(4), "Orientation (Data4)", 0, 3, "0 tangential, 1 sagittal, 2 x, 3 y");
                var (_, dx, dy) = Phase(c, j, x, y);
                var (u, v) = SurfaceProbe.Radial(x, y);
                double slope = orientation switch { 0 => dx * u + dy * v, 1 => -dx * v + dy * u, 2 => dx, _ => dy };
                return c.Curvature(slope / (2 * Math.PI));
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y", "Remove", "Orientation" });

        // ---- Thickness at a point ----
        yield return Make("STHI", "Thickness at a point", LensPropertiesCategory, page,
            "Thickness from surface Surf to the next at (X, Y) in lens units: center thickness plus the next sag minus this sag, each face flat beyond its mechanical (Mode 0) or clear (Mode 1) semi-diameter; tilts and decenters between the surfaces are not allowed.",
            c =>
            {
                int j = PointSurface(c);
                bool mechanical = Mechanical(c.Row.Int2, "Mode (Int2)");
                double x = OpticsConventions.ToMeters(c.Row.DataAt(1), c.Unit), y = OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit);
                double t = Axial(c, j);
                double FaceSag(int k)
                {
                    double edge = Semi(c, k, mechanical), r = Math.Sqrt(x * x + y * y), px = x, py = y;
                    if (r > edge) { px *= edge / r; py *= edge / r; }
                    return Sag(c.Lens.Surfaces[k], k, px, py);
                }
                return c.Length(t + FaceSag(j + 1) - FaceSag(j));
            }, int1: "Surf", int2: "Mode", data: new[] { "X", "Y" });

        // ---- Mass and volume ----
        yield return Make("TMAS", "Total mass", LensPropertiesCategory, page + "; Comments on computing element volumes (as OP-44's ElementVolume)",
            "Mass in grams of the glass elements after the surfaces Surf1 to Surf2: each element's volume (ElementVolume, faces squared up to the larger mechanical, Mode 0, or clear, Mode 1, semi-diameter) times the catalog density.",
            c =>
            {
                var (a, b) = Range(c);
                bool mechanical = Mechanical(c.Row.DataAt(1), "Mode (Data1)");
                var media = ReportText.SpaceMedia(c.Lens);
                double grams = 0;
                int elements = 0;
                for (int j = Math.Max(a, 1); j <= b && j < c.Lens.ImageIndex; j++)
                {
                    if (!IsGlass(c, j)) continue;
                    var density = media[j].Material?.Density
                        ?? throw new MeritOperandException($"the glass after surface {j} ({media[j].Describe()}) has no catalog density.");
                    grams += Volume(c, j, mechanical).CubicCentimeters * density;
                    elements++;
                }
                if (elements == 0) throw new MeritOperandException($"surfaces {a} to {b} have no glass after them.");
                return grams;
            }, int1: "Surf1", int2: "Surf2", data: new[] { "Mode" });

        yield return Make("VOLU", "Volume", LensPropertiesCategory, page + "; Comments on computing element volumes (as OP-44's ElementVolume)",
            "Volume in cubic centimetres of the lenses and air spaces after the surfaces Surf1 to Surf2 (each surface to the next), faces squared up to the larger clear semi-diameter (ElementVolume).",
            c =>
            {
                var (a, b) = Range(c);
                double cc = 0;
                for (int j = Math.Max(a, 1); j <= b && j < c.Lens.ImageIndex; j++)
                    cc += Volume(c, j, mechanical: false).CubicCentimeters;
                return cc;
            }, int1: "Surf1", int2: "Surf2");

        // ---- Surface normal ----
        foreach (var (code, axis) in new[] { ("NORX", 0), ("NORY", 1), ("NORZ", 2) })
        {
            string letter = "xyz"[axis].ToString();
            yield return Make(code, $"Surface normal {letter} component", LensPropertiesCategory, page,
                $"The {letter} component of the unit normal of surface Surf at (X, Y) (lens units), the sense that is +z at the vertex; local coordinates when Global is 0, the global frame (surface 1's local frame, as the Global vertex report's default reference) when 1.",
                c =>
                {
                    int j = PointSurface(c);
                    bool global = Flag(c.Row.Int2, "Global (Int2)", 0, 1, "0 local, 1 global") == 1;
                    var n = NormalAt(c, j, OpticsConventions.ToMeters(c.Row.DataAt(1), c.Unit), OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit));
                    if (global) n = Placement(c).RelativeTo(j, 1).ApplyDirection(n);
                    return axis switch { 0 => n.X, 1 => n.Y, _ => n.Z };
                }, int1: "Surf", int2: "Global", data: new[] { "X", "Y" });
        }

        yield return Make("NORD", "Normal distance to the next surface", LensPropertiesCategory, page,
            "Distance, lens units, from surface Surf at (X, Y) along its normal (the sense that is +z at the vertex) to the next surface; the next surface must be coaxial with it.",
            c =>
            {
                int j = PointSurface(c);
                double x = OpticsConventions.ToMeters(c.Row.DataAt(1), c.Unit), y = OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit);
                double t = Axial(c, j);
                var n = NormalAt(c, j, x, y);
                var p = new Vector3D(x, y, Sag(c.Lens.Surfaces[j], j, x, y) - t);   // in the next surface's frame
                if (!(Math.Abs(n.Z) > 1e-12)) throw new MeritOperandException($"the normal at ({F(c.Row.DataAt(1))}, {F(c.Row.DataAt(2))}) is perpendicular to the axis.");
                double s0 = -p.Z / n.Z;
                var q = p + n * s0;
                var next = CompositeSurfaces.WithEffectiveShape(c.Lens.Surfaces[j + 1]);
                double dist;
                bool hit = next.Shape is { } shape
                    ? shape.Intersect(q.X, q.Y, n, next, out _, out dist)
                    : ConicIntersection.Intersect(q.X, q.Y, n, next.Curvature, next.Conic, out _, out dist);
                if (!hit || !double.IsFinite(dist)) throw new MeritOperandException($"the normal line from surface {j} misses surface {j + 1}.");
                return c.Length(s0 + dist);
            }, int1: "Surf", data: new[] { "X", "Y" });

        // ---- Surface curvature and derivatives ----
        yield return Make("SCUR", "Surface curvature", LensPropertiesCategory, page,
            "Curvature, 1/lens unit, of surface Surf at (X, Y): Data 0–3 tangential, sagittal, tangential − sagittal, the largest |tangential − sagittal| over 50 points from the vertex to (X, Y); 4–7 the same in x and y; 8, 9 |R·Sc| (R the radial coordinate, Sc the sagittal curvature; dimensionless) and its largest value over the 50 points.",
            c =>
            {
                int j = PointSurface(c);
                int data = Flag(c.Row.Int2, "Data (Int2)", 0, 9, "0–9, see the description");
                double x = OpticsConventions.ToMeters(c.Row.DataAt(1), c.Unit), y = OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit);
                var probe = Probe(c, j);
                double At(double px, double py, int d)
                {
                    var dv = Derivatives(probe, j, px, py);
                    double T() => SurfaceProbe.Curvature(dv, SurfaceCurvatureData.Tangential, px, py);
                    double S() => SurfaceProbe.Curvature(dv, SurfaceCurvatureData.Sagittal, px, py);
                    double X() => SurfaceProbe.Curvature(dv, SurfaceCurvatureData.X, px, py);
                    double Y() => SurfaceProbe.Curvature(dv, SurfaceCurvatureData.Y, px, py);
                    return d switch
                    {
                        0 => c.Curvature(T()), 1 => c.Curvature(S()), 2 => c.Curvature(T() - S()),
                        4 => c.Curvature(X()), 5 => c.Curvature(Y()), 6 => c.Curvature(X() - Y()),
                        _ => Math.Abs(Math.Sqrt(px * px + py * py) * S()),
                    };
                }
                if (data is 3 or 7 or 9)
                {
                    int d = data == 3 ? 2 : data == 7 ? 6 : 8;
                    double largest = 0;
                    for (int i = 0; i < ScurPoints; i++)
                    {
                        double f = (double)i / (ScurPoints - 1);
                        largest = Math.Max(largest, Math.Abs(At(x * f, y * f, d)));
                    }
                    return largest;
                }
                return At(x, y, data);
            }, int1: "Surf", int2: "Data", data: new[] { "X", "Y" });

        yield return Make("SDRV", "Surface derivative", LensPropertiesCategory, page,
            "Derivative of the sag of surface Surf along its local z at (X, Y): Data 0, 1 the first derivative (dimensionless) in the tangential or sagittal direction; 2, 3 the second derivative (1/lens unit) in those directions.",
            c =>
            {
                int j = PointSurface(c);
                int data = Flag(c.Row.Int2, "Data (Int2)", 0, 3, "0, 1 first, 2, 3 second derivative, tangential then sagittal");
                double x = OpticsConventions.ToMeters(c.Row.DataAt(1), c.Unit), y = OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit);
                var probe = Probe(c, j);
                var d = Derivatives(probe, j, x, y);
                var (u, v) = SurfaceProbe.Radial(x, y);
                double Second(double a, double b) => d.Zxx * a * a + 2 * d.Zxy * a * b + d.Zyy * b * b;
                return data switch
                {
                    0 => SurfaceProbe.Slope(d, SurfaceSlopeData.Tangential, x, y),
                    1 => SurfaceProbe.Slope(d, SurfaceSlopeData.Sagittal, x, y),
                    2 => c.Curvature(Second(u, v)),
                    _ => c.Curvature(Second(-v, u)),
                };
            }, int1: "Surf", int2: "Data", data: new[] { "X", "Y" });
    }

    /// <summary>"50 points equally spaced from the vertex of the surface to the specified X and
    /// Y" (help, SCUR): the vertex and (X, Y) included.</summary>
    public const int ScurPoints = 50;

    /// <summary>Surf, checked to be a surface with a shape (not a coordinate break).</summary>
    private static int PointSurface(MeritRowContext c)
    {
        int j = c.Surface(c.Row.Int1);
        if (c.Lens.Surfaces[j].IsCoordinateBreak) throw new MeritOperandException($"surface {j} is a coordinate break, which has no shape.");
        return j;
    }

    /// <summary>(X, Y) in meters: Data1 and Data2 in lens units (Mode 0, 1) or normalised by the
    /// clear semi-diameter (Mode 2).</summary>
    private static (double X, double Y) PointOf(MeritRowContext c, int j, int mode)
    {
        double x = c.Row.DataAt(1), y = c.Row.DataAt(2);
        if (mode == 2) { double a = Clear(c, j); return (x * a, y * a); }
        return (OpticsConventions.ToMeters(x, c.Unit), OpticsConventions.ToMeters(y, c.Unit));
    }

    private static void OffAxis(double flag)
    {
        if (Flag(flag, "Off-axis (Data3)", 0, 1, "0 system coordinates, 1 off-axis coordinates") == 1)
            throw new MeritOperandException("off-axis coordinates (Off-axis 1) are not built (OP-43 does not build them either); use Off-axis 0.");
    }

    /// <summary>OP-43's probe of surface j: the second-derivative step is 10⁻³ of the clear
    /// semi-diameter (SurfaceShapeAnalysisBase.DifferenceStepFraction) for a shape that needs it.</summary>
    private static SurfaceProbe Probe(MeritRowContext c, int j)
    {
        var s = c.Lens.Surfaces[j];
        bool needsStep = CompositeSurfaces.WithEffectiveShape(s).Shape is not null;
        return new SurfaceProbe(s, needsStep ? SagTableAnalysis.DifferenceStepFraction * Clear(c, j) : 0);
    }

    /// <summary>The sphere to remove and its vertex offset (OP-43's rule): none (Remove 0), the
    /// vertex radius (1), or the best-fit sphere over 0 to the clear semi-diameter by the BFS flag
    /// (2). Remove 3 and 4 (Base and Composite sag) are refused.</summary>
    private static (SurfaceProbe? Sphere, double Offset) Reference(MeritRowContext c, int j, SurfaceProbe probe, double remove, double bfs)
    {
        int r = Flag(remove, "Remove (Data4)", 0, 4, "0 none, 1 base radius, 2 best-fit sphere, 3 base sag, 4 composite sag");
        switch (r)
        {
            case 0: return (null, 0);
            case 1: return (SurfaceProbe.Sphere(c.Lens.Surfaces[j].Curvature), 0);
            case 2:
            {
                int k = Flag(bfs, "BFS (Data5)", 0, 3, "0 minimum volume, 1 minimum RMS, 2 minimum RMS with offset, 3 minimum volume reversed");
                var criterion = k switch
                {
                    1 => BestFitSphereCriterion.MinimumRmsNoOffset,
                    2 => BestFitSphereCriterion.MinimumRmsWithOffset,
                    _ => BestFitSphereCriterion.MinimumVolume,
                };
                int air = BestFitSphere.AirSign(c.Lens, c.System, j, reverse: k == 3, out _);
                var fit = BestFitSphere.Fit(probe, 0, Clear(c, j), criterion, air, $"surface {j}'s best-fit sphere");
                return (SurfaceProbe.Sphere(fit.Curvature), fit.VertexOffset);
            }
            default:
                throw new MeritOperandException($"Remove {r} (the {(r == 3 ? "Base" : "Composite")} sag) is not built; use Remove 0, 1 or 2.");
        }
    }

    private static double Finite(double v, int j, double x, double y) =>
        double.IsFinite(v) ? v : throw new MeritOperandException($"surface {j}'s sag is not defined at (x, y) = ({F(x)}, {F(y)}) m.");

    private static SurfaceDerivatives Slopes(SurfaceProbe probe, int j, double x, double y) =>
        probe.TrySlope(x, y, out double z, out double zx, out double zy)
            ? new SurfaceDerivatives(z, zx, zy, 0, 0, 0)
            : throw new MeritOperandException($"surface {j}'s slope is not defined at (x, y) = ({F(x)}, {F(y)}) m.");

    private static SurfaceDerivatives Derivatives(SurfaceProbe probe, int j, double x, double y) =>
        probe.TryDerivatives(x, y, out var d) ? d
            : throw new MeritOperandException($"surface {j}'s second derivatives are not defined at (x, y) = ({F(x)}, {F(y)}) m.");

    /// <summary>The unit normal (−z_x, −z_y, 1)/√(1 + z_x² + z_y²) of surface j at (x, y), local frame.</summary>
    private static Vector3D NormalAt(MeritRowContext c, int j, double x, double y)
    {
        var d = Slopes(new SurfaceProbe(c.Lens.Surfaces[j], 1), j, x, y);   // slopes only: the step is unused
        double w = Math.Sqrt(1 + d.Zx * d.Zx + d.Zy * d.Zy);
        return new Vector3D(-d.Zx / w, -d.Zy / w, 1 / w);
    }

    /// <summary>The phase Φ (radians) of phase surface j at (x, y, sag) and its derivatives along
    /// the vertex plane's x and y, ∂Φ/∂x + ∂Φ/∂z·z_x and likewise in y (radians per meter).</summary>
    private static (double Phase, double Dx, double Dy) Phase(MeritRowContext c, int j, double x, double y)
    {
        var s = CompositeSurfaces.WithEffectiveShape(c.Lens.Surfaces[j]);
        if (s.Shape is not DiffractiveShape phaseShape)
            throw new MeritOperandException($"surface {j} ({s.Shape?.TypeName ?? "Standard"}) is not a phase surface, so it has no phase (the help: no value is returned).");
        var d = Slopes(new SurfaceProbe(c.Lens.Surfaces[j], 1), j, x, y);
        if (!phaseShape.TryPhase(new Vector3D(x, y, d.Z), s, out double phase, out var g) || !double.IsFinite(phase))
            throw new MeritOperandException($"surface {j}'s phase is not defined at (x, y) = ({F(x)}, {F(y)}) m.");
        return (phase, g.X + g.Z * d.Zx, g.Y + g.Z * d.Zy);
    }

    /// <summary>The volume from surface j to j + 1 (ElementVolume), with each face's mechanical
    /// or clear semi-diameter.</summary>
    private static ElementVolumeResult Volume(MeritRowContext c, int j, bool mechanical)
    {
        var front = c.Lens.Surfaces[j];
        var back = c.Lens.Surfaces[j + 1];
        if (front.IsMirror || back.IsMirror) throw new MeritOperandException($"surface {j} or {j + 1} is a mirror; the space between them has no volume this program computes.");
        double t = Axial(c, j);
        return ElementVolume.Compute(front, Semi(c, j, mechanical), back, Semi(c, j + 1, mechanical), t, $"Surfaces {j}–{j + 1}");
    }
}
