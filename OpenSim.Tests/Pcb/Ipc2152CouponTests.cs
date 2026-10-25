using OpenSim.Core.Geometry2D;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Power;
using OpenSim.Pcb.Thermal;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// FU-10: the board electro-thermal solve against IPC-2152's external-trace heating. The
/// standard itself is not on this machine; the reference is Brooks and Adam's published fit
/// of its 2 oz and 3 oz external charts (<see cref="Ipc2152.ExternalRise"/>), and the
/// board is the coupon of IPC-TM-650 2.5.4.1A the charts were measured on
/// (<see cref="IpcTm650Coupon"/>): one conductor powered, the board in still air at 25 °C
/// with no planes, the conductor temperature the MEAN over the 6 in test section as the
/// method's resistance measurement gives it. The laminate is meshed 20 mm around the
/// conductor (the far board is at ambient; Brooks and Adam's own TRM model of the coupon was
/// a 350 × 45 mm board, which this margin reproduces).
/// </summary>
public class Ipc2152CouponTests
{
    private readonly ITestOutputHelper _output;
    public Ipc2152CouponTests(ITestOutputHelper output) => _output = output;

    private const double Ambient = Ipc2152.MethodReferenceTemperature;

    [Fact]
    public void Fit_ReproducesThePublishedExamples()
    {
        double mil = Ipc2152.Mil;
        // UltraCAD manual p. 4: a 100 mil, 1 oz external trace at 10 A rises 79.9 °C.
        Assert.Equal(79.9, Ipc2152.ExternalRise(10, 100 * mil, Ipc2152.OunceThickness(1)), 0.05);
        // Brooks & Adam Figure 6-1: 100 mil, 2 oz, 10 A, "just under 40 °C".
        double twoOunce = Ipc2152.ExternalRise(10, 100 * mil, Ipc2152.OunceThickness(2));
        Assert.InRange(twoOunce, 39.5, 40.0);
        // Twice the thickness halves the rise (Th⁻¹); the inverse returns the current.
        Assert.Equal(0.5, twoOunce / Ipc2152.ExternalRise(10, 100 * mil, Ipc2152.OunceThickness(1)), 1e-12);
        Assert.Equal(10.0, Ipc2152.ExternalCurrent(twoOunce, 100 * mil, Ipc2152.OunceThickness(2)), 1e-9);
        // The rise is not a function of area alone: 100 mil × 1 oz and 50 mil × 2 oz share
        // their area, and the narrower, thicker one runs hotter (W⁻¹·¹⁵ against Th⁻¹).
        Assert.True(Ipc2152.ExternalRise(10, 50 * mil, Ipc2152.OunceThickness(2)) >
                    1.05 * Ipc2152.ExternalRise(10, 100 * mil, Ipc2152.OunceThickness(1)));
        // The method's copper: 10.827 Ω·cmil/ft is 1.80 µΩ·cm.
        Assert.Equal(1.80e-8, Ipc2152.MethodResistivity(1), 0.005e-8);
        Assert.True(Ipc2152.MethodResistivity(0.5) > Ipc2152.MethodResistivity(1));
        var copper = Ipc2152.MethodCopper(2);
        Assert.Equal(1 / Ipc2152.MethodResistivity(2), copper.ElectricalConductivityAt(Ambient), 1);
        Assert.Equal(Ambient, copper.ResistivityReferenceTemperature);
        // 1/α25 = 1/α20 + 5.
        Assert.Equal(1 / 0.00393 + 5, 1 / copper.ResistivityTemperatureCoefficient!.Value, 1e-9);
    }

