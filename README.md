# OpenSim Studio

**An offline-first, multi-physics engineering simulation platform for Windows.**

OpenSim Studio is a lightweight but technically rigorous alternative to entry-level CAE
tools like Ansys. It takes you from geometry (STL, STEP, PCB fabrication data) through
tetrahedral meshing, finite-element solving, and interactive 3D post-processing — all in
a single WPF desktop application that never touches the network.

Built on .NET 8 with essentially all numerics written first-party: the sparse linear
algebra, the Delaunay mesher, the FE assemblers, the eigensolver, the complex solvers,
the STEP importer, and the method-of-moments RF kernel are all in this repository and
fully testable.

---

## What it can simulate

| Domain | Analyses |
|---|---|
| **Structural** | Linear static (TET4 + quadratic TET10), modal analysis (subspace eigensolver, consistent mass) |
| **Thermal** | Steady-state and transient heat conduction (backward Euler) |
| **Electrical** | DC conduction (voltage, current density, resistance), AC electro-quasistatic frequency sweeps (complex-symmetric COCG solver) |
| **Coupled** | One-way Joule heating (I²R → steady or transient thermal) |
| **PCB** | Gerber RS-274X + Excellon + IPC-2581 import, per-net copper meshing, pad-to-pad trace resistance, full 3D PEEC inductance (self, mutual, coupling k, plane-return loops), lumped R + jωL trace estimates |
| **RF** | First-party method-of-moments antenna solvers: thin-wire EFIE (dipoles, loops, monopoles, board trace chains) and RWG surface MoM for PEC sheets (plates, patches, PCB copper islands), both with optional infinite PEC ground by image theory; **microstrip substrates** via a rigorous layered-media Green's function (direct Sommerfeld integration, surface-wave poles extracted into the power ledger); **coaxial probe feeds** through the slab with a classical 1/ρ attachment mode; input impedance, far-field patterns, directivity, surface-wave power, and near-field E maps in free space, over ground, and inside/above the substrate |

## What it can import

- **STL** — with vertex welding, face detection, and accelerated point-in-solid classification
- **STEP (AP203/AP214)** — a first-party Part 21 parser, NURBS evaluation, and
  watertight-by-construction tessellation; no OpenCascade, no binary blobs
- **Gerber RS-274X + Excellon** — including aperture macros, polygon apertures,
  step-repeat, and G85 routed slots
- **IPC-2581** — both the Cadence/Altium and KiCad exporter dialects
- **Built-in primitives** — parametric boxes, cylinders, etc., for quick studies

## Screenshot workflow

Geometry → tet mesh → materials (20 built-in + user library) → boundary conditions →
solve → color-mapped results in a Helix 3D viewport, with contour lines, section planes,
multi-frame scrubbing (time steps / mode shapes / frequency points), and `.ossproj`
project save/load. The app opens on a KiCad-style home screen with two workspaces:
**Mechanical** (static, modal, thermal) and **Electrical** (DC, AC, Joule, PCB, antenna).

## Getting started

Requirements: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build OpenSimStudio.sln          # build everything
dotnet test  OpenSim.Tests              # run the full test + regression-benchmark suite
dotnet run   --project OpenSim.App      # launch the app
```

Example inputs for trying the PCB and STEP pipelines ship in the repository root
(`Example_Gerbers.zip`, `Example_IPC-2581.cvg`, `Example_Model.step`, `Breakout_Board.xml`).

## Design principles

The priority order is explicit and non-negotiable:

1. **Simulation accuracy** 2. Numerical stability 3. Clean architecture
4. Extensibility 5. Performance 6. UX 7. Visual polish.

A slower correct answer always beats a faster wrong one. Concretely:

- **Correctness is gated by analytical regression benchmarks**, not just unit tests:
  cantilever deflection vs. Timoshenko theory, Fourier slab transients, half-wave dipole
  impedance bands, the Balanis microstrip-patch resonance and cavity edge resistance,
  and antenna **energy-conservation gates** (radiated + surface-wave power must equal
  ½·Re(V·I*)). Failing solvers fail loudly — the platform never returns a
  plausible-looking garbage number.
- **First-party numerics stay transparent.** The CSR sparse matrix + Jacobi-preconditioned
  conjugate gradient, the COCG complex solver, the Bowyer–Watson tet mesher (symbolic
  infinite vertex, CGAL-style), and the subspace eigensolver are core IP, documented,
  and independently tested. The single external runtime dependency for simulation is
  **Clipper2** (MIT), wrapped behind an interface for 2D polygon booleans only.
- **Educational transparency.** Assumptions are printed with results (e.g. PEEC reports
  DC-current assumptions; monopole results state the image-plane halving) rather than
  hidden inside a black box.
- **Fully offline.** No telemetry, no cloud solves, no license server.

## Architecture

Eight projects with strictly downward dependencies. Everything below the app layer
targets plain `net8.0` (no WPF), which mechanically enforces the UI/simulation split:

```
OpenSim.App       WPF shell, per-concern MVVM viewmodels, Helix 3D rendering, DI root
OpenSim.Rf        thin-wire + RWG surface MoM, layered-media (microstrip) kernels,
                  probe feeds, far/near fields, trace-chain adapter
OpenSim.Pcb       Gerber/Excellon/IPC-2581 import, 2.5D PCB meshing, PEEC inductance
OpenSim.Solvers   TET4/TET10 assembly; static, modal, thermal, DC/AC electrical, Joule
OpenSim.Meshing   Bowyer–Watson Delaunay tet mesher, quality metrics, refinement
OpenSim.Geometry  STL + first-party STEP import, primitives, face detection
OpenSim.Core      math/numerics, domain model, result fields, interfaces, persistence
OpenSim.Tests     xUnit: unit tests + analytical regression benchmarks (650+ green)
```

Every replaceable piece is an interface in `OpenSim.Core/Interfaces`
(`IGeometryImporter`, `IMeshGenerator`, `ISolver`) registered through DI — new solvers
and importers plug into the shared `FeMesh`/`IResultField` contracts without touching
the rest of the platform.

## Project status

| Phase | Scope | Status |
|---|---|---|
| Milestone 1 | End-to-end vertical slice: geometry → mesh → static solve → results | ✅ Complete |
| Phase 1 | Mesh quality refinement, TET10 elements, docking UI, contours/sections | ✅ Complete |
| Phase 2 | PCB import, DC electrical, thermal, Joule coupling, materials, STEP import | ✅ Largely complete (SVG import deferred) |
| Phase 3 | Multi-frame results, transient thermal, modal, AC sweeps, 3D PEEC | ✅ Solver track complete |
| Phase 4 | RF antenna simulator | 🚧 In progress — thin-wire MoM, PEC ground planes, RWG surface MoM, layered-media microstrip (rigorous Sommerfeld Green's function), substrate near-field maps, deterministic parallel solves (11.8× on 16 cores, bitwise-identical at any thread count), and coax probe feeds all shipped; probe UI wiring, multi-layer stackups, optimization, plugin SDK, and reporting open |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The short version: correctness first, keep the
layering intact, and never weaken a regression tolerance to make a test pass.
