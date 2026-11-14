# Optics conventions

This is the one written convention for the Optics toolbox. Every optics item follows it, and the
code that implements each rule is named beside the rule. The tests are in
`OpenSim.Tests/Optics/OpticsConventionsTests.cs` and `OpenSim.Tests/Core/RigidTransformTests.cs`;
those of the air and glass index models (section 2) in `OpenSim.Tests/Optics/AirIndexTests.cs` and
`OpenSim.Tests/Optics/ThermalIndexTests.cs`; those of paraxial optics (section 11) in
`OpenSim.Tests/Optics/ParaxialTests.cs`; those of thin-film coatings (section 12) in
`OpenSim.Tests/Optics/ThinFilmTests.cs` and `OpenSim.Tests/Optics/CoatingCatalogTests.cs`; those of the
real ray trace (section 13) in `OpenSim.Tests/Optics/RealRayTraceTests.cs`; those of Gaussian beams (section 14) in
`OpenSim.Tests/Optics/GaussianBeamTests.cs`; those of coordinate breaks and tilts (section 15) in `OpenSim.Tests/Optics/CoordinateBreakTests.cs`; those of ray aiming and vignetting (section 16) in `OpenSim.Tests/Optics/RayAimingTests.cs`; those of aspheres (section 17) in `OpenSim.Tests/Optics/AsphereTests.cs`; those of the idealised and special surfaces (section 18) in `OpenSim.Tests/Optics/IdealSurfaceTests.cs`; those of solves (section 19) in `OpenSim.Tests/Optics/SolveEngineTests.cs`; those of lens data and the editing tools (section 20) in `OpenSim.Tests/Optics/LensDataTests.cs`; those of the polarization ray trace (section 21) in `OpenSim.Tests/Optics/PolarizationTraceTests.cs`; the cross-checks against other programs (section 22) in
`OpenSim.Tests/Optics/ReferenceProgramTests.cs`.

Two rules decide everything below:

- **Geometry follows OpticStudio exactly** (signs, tilt order, normalisation), because `.zmx`
  import depends on it.
- **Physical models do not have to.** Where OpticStudio's choice and the most accurate model
  differ, the most accurate model is the default, an OpticStudio-compatible setting is
  selectable, and the result line names the model in use.

Section 9 lists where each OpticStudio rule was read, and section 10 lists the points that
still need a check against OpticStudio's own output.

## 1. Units

| Quantity | Inside the program | In the editors and in lens files |
|---|---|---|
| Length | meters | lens units: millimeters, centimeters, inches or meters |
| Wavelength | meters, in vacuum | micrometers |
| Tilt | sine and cosine, or radians | degrees |
| Curvature | 1/m | radius in lens units |
| Optical path | meters | waves, or lens units |

- All stored and computed lengths are SI. Lens units exist only at the editor and the file
  reader and writer. `OpticsConventions.MetersPer`, `ToMeters`, `FromMeters`.
- The inch is 0.0254 m exactly.
- The choice of lens units never changes the wavelength unit.
- `OpticsConventions.MicrometersToMeters` changes the unit only. See section 2 for air against
  vacuum.

## 2. Wavelength: vacuum inside, air in OpticStudio files

- A `Ray` carries its **vacuum** wavelength. Vacuum wavelength does not change with the
  medium, temperature or pressure, so it identifies the light without further context.
- OpticStudio's wavelengths are measured in air at the system temperature and pressure (20 °C
  and 1 atm unless changed), and its glass indices are relative to that air.
- So a wavelength in the table (or read from a `.zmx` file) is multiplied by the index of air
  at the system temperature and pressure, evaluated at the vacuum wavelength, to give the
  vacuum wavelength: λvac = n_air(λvac)·λair, solved by fixed-point iteration
  (`AirIndex.VacuumFromAir`, `OpticsEnvironment.VacuumMicrometers`,
  `Wavelength.VacuumMicrometers`). This conversion uses the air model whether or not indices
  are adjusted to the environment. A vacuum (pressure 0) has index 1 exactly.
- **Air index** (OP-13, `OpenSim.Optics/Materials/AirIndex.cs`). Three models, chosen in the
  environment group and saved with the system:
  - **Ciddor** (the default): the Ciddor equation as NIST computes it (Appendix A, A1–A41),
    with CO2 (default 450 µmol/mol, NIST's recommended value) and relative humidity (default 0,
    dry air). Refused outside 0.3–1.7 µm (vacuum), −40 to +100 °C and 10–140 kPa, the ranges
    the NIST text gives.
  - **SCHOTT TIE-19** (the OpticStudio-compatible setting): TIE-19 equations (8) and (9), the
    formula both open AGF readers use. Dry air; it states no range, and is refused only at its
    poles (λ ≤ 1/√41 µm, T ≤ −272.48 °C).
  - **Modified Edlén**: NIST A42–A49, CO2 fixed at 450 µmol/mol by the formula, same ranges as
    Ciddor.

  At 20 °C, 1 atm, dry air with 450 µmol/mol CO2, TIE-19's air index is 3·10⁻⁹ to 1.7·10⁻⁸
  below Ciddor's over 0.4–1.7 µm (measured, `AirIndexTests`); humidity, which TIE-19 does not
  model, moves Ciddor's by about 4·10⁻⁷ at 50 % and 20 °C. The system summary names the model
  in use.
- **Catalog indices** (OP-11, `OpenSim.Optics/Materials`). A glass's dispersion formula takes
  the wavelength in micrometers in the catalog's air and gives the index relative to that air,
  at the catalog's reference temperature (the TD line's last number) and pressure.
  `OpticalMaterial.RelativeIndex` returns exactly that and nothing else.
- **Temperature and pressure** (OP-13, `ThermalIndex`). With *Adjust indices to the
  environment* on, a glass's index follows SCHOTT TIE-19:
  1. the formula is evaluated at the wavelength in the reference air,
     λvac / n_air(λvac, T0, P_ref), giving n_rel(λ, T0);
  2. n_abs(λ, T0) = n_rel(λ, T0) · n_air(λ, T0, P_ref) (equation (5));
  3. n_abs(λ, T) = n_abs(λ, T0) + Δn_abs, with
     Δn_abs = (n² − 1)/(2n) · (D0 ΔT + D1 ΔT² + D2 ΔT³ + (E0 ΔT + E1 ΔT²)/(λ² − λtk²)),
     ΔT = T − T0, λ the vacuum wavelength in µm, n = n_rel(λ, T0) (equations (3), (4));
  4. n_rel(λ, T, P) = n_abs(λ, T) / n_air(λ, T, P) (equation (5)).

  T0 is the TD line's reference temperature; P_ref is 101325 Pa. Both air indices come from
  the selected model, and the reference air takes the system's CO2 and humidity, so at
  T = T0 and P = P_ref the result is the catalog index exactly, and in a vacuum it is the
  absolute index. A glass without a TD line (and every tabulated material) gets no dn/dT and
  T0 = 20 °C, TIE-19's T0. With the setting off, a glass's index is the catalog's at the
  table wavelength, unchanged (`OpticsEnvironment.GlassIndex`).
- A wavelength outside a material's valid range (the catalog's LD line, or the ends of a
  tabulated material's table) is refused with an error naming the glass, the catalog, the
  wavelength and the range. Nothing is extrapolated.
- Tabulated materials interpolate n and k linearly between rows (`TabulatedIndex`); that
  choice is ours, not read from OpticStudio.
- **Shipped material data** (OP-12, `OpenSim.Optics/Materials/RefractiveIndexInfo*.cs`). The
  program ships 52 pages of the refractiveindex.info database (CC0 1.0), embedded unchanged in
  OpenSim.Optics with the database's `LICENSE` and `permission.txt`
  (`OpenSim.Optics/Materials/RefractiveIndexInfo/`, list in `manifest.tsv`; source: the
  database's GitHub repository, branch main, commit e6d15e3, cloned 2026-11-14). They are three
  built-in catalogs named after the database's shelves: **RII-MAIN** (44: fused silica and
  crystal quartz, CaF2, BaF2, SrF2, LiF, MgF2 o/e, sapphire o/e, Si and Ge at 293 K, ZnSe, ZnS,
  KBr, KCl, NaCl, CsI, CsBr, AgCl, diamond, calcite o/e, LiNbO3 o/e, KDP o/e, BBO o/e, YAG,
  spinel, MgO, Y2O3, rutile o/e, and the coating materials HfO2, Ta2O5, Nb2O5, Si3N4, ZrO2,
  AlN o/e, plus water), **RII-GLASS** (4 non-vendor glasses: clear soda-lime, BGG, ZBLAN,
  Pilkington Optiwhite) and **RII-ORGANIC** (4: PMMA, polystyrene, polycarbonate, PET). A
  material is named book-page as the database's `catalog-nk.yml` writes them (`SiO2-Malitson`,
  `Al2O3-Malitson-o`) and carries the page path, the catalog's book and page titles, and the
  page's REFERENCES, COMMENTS and CONDITIONS (`OpticalMaterial.Source`).
  - *Which page.* One page per material (one per axis for a uniaxial crystal): the first page
    the catalog lists for that book, except where the first page does not cover the material's
    use as an optical material, when a later page from the same book is taken: Si and Ge
    (Li 1980 at 293 K, 1.2–14 and 1.9–18 µm, instead of Aspnes 1983, 0.21–0.83 µm), ZnSe
    (Connolly 1979, 0.54–18.2 µm, instead of Marple 1964, 0.48–2.5 µm), crystal quartz, calcite
    and the other crystal axes (the e-axis page beside the o-axis page). Pages whose n falls to
    1 or below inside an absorption band were not taken, because the gate requires n > 1 over
    the whole range: diamond Phillip (Peter 1923 instead), Ta2O5 Bright (Gao 2012 instead),
    Nb2O5 Franta (Lemarchand 2013 instead); the glass shelf's BK7 (Lane, 5–33 µm) and LZOS K108
    (Bassarab) pages were left out for the same reason. Metals are not shipped (their n is
    below 1).
  - *Left out.* The database's `data/specs` shelf (copies of vendor datasheets: SCHOTT, OHARA,
    HOYA, CDGM, Sumita, Hikari and others) is not shipped, by the same policy that ships no
    vendor AGF catalog (OQ-4); the owner may revisit it. Nonlinear-index pages
    (`catalog-n2.yml`, "tabulated n2") are not optical materials here and are refused by the
    reader.
  - *Formulas.* The database numbers its own nine formulas 1–9 (`RefractiveIndexInfoFormulas`,
    from its "Dispersion formulas" sheet, 2014-06-29); they are not OpticStudio's thirteen, and
    the same number means different things in the two lists, so a material records which list
    it uses (`OpticalMaterial.Numbering`). λ is in µm. A page lists only the coefficients it
    uses; the terms it gives are evaluated, and a list that stops inside a term is refused.
  - *Tables.* "tabulated nk" is a `TabulatedIndex`; "tabulated n" is one whose k is not data
    (`HasExtinction` false, `ExtinctionCoefficient` returns null); "tabulated k" beside a
    formula or an n table is an `ExtinctionTable` with its own range, and asking for k outside
    that range is refused, as n is outside the index range.
  - *Reference conditions.* The database states no single convention: whether a page's
    wavelengths are in vacuum or air, and whether n is absolute or relative to air, follow the
    page's own source. The values are used as written, as a catalog index, without conversion.
    The pages carry no TD data, so with *Adjust indices to the environment* on they get no dn/dT
    and T0 = 20 °C like any glass without a TD line; their stated CONDITIONS (temperature in K,
    as the database writes it) are shown, not applied.
  - *The reader* (`RefractiveIndexInfoReader`, on a first-party YAML subset `YamlSubset`, no
    package) reads REFERENCES, COMMENTS or NOTES, DATA, CONDITIONS and PROPERTIES (nd and Vd
    from PROPERTIES go to the self-check). Run over all 3480 data files of the cloned database
    outside its `n2` folders, it reads 3390 and refuses 90, each with the page and line named: duplicate or
    decreasing wavelengths, negative k or n in a table, pages with k only, one page that gives
    n twice (polyvinylpyrrolidone, König) and one whose coefficients stop inside a term
    (AgGaSe2, Boyd-o). No page failed on its YAML. Reading a user's own database page into a
    catalog is not built: user catalogs are AGF files.

## 3. Axes, radius, thickness and mirrors

- The frame is right-handed. Light starts out along **+z**. In a layout seen from the side, y
  is up and x points into the screen.
- **Radius.** Positive when the centre of curvature lies toward +z of the surface vertex. This
  holds whatever the number of mirrors before the surface. A radius of zero or infinity is a
  plane. `OpticsConventions.CurvatureFromRadius`, `RadiusFromCurvature`.
- **Thickness.** The distance along the local z from one vertex to the next. It changes sign
  after every mirror: after an odd number of mirrors every thickness is negative. A coordinate
  rotation of 180° does not change this rule. `OpticsConventions.ThicknessSignAfter`.
- **Mirror.** A surface whose material is `MIRROR`. The space after it has the index of the
  space before it.
- **Index.** Always positive here. The direction of travel is carried by the ray's direction
  vector, and nothing is negated after a mirror. `OpticsConventions.Reflect`.

## 4. Tilts, decenters and the rigid transform

`RigidTransform` (`OpenSim.Core/Numerics/RigidTransform.cs`) is a rotation R and a translation
t. A point p in the local frame sits at **R·p + t** in the parent frame. The columns of R are the
local axes written in parent coordinates. This is the same form OpticStudio reports for a
surface's global vertex: global = offset + R·local.

Rx, Ry, Rz are the right-handed rotations about the positive axes
(`RigidTransform.RotationAboutX` and so on):

```
Rx(a) = | 1    0       0    |   Ry(b) = |  cos b  0  sin b |   Rz(c) = | cos c  −sin c  0 |
        | 0  cos a  −sin a  |           |    0    1    0   |           | sin c   cos c  0 |
        | 0  sin a   cos a  |           | −sin b  0  cos b |           |   0       0    1 |
```

A coordinate break, and a surface's own tilt and decenter, hold a decenter (dx, dy), three tilts
in degrees and an order (`TiltDecenter`, `TiltDecenterOrder`). Tilts are right-handed, and each
is taken about the axis as the earlier tilts left it.

| Order | Steps | Point p of the new frame, in the old frame |
|---|---|---|
| Decenter then tilt (order flag 0) | decenter x, decenter y, tilt about x, the new y, the new z | (dx, dy, 0) + Rx·Ry·Rz·p |
| Tilt then decenter (order flag not 0) | tilt about z, the new y, the new x, then decenter along the tilted x and y | Rz·Ry·Rx·((dx, dy, 0) + p) |

- Written out, Rx·Ry·Rz has the rows (cy·cz, −cy·sz, sy), (cx·sz + sx·sy·cz, cx·cz − sx·sy·sz,
  −sx·cy) and (sx·sz − cx·sy·cz, sx·cz + cx·sy·sz, cx·cy).
- Each order with every sign reversed undoes the other. `TiltDecenter.Reversed`.
- The thickness of a coordinate break is applied last, along the new z, in either order.
- A coordinate break is always relative to the frame of the surface before it.
- Non-sequential objects are placed the same way as "decenter then tilt", with a z decenter as
  well.
- `TiltDecenter.FromTransform` reads the tilts and decenters back from a transform, in either
  order. A tilt and decenter has no z shift, so that part is returned separately. When the y
  tilt is ±90° the x and z tilts turn about the same line, and the z tilt is reported as zero.
