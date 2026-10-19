"""Own re-digitisation of the measured (dash-dot) curve where it runs just BELOW the FD-TD line.
redigit.py cal.json out.csv mode f0 f1 step vmin vmax [hv_f0 hv_f1 hv_v0 hv_v1 hv_step]
mode 'below': measured lies below (more negative) than the FD-TD line in this range.
Vertical probes (constant f): if two separate runs -> lowest run centre; if one run wider than a single
line by >=4 px -> bottom edge + half single-line thickness; if one single-width run -> no measured ink (dash gap).
Optional horizontal probes (constant value, for the steep roll-off): measured lies at LOWER frequency than
FD-TD; separate runs -> leftmost run centre; merged -> left edge + half single-line horizontal width.
Single-line thickness is measured from isolated single runs inside the same range (median)."""
import sys, json, csv
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
cal = json.load(open(sys.argv[1]))
a = np.array(cal['x_coef']); b = np.array(cal['y_coef'])
M = np.array([[a[1], a[2]], [b[1], b[2]]]); Minv = np.linalg.inv(M)
im = np.asarray(Image.open(cal['page']).convert('L'), dtype=np.float32)
to_px = lambda f, v: Minv @ np.array([f - a[0], v - b[0]])
to_d = lambda px, py: (a[0] + a[1] * px + a[2] * py, b[0] + b[1] * px + b[2] * py)
cal_rms_v = cal['y_rms']; cal_rms_f = cal['x_rms']
out_path = sys.argv[2]; mode = sys.argv[3]
f0, f1, step, vmin, vmax = map(float, sys.argv[4:9])


def vruns(f):
    p0 = to_px(f, vmax); p1 = to_px(f, vmin)
    n = int(abs(p1[1] - p0[1])) + 1
    ys = np.linspace(p0[1], p1[1], n); xs = np.linspace(p0[0], p1[0], n)
    vals = np.array([im[int(round(y)), int(round(x)) - 1:int(round(x)) + 2].mean() for x, y in zip(xs, ys)])
    ink = vals < 128
    d = np.diff(np.concatenate([[0], ink.astype(int), [0]]))
    s = np.where(d == 1)[0]; e = np.where(d == -1)[0]
    return [(to_d(xs[i], ys[i])[1], to_d(xs[j - 1], ys[j - 1])[1], j - i) for i, j in zip(s, e)]


def hruns(v, g0, g1):
    p0 = to_px(g0, v); p1 = to_px(g1, v)
    n = int(abs(p1[0] - p0[0])) + 1
    xs = np.linspace(p0[0], p1[0], n); ys = np.linspace(p0[1], p1[1], n)
    vals = np.array([im[int(round(y)) - 1:int(round(y)) + 2, int(round(x))].mean() for x, y in zip(xs, ys)])
    ink = vals < 128
    d = np.diff(np.concatenate([[0], ink.astype(int), [0]]))
    s = np.where(d == 1)[0]; e = np.where(d == -1)[0]
    return [(to_d(xs[i], ys[i])[0], to_d(xs[j - 1], ys[j - 1])[0], j - i) for i, j in zip(s, e)]


fs = np.round(np.arange(f0, f1 + 1e-9, step), 4)
allv = {f: vruns(f) for f in fs}
# single-line vertical thickness (px and dB) from frequencies with exactly two separate runs
singles = [r for f in fs if len(allv[f]) >= 2 for r in allv[f]]
w_single = float(np.median([r[2] for r in singles])) if singles else 12.0
dv_per_px = abs(b[2])
h_db = (w_single * dv_per_px) / 2
print('single-line vertical thickness: %.1f px = %.3f dB (n=%d)' % (w_single, w_single * dv_per_px, len(singles)))
rows = []
for f in fs:
    rr = allv[f]
    if not rr:
        continue
    if len(rr) >= 2:
        low = min(rr, key=lambda r: r[1])
        v = (low[0] + low[1]) / 2; how = 'separate run centre'; u = np.hypot(cal_rms_v, 0.05)
    elif rr[0][2] >= w_single + 4:
        v = rr[0][1] + h_db; how = 'merged: bottom edge + half line'; u = np.hypot(cal_rms_v, 0.08)
    else:
        continue
    rows.append((float(f), float(v), how, float(u), float(cal_rms_f + 0.005)))
if len(sys.argv) > 9:
    g0, g1, v0, v1, vs = map(float, sys.argv[9:14])
    vals = np.round(np.arange(v0, v1 - 1e-9, -abs(vs)), 3)
    allh = {v: hruns(v, g0, g1) for v in vals}
    sing_h = [r for v in vals if len(allh[v]) >= 2 for r in allh[v]]
    wh = float(np.median([r[2] for r in sing_h])) if sing_h else 12.0
    df_per_px = abs(a[1])
    hf = wh * df_per_px / 2
    print('single-line horizontal width: %.1f px = %.4f GHz (n=%d)' % (wh, wh * df_per_px, len(sing_h)))
    for v in vals:
        rr = allh[v]
        if not rr:
            continue
        if len(rr) >= 2:
            left = min(rr, key=lambda r: r[0])
            f = (left[0] + left[1]) / 2; how = 'h: separate run centre'; uf = np.hypot(cal_rms_f, 0.01)
        elif rr[0][2] >= wh + 4:
            f = rr[0][0] + hf; how = 'h: merged: left edge + half line'; uf = np.hypot(cal_rms_f, 0.02)
        else:
            continue
        rows.append((float(f), float(v), how, float(np.hypot(cal_rms_v, 0.03)), float(uf)))
rows.sort()
with open(out_path, 'w', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['freq_GHz', 'value', 'method', 'u_value', 'u_freq'])
    for r in rows:
        w.writerow(['%.4f' % r[0], '%.3f' % r[1], r[2], '%.3f' % r[3], '%.4f' % r[4]])
for r in rows:
    print('%.3f  %.3f  %s' % (r[0], r[1], r[2]))
