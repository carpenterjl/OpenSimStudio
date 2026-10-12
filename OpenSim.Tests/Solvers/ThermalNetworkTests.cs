using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-26: lumped thermal networks as unknowns beside the mesh. References: the same discrete
/// problem written with conditions instead of nodes (identical to solver precision), a rod
/// with a two-resistor part in closed form, the lumped RC body (its backward-Euler
/// recursion exactly, its closed form within the step error), and energy balances.
/// </summary>
public class ThermalNetworkTests
{
    private readonly ITestOutputHelper _out;
    public ThermalNetworkTests(ITestOutputHelper output) => _out = output;

    private static Material Solid(string name, double k, double density = 2000, double heat = 900) => new()
    {
        Name = name, YoungsModulus = 1e9, PoissonRatio = 0.3, Density = density,
        ThermalConductivity = k, SpecificHeat = heat, Emissivity = 0.9
    };

    private const int Near = StructuredBoxMesh.FaceZMax, Far = StructuredBoxMesh.FaceZMin;
    private const double Side = 4e-3, Length = 20e-3, K = 10, Ambient = 300, Sink = 320;
    private static double BarResistance => Length / (K * Side * Side);

    /// <summary>A 4 mm × 4 mm × 20 mm rod along z; the part sits on z = 20 mm (face 5).</summary>
    private static FeMesh Rod() => StructuredBoxMesh.Build(0, Side, 0, Side, 0, Length, 2, 2, 10);

    private static FixedTemperature SinkAt(double kelvin) =>
        new() { Name = "sink", FaceIds = new[] { Far }, Kelvin = kelvin };