- Tilts that are multiples of 90° give exact zeros and ones. `OpticsConventions.SinCosDegrees`.

A surface's tilt and decenter acts as a coordinate break before the surface and another after
it. The "after" values may be given, or picked up from, or the reverse of, this or an earlier
surface's "before" values. Section 15 gives how the tracer applies them (OP-21).

## 5. The ray

`Ray` (`OpenSim.Optics/Ray.cs`):

| Field | Meaning |
|---|---|
| Position | meters, in the frame the holder of the ray is working in |
| Direction | unit vector; its components are the direction cosines (L, M, N). It points the way the light travels, so N is negative after one mirror |
| Wavelength | vacuum wavelength, meters |
| OpticalPath | the sum of index × geometric length so far, meters |
| Intensity | relative power, 1 at launch |
| Field | the electric field (three complex components, same frame as Direction), or nothing when polarization is not traced |
| Status, FailedAtSurface | why and where the ray stopped |

- The optical path grows on every real step. Only a virtual step (back to a vertex plane, or to
  the reference sphere) is negative, and it takes the path back by index × distance.
- A ray that fails is kept, with its status and the surface it failed at. It is never dropped
  silently.

`RayStatus`:

| Status | Meaning |
|---|---|
| Ok | valid so far |
| MissedSurface | the ray's line does not meet the surface |
| TotalInternalReflection | total internal reflection where the ray should refract |
| Vignetted | outside the clear aperture, or inside an obscuration |
| NoConvergence | an iterative intersection or solve did not converge |
| EvanescentOrder | the asked diffraction order does not propagate |

OpticStudio's older interfaces report a miss as the positive surface number, total internal
reflection as the negative surface number, and vignetting as a separate surface number. The
scripting item (OP-98) maps `RayStatus` and `FailedAtSurface` to those codes.

## 6. Optical path difference

- OPD compares a ray with the chief ray of the same field and wavelength. Both paths are
  measured to the reference sphere, which is centred on the image point and passes through the
  centre of the exit pupil.
- **OPD = chief path − ray path.** A positive OPD means the ray's wavefront is ahead of the
  reference sphere. `OpticsConventions.OpticalPathDifference`.
- It is shown in waves of the ray's own wavelength. `OpticsConventions.InWaves`.
- This sign is one of the open checks in section 10.

## 7. Normalised field and pupil coordinates

- **Field (Hx, Hy).** The field point is f = H × F on each axis.
  `OpticsConventions.FieldScale`, `NormalizeField`, `DenormalizeField`.
  - *Radial:* F is the same on both axes, the radial distance of the field point farthest from
    the origin. Fields (10, 0) and (−10, −3) give F = √(10² + 3²). Hx² + Hy² ≤ 1.
  - *Rectangular:* Fx is the largest |x| and Fy the largest |y| of the field points.
    −1 ≤ Hx, Hy ≤ 1.
- **Pupil (Px, Py).** A point on the unit circle. The radius it scales is the paraxial
  entrance-pupil radius, or the stop radius when ray aiming is on. (0, 0) is the chief ray and
  (0, 1) the upper marginal ray. Px² + Py² ≤ 1. With ray aiming off the real ray trace launches
  on the paraxial entrance pupil (section 13); with it on, rays are aimed at the stop
  (section 16). A field's vignetting factors remap (Px, Py) first (section 16).

## 8. Where our default differs from OpticStudio

| Matter | Default here | OpticStudio-compatible setting |
|---|---|---|
| Wavelength carried by a ray | vacuum | air at system temperature and pressure (file values) |
| Index after a mirror | positive, direction in the ray | same results; only the bookkeeping differs |
| Index of air | Ciddor (NIST Appendix A), CO2 450 µmol/mol, dry air | SCHOTT TIE-19 equations (8)–(9) |
| Complex index inside the coating code | n + ik, k ≥ 0 absorbs, time factor e^(−iωt) (as tmm) | n − ik (OpticStudio's coating file writes k negative; the reader and writer change the sign) |
| Coating phases reported | OpticStudio's convention (the conjugate of the internal amplitudes) | same |
| Coating material outside its n, k table | refused | OpticStudio holds the first or last row's values |
| Table coating outside its wavelengths | refused | not stated in OpticStudio's help |

More rows are added by the items that introduce a physical model (OP-63).

## 9. Sources

OpticStudio rules were read from the ZEMAX Optical Design Program User's Manual (Radiant ZEMAX,
8 July 2011; page numbers are the printed ones) and spot-checked against the current Ansys
OpticStudio help, which has the same wording.

| Rule | Where |
|---|---|
| Axes | manual p. 52 |
| Normalised field and pupil coordinates | manual pp. 55–57 |
| Radius sign | manual p. 58 |
| Thickness sign after mirrors, MIRROR material | manual pp. 54, 60, 76 |
| Wavelength in micrometers, in air | manual pp. 62, 104 |
| Surface tilt and decenter, before and after | manual pp. 84–85 |
| Lens units | manual p. 104 |
| OPD reference sphere | manual p. 110 |
| Local to global, columns of R | manual pp. 111–112 |
| OPD definition | manual p. 129 |
| Coordinate break, order flag, right-handed tilts in degrees | manual pp. 293–294 |
| Non-sequential object placement | manual pp. 350, 354 |
| Ray-trace error and vignetting codes | manual pp. 334, 712, 763–764 |
| AGF catalog layout, dispersion formula numbers and expressions, glass status numbers | AGFFileReader.jl (MIT): `src/GlassDownload.jl`, `src/utilities.jl` lines 133–183, `src/constants.jl`; ZemaxGlass.py (MIT): `parse_glass_input`, `get_dispersion` lines 845–888, and its user manual's AGF layout and nomenclature. Not read from Ansys's own page |
| Glass dn/dT, absolute and relative index, TIE-19 air formula | SCHOTT TIE-19, "Temperature Coefficient of the Refractive Index", Version August 2023: equations (2)–(10), P0 = 0.101325·10⁶ Pa (p. 3), catalog values at 0.10133·10⁶ Pa (p. 2), Table 1 (pp. 3–5) |
| Ciddor and modified Edlén air, vapour pressure, humidity, ranges | J. A. Stone and J. H. Zimmerman, NIST Engineering Metrology Toolbox, "Refractive Index of Air", Documentation (emtoolbox.nist.gov/Wavelength/Documentation.asp): Appendix A equations A1–A49 and Table A1; Section IV (ranges, CO2 450 µmol/mol); Section VII (Table 1, Table 2, Ciddor–Edlén agreement). Ciddor's own paper (Appl. Opt. 35, 1566 (1996)) was not read |
| Partial dispersion P_g,F and the normal line ΔP_g,F = P_g,F − (0.6438 − 0.001682·Vd) | ZemaxGlass user manual (zemaxglass_manual.txt, nomenclature, "P" and "dPgF"); AGFFileReader.jl `src/runtime.jl` line 77, crediting SCHOTT TIE-29 (TIE-29 itself not on this machine) |
| Thermo-optical constant G of the athermal glass map | SCHOTT TIE-19 equation (10) and Table 2 (p. 9) |
| Internal transmittance at another thickness | AGFFileReader.jl `src/utilities.jl`, `absorption` (absorption −ln(t)/τ per mm) |
| Coordinate break (six parameters, order flag, thickness applied last, dummy surface, not a mirror or the object, medium of the surface before); surface tilt/decenter before and after with the five after-surface options; coordinate return (none, orientation only, orientation XY, orientation XYZ, "To Surf"); Tilt/Decenter Elements; Local To Global; Global To Local; Add Fold Mirror; Delete Fold Mirror | OpticStudio 2025 R2 help (ansyshelp.ansys.com, pages "Coordinate Break", "Tilt/Decenter", "Tilt/Decenter Elements", "Local To Global", "Global To Local", "Add Fold Mirror", "Delete Fold Mirror"), read 2026-11-14; not saved (the site serves a browser check to plain downloads) |
| Coating file layout (MATE, TAPR, COAT, loops, I.xx, IDEAL, IDEAL2, TABLE, comments), relative thickness d = λ0 T / n0, taper formulas, layer order on surfaces, uncoated mirror 0.7 − 7.0i, coating multiplier limit 10 | OpticStudio 2025 R2 help (ansyshelp.ansys.com, "Defining Coatings" and the pages it links; "Specifying Coatings on Surfaces"; "Properties of Uncoated Surfaces"; "Optimizing Coatings"), saved 2026-11-14 under `plans/sources/optics/coatingfile/` |
| Index sign n − ik, propagation factor e^(−2πiτ/λ), definitions of R, T, A, diattenuation, phase and retardance | OpticStudio 2025 R2 help, "Defining Coatings" and "Definition of Polarization Terms" (same folder) |
| Thin-film amplitude convention and the cross-check values | tmm 0.2.0 (S. Byrnes, MIT), `tmm_core.py`: `interface_r`, `interface_t`, `coh_tmm`, `T_from_t`, `power_entering_from_r` (`plans/sources/optics/tmm/`) |
| What OpticStudio-compatible readers do with TD data | AGFFileReader.jl `src/utilities.jl` (`index`, `absairindex`); ZemaxGlass.py `get_dispersion` (lines ~780–917) |
| refractiveindex.info formulas 1–9 and their names | M. Polyanskiy, "Dispersion formulas", RefractiveIndex.INFO, 2014-06-29 (`database/doc/Dispersion formulas.pdf` in the database repository; one page) |
| refractiveindex.info page layout, book and page ids and titles, licence | The database files themselves (`database/data`, `catalog-nk.yml`, `LICENSE`, `doc/permission.txt`), commit e6d15e3; its schema test `tests/test_yaml.py`. The database's own Python scripts (GPL) were not used |

The manual gives the tilt rules in words and has no matrices. The written-out matrix in section
4 follows from those words, and agrees with two independent write-ups of what the program
reports: a Zemax community thread on the rotation-matrix article
(community.zemax.com/zos-api-12/problem-with-rotation-matrix-article-26) and Prime Optics'
"Coordinate System Alignment and ZEMAX Coordinate Breaks"
(primeoptics.com.au/Reference/CoordinateSystemAlignment_ZEMAX.pdf).

## 10. Not yet checked against OpticStudio output

These are settled from the manual's wording but not yet from the program's numbers. Each is a
single place in the code, so a correction is one line and its test.

1. **Sign of the x and y tilts.** The manual says all three are right-handed, and the two
   write-ups above agree. One open-source reader (RayOptics, `misc_math.py`, `euler2opt`)
   negates the x and y tilts on import, which reads as the opposite sense. Check: one
   coordinate break with three unequal tilts, compared with OpticStudio's global-vertex listing.
   Planned with OP-21 and OP-102.
2. **Sign of OPD.** The manual defines OPD as a difference between the ray's path and the chief
   ray's without an equation. "Positive means ahead" is taken from a third-party note (the POPPY
   documentation), not from Zemax. Check: the sign of the OPD fan of a singlet with known
   spherical aberration. Planned with OP-35 and OP-102.
3. **Error codes of the current scripting interface.** The positive/negative surface-number
   rule is documented for the older interfaces only. Planned with OP-98.
4. **Dispersion formula 4 (Sellmeier 2).** The two readers above disagree on its second term:
   AGFFileReader has C4 λ²/(λ² − C5²), ZemaxGlass C4/(λ² − C5²). The code uses AGFFileReader's
   form (`DispersionFormulas.Evaluate`, marked unverified), and the AGF reader names every glass
   that uses formula 4 in a note. No glass in the 23 catalogs read for OP-11 uses it. Check: one
   glass with formula 4 and C4 ≠ 0, its index compared with OpticStudio's.
5. **Thermal index: where the two open readers and TIE-19 differ** (OP-13). The code follows
   TIE-19 in each case; none is confirmed against OpticStudio. Check: one glass with TD data at
   40 °C and 0.8 atm in OpticStudio with its TIE-19 air, the index compared to 10⁻⁸.
   - *Sign of λtk.* TIE-19 and ZemaxGlass write λ² − λtk². AGFFileReader writes λ² − S·λtk², S
     the sign of λtk. They differ only for a negative λtk; of 2029 TD lines in the 24 AGF files
     under `plans/sources`, two real glasses have one (lightpath.agf, D-ZLAF52LAM and
     D-ZLAF52LA_M) besides one test glass.
   - *Wavelength in equation (3).* TIE-19 says the vacuum wavelength. AGFFileReader uses the
     wavelength in the reference air; ZemaxGlass the wavelength as given (in the system's
     air). λ² differs by about 5·10⁻⁴ of itself, which moves only the E0, E1 term of Δn.
   - *Air index at which wavelength.* TIE-19 (9) and NIST take the vacuum wavelength, and so
     does this code. AGFFileReader evaluates its air at the air wavelength.
   - *Referring to air.* AGFFileReader converts to absolute and back with the air at the
     reference and system conditions, as TIE-19 (5) does, and scales the formula wavelength by
     the ratio of the two air indices. ZemaxGlass adds Δn_abs to the relative index and uses no
     air index for a glass at all, so its glass indices do not change with pressure.
   - *Reference pressure.* TIE-19 gives P0 = 0.101325·10⁶ Pa for the air formula (p. 3) and
     says catalog indices apply at 0.10133·10⁶ Pa (p. 2), which is P0 rounded to five figures.
     101325 Pa is used, as AGFFileReader does (1 atm). Read literally, 101330 Pa would move a
     relative index by about 1.3·10⁻⁸. ZemaxGlass uses 1.0113·10⁵ Pa for its AIR material,
     which matches neither.
   - *TIE-19 equation (10).* Its constants (0.00367, T) are not the derivative of equation (8)
     (3.4785·10⁻³, T − 15); the two agree to 3·10⁻⁶ of the value from −40 °C to 100 °C. Only
     (8) enters the index; (10) is provided as the source writes it.
   - *Range of the dn/dT constants.* TIE-19 states SCHOTT's constants valid from −100 °C to
     +140 °C and 0.365–1.014 µm. Other vendors' TD lines state no range. No range is enforced
     on Δn: like the open readers, the code applies the constants anywhere.
   - *TIE-19 Table 1.* With the TD constants of the schott.agf shipped with ZemaxGlass, the
     model reproduces Table 1's relative coefficients (+20/+40 °C, e-line) for N-BK7, N-PK51
     and F2 to 0.03·10⁻⁶/K, SF57 to 0.15 and N-LAF2 to 0.09·10⁻⁶/K. Whether that file's
     constants are older than the 2023 table is not known.

6. **Paraxial results with mirrors and theodolite fields** (OP-19). Section 11's effective
   focal length is 1/power, positive for a converging mirror; image-space positions after an
   odd number of mirrors are negative. Whether OpticStudio's System Data lists the same signs
   for a mirror system, where it measures cardinal points from, and its theodolite-angle
   formula, have not been read at a source. Check: a single concave mirror and a field of
   azimuth 3°, elevation 4°, compared with OpticStudio's System Data and Paraxial Ray Data.

7. **Model glasses and glass offsets** (OP-14). OpticStudio's model-glass formula and its glass
   offset rule are not published or not read. The model glass here
   (`OpenSim.Optics/Materials/Tools/ModelGlassFit.cs`) is a constrained fit over the active
   catalogs that reproduces the requested nd, Vd and ΔP_g,F exactly; the offset
   (`GlassOffsets.cs`) is an affine map of the index that moves nd and Vd and keeps every
   relative partial dispersion. Both are this program's choices. Check: a model glass and an
   offset glass compared with OpticStudio's indices at the F, d, C and g lines and at 1 µm.

