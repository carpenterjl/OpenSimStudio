"""Smith-chart calibration and marker reading for Sheen (1991) Fig. 4.15 (thesis p.163).
Usage: python -I smith.py <page.png> <markers.json> <out_prefix>
 1. Outer circle (|G|=1): radial profiles from an approximate centre every 1 deg; the outermost ink run within
    +-60 px of the approximate radius gives a boundary point (run centre). Algebraic (Kasa) circle fit with
    iterative 2.5-sigma rejection (markers/labels near the rim are rejected). Residual rms reported (px).
 2. Real axis: the horizontal diameter, centre row of ink in columns across the chart -> straight-line fit;
    its angle gives the rotation of the scan; its offset from the circle centre is reported.
 3. Checks: the r = 1/3, 1, 3 circles cross the real axis at G = -0.5, 0, +0.5 (r=1 passes through the centre):
    crossing positions are measured just above and below the axis and converted to G.
 4. Markers: 'o' -> centroid of the enclosed white hole; 'star'/'x' -> ink centroid inside a small window after
    removing pixels that belong to long chart lines (connected components larger than lmax px).
 G = ((x - xc) + j (yc - y)) / R, rotated by the real-axis angle. Z = Z0 (1+G)/(1-G), Z0 = 50 ohm assumed.
"""
import sys, json, math
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

img = np.asarray(Image.open(sys.argv[1]).convert("L"), dtype=float)
mks = json.load(open(sys.argv[2], encoding="utf-8-sig"))
outp = sys.argv[3]
ink = img < 128
H, W = img.shape
xc0, yc0, R0 = mks["approx_centre"]


def runs(mask):
    m = np.concatenate([[False], mask, [False]]).astype(int)
    d = np.diff(m)
    return list(zip(np.where(d == 1)[0], np.where(d == -1)[0] - 1))


