using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Everything downstream of a hexahedral solve that used to read the tetrahedron list: section
/// cutting, the exported report, and the mesh-quality summary.
///
/// These are the places where a hexahedral mesh would otherwise have failed QUIETLY rather than
/// loudly — an empty tetrahedron list produces an empty cut, a zero centroid, and a quality
/// summary averaging nothing. Each is now branched, and each branch is gated here.
/// </summary>
public class HexDownstreamTests
{
    private const double Lx = 0.20, Ly = 0.06, Lz = 0.02;

    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh HexMesh(int nx = 6, int ny = 3, int nz = 2) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    // ------------------------------------------------------------------ section cutting

    /// <summary>
    /// A mid-plane cut of a hexahedral mesh produces a cross-section covering the whole
    /// face — the sub-tetrahedra tile the element, so their cut triangles tile the section.
    /// An empty or partial result is what a missing hexahedral branch would have produced.
    /// </summary>
    [Fact]
    public void AMidPlaneCut_CoversTheWholeCrossSection()
    {
        var mesh = HexMesh();
        var scalars = new double[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++) scalars[i] = mesh.Nodes[i].Y;

        var plane = new SectionPlane(SectionAxis.X, Lx / 2);
        var cut = SectionCutter.Cut(mesh, plane, scalars, null, 0);

        Assert.NotEmpty(cut);

        // The cut triangles cover the beam's y-z cross-section exactly.
        double area = cut.Sum(t => 0.5 * Vector3D.Cross(t.P1 - t.P0, t.P2 - t.P0).Length);
        Assert.Equal(Ly * Lz, area, 10);

        // And every cut vertex lies on the plane, with its scalar the interpolated field.
        foreach (var t in cut)
            foreach (var (p, s) in new[] { (t.P0, t.S0), (t.P1, t.S1), (t.P2, t.S2) })
            {
                Assert.Equal(Lx / 2, p.X, 9);
                Assert.Equal(p.Y, s, 9);
            }
    }

    [Fact]
    public void APlaneOutsideTheMesh_CutsNothing()
    {
        var mesh = HexMesh();
        var scalars = new double[mesh.NodeCount];
        var cut = SectionCutter.Cut(mesh, new SectionPlane(SectionAxis.X, 10 * Lx), scalars, null, 0);
        Assert.Empty(cut);
    }

    // ------------------------------------------------------------------ mesh quality

    /// <summary>
    /// The quality summary of a hexahedral mesh reports the cell EDGE RATIO, so a cubic cell
    /// reads 1. Applying the tetrahedral radius ratio here would have reported a number on a
    /// different scale beside the tetrahedral one in the same UI field — or, before the
    /// branch existed, no number at all, since the tetrahedron list is empty.
    /// </summary>
    [Fact]
    public void CubicCells_ReportAPerfectEdgeRatio()
    {
        // 0.20 x 0.06 x 0.02 in 10 x 3 x 1 cells is exactly 20 mm cubes.
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(10, 3, 1)
            });

        var stats = MeshQuality.Compute(mesh);

        Assert.Equal(mesh.ElementCount, stats.ElementCount);
        Assert.Equal(mesh.NodeCount, stats.NodeCount);
        Assert.Equal(1.0, stats.MinQuality, 12);
        Assert.Equal(1.0, stats.AverageQuality, 12);
        Assert.Equal(0.02, stats.MinEdgeLength, 12);
        Assert.Equal(0.02, stats.MaxEdgeLength, 12);
        Assert.Equal(Lx * Ly * Lz, stats.TotalVolume, 12);
    }

    [Fact]
    public void StretchedCells_ReportTheirAspectRatio()
    {
        // 0.20 x 0.06 x 0.02 in 5 x 3 x 1 cells: 40 x 20 x 20 mm, ratio 0.5.
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(5, 3, 1)
            });

        var stats = MeshQuality.Compute(mesh);
        Assert.Equal(0.5, stats.MinQuality, 12);
        Assert.Equal(0.02, stats.MinEdgeLength, 12);
        Assert.Equal(0.04, stats.MaxEdgeLength, 12);
    }

    // ------------------------------------------------------------------ the exported report

    [Fact]
    public void TheExportedReport_NamesTheElementTypeAndCentresElementsOnTheirCorners()
    {
        var mesh = HexMesh(4, 2, 1);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new PressureLoad { Name = "p", FaceIds = new List<int> { 5 }, Magnitude = 1e5 }
            }
        };
        var output = new LinearStaticSolver().Solve(input);

        var context = new ResultReportContext
        {
            ProjectName = "hex",
            BodyName = "beam",
            Analysis = "Linear static",
            Material = Steel,
            BoundaryConditions = input.BoundaryConditions
        };

        var csv = ResultReportCsv.WriteSummary(output.Fields, mesh, context);

        Assert.Contains("HEX20 (quadratic)", csv);
        Assert.Contains($"{mesh.ElementCount} elements", csv);

        // An element field exports one row per element, positioned at the centroid of its
        // eight CORNERS — for the first cell of this lattice, the middle of that cell.
        var tensor = output.Fields.First(f => f.Location == FieldLocation.Element);
        var rows = ResultReportCsv.WriteField(tensor, mesh, context)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("#")).ToList();

        var expected = new Vector3D(Lx / 8, Ly / 4, Lz / 2);
        Assert.Contains(rows, l => l.Contains(expected.X.ToString("R"))
                                && l.Contains(expected.Y.ToString("R"))
                                && l.Contains(expected.Z.ToString("R")));
    }

    /// <summary>
    /// The export stays a deterministic function of the solve: no timestamp, so two exports of
    /// the same result compare byte for byte. Re-asserted here because the hexahedral branch
    /// touches the preamble.
    /// </summary>
    [Fact]
    public void TwoExportsOfTheSameHexResult_AreByteIdentical()
    {
        var mesh = HexMesh(3, 2, 1);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new List<BoundaryCondition>
            {
                new FixedSupport { Name = "root", FaceIds = new List<int> { 0 } },
                new PressureLoad { Name = "p", FaceIds = new List<int> { 5 }, Magnitude = 1e5 }
            }
        };
        var output = new LinearStaticSolver().Solve(input);
        var context = new ResultReportContext
        {
            ProjectName = "hex", BodyName = "beam", Analysis = "Linear static",
            Material = Steel, BoundaryConditions = input.BoundaryConditions
        };

        Assert.Equal(ResultReportCsv.WriteSummary(output.Fields, mesh, context),
                     ResultReportCsv.WriteSummary(output.Fields, mesh, context));
    }

    // ------------------------------------------------------------------ assembler routing

    [Fact]
    public void TheTetrahedralQuadraticAssembler_RefusesAHexMeshByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new Tet10Assembler(HexMesh(2, 2, 1), Steel));
        Assert.Contains("Hex20Assembler", ex.Message);
    }
}
