namespace OpenSim.Geometry.Step.Part21;

internal enum StepTokenKind
{
    Hash,        // #123 — instance id or reference (Integer carries the id)
    Keyword,     // CARTESIAN_POINT, ISO-10303-21 (hyphens allowed for the section markers)
    Integer,
    Real,
    Text,        // decoded string literal
    Enumeration, // .MILLI. → "MILLI"
    Dollar,
    Star,
    LParen,
    RParen,
    Comma,
    Equals,
    Semicolon,
    EndOfFile
}

/// <summary>One lexical token with its 1-based source line for error reporting.</summary>
internal readonly record struct StepToken(StepTokenKind Kind, string Text, long Integer, double Real, int Line)
{
    public override string ToString() => Kind switch
    {
        StepTokenKind.Hash => $"#{Integer}",
        StepTokenKind.Keyword => Text,
        StepTokenKind.Integer => Integer.ToString(),
        StepTokenKind.Real => Real.ToString("R"),
        StepTokenKind.Text => $"'{Text}'",
        StepTokenKind.Enumeration => $".{Text}.",
        StepTokenKind.Dollar => "$",
        StepTokenKind.Star => "*",
        StepTokenKind.LParen => "(",
        StepTokenKind.RParen => ")",
        StepTokenKind.Comma => ",",
        StepTokenKind.Equals => "=",
        StepTokenKind.Semicolon => ";",
        _ => "<end of file>"
    };
}
