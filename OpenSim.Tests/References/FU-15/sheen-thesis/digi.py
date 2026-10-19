"""Tick-calibrated curve digitiser for the Sheen (1991) thesis plots (framed plots, inward tick marks, no rulings).

Usage:
  python -I digi.py <config.json> <outdir> calib        -> calibration only + data-grid view for picking seeds
  python -I digi.py <config.json> <outdir> track        -> calibration + curve tracking + overlays + json

Calibration (no grid rulings exist in these plots, so the tick marks are the reference):
 1. The four frame lines are located with sub-pixel accuracy: along each line, every 4 px, the centroid of the
    dark run nearest the line is taken and a robust straight line is fitted (x = a + b*y for the vertical sides,
    y = a + b*x for the horizontal ones). This gives the rotation/skew of the scanned page.
 2. Inward tick marks are found by sampling the darkness along a path parallel to each frame line, at an inward
    offset band [o0, o1] px (inside the frame line, shorter than the minor tick length): every tick gives a peak;
    its centroid is the tick position; it is assigned to the nearest expected tick value.
 3. Two independent linear (affine) inverse maps are fitted by least squares:
       X(value along x) = p0 + p1*x_px + p2*y_px      (from the ticks on the bottom AND top axes)
       Y(value along y) = q0 + q1*x_px + q2*y_px      (from the ticks on the left AND right axes)
    The p2, q1 terms carry the skew. Residuals are reported in data units (rms, max).
Ink maps:  D = darkness (0..1). The frame lines (+-hw px) and user mask rectangles are blanked. Connected
    components of D>0.5 are split into LONG (max bbox side >= long_len px: the continuous solid curve) and SHORT
    (dashes / dots / markers). Map for a curve = w_long*D_long + w_short*D_short (per-curve weights).
Tracking: globally optimal ridge path (Viterbi) inside a corridor of half-width W px around the seed polyline
    (seeds given in DATA units), 1 px arc-length steps, smoothness penalty lam*(dt)^2, max lateral step maxstep.
    Flags: 0 = on ink (S > dthr), 1 = bridged a gap (no ink), 2 = ran along the corridor edge (suspect).
"""
import sys, os, json, math
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

cfg_path, outdir, mode = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(cfg_path, encoding="utf-8-sig"))
os.makedirs(outdir, exist_ok=True)
fig = cfg["figure"]
img = np.asarray(Image.open(cfg["image"]).convert("L"), dtype=float)
H, Wd = img.shape
D0 = np.clip((255.0 - img) / 255.0, 0, 1)
try:
    FONT = ImageFont.truetype("arial.ttf", 16)
except Exception:
    FONT = ImageFont.load_default()


def runs(mask):
    """start,end (inclusive) of True runs in a 1-D bool array"""
    m = np.concatenate([[False], mask, [False]]).astype(int)
    d = np.diff(m)
    s = np.where(d == 1)[0]
    e = np.where(d == -1)[0] - 1
    return list(zip(s, e))


def robust_line(t, u, it=6, k=2.5):
    t = np.asarray(t, float); u = np.asarray(u, float)
    keep = np.ones(len(t), bool)
    for _ in range(it):
        A = np.vstack([np.ones(keep.sum()), t[keep]]).T
        co, *_ = np.linalg.lstsq(A, u[keep], rcond=None)
        r = u - (co[0] + co[1] * t)
        sd = max(np.std(r[keep]), 0.3)
        nk = np.abs(r) < k * sd
        if np.array_equal(nk, keep):
            break
        keep = nk
    return co, float(np.std(r[keep])), int(keep.sum()), len(t)


def frame_line(side, approx, lo, hi, search=25, maxw=16):
    """side in left/right (vertical) or top/bottom (horizontal). approx: approx px of line; lo..hi range along it"""
    ts, us = [], []
    for t in range(int(lo), int(hi), 4):
        if side in ("left", "right"):
            a = int(round(approx)); seg = D0[t, a - search:a + search + 1]; base = a - search
        else:
            a = int(round(approx)); seg = D0[a - search:a + search + 1, t]; base = a - search
        rr = [(s, e) for s, e in runs(seg > 0.5) if e - s + 1 <= maxw]
        if not rr:
            continue
        cen = [base + s + (np.sum(np.arange(s, e + 1) * seg[s:e + 1]) / np.sum(seg[s:e + 1]) - s) for s, e in rr]
        c = min(cen, key=lambda c: abs(c - approx))
        if abs(c - approx) < search:
            ts.append(t); us.append(c)
    co, sd, nk, n = robust_line(ts, us)
    return {"a": float(co[0]), "b": float(co[1]), "sd_px": sd, "n_used": nk, "n": n}


def line_pos(L, t):
    return L["a"] + L["b"] * t


def tick_profile(side, L, lo, hi, o0, o1):
    """darkness profile along frame line 'side' sampled at inward offsets o0..o1"""
    ts = np.arange(int(lo), int(hi), 1.0)
    offs = np.arange(o0, o1 + 1e-9, 1.0)
    sgn = {"left": +1, "right": -1, "top": +1, "bottom": -1}[side]
    acc = np.zeros(len(ts))
    for o in offs:
        if side in ("left", "right"):
            xs = line_pos(L, ts) + sgn * o; ys = ts
        else:
            ys = line_pos(L, ts) + sgn * o; xs = ts
        acc += ndimage.map_coordinates(D0, [ys, xs], order=1, mode="nearest")
    return ts, acc / len(offs)


