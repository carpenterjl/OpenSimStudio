# Contributing to OpenSim Studio

Thanks for your interest! OpenSim Studio is a numerics-first project: contributions are
judged first on correctness, then on architectural fit. This document explains both.

## Getting set up

Requirements: Windows, .NET 8 SDK. No native dependencies, no submodules.

```
dotnet build OpenSimStudio.sln          # build everything
dotnet test  OpenSim.Tests              # must be green before and after your change
dotnet run   --project OpenSim.App      # launch the WPF app
```

## The one rule that overrides everything

**Never weaken a regression tolerance to make a test pass without understanding why the
number moved.** The benchmarks in `OpenSim.Tests/Solvers` compare solver output against
closed-form analytical solutions (Timoshenko beams, Fourier slabs, Grover inductance
formulas, dipole radiation resistance, …). They are the project's correctness gate. If
your change shifts a benchmarked value, the PR must explain the physics or numerics of
why — "the test was too strict" is not an explanation.

The gates are not all in `Solvers`. These, elsewhere in `OpenSim.Tests`, each pin a
defect that once shipped a wrong number, and each fails on the code before its fix:

| Gate | Where | What it holds |
|---|---|---|
| Voxel Poiseuille identity | `Cfd/VoxelWallTests` | Flow between voxel slabs equals the domain-wall closed form to 1e-8 on every axis (the no-slip wall is on the cell boundary) |
| Square-duct f·Re | `Cfd/VoxelWallTests` | Converges to Shah & London's 56.908 at second order, from below |
| Inflow property state | `Cfd/InflowStateTests` | Fluid properties are taken at the inflow-weighted stream temperature, not the surroundings' ambient |
| Hoer–Love oracles | `Pcb/RectangularBarKernelTests` | Bar–bar mutual against the cross-section average of the exact filament kernel (1e-8), self against the slender series and the subdivision identity, both kernel regimes agreeing on their boundary |
| Subdivision invariance | `Pcb/LoopComposer3DTests` | A straight bar composes to one inductance however it is segmented (1e-9) |
| Plane-return loops | `Pcb/PlaneReturnTests` | Trace over plane against an independent sub-filament quadrature (1e-4); wide traces positive and inside the microstrip band |
| Thick-stack pole equality | `Rf/MultiLayerPoleTests`, `Rf/SurfaceWaveDispersionTests` | The multi-layer pole finder equals the pinned single-slab finder branch for branch; mode counts equal the closed-form cutoff counts |
| IBIS axis convention | `Si/IbisConventionTests`, `Si/IbisSpecSyntaxTests` | Closed-form load lines on the spec's Vcc-relative axis, finite-difference conductance on all four tables, round-trip extraction at typ/min/max, spec syntax and `[Submodel]` isolation |
| Mesh-audit fixtures | `Meshing/MeshAudit*Tests` | Each known way a mesh can differ from its geometry is caught by a named check, beside a control that passes; thresholds are twice what the fixtures measure |
| Steel conductivity | `Core/MaterialStrengthTests` | Structural steel σ in the carbon-steel handbook band, above 304 stainless |

The priority order for every decision:
**1. accuracy, 2. numerical stability, 3. clean architecture, 4. extensibility,
5. performance, 6. UX, 7. visual polish.** A slower correct answer beats a faster
wrong one.

## Architecture rules (hard constraints)

Layering is strictly downward:

```
App (WPF/Helix) → ViewModels → Core interfaces → Geometry / Meshing / Solvers → Core numerics
```

- **No simulation code may reference WPF, Helix, or any UI type.** Everything below
  `OpenSim.App` targets plain `net8.0`; keep it that way — it mechanically enforces the
  rule.
- **Rendering never knows about physics and vice versa.** The only place simulation data
  meets the rendering stack is `OpenSim.App/Rendering/SceneBuilder`.
- **All solvers share the same contracts.** New solvers consume the same `FeMesh` and
  produce the same `IResultField` types — do not invent a parallel mesh or result model.
- **Extend through the interface seams.** New importers/meshers/solvers implement
  `IGeometryImporter` / `IMeshGenerator` / `ISolver` from `OpenSim.Core/Interfaces` and
  register through DI in `App.xaml.cs`.
- **No new dependencies without discussion.** The dependency bar is high and offline-first
  is a requirement: pure managed code, permissive license, source available. The only
  simulation-side NuGet package today is Clipper2, wrapped behind `IPolygonOps` so its
  types never leak. Do not swap first-party numerics (CSR/CG, the mesher, the
  eigensolver, the MoM kernel) for black-box libraries.

## Coding standards

- **MVVM only.** Code-behind is limited to purely visual behavior (hit testing, scroll,
  zoom-to-fit). ViewModels use CommunityToolkit.Mvvm source generators. App-layer
  viewmodels are per-concern; shared state flows through `Services/ProjectSession`
  events — viewmodels do not call each other.
