namespace OpenSim.Core.Results;

/// <summary>
/// One labeled result set of a multi-frame solve — a time step, a vibration mode, or a
/// frequency point. All frames of one solve must carry the same field names, units,
/// locations, and counts, so a viewer can keep the selected field while scrubbing frames.
/// </summary>
/// <param name="Label">Display label, e.g. "t = 2.5 s", "Mode 3 — 1.24 kHz", "f = 1 MHz".</param>
/// <param name="Value">The frame's position on the axis (seconds, mode number, hertz) — used for ordering and scrubber placement.</param>
/// <param name="Fields">The result fields at this frame.</param>
public sealed record ResultFrame(string Label, double Value, IReadOnlyList<IResultField> Fields)
{
    /// <summary>Unit of <see cref="Value"/> ("s", "Hz"); null for dimensionless axes such as mode number.</summary>
    public string? Unit { get; init; }

    /// <summary>Optional per-frame scalars (label → value), e.g. "|Z| (Ω)" and "Phase (°)" at one frequency.</summary>
    public IReadOnlyDictionary<string, double>? Summary { get; init; }
}
