using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One detected port of the fluid domain, as the user configures it: what it does, how
/// fast the stream moves through it, and how hot that stream is.
/// <para>
/// The velocity is entered as a SPEED, not a vector: a port lies on a known box face, so
/// its inward normal is known, and asking for three components would invite a sign error
/// that reads as "the inlet sucks". The vector handed to the solver is speed × inward
/// normal, which is why an inlet on XMax flows in −x without the user saying so.
/// </para>
/// </summary>
public sealed partial class FlowOpeningRow : ObservableObject
{
    public FlowOpeningRow(FlowOpeningCandidate candidate, int index)
    {
        Candidate = candidate;
        Index = index;
        Label = candidate.Describe();
    }

    /// <summary>1-based position in the detected list — what the port is called in the
    /// solver's own ledger ("Opening 2 (XMax)"), so the panel and the log agree.</summary>
    public int Index { get; }

    /// <summary>The detected geometry this row configures.</summary>
    public FlowOpeningCandidate Candidate { get; }

    /// <summary>"XMin, ⌀20.0 mm at (0.0, 180.0, 25.0) mm".</summary>
    public string Label { get; }

    /// <summary>0 = unused (stays a wall), 1 = inlet, 2 = pressure outlet.</summary>
    [ObservableProperty] private int _role;

    /// <summary>Stream speed through the port [m/s]; inlets only.</summary>
    [ObservableProperty] private double _speed = 0.1;

    /// <summary>Stream temperature [K]; inlets only. Empty/0 means the ambient.</summary>
    [ObservableProperty] private double _temperature = 293.15;

    /// <summary>Whether the speed/temperature boxes apply (an outlet takes neither).</summary>
    public bool IsInlet => Role == 1;

    partial void OnRoleChanged(int value) => OnPropertyChanged(nameof(IsInlet));

    /// <summary>The solver-facing opening, or null when this port is left as wall.</summary>
    public FlowOpening? Build()
    {
        if (Role == 0) return null;
        if (Role == 2)
            return Candidate.ToOpening(FlowFaceKind.OutletPressure, new Vector3D(0, 0, 0), null);
        return Candidate.ToOpening(FlowFaceKind.InletVelocity,
            InwardNormal(Candidate.Face) * Speed, Temperature);
    }

    /// <summary>The domain-box face's INWARD normal — the direction an inlet flows.</summary>
    public static Vector3D InwardNormal(BoxFace face) => face switch
    {
        BoxFace.XMin => new Vector3D(1, 0, 0),
        BoxFace.XMax => new Vector3D(-1, 0, 0),
        BoxFace.YMin => new Vector3D(0, 1, 0),
        BoxFace.YMax => new Vector3D(0, -1, 0),
        BoxFace.ZMin => new Vector3D(0, 0, 1),
        _ => new Vector3D(0, 0, -1)
    };
}
