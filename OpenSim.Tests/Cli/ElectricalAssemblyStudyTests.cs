using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Tests.Solvers;
using Xunit;

namespace OpenSim.Tests.Cli;

/// <summary>
/// FU-27: a project of several bodies runs its DC electrical study as one assembly, joined at
/// the detected contacts with the project's electrical contact conductance — and, with no
/// conductance stated, on the first body alone, saying so.
/// </summary>
public class ElectricalAssemblyStudyTests
{
    private const double L1 = 0.02, L2 = 0.03, W = 0.01, Area = W * W, Joint = 1e7, Volts = 1.0;

    private static Material Conductor(string name, double sigma) => new()
    {
        Name = name, YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8900,
        ElectricalConductivity = sigma, ThermalConductivity = 400, SpecificHeat = 385
    };

    private static SimProject TwoBars(double? jointConductance)
    {
        var leftMaterial = Conductor("Left bar alloy", 5.96e7);
        var rightMaterial = Conductor("Right bar alloy", 1e6);
        var left = StructuredBoxMesh.Box("Left", 0, L1, 0, W, 0, W, 2, 2, 2, leftMaterial);
        var right = StructuredBoxMesh.Box("Right", L1, L1 + L2, 0, W, 0, W, 3, 2, 2, rightMaterial);
        left.BoundaryConditions.Add(new VoltagePotential { Name = "Drive", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = Volts });
        right.BoundaryConditions.Add(new VoltagePotential { Name = "Return", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = 0 });
        var project = new SimProject { Name = "Two bars", AnalysisType = "Electrical" };
        project.Bodies.Add(left);
        project.Bodies.Add(right);
        project.Assembly = new AssemblySettings { ElectricalContactConductance = jointConductance };
        return project;
    }

    [Fact]
    public void WithAJointConductance_TheBarsAreSolvedAsOneAssembly()
    {
        var log = new List<string>();
        var run = StudyRunner.Solve(TwoBars(Joint), null, new MaterialLibrary(), new StudySettings(), log.Add);

        Assert.Equal("Left + Right", run.BodyName);
        double expected = L1 / (5.96e7 * Area) + 1 / (Joint * Area) + L2 / (1e6 * Area);
        Assert.Equal(expected, run.Output.Summary!["Resistance (Ω)"], expected * 1e-9);
        Assert.Contains(log, l => l.Contains("Electrical joints: 1 contact(s)"));
        Assert.Contains("Left + Right", run.Report.ToMarkdown());
    }

    [Fact]
    public void WithoutAJointConductance_TheFirstBodyIsSolvedAlone_AndTheLogSaysSo()
    {
        var log = new List<string>();
        var run = StudyRunner.Solve(TwoBars(null), null, new MaterialLibrary(), new StudySettings(), log.Add);

        Assert.Equal("Left", run.BodyName);
        Assert.Contains(log, l => l.Contains("solving 'Left' alone"));
        // One body, one voltage: no two-terminal resistance.
        Assert.False(run.Output.Summary!.ContainsKey("Resistance (Ω)"));
    }
}
