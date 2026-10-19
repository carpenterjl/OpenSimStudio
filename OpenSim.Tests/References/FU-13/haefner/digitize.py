"""Semi-automatic digitiser for the curves of Haefner (1937) Figs. 5-9.

Usage: python -I digitize.py <config.json> <outdir>

Config (pixel coordinates are those of the page render named in "image"):
{
 "figure": "fig5", "image": "<png>", "page": "...",
 "xcal": {"bands": [[y0,y1],[y0,y1]], "lines": [[approx_px, value], ...], "win": 6},
 "ycal": {"bands": [[x0,x1],[x0,x1]], "lines": [[approx_px, value], ...], "win": 6},
 "plot": [x0,y0,x1,y1],                 # plot interior (for ink map)
 "line_len": 31,                        # structuring length for grid-line suppression (px)
 "curves": [{"name": "...", "seeds": [[x,y],...], "r": 3.0, "label": "..."}],
 "resample_x": [start, stop, step]      # x-values (data units) at which to tabulate
}
Steps
 1. Calibration: each eye-picked grid line is refined to the sub-pixel minimum of the mean intensity
    profile in each band (+/- win px). Model: x_px = x0 + sx*value + kx*(y - yc) (kx = skew from the two
    bands), likewise for y. Residuals of the linear fits are reported (data units).
 2. Ink map: grid rulings are long straight horizontal/vertical dark runs. H = grey-dilation (max filter) of
    the image along rows (length L) is dark only on horizontal rulings; V likewise for columns.
    Background B = min(H, V, paper level); ink D = max(B - I, 0), Gaussian-smoothed (sigma 0.8 px).
 3. Tracking: from the first seed, step 1 px along the curve; at each step search along the normal within
    +/- r px of the predicted point for the D-weighted centroid around the D maximum; if D is too weak
    (crossing a ruling, a gap) coast on the predicted direction. The predicted direction blends the recent
    track direction with the seed-polyline tangent, and the point is pulled back if it strays > 2r from
    the seed polyline (flagged).
 4. Output per curve: raw track (px and data units, D strength, measured/coasted flag), and a table at the
    requested x-values from a local linear fit (window +/- 1.5 % of x-range) of measured points, with the
    local scatter.
"""
import sys, os, json, math
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

cfg_path, outdir = sys.argv[1], sys.argv[2]
cfg = json.load(open(cfg_path))
os.makedirs(outdir, exist_ok=True)
fig = cfg["figure"]
img = np.asarray(Image.open(cfg["image"]).convert("L"), dtype=float)
Hh, Ww = img.shape


def refine(profile, approx, win):
    """sub-pixel minimum of a 1-D profile within +/- win of approx (3-px box smoothing, parabola fit)."""
    pr = ndimage.uniform_filter1d(profile, 3)
    a = int(round(approx))
    lo, hi = max(a - win, 1), min(a + win, len(pr) - 2)
    i = lo + int(np.argmin(pr[lo:hi + 1]))
    y0, y1, y2 = pr[i - 1], pr[i], pr[i + 1]
    den = y0 - 2 * y1 + y2
    off = 0.5 * (y0 - y2) / den if den > 0 else 0.0
    return i + max(-0.5, min(0.5, off)), float(y1)


def calibrate(axis):
    c = cfg[axis]
    win = c.get("win", 6)
    rows = []
    for bi, band in enumerate(c["bands"]):
        b0, b1 = band
        lines = c["lines_by_band"][bi] if "lines_by_band" in c else c["lines"]
        if axis == "xcal":   # vertical rulings: column profile over rows b0..b1
            prof = img[b0:b1, :].mean(axis=0)
        else:                # horizontal rulings: row profile over columns b0..b1
            prof = img[:, b0:b1].mean(axis=1)
        bc = 0.5 * (b0 + b1)
        for ln in lines:
            approx, val = ln[0], ln[1]
            mode = ln[2] if len(ln) > 2 else "refine"
            w = int(ln[3]) if len(ln) > 3 else win
            if mode == "eye":          # label centre read by eye: used as is (no profile refinement)
                pos, depth = float(approx), float("nan")
            else:                      # a ruling / frame line: refined to the profile minimum
                pos, depth = refine(prof, approx, w)
            rows.append((val, bc, pos, approx, depth))
        if c.get("single_band_eye", False):
            break
    if c.get("no_skew", False):
        A = np.array([[1.0, r[0], 0.0] for r in rows])
    else:
        A = np.array([[1.0, r[0], r[1]] for r in rows])
    yv = np.array([r[2] for r in rows])
    coef, *_ = np.linalg.lstsq(A, yv, rcond=None)
    pred = A @ coef
    resid_px = yv - pred
    resid_units = resid_px / coef[1]
    return coef, rows, resid_px, resid_units