def find_ticks(side, L, axcfg, lo, hi):
    o0, o1 = axcfg.get("band", [7, 15])
    ts, p = tick_profile(side, L, lo, hi, o0, o1)
    thr = axcfg.get("thr", 0.45)
    v0, p0 = axcfg["first"]   # value, approx px (along the side)
    v1, p1 = axcfg["last"]
    step = axcfg["step"]
    sp = (p1 - p0) / (v1 - v0) * step
    out = []
    for s, e in runs(p > thr):
        if e - s + 1 > axcfg.get("maxw", 18) or e - s + 1 < 2:
            continue
        w = p[s:e + 1]
        c = float(np.sum(ts[s:e + 1] * w) / np.sum(w))
        val = v0 + (c - p0) / (p1 - p0) * (v1 - v0)
        k = round((val - v0) / step)
        vexp = v0 + k * step
        pexp = p0 + (vexp - v0) / (v1 - v0) * (p1 - p0)
        if abs(c - pexp) < axcfg.get("tol", 0.3) * abs(sp) and vexp not in axcfg.get("exclude", []):
            out.append((float(vexp), c, float(np.max(w)), int(e - s + 1)))
    # keep best per value
    best = {}
    for v, c, m, wdt in out:
        if v not in best or m > best[v][1]:
            best[v] = (c, m, wdt)
    return [(v, best[v][0], best[v][1], best[v][2]) for v in sorted(best)], (ts, p)


# ---------------- calibration
bx0, by0, bx1, by1 = cfg["box"]
FR = {
    "left": frame_line("left", bx0, by0 + 30, by1 - 30),
    "right": frame_line("right", bx1, by0 + 30, by1 - 30),
    "top": frame_line("top", by0, bx0 + 30, bx1 - 30),
    "bottom": frame_line("bottom", by1, bx0 + 30, bx1 - 30),
}
ticks = {"x": [], "y": []}
profiles = {}
for side in cfg["xticks"]["sides"]:
    L = FR[side]
    tk, prof = find_ticks(side, L, cfg["xticks"], bx0 + 12, bx1 - 12)
    profiles[side] = prof
    for v, c, m, w in tk:
        ticks["x"].append({"side": side, "value": v, "x": c, "y": line_pos(L, c), "peak": m, "width": w})
for side in cfg["yticks"]["sides"]:
    L = FR[side]
    tk, prof = find_ticks(side, L, cfg["yticks"], by0 + 12, by1 - 12)
    profiles[side] = prof
    for v, c, m, w in tk:
        if [side, v] in cfg["yticks"].get("exclude_pairs", []):
            continue
        ticks["y"].append({"side": side, "value": v, "x": line_pos(L, c), "y": c, "peak": m, "width": w})


def fit_inv(tl):
    A = np.array([[1.0, t["x"], t["y"]] for t in tl])
    v = np.array([t["value"] for t in tl])
    co, *_ = np.linalg.lstsq(A, v, rcond=None)
    r = v - A @ co
    return co, r


P, rx = fit_inv(ticks["x"])
Q, ry = fit_inv(ticks["y"])
for t, r in zip(ticks["x"], rx):
    t["resid"] = float(r)
for t, r in zip(ticks["y"], ry):
    t["resid"] = float(r)


def px2d(x, y):
    return P[0] + P[1] * x + P[2] * y, Q[0] + Q[1] * x + Q[2] * y


Minv = np.array([[P[1], P[2]], [Q[1], Q[2]]])


def d2px(X, Y):
    X = np.atleast_1d(np.asarray(X, float)); Y = np.atleast_1d(np.asarray(Y, float))
    rhs = np.vstack([X - P[0], Y - Q[0]])
    sol = np.linalg.solve(Minv, rhs)
    return sol[0], sol[1]


# frame lines mapped to data (consistency check)
mid_y = 0.5 * (by0 + by1); mid_x = 0.5 * (bx0 + bx1)
frame_vals = {
    "left_X": float(px2d(line_pos(FR["left"], mid_y), mid_y)[0]),
    "right_X": float(px2d(line_pos(FR["right"], mid_y), mid_y)[0]),
    "top_Y": float(px2d(mid_x, line_pos(FR["top"], mid_x))[1]),
    "bottom_Y": float(px2d(mid_x, line_pos(FR["bottom"], mid_x))[1]),
}
xpx_per_unit = 1.0 / math.hypot(P[1], P[2])
ypx_per_unit = 1.0 / math.hypot(Q[1], Q[2])
calib = {
    "X_of_px": "X = %.6f + %.8f*x + %.8f*y" % tuple(P),
    "Y_of_px": "Y = %.6f + %.8f*x + %.8f*y" % tuple(Q),
    "P": list(map(float, P)), "Q": list(map(float, Q)),
    "x_px_per_unit": xpx_per_unit, "y_px_per_unit": ypx_per_unit,
    "n_xticks": len(ticks["x"]), "n_yticks": len(ticks["y"]),
    "x_resid_rms": float(np.sqrt(np.mean(rx ** 2))), "x_resid_max": float(np.max(np.abs(rx))),
    "y_resid_rms": float(np.sqrt(np.mean(ry ** 2))), "y_resid_max": float(np.max(np.abs(ry))),
    "frame": FR, "frame_values": frame_vals, "ticks": ticks,
}
print(json.dumps({k: v for k, v in calib.items() if k not in ("ticks", "frame")}, indent=1))
print("frame", json.dumps(FR))
for ax in ("x", "y"):
    print(ax, "ticks:", " ".join("%s:%g(%+.3f)" % (t["side"][0], t["value"], t["resid"]) for t in ticks[ax]))

