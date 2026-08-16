using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// The plumbing that turns a <see cref="SurfaceFilmModel"/> into matrix and load
/// contributions, and the convergence test of the fixed-point iteration that surrounds
/// them. Shared by the steady and transient thermal solvers so an environment means
/// exactly the same thing in both.
/// </summary>
internal static class EnvironmentThermalTerms
{
    /// <summary>Absolute convergence floor of the nonlinear iteration [K].</summary>
    public const double AbsoluteTolerance = 1e-6;

    /// <summary>Relative convergence floor, applied to the temperature scale.</summary>
    public const double RelativeTolerance = 1e-8;

    /// <summary>Iterate cap inside one backward-Euler step. A step that needs more than this
    /// is not converging, and a smaller time step is the fix.</summary>
    public const int MaxTransientIterations = 25;

    /// <summary>Iterate cap for the steady solve, which has no time step to lean on and
    /// starts from a much worse guess.</summary>
    public const int MaxSteadyIterations = 50;

    /// <summary>
    /// The conduction matrix plus the film's Robin surface terms. The base matrix is
    /// assembled ONCE and copied here per iterate: only the surface coefficients change,
    /// and re-integrating every element volume to update a surface term would dominate the
    /// cost of the whole nonlinear loop.
    /// </summary>
    public static CsrMatrix WithFilm(CsrMatrix baseMatrix, FeMesh mesh, SurfaceFilmModel film)
    {
        var builder = new SparseMatrixBuilder(baseMatrix.RowCount, baseMatrix.ColumnCount);
        for (int row = 0; row < baseMatrix.RowCount; row++)
            for (int k = baseMatrix.RowPointers[row]; k < baseMatrix.RowPointers[row + 1]; k++)
                builder.Add(row, baseMatrix.ColumnIndices[k], baseMatrix.Values[k]);

        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;
            ScalarDiffusionAssembler.AddRobinSurface(builder, mesh, mesh.BoundaryTriangles[t], h);
        }
        return builder.Build();
    }

    /// <summary>The nodal loads plus the film's ambient term h·T_ref·∫NᵢdA = h·T_ref·A/3
    /// per triangle node — with T_ref the triangle's OWN reference when the film carries
    /// per-triangle references (the CFD film drives toward the local fluid temperature).
    /// Returns a new vector; the caller's constant loads are reused unchanged across
    /// iterates.</summary>
    public static double[] WithFilmLoads(IReadOnlyList<double> constantLoads, FeMesh mesh,
        SurfaceFilmModel film)
    {
        var loads = constantLoads.ToArray();
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;
            var triangle = mesh.BoundaryTriangles[t];
            double share = h * film.ReferenceTemperatureOf(t)
                           * ScalarDiffusionAssembler.SurfaceArea(mesh, triangle) / 3.0;
            loads[triangle.A] += share;
            loads[triangle.B] += share;
            loads[triangle.C] += share;
        }
        return loads;
    }

    /// <summary>
    /// The film coefficient as a nodal field [W/(m²·K)]: each wetted node takes the
    /// area-weighted mean of the coefficients around it, interior nodes stay at zero. A
    /// per-triangle quantity has to become nodal to ride the same result-field pipeline as
    /// temperature, and area weighting is what makes the value mesh-independent.
    /// </summary>
    public static double[] NodalFilmField(FeMesh mesh, SurfaceFilmModel film)
    {
        var values = new double[mesh.NodeCount];
        var weights = new double[mesh.NodeCount];
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h)) continue;
            var triangle = mesh.BoundaryTriangles[t];
            double area = ScalarDiffusionAssembler.SurfaceArea(mesh, triangle);
            Accumulate(triangle.A);
            Accumulate(triangle.B);
            Accumulate(triangle.C);

            void Accumulate(int node)
            {
                values[node] += h * area;
                weights[node] += area;
            }
        }
        for (int i = 0; i < values.Length; i++)
            if (weights[i] > 0)
                values[i] /= weights[i];
        return values;
    }

    /// <summary>Largest nodal change between two iterates [K].</summary>
    public static double MaxChange(IReadOnlyList<double> previous, IReadOnlyList<double> current)
    {
        double max = 0;
        for (int i = 0; i < current.Count; i++)
            max = Math.Max(max, Math.Abs(current[i] - previous[i]));
        return max;
    }

    /// <summary>The convergence threshold at the current temperature scale: absolute at
    /// small temperatures, relative once the field is large.</summary>
    public static double Threshold(IReadOnlyList<double> temperature)
    {
        double scale = 0;
        for (int i = 0; i < temperature.Count; i++)
            scale = Math.Max(scale, Math.Abs(temperature[i]));
        return Math.Max(AbsoluteTolerance, RelativeTolerance * scale);
    }
}
