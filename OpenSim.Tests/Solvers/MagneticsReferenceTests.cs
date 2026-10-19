using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-16: the 2D magnetics solver against independent references, in place of running FEMM itself
/// (the owner's choice). (a) MATLAB R2025b's PDE Toolbox on OpenSim's own models (pot core, solid
/// turns, ER section, rings): the toolbox meshes quadratic triangles and solves the linear cases
/// (femodel, or its assembled matrices for the time-harmonic ones); the saturating ER section is
/// solved by a Newton solver written for FU-16 on that mesh. Meshes are refined until the values
/// stop moving; a reference is the Richardson value where the sequence is monotone, else the
/// finest mesh's. (b) FEMM's published examples from femm.info (inductor, AC force, TEAM 28, air
/// core, tube): FEMM's printed numbers, plus values computed for FU-16 — the exact air-core
/// inductances, the tube loss integrated from the page's analytic solution, and converged values
/// from a linear-triangle solver written for FU-16 on the toolbox's mesher (1/r at centroids:
/// OpenSim's own element family, so these fix the converged value and the box size, not the
/// discretisation). Every case runs at a mesh a test can afford; how each one converges onto its
/// reference under refinement (Release runs, not repeated here) is in the comments and in
/// OpenSim.Tests/References/FU-16, with the scripts and FEMM's numbers.
/// </summary>
public class MagneticsReferenceTests
{
    private readonly ITestOutputHelper _out;
    public MagneticsReferenceTests(ITestOutputHelper output) => _out = output;
    private const double Mu0 = MagneticConstants.Mu0;
    private const double Inch = 0.0254;

    private static MagneticModel2D Remesh(MagneticModel2D m, double max, double min, double growth)
    {
        var r = new MagneticModel2D
        {
            Geometry = m.Geometry, DomainX0 = m.DomainX0, DomainX1 = m.DomainX1, DomainY0 = m.DomainY0, DomainY1 = m.DomainY1,
            Background = m.Background, NaturalSides = m.NaturalSides, MaxElement = max, MinElement = min, Growth = growth
        };
        r.Regions.AddRange(m.Regions);
        r.Coils.AddRange(m.Coils);
        r.Conductors.AddRange(m.Conductors);
        r.ExtraLinesX.AddRange(m.ExtraLinesX);
        r.ExtraLinesY.AddRange(m.ExtraLinesY);
        return r;
    }

    /// <summary>The four 1-turn PCB rings of MagneticsTests.GappedPotCore_InductanceFollowsTheGap.</summary>
    private static List<WindingTurn> PotTurns() =>
        Enumerable.Range(0, 4).Select(i => new WindingTurn("P", 1, 4e-3 + i * 1.2e-3, 4.8e-3 + i * 1.2e-3, -35e-6, 0)).ToList();

    private static AxisymmetricCore PotCore(double gap) =>
        new(3e-3, 10e-3, 12e-3, 2e-3, 2e-3, gap, new MagneticMaterial("ferrite", 2000));

    [Theory]
    [InlineData(0.1e-3, 6.3698016e-6)]
    [InlineData(0.2e-3, 3.6864253e-6)]
    [InlineData(0.4e-3, 2.2518179e-6)]
    public void APotCore_HasTheReferenceInductance_AtItsDefaultMesh(double gap, double matlab)
    {
        // The gapped pot core the existing test only bounds from below (µ0N²A/g leaves out the
        // window flux in parallel with the gap and the fringing): PlanarWindingSection.Build's own
        // box and mesh, against MATLAB's L extrapolated over five meshes to 2.2–2.7M nodes.
        // Measured at the default mesh: −0.34 / −0.30 / −0.26 % (0.1 / 0.2 / 0.4 mm gaps), from
        // below; refined to 1 mm / 8.75 µm / 1.25, −0.16 / −0.14 / −0.12 %; to 0.5 mm / 4.4 µm /
        // 1.18, −0.08 / −0.07 / −0.06 %; the three-level Richardson value within 0.014 % of MATLAB
        // in every case.
        var built = PlanarWindingSection.Build(PotTurns(), new Dictionary<string, Complex> { ["P"] = 1 }, PotCore(gap), solidTurns: false);
        var solution = new MagneticSolver2D().SolveStatic(built.Model);
        double l = built.Inductance(solution, "P").Real;
        _out.WriteLine($"gap {gap * 1e3} mm: L {l * 1e6:F6} µH against MATLAB {matlab * 1e6:F6} ({(l / matlab - 1) * 100:F3} %)");
        Assert.InRange(l / matlab, 0.995, 1.0);
        Assert.Equal(l, 2 * solution.Energy(), 1e-6 * l);
    }

