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

    /// <summary>
    /// Electrical conductance per area of every detected joint [S/m²] — the reciprocal of its
    /// contact resistance × area — for an electrical solve of the assembly. Null (the default,
    /// and what every older file loads as) means no value is known: the electrical solvers
    /// then run on one body at a time, as they always did. There is deliberately no stand-in
    /// number: against a conductor a joint is far from ideal at any value one could guess
    /// (a copper bar 10 mm long has 1.7e-10 Ω·m² of its own), so the joint's value is the
    /// user's to state.
    /// </summary>
    public double? ElectricalContactConductance { get; init; }

    /// <summary>The detection settings these project settings describe.</summary>
    public ContactDetectionSettings ToDetectionSettings() => new()
    {
        DefaultConductance = ContactConductance,
        GapTolerance = ContactGapTolerance
    };
}
