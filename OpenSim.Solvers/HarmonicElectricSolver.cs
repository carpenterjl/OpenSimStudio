using System.Numerics;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Harmonic electro-quasistatic (EQS) solver over TET4 elements: ∇·((σ + jωε₀εᵣ)∇φ) = 0
/// swept over geometrically spaced frequencies. Physically meaningful for dielectric and
/// capacitive studies (lossy insulators, RC media) and for conductors in a dielectric
/// (copper plates in FR4: the capacitor they form, the laminate's leakage, a floating
/// shield between them). NOT for a metal alone: with ωε ≪ σ everywhere the equation is
/// the DC one at every frequency, and the sweep says so (<see cref="NoteAllConductors"/>).
/// <para>
/// A conductor+dielectric composite spans a 10²¹ admittivity ratio that no single
/// iterative solve resolves, so — like the DC solver — it is solved in two stages wherever
/// the ratio exceeds 10⁸: the conductors that reach a voltage first, with the dielectric
/// carrying nothing (to one part in the ratio), then the dielectric and any floating
/// conductor with the conductor potentials imposed. The current the dielectric draws from a
/// conductor's surface — the displacement current of the capacitor, the leakage of the
/// laminate — is credited to that conductor's electrode, so the impedance is the whole
/// structure's. At a frequency where ωε has brought the spread under 10⁸ the mesh is solved
/// as one system.
/// </para>
/// Conventions: phasor amplitudes are PEAK values; the loss density is the time average
/// ½σ|∇φ|². Assumes no magnetic induction (fields quasi-static — valid while structures
/// are small against the wavelength and skin effect is negligible).
/// </summary>
public sealed class HarmonicElectricSolver : ISolver
{
    private const double Epsilon0 = 8.8541878128e-12;   // vacuum permittivity [F/m]

    /// <summary>Maximum spread of |σ + jωε₀εᵣ| across elements solved as ONE system — the
    /// same limit past which the DC solver stages conductors and insulators
    /// (<see cref="ElectricalConductionSolver.ConductivitySpreadLimit"/>).</summary>
    private const double AdmittivitySpreadLimit = ConductorPartition.SpreadLimit;

    private const double Mu0 = 4e-7 * Math.PI;           // vacuum permeability [H/m]

