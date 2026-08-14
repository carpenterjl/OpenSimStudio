using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Physics gates for heat crossing a contact interface between two separately meshed bodies.
///
/// The headline is the one-dimensional series resistance
/// q = ΔT / (L₁/k₁A + 1/h_cA + L₂/k₂A). On a matching interface this is an EXACT identity of
/// the discrete problem, not an approximation: the analytic solution is linear inside each
/// slab (so it lies in the TET4 space), the jump across the interface is constant, and the
/// vertex quadrature integrates a per-triangle linear function exactly. Anything but a
/// machine-precision match means the coupling operator is wrong.
/// </summary>
public class ThermalContactTests
{
    private const double L1 = 0.02, L2 = 0.03;      // slab lengths [m]
    private const double W = 0.01;                  // square cross-section side [m]
    private const double Area = W * W;
    private const double K1 = 45, K2 = 400;         // steel, copper [W/(m·K)]
    private const double Hc = 5e3;                  // contact conductance [W/(m²·K)]
    private const double Hot = 400, Cold = 300;     // end temperatures [K]

    private static readonly Material Steel = StructuredBoxMesh.Conductor("Steel", K1);
    private static readonly Material Copper = StructuredBoxMesh.Conductor("Copper", K2);

    /// <summary>The two slabs end to end along x, meshed independently.</summary>
    private static List<Body> Slabs(int n1, int t1, int n2, int t2, double gap = 0)
    {
        var left = StructuredBoxMesh.Box("Left", 0, L1, 0, W, 0, W, n1, t1, t1, Steel);
        var right = StructuredBoxMesh.Box("Right", L1 + gap, L1 + L2 + gap, 0, W, 0, W, n2, t2, t2, Copper);
        left.BoundaryConditions.Add(new FixedTemperature
        {
            Name = "Hot",
            FaceIds = new[] { StructuredBoxMesh.FaceXMin },
            Kelvin = Hot
        });
        right.BoundaryConditions.Add(new FixedTemperature
        {
            Name = "Cold",
            FaceIds = new[] { StructuredBoxMesh.FaceXMax },
            Kelvin = Cold
        });
        return new List<Body> { left, right };
    }

