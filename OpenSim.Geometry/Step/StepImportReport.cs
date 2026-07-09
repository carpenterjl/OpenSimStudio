using OpenSim.Core.Model;

namespace OpenSim.Geometry.Step;

/// <summary>
/// Import result plus the advisory notes the log panel should show (multi-solid choice,
/// ignored assembly transforms, sampling-floor merges, orientation repairs, counts).
/// Notes are how "never a silent surprise" survives the importer running on a background
/// thread — the view model flushes them to the log after the await.
/// </summary>
public sealed record StepImportReport(TriangleMesh Mesh, IReadOnlyList<string> Notes);
