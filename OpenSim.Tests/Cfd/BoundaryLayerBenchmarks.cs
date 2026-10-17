using System.Diagnostics;
using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Solvers.Environment;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// The Blasius–Pohlhausen similarity solution of the laminar flat-plate boundary layer,
/// integrated here rather than tabulated: f‴ + ½·f·f″ = 0 with f(0) = f′(0) = 0 and
/// f′(∞) = 1, solved by shooting on f″(0) with fourth-order Runge–Kutta; the isothermal
/// wall's energy equation θ″ + ½·Pr·f·θ′ = 0 integrates in closed form against it,
/// θ′ = θ′(0)·exp(−½Pr∫f) = θ′(0)·(f″/f″(0))^Pr, so θ(∞) = 1 gives
/// θ′(0) = 1 / ∫₀^∞ (f″/f″(0))^Pr dη. The wall shear is τ_w = μu∞√(u∞/νx)·f″(0), i.e.
/// ½·C_f·√Re_x = f″(0), and the local Nusselt number is Nu_x = θ′(0)·√Re_x. Nothing in
/// this class is a typed reference constant: the textbook numbers 0.332 and
/// 0.332·Pr^⅓ are what these integrals round to, which a test below checks.
/// </summary>
internal static class BlasiusSimilarity
{
    private const double EtaMax = 10.0;
    private const int Steps = 20000;

    public sealed record Profile(double WallShear, double[] FPrime, double[] FDoublePrime, double DEta)
    {
        /// <summary>θ′(0) for the given Prandtl number (Simpson on the stored f″).</summary>
        public double WallTemperatureGradient(double prandtl)
        {
            int n = FDoublePrime.Length - 1;
            double sum = 0;
            for (int k = 0; k <= n; k++)
            {
                double w = k == 0 || k == n ? 1 : k % 2 == 1 ? 4 : 2;
                sum += w * Math.Pow(FDoublePrime[k] / WallShear, prandtl);
            }
            return 1.0 / (sum * DEta / 3);
        }

        /// <summary>f′(η) by linear interpolation (1 beyond the table).</summary>
        public double Velocity(double eta)
        {
            double s = eta / DEta;
            int k = (int)Math.Floor(s);
            if (k >= FPrime.Length - 1) return 1.0;
            double w = s - k;
            return FPrime[k] + w * (FPrime[k + 1] - FPrime[k]);
        }

        /// <summary>θ(η) for the given Prandtl number: θ = 1 − θ′(0)·∫₀^η (f″/f″(0))^Pr dη′, so
        /// θ(0) = 1 at the wall and θ → 0 in the stream (the wall-excess form the plate test uses).</summary>
        public double[] TemperatureExcess(double prandtl)
        {
            double gradient = WallTemperatureGradient(prandtl);
            var theta = new double[FDoublePrime.Length];
            double integral = 0;
            theta[0] = 1;
            for (int k = 1; k < theta.Length; k++)
            {
                integral += 0.5 * DEta * (Math.Pow(FDoublePrime[k - 1] / WallShear, prandtl)
                                          + Math.Pow(FDoublePrime[k] / WallShear, prandtl));
                theta[k] = Math.Max(0, 1 - gradient * integral);
            }
            return theta;
        }

        /// <summary>η at which f′ reaches 0.99 — the 99 % thickness is δ = η₉₉·√(νx/u∞).</summary>
        public double Eta99
        {
            get
            {
                for (int k = 1; k < FPrime.Length; k++)
                    if (FPrime[k] >= 0.99)
                        return DEta * (k - 1 + (0.99 - FPrime[k - 1]) / (FPrime[k] - FPrime[k - 1]));
                throw new InvalidOperationException("f′ never reached 0.99.");
            }
        }
    }

