"""Export digitised curves of Sheen (1991) to CSV with per-point uncertainty, and compute key features.
Usage: python -I export.py <workdir> <outdir>
Uncertainty model (per point, 1 sigma):
  sigma_perp (px, perpendicular to the curve):
     on ink, centroid of a single stroke (flag 0)            2.0 px
     DP override on ink (flag 5)                             2.0 px
     merged strokes, offset from run end (flag 3)            3.5 px
     null/peak tip (flag 4)                                  3.5 px
     bridged gap of a dashed/dash-dot curve (flag 1)         3 px + 0.15 * distance (px, along the track) to nearest on-ink point
     ran along the DP corridor edge (flag 2)                 5.0 px
     measured curve hidden under the model curve (coincident ranges, listed in the metadata)  6.0 px (half stroke)
  u_freq  = sqrt(cal_x_rms^2 + (sigma_perp*|n_x|/px_per_GHz)^2)
  u_value = sqrt(cal_y_rms^2 + (sigma_perp*|n_y|/px_per_unit)^2)
  n = unit normal of the curve in image px; cal_*_rms = rms residual of the tick calibration (data units).
  The point lies ON the curve; the pair (u_freq, u_value) describes the perpendicular uncertainty projected on the axes.
"""
import sys, os, json, math, csv
import numpy as np
from scipy import ndimage

wd, od = sys.argv[1], sys.argv[2]
os.makedirs(od, exist_ok=True)

COINCIDENT = {  # measured curve hidden under the model (FDTD) stroke: (figure, X-ranges)
    "fig3_33": [[4.68, 8.25], [10.97, 11.3], [15.88, 16.0], [17.75, 19.1]],
    "fig3_34": [[0.42, 1.93], [4.45, 5.35], [8.36, 10.55]],
}
XMIN = {"fig3_34": 0.6, "fig3_33": 0.12}

# ---- patches for the measured |S11| where the Fig. 3.33 measured trace is hidden under the FD-TD stroke:
# taken from the SAME measured data re-plotted in Figs. 3.27 / 3.29 (where the model curve is elsewhere)
from PIL import Image


def comp_centreline(cfgf, calf, near, inset=5.0, win=60):
    """centre line (3 points: ends inset by `inset` px and centroid) of the ink component nearest `near` (data units)"""
    cfg = json.load(open(cfgf, encoding="utf-8-sig")); cal = json.load(open(calf, encoding="utf-8-sig"))
    P, Q = cal["P"], cal["Q"]; M = np.array([[P[1], P[2]], [Q[1], Q[2]]])
    px, py = np.linalg.solve(M, [near[0] - P[0], near[1] - Q[0]])
    img = np.asarray(Image.open(cfg["image"]).convert("L"))
    x0, y0 = int(px - win), int(py - win)
    sub = img[y0:y0 + 2 * win, x0:x0 + 2 * win] < 128
    lab, n = ndimage.label(sub, structure=np.ones((3, 3)))
    best = None
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        d = np.min(np.hypot(xs - win, ys - win))
        if best is None or d < best[0]:
            best = (d, xs, ys)
    _, xs, ys = best
    xs = xs + x0; ys = ys + y0
    cov = np.cov(np.vstack([xs, ys])); ev, evec = np.linalg.eigh(cov); dv = evec[:, -1]
    pr = (xs - xs.mean()) * dv[0] + (ys - ys.mean()) * dv[1]
    out = []
    for q in ((pr.min() + inset, 0.0, pr.max() - inset) if pr.max() - pr.min() > 3 * inset else (0.0,)):
        sel = np.abs(pr - q) < 3
        mx, my = xs[sel].mean(), ys[sel].mean()
        out.append((P[0] + P[1] * mx + P[2] * my, Q[0] + Q[1] * mx + Q[2] * my))
    return sorted(out), cal