8. **Coatings** (OP-15). Settled from OpticStudio's help pages, not from its numbers; no coating
   file written by OpticStudio itself was available. Check each with one coating in
   OpticStudio's coating plots.
   - *Sign of p reflection.* OpticStudio's reflection retardance φp − φs + π implies that its
     r_p is −r_s at normal incidence, as tmm's and this code's are; the help does not state it.
   - *Diattenuation in reflection.* The help defines D with T_s and T_p; the reflection plot
     here uses the same form with R_s and R_p.
   - *Polynomial taper terms.* The help lists E_0 to E_6 and "etc."; the terms beyond E_6 follow
     the visible pattern (degree by degree, falling power of x′). Its sum for f_p runs to 22
     while the PT term range stops at 20; 20 is used.
   - *Coating multiplier and index offsets.* The help ("Optimizing Coatings") calls them a
     linear thickness scale and dimensionless offsets of the index and extinction coefficient;
     the extinction offset is added to k in this code's sign (positive absorbs more), which
     OpticStudio's sign convention may reverse. The surface coating tab that holds them was not
     read.
   - *Table coatings.* Phases interpolate as given (not unwrapped); under total internal
     reflection a table coating reflects everything with phase π (the help does not say).
   - *IDEAL2 amplitudes* with |r|² + |t|² above 1 are refused; the help does not say what
     OpticStudio does.

9. **Real ray trace choices** (OP-20). Each is this program's reading of an OpticStudio name or a
   choice of its own; none is compared with OpticStudio's output. Check each with one small lens
   traced in both programs.
   - *Spider:* arms start at the centre, the first along +x, the others evenly spaced toward +y;
     arm width is the full width.
   - *Ignore this surface:* the ray crosses the vertex plane unchanged (no shape, aperture or
     change of medium), and the thickness still counts.
   - *Skip rays to this surface:* read as a drawing option only; the trace is unchanged.
   - *Chip zone:* the mechanical semi-diameter is the larger of the given one (or the clear one)
     and clear plus chip zone.
   - *Automatic semi-diameters:* the chief ray and rim rays of the paraxial pupil (four by
     default), every field and wavelength, apertures ignored; margins (h + margin)·(1 + %/100)
     as OP-18's record states. OpticStudio's own sampling was not read.
   - *Launch plane* for an object at infinity: the plane through surface 1's vertex at right
     angles to the field's direction. It moves absolute optical paths, not differences between
     rays of one field.
   - *Pupil wavelength:* the paraxial entrance pupil of the primary wavelength places the rays at
     every wavelength.
   - *Far sheet:* a ray that meets a conic only beyond the half that holds the vertex (the far
     hemisphere of a sphere, the other sheet of a hyperboloid) is a miss; OpticStudio's
     hyperhemispheric surfaces are not built.

10. **Gaussian beam signs and inputs** (OP-48). Section 14's sign rules (the waist position
    measured along z from the surface, the wavefront radius positive when diverging, the
    input waist position measured from surface 1, an M² input waist being the real beam's)
    and its "Gouy phase accumulated from surface 1" are this program's choices; OpticStudio's
    Paraxial Gaussian Beam help was not read. Check: one singlet and one concave mirror with a
    beam of known waist and M² = 2, compared with OpticStudio's Paraxial Gaussian Beam listing.

11. **Coordinate breaks, tilts and returns** (OP-21). Section 15 follows the help pages'
    wording; no lens file exported by OpticStudio and no OpticStudio Global Vertex listing was
    available, so none is compared with the program's numbers. Check: one coordinate break with
    three unequal tilts and decenters in each order, one surface with before and after tilts
    using each after-option, one return of each kind, and Add Fold Mirror with a reflect angle
    of 60°, compared with OpticStudio's Global Vertex listing and Single Ray Trace.
    - *Tilt signs (item 1).* Read as the help says: right-handed about the positive axes, each
      about the axis as the earlier tilts left it. So a positive tilt about x turns +y toward
      +z, and the vertex a thickness t after a break of +90° about x is at (0, −t, 0); about y it
      is at (t, 0, 0) (`CoordinateBreakTests.TiltSigns_PlaceTheNextVertexAsTheRightHandRuleSays`).
      RayOptics' negation of the x and y tilts on import remains the one contrary reading.
    - *Coordinate return, Orientation XY.* The help says the x and y decenters are solved "to
      restore the X and Y vertex offset". Read here as: the break's vertex lies on the target
      surface's z axis (zero offset along the target's own x and y, which the break's axes then
      are). With Orientation only the entered decenters and thickness are kept; with
      Orientation XY the entered thickness is kept.
    - *The return target's frame* is the target surface's own frame, after its own "before"
      tilt. A return to the object surface is refused.
    - *Reflect angle* of Add Fold Mirror ("the angle between the incident and reflected beam")
      is read as the angle between the two beams at the mirror, so each break tilts by half of
      it and 90° makes a right-angle fold; a positive angle turns the beam toward +y (about x)
      or −x (about y). OpticStudio's second break is a pick-up of the first; here it is a copy.
    - *Reversing later surfaces* after Add or Delete Fold Mirror changes thickness and curvature
      (as the help says) and also the x and y tilts of later breaks and surface tilts (the help
      is silent; it is what keeps the rays on the same path). Delete Fold Mirror removes the
      mirror itself as well as its breaks and adds the thicknesses to the surface before.
    - *Tilt/Decenter Elements* inserts a dummy at the group's start, the pivot and tilt breaks,
      then after the group a return (orientation XYZ) to the dummy and one break that restores
      the original step to the next surface. The help names the surfaces it inserts but not
      their values, so the layout is this program's; the pivot is taken in the frame before the
      first surface's own tilt.
    - *Local To Global* gives each group a return to the reference, a position break and a
      rotation break (the help's three); *Global To Local* keeps the thickness of the surface
      before a run of breaks and puts the rest of the step in the one break that replaces it.
    - *Paraxial data of a tilted system.* `SequentialLens.ToParaxialLens` unfolds a lens whose
      tilts only fold the axis with plane mirrors and refuses any other tilted or decentered
      lens. What OpticStudio's paraxial data do for a non-axial system was not read.
    - *A ray parallel to a break's plane* is left where it is; OpticStudio's behaviour there was
      not read.

12. **Ray aiming and vignetting choices** (OP-22). Each is this program's reading of the
    OpticStudio help or a choice of its own (section 16); none is compared with OpticStudio's
    output. Check: one pupil-aberrated singlet with ray aiming paraxial and real, one field
    with vignetting factors, and Set Vignetting on a lens with one clipping aperture, each
    compared with OpticStudio's single ray trace and field data.
    - *Pupil shift and compress:* how they enter the first guess (x and y shifts scaled with
      the radial normalised field only when asked, the z shift always; compress multiplies the
      pupil coordinates by 1 − c), and the default of "scale by field" (off; the help does not
      give one).
    - *Robust aiming:* continuation from the axis and the virtual-segment check are this
      program's; OpticStudio's robust algorithm is not described.
    - *Interpolation of vignetting factors* between fields: linear in Hy when every field is on
      the y axis, the nearest field otherwise.
    - *Set Vignetting:* edges searched along the lines through the centre, the centre moved to
      the midpoints; the angle set to zero.
    - *Replace Vignetting With Apertures:* circular apertures at the largest height of the
      vignetted pupils' rim rays; the help does not describe OpticStudio's algorithm.
    - *Vignetting Plot with the factors kept:* the fraction is multiplied by
      (1 − VCX)(1 − VCY).
    - *Real stop radius when the marginal ray fails:* the paraxial radius, with a note.


13. **Asphere readings** (OP-23). Section 17's forms follow the OpticStudio 2025 R2 help pages
    where they give an equation; these points are this program's reading and are not compared
    with OpticStudio's output:
    - *Alternate Even / Alternate Odd:* "the alternate solution" is read as the base conic's
      other root, refined on the asphere's sag; it must lie on the sheet that holds the vertex.
    - *Odd Cosine:* θ = atan2(y, x); the slopes of the odd and cosine terms are taken as zero
      on the axis.
    - *Qbfs with a conic:* the help cites Forbes 2011 Eqs. 2.1–2.4, which carry the conic κ
      through 1/σ = √(1 − κc²r²)/φ; that is what is built. Whether OpticStudio evaluates
      exactly this with κ ≠ 0 was not checked.
    - *Convert Asphere Types:* the fit (samples, equal weights, Qbfs's curvature from the conic
      through the edge point) is this program's; the help says only "least squares".
    - *Paraxial curvature* (TC-6, section 17): cone terms (β1 r, Extended Odd's α1 ρ, cosine
      terms with P = 1) are left out of first-order data; an Odd Cosine P = 2 term with odd B
      contributes the mean of its ±y halves; the Alternate forms use their sag's vertex
      curvature; the Periodic surface its y–z value. The help does not describe the paraxial
      trace of any of these.
    Check: each form with non-zero terms, one ray traced in both programs, and the EFL of a
    lens with a non-zero r² term and with a non-zero β1 in both programs.

14. **Idealised and special surfaces** (OP-27). Section 18's surfaces follow their OpticStudio
    2025 R2 help pages; these points are this program's reading where a page is silent, and
    none is compared with OpticStudio's output.
    - *Paraxial OPD:* modes 2 and 3 are built as exact path terms (section 18); OpticStudio's
      own formulas are not published, so its OPD numbers may differ in the residuals. Modes 0
      and 1 are not built.
    - *Paraxial XY OPD:* the paraxial thin-lens path −(φx·x² + φy·y²)/2, flagged approximate.
    - *ABCD:* ω read as the slope L/N; the matrix ignores the indices; the ray leaves from the
      moved point (x′, y′).
    - *Extended Fresnel:* "accounts for both" read as the normal of z_s + z_f.
    - *Lenslet Array:* built from the help's description of US_ARRAYEVEN (odd nx × ny cells,
      even-asphere lenslets), not from the DLL; a ray outside the cells misses.
    - *Paraxial engine:* anamorphic surfaces (Paraxial XY, ABCD, Cylinder Fresnel, Generalized
      Fresnel) give their y-z behaviour.
    Check: one lens per surface type traced in both programs (ray positions, directions, and
    OPD for the Paraxial surface at finite and infinite conjugates).

15. **Solves** (OP-29). Each solve follows the wording of OpticStudio's help (section 19); none
    is compared with OpticStudio's numbers. The readings that the help leaves open:
    - *Edge thickness at radial height 0* uses the mechanical semi-diameter, as "Thickness
      Solves" says; the solve summary table says the clear semi-diameter.
    - *Aplanatic* is u′/n′ = u/n of the paraxial marginal ray. The help says only "aplanatic
      with respect to the paraxial marginal ray".
    - *Concentric with surface* puts the centre of curvature on the other surface's vertex.
    - *F/#* sets the exit slope to −1/(2F) literally, with no index and whatever the number of
      mirrors.
    - *Pupil position* uses the paraxial chief ray. The help traces real differential rays, and
      the two agree on a lens with no tilts.
    - *Optical path difference* uses a reference sphere centred on the chief ray's image-surface
      intercept and passing through the paraxial exit pupil, and ignores apertures.
    - *Real marginal ray height* ignores apertures.
    - *Element power* and *ignored* cases are refused with a message where OpticStudio
      ignores the solve.
    - *Pickups* between curvature and length columns are refused, because the help does not
      say how one becomes the other.
    - *Material pickup* copies the MIRROR flag with the medium.
    Check: one lens with one solve of each kind, compared with the values OpticStudio's
    lens data editor shows.

16. **Lens data and editing tools** (OP-31). Section 20's readings of OpticStudio's help are
    not compared with OpticStudio's output: the rule Reverse Elements and Make Double Pass
    follow (derived from section 3's signs, the help gives none), which solves and pickups they
    remove, the surfaces Set All Radii / Thickness Variable choose ("boundary between two
    different media" read as a different material entry, MIRROR included; the object distance
    left out), Scale Lens leaving the system data unscaled, Convert to Maximum Apertures sizing
    one configuration, the inserted surface being Standard (the help says "OFF", an ignored
    surface), and System Check's list of checks (the help does not list OpticStudio's). TC-6's
    readings are not compared either: parameters 13 on saved as extra data, the maximum term
    numbers of the Q-type and the Superconic, the units of the ABCD matrix, a type change
    resetting the parameters, and pickups converted between columns of different units.
    Check: each tool on one small lens in both programs, compared with OpticStudio's
    prescription, and one lens of each surface type saved by OpticStudio and read back.

17. **Polarization ray trace choices** (OP-51). Section 21's rules are derived or read from
    OpticStudio's help, but none is compared with its output. Check each with a coated singlet
    and a fold mirror traced in both programs (Polarization Ray Trace listing):
    - *Power-normalised amplitudes:* the field carries √T and √R, so |E|² is the ray's power;
      OpticStudio lists "field" and "ray" amplitude coefficients whose normalisation was not
      read.
    - *Ray-equivalent phase:* t·e^(−iψ) and r·e^(−2iψ), ψ = 2π·n0·D·cos θ0/λ, derived from the
      help's description, not from a formula it states.
    - *Default Jones vector* (1, 0), polarized: OpticStudio's default was not read.
    - *IT interpolation:* α linear in wavelength between IT points, refused outside them;
      OpticStudio's interpolation and its behaviour outside the table were not read.
    - *Launch segment of an object at infinity:* no bulk absorption (the launch plane is
      arbitrary).
    - *Image surface and ignored surfaces:* no interface, so no coating is applied there.
    - *IDEAL coatings on mirrors:* their equal s and p reflection phases give a reflection
      retardance of 180° in the frame where a bare interface has r_p = −r_s (as the coating
      plots already report); whether OpticStudio applies them so was not read.
    - *Phase difference* in the pupil map is Ey's phase minus Ex's; the help's wording does not
      fix the order.

18. **Cross-checks are against open programs, not OpticStudio** (OP-102). Section 22's
    reference files come from Optiland, RayOptics and tmm. They check the real ray trace, the
    paraxial data and the thin-film engine against independently written code using the same
    sign rules, but OpticStudio itself was not available, so no number here is claimed to
    agree with OpticStudio's. The open checks above stay open. Check: the five lenses of
    `OpenSim.Tests/References/OP-102/lenses.py` in OpticStudio (Single Ray Trace and System
    Data), stored beside the others as OpticStudio reference files when access exists.

## 11. Paraxial optics

The paraxial engine (OP-19, `OpenSim.Optics/Paraxial/ParaxialSystem.cs`) traces y-nu rays
through a `ParaxialLens`: surface 0 is the object (its thickness the object distance, possibly
+infinity), the last is the image surface, and exactly one surface between is the stop. Each
surface has a curvature, a thickness, a medium after it (a constant index or an
`OpticalMaterial`), and stop and mirror flags. The curvature of the object and image surfaces
is ignored: heights there are in the vertex plane.

- **Signed index.** Indices stay positive in the data (section 3). Inside the paraxial trace
  each space's index is multiplied by −1 per mirror before it. With the thickness sign of
  section 3 the reduced thickness t/n is then positive in every space, and one pair of
  equations covers lenses and mirrors: transfer y′ = y + (t/n)·ν, surface ν′ = ν − y·φ,
  φ = c·(n′ − n). A mirror in air has φ = −2c, positive for a concave mirror (c < 0 when it
  faces light travelling +z).