    private static IReadOnlyList<double> Temperature(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;

    private static ThermalNetworkNode Node(string name, double power = 0, double capacity = 0, double? fixedKelvin = null,
        PowerProfile? profile = null) =>
        new() { Name = name, PowerWatts = power, CapacityJoulesPerKelvin = capacity, FixedKelvin = fixedKelvin, PowerProfile = profile };

    /// <summary>The datasheet's two-resistor part as a network: junction → footprint through θJB,
    /// junction → case through θJC, case → ambient through R_top.</summary>
    private static ThermalNetwork TwoResistorPart(double power, double thetaJb, double thetaJc, double top,
        double ambient, int face, double junctionCapacity = 0, double caseCapacity = 0) => new()
    {
        Name = "U1",
        Nodes = new[]
        {
            Node("junction", power, junctionCapacity), Node("case", 0, caseCapacity), Node("ambient", fixedKelvin: ambient)
        },
        Links = new[]
        {
            new ThermalNetworkLink("junction", "case", 1 / thetaJc), new ThermalNetworkLink("case", "ambient", 1 / top)
        },
        Attachments = new[] { new ThermalNetworkAttachment("junction", new[] { face }, 1 / thetaJb) }
    };

    // ---------------- Steady: the network against the conditions it stands for ----------------

    [Fact]
    public void AFixedNodeAttachedToAFace_IsAConvectionCondition()
    {
        var mesh = Rod();
        const double h = 50, power = 1.5;
        double area = Side * Side;
        var flux = new HeatFlux { Name = "heat", FaceIds = new[] { Far }, TotalPower = power };
        var byCondition = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("bar", K),
            BoundaryConditions = new BoundaryCondition[]
            {
                flux, new Convection { Name = "air", FaceIds = new[] { Near }, Coefficient = h, AmbientTemperature = Ambient }
            }
        });
        var byNetwork = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { flux },
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "air", Nodes = new[] { Node("air", fixedKelvin: Ambient) },
                    Attachments = new[] { new ThermalNetworkAttachment("air", new[] { Near }, h * area) }
                }
            }
        });
        var a = Temperature(byCondition);
        var b = Temperature(byNetwork);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i], 1e-6);
        var state = Assert.Single(byNetwork.Networks!);
        Assert.Equal(Ambient, state.Of("air"));
        // The node gave the mesh −P: the mesh's heat left through it.
        Assert.Equal(-power, state.AttachmentWatts[0], 1e-6);
        Assert.Contains(byNetwork.Log, l => l.Contains("Thermal network 'air'"));
    }

    [Fact]
    public void ATwoResistorPartOnARod_MatchesTheClosedForm()
    {
        const double power = 2, thetaJc = 5, thetaJb = 10, top = 40;
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = Rod(), Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { SinkAt(Sink) },
            ThermalNetworks = new[] { TwoResistorPart(power, thetaJb, thetaJc, top, Ambient, Near) }
        });
        // Junction to ambient by way of the top: θJC then R_top.
        double series = thetaJc + top;
        double toBoard = (Ambient + power * series - Sink) / (thetaJb + series + BarResistance);
        double board = Sink + toBoard * BarResistance, junction = board + thetaJb * toBoard;
        double @case = junction - thetaJc * (power - toBoard);
        var state = Assert.Single(output.Networks!);
        _out.WriteLine($"junction {state.Of("junction"):g9} (exact {junction:g9}), case {state.Of("case"):g9} ({@case:g9}), " +
                       $"into the board {state.AttachmentWatts[0]:g9} ({toBoard:g9})");
        Assert.Equal(junction, state.Of("junction"), 6);
        Assert.Equal(@case, state.Of("case"), 6);
        Assert.Equal(Ambient, state.Of("ambient"));
        Assert.Equal(toBoard, state.AttachmentWatts[0], 1e-6);
        Assert.Equal(power - toBoard, state.LinkWatts[0], 1e-6);   // junction → case
        Assert.Equal(power - toBoard, state.LinkWatts[1], 1e-6);   // case → ambient
        // The rod's near face sits at the board temperature the network saw.
        var temperature = Temperature(output);
        double faceMean = output.Fields.Count > 0 ? Rod().GetFaceTriangles(new[] { Near }).Average(t => (temperature[t.A] + temperature[t.B] + temperature[t.C]) / 3) : 0;
        Assert.Equal(board, faceMean, 6);
    }

    [Fact]
    public void ANetworkAlone_AnchorsTheMesh()
    {
        // No condition on the rod: its level is fixed only through the part's top path, and
        // nothing flows into it — the rod sits at the junction temperature.
        const double power = 0.5, thetaJc = 3, thetaJb = 12, top = 30;
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = Rod(), Material = Solid("bar", K), BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ThermalNetworks = new[] { TwoResistorPart(power, thetaJb, thetaJc, top, Ambient, Near) }
        });
        var state = Assert.Single(output.Networks!);
        double junction = Ambient + power * (thetaJc + top);
        Assert.Equal(junction, state.Of("junction"), 6);
        Assert.Equal(0, state.AttachmentWatts[0], 1e-6);
        Assert.All(Temperature(output), t => Assert.Equal(junction, t, 6));
    }

    [Fact]
    public void ATiedNode_IsTheFaceMean_AndItsPowerAHeatFlow()
    {
        var mesh = Rod();
        const double power = 1.2, g = 0.05;
        double area = Side * Side;
        var byCondition = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("bar", K),
            BoundaryConditions = new BoundaryCondition[]
            {
                SinkAt(Sink),
                new HeatFlux { Name = "heat", FaceIds = new[] { Near }, TotalPower = power },
                new Convection { Name = "air", FaceIds = new[] { Near }, Coefficient = g / area, AmbientTemperature = Ambient }
            }
        });
        var byNetwork = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { SinkAt(Sink) },
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "pad", Nodes = new[] { Node("pad", power), Node("ambient", fixedKelvin: Ambient) },
                    Links = new[] { new ThermalNetworkLink("pad", "ambient", g) },
                    Attachments = new[] { new ThermalNetworkAttachment("pad", new[] { Near }, double.PositiveInfinity) }
                }
            }
        });
        var a = Temperature(byCondition);
        var b = Temperature(byNetwork);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i], 1e-6);
        var state = Assert.Single(byNetwork.Networks!);
        double faceMean = mesh.GetFaceTriangles(new[] { Near }).Average(t => (b[t.A] + b[t.B] + b[t.C]) / 3);
        Assert.Equal(faceMean, state.Of("pad"), 10);
        // What the face received through the pad: its power plus what the ambient link brought (negative).
        Assert.Equal(power + g * (Ambient - faceMean), state.AttachmentWatts[0], 1e-6);
        Assert.Equal(g * (faceMean - Ambient), state.LinkWatts[0], 1e-6);
    }

    [Fact]
    public void AnInfiniteLink_MakesTwoNodesOne()
    {
        const double power = 2, thetaJc = 5, thetaJb = 10, top = 40;
        var one = TwoResistorPart(power, thetaJb, thetaJc, top, Ambient, Near);
        var split = one with
        {
            Nodes = new[] { Node("junction", 0.75 * power), Node("die", 0.25 * power), Node("case"), Node("ambient", fixedKelvin: Ambient) },
            Links = one.Links.Append(new ThermalNetworkLink("die", "junction", double.PositiveInfinity)).ToList()
        };
        SolveOutput Run(ThermalNetwork network) => new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = Rod(), Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { SinkAt(Sink) },
            ThermalNetworks = new[] { network }
        });
        var a = Run(one).Networks![0];
        var b = Run(split).Networks![0];
        Assert.Equal(a.Of("junction"), b.Of("junction"), 1e-6);
        Assert.Equal(b.Of("junction"), b.Of("die"), 12);
        Assert.Equal(a.Of("case"), b.Of("case"), 1e-6);
        Assert.True(double.IsNaN(b.LinkWatts[2]));
    }

    // ---------------- Transient: capacities ----------------

    private static TransientThermalSettings Settings(double dt, double duration) =>
        new() { InitialTemperature = Ambient, TimeStep = dt, Duration = duration, OutputStride = 1 };

    [Fact]
    public void ANodeWithCapacity_FollowsTheBackwardEulerRecursion_AndTheLumpedBody()
    {
        // The node hangs on a fixed ambient through G and touches nothing else; the cube
        // (no condition) stays where it started. τ = C/G = 10 s.
        const double c = 2.0, g = 0.2, power = 1.0, dt = 0.5, duration = 30;
        var mesh = StructuredBoxMesh.Build(0, 0.01, 0, 0.01, 0, 0.01, 2, 2, 2);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("block", 1), BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = Settings(dt, duration),
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "rc", Nodes = new[] { Node("node", power, c), Node("ambient", fixedKelvin: Ambient) },
                    Links = new[] { new ThermalNetworkLink("node", "ambient", g) }
                }
            }
        });
        var frames = output.Frames!;
        Assert.Equal(61, frames.Count);
        double recursion = Ambient;
        for (int n = 1; n < frames.Count; n++)
        {
            recursion = (recursion * c / dt + power + g * Ambient) / (c / dt + g);
            double node = frames[n].Networks![0].Of("node");
            Assert.Equal(recursion, node, 1e-5);
            double exact = Ambient + power / g * (1 - Math.Exp(-n * dt * g / c));
            Assert.InRange(node - Ambient, 0.9 * (exact - Ambient), 1.1 * (exact - Ambient));
            Assert.All(Temperature(new SolveOutput { Fields = frames[n].Fields, Log = output.Log }), t => Assert.Equal(Ambient, t, 1e-5));
        }
        _out.WriteLine($"after {duration} s: node {recursion - Ambient:f4} K above ambient, closed form {power / g * (1 - Math.Exp(-duration * g / c)):f4} K");
        Assert.Equal(frames[^1].Networks![0].Of("node"), output.Networks![0].Of("node"));
    }

    [Fact]
    public void ANodesOwnPowerProfile_IsAppliedAtTheStepMean()
    {
        const double c = 2.0, g = 0.2, power = 1.0, dt = 0.5, duration = 20;
        var pulse = PowerProfile.Pulse(period: 3, dutyCycle: 0.4, onScale: 1, offScale: 0.1, delay: 0.7);
        var mesh = StructuredBoxMesh.Build(0, 0.01, 0, 0.01, 0, 0.01, 2, 2, 2);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid("block", 1), BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = Settings(dt, duration),
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "rc", Nodes = new[] { Node("node", power, c, profile: pulse), Node("ambient", fixedKelvin: Ambient) },
                    Links = new[] { new ThermalNetworkLink("node", "ambient", g) }
                }
            }
        });
        var frames = output.Frames!;
        double recursion = Ambient;
        for (int n = 1; n < frames.Count; n++)
        {
            double scale = pulse.Mean((n - 1) * dt, n * dt);
            recursion = (recursion * c / dt + scale * power + g * Ambient) / (c / dt + g);
            Assert.Equal(recursion, frames[n].Networks![0].Of("node"), 1e-5);
        }
        Assert.Contains(output.Log, l => l.Contains("own profiles"));
    }

    [Fact]
    public void ACapacitiveNodeOnAnAdiabaticBlock_ConservesEnergy()
    {
        // Everything the node dissipates is stored: in the node and in the block it is
        // attached to. Σ capacity·ΔT = P·t to solver precision at every stored step.
        const double c = 5.0, g = 0.5, power = 2.0, dt = 2, duration = 40, side = 0.02;
        var material = Solid("block", 5, density: 2000, heat: 800);
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 3, 3, 3);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = material, BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = Settings(dt, duration) with { Capacity = ThermalCapacity.Lumped },
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "part", Nodes = new[] { Node("node", power, c) },
                    Attachments = new[] { new ThermalNetworkAttachment("node", new[] { StructuredBoxMesh.FaceZMax }, g) }
                }
            }
        });
        double blockCapacity = material.Density * material.SpecificHeat!.Value * side * side * side;
        var frames = output.Frames!;
        for (int n = 1; n < frames.Count; n++)
        {
            var temperature = Temperature(new SolveOutput { Fields = frames[n].Fields, Log = output.Log });
            // Lumped capacity: each node holds ρc·V/4 of every element around it.
            double stored = 0;
            var share = new double[mesh.NodeCount];
            for (int e = 0; e < mesh.ElementCount; e++)
                foreach (int meshNode in mesh.GetElementNodes(e)) share[meshNode] += mesh.ElementVolume(e) / 4;
            for (int i = 0; i < mesh.NodeCount; i++)
                stored += material.Density * material.SpecificHeat.Value * share[i] * (temperature[i] - Ambient);
            double node = frames[n].Networks![0].Of("node");
            stored += c * (node - Ambient);
            Assert.Equal(power * n * dt, stored, power * n * dt * 1e-5);
            Assert.True(node > temperature.Max(), "the node is the hottest point");
        }
        _out.WriteLine($"block capacity {blockCapacity:g4} J/K, node {c} J/K; after {duration} s the node is " +
                       $"{frames[^1].Networks![0].Of("node") - Ambient:f3} K above the start");
    }

    // ---------------- Thermal impedance on a network node ----------------

    [Fact]
    public void ImpedanceOfAJunction_EndsAtItsSteadyRise_AndFollowsTheRcBody()
    {
        // (a) The part on the rod, sink and ambient alike at 300 K: Z_th(∞) at the junction
        // is the steady rise per watt. (b) The RC node alone follows (1/G)(1 − e^(−t/τ)).
        const double power = 2, thetaJc = 5, thetaJb = 10, top = 40;
        var input = new SolveInput
        {
            Mesh = Rod(), Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { SinkAt(Ambient) },
            ThermalNetworks = new[] { TwoResistorPart(power, thetaJb, thetaJc, top, Ambient, Near) }
        };
        var steady = new HeatConductionSolver().Solve(input).Networks![0];
        var impedance = ThermalImpedanceSolver.Solve(input, settings: new ThermalImpedanceSettings
        {
            StartTime = 1e-4, EndTime = 1e4, PointsPerDecade = 8
        });
        var curve = Assert.Single(impedance.Curves);
        Assert.Equal("U1: junction", curve.Name);
        double rise = (steady.Of("junction") - Ambient) / power;
        _out.WriteLine($"junction: steady {rise:g6} K/W, curve end {curve.KelvinPerWatt[^1]:g6}, curve steady {curve.SteadyKelvinPerWatt:g6}");
        _out.WriteLine("first points: " + string.Join(", ", curve.TimesSeconds.Take(6).Select((t, i) => $"{t:g2} s {curve.KelvinPerWatt[i]:g5}")));
        _out.WriteLine(string.Join(Environment.NewLine, impedance.Log));
        Assert.Equal(rise, curve.SteadyKelvinPerWatt, rise * 1e-6);
        Assert.InRange(curve.KelvinPerWatt[^1] / rise, 0.999, 1.001);
        // Monotone to solver precision (the settled tail wanders by ~1e-6 of the steady value).
        for (int i = 1; i < curve.KelvinPerWatt.Count; i++)
            Assert.True(curve.KelvinPerWatt[i] >= curve.KelvinPerWatt[i - 1] - 1e-5 * rise,
                $"not monotone at {curve.TimesSeconds[i]:g3} s: {curve.KelvinPerWatt[i - 1]:g9} → {curve.KelvinPerWatt[i]:g9}");
        // A junction with no thermal mass of its own rises at once onto its resistances.
        Assert.True(curve.KelvinPerWatt[0] > 0.5 * Math.Min(thetaJb, thetaJc + top));

        const double c = 2.0, g = 0.2;
        var rc = ThermalImpedanceSolver.Solve(new SolveInput
        {
            Mesh = StructuredBoxMesh.Build(0, 0.01, 0, 0.01, 0, 0.01, 2, 2, 2), Material = Solid("block", 1),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "rc", Nodes = new[] { Node("node", 1, c), Node("ambient", fixedKelvin: Ambient) },
                    Links = new[] { new ThermalNetworkLink("node", "ambient", g) }
                }
            }
        }, new[] { new ThermalProbe { Name = "node", Network = "rc", Node = "node" } },
            new ThermalImpedanceSettings { StartTime = 0.01, EndTime = 100, PointsPerDecade = 12 });
        var rcCurve = rc.Curves[0];
        double worst = 0;
        for (int i = 0; i < rcCurve.TimesSeconds.Count; i++)
        {
            double exact = (1 - Math.Exp(-rcCurve.TimesSeconds[i] * g / c)) / g;
            worst = Math.Max(worst, Math.Abs(rcCurve.KelvinPerWatt[i] - exact) / (1 / g));
        }
        _out.WriteLine($"RC node: worst deviation from (1/G)(1 − e^(−t/τ)) {worst:g3} of 1/G");
        Assert.True(worst < 3e-3, $"worst {worst:g3}");
        Assert.Equal(1 / g, rcCurve.SteadyKelvinPerWatt, 1e-9);
    }

    // ---------------- An enclosure's air ----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEnclosureAirNode_WarmsByTheHeatItReceives(bool radiation)
    {
        // A heated block in still air inside an enclosure whose air loses heat to the outside
        // through G_vent. Everything the block dissipates ends in the air and leaves through
        // G_vent, radiation included (the walls are at the air's temperature): at the steady
        // state G_vent·(T_air − T_outside) = P exactly.
        const double power = 3.0, vent = 0.6, side = 0.03;
        var environment = new EnvironmentSettings { AmbientTemperature = Ambient, IncludeRadiation = radiation };
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 3, 3, 3);
        var air = new ThermalNetwork
        {
            Name = "enclosure", Nodes = new[] { Node("air"), Node("outside", fixedKelvin: Ambient) },
            Links = new[] { new ThermalNetworkLink("air", "outside", vent) },
            EnvironmentAmbientNode = "air"
        };
        var input = new SolveInput
        {
            Mesh = mesh, Material = Solid("block", 50),
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "heat", FaceIds = new[] { StructuredBoxMesh.FaceZMin }, TotalPower = power }
            },
            Environment = environment, ThermalNetworks = new[] { air }
        };
        var output = new HeatConductionSolver().Solve(input);
        var state = Assert.Single(output.Networks!);
        double airKelvin = state.Of("air");
        var temperature = Temperature(output);
        _out.WriteLine($"radiation {radiation}: air {airKelvin - Ambient:f3} K above the outside (balance says {power / vent:f3}); " +
                       $"block {temperature.Min() - Ambient:f2} … {temperature.Max() - Ambient:f2} K above");
        Assert.Equal(power / vent, airKelvin - Ambient, power / vent * 1e-6);
        Assert.Equal(power, state.LinkWatts[0], power * 1e-6);
        Assert.True(temperature.Min() > airKelvin, "the block is hotter than the air it heats");
        Assert.Contains(output.Log, l => l.Contains("drives toward 'air'"));

        // Without the enclosure (the plain ambient) the block sits lower by about the air's rise.
        var open = Temperature(new HeatConductionSolver().Solve(input with { ThermalNetworks = null }));
        Assert.InRange(temperature.Average() - open.Average(), 0.5 * (airKelvin - Ambient), 1.5 * (airKelvin - Ambient));

        // The transient settles onto the same state.
        var transient = new TransientThermalSolver().Solve(input with
        {
            TransientThermal = new TransientThermalSettings { InitialTemperature = Ambient, TimeStep = 200, Duration = 20000 }
        });
        Assert.Equal(airKelvin, transient.Networks![0].Of("air"), (airKelvin - Ambient) * 2e-3);

        // Z_th about that state sees the enclosure too: the steady value is the block's rise
        // over the outside per watt, air included.
        var impedance = ThermalImpedanceSolver.Solve(input,
            new[] { new ThermalProbe { Name = "heated", FaceIds = new[] { StructuredBoxMesh.FaceZMin } } },
            new ThermalImpedanceSettings { StartTime = 1, EndTime = 1e5, PointsPerDecade = 6 });
        double heatedMean = mesh.GetFaceTriangles(new[] { StructuredBoxMesh.FaceZMin })
            .Average(t => (temperature[t.A] + temperature[t.B] + temperature[t.C]) / 3);
        Assert.Equal((heatedMean - Ambient) / power, impedance.Curves[0].SteadyKelvinPerWatt, (heatedMean - Ambient) / power * 2e-3);
    }

    // ---------------- Refusals ----------------

    [Fact]
    public void Refusals_NameTheNode()
    {
        var mesh = Rod();
        SolveInput Input(ThermalNetwork network, EnvironmentSettings? environment = null) => new()
        {
            Mesh = mesh, Material = Solid("bar", K), BoundaryConditions = new BoundaryCondition[] { SinkAt(Sink) },
            ThermalNetworks = new[] { network }, Environment = environment
        };
        string Message(ThermalNetwork network, EnvironmentSettings? environment = null) =>
            Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Validate(Input(network, environment))).Message;

        var ambient = Node("ambient", fixedKelvin: Ambient);
        Assert.Contains("two nodes are named", Message(new ThermalNetwork { Name = "n", Nodes = new[] { Node("a"), Node("a") } }));
        Assert.Contains("does not have", Message(new ThermalNetwork
        {
            Name = "n", Nodes = new[] { Node("a"), ambient }, Links = new[] { new ThermalNetworkLink("a", "b", 1) }
        }));
        Assert.Contains("nothing fixes its temperature", Message(new ThermalNetwork { Name = "n", Nodes = new[] { Node("a", 1), ambient } }));
        Assert.Contains("tied to two sets of faces", Message(new ThermalNetwork
        {
            Name = "n", Nodes = new[] { Node("a", 1) },
            Attachments = new[]
            {
                new ThermalNetworkAttachment("a", new[] { Near }, double.PositiveInfinity),
                new ThermalNetworkAttachment("a", new[] { Far }, double.PositiveInfinity)
            }
        }));
        Assert.Contains("fixed temperature condition", Message(new ThermalNetwork
        {
            Name = "n", Nodes = new[] { ambient },
            Attachments = new[] { new ThermalNetworkAttachment("ambient", new[] { Near }, double.PositiveInfinity) }
        }));
        Assert.Contains("face-to-face", Message(new ThermalNetwork
        {
            Name = "n", Nodes = new[] { Node("a"), Node("b") }, Links = new[] { new ThermalNetworkLink("a", "b", 1) },
            Attachments = new[]
            {
                new ThermalNetworkAttachment("a", new[] { Near }, double.PositiveInfinity),
                new ThermalNetworkAttachment("b", new[] { Far }, double.PositiveInfinity)
            }
        }));
        Assert.Contains("cannot also dissipate", Message(new ThermalNetwork { Name = "n", Nodes = new[] { Node("hot", 1, fixedKelvin: 350) } }));
        var air = new ThermalNetwork
        {
            Name = "n", Nodes = new[] { Node("air"), ambient }, Links = new[] { new ThermalNetworkLink("air", "ambient", 1) },
            EnvironmentAmbientNode = "air"
        };
        Assert.Contains("no environment", Message(air));
        Assert.Contains("reaches no fixed temperature", Message(air with
        {
            Links = Array.Empty<ThermalNetworkLink>(),
            Attachments = new[] { new ThermalNetworkAttachment("air", new[] { Near }, 1) }
        }, new EnvironmentSettings { AmbientTemperature = Ambient, IncludeRadiation = false }));
        Assert.Contains("no node 'nowhere'", Assert.Throws<InvalidOperationException>(() =>
            ThermalImpedanceSolver.Solve(Input(TwoResistorPart(1, 10, 5, 40, Ambient, Near)),
                new[] { new ThermalProbe { Name = "p", Network = "U1", Node = "nowhere" } })).Message);
    }
}