PATCH_ROWS = []
PATCH_DROP = {("fig3_33", "measured"): [lambda X, Y: (X >= 15.66) & (X <= 16.36),
                                       lambda X, Y: (X >= 4.85) & (X <= 5.15) & (Y < -28.4)],
              ("fig3_34", "measured"): [lambda X, Y: (X > 8.17) & (X < 8.68), lambda X, Y: X < 1.98],
              ("fig3_34", "fdtd"): [lambda X, Y: Y < -48.5]}
PATCH_SRC = {"fig3_33": [("fig3_27", 110, (15.865, -17.159)), ("fig3_29", 111, (16.086, -14.593)), ("fig3_29", 111, (16.267, -11.914))],
             "fig3_34": [("fig3_28", 110, (8.278, -32.70)), ("fig3_28", 110, (8.396, -29.90)), ("fig3_28", 110, (8.531, -27.23)),
                         ("fig3_28", 110, (8.647, -24.53))]}


def build_patches(wdir, target="fig3_33"):
    sd = os.path.dirname(os.path.abspath(__file__))
    rows = []
    # Fig 3.27 dot at the bottom of the ~15.9 GHz measured null; Fig 3.29 rising dash after it (target fig3_33)
    # Fig 3.28 dashes/dots of the measured |S21| rising edge 8.2-8.65 GHz (target fig3_34)
    for figname, page, near in PATCH_SRC[target]:
        pts, cal = comp_centreline(os.path.join(sd, "cfg_%s.json" % figname), os.path.join(wdir, "%s_calib.json" % figname), near)
        for X, Y in pts:
            rows.append(dict(freq_GHz=round(float(X), 5), value=round(float(Y), 4), quantity="S11_dB", curve="measured",
                             figure="Fig. " + figname[3:].replace("_", "."), thesis_page=page, pdf_page=page,
                             u_freq=round(math.hypot(cal["x_resid_rms"], 2.5 / cal["x_px_per_unit"]), 5),
                             u_value=round(math.hypot(cal["y_resid_rms"], 2.5 / cal["y_px_per_unit"]), 4)))
    if target == "fig3_33":
        # Fig 3.27: lowest measured ink of the ~5 GHz null, X 4.939-5.010 GHz, bottom -28.61 dB; centre = bottom + half stroke (0.21 dB)
        rows.append(dict(freq_GHz=4.975, value=-28.40, quantity="S11_dB", curve="measured", figure="Fig. 3.27", thesis_page=110, pdf_page=110,
                         u_freq=0.035, u_value=0.4))
    else:
        for r in rows:
            r["quantity"] = "S21_dB"
    return rows


def load(fig):
    t = json.load(open(os.path.join(wd, fig + "_track.json")))
    return t


def sigma_perp(flags, coincident_mask):
    f = np.asarray(flags)
    s = np.full(len(f), 2.0)
    s[(f == 3) | (f == 4)] = 3.5
    s[f == 2] = 5.0
    on = (f == 0) | (f == 5) | (f == 3) | (f == 4)
    if (f == 1).any():
        if on.any():
            dist = ndimage.distance_transform_edt(~on)
        else:
            dist = np.full(len(f), 50.0)
        s[f == 1] = 3.0 + 0.15 * dist[f == 1]
    s[coincident_mask] = np.maximum(s[coincident_mask], 6.0)
    return s


