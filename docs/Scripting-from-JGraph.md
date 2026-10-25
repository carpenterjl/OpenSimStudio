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

## Running headless

`jgraph -batch "script.m"` runs a script with no window and exits; `-logfile run.log` tees the
output. OpenSim's command line stays independent: nothing here changes `opensim solve` or `sweep`.
