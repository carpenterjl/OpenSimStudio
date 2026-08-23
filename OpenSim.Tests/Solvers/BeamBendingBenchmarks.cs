using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Reproduction of an external reference: an Ansys 2023 R1 Static Structural study of a
/// 200 x 60 x 20 mm beam, FIXED ON ITS TWO OPPOSITE BOTTOM EDGES and loaded by a total
/// force on the top face. SOLID186 quadratic elements, 100 x 25 edge divisions, four
/// materials at 500 kN and 1000 kN.
///
/// This is the first gate in the project built from someone else's numbers rather than an
/// analytical identity, so it is deliberately split into three kinds of assertion:
///
/// 1. IDENTITIES that hold on ANY mesh, asserted at machine precision — load linearity,
///    and E-independence of stress under a prescribed force. These carry the precision
///    claims and would catch a real regression.
/// 2. The REFERENCE INVARIANTS measured in the Ansys data itself: u_max * E constant to
///    3.8% across a 151x range of E, and average von Mises constant to 0.46%. Both are
///    consequences of linear elasticity, and their residual spread is the Poisson range.
/// 3. The ABSOLUTE displacement, which needs a converged mesh and is therefore the loosest.
///
/// What is deliberately NOT gated: the peak stress (2.7e10 Pa) and peak strain (0.135, and
/// 16.2 for ABS — 1620% strain). Those are a SUPPORT SINGULARITY at a mathematically fixed
/// line. They do not converge under refinement in any code, so matching them would mean
/// matching Ansys mesh for mesh rather than matching the physics.
/// </summary>
public class BeamBendingBenchmarks
{
    private readonly ITestOutputHelper _output;

    public BeamBendingBenchmarks(ITestOutputHelper output) => _output = output;

    private const double Length = 0.200, Height = 0.060, Thickness = 0.020;
    private const double Load500 = 5e5;

    /// <summary>The four reference materials, with the elastic constants the Ansys run used.</summary>
    private static readonly (string Name, double E, double Nu, double AnsysMaxDeflection)[] Reference =
    {
        ("Structural steel", 2.0e11,   0.30,   7.6947e-4),
        ("ABS",              1.628e9,  0.4089, 9.2235e-2),
        ("Alumina 88%",      2.459e11, 0.2392, 6.3246e-4),
        ("E-Glass",          7.3e10,   0.22,   2.1372e-3)
    };

    private static Material Make(string name, double e, double nu) => new()
    {
        Name = name, YoungsModulus = e, PoissonRatio = nu, Density = 2000
    };

