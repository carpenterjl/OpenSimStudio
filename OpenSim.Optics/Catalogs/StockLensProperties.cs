using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Catalogs;

// OP-16: the search data of a lens (docs/Optics-Conventions.md section 53).

/// <summary>The search data of a lens file: what <see cref="StockLens"/> lists besides the names.</summary>
public sealed record StockLensData(double? EffectiveFocalLengthMm, double? EntrancePupilDiameterMm,
    StockLensShape Shape, StockLensType Type, int Elements);

/// <summary>A group of surfaces of the current lens taken on its own (Lens Catalogs' default
/// search ranges).</summary>
/// <param name="EffectiveFocalLengthMm">The group's paraxial EFL in air at the primary wavelength, mm; null when afocal.</param>
/// <param name="DiameterMm">Twice the largest clear semi-diameter of the group, mm.</param>
public sealed record StockLensGroup(double? EffectiveFocalLengthMm, double DiameterMm);

/// <summary>
/// The Lens Catalogs tool's search data of a lens (section 53): shape, type, number of elements,
/// effective focal length and entrance pupil diameter, the last two from the paraxial engine
/// (section 11) at the file's primary wavelength, in millimeters.
/// </summary>
public static class StockLensProperties
{
    /// <summary>Two radii whose magnitudes differ by no more than this, relative to the larger,
    /// make an equi-convex or equi-concave lens. This program's choice (the help gives no rule):
    /// vendor files write the two radii of an equi lens with the same digits, so they are equal
    /// exactly; the margin only absorbs a unit conversion's rounding.</summary>
    public const double EquiTolerance = 1e-9;

    /// <summary>The lens surfaces (1 to N − 2) followed by a glass, and the mirrors.</summary>
    public static int ElementCount(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        int n = 0;
        for (int j = 1; j < surfaces.Count - 1; j++)
            if (!SavedSurfaces.IsAir(surfaces[j])) n++;
        return n;
    }

    /// <summary>
    /// The shape class (section 53): a lens of more or fewer than one element, or with a mirror,
    /// is Other (the help: "Other is used if the lens is multi-element"); a singlet, from the
    /// radii of the surface before its glass and the one after: Plano when either is a plane,
    /// Meniscus when both centres of curvature lie on the same side, Equi when they lie on
    /// opposite sides with equal magnitudes (<see cref="EquiTolerance"/>), Bi otherwise.
    /// </summary>
    public static StockLensShape Shape(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        if (ElementCount(surfaces) != 1) return StockLensShape.Other;
        int j = Enumerable.Range(1, surfaces.Count - 2).First(k => !SavedSurfaces.IsAir(surfaces[k]));
        if (SavedSurfaces.IsMirror(surfaces[j]) || j + 1 > surfaces.Count - 2) return StockLensShape.Other;
        var a = surfaces[j];
        var b = surfaces[j + 1];
        if (SavedSurfaces.IsPlane(a) || SavedSurfaces.IsPlane(b)) return StockLensShape.Plano;
        double r1 = a.Radius!.Value, r2 = b.Radius!.Value;
        if (Math.Sign(r1) == Math.Sign(r2)) return StockLensShape.Meniscus;
        return Math.Abs(Math.Abs(r1) - Math.Abs(r2)) <= EquiTolerance * Math.Max(Math.Abs(r1), Math.Abs(r2))
            ? StockLensShape.Equi : StockLensShape.Bi;
    }

    /// <summary>
    /// The type class (section 53), over the lens surfaces: GRIN when a surface type's name says
    /// gradient index (none is read from a .zmx in this build), else Toroidal when a surface is
    /// Toroidal, else Aspheric when a surface is an asphere type or has a non-zero conic, else
    /// Spherical. The help does not say which class wins when a lens has several; this order is
    /// this program's.
    /// </summary>
    public static StockLensType Type(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        var lens = surfaces.Skip(1).Take(Math.Max(0, surfaces.Count - 2)).ToList();
        static bool Named(OpticsSurfaceSettings s, string part) => s.Type?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;
        if (lens.Any(s => Named(s, "Gradient") || Named(s, "GRIN"))) return StockLensType.Grin;
        if (lens.Any(s => Named(s, "Toroidal"))) return StockLensType.Toroidal;
        if (lens.Any(s => Named(s, "Asphere") || s.Conic != 0)) return StockLensType.Aspheric;
        return StockLensType.Spherical;
    }