    public static Profile Solve()
    {
        double dEta = EtaMax / Steps;
        double s0 = 0.3, s1 = 0.35;
        double g0 = Shoot(s0, dEta, null, null), g1 = Shoot(s1, dEta, null, null);
        for (int it = 0; it < 60 && Math.Abs(g1) > 1e-14; it++)
        {
            double s2 = s1 - g1 * (s1 - s0) / (g1 - g0);
            s0 = s1; g0 = g1;
            s1 = s2; g1 = Shoot(s1, dEta, null, null);
        }
        var fp = new double[Steps + 1];
        var fpp = new double[Steps + 1];
        Shoot(s1, dEta, fp, fpp);
        return new Profile(s1, fp, fpp, dEta);
    }

    /// <summary>Integrates with f″(0) = <paramref name="shear"/>; returns f′(η_max) − 1.</summary>
    private static double Shoot(double shear, double dEta, double[]? fp, double[]? fpp)
    {
        double f = 0, f1 = 0, f2 = shear;
        if (fp is not null) { fp[0] = f1; fpp![0] = f2; }
        for (int k = 0; k < Steps; k++)
        {
            // y = (f, f′, f″), y′ = (f′, f″, −½ f f″).
            var (a0, a1, a2) = Rate(f, f1, f2);
            var (b0, b1, b2) = Rate(f + 0.5 * dEta * a0, f1 + 0.5 * dEta * a1, f2 + 0.5 * dEta * a2);
            var (c0, c1, c2) = Rate(f + 0.5 * dEta * b0, f1 + 0.5 * dEta * b1, f2 + 0.5 * dEta * b2);
            var (d0, d1, d2) = Rate(f + dEta * c0, f1 + dEta * c1, f2 + dEta * c2);
            f += dEta / 6 * (a0 + 2 * b0 + 2 * c0 + d0);
            f1 += dEta / 6 * (a1 + 2 * b1 + 2 * c1 + d1);
            f2 += dEta / 6 * (a2 + 2 * b2 + 2 * c2 + d2);
            if (fp is not null) { fp[k + 1] = f1; fpp![k + 1] = f2; }
        }
        return f1 - 1.0;

        static (double, double, double) Rate(double f, double f1, double f2) => (f1, f2, -0.5 * f * f2);
    }
}

/// <summary>
/// The laminar boundary-layer (Prandtl) equations marched along a wall under a PRESCRIBED
/// free stream u_e(x): u·u_x + v·u_y = u_e·u_e′ + ν·u_yy, u_x + v_y = 0 and
/// u·θ_x + v·θ_y = α·θ_yy, implicit in y (tridiagonal) with one Picard pass per step on the
/// convective coefficients, v from continuity, started with the Blasius profile at x₀. With
/// u_e ≡ u∞ it reproduces Blasius–Pohlhausen (a test checks that); with the free stream a
/// finite-domain CFD solve actually has, it says what wall shear and wall heat flux the
/// laminar layer owes to THAT stream — the exact reference the Blasius constant stops being
/// once the stream accelerates by a few percent.
/// </summary>
internal sealed class BoundaryLayerMarch
{
    private readonly double _nu, _alpha, _dy;
    private readonly int _n;

    public BoundaryLayerMarch(double nu, double alpha, double yMax, int points)
    {
        _nu = nu;
        _alpha = alpha;
        _n = points;
        _dy = yMax / points;
    }

    public sealed record Result(double[] VelocityGradient, double[] TemperatureGradient);

