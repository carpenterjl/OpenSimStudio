"""Reference thin-film values from tmm (S. Byrnes, MIT licence) for OP-102.

Run once, outside the test suite, in the venv that holds tmm (README.md, "Regenerating"):

    python -I gen_tmm.py <output folder>

Writes tmm_stacks.json: for each stack below, at each wavelength and angle of incidence,
tmm.coh_tmm's complex amplitudes r and t, R, T and power_entering for s and p light. The
stacks are arbitrary test inputs (not published coatings): a single quarter-wave-like layer,
a 9-layer high/low stack, an absorbing metal layer between dielectrics on an absorbing
substrate, and light from glass into air beyond the critical angle (total internal
reflection). Thicknesses and wavelengths are in nanometres; indices n + ik with k >= 0
absorbing, tmm's convention (docs/Optics-Conventions.md section 12).
"""

import json
import math
import os
import platform
import sys

import numpy as np
import tmm
from importlib.metadata import version

STACKS = {
    "single_layer": {"incident": 1.0, "layers": [(1.38, 0.0, 99.6)], "substrate": (1.52, 0.0),
                     "wavelengths": [450.0, 550.0, 700.0], "angles": [0.0, 30.0, 60.0, 80.0]},
    "hl_9": {"incident": 1.0,
             "layers": [(2.35, 0.0, 58.5) if i % 2 == 0 else (1.46, 0.0, 94.2) for i in range(9)],
             "substrate": (1.52, 0.0),
             "wavelengths": [480.0, 550.0, 620.0], "angles": [0.0, 20.0, 45.0, 70.0]},
    "absorbing": {"incident": 1.0,
                  "layers": [(1.46, 0.0, 70.0), (0.13, 3.3, 12.0), (2.1, 0.02, 55.0)],
                  "substrate": (3.9, 0.02),
                  "wavelengths": [500.0, 633.0], "angles": [0.0, 35.0, 65.0]},
    "glass_to_air": {"incident": 1.5, "layers": [(1.38, 0.0, 110.0)], "substrate": (1.0, 0.0),
                     "wavelengths": [550.0], "angles": [0.0, 30.0, 41.0, 50.0, 70.0]},
}


def cplx(z):
    z = complex(z)
    return [z.real, z.imag]


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    cases = []
    for name, st in STACKS.items():
        n_list = [st["incident"]] + [complex(n, k) for n, k, _ in st["layers"]] + [complex(*st["substrate"])]
        d_list = [math.inf] + [d for _, _, d in st["layers"]] + [math.inf]
        for lam in st["wavelengths"]:
            for deg in st["angles"]:
                row = {"stack": name, "wavelength_nm": lam, "angle_deg": deg}
                for pol in "sp":
                    res = tmm.coh_tmm(pol, n_list, d_list, deg * math.pi / 180, lam)
                    row[pol] = {"r": cplx(res["r"]), "t": cplx(res["t"]), "R": float(res["R"]),
                                "T": float(res["T"]), "power_entering": float(res["power_entering"])}
                cases.append(row)
    doc = {
        "program": "tmm", "program_version": version("tmm"), "numpy_version": np.__version__,
        "python_version": platform.python_version(),
        "function": "tmm.coh_tmm(pol, n_list, d_list, angle_rad, lam_vac)",
        "units": "nm",
        "stacks": {k: {"incident": v["incident"], "layers": [list(l) for l in v["layers"]],
                       "substrate": list(v["substrate"])} for k, v in STACKS.items()},
        "cases": cases,
    }
    path = os.path.join(out_dir, "tmm_stacks.json")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=1)
        fh.write("\n")
    print(f"wrote {path}: {len(cases)} cases")


if __name__ == "__main__":
    main()