- One primary type per file. Prefer records for immutable model data.
- Public APIs get XML doc comments. Comment the *why* (a constraint, a non-obvious
  numerical choice), not the *what*.
- **Validate before acting**: geometry before meshing; mesh + material + boundary
  conditions before solving. Errors must be actionable and surfaced to the log panel —
  no silent failures. An unsupported input fails loudly naming what and where (see the
  STEP and Gerber importers for the house style).

## Writing numerical tests

- Benchmark against **analytical solutions**, and make assertions sharp against the
  *discretization model*, not loose against the ideal. Example: a tessellated cylinder
  is compared to the inscribed-prism volume ½nr²h·sin(2π/n) at 1e-12, not to πr²h at 5%.
  The mesher's deterministic surface jitter means absolute "patch test" tolerances
  bottom out around 2% — sharp assertions divide the geometry factor out instead
  (e.g. AC electrical asserts Z·(σ+jωε) against R_dc·σ at 1e-9).
- When a method has a known bias, assert it as a **one-sided band with the physical
  trend**, don't widen a symmetric tolerance. (TET4 bending is stiff — frequencies come
  out high; thin-wire MoM sits below the ideal-dipole radiation resistance by O(1/Ω).)
- Where an independent formulation exists, keep an **oracle test** (e.g. the exact
  filament-inductance kernel is checked against a Gauss–Legendre Neumann integral; the
  optimized polarity composition is pinned vertex-exact against the naive fold).
- Determinism matters: parallel code paths must produce bitwise-identical output to
  their sequential form (see `ImportDeterminismTests` for the pattern).
- **Mesh a fixture at two elements or more through its thinnest dimension.** The Delaunay
  mesher refuses anything thinner (`MeshAudit`, check A0) and audits what it does produce
  against the geometry, so a benchmark on a 10 mm bar uses `TargetEdgeLength ≤ 0.005`. To
  test the audit itself, do not try to coax a wrong mesh out of a mesher: build the mesh
  of a deliberately different solid with `GridSolid` / `SurfaceBuilder`
  (`OpenSim.Tests/Meshing/MeshAuditFixtures.cs`) and audit it against the geometry, so the
  assertion can name the check that must catch it. Every such fixture has a control
  beside it — the same solid meshed correctly — that must pass everything.

## Things that look wrong but are deliberate

Before "fixing" one of these, read the rationale in `CLAUDE.md` — each was hard-won:

- The tet mesher uses a **symbolic infinite vertex**, not a finite super-tetrahedron,
  and applies **deterministic jitter** to every inserted point (including Steiner
  points). Both prevent floating-point predicate failures.
- The Delaunay mesher **audits its own output and throws** (`MeshAuditException`) rather
  than return a mesh that is not the geometry, and it **rebuilds** — without the interior
  points a sliver cull exposed, or with feature edges seeded twice as densely — before it
  gives up. Neither rebuild runs on a mesh that passes first time, which is what keeps
  every existing mesh bitwise what it was. The audit's thresholds (`MeshAudit.DistanceFactor`
  and friends) are twice what named fixtures measure, and a test asserts the "twice": do
  not tune them to make a mesh pass.
- Transient thermal is **backward Euler on purpose** (unconditionally stable *and*
  monotone; Crank–Nicolson oscillates on step changes).
- Modal analysis uses **subspace iteration, not Lanczos** (the breakdown/ghost-mode risk
  profile is wrong for this project), and convergence requires the true residual, not
  just eigenvalue stagnation.
- AC electrical uses **COCG, not CG** — plain CG is mathematically wrong on
  complex-symmetric systems.
- DC/AC electrical **never runs on a mixed copper+FR4 mesh** (a ~1e21 conductivity
  spread destroys CG conditioning); the copper-only mesh path exists for this.
- TET10 mass uses the 14-point degree-5 Keast rule because its all-positive weights keep
  the mass matrix SPD; the compact degree-4 rule can go indefinite.
- Sub-0.02 radius-ratio sliver tets are **culled unconditionally** near surfaces and
  must never be re-admitted, even to fix a non-manifold edge.

## Pull request checklist

1. `dotnet build OpenSimStudio.sln` and `dotnet test OpenSim.Tests` are green.
2. New solver/importer/mesher functionality comes with tests, preferably including an
   analytical benchmark in the house style.
3. The layering rules above are intact (no UI references below `OpenSim.App`).
4. Errors your code can produce are actionable and reach the log panel.
5. If a benchmark value moved, the PR description explains why in physical/numerical
   terms.
6. Keep PRs focused — one feature or fix per PR, with a description of what was
   validated and how.

## Reporting bugs

Open an issue with: what you did (input files if shareable), what you expected, what
happened, and the log-panel output. For numerical issues, the mesh statistics and solver
summary from the log are usually the fastest path to a diagnosis.