    [Fact]
    public void Coupon_IsTheMethodsTestPattern()
    {
        Assert.Equal(IpcTm650Coupon.TestSectionLength, IpcTm650Coupon.TabX1 - IpcTm650Coupon.TabX0, 1e-12);
        Assert.Equal(0.5 * 0.3048, IpcTm650Coupon.TestSectionLength, 1e-12);          // "L = 0.5 ft"
        Assert.Equal(12 * 25.4e-3, IpcTm650Coupon.ConductorLength, 1e-12);
        Assert.True(IpcTm650Coupon.ConductorY(0) > IpcTm650Coupon.ConductorY(3));
        Assert.True(IpcTm650Coupon.ConductorY(0) + IpcTm650Coupon.TerminalDiameter < IpcTm650Coupon.BoardWidth);

        var (net, pads) = IpcTm650Coupon.Conductor(3);
        double w = IpcTm650Coupon.ConductorWidths[3], r = IpcTm650Coupon.TerminalDiameter / 2;
        // Copper area: the trace between the disc centres plus the two discs less the trace
        // inside them — the discs' area plus the trace outside them (chord geometry).
        double theta0 = Math.Asin(w / 2 / r);
        double segment = r * r * (theta0 - Math.Sin(theta0) * Math.Cos(theta0));   // disc area cut by the trace's edge… twice per disc
        double discsPlusTrace = 2 * Math.PI * r * r + w * (IpcTm650Coupon.ConductorLength - 2 * r * Math.Cos(theta0));
        Assert.Equal(discsPlusTrace, net.Area, discsPlusTrace * 0.01);          // 24-gon discs
        Assert.Equal(2, pads.Count);
        Assert.Equal("P", pads[0].ComponentRef);
        Assert.Equal(IpcTm650Coupon.TerminalX1, pads[1].Center.X, 1e-12);
        Assert.True(IpcTm650Coupon.InTestSection(0.5 * (IpcTm650Coupon.TerminalX0 + IpcTm650Coupon.TerminalX1)));
        Assert.False(IpcTm650Coupon.InTestSection(IpcTm650Coupon.TerminalX0 + 1e-3));
        _ = segment;

        Assert.Equal(0.4, IpcTm650Coupon.Polyimide.ThermalConductivity);
        Assert.Equal(0.070 * 25.4e-3, IpcTm650Coupon.BoardThickness, 1e-15);
    }

    // ---------------- The coupon solved ----------------

    private sealed record CouponRun(RailThermalReport Report, FeMesh Mesh, Material Copper, double Fit,
        double MeanRise, double RiseFromResistance, double PeakRise, double TopFilm, double BottomFilm);

