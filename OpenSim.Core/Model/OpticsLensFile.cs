namespace OpenSim.Core.Model;

/// <summary>
/// A lens saved on its own, outside a project (OpticStudio's lens file save and load): the
/// optics settings and a name. Written and read by <c>OpticsFileSerializer</c>.
/// </summary>
public sealed class OpticsLensFile
{
    /// <summary>The lens-file format this file was written in
    /// (<see cref="Persistence.OpticsFileSerializer.CurrentFormatVersion"/>); 1 = the first.</summary>
    public int FormatVersion { get; set; }

    /// <summary>The lens's name, for the user (the system title, or the file name).</summary>
    public string Name { get; set; } = "Untitled lens";

    /// <summary>The whole optical model, as a project's <see cref="SimProject.Optics"/>.</summary>
    public OpticsSettings? Optics { get; set; }
}
