namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// A backdrill fabrication spec from a <c>&lt;Spec&gt;&lt;Backdrill type=…&gt;</c> block
/// (Cadence puts them in the CadHeader): the drill enters at <see cref="StartLayer"/>
/// and bores the barrel stub out down to — but never cutting —
/// <see cref="MustNotCutLayers"/>. Electrically, a backdrill SEVERS a coincident via's
/// connection on the drilled-out layers: the copper ring may remain, but the barrel no
/// longer joins it to the rest of the via.
/// </summary>
public sealed record Ipc2581BackdrillSpec(
    string Name,
    string? StartLayer,
    IReadOnlyList<string> MustNotCutLayers,
    double? MaxStubLengthMeters);