    private static (double[] Temperature, FeMeshAssembler.AssembledMesh Assembled,
        IReadOnlyList<ContactInterface> Contacts) SolveStack(
        int n1, int t1, int n2, int t2, double conductance = Hc)
    {
        var assembled = FeMeshAssembler.Assemble(Slabs(n1, t1, n2, t2));
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
                new ContactDetectionSettings { DefaultConductance = conductance })
            .ToList();
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Steel,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts
        });
        var field = (NodalScalarField)output.Fields.First(f => f.Name == "Temperature");
        return (field.Values.ToArray(), assembled, contacts);
    }

    /// <summary>Mean temperature over the nodes of one body lying on the plane x = value;
    /// also asserts they agree, which is the 1-D character of the solution.</summary>
    private static double PlaneTemperature(FeMeshAssembler.AssembledMesh assembled,
        double[] temperature, int bodyIndex, double x)
    {
        var values = new List<double>();
        for (int n = 0; n < assembled.Mesh.NodeCount; n++)
            if (assembled.BodyOfNode(n) == bodyIndex && Math.Abs(assembled.Mesh.Nodes[n].X - x) < 1e-12)
                values.Add(temperature[n]);
        Assert.NotEmpty(values);
        Assert.True(values.Max() - values.Min() < 1e-8,
            $"temperatures on x = {x} of body {bodyIndex} spread by {values.Max() - values.Min():g3} K");
        return values.Average();
    }

    /// <summary>
    /// Net heat entering through a prescribed-temperature face [W], as the REACTION
    /// Σ(K·T)ᵢ over its nodes. The discrete equations make (K·T)ᵢ = 0 at every free node, so
    /// this sum is exactly the flow the discrete problem carries — and unlike reading a
    /// temperature off a plane it stays meaningful when the interface field is not uniform.
    /// </summary>
    private static double HeatInThroughFace(FeMeshAssembler.AssembledMesh assembled,
        IReadOnlyList<ContactInterface> contacts, double[] temperature, int faceId)
    {
        var assembler = new ScalarDiffusionAssembler(assembled.Mesh,
            el => assembled.RegionMaterials[assembled.Mesh.RegionOf(el)].ThermalConductivity!.Value);
        var stiffness = assembler.AssembleStiffness(null, contacts, default);
        var product = new double[assembled.Mesh.NodeCount];
        stiffness.Multiply(temperature, product);
        return assembled.Mesh.GetFaceNodes(new[] { faceId }).Sum(n => product[n]);
    }

    /// <summary>The exact one-dimensional heat flow through the stack [W].</summary>
    private static double SeriesFlow(double conductance = Hc) =>
        (Hot - Cold) / (L1 / (K1 * Area) + 1 / (conductance * Area) + L2 / (K2 * Area));

    [Fact]
    public void MatchingInterface_ReproducesTheSeriesResistanceExactly()
    {
        var (temperature, assembled, contacts) = SolveStack(2, 2, 3, 2);

        double hotEnd = PlaneTemperature(assembled, temperature, 0, 0);
        double leftFace = PlaneTemperature(assembled, temperature, 0, L1);
        double rightFace = PlaneTemperature(assembled, temperature, 1, L1);
        double coldEnd = PlaneTemperature(assembled, temperature, 1, L1 + L2);

        Assert.Equal(Hot, hotEnd, 12);
        Assert.Equal(Cold, coldEnd, 12);

        // Four independent readings of the same heat flow: the reaction at the hot end, and
        // conduction through slab 1, across the interface, and through slab 2. They must
        // agree with each other and with the analytic series result.
        double expected = SeriesFlow();
        double reaction = HeatInThroughFace(assembled, contacts, temperature, StructuredBoxMesh.FaceXMin);
        double throughLeft = K1 * Area * (hotEnd - leftFace) / L1;
        double acrossGap = Hc * Area * (leftFace - rightFace);
        double throughRight = K2 * Area * (rightFace - coldEnd) / L2;

        Assert.Equal(expected, reaction, RelativeTolerance(expected, 1e-9));
        Assert.Equal(expected, throughLeft, RelativeTolerance(expected, 1e-9));
        Assert.Equal(expected, acrossGap, RelativeTolerance(expected, 1e-9));
        Assert.Equal(expected, throughRight, RelativeTolerance(expected, 1e-9));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(4, 6)]
    public void NonMatchingInterface_ConvergesToTheSeriesResistance(int left, int right)
    {
        // The two surface meshes now share no nodes. The exact solution is still in the
        // space, but a source vertex projects into the INTERIOR of an opposing triangle,
        // where the vertex rule integrates the opposing trace only to first order — so the
        // interface temperature is no longer uniform (a known trait of node-to-surface
        // coupling; a mortar projection is what removes it) and the total flow carries a
        // discretization error. The gate is the error's SIZE and its decay under refinement,
        // not a hidden tolerance on the matching case. Measured: 0.050% at 2×3 and 0.013% at
        // 4×6 — a 3.8× decay for a 2× refinement, so the band is set at the measured level.
        var (temperature, assembled, contacts) = SolveStack(2, left, 3, right);
        double reaction = HeatInThroughFace(assembled, contacts, temperature, StructuredBoxMesh.FaceXMin);
        double error = Math.Abs(reaction - SeriesFlow()) / SeriesFlow();
        Assert.True(error < 1e-3, $"non-matching interface flow is off by {error:P3}");
    }

    [Fact]
    public void NonMatchingInterfaceError_ShrinksWithTheElementSize()
    {
        double Coarse() => Error(2, 3);
        double Fine() => Error(4, 6);
        Assert.True(Fine() < Coarse(),
            $"refinement did not improve the interface: {Coarse():P3} → {Fine():P3}");

        double Error(int left, int right)
        {
            var (temperature, assembled, contacts) = SolveStack(2, left, 3, right);
            double reaction = HeatInThroughFace(assembled, contacts, temperature,
                StructuredBoxMesh.FaceXMin);
            return Math.Abs(reaction - SeriesFlow()) / SeriesFlow();
        }
    }

    [Fact]
    public void PerfectContactLimit_ApproachesTheFusedSlab()
    {
        // Both slabs of the SAME material and a very stiff joint must reproduce a single
        // conducting bar. The bound is derived, not tuned: the interface still carries
        // R_int = 1/(h_c·A) out of R_total, so the flows differ by at most that fraction.
        const double stiff = 1e9;
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Left", 0, L1, 0, W, 0, W, 2, 2, 2, Steel),
            StructuredBoxMesh.Box("Right", L1, L1 + L2, 0, W, 0, W, 3, 2, 2, Steel)
        };
        bodies[0].BoundaryConditions.Add(new FixedTemperature
        { Name = "Hot", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = Hot });
        bodies[1].BoundaryConditions.Add(new FixedTemperature
        { Name = "Cold", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = Cold });

        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
            new ContactDetectionSettings { DefaultConductance = stiff });
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Steel,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts
        });
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature"))
            .Values.ToArray();
        double leftFace = PlaneTemperature(assembled, temperature, 0, L1);
        double measured = K1 * Area * (Hot - leftFace) / L1;

        double fused = (Hot - Cold) / ((L1 + L2) / (K1 * Area));
        double interfaceShare = (1 / (stiff * Area)) / ((L1 + L2) / (K1 * Area) + 1 / (stiff * Area));
        Assert.True(Math.Abs(measured - fused) / fused <= interfaceShare + 1e-9,
            $"contact flow {measured:g10} W vs fused {fused:g10} W exceeds the interface share " +
            $"{interfaceShare:g3}");
    }

    [Fact]
    public void EqualEndTemperatures_LeaveTheWholeAssemblyUniform()
    {
        // The sharpest sign gate available: a uniform field has zero jump, so a correctly
        // signed coupling transports nothing. A flipped sign would make the "jump" a SUM and
        // pull the second body away from equilibrium.
        var bodies = Slabs(2, 2, 3, 3);
        bodies[1].BoundaryConditions.Clear();
        bodies[1].BoundaryConditions.Add(new FixedTemperature
        { Name = "Also hot", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = Hot });

        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Steel,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts
        });
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;
        foreach (double t in temperature)
            Assert.Equal(Hot, t, 8);
    }

    [Fact]
    public void ContactMatrix_IsSymmetricAndPositiveSemiDefinite()
    {
        var assembled = FeMeshAssembler.Assemble(Slabs(2, 2, 3, 3));
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        var assembler = new ScalarDiffusionAssembler(assembled.Mesh, _ => 1.0);
        var withContacts = assembler.AssembleStiffness(null, contacts, default);

        for (int row = 0; row < withContacts.RowCount; row++)
            for (int k = withContacts.RowPointers[row]; k < withContacts.RowPointers[row + 1]; k++)
            {
                int col = withContacts.ColumnIndices[k];
                Assert.Equal(withContacts.Values[k], Entry(withContacts, col, row));
            }

        // xᵀKx ≥ 0 on deterministic pseudo-random vectors — the property the CG depends on.
        var x = new double[withContacts.RowCount];
        var kx = new double[withContacts.RowCount];
        var random = new Random(20260813);
        for (int trial = 0; trial < 5; trial++)
        {
            for (int i = 0; i < x.Length; i++) x[i] = random.NextDouble() - 0.5;
            withContacts.Multiply(x, kx);
            double quadratic = 0;
            for (int i = 0; i < x.Length; i++) quadratic += x[i] * kx[i];
            Assert.True(quadratic >= -1e-12, $"xᵀKx = {quadratic:g6} is negative");
        }
    }

    [Fact]
    public void NoContacts_LeavesTheAssembledMatrixBitwiseUnchanged()
    {
        // The regression pin for every single-body solve: adding the contact path must not
        // perturb the arithmetic of assemblies that have no contacts.
        var assembled = FeMeshAssembler.Assemble(Slabs(2, 2, 3, 3));
        var assembler = new ScalarDiffusionAssembler(assembled.Mesh, el => el % 3 + 1.0);
        var robin = assembled.Mesh.BoundaryTriangles.Take(20)
            .Select(t => new ScalarDiffusionAssembler.RobinTerm(t, 12.5)).ToList();

        var legacy = assembler.AssembleStiffness(robin);
        var withNullContacts = assembler.AssembleStiffness(robin, null, default);

        Assert.Equal(legacy.NonZeroCount, withNullContacts.NonZeroCount);
        for (int i = 0; i < legacy.Values.Length; i++)
            Assert.Equal(legacy.Values[i], withNullContacts.Values[i]);   // bitwise
    }

    [Fact]
    public void AFloatingBody_IsATypedFailureNamingIt()
    {
        // Bodies far enough apart to find no contact, with a condition on only one of them.
        var bodies = Slabs(2, 2, 3, 2, gap: 0.02);
        bodies[1].BoundaryConditions.Clear();
        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        Assert.Empty(contacts);

        var ex = Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Solve(
            new SolveInput
            {
                Mesh = assembled.Mesh,
                Material = Steel,
                RegionMaterials = assembled.RegionMaterials,
                BoundaryConditions = assembled.BoundaryConditions,
                ThermalContacts = contacts
            }));
        Assert.Contains("region(s) 1", ex.Message);
        Assert.Contains("undetermined", ex.Message);
    }

    [Fact]
    public void ContactAnchorsABodyThatCarriesNoConditionOfItsOwn()
    {
        // The same assembly, but touching: body 1 is now anchored THROUGH the interface.
        var bodies = Slabs(2, 2, 3, 2);
        bodies[1].BoundaryConditions.Clear();
        bodies[0].BoundaryConditions.Add(new Convection
        {
            Name = "Cooling",
            FaceIds = new[] { StructuredBoxMesh.FaceYMax },
            Coefficient = 25,
            AmbientTemperature = Cold
        });
        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Steel,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts
        });
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;
        Assert.All(temperature, t => Assert.InRange(t, Cold - 1e-6, Hot + 1e-6));
        Assert.Contains(output.Log, l => l.Contains("Thermal contact bodies 0–1"));
    }

    [Fact]
    public void TransientRunToSteadyState_MatchesTheSteadySolution()
    {
        var bodies = Slabs(2, 1, 2, 1);
        var assembled = FeMeshAssembler.Assemble(bodies);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        var input = new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Steel,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ThermalContacts = contacts
        };
        var steady = ((NodalScalarField)new HeatConductionSolver().Solve(input)
            .Fields.First(f => f.Name == "Temperature")).Values;

        // ρc/k gives a diffusion time of ~a second here; 2000 s is deep into steady state.
        var transient = ((NodalScalarField)new TransientThermalSolver().Solve(input with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = Cold,
                Duration = 2000,
                TimeStep = 20,
                OutputStride = 50
            }
        }).Fields.First(f => f.Name == "Temperature")).Values;

        for (int i = 0; i < steady.Count; i++)
            Assert.Equal(steady[i], transient[i], 6);
    }

    private static double Entry(CsrMatrix matrix, int row, int col)
    {
        for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
            if (matrix.ColumnIndices[k] == col) return matrix.Values[k];
        return 0;
    }

    /// <summary>Decimal places corresponding to a relative tolerance on a known magnitude —
    /// keeps the assertions readable while staying relative in substance.</summary>
    private static int RelativeTolerance(double magnitude, double relative) =>
        Math.Max(0, (int)Math.Floor(-Math.Log10(Math.Abs(magnitude) * relative)));
}
