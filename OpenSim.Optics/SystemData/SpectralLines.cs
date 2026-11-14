namespace OpenSim.Optics;

/// <summary>A named spectral line: its designation ("d"), what emits it, and its wavelength in
/// micrometers as the source prints it.</summary>
public sealed record SpectralLine(string Designation, string Source, double Micrometers);

/// <summary>A named set of wavelengths the editor can load in one step, with its primary.</summary>
public sealed record WavelengthPreset(string Name, IReadOnlyList<SpectralLine> Lines, int PrimaryIndex)
{
    /// <summary>The preset as a wavelength table, every weight 1.</summary>
    public WavelengthSet ToWavelengthSet() => new()
    {
        Wavelengths = Lines.Select(l => new Wavelength { Micrometers = l.Micrometers }).ToArray(),
        PrimaryIndex = PrimaryIndex
    };
}

/// <summary>
/// Spectral lines and the wavelength presets built from them.
/// <para>
/// Source, for every wavelength here: SCHOTT AG, Technical Information TIE-29 "Refractive Index
/// and Dispersion", April 2005, Table 2.3-1 "Wavelengths for a selection of frequently used
/// spectral lines" (page 5/15), read 2026-11-13 from
/// wp.optics.arizona.edu/optomech/wp-content/uploads/sites/53/2016/10/tie-29_refractive_index_v2_us.pdf.
/// The values are copied with the digits the table prints (the text beside it asks for exactly
/// those digits in the Sellmeier equation), converted from nanometers to micrometers by moving
/// the decimal point only. The table does not say air or vacuum; the same page says the
/// dispersion equation it serves gives indices in air at room temperature, which is
/// OpticStudio's meaning of a wavelength too (<see cref="Wavelength"/>).
/// </para>
/// <para>
/// Laser lines other than the two the table lists (He-Ne 632.8 nm, Nd glass 1060.0 nm) are left
/// out until read at a source: OpticStudio's preset list has more, read from memory only.
/// </para>
/// </summary>
public static class SpectralLines
{
    // TIE-29 Table 2.3-1, in the table's order; wavelength [nm] / 1000.
    public static SpectralLine HgInfrared2325 { get; } = new("", "Infrared mercury line (Hg)", 2.32542);
    public static SpectralLine HgInfrared1970 { get; } = new("", "Infrared mercury line (Hg)", 1.97009);
    public static SpectralLine HgInfrared1529 { get; } = new("", "Infrared mercury line (Hg)", 1.529582);
    public static SpectralLine NdGlassLaser { get; } = new("", "Neodymium glass laser (Nd)", 1.0600);
    public static SpectralLine T { get; } = new("t", "Infrared mercury line (Hg)", 1.01398);
    public static SpectralLine S { get; } = new("s", "Infrared cesium line (Cs)", 0.85211);
    public static SpectralLine R { get; } = new("r", "Red helium line (He)", 0.7065188);
    public static SpectralLine C { get; } = new("C", "Red hydrogen line (H)", 0.6562725);
    public static SpectralLine CPrime { get; } = new("C'", "Red cadmium line (Cd)", 0.6438469);
    public static SpectralLine HeNeLaser { get; } = new("", "Helium-neon gas laser (He-Ne)", 0.6328);
    public static SpectralLine D { get; } = new("D", "Yellow sodium line (Na), centre of the double line", 0.5892938);
    public static SpectralLine LowerD { get; } = new("d", "Yellow helium line (He)", 0.5875618);
    public static SpectralLine E { get; } = new("e", "Green mercury line (Hg)", 0.546074);
    public static SpectralLine F { get; } = new("F", "Blue hydrogen line (H)", 0.4861327);
    public static SpectralLine FPrime { get; } = new("F'", "Blue cadmium line (Cd)", 0.4799914);
    public static SpectralLine G { get; } = new("g", "Blue mercury line (Hg)", 0.4358343);
    public static SpectralLine H { get; } = new("h", "Violet mercury line (Hg)", 0.4046561);
    public static SpectralLine I { get; } = new("i", "Ultraviolet mercury line (Hg)", 0.3650146);
    public static SpectralLine HgUltraviolet334 { get; } = new("", "Ultraviolet mercury line (Hg)", 0.3341478);
    public static SpectralLine HgUltraviolet312 { get; } = new("", "Ultraviolet mercury line (Hg)", 0.3125663);
    public static SpectralLine HgUltraviolet296 { get; } = new("", "Ultraviolet mercury line (Hg)", 0.2967278);
    public static SpectralLine HgUltraviolet280 { get; } = new("", "Ultraviolet mercury line (Hg)", 0.2804);
    public static SpectralLine HgUltraviolet248 { get; } = new("", "Ultraviolet mercury line (Hg)", 0.2483);

    /// <summary>Every line of the table, longest wavelength first as the table lists them.</summary>
    public static IReadOnlyList<SpectralLine> All { get; } = new[]
    {
        HgInfrared2325, HgInfrared1970, HgInfrared1529, NdGlassLaser, T, S, R, C, CPrime, HeNeLaser,
        D, LowerD, E, F, FPrime, G, H, I, HgUltraviolet334, HgUltraviolet312, HgUltraviolet296,
        HgUltraviolet280, HgUltraviolet248
    };

    /// <summary>
    /// The presets the editor offers, shortest wavelength first within each set. The visible
    /// sets are the line triples the Abbe numbers ν_d (F, d, C) and ν_e (F', e, C') are built
    /// on (TIE-29, equations 2.1-1 and 2.1-2), with the middle line primary; the choice of
    /// sets and primaries is ours.
    /// </summary>
    public static IReadOnlyList<WavelengthPreset> Presets { get; } = new[]
    {
        new WavelengthPreset("F, d, C (visible)", new[] { F, LowerD, C }, 1),
        new WavelengthPreset("F', e, C' (visible)", new[] { FPrime, E, CPrime }, 1),
        new WavelengthPreset("g, F, d, C", new[] { G, F, LowerD, C }, 2),
        new WavelengthPreset("d (0.5875618 µm)", new[] { LowerD }, 0),
        new WavelengthPreset("e (0.546074 µm)", new[] { E }, 0),
        new WavelengthPreset("Helium-neon laser (0.6328 µm)", new[] { HeNeLaser }, 0),
        new WavelengthPreset("Neodymium glass laser (1.0600 µm)", new[] { NdGlassLaser }, 0),
    };
}