    /// <summary>
    /// The search data of a lens read from a file. The focal length and pupil come from the
    /// paraxial engine on the lens with its solves evaluated (<see cref="LensEditContext.ToLens"/>)
    /// and the file's own system data; the lens's glasses are looked up in its active catalogs
    /// (every catalog given when it names none), <paramref name="extraCatalogs"/> (a .zar's own)
    /// first. Throws <see cref="ArgumentException"/> naming every problem when the lens cannot
    /// be traced.
    /// </summary>
    public static StockLensData Of(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, IReadOnlyList<OpticalCatalog>? extraCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var surfaces = optics.Surfaces ?? throw new ArgumentException("The lens has no surfaces.");
        if (surfaces.Count < 3) throw new ArgumentException("The lens needs the object, at least one surface and the image.");
        var context = new LensEditContext(optics.SystemData, CatalogsFor(optics, catalogs, extraCatalogs));
        var lens = context.ToLens(surfaces);
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), context.System());
        return new StockLensData(
            model.IsAfocal ? null : Millimeters(model.EffectiveFocalLength),
            Millimeters(model.EntrancePupilDiameter),
            Shape(surfaces), Type(surfaces), ElementCount(surfaces));
    }

    /// <summary>
    /// Surfaces <paramref name="first"/> to <paramref name="last"/> of the current lens taken on
    /// their own in air, as the Lens Catalogs tool takes the surface group it opens on ("OpticStudio
    /// computes the lens focal length and diameter and adds plus/minus 5% to define the default
    /// search ranges", help, Lens Catalogs): the EFL of the group alone (object at infinity, stop
    /// on its first surface, flags removed, the medium before it air) at the primary wavelength,
    /// and twice the largest clear semi-diameter of the group in the whole lens
    /// (<see cref="ApertureTools.ClearSemiDiameters"/>). Millimeters.
    /// </summary>
    public static StockLensGroup OfGroup(IReadOnlyList<OpticsSurfaceSettings> surfaces, int first, int last, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(context);
        if (surfaces.Count < 3 || first < 1 || last > surfaces.Count - 2 || first > last)
            throw new ArgumentException($"Lens Catalogs: the surface group must lie within 1 to {surfaces.Count - 2} with the first not after the last (it is {first} to {last}).");
        double perUnit = OpticsConventions.MetersPer(context.Unit) * 1000;
        double[] clear = ApertureTools.ClearSemiDiameters(surfaces, context);
        double diameter = 0;
        for (int j = first; j <= last; j++) diameter = Math.Max(diameter, 2 * clear[j]);

        var group = new List<OpticsSurfaceSettings> { new() { ThicknessIsInfinite = true } };
        for (int j = first; j <= last; j++)
            group.Add(surfaces[j] with { Flags = null, IsStop = j == first, TiltBefore = null, TiltAfter = null, CoordinateReturn = null });
        group.Add(new OpticsSurfaceSettings());
        var system = (context.SystemData ?? new OpticsSystemSettings()) with
        {
            ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
            ApertureValue = diameter > 0 ? diameter : 1,
            FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
            Fields = new() { new() },
            TelecentricObjectSpace = false,
            AfocalImageSpace = false,
            RayAiming = null,
        };
        var groupContext = new LensEditContext(system, context.Catalogs);
        var model = ParaxialSystem.Create(groupContext.ToLens(group).ToParaxialLens(), groupContext.System());
        return new StockLensGroup(model.IsAfocal ? null : Millimeters(model.EffectiveFocalLength), diameter * perUnit);
    }

    /// <summary>The catalogs a lens file's glasses are found in: <paramref name="extra"/> first, then
    /// the file's active catalogs among <paramref name="catalogs"/> (all of them when it names none).</summary>
    internal static IReadOnlyList<OpticalCatalog> CatalogsFor(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, IReadOnlyList<OpticalCatalog>? extra)
    {
        var names = optics.Materials?.ActiveCatalogs is { Count: > 0 } active ? active : null;
        var list = new List<OpticalCatalog>(extra ?? Array.Empty<OpticalCatalog>());
        list.AddRange(GlassLookup.ActiveCatalogs(catalogs, names));
        return list;
    }

    private static double? Millimeters(double? meters) => meters is double m ? m * 1000 : null;
}