    [Theory]
    [InlineData(true, 1e5, false, 0.104747675, 3.6809608e-6)]
    [InlineData(true, 1e6, false, 0.399571209, 3.6175683e-6)]
    [InlineData(true, 1e6, true, 0.399571209, 3.6175683e-6)]
    [InlineData(false, 1e5, false, 0.0964069766, 2.38790411e-7)]
    [InlineData(false, 1e6, false, 0.120920223, 2.34486917e-7)]
    public void SolidTurns_HaveTheReferenceAcResistanceAndInductance(bool withCore, double frequency, bool refined,
        double matlabR, double matlabL)
    {
        // The same four rings as solid copper in series at 1 A, gap 0.2 mm, with and without the
        // core: skin and proximity loss in turns lying in the gap's fringing field. MATLAB solves the
        // same ψ = r·A_φ formulation (one unit-voltage solve per turn, the 4×4 admittance, the series
        // current imposed) on meshes whose copper elements are under δ/4; the with-core L values are
        // its Richardson values, every R and the air-case L its finest mesh's (last change under
        // 2e-6; the 1 MHz core R is not monotone under refinement). Measured at Build's default mesh:
        // with the core R +0.018 % and L −0.30 % at 100 kHz; at 1 MHz (δ = 66 µm) R +2.4 %, L −0.33 %
        // — the default 2 mm far cells and 1.35 growth leave the gap's field too coarse for R there;
        // refined to 1 mm / 8.75 µm / 1.25, +0.94 % and −0.15 %; to 0.5 mm / 4.4 µm / 1.18, +0.43 %
        // and −0.07 % (Richardson +0.13 %). In air R −0.006 % / +0.49 %, L −0.74 % / −0.75 %.
        var core = withCore ? PotCore(0.2e-3) : null;
        var built = PlanarWindingSection.Build(PotTurns(), new Dictionary<string, Complex> { ["P"] = 1 }, core, solidTurns: true);
        var model = refined ? Remesh(built.Model, 1e-3, 8.75e-6, 1.25) : built.Model;
        var solution = new MagneticSolver2D().SolveTimeHarmonic(model, frequency);
        double r = built.Resistance(solution, "P"), l = built.Inductance(solution, "P").Real;
        double dr = r / matlabR - 1, dl = l / matlabL - 1;
        _out.WriteLine($"{(withCore ? "core" : "air")} {frequency / 1e3:g4} kHz{(refined ? " refined" : "")}: " +
                       $"R {r:F6} Ω ({dr * 100:F3} %), L {l * 1e6:F6} µH ({dl * 100:F3} %), {solution.NodeCount} nodes");
        // L from below. R from above at 1 MHz; at 100 kHz within 0.02 % from either side (core
        // +0.018 % from above, air −0.006 % from below), so that band is two-sided.
        double rBand = frequency < 5e5 ? 5e-4 : withCore ? (refined ? 0.012 : 0.03) : 0.007;
        Assert.InRange(dr, frequency < 5e5 ? -rBand : 0, rBand);
        Assert.InRange(dl, withCore ? (refined ? -0.003 : -0.005) : -0.01, 0);
    }

    /// <summary>B(H) = µ0·H + (2·Js/π)·atan(π(µri − 1)µ0H/(2Js)), Js = 1.6 T, µri = 2000, tabulated
    /// at H = 0 and H_k = 10^(5k/80) A/m, k = 0…80 — the table both codes were given.</summary>
    private static BhCurve AnalyticCurve()
    {
        const double js = 1.6, mui = 2000;
        double B(double h) => Mu0 * h + 2 * js / Math.PI * Math.Atan(Math.PI * (mui - 1) * Mu0 * h / (2 * js));
        var points = new List<(double, double)> { (0, 0) };
        for (int k = 0; k <= 80; k++) { double h = Math.Pow(10, 5.0 * k / 80); points.Add((h, B(h))); }
        return new BhCurve(points);
    }

