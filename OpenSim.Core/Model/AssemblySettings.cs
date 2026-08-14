namespace OpenSim.Core.Model;

/// <summary>
/// How the bodies of an assembly are joined when they are merged into one solve. Null on a
/// project — which is what every file written before assemblies existed loads as — means the
/// defaults below, so nothing about an old project changes.
/// </summary>
public sealed record AssemblySettings
{
    /// <summary>
    /// Interfacial conductance applied to every detected joint [W/(m²·K)]. See
    /// <see cref="ContactDetectionSettings.DefaultConductance"/> for what the default means
    /// and how far real joints spread around it.
    /// </summary>
    public double ContactConductance { get; init; } = 5e3;

    /// <summary>How far apart two surfaces may be and still be treated as touching [m];
    /// 0 = auto from the mesh size.</summary>
    public double ContactGapTolerance { get; init; }

    /// <summary>The detection settings these project settings describe.</summary>
    public ContactDetectionSettings ToDetectionSettings() => new()
    {
        DefaultConductance = ContactConductance,
        GapTolerance = ContactGapTolerance
    };
}
