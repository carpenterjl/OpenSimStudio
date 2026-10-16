using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-45: quadratic (TET10) elements in the DC conduction and steady heat solvers, flux
/// recovery with its error estimate, and flux probes. Every gate below is a case whose exact
/// answer lies in the element space — linear or quadratic fields on straight-edged elements —
/// so it holds to round-off, not to a band; the rates are FluxRecoveryConvergenceTests'.
/// </summary>
public class QuadraticScalarSolverTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 390, SpecificHeat = 385
    };

    private static readonly Material Laminate = new()
    {
        Name = "Laminate", YoungsModulus = 20e9, PoissonRatio = 0.15, Density = 1850,
        ElectricalConductivity = 1e-14, ThermalConductivity = 0.3, SpecificHeat = 1100
    };

    /// <summary>A structured box with region 1 where <paramref name="inUpper"/> holds for
    /// the element centroid, upgraded to TET10 when asked.</summary>
    private static FeMesh Box(double lx, double ly, double lz, int nx, int ny, int nz, bool quadratic,
        Func<Vector3D, bool>? inUpper = null)
    {
        var mesh = StructuredBoxMesh.Build(0, lx, 0, ly, 0, lz, nx, ny, nz);
        if (inUpper is not null)
        {
            var regions = Enumerable.Range(0, mesh.ElementCount).Select(e =>
            {
                var t = mesh.Elements[e];
                var c = (mesh.Nodes[t.N0] + mesh.Nodes[t.N1] + mesh.Nodes[t.N2] + mesh.Nodes[t.N3]) / 4;
                return inUpper(c) ? 1 : 0;
            }).ToArray();
            mesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, regions);
        }
        return quadratic ? QuadraticMeshBuilder.Upgrade(mesh) : mesh;
    }

    private static double Summary(SolveOutput output, string key) => output.Summary![key];

    // ---------------- the element ----------------

    [Fact]
    public void ReferenceIntegrals_AgreeWithQuadrature()
    {
        // The exact monomial integrals against the positive quadrature rules, which share
        // nothing with them but the shape functions.
        Span<double> n = stackalloc double[10];
        var load = new double[10];
        foreach (var (l, w) in ScalarElements.TetDegree5)
        {
            ScalarElements.Tet10Values(l, n);
            for (int i = 0; i < 10; i++) load[i] += w * n[i];
        }
        for (int i = 0; i < 10; i++) Assert.Equal(load[i], ScalarElements.Tet10Load[i], 14);

        var tri = new double[6];
        var mass = new double[6, 6];
        Span<double> m = stackalloc double[6];
        foreach (var (l, w) in ScalarElements.TriangleRule)
        {
            ScalarElements.Tri6Values(l, m);
            for (int i = 0; i < 6; i++)
            {
                tri[i] += w * m[i];
                for (int j = 0; j < 6; j++) mass[i, j] += w * m[i] * m[j];
            }
        }
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(tri[i], ScalarElements.Tri6Load[i], 14);
            for (int j = 0; j < 6; j++) Assert.Equal(mass[i, j], ScalarElements.Tri6Mass[i, j], 14);
        }
        Assert.Equal(1.0, ScalarElements.Tet10Load.Sum(), 14);
        Assert.Equal(1.0, ScalarElements.Tri6Load.Sum(), 14);
    }

    [Fact]
    public void PatchTest_QuadraticFieldReproducedOnAnUnstructuredTet10Mesh()
    {
        // u = x² − y² + 3xz is harmonic and lies in the TET10 space of any straight-edged
        // mesh: with it on every boundary node, every interior node and the gradient at every
        // point are exact, the recovered flux (a quadratic fit to a linear flux) is exact, and
        // the error estimate is zero.
        var mesh = new DelaunayMeshGenerator().Generate(PrimitiveFactory.CreateBox(1, 0.8, 0.6),
            new MeshSettings { TargetEdgeLength = 0.25, ElementOrder = ElementOrder.Quadratic });
        static double U(Vector3D p) => p.X * p.X - p.Y * p.Y + 3 * p.X * p.Z;
        static Vector3D Grad(Vector3D p) => new(2 * p.X + 3 * p.Z, -2 * p.Y, 3 * p.X);

        var assembler = new ScalarDiffusionAssembler(mesh, _ => 2.0);
        var prescribed = new Dictionary<int, double>();
        foreach (var t in mesh.BoundaryTriangles)
            foreach (int node in assembler.TriangleNodes(t))
                prescribed[node] = U(mesh.Nodes[node]);
        Assert.True(mesh.NodeCount > prescribed.Count);
        var u = ConstrainedSystemSolver.Solve(assembler.AssembleStiffness(), new double[mesh.NodeCount], prescribed,
            tolerance: 1e-14).Displacements;
        for (int i = 0; i < mesh.NodeCount; i++) Assert.Equal(U(mesh.Nodes[i]), u[i], 9);

        var recovery = FluxRecovery.Recover(assembler, u, _ => ConductivityTensor.Isotropic(2.0));
        double largest = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            foreach (var (l, _) in ScalarElements.TetDegree5)
            {
                var t = mesh.Elements[e];
                var x = mesh.Nodes[t.N0] * l[0] + mesh.Nodes[t.N1] * l[1] + mesh.Nodes[t.N2] * l[2] + mesh.Nodes[t.N3] * l[3];
                var exact = Grad(x) * -2.0;
                largest = Math.Max(largest, (recovery.ComputedAt(e, l) - exact).Length);
                largest = Math.Max(largest, (recovery.RecoveredAt(e, l) - exact).Length);
            }
        Assert.True(largest < 1e-7, $"flux off by {largest:g3}");
        Assert.True(recovery.RelativeError < 1e-8, $"estimate {recovery.RelativeError:g3}");
        Assert.Equal(0, recovery.FallbackNodes);
    }

    // ---------------- DC conduction ----------------

    [Fact]
    public void Conduction_Tet10Bar_ResistanceAndProbesExact()
    {
        const double length = 0.1, width = 0.02, thick = 0.01, volts = 1e-3;
        var mesh = Box(length, width, thick, 6, 2, 2, quadratic: true);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = volts }
            },
            FluxRecovery = new FluxRecoverySettings
            {
                Probes = new[]
                {
                    new FluxProbe { Name = "mid", Origin = new Vector3D(0.037, 0, 0), Normal = -Vector3D.UnitX },
                    new FluxProbe { Name = "tilted", Origin = new Vector3D(0.05, 0.01, 0.005), Normal = new Vector3D(-1, 0.4, 0.3) },
                    new FluxProbe { Name = "end", FaceIds = new[] { StructuredBoxMesh.FaceXMin } }
                }
            }
        });

        double resistance = length / (Copper.ElectricalConductivity!.Value * width * thick);
        double current = volts / resistance;
        Assert.Equal(resistance, Summary(output, "Resistance (Ω)"), resistance * 1e-9);
        // The current flows from the supply (x-max) to ground, along −x: the probes whose
        // normal points along −x read +I, and the end face, leaving through x-min, too.
        Assert.Equal(current, Summary(output, "Probe 'mid' current (A)"), current * 1e-9);
        Assert.Equal(current, Summary(output, "Probe 'tilted' current (A)"), current * 1e-9);
        Assert.Equal(current, Summary(output, "Probe 'end' current (A)"), current * 1e-9);
        Assert.True(Summary(output, "Current density error estimate (%)") < 1e-6);

        var recovered = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density (recovered)");
        double j = Copper.ElectricalConductivity.Value * volts / length;
        for (int n = 0; n < mesh.NodeCount; n++) Assert.Equal(-j, recovered.Values[n].X, j * 1e-9);
        Assert.Contains(output.Fields, f => f.Name == "Current density error (element)");
    }

    [Fact]
    public void Conduction_Tet10_ConservativeProbeIsTheElectrodeCurrent()
    {
        // A field that is not in the element space: a supply on part of one end (a terminal
        // that crowds the current), on an unstructured mesh. The conservative value through
        // a cross-section is the solve's own balance — the electrode's reaction — to the
        // solver's tolerance, while the section of the recovered flux differs by the
        // discretization error.
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var mesh = new DelaunayMeshGenerator().Generate(PrimitiveFactory.CreateBox(0.1, 0.02, 0.01),
                new MeshSettings { TargetEdgeLength = 0.004, ElementOrder = order });
            var output = new ElectricalConductionSolver().Solve(new SolveInput
            {
                Mesh = mesh, Material = Copper,
                BoundaryConditions = new BoundaryCondition[]
                {
                    new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                    new CurrentFlow { Name = "Feed", FaceIds = new[] { 1 }, TotalCurrent = 5, Equipotential = true }
                },
                FluxRecovery = new FluxRecoverySettings
                {
                    Probes = new[] { new FluxProbe { Name = "cut", Origin = new Vector3D(0.0431, 0, 0), Normal = -Vector3D.UnitX } }
                }
            });
            Assert.Equal(5, Summary(output, "Probe 'cut' current (A)"), 5 * 1e-8);
            Assert.Contains(output.Log, l => l.Contains("Probe 'cut'") && l.Contains("section"));
        }
    }

    [Fact]
    public void Conduction_Tet10_TerminalTiesTheMidEdgeNodes()
    {
        // An equipotential terminal at x-max ties its six-node faces into one unknown: its
        // potential is I·R exactly.
        const double length = 0.04, width = 0.01, height = 0.004, amps = 2;
        var mesh = Box(length, width, height, 4, 2, 2, quadratic: true);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
                new CurrentFlow { Name = "Feed", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = amps, Equipotential = true }
            }
        });
        double resistance = length / (Copper.ElectricalConductivity!.Value * width * height);
        Assert.Equal(amps * resistance, Summary(output, "Terminal 'Feed' potential (V)"), amps * resistance * 1e-9);
    }

    [Fact]
    public void Conduction_Tet10_InsulatorStageAndPerMaterialRecovery()
    {
        // Copper in the lower half, laminate above, a voltage across both ends: the staged
        // solve, whose partition now follows the mid-edge nodes too. The copper's resistance
        // is exact, and the recovered current density keeps each material's own value at the
        // interface — the copper's −σ∇φ, and the laminate's, 10²¹ times smaller.
        const double length = 0.04, width = 0.01, height = 0.004, volts = 1e-3;
        var mesh = Box(length, width, height, 4, 2, 2, quadratic: true, inUpper: c => c.Z > height / 2);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Copper,
            RegionMaterials = new Dictionary<int, Material> { [0] = Copper, [1] = Laminate },
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = volts }
            },
            FluxRecovery = new FluxRecoverySettings()
        });
        Assert.Contains(output.Log, l => l.Contains("insulators"));
        double resistance = length / (Copper.ElectricalConductivity!.Value * width * height / 2);
        Assert.Equal(resistance, Summary(output, "Resistance (Ω)"), resistance * 1e-9);

        var recovered = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density (recovered)");
        Assert.NotNull(recovered.Sides);
        double j = Copper.ElectricalConductivity.Value * volts / length;
        foreach (int node in recovered.Sides!.Nodes)
        {
            var sides = recovered.Sides.At(node);
            Assert.Equal(-j, sides.Single(s => s.Group == 0).Value.X, j * 1e-9);
            Assert.True(Math.Abs(sides.Single(s => s.Group == 1).Value.X) < j * 1e-15);
        }
    }

    // ---------------- steady heat conduction ----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heat_UniformSource_ProbeCorrectsForTheSourceInTheBand(bool quadratic)
    {
        // A slab with a uniform source between two fixed temperatures: T is a parabola,
        // exact on TET10 and nodally exact on TET4 (the problem is one-dimensional). The heat
        // flow through x = x₀ is q·A·(x₀ − L/2); the conservative value is exact on both
        // element orders because the band's source beyond the plane is clipped exactly, and
        // the section of the recovered flux is exact on TET10 (a linear flux).
        const double length = 0.05, side = 0.02, k = 20, source = 4e6, x0 = 0.0183;
        var mesh = Box(length, side, side, 5, 2, 2, quadratic);
        var material = StructuredBoxMesh.Conductor("Steel", k);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Left", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 300 },
                new FixedTemperature { Name = "Right", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = 300 }
            },
            ElementHeatSource = Enumerable.Repeat(source, mesh.ElementCount).ToArray(),
            FluxRecovery = new FluxRecoverySettings
            {
                Probes = new[] { new FluxProbe { Name = "cut", Origin = new Vector3D(x0, 0, 0), Normal = Vector3D.UnitX } }
            }
        });

        double exact = source * side * side * (x0 - length / 2);
        Assert.Equal(exact, Summary(output, "Probe 'cut' heat flow (W)"), Math.Abs(exact) * 1e-9);
        var temperature = ((NodalScalarField)output.Fields[0]).Values;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double x = mesh.Nodes[n].X;
            Assert.Equal(300 + source / (2 * k) * x * (length - x), temperature[n], 1e-6);
        }
        if (quadratic) Assert.True(Summary(output, "Heat flux error estimate (%)") < 1e-6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heat_ConvectionOnSixNodeTriangles_HeatFlowExact(bool quadratic)
    {
        // Fixed temperature at x = 0, convection at x = L: T is linear, T_L =
        // (k/L·T₀ + h·T∞)/(k/L + h). The face probe on the convective face reads h(T − T∞)·A
        // from the condition itself, the plane probe the same through the body.
        const double length = 0.03, side = 0.01, k = 15, h = 800, t0 = 400, ambient = 300;
        var mesh = Box(length, side, side, 3, 2, 2, quadratic);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = StructuredBoxMesh.Conductor("Alloy", k),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Hot", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = t0 },
                new Convection { Name = "Film", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Coefficient = h, AmbientTemperature = ambient }
            },
            FluxRecovery = new FluxRecoverySettings
            {
                Probes = new[]
                {
                    new FluxProbe { Name = "face", FaceIds = new[] { StructuredBoxMesh.FaceXMax } },
                    new FluxProbe { Name = "plane", Origin = new Vector3D(0.0117, 0, 0), Normal = Vector3D.UnitX }
                }
            }
        });
        double tl = (k / length * t0 + h * ambient) / (k / length + h);
        double flow = h * (tl - ambient) * side * side;
        Assert.Equal(flow, Summary(output, "Probe 'face' heat flow (W)"), flow * 1e-9);
        Assert.Equal(flow, Summary(output, "Probe 'plane' heat flow (W)"), flow * 1e-9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heat_LayersSideBySide_RecoveryKeepsEachMaterialAndTheBoxSelectsOne(bool quadratic)
    {
        // Two layers stacked in z, both between the same two temperatures along x: the
        // gradient is the same, the flux is −k·∇T in each, so it jumps across the interface.
        // A patch spanning both would blur it; per material, both sides are exact. A plane
        // limited to the lower layer's box takes that layer's heat flow alone.
        const double length = 0.02, width = 0.01, height = 0.004, k1 = 200, k2 = 2;
        var mesh = Box(length, width, height, 4, 2, 2, quadratic, inUpper: c => c.Z > height / 2);
        var lower = StructuredBoxMesh.Conductor("Lower", k1);
        var upper = StructuredBoxMesh.Conductor("Upper", k2);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = lower,
            RegionMaterials = new Dictionary<int, Material> { [0] = lower, [1] = upper },
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Hot", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 350 },
                new FixedTemperature { Name = "Cold", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = 300 }
            },
            FluxRecovery = new FluxRecoverySettings
            {
                Probes = new[]
                {
                    new FluxProbe
                    {
                        Name = "lower", Origin = new Vector3D(0.0071, 0, 0), Normal = Vector3D.UnitX,
                        BoxMin = new Vector3D(-1, -1, -1), BoxMax = new Vector3D(1, 1, height / 2)
                    },
                    new FluxProbe { Name = "all", Origin = new Vector3D(0.0071, 0, 0), Normal = Vector3D.UnitX }
                }
            }
        });

        double gradient = 50 / length;
        double flowLower = k1 * gradient * width * height / 2, flowUpper = k2 * gradient * width * height / 2;
        Assert.Equal(flowLower, Summary(output, "Probe 'lower' heat flow (W)"), flowLower * 1e-9);
        Assert.Equal(flowLower + flowUpper, Summary(output, "Probe 'all' heat flow (W)"), flowLower * 1e-9);

        var recovered = (NodalVectorField)output.Fields.Single(f => f.Name == "Heat flux (recovered)");
        Assert.NotNull(recovered.Sides);
        foreach (int node in recovered.Sides!.Nodes)
        {
            var sides = recovered.Sides.At(node);
            // To the solver's tolerance (10⁻¹⁰ of the load), which the recovery carries.
            Assert.Equal(k1 * gradient, sides.Single(s => s.Group == 0).Value.X, k1 * gradient * 1e-7);
            Assert.Equal(k2 * gradient, sides.Single(s => s.Group == 1).Value.X, k1 * gradient * 1e-7);
            Assert.Equal(k1 * gradient, recovered.Values[node].X, k1 * gradient * 1e-7);   // the larger side
        }
        Assert.True(Summary(output, "Heat flux error estimate (%)") < 1e-6);
    }

    // ---------------- what stays as it was, and what is refused ----------------

    [Fact]
    public void WithoutRecovery_TheFieldsAndSummaryAreUnchanged()
    {
        var mesh = Box(0.02, 0.01, 0.01, 2, 2, 2, quadratic: true);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = StructuredBoxMesh.Conductor("Alloy", 10),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Hot", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 310 },
                new HeatFlux { Name = "In", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalPower = 3 }
            }
        });
        Assert.Equal(new[] { "Temperature", "Heat flux" }, output.Fields.Select(f => f.Name));
        Assert.Null(output.Summary);
    }

    [Fact]
    public void Tet10_RefusesTheSurfaceModelsStampedOnCorners()
    {
        var mesh = Box(0.02, 0.01, 0.01, 2, 2, 2, quadratic: true);
        var ex = Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = StructuredBoxMesh.Conductor("Alloy", 10) with { Emissivity = 0.8 },
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "In", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalPower = 3 }
            },
            Environment = new EnvironmentSettings { Medium = MediumKind.StillFluid, AmbientTemperature = 300 }
        }));
        Assert.Contains("an environment", ex.Message);
        Assert.Contains("linear", ex.Message);

        Assert.Throws<InvalidOperationException>(() => new TransientThermalSolver().Validate(new SolveInput
        {
            Mesh = mesh, Material = StructuredBoxMesh.Conductor("Alloy", 10),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Hot", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 310 }
            },
            TransientThermal = new TransientThermalSettings { InitialTemperature = 300, Duration = 1, TimeStep = 0.1 }
        }));
    }

    [Fact]
    public void Probes_AreValidated()
    {
        var mesh = Box(0.02, 0.01, 0.01, 2, 1, 1, quadratic: false);
        SolveInput With(params FluxProbe[] probes) => new()
        {
            Mesh = mesh, Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "A", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
                new VoltagePotential { Name = "B", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = 1 }
            },
            FluxRecovery = new FluxRecoverySettings { Probes = probes }
        };
        var solver = new ElectricalConductionSolver();
        Assert.Contains("non-zero normal", Assert.Throws<InvalidOperationException>(() =>
            solver.Validate(With(new FluxProbe { Name = "p", Normal = Vector3D.Zero }))).Message);
        Assert.Contains("named 'p'", Assert.Throws<InvalidOperationException>(() =>
            solver.Validate(With(new FluxProbe { Name = "p", Normal = Vector3D.UnitX },
                new FluxProbe { Name = "p", Normal = Vector3D.UnitY }))).Message);
        Assert.Contains("does not have", Assert.Throws<InvalidOperationException>(() =>
            solver.Validate(With(new FluxProbe { Name = "f", FaceIds = new[] { 99 } }))).Message);
        Assert.Contains("both or neither", Assert.Throws<InvalidOperationException>(() =>
            solver.Validate(With(new FluxProbe { Name = "b", Normal = Vector3D.UnitX, BoxMin = Vector3D.Zero }))).Message);
    }
}
