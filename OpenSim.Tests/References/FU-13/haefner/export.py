"""Export digitised curves to CSV (DIGITISED from the figures, not printed values) and run cross-checks.
Usage: python -I export.py <workdir> <outdir>
Reads <workdir>/fig5_border_digitized.json, fig6_digitized.json, fig7_digitized.json, fig8_digitized.json.
Writes <outdir>/fig{5,6,7,8}_digitized.csv and <outdir>/digitized_checks.json.

Uncertainty model per tabulated point (1 sigma, in R units):
  u_cal   = calibration fit rms in R, plus the rms in p times the local slope dR/dp
  u_trace = local scatter of the traced centre-line (sd of residuals of the local linear fit)
  u_draw  = drafting/line-centre allowance: 0.5 px of line-centre ambiguity converted to R
           along the normal of the curve (both axes), using the figure's px/unit
  u_val   = figure-level validation term: rms residual of the digitised round-wire curve against the exact
            Bessel ratio on the same figure (Fig. 5 for fig5/fig6 scale class, Fig. 7 for fig7/fig8)
  u_total = sqrt(sum of squares)
"""
import sys, os, json, math, csv
import numpy as np
from scipy.special import jv

wd, od = sys.argv[1], sys.argv[2]
os.makedirs(od, exist_ok=True)


def ratio_round(p):
    p = np.asarray(p, dtype=float)
    ka = (1 - 1j) * p / math.sqrt(2)
    with np.errstate(all="ignore"):
        out = np.real(ka / 2 * jv(0, ka) / jv(1, ka))
    return np.where(p < 1e-6, 1.0, out)


# valid p-ranges per curve (beyond these the trace is in a merge/crossing zone or past the drawn end);
# chosen from the overlays (work/*_overlay_full.png) and the measured-fraction of the raw trace.
VALID = {
    "fig5": {"round_theoretical": (1.0, 8.4, "Fig. 5 theoretical round-wire curve (No. 0 B&S test points on it)")},
    "fig6": {"sf2400": (2.7, 4.9, ""), "sf6": (3.9, 6.0, ""), "sf4": (3.9, 5.0, ""), "sf5": (3.9, 4.8, ""),
             "sf8": (4.2, 6.0, "label '10' also lies along this line near p~5.1: 8 vs 10 identity uncertain"),
             "sf12": (4.5, 6.0, ""), "sf20": (4.5, 6.0, "")},
    "fig7": {"round": (0.6, 3.5, ""), "ratio_2400": (0.6, 5.0, ""), "ratio_5": (0.6, 3.85, ""),
             "ratio_8": (0.6, 4.3, ""), "ratio_10": (0.6, 4.3, ""), "ratio_16": (0.6, 4.7, ""), "ratio_24": (0.6, 5.0, ""),
             "dashed_C": (0.6, 2.2, "dashed Dwight thin-strip series curve, lower branch")},
    "fig8": {"trunk": (1.1, 3.0, "common trunk of all test curves below p~3"), "sf1_test": (3.1, 7.8, ""),
             "sf2_test": (3.1, 8.0, ""), "sf6_test": (3.4, 8.8, ""), "sf12_test": (3.5, 10.8, ""), "sf18_test": (3.5, 11.9, "")},
}
MERGED = {  # p-ranges where a curve shares ink with others (value is the common band, not curve-specific)
    "fig7": {"ratio_5": [(0.6, 2.05), (2.45, 2.9)], "ratio_8": [(0.6, 2.9)], "ratio_10": [(0.6, 2.9)], "ratio_16": [(0.6, 2.9)],
             "ratio_24": [(0.6, 2.9)], "round": [(2.3, 2.9)], "ratio_2400": [(2.3, 2.8)]},
    "fig8": {"trunk": [(1.1, 3.0)]},
}

files = {"fig5": "fig5_border_digitized.json", "fig6": "fig6_digitized.json", "fig7": "fig7_digitized.json", "fig8": "fig8_digitized.json"}
data = {k: json.load(open(os.path.join(wd, v))) for k, v in files.items()}

# validation term from the round-wire curves
def val_rms(fig, name, lo, hi):
    cv = [c for c in data[fig]["curves"] if c["name"] == name][0]
    t = [(r["X"], r["Y"]) for r in cv["table"] if r["Y"] is not None and lo <= r["X"] <= hi]
    X = np.array([a for a, b in t]); Y = np.array([b for a, b in t])
    d = Y - ratio_round(X)
    return float(np.sqrt(np.mean(d ** 2))), float(np.mean(d)), float(np.max(np.abs(d))), len(X)