# ---------------- ink maps
D = D0.copy()
blank = np.zeros_like(D, bool)
yy, xx = np.mgrid[0:H, 0:Wd]
hw = cfg.get("frame_hw", 7)
fb = cfg.get("frame_blank_extent", 60)
for side, L in FR.items():
    if side in ("left", "right"):
        xl = L["a"] + L["b"] * yy
        blank |= (np.abs(xx - xl) <= hw) & (yy > by0 - fb) & (yy < by1 + fb)
    else:
        yl = L["a"] + L["b"] * xx
        blank |= (np.abs(yy - yl) <= hw) & (xx > bx0 - fb) & (xx < bx1 + fb)
# tick marks: blank small rectangles inward of each detected tick
tl_len = cfg.get("tick_blank_len", 0)
if tl_len:
    for t in ticks["x"]:
        sgn = -1 if t["side"] == "bottom" else 1
        y_a = t["y"]; y_b = t["y"] + sgn * tl_len
        blank[int(min(y_a, y_b)):int(max(y_a, y_b)) + 1, int(t["x"] - 7):int(t["x"] + 8)] = True
    for t in ticks["y"]:
        sgn = 1 if t["side"] == "left" else -1
        x_a = t["x"]; x_b = t["x"] + sgn * tl_len
        blank[int(t["y"] - 7):int(t["y"] + 8), int(min(x_a, x_b)):int(max(x_a, x_b)) + 1] = True
for r in cfg.get("masks", []):
    blank[r[1]:r[3], r[0]:r[2]] = True
# outside the frame
inside = (xx > FR["left"]["a"] + FR["left"]["b"] * yy) & (xx < FR["right"]["a"] + FR["right"]["b"] * yy) & \
         (yy > FR["top"]["a"] + FR["top"]["b"] * xx) & (yy < FR["bottom"]["a"] + FR["bottom"]["b"] * xx)
D[blank | ~inside] = 0.0
lab, nl = ndimage.label(D > 0.5, structure=np.ones((3, 3)))
sl = ndimage.find_objects(lab)
long_len = cfg.get("long_len", 150)
is_long = np.zeros(nl + 1, bool)
for i, s in enumerate(sl, start=1):
    if s is None:
        continue
    h = s[0].stop - s[0].start; w = s[1].stop - s[1].start
    if max(h, w) >= long_len:
        is_long[i] = True
# assign every pixel with D>0.05 to the nearest labelled component class (dilate the labels a little)
lab_d = ndimage.grey_dilation(lab, size=(5, 5))
LONG = is_long[lab_d] & (D > 0.02)
D_long = np.where(LONG, D, 0.0)
D_short = np.where(~LONG, D, 0.0)
sig = cfg.get("ink_sigma", 0.8)
D_long = ndimage.gaussian_filter(D_long, sig)
D_short = ndimage.gaussian_filter(D_short, sig)
Dall = ndimage.gaussian_filter(D, sig)

# ---------------- views
pad = 30


def crop_view(name, curves_pts=None, grid=True):
    x0 = int(bx0 - pad); y0 = int(by0 - pad); x1 = int(bx1 + pad); y1 = int(by1 + pad)
    base = Image.fromarray(img.astype(np.uint8)).convert("RGB")
    arr = np.asarray(base).copy()
    if curves_pts is None:
        # colour the long/short classification lightly
        arr[LONG & (D > 0.3)] = (np.array([60, 60, 60])).astype(np.uint8)
        sm = (~LONG) & (D > 0.3)
        arr[sm] = np.array([120, 120, 255], np.uint8)
    else:
        arr = (155 + arr.astype(float) * 100 / 255).astype(np.uint8)   # lighten the ink
    base = Image.fromarray(arr)
    dr = ImageDraw.Draw(base)
    if grid:
        g = cfg.get("view_grid", {})
        gx = g.get("x", [cfg["xticks"]["first"][0], cfg["xticks"]["last"][0], cfg["xticks"]["step"] / 2])
        gy = g.get("y", [cfg["yticks"]["first"][0], cfg["yticks"]["last"][0], cfg["yticks"]["step"] / 2])
        X0, X1 = min(gx[0], gx[1]), max(gx[0], gx[1])
        Y0, Y1 = min(gy[0], gy[1]), max(gy[0], gy[1])
        ylo, yhi = cfg.get("y_range", [Y0, Y1]); xlo, xhi = cfg.get("x_range", [X0, X1])
        lab_x_at = g.get("xlab_at", list(np.linspace(ylo, yhi, 5)))
        lab_y_at = g.get("ylab_at", list(np.linspace(xlo, xhi, 6)))
        for xv in np.arange(xlo, xhi + 1e-9, gx[2]):
            a = d2px([xv, xv], [ylo, yhi])
            dr.line([(a[0][0], a[1][0]), (a[0][1], a[1][1])], fill=(255, 150, 150), width=1)
            for yl in lab_x_at:
                b = d2px([xv], [yl])
                dr.text((b[0][0] + 2, b[1][0] + 2), "%g" % xv, fill=(220, 0, 0), font=FONT)
        for yv in np.arange(ylo, yhi + 1e-9, gy[2]):
            a = d2px([xlo, xhi], [yv, yv])
            dr.line([(a[0][0], a[1][0]), (a[0][1], a[1][1])], fill=(150, 150, 255), width=1)
            for xl in lab_y_at:
                b = d2px([xl], [yv])
                dr.text((b[0][0] + 4, b[1][0] - 18), "%g" % yv, fill=(0, 0, 220), font=FONT)
    for t in ticks["x"] + ticks["y"]:
        dr.ellipse([t["x"] - 4, t["y"] - 4, t["x"] + 4, t["y"] + 4], outline=(0, 200, 0), width=2)
    cols = [(230, 0, 0), (0, 150, 0), (255, 120, 0), (200, 0, 200), (0, 160, 160), (120, 60, 0)]
    if curves_pts:
        for ci, (pts, flags, seeds) in enumerate(curves_pts):
            col = cols[ci % len(cols)]
            for i in range(len(pts) - 1):
                f = flags[i]
                c = col if f == 0 else ((230, 170, 0) if f == 1 else (0, 200, 255))
                dr.line([tuple(pts[i]), tuple(pts[i + 1])], fill=c, width=2)
            if cfg.get("show_seeds", False):
                for s in seeds:
                    dr.line([s[0] - 3, s[1] - 3, s[0] + 3, s[1] + 3], fill=col); dr.line([s[0] - 3, s[1] + 3, s[0] + 3, s[1] - 3], fill=col)
    base.crop((x0, y0, x1, y1)).save(os.path.join(outdir, name))


