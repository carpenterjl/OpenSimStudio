using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>
/// A vector of three complex components, for the wave vectors and fields of plane waves that
/// may be evanescent (OP-53). <see cref="Dot"/> is the bilinear product (no conjugate), which is
/// what the dispersion relation and the transversality conditions N·D = 0 use;
/// <see cref="Norm"/> is the Hermitian length.
/// </summary>
public readonly record struct ComplexVector3(Complex X, Complex Y, Complex Z)
{
    public static ComplexVector3 FromReal(Vector3D v) => new(v.X, v.Y, v.Z);

    public static ComplexVector3 operator +(ComplexVector3 a, ComplexVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static ComplexVector3 operator -(ComplexVector3 a, ComplexVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static ComplexVector3 operator -(ComplexVector3 a) => new(-a.X, -a.Y, -a.Z);
    public static ComplexVector3 operator *(ComplexVector3 a, Complex s) => new(a.X * s, a.Y * s, a.Z * s);
    public static ComplexVector3 operator *(Complex s, ComplexVector3 a) => a * s;

    /// <summary>The bilinear product a·b = Σ aᵢbᵢ (no conjugate).</summary>
    public static Complex Dot(ComplexVector3 a, ComplexVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>a·d for a real vector d.</summary>
    public Complex Dot(Vector3D d) => X * d.X + Y * d.Y + Z * d.Z;

    public static ComplexVector3 Cross(ComplexVector3 a, ComplexVector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public ComplexVector3 Conjugate() => new(Complex.Conjugate(X), Complex.Conjugate(Y), Complex.Conjugate(Z));

    /// <summary>√(|X|² + |Y|² + |Z|²).</summary>
    public double Norm => Math.Sqrt(X.Real * X.Real + X.Imaginary * X.Imaginary + Y.Real * Y.Real
        + Y.Imaginary * Y.Imaginary + Z.Real * Z.Real + Z.Imaginary * Z.Imaginary);

    /// <summary>The vector divided by its Hermitian length.</summary>
    public ComplexVector3 Unit() => this * (1 / Norm);

    /// <summary>The real parts.</summary>
    public Vector3D Real => new(X.Real, Y.Real, Z.Real);

    /// <summary>True when every imaginary part is exactly zero.</summary>
    public bool IsReal => X.Imaginary == 0 && Y.Imaginary == 0 && Z.Imaginary == 0;
}