    /// <summary>Marches from x₀ (Blasius start at u_e(x₀)) in steps of <paramref name="dx"/>,
    /// recording the wall gradients ∂u/∂y|₀ and −∂θ/∂y|₀ (second-order one-sided) at each
    /// station, which must be x₀ + a whole number of steps.</summary>
    public Result March(Func<double, double> freeStream, Func<double, double> freeStreamSlope,
        double x0, double dx, IReadOnlyList<double> stations, BlasiusSimilarity.Profile blasius, double prandtl)
    {
        var u = new double[_n + 1];
        var theta = new double[_n + 1];
        var v = new double[_n + 1];
        double ue0 = freeStream(x0);
        double scale = Math.Sqrt(ue0 / (_nu * x0));
        var thetaProfile = blasius.TemperatureExcess(prandtl);
        for (int j = 0; j <= _n; j++)
        {
            double eta = j * _dy * scale;
            u[j] = ue0 * blasius.Velocity(eta);
            int k = (int)Math.Min(Math.Floor(eta / blasius.DEta), thetaProfile.Length - 1);
            theta[j] = thetaProfile[k];
        }
        // v from the similarity solution: v = ½√(νu_e/x)·(ηf′ − f), with f = ∫f′.
        double f = 0;
        for (int j = 1; j <= _n; j++)
        {
            double eta = j * _dy * scale;
            f += 0.5 * _dy * scale * (blasius.Velocity((j - 1) * _dy * scale) + blasius.Velocity(eta));
            v[j] = 0.5 * Math.Sqrt(_nu * ue0 / x0) * (eta * blasius.Velocity(eta) - f);
        }

        var gradU = new double[stations.Count];
        var gradT = new double[stations.Count];
        int next = 0;
        double x = x0;
        if (Math.Abs(stations[0] - x0) < 1e-9 * Math.Max(1, Math.Abs(x0)))
        {
            // The start itself: the similarity profile's gradients.
            gradU[0] = (4 * u[1] - u[2]) / (2 * _dy);
            gradT[0] = (3 * theta[0] - 4 * theta[1] + theta[2]) / (2 * _dy);
            next = 1;
        }
        var a = new double[_n + 1]; var b = new double[_n + 1]; var c = new double[_n + 1]; var r = new double[_n + 1];
        var uNew = new double[_n + 1]; var vNew = new double[_n + 1]; var thetaNew = new double[_n + 1];
        while (next < stations.Count)
        {
            double xNew = x + dx;
            double ue = freeStream(xNew);
            double pressureTerm = ue * freeStreamSlope(xNew);

            // Momentum: two passes, the second with the new field as the convective coefficient.
            Array.Copy(u, uNew, u.Length);
            Array.Copy(v, vNew, v.Length);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int j = 1; j < _n; j++)
                {
                    double uc = Math.Max(uNew[j], 0), vc = vNew[j];
                    a[j] = -vc / (2 * _dy) - _nu / (_dy * _dy);
                    b[j] = uc / dx + 2 * _nu / (_dy * _dy);
                    c[j] = vc / (2 * _dy) - _nu / (_dy * _dy);
                    r[j] = uc * u[j] / dx + pressureTerm;
                }
                Solve(a, b, c, r, uNew, 0.0, ue);
                vNew[0] = 0;
                for (int j = 1; j <= _n; j++)
                    vNew[j] = vNew[j - 1] - 0.5 * _dy * ((uNew[j] - u[j]) + (uNew[j - 1] - u[j - 1])) / dx;
            }
            // Energy with the new velocities.
            for (int j = 1; j < _n; j++)
            {
                double uc = Math.Max(uNew[j], 0), vc = vNew[j];
                a[j] = -vc / (2 * _dy) - _alpha / (_dy * _dy);
                b[j] = uc / dx + 2 * _alpha / (_dy * _dy);
                c[j] = vc / (2 * _dy) - _alpha / (_dy * _dy);
                r[j] = uc * theta[j] / dx;
            }
            Solve(a, b, c, r, thetaNew, 1.0, 0.0);

