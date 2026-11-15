using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>One plane wave of an interface problem: its mode, wave and unit field.</summary>
public sealed record InterfaceWave(CrystalMode Mode, ModeWave Wave, ComplexVector3 Field)
{
    public bool Propagating => Wave.Propagating;
}

/// <summary>
/// The reflection and transmission of plane waves at a plane interface between two media, each
/// isotropic or uniaxial (OP-53, docs/Optics-Conventions.md §62). For each incident mode the
/// two reflected and two transmitted waves share its tangential wave vector κ; their amplitudes
/// make the tangential parts of E and H = N × E continuous (Maxwell's boundary conditions for a
/// non-magnetic medium without surface currents), four complex equations for the four
/// amplitudes, solved here by Gaussian elimination with partial pivoting. The amplitudes are
/// returned power-normalised as the polarization ray trace wants them (§21): ã = a·√(|F|/F_inc),
/// F the power flux of a unit-field wave along η̂ (<see cref="UniaxialOptics.Flux"/>), so that
/// |ã|² is the fraction of the incident power the wave carries away and the four |ã|² sum to 1
/// for a lossless interface. An evanescent wave carries no power and gets ã = 0.
/// <para>
/// This is derived here from the boundary conditions; OpticStudio's help does not give its
/// amplitude coefficients at a birefringent interface, and the papers that do (McClain, Hillman
/// and Chipman, JOSA A 10 (1993) 2371 and 2383) were not available. Two checks pin it: with
/// n_o = n_e it gives Fresnel's coefficients, and power is conserved for any crystal
/// orientation (BirefringentTests).
/// </para>
/// </summary>
public sealed class AnisotropicInterface
{
    private AnisotropicInterface(InterfaceWave[] incident, InterfaceWave[] reflected, InterfaceWave[] transmitted,
        Complex[,] r, Complex[,] t)
    {
        Incident = incident;
        Reflected = reflected;
        Transmitted = transmitted;
        _r = r;
        _t = t;
    }

    private readonly Complex[,] _r, _t;

    /// <summary>The two incident waves (index = <see cref="CrystalMode"/>; s and p in an isotropic medium).</summary>
    public IReadOnlyList<InterfaceWave> Incident { get; }
    public IReadOnlyList<InterfaceWave> Reflected { get; }
    public IReadOnlyList<InterfaceWave> Transmitted { get; }

    /// <summary>Power-normalised amplitude of transmitted wave <paramref name="outMode"/> for a
    /// unit-field incident wave <paramref name="inMode"/> (zero for an evanescent wave on either side).</summary>
    public Complex Transmission(CrystalMode outMode, CrystalMode inMode) => _t[(int)outMode, (int)inMode];

    /// <summary>Power-normalised amplitude of reflected wave <paramref name="outMode"/>.</summary>
    public Complex Reflection(CrystalMode outMode, CrystalMode inMode) => _r[(int)outMode, (int)inMode];

    /// <summary>
    /// Solves the interface for the tangential wave vector <paramref name="kappa"/> (at right
    /// angles to <paramref name="eta"/>, the unit normal pointing from <paramref name="before"/>
    /// into <paramref name="after"/>). <paramref name="fallback"/> is a real unit vector at
    /// right angles to η̂ used as the s direction at normal incidence along the optic axis
    /// (<see cref="UniaxialOptics.Field"/>). Throws when the boundary equations are singular.
    /// </summary>
    public static AnisotropicInterface Solve(OpticalSide before, OpticalSide after, Vector3D kappa, Vector3D eta, Vector3D fallback)
    {
        InterfaceWave Make(OpticalSide side, CrystalMode mode, int sense)
        {
            var w = UniaxialOptics.Wave(side, mode, kappa, eta, sense);
            return new InterfaceWave(mode, w, UniaxialOptics.Field(side, mode, w.WaveVector, eta, fallback));
        }
        var modes = new[] { CrystalMode.Ordinary, CrystalMode.Extraordinary };
        var incident = modes.Select(m => Make(before, m, +1)).ToArray();
        var reflected = modes.Select(m => Make(before, m, -1)).ToArray();
        var transmitted = modes.Select(m => Make(after, m, +1)).ToArray();

        // Two real tangential directions.
        var u1 = kappa.Length > 1e-12 ? kappa / kappa.Length : fallback;
        var u2 = Vector3D.Cross(eta, u1);
        u2 /= u2.Length;

        // Columns: r1, r2, t1, t2. Rows: E·u1, E·u2, H·u1, H·u2 continuity.
        var unknowns = new[] { reflected[0], reflected[1], transmitted[0], transmitted[1] };
        var matrix = new Complex[4, 4];
        for (int c = 0; c < 4; c++)
        {
            var w = unknowns[c];
            var h = ComplexVector3.Cross(w.Wave.WaveVector, w.Field);
            double sign = c < 2 ? 1 : -1;
            matrix[0, c] = sign * w.Field.Dot(u1);
            matrix[1, c] = sign * w.Field.Dot(u2);
            matrix[2, c] = sign * h.Dot(u1);
            matrix[3, c] = sign * h.Dot(u2);
        }

        var r = new Complex[2, 2];
        var t = new Complex[2, 2];
        var flux = unknowns.Select(w => w.Propagating ? Math.Abs(UniaxialOptics.Flux(w.Field, w.Wave.WaveVector, eta)) : 0).ToArray();
        for (int b = 0; b < 2; b++)
        {
            var inc = incident[b];
            if (!inc.Propagating) continue;
            double fIn = UniaxialOptics.Flux(inc.Field, inc.Wave.WaveVector, eta);
            if (!(fIn > 0))
                throw new InvalidOperationException($"Birefringent interface: the incident {inc.Mode} wave carries no power towards the surface (flux {fIn}).");
            var hIn = ComplexVector3.Cross(inc.Wave.WaveVector, inc.Field);
            var rhs = new[] { -inc.Field.Dot(u1), -inc.Field.Dot(u2), -hIn.Dot(u1), -hIn.Dot(u2) };
            var x = Solve4((Complex[,])matrix.Clone(), rhs);
            for (int m = 0; m < 2; m++)
            {
                r[m, b] = x[m] * Math.Sqrt(flux[m] / fIn);
                t[m, b] = x[2 + m] * Math.Sqrt(flux[2 + m] / fIn);
            }
        }
        return new AnisotropicInterface(incident, reflected, transmitted, r, t);
    }

    /// <summary>Gaussian elimination with partial pivoting on a 4 × 4 complex system.</summary>
    private static Complex[] Solve4(Complex[,] a, Complex[] b)
    {
        const int n = 4;
        double scale = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) scale = Math.Max(scale, a[i, j].Magnitude);
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++)
                if (a[i, k].Magnitude > a[p, k].Magnitude) p = i;
            if (!(a[p, k].Magnitude > 1e-13 * scale))
                throw new InvalidOperationException("Birefringent interface: the boundary equations are singular (a grazing wave or a degenerate field).");
            if (p != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
                (b[k], b[p]) = (b[p], b[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                var f = a[i, k] / a[k, k];
                if (f == Complex.Zero) continue;
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }
}