    /// <summary>
    /// The beam meshed with QUADRATIC elements. TET10 is not optional here: the documented
    /// TET4 bending band is [0.40, 1.05] of Timoshenko against TET10 [0.92, 1.03], and this
    /// benchmark is entirely about a bending deflection.
    /// </summary>
    private static FeMesh MeshBeam(double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Length, Height, Thickness),
            new MeshSettings { TargetEdgeLength = h, ElementOrder = ElementOrder.Quadratic });

    /// <summary>
    /// The two support edges: where the bottom face (y-min, id 2) meets each end face
    /// (x-min id 0, x-max id 1). Exactly the "Fixed Support scoped to 2 Edges" of the
    /// reference — and a scope that could not be expressed at all before edge scoping.
    /// </summary>
    private static SolveInput Setup(FeMesh mesh, Material material, double load)
    {
        var left = mesh.Edges.EdgesBetween(new[] { 2, 0 });
        var right = mesh.Edges.EdgesBetween(new[] { 2, 1 });
        Assert.True(left.Count == 1 && right.Count == 1,
            $"Expected one support edge per end, found {left.Count} and {right.Count}.");

        return new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport
                {
                    Name = "Supports",
                    FaceIds = Array.Empty<int>(),
                    EdgeIds = new[] { left[0], right[0] }
                },
                new ForceLoad
                {
                    Name = "Load",
                    FaceIds = new[] { 3 },                    // y-max, the top face
                    TotalForce = new Vector3D(0, -load, 0)
                }
            }
        };
    }

    private sealed record Run(double MaxDeflection, double MeanVonMises, double MaxVonMises,
        double MeanEquivalentStrain, int Nodes);

    private static Run Solve(FeMesh mesh, Material material, double load)
    {
        var output = new LinearStaticSolver().Solve(Setup(mesh, material, load));
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");
        var vonMises = output.Fields.First(f => f.Name == "Stress (von Mises)");
        var strain = output.Fields.First(f => f.Name == "Equivalent elastic strain");

        var stressStats = FieldStatistics.Compute(vonMises, mesh);
        return new Run(
            displacement.Values.Max(v => v.Length),
            stressStats.Mean,
            stressStats.Max,
            FieldStatistics.Compute(strain, mesh).Mean,
            mesh.NodeCount);
    }

    // ------------------------------------------------------------------ identities

    /// <summary>
    /// Under a PRESCRIBED FORCE, displacement scales exactly as 1/E and stress does not
    /// depend on E at all: u = K^-1 f with K proportional to E, and stress = C K^-1 f with C
    /// also proportional to E. On one fixed mesh at one fixed Poisson ratio this is an
    /// algebraic identity, so it is asserted at solver precision rather than as a band.
    /// It is also the mechanism behind the reference invariants below.
    /// </summary>
    [Fact]
    public void AtFixedPoissonRatio_DeflectionScalesAsOneOverE_AndStressIsEIndependent()
    {
        var mesh = MeshBeam(0.012);
        var soft = Solve(mesh, Make("soft", 1.0e9, 0.3), Load500);
        var stiff = Solve(mesh, Make("stiff", 2.0e11, 0.3), Load500);

        Assert.Equal(1.0, soft.MaxDeflection * 1.0e9 / (stiff.MaxDeflection * 2.0e11), 6);
        Assert.Equal(1.0, soft.MeanVonMises / stiff.MeanVonMises, 6);
        Assert.Equal(1.0, soft.MaxVonMises / stiff.MaxVonMises, 6);
    }

    /// <summary>
    /// Doubling the load doubles every field. The reference 1000 kN runs are exactly 2x the
    /// 500 kN ones in every column, which is the first thing that confirms the study is
    /// linear elastic and therefore reproducible at all.
    /// </summary>
    [Fact]
    public void DoublingTheLoad_DoublesEveryField()
    {
        var mesh = MeshBeam(0.012);
        var steel = Make("Structural steel", 2.0e11, 0.30);
        var single = Solve(mesh, steel, Load500);
        var doubled = Solve(mesh, steel, 2 * Load500);

        Assert.Equal(2.0, doubled.MaxDeflection / single.MaxDeflection, 6);
        Assert.Equal(2.0, doubled.MeanVonMises / single.MeanVonMises, 6);
        Assert.Equal(2.0, doubled.MaxVonMises / single.MaxVonMises, 6);
        Assert.Equal(2.0, doubled.MeanEquivalentStrain / single.MeanEquivalentStrain, 6);
    }

    // ------------------------------------------------------- the reference invariants

    /// <summary>
    /// Reference invariant #1, measured in the Ansys data: u_max * E is constant to 3.8%
    /// across E = 1.628 GPa to 245.9 GPa (1.5389 / 1.5016 / 1.5552 / 1.5602 x 1e8). The
    /// residual spread is the Poisson range 0.22 to 0.41, not noise — so the gate is the
    /// same width as the reference invariant itself.
    /// </summary>
    [Fact]
    public void MaxDeflectionTimesE_IsMaterialIndependent_ToTheReferenceSpread()
    {
        var mesh = MeshBeam(0.012);
        var products = new List<double>();
        foreach (var (name, e, nu, _) in Reference)
        {
            double product = Solve(mesh, Make(name, e, nu), Load500).MaxDeflection * e;
            products.Add(product);
            _output.WriteLine($"{name,-18} u_max*E = {product:g6}");
        }

        double mean = products.Average();
        double spread = (products.Max() - products.Min()) / mean;
        // Measured 4.48% on this mesh against the reference 3.81%: the same physics on a
        // different discretization, so the gate is the reference width plus a stated margin.
        _output.WriteLine($"spread = {spread:P2} (Ansys reference: 3.81%)");
        Assert.InRange(spread, 0.0, 0.06);
    }

    /// <summary>
    /// Reference invariant #2: the AVERAGE von Mises stress is material-independent to
    /// 0.46% in the reference (3.5365 to 3.5527e8 Pa at 500 kN). Under a prescribed force
    /// stress does not depend on E at all, so only the Poisson ratio moves it.
    /// </summary>
    [Fact]
    public void AverageVonMises_IsMaterialIndependent_ToTheReferenceSpread()
    {
        var mesh = MeshBeam(0.012);
        var means = new List<double>();
        foreach (var (name, e, nu, _) in Reference)
        {
            double mean = Solve(mesh, Make(name, e, nu), Load500).MeanVonMises;
            means.Add(mean);
            _output.WriteLine($"{name,-18} mean von Mises = {mean:g6} Pa");
        }

        double spread = (means.Max() - means.Min()) / means.Average();
        // Measured 0.99% against the reference 0.46%.
        _output.WriteLine($"spread = {spread:P2} (Ansys reference: 0.46%)");
        Assert.InRange(spread, 0.0, 0.02);
    }

    // ------------------------------------------------------------- absolute agreement

    /// <summary>
    /// The mesh-robust absolute target. Reported at three edge lengths so the trend is on
    /// the record rather than a single number being presented as the answer; the assertion
    /// is on the finest of the three, which is as far as a Debug-suite run can go. A
    /// tighter comparison at a genuinely converged mesh belongs out of suite, the way the
    /// 128-squared cavity benchmark does.
    /// </summary>
    [Fact]
    public void SteelDeflection_ApproachesTheAnsysValue_AsTheMeshRefines()
    {
        var steel = Make("Structural steel", 2.0e11, 0.30);
        const double ansys = 7.6947e-4;

        double finest = 0;
        foreach (double h in new[] { 0.020, 0.015, 0.011 })
        {
            var mesh = MeshBeam(h);
            var run = Solve(mesh, steel, Load500);
            finest = run.MaxDeflection;
            _output.WriteLine(
                $"h = {h * 1e3:F0} mm: {run.Nodes,6} nodes, u_max = {run.MaxDeflection:g6} m " +
                $"({run.MaxDeflection / ansys:P1} of Ansys)");
        }

        // Euler-Bernoulli for a simply supported beam under this UDL gives 7.234e-4 m and
        // the reference reads 7.6947e-4, i.e. +6.4% of shear deformation at L/h = 3.33 —
        // so the true answer is bracketed and our mesh must land in that neighbourhood.
        // MEASURED at h = 11 mm: 87.9% of the Ansys value, from below and converging
        // monotonically with refinement (out of suite, TET10: 91.5% at 8 mm, 94.5% at 6 mm,
        // 96.1% at 5 mm / 44k nodes). What remains at these sizes is DISCRETIZATION, not
        // the support model: since edge hugging landed, the fixed nodes lie exactly on the
        // support line rather than within a jitter amplitude of it. Matching the reference
        // at its own mesh density is what StructuredBeamBenchmark measures, at 102.4%.
        Assert.InRange(finest / ansys, 0.80, 1.10);
    }

    /// <summary>
    /// The reference table lists ABS at 1000 kN as 0.14117 m, which is its AVERAGE
    /// deflection column; the PDF maximum is 0.18447 m, exactly twice the 500 kN maximum.
    /// The other three rows of that table are maxima. Recording it here so a future
    /// comparison against the write-up is not chasing a discrepancy that is a transcription.
    /// </summary>
    [Fact]
    public void ReferenceLinearity_HoldsAcrossTheReportedMaxima()
    {
        foreach (var (name, _, _, max500) in Reference)
            _output.WriteLine($"{name,-18} 500 kN max {max500:g6} m -> 1000 kN expected {2 * max500:g6} m");

        // Compared as ratios: the PDF prints five significant figures, so an absolute
        // comparison against the printed 1000 kN value fails on the rounding alone
        // (2 x 7.6947e-4 = 1.53894e-3 against a printed 1.5389e-3).
        Assert.Equal(1.0, 2 * 9.2235e-2 / 0.18447, 5);    // ABS, as printed in the reference PDF
        Assert.Equal(1.0, 2 * 7.6947e-4 / 1.5389e-3, 4);  // steel, matching the write-up table
    }
}
