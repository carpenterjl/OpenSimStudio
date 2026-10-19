"""Reproducible independent checkpoints (MY calibration, MY reading rule per point) vs the other agent's CSVs.
Rules: 'upper'/'lower' = centre of the upper/lower of two separate ink runs on the constant-f line;
'bot+h' = bottom ink edge + half single-line thickness (0.212 dB; line 12 px = 0.425 dB);
'top-h' = top ink edge - half thickness; 'tip' = deepest ink edge + half thickness (null tip);
'hleft:v' = constant-value probe at v, left ink edge + half horizontal line width (0.053 GHz) -> frequency.
'hcen:v' = centre of the (single) run on the constant-value line, index k (0=leftmost)."""
import json, csv, sys
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
H = 0.212


class Cal:
    def __init__(s, path):
        c = json.load(open(path)); s.a = np.array(c['x_coef']); s.b = np.array(c['y_coef'])
        M = np.array([[s.a[1], s.a[2]], [s.b[1], s.b[2]]]); s.Minv = np.linalg.inv(M)
        s.im = np.asarray(Image.open(c['page']).convert('L'), dtype=np.float32); s.yrms = c['y_rms']; s.xrms = c['x_rms']

    def px(s, f, v):
        return s.Minv @ np.array([f - s.a[0], v - s.b[0]])

    def d(s, x, y):
        return s.a[0] + s.a[1] * x + s.a[2] * y, s.b[0] + s.b[1] * x + s.b[2] * y

    def vruns(s, f, vmin, vmax):
        p0 = s.px(f, vmax); p1 = s.px(f, vmin); n = int(abs(p1[1] - p0[1])) + 1
        ys = np.linspace(p0[1], p1[1], n); xs = np.linspace(p0[0], p1[0], n)
        ink = np.array([s.im[int(round(y)), int(round(x)) - 1:int(round(x)) + 2].mean() for x, y in zip(xs, ys)]) < 128
        dd = np.diff(np.concatenate([[0], ink.astype(int), [0]])); st = np.where(dd == 1)[0]; en = np.where(dd == -1)[0]
        return [(s.d(xs[i], ys[i])[1], s.d(xs[j - 1], ys[j - 1])[1]) for i, j in zip(st, en)]

    def hruns(s, v, f0, f1):
        p0 = s.px(f0, v); p1 = s.px(f1, v); n = int(abs(p1[0] - p0[0])) + 1
        xs = np.linspace(p0[0], p1[0], n); ys = np.linspace(p0[1], p1[1], n)
        ink = np.array([s.im[int(round(y)) - 1:int(round(y)) + 2, int(round(x))].mean() for x, y in zip(xs, ys)]) < 128
        dd = np.diff(np.concatenate([[0], ink.astype(int), [0]])); st = np.where(dd == 1)[0]; en = np.where(dd == -1)[0]
        return [(s.d(xs[i], ys[i])[0], s.d(xs[j - 1], ys[j - 1])[0]) for i, j in zip(st, en)]


def theirs(path, qty):
    cur = {}
    for r in csv.DictReader(open(path)):
        if r['quantity'] == qty:
            cur.setdefault(r['curve'], []).append((float(r['freq_GHz']), float(r['value'])))
    return {k: np.array(sorted(v)) for k, v in cur.items()}


def interp_v(arr, f):
    return float(np.interp(f, arr[:, 0], arr[:, 1])) if arr[0, 0] <= f <= arr[-1, 0] else float('nan')


def cross_f(arr, v, f0, f1):
    """first frequency in [f0,f1] where their (frequency-sorted) curve crosses v"""
    m = (arr[:, 0] >= f0) & (arr[:, 0] <= f1); A = arr[m]
    for i in range(len(A) - 1):
        if (A[i, 1] - v) * (A[i + 1, 1] - v) <= 0 and A[i, 1] != A[i + 1, 1]:
            return float(A[i, 0] + (v - A[i, 1]) * (A[i + 1, 0] - A[i, 0]) / (A[i + 1, 1] - A[i, 1]))
    return float('nan')


