namespace OpenSim.Core.Model;

/// <summary>
/// The Optics workspace's inputs, saved with the project so an optical study re-opens the way
/// it was set up. Null on a project — every file written before the workspace existed — leaves
/// the workspace as it is. Results are not saved; they are recomputed.
/// </summary>
public sealed record OpticsSettings
{
    /// <summary>Sequential or non-sequential, by the saved name OpenSim.Optics defines
    /// (<c>OpticsModes.SavedName</c>). A string, as the project's analysis type is, so a file
    /// from a later build with a mode this one does not know still loads. Null means the
    /// workspace's default.</summary>
    public string? Mode { get; init; }
}
