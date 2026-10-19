"""Own axis calibration from tick marks (independent of the other agent's code).

calib.py cfg.json out.json

cfg: {"page": ..., "frame": [L, R, T, B] approx pixel centres of frame lines,
      "x_ticks": {"v0": 0, "dv": 1, "n": 21}  values of the ticks along x (both top and bottom axes),
      "y_ticks": {"v0": 10, "dv": -10, "n": 7} values along y (top to bottom; both left and right),
      "x_guess": [px_of_v0, px_of_last], "y_guess": [py_of_v0, py_of_last],
      "band": [4, 18]  (tick search band, px inward from the frame line edge),
      "exclude_x": [values], "exclude_y": [values]}
Fits each frame edge with a robust line, finds tick centroids in a band just inside each
edge, assigns them to the nearest expected value, then least-squares fits
  v_x = a0 + a1*px + a2*py ,  v_y = b0 + b1*px + b2*py
Reports residuals."""
import sys, json
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

cfg = json.load(open(sys.argv[1]))
im = np.asarray(Image.open(cfg['page']).convert('L'), dtype=np.float32)
ink = im < 128
L, R, T, B = cfg['frame']
band0, band1 = cfg.get('band', [4, 18])


def runs(v):
    """return list of (start, end) of True runs in 1-D bool array"""
    d = np.diff(np.concatenate([[0], v.astype(int), [0]]))
    s = np.where(d == 1)[0]
    e = np.where(d == -1)[0]
    return list(zip(s, e))


def fit_edge(horizontal, approx, lo, hi, win=25):
    """robust fit of a frame line; returns (m, c, halfwidth) with centre = m*t + c"""
    ts, cs, ws = [], [], []
    for t in range(int(lo), int(hi), 3):
        if horizontal:
            col = ink[int(approx - win):int(approx + win), t]
        else:
            col = ink[t, int(approx - win):int(approx + win)]
        best = None
        for s, e in runs(col):
            c = approx - win + (s + e - 1) / 2.0
            if e - s >= 4 and (best is None or abs(c - approx) < abs(best[0] - approx)):
                best = (c, e - s)
        if best is not None:
            ts.append(t); cs.append(best[0]); ws.append(best[1])
    ts, cs, ws = map(np.asarray, (ts, cs, ws))
    keep = np.ones(len(ts), bool)
    for _ in range(5):
        p = np.polyfit(ts[keep], cs[keep], 1)
        r = cs - np.polyval(p, ts)
        sd = 1.4826 * np.median(np.abs(r[keep] - np.median(r[keep])))
        keep = np.abs(r) < max(3 * sd, 0.75)
    return float(p[0]), float(p[1]), float(np.median(ws[keep]) / 2.0), int(keep.sum())


edges = {}
edges['B'] = fit_edge(True, B, L + 20, R - 20)
edges['T'] = fit_edge(True, T, L + 20, R - 20)
edges['L'] = fit_edge(False, L, T + 20, B - 20)
edges['R'] = fit_edge(False, R, T + 20, B - 20)


def ticks_along(edge, horizontal, inward):
    """find tick centroids in a band just inside the frame line.
    horizontal edge: ticks are vertical strokes; returns list of (px, py)"""
    m, c, hw = edges[edge][:3]
    out = []
    if horizontal:
        lo, hi = L + 8, R - 8
        prof = []
        for x in range(int(lo), int(hi)):
            yc = m * x + c
            ys = yc + inward * (hw + np.arange(band0, band1))
            prof.append(ink[ys.astype(int), x].mean())
        prof = np.asarray(prof)
        for s, e in runs(prof > 0.6):
            if 3 <= e - s <= 30:
                xs = np.arange(s, e) + lo
                w = prof[s:e]
                xc = float((xs * w).sum() / w.sum())
                out.append((xc, m * xc + c, e - s))
    else:
        lo, hi = T + 8, B - 8
        prof = []
        for y in range(int(lo), int(hi)):
            xc0 = m * y + c
            xs = xc0 + inward * (hw + np.arange(band0, band1))
            prof.append(ink[y, xs.astype(int)].mean())
        prof = np.asarray(prof)
        for s, e in runs(prof > 0.6):
            if 3 <= e - s <= 30:
                ys = np.arange(s, e) + lo
                w = prof[s:e]
                yc = float((ys * w).sum() / w.sum())
                out.append((m * yc + c, yc, e - s))
    return out


