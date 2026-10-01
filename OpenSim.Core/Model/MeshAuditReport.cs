using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>How one audit check came out.</summary>
public enum MeshAuditOutcome
{
    Passed,
    Failed,
    /// <summary>Not evaluated — it does not apply, or a check it depends on failed first.</summary>
    Skipped
}

/// <summary>
/// One check of the post-mesh audit. <paramref name="Observed"/> and <paramref name="Limit"/>
/// are the worst measured value and the bound it was held to, where the check has them;
/// <paramref name="Location"/> is where the worst value (or the first failure) sits.
/// </summary>
public sealed record MeshAuditCheck(
    string Id,
    string Name,
    MeshAuditOutcome Outcome,
    string Detail,
    double? Observed = null,
    double? Limit = null,
    Vector3D? Location = null);

/// <summary>
/// What the post-mesh audit found: one entry per check, in order, plus remarks that are not
/// failures. Attached to the <see cref="FeMesh"/> it describes, for display only.
/// </summary>
public sealed record MeshAuditReport(
    double EdgeLength,
    IReadOnlyList<MeshAuditCheck> Checks,
    IReadOnlyList<string> Warnings)
{
    public bool Passed => Checks.All(c => c.Outcome != MeshAuditOutcome.Failed);

    public IEnumerable<MeshAuditCheck> Failures => Checks.Where(c => c.Outcome == MeshAuditOutcome.Failed);

    /// <summary>The check with this id ("A0" … "A10"), or null when it was not part of the run.</summary>
    public MeshAuditCheck? this[string id] => Checks.FirstOrDefault(c => c.Id == id);

    /// <summary>One line per failed check — what an exception message or a log line carries.</summary>
    public string FailureSummary =>
        string.Join(" | ", Failures.Select(c => $"{c.Id} {c.Name}: {c.Detail}"));

    /// <summary>One line per check, for the meshing view.</summary>
    public string Describe()
    {
        var lines = Checks.Select(c => c.Outcome switch
        {
            MeshAuditOutcome.Passed => $"{c.Id} {c.Name}: ok" + (c.Detail.Length > 0 ? $" ({c.Detail})" : ""),
            MeshAuditOutcome.Skipped => $"{c.Id} {c.Name}: skipped ({c.Detail})",
            _ => $"{c.Id} {c.Name}: FAILED — {c.Detail}"
        });
        return string.Join(Environment.NewLine, lines.Concat(Warnings.Select(w => "note: " + w)));
    }
}

/// <summary>
/// The mesh does not represent the geometry it was made from, or the geometry cannot be
/// represented at the requested element size. Carries the full <see cref="Report"/>.
/// </summary>
public sealed class MeshAuditException : InvalidOperationException
{
    public MeshAuditReport Report { get; }

    public MeshAuditException(MeshAuditReport report)
        : base("Mesh audit failed — " + report.FailureSummary)
    {
        Report = report;
    }
}
