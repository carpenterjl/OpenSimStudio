using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// HEX20 through the modal solver, against the same analytical rod and beam frequencies the
/// tetrahedral benchmarks use.
///
/// Modal is where the choice of full 3x3x3 integration earns its keep. Reduced integration on
/// a 20-node hexahedron admits spurious zero-energy modes, and in an eigensolve those are not
/// a cosmetic artifact — they are eigenvalues, and they would appear among the reported
/// frequencies as modes of a structure that has none. That the frequencies below land where
/// the analysis says they should IS the evidence that no such mode is present.
/// </summary>
public class HexModalBenchmarks
{
    private readonly ITestOutputHelper _output;

    public HexModalBenchmarks(ITestOutputHelper output) => _output = output;

    private static readonly Material Steel = new()
    {
        Name = "Steel", YoungsModulus = 200e9, PoissonRatio = 0.3, Density = 7850
    };

    private static FeMesh HexBox(double x, double y, double z, int nx, int ny, int nz) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    private static SolveInput CantileverInput(FeMesh mesh, Material material, int modes) => new()
    {
        Mesh = mesh,
        Material = material,
        BoundaryConditions = new BoundaryCondition[]
        {
            new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } }
        },
        Modal = new ModalSettings { ModeCount = modes }
    };

    private static double ModeFrequency(SolveOutput output, int index) =>
        output.Frames![index].Summary!["Frequency (Hz)"];

    /// <summary>
    /// Fixed-free axial rod at nu = 0, where u_x = sin(pi*x/2L) uniform over the cross-section
    /// is an EXACT three-dimensional eigenmode, so the one-dimensional f = (1/4L)*sqrt(E/rho)
    /// applies with no slenderness correction.
    /// <para>
    /// The proportions and the mode count mirror the tetrahedral benchmark beside this one, and
    /// the mesh is deliberately COARSE. A subspace iteration costs an inner CG solve per vector
    /// per pass, and a HEX20 mesh has roughly seven times the couplings per row that the linear
    /// tetrahedra of that benchmark do — a finer mesh here does not make the answer better, it
    /// makes the test take minutes.
    /// </para>
    /// <para>
    /// The axial mode is not the lowest — bending pairs and torsion come first — so it is found
    /// by axial DOMINANCE (the share of modal energy in the x direction), and a run that finds
    /// no dominant axial mode fails saying so rather than measuring whichever mode came first.
    /// </para>
    /// </summary>
    [Fact]
    public void AxialRod_FundamentalAxialFrequency_MatchesTheClosedForm()
    {
        const double length = 0.5, side = 0.1;
        var material = Steel with { PoissonRatio = 0.0 };
        var mesh = HexBox(length, side, side, 8, 2, 2);

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, material, 10));

        int axialMode = -1;
        double bestDominance = 0;
        for (int i = 0; i < output.Frames!.Count; i++)
        {
            var shape = (NodalVectorField)output.Frames[i].Fields.Single(f => f.Name == "Mode shape");
            double axial = 0, total = 0;
            foreach (var v in shape.Values)
            {
                axial += v.X * v.X;
                total += v.LengthSquared;
            }
            double dominance = total > 0 ? axial / total : 0;
            if (dominance > bestDominance)
            {
                bestDominance = dominance;
                axialMode = i;
            }
        }

        Assert.True(bestDominance > 0.8,
            $"No predominantly axial mode among the first 10 (best dominance {bestDominance:g3}).");

        double analytic = Math.Sqrt(material.YoungsModulus / material.Density) / (4 * length);
        double measured = ModeFrequency(output, axialMode);
        _output.WriteLine($"axial mode {axialMode}: {measured:g6} Hz against {analytic:g6} Hz " +
                          $"-> {measured / analytic:P2} (dominance {bestDominance:g3})");

        Assert.InRange(measured / analytic, 0.97, 1.03);
    }

    /// <summary>
    /// The first bending pair of a cantilever, against Euler-Bernoulli with the standard
    /// 1.875104 root. A short beam is genuinely stiffer than that thin-beam value, so the band
    /// is one-sided high — the same shape as the tetrahedral gate, and expected to sit inside
    /// TET10's [0.95, 1.06].
    /// </summary>
    [Fact]
    public void CantileverBeam_FirstBendingFrequency_MatchesEulerBernoulli()
    {
        const double length = 0.40, width = 0.02, height = 0.03;
        var mesh = HexBox(length, width, height, 20, 2, 3);

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 4));
        double measured = ModeFrequency(output, 0);

        // Bending about the weaker axis: the smaller second moment of area governs.
        double inertia = length > 0 ? Math.Min(
            height * width * width * width / 12.0,
            width * height * height * height / 12.0) : 0;
        double area = width * height;
        const double beta = 1.875104068711961;
        double analytic = beta * beta / (2 * Math.PI * length * length)
            * Math.Sqrt(Steel.YoungsModulus * inertia / (Steel.Density * area));

        _output.WriteLine($"first bending {measured:g6} Hz against Euler-Bernoulli " +
                          $"{analytic:g6} Hz -> {measured / analytic:P2}");

        Assert.InRange(measured / analytic, 0.95, 1.06);
    }

    /// <summary>
    /// Every reported frequency must be a real, positive mode. A spurious zero-energy mode from
    /// under-integration would surface here as a frequency at or near zero among the first few —
    /// the failure this element's quadrature choice exists to prevent.
    /// </summary>
    [Fact]
    public void NoSpuriousZeroEnergyModesAppear()
    {
        const double length = 0.30, width = 0.03, height = 0.02;
        var mesh = HexBox(length, width, height, 12, 2, 2);

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 8));

        var frequencies = Enumerable.Range(0, output.Frames!.Count)
            .Select(i => ModeFrequency(output, i)).ToList();
        _output.WriteLine("modes: " + string.Join(", ", frequencies.Select(f => $"{f:g5} Hz")));

        // The first bending mode of this beam is hundreds of Hz; anything far below it would be
        // a mode of nothing.
        double first = frequencies[0];
        Assert.True(first > 100, $"the lowest mode is {first:g4} Hz, which is not a real mode of this beam");
        Assert.Equal(frequencies.OrderBy(f => f), frequencies);      // reported in order
        Assert.All(frequencies, f => Assert.True(f > 0));
    }

    /// <summary>The solve reports the element it used, as the static solver does.</summary>
    [Fact]
    public void AModalSolveLogsTheElementType()
    {
        var mesh = HexBox(0.20, 0.03, 0.02, 8, 2, 2);
        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 2));
        Assert.Contains(output.Log, line => line.Contains("HEX20"));
    }
}
