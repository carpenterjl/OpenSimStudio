using System.Numerics;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Harmonic electro-quasistatic (EQS) solver over TET4 elements: ∇·((σ + jωε₀εᵣ)∇φ) = 0
/// swept over geometrically spaced frequencies. Physically meaningful for dielectric and
/// capacitive studies (lossy insulators, RC media) — NOT for conductor+dielectric
/// composites: the σ-spread conditioning ban of the DC solver stands at AC because
/// ωε₀εᵣ never rescues a ~10²¹ copper/FR4 contrast, so such inputs are rejected loudly
/// with a pointer at the copper-only mesh path and the lumped R + jωL estimator.
/// Conventions: phasor amplitudes are PEAK values; the loss density is the time average
/// ½σ|∇φ|². Assumes no magnetic induction (fields quasi-static — valid while structures
/// are small against the wavelength and skin effect is negligible).
/// </summary>
public sealed class HarmonicElectricSolver : ISolver
{
    private const double Epsilon0 = 8.8541878128e-12;   // vacuum permittivity [F/m]

    /// <summary>Maximum admissible spread of |σ + jωε₀εᵣ| across elements — the same
    /// CG/COCG-conditioning policy as the DC solver's copper+FR4 ban.</summary>
    private const double AdmittivitySpreadLimit = 1e8;

