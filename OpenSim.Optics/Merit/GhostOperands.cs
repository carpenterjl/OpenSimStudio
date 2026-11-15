using System.Globalization;
using OpenSim.Optics.Analysis.StrayLight;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>
/// OP-46's ghost focus operands (GPIM, GPRT, GPRX, GPRY, GPSX, GPSY, GAOI) and YNIP, registered
/// in <see cref="MeritOperandRegistry.CreateDefault"/> under "// OP-46". Definitions read from
/// OpticStudio 2025 R2's help, saved under <c>plans/sources/optics/op-46-help/</c>: "Ghost Focus
/// Control" and "Optimization Operands Alphabetically"; YNIP from "Constraints on Paraxial Ray
/// Data" (op-54-help). The help names the parameters (Surf1, Surf2, Surf3, Mode, SetVig, Hx, Hy,
/// Px, Py; Surf and Wave for YNIP) but not their columns: integers first, then reals in the order
/// the help names them, is this program's reading (docs/Optics-Conventions.md §10 item 45). Every
/// ghost system is built by <see cref="GhostSystems.Build"/> and every operand uses the primary
/// wavelength, as the help says for GPRT and GAOI.
/// </summary>
public static class GhostOperands
{
    private const string Category = "Ghost focus control";
    private const string Source = "Ghost Focus Control; Optimization Operands Alphabetically";

    /// <summary>Every definition, in the help's order, then YNIP.</summary>
    public static IEnumerable<MeritOperandDefinition> Definitions()
    {
        yield return GhostAngleOfIncidence();
        yield return GhostPupilImage();
        yield return GhostRay("GPRT", "Ghost ray transmission", "Unpolarized transmission of the real ray from object to image along the double-bounce ghost path Surf1 → Surf2 (Fresnel or coating R at the bounces, T elsewhere, at the ray's angles; bulk absorption not included).", Kind.Transmission);
        yield return GhostRay("GPRX", "Ghost ray real x", "x of the real ray on the image surface after the double-bounce ghost path Surf1 → Surf2, lens units.", Kind.RealX);
        yield return GhostRay("GPRY", "Ghost ray real y", "y of the real ray on the image surface after the double-bounce ghost path Surf1 → Surf2, lens units.", Kind.RealY);
        yield return GhostRay("GPSX", "Ghost ray paraxial x", "x of the paraxial ray on the image surface after the double-bounce ghost path Surf1 → Surf2, lens units.", Kind.ParaxialX);
        yield return GhostRay("GPSY", "Ghost ray paraxial y", "y of the paraxial ray on the image surface after the double-bounce ghost path Surf1 → Surf2, lens units.", Kind.ParaxialY);
        yield return YniParaxial();
    }

    private enum Kind { Transmission, RealX, RealY, ParaxialX, ParaxialY }