base = sys.argv[1]
cals = {k: Cal('%s/work/cal%s.json' % (base, k)) for k in ('333', '334')}
other = sys.argv[2]
T = {'333': theirs(other + '/fig3_33_S11_dB.csv', 'S11_dB'), '334': theirs(other + '/fig3_34_S21_dB.csv', 'S21_dB')}
spec = [
    # fig, curve, f, rule, vmin, vmax
    ('333', 'measured', 2.0, 'upper', -12, -5), ('333', 'measured', 2.1, 'upper', -12, -5), ('333', 'measured', 2.2, 'upper', -12, -5),
    ('333', 'measured', 3.8, 'lower', -12, -5), ('333', 'measured', 8.6, 'bot+h', -3, 1), ('333', 'measured', 9.75, 'lower', -6, 0),
    ('333', 'measured', 9.8, 'lower', -6, 0), ('333', 'measured', 10.84, 'tip', -45, -10), ('333', 'measured', 12.3, 'lower', -12, -4),
    ('333', 'measured', 12.5, 'lower', -12, -4), ('333', 'measured', 13.0, 'lower', -12, -4), ('333', 'measured', 13.6, 'lower', -12, -4),
    ('333', 'measured', 13.8, 'lower', -12, -4), ('333', 'measured', 15.5, 'upper', -22, -12), ('333', 'measured', 15.6, 'upper', -22, -12),
    ('333', 'measured', 15.85, 'upper', -22, -12), ('333', 'measured', 16.5, 'upper', -14, -6), ('333', 'measured', 16.6, 'upper', -14, -6),
    ('333', 'measured', 17.3, 'upper', -9, -3), ('333', 'measured', 17.4, 'upper', -9, -3), ('333', 'measured', 19.45, 'bot+h', -6, -2.5),
    ('333', 'measured', 19.9, 'bot+h', -6, -2.5), ('333', 'measured', 19.95, 'bot+h', -6, -2.5),
    ('333', 'fdtd', 2.1, 'lower', -12, -5), ('333', 'fdtd', 3.8, 'upper', -12, -5), ('333', 'fdtd', 5.0, 'tip', -35, -15),
    ('333', 'fdtd', 9.75, 'upper', -6, 0), ('333', 'fdtd', 10.98, 'tip0', -21, -15), ('333', 'fdtd', 12.5, 'upper', -12, -4),
    ('333', 'fdtd', 15.7, 'tip0', -21, -17), ('333', 'fdtd', 16.6, 'lower', -14, -6), ('333', 'fdtd', 0.13, 'tip', -36, -31.2),
    ('334', 'measured', 2.0, 'lower', -2.6, 1), ('334', 'measured', 2.1, 'lower', -2.6, 1), ('334', 'measured', 2.3, 'lower', -2.6, 1),
    ('334', 'measured', 2.5, 'lower', -2.6, 1), ('334', 'measured', 3.0, 'lower', -2.6, 1), ('334', 'measured', 3.5, 'bot+h', -2.6, 1),
    ('334', 'measured', 3.8, 'bot+h', -2.6, 1), ('334', 'measured', 4.3, 'bot+h', -2.6, 1), ('334', 'measured', 4.8, 'bot+h', -2.6, 1),
    ('334', 'measured', 4.9, 'bot+h', -2.6, 1), ('334', 'measured', 5.0, 'bot+h', -2.6, 1), ('334', 'measured', 5.1, 'bot+h', -2.6, 1),
    ('334', 'measured', 6.94, 'tip', -45, -30), ('334', 'measured', 7.53, 'upper', -45, -30), ('334', 'measured', 7.93, 'upper', -40, -33),
    ('334', 'measured', 11.8, 'lower', -4, 0), ('334', 'measured', 12.0, 'lower', -4, 0), ('334', 'measured', 13.3, 'bot+h', -4, 0),
    ('334', 'measured', 14.1, 'lower', -4, 0), ('334', 'measured', 14.6, 'lower', -4, 0), ('334', 'measured', 14.9, 'lower', -4, 0),
    ('334', 'measured', 15.5, 'lower', -4, 0), ('334', 'measured', 16.0, 'lower', -5, 0), ('334', 'measured', 16.4, 'lower', -5, 0),
    ('334', 'measured', 19.55, 'lower', -35, -20),
    ('334', 'fdtd', 2.1, 'upper', -2.6, 1), ('334', 'fdtd', 3.0, 'upper', -2.6, 1), ('334', 'fdtd', 4.8, 'top-h', -2.6, 1),
    ('334', 'fdtd', 11.2, 'single', -4, 0), ('334', 'fdtd', 14.3, 'upper', -4, 0), ('334', 'fdtd', 16.0, 'upper', -5, 0),
    ('334', 'fdtd', 19.55, 'upper', -35, -2),
]
hspec = [  # fig, curve, value, rule, f0, f1  -> frequency at which curve crosses value
    ('334', 'measured', -1.0, 'hleft', 5.0, 5.6), ('334', 'measured', -10.0, 'hcen0', 5.8, 6.3), ('334', 'measured', -20.0, 'hcen0', 6.2, 6.6),
    ('334', 'fdtd', -1.0, 'hright', 5.0, 5.6), ('334', 'fdtd', -10.0, 'hcen1', 5.8, 6.3), ('334', 'fdtd', -20.0, 'hcen1', 6.2, 6.9),
    ('334', 'fdtd', -40.0, 'hcen0', 7.5, 8.0), ('334', 'fdtd', -40.0, 'hcen0', 8.1, 8.5), ('334', 'measured', -20.0, 'hcen0', 8.7, 9.1),
]
rows = []
for fig, cur, f, rule, vmin, vmax in spec:
    c = cals[fig]; rr = c.vruns(f, vmin, vmax)
    rr = [r for r in rr if abs(r[0] - r[1]) > 0.05]
    if rule == 'upper':
        r = max(rr, key=lambda r: r[0]); v = (r[0] + r[1]) / 2
    elif rule == 'lower':
        r = min(rr, key=lambda r: r[1]); v = (r[0] + r[1]) / 2
    elif rule == 'single':
        r = rr[0]; v = (r[0] + r[1]) / 2
    elif rule == 'bot+h':
        v = min(r[1] for r in rr) + H
    elif rule == 'top-h':
        v = max(r[0] for r in rr) - H
    elif rule == 'tip':
        v = min(r[1] for r in rr) + H
    elif rule == 'tip0':  # FD-TD tip = bottom of the uppermost run (measured dashes may lie below)
        r = max(rr, key=lambda r: r[0]); v = r[1] + H
    th = interp_v(T[fig][cur], f)
    kind = 'value_at_freq'
    if rule in ('tip', 'tip0'):
        A = T[fig][cur]; m = np.abs(A[:, 0] - f) <= 0.06
        th = float(A[m, 1].min()) if m.any() else float('nan'); kind = 'null_depth_vs_their_min_within_0.06GHz'
    rows.append(dict(figure='Fig. 3.%s' % fig[1:], page=int({'333': 115, '334': 116}[fig]), curve=cur, kind=kind, freq_GHz=f,
                     mine=round(v, 3), theirs=round(th, 3), diff=round(v - th, 3), rule=rule))
for fig, cur, v, rule, f0, f1 in hspec:
    c = cals[fig]; rr = c.hruns(v, f0, f1)
    if rule == 'hleft':
        fm = min(r[0] for r in rr) + 0.053
    elif rule == 'hright':
        fm = max(r[1] for r in rr) - 0.053
    else:
        k = int(rule[-1]); rr = sorted(rr); r = rr[min(k, len(rr) - 1)]; fm = (r[0] + r[1]) / 2
    th = cross_f(T[fig][cur], v, f0, f1)
    rows.append(dict(figure='Fig. 3.%s' % fig[1:], page=int({'333': 115, '334': 116}[fig]), curve=cur, kind='freq_at_value', freq_GHz=round(fm, 4),
                     mine=v, theirs=round(th, 4), diff=round(fm - th, 4), rule=rule))
with open(sys.argv[3], 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)
for r in rows:
    print(r)

