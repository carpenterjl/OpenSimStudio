# Optics workspace — user guide

The Optics workspace is new and is being built. Today it is a shell: you can open it, choose
between the sequential and non-sequential modes, and save that choice with the project. There
is no system editor yet, so there is nothing to trace and no result to show. This guide covers
only what exists; the last section lists what is coming.

The conventions every optics feature follows (units, signs, tilt and decenter order, OPD sign,
field and pupil normalisation) are in [Optics-Conventions.md](Optics-Conventions.md).

## Opening the workspace

- From the home screen, click the **Optics** card (the last of the seven workspace cards; its
  chips read *Sequential* and *Non-sequential*).
- From anywhere else, click **Optics** on the workspace rail at the left (tooltip
  *Optics (ray optics)*).

The ribbon badge then shows the lens icon and "Optics", and the analysis picker offers one
analysis, **Optical system (ray optics)** (`OpenSim.App/ViewModels/AnalysisOption.cs`).

## The study rail

Like the RF and signal-integrity workspaces, Optics has no Geometry or Mesh step: an optical
system is described by its own surfaces or objects, not by a part. The rail has three steps
(`OpenSim.App/ViewModels/StudyRailViewModel.cs`):

| Step | State today | Note shown |
|---|---|---|
| Setup | Available | Mode and system |
| Solve | Locked | Needs an optical system |
| Results | Locked | No solution yet |

The Solve button is disabled; its tooltip reads "There is no optical system to trace yet". The
Study card at the foot of the rail shows the project name as *System* and *Units: SI (m)*.

## Sequential or non-sequential

The workspace has one panel, **Optical system** (`OpenSim.App/Views/Panels/OpticsPanel.xaml`),
holding the **Mode** picker:

- **Sequential** (the default) — surfaces in a fixed order: every ray meets surface 1, then 2,
  and so on. The description used for lens design.
- **Non-sequential** — objects in space with no set order: a ray meets whatever lies in its
  path. The description used for illumination and stray light.

The line under the picker restates the selected mode's meaning. Changing the mode has no other
effect yet, because there is no system for it to apply to.

## What saves with the project

The project file (`.ossproj`, format version 4) stores:

- the **mode**, under a fixed saved name (`Sequential` or `NonSequential`,
  `OpenSim.Optics/OpticsMode.cs`), so a renamed enum member cannot change how an old file
  reads;
- the **analysis**, so a project saved with *Optical system (ray optics)* selected reopens in
  the Optics workspace.

Nothing else is saved, because nothing else exists; results, when there are any, will be
recomputed rather than stored (`OpenSim.Core/Model/OpticsSettings.cs`).

When a project is opened:

- a mode this version does not know (from a hand-edited file or a later version) is not
  guessed at: the current mode is kept and the log says so, naming the unknown mode;
- a file written before the Optics workspace existed carries no optics settings and leaves
  the mode as it was;
- **New project** resets the mode to Sequential.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- the sequential surface editor and the non-sequential object editor;
- system data (aperture, fields, wavelengths) and glass catalogs;
- ray tracing, layouts and the analysis windows (spot diagrams, ray fans, OPD, MTF, PSF and
  the rest);
- optimization and tolerancing of a lens;
- lens-file import.
