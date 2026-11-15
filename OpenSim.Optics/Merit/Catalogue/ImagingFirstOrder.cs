using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

// OP-55A: first-order optical properties (help: First-Order Optical Properties), by the paraxial
// model (ParaxialSystem, docs/Optics-Conventions.md §11), the cardinal planes of a surface range
// (CardinalPlanes, §29) and the real working F/# (RealWorkingFNumber, §25).
public static partial class ImagingOperands
{
    private const string FirstOrderPage = "First-Order Optical Properties";
    private const string FirstOrderCategory = "First-order optical properties";

    private static IEnumerable<MeritOperandDefinition> FirstOrderDefinitions()
    {
        yield return Paraxial("AMAG", "Angular magnification",
            "Ratio of the image-space to object-space paraxial chief ray slopes (ū′/ū) at wavelength Wave.", wave: true,
            p => p.AngularMagnification ?? throw new MeritOperandException("the paraxial chief ray is parallel to the axis in object space, so there is no angular magnification."),
            length: false);
        yield return Paraxial("ENPP", "Entrance pupil position",
            "Paraxial entrance pupil position from surface 1's vertex, lens units, primary wavelength.", wave: false,
            p => p.EntrancePupilPosition ?? throw new MeritOperandException("the entrance pupil is at infinity."), length: true);
        yield return Paraxial("EPDI", "Entrance pupil diameter",
            "Paraxial entrance pupil diameter, lens units, primary wavelength.", wave: false,
            p => p.EntrancePupilDiameter ?? throw new MeritOperandException("the entrance pupil is at infinity, so it has no diameter."), length: true);
        yield return Paraxial("EXPD", "Exit pupil diameter",
            "Paraxial exit pupil diameter, lens units, primary wavelength.", wave: false,
            p => p.ExitPupilDiameter ?? throw new MeritOperandException("the exit pupil is at infinity, so it has no diameter."), length: true);
        yield return Paraxial("EXPP", "Exit pupil position",
            "Paraxial exit pupil position from the image surface's vertex, lens units, primary wavelength.", wave: false,
            p => (p.ExitPupilPosition ?? throw new MeritOperandException("the exit pupil is at infinity."))
                 - p.Lens.Surfaces[p.Lens.LastSurfaceIndex].Thickness, length: true);
        yield return Paraxial("ISFN", "Image space F/#",
            "Paraxial infinite-conjugate F/#: |EFL| over the paraxial entrance pupil diameter, primary wavelength.", wave: false,
            p => p.ImageSpaceFNumber ?? throw new MeritOperandException("the system is afocal or its entrance pupil is at infinity, so it has no image space F/#."), length: false);
        yield return Paraxial("ISNA", "Image space numerical aperture",
            "Paraxial image-space NA n′·sin(atan|u′|) of the marginal ray at the conjugates in use, primary wavelength.", wave: false,
            p => p.ImageNumericalAperture, length: false);
        yield return Paraxial("LINV", "Lagrange invariant",
            "Lagrange invariant n(ū·y − u·ȳ) of the paraxial marginal and full-field chief rays, lens units, at wavelength Wave.", wave: true,
            p => p.LagrangeInvariant, length: true);
        yield return Paraxial("OBSN", "Object space numerical aperture",
            "Paraxial object-space NA n·sin(atan|u|) of the marginal ray, on axis, primary wavelength; zero for an object at infinity.", wave: false,
            p => p.ObjectNumericalAperture, length: false);
        yield return Paraxial("PIMH", "Paraxial image height",
            "Height of the full-field paraxial chief ray at the paraxial image, lens units, at wavelength Wave.", wave: true,
            p => p.ParaxialImageHeight ?? throw new MeritOperandException("the paraxial image is at infinity."), length: true);
        yield return Paraxial("PMAG", "Paraxial magnification",
            "Paraxial lateral magnification at the paraxial image (ν/ν′ of the marginal ray), at wavelength Wave; zero for an object at infinity.", wave: true,
            p => p.LateralMagnification ?? throw new MeritOperandException("the paraxial image is at infinity, so there is no lateral magnification."), length: false);

        yield return new MeritOperandDefinition
        {
            Code = "POWR", Name = "Surface power", Category = FirstOrderCategory, Source = FirstOrderPage,
            Description = "Paraxial power φ = c·(n′ − n) (signed indices, §11) of surface Surf, 1/lens unit, at wavelength Wave; Standard surfaces only.",
            Int1 = "Surf", Int2 = "Wave",
            Compute = c =>
            {
                int surf = c.Surface(c.Row.Int1);
                var p = c.Paraxial(c.Wavelength(c.Row.Int2));
                if (surf < 1 || surf > p.Lens.LastSurfaceIndex)
                    throw new MeritOperandException($"Surf {surf} is the object or image surface, which has no power.");
                var s = c.Lens.Surfaces[surf];
                if (s.Shape is not null || p.Lens.Surfaces[surf].ThinLensPower != 0 || p.Lens.Surfaces[surf].SlopeMatrix is not null)
                    throw new MeritOperandException($"surface {surf} is not a Standard surface; the help: POWR \"only works for standard surfaces\".");
                return c.Curvature(p.Powers[surf]);
            },
        };

        yield return EflPlane("EFLX", "Effective focal length x", "x");
        yield return EflPlane("EFLY", "Effective focal length y", "y");

        yield return new MeritOperandDefinition
        {
            Code = "CARD", Name = "Cardinal point data", Category = FirstOrderCategory, Source = FirstOrderPage,
            Description = "A cardinal datum of surfaces Surf1 to Surf2 at wavelength Wave (Data1), lens units: Data (Data3) 0/1 object/image focal length, 2/3 focal planes, 4/5 principal, 6/7 anti-principal, 8/9 nodal, 10/11 anti-nodal planes; object-space positions from Surf1's vertex, image-space from Surf2's. Orientation (Data2) 0 (YZ) or 1 (XZ), which agree for a rotationally symmetric lens.",
            Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Wave", "Orientation", "Data" },
            Compute = c =>
            {
                int wave = c.Wavelength(Whole(c.Row.DataAt(1), "Wave"));
                int orientation = Whole(c.Row.DataAt(2), "Orientation");
                if (orientation is not (0 or 1)) throw new MeritOperandException($"Orientation {orientation} must be 0 (YZ) or 1 (XZ).");
                int data = Whole(c.Row.DataAt(3), "Data");
                if (data < 0 || data > 11) throw new MeritOperandException($"Data {data} must be 0 to 11.");
                var p = c.Paraxial(wave);
                var (a, b) = Range(c, p);
                RequireSymmetric(c, "CARD's YZ and XZ planes");
                var planes = Engine("the cardinal planes", () => CardinalPlanes.Compute(p, a, b));
                if (planes.IsAfocal) throw new MeritOperandException($"surfaces {a} to {b} are afocal; they have no cardinal points.");
                double? v = data switch
                {
                    0 => planes.ObjectFocalLength, 1 => planes.ImageFocalLength,
                    2 => planes.Focal?.Object, 3 => planes.Focal?.Image,
                    4 => planes.Principal?.Object, 5 => planes.Principal?.Image,
                    6 => planes.AntiPrincipal?.Object, 7 => planes.AntiPrincipal?.Image,
                    8 => planes.Nodal?.Object, 9 => planes.Nodal?.Image,
                    10 => planes.AntiNodal?.Object, _ => planes.AntiNodal?.Image,
                };
                return c.Length(v ?? throw new MeritOperandException($"Data {data}'s plane of surfaces {a} to {b} is at infinity."));
            },
        };

        yield return new MeritOperandDefinition
        {
            Code = "WFNO", Name = "Working F/#", Category = FirstOrderCategory, Source = FirstOrderPage + "; Working F/#",
            Description = "Real working F/# 1/(2n sin θ) on axis at the primary wavelength: the mean NA² of the four marginal rays about the chief ray (help, \"Working F/#\"; RealWorkingFNumber), capped at 10,000.",
            Compute = c =>
            {
                var (trace, launcher) = For(c).Tracer(c.System.Wavelengths.PrimaryIndex + 1);
                var w = RealWorkingFNumber.Compute(trace, launcher, 0, 0);
                return w.Succeeded ? w.Value : throw new MeritOperandException($"the working F/# cannot be found: {w.Failure}.");
            },
        };
    }

