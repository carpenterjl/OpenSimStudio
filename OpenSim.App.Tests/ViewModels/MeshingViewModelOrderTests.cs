using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// D7 (evaluation 2026-09-09, worked 2026-10-25): the mesher defaulted to TET4, whose bending
/// deflection reads as little as 40 % of Timoshenko on the suite's own meshes. The meshing panel's
/// element order now follows the analysis — quadratic for a structural one, linear for the thermal
/// and electrical solvers that take linear meshes — until the user sets the box, and the solver
/// itself says so when it is handed TET4 (LinearStaticSolverTests).
/// </summary>
public sealed class MeshingViewModelOrderTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private static AnalysisOption Option(AnalysisType kind) => AnalysisOption.All.First(o => o.Kind == kind);

    [Fact]
    public void TheOrder_FollowsTheAnalysis_UntilTheUserSetsIt()
    {
        var session = _app.Resolve<ProjectSession>();
        session.ActiveWorkspace = WorkspaceKind.Structural;
        session.SelectedAnalysis = Option(AnalysisType.Static);
        var meshing = _app.Resolve<MeshingViewModel>();
        Assert.True(meshing.QuadraticElements);                           // structural: TET10
        Assert.Contains("structural default", meshing.QuadraticLabel);

        session.SelectedAnalysis = Option(AnalysisType.Thermal);
        Assert.False(meshing.QuadraticElements);                          // thermal: linear
        Assert.Contains("linear meshes", meshing.QuadraticLabel);

        session.SelectedAnalysis = Option(AnalysisType.Modal);
        Assert.True(meshing.QuadraticElements);

        // The user clears the box: the choice sticks across analyses from then on.
        meshing.QuadraticElements = false;
        session.SelectedAnalysis = Option(AnalysisType.Thermal);
        session.SelectedAnalysis = Option(AnalysisType.Static);
        Assert.False(meshing.QuadraticElements);
    }

    [Fact]
    public void ALoadedProject_KeepsASavedQuadraticOrder_AndFollowsTheAnalysisForASavedLinearOne()
    {
        var session = _app.Resolve<ProjectSession>();
        session.ActiveWorkspace = WorkspaceKind.Structural;
        session.SelectedAnalysis = Option(AnalysisType.Thermal);
        var meshing = _app.Resolve<MeshingViewModel>();
        Assert.False(meshing.QuadraticElements);

        meshing.AdoptLoadedOrder(ElementOrder.Quadratic);               // saved quadratic: a choice, kept
        Assert.True(meshing.QuadraticElements);
        session.SelectedAnalysis = Option(AnalysisType.Static);
        session.SelectedAnalysis = Option(AnalysisType.Thermal);
        Assert.True(meshing.QuadraticElements);

        meshing.AdoptLoadedOrder(ElementOrder.Linear);                  // saved linear: the old default, follows again
        Assert.False(meshing.QuadraticElements);
        session.SelectedAnalysis = Option(AnalysisType.Static);
        Assert.True(meshing.QuadraticElements);
    }

    [Fact]
    public void Hexahedra_AreQuadratic_WhateverTheAnalysis()
    {
        var session = _app.Resolve<ProjectSession>();
        session.ActiveWorkspace = WorkspaceKind.Structural;
        session.SelectedAnalysis = Option(AnalysisType.Thermal);
        var meshing = _app.Resolve<MeshingViewModel>();
        Assert.False(meshing.QuadraticElements);
        meshing.MeshMethod = MeshMethod.StructuredLattice;
        meshing.ElementShape = ElementShape.Tetrahedral;                 // the panel starts on hexahedra; leave and come back
        Assert.False(meshing.QuadraticElements);
        meshing.ElementShape = ElementShape.Hexahedral;
        Assert.True(meshing.QuadraticElements);                           // HEX20 is the only hexahedron
        Assert.False(meshing.QuadraticOrderIsAChoice);
    }
}
