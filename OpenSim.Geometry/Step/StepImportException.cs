namespace OpenSim.Geometry.Step;

/// <summary>
/// Base class for every failure the STEP importer can raise. The message is always
/// actionable (it names the offending entity #id, line, or construct) because a STEP
/// import failure surfaces directly in the application log — never a silent drop.
/// </summary>
public class StepImportException : Exception
{
    public StepImportException(string message) : base(message) { }

    public StepImportException(string message, Exception inner) : base(message, inner) { }
}