- **Slopes.** u = dy/dz in the system's frame; z does not turn round at a mirror. ν = n·u with
  the signed index. The ABCD matrices act on (y, ν) and have determinant 1
  (`ParaxialMatrix`).
- **Indices** are relative to the system's air at the table wavelength
  (`OpticsEnvironment.GlassIndex`), so they follow the "adjust indices to the environment"
  setting. A constant index is the same at every wavelength.
- **Positions** are signed distances along z. Object-space ones (front focal point, front
  principal and nodal points, entrance pupil) are measured from surface 1's vertex;
  image-space ones (back focal length, rear principal and nodal points, exit pupil, paraxial
  image) from the last surface's vertex. With the system matrix [[A, B], [C, D]] from just
  before surface 1 to just after the last surface, power Φ = −C, object-space index n and
  signed image-space index n′: EFL = 1/Φ, f′ = n′/Φ, f = −n/Φ, BFL = A·n′/Φ, FFL = −D·n/Φ,
  front principal plane (1 − D)·n/Φ, rear (A − 1)·n′/Φ, front nodal point (n′ − D·n)/Φ, rear
  (A·n′ − n)/Φ.
- **Afocal.** A system is afocal when |C| is within 32·(surfaces + 2)·2⁻⁵² of the sum of
  the absolute values of the terms that make it up (the product of the absolute-value
  matrices), i.e. zero within its rounding. Then there is no focal length, cardinal point or
  image-space F-number, and the angular magnification ū′/ū is D·n/n′. The same test decides
  a pupil or an image at infinity.
- **Entrance pupil.** With the matrix from surface 1 to the stop's vertex, the pupil is at
  z_E = B·n/A from surface 1; A = 0 (within rounding) puts it at infinity. *Telecentric
  object space* puts it at infinity by definition: chief rays enter parallel to the axis.
- **Aperture** (`ApertureType`): the entrance pupil diameter; the image space F/# as
  |EFL|/EPD (at whatever conjugates); the object space NA, which launches the marginal slope
  tan(asin(NA/n)); the object cone angle θ, slope tan θ; float by stop size, the marginal ray
  reaching the stop's semi-diameter; the paraxial working F/# 1/(2·n′·|u′|). NA and the
  cone angle need an object at a finite distance; EPD and the image space F/# need the pupil
  not at infinity. Results report NA = n·sin(atan |u|).
- **Fields** (`ParaxialSystem.TraceChiefRay`): an angle θ launches the chief slope tan θ
  through the entrance-pupil centre; a theodolite field (azimuth x, elevation y) the
  direction (cos el·sin az, sin el, cos el·cos az), slopes tan az and tan el / cos az; an
  object height the ray from the object point through the pupil centre (parallel to the
  axis when the pupil is at infinity); a paraxial image height the chief ray reaching that
  height on the image surface. Real image height is defined by the real chief ray and is
  refused here; the real ray launcher solves it (section 16). The x and y planes are traced
  independently.
- **Marginal and chief rays.** The marginal ray runs from the axial object point to the top
  of the entrance pupil; the chief ray of the reports is that of the largest y field
  (Hy = 1). Lateral magnification is ν/ν′ of the marginal ray (zero for an object at
  infinity); angular magnification ū′/ū of the chief ray; the Lagrange invariant
  H = n·(ū·y − u·ȳ) with the signed index, the same in every space.
- The reports' signs and reference points are ours; section 10, item 6, lists the check
  against OpticStudio.

## 12. Thin-film coatings

`OpenSim.Optics/Coatings` (OP-15). The engine is `ThinFilm`, the coating model
`CoatingCatalog`, and `CoatingEvaluator` applies a coating to a surface.

**Index and time convention.** Inside the code a complex index is N = n + ik with k ≥ 0 for an
absorbing material, and a plane wave is e^(i(k·r − ωt)), so a forward wave in an absorbing
layer decays with depth. This is tmm's convention (`tmm_core.py`, `coh_tmm`: kz "positive
imaginary part means decaying"). OpticStudio uses the conjugate one: n − ik, k written
negative in its coating file, propagation factor e^(−2πiτ/λ) ("Defining Coatings",
"Definition of Polarization Terms"). The coating file reader stores k = −(written value) and
refuses a positive written value (gain); the writer writes −k.

**The calculation** (`ThinFilm.Compute`). With β = n0 sin θ0 (n0 real):

- each medium j: q_j = N_j cos θ_j = √(N_j² − β²), the root with Im q ≥ 0 (the forward,
  decaying wave; for k ≥ 0 the principal root);
- tilted admittances η_j = q_j for s and N_j² / q_j for p; phase thickness δ_j = 2π q_j d_j / λ;
- writing a layer's tangential fields as E = A e^(iκz) + B e^(−iκz), H = η(A e^(iκz) − B e^(−iκz)),
  κ = 2π q / λ, and eliminating A and B between its two faces gives
  [E; H]_front = [[cos δ, −i sin δ / η], [−i η sin δ, cos δ]] [E; H]_back;
- [B; C] = Π_j M_j [1; η_s] from the incident side; ρ = (η0 B − C)/(η0 B + C),
  τ = 2η0 / (η0 B + C) (tangential E);
- R = |ρ|², T = 4 η0 Re(η_s) / |η0 B + C|², A = 4 η0 Re(B C* − η_s) / |η0 B + C|². Each is
  computed on its own, so R + T + A = 1 is a check (the tests hold it to 10⁻¹² in every case,
  absorbing or not).

Each M_j is multiplied by e^(iδ_j) (modulus ≤ 1), which turns its entries into (1 ± e^(2iδ))/2,
bounded for any thickness; the product of the factors is carried apart and restores B and C. An
opaque layer therefore gives T = 0 exactly instead of an overflow. Nothing is clamped (tmm caps
Im δ at 35).

**Amplitudes** returned by `ThinFilm` are tmm's: s amplitudes are the tangential ratios;
p reflection is r_p = −ρ_p, so r_p = −r_s at normal incidence; p transmission is the full-field
ratio τ_p cos θ0 / cos θ_s. With these, r, t, R and T equal tmm's `coh_tmm` output (to 10⁻¹³ in
`ThinFilmTests`), and tmm's absorbed fraction (power_entering − T) equals A. tmm also accepts an
absorbing incident medium; this code refuses one, since one plane wave does not define the
incident power there.

**Reported quantities** (`CoatingResponse`), as OpticStudio's help defines them:

- the phase φ of each amplitude in OpticStudio's convention, φ = −arg(amplitude in ours),
  wrapped to (−180°, 180°];
- retardance in transmission φp − φs, in reflection φp − φs + 180°, wrapped; zero at normal
  incidence for any stack;
- diattenuation |T_s − T_p| / (T_s + T_p), and the same with R for reflection (zero when the
  sum is zero);
- A = 1 − R − T for ideal and table coatings (they have no physics to compute it from).

**Coatings and surfaces** (OpticStudio help, read): layers are listed from the incident side
and the substrate is not; from glass to air the order is reversed; on a mirror the last layer
is the semi-infinite substrate, and an uncoated mirror is aluminium 0.7 − 7.0i (0.7 + 7.0i here);
on a mirror everything not reflected counts as absorbed (this code's choice). A relative
thickness is in waves of the primary wavelength in the layer, d = λ0 T / n0, n0 the real part
of the layer's index there (before index offsets). Ideal (IDEAL, I.xx, IDEAL2) and table
coatings follow their help pages (`Coating.cs` names each rule); a table interpolates linearly
in cos θ between angles, holds the end angles' data beyond them, and is read at the refracted
angle from high to low index.

**Wavelength and media.** Coating material tables, like catalog glasses, are in µm in air and
give indices relative to air; a relative index with the air wavelength gives the same phase
thickness as the absolute index with the vacuum wavelength, so no conversion is made. The
coating plots use catalog indices as they are; they do not adjust them to the environment.

**Not built.** Incoherent thick layers; coating design or optimisation (OpticStudio has none
either). Coatings on surfaces and on traced rays are section 21's.

## 13. Real ray trace

The real ray trace (OP-20, `OpenSim.Optics/Sequential`) traces rays through a `SequentialLens`:
the surfaces of section 11 with a conic constant, an aperture, clear and mechanical
semi-diameters, a chip zone, and the flags *ignore this surface* and *skip rays to this
surface*. `SequentialLens.ToParaxialLens` gives the same lens to the paraxial engine (conics and
apertures do not enter first-order optics; an ignored surface becomes a plane with the medium
before it). Spencer and Murty (JOSA 52, 1962) and Welford were not on hand; every formula below
is derived in `ConicIntersection.cs` and the gate tests, not copied.

- **Frames.** A launched ray is written in the global frame: object space's axes, origin at
  surface 1's vertex before surface 1's own tilt (surface 1's local frame when it has none).
  Surface j's frame is placed in surface j − 1's by one transform
  (`SequentialTrace.SurfaceStep`, built by `SurfacePlacement`, section 15): the previous
  surface's "after" tilt, its thickness along z, then this surface's "before" tilt or
  coordinate break. Global positions are in the global frame. Signs are those of
  section 3: the frame's z never turns round at a mirror, thicknesses after an odd number of
  mirrors are negative, and a ray's direction carries the sense of travel (N < 0 after one
  mirror).
- **Surface.** F = c·(x² + y² + (1 + k)·z²) − 2z = 0, curvature c, conic k (0 sphere, −1
  paraboloid, < −1 hyperboloid, −1 to 0 prolate, > 0 oblate). Only the sheet that holds the
  vertex is a surface: 1 − c(1 + k)·z > 0.
- **Loop.** For each surface: transfer to the vertex plane z = 0 (distance −z/N, negative for a
  virtual step back); intersect by the rationalised root s = −C/(B − sign(N)·√(B² − AC)) with
  A = c(L² + M² + (1 + k)N²), B = c(xL + yM) − N, C = c(x² + y²), which has no cancellation for
  the vertex root and is finite for a plane and an axial paraboloid; test the aperture; refract
  or reflect with the unit normal (−cx, −cy, 1 − c(1 + k)z)/|…|.
- **Refraction.** d′ = μ·d + Γ·n̂, μ = n/n′, cos I = d·n̂ ≥ 0, sin²I = |d × n̂|²,
  cos T = √(1 − μ² sin²I), Γ = (1 − μ)(1 + μ)/(cos T + μ cos I). This conserves
  n·(d × n̂), which is Snell's law. μ² sin²I > 1 is total internal reflection.
  **Reflection** is `OpticsConventions.Reflect`; the index after a mirror is the one before.
- **Optical path.** Each segment adds n·s, n the index before the surface relative to the
  system's air (the paraxial engine's indices) and s the signed geometric length, so the path is
  the absolute optical path divided by the air index; in waves divide by the table (air)
  wavelength. A ray carries its vacuum wavelength (section 2). The batch trace returns each ray's
  path minus its field's chief ray's, summed segment by segment (`TracedRay.PathFromChief`),
  which keeps the digits a difference of two totals would lose. OPD against the reference sphere
  is OP-35.
- **Launch** (`RayLauncher`), ray aiming off: the pupil point (Px·r, Py·r) lies on the paraxial
  entrance pupil of the primary wavelength (radius r, position z_E). The field point f = H·F goes
  through the paraxial chief ray (`ParaxialSystem.TraceChiefRay`), whose object-space slopes are
  the exact tangents of an angle field and whose object heights are exact for a height field.
  Object at infinity: direction (ūx, ūy, 1) normalised, through the pupil point, starting on the
  plane through surface 1's vertex at right angles to it. Finite object: from the object point
  (x̄, ȳ, −t0) to the pupil point; with the entrance pupil at infinity, direction (Px·u, Py·u, 1),
  u the paraxial marginal slope. The object surface is taken as a plane. Ray aiming, vignetting
  factors and real image height are section 16.
- **Apertures** (`SurfaceAperture`) are tested at the intersection, on the local x and y:
  circular (minimum and maximum radius), rectangular (half-widths), elliptical (semi-axes),
  spider (arm width and count), user-defined polygon (even-odd rule) and floating (the clear
  semi-diameter); each may be decentered and made an obscuration. A surface without an aperture
  never vignettes, whatever its semi-diameter.
- **Failures.** A miss (no real root, the far sheet, or a ray parallel to the vertex plane),
  total internal reflection and an aperture stop the ray with `RayStatus` MissedSurface,
  TotalInternalReflection or Vignetted and the surface number; the ray is returned, never
  dropped, and `TracePath` keeps its hits up to that surface.
- **Semi-diameters** (`SemiDiameterSizing`): a fixed clear semi-diameter is kept; an automatic
  one is the largest √(x² + y²) the chief ray and the pupil-rim rays of every field and
  wavelength reach (apertures ignored), then (h + margin)·(1 + percent/100). Mechanical is the
  larger of the given one (or the clear one) and clear plus chip zone.
- **Batch** (`BatchTracer`): fields × wavelengths × pupil points; each field and wavelength is
  one `ParallelSlots` work item, so the result is bitwise the same at any degree of parallelism.
- The choices not read from OpticStudio are listed in section 10, item 9.

## 14. Gaussian beams

`OpenSim.Optics/Paraxial/GaussianBeam.cs` (OP-48) carries a fundamental-mode Gaussian beam
through the paraxial model of section 11. The equations are derived from the definitions
q = z + i·z_R and 1/q = 1/R − i·λ/(π·w²) in the comments of `GaussianBeamTests`; Siegman's
*Lasers*, the plan's reference, is not on disk and nothing is copied from it.

- **Reduced beam parameter.** The engine carries q̂ = q/n with n the signed index of the
  space (section 11). Because the ABCD matrices act on (y, n·u), q̂ = y/(n·u) of any ray pair
  obeys q̂′ = (A·q̂ + B)/(C·q̂ + D) with the same matrices: a transfer adds t/n, a surface of
  power φ makes 1/q̂′ = 1/q̂ − φ, and mirrors need nothing more. In a space of index |n| the
  physical parameter along the beam's direction of travel is q = |n|·q̂; Im q̂ stays positive
  through every lossless step.
- **Wavelength.** The table's wavelength is in the system's air, which is the wavelength in a
  medium of relative index 1 (section 2); in a medium of relative index n it is λ/n. So
  z_R = π·w0²·n/λ in that medium.
- **Reported quantities**, per space: beam radius w = w0·√(1 + (z/z_R)²) (1/e² intensity);
  waist radius w0 = √(λ_n·z_R/π); Rayleigh range z_R = Im q; distance from the waist z = Re q
  along the direction of travel; divergence θ = w0/z_R (half angle, radians); wavefront radius
  R = z + z_R²/z, positive when diverging and infinite at a waist; and the Gouy phase summed
  over the spaces crossed since surface 1, atan(z₂/z_R) − atan(z₁/z_R) per space, which for a
  total below π equals −arg(A + B/q̂) of the matrix crossed.
- **Positions.** The input waist position is a signed distance along z from surface 1's
  vertex (negative in front of it), like every object-space position. A reported waist
  position is a signed distance along z from the surface's vertex to the waist of the beam in
  the space after it: −n·Re q̂, so after an odd number of mirrors a waist ahead of the beam is
  at negative z, as a thickness is. Rows are the beam just after each surface; the first row
  is the input beam arriving at surface 1, the last the beam arriving at the image surface.
