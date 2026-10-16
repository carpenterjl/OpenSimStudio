using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-52: mesh-convergence (order) tests for the flux of the scalar solvers and its
/// recovery (FU-45), on the unit cube.
/// <para>
/// u = x + a·cosh(2π(x − ½))·cos 2πy is harmonic, its normal derivative vanishes on the
/// four faces y, z ∈ {0, 1}, which are left insulated, and u itself is the fixed value on
/// the faces x = 0 and x = 1 (every node, mid-edge nodes included). Its flux is q = −k∇u;
/// the cosine integrates to zero over a cross section, so the flux through any plane that
/// separates the two fixed faces is −k. (The mode is symmetric about y = ½: an
/// antisymmetric one, cos πy, has a discrete net flux of exactly zero on this mesh too, and
/// would measure nothing.)
/// Structured meshes halve h exactly, so the observed order is log₂ of the ratio of
/// successive errors.
/// </para>
/// </summary>
public class FluxRecoveryConvergenceTests
{
    private readonly ITestOutputHelper _output;
    public FluxRecoveryConvergenceTests(ITestOutputHelper output) => _output = output;

    private const double K = 2.5;
    private const double A = 0.3;
    private const double Rate = 2 * Math.PI;

    private static double U(Vector3D p) => p.X + A * Math.Cosh(Rate * (p.X - 0.5)) * Math.Cos(Rate * p.Y);

    private static Vector3D Q(Vector3D p)
    {
        double ch = Math.Cosh(Rate * (p.X - 0.5)), sh = Math.Sinh(Rate * (p.X - 0.5));
        return new Vector3D(1 + A * Rate * sh * Math.Cos(Rate * p.Y), -A * Rate * ch * Math.Sin(Rate * p.Y), 0) * -K;
    }

    /// <summary>The planes: between node layers, on a node layer, and oblique.</summary>
    private static readonly (string Name, Vector3D Origin, Vector3D Normal)[] Planes =
    {
        ("x = 0.37", new Vector3D(0.37, 0, 0), Vector3D.UnitX),
        ("x = 0.5", new Vector3D(0.5, 0, 0), Vector3D.UnitX),
        ("oblique", new Vector3D(0.5, 0.5, 0.5), new Vector3D(1, 0.3, 0.2))
    };

    internal sealed record Errors(double Raw, double Recovered, double TrueEnergy, double EstimatedEnergy,
        double[] Conservative, double[] Section);

    private static Errors Solve(int n, bool quadratic)
    {
        var mesh = StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, n, n, n);
        if (quadratic) mesh = QuadraticMeshBuilder.Upgrade(mesh);
        var assembler = new ScalarDiffusionAssembler(mesh, _ => K);
        var stiffness = assembler.AssembleStiffness();
        var prescribed = new Dictionary<int, double>();
        foreach (var t in mesh.BoundaryTriangles)
            if (t.FaceId is StructuredBoxMesh.FaceXMin or StructuredBoxMesh.FaceXMax)
                foreach (int node in assembler.TriangleNodes(t))
                    prescribed[node] = U(mesh.Nodes[node]);
        var u = ConstrainedSystemSolver.Solve(stiffness, new double[mesh.NodeCount], prescribed, tolerance: 1e-14)
            .Displacements;