def curve_rows(t, cv, fig, quantity, label, step_px=4.0, clip=None):
    cal = t["calibration"]
    raw = cv["raw"]
    x = np.array([r["x_px"] for r in raw]); y = np.array([r["y_px"] for r in raw])
    X = np.array([r["X"] for r in raw]); Y = np.array([r["Y"] for r in raw])
    fl = np.array([r["flag"] for r in raw])
    keep = X >= XMIN.get(fig, -1e9)
    if clip is not None:
        keep &= (Y > clip)
    for fn in PATCH_DROP.get((fig, label), []):
        keep &= ~fn(X, Y)
    x, y, X, Y, fl = x[keep], y[keep], X[keep], Y[keep], fl[keep]
    coinc = np.zeros(len(X), bool)
    if label == "measured":
        for a, b in COINCIDENT.get(fig, []):
            coinc |= (X >= a) & (X <= b)
    sp = sigma_perp(fl, coinc)
    # unit normal from smoothed tangent
    tx = ndimage.uniform_filter1d(np.gradient(x), 9, mode="nearest"); ty = ndimage.uniform_filter1d(np.gradient(y), 9, mode="nearest")
    nn = np.hypot(tx, ty); nn[nn == 0] = 1
    nx, ny = -ty / nn, tx / nn
    pxu, pyu = cal["x_px_per_unit"], cal["y_px_per_unit"]
    uf = np.sqrt(cal["x_resid_rms"] ** 2 + (sp * np.abs(nx) / pxu) ** 2)
    uv = np.sqrt(cal["y_resid_rms"] ** 2 + (sp * np.abs(ny) / pyu) ** 2)
    # arc-length decimation
    s = np.concatenate([[0], np.cumsum(np.hypot(np.diff(x), np.diff(y)))])
    idx = [0]
    for i in range(1, len(s)):
        if s[i] - s[idx[-1]] >= step_px:
            idx.append(i)
    if idx[-1] != len(s) - 1:
        idx.append(len(s) - 1)
    rows = []
    for i in idx:
        rows.append(dict(freq_GHz=round(float(X[i]), 5), value=round(float(Y[i]), 4), quantity=quantity, curve=label,
                         figure=t["figure"].replace("fig", "Fig. ").replace("_", "."), thesis_page=t["thesis_page"], pdf_page=t["pdf_page"],
                         u_freq=round(float(uf[i]), 5), u_value=round(float(uv[i]), 4)))
    full = dict(X=X, Y=Y, uf=uf, uv=uv, fl=fl, coinc=coinc)
    return rows, full


FIELDS = ["freq_GHz", "value", "quantity", "curve", "figure", "thesis_page", "pdf_page", "u_freq", "u_value"]


def write(name, rows):
    with open(os.path.join(od, name), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS); w.writeheader()
        for r in rows:
            w.writerow({k: r[k] for k in FIELDS})
    print("wrote", name, len(rows))


feat = {}


def local_extrema(X, Y, kind, windows):
    out = []
    for a, b in windows:
        m = (X >= a) & (X <= b)
        if not m.any():
            continue
        i = np.argmin(Y[m]) if kind == "min" else np.argmax(Y[m])
        out.append({"window_GHz": [a, b], "freq_GHz": round(float(X[m][i]), 4), "value": round(float(Y[m][i]), 3)})
    return out


def crossings(X, Y, level, a, b, direction):
    m = (X >= a) & (X <= b)
    Xs, Ys = X[m], Y[m]
    o = np.argsort(Xs); Xs, Ys = Xs[o], Ys[o]
    res = []
    for i in range(len(Xs) - 1):
        if direction == "down" and Ys[i] >= level > Ys[i + 1] or direction == "up" and Ys[i] < level <= Ys[i + 1]:
            f = Xs[i] + (level - Ys[i]) * (Xs[i + 1] - Xs[i]) / (Ys[i + 1] - Ys[i])
            res.append(round(float(f), 4))
    return res


