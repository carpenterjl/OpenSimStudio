namespace OpenSim.Optics.Materials;

/// <summary>Which list of dispersion formulas a material's <see cref="OpticalMaterial.FormulaNumber"/>
/// counts in. The two lists share numbers but not meanings.</summary>
public enum FormulaNumbering
{
    /// <summary>OpticStudio's thirteen, as an AGF catalog numbers them (<see cref="DispersionFormulas"/>).</summary>
    OpticStudio = 0,

    /// <summary>The refractiveindex.info database's nine (<see cref="RefractiveIndexInfoFormulas"/>).</summary>
    RefractiveIndexInfo = 1
}