    /// <summary>One conductor of the coupon at one current, with the given cooling.</summary>
    private CouponRun Solve(double ounces, int conductor, double amps, EnvironmentSettings? environment,
        IReadOnlyList<BoundaryCondition>? conditions = null, double edge = 1.5e-3, int gapLayers = 1)
    {
        var (net, pads) = IpcTm650Coupon.Conductor(conductor);
        var polyimide = IpcTm650Coupon.Polyimide;
        var copper = Ipc2152.MethodCopper(ounces);
        double thickness = Ipc2152.OunceThickness(ounces), width = IpcTm650Coupon.ConductorWidths[conductor];
        var options = new NetMeshOptions
        {
            TargetEdgeLength = edge, CopperThickness = thickness,
            DefaultDielectricThickness = IpcTm650Coupon.BoardThickness
        };
        var board = new BoardBodyOptions
        {
            Outline = new[] { IpcTm650Coupon.Outline }, CopperLayerCount = 2, Margin = 20e-3, GapSubdivisions = gapLayers,
            DielectricMaterialName = polyimide.Name
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var fe = mesh.Body.Mesh!;
        _output.WriteLine($"mesh: {fe.NodeCount} nodes, {fe.ElementCount} elements in {sw.Elapsed.TotalSeconds:f1} s; " +
                          string.Join(" | ", mesh.Warnings));
        var rail = new RailSetup
        {
            Sources = new[] { new RailSource("P", new[] { mesh.Pads.Single(p => p.ComponentRef == "P") }, 5.0) },
            Sinks = new[] { new RailSink("R", new[] { mesh.Pads.Single(p => p.ComponentRef == "R") }, amps) },
            CopperConductivity = copper.ElectricalConductivity!.Value,
            CurrentDensityLimit = 0
        };
        sw.Restart();
        var report = RailThermalAnalysis.Solve(mesh, net, new RailThermalSetup
        {
            Rail = rail, Copper = copper, Laminate = polyimide, Environment = environment,
            ThermalConditions = conditions ?? Array.Empty<BoundaryCondition>(),
            SurfaceEmissivity = null,                      // bare copper on bare laminate (5.1.1)
            Tolerance = 0.01,
            StartTemperature = Ambient                     // R_t1 is measured at the ambient (5.2.3)
        });
        _output.WriteLine($"solve: {sw.Elapsed.TotalSeconds:f1} s, {report.Iterations} passes");
        foreach (string line in report.Describe()) _output.WriteLine(line);
        foreach (string line in report.Log.Where(l => l.Contains("Environment") || l.Contains("film") || l.Contains("Rayleigh")))
            _output.WriteLine("  " + line);

        // The method's quantity: the mean copper rise over the test section between the
        // tabs, weighted as the resistance measurement weights it (by J², uniform along a
        // straight trace, so by volume), and the same thing straight from the resistance
        // ratio of the section: Δt = (R_t/R_t1 − 1)/α at the same current.
        var sol = report.Solution;
        double weight = 0, weighted = 0, hot = 0, cold = 0, peak = 0;
        for (int e = 0; e < fe.ElementCount; e++)
        {
            if (sol.ColdConductivity[e] == 0) continue;
            var el = fe.Elements[e];
            double x = 0.25 * (fe.Nodes[el.N0].X + fe.Nodes[el.N1].X + fe.Nodes[el.N2].X + fe.Nodes[el.N3].X);
            if (!IpcTm650Coupon.InTestSection(x)) continue;
            double v = fe.ElementVolume(e);
            double pCold = sol.ColdElectrical.ElementPowerDensity[e] * v, pHot = sol.Electrical.ElementPowerDensity[e] * v;
            weight += pCold;
            weighted += pCold * (sol.ElementTemperature[e] - report.StartKelvin);
            hot += pHot;
            cold += pCold;
            peak = Math.Max(peak, sol.ElementTemperature[e] - report.StartKelvin);
        }
        double mean = weighted / weight;
        double fromResistance = (hot / cold - 1) / copper.ResistivityTemperatureCoefficient!.Value;
        double fit = Ipc2152.ExternalRise(amps, width, thickness);

        // The effective total film on each face: what left it over its mean excess.
        var thermal = RailThermalAnalysis.WithoutPadFaces(fe);
        double[] flux = new double[2], excess = new double[2];
        foreach (var t in thermal.BoundaryTriangles)
        {
            if (t.FaceId > 1) continue;
            double a = 0.5 * OpenSim.Core.Numerics.Vector3D.Cross(thermal.Nodes[t.B] - thermal.Nodes[t.A], thermal.Nodes[t.C] - thermal.Nodes[t.A]).Length;
            double tm = (sol.Temperature[t.A] + sol.Temperature[t.B] + sol.Temperature[t.C]) / 3 - report.StartKelvin;
            excess[t.FaceId] += a * tm;
        }
        // Energy split between the faces is not known from the temperatures alone; report the
        // total heat over the summed area-excess as the one effective film, per face in
        // proportion to its excess (equal films on both faces would give exactly this).
        double totalExcess = excess[0] + excess[1];
        double hEff = report.Rail.CopperLossWatts / totalExcess;
        flux[0] = hEff; flux[1] = hEff;

        _output.WriteLine($"{ounces:g2} oz, {width * 1e3:f2} mm, {amps:g3} A ({edge * 1e3:g2} mm, {gapLayers} layer(s)): " +
                          $"hottest {report.PeakCopperKelvin - 273.15:f2} °C; mean rise over the test section " +
                          $"{mean:f2} K (from the resistance ratio {fromResistance:f2} K), peak {peak:f2} K, " +
                          $"one-way peak {report.OneWayPeakCopperKelvin - report.StartKelvin:f2} K; " +
                          $"IPC-2152 fit {fit:f2} K → model/fit {mean / fit:f3}; " +
                          $"effective film {hEff:f2} W/m²K over the top+bottom area-excess, loss {report.Rail.CopperLossWatts:f3} W");
        return new CouponRun(report, fe, copper, fit, mean, fromResistance, peak, flux[0], flux[1]);
    }

    /// <summary>
    /// The product's cooling — natural convection and radiation from the environment model —
    /// against the fit of the IPC charts, at points inside the charts' band. Measured
    /// 2026-10-17 on the gate mesh (1.5 mm edge, one element layer through the laminate):
    /// 2 oz, 100 mil, 10 A → mean rise 44.70 K over the test section against the fit's
    /// 39.97 K (model/fit 1.118; peak 44.80 K, one-way 38.81 K, effective film 10.0 W/m²K
    /// convection plus radiation); 3 oz, 100 mil, 10 A → 29.36 K against 26.64 K (1.102;
    /// peak 29.46 K, one-way 26.72 K). On a mesh finer in every direction (1 mm, two layers)
    /// the same point reads 46.09 K (1.153); on the linear control below the coarse mesh
    /// reads 0.968 of the fine one, so the gate mesh sits about 3 % cool and the band is
    /// set about the measurement with that margin. The solver reads the IPC charts' rise
    /// from above, by 12–15 % at 40 K: with the film Brooks and Adam tuned to the IPC data
    /// the same board gives 1.048 (<see cref="ExternalTrace_WithThePapersFilm_RisesAsTheIpcChartsSay"/>),
    /// and the method's 1.80 µΩ·cm copper dissipates 6 % more than their 1.7, so what the
    /// environment model itself contributes is a film about a tenth weaker than the data
    /// imply — on the conservative side.
    /// </summary>
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(2.0, 3, 10.0, 1.118)]      // 2 oz, 100 mil, 10 A: the fit's 40 °C, Brooks & Adam's own worked example
    [InlineData(3.0, 3, 10.0, 1.102)]      // 3 oz, 100 mil, 10 A: the other chart the fit was made from (26.6 °C)
    public void ExternalTrace_InStillAir_RisesAsTheIpcChartsSay(double ounces, int conductor, double amps, double measuredRatio)
    {
        var run = Solve(ounces, conductor, amps, new EnvironmentSettings { AmbientTemperature = Ambient });
        // The two readings of the method's quantity agree (the resistance ratio is the
        // temperature mean by construction of the linear law).
        Assert.Equal(run.MeanRise, run.RiseFromResistance, run.MeanRise * 0.01);
        Assert.True(run.PeakRise >= run.MeanRise);
        Assert.InRange(run.Fit, Ipc2152.ChartMinimumRise, Ipc2152.ChartMaximumRise);
        // Band: the first measurement ± 0.08 (the 3 % mesh margin and room for the
        // environment's own loop); it must stay ABOVE the charts — the IPC data are the
        // conservative reading a designer sizes to, and a model reading below them would be
        // flattering, not agreeing.
        Assert.InRange(run.MeanRise / run.Fit, measuredRatio - 0.08, measuredRatio + 0.08);
        Assert.True(run.MeanRise >= run.Fit, $"model {run.MeanRise:f2} K below the IPC fit {run.Fit:f2} K");
    }

