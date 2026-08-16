using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// The uniform Cartesian MAC (marker-and-cell) grid the CFD solver runs on: cubic cells
/// of edge <see cref="H"/>, scalar unknowns (pressure, temperature) at cell centers and
/// velocity components on the cell FACES they are normal to — u on x-faces, v on
/// y-faces, w on z-faces. The staggering is the point: the discrete divergence of a
/// face-velocity field and the discrete gradient of a center-pressure field are exact
/// adjoints, so the pressure projection produces a field whose discrete divergence is
/// zero to solver tolerance BY CONSTRUCTION — mass conservation is a machine-checkable
/// identity, not an aspiration.
/// <para>
/// Index conventions (used verbatim by every loop in this project — the deterministic
/// ordering): cell (i, j, k) has flat index i + Nx·(j + Ny·k), i.e. x fastest; loops run
/// k outer, j middle, i inner. A u-face (i, j, k), i ∈ [0, Nx], sits between cells
/// (i−1, j, k) and (i, j, k); v and w faces likewise along y and z.
/// </para>
/// The grid itself is geometry + cell classification only. Field arrays are owned by
/// the solver state and allocated through the helpers here so their lengths can never
/// drift from the grid's.
/// </summary>
public sealed class CartesianGrid
{
    /// <summary>Cell counts along x, y, z.</summary>
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }

    /// <summary>The domain's min corner [m].</summary>
    public Vector3D Origin { get; }

    /// <summary>Cubic cell edge [m].</summary>
    public double H { get; }

    /// <summary>
    /// Which body each cell sits in: −1 = fluid, b ≥ 0 = solid cell of body b (the
    /// assembly's body index). Filled by the <see cref="Voxelizer"/>; every other
    /// consumer treats it as read-only.
    /// </summary>
    public int[] CellBody { get; }

    /// <summary>Marks a fluid cell in <see cref="CellBody"/>.</summary>
    public const int Fluid = -1;

    public CartesianGrid(int nx, int ny, int nz, Vector3D origin, double h)
    {
        if (nx < 1 || ny < 1 || nz < 1)
            throw new ArgumentException($"Grid needs at least one cell per axis (got {nx}×{ny}×{nz}).");
        if (h <= 0 || double.IsNaN(h))
            throw new ArgumentException($"Cell size must be positive (got {h}).", nameof(h));
        checked
        {
            // Overflow-safe: CfdSettings.MaxCells refuses oversized grids upstream, but a
            // direct construction must fail loudly too, never wrap into a negative length.
            int cells = nx * ny * nz;
            _ = cells;
        }
        Nx = nx; Ny = ny; Nz = nz;
        Origin = origin;
        H = h;
        CellBody = new int[nx * ny * nz];
        Array.Fill(CellBody, Fluid);
    }

    /// <summary>Total cell count.</summary>
    public int CellCount => Nx * Ny * Nz;

    /// <summary>Number of u-faces (x-normal): (Nx+1)·Ny·Nz.</summary>
    public int UCount => (Nx + 1) * Ny * Nz;

    /// <summary>Number of v-faces (y-normal): Nx·(Ny+1)·Nz.</summary>
    public int VCount => Nx * (Ny + 1) * Nz;

    /// <summary>Number of w-faces (z-normal): Nx·Ny·(Nz+1).</summary>
    public int WCount => Nx * Ny * (Nz + 1);

    /// <summary>Flat index of cell (i, j, k).</summary>
    public int CellIndex(int i, int j, int k) => i + Nx * (j + Ny * k);

    /// <summary>Flat index of u-face (i, j, k), i ∈ [0, Nx].</summary>
    public int UIndex(int i, int j, int k) => i + (Nx + 1) * (j + Ny * k);

    /// <summary>Flat index of v-face (i, j, k), j ∈ [0, Ny].</summary>
    public int VIndex(int i, int j, int k) => i + Nx * (j + (Ny + 1) * k);

    /// <summary>Flat index of w-face (i, j, k), k ∈ [0, Nz].</summary>
    public int WIndex(int i, int j, int k) => i + Nx * (j + Ny * k);

    /// <summary>Center of cell (i, j, k) [m].</summary>
    public Vector3D CellCenter(int i, int j, int k) => new(
        Origin.X + (i + 0.5) * H,
        Origin.Y + (j + 0.5) * H,
        Origin.Z + (k + 0.5) * H);

    /// <summary>Center of u-face (i, j, k) [m] — on the face plane x = Origin.X + i·H.</summary>
    public Vector3D UFaceCenter(int i, int j, int k) => new(
        Origin.X + i * H,
        Origin.Y + (j + 0.5) * H,
        Origin.Z + (k + 0.5) * H);

    /// <summary>Center of v-face (i, j, k) [m].</summary>
    public Vector3D VFaceCenter(int i, int j, int k) => new(
        Origin.X + (i + 0.5) * H,
        Origin.Y + j * H,
        Origin.Z + (k + 0.5) * H);

    /// <summary>Center of w-face (i, j, k) [m].</summary>
    public Vector3D WFaceCenter(int i, int j, int k) => new(
        Origin.X + (i + 0.5) * H,
        Origin.Y + (j + 0.5) * H,
        Origin.Z + k * H);

    /// <summary>Whether cell (i, j, k) is fluid.</summary>
    public bool IsFluid(int i, int j, int k) => CellBody[CellIndex(i, j, k)] == Fluid;

    /// <summary>Whether the flat cell index is fluid.</summary>
    public bool IsFluidCell(int cell) => CellBody[cell] == Fluid;

    /// <summary>A zeroed cell-centered field (pressure, temperature, divergence).</summary>
    public double[] AllocateCellField() => new double[CellCount];

    /// <summary>A zeroed u-face field.</summary>
    public double[] AllocateUField() => new double[UCount];

    /// <summary>A zeroed v-face field.</summary>
    public double[] AllocateVField() => new double[VCount];

    /// <summary>A zeroed w-face field.</summary>
    public double[] AllocateWField() => new double[WCount];
}