cx, rx, resx_px, resx_u = calibrate("xcal")   # x_px = cx0 + cx1*X + cx2*y_px
cy, ry, resy_px, resy_u = calibrate("ycal")   # y_px = cy0 + cy1*Y + cy2*x_px


def px_to_data(x, y):
    # solve x = cx0 + cx1 X + cx2 y ; y = cy0 + cy1 Y + cy2 x
    X = (x - cx[0] - cx[2] * y) / cx[1]
    Y = (y - cy[0] - cy[2] * x) / cy[1]
    return X, Y


def data_to_px(X, Y):
    # x = cx0 + cx1 X + cx2 y ; y = cy0 + cy1 Y + cy2 x  -> linear system
    M = np.array([[1.0, -cx[2]], [-cy[2], 1.0]])
    b = np.array([cx[0] + cx[1] * X, cy[0] + cy[1] * Y])
    return np.linalg.solve(M, b)


calib = {
    "x_model": "x_px = %.4f + %.4f*X + %.6f*y_px" % tuple(cx),
    "y_model": "y_px = %.4f + %.4f*Y + %.6f*x_px" % tuple(cy),
    "x_px_per_unit": cx[1], "y_px_per_unit": cy[1],
    "x_skew_px_per_px": cx[2], "y_skew_px_per_px": cy[2],
    "x_resid_rms_units": float(np.sqrt(np.mean(resx_u ** 2))), "x_resid_max_units": float(np.max(np.abs(resx_u))),
    "y_resid_rms_units": float(np.sqrt(np.mean(resy_u ** 2))), "y_resid_max_units": float(np.max(np.abs(resy_u))),
    "x_lines": [{"value": r[0], "band_center": r[1], "refined_px": r[2], "eye_px": r[3], "resid_units": float(u)} for r, u in zip(rx, resx_u)],
    "y_lines": [{"value": r[0], "band_center": r[1], "refined_px": r[2], "eye_px": r[3], "resid_units": float(u)} for r, u in zip(ry, resy_u)],
}

# ---- ink map
x0, y0, x1, y1 = cfg["plot"]
L = cfg.get("line_len", 31)
sub = img[y0:y1, x0:x1]
Hd = ndimage.maximum_filter(sub, size=(1, L))
Vd = ndimage.maximum_filter(sub, size=(L, 1))
paper = np.percentile(sub, 90)
B = np.minimum(np.minimum(Hd, Vd), paper)
D = np.clip(B - sub, 0, None)
D = ndimage.gaussian_filter(D, cfg.get("ink_sigma", 0.8))
Dfull = np.zeros_like(img)
Dfull[y0:y1, x0:x1] = D
dthr = cfg.get("dthr", None)
if dthr is None:
    dthr = float(np.percentile(D, 97)) * 0.6
Image.fromarray(np.clip(255 - Dfull * 2, 0, 255).astype(np.uint8)).save(os.path.join(outdir, f"{fig}_inkmap_full.png"))


def sample(arr, x, y):
    return ndimage.map_coordinates(arr, [[y], [x]], order=1, mode="nearest")[0]


def poly_closest(seeds, p):
    """closest point on polyline, its tangent and arclength param."""
    best = None
    for i in range(len(seeds) - 1):
        a, b = seeds[i], seeds[i + 1]
        ab = b - a
        t = np.clip(np.dot(p - a, ab) / np.dot(ab, ab), 0, 1)
        q = a + t * ab
        d = np.linalg.norm(p - q)
        if best is None or d < best[0]:
            best = (d, q, ab / np.linalg.norm(ab), i + t)
    return best