# 1. outer circle
pts = []
for ang in np.arange(0, 360, 1.0):
    t = math.radians(ang)
    rr = np.arange(R0 - 60, R0 + 60, 0.5)
    xs = xc0 + rr * math.cos(t); ys = yc0 - rr * math.sin(t)
    v = ndimage.map_coordinates(img, [ys, xs], order=1) < 128
    rs = runs(v)
    if not rs:
        continue
    s, e = rs[-1]
    rm = rr[(s + e) // 2]
    pts.append((xc0 + rm * math.cos(t), yc0 - rm * math.sin(t), ang))
pts = np.array(pts)
keep = np.ones(len(pts), bool)
for it in range(8):
    x, y = pts[keep, 0], pts[keep, 1]
    A = np.vstack([x, y, np.ones_like(x)]).T
    b = x ** 2 + y ** 2
    c, *_ = np.linalg.lstsq(A, b, rcond=None)
    xc, yc = c[0] / 2, c[1] / 2
    R = math.sqrt(c[2] + xc ** 2 + yc ** 2)
    res = np.hypot(pts[:, 0] - xc, pts[:, 1] - yc) - R
    sd = np.std(res[keep])
    nk = np.abs(res) < max(2.5 * sd, 1.0)
    if np.array_equal(nk, keep):
        break
    keep = nk
circ = {"xc": xc, "yc": yc, "R": R, "resid_rms_px": float(np.std(res[keep])), "n_used": int(keep.sum()), "n": len(pts)}
print("outer circle", json.dumps(circ))

# 2. real axis (horizontal diameter)
ax_pts = []
for x in range(int(xc - 0.95 * R), int(xc + 0.6 * R), 3):
    col = ink[int(yc) - 25:int(yc) + 26, x]
    rs = runs(col)
    if not rs:
        continue
    s, e = min(rs, key=lambda r: abs((r[0] + r[1]) / 2 - 25))
    if e - s + 1 > 16:
        continue
    ax_pts.append((x, int(yc) - 25 + 0.5 * (s + e)))
ax_pts = np.array(ax_pts)
co = np.polyfit(ax_pts[:, 0], ax_pts[:, 1], 1)
rot = math.atan(co[0])          # image y grows downward: positive slope = axis rotated clockwise on paper
axis_off = float(np.polyval(co, xc) - yc)
print("real axis: slope %.6f (%.4f deg), offset at centre %.2f px, rms %.2f px" % (
    co[0], math.degrees(rot), axis_off, np.std(ax_pts[:, 1] - np.polyval(co, ax_pts[:, 0]))))


def gamma(px, py):
    dx, dy = (px - xc) / R, (yc - py) / R
    # undo rotation: axis direction on paper is (1, -slope) in (x, up) coords
    a = -math.atan(co[0]) * -1.0
    cr, sr = math.cos(rot), math.sin(rot)
    gx = dx * cr - dy * sr
    gy = dx * sr + dy * cr
    return complex(gx, gy)


# 3. r-circle crossings on the real axis (just above and below the axis)
checks = []
for off in (-14, 14):
    yy = np.polyval(co, np.arange(W)) + off
    prof = ndimage.map_coordinates(img, [yy, np.arange(W, dtype=float)], order=1)
    seg = np.arange(int(xc - 0.8 * R), int(xc + 0.7 * R))
    rs = runs(prof[seg] < 128)
    for s, e in rs:
        if e - s + 1 > 30:
            continue
        xm = seg[0] + 0.5 * (s + e)
        g = gamma(xm, np.polyval(co, xm))
        checks.append({"offset_px": off, "x_px": float(xm), "G_real": g.real})
print("r-circle crossings near the axis (G real):", ", ".join("%.4f" % c["G_real"] for c in checks))

# 4. markers
lab, n = ndimage.label(ink, structure=np.ones((3, 3)))
sizes = ndimage.sum(np.ones_like(lab), lab, index=np.arange(n + 1))
out = []
ov = Image.open(sys.argv[1]).convert("RGB")
dr = ImageDraw.Draw(ov)
for mk in mks["markers"]:
    ax, ay = mk["x"], mk["y"]
    w = mk.get("win", 32)
    x0, y0 = int(ax - w), int(ay - w)
    sub = ink[y0:y0 + 2 * w + 1, x0:x0 + 2 * w + 1]
    info = {}
    if mk["type"] == "o":
        lb, nn = ndimage.label(~sub)
        best = None
        for i in range(1, nn + 1):
            m = lb == i
            ys, xs = np.nonzero(m)
            if ys.min() == 0 or xs.min() == 0 or ys.max() == m.shape[0] - 1 or xs.max() == m.shape[1] - 1:
                continue
            a = m.sum()
            if not (mk.get("amin", 60) <= a <= mk.get("amax", 1500)):
                continue
            d = math.hypot(xs.mean() - w, ys.mean() - w)
            if best is None or d < best[0]:
                best = (d, xs.mean(), ys.mean(), a)
        if best is None:
            cx = cy = float("nan"); info["status"] = "no hole"
        else:
            cx, cy = best[1], best[2]; info.update(status="ok", hole_area_px=int(best[3]))
    else:
        labsub = lab[y0:y0 + 2 * w + 1, x0:x0 + 2 * w + 1]
        small = sub & (sizes[labsub] < mk.get("lmax", 3000))
        ys, xs = np.nonzero(small)
        if len(xs) == 0:
            cx = cy = float("nan"); info["status"] = "no small ink"
        else:
            cx, cy = xs.mean(), ys.mean(); info.update(status="ok", n_px=int(len(xs)))
    px, py = x0 + cx, y0 + cy
    g = gamma(px, py)
    z = 50 * (1 + g) / (1 - g)
    rec = dict(mk, x_px=float(px), y_px=float(py), G_re=g.real, G_im=g.imag, G_mag=abs(g),
               G_dB=20 * math.log10(abs(g)), R_ohm=z.real, X_ohm=z.imag, **info)
    out.append(rec)
    dr.ellipse([px - 6, py - 6, px + 6, py + 6], outline=(255, 0, 0), width=2)
    dr.rectangle([x0, y0, x0 + 2 * w, y0 + 2 * w], outline=(0, 160, 0))
    print("%-10s %-4s px(%.1f,%.1f) G=%.4f%+.4fj |G|=%.4f (%.2f dB) Z(50)=%.1f%+.1fj %s" % (
        mk["name"], mk["type"], px, py, g.real, g.imag, abs(g), 20 * math.log10(abs(g)), z.real, z.imag, json.dumps(info)))
dr.ellipse([xc - R, yc - R, xc + R, yc + R], outline=(0, 0, 255))
dr.ellipse([xc, yc - R / 2, xc + R, yc + R / 2], outline=(0, 0, 255))
json.dump({"circle": circ, "axis_slope": co[0], "axis_offset_px": axis_off, "r_checks": checks, "markers": out},
          open(outp + "_smith.json", "w"), indent=1)
ov.crop((int(xc - R - 60), int(yc - R - 60), int(xc + R + 60), int(yc + R + 60))).save(outp + "_smith_overlay.png")
