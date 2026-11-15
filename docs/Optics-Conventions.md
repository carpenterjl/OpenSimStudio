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
scripting surface (OP-98, `OpticsScripting.TraceRay`) maps `RayStatus` and `FailedAtSurface` to
those codes (section 47).

## 6. Optical path difference

- OPD compares a ray with the chief ray of the same field and wavelength. Both paths are
  measured to the reference sphere, which is centred on the image point and passes through the
  centre of the exit pupil.
- **OPD = chief path − ray path.** A positive OPD means the ray's wavefront is ahead of the
  reference sphere. `OpticsConventions.OpticalPathDifference`.
- It is shown in waves of the ray's own wavelength. `OpticsConventions.InWaves`.
- This sign is one of the open checks in section 10.
- The reference surfaces (the sphere, its plane limit, the afocal planes) and the OPD analyses
  are in section 26.

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
   rule is documented for the older interfaces only. OP-98 took it from the ZPL help (RAYTRACE,
   RAYE and RAYV); item 43 lists what that page does not settle.
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

19. **Toroidal, biconic and irregular surfaces** (OP-24). Section 23's surfaces follow their
    OpticStudio 2025 R2 help pages; these points are this program's reading where a page is
    silent, and none is compared with OpticStudio's output.
    - *Toroidal rotation:* the sheet z = R − sign(R)·√((R − z_y)² − x²) through the vertex, read
      from the help's two figures; the sag is undefined where the curve crosses the axis of
      rotation.
    - *Irregular r_max:* the surface's fixed clear semi-diameter; an automatic one is refused
      when Zs, Za or Zc is non-zero. The help says "the clear semi-diameter or semi-diameter
      value" without saying which value an automatic semi-diameter gives.
    - *Irregular tilt order:* the help's "decentered, tilted about x, then about y" taken as
      the coordinate break of order 0 with no z tilt (§4).
    - *Paraxial model:* the y–z vertex curvature, and a refusal for a vertex off z = 0, a slope
      at the vertex or ∂²z/∂x∂y ≠ 0 (a Zernike piston, defocus, tilt or 45° astigmatism term,
      a linear x or y term, a decentered or tilted Irregular surface, an Irregular Za at an
      angle that is not a multiple of 90°). What OpticStudio's paraxial data do with these was
      not read.
    - *Extrapolate flag 0:* the Zernike terms are dropped for ρ > 1 (sag and slope), so the
      surface has a step at the normalization radius, as the help's words imply.
    Check: one lens per type traced in both programs (ray positions and directions), and the
    paraxial data of a lens holding a Zernike defocus term.

20. **Multi-configuration readings** (OP-30). Section 24's operands follow the help's table, but
    no configuration is compared with OpticStudio's. Check, with a two-configuration zoom saved
    in both programs (Multi-Configuration Editor and Prescription Data per configuration):
    - *An operand over a solved cell* drops the solve in that configuration; OpticStudio's
      precedence was not read.
    - *Insert Configuration* copies the column at the cursor; what OpticStudio's new column
      holds was not read.
    - *PUCN* applies to fixed and variable cells only, and a negative PUCN is refused.
    - *APMX on a spider* is the number of arms, read from the order of the help's sentence.
    - *SDIA and MCSD on an automatic semi-diameter* read no value (Add All Data skips them);
      writing one fixes the cell.
    - *Next and Previous* stop at the ends; whether OpticStudio wraps was not read.

21. **Ray fans, spot diagrams and footprints** (OP-34). Section 25's readings of OpticStudio's
    help that its output was not compared with:
    - *Pupil patterns:* hexapolar rings at radii k/N, the square grid cell-centred (n × n,
      2/n apart), and a dithered pattern of as many rays as the hexapolar one, uniform over the
      disk from a fixed seed. The help gives the ray counts of the hexapolar pattern only.
    - *Wavelength weights:* every wavelength traces the full pattern and each ray weighs its
      wavelength's weight in the RMS and the centroid; OpticStudio instead thins the grids of
      lower-weight wavelengths. The two agree in expectation, not ray for ray.
    - *Working F/# off axis:* θ is taken between the chief ray and each of the four marginal
      rays at the image surface; the help names "the axis ray" without defining it further.
    - *Chief ray as reference:* traced with apertures ignored, so an obscured chief ray still
      gives a reference point; the help does not say what OpticStudio does then.
    - *Incident angle sign:* atan2(M, |N|) in the image surface's local frame.
    Check: the standard spot (RMS, GEO, Airy radius), a ray fan and a footprint of a singlet at
    two fields in OpticStudio, with all three patterns.

22. **OPD references and the OPD analyses** (OP-35). Section 26 follows OpticStudio's help
    wording; these readings are not checked against its output:
    - *All wavelengths referenced to the primary* (the OPD fan with every wavelength, the
      Wavefront Map's "Reference To Primary"): the sphere's centre is the primary wavelength's
      chief ray (or centroid), but each wavelength's OPD is against its own chief ray, so there
      is no piston between wavelengths. The help says "the primary wavelength based reference
      sphere and chief ray"; if OpticStudio also takes the primary chief ray's path, its OPD at
      other wavelengths carries a constant that this one does not.
    - *Centroid reference:* the sphere is centred on the mean image-surface intercept of a
      square pupil grid. The help ("RMS vs. Field") speaks of a mean-shifted, tilted reference
      sphere with Rimmer's correction; that correction is not built.
    - *Telecentric image space:* the exit-pupil reference becomes the plane through the image
      point normal to each ray (the Infinity formula). The help does not say what OpticStudio
      does when the exit pupil is at infinity.
    - *Afocal exit-pupil plane:* placed at the point of the chief ray's line nearest the
      paraxial exit pupil centre, normal to the reference direction.
    - *Interferogram tilt:* "the number of fringes of tilt" is taken across the full pupil
      diameter.

    Check: a singlet with spherical aberration and lateral colour at two wavelengths, its OPD
    fan (one and all wavelengths, chief and centroid), wavefront map and interferogram compared
    with OpticStudio's; a telecentric lens's OPD fan. This also settles item 2.

23. **Field curvature, distortion, colour and relative illumination** (OP-37, section 27). Read
    from the help pages' wording; no OpticStudio number was compared:
    - *Field curvature* is measured from the chief ray's image point along the image surface's
      z axis, positive in the direction the light travels; the help says "along the Z-axis from
      the image surface to the paraxial image surface" without a sign rule.
    - *Distortion's reference*: f is the slope of the real chief ray's radial image height
      against tan θ (or θ, or the field height) from rays at H = ±10⁻⁴; the help says "tracing a
      real ray from a very small field height, and then scaling".
    - *Calibrated distortion*: the best-fit f is the least-squares Σ r·g/Σ g² over the scan; the
      help says only that it "selects the best f value to minimize the distortion".
    - *Field scans* run to the largest radial field (fields normalised radially for the scan),
      and a real-image-height system is scanned in paraxial image height for distortion, where
      OpticStudio switches to field angle.
    - *Grid distortion*: symmetric magnification is the nearest rotation and scale of the ABCD
      matrix, A = D = (A + D)/2, C = −B = (C − B)/2; SMIA-TV with a failed ray is reported as not
      computed, where the help lists zero.
    - *Lateral colour*: the sign is y(shortest) − y(longest), or y(λ) − y(primary).
    - *Relative illumination*: the integral is this program's derivation in pupil coordinates
      (Rimmer's paper was not available), not the help's uniform grid in cosine space; the
      effective F/# uses geometric direction cosines with no n′ factor; apodization weights the
      integrand as transmission does.
    Check: a singlet's field curvature, F-tan and calibrated distortion, grid distortion and RI
    against OpticStudio's text output.

24. **Surface shape analyses** (OP-43). Section 28's readings of OpticStudio's help are not
    compared with its output. Check each on one Even Asphere in both programs (Sag Table,
    Surface Sag, Surface Curvature, Surface Slope and their cross-sections):
    - *RMS criteria:* the RMS is area-weighted (2πr dr) over the annulus; the help does not say
      how OpticStudio weights it.
    - *BFS Sag and Deviation:* BFS Sag is the sphere's sag from its own vertex and Deviation is
      BFS Sag − Sag, both without the vertex offset; Remove includes the offset. The help says
      Deviation ignores the offset but not whether BFS Sag does.
    - *Air side and Reverse Direction:* the lower-index side is air (the side the light arrives
      from on a mirror); Reverse Direction takes the other side, as the Sag Table page says. The
      Surface Sag page says instead that it "changes the sign on the radius of curvature of the
      best-fit sphere"; the two were not reconciled.
    - *Curvature modulus:* read as the larger magnitude of the two principal curvatures; the help
      says only "the maximum length of the curvature vector".
    - *Tangential and sagittal:* read as the normal curvatures and slopes along the radial and
      azimuthal directions from the vertex; the help names them without a formula.
    - *Remove on slope and curvature maps:* the removed sphere's own slope or curvature at the
      same point is subtracted; the help says the base radius is "subtracted from the current
      curvature" (or slope) without a formula.
    - *Automatic step* of the Sag Table and the default sampling of 65: OpenSim's choices.

25. **Report readings** (OP-44). Section 29's report layouts are this program's (the help lists
    the sections, not their columns), and these readings of OpticStudio are not compared with
    its output: the edge thickness taken at the surface's own clear semi-diameter; a cemented
    doublet listed as two elements; the clear (not mechanical) semi-diameter used for element
    volumes; non-rotational faces squared up rather than tapered; the cardinal-plane signs
    (positions along z from the first and last vertex, as §11); the surface focal lengths n′/φ
    and −n/φ and the element focal length 1/φ in Surface Data; and Index Data's TCE column as
    the glass's catalog value. Check: Prescription Data, Surface Data and Cardinal Points of a
    singlet, a cemented doublet and an immersed-image lens in both programs.

26. **Jones and Mueller matrix surfaces** (OP-52). Section 30's readings of OpticStudio's help
    are not compared with its output. Check with a Jones surface at normal and at 20° incidence
    (Polarization Ray Trace listing):
    - *Complex convention:* the entries Ar + i·Ai are taken in the convention of OpticStudio's
      Jones-vector phases, so this program applies their conjugates; the help does not say.
    - *Frame:* Ex and Ey along the surface's local x and y axes.
    - *Off normal incidence:* the matrix acts in the transverse basis (û, v̂) built from the
      local x axis; OpticStudio's adjustment of Ez or of Ex and Ey is not given as a formula.
    - *Interface:* the surface is also an ordinary interface (Fresnel or coating), and the matrix
      acts after it.
    - *Image surface:* a matrix type on the image surface is not applied (no interface is).

27. **Merit function readings** (OP-54). Section 31's rules come from OpticStudio's help, but
    none is compared with its output, and some are this program's reading where the help is
    silent:
    - *Parameter columns.* The help names each operand's parameters (Surf, Wave, Op#, Flag,
      Factor) but places only some of them: Data1 to Data4 are Hx, Hy, Px, Py; CTGT, CTVA,
      CVVA and COVA take their surface in Int1; TTHI, SUMM, DIVI, PROD and DIFF take theirs in
      Int1 and Int2; OPGT its row in Int1. Wave in Int2 (EFFL, REAX, REAY, REAZ, REAR), the
      trigonometric Flag in Int2, and DIVB's and PROB's Factor in Data1 are read by that pattern,
      not from the help.
    - *Wavelength numbers* start at 1; Wave 0 is refused (whether OpticStudio reads it as the
      primary was not found).
    - *A failed ray* fails its row only, scored with the penalty 10⁶ in place of V − T; the help
      does not say how OpticStudio scores it. A ray that fails after the surface a row reads
      does not fail the row.
    - *Rotational symmetry* for SKIS and SKIN is tested on the lens alone (no tilt, decenter or
      coordinate break; Standard, radial asphere or Paraxial surfaces only); the help gives no
      test.
    - *Math rows* read earlier rows only (the help says "prior" for DIVB alone); OSUM, QSUM,
      MAXX and MINN read their range inclusively; GOTO, SKIS and SKIN jump forward only.
    - *MXCT* is read as MNCT with the sign turned (the help's worked example is MNCT's).

28. **Seidel and full-field aberration readings** (OP-36, section 32). Read from the help pages
    "Seidel Coefficients", "Seidel Diagram" and "Full-Field Aberration"; no OpticStudio number
    was compared:
    - *Wave column.* The page's relations S1 = 8 W040, S2 = 2 W131, S3 = 2 W222,
      S4 = 4 W220 − 2 W222, S5 = 2 W311 are used. Its table prints S1/2, S3/4 and (3 S3 + S4)/2
      for W040, W222 and W220T, which contradict them; W220T is taken as (3 S3 + S4)/4.
    - *Transverse sign.* The page writes every transverse coefficient as −k·S/(n′u′). With this
      program's slope u′ = dy/dz that is the negative of the image-plane ray error, so the minus
      sign is dropped (the page's u′ is read as measured the other way); TSPH then equals the
      ray fan's cubic coefficient.
    - *Longitudinal form* k·S/(|n′|u′²): positive when rays cross the axis before the paraxial
      image along the light; the absolute index after mirrors is this program's reading.
    - *Colour* uses δn = n(shortest) − n(longest) of the table, and the chief ray is that of the
      largest radial field; the help says only "the extreme defined wavelength".
    - *Odd terms.* Odd and Extended Odd Aspheres with an r or r³ term are refused; the help lists
      the types as supported without saying how those terms enter.
    - *Petzval radius* uses signed indices in each term and |n| of image space.
    - *Full-field aberration:* Relative and Average act on the plotted magnitude (the help's
      per-term averaging is for its icon display, not built); the OPD is on the exit-pupil
      reference sphere centred on each point's chief ray; vignetting factors are cleared; the
      default half-width (0) is the largest radial field.
    Check: Seidel Coefficients text of a singlet and of a two-mirror telescope, and Full-Field
    Aberration of a singlet, in both programs.
29. **Point spread function readings** (OP-38). Section 33 follows OpticStudio's help, but no
    PSF is compared with its output, and some rules are this program's where the help is silent:
    - *Default FFT image delta.* The help stretches the pupil grid (Δ = λF(n − 2)/(2n)·(32/n)^½);
      here the grid is not stretched, Δ = λ/(n·|a|·2n·ΔP), so for the same sampling OpticStudio's
      image is finer and its pupil sampled more coarsely.
    - *Sampling thresholds.* ½ and ¼ wave per pupil sample are derived here; OpticStudio's own
      thresholds and messages were not found.
    - *Polychromatic weights.* w·(λ_min/λ)² over each wavelength's unaberrated peak, read from the
      Huygens page's "wFactor" and applied to the FFT PSF as well; the help gives none for the FFT.
    - *Huygens weights.* Pupil amplitude × √(direction-space Jacobian), derived; OpticStudio's
      weighting is not stated. Use Centroid centres on the A²-weighted mean intercept.
    - *Strehl.* The peak is refined off the grid; OpticStudio may report the largest sample.
    - *Edge spread* runs from 0 to 1 across the window; the help does not give its normalisation.
    Check: FFT and Huygens PSFs of a singlet off axis, saved by OpticStudio as text, compared here
    at the same sampling.
30. **RMS against field, wavelength and focus** (OP-40, section 34). Read from the help pages'
    wording; no OpticStudio number was compared:
    - *Gaussian quadrature* is derived (Gauss–Legendre in ρ², equally spaced arms from +x), not
      copied from Forbes's paper, which was not available; OpticStudio's ring radii, arm angles
      and number of arms for the RMS analyses may differ (here 4 arms per ring). The values agree
      wherever both rules are exact.
    - *Centroid wavefront reference*: the weighted least-squares piston and tilt in pupil
      coordinates are removed; the help's "mean shifted, tilted reference sphere" with Rimmer's
      correction is not built.
    - *Polychromatic wavefront* in waves of the primary wavelength, one mean and tilt over every
      ray of every wavelength; the help does not name the unit.
    - *Field scan extent*: the largest defined field component in the scan direction ("the
      limits of the defined fields in the selected direction"); OP-37's scans use the largest
      radial field instead.
    - *Remove Vignetting Factors* replaces the factors with surface apertures, as in OP-37.
    - *Field Map automatic size*: a field size of 0 means the largest radial defined field; the
      help gives no default.
    - *Strehl ratio*: the help computes it "using the method described in "Strehl ratio
      approximation"", a section not in the online help; the PSF-peak ratio of its "Strehl
      Ratio" page (OP-38's engine) is used instead, at a PSF sampling of the program's choosing.
      The help says it "is only available for monochromatic calculations"; here a
      polychromatic curve uses OP-38's polychromatic FFT PSF.
    Check: RMS vs Field (wavefront and spot, chief and centroid, GQ and RA), RMS vs Focus and
    RMS vs Wavelength of a singlet at two fields and three wavelengths against OpticStudio's
    text output.
31. **Local optimization readings** (OP-57). Section 35 follows OpticStudio's help (Optimize!;
    Modifying the merit function), but none of it is compared with OpticStudio's output:
    - *Orthogonal Descent* is described in the help only as an orthonormalization of the
      variables with discrete sampling and no derivatives; this program's is Rosenbrock's
      rotating-coordinates search, with step factors of its own choosing.
    - *A cycle* is read as one derivative-and-step of DLS (one kernel iteration) or one pass
      through every direction of OD.
    - *Negative weights* are met by the method of multipliers; the help says only that they are
      treated as Lagrangian multipliers and converge in a few cycles. The tolerance
      (1e-9 × max(1, |T|)) and the penalty schedule are this program's.
    - *Radius variables* are varied as curvatures (the help lists "curvatures" among the
      variables).
    - *The default weight* of a new row in the editor is 1; OpticStudio's was not read.
    Check: a merit function with one row of each operand, saved by OpticStudio with its values,
    evaluated here on the same lens.
32. **Tolerance operand readings** (OP-63). Section 36's rules come from OpticStudio's help;
    these choices fill its gaps and none is compared with OpticStudio's tolerance files:
    - *Radial direction:* TSDR and TEDR take the drawn direction (+y when none is drawn), as the
      help sets +y for TARR and TRLR in the sensitivity analysis; their own pages give none.
    - *TIR sign:* TIRX tilts about +y by +atan(TIR/(2S)) and TIRY about +x by the same, as the
      help's angle formula writes them; with right-handed tilts (§4) a positive TIRX then lowers
      the sag at +x, opposite to the help's ΔZx = (TIRX/2)·ρx. Only the sign of an equal and
      opposite pair is affected.
    - *Rolls:* a roll "along x" turns the element about the axis (sin φ, −cos φ, 0) through the
      roll surface's center of curvature, φ = 0 for TARX and TRLX and 90° for TARY and TRLY, so a
      positive angle moves the vertex of a surface with positive radius toward +x or +y. The
      roll TIR is read at the lateral positions ±h of the rolled read surface, along the
      element's original axis.
    - *Pivots:* a mechanical edge (MPVT 2, 3) is the axial point at the sag of the surface's
      mechanical semi-diameter; the geometric center (MPVT 4) is the midpoint of the front and
      rear vertices. Pivots other than the front vertex need a group without breaks or surface
      tilts.
    - *Element wrap:* the help describes OpticStudio's breaks only in words ("insert a coordinate
      break surface before and after the lens group, and to use a dummy surface at the end of
      the group to return to the front vertex"); this program's surfaces (§36) place every
      surface the same, but a merit row that sums thicknesses across the group (TTHI, MNCT)
      sees the inserted ones.
    - *ISO operands:* Int2 is Units and Int3 Statistics, as the ISOB and ISOC pages order them
      (the ISOD page lists Statistics first).
    - *TIND and TABB on a catalog glass* use this program's glass offset rule (`GlassOffsets`,
      item 7), not OpticStudio's model-glass difference.
    Check: a lens with one row of each built operand, OpticStudio's Monte Carlo file saved for a
    sensitivity case of each (SAVE), compared surface by surface with the perturbed record.
33. **Thermal analysis readings** (OP-91). Section 37 follows OpticStudio's help pages, but
    nothing is compared with OpticStudio's output, and these are this program's readings where
    the help is short:
    - *The mount's edge model.* The help ("Thickness (THIC) Values") describes the edge
      thickness at the mechanical semi-diameters, the mount's radial growth and the two contact
      cases in words and a figure; the formula T' = E(1 + α_mΔT) + Z'_n(h'_n) − Z'_{n+1}(h'_{n+1}),
      with each surface's sag scaled by its own expansion and h' = min(h(1 + α_mΔT), h(1 + α_jΔT)),
      is this program's reading of them. The help's linked Knowledgebase article was not read.
    - *Semi-diameters.* Make Thermal's page says it adds thermal pickups for "clear
      semi-diameter or semi-diameters"; the "Defining Which Parameters" pages do not say how they
      scale. Here SDIA and MCSD scale with the radius rule's expansion; CHZN (chip zone) is a
      straight pickup, as "All Other Values" says.
    - *Make Thermal's choices.* The nominal of "Delete existing configuration data" is the current
      configuration; with existing configurations kept, nominal c's new configurations follow the
      originals c by c, new rows go after the existing ones, and existing non-length rows pick up
      their nominal. Surface 0 and the image get no rows; a value under a Lens Data Editor solve
      or pickup gets none (a row would replace the solve). One new configuration takes the
      minimum temperature. The optional pressure is this program's addition.
    - *Several TEMP rows* ("Defining Multiple Environments within a Single Configuration") are not
      built: a thermal pickup of a length in such a table fails.
    Check: a lens made thermal by OpticStudio, its thermal pickups' values read from its
    Multi-Configuration Editor and compared with this program's for the same lens and catalog.
34. **MTF readings** (OP-39, section 38). Read from the help pages' wording; no OpticStudio
    number was compared:
    - *FFT MTF* is the pupil autocorrelation on OP-38's pupil grid, interpolated linearly between
      whole-node shifts. The help's "faster version" for FFT Through Focus MTF (the MTFA
      operand's Grid = 0 sparse method) is not described and not built; through focus uses the
      same autocorrelation.
    - *Polychromatic weights*: each wavelength's OTF weighted by its weight × (λ_min/λ)² × its
      PSF's energy, so the polychromatic OTF is the transform of OP-38's polychromatic PSF; the
      help does not say how OpticStudio weights them. The geometric MTF weights by the
      wavelength weight.
    - *Geometric MTF* is the exact transform of the ray set (no binning into a line spread
      function); OpticStudio's grid algorithm and its sparse operand method are not described.
      Multiply by diffraction limit uses the same field's unaberrated pupil; the help does not
      say which diffraction limit.
    - *Huygens MTF* transforms the Huygens PSF directly at each frequency; the help says "an FFT
      of the Huygens PSF".
    - *Diffraction limit* curves are the reference field's (help, "Diffraction Limited"), also on
      the Huygens and geometric plots and as an option on the through-focus plots, where the help
      lists no such setting.
    - *Contrast Loss Map*: the help's δ is stated two ways that disagree ("ξ(Δ) = Δ/(D/2)·ξ_c"
      with "Δ = D/2" at the cutoff, and an example where δ = 0.1 puts the pair at 1 ± 0.1);
      here the pair is separated by the autocorrelation shift λν/(n|a|), 2ν/ν_c for a circular
      pupil, as the MTF needs. The default frequency, "5% of the cutoff", uses the cutoff
      2n√|a_x·a_y|/λ. Drawn as maps, not circles.
    - *Defaults* the help does not give (frequencies, steps, pixels, 101 plotted frequencies,
      129 × 129 surface points) are this program's.
    Check: FFT, Huygens and geometric MTF of a singlet at two fields, polychromatic and
    monochromatic, against OpticStudio's text output; the contrast loss map's δ against its
    plot.

35. **Enclosed energy readings** (OP-41, section 39). Read from the help pages' wording; no
    OpticStudio number was compared:
    - *X-only and Y-only.* The diffraction page says the options "compute the fraction of energy
      in the PSF in the [-y,y] or [-x,x] range of the y- or x-axes, respectively", then gives
      the example "the X-only enclosed energy for an x-value of 10 microns represents the
      fraction of energy in the region 20 microns across in the x-direction". The example (and
      the geometric page) is followed: X-only is the slit |x − x_ref| ≤ size.
    - *Diffraction centroid.* The help says "image centroid" without saying whether it is the
      PSF's or the rays'; here it is the rays' light-weighted mean intercept (as OP-38's Huygens
      Use Centroid), projected along the chief ray onto the PSF plane.
    - *Polychromatic weighting.* Each wavelength's fraction is weighted by its weight times the
      share of a clear pupil it passes; the help does not say how wavelengths combine.
    - *Default scaling* (maximum distance 0): the help gives none. Diffraction: the smaller of half
      the PSF's period and the larger of 5·λ·F and 1.5 × the rays' spread; geometric and extended
      source: the distance that holds every ray.
    - *Multiply by diffraction limit.* "Scaling the geometric data by the theoretical diffraction
      limit curve" is read as the product of the geometric fraction and the Airy disk's
      1 − J₀² − J₁², at the real working F/# of the reference field of "Diffraction Limited" and
      the selected (or primary) wavelength; it is refused for types other than encircled.
    - *Geometric line/edge spread.* Both orientations are drawn; the line spread is binned in
      100 bins and scaled to its peak, the edge spread runs 0 to 1. The help gives neither the
      bins nor the normalisation.
    - *Extended source.* Rays are placed by a Sobol sequence instead of at random, each wavelength
      traces its own rays instead of a random wavelength per ray, the binary IMA byte order is
      read as little-endian, and row 1 of an IMA image is its top. The x and y distributions use
      100 strips across ±the maximum distance; the help gives no strip width.
    Check: Diffraction and Geometric Enclosed Energy (all four types, chief ray and centroid) and
    an Extended Source run of a text IMA file for a singlet at two fields, against OpticStudio's
    text output.

