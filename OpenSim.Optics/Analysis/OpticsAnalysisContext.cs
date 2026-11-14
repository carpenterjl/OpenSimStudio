using OpenSim.Core.Model;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// What an analysis computes from: an immutable snapshot of the optical system taken on the UI
/// thread when the change happened, so a compute on a background thread never sees an edit
/// half made. The system is the project's optics record (<see cref="OpticsSettings"/>); as the
/// system editors arrive they add their data there, and analyses read it from here.
/// </summary>
public sealed record OpticsAnalysisContext(string ProjectName, OpticsSettings System)
{
    /// <summary>The mode the snapshot is in; an unknown saved name reads as the default.</summary>
    public OpticsMode Mode =>
        OpticsModes.TryParseSavedName(System.Mode, out var mode) ? mode : OpticsModes.All[0];
}
