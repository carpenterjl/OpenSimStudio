using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.Tests.Plotting;

/// <summary>The plot text export (OP-7): what is written reads back as the same doubles, bit for bit.</summary>
public class PlotCsvTests
{
    private static readonly double[] Awkward =
        { 1.0 / 3, -2.5e-300, 6.02214076e23, 0.1 + 0.2, double.Epsilon, -0.0, double.NaN, double.PositiveInfinity, 42 };

    /// <summary>The same double bit for bit (so −0 stays −0); any NaN matches any NaN, since NaN
    /// payloads are not something a text format carries.</summary>
    private static void AssertSame(double expected, double actual)
    {
        if (double.IsNaN(expected)) { Assert.True(double.IsNaN(actual)); return; }
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }

    [Fact]
    public void Points_RoundTripBitForBit_WithAwkwardNamesAndValues()
    {
        var a = Awkward.Select((v, i) => new Point2(v, i * 0.7)).ToArray();
        var b = new[] { new Point2(1e-9, -1e9) };
        string csv = PlotCsv.Points("x [mm]", "y, mm", new (string, IReadOnlyList<Point2>)[]
        {
            ("λ = 0.5876 µm, \"d\"", a),
            ("#2", b)
        }, new[] { "unit: mm", "two\nlines" });

        var back = PlotCsv.ReadPoints(csv);
        Assert.Equal(a.Length + b.Length, back.Count);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal("λ = 0.5876 µm, \"d\"", back[i].Series);
            AssertSame(a[i].X, back[i].X);
            AssertSame(a[i].Y, back[i].Y);
        }
        Assert.Equal("#2", back[^1].Series);
        Assert.Equal(new CsvPoint("#2", 1e-9, -1e9), back[^1]);

        var (header, _) = PlotCsv.ReadTable(csv);
        Assert.Equal(new[] { "series", "x [mm]", "y, mm" }, header);
        Assert.StartsWith("# unit: mm\r\n# two lines\r\nseries,x [mm],\"y, mm\"\r\n", csv);
    }

    [Fact]
    public void Grid_RoundTrips()
    {
        var field = GridField.FromFunction((x, y) => Math.Sin(x) * Math.Exp(y) / 3, 4, 6, -1.25, 3.5, 0.1, 0.7);
        field.Values[2, 3] = double.NaN;
        var back = PlotCsv.ReadGrid(PlotCsv.Grid(field, comments: new[] { "unit: waves" }));
        Assert.Equal(field.Rows, back.Rows);
        Assert.Equal(field.Columns, back.Columns);
        Assert.Equal(field.X0, back.X0); Assert.Equal(field.X1, back.X1);
        Assert.Equal(field.Y0, back.Y0); Assert.Equal(field.Y1, back.Y1);
        for (int r = 0; r < field.Rows; r++)
            for (int c = 0; c < field.Columns; c++)
                AssertSame(field.Values[r, c], back.Values[r, c]);
    }

    [Fact]
    public void Sections_SplitBackIntoTheirNamedTables()
    {
        string one = PlotCsv.Points("x", "y", new (string, IReadOnlyList<Point2>)[] { ("a", new[] { new Point2(1, 2) }) });
        string two = PlotCsv.Points("x", "y", new (string, IReadOnlyList<Point2>)[] { ("b", new[] { new Point2(3, 4), new Point2(5, 6) }) },
            new[] { "unit: um" });
        var sections = PlotCsv.SplitSections(PlotCsv.Sections(new[] { ("field 1: points", one), ("field 2: points", two) }));
        Assert.Equal(2, sections.Count);
        Assert.Equal("field 1: points", sections[0].Name);
        Assert.Equal("field 2: points", sections[1].Name);
        Assert.Equal(new CsvPoint("a", 1, 2), PlotCsv.ReadPoints(sections[0].Csv).Single());
        Assert.Equal(new[] { new CsvPoint("b", 3, 4), new CsvPoint("b", 5, 6) }, PlotCsv.ReadPoints(sections[1].Csv));
    }

    [Fact]
    public void ReadTable_HandlesQuotedLineBreaks_AndFailsOnAnOpenQuote()
    {
        var (header, rows) = PlotCsv.ReadTable("a,b\r\n\"one\r\ntwo\",3\n\"x\"\"y\",4");
        Assert.Equal(new[] { "a", "b" }, header);
        Assert.Equal(new[] { "one\r\ntwo", "3" }, rows[0]);
        Assert.Equal(new[] { "x\"y", "4" }, rows[1]);
        var ex = Assert.Throws<FormatException>(() => PlotCsv.ReadTable("a\r\n\"open"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Parse_NamesTheBadRow() =>
        Assert.Contains("row 3", Assert.Throws<FormatException>(() => PlotCsv.ReadPoints("s,x,y\r\na,1,2\r\na,1,2\r\na,one,2")).Message);
}
