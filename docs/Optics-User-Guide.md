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

- **What ships.** Three built-in catalogs from the refractiveindex.info database, which is in
  the public domain (CC0 1.0; its licence ships with the program in
  `OpenSim.Optics/Materials/RefractiveIndexInfo`): **RII-MAIN** (44 crystals, infrared
  materials and coating materials, among them fused silica, CaF2, BaF2, MgF2, sapphire, Si, Ge,
  ZnSe, ZnS, KBr, NaCl, LiF, crystal quartz and calcite), **RII-GLASS** (4 non-vendor glasses:
  soda-lime, BGG, ZBLAN, Pilkington Optiwhite) and **RII-ORGANIC** (PMMA, polystyrene,
  polycarbonate, PET). They are listed as *built in*. Each material is named after the
  database's book and page (`CaF2-Li`, `Al2O3-Malitson-o` for sapphire's ordinary ray) and
  its form shows where its data come from. No vendor glass ships: the database's copies of
  vendor datasheets (its "specs" shelf) are left out, as vendor catalogs are; see
  docs/Optics-Conventions.md §2 for the full list and how the pages were chosen.
- **Vendor catalogs.** OpticStudio AGF files in your catalog folder,
  `%AppData%/OpenSimStudio/GlassCatalogs` unless you choose another with **Folder…** (the choice
  is kept for next time). **Import AGF…** copies a file into that folder and loads it; a copy of
  the same name is replaced. A catalog is named after its file; a file named like a built-in
  catalog replaces it, and the log says so. Vendor catalogs may not be redistributed, so you
  download them from the vendor and
  import them. AGF files in UTF-16 or ASCII/UTF-8, with either line end, are read. Importing a
  refractiveindex.info page or YAML file of your own is not built.
- **Active catalogs.** Tick the catalogs the lens draws its glasses from; a glass name is
  looked up in the ticked catalogs from the top. Until you change a tick every loaded catalog
  is active. The ticks save with the project and the lens file. A saved catalog this machine
  does not have stays in the list as *not loaded*, and the log says so.
- **The self-check.** Selecting a catalog computes every glass's nd and Vd from its
  coefficients and compares them with the values the catalog prints, to the digits printed. The
  line under the list counts the glasses that match, those that do not, and those not checked
  (placeholder entries such as infrared glasses printed with nd = 1, glasses whose valid
  range does not reach the visible, and the shipped materials, whose pages state no nd and
  Vd). Some vendors print a melt's measured nd beside fitted
  coefficients, so a few glasses can differ in the last printed digit or two without any error
  in the file or the program. Notes from reading the file (repeated glass names, skipped lines,
  a glass using formula 4, whose form is not yet confirmed) are listed under it.
- **A glass's data.** The form shows the dispersion formula and coefficients, valid range,
  printed and computed nd and Vd with the glass's own self-check, glass code, status, reference
  temperature, the dn/dT constants, thermal expansion, density, ΔPgF, relative cost and
  resistance classes, the internal transmittance table's extent and the vendor's comment. For a
  shipped material it shows the database's formula ("refractiveindex.info formula 1
  (Sellmeier (preferred))" — the database numbers its formulas 1–9, not as OpticStudio does) or
  "Tabulated n" / "Tabulated n, k", and under **Source** the database page and its references,
  and under **Conditions** what the page states (temperature in K). The shipped indices are
  used as their sources give them, without converting between vacuum and air wavelengths, and
  they carry no dn/dT data.
  **Index at wavelength** evaluates the glass at any wavelength inside its valid range, at the
  catalog's reference temperature; outside the range it says so rather than extrapolate.
  With **Adjust indices to the environment** on (system data, Environment), a second line
  gives the index at the system's temperature and pressure, relative to the system's air and
  absolute, and names the air model; the wavelength is then taken as measured in the system's
  air.
- The data are shown, not edited, and no surface uses a glass yet.

## Glass tools and material plots

Seven analyses in the **Materials** group of the analysis windows (OP-14,
`OpenSim.Optics/Analysis/MaterialAnalyses.cs`) work on the active glass catalogs, so any catalog
you import or tick appears in them; ticking, importing or reloading recomputes open windows.
Their settings are edited in the window's settings form and save with the project.

