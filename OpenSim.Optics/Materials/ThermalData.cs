namespace OpenSim.Optics.Materials;

/// <summary>
/// The TD line of an AGF catalog: the constants of the glass's dn/dT model and the
/// temperature its index data refer to. Field order from the ZemaxGlass user manual
/// (zemaxglass_manual.txt, "TD &lt;D0&gt; &lt;D1&gt; &lt;D2&gt; &lt;E0&gt; &lt;E1&gt; &lt;Ltk&gt; &lt;Temp&gt;") and
/// AGFFileReader.jl (<c>src/GlassDownload.jl</c>, the AGF layout table). Held as the file
/// prints them; <see cref="ThermalIndex"/> (OP-13) uses them in SCHOTT TIE-19's equations (2)–(4).
/// </summary>
/// <param name="D0">D0, as printed.</param>
/// <param name="D1">D1, as printed.</param>
/// <param name="D2">D2, as printed.</param>
/// <param name="E0">E0, as printed.</param>
/// <param name="E1">E1, as printed.</param>
/// <param name="LambdaTk">λtk, micrometers (both readers subtract its square from λ² in µm²).</param>
/// <param name="ReferenceTemperatureCelsius">The reference temperature of the glass's index
/// data, °C (both readers take the TD line's seventh number as the reference temperature).</param>
public sealed record ThermalData(double D0, double D1, double D2, double E0, double E1, double LambdaTk,
    double ReferenceTemperatureCelsius);
