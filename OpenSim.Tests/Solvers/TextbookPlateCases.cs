using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// End-to-end plate cases against worked textbook examples (FU-51; audit gap (c)9): the
/// environment model is built on a plate mesh, evaluated, and solved, and its film
/// coefficient and heat loss are read against the numbers the textbook arrives at.
/// <para>
/// Source: Lienhard IV &amp; Lienhard V, <i>A Heat Transfer Textbook</i>, 6th ed. (version
/// 6.00, 19 April 2024), Phlogiston Press, downloaded from ahtt.mit.edu with the authors'
/// permission to download for non-profit instructional use. Every reference number below
/// is read from the example's printed solution; the air properties the examples use come
/// from the book's own Appendix A table, while the solver uses Incropera's Table A.4
/// (<see cref="FluidLibrary.Air"/>), so a percent-level difference in Ra and Re is
/// expected and is measured separately from the model's own choices.
/// </para>
/// <list type="bullet">
/// <item><b>Example 8.1 (p. 421):</b> air at 20 °C next to a 0.30 m high wall at 50 °C. At
/// T_f = 35 °C: Pr = 0.710, ν = 1.643e-5 m²/s, α = 2.315e-5 m²/s, k = 0.02672 W/m·K,
/// β = 1/308 K⁻¹; Ra_L = 6.78e7; the Churchill–Chu LAMINAR correlation, eqn. (8.13a),
/// Nu_L = 47.33; h = 4.22 W/m²K.</item>
/// <item><b>Example 6.5 (pp. 310–311):</b> air at 20 °C moving at 15 m/s over an isothermal
/// plate at 110 °C, ½ m long and ½ m wide. At T_f = 65 °C: Pr = 0.703, ν = 1.95e-5 m²/s,
/// k = 0.0292 W/m·K; Re_L = 384 600 (laminar to the trailing edge); Nu_L = 0.664·Re_L^½·Pr^⅓
/// = 366.2; h = 21.4 W/m²K; Q = h·A·ΔT = 21.4·(0.5)²·90 = 482 W on the one face.</item>
/// </list>
/// </summary>
public class TextbookPlateCases
{
    private readonly ITestOutputHelper _out;

    public TextbookPlateCases(ITestOutputHelper output) => _out = output;

    // Example 8.1
    private const double WallHeight = 0.30;
    private const double WallWidth = 0.30;
    private const double RoomAir = 293.15;
    private const double WallTemperature = 323.15;
    private const double Ex81Rayleigh = 6.78e7;
    private const double Ex81Nusselt = 47.33;
    private const double Ex81Coefficient = 4.22;
    private const double Ex81Prandtl = 0.710;
    private const double Ex81Viscosity = 1.643e-5;
    private const double Ex81Diffusivity = 2.315e-5;
    private const double Ex81Conductivity = 0.02672;

    // Example 6.5
    private const double PlateLength = 0.5;
    private const double PlateWidth = 0.5;
    private const double TunnelAir = 293.15;
    private const double TunnelSpeed = 15.0;
    private const double PlateTemperature = 383.15;
    private const double Ex65Reynolds = 384_600;
    private const double Ex65Nusselt = 366.2;
    private const double Ex65Coefficient = 21.4;
    private const double Ex65Heat = 482.0;
    private const double Ex65Prandtl = 0.703;
    private const double Ex65Viscosity = 1.95e-5;
    private const double Ex65Conductivity = 0.0292;

    /// <summary>A plate the thermal solve keeps isothermal to a fraction of a kelvin: the
    /// conduction along it is 10⁴–10⁵ times the film's, so the fixed temperature on one edge
    /// face reaches the whole plate and the textbook's "isothermal surface" is what the
    /// environment sees.</summary>
    private const double PlateConductivity = 1e6;
    private const double PlateThickness = 0.01;