def track(seeds, r, cont=1.5):
    seeds = np.array(seeds, dtype=float)
    pts = [seeds[0].copy()]
    flags = [0]
    strength = [float(sample(Dfull, *seeds[0]))]
    d = seeds[1] - seeds[0]
    d /= np.linalg.norm(d)
    offs = np.arange(-r, r + 1e-9, 0.25)
    nmax = int(sum(np.linalg.norm(seeds[i + 1] - seeds[i]) for i in range(len(seeds) - 1)) * 1.5) + 10
    for _ in range(nmax):
        cur = pts[-1]
        dist, q, tan, s = poly_closest(seeds, cur)
        if s >= len(seeds) - 1 - 1e-6:
            break
        # direction: recent track direction blended with seed tangent
        if len(pts) > 6:
            rd = pts[-1] - pts[-6]
            if np.linalg.norm(rd) > 0:
                rd /= np.linalg.norm(rd)
                d = 0.6 * rd + 0.4 * tan
            else:
                d = tan
        else:
            d = tan
        d /= np.linalg.norm(d)
        pred = cur + d
        n = np.array([-d[1], d[0]])
        xs = pred[0] + offs * n[0]
        ys = pred[1] + offs * n[1]
        vals = ndimage.map_coordinates(Dfull, [ys, xs], order=1, mode="nearest")
        # continuity prior: prefer ink close to the predicted position (sigma = cont px)
        score = vals * np.exp(-0.5 * (offs / cont) ** 2)
        k = int(np.argmax(score))
        if vals[k] > dthr:
            sel = np.abs(offs - offs[k]) <= 1.0
            w = vals[sel]
            t = float(np.sum(offs[sel] * w) / np.sum(w))
            new = pred + t * n
            flag = 0
            st = float(vals[k])
        else:
            new = pred
            flag = 1
            st = float(vals[k])
        # keep near the seed polyline
        dist2, q2, _, _ = poly_closest(seeds, new)
        if dist2 > 2 * r:
            new = q2 + (new - q2) * (2 * r / dist2)
            flag = 2
        pts.append(new)
        flags.append(flag)
        strength.append(st)
    return np.array(pts), np.array(flags), np.array(strength)


def track_dp(seeds, W, lam, maxstep=1.5, tstep=0.5, smooth_len=15):
    """Globally optimal ridge path inside a corridor of half-width W around the seed polyline.
    Arc-length samples (1 px) along the seed polyline; offsets t in [-W, W] along the smoothed normal;
    maximise sum(D) - lam * sum((t_i - t_{i-1})^2) subject to |t_i - t_{i-1}| <= maxstep (Viterbi)."""
    seeds = np.array(seeds, dtype=float)
    seg = np.diff(seeds, axis=0)
    segl = np.linalg.norm(seg, axis=1)
    s_cum = np.concatenate([[0], np.cumsum(segl)])
    ss = np.arange(0, s_cum[-1], 1.0)
    cx_ = np.interp(ss, s_cum, seeds[:, 0])
    cy_ = np.interp(ss, s_cum, seeds[:, 1])
    tx = np.gradient(cx_); ty = np.gradient(cy_)
    k = max(1, int(smooth_len))
    tx = ndimage.uniform_filter1d(tx, 2 * k + 1, mode="nearest"); ty = ndimage.uniform_filter1d(ty, 2 * k + 1, mode="nearest")
    nrm = np.hypot(tx, ty); tx /= nrm; ty /= nrm
    nx, ny = -ty, tx
    tt = np.arange(-W, W + 1e-9, tstep)
    PX = cx_[:, None] + tt[None, :] * nx[:, None]
    PY = cy_[:, None] + tt[None, :] * ny[:, None]
    S = ndimage.map_coordinates(Dfull, [PY.ravel(), PX.ravel()], order=1, mode="nearest").reshape(PX.shape)
    n, m = S.shape
    cost = np.full((n, m), np.inf)
    back = np.zeros((n, m), dtype=int)
    cost[0] = -S[0]
    jmax = int(round(maxstep / tstep))
    for i in range(1, n):
        best = np.full(m, np.inf); arg = np.zeros(m, dtype=int)
        for dj in range(-jmax, jmax + 1):
            pen = lam * (dj * tstep) ** 2
            src = np.roll(cost[i - 1], dj)
            idx = np.roll(np.arange(m), dj)
            if dj > 0: src[:dj] = np.inf
            if dj < 0: src[dj:] = np.inf
            c = src + pen
            better = c < best
            best[better] = c[better]; arg[better] = idx[better]
        cost[i] = best - S[i]
        back[i] = arg
    j = int(np.argmin(cost[-1]))
    path = np.zeros(n, dtype=int)
    for i in range(n - 1, -1, -1):
        path[i] = j
        j = back[i, j]
    # sub-pixel: D-weighted centroid within +/-1 px of the chosen offset
    pts, flags, st = [], [], []
    for i in range(n):
        jj = path[i]
        sel = np.abs(tt - tt[jj]) <= 1.0
        w = S[i, sel]
        t = float(np.sum(tt[sel] * w) / np.sum(w)) if np.sum(w) > 0 else tt[jj]
        pts.append((cx_[i] + t * nx[i], cy_[i] + t * ny[i]))
        flags.append(0 if S[i, jj] > dthr else 1)
        st.append(float(S[i, jj]))
    edge = np.abs(tt[path]) >= W - 1.0
    fl = np.array(flags)
    fl[edge] = 2          # path ran along the corridor edge: suspect
    return np.array(pts), fl, np.array(st)


