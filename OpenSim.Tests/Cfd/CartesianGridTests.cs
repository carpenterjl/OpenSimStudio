using OpenSim.Cfd;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Cfd;

public class CartesianGridTests
{
    // The index formulas are the load-bearing contract: every solver loop, scatter and
    // flux sweep uses them. Prove each is a bijection onto [0, count) by marking hits.

    [Fact]
    public void CellIndex_IsABijection()
    {
        var g = new CartesianGrid(3, 4, 5, new Vector3D(0, 0, 0), 1.0);
        var hit = new bool[g.CellCount];
        for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                {
                    int idx = g.CellIndex(i, j, k);
                    Assert.False(hit[idx]);
                    hit[idx] = true;
                }
        Assert.All(hit, h => Assert.True(h));
    }

    [Fact]
    public void FaceIndices_AreBijections_WithTheDocumentedCounts()
    {
        var g = new CartesianGrid(3, 4, 5, new Vector3D(0, 0, 0), 1.0);
        Assert.Equal(4 * 4 * 5, g.UCount);
        Assert.Equal(3 * 5 * 5, g.VCount);
        Assert.Equal(3 * 4 * 6, g.WCount);

        var hitU = new bool[g.UCount];
        for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i <= g.Nx; i++)
                {
                    int idx = g.UIndex(i, j, k);
                    Assert.False(hitU[idx]);
                    hitU[idx] = true;
                }
        Assert.All(hitU, h => Assert.True(h));

        var hitV = new bool[g.VCount];
        for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j <= g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                {
                    int idx = g.VIndex(i, j, k);
                    Assert.False(hitV[idx]);
                    hitV[idx] = true;
                }
        Assert.All(hitV, h => Assert.True(h));

        var hitW = new bool[g.WCount];
        for (int k = 0; k <= g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                {
                    int idx = g.WIndex(i, j, k);
                    Assert.False(hitW[idx]);
                    hitW[idx] = true;
                }
        Assert.All(hitW, h => Assert.True(h));
    }

    [Fact]
    public void CellAndFaceCenters_AreExactStaggeredPositions()
    {
        var g = new CartesianGrid(4, 4, 6, new Vector3D(1, 2, 3), 0.5);

        Assert.Equal(new Vector3D(1.25, 2.25, 3.25), g.CellCenter(0, 0, 0));
        Assert.Equal(new Vector3D(2.25, 2.75, 3.75), g.CellCenter(2, 1, 1));

        // u-face i = 2 sits ON the plane x = 1 + 2·0.5, centered in y and z.
        Assert.Equal(new Vector3D(2.0, 2.75, 3.25), g.UFaceCenter(2, 1, 0));
        // v-face j = 3 on y = 2 + 1.5.
        Assert.Equal(new Vector3D(1.75, 3.5, 4.25), g.VFaceCenter(1, 3, 2));
        // w-face k = 6 is the domain's top plane.
        Assert.Equal(new Vector3D(1.25, 2.25, 6.0), g.WFaceCenter(0, 0, 6));
    }

    [Fact]
    public void EveryCellStartsFluid()
    {
        var g = new CartesianGrid(2, 2, 2, new Vector3D(0, 0, 0), 1.0);
        Assert.All(g.CellBody, b => Assert.Equal(CartesianGrid.Fluid, b));
        Assert.True(g.IsFluid(1, 1, 1));
    }

    [Fact]
    public void AllocatedFields_MatchTheGridCounts()
    {
        var g = new CartesianGrid(3, 4, 5, new Vector3D(0, 0, 0), 1.0);
        Assert.Equal(g.CellCount, g.AllocateCellField().Length);
        Assert.Equal(g.UCount, g.AllocateUField().Length);
        Assert.Equal(g.VCount, g.AllocateVField().Length);
        Assert.Equal(g.WCount, g.AllocateWField().Length);
    }

    [Fact]
    public void InvalidConstruction_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CartesianGrid(0, 1, 1, new Vector3D(0, 0, 0), 1.0));
        Assert.Throws<ArgumentException>(() => new CartesianGrid(1, 1, 1, new Vector3D(0, 0, 0), 0.0));
        Assert.Throws<ArgumentException>(() => new CartesianGrid(1, 1, 1, new Vector3D(0, 0, 0), double.NaN));
    }
}
