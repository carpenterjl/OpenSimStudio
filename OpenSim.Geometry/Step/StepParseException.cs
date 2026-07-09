namespace OpenSim.Geometry.Step;

/// <summary>
/// Malformed ISO 10303-21 text. Always carries the 1-based source line so the user can
/// open the file at the problem — Part 21 files are plain text and line numbers are the
/// only address a person can navigate by.
/// </summary>
public sealed class StepParseException : StepImportException
{
    /// <summary>1-based line in the STEP file where the problem was detected.</summary>
    public int Line { get; }

    public StepParseException(int line, string message)
        : base($"STEP parse error at line {line}: {message}")
    {
        Line = line;
    }
}