        var recovery = FluxRecovery.Recover(assembler, u, _ => ConductivityTensor.Isotropic(K));
        double raw = 0, recovered = 0, energy = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double volume = mesh.ElementVolume(e);
            var t = mesh.Elements[e];
            foreach (var (l, w) in ScalarElements.TetDegree5)
            {
                var x = mesh.Nodes[t.N0] * l[0] + mesh.Nodes[t.N1] * l[1] + mesh.Nodes[t.N2] * l[2] + mesh.Nodes[t.N3] * l[3];
                var exact = Q(x);
                var computed = recovery.ComputedAt(e, l);
                raw += w * volume * (exact - computed).LengthSquared;
                recovered += w * volume * (exact - recovery.RecoveredAt(e, l)).LengthSquared;
                energy += w * volume * (exact - computed).LengthSquared / K;
            }
        }

        // The insulated faces carry nothing; the fixed faces' outflow is what the solve finds.
        FluxProbeEvaluator.KnownOutflow known = (ti, _) =>
            mesh.BoundaryTriangles[ti].FaceId is StructuredBoxMesh.FaceXMin or StructuredBoxMesh.FaceXMax ? null : 0;
        var probes = Planes.Select(p => FluxProbeEvaluator.Evaluate(
            new FluxProbe { Name = p.Name, Origin = p.Origin, Normal = p.Normal }, recovery, assembler, null, known)).ToList();

        return new Errors(Math.Sqrt(raw), Math.Sqrt(recovered), Math.Sqrt(energy), Math.Sqrt(recovery.ErrorEnergy),
            probes.Select(r => Math.Abs(r.Conservative!.Value + K)).ToArray(),
            probes.Select(r => Math.Abs(r.Section + K)).ToArray());
    }

    /// <summary>
    /// The computed flux converges at the element's order p (1 on TET4, 2 on TET10). The
    /// recovered flux converges faster — in the interior at the superconvergent rate, and over
    /// the whole cube, where the patches at the boundary only extrapolate, at p + ½ in the
    /// limit — and the estimate's effectivity η/‖q − q_h‖ tends to 1 from level to level.
    /// The probes' conservative value is the discrete balance, and with fixed values on two
    /// whole parallel faces that balance is exact for any field: x itself lies in the element
    /// space, so the discrete current is k·∮u_h·n_x over the fixed faces alone. Every plane —
    /// between node layers, on one, oblique — reads −k to round-off. The section value
    /// converges at least at order p + 1 over the three levels.
    /// </summary>
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 4)]
    public void FluxRecoveryAndProbes_ConvergeAtTheirOrders(bool quadratic, int coarsest)
    {
        int p = quadratic ? 2 : 1;
        var levels = new[] { coarsest, 2 * coarsest, 4 * coarsest }.Select(n => Solve(n, quadratic)).ToList();
        foreach (var e in levels)
            _output.WriteLine($"raw {e.Raw:e4} recovered {e.Recovered:e4} effectivity {e.EstimatedEnergy / e.TrueEnergy:F4} " +
                              $"conservative {string.Join(" ", e.Conservative.Select(v => v.ToString("e2")))} " +
                              $"section {string.Join(" ", e.Section.Select(v => v.ToString("e3")))}");
        var (middle, finest) = (levels[1], levels[2]);

        double rawOrder = Math.Log2(middle.Raw / finest.Raw);
        double recoveredOrder = Math.Log2(middle.Recovered / finest.Recovered);
        _output.WriteLine($"orders: computed {rawOrder:F3}, recovered {recoveredOrder:F3}");
        Assert.InRange(rawOrder, p - 0.05, p + 0.05);
        Assert.True(recoveredOrder >= p + 0.35, $"recovered order {recoveredOrder:F3}");
        Assert.True(finest.Recovered < 0.5 * finest.Raw);

        var miss = levels.Select(e => Math.Abs(e.EstimatedEnergy / e.TrueEnergy - 1)).ToList();
        Assert.True(miss[2] <= 0.1, $"effectivity off by {miss[2]:F4}");
        Assert.True(miss[1] < miss[0] && miss[2] < miss[1], "the effectivity does not approach 1");

        foreach (var e in levels)
            Assert.All(e.Conservative, error => Assert.True(error < 1e-12 * K, $"conservative error {error:g3}"));
        double sectionOrder = Math.Log2(levels[0].Section[0] / finest.Section[0]) / 2;
        _output.WriteLine($"section order over the three levels at x = 0.37: {sectionOrder:F3}");
        Assert.True(sectionOrder >= p + 1, $"section order {sectionOrder:F3}");
    }
}
