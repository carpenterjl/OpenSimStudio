using System.Text;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>OP-67: tolerance scripts in the macro language (OpticStudio 2025 R2 help, "Using
/// Tolerance Scripts", "The Tolerance Script Commands", "Tolerance Script Example", saved under
/// plans/sources/optics/op-67-help/). The lens is the tolerance-run fixture singlet (arbitrary
/// inputs).</summary>
public sealed class ToleranceScriptTests
{
    /// <summary>The marginal ray at the image (surface 3 of the singlet) and the chief ray: a small
    /// merit function every script below loads.</summary>
    private static readonly OpticsMeritFunctionSettings Spot = new()
    {
        Operands = new()
        {
            new() { Type = "REAY", Int1 = 3, Int2 = 1, Data = new List<double> { 0, 0, 0, 1 }, Target = 0, Weight = 1 },
            new() { Type = "REAY", Int1 = 3, Int2 = 1, Data = new List<double> { 0, 0, 0, 0.5 }, Target = 0, Weight = 1 },
        },
    };

    private static ToleranceScriptOptions Script(string text, string name = "test.tsc") => new()
    {
        Name = name, Text = text, MeritFunctions = new Dictionary<string, OpticsMeritFunctionSettings> { ["SPOT"] = Spot },
    };

    private static ToleranceRunOptions WithScript(string text, int? threads = 1, string name = "test.tsc") => new()
    {
        Criterion = ToleranceCriterion.UserScript, Fields = ToleranceFieldSet.UserDefined, Script = Script(text, name), MaxDegreeOfParallelism = threads,
    };

    [Fact]
    public void TheCommandTable_IsTheHelpsCommandList()
    {
        // "The Tolerance Script Commands" lists, after "!" (a comment): CEDV, CLEARCOMP, CLOSEFILE,
        // CMCO, CNPA, CNPS, COMP, CPAR, FORMAT, GETMERIT, HEADER, LOADMERIT, NOMINAL, OPENFILE,
        // OPTIMIZE, OPTIMIZE-OD, PERTURB, QUIET, REPORT (with REPORT_NB under its syntax 4), SAVE, UPDATE.
        var expected = new[]
        {
            "CEDV", "CLEARCOMP", "CLOSEFILE", "CMCO", "CNPA", "CNPS", "COMP", "CPAR", "FORMAT", "GETMERIT", "HEADER", "LOADMERIT",
            "NOMINAL", "OPENFILE", "OPTIMIZE", "OPTIMIZE-OD", "PERTURB", "QUIET", "REPORT", "REPORT_NB", "SAVE", "UPDATE",
        };
        Assert.Equal(expected, ToleranceScript.Commands.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "CEDV", "CNPA", "CNPS" }, ToleranceScript.Commands.Where(c => !c.Bound).Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Arguments_SplitAtBlanksOrAtCommas()
    {
        Assert.Equal(new[] { "3", "1" }, ToleranceScript.Items("3 1"));
        Assert.Equal(new[] { "3", "1" }, ToleranceScript.Items(" 3, 1 "));
        Assert.Equal(new[] { "\"MTF at field 1 = \"", "2" }, ToleranceScript.Items("\"MTF at field 1 = \" 2"));
        // The help's PERTURB example, and a negative limit, which the blank form keeps as one argument.
        Assert.Equal(new[] { "0", "6", "0", "0", "0", "3.0", "0.0", "1.0" }, ToleranceScript.Items("0 6 0 0 0 3.0 0.0 1.0"));
        Assert.Equal(new[] { "0", "6", "0", "0", "1", "0", "-1.0", "1.0" }, ToleranceScript.Items("0 6 0 0 1 0 -1.0 1.0"));
        Assert.Equal(new[] { "n + 1", "2" }, ToleranceScript.Items("n + 1, 2"));
        Assert.Equal(new[] { "SQRT(a, b)", "2" }, ToleranceScript.Items("SQRT(a, b) 2"));
        Assert.Empty(ToleranceScript.Items("   "));
    }

    [Fact]
    public void Check_NamesTheLineOfASyntaxError_ARefusedKeyword_AndACommandNotBuilt()
    {
        Assert.Empty(ToleranceScript.Check(Script("CLEARCOMP\nLOADMERIT SPOT\nCPAR 3 1\nOPTIMIZE-OD 2\nREPORT \"x\" 0\nOUTPUT SCREEN")));
        var syntax = Assert.Single(ToleranceScript.Check(Script("GETMERIT\nIF 1\nGETMERIT", "a.tsc")));
        Assert.Contains("a.tsc, line 2", syntax);
        Assert.Contains(ToleranceScript.Check(Script("GETMERIT\nSAVELENS X.ZMX", "b.tsc")), p => p.Contains("line 2") && p.Contains("SAVELENS"));
        Assert.Contains(ToleranceScript.Check(Script("CEDV 3 1", "c.tsc")), p => p.Contains("line 1") && p.Contains("CPAR"));
        Assert.Contains(ToleranceScript.Check(Script("GETMERIT\n\nOUTPUT \"x.txt\"", "d.tsc")), p => p.Contains("line 3") && p.Contains("OPENFILE"));
    }

    /// <summary>A script that only loads a merit function gives the merit function criterion's
    /// numbers bit for bit: both evaluate the same rows on the same lenses (an axial trial lens keeps
    /// its own aim, TC-15), and trials start from the same lens.</summary>
    [Fact]
    public void LoadMeritAlone_EqualsTheMeritFunctionCriterion()
    {
        var lens = X.Singlet() with { MeritFunction = Spot };
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TTHI", 1, 2, -0.2, 0.2));
        var merit = ToleranceAnalysis.Run(lens, table, X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.MeritFunction, Compensation = ToleranceCompensation.None, MaxDegreeOfParallelism = 1 });
        var script = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("LOADMERIT SPOT"));
        Assert.Equal(merit.Nominal, script.Nominal);
        Assert.Equal(merit.Rows.Select(r => (r.AtMin.Criterion, r.AtMax.Criterion)), script.Rows.Select(r => (r.AtMin.Criterion, r.AtMax.Criterion)));
        Assert.Empty(script.Compensators);
        Assert.All(script.Rows, r => Assert.NotNull(r.AtMax.Script));
        // GETMERIT on the lens's own merit function (renumbered by the tolerancing) is the same again.
        var own = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("GETMERIT"));
        Assert.Equal(merit.Rows.Select(r => r.AtMax.Criterion), own.Rows.Select(r => r.AtMax.Criterion));
    }

    /// <summary>"Runs without a script are unchanged": a script set with another criterion is not used.</summary>
    [Fact]
    public void AScriptWithAnotherCriterion_IsNotUsed()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1));
        var plain = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront);
        var withScript = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront with { Script = Script("THIS IS NOT ZPL") });
        Assert.Equal(plain.Nominal, withScript.Nominal);
        Assert.Equal(plain.Rows[0].AtMax.Criterion, withScript.Rows[0].AtMax.Criterion);
        Assert.Null(withScript.NominalScript);
        Assert.Null(withScript.Rows[0].AtMax.Script);
    }

    /// <summary>A script error fails the trial, naming the script and the line; the nominal (where
    /// PERTURB is ignored, help) still runs. A syntax error stops the run before it starts.</summary>
    [Fact]
    public void AScriptError_FailsTheTrialByNameAndLine()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1));
        var r = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("LOADMERIT SPOT\n! surface 99 does not exist\nPERTURB 0 99 0 0 1 0 0 0.1", name: "BAD.TSC"));
        Assert.True(double.IsFinite(r.Nominal));
        foreach (var t in new[] { r.Rows[0].AtMin, r.Rows[0].AtMax })
        {
            Assert.False(t.Succeeded);
            Assert.Contains("Tolerance script BAD.TSC, line 3:", t.Failure);
            Assert.Contains("surface 99", t.Failure);
        }

        var syntax = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("GETMERIT\nFOR i, 1, 2, 1", name: "S.TSC")));
        Assert.Contains("S.TSC, line 2", syntax.Message);

        var none = Assert.Throws<ToleranceScriptException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("CLEARCOMP")));
        Assert.Contains("returned no criterion", none.Message);

        var missing = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("") with { Script = null }));
        Assert.Contains("needs a tolerance script", missing.Message);
    }

    /// <summary>"If any compensators are defined in the script, then no compensators are allowed in
    /// the Tolerance Data Editor" (help); without script compensators the table's are the script's.</summary>
    [Fact]
    public void ScriptCompensators_AndTableCompensators_DoNotMix()
    {
        var lens = X.Singlet() with { MeritFunction = Spot };
        var table = X.Table(X.Row("COMP", 2, 0, -5, 5), X.Row("TRAD", 1, 0, -1, 1));
        var mixed = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("COMP 2 0\nLOADMERIT SPOT\nOPTIMIZE")));
        Assert.Contains("no compensators are allowed in the Tolerance Data Editor", mixed.Message);

        // The table's COMP drives the back distance under the script's OPTIMIZE: the marginal rays
        // are brought to the image, so the criterion falls below the uncompensated one.
        var compensated = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("LOADMERIT SPOT\nOPTIMIZE"));
        var plain = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, WithScript("LOADMERIT SPOT"));
        Assert.True(compensated.Rows[0].AtMax.Succeeded, compensated.Rows[0].AtMax.Failure);
        Assert.True(compensated.Rows[0].AtMax.Criterion < plain.Rows[0].AtMax.Criterion,
            $"{compensated.Rows[0].AtMax.Criterion:R} against {plain.Rows[0].AtMax.Criterion:R}");
    }

    /// <summary>PERTURB draws from the trial's own stream: Monte Carlo trials differ, the nominal is
    /// not perturbed, and one thread or many give the same bits.</summary>
    [Fact]
    public void Perturb_IsDeterministicPerTrial_AndIgnoredOnTheNominal()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -0.5, 0.5));
        const string text = "PERTURB 0 2 0 0 1 0 -0.3 0.3\nUPDATE\nLOADMERIT SPOT";
        var mc = new MonteCarloOptions { Trials = 4, Seed = 3 };
        var one = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, WithScript(text, 1), mc);
        var many = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, WithScript(text, null), mc);
        Assert.Equal(one.Trials.Select(t => t.Criterion), many.Trials.Select(t => t.Criterion));
        Assert.All(one.Trials, t => Assert.True(t.Succeeded, t.Failure));
        Assert.Equal(4, one.Trials.Select(t => t.Criterion).Distinct().Count());
        var unperturbed = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, WithScript("LOADMERIT SPOT", 1), mc);
        Assert.Equal(unperturbed.Nominal, one.Nominal);
        // The script's draws do not touch the rows' draws (stream −k against stream k).
        Assert.Equal(unperturbed.Trials.Select(t => t.Draws[0].Value), one.Trials.Select(t => t.Draws[0].Value));
    }

    /// <summary>REPORT, HEADER, OPENFILE and QUIET: the window text, the reports, and a file written
    /// once the run is done, the nominal first and then the trials in order, the same at any thread count.</summary>
    [Fact]
    public void ReportsAndFiles_FollowTheHelp_AndDoNotDependOnThreads()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -0.5, 0.5));
        const string text = """
            LOADMERIT SPOT
            REPORT "merit = " 0
            REPORT_NB "row 1 = " 1
            REPORT "" -1
            OPENFILE "C:\out\data.txt" TXT
            HEADER "trial"
            QUIET ON
            REPORT "not shown " 2
            CLOSEFILE
            """;
        Dictionary<string, byte[]> Files(int? threads, out MonteCarloResult result)
        {
            var written = new Dictionary<string, byte[]>();
            var o = WithScript(text, threads);
            o = o with { Script = o.Script! with { WriteFile = (p, b) => written[p] = b } };
            result = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, o, new MonteCarloOptions { Trials = 3, Seed = 5 });
            return written;
        }
        var a = Files(1, out var r);
        var b = Files(null, out _);
        var path = Assert.Single(a.Keys);
        Assert.Equal(a[path], b[path]);
        Assert.Equal(new[] { path }, r.ScriptFiles);

        var lines = Encoding.UTF8.GetString(a[path]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Per run: the HEADER line (text, then "Nominal" or the trial number) and REPORT 2's number
        // (to a file only the number, help, REPORT).
        Assert.Equal(8, lines.Length);
        Assert.Equal("trial Nominal", lines[0]);
        Assert.Equal("trial 1", lines[2]);
        Assert.Equal("trial 3", lines[6]);

        var t = r.Trials[0];
        Assert.Equal(4, t.Script!.Reports.Count);
        Assert.Equal(t.Criterion, t.Script.Reports[0].Value);   // REPORT 0 is the merit function, the criterion
        Assert.Null(t.Script.Reports[2].Value);
        Assert.Contains("merit = ", t.Script.Text);
        Assert.Contains("row 1 = ", t.Script.Text);
        Assert.DoesNotContain("not shown", t.Script.Text);   // QUIET ON
        // FORMAT's default in a script is 16.8 EXP (help, FORMAT).
        Assert.Contains("merit = " + t.Criterion.ToString("E8", System.Globalization.CultureInfo.InvariantCulture).PadLeft(16), t.Script.Text);
        Assert.Contains(r.ReportLines(), l => l.Contains("Script output:"));
    }

    [Fact]
    public void OpenFile_IsRefusedInTheInverseModes_AsTheHelpSays()
    {
        var r = ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs,
            WithScript("LOADMERIT SPOT\nGETMERIT", name: "I.TSC") with { Mode = ToleranceMode.InverseIncrement, Increment = 1 });
        Assert.True(double.IsFinite(r.Nominal));
        var ex = Assert.Throws<ToleranceScriptException>(() => ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs,
            WithScript("LOADMERIT SPOT\nOPENFILE \"C:\\x.txt\" TXT", name: "I.TSC") with { Mode = ToleranceMode.InverseIncrement, Increment = 1 }));
        Assert.Contains("line 2", ex.Message);
        Assert.Contains("inverse sensitivity", ex.Message);
    }

    [Fact]
    public void QuickYield_RefusesTheUserScript()
    {
        var ex = Assert.Throws<ArgumentException>(() => QuickYield.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs, WithScript("LOADMERIT SPOT")));
        Assert.Contains("user script", ex.Message);
    }
}
