"""Locate plot markers ('o' open circles, '*' filled stars, 'x' crosses) near approximate DATA positions.
Usage: python -I markers.py <cfg.json> <calib.json> <markers.json> <out_prefix>
markers.json: [{"name":..., "type":"o"|"star"|"x", "X":approx, "Y":approx, "win":px(optional)}, ...]
Methods
 o    : the enclosed white hole of the ring (white connected component not touching the search window border,
        area in [amin, amax]) -> its centroid. Radius of the hole reported.
 star : per-column vertical ink run through the window centre row band; the local line thickness is the median
        run height at the window edges (0 if no line). Star columns = run height > line + 6 px. Centre x = mid of the
        first/last star column; centre y = mean of run mid-points over the central star columns.
 x    : centroid of ink in the window after removing long line runs (fallback: ink centroid).
Data values from the affine tick calibration (calib.json)."""
import sys, json, math
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

cfg = json.load(open(sys.argv[1], encoding="utf-8-sig"))
cal = json.load(open(sys.argv[2], encoding="utf-8-sig"))
mks = json.load(open(sys.argv[3], encoding="utf-8-sig"))
outp = sys.argv[4]
img = np.asarray(Image.open(cfg["image"]).convert("L"), dtype=float)
P, Q = cal["P"], cal["Q"]
M = np.array([[P[1], P[2]], [Q[1], Q[2]]])


def d2p(X, Y):
    return np.linalg.solve(M, [X - P[0], Y - Q[0]])


def p2d(x, y):
    return P[0] + P[1] * x + P[2] * y, Q[0] + Q[1] * x + Q[2] * y


def runs(mask):
    m = np.concatenate([[False], mask, [False]]).astype(int)
    d = np.diff(m)
    return list(zip(np.where(d == 1)[0], np.where(d == -1)[0] - 1))


res = []
ov = Image.open(cfg["image"]).convert("RGB")
dr = ImageDraw.Draw(ov)
for mk in mks:
    ax, ay = d2p(mk["X"], mk["Y"])
    w = mk.get("win", 40)
    x0, y0 = int(ax - w), int(ay - w)
    sub = img[y0:y0 + 2 * w + 1, x0:x0 + 2 * w + 1]
    ink = sub < 128
    info = {}
    if mk["type"] == "o":
        lab, n = ndimage.label(~ink)
        best = None
        for i in range(1, n + 1):
            m = lab == i
            ys, xs = np.nonzero(m)
            if ys.min() == 0 or xs.min() == 0 or ys.max() == m.shape[0] - 1 or xs.max() == m.shape[1] - 1:
                continue
            a = m.sum()
            if not (mk.get("amin", 80) <= a <= mk.get("amax", 1500)):
                continue
            cx, cy = xs.mean(), ys.mean()
            d = math.hypot(cx - w, cy - w)
            if best is None or d < best[0]:
                best = (d, cx, cy, a)
        if best is None:
            info["status"] = "no enclosed hole"
            cx, cy = float("nan"), float("nan")
        else:
            _, cx, cy, a = best
            info.update(status="ok", hole_area_px=int(a), hole_r_px=math.sqrt(a / math.pi))
    elif mk["type"] == "star":
        band = mk.get("band", 30)
        cols = []
        for j in range(ink.shape[1]):
            rr = [r for r in runs(ink[:, j]) if r[1] >= w - band and r[0] <= w + band]
            if rr:
                r = max(rr, key=lambda r: r[1] - r[0])
                cols.append((j, r[0], r[1], r[1] - r[0] + 1))
            else:
                cols.append((j, -1, -1, 0))
        hts = np.array([c[3] for c in cols])
        edge = np.concatenate([hts[:8], hts[-8:]])
        lt = float(np.median(edge)) if mk.get("line", True) else 0.0
        star = [c for c in cols if c[3] > lt + mk.get("excess", 6)]
        # keep the contiguous group nearest the centre
        if star:
            js = np.array([c[0] for c in star])
            groups = np.split(js, np.where(np.diff(js) > 2)[0] + 1)
            g = min(groups, key=lambda g: abs(g.mean() - w))
            jl, jr = g.min(), g.max()
            cx = 0.5 * (jl + jr)
            cen = [c for c in cols if abs(c[0] - cx) <= mk.get("cw", 4) and c[3] > 0]
            cy = float(np.mean([0.5 * (c[1] + c[2]) for c in cen]))
            info.update(status="ok", line_thick_px=lt, star_w_px=int(jr - jl + 1),
                        star_h_px=int(max(c[3] for c in cen)))
        else:
            cx, cy = float("nan"), float("nan"); info["status"] = "no star columns"
    else:
        ys, xs = np.nonzero(ink)
        wts = (255 - sub[ys, xs])
        cx, cy = float(np.sum(xs * wts) / np.sum(wts)), float(np.sum(ys * wts) / np.sum(wts))
        info["status"] = "ink centroid"
    px, py = x0 + cx, y0 + cy
    X, Y = p2d(px, py)
    res.append(dict(mk, x_px=float(px), y_px=float(py), X_found=float(X), Y_found=float(Y), **info))
    col = (255, 0, 0) if mk["type"] == "o" else ((0, 160, 0) if mk["type"] == "star" else (0, 0, 255))
    if not math.isnan(px):
        dr.line([px - 12, py, px + 12, py], fill=col, width=2); dr.line([px, py - 12, px, py + 12], fill=col, width=2)
    dr.rectangle([x0, y0, x0 + 2 * w, y0 + 2 * w], outline=col)
    print("%-14s %-4s approx(%.4f,%8.2f) found(%.4f,%8.2f) px(%.1f,%.1f) %s" % (
        mk["name"], mk["type"], mk["X"], mk["Y"], X, Y, px, py, json.dumps(info)))
json.dump(res, open(outp + "_markers.json", "w"), indent=1)
bx0, by0, bx1, by1 = cfg["box"]
ov.crop((bx0 - 40, by0 - 40, bx1 + 40, by1 + 40)).save(outp + "_markers_overlay.png")
