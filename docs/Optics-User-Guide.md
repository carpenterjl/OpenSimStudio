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

- the ZPL macro solve in the editor (a macro sets it with SOLVETYPE; ZPL macros below), and
  on parameters;
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
  lists it; the analyses then refuse the lens with the same message. Variables are what the
  Optimize tool varies. Chip-zone and mechanical semi-diameter pickups are stored
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
maps and birefringent media (OP-53) (the Jones and Mueller matrix surfaces are in their own
section below); polarization of
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

## Toroidal, biconic and irregular surfaces

The real ray trace also takes four surfaces without rotational symmetry (OP-24,
`OpenSim.Optics/Sequential/Anamorphic`), as their OpticStudio 2025 R2 help pages define them:

- **Toroidal:** a y–z curve (the surface's radius and conic plus α1…α7 on y² … y¹⁴) turned
  about an axis parallel to y at the radius of rotation (parameter 1), with optional Zernike
  Standard sag terms. A radius of rotation of 0 is infinite: that is OpticStudio's cylinder
  lens, with power in y only; an infinite y radius with a radius of rotation gives power in x.
- **Biconic:** separate radius and conic in x (parameters 1 and 2) and y (the radius and conic
  columns).
- **Biconic Zernike:** the biconic plus polynomials in x and in y (x¹ … x¹⁶, y¹ … y¹⁶) and up to
  210 Zernike Standard terms.
- **Irregular:** a Standard conic with spherical aberration, astigmatism and coma terms given at
  the clear semi-diameter, the whole surface decentered and tilted for tolerancing. The terms
  need a fixed clear semi-diameter; with an automatic one the trace stops with a message.

The Lens Data Editor offers and saves all four with OpticStudio's parameter columns, including
the extrapolate flag in parameter 0 of the Zernike forms (0: the Zernike terms stop at the
normalization radius; 1: they apply everywhere).

Paraxial data use the surface's curvature in the y–z plane at its vertex (for example the y
radius of a biconic or a cylinder lens), as for Paraxial XY. A surface that the rotationally
symmetric paraxial model cannot hold is refused with a message naming the surface and the
cause: a Zernike term that moves the vertex (piston, defocus and the other m = 0 terms), tilts
it (tilt, coma) or turns the astigmatism to 45°, a linear x or y term, a tilted or decentered
Irregular surface, or Irregular astigmatism at an angle that is not a multiple of 90°. A lens
holding such a surface cannot be launched, aimed, solved or analysed until that is lifted; only
rays traced from given starting points go through it. The equations and the readings not yet
compared with OpticStudio are in `docs/Optics-Conventions.md` §23 and §10 item 19.

Not built: importing Zernike coefficients from a file into the Toroidal surface, a separate
"Cylinder" type name, and any comparison with OpticStudio's output.

## Multiple configurations

**Multi-Configuration Editor…** in the Optics panel opens the configurations of the lens
(zoom positions, conjugates, scan angles) in a window
(`OpenSim.App/Views/MultiConfigEditorWindow.xaml`,
`OpenSim.App/ViewModels/OpticsMultiConfigEditorViewModel.cs`). Beside it, a picker sets the
**current configuration**: every analysis computes on that configuration, and changing it
recomputes the open analysis windows. The table saves with the project and the lens file; a
lens without one is a single configuration, and files from before this feature open as such.

- **The table.** One row per operand: the operand code in the first column (MOFF, an unused
  row, is the default), its numbers (surface, parameter, field or wavelength, as the column
  header says) in the next three, then one column per configuration. Insert and Delete (or the
  buttons) add and remove rows; cell and row edits undo and redo. Surfaces count from the
  object (0), fields and wavelengths from 1; values are in the lens unit, degrees and
  micrometers, as in the other editors. THIC takes Infinity.
- **Operands.** 92 of OpticStudio's 116 codes are applied: surface data, glass, model glass,
  parameters, semi-diameters, conic, TCE, coating, comment, ignore and draw flags, the stop,
  apertures, tilts and decenters, coordinate returns, solve parameters, system aperture,
  apodization, fields, wavelengths, ray aiming, polarization, temperature, pressure, title,
  configuration weight (CWGT) and range pickups (PUCN). The others (non-sequential objects,
  composite surfaces, CAD parts, tolerancing, settings this program does not save) can be
  entered and are saved, but the problems line names them and the analyses refuse the table
  until they are removed. The type cell's tooltip says what each operand does.
- **A value an operand controls** is replaced in every configuration, whatever the Lens Data
  Editor holds; a solve or pickup on that Lens Data Editor cell is not applied in the
  configurations. Edit such values in this editor. The Lens Data Editor keeps showing the lens
  as entered, not the current configuration.
- **Cell flags.** A cell can be variable, pick up another cell (scale and offset; from an
  earlier configuration, or an earlier row of the same configuration), or carry a thermal
  pickup (see Thermal analysis below).
- **Configurations.** Insert configuration (a copy of the column at the cursor), Insert with
  pickups (every cell picks up configuration 1), Delete configuration (the column at the
  cursor; pickups from it become fixed and the status line says which), Previous and Next.
  These change the columns, so they clear the undo history.
- **Tools.** Add all data adds a row of the chosen operand for every surface, field or
  wavelength that holds such data and is not yet in the table, at its current value in each
  configuration. Make single configuration removes the table and leaves the lens, the system
  data included, in the current configuration.
- **Problems.** The line under the table lists, per configuration, every row that cannot be
  applied (a missing surface, an unsupported operand, a pickup from a later cell), and the
  failing cell carries the message.
- **Not built.** Make Conjugate, ZPL-macro solves, and showing or editing the current
  configuration's values in the Lens Data Editor.

## Ray fans, spot diagrams and footprints

Eight analyses in the group **Rays and spots** trace the lens of the Lens Data Editor with the
real ray trace, ray aiming and the fields' vignetting factors as the system data sets them:

- **Ray aberration (ray fan):** for each field, the tangential fan (EY against Py) and the
  sagittal fan (EX against Px) on the image surface, in µm, one curve per wavelength, relative
  to the primary wavelength's chief ray (or the shown wavelength's when one is chosen). Number
  of rays per side, field, wavelength, the plotted component of each fan, and whether apertures
  are checked are settings.
- **Standard spot diagram:** the rays of a hexapolar, square or dithered pupil pattern on the
  image surface (or any surface), in µm about the chief ray, centroid, middle or vertex, with
  the RMS and GEO radii and the Airy disk (1.22 λ times the real working F/#). The ray density
  is the number of hexapolar rings, or the rays across the square grid; at least 3.
- **Through focus spot diagram:** five spots per field at −2, −1, 0, 1 and 2 times the delta
  focus (µm) from the image surface.
- **Full field spot diagram:** every field on one plot in lens units, relative to field 1's
  reference, with an exaggeration factor on each spot's size.
- **Matrix spot diagram:** one spot per field and wavelength, optionally each referred to its
  own point ("ignore lateral color").
- **Footprint diagram:** a (2n + 1) × (2n + 1) grid of rays where it meets any surface, in lens
  units, over the circle of the surface's semi-diameter, with the percentage of rays through.
- **Y-Ybar drawing:** paraxial marginal ray height against chief ray height at each surface.
- **Incident angle vs. image height:** the angle at the image surface of the lower marginal,
  chief and upper marginal rays against image height, for fields along +y.

Rays that fail or are stopped by an aperture are never drawn; each analysis lists how many,
why and at which surface. The dithered pattern uses a fixed seed, so it is the same on every
run. The definitions, and what was chosen where OpticStudio's help is silent, are in
`docs/Optics-Conventions.md` §25 and §10 item 21.

Not built: the Configuration Matrix Spot (it needs multi-configuration, OP-30); apodized pupil
sampling (a Gaussian or cosine-cubed apodization is named in the result and not applied); the
Airy ellipse (a circle is drawn) and the Airy disk on the matrix spot; spots in direction
cosines; polarization and scattering in the spots; the ray fan at intermediate surfaces; the
footprint's "Ring" density and aperture shapes other than the semi-diameter circle; afocal-mode
spots.

## Optical path difference and wavefront

Five analyses read the lens data editor's surfaces and measure the optical path difference (OPD)
against the chief ray, in waves (positive: the ray's wavefront is ahead of the reference). Four
are in the **Wavefront** group, one in **Aberrations**.

- **Optical path difference:** the OPD fan against Py (tangential, Px = 0) and Px (sagittal,
  Py = 0), with *Number of rays* on each side of the centre, one field or all (field 0), one
  wavelength or all (wavelength 0; every wavelength then uses the primary wavelength's reference
  point). A table lists every value; a failed ray is marked "failed".
- **Wavefront map:** the OPD over an n × n pupil grid as a false-colour image or contours, with
  its peak-to-valley and its RMS about the mean. *Remove tilt* and *Remove focus* subtract a
  least-squares fit; *Reference to primary* uses the primary wavelength's reference point.
- **Interferogram:** the fringes of this beam against a perfect reference beam, dark where the
  OPD is a whole number of waves; *Scale factor* (2 for double pass) and *X-tilt*, *Y-tilt*
  (fringes across the pupil diameter).
- **Foucault analysis:** the knife-edge shadowgram: the diffraction field at focus, the knife
  (left, right, above or below, at *Position* µm from the chief ray) taking away its half, and
  the field propagated back to the pupil. *Logarithmic* shows log10 of the relative intensity.
- **Pupil aberration:** where real rays cross the stop compared with where the paraxial pupil
  puts them, as a percentage of the stop radius. More than a few percent means ray aiming is
  needed; with aiming on it is near zero.

Common settings: **Reference OPD** — *Exit pupil* (the default: a sphere centred on the image
point through the paraxial exit pupil; a plane per ray when the exit pupil is at infinity),
*Infinity*, *Absolute* (no correction) or *Absolute 2*; in afocal image space the reference is a
plane normal to the chief ray. **Reference point** — chief ray or centroid. **Check apertures**,
and **Allow OPD limitations**, which lets the analyses run through a surface (ABCD, a Fresnel on
a curved substrate, a lenslet array, Paraxial XY) whose OPD is approximate or undefined; without
it such a lens is refused with the surface named. The result lists the reference used and how
many rays failed. The rules are in `docs/Optics-Conventions.md` §26; readings not yet checked
against OpticStudio are in §10 item 22.

Not built: the Reference OPD as one system setting (each analysis has its own), polarization
phase, exit-pupil-shaped maps, intermediate surfaces, subapertures, the interferogram's second
beam from another configuration, the Foucault reference bitmap and the FOUC operand.

## Field curvature, distortion, colour and illumination

Six analyses (OP-37, `OpenSim.Optics/Analysis/FieldAberrations/`) read the lens data editor's
surfaces. Five are under **Aberrations**, relative illumination under **Illumination**.

- **Field curvature and distortion.** Tangential (T) and sagittal (S) focus against field, from
  Coddington's equations along each field's real chief ray, as the distance along the image
  surface's z from the chief ray's image point to each focus (positive in the direction the
  light travels); and distortion against field as a percentage or as a length, F-tan(θ), F-θ or
  either calibrated (best-fit focal length). Settings: wavelength (0 for all), number of field
  points, scan direction (+y, +x, −y, −x), distortion type, distortion as a length, ignore
  vignetting factors (on by default). The text lists the reference focal length used. For a
  system that is not symmetric about the scan plane, or with an ideal, ABCD or Fresnel surface,
  the field curvature is listed as not computed and the distortion is still given.
- **Grid distortion.** A square grid of chief rays against the linear (ABCD) prediction taken
  at a reference field; the percent distortion at each point, the largest, and SMIA-TV
  distortion. Settings: grid size, wavelength, reference field (0 for the axis), plot scale
  (exaggeration), H/W aspect, symmetric magnification, field width. Angle fields use tangents of
  the angles; theodolite fields are refused.
- **Longitudinal aberration.** Where on-axis marginal rays cross the axis, measured from the
  image surface, against pupil zone, for each wavelength (zone 0 paraxial).
- **Lateral colour.** The chief ray's image height of the shortest wavelength minus the longest,
  or of every wavelength minus the primary, against +y field; real or paraxial rays.
- **Chromatic focal shift.** The focal shift against wavelength over the range of the wavelength
  table, referred to the primary wavelength's paraxial focus; paraxial, or real marginal rays at
  a pupil zone. The text lists the shift at each table wavelength, the largest range, and the
  diffraction-limited depth of focus 4λF².
- **Relative illumination.** Image irradiance of a uniform Lambertian scene against field,
  normalised to its largest value, from the projected solid angle of the exit pupil seen from
  each image point; with the effective F/#. Settings: ray density (at least 5), field density,
  use polarization (weights each ray by its Fresnel or coating transmission for unpolarized
  light), remove vignetting factors, wavelength, scan direction, log scale. The pupil
  apodization (Gaussian or cosine cubed) is applied.

The definitions, what each number assumes, and which readings of OpticStudio's help are this
program's own are in `docs/Optics-Conventions.md` §27 and §10 item 23. Not built: the afocal
forms (diopters and angles), field curvature for skew (non-symmetric) systems, the SMIA-TV,
reference-field and H/W aspect settings of field curvature and distortion, the vector display of
grid distortion, the Airy disk of lateral colour, apodization of an object-cone-angle aperture,
and any comparison with OpticStudio's output. The vignetting plot is described under "Ray aiming
and vignetting".

## Surface shape analyses

The analysis menu's **Surface** group (OP-43) shows the shape of any surface of the lens, as a
fabricator or an asphere designer reads it. Every analysis takes the **Surface** number (1 to the
image surface; a coordinate break is refused) and works over that surface's clear semi-diameter.

- **Sag table**: the sag along +y from the vertex with its best-fit sphere (BFS). Settings:
  **Step size** (0 chooses one), **Best fit sphere criterion** (minimum volume, minimum RMS with
  no offset, minimum RMS with offset), **Reverse direction** (take the other side of the surface
  as the air side), **Minimum radius** and **Maximum radius** of the fit (both 0: the whole
  clear semi-diameter). The listing gives the BFS radius and vertex offset, the depth and volume
  of material to remove (minimum volume), the slope deviations, and the eight columns Y-coord,
  Sag, BFS sag, Deviation, Remove, Slope, BFS slope and Slope delta, with a plot of the sag and
  the sphere.
- **Surface sag**, **Surface slope**, **Surface curvature**: false-colour or contour maps
  (**Show as**, **Contour format**: blank, one interval, or a list of levels) on an n × n grid
  (**Sampling**, odd, default 65), blank outside the clear aperture. Slope and curvature take a
  **Data** choice: tangential, sagittal, x, y or modulus. Sag is in lens units, slope
  dimensionless, curvature in 1/lens unit.
- **Surface sag/slope/curvature cross section**: the same quantities along a line through the
  vertex at **Angle** degrees from the local x axis, across the clear diameter.
- **Remove** (maps and cross-sections): nothing, the sphere of the vertex radius, or the
  best-fit sphere (with its criterion and Reverse direction).

Each result lists what it assumed: the clear semi-diameter and where it came from, how the
curvatures were computed (analytic for a Standard surface, differenced for other types), which
side was taken as air, and the sphere removed. Definitions and the readings of OpticStudio's
help are in `docs/Optics-Conventions.md` §28. Not built: Surface Phase and Phase Slope (no
surface type carries a phase yet), off-axis coordinates, the Base Sag and Composite Sag removal
options, and the 3-D surface plot and grey-scale display.

## Reports

Six analyses in the Reports group follow OpticStudio's Reports group (details and formulas in
`docs/Optics-Conventions.md` §29). They read the project's surfaces with solves evaluated, list
lengths in the system's lens unit, and give the same text on every run.

- **System data**: the system summary, the aperture, fields and wavelengths as entered, and the
  paraxial system data with focal data by wavelength.
- **Prescription data**: the full prescription in OpticStudio's section order: general data,
  surface data, surface detail (parameters, tilts and decenters, apertures), edge thickness,
  solves and variables, indices at every wavelength, global vertices and centres of curvature
  (in the frame of the "Global reference surface" setting), element volume and mass, cardinal
  points and the glass catalog files used. Each optional section has a checkbox. Element mass
  needs a catalog glass with a density; a constant index has none.
- **Surface data**: one surface's shape, sag and edge thickness, surface and element powers and
  focal lengths, indices before and after, and its element's volume.
- **Cardinal points**: focal, principal, anti-principal, nodal and anti-nodal planes of a range
  of surfaces at one wavelength.
- **System summary graphic**: the handful of first-order numbers a report page shows, as text.
- **Report graphic**: 4, 6 or 9 analyses on one page. Type each position's analysis id (for
  example `SingleRayTrace`, `CardinalPoints`, `ParaxialGaussianBeam`); each runs with the
  settings it was last used with. The window lists the plots of all positions in order.

