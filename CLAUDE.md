# CLAUDE.md

Guide for AI agents working in this repository. It distils [CONTRIBUTING.md](CONTRIBUTING.md)
and [README.md](README.md); where they say more, they win.

## Build and test

```
dotnet build OpenSimStudio.sln                         # everything
dotnet test  OpenSim.Tests --filter "FullyQualifiedName~OpticsConventionsTests"   # one test class
dotnet test  OpenSim.App.Tests --filter "FullyQualifiedName~OpticsWorkspaceTests" # one view-model class
dotnet test  OpenSim.Tests -c Release --settings OpenSim.Tests/slow.runsettings  # the slow gates only
dotnet run   --project OpenSim.App                     # launch the app
```

- **Run only the tests your change affects.** The full suite takes more than two hours. Pick
  the test classes that cover the files you changed and run them with `--filter`. A filter is
  ANDed with `Category!=Slow` from `OpenSim.Tests/default.runsettings`, so slow tests stay out
  unless you switch to `slow.runsettings`.
- **Two test projects.** `OpenSim.Tests` (`net8.0`) holds the library tests and the analytical
  benchmarks. `OpenSim.App.Tests` (`net8.0-windows`) holds the headless view-model tests. They
  are separate on purpose: the WPF app recompiles whenever any library DLL changes, so keeping
  it out of `OpenSim.Tests` keeps focused solver-test runs fast
  (`OpenSim.App.Tests/README.md`). Run `OpenSim.App.Tests` when you touch `OpenSim.App`.
- Never weaken a regression tolerance to make a test pass without explaining, in physical or
  numerical terms, why the number moved.

## Accuracy and sources

- Priority order: accuracy, numerical stability, clean architecture, extensibility,
  performance, UX, visual polish. A slower correct answer beats a faster wrong one.
- **Never type a reference value from memory.** Every constant, table entry, test expectation
  and published result is copied from a source you have open, and the source is cited beside
  it (author, title, table or equation, page). If you cannot find the source, say so and leave
  the value out; do not approximate it.
- Benchmark against analytical solutions or independent oracles, with sharp tolerances against
  the discretisation model (CONTRIBUTING.md, "Writing numerical tests").
- Failing solvers fail loudly with an actionable message naming what and where; no plausible
  garbage, no silent fallbacks.

## Dependencies and numerics

- **No new NuGet packages.** The only simulation-side package is Clipper2, behind
  `IPolygonOps`. MathNet.Numerics is referenced by `OpenSim.Tests` only, as a test oracle.
- **Numerics are first-party** (`OpenSim.Core/Numerics`: sparse and dense solvers, FFT, SVD,
  Cholesky, quadrature, splines, root finding, the optimizer kernel). Extend them; do not swap
  them for a library.
- Parallel code paths must be bitwise identical to their sequential form.

## Architecture

- Nothing below `OpenSim.App` references WPF, Helix or any UI type; those projects target
  plain `net8.0`.
- Finite-element solvers share `FeMesh` and `IResultField`. The antenna, signal-integrity,
  PDN and optics studies keep their own result records; reports use `StudyReport`
  (CONTRIBUTING.md, "Architecture rules").
- MVVM only; viewmodels use CommunityToolkit.Mvvm and talk through `Services/ProjectSession`,
  not to each other.
- **New forms use `FormGrid`** (`OpenSim.App/Controls/FormGrid.cs`): label/value pairs, one
  per row.
- **Optics follows [docs/Optics-Conventions.md](docs/Optics-Conventions.md)** for units, signs,
  tilt and decenter order, OPD sign and field/pupil normalisation. Change the document and its
  tests together, never the code alone.
- The list "Things that look wrong but are deliberate" in CONTRIBUTING.md (infinite-vertex
  mesher, mesh audit, backward Euler, subspace iteration, COCG, the Keast rule, sliver culling)
  gives the reasons; do not "fix" any of them.

## Git

- **Commit directly on `main`. Never create branches.**
- Commit subjects name the area: `Core: …`, `Optics: …`, `Docs: …`. The body is prose that says
  what changed, why, and how it was validated.

## Documentation

Plain and factual; describe what the code does today, with file references where useful. No
aspirational claims: something not built is listed as not built.
