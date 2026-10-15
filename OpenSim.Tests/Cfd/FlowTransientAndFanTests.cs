using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// FU-43: the momentum marched in real time, and fans driven by their curves.
/// </summary>
public class FlowTransientAndFanTests
{
    private readonly ITestOutputHelper _out;
    public FlowTransientAndFanTests(ITestOutputHelper output) => _out = output;

    private static VoxelizedDomain FluidOnlyDomain(double x, double y, double z, int nx, int ny, int nz)
    {
        double h = x / nx;
        var grid = new CartesianGrid(nx, ny, nz, new Vector3D(0, 0, 0), h);
        return new VoxelizedDomain(grid, Array.Empty<WallFace>(), grid.CellCount, Array.Empty<int>(), Array.Empty<string>());
    }

    // ---------------------------------------------------------------- start-up Couette

    [Fact]
    public void StartUpCouette_IsBackwardEulerOnTheDiscreteDiffusion_AndFollowsTheSeries()
    {
        // The lid (z = H) starts moving at U at t = 0 over fluid at rest. The flow stays
        // uniform in x, so advection and the projection do nothing and the march is backward
        // Euler on the cell-centred diffusion with the half-cell ghosts (u = −u₀ below the
        // fixed wall, 2U − u_N−1 above the lid) — reproduced independently here, which makes
        // the gate sharp. The continuous solution is u = Uz/H + (2U/π)Σ (−1)ⁿ/n·sin(nπz/H)·
        // e^(−n²π²νt/H²).
        const double U = 1.0, H = 1.0, nu = 1.0, duration = 0.1;
        const int cells = 8;
        var domain = FluidOnlyDomain(1, 0.5, H, cells, 4, cells);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Wall, ZMaxFace = FlowFaceKind.InletVelocity,
            XMinFace = FlowFaceKind.OutletPressure, XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Symmetry, YMaxFace = FlowFaceKind.Symmetry,
            InletVelocity = new Vector3D(U, 0, 0), SteadyTolerance = 1e-14
        };

        double SeriesAt(double z, double t)
        {
            double u = U * z / H;
            for (int n = 1; n < 400; n++)
                u += 2 * U / Math.PI * (n % 2 == 0 ? 1 : -1) / n * Math.Sin(n * Math.PI * z / H)
                     * Math.Exp(-n * n * Math.PI * Math.PI * nu * t / (H * H));
            return u;
        }