The prescription's text reads back: `PrescriptionReader.Read` (OpenSim.Optics) turns the Text
tab of a prescription into the saved surfaces of the same lens, tilts, aspheres, apertures and
comments included; solve and variable flags are not carried.

Not built: Multi-Config Data, OpticStudio's real working F/# per field and wavelength, POP
settings, the XZ orientation of Cardinal points, the best fit glass of a model glass, a drawn
system summary graphic, and the report graphic's page arrangement and saved report files.
Element volumes take circular edges at the clear semi-diameters; surface apertures do not enter.

## Jones and Mueller matrix surfaces

Two surface types act on the polarization of light without changing the geometry of the trace
(OP-52). Both are planes: rays refract or reflect there as at a plane Standard surface, and
the surface's coating or Fresnel amplitudes apply as at any interface. Pick them in the Lens
Data Editor's type column; neither takes a radius or conic.

- **Jones Matrix** (as in OpticStudio): parameters 1–8 are Ar, Ai, Br, Bi, Cr, Ci, Dr, Di, the
  real and imaginary parts of the 2 × 2 matrix [[A, B], [C, D]] acting on the field's
  components along the surface's local x and y axes, in OpticStudio's sign convention for
  phases. Examples from OpticStudio's help: an X analyzer is A = 1 and the rest 0; a
  quarter-wave plate in x is A = (0, 1), D = (1, 0); a 25% intensity filter is A = D = 0.5. The
  matrix is meant for normal incidence; at other angles it acts in the plane at right angles to
  the ray, which keeps the field transverse and never adds power.