    [Theory]
    [InlineData(5.0, 2.200082952e-3, 5.377133773e-3, 0.005, 0.005)]
    [InlineData(17.5, 5.691047339e-3, 4.163330423e-2, 0.004, 0.0035)]
    [InlineData(50.0, 8.230197069e-3, 1.158731898e-1, 0.003, 0.0015)]
    [InlineData(550.0, 1.015116267e-2, 4.612971456e-1, 0.001, 0.0025)]
    public void ASaturatingErSection_HasTheReferenceFluxAndEnergy(double ampereTurns, double matlabLambda, double matlabEnergy,
        double lambdaBand, double energyBand)
    {
        // Saturation in a NON-uniform field (the existing gates saturate a uniform one): the ER
        // core's planar leg section with no gap, from the linear range (leg 0.37 T) through the knee
        // into saturation (leg 1.61 T, corners above the table). The MATLAB reference is a Newton
        // solver written for FU-16 (nl_newton_planar.m) on the toolbox's quadratic mesh — femodel's
        // nonlinear path did not converge here — checked against femodel in the linear limit and
        // against an exact 1D nonlinear case; the references are its level-4 mesh with the eight
        // window corners refined 16× (case3_corner_refinement.csv; level 3 to 4 within 6.2e-6), and
        // energy is ∫H·dB, not co-energy. Measured with 0.1 mm cells at the region edges: λ −0.31 /
        // −0.25 / −0.18 / −0.06 %, W −0.29 / −0.21 / −0.08 / +0.15 % (each row's band 1.5–1.9× its
        // own); refined to 0.5 mm / 25 µm / 1.15, λ −0.06 / −0.01 % at 5 / 550 A.
        var steel = new MagneticMaterial("steel", 1) { Curve = AnalyticCurve() };
        var er = new ErCore(4e-3, 6e-3, 3e-3, 12e-3, 3e-3, 8e-3, 0, steel);
        var solution = new MagneticSolver2D().SolveStatic(er.PlanarLegSection(ampereTurns, 0.1e-3));
        double lambda = solution.FluxLinkage("window").Real, energy = solution.Energy();
        _out.WriteLine($"{ampereTurns} A: λ {lambda:E7} Wb/m ({(lambda / matlabLambda - 1) * 100:F3} %), " +
                       $"W {energy:E7} J/m ({(energy / matlabEnergy - 1) * 100:F3} %)");
        Assert.DoesNotContain(solution.Log, l => l.Contains("WARNING"));
        Assert.Equal(1, lambda / matlabLambda, lambdaBand);
        Assert.Equal(1, energy / matlabEnergy, energyBand);
    }

