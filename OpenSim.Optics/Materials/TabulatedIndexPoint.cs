namespace OpenSim.Optics.Materials;

/// <summary>One row of a tabulated material: the real index n and extinction coefficient k at
/// one wavelength (µm, in air at the reference conditions, like a catalog formula's).</summary>
public sealed record TabulatedIndexPoint(double Micrometers, double N, double K);