        var errors = new List<double>();
        foreach (int steps in new[] { 20, 40 })
        {
            double dt = duration / steps;
            var solver = new IncompressibleFlowSolver(domain, settings, new FluidState(1, nu, 1, 1, 0));
            solver.ResetVelocity();
            var march = solver.AdvanceFlow(duration, null, maxStep: dt);
            var flow = solver.Snapshot(march.Steps, 0);
            Assert.Equal(steps, march.Steps);

            // Backward Euler on the same stencil.
            double h = H / cells, r = nu * dt / (h * h);
            var u = new double[cells];
            for (int s = 0; s < steps; s++)
            {
                var a = new double[cells]; var b = new double[cells]; var c = new double[cells]; var rhs = (double[])u.Clone();
                for (int k = 0; k < cells; k++)
                {
                    b[k] = 1 + 2 * r;
                    if (k > 0) a[k] = -r;
                    if (k < cells - 1) c[k] = -r;
                }
                b[0] += r;                       // ghost −u₀ under the fixed wall
                b[cells - 1] += r;               // ghost 2U − u above the lid
                rhs[cells - 1] += 2 * r * U;
                for (int k = 1; k < cells; k++)  // Thomas algorithm
                {
                    double m = a[k] / b[k - 1];
                    b[k] -= m * c[k - 1];
                    rhs[k] -= m * rhs[k - 1];
                }
                u[cells - 1] = rhs[cells - 1] / b[cells - 1];
                for (int k = cells - 2; k >= 0; k--) u[k] = (rhs[k] - c[k] * u[k + 1]) / b[k];
            }

            var g = flow.Grid;
            double scheme = 0, series = 0;
            for (int k = 0; k < cells; k++)
            {
                double z = (k + 0.5) * h;
                for (int i = 0; i <= g.Nx; i++)
                    scheme = Math.Max(scheme, Math.Abs(flow.U[g.UIndex(i, 1, k)] - u[k]));
                series = Math.Max(series, Math.Abs(flow.U[g.UIndex(3, 1, k)] - SeriesAt(z, duration)));
            }
            errors.Add(series);
            _out.WriteLine($"{steps} steps: against backward Euler on the stencil {scheme:e2}; against the series {series:e4}");
            Assert.True(scheme < 1e-9, $"{steps} steps: {scheme:e2} from the discrete recurrence");
            Assert.True(flow.W.Max(Math.Abs) < 1e-9);
        }
        // Mostly the time step's error: halving the step takes off a good part of it.
        Assert.True(errors[1] < 0.75 * errors[0], $"series error {errors[0]:e3} → {errors[1]:e3}");
        Assert.True(errors[1] < 0.02 * U);
    }

    [Fact]
    public void AdvanceFlow_SettledFlow_StopsEarly_AndStateRoundTrips()
    {
        var domain = FluidOnlyDomain(1, 0.5, 1, 8, 4, 8);
        var settings = new CfdSettings
        {
            ZMinFace = FlowFaceKind.Wall, ZMaxFace = FlowFaceKind.InletVelocity,
            XMinFace = FlowFaceKind.OutletPressure, XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Symmetry, YMaxFace = FlowFaceKind.Symmetry,
            InletVelocity = new Vector3D(1, 0, 0), SteadyTolerance = 1e-9, MaxSteps = 20000
        };
        var solver = new IncompressibleFlowSolver(domain, settings, new FluidState(1, 1, 1, 1, 0));
        var steady = solver.SolveSteady();
        var saved = solver.SaveState();
        var march = solver.AdvanceFlow(100, null);
        Assert.True(march.Settled);
        Assert.True(march.Steps < march.Planned, $"{march.Steps} of {march.Planned} steps");
        solver.ResetVelocity();
        Assert.True(solver.Snapshot(0, 0).U.Max(Math.Abs) <= 1);   // the lid itself still moves
        solver.RestoreState(saved);
        var back = solver.Snapshot(0, 0);
        for (int n = 0; n < back.U.Length; n++) Assert.Equal(steady.U[n], back.U[n]);
    }

    // ---------------------------------------------------------------- fans

    private const double SlotH = 0.5, SlotW = 0.125, SlotL = 4.0, Cell = 0.0625, Mu = 0.1;

    private static CfdSettings Slot(FanCurve? fan, double speed) => new()
    {
        XMinFace = FlowFaceKind.Wall, XMaxFace = FlowFaceKind.OutletPressure,
        ZMinFace = FlowFaceKind.Wall, ZMaxFace = FlowFaceKind.Wall,
        YMinFace = FlowFaceKind.Symmetry, YMaxFace = FlowFaceKind.Symmetry,
        Openings = new[]
        {
            new FlowOpening
            {
                Face = BoxFace.XMin, UMin = 0, UMax = SlotW, VMin = 0, VMax = SlotH,
                Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(speed, 0, 0), Fan = fan
            }
        },
        SteadyTolerance = 1e-8, MaxSteps = 200000
    };

    /// <summary>The inlet plane's static pressure, read the way a fan sees it: per inlet
    /// cell the line through it and the next cell, half a cell out; the mean.</summary>
    private static double InletPressure(FlowSolution flow)
    {
        var g = flow.Grid;
        double sum = 0;
        int count = 0;
        for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
            {
                sum += 1.5 * flow.Pressure[g.CellIndex(0, j, k)] - 0.5 * flow.Pressure[g.CellIndex(1, j, k)];
                count++;
            }
        return sum / count;
    }

    [Fact]
    public void Fan_SettlesWhereTheSystemLineMeetsTheCurve()
    {
        var domain = FluidOnlyDomain(SlotL, SlotW, SlotH, (int)(SlotL / Cell), (int)(SlotW / Cell), (int)(SlotH / Cell));
        var fluid = new FluidState(1, Mu, 1, 1, 0);
        double area = SlotW * SlotH;

        // The system's line from two fixed flows, read from the pressure field.
        double Resistance(double speed)
        {
            var flow = new IncompressibleFlowSolver(domain, Slot(null, speed), fluid).SolveSteady();
            return InletPressure(flow) / (speed * area);
        }
        double r1 = Resistance(0.05), r2 = Resistance(0.1);
        // Plane Poiseuille on this grid: the discrete profile is the parabola a·z(H − z) plus
        // a·h²/4 (the half-cell ghost), and the cells sum the parabola to a·(H²/6 + h²/12) per
        // unit height — so the flow per width is a·H³/6·(1 + 2h²/H²), a = G/(2μ), and the
        // developed channel's R = 12μL/(W·H³·(1 + 2h²/H²)).
        double poiseuille = 12 * Mu * SlotL / (SlotW * Math.Pow(SlotH, 3) * (1 + 2 * Cell * Cell / (SlotH * SlotH)));
        _out.WriteLine($"system: R = {r1:G6} and {r2:G6} Pa·s/m³ at two flows; developed Poiseuille on this grid {poiseuille:G6}");
        Assert.InRange(r2 / r1, 0.99, 1.01);                  // Stokes-like: linear in Q
        Assert.InRange(r1 / poiseuille, 1.0, 1.1);            // the entrance only adds

        // A straight fan line: P0 at no flow, zero at Qmax. The point is where it meets R·Q.
        const double p0 = 4, qMax = 0.0125;
        var fan = new FanCurve { Points = new[] { new FanPoint(0, p0), new FanPoint(qMax, 0) } };
        double r = 0.5 * (r1 + r2);
        double expected = p0 / (r + p0 / qMax);
        var solver = new IncompressibleFlowSolver(domain, Slot(fan, 1), fluid);
        var result = solver.SolveSteady();
        _out.WriteLine(string.Join("\n", result.Notes.Where(n => n.StartsWith("Fan"))));
        double q = result.InflowRate;
        _out.WriteLine($"operating point {q:G6} m³/s against the line's {expected:G6}; inlet pressure {InletPressure(result):G5} Pa, " +
                       $"the fan's {fan.PressureAt(q):G5} Pa");
        Assert.InRange(q / expected, 0.998, 1.002);
        Assert.True(Math.Abs(InletPressure(result) - fan.PressureAt(q)) <= 2 * IncompressibleFlowSolver.FanTolerance * p0);
        Assert.Contains(result.Notes, n => n.StartsWith("Fan on opening 1"));

        // A curve with a knee: more points, the same rule.
        var knee = new FanCurve { Points = new[] { new FanPoint(0, 5), new FanPoint(0.004, 4.5), new FanPoint(0.008, 2.5), new FanPoint(0.011, 0) } };
        var kneed = new IncompressibleFlowSolver(domain, Slot(knee, 1), fluid).SolveSteady();
        double qk = kneed.InflowRate;
        _out.WriteLine($"knee curve: {qk:G6} m³/s, system {r * qk:G5} Pa, fan {knee.PressureAt(qk):G5} Pa");
        Assert.True(Math.Abs(r * qk - knee.PressureAt(qk)) <= 0.01 * 5);
    }

    [Fact]
    public void Fan_RefusesWhatIsNotAFan()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FanCurve { Points = new[] { new FanPoint(0, 1), new FanPoint(1, 2) } }.Validate());   // rising
        Assert.Throws<InvalidOperationException>(() =>
            new FanCurve { Points = new[] { new FanPoint(0, 1), new FanPoint(0, 0) } }.Validate());   // flows not rising
        var fan = new FanCurve { Points = new[] { new FanPoint(0, 4), new FanPoint(0.01, 1) } };
        Assert.Equal(0.01 + 1 / 300.0, fan.FreeDelivery, 12);                                      // last segment run on
        var domain = FluidOnlyDomain(SlotL, SlotW, SlotH, 64, 2, 8);
        var two = Slot(fan, 1) with
        {
            Openings = Slot(fan, 1).Openings.Concat(new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.ZMin, UMin = 1, UMax = 1.5, VMin = 0, VMax = SlotW, Velocity = new Vector3D(0, 0, 1), Fan = fan
                }
            }).ToList()
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new IncompressibleFlowSolver(domain, two, new FluidState(1, Mu, 1, 1, 0)).SolveSteady());
        Assert.Contains("one fan", ex.Message);
    }
}
