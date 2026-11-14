# OP-102 — Cross-checks against independent programs

Reference material behind `OpenSim.Tests/Optics/ReferenceProgramTests.cs`. The owner decided on
2026-11-12 (plan OQ-9a) that Python reference tools may generate stored values. The scripts here
are run once by hand; the test suite only reads the JSON files they wrote. When the files are
absent the tests report themselves as skipped and name this folder.

No claim of agreement with OpticStudio is made by anything here: OpticStudio was not used.

## Programs (installed from PyPI on 2026-11-14)

| Program | Version | Licence | Used for |
|---|---|---|---|
| Optiland (K. Harrison) | 0.6.3 | MIT | real rays, launch rays, paraxial data |
| RayOptics (M. J. Hayford) | 0.9.10 (with opticalglass 2.0.2) | BSD 3-Clause | real rays, first-order data |
| tmm (S. Byrnes) | 0.2.0 | MIT | thin-film amplitudes and powers |

Python 3.12.10, numpy 2.5.4, scipy 1.18.1, numba 0.68.1 (Optiland's backend). The full set of
packages in the environment is `requirements-frozen.txt` (`pip freeze`). The environment was a
venv at `plans/sources/optics/pyref-venv` (not in the repository); the brief for this item
allowed installing these three packages and their dependencies there. prysm and POPPY
(diffraction) are for later items and were not installed.

## Files

- `lenses.py`: the five reference lenses and the rays traced, defined once for both ray
  generators. All media are constant indices, so no dispersion formula or catalog is involved
  and both programs and OpenSim certainly use the same index. Radii, thicknesses, conics and
  indices are arbitrary test inputs, not a published design.
  - `singlet`: biconvex, prolate-ellipsoid front (k = −0.6), stop on surface 1, object at
    infinity, angle fields 0, 3.5, 7°.
  - `doublet_finite`: cemented doublet, object 250 mm in front, object-height fields 0, 10,
    20 mm.
  - `triplet`: Cooke-triplet-like, separate plane stop between the second and third elements,
    oblate-ellipsoid last surface (k = +0.5), angle fields 0, 10, 15°.
  - `parabola`: concave paraboloid mirror, image at its focus, fields 0, 1, 2°.
  - `two_mirror`: paraboloid primary and hyperboloid (k = −3) convex secondary, negative
    thickness between them, fields 0, 0.3, 0.5°.

  Each field is traced at eight pupil points: (0, 0), (0, ±1), (1, 0), (0, 0.5), (0.6, 0.6),
  (−0.3, 0.8), (0.7, −0.4) — 24 rays per lens, skew rays included. Wavelength 0.55 µm (it only
  converts optical path to waves).
- `gen_optiland.py` → `optiland_<lens>.json`; `gen_rayoptics.py` → `rayoptics_<lens>.json`;
  `gen_tmm.py` → `tmm_stacks.json`. Each script's docstring says exactly what it stores.
- `requirements-frozen.txt`: the environment.

Each lens file holds the lens as given (millimetres; JSON null = infinite radius or object
distance), the program's paraxial data under the program's own names, and per ray: the launch
point and direction in surface 1's frame, and at every surface from 1 to the image the local
intersection point (vertex at the origin), the direction cosines after the surface, and the
optical path from the launch point. Floats are written with Python's shortest round-trip repr.

## Regenerating

```
python -m venv plans/sources/optics/pyref-venv
plans/sources/optics/pyref-venv/Scripts/python -m pip install optiland==0.6.3 rayoptics==0.9.10 tmm==0.2.0
cd OpenSim.Tests/References/OP-102
<venv>/Scripts/python -I -B gen_optiland.py
<venv>/Scripts/python -I -B gen_rayoptics.py
<venv>/Scripts/python -I -B gen_tmm.py
```

Each script takes an optional output folder (default: its own). Two runs on 2026-11-14 wrote
byte-identical files.

## Conventions of each program, as read in its source

Read in the installed packages (paths under `site-packages`):

- **Units.** Optiland: lens units, taken as millimetres here; wavelength in µm. RayOptics:
  lens units (millimetres here); wavelength in nm (`raytrace.trace`, "wvl: wavelength in nm").
  tmm: any length unit common to thicknesses and wavelength (nm here). OpenSim: meters inside;
  the tests convert.
- **Signs.** Both programs use radius positive with the centre of curvature toward +z, and a
  negative thickness after a mirror (Optiland `materials` "mirror" keeps the medium before;
  RayOptics `create_surface_and_gap`, 'REFL' sets z_dir = −1 and keeps the medium; its local
  transforms are a translation by the gap thickness with no rotation). Directions are unit
  vectors pointing the way the light travels (N < 0 after one mirror) in both, as in OpenSim.
  Their indices stay positive after a mirror.
- **Intersections.** Optiland `geometries/standard.py`: closed-form conic root in the
  "citardauque" form, sag sheet only. RayOptics `elem/profiles.py`, `Conic.intersect`:
  closed-form root s = cx2/(z_dir·√(b² − ax2·cx2) − b) from the foot of the perpendicular from
  the vertex (`raytrace.trace_raw`). Neither iterates, so both are exact to rounding.
- **Optical path.** Optiland adds t·n(λ) per step from the ray's current point to the
  intersection (`standard_surface.py`, `_trace_real`), starting at 0 at the launch point.
  RayOptics' `trace_raw` adds index × distance per gap; the script sums `rndx[j−1]·dst[j−1]`
  surface by surface. OpenSim adds n·s per segment. With constant indices all three are the sum
  of index × geometric length; air is exactly 1 in all three (Optiland `IdealMaterial(1.0)`,
  opticalglass `Air.rindex` returns 1.0, OpenSim's constant indices are relative to air).
- **Launch rays, ray aiming off.** Optiland's default ray aimer is "paraxial"
  (`rays/ray_aiming/paraxial.py`): the ray goes through (Px·EPD/2, Py·EPD/2, z_EP) on the
  paraxial entrance pupil; for an object at infinity it starts at
  (Px·r − tan θx·d, Py·r − tan θy·d, z_EP − d) (`fields/field_types/angle.py`), so its direction
  is (tan θx, tan θy, 1) normalised; for an object height it starts at the object point
  (`object_height.py`). That is OpenSim's rule (`RayLauncher`), so the test compares the
  launches. RayOptics' launcher (`trace_base`, `ray_start_from_osp`) aims the chief ray at the
  real stop (`aim_chief_ray`) and represents an object at infinity by an object 1e10 mm away,
  so the RayOptics launch rays are built by `gen_rayoptics.py` from RayOptics' paraxial
  entrance pupil with the rule above, start 20 mm in front of surface 1 (object at infinity),
  and are not used to check OpenSim's launcher.
- **Paraxial data.** Optiland (`paraxial.py`): f2 = n′/Φ with the image-space index signed by
  the mirrors (−100 mm for the concave mirror), f1 = −n/Φ; F1, P1, EPL from surface 1; F2, P2,
  XPL from the image surface; XPD = 2 × marginal height at the exit pupil. RayOptics
  (`parax/firstorder.py`): efl = fl_img = n_k/power, fl_obj = n_0/power; pp1, ffl, enp_dist
  from surface 1; ppk, bfl from the last surface; img_dist from the last surface;
  exp_dist = img_dist − h̄_k/ū_k, which is the exit pupil from the last surface only when the
  image surface is at the paraxial image. The OpenSim names each maps to are in
  docs/Optics-Conventions.md §22 and the test comments.
- **Thin films.** tmm's `coh_tmm`: n + ik with k ≥ 0, forward wave with Im kz > 0, r_p = −r_s
  at normal incidence; the same convention as OpenSim's `ThinFilm` (Conventions §12), so values
  compare without mapping.

## What the files show (measured 2026-11-14)

- **Optiland against RayOptics** (independent of OpenSim). Per-surface positions agree to
  2.7e-13 mm, direction cosines to 1.5e-15, optical paths between surface 1 and later surfaces
  to 4.0e-13 mm, on every lens except the triplet, where the launch rays differ by up to 1.1e-8
  mm because RayOptics' entrance pupil (enp_dist) differs from Optiland's EPL by 3.7e-8 mm: the
  1e10 mm stand-in for infinity. Focal lengths, principal planes and focal points agree to
  1e-12 relative.
- **RayOptics' stand-in.** With an object at L = 1e10 mm instead of infinity, the image moves
  by f²/L (Newton's equation): img_dist is off by 1.0e-6 mm on the parabola (f = 100 mm),
  6.1e-7 mm on the singlet, 3.6e-5 mm on the two-mirror lens (EFL 600 mm). The conjugate-
  dependent first-order quantities are therefore compared with an allowance of 2·EFL²/L; the
  system-matrix quantities, and everything on the finite-conjugate doublet, with 1e-9 mm.
- **RayOptics' exit pupil.** exp_dist is not measured from the last surface when the image
  surface is away from the paraxial image (e.g. singlet: exp_dist −2.150 mm, the exit pupil is
  3.997 mm in front of the last surface, the difference being img_dist − t_last = 1.847 mm).
  The test compares exp_dist − img_dist + t_last.
- **OpenSim**: the comparisons are the tests; their results are not recorded here, because
  the suite is run by the owner.

## Not covered

Catalog glasses (both programs read AGF-like data differently; constant indices were chosen to
keep the comparison about the trace), tilts and decenters (OP-21), aspheres (OP-23), ray aiming
(OP-22), OPD against a reference sphere (OP-35), diffraction (prysm, POPPY: later items), and
OpticStudio.
