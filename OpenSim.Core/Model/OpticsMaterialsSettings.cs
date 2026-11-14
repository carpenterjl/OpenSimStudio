namespace OpenSim.Core.Model;

/// <summary>
/// The optical system's material choices as the project and the lens file save them (OP-11):
/// which glass catalogs the lens draws its glasses from. Null on
/// <see cref="OpticsSettings.Materials"/> — every file written before OP-11 — means the
/// default, every catalog loaded. The catalogs themselves are not saved here: they are files
/// in the user's catalog folder (or built in), named by their file name.
/// </summary>
public sealed record OpticsMaterialsSettings
{
    /// <summary>The active catalogs by name, in the order a glass name is searched. Null means
    /// every loaded catalog; a name that is not loaded is kept, so a lens passed through a
    /// machine without that catalog does not lose the choice.</summary>
    public List<string>? ActiveCatalogs { get; init; }
}