36. **Default merit function wizard readings** (OP-56, section 40). Read from the help's
    Optimization Wizard pages and the alphabetical operand list; nothing is compared with
    OpticStudio's output, and these are this program's readings where the help is short:
    - *The group columns.* OpticStudio groups TRAC, TRCX, TRCY, OPDM and OPDX (and, by their
      pages, ANCX and ANCY) "by field position and wavelength" in the editor; here each such row
      carries its pupil rule (Int1 rings or grid, Data5 arms, 0 for the rectangular array) and
      Data6 (one reference per field, or per wavelength). OpticStudio's own rows have no such
      columns; an imported TRAC row fails, asking for the rows to be regenerated.
    - *Which chief ray.* TRAR's page says "See ANAR", whose reference is "the primary wavelength
      chief ray"; TRAX and TRAY, whose pages say only "the chief ray", are read the same way.
      OPDC's reference sphere is on its own wavelength's chief ray.
    - *Polychromatic wavefront.* A group over every weighted wavelength (lateral colour counted)
      is referred to the primary wavelength's reference sphere and its mean or plane is fitted
      over all of them, as in OP-40's polychromatic RMS; each row's value is then in waves of its
      own wavelength, as OPDC's page says ("in waves at the wavelength defined by Wave").
    - *Arms and symmetry.* The arms start on +x, as in OP-40's rule; OpticStudio's arm angles are
      not documented. With symmetry, arms on the y axis keep their own weight, so 8 or 12 arms
      keep more than half; the help says "half the number of arms".
    - *Weights.* Field weight × wavelength weight × pupil weight × overall weight; how OpticStudio
      normalises its weights is not documented (the merit value divides by Σ W either way).
    - *Boundary columns.* MNEG's and MNEA's Zone and Mode in Data1 and Data2; the edge thickness
      at this surface's semi-diameter along +y; the boundary rows cover surfaces 1 to the one
      before the image and are written once, not per configuration. The thickness defaults are
      this program's.
    - *Start At and DMFS.* The generated rows are inserted, not written over earlier ones (help,
      "Button Functions": "adds the generated merit function to the existing merit function");
      the wizard writes no DMFS row.
    - *Contrast (Moore–Elliott).* The help ("Optimizing for MTF") gives the method, the shift
      equation and φ(x_j, y_k) − φ(x_j − Δ, y_k) = 0, but not MECA/MECS/MECT's values or columns
      nor the rows the wizard writes. Here: a MEC row is OPD(P) − OPD(P − Δ) in waves of its
      wavelength, OPDC's OPD (chief-ray sphere); the shift is 2ξ·λ·F/# pupil radii (the cutoff
      where the pupils just touch, the reading of item 34's Contrast Loss Map, since the help's
      text puts the cutoff at Δ = D/2), along y for MECT and x for MECS, F/# the paraxial working
      F/# at the row's wavelength; MECA = (MECT + MECS)/2. Columns: Int2 Wave, Data1–4 Hx, Hy, Px,
      Py, Data5 Freq (the help's Field is taken from Hx and Hy). The wizard writes, per field,
      wavelength and pupil point of the rule, a MECT row (weight × relative T weight) and a MECS
      row (× relative S weight) whose partner point is inside the pupil and traces, without
      symmetry halving; pairs left out are counted in the notes, and the kept rows keep their
      quadrature weights. The relative weights default to 1 and the frequency has no default
      (the help gives none).
    - *MTF operands.* MTFT/MTFS/MTFA and GMTT/GMTS/GMTA take their columns in the order the help
      lists the parameters (Int1 Samp, Int2 Wave, Data1 Field, Data2 Freq, then Grid and Data
      Type, or !Scl and Grid); Samp n is a 32·2ⁿ⁻¹ grid; the sparse method (Grid = 0) is not
      described, so both Grid values use the grid method; Data Type 1–3 and Field 0 are refused.
    Check: OpticStudio's default merit function for a test lens, its rows and merit value
    compared with this program's.

37. **Glass substitution readings** (OP-59). Section 41 follows OpticStudio's help pages, but
    nothing is compared with OpticStudio's output, and these are this program's readings:
    - *RGLA's columns and range.* Surf1 and Surf2 in Int1 and Int2, Wn, Wa and Wp in Data1 to
      Data3; over a range of surfaces the value is the target plus the summed excesses (the help
      defines one glass only).
    - *Where a glass sits.* The printed nd, Vd and ΔPgF of the catalog first, computed values
      where it prints none.
    - *Conversion weights.* The model-to-real conversion uses RGLA's default Wn and Wa (the help
      says only "similar to the RGLA definition", partial dispersion omitted).
    - *The template.* A glass without a status, and a Melt glass, pass the status test; a missing
      status counts as incomplete data only when a status box is clear.
    - *The search order.* OpticStudio substitutes glasses inside its Hammer and Global
      optimizers, whose order is not published; this program's coordinate search is its own.
    Check: a lens with model glasses converted by OpticStudio and by this program against the
    same catalog and template; RGLA and GCOS values read from OpticStudio's Merit Function
    Editor for the same lens.

38. **Sensitivity and inverse sensitivity readings** (OP-64). Section 42 follows OpticStudio's
    help pages; nothing is compared with OpticStudio's tolerance report, and these are this
    program's readings where the help is short:
    - *Combining fields.* The help says the criterion is "computed as an average over all field
      positions" through a merit function OpticStudio builds; here the field values are combined
      as √(Σ w v² / Σ w), the merit function's form (the Strehl ratio, the MTF and the encircled
      energy, where larger is better, as the weighted mean).
    - *The largest field coordinate* of the Y- and XY-Symmetric sets is the largest radial distance
      of the system's field points; with every field on the axis the set is the one on-axis point.
    - *Paraxial focus.* "How OpticStudio Computes the Tolerance Analysis" says that without
      Optimize "the back focal position is set to paraxial focus"; "Evaluating Compensators" says
      the focus is adjusted "to maintain the amount of defocus in the nominal lens design". This
      program keeps the nominal lens as entered and holds its defocus (thickness before the image
      minus the paraxial image distance), so a nominal lens not at its paraxial focus keeps its
      own defocus.
    - *Larger-is-better criteria* (the Strehl ratio, the MTF, the encircled energy): the degradation is −Δ, the
      inverse Increment a decrease (as the help says for MTF), and the estimated performance
      N − C with the linear difference; with the RSS difference it is not given, since the help's
      √(N² + C²) is written for a criterion that grows worse upward.
    - *Compensator bounds* are soft rows of weight 1 next to the criterion's rows (the help says
      they "may be violated if doing so would provide better performance"; their weight is not
      published); a COMP with Min = Max = 0 is held at its nominal by that row. CPAR and CMCO
      bounds are not built.
    - *Worst offenders* are the individual min and max trials sorted by degradation (the help:
      "sorts the tolerances by contribution to the criterion"); Show Worst's default 10 is this
      program's.
    - *Inverse search.* Brent's method on t in (0, 1) with the limit t·L; an operand whose Min must
      be −Max keeps the tighter of the two adjusted limits.
    - *MTF criteria.* The help's Sampling for MTF is a grid ("a sampling of 1 yielding a 32 x 32
      grid, sampling of 2 yielding a 64 x 64 grid, etc."); here the grid is given directly (the
      pupil grid, 16 to 1024, shared with the Strehl ratio). The diffraction MTF is OP-39's FFT
      engine and the geometric MTF its geometric engine times the diffraction-limited MTF of the
      same pupil (the help's GMTA recommends that scaling; the tolerancing page does not say).
      The MTF Frequency has no default in the help; the run refuses 0.
    - *Encircled energy* is not one of OpticStudio's tolerancing criteria (the plan adds it, as
      the Strehl ratio): OP-41's diffraction or geometric fraction within a radius (µm) of the
      centroid (the reference OpticStudio "always uses ... for tolerancing" for spot and
      wavefront), encircled only, on the pupil grid.
    Check: a lens toleranced by OpticStudio in each mode with each compensator choice, its report's
    nominal, per-tolerance changes, RSS estimate and inverse limits compared with this program's.

39. **Global optimization readings** (OP-58, section 43). Section 43 reads OpticStudio's help
    pages for the two dialogs' settings and behaviour; the algorithms are this program's own and
    nothing is compared with OpticStudio's output. Readings:
    - *What is kept.* "# To Save" and the ten merit values shown are read as one best list of
      `SaveCount` designs (default 10), kept in memory; no GLOPT_*.ZMX or *_HAMMER.ZMX file is
      written and Resume is not built.
    - *Same basic form.* The help rejects "the older design being replaced ... if it has the same
      basic form as the new lens" without defining form; here two designs have the same form
      when their glasses are equal and every variable differs by at most 1 % of its range.
    - *Ranges.* The help says OpticStudio takes variable ranges from MNCT/MNET boundaries and uses
      the starting focal length "as a scaling parameter"; this program draws starts from
      explicit ranges (default a curvature of ±3/|EFL| and the other rules of section 43) and
      leaves MNCT/MNET to the merit function.
    - *Stopping.* OpticStudio runs both until Stop; here both also stop after a set number of
      generations or rounds, or a time limit checked between them.
    - *Applying.* OpticStudio updates the lens with the best design on Exit; here the lens changes
      only when a listed design is applied.
    Check: none possible beyond the settings, since the algorithms differ; a common benchmark
    (the same triplet or double-Gauss start and merit function) run in both, comparing the best
    merit reached and the forms found.

40. **Interactive adjustment readings** (OP-60, section 44). Read from the help pages' wording
    ("Quick Focus", "Quick Adjust", "Slider", "Visual Optimizer"); nothing is compared with
    OpticStudio's output, and these are this program's readings where the help is short:
    - *Combining fields.* Both Quick tools compute "a polychromatic average over the field, using
      the defined fields, wavelengths, and weights"; here the fields' RMS values are combined as
      √(Σ w v² / Σ w), the merit function's form (as §42 does for tolerancing).
    - *Sampling.* The help gives none; here Gaussian quadrature, 6 rings by default (the RMS
      analyses' default), 1 to 20.
    - *Quick Adjust's criteria.* The help names "best transverse or angular ray focus" and says
      every criterion uses the centroid, but lists no choices; radial, x and y of each are this
      program's, by analogy with Quick Focus. "Angular" is read as the spread of the direction
      cosines after refraction into the evaluation surface.
    - *Quick Focus in afocal image space.* The help lists four criteria at the image surface and
      no angular one; the thickness before the image does not change ray angles, so afocal
      systems are refused and pointed to Quick Adjust's angular criteria.
    - *Slider and Visual Optimizer ranges.* The help gives no default range; here ±10 % of the
      value (±1, or ±0.01 per lens unit for a curvature, when it is zero).
    Check: Quick Focus and Quick Adjust on a lens in OpticStudio with each criterion, the
    adjusted thickness or radius compared with this program's.

41. **Monte Carlo readings** (OP-65). Section 45 follows OpticStudio's help pages; nothing is
    compared with OpticStudio's Monte Carlo output, and these are this program's readings where
    the help is short:
    - *Truncated normal.* The help restricts the normal draw "to lie within n standard deviations
      of zero"; here a draw outside ±Δ is drawn again (truncation, not clamping to the extremes).
    - *Radial operands.* For TEDR, TSDR, TARR and TRLR with a zero limit the help says the
      normal and parabolic distributions are "centered at 0"; the half-width is taken as
      max(|Min|, |Max|), and the direction is drawn uniform over 0–360°. The uniform and user
      defined pages do not say this, so those keep [Min, Max].
    - *User defined tables.* T is read as linear between the tabulated points and its integral
      inverted exactly; X must increase strictly; more than 200 points is accepted with a note
      (the help "warns").
    - *Seeds.* OpticStudio increments a SEED value "for each generated system"; here each SEED
      value and trial number select one SplitMix64 stream, so the numbers differ from
      OpticStudio's while keeping its property (reproducible, each trial unique). SEED 0 and rows
      under no SEED use the run's seed. The draw order within a trial is the row order: value,
      then a radial direction, then TIRR's angle.
    - *Statistics.* The help does not list the report's statistics; reported are the mean, the
      sample standard deviation (n − 1), min, max, chosen percentiles (linear interpolation
      between order statistics) and a yield against a limit, over the evaluated trials only.
    - *Saved trial lens.* The paraxial focus shift is written into the thickness before the image.
    Check: a lens with a seeded Monte Carlo run in OpticStudio, the spread of its criterion (mean,
    standard deviation) compared with this program's over the same tolerances and statistics.

43. **Scripting ray error codes** (OP-98, section 47). The ZPL help page "RAYTRACE" (2025 R2,
    saved under `plans/sources/optics/op-98-help/`) gives RAYE: zero, the surface missed
    (positive), the surface of total internal reflection (negative), −9999 when the ray cannot be
    launched; and RAYV: the surface of vignetting, or zero. These are this program's readings
    where the page is silent:
    - *No convergence and evanescent orders.* The page names no code for an iterative
      intersection that does not converge or a diffraction order that does not propagate; both
      are reported as RAYE = +n (the ray found no intercept at surface n).
    - *Cannot be launched* is read as the ray aimer failing to converge (`RayLauncher.LaunchAimed`
      reporting a failure); an object in the entrance-pupil plane throws instead, as the Single
      Ray Trace does.
    - *Vignetting with RAYE.* A vignetted ray has RAYE = 0 and RAYV = n; the page keeps the two
      apart ("Values returned for surfaces past the surface of vignetting may not be accurate").
    Check: OpticStudio's RAYTRACE on a lens built to miss, totally reflect and vignette one ray
    each, and one whose aim fails, RAYE and RAYV compared.

44. **Test plate readings** (OP-17, section 48). The help page "Test Plate Fitting" (2025 R2,
    saved under `plans/sources/optics/op-17-help/`) gives the file layout, the four orders, the
    diameter (3/4) and edge-sag (0.2 mm) tests and "NO MATCH FOUND!". These are this program's
    readings where the page is silent:
    - *The code names the plate.* Code −1 "concave only" and 1 "convex only" are read as the
      plate's own shape (the Test Plate Lists page: "availability of concave and convex test
      plates"), so a convex lens surface needs a concave plate (code −1 or 0) and a concave one a
      convex plate (1 or 0). A cemented surface needs both (code 0).
    - *Convex or concave* is decided from the materials: the surface is convex when its centre of
      curvature lies in its element's material (§48 gives the side rule for mirrors and reversed
      light). A radius flagged Variable on a surface with air on both sides is refused.
    - *Fringes* are N = r²·|c_plate − c|/λ over the clear semi-diameter r, from the TFRN page's
      ΔZ = (λ/2)·N and ΔC = 2ΔZ/r²; the page does not name a wavelength for fitting, so the
      report uses 0.6328 µm (the TWAV default) unless set. The fitting order does not depend on it.
    - *Edge sag* compares the surface's conic sag (its conic constant included, higher-order
      aspheric terms not) with the plate's spherical sag at the clear semi-diameter; a plate whose
      radius is shorter than the clear semi-diameter fails the test.
    - *No match.* A radius with no admissible plate is reported when it is found (before the
      remaining radii are fitted) and stays variable, re-optimized as a compensator ("that radii
      will subsequently be ignored").
    - *Ties.* The lower surface is fitted first; the earlier line of the list wins between plates;
      Try All keeps the first order (help order) on equal merit.
    Check: Test Plate Fitting in OpticStudio on a lens and a vendor list, the chosen plates, their
    order and the merit after each step compared with this program's report.

46. **High-Yield readings** (OP-62, section 50). The penalty τ = |n − n′|(1 − R·N) and which
    ray R is are copied from Zemax Knowledgebase KA-01837; nothing is compared with OpticStudio's
    HYLD values or its "Improve Manufacturing Yield" rows. These are this program's readings
    where the sources are short:
    - *Row weights.* Each HYLD row weighs W × overall × field × wavelength × pupil weight, the
      default merit function's ray weight times the High-Yield Weight. The help says only that
      the Weight "sets the weight of the overall contribution from the HYLD operands"; the
      article's editor screenshot shows HYLD weights equal to the criterion rows' ring weights.
    - *Which rays and surfaces.* The default merit function's rays (same rule, symmetry
      reduction and switch to the rectangular array), one row per surface with a different index
      on either side; rays that fail are left out. The screenshot shows rows on surfaces 2 and 3
      only, its HYLD rows at pupil points that are not the criterion rows' printed above them, so
      OpticStudio's own sampling of the HYLD rows is not reproduced exactly.
    - *Mirrors.* The sources do not say what n′ is at a mirror; HYLD there fails by name and no
      rows are written for mirrors.
    - *Column order.* Surf in Int1, Wave in Int2, Hx Hy Px Py in Data1–4, from the screenshot.
    Check: OpticStudio's HYLD values on a lens, ray by ray, and the rows its wizard writes.

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
  are left out. The fast even-asphere trace (Newton is used for every form). Saving and editing
  Odd Cosine (its term order is not documented; §20 lists the saved types). Section 10, item 13, lists the readings not yet compared with
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
  The curvature forms give the curvature the paraxial engine must see. On a surface with a
  shape (an asphere's 2α₁ term, a thin-lens power; section 17, section 18) the engine sees
  `ParaxialEquivalent`, so the vertex curvature entered is found by inverting that map, which
  is linear in the curvature (TC-6). A shape whose paraxial action does not change with its
  curvature (an ideal Paraxial lens, an ABCD block) fails the solve by name.
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
  The substitute solve leaves the glass unchanged; it is a flag for glass substitution (§41)
  and the Hammer search (§43).
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
  - Kept, not evaluated: variables (the optimizer, §35, reads them); the semi-diameters'
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
  it yet); pupil apodization and vignetting factors (OP-22); retardance of the system (the Jones
  and Mueller matrix surfaces of OP-52 are section 30); birefringent media (OP-53); gradient-index polarization;
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

## 23. Toroidal, biconic and irregular surfaces

OP-24 adds four `SurfaceShape` subclasses in `OpenSim.Optics/Sequential/Anamorphic/`. Each
definition is quoted from its OpticStudio 2025 R2 help page (ansyshelp.ansys.com, OpticStudio
User Guide, "Sequential Surfaces (lens data editor)": "Toroidal", "Biconic", "Biconic Zernike",
"Irregular", and "Zernike Standard Sag" for the Zernike terms) on the class; the pages are saved
under `plans/sources/optics/op-24-help/`. Formulas not on a page are derived in the class
comments and in `AnamorphicSurfaceTests`. All four use the asphere intersection of §17
(damped Newton from the base conic's root, bisection fallback). Coefficients are SI.

- **Toroidal** (`ToroidalSurface`): the y–z curve z_y = c·y²/(1 + √(1 − (1 + k)c²y²)) + α1·y² +
  … + α7·y¹⁴ (c, k the surface's own) turned about the line z = R parallel to y, R the radius
  of rotation (parameter 1, 0 = infinite). With ρ = R − z_y,
  z = z_y + x²/(ρ + sign(ρ)·√(ρ² − x²)); ∂z/∂x = sign(ρ)·x/√(ρ² − x²), ∂z/∂y = |ρ|/√(ρ² − x²)·z_y′.
  Not defined where x² ≥ ρ² or ρ has the other sign from R. Optional Zernike Standard terms
  (parameters 0, 13, 14, 15–245). **Cylinder:** R = 0 gives z = z_y(y), power in y only; c = 0
  with R ≠ 0 gives power in x only.
- **Biconic** (`BiconicSurface`): z = (cx·x² + cy·y²)/(1 + √(1 − (1 + kx)cx²x² − (1 + ky)cy²y²)),
  cy and ky the surface's, Rx and kx parameters 1 and 2 (Rx = 0 is infinite). With cx = cy and
  kx = ky it is the Standard conic.
- **Biconic Zernike** (`BiconicZernikeSurface`): the biconic + Σ αᵢ·xⁱ + Σ βᵢ·yⁱ (i = 1…16,
  parameters 13–28 and 29–44) + Σ Aᵢ·Zᵢ (parameters 3 the term count up to 210, 4 the
  normalization radius, 45–254 the coefficients, 0 the extrapolate flag).
- **Zernike terms** (`ZernikeSagTerms`): Noll-ordered Standard polynomials of OP-33
  (`ZernikeBasis`, φ counter-clockwise from +x) at (x/R_n, y/R_n), coefficients in lens units;
  extrapolate 0 drops them for ρ > 1. A term count above zero needs R_n > 0.
- **Irregular** (`IrregularSurface`): z = conic + Zs·ρ⁴ + Za·ρ′y² + Zc·ρ²·ρ′y, ρ = r/r_max,
  ρ′y = ρy·cos θ − ρx·sin θ, r_max the surface's fixed clear semi-diameter (an automatic one
  is refused when a Z term is non-zero). The whole sag lives in the frame of the coordinate
  break (dx, dy, tilt x, tilt y, 0, order 0) of §4: the ray is carried into it, met there and
  carried back with its normal; the distance along the ray is the same in both frames. The
  shape's `Sag` is the local z line's intersection with the placed surface.
- **Paraxial model.** The paraxial engine is rotationally symmetric and traces the y–z meridian
  (§18), so each surface gives its y–z vertex curvature ∂²z/∂y²(0, 0): Toroidal c + 2α1,
  Biconic cy, Biconic Zernike cy + 2β2, Irregular c + 2Za·cos²θ/r_max², plus the Zernike terms'
  share (−2N·A·p_k(0)/R_n² from each cos 2θ term, N its normalisation and p_k(0) its radial
  polynomial's lowest coefficient from the recurrence at the origin; −2N·A/R_n² for n = 2). The x–z curvature is not passed on, as for Paraxial XY. A surface
  whose vertex is off z = 0 (Zernike piston and every m = 0 term), has a slope there (Zernike
  tilt and coma, α1, β1, a tilted or decentered Irregular surface other than a decentered
  plane) or has ∂²z/∂x∂y ≠ 0 (Zernike sin 2θ, an Irregular Za at an angle not a multiple of
  90°) has no such model and is refused with an `ArgumentException` naming the cause and the
  surface, as the engine refuses a tilted or decentered lens (§15). Such a lens still traces
  real rays from given starting points; the launcher, ray aiming, solves and every analysis
  that needs paraxial data refuse it. `OpdLimitation` is null: the path through these surfaces
  is the true one.
- **Saved layouts** (`SurfaceTypes`, §20): parameter numbers, names and units from each page's
  parameter table; parameter 0 (extrapolate) is a whole number 0 or 1; numbers 13 and above
  are extra data.
- **Gates** (`AnamorphicSurfaceTests`): a Biconic and a Biconic Zernike with equal x and y terms,
  an Irregular surface with zero terms and a toroid with R = Ry, k = 0 trace as the Standard
  surface to 1e-12 m; a cylinder lens focuses collimated y rays, Richardson-extrapolated to
  zero height, at the derived back focal distance Rc/(n − 1) − t/n to 1e-11 m, with x rays
  undeviated (a line focus), and the paraxial engine puts its focus there too; normals match
  central differences of the sag to 1e-8; intersections lie on the surface to 1e-14 m.
- **Not built:** OpticStudio's import of Zernike coefficients into the Toroidal surface (they
  are typed in the editor); the Cylinder as a separate type name (it is the Toroidal surface);
  any comparison with OpticStudio's output.

## 24. Multi-configuration

A multi-configuration system (OP-30) is the optics record plus a table,
`OpticsSettings.MultiConfiguration` (`OpenSim.Core/Model/OpticsMultiConfigSettings.cs`): operand
rows, each with an operand code, up to three integer arguments and one value per
configuration, the number of configurations and the current one. Null is one configuration, the
system as entered; the project format version is unchanged. The operand list, the arguments and
the integer codes are OpticStudio's (OpticStudio 2025 R2 help, "Multi-Configuration Operands" and
the pages under "Multiple Configuration Editor", saved in `plans/sources/optics/op-30-help/`).

- **Substitution.** Configuration k is the entered record with every row's value in column k
  written into the data the operand controls (`MultiConfiguration.SettingsFor`,
  `OpenSim.Optics/MultiConfig/MultiConfiguration.cs`); the result has no table, and
  `ProjectLens` gives its solved lens (`SolveFor`, `LensFor`). Rows are applied in table order,
  so a later row wins where two control the same value (the help says so for IGNR and IGNM).
  Every problem (an unknown or unsupported code, a missing surface, field or wavelength, a
  value the target refuses) is listed in one `ArgumentException`, a line each naming the row,
  its code and arguments, and the configuration.
