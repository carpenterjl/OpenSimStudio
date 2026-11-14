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

The analysis **System data and paraxial rays** (Reports) lists them for the surfaces of the
Lens Data Editor (see below). A real image height field is refused here (the real ray
launcher solves it; see Ray aiming and vignetting). Not built: configurations (OP-30), and
any comparison of these numbers with OpticStudio's output.

## Real ray trace

The real ray trace (`OpenSim.Optics/Sequential`) traces rays exactly through a sequential lens
of rotationally symmetric conic surfaces: spheres, paraboloids, hyperboloids and prolate and
oblate ellipsoids, refracting or mirrors. It computes where each ray meets each surface, its
direction after refraction or reflection, and its optical path. A ray that misses a surface,
is totally internally reflected, or is stopped by an aperture is kept and reported with the
reason and the surface number.

- **Apertures:** circular (with an inner radius for an annulus), rectangular, elliptical,
  spider, user-defined polygon and floating (the surface's clear semi-diameter), each
  decentered or as an obscuration if wanted. A surface without an aperture does not stop rays.
- **Semi-diameters:** a surface keeps a semi-diameter you give it; otherwise it is sized from
  the chief and rim rays of every field and wavelength, plus the system's semi-diameter margins.
  The mechanical semi-diameter includes the chip zone.
- **Ignore this surface** traces the surface as if it were not there; **skip rays to this
  surface** only affects drawing.
- **Rays** are launched from normalised field and pupil coordinates; with ray aiming off the
  pupil coordinates are on the paraxial entrance pupil (ray aiming is the next section).
- A batch trace over fields, wavelengths and pupil points gives the same numbers, bit for bit,
  however many processor cores it runs on.

The analysis **Single ray trace** (Rays) lists one ray surface by surface: coordinates (local or
global), direction cosines, angles of incidence and exit, path lengths and optical path, and the
status, for the surfaces of the Lens Data Editor. The equations and the
points not yet compared with OpticStudio are in `docs/Optics-Conventions.md` §13 and §10 item 9.

Not built: surface types beyond the aspheres and the idealised and special surfaces (OP-24 on, and those listed as not built under "Aspheres" and "Idealised and special surfaces"), hyperhemispheric surfaces, curved object surfaces, OPD
against the reference sphere (OP-35), and layouts drawing traced rays.

## Gaussian beams

The analysis **Paraxial Gaussian beam** (Beams) carries a Gaussian beam through the paraxial
model (`OpenSim.Optics/Paraxial/GaussianBeam.cs`, `OpenSim.Optics/Analysis/GaussianBeamAnalysis.cs`).
Its settings: the wavelength row (0 for the primary); in x, the waist radius (1/e² intensity),
the waist's position as a signed distance along z from surface 1 (negative in front of it) and
M²; **Same in y** (on: a round beam; off: the y waist, position and M² given separately); and
the points per gap of the plot. Lengths in the settings are millimeters whatever the lens unit.

It lists the input beam (Rayleigh range and divergence in object space) and a table, **Paraxial
Gaussian Beam**, with one row per surface (two tables, x and y, for an astigmatic beam): beam
radius, waist radius, waist position from the surface, Rayleigh range, divergence, wavefront
radius and the Gouy phase accumulated from surface 1. The plot shows the beam radius against
the distance along the axis from surface 1, unfolded at mirrors; with M² above 1 the embedded
Gaussian is drawn dashed. Signs and the M² model are in `docs/Optics-Conventions.md` §14.

It reads the surfaces of the Lens Data Editor. Not built: the skew
(generally astigmatic) Gaussian beam about a real chief ray, which needs the real ray trace;
tilted, decentered and anamorphic surfaces; Gaussian beam operands; any comparison with
OpticStudio's output.

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
`docs/Optics-Conventions.md` §12. Coatings on surfaces and their effect on traced rays are
the polarization ray trace's (see Polarization). Not built: per-layer multipliers and offsets in the plots' settings
(the library takes them), incoherent thick layers, and coating design or optimisation.

## Coordinate breaks and tilts

The real ray trace places surfaces with coordinate breaks and surface tilts and decenters
(`OpenSim.Optics/Sequential/SurfacePlacement.cs`, `CoordinateBreak.cs`):

- **Coordinate break:** a dummy surface with x and y decenters, tilts about x, y and z in
  degrees, and the order flag (decenter then tilt, or tilt then decenter). Its thickness is
  applied last, along the new z. A break can return to a prior surface's coordinate system:
  orientation only, orientation and x, y, or orientation and x, y, z (the thickness is then
  solved as well).
- **Surface tilt and decenter:** any other surface can be tilted and decentered before itself,
  and after itself with explicit values, its own "before" values picked up or reversed, or a
  prior surface's picked up or reversed. Not before surface 1 when the object is at infinity.
- **Global coordinates:** "global" means object space's axes with the origin at surface 1's
  vertex before its own tilt. The analysis **Global vertex** (Reports) lists every surface's
  vertex, tilts and rotation matrix relative to a reference surface you choose, and the values
  of every break with returns solved.
- **Tools** (library functions in `CoordinateBreakTools`, no menu yet): Tilt/Decenter Elements
  (tilt a group about a pivot and put everything after it back where it was), Add Fold Mirror
  and Delete Fold Mirror, Local To Global and Global To Local. None moves a surface.
- **Paraxial data** of a tilted lens: a lens whose tilts only fold the axis with plane mirrors
  is unfolded and gives its first-order data; any other tilted or decentered lens is refused by
  the paraxial analyses and the ray launcher, with the surface named. The real trace itself
  has no such limit.

Breaks, tilts and returns are edited and saved in the lens data editor (see Lens Data Editor).
The rules, the readings of OpticStudio's help and the points not
yet compared with OpticStudio are in `docs/Optics-Conventions.md` §15 and §10 item 11.

Not built: multi-configuration control, the tools in the editor, tilting the object surface, layouts drawing tilted surfaces, and
paraxial data, ray aiming and ray launching for non-axial systems (OP-22).

## Ray aiming and vignetting

The **Ray aiming** group of the system data (`OpenSim.App/Views/Panels/OpticsSystemDataPanel.xaml`)
sets how rays are launched (`OpenSim.Optics/Sequential/RayLauncher.cs`):

- **Off** launches rays through the paraxial entrance pupil, as before. **Paraxial** and
  **Real** aim every ray so it crosses the stop at the requested pupil point, the stop radius
  set by the paraxial or the real marginal ray (with *float by stop size*, the stop's
  semi-diameter). Aiming matters when the pupil is aberrated: wide-angle lenses, a stop behind
  strong optics, fast systems.
- **Pupil shift x, y, z** (lens units) and **pupil compress x, y** only give the aim a better
  first guess; they never change the pupil. **Scale pupil shift x and y by field** scales the x
  and y shifts with the field (the z shift always is).
- **Robust ray aiming (slow)** reaches every ray step by step from the axis and refuses a ray
  that reaches the stop by a virtual path; use it for wide-angle systems.
- A ray that cannot be aimed is reported as not converged at the stop, not traced.
- **Real image height** fields work: the chief ray is found that lands at that height on the
  image surface.

The **vignetting factors** of each field (VDX, VDY, VCX, VCY, TAN) move, shrink and turn the
pupil that field's rays are launched into, by OpticStudio's equations; with aiming on, the
remapped point is on the stop. Between defined fields they are interpolated along y.
**Set Vignetting**, **Clear Vignetting** and **Replace Vignetting With Apertures** exist in the
library (`VignettingTools`) but have no buttons yet.

The analysis **Vignetting plot** (Rays) shows the fraction of rays that reach the image against
field along +y, at the primary wavelength, from a (2n + 1) × (2n + 1) grid per field (ray
density n, at least 5) at the chosen number of fields. By default the fields' vignetting factors
are first replaced by surface apertures. Like the other ray analyses it reads the lens data
editor's surfaces. Details, the closed-form checks and what is this program's own reading
are in `docs/Optics-Conventions.md` §16 and §10 item 12.

Not built: a 180° fisheye (fields must stay below 90°), aiming in systems with tilted mirrors
(OP-21), OpticStudio's enhanced ray aiming, the ray-aiming cache switch and wizard, automatic
pupil shifts, and any comparison with OpticStudio's output.

## Aspheres

The real ray trace also traces these surface types (`OpenSim.Optics/Sequential/Aspheres`), each
a Standard conic plus its own terms, as OpticStudio defines them: Even Asphere, Odd Asphere,
Extended Asphere, Extended Odd Asphere, Q-Type Asphere (Qbfs and Qcon, after Forbes),
Superconic, Alternate Even, Alternate Odd, Periodic and Odd Cosine. All of them share one
intersection method (Newton's method started from the conic, with a bisection fallback); a ray
that cannot be placed on the surface is reported as missing it. Coefficients are in SI units.

An Even Asphere can be converted to a Q-Type Asphere and back by a least-squares fit of the sag
over an aperture radius, which also becomes the Q-type normalization radius; the fit reports
its RMS and largest sag error. An Even Asphere without the r² term converts to Qcon and back
exactly; an r² term cannot be expressed in Qcon and shows as fit error.

The Lens Data Editor offers every form except Odd Cosine (its help page does not give the
order of its term parameters), with OpticStudio's parameter columns, and saves them with the lens
(see "Lens Data Editor"). Odd Cosine is built from code only. Not built: Cubic Spline and Extended Cubic Spline (OpticStudio's
help does not give the spline's end conditions) and the fast even-asphere trace. The equations,
sources and the points not yet compared with OpticStudio are in `docs/Optics-Conventions.md`
§17 and §10 item 13.

Paraxial data (EFL, BFL, pupils, paraxial rays, the solves) use each surface's curvature at
its vertex, so an r² term counts: an Even Asphere's α1 adds 2α1 to the curvature, a Qbfs
surface's polynomial adds 2S(0)/ρmax², and so on (§17 lists every form). A cone term (the Odd
Asphere's β1·r and its like) has no first-order equivalent and is left out of paraxial data
without a warning; the real trace includes it. The Periodic and Odd Cosine surfaces give their
curvature in the y–z plane. An Odd Cosine term with a power between 0 and 2 (other than 1) has
no curvature at the vertex, and the lens is refused with a message naming the surface.

## Idealised and special surfaces

The real ray trace also takes these OpticStudio surface types (OP-27), each as its OpticStudio
2025 R2 help page defines it:

- **Paraxial:** an ideal thin lens of given focal length on a plane. Its optical path keeps it
  free of aberration: exactly, for collimated light by default, or for the object distance the
  paraxial marginal ray gives (OpticStudio's OPD modes 2 and 3).
- **Paraxial XY:** an ideal cylindrical or toroidal thin lens with x and y powers; its OPD is
  only paraxially accurate and is flagged so.
- **ABCD:** a black box given by x and y ray matrices on height and slope. No OPD through it.
- **Retro Reflect:** a plane that, as a mirror, sends every ray back along itself.
- **Conjugate:** the mirror-like surface that images one given point perfectly onto another.
- **Fresnel, Extended Fresnel, Generalized Fresnel, Cylinder Fresnel:** the ray meets the
  substrate and turns by the slope of the Fresnel facets. No OPD through a curved substrate.
- **Lenslet Array:** an odd number of rectangular even-asphere lenslets in x and y.

The paraxial data take each surface's first-order effect (the y section of an anamorphic one).
The lens data editor edits and saves them, except the Lenslet Array, whose help page gives no
parameter layout. The equations and the points not yet compared with OpticStudio are in
`docs/Optics-Conventions.md` §18 and §10 item 14.

Not built: the Slide, Filter, Data and Atmospheric surfaces (the help pages do not define
Slide's transmission, Filter and Data are user-defined DLL surfaces, and the atmospheric
refraction model's sources are not on hand), the Paraxial surface's OPD modes 0 and 1, and
any comparison with OpticStudio's output.

## Solves

A solve computes one cell of the lens data from a condition. It is recomputed each time the
solves are applied. The solve library (`OpenSim.Optics/Sequential/Solves`) has OpticStudio's
solves:

- **Curvature:** marginal ray angle, chief ray angle, pickup, marginal ray normal, chief ray
  normal, aplanatic, element power, concentric with surface, concentric with radius, F/#.
- **Thickness:** marginal ray height (paraxial, or a real ray at a pupil zone), chief ray
  height, edge thickness, pickup, optical path difference, position, compensator, center of
  curvature, pupil position.
- **Material:** model glass, pickup, substitute, offset.
- **Clear semi-diameter:** automatic, pickup, maximum.
- **Conic, parameters and TCE:** pickup.

Solves run in surface order, and on each surface in column order. By default the engine repeats
the pass until nothing changes, so a solve that depends on a later surface still settles. A
pickup can only read an earlier surface, or an earlier column of the same surface. A marginal
ray height solve cannot sit before the stop. Both rules are OpticStudio's.

A solve that cannot be met leaves its cell as it was and says why, naming the surface, column
and solve. For example, a marginal ray angle on a surface with the same index on both sides.

The lens data editor stores solves and pickups on its cells and evaluates them after every
edit; the analyses trace the solved lens (see Lens Data Editor). The definitions, formulas and the readings not yet compared
with OpticStudio are in `docs/Optics-Conventions.md` §19 and §10 item 15.

Notes on specific solves:

- The maximum semi-diameter is the automatic value of the one configuration, because
  configurations are not built.
- The model glass is this program's stand-in, not OpticStudio's formula (see "Glass tools and
  material plots").
- The substitute flag leaves the glass unchanged.

Not built:

- the ZPL macro solve (needs the macro interpreter);
- the chief ray solve on coordinate-break parameters (offered in the editor, refused when
  evaluated);
- solves on extra data;
- the optimizer that would use solves in its derivatives.

## Lens Data Editor

**Lens Data Editor…** in the Optics panel opens the sequential surfaces in a window
(`OpenSim.App/Views/LensDataEditorWindow.xaml`, `OpenSim.App/ViewModels/OpticsLensEditorViewModel.cs`).
The panel shows the number of surfaces and whether the lens can be traced. The surfaces save
with the project and the lens file; a new project starts with an object at infinity, a stop
and an image surface. The analyses read these surfaces with their solves evaluated.

- **The table.** One row per surface, 0 the object and the last the image; neither can be
  deleted, and no surface can be inserted before the object or after the image. Columns:
  comment, radius (Infinity is a plane), thickness, material, coating, clear semi-diameter,
  chip zone, mechanical semi-diameter, conic, TCE, then the surface type's parameters.
  Lengths are in the lens unit; changing the lens unit converts the surfaces, parameters
  included, so the lens keeps its size (and clears the undo history). The material is a glass
  of the active catalogs (`catalog:glass` picks one), `MIRROR`, an index such as `1.5`, a model
  glass `nd, Vd` or `nd, Vd, ΔPgF`, or empty for air.
- **Surface types.** The type column (or Surface properties…) offers Standard and every type
  the ray trace has: Even Asphere, Odd Asphere, Alternate Even, Alternate Odd, Extended Asphere,
  Extended Odd Asphere, Q-Type Asphere, Superconic, Periodic, Fresnel, Extended Fresnel,
  Cylinder Fresnel, Generalized Fresnel, ABCD, Conjugate, Paraxial, Paraxial XY, Retro Reflect
  and Coordinate Break. Each type's parameter columns are OpticStudio's, named from its help
  page; the tooltip gives the parameter number and the unit (for example lens units^-3 for an
  Even Asphere's α2). Paraxial, Paraxial XY, ABCD, Retro Reflect, Conjugate and Coordinate
  Break have no radius or conic column. A type with more than 32 parameters (Extended Asphere,
  Q-Type, Superconic, Generalized Fresnel) shows its first 32; the rest are kept and saved.
  Changing a row's type keeps the shared columns and clears the parameters. The Paraxial
  surface needs OPD mode 2 (infinite conjugates) or 3 (paraxial conjugates).
- **Coordinate breaks and tilts.** A Coordinate Break row has comment, thickness, decenter X
  and Y, tilt about X, Y and Z (degrees) and order (0 decenter then tilt, 1 tilt then
  decenter); its properties hold the coordinate return (none, orientation only, orientation
  XY, orientation XYZ) and the surface to return to. Every other surface's properties hold the
  tilt and decenter before it (decenters, tilts, order) and after it: explicit values, this
  surface's before values picked up or reversed, or a prior surface's picked up or reversed.
- **Semi-diameters.** A clear or mechanical semi-diameter marked **A** is automatic and shows
  the size the program computed; typing a value fixes it.
- **Flags.** Cells can be made variable (V), given a solve (OpticStudio's solve names, with
  their parameters) or picked up from another cell (P), with the grid's context menu,
  Ctrl+Shift+V (variable) or Ctrl+Shift+F (the flag editor). Solves and pickups are evaluated
  after every edit: the cell shows the solved value. A solve that cannot be met keeps the
  cell's value, adds "!" to its badge and puts the reason in its tooltip, and the lens check
  lists it; the analyses then refuse the lens with the same message. Variables are stored for
  the optimizer, which is not built. Chip-zone and mechanical semi-diameter pickups are stored
  but not evaluated (OpticStudio has no solve for those columns), and System Check says so.
  Parameter columns 13 and above take variables only.
- **Surface properties…** (or the row's context menu) opens the surface's properties: type,
  row colour, make surface stop, ignore this surface, aperture type and its sizes and decenters
  (circular, rectangular, elliptical, spider, user-defined vertices as `x y; x y`, floating),
  obscuration, the tilt and decenter before and after, and the drawing options (skip rays to
  this surface; hide rays and do not draw are kept for the layouts but not drawn differently
  yet). A coordinate break's properties are its coordinate return and the drawing options.
- **Commands.** Insert surface, Insert after, Delete surface, Undo, Redo (every edit and every
  tool is one undo step; the grid's own keys work too), Make stop, Go to surface (a number, or
  part of a type or comment).
- **Tools** (the Tools expander; first and last surface, factor, focal length and reflecting
  surface are its inputs): Reverse elements, Scale lens, Make focal, Make double pass, Remove
  all apertures, Semi-diameters to circular / floating / maximum apertures, Remove all
  variables, Set all radii variable, Set all thicknesses variable, System check and Compare with
  file… (the File Comparator, against a lens file or a project; Differences only is on by
  default). Scale lens scales each parameter by its unit (a decenter by the factor, an Even
  Asphere's α2 by the factor⁻³, a tilt not at all). Reverse elements and Make double pass
  refuse surfaces of other types than Standard and tilted surfaces, naming them. What each
  tool does, and where it reads OpticStudio's help differently, is in
  `docs/Optics-Conventions.md` §20.
- A surface type this build does not trace (from a file of a later build) is shown with its
  parameters by number, saved back unchanged, and the lens check names it; the analyses then
  refuse the lens.

Not built: the Odd Cosine and Lenslet Array surfaces in the editor (their help pages do not
give the parameter layout), Paraxial OPD modes 0 and 1, solves on parameters 13 and above, the
chief ray solve on coordinate-break parameters, the optimizer, configurations (OP-30), Create
Error Ray, the surface properties' scattering, physical optics and import tabs, and
per-surface coating layer multipliers.

## Polarization

The polarization ray trace (`OpenSim.Optics/Polarization`, OP-51) carries the electric field
along each real ray: the Fresnel or coating amplitudes in each surface's s and p frame, the
reflectance of mirrors, and the bulk absorption of the glasses. The field is scaled so that its
squared magnitude is the ray's power relative to launch, so the trace gives each ray's
transmission as well as its polarization.

- **System polarization** (system data panel, group **Polarization**): **Unpolarized** (each
  ray traced with (Jx, Jy) = (1, 0) and (0, 1), intensities averaged), or the Jones vector Jx,
  Jy with the X-phase and Y-phase in degrees (OpticStudio's sign convention); **Method**, the
  rule that turns the Jones vector into a field (X, Y or Z axis reference, as OpticStudio
  defines them; X is the default); **Convert thin-film phase to ray equivalent** (on by
  default). These save with the project and the lens file.
- **Coatings on surfaces.** A sequential surface names a coating of the catalog
  (`SequentialSurface.Coating`); blank is an uncoated surface. An uncoated mirror is aluminium
  0.7 − 7.0i. From glass to air the layers are reversed, as in the coating plots. A name the
  catalog lacks stops the trace with a message naming the surface.
- **Bulk absorption** comes from a glass's internal transmittance (IT) table, interpolated in
  wavelength; a wavelength outside the table is refused, not extrapolated. A material with an
  extinction coefficient k instead uses it. Each analysis has a **Bulk absorption** switch.

The **Polarization** group of the analysis picker has five analyses. Each takes its own Jones
vector (and **Unpolarized** where OpticStudio has it); the method and the phase conversion come
from the system polarization.

- **Polarization ray trace:** one ray (field, pupil, wavelength), surface by surface: angle of
  incidence, the bulk internal transmittance of the segment, the coating's intensity
  transmittance (reflectance on a mirror), the s and p amplitude coefficients applied, the field
  after the surface (local or global frame), and the relative and total transmission; then the
  system's 3 × 3 polarization matrix and its diattenuation.
- **Polarization pupil map:** the polarization ellipse at each point of a square pupil grid, at
  a chosen surface, drawn at the pupil point; a table of |Ex|, |Ey|, the phase of Ey minus that
  of Ex, and the orientation of the ellipse; and the mean transmission over the grid.
- **Transmission:** for every field and wavelength, the mean transmission of a pupil grid (rays
  stopped by apertures count zero), and the chief ray's relative and total transmission at
  each surface.
- **Phase aberration:** the phase of the image-space Ex and Ey along the tangential and
  sagittal fans, minus the chief ray's, in waves.
- **Transmission fan:** transmission along the tangential and sagittal fans for each field and
  wavelength.

Like the other ray analyses they read the lens data editor's surfaces. The equations are in `docs/Optics-Conventions.md` §21; the choices not
yet compared with OpticStudio are in §10 item 17.

Not built: real transmission in the other analyses (the call `PolarizationTracer.Transmission`
exists for them, but spot diagrams, MTF, PSF and the rest do not use it yet); pupil
apodization and vignetting factors in the transmission (OP-22); diattenuation and retardance
maps, Jones and Mueller matrix surfaces (OP-52) and birefringent media (OP-53); polarization of
non-sequential rays; the Polarization pupil map's interference between configurations; the
system's retardance (only its diattenuation is listed).

## Cross-checks against other programs

The real ray trace, the paraxial data and the thin-film engine are checked against stored
results of three independently written open programs: Optiland and RayOptics for rays and
first-order data, tmm for coatings (`OpenSim.Tests/References/OP-102`, read by
`OpenSim.Tests/Optics/ReferenceProgramTests.cs`). Five lenses with constant indices are
compared ray by ray and surface by surface: positions to 10⁻⁹ mm, optical paths to 10⁻⁶ waves.
The reference files were generated once by the Python scripts in that folder; the program does
not need Python. How each program's conventions map onto this program's is in
`docs/Optics-Conventions.md` §22. These are checks for developers; there is nothing to set in
the workspace. Not built: checks with catalog glasses, tilts, aspheres or ray aiming, and any
comparison with OpticStudio, which was not available.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- the non-sequential object editor; in the Lens Data Editor, surface types other than
  Standard, tilts and decenters, and the evaluation of solves and pickups;
- system data (aperture, fields, wavelengths);
- layouts with traced rays and the analysis windows (spot diagrams, ray fans, OPD, MTF, PSF and
  the rest);
- the solve column of the sequential surface editor (the solve library is built; see Solves);
- real transmission and polarization in the image-quality analyses (the polarization trace of
  OP-51 is built, but nothing but its own five analyses uses it yet);
- optimization and tolerancing of a lens;
- lens-file import;
- reference results from OpticStudio itself (the cross-checks use open programs only).
