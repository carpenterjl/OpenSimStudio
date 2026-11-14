"""The reference lenses of OP-102, defined once for every generator script.

Lengths are millimetres, the wavelength micrometres. Every radius, thickness, conic constant
and index below is an arbitrary test input chosen for this check (not a published design and
not reference data): the reference values are what the independent programs compute for these
inputs. All media are constant indices, so no dispersion formula is involved and the
wavelength enters only through the conversion of optical path to waves.

Surface list: entry 0 is the object (its thickness the object distance, math.inf for an object
at infinity), the last entry is the image surface. ``index`` is the medium after the surface
(None on a mirror, where the medium after is the one before). Radii use OpticStudio's sign
(positive when the centre of curvature lies toward +z of the vertex), thicknesses are negative
after an odd number of mirrors; both programs and OpenSim use the same rule
(docs/Optics-Conventions.md section 3; the README records where each program states it).
"""

import math

INF = math.inf
WAVELENGTH_UM = 0.55

# Normalised pupil points traced at every field: the chief ray, the four rim rays of the
# meridional and sagittal fans, and skew rays inside the pupil.
PUPIL = [(0.0, 0.0), (0.0, 1.0), (0.0, -1.0), (1.0, 0.0), (0.0, 0.5),
         (0.6, 0.6), (-0.3, 0.8), (0.7, -0.4)]


def S(radius=INF, thickness=0.0, index=1.0, conic=0.0, stop=False, mirror=False):
    return {"radius": radius, "conic": conic, "thickness": thickness,
            "index": None if mirror else index, "stop": stop, "mirror": mirror}


LENSES = {
    # Biconvex singlet, prolate-ellipsoid front, stop on the front surface, object at infinity.
    "singlet": {
        "surfaces": [S(thickness=INF),
                     S(50.0, 6.0, 1.5168, conic=-0.6, stop=True),
                     S(-200.0, 73.0),
                     S()],
        "epd": 20.0, "field_type": "angle", "fields": [0.0, 3.5, 7.0],
    },
    # Cemented doublet at a finite conjugate (object 250 mm in front), object-height fields.
    "doublet_finite": {
        "surfaces": [S(thickness=250.0),
                     S(60.0, 7.0, 1.5168, stop=True),
                     S(-45.0, 3.0, 1.6727),
                     S(-150.0, 160.0),
                     S()],
        "epd": 18.0, "field_type": "object_height", "fields": [0.0, 10.0, 20.0],
    },
    # Cooke-triplet-like layout with a separate plane stop between the second and third
    # elements and an oblate-ellipsoid last surface; object at infinity.
    "triplet": {
        "surfaces": [S(thickness=INF),
                     S(22.0, 3.5, 1.62),
                     S(-400.0, 4.0),
                     S(-21.0, 1.2, 1.60),
                     S(20.0, 2.0),
                     S(INF, 2.0, stop=True),
                     S(80.0, 3.0, 1.62),
                     S(-18.0, 42.0, conic=0.5),
                     S()],
        "epd": 10.0, "field_type": "angle", "fields": [0.0, 10.0, 15.0],
    },
    # Concave paraboloidal mirror, image surface at its focus, object at infinity.
    "parabola": {
        "surfaces": [S(thickness=INF),
                     S(-200.0, -100.0, conic=-1.0, stop=True, mirror=True),
                     S()],
        "epd": 50.0, "field_type": "angle", "fields": [0.0, 1.0, 2.0],
    },
    # Two-mirror (Cassegrain-like) layout: paraboloidal primary, hyperboloidal convex
    # secondary, negative thickness between them, image back past the primary's vertex.
    "two_mirror": {
        "surfaces": [S(thickness=INF),
                     S(-400.0, -150.0, conic=-1.0, stop=True, mirror=True),
                     S(-150.0, 180.0, conic=-3.0, mirror=True),
                     S()],
        "epd": 60.0, "field_type": "angle", "fields": [0.0, 0.3, 0.5],
    },
}


def rays(lens):
    """(hx, hy, px, py) of every traced ray: each field (normalised by the largest) at every
    pupil point."""
    fields = lens["fields"]
    fmax = max(abs(f) for f in fields)
    out = []
    for f in fields:
        hy = f / fmax if fmax else 0.0
        for px, py in PUPIL:
            out.append((0.0, hy, px, py))
    return out


def stop_index(lens):
    return next(i for i, s in enumerate(lens["surfaces"]) if s["stop"])


def media(lens):
    """The index of the space after each surface (a mirror keeps the one before)."""
    out = []
    for i, s in enumerate(lens["surfaces"]):
        out.append(out[i - 1] if s["mirror"] else s["index"])
    return out
