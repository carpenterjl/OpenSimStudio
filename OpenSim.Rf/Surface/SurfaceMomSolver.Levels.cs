using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// FU-34 — sheet metal on SEVERAL interfaces of one grounded stackup in one solve. Each
/// triangle sits at the height of the interface its metal is on (z measured from the ground
/// plane, which is z = 0); a pair on one level is assembled exactly as a single-level layered
/// solve (that level's <see cref="MultiLayerKernelTable"/>), and a pair across two levels through
/// the <see cref="CrossLevelKernelTable"/> between them:
///  • the two images — the direct one between the planes, and the ground's — as free-space pair
///    moments of the source triangle where it is (direct) and mirrored in the ground (shifted
///    down by twice its height), with the full near-regime dispatch, since two levels a tenth of
///    a millimetre apart are nearly singular on triangle scale;
///  • the tabulated remainder, a function of the LATERAL distance only, by a product rule.
/// Neither track is singular: no vertex, edge or point is shared between levels.
/// Every cross entry is computed once and written to both (m, n) and (n, m): the kernels are
/// reciprocal (gated on the tables), so the matrix is complex-symmetric as a single level's is.
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Facts every consumer of a multi-level result must show next to it.</summary>
    public static IReadOnlyList<string> LevelsAssumptions { get; } = new[]
    {
        "Zero-thickness sheet metal on two or more interfaces of one grounded stackup; perfect conductors unless a sheet surface impedance is given.",
        "Levels couple through the layered Green's function between their planes (surface waves included).",
        "Vias between levels are thin tubes attached by the 1/ρ junction mode at a mesh vertex of each level; their attachment fans must not touch.",
        "Delta-gap ports across interior edges on any level; no pin to the ground plane in the same solve."
    };

    /// <summary>The ports' admittance matrix of sheet metal on several interfaces of
    /// <paramref name="stackup"/>. <paramref name="rhoMax"/> bounds every table (the structure's
    /// largest lateral extent, with margin).</summary>
    public SurfaceMultiPortSolution SolveLevels(SurfaceStructure surface, LayeredStackup stackup,
        double frequencyHz, IReadOnlyList<SurfacePort> ports, double rhoMax)
    {
        var assembled = AssembleLevels(surface, stackup, frequencyHz, rhoMax, MaxDegreeOfParallelism);
        foreach (var port in ports)
            foreach (int e in port.EdgeBases)
                if (e < 0 || e >= surface.BasisCount || surface.Edges[e].MinusTriangle < 0)
                    throw new ArgumentException("A port needs interior edges of the sheet.", nameof(ports));
        return SolveMultiPortAssembled(surface, ports, frequencyHz, assembled.Matrix);
    }

    /// <summary>Which interface each triangle's metal is on.</summary>
    internal static int[] TriangleLevels(SurfaceStructure surface, LayeredStackup stackup)
    {
        if (surface.Ground is not null)
            throw new ArgumentException("A layered solve must not carry a SurfaceStructure ground plane.", nameof(surface));
        var heights = stackup.InterfaceHeights();
        double tolerance = 1e-9 * stackup.TotalThicknessMeters;
        var levels = new int[surface.Triangles.Count];
        for (int t = 0; t < levels.Length; t++)
        {
            var (a, b, c) = surface.Triangles[t];
            double z = surface.Vertices[a].Z;
            if (Math.Abs(surface.Vertices[b].Z - z) > tolerance || Math.Abs(surface.Vertices[c].Z - z) > tolerance)
                throw new ArgumentException($"Triangle {t} is not horizontal: metal on several levels is flat on each.", nameof(surface));
            int level = Array.FindIndex(heights, h => Math.Abs(h - z) <= tolerance);
            if (level < 0)
                throw new ArgumentException(
                    $"Triangle {t} at z = {z * 1e3:g6} mm is on no interface of the stackup (heights from the ground, "
                    + $"{string.Join(", ", heights.Select(h => (h * 1e3).ToString("g6")))} mm).", nameof(surface));
            levels[t] = level;
        }
        return levels;
    }

    internal sealed record LevelsAssembly(ComplexDenseMatrix Matrix, int[] TriangleLevel,
        IReadOnlyDictionary<int, MultiLayerKernelTable> Own, IReadOnlyDictionary<(int, int), CrossLevelKernelTable> Cross);

    internal static LevelsAssembly AssembleLevels(SurfaceStructure surface, LayeredStackup stackup,
        double frequencyHz, double rhoMax, int? maxDegreeOfParallelism)
    {
        var levels = TriangleLevels(surface, stackup);
        int n = stackup.Layers.Count;
        var used = levels.Distinct().OrderBy(l => l).ToArray();
        var own = used.ToDictionary(l => l, l => new MultiLayerKernelTable(stackup, frequencyHz, rhoMax,
            maxDegreeOfParallelism, sourceInterface: l == n - 1 ? null : l));
        var cross = new Dictionary<(int, int), CrossLevelKernelTable>();
        for (int i = 0; i < used.Length; i++)
            for (int j = i + 1; j < used.Length; j++)
                cross[(used[i], used[j])] = new CrossLevelKernelTable(stackup, frequencyHz, rhoMax,
                    sourceInterface: used[i], observationInterface: used[j], maxDegreeOfParallelism);
        var ownSplits = own.ToDictionary(kv => kv.Key, kv => new LayeredKernelSplit(kv.Value));

        double omega = 2 * Math.PI * frequencyHz;
        var z = new ComplexDenseMatrix(surface.BasisCount, surface.BasisCount);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var pairs = PairMomentSchedule.Build(surface);
        var slots = PairMomentSchedule.Compute(pairs, maxDegreeOfParallelism, (p, q) =>
        {
            int lp = levels[p], lq = levels[q];
            if (lp == lq)
            {
                var (a, phi) = LayeredPairMoments(surface, p, q, ownSplits[lp]);
                if (p == q) { a = a.Symmetrized(); phi = phi.Symmetrized(); }
                return (a, phi);
            }
            var table = cross[(Math.Min(lp, lq), Math.Max(lp, lq))];
            return CrossLevelPairMoments(surface, p, q, table);
        });
        for (int i = 0; i < pairs.Length; i++)
            ScatterLayeredPair(z, surface, pairs[i].P, pairs[i].Q, slots[i].Item1, slots[i].Item2, vectorFactor, chargeFactor);
        return new LevelsAssembly(z, levels, own, cross);
    }

    /// <summary>The g-normalized moments (e^{−jk₀R}/R scale, as the single-level split) of a
    /// triangle pair on two levels: the direct and ground images as free-space pair moments, the
    /// smooth remainder by a degree-6 product rule over the lateral distance.</summary>
    private static (SurfaceMoments A, SurfaceMoments Phi) CrossLevelPairMoments(
        SurfaceStructure surface, int p, int q, CrossLevelKernelTable table)
    {
        var pVerts = PVertices(surface, p);
        var qVerts = PVertices(surface, q);
        var momentsA = new SurfaceMoments();
        var momentsPhi = new SurfaceMoments();

        // Direct: the source where it is. Ground image: the source mirrored in z = 0.
        double zq = qVerts.Item1.Z;
        var mirrored = new Vector3D(0, 0, -2 * zq);
        var direct = GeometricPairMoments(pVerts, qVerts, table.K0);
        var image = GeometricPairMoments(pVerts, (qVerts.Item1 + mirrored, qVerts.Item2 + mirrored, qVerts.Item3 + mirrored), table.K0);
        Complex directA = table.GaImages[0].Coeff, imageA = table.GaImages[1].Coeff;
        Complex directPhi = table.PhiImages[0].Coeff, imagePhi = table.PhiImages[1].Coeff;
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                momentsA[a, b] = directA * direct[a, b] + imageA * image[a, b];
                momentsPhi[a, b] = directPhi * direct[a, b] + imagePhi * image[a, b];
            }

        // Remainder over the lateral separation, scaled to the g normalization (4π/µ₀, 4πε₀).
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(6);
        double areaP = surface.TriangleAreas[p], areaQ = surface.TriangleAreas[q];
        double scaleA = 4 * Math.PI / RfConstants.Mu0, scalePhi = 4 * Math.PI * RfConstants.Eps0;
        Span<double> lp = stackalloc double[3];
        Span<double> lq = stackalloc double[3];
        for (int i = 0; i < w.Length; i++)
        {
            var r = pVerts.Item1 * l1[i] + pVerts.Item2 * l2[i] + pVerts.Item3 * l3[i];
            lp[0] = l1[i]; lp[1] = l2[i]; lp[2] = l3[i];
            for (int j = 0; j < w.Length; j++)
            {
                var rPrime = qVerts.Item1 * l1[j] + qVerts.Item2 * l2[j] + qVerts.Item3 * l3[j];
                double dx = r.X - rPrime.X, dy = r.Y - rPrime.Y;
                var (sa, sp) = table.EvaluateSmooth(Math.Sqrt(dx * dx + dy * dy));
                double weight = w[i] * w[j] * areaP * areaQ;
                Complex wa = weight * scaleA * sa, wf = weight * scalePhi * sp;
                lq[0] = l1[j]; lq[1] = l2[j]; lq[2] = l3[j];
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                    {
                        momentsA[a, b] += lp[a] * lq[b] * wa;
                        momentsPhi[a, b] += lp[a] * lq[b] * wf;
                    }
            }
        }
        return (momentsA, momentsPhi);
    }
}
