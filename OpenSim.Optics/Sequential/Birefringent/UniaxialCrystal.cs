using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>The two eigenmodes of a uniaxial crystal (in an isotropic medium: the s and p
/// polarizations of <see cref="UniaxialOptics.Field"/>).</summary>
public enum CrystalMode
{
    Ordinary = 0,
    Extraordinary = 1,
}

/// <summary>
/// A homogeneous uniaxial crystal (OP-53, docs/Optics-Conventions.md §62): the ordinary index
/// n_o, the extraordinary index n_e, both relative to the system's air like every index of the
/// trace, and the unit crystal (optic) axis â in some frame.
/// <para>
/// <b>Index of the extraordinary wave.</b> OpticStudio 2025 R2 help, "Birefringent In and
/// Birefringent Out" (saved under plans/sources/optics/op-53-help/), equation 3:
/// (1/n(θ_w))² = (cos θ_w / n_o)² + (sin θ_w / n_e)², θ_w the angle between â and the wave
/// vector k̂ (cos θ_w = k̂·â). Written for the wave vector N = n k̂ (in units of the vacuum
/// wavenumber) this is the extraordinary sheet of the normal surface,
/// (N·â)²/n_o² + (N·N − (N·â)²)/n_e² = 1, which <see cref="UniaxialOptics.Wave"/> solves.
/// </para>
/// <para>
/// <b>Ray direction.</b> The energy (Poynting, ray) direction ŝ is normal to the normal surface
/// at N (the group velocity ∇_k ω), so ŝ ∝ ∇F with F the left side above:
/// ŝ ∝ N/n_e² + Δ (N·â) â, Δ = 1/n_o² − 1/n_e² (derived here). With k̂ ⟂ â in the plane of
/// k̂ and â this gives tan α = (n_e² − n_o²) tan θ_w / (n_e² + (n_o tan θ_w)²) for the angle α
/// between k̂ and ŝ, the help's equations 4 and 5, and ŝ, k̂ and â coplanar as the help says
/// (the derivation is repeated in BirefringentTests). For the ordinary wave ŝ = k̂.
/// </para>
/// </summary>
public readonly record struct UniaxialCrystal
{
    public UniaxialCrystal(double ordinary, double extraordinary, Vector3D axis)
    {
        if (!double.IsFinite(ordinary) || !(ordinary > 0))
            throw new ArgumentOutOfRangeException(nameof(ordinary), ordinary, "The ordinary index must be a positive number.");
        if (!double.IsFinite(extraordinary) || !(extraordinary > 0))
            throw new ArgumentOutOfRangeException(nameof(extraordinary), extraordinary, "The extraordinary index must be a positive number.");
        double length = axis.Length;
        if (!double.IsFinite(length) || !(length > 0))
            throw new ArgumentException($"The crystal axis {axis} has no direction; give its direction cosines.", nameof(axis));
        Ordinary = ordinary;
        Extraordinary = extraordinary;
        Axis = axis / length;
    }

    /// <summary>n_o.</summary>
    public double Ordinary { get; }

    /// <summary>n_e.</summary>
    public double Extraordinary { get; }

    /// <summary>The unit crystal axis â.</summary>
    public Vector3D Axis { get; }

    /// <summary>Δ = 1/n_o² − 1/n_e².</summary>
    public double Delta => 1 / (Ordinary * Ordinary) - 1 / (Extraordinary * Extraordinary);

    /// <summary>The same crystal with its axis in another frame.</summary>
    public UniaxialCrystal WithAxis(Vector3D axis) => new(Ordinary, Extraordinary, axis);

    /// <summary>n(θ_w) of the extraordinary wave whose wave normal is <paramref name="waveNormal"/>
    /// (any length), the help's equation 3.</summary>
    public double EffectiveIndex(Vector3D waveNormal)
    {
        var k = waveNormal / waveNormal.Length;
        double c = Vector3D.Dot(k, Axis);
        double s2 = Math.Max(0, 1 - c * c);
        return 1 / Math.Sqrt(c * c / (Ordinary * Ordinary) + s2 / (Extraordinary * Extraordinary));
    }

    /// <summary>The phase index of a mode for a wave normal: n_o, or n(θ_w).</summary>
    public double PhaseIndex(CrystalMode mode, Vector3D waveNormal) =>
        mode == CrystalMode.Ordinary ? Ordinary : EffectiveIndex(waveNormal);

    /// <summary>The unit ray (energy) direction ŝ of a mode whose real wave vector is
    /// <paramref name="waveVector"/>: k̂ for the ordinary wave, N/n_e² + Δ(N·â)â normalised for
    /// the extraordinary one.</summary>
    public Vector3D RayDirection(CrystalMode mode, Vector3D waveVector)
    {
        var s = mode == CrystalMode.Ordinary
            ? waveVector
            : waveVector / (Extraordinary * Extraordinary) + Axis * (Delta * Vector3D.Dot(waveVector, Axis));
        return s / s.Length;
    }

    /// <summary>The angle between the wave normal and the ray of the extraordinary wave along
    /// <paramref name="waveNormal"/>, radians.</summary>
    public double WalkOffAngle(Vector3D waveNormal)
    {
        var k = waveNormal / waveNormal.Length;
        var s = RayDirection(CrystalMode.Extraordinary, k);
        return Math.Atan2(Vector3D.Cross(k, s).Length, Vector3D.Dot(k, s));
    }
}

/// <summary>One side of an interface: an isotropic medium of index <see cref="Index"/>, or a
/// uniaxial crystal (then <see cref="Index"/> is its ordinary index).</summary>
public readonly record struct OpticalSide
{
    private OpticalSide(double index, UniaxialCrystal? crystal)
    {
        Index = index;
        Crystal = crystal;
    }

    public double Index { get; }
    public UniaxialCrystal? Crystal { get; }
    public bool IsCrystal => Crystal is not null;

    public static OpticalSide Isotropic(double index)
    {
        if (!double.IsFinite(index) || !(index > 0))
            throw new ArgumentOutOfRangeException(nameof(index), index, "An index must be a positive number.");
        return new OpticalSide(index, null);
    }

    public static OpticalSide Of(UniaxialCrystal crystal) => new(crystal.Ordinary, crystal);
}

/// <summary>A plane wave of one mode at an interface: its wave vector N = n k̂ (in units of the
/// vacuum wavenumber; complex when evanescent), whether it propagates, and its unit ray direction
/// (zero when it does not).</summary>
public readonly record struct ModeWave(ComplexVector3 WaveVector, bool Propagating, Vector3D Ray)
{
    /// <summary>The real wave vector of a propagating wave.</summary>
    public Vector3D RealWaveVector => WaveVector.Real;
}

/// <summary>
/// Plane waves of a uniaxial crystal or an isotropic medium at an interface (OP-53,
/// docs/Optics-Conventions.md §62): the wave vectors that share a tangential component (Snell's
/// law in its general form, continuity of the tangential wave vector), the ray directions, the
/// electric fields of the eigenmodes, and the power-normalised reflection and transmission
/// amplitudes from the boundary conditions.
/// </summary>
public static class UniaxialOptics
{
    /// <summary>|N × â| below this times |N| is "along the optic axis", where the ordinary and
    /// extraordinary waves coincide and their fields are chosen as s and p
    /// (<see cref="Field"/>).</summary>
    public const double AlongAxis = 1e-9;

    /// <summary>
    /// The wave of <paramref name="mode"/> in <paramref name="side"/> whose wave vector has the
    /// tangential part <paramref name="kappa"/> (real, at right angles to the unit normal
    /// <paramref name="eta"/>): N = κ + q η̂. <paramref name="sense"/> +1 asks for the wave
    /// whose energy travels along +η̂ (incident and transmitted waves, η̂ oriented along the
    /// light), −1 for the one travelling back (reflected waves). An evanescent wave decays away
    /// from the interface into its own side: Im q has the sign of <paramref name="sense"/> for a
    /// field e^(i k₀ N·r).
    /// <para>
    /// Isotropic medium and ordinary wave: q² = n² − κ·κ. Extraordinary wave: putting N = κ + qη̂
    /// in (N·â)²Δ + N·N/n_e² = 1 (the normal surface of <see cref="UniaxialCrystal"/>, rearranged)
    /// gives α q² + β q + γ = 0 with α = B²Δ + 1/n_e², β = 2ABΔ, γ = A²Δ + κ·κ/n_e² − 1,
    /// A = κ·â, B = η̂·â (derived). α &gt; 0 always. Of two real roots the one whose ray has the
    /// larger sense·(ŝ·η̂) is taken: the extraordinary sheet is a convex ellipsoid, so the line
    /// N = κ + qη̂ meets it where the outward normals (the rays) point one each way along η̂.
    /// </para>
    /// </summary>
    public static ModeWave Wave(OpticalSide side, CrystalMode mode, Vector3D kappa, Vector3D eta, int sense)
    {
        if (sense != 1 && sense != -1) throw new ArgumentOutOfRangeException(nameof(sense), sense, "The sense is +1 or -1.");
        double tau2 = kappa.LengthSquared;
        if (side.Crystal is not { } c || mode == CrystalMode.Ordinary)
        {
            double n = side.Index;
            double q2 = n * n - tau2;
            if (q2 >= 0)
            {
                var wave = kappa + eta * (sense * Math.Sqrt(q2));
                return new ModeWave(ComplexVector3.FromReal(wave), true, wave / wave.Length);
            }
            var evanescent = ComplexVector3.FromReal(kappa) + ComplexVector3.FromReal(eta) * new System.Numerics.Complex(0, sense * Math.Sqrt(-q2));
            return new ModeWave(evanescent, false, Vector3D.Zero);
        }

        var a = c.Axis;
        double delta = c.Delta, ine2 = 1 / (c.Extraordinary * c.Extraordinary);
        double A = Vector3D.Dot(kappa, a), B = Vector3D.Dot(eta, a);
        double alpha = B * B * delta + ine2, beta = 2 * A * B * delta, gamma = A * A * delta + tau2 * ine2 - 1;
        double disc = beta * beta - 4 * alpha * gamma;
        if (disc >= 0)
        {
            double root = Math.Sqrt(disc);
            double t = -0.5 * (beta + Math.CopySign(root, beta));
            double q1 = t / alpha;
            double q2 = t != 0 ? gamma / t : q1;
            var w1 = kappa + eta * q1;
            var w2 = kappa + eta * q2;
            var s1 = c.RayDirection(CrystalMode.Extraordinary, w1);
            var s2 = c.RayDirection(CrystalMode.Extraordinary, w2);
            bool first = sense * Vector3D.Dot(s1, eta) >= sense * Vector3D.Dot(s2, eta);
            return first
                ? new ModeWave(ComplexVector3.FromReal(w1), true, s1)
                : new ModeWave(ComplexVector3.FromReal(w2), true, s2);
        }
        var q = new System.Numerics.Complex(-beta / (2 * alpha), sense * Math.Sqrt(-disc) / (2 * alpha));
        return new ModeWave(ComplexVector3.FromReal(kappa) + ComplexVector3.FromReal(eta) * q, false, Vector3D.Zero);
    }

    /// <summary>
    /// The unit electric field of a mode with wave vector <paramref name="wave"/> (Hermitian
    /// norm 1). Ordinary wave: E ∝ N × â (at right angles to the plane of k̂ and â; help, "Birefringent
    /// Types": the ordinary ray "carries the portion of the electric field that lies orthogonal
    /// to the plane that contains the direction of propagation and the crystal axis vector").
    /// Extraordinary wave: D ∝ (N × â) × N = â(N·N) − N(N·â) lies in that plane with N·D = 0,
    /// and E = ε⁻¹D = D/n_o² + (1/n_e² − 1/n_o²)(â·D)â (derived from ∇·D = 0 and the uniaxial
    /// permittivity ε = n_o² I + (n_e² − n_o²) â âᵀ); this E is at right angles to the ray.
    /// Isotropic medium: Ordinary is s, E ∝ N × η̂, and Extraordinary is p, E ∝ N × E_s. Along the
    /// optic axis, where N × â vanishes, the crystal's waves are the isotropic s and p; at normal
    /// incidence, where N × η̂ vanishes too, s is <paramref name="fallback"/> (a real unit vector
    /// at right angles to η̂).
    /// </summary>
    public static ComplexVector3 Field(OpticalSide side, CrystalMode mode, ComplexVector3 wave, Vector3D eta, Vector3D fallback)
    {
        double nNorm = wave.Norm;
        var etaC = ComplexVector3.FromReal(eta);
        ComplexVector3 S()
        {
            var s = ComplexVector3.Cross(wave, etaC);
            return s.Norm >= AlongAxis * nNorm ? s.Unit() : ComplexVector3.FromReal(fallback).Unit();
        }
        ComplexVector3 P() => ComplexVector3.Cross(wave, S()).Unit();
        if (side.Crystal is not { } c) return mode == CrystalMode.Ordinary ? S() : P();
        var a = ComplexVector3.FromReal(c.Axis);
        var o = ComplexVector3.Cross(wave, a);
        bool alongAxis = o.Norm < AlongAxis * nNorm;
        if (mode == CrystalMode.Ordinary) return alongAxis ? S() : o.Unit();
        if (alongAxis) return P();
        var d = a * ComplexVector3.Dot(wave, wave) - wave * ComplexVector3.Dot(wave, a);
        double ino2 = 1 / (c.Ordinary * c.Ordinary), ine2 = 1 / (c.Extraordinary * c.Extraordinary);
        var e = d * ino2 + a * ((ine2 - ino2) * ComplexVector3.Dot(a, d));
        return e.Unit();
    }

    /// <summary>The power flux along η̂ of a unit-field wave, Re((E × H*)·η̂) with H = N × E
    /// (up to a constant common to every wave of the problem).</summary>
    public static double Flux(ComplexVector3 field, ComplexVector3 wave, Vector3D eta)
    {
        var h = ComplexVector3.Cross(wave, field);
        return ComplexVector3.Cross(field, h.Conjugate()).Dot(eta).Real;
    }

    /// <summary>The unit ray of the wave, refracted from the wave vector
    /// <paramref name="waveBefore"/> at a surface of unit normal <paramref name="normal"/> into
    /// <paramref name="after"/> in <paramref name="mode"/>, with its wave vector; false when that
    /// wave does not propagate (total internal reflection of the mode). The tangential part of
    /// the wave vector is kept and the energy goes on through the surface, the way
    /// <paramref name="rayBefore"/> went.</summary>
    public static bool TryRefract(Vector3D waveBefore, Vector3D rayBefore, Vector3D normal, OpticalSide after, CrystalMode mode,
        out Vector3D ray, out Vector3D waveAfter)
    {
        double side = Vector3D.Dot(rayBefore, normal);
        var eta = side < 0 ? -normal : normal;
        var kappa = waveBefore - eta * Vector3D.Dot(waveBefore, eta);
        var w = Wave(after, mode, kappa, eta, +1);
        if (!w.Propagating || side == 0)
        {
            ray = rayBefore;
            waveAfter = waveBefore;
            return false;
        }
        ray = w.Ray;
        waveAfter = w.RealWaveVector;
        return true;
    }
}