# ---------------- Fig 3.33 (|S11|) and Fig 3.34 (|S21|)
allrows = []
for fig, q in (("fig3_33", "S11_dB"), ("fig3_34", "S21_dB")):
    t = load(fig)
    rows = []
    fulls = {}
    for cv in t["curves"]:
        lab = "fdtd" if cv["name"].startswith("fdtd") else "measured"
        r, full = curve_rows(t, cv, fig, q, lab)
        if lab == "measured":
            pr = build_patches(wd, fig)
            r = sorted(r + pr, key=lambda z: z["freq_GHz"])
            full = dict(full)
            full["X"] = np.concatenate([full["X"], [p["freq_GHz"] for p in pr]])
            full["Y"] = np.concatenate([full["Y"], [p["value"] for p in pr]])
            full["uf"] = np.concatenate([full["uf"], [p["u_freq"] for p in pr]])
            full["uv"] = np.concatenate([full["uv"], [p["u_value"] for p in pr]])
            o = np.argsort(full["X"], kind="stable")
            for k in ("X", "Y", "uf", "uv"):
                full[k] = full[k][o]
            print("patch rows:", [(p["figure"], p["freq_GHz"], p["value"]) for p in pr])
        rows += r; fulls[lab] = full
    write(f"{fig}_{q}.csv", rows); allrows += rows
    feat[fig] = {"calibration": {k: t["calibration"][k] for k in ("X_of_px", "Y_of_px", "x_resid_rms", "x_resid_max", "y_resid_rms", "y_resid_max", "n_xticks", "n_yticks", "frame_values")}}
    for lab, fu in fulls.items():
        X, Y = fu["X"], fu["Y"]
        d = {}
        if fig == "fig3_33":
            d["nulls"] = local_extrema(X, Y, "min", [[0.1, 0.6], [4.5, 5.5], [10.3, 11.4], [15.0, 16.3]])
            d["passband_max_0.5_4.5GHz"] = local_extrema(X, Y, "max", [[0.5, 4.5]])
            d["band_6_10GHz_max"] = local_extrema(X, Y, "max", [[5.5, 10.0]])
            d["band_11_15GHz_max"] = local_extrema(X, Y, "max", [[11.0, 15.0]])
            d["band_16_20GHz_max"] = local_extrema(X, Y, "max", [[16.0, 20.0]])
            for f0 in (1.0, 2.0, 3.0, 4.0):
                m = np.abs(X - f0) < 0.03
                if m.any():
                    d[f"value_at_{f0}GHz"] = round(float(np.mean(Y[m])), 3)
        else:
            d["passband_max_0.5_5.5GHz"] = local_extrema(X, Y, "max", [[0.5, 5.5]])
            d["minus3dB_falling"] = crossings(X, Y, -3.0, 4.5, 7.0, "down")
            d["minus1dB_falling"] = crossings(X, Y, -1.0, 4.5, 7.0, "down")
            d["minus10dB_falling"] = crossings(X, Y, -10.0, 4.5, 7.5, "down")
            d["minus20dB_falling"] = crossings(X, Y, -20.0, 4.5, 7.5, "down")
            d["minus3dB_rising"] = crossings(X, Y, -3.0, 8.0, 12.0, "up")
            d["minus20dB_rising"] = crossings(X, Y, -20.0, 8.0, 12.0, "up")
            d["stopband_minima"] = local_extrema(X, Y, "min", [[6.5, 7.3], [7.6, 8.3], [18.5, 20.0]])
            d["stopband_local_max_7.2_7.8"] = local_extrema(X, Y, "max", [[7.2, 7.8]])
            d["upper_passband_max_10_14"] = local_extrema(X, Y, "max", [[10.0, 14.0]])
            for f0 in (1.0, 2.0, 2.5, 3.0, 4.0, 5.0):
                m = np.abs(X - f0) < 0.03
                if m.any():
                    d[f"value_at_{f0}GHz"] = round(float(np.mean(Y[m])), 3)
            d["below_frame_bottom_points"] = int(np.sum(Y < -49.5))
            d["minus40dB_falling"] = crossings(X, Y, -40.0, 6.5, 8.2, "down")
            d["minus40dB_rising"] = crossings(X, Y, -40.0, 7.6, 9.0, "up")
            d["minus48.5dB_falling"] = crossings(X, Y, -48.4, 7.5, 8.2, "down")
            d["minus48.5dB_rising"] = crossings(X, Y, -48.4, 7.9, 8.6, "up")
            if lab == "fdtd":
                tr = load(fig)["curves"][0]["raw"]
                bx = [r["X"] for r in tr if r["Y"] < -49.3]
                d["fdtd_at_frame_bottom_X_range_GHz"] = [round(min(bx), 3), round(max(bx), 3)] if bx else None
        # typical uncertainty
        d["u_freq_median"] = round(float(np.median(fu["uf"])), 4); d["u_value_median"] = round(float(np.median(fu["uv"])), 4)
        d["u_value_p90"] = round(float(np.percentile(fu["uv"], 90)), 4); d["u_freq_p90"] = round(float(np.percentile(fu["uf"], 90)), 4)
        feat[fig][lab] = d

