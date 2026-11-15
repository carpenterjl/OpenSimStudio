using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Birefringent;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// The polarization ray trace through birefringent crystals (OP-53, docs/Optics-Conventions.md
/// §62): the 3 × 3 matrix of a Birefringent In or Out surface, and the phase the carried mode
/// gains on a segment inside a crystal in modes 2 and 3. Everything is in the global frame
/// (surface 1's), like the rest of <see cref="PolarizationTracer"/>.
/// <para>
/// <b>Field in a crystal.</b> The field is carried transverse to the traced ray ŝ, on the basis
/// OpticStudio's help describes ("Transmission and other properties of birefringent surfaces"):
/// "The plane that contains the refracted ray and the crystal axis vector is the parallel plane;
/// and the P vector lies in this plane normal to the ray vector. The S vector is perpendicular to
/// both P and the ray vector." Here Ŝ = ŝ × â/|ŝ × â| (the ordinary field's direction) and
/// P̂ = (â − (â·ŝ)ŝ)/|…| (the extraordinary field's direction, which is at right angles to the
/// ray). Along the optic axis Ŝ and P̂ are the interface's s and p
/// (<see cref="UniaxialOptics.Field"/>).
/// </para>
/// <para>
/// <b>At an interface.</b> The incident field is split into the incident modes (its Ŝ and P̂
/// parts in a crystal; the s and p of the incident wave in an isotropic medium); each is carried
/// through <see cref="AnisotropicInterface"/> at the traced wave's tangential wave vector,
/// giving power-normalised amplitudes into the transmitted modes. Into a crystal, modes 0 and 1
/// keep the traced mode only ("the fraction of ray energy in the part of the ray that is not
/// traced is discarded"); modes 2 and 3 also keep the other mode's amplitude, placed on its basis
/// vector of the traced ray ("Modes 2 and 3 assume that the angular deviation between the
/// ordinary and extraordinary beam is so small that the region of interest can be approximated
/// by a single beam"). Into an isotropic medium (a Birefringent Out) both parts go on, as one
/// field of the exit ray. The matrix is Σ a_mb ô_m b̂_bᵀ + k̂_out k̂_inᵀ.
/// </para>
/// <para>
/// <b>Phase in modes 2 and 3 (derived).</b> The traced mode's phase is the ray's optical path,
/// N_t·Δr (the trace accumulates it). The other mode was launched at the last interface with the
/// same tangential wave vector, so its phase over the same displacement Δr is N_o·Δr; its part
/// of the field is multiplied by e^(i k₀ (N_o − N_t)·Δr) (field e^(i(k·r − ωt)), §21), k₀ = 2π/λ
/// in the system's air. For a plane-parallel plate this is exactly the plane-wave retardance
/// k₀ (q_o − q_t) d whichever mode is traced. The help says only that "the ordinary index is used
/// to phase rotate the S component of the field, and the effective index (properly referenced to
/// the ordinary ray direction) is used to rotate the P component".
/// </para>
/// <para>
/// <b>Approximations.</b> In modes 2 and 3 the other mode's part travels with the traced ray (no
/// walk-off), and at the next interface it is taken at the traced wave's tangential wave vector
/// there (the same as its own for parallel faces). A part whose mode does not propagate at an
/// interface (total internal reflection of that mode) carries no power on.
/// </para>
/// </summary>
internal static class BirefringentPolarization
{
    /// <summary>What modes 2 and 3 carry along a crystal: the traced and the other wave vectors
    /// and the other mode's basis vector (global frame).</summary>
    internal sealed record Carry(Vector3D Traced, Vector3D Other, CrystalMode OtherMode, UniaxialCrystal Crystal);

    /// <summary>The basis (Ŝ, P̂) of the field carried along ray <paramref name="ray"/>.</summary>
    internal static (Vector3D S, Vector3D P) Basis(Vector3D ray, UniaxialCrystal? crystal, Vector3D eta, Vector3D fallback)
    {
        if (crystal is { } c)
        {
            var s = Vector3D.Cross(ray, c.Axis);
            if (s.Length >= UniaxialOptics.AlongAxis)
            {
                var p = c.Axis - ray * Vector3D.Dot(c.Axis, ray);
                return (s / s.Length, p / p.Length);
            }
        }
        var s2 = Vector3D.Cross(ray, eta);
        if (s2.Length < UniaxialOptics.AlongAxis) s2 = fallback - ray * Vector3D.Dot(fallback, ray);
        s2 /= s2.Length;
        var p2 = Vector3D.Cross(ray, s2);
        return (s2, p2 / p2.Length);
    }

