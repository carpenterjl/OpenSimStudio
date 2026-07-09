using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry.Step;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// The vertical-slice gate: a STEP solid must flow through the SAME pipeline as every
/// other geometry source — watertight gate in the tet mesher, native face ids usable as
/// boundary-condition targets, and a physically sane static solve at the end.
/// </summary>
public class StepEndToEndTests
{
    [Fact]
    public void StepBox_Meshes_AndAxialStretchMatchesRodTheory()
    {
        // 10×10×50 mm column, fixture face order: 0 bottom, 1 top, 2..5 sides.
        var mesh = new StepImporter().ImportText(StepFixtures.Box(10, 10, 50)).Mesh;
        Assert.True(mesh.IsWatertight());

        var feMesh = new DelaunayMeshGenerator().Generate(mesh,
            new MeshSettings { TargetEdgeLength = 4e-3 });
        Assert.True(feMesh.ElementCount > 0);

        var steel = new Material
        {
            Name = "Steel",
            YoungsModulus = 200e9,
            PoissonRatio = 0.30,
            Density = 7850
        };
        const double force = 1000;
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = feMesh,
            Material = steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Base", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "Pull", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, 0, force) }
            }
        });

        var displacement = (NodalVectorField)output.Fields.Single(f => f.Name == "Displacement");
        var topNodes = feMesh.GetFaceNodes(new[] { 1 });
        double meanTip = topNodes.Average(n => displacement.Values[n].Z);

        // δ = FL/EA; the axial state is nearly constant strain, which TET4 represents
        // well — a one-sided band consistent with the element's slightly stiff bias.
        double exact = force * 0.05 / (steel.YoungsModulus * 0.01 * 0.01);
        Assert.InRange(meanTip, 0.85 * exact, 1.02 * exact);
    }
}