def short_components():
    """list of SHORT components (dashes, dots, markers) with centroid and extent in data units"""
    out = []
    for i, s in enumerate(sl, start=1):
        if s is None or is_long[i]:
            continue
        m = lab[s] == i
        n = int(m.sum())
        if n < cfg.get("min_comp_px", 25):
            continue
        ys, xs = np.nonzero(m)
        ys = ys + s[0].start; xs = xs + s[1].start
        cxp, cyp = xs.mean(), ys.mean()
        X, Y = px2d(cxp, cyp)
        Xa, Ya = px2d(xs, ys)
        # principal direction (for the dash orientation)
        cov = np.cov(np.vstack([xs, ys])) if n > 2 else np.eye(2)
        ev, evec = np.linalg.eigh(cov)
        L = 4 * math.sqrt(max(ev[-1], 0))
        out.append({"id": i, "n": n, "X": float(X), "Y": float(Y), "Xmin": float(Xa.min()), "Xmax": float(Xa.max()),
                    "Ymin": float(Ya.min()), "Ymax": float(Ya.max()), "len_px": float(L), "x_px": float(cxp), "y_px": float(cyp)})
    out.sort(key=lambda c: c["X"])
    return out


def auto_trace(a):
    """greedy column-run continuity trace of the (continuous) solid curve -> list of seeds (data units)"""
    X0, X1 = a["x_range"]
    x0p = d2px([X0], [a["start"][1]])[0][0]; x1p = d2px([X1], [a["start"][1]])[0][0]
    ystart = d2px([a["start"][0]], [a["start"][1]])[1][0]
    step = a.get("step_px", 4)
    M = (D > 0.5)
    wl = a.get("prefer_long", True)
    prev = None
    pts = []
    for x in range(int(round(x0p)), int(round(x1p)), 1):
        col = M[:, x]
        rr = runs(col)
        if not rr:
            continue
        def score(r):
            s, e = r
            ref = (ystart, ystart) if prev is None else prev
            gap = max(0, s - ref[1], ref[0] - e)
            longness = np.mean(LONG[s:e + 1, x])
            return gap + (0 if (not wl or longness > 0.5) else a.get("short_pen", 15))
        best = min(rr, key=score)
        if prev is not None and score(best) > a.get("max_gap", 40):
            continue
        prev = best
        s, e = best
        # thickness-aware centre: a run much thicker than the stroke at the local slope is a merge with
        # another curve; the solid curve is then the end of the run nearest the prediction
        lw0 = a.get("line_w", 9.0)
        yc = 0.5 * (s + e)
        if len(pts) >= 6:
            xp = np.array([p[0] for p in pts[-6:]]); yp = np.array([p[1] for p in pts[-6:]])
            sl_ = np.polyfit(xp, yp, 1)[0]
            pred = yp[-1] + sl_ * (x - xp[-1])
            expt = lw0 * math.sqrt(1 + sl_ ** 2)
            if (e - s + 1) > a.get("merge_mult", 1.6) * expt and expt < 4 * lw0:
                c1, c2 = s + expt / 2, e - expt / 2
                yc = c1 if abs(c1 - pred) < abs(c2 - pred) else c2
        pts.append((x, yc, s, e))
    lw = a.get("line_w", 9.0)
    thick = a.get("thick_mult", 2.5) * lw
    xs = np.array([p[0] for p in pts]); ss_ = np.array([p[2] for p in pts]); ee = np.array([p[3] for p in pts])
    keep = {}
    for i, (x, yc, s, e) in enumerate(pts):
        if (x - int(round(x0p))) % step == 0:
            keep[i] = yc
    hw_ = a.get("ext_win", 8)
    for i in range(len(pts)):
        lo, hi = max(0, i - hw_), min(len(pts), i + hw_ + 1)
        if ee[i] - ss_[i] + 1 > thick:
            if ee[i] == ee[lo:hi].max() and (i == 0 or ee[i] > ee[i - 1] or True) and ee[i] > ee[lo] and ee[i] > ee[hi - 1]:
                keep[i] = ee[i] - lw / 2          # lowest point of a null (tip)
            elif ss_[i] == ss_[lo:hi].min() and ss_[i] < ss_[lo] and ss_[i] < ss_[hi - 1]:
                keep[i] = ss_[i] + lw / 2         # highest point of a sharp peak
    seeds = []
    for i in sorted(keep):
        X, Y = px2d(pts[i][0], keep[i])
        seeds.append([float(X), float(Y)])
    return seeds, pts


