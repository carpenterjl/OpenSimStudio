using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-57: the lenses and merit functions the local optimization tests share. Every lens is in
/// millimeters with constant indices (relative to air), so no glass catalog is read; radii,
/// thicknesses and indices are arbitrary inputs, not designs from a source.
/// </summary>
internal static class LocalOptimizationFixtures
{
    public static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    public static OpticsCellFlagSettings Variable(string column) => new() { Column = column, Kind = "Variable" };

    public static OpticsCellFlagSettings Solve(string column, string solve, params double[] parameters) =>
        new() { Column = column, Kind = "Solve", Solve = solve, SolveParameters = parameters.ToList() };

    /// <summary>The singlet index (constant).</summary>
    public const double SingletIndex = 1.5;

    /// <summary>The singlet's focal length, mm, held by the F/# solve: F/# = f/EPD = 100/2 = 50.</summary>
    public const double SingletFocalLength = 100;

    /// <summary>
    /// A thin singlet for an object at infinity, stop on its first surface, entrance pupil 2 mm,
    /// centre thickness 0.2 mm, index 1.5. The front radius (start <paramref name="r1"/>) is the
    /// variable; the back curvature is an F/# solve (F/50, so the paraxial focal length stays
    /// 100 mm) and the back distance a marginal-ray-height solve (height 0: the paraxial focus).
    /// Both solves follow the variable in every residual.
    /// </summary>
    public static OpticsSettings Singlet(double r1 = 100, bool r1Variable = true) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 2 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Radius = r1, Thickness = 0.2, Index = SingletIndex, IsStop = true,
                Flags = r1Variable ? new() { Variable("Radius") } : null,
            },
            new()
            {
                Radius = -100, Thickness = 100,
                Flags = new() { Solve("Radius", "F/#", 50), Solve("Thickness", "Marginal ray height", 0, 0) },
            },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(MarginalRayMerit(imageSurface: 3, 1.0)),
    };

    /// <summary>REAY of the on-axis ray at each pupil zone Py on the image surface, target 0,
    /// weight 1: the transverse ray aberration at the paraxial focus.</summary>
    public static MeritFunction MarginalRayMerit(int imageSurface, params double[] zones) =>
        new(zones.Select(py => MeritOperandRow.Of("REAY", imageSurface, 1, target: 0, weight: 1, 0, 0, 0, py)));

    /// <summary>
    /// A cemented doublet (indices 1.5 and 1.65) for an object at infinity, stop on surface 1,
    /// entrance pupil 20 mm. Surface 1's radius is fixed; the junction and back radii are the
    /// variables; the back distance is a marginal-ray-height solve (paraxial focus). The merit
    /// function holds the focal length at 100 mm and the on-axis transverse aberration at five
    /// pupil zones at zero: two variables against six rows, so the optimum has a non-zero merit
    /// and, with the power and the third-order spherical aberration both strongly dependent on
    /// the two curvatures, is an isolated minimum.
    /// </summary>
    public static OpticsSettings Doublet(double r2 = -45, double r3 = -200) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 20 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 60, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = r2, Thickness = 2, Index = 1.65, Flags = new() { Variable("Radius") } },
            new()
            {
                Radius = r3, Thickness = 95,
                Flags = new() { Variable("Radius"), Solve("Thickness", "Marginal ray height", 0, 0) },
            },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(
            new[] { MeritOperandRow.Of("EFFL", 0, 1, target: 100, weight: 1) }
                .Concat(MarginalRayMerit(imageSurface: 4, 1.0, 0.85, 0.7, 0.5, 0.3).Rows))),
    };
}
