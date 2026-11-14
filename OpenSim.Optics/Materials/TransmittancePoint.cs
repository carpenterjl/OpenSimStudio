namespace OpenSim.Optics.Materials;

/// <summary>
/// One IT line of an AGF catalog: the internal transmittance (Fresnel reflections removed) of
/// a sample of the given thickness at one wavelength. Field order from the ZemaxGlass user
/// manual (zemaxglass_manual.txt, "IT &lt;lambda&gt; &lt;transmission&gt; &lt;thickness&gt;"). The
/// thickness is in millimeters: AGFFileReader.jl computes absorption "per mm" from it
/// (<c>src/utilities.jl</c>, <c>absorption</c>). Held as the file prints it; no absorption
/// model reads the table yet.
/// </summary>
/// <param name="Micrometers">Wavelength, µm.</param>
/// <param name="Transmittance">Internal transmittance, 0 to 1.</param>
/// <param name="ThicknessMillimeters">Sample thickness, mm; null when the line omits it.</param>
public sealed record TransmittancePoint(double Micrometers, double Transmittance, double? ThicknessMillimeters);
