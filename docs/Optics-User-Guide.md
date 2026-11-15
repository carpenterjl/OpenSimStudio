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
configurations choice; the tolerance data file and viewer. Element decenter and tilt tolerances
(TEDX … TETZ and the rolls) cannot be evaluated yet: the ray launcher refuses the decentered lens
they make, and the report names those rows.

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
  their count shown. Trials carrying element decenters and tilts fail for now: the ray launcher
  refuses the decentered lens they make.
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
- Optics sweeps: an `opensim sweep` job whose targets are all `optics:...` sets cells of the saved
  lens at each point and tabulates the saved merit function and each row's value. Targets:
  `optics:surface<n>.radius|curvature|thickness|conic|semiDiameter|tce|index|par<k>|modelNd|modelVd|modelDeltaPgF`
  (surface 0 is the object), `optics:system.apertureValue|temperature|pressure`,
  `optics:wavelength<n>[.weight]` and `optics:field<n>.x|y|weight`. A cell driven by a solve or
  pickup, a surface that does not exist and a property not listed are refused, naming the target.
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

Not built: the macro language (ZPL); plug-ins. The global searches and Monte Carlo were not
tried under JGraph itself; their calls are checked by the test suite.

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
the TOLR operand (a tolerance estimate inside the merit function), HYLD at mirrors, and a
command-line option.

## Coming

Not built yet, and not to be relied on until they appear in this guide:

- the non-sequential object editor;
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
  energy criteria, Monte Carlo tolerancing and the contrast (MTF) wizard criterion are built; the
  full operand catalogue and the encircled-energy merit operands are not);
- Make Conjugate in the Multi-Configuration Editor; thermal pickups of parameters, several
  temperatures in one configuration, and athermal optimization;
- the parts of OpticStudio's reports listed as not built under Reports (multi-configuration
  data, real F/# per field, POP settings, the report graphic's page arrangement);
- lens-file import;
- reference results from OpticStudio itself (the cross-checks use open programs only).