    /// <summary>
    /// GPIM, ghost pupil image. "Returns one over the absolute value of the distance from the
    /// image surface to the closest pupil ghost"; Surf1 or Surf2 of −1 takes every surface (only
    /// surfaces with index changes, no first bounce on a mirror); Mode 0 pupil ghosts, 1 image
    /// ghosts, 2 ghost pupil magnification (help). The pupil ghost is where the ghost path images
    /// the pupil: the crossing of the paraxial chief ray with the axis after the last surface; the
    /// image ghost is the paraxial marginal ray's crossing. A ghost leaving parallel to the axis
    /// contributes 0. Mode 2 returns the ghost exit pupil diameter over the entrance pupil diameter
    /// of the ghost whose pupil ghost is closest to the image (this program's reading of "worst"
    /// for a magnification, §10 item 45). The image surface is not a wildcard candidate (it has no
    /// index change). The WFB and WSB columns of OpticStudio (the worst pair) are not built.
    /// </summary>
    public static MeritOperandDefinition GhostPupilImage() => new()
    {
        Code = "GPIM", Name = "Ghost pupil image", Category = Category, Source = Source,
        Description = "1/|distance| from the image surface to the closest double-bounce pupil ghost (Mode 0) or image ghost (Mode 1), or the ghost pupil magnification of the closest pupil ghost (Mode 2); "
            + "Surf1 or Surf2 of −1 takes every surface with an index change. Paraxial, primary wavelength.",
        Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Mode" },
        Compute = c =>
        {
            int mode = (int)Math.Round(c.Row.DataAt(1));
            if (mode is < 0 or > 2 || c.Row.DataAt(1) != mode)
                throw new MeritOperandException($"Mode must be 0 (pupil ghosts), 1 (image ghosts) or 2 (ghost pupil magnification); it is {F(c.Row.DataAt(1))}.");
            var p = c.Paraxial(c.System.Wavelengths.PrimaryIndex + 1);
            int image = c.Lens.ImageIndex;
            var candidates = GhostSystems.Candidates(c.Lens, p.Indices, 1, image, includeImage: false);
            int s1 = c.Row.Int1, s2 = c.Row.Int2;
            var pairs = new List<(int, int)>();
            if (s1 >= 0 && s2 >= 0) pairs.Add(CheckPair(c, s1, s2));
            else
                foreach (var (j, i) in GhostSystems.Pairs(candidates))
                    if ((s1 < 0 || j == s1) && (s2 < 0 || i == s2)) pairs.Add((j, i));
            if (pairs.Count == 0)
                throw new MeritOperandException($"no double-bounce pair matches Surf1 {s1}, Surf2 {s2} among the surfaces with an index change ({string.Join(", ", candidates)}).");

            double worst = 0, magnification = double.NaN;
            foreach (var (j, i) in pairs)
            {
                var (model, marginal, chief) = Paraxial(c, p, j, i);
                double? d = model.AxisCrossingFromImage(mode == 1 ? marginal : chief);
                if (d is not double z) continue;
                if (z == 0) throw new MeritOperandException($"the ghost {j} → {i} {(mode == 1 ? "focuses" : "images the pupil")} exactly on the image surface, so 1/distance is infinite.");
                double value = 1 / Math.Abs(c.Length(z));
                if (value > worst)
                {
                    worst = value;
                    if (mode == 2)
                    {
                        if (p.EntrancePupilDiameter is not double epd || !(epd > 0))
                            throw new MeritOperandException("Mode 2 needs the entrance pupil diameter, and the entrance pupil is at infinity.");
                        magnification = 2 * Math.Abs(model.HeightAt(marginal, z)) / epd;
                    }
                }
            }
            if (mode == 2)
                return double.IsNaN(magnification)
                    ? throw new MeritOperandException("no ghost of the selection forms a pupil image at a finite distance, so there is no ghost pupil magnification.")
                    : magnification;
            return worst;
        },
    };

