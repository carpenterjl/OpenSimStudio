using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Cfd;

public class FlowEnergyTests
{
    private static VoxelizedDomain FluidOnlyDomain(int nx, int ny, int nz, double h)
    {
        var grid = new CartesianGrid(nx, ny, nz, new Vector3D(0, 0, 0), h);
        return new VoxelizedDomain(grid, Array.Empty<WallFace>(), grid.CellCount,
            Array.Empty<int>(), Array.Empty<string>());
    }

    // ================================================================ conduction limit

    [Fact]
    public void SealedCavity_PureConduction_LinearProfileIsExact()
    {
        // No flow, hot x-min face, cold x-max face, everything else adiabatic. The
        // half-cell Dirichlet ghost is exact for linear profiles, so the discrete steady
        // temperature IS T_h − (T_h − T_c)·x/L — a machine-sharp gate.
        var domain = FluidOnlyDomain(8, 4, 1, 1.0 / 8);
        var settings = new CfdSettings { SteadyTolerance = 1e-12, MaxSteps = 20000 };
        var thermal = new FlowThermalOptions
        {
            AmbientTemperature = 0.5,
            XMinTemperature = 1.0,
            XMaxTemperature = 0.0
        };

        var flow = new IncompressibleFlowSolver(domain, settings,
            new FluidState(1, 1, 1, 1, 0), thermal: thermal).SolveSteady();

        var g = flow.Grid;
        double maxErr = 0;
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                double x = (i + 0.5) * g.H;
                double exact = 1.0 - x;
                maxErr = Math.Max(maxErr,
                    Math.Abs(flow.Temperature![g.CellIndex(i, j, 0)] - exact));
            }
        Assert.True(maxErr <= 1e-9, $"conduction profile error {maxErr:E2}");
        // And the velocity field never woke up.
        Assert.True(flow.U.Max(Math.Abs) <= 1e-12);
    }

    // ================================================================ buoyancy-off pin

    [Fact]
    public void ThermalWithoutGravity_LeavesTheMomentumPathBitwise()
    {
        // The null-coupling pin: adding a passive energy equation (no buoyancy, nothing
        // heated) must not move a single bit of the flow solution.
        CfdSettings Settings() => new()
        {
            YMaxFace = FlowFaceKind.InletVelocity,
            InletVelocity = new Vector3D(1, 0, 0),
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-6,
            MaxSteps = 10000
        };
        // α (=k/ρc_p) chosen BELOW ν so the automatic initial Δt is identical.
        var fluid = new FluidState(1, 0.01, 0.005, 1, 0);

        var flowOnly = new IncompressibleFlowSolver(
            FluidOnlyDomain(16, 16, 1, 1.0 / 16), Settings(), fluid).SolveSteady();
        var withThermal = new IncompressibleFlowSolver(
            FluidOnlyDomain(16, 16, 1, 1.0 / 16), Settings(), fluid,
            thermal: new FlowThermalOptions { AmbientTemperature = 300 }).SolveSteady();

        Assert.Equal(flowOnly.Steps, withThermal.Steps);
        Assert.Equal(flowOnly.U, withThermal.U);
        Assert.Equal(flowOnly.V, withThermal.V);
        Assert.Equal(flowOnly.Pressure, withThermal.Pressure);
        // The passive temperature never left ambient.
        Assert.All(withThermal.Temperature!, t => Assert.Equal(300.0, t, 6));
    }

    // ================================================================ heated cavity

    private static (double NuHot, double NuCold, FlowSolution Flow) HeatedCavity(
        double rayleigh, int n)
    {
        // De Vahl Davis square cavity: hot left wall (T=1), cold right wall (T=0),
        // adiabatic top/bottom, gravity −y. Nondimensional: L = ΔT = α = 1, ν = Pr,
        // gβ = Ra·Pr ⇒ Ra = gβ·ΔT·L³/(ν·α).
        const double Pr = 0.71;
        var domain = FluidOnlyDomain(n, n, 1, 1.0 / n);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            SteadyTolerance = 1e-6,
            MaxSteps = 200000
        };
        var thermal = new FlowThermalOptions
        {
            AmbientTemperature = 0.5,
            XMinTemperature = 1.0,
            XMaxTemperature = 0.0,
            Gravity = new Vector3D(0, -rayleigh * Pr, 0)
        };
        var fluid = new FluidState(1, Pr, 1, 1, 1);   // ν = Pr, α = 1, β = 1

        var flow = new IncompressibleFlowSolver(domain, settings, fluid, thermal: thermal)
            .SolveSteady();

        var g = flow.Grid;
        double h = g.H;
        double nuHot = 0, nuCold = 0;
        for (int j = 0; j < g.Ny; j++)
        {
            nuHot += (1.0 - flow.Temperature![g.CellIndex(0, j, 0)]) / (h / 2) * h;
            nuCold += (flow.Temperature![g.CellIndex(g.Nx - 1, j, 0)] - 0.0) / (h / 2) * h;
        }
        return (nuHot, nuCold, flow);
    }

    [Fact]
    public void HeatedCavity_Ra1e3_MatchesDeVahlDavis_AndBalancesEnergy()
    {
        var (nuHot, nuCold, flow) = HeatedCavity(1e3, 48);

        // de Vahl Davis (1983): Nu̅ = 1.118.
        Assert.True(Math.Abs(nuHot - 1.118) <= 0.05 * 1.118,
            $"Ra=1e3 mean Nusselt {nuHot:F4} vs benchmark 1.118");
        // Steady energy balance: heat in at the hot wall ≡ heat out at the cold wall
        // (advective fluxes vanish on the closed boundary; the march residual sets the floor).
        Assert.True(Math.Abs(nuHot - nuCold) <= 0.02 * nuHot,
            $"hot {nuHot:F4} vs cold {nuCold:F4}");
        // Upwind + implicit diffusion is monotone: T stays inside the imposed bounds.
        Assert.All(flow.Temperature!, t => Assert.InRange(t, -1e-9, 1 + 1e-9));
    }

    [Fact]
    public void HeatedCavity_Ra1e4_MatchesDeVahlDavis()
    {
        var (nuHot, nuCold, _) = HeatedCavity(1e4, 64);

        // de Vahl Davis (1983): Nu̅ = 2.243.
        Assert.True(Math.Abs(nuHot - 2.243) <= 0.05 * 2.243,
            $"Ra=1e4 mean Nusselt {nuHot:F4} vs benchmark 2.243");
        Assert.True(Math.Abs(nuHot - nuCold) <= 0.02 * nuHot,
            $"hot {nuHot:F4} vs cold {nuCold:F4}");
    }
}