def assign(found, v0, dv, n, p0, p1, along):
    """assign found ticks (coordinate index along) to expected values by initial linear guess"""
    vals = v0 + dv * np.arange(n)
    pos = p0 + (p1 - p0) * np.arange(n) / (n - 1)
    step = abs(p1 - p0) / (n - 1)
    res = []
    for t in found:
        k = int(np.argmin(np.abs(pos - t[along])))
        if abs(pos[k] - t[along]) < 0.3 * step:
            res.append((t[0], t[1], float(vals[k]), t[2]))
    return res


xt, yt = cfg['x_ticks'], cfg['y_ticks']
xg, yg = cfg['x_guess'], cfg['y_guess']
tk = {}
tk['B'] = assign(ticks_along('B', True, -1), xt['v0'], xt['dv'], xt['n'], xg[0], xg[1], 0)
tk['T'] = assign(ticks_along('T', True, +1), xt['v0'], xt['dv'], xt['n'], xg[0], xg[1], 0)
tk['L'] = assign(ticks_along('L', False, +1), yt['v0'], yt['dv'], yt['n'], yg[0], yg[1], 1)
tk['R'] = assign(ticks_along('R', False, -1), yt['v0'], yt['dv'], yt['n'], yg[0], yg[1], 1)
exx = set(cfg.get('exclude_x', []))
exy = set(cfg.get('exclude_y', []))


def lsq(rows):
    A = np.array([[1, r[0], r[1]] for r in rows])
    v = np.array([r[2] for r in rows])
    keep = np.ones(len(v), bool)
    for _ in range(4):
        coef, *_ = np.linalg.lstsq(A[keep], v[keep], rcond=None)
        res = v - A @ coef
        rms = np.sqrt(np.mean(res[keep] ** 2))
        new = np.abs(res) < max(4 * rms, 1e-9)
        if new.sum() == keep.sum():
            break
        keep = new
    return coef, res, keep


drop = cfg.get('drop_near', [])
def ok(t):
    return all((t[0]-d[0])**2 + (t[1]-d[1])**2 > d[2]**2 for d in drop)
xrows = [t for t in tk['B'] + tk['T'] if t[2] not in exx and ok(t)]
yrows = [t for t in tk['L'] + tk['R'] if t[2] not in exy and ok(t)]
cx, rx, kx = lsq(xrows)
cy, ry, ky = lsq(yrows)
out = {
    'page': cfg['page'],
    'edges': {k: {'m': v[0], 'c': v[1], 'halfwidth': v[2], 'npts': v[3]} for k, v in edges.items()},
    'x_coef': cx.tolist(), 'y_coef': cy.tolist(),
    'x_ticks': [dict(px=float(r[0]), py=float(r[1]), v=r[2], width=int(r[3]), res=float(e), used=bool(k)) for r, e, k in zip(xrows, rx, kx)],
    'y_ticks': [dict(px=float(r[0]), py=float(r[1]), v=r[2], width=int(r[3]), res=float(e), used=bool(k)) for r, e, k in zip(yrows, ry, ky)],
    'x_rms': float(np.sqrt(np.mean(rx[kx] ** 2))), 'x_max': float(np.max(np.abs(rx[kx]))), 'n_x': int(kx.sum()),
    'y_rms': float(np.sqrt(np.mean(ry[ky] ** 2))), 'y_max': float(np.max(np.abs(ry[ky]))), 'n_y': int(ky.sum()),
}
json.dump(out, open(sys.argv[2], 'w'), indent=1)
print('edges', {k: (round(v[0], 5), round(v[1], 1), v[2], v[3]) for k, v in edges.items()})
print('x ticks found B/T:', len(tk['B']), len(tk['T']), ' y ticks L/R:', len(tk['L']), len(tk['R']))
print('x: rms %.4f max %.4f n=%d  coef %s' % (out['x_rms'], out['x_max'], out['n_x'], cx))
print('y: rms %.4f max %.4f n=%d  coef %s' % (out['y_rms'], out['y_max'], out['n_y'], cy))
for r, e, k in zip(xrows, rx, kx):
    if not k:
        print('  x outlier', r, round(float(e), 4))
for r, e, k in zip(yrows, ry, ky):
    if not k:
        print('  y outlier', r, round(float(e), 4))
print('y ticks:', [(round(t['py'], 1), t['v'], round(t['res'], 3)) for t in out['y_ticks']])