            Array.Copy(uNew, u, u.Length);
            Array.Copy(vNew, v, v.Length);
            Array.Copy(thetaNew, theta, theta.Length);
            x = xNew;
            if (Math.Abs(x - stations[next]) < 1e-9 * Math.Max(1, Math.Abs(x)))
            {
                gradU[next] = (4 * u[1] - u[2]) / (2 * _dy);
                gradT[next] = (3 * theta[0] - 4 * theta[1] + theta[2]) / (2 * _dy);
                next++;
            }
            else if (x > stations[next] + 1e-9)
                throw new InvalidOperationException($"Station {stations[next]} is not on the march (x = {x}).");
        }
        return new Result(gradU, gradT);
    }

    /// <summary>Thomas algorithm for a[j]·y[j−1] + b[j]·y[j] + c[j]·y[j+1] = r[j], j = 1…n−1,
    /// with y[0] and y[n] given.</summary>
    private void Solve(double[] a, double[] b, double[] c, double[] r, double[] y, double y0, double yN)
    {
        var cp = new double[_n + 1];
        var rp = new double[_n + 1];
        y[0] = y0;
        y[_n] = yN;
        double denominator = b[1];
        cp[1] = c[1] / denominator;
        rp[1] = (r[1] - a[1] * y0) / denominator;
        for (int j = 2; j < _n; j++)
        {
            denominator = b[j] - a[j] * cp[j - 1];
            cp[j] = c[j] / denominator;
            rp[j] = (r[j] - a[j] * rp[j - 1]) / denominator;
        }
        rp[_n - 1] -= cp[_n - 1] * yN;
        cp[_n - 1] = 0;
        y[_n - 1] = rp[_n - 1];
        for (int j = _n - 2; j >= 1; j--)
            y[j] = rp[j] - cp[j] * y[j + 1];
    }
}

/// <summary>
/// External forced convection with the boundary layer RESOLVED on the grid (FU-51; audit
/// gap (c)6): a uniform stream meets a sharp-edged isothermal plate that is the whole
/// bottom of the domain, so the velocity and thermal boundary layers both start at the
/// inlet — the Blasius–Pohlhausen configuration — and the solver's wall heat flux and wall
/// shear are read station by station against the similarity solution integrated in
/// <see cref="BlasiusSimilarity"/>.
/// <para>
/// Setup: a 2-D domain L × 0.4L (one cell deep between symmetry planes), ρ = 1, u∞ = 1,
/// Re_L = u∞L/ν = 2000, Pr = 0.71 (air-like), the wall at θ = 1 with the stream at 0.
/// The top and the downstream faces are pressure outlets so the boundary layer's
/// displacement can leave (a slip lid 0.4L up would accelerate the stream by δ*/H ≈ 14 %
/// at the trailing edge). Stations are read on 0.35L ≤ x ≤ 0.75L: upstream of that the
/// layer is a few cells thick, downstream the outlet is near. On the gate grid
/// (L/120, 120 × 48 cells) the 99 % thickness at x = 0.35L is 8.5 cells (thermal 9.5).
/// </para>
/// <para>
/// What the gate honestly measures. The first run read the wall shear 16 % and the wall
/// heat flux 5 % ABOVE Blasius–Pohlhausen, both excesses shrinking downstream. The
/// diagnostics (outer-row velocity, top outflow, wall pressure, section flux per column)
/// showed why: the leading edge displaces fluid faster than a 0.4L-high open top can let
/// it out, so the stream over the plate accelerates 3.5–3.8 % (1.000 → 1.035 by x = 0.74L,
/// most of it before x = 0.3L) — a mild favourable gradient a laminar layer answers with a
/// disproportionate thinning (Thwaites: θ² ∝ u_e⁻⁶∫u_e⁵dx). The gate therefore compares the
/// solver with the laminar boundary-layer equations MARCHED UNDER THE FREE STREAM THE
/// SOLVER HAS (<see cref="BoundaryLayerMarch"/>, itself checked against Blasius), which is
/// the statement a finite domain can make exactly: given its stream, does the solver grow
/// the laminar layer? Against Blasius as such the headline ratios are recorded and gated
/// loosely as the domain effect they are; a domain several times taller would shrink it in
/// proportion to δ*/H at several times the cost.
/// </para>
/// </summary>
public class BoundaryLayerBenchmarks
{
    private readonly ITestOutputHelper _out;

    public BoundaryLayerBenchmarks(ITestOutputHelper output) => _out = output;

    private const double Length = 1.0;
    private const double Height = 0.4;
    private const double Speed = 1.0;
    private const double ReynoldsL = 2000;
    private const double Prandtl = 0.71;
    private const double WindowStart = 0.35;
    private const double WindowEnd = 0.75;

    private static readonly Lazy<BlasiusSimilarity.Profile> Blasius = new(BlasiusSimilarity.Solve);