    [Fact]
    public void CoaxialRings_HaveTheReferenceSelfAndMutualInductance()
    {
        // MagneticsTests' two rings, against MATLAB on the same 0.15 m box (its L 6.0271882e-8 H,
        // M 1.1121673e-8 H; the thin-ring formula the old test uses is 6.02752e-8, Maxwell's M
        // 1.11261e-8 — the box clips M by 0.04 %). Measured at 3 mm / 12.5 µm / 1.2: L −0.49 %, M
        // −0.19 %; at 1.5 mm / 6.25 µm / 1.15, −0.24 % and −0.09 %.
        const double r = 10e-3, c = 0.2e-3, sep = 5e-3;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 0.15, DomainY0 = -0.15, DomainY1 = 0.15,
            MaxElement = 3e-3, MinElement = 12.5e-6, Growth = 1.2
        };
        model.Regions.Add(new MagneticRegion("ring1", r - c / 2, r + c / 2, -c / 2, c / 2, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("ring2", r - c / 2, r + c / 2, sep - c / 2, sep + c / 2, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("ring1", new[] { ("ring1", 1) }, 1, 1));
        model.Coils.Add(new StrandedCoil("ring2", new[] { ("ring2", 1) }, 1, 0));
        var solution = new MagneticSolver2D().SolveStatic(model);
        double l = solution.FluxLinkage("ring1").Real, m = solution.FluxLinkage("ring2").Real;
        _out.WriteLine($"L {l * 1e9:F5} nH ({(l / 6.0271882e-8 - 1) * 100:F3} %), M {m * 1e9:F5} nH ({(m / 1.1121673e-8 - 1) * 100:F3} %)");
        Assert.InRange(l / 6.0271882e-8, 0.993, 1.0);
        Assert.InRange(m / 1.1121673e-8, 0.996, 1.0);
    }

    [Fact]
    public void FemmsInductorExample_HasTheReferenceInductanceEnergyAndForce()
    {
        // femm.info InductanceExample / ACForceExample (model induct1a.fem): an E–I core of µr 2500,
        // 66 turns each way in its slots, 1 A, A = 0 on FEMM's own 2.5 × 2 in box, 1 in deep. FEMM
        // prints L 1.73 mH and W 0.000865042 J from its 0.05 in mesh, which is 2.25 % below the
        // converged value; MATLAB's linear-triangle solve on the same box (five meshes, extrapolated)
        // gives L 1.769840 mH, W 8.849199e-4 J, force on the I 1.116476 N (FEMM: weighted stress
        // 1.11347 N, line integral 1.11289 N). Measured here (1 mm / 0.08 mm / 1.3): L and W −0.33 %,
        // F −0.37 % of the converged values, F −0.10 % of FEMM's; refined to 0.25 mm / 0.02 mm /
        // 1.15, L −0.05 % and F −0.05 %, the three-level extrapolation −0.0015 % (L) and +0.033 % (F)
        // off them. FEMM's own 1.73 mH lies below this test's band, so ours is between FEMM's and
        // the converged value.
        var iron = new MagneticMaterial("core iron", 2500);
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -1.25 * Inch, DomainX1 = 1.25 * Inch, DomainY0 = -0.75 * Inch, DomainY1 = 1.25 * Inch,
            MaxElement = 1e-3, MinElement = 0.08e-3, Growth = 1.3
        };
        model.Regions.Add(new MagneticRegion("E", -0.75 * Inch, 0.75 * Inch, 0, 0.75 * Inch, iron));
        model.Regions.Add(new MagneticRegion("SlotL", -0.5 * Inch, -0.25 * Inch, 0, 0.5 * Inch, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("SlotR", 0.25 * Inch, 0.5 * Inch, 0, 0.5 * Inch, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("I", -0.75 * Inch, 0.75 * Inch, -0.275 * Inch, -0.025 * Inch, iron));
        model.Coils.Add(new StrandedCoil("icoil", new[] { ("SlotL", 1), ("SlotR", -1) }, 66, 1.0));
        var solver = new MagneticSolver2D();
        var solution = solver.SolveStatic(model);
        double l = solution.Inductance("icoil").Real * Inch, w = solution.Energy() * Inch, f = solution.Force("I").Fy * Inch;
        double fAc = solver.SolveTimeHarmonic(model, 60).Force("I").Fy * Inch;
        _out.WriteLine($"L {l * 1e3:F5} mH, W {w:E6} J, F {f:F5} N, 60 Hz mean force {fAc:F6} N (per inch of depth)");
        Assert.InRange(l / 1.769840e-3, 0.995, 1.0);
        Assert.InRange(w / 8.849199e-4, 0.995, 1.0);
        Assert.Equal(l, 2 * w, 1e-6 * l);
        Assert.Equal(1, f / 1.116476, 0.005);
        Assert.Equal(1, f / 1.11347, 0.005);
        Assert.Equal(1, f / 1.11289, 0.005);
        // At 60 Hz, 1 A peak, nothing in this model conducts (the slots are air carrying a stranded
        // coil with no strand data): the time-average force is half the static one. FEMM's 60 Hz run
        // keeps its 18 AWG slots' homogenised proximity eddy currents (the −j1.78e-5 N in its 2X
        // force), yet its DC component, 0.556734 N by weighted stress, is still half its static
        // 1.11347 N to the printed digits.
        Assert.Equal(f / 2, fAc, 1e-6 * f);
        Assert.Equal(1, fAc / 0.556734, 0.005);
    }

    [Fact]
    public void Team28_HasFemmsPlateForceAndLosses()
    {
        // TEAM problem 28 as FEMM models it (team28a/k.fem): two 18 AWG coils in series at 20 A peak,
        // 50 Hz (+960 turns r 2.7–5.5 cm, −576 turns r 8–9.5 cm, both z ±2.6 cm), an aluminium plate
        // (34 MS/m) r 0–6.5 cm, z 3.73–4.03 cm. FEMM leaves it unbounded, with an asymptotic boundary
        // (team28a) or a Kelvin transformation (team28k); here an A = 0 box at 2 m, which MATLAB's
        // linear-triangle solve found within 1.4e-4 (Lorentz force, plate loss) to 4.6e-4 (stress-
        // tensor force) of a 4 m one — a 0.24 m box puts the force 12 % high. FEMM: plate force
        // 1.00693 N (Lorentz, team28a, its finest mesh, 987616 nodes); plate loss 13.1981 W, coil
        // resistive loss 2361.59 W, coil proximity loss 0.914029 W (team28k's Table 2, no mesh
        // stated). MATLAB, extrapolated: 1.008390 N, 13.2038 W. FEMM's force still rises with its
        // mesh (1.00067, 1.00559, 1.00670, 1.00693 N), so the upper limits are MATLAB's and FEMM's
        // numbers are checked for closeness. Measured here (50 mm / 0.25 mm / 1.3): −0.49 % and
        // −0.18 % of MATLAB's force and plate loss (−0.35 % and −0.14 % of FEMM's); at 25 mm /
        // 0.125 mm / 1.2, −0.14 % and −0.15 % (−0.00 % and −0.10 % of FEMM's), both rising.
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 2, DomainY0 = -2, DomainY1 = 2,
            MaxElement = 0.05, MinElement = 0.25e-3, Growth = 1.3
        };
        model.Regions.Add(new MagneticRegion("InnerCoil", 0.027, 0.055, -0.026, 0.026, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("OuterCoil", 0.08, 0.095, -0.026, 0.026, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("Plate", 0, 0.065, 0.0373, 0.0403, new MagneticMaterial("aluminium", 1, 3.4e7)));
        // 18 AWG as FEMM's model gives it: 1.0239652968433499 mm, 58 MS/m.
        const double wire = 1.0239652968433499e-3;
        model.Coils.Add(new StrandedCoil("Inner", new[] { ("InnerCoil", 1) }, 960, 20) { StrandDiameter = wire, StrandConductivity = 5.8e7 });
        model.Coils.Add(new StrandedCoil("Outer", new[] { ("OuterCoil", -1) }, 576, 20) { StrandDiameter = wire, StrandConductivity = 5.8e7 });
        var solution = new MagneticSolver2D().SolveTimeHarmonic(model, 50);
        double force = solution.Force("Plate").Fy, plate = solution.OhmicLoss(new[] { "Plate" });
        var inner = solution.StrandLoss("Inner");
        var outer = solution.StrandLoss("Outer");
        double transport = inner.TransportWatts + outer.TransportWatts, proximity = inner.ProximityWatts + outer.ProximityWatts;
        _out.WriteLine($"force {force:F5} N, plate {plate:F4} W, coils {transport:F3} W resistive + {proximity:F5} W proximity, {solution.NodeCount} nodes");
        Assert.InRange(force / 1.008390, 0.9925, 1.0);
        Assert.InRange(plate / 13.2038, 0.9966, 1.0);
        Assert.Equal(1, force / 1.00693, 0.005);
        Assert.Equal(1, plate / 13.1981, 0.003);
        // ½·I²·R_dc of the two windings: geometry, wire and copper only — FEMM's to its six digits.
        Assert.Equal(2361.59, transport, 0.005);
        // FEMM computes the strands' proximity loss in the field with its own fitted bulk model;
        // OpenSim homogenises σω²d²/32·|B|² (d ≪ δ here). On MATLAB's converged field that formula
        // gives 0.91350 W (FEMM +0.058 %); here −0.82 % with this mesh's field.
        Assert.InRange(proximity / 0.914029, 0.985, 1.0);
    }

    [Theory]
    [InlineData(1.0, 2.0, 1.0, 6.474630984e-4, 0.993)]
    [InlineData(1.0, 1.25, 4.0, 2.319123653e-4, 0.998)]
    [InlineData(0.25, 2.0, 0.25, 4.103317339e-4, 0.995)]
    public void FemmsAirCoreCoils_HaveTheExactInductance(double ri, double ro, double z, double exact, double floor)
    {
        // femm.info AirCore: 100-turn rectangular-section coils (inches), whose exact inductance is
        // the filament double integral over the section (computed for References/FU-16, quadratures
        // agreeing to 3e-13). FEMM, with its open boundary on a sphere of radius 2·max(ri, ro, z):
        // −0.061 / −0.475 / −0.191 %; Wheeler's formula −0.71 / −3.52 / −6.89 %. Here an A = 0 box
        // eight times that radius; MATLAB's linear-triangle solves in it read −0.013 / −0.015 /
        // −0.061 % at their finest meshes — mesh error, not the box: the same settings in a box twice
        // the size move them under 0.01 %. Measured −0.50 / −0.13 / −0.35 %; halving every element
        // size takes the first to −0.13 %.
        double sphere = 2 * Math.Max(ro, Math.Max(ri, z)) * Inch, box = 8 * sphere;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = box, DomainY0 = -box, DomainY1 = box,
            MaxElement = sphere / 10, MinElement = Math.Min(ro - ri, z) * Inch / 20, Growth = 1.2
        };
        model.Regions.Add(new MagneticRegion("Coil", ri * Inch, ro * Inch, -z / 2 * Inch, z / 2 * Inch, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("icoil", new[] { ("Coil", 1) }, 100, 1.0));
        double l = new MagneticSolver2D().SolveStatic(model).Inductance("icoil").Real;
        _out.WriteLine($"ri {ri} ro {ro} z {z} in: L {l * 1e3:F6} mH against {exact * 1e3:F6} ({(l / exact - 1) * 100:F3} %)");
        Assert.InRange(l / exact, floor, 1.0);
    }

    private static IReadOnlyList<Point2> Circle(double r, int n, bool ccw) => Enumerable.Range(0, n)
        .Select(i => (ccw ? 1 : -1) * 2 * Math.PI * i / n).Select(a => new Point2(r * Math.Cos(a), r * Math.Sin(a))).ToList();

    [Fact]
    public void FemmsTubeExample_HasTheExactEddyLoss()
    {
        // femm.info TubeExample (tube.fem): a steel tube (ri 0.5 in, ro 1 in, µr 1000, 10 MS/m) round a
        // copper wire (r 0.1 in) carrying 100 A peak at 1 Hz, the tube free of net current. FEMM puts
        // A = 0 on the tube's outer surface; here on a ±1.2 in square with air between (MATLAB,
        // 360-gons: 1.2 to 10 in boxes agree to 1.2e-6). FEMM does not print the loss: integrated
        // for FU-16 from the page's exact solution (δ = 5.03292 mm) it is 1.7599703 W/m (the page
        // prints c1's real part without its minus sign; the boundary conditions fix it). The circles
        // are polygons here, as in FEMM: the tube's 5° arcs are 72-gons, the wire's 10° arcs a
        // 36-gon. MATLAB's linear-triangle runs on 72-gons give 1.7627737 / 1.7609248 / 1.7604799
        // W/m as the elements halve (still falling 0.025 % a step), and 1.7601272 on 360-gons.
        // Measured: 1.7607909 W/m (+0.047 % against the circle, +0.018 % against MATLAB's finest
        // 72-gon mesh); with every element size about halved (0.3 / 0.08 mm), 144-gons +0.013 % and
        // 360-gons +0.005 % — the loss approaches the circle's from above.
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -1.2 * Inch, DomainX1 = 1.2 * Inch, DomainY0 = -1.2 * Inch, DomainY1 = 1.2 * Inch,
            MaxElement = 0.6e-3, MinElement = 0.15e-3, Growth = 1.2
        };
        model.Regions.Add(MagneticRegion.FromPolygon("Tube", new Polygon2(Circle(1.0 * Inch, 72, true), new[] { Circle(0.5 * Inch, 72, false) }),
            new MagneticMaterial("iron", 1000, 1e7)));
        model.Regions.Add(MagneticRegion.FromPolygon("Wire", new Polygon2(Circle(0.1 * Inch, 36, true)), new MagneticMaterial("copper", 1, 5.8e7)));
        model.Conductors.Add(new SolidConductor("Wire", new[] { "Wire" }, 100));
        var solution = new MagneticSolver2D().SolveTimeHarmonic(model, 1.0);
        double loss = solution.OhmicLoss(new[] { "Tube" });
        _out.WriteLine($"tube loss {loss:F7} W/m ({(loss / 1.7599703 - 1) * 100:F4} % against the exact circle), {solution.NodeCount} nodes");
        Assert.InRange(loss / 1.7599703, 1.0, 1.0007);
    }
}
