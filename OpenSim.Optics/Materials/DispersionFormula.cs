namespace OpenSim.Optics.Materials;

/// <summary>
/// OpticStudio's thirteen dispersion formulas, by the number an AGF catalog's NM line gives
/// them. The numbering and the names follow AGFFileReader.jl (MIT, Microsoft),
/// <c>src/constants.jl</c>, <c>DISPFORM_NAMES</c> (1-based: entry n is formula n), and agree
/// with ZemaxGlass.py (MIT, N. Hagen), <c>get_dispersion</c>, which labels formula 13
/// "Hikari" where AGFFileReader says "Extended3". The expressions are in
/// <see cref="DispersionFormulas.Evaluate"/>.
/// </summary>
public enum DispersionFormula
{
    Schott = 1,
    Sellmeier1 = 2,
    Herzberger = 3,
    /// <summary>Unverified: the two readers disagree on its second term
    /// (<see cref="DispersionFormulas.Evaluate"/>).</summary>
    Sellmeier2 = 4,
    Conrady = 5,
    Sellmeier3 = 6,
    HandbookOfOptics1 = 7,
    HandbookOfOptics2 = 8,
    Sellmeier4 = 9,
    Extended1 = 10,
    Sellmeier5 = 11,
    Extended2 = 12,
    Extended3 = 13
}