if mode == "calib":
    crop_view(f"{fig}_calibview.png")
    json.dump(calib, open(os.path.join(outdir, f"{fig}_calib.json"), "w"), indent=1)
    comps = short_components()
    json.dump(comps, open(os.path.join(outdir, f"{fig}_shortcomps.json"), "w"), indent=0)
    for c in comps:
        print("comp %5d n=%5d X=%7.3f Y=%8.3f  X[%7.3f,%7.3f] Y[%8.3f,%8.3f] len=%5.1f" % (
            c["id"], c["n"], c["X"], c["Y"], c["Xmin"], c["Xmax"], c["Ymin"], c["Ymax"], c["len_px"]))
    sys.exit(0)


# ---------------- tracking
def track_dp(Smap, seeds, W, lam, maxstep=1.5, tstep=0.5, smooth_len=8, dthr=0.15):
    seeds = np.array(seeds, dtype=float)
    seg = np.diff(seeds, axis=0)
    segl = np.linalg.norm(seg, axis=1)
    s_cum = np.concatenate([[0], np.cumsum(segl)])
    ss = np.arange(0, s_cum[-1], 1.0)
    cx_ = np.interp(ss, s_cum, seeds[:, 0]); cy_ = np.interp(ss, s_cum, seeds[:, 1])
    tx = np.gradient(cx_); ty = np.gradient(cy_)
    k = max(1, int(smooth_len))
    tx = ndimage.uniform_filter1d(tx, 2 * k + 1, mode="nearest"); ty = ndimage.uniform_filter1d(ty, 2 * k + 1, mode="nearest")
    nrm = np.hypot(tx, ty); tx /= nrm; ty /= nrm
    nx, ny = -ty, tx
    tt = np.arange(-W, W + 1e-9, tstep)
    PX = cx_[:, None] + tt[None, :] * nx[:, None]
    PY = cy_[:, None] + tt[None, :] * ny[:, None]
    S = ndimage.map_coordinates(Smap, [PY.ravel(), PX.ravel()], order=1, mode="nearest").reshape(PX.shape)
    n, m = S.shape
    cost = np.full((n, m), np.inf); back = np.zeros((n, m), dtype=int)
    cost[0] = -S[0]
    jmax = int(round(maxstep / tstep))
    for i in range(1, n):
        best = np.full(m, np.inf); arg = np.zeros(m, dtype=int)
        for dj in range(-jmax, jmax + 1):
            pen = lam * (dj * tstep) ** 2
            src = np.roll(cost[i - 1], dj); idx = np.roll(np.arange(m), dj)
            if dj > 0: src[:dj] = np.inf
            if dj < 0: src[dj:] = np.inf
            c = src + pen
            better = c < best
            best[better] = c[better]; arg[better] = idx[better]
        cost[i] = best - S[i]; back[i] = arg
    j = int(np.argmin(cost[-1]))
    path = np.zeros(n, dtype=int)
    for i in range(n - 1, -1, -1):
        path[i] = j; j = back[i, j]
    pts, flags, st = [], [], []
    for i in range(n):
        jj = path[i]
        sel = np.abs(tt - tt[jj]) <= 2.0
        w = S[i, sel]
        t = float(np.sum(tt[sel] * w) / np.sum(w)) if np.sum(w) > 1e-6 else tt[jj]
        pts.append((cx_[i] + t * nx[i], cy_[i] + t * ny[i]))
        flags.append(0 if S[i, jj] > dthr else 1); st.append(float(S[i, jj]))
    fl = np.array(flags)
    fl[np.abs(tt[path]) >= W - 1.0] = 2
    return np.array(pts), fl, np.array(st)


res = {"figure": fig, "image": cfg["image"], "thesis_page": cfg.get("thesis_page"), "pdf_page": cfg.get("pdf_page"),
       "calibration": calib, "curves": []}
views = []
COMPS = {c["id"]: c for c in short_components()}
TRACKS = {}


def stroke_mask(pts, r):
    m = np.zeros_like(D, bool)
    ix = np.clip(np.round(pts[:, 0]).astype(int), 0, Wd - 1); iy = np.clip(np.round(pts[:, 1]).astype(int), 0, H - 1)
    m[iy, ix] = True
    dist = ndimage.distance_transform_edt(~m)
    return dist <= r


