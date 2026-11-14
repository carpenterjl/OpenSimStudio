"""Reference ray data and first-order data from RayOptics (BSD licence) for the OP-102 lenses.

Run once, outside the test suite, in the venv that holds RayOptics (README.md, "Regenerating"):

    python -I gen_rayoptics.py <output folder>

For each lens in lenses.py it writes rayoptics_<lens>.json: the lens as given, RayOptics'
first-order data (``opm['analysis_results']['parax_data'].fod``) under RayOptics' own names,
and for every (field, pupil) ray of lenses.rays() the launch ray and the ray at every surface
from surface 1 to the image, as traced by RayOptics' ``raytrace.trace_raw``: local position,
direction cosines after the surface, and the optical path from the launch point. Lengths are
millimetres. JSON null stands for an infinite radius or object distance.

Launch rays. RayOptics' own launcher (``trace_base``) aims the chief ray at the real stop
(``aim_chief_ray``), which is ray aiming; OpenSim's launcher has ray aiming off. So the launch
rays here are built by this script from RayOptics' paraxial entrance pupil (fod.enp_dist from
surface 1, fod.enp_radius), the rule OpenSim and Optiland use without ray aiming: the ray goes
through (Px r, Py r, enp_dist); for an object at infinity its direction is
(tan ux, tan uy, 1) normalised, u the field angle, and it starts on the plane 20 mm in front
of surface 1; for an object at a finite distance it starts at the object point (0, H y_max).
The object interface of the traced path is placed on that start plane (instead of RayOptics'
1e10 mm stand-in for infinity, which would cost about 1e-6 mm of precision) and is not
intersected (``intersect_obj=False``).
"""

import json
import math
import os
import platform
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lenses  # noqa: E402

import numpy as np  # noqa: E402
from importlib.metadata import version  # noqa: E402
from rayoptics.optical.opticalmodel import OpticalModel  # noqa: E402
from rayoptics.raytr.opticalspec import PupilSpec, FieldSpec, WvlSpec  # noqa: E402
from rayoptics.elem.profiles import Conic  # noqa: E402
from rayoptics.raytr import raytrace as rt  # noqa: E402

WVL_NM = lenses.WAVELENGTH_UM * 1000.0
START_GAP = 20.0     # start plane in front of surface 1 for an object at infinity, mm


def finite_or_none(v):
    return None if math.isinf(v) else v


def build(lens):
    opm = OpticalModel()
    opm.radius_mode = True
    sm = opm['seq_model']
    osp = opm['optical_spec']
    fields = lens["fields"]
    fmax = max(abs(f) for f in fields)
    osp['pupil'] = PupilSpec(osp, key=['object', 'epd'], value=lens["epd"])
    key = 'angle' if lens["field_type"] == "angle" else 'height'
    osp['fov'] = FieldSpec(osp, key=['object', key], value=fmax,
                           flds=[f / fmax for f in fields], is_relative=True)
    osp['wvls'] = WvlSpec([(WVL_NM, 1.0)], ref_wl=0)
    surfaces = lens["surfaces"]
    t0 = surfaces[0]["thickness"]
    sm.gaps[0].thi = 1e10 if math.isinf(t0) else t0
    for s in surfaces[1:-1]:
        r = 0.0 if math.isinf(s["radius"]) else s["radius"]
        medium = 'REFL' if s["mirror"] else s["index"]
        sm.add_surface([r, s["thickness"], medium])
        if s["conic"] != 0.0:
            sm.ifcs[sm.cur_surface].profile = Conic(r=r, cc=s["conic"])
        if s["stop"]:
            sm.set_stop()
    opm.update_model()
    return opm


def run(name, lens, out_dir):
    opm = build(lens)
    sm = opm['seq_model']
    fod = opm['analysis_results']['parax_data'].fod
    paraxial = {k: float(getattr(fod, k)) for k in
                ("efl", "fl_obj", "fl_img", "pp1", "ppk", "ffl", "bfl", "enp_dist", "enp_radius",
                 "exp_dist", "exp_radius", "img_dist", "obj_dist", "m", "opt_inv")}

    surfaces = lens["surfaces"]
    infinite = math.isinf(surfaces[0]["thickness"])
    start_gap = START_GAP if infinite else surfaces[0]["thickness"]
    path = list(sm.path(wl=WVL_NM))
    ifc0, gap0, (rot0, _), n0, zdir0 = path[0]
    path[0] = (ifc0, gap0, (rot0, np.array([0.0, 0.0, start_gap])), n0, zdir0)

    fmax = max(abs(f) for f in lens["fields"])
    r = fod.enp_radius
    out_rays = []
    for hx, hy, px, py in lenses.rays(lens):
        pupil = np.array([px * r, py * r, fod.enp_dist])
        if infinite:
            u = math.radians(hy * fmax)
            ux = math.radians(hx * fmax)
            d = np.array([math.tan(ux), math.tan(u), 1.0])
            d /= np.linalg.norm(d)
            start = pupil - d * ((pupil[2] + start_gap) / d[2])
        else:
            start = np.array([hx * fmax, hy * fmax, -start_gap])
            d = pupil - start
            d /= np.linalg.norm(d)
        pt0 = start + np.array([0.0, 0.0, start_gap])       # object-interface coordinates
        ray, _, _ = rt.trace_raw(iter(path), pt0, d, WVL_NM, intersect_obj=False)
        per_surface = []
        opl = 0.0
        for j in range(1, len(ray)):
            opl += path[j - 1][3] * ray[j - 1][2]
            pt, after_dir = ray[j][0], ray[j][1]
            per_surface.append([float(pt[0]), float(pt[1]), float(pt[2]),
                                float(after_dir[0]), float(after_dir[1]), float(after_dir[2]), float(opl)])
        if len(per_surface) != len(surfaces) - 1:
            raise RuntimeError(f"{name}: ray {(hx, hy, px, py)} gave {len(per_surface)} surfaces")
        out_rays.append({"hx": hx, "hy": hy, "px": px, "py": py,
                         "launch": [float(v) for v in start] + [float(v) for v in d],
                         "surfaces": per_surface})

    doc = {
        "program": "rayoptics", "program_version": version("rayoptics"),
        "opticalglass_version": version("opticalglass"),
        "numpy_version": np.__version__, "python_version": platform.python_version(),
        "lens": name, "units": "mm", "wavelength_um": lenses.WAVELENGTH_UM,
        "surfaces": [{**s, "radius": finite_or_none(s["radius"]),
                      "thickness": finite_or_none(s["thickness"])} for s in surfaces],
        "epd": lens["epd"], "field_type": lens["field_type"], "fields": lens["fields"],
        "launch_frame": "surface 1 local frame; built by gen_rayoptics.py from RayOptics' paraxial entrance pupil",
        "paraxial": paraxial,
        "rays": out_rays,
    }
    out = os.path.join(out_dir, f"rayoptics_{name}.json")
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=1)
        fh.write("\n")
    print(f"wrote {out}: {len(out_rays)} rays")


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    for name, lens in lenses.LENSES.items():
        run(name, lens, out_dir)


if __name__ == "__main__":
    main()