# ---------------- Fig 4.14 (Zin = R + jX), markers, Smith chart
t = load("fig4_14")
rows = []
fz = {}
for cv in t["curves"]:
    q = "Zin_R_ohm" if cv["name"].startswith("R") else "Zin_X_ohm"
    r, full = curve_rows(t, cv, "fig4_14", q, "fdtd", step_px=3.0)
    rows += r; fz[q] = full
cal = t["calibration"]
mk = json.load(open(os.path.join(wd, "fig4_14_markers.json")))
for m in mk:
    q = "Zin_R_ohm" if m["name"].startswith("R") else "Zin_X_ohm"
    sp = 2.0 if m["type"] == "star" else (3.0 if m.get("hole_area_px", 999) < 400 else 1.5)
    rows.append(dict(freq_GHz=round(m["X_found"], 5), value=round(m["Y_found"], 3), quantity=q, curve="measured", figure="Fig. 4.14",
                     thesis_page=162, pdf_page=162, u_freq=round(math.hypot(cal["x_resid_rms"], sp / cal["x_px_per_unit"]), 5),
                     u_value=round(math.hypot(cal["y_resid_rms"], sp / cal["y_px_per_unit"]), 3)))
sm = json.load(open(os.path.join(wd, "fig4_15_smith.json")))
uG = math.hypot(sm["circle"]["resid_rms_px"], 1.5) / sm["circle"]["R"]
freqs = [2.10, 2.15, 2.20, 2.25, 2.30, 2.35, 2.40]
smith_rows = []
for f0, m in zip(freqs, sm["markers"]):
    g = complex(m["G_re"], m["G_im"])
    for q, v, u in (("S11_dB_Fig4.15_Smith", m["G_dB"], 8.686 * uG / abs(g)), ("Gamma_re_Fig4.15", m["G_re"], uG), ("Gamma_im_Fig4.15", m["G_im"], uG),
                    ("Zin_R_ohm_from_Smith_Z0_50", m["R_ohm"], None), ("Zin_X_ohm_from_Smith_Z0_50", m["X_ohm"], None)):
        if u is None:
            # propagate: dZ = 2 Z0 dG / (1-G)^2
            u = 2 * 50 * uG / abs(1 - g) ** 2
        smith_rows.append(dict(freq_GHz=f0, value=round(v, 4), quantity=q, curve="measured", figure="Fig. 4.15", thesis_page=163, pdf_page=163,
                               u_freq=0.0, u_value=round(u, 4)))
# FDTD |S11| derived from the digitised R and X curves (Z0 = 50 ohm), on a common grid
R = fz["Zin_R_ohm"]; Xc = fz["Zin_X_ohm"]
grid = np.arange(2.005, 2.4951, 0.0025)
def interp(full, g):
    o = np.argsort(full["X"]); return np.interp(g, full["X"][o], full["Y"][o]), np.interp(g, full["X"][o], full["uv"][o])
Rg, uR = interp(R, grid); Xg, uX = interp(Xc, grid)
Z = Rg + 1j * Xg
G = (Z - 50) / (Z + 50)
GdB = 20 * np.log10(np.abs(G))
# uncertainty by finite differences
dG_dR = np.abs(((Z + 1 - 50) / (Z + 1 + 50)) - G); dG_dX = np.abs(((Z + 1j - 50) / (Z + 1j + 50)) - G)
uGd = np.sqrt((dG_dR * uR) ** 2 + (dG_dX * uX) ** 2)
for f0, v, u in zip(grid, GdB, 8.686 * uGd / np.abs(G)):
    rows.append(dict(freq_GHz=round(float(f0), 5), value=round(float(v), 4), quantity="S11_dB_derived_from_digitised_RX_Z0_50", curve="fdtd",
                     figure="Fig. 4.14", thesis_page=162, pdf_page=162, u_freq=0.0, u_value=round(float(u), 4)))
