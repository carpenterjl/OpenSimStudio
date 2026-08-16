using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// Turns a resolved flow field's wall heat exchange into the <see cref="SurfaceFilmModel"/>
/// the FE thermal solvers consume — the Stage 1 ↔ Stage 2 seam, now filled from the
/// fluid side.
/// <para>
/// Per wall face the fluid-side exchange is pure near-wall conduction,
/// q″ = k_f·(T_wall − T_cell)/(h/2) — exactly the flux the fluid energy equation's
/// half-cell Dirichlet wall term uses, so the two models exchange the SAME heat by
/// construction. Expressed as a film: coefficient g = k_f/(h/2) toward the LOCAL fluid
/// cell temperature. This form is chosen over h = q″/(T_wall − T_ambient) on purpose:
/// it never divides by a vanishing temperature difference, it is always positive (the
/// Robin term stays SPD), and it self-corrects between conjugate iterations because the
/// flux responds to the solid surface temperature with the physical sign.
/// </para>
/// Aggregation to an FE boundary triangle is conductance-weighted: h_tri = Σ(g·a)/A_tri
/// (a = the voxel face area h², A_tri = the triangle's own area — the areas legitimately
/// differ, that mismatch IS the staircase approximation), and the reference temperature
/// is the conductance-weighted mean of the adjacent fluid cells, which reproduces the
/// summed face flux identically: h_tri·A_tri·(T_w − T̄) ≡ Σ g·a·(T_w − T_cell).
/// </summary>
public static class WallFluxExtractor
{
    /// <param name="domain">The voxelized domain whose wall faces carry the triangle mapping.</param>
    /// <param name="flow">The resolved flow (must carry a temperature field).</param>
    /// <param name="fluidConductivity">k_f [W/(m·K)] at the film state.</param>
    /// <param name="mesh">The FE mesh the film is for (the merged assembly mesh).</param>
    /// <param name="ambientTemperature">Fallback reference [K] (reported for unwetted use).</param>
    /// <param name="claimedFaceIds">Geometric faces the user owns with their own thermal
    /// BCs — excluded from the film entirely (the pad-electrode precedent).</param>
    public static SurfaceFilmModel Extract(VoxelizedDomain domain, FlowSolution flow,
        double fluidConductivity, FeMesh mesh, double ambientTemperature,
        IReadOnlySet<int>? claimedFaceIds = null)
    {
        if (flow.Temperature is null)
            throw new InvalidOperationException(
                "The flow solution carries no temperature field — solve with thermal options " +
                "before extracting a wall film.");

        int triCount = mesh.BoundaryTriangles.Count;
        var conductance = new double[triCount];       // Σ g·a  [W/K]
        var weightedT = new double[triCount];         // Σ g·a·T_cell

        double h = domain.Grid.H;
        double g = fluidConductivity / (h / 2);       // per-area film [W/(m²·K)]
        double faceArea = h * h;
        foreach (var wf in domain.WallFaces)
        {
            double ga = g * faceArea;
            conductance[wf.BoundaryTriangle] += ga;
            weightedT[wf.BoundaryTriangle] += ga * flow.Temperature[wf.FluidCell];
        }

        var coefficients = new double[triCount];
        var references = new double[triCount];
        int wetted = 0;
        for (int t = 0; t < triCount; t++)
        {
            if (conductance[t] <= 0
                || (claimedFaceIds?.Contains(mesh.BoundaryTriangles[t].FaceId) ?? false))
            {
                coefficients[t] = double.NaN;
                references[t] = ambientTemperature;
                continue;
            }
            double area = TriangleArea(mesh, mesh.BoundaryTriangles[t]);
            coefficients[t] = conductance[t] / area;
            references[t] = weightedT[t] / conductance[t];
            wetted++;
        }

        return new SurfaceFilmModel
        {
            TriangleFilmCoefficient = coefficients,
            TriangleReferenceTemperature = references,
            ReferenceTemperature = ambientTemperature,
            Origin = $"CFD wall heat flux ({wetted} wetted triangles, " +
                     $"staircase conduction at {h:G3} m cells)"
        };
    }

    internal static double TriangleArea(FeMesh mesh, BoundaryTriangle tri)
    {
        var a = mesh.Nodes[tri.A];
        return 0.5 * Vector3D.Cross(mesh.Nodes[tri.B] - a, mesh.Nodes[tri.C] - a).Length;
    }
}