results = {"figure": fig, "image": cfg["image"], "page": cfg.get("page"), "calibration": calib, "dthr": dthr, "curves": []}
ov = Image.open(cfg["image"]).convert("RGB")
dr = ImageDraw.Draw(ov)
colors = [(230, 0, 0), (0, 120, 255), (0, 170, 0), (200, 0, 200), (255, 140, 0), (0, 180, 180), (120, 60, 0), (90, 0, 200), (200, 200, 0), (0, 0, 0)]
rs = cfg.get("resample_x")
for ci, cv in enumerate(cfg["curves"]):
    if cv.get("method", cfg.get("method", "dp")) == "dp":
        pts, flags, st = track_dp(cv["seeds"], cv.get("W", cfg.get("W", 8.0)), cv.get("lam", cfg.get("lam", 3.0)),
                                  cv.get("maxstep", cfg.get("maxstep", 1.5)))
    else:
        pts, flags, st = track(cv["seeds"], cv.get("r", 3.0), cv.get("cont", cfg.get("cont", 1.5)))
    X, Y = px_to_data(pts[:, 0], pts[:, 1])
    raw = [{"x_px": float(a), "y_px": float(b), "X": float(c), "Y": float(e), "flag": int(f), "D": float(g)}
           for a, b, c, e, f, g in zip(pts[:, 0], pts[:, 1], X, Y, flags, st)]
    meas = flags == 0
    table = []
    xr = cv.get("resample_x", rs)
    if xr:
        xv_all = [l["value"] for l in calib["x_lines"]]
        span = cfg.get("x_span", max(xv_all) - min(xv_all))
        h = cv.get("fit_halfwidth", 0.015 * span)
        Xm, Ym = X[meas], Y[meas]
        for xv in np.arange(xr[0], xr[1] + 1e-9, xr[2]):
            if xv < X.min() - 1e-9 or xv > X.max() + 1e-9:
                continue
            sel = np.abs(Xm - xv) <= h
            if sel.sum() < 4:
                table.append({"X": round(float(xv), 6), "Y": None, "n": int(sel.sum())})
                continue
            xs_, ys_ = Xm[sel] - xv, Ym[sel]
            keep = np.ones(len(xs_), bool)
            for _it in range(4):   # robust: iteratively drop > 2.5 sigma (markers, crossings)
                A = np.vstack([np.ones(keep.sum()), xs_[keep]]).T
                co, *_ = np.linalg.lstsq(A, ys_[keep], rcond=None)
                res_all = ys_ - (co[0] + co[1] * xs_)
                sd = np.std(res_all[keep], ddof=2) if keep.sum() > 3 else 0
                nk = np.abs(res_all) <= max(2.5 * sd, 1e-9)
                if nk.sum() < 4 or np.array_equal(nk, keep):
                    break
                keep = nk
            res = res_all[keep]
            table.append({"X": round(float(xv), 6), "Y": float(co[0]), "n": int(keep.sum()), "n_rejected": int((~keep).sum()),
                          "scatter_sd": float(np.std(res, ddof=2)) if keep.sum() > 2 else None})
    frac_meas = float(meas.mean())
    results["curves"].append({"name": cv["name"], "label": cv.get("label", ""), "r": cv.get("r", 3.0), "seeds": cv["seeds"],
                              "n_points": len(raw), "fraction_measured": frac_meas, "n_pulled_back": int((flags == 2).sum()),
                              "table": table, "raw": raw})
    col = colors[ci % len(colors)]
    for (a, b), f in zip(pts, flags):
        if f == 0:
            dr.point((a, b), fill=col)
        else:
            dr.point((a, b), fill=(255, 200, 0))
    for s in cv["seeds"]:
        dr.ellipse([s[0] - 2, s[1] - 2, s[0] + 2, s[1] + 2], outline=col)
    print(f"{cv['name']:24s} pts={len(raw):5d} measured={frac_meas:.2f} pulled={int((flags == 2).sum())}")
# calibration lines overlay
for v in calib["x_lines"]:
    pass
ov.save(os.path.join(outdir, f"{fig}_overlay_full.png"))
json.dump(results, open(os.path.join(outdir, f"{fig}_digitized.json"), "w"), indent=1)
print(json.dumps({k: v for k, v in calib.items() if not k.endswith("lines")}, indent=1))
print("dthr", dthr)