    private sealed record PlateRun(int Nx, int Ny, int Steps, double Seconds,
        double[] X, double[] NusseltOverRootRe, double[] ShearOverRootRe, double WallHeat,
        double[] OuterSpeed, double[] TopOutflow, double[] WallPressure, double[] Flux,
        double[] WallVelocityGradient, double[] WallTemperatureGradient)
    {
        public double H => Length / Nx;

        /// <summary>The free stream the solver has, from the outer row: linear between the
        /// column centres, held at the ends.</summary>
        public double FreeStream(double x)
        {
            double s = x / H - 0.5;
            int i = (int)Math.Floor(s);
            if (i < 0) return OuterSpeed[0];
            if (i >= Nx - 1) return OuterSpeed[Nx - 1];
            double w = s - i;
            return OuterSpeed[i] + w * (OuterSpeed[i + 1] - OuterSpeed[i]);
        }

        /// <summary>du_e/dx by central differences of the column samples (one-sided at the ends).</summary>
        public double FreeStreamSlope(double x)
        {
            int i = Math.Clamp((int)Math.Round(x / H - 0.5), 0, Nx - 1);
            int lo = Math.Max(i - 1, 0), hi = Math.Min(i + 1, Nx - 1);
            return (OuterSpeed[hi] - OuterSpeed[lo]) / ((hi - lo) * H);
        }
        public IEnumerable<int> Window => Enumerable.Range(0, X.Length)
            .Where(i => X[i] >= WindowStart * Length && X[i] <= WindowEnd * Length);

        public double MeanNusseltRatio(double reference) => Window.Average(i => NusseltOverRootRe[i]) / reference;
        public double MeanShearRatio(double reference) => Window.Average(i => ShearOverRootRe[i]) / reference;
    }

    private static PlateRun LaminarPlate(int nx)
    {
        double h = Length / nx;
        int ny = (int)Math.Round(Height / h);
        double nu = Speed * Length / ReynoldsL;
        double alpha = nu / Prandtl;

        var grid = new CartesianGrid(nx, ny, 1, new Vector3D(0, 0, 0), h);
        var domain = new VoxelizedDomain(grid, Array.Empty<WallFace>(), grid.CellCount,
            Array.Empty<int>(), Array.Empty<string>());
        var settings = new CfdSettings
        {
            XMinFace = FlowFaceKind.InletVelocity,
            XMaxFace = FlowFaceKind.OutletPressure,
            YMinFace = FlowFaceKind.Wall,
            YMaxFace = FlowFaceKind.OutletPressure,
            ZMinFace = FlowFaceKind.Symmetry,
            ZMaxFace = FlowFaceKind.Symmetry,
            InletVelocity = new Vector3D(Speed, 0, 0),
            MaxSteps = 200000
            // SteadyTolerance left at its default on purpose (audit (c)6).
        };
        var thermal = new FlowThermalOptions { AmbientTemperature = 0.0, YMinTemperature = 1.0 };
        // ρ = 1, μ = ν, k = α, c_p = 1 ⇒ ν, α and Pr = ν/α as intended; β = 0, no gravity.
        var fluid = new FluidState(1.0, nu, alpha, 1.0, 0.0);

        var watch = Stopwatch.StartNew();
        var flow = new IncompressibleFlowSolver(domain, settings, fluid, thermal: thermal).SolveSteady();
        watch.Stop();

        var x = new double[nx];
        var nusselt = new double[nx];
        var shear = new double[nx];
        var outer = new double[nx];
        var top = new double[nx];
        var wallP = new double[nx];
        var flux = new double[nx];
        var gradU = new double[nx];
        var gradT = new double[nx];
        double wallHeat = 0;
        for (int i = 0; i < nx; i++)
        {
            x[i] = (i + 0.5) * h;
            outer[i] = 0.5 * (flow.U[grid.UIndex(i, ny / 2, 0)] + flow.U[grid.UIndex(i + 1, ny / 2, 0)]);
            top[i] = flow.V[grid.VIndex(i, ny, 0)];
            wallP[i] = flow.Pressure[grid.CellIndex(i, 0, 0)];
            for (int j = 0; j < ny; j++) flux[i] += flow.U[grid.UIndex(i, j, 0)] * h;
            double reX = Speed * x[i] / nu;
            // The solver's own wall flux: the half-cell Dirichlet gradient, q = k·(θ_w − θ₀)/(h/2).
            // Nu_x = q·x/(k·Δθ) with Δθ = 1. (Both the Blasius f and θ profiles have zero
            // curvature at the wall, so the half-cell gradient is third-order accurate in h.)
            double theta0 = flow.Temperature![grid.CellIndex(i, 0, 0)];
            double q = 2 * (1.0 - theta0) / h;
            gradT[i] = q;
            nusselt[i] = q * x[i] / Math.Sqrt(reX);
            wallHeat += alpha * q * h;
            // Wall shear from the first row's cell-centred u: τ_w = μ·u₀/(h/2); ½C_f√Re_x = τ_w/(ρu∞²)·√Re_x.
            double u0 = 0.5 * (flow.U[grid.UIndex(i, 0, 0)] + flow.U[grid.UIndex(i + 1, 0, 0)]);
            gradU[i] = 2 * u0 / h;
            shear[i] = nu * gradU[i] / (Speed * Speed) * Math.Sqrt(reX);
        }
        return new PlateRun(nx, ny, flow.Steps, watch.Elapsed.TotalSeconds, x, nusselt, shear, wallHeat,
            outer, top, wallP, flux, gradU, gradT);
    }

