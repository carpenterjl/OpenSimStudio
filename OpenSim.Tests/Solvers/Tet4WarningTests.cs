using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>D7: a structural solve on TET4 says so in its log; TET10 and HEX20 solves do not.</summary>
public class Tet4WarningTests
{
    private static SolveOutput SolveBlock(ElementShape shape, ElementOrder order)
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(PrimitiveFactory.CreateBox(0.02, 0.01, 0.01),
            new MeshSettings { Method = MeshMethod.StructuredLattice, Shape = shape, Divisions = new LatticeDivisions(4, 2, 2), ElementOrder = order });
        return new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = new Material { Name = "Steel", YoungsModulus = 2e11, PoissonRatio = 0.3, Density = 7850 },
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Root", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "Tip", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, -100, 0) }
            }
        });
    }

    [Fact]
    public void ATet4Solve_IsWarned_AndQuadraticSolvesAreNot()
    {
        Assert.Contains(LinearStaticSolver.Tet4Warning, SolveBlock(ElementShape.Tetrahedral, ElementOrder.Linear).Log);
        Assert.DoesNotContain(LinearStaticSolver.Tet4Warning, SolveBlock(ElementShape.Tetrahedral, ElementOrder.Quadratic).Log);
        Assert.DoesNotContain(LinearStaticSolver.Tet4Warning, SolveBlock(ElementShape.Hexahedral, ElementOrder.Quadratic).Log);
    }
}
