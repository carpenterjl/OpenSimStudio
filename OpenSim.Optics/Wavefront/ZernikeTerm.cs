namespace OpenSim.Optics.Wavefront;

/// <summary>
/// One Zernike term by its radial degree <see cref="N"/> and signed azimuthal frequency
/// <see cref="M"/>: M &gt; 0 is R_n^M(ρ)·cos Mθ, M &lt; 0 is R_n^|M|(ρ)·sin |M|θ and M = 0 is
/// R_n^0(ρ). n − |M| is even and |M| ≤ n. This is the pairing of Noll (1976) and of the
/// "Zernike polynomials" article's double-index notation; the single-index orderings map onto
/// it through <see cref="ZernikeIndex"/>.
/// </summary>
public readonly record struct ZernikeTerm
{
    public ZernikeTerm(int n, int m)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "The radial degree must be 0 or more.");
        if (Math.Abs(m) > n || ((n - Math.Abs(m)) & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(m), m, $"|m| must be at most n = {n} and n − |m| must be even.");
        N = n;
        M = m;
    }

    /// <summary>The radial degree n.</summary>
    public int N { get; }

    /// <summary>The signed azimuthal frequency: positive for cos, negative for sin.</summary>
    public int M { get; }

    /// <summary>|M|.</summary>
    public int AbsM => Math.Abs(M);

    /// <summary>True for a sin term.</summary>
    public bool IsSine => M < 0;

    public override string ToString() => $"Z(n={N}, m={M})";
}
