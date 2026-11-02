using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Results;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// Results belong to the analysis that produced them: the rail, the results list and everything
/// that reads them show the selected analysis's own, or say it has not been solved.
/// </summary>
public sealed class ResultsPerAnalysisTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private ProjectSession Session => _app.Resolve<ProjectSession>();
    private StudyRailViewModel Rail => _app.Resolve<StudyRailViewModel>();
    private ResultsViewModel Results => _app.Resolve<ResultsViewModel>();

    private static IResultField[] OneField(string name) =>
        new IResultField[] { new NodalScalarField(name, "Pa", new[] { 1.0, 2.0 }) };

    private static AnalysisOption Option(AnalysisType kind) => AnalysisOption.All.First(o => o.Kind == kind);

    private StudyStep Step(StepKind kind) => Rail.Steps.First(s => s.Kind == kind);

    [Fact]
    public void AnAnalysisWithNoSolve_ShowsNoResults_AndItsResultsStepIsLocked()
    {
        _ = Rail; _ = Results;
        Session.RaiseResultsProduced(OneField("Stress"), AnalysisType.Static);
        Assert.Equal(StepState.Done, Step(StepKind.Results).State);
        Assert.Equal(StepKind.Results, Rail.SelectedStep!.Kind);

        Session.SelectedAnalysis = Option(AnalysisType.Modal);

        Assert.Empty(Results.ResultFields);
        Assert.Null(Results.SelectedField);
        Assert.Equal(StepState.Locked, Step(StepKind.Results).State);
        Assert.Equal("No solution yet", Step(StepKind.Results).Detail);
        Assert.NotEqual(StepKind.Results, Rail.SelectedStep!.Kind);   // it gave way to an open step
        Assert.Equal("Ready", Session.StatusText);
    }

    [Fact]
    public void SelectingTheAnalysisAgain_BringsItsResultsBack_WithoutMovingTheUser()
    {
        _ = Rail; _ = Results;
        var stress = OneField("Stress");
        Session.RaiseResultsProduced(stress, AnalysisType.Static);
        Session.SelectedAnalysis = Option(AnalysisType.Modal);
        var before = Rail.SelectedStep;

        Session.SelectedAnalysis = Option(AnalysisType.Static);

        Assert.Same(stress[0], Assert.Single(Results.ResultFields));
        Assert.Equal(StepState.Done, Step(StepKind.Results).State);
        Assert.Same(before, Rail.SelectedStep);
    }

    [Fact]
    public void EachAnalysisKeepsItsOwnResults()
    {
        _ = Rail; _ = Results;
        var stress = OneField("Stress");
        var mode = OneField("Mode shape");
        Session.RaiseResultsProduced(stress, AnalysisType.Static);
        Session.SelectedAnalysis = Option(AnalysisType.Modal);
        Session.RaiseResultsProduced(mode, AnalysisType.Modal);
        Assert.Same(mode[0], Assert.Single(Results.ResultFields));

        Session.SelectedAnalysis = Option(AnalysisType.Static);
        Assert.Same(stress[0], Assert.Single(Results.ResultFields));

        Session.SelectedAnalysis = Option(AnalysisType.Modal);
        Assert.Same(mode[0], Assert.Single(Results.ResultFields));
    }

    [Fact]
    public void ASolveThatEndsAfterThePickerMoved_IsKeptForItsAnalysis_NotShownOnTheOther()
    {
        _ = Rail; _ = Results;
        Session.SelectedAnalysis = Option(AnalysisType.Modal);

        Session.RaiseResultsProduced(OneField("Stress"), AnalysisType.Static);

        Assert.Empty(Results.ResultFields);
        Assert.Equal(StepState.Locked, Step(StepKind.Results).State);

        Session.SelectedAnalysis = Option(AnalysisType.Static);
        Assert.Single(Results.ResultFields);
    }

    [Fact]
    public void ANewMesh_EndsTheResultsOfEveryAnalysis()
    {
        _ = Rail; _ = Results;
        Session.RaiseResultsProduced(OneField("Stress"), AnalysisType.Static);
        Session.SelectedAnalysis = Option(AnalysisType.Modal);
        Session.RaiseResultsProduced(OneField("Mode shape"), AnalysisType.Modal);

        Session.RaiseMeshChanged();
        Assert.Empty(Results.ResultFields);
        Assert.Equal(StepState.Locked, Step(StepKind.Results).State);

        Session.SelectedAnalysis = Option(AnalysisType.Static);
        Assert.Empty(Results.ResultFields);
    }
}