    /// <summary>A unit vector at right angles to η̂: the part of x̂ (or ŷ when η̂ is near x̂).</summary>
    internal static Vector3D Fallback(Vector3D eta)
    {
        var a = Math.Abs(eta.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var f = a - eta * Vector3D.Dot(a, eta);
        return f / f.Length;
    }

    /// <summary>The matrix multiplying the other mode's part by e^(iφ) after a displacement
    /// <paramref name="displacement"/> (global, meters) along the traced ray.</summary>
    internal static PolarizationMatrix Propagation(Carry carry, Vector3D ray, Vector3D displacement, double airWavelength)
    {
        double phi = 2 * Math.PI / airWavelength * Vector3D.Dot(carry.Other - carry.Traced, displacement);
        var (s, p) = Basis(ray, carry.Crystal, ray, Fallback(ray));
        var b = carry.OtherMode == CrystalMode.Ordinary ? s : p;
        var f = Complex.FromPolarCoordinates(1, phi) - Complex.One;
        var m = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = (r == c ? Complex.One : Complex.Zero) + f * (b[r] * b[c]);
        return PolarizationMatrix.FromRowMajor(m);
    }

    /// <summary>The result at one birefringent surface.</summary>
    internal sealed record Result(PolarizationMatrix Matrix, Carry? Carry, Complex DiagonalS, Complex DiagonalP);

    /// <summary>
    /// The matrix of a Birefringent In or Out surface for the traced ray arriving along
    /// <paramref name="kIn"/> with wave vector <paramref name="waveIn"/> and leaving along
    /// <paramref name="kOut"/> (all global, unit rays), surface normal <paramref name="normal"/>.
    /// <paramref name="before"/> and <paramref name="after"/> are the two sides (crystal axes in
    /// the global frame); <paramref name="space"/> the crystal after, or null.
    /// </summary>
    internal static Result Interface(int surface, Vector3D kIn, Vector3D waveIn, Vector3D kOut, Vector3D normal,
        OpticalSide before, OpticalSide after, CrystalSpace? space)
    {
        var eta = Vector3D.Dot(kIn, normal) < 0 ? -normal : normal;
        var kappa = waveIn - eta * Vector3D.Dot(waveIn, eta);
        var fallback = Fallback(eta);
        AnisotropicInterface solved;
        try { solved = AnisotropicInterface.Solve(before, after, kappa, eta, fallback); }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException($"Polarization ray trace, surface {surface}: {e.Message}", e);
        }

        // The incident basis and the sign that takes a basis amplitude to the mode's own field.
        var inBasis = new Vector3D[2];
        var inSign = new double[2];
        if (before.Crystal is { } cb)
        {
            var (s, p) = Basis(kIn, cb, eta, fallback);
            inBasis[0] = s;
            inBasis[1] = p;
            for (int b = 0; b < 2; b++) inSign[b] = Sign(solved.Incident[b].Field, inBasis[b]);
        }
        else
            for (int b = 0; b < 2; b++)
            {
                inBasis[b] = solved.Incident[b].Field.Real;
                inSign[b] = 1;
            }

        var outBasis = new Vector3D[2];
        var outSign = new double[2];
        var keep = new bool[2];
        if (after.Crystal is { } ca)
        {
            var (s, p) = Basis(kOut, ca, eta, fallback);
            outBasis[0] = s;
            outBasis[1] = p;
            var traced = space?.TracedMode ?? CrystalMode.Ordinary;
            for (int m = 0; m < 2; m++)
            {
                outSign[m] = Sign(solved.Transmitted[m].Field, outBasis[m]);
                keep[m] = m == (int)traced || (space?.KeepsOtherMode ?? false);
            }
        }
        else
            for (int m = 0; m < 2; m++)
            {
                outBasis[m] = solved.Transmitted[m].Field.Real;
                outSign[m] = 1;
                keep[m] = true;
            }

        var coef = new Complex[2, 2];
        var entries = new Complex[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                entries[3 * r + c] = kOut[r] * kIn[c];
        for (int m = 0; m < 2; m++)
        {
            if (!keep[m]) continue;
            for (int b = 0; b < 2; b++)
            {
                var a = solved.Transmission((CrystalMode)m, (CrystalMode)b) * (outSign[m] * inSign[b]);
                coef[m, b] = a;
                if (a == Complex.Zero) continue;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        entries[3 * r + c] += a * (outBasis[m][r] * inBasis[b][c]);
            }
        }

        Carry? carry = null;
        if (after.Crystal is { } crystal && space is { KeepsOtherMode: true })
        {
            var other = space.TracedMode == CrystalMode.Ordinary ? CrystalMode.Extraordinary : CrystalMode.Ordinary;
            var o = solved.Transmitted[(int)other];
            var t = solved.Transmitted[(int)space.TracedMode];
            if (o.Propagating && t.Propagating)
                carry = new Carry(t.Wave.RealWaveVector, o.Wave.RealWaveVector, other, crystal);
        }
        return new Result(PolarizationMatrix.FromRowMajor(entries), carry, coef[0, 0], coef[1, 1]);
    }

    /// <summary>−1 when the mode's own field points against the basis vector, else +1.</summary>
    private static double Sign(ComplexVector3 field, Vector3D basis) => field.Dot(basis).Real < 0 ? -1 : 1;
}
