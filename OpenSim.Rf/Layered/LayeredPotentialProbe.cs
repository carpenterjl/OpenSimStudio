using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Scalar potential Φ(r) of a layered solve at an in-plane point: Φ = K_Φ ∗ q with
/// q = −(1/jω)∇·J constant per triangle (±l/A per RWG support). The PEC ground is the
/// zero reference, so Φ at a patch edge IS the edge voltage — which makes the classic
/// cavity-model edge resistance R_edge = |V_edge|²/(2P_in) measurable from the field
/// solution without a vertical feed (v1 has no vertical currents by scope; the
/// series-gap port's own R is a different, smaller number).
/// Singularity handling: the c₀/ρ static primary integrates analytically per triangle
/// (Wilton–Rao I₀); everything else — the primary's phase remainder, the 2d image,
/// the tabulated Sommerfeld part — is regular and takes a 7-point Dunavant rule.
/// </summary>
internal static class LayeredPotentialProbe
{
    public static Complex ScalarPotential(SurfaceStructure surface, LayeredKernelTable kernel,
        SurfaceMomSolution solution, Vector3D point)
    {
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        var (c0, _) = kernel.PhiImages;
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);

        Complex potential = Complex.Zero;
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var supports = surface.TriangleSupports[t];
            if (supports.Count == 0) continue;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];

            // q_T = −(1/jω) Σ σ l/A · I  (RWG divergence is ±l/A on its triangle).
            Complex charge = Complex.Zero;
            foreach (var (basis, sign, _) in supports)
                charge += sign * surface.Edges[basis].Length / area * solution.EdgeCurrents[basis];
            charge /= Complex.ImaginaryOne * omega;

            // Static primary: analytic ∫dS′/ρ (in-plane point ⇒ ρ = R).
            var (i0, _, _) = TrianglePotentials.Integrals(va, vb, vc, point);
            potential += charge * c0 / (4 * Math.PI * RfConstants.Eps0) * i0;

            // Regular remainder pointwise: K_Φ(ρ) − c₀/(4πε₀ρ).
            for (int i = 0; i < w.Length; i++)
            {
                var source = va * l1[i] + vb * l2[i] + vc * l3[i];
                double rho = (point - source).Length;
                var (_, kPhi) = kernel.EvaluateKernels(Math.Max(rho, 1e-12));
                Complex regular = kPhi - c0 / (4 * Math.PI * RfConstants.Eps0 * Math.Max(rho, 1e-12));
                potential += charge * w[i] * area * regular;
            }
        }
        return potential;
    }
}
