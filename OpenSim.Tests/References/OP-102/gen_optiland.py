"""Reference ray data and first-order data from Optiland (MIT licence) for the OP-102 lenses.

Run once, outside the test suite, in the venv that holds Optiland (README.md, "Regenerating"):

    python -I gen_optiland.py <output folder>

For each lens in lenses.py it writes optiland_<lens>.json: the lens as given, Optiland's
paraxial data under Optiland's own names, and for every (field, pupil) ray of lenses.rays()
the launch ray Optiland's paraxial ray aimer generates and the ray at every surface from
surface 1 to the image: local position (vertex at the origin), direction cosines after the
surface, and the optical path accumulated from the launch point. Lengths are millimetres.
JSON null stands for an infinite radius or object distance.
"""

import json
import math
import os
import platform
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lenses  # noqa: E402

import numpy as np  # noqa: E402
import optiland  # noqa: E402
from optiland import optic  # noqa: E402
from optiland.materials import IdealMaterial  # noqa: E402
from importlib.metadata import version  # noqa: E402


def finite_or_none(v):
    return None if math.isinf(v) else v


def build(lens):
    op = optic.Optic()
    for i, s in enumerate(lens["surfaces"]):
        last = i == len(lens["surfaces"]) - 1
        kwargs = dict(index=i, radius=s["radius"], thickness=s["thickness"])
        if s["conic"] != 0.0:
            kwargs["conic"] = s["conic"]
        if last:
            op.surfaces.add(index=i)
            continue
        if s["mirror"]:
            material = "mirror"
        elif s["index"] == 1.0:
            material = "air"
        else:
            material = IdealMaterial(n=s["index"], k=0.0)
        op.surfaces.add(is_stop=s["stop"], material=material, **kwargs)
    op.set_aperture(aperture_type="EPD", value=lens["epd"])
    op.fields.set_type("angle" if lens["field_type"] == "angle" else "object_height")
    for f in lens["fields"]:
        op.fields.add(y=f)
    op.wavelengths.add(value=lenses.WAVELENGTH_UM, is_primary=True)
    return op


def f(v):
    return float(np.asarray(v).reshape(-1)[0])


def run(name, lens, out_dir):
    op = build(lens)
    surfaces = op.surfaces.surfaces
    z = [f(s.geometry.cs.z) for s in surfaces]
    z1 = z[1]
    p = op.paraxial
    paraxial = {
        "f1": f(p.f1()), "f2": f(p.f2()), "F1": f(p.F1()), "F2": f(p.F2()),
        "P1": f(p.P1()), "P2": f(p.P2()), "EPL": f(p.EPL()), "EPD": f(p.EPD()),
        "XPL": f(p.XPL()), "XPD": f(p.XPD()),
    }
    out_rays = []
    for hx, hy, px, py in lenses.rays(lens):
        rays = op.ray_tracer.ray_generator.generate_rays(
            hx, hy, np.array([px]), np.array([py]), lenses.WAVELENGTH_UM)
        launch = [f(rays.x), f(rays.y), f(rays.z) - z1, f(rays.L), f(rays.M), f(rays.N)]
        op.surfaces.trace(rays)
        per_surface = []
        for j in range(1, len(surfaces)):
            s = surfaces[j]
            per_surface.append([f(s.x), f(s.y), f(s.z) - z[j], f(s.L), f(s.M), f(s.N), f(s.opd)])
        if any(not math.isfinite(v) for row in per_surface for v in row):
            raise RuntimeError(f"{name}: ray {(hx, hy, px, py)} failed in Optiland")
        out_rays.append({"hx": hx, "hy": hy, "px": px, "py": py,
                         "launch": launch, "surfaces": per_surface})

    doc = {
        "program": "optiland", "program_version": version("optiland"),
        "numpy_version": np.__version__, "python_version": platform.python_version(),
        "lens": name, "units": "mm", "wavelength_um": lenses.WAVELENGTH_UM,
        "surfaces": [{**s, "radius": finite_or_none(s["radius"]),
                      "thickness": finite_or_none(s["thickness"])} for s in lens["surfaces"]],
        "epd": lens["epd"], "field_type": lens["field_type"], "fields": lens["fields"],
        "launch_frame": "surface 1 local frame; Optiland's paraxial ray aimer (default)",
        "paraxial": paraxial,
        "rays": out_rays,
    }
    path = os.path.join(out_dir, f"optiland_{name}.json")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=1)
        fh.write("\n")
    print(f"wrote {path}: {len(out_rays)} rays")


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    for name, lens in lenses.LENSES.items():
        run(name, lens, out_dir)


if __name__ == "__main__":
    main()