    private static BoundaryLayerMarch March() =>
        new(Speed * Length / ReynoldsL, Speed * Length / ReynoldsL / Prandtl, yMax: 0.3 * Length, points: 1200);

    /// <summary>The march's x step: an eighth of the CFD cell, so every column centre is a step.</summary>
    private static double MarchStep(double h) => h / 8;

    [Fact]
    public void SimilaritySolution_IsWhatTheTextbookConstantsRound()
    {
        // The integrated Blasius wall shear f″(0) is the 0.332 of C_f = 0.664/√Re_x, and the
        // Pohlhausen gradient θ′(0) is the 0.332·Pr^⅓ of Nu_x — to the rounding of the
        // textbook constants for gases (Pr ≈ 0.7–1), and within the stated accuracy of the
        // ⅓-power Prandtl law at Pr = 7 (water). The correlation code is thus checked against
        // the exact laminar solution, not against a sibling fit.
        var profile = Blasius.Value;
        _out.WriteLine($"Blasius f″(0) = {profile.WallShear:F6}, η₉₉ = {profile.Eta99:F4}; " +
                       $"θ′(0): Pr 0.71 {profile.WallTemperatureGradient(0.71):F6}, " +
                       $"Pr 1 {profile.WallTemperatureGradient(1.0):F6}, Pr 7 {profile.WallTemperatureGradient(7.0):F6}");

        Assert.InRange(0.332 / profile.WallShear, 0.999, 1.001);
        // Pr = 1: the thermal and velocity layers coincide and θ′(0) = f″(0) exactly.
        Assert.Equal(profile.WallShear, profile.WallTemperatureGradient(1.0), 6);
        foreach (double pr in new[] { 0.71, 1.0 })
            Assert.InRange(ConvectionCorrelations.ForcedFlatPlateLocalLaminar(1.0, pr)
                           / profile.WallTemperatureGradient(pr), 0.99, 1.01);
        Assert.InRange(ConvectionCorrelations.ForcedFlatPlateLocalLaminar(1.0, 7.0)
                       / profile.WallTemperatureGradient(7.0), 0.97, 1.0);
        // The 99 % thickness coefficient the textbooks quote as 4.92 (Example 6.5 of
        // Lienhard & Lienhard uses it) or 5.0.
        Assert.InRange(profile.Eta99, 4.9, 5.0);
    }