    private static SolveInput Input(FeMesh mesh, EnvironmentSettings environment,
        IReadOnlyList<BoundaryCondition>? conditions = null) => new()
    {
        Mesh = mesh,
        Material = StructuredBoxMesh.Conductor("Isothermal plate", PlateConductivity),
        BoundaryConditions = conditions ?? Array.Empty<BoundaryCondition>(),
        Environment = environment
    };

    private static double[] Temperatures(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.ToArray();

    private static double TriangleArea(FeMesh mesh, BoundaryTriangle tri)
    {
        var a = mesh.Nodes[tri.A];
        var b = mesh.Nodes[tri.B] - a;
        var c = mesh.Nodes[tri.C] - a;
        double nx = b.Y * c.Z - b.Z * c.Y, ny = b.Z * c.X - b.X * c.Z, nz = b.X * c.Y - b.Y * c.X;
        return 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    /// <summary>Σ h·A·(T − T∞) over one geometric face of the film the environment evaluates
    /// at the given nodal temperatures — the face's heat loss, and the area-mean h with it.</summary>
    private static (double Heat, double MeanCoefficient, double Area) FaceLoss(EnvironmentBoundaryModel model,
        FeMesh mesh, int faceId, double[] nodal, double ambient)
    {
        var film = model.Evaluate(nodal);
        double heat = 0, hA = 0, area = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            if (tri.FaceId != faceId) continue;
            double a = TriangleArea(mesh, tri);
            double ts = (nodal[tri.A] + nodal[tri.B] + nodal[tri.C]) / 3;
            heat += film.TriangleFilmCoefficient[t] * a * (ts - ambient);
            hA += film.TriangleFilmCoefficient[t] * a;
            area += a;
        }
        return (heat, hA / area, area);
    }

    [Fact]
    public void VerticalWallInStillAir_Example81()
    {
        // The wall: 0.30 m high (z, against the default −z gravity), 0.30 m wide, 10 mm thick;
        // its broad faces are the ±x faces. Still air at 20 °C, radiation off as in the example.
        var mesh = StructuredBoxMesh.Build(0, PlateThickness, 0, WallWidth, 0, WallHeight, 1, 6, 6);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = RoomAir, IncludeRadiation = false
        };

        // 1. The dimensionless groups from the solver's own air table at the example's film
        //    temperature, against the example's: the property tables differ by a percent or
        //    two (ν, α) and Ra carries both.
        var state = FluidLibrary.Air.AtTemperature(0.5 * (WallTemperature + RoomAir));
        double rayleigh = ConvectionCorrelations.Rayleigh(state, 9.80665, WallTemperature - RoomAir, WallHeight);
        _out.WriteLine($"Air at 308 K: Pr {state.Prandtl:F4} (book {Ex81Prandtl}), ν {state.KinematicViscosity:E4} " +
                       $"(book {Ex81Viscosity:E4}), α {state.ThermalDiffusivity:E4} (book {Ex81Diffusivity:E4}), " +
                       $"k {state.ThermalConductivity:F5} (book {Ex81Conductivity}); Ra {rayleigh:E4} (book {Ex81Rayleigh:E3})");
        Assert.InRange(state.Prandtl / Ex81Prandtl, 0.99, 1.01);
        Assert.InRange(state.ThermalConductivity / Ex81Conductivity, 0.99, 1.01);
        Assert.InRange(rayleigh / Ex81Rayleigh, 0.95, 1.02);

        // 2. The example's own correlation (Churchill–Chu laminar, eqn. 8.13a) with the
        //    solver's properties reproduces the example's h: what the property tables cost.
        double laminarNusselt = ConvectionCorrelations.NaturalVerticalPlateLaminar(rayleigh, state.Prandtl);
        double laminarCoefficient = laminarNusselt * state.ThermalConductivity / WallHeight;
        _out.WriteLine($"Eqn. (8.13a) with the solver's air: Nu {laminarNusselt:F2} (book {Ex81Nusselt}), " +
                       $"h {laminarCoefficient:F3} (book {Ex81Coefficient})");
        Assert.InRange(laminarCoefficient / Ex81Coefficient, 0.99, 1.01);

        // 3. The environment model on the wall, evaluated at the wall's temperature: the
        //    broad face is a vertical plate of height 0.30 m. The MODEL uses Churchill–Chu's
        //    all-Ra form (eqn. 8.13b; one expression, no branch to cross mid-transient),
        //    which at Ra ≈ 7e7 reads ABOVE the laminar form the book prefers below 1e9 — the
        //    all-Ra form is already blending turbulence in. First measurement: 1.15× the
        //    book's h; gated as that ratio, and as the identity with the correlation it is.
        var log = new List<string>();
        var model = EnvironmentBoundaryModel.Build(Input(mesh, environment), log)!;
        var panel = model.Panels.First(p => p.FaceId == StructuredBoxMesh.FaceXMax);
        Assert.Equal(PanelShape.VerticalPlate, panel.Shape);
        Assert.Equal(WallHeight, panel.CharacteristicLength, 12);
        var uniform = Enumerable.Repeat(WallTemperature, mesh.NodeCount).ToArray();
        var (_, modelCoefficient, area) = FaceLoss(model, mesh, StructuredBoxMesh.FaceXMax, uniform, RoomAir);
        double allRaCoefficient = ConvectionCorrelations.NaturalVerticalPlate(rayleigh, state.Prandtl)
                                  * state.ThermalConductivity / WallHeight;
        _out.WriteLine($"Model on the wall: h {modelCoefficient:F3} W/m²K = {modelCoefficient / Ex81Coefficient:F3}× the book " +
                       $"(all-Ra form {allRaCoefficient:F3}, laminar form {laminarCoefficient:F3})");
        Assert.Equal(WallHeight * WallWidth, area, 9);
        Assert.Equal(allRaCoefficient, modelCoefficient, 9);
        Assert.InRange(modelCoefficient / Ex81Coefficient, 1.12, 1.18);

        // 4. End to end: hold the bottom edge at 50 °C, let the environment carry the rest,
        //    solve. The plate comes out isothermal and its broad face loses the model's
        //    h·A·ΔT — the same ratio to the book's 4.22·(0.30)²·30 = 11.4 W.
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "Held", FaceIds = new[] { StructuredBoxMesh.FaceZMin }, Kelvin = WallTemperature }
        };
        var output = new HeatConductionSolver().Solve(Input(mesh, environment, conditions));
        var solved = Temperatures(output);
        Assert.True(solved.Max(t => Math.Abs(t - WallTemperature)) < 0.05,
            $"the plate is not isothermal: {solved.Min():F3}…{solved.Max():F3} K");
        var (loss, solvedCoefficient, _) = FaceLoss(model, mesh, StructuredBoxMesh.FaceXMax, solved, RoomAir);
        double bookLoss = Ex81Coefficient * WallHeight * WallWidth * (WallTemperature - RoomAir);
        _out.WriteLine($"Solved: plate {solved.Min():F3}…{solved.Max():F3} K; broad face loses {loss:F3} W " +
                       $"(book h·A·ΔT {bookLoss:F3} W, ratio {loss / bookLoss:F3}); h {solvedCoefficient:F3}");
        Assert.InRange(loss / bookLoss, 1.12, 1.18);
        Assert.Contains(output.Log, line => line.Contains("natural convection"));
    }

    [Fact]
    public void FlatPlateInAWindTunnel_Example65()
    {
        // The plate: 0.5 m along the stream (x), 0.5 m wide, 10 mm thick, broad faces ±z.
        // Air at 20 °C at 15 m/s along +x; radiation off as in the example. Gravity is left
        // at its default — a user's setting — so the model blends its natural-convection leg
        // in with the cube law; on this plate that leg is a quarter of the forced one and
        // moves h by under 2 %.
        var mesh = StructuredBoxMesh.Build(0, PlateLength, 0, PlateWidth, 0, PlateThickness, 8, 8, 1);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid, AmbientTemperature = TunnelAir,
            FlowVelocity = new Vector3D(TunnelSpeed, 0, 0), IncludeRadiation = false
        };

        // 1. Reynolds and Prandtl from the solver's air at the film temperature.
        var state = FluidLibrary.Air.AtTemperature(0.5 * (PlateTemperature + TunnelAir));
        double reynolds = ConvectionCorrelations.Reynolds(state, TunnelSpeed, PlateLength);
        _out.WriteLine($"Air at 338 K: Pr {state.Prandtl:F4} (book {Ex65Prandtl}), ν {state.KinematicViscosity:E4} " +
                       $"(book {Ex65Viscosity:E3}), k {state.ThermalConductivity:F5} (book {Ex65Conductivity}); " +
                       $"Re_L {reynolds:F0} (book {Ex65Reynolds})");
        Assert.InRange(state.Prandtl / Ex65Prandtl, 0.99, 1.01);
        Assert.InRange(reynolds / Ex65Reynolds, 0.97, 1.03);
        Assert.True(reynolds < ConvectionCorrelations.TransitionReynolds, "the book's case is laminar to the trailing edge");

        // 2. The example's correlation (eqn. 6.68) with the solver's properties.
        double nusselt = ConvectionCorrelations.ForcedFlatPlate(reynolds, state.Prandtl);
        double forcedCoefficient = nusselt * state.ThermalConductivity / PlateLength;
        _out.WriteLine($"Eqn. (6.68) with the solver's air: Nu {nusselt:F1} (book {Ex65Nusselt}), h {forcedCoefficient:F3} (book {Ex65Coefficient})");
        Assert.InRange(forcedCoefficient / Ex65Coefficient, 0.98, 1.02);

        // 3. The environment model on the plate: the broad face runs parallel to the stream
        //    over its own 0.5 m, so it takes the flat-plate result over that run.
        var log = new List<string>();
        var model = EnvironmentBoundaryModel.Build(Input(mesh, environment), log)!;
        var panel = model.Panels.First(p => p.FaceId == StructuredBoxMesh.FaceZMax);
        Assert.Equal(PanelFlowRegime.Parallel, panel.FlowRegime);
        Assert.Equal(PlateLength, panel.FlowLength, 12);
        var uniform = Enumerable.Repeat(PlateTemperature, mesh.NodeCount).ToArray();
        var (heat, modelCoefficient, area) = FaceLoss(model, mesh, StructuredBoxMesh.FaceZMax, uniform, TunnelAir);
        _out.WriteLine($"Model on the plate: h {modelCoefficient:F3} W/m²K ({modelCoefficient / Ex65Coefficient:F4}× the book), " +
                       $"face loss {heat:F1} W (book {Ex65Heat} W)");
        Assert.Equal(PlateLength * PlateWidth, area, 9);
        Assert.InRange(modelCoefficient / Ex65Coefficient, 0.97, 1.03);
        Assert.InRange(heat / Ex65Heat, 0.97, 1.03);

        // 4. End to end: the leading-edge face held at 110 °C, the environment on the rest.
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "Steam", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = PlateTemperature }
        };
        var output = new HeatConductionSolver().Solve(Input(mesh, environment, conditions));
        var solved = Temperatures(output);
        Assert.True(solved.Max(t => Math.Abs(t - PlateTemperature)) < 0.2,
            $"the plate is not isothermal: {solved.Min():F3}…{solved.Max():F3} K");
        var (loss, solvedCoefficient, _) = FaceLoss(model, mesh, StructuredBoxMesh.FaceZMax, solved, TunnelAir);
        _out.WriteLine($"Solved: plate {solved.Min():F3}…{solved.Max():F3} K; top face loses {loss:F1} W " +
                       $"(book {Ex65Heat} W, ratio {loss / Ex65Heat:F4}); h {solvedCoefficient:F3}");
        Assert.InRange(loss / Ex65Heat, 0.97, 1.03);
        Assert.Contains(output.Log, line => line.Contains("forced convection"));
    }
}
