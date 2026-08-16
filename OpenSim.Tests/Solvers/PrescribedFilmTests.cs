using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Solvers;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates on <see cref="SolveInput.PrescribedFilm"/> — the seam the CFD conjugate study
/// hands its wall film through. The thermal solvers must treat it as a FIXED Robin
/// exchange identical in effect to the equivalent hand-placed convection condition.
/// </summary>
public class PrescribedFilmTests
{
    private static FeMesh BoxMesh() => StructuredBoxMesh.Build(0, 0.1, 0, 0.1, 0, 0.1, 3, 3, 3);

    private static Material Conductor() => StructuredBoxMesh.Conductor("k", 50);

    private static SurfaceFilmModel UniformFilm(FeMesh mesh, double h, double tRef) =>
        new()
        {
            TriangleFilmCoefficient = Enumerable.Repeat(h, mesh.BoundaryTriangles.Count).ToArray(),
            ReferenceTemperature = tRef,
            Origin = "test"
        };

    private static IReadOnlyList<double> Temperatures(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;

    // ---------------------------------------------------------------- identities

    [Fact]
    public void UniformPrescribedFilm_IsIdenticalToTheEquivalentConvectionCondition()
    {
        // The film rides the SAME Robin machinery as a hand-placed convection BC, so a
        // uniform film over every face and a convection condition over every face are the
        // same discrete problem — an identity, not a tolerance.
        var mesh = BoxMesh();
        var allFaces = Enumerable.Range(0, 6).ToArray();
        var source = Enumerable.Repeat(1e6, mesh.ElementCount).ToArray();

        var viaFilm = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = source,
            PrescribedFilm = UniformFilm(mesh, 25, 300)
        });
        var viaConvection = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new Convection { Name = "all", FaceIds = allFaces, Coefficient = 25, AmbientTemperature = 300 }
            },
            ElementHeatSource = source
        });

        var tFilm = Temperatures(viaFilm);
        var tConv = Temperatures(viaConvection);
        for (int i = 0; i < tFilm.Count; i++)
            Assert.Equal(tConv[i], tFilm[i], 8);
    }

    [Fact]
    public void FilmWithNoOtherDrive_PullsTheBodyExactlyToItsReference()
    {
        // Nothing else touches the body: the unique steady state is T ≡ T_ref.
        var mesh = BoxMesh();
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = UniformFilm(mesh, 40, 350)
        });

        Assert.All(Temperatures(output), t => Assert.Equal(350.0, t, 8));
    }

    [Fact]
    public void PerTriangleReferences_DriveAConductionBridgeBetweenThem()
    {
        // Left half of the skin held toward 350, right half toward 300 through equal
        // films: the temperature field must land strictly between the two references and
        // hotter on the left — the per-triangle reference plumbing at work.
        var mesh = BoxMesh();
        int n = mesh.BoundaryTriangles.Count;
        var h = new double[n];
        var refs = new double[n];
        for (int t = 0; t < n; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            double cx = (mesh.Nodes[tri.A].X + mesh.Nodes[tri.B].X + mesh.Nodes[tri.C].X) / 3;
            h[t] = 100;
            refs[t] = cx < 0.05 ? 350 : 300;
        }
        var film = new SurfaceFilmModel
        {
            TriangleFilmCoefficient = h,
            TriangleReferenceTemperature = refs,
            ReferenceTemperature = 300,
            Origin = "test two-zone"
        };

        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = film
        });

        var temps = Temperatures(output);
        Assert.All(temps, t => Assert.InRange(t, 300.0, 350.0));
        double left = temps.Where((_, i) => mesh.Nodes[i].X < 0.02).Average();
        double right = temps.Where((_, i) => mesh.Nodes[i].X > 0.08).Average();
        Assert.True(left > right + 1,
            $"left {left:F2} K should clearly exceed right {right:F2} K");
    }

    [Fact]
    public void TransientWithPrescribedFilm_ApproachesTheSteadySolution()
    {
        // The frozen-film transient contract: held long enough, the march must land on
        // the steady solve with the SAME film — the plumbing gate across both solvers.
        var mesh = BoxMesh();
        var film = UniformFilm(mesh, 30, 320);
        var material = Conductor();
        var source = Enumerable.Repeat(5e5, mesh.ElementCount).ToArray();

        var steady = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = source,
            PrescribedFilm = film
        });
        var transient = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = source,
            PrescribedFilm = film,
            TransientThermal = new TransientThermalSettings
            {
                // The lumped time constant ρcV/(hA) ≈ 2.8e5 s: run ~10 of them. Backward
                // Euler's fixed point IS the steady solution at ANY step size, so the
                // large step only sets how fast the march contracts, not where it lands.
                InitialTemperature = 320,
                Duration = 3e6,
                TimeStep = 2e4
            }
        });

        var tSteady = Temperatures(steady);
        var tFinal = Temperatures(transient);
        double scale = tSteady.Max() - 320.0;
        for (int i = 0; i < tSteady.Count; i++)
            Assert.True(Math.Abs(tFinal[i] - tSteady[i]) <= 1e-4 * scale + 1e-6,
                $"node {i}: transient {tFinal[i]:F6} vs steady {tSteady[i]:F6}");
    }

    // ---------------------------------------------------------------- typed failures

    [Fact]
    public void FilmAndEnvironmentTogether_IsATypedFailure()
    {
        var mesh = BoxMesh();
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = UniformFilm(mesh, 25, 300),
            Environment = new EnvironmentSettings()
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new HeatConductionSolver().Validate(input));
        Assert.Contains("double-count", ex.Message);
    }

    [Fact]
    public void FilmWithWrongTriangleCount_IsATypedFailure()
    {
        var mesh = BoxMesh();
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = new SurfaceFilmModel
            {
                TriangleFilmCoefficient = new double[] { 1, 2, 3 },
                ReferenceTemperature = 300,
                Origin = "test"
            }
        };

        Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Validate(input));
    }

    [Fact]
    public void NegativeFilmCoefficient_IsATypedFailure()
    {
        var mesh = BoxMesh();
        var film = UniformFilm(mesh, 25, 300);
        var coefficients = film.TriangleFilmCoefficient.ToArray();
        coefficients[0] = -5;
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = film with { TriangleFilmCoefficient = coefficients }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new HeatConductionSolver().Validate(input));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void FilmCoefficientField_IsReportedInTheOutput()
    {
        var mesh = BoxMesh();
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Conductor(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            PrescribedFilm = UniformFilm(mesh, 40, 350)
        });

        Assert.Contains(output.Fields, f => f.Name == "Film coefficient");
    }
}