    /// <param name="Nusselt">Window mean of the solver's Nu_x/√Re_x over Pohlhausen's θ′(0).</param>
    /// <param name="Shear">Window mean of the solver's ½C_f√Re_x over Blasius's f″(0).</param>
    /// <param name="Heat">The plate's total wall heat over Pohlhausen's average 2θ′(0)√Re_L.</param>
    /// <param name="Acceleration">u_e at the window's end over u_e at the inlet, minus 1.</param>
    /// <param name="VelocityGradient">Window mean of the solver's wall ∂u/∂y over the boundary-layer
    /// march's under the solver's own free stream; <paramref name="VelocitySpread"/> its (max−min)/mean.</param>
    /// <param name="TemperatureGradient">The same for the wall −∂θ/∂y.</param>
    private sealed record Measured(PlateRun Run, double Nusselt, double Shear, double Heat, double Acceleration,
        double VelocityGradient, double VelocitySpread, double TemperatureGradient, double TemperatureSpread);

    private Measured Measure(int nx)
    {
        var profile = Blasius.Value;
        double thetaPrime = profile.WallTemperatureGradient(Prandtl);
        var run = LaminarPlate(nx);
        // Pohlhausen's plate average over 0…L is 2·θ′(0)·√Re_L (in units of k·Δθ).
        double heatRatio = run.WallHeat / (Speed * Length / ReynoldsL / Prandtl * 2 * thetaPrime * Math.Sqrt(ReynoldsL));
        var window = run.Window.ToArray();

        // The laminar layer the solver's free stream owes, marched from the first column.
        var march = March().March(run.FreeStream, run.FreeStreamSlope, run.X[0], MarchStep(run.H), run.X, profile, Prandtl);
        var velocityRatio = window.Select(i => run.WallVelocityGradient[i] / march.VelocityGradient[i]).ToArray();
        var temperatureRatio = window.Select(i => run.WallTemperatureGradient[i] / march.TemperatureGradient[i]).ToArray();

        var m = new Measured(run, run.MeanNusseltRatio(thetaPrime), run.MeanShearRatio(profile.WallShear), heatRatio,
            run.OuterSpeed[window[^1]] / run.OuterSpeed[0] - 1,
            velocityRatio.Average(), (velocityRatio.Max() - velocityRatio.Min()) / velocityRatio.Average(),
            temperatureRatio.Average(), (temperatureRatio.Max() - temperatureRatio.Min()) / temperatureRatio.Average());
        _out.WriteLine($"{run.Nx}×{run.Ny} cells, {run.Steps} steps, {run.Seconds:F0} s: free stream +{m.Acceleration:P2} by the " +
                       $"window's end; against Blasius–Pohlhausen Nu_x/√Re_x = {m.Nusselt:F4}·θ′(0), ½C_f√Re_x = {m.Shear:F4}·f″(0), " +
                       $"plate heat {m.Heat:F4}; against the layer marched under that stream: wall ∂u/∂y {m.VelocityGradient:F4} " +
                       $"(spread {m.VelocitySpread:P2}), wall −∂θ/∂y {m.TemperatureGradient:F4} (spread {m.TemperatureSpread:P2})");
        foreach (int i in window.Where(i => i % (nx / 15) == 0))
            _out.WriteLine($"  x = {run.X[i]:F3}: u_e {run.OuterSpeed[i]:F4}; Nu_x/√Re_x {run.NusseltOverRootRe[i]:F4}, " +
                           $"½C_f√Re_x {run.ShearOverRootRe[i]:F4}; solver/march ∂u/∂y {run.WallVelocityGradient[i] / march.VelocityGradient[i]:F4}, " +
                           $"−∂θ/∂y {run.WallTemperatureGradient[i] / march.TemperatureGradient[i]:F4}");
        return m;
    }

