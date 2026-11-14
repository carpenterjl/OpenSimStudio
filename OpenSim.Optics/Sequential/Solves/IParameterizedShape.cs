namespace OpenSim.Optics.Sequential.Solves;

/// <summary>
/// A <see cref="SurfaceShape"/> whose numeric parameters (OpticStudio's parameter columns)
/// parameter solves can read and write. The aspheres (OP-23) and special surfaces (OP-27) keep
/// their parameters in their shape classes and take part in parameter pickups through the
/// registry of saved surface types (<see cref="SurfaceTypes"/>, TC-6), as do coordinate breaks;
/// another shape takes part by implementing this interface, which the engine asks first. A
/// parameter pickup on any other surface fails with a message naming it. Parameters are numbered from 0, as OpticStudio's "Parameter 0 through 12"
/// (OpticStudio 2025 R2 help, "Integer Codes for Column Numbers").
/// </summary>
public interface IParameterizedShape
{
    /// <summary>The number of parameters (parameter numbers 0 to count − 1).</summary>
    int ParameterCount { get; }

    /// <summary>The value of parameter <paramref name="index"/>.</summary>
    double GetParameter(int index);

    /// <summary>A copy of this shape with parameter <paramref name="index"/> set to
    /// <paramref name="value"/>; throws <see cref="ArgumentException"/> when the value is not
    /// allowed, with a message naming the parameter.</summary>
    SurfaceShape WithParameter(int index, double value);
}
