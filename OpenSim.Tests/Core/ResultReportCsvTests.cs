using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Tests.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for the result CSV export. The determinism gate is the load-bearing one: the file
/// deliberately carries NO timestamp, so two exports of the same solve are byte-identical
/// and a report can be regenerated and diffed.
/// </summary>
public class ResultReportCsvTests
{
    private static FeMesh Mesh() => StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, 2, 2, 2);

    private static Material Steel() => new()
    {
        Name = "Structural steel", YoungsModulus = 2e11, PoissonRatio = 0.3, Density = 7850,
        YieldStrength = 250e6, UltimateTensileStrength = 460e6
    };

    private static ResultReportContext Context() => new()
    {
        ProjectName = "Beam study",
        BodyName = "Beam",
        Analysis = "Static structural",
        Material = Steel(),
        BoundaryConditions = new BoundaryCondition[]
        {
            new FixedSupport { Name = "Supports", FaceIds = Array.Empty<int>(), EdgeIds = new[] { 0, 1 } }
        }
    };

    private static IReadOnlyList<IResultField> Fields(FeMesh mesh)
    {
        var values = new double[mesh.NodeCount];
        for (int i = 0; i < values.Length; i++) values[i] = i;
        return new IResultField[]
        {
            new NodalScalarField("Stress (von Mises)", "Pa", values),
            new ElementScalarField("Density", "kg/m3", Enumerable.Repeat(7850.0, mesh.ElementCount).ToArray())
        };
    }

    [Fact]
    public void Summary_CarriesThePreambleOnce_AndOneRowPerField()
    {
        var mesh = Mesh();
        string csv = ResultReportCsv.WriteSummary(Fields(mesh), mesh, Context());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(lines, l => l.StartsWith("# project: Beam study"));
        Assert.Contains(lines, l => l.StartsWith("# body: Beam"));
        Assert.Contains(lines, l => l.StartsWith("# analysis: Static structural"));
        Assert.Contains(lines, l => l.Contains("material: Structural steel"));
        Assert.Contains(lines, l => l.Contains("yield strength"));
        // The scope of every condition is stated, so an edge-supported run is self-describing.
        Assert.Contains(lines, l => l.Contains("Supports") && l.Contains("2 edge(s)"));
        Assert.Contains(lines, l => l.Contains("2 nodes") is false && l.StartsWith("# mesh:"));

        // Header plus exactly two data rows.
        int header = Array.FindIndex(lines, l => l.StartsWith("Field,Unit,"));
        Assert.True(header >= 0);
        Assert.Equal(2, lines.Length - header - 1);
        Assert.StartsWith("Stress (von Mises)", lines[header + 1]);
        Assert.Contains(",node,", lines[header + 1]);
        Assert.Contains(",element,", lines[header + 2]);
    }

    /// <summary>
    /// No timestamp anywhere, so the export is a pure function of the solve. This is what
    /// lets a generated report be regenerated and compared.
    /// </summary>
    [Fact]
    public void Export_IsDeterministic_ByteForByte()
    {
        var mesh = Mesh();
        string a = ResultReportCsv.WriteSummary(Fields(mesh), mesh, Context());
        string b = ResultReportCsv.WriteSummary(Fields(mesh), mesh, Context());

        Assert.Equal(a, b);
        Assert.DoesNotContain("20", a.Split("\r\n")[0]);   // no date stamped into the title line
    }

    [Fact]
    public void FieldExport_HasOneRowPerEntity_WithItsPosition()
    {
        var mesh = Mesh();
        var field = Fields(mesh)[0];
        var lines = ResultReportCsv.WriteField(field, mesh, Context())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        int header = Array.FindIndex(lines, l => l.StartsWith("Node,X (m)"));
        Assert.True(header >= 0);
        Assert.Equal(field.Count, lines.Length - header - 1);

        // Node 0 of this box sits at the origin and carries value 0.
        var first = lines[header + 1].Split(',');
        Assert.Equal("0", first[0]);
        Assert.Equal(0.0, double.Parse(first[1]), 12);
        Assert.Equal(0.0, double.Parse(first[4]), 12);
    }

    [Fact]
    public void ElementFieldExport_ReportsCentroids()
    {
        var mesh = Mesh();
        var lines = ResultReportCsv.WriteField(Fields(mesh)[1], mesh, Context())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        int header = Array.FindIndex(lines, l => l.StartsWith("Element,Centroid X"));
        Assert.True(header >= 0);
        Assert.Equal(mesh.ElementCount, lines.Length - header - 1);
    }

    [Fact]
    public void FieldNamesWithCommas_AreQuoted()
    {
        var mesh = Mesh();
        var field = new NodalScalarField("Stress, von Mises", "Pa", new double[mesh.NodeCount]);
        string csv = ResultReportCsv.WriteSummary(new IResultField[] { field }, mesh, Context());

        Assert.Contains("\"Stress, von Mises\"", csv);
    }

    [Fact]
    public void Values_RoundTripThroughTheirText()
    {
        var mesh = Mesh();
        var values = new double[mesh.NodeCount];
        values[1] = 1.0 / 3.0;
        values[2] = 2.7044e10;
        var lines = ResultReportCsv.WriteField(new NodalScalarField("V", "Pa", values), mesh, Context())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int header = Array.FindIndex(lines, l => l.StartsWith("Node,X (m)"));

        Assert.Equal(1.0 / 3.0, double.Parse(lines[header + 2].Split(',')[4]));
        Assert.Equal(2.7044e10, double.Parse(lines[header + 3].Split(',')[4]));
    }
}
