using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Cfd;

public class CfdSettingsTests
{
    // ---------------------------------------------------------------- auto domain

    [Fact]
    public void AutoDomain_FlowAlongX_Is2LUpstream_5LWake_2LCrossflow_Exactly()
    {
        // Solid [0,1]×[0,2]×[0,0.5]: L_x = 1, L_y = 2, L_z = max(0.5, 0.2·2) = 0.5.
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 2, 0.5));
        var settings = new CfdSettings { CellSize = 0.25 };

        var grid = settings.ResolveGrid(solid, new Vector3D(1, 0, 0));

        // Upstream 2·1, wake 5·1; crossflow 2·2 and 2·0.5 both sides. All extents are
        // exact multiples of 0.25, so snapping adds nothing and the box is EXACT.
        Assert.Equal(-2.0, grid.Domain.Min.X);
        Assert.Equal(-4.0, grid.Domain.Min.Y);
        Assert.Equal(-1.0, grid.Domain.Min.Z);
        Assert.Equal(6.0, grid.Domain.Max.X);
        Assert.Equal(6.0, grid.Domain.Max.Y);
        Assert.Equal(1.5, grid.Domain.Max.Z);
        Assert.Equal(32, grid.CellsX);
        Assert.Equal(40, grid.CellsY);
        Assert.Equal(10, grid.CellsZ);
        Assert.Contains(grid.Notes, n => n.Contains("wake"));
    }

    [Fact]
    public void AutoDomain_NoFlow_Is2LOnEverySide()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings { CellSize = 0.5 };

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));

        Assert.Equal(-2.0, grid.Domain.Min.X);
        Assert.Equal(3.0, grid.Domain.Max.X);
        Assert.Equal(-2.0, grid.Domain.Min.Y);
        Assert.Equal(3.0, grid.Domain.Max.Y);
        Assert.Contains(grid.Notes, n => n.Contains("no imposed flow"));
    }

    [Fact]
    public void AutoDomain_ThinBody_MarginFlooredAt20PercentOfLargestExtent()
    {
        // A plate: 1×1×0.01. The z margin must use L_z = 0.2·1, not 0.01 — the flow
        // needs room OVER the plate, not a hundredth of a plate-thickness.
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 0.01));
        var settings = new CfdSettings { CellSize = 0.05 };

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));

        Assert.Equal(-0.4, grid.Domain.Min.Z, 12);
        Assert.True(grid.Domain.Max.Z >= 0.01 + 0.4 - 1e-12);
    }

    [Fact]
    public void UserDomainBox_IsHonored_AndSnappedOutwardToWholeCells()
    {
        var solid = new Aabb(new Vector3D(0.2, 0.2, 0.2), new Vector3D(0.8, 0.8, 0.8));
        var settings = new CfdSettings
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1.1)),
            CellSize = 0.25
        };

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));

        Assert.Equal(0.0, grid.Domain.Min.X);
        Assert.Equal(1.0, grid.Domain.Max.X);            // 4 cells exactly
        Assert.Equal(4, grid.CellsX);
        Assert.Equal(5, grid.CellsZ);                    // ceil(1.1/0.25) = 5
        Assert.Equal(0.25 * 5, grid.Domain.Max.Z);       // snapped OUTWARD: 1.25, not 1.1
    }

    [Fact]
    public void UserDomainBox_MayCoverPartOfTheSolids_ButMustSayHowMuch()
    {
        // The rule is INTERSECTION, not containment: an internal-flow domain is
        // deliberately a subset of the solid — the passage and nothing else — and
        // forcing the box to swallow the whole part would drag every external pocket in
        // as a sealed cavity. What the user is owed is the COVERAGE, said out loud,
        // because solid outside the domain is invisible to the flow.
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.5, 1, 1)),
            CellSize = 0.1
        };

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));
        Assert.Equal(0.5, grid.Domain.Max.X, 12);
        Assert.Contains(grid.Notes, n => n.Contains("50.0% of"));
    }

    [Fact]
    public void UserDomainBox_MissingTheSolidsEntirely_IsATypedFailure()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings
        {
            DomainBox = new Aabb(new Vector3D(5, 5, 5), new Vector3D(6, 6, 6)),
            CellSize = 0.1
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => settings.ResolveGrid(solid, new Vector3D(0, 0, 0)));
        Assert.Contains("does not overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroExtentSolid_IsATypedFailure()
    {
        var point = new Aabb(new Vector3D(1, 1, 1), new Vector3D(1, 1, 1));
        var settings = new CfdSettings();

        Assert.Throws<InvalidOperationException>(
            () => settings.ResolveGrid(point, new Vector3D(0, 0, 0)));
    }

    // ---------------------------------------------------------------- cell size

    [Fact]
    public void AutoCellSize_IsSmallestSolidExtentOver24()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings();

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));

        Assert.Equal(1.0 / 24.0, grid.CellSize);
        Assert.True(grid.CellCount <= settings.MaxCells);
        Assert.Contains(grid.Notes, n => n.Contains("Auto cell size"));
    }

    [Fact]
    public void ExplicitCellSize_OverTheBudget_IsATypedFailureNamingTheSizeThatFits()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings { CellSize = 0.01 };   // 500³ = 125M cells

        var ex = Assert.Throws<InvalidOperationException>(
            () => settings.ResolveGrid(solid, new Vector3D(0, 0, 0)));
        Assert.Contains("fits", ex.Message);
        Assert.Contains("cell size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoCellSize_OverTheBudget_CoarsensToFit_WithANote()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        var settings = new CfdSettings { MaxCells = 1000 };

        var grid = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));

        Assert.True(grid.CellCount <= 1000);
        Assert.Contains(grid.Notes, n => n.Contains("coarsened"));
    }

    // ---------------------------------------------------------------- face policy

    [Fact]
    public void ForExternalFlow_AlongX_InletUpstream_OutletDownstream_SymmetryLateral()
    {
        var s = CfdSettings.ForExternalFlow(new Vector3D(2, 0, 0));

        Assert.Equal(FlowFaceKind.InletVelocity, s.XMinFace);
        Assert.Equal(FlowFaceKind.OutletPressure, s.XMaxFace);
        Assert.Equal(FlowFaceKind.Symmetry, s.YMinFace);
        Assert.Equal(FlowFaceKind.Symmetry, s.YMaxFace);
        Assert.Equal(FlowFaceKind.Symmetry, s.ZMinFace);
        Assert.Equal(FlowFaceKind.Symmetry, s.ZMaxFace);
        Assert.Equal(new Vector3D(2, 0, 0), s.InletVelocity);
    }

    [Fact]
    public void ForExternalFlow_NegativeZ_SwapsInletAndOutlet()
    {
        var s = CfdSettings.ForExternalFlow(new Vector3D(0, 0, -1));

        Assert.Equal(FlowFaceKind.OutletPressure, s.ZMinFace);
        Assert.Equal(FlowFaceKind.InletVelocity, s.ZMaxFace);
        Assert.Equal(FlowFaceKind.Symmetry, s.XMinFace);
    }

    [Fact]
    public void ForExternalFlow_StillFluid_OpensEveryFaceAsAPressureOutlet()
    {
        var s = CfdSettings.ForExternalFlow(new Vector3D(0, 0, 0));

        foreach (var face in new[] { BoxFace.XMin, BoxFace.XMax, BoxFace.YMin,
                                     BoxFace.YMax, BoxFace.ZMin, BoxFace.ZMax })
            Assert.Equal(FlowFaceKind.OutletPressure, s.FaceKind(face));
    }

    [Fact]
    public void HasOpenBoundary_FalseForClosedBox_TrueWithAnOpening()
    {
        var closed = new CfdSettings();
        Assert.False(closed.HasOpenBoundary);

        var withOpening = closed with
        {
            Openings = new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0, UMax = 1, VMin = 0, VMax = 1,
                    Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(1, 0, 0)
                }
            }
        };
        Assert.True(withOpening.HasOpenBoundary);

        var external = CfdSettings.ForExternalFlow(new Vector3D(1, 0, 0));
        Assert.True(external.HasOpenBoundary);
    }
}