def residual_seeds(cv, R):
    """centroids (data units) of residual ink components (ink not explained by the solid curve(s))"""
    lab_r, nr = ndimage.label(R > 0.5, structure=np.ones((3, 3)))
    out = []
    X0, X1 = cv["x_range"]
    for i, s in enumerate(ndimage.find_objects(lab_r), start=1):
        if s is None:
            continue
        m = lab_r[s] == i
        if m.sum() < cv.get("min_res_px", 30):
            continue
        ys, xs = np.nonzero(m); ys = ys + s[0].start; xs = xs + s[1].start
        X, Y = px2d(xs.mean(), ys.mean())
        if not (X0 <= X <= X1):
            continue
        yr = cv.get("y_range", [-1e9, 1e9])
        if not (yr[0] <= Y <= yr[1]):
            continue
        if any(math.hypot((X - e[0]) / e[2], (Y - e[1]) / e[3]) < 1 for e in cv.get("exclude", [])):
            continue
        # long residual pieces (steep/long dashes): split into points along their principal axis
        Xa, Ya = px2d(xs, ys)
        if len(xs) > 2:
            cov = np.cov(np.vstack([xs, ys])); ev, evec = np.linalg.eigh(cov)
            L = 4 * math.sqrt(max(ev[-1], 0)); d = evec[:, -1]
        else:
            L = 0; d = np.array([1.0, 0])
        if L > 40:
            proj = (xs - xs.mean()) * d[0] + (ys - ys.mean()) * d[1]
            for q in np.linspace(proj.min() + 5, proj.max() - 5, max(2, int(L / 20))):
                sel = np.abs(proj - q) < 6
                if sel.sum() > 3:
                    Xq, Yq = px2d(xs[sel].mean(), ys[sel].mean())
                    out.append([float(Xq), float(Yq)])
        else:
            out.append([float(X), float(Y)])
    return out


