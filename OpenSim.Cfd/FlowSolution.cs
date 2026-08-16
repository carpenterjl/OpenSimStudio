using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// A converged incompressible flow field on the MAC grid: face-normal velocities
/// (staggered — <see cref="U"/>[<see cref="CartesianGrid.UIndex"/>] etc.), cell-centered
/// pressure, and the solve diagnostics every consumer of a CFD result should see.
/// </summary>
/// <param name="Grid">The grid the field lives on (cell classification included).</param>
/// <param name="U">x-velocity on x-faces [m/s], full face array (walls/inlets included).</param>
/// <param name="V">y-velocity on y-faces [m/s].</param>
/// <param name="W">z-velocity on z-faces [m/s].</param>
/// <param name="Pressure">Cell-centered pressure [Pa], zero level at the outlets (or the
/// pinned cell of an enclosure); solid cells carry 0.</param>
/// <param name="Steps">Pseudo-time steps the steady march took.</param>
/// <param name="Residual">Final per-step relative velocity change (the march residual).</param>
/// <param name="MaxDivergence">max |∇·u| over fluid cells [1/s] — the mass-conservation
/// identity; solver-tolerance-sized by construction, and gated as such.</param>
/// <param name="InflowRate">Total volume flow entering the domain boundary [m³/s].</param>
/// <param name="OutflowRate">Total volume flow leaving the domain boundary [m³/s].</param>
/// <param name="Notes">Every automatic choice and warning of this solve.</param>
/// <param name="Temperature">Cell-centered fluid temperature [K] when the solve carried
/// an energy equation; null for a flow-only solve. Solid cells hold the ambient value —
/// the solid's real temperature field lives on the FE mesh, not here.</param>
public sealed record FlowSolution(
    CartesianGrid Grid,
    double[] U, double[] V, double[] W, double[] Pressure,
    int Steps, double Residual, double MaxDivergence,
    double InflowRate, double OutflowRate,
    IReadOnlyList<string> Notes,
    double[]? Temperature = null)
{
    /// <summary>Cell-centered velocity — the average of the cell's opposing face values,
    /// which is what glyphs, streamline seeds and slice samplers want.</summary>
    public Vector3D CellVelocity(int i, int j, int k) => new(
        0.5 * (U[Grid.UIndex(i, j, k)] + U[Grid.UIndex(i + 1, j, k)]),
        0.5 * (V[Grid.VIndex(i, j, k)] + V[Grid.VIndex(i, j + 1, k)]),
        0.5 * (W[Grid.WIndex(i, j, k)] + W[Grid.WIndex(i, j, k + 1)]));

    /// <summary>Largest velocity magnitude over cell centers [m/s].</summary>
    public double MaxSpeed()
    {
        double max = 0;
        for (int k = 0; k < Grid.Nz; k++)
            for (int j = 0; j < Grid.Ny; j++)
                for (int i = 0; i < Grid.Nx; i++)
                {
                    if (!Grid.IsFluid(i, j, k)) continue;
                    double s = CellVelocity(i, j, k).Length;
                    if (s > max) max = s;
                }
        return max;
    }
}
