using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Sequential.Solves;

/// <summary>Settings of one application of solves.</summary>
public sealed record SolveOptions
{
    /// <summary>OpticStudio's "iterate solves when updating": repeat the pass in surface order
    /// until no solved cell changes, so a solve that reads a later surface (a position solve
    /// whose reference follows it, a pickup into the stop's semi-diameter under float by stop
    /// size, an F/# aperture with a marginal-ray solve) sees the final values. False makes one
    /// pass.</summary>
    public bool Iterate { get; init; } = true;

    /// <summary>The most passes before the engine reports that the solves did not converge.</summary>
    public int MaxPasses { get; init; } = 50;

    /// <summary>Relative tolerance of the convergence test on numeric cells: a pass converges
    /// when every solved number changes by at most RelativeTolerance·max(|a|, |b|) +
    /// <see cref="AbsoluteTolerance"/>.</summary>
    public double RelativeTolerance { get; init; } = 1e-12;

    /// <summary>Absolute tolerance of the convergence test, in the cell's SI unit (meters or 1/m).</summary>
    public double AbsoluteTolerance { get; init; } = 1e-15;

    /// <summary>The model-glass fit that <see cref="ModelGlassSolve"/> uses, trained on the
    /// active catalogs (<see cref="ModelGlassFit.Train"/>). A model glass solve without it fails.</summary>
    public ModelGlassFit? ModelGlass { get; init; }

    /// <summary>The active glass catalogs, against which a <see cref="SubstituteGlassSolve"/>'s
    /// catalog name is checked. Null skips the check.</summary>
    public IReadOnlyList<OpticalCatalog>? Catalogs { get; init; }
}

/// <summary>What one solve did in the last pass.</summary>
public sealed record SolveStatus
{
    public required SurfaceSolve Solve { get; init; }

    /// <summary>True when the solve set its cell.</summary>
    public bool Ok => Error is null;

    /// <summary>The number written (SI units), or null for a material, an automatic
    /// semi-diameter, a substitute flag or a failure.</summary>
    public double? Value { get; init; }

    /// <summary>The reason the solve failed, naming the surface, column and solve; null when ok.</summary>
    public string? Error { get; init; }

    /// <summary>A remark on a solve that worked (e.g. a flag with no value, or a single
    /// configuration standing in for several).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The lens after the solves, each solve's status, and whether the passes converged. When
/// <see cref="Succeeded"/> is false the lens has every solve that worked applied and the cells
/// of failed solves as they were; it must not be used as a solved lens.
/// </summary>
public sealed record SolveResult
{
    public required SequentialLens Lens { get; init; }

    /// <summary>One status per solve, in evaluation order (surface, then column).</summary>
    public required IReadOnlyList<SolveStatus> Statuses { get; init; }

    /// <summary>Problems with the solve set itself or the passes (validation, no convergence),
    /// and every failed solve's error, one line each.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The passes made.</summary>
    public int Passes { get; init; }

    public bool Succeeded => Errors.Count == 0;

    /// <summary>Throws <see cref="InvalidOperationException"/> listing every error, or returns
    /// the solved lens.</summary>
    public SequentialLens LensOrThrow()
    {
        if (!Succeeded)
            throw new InvalidOperationException("Solves failed:" + Environment.NewLine + string.Join(Environment.NewLine, Errors));
        return Lens;
    }
}