- **M² (embedded Gaussian).** With beam quality M² the input waist W0 is the real beam's; the
  embedded Gaussian has waist W0/M (M = √M²) and is the beam propagated, so z_R = π·W0²·n/(M²·λ).
  The real beam's radius, waist radius and divergence are M times the embedded beam's; its
  Rayleigh range, waist position, wavefront radius and Gouy phase are the embedded beam's.
- **Astigmatism.** The x and y planes have their own waist, position and M² and propagate
  independently (a simple astigmatic beam); with rotationally symmetric surfaces the same
  matrices act on both.
- **Not built.** The generally astigmatic (skew) beam traced about a real chief ray, which
  needs the real ray trace (OP-20) and Arnaud's ray representation; tilted, decentered and
  anamorphic (toroidal, cylindrical) surfaces; Gaussian beam operands; apertures clipping the
  beam. Section 10, item 10, lists the check against OpticStudio.

## 15. Coordinate breaks, tilts and global coordinates

OP-21, `OpenSim.Optics/Sequential/SurfacePlacement.cs`, `CoordinateBreak.cs` and
`CoordinateBreakTools.cs`. The rules are read from the OpticStudio 2025 R2 help pages listed in
section 9; the formulas below are derived in the code comments and the tests, not copied.
Section 10, item 11, lists what is not yet compared with OpticStudio's output.

- **Data.** A `SequentialSurface` is a coordinate break when `CoordinateBreak` is set: a
  `TiltDecenter` (section 4) and an optional coordinate return. Any other surface may have
  `TiltBefore` (a `TiltDecenter`) and `TiltAfter` (a `SurfaceTiltAfter`: explicit values, this
  surface's "before" picked up or reversed, or a prior surface's "before" picked up or
  reversed). "Reversed" is `TiltDecenter.Reversed`: the other order, every value negated, which
  undoes the original exactly. Saved lenses hold them (section 20, TC-6).
- **Global frame.** Object space's axes, origin at surface 1's vertex before surface 1's own
  tilt and decenter. Rays are launched in it, and "global" in every result means it.
- **Placement.** For j ≥ 1, with frame(0 → 1) the identity:
  frame(j) = frame(j − 1) · After(j − 1) · Translate(0, 0, t(j − 1)) · Before(j), where Before
  is the break's transform for a break and `TiltBefore` otherwise, and After is the resolved
  `TiltAfter` (the identity for a break). So a thickness is measured along z after all the
  surface's tilts and decenters, and a break's thickness is applied last, in either order.
  `SurfacePlacement.Global(j)` is frame(j); `SequentialTrace.SurfaceStep(j)` is the step from
  frame(j − 1). Without tilts the step is exactly the translation by the previous thickness, so
  untilted lenses trace bit for bit as before.
- **Coordinate break in the trace.** A dummy plane: the ray is moved to the plane z = 0 of the
  break's frame (a virtual step back if it lies behind) and goes on with the same direction and
  medium; a ray parallel to that plane (|N| < 10⁻¹² in its frame) stays where it is. A break has no curvature, conic,
  shape, aperture, mirror flag, ignore flag or surface tilt, and cannot be the object or the
  image (each refused by `SequentialLens.Validate`).
- **Coordinate return** (`CoordinateReturnMode`), to a prior surface k ≥ 1, solved against
  frame(k). With P the frame the break starts from and Q = P⁻¹·frame(k) (rotation R, translation
  q, R's third column r₃): the break's rotation is R in every mode. *Orientation only* keeps the
  entered decenters and thickness. *Orientation XY* puts the break's vertex on k's z axis: in
  the order decenter then tilt (dx, dy, 0) = q + s·r₃ with s = −q_z/r₃z; in the order tilt then
  decenter (dx, dy) = the x and y of Rᵀ·q. *Orientation XYZ* also sets the break's thickness so
  the next surface lands on k's vertex: −s, or the z of Rᵀ·q. Decenter then tilt needs r₃z ≠ 0
  and otherwise fails, naming the break and advising the other order. The trace uses the exact
  solved transform; `SurfacePlacement.ResolvedBefore` reports its degrees read back from it.
- **Restrictions read from the help:** no "before" tilt on surface 1 with the object at
  infinity; no tilt on a break. Also refused here: a tilt of the object surface, an "after"
  tilt on the object or image, a pick-up from a break, a return to a later surface or to the
  object.
- **Global vertex.** `SurfacePlacement.RelativeTo(j, reference)` is frame(reference)⁻¹·frame(j);
  its translation is the vertex and its columns the local axes (global = vertex + R·local, as
  section 4). `LocalToGlobal` and `GlobalToLocal` convert points. The analysis **Global vertex**
  (`GlobalVertexAnalysis`) lists every surface's vertex, the tilts that reproduce its rotation
  as a break in the order decenter then tilt, the rotation matrix, and each break with returns
  solved, relative to a chosen reference surface (0 for the global frame).
- **Paraxial model.** `SequentialLens.ToParaxialLens` copies the thicknesses of a lens without
  tilts or breaks. With some, it follows the ray from the global origin along +z through the
  surfaces and uses its signed lengths as thicknesses (negative after an odd number of
  mirrors), breaks becoming planes in the medium before them. That is exact when the tilts only
  fold the axis with plane mirrors and move dummy planes. A surface with power or a shape that
  the axis meets off its vertex or obliquely, a local z pointing the wrong way for the mirror
  count, or a refracting plane met obliquely makes the lens non-axial, and the method throws
  `InvalidOperationException` naming the surface; `SequentialLens.IsAxial` reports the same
  without throwing. The ray launcher and the paraxial analyses therefore refuse a non-axial
  lens (ray aiming for such systems is OP-22); the real trace itself does not.
- **Lens tools** (`CoordinateBreakTools`). Each returns a new lens, renumbers the surfaces that
  returns and pick-ups name, refuses what it cannot rewrite exactly, and moves no surface.
  - *TiltDecenterElements*: a dummy at the group's start, a break to the pivot (if not the
    origin), the tilt break, a break back from the pivot, the group, a return (orientation XYZ,
    order tilt then decenter) to the dummy, and a break holding the original step to the next
    surface. The pivot is in the frame before the first surface's own tilt, or global.
  - *AddFoldMirror*: the fold surface must be a plane dummy in one medium. A break of half the
    reflect angle about x or y (thickness 0), the surface as a mirror (thickness 0), the same
    break carrying the fold surface's thickness negated; every later surface reversed. A plane
    mirror between breaks of θ/2 reflects the space after it by Rx(θ)·diag(1, 1, −1), so
    "reversed" means thickness, curvature, and the x and y tilts of later breaks and tilts
    change sign, decenters and z tilts do not. Later surfaces with a shape, and returns or
    pick-ups across the fold, are refused.
  - *DeleteFoldMirror*: removes a zero-thickness break before the plane mirror, the mirror, and
    the break after it when the mirror has zero thickness; adds the removed thicknesses (signs
    restored) to the surface before; reverses every later surface.
  - *LocalToGlobal*: groups start at the first surface and after every break in the range; the
    breaks are removed and each group gets a return (orientation XYZ) to the reference, a break
    with the group's x, y (decenters) and z (thickness) in the reference's frame, and a break
    with its tilts.
  - *GlobalToLocal*: each run of breaks becomes one explicit break in the chosen order; the
    surface before keeps its thickness. `CoordinateBreakTools.Decompose` writes a transform
    m = Break·Translate(0, 0, t): tilt then decenter (dx, dy, t) = Rᵀ·q; decenter then tilt
    t = q_z/R₃₃, dx = q_x − R₁₃·t, dy = q_y − R₂₃·t (refused when R₃₃ = 0).
- **Gates** (`CoordinateBreakTests`, closed forms derived in the tests): a plate of index n
  tilted by a displaces a ray by t·sin(a − b)/cos b with sin a = n·sin b, built with breaks and
  with surface tilts, in both orders; a pair of 90° and −90° fold mirrors returns the axis,
  displaced by the fold separation, and images exactly as the unfolded lens; an Offner relay
  (concentric mirrors of radius R and R/2, object at the centre plane) built with breaks
  traces as the same relay without them, images its centre onto itself, and its
  tangential-minus-sagittal focus separation grows sixteen-fold when the field doubles (fifth
  order, not third). Third-order anastigmatism follows from the incidence of the marginal ray
  from the common centre being zero at every mirror (S1 = 0 per surface, and so S3 = 0 by the
  stop-shift relations); the higher-order coefficients are not derived, so the ratio is
  allowed 14 to 18.5.
- **Not built:** multi-configuration control; OpticStudio's solve-removal modes
  and colours/comments options of the tools; hiding the trailing dummy; tilting the object
  surface; layouts drawing tilted surfaces; paraxial data, ray aiming and the launcher for
  non-axial systems.

## 16. Ray aiming and vignetting

Ray aiming and vignetting factors (OP-22) are in `OpenSim.Optics/Sequential/RayLauncher.cs`,
`PupilVignetting.cs` and `VignettingTools.cs`; the setting is `OpticalSystemData.RayAiming`
(`OpenSim.Optics/SystemData/RayAiming.cs`). Sources: Ansys OpticStudio 2025 R2 help on
ansyshelp.ansys.com, the topics "Ray Aiming", "Vignetting Factors" (Conventions and
Definitions), "Vignetting", "Vignetting Plot", "Replace Vignetting With Apertures" and "Field
Type" (copies in `plans/sources/optics/ansys-op22/`, not in the repository). Optiland's robust
scheme was not read; nothing is taken from it.

- **Modes.** *Off:* rays go through the paraxial entrance pupil of the primary wavelength
  (section 13), bit for bit the launch OP-20 built. *Paraxial* and *Real:* every ray is aimed so
  it crosses the stop surface at (Px·R, Py·R) in the stop's local frame; R is the paraxial
  marginal ray's height at the stop (Paraxial) or the real marginal ray's (Real), the real one
  traced from the centre of the object through the rim of the paraxial pupil at the primary
  wavelength, apertures ignored. With the aperture type *float by stop size* R is the stop's
  semi-diameter in both modes (help: "both real and paraxial rays will be aimed to the real
  stop exactly"). When the real marginal ray does not reach the stop, the paraxial R is used and
  the launcher says so in its notes, as the help says OpticStudio does.
- **Unknowns.** A field fixes the object side: the direction (object at infinity) or the object
  point (finite object). The two unknowns are where the ray crosses the paraxial entrance-pupil
  plane z = z_E, or its two slopes (the x and y components of a direction (a, b, 1)) when that
  plane is at infinity. Aiming off is these unknowns at (Px·r, Py·r) (or (Px·u, Py·u)).
- **Solve.** `DampedNewton2D` (`OpenSim.Core/Numerics`): Newton on the stop landing point, with a
  forward-difference Jacobian (step 10⁻⁷ of the pupil radius or marginal slope) and a
  backtracking line search that halves the step until the residual norm falls by the Armijo
  fraction. Converged at a miss of 10⁻¹³·R; when it stalls at rounding, accepted up to 10⁻¹¹·R.
  The chief ray of a field starts from the paraxial chief ray moved by the pupil shift (below);
  a pupil ray starts from its field's chief solution plus the paraxial pupil offset (with the
  compression). When Newton fails, continuation: the chief ray is followed in field from the
  axis (the field scaled by t from 0 to 1), a pupil ray in pupil from the chief ray, in steps of
  1/4 that halve on a failure (to 1/512) and double on a success, each seeded by linear
  extrapolation of the last two solutions. A ray that still cannot be aimed is returned failed,
  `RayStatus.NoConvergence` at the stop, with the reason (`RayLauncher.LaunchAimed`).
- **Robust.** Always continues (steps of 1/8, not grown) instead of trying Newton from the
  paraxial guess first, so the root found is the one connected to the axial ray; and refuses a
  ray that reaches the stop along a virtual segment: a segment from surface 2 to the stop
  whose length has the opposite sign to thickness times sense of travel (the help: robust mode
  makes sure that if several ray paths reach a stop point "only the correct one is chosen").
- **Pupil shift and compress** only seed the solve (help: "just to get the ray aiming
  started"). The first guess is the aim point (Px(1 − cx)·r + sx·k, Py(1 − cy)·r + sy·k,
  z_E + sz·|H|), k = |H| (the radial normalised field) when the shifts scale with field and 1
  otherwise, carried along the field's family of rays to the plane z = z_E. Shifts are meters
  (lens units in the file). With the entrance pupil at infinity only the compressions apply.
- **Chief-ray solving and caching.** A field's chief aim is cached per wavelength and field; a
  solve is a pure function of its inputs, so the cache does not depend on order and a batch
  traced in parallel is bitwise the sequential one.
- **Real image height.** The field is the paraxial image height whose real chief ray (aimed as
  the system says, primary wavelength, apertures ignored) meets the image surface at the
  requested height: Newton on the two image coordinates from the paraxial guess, then
  continuation in height; with aiming on, each trial field's chief ray is aimed by one Newton
  solve from the last converged aim. The paraxial engine still refuses the type; the launcher
  builds its paraxial model with the fields read as paraxial image heights. A height no chief
  ray reaches throws, naming it. This closes OP-18's deferred check "every field type launches
  the same chief ray where equivalent" for real image height (`RayAimingTests`).
