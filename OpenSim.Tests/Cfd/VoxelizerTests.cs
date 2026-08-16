using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

public class VoxelizerTests
{
    private static CfdSettings.ResolvedGrid UnitDomain(double h, int n) =>
        new(new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1)), h, n, n, n,
            Array.Empty<string>());

    // ---------------------------------------------------------------- exact counts

    [Fact]
    public void AxisAlignedBox_ClassifiesExactCellCounts()
    {
        // Domain [0,1]³ at h = 1/8: cell centers at 0.0625 + i·0.125. The solid box
        // [0.25, 0.75]³ contains exactly the centers {0.3125, 0.4375, 0.5625, 0.6875}
        // per axis → 4³ = 64 solid cells, EXACT — no tolerance in this gate.
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 4, 4, 4);

        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, UnitDomain(0.125, 8));

        Assert.Equal(64, domain.SolidCellCounts[0]);
        Assert.Equal(8 * 8 * 8 - 64, domain.FluidCellCount);
        // The solid cells form a 4³ block: its surface is 6·4² = 96 wall faces.
        Assert.Equal(96, domain.WallFaces.Count);
    }

    [Fact]
    public void WallFaces_AlwaysSeparateOneFluidFromOneSolidCell()
    {
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 4, 4, 4);
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, UnitDomain(0.125, 8));

        foreach (var wf in domain.WallFaces)
        {
            Assert.Equal(CartesianGrid.Fluid, domain.Grid.CellBody[wf.FluidCell]);
            Assert.Equal(wf.SolidBody, domain.Grid.CellBody[wf.SolidCell]);
            Assert.Equal(0, wf.SolidBody);
        }
    }

    [Fact]
    public void WallFaces_MapToBoundaryTrianglesOfTheMatchingGeometricFace()
    {
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 4, 4, 4);
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, UnitDomain(0.125, 8));

        // Wall faces strictly interior to each side of the solid block (not on the
        // side's edge ring) sit closest to that geometric face's own triangles: an
        // x-normal wall face with the solid on the + side is the box's x-min face.
        int checkedFaces = 0;
        foreach (var wf in domain.WallFaces)
        {
            if (wf.Axis != 0) continue;
            // Solid cells span grid indices [2,5] in each axis; interior j/k ∈ {3,4}.
            if (wf.J is not (3 or 4) || wf.K is not (3 or 4)) continue;
            var tri = mesh.BoundaryTriangles[wf.BoundaryTriangle];
            int expected = wf.SolidIsHighSide ? StructuredBoxMesh.FaceXMin : StructuredBoxMesh.FaceXMax;
            Assert.Equal(expected, tri.FaceId);
            checkedFaces++;
        }
        Assert.Equal(8, checkedFaces); // 2×2 interior faces on each of the two x sides
    }

    // ---------------------------------------------------------------- multi-body

    [Fact]
    public void TwoBodies_ThroughTheRealMerge_GetPerBodyCountsAndOwnership()
    {
        // Two separated cubes through FeMeshAssembler — the production path. At
        // h = 1/16 the centers inside (0.2, 0.4) are {0.21875, 0.28125, 0.34375}:
        // 3³ = 27 cells per body, exact.
        var material = StructuredBoxMesh.Conductor("k", 100);
        var bodies = new[]
        {
            StructuredBoxMesh.Box("A", 0.2, 0.4, 0.2, 0.4, 0.2, 0.4, 2, 2, 2, material),
            StructuredBoxMesh.Box("B", 0.6, 0.8, 0.6, 0.8, 0.6, 0.8, 2, 2, 2, material)
        };
        var assembled = FeMeshAssembler.Assemble(bodies);

        var domain = Voxelizer.Voxelize(assembled.Mesh, assembled.NodeBases.ToArray(),
            UnitDomain(0.0625, 16));

        Assert.Equal(new[] { 27, 27 }, domain.SolidCellCounts);
        Assert.Equal(16 * 16 * 16 - 54, domain.FluidCellCount);

        // Every wall face's mapped triangle must belong to the wall's own body —
        // the seam the conjugate flux exchange rides on.
        foreach (var wf in domain.WallFaces)
        {
            var tri = assembled.Mesh.BoundaryTriangles[wf.BoundaryTriangle];
            int triBody = tri.A >= assembled.NodeBases[1] ? 1 : 0;
            Assert.Equal(wf.SolidBody, triBody);
        }
        Assert.Equal(2 * 6 * 9, domain.WallFaces.Count); // two 3³ blocks: 6·3² faces each
    }

    // ---------------------------------------------------------------- typed failures

    [Fact]
    public void BodySmallerThanACell_IsATypedFailureNamingTheBody()
    {
        // [0.26, 0.30]³ contains no cell center at h = 1/8 (0.1875 < 0.26, 0.3125 > 0.30).
        var mesh = StructuredBoxMesh.Build(0.26, 0.30, 0.26, 0.30, 0.26, 0.30, 1, 1, 1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Voxelizer.Voxelize(mesh, new[] { 0 }, UnitDomain(0.125, 8)));
        Assert.Contains("occupies no grid cell", ex.Message);
        Assert.Contains("Body 0", ex.Message);
    }

    [Fact]
    public void SolidFillingTheWholeDomain_IsATypedFailure()
    {
        var mesh = StructuredBoxMesh.Build(-0.5, 1.5, -0.5, 1.5, -0.5, 1.5, 2, 2, 2);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Voxelizer.Voxelize(mesh, new[] { 0 }, UnitDomain(0.125, 8)));
        Assert.Contains("no fluid cells", ex.Message);
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void Voxelization_IsBitwiseIdentical_AtAnyDegreeOfParallelism()
    {
        var material = StructuredBoxMesh.Conductor("k", 100);
        var bodies = new[]
        {
            StructuredBoxMesh.Box("A", 0.2, 0.4, 0.2, 0.4, 0.2, 0.4, 2, 2, 2, material),
            StructuredBoxMesh.Box("B", 0.6, 0.8, 0.6, 0.8, 0.6, 0.8, 2, 2, 2, material)
        };
        var assembled = FeMeshAssembler.Assemble(bodies);
        var resolved = UnitDomain(0.0625, 16);

        var serial = Voxelizer.Voxelize(assembled.Mesh, assembled.NodeBases.ToArray(),
            resolved, maxDegreeOfParallelism: 1);
        var parallel = Voxelizer.Voxelize(assembled.Mesh, assembled.NodeBases.ToArray(),
            resolved, maxDegreeOfParallelism: -1);

        Assert.Equal(serial.Grid.CellBody, parallel.Grid.CellBody);
        Assert.Equal(serial.WallFaces, parallel.WallFaces);
        Assert.Equal(serial.FluidCellCount, parallel.FluidCellCount);
    }
}