checks = {}
v5 = val_rms("fig5", "round_theoretical", 1.0, 8.4)
v7 = val_rms("fig7", "round", 0.8, 3.4)
checks["fig5_round_vs_bessel"] = dict(zip(["rms", "mean", "max_abs", "n"], v5))
checks["fig7_round_vs_bessel"] = dict(zip(["rms", "mean", "max_abs", "n"], v7))
UVAL = {"fig5": v5[0], "fig6": v5[0], "fig7": v7[0], "fig8": v7[0]}

summary = {}
for fig, d in data.items():
    cal = d["calibration"]
    px_x, px_y = abs(cal["x_px_per_unit"]), abs(cal["y_px_per_unit"])
    rows = []
    for cv in d["curves"]:
        if cv["name"] not in VALID.get(fig, {}):
            continue
        lo, hi, note = VALID[fig][cv["name"]]
        tab = [t for t in cv["table"] if t["Y"] is not None and lo - 1e-9 <= t["X"] <= hi + 1e-9]
        if not tab:
            continue
        X = np.array([t["X"] for t in tab]); Y = np.array([t["Y"] for t in tab])
        slope = np.gradient(Y, X) if len(X) > 2 else np.zeros_like(X)
        for t, s in zip(tab, slope):
            u_cal = math.hypot(cal["y_resid_rms_units"], cal["x_resid_rms_units"] * s)
            u_tr = t.get("scatter_sd") or 0.0
            # 0.5 px normal displacement -> R change: dn * sqrt(1/px_y^2 + (s/px_x)^2) / ... (vertical equiv.)
            u_draw = 0.5 * math.sqrt((1 / px_y) ** 2 + (s / px_x) ** 2)
            u_val = UVAL[fig]
            u_tot = math.sqrt(u_cal ** 2 + u_tr ** 2 + u_draw ** 2 + u_val ** 2)
            merged = any(a <= t["X"] <= b for a, b in MERGED.get(fig, {}).get(cv["name"], []))
            rows.append({"figure": fig, "curve": cv["name"], "label": cv["label"], "p": round(t["X"], 4),
                         "R_ratio": round(t["Y"], 4), "n_trace_pts": t["n"], "u_cal": round(u_cal, 4), "u_trace": round(u_tr, 4),
                         "u_draw": round(u_draw, 4), "u_val": round(u_val, 4), "u_total": round(u_tot, 4),
                         "merged_zone": int(merged), "note": note})
        summary.setdefault(fig, {})[cv["name"]] = {"p_range": [float(X.min()), float(X.max())], "n": len(X)}
    fn = os.path.join(od, f"{fig}_digitized.csv")
    with open(fn, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader(); w.writerows(rows)
    print(fn, len(rows))

def at(fig, name, p):
    cv = [c for c in data[fig]["curves"] if c["name"] == name][0]
    t = [r for r in cv["table"] if r["Y"] is not None]
    X = np.array([r["X"] for r in t]); Y = np.array([r["Y"] for r in t])
    return float(np.interp(p, X, Y)) if X.min() <= p <= X.max() else None

# Dwight check on Fig. 7 (text p.445: at p=2 calculated is 1.81 % below measured; ~2 % below for 5:1 and 24:1)
c = {}
for p in (1.0, 1.5, 2.0):
    dC = at("fig7", "dashed_C", p)
    c[f"p={p}"] = {k: at("fig7", k, p) for k in ("ratio_5", "ratio_8", "ratio_24", "ratio_2400", "round")}
    c[f"p={p}"]["dashed_C"] = dC
    if dC:
        c[f"p={p}"]["pct_C_below_ratio5"] = 100 * (c[f"p={p}"]["ratio_5"] - dC) / c[f"p={p}"]["ratio_5"]
        c[f"p={p}"]["pct_C_below_ratio24"] = 100 * (c[f"p={p}"]["ratio_24"] - dC) / c[f"p={p}"]["ratio_24"]
checks["fig7_dwight_vs_test"] = c
# worked example p.445: shape factor 5, p = 3.92 -> 1.48 from Fig. 6
checks["fig6_sf5_at_p3.92"] = at("fig6", "sf5", 3.92)
# Fig. 6 vs Fig. 7 overlap (similitude figure vs small-ratio figure)
ov = {}
for p in (4.0, 4.2, 4.4, 4.6, 4.8):
    ov[f"p={p}"] = {"fig6_sf5": at("fig6", "sf5", p), "fig7_ratio_5": at("fig7", "ratio_5", p),
                    "fig6_sf8": at("fig6", "sf8", p), "fig7_ratio_8": at("fig7", "ratio_8", p), "fig7_ratio_10": at("fig7", "ratio_10", p),
                    "fig6_sf2400": at("fig6", "sf2400", p), "fig7_ratio_2400": at("fig7", "ratio_2400", p),
                    "fig6_sf20": at("fig6", "sf20", p), "fig7_ratio_24": at("fig7", "ratio_24", p), "fig7_ratio_16": at("fig7", "ratio_16", p)}
checks["fig6_vs_fig7"] = ov
# Fig. 8: slope of test curves at high p vs Cockroft square 0.418 p, and intercept
sl = {}
for name, lo, hi in (("sf1_test", 5.0, 7.8), ("sf2_test", 5.0, 8.0), ("sf6_test", 6.0, 8.8), ("sf12_test", 7.0, 10.8), ("sf18_test", 8.0, 11.9)):
    cv = [cc for cc in data["fig8"]["curves"] if cc["name"] == name][0]
    t = [r for r in cv["table"] if r["Y"] is not None and lo <= r["X"] <= hi]
    X = np.array([r["X"] for r in t]); Y = np.array([r["Y"] for r in t])
    co = np.polyfit(X, Y, 1)
    sl[name] = {"fit_range": [lo, hi], "slope": float(co[0]), "intercept": float(co[1]), "n": len(X)}
sl["cockroft_square_slope_1.86/(pi*sqrt2)"] = 1.86 / (math.pi * math.sqrt(2))
checks["fig8_linear_fits"] = sl
# Fig. 5/7 round at the Table II p values
checks["fig5_at_tableII_p"] = {p: at("fig5", "round_theoretical", p) for p in (1.989, 2.812, 3.978, 4.871, 5.627, 6.291, 6.889, 7.441, 8.201)}
checks["fig6_sf5_at_p3.92"] = None
cv = [cc for cc in data["fig6"]["curves"] if cc["name"] == "sf5"][0]
raw = [r for r in cv["raw"] if r["flag"] == 0]
Xr = np.array([r["X"] for r in raw]); Yr = np.array([r["Y"] for r in raw])
sel = np.abs(Xr - 3.92) <= 0.06
if sel.sum() >= 4:
    checks["fig6_sf5_at_p3.92"] = {"R": float(np.polyval(np.polyfit(Xr[sel] - 3.92, Yr[sel], 1), 0.0)), "n": int(sel.sum()),
                                   "printed_p445": 1.48}
# Fig. 6 vs Fig. 8 (same F&G-type curves replotted?)
cmp68 = {}
for p in (5.0, 5.5, 5.9):
    cmp68[f"p={p}"] = {"fig6_sf6": at("fig6", "sf6", p), "fig8_sf6": at("fig8", "sf6_test", p),
                       "fig6_sf12": at("fig6", "sf12", p), "fig8_sf12": at("fig8", "sf12_test", p),
                       "fig6_sf20": at("fig6", "sf20", p), "fig8_sf18": at("fig8", "sf18_test", p)}
checks["fig6_vs_fig8"] = cmp68
# uncertainty summary per figure
us = {}
for fig in ("fig5", "fig6", "fig7", "fig8"):
    with open(os.path.join(od, f"{fig}_digitized.csv"), encoding="utf-8") as f:
        rr = list(csv.DictReader(f))
    u = np.array([float(r["u_total"]) for r in rr])
    us[fig] = {"median_u_total": float(np.median(u)), "p90_u_total": float(np.percentile(u, 90)), "max_u_total": float(u.max()), "rows": len(rr)}
checks["uncertainty_summary"] = us
checks["summary"] = summary
json.dump(checks, open(os.path.join(od, "digitized_checks.json"), "w"), indent=1)
print(json.dumps({k: v for k, v in checks.items() if k != "summary"}, indent=1))
