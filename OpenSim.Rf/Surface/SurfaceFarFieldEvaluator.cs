using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Far field of a solved RWG surface: the radiation vector
/// N(θ,φ) = Σ_T ∫ J(r′) e^{jk r̂·r′} dS (7-point Dunavant per triangle — at λ/10 edges
/// the phase factor is slow, the same argument as the wire evaluator's 8-point Gauss),
/// fed into the shared sphere/hemisphere integration in <see cref="FarFieldEvaluator"/>.
/// Reuses <see cref="FarFieldPattern"/> — the record is source-agnostic. Over a ground
/// plane every triangle contributes its image: mirrored position with the current
/// mapped to (−Jx, −Jy, +Jz), the −(mirror pushforward) of the solver's image pass.
/// </summary>
public static class SurfaceFarFieldEvaluator
{
    public static FarFieldPattern Compute(SurfaceStructure surface, SurfaceMomSolution solution,
        int thetaCount = 32, int phiCount = 64)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        return FarFieldEvaluator.IntegratePattern(omega, surface.Ground is not null,
            thetaCount, phiCount, direction => RadiationVector(surface, solution, k, direction));
    }

    private static (Complex X, Complex Y, Complex Z) RadiationVector(SurfaceStructure surface,
        SurfaceMomSolution solution, double k, Vector3D direction)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        Complex nx = Complex.Zero, ny = Complex.Zero, nz = Complex.Zero;

        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var supports = surface.TriangleSupports[t];
            if (supports.Count == 0) continue;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];

            for (int i = 0; i < w.Length; i++)
            {
                var point = va * l1[i] + vb * l2[i] + vc * l3[i];

                // J(r′) = Σ σ (l/2A)(r′ − p_opp) · I  — complex per Cartesian component.
                Complex jx = Complex.Zero, jy = Complex.Zero, jz = Complex.Zero;
                foreach (var (basis, sign, opposite) in supports)
                {
                    Complex coefficient = solution.EdgeCurrents[basis]
                        * (sign * surface.Edges[basis].Length / (2 * area));
                    var rho = point - surface.Vertices[opposite];
                    jx += coefficient * rho.X;
                    jy += coefficient * rho.Y;
                    jz += coefficient * rho.Z;
                }

                double weight = w[i] * area;
                Complex phase = Complex.Exp(new Complex(0, k * Vector3D.Dot(direction, point)));
                nx += weight * phase * jx;
                ny += weight * phase * jy;
                nz += weight * phase * jz;

                if (surface.Ground is { } ground)
                {
                    var image = ThinWireMomSolver.Mirror(point, ground.SurfaceZ);
                    Complex imagePhase = Complex.Exp(new Complex(0, k * Vector3D.Dot(direction, image)));
                    // Image current: −(mirror pushforward) = (−Jx, −Jy, +Jz).
                    nx += weight * imagePhase * (-jx);
                    ny += weight * imagePhase * (-jy);
                    nz += weight * imagePhase * jz;
                }
            }
        }
        return (nx, ny, nz);
    }
}
