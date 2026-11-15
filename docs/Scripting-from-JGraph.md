# Scripting OpenSim from JGraph

OpenSim has no scripting language of its own, by decision (FU-46, 2026-10-25). Loops over
solves, a decision between two studies, post-processing and plots run in **JGraph** — a
MATLAB-compatible technical-computing environment on .NET 8 — which drives OpenSim's libraries
**in-process** through its MATLAB-style .NET interop: `NET.addAssembly` loads an assembly, a dotted
name reaches a type, a property is read and written with a dot, a JGS function handle becomes a
.NET delegate where one is wanted. JSON job files and `opensim sweep` remain for a study that
needs no logic between runs.

The worked script is [`examples/jgraph/opensim_from_jgraph.m`](../examples/jgraph/opensim_from_jgraph.m).
It was run with `jgraph -batch` on 2026-10-25 against the Debug build in `OpenSim.Cli/bin/Debug/net8.0/`
and printed, after the meshing and solver log lines:

```
RESULT Max Temperature (K) = 400
RESULT Min Temperature (K) = 300
RESULT Max Heat flux (W/m²) = 2e+06
RESULT Min Heat flux (W/m²) = 2e+06
RESULT Z0 = 50.4731 ohm, eeff = 1.88472
```

(2e6 W/m² is k·ΔT/L = 400 × 100 / 0.02 for the bar; the microstrip numbers are the impedance
calculator's.) `OpenSim.Tests/Cli/ScriptingSurfaceTests.cs` makes the same calls directly and
pins these numbers, and checks by reflection that every entry point below is public with the
signature shown — a rename fails there before it fails a script.

## Loading the assemblies

```matlab
bin = 'D:\...\OpenSimStudio\OpenSim.Cli\bin\Debug\net8.0\';   % or a published folder
libs = {'OpenSim.Core', 'OpenSim.Geometry', 'OpenSim.Meshing', 'OpenSim.Solvers', 'OpenSim.Pcb', 'OpenSim.Rf', 'OpenSim.Cfd'};
for i = 1:numel(libs), NET.addAssembly([bin libs{i} '.dll']); end
NET.addAssembly([bin 'opensim.dll']);                             % the command-line assembly, last
```

Two things to know, both JGraph's behaviour (found 2026-10-25):

- **Only the added assembly's types become visible**, as in MATLAB: a script that names
  `OpenSim.Core.Model.Body` must have added `OpenSim.Core.dll`, not only `opensim.dll`.
- **JGraph's load context does not probe the assembly's folder for its dependencies** (MATLAB's
  does). Add the libraries first and `opensim.dll` last, as above; added the other way round, the
  command-line assembly's types are not catalogued (`a.Classes` on its `NET.Assembly` throws
  `FileNotFoundException: OpenSim.Core`). Logged for JGraph as TC-3 in `plans/open_task_chips.xlsx`.

## Entry points

The same calls the command line's `solve` makes (`OpenSim.Cli/CliRunner.cs`):

| Step | Call |
|---|---|
| Load / save a project | `OpenSim.Core.Persistence.ProjectSerializer().Load(path)`, `.Save(project, path)` |
| Materials | `OpenSim.Core.Persistence.MaterialLibrary()` (the user's library; the project's own materials win, logged) |
| Settings | `OpenSim.Cli.StudySettings.FromProject(project.Solver)` — then set properties (`Duration`, `TimeStep`, `MinFrequency`, …) |
| Mesh | `OpenSim.Cli.StudyRunner.MeshBodies(project, OpenSim.Cli.StudyRunner.DefaultMesher(), remesh, @(s) disp(s))` |
| Solve | `run = OpenSim.Cli.StudyRunner.Solve(project, 'Thermal', materials, settings, @(s) disp(s))` — the analysis is one of `StudyRunner.Analyses`, or `[]` for the project's own |
| Read results | `OpenSim.Cli.StudyRunner.Outputs(run)` — a `Dictionary<string,double>` of the report's scalar outputs; `run.Output` is the full `SolveOutput`, `run.Report.ToMarkdown()` the report |
| Impedance calculator | `OpenSim.Rf.Si.ImpedanceCalculator.Solve(lineSpec)` with a `OpenSim.Rf.Si.LineSpec()` whose properties are set |
| EMI study | `OpenSim.Rf.Emi.EmiStudy.Run(input, OpenSim.Rf.Emi.EmiStudy.NamedLimit('FccConductedClassBQuasiPeak'), ...)` |
| Planar magnetics | `OpenSim.Pcb.Magnetics.PlanarMagneticsStudy.Run(input)` |
| Sweeps | the JSON plan path is `opensim sweep`; from a script, loop over `Solve` yourself and plot |

