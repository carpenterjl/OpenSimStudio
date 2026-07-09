namespace OpenSim.Geometry.Step;

/// <summary>
/// A STEP entity outside the v1 subset was reached from a solid. The message names the
/// entity, its #id, and where it was used, so the user knows exactly what their exporter
/// produced — the project rule is a loud typed failure, never a silently wrong mesh.
/// </summary>
public sealed class StepUnsupportedEntityException : StepImportException
{
    private const string Supported =
        "v1 supports: plane, cylindrical, conical, spherical, toroidal, linear-extrusion, " +
        "revolution and B-spline surfaces; line, circle, ellipse and B-spline curves.";

    public StepUnsupportedEntityException(int id, string keyword, string context)
        : base($"#{id} {keyword} ({context}) is not supported yet. {Supported}")
    {
    }

    public StepUnsupportedEntityException(string message) : base(message) { }
}