for cv in cfg["curves"]:
    wl, ws = cv.get("w_long", 1.0), cv.get("w_short", 1.0)
    Smap = wl * D_long + ws * D_short
    if "auto" in cv and cv.get("direct", False):
        # per-column result of the continuity trace, no DP: centre of the stroke in each column (thickness-aware),
        # tip/peak columns at the run end -/+ half the line width; light smoothing where the curve is shallow
        pieces = cv["auto"] if isinstance(cv["auto"], list) else [cv["auto"]]
        apts = []
        for pc in pieces:
            _, ap = auto_trace(pc)
            apts += ap
        cv["auto"] = pieces[0] | {"pieces": len(pieces)}
        lw0 = cv["auto"].get("line_w", 9.0)
        ax_ = np.array([p[0] for p in apts], float); ay_ = np.array([p[1] for p in apts], float)
        as_ = np.array([p[2] for p in apts], float); ae_ = np.array([p[3] for p in apts], float)
        thick = (ae_ - as_ + 1)
        hw2 = cv["auto"].get("ext_win", 8)
        flags = np.zeros(len(ax_), int)
        kk = 4
        for i in range(kk, len(ax_) - kk):
            lo, hi = max(0, i - hw2), min(len(ax_), i + hw2 + 1)
            sL = (ay_[i] - ay_[i - kk]) / kk; sR = (ay_[i + kk] - ay_[i]) / kk
            if thick[i] > 2.5 * lw0:
                if ae_[i] == ae_[lo:hi].max() and ae_[i] > ae_[lo] and ae_[i] > ae_[hi - 1] and sL > 1.0 and sR < -1.0:
                    ay_[i] = ae_[i] - lw0 / 2; flags[i] = 4      # null tip
                elif as_[i] == as_[lo:hi].min() and as_[i] < as_[lo] and as_[i] < as_[hi - 1] and sL < -1.0 and sR > 1.0:
                    ay_[i] = as_[i] + lw0 / 2; flags[i] = 4
        # ---- normal-profile refinement (any slope): resample the rough trace by arc length, and at each sample
        # find the ink run along the local normal; a run thicker than ~1.5 line widths is a merge with the other
        # curve -> the solid centre is lw/2 inside the run end that continues the previous refined offset.
        rough = np.vstack([ax_, ay_]).T
        segl = np.hypot(*np.diff(rough, axis=0).T)
        scum = np.concatenate([[0], np.cumsum(segl)])
        sgrid = np.arange(0, scum[-1], cv.get("ds", 2.0))
        rx = np.interp(sgrid, scum, rough[:, 0]); ry = np.interp(sgrid, scum, rough[:, 1])
        fl_s = np.interp(sgrid, scum, (flags == 4).astype(float)) > 0.5
        k_ = cv.get("tan_win", 6)
        tx = ndimage.uniform_filter1d(np.gradient(rx), 2 * k_ + 1, mode="nearest")
        ty = ndimage.uniform_filter1d(np.gradient(ry), 2 * k_ + 1, mode="nearest")
        nn = np.hypot(tx, ty); tx /= nn; ty /= nn
        nx_, ny_ = -ty, tx
        tt = np.arange(-cv.get("prof_half", 22), cv.get("prof_half", 22) + 1e-9, 0.5)
        PX = rx[:, None] + tt[None, :] * nx_[:, None]; PY = ry[:, None] + tt[None, :] * ny_[:, None]
        PR = ndimage.map_coordinates(D0, [PY.ravel(), PX.ravel()], order=1, mode="nearest").reshape(PX.shape)
        PB = ndimage.map_coordinates(blank.astype(float), [PY.ravel(), PX.ravel()], order=0, mode="nearest").reshape(PX.shape)
        PR[PB > 0.5] = 0.0
        outp, oflag, othk = [], [], []
        prev_off = 0.0
        for i in range(len(sgrid)):
            if fl_s[i]:
                outp.append((rx[i], ry[i])); oflag.append(4); othk.append(0.0); continue
            rr = runs(PR[i] > 0.5)
            if not rr:
                outp.append((rx[i], ry[i])); oflag.append(1); othk.append(0.0); continue
            def rdist(r):
                t1, t2 = tt[r[0]], tt[r[1]]
                return 0.0 if t1 <= prev_off <= t2 else min(abs(t1 - prev_off), abs(t2 - prev_off))
            r = min(rr, key=rdist)
            t1, t2 = tt[r[0]], tt[r[1]]
            L = t2 - t1 + 0.5
            if rdist(r) > lw0:
                outp.append((rx[i] + prev_off * nx_[i], ry[i] + prev_off * ny_[i])); oflag.append(1); othk.append(L); continue
            if L <= cv.get("merge_mult", 1.45) * lw0:
                w = PR[i, r[0]:r[1] + 1]
                off = float(np.sum(tt[r[0]:r[1] + 1] * w) / np.sum(w)); fflag = 0
            else:
                c1, c2 = t1 + lw0 / 2, t2 - lw0 / 2
                off = c1 if abs(c1 - prev_off) < abs(c2 - prev_off) else c2; fflag = 3
            prev_off = 0.7 * off + 0.3 * prev_off if fflag == 3 else off
            outp.append((rx[i] + off * nx_[i], ry[i] + off * ny_[i])); oflag.append(fflag); othk.append(L)
        pts = np.array(outp)
        # light smoothing along the curve (not at tips)
        sm_n = cv.get("smooth", 5)
        flags = np.array(oflag)
        psm = np.vstack([ndimage.uniform_filter1d(pts[:, 0], sm_n, mode="nearest"),
                         ndimage.uniform_filter1d(pts[:, 1], sm_n, mode="nearest")]).T
        tipnear = ndimage.maximum_filter1d((flags == 4).astype(int), sm_n + 2) > 0
        pts = np.where(tipnear[:, None], pts, psm)
        st = np.array(othk)
        # manual overrides: ranges where the continuity trace is unreliable -> DP on manual seeds
        for ov in cv.get("override", []):
            ox, oy = d2px([s[0] for s in ov["seeds"]], [s[1] for s in ov["seeds"]])
            opts, ofl, ost = track_dp(Dall, np.vstack([ox, oy]).T, ov.get("W", 5.0), ov.get("lam", 3.0), 1.0)
            # splice (keeps the arc-length order of the column trace): replace the stretch between the trace points
            # nearest to the override's first and last points
            i0 = int(np.argmin(np.hypot(pts[:, 0] - opts[0, 0], pts[:, 1] - opts[0, 1])))
            i1 = int(np.argmin(np.hypot(pts[:, 0] - opts[-1, 0], pts[:, 1] - opts[-1, 1])))
            if i1 < i0:
                i0, i1 = i1, i0; opts = opts[::-1]; ofl = ofl[::-1]; ost = ost[::-1]
            pts = np.vstack([pts[:i0], opts, pts[i1 + 1:]])
            flags = np.concatenate([flags[:i0], np.where(ofl == 0, 5, ofl), flags[i1 + 1:]])
            st = np.concatenate([st[:i0], ost, st[i1 + 1:]])
        seeds_px = pts[::20]
        TRACKS[cv["name"]] = pts
        X, Y = px2d(pts[:, 0], pts[:, 1])
        raw = [{"x_px": float(a), "y_px": float(b), "X": float(c), "Y": float(e), "flag": int(f), "S": float(g)}
               for a, b, c, e, f, g in zip(pts[:, 0], pts[:, 1], X, Y, flags, st)]
        res["curves"].append({k: v for k, v in cv.items()} | {"n_points": len(raw), "raw": raw})
        views.append((pts, np.where(flags == 4, 0, flags), seeds_px))
        print(f"{cv['name']:28s} pts={len(raw):5d} (direct column trace)")
        continue
    if "auto" in cv:
        seeds, _ = auto_trace(cv["auto"])
        cv["seeds"] = cv.get("pre_seeds", []) + seeds + cv.get("post_seeds", [])
    if "residual_of" in cv:
        stroke = np.zeros_like(D, bool)
        for nm in cv["residual_of"]:
            stroke |= stroke_mask(TRACKS[nm], cv.get("stroke_r", 5.5))
        Dm = D.copy(); Dm[stroke] = 0.0
        R = ndimage.gaussian_filter(Dm, sig)
        Smap = R + cv.get("w_stroke", 0.5) * ndimage.gaussian_filter(np.where(stroke, D, 0.0), sig)
        if cv.get("use_full", False):
            Smap = Dall
        rs = residual_seeds(cv, Dm) if not cv.get("manual_seeds", False) else []
        allp = rs + [list(s) for s in cv.get("extra_seeds", [])]
        # fill long gaps between residual seeds with points of the solid track (curves overlap there)
        allp.sort(key=lambda p: p[0])
        if cv.get("fill_from", None):
            T = TRACKS[cv["fill_from"]]
            TX, TY = px2d(T[:, 0], T[:, 1])
            fg = cv.get("fill_gap", 0.3)
            add = []
            tolpx = cv.get("fill_tol_px", 25)
            for p, q in zip(allp[:-1], allp[1:]):
                if q[0] - p[0] > fg:
                    for xv in np.arange(p[0] + fg / 2, q[0] - fg / 4, fg / 2):
                        j = int(np.argmin(np.abs(TX - xv)))
                        yi = p[1] + (q[1] - p[1]) * (TX[j] - p[0]) / (q[0] - p[0])
                        a_ = np.array(d2px([TX[j]], [TY[j]])).ravel(); b_ = np.array(d2px([TX[j]], [yi])).ravel()
                        if np.hypot(*(a_ - b_)) < tolpx:
                            add.append([float(TX[j]), float(TY[j])])
            allp = sorted(allp + add, key=lambda p: p[0])
        if cv.get("manual_seeds", False):
            allp = list(cv["seeds"])
            if cv.get("residual_windows"):
                rsd = residual_seeds(cv, Dm)
                extra = [p for p in rsd if any(w0 <= p[0] <= w1 for w0, w1 in cv["residual_windows"])]
                print("residual seeds added:", [[round(a, 3), round(b, 2)] for a, b in extra])
                allp = allp + extra
                def keyx(s):
                    if isinstance(s, dict):
                        if "near" in s:
                            return s["near"][0]
                        return s["x"][0] if "x" in s else s.get("xsort", 0)
                    return s[0]
                allp = sorted(allp, key=keyx)
        cv["seeds"] = allp
    sd = []
    for s in cv["seeds"]:
        if isinstance(s, dict) and "near" in s:
            nx0, ny0 = d2px([s["near"][0]], [s["near"][1]])
            best = min(COMPS.values(), key=lambda c: math.hypot(c["x_px"] - nx0[0], c["y_px"] - ny0[0]))
            dpx = math.hypot(best["x_px"] - nx0[0], best["y_px"] - ny0[0])
            if dpx > s.get("tol_px", 25):
                print("WARNING near", s["near"], "nearest comp %.1f px away -> skipped" % dpx)
                continue
            s = dict(s, comp=best["id"])
        if isinstance(s, dict) and "comp" in s and s.get("ends", False):
            i = s["comp"]; sl_i = sl[i - 1]
            m = lab[sl_i] == i
            ys, xs = np.nonzero(m); ys = ys + sl_i[0].start; xs = xs + sl_i[1].start
            cov = np.cov(np.vstack([xs, ys])); ev, evec = np.linalg.eigh(cov); dv = evec[:, -1]
            pr = (xs - xs.mean()) * dv[0] + (ys - ys.mean()) * dv[1]
            inset = s.get("inset", 5.0)
            trip = []
            for q in (pr.min() + inset, 0.0, pr.max() - inset):
                sel = np.abs(pr - q) < 3
                Xq, Yq = px2d(xs[sel].mean(), ys[sel].mean())
                trip.append([float(Xq), float(Yq)])
            sd += sorted(trip, key=lambda p: p[0])
        elif isinstance(s, dict) and "comp" in s:
            c = COMPS[s["comp"]]; sd.append([c["X"], c["Y"]])
        elif isinstance(s, dict) and "from" in s:
            T = TRACKS[s["from"]]
            TX, TY = px2d(T[:, 0], T[:, 1])
            sel = np.where((TX >= s["x"][0]) & (TX <= s["x"][1]))[0]
            if len(sel):
                seg = T[sel[0]:sel[-1] + 1]
                segX, segY = TX[sel[0]:sel[-1] + 1], TY[sel[0]:sel[-1] + 1]
                L = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(seg, axis=0).T))])
                for q in np.arange(0, L[-1] + 1e-9, s.get("step_px", 15)):
                    j = int(np.argmin(np.abs(L - q)))
                    sd.append([float(segX[j]), float(segY[j])])
        else:
            sd.append(s)
    # robust seed cleaning: drop seeds far (normal distance) from a local straight-line fit of their neighbours
    if cv.get("clean", None):
        cl = cv["clean"]
        for _it in range(cl.get("iter", 2)):
            arr = np.array(sd)
            px_, py_ = d2px(arr[:, 0], arr[:, 1])
            keep = np.ones(len(sd), bool)
            for i in range(len(sd)):
                if any(r[0] <= arr[i, 0] <= r[1] for r in cl.get("protect", [])):
                    continue
                nb = [j for j in range(len(sd)) if j != i and abs(px_[j] - px_[i]) <= cl.get("R", 25)]
                if len(nb) < 3:
                    continue
                A = np.vstack([np.ones(len(nb)), px_[nb]]).T
                co, *_ = np.linalg.lstsq(A, py_[nb], rcond=None)
                dist = abs(py_[i] - (co[0] + co[1] * px_[i])) / math.sqrt(1 + co[1] ** 2)
                if dist > cl.get("tol", 4.0):
                    keep[i] = False
            sd = [s for s, k in zip(sd, keep) if k]
    cv["seeds"] = sd
    sx, sy = d2px([s[0] for s in cv["seeds"]], [s[1] for s in cv["seeds"]])
    seeds_px = np.vstack([sx, sy]).T
    pts, flags, st = track_dp(Smap, seeds_px, cv.get("W", 10.0), cv.get("lam", 2.0), cv.get("maxstep", 1.5),
                              dthr=cv.get("dthr", 0.15))
    TRACKS[cv["name"]] = pts
    X, Y = px2d(pts[:, 0], pts[:, 1])
    raw = [{"x_px": float(a), "y_px": float(b), "X": float(c), "Y": float(e), "flag": int(f), "S": float(g)}
           for a, b, c, e, f, g in zip(pts[:, 0], pts[:, 1], X, Y, flags, st)]
    res["curves"].append({k: v for k, v in cv.items() if k != "seeds"} | {"seeds": cv["seeds"], "n_points": len(raw),
                          "fraction_on_ink": float(np.mean(flags == 0)), "n_edge": int(np.sum(flags == 2)), "raw": raw})
    views.append((pts, flags, seeds_px))
    print(f"{cv['name']:28s} pts={len(raw):5d} on_ink={np.mean(flags == 0):.3f} gap={np.sum(flags == 1)} edge={np.sum(flags == 2)}")
crop_view(f"{fig}_trackview.png", views, grid=cfg.get("track_grid", False))
json.dump(res, open(os.path.join(outdir, f"{fig}_track.json"), "w"), indent=0)
