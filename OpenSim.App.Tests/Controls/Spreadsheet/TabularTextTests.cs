using OpenSim.App.Controls.Spreadsheet;

namespace OpenSim.App.Tests.Controls.Spreadsheet;

/// <summary>Clipboard text in and out: tab-separated, quoted as RFC 4180 section 2 quotes CSV.</summary>
public sealed class TabularTextTests
{
    [Fact]
    public void Records_SplitOnAnyLineBreak_AndAFinalBreakAddsNoRecord()
    {
        var crlf = TabularText.Parse("a\tb\r\nc\td\r\n");
        var lf = TabularText.Parse("a\tb\nc\td");
        var cr = TabularText.Parse("a\tb\rc\td\r");

        foreach (var parsed in new[] { crlf, lf, cr })
        {
            Assert.Equal(2, parsed.Count);
            Assert.Equal(new[] { "a", "b" }, parsed[0]);
            Assert.Equal(new[] { "c", "d" }, parsed[1]);
        }
    }

    [Fact]
    public void EmptyFields_AreKept_AndEmptyTextHasNoRecords()
    {
        Assert.Empty(TabularText.Parse(""));
        Assert.Equal(new[] { "", "x", "" }, TabularText.Parse("\tx\t")[0]);
        Assert.Equal(new[] { "" }, Assert.Single(TabularText.Parse("\r\n")));
    }

    [Fact]
    public void QuotedFields_HoldTabsLineBreaksAndDoubledQuotes()
    {
        var parsed = TabularText.Parse("\"two\r\nlines\"\t\"a \"\"quote\"\"\"\t\"tab\there\"\r\nnext");

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { "two\r\nlines", "a \"quote\"", "tab\there" }, parsed[0]);
        Assert.Equal(new[] { "next" }, parsed[1]);
    }

    [Fact]
    public void AQuoteInsideAField_IsAnOrdinaryCharacter()
    {
        Assert.Equal(new[] { "12\" lens", "x" }, TabularText.Parse("12\" lens\tx")[0]);
    }

    [Fact]
    public void Format_QuotesOnlyWhatNeedsIt_AndRoundTrips()
    {
        var records = new IReadOnlyList<string>[]
        {
            new[] { "1.5", "N-BK7", "" },
            new[] { "say \"hi\"", "a\tb", "two\nlines" },
        };

        string text = TabularText.Format(records);

        Assert.Equal("1.5\tN-BK7\t\r\n\"say \"\"hi\"\"\"\t\"a\tb\"\t\"two\nlines\"", text);
        var back = TabularText.Parse(text);
        Assert.Equal(records[0], back[0]);
        Assert.Equal(records[1], back[1]);
    }

    [Fact]
    public void ABlock_PadsShortRecords()
    {
        var block = SheetBlock.Parse("1\t2\t3\r\n4");
        Assert.Equal(2, block.RowCount);
        Assert.Equal(3, block.ColumnCount);
        Assert.Equal("", block.TextAt(1, 2));
        Assert.Null(block.FlagAt(0, 0));
    }
}