Building a project in the script is plain object construction (see the example): `SimProject()`,
`Body()`, `OpenSim.Geometry.PrimitiveFactory.CreateBox(x, y, z)`, `MeshSettings()`, `Material()`,
boundary conditions such as `FixedTemperature()`, added with `project.Bodies.Add(body)` and
`body.BoundaryConditions.Add(bc)`.

**Lists.** Model properties typed `IReadOnlyList<int>` (a boundary condition's `FaceIds`) take no
plain number from a script — the interop converts numbers to arrays, not to a generic interface.
Hand over a `List<int>`, which implements it:

```matlab
faces = NET.createGeneric('System.Collections.Generic.List', {'System.Int32'}, 1);
faces.Add(int32(4));
cold.FaceIds = faces;
```

**Delegates.** Where a method takes `Action<string>` (the log) or `IProgress<T>`, pass a function
handle (`@(s) disp(s)`) or `[]`.

## Optics

The optics model joins the surface (OP-98). A script works on the saved records the editors edit
— `OpenSim.Core.Model.OpticsSettings` and its parts — and hands them, with a list of glass
catalogs, to the engines, which build the lens from them as the application does. The worked
script is [`examples/jgraph/optics_from_jgraph.m`](../examples/jgraph/optics_from_jgraph.m): it
builds a fused-silica singlet, adds the default merit function to a focal-length row, optimises
it, traces one ray, runs Quick Sensitivity on the two radii and plots the FFT MTF. It was run
with `jgraph -batch` on 2026-11-14 against the Debug build of this commit and printed:

```
RESULT merit rows = 40, start merit = 4.65323
RESULT merit 4.65323 -> 0.0282677 after 21 cycles (Converged)
RESULT R1 = 56.0911 mm, R2 = -245.387 mm
RESULT marginal ray of field 2: RAYE = 0, RAYV = 0
RESULT nominal RMS spot radius = 0.0302453 mm
RESULT tolerance row 1 (TRAD): change 7.13579e-05 at min, -6.92547e-05 at max
RESULT tolerance row 2 (TRAD): change -3.61436e-06 at min, 3.63611e-06 at max
RESULT Field 1 (0, 0°) T: MTF(0) = 1, min = 2.86042e-18, max = 1
RESULT Field 1 (0, 0°) S: MTF(0) = 1, min = 4.97391e-18, max = 1
RESULT Field 2 (0, 3°) T: MTF(0) = 1, min = 1.50864e-18, max = 1
RESULT Field 2 (0, 3°) S: MTF(0) = 1, min = 3.33819e-18, max = 1
RESULT Diffraction limit: MTF(0) = 1, min = 4.88386e-17, max = 1
```

and wrote the plot to `optics_from_jgraph_mtf.png` in the working folder. (The run used JGraph
built from source on 2026-11-11, `JGraph.Cli/bin/Release/net8.0/jgraph.exe`; the JGraph 0.2.2
installed under `C:\Program Files\JGraph` has no `NET` interop and stops at the first
`NET.addAssembly`.) `ScriptingSurfaceTests.TheOpticsScript_ByDirectCall_BuildsOptimisesAndPlotsMtf`
makes the same calls and checks that the merit falls, EFFL moves toward its target, the ray has no
error code, every tolerance trial succeeds, and every MTF curve is 1 at zero frequency and stays in
[0, 1]; the optimizer's numbers above are not asserted (its own gates pin it).

### Loading

```matlab
NET.addAssembly([bin 'OpenSim.Core.dll']);
NET.addAssembly([bin 'OpenSim.Optics.dll']);
```

`OpenSim.Optics.dll` sits next to the others in `OpenSim.Cli/bin/Debug/net8.0/`. Core goes first,
for the reason given above.

### Entry points

`OpenSim.Optics.Scripting.OpticsScripting` (called `S` below) is a small facade over steps a script
would otherwise repeat; everything else is the library's own public types.

| Step | Call |
|---|---|
| A project's lens | `optics = S.Load(path)` — `SimProject.Optics` of a saved project; save with `ProjectSerializer().Save(project, path)` after setting `project.Optics` |
| Glass catalogs | `S.Catalogs(optics)` — the user's library narrowed to the lens's active catalogs; or `OpenSim.Optics.Materials.GlassCatalogLibrary.BuiltInCatalogs` |
| System data | `OpenSim.Core.Model.OpticsSystemSettings()`: `ApertureType`, `ApertureValue`, `Fields` (a `List<OpticsFieldSettings>`), `Wavelengths` (a `List<OpticsWavelengthSettings>`), `PrimaryWavelength`; named lines in `OpenSim.Optics.SpectralLines` (`F`, `LowerD`, `C`, …) |
| Lens data | `OpticsSurfaceSettings()`: `Radius`, `Thickness`, `ThicknessIsInfinite`, `Material` (a catalog glass name) or `Index`, `IsStop`, `Flags` (a `List<OpticsCellFlagSettings>`: `Column`, `Kind` = `Variable` / `Solve` / `Pickup`, `Solve`, `SolveParameters`) |
| Merit function | rows `OpenSim.Optics.Merit.MeritOperandRow.Of(code, int1, int2, target, weight, data)` into `MeritFunction(rows)`, saved with `MeritFunctionSettings.ToSettings`; the wizard: `optics = S.WithDefaultMerit(optics, catalogs, options)` (`options` a `DefaultMeritOptions`, or `[]`); evaluate: `S.EvaluateMerit(optics, catalogs)` (`.Value`, `.Rows`) |
| Multi-configuration | `optics.MultiConfiguration` (`OpticsMultiConfigSettings`: `ConfigurationCount`, `CurrentConfiguration`, `Operands`); `OpenSim.Optics.MultiConfig.MultiConfiguration.SettingsFor(optics, k, catalogs)` is configuration k's record, `.WithValue` sets a cell |
| Local optimizer (OP-57) | `opt = OpenSim.Optics.Optimization.LocalOptimizer.Run(optics, catalogs)` (optional `LocalOptimizationOptions`); `opt.StartMerit`, `opt.Merit`, `opt.Cycles`, `opt.Stop`, `opt.Optimized` (the record with the variables set) |
| Any analysis by id | `S.AnalysisIds()` lists the ids; `s = S.AnalysisSettings('FftMtf')` gives the defaults to change; `r = S.Analyze(optics, catalogs, 'FftMtf', s)` (`s` may be `[]`) — `r.Text`, `r.Plots` (`LinePlot`: `Curves`, each with `Name`, `X`, `Y`; `AnalysisScatterPlot`; `AnalysisMapPlot`), `r.Assumptions` |
| One ray | `S.TraceRay(optics, catalogs, hx, hy, px, py, wavelength)` — `.Path` (surface by surface, meters), `.Error` (OpticStudio's RAYE: 0, +n missed surface n, −n total internal reflection at n, −9999 not launched), `.Vignetted` (RAYV) |
| Tolerance data | `OpenSim.Optics.Tolerancing.ToleranceOperandRow()` (`Code`, `Int1`, `Int2`, `Min`, `Max`) into `ToleranceTable(rows)`; the saved table: `ToleranceDataSettings.FromSettings(optics.Tolerances, notes)` |
| Tolerancer (OP-64) | `ToleranceAnalysis.QuickSensitivity(optics, table, catalogs)` or `.Run(optics, table, catalogs, options)` (`ToleranceRunOptions`: `Mode`, `Criterion`, …) — `.Nominal`, `.Unit`, `.Rows` (each `.Row`, `.Code`, `.AtMin.Change`, `.AtMax.Change`), `.ReportLines()` |
| Global search (OP-58) | `g = OpenSim.Optics.Optimization.Global.GlobalSearch.Run(optics, catalogs, o)` (`o` a `GlobalSearchOptions`, or `[]`: `PopulationSize`, `Generations`, `Seed`, `MaxEvaluationsPerTrial`, `CurvatureLimit`, `TimeLimit`, `MaxDegreeOfParallelism`, `Local`) — `g.StartMerit`, `g.Best` (best first; each `.Merit`, `.ConstraintsMet`, `.Values`, `.Design`, `.Trial`), `g.Stop`, `g.Trials`, `g.FailedTrials`, `g.Log`; `g.ApplyTo(optics, g.Best(1))` writes a design into the record |
| Hammer search (OP-58) | `HammerSearch.Run(optics, catalogs, o)` (`o` a `HammerSearchOptions`: `Rounds`, `TrialsPerRound`, `StepFraction`, `GlassChangeProbability`, `Seed`, …) — the same result type; `.Best` designs also carry `.Glasses` for the Substitute surfaces |
| Monte Carlo (OP-65) | `mc = S.MonteCarlo(optics, catalogs)` runs the saved tolerance rows with the saved run and Monte Carlo settings (or pass a `OpenSim.Optics.Tolerancing.MonteCarloOptions`: `Trials`, `Seed`, `Statistics`, `StandardDeviations`, `YieldLimit`, `Percentiles`); or `MonteCarloAnalysis.Run(optics, table, catalogs, runOptions, mcOptions)` — `mc.Nominal`, `mc.Unit`, `mc.Trials` (each `.Index`, `.Criterion`, `.Change`, `.Failure`, `.Draws`), `mc.Statistics` (`.Mean`, `.StandardDeviation`, `.Percentiles`), `mc.Yield.Fraction`, `mc.Evaluated`, `mc.Failed`, `mc.ReportLines()`, `mc.HistogramPlot([], [], [])`; `MonteCarloAnalysis.TrialLens(optics, table, catalogs, runOptions, mcOptions, k)` is trial k's lens (pass the seed `mc.Seed` names) |
| Test plates (OP-17) | `list = OpenSim.Optics.Optimization.TestPlates.TestPlateList.Read(path)` (or `.Parse(text, [])`; `list.Plates`, `list.Write(path)`); `r = TestPlateFitting.Run(optics, list, catalogs, o)` (`o` a `TestPlateFitOptions`, or `[]`: `Method`, `Optimization`, `TestWavelengthMicrometers`) — `r.Fitted`, `r.Merit`, `r.Steps` (each `.Surface`, `.Plate`, `.RadiusBefore`, `.RadiusAfter`, `.Merit`, `.NoMatch`), `r.ReportLines()` |

`S.MonteCarlo` exists because the saved Monte Carlo settings are read by a method with two out
parameters (`MonteCarloSettings.FromSettings`), which the interop was not tried with.
The global searches and Monte Carlo were not run under JGraph; the reflection test pins their
signatures. docs/Optics-Conventions.md §47 gives the facade's rules.

### What the interop needs (found 2026-11-14)

- **Records.** The optics records are C# records with init-only setters; JGraph sets them with a
  dot like any property (`front.Radius = 50`). A nullable number (`Radius`) reads back as a
  `System.Nullable<Double>`: use `.Value`.
- **Arrays and lists.** A property declared as a list may hold a .NET array at run time. Arrays
  index from 1 with parentheses and count with `.Length` (`result.Plots(1)`, `eval.Rows.Length`);
  lists index from 0 with `.Item` and count with `.Count` (`plot.Curves.Item(0)`). `double(x)`
  turns a `System.Double[]` (a curve's `X` and `Y`) into a numeric vector.
- **`params` arrays are not filled in.** `MeritOperandRow.Of` ends in `params double[] data`; pass
  the six values (`[0 0 0 0 0 0]`) — called without them, the interop finds no matching method.
  Optional parameters may be left out, as elsewhere.
- **Lists of OpenSim types** come from `NET.createGeneric('System.Collections.Generic.List',
  {'OpenSim.Core.Model.OpticsSurfaceSettings'}, n)`, as for the `List<int>` above.

### Python (pythonnet)

Python reaches the same assemblies through [pythonnet](https://pythonnet.github.io/) 3 on .NET 8;
nothing in OpenSim is specific to it. This sketch has **not been run** (pythonnet is not installed
where OP-98 was written):

```python
from pythonnet import load
load("coreclr")                                    # .NET 8, not .NET Framework
import clr, sys
sys.path.append(r"D:\...\OpenSimStudio\OpenSim.Cli\bin\Debug\net8.0")
clr.AddReference("OpenSim.Core")
clr.AddReference("OpenSim.Optics")
from OpenSim.Optics.Scripting import OpticsScripting as S
from OpenSim.Optics.Optimization import LocalOptimizer

optics = S.Load(r"C:\work\singlet.ossproj")        # a project saved with variables and a merit function
catalogs = S.Catalogs(optics)
opt = LocalOptimizer.Run(optics, catalogs)
print(opt.StartMerit, "->", opt.Merit)
mtf = S.Analyze(opt.Optimized, catalogs, "FftMtf", None)
for curve in mtf.Plots[0].Curves:
    print(curve.Name, list(curve.X)[:3], list(curve.Y)[:3])
```

## Running headless

`jgraph -batch "script.m"` runs a script with no window and exits; `-logfile run.log` tees the
output. OpenSim's command line stays independent: nothing here changes `opensim solve` or `sweep`.
