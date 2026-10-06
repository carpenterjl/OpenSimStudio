using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Studies;
using OpenSim.Solvers;
using OpenSim.Tests.Solvers;
using Xunit.Abstractions;

namespace OpenSim.Tests.Core;

/// <summary>Feature 15: the sweep engine and the study report, against functions whose
/// answers are known exactly.</summary>
public class ParameterSweepTests
{
    private readonly ITestOutputHelper _out;
    public ParameterSweepTests(ITestOutputHelper output) => _out = output;

    private static readonly SweepParameter A = SweepParameter.Linear("a", 1, 3, 3);                 // 1, 2, 3
    private static readonly SweepParameter B = SweepParameter.Tolerance("b", 10, 0.1, 3, "mm");     // 9, 10, 11

    private static IReadOnlyDictionary<string, double> Linear(IReadOnlyDictionary<string, double> p) =>
        new Dictionary<string, double> { ["y"] = 2 * p["a"] + 3 * p["b"], ["z"] = p["a"] * p["b"] };

    [Fact]
    public void Plans_EnumerateTheRightPoints()
    {
        var factorial = new SweepPlan { Parameters = new[] { A, B } };
        var points = factorial.Points().ToList();
        Assert.Equal(9, factorial.Count);
        Assert.Equal(9, points.Count);
        Assert.Equal((1.0, 9.0), (points[0]["a"], points[0]["b"]));
        Assert.Equal((1.0, 10.0), (points[1]["a"], points[1]["b"]));      // the last parameter runs fastest
        Assert.Equal((3.0, 11.0), (points[8]["a"], points[8]["b"]));
        Assert.Equal(Enumerable.Range(0, 9), points.Select(p => p.Index));

        var oneAtATime = new SweepPlan { Parameters = new[] { A, B }, Mode = SweepMode.OneAtATime };
        points = oneAtATime.Points().ToList();
        Assert.Equal(7, points.Count);
        Assert.Equal((2.0, 10.0), (points[0]["a"], points[0]["b"]));      // nominal: middle of a, the given nominal of b
        Assert.Null(points[0].Varied);
        Assert.All(points.Skip(1).Take(3), p => { Assert.Equal("a", p.Varied); Assert.Equal(10.0, p["b"]); });
        Assert.All(points.Skip(4), p => { Assert.Equal("b", p.Varied); Assert.Equal(2.0, p["a"]); });

        var corners = new SweepPlan { Parameters = new[] { A, B }, Mode = SweepMode.Corners };
        points = corners.Points().ToList();
        Assert.Equal(5, points.Count);
        Assert.Equal((2.0, 10.0), (points[0]["a"], points[0]["b"]));
        Assert.Contains(points, p => p["a"] == 1 && p["b"] == 11);
        Assert.Contains(points, p => p["a"] == 3 && p["b"] == 9);
        Assert.Equal(4, points.Skip(1).Distinct().Count());

        Assert.Throws<InvalidOperationException>(() => new SweepPlan { Parameters = new[] { A, A } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SweepPlan { Parameters = Array.Empty<SweepParameter>() }.Validate());
    }

    [Fact]
    public void OneAtATime_SensitivitiesAreTheExactSlopes_AndTheCsvHoldsEveryRun()
    {
        var plan = new SweepPlan { Parameters = new[] { A, B }, Mode = SweepMode.OneAtATime };
        var result = ParameterSweep.Run(plan, Linear);
        _out.WriteLine(string.Join("\n", result.Describe()));
        Assert.Equal(0, result.Failures);
        Assert.Equal(new[] { "y", "z" }, result.OutputNames);

        var s = result.Sensitivities();
        Assert.Equal(2.0, s.Single(x => x.Parameter == "a" && x.Output == "y").Slope, 12);
        Assert.Equal(3.0, s.Single(x => x.Parameter == "b" && x.Output == "y").Slope, 12);
        // z = a·b: dz/da = b = 10 at nominal; normalized (Δz/z)/(Δa/a) = 1 exactly for a product.
        Assert.Equal(10.0, s.Single(x => x.Parameter == "a" && x.Output == "z").Slope, 12);
        Assert.Equal(1.0, s.Single(x => x.Parameter == "a" && x.Output == "z").Normalized, 12);
        Assert.Equal(1.0, s.Single(x => x.Parameter == "b" && x.Output == "z").Normalized, 12);
        // y = 2a + 3b at (2, 10) is 34: normalized to a = (4/34)/(2/2) = 0.1176…
        Assert.Equal(4.0 / 34, s.Single(x => x.Parameter == "a" && x.Output == "y").Normalized, 12);

        var (min, atMin, max, atMax) = result.Range("y");
        Assert.Equal(2 * 2 + 3 * 9, min, 12);
        Assert.Equal(9.0, atMin["b"]);
        Assert.Equal(2 * 2 + 3 * 11, max, 12);
        Assert.Equal(11.0, atMax["b"]);
        Assert.Equal(34.0, result.Nominal()!.Outputs["y"], 12);

        var csv = result.ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("run,a,b [mm],y,z,error", csv[0]);
        Assert.Equal(8, csv.Length);
        Assert.Equal("0,2,10,34,20,", csv[1]);
    }

    [Fact]
    public void FullFactorial_FindsTheNominalRow_AndRecordsFailures()
    {
        var plan = new SweepPlan { Parameters = new[] { A, B } };
        int calls = 0;
        var result = ParameterSweep.Run(plan, p =>
        {
            calls++;
            if (p["a"] == 3 && p["b"] == 11) throw new InvalidOperationException("the corner did not converge");
            return Linear(p);
        });
        Assert.Equal(9, calls);
        Assert.Equal(1, result.Failures);
        var failed = result.Rows.Single(r => !r.Succeeded);
        Assert.Equal("the corner did not converge", failed.Error);
        Assert.Equal((3.0, 11.0), (failed.Point["a"], failed.Point["b"]));
        Assert.Equal(34.0, result.Nominal()!.Outputs["y"], 12);
        Assert.Empty(result.Sensitivities());
        Assert.Contains("the corner did not converge", result.ToCsv());
        Assert.Contains("1 failed", string.Join("\n", result.Describe()));
    }

    [Fact]
    public void StudyReport_FromASolve_CarriesInputsResultsAndAssumptions()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.01, 0, 0.01, 0, 0.02, 2, 2, 4);
        var material = StructuredBoxMesh.Conductor("rod", 400);
        var input = new SolveInput
        {
            Mesh = mesh, Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "cold", FaceIds = new[] { StructuredBoxMesh.FaceZMin }, Kelvin = 300 },
                new FixedTemperature { Name = "hot", FaceIds = new[] { StructuredBoxMesh.FaceZMax }, Kelvin = 400 }
            }
        };
        var output = new HeatConductionSolver().Solve(input);
        var report = StudyReport.FromSolve("Rod project", "rod", "Thermal", input, output, new[] { "The rod's sides are adiabatic." });
        string md = report.ToMarkdown();
        _out.WriteLine(md);
        Assert.StartsWith("# Rod project — Thermal", md);
        Assert.Contains("| Condition 'hot' | FixedTemperature on 1 face(s): 400 K |", md);
        Assert.Contains("| Temperature | K | 300 | 400 |", md);
        Assert.Contains("## Assumptions and limits", md);
        Assert.Contains("- The rod's sides are adiabatic.", md);
        Assert.Contains("## Solver log", md);
        Assert.Equal(report.Log, output.Log);

        string html = report.ToHtml();
        Assert.Contains("<title>Rod project — Thermal</title>", html);
        Assert.Contains("<td>Temperature</td><td>K</td><td>300</td><td>400</td>", html);
        Assert.DoesNotContain("<script", html);

        // Escaping: a value with markup in it is text, not markup.
        var hostile = new StudyReport { Title = "a <b> & c", Inputs = new[] { ("k|ey", "v|alue") } };
        Assert.Contains("a &lt;b&gt; &amp; c", hostile.ToHtml());
        Assert.Contains(@"| k\|ey | v\|alue |", hostile.ToMarkdown());
        Assert.Contains("a <b> & c", hostile.ToText());
    }
}
