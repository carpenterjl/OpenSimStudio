namespace OpenSim.Optics.Paraxial;

/// <summary>
/// A paraxial (ABCD) matrix acting on the column (y, ν), with y the ray height and ν = n·u the
/// reduced slope: n the signed index (docs/Optics-Conventions.md §11) and u = dy/dz. In these
/// variables every matrix has determinant 1. A transfer by thickness t in a space of signed
/// index n is [[1, t/n], [0, 1]]; a surface of power φ is [[1, 0], [−φ, 1]]; the system's power
/// is −C.
/// </summary>
public readonly record struct ParaxialMatrix(double A, double B, double C, double D)
{
    public static ParaxialMatrix Identity => new(1, 0, 0, 1);

    /// <summary>A transfer by the reduced thickness t/n.</summary>
    public static ParaxialMatrix Transfer(double reducedThickness) => new(1, reducedThickness, 0, 1);

    /// <summary>Refraction or reflection at a surface of power φ = c·(n′ − n), signed indices.</summary>
    public static ParaxialMatrix Refraction(double power) => new(1, 0, -power, 1);

    public double Determinant => A * D - B * C;

    /// <summary><paramref name="second"/> applied after <paramref name="first"/>: second·first.</summary>
    public static ParaxialMatrix operator *(ParaxialMatrix second, ParaxialMatrix first) => new(
        second.A * first.A + second.B * first.C,
        second.A * first.B + second.B * first.D,
        second.C * first.A + second.D * first.C,
        second.C * first.B + second.D * first.D);

    /// <summary>The matrix of absolute values; a product of these bounds the size of every term
    /// summed into the product of the matrices themselves, which gives a rounding-error scale.</summary>
    public ParaxialMatrix Abs() => new(Math.Abs(A), Math.Abs(B), Math.Abs(C), Math.Abs(D));

    /// <summary>The ray (y, ν) after this matrix.</summary>
    public (double Y, double Nu) Apply(double y, double nu) => (A * y + B * nu, C * y + D * nu);
}