    /// <summary>
    /// The control that separates the board's conduction model from the film model: the
    /// cooling Brooks and Adam used to reproduce the IPC data in TRM, a fixed total heat
    /// transfer coefficient of 11 W/m²K on both faces (their section 5; their Figure 4-1
    /// range for external traces is 10 at low temperature to about 14 at high).
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void ExternalTrace_WithThePapersFilm_RisesAsTheIpcChartsSay()
    {
        var film = new BoundaryCondition[]
        {
            new Convection { Name = "TRM film", FaceIds = new[] { 0, 1 }, Coefficient = 11.0, AmbientTemperature = Ambient }
        };
        var run = Solve(2.0, 3, 10.0, null, film);
        Assert.Equal(run.MeanRise, run.RiseFromResistance, run.MeanRise * 0.01);
        // Measured 2026-10-17: 41.87 K against the fit's 39.97 K (1.048) on the gate mesh,
        // 43.25 K (1.082) on the 1 mm / two-layer mesh. Band: the measurement ± 0.08.
        Assert.InRange(run.MeanRise / run.Fit, 1.048 - 0.08, 1.048 + 0.08);
        // The resistivity feedback is a sixth of the rise here (one-way 36.1 K).
        Assert.True(run.Report.PeakCopperKelvin > run.Report.OneWayPeakCopperKelvin + 3);
    }
}