    [Fact]
    public void BoundaryLayerMarch_UnderAUniformStream_IsBlasiusPohlhausen()
    {
        // The march's own check: started with the Blasius profile at the first column and
        // fed a uniform stream, it must stay on the similarity solution — wall gradients
        // f″(0)·√(u∞/νx) and θ′(0)·√(u∞/νx) at every station of the gate window.
        var profile = Blasius.Value;
        double nu = Speed * Length / ReynoldsL, h = Length / 120;
        var stations = Enumerable.Range(0, 120).Select(i => (i + 0.5) * h).ToArray();
        var result = March().March(_ => Speed, _ => 0.0, stations[0], MarchStep(h), stations, profile, Prandtl);
        double thetaPrime = profile.WallTemperatureGradient(Prandtl);
        double worstU = 0, worstT = 0;
        for (int i = 0; i < stations.Length; i++)
        {
            if (stations[i] < WindowStart * Length || stations[i] > WindowEnd * Length) continue;
            double root = Math.Sqrt(Speed / (nu * stations[i]));
            worstU = Math.Max(worstU, Math.Abs(result.VelocityGradient[i] / (profile.WallShear * Speed * root) - 1));
            worstT = Math.Max(worstT, Math.Abs(result.TemperatureGradient[i] / (thetaPrime * root) - 1));
        }
        _out.WriteLine($"march vs similarity over the window: worst ∂u/∂y {worstU:P3}, worst −∂θ/∂y {worstT:P3}");
        Assert.True(worstU < 0.01, $"wall shear off by {worstU:P2}");
        Assert.True(worstT < 0.01, $"wall heat flux off by {worstT:P2}");
    }

    /// <summary>
    /// First measurement. Gate grid L/120 (120 × 48 cells, 3 679 steps, ~7 min): free stream
    /// +3.50 % by the window's end; against the layer marched under that stream the solver's
    /// wall ∂u/∂y reads 1.043 (1.058 → 1.024 across the window) and its wall −∂θ/∂y 1.022
    /// (1.030 → 1.014); against Blasius–Pohlhausen as such Nu_x/√Re_x = 1.055·θ′(0),
    /// ½C_f√Re_x = 1.163·f″(0), the plate's heat 1.138 of Pohlhausen's average. Coarser grid
    /// L/90 (90 × 36, 2 948 steps, ~2.5 min): stream +3.82 %; ∂u/∂y 0.981 (0.990 → 0.968),
    /// −∂θ/∂y 1.017 (1.025 → 1.007); Blasius ratios 1.051 / 1.102 / 1.137. So the laminar
    /// layer under the accelerated stream carries 11–12 % more shear than Blasius on both
    /// grids (1.163/1.043, 1.102/0.981) — the domain effect — and what is left is the
    /// solver's own: wall heat flux within 2–3 % of boundary-layer theory on both grids, wall
    /// shear within 2–6 % with the sign changing between them (first-order upwind, two
    /// counteracting numerical diffusivities), the ratios drifting 2–4 % along the window.
    /// Bands: those measurements with margin.
    /// </summary>
    [Fact]
    public void LaminarPlate_WallHeatAndShear_FollowTheLaminarBoundaryLayer()
    {
        var fine = Measure(120);
        var coarse = Measure(90);

        foreach (var m in new[] { fine, coarse })
        {
            // The free stream the finite domain leaves the layer with (see the class notes).
            Assert.InRange(m.Acceleration, 0.0, 0.06);
            // Against the laminar layer under THAT stream: the solver's wall gradients …
            Assert.InRange(m.VelocityGradient, 0.94, 1.08);
            Assert.InRange(m.TemperatureGradient, 0.98, 1.06);
            // … and how much they drift along the window.
            Assert.True(m.VelocitySpread < 0.06, $"∂u/∂y ratio varies {m.VelocitySpread:P2} across the window");
            Assert.True(m.TemperatureSpread < 0.04, $"−∂θ/∂y ratio varies {m.TemperatureSpread:P2} across the window");
        }
        // Against Blasius–Pohlhausen as such, the headline numbers of the gate grid.
        Assert.InRange(fine.Nusselt, 1.02, 1.09);
        Assert.InRange(fine.Shear, 1.12, 1.21);
        Assert.InRange(fine.Heat, 1.08, 1.20);
    }
}
