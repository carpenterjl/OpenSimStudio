using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Fix 9 gates (RF-1): the sign of the charge in the surface near-field evaluators.
/// The oracle is Maxwell's equations themselves, which no evaluator shares code with:
/// in a source-free region ∇×H = jωε₀E. H comes from the currents alone, so the curl of
/// H (by central differences) fixes E including the sign of its scalar-potential part.
/// </summary>
public class NearFieldSignTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;
    private const double Eps0 = 8.8541878128e-12;

    private static SurfaceStructure VerticalStrip(double width, double zMin, double zMax, int rows)
    {
        var vertices = new List<Vector3D>(2 * (rows + 1));
        for (int j = 0; j <= rows; j++)
        {
            double z = zMin + (zMax - zMin) * j / rows;
            vertices.Add(new Vector3D(-width / 2, 0, z));
            vertices.Add(new Vector3D(width / 2, 0, z));
        }
        var triangles = new List<(int, int, int)>(2 * rows);
        for (int j = 0; j < rows; j++)
        {
            int v00 = 2 * j, v10 = 2 * j + 1, v01 = 2 * j + 2, v11 = 2 * j + 3;
            if (j % 2 == 0) { triangles.Add((v00, v11, v10)); triangles.Add((v00, v01, v11)); }
            else { triangles.Add((v00, v01, v10)); triangles.Add((v10, v01, v11)); }
        }
        return new SurfaceStructure(vertices, triangles, null);
    }

    private static (SurfaceStructure Strip, SurfaceMomSolution Solution) StripDipole(double width, int rows)
    {
        double h = 0.25 * Lambda;
        var strip = VerticalStrip(width, -h, h, rows);
        int port = -1;
        for (int e = 0; e < strip.Edges.Count; e++)
            if (strip.Edges[e].V1 == rows && strip.Edges[e].V2 == rows + 1) port = e;
        Assert.True(port >= 0);
        var solution = new SurfaceMomSolver().Solve(strip, Frequency,
            new SurfacePort(new[] { port }, new Vector3D(0, 0, 1)));
        return (strip, solution);
    }

    /// <summary>‖∇×H − jωε₀E‖ / ‖jωε₀E‖ at a point, H differenced over ±step.</summary>
    private static double AmpereResidual(Func<IReadOnlyList<Vector3D>, FieldMap> evaluate,
        Vector3D point, double step, double frequency)
    {
        var offsets = new[]
        {
            new Vector3D(step, 0, 0), new Vector3D(-step, 0, 0),
            new Vector3D(0, step, 0), new Vector3D(0, -step, 0),
            new Vector3D(0, 0, step), new Vector3D(0, 0, -step),
        };
        var points = new List<Vector3D> { point };
        points.AddRange(offsets.Select(o => point + o));
        var map = evaluate(points);
        var h = map.H!;
        Complex D((Complex X, Complex Y, Complex Z) plus, (Complex X, Complex Y, Complex Z) minus,
            Func<(Complex X, Complex Y, Complex Z), Complex> pick) => (pick(plus) - pick(minus)) / (2 * step);

        Complex dHz_dy = D(h[3], h[4], v => v.Z), dHy_dz = D(h[5], h[6], v => v.Y);
        Complex dHx_dz = D(h[5], h[6], v => v.X), dHz_dx = D(h[1], h[2], v => v.Z);
        Complex dHy_dx = D(h[1], h[2], v => v.Y), dHx_dy = D(h[3], h[4], v => v.X);
        Complex cx = dHz_dy - dHy_dz, cy = dHx_dz - dHz_dx, cz = dHy_dx - dHx_dy;

        var jwe = new Complex(0, 2 * Math.PI * frequency * Eps0);
        var (ex, ey, ez) = map.E[0];
        double residual = Math.Sqrt(Sq(cx - jwe * ex) + Sq(cy - jwe * ey) + Sq(cz - jwe * ez));
        double scale = Math.Sqrt(Sq(jwe * ex) + Sq(jwe * ey) + Sq(jwe * ez));
        return residual / scale;

        static double Sq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
    }

    [Fact]
    public void FreeSpaceProbe_SatisfiesAmpere_InTheNearField()
    {
        var (strip, solution) = StripDipole(Lambda / 100, 20);
        foreach (var point in new[]
        {
            new Vector3D(0.10 * Lambda, 0.06 * Lambda, 0.12 * Lambda),
            new Vector3D(0.05 * Lambda, 0.20 * Lambda, 0.30 * Lambda),   // beyond the dipole's tip height
            new Vector3D(-0.3 * Lambda, 0.25 * Lambda, 0.05 * Lambda),
        })
        {
            double residual = AmpereResidual(p => SurfaceFieldProbe.Evaluate(strip, solution, p),
                point, Lambda / 400, Frequency);
            Assert.True(residual < 0.03, $"∇×H vs jωε₀E at {point}: residual {residual:g3}");
        }
    }

    [Fact]
    public void FreeSpaceProbe_FarField_IsTransverse()
    {
        // 45° off the dipole axis the true far field has no radial part; with the charge
        // sign reversed the radial part was as large as the transverse one (|E| 2.2× high).
        var (strip, solution) = StripDipole(Lambda / 100, 20);
        var direction = new Vector3D(0, Math.Sin(Math.PI / 4), Math.Cos(Math.PI / 4));
        var map = SurfaceFieldProbe.Evaluate(strip, solution, new[] { direction * (20 * Lambda) });
        var (ex, ey, ez) = map.E[0];
        Complex radial = ex * direction.X + ey * direction.Y + ez * direction.Z;
        Assert.True(radial.Magnitude < 0.03 * map.Magnitude[0],
            $"E·r̂ = {radial.Magnitude:g3} of |E| = {map.Magnitude[0]:g3}");
    }

    [Fact]
    public void StripDipole_NearField_MatchesTheEquivalentWire()
    {
        // A strip of width w radiates like a wire of radius w/4. Compared per ampere of
        // feed current at distances of several widths and more, complex E within 5 %.
        double width = Lambda / 100;
        var (strip, surfaceSolution) = StripDipole(width, 20);
        Complex stripCurrent = Complex.One / surfaceSolution.InputImpedance;

        double length = 0.5 * Lambda;
        var grid = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), width / 4) },
            maxElementLength: length / 20);
        var wire = grid.Structure!;
        int feed = wire.NearestBasis(Vector3D.Zero);
        var wireSolution = new ThinWireMomSolver().Solve(wire, Frequency, feed);
        Complex wireCurrent = wireSolution.BasisCurrents[feed];

        var points = new[]
        {
            new Vector3D(0, 6 * width, 0),
            new Vector3D(0, 10 * width, 0.1 * Lambda),
            new Vector3D(0.1 * Lambda, 0.1 * Lambda, 0.2 * Lambda),
            new Vector3D(0, 0.2 * Lambda, 0.35 * Lambda),
            new Vector3D(0.3 * Lambda, 0.3 * Lambda, 0.3 * Lambda),
        };
        var fromStrip = SurfaceFieldProbe.Evaluate(strip, surfaceSolution, points);
        var fromWire = FieldProbe.Evaluate(wire, wireSolution, points);
        for (int i = 0; i < points.Length; i++)
        {
            var s = fromStrip.E[i];
            var w = fromWire.E[i];
            double difference = Math.Sqrt(
                Sq(s.X / stripCurrent - w.X / wireCurrent) + Sq(s.Y / stripCurrent - w.Y / wireCurrent)
                + Sq(s.Z / stripCurrent - w.Z / wireCurrent));
            double scale = fromWire.Magnitude[i] / wireCurrent.Magnitude;
            Assert.True(difference < 0.05 * scale,
                $"point {i}: strip vs wire E per ampere differ by {difference / scale:g3}");
        }

        static double Sq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
    }

    [Fact]
    public void LayeredEvaluator_SatisfiesAmpere_AboveThePatch()
    {
        // The Balanis patch on εr = 2.2 at 10 GHz, sampled in the air above it. E_z
        // carries both gauge legs (A_z through W, and ∂zΦ), and H carries the A_z leg, so
        // this closes only when every charge-dependent term has the physical sign.
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        const double f = 10e9;
        double lambda = 299_792_458.0 / f, d = substrate.ThicknessMeters;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(1.186e-2, 0.906e-2, 1.4e-3, z: d,
            portFraction: 0.08);
        var table = new LayeredKernelTable(substrate, f, 0.05);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);

        foreach (var point in new[]
        {
            new Vector3D(0.2e-2, 0.1e-2, d + 0.4e-2),
            new Vector3D(0.9e-2, -0.3e-2, d + 0.25e-2),     // off to the side of the patch
            new Vector3D(-0.4e-2, 0.8e-2, d + 0.6e-2),
        })
        {
            double residual = AmpereResidual(
                p => LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, p),
                point, lambda / 300, f);
            Assert.True(residual < 0.05, $"∇×H vs jωε₀E at {point}: residual {residual:g3}");
        }
    }
}
