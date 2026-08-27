using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;

namespace OpenSim.Tests.Rf;

/// <summary>
/// The shared fixture set for the Stage C2 (multi-layer probe) gates — the Balanis Ex 14.1 patch
/// the shipped single-slab probe tests use, so the two stages are directly comparable.
///
/// <para>The gates live in three classes by TOPIC — the identities, the quasi-static razor, the
/// buried metal plane. Splitting them was first tried to shorten the batch's wall clock (xunit
/// parallelizes across classes but not within one, and a multi-layer probe solve costs ~17 s),
/// and MEASURED not to: 5m41s split against 5m09s as one class. A single solve already saturates
/// every core inside the coupling-table build, so there is nothing left for class-level
/// parallelism to use. The split stays for readability, not for speed.</para>
/// </summary>
internal static class MultiLayerProbeFixtures
{
    public const double PatchW = 1.186e-2;
    public const double PatchL = 0.906e-2;
    public const double MeshEdge = 1.4e-3;

    /// <summary>The coarsest mesh whose snapped probe vertex is still off the rim — measured;
    /// 1.8 mm and beyond put it on the boundary and the attachment fan refuses by name.</summary>
    public const double CoarseEdge = 1.6e-3;

    public const double Thickness = 1.588e-3;
    public const double ProbeRadius = 0.2e-3;

    /// <summary>4 elements over the slab are 0.397 mm, so they need a bore under 0.2 mm to clear
    /// the reduced-kernel element ≳ 2·radius floor.</summary>
    public const double ThinRadius = 0.15e-3;

    public const double ProbeY = -PatchL / 4;
    public const double Frequency = 9.4e9;

    public static readonly SubstrateStackup Substrate = new(2.2, 0.0, Thickness);
    public static readonly LayeredStackup OneLayer = LayeredStackup.FromSubstrate(Substrate);

    public static SurfaceStructure Plate(double edge) =>
        SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, edge, z: Thickness, portFraction: 0, snapVertex: (0.0, ProbeY))
        .Structure!;

    public static LayeredStackup Two(double e1, double h1, double e2, double h2) =>
        new(new[]
        {
            new LayeredStackup.Layer(e1, 0.0, h1),
            new LayeredStackup.Layer(e2, 0.0, h2)
        });

    public static ProbeFeed Probe(double radius = ProbeRadius, int segments = 3) =>
        new(0.0, ProbeY, radius, segments);
}