    public string Name => "AC electrical (electro-quasistatic)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The AC electrical solver supports linear (TET4) meshes only; " +
                "re-generate the mesh with linear elements.");

        var settings = input.HarmonicElectric ?? throw new InvalidOperationException(
            "AC sweep settings (frequency range and point count) are missing.");
        if (settings.MinFrequency <= 0)
            throw new InvalidOperationException("The minimum frequency must be positive.");
        if (settings.MaxFrequency < settings.MinFrequency)
            throw new InvalidOperationException("The maximum frequency must be at least the minimum.");
        if (settings.PointCount is < 1 or > 200)
            throw new InvalidOperationException("The sweep point count must lie in [1, 200].");

        ValidateAcMaterial(input.Material);
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                ValidateAcMaterial(material);

        if (!input.BoundaryConditions.OfType<VoltagePotential>().Any())
            throw new InvalidOperationException(
                "At least one voltage potential is required; without a reference potential the solution is not unique.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (VoltagePotential or CurrentFlow))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to an AC electrical solve. " +
                    "Use voltage potentials and current flows.");
            if (bc.FaceIds.Count == 0)
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' has no faces assigned.");
            if (input.Mesh.GetFaceNodes(bc.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' targets faces that do not exist on the mesh.");
        }

        // Early feedback: the spread is worst at one end of the sweep, so checking both
        // ends at validation time catches banned material combinations before solving.
        CheckAdmittivitySpread(input, 2 * Math.PI * settings.MinFrequency);
        CheckAdmittivitySpread(input, 2 * Math.PI * settings.MaxFrequency);
    }

    private static void ValidateAcMaterial(Material material)
    {
        if (material.ElectricalConductivity is null && material.RelativePermittivity is null)
            throw new InvalidOperationException(
                $"Material '{material.Name}': an AC electrical solve needs ElectricalConductivity " +
                "and/or RelativePermittivity set (unset values default to σ = 0 and εᵣ = 1).");
        if (material.ElectricalConductivity is < 0)
            throw new InvalidOperationException(
                $"Material '{material.Name}': electrical conductivity cannot be negative.");
        if (material.RelativePermittivity is <= 0)
            throw new InvalidOperationException(
                $"Material '{material.Name}': relative permittivity must be positive.");
    }

    private static double Sigma(Material m) => m.ElectricalConductivity ?? 0;
    private static double Permittivity(Material m) => Epsilon0 * (m.RelativePermittivity ?? 1);

    private static void CheckAdmittivitySpread(SolveInput input, double omega)
    {
        double min = double.MaxValue, max = 0;
        for (int e = 0; e < input.Mesh.ElementCount; e++)
        {
            var m = input.MaterialOf(e);
            double magnitude = new Complex(Sigma(m), omega * Permittivity(m)).Magnitude;
            min = Math.Min(min, magnitude);
            max = Math.Max(max, magnitude);
        }
        if (max / min > AdmittivitySpreadLimit)
            throw new InvalidOperationException(
                $"The admittivity |σ + jωε| spans a factor of {max / min:g2} across the mesh at " +
                $"ω = {omega:g3} rad/s — far beyond what the iterative solver can condition (limit 1e8). " +
                "Conductor+dielectric composites stay banned at AC just like at DC: mesh the copper " +
                "alone (copper-only PCB path) for conduction, or use the lumped R + jωL trace-impedance " +
                "estimate in the electrical setup panel.");
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var settings = input.HarmonicElectric!;

        log.Add("Electro-quasistatic sweep: peak phasors; loss density is the time average ½σ|∇φ|²; " +
                "magnetic induction and skin effect are neglected.");

        progress?.Report(new SolverProgress("Assembling σ and ε matrices", 0.03));
        var assembler = new ScalarDiffusionAssembler(mesh, el => Sigma(input.MaterialOf(el)));
        var conductance = assembler.AssembleStiffness(cancellationToken: cancellationToken);
        var capacitance = new ScalarDiffusionAssembler(mesh, el => Permittivity(input.MaterialOf(el)))
            .AssembleStiffness(cancellationToken: cancellationToken);
        log.Add($"Assembled {conductance.RowCount} DOF system, {conductance.NonZeroCount} non-zeros " +
                "(σ and ε share one sparsity).");

        var loads = new double[mesh.NodeCount];
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
        {
            ScalarSolverHelpers.DistributeOverFaces(mesh, current.FaceIds, current.TotalCurrent, loads, current.Name);
            log.Add($"Current '{current.Name}': {current.TotalCurrent:g4} A (peak, 0° phase) injected.");
        }
        var complexLoads = new Complex[mesh.NodeCount];
        for (int i = 0; i < loads.Length; i++)
            complexLoads[i] = loads[i];

        var prescribed = new Dictionary<int, Complex>();
        foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
        {
            var nodes = mesh.GetFaceNodes(voltage.FaceIds);
            foreach (int node in nodes)
                prescribed[node] = voltage.Volts;
            log.Add($"Voltage '{voltage.Name}': {voltage.Volts:g4} V (peak, 0° phase) on {nodes.Count} nodes.");
        }

        var frequencies = SweepFrequencies(settings);
        var electrodes = input.BoundaryConditions.OfType<VoltagePotential>().ToList();
        var currents = input.BoundaryConditions.OfType<CurrentFlow>().ToList();
        var drive = ResolveDriveMode(electrodes, currents);
        if (drive == DriveMode.None)
            log.Add("Impedance is only reported for two voltage electrodes at different potentials, " +
                    "or one injected current against a common voltage reference.");

        var frames = new List<ResultFrame>();
        Complex[]? phi = null;
        for (int fi = 0; fi < frequencies.Length; fi++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double f = frequencies[fi];
            double omega = 2 * Math.PI * f;
            CheckAdmittivitySpread(input, omega);

            var system = ComplexCsrMatrix.Combine(conductance, capacitance, omega);
            // Warm start from the previous frequency — adjacent sweep points are close.
            var result = ComplexConstrainedSystemSolver.Solve(system, complexLoads, prescribed,
                warmStart: phi, cancellationToken: cancellationToken);
            phi = result.Solution;

            Dictionary<string, double>? frameSummary = null;
            Complex? z = drive switch
            {
                DriveMode.TwoVoltage => TerminalImpedance(mesh, system, phi, complexLoads, electrodes),
                DriveMode.CurrentInjection => CurrentDriveImpedance(phi, loads,
                    currents[0].TotalCurrent, electrodes[0].Volts),
                _ => null
            };
            if (z is { } impedance)
            {
                frameSummary = new Dictionary<string, double>
                {
                    ["Frequency (Hz)"] = f,
                    ["|Z| (Ω)"] = impedance.Magnitude,
                    ["Phase (°)"] = impedance.Phase * 180 / Math.PI
                };
                log.Add($"f = {FormatFrequency(f)}: |Z| = {impedance.Magnitude:g4} Ω, " +
                        $"phase {impedance.Phase * 180 / Math.PI:g3}° " +
                        $"(COCG {result.Iterations.Iterations} iterations).");
            }
            frames.Add(BuildFrame(f, omega, phi, mesh, assembler, input, frameSummary));
            progress?.Report(new SolverProgress($"f = {FormatFrequency(f)}",
                0.05 + 0.95 * (fi + 1) / frequencies.Length));
        }

        var summary = new Dictionary<string, double>();
        if (frames[0].Summary is { } first)
        {
            summary["|Z| at f min (Ω)"] = first["|Z| (Ω)"];
            summary["Phase at f min (°)"] = first["Phase (°)"];
        }
        if (frames.Count > 1 && frames[^1].Summary is { } last)
        {
            summary["|Z| at f max (Ω)"] = last["|Z| (Ω)"];
            summary["Phase at f max (°)"] = last["Phase (°)"];
        }

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = frames[0].Fields,   // default frame: the lowest frequency
            Log = log,
            Frames = frames,
            FrameAxis = "Frequency",
            Summary = summary.Count > 0 ? summary : null
        };
    }

    private static double[] SweepFrequencies(HarmonicElectricSettings settings)
    {
        int n = settings.PointCount;
        var frequencies = new double[n];
        if (n == 1)
        {
            frequencies[0] = settings.MinFrequency;
            return frequencies;
        }
        double ratio = settings.MaxFrequency / settings.MinFrequency;
        for (int k = 0; k < n; k++)
            frequencies[k] = settings.MinFrequency * Math.Pow(ratio, (double)k / (n - 1));
        return frequencies;
    }

    private static ResultFrame BuildFrame(double frequency, double omega, Complex[] phi,
        FeMesh mesh, ScalarDiffusionAssembler assembler, SolveInput input,
        Dictionary<string, double>? frameSummary)
    {
        var magnitude = new double[mesh.NodeCount];
        var phase = new double[mesh.NodeCount];
        var phiRe = new double[mesh.NodeCount];
        var phiIm = new double[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
        {
            magnitude[i] = phi[i].Magnitude;
            phase[i] = phi[i].Phase * 180 / Math.PI;
            phiRe[i] = phi[i].Real;
            phiIm[i] = phi[i].Imaginary;
        }

        var currentDensity = new double[mesh.ElementCount];
        var lossDensity = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var m = input.MaterialOf(e);
            var gradRe = assembler.ElementGradient(e, phiRe);
            var gradIm = assembler.ElementGradient(e, phiIm);
            double gradSquared = gradRe.LengthSquared + gradIm.LengthSquared;   // |∇φ|² (peak)
            double admittivity = new Complex(Sigma(m), omega * Permittivity(m)).Magnitude;
            currentDensity[e] = admittivity * Math.Sqrt(gradSquared);           // |J| = |σ+jωε|·|∇φ|
            lossDensity[e] = 0.5 * Sigma(m) * gradSquared;                      // time-averaged
        }

        var fields = new IResultField[]
        {
            new NodalScalarField("Potential magnitude", "V", magnitude),
            new NodalScalarField("Potential phase", "°", phase),
            new NodalScalarField("Current density |J|", "A/m²",
                ScalarSolverHelpers.NodalAverage(mesh, currentDensity)),
            new NodalScalarField("Loss density", "W/m³",
                ScalarSolverHelpers.NodalAverage(mesh, lossDensity))
        };
        return new ResultFrame($"f = {FormatFrequency(frequency)}", frequency, fields)
        {
            Unit = "Hz",
            Summary = frameSummary
        };
    }

    /// <summary>How the two-terminal impedance is extracted from the field solution.</summary>
    private enum DriveMode
    {
        /// <summary>No supported two-terminal configuration — fields only, no |Z|.</summary>
        None,
        /// <summary>Exactly two voltage electrodes at different potentials: Z = ΔV/I from reactions.</summary>
        TwoVoltage,
        /// <summary>One injected current against voltage electrodes at one common potential
        /// (mirrors the DC solver's current-drive rule): Z from the port power.</summary>
        CurrentInjection
    }

    private static DriveMode ResolveDriveMode(IReadOnlyList<VoltagePotential> electrodes,
        IReadOnlyList<CurrentFlow> currents)
    {
        if (electrodes.Count == 2 && currents.Count == 0 && electrodes[0].Volts != electrodes[1].Volts)
            return DriveMode.TwoVoltage;
        if (currents.Count == 1 && currents[0].TotalCurrent != 0
            && electrodes.Count > 0 && electrodes.All(e => e.Volts == electrodes[0].Volts))
            return DriveMode.CurrentInjection;
        return DriveMode.None;
    }

    /// <summary>
    /// Current-drive impedance Z = (Σᵢ φᵢ fᵢ)/I² − V_g/I, with fᵢ the (real) injected
    /// nodal loads (Σfᵢ = I) and V_g the common reference potential. This is the
    /// energy-consistent port voltage — Z = 2S/|I|² with S = ½φᴴf for peak phasors — and
    /// reduces exactly to the DC solver's R = P/I² as ω → 0, so DC and AC current-drive
    /// answers agree by construction.
    /// </summary>
    private static Complex CurrentDriveImpedance(Complex[] phi, double[] loads,
        double totalCurrent, double groundVolts)
    {
        Complex weighted = Complex.Zero;
        for (int i = 0; i < loads.Length; i++)
            if (loads[i] != 0)
                weighted += phi[i] * loads[i];
        return weighted / (totalCurrent * totalCurrent) - groundVolts / totalCurrent;
    }

    /// <summary>Z = ΔV / I from the complex nodal reactions A·φ − f summed over the
    /// higher-potential electrode (the complex analogue of the DC electrode current).</summary>
    private static Complex? TerminalImpedance(FeMesh mesh, ComplexCsrMatrix system, Complex[] phi,
        Complex[] loads, IReadOnlyList<VoltagePotential> electrodes)
    {
        var reactions = new Complex[phi.Length];
        system.Multiply(phi, reactions);
        for (int i = 0; i < reactions.Length; i++)
            reactions[i] -= loads[i];

        var driven = electrodes[0].Volts > electrodes[1].Volts ? electrodes[0] : electrodes[1];
        Complex current = Complex.Zero;
        foreach (int node in mesh.GetFaceNodes(driven.FaceIds))
            current += reactions[node];
        if (current == Complex.Zero)
            return null;
        double deltaV = Math.Abs(electrodes[0].Volts - electrodes[1].Volts);
        return deltaV / current;
    }

    private static string FormatFrequency(double hz) => hz switch
    {
        >= 1e9 => $"{hz / 1e9:g4} GHz",
        >= 1e6 => $"{hz / 1e6:g4} MHz",
        >= 1e3 => $"{hz / 1e3:g4} kHz",
        _ => $"{hz:g4} Hz"
    };
}