- **Glass map.** nd against Vd, or P_g,F against Vd (the partial dispersion diagram), one
  colour and marker per catalog; hovering a point names the glass. Every glass is placed at the
  nd, Vd and P_g,F computed from its coefficients, not at the printed values; glasses that
  cannot be placed (no valid range over F, d and C, placeholder entries, no g line for P_g,F)
  are listed with the reason. Vd increases to the right (printed maps usually reverse it).
- **Athermal glass map.** Thermal expansion α (the catalog's ED value for −30…+70 °C) against
  the thermo-optical constant G = α·(n_rel − 1) + dn_rel/dT of SCHOTT TIE-19 equation (10), or
  against dn_rel/dT alone. dn_rel/dT is the mean over a temperature interval (default +20/+40
  °C at the e line, as TIE-19's Table 2), from the TIE-19 dn/dT model and the glass's TD
  constants, with the TIE-19 air by default. Glasses without TD constants or an expansion
  coefficient are left out and listed.
- **Dispersion vs wavelength.** Index against wavelength for glasses you name ("N-BK7, F2";
  "catalog:glass" picks a catalog), each drawn only over its valid range.
- **Internal transmittance vs wavelength.** The catalog's IT table scaled to a chosen
  thickness (t^(d/τ) per point), drawn at the table's own wavelengths with nothing
  extrapolated; points without a sample thickness are left out and counted.
- **Glass compare.** Named glasses side by side: computed and printed nd, Vd, ΔP_g,F, ne, Ve,
  nF − nC, P_g,F, the index at the system's wavelengths (or a list you give), and the thermal,
  cost and resistance data as printed.
- **Model glass.** A glass from nd, Vd and ΔP_g,F (P_g,F measured from the normal line
  0.6438 − 0.001682·Vd). **This is OpenSim's own stand-in, not OpticStudio's model glass**,
  whose formula is not published: the relative partial dispersion is fitted as a linear
  function of P_g,F over the active catalogs' glasses, with constraints that make the requested
  nd, Vd and P_g,F come out exactly, and the result is an ordinary Herzberger (formula 3) glass
  valid from 0.4047 to 1.014 µm. Away from the d line its indices will differ from OpticStudio's,
  and a different set of active catalogs gives a different model; the window reports how well
  the model reproduces the training glasses (trained on the SCHOTT catalog: RMS 8e-5 in index,
  worst 1.3e-3).
- **Glass fitting.** Fits any dispersion formula except Sellmeier 2 to a text file of
  "wavelength(µm) index" pairs, and lists the coefficients with standard errors, the residuals
  (plotted) and the condition number, and the NM, CD and LD lines of an AGF entry to paste into
  a catalog. Sellmeier-type formulas start from a grid search over resonance positions and are
  refined by damped least squares.

Glass index and Abbe offsets exist in the library (`GlassOffsets`): they move nd and Vd by
given amounts and keep every relative partial dispersion; this is this program's rule, not
OpticStudio's, whose formula has not been read. No window applies them yet, because no surface
uses a glass.

## Environment and the index of air

The **Environment** group of the system data (`OpenSim.App/Views/Panels/OpticsSystemDataPanel.xaml`)
holds the system's temperature (°C) and pressure (atm; 0 is a vacuum, 1 atm is 101325 Pa), the
**air index model**, and **Adjust indices to the environment**. All of it saves with the
project and the lens file.

- **Air index model.** *Ciddor (NIST)* is the default: the Ciddor equation as NIST's
  Engineering Metrology Toolbox computes it, with **CO2** (default 450 µmol/mol) and
  **relative humidity** (default 0 %, dry air). *SCHOTT TIE-19 (OpticStudio-compatible)* is
  the formula OpticStudio-compatible tools use; it has no CO2 or humidity input. *Modified
  Edlén (NIST)* is the other NIST equation. The line under the group names the model in use
  and its inputs, and so does the System summary analysis.
- **Ranges.** Ciddor and Edlén are valid for vacuum wavelengths 0.3–1.7 µm, −40 to +100 °C
  and 10–140 kPa; outside that the system check reports a problem and suggests the TIE-19
  model, which states no range. An infrared system therefore needs the TIE-19 model, or a
  vacuum.
- **Wavelengths** in the table are in the system's air, as OpticStudio's are; the vacuum
  wavelength a ray carries is found with the air model, whatever the setting below.
- **Adjust indices to the environment.** Off (the default, as in OpticStudio): every glass
  has its catalog index at the catalog's reference temperature. On: SCHOTT TIE-19's dn/dT
  model moves each glass to the system temperature, and its index is referred to the system's
  air. At the catalog's reference temperature and 1 atm this gives the catalog index exactly;
  in a vacuum it gives the absolute index. A glass without dn/dT data (no TD line) changes
  only through the air. The equations and every difference from other tools are in
  `docs/Optics-Conventions.md` §2 and §10.

## Paraxial data

The paraxial engine (`OpenSim.Optics/Paraxial`) computes first-order data: effective, back and
front focal lengths, cardinal points (focal, principal, nodal), entrance and exit pupil
positions and diameters, lateral and angular magnification, image-space and working F-numbers,
object and image NA, the Lagrange invariant, the ABCD matrix and the paraxial marginal and
chief ray at every surface, per wavelength, for finite and infinite conjugates, telecentric
object space and afocal systems. Signs and reference points are in
`docs/Optics-Conventions.md` §11.

The analysis **System data and paraxial rays** (Reports) lists them. There is no sequential
surface editor yet, so in the app this analysis says the system has no surfaces; the engine is
used from code and tests until the surface editor (OP-20) feeds it. Not built: real image
height fields (need ray aiming, OP-22), configurations (OP-30), and any comparison of these
numbers with OpticStudio's output.

## Coatings

Below the glass catalogs, the **Coatings** group (`OpenSim.App/Views/Panels/OpticsCoatingsPanel.xaml`)
holds the lens's thin-film coating catalog. It saves with the project and the lens file.

- **Materials.** A coating material is a table of wavelength (µm), n and k. One row is a
  constant index; two or more interpolate linearly, and a wavelength outside the table is
  refused rather than extrapolated (OpticStudio holds the end values instead). k is zero or
  positive for an absorbing material (index n + ik). OpticStudio's coating file writes k
  negative; reading and writing such a file change the sign.
- **Coatings.** *Add layers* makes a layer coating: one line per layer, from the incident
  side, each with its material, thickness, **µm** (absolute thickness; otherwise the
  thickness is in waves of the primary wavelength in the layer, d = λ0·T/n0), **Loop**
  (repeat a group: write the count on the group's first and last lines) and **Taper** (a taper
  profile by name). The substrate is not listed. *Add ideal* makes an ideal coating given by
  T, R and the reflection under total internal reflection.
- **Read catalog… / Write catalog…** read and write OpticStudio coating files (`.dat`: MATE,
  TAPR, COAT, IDEAL, IDEAL2, TABLE, the `COAT I.xx` shorthand) and this program's own
  catalog (`.oscoat.json`). Reading replaces the catalog. Encrypted coatings cannot be read:
  they are left out and named. IDEAL2 and table coatings and taper profiles are kept and
  shown, but are edited in a file, not in the panel.
- **Problems** in the catalog (an undefined material or taper, a repeated name, a loop
  without its closing line) are listed in red under the group.

The **Coatings** group of the analysis picker has twelve plots: Reflection, Transmission,
Absorption, Diattenuation, Phase and Retardance, each against angle and against wavelength.
Their settings name the coating (blank: an uncoated surface), the incident medium and the
substrate (blank is air; a number is an index; otherwise a coating material or a glass of the
active catalogs by name), whether the surface is a mirror (its coating's last layer is then
the substrate; uncoated, aluminium 0.7 − 7.0i), the angle or wavelength range, the coating
multiplier and index and extinction offsets, and the point and surface radius at which tapers
are evaluated. Against angle, wavelength 0 means the primary wavelength; against wavelength,
0 means the system's shortest or longest wavelength. From glass to air (an incident medium
named and the substrate blank) the layers are applied in reverse order, as OpticStudio does.

What the plots show: s, p and their average for R, T and A; diattenuation |s − p|/(s + p)
for reflection and transmission; the phases of the s and p reflected and transmitted
amplitudes in OpticStudio's convention; retardance φp − φs (transmission) and
φp − φs + 180° (reflection). The equations and how they compare with other tools are in
`docs/Optics-Conventions.md` §12. Not built: coatings on surfaces (there are no surfaces
yet), coatings applied to traced rays, per-layer multipliers and offsets in the plots' settings
(the library takes them), incoherent thick layers, and coating design or optimisation.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- the sequential surface editor and the non-sequential object editor;
- system data (aperture, fields, wavelengths);
- ray tracing, layouts and the analysis windows (spot diagrams, ray fans, OPD, MTF, PSF and
  the rest);
- optimization and tolerancing of a lens;
- lens-file import.
