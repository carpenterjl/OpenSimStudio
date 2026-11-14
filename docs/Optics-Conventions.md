# Optics conventions

This is the one written convention for the Optics toolbox. Every optics item follows it, and the
code that implements each rule is named beside the rule. The tests are in
`OpenSim.Tests/Optics/OpticsConventionsTests.cs` and `OpenSim.Tests/Core/RigidTransformTests.cs`;
those of the air and glass index models (section 2) in `OpenSim.Tests/Optics/AirIndexTests.cs` and
`OpenSim.Tests/Optics/ThermalIndexTests.cs`; those of paraxial optics (section 11) in
`OpenSim.Tests/Optics/ParaxialTests.cs`.

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
surface's "before" values. Those rules are built with the coordinate-break item (OP-21).

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
  (0, 1) the upper marginal ray. Px² + Py² ≤ 1. Built with ray aiming (OP-22).

## 8. Where our default differs from OpticStudio

| Matter | Default here | OpticStudio-compatible setting |
|---|---|---|
| Wavelength carried by a ray | vacuum | air at system temperature and pressure (file values) |
| Index after a mirror | positive, direction in the ray | same results; only the bookkeeping differs |
| Index of air | Ciddor (NIST Appendix A), CO2 450 µmol/mol, dry air | SCHOTT TIE-19 equations (8)–(9) |

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
  height on the image surface. Real image height needs the real trace and ray aiming
  (OP-20, OP-22) and is refused. The x and y planes are traced independently.
- **Marginal and chief rays.** The marginal ray runs from the axial object point to the top
  of the entrance pupil; the chief ray of the reports is that of the largest y field
  (Hy = 1). Lateral magnification is ν/ν′ of the marginal ray (zero for an object at
  infinity); angular magnification ū′/ū of the chief ray; the Lagrange invariant
  H = n·(ū·y − u·ȳ) with the signed index, the same in every space.
- The reports' signs and reference points are ours; section 10, item 6, lists the check
  against OpticStudio.
