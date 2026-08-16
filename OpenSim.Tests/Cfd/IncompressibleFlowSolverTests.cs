using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

public class IncompressibleFlowSolverTests
{
    private static VoxelizedDomain FluidOnlyDomain(double x, double y, double z,
        int nx, int ny, int nz)
    {
        double h = x / nx;
        var grid = new CartesianGrid(nx, ny, nz, new Vector3D(0, 0, 0), h);
        return new VoxelizedDomain(grid, Array.Empty<WallFace>(), grid.CellCount,
            Array.Empty<int>(), Array.Empty<string>());
    }

    private static FluidState Fluid(double density, double viscosity) =>
        new(density, viscosity, 1, 1, 0);

    // ================================================================ Couette — EXACT

    [Fact]
    public void Couette_LinearShearProfile_IsExactToSolverTolerance()
    {
        // Bottom wall fixed, top boundary an InletVelocity face with TANGENTIAL velocity
        // (U, 0, 0) — the moving-no-slip-wall expression. The half-cell reflected ghost
        // is exact for linear profiles, so the discrete steady state IS u = U·z/H and
        // the gate is machine-sharp, not a band.
        const double U = 1.0, H = 1.0;
        var domain = FluidOnlyDomain(1, 0.5, H, 8, 4, 8);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Wall,
            ZMaxFace = FlowFaceKind.InletVelocity,
            XMinFace = FlowFaceKind.OutletPressure,
            XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Symmetry,
            YMaxFace = FlowFaceKind.Symmetry,
            InletVelocity = new Vector3D(U, 0, 0),
            SteadyTolerance = 1e-11,
            MaxSteps = 20000
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(1, 1)).SolveSteady();