    private static MeritOperandDefinition GhostRay(string code, string name, string description, Kind kind) => new()
    {
        Code = code, Name = name, Category = Category, Source = Source,
        Description = description + " Primary wavelength, field (Hx, Hy), pupil (Px, Py); field and aperture as defined for the original lens (help: use field angles or object heights and an entrance pupil diameter, stop before the first bounce).",
        Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Hx", "Hy", "Px", "Py" },
        Compute = c =>
        {
            var (j, i) = CheckPair(c, c.Row.Int1, c.Row.Int2);
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2), px = c.Row.DataAt(3), py = c.Row.DataAt(4);
            if (kind is Kind.ParaxialX or Kind.ParaxialY)
            {
                var p = c.Paraxial(c.System.Wavelengths.PrimaryIndex + 1);
                var (model, marginal, _) = Paraxial(c, p, j, i);
                var scale = c.System.Fields.Scale;
                var (cx, cy) = Guard(() => p.TraceChiefRay(new FieldPoint { X = hx * scale.X, Y = hy * scale.Y }));
                var chief = model.Trace((kind == Kind.ParaxialX ? cx : cy).Heights[1], (kind == Kind.ParaxialX ? cx : cy).ReducedSlopes[0]);
                int g = chief.Heights.Count - 1;
                double pupil = kind == Kind.ParaxialX ? px : py;
                return c.Length(pupil * marginal.Heights[g] + chief.Heights[g]);
            }
            var (ghost, path) = RealPath(c, j, i, hx, hy, px, py);
            if (!path.Final.IsOk)
                throw new MeritOperandException($"the ghost ray (Hx {F(hx)}, Hy {F(hy)}, Px {F(px)}, Py {F(py)}) failed: {RayStatuses.Label(path.Status).ToLowerInvariant()} at {GhostFocusGenerator.Where(ghost, path.FailedAtSurface)}.");
            return kind switch
            {
                Kind.RealX => c.Length(path.Final.Position.X),
                Kind.RealY => c.Length(path.Final.Position.Y),
                _ => Transmission(c, ghost, path),
            };
        },
    };

    /// <summary>
    /// GAOI, ghost angle of incidence (degrees) at Surf3 after the double bounce Surf1 → Surf2:
    /// Surf3 &gt; Surf2 is the original surface met on the way out after the second bounce
    /// (Surf3 must lie on that pass), a negative Surf3 is |Surf3| in the ghost system directly, and
    /// Surf3 = 0 the ghost system's last surface (help). SetVig = 1 (vignetting factors for the
    /// ghost system) is not built and is refused.
    /// </summary>
    public static MeritOperandDefinition GhostAngleOfIncidence() => new()
    {
        Code = "GAOI", Name = "Ghost angle of incidence", Category = Category, Source = Source,
        Description = "Angle of incidence in degrees of the real ray at surface Surf3 after the double-bounce ghost Surf1 → Surf2 (Surf3 < 0: that surface of the ghost system; 0: its last surface). Primary wavelength.",
        Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Surf3", "SetVig", "Hx", "Hy", "Px", "Py" },
        Compute = c =>
        {
            var (j, i) = CheckPair(c, c.Row.Int1, c.Row.Int2);
            double surf3 = c.Row.DataAt(1);
            if (surf3 != Math.Round(surf3)) throw new MeritOperandException($"Surf3 must be a whole number (it is {F(surf3)}).");
            if (c.Row.DataAt(2) != 0)
                throw new MeritOperandException("SetVig = 1 (vignetting factors set for the ghost system) is not built in this version; use SetVig = 0.");
            double hx = c.Row.DataAt(3), hy = c.Row.DataAt(4), px = c.Row.DataAt(5), py = c.Row.DataAt(6);
            var (ghost, path) = RealPath(c, j, i, hx, hy, px, py);
            int k = (int)surf3, g;
            int gImage = ghost.Lens.ImageIndex;
            if (k < 0)
            {
                g = -k;
                if (g > gImage) throw new MeritOperandException($"Surf3 {k} names ghost surface {g}, but the ghost system has surfaces 0 to {gImage}.");
            }
            else if (k == 0) g = gImage;
            else
            {
                if (k <= i || k > c.Lens.ImageIndex)
                    throw new MeritOperandException($"Surf3 {k} must be greater than Surf2 {i} and at most the image {c.Lens.ImageIndex}.");
                g = -1;
                for (int a = 0; a < ghost.Origins.Count; a++)
                    if (ghost.Origins[a].Original == k && ghost.Origins[a].Pass is GhostPass.Out or GhostPass.End) { g = a; break; }
                if (g < 0) throw new MeritOperandException($"surface {k} is not on the way out of the ghost {j} → {i}.");
            }
            if (path.Hits.Count <= g || path.Hits[g].Status != RayStatus.Ok)
                throw new MeritOperandException($"the ghost ray (Hx {F(hx)}, Hy {F(hy)}, Px {F(px)}, Py {F(py)}) failed before {GhostFocusGenerator.Where(ghost, g)}: {RayStatuses.Label(path.Status).ToLowerInvariant()} at {GhostFocusGenerator.Where(ghost, path.FailedAtSurface)}.");
            return path.Hits[g].AngleOfIncidenceDegrees;
        },
    };

    /// <summary>YNIP: y·n·i of the paraxial marginal ray at surface Surf, wavelength Wave
    /// (<see cref="YniContributions"/>), lens units.</summary>
    public static MeritOperandDefinition YniParaxial() => new()
    {
        Code = "YNIP", Name = "YNI paraxial", Category = "Constraints on paraxial ray data", Source = "Constraints on Paraxial Ray Data; YNI Contributions",
        Description = "The paraxial marginal ray height times the index times the angle of incidence at surface Surf, wavelength Wave (related to the surface's narcissus contribution), lens units.",
        Int1 = "Surf", Int2 = "Wave",
        Compute = c =>
        {
            int j = c.Surface(c.Row.Int1);
            int w = c.Wavelength(c.Row.Int2);
            if (j < 1 || j >= c.Lens.ImageIndex)
                throw new MeritOperandException($"Surf {j} must be a surface between the object and the image (1 to {c.Lens.ImageIndex - 1}).");
            var y = YniContributions.Of(c.Paraxial(w), j);
            if (double.IsNaN(y.Yni))
                throw new MeritOperandException($"surface {j} is a paraxial surface type (thin-lens power or ABCD matrix) and has no angle of incidence.");
            return c.Length(y.Yni);
        },
    };

    // ------------------------------------------------------------------ helpers

    private static (int, int) CheckPair(MeritRowContext c, int surf1, int surf2)
    {
        int image = c.Lens.ImageIndex;
        if (surf1 < 1 || surf1 > image)
            throw new MeritOperandException($"Surf1 {surf1} is not a surface 1 to the image {image}.");
        if (surf2 < 1 || surf2 >= surf1)
            throw new MeritOperandException($"Surf2 {surf2} must lie between 1 and Surf1 − 1 = {surf1 - 1}: the first bounce surface number must be larger than the second (help).");
        return (surf1, surf2);
    }

    private static (GhostParaxial Model, GhostParaxialRay Marginal, GhostParaxialRay Chief) Paraxial(MeritRowContext c, ParaxialSystem p, int j, int i)
    {
        var ghost = Guard(() => GhostSystems.Build(c.Lens, j, i));
        var model = Guard(() => new GhostParaxial(ghost, p.WavelengthMicrometers, c.System.Environment));
        return (model,
            model.Trace(p.MarginalRay.Heights[1], p.MarginalRay.ReducedSlopes[0]),
            model.Trace(p.ChiefRay.Heights[1], p.ChiefRay.ReducedSlopes[0]));
    }

    private static (GhostSystem Ghost, RayPath Path) RealPath(MeritRowContext c, int j, int i, double hx, double hy, double px, double py)
    {
        var ghost = Guard(() => GhostSystems.Build(c.Lens, j, i));
        int row = c.System.Wavelengths.PrimaryIndex;
        return Guard(() =>
        {
            IReadOnlyList<double>? sd = null;
            if (c.Lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
                sd = SemiDiameterSizing.Compute(c.Lens, c.System).Clear;
            var original = SequentialTrace.Create(c.Lens, c.System, row, sd);
            var launch = new RayLauncher(c.Lens, c.System).Launch(original, hx, hy, px, py);
            if (!launch.IsOk)
                return (ghost, new RayPath { Hits = Array.Empty<SurfaceHit>(), Final = launch });
            return (ghost, SequentialTrace.Create(ghost.Lens, c.System, row).TracePath(launch));
        });
    }

    /// <summary>R at the two bounces and T at every other surface the path crosses, at the ray's
    /// angles of incidence, from the uncoated Fresnel interfaces. A coated surface is refused: the
    /// merit evaluation is not given the coating catalog in this build.</summary>
    private static double Transmission(MeritRowContext c, GhostSystem ghost, RayPath path)
    {
        var intensity = new GhostIntensity(c.Lens, c.System, c.System.Wavelengths.PrimaryIndex, CoatingCatalog.Empty, null);
        double t = 1;
        int gImage = ghost.Lens.ImageIndex;
        for (int g = 1; g < gImage; g++)
        {
            var o = ghost.Origins[g];
            double aoi = path.Hits[g].AngleOfIncidenceDegrees;
            t *= Guard(() => o.Pass switch
            {
                GhostPass.FirstBounce => intensity.Reflectance(o.Original, forward: true, aoi),
                GhostPass.SecondBounce => intensity.Reflectance(o.Original, forward: false, aoi),
                _ when intensity.Acts(o.Original) => intensity.Transmittance(o.Original, o.Pass != GhostPass.Back, aoi),
                _ => 1.0,
            });
        }
        return t;
    }

    private static T Guard<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new MeritOperandException(e.Message); }
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