    public string Name => "AC electrical (electro-quasistatic)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The AC electrical solver supports linear tetrahedral (TET4) meshes only; " +
                "re-generate the mesh with linear tetrahedral elements.");

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
            BoundaryScope.Validate(bc, input.Mesh);
        }

        if (input.ElectricalContacts is not null)
            foreach (var contact in input.ElectricalContacts)
                contact.Validate(input.Mesh.NodeCount);

        var partition = Partition(input);
        EquipotentialTerminals.Of(input.Mesh, input.BoundaryConditions);

        // The spread is worst at one end of the sweep. Past the limit the conductors and
        // insulators are staged; what cannot be staged is a spread with no conductor /
        // insulator split behind it (two dielectrics 10⁸ apart in ε), which no stage helps.
        foreach (double f in new[] { settings.MinFrequency, settings.MaxFrequency })
        {
            double spread = Spread(input, 2 * Math.PI * f);
            if (spread > AdmittivitySpreadLimit && !partition.Staged)
                throw new InvalidOperationException(
                    $"The admittivity |σ + jωε| spans a factor of {spread:g2} across the mesh at " +
                    $"{FormatFrequency(f)} with no conductor/insulator split to stage (limit 1e8 for one " +
                    "system). Check the materials' permittivities.");
        }
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

    private static ConductorPartition Partition(SolveInput input) =>
        ConductorPartition.Of(input.Mesh, e => Sigma(input.MaterialOf(e)), input.BoundaryConditions,
            input.ElectricalContacts);

    /// <summary>max|σ + jωε| / min|σ + jωε| over the elements.</summary>
    private static double Spread(SolveInput input, double omega)
    {
        double min = double.MaxValue, max = 0;
        for (int e = 0; e < input.Mesh.ElementCount; e++)
        {
            var m = input.MaterialOf(e);
            double magnitude = new Complex(Sigma(m), omega * Permittivity(m)).Magnitude;
            min = Math.Min(min, magnitude);
            max = Math.Max(max, magnitude);
        }
        return min > 0 ? max / min : double.PositiveInfinity;
    }

    /// <summary>
    /// A sweep over a mesh that is ALL conductor (ωε ≪ σ in every element up to f max)
    /// solves ∇·(σ∇φ) = 0 at every frequency: |Z| is the DC resistance throughout, with
    /// no inductance and no skin effect, and looked like a measured flat response. Said
    /// here, with the skin depth against the body so the reader can see where even the
    /// resistance stops holding. Returns the warning, or null when a dielectric takes part.
    /// </summary>
    internal static string? NoteAllConductors(SolveInput input, HarmonicElectricSettings settings)
    {
        double omegaMax = 2 * Math.PI * settings.MaxFrequency;
        double muSigma = 0;
        for (int e = 0; e < input.Mesh.ElementCount; e++)
        {
            var m = input.MaterialOf(e);
            if (omegaMax * Permittivity(m) > 1e-3 * Sigma(m)) return null;
            muSigma = Math.Max(muSigma, Mu0 * (m.RelativePermeability ?? 1) * Sigma(m));
        }

        var bounds = Aabb.FromPoints(input.Mesh.Nodes);
        var size = bounds.Max - bounds.Min;
        double extent = Math.Min(size.X, Math.Min(size.Y, size.Z));
        double depth = Math.Sqrt(2 / (omegaMax * muSigma));
        // δ = extent/2: below this the current fills the cross-section.
        double crossover = 4 / (Math.PI * muSigma * extent * extent);
        string tail = crossover < settings.MaxFrequency
            ? $"above about {FormatFrequency(crossover)} the current no longer fills the " +
              "cross-section, so the real resistance is higher than shown as well."
            : "the resistance itself holds over this sweep; the inductive part does not exist here.";
        return "WARNING: every material in this mesh is a conductor over the whole sweep (ωε below " +
               "0.001·σ), so this quasi-static solve returns the DC resistance as |Z| at every " +
               "frequency: it has no inductance and no skin effect. Skin depth at " +
               $"{FormatFrequency(settings.MaxFrequency)} is {depth * 1e3:g3} mm against a smallest " +
               $"body extent of {extent * 1e3:g3} mm; " + tail +
               " For a trace's impedance against frequency use the lumped R + jωL estimate.";
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var settings = input.HarmonicElectric!;
        var partition = Partition(input);
        var terminals = EquipotentialTerminals.Of(mesh, input.BoundaryConditions);
        var contacts = input.ElectricalContacts;

        log.Add("Electro-quasistatic sweep: peak phasors; loss density is the time average ½σ|∇φ|²; " +
                "magnetic induction and skin effect are neglected.");
        if (NoteAllConductors(input, settings) is { } metallic) log.Add(metallic);
        var groups = ScalarSolverHelpers.MaterialGroups(input, out var groupNames);
        if (groups is not null)
            log.Add(ScalarSolverHelpers.InterfaceNote("current density and loss density"));
        if (contacts is not null)
            foreach (var contact in contacts)
                log.Add($"Electrical contact bodies {contact.BodyA}–{contact.BodyB}: G = {contact.Conductance:g4} S/m² " +
                        $"over {contact.CoupledArea:g4} m².");

        var loads = new double[mesh.NodeCount];
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
        {
            ScalarSolverHelpers.DistributeOverFaces(mesh, current.FaceIds, current.TotalCurrent, loads, current.Name);
            log.Add(current.Equipotential
                ? $"Terminal '{current.Name}': {current.TotalCurrent:g4} A (peak, 0° phase) into one equipotential face."
                : $"Current '{current.Name}': {current.TotalCurrent:g4} A (peak, 0° phase) injected at uniform density.");
        }
        var complexLoads = new Complex[mesh.NodeCount];
        for (int i = 0; i < loads.Length; i++)
            complexLoads[i] = loads[i];

        var prescribedVolts = new Dictionary<int, double>();
        var prescribed = new Dictionary<int, Complex>();
        foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
        {
            var nodes = mesh.GetScopeNodes(voltage);
            foreach (int node in nodes)
            {
                prescribedVolts[node] = voltage.Volts;
                prescribed[node] = voltage.Volts;
            }
            log.Add($"Voltage '{voltage.Name}': {voltage.Volts:g4} V (peak, 0° phase) on {nodes.Count} nodes.");
        }

        var frequencies = SweepFrequencies(settings);
        var electrodes = input.BoundaryConditions.OfType<VoltagePotential>().ToList();
        var currents = input.BoundaryConditions.OfType<CurrentFlow>().ToList();
        var drive = ResolveDriveMode(electrodes, currents);
        if (drive == DriveMode.None)
            log.Add("Impedance is only reported for two voltage electrodes at different potentials, " +
                    "or one injected current against a common voltage reference.");

        if (partition.Staged)
            log.Add($"{partition.InsulatorElements} of {mesh.ElementCount} elements are insulators (conductivity " +
                    $"below 1/{AdmittivitySpreadLimit:G1} of the best conductor). Wherever |σ + jωε| spans more " +
                    "than 1e8 the conductors are solved first and the insulators second with the conductor " +
                    "potentials imposed — the current the insulators draw from a conductor's surface is " +
                    "credited to its electrode; where ωε brings the spread under 1e8 the mesh is one system.");

        var sigmaOf = (Func<int, double>)(e => partition.Sigma[e]);
        var epsOf = (Func<int, double>)(e => Permittivity(input.MaterialOf(e)));
        var context = new Context(mesh, partition, terminals, contacts, sigmaOf, epsOf, complexLoads, loads,
            prescribed, prescribedVolts, electrodes, cancellationToken);
        OneSystem? one = null;
        Staged? staged = null;
        double neglected = 0;
        bool anyStaged = false, anyOne = false;

        var frames = new List<ResultFrame>();
        for (int fi = 0; fi < frequencies.Length; fi++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double f = frequencies[fi];
            double omega = 2 * Math.PI * f;
            bool stageThis = partition.Staged && Spread(input, omega) > AdmittivitySpreadLimit;

            Engine engine;
            if (stageThis)
            {
                staged ??= new Staged(context, log);
                engine = staged;
                anyStaged = true;
                neglected = Math.Max(neglected, staged.NeglectedRatio(omega));
            }
            else
            {
                one ??= new OneSystem(context, log);
                engine = one;
                anyOne = true;
            }
            var step = engine.Solve(omega);

            Dictionary<string, double>? frameSummary = null;
            Complex? z = drive switch
            {
                DriveMode.TwoVoltage => TerminalImpedance(step.ElectrodeCurrents, electrodes),
                DriveMode.CurrentInjection => CurrentDriveImpedance(step.Phi, loads,
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
                        $"phase {impedance.Phase * 180 / Math.PI:g3}° ({step.Iterations}).");
            }
            else
                log.Add($"f = {FormatFrequency(f)}: solved ({step.Iterations}).");
            frames.Add(BuildFrame(f, omega, step.Phi, mesh, engine.Assembler, engine.SolvedSigma, engine.SolvedPermittivity,
                frameSummary, groups, groupNames));
            progress?.Report(new SolverProgress($"f = {FormatFrequency(f)}",
                0.05 + 0.95 * (fi + 1) / frequencies.Length));
        }

        if (anyStaged)
            log.Add($"Staged solve: stage one neglects the insulators' admittance against the conductors', " +
                    $"at most {neglected:g2} relative over this sweep." +
                    (anyOne ? " Frequencies with a spread under 1e8 were solved as one system." : ""));
        if (partition.FloatingRegions.Count > 0)
            log.Add($"Mesh region(s) {string.Join(", ", partition.FloatingRegions)} hold conductor that " +
                    "reaches no voltage potential: it floats with the dielectric around it (solved as an " +
                    $"equipotential, {ElectricalConductionSolver.FloatingConductorContrast:G1}× the insulators' " +
                    "admittivity, at the frequencies that are staged).");
        if (terminals is not null && frames.Count > 0)
            for (int t = 0; t < terminals.Terminals.Count; t++)
                log.Add($"Terminal '{terminals.Terminals[t].Name}': equipotential; its potential is the " +
                        "'Potential magnitude' on its face.");

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

    /// <summary>What both engines need, built once per solve.</summary>
    private sealed record Context(FeMesh Mesh, ConductorPartition Partition, EquipotentialTerminals? Terminals,
        IReadOnlyList<ContactInterface>? Contacts, Func<int, double> SigmaOf, Func<int, double> EpsilonOf,
        Complex[] Loads, double[] RealLoads, Dictionary<int, Complex> Prescribed,
        Dictionary<int, double> PrescribedVolts, IReadOnlyList<VoltagePotential> Electrodes,
        CancellationToken Ct);

    private readonly record struct Step(Complex[] Phi, Complex[] ElectrodeCurrents, string Iterations);

    private abstract class Engine
    {
        /// <summary>σ and ε each element was solved with (a floating conductor's are the contrast values).</summary>
        public abstract double[] SolvedSigma { get; }
        public abstract double[] SolvedPermittivity { get; }
        /// <summary>Any assembler over the mesh — the gradients do not depend on the coefficient.</summary>
        public abstract ScalarDiffusionAssembler Assembler { get; }
        public abstract Step Solve(double omega);

        protected static IReadOnlyList<ContactInterface>? ZeroConductance(IReadOnlyList<ContactInterface>? contacts) =>
            contacts?.Select(c => c with { Conductance = 0 }).ToList();

        protected static (Complex[] Free, int Iterations) Cocg(ComplexCsrMatrix matrix, Complex[] rhs, Complex[] start,
            CancellationToken ct)
        {
            if (rhs.Length == 0) return (start, 0);
            var cocg = new CocgSolver { Tolerance = 1e-10, MaxIterations = Math.Max(4 * rhs.Length, 1000) };
            var iterations = cocg.Solve(matrix, rhs, start, ct);
            if (!iterations.Converged)
                throw new InvalidOperationException(
                    $"COCG did not converge after {iterations.Iterations} iterations " +
                    $"(residual {iterations.ResidualNorm:g3}). Check materials and mesh quality.");
            return (start, iterations.Iterations);
        }

        /// <summary>Σ(A·φ − f) over each electrode's nodes, plus any credited current.</summary>
        protected static Complex[] ElectrodeCurrents(Context c, Complex[] reactions, Complex[]? credit)
        {
            var currents = new Complex[c.Electrodes.Count];
            for (int i = 0; i < currents.Length; i++)
            {
                Complex sum = Complex.Zero;
                foreach (int node in c.Mesh.GetScopeNodes(c.Electrodes[i]))
                    sum += reactions[node] - c.Loads[node];
                currents[i] = sum + (credit?[i] ?? Complex.Zero);
            }
            return currents;
        }
    }

    /// <summary>
    /// One complex system over the whole mesh, reduced ONCE for the sweep: the free-DOF map
    /// and the reduced sparsity are properties of the mesh and the electrodes, not of the
    /// frequency; only one multiply per entry depends on omega. Equipotential terminals tie
    /// both real assemblies the same way before the reduction.
    /// </summary>
    private sealed class OneSystem : Engine
    {
        private readonly Context _c;
        private readonly CsrMatrix _re, _im;
        private readonly ComplexReducedSystem _reduced;
        private readonly Complex[] _tiedLoads;
        private Complex[]? _warm;   // tied space

        public OneSystem(Context c, List<string> log)
        {
            _c = c;
            Assembler = new ScalarDiffusionAssembler(c.Mesh, c.SigmaOf);
            _re = Assembler.AssembleStiffness(null, c.Contacts, c.Ct);
            _im = new ScalarDiffusionAssembler(c.Mesh, c.EpsilonOf).AssembleStiffness(null, ZeroConductance(c.Contacts), c.Ct);
            SolvedSigma = Enumerable.Range(0, c.Mesh.ElementCount).Select(c.SigmaOf).ToArray();
            SolvedPermittivity = Enumerable.Range(0, c.Mesh.ElementCount).Select(c.EpsilonOf).ToArray();
            var ties = c.Terminals;
            var (re, im) = ties is null ? (_re, _im) : (ties.Tie(_re), ties.Tie(_im));
            _tiedLoads = ties is null ? c.Loads : ties.TieLoads(c.Loads);
            _reduced = ComplexReducedSystem.Reduce(re, im, ties is null ? c.Prescribed : ties.TiePrescribed(c.Prescribed));
            log.Add($"Assembled {_re.RowCount} DOF system, {_re.NonZeroCount} non-zeros (σ and ε share one sparsity)" +
                    (ties is null ? "." : $"; {ties.Terminals.Count} equipotential terminal(s) tie it to {ties.TiedCount} unknowns."));
        }

        public override double[] SolvedSigma { get; }
        public override double[] SolvedPermittivity { get; }
        public override ScalarDiffusionAssembler Assembler { get; }

        public override Step Solve(double omega)
        {
            var (free, iterations) = Cocg(_reduced.MatrixAt(omega), _reduced.LoadsAt(omega, _tiedLoads),
                _reduced.Restrict(_warm), _c.Ct);
            _warm = _reduced.Expand(free);
            var phi = _c.Terminals is null ? _warm : _c.Terminals.Expand(_warm);

            // The impedance terms read the FULL system, so it is still combined here — one
            // pass over the assembled entries, not a reduction.
            var reactions = new Complex[phi.Length];
            ComplexCsrMatrix.Combine(_re, _im, omega).Multiply(phi, reactions);
            return new Step(phi, ElectrodeCurrents(_c, reactions, null), $"COCG {iterations} iterations, one system");
        }
    }

    /// <summary>
    /// The staged solve: the conductors that reach a voltage first (every other node held at
    /// 0, out of the system), then the insulators and the floating conductors with the
    /// conductor potentials imposed. Both stages are reduced once; stage two's imposed VALUES
    /// change with the frequency and are passed per point. A floating conductor stands at the
    /// contrast factor above the insulators' σ AND ε, so its coefficient is frequency-free and
    /// it is an equipotential to about 10⁻⁴ at every frequency.
    /// </summary>
    private sealed class Staged : Engine
    {
        private readonly Context _c;
        private readonly CsrMatrix _re1, _im1, _re2, _im2;
        private readonly ComplexReducedSystem _first, _second;
        private readonly Complex[] _tiedLoads;
        private readonly Dictionary<int, double> _pieceVolts;
        private readonly HashSet<int> _driven;
        private readonly Dictionary<int, List<int>> _electrodesOfPiece;
        private readonly double _insulatorMaxSigma, _insulatorMaxEpsilon;
        private Complex[]? _warm1, _warm2;

        public Staged(Context c, List<string> log)
        {
            _c = c;
            var mesh = c.Mesh;
            var p = c.Partition;
            int nodes = mesh.NodeCount;

            // Stage one: the anchored conductors at their own σ and ε.
            Assembler = new ScalarDiffusionAssembler(mesh, el => p.Anchored[el] ? c.SigmaOf(el) : 0.0);
            _re1 = Assembler.AssembleStiffness(null, p.StageOneContacts, c.Ct);
            _im1 = new ScalarDiffusionAssembler(mesh, el => p.Anchored[el] ? c.EpsilonOf(el) : 0.0)
                .AssembleStiffness(null, ZeroConductance(p.StageOneContacts), c.Ct);
            var held = new Dictionary<int, Complex>(c.Prescribed);
            int conductorNodes = 0;
            for (int n = 0; n < nodes; n++)
            {
                if (p.NodeAnchored[n]) conductorNodes++;
                else held.TryAdd(n, Complex.Zero);
            }
            var ties = c.Terminals;
            var (re1, im1) = ties is null ? (_re1, _im1) : (ties.Tie(_re1), ties.Tie(_im1));
            _tiedLoads = ties is null ? c.Loads : ties.TieLoads(c.Loads);
            _first = ComplexReducedSystem.Reduce(re1, im1, ties is null ? held : ties.TiePrescribed(held));
            (_pieceVolts, _driven) = p.SnapPieces(c.PrescribedVolts, n => c.RealLoads[n] != 0);
            _electrodesOfPiece = p.ElectrodesOfPieces(mesh, c.Electrodes);

            // Stage two: everything else. Insulators as given; a floating conductor at the
            // contrast above the insulators' largest σ and largest ε.
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (p.Conductor[e]) continue;
                _insulatorMaxSigma = Math.Max(_insulatorMaxSigma, c.SigmaOf(e));
                _insulatorMaxEpsilon = Math.Max(_insulatorMaxEpsilon, c.EpsilonOf(e));
            }
            double contrast = ElectricalConductionSolver.FloatingConductorContrast;
            SolvedSigma = new double[mesh.ElementCount];
            SolvedPermittivity = new double[mesh.ElementCount];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                bool floating = p.Conductor[e] && !p.Anchored[e];
                SolvedSigma[e] = floating ? Math.Min(c.SigmaOf(e), contrast * _insulatorMaxSigma) : c.SigmaOf(e);
                SolvedPermittivity[e] = floating ? Math.Max(c.EpsilonOf(e), contrast * _insulatorMaxEpsilon) : c.EpsilonOf(e);
            }
            _re2 = new ScalarDiffusionAssembler(mesh, el => p.Anchored[el] ? 0.0 : SolvedSigma[el])
                .AssembleStiffness(null, p.StageTwoContacts, c.Ct);
            _im2 = new ScalarDiffusionAssembler(mesh, el => p.Anchored[el] ? 0.0 : SolvedPermittivity[el])
                .AssembleStiffness(null, ZeroConductance(p.StageTwoContacts), c.Ct);
            var imposed = new Dictionary<int, Complex>(c.Prescribed);
            for (int n = 0; n < nodes; n++)
                if (p.NodeAnchored[n]) imposed.TryAdd(n, Complex.Zero);   // values come per frequency
            _second = ComplexReducedSystem.Reduce(_re2, _im2, imposed);

            log.Add($"Staged: conductors {conductorNodes} nodes ({_first.FreeCount} unknowns" +
                    (ties is null ? "" : $", {ties.Terminals.Count} equipotential terminal(s)") +
                    $"), insulators {_second.FreeCount} unknowns.");
        }

        public override double[] SolvedSigma { get; }
        public override double[] SolvedPermittivity { get; }
        public override ScalarDiffusionAssembler Assembler { get; }

        /// <summary>What stage one neglects: the insulators' largest admittivity against the
        /// conductors' smallest, at this frequency.</summary>
        public double NeglectedRatio(double omega)
        {
            double conductorMin = double.MaxValue;
            var p = _c.Partition;
            for (int e = 0; e < p.Conductor.Length; e++)
                if (p.Anchored[e])
                    conductorMin = Math.Min(conductorMin, new Complex(_c.SigmaOf(e), omega * _c.EpsilonOf(e)).Magnitude);
            double insulatorMax = new Complex(_insulatorMaxSigma, omega * _insulatorMaxEpsilon).Magnitude;
            return conductorMin > 0 && conductorMin < double.MaxValue ? insulatorMax / conductorMin : double.NaN;
        }

        public override Step Solve(double omega)
        {
            var c = _c;
            var p = c.Partition;
            int nodes = c.Mesh.NodeCount;

            // ---- Stage one.
            var (free1, it1) = Cocg(_first.MatrixAt(omega), _first.LoadsAt(omega, _tiedLoads),
                _first.Restrict(_warm1), c.Ct);
            _warm1 = _first.Expand(free1);
            var phi = c.Terminals is null ? (Complex[])_warm1.Clone() : c.Terminals.Expand(_warm1);
            var reactions = new Complex[nodes];
            ComplexCsrMatrix.Combine(_re1, _im1, omega).Multiply(phi, reactions);

            // A piece held at one potential with nothing injected: that potential exactly,
            // zero reactions (see the DC solver).
            for (int n = 0; n < nodes; n++)
            {
                if (!p.NodeAnchored[n] || _driven.Contains(p.Piece[n])) continue;
                phi[n] = _pieceVolts[p.Piece[n]];
                reactions[n] = Complex.Zero;
            }

            // ---- Stage two, with the conductor potentials imposed.
            int it2 = 0;
            if (_second.FreeCount > 0)
            {
                var (free2, iterations) = Cocg(_second.MatrixAt(omega), _second.LoadsAt(omega, new Complex[nodes], phi),
                    _second.Restrict(_warm2), c.Ct);
                it2 = iterations;
                phi = _second.Expand(free2, phi);
                _warm2 = phi;
            }
            var rest = new Complex[nodes];
            ComplexCsrMatrix.Combine(_re2, _im2, omega).Multiply(phi, rest);

            // What the insulators draw from a conductor's surface (conduction AND displacement
            // current) is supplied through the conductor by its electrode(s).
            var drawn = new Dictionary<int, Complex>();
            for (int n = 0; n < nodes; n++)
            {
                if (p.NodeAnchored[n] && !c.Prescribed.ContainsKey(n))
                    drawn[p.Piece[n]] = drawn.GetValueOrDefault(p.Piece[n]) + rest[n];
                else
                    reactions[n] += rest[n];
            }
            var credit = new Complex[c.Electrodes.Count];
            foreach (var (piece, current) in drawn)
                if (_electrodesOfPiece.TryGetValue(piece, out var owners))
                    foreach (int i in owners) credit[i] += current / owners.Count;

            return new Step(phi, ElectrodeCurrents(c, reactions, credit),
                $"COCG {it1} + {it2} iterations, staged");
        }
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
        FeMesh mesh, ScalarDiffusionAssembler assembler, double[] sigma, double[] permittivity,
        Dictionary<string, double>? frameSummary, IReadOnlyList<int>? groups, IReadOnlyList<string>? groupNames)
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
            var gradRe = assembler.ElementGradient(e, phiRe);
            var gradIm = assembler.ElementGradient(e, phiIm);
            double gradSquared = gradRe.LengthSquared + gradIm.LengthSquared;   // |∇φ|² (peak)
            double admittivity = new Complex(sigma[e], omega * permittivity[e]).Magnitude;
            currentDensity[e] = admittivity * Math.Sqrt(gradSquared);           // |J| = |σ+jωε|·|∇φ|
            lossDensity[e] = 0.5 * sigma[e] * gradSquared;                      // time-averaged
        }

        var (currentNodal, currentSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, currentDensity, groups, groupNames);
        var (lossNodal, lossSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, lossDensity, groups, groupNames);
        var fields = new IResultField[]
        {
            new NodalScalarField("Potential magnitude", "V", magnitude),
            new NodalScalarField("Potential phase", "°", phase),
            new NodalScalarField("Current density |J|", "A/m²", currentNodal, currentSides),
            new NodalScalarField("Loss density", "W/m³", lossNodal, lossSides)
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
    /// answers agree by construction. On an equipotential terminal every φᵢ is the
    /// terminal's potential, so the same expression is (V_terminal − V_g)/I.
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

    /// <summary>Z = ΔV / I from the complex current into the higher-potential electrode
    /// (the complex analogue of the DC electrode current).</summary>
    private static Complex? TerminalImpedance(Complex[] electrodeCurrents, IReadOnlyList<VoltagePotential> electrodes)
    {
        int driven = electrodes[0].Volts > electrodes[1].Volts ? 0 : 1;
        var current = electrodeCurrents[driven];
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