- **Vignetting factors** (`PupilVignetting`): copied from the help's "Vignetting Factors",
  first P′x = VDX + Px(1 − VCX), P′y = VDY + Py(1 − VCY), then turned by the angle θ (TAN):
  P″x = P′x cos θ − P′y sin θ, P″y = P′x sin θ + P′y cos θ. The remapped point goes to the
  paraxial pupil with aiming off and to the stop with it on (help: "If ray aiming is turned on,
  then the remapping is done at the stop surface"). All-zero factors leave the point
  unchanged, bit for bit. At a field that is not a defined one: linear interpolation in Hy
  between neighbouring fields when every field has x = 0 and the point has Hx = 0 (held at the
  end values beyond them); otherwise the nearest defined field. The help says it interpolates
  for rotationally symmetric systems and fields along y, and uses the closest field otherwise.
- **Set Vignetting** (`VignettingTools.SetVignetting`), primary wavelength, the factors cleared
  first: a 21 × 21 grid is traced with apertures; a ray passes when it reaches the image (a
  miss, total internal reflection or a failed aim is vignetted). From the centroid of the
  passing rays (or the passing ray nearest it), the edges are found along the vertical and
  horizontal lines through the centre by a 64-step scan to the unit circle and bisection to
  10⁻¹⁰; the centre moves to the midpoints and the search repeats (at most four rounds). Then
  VDX, VDY are the midpoints, VCX = 1 − (right − left)/2, VCY = 1 − (top − bottom)/2, TAN = 0.
  An unvignetted field (the centre passes and all four edges from it reach the unit circle)
  gets zeros. A field with no passing ray keeps its factors, with a note. *Clear Vignetting* is
  `VignettingTools.Clear`.
- **Replace Vignetting With Apertures** (`VignettingTools.ReplaceWithApertures`): 64 rays round
  the rim of each defined field's vignetted pupil, primary wavelength, apertures ignored; every
  surface between object and image without an aperture (and not ignored) gets a circular
  aperture of the largest radial height they reach there; the factors are cleared.
- **Vignetting Plot** (`VignettingPlotAnalysis`, Rays): fields Hy = 0 to 1 along +y, a
  (2n + 1) × (2n + 1) grid (points (i/n, k/n) inside the unit circle) at the primary wavelength;
  the fraction reaching the image. By default the factors are first replaced with apertures
  (the help's default); kept, the fraction is multiplied by (1 − VCX)(1 − VCY). Ray density at
  least 5 (the help's minimum).
- **Gate** (`RayAimingTests`): with aiming paraxial or real, robust or not, every pupil point
  (the chief ray included) lands on its stop point within 10⁻¹⁰·R, read from an independent
  trace, on a singlet with the stop 20 mm behind it (a third of the stop radius of pupil
  aberration at 20°), a wide-angle pair to 75° (where the unaimed chief ray does not reach the
  stop), and coaxial Cassegrain and three-mirror systems used off axis through field-biased
  fields. With aiming off the launch equals OP-20's bit for bit. Set Vignetting and the
  Vignetting Plot are checked against a closed form (a clipping aperture in object space).
- **Not built / not covered.** A 180° fisheye: angle fields must stay below 90° and the launch
  for an object at infinity takes the direction (ūx, ūy, 1), so fields at or past 90° cannot be
  stated; the off-axis three-mirror system with tilted mirrors needs OP-21's tilts. Not built:
  OpticStudio's enhanced ray aiming, the ray-aiming cache switch and wizard, automatic pupil
  shifts, aiming to a decentered stop aperture's centre (the aim is the stop's vertex), real
  image height in afocal image space, and Set Vignetting and Replace Vignetting buttons in the
  app (they are library functions). Section 10, item 12 lists
  the checks against OpticStudio.

## 17. Aspheres

`OpenSim.Optics/Sequential/Aspheres` (OP-23) adds surface shapes (section 13's
`SurfaceShape` hook) for the rotationally symmetric forms of OpticStudio and two that are not
symmetric but share the machinery. Every form is the Standard conic of the surface's curvature
c and conic k plus its own terms; the coefficients live on the shape, in SI units (a
coefficient on rⁿ is in m^(1 − n); a coefficient on a normalised radius is in meters). The
sag formulas and parameter meanings are copied from the OpticStudio 2025 R2 help
(ansyshelp.ansys.com), pages saved on 2026-11-14 under `plans/sources/optics/aspheres/`; the Q
polynomials from Forbes's papers (`plans/sources/optics/forbes/`, transcription with
equation numbers).

| Form | Class | Added to the conic | Source |
|---|---|---|---|
| Even Asphere | `EvenAsphere` | Σ αᵢ r²ⁱ, i = 1…8 | help, "Even Asphere" |
| Odd Asphere | `OddAsphere` | Σ βᵢ rⁱ, i = 1…8 | help, "Odd Asphere" |
| Extended Asphere | `ExtendedAsphere` | Σ αᵢ ρ²ⁱ, ρ = r/R_n, up to 240 terms | help, "Extended Asphere" |
| Extended Odd Asphere | `ExtendedOddAsphere` | Σ αᵢ ρⁱ, ρ = r/R_n, up to 240 terms | help, "Extended Odd Asphere" |
| Q-Type, Qcon | `QTypeAsphere` | x²·Σ a_m Q^con_m(x), x = (r/ρmax)², up to 100 terms | help, "Q-Type Asphere (sequential…)"; Forbes, Opt. Express 15, 5218 (2007), Eqs. 2–3, 8 |
| Q-Type, Qbfs | `QTypeAsphere` | (ψ/φ)·x(1 − x)·Σ a_m Q^bfs_m(x), φ = √(1 − (1 + k)c²r²), ψ = √(1 − kc²r²) | help; Forbes, Opt. Express 19, 9923 (2011), Eqs. 2.1–2.4 |
| Superconic | `SuperconicSurface` | implicit: A z² − 2Bz + C = 0, s² = r² + z², A = kc, B = 1 + Σ Uᵢ s²ⁱ, C = c s² + Σ Vᵢ s²ⁱ⁺², up to 120 pairs | help, "Superconic" |
| Alternate Even / Odd | `AlternateEvenAsphere`, `AlternateOddAsphere` | as Even / Odd, met at the other root | help, "Alternate Even", "Alternate Odd" |
| Periodic | `PeriodicSurface` | −A[¼(1 + cos 2παx)(1 + cos 2πβy) − 1] | help, "Periodic" |
| Odd Cosine | `OddCosineSurface` | Σ βᵢ rⁱ (16) + Σ Aᵢ s^Pᵢ cos(Bᵢθ + Cᵢ) (0–6), s = r/R; R ≤ 0 drops the cosine terms | help, "Odd Cosine" |

- **Q polynomials** (`QPolynomials`) are evaluated by recurrence only: Q^con by the Jacobi
  recurrence with Forbes's constants (Opt. Express 18, 13851 (2010), Eq. 5.2) and Clenshaw's
  sum and its derivative (Eqs. 2.2–2.3); Q^bfs through the auxiliary polynomials
  P_{m+1} = (2 − 4x)P_m − P_{m−1} and the banded change of basis f, g, h (Opt. Express 18,
  19700 (2010), Eqs. 2.5–2.7, 3.2–3.12, A.14–A.16). No monomial expansion is formed.
- **Intersection** (`AsphereIntersection`, one routine for all forms): start at the base
  conic's root (`ConicIntersection`), or at t = 0 when the conic is missed; damped Newton on
  g(t) = tN − z(x + tL, y + tM), the step halved until |g| does not grow and the sag is defined
  (which keeps iterates inside the conic's square root), at most 64 steps; if Newton stalls,
  bisection on a bracket searched outward from the start. A failure is a miss
  (`RayStatus.MissedSurface`). The normal is (−∂z/∂x, −∂z/∂y, 1)/|…| from the analytic slopes,
  +z at the vertex like the conic's. A sag is undefined (and a ray there misses) where
  (1 + k)c²r² ≥ 1, and for Qbfs also where kc²r² ≥ 1.
- **Alternate forms.** The base conic's quadratic A·s² + 2B·s + C = 0 has roots whose product
  is C/A; with the vertex root s₁ = −C/q the alternate start is s₂ = −q/A. It must lie on the
  vertex sheet, so for ordinary rays through a lens (whose other root is on the far
  hemisphere) the alternate forms miss.
- **Superconic sag** is the root of F(z) on the vertex sheet (∂F/∂z < 0, its vertex sign),
  reached by damped Newton from the conic's sag; its slope is −F_r/F_z.
- **Conversion** (`AsphereConversion`): Even Asphere ↔ Q-type by linear least squares
  (`HouseholderLeastSquares`) on equal-area samples of 0 ≤ r ≤ ρ, ρ also the Q-type
  normalization radius. To Qcon and to Even the curvature and conic are kept; to Qbfs the conic
  is kept and the curvature is that of the conic through the vertex and the edge point,
  c(ρ² + (1 + k)f²) = 2f (Forbes's best-fit sphere for k = 0). The RMS and largest residual
  are returned. An Even Asphere without α1 goes to Qcon and back exactly (to rounding); α1·r²
  cannot be expressed in Qcon and appears in the residual. `QPolynomials.BfsFitByCosineTransform`
  is Forbes's DCT fit (2010 Eqs. 4.4–4.6); the paper prints the factor (−1)^m/(2N), but its
  derivation (Eq. B.4) and its worked example (Eq. 4.7) need (−1)^m/N, which is used.
- **Units of the Superconic.** OpticStudio writes A = k/R and C = s²/R + …; with c = 1/R this is
  A = kc, C = cs² + …, which also covers the plane (c = 0).
- **Gates** (`OpenSim.Tests/Optics/AsphereTests.cs`): every form set to a conic traces as the
  Standard surface to 1e-12 (including a superconic sphere written with U1 = λ, V1 = λc, which
  is the sphere's equation times 1 + λs²); the alternate forms meet the closed-form second root;
  normals equal central-difference sag gradients to 1e-8; Even → Qcon → Even keeps the sag to
  1e-12 of the aperture; the recurrences equal Forbes's closed forms (2007 Eqs. 9 and 14) and
  his matrix (2010 Eq. A.17); the DCT fit reproduces his worked example (2010 Eqs. 4.7, 4.8).
- **Not built.** Cubic Spline and Extended Cubic Spline: the help gives the sample points and
  says the segments have continuous first and second derivatives, but not the end conditions
  at the vertex and the edge, so the curve between samples cannot be reproduced and the forms
  are left out. The fast even-asphere trace (Newton is used for every form). Saving these
  surfaces is OP-31's. Section 10, item 13, lists the readings not yet compared with
  OpticStudio.
- **Paraxial curvature** (TC-6). Each form overrides `SurfaceShape.ParaxialEquivalent` with
  its sag's second derivative at the vertex along the y meridian, so first-order data (EFL,
  BFL, pupils, the paraxial rays, ray aiming's paraxial mode, the solves) include its
  second-order term. Derived from the sag equations above, in each override's comment:

  | Form | Paraxial curvature |
  |---|---|
  | Even Asphere, Alternate Even | c + 2α1 |
  | Odd Asphere, Alternate Odd | c + 2β2 |
  | Extended Asphere | c + 2α1/R_n² |
  | Extended Odd Asphere | c + 2α2/R_n² |
  | Qcon | c (x²S(x) is O(r⁴)) |
  | Qbfs | c + 2S(0)/ρmax², S(0) = Σ a_m Q^bfs_m(0) |
  | Superconic | c (the U and V terms start at r⁴) |
  | Periodic | c + 2π²Aβ² (y–z; the x–z value c + 2π²Aα² is not used) |
  | Odd Cosine | c + 2β2 + Σ over terms with P = 2 of (2Aᵢ/R²)·½[cos(Bᵢπ/2 + Cᵢ) + cos(−Bᵢπ/2 + Cᵢ)] (R > 0) |

  Readings (OpticStudio's help does not say how its paraxial trace treats these terms):
  - *Cone terms.* β1 r (Odd, Alternate Odd, Odd Cosine), α1 ρ (Extended Odd) and a cosine
    term with P = 1 are cones at the vertex: along y the sag has a slope that jumps from −β1
    to +β1, which turns every ray by about the same angle whatever its height. A first-order
    model is linear in height and cannot hold that, so these terms are left out and the
    curvature is the limit of d²z/dr² as r → 0⁺. Nothing at run time says a cone was left out;
    the real trace keeps it. Terms in r³ and higher have no second-order part.
  - *Odd Cosine halves.* A P = 2 cosine term whose B is not even differs on the +y and −y
    halves of the meridian; the paraxial engine is given the mean, the part even in y. A term
    with P = 0 is a step at the vertex and adds no curvature. A non-zero term with
    0 < P < 2 (P ≠ 1) or P < 0 has unbounded curvature at the vertex; `ToParaxialLens` refuses
    it with an `ArgumentException` naming the surface and the term, so the real trace, which
    is launched from the paraxial data, refuses it too.
  - *Alternate forms* take the vertex curvature of their sag, though their rays meet the
    surface at the conic's other root.
  - *Periodic.* The curvature is the vertex value; when the period is short against the beam
    it describes a region much smaller than the beam.
  - A form without a second-order term gives exactly the Standard surface's curvature (the
    same double).

  Gates (`OpenSim.Tests/Optics/AsphereParaxialTests.cs`): each form's curvature equals the
  derived value to 64 ulps and a Richardson-extrapolated second difference of its own sag at
  the vertex to 1e-7 m⁻¹; an Even Asphere with only α1 has the first-order data of the sphere
  of curvature 2α1, bit for bit; real marginal rays at heights 0.2 mm down to 1.6 µm converge
  to the paraxial focus (Aitken limit within 2e-9 m); forms without an r² term leave the
  paraxial lens unchanged bit for bit; the Odd Cosine refusal names the surface and the term.

## 18. Idealised and special surfaces

OP-27 adds `SurfaceShape` subclasses in `OpenSim.Optics/Sequential/Special/` for OpticStudio's
idealised and special sequential surfaces. Each one's definition is quoted from its OpticStudio
2025 R2 help page (ansyshelp.ansys.com, OpticStudio User Guide, "Sequential Surfaces (lens data
editor)") on the class; the pages are saved under `plans/sources/optics/ansyshelp-op27/`.
Formulas not on a page are derived in the class comments and in `IdealSurfaceTests`.

- **The shape hook** (`SurfaceShape`): `ParaxialEquivalent` gives the paraxial engine a
  curvature, a thin-lens power or an ABCD matrix (default: the surface's vertex curvature);
  `SequentialLens.ToParaxialLens` uses it. `OpdLimitation` is null when the trace's optical
  path through the shape is the true one, else a sentence saying why OPD is not defined there.
- **Paraxial engine additions** (`ParaxialSurface.ThinLensPower`, `ParaxialSurface.SlopeMatrix`).
  A thin-lens power adds to c·(n′ − n) and enters the signed-index formalism unchanged (a thin
  lens has one power whichever way light crosses it). An ABCD matrix on (y, u) becomes
  [[A, B/n], [n′C, n′D/n]] on (y, n·u) with signed indices. When B ≠ 0 the ray moves on the
  vertex plane: `ParaxialRay.Heights` keeps the arriving height and the next transfer starts
  from the moved one, so such a surface may not be surface 1, the stop or the last surface.
  The Gaussian beam refuses a matrix of negative determinant (a retroreflector).
- **Paraxial** (`ParaxialShape`): plane; n′_s·u′ = n_s·u − x·φ (and y), u = L/N, φ = 1/f with f
  in air, signed indices (so the rule holds after mirrors). It images every point of every
  plane perfectly (linear in the slopes). The optical path added at the hit X is
  E = n·[ℓ(P→O) − ℓ(P→X)] + n′·[ℓ(O→P′) − ℓ(X→P′)], ℓ signed along the travel, O the vertex,
  P where the incoming ray crosses the object plane the surface works at, P′ its image from
  n′_s/z_i − n_s/z_o = φ. Every ray from P then has the same path to P′. Object at infinity
  (default; OpticStudio's OPD mode 2): the object term is −n·(X·d). `ResolveConjugates` sets the
  object plane from the paraxial marginal ray at the primary wavelength (mode 3). Paraxially
  E ≈ −φ·r²/2 at any conjugates: with u the incoming slope, the object term is n·r²/(2a) − n·r·u
  and the image term n′·r²/(2b) + n′·r·u′, and n′u′ − nu = −rφ with n/a + n′/b = φ.
- **Paraxial XY** (`ParaxialXYShape`): the same rule with φx and φy; path −(φx·x² + φy·y²)/2,
  OPD flagged approximate.
- **ABCD** (`AbcdShape`): (x′, ω′) = M·(x, ω) per axis with ω = L/N; the ray leaves from (x′, y′);
  A ≠ 0 (x is recovered from x′); no path term, OPD flagged undefined, as OpticStudio does.
- **Retro Reflect** (`RetroReflectShape`): plane; on a mirror the direction leaving is −d; in
  glass or air ordinary refraction. Paraxially the identity on (y, u), diag(1, −1) on (y, n·u).
- **Conjugate** (`ConjugateShape`): |p − P₁| ± |p − P₂| = |P₁| ± |P₂| (+ when z₁, z₂ have one
  sign); Newton's iteration for the intersection and the sag; normal ±∇F/|∇F|, +z at the
  vertex; only the vertex sheet. Paraxial curvature ∂²z/∂y² at the vertex, (1/z₁ + 1/z₂)/2 on
  the axis.
- **Fresnel family** (`FresnelShapes.cs`): intercept the substrate, refract with the facet's
  normal (−f_x, −f_y, 1)/|…|; no groove geometry, path to the substrate only. Fresnel: plane
  substrate, facet the even asphere of the surface's c, k and α₁…α₈. Extended Fresnel:
  even-asphere substrate, normal of z_s + z_f. Generalized Fresnel: even-asphere substrate,
  facet Σ Aᵢ·Eᵢ(x/R, y/R) in the extended polynomial order (x, y, x², xy, y², …). Cylinder
  Fresnel: substrate and facet in y only. OPD flagged undefined on a curved substrate.
- **Lenslet Array** (`LensletArrayShape`): odd nx × ny cells W × H centred on the vertex, each the
  even asphere of the surface's c, k and α about its centre; a ray outside the cells misses.
- **Not built.** Slide (a bitmap's RGB values set the transmission through "a linear
  interpolation algorithm" the help does not give, and no bitmap reader exists); Filter (the
  help says only that it is a user-defined surface DLL); Data (a store of extra values for a
  user-defined surface, which this program does not have); Atmospheric (the help cites
  Seidelmann, Explanatory Supplement to the Astronomical Almanac §3.281, and Hohenkerk and
  Sinclair, NAO Technical Note 63, neither of which is on this machine; the Starlink PAL routine
  `palRefro`, saved in `plans/sources/optics/pal-refro/`, implements Hohenkerk and Sinclair with
  its own refractivity changes and is LGPL, so it was not ported without the owner's decision);
  Paraxial OPD modes 0 and 1. Saved lenses and the lens data editor hold these surfaces
  (section 20), except the Lenslet Array, whose help page lists no parameters.

## 19. Solves

Solves (OP-29, `OpenSim.Optics/Sequential/Solves`) compute a cell of the lens data from a
condition. The definitions come from OpticStudio's 2025 R2 help on ansyshelp.ansys.com, read
2026-11-14 and saved in plans/sources/optics/solves/. The pages are "Summary of Solves",
"Curvature Solves", "Thickness Solves", "Edge Thickness", "Material Solves", "Clear
Semi-Diameter or Semi-Diameter Solves", "Conic Solves", "TCE Solve", "Parameter Solves",
"Restrictions" and "Integer Codes for Column Numbers". A solve is a record (`SurfaceSolve`), and
`SolveEngine.Apply` returns a new lens with a status for each solve.

- **Units.** Lengths are meters and curvatures 1/m. Paraxial angles are slopes u = dy/dz in the
  system frame (section 11). Paraxial solves use the primary wavelength and the signed indices
  of section 11, so they work through mirrors.
- **Order.** Solves run from surface 0 to the image. On each surface the order is curvature,
  thickness, material, clear semi-diameter, conic, parameters (by number), then TCE
  ("Restrictions"). Each solve sees the lens with every earlier solve already applied.
- **Iteration.** With `SolveOptions.Iterate` (the default) the engine repeats the pass until a
  whole pass leaves every solved cell unchanged. Numbers count as unchanged within a relative
  1e-12 plus 1e-15 absolute. The default limit is 50 passes; past it the engine reports the
  cells that are still changing. Solves that read a later surface need this, for example a
  position solve whose reference surface follows it.
- **Pickups.** The target takes O + S·V. The source must be an earlier surface, or an earlier
  column on the same surface ("Restrictions"). Thickness and parameter pickups take an offset.
  Curvature, semi-diameter and conic pickups take only a scale. The curvature scale applies to
  the curvature, not the radius. Material and TCE pickups copy the value. An automatic source
  semi-diameter is read from the sizing of section 13.
- **Paraxial rays.** The marginal ray is the aperture-sized one. The chief ray is that of the
  full field, and a solve that needs it fails when every field is on the axis. The closed forms
  are written out in `SolveEngine.cs`:
  - marginal or chief ray angle, and F/#: c = (ν − n′u′)/(y(n′ − n));
  - normal to a ray: c = −u/y;
  - aplanatic: c = −u(n + n′)/(n·y);
  - element power: c₂ = (φ − φ₁)/((n₃ − n₂)(1 − φ₁t₂/n₂));
  - marginal or chief ray height: t = (h − y)/u;
  - pupil position: t = −ȳ/ū of the unit paraxial chief ray.
- **Real rays.** The real marginal-ray-height solve (pupil zone not 0) traces the on-axis ray at
  Py = zone with apertures ignored. It sets t so that the ray's height on the next vertex plane
  is the target. The optical path difference solve finds the thickness by a bracket search and
  Brent's method. `SolveEngine.OnAxisOpdWaves` gives its OPD: the on-axis ray and chief ray run
  to the reference sphere described in section 10, item 15. OPD = chief path − ray path
  (section 6), in primary waves of the system's air.
- **Geometry.** Positions are sums of thicknesses along z. That holds while there are no tilts
  (OP-21), and OpticStudio states the same assumption for the position solve.
  - Edge thickness: E = Z(next) − Z(this) + T, both sags at this surface's radial height ("Edge
    Thickness").
  - Center of curvature: t = R(p) − Σ t.
  - Concentric with surface: R = −Σ t.
  - Concentric with radius: R = R(p) − Σ t.
- **Materials.** The model glass solve uses this program's model-glass stand-in (section 10,
  item 7), from a fit passed in `SolveOptions.ModelGlass`. The offset solve applies
  `GlassOffsets` to the glass as entered, so applying it twice does not double it.
  The substitute solve leaves the glass unchanged; it is a flag for global optimization, which
  is not built.
- **Semi-diameters.** The automatic solve sets the cell to automatic. The maximum solve writes
  the automatic value of the single configuration, because multiple configurations (OP-30) are
  not built.
- **Parameters** are read through `IParameterizedShape` when a shape implements it, and
  otherwise through the registry of saved surface types (`SurfaceTypes`, section 20): a
  registered shape's parameter columns and a coordinate break's six values (TC-6). **TCE** is `SequentialSurface.ThermalExpansion` (10⁻⁶/K), which is
  data only.
- **Re-application and the optimizer.** `Apply` is a pure function of the lens as entered, the
  system data and the solves. A solved cell is recomputed whatever it holds, so applying the
  solves to a solved lens returns the same lens in one pass. An optimizer changes a variable,
  re-applies the solves, and evaluates the solved lens, so the solves follow every variable in
  its derivatives. `SolveEngine.SolvedCells` lists the cells that must not be variables.
- **Failures.** A solve that cannot be met leaves its cell unchanged and reports why, naming
  the surface, column and solve. Examples are equal indices, a ray crossing the axis or leaving
  parallel to it, a sag beyond the hemisphere, a failed real ray, an OPD with no bracket, and
  no convergence. Solve sets that break the help's restrictions are refused before any pass:
  a pickup from a later cell, a marginal ray height solve before the stop, or two solves on one
  cell.
- **Not built.**
  - The ZPL macro solve, which needs the macro interpreter (OP-99).
  - The parameter chief ray solve. Its record exists, the editor offers it on a coordinate
    break's first four parameters, and the engine reports it as not built.
  - Extra data columns.
  - Solves on the non-sequential editor.
  - Variables and the optimizer.

## 20. Lens data and saved surfaces

The sequential surfaces (OP-31) are saved in the optics record as `OpticsSettings.Surfaces`
(`OpenSim.Core/Model/OpticsSurfaceSettings.cs`), a list from the object (surface 0) to the
image. The project format version is unchanged (5); a file without the list is a system with
no surfaces. TC-6 added surface types, tilts, coordinate breaks and solve evaluation as
nullable fields and new readers, without a format change.

- **Units and signs.** Lengths are in the system data's lens unit, as the editor shows them, so
  a typed value reloads digit for digit. Signs are section 3's. A plane is written without a
  radius (JSON has no infinity; zero is also a plane); an infinite thickness (an object at
  infinity) is the flag `ThicknessIsInfinite`. Semi-diameters are null when automatic.
- **Material.** One of: a glass name of the active catalogs (`catalog:glass` picks a catalog),
  `MIRROR` (any case), a constant index relative to the system's air, or a model glass (nd, Vd,
  ΔPgF; `ModelGlassFit`, trained on the active catalogs). Nothing is the system's air. The
  editor's material cell reads `N-BK7`, `MIRROR`, `1.5` and `1.6, 40` or `1.6, 40, 0.01`.
- **Generic data.** The surface type is a name (null is Standard); its numeric data is a
  numbered parameter list (OpticStudio's Par columns) and an extra-data list; the per-cell
  flags are kept as given: `Variable`, `Solve` with its name and parameters, `Pickup` with the
  source surface (0-based), source column, scale and offset. Column names are
  `SavedSurfaces`' (`Radius`, `Thickness`, `Material`, `SemiDiameter`, `Conic`, `Par3`, …).
- **Surface types** (`OpenSim.Optics/Sequential/SurfaceTypes.cs`). A registry gives each type
  its OpticStudio parameter columns: number, name, unit as a power of length (a value v in lens
  units is v·u^p in SI, u meters per lens unit), integer columns and their ranges. Each layout
  is copied from the type's OpticStudio 2025 R2 help page, cited on the layout: Even Asphere,
  Odd Asphere, Alternate Even, Alternate Odd, Extended Asphere, Extended Odd Asphere, Q-Type
  Asphere, Superconic, Periodic (`plans/sources/optics/aspheres/`); Fresnel, Extended Fresnel,
  Cylinder Fresnel, Generalized Fresnel, ABCD, Conjugate, Paraxial, Paraxial XY, Retro Reflect
  (`plans/sources/optics/ansyshelp-op27/`); Coordinate Break (`plans/sources/optics/tc6-coordbreak/`).
  The registry builds the shape from SI parameters and reads them back exactly. Readings:
  - Parameters 13 and above are saved in the extra-data list with the same number. The help
    pages number these columns as parameters and call them extra data.
  - A "maximum term number" N uses the first N coefficients and ignores the rest ("terms
    beyond this number are ignored", Extended Asphere). Q-Type: A0 … A(N − 1). Superconic: a
    term is one U or one V value, so N = 3 is U1, V1, U2. A shape read back writes N as the
    number of coefficients it holds.
  - Units: an Even Asphere's αᵢ on r²ⁱ is in lens units^(1 − 2i), an Odd Asphere's βᵢ in
    lens units^(1 − i), a Superconic's Uᵢ in lens units^(−2i) and Vᵢ in lens units^(−2i − 1),
    Periodic α and β and Paraxial XY powers in inverse lens units, ABCD B in lens units and C
    in inverse lens units, the Fresnel curvatures in inverse lens units, normalization radii
    and the Extended, Q-type and Generalized Fresnel coefficients in lens units.
  - Paraxial, Paraxial XY, ABCD, Retro Reflect, Conjugate and the coordinate break have no
    radius or conic; a value there is refused rather than ignored.
  - Paraxial OPD mode 2 is infinite conjugates and mode 3 takes its conjugates from the solved
    lens (`ParaxialShape.ResolveConjugates`, run by `ProjectLens`); modes 0 and 1 are refused.
  - Coordinate break order: 0 is decenter then tilt; any other value tilt then decenter
    ("any other value (such as unity)"), written back as 1.
  A type that is not registered is saved and shown by parameter number and refused when the
  lens is converted, naming the surface and the type.
- **Tilts and returns.** `TiltBefore` and `TiltAfter` (`OpticsTiltDecenterSettings`: decenters
  in lens units, tilts in degrees, order `DecenterThenTilt` or `TiltThenDecenter`; the after
  record's `Mode` is `Explicit`, `PickUpThisSurface`, `ReverseThisSurface`, `PickUpSurface` or
  `ReverseSurface`, with `Surface` for the last two) follow OpticStudio's Surface Properties ›
  Tilt/Decenter page and map onto `SequentialSurface.TiltBefore` and `TiltAfter` (section 15).
  A coordinate break's `CoordinateReturn` (`None`, `OrientationOnly`, `OrientationXY`,
  `OrientationXYZ`, and the prior surface) becomes `CoordinateBreak.Return` and
  `ReturnToSurface`, with the entered values kept, so orientation only keeps the break's
  decenters.
- **Conversion** (`OpenSim.Optics/Sequential/SavedSurfaces.cs`). `ToLens` gives the
  `SequentialLens` in meters (radius × meters per lens unit, curvature by
  `OpticsConventions.CurvatureFromRadius`, each parameter by its column's power), with every
  problem named, one line per surface, never traced as something else: a parameter the type
  does not have, a value the shape refuses, a radius on a type without one, a material on a
  coordinate break, an unknown tilt order or mode. `FromLens` writes a lens built in code
  back; a shape whose type is not registered is refused. In meters every factor is 1, so a
  lens round-trips bit for bit (`SavedSurfaceTypesTests`).
- **Solves** (`SavedSolves.cs`, `Analysis/ProjectLens.cs`). `ProjectLens.Solve` converts the
  saved lens, translates the flags into section 19's `SurfaceSolve` records, and runs
  `SolveEngine.Apply` with the project's system data and active catalogs. The analyses read the
  solved lens through `ProjectLens`; a failing solve throws an `ArgumentException` whose lines
  name the surface, the cell and the solve. A lens without flags is converted as before,
  without reading the system data.
  - Solve names are the editor's, which are OpticStudio's "Summary of Solves" names, with
    that table's parameters in lens units: heights, edge thicknesses, radial heights,
    lengths and sums × u; element power ÷ u; angles, F/#, pupil zones and waves as they are;
    surface numbers must be whole.
  - Columns map to `SolveColumn`: Radius → curvature, Thickness, Material, SemiDiameter,
    Conic, Tce, and `Par n` → parameter n for n ≤ 12 (OpticStudio's pickup column codes reach
    "Parameter 0 through 12"). Parameter pickups read and write shape parameters through the
    registry, and a coordinate break's six values (decenters in meters, tilts in degrees).
  - A pickup T = O + S·V given in lens units becomes O·f_t + (S·f_t/f_s)·V in SI, f_t and f_s
    the SI factors of the target and source columns (a parameter's from its type). A
    curvature picks up a curvature with the scale unchanged.
  - Kept, not evaluated: variables (the optimizer is not built); the semi-diameters'
    "Automatic", which stays the sizing of section 13; chip-zone and mechanical
    semi-diameter pickups, which have no column in OpticStudio's Summary of Solves (System
    Check warns).
  - Refused, naming the cell: an unknown solve name or flag kind, a pickup from a surface that
    does not exist or from a column a pickup cannot read, a solve or pickup on extra data, and
    the parameter chief ray solve (not built).
- **Editor** (`OpticsLensEditorViewModel`, `OpticsLensEditorSchema`). Every registered type is
  a row type with its parameters after the shared columns, named from the help page and
  explained (with the unit) in the tooltip; the first 32 are shown and the rest kept. A
  coordinate break has comment, thickness and its six parameters; its flyout has the
  coordinate return. Other types' flyouts have the tilt and decenter before and after. After
  every edit the editor shows each solved value in its cell (no undo step) and puts a failing
  solve's message on its cell (badge "!", tooltip); the lens check lists the failures.
  Changing a row's type keeps the shared columns and resets the parameters: this program's
  reading, since OpticStudio's "Type (surface properties)" page does not say.
- **Editing tools** (`OpenSim.Optics/Sequential/LensEditing/`), each read from OpticStudio
  2025 R2's help (the files are in `plans/sources/optics/ansyshelp-op31/`):
  - *Reverse Elements* (first to last surface): surface first + i becomes old surface last − i
    with its radius negated; the thickness and material after it are those after old surface
    last − 1 − i, and after the new last surface those after the old last, so the gaps before
    and after the group stay. Solves and pickups on the reversed radius, thickness and material
    cells are removed (the help: solves on affected data are removed); variables move with
    their values. Mirrors, surface tilts and every type but Standard in the group are refused
    by name.
  - *Scale Lens* (factor, first to last surface): lengths × factor (radius, finite thickness,
    semi-diameters, chip zone, aperture sizes and decenters, tilt decenters), each parameter of
    a registered type × factor^p of its column ("Scale Lens": units that change from term to
    term are accounted for), and pickup offsets by the target column's unit; not the conic,
    TCE, tilts, integer parameters, wavelengths, solve parameters or the system data. A type
    that is not registered is refused by name. The lens-unit change converts the same way and
    leaves an unregistered type's parameters with a note. *Make Focal* scales every surface by
    target / current paraxial effective focal length at the primary wavelength; a sign change
    and an afocal lens are refused.
  - *Make Double Pass* (reflect at surface k): k becomes a mirror with thickness −t(k−1); copies
    of surfaces k−1 … 1 follow with the same radius and conic, thickness −t(j−1) and the
    material that was before surface j, picking radius, conic and thickness (scale −1) up from
    the originals; the image follows at −t0, on the object surface; with the object at
    infinity it is placed at thickness 0. All solves are removed. A mirror before k, a surface
    tilt, or a type but Standard is refused.
  - *Remove All Apertures*; *Convert Semi-Diameters to Circular / Floating / Maximum
    Apertures* on surfaces 1 to the image without an aperture (circular: a fixed semi-diameter
    and a circular aperture of that radius, also on a floating aperture with a picked-up
    semi-diameter; floating: the semi-diameter mode is kept; maximum: the automatic size of
    the one configuration, fixed, with the "Maximum" solve stored).
  - *Remove All Variables* (variables only); *Set All Radii Variable* (Standard, curved, the
    material differs from the one before); *Set All Thickness Variable* (surfaces 1 to the last,
    finite and non-zero). Cells with a solve or pickup are left.
  - *System Check*: errors (system data, conversion, solves that fail or cannot be evaluated)
    and warnings (unknown coating, pickups kept but not evaluated, a pickup from a surface
    that does not exist, glasses not covering a wavelength, chief and marginal rays that
    fail). *File Comparator*: every field of the system data and of each surface, by default
    only where they differ.
- **Not built.** Odd Cosine (its help page lists parameters 31–54 as "the A, P, B, and C
  terms" without their order) and Lenslet Array (a user-defined DLL whose parameters the page
  does not list) are not registered; Paraxial OPD modes 0 and 1; solves and pickups on extra
  data, the parameter chief ray solve, chip-zone and mechanical semi-diameter pickups;
  Reverse Elements and Make Double Pass on other types and tilted surfaces; the optimizer that
  would use variables; configurations (OP-30); Create Error Ray (non-sequential sources); the
  surface properties' scattering, physical optics and import tabs; coating layer multipliers on
  a surface; and the drawing options (hide rays, do not draw) in the layouts. Section 10,
  item 16, lists what is to compare with OpticStudio.

## 21. Polarization ray trace

`OpenSim.Optics/Polarization` (OP-51) carries the electric field along a real ray of section 13.
`PolarizationTracer` reads the surface-by-surface record of `SequentialTrace.TracePath`
(normals, directions, indices, segment lengths) and does not change the trace loop. Yun,
Crabtree and Chipman, "Three-dimensional polarization ray-tracing calculus I and II",
Appl. Opt. 50 (2011) 2855 and 2866, were not read (no free copy was found); the construction
below is derived in `PolarizationMatrix.cs` and the comments of `PolarizationTraceTests`.
OpticStudio's help pages on polarization (System Explorer "Polarization", "Defining the Initial
Polarization", "Convert Thin Film Phase to Ray Equivalent", "Unpolarized", "Bulk Absorption and
Transmission", and the five analyses) are saved under `plans/sources/optics/ansyshelp-op51/`.

- **Frame and convention.** Fields are three complex components in surface 1's frame (the
  global frame of section 13), in this program's convention of section 12: a wave
  e^(i(k·r − ωt)), index n + ik. Phases shown to the user, and entered as the Jones X-phase and
  Y-phase, are OpticStudio's: φ = −arg. `Ray.Field` of the final ray is in the image surface's
  frame, and `Ray.Intensity` is |E|² there.
- **Launch.** A Jones vector (Jx, Jy), normalised to unit intensity, is the field
  Jx·Ŝ + Jy·P̂ with K the ray direction and, by the method chosen (OpticStudio's three rules):
  X axis P = K × X̂, S = P × K; Y axis S = Ŷ × K, P = K × S; Z axis S = K × Ẑ, P = K × S. Where a
  rule is undefined (K along its axis), S is the part of x̂ (ŷ when K lies along x) at right
  angles to K and P = K × S. `JonesVector.LaunchBasis`.
- **Power normalisation.** |E|² is the ray's power relative to launch at every point. An
  amplitude coefficient is a = √T·e^(i arg t) in transmission and √R·e^(i arg r) in reflection,
  T and R the coating engine's power fractions (section 12, which carry the n·cos θ factors)
  and arg the phase of its amplitude. The propagation phase 2π·OPL/λ is not in the field (it
  is the ray's optical path), so a component's phase is the polarization phase alone.
- **Interface matrix.** ŝ = k̂_in × η̂/|…| (η̂ the normal), p̂_in = k̂_in × ŝ,
  p̂_out = k̂_out × ŝ, and P = a_s ŝŝᵀ + a_p p̂_out p̂_inᵀ + k̂_out k̂_inᵀ: an orthogonal change
  into the local s, p, k frame, a diagonal action, and the change back into the exit frame.
  In this frame a bare interface has r_p = (n2 cos θi − n1 cos θt)/(n2 cos θi + n1 cos θt), so
  r_p = −r_s at normal incidence, which is the sign of section 12's p amplitudes. At normal
  incidence ŝ is any transverse unit vector (P does not depend on it). For a surface that turns
  the ray out of its plane of incidence (a redirecting shape), ŝ_out is the part of ŝ at right
  angles to k̂_out, which keeps the field transverse; OpticStudio's help says such surfaces do
  not have valid polarization data, and neither do they here.
- **Coefficients.** `CoatingEvaluator` at the angle of incidence, the incident and substrate
  indices of the trace (relative to the system's air, like the coating file), the trace's air
  wavelength, the primary wavelength for relative thicknesses, and the hit point and radius in
  lens units for tapers. Uncoated: the bare Fresnel interface; uncoated mirror: aluminium.
  Layers are reversed from glass to air, glass being any medium but index-1 air. Mirrors use
  R; refracting surfaces T.
- **Ray-equivalent phase** (on by default, as in OpticStudio). A layer coating's amplitude
  relates the field at its outer face to the field at the substrate; the trace goes straight
  to the substrate with the coating grown into the space before it. Between the planes z = −D
  and z = 0 a plane wave of the incident medium gains ψ = 2π·n0·D·cos θ0/λ at a fixed
  transverse point, D the coating's physical thickness (on a mirror without the substrate
  layer), so the coefficients applied are t·e^(−iψ) and r·e^(−2iψ). An index-matched layer then
  gives the bare coefficients exactly (a gate test). Ideal and table coatings have no
  thickness and are not converted.
- **Bulk absorption.** The segment ending on surface j attenuates the field by e^(−ατ/2), τ its
  signed geometric length and α the intensity coefficient of the space after surface j − 1
  (`BulkAbsorption`): from a glass's IT table, α_i = −ln(t_i)/τ_i per mm, linear in wavelength
  between points and refused outside them; else α = 4πk/λ from the extinction coefficient;
  else zero. The launch segment of an object at infinity is not attenuated.
- **Unpolarized** light is the mean of the transmissions of (1, 0) and (0, 1) (OpticStudio help,
  "Unpolarized"); any orthonormal pair gives the same mean, since |PE|² is a Hermitian form.
- **Per surface** (`PolarizedSurface`): the relative transmission is |E|² after the surface over
  |E|² after the previous one (the bulk transmittance of the material before times the
  surface's transmission, OpticStudio's "Rel. Tran"); the total is |E|² after it.
- **System matrix.** The product of the interface matrices and bulk factors; its diattenuation
  (T_max − T_min)/(T_max + T_min) comes from the eigenvalues of the 2 × 2 Gram matrix of its
  action on the transverse plane at launch.
- **Analyses** (Polarization group): Polarization ray trace; Polarization pupil map (ellipse of
  Re(E e^(−iωt)) of the local Ex, Ey; orientation ½·atan2(2 Re(Ex Ey*), |Ex|² − |Ey|²);
  transmission the mean of |E|², Ez included, over the grid); Transmission (mean over a square
  pupil grid, failed rays zero; chief ray per surface); Phase aberration (phase of the image
  Ex, Ey minus the chief ray's, in waves, wrapped to (−½, ½]; a chief component below 10⁻²⁴ of
  the ray's power takes the phase of the two nearest fan rays); Transmission fan.
- **System polarization** (`SystemPolarization` on `OpticalSystemData`, saved as the nullable
  `OpticsSystemSettings.Polarization`): unpolarized or a Jones vector, the method, and the
  phase conversion. The five analyses take their own Jones vector, as OpticStudio's do.
- **API for other analyses.** `PolarizationTracer.Transmission(RayPath, SystemPolarization)`
  gives the power a traced ray carries to the image for the system polarization.
- **Gates** (`PolarizationTraceTests`): Fresnel amplitudes against the derived closed forms and
  R + T = 1 at bare and dielectric-coated interfaces; zero p reflection at Brewster's angle
  tan θ = n2/n1; normal-incidence transmission of an uncoated lens (1 − R)², times the internal
  transmittance for an absorbing glass; unpolarized transmission the mean of any orthogonal
  pair; the field transverse to the ray at every surface; normal incidence on a mirror scaling
  the whole field by r_s; the index-matched layer above.
- **Not built.** Real transmission in the other analyses (the API exists; nothing else calls
  it yet); pupil apodization and vignetting factors (OP-22); retardance of the system; Jones
  and Mueller matrix surfaces (OP-52); birefringent media (OP-53); gradient-index polarization;
  non-sequential polarization; interference between configurations in the pupil map. The
  points not compared with OpticStudio are section 10, item 17.

## 22. Cross-checks against independent programs

OP-102 compares OpenSim with independently written open programs through stored reference
files in `OpenSim.Tests/References/OP-102`, written once by the committed scripts there
(`gen_optiland.py`, `gen_rayoptics.py`, `gen_tmm.py`) and read by
`OpenSim.Tests/Optics/ReferenceProgramTests.cs`; the test suite never runs Python. The programs
are Optiland 0.6.3 (MIT), RayOptics 0.9.10 (BSD 3-Clause) and tmm 0.2.0 (MIT); the folder's
README lists the environment, how to regenerate, and where each convention below was read in
each program's source. When the files are absent the tests are reported as skipped. OpticStudio
was not used (section 10, item 18).

**Lenses.** Five lenses with constant indices (no catalog glass, so all three programs certainly
use the same index): a singlet with a prolate front, a cemented doublet at a finite conjugate
with object-height fields, a Cooke-triplet-like lens with a separate plane stop and an oblate
surface, a paraboloid mirror, and a paraboloid–hyperboloid two-mirror lens. 24 rays each
(three fields, eight pupil points including skew rays). Wavelength 0.55 µm.

**Mapping.**

| Matter | Optiland | RayOptics | OpenSim |
|---|---|---|---|
| Length unit | lens units, mm here | lens units, mm here | meters (the tests multiply by 10⁻³) |
| Wavelength unit | µm | nm | µm in the table |
| Radius and thickness signs | as section 3 | as section 3 | section 3 |
| Index after a mirror | positive (medium before kept) | positive (medium before kept) | positive |
| Local frame after a mirror | not turned | not turned (translation only) | not turned |
| Optical path | Σ t·n from the launch point | Σ n·dst per gap (summed by the script) | Σ n·s from the launch point |
| Launch, ray aiming off | "paraxial" aimer: paraxial entrance pupil, direction (tan θx, tan θy, 1) | chief ray aimed at the real stop; not used: the script builds the launch from RayOptics' paraxial pupil with OpenSim's rule | `RayLauncher` (section 13) |
| Image-space focal length n′/Φ | f2 | efl (= fl_img) | `ImageSpaceFocalLength` |
| Object-space focal length −n/Φ | f1 | −fl_obj | `ObjectSpaceFocalLength` |
| Front focal point, principal plane (from surface 1) | F1, P1 | ffl, pp1 | `FrontFocalLength`, `FrontPrincipalPlane` |
| Back focal point, rear principal plane (from the last surface) | F2 + t_last, P2 + t_last (Optiland measures from the image surface) | bfl, ppk | `BackFocalLength`, `RearPrincipalPlane` |
| Entrance pupil position and diameter | EPL, EPD | enp_dist, 2·enp_radius | `EntrancePupilPosition`, `EntrancePupilDiameter` |
| Exit pupil position (from the last surface) | XPL + t_last | exp_dist − img_dist + t_last | `ExitPupilPosition` |
| Exit pupil diameter | \|XPD\| | 2·exp_radius | \|`ExitPupilDiameter`\| |
| Paraxial image (from the last surface) | not compared | img_dist | `ParaxialImageDistance` |
| Thin films | — | — | tmm `coh_tmm`: same convention as section 12, compared without mapping |

**What is compared and to what tolerance.**

- *Real rays.* Each stored ray is launched in OpenSim from the program's own launch point and
  direction, and at every surface from 1 to the image the local intersection point must agree
  to 10⁻⁹ mm, the direction cosines to 10⁻¹², and the optical path from the launch point to
  10⁻⁶ waves (5.5·10⁻¹³ m at 0.55 µm), with each of the two programs. Optiland and RayOptics
  agree with each other to 2.7·10⁻¹³ mm and 1.5·10⁻¹⁵ (README).
- *Launch.* OpenSim's launcher against Optiland's paraxial aimer: the same direction (10⁻¹²)
  and OpenSim's start point on Optiland's ray line (10⁻⁹ mm). Only the line is compared,
  because the two start the ray at different points along it.
- *First-order data.* Against Optiland, every quantity of the table to 10⁻⁹ mm. Against
  RayOptics, the system-matrix quantities (focal lengths, focal points, principal planes) to
  10⁻⁹ mm; the conjugate-dependent ones (pupils, image distance) to 10⁻⁹ mm with a finite
  object and to 10⁻⁹ mm + 2·EFL²/L for an object at infinity, because RayOptics stands in an
  object at L = 10¹⁰ mm, which moves an image by f²/L (Newton's equation). The stored files show
  exactly that shift (10⁻⁶ mm for the 100 mm mirror, 3.6·10⁻⁵ mm for the 600 mm two-mirror lens).
- *Thin films.* Four stacks (a single layer, nine high/low layers, an absorbing metal layer on
  an absorbing substrate, and glass to air through total internal reflection) at 2–5 angles and
  1–3 wavelengths: r and t (complex, so their phases), R, T and the absorbed fraction
  (power_entering − T) to 10⁻¹².

**Not built.** Catalog glasses in the cross-checks, tilts and decenters, aspheres, ray aiming,
OPD against a reference sphere, diffraction (prysm and POPPY, later items), and any OpticStudio
reference file.
