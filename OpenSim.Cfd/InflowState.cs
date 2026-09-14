using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// The state the CFD working fluid's properties are evaluated at: the inflow-weighted
/// mean temperature of everything PRESCRIBED to enter the domain.
/// <para>
/// The flow solver freezes ν, ρ, α and β at construction (its Helmholtz operators are
/// pre-assembled on them), so one temperature has to stand for the whole fluid. The
/// honest one is the stream's own. Evaluating at the surroundings' ambient — the air
/// around a water circuit — put every internal case at the wrong state: water arriving
/// at 363 K has a ν 2.6× lower than water at 300 K, so a passage reported as laminar at
/// Re = 1.7e3 actually ran at ≈ 4.5e3, above the solver's own warning line. And even an
/// external flow may carry an inlet opening hotter than the ambient it sits in.
/// </para>
/// <para>
/// The rule is the same in every fluid-selection mode: over every domain-boundary face
/// of a fluid cell whose boundary is a <see cref="FlowFaceKind.InletVelocity"/>, weight =
/// the prescribed INWARD normal volume flow max(q_n, 0)·h² (a tangential lid or an
/// inlet pointing outward carries nothing), temperature = the opening's stream
/// temperature, else the face's explicit temperature, else the ambient — the precedence
/// the energy equation itself uses at that face (<see cref="CfdSettings.BoundaryAt"/>
/// is the shared lookup). Faces against solid cells do not count, so the weighting is the
/// mouth the flow actually enters through, not the rectangle the user typed. No inflow at
/// all (a sealed cavity, a still box open to ambient, a lid-driven cavity) leaves the
/// ambient, so those cases are bitwise what they were; a single stream temperature is
/// returned exactly, not through a weighted quotient.
/// </para>
/// Re-evaluating at a film temperature per outer iteration would mean rebuilding the
/// solver each time; that is a named refinement, not done here.
/// </summary>
public static class InflowState
{
    /// <param name="Temperature">The property-evaluation temperature [K].</param>
    /// <param name="Ambient">The ambient it falls back to [K].</param>
    /// <param name="InflowRate">Σ max(q_n, 0)·h² over the inlet faces [m³/s]; zero means
    /// the ambient was used.</param>
    /// <param name="InletFaces">Boundary fluid faces that carried prescribed inflow.</param>
    /// <param name="Minimum">Coldest stream temperature that contributed [K]
    /// (= <paramref name="Temperature"/> when nothing did).</param>
    /// <param name="Maximum">Hottest stream temperature that contributed [K].</param>
    public sealed record Result(double Temperature, double Ambient, double InflowRate,
        int InletFaces, double Minimum, double Maximum)
    {
        /// <summary>True when prescribed inflow set the temperature; false when the
        /// ambient stood in.</summary>
        public bool FromInflow => InflowRate > 0;

        /// <summary>How the temperature was arrived at, for the solve log.</summary>
        public string Describe() => FromInflow
            ? Minimum == Maximum
                ? $"the stream temperature over {InletFaces} inlet faces carrying {InflowRate:G3} m³/s"
                : $"inflow-weighted over {InletFaces} inlet faces carrying {InflowRate:G3} m³/s, " +
                  $"streams from {Minimum:F2} to {Maximum:F2} K"
            : $"no prescribed inflow, so the ambient {Ambient:F2} K stands in";
    }

    /// <summary>The inflow-weighted property temperature of a gridded domain under the
    /// given boundary policy and energy options (ambient and explicit face temperatures).</summary>
    public static Result PropertyTemperature(CartesianGrid grid, CfdSettings settings,
        FlowThermalOptions thermal)
    {
        double ambient = thermal.AmbientTemperature;
        double area = grid.H * grid.H;
        double weight = 0, weighted = 0;
        int faces = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;

        void Consider(int axis, bool high, Vector3D center)
        {
            var face = axis switch
            {
                0 => high ? BoxFace.XMax : BoxFace.XMin,
                1 => high ? BoxFace.YMax : BoxFace.YMin,
                _ => high ? BoxFace.ZMax : BoxFace.ZMin
            };
            var (u, v) = axis switch
            {
                0 => (center.Y, center.Z),
                1 => (center.X, center.Z),
                _ => (center.X, center.Y)
            };
            var (kind, velocity, streamT) = settings.BoundaryAt(face, u, v);
            if (kind != FlowFaceKind.InletVelocity) return;
            double normal = axis == 0 ? velocity.X : axis == 1 ? velocity.Y : velocity.Z;
            double inward = high ? -normal : normal;
            if (inward <= 0) return;
            double t = streamT ?? thermal.FaceTemperature(axis, high) ?? ambient;
            double q = inward * area;
            weight += q;
            weighted += q * t;
            faces++;
            if (t < min) min = t;
            if (t > max) max = t;
        }

        // Canonical (k, j, i) order, sequential: the result is deterministic.
        for (int k = 0; k < grid.Nz; k++)
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                {
                    if (!grid.IsFluid(i, j, k)) continue;
                    if (i == 0) Consider(0, false, grid.UFaceCenter(0, j, k));
                    if (i == grid.Nx - 1) Consider(0, true, grid.UFaceCenter(grid.Nx, j, k));
                    if (j == 0) Consider(1, false, grid.VFaceCenter(i, 0, k));
                    if (j == grid.Ny - 1) Consider(1, true, grid.VFaceCenter(i, grid.Ny, k));
                    if (k == 0) Consider(2, false, grid.WFaceCenter(i, j, 0));
                    if (k == grid.Nz - 1) Consider(2, true, grid.WFaceCenter(i, j, grid.Nz));
                }

        if (weight <= 0)
            return new Result(ambient, ambient, 0, 0, ambient, ambient);
        // One stream temperature is returned as itself: a weighted quotient of equal
        // values need not round back to the value, and "same inlet temperature as
        // today" must mean bitwise the same properties.
        double temperature = min == max ? min : weighted / weight;
        return new Result(temperature, ambient, weight, faces, min, max);
    }
}