        var g = flow.Grid;
        double maxErr = 0;
        for (int k = 0; k < g.Nz; k++)
        {
            double exact = U * (k + 0.5) * g.H / H;
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i <= g.Nx; i++)
                    maxErr = Math.Max(maxErr, Math.Abs(flow.U[g.UIndex(i, j, k)] - exact));
        }
        Assert.True(maxErr <= 1e-8 * U, $"Couette profile error {maxErr:E2}");
        Assert.True(flow.V.Max(Math.Abs) <= 1e-9 * U);
        Assert.True(flow.W.Max(Math.Abs) <= 1e-9 * U);
    }

    // ================================================================ Poiseuille — EXACT

    [Fact]
    public void PlanePoiseuille_MatchesTheClosedFormDiscreteSolution()
    {
        // Body-force-driven channel between z walls. The discrete steady profile is the
        // analytic parabola PLUS the exact constant g·h²/(8ν) — the half-cell Dirichlet
        // ghost's effect on a quadratic, derived in closed form — so this gate is sharp
        // at solver tolerance, with the discretization model built in (the house
        // "sharp against the discretization model" style).
        const double gx = 1e-3, H = 1.0, nu = 1.0;
        var domain = FluidOnlyDomain(1, 0.5, H, 8, 4, 8);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Wall,
            ZMaxFace = FlowFaceKind.Wall,
            XMinFace = FlowFaceKind.OutletPressure,
            XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Symmetry,
            YMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-11,
            MaxSteps = 20000
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(1, 1),
            bodyAcceleration: new Vector3D(gx, 0, 0)).SolveSteady();

        var g = flow.Grid;
        double h = g.H;
        double uMax = gx * H * H / (8 * nu);
        double maxErr = 0;
        for (int k = 0; k < g.Nz; k++)
        {
            double z = (k + 0.5) * h;
            double exact = gx / (2 * nu) * z * (H - z) + gx * h * h / (8 * nu);
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i <= g.Nx; i++)
                    maxErr = Math.Max(maxErr, Math.Abs(flow.U[g.UIndex(i, j, k)] - exact));
        }
        Assert.True(maxErr <= 1e-8 * uMax, $"Poiseuille profile error {maxErr:E2} vs u_max {uMax:E2}");
    }

    // ================================================================ mass conservation

    [Fact]
    public void Projection_LeavesTheDiscreteDivergenceAtSolverTolerance()
    {
        const double U = 1.0;
        var domain = FluidOnlyDomain(1, 0.5, 1, 8, 4, 8);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Wall,
            ZMaxFace = FlowFaceKind.InletVelocity,
            XMinFace = FlowFaceKind.OutletPressure,
            XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Symmetry,
            YMaxFace = FlowFaceKind.Symmetry,
            InletVelocity = new Vector3D(U, 0, 0),
            SteadyTolerance = 1e-11,
            MaxSteps = 20000
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(1, 1)).SolveSteady();

        // The identity the staggered projection buys: discrete divergence at CG scale.
        Assert.True(flow.MaxDivergence <= 1e-7 * U / flow.Grid.H,
            $"max divergence {flow.MaxDivergence:E2}");
        // What enters must leave, to the same scale.
        Assert.True(flow.InflowRate > 0);
        Assert.True(Math.Abs(flow.InflowRate - flow.OutflowRate) <= 1e-7 * flow.InflowRate,
            $"in {flow.InflowRate:E6} vs out {flow.OutflowRate:E6}");
    }

    // ================================================================ obstacle in a channel

    [Fact]
    public void ChannelWithASolidBox_Converges_NoSlipOnWallFaces_MassBalanced()
    {
        // A cube blocking part of a channel — the first solid-interaction gate: the
        // solver must leave every solid-adjacent face at rest and still balance mass.
        var mesh = StructuredBoxMesh.Build(0.5, 0.75, 0.125, 0.375, 0.125, 0.375, 2, 2, 2);
        var resolved = new CfdSettings.ResolvedGrid(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
            0.0625, 24, 8, 8, Array.Empty<string>());
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, resolved);
        var settings = new CfdSettings
        {
            XMinFace = FlowFaceKind.InletVelocity,
            XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Wall,
            YMaxFace = FlowFaceKind.Wall,
            ZMinFace = FlowFaceKind.Wall,
            ZMaxFace = FlowFaceKind.Wall,
            InletVelocity = new Vector3D(0.5, 0, 0),
            SteadyTolerance = 1e-7,
            MaxSteps = 20000
        };

        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(1, 0.05)).SolveSteady();

        var g = flow.Grid;
        // Every face the voxelizer classified as a wall face carries zero normal velocity.
        foreach (var wf in domain.WallFaces)
        {
            double vn = wf.Axis switch
            {
                0 => flow.U[g.UIndex(wf.I, wf.J, wf.K)],
                1 => flow.V[g.VIndex(wf.I, wf.J, wf.K)],
                _ => flow.W[g.WIndex(wf.I, wf.J, wf.K)]
            };
            Assert.Equal(0.0, vn);
        }
        Assert.True(Math.Abs(flow.InflowRate - flow.OutflowRate) <= 1e-6 * flow.InflowRate);
        // The blockage accelerates the bypass flow: somewhere the speed exceeds the inlet.
        Assert.True(flow.MaxSpeed() > 0.5);
    }

    // ================================================================ lid-driven cavity

    /// <summary>Ghia, Ghia &amp; Shin (1982), Re = 100: u along the vertical centerline.</summary>
    private static readonly (double Y, double U)[] GhiaRe100 =
    {
        (0.0547, -0.03717), (0.0625, -0.04192), (0.0703, -0.04775), (0.1016, -0.06434),
        (0.1719, -0.10150), (0.2813, -0.15662), (0.4531, -0.21090), (0.5000, -0.20581),
        (0.6172, -0.13641), (0.7344, 0.00332), (0.8516, 0.23151), (0.9531, 0.68717),
        (0.9609, 0.73722), (0.9688, 0.78871), (0.9766, 0.84123)
    };

    private static double CavityCenterlineError(int n)
    {
        var domain = FluidOnlyDomain(1, 1, 1.0 / n, n, n, 1);
        var settings = new CfdSettings
        {
            YMaxFace = FlowFaceKind.InletVelocity,       // the moving lid
            InletVelocity = new Vector3D(1, 0, 0),
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-6,
            MaxSteps = 60000
        };
        var flow = new IncompressibleFlowSolver(domain, settings, Fluid(1, 0.01))
            { LinearTolerance = 1e-9 }.SolveSteady();

        var g = flow.Grid;
        int iMid = n / 2;                                // the u-face ON x = 0.5
        double maxErr = 0;
        foreach (var (y, uRef) in GhiaRe100)
        {
            // Linear interpolation between the two u-face rows straddling y.
            double t = y / g.H - 0.5;
            int j0 = Math.Clamp((int)Math.Floor(t), 0, g.Ny - 2);
            double f = t - j0;
            double u = (1 - f) * flow.U[g.UIndex(iMid, j0, 0)]
                     + f * flow.U[g.UIndex(iMid, j0 + 1, 0)];
            maxErr = Math.Max(maxErr, Math.Abs(u - uRef));
        }
        return maxErr;
    }

    [Fact]
    public void LidDrivenCavity_Re100_MatchesGhia_AndRefinesTowardIt()
    {
        // First-order upwind carries numerical diffusion — the 3%-of-U_lid band is the
        // honest accuracy statement for this scheme, and the coarse run must be WORSE
        // (the refinement trend is the scheme working, not luck). Gated at 64² because
        // the Debug suite must stay runnable (measured: 64² ≈ 1,540 steps / 17.5 s
        // Release, max centerline error 0.0088 of U_lid; the 128² run was verified
        // out-of-suite in Release — a gate the suite cannot practically run protects
        // nothing, the relative-perf-guard precedent).
        double err32 = CavityCenterlineError(32);
        double err64 = CavityCenterlineError(64);

        Assert.True(err64 <= 0.03, $"64² centerline error {err64:F4} of U_lid");
        Assert.True(err64 < err32,
            $"refinement must reduce the error (32²: {err32:F4}, 64²: {err64:F4})");
    }

    // ================================================================ determinism

    [Fact]
    public void SteadyMarch_IsBitwiseIdentical_AtAnyDegreeOfParallelism()
    {
        var settingsTemplate = new CfdSettings
        {
            YMaxFace = FlowFaceKind.InletVelocity,
            InletVelocity = new Vector3D(1, 0, 0),
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-5,
            MaxSteps = 5000
        };

        FlowSolution Run(int dop)
        {
            var domain = FluidOnlyDomain(1, 1, 1.0 / 16, 16, 16, 1);
            return new IncompressibleFlowSolver(domain, settingsTemplate, Fluid(1, 0.01),
                maxDegreeOfParallelism: dop).SolveSteady();
        }

        var serial = Run(1);
        var parallel = Run(-1);

        Assert.Equal(serial.Steps, parallel.Steps);
        Assert.Equal(serial.U, parallel.U);
        Assert.Equal(serial.V, parallel.V);
        Assert.Equal(serial.W, parallel.W);
        Assert.Equal(serial.Pressure, parallel.Pressure);
    }

    // ================================================================ typed failures

    [Fact]
    public void ReynoldsFarBeyondLaminar_IsATypedFailure()
    {
        var domain = FluidOnlyDomain(1, 1, 1.0 / 8, 8, 8, 1);
        var settings = new CfdSettings
        {
            YMaxFace = FlowFaceKind.InletVelocity,
            InletVelocity = new Vector3D(1, 0, 0),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new IncompressibleFlowSolver(domain, settings, Fluid(1, 1e-6)));
        Assert.Contains("laminar", ex.Message);
    }

    [Fact]
    public void ClosedBoxWithNetInflow_IsATypedFailure()
    {
        var domain = FluidOnlyDomain(1, 1, 1, 4, 4, 4);
        var settings = new CfdSettings
        {
            // All walls, plus one inlet opening blowing in with nowhere to leave.
            Openings = new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0, UMax = 1, VMin = 0, VMax = 1,
                    Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(0.1, 0, 0)
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new IncompressibleFlowSolver(domain, settings, Fluid(1, 1)));
        Assert.Contains("unbalanced", ex.Message);
    }

    [Fact]
    public void ExhaustedMaxSteps_IsATypedFailureWithTheResidualStory()
    {
        var domain = FluidOnlyDomain(1, 1, 1.0 / 8, 8, 8, 1);
        var settings = new CfdSettings
        {
            YMaxFace = FlowFaceKind.InletVelocity,
            InletVelocity = new Vector3D(1, 0, 0),
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-14,      // unreachable in...
            MaxSteps = 3                  // ...three steps
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new IncompressibleFlowSolver(domain, settings, Fluid(1, 0.01)).SolveSteady());
        Assert.Contains("did not converge", ex.Message);
    }
}
