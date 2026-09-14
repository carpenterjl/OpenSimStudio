using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// Where a VOXEL no-slip wall sits. A solid cell's surface is the cell boundary between
/// it and the fluid, half a cell from the first tangential velocity face — the same place
/// a domain wall sits, and the same place the thermal stencil already put it. Before
/// WI-5 (D10) the tangential stencil read the face one cell over as a known zero, so
/// every voxel passage was effectively one cell wider than drawn and Δp and wall shear
/// were under-predicted by ≈50 % on a 2-cell bore. These gates pin the wall to the cell
/// boundary: the voxel Poiseuille identity is sharp against the discretisation model at
/// solver tolerance, the square-duct Poiseuille number converges at second order to Shah
/// &amp; London, and the pressure-outlet plane carries the interior stencil unchanged.
/// </summary>
public class VoxelWallTests
{
    private const double Nu = 1.0;
    private const double Gx = 1e-3;

    private static FluidState Fluid(double viscosity) => new(1, viscosity, 1, 1, 0);

    /// <summary>A hand-built voxel domain: solid where the predicate says, wall faces
    /// enumerated the way the voxelizer does (one fluid cell, one solid cell, the solid's
    /// side recorded). The boundary-triangle map is not needed by the flow path.</summary>
    private static VoxelizedDomain Domain(int nx, int ny, int nz, double h,
        Func<int, int, int, bool> solid)
    {
        var grid = new CartesianGrid(nx, ny, nz, new Vector3D(0, 0, 0), h);
        int solids = 0;
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    if (solid(i, j, k))
                    {
                        grid.CellBody[grid.CellIndex(i, j, k)] = 0;
                        solids++;
                    }

        var walls = new List<WallFace>();
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    if (!grid.IsFluid(i, j, k)) continue;
                    int fluid = grid.CellIndex(i, j, k);
                    for (int d = 0; d < 3; d++)
                        for (int s = -1; s <= 1; s += 2)
                        {
                            int ni = i + (d == 0 ? s : 0), nj = j + (d == 1 ? s : 0), nk = k + (d == 2 ? s : 0);
                            if (ni < 0 || nj < 0 || nk < 0 || ni >= nx || nj >= ny || nk >= nz) continue;
                            if (grid.IsFluid(ni, nj, nk)) continue;
                            int fi = i + (d == 0 && s > 0 ? 1 : 0);
                            int fj = j + (d == 1 && s > 0 ? 1 : 0);
                            int fk = k + (d == 2 && s > 0 ? 1 : 0);
                            walls.Add(new WallFace(d, fi, fj, fk, fluid, grid.CellIndex(ni, nj, nk), 0, -1, s > 0));
                        }
                }
        return new VoxelizedDomain(grid, walls, grid.CellCount - solids, new[] { solids },
            Array.Empty<string>());
    }

    private static CfdSettings Faces(FlowFaceKind[] kinds, double steadyTolerance = 1e-11) => new()
    {
        XMinFace = kinds[0], XMaxFace = kinds[1],
        YMinFace = kinds[2], YMaxFace = kinds[3],
        ZMinFace = kinds[4], ZMaxFace = kinds[5],
        SteadyTolerance = steadyTolerance,
        MaxSteps = 200000
    };

    private static double Face(FlowSolution flow, int axis, int i, int j, int k) => axis switch
    {
        0 => flow.U[flow.Grid.UIndex(i, j, k)],
        1 => flow.V[flow.Grid.VIndex(i, j, k)],
        _ => flow.W[flow.Grid.WIndex(i, j, k)]
    };

    private static int Coord(int axis, int i, int j, int k) => axis == 0 ? i : axis == 1 ? j : k;

    private static Vector3D Along(int axis, double value) => axis switch
    {
        0 => new Vector3D(value, 0, 0),
        1 => new Vector3D(0, value, 0),
        _ => new Vector3D(0, 0, value)
    };

    // ================================================================ voxel Poiseuille — EXACT

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void PlanePoiseuilleBetweenVoxelSlabs_IsTheDomainWallClosedForm_OnEveryAxis(
        int flowAxis, int wallAxis)
    {
        // Two solid slabs, two cells thick, inside a larger domain; the fluid channel
        // between them is 8 cells tall. The discrete steady profile must be EXACTLY what
        // the domain-wall gate pins — the parabola plus the half-cell ghost's constant
        // g·h²/(8ν) — measured from the fluid–solid cell boundary. Before the fix the
        // wall sat one cell further out and every face was off by tens of percent.
        const int along = 8, across = 8, span = 4, slab = 2;
        const double H = 1.0;
        double h = H / across;
        int spanAxis = 3 - flowAxis - wallAxis;
        var n = new int[3];
        n[flowAxis] = along;
        n[wallAxis] = across + 2 * slab;
        n[spanAxis] = span;
        var domain = Domain(n[0], n[1], n[2], h, (i, j, k) =>
        {
            int w = Coord(wallAxis, i, j, k);
            return w < slab || w >= slab + across;
        });
        var kinds = new FlowFaceKind[6];
        kinds[2 * flowAxis] = kinds[2 * flowAxis + 1] = FlowFaceKind.OutletPressure;
        kinds[2 * wallAxis] = kinds[2 * wallAxis + 1] = FlowFaceKind.Wall;
        kinds[2 * spanAxis] = kinds[2 * spanAxis + 1] = FlowFaceKind.Symmetry;

        var flow = new IncompressibleFlowSolver(domain, Faces(kinds), Fluid(Nu),
            bodyAcceleration: Along(flowAxis, Gx)).SolveSteady();

        double uMax = Gx * H * H / (8 * Nu);
        double maxErr = 0;
        int solidFaces = 0;
        var g = flow.Grid;
        int d0 = g.Nx + (flowAxis == 0 ? 1 : 0), d1 = g.Ny + (flowAxis == 1 ? 1 : 0), d2 = g.Nz + (flowAxis == 2 ? 1 : 0);
        for (int k = 0; k < d2; k++)
            for (int j = 0; j < d1; j++)
                for (int i = 0; i < d0; i++)
                {
                    int w = Coord(wallAxis, i, j, k) - slab;
                    double u = Face(flow, flowAxis, i, j, k);
                    if (w < 0 || w >= across)
                    {
                        Assert.Equal(0.0, u);       // faces in the slabs never move
                        solidFaces++;
                        continue;
                    }
                    // Every face of the flow component — the two pressure-outlet planes
                    // included, which is where the outlet-plane stencil is proved.
                    double z = (w + 0.5) * h;
                    double exact = Gx / (2 * Nu) * z * (H - z) + Gx * h * h / (8 * Nu);
                    maxErr = Math.Max(maxErr, Math.Abs(u - exact));
                }
        Assert.True(solidFaces > 0);
        Assert.True(maxErr <= 1e-8 * uMax,
            $"voxel Poiseuille (flow {flowAxis}, walls {wallAxis}) error {maxErr:E2} vs u_max {uMax:E2}");
        for (int a = 0; a < 3; a++)
        {
            if (a == flowAxis) continue;
            var other = a == 0 ? flow.U : a == 1 ? flow.V : flow.W;
            Assert.True(other.Max(Math.Abs) <= 1e-9 * uMax, $"component {a} must stay at rest");
        }
    }

    // ================================================================ square duct — Shah & London

    /// <summary>Developed laminar flow in an n×n-cell square voxel duct (unit side, unit
    /// viscosity, body-force driven, pressure outlets at both ends so the field is
    /// axially uniform): the Poiseuille number f·Re = 2·g·W²/(ū·ν), with f the Darcy
    /// friction factor and Re on the side W (= D_h of a square).</summary>
    private static double SquareDuctPoiseuilleNumber(int n)
    {
        const double W = 1.0;
        double h = W / n;
        var domain = Domain(2, n + 2, n + 2, h,
            (i, j, k) => j == 0 || j == n + 1 || k == 0 || k == n + 1);
        var kinds = new[]
        {
            FlowFaceKind.OutletPressure, FlowFaceKind.OutletPressure,
            FlowFaceKind.Wall, FlowFaceKind.Wall, FlowFaceKind.Wall, FlowFaceKind.Wall
        };
        var flow = new IncompressibleFlowSolver(domain, Faces(kinds), Fluid(Nu),
            bodyAcceleration: new Vector3D(Gx, 0, 0)).SolveSteady();

        double sum = 0;
        for (int k = 1; k <= n; k++)
            for (int j = 1; j <= n; j++)
                sum += flow.U[flow.Grid.UIndex(1, j, k)];
        double mean = sum / (n * n);
        return 2 * Gx * W * W / (mean * Nu);
    }

    /// <summary>Shah &amp; London (1978), square duct: f·Re = 56.908 (Fanning 14.227).</summary>
    private const double SquareDuctPoiseuille = 56.908;

    [Fact]
    public void SquareVoxelDuct_PoiseuilleNumberConvergesToShahAndLondon_AtSecondOrder()
    {
        // The duct's walls are grid-aligned, so the only discretisation error is the
        // stencil's own: the half-cell ghost is exact on the quadratic part of the
        // profile and leaves an O(h²) residual, so f·Re must approach 56.908 at SECOND
        // order. A wall imposed a full cell out is a first-order geometry error — it
        // reads (n/(n+1))² low, 21 % at 8 cells — and the observed order collapses to
        // one; that is what the order assertion refuses.
        double fRe8 = SquareDuctPoiseuilleNumber(8);
        double fRe16 = SquareDuctPoiseuilleNumber(16);
        double fRe32 = SquareDuctPoiseuilleNumber(32);
        double err8 = Math.Abs(fRe8 - SquareDuctPoiseuille) / SquareDuctPoiseuille;
        double err16 = Math.Abs(fRe16 - SquareDuctPoiseuille) / SquareDuctPoiseuille;
        double err32 = Math.Abs(fRe32 - SquareDuctPoiseuille) / SquareDuctPoiseuille;
        string story = $"f·Re = {fRe8:F3} / {fRe16:F3} / {fRe32:F3} at 8 / 16 / 32 cells " +
                       $"(errors {err8:E2} / {err16:E2} / {err32:E2})";

        // One-sided: the half-cell ghost adds the POSITIVE constant g·h²/(8ν) to the
        // profile (the plane-channel closed form), so the discrete flow is slightly too
        // large and f·Re approaches 56.908 from BELOW at every resolution.
        Assert.True(fRe8 < SquareDuctPoiseuille && fRe16 < SquareDuctPoiseuille
                    && fRe32 < SquareDuctPoiseuille, "f·Re must approach from below: " + story);
        Assert.True(err16 < err8 && err32 < err16, "error must fall with resolution: " + story);
        Assert.InRange(fRe32 / SquareDuctPoiseuille, 0.90, 1.02);
        double order = Math.Log2(err16 / err32);
        Assert.True(order >= 1.8, $"observed order {order:F2} between 16 and 32 cells; " + story);
    }

    // ================================================================ outlet plane

    [Fact]
    public void PressureOutletPatchFlankedBySolid_CarriesTheInteriorDevelopedProfile()
    {
        // A square voxel duct whose two ends are pressure-outlet PATCHES the size of the
        // bore, the rest of those domain faces covered by the solid shell. The transverse
        // neighbours of an outlet-plane face over the shell are faces whose ONE interior
        // cell is solid: the classifier must see the half-cell wall there exactly as in
        // the interior, or the outlet plane would develop its own, wider profile. Mass
        // balance alone cannot see that — the profile can.
        const int n = 8, along = 6;
        double h = 1.0 / n;
        var domain = Domain(along, n + 2, n + 2, h,
            (i, j, k) => j == 0 || j == n + 1 || k == 0 || k == n + 1);
        double lo = h, hi = (n + 1) * h;
        var settings = Faces(new[]
        {
            FlowFaceKind.Wall, FlowFaceKind.Wall, FlowFaceKind.Wall,
            FlowFaceKind.Wall, FlowFaceKind.Wall, FlowFaceKind.Wall
        }) with
        {
            Openings = new[]
            {
                new FlowOpening { Face = BoxFace.XMin, UMin = lo, UMax = hi, VMin = lo, VMax = hi, Kind = FlowFaceKind.OutletPressure },
                new FlowOpening { Face = BoxFace.XMax, UMin = lo, UMax = hi, VMin = lo, VMax = hi, Kind = FlowFaceKind.OutletPressure }
            }
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(Nu),
            bodyAcceleration: new Vector3D(Gx, 0, 0)).SolveSteady();

        var g = flow.Grid;
        double uMax = 0, maxDiff = 0;
        int flanking = 0;
        for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
            {
                double interior = flow.U[g.UIndex(along / 2, j, k)];
                double outletHi = flow.U[g.UIndex(along, j, k)];
                double outletLo = flow.U[g.UIndex(0, j, k)];
                if (!g.IsFluid(0, j, k))
                {
                    // Solid-covered part of the outlet plane: no flow, ever.
                    Assert.Equal(0.0, outletHi);
                    Assert.Equal(0.0, outletLo);
                    flanking++;
                    continue;
                }
                uMax = Math.Max(uMax, Math.Abs(interior));
                maxDiff = Math.Max(maxDiff, Math.Abs(outletHi - interior));
                maxDiff = Math.Max(maxDiff, Math.Abs(outletLo - interior));
            }
        Assert.True(flanking > 0);
        Assert.True(uMax > 0);
        Assert.True(maxDiff <= 1e-8 * uMax,
            $"outlet-plane profile differs from the interior by {maxDiff:E2} (u_max {uMax:E2})");
    }

    // ================================================================ adjacent inlet + outlet

    [Fact]
    public void AdjacentInletAndOutletPatches_OnOneDomainFace_RunAndConserveMass()
    {
        // A U-turn: a solid divider splits the box, the stream enters through the lower
        // half of the x-min face and leaves through the upper half of the SAME face. The
        // two patches touch, so an active outlet face has a prescribed inlet face as its
        // transverse neighbour on the plane (a known value, kept) while the divider gives
        // it solid neighbours one cell in (the half-cell wall). Everything the classifier
        // distinguishes meets on one plane; the projection's identity must survive it.
        const int nx = 8, ny = 4, nz = 8;
        const double U = 0.1;
        double h = 1.0 / nx;
        var domain = Domain(nx, ny, nz, h, (i, j, k) => i >= 1 && i <= 5 && (k == 3 || k == 4));
        var settings = Faces(new[]
        {
            FlowFaceKind.Wall, FlowFaceKind.Wall,
            FlowFaceKind.Symmetry, FlowFaceKind.Symmetry,
            FlowFaceKind.Wall, FlowFaceKind.Wall
        }, steadyTolerance: 1e-9) with
        {
            Openings = new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0, UMax = ny * h, VMin = 0, VMax = 4 * h,
                    Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(U, 0, 0)
                },
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0, UMax = ny * h, VMin = 4 * h, VMax = nz * h,
                    Kind = FlowFaceKind.OutletPressure
                }
            }
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(0.05)).SolveSteady();

        // What the inlet patch prescribes: 4 × 4 faces at U.
        double inflow = U * 4 * 4 * h * h;
        Assert.True(Math.Abs(flow.InflowRate - inflow) <= 1e-12 * inflow,
            $"inflow {flow.InflowRate:R} vs prescribed {inflow:R}");
        Assert.True(Math.Abs(flow.InflowRate - flow.OutflowRate) <= 1e-9 * inflow,
            $"in {flow.InflowRate:E12} vs out {flow.OutflowRate:E12}");
        Assert.True(flow.MaxDivergence <= 1e-9 * U / h, $"max divergence {flow.MaxDivergence:E2}");
        // The return leg is really flowing: the outlet faces carry the stream back out.
        double outletSpeed = 0;
        for (int k = 4; k < nz; k++)
            for (int j = 0; j < ny; j++)
                outletSpeed = Math.Max(outletSpeed, -flow.U[flow.Grid.UIndex(0, j, k)]);
        Assert.True(outletSpeed > 0.5 * U, $"outlet plane peak {outletSpeed:F4} m/s");
    }
}