write("fig4_14_Zin.csv", rows); allrows += rows
write("fig4_15_smith_measured.csv", smith_rows); allrows += smith_rows
write("all_digitised.csv", allrows)

# features for the patch
Rx = R["X"]; Ry = R["Y"]
iR = int(np.argmax(Ry))
o = np.argsort(Xc["X"]); XX, XY = Xc["X"][o], Xc["Y"][o]
zc = [round(float(XX[i] + (0 - XY[i]) * (XX[i + 1] - XX[i]) / (XY[i + 1] - XY[i])), 4) for i in range(len(XX) - 1)
      if XY[i] > 0 >= XY[i + 1] and 2.2 < XX[i] < 2.35]
iG = int(np.argmin(GdB))
mR = [m for m in mk if m["name"].startswith("R")]; mX = [m for m in mk if m["name"].startswith("X")]
feat["fig4_14"] = {
    "calibration": {k: cal[k] for k in ("X_of_px", "Y_of_px", "x_resid_rms", "x_resid_max", "y_resid_rms", "y_resid_max", "n_xticks", "n_yticks", "frame_values")},
    "fdtd": {"R_max_ohm": round(float(Ry[iR]), 2), "f_Rmax_GHz": round(float(Rx[iR]), 4),
             "X_max_ohm": local_extrema(XX, XY, "max", [[2.2, 2.29]]), "X_min_ohm": local_extrema(XX, XY, "min", [[2.28, 2.35]]),
             "X_zero_crossing_GHz_descending": zc,
             "S11_dB_min_derived_Z0_50": round(float(GdB[iG]), 3), "f_S11min_GHz": round(float(grid[iG]), 4)},
    "measured_markers_fig4_14": [{"name": m["name"], "freq_GHz": round(m["X_found"], 4), "value_ohm": round(m["Y_found"], 2)} for m in mk],
    "measured_smith_fig4_15": [{"freq_GHz": f0, "G_re": round(m["G_re"], 4), "G_im": round(m["G_im"], 4), "S11_dB": round(m["G_dB"], 3),
                                "R_ohm": round(m["R_ohm"], 1), "X_ohm": round(m["X_ohm"], 1)} for f0, m in zip(freqs, sm["markers"])],
    "smith_calibration": {"circle": sm["circle"], "axis_slope": sm["axis_slope"], "axis_offset_px": sm["axis_offset_px"],
                          "r_checks_G_real": [round(c["G_real"], 4) for c in sm["r_checks"]], "u_Gamma": round(uG, 4)},
}
# measured S11 from Fig 4.14 markers (Z0 = 50) for comparison with the Smith chart
cmp = []
for f0 in freqs:
    r_ = [m for m in mR if abs(m["X_found"] - f0) < 0.01][0]; x_ = [m for m in mX if abs(m["X_found"] - f0) < 0.01][0]
    z = complex(r_["Y_found"], x_["Y_found"]); g = (z - 50) / (z + 50)
    sm_m = sm["markers"][freqs.index(f0)]
    cmp.append({"freq_GHz": f0, "R_fig414": round(z.real, 1), "X_fig414": round(z.imag, 1), "S11_dB_from_fig414_Z0_50": round(20 * math.log10(abs(g)), 3),
                "R_smith": round(sm_m["R_ohm"], 1), "X_smith": round(sm_m["X_ohm"], 1), "S11_dB_smith": round(sm_m["G_dB"], 3)})
feat["fig4_14"]["measured_cross_check_414_vs_415"] = cmp
json.dump(feat, open(os.path.join(od, "features.json"), "w"), indent=1)
print(json.dumps(feat, indent=1)[:200])