- **Units and numbering.** Values are in the saved record's units: lens units, 1/lens unit for
  CRVT, degrees, micrometers, °C, atmospheres. Surfaces count from the object (0), fields and
  wavelengths from 1. THIC takes +infinity (saved as `IsInfinite`, since JSON has no infinity).
  Operand strings are limited to 32 characters (help: "Comment About Operands that Define
  Character Strings"); the table to 160 configurations (help: "Defining the Number of
  Configurations").
- **Solves under an operand.** Writing a surface cell (CRVT, THIC, GLSS, CONN, SDIA, MCSD, CHZN,
  TCEX, PRAM, the model glass, APER under float by stop size) removes a solve or pickup the Lens
  Data Editor has on that cell, keeping a variable flag, so the operand's value is the one
  traced in that configuration. This is this program's rule: the help does not say which wins.
  The solve-parameter operands (CSP, TSP, PSP) instead change the cell's solve: for a pickup the
  parameters are surface, scale factor, offset (help: "Summary of Solves"; a curvature pickup
  has no offset), for a solve its own parameters in that table's order.
- **Cells.** Fixed, `Variable` (listed by `MultiConfiguration.Variables` for the optimizer,
  written back by `WithValue`), `Pickup` (target × scale + offset from a cell whose
  configuration and operand numbers are each no greater than its own and which is not itself,
  help: "Solve Types"; a text cell copies the text) or `ThermalPickup` (the value of an earlier
  reference configuration expanded to this configuration's temperature, evaluated after the
  configuration's other cells; section 37). PUCN with value c in configuration k makes the fixed and variable
  cells of the rows below it, up to the next PUCN row, take configuration c's value; c must be
  less than k (help); 0 ends the range; a negative PUCN ("a negative pickup") is not defined
  further by the help and is refused.
- **Weights.** The weight of configuration k is the value of the last CWGT row there, 1 without
  one (`MultiConfiguration.WeightOf`); CWGT, PUCN, HOLD and MOFF change nothing in the system.
- **Supported operands.** 92 of the help's 116 codes: surface data (CRVT, THIC, GLSS, CONN,
  SDIA, MCSD, CHZN, TCEX, COTN, MCOM, PRAM and the obsolete PAR1–PAR8 as PRAM 1–8, MIND, MABB,
  MDPG), flags and stop (IGNR, IGNM, SDRW, SRTS, STPS), apertures (APTP 0–10, APMN, APMX, APDX,
  APDY), tilt and decenter before and after (CBDX … CBOR, CADX … CAOR), coordinate returns
  (CROR, CRSR), solve parameters (CSP1–2, TSP1–3, PSP1–3), system data (AFOC, TELE, APDF, APDT,
  SATP, APER, FLTP, RAAM, MTFU, TEMP, PRES, PSHX/Y/Z, PSCX/Y, LTTL, PRWV), polarization (GPJX,
  GPJY, GPPX, GPPY, GPIU), fields (XFIE, YFIE, FLWT, FVDX, FVDY, FVCX, FVCY, FTAN, and the
  obsolete FVAN as FTAN), wavelengths (WAVE, WLWT), and CWGT, PUCN, HOLD, MOFF. The type
  operands' integers are the help's and equal the enumerations' values (`ApertureType`,
  `FieldType`, `ApodizationType`, `RayAimingMode`, `MtfUnit`, `TiltDecenterOrder`,
  `CoordinateReturnMode`). APMN and APMX are the first and second parameter of the aperture
  (circular: minimum and maximum radius; rectangular and elliptical: x and y half-widths;
  spider: arm width and number of arms, the order of the help's "Aperture" page); they refuse a
  user-defined or floating aperture. APER under float by stop size is the stop's semi-diameter
  (help). SDIA and MCSD read no value from an automatic semi-diameter.
- **Not supported** (registered, so a table naming them fails with the code and the reason):
  AICN and CPCN (Inventor and Creo parts), CSDX–CSTZ (composite surfaces, OP-32), FCMM (fields
  have no comment), GCRS, MCHI and OPDR (not in the saved system data), GQPO (the default merit
  function, OP-54), HLGV (no optically fabricated hologram), IGTO (tolerancing, OP-63), NCOM,
  NCOT, NGLS, NPAR, NPOS, NPRO (non-sequential, OP-70 on), PXAR (no physical optics), UDAF
  (apertures are vertex lists, not files), GPEX and GPEY (obsolete, no meaning given).
- **Tools** (`OpenSim.Optics/MultiConfig/MultiConfigTools.cs`). Insert Configuration at a
  position copies the stored values of the configuration that was there (the last when
  appending) as fixed values: the help does not say what a new column holds. Insert with
  Pickups makes every new cell pick up the same row of configuration 1. Insert and Delete move
  pickup references, thermal references, PUCN values and the current configuration with their
  configuration; Delete turns a pickup from the deleted configuration into a fixed cell and a
  PUCN naming it into 0, and says so. Make Single Configuration is `SettingsFor` the current
  configuration. Add All Data adds, for one chosen operand, a row for each target the system
  holds such data for (every thickness, every field's y value, each surface with a tilt …) that
  the table does not already control, each cell fixed at the value the target has in that
  configuration, so no configuration changes. Next and Previous stop at the ends. Make Thermal
  is section 37's.
- **Current configuration.** Every analysis computes on the current configuration:
  `OpticsAnalysis<T>.Compute` passes its context through
  `MultiConfiguration.ForCurrentConfiguration` (help: "The program will use the current
  configuration for all calculations and graphics", "Changing Configurations"). The Lens Data
  Editor keeps the entered lens; it does not show the current configuration's values.
- **Not built.** Make Conjugate
  (the help names its assumptions but not how the subset's object thickness and "other data as
  appropriate" are formed); ZPL-macro and substitute solves; Add Hologram Variables; showing
  the current configuration's values in the Lens Data Editor and writing Lens Data Editor
  edits back into the table, as OpticStudio does.

## 25. Ray fans, spot diagrams and footprints

The ray and spot analyses of OP-34 are in `OpenSim.Optics/Analysis/Rays/` and the pupil
patterns in `OpenSim.Optics/Analysis/Sampling/PupilSampling.cs`. Sources: Ansys OpticStudio
2025 R2 help on ansyshelp.ansys.com, the topics "Hexapolar Rings", "Working F/#", "Ray
Aberration (rays and spots)", "Standard Spot Diagram", "Through Focus Spot Diagram", "Full
Field Spot Diagram", "Matrix Spot Diagram", "Footprint Diagram", "Y-Ybar Drawing" and
"Incident Angle vs. Image Height" (copies in `plans/sources/optics/op-34-help/`, not in the
repository).

- **Pupil patterns** (`PupilSampling`). Hexapolar of N rings: the central ray, then ring k
  (k = 1 … N) of 6k rays at radius k/N, at angles 2πj/(6k) starting on +x, 1 + 3N(N + 1) rays
  (help: 6 rays in the first ring "with the first ray starting at 0 degrees", 6 more in each
  ring, 91 rays for 5 rings). Square of n: the cell-centred n × n grid (points 2/n apart,
  `BatchTracer.SquareGrid`) inside the unit circle. Dithered of N: 1 + 3N(N + 1) points
  (√u cos 2πv, √u sin 2πv), u and v from SplitMix64 with the fixed seed
  `PupilSampling.DitherSeed`, so it is the same on every run. The ray density's minimum for
  the spot diagrams is 3 (help). The ring radii, the square grid's placement and the dithered
  count are this program's choices; the help does not state them.
- **Tracing** (`RaySampler`). Rays are launched by `RayLauncher` (ray aiming and vignetting
  factors as section 16 says) and traced with the surface apertures. At the image surface the
  rays come from `BatchTracer`; at another surface each ray is traced surface by surface and
  its hit there is read, in that surface's local frame. A ray that fails is kept with its
  status and surface; the analyses count and list the failures ("24 stopped by an aperture at
  surface 1") and never draw or use them. The chief rays used as references are traced with
  apertures ignored. Both paths run one work item per field and wavelength and give the same
  bits at any degree of parallelism.
- **Spot radii** (`SpotStatistics`). About a reference point (x_r, y_r):
  RMS = √(Σ wᵢ rᵢ² / Σ wᵢ), RMS x and y the same with the x and y parts, GEO = max rᵢ (help:
  GEO is the radius of the circle about the reference that holds every ray; RMS is the root of
  the mean squared distance and considers the wavelength weighting). wᵢ is the wavelength's
  weight in a polychromatic spot (a wavelength of weight zero is left out) and 1 in a
  monochromatic one. References: the real chief ray at the primary wavelength (default), the
  weighted centroid, the middle ((min + max)/2 in x and in y), or the vertex (0, 0).
- **Working F/# and Airy disk** (`RealWorkingFNumber`). W = 1/(2 n sin θ) (help), θ between the
  field's chief ray and each marginal ray (0, ±1), (±1, 0) of the vignetted pupil at the image
  surface, n the image space's index; off axis, W = 1/(2√(mean of (n sin θ)²)) over the four
  (help: "the average of the square of the numerical aperture"). Apertures are ignored and
  vignetting factors kept, as the help says; W is capped at 10 000. The Airy radius is
  1.22 λ W (help), λ the primary wavelength when polychromatic or the one shown, in the
  system's air. It is drawn as a circle about the reference. When a marginal ray fails the
  result says which; the help's smaller-pupil estimate is not built.
- **Ray aberration (ray fan).** Tangential fan (0, Py), sagittal (Px, 0), Py and Px = k/n for
  k = −n … n; the plotted value is the ray's image-surface coordinate minus the reference chief
  ray's, in µm: the primary wavelength's chief ray when all wavelengths are shown, the shown
  wavelength's when one is (help). Default components EY (tangential) and EX (sagittal).
- **Through focus.** Five spots at −2, −1, 0, 1, 2 times the delta focus (µm): every ray and the
  chief ray are carried in a straight line from the image surface to the plane z = dz of its
  local frame, (x + dz·L/N, y + dz·M/N).
- **Full field.** Every field on one plot in lens units, a ray at R_f + E·(p − R_f) − R_1, with
  R_f its field's reference and R_1 the first field's (help: the chief ray of field 1 when the
  chief ray is the reference), E the exaggeration.
- **Matrix spot.** One spot per field and wavelength; referred to the field's point (primary
  chief ray, or centroid or middle over every wavelength) or, with "ignore lateral color", to
  each cell's own point.
- **Footprint.** A (2n + 1) × (2n + 1) grid (i/n, k/n) inside the pupil (help: "a setting of 10
  will trace a grid of 21 x 21 rays") on any surface, lens units, local frame, with the circle
  of the surface's semi-diameter (or its automatic clear semi-diameter). Rays stopped before
  the surface are never drawn; rays stopped at it (where they meet it) or later are drawn unless
  "delete vignetted". Rays through = drawn / launched (help).
- **Y-Ybar.** The paraxial marginal ray height y against the paraxial full-field chief ray
  height ȳ at each surface (section 11), at one wavelength.
- **Incident angle vs. image height.** Fields Hy = 0 … 1, Hx = 0; the rays (0, −1), (0, 0),
  (0, 1); angle atan2(M, |N|) in degrees in the image surface's local frame (the help assumes
  the image surface flat); image height the chief ray's local y.
- **Gate** (`OpenSim.Tests/Optics/RayFanSpotTests`): a paraboloid on axis and an ideal Paraxial
  surface at three fields have RMS and GEO radii of a few ulps; a concave spherical mirror's
  ray fan equals its exact transverse aberration (closed form) to rounding, and an odd fit
  a₁P + a₃P³ + a₅P⁵ gives a₃ = −(D/2)³/(2ρ²) (derived in the test by expanding the exact form;
  OP-36's Seidel sums are not built) and a₁ = 0, both to 10⁻⁶ of a₃; the on-axis footprint of a
  singlet at D = 20 µm equals the paraxial marginal beam diameter to 10⁻⁶ (and differs at
  D = 20 mm); RMS, GEO and the four references on hand-built ray sets; the paraboloid's working
  F/# 1/(2 sin θ) and its through-focus GEO |dz|·tan θ; the ideal lens's full-field offsets,
  incident angles and Y-Ybar heights in closed form.
- **Not built.** Configuration Matrix Spot (needs OP-30's configurations); apodized sampling (a
  non-uniform apodization is named in the assumptions and not applied); OpticStudio's thinning
  of lower-weight wavelengths; the Airy ellipse (a circle is drawn) and the Airy disk on the
  matrix spot; direction-cosine spots; polarization and scattering in the spots; the ray fan at
  intermediate surfaces and its vignetted-pupil scaling; the footprint's "Ring" density and
  surface aperture shapes other than the semi-diameter circle; afocal-mode spots (positions
  are on the image surface regardless). Section 10, item 21 lists the checks against
  OpticStudio.

## 26. Optical path difference and wavefront

OP-35. Code: `OpenSim.Optics/Wavefront/OpdEngine.cs`, `WavefrontTools.cs` and
`FoucaultShadowgram.cs`; the analyses in `OpenSim.Optics/Analysis/WavefrontAnalyses.cs`. Tests:
`OpenSim.Tests/Optics/WavefrontAnalysisTests.cs` and the gate
`OpenSim.Tests/Optics/Gates/OpdGateTests.cs`. OpticStudio's definitions are read from its 2025 R2
help ("Reference OPD", "Exit Pupil", "Infinity", "Absolute/Absolute 2", "Afocal Image Space",
"Optical Path Difference", "Ray Aberration (Aberrations)", "Wavefront Map", "Interferogram",
"Foucault Analysis (Wavefront)", "Pupil Aberration", "RMS vs. Field"), saved under
`plans/sources/optics/op-35-help/`. Welford's "Aberrations of Optical Systems" and Hopkins were
not on hand; the formulas below are derived from the definitions, not copied.

- **What is compared.** A ray and the chief ray of the same field and wavelength are traced to
  the image surface (`BatchTracer`; ΔP, the ray's path minus the chief ray's, summed segment by
  segment) and carried along their image-space lines to the reference surface.
  **OPD = chief path − ray path** (section 6), in waves of the traced wavelength in the system's
  air (paths are relative to the air index, section 13). The chief ray's OPD is zero by
  construction. This is the help's "OPD = OPD Absolute + OPD Correction".
- **Reference point** (`OpdReferencePoint`): the chief ray's image-surface intercept, or the
  centroid, the mean intercept of the rays of an n × n grid of pupil nodes (n = 33 by default).
  Moving the centre adds, to first order, a tilt.
- **Reference sphere** (Reference OPD = Exit Pupil, the default). Centre C, radius R = |E − C|,
  E the centre of the primary wavelength's paraxial exit pupil (`ParaxialSystem.ExitPupilPosition`,
  placed from the last surface's vertex as the OPD solve does), on E's side of C (σ = −1 when E
  lies behind C along the reference ray, +1 when ahead). With q a ray's image intercept, d its
  unit direction, w = q − C, b = w·d and p = −|w − b·d|²:
  `OPD = −ΔP + n·[(b_r − b_c) + σ·(p_c − p_r)/(√(R² + p_c) + √(R² + p_r))]`, n the image-space
  index. A ray whose line passes farther than R from C misses the sphere and is reported.
- **Infinity, and telecentric image space.** As R → ∞ the bracket tends to b_r − b_c, the error
  at most n·|w|²/R: each ray carried to the plane through C normal to itself. This is the help's
  Infinity reference (the correction "given strictly by the angular error in the ray"), and it is
  what the exit-pupil reference uses when the paraxial exit pupil is at infinity, so a telecentric
  lens is the continuous limit of nearby non-telecentric ones (the sphere's sheet changes side as
  the exit pupil passes through infinity; the OPD does not jump).
- **Absolute, Absolute 2** (focal): no correction, OPD = −ΔP, the paths to the image surface.
  The reference point plays no part.
- **Afocal image space** (`Aperture.AfocalImageSpace`): a plane with unit normal u, the chief
  ray's direction (the mean ray direction for the centroid reference), through Q; a ray meets it
  at t = (Q − q)·u/(d·u) and OPD = −ΔP + n·(t_c − t_r). Exit Pupil (and Infinity, which the help
  says is the same in afocal mode): Q is the point of the chief ray's line nearest E; with the
  exit pupil at infinity this is refused. Absolute: Q is the chief ray's image intercept.
  Absolute 2: no correction.
- **Reference to primary.** The centre (or afocal direction) is the primary wavelength's; the
  OPD is still against the traced wavelength's own chief ray (section 10, item 22).
- **OPD limitations.** A surface whose shape says the OPD through it is approximate or undefined
  (`SurfaceShape.OpdLimitation`) makes the engine refuse, naming the surface, unless "Allow OPD
  limitations" is on; the result then lists it.
- **Failed rays** keep their status and surface; their OPD is NaN; they are blank on maps,
  left out of curves and statistics, and counted in the assumptions.
- **Statistics** (`WavefrontTools.Statistics`): peak-to-valley = max − min; RMS about the mean
  (piston removed), every sample weighted equally (the help's rectangular-array method;
  Gaussian quadrature is not built).
- **Tilt and focus removal** (`WavefrontTools.Remove`): a joint least-squares fit of Fringe
  Zernike terms 1–3 or 1–4 (`ZernikeFit`; terms 2, 3 and 4 are x, y and 2ρ² − 1 on the
  normalised pupil); only the removed terms are subtracted.
- **Pupil grids.** Maps use an n × n grid of nodes from −1 to 1 (an odd n puts a node on the
  chief ray); nodes outside the unit circle are blank. This is a private grid until OP-34's
  shared pupil sampling lands.
- **Interferogram:** intensity (1 − cos 2πΔ)/2, Δ = s·W + (t_x·Px + t_y·Py)/2, dark where Δ is
  a whole number.
- **Foucault:** the pupil field is e^{2πiW} (zero where the ray failed); a pupil node stands for
  the image-space direction d_c + (a_x·Px, a_y·Py), a fitted to the rays' directions; the image
  field U(x) = Σ e^{2πi(W − n·Δd·x/λ)} is the matrix Fourier transform on a window of
  ±(1.5 × the rays' geometric spread + 16 λ/(n·a)) at spacing λ/(4n·a); the knife zeroes its
  half-plane (left x ≤ pos, right x ≥ pos, above y ≥ pos, below y ≤ pos, in the image surface's
  frame, from the chief ray); the inverse sum returns to the pupil; the intensity is divided by
  the mean of the same reconstruction without the knife. A window needing more than 4097 samples
  per axis is refused, as is afocal image space. A pupil sampled every Δp makes the image
  field periodic with period λ/(n·a·Δp); so the spot's replicas do not overlap the window
  (±half), the pupil grid is raised until Δp ≤ λ/(2n·a·half), with 10 % to spare, up to 1025
  nodes per side (refused beyond), and the result says so.
- **Pupil aberration:** 100 × (real stop intercept − P × r)/r, r the on-axis primary-wavelength
  paraxial marginal ray height at the stop; the y component against Py (tangential), x against
  Px (sagittal); apertures ignored.

Not built: OpticStudio's system-level Reference OPD setting (each analysis has its own), the
Rimmer centroid correction, polarization phase in the wavefront, "Use Exit Pupil Shape" on the
maps, rotation and surface plots, analyses at intermediate surfaces, subapertures, STAR data,
Gaussian-quadrature RMS, the interferogram's second beam from another configuration, "Ref Beam
To Vertex" and "Consider Path Length Difference", the Foucault reference bitmap, the difference
display, its registration and the FOUC operand.

## 27. Field curvature, distortion, colour and relative illumination

OP-37 adds six analyses under `OpenSim.Optics/Analysis/FieldAberrations/`. Their definitions are
read from the Ansys OpticStudio 2025 R2 help pages "Field Curvature and Distortion", "Grid
Distortion", "Longitudinal Aberration", "Lateral Color", "Chromatic Focal Shift", "Relative
Illumination" and "Apodization Type" (saved in `plans/sources/optics/op-37-help/` with their
equation images). The tests are `OpenSim.Tests/Optics/FieldAberrationTests.cs` and
`OpenSim.Tests/Optics/Gates/FieldAberrationGateTests.cs`. Readings not checked against
OpticStudio are section 10, item 23.

**Field scans.** A scan runs along +y, +x, −y or −x (`FieldScan`) in normalised field H from 0 to
1, with the fields normalised radially for the scan, so H = 1 is the largest radial field
(`FieldScanCommon.ScanSystem`). The tangential plane contains the scan direction; the sagittal
direction is its normal (x̂ for a y scan, ŷ for an x scan).

**Coddington traces** (`CoddingtonTrace`). Along the traced real chief ray, the sagittal and
tangential distances s and t to the foci (along the ray, positive in the direction of travel)
follow n′/s′ = n/s + φ_s and n′cos²I′/t′ = n cos²I/t + φ_t, then s ← s′ − d and t ← t′ − d over
each segment d. The equations are those of RayOptics 0.9.10 (`raytr/trace.py`,
`trace_coddington_fan`, BSD 3-Clause), which states them for spheres with
φ = c(n′cos I′ − n cos I). Here φ_t and φ_s use the surface's normal curvatures κ_t and κ_s in
the tangential and sagittal directions at the hit point, from the second fundamental form of its
sag (closed form for the Standard conic; central differences of the slopes for a sag shape;
second differences of the sag otherwise). This extension is derived in the code, not taken from
a source; the tests check it against finite-difference foci of real rays. Cosines are signed
against the surface's +z-at-vertex normal and indices are positive, so mirrors need no special
case. The trace refuses, naming the surface, when the chief ray or a normal leaves the meridional
plane (a skew pencil), when the tangential and sagittal directions are not principal directions
of the surface, and at a surface that turns rays by its own rule (paraxial, ABCD, Fresnel,
lenslet, retroreflector). Field curvature is t and s at the image surface times |N| of the chief
ray: the distance along the image surface's z from the chief ray's image point to each focus,
positive in the direction of travel.

**Distortion** (`FieldCurvatureDistortion`). The real chief ray's radial image height r against
the reference f·g: g is tan θ (F-tan) or θ (F-θ), θ the object-space chief ray's angle to the
axis, for angle fields, or the field height for height fields (F-θ is refused for those). f is
the slope dr/dg from chief rays at H = ±10⁻⁴; the calibrated forms replace it by Σ r·g/Σ g² over
the scan. Percent distortion is 100·(r − f·g)/(f·g), at the axis its limit 100·(k/f − 1). An
F-tan scan of angle fields beyond 89° stops at 89°. Real image heights are read as paraxial
image heights. When the Coddington trace refuses, the distortion is still given and the field
curvature is listed as not computed.

**Grid distortion** (`GridDistortion`). Field coordinates are (tan θx, tan θy) for angle fields
and heights otherwise; the grid's corner is at the largest radial field, or the grid has a given
full x width, with the H/W aspect; the ABCD matrix comes from central differences at ±10⁻⁴ of
the half-width about the reference field; percent distortion follows the help's sign rule;
SMIA-TV uses the four corners and the ends of the centre column. Theodolite fields are refused.

**Longitudinal aberration, lateral colour, chromatic focal shift.** Axis crossings are measured
from the image surface's vertex along its z, positive in the direction of travel
(`FieldScanCommon.AxisCrossing`; paraxially −h/u at the image surface). Longitudinal aberration
uses on-axis rays (0, 0, 0, ρ) at each wavelength, ρ = 0 being the paraxial marginal ray.
Lateral colour is the chief ray's image-surface y, real (as the launcher aims it) or paraxial
(each wavelength's own paraxial chief ray). The focal shift is the marginal ray's crossing
(paraxial, or real at a pupil zone) minus the primary wavelength's paraxial crossing, at
wavelengths spread evenly over the table's range; the depth of focus is 4λF² with the paraxial
working F/#.

**Relative illumination** (`RelativeIllumination`). Rimmer's paper (Proc. SPIE 655, 1986), which
the help cites, was not available; the integral is derived in the code. With radiance conserved
along rays and a uniform Lambertian object, the image irradiance is proportional to
A = ∫∫ τ·a²·(d̂·n̂)/|N|·|∂(L, M)/∂(Px, Py)| dPx dPy over the unit pupil: the projected solid
angle of the exit pupil seen from the image point, as an area in direction-cosine space. τ is
the ray's transmission (1, or the polarization trace's unpolarized transmission), a the
apodization amplitude (Gaussian e^(−Gρ²), cosine cubed (1 + (ρ tan α)²)^(−3/4), from "Apodization
Type"), n̂ the image surface's normal. The quadrature is ⌈n/2⌉ Gauss–Legendre radii by 2n angles;
the Jacobian comes from central differences at ±10⁻⁵ traced with apertures ignored.
RI = A/max A; effective F/# = √(π/(4A)).

**Not built.** The afocal forms (diopters, angles); field curvature through skew pencils (a
general astigmatic trace) and through surfaces that redirect rays by their own rule; the SMIA-TV
option, reference field and H/W aspect of Field Curvature and Distortion; the Display (cross or
vector) choice of Grid Distortion; the Airy disk of Lateral Color; the solid-angle apodization
of an object-cone-angle aperture; plot-scale settings (the plot window scales itself). The
Vignetting Plot is OP-22's (section 16).

## 28. Surface shape analyses

`OpenSim.Optics/Analysis/SurfaceAnalyses/` (OP-43) holds OpticStudio's Sag Table, Surface Sag,
Surface Curvature, Surface Slope and the three cross-sections, registered under the category
"Surface". Their settings and the meaning of each column are read from OpticStudio's 2025 R2
help pages "Sag Table" (Tolerance tab, Manufacturing Drawings and Data), "Surface Sag",
"Surface Curvature", "Surface Slope", "Surface Sag Cross Section", "Surface Curvature Cross
Section", "Surface Slope Cross Section" and the operand page "Best Fit Sphere Data", saved under
`plans/sources/optics/op-43-help/`.

- **Frame.** Every quantity is in the surface's local frame (§3): vertex at the origin, +z along
  the local axis, curvature positive when the surface bends toward +z. The maps cover the clear
  semi-diameter round the vertex on an n × n grid (n odd, 5 to 1025, default 65), blank (NaN)
  outside it and where the surface's aperture blocks; the cross-sections run along a line through
  the vertex at an angle from the local x axis, across the full clear diameter. An automatic
  clear semi-diameter is sized from rays (§13).
- **Sag and slopes** (`SurfaceProbe`). The Standard surface's sag and slopes are the conic's
  (`ConicSag`, §13); an asphere's are its own analytic sag and slopes (§17); any other shape's
  sag is `SurfaceShape.Sag` and its slopes are −nx/nz and −ny/nz from `SurfaceShape.Normal`, the
  slope the trace refracts with. Slopes are dimensionless dz along a direction of the vertex
  plane: tangential is radial from the vertex, sagittal at right angles to it (both +x on the
  axis), x and y the axes, and the modulus |∇z| ("the maximum length of the slope vector").
- **Curvatures.** The normal curvature of the surface in the direction of the vertex plane
  (u, v): κ = (Lu² + 2Muv + Nv²)/(Eu² + 2Fuv + Gv²), with E = 1 + zx², F = zx·zy, G = 1 + zy²
  and L, M, N = zxx, zxy, zyy divided by W = √(1 + zx² + zy²). For a surface of revolution this
  is z″/(1 + z′²)^(3/2) tangentially and z′/(r·√(1 + z′²)) sagittally, so a sphere's is 1/R
  everywhere. The modulus is the larger magnitude of the principal curvatures, the eigenvalues
  of the Weingarten matrix I⁻¹·II (from its entries, so the root keeps its digits on a sphere).
  The Standard conic's second derivatives are analytic (d²z/dr² = c/φ³, (dz/dr)/r = c/φ,
  φ = √(1 − (1 + k)c²r²)); every other shape's are central differences of its analytic slopes,
  Richardson-extrapolated from h and h/2 with h = 10⁻³ of the clear semi-diameter: truncation
  error −h⁴/480 times the sag's sixth derivative, rounding about ε·|slope|/h, together below
  10⁻¹² of the curvature for a 50 mm sphere at a 10 mm semi-diameter. Where a stencil point
  falls outside the sag's domain the sample is blank and the count is reported.
- **Best-fit sphere** (`BestFitSphere`). As the Sag Table help says, "only the Y-coordinate of
  the surface is considered": the fit reads the profile z(0, y) over [minimum, maximum radius]
  (both 0: 0 to the clear semi-diameter), sampled at 2001 equal steps, with integrals by
  Simpson's rule and the area weight 2πr. Sphere sag s(r) = c·r²/(1 + √(1 − c²r²)).
  *Minimum RMS, no offset:* c minimises the area-weighted RMS of z − s. *Minimum RMS, with
  offset:* the offset is the weighted mean of z − s, and c minimises the RMS of what is left.
  *Minimum volume:* the sphere is moved along z until it touches the surface from the air side,
  so every depth to remove is ≥ 0, and c minimises the volume ∫ depth·2πr dr. c is found by
  scanning 400 values over |c| < 1/r_max (and the vertex curvature), refining with Brent's
  method (10⁻¹⁰ relative), and for the RMS criteria Gauss–Newton steps on c with
  ∂s/∂c = r²/(φ(1 + φ)) (derived), kept only while they lower the RMS.
- **Air side.** The side with the lower index at the primary wavelength is air; on a mirror the
  side the light arrives from. The light travels +z after an even number of mirrors and −z after
  an odd number (§3). With equal indices the side cannot be told: +z is taken to go from air to
  glass (as OpticStudio's listing words it) and the result says so. Reverse Direction takes the
  other side.
- **Sag Table columns** (the help's eight): Y-coord; Sag; BFS Sag, the sphere's sag from its own
  vertex; Deviation = BFS Sag − Sag, "ignoring any vertex offset"; Remove, the depth from the
  sphere placed at its vertex offset to the surface, measured into the material (positive =
  material to remove); Slope, BFS Slope and Slope Delta = Slope − BFS Slope (dz/dr along +y).
  Figures: BFS radius and vertex offset; for minimum volume the maximum and RMS depth to remove
  and the volume to remove; for the RMS criteria the RMS difference (OpenSim's addition); the
  maximum and RMS slope deviation over the help's "1000 equally spaced Y-points". Rows run from
  the vertex in steps of the step size (0: the maximum radius over 50 rounded down to 1, 2 or
  5 × 10ⁿ, OpenSim's rule) to the maximum radius.
- **Remove** (maps and cross-sections). None; Base Radius, the sphere of the vertex radius
  (conic 0, no offset); Best Fit Sphere by the chosen criterion, fitted over 0 to the clear
  semi-diameter. The removed sphere's own value at the same point (its sag plus its vertex
  offset, its slope, or its curvature) is subtracted.
- **Contour format** (Surface Sag help, "The Contour Format String"): blank for the default; one
  value for the interval (more than 100 contours fall back to the default, with a note); several
  values for only those contours; in the map's units.

**Not built.** Surface Phase, Phase Slope and their cross-sections (no surface type carries a
phase: there are no diffractive surfaces yet); Off-Axis Coordinates; Remove "Base Sag" and
"Composite Sag" (no STAR data or composite surfaces); Show As surface plot and grey scale; the
BFSD operand (a merit-function item); a best-fit sphere of a surface that is not rotationally
symmetric beyond its y profile. The readings in §10 item 24 are not compared with OpticStudio's
output.

## 29. Reports

`OpenSim.Optics/Analysis/ReportAnalyses/` (OP-44) builds OpticStudio's Reports group as text
analyses on `StudyReport` sections (`OpticsAnalysisResult.FromSections`), all in the menu group
"Reports". Contents and section order are read from OpticStudio's 2025 R2 help, pages "Reports
Group", "System Data", "Prescription Data (reports group, the analyze tab, sequential ui mode)",
"Surface Data", "Cardinal Points (Reports group, the Analyze tab, sequential ui mode)", "Report
Graphic" and "System Summary Graphic", saved under `plans/sources/optics/op-44-help/`. The help
lists the sections but not their layout, so the tables are this program's. Every report is
written in the invariant culture and carries no timestamp, so a report of the same system is the
same bytes on every run.

- **System Data** (`SystemDataAnalysis`). The help: "pupil positions and sizes, magnification,
  F/#, etc." Assembled from the analyses that already compute them: `SystemSummaryAnalysis`
  (project, mode, environment; in the system's lens unit, conventions left out), the system
  inputs (lens unit, aperture, fields, wavelengths), and `ParaxialDataAnalysis` without its ray
  table and cardinal points (system data, ABCD matrix, focal data by wavelength).
- **Prescription Data** (`PrescriptionDataAnalysis`). The help's sections in its order, where
  OpenSim has the data: General Data (system inputs and the paraxial system data), Surface Data
  (type, radius, thickness, material, semi-diameter, conic, comment), Surface Detail (surface
  parameters, coordinate return, mechanical semi-diameter, chip zone, TCE, coating, ignore and
  skip flags, then the tables Tilts and decenters and Apertures), Edge Thickness,
  Solves/Variables (the saved flags; the listed values are the solved ones), Index Data (index
  relative to the system's air at every wavelength, the glass's catalog TCE and the surface's
  TCE column), Global Vertex (the sections of `GlobalVertexAnalysis`, §15, reused), Center Of
  Curvature, Element Volume, Cardinal Points and Files Used (glass catalogs by file name, so the
  report does not depend on the machine's paths, and coating names). Not built: Multi-Config
  Data (OP-30), F/ Numbers (OpticStudio's real working F/# per field and wavelength), POP
  Settings, and NSC group data; the report lists them under "Not in this report".
- **Surface Data** (`SurfaceDataAnalysis`). For one surface: shape, curvature, thickness, the
  media on either side, clear semi-diameter, sag at (a, 0) and (0, a), edge thickness, global
  vertex, surface power φ = c·(n′ − n) with signed indices (or a special surface's paraxial
  equivalent), the surface focal lengths n′/φ and −n/φ, and for a surface followed by glass the
  element's power (−C of the two surfaces' matrix with the media on either side) and focal
  length 1/φ, all at the primary wavelength; the index before and after at every wavelength;
  the volume of the element it fronts. Not built: the "best fit glass" OpticStudio lists for a
  model glass.
- **Cardinal Points** (`CardinalPointsAnalysis`, `CardinalPlanes`). Settings First Surface, Last
  Surface (0 for the last surface) and Wavelength, as the help names them. With M = [[A, B],
  [C, D]] the range's matrix on (y, n·u) and n, n′ the signed indices before the first and after
  the last surface, an object plane at z from the first vertex and its image at z′ from the last
  are conjugate with lateral magnification m when z′ = n′(m − A)/C and z = n(D − 1/m)/C
  (derived: T(z′/n′)·M·T(−z/n) has B = 0, A = m and D = 1/m). Principal m = +1, anti-principal
  m = −1, nodal m = n/n′, anti-nodal m = −n/n′ (angular magnification n/(n′m) = ±1), focal
  planes z′ = −n′A/C and z = nD/C. Positions are along z, positive toward +z, negative after an
  odd number of mirrors, as §11. For the whole lens they reproduce `ParaxialSystem`'s focal,
  principal and nodal positions. The help's Orientation setting (XZ or YZ) is not built: the
  paraxial engine traces the y meridian. A lens whose tilts do more than fold the axis is
  refused (§15).
- **System Summary Graphic** (`SystemSummaryGraphicAnalysis`). The few first-order numbers a
  report page shows (focal lengths, F/#, pupil, track, field, wavelengths), as text: the graphic
  panel is not drawn.
- **Report Graphic** (`ReportGraphicAnalysis`). Layout 2x2, 2x3 or 3x3 (4, 6 or 9 positions)
  and the saved id of the analysis at each position (blank for none; the report graphic itself
  is refused, an unknown id fails naming the position). Each position's analysis is computed
  with the settings it was last used with in the project (`OpticsAnalysesSettings.LastSettings`),
  or its defaults; its plots and sections are gathered in position order, titled "n. Name: …",
  after a Positions table. A failing analysis shows "Failed: message" at its position and in
  the assumptions. The default positions (System summary graphic, Paraxial Gaussian beam,
  Cardinal points, Single ray trace) are this program's choice. Not built: the page arrangement
  (OpticStudio's Top Left, Centered and Clustered layout modes; the window lists the plots in
  order), per-position settings inside the report, and saved report files (.22, .23, .33).

**Semi-diameters, edge thickness and elements.** Semi-diameters are the clear ones as entered,
or sized by `SemiDiameterSizing` (§13) when automatic; the image surface's alone does not start
a sizing trace. The edge thickness of surface j is the axial distance to surface j + 1 plus that
surface's sag minus surface j's, at (a, 0) for X and (0, a) for Y, a surface j's clear
semi-diameter; it is given only when surface j + 1's frame is surface j's moved along its z (no
relative tilt or lateral decenter, to 10⁻¹²). An element is every space between surfaces j and
j + 1 whose medium is not the system's air, so a cemented doublet is two elements; a mirror,
coordinate break or ignored surface as a face, faces that are not coaxial, and unsized
semi-diameters are listed as not computed with the reason.

**Element volume** (`ElementVolume`). Each face has a circular edge at its clear semi-diameter
and the smaller face is "squared up" to the larger, as the help states for spherical and plane
faces: beyond its edge it continues as a plane at its edge sag. With R the larger
semi-diameter, V = π R² t + W(back) − W(front), W the integral of the clamped sag over the disc
of radius R. Standard faces (plane, sphere, conic) are exact: the conic sag obeys
r² = 2ρs − (1 + k)s² (ρ = 1/c), so ∫₀^a 2πr s dr = π s_a²(ρ − 2(1 + k)s_a/3) (derived in
`ElementVolume`). Other shapes are numerical: a rotationally symmetric sag with a 64-node
Gauss–Legendre rule in r, any other sag with the same rule times a 128-point trapezoidal rule
in θ. The help says OpticStudio "tapers" non-rotational faces instead and reaches "about 0.1%"
numerically; here every face is squared up. Faces that cross (local thickness not positive on 65
radii in four directions) fail with the element named. Surface apertures (rectangular,
elliptical, decentered) do not enter. Mass is the volume in cc times the catalog density
(`OpticalMaterial.Density`, g/cc); a constant index has none. OpticStudio's 3.6 g/cc for
gradient-index surfaces is not used (no gradient-index surface is built).

**Reading the prescription back** (`PrescriptionReader`). The four surface sections write every
value the trace uses in round-trip precision ("R" format; "Infinity"), the comment as a quoted,
escaped string in the last column, and the stop as the row label STO. `PrescriptionReader.Read`
takes the report's plain text (the Text tab) and returns the lens unit (from the "Radius (unit)"
header) and the saved surfaces; `SavedSurfaces.ToLens` with the same catalogs gives the lens
back. It reads only this program's report, not OpticStudio files. Not carried: variable, solve
and pickup flags (the read-back lens holds the solved values), the drawing options (hide rays,
do not draw, row colour), and model glasses, which `SavedSurfaces.FromLens` writes by name.

## 30. Jones and Mueller matrix surfaces

OP-52 adds two plane surface types that act on the polarization ray trace of section 21:
OpticStudio's Jones Matrix surface and a Mueller Matrix surface of this program's own
(`OpenSim.Optics/Polarization/MatrixSurfaces.cs`, `StokesVector.cs`, and additions to
`PolarizationTracer.cs`). Sources, saved under `plans/sources/optics/op-52-help/`: OpticStudio
2025 R2 help, "Jones Matrix (sequential surfaces, lens data editor)", "Defining Polarizing
Components" (its sample Jones matrices) and "Summary Table of Sequential Surface Types";
Wikipedia, "Stokes parameters" and "Mueller calculus".

- **Geometry.** Both are planes ("The surface shape is always plane"), traced by the same
  `ConicIntersection` calls as a Standard surface of zero curvature and conic, so the geometric
  trace is a plane's bit for bit. Rays refract or reflect there by the media on the two sides,
  and the surface is an ordinary interface for the Fresnel or coating amplitudes of section 21;
  the matrix acts after that interface, on the light leaving the surface. Radius and conic are
  refused on both types when a saved lens is read. Only the polarization trace and its analyses
  see the matrix, as the help says of the Jones surface.
- **Jones Matrix parameters** (help table): 1 Ar, 2 Ai, 3 Br, 4 Bi, 5 Cr, 6 Ci, 7 Dr, 8 Di;
  (Ex′, Ey′) = (A Ex + B Ey, C Ex + D Ey). The entries are read as OpticStudio complex numbers
  in its convention, so the matrix applied in this program's convention (section 21) is the
  conjugate, A = Ar − i·Ai and so on (a field in one convention is the conjugate of the field in
  the other, and conj(JE) = conj(J)·conj(E)).
- **Local frame.** Ex and Ey are read along the surface's local x and y axes. For a ray leaving
  along k̂ the transverse basis is û = the part of x̂_L at right angles to k̂, normalised, and
  v̂ = k̂ × û (where k̂ lies along x̂_L: v̂ = the part of ŷ_L at right angles to k̂, û = v̂ × k̂).
  At normal incidence (û, v̂) = (x̂_L, ŷ_L). `PlaneMatrixShape.TransverseBasis`.
- **Embedding in the 3 × 3 calculus.** P_J = [û v̂]·J·[û v̂]ᵀ + k̂k̂ᵀ
  = û(A ûᵀ + B v̂ᵀ) + v̂(C ûᵀ + D v̂ᵀ) + k̂k̂ᵀ. It maps a transverse field Eu û + Ev v̂ to
  (A Eu + B Ev) û + (C Eu + D Ev) v̂ and k̂ to k̂, like the interface matrices, and multiplies
  the surface's interface matrix from the left (P = P_J·P_interface), so the system matrix stays
  the product of the surfaces' matrices. The help says OpticStudio adjusts Ez, or Ex and Ey,
  after the matrix so that k·E = 0 without increasing |E|, and gives no formula; this program's
  embedding keeps the field transverse by construction, never increases |E| for a passive
  matrix (singular values at most 1) and keeps |E| for a unitary one at every angle, but off
  normal incidence it need not give OpticStudio's numbers (section 10, item 26). An exact
  identity matrix (Ar = Dr = 1, the rest 0) is skipped, so it leaves every number of the trace
  unchanged.
- **Mueller Matrix surface.** OpticStudio has no sequential Mueller surface (the summary table
  lists none). Parameters 1 to 16 are M00 … M33 row-major (parameter 4r + c + 1 is M_rc),
  dimensionless; 13–16 are saved as extra data like every registered type's numbers from 13.
  S_out = M·S_in with the Stokes vector in the basis (û, v̂) above. An output that is not a
  physical Stokes vector (I < −10⁻¹²·I_in, or √(Q² + U² + V²) > I + 10⁻¹²·I_in) stops the
  trace with a message naming the surface; a full test of the physical realisability of M is
  not made.
- **Stokes vector.** In a basis (û, v̂), I = |Eu|² + |Ev|², Q = |Eu|² − |Ev|²,
  U = 2 Re(Eu Ev*), V = −2 Im(Eu Ev*) ("Stokes parameters", representation in a fixed basis),
  applied to this program's amplitudes; it is S = A·(E ⊗ E*) with "Mueller calculus"'s matrix A,
  and a Jones matrix's Mueller matrix is A·(J ⊗ J*)·A⁻¹ (`StokesVector.MuellerFromJones`). Which
  sign of V is right-handed is not asserted; the degree of circular polarization is V/I, signed.
  The degrees of polarization, of linear and of circular polarization are √(Q² + U² + V²)/I,
  √(Q² + U²)/I and V/I, NaN where I = 0.
- **How the two calculi compose.** A ray is described by its field E until it meets a Mueller
  surface. There the trace forms the 3 × 3 coherency matrix Φ = E E^H (surface 1's frame),
  reads the Stokes vector from its transverse 2 × 2 block in (û, v̂), applies M, and writes the
  result back as Φ = Σ c_ab â b̂ᵀ with c00 = (I + Q)/2, c11 = (I − Q)/2, c01 = (U − iV)/2
  (`CoherencyMatrix`). From then on every polarization matrix P of section 21 (interfaces, Jones
  surfaces) acts as Φ → P Φ P^H and bulk absorption as Φ → T·Φ; further Mueller surfaces act as
  above. The power is tr Φ. A fully polarized state has Φ = E E^H, so for a ray that meets no
  Mueller surface the two descriptions agree; every step is linear in Φ, so unpolarized light is
  still the mean of the (1, 0) and (0, 1) traces (`PolarizationTracer.FinalStokes`).
- **What is reported after a Mueller surface.** The field and the system matrix are not defined
  (NaN in `PolarizedSurface.FieldOut`, `Cumulative` and `Ray.Field`); `PolarizedSurface.Coherency`
  and `PolarizedPath.FinalCoherency` hold the state, and `PolarizedPath.MuellerSurface` names
  the first such surface. Every transmission (per surface, per ray, the Transmission and
  Transmission fan analyses, `PolarizationTracer.Transmission`) is tr Φ. The Polarization ray
  trace shows dashes for Ex, Ey, Ez after the surface, "not defined" for the system
  diattenuation and no system matrix; for every ray it now also lists the Stokes vector at the
  last surface reached (that surface's local x, y basis) with the three degrees of polarization.
  The Polarization pupil map at or after a Mueller surface, and the Phase aberration of a lens
  with one, refuse with a message naming the surface, since partially polarized light has no
  single ellipse or phase.
- **Gates** (`OpenSim.Tests/Optics/JonesMuellerSurfaceTests`): Malus's law, cos²θ through two
  polarizers (½cos²θ unpolarized, degree of polarization 1 after them); a quarter-wave plate at
  45° (the help's quarter-wave plate rotated) turns x-linear light circular, |V|/I = 1; the
  depolarizer diag(1, d, d, d) leaves a degree of polarization d for linear and circular light,
  and an analyzer behind it passes (1 + d)/2; an identity Jones surface leaves positions,
  directions, path, fields and matrices unchanged bit for bit against a Standard plane; a
  unitary Jones matrix keeps the power at oblique incidence; A·(J ⊗ J*)·A⁻¹ reproduces two
  entries of "Mueller calculus"'s table, and a Mueller surface holding the quarter-wave plate's
  matrix gives the Jones surface's Stokes vector.
- **Not built.** The non-sequential Jones Matrix object and any Mueller object (they need the
  non-sequential core, OP-70); a test of the physical realisability of a Mueller matrix (only
  its output on each ray is checked); diattenuation and retardance of a system containing a
  Mueller surface; the pupil map of the polarized part of partially polarized light;
  depolarization by anything but a Mueller surface (scattering, coatings; birefringence is
  OP-53).

## 31. Merit function

`OpenSim.Optics/Merit` (OP-54) evaluates a merit function: a list of operand rows, each with
OpticStudio's four-letter code, two integer and six real parameters (Int1, Int2, Data1 to
Data6), a target and a weight. The definitions were read from OpticStudio's 2025 R2 help, saved
under `plans/sources/optics/op-54-help/` (Modifying the merit function, Notes on Operand
Weights, Understanding Boundary Operands, Merit Function Control Operands, General Math
Operands, Changing System Data, the category pages and the alphabetical operand list; the two
equation images are saved rendered as PNG beside their SVGs).

- **The merit value.** MF² = Σ Wᵢ(Vᵢ − Tᵢ)² / Σ Wᵢ, with W the absolute value of the weight,
  V the value and T the target (help: Modifying the merit function, "Merit function
  definition"). Rows with weight 0, control rows, CONF and skipped rows are not in either sum.
  With no weighted row the merit value is 0. Each sum adds its terms smallest first, so
  reordering rows that do not read each other gives the same bits. Negative weights count by
  their absolute value; their Lagrange-multiplier meaning belongs to the optimizer (§35).
- **Percent contribution.** 100·Wᵢ(Vᵢ − Tᵢ)² / Σⱼ Wⱼ(Vⱼ − Tⱼ)² (help: Notes on Operand Weights).
- **Residuals.** One per row, rᵢ = √(Wᵢ/ΣW)·(Vᵢ − Tᵢ), zero for rows that do not count, so
  Σ rᵢ² = MF². `MeritResiduals` hands them to the optimizer kernel as its `ResidualFunction`
  (OP-5); how the variables change the lens is the caller's.
- **Bounds.** A one-sided bound (CTGT, CTLT, CVGT, CVLT, OPGT, OPLT, ABGT, ABLT) takes the
  target as its value while it holds and the actual value when violated (help: Understanding
  Boundary Operands). MNCT's value is the target less the sum of the violations over its
  surface range (the help's example: thicknesses 2.5 and 2.2 under 3.0 give 1.7); MXCT is the
  same with the sign turned.
- **Control rows** (help: Merit Function Control Operands). BLNK and DMFS do nothing (DMFS only
  marks where the Optimization Wizard's default merit function goes, §40); OOFF is evaluated as BLNK and is what a
  saved code this build does not know becomes, named in the load notes; ENDX ends the
  evaluation; GOTO continues at row Op#; SKIS continues at row Op# when the lens is rotationally
  symmetric, SKIN when it is not; USYM anywhere makes every configuration count as symmetric.
  Jumps go forward only. The order of evaluation depends on these rows alone, never on a value.
- **Configurations.** CONF switches the configuration for the rows after it (help: Changing
  System Data); evaluation starts in configuration 1 unless told otherwise. Configurations come
  from `IMeritConfigurations` (a lens and system data per number); `SingleMeritConfiguration`
  is the project's one configuration until multi-configuration (OP-30) implements the interface.
- **Math rows** read the values of earlier rows by number. A math row reading a failed or
  skipped row fails, naming it.
- **Failures.** A row whose value cannot be computed (a ray that fails at or before the surface
  it reads, a surface or wavelength that does not exist, a negative square root, a division by
  zero, an afocal system for EFFL) fails alone: its value is NaN, its message names the row,
  and it stands in the sums with V − T = `FailedRowPenalty` (10⁶ by default, this program's
  choice). Problems of the list itself (an unknown code, a backward jump, a math row reading a
  later or a control row, a CONF to no configuration) are refused before anything is computed,
  every one named.
- **Shared traces and determinism.** The real rays the evaluated rows read are collected once
  per configuration, wavelength, field and pupil point and traced in parallel, each into its own
  slot; the rows are then evaluated in order. The result is bitwise the same at any degree of
  parallelism.
- **Units.** Lengths in lens units, curvatures in 1/lens unit; real-ray coordinates are local to
  the surface (help: Constraints on Real Ray Data, "Local real ray y-coordinate"). Total track
  adds the thicknesses from surface 1 to the image and takes the greatest less the least vertex
  z (help: Total Track).

**Operands built** (`MeritOperandRegistry.CreateDefault`; OpenSim's names, codes alongside):
the control rows BLNK, DMFS, ENDX, GOTO, OOFF, SKIN, SKIS, USYM and CONF; EFFL and TOTR; CTVA,
CTGT, CTLT, CVVA, CVGT, CVLT, COVA, TTHI, MNCT and MXCT; REAX, REAY, REAZ and REAR; and the
general math operands ABSO, ACOS, ASIN, ATAN, CONS, COSI, DIFF, DIVB, DIVI, LOGE, LOGT, MAXX,
MINN, OPGT, OPLT, OPVA, OSUM, PROB, PROD, QSUM, RECI, SQRT, SUMM, SINE, TANG, ABGT and ABLT.

**Not built.** The rest of the operand catalogue (OP-55); EQUA (the help does not say how its
value meets its target); the default merit function and its wizard are §40; the optimizer is
§35; the merit function listing; reading or writing OpticStudio's .MF
files (the help names the file but does not document its format).

## 32. Seidel and full-field aberrations

OP-36 adds three analyses in `OpenSim.Optics/Analysis/Aberrations/`: Seidel Coefficients,
Seidel Diagram and Full-Field Aberration. Their definitions are read from the Ansys OpticStudio
2025 R2 help pages of the same names (saved with their equation images in
`plans/sources/optics/op-36-help/`). The tests are `OpenSim.Tests/Optics/SeidelTests.cs` and
`OpenSim.Tests/Optics/Gates/SeidelGateTests.cs`. Readings not checked against OpticStudio are
section 10, item 28.

**Surface sums** (`SeidelEngine`). From the paraxial marginal ray (y, u) of the system aperture
and the chief ray (ȳ, ū) of the largest radial field, at the selected wavelength, with signed
indices (section 11): A = n(u + yc), Ā = n(ū + ȳc), H = n(ūy − uȳ), Δ(u/n) = u′/n′ − u/n,
Δ(1/n²) = 1/n′² − 1/n², P = c(1/n′ − 1/n), and

- S1 = −A²yΔ(u/n), S2 = −AĀyΔ(u/n), S3 = −Ā²yΔ(u/n), S4 = −H²P,
  S5 = −Ā(Ā²Δ(1/n²)y − (H + Āy)ȳP);
- CL = AyΔ(δn/n), CT = ĀyΔ(δn/n), δn = n(shortest) − n(longest) of each space, signed like n;
- an aspheric surface adds 8G(n′ − n)·(y⁴, y³ȳ, y²ȳ², 0, yȳ³) to (S1 … S5), G being the r⁴
  coefficient of its sag minus c_p³/8 of its paraxial sphere (for a Standard surface kc³/8).

These are RayOptics 0.9.10's formulas (`rayoptics/parax/thirdorder.py`, BSD 3-Clause), written
anew here; G is generalised to surfaces with an r² term (derived in
`SeidelEngine.FourthOrderDeparture`). Welford's book, which the help cites, was not on hand. c is
the paraxial curvature (an asphere's r² term included). Supported surfaces are those the help
lists: Standard, Even, Odd, Extended and Extended Odd Asphere; the alternate-root forms, odd
aspheres with an r or r³ term, and every other shape are refused naming the surface. Ignored
surfaces and coordinate breaks contribute nothing; tilts are accepted only where the paraxial
lens is (section 15). Mirrors need no special case.

**Conversions** (`SeidelResult`). Waves at the edge of the exit pupil: W040 = S1/8,
W131 = S2/2, W222 = S3/2, W220P = S4/4, W311 = S5/2, W020 = CL/2, W111 = CT,
W220S = (S3 + S4)/4, W220M = (2S3 + S4)/4, W220T = (3S3 + S4)/4, divided by the wavelength in
the system's air. Transverse: k·S/(n′u′), n′u′ the marginal ray's reduced slope in image space,
with k = 1/2 (TSPH, TSCO, TPFC, TDIS on S1, S2, S4, S5), 3/2 (TTCO on S2), 1 (TAST on S3,
TAXC on CL, TLAC on CT), and TSFC = (S3 + S4)/(2n′u′), TTFC = (3S3 + S4)/(2n′u′); these are the
image-plane ray errors (TSPH is the marginal ray's height on the paraxial image plane to third
order). Longitudinal: the same with |n′|u′² in place of n′u′, positive when the rays cross the
axis before the paraxial image along the light. Both are undefined (not listed) when the image is
at infinity. Petzval radius: 1/R_p = −|n_m| Σ (nᵢ − nᵢ₋₁)/(nᵢnᵢ₋₁Rᵢ), signed as a radius
(section 3), infinite when the sum is zero.

**Seidel Diagram.** The unconverted S1 … S5, CL, CT of a surface range and their sum as bars
(rectangles in a line plot, `SeidelDiagramAnalysis.Bars`), the sum at the last surface + 1;
distortion and colour can be left out.

**Full-field aberration** (`FieldZernike`, `FullFieldAberrationAnalysis`). A grid of
nx × ny field points centred on a chosen field, half-widths in the field's unit (0: the largest
radial field), rectangular or elliptical; points outside the field normalisation boundary are not
computed. Each point is appended to the field list (the scale is unchanged, being inside the
boundary) with vignetting factors cleared, its OPD computed by `OpdEngine` (exit-pupil reference
sphere centred on the chief ray, section 26) on an n × n pupil grid, and Zernike Standard terms
1 … N fitted by least squares over the rays that reached the reference. Plotted: Z4,
√(Z5² + Z6²) with angle ½·atan2(−Z5, −Z6), √(Z7² + Z8²) with angle atan2(−Z7, −Z8), or one
term; absolute, less the average over the computed points, or the average.

**Not built here.** The Contrast Loss Map is built with the MTF (§38); not built: the Seidel Diagram's plot scale;
Full-Field Aberration's icon display and grey-scale styles (the map is a false-colour image or
contours); fifth- and higher-order (Sasian) coefficients; the afocal forms of the transverse and
longitudinal coefficients.

## 33. Point spread function and Strehl ratio

OP-38. Code: `OpenSim.Optics/Wavefront/PsfEngine.cs` (public: `PupilFunction`, `PsfImage`,
`HuygensRay`, `HuygensPupil`, `PsfEngine`) and the analyses in
`OpenSim.Optics/Analysis/PsfAnalyses.cs`. Tests: `OpenSim.Tests/Optics/PsfAnalysisTests.cs` and the
gate `OpenSim.Tests/Optics/Gates/PsfGateTests.cs`. OpticStudio's behaviour is read from its 2025 R2
help ("PSF", "FFT PSF", "FFT Cross Section", "FFT Line/Edge Spread", "Huygens PSF", "Huygens Cross
Section", "Strehl Ratio"), saved with their equation images under
`plans/sources/optics/op-38-help/`. Goodman and Born and Wolf were not on hand; the closed forms
the gate uses are derived in its comments.

- **Pupil function.** A·e^{2πiW} on an n × n grid of normalised pupil nodes from −1 to 1
  (`OpdEngine.Node`; row = Py, column = Px). W is the OPD of section 26 (exit-pupil reference
  sphere centred on the chief ray). A is the pupil apodization (uniform, Gaussian e^{−Gρ²} or
  cosine cubed, read as section 27 reads it) where the ray reaches the image, and 0 where it is
  vignetted or fails; the failures are counted by reason in the result.
- **Pupil scale.** A node stands for the image-space direction offset (a_x·Px, a_y·Py) from the
  chief ray, on two unit axes normal to the chief ray (the image surface's x̂ and ŷ with the chief
  ray's component taken out); a_x and a_y are least-squares fits to the traced rays' direction
  offsets. The working F/# the result reports is 1/(2n·|a|), n the image-space index. A negative a
  (an inverted image) reverses the transform, so x and y stay the image's.
- **Kernel.** U(x) = Σ A·e^{2πi(W − n(a_x·Px·x + a_y·Py·y)/λ)}, λ the wavelength in the system's
  air (the Foucault kernel of section 26). Intensity is |U|² / (Σ A)²: the peak of the
  unaberrated PSF (the same A, W = 0) is 1, so the PSF's peak is the Strehl ratio.
- **FFT PSF.** The pupil is zero-padded to M = 2n (help: for 32 × 32 sampling the PSF is
  "distributed over a 64 x 64 size grid") and transformed (`Fft2D`). Sample p of each axis is at
  (p − M/2)·Δ, Δ = λ/(n·|a|·M·ΔP), ΔP = 2/(n − 1); the window is one period of the PSF, so its
  energy sums exactly (Parseval) to the transmission Σ A² / (nodes in the unit circle). The
  help's pupil-grid stretching (its default Δ = λF(n − 2)/(2n)·(32/n)^½) is not built: the
  default Δ here is the unstretched one, and the image does not grow finer with sampling. The
  plane is normal to the chief ray through the reference point, as the help says.
- **Image delta.** A chosen Δ samples the same sum at (p − M/2)·Δ by the matrix Fourier transform.
  A Δ larger than the FFT's own would make the window wider than the period and is refused (the
  help also refuses a delta "too big").
- **Polychromatic.** Every wavelength is referenced to the primary wavelength's chief ray
  (lateral colour is a shift), sampled at the shortest wavelength's FFT grid by the matrix Fourier
  transform (no interpolation), and summed with factor w·(λ_min/λ)², each over its own unaberrated
  peak, then divided by Σ w·(λ_min/λ)². The factor is the help's Huygens "wFactor" without the
  configuration weight; it gives each wavelength energy in proportion to its weight. Phase, real
  and imaginary displays with every wavelength chosen use the primary wavelength (help, "FFT PSF").
- **Strehl ratio** (help: "the peak intensity of the diffraction point spread function (PSF)
  divided by the peak intensity of the diffraction point spread function (PSF) in the absence of
  aberrations"). The aberrated peak is the largest sample refined by eight rounds of 7 × 7 patches
  of the sum, the spacing divided by 3 each round, so it does not depend on where the grid falls.
  A ratio below 0.1 is warned about (the help: "not useful … for Strehl ratios smaller than about
  0.1"). The Strehl analysis also lists σ, the RMS of W about its mean with each node weighted by
  its amplitude, and 1 − (2πσ)², the second-order estimate (derived; at the reference point it is
  a lower bound, see the gate).
- **Sampling rule** (`PsfEngine.CheckSampling`). The sum over nodes spaced ΔP repeats in the
  image with period T = λ/(n·|a|·ΔP). A wavefront slope of s waves per node puts the light at s·T
  from the reference point (stationary phase), and the window holds ±T/2, so: more than ½ wave
  between neighbouring lit nodes is **ALIASED** (the light wraps round); more than ¼ is a caution
  (the outer half holds wrapped tails). Rays whose image intercepts reach beyond ±T/2 are also
  ALIASED, as is an image grid (Huygens, chosen delta) wider than T. The help gives the tradeoff
  but no number; ½ and ¼ are this program's, derived above. The help's validity notes are warned
  about too: a chief ray more than 20° from the image surface's normal (FFT "overly optimistic")
  and an image cone faster than F/1.5 (scalar theory). Warnings head the result.
- **Huygens PSF.** Rays on the same n × n grid are traced to the image surface with the Absolute
  reference (each ray's path P relative to the chief ray, waves). The plane is "tangent to the
  image surface at the chief ray intercept" (or the centroid with Use Centroid: the mean intercept
  weighted by A²), its axes the image surface's x̂ and ŷ with the normal component taken out. Each
  ray is a plane wave (the help's "planar reference"): U(r) = Σ c·e^{2πi[P − n·d·(r − q)/λ]}, d its
  direction and q its intercept; intensity over (Σ c)². The weight c = A·√J,
  J = |∂(d·x̂′, d·ŷ′)/∂(Px, Py)| by differences between neighbouring nodes: the rays sample the
  entrance pupil evenly and carry equal power, so a distorted pupil keeps its energy where it falls
  (derived; the help does not say how OpticStudio weights rays). Default spacing λF/√n_p (help),
  λ the selected or longest wavelength, F from the fitted pupil scale; point p at (p − m/2)·Δ.
  Polychromatic: the same w·(λ_min/λ)² sum, centred on the primary wavelength. The help's switch
  to a spherical phase reference (when the exit pupil's distances to the image centre and corner
  differ by more than λ/4) is not built; the condition is warned about, λ read as the wavelength
  in the image medium.
- **Cross sections.** FFT: a row (X) or column (Y) of the 2n × 2n image, 0 for the centre (the
  reference point, "not always the peak of the PSF"). Huygens: the sum evaluated on the line
  through the centre only.
- **Line and edge spread** (help: "The X-orientation means the line or edge is parallel to the
  X-axis"). X sums each FFT row over x and runs along y; Y the reverse. The line spread is divided
  by its peak; the edge spread is the running sum (half the current sample) over the window's
  total.
- **Determinism.** The FFT, the matrix transform and the Huygens sum compute each output row on
  its own; results are bitwise the same at any degree of parallelism.

Not built: polarization (vector diffraction), the image rotation setting, surface plots,
intermediate surfaces, the help's pupil-grid stretching, the Huygens spherical phase reference,
the Huygens sum over configurations, True Color, and "Use Coherent PSF" in the line/edge spread.

## 34. RMS against field, wavelength and focus

`OpenSim.Optics/Analysis/Rms` (OP-40) computes OpticStudio's RMS vs Field, RMS vs Wavelength,
RMS vs Focus and RMS Field Map; `OpenSim.Optics/Analysis/Sampling/PupilQuadrature.cs` holds the
pupil integration rules. The definitions were read from OpticStudio's 2025 R2 help pages of
those names and "Gaussian Quadrature", "Rectangular Array", "Strehl Ratio", "Diffraction
Limited", saved under `plans/sources/optics/op-40-help/`.

- **Gaussian quadrature** (`PupilQuadrature.Gaussian`). n rings at the Gauss–Legendre nodes of
  u = ρ² on [ε², 1] (`OpenSim.Core/Numerics/GaussLegendre`), N arms at θ = 2πj/N from +x, each
  point weighted gᵢ/((1 − ε²)N); the weights sum to 1. Derived in the class remarks (Forbes,
  JOSA A 5, 1943 (1988), which the help cites, was not available): the rule averages every
  polynomial of degree ≤ min(4n − 1, N − 1) in (Px, Py) exactly, so with N = 4n (the RMS
  analyses' choice) an RMS is exact when the OPD or ray error is a polynomial of degree
  ≤ 2n − 1, the help's "(2*n − 1)". Rings 1 to 20 (the help's maximum). ε is the linear
  obscuration ("done as a linear fraction"); the RMS analyses use ε = 0.
- **Rectangular array** (`PupilQuadrature.Rectangular`): OP-34's cell-centred square grid
  (`PupilSampling.Square`), the points inside the unit pupil weighted equally.
- **Spot RMS**: √(Σw r²/Σw) of the image-surface positions (local frame) about the chief ray's
  image point (the computed wavelength's, or the primary's when polychromatic) or about the
  weighted centroid; x and y alike (§25). w is the pupil weight, times the wavelength weight when
  polychromatic.
- **Wavefront RMS**: the OPD of §26 against the exit-pupil reference sphere centred on the chief
  ray, in waves; chief ray reference removes the weighted mean, centroid reference the weighted
  least-squares a + b·Px + c·Py. Polychromatic: every wavelength's OPD against the primary
  wavelength's sphere (`ReferenceToPrimary`), pooled with weights w·(wavelength weight), in
  waves of the primary wavelength.
- **Failed rays.** Gaussian quadrature: a point where any ray fails (or misses the reference
  sphere) has no value; its message names the rays and suggests the rectangular array. The
  rectangular array leaves such rays out and counts them by status and surface.
- **Fields and wavelengths between the tables.** A sampled field is appended to the field table
  as one more point (carrying the launcher's vignetting factors at that field), a sampled
  wavelength to the wavelength table; the primary wavelength is unchanged. Floating
  semi-diameters are sized once for the system as defined and frozen, except on the stop.
- **Focus** (help, "RMS vs. Focus"): the shift, in lens units, is added to the thickness of the
  surface before the image.
- **Scans.** RMS vs Field: evenly spaced fields from 0 to the largest defined field component in
  the chosen direction (+y, +x, −y, −x), 2 to 100 points. RMS vs Wavelength: 2 to 100 evenly
  spaced wavelengths from the table's shortest to longest, each monochromatic, one curve per
  field row. RMS vs Focus: 2 to 100 shifts, one curve per field row, polychromatic or one
  wavelength. RMS Field Map: an nx × ny grid of field positions centred on a field row, half
  widths in field units (0: the largest radial field).
- **Diffraction limit** (help, "Show Diffraction Limit"): spot, 1.22 λ times the working F/# of
  the on-axis field (§25's `RealWorkingFNumber`; primary wavelength when polychromatic; a curve
  against wavelength for RMS vs Wavelength); wavefront, 0.072 waves.
- **Peak to valley** (Field Map, rectangular array, one wavelength): max − min of the residual
  OPD after the reference's piston (and tilt) removal.
- **Strehl ratio** (all four analyses; `RmsCore.EvaluateStrehl`). OP-38's PSF-peak ratio (§33),
  computed as the Strehl ratio analysis computes it, at the point's appended field, wavelength
  and focus: `PsfEngine.BuildPupil` on an n × n pupil (setting "PSF sampling", 16 to 1024,
  default 64, OP-38's default; apertures checked; OPD against the exit-pupil sphere on the chief
  ray), then `PsfEngine.Strehl` for one wavelength or `PsfRuns.PolychromaticStrehl` (OP-38's
  polychromatic FFT PSF: every wavelength referred to the primary wavelength's chief ray, weight
  × (λ_min/λ)², the peak refined off the grid) for several. The quadrature rule and Refer to do
  not apply (the peak is searched for wherever it lies). A failed ray carries no light, so the
  point keeps a value; the PSF sampling check, the pupil's warnings and a ratio below 0.1 are
  listed in the result, each naming the point. Diffraction limit 0.8 (help, "Show Diffraction
  Limit": "For Strehl ratio, 0.8 is used").
- **Determinism.** The sample points are evaluated one per `ParallelSlots` slot, each tracing
  with one thread, so the result is bitwise the same at any degree of parallelism.
- **Afocal image space** is refused (the help's afocal forms in diopters are not built).

**Not built.** Plot Scale; Use Polarization; the Field
Map's Surface setting (image surface only); OpticStudio's halving of arms for symmetric systems;
afocal units.

## 35. Local optimization

`OpenSim.Optics/Optimization` (OP-57) optimizes the project's system against its merit function
(§31). The help read is OpticStudio 2025 R2's Optimize! page, saved under
`plans/sources/optics/op-57-help/`, with Modifying the merit function from OP-54's folder.

- **Variables** (`LensVariables`). Surface cells flagged Variable, in surface order and, within a
  surface, flag order, then multi-configuration cells flagged Variable, row by row
  (`MultiConfiguration.Variables`). Kinds: curvature (a Radius cell, varied as 1/R in 1/lens
  unit, 0 written as a plane), thickness, conic, semi-diameter, TCE, parameter (any "Par n",
  parameters and extra data, in the column's unit) and configuration (a real-number table
  cell, in its operand's unit). Refused with the cell named: the Material cell itself (glass variables are
  §41), an automatic semi-diameter, an infinite thickness, a whole-number parameter, a text,
  whole-number or infinite table cell, any other column.
- **Evaluation.** Each trial design writes the variables into a copy of the optics record and
  builds every configuration through `ProjectMeritConfigurations` (the multi-configuration
  table, then solves and pickups by `ProjectLens`), so solves and pickups follow the variables
  inside every residual. The residuals are the merit function's (§31): Σ r² = MF².
- **Damped least squares** is the kernel `Core/Numerics/Optimization/DampedLeastSquares`
  (Levenberg–Marquardt through the SVD, Moré's column scaling, finite differences forward then
  central), unchanged except an accepted-step callback (`OnAcceptedStep`) added for progress
  and best-so-far.
- **Orthogonal Descent** is `Core/Numerics/Optimization/OrthogonalDescent` (new): trial steps
  along orthonormal directions, a success growing that direction's step by 3 and a failure
  reversing it and halving it, the directions re-orthonormalised (Gram–Schmidt on the summed
  progress, Rosenbrock 1960) once each has had a success and a failure. First steps 5 % of
  |value| (5 % of the scale for a zero value); it stops when every step is below 1e-9 of its
  first size. The factors are this program's choice.
- **Scaling and increments** per kind (`LensVariableKindSettings`): the algorithms work on
  value / Scale (default 1); a derivative increment, in the variable's unit, replaces the
  kernel's relative finite-difference step (a kind without one then takes √ε·max(|x₀|, 1),
  fixed at the start of each run).
- **Cycles.** Automatic: the algorithm's own stopping tests (DLS at most 1000 iterations).
  Fixed: that many cycles in all. Infinite: restarted from the best design whenever it stops,
  until cancelled.
- **Constraints.** Bound operands are weighted rows (§31). A row with a negative weight is an
  exact constraint: with cᵢ its residual, rows √ρ(cᵢ + λᵢ/2ρ) are appended; after each run
  λᵢ += 2ρcᵢ, and ρ (from 1) grows tenfold when ‖c‖ fell by less than a factor 4; the row is met
  when |V − T| ≤ 1e-9 × max(1, |T|), otherwise the run ends after 30 rounds as "constraints not
  met". The merit value reported is §31's, with absolute weights.
- **Start and failures.** A starting design with a failed row is refused with every failure
  named (help: "If any of the operands cannot be computed, the optimization cannot begin");
  later designs that fail get §31's penalty and the algorithm steps away.
- **Cancellation** returns the last accepted design, which without negative-weight rows is the
  best so far (every accepted step lowers the merit).
- **Determinism.** The finite-difference columns and the ray traces run in parallel slots; the
  result is bitwise the same at any thread count. OD evaluates sequentially.
- **Apply and undo** (`LocalOptimizationResult.ApplyTo`, `UndoOn`) copy only the variable cells
  (the saved radius itself, not 1/(1/R)) into the current record, refusing if its variables are
  no longer the run's.
- **App.** The Optimize window (algorithm, cycles, threads, Start/Stop, merit before and now,
  log) applies each run's result through the Lens Data Editor as one undo step, and the
  multi-configuration table by reloading its editor.

**Gate** (`Gates/LocalOptimizationGateTests`): a perturbed doublet returns to the optimum the
test builds by optimizing it once; a 0.2 mm singlet with an F/# solve bends to the Coddington
shape factor 2(N² − 1)/(N + 2), derived in the test from the Seidel sum, and agrees with an
independent Brent search; results are bitwise identical at 1, 4 and unlimited threads;
cancellation keeps the best design so far.

Glass variables and substitution are in §41 (OP-59), global optimization in §43 (OP-58).
**Not built.** Analytic
or automatic derivatives (finite differences only; the kernel accepts an analytic Jacobian);
OpticStudio's own Orthogonal Descent (unpublished); the window's per-kind scale and increment
settings and saving its settings; Auto Update of analysis windows during a run.

## 36. Tolerance data editor and tolerance operands

`OpenSim.Optics/Tolerancing` (OP-63) holds the tolerance table and the rules that perturb a copy
of the saved lens by one row at a given value. The operands were read from OpticStudio's 2025 R2
help, saved under `plans/sources/optics/op-63-help/` (Tolerance Operands, the two summary tables,
each operand's page, Tolerance Control Operands and their pages, Tolerancing with the Irregular
Surface Type, Nesting Rules for Monte Carlo Analysis, Tolerancing with Solves; the equation
images under `images/`). Sensitivity and inverse sensitivity with compensators use the rules (OP-64, §42), and so does
Monte Carlo (OP-65, §45).

- **Rows.** A row is a four-letter code, Int1 to Int3, Min, Max, a comment, and OpticStudio's
  two operand properties (Ignore; Do not adjust during inverse tolerancing). Min and Max are the
  extreme changes from nominal in the operand's unit. The control rows act on the rows below
  them: TWAV sets the test wavelength (its Min, micrometers; none means the primary wavelength),
  MPVT the element pivot (none means the front vertex), STAT and SEED the Monte Carlo
  statistics. COMP, CPAR, CEDV and CMCO define compensators (`ToleranceTable.Compensators`).
- **Direct values.** TRAD adds to the radius (Code 0, lens units) or scales it by 1 + v/100
  (Code 1); TCUR adds to the curvature; TFRN adds ΔC = 2ΔZ/r², ΔZ = λN/2, r the clear
  semi-diameter; TTHI adds to the thickness and takes the same amount from the adjustment
  surface unless Int2 = Int1; TCON adds to the conic; TSDI fixes the semi-diameter at its value
  (or the nominal lens's automatic one) plus v; TPAR adds to a parameter; TPAI sets
  P′ = 1/(1/P + Δ); TUDX … TUTZ add to the values of an existing coordinate break; TMCO adds to
  a multi-configuration cell. A cell set by a solve, a pickup or a multi-configuration row is
  refused by name, since the change would be overwritten (OpticStudio leaves solves in place,
  "How OpticStudio Computes the Tolerance Analysis").
- **Index and Abbe.** A constant index takes TIND as an offset (TABB is ignored, as the help
  says for glass that is neither a model nor a catalog glass); a model glass changes nd or Vd;
  a catalog glass, when every wavelength lies in 0.3–2.5 µm, takes a glass offset solve
  (ΔNd, ΔVd) on its material. Outside that band the help's constant offset is refused (the
  saved lens cannot hold it).
- **The Irregular surface.** TSDX, TSDY, TSDR, TSTX, TSTY, TIRX, TIRY and TIRR turn a Standard
  surface into the Irregular surface (§23) and add to its decenter, tilt or terms; a surface
  already Irregular takes the sum. TIR: θ = atan((TIR/2)/S), S the clear semi-diameter, about y
  for TIRX and x for TIRY. TIRR: half spherical, half astigmatic, Zs = Za; a surface with an
  automatic semi-diameter gets the nominal one fixed, since the Irregular terms are scaled by it.
- **TIRR's fringes** (OQ-10). The help's formula ΔZ = (λt/2)(W/2·ρ⁴ + W/2·ρ′y²) makes the raw
  peak-to-valley of the sag change W double-pass fringes, Zs = Za = λtW/4 (the setting
  "OpticStudio"). The default ("PowerRemoved") scales the same shape so its peak-to-valley after
  removing piston, tilt and power by least squares over the aperture is W·λt/2, the
  irregularity a drawing or a test-plate count means ("Per ISO-10110, power and tilt should be
  removed", help page ISOB). That peak-to-valley is 17/32 of the raw one (derived in
  `IrregularityModels`, checked on a grid), so the default's terms are 32/17 of OpticStudio's.
  No secondary source disputing the help's formula was available to read; the help page is the
  source of both settings.
- **Zernike irregularity.** TEZI on a Toroidal surface sets its own Zernike Standard terms Min
  term to Max term to rms/√n each (their root sum of squares is the RMS, the terms being
  orthonormal), and the normalization radius to the clear semi-diameter. TEZI on other surfaces,
  TEXI (Zernike Fringe Sag) and ISOA to ISOD (Zernike Standard Sag composite add-ons) are
  defined and refused by name: those surfaces are OP-25 and OP-32. TEXI's coefficient
  c = (λ/2)f/√n and ISOA's A4 = Max/(2√3), A1 = √3·A4 are in `IrregularityModels`.
- **Element tolerances.** TEDX, TEDY, TEDR, TETX, TETY, TETZ, TARX, TARY, TARR, TRLX, TRLY and
  TRLR wrap the group First–Last: before it a zero coordinate break (the return target), a break
  to the pivot P (decenter x, y; thickness z) when P ≠ 0, the perturbing break(s), a break back
  from P; after it a break that returns to the target (orientation and XYZ, tilt then decenter)
  and one break per group surface that repeats the group's steps (thickness picked up from the
  surface, a break's values picked up from the break, a surface's own tilts copied). So the
  group turns as one about P and every surface after it stays where it was, whatever solves and
  compensators make of the group's thicknesses. A roll turns about the roll surface's center of
  curvature; a roll TIR is turned into the angle whose TIR it is (Brent's method). A group
  holding a coordinate return is refused. The pivot and the roll read the saved radii, conics
  and thicknesses of the group; one of those set by a solve, a pickup or a multi-configuration
  row is refused, since the trace would use another value.
- **Renumbering.** Inserted surfaces renumber the pickup sources, the surface arguments of
  solves, tilt pick-ups and coordinate returns, the surface arguments of multi-configuration
  rows (and the values of CRSR and STPS), and the Surf arguments of merit rows. The tolerance
  table is not renumbered: each change maps its numbers (`ToleranceChange.MapSurface`), so rows
  applied one after another (the Monte Carlo case, nested element groups included) find their
  surfaces.
- **Removal.** `ToleranceChange.Remove` deletes the inserted surfaces, renumbers back and
  restores the values it changed from the copies it kept: the record comes back bitwise.
- **Validation.** Each row's integers and limits against its page (surface ranges, codes, term
  ranges, Min ≤ Max, non-negative radial limits, Min = −Max where the help sets it) and the
  element rows against the nesting rules: ranges must be nested or apart, and the outermost of a
  nest comes first; a range sharing a limit with an earlier one counts as nested.
- **Readings** where the help leaves a gap are listed in §10, item 32.

**Not built.** Applying coating operands (the per-surface coating multipliers and offsets are
not saved), non-sequential operands (OP-70), Zernike Fringe and Standard Sag surfaces (OP-25)
and composite add-ons (OP-32) with the operands that need them, cylindrical roll surfaces, the
default tolerances wizard, the tolerance listing, sorting and .TOL files (the sensitivity and Monte Carlo analyses and
compensator optimization are §42 and §45).

## 37. Thermal analysis

OP-91 adds OpticStudio's thermal configurations: the thermal pickup solve of the
Multi-Configuration Editor and the Make Thermal tool. Sources: OpticStudio 2025 R2 help, System
Explorer › Environment and its pages ("Thermal Analysis of Optical Systems", "Defining Multiple
Temperature and Pressure Values", "Defining Which Parameters Consider Thermal Effects",
"Radius of Curvature (CRVT) Values", "Thickness (THIC) Values", "Thermal Parameter Values",
"All Other Values", "Adding TCE data", "Modeling Gases and Liquids", "Index of Refraction
Computation", "Limitations of Thermal Analysis", "Edge Thickness", "TCE"), and the
Multi-Configuration pages "Make Thermal" and "Solve Types"; copies in
`plans/sources/optics/op-91-help/` and `op-30-help/` (not in the repository). Code:
`OpenSim.Optics/MultiConfig/LensThermalModel.cs` (the expansion rules),
`MultiConfiguration.cs` (evaluation), `MultiConfigTools.MakeThermal`.

- **Expansion.** A length L at a temperature ΔT from its reference becomes L(1 + αΔT)
  ("Defining Multiple Temperature and Pressure Values"); a curvature c becomes c/(1 + αΔT).
  TCEs, in the catalogs' ED line and in the Lens Data Editor's TCE column, are in 10⁻⁶/K
  (help: TCE; ZemaxGlass manual). The catalog value is the −30…+70 °C one, used at any
  temperature, as OpticStudio does (help: TCE).
- **Which α.** Radii, and here also clear and mechanical semi-diameters: the surface's catalog
  glass; for an air or MIRROR surface the previous surface's catalog glass, else its own TCE
  column; for any other medium (constant index, model glass) the TCE column ("Radius of
  Curvature (CRVT) Values", "Adding TCE data"). A glass whose catalog sets "ignore thermal
  expansion" is not a solid and uses the TCE column ("Modeling Gases and Liquids").
  Thicknesses: a solid catalog glass scales with its TCE; any other space is a mount.
- **The mount.** The space after surface n is a spacer of length E = T − Z_n(h_n) + Z_{n+1}(h_{n+1})
  (help: Edge Thickness), the sags Z taken at each surface's mechanical semi-diameter h of the
  reference configuration (fixed values as entered, automatic ones sized by rays, section 13).
  It expands with surface n's TCE column α_m: E' = E(1 + α_mΔT). Each bounding surface j grows
  by s_j = 1 + α_jΔT (the radius rule), so its sag becomes s_j·Z_j(r/s_j); the contact moves
  to h'_j = min(h_j(1 + α_mΔT), h_j s_j), never past the lens's expanded edge (the help's two
  cases). T' = E' + Z'_n(h'_n) − Z'_{n+1}(h'_{n+1}). With every α equal this is T(1 + αΔT);
  with α_m = 0 the thickness still changes when the radii do, as the help says.
- **Glass and material (OQ-12).** `OpticsSettings.Thermal` (`OpticsThermalSettings`) may link a
  glass ("N-BK7" or "CATALOG:N-BK7") to a mechanical `Material`, stored whole; its
  `ThermalExpansionCoefficient` (1/K, a new nullable field of the material record) then
  replaces the catalog TCE everywhere that glass's TCE is read. A linked material without a
  coefficient, or an unlinked glass without a catalog TCE, fails by name. No built-in material
  has a coefficient yet.
- **The thermal pickup.** A cell of configuration k with reference configuration c (1 ≤ c < k)
  takes the value of its row in c, changed for ΔT = T_k − T_c, where T is the configuration's
  TEMP value (the system temperature without a TEMP row). CRVT, THIC, SDIA and MCSD expand as
  above, read on configuration c's lens; every other operand copies c's value ("All Other
  Values"); PRAM and PAR1–8 fail (scaling by the parameter's units, "Thermal Parameter Values",
  needs units the surface types do not declare). ΔT = 0 returns c's value exactly. Thermal
  pickups are evaluated after the configuration's other cells, so a pickup in the same
  configuration cannot read one. The glasses come from the active catalogs, which
  `SettingsFor`, `ValuesIn`, `SolveFor`, `LensFor` and `ForCurrentConfiguration` take; without
  them a length's thermal pickup at a changed temperature fails by name.
- **Indices.** Each configuration's TEMP and PRES set the system environment; with "Adjust index
  to environment" on, the glass indices follow section 2's temperature and pressure model (OP-13, `ThermalIndex`). Make Thermal
  says so when the setting is off.
- **Make Thermal.** "Delete existing configuration data": one nominal configuration (the
  current one, at its temperature and pressure) and N more at temperatures from the minimum to
  the maximum in equal steps (N = 1: the minimum). "Use existing n configurations as nominal":
  n + n·N configurations, nominal c's N following the originals in order; existing rows' new
  cells pick up nominal c (a thermal pickup for CRVT, THIC, SDIA, MCSD), TEMP takes the new
  temperatures. Rows TEMP and PRES (when missing) and CRVT, THIC, SDIA, MCSD for surfaces 1 to
  the one before the image are added where the table lacks them and the value is fixed
  (planes, infinite thicknesses and automatic semi-diameters have none; a Lens Data Editor
  solve or pickup is kept and the notes name it), sorted by operand or by surface. The pressure
  of the new configurations is the nominal's (help) unless given. The table is limited to 160
  configurations.
- **Not built.** Thermal pickups of parameters and extra data; several environments in one
  configuration (several TEMP or PRES rows); thermal pickups of glasses beyond a copy; TCE and
  thermal merit operands (OP-55) and athermalisation by optimization (OP-57); the merit function
  over a thermal table needs the catalogs passed to `ProjectMeritConfigurations`'s `SettingsFor`
  (a one-line change in `OpenSim.Optics/Merit/MeritConfigurations.cs`, not made here), until
  then its thermal configurations at a changed temperature fail by name; the prescription
  report's thermal listing; coupling to OpenSim's own thermal and structural solvers (OP-92).

## 38. Modulation transfer function

`OpenSim.Optics/Analysis/Mtf` (OP-39): `MtfEngine.cs` (public: `MtfPupil`, `MtfEngine`,
`MtfValue`, `MtfOptions`, `GeometricRay`, `GeometricSpot`), `MtfCore.cs`, `MtfRuns.cs`,
`MtfAnalyses.cs` and `ContrastLossMapAnalysis.cs`. Tests: `OpenSim.Tests/Optics/MtfAnalysisTests.cs`
and the gate `OpenSim.Tests/Optics/Gates/MtfGateTests.cs`. OpticStudio's behaviour is read from
its 2025 R2 help ("MTF", the thirteen FFT, Huygens and geometric MTF pages, "MTF Data",
"ZemaxMTFUnits MTFUnits", "Contrast Loss Map (MTF)", with the square-wave equation image), saved
under `plans/sources/optics/op-39-help/`; "Contrast Loss Map", "Optimizing for MTF" and
"Diffraction Limited" from earlier items' folders. Goodman, Hopkins' defocus series and O'Neill's
annular closed form were not on hand; the closed forms are derived in the gate's comments.

- **OTF.** OTF(ν) = ∫ I(x)·e^{−2πiνx} dx / ∫ I dx, the PSF's x as section 33 defines it.
  Modulation |OTF|; real and imaginary parts; phase in degrees, its origin the chief ray.
  Tangential: frequency along y; sagittal: along x. For the FFT engine these are pupil axes
  (help, "FFT MTF"); for the Huygens and geometric engines the image plane's (help).
- **FFT engine** (`MtfPupil`). The pupil function of section 33 (`PsfEngine.BuildPupil`, the same
  n × n nodes, OPD reference and fitted pupil scale a). OTF(ν) = Σ f(P)·f*(P + ν/c) / Σ |f|²,
  c = n·a/λ (derived in `MtfPupil`), computed at every whole-node shift by the FFT of the pupil
  zero-padded to 2n (|F|² transformed back; the circular correlation is the linear one) and
  interpolated linearly in real and imaginary parts between shifts. A shift of one node is the
  frequency c·ΔP; the cutoff of each axis is 2c = 2n|a|/λ = 1/(λF) (help: the working F/# "for
  the sagittal and tangential response separately"). At whole-node shifts the OTF equals the
  transform of OP-38's FFT PSF exactly. The sampling warnings are section 33's.
- **Afocal** (`MtfEngine.BuildAfocal`). The OPD against the afocal reference plane (section 26),
  the beam's half-width b per unit pupil coordinate fitted to where the rays cross that plane;
  c = n·b/λ cycles per radian, cutoff 2n·b/λ, the exit pupil diameter over λ in air (help).
  Shown in cycles per milliradian, the help's default afocal unit; other afocal angle units are
  not built. Huygens and through-focus MTF refuse afocal image space (diopters not built).
- **Huygens engine.** OP-38's Huygens PSF (pupil sampling, image sampling, image delta; the
  primary wavelength's chief ray as centre when polychromatic) transformed directly at each
  frequency, Σ I·e^{−2πiν·y} / Σ I. Frequencies above the grid's Nyquist 1/(2Δ) and a PSF whose
  edge samples exceed 10⁻³ of its peak are warned about. Cutoffs from the fitted pupil scale.
- **Geometric engine** (`MtfEngine.BuildGeometric`, `GeometricOtf`). The same n × n pupil nodes
  traced to the image surface (OPD limitations allowed; only rays are used), each ray weighted by
  its apodization squared; OTF = Σ w·e^{−2πiν·y} / Σ w over the rays' image-surface coordinates
  relative to the chief ray (afocal: direction cosines read as radians), the exact transform of
  the rays' line spread function. *Multiply by diffraction limit* (default on) multiplies each
  wavelength's OTF by the modulation of the same field's unaberrated pupil (the same amplitudes,
  zero OPD, the scale fitted to the same rays).
- **Polychromatic.** FFT and Huygens: every wavelength's OPD against the primary wavelength's
  chief ray (lateral colour is phase); FFT OTFs weighted by w·(λ_min/λ)²·Σ A²/((Σ A)²|step_x·
  step_y|), the energy of that wavelength's term in section 33's polychromatic PSF, so the sum is
  that PSF's transform; Huygens transforms section 33's combined PSF. Geometric: weighted by w,
  every wavelength's rays measured from the first wavelength's chief ray.
- **Square wave** (help, "FFT MTF", equation image): S(ν) = (4/π)[M(ν) − M(3ν)/3 + M(5ν)/5 − …]
  over the odd harmonics below the cutoff, M the modulation; S(0) = 1. Derived from the square
  wave's Fourier series (Coltman's series); FFT only, as the help offers it.
- **Diffraction limit** (help, "Diffraction Limited"). The FFT MTF of the reference field — the
  first field at (0, 0), else field 1 — with the OPD set to zero (apodization, vignetting, F/#s
  kept), drawn dashed; on modulation and square-wave plots only.
- **Analyses.** Against frequency: 101 frequencies from 0 to the max frequency (0: the largest
  cutoff), T and S per field (0: every field). Through focus: 2 to 100 planes from −Δ to +Δ (lens
  units, added to the thickness before the image surface, as section 34). Surface: 129 × 129
  frequencies over ±the largest cutoff (FFT bilinear on the shift plane). Vs field: up to six
  frequencies, 2 to 100 fields along +y, +x, −y or −x to the largest defined field in that
  direction. Map: x × y pixels (2 to 101), each computed at its centre, total widths in field
  units (0: twice the largest radial field), centred on a field row or (0, 0); data T, S,
  average, minimum, maximum (geometric: T, S, average). Fields between the table's are appended
  to the field table (section 34's rule); Remove vignetting factors (default on, as the help)
  replaces them with apertures first. A point that fails is blank and its reason listed.
- **Contrast loss map** (help, "Contrast Loss Map"). For each node of an n × n pupil grid, the
  pair (Px ± δ_x/2, Py) (sagittal) and (Px, Py ± δ_y/2) (tangential) with δ = λν/(n|a|) on each
  axis, and loss 0.5(1 − cos 2π(W₁ − W₂)); blank where a ray of the pair is outside the unit
  circle or has no OPD. Frequency 0 is 5 % of 2n√|a_x·a_y|/λ. The MTF and diffraction-limited MTF
  at the frequency are the FFT engine's at 128 × 128 (help), or "Cannot compute MTF" when that
  pupil is aliased.
- **Units.** Settings frequencies are in cycles per millimeter (afocal: per milliradian); the
  engine works per meter (per radian), × 1000. A focal system whose MTF unit is cycles per
  milliradian is refused (object-space frequency not built).
- **One point for criteria.** `MtfEngine.Evaluate(lens, system, field, wavelengths, frequency,
  options)` returns the tangential and sagittal modulation (or square wave) of one field row and
  wavelength set at one frequency by any engine, on the lens and system given (a configuration's
  own). OP-64's MTF tolerance criteria (§42) and the MTF merit operands MTFT, MTFS, MTFA, GMTT,
  GMTS and GMTA (§40, `Merit/MtfOperands.cs`) call it.
- **Determinism.** Through-focus planes, vs-field points and map pixels are one `ParallelSlots`
  slot each, every FFT on one thread, so results are bitwise the same at any degree of
  parallelism.

**Not built.** Polarization; intermediate surfaces (the Surface setting); scattered rays; Use
Dashes; the Huygens sum over configurations; the coherent transfer function; the 3-D surface plot
with its Rotation and Scale; the through-focus spline and 101-point listing; the MTF operands'
sparse methods (Grid = 0; the grid method is used), their Data Type 1–3 and Field 0, the Huygens
and square-wave operands (MTHx, MSWx) and the N and X (minimum, maximum) forms; object-space cycles per milliradian for focal systems; afocal angle
units other than milliradians; afocal Huygens and through-focus MTF; the contrast loss map's
circles and Show OPD indicator; O'Neill's annular closed form and Hopkins' series as gates.

## 39. Enclosed energy

OP-41. Code: `OpenSim.Optics/Analysis/EnclosedEnergy/` (public: `EnclosedEnergyType`,
`EnclosedEnergyReference`, `AiryEnclosedEnergy`, `EnclosedEnergyRegions`,
`DiffractionEnclosedEnergyKernel`, `DiffractionEnclosedEnergySetup`, `DiffractionEnclosedEnergy`,
`GeometricEnclosedEnergySpot`, `GeometricEnclosedEnergy`, `ImaImage`, `ImaFile`,
`ExtendedSourceRequest`, `ExtendedSourceEnclosedEnergy`; the four analyses in
`EnclosedEnergyAnalyses.cs`). Tests: `OpenSim.Tests/Optics/EnclosedEnergyTests.cs` and the gate
`OpenSim.Tests/Optics/Gates/EnclosedEnergyGateTests.cs`. OpticStudio's behaviour is read from its
2025 R2 help ("Enclosed Energy", "Diffraction (enclosed energy)", "Geometric", "Geometric
Line/Edge Spread", "Extended Source", "Geometric Image Analysis"), saved under
`plans/sources/optics/op-41-help/`.

- **Regions.** Encircled: a circle of radius r about the reference. X-only: the slit
  |x − x_ref| ≤ h, infinite in y; Y-only the same turned (help example: an x-value of 10 µm is
  "the region 20 microns across in the x-direction ... infinite in the other direction").
  Ensquared: the square of half-width h. Each curve runs from size 0 to the maximum distance in
  100 steps; the result lists the sizes holding 50 % and 80 %.
- **Reference.** Chief ray (the selected wavelength's, the primary's when several), centroid,
  vertex (the surface's local (0, 0)); the geometric enclosed energy also offers the middle,
  "the coordinate upon which the smallest circle enclosing all the ray intercepts is centered",
  found by the incremental smallest-circle algorithm over a fixed-seed shuffle.
- **Diffraction enclosed energy.** From OP-38's pupil function (section 33): the PSF of the
  sampled pupil is I(x) = Σ_m C_m e^{−i k_m·x} with C the pupil's autocorrelation (by FFT of the
  pupil zero-padded to 2n) and k_m = 2π·n·ΔP·(a_x m_x, a_y m_y)/λ. The energy in a region centred
  on x₀ is Σ C_m e^{−i k_m·x₀}·D(k_m), D the region's Fourier transform (2πr·J₁(|k|r)/|k| for the
  circle, (2 sin(k_x h)/k_x)(2 sin(k_y h)/k_y) for the square, the m_y = 0 terms with
  2 sin(k_x h)/k_x for a slit over one period), over one period's energy C_0·T_x·T_y. This is
  exact for the sampled pupil: no image grid enters. Sizes are limited to half the period
  T = λ/(n·|a|·ΔP), where a region would overlap the PSF's periodic copies (refused with the
  limit named). The reference point is projected along each wavelength's chief ray onto its PSF
  plane; the centroid is the rays' mean image-surface intercept weighted by A² (and each
  wavelength by its share). A reference more than a quarter period from the chief ray is warned
  about (the help: with the vertex OpticStudio "is unable to detect if the sampling is
  sufficient"). OP-38's sampling warnings are repeated. Polychromatic: every wavelength
  referenced to the primary's chief ray, each fraction weighted by the wavelength's weight
  times its pupil transmission. *Show diffraction limit*: the reference field's pupil (the (0, 0)
  field, else field 1; help, "Diffraction Limited") with the OPD set to zero, about its chief ray.
- **Geometric enclosed energy.** Rays on an n × n grid of pupil nodes inside the unit circle,
  traced to the chosen surface; each ray that passes weighs its wavelength's weight (1 for one
  wavelength) times the apodization's intensity A(ρ)². The fraction at a size is the weight of
  the rays at a distance at most that size over the weight of every ray that passed; failed and
  vignetted rays carry no light and are counted by status and surface. *Multiply by diffraction
  limit* (encircled, image surface): the fraction times the Airy disk's E(v) = 1 − J₀(v)² − J₁(v)²,
  v = π·r/(λF), F the real working F/# (OP-34's) at the reference field.
- **Airy enclosed energy.** E(v) = ∫₀^v 2J₁(t)²/t dt = 1 − J₀(v)² − J₁(v)², derived in
  `AiryEnclosedEnergy` from the Airy intensity (2J₁(v)/v)² and J₀′ = −J₁, J₁′ = J₀ − J₁/v.
- **Geometric line/edge spread.** The same rays about the chief ray. X orientation (a line or
  edge parallel to x) runs along y, Y along x. The line spread is the weight in each of 100 bins
  across ±the maximum radius over the largest bin's; the edge spread at a bin centre is the
  weight at or below it over the total. The diffraction line/edge spread is OP-38's FFT
  Line/Edge Spread.
- **Extended source.** A text IMA file (size, then rows of digits 0–9) or a binary one (int16
  0, width 1–8000, number of maps; then bytes, column fastest), row 1 the top. One map serves
  every wavelength; a binary file may give one per wavelength. Pixel (r, c) of an n × n image
  spans the field (f_x + ((c + u)/n − ½)·S, f_y + (½ − (r + v)/n)·S), S the field size in field
  units (degrees, or lens units for heights). About 1000 × "Rays x 1000" rays are shared by
  pixel value × wavelength weight, at least one per lit pixel and wavelength; each weighs its
  pixel's value × weight over the pixel's count, times A(ρ)². Positions in the pixel and pupil
  come from a 4-D Sobol sequence (ρ = √s, θ = 2πt). Field points are normalised against the
  defined fields plus the image's corners; vignetting factors are refused unless removed
  (replaced by apertures, as OP-40). Types: the four regions, and x or y distributions (the
  weight in 100 strips across ±the maximum distance) with the geometric FWHM between the
  half-maximum crossings.
- **Criterion form.** `DiffractionEnclosedEnergy.Fraction` and `GeometricEnclosedEnergy.Fraction`
  return the fraction at one size for one field of one configuration's lens and system; OP-64's
  encircled-energy tolerance criteria (§42) call them. The encircled-energy merit operands (DENC,
  DENF, GENC, GENF, XENC, XENF) are not built.
- **Determinism.** The kernel's FFT and each size's sum are bitwise the same at any degree of
  parallelism; the extended source runs one slot per wavelength and pixel row with a fixed
  Sobol index each.

Not built: the Huygens PSF option of the diffraction enclosed energy, polarization, scattering,
intermediate surfaces for the diffraction enclosed energy, BIM files, the Geometric Image
Analysis's own options (parity, rotation, Lambertian source, pixel interpolation), dashed-line
styling.

## 40. Default merit function wizard

`OpenSim.Optics/Merit/DefaultMeritWizard.cs` (OP-56) writes OpticStudio's default merit
function; `OpenSim.Optics/Merit/RayAberrationOperands.cs` and `MtfOperands.cs` hold the operands
it writes. The help
read is OpticStudio 2025 R2's Optimization Wizard and its pages (Optimization Function Types,
Criteria, Reference Points, Distortion and Color, Pupil Integration Settings, Optimization Goal,
Boundary Values, Other Settings, Button Functions; saved under
`plans/sources/optics/op-56-help/`), "Gaussian Quadrature" and "Rectangular Array" (OP-40's
folder) and the alphabetical operand list (OP-54's folder).

- **Rows.** For each configuration chosen (the current one, or all; each after a CONF row when
  the system has more than one), each field and each wavelength of non-zero weight, and each
  pupil point of the rule: one row of the criterion's operand, target 0, weight
  W = overall × w_field × w_wavelength × w_pupil (× the relative X or Y weight). The pupil
  weights sum to 1, so
  MF² = Σ_f w_f Σ_λ w_λ Σ_i w_i e²(f, λ, i) / (Σ_f w_f · Σ_λ w_λ):
  - spot radius, spot x, spot y, wavefront: for one field and one wavelength MF is OP-40's RMS
    (§34) over the same rule. With one centroid per field over every wavelength,
    MF² = Σ_f w_f R_f² / Σ_f w_f, R_f OP-40's polychromatic RMS (exactly for the spot; for the
    wavefront each wavelength's residual is in its own waves, while §34's polychromatic RMS is in
    primary waves). With Ignore Lateral Color, MF² = Σ_f Σ_λ w_f w_λ R²(f, λ) / (Σ w_f · Σ w_λ),
    R(f, λ) the monochromatic RMS;
  - spot x + y and angular: x rows weighted by the relative X weight a, y rows by b, so
    MF² = (a·R_x² + b·R_y²)/(a + b), which is R/√2 for a = b. A zero relative weight writes no
    rows for that component; both zero write radial rows (TRAC or TRAR, ANAC or ANAR).
- **Contrast** (help, "Optimization Function Criteria": "Optimizes the MTF of the system using the
  Moore-Elliott Contrast method"; "Other Settings": Relative S and T Weight, Spatial Frequency;
  "Optimizing for MTF"). Per configuration, field and wavelength of non-zero weight and pupil
  point of the rule, a MECT row (weight W × relative T weight) and a MECS row (W × relative S
  weight), target 0, Data5 the spatial frequency (cycles/mm). A MEC row is the pupil phase
  difference OPD(P) − OPD(P − Δ) in waves (OPDC's OPD), Δ = 2ξ·λ·F/# pupil radii along y (T) or
  x (S), F/# the paraxial working F/# at the row's wavelength; MECA is their mean. A pair whose
  partner lies outside the pupil, or whose partner ray fails, is not written (the notes count
  them); no pair at all, a frequency at or past the cutoff, is refused; there is no symmetry
  halving. The reference setting does not apply. Afocal image space is refused (no working F/#).
  The readings are §10 item 36's.
- **MTF operands** (help, "MTF Data"). MTFT, MTFS, MTFA (OP-39's FFT engine) and GMTT, GMTS, GMTA
  (its geometric engine; !Scl 0 multiplies by the diffraction-limited MTF): field Field at Freq
  (MTF units), wavelength Wave (0: every wavelength, polychromatic), on a 32·2^(Samp−1) pupil grid
  (Samp 1 to 6), equal to `MtfEngine.Evaluate` on the configuration's lens; T and S are computed
  once per lens, system and settings and shared. The wizard does not write them.
- **Operands** (help: the alphabetical list). Centroid: TRAC (radial), TRCX, TRCY, ANAC, ANCX,
  ANCY (direction cosines l, m), OPDX (mean and tilt removed: the weighted least-squares plane
  a + b·Px + c·Py, OP-40's fit). Chief ray: TRAR, TRAX, TRAY, ANAR, ANAX, ANAY from the primary
  wavelength's chief ray, and OPDM (mean removed). Unreferenced: OPDC (the OPD against the sphere
  on the row's own wavelength's chief ray, nothing removed). Spot positions are on the image
  surface in its local frame, in lens units; the OPD is §26's, against the exit-pupil reference
  sphere on the chief ray, in waves of the row's wavelength.
- **Groups.** A centroid row's reference comes from its whole group: the rule named by its own
  columns (Int1 rings or grid, Data5 arms or 0 for the rectangular array, Data6 0 for every
  weighted wavelength of the field or 1 for its own wavelength alone). The group is traced whole
  once per lens, system and group and cached on the lens and system instances
  (`RayAberrationOperands`). The row's own ray is read from the group, or traced alone when its
  pupil point is not one of the rule's. Chief-ray rows trace their own ray through the
  evaluator's ray hook and read the chief ray from the cache. The reason: the evaluator's hook
  carries one ray per row and a row sees only its own columns (§31); `MeritEvaluator` is
  unchanged.
- **Tracing.** Spot and angular rays are traced as the evaluator traces a row's ray (automatic
  semi-diameters sized once). The OPD is `OpdEngine`'s, on the lens with its floating
  semi-diameters frozen; a field that is not a table row is appended with the launcher's
  vignetting factors, as §34 does. A group over every wavelength is referred to the primary
  wavelength's sphere.
- **Failures.** One failed ray in a Gaussian group fails every row of the group, naming the
  rays and asking for the rectangular array. A rectangular-array group leaves its failed rays out
  of the reference. A row whose own ray fails fails alone. A rule column out of range fails the
  row, naming the column.
- **Pupil integration.** Gaussian quadrature is OP-40's `PupilQuadrature.Gaussian`: rings 1 to
  20, arms 6, 8, 10 or 12 (the help's choices), exact for integrands of degree
  ≤ min(4n − 1, N − 1) (§34). So with 6 arms an RMS is exact only for errors of degree ≤ 2,
  whatever the rings. The obscuration is 0 (not offered). The rectangular array is OP-40's
  cell-centred grid, 2 to 100 across (this program's limit), equally weighted; Delete Vignetted
  leaves out the points whose ray fails when the rows are written.
- **Vignetting.** Before writing Gaussian rows the wizard traces every ray of the rule at every
  chosen configuration, field and wavelength. If any fails, it writes the rectangular array
  instead and its notes say where (help, "Gaussian Quadrature": GQ "assumes the pupil is a
  circle ... GQ should not be used"). Vignetting factors are applied by the launcher and do not
  count as vignetting.
- **Symmetry** (help, "Other Settings"). Automatic is on when
  `MeritEvaluator.IsRotationallySymmetric` holds. On, a field with Hx = 0 keeps the points with
  Px > 0 at twice their weight and those with Px = 0 (|Px| ≤ 10⁻¹²) at their own; the on-axis
  field keeps one point per ring (the first arm, at the whole ring's weight) for the radial
  criteria (wavefront, spot radius, radial x + y or angular). Fields with Hx ≠ 0 keep every
  point. The references still come from the whole rule. No USYM row is written.
- **Boundaries** (help, "Boundary Values"). MNCG, MXCG, MNEG (glass) and MNCA, MXCA, MNEA (air)
  over surfaces 1 to the one before the image, weight = the overall weight, with MNCT's rule
  (§31) over the surfaces whose following medium is glass or the system's air (§29's medium
  rule: a mirror or coordinate break carries the medium before it). The edge thickness is the
  axial distance to the next surface plus its sag minus this surface's at (0, a), a the
  mechanical semi-diameter (Mode 0; the clear one plus the chip zone when none is set) or the
  clear one (Mode 1), times Zone when Zone is not 0. A coordinate break or a tilted or decentered
  next surface fails the row.
- **Start At** (help, "Other Settings"; DMFS). The generated rows begin at row Start At: by
  default the row after the first DMFS row, else the end; 0 is the end. Rows after them that name
  rows by number (GOTO, SKIS, SKIN, and the Op# columns of math rows) are renumbered. When the
  generated rows contain CONF and existing rows follow, a CONF back to the configuration in
  effect at the start row is added after them. The first generated row is a BLNK whose comment
  says what was written; each field's rows follow a BLNK naming the field.
- **Settings** (help, "Button Functions"). Save Settings stores the options with the project
  (`OpticsSettings.MeritWizard`, an `OpticsMeritWizardSettings` with the choices by name); Load
  Settings and Reset Settings show the saved and the default options.

**Not built.** Contrast in afocal image space; MTHx, MSWx and the N and X forms of the MTF
operands; the PTV function type (the rows are RMS); Max
Distortion (DIMX); the optimization goal "Improve manufacturing yield" (HYLD); Add Favorite
Operands; the Gaussian quadrature obscuration; OpticStudio's arm angles; the other boundary
operands (MXEG, MXEA, XNEG and the rest); a copy of the boundary rows per configuration.

## 41. Glass substitution

`OpenSim.Optics/Optimization` and `Materials/Tools` (OP-59) let the optimizer change glasses. The
help read is OpticStudio 2025 R2's, saved under `plans/sources/optics/op-59-help/` (Optimizing
Using Model Glasses with its equation image, Glass Substitution Template (global optimizers
group), Description of Catalog Data, Constraints on Glass Data, Restricting Selected Glasses,
Using Glass Substitution, Global Optimization of Glass Selection) and
`plans/sources/optics/solves/Material_Solves.html`.

- **Saved flags** (`GlassVariables`). The Lens Data Editor's Material cell takes no Variable or
  Solve flag, so glass flags name columns of their own, kept by the editor with the row:
  `ModelNd`, `ModelVd`, `ModelDeltaPgF` (Variable, on a surface with a model glass) and
  `Substitute` (Variable, on a surface with a glass). The Optimize window's Glasses section sets
  them. No new surface field is saved.
- **Model-glass variables** (`LensVariables`, kinds `ModelIndex`, `ModelAbbe`,
  `ModelPartialDispersion`). The optimizer varies the model glass's nd, Vd or ΔPgF; the model is
  this program's stand-in (`ModelGlassFit`), not OpticStudio's formula. A trial with nd ≤ 1 or
  Vd ≤ 0 cannot be built and gets the failed-row penalty.
- **Distance** (`GlassDistance`). d = [Wn(ΔNd)² + Wa(ΔVd)² + Wp(ΔPgF)²]^½ with zero weights
  meaning 1, 1E-04 and 1E+02 (help: Optimizing Using Model Glasses). A glass's point is its
  printed nd, Vd and ΔPgF, computed from its dispersion where the catalog prints none (§10 item 37).
- **Template** (`OpticsSettings.GlassSubstitution`, `GlassSubstitutionTemplate`). Use template;
  exclude glasses with incomplete data; the Standard, Preferred, Obsolete and Special boxes;
  maximum relative cost, CR, FR, SR, AR and PR, each ignored when empty or zero. A datum the
  catalog does not record (absent, or −1) fails a test only when incomplete data are excluded.
  Substitution and conversion also always drop glasses flagged Exclude Substitution, tabulated
  materials, and glasses whose valid range misses a wavelength of the lens. The AGF catalogs
  record every datum the template reads (OD and NM lines); the refractiveindex.info seed records
  none, so its materials pass unless incomplete data are excluded. The template's `Catalogs`
  list (not OpticStudio's) restricts substitution to named catalogs; OpticStudio's per-surface
  substitute catalog is not saved.
- **Model to real glass** (`ModelGlassConversion`). The nearest candidate by the distance with
  Wp = 0 and the RGLA defaults for Wn and Wa. If no glass meets the template, incomplete data are
  allowed, then the template is ignored, as the help says; its last step (glass 1 of the catalog,
  whatever it is) is not built: the conversion fails naming the surface. The conversion does not
  re-optimize.
- **Substitute solve** (`SubstituteGlassSolve`). With the active catalogs given, the solve fails
  when no glass meets `SolveOptions.SubstitutionTemplate` (OpticStudio removes the solve
  instead); the glass is unchanged. The saved lens does not produce this solve (its substitution
  flag is the `Substitute` column above).
- **Search** (`GlassSubstitution`). Re-optimize the start with its own glasses; then, round by
  round, for each flagged surface try every candidate (written "catalog:glass"), each time from
  the best design and re-optimized with `LocalOptimizer`, and keep the best when it lowers the
  merit by more than 1e-9 relative. Rounds end when one changes nothing (at most three by
  default). A trial that fails is listed with its reason. Sequential, so as deterministic as the
  local optimizer; Stop keeps the best so far.
- **Operands** (`GlassOperands`, registered under `// OP-59`). RGLA (Surf1, Surf2, Wn, Wa, Wp):
  for each surface in the range with a glass, the distance to the nearest glass of the active
  catalogs not excluded from substitution; the value is the target while every glass is within
  it, else the target plus the excesses. GCOS (Surf): the catalog's relative cost of the glass
  after the surface; fails for a non-catalog medium or a cost the catalog does not record. The
  catalogs reach the operands through `IMeritConfigurations.Catalogs` (one added line in
  `MeritEvaluator`).
- **Not built.** Substitution inside a Global search (the Hammer search of §43 replaces
  Substitute glasses); a per-surface substitute catalog in the saved lens; the template's Save/Load
  of *.GST files and "Save As New Glass Catalog"; MNIN, MXIN, MNAB, MXAB, MNPD, MXPD, GTCE and
  INDX; the conversion's last-resort glass.

## 42. Sensitivity and inverse sensitivity

`OpenSim.Optics/Tolerancing/ToleranceAnalysis.cs`, `ToleranceCriterionEvaluator.cs`,
`ToleranceCompensators.cs` and `ToleranceRunOptions.cs` (OP-64) run OpticStudio's Sensitivity,
Inverse Limit and Inverse Increment modes and Quick Sensitivity on the tolerance table of §36. The
definitions are OpticStudio's 2025 R2 help, saved under `plans/sources/optics/op-63-help/`
("Tolerancing", the dialog) and `plans/sources/optics/op-64-help/` ("How OpticStudio Computes the
Tolerance Analysis", "Evaluating Compensators", "Sensitivity Analysis", "The RSS Estimated Change",
"Inverse Sensitivity Analysis", "Quick Sensitivity"; the equation images rendered under `images/`).
§10 item 38 lists the readings where the help is short.

- **Nominal.** The current configuration is toleranced. The compensators are evaluated on the
  nominal lens first (Optimize: the optimizer drives them; paraxial focus and none: the lens as
  entered), and its criterion is N.
- **Trial.** Each tolerance row (not ignored, not a control row) is applied alone to the
  compensated nominal at its Min, then at its Max, by OP-63's `TolerancePerturber`; the
  compensators are evaluated and the criterion P recorded. A row the perturber refuses, or a
  criterion that cannot be evaluated (a failed ray in the quadrature, a lens that cannot be
  built), records the reason for that trial; the others go on.
- **Change.** Linear Difference Δ = P − N; RSS Difference Δ = S(P − N)·√|P² − N²|, S(x) = +1 for
  x ≥ 0 and −1 otherwise (help, "Tolerancing", "Change").
- **Estimate.** C² = Σᵢ (Δᵢ,min² + Δᵢ,max²)/2; F = N + C (linear) or √(N² + C²) (RSS) (help, "The RSS
  Estimated Change"). Not given when any trial failed (the help: the statistics are then
  "usually meaningless"). The worst offenders list the trials by degradation, largest first.
- **Criteria** (help, "Tolerancing", Criterion tab). RMS spot radius, x and y (lens units) and RMS
  wavefront (waves of the primary wavelength): OP-40's RMS core (§34) over the Gaussian quadrature
  with Sampling rings and 2 × Sampling arms ("The number of arms is always twice the number of rays
  along each arm"), about the centroid ("OpticStudio always uses a centroid reference for
  tolerancing"), polychromatic over the system's wavelengths and weights, at the Y-Symmetric (±1,
  ±0.7, 0 of the largest field in y; centre weight 2), XY-Symmetric (plus ±0.7, ±1 in x; centre
  weight 4) or user-defined field points, combined as √(Σ w v² / Σ w). Merit function: the lens's
  own merit function over every configuration. Boresight error: the radial image coordinate of
  the on-axis chief ray at the primary wavelength over |EFL|, radians. Strehl ratio (the plan's
  addition): OP-38's ratio through the RMS core, weighted mean over the fields. Geometric and
  diffraction MTF, tangential, sagittal or average ("the average of the tangential and sagittal
  responses is used"): OP-39's `MtfEngine.Evaluate` at the MTF Frequency (cycles/mm), the FFT
  engine or the geometric engine times the diffraction-limited MTF, on the pupil grid (16 to
  1024), polychromatic, weighted mean over the fields. Diffraction and geometric encircled energy
  (the plan's addition): OP-41's fraction within the radius (µm) of the centroid on the pupil grid,
  weighted mean over the fields. Each field point is appended to the system as its own field, as
  the RMS core does. The MTF and the encircled energy are larger-is-better, like the Strehl ratio,
  and take no part in Quick Sensitivity. A criterion is added with one enum value, one arm in
  `ToleranceCriteria.Info` and one in the evaluator.
- **Compensators** (help, "Evaluating Compensators"). None. Paraxial focus: each perturbed lens is
  moved at the image so that its defocus (the thickness before the image minus the paraxial image
  distance −y/u of the marginal ray) equals the nominal's, in every configuration the criterion
  reads; COMP and CPAR rows are ignored. Optimize (DLS: one Orthogonal Descent cycle, then damped
  least squares; OD: Orthogonal Descent only): every variable flag is removed, the COMP
  (thickness, curvature, conic), CPAR and CMCO cells (all, or the rows chosen) are flagged
  Variable, and OP-57's `LocalOptimizer` (§35) minimises the criterion's rows (weight the field
  weight, target 0, or 1 for a larger-is-better criterion) plus a soft bound row per COMP (nominal + Min to
  nominal + Max, weight 1); with the merit function criterion, the lens's merit function without
  bounds (help). A compensator cell that carries a solve or pickup is refused. The trial reports
  each compensator's change from the compensated nominal.
- **Inverse** (help, "Inverse Sensitivity Analysis"). The target T is the Increment (positive), or
  the change from N to the Limit (worse than N). For each side, when the degradation at the
  starting limit L is at most T, or the row is marked Do Not Adjust, L is kept ("the tolerance
  will never be loosened"); otherwise Brent's method (`RootFinding.Brent`) finds t in (0, 1) where
  the degradation at t·L is T, to `InverseTolerance` (default 10⁻⁷) of L, and the trial is
  evaluated again at the new limit. The estimate uses the adjusted limits; the result carries the
  table with them, which the editor writes back on request.
- **Quick Sensitivity** (help: "a sensitivity-only analysis with Paraxial Focus compensation"):
  Sensitivity with the paraxial focus compensator, for the spot, wavefront and boresight criteria.
- **Determinism.** Rows run in parallel, one slot each; everything inside a slot (perturbation,
  optimizer, criterion) runs on one thread, so the result is bitwise the same at any thread count.
- **Lenses the ray launcher refuses.** The launcher needs an axial paraxial model (§15): element
  decenters and tilts (TEDX … TETZ, the rolls), which wrap the group in coordinate breaks, make
  the lens non-axial, so their trials fail by name until the launcher handles such lenses (OP-22).
- **Settings.** The MTF Frequency (`MtfFrequency`) and the encircled radius
  (`EncircledRadiusMicrometers`) are saved on `OpticsToleranceRunSettings` with the rest; neither
  has a default (0 is refused for its criteria).
- **Not built.** Ensquared, x-only and y-only enclosed energy as criteria; MTF warnings (aliasing)
  are not reported per trial; RMS angular radial, x and y, the user script (OP-67); Skip Sensitivity (Monte Carlo runs on its own, §45);
  Separate Fields/Configs; the polynomial fit and the cache; Force Ray Aiming; Config "All"; the
  .ZTD file, the Tolerance Data Viewer and compensator statistics (OP-66); CPAR and CMCO bounds;
  CEDV (deprecated) compensators.

## 43. Global optimization

`OpenSim.Optics/Optimization/Global/` (OP-58) searches for better design forms than the local
optimizer reaches. The help read is OpticStudio 2025 R2's, saved under
`plans/sources/optics/op-58-help/` (Global Optimizer, Hammer Optimizer, Global Optimization,
Global Optimization Capabilites of OpticStudio, The Global Optimization Algorithm, The Hammer
Optimization algorithm, Suggestions for Using Global Optimizers). OpticStudio's two algorithms
are unpublished; the algorithms below are this program's own, chosen from published methods,
and are not OpticStudio's.

- **Global search** (`GlobalSearch`). Differential evolution DE/rand/1/bin (R. Storn and K. Price,
  "Differential Evolution – A Simple and Efficient Heuristic for Global Optimization over
  Continuous Spaces", J. Global Optim. 11, 341–359, 1997, eqs. 1–4; copy under
  `op-58-help/storn-price-de.pdf`) over the variables of `LensVariables`. Population NP (default
  max(8, 2D), at least 4); the first population uniform in the ranges, member 0 the start; for
  target i the mutant x_r1 + F(x_r2 − x_r3), r1, r2, r3 distinct and ≠ i, clipped into the ranges;
  binomial crossover with CR, at least one coordinate from the mutant; F = 0.5 and CR = 0.9 by the
  paper's §4. Each trial vector is locally optimized by `LocalOptimizer` (§35) and the
  population holds the optimized designs: the evolution runs on basin hopping's transformed
  surface Ẽ(X) = min{E(X)} (D. J. Wales and J. P. K. Doye, J. Phys. Chem. A 101, 5111, 1997;
  arXiv:cond-mat/9803344, eq. 2; `op-58-help/wales-doye-1997.pdf`). Generations after the first
  population: default 10. Global does not change glasses.
- **Hammer search** (`HammerSearch`). Monotonic basin hopping (Wales and Doye 1997): the start
  is re-optimized (trial 0); each round runs K trials (default 8) from the current design; a trial
  either moves every variable by a uniform number in [−1, 1] times the step (default 0.1 of the
  variable's range) or, with surfaces flagged Substitute and with probability 0.5 (1 when there
  are no variables), replaces one of their glasses, chosen uniformly, by a candidate chosen
  uniformly from OP-59's `GlassSubstitutionTemplate.Candidates` (§41) through
  `GlassVariables.WithGlass`; then the design is re-optimized. The round's best trial replaces the
  current design when it is better by more than 1e-9 relative; Wales and Doye's Metropolis test
  at a finite temperature is replaced by accepting only improvements. While the current design
  misses its exact constraints a glass trial starts from the start's variables, as §41's search
  does. Model-glass variables move like any variable; a surface flagged Substitute may not also
  vary its model glass. Default 25 rounds.
- **Ranges** (`GlobalTrials.DefaultRanges`, this program's choices; per variable through
  `Ranges`). Curvature ±3/|EFL| of the start (configuration 1, lens units) or ±the curvature
  limit given, widened to contain the start's value; thickness 0.5 to 1.5 times its value (0 to
  |EFL|/10 when zero); a model glass's nd with n − 1 from 0.5 to 1.5 times; anything else ±50 % of
  its value, or ±1 when zero. Ranges bound only the drawing; the local optimizer may leave them.
- **Ranking and the best list** (`GlobalDesign.Compare`, `GlobalBestList`). A design that meets
  every negative-weight row (|V − T| ≤ the local optimizer's tolerance × max(1, |T|)) ranks above
  one that does not (the rule of §41), then the lower merit, then the earlier trial. The list
  keeps `SaveCount` designs (default 10); a design of the same form as one listed (§10 item 39)
  replaces it when better and is dropped otherwise. A trial that cannot be built or evaluated is
  counted and named in the log (the first 20), never listed; in the Global search it never
  replaces its target, and a failed member of the first population keeps its unoptimized vector
  with an infinite merit.
- **Local optimization inside.** The caller's algorithm and cycles (infinite runs as
  automatic), scales and constraint settings; one thread per trial; an evaluation limit per
  trial (default 2000); solves and pickups re-evaluated as in §35.
- **Determinism.** Trial t draws only from `SplitMix64.ForStream(seed, t)` (Global: t = generation
  × NP + member; Hammer: t − 1 = (round − 1)K + k); the trials of a generation or round run in
  `ParallelSlots` from the previous state, and selection and the best list take them in index
  order. A seed and settings reproduce a run bitwise at any thread count. The time limit is
  checked between generations or rounds, so a timed run equals an untimed one with as many.
- **Stop** keeps the designs finished before it; a trial cut off by Stop is discarded.
- **Not built.** OpticStudio's GLOPT and _HAMMER files, Resume, the Global search's periodic
  return to listed designs, Auto Update of open windows during a run, glass changes in the
  Global search, saved search settings, a finite-temperature (Metropolis) Hammer.

## 44. Interactive adjustment tools

`OpenSim.Optics/Optimization/Interactive/` (OP-60) holds Quick Focus and Quick Adjust; the
Slider and the Visual Optimizer are app view models (`OpenSim.App/ViewModels/OpticsSliderViewModel.cs`).
The definitions were read from OpticStudio's 2025 R2 help pages "Quick Focus", "Quick Adjust",
"Slider", "Visual Optimizer" and "Manual Adjustment Group", saved under
`plans/sources/optics/op-60-help/`.

- **Quick Focus** (`QuickFocus`). The help: "This feature adjusts the thickness of the surface
  prior to the image surface. The thickness chosen is that which minimizes the RMS aberration."
  Criteria (help, Settings): spot size radial, spot size x only, spot size y only, wavefront
  error; Use Centroid references them to the centroid instead of the chief ray. One field's
  value is OP-40's RMS (§34, `RmsCore.Evaluate`, the engine of RMS vs Focus) at the field row,
  over every wavelength with its weight (polychromatic when there are several), the trial shift
  added to the thickness before the image (as RMS vs Focus adds it). Fields with a positive
  weight combine as √(Σ w_f v_f² / Σ w_f); a single field is its own value, so on a one-field
  lens the criterion is exactly the RMS vs Focus curve. Afocal image space is refused.
- **Quick Adjust** (`QuickAdjust`). The help: "adjusts any radius or thickness to minimize the
  RMS aberration at any subsequent surface"; "All criterion use the image centroid for a
  reference"; angular data are "computed after refraction into the specified evaluation
  surface". The rays are OP-34's (`RaySampler`: field rows and wavelengths with a positive
  weight, the quadrature's pupil points, read at the evaluation surface in its local frame).
  Spot: RMS of the positions about their centroid, radial, x or y; angular: the same of the
  direction cosines (L, M) after the surface, shown in milliradians (cosine × 1000).
  Wavelengths pool with weight pupil weight × wavelength weight; fields combine as for Quick
  Focus. A radius is searched as its curvature (it may pass through a plane); the evaluation
  surface must come after the adjusted one. Floating semi-diameters are sized again at every
  trial.
- **Sampling.** Gaussian quadrature (§34), 1 to 20 rings with 4 arms each, default 6; a trial
  where any ray fails has no value.
- **Search** (`InteractiveSearch`). The variable is x₀ + t·h, h = 1 % of the lens's length
  scale L (the sum of its finite thicknesses from surface 1 to the one before the image; 1 mm
  when zero) for a thickness, 1 % of max(|c|, 1/L) for a curvature.
  `BrentMinimizer.LineSearch` takes unit steps either way from t = 0, widens the bracket by the
  golden ratio while the criterion falls, and runs Brent's localmin on the bracket to
  tol = 1.49e-8·|t| + 1e-11; the minimum of a unimodal criterion lies within 2·tol·h of the
  answer (`PositionTolerance`). A trial without a value counts as +∞ (a wall); the start must
  have a value; a search that does not improve leaves the cell unchanged and says so.
- **On the project record** (`QuickFocus.Run`, `QuickAdjust.Run`). The lens is the current
  configuration's solved lens. The adjusted cell is refused when a solve or pickup sets it, when
  the multi-configuration table sets it in the current configuration (the new value would be
  overwritten), and in non-sequential mode. The new value is written into the saved cell in lens
  units (thickness: the old value plus the shift; radius: 1/curvature, a plane at 0). A change
  that moves other cells through their solves (a pickup of the adjusted cell, say) is not
  searched with those solves re-evaluated: the search runs on the solved lens with only the
  adjusted cell changed.
- **Slider** (help, "Slider"). Drags one surface value (radius, curvature, thickness, conic,
  Par 1–8) between a start and a stop. "A copy is made of the data to be modified": each move
  publishes the editors' record with that value to the session (`ProjectSession.PublishOptics`)
  without changing the editors, so every analysis window with auto-update on recomputes; Save
  (and Keep and Exit) writes the value through the Lens Data Editor as one undoable edit; Exit,
  closing the window or choosing another surface or parameter publishes the editors' record
  again. Animate steps the value by 1/50 of the range every 400 ms, wrapping at the stop.
- **Visual Optimizer** (help, "Visual Optimizer"). One to eight sliders ("between 1 and 8") on
  the lens's variables (`LensVariables.Find`; "the variable status of each value must be set
  prior"), radius variables as curvatures ("radius variables are always varied as
  curvatures"), moved together, published and kept as the Slider's are (a multi-configuration
  variable is written through the Multi-Configuration Editor).
- **Update budget.** The analysis windows' debounce, `OpticsAnalysesViewModel.DefaultDebounce`
  = 250 ms (OP-9's value, named by OP-60; this program's own, not OpticStudio's): a move issues
  each window's request at once, the compute starts after 250 ms without a further move, and
  moves inside it coalesce into one compute of the last value; a compute overtaken by a move is
  cancelled. OpticStudio's Visual Optimizer instead waits for each update to finish ("will not
  allow another update to be performed until the current update is completed").
- **Not built.** NSC Quick Focus and every non-sequential part (no non-sequential mode yet); the
  Slider's system, configuration and NSC data types (aperture, fields, wavelengths,
  apodization, temperature, pressure, multi-configuration operands) and its Window choice
  (every open window with auto-update on updates); the Visual Optimizer's Window choice and
  Animate; Quick Focus's angular form for afocal systems; a wavefront criterion in Quick Adjust
  (the help names none).

## 45. Monte Carlo tolerancing

`OpenSim.Optics/Tolerancing/MonteCarloAnalysis.cs` and `MonteCarloDistributions.cs` (OP-65).
Sources: OpticStudio 2025 R2 help, "Monte Carlo Analysis", "Normal Statistical Distribution",
"Uniform Statistical Distribution", "Parabolic Statistical Distribution" (and its equation image),
"User Defined Statistical Distribution", "Discussion of Monte Carlo Analysis Method", "Histogram",
"Yield", "Tolerance Data Viewer" (saved under `plans/sources/optics/op-65-help/`), "STAT",
"SEED", "Nesting Rules for Monte Carlo Analysis", "TIRR" (`op-63-help/`) and "Tolerancing", the
dialog's Monte Carlo tab (`op-64-help/Tolerancing.txt`).

- **Set-up.** The criterion, compensators, fields and sampling are OP-64's (§42), built by the
  same code (`ToleranceAnalysis.Prepare`); the compensated nominal lens gives N.
- **Distributions.** Offsets from the midpoint of Min and Max, Δ = (Max − Min)/2. Normal: σ = Δ/n,
  n from the STAT row's Int2 (1 to 10) or the run's default 2, truncated at ±Δ. Uniform: equal
  probability over ±Δ. Parabolic: p(x) = 3x²/(2Δ³), drawn as Δ·∛(2u − 1). User defined: the .UDD
  table named in the STAT row's comment (two columns X, T; X from 0 to 1, first 0, last 1),
  mapped onto [Min, Max]. Rows no STAT row governs use the run's statistics (normal, uniform or
  parabolic; default normal).
- **A trial.** Every tolerance row (not ignored, not a control row) draws its value in row order;
  radial operands also draw a direction and TIRR its astigmatism angle, uniform over 0–360°. The
  rows are applied one after another by `TolerancePerturber` to the compensated nominal, each
  change's `MapSurface` composed so later rows find their surfaces (§36); the compensators are
  evaluated (§42) and the criterion and its change recorded. Rows whose operand this version
  cannot apply are left out of every trial and named.
- **Random numbers and determinism.** Rows under one SEED value draw from
  `SplitMix64.ForStream(seed, k)` for trial k; SEED 0 and rows under no SEED use the run's seed
  (0 draws one from the clock, reported). Trial k is therefore the same alone
  (`MonteCarloAnalysis.Trial`), in sequence or in parallel. Trials run on `ParallelSlots`, one
  thread each, and the statistics are reduced in trial order: the result is bitwise identical at
  any thread count.
- **Failures.** A trial whose perturbation or evaluation fails keeps its draws and reason and is
  left out of the statistics and the yield, with the count reported. Element decenters and tilts
  make the lens non-axial, which the ray launcher refuses (§15), so such trials fail by name.
- **Results.** Mean, sample standard deviation, min, max, percentiles (linear interpolation
  between order statistics, as `ParameterSweep`'s statistics), best and worst trial, each
  compensator's statistics, the yield (criterion ≤ limit, or ≥ where larger is better) over the
  evaluated trials, a histogram (bins between Min Value and Max Value, results outside counted in
  one bar each, help "Histogram"), and an MTF overlay (OP-39's FFT MTF of each compensated trial
  lens at one field, every wavelength, the help's "Overlay MC Graphics"). `TrialLens` regenerates
  trial k's compensated lens for saving (help, "# Monte Carlo Save"); with paraxial focus the
  shift is written into the thickness before the image, refused for several configurations or a
  thickness set by a solve, pickup or multi-configuration row.
- **Distributions not in ParameterSweep.** The tolerance distributions stay in `Tolerancing`
  (their ranges, truncation and radial rules are OpticStudio's); `ParameterSweep` is unchanged.
  Its statistics are not reused directly because they take a sweep result; the percentile
  definition is the same.
- **Readings** where the help is short are in §10, item 41.

**Not built.** TEZI's random per-term coefficients ("each polynomial term is assigned a
coefficient randomly chosen between -1.0 and 1.0 ... normalized"; a trial applies the drawn RMS
equally over the terms, as the sensitivity analysis does), correlated draws, Save Best and Worst
Monte Carlo files by name and the File Prefix, the ZTD data file, the Tolerance Data Viewer and
Yield curve (OP-66), tolerance scripts (OP-67), overlay of analyses other than the MTF, and
evaluation of element decenter and tilt trials (the launcher, OP-22).

## 46. Command-line optics

`OpenSim.Cli/CliRunner.Optics.cs`, `OpenSim.Cli/SweepSpec.cs` (`OpticsTargets`) and
`OpenSim.Optics/Analysis/OpticsBatch.cs` (OP-97) run the optics of a saved project from the
command line (`opensim optics list|analyse|trace|optimise|tolerance`, and optics targets in
`opensim sweep`).

- **One report function.** An analysis report is `OpticsAnalysisReport.Compose` over the
  analyses' results, the function the app's Save report calls
  (`OpticsAnalysesViewModel.ComposeReport`). `OpticsBatch.Context` builds the snapshot the app's
  windows take (`OpticsAnalysesViewModel.Snapshot`): the optics record with `Analyses` null and the
  lens's active catalogs among the loaded ones. `OpticsBatch.SavedSettings` gives an analysis the
  settings a new window opens with: the project's `LastSettings` for its id, read by
  `OpticsAnalysisSettingsCodec.Load`, or its defaults. `OpticsBatch.Run` titles each entry as a
  window is titled (the name, then "(2)", "(3)") and records a failed compute as the window does
  ("Failed: " and the message). With the same file, settings and catalogs the Markdown and HTML
  are therefore byte-identical (gate in docs/Optics-Gates.md). The app's `Snapshot` is not changed
  to call `OpticsBatch.Context` (that view model is OP-60's in this wave); the parity test holds
  the two to the same output.
- **Catalogs.** The command line loads `GlassCatalogLibrary` as the app does (built-ins, then the
  user's catalog folder); `--builtin-catalogs` takes the built-in set alone, in the library's
  order (by name).
- **Settings overrides.** `--settings FILE` is read strictly (`JsonUnmappedMemberHandling.Disallow`):
  an unknown property is an error, not a silent default as in a project load. `--set` names the
  settings record's properties (any case); numbers in the invariant culture, enums by name.
- **Plot data.** `AnalysisPlotCsv` writes a `LinePlot` and an `AnalysisScatterPlot` as long-form
  points and an `AnalysisMapPlot` as a matrix, through `PlotCsv` (RFC 4180, invariant culture,
  shortest round-trip numbers, `#` metadata lines), so the file reads back to the plotted doubles.
- **Optimise and tolerance** call `LocalOptimizer.Run` (§35) and `ToleranceAnalysis.Run` or
  `QuickSensitivity` (§42) on the saved record with its active catalogs; the saved run settings
  (`OpticsToleranceRunSettings`) are the defaults the options override. The optimised record is
  written with `LocalOptimizationResult.ApplyTo` into a copy of the project; `tolerance.txt` is
  `ToleranceRunResult.ReportLines` joined by newlines, the editor's report text.
- **Sweep targets.** `OpticsTargets.Apply` sets one cell of the saved record, in saved units:
  radius (0 a plane, as the editor reads it), curvature (0 a plane), thickness, conic,
  semi-diameter, TCE, a constant index, a parameter column and the model-glass values through
  `LensVariables.Apply`, the aperture value, temperature, pressure, a wavelength and its weight,
  a field's x, y and weight. Surfaces are numbered as the editor numbers them (0 is the object);
  wavelengths and fields from 1. `Validate` applies each parameter's nominal (or first value)
  before the sweep and lists every refusal by parameter and target. A cell with a solve or pickup
  flag is refused, since the solve would overwrite the value. The outputs are the saved merit
  function (`MeritEvaluator.Evaluate` over `ProjectMeritConfigurations`) and each evaluated row's
  value; a failed row fails that run, naming the row. A job mixing optics and other targets is
  refused.
- **Cancellation.** `Program` turns the first Ctrl+C into a cancellation token
  (`CliRunner.Run(args, out, err, token)`); the analyses, the optimizer, the tolerance run and the
  optics sweep poll it. A cancelled optimization writes its best design and exits 2; the others
  exit 2 with "Cancelled.". The other commands do not take the token.
- **Global and Hammer searches** (`CliRunner.OpticsSearch.cs`). `optics optimise --global` calls
  `GlobalSearch.Run` and `--hammer` calls `HammerSearch.Run` (§43) on the saved record with its
  active catalogs. An option not given keeps the engine's own default (`GlobalSearchOptions`,
  `HammerSearchOptions`); `--algorithm` and `--cycles` set each trial's local optimization
  (infinite cycles run as automatic, as the engine requires), `--max-evaluations` is the limit
  per trial, `--time-limit` is in seconds. A refusal by the engine (a population below 4, no
  variables, an afocal start without `--curvature-limit`) exits 2 with its message. The folder
  (`<project>-optics-global` or `-optics-hammer`) holds `report.md`/`.html` (`SearchReport`),
  `designs.csv` (the best list, best first, values in saved units, a radius as its curvature),
  `log.txt` (the engine's log) and `<project>-optimised.ossproj`, the best design written with
  `GlobalSearchResult.ApplyTo` into a copy of the project. A cancelled run writes the designs
  found so far and exits 2; with no design (stopped before the first trial, or every trial
  failed) no project is written and the exit is 2. Failed trials alone do not change the exit
  code (random starts that cannot be traced are expected); they are counted in the report.
- **Monte Carlo** (`CliRunner.OpticsSearch.cs`). `optics tolerance --montecarlo` (or
  `--mode montecarlo`) calls `MonteCarloAnalysis.Run` (§45) with the run options the sensitivity
  run would use (the saved run settings and the command line's criterion, compensation, fields,
  sampling and threads) and the project's saved Monte Carlo settings
  (`MonteCarloSettings.FromSettings`), overridden by `--trials`, `--seed` (0 draws from the
  clock, as the engine does) and `--yield-limit`. `--montecarlo` with another `--mode` is a usage
  error. The folder (`<project>-optics-montecarlo`) holds `report.md`/`.html`
  (`MonteCarloReport`), `montecarlo.txt` (`MonteCarloResult.ReportLines`, the editor's report
  text), `trials.csv` (each trial's criterion, change, compensators, failure and drawn values,
  with the direction or angle of radial and TIRR rows), `histogram.csv` and, with an MTF overlay
  saved, `mtf-overlay.csv` (`AnalysisPlotCsv`), and `log.txt`, which names each failed trial with
  the engine's reason. `--save-trial k` writes `MonteCarloAnalysis.TrialLens` of trial k (with the
  seed the result names) as `MC_T000k.osslens`, the app's file name; a trial that failed or whose
  focus shift cannot be saved is logged and gives exit 2. A failed trial gives exit 2, as a row
  that could not be evaluated does in sensitivity. Cancellation stops the run with "Cancelled."
  (exit 2); the engine returns no partial result.
- **Not built.** Multi-configuration cells as sweep targets, lens files (`.osslens`) as input,
  ranges for the global searches from the command line (the engines' default ranges are used,
  `--curvature-limit` aside), and CSV of the analyses' tables (their text is written) are not
  built.

## 47. Scripting surface

`OpenSim.Optics/Scripting/OpticsScripting.cs` (OP-98) is the small facade a script calls besides
the library's own public types; `docs/Scripting-from-JGraph.md` ("Optics") lists the whole
surface and `OpenSim.Tests/Cli/ScriptingSurfaceTests.cs` pins it by reflection. A script works on
the saved records (`OpticsSettings` and its parts), the same data the editors edit; the engines
build the lens from them through `ProjectLens` as the application does.

- **Load** returns `SimProject.Optics` of a saved project; a project without one is refused.
- **Catalogs** loads the user's glass library (`GlassCatalogLibrary`: the built-in catalogs and
  the catalog folder) and narrows it to the record's active catalogs
  (`GlassLookup.ActiveCatalogs`); active names of which none is loaded are refused.
  `GlassCatalogLibrary.BuiltInCatalogs` alone gives a result independent of the user's folder.
- **Analyze** runs any registry analysis (`OpticsAnalysisRegistry.CreateDefault`) by its saved id
  on an `OpticsAnalysisContext` of the record and the catalogs, with the given settings record or
  the analysis's defaults; the current configuration is used, as every analysis does (§24).
  `AnalysisIds` lists the ids; `AnalysisSettings` gives a defaults record to change. An unknown id
  is refused, naming where the list is.
- **WithDefaultMerit** runs the Optimization Wizard (§40) on the record's configurations and
  inserts the rows at Start At (null: the row after DMFS, else the end) with
  `DefaultMeritWizard.Insert`, as the Merit Function Editor does. **EvaluateMerit** evaluates the
  saved merit function over every configuration (`ProjectMeritConfigurations`). Both refuse a
  saved row whose operand this version does not know, rather than reading it as OOFF as the
  editor's load does.
- **TraceRay** traces one ray of normalised field and pupil at a wavelength row (0 the primary)
  through the current configuration, as the Single Ray Trace analysis does (automatic
  semi-diameters sized first when a floating aperture needs them), and maps the ray's status to
  OpticStudio's ZPL codes (help, "RAYTRACE"): RAYE = 0 for a ray that reached the image or was
  vignetted, +n for a miss at surface n, −n for total internal reflection at n, −9999 when the
  aimer fails; RAYV = n for vignetting at n, else 0. No convergence and evanescent orders give
  +n (§10 item 43). The path stays in meters (§1).
- **Optimization and tolerancing** are called directly: `LocalOptimizer.Run` (§35),
  `GlobalSearch.Run` and `HammerSearch.Run` (§43), `ToleranceAnalysis.Run` and
  `QuickSensitivity` (§42) and `MonteCarloAnalysis.Run` and `TrialLens` (§45) on a
  `ToleranceTable` (`ToleranceDataSettings.FromSettings` reads the saved one).
- **MonteCarlo** runs `MonteCarloAnalysis.Run` on the saved table with the saved run settings
  and the given `MonteCarloOptions`, or the saved Monte Carlo settings when none are given (their
  reader has two out parameters, which a script may not be able to call). Like
  `WithDefaultMerit`, it refuses saved entries this version cannot read (an unknown operand,
  model, criterion or statistics name) instead of using the editor's fallback, and refuses a
  record with no tolerance rows.
- **Records from a script.** The settings records have init-only setters. JGraph's interop and
  pythonnet set properties by reflection, which writes init-only setters, so a script builds a
  record with `r = Type(); r.Property = value` (checked under JGraph on 2026-11-14; pythonnet not
  run). A record is then changed in place, which C# code never does; a script should build a
  record before handing it to an engine and not change it while one holds it.
- **Not built.** The non-sequential model, macro language (OP-99) and plug-ins (OP-100). The
  global searches and Monte Carlo were pinned by reflection but not run under JGraph.

## 48. Test plates

`OpenSim.Optics/Optimization/TestPlates/` (OP-17) reads and writes fabricators' test plate lists
and fits variable radii to them, after OpticStudio 2025 R2 help, "Test Plate Fitting" and "Test
Plate Lists" (saved under `plans/sources/optics/op-17-help/`). No list ships with the program.

- **List layout** (`TestPlateList`). Up to 15 header lines starting with "!", then one plate per
  line, `partname radius diameter code`, separated by spaces or tabs; radius and diameter in
  millimeters whatever the lens unit; code −1 concave only, 0 both, 1 convex only; at most 30,000
  plates. Blank lines are skipped. A line with the wrong number of values, a radius that is not
  positive, a negative diameter, a code other than −1, 0, 1, a "!" line after the first plate, a
  16th header line or a 30,001st plate fails the read, naming the file and every bad line. The
  writer gives the same layout with round-trip numbers and refuses what the layout cannot hold.
  The list report shows "!" as a space and columns CC and CX, as the Test Plate Lists tool does.
- **What is fitted** (`TestPlateFitting.Run`). Only Radius cells flagged Variable in the Lens Data
  Editor. Variable multi-configuration CRVT cells are not fitted (a note says so); they and every
  other variable are re-optimized as compensators. The current merit function is the figure of
  merit. Clear semi-diameters are the fixed ones as entered, automatic ones sized as the editor
  sizes them (`ApertureTools.ClearSemiDiameters`, the base configuration), re-read at every step.
- **Element side.** Light runs along +z and reverses at each mirror. A refracting surface's
  element material is the solid medium after it (+direction) or before it (−direction); solid on
  both sides is cemented. A mirror's substrate lies behind the reflecting face (+direction),
  unless the light arrives through glass (a Mangin mirror: −direction). The medium after a mirror
  is the one before it; a constant index of exactly 1 counts as air. With s that side in the
  surface's local z, a surface of curvature sign σ is convex when σ = s.
- **Admissible plate.** The right shape (convex surface: a concave plate, code −1 or 0; concave
  surface: a convex plate, 1 or 0; cemented: code 0; a plane surface may take either sign); a
  diameter of at least 3/4 of the clear aperture diameter (0 skips the test); and an edge sag
  within 0.2 mm of the surface's at the clear semi-diameter, the surface's sag with its conic and
  the plate's spherical with the surface's sign (§3 sag). The closest admissible plate is the one
  of fewest fringes N = r²·|σ/R_plate − c|/λ (r the clear semi-diameter, λ the test wavelength,
  default 0.6328 µm), ties to the earlier line.
- **Procedure.** The design is optimized locally first (`LocalOptimizer`, §35). Then, while
  variable radii remain: each one's closest plate is found; a radius with none is reported
  "NO MATCH FOUND!" with the count of plates failing each test and stays variable; of the rest the
  method picks one (Best To Worst: fewest fringes; Worst To Best: most; Long To Short: smallest
  |c|; Short To Long: largest; ties to the lower surface); its radius becomes σ·R_plate (in lens
  units), its Variable flag is removed and every remaining variable is re-optimized (or the merit
  only evaluated when none is left). Try All runs the four orders from the same pre-optimized
  design and keeps the lowest final merit, the first in the help's order on a tie; an order that
  fails is reported with its reason and the others still count. "# Opt Cycles" is the local
  optimizer's Automatic or Fixed cycles; infinite cycles run as automatic.
- **Result.** Each step gives the surface, the radius before and after (lens units), the plate,
  its fringes, the clear semi-diameter used and the merit after the re-optimization; the report
  (`ReportLines`) adds the vendor header, the order kept, each order's merit under Try All and the
  merit before and after. A re-optimization that fails stops the order with a message naming the
  order, surface and plate (and the list line). Cancellation returns the design so far, flagged;
  the app does not apply a stopped fit.
- **App.** The optics panel's Test Plate Fitting… opens a FormGrid dialog (list file, method,
  cycles, test wavelength; Show List, Fit, Stop). Fit writes the fitted design through the Lens
  Data Editor as one undoable edit, and the multi-configuration table too when one of its cells is
  variable, as the Optimize tool does.
- **Not built.** Fitting multi-configuration CRVT cells; a Testplate folder of installed lists
  (any file is chosen instead); the ZPL keyword TESTPLATEFIT and a command-line arm; higher-order
  aspheric terms in the edge-sag test. Nothing is compared with OpticStudio's output (§10 item 44).

## 50. High-yield optimization

`OpenSim.Optics/Optimization/HighYield/` (`HighYieldPenalty.cs`, `HighYieldMerit.cs`) and the
operand `OpenSim.Optics/Merit/HighYieldOperands.cs` (OP-62). Sources, saved under
`plans/sources/optics/op-62-help/`: OpticStudio 2025 R2 help, "Optimization Goal" and
"Constraints on Real Ray Data" (HYLD); Zemax Knowledgebase KA-01837, "Designing for as-built
performance with High-Yield Optimization" (C. Normanshire, 2021, an archived copy with its
equation images); the Zemax eGuide "High-Yield Optimization" (2019). The help cites K. E. Moore,
"Optimization for as-built performance", Proc. SPIE 10925, 1092502 (2019); it is not freely
readable and was not read. Ansys's current knowledgebase page (optics.ansys.com) refused
automated fetches; the archived Zemax copy was used instead.

- **The measure: the HYLD term.** OpticStudio's tolerance-sensitivity operand for optimization is
  HYLD, a ray-angle proxy, not a tolerance run: "uses the angles of incidence as a proxy for the
  actual tolerances" (eGuide). Implemented as KA-01837 defines it: τ = |n − n′|(1 − R·N), n and
  n′ the indices before and after the surface, N the unit normal, R the incident ray when
  n′ &gt; n, else the refracted ray — the larger of the two angles. R·N is taken as |R·N| (the
  angle's cosine, 0 to 90°), and 1 − cos θ is computed as sin²θ/(1 + cos θ), |R × N|² for
  sin²θ, which keeps its digits at small angles. Dimensionless; ≈ |n − n′|θ²/2 for small θ.
  The help's other tolerance-data operand, TOLR (an RSS tolerance estimate inside the merit
  function), is not built.
- **HYLD.** Surf (Int1), Wave (Int2), Hx, Hy, Px, Py (Data1–4); the ray is the merit function's
  shared real ray. It fails by name for surface 0, a ray that fails at or before the surface,
  and a mirror (the sources do not give n′ there).
- **The rows.** `HighYieldMerit.Generate` writes, for every configuration, field and wavelength
  of non-zero weight and every pupil point of the default merit function (the Optimization
  Wizard's saved options: its rule, symmetry reduction and switch to the rectangular array when
  vignetted, `DefaultMeritWizard.Points`), one HYLD row per surface with a different index on
  either side (`HighYieldPenalty.RefractingSurfaces`, the indices of the real-ray trace), target
  0, weight W × overall × field × wavelength × pupil weight. Rays that fail are left out and
  counted in the notes; mirrors are named. A BLNK row whose comment starts "High-Yield:" heads
  the block, CONF rows switch configurations, and the block sits at the end of the merit
  function: `WithRows` replaces an earlier block, `Without` removes it, and a block not at the
  end, HYLD rows outside it, or a jump into it are refused by name.
- **Report.** `HighYieldMerit.Report` groups an evaluation's HYLD rows by configuration and
  surface: Σwτ/Σw, the largest τ and the share of the merit function.
- **Optimize window.** The High Yield section: Weight (default 1, the wizard's box in the
  article's screenshot; saved with the lens as `OpticsSettings.HighYield`), Add High-Yield rows
  (one undo step of the Merit Function Editor), Remove, Evaluate and the per-surface table.
  Optimization itself is the ordinary Start (OP-57) or Global and Hammer (OP-58) on the merit
  function holding the rows.
- **Gate.** `Gates/HighYieldGateTests` (Slow): a zero-thickness glass shell before an ideal lens,
  so every shell curvature has the same perfect nominal image; with the High-Yield rows the
  optimizer flattens the shell, and OP-65's Monte Carlo over the shell front's decenter (fixed
  seed) gives a standard deviation of the RMS spot radius below half the start's, with the
  nominal RMS spot radii equal to 1e-9 lens units.
- **Readings** where the sources are short are in §10, item 46.

**Not built.** The "Improve Manufacturing Yield" choice inside the Optimization Wizard itself
(the rows are added from the Optimize window, from the wizard's saved options); TOLR; HYLD at
mirrors; OpticStudio's exact HYLD pupil sampling; a command-line arm.
