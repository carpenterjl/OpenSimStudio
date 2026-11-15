using OpenSim.Core.Model;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-99: the ZPL language core against programs evaluated by hand (the expected value of each is
/// worked out in its comment), and the language's errors, each named by line. The language is
/// OpticStudio's help, "About the ZPL" chapter (saved under plans/sources/optics/op-99-help/).
/// Numbers print in the default format, four decimals (<c>FORMAT .4</c>; ZplFormatSpec.Default
/// says why).
/// </summary>
public sealed class ZplLanguageTests
{
    private static readonly string NL = Environment.NewLine;

    internal static string Out(string macro, ZplRunOptions? options = null) => ZplMacro.Run(macro, options).Output;

    internal static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + Environment.NewLine));

    // ---------------------------------------------------------------- Expressions

    [Fact]
    public void Arithmetic_FollowsTheUsualPrecedence()
    {
        // 2 + 3·4 − 6/2 = 2 + 12 − 3 = 11; −2·−3 = 6; (1 + 2)·3 = 9; 7 − 2 − 1 = 4 (left to right);
        // 8/4/2 = 1 (left to right); −(2 + 3) = −5.
        Assert.Equal(Lines("11.0000", "6.0000", "9.0000", "4.0000", "1.0000", "-5.0000"),
            Out("PRINT 2 + 3 * 4 - 6 / 2\nPRINT -2 * -3\nPRINT (1 + 2) * 3\nPRINT 7 - 2 - 1\nPRINT 8 / 4 / 2\nPRINT -(2 + 3)"));
    }

    [Fact]
    public void LogicalOperators_GiveOneOrZero_AsTheHelpsTableDefinesThem()
    {
        // (3 > 2) & (2 > 3) = 1 & 0 = 0; (3 > 2) | (2 > 3) = 1; 1 ^ 1 = 0; 1 ^ 0 = 1; !0 = 1; !5 = 0;
        // 2 == 2 is 1; 5 != 5 is 0; 2 >= 2 is 1; 3 <= 2 is 0; 2 < 3 & 4 > 1 = 1 & 1 = 1 (comparisons
        // bind tighter than &, this program's reading).
        Assert.Equal(Lines("0", "1", "0", "1", "1", "0", "1", "0", "1", "0", "1"),
            Out("FORMAT .0\nPRINT (3 > 2) & (2 > 3)\nPRINT (3 > 2) | (2 > 3)\nPRINT 1 ^ 1\nPRINT 1 ^ 0\nPRINT !0\nPRINT !5\n"
                + "PRINT 2 == 2\nPRINT 5 != 5\nPRINT 2 >= 2\nPRINT 3 <= 2\nPRINT 2 < 3 & 4 > 1"));
    }

    [Fact]
    public void StringOperators_ConcatenateAndCompare()
    {
        // "ab" + "cd" = "abcd"; "abc" $== "abc" is 1; "abc" $< "abd" is 1; "b" $> "a" is 1;
        // "a" $!= "a" is 0; "a" $>= "b" is 0; "a" $<= "a" is 1.
        Assert.Equal(Lines("abcd", "1", "1", "1", "0", "0", "1"),
            Out("A$ = \"ab\"\nB$ = A$ + \"cd\"\nPRINT B$\nFORMAT .0\nPRINT \"abc\" $== \"abc\"\nPRINT \"abc\" $< \"abd\"\nPRINT \"b\" $> \"a\"\n"
                + "PRINT \"a\" $!= \"a\"\nPRINT \"a\" $>= \"b\"\nPRINT \"a\" $<= \"a\""));
    }

    [Fact]
    public void Names_AreNotCaseSensitive_AndStringAndNumberNamesAreSeparate()
    {
        // x and X are one variable ("ZPL is not case-sensitive", Numeric Variables); x$ is another.
        Assert.Equal(Lines("3.0000", "three"), Out("x = 1\nX = x + 2\nx$ = \"three\"\nprint X\nPrInT x$"));
    }

    [Fact]
    public void Comments_RemBangAndHash_AreIgnored()
    {
        Assert.Equal(Lines("1.0000"), Out("REM a remark\n! another\nx = 1 # the rest is ignored\nPRINT x # \"#\" in a comment\n   ! indented"));
        // A # inside a string is text.
        Assert.Equal(Lines("a#b"), Out("PRINT \"a#b\""));
    }

    // ---------------------------------------------------------------- Control flow

    [Fact]
    public void ForLoops_FollowTheHelpsRules()
    {
        // Σ 1..10 = 55; 5·4·3·2·1 = 120 counting down; start = stop runs once (k = 1); the help's
        // example FOR i, j, j + 5, 2 with j = 5: i = 5, 7, 9 (11 > 10 stops), k = 10 + 12 + 14 = 36;
        // nested 3 × 4 = 12 passes; "FOR i = 1, 3, 1" (the help's other spelling) sums 6.
        Assert.Equal(Lines("55.0000", "120.0000", "1.0000", "36.0000", "12.0000", "6.0000"),
            Out("s = 0\nFOR i, 1, 10, 1\ns = s + i\nNEXT\nPRINT s\n"
                + "p = 1\nFOR i, 5, 1, -1\np = p * i\nNEXT i\nPRINT p\n"
                + "k = 0\nFOR i, 2, 2, 1\nk = k + 1\nNEXT\nPRINT k\n"
                + "j = 5\nk = 0\nFOR i, j, j + 5, 2\nk = i + j + k\nNEXT\nPRINT k\n"
                + "c = 0\nFOR a, 1, 3, 1\nFOR b, 1, 4, 1\nc = c + 1\nNEXT b\nNEXT a\nPRINT c\n"
                + "s = 0\nFOR i = 1, 3, 1\ns = s + i\nNEXT\nPRINT s"));
    }

    [Fact]
    public void ForLoop_StopAndIncrement_AreEvaluatedOnce()
    {
        // n changes inside the loop, but the stop value 3 was saved: three passes.
        Assert.Equal(Lines("3.0000"), Out("n = 3\nc = 0\nFOR i, 1, n, 1\nn = 100\nc = c + 1\nNEXT\nPRINT c"));
    }

    [Fact]
    public void IfBlocks_ElseAndThen_ChooseOneBranch()
    {
        Assert.Equal(Lines("x is less than y", "inner", "z"),
            Out("x = 1\ny = 2\nif (x < y)\nPRINT \"x is less than y\"\nIF x == 1\nPRINT \"inner\"\nENDIF\nELSE\nPRINT \"never\"\nENDIF\n"
                + "IF x > y THEN PRINT \"no\"\nIF !(x > y) THEN PRINT \"z\""));
    }

    [Fact]
    public void GotoAndLabels_LoopByNumberAndByWord()
    {
        // i counts 1, 2, …, 5 through LABEL 1; then a word label skips a line.
        Assert.Equal(Lines("5.0000", "after"),
            Out("i = 0\nLABEL 1\ni = i + 1\nIF i < 5 THEN GOTO 1\nPRINT i\nGOTO skip\nPRINT \"skipped\"\nLABEL skip\nPRINT \"after\""));
    }

    [Fact]
    public void Gosub_ReturnsToTheLineAfterTheCall_AndNests()
    {
        // add sets z = x + y = 3 and calls twice, which doubles z: 6.
        Assert.Equal(Lines("the sum of 1.0000 and 2.0000 is 3.0000", "6.0000"),
            Out("x = 1\ny = 2\nGOSUB add\nPRINT \"the sum of \", x, \" and \", y, \" is \", z\nGOSUB both\nPRINT z\nEND\n"
                + "SUB add\nz = x + y\nRETURN\nSUB both\nGOSUB add\nGOSUB twice\nRETURN\nSUB twice\nz = 2 * z\nRETURN"));
    }

    [Fact]
    public void End_StopsTheMacro()
    {
        Assert.Equal(Lines("a"), Out("PRINT \"a\"\nEND\nPRINT \"b\""));
    }

    // ---------------------------------------------------------------- Arrays and vectors

    [Fact]
    public void Arrays_StartAtOne_AndHoldIntegersWhenDeclaredSo()
    {
        // Σ over i, j = 1..5 of (i + j) = 5·15 + 5·15 = 150. INTEGER keeps 2.7 as 2 and −2.7 as −2
        // (truncation, as C converts).
        Assert.Equal(Lines("150.0000", "2.0000", "-2.0000"),
            Out("DECLARE Z, DOUBLE, 2, 5, 5\nFOR i, 1, 5, 1\nFOR j, 1, 5, 1\nZ(i, j) = i + j\nNEXT\nNEXT\n"
                + "s = 0\nFOR i, 1, 5, 1\nFOR j, 1, 5, 1\ns = s + Z(i,j)\nNEXT\nNEXT\nPRINT s\nRELEASE Z\n"
                + "DECLARE K, INTEGER, 1, 2\nK(1) = 2.7\nK(2) = -2.7\nPRINT K(1)\nPRINT K(2)"));
    }

    [Fact]
    public void Arrays_OutOfRange_AndAfterRelease_AreErrors()
    {
        var e = Assert.Throws<ZplException>(() => Out("DECLARE A, DOUBLE, 1, 3\nA(4) = 1"));
        Assert.Equal(2, e.Line);
        Assert.Contains("from 1 to 3", e.Message);
        e = Assert.Throws<ZplException>(() => Out("DECLARE A, DOUBLE, 1, 3\nRELEASE A\nx = A(1)"));
        Assert.Equal(3, e.Line);
    }

    // ---------------------------------------------------------------- Output and FORMAT

    [Fact]
    public void Format_ReproducesTheHelpsExample()
    {
        // Help, FORMAT, example: X = 5 printed as 5.000 (5.3), 5.00E+000 (12.2 EXP), 5 (6 INT) and
        // 000005 ("%#06i" LIT). The help's page collapses leading blanks; the widths are its
        // definition: 12 characters for 12.2 EXP (three blanks before the nine of 5.00E+000) and
        // 6 for 6 INT (five blanks).
        Assert.Equal(Lines("A:5.000", "B:   5.00E+000", "C:     5", "D:000005"),
            Out("X = 5\nFORMAT 5.3\nPRINT \"A:\", X\nFORMAT 12.2 EXP\nPRINT \"B:\", X\nFORMAT 6 INT\nPRINT \"C:\", X\nFORMAT \"%#06i\" LIT\nPRINT \"D:\", X"));
    }

    [Fact]
    public void Format_WidthIsIgnoredWhenTheNumberDoesNotFit_AndCFormatsFollowPrintf()
    {
        // 12345.678 does not fit 4 places: "the m portion … will be ignored". %8.3f of −1.5 is
        // "  -1.500"; %-6d of 42 is "42    "; %+.2e of 1234.5 is +1.23e+03 (two exponent digits, C);
        // %g of 0.0001 is 0.0001 and of 1e-5 is 1e-05; %x of 255 is ff; 50%% is a percent sign.
        Assert.Equal(Lines("12345.68", "[  -1.500]", "[42    ]", "+1.23e+03", "0.0001", "1e-05", "ff", "50%"),
            Out("FORMAT 4.2\nPRINT 12345.678\nFORMAT \"[%8.3f]\" LIT\nPRINT -1.5\nFORMAT \"[%-6d]\" LIT\nPRINT 42\n"
                + "FORMAT \"%+.2e\" LIT\nPRINT 1234.5\nFORMAT \"%g\" LIT\nPRINT 0.0001\nPRINT 0.00001\nFORMAT \"%x\" LIT\nPRINT 255\n"
                + "FORMAT \"%.0f%%\" LIT\nPRINT 50"));
    }

    [Fact]
    public void Print_TrailingCommaJoinsLines_AndItemsMayStandSideBySide()
    {
        // A trailing comma keeps the line open (help, PRINT); PRINT alone is a blank line; the
        // help's own PRINT "Surface " $STR(n) has no comma between its items.
        Assert.Equal("ab" + NL + NL + "Surface 2" + NL,
            Out("PRINT \"a\",\nPRINT \"b\"\nPRINT\nn = 2\nFORMAT .0\nPRINT \"Surface \" $STR(n)"));
    }

    [Fact]
    public void Rewind_ErasesTheLastLine()
    {
        // Help, REWIND: "erases the last line printed by the PRINT command".
        Assert.Equal(Lines("keep", "New First line"), Out("PRINT \"keep\"\nPRINT \"First line\"\nREWIND\nPRINT \"New First line\""));
    }

    // ---------------------------------------------------------------- String functions

    [Fact]
    public void StringFunctions_ExtractPadAndConvert()
    {
        // $GETSTRING("one two three", 2) = two; $GETSTRINGC("a, b,c", 2) = b (trimmed);
        // $LEFTSTRING("abc", 5) = "abc  "; $RIGHTSTRING("abcdef", 2) = ef; SLEN = 13; SVAL("2.5")·2 = 5;
        // $STR(1/4) at FORMAT .2 = 0.25; SCOM("a", "b") = −1; past the last word is empty.
        Assert.Equal(Lines("two", "b", "[abc  ]", "ef", "13.0000", "5.0000", "0.25", "-1.0000", "[]", "\"q\"", "a\tb"),
            Out("A$ = \"one two three\"\nPRINT $GETSTRING(A$, 2)\nC$ = \"a, b,c\"\nPRINT $GETSTRINGC(C$, 2)\nB$ = \"abc\"\n"
                + "L$ = \"[\" + $LEFTSTRING(B$, 5) + \"]\"\nPRINT L$\nD$ = \"abcdef\"\nPRINT $RIGHTSTRING(D$, 2)\nPRINT SLEN(A$)\n"
                + "V$ = \"2.5\"\nPRINT SVAL(V$) * 2\nFORMAT .2\nS$ = $STR(1/4)\nPRINT S$\nFORMAT .4\nE$ = \"a\"\nF$ = \"b\"\nPRINT SCOM(E$, F$)\n"
                + "G$ = \"[\" + $GETSTRING(A$, 9) + \"]\"\nPRINT G$\nQ$ = $QUOTE() + \"q\" + $QUOTE()\nPRINT Q$\nT$ = \"a\" + $TAB() + \"b\"\nPRINT T$"));
    }

    [Fact]
    public void MathFunctions_GiveTheirDefinitions()
    {
        // OPTRETURN keeps each value exactly; the expected values are the definitions in the help's
        // function table evaluated with System.Math.
        var r = ZplMacro.Run("OPTRETURN 0, ABSO(-2)\nOPTRETURN 1, 2*ACOS(0)\nOPTRETURN 2, ASIN(1)\nOPTRETURN 3, ATAN(1)\nOPTRETURN 4, COSI(0)\n"
            + "OPTRETURN 5, SINE(0)\nOPTRETURN 6, TANG(0)\nOPTRETURN 7, EXPE(1)\nOPTRETURN 8, EXPT(2)\nOPTRETURN 9, LOGE(EXPE(2))\n"
            + "OPTRETURN 10, LOGT(1000)\nOPTRETURN 11, SQRT(16)\nOPTRETURN 12, INTE(-2.5)\nOPTRETURN 13, INTE(2.5)\nOPTRETURN 14, SIGN(-3)\n"
            + "OPTRETURN 15, MAGN(3, 4)\nOPTRETURN 16, POWR(-2, 3)\nA$ = \"7\"\nOPTRETURN 17, LVAL(A$)");
        double?[] expected = { 2, Math.PI, Math.PI / 2, Math.Atan(1), 1, 0, 0, Math.E, 100, Math.Log(Math.Exp(2)), Math.Log10(1000), 4, -3, 2, -1, 5, 8, 7 };
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], r.OptReturn[i]);
    }

    [Fact]
    public void FunctionsOutsideTheirDomain_Fail()
    {
        Assert.Contains("SQRT", Assert.Throws<ZplException>(() => Out("x = SQRT(-1)")).Message);
        Assert.Contains("LOGE", Assert.Throws<ZplException>(() => Out("x = LOGE(0)")).Message);
        Assert.Contains("ACOS", Assert.Throws<ZplException>(() => Out("x = ACOS(2)")).Message);
        Assert.Contains("division by zero", Assert.Throws<ZplException>(() => Out("x = 1 / 0")).Message);
        Assert.Contains("not a finite number", Assert.Throws<ZplException>(() => Out("x = EXPE(1000)")).Message);
    }

    // ---------------------------------------------------------------- Errors, by line

    [Theory]
    [InlineData("x = 1\nIF x\nPRINT x", 2, "IF has no ENDIF")]
    [InlineData("FOR i, 1, 3, 1\nPRINT i", 1, "has no NEXT")]
    [InlineData("FOR i, 1, 3, 1\nNEXT j", 2, "closes the FOR I")]
    [InlineData("x = 1\nGOTO 5", 2, "no LABEL 5")]
    [InlineData("GOSUB nowhere", 1, "no SUB NOWHERE")]
    [InlineData("x = 1\nELSE", 2, "ELSE without an IF")]
    [InlineData("x = 1\nNOTAKEYWORD 3", 2, "not a ZPL keyword")]
    [InlineData("THIC = 3", 1, "name of a ZPL keyword or function")]
    [InlineData("x = (1 + 2", 1, "')' expected")]
    [InlineData("x = 1 = 2", 1, "compare with '=='")]
    [InlineData("LABEL 1\nLABEL 1", 2, "defined twice")]
    [InlineData("FOR i, 1, 3\nNEXT", 1, "increment is missing")]
    [InlineData("A$ = \"abc", 1, "not closed")]
    public void ParseErrors_NameTheLine(string macro, int line, string text)
    {
        var e = Assert.Throws<ZplException>(() => ZplMacro.Parse(macro, "test.zpl"));
        Assert.Equal(line, e.Line);
        Assert.Contains(text, e.Message);
        Assert.StartsWith("test.zpl, line " + line, e.Message);
    }

    [Theory]
    [InlineData("x = 1\nPRINT y", 2, "used before a value")]
    [InlineData("x = 1\nRETURN", 2, "RETURN without a GOSUB")]
    [InlineData("x = 1\nSUB s\nRETURN", 2, "without a GOSUB")]
    [InlineData("GOSUB s\nEND\nSUB s\nx = 1", 4, "terminated by a RETURN")]
    [InlineData("x$ = 3", 1, "string variable")]
    [InlineData("x = \"a\"", 1, "numeric variable")]
    [InlineData("x = 1 + \"a\"", 1, "cannot add a number to a string")]
    [InlineData("FOR i, 1, 3, -1\nNEXT", 1, "would not end")]
    [InlineData("x = NOSUCH(1)", 1, "neither a ZPL function")]
    [InlineData("x = SQRT(1, 2)", 1, "takes 1 argument")]
    public void RunErrors_NameTheLine(string macro, int line, string text)
    {
        var e = Assert.Throws<ZplException>(() => Out(macro));
        Assert.Equal(line, e.Line);
        Assert.Contains(text, e.Message);
    }

    [Fact]
    public void AKeywordNotBound_FailsByNameAndLine_WhenReached()
    {
        // Not reached: the macro runs. Reached: it fails with the name and the line.
        Assert.Equal(Lines("ok"), Out("x = 0\nIF x THEN BEEP\nPRINT \"ok\""));
        var e = Assert.Throws<ZplNotBoundException>(() => Out("x = 1\nIF x THEN BEEP"));
        Assert.Equal("BEEP", e.Name);
        Assert.Equal(2, e.Line);
        Assert.Contains("BEEP is not bound", e.Message);
        var f = Assert.Throws<ZplNotBoundException>(() => Out("x = 1\n\ny = GNUM(\"BK7\")"));
        Assert.Equal("GNUM", f.Name);
        Assert.Equal(3, f.Line);
        // Parse lists them both before a run.
        var names = ZplMacro.Parse("BEEP\ny = GNUM(\"BK7\")\nx = SQRT(4)").NotBound();
        Assert.Equal(new[] { ("BEEP", 1), ("GNUM", 2) }, names);
    }

    [Fact]
    public void AStringVariable_HoldsAtMost360Characters()
    {
        // "String variables can hold a maximum of 360 characters" (String Variables).
        Out("A$ = $LEFTSTRING(\"x\", 360)");
        Assert.Contains("360", Assert.Throws<ZplException>(() => Out("A$ = $LEFTSTRING(\"x\", 361)")).Message);
    }

    [Fact]
    public void ARun_IsCancelled_ByItsToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ZplMacro.Run("LABEL 1\nGOTO 1", new ZplRunOptions { CancellationToken = cts.Token }));
    }
}