- **Mueller Matrix** (this program's own type; OpticStudio has no sequential one): parameters
  1–16 are the 4 × 4 Mueller matrix M00 … M33 row by row, acting on the Stokes vector
  (I, Q, U, V) in the surface's local x, y axes. It can depolarize: diag(1, d, d, d) keeps the
  power and multiplies the degree of polarization by d. A matrix that would turn a ray's Stokes
  vector into an unphysical one (negative intensity, or more polarized power than power) stops
  the analysis with a message naming the surface.

After a Mueller surface a ray carries partially polarized light, which has no single field:

- **Polarization ray trace** shows dashes for Ex, Ey, Ez from that surface on, and "not
  defined" for the system diattenuation and matrix. For every ray it lists the Stokes vector at
  the last surface reached and the degrees of polarization, linear polarization and circular
  polarization (V/I, signed).
- **Transmission** and **Transmission fan** work as before (the power is exact).
- **Polarization pupil map** at or after a Mueller surface, and **Phase aberration** of a lens
  with one, stop with a message naming the surface.

Only the polarization analyses see the matrices; the geometric analyses treat both types as
plain planes. The equations and the readings of OpticStudio's help are in
`docs/Optics-Conventions.md` §30 and §10 item 26.

Not built: the non-sequential Jones Matrix object and any non-sequential Mueller object (they
need the non-sequential core, OP-70); a check that a Mueller matrix is physically realisable
for every input (only each ray's output is checked); the pupil map of the polarized part of
partially polarized light.

## Merit function

**Merit Function Editor…** in the Optics panel opens the merit function: a list of operands,
one per row, saved with the project and the lens file. Type an operand's four-letter code
(EFFL), its name (Effective focal length) or pick it in the Operand column; the parameter
columns then show what the operand reads (Surf, Wave, Hx, Hy, Px, Py, Op#...). "Find an
operand" searches the codes and names. Insert, Delete, Undo and Redo work as in the Lens Data
Editor, as do the grid's own keys.

**Update** evaluates the merit function on the project's lens, solves applied, and fills the
Value and % Contrib columns; the merit value is shown at the top. The merit value is the
weighted root-mean-square of value minus target, √(Σ W(V − T)² / Σ W), with W the absolute
value of the weight. Any edit clears the values until the next Update. A row that cannot be
computed (a ray that misses a surface, a surface that does not exist) shows "failed", with the
reason in its tooltip and below the grid; it is scored with a large penalty and the other rows
are unaffected. A list that cannot be evaluated at all (an unknown code, a GOTO to an earlier
row, a math row reading a later one) is refused with every problem named.

Built: the control operands (BLNK, DMFS, ENDX, GOTO, OOFF, SKIN, SKIS, USYM), CONF, effective
focal length, total track, a few lens-data constraints and bounds, real ray coordinates and the
general math operands, and the ray-aberration and boundary operands of the Optimization Wizard
(next section). Rules and readings are in docs/Optics-Conventions.md §31. Not built: the rest of
the operand catalogue, the merit function listing, and .MF files. Optimization follows the
wizard.

## Seidel and full-field aberrations

Three analyses (OP-36, `OpenSim.Optics/Analysis/Aberrations/`) under **Aberrations** read the lens
data editor's surfaces.

- **Seidel coefficients.** Surface by surface and summed: the unconverted Seidel sums S1 to S5
  (spherical, coma, astigmatism, Petzval field curvature, distortion) and the colour sums CL and
  CT (lens units); the wave coefficients W040 … W220T (waves at the edge of the exit pupil); the
  transverse coefficients TSPH … TLAC and the longitudinal ones LSPH … LLAC (lens units); the
  Petzval radius and the Lagrange invariant. The aspheric part of each aspheric surface is listed.
  Setting: wavelength (0 for the primary). Colour is measured between the shortest and longest
  table wavelengths.
- **Seidel diagram.** The same unconverted sums as bars for a surface range, with their sum as
  the last group. Settings: first and last surface, wavelength, ignore distortion, ignore
  chromatic.
- **Full-field aberration.** Zernike Standard terms fitted to the wavefront at each point of a
  grid of field points, and one aberration mapped over the field: defocus (Z4), primary
  astigmatism or primary coma (magnitude, with the angle in the table), or any one term.
  Settings: centre field, field shape (rectangular or elliptical), wavelength, x and y half-widths
  (0 for the largest field) and samplings, maximum term (up to 231), aberration and term, pupil
  sampling, display (absolute, relative to the average, or the average), image or contours.
  Points outside the field's normalisation boundary are left blank.

The Seidel sums hold for Standard, Even, Odd, Extended and Extended Odd Asphere surfaces, the
types OpticStudio's help lists; any other surface is refused, naming it, as is an odd asphere
with an r or r³ term. The transverse coefficients are the image-plane ray errors (TSPH matches the
ray fan's cubic coefficient); the longitudinal ones are positive when rays cross the axis before
the paraxial image. Definitions and the readings of OpticStudio's help that are this program's
own are in `docs/Optics-Conventions.md` §32 and §10 item 28. The Contrast Loss Map is in the MTF
group (see Modulation transfer function). Not built: the Seidel diagram's plot scale, full-field aberration's icon and grey-scale
displays, fifth-order coefficients, the afocal forms, and any comparison with OpticStudio's
output.

## Point spread function and Strehl ratio

Six analyses in the **PSF** group compute the diffraction image of a point source from the lens
data editor's surfaces: the pupil function (the OPD against the chief ray's reference sphere, the
pupil apodization, and no light where a ray is vignetted or fails), transformed to the image.

- **FFT PSF:** the PSF by FFT of an n × n pupil grid (*Sampling*) zero-padded to 2n × 2n, on a
  plane normal to the chief ray, as a false-colour map or contours. *Type*: linear or logarithmic
  intensity (*Log decades* below the peak), real or imaginary amplitude, or phase in degrees.
  *Display* draws only the central samples. *Image delta* (µm) samples the image more finely
  than the FFT's own spacing (0 for the FFT's own; a larger one is refused). *Wavelength* 0 is
  polychromatic: every wavelength on the shortest wavelength's grid, weighted by its weight and
  (λ_min/λ)².
- **FFT PSF cross section:** a row (X) or column (Y) of the FFT PSF, the centre by default;
  *Plot scale* limits the half-width.
- **FFT line/edge spread:** the FFT PSF summed across a line parallel to x (X) or y (Y); the
  line spread is scaled to its peak, the edge spread runs from 0 to 1.
- **Huygens PSF:** the sum of one wavelet per traced ray on the plane tangent to the image
  surface, so tilted or curved image surfaces and distorted pupils are handled. *Pupil
  sampling*, *Image sampling* and *Image delta* (0 for the help's default λF/√n);
  *Use centroid* centres the image on the rays' centroid instead of the chief ray. The result
  lists the centre, the peak position and the PSF's own centroid offset.
- **Huygens PSF cross section:** the Huygens PSF along x or y through the centre.
- **Strehl ratio:** for each field and wavelength (and polychromatic), the Strehl ratio, the RMS
  wavefront σ and the small-aberration estimate 1 − (2πσ)².

Every PSF is scaled so the unaberrated PSF (the same pupil with no OPD) peaks at 1; its own peak
is then the Strehl ratio. *Normalize* scales the peak to 1 instead. The Strehl ratio is the
peak found by refining the largest sample; below about 0.1 it is flagged as not useful.

**Sampling warnings** head the result: *ALIASED* when the wavefront changes by more than half a
wave between neighbouring pupil samples, when the rays spread beyond the half-period the pupil
sampling resolves, or when the image grid is wider than that period; a caution above a quarter
wave. Raise the sampling or bring the image nearer focus. The help's validity notes are also
flagged: a chief ray more than 20° from the image surface's normal (use the Huygens PSF), a cone
faster than F/1.5 (scalar diffraction is optimistic there), and, for the Huygens PSF, an image
large enough that OpticStudio would switch to a spherical phase reference.

The rules are in `docs/Optics-Conventions.md` §33; readings not yet checked against OpticStudio
are in §10 item 29. Not built: polarization, image rotation, surface plots, intermediate
surfaces, OpticStudio's pupil-grid stretching, the Huygens spherical phase reference, the sum
over configurations, True Color and the coherent line/edge spread.

## RMS against field, wavelength and focus

Four analyses in the RMS group plot the RMS wavefront error (waves), the RMS spot radius,
x or y (µm), or the Strehl ratio:

- **RMS vs field**: at evenly spaced fields from the axis to the largest defined field in one
  direction (+y, +x, −y, −x), one curve per wavelength and a polychromatic curve (or only one
  of these, as Curves says).
- **RMS vs wavelength**: at evenly spaced wavelengths between the shortest and longest in the
  wavelength table, one curve per field.
- **RMS vs focus**: at evenly spaced focus shifts (lens units, added to the thickness before
  the image surface), one curve per field, polychromatic or at one wavelength.
- **RMS field map**: over a grid of field positions centred on one field, as a false-colour
  image or contours; with the rectangular array and one wavelength it can also show the
  wavefront's peak to valley.

Method: **Gaussian quadrature** traces rings and arms of rays with weights chosen so that few
rays give an accurate RMS (ray density = rings, 1 to 20, with 4 arms per ring). It assumes the
whole pupil is traced: where an aperture clips a ray, the point is left blank and the reason
listed. Use the **rectangular array** (ray density = rays across a square grid) for a vignetted
pupil; it leaves clipped rays out and counts them. Refer to chief ray removes the wavefront's
piston (spot: about the chief ray); centroid also removes its tilt (spot: about the centroid).
Show diffraction limit draws 0.072 waves, 0.8 for the Strehl ratio, or 1.22 λ times the
on-axis working F/# for a spot.

The **Strehl ratio** is the one the Strehl ratio analysis computes (the FFT PSF's peak over the
unaberrated peak), at each sampled field, wavelength or focus; *PSF sampling* sets its pupil
grid (16 to 1024, default 64). Method, ray density and Refer to do not change it. The
polychromatic curve uses the polychromatic FFT PSF (OpticStudio's RMS plots offer the Strehl
ratio for one wavelength only). Sampling warnings such as aliasing are listed under the table
with the point they belong to; raise the PSF sampling when one appears. Definitions are in
docs/Optics-Conventions.md §34.

Not built: Plot Scale, Use Polarization,
the field map at intermediate surfaces, and afocal systems (refused).

## Modulation transfer function

The MTF group holds fourteen analyses over three ways of computing the optical transfer function
(OTF), tangential (T, frequency along y) and sagittal (S, along x):

- **FFT** (FFT MTF, FFT through focus MTF, FFT surface MTF, FFT MTF vs field, FFT MTF map): the
  autocorrelation of the same pupil function the FFT PSF uses, so it is the transform of that
  PSF. The cutoff of each axis is 1/(λF) with the working F/# the traced rays give (for afocal
  systems the exit pupil diameter over λ, in cycles per milliradian). Type: modulation, real
  part, imaginary part, phase (degrees) or square-wave response.
- **Huygens** (Huygens MTF, through focus, surface, vs field): the transform of the Huygens PSF,
  with its pupil sampling, image sampling and image delta. Frequencies above the image grid's
  Nyquist frequency, or a PSF that reaches the grid's edge, are warned about.
- **Geometric** (Geometric MTF, through focus, vs field, map): the transform of the rays' spot,
  optionally multiplied by the diffraction-limited MTF of the same pupil (on by default, as the
  help advises). Good for strongly aberrated lenses; it does not know about diffraction
  otherwise.
- **Contrast loss map**: for each pupil point, how much a pair of rays one MTF-frequency shift
  apart lose contrast, 0.5(1 − cos 2πΔW), as two false-colour maps (T and S), with the MTF and
  its diffraction limit at that frequency (frequency 0: 5 % of the cutoff).

Wavelength 0 is polychromatic: every wavelength referred to the primary wavelength's chief ray,
weighted as the polychromatic PSF weights it. Field 0 plots every field. Show diffraction limit
draws the FFT MTF of the on-axis field (or field 1 if none is on axis) with the OPD set to zero.
Through focus adds the shift (lens units) to the thickness before the image surface. MTF vs field
and the maps sample fields between the table's, as the RMS analyses do; Remove vignetting factors
(on by default) replaces vignetting factors with apertures first. Map pixels are computed at their
centres; a width of 0 is twice the largest radial field. The MTF unit is cycles per millimeter
(cycles per milliradian in afocal mode); a focal system set to cycles per milliradian is refused.
Definitions are in docs/Optics-Conventions.md §38. The same engine (`MtfEngine.Evaluate`) gives
the MTF merit operands (MTFT, MTFS, MTFA; GMTT, GMTS, GMTA) and the MTF tolerance criteria.

Not built: polarization, intermediate surfaces, scattered rays, the Huygens sum over
configurations, the coherent transfer function, the 3-D surface plot, the through-focus spline,
the contrast loss map's circles and OPD indicator, afocal through-focus and Huygens MTF, and
the object-space cycles-per-milliradian unit of focal systems.

## Enclosed energy

Four analyses in the **Enclosed energy** group give the fraction of a point image's energy inside
a region about a reference point.

- **Diffraction enclosed energy:** from the PSF of an n × n pupil grid (*Sampling*, 16 to 512),
  computed exactly for that grid (no image sampling). *Type*: encircled (radius), x-only or
  y-only (a slit infinite in the other direction), or ensquared (half-width). *Refer to*: chief
  ray, centroid or vertex. *Maximum distance* (µm, 0 for the default) is limited to half the
  PSF's period; raise the sampling for larger distances. *Show diffraction limit* adds the
  aberration-free curve of the reference field. *Wavelength* 0 combines the wavelengths by their
  weights; *Field* 0 draws every field.
- **Geometric enclosed energy:** the same from rays on an n × n pupil grid at any surface, about
  the chief ray, centroid, vertex or middle (the centre of the smallest circle holding every
  ray). *Multiply by diffraction limit* (encircled only) multiplies by the Airy disk's curve at
  the reference field's working F/#.
- **Geometric line/edge spread:** the rays' line and edge spread about the chief ray, for a line
  or edge parallel to x and to y.
- **Extended source enclosed energy:** rays from every lit pixel of an IMA image (*File*, its full
  path; text or binary), *Field size* wide in field units, centred on *Field*; about 1000 ×
  *Rays x 1000* rays. Besides the four regions it draws the x or y distribution with its FWHM.
  Fields with vignetting factors need *Remove vignetting factors*.

The result lists the sizes holding 50 % and 80 % of the energy, the reference point, the rays
that did not arrive and the sampling warnings. The rules are in `docs/Optics-Conventions.md`
§39; readings not yet checked against OpticStudio are in §10 item 35. Not built: the Huygens PSF
option, polarization, scattering, BIM files, dashed-line styling.

## Optimization

**Optimize…**, beside the Merit Function Editor in the Optics panel, opens the local optimizer.
It lowers the merit function by changing the cells flagged Variable: radii (varied as
curvatures, so a surface may go flat), thicknesses, conics, entered semi-diameters, TCE,
surface parameters and extra data in the Lens Data Editor, and real-number cells of the
Multi-Configuration Editor. Solves and pickups are re-evaluated for every trial design, so a
solved radius or thickness follows the variables. A variable cell that cannot be varied (a
material, an automatic semi-diameter, an infinite thickness, a whole-number parameter, a text
cell) is refused by name before anything runs.

- **Algorithm.** Damped Least Squares (derivatives by finite differences; recommended) or
  Orthogonal Descent (no derivatives; trial steps along orthonormal directions, for noisy merit
  functions).
- **Cycles.** Automatic runs until no progress is made; 1, 5, 10 or 50 run that many cycles (a
  cycle is one derivative-and-step of DLS or one pass of OD); Infinite runs until Stop.
- **Threads.** The number of threads; the result is the same at any number.
- **Start / Stop.** The merit function before and now and the cycles run update as the run
  improves the design. Stop keeps the best design so far.
- **Applying and Undo.** When a run ends, finished or stopped, its variables are written into
  the lens as one step of the Lens Data Editor's undo (and into the multi-configuration table
  when one of its cells is a variable, which clears that editor's undo history). Undo in the
  Optimize window puts the variable cells back exactly as they were saved.

The starting design must evaluate with no failed row (as in OpticStudio); during the run a
design that cannot be built or traced is scored with the failed-row penalty and avoided. Rows
with a negative weight are met exactly (OpticStudio's Lagrangian multipliers); bound operands
are ordinary weighted rows. Rules and readings are in docs/Optics-Conventions.md §35. Glass
variables and substitution are under Glass substitution below. Global and Hammer searches are under Global and Hammer optimization below. Not built: analytic derivatives, per-type scale and
derivative-increment settings in the window (the library takes them), saving the Optimize
window's settings, and the "Auto Update" of open analysis windows during a run (they update
when the result is applied).

## Optimization Wizard

**Wizard** in the Merit Function Editor opens the Optimization Wizard, which writes the default
merit function: an RMS image error at every field and wavelength, integrated over the pupil.

- **Criterion**: wavefront (OPD in waves), spot radius, spot x, spot y, spot x + y, angular
  (for afocal image space), or contrast (MTF): the Moore–Elliott method, which writes MECT and
  MECS rows, the pupil phase differences across the shift of the **Spatial frequency**
  (cycles/mm), weighted by **Relative T/S weight**. Contrast needs a frequency below the cutoff
  and a focal image space; pupil points whose shifted partner falls outside the pupil are left
  out and counted in the status line. **Reference**: centroid (the field's weighted centroid; for the
  wavefront the mean and tilt are removed), chief ray (the primary wavelength's chief ray; for
  the wavefront the mean is removed), or unreferenced (wavefront only). **Ignore lateral
  colour** gives each wavelength its own centroid.
- **Pupil integration**: Gaussian quadrature (rings 1 to 20, arms 6, 8, 10 or 12; n rings
  integrate aberrations up to order 2n − 1 when there are at least 4n arms) or a rectangular
  array (rays across, 2 to 100; Delete vignetted leaves out the rays that fail now). Gaussian
  quadrature assumes the whole pupil is traced: when any of its rays is vignetted, the wizard
  writes the rectangular array instead and says so in its status line.
- **Symmetry**: automatic (when the lens is rotationally symmetric), on or off. On, fields on the
  y axis get half the rows at twice the weight, and the on-axis field one row per ring for the
  radial criteria; on a symmetric lens the merit value does not change.
- **Boundary values**: minimum and maximum center thickness and minimum edge thickness for glass
  (MNCG, MXCG, MNEG) and air (MNCA, MXCA, MNEA), in lens units, over every surface.
- **Configurations**: the current one or all (each after a CONF row). **Start at row**: where the
  rows go; by default the row after a DMFS row, else the end (0). Later rows that refer to rows
  by number are renumbered. **Overall weight** scales every weight. **Relative X/Y weight** weigh
  the x and y rows of spot x + y and angular (0 leaves those rows out; both 0 write radial rows).

**Apply** inserts the rows (one undo step in the editor); **OK** applies and closes. **Save
Settings** keeps the dialog's settings with the project; **Load Settings** and **Reset
Settings** show the saved ones and the defaults. Each row's weight is overall × field weight ×
wavelength weight × the pupil point's weight, so for one field and one wavelength the merit value
is the RMS of the RMS analyses; for spot x + y with equal relative weights it is the RMS radius
divided by √2. Docs/Optics-Conventions.md §40 has the rules and §10 item 36 the readings.

The Merit Function Editor also takes the MTF operands MTFT, MTFS, MTFA (diffraction) and GMTT,
GMTS, GMTA (geometric): Samp (pupil grid 32·2^(Samp−1)), Wave (0 polychromatic), Field, Freq.

Not built: contrast in afocal image space, the Huygens and square-wave MTF operands, the PTV
type, Max Distortion, the manufacturing-yield goal, Add Favorite Operands and the Gaussian
obscuration.

## Glass substitution

The **Glasses** section of the Optimize window lets the optimizer change glasses.

- **Glass list.** Refresh lists the surfaces with a glass. For a model glass ("nd, Vd" or "nd,
  Vd, ΔPgF" in the Material cell) tick Vary nd, Vary Vd or Vary ΔPgF; Start then varies them
  with the other variables. Tick Substitute on any glass the search may replace. Apply flags
  writes the ticks into the lens (one undo step of the Lens Data Editor).
- **Convert model glasses** replaces each model glass by the nearest catalog glass (by nd and
  Vd, OpticStudio's least-squares distance) within the template; optimize again afterwards.
- **Substitute glasses** tries every candidate glass on each Substitute surface, re-optimizing
  each time with the algorithm and cycles above, keeps the best, and repeats until a round
  changes nothing. The best design is written into the lens; the Lens Data Editor's undo takes
  it back. Stop keeps the best so far. Trials that fail are listed in the log.
- **Glass substitution template** (saved with the lens): the statuses allowed, maximum relative
  cost and CR, FR, SR, AR and PR codes (empty or 0 ignores a limit), whether glasses with
  incomplete data are excluded, and the catalogs to draw from. Glasses flagged Exclude
  Substitution in their catalog, tabulated materials and glasses that do not cover every
  wavelength are never chosen.
- **Operands.** RGLA (reasonable glass: distance from each glass of a surface range to the
  nearest catalog glass) and GCOS (glass cost) are in the Merit Function Editor.

Rules and readings are in docs/Optics-Conventions.md §41. The Hammer search (Global and Hammer
optimization below) also replaces Substitute glasses. Not built: a substitute catalog per surface, saving the template to a *.GST file, and the
operands MNIN, MXIN, MNAB, MXAB, MNPD, MXPD, GTCE and INDX.

## Global and Hammer optimization

The **Global and Hammer** section of the Optimize window looks for better design forms than
Start finds. Both run the algorithm and cycles chosen above for every trial, on the number of
threads above (the designs do not depend on it), and list the ten best designs found: rank,
merit, whether the exact (negative-weight) constraints are met, trial number and, for Hammer,
the glasses. The lens is not changed until you select a design and press **Apply design** (the
Lens Data Editor's undo takes it back). **Stop** keeps the designs found so far.

- **Start Global** begins from random starts within the ranges and evolves them (differential
  evolution); each start is optimized locally. Settings: generations (0 is a plain multi-start)
  and population (empty: twice the number of variables, at least 8). Global does not change
  glasses. As OpticStudio's help advises, begin from a rough design with the right number of
  surfaces, glasses, fields and wavelengths and roughly the right focal length (a curvature
  solve on the last surface holds it), and keep thicknesses physical with boundary operands.
- **Start Hammer** re-optimizes the lens, then repeatedly moves all variables a random step and
  re-optimizes, keeping each improvement. With glasses ticked Substitute in the Glasses section,
  a trial may instead replace one of them by a candidate within the Glass Substitution
  Template. Settings: rounds, trials per round, step (fraction of each variable's range), glass
  change probability.
- **Shared settings:** the seed (the same seed and settings give the same designs), the
  curvature limit (random curvatures lie within ± this; empty is 3/|EFL| of the lens) and a time
  limit in minutes.

The algorithms are this program's own (OpticStudio's are unpublished); rules and readings are in
docs/Optics-Conventions.md §43. Not built: OpticStudio's saved GLOPT and _HAMMER files and
Resume, Auto Update of open windows during a run, glass changes in the Global search, and saving
these settings with the lens.

## Interactive adjustment

Four buttons in the Optics panel open OpticStudio's manual adjustment tools. Each writes its
result into the lens through the Lens Data Editor, so the editor's Undo takes it back.

- **Quick Focus** adjusts the thickness of the surface before the image for the best RMS spot
  radius, spot x, spot y or wavefront error, averaged over the fields and wavelengths with their
  weights. *Use centroid* measures about the centroid instead of the chief ray (better when coma
  dominates). The status line gives the thickness before and after and the RMS before and after;
  the box below lists the assumptions.
- **Quick Adjust** adjusts one radius or thickness (*Adjust surface*, *Radius / thickness*) for
  the best RMS at a later surface (*Evaluate surface*; 0 is the image), about the centroid. The
  *angular* criteria measure the spread of ray directions after the evaluation surface (in
  milliradians): use them to collimate an afocal system, for example the spacing of a
  telescope. The dialog can stay open while you edit; each Adjust reads the lens afresh.
- **Slider** drags one value (radius, curvature, thickness, conic or a parameter of a surface)
  between a start and a stop while every open analysis window with auto-update on recomputes.
  The Lens Data Editor keeps the original until you press *Save* or *Keep and Exit*; *Exit*,
  closing the window or choosing another surface or parameter puts the original back.
  *Animate* steps through the range in a loop.
- **Visual Optimizer** gives up to eight sliders on the lens's variables (flag cells Variable
  first; radii appear as curvatures) and works like the Slider.

Both Quick tools sample the pupil with Gaussian quadrature (rings, 1 to 20, default 6) and need
every ray: a trial value where a ray fails is skipped, and a lens whose rays fail at the start is
refused with the reason. They refuse a cell that a solve, a pickup or the multi-configuration
table sets, and work in the current configuration. The analysis windows wait 250 ms after the
last slider move before computing, so a fast drag computes once, on the value where it stops.
Definitions are in docs/Optics-Conventions.md §44.

Not built: NSC Quick Focus (there is no non-sequential mode yet); the Slider's system,
configuration and NSC data (aperture, fields, wavelengths, temperature and the rest); updating
only one chosen analysis window; Animate in the Visual Optimizer; Quick Focus in afocal image
space (use Quick Adjust's angular criteria).

## Tolerance data

**Tolerance Data Editor…** in the Optics panel opens the tolerance table: what may go wrong in
manufacture and assembly, one operand per row, saved with the project and the lens file. Type an
operand's four-letter code (TRAD, TETX, …) or its name in the Operand column; the integer
columns then show what the operand reads (Surf, Adjust, First surf, Last surf, Roll surf, …),
and Min and Max are the extreme changes from nominal in the operand's unit. "Find an operand"
searches the codes and names. Insert, Delete, Undo and Redo work as in the other editors.
**Loosen 2x** and **Tighten 2x** double or halve every tolerance's limits as one undo step.
Each row has OpticStudio's two operand properties as Yes/No columns: Ignore, and Do not adjust
(during inverse tolerancing).

**Update** checks every row against the lens (surface numbers, codes, term ranges, Min not above
Max, the nesting of element groups) and fills the Nominal column with the value a row
tolerances, where that is one number of the lens. Problems are listed below the grid, each
naming its row.

Control rows act on the rows below them: TWAV sets the test wavelength of the fringe operands
(in its Min column, micrometers; without one the primary wavelength is used), MPVT the pivot of
element tilts (front vertex, rear vertex, front or rear mechanical edge, geometric center),
STAT and SEED the Monte Carlo statistics, COMP, CPAR and CMCO the compensators.

**TIRR irregularity model** picks how a TIRR row's fringes become sag. The default counts the
irregularity with power removed, as an ISO 10110 drawing or a test plate does; "OpticStudio's
TIRR formula" applies the help's formula as written, which gives terms 17/32 as large for the
same number of fringes.

Each operand's rule (how it changes the lens, which surfaces element tolerances insert) is in
docs/Optics-Conventions.md §36, and the readings where OpticStudio's help leaves a gap in §10
item 32. The tolerance run below uses them.

Not built: the coating, non-sequential, TEXI and ISO 10110 operands (defined, refused
when applied, as are TEZI on surfaces other than Toroidal); the default tolerance wizard, the
tolerance listing, sorting and .TOL files.

## Sensitivity and inverse sensitivity

The **Tolerance run** section at the bottom of the Tolerance Data Editor runs the rows of the table
on the project's lens (its current configuration), as OpticStudio's Tolerancing dialog does in its
Sensitivity, Inverse Limit and Inverse Increment modes.

- **Mode.** Sensitivity sets each tolerance to its Min, then its Max, alone, compensates, and
  reports the criterion and its change from the nominal. Inverse Limit tightens each limit until
  the criterion reaches the Limit; Inverse Increment until it changes by the Increment. A limit
  is never loosened, and a row marked Do not adjust is kept.
- **Criterion.** RMS spot radius, x or y (lens units), RMS wavefront (waves), the merit function,
  the boresight error (radians), the Strehl ratio, the geometric or diffraction MTF (tangential,
  sagittal or their average) at **MTF frequency** (cycles/mm), or the diffraction or geometric
  encircled energy within **Encircled radius** (µm) of the centroid. Spot and wavefront are about
  the centroid over every wavelength, at the field points chosen under Fields (Y-Symmetric,
  XY-Symmetric or the system's own); Sampling is the rays along each arm of the Gaussian
  quadrature (twice as many arms). **Pupil grid** (16 to 1024 per side) applies to the Strehl
  ratio, the MTF and the encircled energy. For these three larger is better: a tolerance's
  degradation is the decrease, the Limit lies below the nominal, and the Increment is a
  decrease. The MTF frequency and the radius have no default; a run asks for them.
- **Change.** Linear difference (P − N) or RSS difference.
- **Compensators.** Paraxial focus holds the nominal lens's defocus at the image; Optimize (DLS
  or OD) drives the COMP, CPAR and CMCO rows with the local optimizer (all of them, or the rows
  listed under Compensator rows; Optimizer cycles 0 is automatic); None leaves the lens as
  perturbed.
- **Run**, **Quick Sensitivity** (Sensitivity with paraxial focus, for the spot, wavefront and
  boresight criteria) and **Cancel**. The table lists every row's limits, criterion and change
  at both ends, and the report below it the nominal, the worst offenders, the RSS estimated
  change and performance, the notes (rows that could not be evaluated, limits that were
  tightened) and the assumptions. **Apply inverse limits** writes an inverse run's tightened
  limits into the table as one undo step.
- The settings are saved with the tolerance data.

The definitions are in docs/Optics-Conventions.md §42, and the readings where OpticStudio's help
is short in §10 item 38.

Not built: ensquared, x-only and y-only enclosed energy as criteria, RMS angular and user
scripts;
Separate Fields/Configs, the polynomial fit and cache, Force Ray Aiming and the "All"
configurations choice; the tolerance data file and viewer.

Decenter and tilt tolerances (TSDX … TIRY, TEDX … TETZ, the rolls, TUDX … TUTZ) are evaluated: a
trial lens they make non-axial is traced as it is, with the entrance pupil, fields, paraxial
focus and focal length of the same trial with its decenters and tilts at zero, so they show as
aberration and boresight. A lens whose solves need paraxial data (marginal- or chief-ray solves)
still fails such trials, as do merit operands that read paraxial data and the Optimize
compensator with the merit function criterion (Conventions §42).

## Monte Carlo tolerancing

The **Monte Carlo** section of the Tolerance Data Editor simulates production, as OpticStudio's
Monte Carlo analysis does: each trial draws every tolerance row at random from its distribution,
applies them all at once to the project's lens (its current configuration), compensates and
evaluates the criterion. It uses the criterion, compensator, field, sampling and pupil grid
settings of the Tolerance run section.

- **Trials** (default 20, OpticStudio's default) and **Seed**. Trial k depends only on the seed
  and k, so a run repeats exactly, and one trial run alone, in a run on one thread or in a run on
  many is the same lens. Seed 0 draws a seed from the clock and the report names it. A SEED row
  gives the rows below it a seed of their own (SEED 0: the run's seed).
- **Statistics** for rows no STAT row governs: normal (default), uniform or parabolic, and **Std
  devs to extremes** for the normal (default 2: the tolerance range is ±2σ, and draws beyond it
  are drawn again). A STAT row sets the statistics of the rows below it: 0 normal with Int2
  standard deviations (1 to 10), 1 uniform, 2 parabolic (more draws near the extremes), 3 user
  defined, read from the .UDD file named in the STAT row's comment, in **Distribution folder**.
  A .UDD file is two columns "X T", X from 0.0 to 1.0, T the probability at X; the range Min to
  Max is mapped onto X.
- Radial operands (TEDR, TSDR, TARR, TRLR) draw a random direction too, and TIRR a random
  astigmatism angle. A radial operand with a zero limit is drawn about zero (normal, parabolic).
- **Yield limit** (empty for none): the share of evaluated trials whose criterion is at or below
  the limit (at or above it for the Strehl ratio, MTF and encircled energy). **Percentiles** lists
  the percentiles reported.
- **Run Monte Carlo**, **Stop** and the progress line. The statistics table gives the nominal, the
  number of trials evaluated and failed, the mean, standard deviation (n − 1), min, max, the
  percentiles, the best and worst trials, the yield and the seed. Beside it: a histogram of the
  criterion (**Histogram bins**) and, with **Overlay MTF** on, the FFT MTF of the nominal (dashed)
  and of the first **Overlay trials** trials at **Overlay field**, up to **Overlay max frequency**
  (cycles/mm). The trials table lists each trial's criterion, change, compensators and, for a
  failed trial, why. The report below repeats it all with the notes and assumptions.
- **Save trial…** writes the trial named in **Trial to save** as a lens file (default name
  MC_T0001 and so on, as OpticStudio names its Monte Carlo files): the perturbed, compensated
  lens. With paraxial focus the focus shift is written into the thickness before the image; that
  is refused for a multi-configuration lens or a thickness set by a solve or pickup.
- Failed trials are listed with their reason and left out of the statistics and the yield, with
  their count shown. Trials carrying decenters and tilts are traced as the sensitivity analysis
  traces them (see above); a ray that misses fails the trial.
- The settings are saved with the tolerance data.

The definitions are in docs/Optics-Conventions.md §45, and the readings where OpticStudio's help
is short in §10 item 41.

Not built: TEZI's random per-term coefficients (a trial applies the drawn RMS equally over the
terms), correlated draws, Save Best and Worst files by name, tolerance scripts, the Tolerance
Data Viewer, the yield curve and the ZTD data file; rows whose operand cannot be applied in this
version (coatings, TEXI, ISO 10110) are left out of every trial and named in the report.

## Thermal analysis

The **Thermal** section of the Multi-Configuration Editor sets a lens up at several temperatures,
as OpticStudio's Make Thermal does.

- **Make thermal.** Enter the number of new configurations and the minimum and maximum
  temperature (°C); the new configurations span the range in equal steps. Off, "Use existing
  configurations as nominal" replaces the table by one nominal configuration (the current one)
  plus the new ones; on, each existing configuration gets its own set after the originals.
  Pressure (atm) is the new configurations' air pressure; empty keeps the nominal's. The tool
  adds TEMP and PRES rows and, for each radius, thickness and fixed semi-diameter, a row whose
  new cells are thermal pickups of the nominal. The status line lists what got no row (a value
  under a Lens Data Editor solve, for instance).
- **Thermal pickups** (badge "T"): the reference configuration's value at this configuration's
  temperature. Glass radii, thicknesses and semi-diameters grow with the glass's catalog TCE;
  the air spaces grow with the TCE column of the surface before them (the mount), measured along
  the lens edges at the mechanical semi-diameters, so a change of radius moves the spacing too.
  A surface that is not a catalog glass (a constant index, a model glass) uses its TCE column.
  Other operands copy the reference value. Docs/Optics-Conventions.md section 37 gives the rules.
- **Indices.** Turn on "Adjust index to environment" in the system data for the glass indices to
  follow each configuration's temperature and pressure; Make Thermal reminds you when it is off.
- **Glass and mechanical material.** Link a glass to a material of the material library (Glass,
  Mechanical material, Link): that glass then expands with the material's thermal expansion
  coefficient instead of its catalog TCE. The link saves with the lens. The material library's
  materials have no expansion coefficient yet; a linked material without one is refused by name.
- **Not built.** Thermal pickups of parameters, several temperatures within one configuration,
  thermal merit operands and athermal optimization; the merit function cannot yet evaluate a
  thermal table at a changed temperature.

## Command line

The `opensim` command line (`OpenSim.Cli`) runs the optics of a saved project without the app.
Each subcommand writes a results folder as the other commands do (`report.md`, `report.html`, CSV
tables, `log.txt`); `opensim help` lists every option. Exit codes: 0 done, 1 a usage error (or
a part not built), 2 the run failed or was cancelled.

- `opensim optics list [--analysis ID]` lists the analyses by their saved ids; with an id, its
  settings and their defaults.
- `opensim optics analyse <project> --analysis ID[,ID...]` computes analyses on the saved lens
  (its current configuration) with the settings the project saved for each, which is what a new
  analysis window opens with. `--settings FILE` (JSON, as the project file stores settings) and
  `--set "Name=value;..."` override them; a setting the analysis does not have, or a value of the
  wrong kind, is refused by name. The folder holds the composite report, which is byte for byte
  what **Save report** writes for the same windows in the app, each analysis's text
  (`<ID>.txt`, the window's Text tab) and each plot's data (`<ID>-plot<n>.csv`).
- `opensim optics trace <project> --hx --hy --px --py --wavelength --global` is the Single ray
  trace analysis with those options.
- `opensim optics optimise <project>` runs the local optimizer (Optimization above) on the saved
  variables and merit function: `--algorithm dls|od`, `--cycles auto|N|infinite`,
  `--max-evaluations`, `--threads`. It writes the report, `variables.csv`, `merit.csv` and the
  optimised project as `<project>-optimised.ossproj` in the results folder; the input file is not
  changed.
- `opensim optics optimise <project> --global` or `--hammer` runs the Global or Hammer search
  (Global and Hammer optimization below) instead: `--seed`, `--population`, `--generations`
  (Global), `--rounds`, `--trials` per round, `--step`, `--glass-probability` (Hammer),
  `--curvature-limit`, `--max-evaluations` per trial, `--save`, `--time-limit` in seconds and
  `--threads`; what you leave out takes the search's own default, as the Optimize window has
  it. The same seed and settings give the same designs. The folder holds the report,
  `designs.csv` (the best designs, best first) and the best design as
  `<project>-optimised.ossproj`. Ctrl+C keeps the designs found so far (exit 2).
- `opensim optics tolerance <project>` runs the saved tolerance rows with the saved run settings
  (Sensitivity and inverse sensitivity above); `--mode sensitivity|inverse-limit|inverse-increment|quick`,
  `--criterion`, `--compensation`, `--limit`, `--increment` and the others override them.
  `tolerance.txt` is the editor's report text. A row that could not be evaluated gives exit 2.
- `opensim optics tolerance <project> --montecarlo` (or `--mode montecarlo`) runs Monte Carlo
  tolerancing (Monte Carlo tolerancing below) with the Monte Carlo settings saved in the Tolerance
  Data Editor; `--trials`, `--seed` (0 draws one from the clock) and `--yield-limit` override
  them, and the criterion and compensation options apply as above. `montecarlo.txt` is the
  editor's report text; `trials.csv` lists every trial with its drawn values; `histogram.csv`
  holds the histogram. `--save-trial K` also writes trial K's lens as `MC_T000K.osslens`. A
  failed trial is named in the log and the report and gives exit 2.
- `opensim optics tolerance <project> --quick-yield` runs Quick Yield with the saved Monte Carlo
  and Quick Yield settings (`--samples`, `--seed`, `--yield-limit` override them) and writes
  `quickyield.txt` (the editor's report text), `histogram.csv` and `yield.csv`; a failed
  evaluation gives exit 2. `--wizard` (with any tolerance run) first writes the Tolerance Wizard's
  rows from its saved settings and runs them; the project is not changed.
- `opensim optics tolerance <project> --script FILE.TSC` runs the sensitivity (or, with
  `--montecarlo`, the Monte Carlo) analysis with the User script criterion and the tolerance script
  read from FILE.TSC (see Tolerance scripts); `--merit-folder DIR` and `--script-out DIR` set where
  LOADMERIT reads and OPENFILE and SAVE write (default: the saved folders, else the script's).
- `opensim optics optimise <project> --high-yield W` adds the High-Yield rows at weight W to the
  merit function (from the saved Optimization Wizard options) before optimizing; the written
  project carries them.
- Optics sweeps: an `opensim sweep` job whose targets are all `optics:...` sets cells of the saved
  lens at each point and tabulates the saved merit function and each row's value. Targets:
  `optics:surface<n>.radius|curvature|thickness|conic|semiDiameter|tce|index|par<k>|modelNd|modelVd|modelDeltaPgF`
  (surface 0 is the object), `optics:system.apertureValue|temperature|pressure`,
  `optics:wavelength<n>[.weight]` and `optics:field<n>.x|y|weight`. A cell driven by a solve or
  pickup, a surface that does not exist and a property not listed are refused, naming the target.
- `opensim optics import <file.zmx|file.zar|file.seq|file.len>` reads a Zemax, CODE V or OSLO
  lens file (by its extension) into a lens file (`--out`, default the input's name with
  `.osslens`), and `opensim optics export
  <project|lens.osslens>` writes a project's or lens file's system as a `.zmx` (`--out`). Content
  the subset does not hold fails the command (exit 2) with every item listed; `--lenient` lists
  it and goes on (Zemax lens files below).
- `opensim optics export <project|lens.osslens> --format step|iges|stl|dxf` (or a CAD `--out`
  name) writes the lens as a CAD file instead (CAD export below).
- `opensim optics catalog <folder|catalog.osslcat>` indexes a folder of Zemax lens files as a
  stock lens catalog (or loads a private one), prints the Catalog Report of the lenses meeting
  `--efl MIN:MAX`, `--epd MIN:MAX`, `--shape`, `--type`, `--elements` and `--text`, and with
  `--save OUT.osslcat` writes the private catalog (Stock lens catalogs below).
- `opensim optics macro <macro.zpl> [--lens FILE]` runs a ZPL macro on the lens of a project,
  lens file or .zmx and prints its output (ZPL macros below); `--arg NAME=VALUE,...` sets what
  `$GETARG` reads, `--out` writes the output to a file, `--save-lens` the changed lens.
- `opensim optics convert <project> --tool asphere|best-asphere|stock` runs Convert Asphere
  Types, Find Best Asphere or Stock Lens Matching and writes the report and the project with the
  result (Design conversion tools below).
- `opensim optics efficiency --period UM --wavelength UM [options]` prints a grating's efficiency
  per order by RCWA or the scalar theory, without a project (Diffraction efficiency below).
- Glass catalogs: the app's (the built-in set and your catalog folder); `--builtin-catalogs` uses
  the built-in set alone, for a run that does not depend on the machine.
- Ctrl+C stops a run between steps. A stopped optimization still writes the best design so far.

Not built: variable ranges for the Global and Hammer searches from the command line;
multi-configuration cells as sweep targets; lens files (`.osslens`) as input. The definitions
are in docs/Optics-Conventions.md §46.

## Scripting

The optics model can be driven from a script outside the program: JGraph (MATLAB-style) or
Python through pythonnet load OpenSim's libraries and call them directly. A script can load a
saved project's lens or build one, write the default merit function, run the local optimizer,
run any analysis by its id and read its plots and text, trace one ray (with OpticStudio's RAYE and
RAYV error codes), run the Global and Hammer searches, and run the tolerancer and Monte Carlo
tolerancing. docs/Scripting-from-JGraph.md lists the calls and how
the interop wants them; `examples/jgraph/optics_from_jgraph.m` builds a singlet, optimises it and
plots its MTF. A script works on its own copy of the lens: to see the result in the program, save
it into a project (`project.Optics = …`, then the project serializer's Save) and open that.

Not built: plug-ins. The global searches and Monte Carlo were not tried under JGraph itself;
their calls are checked by the test suite. A ZPL macro can be run from a script with
`ZplMacro.Run` (ZPL macros below).

## Test plate fitting

Fabricators publish lists of the test plates they own; fitting a design's radii to one list
avoids paying for new plates. **Test Plate Fitting…** in the optics panel opens a dialog:

- **Test plate list**: a list file in OpticStudio's .TPD layout (header lines starting with "!",
  then `partname radius diameter code` per line, millimeters, code −1 concave only, 0 both,
  1 convex only). Lists come from fabricators; none ships with the program. **Show List** shows
  the list with its CC and CX columns; a file that does not follow the layout is refused with
  every bad line named.
- **Method of fit**: Try All Methods (the default; keeps the order with the lowest merit), Best
  To Worst, Worst To Best, Long To Short, Short To Long.
- **Automatic cycles** / **Optimization cycles**: how long each optimization runs.
- **Test wavelength**: the wavelength the report's fringes are given at (0.6328 µm by default).

Only radii flagged Variable are fitted; flag thicknesses or other compensators Variable too,
since the merit function is re-optimized after each radius. **Fit** optimizes the lens first,
then fits one radius at a time: the plate must have the right shape (a convex surface needs a
concave plate), be at least 3/4 of the clear aperture in diameter, and have an edge sag within
0.2 mm of the surface's; the closest such plate in fringes is used, the radius loses its Variable
flag and the rest are re-optimized. The report lists the vendor, the order of fitting, each
surface's plate, the radius change and the merit after each step. A radius with no suitable plate
is reported "NO MATCH FOUND!" with the reason and stays variable: it is a candidate for a custom
plate. The fitted design goes into the Lens Data Editor as one edit, which its Undo takes back;
**Stop** ends a fit without changing the lens.

Not built: fitting multi-configuration CRVT cells; an installed folder of lists; the ZPL keyword
TESTPLATEFIT and a command-line arm. The choices where the help is silent (which shape the list's
code names, the fringe wavelength) are listed in docs/Optics-Conventions.md §10 item 44.

## Stray light and PAL/freeform analyses

Four analyses under **Stray light** and **PAL/Freeform** in the analysis list, on the project's
lens in its current configuration.

- **Ghost focus generator** (OpticStudio's Ghost Focus Generator). Settings: Bounces (double or
  single), First and Last Surface (0 = the image), Image Surface Only, Ghost Reflector Coating
  (e.g. I.99 for 1 % on every bounce; blank for the surfaces' own coatings or bare Fresnel),
  Wavelength and the pupil grid of the real rays. Each ghost (every pair of surfaces that change
  the index, plus the image surface) is built as its own lens with the bounce surfaces made
  mirrors. The table lists per ghost: the two reflectances, the relative intensity R1·R2, the
  same with the other surfaces' transmission, the ghost focus distance from the image surface,
  the pupil ghost size on the image, the ghost EFL and where the ghost images the pupil. Below,
  each ghost's surfaces: marginal and chief heights, F/#, real RMS spot radius on axis and possible
  internal focus. A scatter plot shows intensity against focus distance. A ghost that cannot be
  built or traced is listed with the reason; failed rays are counted by surface.
- **YNI contributions**: y·n·i of the paraxial marginal ray at each surface (related to
  narcissus in cooled infrared systems) and their sum.
- **Power pupil map** and **Power field map**: the local optical power (diopters) or EFL along
  the rays across the pupil of one field, or along the chief ray across the field: spherical,
  cylinder, maximum, minimum, tangential, sagittal, X, Y or astigmatic, with an offset, as a false
  colour or contour map. Set Surface to the last surface with power, as the help advises.

Merit operands: GPIM (ghost pupil and image ghost distance, ghost pupil magnification), GPRT,
GPRX, GPRY, GPSX, GPSY, GAOI and YNIP. Definitions in docs/Optics-Conventions.md §49; readings
where the help is short in §10 item 45.

Not built: Biocular Field of View and Dipvergence/Convergence, saving the ghost lens files,
ghosts of tilted or decentered lenses, GPIM's worst-pair columns, GAOI with vignetting factors,
coated surfaces in GPRT.

## High-yield optimization

The **High Yield** section of the Optimize window steers a design toward one that is less
sensitive to manufacturing errors, as OpticStudio's "Improve Manufacturing Yield" does. It adds
HYLD rows to the merit function: at every refracting surface, for every ray of the default merit
function, the penalty |n − n′|(1 − cos θ), θ the larger of the ray's angles of incidence and
exit. Large ray angles make a design's aberrations depend delicately on each surface, so lowering
them lowers the tolerance sensitivity; the optimizer weighs this against the image quality.

- **Weight** (default 1): the weight of the HYLD rows relative to the default merit function's
  rows. A larger weight gives lower ray angles and worse nominal performance; too large a weight
  drives the design toward rays normal to every surface, which no longer focuses. Saved with the
  lens.
- **Add High-Yield rows** writes the rows at the end of the merit function, after a BLNK row
  whose comment starts "High-Yield:", using the Optimization Wizard's saved settings (criterion,
  rings and arms or grid, symmetry, configurations). Adding again replaces them; **Remove** takes
  them out; the Merit Function Editor's undo takes either back. Mirrors get no rows; rays that
  fail are left out, and the log says so. Add the default merit function first, then the
  High-Yield rows, then optimize with **Start** (or Global and Hammer) as usual.
- **Evaluate** lists, surface by surface, the mean and largest penalty and the rows' share of the
  merit function.
- The HYLD operand can also be typed into the Merit Function Editor: Surf, Wave, Hx, Hy, Px, Py.

The definition and the sources are in docs/Optics-Conventions.md §50, and the readings where
OpticStudio's documentation is short in §10 item 46.

Not built: the "Improve Manufacturing Yield" choice inside the Optimization Wizard dialog itself,
the TOLR operand (a tolerance estimate inside the merit function) and HYLD at mirrors. From the
command line: `opensim optics optimise <project> --high-yield W`.

## Tolerance wizard, summary, data viewer and Quick Yield

Four more sections of the Tolerance Data Editor, after OpticStudio's tools of the same names.

- **Tolerance Wizard**: default tolerance rows for the project's lens. Tick the tolerances wanted
  and give their values: radius (lens units, percent of the radius, or fringes; a plano surface
  always takes fringes), thickness, surface decenter X/Y, surface tilt X/Y (lens units of total
  indicator runout, or degrees), S + A irregularity (fringes), element decenter X/Y and tilt X/Y
  (degrees), index and Abbe (percent of the glass's Abbe number). **Start at row** 1 replaces
  the rows, n keeps rows 1 to n − 1, −1 appends; **Start/Stop at surface** limit the surfaces
  (Stop 0: the surface before the image); **Use focus compensation** adds a compensator on the
  thickness before the image, with the range in **Focus compensator ±** (empty: none, and the
  status says so). **Apply wizard** writes the rows as one undo step and lists the surfaces it
  passed over and why; **Reset** restores the defaults, the values OpticStudio's help shows in its
  picture of the dialog (0.2 lens units, 0.2 fringes, 0.2°, index 0.001, Abbe 1 %, test
  wavelength 0.633 µm). The settings are saved with the tolerance data.
- **Tolerance Summary**: the rows in words for a fabricator (what, where, nominal, tolerance,
  unit), the compensators and the control settings, with no operand codes. **Save summary…**
  writes Markdown, HTML or text.
- **Quick Yield**: a predicted Monte Carlo yield at a fraction of the cost. It evaluates each row
  at the two ends of its Monte Carlo range and, with **Cross terms**, each pair of rows together,
  compensating each lens as Monte Carlo does, fits a quadratic model of the criterion, and samples
  that model **Samples** times with the Monte Carlo statistics, seed and yield limit (sample k
  draws what Monte Carlo trial k draws). The status gives the predicted yield with the sampling
  error of the samples; the plots show the histogram and yield curve, with the last Monte Carlo
  run's beside them. It is this program's second-order estimate, not OpticStudio's method (which
  is proprietary), and is wrong where the response is far from quadratic (clipping, compensators
  at their limits).
- **Data viewer…** opens the Tolerance Data Viewer on the last runs: the Monte Carlo trials as a
  table (criterion, change, compensators, every drawn tolerance), **Statistics** of every column,
  **Save CSV…**; the Histogram (bin count, min and max value, values outside counted in one bar)
  and the Yield curve of any column, the criterion's with the Quick Yield curve dashed; the last
  sensitivity rows and report; the last Quick Yield report.

Not built: OpticStudio's vendor and grade presets (the help gives no numbers), the wizard's
Zernike irregularity, named wizard settings files, the .ZTD data file, Quick Yield's precision
levels and own field and sampling controls. The definitions are in docs/Optics-Conventions.md
§51, the readings in §10 item 47.

## Zemax lens files

**Import .zmx…** and **Export .zmx…** sit under **Save the lens** and **Open a lens** in the
system data. Import replaces the workspace's optical system with a Zemax lens file (.zmx), or
with the lens inside a Zemax archive (.zar); export writes the system as a .zmx (UTF-16, as
OpticStudio writes them).

What is read: the title and notes, the lens unit, the aperture, fields with their vignetting,
wavelengths, temperature and pressure, the glass catalog list, and per surface the type, comment,
stop, radius, thickness, conic, glass, coating name, semi-diameter, parameters and extra data,
circular and floating apertures and their decenter. Surface types: Standard, Even, Odd, Extended,
Extended Odd and Q-Type aspheres, Toroidal, Paraxial and Coordinate Break, and the freeform types
Polynomial, Extended Polynomial, Zernike Fringe, Standard and Annular Standard Sag, Off-Axis Conic
Freeform and Radial NURBS, and the diffractive types Diffraction Grating, Toroidal Grating,
Variable Line Space Grating, Binary Optic 1 and 2, Zernike Fringe, Standard and Annular Phase,
Hologram 1 and 2 and Toroidal Hologram, and the crystal types Birefringent In and Out (the
freeform, diffractive and crystal codes are taken from OpticStudio's macro help and have not been
checked against a file OpticStudio wrote; a Grid Sag is refused, as its grid is a separate file).
A Birefringent In's GLAS line holds its crystal glass, such as CALCITE; the extraordinary glass
CALCITE-E is not written and must be in a loaded catalog when the file is read.
Solves: curvature,
thickness, material, semi-diameter and parameter pickups (scale only, same column), and curvature,
thickness and parameter variables. Glass names are checked against your loaded catalogs, in the
file's catalog order; a model glass and a constant index are read as such. An archive's own glass
catalogs are used for the check; copy them into your catalog folder (Materials) to trace the lens.

Anything else in the file stops the import, and the log lists every item with its line, surface
and keyword: another surface type or solve, merit-function rows, tolerance rows, more than one
configuration, an unknown keyword, a glass not in your catalogs. Tick **List unsupported
lens-file content in the log instead of stopping** to read the rest and have those items listed. Display
and program settings (drawing, physical optics, ray aiming, polarization and the like) are never
read; the log names them. Export works the same way the other round: what a .zmx written here
cannot hold (other surface types, composite add-ons, surface tilts and decenters, other apertures, the merit
function, tolerances, configurations, settings away from their defaults) stops it, or with the
box ticked is listed and left out. A lens exported and imported again comes back the same.

From the command line: `opensim optics import <file.zmx|file.zar> [--out lens.osslens]
[--lenient]` and `opensim optics export <project|lens.osslens> [--out lens.zmx] [--lenient]`.

Not built: merit-function, tolerance and multi-configuration data; surface types and solves not
listed above; surface tilts and decenters; rectangular, elliptical and user apertures; ray aiming
and polarization; writing archives; the .zos format; Grid Sag surfaces. CODE V and OSLO files are imported as
described in the next section. Files written here
have not been opened in OpticStudio. The details are in docs/Optics-Conventions.md §52.

## Stock lens catalogs and design templates

**Lens Catalogs…** in the optics panel opens a dialog with three tabs.

*Stock lenses.* The library folder holds your catalogs: each private catalog file (`.osslcat`)
in it, and each sub-folder of vendor `.zmx`/`.zar` files, or CODE V `.seq` and OSLO `.len` files
(one sub-folder per vendor, named after it). No vendor catalog comes with the program; download
the vendors' lens files yourself. Files are read as **Import .zmx** and **Import CODE V .seq /
OSLO .len…** read them; a file that cannot be read is left out and named in the log, with the
reason. Choose a vendor or all catalogs and set the criteria as OpticStudio's Lens
Catalogs tool has them: focal length and entrance pupil diameter ranges in millimeters (each used
when ticked), the shapes (Equi-, Bi-, Plano-, Meniscus) and types (Spherical, GRIN, Aspheric,
Toroidal) to accept (none ticked accepts all), the number of elements, and text the part name
contains. **Set ±5%** takes the ranges from surfaces of the current lens (their focal length in
air and diameter, plus and minus 5 %). **Search** lists part name, focal length, diameter and the
code (shape, type, elements), e.g. (P,S,1); **Catalog Report** lists the lenses by name, by focal
length and by diameter. **Load** replaces the current lens with the selected one; **Insert** puts
its surfaces before the surface you give, as one step the Lens Data Editor's Undo takes back. The
file's object thickness becomes a dummy surface unless **Ignore object** is ticked; in mirror
space the inserted radii and thicknesses change sign unless you clear that box; **Reverse the
lens** turns it round. Your lens's stop and every surface number in it (pickups, solves,
configuration and merit rows) follow their surfaces. A lens in inches is converted to your lens
unit.

The part name is the first word of the file name ("Lens number 5.zmx" is "Lens"), as in
OpticStudio; name files like "LA1951.zmx". Focal lengths and diameters are the paraxial values of
each file's own system at its primary wavelength.

*Make private catalog.* Converts a folder of `.zmx`/`.zar`/`.seq`/`.len` files into one `.osslcat` file in the
library folder, with every lens file inside it, so it works after the folder is gone.

*Design templates.* Six starting designs worked out from first-order formulas: plano and equi
singlets that meet a focal length exactly, a cemented achromat (two catalog glasses, colour
corrected between F and C in the thin-lens limit, scaled to the focal length), a parabolic mirror,
and Keplerian and Galilean telescopes built from ideal lenses. Filter them by text, edit the inputs
(focal length, pupil, field, thickness, glass name or a constant index) and **Load** replaces the
current lens.

From the command line: `opensim optics catalog <folder|catalog.osslcat> [--efl MIN:MAX]
[--epd MIN:MAX] [--shape EBPM?] [--type SGAT] [--elements N] [--text T] [--save OUT.osslcat]`
prints the files left out and the Catalog Report.

Not built: OpticStudio's own `.ZMF` catalog files (convert the vendor's `.zmx` files instead);
non-sequential insertion; the dialog's Prescription and Layout buttons; GRIN lenses; OpticStudio's
hundreds of design templates. Nothing has been compared with OpticStudio's own Lens Catalogs. The
details are in docs/Optics-Conventions.md §53.

## Freeform and data-defined surfaces

The type column of the Lens Data Editor offers eight more surface types, each with its
OpticStudio parameter columns (docs/Optics-Conventions.md §55 lists them):

- **Polynomial:** z = γ1x² + γ2x⁴ + … + γ5y² + … + γ8y⁸ in parameters 1–8; the radius and conic
  are not used and must stay plane and zero.
- **Extended Polynomial:** the surface's conic plus up to 230 terms x, y, x², xy, y², … on x/R_n,
  y/R_n (maximum term number, normalization radius, then the coefficients in lens units).
- **Zernike Fringe Sag, Zernike Standard Sag, Zernike Annular Standard Sag:** the Even Asphere
  plus 37, 231 or 80 Zernike terms (coefficients in lens units), with the extrapolate flag; the
  Fringe and Standard forms take a Zernike decenter, the annular form an obscuration inside which
  the terms are ignored.
- **Off-Axis Conic Freeform:** the off-axis part of the surface's conic at the offset Y0, in a
  frame tangent to it, plus up to 230 freeform terms. With Y0 = 0 it is the Standard surface.
- **Grid Sag:** the Even Asphere plus Zernike Standard terms plus a sag interpolated from a grid
  file (OpticStudio's .DAT layout), bicubic (parameter 0 = 0) or linear (1). A ray outside the
  grid is a ray miss. The grid is saved with the lens in its file's unit; Scale Lens scales it.
  There is no import button yet: a grid enters a lens from a saved lens or from code
  (`GridSagFile.Read`), and a Grid Sag surface without a grid is refused by name.
- **Radial NURBS:** a rational B-spline curve from the vertex through 4 to 60 control points
  (y, z, weight each), turned about the axis; no radius or conic. Beyond the last point's y is a
  ray miss. The degree (cubic) and knots (clamped, uniform) are this program's reading.

Each gives the paraxial engine its y–z curvature at the vertex; a surface whose vertex is off
z = 0, sloped or twisted (a Zernike piston or tilt, a linear polynomial term, a decentered
Zernike set, a NURBS curve starting off z = 0) has no paraxial model and paraxial data are
refused with the cause named.

Not built: Chebyshev Polynomial in the editor (the shape is built in code; its coefficient
columns are not given by its help page), Toroidal NURBS, Q-Type Freeform (its source paper is
not on this machine), TrueFreeForm and Make TrueFreeForm, Grid Point Selector, Export Point
Cloud, a Grid Sag import button, optimizing grid points, Grid Sag in .zmx files, and these types
in CODE V and OSLO files. The other freeform types are read and written in .zmx files (see Zemax
lens files above); a macro sets their parameters with SURP like any other type. The Off-Axis Conic Freeform sign and the NURBS degree are readings not checked against
OpticStudio (docs/Optics-Conventions.md §10 item 51).

## CODE V and OSLO lens files

**Import CODE V .seq / OSLO .len…** sits under the Zemax buttons in the system data. It replaces
the workspace's optical system with a CODE V sequence file (.seq) or an OSLO lens file (.len),
chosen by the extension. Nothing is written in either format.

What is read: the title (and OSLO's notes), the lens unit, the aperture, the fields with CODE V's
vignetting factors or OSLO's field table, the wavelengths, and per surface the radius or
curvature, thickness, glass or mirror, conic, even aspheric terms (an Even Asphere surface), the
stop, a comment, apertures, and surface tilts and decenters. CODE V's and OSLO's tilts about x
and y turn the other way from this program's, so they are saved with the opposite sign; their
decenter kinds become this program's tilt "before" and "after" a surface (decenter and return,
bend, reverse; OSLO's return and bend). A focus shift written on the image surface is added to
the last thickness. Glass names are looked up in your loaded catalogs, with or without the
hyphen CODE V files drop ("NBK7" finds N-BK7); CODE V's fictitious glasses (1.62:36.4 or
620.364) become model glasses, and an OSLO glass given by one index a constant index. OSLO's
pickups are read as the values they give. Solves and variable codes keep the value the file
writes.

Anything else stops the import and the log lists every item with its line, surface and command:
zoom positions or configurations, private glass catalogs, toroids and other surface types,
diffractive surfaces, z decenters, tilt pivots, a glass not in your catalogs, an unknown
command. Tick **List unsupported lens-file content in the log instead of stopping** to read the
rest. Settings that do not change the lens (designer initials, drawing data, CODE V's GO and
DER) are never read; the log names them.

From the command line: `opensim optics import <file.seq|file.len> [--out lens.osslens]
[--lenient]`.

Not built: writing CODE V or OSLO files; multi-configuration data; private and tabulated glasses;
surface types other than Standard and Even Asphere; OSLO's general aspheres, perfect lens and
gratings; solves kept as solves; temperature and pressure from the file. Tilted files have not
been compared with CODE V or OSLO themselves; a CODE V double Gauss reproduces the focal length
CODE V lists for it. The details are in docs/Optics-Conventions.md §54.

## ZPL macros

**ZPL Macros…** in the optics panel opens the macro window, which runs macros written in
OpticStudio's ZPL language on the lens. Choose the **Macro folder** (it holds the macros that
CALLMACRO calls and the ZPLM operand runs, and is where OUTPUT and OPEN look for a file name given
without a folder); its macros are listed under **Macro list** (**Edit** opens one). **Open…** and
**Save As…** read and write .zpl files.

- **Run** runs the macro on a copy of the lens; its PRINT output appears below the editor as it
  runs. When it finishes, its changes are written to the lens: if it changed only surfaces, as one
  edit the Lens Data Editor's Undo takes back; if it changed anything else (system data, the merit
  function, …), the whole system is reloaded, which cannot be undone. **Stop** cancels a run and
  nothing is applied; a macro that fails applies nothing either, and the status line names the
  macro, the line and the problem.
- **Check** lists the keywords and functions the macro uses that this version does not bind, with
  their lines. A macro runs until it reaches one; then it stops, naming it.
- INPUT asks for a value in a small dialog; PAUSE shows its message and waits for OK.

What runs: the whole language (variables, strings, arrays, expressions, IF, FOR, GOTO, GOSUB,
PRINT and FORMAT, files, CALLMACRO), and the keywords and functions bound to the program's
engines — ray tracing (RAYTRACE and RAYX … RAYO), the lens data (SETSURFACEPROPERTY and SPRO, the
surface functions, INSERT, DELETE, STOPSURF, SOLVETYPE, SETVAR, COMPOSITEON, COMPOSITEOFF), the system data
(SETSYSTEMPROPERTY and SYPR, the field and wavelength functions), the merit function (SETOPERAND,
MFCN, OPER, OPEV), OPTIMIZE and QUICKFOCUS, analyses to text files (GETTEXTFILE), lens files
(LOADLENS, SAVELENS), physical optics and beam files (POP, ZBFREAD, ZBFWRITE, ZBFPROPERTIES,
ZBFCLR, ZBFMULT, ZBFTILT, the coherent ZBFSUM), the geometric image analysis (IMA, IMAE,
IMASUM), single-mode fiber coupling (FICL), CAD export (EXPORTCAD: IGES, STEP, STL) and the saved
non-sequential objects (NOBJ, NPAR, NPOS, NPRO, OBJC, SETNSCPARAMETER, SETNSCPOSITION,
SETNSCPROPERTY; surface 1). docs/Optics-ZPL-Library.md lists every keyword and function of the ZPL help
with what each does here or why it is not bound. Lengths are in lens units; the lens-data
functions show solved values, as the editor does; a change made by a keyword takes effect at once
(UPDATE is accepted and checks the lens builds). POP and IMA run their analysis with its
last-used settings; beam files given without a folder are in the macro folder, as are IMA and BIM
files.

**In the merit function**, the ZPLM operand runs macro ZPLnn.ZPL (nn = Mac#, from the macro
folder) on a copy of the lens and takes the value it returns with OPTRETURN in data field Int2;
Data1 to Data6 reach the macro as PVHX … PVEY. **As a solve**, `SOLVETYPE surf, TZ, name.zpl` (CZ,
SZ, KZ for the curvature, semi-diameter and conic) makes the cell take the macro's SOLVERETURN
value; the macro reads the solve's surface with SOSO(0).

From the command line: `opensim optics macro <macro.zpl> [--lens FILE] [--arg NAME=VALUE,...]
[--macro-folder DIR] [--lens-folder DIR] [--out FILE] [--save-lens FILE]`.

Not built: macro graphics and plots, analysis windows driven from a macro (GETT, OPENANALYSISWINDOW),
non-sequential traces and detectors (NSTR, NSTR2, LOST, NSDD …: they wait for source and
detector objects), inserting and deleting objects, the ray database keywords, SAT and the
EXPORTCAD settings with no counterpart in the export, ZBFRESAMPLE, the incoherent ZBFSUM, XDIFFIA, the viewer windows
(ZBFSHOW, IMASHOW), RAYTRACEX and polarization traces, DEFAULTMERIT,
HAMMER and the tolerancing keywords, the multi-configuration and tolerance editor keywords, the
GET… vector keywords, paraxial ray tracing (PARAXIAL ON), the setters on a lens with a
multi-configuration table, parameter macro solves, the macro solve in the Lens Data Editor, and a
JGraph script as a macro solve or operand (tolerance scripts are under Tolerance scripts). No macro has been compared with
OpticStudio's own output. The rules are in docs/Optics-Conventions.md §56.

## Diffractive, grating and holographic surfaces

The type column of the Lens Data Editor offers eleven surfaces that add a phase to the rays
(docs/Optics-Conventions.md §57): **Diffraction Grating**, **Toroidal Grating**, **Variable Line
Space Grating**, **Binary Optic 1**, **Binary Optic 2**, **Zernike Fringe Phase**, **Zernike
Standard Phase**, **Zernike Annular Phase**, **Hologram 1**, **Hologram 2** and **Toroidal
Hologram**, each with its OpticStudio parameter columns. Grating frequencies are in lines per
micrometer and construction wavelengths in micrometers whatever the lens units; binary
coefficients are in radians and Zernike phase coefficients in waves.

The phase turns each ray by the order times its local gradient and adds (λ/2π) times the phase to
the optical path, so OPD, PSF and MTF through these surfaces are exact. A ray whose order does not
propagate stops with "evanescent order". A surface with a purely quadratic phase (Binary Optic 2,
a Zernike defocus term, an on-axis hologram) has a paraxial power that scales with the
wavelength, which the paraxial data and the chromatic analyses use. A grating, a tilt term or an
off-axis hologram deviates the axis, so paraxial data are refused for that lens with the surface
named; the real ray trace still works.

Not built: Radial Grating, Elliptical Grating 1 and 2, Extended Toroidal Grating, Binary Optic 3
and 4, Grid Phase, Zone Plate, Optically Fabricated Hologram, and these types in CODE V and OSLO files (they are read from and written to
.zmx files, see Zemax lens files; macros set them with SETSURFACEPROPERTY). Convert Asphere Types
refuses a diffractive surface, and Find Best Asphere skips one. The readings not yet
checked against OpticStudio are listed in docs/Optics-Conventions.md §10 item 53.

## Composite surfaces

A surface can carry composite add-ons: further surface shapes whose sags are added to its own,
the way OpticStudio's Composite Surface stacks add-on rows on a base (docs/Optics-Conventions.md
§58). In the Lens Data Editor, open **Composite add-ons (current surface)**, pick the base row in
the sheet, and use **Add** (a new add-on is a Standard plane, which adds nothing), then set its
type, radius, conic, parameters (number=value pairs in lens units, e.g. `2=1e-5; 3=-2e-8`),
decenter X and Y (lens units) and tilts X, Y, Z (degrees) relative to the base, and press
**Apply**. **Remove** deletes the selected add-on; **Activate all add-ons** and **Ignore all
add-ons** clear or set every add-on's ignore flag (an ignored add-on adds nothing). Each change is
one undo step, and the add-ons are saved with the lens.

The trace, the paraxial data, the sag analyses (which show the summed sag), edge thicknesses and
OPD use the summed surface. An untilted add-on adds its sag shifted by its decenter; a tilted one
adds its height above its own tilted vertex plane, measured along the base's axis (a reading of
the help, §10 item 54). A decentered or tilted add-on makes the surface non-symmetric: paraxial
data are then refused, naming the add-on, while the lens still traces. Types that can be bases
and add-ons are those of OpticStudio's supported list that this program builds (Standard, the
aspheres, Biconic, Toroidal, Irregular, the polynomial, Zernike and Grid Sag freeforms, Radial
NURBS, …); others are refused by name. The multi-configuration operands CSDX, CSDY, CSTX, CSTY
and CSTZ change an add-on's placement (arguments: the base surface and the add-on's number).

In a macro, `COMPOSITEON n` makes surface n an add-on of surface n + 1 (it leaves the surface
list, so the surfaces after it move up by one); surface n must have thickness 0, the same medium
on both sides, no solves, variables, tilts or aperture, and not be the stop. `COMPOSITEOFF n`
makes the top add-on of surface n a surface again, just before it (thickness 0, the medium before
it). The numbers are this program's surface numbers, in which add-ons are not counted, unlike
OpticStudio's rows (docs/Optics-Conventions.md §10 item 54). A .zmx export stops at a surface with
add-ons (listed and left out with the box ticked), since how the file stores them is not known.
Convert Asphere Types keeps a surface's add-ons and converts the base; Find Best Asphere skips a
surface with add-ons; tolerancing keeps the add-ons with their surface, but refuses the surface
decenter and tilt operands (TSDX, TSDY, TSDR, TSTX, TSTY, TIRX, TIRY) on it: use the element
operands. Diffractive types cannot be add-ons (OpticStudio does not allow them either).

Not built: add-ons as their own editor rows, "Set Tilt/Decenter to follow Base surface aperture"
and "Update Tilt", drawing add-ons in the layouts (the base is drawn), the sag analyses' Remove
"Composite Sag", Seidel sums of composite surfaces, Reverse Elements and Make Double Pass of
surfaces with add-ons, the COMPOSITEOFFAXISAPERTUREON macro keyword, add-ons in .zmx, CODE V and
OSLO files, and diffractive phase add-ons.

## Design conversion tools

**Design Conversion Tools…** in the optics panel opens one window with three of OpticStudio's
Optimization Tools. Each shows a report; **Keep** writes the last result into the lens (the Lens
Data Editor's Undo takes it back); **Stop** ends a search between trials and keeps what it found.

- **Convert Asphere Types**: choose the surface, the new form (Even, Extended, Q-type Qbfs or
  Qcon) and the number of terms or Automatic. The new coefficients are a least-squares fit of the
  old sag over the clear semi-diameter, which is also the normalization radius; with **Fit radius
  and conic** a non-zero or variable radius or conic is fitted too (not one held by a solve). The
  report gives the RMS and largest sag residual in lens units: if they are not small next to the
  semi-diameter, use more terms.
- **Find Best Asphere**: tries a conic, or an even asphere up to the chosen order (terms from r⁴),
  on each candidate surface between First and Last in turn (Standard, no conic, between air and
  glass, radius variable or held by a marginal ray angle or F/# solve, no CRVT row), re-optimizes
  every variable against the current merit function, and ranks the surfaces. The merit function
  should suit an aspheric design. Keep writes the best.
- **Stock Lens Matching**: loads the stock lens catalog folder of Lens Catalogs, then for each lens
  element (or each with a variable radius) tries the catalog lenses within the EFL and EPD
  tolerances (never smaller than the element), in the orientation and shape the options allow,
  keeping the element's air space after it, optionally re-optimizing the variable air spaces,
  and lists the best matches per element and the best combinations. Keep writes the best
  combination.

The trials use the optimization settings at the bottom of the window. From the command line:
`opensim optics convert <project> --tool asphere|best-asphere|stock` (options in `opensim help`).
Not built: writing `<lens>_SLM.zmx`, the coating check, conversion to the Odd forms.

## Tolerance scripts

A tolerance script describes how each toleranced lens is aligned and judged, as OpticStudio's
tolerance scripts do: adjust this, measure that, then compensate. It is written in the ZPL macro
language (see ZPL macros) with the tolerance-script commands added, and runs in every trial of a
sensitivity or Monte Carlo run instead of the compensation.

In the Tolerance Data Editor, open **Tolerance script**, write the script, and choose **User
script** as the criterion in the tolerance run. **Check** lists syntax errors and commands this
version does not run, with their lines. The script, its name and its folders are saved with the
project.

The commands (OpticStudio's list):

- `CLEARCOMP` removes the compensators; `COMP surf code` (0 thickness, 1 curvature, 2 conic),
  `CPAR surf param` and `CMCO operand config` add one. Surface numbers are those of your lens; the
  surfaces the tolerancing inserts are accounted for. With no COMP, CPAR or CMCO in the script,
  the table's COMP, CPAR and CMCO rows are the compensators; with them, the table may not have any.
- `LOADMERIT name` loads a merit function and evaluates it; `GETMERIT` evaluates the current one.
  The merit function files are this program's: build the merit function in the Merit Function
  Editor, set the **Merit folder** and the **Merit file to save**, and press **Save merit function**
  (OpticStudio's .MF files cannot be read). A script can also build one with INSERTMFO and
  SETOPERAND.
- `OPTIMIZE n` (damped least squares) and `OPTIMIZE-OD n` (orthogonal descent) adjust the current
  compensators to minimise the current merit function; n = 0 or nothing runs until converged.
  `NOMINAL OPT_OFF` skips them while the nominal performance is computed.
- `PERTURB type int1 int2 int3 stat nstd min max` adds a random change to a thickness, radius,
  conic, fixed semi-diameter (type 0), parameter (1) or multi-configuration value (3); it does
  nothing on the nominal lens. `UPDATE` checks that the lens still builds.
- `REPORT "text" row` prints the text and the value of a merit function row (0: the whole merit
  function; -1: the text alone); `REPORT_NB` leaves out the line break; `QUIET ON` stops the
  printing. `OPENFILE "name" TXT|BIN`, `HEADER "text"` and `CLOSEFILE` send the numbers to a file
  in the **Output folder**; `SAVE n` saves the lens as TSAVnnnn.ZMX there. Files are written when
  the run is done, in trial order.
- `FORMAT m.n [EXP]` sets REPORT's number format (default 16.8 EXP).

The criterion of each trial is the value of the merit function after the last LOADMERIT, GETMERIT,
OPTIMIZE or OPTIMIZE-OD. The report shows each trial's printed text under "Script output". A script
that fails stops that trial only, and the trial's note names the script, the line and the problem;
a syntax error stops the run before it starts. Arguments may be separated by blanks, as in
OpticStudio (`CPAR 3 1`), or by commas, as in ZPL (`CPAR 3, n + 1`).

An example, after OpticStudio's: centre element 2 with the decenters of the coordinate break at
surface 3, then refocus with the thickness of surface 7, then evaluate:

```
NOMINAL OPT_OFF
CLEARCOMP
LOADMERIT CENTER
CPAR 3 1
CPAR 3 2
OPTIMIZE
CLEARCOMP
LOADMERIT FOCUS
COMP 7 0
OPTIMIZE
CLEARCOMP
LOADMERIT EVALUATION
REPORT "chief ray y = " 1
```

Nested assemblies: a group toleranced inside a larger toleranced group (element tilts and decenters
nested, the outermost first) is set up in the tolerance table as before; a script then aligns each
stage with its own compensators and merit function. OpticStudio has no separate NEST operand, and
neither does this program.

From the command line: `opensim optics tolerance <project> --script FILE.TSC [--merit-folder DIR]
[--script-out DIR]`.

Not built: REPORT … SZERNIKE (Zernike coefficients), PERTURB of extra data and non-sequential
data, CNPA and CNPS, OpticStudio's .MF merit function files, a script in Quick Yield. Keywords that
would make trials depend on each other or on a person (OUTPUT to a file, SAVELENS, LOADLENS, file
operations, INPUT, PAUSE, TOLERANCE) are refused in a script. No script has been compared with
OpticStudio's own output. The rules are in docs/Optics-Conventions.md §60.

## Diffraction efficiency

How much light a grating or hologram sends into each order (docs/Optics-Conventions.md §61). Four
analyses, in the Polarization group of the analysis menu:

- **Diffraction efficiency**, **Efficiency vs. angle** and **Efficiency vs. wavelength** are
  OpticStudio's analyses for volume holograms, by Kogelnik's coupled-wave theory. Set a Hologram 1,
  Hologram 2 or Toroidal Hologram surface's "Volume hologram?" parameter to 1 and fill in the
  thickness, the construction indices n1 and n2, the emulsion index n, the modulation dn, the
  shrinkage and the index shift (the ray trace does not use them). The field type must be Angle:
  the analyses scan the chosen field's Y angle (default 99% to 101% of it) and the wavelength
  (default 99% to 101% of the selected one), trace the chief ray to the hologram and plot the
  diffracted and undiffracted efficiency, or a map of both scans. The order must be +1 or −1.
- **Grating efficiency (RCWA / scalar)** is this program's own: a lamellar, blazed, multilevel or
  sinusoidal relief of given period (or a Diffraction Grating surface's lines/µm), depth and
  indices, by rigorous coupled-wave analysis (TE, TM or unpolarized, reflected and transmitted
  orders, absorption for lossy media) or by the thin-element theory (transmitted orders only, normal
  incidence). It lists every propagating order and can plot one order against angle or wavelength.

The command line gives the grating efficiency without a project:
`opensim optics efficiency --period 1.5 --wavelength 1 --depth 0.5 --angle 10 --polarization tm`
(`opensim help` lists the options).

Not built: the conical mounting and two-dimensional gratings in RCWA, anisotropic media, the
Optically Fabricated Hologram, "Consider Fresnel?", efficiency in the ray trace of the other
diffractive surfaces, and the non-sequential analyses. The volume-hologram readings not yet checked
against OpticStudio are listed in docs/Optics-Conventions.md §10 item 57.

## Birefringent crystals

The type column of the Lens Data Editor offers **Birefringent In** and **Birefringent Out**
(docs/Optics-Conventions.md §62). Put a crystal glass on the Birefringent In surface, for example
CALCITE: its index is the ordinary index, and the extraordinary index is read from the glass of
the same name with "-E" appended (CALCITE-E), which must be in an active catalog. Give the crystal
axis as direction cosines in parameters 2–4 (in the In surface's own coordinates) and choose the
mode in parameter 1: 0 traces the ordinary ray, 1 the extraordinary ray, and 2 and 3 trace the
ordinary or extraordinary ray while the polarization ray trace also keeps the other part of the
field with its phase, which is what a wave plate needs. End the crystal with a Birefringent Out,
or start another crystal with a second Birefringent In (a Wollaston prism); coordinate breaks may
stand between. One trace follows one ray; use two configurations with modes 0 and 1 to see both.

Inside the crystal the ray follows the energy direction, which walks off the wave normal for the
extraordinary ray, and the optical path is the phase along the wave normal, so OPD, PSF and MTF
are those of the traced mode. Paraxial data use the ordinary index; with mode 1 or 3 set Parax
Ignore (parameter 5) to 1, or paraxial data are refused for the lens. Surface coatings are not
allowed on these two types.

Not built: the toroidal substrate (shape 1), crystals whose axis varies with position (gradient
modes), drawing the crystal axis, optical activity, stress birefringence, non-sequential
birefringent objects, and these types in CODE V and OSLO files (Zemax lens files and ZPL macros
carry them). The readings not yet checked against OpticStudio are listed in
docs/Optics-Conventions.md §10 item 58.

## Operand catalogue

### Imaging and ray operands

The merit function editor knows 92 more of OpticStudio's operands: first-order properties,
aberrations, MTF, Strehl ratio, encircled energy, paraxial and real ray data, relative illumination
and the best-fit sphere. Type the four-letter code in the Type column, or search the operand list
by code or by name ("Petzval", "Huygens", "encircled"); each operand shows OpenSim's name with the
code, e.g. "Spherical aberration (Seidel) (SPHA)". The editor names each column after the
operand's parameter (Surf, Wave, Hx …), and the row type's description says which column holds what.

What they compute is what the matching analysis computes, at the same settings:

- **First order** (AMAG, CARD, ENPP, EFLX, EFLY, EPDI, EXPD, EXPP, ISFN, ISNA, LINV, OBSN, PIMH,
  PMAG, POWR, WFNO): the paraxial data of the Paraxial and Cardinal Points reports; WFNO is the
  real working F/# on axis.
- **Aberrations**: SPHA, COMA, ASTI, FCUR, DIST in waves from the Seidel Coefficients (DIST with
  Surf 0: the Field Curvature and Distortion percentage at the edge of the field), PETZ, PETC,
  LACL, AXCL, LONA (Longitudinal Aberration), FCGS, FCGT (Field Curvature), the RMS spot and
  wavefront rows RSCE, RSCH, RSRE, RSRH, RWCE, RWCH, RWRE, RWRH (as the RMS analyses), ZERN
  (Zernike Fringe or Standard coefficient of a field), BSER.
- **MTF**: MTFN, MTFX, GMTN, GMTX (smaller and larger of tangential and sagittal), MSWT, MSWS,
  MSWA, MSWN, MSWX (square wave), MTHT, MTHS, MTHA, MTHN, MTHX (Huygens MTF).
- **Strehl and enclosed energy**: STRH (Huygens PSF), DENC, DENF (diffraction), GENC, GENF
  (geometric); distances in micrometers.
- **Relative illumination**: RELI, EFNO. **Best fit sphere**: BFSD.
- **Paraxial rays**: PARX, PARY, PARR, PATX, PATY. **Real rays**: REAA, REAB, REAC, RENA, RENB,
  RENC, RAID, RAED, RAIN, RAEN, RANG, RETX, RETY, RAGX, RAGY, RAGZ, RAGA, RAGB, RAGC, OPTH, PLEN,
  HHCN, TRAI, MNRI, MXRI, MNRE, MXRE.

A row that cannot be computed fails alone and says why (a ray that misses, a field off the axis
for an operand that scans along an axis, a setting that is not built).

**Not built:** EFLA, POWF, POWP, SFNO, TFNO, ABCD, DISA, DISC, DISG, DIMX, SMIA, BIOC, BIOD, OSCD,
SPCH, TRAD, TRAE, CEHX, CEHY, CENX, CENY, CNAX, CNAY, CNPX, CNPY, DXDX, DXDY, DYDX, DYDY, IMAE, XENC,
XENF, ERFP, FOUC, TOLR, PARZ, PARA, PARB, PARC, PANA, PANB, PANC; polarization and all-configuration
options; the Huygens enclosed energy; the afocal forms. No operand has been compared with
OpticStudio's own output. Details: Optics-Conventions.md section 63.

### Constraint and system operands

The Merit Function Editor offers OpticStudio's constraint and system-data operands with their
four-letter codes: type the code or the name in the Operand column, or search for it.

- **Lens data:** conic bounds (COGT, COLT), diameters (DMGT, DMLT, DMVA), edge thickness along an
  axis (ETGT, ETLT, ETVA) and round the perimeter (XNET, XNEA, XNEG, XXET, XXEA, XXEG), full and
  blank thickness (FTGT, FTLT, BLTH), range bounds on curvature, edge thickness and semi-diameters
  (MNCV, MXCV, MNET, MXET, MXEA, MXEG, MNSD, MXSD, OMMI, OMMX), the mechanical semi-diameter
  (OMSD), the glass thickness sum (TGTH), total thickness (TTGT, TTLT, TTVA) and the spread of a
  thickness sum over the configurations (ZTHI).
- **Lens properties:** cylinder volume (CVOL), diameter-to-thickness ratio (MNDT, MXDT), sag at
  the edge (SAGX, SAGY), sag, slope and curvature at a point (SSAG, SSLP, SCRV), surface curvature
  and derivatives (SCUR, SDRV), thickness at a point (STHI), the surface normal (NORX, NORY, NORZ)
  and the distance along it to the next surface (NORD), volume and mass (VOLU, TMAS), and the phase
  and phase slope of a diffractive surface (SPHS, PSLP).
- **Other data:** surface parameters (PMGT, PMLT, PMVA), glass data (GTCE, INDX, MNAB, MXAB, MNIN,
  MXIN, MNPD, MXPD), global vertex and orientation (GLCX, GLCY, GLCZ, GLCA, GLCB, GLCC, GLCR), the
  wavelength (WLEN), multi-configuration values (MCOG, MCOL, MCOV), the paraxial Gaussian beam
  (GBPD, GBPP, GBPR, GBPS, GBPW, GBPZ) and the surface TCE (TCGT, TCLT, TCVA).

The editor names each operand's parameter columns in its header when the row is selected. Global
coordinates are relative to surface 1. Lengths are in lens units, VOLU in cm³ and TMAS in grams.

**Not built:** DCRV, DSAG, DSLP, DPHS, QSLP, TSAG; IMSF, PRIM, SVIG, CVIG, FDMO, FDRE (operands
that change the system data for later rows); EQUA; the skew Gaussian beam operands (GBSD, GBSP,
GBSR, GBSS, GBSW); the gradient-index, non-sequential, hologram-construction
and coating operands (CMGT to CIVA, CODA); UDOC; off-axis coordinates and the Base/Composite sag
removal in SSAG, SSLP and SCRV; the removal of fitted terms in SPHS and PSLP. (POPD and POPI are
built: see Physical optics propagation; FICL and FICP: see Fiber coupling.)

When you type or load an OpticStudio operand this version does not have, whether from the imaging
or the constraint half, the message says what it computes and why it is not built, for example
"NSDD (OpticStudio: Non-sequential incoherent detector data…) is not built yet: needs OP-79". A
loaded row with such a code becomes OOFF. The same message answers SETOPERAND in a ZPL macro, and
OPEV evaluates any operand listed here by its OCOD number. (The Zemax lens file import reads no
merit rows: it refuses them all, so it never meets an operand code.)

## Universal plots

**Universal plot 1D** and **Universal plot 2D** (category Universal plot) plot any operand of the
Merit Function Editor's catalogue while one or two values of the system step from a start to a
stop value: for example the focal length (EFFL) against a radius, or a real ray coordinate (REAY)
over two thicknesses.

- **Independent variable** (X and Y in 2D): the Category (Surface, System or Configuration), the
  Parameter, the Surface number and a Number, then Start value, Stop value and # of steps (both
  ends included, at least 2). Surface data is Radius (0 is a plane), Curvature, Thickness, Conic
  or Parameter (Number = the parameter column). System data is ApertureValue, ApodizationFactor,
  Temperature, Pressure, FieldX, FieldY, FieldWeight, the vignetting factors FieldDecenterX,
  FieldDecenterY, FieldCompressionX, FieldCompressionY and FieldTilt (Number = the field), and
  Wavelength or WavelengthWeight (Number = the wavelength). Configuration data is the parameter
  Operand: row Number of the Multi-Configuration Editor in configuration Surface.
- **Operand**: a four-letter code with its Int1, Int2 and Data1 to Data6 as the Merit Function
  Editor would hold them (Hx, Hy, Px, Py are in Data1 to Data4 for the ray operands), or `Merit`
  with Line 0 for the merit function's value or Line k for row k's value. Math operands such as
  DIFF have no value alone: use Merit and the row's Line.
- **2D display**: Show as FalseColor or Contour; Min and Max plot scale fix the colour scale of
  the false-colour map (0 = automatic).

Each point is computed on a copy of the system: your lens is not changed. Solves and pickups are
worked out again at every point, as the optimizer does, so a solved back focus follows the swept
radius. The operand is evaluated in the current configuration.

A point whose value cannot be computed (a ray that misses or is totally internally reflected, a
solve that fails, a wavelength made invalid) is a gap in the plot, "failed" in the table, and listed
under Failed points with its parameter value and the reason. The analysis stops with an error only
when no point can be computed; OpticStudio stops at the first failure.

Refused by name: non-sequential data; a cell driven by a solve or pickup; a value that a
multi-configuration row overwrites (sweep that row instead: the message names it; OpticStudio
edits the row for you); two axes that write the same cell. `opensim optics analyse --analysis
UniversalPlot1D --set "Parameter=Radius;Surface=1;Start=20;Stop=200;Steps=10;Operand=EFFL;Int2=1"`
runs it from the command line.

**Not built:** non-sequential data, the surface plot and grey-scale displays, the contour format
string, the 1D plot's min and max value, and the UPL and UP2 settings files. Nothing has been
compared with OpticStudio's output. Details: Optics-Conventions.md section 65.

## Physical optics propagation

Propagates a coherent beam through the lens as a sampled field, so that diffraction between
surfaces is modelled, after OpticStudio's Physical Optics Propagation
(docs/Optics-Conventions.md §66). Two analyses, in the Beams group of the analysis menu:

- **Physical Optics Propagation** builds the initial beam in the space before the start surface
  ("Surf to beam" from it), propagates it surface by surface and shows it just after the end
  surface (0: the image surface). The beam types are Gaussian waist (with Hermite orders and a
  truncating aperture), Gaussian angle, Gaussian size+angle, astigmatic Gaussian, top hat, File (a
  ZBF beam file, full path) and Multimode (a ZMM file of GW, TH, PHASE, COHERENT and INCOHERENT
  lines). The beam is scaled to a total power (W) or a peak irradiance (W per lens unit²). The
  sampling is a power of 2 from 32 to 16384; "Auto width" sets the array to ω0·√(π·n), which the
  help recommends (a multimode beam needs the widths set). "Surface transfer" chooses the phase
  each surface adds: from a real ray at every sample (ray based) or −φ·r²/2 of its paraxial power.
  Surface apertures clip the beam. The result shows irradiance and phase maps, x and y
  cross-sections, encircled power, the beam data (power, peak irradiance, the pilot beam, centroid,
  beam widths, M²), the propagation report (which operator ran where, and the sample spacing) and
  any sampling warnings: power in the array's outer eighth (widen the array), power at high
  spatial frequencies (increase the sampling), or a pilot waist smaller than the wavelength. "Save
  output beam to" writes the end beam as a ZBF file; with "Save beam at all surfaces" every
  surface's beam is written too, named name_0001.ZBF and so on.
- **Beam File Viewer** shows a ZBF file the same way without propagating it.

The phase is shown with OpticStudio's sign: positive where the wavefront is ahead of the reference
(a plane inside the pilot beam's Rayleigh range, a sphere centred on its waist outside).

The merit operands POPD (power, peak irradiance, pilot beam, centroid, widths, M², moments,
irradiance and phase variation, encircled power) and POPI (one pixel's irradiance, field or phase)
run the analysis with its last-used settings; their Surf, Wave and Field columns replace the end
surface, wavelength and field when not zero. `opensim optics analyse` runs both analyses by their
ids `PhysicalOpticsPropagation` and `BeamFileViewer`.

Not built: polarized propagation, "Separate X, Y", off-axis fields and tilted, decentered or folded
systems, the surface properties' physical optics settings, the DLL beam, and POP in
non-sequential mode. Fiber coupling of the propagated beam is described under Fiber coupling. The readings not yet checked
against OpticStudio are listed in docs/Optics-Conventions.md §10 item 62.

## Extended scenes and image simulation

What a real scene looks like through the lens (docs/Optics-Conventions.md §67). Seven analyses in
the "Extended scene" group of the analysis menu; each takes the full path of its source file as a
setting:

- **Image simulation**: a BMP (24/32-bit), PNG (8-bit), IMA or BIM picture "Field Height" high,
  centred on a field, imaged through a grid of PSFs (none, geometric spots, or the Huygens or FFT
  diffraction PSF) interpolated across the field, with distortion and lateral colour from real
  chief rays and, optionally, relative illumination. Oversampling, guard band, flips and quarter
  turns edit the source first; Show As gives the simulated image, the edited source or the PSF
  grid. RGB pictures image red, green and blue at 0.606, 0.535 and 0.465 µm (or on wavelengths 3, 2
  and 1, or one channel at one wavelength) and show one map per colour. The detector defaults to
  the picture's pixel count and the help's pixel size; the result can be written to a BMP or PNG.
- **Geometric image analysis**: rays from an IMA or BIM source binned on a square detector, in
  watts per pixel, with the efficiency; spot diagram, map, contour or cross-sections; pixel
  interpolation; the detector image can be saved as a BIM file.
- **Geometric bitmap image analysis**: the same for a BMP or PNG picture, colour by colour
  (0.656, 0.587 and 0.486 µm for RGB), normalised by the peak or the average pixel.
- **Partially coherent image analysis**: the diffraction image of an IMA or BIM object given in
  image space, with one transfer function (the chosen field's): incoherent, coherent or partially
  coherent (Gaussian or sinc γ of scale α, "mostly coherent" or "mostly incoherent" sums), the
  transfer functions themselves, the raw object and its transform; optional detector resampling
  and the help's MTF estimate from a cross-section.
- **Extended diffraction image analysis**: coherent or incoherent, with the transfer function
  interpolated from an n × n grid over the file, optional distortion and relative illumination.
- **IMA/BIM file viewer** and **Bitmap file viewer** show a file's maps.

Sampling problems (a PSF wider than its window, a transfer function beyond the grid's Nyquist
frequency) are reported under "Sampling and validity warnings". The rays use a Sobol sequence, so a
result repeats exactly.

Not built: polarization, ray scattering, JPG and palette bitmaps, ZBF files, Light Source Analysis
(Premium; non-sequential source files), afocal image space, surface plots, true-colour pictures
(each colour is its own map). The readings not yet checked against OpticStudio are listed in
docs/Optics-Conventions.md §10 item 63.

## Fiber coupling

How much light a system couples into a fiber (docs/Optics-Conventions.md §68). Two analyses in the
Beams group, the POP fiber coupling integral, and two operands:

- **Single Mode Fiber Coupling**: a source fiber at the object point (NA x and y, angles, along the
  object z axis or the chief ray) or, with "Ignore source fiber", the pupil lit by the system
  apodization, coupled into a Gaussian receiving fiber (NA x and y, tilt about X then Y, decenter
  X, Y, Z in lens units, at the image surface's vertex or the chief ray). It reports the system
  efficiency (light through the system), the receiver efficiency (overlap of the arriving wave with
  the fiber's mode), their product, the maximum with the aberrations ignored and the amplitude
  coupling, with maps of the beam and the fiber mode over the pupil. "Use Huygens integral" sums the
  beam on the fiber's plane first (and maps it); the two methods agree where both apply.
- **Multimode Fiber Coupling**: the geometric estimate: the share of the source's light whose rays
  land inside the core (radius, decenter) within the fiber's NA (given, or from the core and
  cladding indices), with a plot of the ray intercepts.
- **Physical Optics Propagation** with "Compute fiber coupling": the "Fiber:" settings define the
  receiving fiber's mode at the end surface (any beam type except DLL and Multimode, with its own
  decenter and tilt); the result adds the system, receiver and total efficiencies.
- **FICL** returns the single-mode coupling (fast or Huygens, power or amplitude, or from the
  analysis's saved settings); **FICP** and POPD 0 to 2 return the POP fiber coupling with the saved
  settings. The polarized values (FICP 1 to 6, POPD 60 to 63) are 0, because the propagation is
  unpolarized.
- **In a macro**, FICL(n) returns the single-mode coupling with the 19 settings placed in vector
  VECn (sampling, wavelength, field, the source and receiving fibers, Huygens), as OpticStudio's
  help lays them out. POP from a macro uses the saved POP settings, fiber data included.

`opensim optics analyse` runs the analyses by their ids `SingleModeFiberCoupling` and
`MultimodeFiberCoupling`.

Not built: polarization (refused), the receiving fiber's index on the image surface in the
single-mode analysis (use POP), OpticStudio's automatic switch-off of the fast algorithm, DLL and
multimode fiber modes, polarized POP coupling. The readings not yet checked against OpticStudio are
listed in docs/Optics-Conventions.md §10 item 64.

## Non-sequential ray tracing

The second engine (docs/Optics-Conventions.md §69): rays that find their own path through objects
placed in space, reflecting, refracting, splitting and being absorbed wherever they land. What is
built is the core: the engine, three objects, a stand-in source and detector, the energy ledger,
and one analysis.

- **Objects.** Sphere (solid or a mirror/absorbing shell), Rectangle, and Standard Lens (two
  conic faces with annular flats and a possibly tapered edge, OpticStudio's nine parameters).
  Each has a position, tilts (about x, then the new y, then the new z, or about the fixed axes),
  a reference object, an "Inside Of" object, a material (blank, a glass or an index, MIRROR or
  ABSORB) and per-face "Face Is" and coating. Where objects overlap or touch, the later object
  decides.
- **Saved scene.** The project's optics record carries the objects and the non-sequential
  controls (`NonSequential` in the project file, lengths in lens units). The objects are edited
  in the Non-Sequential Component Editor (below), written in the file or built through the
  library API.
- **Controls.** Maximum intersections and segments per ray, maximum nested/touching objects,
  minimum relative and absolute intensity, glue distance, simple ray splitting.
- **Energy.** With "Use polarization" on (the default) every face reflects, transmits and absorbs
  by Fresnel's equations or its coating, uncoated mirrors are aluminium, and glasses absorb by
  their internal transmittance; off, interfaces are lossless. Splitting traces both the reflected
  and transmitted rays; simple splitting picks one at random; no splitting traces the transmitted
  ray.
- **Energy ledger.** Every run reports where the emitted energy went: detected, absorbed,
  escaped, below the thresholds, reflected but not traced, stopped at a limit, lost to errors; the
  lines add up to the emitted energy.
- **NSC single ray trace** (analysis group "Non-sequential", also `optics analyse` with id
  `NscSingleRayTrace`): one ray from a start point and direction (optionally in an object's
  frame, optionally inside an object), with or without polarization and splitting, at one
  wavelength; every segment with its parent, level, the object it runs through and hits, the
  face, what happened (X, R, T, G, Z, I), position, direction, normal, intensity and path, then
  the ray's energy ledger. Trace errors are listed, unless "Ignore trace errors" is set.
- **Library API.** `NscTracer.Run` traces any list of rays as a Monte Carlo run, the same to the
  bit at any thread count; `NscSourceStandIn` makes a point-into-a-cone or a collimated-disk
  source; a surface object flagged as a detecting stand-in absorbs and counts what it receives.
- **Macros.** NOBJ, NPAR, NPOS, NPRO and OBJC read the saved objects and SETNSCPARAMETER,
  SETNSCPOSITION and SETNSCPROPERTY change them (surface 1; the built types' parameters and the
  type, comment, reference object, inside of, material, face coating, rotation order and "Face Is"
  properties). NSTR and the detector functions wait for source and detector objects.

Not built: the rest of the object library (OP-72 to
OP-75), sources (OP-77), detectors and the detector viewer (OP-79), scattering (OP-80), volume

Not built: the Non-Sequential Component Editor (OP-71), the rest of the object library (OP-72 to
OP-75), sources beyond those under Non-sequential sources (OP-77), detectors and the detector viewer (OP-79), scattering (OP-80), volume
physics (OP-81), diffraction, gradient index and coating plots in objects (OP-82), ray databases
and filters (OP-83), Consider/Ignore Objects, mixed mode with ports, several wavelengths in one
run, a layout of traced non-sequential rays. The readings not yet checked against OpticStudio are
listed in docs/Optics-Conventions.md §10 item 65.

## CAD export

**Export CAD Files…** in the Optics panel writes the lens elements, mirrors and rays as a file a
mechanical designer can open (OpticStudio's File > Export > CAD Files and Export 2D DXF):

- **STEP** (AP214): one solid per element and per mirror with a substrate. Planes, spheres,
  cylinders and cones are exact surfaces; any other rotationally symmetric surface (a conic, an
  asphere) is a B-spline patch fitted to the tolerance. Rays are polylines.
- **IGES** 5.3: each face as a rational B-spline surface (exact for planes, spheres, cylinders and
  cones, fitted for aspheres), the rims as circles and the rays as polylines; lens data on level 1,
  rays on level 2. IGES solids are not written.
- **STL**: triangles with the tolerance as chordal error, binary or ASCII; no rays (STL has no
  lines).
- **DXF**: the 2-D cross-section as the Cross-Section layout shows it (global z across, y up):
  spheres as arcs, flats and edges as lines, aspheres as polylines, rays as polylines; layers LENS
  and RAYS. Every element's axis must lie in the global YZ plane.

The settings: file type; first and last surface (0 for the last before the image); **Edges**,
applied to every element: Squared to next surface (flats out to the larger face and a cylinder, as
the shaded model draws), Tapered to next surface (a cone between the rims) or Flat to next surface
(a cylinder at the front face's edge, the back face drawn out to it); **Mirror substrate**, the flat
back behind a mirror that is not part of a lens (0 exports the bare mirror surface, which STEP
refuses); **Tolerance** 1E-4 to 1E-7 lens units, the largest deviation of a fitted spline from the
surface and the chordal error of STL and DXF; the ray pattern (none, Y, X or XY fan, ring, grid),
number of rays, field and wavelength (0: all) and Delete vignetted. Files are written in the lens
unit, in the global frame (surface 1's vertex at the origin). The dialog only reads the lens, and its
settings are not saved with the project. The log of the export (what was written, the largest spline
deviation, rays that failed) is shown under the buttons.

Refused, by name: surfaces that are not rotationally symmetric, apertures other than circles
centred on the axis, elements whose faces are tilted or decentered against each other or meet at
the edge, coordinate breaks inside glass.

From the command line: `opensim optics export <project|lens.osslens> --format step|iges|stl|dxf`
(or `--out` with a `.step`, `.stp`, `.igs`, `.iges`, `.stl` or `.dxf` name), with `--first`,
`--last`, `--edges squared|tapered|flat`, `--substrate`, `--tolerance`, `--rays`, `--nrays`,
`--field`, `--wavelength`, `--delete-vignetted` and `--ascii`.

The exported STEP files are read back by OpenSim's own STEP importer in the tests (same volume and
areas); they have not been opened in a CAD program here. Not built: SAT (OP-76), point clouds, 3-D
DXF and IGES Linework, dummy surfaces, solid beams, ray footprints, configurations by file or layer,
and chamfers. The macro keyword EXPORTCAD writes IGES, STEP or STL with the settings placed in
VEC1 (see ZPL macros); SAT and the settings the export has no counterpart for are refused by name.
The equations are in `docs/Optics-Conventions.md` §70.

## Non-Sequential Component Editor

The Optics panel's "Non-Sequential Component Editor…" opens the table of the non-sequential
objects (docs/Optics-Conventions.md §71). Edits are saved with the project and reach the analyses
at once; every edit can be undone.

- **The table.** One row per object: type, comment, reference object, Inside Of, X, Y, Z, the
  tilts about X, Y, Z, the material, then the type's parameters in OpticStudio's order (Sphere,
  Rectangle, Standard Lens). Insert, delete and move rows with the buttons or the grid's keys
  (Insert, Ctrl+Delete, Alt+Up/Down). Reference objects and Inside Of numbers keep naming the same
  objects when rows are inserted, deleted or moved, absolute ones as absolute and relative ones as
  relative; deleting an object another one refers to, or moving it below that object, is refused.
- **Object properties** ("Object properties…"): the type, "Use global XYZ rotation order", the
  detecting stand-in, and "Face Is" and the coating of each face. Sources, scattering, volume
  physics, GRIN, birefringence, diffraction, CAD and detector settings are listed as not built,
  each with its plan item where one exists.
- **Types not built.** An object of a type this build does not have (read from a file) is shown
  with its parameters by number and saved unchanged; it is not traced, and no new object of such a
  type can be added.
- **Replicate Object** (Tools): Number X, Y, Z (odd counts) and Delta X, Y, Z make an array with
  the current object at its centre; the copies follow it as independent rows referenced to it
  (absolute, or relative when checked).
- **Modify Reference Object** (Tools): objects First to Last are placed relative to another
  object (or 0, the scene) without moving: position and tilts are recomputed.
- **View current object…**: the current object alone in a 3-D view, each face its own colour, with
  its local axes; the drawing resolution and opacity are the viewer's own settings.

Not built: Combine Objects, Create Polygon Object, Freeform Z Tools and the Object Editor (each
says which plan item builds it), solves and pickups on object cells, even-count replication,
picking a face in the viewer, Draw settings on layouts. Readings not yet checked against
OpticStudio: Conventions §10 item 67.

## Non-sequential sources

Sources are objects in the non-sequential scene, saved as object rows with their OpticStudio type
name and parameters (1–5 are # Layout Rays, # Analysis Rays, Power, Wavenumber and Color #) and an
optional Sources tab. Built: Source Point, Source Ray, Source Ellipse, Source Rectangle, Source
Gaussian, Source Diode, Source Two Angle, Source Radial, Source Filament, Source Tube and the three
volume sources. Rays never strike a source. Each analysis ray carries the source power divided by
the number of rays, and an array of sources multiplies the power.

- Spectrum: the system wavelengths (all by weight, or the row the wavenumber names), a uniform
  power spectrum, a black body spectrum, or a table of wavelength–weight pairs. Each ray picks its
  wavelength at random by weight.
- Units: the system's source unit. Watts and joules are traced as stated. Lumens need the CIE
  luminous efficiency table (plan item OP-79) and are refused until it is connected.
- Sources tab: random polarization or a Jones vector, pre-propagation, reverse rays, Sobol or
  random sampling, and rectangular, circular or hexapolar arrays. Initial phase, coherence length
  and bulk scatter are saved but have no effect yet.
- Inside Of: a source inside a glass object must name it, as OpticStudio requires; a ray that
  starts inside an object its source does not name stops the run with a message naming the
  source and the ray.

The engine traces one wavelength at a time; a source run traces each wavelength of every source and
adds up the energy. There is no analysis window or ZPL command for a source run yet (the editor is
OP-71, detectors OP-79). Not built: Source Object, Source Imported (OP-75), Source File, Source
IESNA File and Source EULUMDAT File (OP-78), Source DLL and Source Diffractive (OP-100), Radiant
Source Models, spectrum files on disk, the CIE colour models, the hexagonal array. Details and the
readings not yet checked against OpticStudio are in docs/Optics-Conventions.md §72 and §10 item 68.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- layouts with traced rays, and the analysis windows not described above (the Configuration
  Matrix Spot and the rest);
- the MTF in object-space cycles per milliradian for focal systems, afocal through-focus MTF
  (diopters) and the afocal Huygens MTF;
- real transmission and polarization in the image-quality analyses (the polarization trace of
  OP-51 is built; besides its own five analyses only relative illumination uses it, as an
  option);
- the tolerance analyses (the merit function, its editor, the default merit function wizard,
  the local optimizer, glass substitution, the Global and Hammer searches, the tolerance data editor with
  its operands, sensitivity and inverse sensitivity with compensators and the MTF and encircled
  energy criteria, Monte Carlo tolerancing, the tolerance wizard, summary, data viewer and Quick
  Yield, tolerance scripts, the contrast (MTF) wizard criterion and the imaging, ray, constraint
  and system operands are built; the operands listed as not built under Operand catalogue are not);
- Make Conjugate in the Multi-Configuration Editor; thermal pickups of parameters, several
  temperatures in one configuration, and athermal optimization;
- the parts of OpticStudio's reports listed as not built under Reports (multi-configuration
  data, real F/# per field, POP settings, the report graphic's page arrangement);
- Zemax merit-function, tolerance and configuration data;
- reference results from OpticStudio itself (the cross-checks use open programs only).