    private static MeritOperandDefinition Paraxial(string code, string name, string description, bool wave,
        Func<ParaxialSystem, double> value, bool length) => new()
    {
        Code = code, Name = name, Category = FirstOrderCategory, Source = FirstOrderPage, Description = description,
        Int2 = wave ? "Wave" : null,
        Compute = c =>
        {
            var p = c.Paraxial(wave ? c.Wavelength(c.Row.Int2) : c.System.Wavelengths.PrimaryIndex + 1);
            double v = value(p);
            return length ? c.Length(v) : v;
        },
    };

    private static MeritOperandDefinition EflPlane(string code, string name, string plane) => new()
    {
        Code = code, Name = name, Category = FirstOrderCategory, Source = FirstOrderPage,
        Description = $"Effective focal length 1/Φ of surfaces Surf1 to Surf2 in the local {plane} plane, lens units, primary wavelength (rotationally symmetric lenses only: the paraxial engine is axially symmetric).",
        Int1 = "Surf1", Int2 = "Surf2",
        Compute = c =>
        {
            var p = c.Paraxial(c.System.Wavelengths.PrimaryIndex + 1);
            var (a, b) = Range(c, p);
            RequireSymmetric(c, $"the {plane} plane");
            var planes = Engine("the cardinal planes", () => CardinalPlanes.Compute(p, a, b));
            return c.Length(planes.EffectiveFocalLength
                ?? throw new MeritOperandException($"surfaces {a} to {b} are afocal; they have no effective focal length."));
        },
    };

    private static (int A, int B) Range(MeritRowContext c, ParaxialSystem p)
    {
        int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
        int last = p.Lens.LastSurfaceIndex;
        if (a < 1 || b > last || a > b)
            throw new MeritOperandException($"Surf1 {a} to Surf2 {b} must run in order within surfaces 1 to {last} (the last before the image).");
        return (a, b);
    }

    private static void RequireSymmetric(MeritRowContext c, string what)
    {
        if (!MeritEvaluator.IsRotationallySymmetric(c.Lens))
            throw new MeritOperandException($"the lens is not rotationally symmetric (tilts, decenters, coordinate breaks or non-radial surfaces), and {what} of such a lens is not built: the paraxial engine traces one meridional plane.");
    }
}
