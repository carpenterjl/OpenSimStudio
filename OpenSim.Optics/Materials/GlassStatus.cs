namespace OpenSim.Optics.Materials;

/// <summary>
/// A glass's catalog status, by the number an AGF catalog's NM line gives it. The numbering is
/// from the ZemaxGlass user manual (zemaxglass_manual.txt, nomenclature, "status": 0 standard,
/// 1 preferred, 2 obsolete, 3 special, 4 melt) and agrees with the order of AGFFileReader.jl's
/// <c>STATUS</c> list (<c>src/constants.jl</c> line 13).
/// </summary>
public enum GlassStatus
{
    Standard = 0,
    Preferred = 1,
    Obsolete = 2,
    Special = 3,
    Melt = 4
}
