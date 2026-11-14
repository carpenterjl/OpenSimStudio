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

## Glass catalogs

Below the system data, the **Glass catalogs** group (`OpenSim.App/Views/Panels/OpticsMaterialsPanel.xaml`)
lists the glass catalogs the program has loaded and which of them the lens uses.

- **Where catalogs come from.** OpticStudio AGF files in your catalog folder,
  `%AppData%/OpenSimStudio/GlassCatalogs` unless you choose another with **Folder…** (the choice
  is kept for next time). **Import AGF…** copies a file into that folder and loads it; a copy of
  the same name is replaced. A catalog is named after its file. No catalog ships with the
  program yet: vendor catalogs may not be redistributed, so you download them from the vendor
  and import them. AGF files in UTF-16 or ASCII/UTF-8, with either line end, are read.
- **Active catalogs.** Tick the catalogs the lens draws its glasses from; a glass name is
  looked up in the ticked catalogs from the top. Until you change a tick every loaded catalog
  is active. The ticks save with the project and the lens file. A saved catalog this machine
  does not have stays in the list as *not loaded*, and the log says so.
- **The self-check.** Selecting a catalog computes every glass's nd and Vd from its
  coefficients and compares them with the values the catalog prints, to the digits printed. The
  line under the list counts the glasses that match, those that do not, and those not checked
  (placeholder entries such as infrared glasses printed with nd = 1, and glasses whose valid
  range does not reach the visible). Some vendors print a melt's measured nd beside fitted
  coefficients, so a few glasses can differ in the last printed digit or two without any error
  in the file or the program. Notes from reading the file (repeated glass names, skipped lines,
  a glass using formula 4, whose form is not yet confirmed) are listed under it.
- **A glass's data.** The form shows the dispersion formula and coefficients, valid range,
  printed and computed nd and Vd with the glass's own self-check, glass code, status, reference
  temperature, the dn/dT constants, thermal expansion, density, ΔPgF, relative cost and
  resistance classes, the internal transmittance table's extent and the vendor's comment.
  **Index at wavelength** evaluates the glass at any wavelength inside its valid range, at the
  catalog's reference temperature; outside the range it says so rather than extrapolate.
- The data are shown, not edited, and no surface uses a glass yet; the temperature and
  pressure correction of indices is a later item.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- the sequential surface editor and the non-sequential object editor;
- system data (aperture, fields, wavelengths);
- ray tracing, layouts and the analysis windows (spot diagrams, ray fans, OPD, MTF, PSF and
  the rest);
- optimization and tolerancing of a lens;
- lens-file import.
