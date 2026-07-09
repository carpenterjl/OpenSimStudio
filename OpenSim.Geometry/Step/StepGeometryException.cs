namespace OpenSim.Geometry.Step;

/// <summary>
/// Geometry-stage failure (tessellation, UV inversion, refinement cap): the file parsed
/// and resolved but a numerical step could not complete honestly. The message names the
/// entity #ids involved — a wrong point is never returned in place of an error.
/// </summary>
public sealed class StepGeometryException : StepImportException
{
    public StepGeometryException(string message) : base(message) { }
}
